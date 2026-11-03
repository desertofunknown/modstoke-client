// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftService
{
    private bool _crewLocked, _clientCrewAccess;
    private long _nextCrewRequestUpdate;
    private string _crewRequestLabel = "";
    private string _crewRequestKey = "";
    private Il2CppStructArray<RaycastHit>? _accessHits;

    // Session access stays on the Unity thread; pressure-door state belongs to the systems worker.
    private SpacecraftSystemState SystemsState(SpacecraftSystemState state) =>
        state with { CrewLocked = _crewLocked };

    private bool HasCrewAccess(Peer peer) => !_crewLocked || ReferenceEquals(peer, _local)
        || peer.CrewApproved;
    private bool LocalCrewAccess => IsClient ? _clientCrewAccess : _local != null && HasCrewAccess(_local);

    private void ResetAccess()
    {
        _crewLocked = _clientCrewAccess = false;
        _nextCrewRequestUpdate = 0;
        _crewRequestLabel = "";
        _crewRequestKey = "";
        _accessHits = null;
        foreach (var peer in _peers.Values)
        {
            peer.CrewApproved = false;
            peer.CrewRequestedAt = 0;
        }
    }

    private static bool IsAccessAction(ActionCode action) => action is ActionCode.CardReader
        or ActionCode.RetinaScanner or ActionCode.CrewLock or ActionCode.ApproveCrew or ActionCode.DenyCrew;

    private bool AtControl(Peer peer, string command, Vector3 actorPosition)
    {
        if (_body == null || !_body.ContainsCabin(actorPosition)) return false;
        var frame = _body.Root.transform;
        var target = frame.TransformPoint(ClientSpacecraftStations.LocalControlPoint(command,
            frame.InverseTransformPoint(actorPosition), _body.State.Systems));
        if ((target - actorPosition).sqrMagnitude > 9) return false;
        var eye = actorPosition + frame.up * 1.3f;
        var delta = target - eye;
        var distance = delta.magnitude - .06f;
        if (distance <= 0) return true;
        var hits = _accessHits ??= new Il2CppStructArray<RaycastHit>(size: 32);
        var count = Physics.RaycastNonAlloc(eye, delta.normalized, hits, distance, ~0, QueryTriggerInteraction.Ignore);
        if (count == hits.Length) return false;
        var actor = ActorTransform(peer);
        for (var index = 0; index < count; index++)
        {
            var collider = hits[index].collider;
            if (!collider || actor != null && actor && collider.transform.IsChildOf(actor)) continue;
            return false;
        }
        return true;
    }

    private Peer? PendingCrew()
    {
        Peer? oldest = null;
        foreach (var peer in _peers.Values)
        {
            if (peer.CrewRequestedAt == 0) continue;
            if (Now - peer.CrewRequestedAt > 60000 || peer.CrewApproved
                || !TryActor(peer, out var position, out _, out _) || !AtControl(peer, "card-reader", position))
            {
                peer.CrewRequestedAt = 0;
                continue;
            }
            if (oldest == null || peer.CrewRequestedAt < oldest.CrewRequestedAt) oldest = peer;
        }
        return oldest;
    }

    private string CrewRequestLabel()
    {
        if (IsClient) return "";
        if (Now >= _nextCrewRequestUpdate)
        {
            _nextCrewRequestUpdate = Now + 500;
            var pending = PendingCrew();
            _crewRequestLabel = pending?.Owner.ToString() ?? "";
            _crewRequestKey = pending == null ? "" : CrewRequestKey(pending);
        }
        return _crewRequestLabel;
    }

    private static string CrewRequestKey(Peer peer) => peer.Token.ToString("N") + ":" + peer.CrewRequestedAt;

    private string ExecuteAccess(Peer peer, ActionCode action, Vector3 actorPosition, string? requestKey)
    {
        if (_body == null) return "Deploy a spacecraft first.";
        if (_body.State.Travel != SpacecraftTravel.None) return "Wait for the active journey to finish.";
        var command = action switch
        {
            ActionCode.CardReader => "card-reader", ActionCode.RetinaScanner => "retina-scanner",
            ActionCode.CrewLock => "crew-lock", ActionCode.ApproveCrew => "approve-crew", _ => "deny-crew"
        };
        if (!AtControl(peer, command, actorPosition)) return "Approach the matching access control.";
        if (peer.Seat < 0 && !Stationary(_body.State)) return "Use a reserved cabin attachment while the ship is moving.";
        RecordStationInteraction(peer, command, actorPosition);
        if (action == ActionCode.CardReader)
        {
            if (ReferenceEquals(peer, _local) || peer.CrewApproved) return "Crew authorization confirmed for this session.";
            peer.CrewRequestedAt = Now;
            _nextCrewRequestUpdate = 0;
            return "Crew request sent. Stay at the reader while the host approves it.";
        }
        if (action == ActionCode.RetinaScanner)
        {
            // Fictional scanner presentation uses the current game session,
            // never an image, biometric record or a claimed inventory item.
            if (!HasCrewAccess(peer)) return "Crew authorization required. Request access at the card reader.";
            if (!_systems.State.CabinDoorOpen && !_systems.TryCommand("cabin-door", _body.State,
                out var interlock, _body.InnerDoorClosed, _body.CabinDoorClosed)) return interlock;
            _body.SetSystemsState(SystemsState(_systems.State));
            return _crewLocked ? "Crew identity accepted. Cabin opening." : "Open access. Cabin opening.";
        }
        if (!ReferenceEquals(peer, _local) || IsClient) return "Only the host can manage crew access.";
        if (action == ActionCode.CrewLock)
        {
            if (!Stationary(_body.State) || _body.State.Powered) return "Park and switch the engine off before changing crew access.";
            if (!_crewLocked)
                foreach (var occupant in _seats)
                    if (occupant != null && !ReferenceEquals(occupant, _local) && !occupant.CrewApproved)
                        return "Approve seated guests at the reader before enabling the crew lock.";
            _crewLocked = !_crewLocked;
            _body.SetSystemsState(SystemsState(_systems.State));
            return _crewLocked ? "Crew lock enabled. Exits remain available." : "Crew lock disabled. Access is open.";
        }
        var pending = PendingCrew();
        if (pending == null) return "No current crew request at the reader.";
        if (requestKey != CrewRequestKey(pending))
        {
            _nextCrewRequestUpdate = 0;
            return "The crew request changed. Check the current request before approving it.";
        }
        pending.CrewRequestedAt = 0;
        _nextCrewRequestUpdate = 0;
        pending.CrewApproved = action == ActionCode.ApproveCrew;
        pending.Result = pending.CrewApproved ? "Crew access approved for this ship and session." : "Crew access request declined.";
        return pending.Result;
    }

    private bool AllowsLockedAction(Peer peer, ActionCode action, Vector3 actorPosition)
    {
        if (HasCrewAccess(peer) || IsAccessAction(action)) return true;
        if (action is ActionCode.Exit or ActionCode.Stand or ActionCode.Return or ActionCode.CancelBoarding) return true;
        if (_body == null || !_body.ContainsCabin(actorPosition)) return false;
        // An unapproved passenger may leave, but cannot close a door on crew.
        return action switch
        {
            ActionCode.CabinDoor => !_systems.State.CabinDoorOpen
                && _body.Root.transform.InverseTransformPoint(actorPosition).z > -4.84f,
            ActionCode.InnerDoor => !_systems.State.InnerDoorOpen,
            ActionCode.Door => !_body.State.DoorOpen,
            ActionCode.Power => _body.State.Powered && Stationary(_body.State),
            _ => false
        };
    }
}
