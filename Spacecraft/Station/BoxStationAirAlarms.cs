// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Immutable;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Modstoke.Client;

internal enum BoxStationAirMode : byte
{
    Filtering = 1,
    Draught = 2,
    Panic = 3,
    Cycle = 4,
    Off = 5,
    Siphon = 7,
    Contaminated = 8,
    Refill = 9
}

internal readonly record struct BoxStationAirAlarmDevice(
    int Object, int Cell, int Area, int Zone, int Direction, bool ThresholdsDisabled);

internal readonly record struct BoxStationAirAlarmState(
    BoxStationAirMode Mode, byte Danger, byte AreaDanger, bool Powered, ushort WarningMask, ushort DangerMask);

internal sealed class BoxStationAirAlarms
{
    private const double Watts = 4, CyclePressure = 5.06625;
    private const ushort AllReadings = 511;
    private static readonly ConditionalWeakTable<BoxStationLayout, Definitions> LayoutDefinitions = new();
    private readonly BoxStationLayout _layout;
    private readonly BoxStationAirAlarmState[] _states;
    private readonly bool[] _operational;
    private readonly byte[] _areaDanger;
    private bool _hasStepped;

    private sealed class Definitions
    {
        internal required ImmutableArray<BoxStationAirAlarmDevice> Devices { get; init; }
    }

    internal ImmutableArray<BoxStationAirAlarmDevice> Devices { get; }
    internal ImmutableArray<BoxStationAirAlarmState> Snapshot => ImmutableArray.CreateRange(_states);

    internal BoxStationAirAlarms(BoxStationLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        _layout = layout;
        Devices = GetDevices(layout);
        _states = new BoxStationAirAlarmState[Devices.Length];
        _operational = new bool[Devices.Length];
        _areaDanger = new byte[layout.Areas.Length];
        for (int i = 0; i < _states.Length; i++)
            _states[i] = new(BoxStationAirMode.Filtering, 0, 0, false, 0, 0);
    }

    internal static ImmutableArray<BoxStationAirAlarmDevice> GetDevices(BoxStationLayout layout)
        => LayoutDefinitions.GetValue(layout, BuildDefinitions).Devices;

    internal static bool ValidMode(BoxStationAirMode mode)
        => mode is BoxStationAirMode.Filtering or BoxStationAirMode.Draught or BoxStationAirMode.Panic
            or BoxStationAirMode.Cycle or BoxStationAirMode.Off or BoxStationAirMode.Siphon
            or BoxStationAirMode.Contaminated or BoxStationAirMode.Refill;

    internal bool SetMode(int index, BoxStationAirMode mode, BoxStationPlumbing plumbing, BoxStationPower power)
    {
        if (index < 0 || index >= Devices.Length || !ValidMode(mode)
            || _hasStepped && !_operational[index]
            || !power.GetPowered(Devices[index].Area, BoxStationPowerChannel.Environment)) return false;
        _states[index] = _states[index] with { Mode = mode };
        // Peer selections are independent. Re-selecting a mode reissues its command.
        plumbing.ApplyAirMode(Devices[index].Area, mode, power);
        return true;
    }

    // Authority calls this before plumbing flow, inside the power step.
    internal void Step(double dt, BoxStationRooms rooms, BoxStationPlumbing plumbing, BoxStationPower power)
    {
        CheckRooms(rooms);
        if (!double.IsFinite(dt) || dt < 0 || dt > 1) throw new ArgumentOutOfRangeException(nameof(dt));
        if (dt == 0) return;
        _hasStepped = true;
        for (int i = 0; i < Devices.Length; i++)
        {
            var device = Devices[i];
            double requested = Watts * dt;
            bool powered = power.GetPowered(device.Area, BoxStationPowerChannel.Environment)
                && power.Draw(device.Area, BoxStationPowerChannel.Environment, requested) >= requested;
            _operational[i] = powered;
            int node = rooms.NodeAtCell(device.Cell);
            var gas = node >= 0 ? rooms.Gas(node) : StationGas.Vacuum(StationGas.SourceCellVolume);
            Sample(i, gas, powered);
            if (!powered || _states[i].Mode != BoxStationAirMode.Cycle
                || gas.Pressure >= CyclePressure) continue;
            _states[i] = _states[i] with { Mode = BoxStationAirMode.Filtering };
            plumbing.ApplyAirMode(device.Area, BoxStationAirMode.Filtering, power);
        }
        AggregateDanger();
    }

    // Publishing or restoring telemetry never spends energy or reapplies a mode.
    // After a step, availability alone cannot reactivate an alarm whose draw failed.
    internal void Refresh(BoxStationRooms rooms, BoxStationPower power)
    {
        CheckRooms(rooms);
        for (int i = 0; i < Devices.Length; i++)
        {
            var device = Devices[i];
            bool powered = (!_hasStepped || _operational[i])
                && power.GetPowered(device.Area, BoxStationPowerChannel.Environment);
            if (_hasStepped) _operational[i] = powered;
            int node = rooms.NodeAtCell(device.Cell);
            var gas = node >= 0 ? rooms.Gas(node) : StationGas.Vacuum(StationGas.SourceCellVolume);
            Sample(i, gas, powered);
        }
        AggregateDanger();
    }

    private void Sample(int index, in StationGas gas, bool powered)
    {
        ushort warning = 0, danger = 0;
        if (powered && !Devices[index].ThresholdsDisabled)
        {
            double temperature = gas.TemperatureKelvin;
            double partialPressure = StationGas.GasConstant * temperature / gas.Volume;
            Check(gas.Pressure, 20, 50, 325, 550, 1, ref warning, ref danger);
            Check(temperature, 273.15, 283.15, 313.15, 339.15, 2, ref warning, ref danger);
            // Oxygen is evaluated even when absent; a pure inert gas must not read safe.
            Check(gas.OxygenMoles * partialPressure, 16, 19, 135, 140, 4, ref warning, ref danger);
            Check(gas.NitrogenMoles * partialPressure, -1, -1, 1000, 1000, 8, ref warning, ref danger);
            Check(gas.CO2Moles * partialPressure, -1, -1, 5, 10, 16, ref warning, ref danger);
            Check(gas.PlasmaMoles * partialPressure, -1, -1, .2, .5, 32, ref warning, ref danger);
            Check(gas.N2OMoles * partialPressure, -1, -1, .2, .5, 64, ref warning, ref danger);
            Check(gas.TritiumMoles * partialPressure, -1, -1, .2, .5, 128, ref warning, ref danger);
            Check(gas.BZMoles * partialPressure, -1, -1, .2, .5, 256, ref warning, ref danger);
        }
        byte severity = danger != 0 ? (byte)2 : warning != 0 ? (byte)1 : (byte)0;
        _states[index] = new(_states[index].Mode, severity, 0, powered, warning, danger);
    }

    private static void Check(double value, double min2, double min1, double max1, double max2,
        ushort reading, ref ushort warning, ref ushort danger)
    {
        if (min2 >= 0 && value <= min2 || max2 >= 0 && value >= max2) danger |= reading;
        else if (min1 >= 0 && value <= min1 || max1 >= 0 && value >= max1) warning |= reading;
    }

    private void AggregateDanger()
    {
        Array.Clear(_areaDanger);
        for (int i = 0; i < Devices.Length; i++)
            if (_states[i].Powered)
                _areaDanger[Devices[i].Area] = Math.Max(_areaDanger[Devices[i].Area], _states[i].Danger);
        for (int i = 0; i < Devices.Length; i++)
            _states[i] = _states[i] with { AreaDanger = _areaDanger[Devices[i].Area] };
    }

    internal static bool ValidState(BoxStationLayout layout, ImmutableArray<BoxStationAirAlarmState> states)
    {
        var devices = GetDevices(layout);
        if (states.IsDefault || states.Length != devices.Length) return false;
        var areaDanger = new byte[layout.Areas.Length];
        for (int i = 0; i < states.Length; i++)
        {
            var state = states[i];
            byte danger = state.DangerMask != 0 ? (byte)2 : state.WarningMask != 0 ? (byte)1 : (byte)0;
            if (!ValidMode(state.Mode) || state.Danger != danger || state.AreaDanger > 2
                || ((state.WarningMask | state.DangerMask) & ~AllReadings) != 0
                || (state.WarningMask & state.DangerMask) != 0
                || (!state.Powered || devices[i].ThresholdsDisabled) && danger != 0) return false;
            if (state.Powered) areaDanger[devices[i].Area] = Math.Max(areaDanger[devices[i].Area], state.Danger);
        }
        for (int i = 0; i < states.Length; i++)
            if (states[i].AreaDanger != areaDanger[devices[i].Area]) return false;
        return true;
    }

    internal BoxStationAirAlarmsSave Capture()
    {
        var objects = new int[Devices.Length];
        var modes = new BoxStationAirMode[Devices.Length];
        for (int i = 0; i < Devices.Length; i++)
        {
            objects[i] = Devices[i].Object;
            modes[i] = _states[i].Mode;
        }
        return new() { LayoutHash = _layout.LayoutHash, DeviceObjects = objects, Modes = modes };
    }

    internal bool TryRestore(BoxStationAirAlarmsSave? saved)
    {
        if (saved is null || saved.Version != 1 || saved.LayoutHash != _layout.LayoutHash
            || saved.DeviceObjects?.Length != Devices.Length || saved.Modes?.Length != Devices.Length) return false;
        for (int i = 0; i < Devices.Length; i++)
            if (saved.DeviceObjects[i] != Devices[i].Object || !ValidMode(saved.Modes[i])) return false;
        for (int i = 0; i < Devices.Length; i++)
            _states[i] = new(saved.Modes[i], 0, 0, false, 0, 0);
        Array.Clear(_operational);
        Array.Clear(_areaDanger);
        _hasStepped = false;
        return true;
    }

    private static Definitions BuildDefinitions(BoxStationLayout layout)
    {
        const string alarmType = "/obj/machinery/airalarm";
        var devices = ImmutableArray.CreateBuilder<BoxStationAirAlarmDevice>();
        foreach (var item in layout.Objects)
        {
            string type = layout.Types[item.Type];
            if (type != alarmType && !type.StartsWith(alarmType + "/", StringComparison.Ordinal)) continue;
            if (type != alarmType && type != alarmType + "/server" && type != alarmType + "/engine"
                && type != alarmType + "/mixingchamber" && type != alarmType + "/all_access")
                throw new InvalidDataException($"Station air alarm {item.Id} has an unsupported subtype.");
            var cell = layout.Cells[item.Cell];
            int direction = item.Direction == 0 ? 2 : item.Direction;
            if (cell.Zone < 0 || direction is not (1 or 2 or 4 or 8))
                throw new InvalidDataException($"Station air alarm {item.Id} has an invalid compartment or direction.");
            devices.Add(new(item.Id, item.Cell, cell.Area, cell.Zone, direction, type == alarmType + "/server"));
        }
        return new() { Devices = devices.ToImmutable() };
    }

    private void CheckRooms(BoxStationRooms rooms)
    {
        ArgumentNullException.ThrowIfNull(rooms);
        if (!ReferenceEquals(rooms.Layout, _layout))
            throw new ArgumentException("Expected rooms for this station layout.", nameof(rooms));
    }
}

internal sealed class BoxStationAirAlarmsSave
{
    [JsonRequired]
    public int Version { get; set; } = 1;
    [JsonRequired]
    public string? LayoutHash { get; set; }
    [JsonRequired]
    public int[]? DeviceObjects { get; set; }
    [JsonRequired]
    public BoxStationAirMode[]? Modes { get; set; }
}
