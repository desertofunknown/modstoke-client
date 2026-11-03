// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftService
{
    private BoxStationMaterialsControls? _materialControls;
    private bool _materialInput;

    private bool HoldingMaterials(Guid holder)
    {
        if (holder != Guid.Empty && CurrentMaterialsState is { } state)
            foreach (var bundle in state.Bundles)
                if (bundle.Place == BoxStationMaterialPlace.Held && bundle.Holder == holder) return true;
        return false;
    }

    private static bool ValidStationMaterials(int target, ushort setting, Vector3 contact)
    {
        if (!Finite(contact)) return false;
        var control = (BoxStationMaterialControl)(setting & 255);
        var kind = (BoxStationMaterial)(setting >> 8);
        if (control is >= BoxStationMaterialControl.LoadFabricator and <= BoxStationMaterialControl.TakeFabricatorCircuit)
            return target == BoxStationHullRepair.FabricationMachine && kind == 0
                && contact.x == 0 && contact.y == 0 && contact.z == 0;
        if (control == BoxStationMaterialControl.LoadShipSpares)
            return target > 0 && kind == 0 && contact.x == 0 && contact.y == 0 && contact.z == 0;
        if (control is not (BoxStationMaterialControl.TakeStock or BoxStationMaterialControl.Recover
                or BoxStationMaterialControl.TakeBundle or BoxStationMaterialControl.Drop or BoxStationMaterialControl.Rebuild
                or BoxStationMaterialControl.Reinforce or BoxStationMaterialControl.CraftTiles
                or BoxStationMaterialControl.ReplacePanel or BoxStationMaterialControl.ToggleStorage
                or BoxStationMaterialControl.StoreStorage) || kind > BoxStationMaterial.FloorTiles
            || control != BoxStationMaterialControl.Recover && kind != 0) return false;
        if (control == BoxStationMaterialControl.ReplacePanel)
            return ValidStationRepair(target, (ushort)BoxStationRepairControl.Replace, contact);
        var layout = BoxStationLayout.Current;
        if (control == BoxStationMaterialControl.Rebuild)
        {
            if (!BoxStationHull.TryStructure(layout, target, out var structure)) return false;
            var center = layout.CellPosition(structure.Cell);
            return contact.y is >= -.02f and <= BoxStationLayout.DeckHeight + .02f
                && MathF.Abs(contact.x - center.x) <= BoxStationLayout.TileSize * .5f + .02f
                && MathF.Abs(contact.z - center.z) <= BoxStationLayout.TileSize * .5f + .02f;
        }
        if (contact.x != 0 || contact.y != 0 || contact.z != 0) return false;
        return control switch
        {
            BoxStationMaterialControl.TakeStock => (uint)target < (uint)BoxStationHullRepair.GetMaterialSources(layout).Length,
            BoxStationMaterialControl.Recover => BoxStationHull.StructureTarget(layout, target) >= 0
                || BoxStationHullRepair.TryConstruction(layout, target, out _),
            BoxStationMaterialControl.TakeBundle or BoxStationMaterialControl.Reinforce => target > 0,
            BoxStationMaterialControl.ToggleStorage or BoxStationMaterialControl.StoreStorage
                => BoxStationStorage.TryDefinition(layout, target, out _),
            _ => target == 0
        };
    }

    private void CommandStationMaterials(BoxStationMaterialControl control, int target, BoxStationMaterial kind, Vector3 contact)
    {
        ushort setting = (ushort)((byte)control | (control == BoxStationMaterialControl.Recover ? (byte)kind << 8 : 0));
        if (!_ready || !ValidStationMaterials(target, setting, contact)) return;
        Dispatch(ActionCode.StationMaterials, stationTarget: target, stationSetting: setting, stationAim: contact);
    }

    private string ExecuteStationMaterials(Peer peer, int target, ushort setting, Vector3 contact, Vector3 feet)
    {
        if (!ValidStationMaterials(target, setting, contact)) return "Invalid material control.";
        string failure = StationControlAccess(peer, feet);
        if (failure.Length != 0) return failure;
        if (!TryActor(peer, out _, out _, out var actor)) return "The player attachment changed.";
        var control = (BoxStationMaterialControl)(setting & 255);
        if (control == BoxStationMaterialControl.LoadShipSpares) return ExecuteShipSpares(peer, target, feet, actor);
        var model = _orbitalStation!;
        if (!model.ContainsWalkArea(feet)) return "Stand on the station deck to handle supplies.";
        var repair = _stationSystems.Repair;
        var kind = (BoxStationMaterial)(setting >> 8);
        string result;
        if (control is BoxStationMaterialControl.TakeStock or BoxStationMaterialControl.TakeBundle or BoxStationMaterialControl.Recover
            or BoxStationMaterialControl.CraftTiles or BoxStationMaterialControl.ReplacePanel
            or BoxStationMaterialControl.UnloadFabricatorSteel or BoxStationMaterialControl.UnloadFabricatorCable
            or BoxStationMaterialControl.TakeFabricatorLine or BoxStationMaterialControl.TakeFabricatorCircuit)
        {
            if (HasHeldEngineering(peer.Lease, actor)) return "Put down the engineering tool first.";
            foreach (var item in _stationSystems.Firefighting.State.Items)
                if (item.Place == BoxStationExtinguisherPlace.Held
                    && (item.Holder == peer.Lease || actor != 0 && item.Actor == actor))
                    return "Put down the extinguisher first.";
            foreach (var item in _stationSystems.Foam.State.Items)
                if (item.Place == BoxStationFoamPlace.Held
                    && (item.Holder == peer.Lease || actor != 0 && item.Actor == actor))
                    return "Put down the sealant canister first.";
            foreach (var pack in repair.State.Packs)
                if (pack.Place == BoxStationRepairPlace.Held
                    && (pack.Holder == peer.Lease || actor != 0 && pack.Actor == actor))
                    return "Put down the tile bundle first.";
        }
        if (control is BoxStationMaterialControl.ToggleStorage or BoxStationMaterialControl.StoreStorage)
            return ExecuteStationStorage(peer, target, control, feet);
        if (control is >= BoxStationMaterialControl.LoadFabricator and <= BoxStationMaterialControl.TakeFabricatorCircuit)
            result = ExecuteStationFabrication(peer, target, control, feet, actor);
        else if (control is BoxStationMaterialControl.TakeStock or BoxStationMaterialControl.TakeBundle or BoxStationMaterialControl.Recover)
        {
            var state = repair.Materials;
            model.UpdatePortables(_stationSystems.Portables);
            Vector3 point;
            if (control == BoxStationMaterialControl.TakeStock)
            {
                if (state.Stocks[target] == 0) return "This supply is depleted.";
                point = model.MaterialStockPoint(target);
            }
            else if (control == BoxStationMaterialControl.TakeBundle)
            {
                BoxStationMaterialBundle selected = default;
                foreach (var bundle in state.Bundles)
                    if (bundle.Id == target) { selected = bundle; break; }
                if (selected.Id != target) return "That bundle is no longer available.";
                if (selected.Place == BoxStationMaterialPlace.Stored)
                {
                    if (!StationStorageCurrent() || !model.StorageOpen(selected.Storage)) return "Open the storage first.";
                    var eye = feet + model.Root.transform.up * 1.3f;
                    if (!model.TryStoredMaterialContact(target, eye, out point)) return "That bundle is not accessible.";
                }
                else if (selected.Place == BoxStationMaterialPlace.Dropped) point = model.MaterialBundlePoint(target);
                else return "That bundle is no longer available.";
            }
            else
            {
                bool available = false;
                if (BoxStationHullRepair.TryConstruction(_stationSystems.Layout, target, out _))
                {
                    var construction = repair.ConstructionAt(target);
                    available = kind == BoxStationMaterial.Steel && construction.SteelYield > 0
                        || kind == BoxStationMaterial.Plasteel && construction.PlasteelYield > 0;
                }
                foreach (var yield in state.Pending)
                {
                    if (yield.StructureId != target) continue;
                    BoxStationHull.TryStructure(_stationSystems.Layout,
                        BoxStationHull.StructureTarget(_stationSystems.Layout, target), out var structure);
                    var sheet = BoxStationHullRepair.StructureMaterial(_stationSystems.Layout, structure);
                    var shard = structure.Plasma ? BoxStationMaterial.PlasmaGlassShards : BoxStationMaterial.GlassShards;
                    available = kind == BoxStationMaterial.Rods && yield.Rods > 0
                        || kind == sheet && yield.Sheets > 0 || kind == shard && yield.Shards > 0;
                    break;
                }
                if (!available) return "That material has already been recovered.";
                point = model.MaterialYieldPoint(target, kind);
            }
            failure = ReachStationControl(peer, feet, point);
            if (failure.Length != 0) return failure;
            if (!TryStationToolDrop(peer, feet, out var drop, SpacecraftHeldStationToolKind.Material))
                return "There is no clear deck within reach for the bundle.";
            result = control switch
            {
                BoxStationMaterialControl.TakeStock => repair.TakeMaterialStock(target, peer.Lease, actor, drop),
                BoxStationMaterialControl.TakeBundle => repair.TakeMaterialBundle(target, peer.Lease, actor, drop),
                _ => repair.RecoverMaterial(target, kind, peer.Lease, actor, drop)
            };
        }
        else if (control == BoxStationMaterialControl.Drop)
        {
            if (!TryStationToolDrop(peer, feet, out var drop, SpacecraftHeldStationToolKind.Material))
                return "There is no clear deck within reach for the bundle.";
            CancelEngineeringWork(peer);
            result = repair.DropMaterials(peer.Lease, drop);
        }
        else if (control == BoxStationMaterialControl.CraftTiles)
        {
            if (!TryStationToolDrop(peer, feet, out var drop, SpacecraftHeldStationToolKind.Material))
                return "There is no clear deck within reach for the remaining steel.";
            CancelEngineeringWork(peer);
            result = repair.CraftMaterialTiles(peer.Lease, actor, drop);
        }
        else if (control == BoxStationMaterialControl.ReplacePanel)
        {
            var hull = _stationSystems.Hull;
            if (hull.Integrity(target) == BoxStationHull.Capacity && !hull.Foam(target / 2, (target & 1) != 0))
                return "That panel is already intact.";
            failure = ReachStationControl(peer, feet, model.Root.transform.TransformPoint(contact));
            if (failure.Length != 0) return failure;
            if (hull.Breached(target / 2, (target & 1) != 0) && !FoamPanelClear(target))
                return "Clear the breach before fitting the replacement panel.";
            CancelEngineeringWork(peer);
            long revision = hull.State.Revision;
            result = repair.ReplaceMaterialTile(peer.Lease, actor, target);
            if (hull.State.Revision != revision)
            {
                _nextStationSnapshot = 0;
                PublishHullState();
            }
        }
        else if (control == BoxStationMaterialControl.Reinforce)
        {
            bool rods = false;
            foreach (var bundle in repair.Materials.Bundles)
                if (bundle.Id == target && bundle.Place == BoxStationMaterialPlace.Dropped && bundle.Material == BoxStationMaterial.Rods)
                { rods = true; break; }
            if (!rods) return "Aim at an available rod bundle.";
            model.UpdatePortables(_stationSystems.Portables);
            failure = ReachStationControl(peer, feet, model.MaterialBundlePoint(target));
            if (failure.Length != 0) return failure;
            CancelEngineeringWork(peer);
            result = repair.ReinforceMaterials(peer.Lease, target);
        }
        else
        {
            if (repair.HeldMaterials(peer.Lease) < 0) return "Take construction materials first.";
            if (!MaterialStructureContact(target, contact)) return "Aim at the original frame.";
            failure = ReachStationControl(peer, feet, model.Root.transform.TransformPoint(contact));
            if (failure.Length != 0) return failure;
            if (!MaterialStructureClear(target)) return "Clear the frame before rebuilding.";
            if (!BoxStationHull.TryStructure(_stationSystems.Layout, target, out var structure)) return "Unknown structure.";
            byte previous = _stationSystems.Hull.StructureIntegrity(structure.Id);
            if (structure.Kind == BoxStationStructureKind.Grille && previous == 0) return "Hold the build control to fit the grille.";
            long revision = _stationSystems.Hull.State.Revision;
            result = repair.RebuildStructure(peer.Lease, target);
            if (revision != _stationSystems.Hull.State.Revision)
            {
                _stationSystems.Engineering.RecordRebuild(target, previous);
                MarkEngineeringDirty();
                _nextStationSnapshot = 0;
                PublishHullState();
            }
        }
        MarkMaterialsDirty();
        return result;
    }

    private bool MaterialStructureContact(int target, Vector3 contact)
    {
        int count = _orbitalStation!.MaterialStructurePlaneCount(target);
        for (int plane = 0; plane < count; plane++)
        {
            if (!_orbitalStation.MaterialStructureBounds(target, plane, out var bounds)) continue;
            var delta = contact - bounds.center;
            var half = bounds.extents;
            float x = MathF.Abs(delta.x), y = MathF.Abs(delta.y), z = MathF.Abs(delta.z);
            const float tolerance = .02f;
            if (x <= half.x + tolerance && y <= half.y + tolerance && z <= half.z + tolerance
                && (MathF.Abs(x - half.x) <= tolerance || MathF.Abs(y - half.y) <= tolerance
                    || MathF.Abs(z - half.z) <= tolerance)) return true;
        }
        return false;
    }

    private bool MaterialStructureClear(int target)
    {
        var model = _orbitalStation!;
        var frame = model.Root.transform;
        int planes = model.MaterialStructurePlaneCount(target);
        if (planes == 0) return false;
        for (int plane = 0; plane < planes; plane++)
        {
            if (!model.MaterialStructureBounds(target, plane, out var bounds)) return false;
            var center = bounds.center;
            // Adjacent deck and frame faces share a boundary, not occupied volume.
            var half = bounds.extents - Vector3.one * .015f;
            if (_local != null && TryActor(_local, out var local, out _, out _)
                && !FoamCrewClear(frame.InverseTransformPoint(local), center, half)) return false;
            foreach (var peer in _peers.Values)
                if (TryActor(peer, out var feet, out _, out _)
                    && !FoamCrewClear(frame.InverseTransformPoint(feet), center, half)) return false;
            var overlaps = _foamOverlaps ??= new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Collider>(size: 32);
            int count = Physics.OverlapBoxNonAlloc(frame.TransformPoint(center), half,
                overlaps, frame.rotation, ~0, QueryTriggerInteraction.Ignore);
            bool clear = count >= 0 && count < overlaps.Length;
            for (int i = 0; i < overlaps.Length; i++)
            {
                var collider = overlaps[i];
                if (i < count && collider && !model.IsMaterialRebuildCollider(collider, target)) clear = false;
                overlaps[i] = null!;
            }
            if (!clear) return false;
        }
        return true;
    }

    private bool MaterialsDropClear(Vector3 point, float radius)
    {
        var state = _stationSystems.Repair.Materials;
        var sources = BoxStationHullRepair.GetMaterialSources(_stationSystems.Layout);
        for (int i = 0; i < sources.Length; i++)
            if (state.Stocks[i] > 0 && MathF.Abs(point.x - sources[i].Position.X) < radius + .25f
                && MathF.Abs(point.z - sources[i].Position.Z) < radius + .33f) return false;
        foreach (var bundle in state.Bundles)
            if (bundle.Place == BoxStationMaterialPlace.Dropped
                && MathF.Abs(point.x - bundle.Position.X) < radius + .25f
                && MathF.Abs(point.z - bundle.Position.Z) < radius + .33f) return false;
        return true;
    }

    private bool HasHeldMaterials(Guid holder, ulong actor)
    {
        foreach (var bundle in _stationSystems.Repair.Materials.Bundles)
            if (bundle.Place == BoxStationMaterialPlace.Held
                && (bundle.Holder == holder || actor != 0 && bundle.Actor == actor)) return true;
        return false;
    }

    private bool UpdateMaterialControls(bool interactive, bool storageOnly = false)
    {
        _materialInput = false;
        if (_orbitalStation?.Visible != true) { _materialControls?.Hide(); return false; }
        _materialControls ??= new BoxStationMaterialsControls(_orbitalStation, CommandStationMaterials,
            RequestEngineeringWork, CommandStationCable, CommandStationFloor);
        var portables = CurrentPortablesState;
        var state = portables?.Materials;
        bool coherent = state is { } current && current.HullRevision == _orbitalStation.HullState.Revision
            && current.CableRevision == _orbitalStation.CableRevision && current.Floors.Revision == _orbitalStation.FloorRevision
            && current.DoorConstruction.Revision == _orbitalStation.DoorRevision;
        return _materialInput = _materialControls.Update(state, LocalFirefightingHolder, interactive && coherent,
            _feedback, portables, storageOnly);
    }
}
