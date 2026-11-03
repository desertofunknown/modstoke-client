// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftService
{
    private bool StationStorageCurrent()
    {
        var model = _orbitalStation!;
        var portables = _stationSystems.Portables;
        var state = portables.Materials;
        if (state.HullRevision != _stationSystems.Hull.State.Revision || model.HullState.Revision != state.HullRevision
            || state.CableRevision != _stationSystems.Power.CableState.Revision || model.CableRevision != state.CableRevision
            || model.DoorRevision != state.DoorConstruction.Revision) return false;
        model.UpdatePortables(portables);
        return model.PortableState == portables && model.FloorRevision == state.Floors.Revision;
    }

    private void ResolveStationStorageClosures()
    {
        if (IsClient || _orbitalStation?.Visible != true) return;
        var model = _orbitalStation;
        var repair = _stationSystems.Repair;
        var state = repair.Materials;
        if (state.StorageOpen.Length != model.StorageDefinitions.Length) return;
        bool changed = false;
        foreach (var definition in model.StorageDefinitions)
        {
            int storage = definition.Id;
            if (state.StorageOpen[storage] || model.StorageAmount(storage) <= 0
                || StationStorageCrewClear(model.StorageSweepBounds(storage))) continue;
            repair.ToggleMaterialStorage(storage);
            changed = true;
        }
        if (changed)
        {
            MarkPortablesDirty();
            model.UpdatePortables(_stationSystems.Portables);
        }
    }

    private string ExecuteStationStorage(Peer peer, int storage, BoxStationMaterialControl control, Vector3 feet)
    {
        if (!StationStorageCurrent()) return "Storage state is not current.";
        var model = _orbitalStation!;
        var repair = _stationSystems.Repair;
        if (!model.StorageSettled(storage)) return "Wait for the storage to finish moving.";
        if (!TryActor(peer, out _, out _, out var actor)) return "The player attachment changed.";
        if (control == BoxStationMaterialControl.StoreStorage)
        {
            if (!model.StorageOpen(storage)) return "Open the storage first.";
            if (!StationStorageHolderCurrent(peer.Lease, actor)) return "The held equipment attachment changed.";
            if (!BoxStationSystems.StorageHasRoom(_stationSystems.Layout, _stationSystems.Portables, storage))
                return "This storage is full.";
        }
        string failure = ReachStationControl(peer, feet, model.StorageHandlePoint(storage), false);
        if (failure.Length != 0) return failure;
        if (!StationStorageClear(storage)) return "Clear the storage and its opening first.";
        CancelEngineeringWork(peer);
        RecordStationInteraction(peer, actor, model.StorageHandlePoint(storage));
        string result = control == BoxStationMaterialControl.ToggleStorage
            ? repair.ToggleMaterialStorage(storage) : _stationSystems.StorePortable(peer.Lease, storage);
        MarkPortablesDirty();
        return result;
    }

    private bool StationStorageHolderCurrent(Guid holder, ulong actor)
    {
        var state = _stationSystems.Portables;
        int held = 0;
        foreach (var item in state.Materials.Bundles)
            if (item.Place == BoxStationMaterialPlace.Held && item.Holder == holder && item.Actor == actor) held++;
        foreach (var item in state.Engineering.Tools)
            if (item.Place == BoxStationToolPlace.Held && item.Holder == holder && item.Actor == actor) held++;
        foreach (var item in state.Firefighting.Items)
            if (item.Place == BoxStationExtinguisherPlace.Held && item.Holder == holder && item.Actor == actor) held++;
        foreach (var item in state.Foam.Items)
            if (item.Place == BoxStationFoamPlace.Held && item.Holder == holder && item.Actor == actor) held++;
        foreach (var item in state.Repair.Packs)
            if (item.Place == BoxStationRepairPlace.Held && item.Holder == holder && item.Actor == actor) held++;
        return held == 1;
    }

    private bool StationStorageClear(int storage)
    {
        var model = _orbitalStation!;
        var frame = model.Root.transform;
        var bounds = model.StorageSweepBounds(storage);
        var center = bounds.center;
        var half = bounds.extents;
        if (!Finite(center) || !Finite(half) || half.x <= 0 || half.y <= 0 || half.z <= 0) return false;
        // Loose station equipment has no native collision, but still occupies the opening.
        float radius = Mathf.Max(half.x, half.z);
        if (!RepairDropClear(center, radius) || !EngineeringDropClear(center, radius)) return false;
        var materials = _stationSystems.Repair.Materials;
        var sources = BoxStationHullRepair.GetMaterialSources(_stationSystems.Layout);
        for (int i = 0; i < sources.Length; i++)
            if (materials.Stocks[i] > 0 && MathF.Abs(center.x - sources[i].Position.X) < half.x + .3f
                && MathF.Abs(center.z - sources[i].Position.Z) < half.z + .31f) return false;
        foreach (var bundle in materials.Bundles)
            if (bundle.Place == BoxStationMaterialPlace.Dropped
                && MathF.Abs(center.x - bundle.Position.X) < half.x + .3f
                && MathF.Abs(center.z - bundle.Position.Z) < half.z + .31f) return false;
        foreach (var item in _stationSystems.Firefighting.State.Items)
            if (item.Place is not (BoxStationExtinguisherPlace.Held or BoxStationExtinguisherPlace.Stored)
                && MathF.Abs(center.x - item.Position.X) < half.x + .2f
                && MathF.Abs(center.z - item.Position.Z) < half.z + .2f) return false;
        foreach (var item in _stationSystems.Foam.State.Items)
            if (item.Place is not (BoxStationFoamPlace.Held or BoxStationFoamPlace.Spent or BoxStationFoamPlace.Stored)
                && MathF.Abs(center.x - item.Position.X) < half.x + .2f
                && MathF.Abs(center.z - item.Position.Z) < half.z + .2f) return false;
        if (!StationStorageCrewClear(bounds)) return false;
        var overlaps = _foamOverlaps ??= new Il2CppReferenceArray<Collider>(size: 32);
        int count = Physics.OverlapBoxNonAlloc(frame.TransformPoint(center), half,
            overlaps, frame.rotation, ~0, QueryTriggerInteraction.Ignore);
        bool clear = count >= 0 && count < overlaps.Length;
        for (int i = 0; i < overlaps.Length; i++)
        {
            var collider = overlaps[i];
            if (i < count && collider && !model.OwnsStorageCollider(storage, collider)) clear = false;
            overlaps[i] = null!;
        }
        return clear;
    }

    private bool StationStorageCrewClear(Bounds bounds)
    {
        var frame = _orbitalStation!.Root.transform;
        var center = bounds.center;
        var half = bounds.extents;
        // Include the interior as well as the closure sweep so closing cannot trap a crew member.
        if (_local != null && TryActor(_local, out var local, out _, out _)
            && !FoamCrewClear(frame.InverseTransformPoint(local), center, half)) return false;
        foreach (var peer in _peers.Values)
            if (TryActor(peer, out var feet, out _, out _)
                && !FoamCrewClear(frame.InverseTransformPoint(feet), center, half)) return false;
        return true;
    }
}
