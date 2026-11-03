// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftService
{
    private SpacecraftExtinguisherRack? _extinguisherRack;
    private BoxStationWaterEffects? _shipWaterEffects;
    private string _shipFirefightingPrompt = "";
    private string _shipFirefightingReadout = "";
    private BoxStationExtinguisherState _shipReadoutState;
    private float _nextShipSpray;

    private void EnsureShipFirefighting()
    {
        if (_body?.Ready != true || _extinguisherRack != null) return;
        _extinguisherRack = new SpacecraftExtinguisherRack(_body, CommandFirefighting);
        _body.SetExtinguisherRackSolid(_extinguisherRack.Solid);
    }

    private bool UpdateShipFirefighting(bool interactive)
    {
        _shipFirefightingPrompt = "";
        int seat = IsClient ? _clientSeat : _local?.Seat ?? -1;
        bool available = interactive && _ready && _body is { Ready: true, TelemetryCurrent: true }
            && _body.State.Travel == SpacecraftTravel.None && !BoardingPending
            && _rider is { Active: true, Attached: true, Walking: true } && LocalCrewAccess && LocalAlive()
            && _body.ContainsCabin(LocalPlayer.Transform.position)
            && seat is >= 0 and < 3 && LocalFirefightingHolder != Guid.Empty;
        ulong actor = available ? _actorIds[seat] : 0;
        bool rackUsed = _extinguisherRack?.Interact(LocalFirefightingHolder, actor, available, out _shipFirefightingPrompt) == true;
        if (!available || !ClientSpacecraftRider.WorldInputAvailable || Cursor.visible
            || CurrentFirefightingState is not { } state) return rackUsed;
        var camera = LocalPlayer.MainCam;
        if (camera == null || !camera || !camera.isActiveAndEnabled) return rackUsed;
        int held = -1;
        for (int i = 0; i < state.Items.Length; i++)
            if (state.Items[i].Place == BoxStationExtinguisherPlace.Held
                && state.Items[i].Holder == LocalFirefightingHolder && state.Items[i].Actor == actor)
            { held = i; break; }
        if (held < 0) return rackUsed;
        var item = state.Items[held];
        if (_shipFirefightingReadout.Length == 0 || item.Water != _shipReadoutState.Water || item.Safety != _shipReadoutState.Safety
            || (item.CooldownSeconds > 0) != (_shipReadoutState.CooldownSeconds > 0))
        {
            _shipReadoutState = item;
            string condition = item.Safety ? "Safety on" : item.Water < 1 ? "Empty"
                : item.CooldownSeconds > 0 ? "Cooling down" : "Ready";
            _shipFirefightingReadout = $"Extinguisher {item.Water:0.#}/{BoxStationFirefighting.ExtinguisherCapacity:0} · {condition} · LMB spray · R safety";
        }
        _shipFirefightingPrompt = _shipFirefightingPrompt.Length == 0 ? _shipFirefightingReadout
            : _shipFirefightingPrompt + "\n" + _shipFirefightingReadout;
        if (Input.GetKeyDown(KeyCode.R))
            CommandFirefighting(BoxStationFirefightingControl.Safety, 0, Vector3.zero);
        else if (Input.GetKeyDown(KeyCode.G))
            _shipFirefightingPrompt = _feedback = "Return the extinguisher to its cradle.";
        else if (Input.GetMouseButton(0) && Time.unscaledTime >= _nextShipSpray
            && !item.Safety && item.Water >= 1 && item.CooldownSeconds <= 0)
        {
            var aim = _body!.Root.transform.InverseTransformDirection(camera.transform.forward);
            if (Finite(aim) && aim.sqrMagnitude is >= .98f and <= 1.02f)
            {
                _nextShipSpray = Time.unscaledTime + BoxStationFirefighting.SprayCooldownSeconds;
                CommandFirefighting(BoxStationFirefightingControl.ShipSpray, 0, aim.normalized);
            }
        }
        return rackUsed || Input.GetKeyDown(KeyCode.R) || Input.GetKeyDown(KeyCode.G) || Input.GetMouseButton(0);
    }

    private string ExecuteShipFirefighting(Peer peer, BoxStationFirefightingControl control, int target, Vector3 aim)
    {
        if (_shipId == Guid.Empty || _body is not { Ready: true, TelemetryCurrent: true } || _extinguisherRack == null
            || _travel != SpacecraftTravel.None || _body.State.Travel != SpacecraftTravel.None
            || !PeerCurrent(peer) || !HasCrewAccess(peer) || !peer.Attached || !peer.Walking
            || !peer.Prepared || !peer.EntryPrepared || peer.Seat is < 0 or > 2
            || !ReferenceEquals(_seats[peer.Seat], peer) || peer.Lease == Guid.Empty
            || !TryActor(peer, out var feet, out var rotation, out var actor) || !CabinActorCurrent(peer, feet)
            || !_body.ContainsCabin(feet)) return "Stand inside the ship with a current walking attachment.";
        var tools = _stationSystems.Firefighting;
        bool take = control == BoxStationFirefightingControl.ShipTake;
        int id = take ? target : tools.Held(peer.Lease);
        if ((uint)id >= (uint)tools.State.Items.Length) return "Hold the extinguisher you want to use.";
        var item = tools.State.Items[id];
        if (take && (item.Place != BoxStationExtinguisherPlace.ShipStored || item.ShipSlot != 0))
            return "That extinguisher is no longer in the cradle.";
        if (!take && (item.Place != BoxStationExtinguisherPlace.Held || item.Actor != actor))
            return "The held extinguisher changed.";
        if (control == BoxStationFirefightingControl.ShipSpray)
            return SprayShipWater(peer, feet, rotation, actor, aim.normalized);
        if (control == BoxStationFirefightingControl.Safety)
        {
            string status = tools.ToggleSafety(peer.Lease);
            MarkFirefightingDirty();
            return status;
        }
        var point = _extinguisherRack.ControlPoint(take);
        var eye = feet + _body.Root.transform.up * 1.3f;
        if ((point - eye).sqrMagnitude > 2.4f * 2.4f
            || !_body.CanSeeControl(eye, point, -1, ActorTransform(peer), null, _extinguisherRack.Solid))
            return "Move within clear reach of the extinguisher cradle.";
        if (take)
        {
            if (HasHeldEngineering(peer.Lease, actor)) return "Put down the engineering tool first.";
            string failure = OtherPortableInHand(peer.Lease, actor);
            if (failure.Length != 0) return failure;
        }
        CancelEngineeringWork(peer);
        if (_manualRepairInputs[peer.Seat] is { } repair) StopManualRepair(repair);
        string result = take ? tools.TakeShipExtinguisher(id, peer.Lease, actor) : tools.StoreShipExtinguisher(peer.Lease, actor);
        MarkFirefightingDirty();
        return result;
    }

    private bool CanRetainSuspendedShipPortable(Peer peer)
    {
        if (!peer.AttachmentSuspended || peer.Attached || !peer.EntryPrepared || peer.ForceReturn
            || _shipId == Guid.Empty || peer.Seat is < 0 or > 2 || peer.Lease == Guid.Empty
            || !ReferenceEquals(_seats[peer.Seat], peer)
            || !_movement.TryGetValue(peer, out var evidence) || !evidence.Active || evidence.ReturnIssued
            || evidence.Ship != _shipId || !MovementCaptureRetained(peer, evidence)) return false;
        if (evidence.ActorPointer != IntPtr.Zero
            && ActorTransform(peer) is { } actor && !actor.WasCollected && actor
            && actor.Pointer != evidence.ActorPointer) return false;
        if (peer.Walking) return evidence.CabinActive && evidence.CabinLease == peer.Lease;
        var history = _movementSeats[peer.Seat];
        return history.Lease == peer.Lease && history.Epoch == _epoch && history.Ship == _shipId;
    }

    private bool CanRetainShipPortable(Peer peer, Vector3 feet)
    {
        if (_body is not { Ready: true, TelemetryCurrent: true } || !peer.Prepared || !peer.EntryPrepared
            || peer.Seat is < 0 or > 2 || !ReferenceEquals(_seats[peer.Seat], peer) || peer.Lease == Guid.Empty) return false;
        if (peer.Walking)
        {
            if (_body.State.Travel != SpacecraftTravel.None || !peer.Attached
                || !CabinActorCurrent(peer, feet) || !_body.ContainsCrewWalkArea(feet)) return false;
        }
        else if ((!peer.Attached || !ActorAtSeat(peer)) && Now > peer.DistanceGrace) return false;
        return true;
    }

    private bool CanRetainShipExtinguisher(Peer peer, ulong actor, Vector3 feet)
    {
        if (!CanRetainShipPortable(peer, feet)) return false;
        foreach (var item in _stationSystems.Firefighting.State.Items)
            if (item.Place == BoxStationExtinguisherPlace.Held && item.Holder == peer.Lease && item.Actor == actor) return true;
        return false;
    }

    private string SprayShipWater(Peer peer, Vector3 feet, Quaternion rotation, ulong actor, Vector3 aim)
    {
        var frame = _body!.Root.transform;
        var actorTransform = ActorTransform(peer);
        if (actorTransform == null || !actorTransform) return "The player attachment changed.";
        var originWorld = feet + frame.up * 1.25f + rotation * Vector3.right * .22f + rotation * Vector3.forward * .32f;
        var eye = feet + frame.up * 1.3f;
        var muzzle = originWorld - eye;
        if (!TraceWater(peer, eye, muzzle.normalized, muzzle.magnitude, out var muzzleRange)
            || muzzleRange + .01f < muzzle.magnitude) return "Move the extinguisher clear of the obstruction.";
        var origin = frame.InverseTransformPoint(originWorld);
        if (!FirefightingPoint(origin, true)) return "Aim from inside the ship.";
        var right = Vector3.Cross(MathF.Abs(aim.y) > .95f ? Vector3.forward : Vector3.up, aim).normalized;
        var up = Vector3.Cross(aim, right).normalized;
        var endpoints = new Vector3[5];
        Span<SpacecraftCompartment> sections = stackalloc SpacecraftCompartment[5];
        sections.Fill((SpacecraftCompartment)byte.MaxValue);
        for (int ray = 0; ray < endpoints.Length; ray++)
        {
            var direction = (aim + (ray switch
            { 1 => right * .06f, 2 => -right * .06f, 3 => up * .06f, 4 => -up * .06f, _ => Vector3.zero })).normalized;
            if (!TraceWater(peer, originWorld, frame.TransformDirection(direction), 6, out var length))
                return "The spray path could not be resolved.";
            endpoints[ray] = origin + direction * length;
            if (!FirefightingPoint(endpoints[ray], true)) return "The spray path is outside the ship's local frame.";
            float nearest = length + 1;
            for (int section = 0; section < 3; section++)
            {
                var compartment = (SpacecraftCompartment)section;
                var delta = SpacecraftFireEffects.Source(compartment) - origin;
                float along = Vector3.Dot(delta, direction);
                if (along < 0 || along > length || along >= nearest
                    || (delta - direction * along).sqrMagnitude > .35f * .35f) continue;
                // The hotspot must also be visible; its radius cannot reach around a wall or closed hatch.
                if (!TraceWater(peer, originWorld, frame.TransformDirection(delta.normalized), delta.magnitude, out var sourceRange))
                    return "The fire's spray path could not be resolved.";
                if (sourceRange + .01f < delta.magnitude) continue;
                sections[ray] = compartment;
                nearest = along;
            }
        }
        if (!_systems.SprayFire(_stationSystems.Firefighting, peer.Lease, actor, sections,
            out int item, out float units, out string status)) return status;
        BroadcastFirefightingBurst(item, origin, endpoints, units, true);
        return status;
    }

    private void SetShipExtinguisherSpray(int item, float amount)
    {
        if (_firefightingHands?.UsesStation(_orbitalStation) == true)
            _orbitalStation!.SetExtinguisherSpray(item, amount);
        else _extinguisherRack?.SetExtinguisherSpray(item, amount);
    }

    private void DisposeShipFirefighting()
    {
        _shipWaterEffects?.Dispose(); _shipWaterEffects = null;
        _body?.SetExtinguisherRackSolid(null);
        var rack = _extinguisherRack;
        _extinguisherRack = null;
        _shipFirefightingPrompt = "";
        _nextShipSpray = 0;
        rack?.Dispose();
    }
}
