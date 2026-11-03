// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftService
{
    private static bool ValidStationCable(int target, ushort setting, Vector3 contact)
    {
        var layout = BoxStationLayout.Current;
        var sites = BoxStationPower.GetCableSites(layout);
        if (setting > 1 || (uint)target >= sites.Length || !Finite(contact)
            || !BoxStationPower.CableAccessible(layout, target) && !BoxStationHullRepair.FloorCovering(layout, sites[target].Cell)
            || MathF.Abs(contact.y - .018f) > .04f) return false;
        var center = layout.CellPosition(sites[target].Cell);
        return MathF.Abs(contact.x - center.x) <= BoxStationLayout.TileSize * .5f + .04f
            && MathF.Abs(contact.z - center.z) <= BoxStationLayout.TileSize * .5f + .04f;
    }

    private void CommandStationCable(int target, bool install, Vector3 contact)
    {
        ushort setting = install ? (ushort)1 : (ushort)0;
        if (!_ready || !ValidStationCable(target, setting, contact)) return;
        _engineeringRequested = null;
        Dispatch(ActionCode.StationCable, stationTarget: target, stationSetting: setting, stationAim: contact);
    }

    private string ExecuteStationCable(Peer peer, int target, ushort setting, Vector3 contact)
    {
        if (!ValidStationCable(target, setting, contact)) return "Invalid cable target.";
        string failure = EngineeringActor(peer, out var feet, out _, out var actor);
        if (failure.Length != 0) return failure;
        bool install = setting == 1;
        failure = StationMaintenanceHands(peer, actor, install, BoxStationMaterial.Cable, BoxStationToolKind.Wirecutters);
        if (failure.Length != 0) return failure;
        var repair = _stationSystems.Repair;
        var power = _stationSystems.Power;
        var model = _orbitalStation!;
        if (model.HullState.Revision != _stationSystems.Hull.State.Revision || !model.UpdateCables(power.CableState))
            return "Station geometry is updating.";
        var materials = repair.Materials;
        model.UpdatePortables(_stationSystems.Portables);
        if (model.MaterialState != materials) return "Station geometry is updating.";
        if (!model.ValidCableContact(target, contact, install)) return "That cable is no longer accessible.";
        var point = model.Root.transform.TransformPoint(contact);
        failure = ReachStationControl(peer, feet, point, false);
        if (failure.Length != 0) return failure;
        System.Numerics.Vector3 drop = default;
        if (!install && !TryStationToolDrop(peer, feet, out drop, SpacecraftHeldStationToolKind.Material))
            return "There is no clear deck nearby for the recovered cable.";
        CancelEngineeringWork(peer);
        bool changed = install ? repair.TryInstallCable(power, target, peer.Lease, actor, out var result)
            : repair.TryCutCable(power, target, drop, out result);
        if (!changed) return result;
        MarkMaterialsDirty();
        _stationState = _stationSystems.State;
        _nextStationSnapshot = 0;
        RecordStationInteraction(peer, actor, point);
        if (!model.UpdateCables(power.CableState))
            throw new InvalidOperationException("Station cable projection rejected the committed cable state.");
        model.UpdatePortables(_stationSystems.Portables);
        return result;
    }
}
