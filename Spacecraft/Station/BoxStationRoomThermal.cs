// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Immutable;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Modstoke.Client;

internal enum BoxStationRoomThermalMode : byte
{
    Auto = 0,
    Heat = 1,
    Cool = 2
}

internal enum BoxStationRoomThermalControl : byte
{
    Power = 0,
    Mode = 1,
    TargetDown = 2,
    TargetUp = 3,
    Hatch = 4,
    Cell = 5,
    Charge = 6,
    RejectHeat = 7
}

internal readonly record struct BoxStationRoomThermalDevice(int Object, int Cell, int Area, int Zone, int Direction);

internal readonly record struct BoxStationRoomThermalState(
    BoxStationRoomThermalMode Mode, bool Enabled, bool HatchOpen, bool CellDocked, bool Charging, bool RejectHeat,
    float TargetKelvin, float CellJoules, float CoreKelvin, float RoomKelvin,
    float ElectricWatts, float ThermalWatts, float ChargeWatts, bool SinkLimited);

internal sealed class BoxStationRoomThermal
{
    internal const double CellCapacityJoules = 20_000_000, MaximumCoolingCoreKelvin = 353.15;
    internal const float RatedWatts = 20_000, ChargeRatedWatts = 500;
    internal const float MinimumTargetKelvin = 273.15f, MaximumTargetKelvin = 333.15f;
    private const double CoreCapacity = 100_000, MinimumKelvin = 2.7;
    private const double ClosedConductance = .2, OpenConductance = 60, ChargeEfficiency = .9;
    private const double CoolingEfficiency = .35, MaximumCop = 4, MinimumLiftKelvin = 20, ToleranceKelvin = 1;
    internal const float MaximumCoreKelvin = 1000;
    internal const float MaximumThermalWatts = (float)(RatedWatts + OpenConductance * StationGas.MaximumTemperature);
    private static readonly ConditionalWeakTable<BoxStationLayout, Definitions> LayoutDefinitions = new();
    private readonly BoxStationLayout _layout;
    private readonly BoxStationRoomThermalState[] _states;
    private readonly double[] _cellJoules, _coreKelvin;

    private sealed class Definitions
    {
        internal required ImmutableArray<BoxStationRoomThermalDevice> Devices { get; init; }
    }

    internal ImmutableArray<BoxStationRoomThermalDevice> Devices { get; }

    internal BoxStationRoomThermal(BoxStationLayout layout, bool seedSource)
    {
        ArgumentNullException.ThrowIfNull(layout);
        _layout = layout;
        Devices = GetDevices(layout);
        _states = new BoxStationRoomThermalState[Devices.Length];
        _cellJoules = new double[Devices.Length];
        _coreKelvin = new double[Devices.Length];
        for (int i = 0; i < Devices.Length; i++)
        {
            _cellJoules[i] = seedSource ? CellCapacityJoules : 0;
            _coreKelvin[i] = StationGas.ReferenceTemperature;
            _states[i] = new(BoxStationRoomThermalMode.Auto, false, false, false, false, false,
                (float)StationGas.ReferenceTemperature, (float)_cellJoules[i], (float)_coreKelvin[i], 0, 0, 0, 0, false);
        }
    }

    internal static ImmutableArray<BoxStationRoomThermalDevice> GetDevices(BoxStationLayout layout)
        => LayoutDefinitions.GetValue(layout, BuildDefinitions).Devices;

    internal ImmutableArray<BoxStationRoomThermalState> Snapshot(BoxStationRooms rooms)
    {
        CheckRooms(rooms);
        var states = ImmutableArray.CreateBuilder<BoxStationRoomThermalState>(Devices.Length);
        for (int i = 0; i < Devices.Length; i++)
        {
            int node = rooms.NodeAtCell(Devices[i].Cell);
            double temperature = node >= 0 && rooms.Gas(node).HeatCapacity > 0 ? rooms.Gas(node).TemperatureKelvin : 0;
            // Work describes the completed step; room gas may have changed since this device ran.
            states.Add(_states[i] with
            {
                CellJoules = (float)_cellJoules[i], CoreKelvin = (float)_coreKelvin[i],
                RoomKelvin = (float)temperature
            });
        }
        return states.MoveToImmutable();
    }

    // Authority calls this inside the station power step. Conditioning only draws from its own cell.
    internal void Step(double dt, BoxStationRooms rooms, BoxStationPower power)
    {
        CheckRooms(rooms);
        if (!double.IsFinite(dt) || dt < 0 || dt > 1) throw new ArgumentOutOfRangeException(nameof(dt));
        if (dt == 0) return;
        for (int i = 0; i < Devices.Length; i++)
        {
            ClearWork(i);
            double heat = ExchangeCore(i, dt * .5, rooms);
            if (_states[i].Charging) Charge(i, dt, power);
            if (_states[i].Enabled)
            {
                if ((float)_cellJoules[i] <= 0)
                    _states[i] = _states[i] with { Enabled = false };
                else
                    heat += ConditionRoom(i, dt, rooms);
            }
            heat += ExchangeCore(i, dt * .5, rooms);
            _states[i] = _states[i] with { ThermalWatts = (float)(heat / dt) };
        }
    }

    internal bool ApplyControl(int index, BoxStationRoomThermalControl control)
    {
        if (index < 0 || index >= Devices.Length || !ValidControl(control)) return false;
        var state = _states[index];
        switch (control)
        {
            case BoxStationRoomThermalControl.Power:
                if (!state.Enabled && (state.HatchOpen || state.CellDocked || state.Charging
                    || state.RejectHeat || (float)_cellJoules[index] <= 0)) return false;
                state = state with { Enabled = !state.Enabled };
                break;
            case BoxStationRoomThermalControl.Mode:
                if (!state.HatchOpen) return false;
                state = state with { Mode = (BoxStationRoomThermalMode)(((int)state.Mode + 1) % 3) };
                break;
            case BoxStationRoomThermalControl.TargetDown:
            case BoxStationRoomThermalControl.TargetUp:
                if (!state.HatchOpen) return false;
                float target = Math.Clamp(state.TargetKelvin
                    + (control == BoxStationRoomThermalControl.TargetUp ? 1 : -1), MinimumTargetKelvin, MaximumTargetKelvin);
                if (target == state.TargetKelvin) return false;
                state = state with { TargetKelvin = target };
                break;
            case BoxStationRoomThermalControl.Hatch:
                state = state with { HatchOpen = !state.HatchOpen, Enabled = false, Charging = false };
                break;
            case BoxStationRoomThermalControl.Cell:
                if (!state.HatchOpen || state.Charging) return false;
                state = state with { CellDocked = !state.CellDocked };
                break;
            case BoxStationRoomThermalControl.Charge:
                if (!state.HatchOpen || !state.CellDocked || state.Enabled) return false;
                state = state with { Charging = !state.Charging };
                break;
            case BoxStationRoomThermalControl.RejectHeat:
                state = state with { RejectHeat = !state.RejectHeat, Enabled = false };
                break;
        }
        _states[index] = state;
        ClearWork(index);
        return true;
    }

    internal static bool ValidControl(BoxStationRoomThermalControl control)
        => control is BoxStationRoomThermalControl.Power or BoxStationRoomThermalControl.Mode
            or BoxStationRoomThermalControl.TargetDown or BoxStationRoomThermalControl.TargetUp
            or BoxStationRoomThermalControl.Hatch or BoxStationRoomThermalControl.Cell
            or BoxStationRoomThermalControl.Charge or BoxStationRoomThermalControl.RejectHeat;

    private void ClearWork(int index)
        => _states[index] = _states[index] with { ElectricWatts = 0, ThermalWatts = 0, ChargeWatts = 0, SinkLimited = false };

    private double ConditionRoom(int index, double dt, BoxStationRooms rooms)
    {
        int node = rooms.NodeAtCell(Devices[index].Cell);
        if (node < 0) return 0;
        var gas = rooms.Gas(node);
        if (!WithinBounds(gas) || gas.HeatCapacity <= 0) return 0;
        var state = _states[index];
        double delta = state.TargetKelvin - gas.TemperatureKelvin;
        bool heating = delta > ToleranceKelvin && state.Mode != BoxStationRoomThermalMode.Cool;
        bool cooling = delta < -ToleranceKelvin && state.Mode != BoxStationRoomThermalMode.Heat;
        if (!heating && !cooling) return 0;
        double heat = Math.Min(RatedWatts * dt,
            Math.Abs(state.TargetKelvin * gas.HeatCapacity - gas.ThermalJoules) * (1 - 1e-12));
        double paid;
        double core = _coreKelvin[index];
        if (heating)
        {
            paid = Math.Min(heat, Math.Min(_cellJoules[index], HeatingHeadroom(gas)));
            heat = paid;
        }
        else
        {
            double headroom = Math.Max(0, (MaximumCoolingCoreKelvin - core) * CoreCapacity) * (1 - 1e-12);
            if (headroom <= 0)
            {
                _states[index] = state with { SinkLimited = true };
                return 0;
            }
            // Bound the COP across the proposed cold-side drop and core rise, including electrical work.
            double coldBound = Math.Max(state.TargetKelvin, gas.TemperatureKelvin - heat / gas.HeatCapacity);
            double hotBound = core + Math.Min(headroom, heat + RatedWatts * dt) / CoreCapacity;
            double cop = Math.Min(MaximumCop, CoolingEfficiency * coldBound / Math.Max(MinimumLiftKelvin, hotBound - coldBound));
            double requested = Math.Min(heat / cop, Math.Min(RatedWatts * dt, _cellJoules[index]));
            paid = Math.Min(requested, headroom / (cop + 1));
            _states[index] = state with { SinkLimited = paid < requested };
            heat = -paid * cop;
            core += (paid - heat) / CoreCapacity;
        }
        if (paid <= 0) return 0;
        var changed = gas;
        if (!changed.TryAddThermalEnergy(heat) || !WithinBounds(changed)
            || !double.IsFinite(core) || core < MinimumKelvin || core > MaximumCoreKelvin) return 0;
        // Commit the gas, retained heat and actual finite cell debit together.
        rooms.Gas(node) = changed;
        _coreKelvin[index] = core;
        _cellJoules[index] -= paid;
        _states[index] = _states[index] with
        {
            Enabled = (float)_cellJoules[index] > 0, ElectricWatts = (float)(paid / dt)
        };
        return heat;
    }

    private double ExchangeCore(int index, double dt, BoxStationRooms rooms)
    {
        int node = rooms.NodeAtCell(Devices[index].Cell);
        if (node < 0) return 0;
        var gas = rooms.Gas(node);
        double capacity = gas.HeatCapacity;
        if (capacity <= 0 || !WithinBounds(gas)) return 0;
        double before = _coreKelvin[index];
        double conductance = _states[index].RejectHeat ? OpenConductance : ClosedConductance;
        double inverseCapacity = 1 / capacity + 1 / CoreCapacity;
        double heat = (before - gas.TemperatureKelvin) / inverseCapacity
            * (1 - Math.Exp(-conductance * inverseCapacity * dt));
        double transferLimit = conductance * Math.Abs(before - gas.TemperatureKelvin) * dt;
        heat = Math.Clamp(heat, -transferLimit, transferLimit);
        if (heat > 0)
            heat = Math.Min(heat, Math.Min(HeatingHeadroom(gas), (before - MinimumKelvin) * CoreCapacity));
        else
            heat = -Math.Min(-heat, Math.Min(Math.Max(0, gas.ThermalJoules - MinimumKelvin * capacity) * (1 - 1e-12),
                (MaximumCoreKelvin - before) * CoreCapacity));
        double core = Math.Clamp(before - heat / CoreCapacity, MinimumKelvin, MaximumCoreKelvin);
        // Use the representable core change for both bodies without rounding past the transfer budget.
        double represented = (before - core) * CoreCapacity;
        if (heat > 0 && represented > heat) core = Math.BitIncrement(core);
        else if (heat < 0 && represented < heat) core = Math.BitDecrement(core);
        heat = (before - core) * CoreCapacity;
        var changed = gas;
        if (!changed.TryAddThermalEnergy(heat) || !WithinBounds(changed)) return 0;
        rooms.Gas(node) = changed;
        _coreKelvin[index] = core;
        return heat;
    }

    private void Charge(int index, double dt, BoxStationPower power)
    {
        double requested = Math.Min(ChargeRatedWatts * dt,
            Math.Min((CellCapacityJoules - _cellJoules[index]) / ChargeEfficiency,
                (MaximumCoreKelvin - _coreKelvin[index]) * CoreCapacity / (1 - ChargeEfficiency)) * (1 - 1e-12));
        if (requested <= 0) return;
        // The one physical cell stays in its service cradle. Losses remain in the finite core.
        double paid = power.Draw(Devices[index].Area, BoxStationPowerChannel.Equipment, requested);
        if (paid <= 0) return;
        _cellJoules[index] += paid * ChargeEfficiency;
        _coreKelvin[index] += paid * (1 - ChargeEfficiency) / CoreCapacity;
        _states[index] = _states[index] with { ChargeWatts = (float)(paid / dt) };
    }

    private static double HeatingHeadroom(in StationGas gas)
    {
        if (gas.HeatCapacity <= 0 || !WithinBounds(gas)) return 0;
        double limit = Math.Min(StationGas.MaximumTemperature,
            BoxStationSystems.MaximumPressure * gas.Volume / (gas.Moles * StationGas.GasConstant));
        return Math.Max(0, gas.HeatCapacity * limit - gas.ThermalJoules) * (1 - 1e-12);
    }

    private static bool WithinBounds(in StationGas gas)
        => double.IsFinite(gas.Pressure) && gas.Pressure >= 0 && gas.Pressure <= BoxStationSystems.MaximumPressure
            && gas.Moles <= gas.Volume * BoxStationSystems.MaximumMolesPerCubicMeter
            && double.IsFinite(gas.TemperatureKelvin) && gas.TemperatureKelvin > 0
            && gas.TemperatureKelvin <= StationGas.MaximumTemperature;

    internal static bool ValidState(BoxStationRoomThermalState state)
    {
        if (state.Mode is not (BoxStationRoomThermalMode.Auto or BoxStationRoomThermalMode.Heat or BoxStationRoomThermalMode.Cool)
            || !float.IsFinite(state.TargetKelvin) || state.TargetKelvin < MinimumTargetKelvin || state.TargetKelvin > MaximumTargetKelvin
            || !float.IsFinite(state.CellJoules) || state.CellJoules < 0 || state.CellJoules > CellCapacityJoules
            || !float.IsFinite(state.CoreKelvin) || state.CoreKelvin < (float)MinimumKelvin || state.CoreKelvin > MaximumCoreKelvin
            || !float.IsFinite(state.RoomKelvin) || state.RoomKelvin < 0 || state.RoomKelvin > StationGas.MaximumTemperature
            || !float.IsFinite(state.ElectricWatts) || state.ElectricWatts < 0 || state.ElectricWatts > RatedWatts
            || !float.IsFinite(state.ThermalWatts) || Math.Abs(state.ThermalWatts) > MaximumThermalWatts
            || !float.IsFinite(state.ChargeWatts) || state.ChargeWatts < 0 || state.ChargeWatts > ChargeRatedWatts) return false;
        if (state.Enabled && (state.HatchOpen || state.CellDocked || state.Charging || state.RejectHeat || state.CellJoules <= 0)
            || state.Charging && (!state.HatchOpen || !state.CellDocked || state.Enabled)
            || state.ChargeWatts > 0 && !state.Charging
            || state.ElectricWatts > 0 && (state.HatchOpen || state.CellDocked || state.Charging || state.RejectHeat)
            || state.SinkLimited && (state.Mode == BoxStationRoomThermalMode.Heat
                || state.HatchOpen || state.CellDocked || state.Charging || state.RejectHeat)) return false;
        return true;
    }

    internal static bool ValidState(BoxStationLayout layout, ImmutableArray<BoxStationRoomThermalState> states)
    {
        if (states.IsDefault || states.Length != GetDevices(layout).Length) return false;
        foreach (var state in states)
            if (!ValidState(state)) return false;
        return true;
    }

    internal SavedBoxStationRoomThermal Capture()
    {
        var saved = new SavedBoxStationRoomThermal
        {
            LayoutHash = _layout.LayoutHash, DeviceObjects = new int[Devices.Length],
            Modes = new BoxStationRoomThermalMode[Devices.Length], Enabled = new bool[Devices.Length],
            HatchOpen = new bool[Devices.Length], CellDocked = new bool[Devices.Length],
            Charging = new bool[Devices.Length], RejectHeat = new bool[Devices.Length], TargetKelvin = new float[Devices.Length],
            CellJoules = (double[])_cellJoules.Clone(), CoreKelvin = (double[])_coreKelvin.Clone()
        };
        for (int i = 0; i < Devices.Length; i++)
        {
            var state = _states[i];
            saved.DeviceObjects[i] = Devices[i].Object;
            saved.Modes[i] = state.Mode;
            saved.Enabled[i] = state.Enabled;
            saved.HatchOpen[i] = state.HatchOpen;
            saved.CellDocked[i] = state.CellDocked;
            saved.Charging[i] = state.Charging;
            saved.RejectHeat[i] = state.RejectHeat;
            saved.TargetKelvin[i] = state.TargetKelvin;
        }
        return saved;
    }

    internal bool TryRestore(SavedBoxStationRoomThermal? saved)
    {
        if (saved is null || saved.Version != 1 || saved.LayoutHash != _layout.LayoutHash
            || saved.DeviceObjects?.Length != Devices.Length || saved.Modes?.Length != Devices.Length
            || saved.Enabled?.Length != Devices.Length || saved.HatchOpen?.Length != Devices.Length
            || saved.CellDocked?.Length != Devices.Length || saved.Charging?.Length != Devices.Length
            || saved.RejectHeat?.Length != Devices.Length || saved.TargetKelvin?.Length != Devices.Length
            || saved.CellJoules?.Length != Devices.Length || saved.CoreKelvin?.Length != Devices.Length) return false;
        var restored = new BoxStationRoomThermalState[Devices.Length];
        for (int i = 0; i < Devices.Length; i++)
        {
            if (saved.DeviceObjects[i] != Devices[i].Object || !double.IsFinite(saved.CellJoules[i])
                || saved.CellJoules[i] < 0 || saved.CellJoules[i] > CellCapacityJoules
                || !double.IsFinite(saved.CoreKelvin[i]) || saved.CoreKelvin[i] < MinimumKelvin
                || saved.CoreKelvin[i] > MaximumCoreKelvin) return false;
            restored[i] = new(saved.Modes[i], saved.Enabled[i], saved.HatchOpen[i], saved.CellDocked[i],
                saved.Charging[i], saved.RejectHeat[i], saved.TargetKelvin[i], (float)saved.CellJoules[i],
                (float)saved.CoreKelvin[i], 0, 0, 0, 0, false);
            if (!ValidState(restored[i])) return false;
        }
        Array.Copy(restored, _states, Devices.Length);
        Array.Copy(saved.CellJoules, _cellJoules, Devices.Length);
        Array.Copy(saved.CoreKelvin, _coreKelvin, Devices.Length);
        return true;
    }

    private static Definitions BuildDefinitions(BoxStationLayout layout)
    {
        const string heaterType = "/obj/machinery/space_heater";
        var devices = ImmutableArray.CreateBuilder<BoxStationRoomThermalDevice>();
        foreach (var item in layout.Objects)
        {
            string type = layout.Types[item.Type];
            if (type != heaterType && !type.StartsWith(heaterType + "/", StringComparison.Ordinal)) continue;
            if (type != heaterType || item.Raw.Trim() != heaterType)
                throw new InvalidDataException($"Station room conditioner {item.Id} has an unsupported subtype or source override.");
            var cell = layout.Cells[item.Cell];
            int direction = item.Direction == 0 ? 2 : item.Direction;
            if (cell.Zone < 0 || direction is not (1 or 2 or 4 or 8))
                throw new InvalidDataException($"Station room conditioner {item.Id} has an invalid compartment or direction.");
            devices.Add(new(item.Id, item.Cell, cell.Area, cell.Zone, direction));
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

internal sealed class SavedBoxStationRoomThermal
{
    [JsonRequired]
    public int Version { get; set; } = 1;
    [JsonRequired]
    public string? LayoutHash { get; set; }
    [JsonRequired]
    public int[]? DeviceObjects { get; set; }
    [JsonRequired]
    public BoxStationRoomThermalMode[]? Modes { get; set; }
    [JsonRequired]
    public bool[]? Enabled { get; set; }
    [JsonRequired]
    public bool[]? HatchOpen { get; set; }
    [JsonRequired]
    public bool[]? CellDocked { get; set; }
    [JsonRequired]
    public bool[]? Charging { get; set; }
    [JsonRequired]
    public bool[]? RejectHeat { get; set; }
    [JsonRequired]
    public float[]? TargetKelvin { get; set; }
    [JsonRequired]
    public double[]? CellJoules { get; set; }
    [JsonRequired]
    public double[]? CoreKelvin { get; set; }
}
