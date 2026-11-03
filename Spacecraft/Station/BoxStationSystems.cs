// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Immutable;
using System.IO;
using System.Text.Json.Serialization;

namespace Modstoke.Client;

internal readonly record struct BoxStationDoorState(
    float Amount, bool TargetOpen, bool Cycling, bool Interlocked, bool Powered);

internal readonly record struct BoxStationState(string LayoutHash,
    ImmutableArray<OrbitalStationAtmosphere> Zones, ImmutableArray<BoxStationDoorState> Doors,
    float BatteryKwh, float ReserveMoles, bool Powered, ImmutableArray<BoxStationPowerState> Power,
    ImmutableArray<BoxStationPlumbingState> Plumbing, ImmutableArray<BoxStationAirAlarmState> Alarms,
    ImmutableArray<BoxStationRadiatorState> Radiators, ImmutableArray<BoxStationRoomThermalState> RoomThermal,
    ImmutableArray<BoxStationFireAlarmState> FireAlarms, ImmutableArray<BoxStationFireAreaState> FireAreas, long HullRevision,
    ImmutableArray<BoxStationRoomSpan> RoomSpans, ImmutableArray<BoxStationSmesState> Smes, BoxStationCableState Cables,
    long DoorRevision, ImmutableArray<int> BuiltDoors);

internal sealed partial class BoxStationSystems
{
    internal const float BatteryCapacityKwh = 96;
    internal const double MaximumPressure = 1e12, MaximumMolesPerCubicMeter = 1_000_000;
    internal const float MaximumCombustionDensity = 1e14f;
    private const double CellVolume = BoxStationRooms.CellVolume, ReserveVolume = 20;
    private const double MaximumTemperature = StationGas.MaximumTemperature, SafePressureDifference = 5;
    private readonly BoxStationRooms _rooms;
    private float[] _combustionDensity = Array.Empty<float>();
    private int[] _occupants = Array.Empty<int>();
    private float[] _doors;
    private bool[] _targets, _cycling;
    private int[] _partners;
    private GasLink[] _links;
    private int[][] _doorLinks;
    private StationGas _reserve = StationGas.Vacuum(ReserveVolume);
    private double _batteryKwh;

    internal BoxStationLayout Layout { get; }
    internal BoxStationPower Power { get; }
    internal BoxStationPlumbing Plumbing { get; }
    internal BoxStationAirAlarms Alarms { get; }
    internal BoxStationFireAlarms FireAlarms { get; }
    internal BoxStationFirefighting Firefighting { get; private set; }
    internal BoxStationRoomThermal RoomThermal { get; }
    internal BoxStationHull Hull { get; }
    internal BoxStationFoam Foam { get; private set; }
    internal BoxStationHullRepair Repair { get; private set; }
    internal BoxStationEngineering Engineering { get; private set; }
    internal int ZoneCount => _rooms.Count;
    internal int DoorCount => _doors.Length;
    internal double ReserveMoles => _reserve.Moles;

    internal BoxStationSystems(BoxStationLayout layout) : this(layout, true) { }

    private BoxStationSystems(BoxStationLayout layout, bool seedSource, BoxStationHull? hull = null,
        ImmutableArray<int> builtDoors = default, long doorRevision = 0)
    {
        ArgumentNullException.ThrowIfNull(layout);
        Layout = layout;
        _builtDoors = builtDoors.IsDefault ? ImmutableArray<int>.Empty : builtDoors;
        ArgumentOutOfRangeException.ThrowIfNegative(doorRevision);
        _doorRevision = doorRevision;
        DoorDefinitions = BoxStationDoors.Definitions(layout, _builtDoors);
        Power = new(layout, seedSource);
        Plumbing = new(layout, seedSource);
        Alarms = new(layout);
        FireAlarms = new(layout);
        Firefighting = new(layout, seedSource);
        RoomThermal = new(layout, seedSource);
        Hull = hull ?? new(layout);
        _rooms = new(layout, Hull, _builtDoors);
        Foam = new(layout, Hull, seedSource);
        Repair = new(layout, Hull, seedSource);
        Engineering = new(layout, Hull, Repair, seedSource, TryRestoreWall, TrySetDoorConstruction);
        _doors = new float[DoorDefinitions.Length];
        _targets = new bool[_doors.Length];
        _cycling = new bool[_doors.Length];
        _partners = new int[_doors.Length];
        Array.Fill(_partners, -1);
        (_links, _doorLinks) = BuildLinks(layout, DoorDefinitions);
        FindAirlockPartners();
        SynchronizeRooms();
        if (!seedSource) return;

        var mixes = new StationGas[layout.GasMixes.Length];
        for (int mix = 0; mix < mixes.Length; mix++)
            if (!StationGas.TryCreate(layout.GasMixes[mix], CellVolume, out mixes[mix])
                || mixes[mix].Pressure > MaximumPressure || mixes[mix].Moles > MaximumMolesPerCubicMeter * CellVolume
                || mixes[mix].TemperatureKelvin > MaximumTemperature)
                throw new InvalidDataException($"Station gas recipe {mix} is invalid.");
        foreach (var entry in layout.InitialGas)
        {
            int node = _rooms.NodeAtCell(entry.Cell);
            if (node < 0) continue;
            var gas = mixes[entry.Mix];
            gas.TransferTo(ref _rooms.Gas(node), gas.Moles);
        }
        for (int door = 0; door < DoorCount; door++)
        {
            _targets[door] = DoorDefinitions[door].InitiallyOpen;
            _doors[door] = _targets[door] ? 1 : 0;
        }
        if (!ValidInterlocks(_doors, _targets, _cycling))
            throw new InvalidDataException("Station airlocks have conflicting initial door states.");
        Alarms.Refresh(_rooms, Power);
        FireAlarms.Refresh(_rooms, Power);
    }

    internal BoxStationState State
    {
        get
        {
            SynchronizeRooms();
            Alarms.Refresh(_rooms, Power);
            FireAlarms.Refresh(_rooms, Power);
            var zones = ImmutableArray.CreateBuilder<OrbitalStationAtmosphere>(ZoneCount);
            var doors = ImmutableArray.CreateBuilder<BoxStationDoorState>(DoorCount);
            var power = Power.Snapshot;
            bool powered = false;
            foreach (var apc in power) powered |= apc.Available != 0;
            for (int zone = 0; zone < ZoneCount; zone++) zones.Add(NodeAtmosphere(zone));
            for (int door = 0; door < DoorCount; door++)
                doors.Add(new(_doors[door], _targets[door], _cycling[door], Interlocked(door), DoorPowered(door)));
            return new(Layout.LayoutHash, zones.MoveToImmutable(), doors.MoveToImmutable(),
                (float)_batteryKwh, (float)_reserve.Moles, powered, power, Plumbing.Snapshot(_rooms, Power), Alarms.Snapshot,
                Plumbing.RadiatorSnapshot(), RoomThermal.Snapshot(_rooms), FireAlarms.AlarmSnapshot, FireAlarms.AreaSnapshot,
                Hull.State.Revision, _rooms.Spans, Power.SmesSnapshot, Power.CableState, _doorRevision, _builtDoors);
        }
    }

    internal bool PulseInjector(int device, out string feedback)
    {
        SynchronizeRooms();
        return Plumbing.PulseInjector(device, _rooms, Power, out feedback);
    }

    internal OrbitalStationAtmosphere GetAtmosphere(int cell)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cell);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(cell, Layout.Cells.Length);
        SynchronizeRooms();
        int node = _rooms.NodeAtCell(cell);
        return node >= 0 ? NodeAtmosphere(node) : new(0, 0, 0, (float)(StationGas.ReferenceTemperature - 273.15));
    }

    private OrbitalStationAtmosphere NodeAtmosphere(int node)
    {
        ref var gas = ref _rooms.Gas(node);
        return new((float)gas.Pressure, (float)gas.OxygenFraction, (float)gas.CO2Fraction,
            (float)(gas.TemperatureKelvin - 273.15), (float)gas.PlasmaFraction, (float)gas.N2OFraction,
            (float)gas.WaterVaporFraction, (float)gas.TritiumFraction, (float)gas.BZFraction,
            _combustionDensity[node]);
    }

    internal float DoorAmount(int door)
    {
        CheckDoor(door);
        return _doors[door];
    }

    internal StationWaterSpray ApplyWaterSpray(int cell, double units)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cell);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(cell, Layout.Cells.Length);
        int node = _rooms.NodeAtCell(cell);
        return node >= 0 ? _rooms.Gas(node).ApplyWaterSpray(units) : default;
    }

    internal bool DoorOpen(int door)
    {
        CheckDoor(door);
        return _targets[door];
    }

    internal string CommandDoor(int door)
    {
        if (door < 0 || door >= DoorCount) return "Unknown station door.";
        if (!DoorPowered(door)) return "This area's environment circuit has no power.";
        if (_cycling[door])
        {
            _cycling[door] = false;
            return "Pressure cycle cancelled.";
        }
        if (_targets[door])
        {
            _targets[door] = false;
            return "Station door closing.";
        }
        string result = RequestDoorOpening(door);
        return FireAlarms.DoorLatched(door) ? result + " Fire alarm remains active; this release is temporary." : result;
    }

    private string RequestDoorOpening(int door)
    {
        int partner = _partners[door];
        if (partner >= 0)
        {
            _targets[partner] = _cycling[partner] = false;
            _cycling[door] = true;
            return "Airlock sealing and equalizing pressure.";
        }
        if (!PressureSafe(door))
        {
            _cycling[door] = true;
            return "Station door sealing and equalizing pressure.";
        }
        _targets[door] = true;
        return "Station door opening.";
    }

    private void ApplyFireDoorRequests()
    {
        for (int door = 0; door < Layout.Doors.Length; door++)
        {
            sbyte request = FireAlarms.TakeDoorRequest(door);
            if (request < 0)
            {
                // A fire closure must also seal the pressure-equalization bypass.
                _targets[door] = _cycling[door] = false;
            }
            else if (request > 0 && !_targets[door] && !_cycling[door] && DoorPowered(door))
                RequestDoorOpening(door);
        }
    }

    // Cell identities remain valid when construction changes room ownership during this step.
    internal void Step(float dt, ReadOnlySpan<int> occupants, ReadOnlySpan<bool> obstructed,
        BoxStationRadiation radiation = default, Action<BoxStationSystems, float>? serviceDockedShip = null)
    {
        if (!float.IsFinite(dt) || dt < 0) throw new ArgumentOutOfRangeException(nameof(dt));
        if (occupants.Length > 64) throw new ArgumentOutOfRangeException(nameof(occupants));
        if (obstructed.Length != DoorCount) throw new ArgumentException("Expected one obstruction per station door.", nameof(obstructed));
        foreach (int cell in occupants)
            if ((uint)cell >= (uint)Layout.Cells.Length) throw new ArgumentOutOfRangeException(nameof(occupants));
        if (dt == 0) return;
        dt = MathF.Min(dt, .1f);
        double reactionSeconds = Math.Min((double)dt, .1);
        Firefighting.Step(reactionSeconds);
        Foam.Step(dt);
        SynchronizeRooms();
        Array.Clear(_occupants);
        foreach (int cell in occupants)
        {
            int node = _rooms.NodeAtCell(cell);
            if (node >= 0) _occupants[node]++;
        }

        Power.BeginStep(dt, radiation.CollectorExposure);
        Alarms.Step(dt, _rooms, Plumbing, Power);
        FireAlarms.Step(reactionSeconds, _rooms, Power);
        ApplyFireDoorRequests();
        // Obstruction protection wins over automatic fire closure. The latched
        // area will request closure again after its normal reclose interval.
        for (int door = 0; door < DoorCount; door++)
        {
            if (!obstructed[door] || _doors[door] <= 0 || _targets[door]) continue;
            _cycling[door] = false;
            _targets[door] = true;
            int partner = _partners[door];
            if (partner < 0) continue;
            _targets[partner] = _cycling[partner] = false;
        }

        for (int door = 0; door < DoorCount; door++)
        {
            if (!DoorPowered(door) || _targets[door] && Interlocked(door)) continue;
            if (_targets[door] && _doors[door] == 0 && !PressureSafe(door))
            {
                _targets[door] = false;
                _cycling[door] = true;
            }
            float target = _targets[door] ? 1 : 0;
            double distance = Math.Abs(target - _doors[door]);
            double movement = Math.Min(distance, .65 * dt);
            if (movement == 0) continue;
            double requested = movement / .65 * 150;
            double consumed = Power.Draw(DoorArea(door), BoxStationPowerChannel.Environment, requested);
            _doors[door] = consumed == requested && movement == distance ? target
                : Math.Clamp((float)(_doors[door] + Math.Sign(target - _doors[door]) * movement * consumed / requested), 0, 1);
        }
        AdvanceCycles(dt);
        Plumbing.Step(reactionSeconds, _rooms, Power);
        Plumbing.StepRadiators(dt, radiation);
        RoomThermal.Step(dt, _rooms, Power);
        Repair.StepFabrication(dt, Power, _rooms);
        serviceDockedShip?.Invoke(this, dt);
        Power.EndStep();

        foreach (int index in _activeLinks)
        {
            var link = _links[index];
            double area = LinkOpenArea(link, -1);
            if (area > 0) Flow(link, area, dt);
        }
        for (int zone = 0; zone < ZoneCount; zone++)
        {
            ref var gas = ref _rooms.Gas(zone);
            if (_breachAreas[zone] > 0) gas.ExhaustToVacuum(_breachAreas[zone], dt);
            gas.Breathe(SpacecraftAtmosphere.RestingOxygenMolesPerSecond * _occupants[zone] * dt);
            var reaction = gas.React(reactionSeconds);
            _combustionDensity[zone] = (float)(reaction.CombustionJoules / (reactionSeconds * gas.Volume));
        }
        // Construction may change dense door indices; consume this frame's obstruction array first.
        Engineering.Step(dt);
    }

    private void AdvanceCycles(double dt)
    {
        for (int door = 0; door < DoorCount; door++)
        {
            if (!_cycling[door] || _doors[door] > 0 || Interlocked(door) || !DoorPowered(door)) continue;
            double seconds = Power.Draw(DoorArea(door), BoxStationPowerChannel.Environment, 5 * dt) / 5;
            if (seconds == 0) continue;
            // The powered bypass is a passive valve across this door's own edges.
            // Other closed doors still seal their shared edges, including stacked doors.
            foreach (int index in _doorLinks[door])
            {
                var link = _links[index];
                double area = LinkOpenArea(link, door);
                if (area > 0) Flow(link, area, seconds);
            }
            if (!PressureSafe(door)) continue;
            _cycling[door] = false;
            _targets[door] = true;
        }
    }

    private bool PressureSafe(int door)
    {
        foreach (int index in _doorLinks[door])
        {
            var link = _links[index];
            if (!LinkWillOpen(link, door)) continue;
            int firstNode = _rooms.NodeAtCell(link.First), secondNode = _rooms.NodeAtCell(link.Second);
            double first = firstNode >= 0 ? _rooms.Gas(firstNode).Pressure : 0;
            double second = secondNode >= 0 ? _rooms.Gas(secondNode).Pressure : 0;
            if (Math.Abs(first - second) > SafePressureDifference) return false;
        }
        return true;
    }

    private bool Interlocked(int door)
    {
        int partner = _partners[door];
        return partner >= 0 && (_doors[partner] > 0 || _targets[partner] || _cycling[partner]);
    }

    private int DoorArea(int door) => Layout.Cells[DoorDefinitions[door].Cell].Area;
    private bool DoorPowered(int door) => Power.GetPowered(DoorArea(door), BoxStationPowerChannel.Environment);


    internal static bool ValidState(BoxStationLayout layout, BoxStationState state)
    {
        if (state.LayoutHash != layout.LayoutHash || state.HullRevision < 0 || state.Zones.IsDefault || state.Doors.IsDefault
            || state.DoorRevision < 0 || !BoxStationDoors.ValidEdges(layout, state.BuiltDoors)
            || state.DoorRevision == 0 && state.BuiltDoors.Length != 0
            || state.Doors.Length != layout.Doors.Length + state.BuiltDoors.Length
            || !InRange(state.BatteryKwh, BatteryCapacityKwh)
            || !InRange(state.ReserveMoles, ReserveVolume * MaximumMolesPerCubicMeter)
            || !BoxStationRooms.ValidSpans(layout, state.RoomSpans, state.Zones.Length)) return false;
        var apcs = BoxStationPower.GetApcs(layout);
        if (state.Power.IsDefault || state.Power.Length != apcs.Length) return false;
        bool powered = false;
        for (int i = 0; i < apcs.Length; i++)
        {
            var apc = state.Power[i];
            if (!InRange(apc.ChargeKwh, (float)(apcs[i].CapacityJoules / 3_600_000))
                || apc.Enabled > 7 || (apc.Available & ~apc.Enabled) != 0) return false;
            powered |= apc.Available != 0;
        }
        if (powered != state.Powered || !BoxStationPower.ValidSmesState(layout, state.Smes, state.Cables)) return false;
        if (!BoxStationPlumbing.ValidState(layout, state.Plumbing)) return false;
        if (!BoxStationAirAlarms.ValidState(layout, state.Alarms)) return false;
        if (!BoxStationFireAlarms.ValidState(layout, state.FireAlarms, state.FireAreas)) return false;
        if (!BoxStationRoomThermal.ValidState(layout, state.RoomThermal)) return false;
        if (state.Radiators.IsDefault || state.Radiators.Length != BoxStationPlumbing.GetRadiators(layout).Length) return false;
        foreach (var radiator in state.Radiators)
            if (!BoxStationPlumbing.ValidRadiatorState(radiator)) return false;
        foreach (var gas in state.Zones)
            if (!ValidAtmosphere(gas)) return false;
        foreach (var door in state.Doors)
            if (!InRange(door.Amount, 1) || door.TargetOpen && door.Cycling) return false;
        return true;
    }

    private bool ValidInterlocks(ReadOnlySpan<float> amounts, ReadOnlySpan<bool> targets, ReadOnlySpan<bool> cycles)
    {
        for (int door = 0; door < DoorCount; door++)
        {
            int partner = _partners[door];
            if (partner < 0) continue;
            if (amounts[door] > 0 && amounts[partner] > 0 || targets[door] && targets[partner]
                || cycles[door] && cycles[partner] || targets[door] && amounts[partner] > 0
                || cycles[door] && targets[partner]) return false;
        }
        return true;
    }

    private static bool TryRestoreGas(double[]? values, double volume, out StationGas gas)
        => StationGas.TryRestore(values, volume, MaximumPressure, MaximumMolesPerCubicMeter * volume, out gas)
            && gas.TemperatureKelvin <= MaximumTemperature;

    private static bool InRange(float value, double maximum)
        => float.IsFinite(value) && value >= 0 && value <= maximum;

    private void CheckDoor(int door)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(door);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(door, DoorCount);
    }
}

