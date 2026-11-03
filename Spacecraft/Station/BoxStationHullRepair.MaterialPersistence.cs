// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Modstoke.Client;

internal sealed partial class BoxStationHullRepair
{
    internal static bool ValidMaterials(BoxStationLayout layout, BoxStationMaterialState state)
    {
        var definitions = LayoutMaterials.GetValue(layout, BuildMaterialDefinitions);
        if (state.LayoutHash != layout.LayoutHash || state.HullRevision < 0 || state.NextBundleId < 1
            || !BoundedMaterialBudget(state.Budget, definitions.Budget) || !BoundedMaterialBudget(state.Installed, state.Budget)
            || state.Stocks.IsDefault || state.Stocks.Length != definitions.Sources.Length
            || state.Pending.IsDefault || state.Pending.Length > definitions.Structures.Length
            || state.Bundles.IsDefault || state.Bundles.Length > MaxMaterialBundles
            || state.StorageOpen.IsDefault || state.StorageOpen.Length != BoxStationStorage.GetDefinitions(layout).Length
            || !ValidFabrication(layout, state.Fabrication)
            || !ValidDoorConstruction(layout, state.DoorConstruction)
            || state.DoorElectronicsBudget != definitions.DoorElectronicsBudget
            || state.CableRevision < 0 || state.CableBudget != definitions.CableBudget
            || state.CableInstalled < 0 || state.CableInstalled > definitions.CableSiteCount || state.CableInstalled > state.CableBudget
            || state.CableRevision == 0 && state.CableInstalled != definitions.CableCount) return false;
        long glass = state.Installed.Glass, plasma = state.Installed.PlasmaGlass, rods = state.Installed.Rods;
        long cable = state.CableInstalled + (long)state.Fabrication.Cable + 2L * state.Fabrication.ProducedCircuit;
        long lineSpares = state.Fabrication.LineStock + (long)state.Fabrication.ExportedLine;
        long circuitSpares = state.Fabrication.CircuitStock + (long)state.Fabrication.ExportedCircuit;
        long electronics = 0;
        foreach (var assembly in state.DoorConstruction.Assemblies)
        {
            if (assembly.Stage >= BoxStationDoorStage.Wired) cable++;
            if (assembly.Stage >= BoxStationDoorStage.Electronics) electronics++;
        }
        for (int i = 0; i < state.Stocks.Length; i++)
        {
            if (state.Stocks[i] > definitions.Sources[i].Count) return false;
            AccumulateMaterials(MaterialUnits(definitions.Sources[i].Material, state.Stocks[i]), ref glass, ref plasma, ref rods);
            if (definitions.Sources[i].Material == BoxStationMaterial.Cable) cable += state.Stocks[i];
            else if (definitions.Sources[i].Material == BoxStationMaterial.DoorElectronics) electronics += state.Stocks[i];
        }
        int previous = -1;
        foreach (var pending in state.Pending)
        {
            if (pending.StructureId <= previous || pending.StructureId >= definitions.Indices.Length
                || definitions.Indices[pending.StructureId] < 0
                || pending.Sheets == 0 && pending.Shards == 0 && pending.Rods == 0) return false;
            previous = pending.StructureId;
            var structure = definitions.Structures[definitions.Indices[pending.StructureId]];
            var material = StructureMaterial(layout, structure);
            if (structure.Kind == BoxStationStructureKind.Grille ? pending.Sheets != 0 || pending.Shards != 0
                : material is BoxStationMaterial.Glass or BoxStationMaterial.PlasmaGlass && pending.Rods != 0) return false;
            AccumulateMaterials(MaterialUnits(material, pending.Sheets), ref glass, ref plasma, ref rods);
            AccumulateMaterials(MaterialUnits(ShardMaterial(structure), pending.Shards), ref glass, ref plasma, ref rods);
            rods += pending.Rods;
        }
        previous = 0;
        var holders = new HashSet<Guid>();
        var actors = new HashSet<ulong>();
        var storageSlots = new HashSet<(int Storage, byte Slot)>();
        foreach (var bundle in state.Bundles)
        {
            if (bundle.Id <= previous || bundle.Id >= state.NextBundleId || bundle.Count is < 1 or > MaxMaterialBundleCount
                || bundle.Material > BoxStationMaterial.CircuitSpare
                || bundle.Material == BoxStationMaterial.FloorTiles && bundle.Count > MaxFloorTileBundleCount
                || bundle.Material == BoxStationMaterial.Cable && bundle.Count > MaxCableBundleCount
                || bundle.Material == BoxStationMaterial.LineSpare && bundle.Count > FabricationLineCapacity
                || bundle.Material == BoxStationMaterial.CircuitSpare && bundle.Count > FabricationCircuitCapacity
                || !ValidDropPosition(layout, bundle.Position)) return false;
            previous = bundle.Id;
            switch (bundle.Place)
            {
                case BoxStationMaterialPlace.Dropped:
                    if (bundle.Actor != 0 || bundle.Holder != Guid.Empty || bundle.Storage != -1 || bundle.Slot != 0) return false;
                    break;
                case BoxStationMaterialPlace.Held:
                    if (bundle.Holder == Guid.Empty || !holders.Add(bundle.Holder) || bundle.Actor != 0 && !actors.Add(bundle.Actor)
                        || bundle.Storage != -1 || bundle.Slot != 0) return false;
                    break;
                case BoxStationMaterialPlace.Stored:
                    if (bundle.Actor != 0 || bundle.Holder != Guid.Empty
                        || !BoxStationStorage.TryDefinition(layout, bundle.Storage, out var storage)
                        || bundle.Slot >= BoxStationStorage.Capacity(storage)
                        || bundle.Position != BoxStationStorage.Position(layout, storage)
                        || !storageSlots.Add((bundle.Storage, bundle.Slot))) return false;
                    break;
                default:
                    return false;
            }
            AccumulateMaterials(MaterialUnits(bundle.Material, bundle.Count), ref glass, ref plasma, ref rods);
            if (bundle.Material == BoxStationMaterial.Cable) cable += bundle.Count;
            else if (bundle.Material == BoxStationMaterial.DoorElectronics) electronics += bundle.Count;
            else if (bundle.Material == BoxStationMaterial.LineSpare) lineSpares += bundle.Count;
            else if (bundle.Material == BoxStationMaterial.CircuitSpare) circuitSpares += bundle.Count;
        }
        return glass == state.Budget.Glass && plasma == state.Budget.PlasmaGlass && rods == state.Budget.Rods
            && cable == state.CableBudget && electronics == state.DoorElectronicsBudget
            && lineSpares == state.Fabrication.ProducedLine && circuitSpares == state.Fabrication.ProducedCircuit
            && ValidConstructionMaterials(layout, state) && ValidDoorConstructionFootprints(layout, state);
    }

    internal static bool ValidMaterials(BoxStationLayout layout, BoxStationMaterialState state, BoxStationHullState hull)
        => BoxStationHull.ValidState(layout, hull) && state.HullRevision == hull.Revision
            && ValidMaterials(layout, state) && state.Installed == InstalledMaterials(layout, hull)
            && ValidConstructionStates(layout, state.Construction, hull) && ValidFloors(layout, state.Floors, hull);

    private static bool BoundedMaterialBudget(BoxStationMaterialBudget budget, BoxStationMaterialBudget maximum)
        => budget.Glass >= 0 && budget.Glass <= maximum.Glass && budget.PlasmaGlass >= 0
            && budget.PlasmaGlass <= maximum.PlasmaGlass && budget.Rods >= 0 && budget.Rods <= maximum.Rods;

    private static void AccumulateMaterials(BoxStationMaterialBudget units, ref long glass, ref long plasma, ref long rods)
    {
        glass += units.Glass;
        plasma += units.PlasmaGlass;
        rods += units.Rods;
    }

    private static BoxStationMaterialBudget InstalledMaterials(BoxStationLayout layout, BoxStationHullState hull)
    {
        var definitions = LayoutMaterials.GetValue(layout, BuildMaterialDefinitions);
        var installed = new BoxStationMaterialBudget();
        int panel = 0;
        foreach (var structure in definitions.Structures)
        {
            int target = layout.Cells.Length * 3 + structure.Id;
            while (panel < hull.Panels.Length && hull.Panels[panel].Target < target) panel++;
            byte integrity = panel < hull.Panels.Length && hull.Panels[panel].Target == target
                ? hull.Panels[panel].Integrity : structure.InitialIntegrity;
            installed = AddMaterialUnits(installed, MaterialUnits(StructureMaterial(layout, structure), StructureUnits(structure, integrity)));
        }
        return installed;
    }

    internal SavedBoxStationMaterials CaptureMaterials()
    {
        var state = Materials;
        var saved = new SavedBoxStationMaterials
        {
            LayoutHash = state.LayoutHash, HullRevision = state.HullRevision, Budget = state.Budget, Installed = state.Installed,
            NextBundleId = state.NextBundleId, SourceObjects = new int[_materialStocks.Length], Stocks = state.Stocks.AsSpan().ToArray(),
            Pending = state.Pending.AsSpan().ToArray(), Bundles = new SavedBoxStationMaterialBundle[state.Bundles.Length],
            Construction = new() { Budget = state.ConstructionBudget, Installed = state.ConstructionInstalled,
                Waste = state.ConstructionWaste, ImportedSteel = state.ImportedSteel, Rows = state.Construction.AsSpan().ToArray(),
                ConsumedFloorTiles = state.ConsumedFloorTiles },
            CableRevision = state.CableRevision, CableBudget = state.CableBudget, CableInstalled = state.CableInstalled,
            DoorConstruction = state.DoorConstruction, DoorElectronicsBudget = state.DoorElectronicsBudget,
            StorageOpen = state.StorageOpen.AsSpan().ToArray(), Fabrication = state.Fabrication, ExportedSteel = state.ExportedSteel,
            Floors = new() { Revision = state.Floors.Revision, LostTiles = state.Floors.LostTiles, Removed = state.Floors.Removed.AsSpan().ToArray() }
        };
        for (int i = 0; i < saved.SourceObjects.Length; i++) saved.SourceObjects[i] = _materialDefinitions.Sources[i].Object;
        for (int i = 0; i < saved.Bundles.Length; i++)
        {
            var bundle = state.Bundles[i];
            // Only session custody is released; stored bundles keep their exact container and slot.
            saved.Bundles[i] = new()
            {
                Id = bundle.Id, Material = bundle.Material, Count = bundle.Count,
                Place = bundle.Place == BoxStationMaterialPlace.Held ? BoxStationMaterialPlace.Dropped : bundle.Place,
                PositionX = bundle.Position.X, PositionY = bundle.Position.Y, PositionZ = bundle.Position.Z,
                Storage = bundle.Place == BoxStationMaterialPlace.Stored ? bundle.Storage : -1,
                Slot = bundle.Place == BoxStationMaterialPlace.Stored ? bundle.Slot : (byte)0
            };
        }
        return saved;
    }

    internal bool TryRestoreMaterials(SavedBoxStationMaterials? saved)
    {
        if (saved is null || saved.Version is < 1 or > 11 || saved.LayoutHash != _layout.LayoutHash
            || saved.Pending is null || saved.Pending.Length > _materialPending.Length
            || saved.Bundles is null || saved.Bundles.Length > MaxMaterialBundles) return false;
        int storageCount = BoxStationStorage.GetDefinitions(_layout).Length;
        if (saved.Version < 11 ? saved.ExportedSteelSpecified : !saved.ExportedSteel.HasValue) return false;
        if (saved.Version < 10 ? saved.FabricationSpecified : !saved.Fabrication.HasValue) return false;
        if (saved.Version < 9 ? saved.StorageOpenSpecified : saved.StorageOpen?.Length != storageCount) return false;
        var storageOpen = ImmutableArray.CreateRange(saved.StorageOpen ?? new bool[storageCount]);
        int sourceCount = saved.Version == 1 ? _materialDefinitions.LegacySourceCount
            : saved.Version < 4 ? _materialDefinitions.ConstructionSourceCount
            : saved.Version < 8 ? _materialDefinitions.CableSourceCount : _materialStocks.Length;
        if (saved.Version < 8 ? saved.DoorConstructionSpecified || saved.DoorElectronicsBudgetSpecified
            : !saved.DoorConstruction.HasValue || !saved.DoorElectronicsBudget.HasValue) return false;
        var doors = saved.DoorConstruction ?? new BoxStationDoorConstructionState(0, ImmutableArray<BoxStationDoorAssembly>.Empty);
        if (saved.Version < 4
            ? saved.CableRevisionSpecified || saved.CableBudgetSpecified || saved.CableInstalledSpecified
            : !saved.CableRevision.HasValue || !saved.CableBudget.HasValue || !saved.CableInstalled.HasValue) return false;
        if (saved.Version < 5 ? saved.FloorsSpecified
            : saved.Floors?.Removed is null || saved.Floors.Removed.Length > GetFloorCells(_layout).Length) return false;
        var floors = saved.Version < 5 ? MigrateFloors(_layout, _hull.State)
            : new BoxStationFloorState(saved.Floors!.Revision, saved.Floors.LostTiles, ImmutableArray.CreateRange(saved.Floors.Removed!));
        if (saved.SourceObjects?.Length != sourceCount || saved.Stocks?.Length != sourceCount
            || (saved.Version == 1 ? saved.Construction is not null : saved.Construction?.Rows is null)) return false;
        var construction = saved.Version == 1 ? MigrateConstruction(_layout, _hull.State) : saved.Construction!;
        if (construction.Rows is null || construction.Rows.Length > _construction.Length
            || (saved.Version < 3 ? construction.ConsumedFloorTilesSpecified : !construction.ConsumedFloorTiles.HasValue)) return false;
        // Older saves could alter original frames, but could not build walls at new sites.
        if (saved.Version <= 5)
            foreach (var value in construction.Rows)
            {
                if (value.Cell < 0 || value.Cell >= _constructionDefinitions.Indices.Length) return false;
                int index = _constructionDefinitions.Indices[value.Cell];
                if (index < 0) return false;
                var definition = _constructionDefinitions.Sources[index];
                if (!definition.OriginalWall && (!definition.OriginalGirder || value.Stage == BoxStationFrameStage.Wall)) return false;
            }
        var constructionRows = ImmutableArray.CreateBuilder<BoxStationConstruction>(construction.Rows.Length);
        foreach (var value in construction.Rows)
        {
            var candidateRow = value;
            // Version 1 is already migrated from the restored hull by MigrateConstruction.
            if (saved.Version is >= 2 and <= 6)
            {
                if (value.WallKind != BoxStationWallKind.Unspecified
                    || value.Cell < 0 || value.Cell >= _constructionDefinitions.Indices.Length) return false;
                int index = _constructionDefinitions.Indices[value.Cell];
                if (index < 0) return false;
                var definition = _constructionDefinitions.Sources[index];
                candidateRow = value with { WallKind = value.Stage != BoxStationFrameStage.Wall ? BoxStationWallKind.None
                    : definition.Reinforced ? BoxStationWallKind.Reinforced : BoxStationWallKind.Steel };
            }
            constructionRows.Add(candidateRow);
        }
        for (int i = 0; i < saved.SourceObjects.Length; i++)
            if (saved.SourceObjects[i] != _materialDefinitions.Sources[i].Object) return false;
        var bundles = ImmutableArray.CreateBuilder<BoxStationMaterialBundle>(saved.Bundles.Length);
        foreach (var bundle in saved.Bundles)
        {
            if (bundle is null || bundle.Place is not (BoxStationMaterialPlace.Dropped or BoxStationMaterialPlace.Stored)
                || (saved.Version < 9 ? bundle.Place == BoxStationMaterialPlace.Stored || bundle.StorageSpecified || bundle.SlotSpecified
                    : !bundle.Storage.HasValue || !bundle.Slot.HasValue)
                || saved.Version == 1 && bundle.Material > BoxStationMaterial.PlasmaGlassShards
                || saved.Version == 2 && bundle.Material > BoxStationMaterial.Plasteel
                || saved.Version == 3 && bundle.Material > BoxStationMaterial.FloorTiles
                || saved.Version < 8 && bundle.Material > BoxStationMaterial.Cable
                || saved.Version < 10 && bundle.Material > BoxStationMaterial.DoorElectronics) return false;
            bundles.Add(new(bundle.Id, bundle.Material, bundle.Count, bundle.Place,
                new(bundle.PositionX, bundle.PositionY, bundle.PositionZ), 0, Guid.Empty, bundle.Storage ?? -1, bundle.Slot ?? 0));
        }
        var stocks = ImmutableArray.CreateBuilder<ushort>(_materialStocks.Length);
        stocks.AddRange(saved.Stocks);
        for (int i = sourceCount; i < _materialStocks.Length; i++) stocks.Add(_materialDefinitions.Sources[i].Count);
        var candidate = new BoxStationMaterialState(_layout.LayoutHash, saved.HullRevision, saved.Budget, saved.Installed,
            saved.NextBundleId, stocks.MoveToImmutable(), ImmutableArray.CreateRange(saved.Pending), bundles.MoveToImmutable(),
            construction.Budget, construction.Installed, construction.Waste, construction.ImportedSteel,
            constructionRows.MoveToImmutable(), construction.ConsumedFloorTiles ?? 0,
            saved.CableRevision ?? 0, saved.CableBudget ?? _materialDefinitions.CableBudget,
            saved.CableInstalled ?? _materialDefinitions.CableCount, floors, doors,
            saved.DoorElectronicsBudget ?? _materialDefinitions.DoorElectronicsBudget, storageOpen,
            saved.Fabrication ?? EmptyFabrication, saved.ExportedSteel ?? 0);
        if (!ValidMaterials(_layout, candidate, _hull.State)) return false;
        candidate.Stocks.CopyTo(_materialStocks);
        for (int i = 0; i < _materialPending.Length; i++)
            _materialPending[i] = new(_materialDefinitions.Structures[i].Id, 0, 0, 0);
        foreach (var pending in candidate.Pending) _materialPending[_materialDefinitions.Indices[pending.StructureId]] = pending;
        Array.Clear(_materialBundles);
        candidate.Bundles.CopyTo(_materialBundles);
        _materialBudget = candidate.Budget;
        for (int i = 0; i < _construction.Length; i++)
            _construction[i] = InitialConstruction(_constructionDefinitions.Sources[i]);
        foreach (var value in candidate.Construction) _construction[_constructionDefinitions.Indices[value.Cell]] = value;
        _constructionBudget = candidate.ConstructionBudget;
        _constructionWaste = candidate.ConstructionWaste;
        _importedSteel = candidate.ImportedSteel;
        _consumedFloorTiles = candidate.ConsumedFloorTiles;
        _cableRevision = candidate.CableRevision;
        _cableBudget = candidate.CableBudget;
        _cableInstalled = candidate.CableInstalled;
        _doorConstruction = candidate.DoorConstruction;
        _doorElectronicsBudget = candidate.DoorElectronicsBudget;
        _storageOpen = candidate.StorageOpen;
        _fabrication = candidate.Fabrication;
        _shipExportedSteel = candidate.ExportedSteel;
        _floors = candidate.Floors;
        _floorHullRevision = candidate.HullRevision;
        _constructionHullRevision = candidate.HullRevision;
        _nextMaterialBundleId = candidate.NextBundleId;
        _materials = candidate;
        _materialsDirty = false;
        _materialsInitialized = true;
        return true;
    }
}

internal sealed class SavedBoxStationMaterials
{
    [JsonRequired]
    public int Version { get; set; } = 11;
    [JsonRequired]
    public string? LayoutHash { get; set; }
    [JsonRequired]
    public long HullRevision { get; set; }
    [JsonRequired]
    public BoxStationMaterialBudget Budget { get; set; }
    [JsonRequired]
    public BoxStationMaterialBudget Installed { get; set; }
    [JsonRequired]
    public int NextBundleId { get; set; }
    [JsonRequired]
    public int[]? SourceObjects { get; set; }
    [JsonRequired]
    public ushort[]? Stocks { get; set; }
    [JsonRequired]
    public BoxStationPendingYield[]? Pending { get; set; }
    [JsonRequired]
    public SavedBoxStationMaterialBundle[]? Bundles { get; set; }
    public SavedBoxStationConstruction? Construction { get; set; }
    private long? _cableRevision;
    public long? CableRevision
    {
        get => _cableRevision;
        set { _cableRevision = value; CableRevisionSpecified = true; }
    }
    internal bool CableRevisionSpecified { get; private set; }
    private int? _cableBudget;
    public int? CableBudget
    {
        get => _cableBudget;
        set { _cableBudget = value; CableBudgetSpecified = true; }
    }
    internal bool CableBudgetSpecified { get; private set; }
    private int? _cableInstalled;
    public int? CableInstalled
    {
        get => _cableInstalled;
        set { _cableInstalled = value; CableInstalledSpecified = true; }
    }
    internal bool CableInstalledSpecified { get; private set; }
    private SavedBoxStationFloors? _floors;
    public SavedBoxStationFloors? Floors
    {
        get => _floors;
        set { _floors = value; FloorsSpecified = true; }
    }
    internal bool FloorsSpecified { get; private set; }
    private BoxStationDoorConstructionState? _doorConstruction;
    public BoxStationDoorConstructionState? DoorConstruction
    {
        get => _doorConstruction;
        set { _doorConstruction = value; DoorConstructionSpecified = true; }
    }
    internal bool DoorConstructionSpecified { get; private set; }
    private int? _doorElectronicsBudget;
    public int? DoorElectronicsBudget
    {
        get => _doorElectronicsBudget;
        set { _doorElectronicsBudget = value; DoorElectronicsBudgetSpecified = true; }
    }
    internal bool DoorElectronicsBudgetSpecified { get; private set; }
    private bool[]? _storageOpen;
    public bool[]? StorageOpen
    {
        get => _storageOpen;
        set { _storageOpen = value; StorageOpenSpecified = true; }
    }
    internal bool StorageOpenSpecified { get; private set; }
    private BoxStationFabricationState? _fabrication;
    public BoxStationFabricationState? Fabrication
    {
        get => _fabrication;
        set { _fabrication = value; FabricationSpecified = true; }
    }
    internal bool FabricationSpecified { get; private set; }
    private int? _exportedSteel;
    public int? ExportedSteel
    {
        get => _exportedSteel;
        set { _exportedSteel = value; ExportedSteelSpecified = true; }
    }
    internal bool ExportedSteelSpecified { get; private set; }
}

internal sealed class SavedBoxStationMaterialBundle
{
    [JsonRequired]
    public int Id { get; set; }
    [JsonRequired]
    public BoxStationMaterial Material { get; set; }
    [JsonRequired]
    public ushort Count { get; set; }
    [JsonRequired]
    public BoxStationMaterialPlace Place { get; set; }
    [JsonRequired]
    public float PositionX { get; set; }
    [JsonRequired]
    public float PositionY { get; set; }
    [JsonRequired]
    public float PositionZ { get; set; }
    private int? _storage;
    public int? Storage
    {
        get => _storage;
        set { _storage = value; StorageSpecified = true; }
    }
    internal bool StorageSpecified { get; private set; }
    private byte? _slot;
    public byte? Slot
    {
        get => _slot;
        set { _slot = value; SlotSpecified = true; }
    }
    internal bool SlotSpecified { get; private set; }
}
