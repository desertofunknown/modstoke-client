// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.IO;
using FMOD;
using Modstoke.Sons;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Client;

// Prerecorded announcements are selected from authoritative telemetry on the Unity thread.
internal sealed class ClientSpacecraftComputerAudio : IDisposable
{
    private enum Cue { Start, Stop, Oxygen, Pressure, Override, Hull, Subsystem, WarpReady, Warp, Arrival, Fuel, LifeSupport, CarbonDioxide, Power }
    private static readonly string[] Assets =
    {
        "engine-start", "engine-stop", "oxygen-low", "pressure-low", "airlock-override", "hull-damage", "subsystem-fault",
        "warp-ready", "warp-engaged", "arrival", "fuel-reserve", "life-support", "carbon-dioxide", "power-reserve"
    };
    private static readonly short[]?[] Samples = new short[Assets.Length][];
    private readonly Sound[] _sounds = new Sound[Assets.Length];
    private readonly float[] _pending = new float[Assets.Length], _lastSpoken = new float[Assets.Length];
    private readonly bool[] _hazards = new bool[Assets.Length];
    private readonly Transform _ship;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private IntPtr _systemHandle;
    private Channel _channel;
    private SpacecraftFlightState _previous;
    private int _speaking = -1;
    private float _nextSpeech;
    private bool _observed, _failed, _disposed;

    internal ClientSpacecraftComputerAudio(Transform ship)
    {
        _ship = ship;
        Array.Fill(_lastSpoken, -1000);
    }

    internal bool Speaking => _speaking >= 0;

    internal void Update(SpacecraftFlightState state, ClientTabletPreferencesSnapshot? preferences)
    {
        if (_disposed || _failed || Environment.CurrentManagedThreadId != _thread) return;
        try
        {
            if (preferences == null || !state.Systems.Ready || !NearShip()
                || GameRuntime.IsTitleScene || GameRuntime.State.PauseMenuOpen || Time.timeScale <= 0)
            { Suspend(); return; }
            var volume = ClientTabletAlerts.DeviceVolume(preferences);
            if (volume <= 0) { Suspend(); return; }
            if (!ClientTabletAlerts.TryGetSystem(out var system)) { ClearHandles(); return; }
            if (_systemHandle != system.handle) { ClearHandles(); _systemHandle = system.handle; }
            var now = Time.realtimeSinceStartup;
            Observe(state, now);
            if (_channel.handle != IntPtr.Zero)
            {
                var result = _channel.isPlaying(out var playing);
                if (result == RESULT.ERR_INVALID_HANDLE || result == RESULT.ERR_CHANNEL_STOLEN || result == RESULT.OK && !playing)
                { _channel = default; _speaking = -1; _nextSpeech = now + .65f; }
                else ClientTabletAlerts.Check(result, "Checking ship computer speech");
            }
            var selected = -1;
            for (var i = 0; i < _pending.Length; i++)
                if (_pending[i] > now && (selected < 0 || Priority((Cue)i) > Priority((Cue)selected))) selected = i;
            if (selected >= 0 && _speaking >= 0 && Priority((Cue)selected) >= 90 && Priority((Cue)_speaking) < 80)
            { Stop(); _nextSpeech = now; }
            if (selected >= 0 && _speaking < 0 && now >= _nextSpeech)
                Speak(system, selected, now);
            if (_channel.handle != IntPtr.Zero)
            {
                var point = _ship.TransformPoint(new Vector3(0, .6f, 1));
                var position = new VECTOR { x = point.x, y = point.y, z = point.z };
                var motion = Vector3.ClampMagnitude(state.Velocity, 100);
                var velocity = new VECTOR { x = motion.x, y = motion.y, z = motion.z };
                ClientTabletAlerts.Check(_channel.set3DAttributes(ref position, ref velocity), "Positioning ship computer speech");
                ClientTabletAlerts.Check(_channel.setVolume(volume * .9f), "Setting ship computer volume");
                ClientTabletAlerts.Check(_channel.setPaused(false), "Playing ship computer speech");
            }
            _previous = state;
            _observed = true;
        }
        catch (Exception error) { Fail(error); }
    }

    private bool NearShip()
    {
        var player = LocalPlayer.Transform;
        if (!_ship || !_ship.gameObject.activeInHierarchy || player == null || player.WasCollected || !player
            || !GameRuntime.State.IsInWorld || !LocalPlayer.IsInWorld) return false;
        var point = _ship.InverseTransformPoint(player.position);
        return point.x is >= -11 and <= 11 && point.y is >= -5 and <= 6 && point.z is >= -21 and <= 13;
    }

    private void Observe(SpacecraftFlightState state, float now)
    {
        var systems = state.Systems;
        var travel = state.Travel != SpacecraftTravel.None;
        var warp = state.Travel is SpacecraftTravel.Destination or SpacecraftTravel.Return or SpacecraftTravel.Moon;
        if (_observed)
        {
            if (state.Powered != _previous.Powered)
            {
                _pending[(int)(state.Powered ? Cue.Stop : Cue.Start)] = 0;
                Queue(state.Powered ? Cue.Start : Cue.Stop, now);
            }
            if (systems.EmergencyOverride && !_previous.Systems.EmergencyOverride) Queue(Cue.Override, now);
            if (warp && _previous.Travel == SpacecraftTravel.None) Queue(Cue.Warp, now);
            if (!travel && _previous.Travel != SpacecraftTravel.None && state.Region != _previous.Region) Queue(Cue.Arrival, now);
        }
        if (!systems.EmergencyOverride) _pending[(int)Cue.Override] = 0;
        if (!warp) _pending[(int)Cue.Warp] = 0;
        if (ClientSpacecraftComputer.CanWarp(systems) && !travel)
        {
            if (!_observed || !ClientSpacecraftComputer.CanWarp(_previous.Systems)) Queue(Cue.WarpReady, now);
        }
        else _pending[(int)Cue.WarpReady] = 0;
        // Partial pressure distinguishes breathable oxygen from a high fraction in near vacuum.
        var oxygen = systems.CabinPressure * systems.OxygenFraction;
        Hazard(Cue.Oxygen, oxygen < 16, oxygen > 18, now);
        Hazard(Cue.Pressure, systems.CabinPressure < 65, systems.CabinPressure > 75, now);
        Hazard(Cue.CarbonDioxide, systems.CO2Fraction > .015f, systems.CO2Fraction < .01f, now);
        Hazard(Cue.Hull, systems.Hull.MinimumIntegrity01 < .8f, systems.Hull.MinimumIntegrity01 > .9f, now);
        var subsystem = Mathf.Min(systems.EngineIntegrity01, Mathf.Min(systems.PowerIntegrity01, systems.WarpIntegrity01));
        Hazard(Cue.Subsystem, subsystem < .6f, subsystem > .7f, now);
        Hazard(Cue.Fuel, systems.Fuel01 < .15f, systems.Fuel01 > .2f, now);
        Hazard(Cue.Power, systems.Battery01 < .12f, systems.Battery01 > .18f, now);
        Hazard(Cue.LifeSupport, systems.LifeSupportIntegrity01 <= .1f || systems.PowerIntegrity01 <= .08f || systems.Battery01 <= .00001f,
            systems.LifeSupportIntegrity01 > .2f && systems.PowerIntegrity01 > .15f && systems.Battery01 > .03f, now);
        if (_observed && (_previous.Systems.Hull.Cabin - systems.Hull.Cabin > .04f
            || _previous.Systems.Hull.Airlock - systems.Hull.Airlock > .04f
            || _previous.Systems.Hull.Entry - systems.Hull.Entry > .04f)) Queue(Cue.Hull, now);
    }

    private void Hazard(Cue cue, bool unsafeValue, bool recovered, float now)
    {
        var index = (int)cue;
        var wasActive = _hazards[index];
        if (unsafeValue) _hazards[index] = true;
        else if (recovered) _hazards[index] = false;
        if (_hazards[index]) Queue(cue, now);
        else if (wasActive) _pending[index] = 0;
    }

    private void Queue(Cue cue, float now)
    {
        var index = (int)cue;
        var cooldown = cue is Cue.Start or Cue.Stop or Cue.Warp or Cue.Arrival ? 8 : cue == Cue.Hull ? 25 : 45;
        if (_pending[index] > now || now - _lastSpoken[index] < cooldown) return;
        _pending[index] = now + (Priority(cue) >= 80 ? 20 : 10);
    }

    private static int Priority(Cue cue) => cue switch
    {
        Cue.LifeSupport => 100, Cue.Oxygen => 95, Cue.CarbonDioxide => 93, Cue.Pressure => 90,
        Cue.Hull => 80, Cue.Subsystem => 70, Cue.Override => 60, Cue.Power => 50, Cue.Fuel => 45,
        Cue.Warp or Cue.Arrival => 40, Cue.WarpReady => 20, _ => 10
    };

    private void Speak(FMOD.System system, int index, float now)
    {
        if (_sounds[index].handle == IntPtr.Zero)
            _sounds[index] = ClientTabletAlerts.CreatePcmSound(system, Samples[index] ??= ReadSamples(index),
                MODE._3D | MODE._3D_WORLDRELATIVE | MODE._3D_INVERSETAPEREDROLLOFF | MODE.LOOP_OFF);
        ClientTabletAlerts.Check(system.getMasterChannelGroup(out var output), "Finding ship computer audio output");
        ClientTabletAlerts.Check(system.playSound(_sounds[index], output, true, out _channel), "Starting ship computer speech");
        _speaking = index;
        _pending[index] = 0;
        _lastSpoken[index] = now;
        ClientTabletAlerts.Check(_channel.set3DMinMaxDistance(10, 32), "Setting ship computer audio range");
        ClientTabletAlerts.Check(_channel.setVolumeRamp(true), "Smoothing ship computer audio");
    }

    private static short[] ReadSamples(int index)
    {
        using var stream = typeof(ClientSpacecraftComputerAudio).Assembly.GetManifestResourceStream(
            "Modstoke.Client.Assets.spacecraft-voice." + Assets[index] + ".wav")
            ?? throw new InvalidDataException("Missing ship computer recording: " + Assets[index]);
        using var reader = new BinaryReader(stream);
        // Bundled recordings use canonical 24 kHz, mono, 16-bit PCM with no auxiliary chunks.
        if (stream.Length is < 46 or > 480044 || reader.ReadUInt32() != 0x46464952
            || reader.ReadUInt32() != stream.Length - 8 || reader.ReadUInt32() != 0x45564157
            || reader.ReadUInt32() != 0x20746d66 || reader.ReadUInt32() != 16
            || reader.ReadUInt16() != 1 || reader.ReadUInt16() != 1 || reader.ReadUInt32() != 24000
            || reader.ReadUInt32() != 48000 || reader.ReadUInt16() != 2 || reader.ReadUInt16() != 16
            || reader.ReadUInt32() != 0x61746164) throw new InvalidDataException("Invalid ship computer recording.");
        var length = reader.ReadUInt32();
        if (length != stream.Length - 44 || length % 2 != 0) throw new InvalidDataException("Invalid ship computer PCM length.");
        var samples = new short[length / 2];
        for (var i = 0; i < samples.Length; i++) samples[i] = reader.ReadInt16();
        return samples;
    }

    internal void Suspend()
    {
        if (_disposed || Environment.CurrentManagedThreadId != _thread) return;
        try
        {
            if (_systemHandle != IntPtr.Zero && ClientTabletAlerts.TryGetSystem(out var system) && system.handle == _systemHandle) Stop();
            else ClearHandles();
        }
        catch (Exception error) { Fail(error); }
        Array.Clear(_pending);
        Array.Clear(_hazards);
        _observed = false;
    }

    private void Stop()
    {
        var channel = _channel;
        _channel = default;
        _speaking = -1;
        if (channel.handle == IntPtr.Zero) return;
        var result = channel.stop();
        if (result != RESULT.ERR_INVALID_HANDLE && result != RESULT.ERR_CHANNEL_STOLEN)
            ClientTabletAlerts.Check(result, "Stopping ship computer speech");
    }

    private void ClearHandles()
    {
        _channel = default;
        _speaking = -1;
        _systemHandle = IntPtr.Zero;
        _observed = false;
        Array.Clear(_sounds);
        Array.Clear(_pending);
        Array.Clear(_hazards);
    }

    private void Fail(Exception error)
    {
        if (!_failed) UnityEngine.Debug.LogWarning("Modstoke Client: ship computer audio is unavailable: " + error);
        _failed = true;
        Release();
    }

    private void Release()
    {
        try
        {
            if (_systemHandle == IntPtr.Zero || !ClientTabletAlerts.TryGetSystem(out var system) || system.handle != _systemHandle) return;
            Exception? failure = null;
            try { Stop(); } catch (Exception error) { failure = error; }
            for (var i = 0; i < _sounds.Length; i++)
            {
                try
                {
                    if (_sounds[i].handle == IntPtr.Zero) continue;
                    var sound = _sounds[i];
                    _sounds[i] = default;
                    ClientTabletAlerts.Check(sound.release(), "Releasing ship computer audio");
                }
                catch (Exception error) { failure ??= error; }
            }
            if (failure != null) UnityEngine.Debug.LogWarning("Modstoke Client: ship computer audio cleanup: " + failure);
        }
        catch (Exception error) { UnityEngine.Debug.LogWarning("Modstoke Client: ship computer audio cleanup: " + error); }
        finally { ClearHandles(); }
    }

    public void Dispose()
    {
        if (_disposed || Environment.CurrentManagedThreadId != _thread) return;
        _disposed = true;
        Release();
    }
}
