// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftService
{
    private BoxStationHullRepairControls? _repairControls;
    private BoxStationRepairState? _heldRepairState;
    private Guid _heldRepairHolder;
    private bool _holdingRepair;
    private bool _repairInput;

    private bool HoldingRepairTiles(Guid holder)
    {
        var state = CurrentRepairState;
        if (_heldRepairState == state && _heldRepairHolder == holder) return _holdingRepair;
        _heldRepairState = state;
        _heldRepairHolder = holder;
        _holdingRepair = false;
        if (holder != Guid.Empty && state is { } current)
            foreach (var pack in current.Packs)
                if (pack.Place == BoxStationRepairPlace.Held && pack.Holder == holder)
                { _holdingRepair = true; break; }
        return _holdingRepair;
    }

    private static bool ValidStationRepair(int target, ushort setting, Vector3 contact)
    {
        if (setting > (ushort)BoxStationRepairControl.TakeSheets || !Finite(contact)) return false;
        var layout = BoxStationLayout.Current;
        var control = (BoxStationRepairControl)setting;
        if (control == BoxStationRepairControl.Replace)
        {
            if (!BoxStationHull.ValidPanelTarget(layout, target)) return false;
            var center = layout.CellPosition(target / 2);
            float plane = (target & 1) == 0 ? 0 : BoxStationLayout.DeckHeight;
            return MathF.Abs(contact.y - plane) <= .015f
                && MathF.Abs(contact.x - center.x) < 1 && MathF.Abs(contact.z - center.z) < 1;
        }
        if (contact.x != 0 || contact.y != 0 || contact.z != 0) return false;
        return control switch
        {
            BoxStationRepairControl.Craft or BoxStationRepairControl.TakeSheets => (uint)target < (uint)BoxStationHullRepair.GetSources(layout).Length
                && BoxStationHullRepair.GetSources(layout)[target].Sheets > 0,
            BoxStationRepairControl.Take => (uint)target < (uint)BoxStationHullRepair.MaxPacks(layout),
            _ => target == 0
        };
    }

    private void CommandStationRepair(BoxStationRepairControl control, int target, Vector3 contact)
    {
        if (!_ready || !ValidStationRepair(target, (ushort)control, contact)) return;
        Dispatch(ActionCode.StationRepair, stationTarget: target, stationSetting: (ushort)control, stationAim: contact);
    }

    private string ExecuteStationRepair(Peer peer, int target, ushort setting, Vector3 contact, Vector3 feet)
    {
        if (!ValidStationRepair(target, setting, contact)) return "Invalid hull repair control.";
        string failure = StationControlAccess(peer, feet);
        if (failure.Length != 0) return failure;
        if (!_orbitalStation!.ContainsWalkArea(feet)) return "Stand on the station deck to handle repair supplies.";
        if (!TryActor(peer, out _, out _, out var actor)) return "The player attachment changed.";
        var control = (BoxStationRepairControl)setting;
        var repair = _stationSystems.Repair;
        string result;
        if (control is BoxStationRepairControl.Craft or BoxStationRepairControl.Take or BoxStationRepairControl.TakeSheets)
        {
            if (HasHeldMaterials(peer.Lease, actor)) return "Put down the material bundle first.";
            if (HasHeldEngineering(peer.Lease, actor)) return "Put down the engineering tool first.";
            foreach (var item in _stationSystems.Firefighting.State.Items)
                if (item.Place == BoxStationExtinguisherPlace.Held
                    && (item.Holder == peer.Lease || actor != 0 && item.Actor == actor))
                    return "Put down the extinguisher first.";
            foreach (var item in _stationSystems.Foam.State.Items)
                if (item.Place == BoxStationFoamPlace.Held
                    && (item.Holder == peer.Lease || actor != 0 && item.Actor == actor))
                    return "Put down the sealant canister first.";
            var state = repair.State;
            if (control == BoxStationRepairControl.TakeSheets)
                foreach (var pack in state.Packs)
                    if (pack.Place == BoxStationRepairPlace.Held
                        && (pack.Holder == peer.Lease || actor != 0 && pack.Actor == actor))
                        return "Put down the tile bundle first.";
            Vector3 point;
            if (control is BoxStationRepairControl.Craft or BoxStationRepairControl.TakeSheets)
            {
                if (state.Sheets[target] == 0) return "This metal supply is depleted.";
                var source = BoxStationHullRepair.GetSources(_stationSystems.Layout)[target];
                point = _orbitalStation.Root.transform.TransformPoint(new Vector3(source.Position.X,
                    source.Position.Y + (Math.Min(state.Sheets[target], (ushort)6) - 1) * .013f + .012f, source.Position.Z));
            }
            else
            {
                if ((uint)target >= (uint)state.Packs.Length) return "That tile bundle is unavailable.";
                var pack = state.Packs[target];
                if (pack.Place is not (BoxStationRepairPlace.Source or BoxStationRepairPlace.Dropped or BoxStationRepairPlace.Stored)
                    || pack.Tiles == 0)
                    return "That tile bundle is no longer available.";
                if (pack.Place == BoxStationRepairPlace.Stored)
                {
                    if (!StationStorageCurrent() || !_orbitalStation.TryStoredPortableContact(
                        SpacecraftHeldStationToolKind.RepairTiles, target, feet + _orbitalStation.Root.transform.up * 1.3f, out point))
                        return "That stored tile bundle is not accessible.";
                }
                else point = _orbitalStation.Root.transform.TransformPoint(
                    new Vector3(pack.Position.X, pack.Position.Y + (pack.Tiles - 1) * .019f + .018f, pack.Position.Z));
            }
            failure = ReachStationControl(peer, feet, point);
            if (failure.Length != 0) return failure;
            var heldKind = control == BoxStationRepairControl.TakeSheets
                ? SpacecraftHeldStationToolKind.Material : SpacecraftHeldStationToolKind.RepairTiles;
            if (!TryStationToolDrop(peer, feet, out var drop, heldKind))
                return "There is no clear deck within reach for the bundle.";
            result = control switch
            {
                BoxStationRepairControl.Craft => repair.Craft(target, peer.Lease, actor, drop),
                BoxStationRepairControl.TakeSheets => repair.TakeSteelStock(target, peer.Lease, actor, drop),
                _ => repair.Take(target, peer.Lease, actor, drop, repair.Materials)
            };
        }
        else if (control == BoxStationRepairControl.Drop)
        {
            if (!TryStationToolDrop(peer, feet, out var drop, SpacecraftHeldStationToolKind.RepairTiles))
                return "There is no clear deck within reach for the tile bundle.";
            result = repair.Drop(peer.Lease, drop);
        }
        else
        {
            if (repair.Held(peer.Lease) < 0) return "Take a tile bundle first.";
            var hull = _stationSystems.Hull;
            if (hull.Integrity(target) == BoxStationHull.Capacity) return "This panel is already intact.";
            failure = ReachStationControl(peer, feet, _orbitalStation.Root.transform.TransformPoint(contact));
            if (failure.Length != 0) return failure;
            if (hull.Breached(target / 2, (target & 1) != 0) && !FoamPanelClear(target))
                return "Clear the breach before fitting the replacement panel.";
            long revision = hull.State.Revision;
            result = repair.Replace(peer.Lease, target);
            if (hull.State.Revision != revision)
            {
                _nextStationSnapshot = 0;
                PublishHullState();
            }
        }
        MarkRepairDirty();
        if (control == BoxStationRepairControl.TakeSheets) MarkMaterialsDirty();
        return result;
    }

    private bool RepairDropClear(Vector3 point, float radius)
    {
        var materials = _stationSystems.Repair.State;
        var sources = BoxStationHullRepair.GetSources(_stationSystems.Layout);
        for (int i = 0; i < sources.Length; i++)
            if (materials.Sheets[i] > 0 && MathF.Abs(point.x - sources[i].Position.X) < radius + .3f
                && MathF.Abs(point.z - sources[i].Position.Z) < radius + .2f) return false;
        foreach (var pack in materials.Packs)
            if (pack.Tiles > 0 && pack.Place is BoxStationRepairPlace.Source or BoxStationRepairPlace.Dropped
                && MathF.Abs(point.x - pack.Position.X) < radius + .18f
                && MathF.Abs(point.z - pack.Position.Z) < radius + .18f) return false;
        return true;
    }

    private bool UpdateRepairControls(bool interactive)
    {
        _repairInput = false;
        if (_orbitalStation?.Visible != true) { _repairControls?.Hide(); return false; }
        _repairControls ??= new BoxStationHullRepairControls(_orbitalStation, CommandStationRepair);
        return _repairInput = _repairControls.Update(CurrentRepairState, LocalFirefightingHolder, interactive, _feedback);
    }
}
