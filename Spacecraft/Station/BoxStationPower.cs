// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Modstoke.Client;

[Flags]
internal enum BoxStationPowerChannel : byte
{
    Equipment = 1,
    Lighting = 2,
    Environment = 4
}

internal readonly record struct BoxStationApc(int Object, int Cell, int Area, int Direction,
    double CapacityJoules, int Network);

internal readonly record struct BoxStationLight(int Object, int Cell, int Area, int Direction, bool Small)
{
    internal double Watts => Small ? 80 : 160;
}

internal readonly record struct BoxStationPowerState(float ChargeKwh, byte Enabled, byte Available);

internal readonly record struct BoxStationSolarPanel(int Object, int Cell, int Network, int Controller);

internal sealed partial class BoxStationPower
{
    private const byte AllChannels = 7;
    private const double JoulesPerKwh = 3_600_000, ControllerWatts = 5;
    private readonly BoxStationLayout _layout;
    private readonly Definitions _definitions;
    private readonly double[] _apcJoules, _smesJoules, _outputRemaining;
    private double[] _solarJoules, _networkRemaining;
    private readonly byte[] _enabled;
    private readonly bool[] _controllerPowered;
    private readonly float[] _lightFraction;
    private double _dt, _solarExposure;
    private bool _stepping;

    internal ImmutableArray<BoxStationApc> Apcs => _connectivity.Apcs;
    internal ImmutableArray<BoxStationLight> Lights => _definitions.Lights;
    internal ImmutableArray<BoxStationSolarPanel> SolarPanels => _connectivity.SolarPanels;
    internal int NetworkCount => _solarJoules.Length;
    internal int CableCount => _definitions.Cables.Length;
    internal int CableSiteCount => _definitions.CableSites.Length;
    internal double SolarWatts { get; private set; }

    internal BoxStationPower(BoxStationLayout layout, bool seedSource)
    {
        ArgumentNullException.ThrowIfNull(layout);
        _layout = layout;
        _definitions = LayoutDefinitions.GetValue(layout, BuildDefinitions);
        _connectivity = OriginalConnectivity(_definitions);
        _cableState = InitialCableState(layout);
        CableInstalledCount = CableCount;
        _apcJoules = new double[Apcs.Length];
        _smesJoules = new double[_definitions.Smes.Length];
        _solarJoules = new double[_definitions.SolarWatts.Length];
        _networkRemaining = new double[_solarJoules.Length];
        _outputRemaining = new double[_smesJoules.Length];
        _smesInputLimits = new double[_smesJoules.Length];
        _smesOutputLimits = new double[_smesJoules.Length];
        _smesInputJoules = new double[_smesJoules.Length];
        _smesOutputJoules = new double[_smesJoules.Length];
        _smesInputWatts = new double[_smesJoules.Length];
        _smesOutputWatts = new double[_smesJoules.Length];
        _smesEnabled = new byte[_smesJoules.Length];
        _enabled = new byte[Apcs.Length];
        _controllerPowered = new bool[Apcs.Length];
        _lightFraction = new float[Apcs.Length];
        for (int i = 0; i < Apcs.Length; i++)
        {
            _enabled[i] = _definitions.InitialChannels[i];
            if (seedSource) _apcJoules[i] = _definitions.InitialApcJoules[i];
        }
        for (int i = 0; i < _smesJoules.Length; i++)
        {
            var initial = _definitions.InitialSmes[i];
            _smesInputLimits[i] = initial.InputWatts;
            _smesOutputLimits[i] = initial.OutputWatts;
            _smesEnabled[i] = initial.Enabled;
            if (seedSource) _smesJoules[i] = initial.Joules;
        }
        RefreshAvailability();
    }

    internal double StoredJoules
    {
        get
        {
            double total = 0;
            foreach (double charge in _apcJoules) total += charge;
            foreach (double charge in _smesJoules) total += charge;
            return total;
        }
    }

    internal ImmutableArray<BoxStationPowerState> Snapshot
    {
        get
        {
            var states = ImmutableArray.CreateBuilder<BoxStationPowerState>(Apcs.Length);
            for (int i = 0; i < Apcs.Length; i++)
            {
                byte available = 0;
                if (GetPowered(Apcs[i].Area, BoxStationPowerChannel.Equipment)) available |= 1;
                if (GetPowered(Apcs[i].Area, BoxStationPowerChannel.Lighting)) available |= 2;
                if (GetPowered(Apcs[i].Area, BoxStationPowerChannel.Environment)) available |= 4;
                states.Add(new((float)(_apcJoules[i] / JoulesPerKwh), _enabled[i], available));
            }
            return states.MoveToImmutable();
        }
    }

    internal int AreaController(int area)
        => area >= 0 && area < _definitions.AreaControllers.Length ? _definitions.AreaControllers[area] : -1;

    internal bool GetPowered(int area, BoxStationPowerChannel channel)
    {
        int apc = AreaController(area);
        if (apc < 0 || !IsChannel(channel) || (_enabled[apc] & (byte)channel) == 0
            || !_controllerPowered[apc]) return false;
        if (channel == BoxStationPowerChannel.Lighting && _definitions.LightWatts[apc] > 0)
            return _lightFraction[apc] > 0;
        return HasSupply(apc);
    }

    internal float LightFraction(int area)
    {
        int apc = AreaController(area);
        return apc >= 0 && (_enabled[apc] & (byte)BoxStationPowerChannel.Lighting) != 0
            ? _lightFraction[apc] : 0;
    }

    internal bool Toggle(int apcIndex, BoxStationPowerChannel channel)
    {
        if (apcIndex < 0 || apcIndex >= Apcs.Length || !IsChannel(channel)) return false;
        _enabled[apcIndex] ^= (byte)channel;
        if (channel == BoxStationPowerChannel.Lighting) _lightFraction[apcIndex] = 0;
        return true;
    }

    // Authority brackets all loads in one step. Exposure is the caller's clamped cosine
    // incidence including eclipse for fixed horizontal collectors, not source tracker logic.
    internal void BeginStep(double dt, double solarExposure)
    {
        if (_stepping) throw new InvalidOperationException("Station power step is already open.");
        if (!double.IsFinite(dt) || dt < 0 || dt > 1) throw new ArgumentOutOfRangeException(nameof(dt));
        if (!double.IsFinite(solarExposure) || solarExposure < 0 || solarExposure > 1)
            throw new ArgumentOutOfRangeException(nameof(solarExposure));
        _stepping = true;
        _dt = dt;
        _solarExposure = solarExposure;
        SolarWatts = 0;
        for (int network = 0; network < _solarJoules.Length; network++)
        {
            double watts = _connectivity.SolarWatts[network] * solarExposure;
            SolarWatts += watts;
            _networkRemaining[network] = _solarJoules[network] = watts * dt;
        }
        for (int i = 0; i < _smesJoules.Length; i++)
        {
            var smes = Smes[i];
            double output = smes.OutputNetwork >= 0 && (_smesEnabled[i] & 2) != 0
                ? Math.Min(_smesJoules[i], _smesOutputLimits[i] * dt) : 0;
            _outputRemaining[i] = output;
            _smesInputJoules[i] = _smesOutputJoules[i] = 0;
            if (smes.OutputNetwork >= 0) _networkRemaining[smes.OutputNetwork] += output;
        }
        if (dt == 0) return;
        for (int i = 0; i < Apcs.Length; i++)
        {
            double control = ControllerWatts * dt;
            _controllerPowered[i] = Consume(i, control) >= control;
            _lightFraction[i] = 0;
            if (!_controllerPowered[i] || (_enabled[i] & (byte)BoxStationPowerChannel.Lighting) == 0) continue;
            double lighting = _definitions.LightWatts[i] * dt;
            _lightFraction[i] = lighting > 0 ? (float)(Consume(i, lighting) / lighting) : 1;
        }
    }

    internal double Draw(int area, BoxStationPowerChannel channel, double joules)
    {
        if (!_stepping) throw new InvalidOperationException("Station power step is not open.");
        if (!IsChannel(channel)) throw new ArgumentOutOfRangeException(nameof(channel));
        if (!double.IsFinite(joules) || joules < 0) throw new ArgumentOutOfRangeException(nameof(joules));
        int apc = AreaController(area);
        if (_dt == 0 || apc < 0 || !_controllerPowered[apc] || (_enabled[apc] & (byte)channel) == 0) return 0;
        return Consume(apc, joules);
    }

    internal void EndStep()
    {
        if (!_stepping) throw new InvalidOperationException("Station power step is not open.");
        // Frozen output budgets contain only energy stored before BeginStep. Newly
        // charged SMES energy cannot be forwarded in this step or spent twice.
        if (_dt > 0)
        {
            for (int i = 0; i < Apcs.Length; i++)
            {
                if (!_definitions.ChargeApc[i]) continue;
                double missing = Apcs[i].CapacityJoules - _apcJoules[i];
                _apcJoules[i] += DrawNetwork(Apcs[i].Network, missing);
            }
            for (int i = 0; i < _smesJoules.Length; i++)
            {
                var smes = Smes[i];
                double input = (_smesEnabled[i] & 1) != 0
                    ? Math.Min(smes.CapacityJoules - _smesJoules[i], _smesInputLimits[i] * _dt) : 0;
                _smesInputJoules[i] = DrawNetwork(smes.InputNetwork, input, i);
                _smesJoules[i] += _smesInputJoules[i];
            }
            for (int i = 0; i < _smesJoules.Length; i++)
            {
                _smesInputWatts[i] = Math.Min(_smesInputLimits[i], _smesInputJoules[i] / _dt);
                _smesOutputWatts[i] = Math.Min(_smesOutputLimits[i], _smesOutputJoules[i] / _dt);
            }
        }
        _stepping = false;
    }

    private double Consume(int apc, double joules)
    {
        double supplied = DrawNetwork(Apcs[apc].Network, joules);
        double battery = Math.Min(_apcJoules[apc], joules - supplied);
        _apcJoules[apc] -= battery;
        return supplied + battery;
    }

    private double DrawNetwork(int network, double joules, int chargingSmes = -1)
    {
        if (network < 0 || joules <= 0) return 0;
        double solar = Math.Min(_solarJoules[network], joules);
        _solarJoules[network] -= solar;
        double supplied = solar;
        foreach (int i in _connectivity.NetworkSmes[network])
        {
            if (i == chargingSmes || supplied >= joules) continue;
            double output = Math.Min(Math.Min(_outputRemaining[i], _smesJoules[i]), joules - supplied);
            _outputRemaining[i] -= output;
            _smesJoules[i] -= output;
            _smesOutputJoules[i] += output;
            supplied += output;
        }
        _networkRemaining[network] = Math.Max(0, _networkRemaining[network] - supplied);
        return supplied;
    }

    private bool HasSupply(int apc)
    {
        if (_apcJoules[apc] > 0) return true;
        int network = Apcs[apc].Network;
        if (network < 0) return false;
        if (_stepping) return _networkRemaining[network] > 0;
        if (_connectivity.SolarWatts[network] * _solarExposure > 0) return true;
        foreach (int i in _connectivity.NetworkSmes[network])
            if (_smesJoules[i] > 0 && (_smesEnabled[i] & 2) != 0 && _smesOutputLimits[i] > 0) return true;
        return false;
    }

    private void RefreshAvailability()
    {
        for (int i = 0; i < Apcs.Length; i++)
        {
            _controllerPowered[i] = HasSupply(i);
            _lightFraction[i] = _controllerPowered[i] && (_enabled[i] & 2) != 0 ? 1 : 0;
        }
    }

    internal BoxStationPowerSave Capture()
    {
        if (_stepping) throw new InvalidOperationException("Cannot save an open station power step.");
        var apcs = new int[Apcs.Length];
        var smes = new int[_smesJoules.Length];
        var settings = new BoxStationSmesSetting[_smesJoules.Length];
        int removedCount = 0, addedCount = 0;
        for (int i = 0; i < Cables.Length; i++)
            if (!CableInstalled(i)) removedCount++;
        for (int i = Cables.Length; i < Sites.Length; i++)
            if (CableInstalled(i)) addedCount++;
        var removed = new int[removedCount];
        var added = new ushort[addedCount];
        removedCount = addedCount = 0;
        for (int i = 0; i < Cables.Length; i++)
            if (!CableInstalled(i)) removed[removedCount++] = Cables[i].Object;
        for (int i = Cables.Length; i < Sites.Length; i++)
            if (CableInstalled(i)) added[addedCount++] = checked((ushort)Sites[i].Cell);
        for (int i = 0; i < apcs.Length; i++) apcs[i] = Apcs[i].Object;
        for (int i = 0; i < smes.Length; i++)
        {
            smes[i] = Smes[i].Object;
            settings[i] = new()
            {
                InputWatts = _smesInputLimits[i], OutputWatts = _smesOutputLimits[i], Enabled = _smesEnabled[i]
            };
        }
        return new()
        {
            Version = 4, LayoutHash = _layout.LayoutHash, ApcObjects = apcs, SmesObjects = smes,
            ApcJoules = (double[])_apcJoules.Clone(), SmesJoules = (double[])_smesJoules.Clone(),
            Enabled = (byte[])_enabled.Clone(), SmesSettings = settings,
            CableRevision = _cableState.Revision, RemovedCables = removed, AddedCableCells = added
        };
    }

    internal bool TryRestore(BoxStationPowerSave? saved)
    {
        if (_stepping || saved is null || saved.Version is not (1 or 2 or 3 or 4) || saved.LayoutHash != _layout.LayoutHash
            || saved.ApcObjects?.Length != Apcs.Length || saved.ApcJoules?.Length != Apcs.Length
            || saved.Enabled?.Length != Apcs.Length || saved.SmesObjects?.Length != _smesJoules.Length
            || saved.SmesJoules?.Length != _smesJoules.Length
            || (saved.Version == 1 ? saved.HasSmesSettings : saved.SmesSettings?.Length != Smes.Length)) return false;
        for (int i = 0; i < Apcs.Length; i++)
            if (saved.ApcObjects[i] != Apcs[i].Object || !ValidCharge(saved.ApcJoules[i], Apcs[i].CapacityJoules)
                || (saved.Enabled[i] & ~AllChannels) != 0) return false;
        for (int i = 0; i < _smesJoules.Length; i++)
            if (saved.SmesObjects[i] != Smes[i].Object
                || !ValidCharge(saved.SmesJoules[i], Smes[i].CapacityJoules)
                || saved.Version >= 2 && !ValidSmesSetting(saved.SmesSettings![i], Smes[i])) return false;
        if (!TryRestoreCableState(saved, out var cables)) return false;
        var connectivity = saved.Version < 3 ? OriginalConnectivity(_definitions) : BuildCableConnectivity(_layout, _definitions, cables.Installed);
        var solar = new double[connectivity.SolarWatts.Length];
        var remaining = new double[solar.Length];
        int installed = CountInstalledCables(_layout, cables);
        saved.ApcJoules.CopyTo(_apcJoules, 0);
        saved.SmesJoules.CopyTo(_smesJoules, 0);
        saved.Enabled.CopyTo(_enabled, 0);
        for (int i = 0; i < Smes.Length; i++)
        {
            var initial = _definitions.InitialSmes[i];
            var settings = saved.Version >= 2 ? saved.SmesSettings![i] : null;
            _smesInputLimits[i] = settings?.InputWatts ?? initial.InputWatts;
            _smesOutputLimits[i] = settings?.OutputWatts ?? initial.OutputWatts;
            _smesEnabled[i] = settings?.Enabled ?? initial.Enabled;
        }
        _solarExposure = 0;
        CommitCables(cables, connectivity, solar, remaining, installed);
        return true;
    }

    // The old global battery had no area custody. Share it by vacant capacity so
    // migration does not arbitrarily energize only the first rooms in map order.
    internal double AllocateLegacyEnergy(double joules)
    {
        if (_stepping) throw new InvalidOperationException("Cannot migrate an open station power step.");
        if (!double.IsFinite(joules) || joules < 0) throw new ArgumentOutOfRangeException(nameof(joules));
        double capacity = 0;
        for (int i = 0; i < Apcs.Length; i++) capacity += Apcs[i].CapacityJoules - _apcJoules[i];
        double remaining = Math.Min(joules, capacity), offered = remaining;
        for (int i = 0; i < Apcs.Length && remaining > 0; i++)
        {
            double vacancy = Apcs[i].CapacityJoules - _apcJoules[i];
            double assigned = Math.Min(vacancy, i == Apcs.Length - 1 ? remaining : offered * vacancy / capacity);
            assigned = Math.Min(assigned, remaining);
            _apcJoules[i] += assigned;
            remaining -= assigned;
        }
        RefreshAvailability();
        return joules - (offered - remaining);
    }

    private static bool IsChannel(BoxStationPowerChannel channel)
        => channel is BoxStationPowerChannel.Equipment or BoxStationPowerChannel.Lighting or BoxStationPowerChannel.Environment;

    private static bool ValidCharge(double joules, double capacity)
        => double.IsFinite(joules) && joules >= 0 && joules <= capacity;
}

internal sealed class BoxStationPowerSave
{
    [JsonRequired]
    public int Version { get; set; } = 4;
    [JsonRequired]
    public string? LayoutHash { get; set; }
    [JsonRequired]
    public int[]? ApcObjects { get; set; }
    [JsonRequired]
    public int[]? SmesObjects { get; set; }
    [JsonRequired]
    public double[]? ApcJoules { get; set; }
    [JsonRequired]
    public double[]? SmesJoules { get; set; }
    [JsonRequired]
    public byte[]? Enabled { get; set; }
    private BoxStationSmesSetting[]? _smesSettings;
    internal bool HasSmesSettings { get; private set; }
    public BoxStationSmesSetting[]? SmesSettings
    {
        get => _smesSettings;
        set
        {
            _smesSettings = value;
            HasSmesSettings = true;
        }
    }
    private long _cableRevision;
    private int[]? _removedCables;
    internal bool HasCableRevision { get; private set; }
    internal bool HasRemovedCables { get; private set; }
    public long CableRevision
    {
        get => _cableRevision;
        set
        {
            _cableRevision = value;
            HasCableRevision = true;
        }
    }
    public int[]? RemovedCables
    {
        get => _removedCables;
        set
        {
            _removedCables = value;
            HasRemovedCables = true;
        }
    }
    private ushort[]? _addedCableCells;
    internal bool HasAddedCableCells { get; private set; }
    public ushort[]? AddedCableCells
    {
        get => _addedCableCells;
        set
        {
            _addedCableCells = value;
            HasAddedCableCells = true;
        }
    }
}
