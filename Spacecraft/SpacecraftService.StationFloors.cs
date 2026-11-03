// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftService
{
    private static bool ValidStationFloor(int cell, ushort setting, Vector3 contact)
    {
        var layout = BoxStationLayout.Current;
        if (setting > 1 || !Finite(contact) || !BoxStationHullRepair.FloorCovering(layout, cell)
            || MathF.Abs(contact.y) > .04f) return false;
        var center = layout.CellPosition(cell);
        return MathF.Abs(contact.x - center.x) <= BoxStationLayout.TileSize * .5f
            && MathF.Abs(contact.z - center.z) <= BoxStationLayout.TileSize * .5f;
    }

    private void CommandStationFloor(int cell, bool install, Vector3 contact)
    {
        ushort setting = install ? (ushort)1 : (ushort)0;
        if (!_ready || !ValidStationFloor(cell, setting, contact)) return;
        _engineeringRequested = null;
        Dispatch(ActionCode.StationFloor, stationTarget: cell, stationSetting: setting, stationAim: contact);
    }

    private string ExecuteStationFloor(Peer peer, int cell, ushort setting, Vector3 contact)
    {
        if (!ValidStationFloor(cell, setting, contact)) return "Invalid floor target.";
        string failure = EngineeringActor(peer, out var feet, out _, out var actor);
        if (failure.Length != 0) return failure;
        bool install = setting == 1;
        failure = StationMaintenanceHands(peer, actor, install, BoxStationMaterial.FloorTiles, BoxStationToolKind.Crowbar);
        if (failure.Length != 0) return failure;
        var repair = _stationSystems.Repair;
        var model = _orbitalStation!;
        if (BoxStationHullRepair.DoorOccupiesCell(_stationSystems.Layout, repair.DoorConstruction, cell))
            return "Remove the door frame before changing its floor covering.";
        if (model.HullState.Revision != _stationSystems.Hull.State.Revision
            || !model.UpdateCables(_stationSystems.Power.CableState)) return "Station geometry is updating.";
        var materials = repair.Materials;
        model.UpdatePortables(_stationSystems.Portables);
        if (model.MaterialState != materials) return "Station geometry is updating.";
        if (!model.ValidFloorContact(cell, contact, install)) return "That floor covering is no longer accessible.";
        var point = model.Root.transform.TransformPoint(contact);
        failure = ReachStationControl(peer, feet, point, false);
        if (failure.Length != 0) return failure;
        System.Numerics.Vector3 drop = default;
        if (!install && !TryStationToolDrop(peer, feet, out drop, SpacecraftHeldStationToolKind.Material))
            return "There is no clear deck nearby for the recovered tile.";
        CancelEngineeringWork(peer);
        bool changed = install ? repair.TryInstallFloor(cell, peer.Lease, actor, out var result)
            : repair.TryRemoveFloor(cell, drop, out result);
        if (!changed) return result;
        MarkMaterialsDirty();
        RecordStationInteraction(peer, actor, point);
        model.UpdatePortables(_stationSystems.Portables);
        if (model.MaterialState != repair.Materials)
            throw new InvalidOperationException("Station floor projection rejected its committed material state.");
        return result;
    }

    private string StationMaintenanceHands(Peer peer, ulong actor, bool install,
        BoxStationMaterial material, BoxStationToolKind toolKind)
    {
        if (install)
        {
            if (HasHeldEngineering(peer.Lease, actor)) return "Put down the engineering tool first.";
            bool held = false;
            foreach (var bundle in _stationSystems.Repair.Materials.Bundles)
                if (bundle.Place == BoxStationMaterialPlace.Held && bundle.Holder == peer.Lease
                    && bundle.Actor == actor && bundle.Material == material && bundle.Count > 0)
                { held = true; break; }
            if (!held) return material == BoxStationMaterial.Cable ? "Hold a cable coil to replace this cable."
                : "Hold floor tiles to replace this covering.";
        }
        else
        {
            if (HasHeldMaterials(peer.Lease, actor)) return "Put down the material bundle first.";
            bool held = false;
            var sources = BoxStationEngineering.GetSources(_stationSystems.Layout);
            foreach (var tool in _stationSystems.Engineering.State.Tools)
                if (tool.Place == BoxStationToolPlace.Held && tool.Holder == peer.Lease && tool.Actor == actor
                    && sources[tool.Id].Kind == toolKind) { held = true; break; }
            if (!held) return toolKind == BoxStationToolKind.Wirecutters ? "Hold wirecutters to cut this cable."
                : "Hold a crowbar to lift this covering.";
        }
        foreach (var item in _stationSystems.Firefighting.State.Items)
            if (item.Place == BoxStationExtinguisherPlace.Held && (item.Holder == peer.Lease || item.Actor == actor))
                return "Put down the extinguisher first.";
        foreach (var item in _stationSystems.Foam.State.Items)
            if (item.Place == BoxStationFoamPlace.Held && (item.Holder == peer.Lease || item.Actor == actor))
                return "Put down the sealant first.";
        foreach (var pack in _stationSystems.Repair.State.Packs)
            if (pack.Place == BoxStationRepairPlace.Held && (pack.Holder == peer.Lease || pack.Actor == actor))
                return "Put down the tile bundle first.";
        return string.Empty;
    }
}
