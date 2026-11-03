// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Modstoke.Client;

internal readonly record struct OrbitalStationAtmosphere(
    float Pressure, float OxygenFraction, float CO2Fraction, float TemperatureC,
    float PlasmaFraction = 0, float N2OFraction = 0, float WaterVaporFraction = 0,
    float TritiumFraction = 0, float BZFraction = 0, float CombustionWattsPerCubicMeter = 0);

internal readonly record struct OrbitalStationState(
    ImmutableArray<OrbitalStationAtmosphere> Zones, ImmutableArray<float> DoorAmounts,
    ushort OpenMask, int CycleDoor, float BatteryKwh, float ReserveMoles, bool Powered);

internal sealed class OrbitalStationSystems
{
    internal const int ZoneCount = 10, DoorCount = 10;
    internal const float BatteryCapacityKwh = 96;
    private const ushort DockMask = (1 << 8) | (1 << 9), AllDoorsMask = (1 << DoorCount) - 1;
    private const double NormalPressure = 101.3, NormalOxygen = .209, NormalCO2 = .0004;
    private static readonly double MaximumReserveMoles = new SpacecraftAtmosphere(20, 2000, 22).Moles;
    private readonly SpacecraftAtmosphere[] _zones = new SpacecraftAtmosphere[ZoneCount];
    private readonly float[] _doors = new float[DoorCount];
    private SpacecraftAtmosphere _reserve = new(20, 2000, 22);
    // Double precision retains sub-frame energy debits at a full battery.
    private double _batteryKwh = BatteryCapacityKwh;
    private ushort _openMask;
    private int _cycleDoor = -1;

    internal OrbitalStationSystems()
    {
        for (int zone = 0; zone < ZoneCount; zone++)
            _zones[zone] = new SpacecraftAtmosphere(Volume(zone), NormalPressure, 22);
    }

    internal OrbitalStationState State
    {
        get
        {
            var zones = ImmutableArray.CreateBuilder<OrbitalStationAtmosphere>(ZoneCount);
            for (int zone = 0; zone < ZoneCount; zone++) zones.Add(GetAtmosphere(zone));
            return new OrbitalStationState(zones.MoveToImmutable(), ImmutableArray.CreateRange(_doors),
                _openMask, _cycleDoor, (float)_batteryKwh, (float)_reserve.Moles, (float)_batteryKwh > 0);
        }
    }

    internal OrbitalStationAtmosphere GetAtmosphere(int zone)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(zone);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(zone, ZoneCount);
        ref var air = ref _zones[zone];
        return new(air.Pressure, air.OxygenFraction, air.CO2Fraction, air.Temperature);
    }

    internal float DoorAmount(int door)
    {
        CheckDoor(door);
        return _doors[door];
    }

    internal bool DoorOpen(int door)
    {
        CheckDoor(door);
        return (_openMask & (1 << door)) != 0;
    }

    internal string CommandDoor(int door)
    {
        if (door is < 0 or >= DoorCount) return "Unknown station door.";
        if (_batteryKwh <= 0) return "Station battery depleted.";
        if (_cycleDoor == door)
        {
            _cycleDoor = -1;
            return "Pressure cycle cancelled.";
        }
        if (door >= 8)
        {
            if (DoorOpen(door))
            {
                _openMask &= unchecked((ushort)~DockMask);
                return "Dock door closing.";
            }
            if (_cycleDoor >= 0) return "Another pressure cycle is active.";
            _openMask &= unchecked((ushort)~DockMask);
            _cycleDoor = door;
            return door == 8 ? "Dock airlock cycling to station pressure." : "Dock airlock cycling to vacuum.";
        }
        DoorZones(door, out int first, out int second);
        if (!DoorOpen(door) && MathF.Abs(_zones[first].Pressure - _zones[second].Pressure) > 5)
        {
            if (_cycleDoor >= 0) return "Another pressure cycle is active.";
            _cycleDoor = door;
            return "Bulkhead sealing and equalizing pressure.";
        }
        _openMask ^= (ushort)(1 << door);
        return DoorOpen(door) ? "Bulkhead opening." : "Bulkhead closing.";
    }

    // Occupants are counts per zone; obstruction telemetry has one entry per door.
    internal void Step(float dt, ReadOnlySpan<int> occupants, ReadOnlySpan<bool> obstructed)
    {
        if (!float.IsFinite(dt) || dt < 0) throw new ArgumentOutOfRangeException(nameof(dt));
        if (occupants.Length != ZoneCount) throw new ArgumentException("Expected ten zone counts.", nameof(occupants));
        if (obstructed.Length != DoorCount) throw new ArgumentException("Expected ten door obstructions.", nameof(obstructed));
        int total = 0;
        foreach (int count in occupants)
        {
            if (count is < 0 or > 64) throw new ArgumentOutOfRangeException(nameof(occupants));
            total += count;
        }
        if (total > 64) throw new ArgumentOutOfRangeException(nameof(occupants));
        if (dt == 0) return;
        dt = MathF.Min(dt, .1f);

        for (int door = 0; door < DoorCount; door++)
        {
            if (!obstructed[door] || _doors[door] <= 0 || DoorOpen(door)) continue;
            _openMask |= (ushort)(1 << door);
            if (_cycleDoor == door || door >= 8 && _cycleDoor >= 8) _cycleDoor = -1;
            if (door >= 8)
            {
                _openMask &= unchecked((ushort)~(1 << (17 - door)));
            }
        }

        double controlSeconds = ConsumeEnergy(.2 * 1000 * dt) / 200;
        if (controlSeconds > 0)
        {
            for (int door = 0; door < DoorCount; door++)
            {
                float target = DoorOpen(door) ? 1 : 0;
                double distance = Math.Abs(target - _doors[door]);
                double movement = Math.Min(distance, .65 * controlSeconds);
                if (movement <= 0) continue;
                double requested = movement / .65 * 150;
                double consumed = ConsumeEnergy(requested);
                double paidMovement = movement * (consumed / requested);
                _doors[door] = consumed == requested && movement == distance ? target
                    : Math.Clamp((float)(_doors[door] + Math.Sign(target - _doors[door]) * paidMovement), 0, 1);
            }
            AdvanceCycle(controlSeconds);
        }

        for (int door = 0; door < 9; door++)
        {
            if (_doors[door] <= 0) continue;
            DoorZones(door, out int first, out int second);
            SpacecraftAtmosphere.Equalize(ref _zones[first], ref _zones[second],
                1 - Math.Exp(-.8 * _doors[door] * dt));
        }
        if (_doors[9] > 0) _zones[9].Vent(0, 22, 1 - Math.Exp(-.8 * _doors[9] * dt));
        for (int zone = 0; zone < ZoneCount; zone++)
        {
            _zones[zone].Breathe(SpacecraftAtmosphere.RestingOxygenMolesPerSecond * occupants[zone] * dt);
            // A lumped hull heat sink approximates slow heat loss; no exterior gas is admitted.
            double change = (-60 - _zones[zone].TemperatureKelvin + 273.15) * (1 - Math.Exp(-.00002 * dt));
            _zones[zone].ChangeTemperature(change);
        }
        if (controlSeconds > 0) LifeSupport(controlSeconds);
    }

    private void AdvanceCycle(double seconds)
    {
        // A bulkhead bypass only runs behind its sealed leaf; dock cycles require both leaves sealed.
        if (_cycleDoor < 0 || _doors[_cycleDoor] > 0
            || _cycleDoor >= 8 && (_doors[8] > 0 || _doors[9] > 0)) return;
        DoorZones(_cycleDoor, out int first, out int second);
        double difference = _cycleDoor == 9 ? _zones[9].Pressure : Math.Abs(_zones[first].Pressure - _zones[second].Pressure);
        if (difference > 3)
        {
            double pumpSeconds = ConsumeEnergy(2000 * seconds) / 2000;
            if (_cycleDoor != 9)
                SpacecraftAtmosphere.Equalize(ref _zones[first], ref _zones[second], 1 - Math.Exp(-.2 * pumpSeconds));
            else
                _zones[9].Vent(0, 22, 1 - Math.Exp(-.3 * pumpSeconds));
            difference = _cycleDoor == 9 ? _zones[9].Pressure : Math.Abs(_zones[first].Pressure - _zones[second].Pressure);
        }
        if (difference > 3) return;
        _openMask |= (ushort)(1 << _cycleDoor);
        _cycleDoor = -1;
    }

    private void LifeSupport(double seconds)
    {
        // Air handling is in the hub. Sealed bulkheads isolate the downstream volumes.
        ref var hub = ref _zones[0];
        if (hub.CO2Fraction > NormalCO2)
            hub.Scrub(.02 * ConsumeEnergy(500 * seconds) / 500, NormalCO2);
        double deficit = hub.MolesToPressure(NormalPressure, NormalOxygen, NormalCO2, _reserve.TemperatureKelvin);
        if (deficit > 0 && _reserve.Moles > 0)
        {
            double targetOxygen = NormalPressure * NormalOxygen * hub.Volume
                / (SpacecraftAtmosphere.GasConstant * hub.TemperatureKelvin);
            double oxygen = Math.Clamp((targetOxygen - hub.OxygenMoles) / deficit, 0, 1);
            double co2 = Math.Min(NormalCO2, 1 - oxygen);
            double supplied = Math.Min(hub.MolesToPressure(NormalPressure, oxygen, co2, _reserve.TemperatureKelvin),
                10 * ConsumeEnergy(1000 * seconds) / 1000);
            _reserve.SupplyTo(ref hub, supplied, oxygen, co2);
        }
        double heat = (22 - hub.TemperatureKelvin + 273.15) * hub.HeatCapacity;
        if (heat != 0 && hub.HeatCapacity > 0)
        {
            double supplied = ConsumeEnergy(Math.Min(Math.Abs(heat), 4000 * seconds));
            hub.ChangeTemperature(Math.Sign(heat) * supplied / hub.HeatCapacity);
        }
    }

    private double ConsumeEnergy(double joules)
    {
        double consumed = Math.Min(joules, _batteryKwh * 3600000);
        _batteryKwh = Math.Max(0, _batteryKwh - consumed / 3600000);
        return consumed;
    }

    internal SavedOrbitalStationSystems Capture()
    {
        var zones = new SavedSpacecraftAtmosphere[ZoneCount];
        for (int zone = 0; zone < ZoneCount; zone++) zones[zone] = _zones[zone].Capture();
        return new SavedOrbitalStationSystems
        {
            Zones = zones, Reserve = _reserve.Capture(), BatteryKwh = _batteryKwh,
            DoorAmounts = (float[])_doors.Clone(), OpenMask = _openMask, CycleDoor = _cycleDoor
        };
    }

    internal static bool TryRestore(SavedOrbitalStationSystems? saved, out OrbitalStationSystems systems)
    {
        systems = null!;
        if (saved == null || saved.Version != 1 || saved.Zones?.Length != ZoneCount
            || saved.DoorAmounts?.Length != DoorCount || !double.IsFinite(saved.BatteryKwh)
            || saved.BatteryKwh is < 0 or > BatteryCapacityKwh
            || !ValidDoors(saved.DoorAmounts, saved.OpenMask, saved.CycleDoor)) return false;
        var candidate = new OrbitalStationSystems();
        // Bound cold gas inventory too, so warming cannot exceed the pressure validation range.
        for (int zone = 0; zone < ZoneCount; zone++)
            if (!SpacecraftAtmosphere.TryRestore(saved.Zones[zone], Volume(zone), 250,
                250 * Volume(zone) / (SpacecraftAtmosphere.GasConstant * 373.15),
                out candidate._zones[zone])) return false;
        if (!SpacecraftAtmosphere.TryRestore(saved.Reserve, 20, 3000, MaximumReserveMoles,
            out candidate._reserve)) return false;
        candidate._batteryKwh = saved.BatteryKwh;
        saved.DoorAmounts.CopyTo(candidate._doors, 0);
        candidate._openMask = saved.OpenMask;
        candidate._cycleDoor = saved.CycleDoor;
        systems = candidate;
        return true;
    }

    internal static bool ValidState(OrbitalStationState state)
    {
        if (state.Zones.IsDefault || state.Zones.Length != ZoneCount
            || state.DoorAmounts.IsDefault || state.DoorAmounts.Length != DoorCount
            || !InRange(state.BatteryKwh, BatteryCapacityKwh)
            || !InRange(state.ReserveMoles, (float)MaximumReserveMoles)
            || state.Powered != (state.BatteryKwh > 0)
            || !ValidDoors(state.DoorAmounts.AsSpan(), state.OpenMask, state.CycleDoor)) return false;
        foreach (var zone in state.Zones)
        {
            if (!InRange(zone.Pressure, 250) || !InRange(zone.OxygenFraction, 1)
                || !InRange(zone.CO2Fraction, 1) || zone.OxygenFraction + zone.CO2Fraction > 1.000001f
                || !float.IsFinite(zone.TemperatureC) || zone.TemperatureC is < -100 or > 100) return false;
        }
        return true;
    }

    private static bool ValidDoors(ReadOnlySpan<float> doors, ushort openMask, int cycleDoor)
    {
        if (openMask > AllDoorsMask || cycleDoor is < -1 or >= DoorCount
            || (openMask & DockMask) == DockMask
            || cycleDoor >= 0 && (openMask & (1 << cycleDoor)) != 0
            || cycleDoor >= 8 && (openMask & DockMask) != 0) return false;
        foreach (float amount in doors)
            if (!InRange(amount, 1)) return false;
        return !(doors[8] > 0 && doors[9] > 0)
            && !((openMask & (1 << 8)) != 0 && doors[9] > 0)
            && !((openMask & (1 << 9)) != 0 && doors[8] > 0);
    }

    private static bool InRange(float value, float maximum)
        => float.IsFinite(value) && value >= 0 && value <= maximum;

    private static double Volume(int zone) => zone == 0 ? 489.6 : zone is >= 5 and <= 8 ? 476 : 65.28;

    private static void CheckDoor(int door)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(door);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(door, DoorCount);
    }

    private static void DoorZones(int door, out int first, out int second)
    {
        first = door < 4 ? 0 : door < 8 ? door - 3 : 5;
        second = door < 8 ? door + 1 : 9;
    }
}

internal sealed class SavedOrbitalStationSystems
{
    [JsonRequired]
    public int Version { get; set; } = 1;
    [JsonRequired]
    public SavedSpacecraftAtmosphere[]? Zones { get; set; }
    [JsonRequired]
    public SavedSpacecraftAtmosphere? Reserve { get; set; }
    [JsonRequired]
    public double BatteryKwh { get; set; }
    [JsonRequired]
    public float[]? DoorAmounts { get; set; }
    [JsonRequired]
    public ushort OpenMask { get; set; }
    [JsonRequired]
    public int CycleDoor { get; set; } = -1;
}
