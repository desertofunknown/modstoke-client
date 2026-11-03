// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using GasVector = System.Numerics.Vector3;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftService
{
    private BoxStationFoamControls? _foamControls;
    private Il2CppReferenceArray<Collider>? _foamOverlaps;
    private bool _foamInput;

    private static bool ValidStationFoam(int target, ushort setting, Vector3 contact)
    {
        if (setting > (ushort)BoxStationFoamControl.Deploy || !Finite(contact)) return false;
        var layout = BoxStationLayout.Current;
        if ((BoxStationFoamControl)setting == BoxStationFoamControl.Deploy)
        {
            if (!BoxStationHull.ValidPanelTarget(layout, target)) return false;
            var center = layout.CellPosition(target / 2);
            float plane = (target & 1) == 0 ? 0 : BoxStationLayout.DeckHeight;
            return MathF.Abs(contact.y - plane) <= .015f
                && MathF.Abs(contact.x - center.x) < 1 && MathF.Abs(contact.z - center.z) < 1;
        }
        return contact.x == 0 && contact.y == 0 && contact.z == 0 && ((BoxStationFoamControl)setting == BoxStationFoamControl.Take
            ? (uint)target < (uint)BoxStationFoam.GetItems(layout).Length : target == 0);
    }

    private void CommandFoam(BoxStationFoamControl control, int target, Vector3 contact)
    {
        if (!_ready || !ValidStationFoam(target, (ushort)control, contact)) return;
        Dispatch(ActionCode.StationFoam, stationTarget: target, stationSetting: (ushort)control, stationAim: contact);
    }

    private string ExecuteStationFoam(Peer peer, int target, ushort setting, Vector3 contact, Vector3 feet)
    {
        if (!ValidStationFoam(target, setting, contact)) return "Invalid hull sealant control.";
        string failure = StationControlAccess(peer, feet);
        if (failure.Length != 0) return failure;
        if (!_orbitalStation!.ContainsWalkArea(feet)) return "Stand on the station deck to handle its sealant.";
        if (!TryActor(peer, out _, out _, out var actor)) return "The player attachment changed.";
        var foam = _stationSystems.Foam;
        var control = (BoxStationFoamControl)setting;
        string result;
        if (control == BoxStationFoamControl.Take)
        {
            if (HasHeldMaterials(peer.Lease, actor)) return "Put down the material bundle first.";
            if (HasHeldEngineering(peer.Lease, actor)) return "Put down the engineering tool first.";
            foreach (var tool in _stationSystems.Firefighting.State.Items)
                if (tool.Place == BoxStationExtinguisherPlace.Held
                    && (tool.Holder == peer.Lease || actor != 0 && tool.Actor == actor))
                    return "Put down the extinguisher first.";
            foreach (var pack in _stationSystems.Repair.State.Packs)
                if (pack.Place == BoxStationRepairPlace.Held
                    && (pack.Holder == peer.Lease || actor != 0 && pack.Actor == actor))
                    return "Put down the tile bundle first.";
            var item = foam.State.Items[target];
            if (item.Place is not (BoxStationFoamPlace.Rack or BoxStationFoamPlace.Dropped or BoxStationFoamPlace.Stored))
                return "That sealant canister is no longer available.";
            Vector3 point;
            if (item.Place == BoxStationFoamPlace.Stored)
            {
                if (!StationStorageCurrent() || !_orbitalStation.TryStoredPortableContact(
                    SpacecraftHeldStationToolKind.Foam, target, feet + _orbitalStation.Root.transform.up * 1.3f, out point))
                    return "That stored canister is not accessible.";
            }
            else point = _orbitalStation.Root.transform.TransformPoint(
                new Vector3(item.Position.X, item.Position.Y, item.Position.Z) + BoxStationModel.FoamCarryGrip);
            failure = ReachStationControl(peer, feet, point);
            if (failure.Length != 0) return failure;
            if (!TryStationToolDrop(peer, feet, out var drop, SpacecraftHeldStationToolKind.Foam)) return "There is no clear deck within reach.";
            result = foam.Take(target, peer.Lease, actor, drop, _stationSystems.Repair.Materials);
        }
        else if (control == BoxStationFoamControl.Drop)
        {
            if (!TryStationToolDrop(peer, feet, out var drop, SpacecraftHeldStationToolKind.Foam)) return "There is no clear deck within reach.";
            result = foam.Drop(peer.Lease, drop);
        }
        else
        {
            if (foam.Held(peer.Lease) < 0) return "Take a smartfoam canister first.";
            if (!_stationSystems.Hull.Breached(target / 2, (target & 1) != 0)) return "This panel no longer has an open breach.";
            var frame = _orbitalStation.Root.transform;
            // The client supplies its exact rendered contact, not an eye origin.
            // Authority checks the panel, range and clear path from this actor.
            failure = ReachStationControl(peer, feet, frame.TransformPoint(contact));
            if (failure.Length != 0) return failure;
            if (!TryFoamAnchor(peer, feet, target, contact, out var anchor))
                return "Move within reach of a clear edge of the breach.";
            result = foam.Deploy(peer.Lease, target, anchor);
        }
        MarkFoamDirty();
        return result;
    }

    private bool TryFoamAnchor(Peer peer, Vector3 feet, int target, Vector3 contact, out GasVector anchor)
    {
        anchor = default;
        var frame = _orbitalStation!.Root.transform;
        var center = _orbitalStation.Layout.CellPosition(target / 2);
        float y = (target & 1) == 0 ? 0 : BoxStationLayout.DeckHeight;
        var localEye = frame.InverseTransformPoint(feet + frame.up * 1.3f);
        Span<Vector3> candidates = stackalloc Vector3[4];
        candidates[0] = new(center.x - 1.1f, y, Mathf.Clamp(contact.z, center.z - .9f, center.z + .9f));
        candidates[1] = new(center.x + 1.1f, y, candidates[0].z);
        candidates[2] = new(Mathf.Clamp(contact.x, center.x - .9f, center.x + .9f), y, center.z - 1.1f);
        candidates[3] = new(candidates[2].x, y, center.z + 1.1f);
        // Prefer the closest reachable rim. No canister is balanced over the void.
        for (int attempt = 0; attempt < candidates.Length; attempt++)
        {
            int nearest = -1;
            float distance = float.PositiveInfinity;
            for (int i = 0; i < candidates.Length; i++)
            {
                float squared = (candidates[i] - localEye).sqrMagnitude;
                if (squared < distance) { nearest = i; distance = squared; }
            }
            if (nearest < 0 || distance > 2.4f * 2.4f) return false;
            var candidate = candidates[nearest];
            candidates[nearest] = Vector3.positiveInfinity;
            if (ReachStationControl(peer, feet, frame.TransformPoint(candidate)).Length != 0) continue;
            var centerOffset = new Vector3(0, (target & 1) == 0 ? .13f : -.13f, 0);
            if (!FoamVolumeClear(candidate + centerOffset, new(.05f, .12f, .05f))) continue;
            anchor = new(candidate.x, candidate.y, candidate.z);
            return true;
        }
        return false;
    }

    private void ResolveFoamSeals()
    {
        if (IsClient || _stationFailed) return;
        var foam = _stationSystems.Foam;
        int count = BoxStationFoam.GetItems(_stationSystems.Layout).Length;
        bool ready = false;
        for (int item = 0; item < count; item++)
            if (foam.Ready(item)) { ready = true; break; }
        if (!ready) return;
        Span<float> doors = stackalloc float[_stationSystems.DoorCount];
        for (int door = 0; door < doors.Length; door++) doors[door] = _stationSystems.DoorAmount(door);
        Span<int> targets = stackalloc int[BoxStationFoam.MaxTargets];
        var builtDoors = BoxStationDoors.CompleteEdges(_stationSystems.Repair.DoorConstruction);
        for (int item = 0; item < count; item++)
        {
            if (!foam.Ready(item)) continue;
            int candidates = foam.CollectTargets(item, doors, targets, builtDoors), clear = 0;
            for (int i = 0; i < candidates; i++)
                if (FoamPanelClear(targets[i])) targets[clear++] = targets[i];
            if (foam.Complete(item, targets[..clear]) > 0)
            {
                _nextStationSnapshot = 0;
                PublishHullState();
            }
            MarkFoamDirty();
        }
    }

    private bool FoamPanelClear(int target)
    {
        // An unprojected orbital station has no colliders in this local frame.
        if (_orbitalStation?.Visible != true) return true;
        var center = _orbitalStation.Layout.CellPosition(target / 2);
        center.y = (target & 1) == 0 ? -.09f : BoxStationLayout.DeckHeight + .09f;
        return FoamVolumeClear(center, new(.985f, .089f, .985f));
    }

    private bool FoamVolumeClear(Vector3 center, Vector3 half)
    {
        var frame = _orbitalStation!.Root.transform;
        // Attached crew use the station's walking capsule even when their native
        // movement collider is disabled. Include them in the hardening clearance.
        if (_local != null && TryActor(_local, out var local, out _, out _)
            && !FoamCrewClear(frame.InverseTransformPoint(local), center, half)) return false;
        foreach (var peer in _peers.Values)
            if (TryActor(peer, out var feet, out _, out _)
                && !FoamCrewClear(frame.InverseTransformPoint(feet), center, half)) return false;
        var overlaps = _foamOverlaps ??= new Il2CppReferenceArray<Collider>(size: 32);
        int count = Physics.OverlapBoxNonAlloc(frame.TransformPoint(center), half,
            overlaps, frame.rotation, ~0, QueryTriggerInteraction.Ignore);
        bool clear = count >= 0 && count < overlaps.Length;
        for (int i = 0; i < overlaps.Length; i++)
        {
            if (i < count && overlaps[i]) clear = false;
            overlaps[i] = null!;
        }
        return clear;
    }

    private static bool FoamCrewClear(Vector3 feet, Vector3 center, Vector3 half)
    {
        float x = Mathf.Max(0, MathF.Abs(feet.x - center.x) - half.x);
        float z = Mathf.Max(0, MathF.Abs(feet.z - center.z) - half.z);
        float low = feet.y + ClientSpacecraftBody.CrewCapsuleBottom;
        float high = feet.y + ClientSpacecraftBody.CrewCapsuleTop;
        float y = Mathf.Max(0, Mathf.Max(low - (center.y + half.y), center.y - half.y - high));
        float radius = ClientSpacecraftBody.CrewCapsuleRadius;
        return x * x + y * y + z * z > radius * radius;
    }

    private bool UpdateFoamControls(bool interactive)
    {
        _foamInput = false;
        if (_orbitalStation?.Visible != true) { _foamControls?.Hide(); return false; }
        _foamControls ??= new BoxStationFoamControls(_orbitalStation, CommandFoam);
        return _foamInput = _foamControls.Update(CurrentFoamState, LocalFirefightingHolder, interactive, _feedback);
    }
}
