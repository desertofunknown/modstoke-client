// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using UnityEngine;
using GasVector = System.Numerics.Vector3;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftService
{
    private BoxStationEngineeringControls? _engineeringControls;
    private bool _engineeringInput, _engineeringSending;
    private BoxStationWorkIntent? _engineeringRequested;
    private BoxStationWorkIntent _engineeringSent;
    private long _engineeringInputSequence, _engineeringGesture, _nextEngineeringPulse;

    private sealed class EngineeringInput
    {
        internal long Sequence, Gesture, At, NextStart;
        internal bool Active;
        internal Guid Lease;
        internal ulong Actor;
        internal GasVector Feet;
        internal BoxStationWorkIntent Intent;
    }

    private bool HoldingEngineering(Guid holder)
    {
        if (holder != Guid.Empty && CurrentEngineeringState is { } state)
            foreach (var tool in state.Tools)
                if (tool.Place == BoxStationToolPlace.Held && tool.Holder == holder) return true;
        return false;
    }

    private bool HasHeldEngineering(Guid holder, ulong actor)
    {
        foreach (var tool in _stationSystems.Engineering.State.Tools)
            if (tool.Place == BoxStationToolPlace.Held
                && (tool.Holder == holder || actor != 0 && tool.Actor == actor)) return true;
        return false;
    }

    private static bool ValidStationEngineering(int target, ushort setting, Vector3 contact)
        => setting <= (ushort)BoxStationEngineeringControl.ShipStore && Finite(contact)
            && contact.x == 0 && contact.y == 0 && contact.z == 0
            && ((BoxStationEngineeringControl)setting switch
            {
                BoxStationEngineeringControl.Take or BoxStationEngineeringControl.ShipTake
                    => (uint)target < (uint)BoxStationEngineering.GetSources(BoxStationLayout.Current).Length,
                BoxStationEngineeringControl.Refill => (uint)target < (uint)BoxStationEngineering.GetFuelTanks(BoxStationLayout.Current).Length,
                _ => target == 0
            });

    private void CommandStationEngineering(BoxStationEngineeringControl control, int target)
    {
        if (!_ready || !ValidStationEngineering(target, (ushort)control, Vector3.zero)) return;
        Dispatch(ActionCode.StationEngineering, stationTarget: target, stationSetting: (ushort)control);
    }

    private string ExecuteStationEngineering(Peer peer, int target, ushort setting, Vector3 feet)
    {
        if (!ValidStationEngineering(target, setting, Vector3.zero)) return "Invalid engineering control.";
        var control = (BoxStationEngineeringControl)setting;
        if (control is BoxStationEngineeringControl.ShipTake or BoxStationEngineeringControl.ShipStore)
            return ExecuteShipToolRack(peer, control, target);
        if (control == BoxStationEngineeringControl.ToggleWelder && _shipId != Guid.Empty
            && _travel == SpacecraftTravel.None && peer.Walking && PeerCurrent(peer) && HasCrewAccess(peer)
            && TryActor(peer, out var shipFeet, out _, out var shipActor)
            && _body?.ContainsCabin(shipFeet) == true
            && CanRetainShipEngineeringTool(peer, shipActor, shipFeet))
        {
            CancelEngineeringWork(peer);
            string ignition = _stationSystems.Engineering.ToggleWelder(peer.Lease);
            MarkEngineeringDirty();
            return ignition;
        }
        string failure = EngineeringActor(peer, out feet, out _, out var actor);
        if (failure.Length != 0) return failure;
        var tools = _stationSystems.Engineering;
        string result;
        if (control == BoxStationEngineeringControl.Take)
        {
            failure = OtherPortableInHand(peer.Lease, actor);
            if (failure.Length != 0) return failure;
            var item = tools.State.Tools[target];
            if (item.Place == BoxStationToolPlace.Held) return "That tool is already in use.";
            if (item.Place == BoxStationToolPlace.ShipStored) return "Take that tool from the ship's rack.";
            _orbitalStation!.UpdatePortables(_stationSystems.Portables);
            Vector3 point;
            if (item.Place == BoxStationToolPlace.Stored)
            {
                if (!StationStorageCurrent() || !_orbitalStation.TryStoredPortableContact(
                    SpacecraftHeldStationToolKind.Engineering, target, feet + _orbitalStation.Root.transform.up * 1.3f, out point))
                    return "That stored tool is not accessible.";
            }
            else point = _orbitalStation.EngineeringToolPoint(target);
            failure = ReachStationControl(peer, feet, point, false);
            if (failure.Length != 0) return failure;
            if (!TryStationToolDrop(peer, feet, out var drop, SpacecraftHeldStationToolKind.Engineering))
                return "There is no clear deck within reach.";
            result = tools.Take(target, peer.Lease, actor, drop, _stationSystems.Repair.Materials);
        }
        else if (control == BoxStationEngineeringControl.Drop)
        {
            if (!TryStationToolDrop(peer, feet, out var drop, SpacecraftHeldStationToolKind.Engineering))
                return "There is no clear deck within reach.";
            CancelEngineeringWork(peer);
            result = tools.Drop(peer.Lease, drop);
        }
        else if (control == BoxStationEngineeringControl.Refill)
        {
            failure = ReachStationControl(peer, feet, _orbitalStation!.FuelTankPoint(target), false);
            if (failure.Length != 0) return failure;
            ushort before = tools.State.TankFuel[target];
            result = tools.Refill(target, peer.Lease, actor);
            if (tools.State.TankFuel[target] < before) CancelEngineeringWork(peer);
        }
        else
        {
            CancelEngineeringWork(peer);
            result = tools.ToggleWelder(peer.Lease);
        }
        MarkEngineeringDirty();
        return result;
    }

    private string OtherPortableInHand(Guid holder, ulong actor)
    {
        if (HasHeldMaterials(holder, actor)) return "Put down the material bundle first.";
        foreach (var tool in _stationSystems.Firefighting.State.Items)
            if (tool.Place == BoxStationExtinguisherPlace.Held
                && (tool.Holder == holder || actor != 0 && tool.Actor == actor)) return "Put down the extinguisher first.";
        foreach (var tool in _stationSystems.Foam.State.Items)
            if (tool.Place == BoxStationFoamPlace.Held
                && (tool.Holder == holder || actor != 0 && tool.Actor == actor)) return "Put down the sealant first.";
        foreach (var pack in _stationSystems.Repair.State.Packs)
            if (pack.Place == BoxStationRepairPlace.Held
                && (pack.Holder == holder || actor != 0 && pack.Actor == actor)) return "Put down the tile bundle first.";
        return "";
    }

    private string EngineeringActor(Peer peer, out Vector3 feet, out GasVector localFeet, out ulong actor)
    {
        feet = default; localFeet = default; actor = 0;
        if (!PeerCurrent(peer) || !HasCrewAccess(peer) || !TryActor(peer, out feet, out _, out actor))
            return "The player attachment changed.";
        string failure = StationControlAccess(peer, feet);
        if (failure.Length != 0) return failure;
        if (!_orbitalStation!.ContainsWalkArea(feet)) return "Stand on the station deck to work.";
        var local = _orbitalStation.Root.transform.InverseTransformPoint(feet);
        localFeet = new(local.x, local.y, local.z);
        return string.Empty;
    }

    private string EngineeringContact(Peer peer, Vector3 feet, BoxStationWorkIntent intent)
    {
        var model = _orbitalStation!;
        Vector3 point;
        if (intent.Kind == BoxStationWorkTarget.Bundle)
        {
            bool available = false;
            foreach (var bundle in _stationSystems.Repair.Materials.Bundles)
                if (bundle.Id == intent.Target && bundle.Place == BoxStationMaterialPlace.Dropped)
                { available = true; break; }
            if (!available) return "That material bundle moved.";
            model.UpdatePortables(_stationSystems.Portables);
            point = model.MaterialBundlePoint(intent.Target);
            var expected = model.Root.transform.InverseTransformPoint(point);
            if ((expected - new Vector3(intent.Contact.X, intent.Contact.Y, intent.Contact.Z)).sqrMagnitude > .04f)
                return "Aim at the material bundle.";
        }
        else if (intent.Kind == BoxStationWorkTarget.DoorConstruction)
        {
            if (model.DoorRevision != _stationSystems.Repair.DoorConstruction.Revision
                && !model.UpdateDoors(_stationSystems.State)) return "Station geometry is updating.";
            model.UpdatePortables(_stationSystems.Portables);
            if (model.MaterialState != _stationSystems.Repair.Materials) return "Station geometry is updating.";
            var contact = new Vector3(intent.Contact.X, intent.Contact.Y, intent.Contact.Z);
            if (!model.DoorConstructionContact(intent.Target, contact)) return "Aim at the door edge or frame.";
            if (!DoorConstructionClear(intent.Target)) return "Clear the door frame and opening first.";
            if (intent.Tool >= 0 && BoxStationHullRepair.ResolveDoorTool(_stationSystems.Layout,
                    _stationSystems.Repair.DoorAt(intent.Target),
                    BoxStationEngineering.GetSources(_stationSystems.Layout)[intent.Tool].Kind, intent.Mode,
                    out var operation, out _)
                && operation is (BoxStationDoorOperation.Unwire or BoxStationDoorOperation.RemoveElectronics
                    or BoxStationDoorOperation.DisassembleFrame)
                && !DoorRefundClear(intent.Target)) return "Clear the deck beside the frame for the recovered material.";
            point = model.Root.transform.TransformPoint(contact);
        }
        else if (intent.Kind is BoxStationWorkTarget.Wall or BoxStationWorkTarget.WallConstruction)
        {
            model.UpdatePortables(_stationSystems.Portables);
            var contact = new Vector3(intent.Contact.X, intent.Contact.Y, intent.Contact.Z);
            if (!model.ConstructionWorkContact(intent.Target, contact)) return "Aim at the wall or frame.";
            bool clearance = intent.Kind == BoxStationWorkTarget.WallConstruction;
            if (intent.Kind == BoxStationWorkTarget.Wall
                && _stationSystems.Repair.ResolveConstruction(intent.Target,
                    BoxStationEngineering.GetSources(_stationSystems.Layout)[intent.Tool].Kind, intent.Mode, out var operation, out _)
                && operation == BoxStationConstructionOperation.AnchorGirder) clearance = true;
            if (clearance && !ConstructionClear(intent.Target))
                return "Clear the construction space first.";
            point = model.Root.transform.TransformPoint(contact);
        }
        else
        {
            var contact = new Vector3(intent.Contact.X, intent.Contact.Y, intent.Contact.Z);
            if (!MaterialStructureContact(intent.Target, contact)) return "Aim at the frame.";
            if (intent.Kind == BoxStationWorkTarget.Construction && !MaterialStructureClear(intent.Target))
                return "Clear the frame before rebuilding.";
            point = model.Root.transform.TransformPoint(contact);
        }
        return ReachStationControl(peer, feet, point, false);
    }

    private bool ConstructionClear(int target)
    {
        var model = _orbitalStation;
        var layout = _stationSystems.Layout;
        if (model?.Visible != true || !BoxStationHullRepair.TryConstruction(layout, target, out var definition)
            || _stationSystems.Hull.Breached(definition.Cell, false)) return false;
        if (BoxStationHullRepair.DoorOccupiesCell(layout, _stationSystems.Repair.DoorConstruction, definition.Cell)) return false;
        if (BoxStationHull.WallTarget(layout, definition.Cell) >= 0
            && ((_stationSystems.Hull.EffectiveFlags(definition.Cell) & BoxStationCellFlags.Floor) == 0
                || _stationSystems.Hull.Foam(definition.Cell, false))) return false;
        var construction = _stationSystems.Repair.ConstructionAt(target);
        if (construction.SteelYield != 0 || construction.PlasteelYield != 0) return false;
        var center = new Vector3(definition.Position.X, definition.Position.Y + BoxStationLayout.DeckHeight * .5f,
            definition.Position.Z);
        var half = new Vector3(BoxStationLayout.TileSize * .5f - .015f,
            BoxStationLayout.DeckHeight * .5f - .015f, BoxStationLayout.TileSize * .5f - .015f);
        if (!RepairDropClear(center, half.x) || !MaterialsDropClear(center, half.x)
            || !EngineeringDropClear(center, half.x)) return false;
        foreach (var pending in _stationSystems.Repair.Materials.Pending)
            if (BoxStationHull.TryStructure(layout, BoxStationHull.StructureTarget(layout, pending.StructureId), out var structure)
                && structure.Cell == definition.Cell) return false;
        // Loose equipment has no collision; do not bury it when the frame is plated.
        foreach (var item in _stationSystems.Firefighting.State.Items)
            if (item.Place is not (BoxStationExtinguisherPlace.Held or BoxStationExtinguisherPlace.Stored)
                && MathF.Abs(center.x - item.Position.X) < half.x + .2f
                && MathF.Abs(center.z - item.Position.Z) < half.z + .2f) return false;
        foreach (var item in _stationSystems.Foam.State.Items)
            if (item.Place is not (BoxStationFoamPlace.Held or BoxStationFoamPlace.Spent or BoxStationFoamPlace.Stored)
                && MathF.Abs(center.x - item.Position.X) < half.x + .2f
                && MathF.Abs(center.z - item.Position.Z) < half.z + .2f) return false;
        var frame = model.Root.transform;
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
            if (i < count && collider && !model.IsConstructionCollider(collider, target)) clear = false;
            overlaps[i] = null!;
        }
        return clear;
    }

    private bool DoorConstructionClear(int target)
    {
        var model = _orbitalStation;
        var layout = _stationSystems.Layout;
        int edge = BoxStationDoors.EdgeFromTarget(layout, target);
        if (model?.Visible != true || !BoxStationDoors.ValidEdge(layout, edge)
            || !BoxStationDoors.TryCells(layout, edge, out int first, out int second)
            || !model.DoorConstructionBounds(target, out var bounds)) return false;
        var hull = _stationSystems.Hull;
        if (!FloorClear(first) || !FloorClear(second)) return false;
        var center = bounds.center;
        var half = bounds.extents - Vector3.one * .015f;
        var repair = _stationSystems.Repair;
        var sources = BoxStationHullRepair.GetSources(layout);
        for (int i = 0; i < sources.Length; i++)
            if (repair.State.Sheets[i] > 0 && Overlaps(sources[i].Position, .3f, .2f)) return false;
        foreach (var pack in repair.State.Packs)
            if (pack.Tiles > 0 && pack.Place != BoxStationRepairPlace.Stored && Overlaps(pack.Position, .18f, .18f)) return false;
        var materials = repair.Materials;
        var stocks = BoxStationHullRepair.GetMaterialSources(layout);
        for (int i = 0; i < stocks.Length; i++)
            if (materials.Stocks[i] > 0 && Overlaps(stocks[i].Position, .25f, .33f)) return false;
        // Held items retain a release position. Keep that reserved volume free too.
        foreach (var bundle in materials.Bundles)
            if (bundle.Count > 0 && bundle.Place != BoxStationMaterialPlace.Stored && Overlaps(bundle.Position, .25f, .33f)) return false;
        foreach (var item in _stationSystems.Engineering.State.Tools)
            if (item.Place != BoxStationToolPlace.Stored && item.ShipSlot == BoxStationEngineering.NoShipSlot
                && Overlaps(item.Position, .13f, .33f)) return false;
        foreach (var item in _stationSystems.Firefighting.State.Items)
            if (item.Place != BoxStationExtinguisherPlace.Stored && Overlaps(item.Position, .2f, .2f)) return false;
        foreach (var item in _stationSystems.Foam.State.Items)
            if (item.Place is not (BoxStationFoamPlace.Spent or BoxStationFoamPlace.Stored)
                && Overlaps(item.Position, .2f, .2f)) return false;
        foreach (var pending in materials.Pending)
            if (BoxStationHull.TryStructure(layout, BoxStationHull.StructureTarget(layout, pending.StructureId), out var structure)
                && (structure.Cell == first || structure.Cell == second)) return false;
        var frame = model.Root.transform;
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
            if (i < count && collider && !model.IsDoorConstructionCollider(collider, target)) clear = false;
            overlaps[i] = null!;
        }
        return clear;

        bool FloorClear(int cell) => (hull.EffectiveFlags(cell) & BoxStationCellFlags.Floor) != 0
            && !hull.Breached(cell, false) && !hull.Foam(cell, false);
        bool Overlaps(GasVector point, float x, float z)
            => MathF.Abs(center.x - point.X) < half.x + x && MathF.Abs(center.z - point.Z) < half.z + z;
    }

    private bool DoorRefundClear(int target)
    {
        var model = _orbitalStation!;
        var layout = _stationSystems.Layout;
        if (!BoxStationDoors.TryCells(layout, BoxStationDoors.EdgeFromTarget(layout, target), out int cell, out _)) return false;
        var point = layout.CellPosition(cell);
        const float radius = .33f;
        if (!StationToolFloorClear(point, radius) || !RepairDropClear(point, radius)
            || !MaterialsDropClear(point, radius) || !EngineeringDropClear(point, radius)) return false;
        foreach (var item in _stationSystems.Firefighting.State.Items)
            if (item.Place is not (BoxStationExtinguisherPlace.Held or BoxStationExtinguisherPlace.Stored)
                && MathF.Abs(point.x - item.Position.X) < radius + .2f
                && MathF.Abs(point.z - item.Position.Z) < radius + .2f) return false;
        foreach (var item in _stationSystems.Foam.State.Items)
            if (item.Place is not (BoxStationFoamPlace.Held or BoxStationFoamPlace.Spent or BoxStationFoamPlace.Stored)
                && MathF.Abs(point.x - item.Position.X) < radius + .2f
                && MathF.Abs(point.z - item.Position.Z) < radius + .2f) return false;
        var frame = model.Root.transform;
        var overlaps = _foamOverlaps ??= new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Collider>(size: 32);
        int count = Physics.OverlapBoxNonAlloc(frame.TransformPoint(point + Vector3.up * .11f), new(.25f, .09f, .33f),
            overlaps, frame.rotation, ~0, QueryTriggerInteraction.Ignore);
        bool clear = count == 0;
        for (int i = 0; i < overlaps.Length; i++) overlaps[i] = null!;
        return clear;
    }

    private void ReceiveEngineeringWork(Peer peer, Message message)
    {
        if (IsClient || !_ready || message.Sequence <= peer.Engineering.Sequence || message.EngineeringGesture <= 0) return;
        var input = peer.Engineering;
        input.Sequence = message.Sequence;
        if (message.EngineeringGesture < input.Gesture) return;
        if (message.EngineeringGesture == input.Gesture)
        {
            if (!message.EngineeringActive || message.EngineeringIntent != input.Intent || Now - input.At >= 800)
                CancelEngineeringWork(peer);
            else if (input.Active) input.At = Now;
            return;
        }
        CancelEngineeringWork(peer);
        input.Gesture = message.EngineeringGesture;
        if (!message.EngineeringActive || message.EngineeringIntent is not { } intent
            || !BoxStationEngineering.ValidIntent(_stationSystems.Layout, intent) || Now < input.NextStart) return;
        string result = EngineeringActor(peer, out var feet, out var localFeet, out var actor);
        if (result.Length == 0) result = EngineeringContact(peer, feet, intent);
        if (result.Length == 0)
        {
            input.NextStart = Now + 800;
            input.Lease = peer.Lease; input.Actor = actor; input.Feet = localFeet; input.Intent = intent; input.At = Now;
            long hullRevision = _stationSystems.Hull.State.Revision;
            if (_stationSystems.Engineering.TryBegin(peer.Lease, actor, intent, localFeet, out result))
                foreach (var work in _stationSystems.Engineering.State.Work)
                    if (work.Holder == peer.Lease) { input.Active = true; break; }
            if (input.Active) result = string.Empty;
            PublishEngineeringChanges(hullRevision);
        }
        peer.Result = result;
        if (ReferenceEquals(peer, _local)) _feedback = result;
        _nextSnapshot = 0;
    }

    private void ValidateEngineeringWork()
    {
        foreach (var peer in _seats)
        {
            if (peer == null || !peer.Engineering.Active) continue;
            var input = peer.Engineering;
            string failure = EngineeringActor(peer, out var feet, out var local, out var actor);
            if (failure.Length != 0 || input.Lease != peer.Lease || input.Actor != actor || Now - input.At >= 800
                || GasVector.DistanceSquared(local, input.Feet) > .15f * .15f
                || EngineeringContact(peer, feet, input.Intent).Length != 0)
            { CancelEngineeringWork(peer); continue; }
            bool active = false;
            foreach (var work in _stationSystems.Engineering.State.Work)
                if (work.Holder == peer.Lease) { active = true; break; }
            if (!active) input.Active = false;
        }
    }

    private void PublishEngineeringChanges(long previousHullRevision)
    {
        MarkEngineeringDirty();
        MarkMaterialsDirty();
        if (_stationState is not { } state || state.DoorRevision != _stationSystems.Repair.DoorConstruction.Revision)
            _nextStationSnapshot = 0;
        if (previousHullRevision == _stationSystems.Hull.State.Revision) return;
        _nextStationSnapshot = 0;
        PublishHullState();
    }

    private void CancelEngineeringWork(Peer peer)
    {
        peer.Engineering.Active = false;
        if (peer.Engineering.Lease != Guid.Empty) _stationSystems.Engineering.Cancel(peer.Engineering.Lease);
        _stationSystems.Engineering.Cancel(peer.Lease);
        MarkEngineeringDirty();
    }

    private void RequestEngineeringWork(BoxStationWorkIntent intent) => _engineeringRequested = intent;

    private void FlushEngineeringInput()
    {
        var request = _engineeringRequested;
        bool active = request.HasValue && _ready && LocalFirefightingHolder != Guid.Empty;
        if (!active)
        {
            if (_engineeringSending) SendEngineeringInput(false);
            _engineeringSending = false;
            return;
        }
        var intent = request!.Value;
        // Contact remains fixed for a gesture. Camera jitter cannot silently restart a timed job.
        bool changed = !_engineeringSending || intent.Tool != _engineeringSent.Tool || intent.Kind != _engineeringSent.Kind
            || intent.Target != _engineeringSent.Target || intent.Mode != _engineeringSent.Mode;
        if (changed)
        {
            if (_engineeringSending) SendEngineeringInput(false);
            _engineeringGesture = checked(_engineeringGesture + 1);
            _engineeringSent = intent;
            _engineeringSending = true;
            _nextEngineeringPulse = 0;
        }
        if (Now < _nextEngineeringPulse) return;
        _nextEngineeringPulse = Now + 200;
        SendEngineeringInput(true);
    }

    private void SendEngineeringInput(bool active)
    {
        // Losing the lease already cancels host work; no release packet can use its retired identity.
        if (!_ready || _shipId == Guid.Empty || LocalFirefightingHolder == Guid.Empty) return;
        long sequence = checked(++_engineeringInputSequence);
        if (IsClient) _event?.EngineeringWork(sequence, _engineeringGesture, active, _engineeringSent);
        else if (_local is { } peer)
            ReceiveEngineeringWork(peer, new Message { Sequence = sequence, EngineeringGesture = _engineeringGesture,
                EngineeringActive = active, EngineeringIntent = active ? _engineeringSent : null });
    }

    private void ResetEngineeringInput()
    {
        _engineeringRequested = null;
        _engineeringSending = _engineeringInput = false;
        _engineeringInputSequence = _engineeringGesture = _nextEngineeringPulse = 0;
        if (_local != null) _local.Engineering = new();
        foreach (var peer in _peers.Values) peer.Engineering = new();
    }

    private bool UpdateEngineeringControls(bool interactive)
    {
        if (_orbitalStation?.Visible != true) { _engineeringControls?.Hide(); return false; }
        _engineeringControls ??= new BoxStationEngineeringControls(_orbitalStation, CommandStationEngineering,
            RequestEngineeringWork, CommandStationCable, CommandStationFloor);
        return _engineeringInput = _engineeringControls.Update(CurrentEngineeringState, LocalFirefightingHolder, interactive, _feedback);
    }

    private bool EngineeringDropClear(Vector3 point, float radius)
    {
        foreach (var tool in _stationSystems.Engineering.State.Tools)
            if (tool.Place is not (BoxStationToolPlace.Held or BoxStationToolPlace.Stored or BoxStationToolPlace.ShipStored)
                && MathF.Abs(point.x - tool.Position.X) < radius + .13f
                && MathF.Abs(point.z - tool.Position.Z) < radius + .33f) return false;
        return true;
    }
}
