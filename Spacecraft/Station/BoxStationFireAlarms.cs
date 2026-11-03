// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Modstoke.Client;

internal enum BoxStationFireAlarmControl : byte
{
    Raise = 0,
    Reset = 1,
    Detection = 2
}

internal readonly record struct BoxStationFireAlarmDevice(int Object, int Cell, int Area, int Zone, int Direction);

internal readonly record struct BoxStationFireAlarmState(
    bool Powered, bool Detecting, byte ThermalHazard, float CooldownSeconds);

internal readonly record struct BoxStationFireAreaState(bool Latched, float RecloseSeconds);

internal sealed partial class BoxStationFireAlarms
{
    private const double IdleWatts = 2, LatchedWatts = 6;
    internal const double AlarmCooldownSeconds = 6.7, RecloseIntervalSeconds = 10;
    private static readonly ConditionalWeakTable<BoxStationLayout, Definitions> LayoutDefinitions = new();
    private readonly BoxStationLayout _layout;
    private readonly Definitions _definitions;
    private readonly BoxStationFireAlarmState[] _states;
    private readonly bool[] _operational, _latched;
    private readonly double[] _cooldownSeconds, _recloseSeconds;
    private readonly sbyte[] _doorRequests;
    private bool _hasStepped;

    internal ImmutableArray<BoxStationFireAlarmDevice> Devices { get; }
    internal ImmutableArray<BoxStationFireAlarmState> AlarmSnapshot => ImmutableArray.CreateRange(_states);
    internal ImmutableArray<BoxStationFireAreaState> AreaSnapshot
    {
        get
        {
            var states = ImmutableArray.CreateBuilder<BoxStationFireAreaState>(_latched.Length);
            for (int area = 0; area < _latched.Length; area++)
                states.Add(new(_latched[area], (float)_recloseSeconds[area]));
            return states.MoveToImmutable();
        }
    }

    internal BoxStationFireAlarms(BoxStationLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        _layout = layout;
        _definitions = LayoutDefinitions.GetValue(layout, BuildDefinitions);
        Devices = _definitions.Devices;
        _states = new BoxStationFireAlarmState[Devices.Length];
        _operational = new bool[Devices.Length];
        _cooldownSeconds = new double[Devices.Length];
        _latched = new bool[layout.Areas.Length];
        _recloseSeconds = new double[layout.Areas.Length];
        _doorRequests = new sbyte[layout.Doors.Length];
        for (int i = 0; i < Devices.Length; i++) _states[i] = new(false, true, 0, 0);
    }

    internal static ImmutableArray<BoxStationFireAlarmDevice> GetDevices(BoxStationLayout layout)
        => LayoutDefinitions.GetValue(layout, BuildDefinitions).Devices;

    internal static ImmutableArray<int> GetDoorAreas(BoxStationLayout layout, int door)
        => door < 0 || door >= layout.Doors.Length ? ImmutableArray<int>.Empty
            : LayoutDefinitions.GetValue(layout, BuildDefinitions).DoorAreas[door];

    internal static bool ValidControl(BoxStationFireAlarmControl control)
        => control is BoxStationFireAlarmControl.Raise or BoxStationFireAlarmControl.Reset
            or BoxStationFireAlarmControl.Detection;

    // Authority calls this inside the power step, before door movement and gas reactions.
    internal void Step(double dt, BoxStationRooms rooms, BoxStationPower power)
    {
        CheckRooms(rooms);
        if (!double.IsFinite(dt) || dt < 0 || dt > 1) throw new ArgumentOutOfRangeException(nameof(dt));
        if (dt == 0) return;
        _hasStepped = true;
        for (int area = 0; area < _latched.Length; area++)
        {
            if (!_latched[area]) continue;
            _recloseSeconds[area] -= dt;
            if (_recloseSeconds[area] > 0) continue;
            QueueClose(area);
            _recloseSeconds[area] += RecloseIntervalSeconds;
        }
        for (int i = 0; i < Devices.Length; i++)
        {
            var device = Devices[i];
            _cooldownSeconds[i] = Math.Max(0, _cooldownSeconds[i] - dt);
            double requested = (_latched[device.Area] ? LatchedWatts : IdleWatts) * dt;
            bool powered = !_definitions.AlwaysUnpowered[device.Area]
                && power.GetPowered(device.Area, BoxStationPowerChannel.Environment)
                && power.Draw(device.Area, BoxStationPowerChannel.Environment, requested) >= requested;
            _operational[i] = powered;
            int node = rooms.NodeAtCell(device.Cell);
            var gas = node >= 0 ? rooms.Gas(node) : StationGas.Vacuum(StationGas.SourceCellVolume);
            Sample(i, gas, powered);
            if (powered && _states[i].ThermalHazard != 0 && _cooldownSeconds[i] == 0) Raise(i);
        }
    }

    // Telemetry refresh neither spends power nor changes latches, requests or timers.
    // Availability cannot revive a sensor whose most recent step failed its draw.
    internal void Refresh(BoxStationRooms rooms, BoxStationPower power)
    {
        CheckRooms(rooms);
        for (int i = 0; i < Devices.Length; i++)
        {
            var device = Devices[i];
            bool powered = (!_hasStepped || _operational[i]) && !_definitions.AlwaysUnpowered[device.Area]
                && power.GetPowered(device.Area, BoxStationPowerChannel.Environment);
            int node = rooms.NodeAtCell(device.Cell);
            var gas = node >= 0 ? rooms.Gas(node) : StationGas.Vacuum(StationGas.SourceCellVolume);
            Sample(i, gas, powered);
        }
    }

    private void Sample(int index, in StationGas gas, bool powered)
    {
        bool detecting = _states[index].Detecting;
        byte hazard = 0;
        if (powered && detecting)
        {
            double temperature = gas.TemperatureKelvin;
            hazard = temperature > 473.15 ? (byte)2 : temperature < 270.15 ? (byte)1 : (byte)0;
        }
        _states[index] = new(powered, detecting, hazard, (float)_cooldownSeconds[index]);
    }

    internal string Command(int index, BoxStationFireAlarmControl control, BoxStationPower power)
    {
        if (index < 0 || index >= Devices.Length || !ValidControl(control)) return "Unknown station fire alarm control.";
        int area = Devices[index].Area;
        if (_definitions.AlwaysUnpowered[area]) return "Fire alarms are disabled in this area.";
        if (_hasStepped && !_operational[index]
            || !power.GetPowered(area, BoxStationPowerChannel.Environment))
            return "This area's environment circuit has no power.";
        switch (control)
        {
            case BoxStationFireAlarmControl.Raise:
                if (_cooldownSeconds[index] > 0) return "Fire alarm is cooling down.";
                Raise(index);
                return "Area fire alarm raised.";
            case BoxStationFireAlarmControl.Reset:
                if (!_latched[area]) return "Area fire alarm is already clear.";
                _latched[area] = false;
                _recloseSeconds[area] = 0;
                foreach (int door in _definitions.AreaDoors[area])
                    _doorRequests[door] = DoorLatched(door) ? (sbyte)-1 : (sbyte)1;
                return "Area fire alarm cleared.";
            case BoxStationFireAlarmControl.Detection:
                bool detecting = !_states[index].Detecting;
                _states[index] = _states[index] with { Detecting = detecting, ThermalHazard = 0 };
                return detecting ? "Fire alarm thermal detection enabled." : "Fire alarm thermal detection disabled.";
            default:
                return "Unknown station fire alarm control.";
        }
    }

    private void Raise(int index)
    {
        int area = Devices[index].Area;
        if (_definitions.AlwaysUnpowered[area]) return;
        _cooldownSeconds[index] = AlarmCooldownSeconds;
        _states[index] = _states[index] with { CooldownSeconds = (float)AlarmCooldownSeconds };
        // Repeated sensor alarms do not postpone the area's independent reclose cycle.
        if (_latched[area]) return;
        _latched[area] = true;
        _recloseSeconds[area] = RecloseIntervalSeconds;
        QueueClose(area);
    }

    private void QueueClose(int area)
    {
        foreach (int door in _definitions.AreaDoors[area]) _doorRequests[door] = -1;
    }

    internal bool DoorLatched(int door)
    {
        if (door < 0 || door >= _doorRequests.Length) return false;
        foreach (int area in _definitions.DoorAreas[door])
            if (_latched[area]) return true;
        return false;
    }

    internal sbyte TakeDoorRequest(int door)
    {
        if (door < 0 || door >= _doorRequests.Length) return 0;
        sbyte request = _doorRequests[door];
        _doorRequests[door] = 0;
        return request;
    }

    internal static bool ValidState(BoxStationLayout layout, ImmutableArray<BoxStationFireAlarmState> alarms,
        ImmutableArray<BoxStationFireAreaState> areas)
    {
        var definitions = LayoutDefinitions.GetValue(layout, BuildDefinitions);
        if (alarms.IsDefault || alarms.Length != definitions.Devices.Length
            || areas.IsDefault || areas.Length != layout.Areas.Length) return false;
        for (int i = 0; i < alarms.Length; i++)
        {
            var state = alarms[i];
            if (state.ThermalHazard > 2 || !ValidSeconds(state.CooldownSeconds, AlarmCooldownSeconds)
                || (!state.Powered || !state.Detecting) && state.ThermalHazard != 0
                || definitions.AlwaysUnpowered[definitions.Devices[i].Area] && state.Powered) return false;
        }
        for (int area = 0; area < areas.Length; area++)
        {
            var state = areas[area];
            if (!ValidSeconds(state.RecloseSeconds, RecloseIntervalSeconds)
                || !state.Latched && state.RecloseSeconds != 0
                || definitions.AlwaysUnpowered[area] && state.Latched) return false;
        }
        return true;
    }

    internal SavedBoxStationFireAlarms Capture()
    {
        var saved = new SavedBoxStationFireAlarms
        {
            LayoutHash = _layout.LayoutHash, DeviceObjects = new int[Devices.Length], Detecting = new bool[Devices.Length],
            CooldownSeconds = (double[])_cooldownSeconds.Clone(), AreaNames = new string[_layout.Areas.Length],
            Latched = (bool[])_latched.Clone(), RecloseSeconds = (double[])_recloseSeconds.Clone(),
            DoorObjects = new int[_layout.Doors.Length], DoorRequests = (sbyte[])_doorRequests.Clone()
        };
        _layout.Areas.CopyTo(saved.AreaNames);
        for (int i = 0; i < Devices.Length; i++)
        {
            saved.DeviceObjects[i] = Devices[i].Object;
            saved.Detecting[i] = _states[i].Detecting;
        }
        for (int door = 0; door < _layout.Doors.Length; door++) saved.DoorObjects[door] = _layout.Doors[door].SourceObject;
        return saved;
    }

    internal bool TryRestore(SavedBoxStationFireAlarms? saved)
    {
        if (saved is null || saved.Version != 1 || saved.LayoutHash != _layout.LayoutHash
            || saved.DeviceObjects?.Length != Devices.Length || saved.Detecting?.Length != Devices.Length
            || saved.CooldownSeconds?.Length != Devices.Length || saved.AreaNames?.Length != _latched.Length
            || saved.Latched?.Length != _latched.Length || saved.RecloseSeconds?.Length != _latched.Length
            || saved.DoorObjects?.Length != _doorRequests.Length || saved.DoorRequests?.Length != _doorRequests.Length)
            return false;
        for (int i = 0; i < Devices.Length; i++)
            if (saved.DeviceObjects[i] != Devices[i].Object
                || !ValidSeconds(saved.CooldownSeconds[i], AlarmCooldownSeconds)) return false;
        for (int area = 0; area < _latched.Length; area++)
            if (saved.AreaNames[area] != _layout.Areas[area] || !ValidSeconds(saved.RecloseSeconds[area], RecloseIntervalSeconds)
                || !saved.Latched[area] && saved.RecloseSeconds[area] != 0
                || _definitions.AlwaysUnpowered[area] && saved.Latched[area]) return false;
        for (int door = 0; door < _doorRequests.Length; door++)
        {
            sbyte request = saved.DoorRequests[door];
            if (saved.DoorObjects[door] != _layout.Doors[door].SourceObject || request is < -1 or > 1) return false;
            var areas = _definitions.DoorAreas[door];
            if (areas.IsEmpty && request != 0) return false;
            bool latched = false;
            foreach (int area in areas) latched |= saved.Latched[area];
            if (request == 1 && latched || request == -1 && !latched) return false;
        }
        // All identities and values are checked before replacing live state.
        for (int i = 0; i < Devices.Length; i++)
            _states[i] = new(false, saved.Detecting[i], 0, (float)saved.CooldownSeconds[i]);
        Array.Copy(saved.CooldownSeconds, _cooldownSeconds, Devices.Length);
        Array.Copy(saved.Latched, _latched, _latched.Length);
        Array.Copy(saved.RecloseSeconds, _recloseSeconds, _latched.Length);
        Array.Copy(saved.DoorRequests, _doorRequests, _doorRequests.Length);
        Array.Clear(_operational);
        _hasStepped = false;
        return true;
    }

    private static bool ValidSeconds(double value, double maximum)
        => double.IsFinite(value) && value >= 0 && value <= maximum;

    private void CheckRooms(BoxStationRooms rooms)
    {
        ArgumentNullException.ThrowIfNull(rooms);
        if (!ReferenceEquals(rooms.Layout, _layout))
            throw new ArgumentException("Expected rooms for this station layout.", nameof(rooms));
    }
}

internal sealed class SavedBoxStationFireAlarms
{
    [JsonRequired]
    public int Version { get; set; } = 1;
    [JsonRequired]
    public string? LayoutHash { get; set; }
    [JsonRequired]
    public int[]? DeviceObjects { get; set; }
    [JsonRequired]
    public bool[]? Detecting { get; set; }
    [JsonRequired]
    public double[]? CooldownSeconds { get; set; }
    [JsonRequired]
    public string[]? AreaNames { get; set; }
    [JsonRequired]
    public bool[]? Latched { get; set; }
    [JsonRequired]
    public double[]? RecloseSeconds { get; set; }
    [JsonRequired]
    public int[]? DoorObjects { get; set; }
    [JsonRequired]
    public sbyte[]? DoorRequests { get; set; }
}
