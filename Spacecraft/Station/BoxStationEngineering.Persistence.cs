// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Modstoke.Client;

internal sealed partial class BoxStationEngineering
{
    internal static bool ValidState(BoxStationLayout layout, BoxStationEngineeringState state)
    {
        var definitions = LayoutDefinitions.GetValue(layout, BuildDefinitions);
        if (state.LayoutHash != layout.LayoutHash || state.HullRevision < 0 || state.Tools.IsDefault
            || state.Tools.Length != definitions.Sources.Length || state.Maintenance.IsDefault
            || state.Maintenance.Length > definitions.Structures.Length || state.Work.IsDefault || state.Work.Length > MaximumWork
            || state.TankFuel.IsDefault || state.TankFuel.Length != definitions.FuelTanks.Length) return false;
        for (int i = 0; i < state.TankFuel.Length; i++)
            if (state.TankFuel[i] > definitions.FuelTanks[i].Capacity) return false;
        var holders = new HashSet<Guid>();
        var actors = new HashSet<ulong>();
        var storageSlots = new HashSet<(int Storage, byte Slot)>();
        var shipSlots = new HashSet<byte>();
        for (int i = 0; i < state.Tools.Length; i++)
        {
            var tool = state.Tools[i];
            var source = definitions.Sources[i];
            if (tool.Id != i || tool.Fuel > source.FuelCapacity || !float.IsFinite(tool.BurnSeconds)
                || tool.BurnSeconds is < 0 or >= FuelSeconds || tool.Fuel == 0 && tool.BurnSeconds != 0
                || tool.Lit && (source.Kind != BoxStationToolKind.Welder || tool.Fuel == 0)
                || tool.Place != BoxStationToolPlace.Stored && (tool.Storage != -1 || tool.Slot != 0)) return false;
            if (tool.ShipSlot != NoShipSlot
                && (tool.ShipSlot >= ShipSlotCount || tool.ShipSlot != (byte)source.Kind
                    || tool.Place is not (BoxStationToolPlace.ShipStored or BoxStationToolPlace.Held)
                    || !shipSlots.Add(tool.ShipSlot))) return false;
            if (tool.Place == BoxStationToolPlace.Source)
            {
                if (tool.Position != source.Position || tool.Fuel != source.FuelCapacity || tool.BurnSeconds != 0 || tool.Lit) return false;
            }
            else if (tool.Place == BoxStationToolPlace.Stored)
            {
                if (tool.Lit || !BoxStationStorage.ValidMembership(layout, tool.Storage, tool.Slot, tool.Position)
                    || !storageSlots.Add((tool.Storage, tool.Slot))) return false;
            }
            else if (tool.Place == BoxStationToolPlace.ShipStored)
            {
                if (tool.Lit || tool.ShipSlot == NoShipSlot || !BoxStationFirefighting.ValidDropPosition(layout, tool.Position)) return false;
            }
            else if (tool.Place is not (BoxStationToolPlace.Dropped or BoxStationToolPlace.Held)
                || !BoxStationFirefighting.ValidDropPosition(layout, tool.Position)) return false;
            if (tool.Place == BoxStationToolPlace.Held)
            {
                if (tool.Holder == Guid.Empty || !holders.Add(tool.Holder) || tool.Actor != 0 && !actors.Add(tool.Actor)) return false;
            }
            else if (tool.Holder != Guid.Empty || tool.Actor != 0) return false;
        }
        int previous = -1;
        foreach (var maintenance in state.Maintenance)
        {
            int id = maintenance.StructureId;
            if (id <= previous || id >= definitions.Indices.Length || definitions.Indices[id] < 0
                || maintenance.Integrity is 0 or > BoxStationHull.Capacity || !float.IsFinite(maintenance.HeatRemaining)) return false;
            var structure = definitions.Structures[definitions.Indices[id]];
            if (maintenance.Stage != 0 && (!Reinforced(layout, structure) || maintenance.Stage is < 4 or > 8 || !maintenance.Anchored)
                || (maintenance.Stage == 7 ? maintenance.HeatRemaining is <= 0 or > HeatSeconds : maintenance.HeatRemaining != 0)
                || maintenance == InitialMaintenance(layout, structure)) return false;
            previous = id;
        }
        holders.Clear();
        actors.Clear();
        var targets = new HashSet<int>();
        foreach (var work in state.Work)
        {
            var intent = work.Intent;
            if (work.Holder == Guid.Empty || !holders.Add(work.Holder) || !actors.Add(work.Actor)
                || !ValidIntent(layout, intent) || !ValidFeet(layout, work.Feet)
                || !float.IsFinite(work.Duration) || work.Duration is <= 0 or > 20 || !float.IsFinite(work.Elapsed)
                || work.Elapsed < 0 || work.Elapsed >= work.Duration || intent.Kind == BoxStationWorkTarget.Bundle
                || !targets.Add(intent.Target)) return false;
            if (MaterialWork(intent))
            {
                foreach (var tool in state.Tools)
                    if (tool.Place == BoxStationToolPlace.Held && (tool.Holder == work.Holder || work.Actor != 0 && tool.Actor == work.Actor)) return false;
                if (intent.Kind is BoxStationWorkTarget.WallConstruction or BoxStationWorkTarget.DoorConstruction) continue;
            }
            else
            {
                var held = state.Tools[intent.Tool];
                var source = definitions.Sources[intent.Tool];
                if (held.Place != BoxStationToolPlace.Held || held.Holder != work.Holder || held.Actor != work.Actor
                    || source.Kind == BoxStationToolKind.Welder && (!held.Lit || held.Fuel == 0)) return false;
                if (intent.Kind is BoxStationWorkTarget.Wall or BoxStationWorkTarget.DoorConstruction) continue;
            }
            if (!BoxStationHull.TryStructure(layout, intent.Target, out var structure)) return false;
            var maintenance = SnapshotMaintenance(layout, state, structure);
            if (intent.Kind == BoxStationWorkTarget.Construction)
            {
                if (structure.Kind != BoxStationStructureKind.Grille || work.Duration != 1) return false;
                if (MaintenanceIndex(state, structure.Id) >= 0) return false;
                continue;
            }
            var toolSource = definitions.Sources[intent.Tool];
            if (Resolve(layout, structure, maintenance, toolSource.Kind, intent.Mode, out float duration) == Operation.None
                || duration * toolSource.Speed != work.Duration) return false;
        }
        return true;
    }

    internal static bool ValidState(BoxStationLayout layout, BoxStationEngineeringState state, BoxStationHullState hull)
    {
        if (!ValidState(layout, state) || !BoxStationHull.ValidState(layout, hull) || state.HullRevision != hull.Revision) return false;
        foreach (var structure in BoxStationHull.GetStructures(layout))
        {
            int target = BoxStationHull.StructureTarget(layout, structure.Id);
            byte integrity = SnapshotIntegrity(hull, target, structure.InitialIntegrity);
            var maintenance = SnapshotMaintenance(layout, state, structure);
            if (integrity != 0 && maintenance.Integrity != integrity) return false;
            if (integrity == 0 && MaintenanceIndex(state, structure.Id) >= 0) return false;
        }
        foreach (var work in state.Work)
        {
            if (work.Intent.Kind is BoxStationWorkTarget.Wall or BoxStationWorkTarget.WallConstruction or BoxStationWorkTarget.DoorConstruction) continue;
            BoxStationHull.TryStructure(layout, work.Intent.Target, out var structure);
            byte integrity = SnapshotIntegrity(hull, work.Intent.Target, structure.InitialIntegrity);
            if (work.Intent.Kind == BoxStationWorkTarget.Construction ? integrity != 0 : integrity == 0) return false;
        }
        return true;
    }

    internal static bool ValidState(BoxStationLayout layout, BoxStationEngineeringState state, BoxStationHullState hull,
        BoxStationMaterialState materials)
    {
        if (!ValidState(layout, state, hull) || !BoxStationHullRepair.ValidMaterials(layout, materials, hull)) return false;
        foreach (var work in state.Work)
        {
            var intent = work.Intent;
            var material = default(BoxStationMaterialBundle);
            foreach (var bundle in materials.Bundles)
            {
                if (bundle.Place != BoxStationMaterialPlace.Held
                    || bundle.Holder != work.Holder && (work.Actor == 0 || bundle.Actor != work.Actor)) continue;
                if (bundle.Holder != work.Holder || bundle.Actor != work.Actor || material.Id != 0) return false;
                material = bundle;
            }
            if (MaterialWork(intent))
            {
                if (material.Id == 0) return false;
                if (intent.Kind == BoxStationWorkTarget.DoorConstruction)
                {
                    var door = BoxStationHullRepair.DoorAt(layout, materials.DoorConstruction, intent.Target);
                    if (!BoxStationHullRepair.ResolveDoorMaterial(layout, door, material.Material, out _, out int cost, out float duration)
                        || material.Count < cost || work.Duration != duration) return false;
                }
                else if (intent.Kind == BoxStationWorkTarget.WallConstruction)
                {
                    var construction = BoxStationHullRepair.ConstructionAt(layout, materials, intent.Target);
                    if (!BoxStationHullRepair.ResolveConstructionMaterial(layout, construction, material.Material, out _, out int cost, out float duration)
                        || material.Count < cost || work.Duration != duration) return false;
                }
                else
                {
                    BoxStationHull.TryStructure(layout, intent.Target, out var structure);
                    int cost = BoxStationHullRepair.MaterialCost(layout, intent.Target, 0);
                    if (cost == 0 || material.Material != BoxStationHullRepair.StructureMaterial(layout, structure) || material.Count < cost) return false;
                }
                continue;
            }
            if (material.Id != 0) return false;
            if (intent.Kind == BoxStationWorkTarget.DoorConstruction)
            {
                var door = BoxStationHullRepair.DoorAt(layout, materials.DoorConstruction, intent.Target);
                var doorTool = GetSources(layout)[intent.Tool];
                if (!BoxStationHullRepair.ResolveDoorTool(layout, door, doorTool.Kind, intent.Mode, out _, out float duration)
                    || work.Duration != duration * doorTool.Speed) return false;
                continue;
            }
            if (intent.Kind != BoxStationWorkTarget.Wall) continue;
            var current = BoxStationHullRepair.ConstructionAt(layout, materials, intent.Target);
            var source = GetSources(layout)[intent.Tool];
            if (!BoxStationHullRepair.ResolveConstruction(layout, current, source.Kind, intent.Mode, out _, out float seconds))
            {
                if (current.Stage != BoxStationFrameStage.Wall || current.Integrity is 0 or >= BoxStationHull.Capacity
                    || source.Kind != BoxStationToolKind.Welder || intent.Mode != BoxStationWorkMode.Repair) return false;
                seconds = WallRepairDuration(current.WallKind);
                if (seconds == 0) return false;
            }
            if (work.Duration != seconds * source.Speed) return false;
        }
        return true;
    }

    private static byte SnapshotIntegrity(BoxStationHullState hull, int target, byte initial)
    {
        int low = 0, high = hull.Panels.Length - 1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            var panel = hull.Panels[middle];
            if (panel.Target == target) return panel.Integrity;
            if (panel.Target < target) low = middle + 1;
            else high = middle - 1;
        }
        return initial;
    }

    private static BoxStationMaintenance SnapshotMaintenance(BoxStationLayout layout, BoxStationEngineeringState state, BoxStationStructure structure)
    {
        int index = MaintenanceIndex(state, structure.Id);
        return index >= 0 ? state.Maintenance[index] : InitialMaintenance(layout, structure);
    }

    private static int MaintenanceIndex(BoxStationEngineeringState state, int id)
    {
        int low = 0, high = state.Maintenance.Length - 1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            int current = state.Maintenance[middle].StructureId;
            if (current == id) return middle;
            if (current < id) low = middle + 1;
            else high = middle - 1;
        }
        return -1;
    }

    internal SavedBoxStationEngineering Capture()
    {
        var state = State;
        var saved = new SavedBoxStationEngineering
        {
            LayoutHash = state.LayoutHash, HullRevision = state.HullRevision,
            Tools = new SavedBoxStationTool[state.Tools.Length], Maintenance = state.Maintenance.AsSpan().ToArray(),
            TankObjects = new int[_tankFuel.Length], TankFuel = state.TankFuel.AsSpan().ToArray()
        };
        for (int i = 0; i < state.Tools.Length; i++)
        {
            var tool = state.Tools[i];
            // Only the saved copy releases custody and extinguishes welders; their burn phase survives.
            if (tool.Place == BoxStationToolPlace.Held)
                tool = tool with { Place = tool.ShipSlot == NoShipSlot ? BoxStationToolPlace.Dropped : BoxStationToolPlace.ShipStored };
            saved.Tools[i] = new()
            {
                Object = _definitions.Sources[i].Object, Id = i, Place = tool.Place,
                PositionX = tool.Position.X, PositionY = tool.Position.Y, PositionZ = tool.Position.Z,
                Fuel = tool.Fuel, BurnSeconds = tool.BurnSeconds, Storage = tool.Storage, Slot = tool.Slot, ShipSlot = tool.ShipSlot
            };
        }
        for (int i = 0; i < _tankFuel.Length; i++) saved.TankObjects[i] = _definitions.FuelTanks[i].Object;
        return saved;
    }

    internal bool TryRestore(SavedBoxStationEngineering? saved)
    {
        if (saved is null || saved.Version is < 1 or > 4 || saved.LayoutHash != _layout.LayoutHash || saved.Tools?.Length != _tools.Length
            || saved.Maintenance is null || saved.Maintenance.Length > _maintenance.Length) return false;
        if (saved.Version < 3 ? saved.TankObjectsSpecified || saved.TankFuelSpecified
            : saved.TankObjects?.Length != _tankFuel.Length || saved.TankFuel?.Length != _tankFuel.Length) return false;
        var tankFuel = ImmutableArray.CreateBuilder<ushort>(_tankFuel.Length);
        for (int i = 0; i < _tankFuel.Length; i++)
        {
            var tank = _definitions.FuelTanks[i];
            if (saved.Version >= 3 && saved.TankObjects![i] != tank.Object) return false;
            // These mapped tanks were not modeled in older saves; only migration creates their initial fuel.
            tankFuel.Add(saved.Version < 3 ? (ushort)tank.Capacity : saved.TankFuel![i]);
        }
        var tools = ImmutableArray.CreateBuilder<BoxStationTool>(_tools.Length);
        for (int i = 0; i < saved.Tools.Length; i++)
        {
            var tool = saved.Tools[i];
            if (tool is null || tool.Object != _definitions.Sources[i].Object || tool.Place == BoxStationToolPlace.Held) return false;
            if (saved.Version == 1
                ? tool.StorageSpecified || tool.SlotSpecified || tool.Place == BoxStationToolPlace.Stored
                : !tool.StorageSpecified || !tool.SlotSpecified || tool.Storage is null || tool.Slot is null) return false;
            if (saved.Version < 4 ? tool.ShipSlotSpecified || tool.Place == BoxStationToolPlace.ShipStored
                : !tool.ShipSlotSpecified || tool.ShipSlot is null) return false;
            tools.Add(new(tool.Id, tool.Place, new(tool.PositionX, tool.PositionY, tool.PositionZ), 0, Guid.Empty,
                tool.Fuel, false, tool.BurnSeconds, tool.Storage ?? -1, tool.Slot ?? 0, tool.ShipSlot ?? NoShipSlot));
        }
        var candidate = new BoxStationEngineeringState(_layout.LayoutHash, saved.HullRevision, tools.MoveToImmutable(),
            ImmutableArray.CreateRange(saved.Maintenance), ImmutableArray<BoxStationWork>.Empty, tankFuel.MoveToImmutable());
        if (!ValidState(_layout, candidate, _hull.State, _repair.Materials)) return false;
        candidate.Tools.CopyTo(_tools);
        candidate.TankFuel.CopyTo(_tankFuel);
        _heated.Clear();
        for (int i = 0; i < _maintenance.Length; i++)
        {
            var structure = _definitions.Structures[i];
            _maintenance[i] = _hull.StructureIntegrity(structure.Id) == 0 ? new(structure.Id, 0, false, 0, 0)
                : SnapshotMaintenance(_layout, candidate, structure);
            if (_maintenance[i].Stage == 7) _heated.Add(i);
        }
        _jobs.Clear();
        _hullRevision = candidate.HullRevision;
        _state = candidate;
        _dirty = false;
        return true;
    }
}

internal sealed class SavedBoxStationEngineering
{
    [JsonRequired]
    public int Version { get; set; } = 4;
    [JsonRequired]
    public string? LayoutHash { get; set; }
    [JsonRequired]
    public long HullRevision { get; set; }
    [JsonRequired]
    public SavedBoxStationTool[]? Tools { get; set; }
    [JsonRequired]
    public BoxStationMaintenance[]? Maintenance { get; set; }
    private int[]? _tankObjects;
    public int[]? TankObjects
    {
        get => _tankObjects;
        set { _tankObjects = value; TankObjectsSpecified = true; }
    }
    [JsonIgnore]
    internal bool TankObjectsSpecified { get; private set; }
    private ushort[]? _tankFuel;
    public ushort[]? TankFuel
    {
        get => _tankFuel;
        set { _tankFuel = value; TankFuelSpecified = true; }
    }
    [JsonIgnore]
    internal bool TankFuelSpecified { get; private set; }
}

internal sealed class SavedBoxStationTool
{
    [JsonRequired]
    public int Object { get; set; }
    [JsonRequired]
    public int Id { get; set; }
    [JsonRequired]
    public BoxStationToolPlace Place { get; set; }
    [JsonRequired]
    public float PositionX { get; set; }
    [JsonRequired]
    public float PositionY { get; set; }
    [JsonRequired]
    public float PositionZ { get; set; }
    [JsonRequired]
    public byte Fuel { get; set; }
    [JsonRequired]
    public float BurnSeconds { get; set; }
    private int? _storage;
    public int? Storage
    {
        get => _storage;
        set { _storage = value; StorageSpecified = true; }
    }
    [JsonIgnore]
    internal bool StorageSpecified { get; private set; }
    private byte? _slot;
    public byte? Slot
    {
        get => _slot;
        set { _slot = value; SlotSpecified = true; }
    }
    [JsonIgnore]
    internal bool SlotSpecified { get; private set; }
    private byte? _shipSlot;
    public byte? ShipSlot
    {
        get => _shipSlot;
        set { _shipSlot = value; ShipSlotSpecified = true; }
    }
    [JsonIgnore]
    internal bool ShipSlotSpecified { get; private set; }
}
