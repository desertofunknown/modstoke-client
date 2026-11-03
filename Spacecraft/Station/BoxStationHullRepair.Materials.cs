// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Vector3 = System.Numerics.Vector3;

namespace Modstoke.Client;

internal enum BoxStationMaterial : byte
{
    Rods, Glass, ReinforcedGlass, PlasmaGlass, ReinforcedPlasmaGlass, GlassShards, PlasmaGlassShards, Steel, Plasteel, FloorTiles, Cable,
    DoorElectronics, LineSpare = 12, CircuitSpare = 13
}

internal enum BoxStationMaterialPlace : byte { Dropped, Held, Stored }

internal enum BoxStationMaterialControl : byte
{
    TakeStock, Recover, TakeBundle, Drop, Rebuild, Dismantle, Recycle, Reinforce, CraftTiles, ReplacePanel, ToggleStorage, StoreStorage,
    LoadFabricator, UnloadFabricatorSteel, UnloadFabricatorCable, FabricateLine, FabricateCircuit, CancelFabrication,
    TakeFabricatorLine, TakeFabricatorCircuit, LoadShipSpares
}

internal readonly record struct BoxStationMaterialBudget(
    [property: JsonRequired] int Glass, [property: JsonRequired] int PlasmaGlass, [property: JsonRequired] int Rods);
internal readonly record struct BoxStationMaterialSource(int Object, int Cell, Vector3 Position, BoxStationMaterial Material, ushort Count);
internal readonly record struct BoxStationPendingYield([property: JsonRequired] int StructureId,
    [property: JsonRequired] ushort Sheets, [property: JsonRequired] ushort Shards, [property: JsonRequired] ushort Rods);
internal readonly record struct BoxStationMaterialBundle(int Id, BoxStationMaterial Material, ushort Count,
    BoxStationMaterialPlace Place, Vector3 Position, ulong Actor, Guid Holder, int Storage = -1, byte Slot = 0);
internal readonly record struct BoxStationMaterialState(string LayoutHash, long HullRevision,
    BoxStationMaterialBudget Budget, BoxStationMaterialBudget Installed, int NextBundleId,
    ImmutableArray<ushort> Stocks, ImmutableArray<BoxStationPendingYield> Pending, ImmutableArray<BoxStationMaterialBundle> Bundles,
    BoxStationConstructionBudget ConstructionBudget, BoxStationConstructionBudget ConstructionInstalled,
    BoxStationConstructionBudget ConstructionWaste, int ImportedSteel, ImmutableArray<BoxStationConstruction> Construction, int ConsumedFloorTiles,
    long CableRevision, int CableBudget, int CableInstalled, BoxStationFloorState Floors,
    BoxStationDoorConstructionState DoorConstruction, int DoorElectronicsBudget, ImmutableArray<bool> StorageOpen,
    BoxStationFabricationState Fabrication, int ExportedSteel);

internal sealed partial class BoxStationHullRepair
{
    internal const int MaxMaterialBundles = 64, MaxMaterialBundleCount = 50, MaxFloorTileBundleCount = 20, MaxCableBundleCount = 30;
    private MaterialDefinitions _materialDefinitions = null!;
    private ushort[] _materialStocks = null!;
    private BoxStationPendingYield[] _materialPending = null!;
    private readonly BoxStationMaterialBundle[] _materialBundles = new BoxStationMaterialBundle[MaxMaterialBundles];
    private BoxStationMaterialBudget _materialBudget;
    private BoxStationMaterialState _materials;
    private int _nextMaterialBundleId = 1;
    private int _consumedFloorTiles;
    private int _shipExportedSteel;
    private bool _materialsDirty = true, _materialsInitialized;

    internal BoxStationMaterialState Materials
    {
        get
        {
            var hull = _hull.State;
            SynchronizeConstruction();
            if (!SynchronizeFloors()) throw new InvalidOperationException("Station floor covering destruction exceeds its state capacity.");
            if (_materialsDirty || _materials.HullRevision != hull.Revision)
            {
                var pending = ImmutableArray.CreateBuilder<BoxStationPendingYield>();
                foreach (var yield in _materialPending)
                    if (yield.Sheets != 0 || yield.Shards != 0 || yield.Rods != 0) pending.Add(yield);
                var bundles = ImmutableArray.CreateBuilder<BoxStationMaterialBundle>();
                foreach (var bundle in _materialBundles)
                    if (bundle.Id != 0) bundles.Add(bundle);
                bundles.Sort(static (a, b) => a.Id.CompareTo(b.Id));
                var installed = _materials.LayoutHash == _layout.LayoutHash && _materials.HullRevision == hull.Revision
                    ? _materials.Installed : InstalledMaterials(_layout, hull);
                _materials = new(_layout.LayoutHash, hull.Revision, _materialBudget, installed,
                    _nextMaterialBundleId, ImmutableArray.CreateRange(_materialStocks), pending.ToImmutable(), bundles.ToImmutable(),
                    _constructionBudget, AddConstructionUnits(InstalledConstruction(_construction), new(_doorConstruction.Assemblies.Length * 4, 0)),
                    _constructionWaste, _importedSteel, CaptureConstruction(), _consumedFloorTiles,
                    _cableRevision, _cableBudget, _cableInstalled, _floors, _doorConstruction, _doorElectronicsBudget,
                    _storageOpen, _fabrication, _shipExportedSteel);
                _materialsDirty = false;
            }
            return _materials;
        }
    }

    private void InitializeMaterials(bool seedSource)
    {
        _materialDefinitions = LayoutMaterials.GetValue(_layout, BuildMaterialDefinitions);
        _materialStocks = new ushort[_materialDefinitions.Sources.Length];
        _materialPending = new BoxStationPendingYield[_materialDefinitions.Structures.Length];
        _storageOpen = ImmutableArray.CreateRange(new bool[BoxStationStorage.GetDefinitions(_layout).Length]);
        _shipExportedSteel = 0;
        for (int i = 0; i < _materialPending.Length; i++)
            _materialPending[i] = new(_materialDefinitions.Structures[i].Id, 0, 0, 0);
        InitializeConstruction();
        InitializeFabrication();
        if (seedSource) SeedMigratedMaterials();
    }

    // Older saves contain no history of recovered material. Only their remaining components are credited.
    internal void SeedMigratedMaterials()
    {
        if (_materialsInitialized) return;
        var budget = InstalledMaterials(_layout, _hull.State);
        for (int i = 0; i < _materialStocks.Length; i++)
        {
            var source = _materialDefinitions.Sources[i];
            _materialStocks[i] = source.Count;
            budget = AddMaterialUnits(budget, MaterialUnits(source.Material, source.Count));
        }
        _materialBudget = budget;
        SeedConstruction();
        _cableRevision = 0;
        _cableBudget = _materialDefinitions.CableBudget;
        _cableInstalled = _materialDefinitions.CableCount;
        _doorElectronicsBudget = _materialDefinitions.DoorElectronicsBudget;
        _floors = MigrateFloors(_layout, _hull.State);
        _floorHullRevision = _hull.State.Revision;
        _materialsInitialized = true;
        _materialsDirty = true;
    }

    internal bool ApplyImpact(int target, float joules)
    {
        if (!_materialsInitialized || !SynchronizeFloors()) return false;
        if (TryConstruction(_layout, target, out _)) return ApplyConstructionImpact(target, joules);
        if (!_hull.TryImpactIntegrity(target, joules, out byte remaining)) return false;
        if (!BoxStationHull.TryStructure(_layout, target, out var structure)) return ApplyFloorImpact(target, joules, remaining);
        byte integrity = _hull.StructureIntegrity(structure.Id);
        int units = StructureUnits(structure, integrity) - StructureUnits(structure, remaining);
        int index = _materialDefinitions.Indices[structure.Id];
        var pending = _materialPending[index];
        var material = StructureMaterial(_layout, structure);
        int shards = structure.Kind == BoxStationStructureKind.Window ? units : 0;
        int rods = structure.Kind == BoxStationStructureKind.Grille
            || material is BoxStationMaterial.ReinforcedGlass or BoxStationMaterial.ReinforcedPlasmaGlass ? units : 0;
        if (pending.Shards + shards > ushort.MaxValue || pending.Rods + rods > ushort.MaxValue) return false;
        var updated = pending with { Shards = (ushort)(pending.Shards + shards), Rods = (ushort)(pending.Rods + rods) };
        if (!_hull.ApplyImpact(target, joules)) return false;
        _materialPending[index] = updated;
        _materialsDirty = true;
        return true;
    }

    internal string DismantleStructure(int target)
    {
        if (!_materialsInitialized || !BoxStationHull.TryStructure(_layout, target, out var structure)) return "Unknown station structure.";
        byte integrity = _hull.StructureIntegrity(structure.Id);
        if (integrity == 0) return "This structure is already removed.";
        int index = _materialDefinitions.Indices[structure.Id];
        var pending = _materialPending[index];
        int units = StructureUnits(structure, integrity);
        int sheets = structure.Kind == BoxStationStructureKind.Window ? units : 0;
        int rods = structure.Kind == BoxStationStructureKind.Grille ? units : 0;
        if (pending.Sheets + sheets > ushort.MaxValue || pending.Rods + rods > ushort.MaxValue) return "Salvage storage is full.";
        var updated = pending with { Sheets = (ushort)(pending.Sheets + sheets), Rods = (ushort)(pending.Rods + rods) };
        if (!_hull.RemoveStructure(target)) return "This structure cannot be dismantled.";
        _materialPending[index] = updated;
        _materialsDirty = true;
        return "Structure dismantled. Materials ready for collection.";
    }

    internal string RebuildStructure(Guid holder, int target)
    {
        if (!_materialsInitialized || !BoxStationHull.TryStructure(_layout, target, out var structure)) return "Unknown station structure.";
        int slot = HeldMaterialSlot(holder);
        if (slot < 0) return "You are not holding construction materials.";
        var bundle = _materialBundles[slot];
        int cost = MaterialCost(_layout, target, _hull.StructureIntegrity(structure.Id));
        if (cost == 0) return "Aim at a missing window or broken grille.";
        if (bundle.Material != StructureMaterial(_layout, structure)) return "This structure requires a different material.";
        if (bundle.Count < cost) return "Not enough material to rebuild this structure.";
        var updated = bundle.Count == cost ? default : bundle with { Count = (ushort)(bundle.Count - cost) };
        // Hull allocation and revision validation finish before the prepared value-type debit is committed.
        if (!_hull.RestoreStructure(target)) return "This structure cannot be rebuilt.";
        _materialBundles[slot] = updated;
        _materialsDirty = true;
        return "Structure rebuilt.";
    }

    internal string TakeMaterialStock(int source, Guid holder, ulong actor, Vector3 dropPosition)
    {
        if (source < 0 || source >= _materialStocks.Length) return "Unknown material stock.";
        if (_materialStocks[source] == 0) return "This stock is empty.";
        string? rejection = PrepareMaterialPickup(holder, actor, dropPosition, out int slot);
        if (rejection is not null) return rejection;
        int maximum = _materialDefinitions.Sources[source].Material == BoxStationMaterial.Cable ? MaxCableBundleCount : MaxMaterialBundleCount;
        ushort count = (ushort)Math.Min(maximum, (int)_materialStocks[source]);
        _materialBundles[slot] = new(_nextMaterialBundleId++, _materialDefinitions.Sources[source].Material,
            count, BoxStationMaterialPlace.Held, dropPosition, actor, holder);
        _materialStocks[source] -= count;
        _materialsDirty = true;
        return "Materials taken.";
    }

    internal string CraftMaterialTiles(Guid holder, ulong actor, Vector3 dropPosition)
    {
        int slot = HeldMaterialSlot(holder);
        if (!_materialsInitialized || slot < 0) return "You are not holding steel sheets.";
        var bundle = _materialBundles[slot];
        if (bundle.Actor != actor) return "Material holder is unavailable.";
        if (bundle.Material != BoxStationMaterial.Steel) return "Floor tiles require steel sheets.";
        if (Held(holder) >= 0 || HasHeldActor(actor)) return "Put down the other floor tiles first.";
        if (!ValidDropPosition(_layout, dropPosition)) return "Stand on clear station flooring to craft tiles.";
        if (bundle.Count == 1)
        {
            _materialBundles[slot] = bundle with { Material = BoxStationMaterial.FloorTiles, Count = 4, Position = dropPosition };
        }
        else
        {
            if (_nextMaterialBundleId == int.MaxValue) return "No further material bundles can be created.";
            int tiles = -1;
            for (int i = 0; i < _materialBundles.Length; i++)
                if (_materialBundles[i].Id == 0) { tiles = i; break; }
            if (tiles < 0) return "Use an existing material bundle before crafting more.";
            var remaining = bundle with { Count = (ushort)(bundle.Count - 1), Place = BoxStationMaterialPlace.Dropped,
                Position = dropPosition, Actor = 0, Holder = Guid.Empty };
            var crafted = new BoxStationMaterialBundle(_nextMaterialBundleId, BoxStationMaterial.FloorTiles, 4,
                BoxStationMaterialPlace.Held, dropPosition, actor, holder);
            _materialBundles[slot] = remaining;
            _materialBundles[tiles] = crafted;
            _nextMaterialBundleId++;
        }
        _materialsDirty = true;
        return bundle.Count == 1 ? "Four floor tiles crafted." : "Four floor tiles crafted. Remaining steel dropped.";
    }

    internal string ReplaceMaterialTile(Guid holder, ulong actor, int target)
    {
        if (!SynchronizeFloors()) return "Floor covering state is unavailable.";
        int slot = HeldMaterialSlot(holder);
        if (!_materialsInitialized || slot < 0) return "You are not holding floor tiles.";
        var bundle = _materialBundles[slot];
        if (bundle.Actor != actor) return "Material holder is unavailable.";
        if (bundle.Material != BoxStationMaterial.FloorTiles) return "This panel requires floor tiles.";
        if (Held(holder) >= 0 || HasHeldActor(actor)) return "Put down the other floor tiles first.";
        if (_consumedFloorTiles == int.MaxValue) return "No further floor tiles can be used.";
        int consumed = _consumedFloorTiles + 1;
        var remaining = bundle.Count == 1 ? default : bundle with { Count = (ushort)(bundle.Count - 1) };
        // RestorePanel completes validation and allocation before these prepared value-type debits.
        if (!_hull.RestorePanel(target)) return "Aim at a damaged original floor or roof panel.";
        _materialBundles[slot] = remaining;
        _consumedFloorTiles = consumed;
        _materialsDirty = true;
        return "Panel replaced. One floor tile used.";
    }

    internal string RecoverMaterial(int structureId, BoxStationMaterial material, Guid holder, ulong actor, Vector3 dropPosition)
    {
        if (TryConstruction(_layout, structureId, out _))
            return RecoverConstructionMaterial(structureId, material, holder, actor, dropPosition);
        int index = MaterialStructureIndex(structureId);
        if (index < 0) return "Unknown salvage source.";
        var pending = _materialPending[index];
        var structure = _materialDefinitions.Structures[index];
        int count = material == BoxStationMaterial.Rods ? pending.Rods
            : material == StructureMaterial(_layout, structure) ? pending.Sheets
            : structure.Kind == BoxStationStructureKind.Window && material == ShardMaterial(structure) ? pending.Shards : 0;
        if (count == 0) return "No matching salvage remains.";
        string? rejection = PrepareMaterialPickup(holder, actor, dropPosition, out int slot);
        if (rejection is not null) return rejection;
        ushort taken = (ushort)Math.Min(MaxMaterialBundleCount, count);
        var updated = material == BoxStationMaterial.Rods ? pending with { Rods = (ushort)(pending.Rods - taken) }
            : material == StructureMaterial(_layout, structure) ? pending with { Sheets = (ushort)(pending.Sheets - taken) }
            : pending with { Shards = (ushort)(pending.Shards - taken) };
        _materialBundles[slot] = new(_nextMaterialBundleId++, material, taken, BoxStationMaterialPlace.Held, dropPosition, actor, holder);
        _materialPending[index] = updated;
        _materialsDirty = true;
        return "Salvage recovered.";
    }

    internal string TakeMaterialBundle(int id, Guid holder, ulong actor, Vector3 dropPosition)
    {
        int slot = MaterialBundleSlot(id);
        if (slot < 0) return "Material bundle is no longer available.";
        var bundle = _materialBundles[slot];
        if (bundle.Place == BoxStationMaterialPlace.Stored)
        {
            if ((uint)bundle.Storage >= (uint)_storageOpen.Length || !_storageOpen[bundle.Storage]) return "Open the storage first.";
        }
        else if (bundle.Place != BoxStationMaterialPlace.Dropped) return "Material bundle is no longer available.";
        string? rejection = MaterialHolderRejection(holder, actor, dropPosition);
        if (rejection is not null) return rejection;
        _materialBundles[slot] = bundle with { Place = BoxStationMaterialPlace.Held, Position = dropPosition,
            Actor = actor, Holder = holder, Storage = -1, Slot = 0 };
        _materialsDirty = true;
        return "Materials taken.";
    }

    internal string DropMaterials(Guid holder, Vector3 dropPosition)
    {
        int slot = HeldMaterialSlot(holder);
        if (slot < 0) return "You are not holding construction materials.";
        if (!ValidDropPosition(_layout, dropPosition)) return "Drop the materials on clear station flooring.";
        _materialBundles[slot] = _materialBundles[slot] with { Place = BoxStationMaterialPlace.Dropped, Position = dropPosition,
            Actor = 0, Holder = Guid.Empty, Storage = -1, Slot = 0 };
        _materialsDirty = true;
        return "Materials dropped.";
    }

    internal int HeldMaterials(Guid holder)
    {
        int slot = HeldMaterialSlot(holder);
        return slot >= 0 ? _materialBundles[slot].Id : -1;
    }

    internal void ObserveHeldMaterials(Guid holder, Vector3 dropPosition)
    {
        int slot = HeldMaterialSlot(holder);
        if (slot >= 0 && _materialBundles[slot].Position != dropPosition && ValidDropPosition(_layout, dropPosition))
        {
            _materialBundles[slot] = _materialBundles[slot] with { Position = dropPosition };
            _materialsDirty = true;
        }
    }

    internal void ReleaseMaterials(Guid holder)
    {
        int slot = HeldMaterialSlot(holder);
        if (slot < 0) return;
        _materialBundles[slot] = _materialBundles[slot] with { Place = BoxStationMaterialPlace.Dropped,
            Actor = 0, Holder = Guid.Empty, Storage = -1, Slot = 0 };
        _materialsDirty = true;
    }

    internal string RecycleMaterials(Guid holder)
    {
        int slot = HeldMaterialSlot(holder);
        if (slot < 0) return "You are not holding glass shards.";
        return RecycleMaterialSlot(slot);
    }

    internal string RecycleDroppedMaterials(int id)
    {
        int slot = MaterialBundleSlot(id);
        if (slot < 0 || _materialBundles[slot].Place != BoxStationMaterialPlace.Dropped) return "Glass shards are no longer available.";
        return RecycleMaterialSlot(slot);
    }

    private string RecycleMaterialSlot(int slot)
    {
        var bundle = _materialBundles[slot];
        if (bundle.Material is not (BoxStationMaterial.GlassShards or BoxStationMaterial.PlasmaGlassShards)) return "Only glass shards can be recycled.";
        _materialBundles[slot] = bundle with { Material = bundle.Material == BoxStationMaterial.GlassShards ? BoxStationMaterial.Glass : BoxStationMaterial.PlasmaGlass };
        _materialsDirty = true;
        return "Glass shards recycled into sheets.";
    }

    internal string ReinforceMaterials(Guid holder, int rodBundleId)
    {
        int slot = HeldMaterialSlot(holder), rods = MaterialBundleSlot(rodBundleId);
        if (slot < 0) return "You are not holding glass sheets.";
        var bundle = _materialBundles[slot];
        if (bundle.Material is not (BoxStationMaterial.Glass or BoxStationMaterial.PlasmaGlass)) return "These sheets cannot be reinforced.";
        if (rods < 0 || _materialBundles[rods].Material != BoxStationMaterial.Rods
            || _materialBundles[rods].Place != BoxStationMaterialPlace.Dropped) return "Select an available bundle of metal rods.";
        var rodBundle = _materialBundles[rods];
        if (rodBundle.Count < bundle.Count) return "One metal rod is required for each glass sheet.";
        _materialBundles[rods] = rodBundle.Count == bundle.Count ? default : rodBundle with { Count = (ushort)(rodBundle.Count - bundle.Count) };
        _materialBundles[slot] = bundle with { Material = bundle.Material == BoxStationMaterial.Glass ? BoxStationMaterial.ReinforcedGlass : BoxStationMaterial.ReinforcedPlasmaGlass };
        _materialsDirty = true;
        return "Glass sheets reinforced.";
    }

    private string? MaterialHolderRejection(Guid holder, ulong actor, Vector3 dropPosition)
    {
        if (!_materialsInitialized || holder == Guid.Empty) return "Material holder is unavailable.";
        if (HeldMaterialSlot(holder) >= 0) return "You are already holding construction materials.";
        foreach (var bundle in _materialBundles)
            if (actor != 0 && bundle.Id != 0 && bundle.Place == BoxStationMaterialPlace.Held && bundle.Actor == actor)
                return "You are already holding construction materials.";
        return ValidDropPosition(_layout, dropPosition) ? null : "Stand on clear station flooring to take materials.";
    }

    private string? PrepareMaterialPickup(Guid holder, ulong actor, Vector3 dropPosition, out int slot)
    {
        slot = -1;
        string? rejection = MaterialHolderRejection(holder, actor, dropPosition);
        if (rejection is not null) return rejection;
        if (_nextMaterialBundleId == int.MaxValue) return "No further material bundles can be created.";
        for (int i = 0; i < _materialBundles.Length; i++)
            if (_materialBundles[i].Id == 0) { slot = i; return null; }
        return "Use an existing material bundle before collecting more.";
    }

    private bool TryPrepareMaterialSalvage(BoxStationMaterial material, Vector3 drop, out int slot,
        out BoxStationMaterialBundle salvage, out bool allocate, out string result)
        => TryPrepareMaterialSalvage(material, 1, drop, out slot, out salvage, out allocate, out result);

    private bool TryPrepareMaterialSalvage(BoxStationMaterial material, int count, Vector3 drop, out int slot,
        out BoxStationMaterialBundle salvage, out bool allocate, out string result)
    {
        slot = -1;
        salvage = default;
        allocate = false;
        result = "";
        int maximum = material == BoxStationMaterial.FloorTiles ? MaxFloorTileBundleCount
            : material == BoxStationMaterial.Cable ? MaxCableBundleCount : MaxMaterialBundleCount;
        if (count < 1 || count > maximum) return false;
        int free = -1;
        for (int i = 0; i < _materialBundles.Length; i++)
        {
            var bundle = _materialBundles[i];
            if (bundle.Id == 0) { if (free < 0) free = i; continue; }
            if (bundle.Place == BoxStationMaterialPlace.Dropped && bundle.Material == material
                && bundle.Count <= maximum - count && bundle.Position == drop) { slot = i; break; }
        }
        allocate = slot < 0;
        if (allocate)
        {
            if (_nextMaterialBundleId == int.MaxValue) { result = "No further material bundles can be created."; return false; }
            if (free < 0) { result = "Use an existing material bundle before collecting more."; return false; }
            slot = free;
        }
        salvage = allocate ? new(_nextMaterialBundleId, material, (ushort)count, BoxStationMaterialPlace.Dropped, drop, 0, Guid.Empty)
            : _materialBundles[slot] with { Count = (ushort)(_materialBundles[slot].Count + count) };
        return true;
    }

    private int HeldMaterialSlot(Guid holder)
    {
        if (holder == Guid.Empty) return -1;
        for (int i = 0; i < _materialBundles.Length; i++)
            if (_materialBundles[i].Id != 0 && _materialBundles[i].Place == BoxStationMaterialPlace.Held && _materialBundles[i].Holder == holder) return i;
        return -1;
    }

    private int MaterialBundleSlot(int id)
    {
        if (id <= 0) return -1;
        for (int i = 0; i < _materialBundles.Length; i++)
            if (_materialBundles[i].Id == id) return i;
        return -1;
    }

    private int MaterialStructureIndex(int id)
        => id >= 0 && id < _materialDefinitions.Indices.Length ? _materialDefinitions.Indices[id] : -1;

    private static BoxStationMaterial ShardMaterial(BoxStationStructure structure)
        => structure.Plasma ? BoxStationMaterial.PlasmaGlassShards : BoxStationMaterial.GlassShards;
}
