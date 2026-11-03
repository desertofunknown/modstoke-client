// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Runtime;
using Modstoke.Sons;
using Sons.Save;
using Sons.StatSystem;
using TheForest.Utils;
using TheForest.World;
using UnityEngine;

namespace Modstoke.Client;

internal sealed class ClientSpacecraftCabin : IDisposable
{
    private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("Modstoke spacecraft cabin");
    private static ClientSpacecraftCabin? _owner;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly CabinWeather _weather = new();
    private readonly Harmony _patches = new("modstoke.client.spacecraft.cabin." + Guid.NewGuid().ToString("N"));
    private MethodInfo? _statsUpdate, _vitalsUpdate;
    private ClientSpacecraftBody? _body;
    private ClientSpacecraftRider? _rider;
    private BoxStationModel? _station;
    private BoxStationState? _stationState;
    private SpacecraftEvaSuitState? _suit;
    private Transform? _suitPlayer;
    private bool _suitWaiting;
    private int _load;
    private int _exposureFrame = -1;
    private int _exposureLoad = -1;
    private float _exposureSeconds, _damageSeconds;
    private PlayerStats? _exposedStats;
    private Transform? _exposedPlayer;
    private readonly ThermalScope[] _thermalScopes = new ThermalScope[8];
    private int _thermalCount, _retryDelay;
    private long _thermalToken, _retryAt, _nextFaultLog;
    private bool _started, _installFailed, _runtimeFault, _disposed;

    internal bool Protected { get; private set; }
    internal string Status { get; private set; } = "Cabin climate is unavailable.";
    private bool OnThread => Environment.CurrentManagedThreadId == _thread;
    private bool HooksReady => OnThread && _started && !_installFailed && !_disposed && ReferenceEquals(_owner, this);
    internal bool Healthy => HooksReady && !_runtimeFault;
    private bool Ready => Healthy;

    internal bool Start()
    {
        if (!OnThread || _disposed || _installFailed) return false;
        if (_started) return Ready;
        if (_owner is { _disposed: true } previous) previous.Dispose();
        if (_owner != null && !ReferenceEquals(_owner, this))
        { Status = "Waiting for cabin climate hooks to be released."; return false; }
        try
        {
            _statsUpdate = UpdateMethod(typeof(PlayerStats));
            _vitalsUpdate = UpdateMethod(typeof(Vitals));
            _patches.Patch(_statsUpdate, prefix: Hook(nameof(BeforeStatsUpdate)), finalizer: Hook(nameof(FinishUpdate)));
            _patches.Patch(_vitalsUpdate, prefix: Hook(nameof(BeforeVitalsUpdate)), finalizer: Hook(nameof(FinishUpdate)));
            _owner = this;
            _started = true;
            Status = "Outside the cabin.";
            return true;
        }
        catch (Exception error)
        {
            _installFailed = true;
            Status = "Cabin climate hooks are unavailable.";
            Log.LogError("Spacecraft cabin hook installation failed: " + error);
            RemoveHooks();
            return false;
        }
    }

    private static MethodInfo UpdateMethod(Type type)
    {
        var method = AccessTools.DeclaredMethod(type, "Update", Type.EmptyTypes)
            ?? throw new MissingMethodException(type.FullName, "Update");
        if (method.IsStatic || method.ReturnType != typeof(void))
            throw new InvalidOperationException("The native survival update signature is not supported: " + type.Name);
        return method;
    }

    private static HarmonyMethod Hook(string name) => new(typeof(ClientSpacecraftCabin), name)
        { priority = Priority.Last };

    internal bool Recover()
    {
        if (!OnThread || _disposed || _installFailed) return false;
        long now = Environment.TickCount64;
        if (!_started)
        {
            if (now < _retryAt) return false;
            _retryAt = now + 250;
            return Start();
        }
        if (!HooksReady) return false;
        if (!_runtimeFault) return true;
        if (now < _retryAt) return false;
        _retryAt = now + 250;
        try
        {
            DrainThermalScopes();
            if (_thermalCount != 0) return false;
            _load = SaveLifecycle.State.BeforeLoadCount;
            if (!TryReadPlayer(out _, out _, out _, out var player)) return false;
            TrySampleAtmosphere(player, out _, out _, out _);
            _runtimeFault = false;
            _retryDelay = 0;
            _retryAt = 0;
            Status = "Cabin climate restored.";
            return true;
        }
        catch (Exception error) { Fail(error); return false; }
    }

    internal void Update(ClientSpacecraftBody? body, SpacecraftRegion localRegion = SpacecraftRegion.Island,
        BoxStationModel? station = null, BoxStationState? stationState = null,
        SpacecraftEvaSuitState? suit = null, bool suitWaiting = false, ClientSpacecraftRider? rider = null)
    {
        if (!OnThread || _disposed) return;
        // Physical roof coverage is independent of cabin pressure and heating.
        _weather.Update(body);
        _body = body;
        _rider = rider;
        _station = station;
        _stationState = stationState;
        _suit = suit;
        _suitPlayer = null;
        _suitWaiting = suitWaiting;
        _load = SaveLifecycle.State.BeforeLoadCount;
        Protected = false;
        if (!Ready) return;
        try
        {
            if (!TryReadPlayer(out var stats, out _, out _, out var player))
            {
                if (!CanRetainExposure()) ResetExposure();
                Status = "Waiting for the local player.";
                return;
            }
            if (_suit.HasValue || _suitWaiting) _suitPlayer = player;
            bool inside = TrySampleAtmosphere(player, out var air, out var compartment, out var waiting);
            if (waiting)
            {
                Status = compartment switch
                {
                    "EVA suit" => "Waiting for EVA suit telemetry.",
                    "Station" => "Waiting for station life-support telemetry.",
                    _ => "Waiting for life-support telemetry."
                };
                return;
            }
            if (!inside && ClientSpacecraftSystems.Breathable(localRegion))
            { ResetExposure(); Status = "Outside the cabin."; return; }
            Protected = inside && air.Pressure >= 55 && air.TemperatureC >= 12;
            Status = compartment == "EVA suit" && _suit is { } currentSuit
                ? air.Pressure < 35 ? "EVA suit pressure low · return to the airlock."
                    : air.Pressure * air.OxygenFraction < 16 ? "EVA suit oxygen low · return for breathing gas."
                    : air.CO2Fraction > .01f ? "EVA suit CO2 high · restore battery and scrubber capacity."
                    : air.TemperatureC < 12 ? "EVA suit cold · return for heat and battery service."
                    : air.TemperatureC > 60 ? "EVA suit overheating · leave the hot compartment."
                    : !currentSuit.Powered ? "EVA suit battery depleted · scrubber and heating stopped."
                    : currentSuit.FilterMoles <= 0 ? "EVA suit filter exhausted · return for scrubber service."
                    : currentSuit.OxygenMoles <= 0 ? "EVA suit oxygen reserve depleted · return for breathing gas."
                    : stats.IsCold || stats._IsFreezing_k__BackingField ? "EVA suit pressurized · warming."
                    : "EVA suit atmosphere breathable."
                : air.Pressure < 35 ? "Vacuum exposure · return to a pressurized compartment."
                : air.Pressure * air.OxygenFraction < 16 ? "Low oxygen · restore pressure and breathing-gas supply."
                : air.CO2Fraction > .01f ? "CO2 rising · restore scrubber power."
                : air.PlasmaFraction > .001f || air.N2OFraction > .05f
                    || air.TritiumFraction > .001f || air.BZFraction > .001f ? "Hazardous atmosphere · seal your EVA suit and leave the compartment."
                : air.Pressure > 250 ? "Extreme pressure · leave the compartment."
                : air.TemperatureC > 60 ? "Dangerous heat · leave the compartment."
                : air.TemperatureC < 12 ? compartment == "Station"
                    ? "Station cold · restore heating power." : "Cabin cold · restore heating power."
                : stats.IsCold || stats._IsFreezing_k__BackingField ? compartment == "Station"
                    ? "Station pressurized · warming." : "Cabin pressurized · warming."
                : compartment switch
                {
                    "Station" => "Station atmosphere breathable.",
                    "Cargo vestibule" => "Cargo vestibule atmosphere breathable.",
                    "Airlock" => "Airlock atmosphere breathable.",
                    _ => "Cabin atmosphere breathable."
                };
            if (SpacecraftLife.Read(stats) == PlayerLifeState.Alive) AdvanceExposure(stats, player, air);
            else ResetExposure();
        }
        catch (Exception error) { Fail(error); }
    }

    private bool TryReadPlayer([NotNullWhen(true)] out PlayerStats? stats,
        [NotNullWhen(true)] out Vitals? vitals, [NotNullWhen(true)] out TemperatureStat? temperature,
        [NotNullWhen(true)] out Transform? player)
    {
        stats = null; vitals = null; temperature = null; player = null;
        if (!HooksReady || _load != SaveLifecycle.State.BeforeLoadCount
            || !GameRuntime.State.IsInWorld || !LocalPlayer.IsInWorld || GameRuntime.IsTitleScene
            || !SaveGameManager.TryGetInstance(out var manager) || !Live(manager) || manager._loadingInProgress)
            return false;
        player = LocalPlayer.Transform;
        stats = LocalPlayer.Stats;
        vitals = LocalPlayer.Vitals;
        if (!Live(player) || !player.gameObject.activeInHierarchy || !Live(stats) || !Live(vitals)
            || !Same(stats._vitals, vitals)
            || SpacecraftLife.Read(stats) is not (PlayerLifeState.Alive or PlayerLifeState.Dying)) return false;
        temperature = vitals._temperature;
        return temperature is not null && !temperature.WasCollected;
    }

    private bool TryProtect([NotNullWhen(true)] out PlayerStats? stats,
        [NotNullWhen(true)] out Vitals? vitals, [NotNullWhen(true)] out TemperatureStat? temperature,
        [NotNullWhen(true)] out Transform? player, out float cabinTemperature)
    {
        cabinTemperature = 0;
        if (!TryReadPlayer(out stats, out vitals, out temperature, out player)) return false;
        if (!TrySampleAtmosphere(player, out var air, out _, out _)) return false;
        cabinTemperature = air.TemperatureC;
        return air.Pressure >= 55 && cabinTemperature >= 12;
    }

    private bool TrySampleAtmosphere(Transform player, out OrbitalStationAtmosphere air,
        out string compartment, out bool waiting)
    {
        air = default;
        compartment = "Cabin";
        waiting = false;
        // A sealed helmet cannot draw cabin or station air, even while indoors.
        if (_suitWaiting && Same(player, _suitPlayer))
        { compartment = "EVA suit"; waiting = true; return false; }
        if (_suit is { } suit && Same(player, _suitPlayer))
        {
            compartment = "EVA suit";
            air = new(suit.Pressure, suit.OxygenFraction, suit.CO2Fraction, suit.TemperatureC);
            return true;
        }
        var position = player.position;
        if (_body?.Ready == true)
        {
            if (_rider?.TryAttachedPosition(_body, player, out var attached) == true) position = attached;
            var shipChest = position + _body.Root.transform.up * .9f;
            if (_body.ContainsCabinAir(shipChest))
            {
                var systems = _body.State.Systems;
                if (!_body.TelemetryCurrent || !systems.Ready)
                { waiting = true; return false; }
                bool entry = _body.IsInEntryVestibule(shipChest);
                bool airlock = _body.IsInAirlock(shipChest);
                compartment = entry ? "Cargo vestibule" : airlock ? "Airlock" : "Cabin";
                air = new(
                    entry ? systems.EntryPressure : airlock ? systems.AirlockPressure : systems.CabinPressure,
                    entry ? systems.EntryOxygenFraction : airlock ? systems.AirlockOxygenFraction : systems.OxygenFraction,
                    entry ? systems.EntryCO2Fraction : airlock ? systems.AirlockCO2Fraction : systems.CO2Fraction,
                    entry ? systems.EntryTemperatureC : airlock ? systems.AirlockTemperatureC : systems.CabinTemperatureC);
                return true;
            }
        }
        // Station atmosphere is independent of ship readiness. An unavailable ship
        // still needs its geometry back before an unsuited crew member can be classified outside.
        waiting = _body is not null && !_body.Ready;
        if (_station?.Visible != true) return false;
        var chest = position + _station.Root.transform.up * .9f;
        int cell = _station.AtmosphereCellAt(chest);
        if (cell < 0) return false;
        compartment = "Station";
        if (_stationState is not { } state || state.HullRevision != _station.HullState.Revision
            || !BoxStationSystems.TryGetAtmosphere(_station.Layout, state, cell, out air))
        {
            // Missing telemetry grants neither warmth nor breathing-gas recovery.
            waiting = true;
            return false;
        }
        waiting = false;
        return true;
    }

    private void AdvanceExposure(PlayerStats stats, Transform player, OrbitalStationAtmosphere air)
    {
        if (_exposureFrame == Time.frameCount
            || !BoltNetwork.isRunning && (!Application.isFocused || GameRuntime.State.PauseMenuOpen)) return;
        _exposureFrame = Time.frameCount;
        var load = SaveLifecycle.State.BeforeLoadCount;
        if (_exposureLoad != load || !Same(_exposedStats, stats) || !Same(_exposedPlayer, player))
        {
            _exposureSeconds = _damageSeconds = 0;
            _exposedStats = stats; _exposedPlayer = player; _exposureLoad = load;
        }
        float dt = Time.deltaTime;
        if (!float.IsFinite(dt) || dt <= 0 || !float.IsFinite(air.Pressure)
            || !float.IsFinite(air.OxygenFraction) || !float.IsFinite(air.CO2Fraction)) return;
        dt = Mathf.Min(dt, .25f);
        float severity = Mathf.Max(Mathf.Clamp01((16 - air.Pressure * air.OxygenFraction) / 16),
            Mathf.Clamp01((air.CO2Fraction - .01f) / .04f));
        severity = Mathf.Max(severity, Mathf.Clamp01((35 - air.Pressure) / 35));
        // Gameplay exposure thresholds, not a chemical reaction or medical model.
        severity = Mathf.Max(severity, Mathf.Clamp01((air.PlasmaFraction - .001f) / .01f));
        severity = Mathf.Max(severity, Mathf.Clamp01((air.N2OFraction - .05f) / .15f));
        severity = Mathf.Max(severity, Mathf.Clamp01((air.TritiumFraction - .001f) / .01f));
        severity = Mathf.Max(severity, Mathf.Clamp01((air.BZFraction - .001f) / .01f));
        severity = Mathf.Max(severity, Mathf.Clamp01((air.Pressure - 250) / 250));
        severity = Mathf.Max(severity, Mathf.Clamp01((air.TemperatureC - 60) / 80));
        if (severity <= 0)
        {
            _exposureSeconds = Mathf.Max(0, _exposureSeconds - dt * 2);
            _damageSeconds = 0;
            return;
        }
        _exposureSeconds = Mathf.Min(60, _exposureSeconds + dt * severity);
        if (_exposureSeconds <= 5) { _damageSeconds = 0; return; }
        _damageSeconds += dt;
        if (_damageSeconds < 1) return;
        _damageSeconds -= 1;
        float damage = Mathf.Lerp(2, 10, severity) * Mathf.Clamp01((_exposureSeconds - 5) / 5);
        // The native hit path owns health, immunity and death transitions. Armour
        // cannot supply air, but native hit-detection and god-mode guards remain.
        stats.Hit(damage, ignoreArmour: true, weight: 0);
    }

    private bool CanRetainExposure()
    {
        if (_exposureLoad != SaveLifecycle.State.BeforeLoadCount || GameRuntime.IsTitleScene
            || !Live(_exposedPlayer) || !Live(_exposedStats)) return false;
        var player = LocalPlayer.Transform;
        var stats = LocalPlayer.Stats;
        // Missing references do not grant a new exposure grace period.
        return (!Live(player) || Same(_exposedPlayer, player))
            && (!Live(stats) || Same(_exposedStats, stats))
            && SpacecraftLife.Read(_exposedStats) is not (PlayerLifeState.Dying or PlayerLifeState.Dead);
    }

    private void ResetExposure()
    {
        _exposureSeconds = _damageSeconds = 0;
        _exposureFrame = -1;
        _exposureLoad = -1;
        _exposedStats = null;
        _exposedPlayer = null;
    }

    private static void BeforeStatsUpdate(PlayerStats __instance, bool __runOriginal, out ThermalCall __state)
    {
        __state = default;
        var owner = _owner;
        owner?.DrainDisposedScopes();
        if (!__runOriginal || owner == null || !owner.Ready) return;
        try
        {
            if (!owner.TryProtect(out var stats, out var vitals, out var temperature, out var player, out var cabinTemperature)
                || !Same(__instance, stats)) return;
            owner.ApplyThermal(stats, vitals, temperature, player, cabinTemperature, heat: true, out __state);
        }
        catch (Exception error)
        {
            owner.Fail(error);
            __state.Restore();
            __state = default;
        }
    }

    private static void BeforeVitalsUpdate(Vitals __instance, bool __runOriginal, out ThermalCall __state)
    {
        __state = default;
        var owner = _owner;
        owner?.DrainDisposedScopes();
        if (!__runOriginal || owner == null || !owner.Ready) return;
        try
        {
            if (!owner.TryProtect(out var stats, out var vitals, out var temperature, out var player, out var cabinTemperature)
                || !Same(__instance, vitals)) return;
            owner.ApplyThermal(stats, vitals, temperature, player, cabinTemperature, heat: false, out __state);
        }
        catch (Exception error)
        {
            owner.Fail(error);
            __state.Restore();
            __state = default;
        }
    }

    private static Exception? FinishUpdate(Exception? __exception, ThermalCall __state)
    {
        __state.Restore();
        return __exception;
    }

    private readonly struct ThermalCall(ClientSpacecraftCabin owner, long token)
    {
        internal void Restore() => owner?.EndThermal(token);
    }

    private void DrainDisposedScopes()
    {
        if (!OnThread || !_disposed || _thermalCount == 0 || Environment.TickCount64 < _retryAt) return;
        _retryAt = Environment.TickCount64 + 250;
        DrainThermalScopes();
    }

    private void ApplyThermal(PlayerStats stats, Vitals vitals, TemperatureStat temperature,
        Transform player, float cabinTemperature, bool heat, out ThermalCall call)
    {
        call = default;
        if (_thermalCount == _thermalScopes.Length)
        { Fail(new InvalidOperationException("Cabin thermal callback nesting exceeded its capacity.")); return; }
        int slot = _thermalCount;
        long token = checked(++_thermalToken);
        _thermalScopes[slot] = new(stats, vitals, temperature, player, cabinTemperature, heat, _load, token);
        _thermalCount++;
        call = new(this, token);
        _thermalScopes[slot].Apply();
    }

    private void EndThermal(long token)
    {
        if (!OnThread) return;
        for (int i = _thermalCount - 1; i >= 0; i--)
            if (_thermalScopes[i].Token == token)
            {
                _thermalScopes[i].Ended = true;
                DrainThermalScopes();
                return;
            }
    }

    private void DrainThermalScopes()
    {
        if (!OnThread) return;
        try
        {
            while (_thermalCount > 0)
            {
                ref var scope = ref _thermalScopes[_thermalCount - 1];
                if (!scope.Ended) return;
                if (!scope.TryRestore())
                {
                    _runtimeFault = true;
                    Protected = false;
                    Status = "Waiting to restore cabin temperature state.";
                    _retryAt = Math.Max(_retryAt, Environment.TickCount64 + 250);
                    return;
                }
                scope = default;
                _thermalCount--;
            }
        }
        catch (Exception error) { Fail(error); }
    }

    // Native PlayerStats.Update consumes heat and shelter, then updates wetness.
    // Vitals.Update independently samples indoor temperature. Keep both overrides
    // inside their native calls so construction and temperature-volume callbacks
    // retain their own state between frames, including after leaving the cabin.
    private struct ThermalScope
    {
        private readonly PlayerStats _stats;
        private readonly Vitals _vitals;
        private readonly TemperatureStat _temperature;
        private readonly Transform _player;
        private readonly bool _warmth, _inside, _heat;
        private readonly float _volumeTemperature, _blend, _cabinTemperature;
        private readonly int _load;
        internal readonly long Token;
        internal bool Ended;
        private byte _writes;

        internal ThermalScope(PlayerStats stats, Vitals vitals,
            TemperatureStat temperature, Transform player, float cabinTemperature, bool heat, int load, long token)
        {
            _stats = stats; _vitals = vitals; _temperature = temperature; _player = player;
            _warmth = temperature._interiorSpaceWarmth;
            _inside = stats._insideTemperatureModifierVolume;
            _volumeTemperature = stats._temperatureModifierVolumeTemperature;
            _blend = stats._currentModifiedTemperatureBlendDelta;
            _heat = heat;
            _cabinTemperature = cabinTemperature;
            _load = load;
            Token = token;
            Ended = false;
            _writes = 0;
        }

        internal void Apply()
        {
            // A native setter may fail after writing; retain responsibility before calling it.
            _writes |= 1;
            _temperature._interiorSpaceWarmth = true;
            if (!_heat) return;
            _writes |= 2;
            _stats._insideTemperatureModifierVolume = true;
            _writes |= 4;
            _stats._temperatureModifierVolumeTemperature = _cabinTemperature;
            _writes |= 8;
            _stats._currentModifiedTemperatureBlendDelta = 1;
        }

        internal bool TryRestore()
        {
            if (_writes == 0) return true;
            if (_load != SaveLifecycle.State.BeforeLoadCount || !Live(_player) || !Live(_stats)
                || !Live(_vitals) || _temperature.WasCollected)
            { _writes = 0; return true; }
            var player = LocalPlayer.Transform;
            var stats = LocalPlayer.Stats;
            var vitals = LocalPlayer.Vitals;
            if (Live(player) && !Same(_player, player) || Live(stats) && !Same(_stats, stats)
                || Live(vitals) && !Same(_vitals, vitals))
            { _writes = 0; return true; }
            if (!Live(player) || !Live(stats) || !Live(vitals)) return false;
            var statsVitals = _stats._vitals;
            var temperature = _vitals._temperature;
            if (Live(statsVitals) && !Same(statsVitals, _vitals)
                || temperature is { WasCollected: false } && temperature.Pointer != _temperature.Pointer)
            { _writes = 0; return true; }
            if (!Live(statsVitals) || temperature == null || temperature.WasCollected) return false;
            if ((_writes & 1) != 0)
            {
                if (_temperature._interiorSpaceWarmth) _temperature._interiorSpaceWarmth = _warmth;
                _writes &= 0xfe;
            }
            // Inside-volume ownership depends on the applied temperature, so restore it first.
            if ((_writes & 2) != 0)
            {
                if (((_writes & 4) == 0 || _stats._temperatureModifierVolumeTemperature == _cabinTemperature)
                    && _stats._insideTemperatureModifierVolume) _stats._insideTemperatureModifierVolume = _inside;
                _writes &= 0xfd;
            }
            if ((_writes & 4) != 0)
            {
                if (_stats._temperatureModifierVolumeTemperature == _cabinTemperature)
                    _stats._temperatureModifierVolumeTemperature = _volumeTemperature;
                _writes &= 0xfb;
            }
            if ((_writes & 8) != 0)
            {
                if (_stats._currentModifiedTemperatureBlendDelta == 1) _stats._currentModifiedTemperatureBlendDelta = _blend;
                _writes &= 0xf7;
            }
            return true;
        }
    }

    internal void Reset()
    {
        if (!OnThread) return;
        _weather.Reset();
        _body = null;
        _rider = null;
        _station = null;
        _stationState = null;
        _suit = null;
        _suitPlayer = null;
        _suitWaiting = false;
        Protected = false;
        ResetExposure();
        DrainThermalScopes();
        if (!_installFailed && !_runtimeFault) Status = "Outside the cabin.";
    }

    private void Fail(Exception error)
    {
        Protected = false;
        _runtimeFault = true;
        Status = "Cabin climate recovery is pending.";
        long now = Environment.TickCount64;
        _retryDelay = _retryDelay == 0 ? 250 : Math.Min(_retryDelay * 2, 4000);
        _retryAt = now + _retryDelay;
        if (now < _nextFaultLog) return;
        _nextFaultLog = now + 30000;
        Log.LogError("Spacecraft cabin climate recovery: " + error);
    }

    private void RemoveHooks()
    {
        _started = false;
        if (ReferenceEquals(_owner, this)) _owner = null;
        RemoveHook(_statsUpdate);
        RemoveHook(_vitalsUpdate);
        _statsUpdate = _vitalsUpdate = null;
    }

    private void RemoveHook(MethodInfo? method)
    {
        if (method == null) return;
        try { _patches.Unpatch(method, HarmonyPatchType.All, _patches.Id); }
        catch (Exception error) { Log.LogError("Spacecraft cabin hook cleanup failed: " + error); }
    }

    public void Dispose()
    {
        if (!OnThread) return;
        if (!_disposed)
        {
            Reset();
            _weather.Reset(force: true);
            _disposed = true;
        }
        else DrainThermalScopes();
        // A failed native restore retains its captures through the existing
        // callbacks. Once drained they are inert until a later Dispose/Start
        // can unpatch outside the executing IL2CPP callback.
        if (_thermalCount == 0) RemoveHooks();
    }

    private static bool Live([NotNullWhen(true)] UnityEngine.Object? value)
        => value is not null && !value.WasCollected && value;

    private static bool Same(UnityEngine.Object? left, UnityEngine.Object? right)
        => Live(left) && Live(right) && left.Pointer == right.Pointer;

    private sealed class CabinWeather
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate byte EnabledGetter(ref nint particleSystem);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int IntGetter(ref nint particleSystem);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate nint ColliderGetter(ref nint particleSystem, int index);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void ColliderAction(ref nint particleSystem, nint collider);

        private const int VolumeCount = 12;
        private static readonly Lazy<TriggerBindings> TriggerAccessors = new(ResolveTriggers);
        private readonly BoxCollider?[] _volumes = new BoxCollider?[VolumeCount];
        private readonly List<WeatherBinding> _bindings = new();
        private ClientSpacecraftBody? _body;
        private GameObject? _root, _follow;
        private RainTypes? _types;
        private int _load = -1;
        private float _nextBind, _nextScan, _nextCleanup;
        private bool _failed, _releasing, _warned, _cleanupWarned;

        internal void Update(ClientSpacecraftBody? body)
        {
            try
            {
                int load = SaveLifecycle.State.BeforeLoadCount;
                if (_load != load || !ReferenceEquals(_body, body) || body?.Ready != true
                    || !GameRuntime.State.IsInWorld || !LocalPlayer.IsInWorld || GameRuntime.IsTitleScene)
                    Reset();
                if (_releasing) { Reset(); return; }
                if (_failed || body?.Ready != true || !GameRuntime.State.IsInWorld
                    || !LocalPlayer.IsInWorld || GameRuntime.IsTitleScene) return;
                float now = Time.realtimeSinceStartup;
                if (now < _nextBind) return;
                _nextBind = now + 2;
                if (!SaveGameManager.TryGetInstance(out var manager) || !Live(manager) || manager._loadingInProgress)
                { Reset(); return; }

                var types = WeatherSystem.GetRainTypes();
                // GetRainFollowGameObject dereferences RainTypes when its cache is empty.
                if (!Live(types)) { Reset(); return; }
                var follow = WeatherSystem.GetRainFollowGameObject();
                if (!Live(follow) || !Same(types.gameObject, follow))
                { Warn("The native precipitation graph does not match its follow root."); Reset(); return; }
                if (!Same(_types, types) || !Same(_follow, follow))
                {
                    Reset();
                    if (_releasing) return;
                    _types = types;
                    _follow = follow;
                    _nextScan = 0;
                }
                _load = load;
                _body = body;
                if (!Live(_root)) BuildVolumes(body.Root.transform);
                if (now >= _nextScan)
                {
                    _nextScan = now + 10;
                    foreach (var binding in _bindings) binding.Seen = false;
                    Scan(types.RainHeavy, rain: true);
                    Scan(types.RainMedium, rain: true);
                    Scan(types.RainLight, rain: true);
                    Scan(types.SnowHeavy, rain: false);
                    Scan(types.SnowMedium, rain: false);
                    Scan(types.SnowLight, rain: false);
                    Scan(types.SnowConstant, rain: false);
                    for (int i = _bindings.Count - 1; i >= 0; i--)
                    {
                        if (_bindings[i].Seen) continue;
                        Remove(_bindings[i]);
                        _bindings.RemoveAt(i);
                    }
                    if (_bindings.Count == 0) Warn("No native precipitation particle systems were found.");
                }
                foreach (var binding in _bindings) Reconcile(binding);
                if (_root != null && !_root.activeSelf) _root.SetActive(true);
            }
            catch (Exception error)
            {
                if (!_failed) Log.LogWarning("Spacecraft cabin spatial precipitation binding stopped: " + error);
                _failed = true;
                Reset(force: true);
            }
        }

        private void BuildVolumes(Transform ship)
        {
            var scale = ship.lossyScale;
            if (!float.IsFinite(scale.x) || !float.IsFinite(scale.y) || !float.IsFinite(scale.z)
                || scale.x <= 0 || scale.y <= 0 || scale.z <= 0)
                throw new InvalidOperationException("The cabin weather root requires positive ship scale.");
            _root = new GameObject("Cabin precipitation volumes") { layer = 2 };
            _root.SetActive(false);
            _root.transform.SetParent(ship, false);
            // Inset from Body's floor, roof and wall faces. The sloping aft entry
            // and external ramp are deliberately outside these fixed roof volumes.
            AddVolume(0, new(-.96f, -1.24f, -8.85f), new(.82f, 1.10f, -4.60f));
            AddVolume(1, new(-3.40f, -1.24f, -4.60f), new(3.19f, 1.25f, 1.65f));
            AddVolume(2, new(-2.72f, 1.25f, -4.60f), new(2.59f, 2.08f, 1.65f));
            AddVolume(3, new(-2.40f, -1.24f, 1.65f), new(2.38f, 2.08f, 4.02f));
            // Each cockpit slice fits its forward (lowest, narrowest) section.
            // No box extends above the visor or sideways past the tapered walls.
            for (int i = 0; i < 8; i++)
            {
                float aft = Mathf.Lerp(4.02f, 9.30f, i / 8f);
                float forward = Mathf.Lerp(4.02f, 9.30f, (i + 1) / 8f);
                float taper = (forward - 4.06f) / 5.44f;
                float ceiling = Mathf.Lerp(2.12f, -.76f, (forward - 4.02f) / 5.48f) - .04f;
                AddVolume(i + 4, new(Mathf.Lerp(-2.49f, -.54f, taper) + .06f, -1.24f, aft),
                    new(Mathf.Lerp(2.47f, .54f, taper) - .06f, ceiling, forward));
            }
        }

        private void AddVolume(int index, Vector3 min, Vector3 max)
        {
            var box = _root!.AddComponent<BoxCollider>();
            _volumes[index] = box;
            box.isTrigger = true;
            box.center = (min + max) * .5f;
            box.size = max - min;
        }

        private void Scan(Component? branch, bool rain)
        {
            if (Live(branch)) Scan(branch.gameObject, rain);
            else if (rain) Warn("A native rain effect branch is unavailable.");
        }

        private void Scan(GameObject? branch, bool rain)
        {
            if (!Live(branch) || !Live(_follow) || !branch.transform.IsChildOf(_follow.transform))
            {
                if (rain) Warn("A native rain effect branch is outside the precipitation follow graph.");
                return;
            }
            var particles = branch.GetComponentsInChildren<ParticleSystem>(true);
            if (rain && particles.Length == 0) Warn("A native rain effect branch has no particle systems.");
            foreach (var system in particles)
            {
                if (!Live(system)) continue;
                WeatherBinding? found = null;
                foreach (var binding in _bindings)
                {
                    if (!Same(binding.System, system)) continue;
                    found = binding;
                    break;
                }
                if (found == null)
                {
                    found = new WeatherBinding(system, rain);
                    _bindings.Add(found);
                }
                found.Seen = true;
            }
        }

        private void Reconcile(WeatherBinding binding)
        {
            if (!Live(binding.System)) { _nextScan = 0; return; }
            try
            {
                var accessors = TriggerAccessors.Value;
                nint pointer = IL2CPP.Il2CppObjectBaseToPtrNotNull(binding.System);
                bool enabled = accessors.Enabled(ref pointer) != 0;
                bool compatible = enabled
                    && accessors.Inside(ref pointer) == (int)ParticleSystemOverlapAction.Kill
                    && accessors.Outside(ref pointer) == (int)ParticleSystemOverlapAction.Ignore
                    && accessors.Enter(ref pointer) == (int)ParticleSystemOverlapAction.Ignore
                    && accessors.Exit(ref pointer) == (int)ParticleSystemOverlapAction.Ignore;
                ValidateReceiver(binding.System, pointer);
                if (!compatible)
                {
                    // SnowHeavy's disabled trigger and any foreign entries stay unchanged.
                    Remove(binding);
                    if (binding.Rain || enabled) Warn("A precipitation trigger policy is incompatible; it was left unchanged.");
                    return;
                }
                int present = OwnedMask(binding, ref pointer);
                if (present == (1 << VolumeCount) - 1) return;
                for (int i = 0; i < _volumes.Length; i++)
                {
                    if (!Live(_volumes[i])) throw new InvalidOperationException("A cabin precipitation volume was lost.");
                    if ((present & (1 << i)) == 0)
                        accessors.AddCollider(ref pointer, IL2CPP.Il2CppObjectBaseToPtrNotNull(_volumes[i]!));
                }
                ValidateReceiver(binding.System, pointer);
                if (OwnedMask(binding, ref pointer) != (1 << VolumeCount) - 1)
                    throw new InvalidOperationException("The precipitation trigger did not retain the cabin volumes.");
            }
            finally
            {
                GC.KeepAlive(binding.System);
                GC.KeepAlive(_volumes);
            }
        }

        private int OwnedMask(WeatherBinding binding, ref nint pointer)
        {
            var accessors = TriggerAccessors.Value;
            int count = accessors.ColliderCount(ref pointer);
            ValidateReceiver(binding.System, pointer);
            if (count < 0 || count > 256)
                throw new InvalidOperationException("The native precipitation collider list exceeds the supported bound.");
            int mask = 0;
            for (int i = 0; i < count; i++)
            {
                nint colliderPointer = accessors.GetCollider(ref pointer, i);
                var collider = colliderPointer == 0 ? null : Il2CppObjectPool.Get<Component>(colliderPointer);
                if (!Live(collider)) continue;
                for (int j = 0; j < _volumes.Length; j++)
                {
                    var owned = _volumes[j];
                    if (owned is not null && !owned.WasCollected && collider.Pointer == owned.Pointer)
                    { mask |= 1 << j; break; }
                }
            }
            ValidateReceiver(binding.System, pointer);
            return mask;
        }

        private void Remove(WeatherBinding binding)
        {
            if (!Live(binding.System)) return;
            try
            {
                var accessors = TriggerAccessors.Value;
                nint pointer = IL2CPP.Il2CppObjectBaseToPtrNotNull(binding.System);
                int present = OwnedMask(binding, ref pointer);
                for (int i = 0; i < _volumes.Length; i++)
                    if ((present & (1 << i)) != 0)
                        accessors.RemoveCollider(ref pointer, IL2CPP.Il2CppObjectBaseToPtrNotNull(_volumes[i]!));
                ValidateReceiver(binding.System, pointer);
                if (OwnedMask(binding, ref pointer) != 0)
                    throw new InvalidOperationException("The precipitation trigger retained an owned cabin volume during cleanup.");
            }
            finally
            {
                GC.KeepAlive(binding.System);
                GC.KeepAlive(_volumes);
            }
        }

        private static void ValidateReceiver(ParticleSystem system, nint pointer)
        {
            if (!Live(system) || pointer != system.Pointer)
                throw new InvalidOperationException("The native precipitation receiver was lost or changed.");
        }

        private static TriggerBindings ResolveTriggers()
        {
            if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
                throw new PlatformNotSupportedException("Cabin precipitation bindings require the Windows x64 player.");
            return new TriggerBindings();
        }

        internal void Reset(bool force = false)
        {
            if (_root is null && _follow is null && _bindings.Count == 0)
            { _releasing = false; return; }
            _releasing = true;
            try
            {
                if (Live(_root) && _root.activeSelf) _root.SetActive(false);
                float now = Time.realtimeSinceStartup;
                if (!force && now < _nextCleanup) return;
                _nextCleanup = now + 2;
                // Keep failed registrations and their disabled colliders alive
                // for bounded cleanup retries. Never clear or restore a foreign list.
                for (int i = _bindings.Count - 1; i >= 0; i--)
                {
                    try { Remove(_bindings[i]); _bindings.RemoveAt(i); }
                    catch (Exception error) { CleanupWarning(error); }
                }
                if (_bindings.Count != 0) return;
                if (Live(_root)) UnityEngine.Object.Destroy(_root);
                _root = _follow = null;
                _types = null;
                _body = null;
                Array.Clear(_volumes);
                _nextCleanup = 0;
                _releasing = false;
            }
            catch (Exception error) { CleanupWarning(error); }
        }

        private void Warn(string message)
        {
            if (_warned) return;
            _warned = true;
            Log.LogWarning("Spacecraft cabin weather: " + message);
        }

        private void CleanupWarning(Exception error)
        {
            if (_cleanupWarned) return;
            _cleanupWarned = true;
            Log.LogWarning("Spacecraft cabin weather cleanup failed: " + error);
        }

        // Unity registers nested modules with '/', unlike the generated '+' names.
        // On Windows x64 the injected module is one ParticleSystem object reference.
        private sealed class TriggerBindings
        {
            internal readonly EnabledGetter Enabled =
                IL2CPP.ResolveICall<EnabledGetter>("UnityEngine.ParticleSystem/TriggerModule::get_enabled_Injected");
            internal readonly IntGetter Inside =
                IL2CPP.ResolveICall<IntGetter>("UnityEngine.ParticleSystem/TriggerModule::get_inside_Injected");
            internal readonly IntGetter Outside =
                IL2CPP.ResolveICall<IntGetter>("UnityEngine.ParticleSystem/TriggerModule::get_outside_Injected");
            internal readonly IntGetter Enter =
                IL2CPP.ResolveICall<IntGetter>("UnityEngine.ParticleSystem/TriggerModule::get_enter_Injected");
            internal readonly IntGetter Exit =
                IL2CPP.ResolveICall<IntGetter>("UnityEngine.ParticleSystem/TriggerModule::get_exit_Injected");
            internal readonly IntGetter ColliderCount =
                IL2CPP.ResolveICall<IntGetter>("UnityEngine.ParticleSystem/TriggerModule::get_colliderCount_Injected");
            internal readonly ColliderGetter GetCollider =
                IL2CPP.ResolveICall<ColliderGetter>("UnityEngine.ParticleSystem/TriggerModule::GetCollider_Injected");
            internal readonly ColliderAction AddCollider =
                IL2CPP.ResolveICall<ColliderAction>("UnityEngine.ParticleSystem/TriggerModule::AddCollider_Injected");
            internal readonly ColliderAction RemoveCollider =
                IL2CPP.ResolveICall<ColliderAction>("UnityEngine.ParticleSystem/TriggerModule::RemoveColliderObject_Injected");
        }

        private sealed class WeatherBinding
        {
            internal readonly ParticleSystem System;
            internal readonly bool Rain;
            internal bool Seen;

            internal WeatherBinding(ParticleSystem system, bool rain)
            { System = system; Rain = rain; }
        }
    }
}
