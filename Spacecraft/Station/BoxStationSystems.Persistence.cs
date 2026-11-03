// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Immutable;
using System.IO;
using System.Text.Json.Serialization;

namespace Modstoke.Client;

internal sealed partial class BoxStationSystems
{
    internal SavedBoxStationSystems Capture()
    {
        SynchronizeRooms();
        var portables = Portables;
        var materials = portables.Materials;
        if (!ValidPortables(Layout, portables)
            || !BoxStationHullRepair.ValidMaterials(Layout, materials, Hull.State, Power.CableState)
            || materials.DoorConstruction.Revision != _doorRevision
            || !BoxStationDoors.CompleteEdges(materials.DoorConstruction).AsSpan().SequenceEqual(_builtDoors.AsSpan())
            || !BoxStationHullRepair.ValidMaterialTransfers(Layout, Repair.State, materials))
            throw new InvalidDataException("Invalid station material transfers.");
        var gas = new double[ZoneCount][];
        var states = new byte[DoorCount];
        for (int zone = 0; zone < ZoneCount; zone++) gas[zone] = _rooms.Gas(zone).CaptureValues();
        for (int door = 0; door < DoorCount; door++)
            states[door] = (byte)((_targets[door] ? 1 : 0) | (_cycling[door] ? 2 : 0));
        return new()
        {
            LayoutHash = Layout.LayoutHash, Gas = gas, Reserve = _reserve.CaptureValues(),
            BatteryKwh = _batteryKwh, DoorAmounts = (float[])_doors.Clone(), DoorStates = states,
            DoorRevision = _doorRevision, BuiltDoors = _builtDoors.AsSpan().ToArray(),
            Power = Power.Capture(), Plumbing = Plumbing.Capture(), AirAlarms = Alarms.Capture(),
            RoomThermal = RoomThermal.Capture(), FireAlarms = FireAlarms.Capture(), Firefighting = Firefighting.Capture(),
            Hull = Hull.Capture(), Foam = Foam.Capture(), Repair = Repair.Capture(),
            Materials = Repair.CaptureMaterials(), Engineering = Engineering.Capture(), RoomSpans = _rooms.Spans.AsSpan().ToArray()
        };
    }

    internal static bool TryRestore(SavedBoxStationSystems? saved, out BoxStationSystems systems)
        => TryRestore(BoxStationLayout.Current, saved, out systems);

    internal static bool TryRestore(BoxStationLayout layout, SavedBoxStationSystems? saved, out BoxStationSystems systems)
    {
        systems = null!;
        if (saved == null || saved.Version is < 1 or > 33 || saved.LayoutHash != layout.LayoutHash
            || (saved.Version >= 27) != saved.DoorRevisionSpecified
            || (saved.Version >= 27) != saved.BuiltDoorsSpecified
            || saved.Version >= 27 && (!saved.DoorRevision.HasValue || saved.DoorRevision < 0
                || saved.BuiltDoors == null || saved.BuiltDoors.Length > BoxStationDoors.MaximumAssemblies
                || saved.DoorRevision == 0 && saved.BuiltDoors.Length != 0)
            || (saved.Version >= 2) != (saved.Power != null)
            || saved.Version >= 2 && saved.Power?.Version != (saved.Version >= 23 ? 4 : saved.Version >= 21 ? 3 : saved.Version >= 20 ? 2 : 1)
            || (saved.Version >= 3) != (saved.Plumbing != null)
            || (saved.Version >= 4) != (saved.AirAlarms != null)
            || (saved.Version >= 7) != (saved.RoomThermal != null)
            || (saved.Version >= 9) != (saved.FireAlarms != null)
            || (saved.Version >= 10) != (saved.Firefighting != null)
            || (saved.Version >= 11) != (saved.Hull != null)
            || (saved.Version >= 12) != (saved.Foam != null)
            || (saved.Version >= 13) != (saved.Repair != null)
            || (saved.Version is >= 14 and <= 23) != (saved.Cavities != null)
            || (saved.Version >= 24) != (saved.RoomSpans != null)
            || (saved.Version >= 16) != (saved.Materials != null)
            || (saved.Version >= 17) != (saved.Engineering != null)
            || saved.Version >= 10 && saved.Firefighting?.Version != (saved.Version >= 32 ? 3 : saved.Version >= 29 ? 2 : 1)
            || saved.Version >= 12 && saved.Foam?.Version != (saved.Version >= 29 ? 2 : 1)
            || saved.Version is >= 17 and < 30 && saved.Engineering?.Version != (saved.Version >= 29 ? 2 : 1)
            || saved.Version >= 13 && saved.Repair?.Version != (saved.Version >= 29 ? 3 : saved.Version >= 18 ? 2 : 1)
            || saved.Version is >= 16 and < 30 && saved.Materials?.Version != (saved.Version >= 28 ? 9 : saved.Version >= 27 ? 8 : saved.Version >= 26 ? 7 : saved.Version >= 25 ? 6 : saved.Version >= 22 ? 5 : saved.Version >= 21 ? 4 : saved.Version >= 19 ? 3 : saved.Version >= 18 ? 2 : 1)
            || saved.Version == 30 && !((saved.Engineering?.Version is 2 or 3 && saved.Materials?.Version == 10)
                || saved.Engineering?.Version == 3 && saved.Materials?.Version == 11)
            || saved.Version >= 31 && (saved.Engineering?.Version != 4 || saved.Materials?.Version != 11)
            || saved.Version >= 11 && saved.Hull?.Version != (saved.Version >= 26 ? 7 : saved.Version >= 25 ? 6 : saved.Version >= 16 ? 5 : saved.Version >= 15 ? 4 : saved.Version >= 14 ? 3 : saved.Version >= 12 ? 2 : 1)
            || saved.Version is >= 14 and <= 23 && saved.Cavities?.Version != (saved.Version >= 15 ? 2 : 1)
            || saved.Version >= 4 && saved.Plumbing?.Version != (saved.Version >= 33 ? 8 : saved.Version >= 8 ? 7 : Math.Min(saved.Version, 6))
            || saved.Gas == null || saved.Gas.Length > layout.Cells.Length
            || saved.Reserve?.Length != (saved.Version >= 8 ? 9 : 6)
            || saved.DoorAmounts?.Length != layout.Doors.Length + (saved.BuiltDoors?.Length ?? 0)
            || saved.DoorStates?.Length != saved.DoorAmounts.Length
            || !double.IsFinite(saved.BatteryKwh) || saved.BatteryKwh is < 0 or > BatteryCapacityKwh) return false;
        var builtDoors = saved.Version >= 27 ? ImmutableArray.CreateRange(saved.BuiltDoors!) : ImmutableArray<int>.Empty;
        if (!BoxStationDoors.ValidEdges(layout, builtDoors)) return false;
        var hull = new BoxStationHull(layout);
        if (saved.Version >= 11 && !hull.TryRestore(saved.Hull)) return false;
        var candidate = new BoxStationSystems(layout, false, hull, builtDoors, saved.DoorRevision ?? 0);
        if (saved.Version >= 24)
        {
            if (saved.RoomSpans!.Length > layout.Cells.Length
                || !candidate._rooms.TryRestoreGas(ImmutableArray.CreateRange(saved.RoomSpans), saved.Gas)) return false;
        }
        else if (!candidate.RestoreLegacyGas(saved)) return false;
        if (!TryRestoreGas(saved.Reserve, ReserveVolume, out candidate._reserve)) return false;
        for (int door = 0; door < candidate.DoorCount; door++)
        {
            byte state = saved.DoorStates[door];
            if (!InRange(saved.DoorAmounts[door], 1) || state > 2) return false;
            candidate._doors[door] = saved.DoorAmounts[door];
            candidate._targets[door] = (state & 1) != 0;
            candidate._cycling[door] = (state & 2) != 0;
        }
        if (!candidate.ValidInterlocks(candidate._doors, candidate._targets, candidate._cycling)) return false;
        if (saved.Version >= 2)
        {
            if (!candidate.Power.TryRestore(saved.Power)) return false;
            candidate._batteryKwh = saved.BatteryKwh;
        }
        else candidate._batteryKwh = candidate.Power.AllocateLegacyEnergy(saved.BatteryKwh * 3_600_000) / 3_600_000;
        // Earlier records never owned pipe or tank inventories. Their new volumes
        // stay empty rather than replenishing gas during migration.
        if (saved.Version >= 3 && !candidate.Plumbing.TryRestore(saved.Plumbing)) return false;
        if (saved.Version >= 4 && !candidate.Alarms.TryRestore(saved.AirAlarms)) return false;
        // Pre-conditioner records own no cell charge; their new cells remain empty.
        if (saved.Version >= 7 && !candidate.RoomThermal.TryRestore(saved.RoomThermal)) return false;
        if (saved.Version >= 9 && !candidate.FireAlarms.TryRestore(saved.FireAlarms)) return false;
        if (saved.Version >= 10)
        {
            if (!candidate.Firefighting.TryRestore(saved.Firefighting)) return false;
        }
        // These physical fixtures did not exist in earlier station saves. Their
        // first migration introduces source stock; subsequent saves retain every unit.
        else candidate.Firefighting = new(layout);
        if (saved.Version >= 12)
        {
            if (!candidate.Foam.TryRestore(saved.Foam)) return false;
        }
        else candidate.Foam = new(layout, candidate.Hull);
        if (saved.Version >= 13)
        {
            if (!candidate.Repair.TryRestore(saved.Repair)) return false;
        }
        else candidate.Repair = new(layout, candidate.Hull);
        if (saved.Version >= 16)
        {
            if (!candidate.Repair.TryRestoreMaterials(saved.Materials)) return false;
        }
        else candidate.Repair.SeedMigratedMaterials();
        var materials = candidate.Repair.Materials;
        if (!BoxStationHullRepair.ValidMaterials(layout, materials, candidate.Hull.State, candidate.Power.CableState)
            || materials.DoorConstruction.Revision != candidate._doorRevision
            || !BoxStationDoors.CompleteEdges(materials.DoorConstruction).AsSpan().SequenceEqual(builtDoors.AsSpan())
            || !BoxStationHullRepair.ValidMaterialTransfers(layout, candidate.Repair.State, materials)) return false;
        candidate.Engineering = new(layout, candidate.Hull, candidate.Repair, saved.Version < 17,
            candidate.TryRestoreWall, candidate.TrySetDoorConstruction);
        if (saved.Version >= 17 && !candidate.Engineering.TryRestore(saved.Engineering)) return false;
        if (!ValidPortables(layout, candidate.Portables)) return false;
        candidate.Alarms.Refresh(candidate._rooms, candidate.Power);
        candidate.FireAlarms.Refresh(candidate._rooms, candidate.Power);
        systems = candidate;
        return true;
    }

    internal static bool TryMigrate(BoxStationLayout layout, SavedOrbitalStationSystems? saved, out BoxStationSystems systems)
    {
        systems = null!;
        if (!OrbitalStationSystems.TryRestore(saved, out _)) return false;
        var candidate = new BoxStationSystems(layout, false);
        double volume = 0;
        for (int node = 0; node < candidate.ZoneCount; node++) volume += candidate._rooms.Gas(node).Volume;
        var quantities = new double[6];
        foreach (var gas in saved!.Zones!)
        {
            quantities[0] += gas.OxygenMoles;
            quantities[1] += gas.NitrogenMoles;
            quantities[2] += gas.CO2Moles;
            quantities[5] += gas.ThermalJoules;
        }
        // Legacy compartments have no map correspondence. Distribute their finite
        // inventory by new volume; changed heat capacities derive a new temperature
        // while every species and the saved sensible energy remain conserved.
        if (!TryRestoreGas(quantities, volume, out var inventory)) return false;
        double density = inventory.Moles / volume;
        for (int zone = 0; zone < candidate.ZoneCount; zone++)
            inventory.TransferTo(ref candidate._rooms.Gas(zone), zone == candidate.ZoneCount - 1
                ? inventory.Moles : density * candidate._rooms.Gas(zone).Volume);
        var reserve = saved.Reserve!;
        quantities[0] = reserve.OxygenMoles;
        quantities[1] = reserve.NitrogenMoles;
        quantities[2] = reserve.CO2Moles;
        quantities[5] = reserve.ThermalJoules;
        if (!TryRestoreGas(quantities, ReserveVolume, out candidate._reserve)) return false;
        candidate._batteryKwh = candidate.Power.AllocateLegacyEnergy(saved.BatteryKwh * 3_600_000) / 3_600_000;
        candidate.Firefighting = new(layout);
        candidate.Foam = new(layout, candidate.Hull);
        candidate.Repair = new(layout, candidate.Hull);
        candidate.Engineering = new(layout, candidate.Hull, candidate.Repair, true,
            candidate.TryRestoreWall, candidate.TrySetDoorConstruction);
        candidate.Alarms.Refresh(candidate._rooms, candidate.Power);
        candidate.FireAlarms.Refresh(candidate._rooms, candidate.Power);
        systems = candidate;
        return true;
    }

    internal static bool TryMigrate(SavedOrbitalStationSystems? saved, out BoxStationSystems systems)
        => TryMigrate(BoxStationLayout.Current, saved, out systems);
}

internal sealed class SavedBoxStationSystems
{
    [JsonRequired]
    public int Version { get; set; } = 33;
    [JsonRequired]
    public string? LayoutHash { get; set; }
    [JsonRequired]
    public double[][]? Gas { get; set; }
    [JsonRequired]
    public double[]? Reserve { get; set; }
    [JsonRequired]
    public double BatteryKwh { get; set; }
    [JsonRequired]
    public float[]? DoorAmounts { get; set; }
    [JsonRequired]
    public byte[]? DoorStates { get; set; }
    public BoxStationPowerSave? Power { get; set; }
    public BoxStationPlumbingSave? Plumbing { get; set; }
    public BoxStationAirAlarmsSave? AirAlarms { get; set; }
    public SavedBoxStationRoomThermal? RoomThermal { get; set; }
    public SavedBoxStationFireAlarms? FireAlarms { get; set; }
    public SavedBoxStationFirefighting? Firefighting { get; set; }
    public SavedBoxStationHull? Hull { get; set; }
    public SavedBoxStationFoam? Foam { get; set; }
    public SavedBoxStationRepair? Repair { get; set; }
    public SavedBoxStationMaterials? Materials { get; set; }
    public SavedBoxStationEngineering? Engineering { get; set; }
    public SavedBoxStationCavities? Cavities { get; set; }
    public BoxStationRoomSpan[]? RoomSpans { get; set; }
    private long? _doorRevision;
    public long? DoorRevision
    {
        get => _doorRevision;
        set { _doorRevision = value; DoorRevisionSpecified = true; }
    }
    internal bool DoorRevisionSpecified { get; private set; }
    private int[]? _builtDoors;
    public int[]? BuiltDoors
    {
        get => _builtDoors;
        set { _builtDoors = value; BuiltDoorsSpecified = true; }
    }
    internal bool BuiltDoorsSpecified { get; private set; }
}
