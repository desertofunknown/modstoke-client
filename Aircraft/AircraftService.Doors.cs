// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientAircraftService
{
    // Hinges travel 65 degrees at 100 degrees/second. Each recipient observes
    // the opening for ordinary exits, even when an earlier snapshot was lost.
    private const long DoorOpeningTime = 850;
    private const long TransitionLifetime = 12000;
    private const byte PortDoor = 1, StarboardDoor = 2;
    private readonly long[] _doorOpenedAt = new long[2];
    private readonly long[] _doorHoldUntil = new long[2];
    private byte _doorMask, _clientDoor;
    private long _doorsClosedAt, _nextSeatStatus;
    private Guid _exitedLease;
    private SeatPhase _clientPhase;
    private byte _entryDoorMask;
    private readonly long[] _entryDoorUntil = new long[2];
    private Il2CppReferenceArray<Collider>? _doorClearance;
    private long _nextEntryDoorCheck;

    private enum SeatPhase : byte { None, Boarding, Seated, OpeningExit, Leaving }

    private bool DoorsSafe(AircraftFlightState flight)
    {
        if (!IsClient)
        {
            if (ExitSafe(flight)) return true;
            foreach (var peer in _seats)
                if (peer != null && peer.EmergencyExit
                    && peer.Phase is SeatPhase.OpeningExit or SeatPhase.Leaving) return true;
            return false;
        }
        if (flight.Grounded) return flight.Velocity.sqrMagnitude < 4;
        // The authenticated host authorizes hull-supported and emergency exits.
        // A replica observes that transition; it does not re-decide host terrain.
        return Now - _lastSnapshot < 1000
            && _clientLease != Guid.Empty && _clientSeat is >= 0 and <= 2
            && _clientPhase is SeatPhase.OpeningExit or SeatPhase.Leaving;
    }

    private static int DoorIndex(byte door) => door == StarboardDoor ? 1 : 0;
    private byte ExitDoor(Vector3 exit) => _body!.Root.transform.InverseTransformPoint(exit).x > 0
        ? StarboardDoor : PortDoor;

    private void BeginBoarding(Peer peer, Vector3 actorPosition)
    {
        peer.Phase = SeatPhase.Boarding;
        peer.EmergencyExit = false;
        var local = _body!.Root.transform.InverseTransformPoint(actorPosition);
        peer.Door = MathF.Abs(local.x) < .1f
            ? peer.Seat == 1 ? StarboardDoor : PortDoor
            : local.x > 0 ? StarboardDoor : PortDoor;
        peer.DoorReadyAt = Now + DoorOpeningTime;
        peer.TransitionExpiresAt = Now + TransitionLifetime;
        peer.LastSeatSequence = 0;
        RefreshDoorTargets(Now);
        _nextSnapshot = 0;
    }

    private void BeginExit(Peer peer, Vector3 exit, bool emergency = false)
    {
        peer.Phase = emergency ? SeatPhase.Leaving : SeatPhase.OpeningExit;
        peer.EmergencyExit = emergency;
        peer.NextExitEvidence = 0;
        peer.Door = ExitDoor(exit);
        peer.Exit = emergency ? exit : null;
        peer.DoorReadyAt = Now + (emergency ? 250 : DoorOpeningTime);
        peer.TransitionExpiresAt = Now + TransitionLifetime;
        peer.Input = peer.Input with { Throttle = 0, Brake = true };
        if (emergency) RecordExitEvidence(peer, exit, Now);
        RefreshDoorTargets(Now);
        _nextSnapshot = 0;
    }

    private void UpdateDoorTransitions(long now)
    {
        if (_body == null) return;
        var flight = _body.State;
        var safe = DoorsSafe(flight);
        for (var i = 0; i < _seats.Length; i++)
        {
            var peer = _seats[i];
            if (peer == null || peer.Phase is SeatPhase.None or SeatPhase.Seated) continue;
            var transitionSafe = peer.Phase == SeatPhase.Boarding
                ? flight.Grounded && flight.Velocity.sqrMagnitude < 4
                : peer.EmergencyExit || ExitSafe(flight);
            if (!transitionSafe || now >= peer.TransitionExpiresAt)
            {
                var reason = !transitionSafe ? "The plane moved. The cabin door was secured."
                    : "The cabin transition timed out. Please try again.";
                if (peer.Phase == SeatPhase.Boarding && !PlayerAttached(peer)) CancelBoarding(peer);
                else RetainSeat(peer);
                TransitionResult(peer, reason);
                continue;
            }
            if (peer.Phase == SeatPhase.Leaving)
            {
                // A moving wreck must not leave an old point behind while the
                // owner's exit acknowledgement is still in flight.
                if (peer.EmergencyExit && now >= peer.DoorReadyAt)
                {
                    peer.DoorReadyAt = now + 250;
                    if (TryDeparture(peer.Seat, true, out var movingExit)) peer.Exit = movingExit;
                }
                if (peer.Exit.HasValue) RecordExitEvidence(peer, peer.Exit.Value, now);
                continue;
            }
            if (peer.Phase != SeatPhase.OpeningExit || now < peer.DoorReadyAt || !DoorVisibleReady(peer.Door)) continue;
            if (!TryDeparture(peer.Seat, peer.EmergencyExit, out var exit))
            {
                HoldDoorAfterTransition(peer);
                RetainSeat(peer);
                TransitionResult(peer, "The cabin exit became blocked. Move to clear ground.");
                continue;
            }
            var door = ExitDoor(exit);
            if (peer.Door != door)
            {
                // The alternative exit must open before it can replace a newly
                // obstructed one. Never teleport through the closed opposite door.
                peer.Door = door;
                peer.DoorReadyAt = now + DoorOpeningTime;
                continue;
            }
            peer.Exit = exit;
            peer.Phase = SeatPhase.Leaving;
            peer.DoorReadyAt = now + 100;
            RecordExitEvidence(peer, exit, now);
            _nextSnapshot = 0;
        }
        if (!safe)
        {
            Array.Clear(_doorHoldUntil);
            _entryDoorMask = 0;
        }
        else if (_entryDoorMask != 0 && now >= _nextEntryDoorCheck)
        {
            _nextEntryDoorCheck = now + 100;
            if (!HasUnseatedCabinPlayer())
                for (var side = 0; side < 2; side++)
                    if ((_entryDoorMask & (1 << side)) != 0 && now >= _entryDoorUntil[side] && EntryDoorClear(side))
                        _entryDoorMask &= (byte)~(1 << side);
        }
        RefreshDoorTargets(now);
    }

    private void RefreshDoorTargets(long now)
    {
        byte mask = 0;
        if (_body != null && DoorsSafe(_body.State))
        {
            mask = _entryDoorMask;
            if (_doorHoldUntil[0] > now) mask |= PortDoor;
            if (_doorHoldUntil[1] > now) mask |= StarboardDoor;
            foreach (var peer in _seats)
                if (peer != null && (peer.Phase is SeatPhase.OpeningExit or SeatPhase.Leaving
                    || peer.Phase == SeatPhase.Boarding && _body.State.Grounded))
                    mask |= peer.Door;
        }
        ApplyDoorMask(mask);
    }

    private string ChangeEntryDoor(Peer peer, Vector3 actorPosition, ActionCode action)
    {
        if (_body == null || peer.Seat >= 0) return "Leave your seat before using the cabin door.";
        var side = action is ActionCode.OpenStarboardDoor or ActionCode.CloseStarboardDoor ? 1 : 0;
        if (!_body.CanReachDoor(actorPosition, side, ActorTransform(peer)))
            return "Approach that cabin door with a clear path.";
        var bit = (byte)(1 << side);
        var opening = action is ActionCode.OpenPortDoor or ActionCode.OpenStarboardDoor;
        if (!opening)
        {
            foreach (var seated in _seats)
                if (seated != null && seated.Door == bit && seated.Phase is SeatPhase.Boarding or SeatPhase.OpeningExit or SeatPhase.Leaving)
                    return "Wait for the cabin transition to finish.";
            if (!EntryDoorClear(side)) return "Step clear of the door before closing it.";
            _entryDoorMask &= (byte)~bit;
        }
        else
        {
            if (!EntryDoorClear(side, opening: true)) return "Clear the door's swing before opening it.";
            _entryDoorMask |= bit;
            _entryDoorUntil[side] = Now + 15000;
        }
        RefreshDoorTargets(Now);
        return opening ? "Cabin door opening." : "Cabin door closing.";
    }

    private bool EntryDoorClear(int side, bool opening = false)
    {
        if (_body == null) return false;
        var frame = _body.Root.transform;
        var overlaps = _doorClearance ??= new Il2CppReferenceArray<Collider>(size: 32);
        var center = new Vector3(side == 0 ? -1.35f : 1.35f, .76f, .32f);
        var halfSize = new Vector3(.75f, .66f, .83f);
        var rotation = Quaternion.identity;
        if (opening)
        {
            halfSize = AircraftLayout.DoorSize * .5f;
            var radius = new Vector2(MathF.Abs(AircraftLayout.DoorCenter.x) + halfSize.x,
                MathF.Abs(AircraftLayout.DoorCenter.z) + halfSize.z).magnitude;
            // Every intermediate leaf pose is within 2.5 degrees of a sample.
            // Pad its horizontal bounds by the furthest corner's arc distance.
            var padding = radius * 2.5f * Mathf.Deg2Rad;
            halfSize += new Vector3(padding, 0, padding);
        }
        // Closing retains the full threshold clearance. Opening checks only the
        // swept leaf, so standing beside an unused corner does not block it.
        for (var sample = 0; sample <= (opening ? 13 : 0); sample++)
        {
            if (opening)
            {
                rotation = Quaternion.Euler(0, (side == 0 ? 1 : -1) * sample * 5, 0);
                center = AircraftLayout.DoorHinge(side) + rotation * AircraftLayout.DoorCenter;
            }
            var count = Physics.OverlapBoxNonAlloc(frame.TransformPoint(center), halfSize,
                overlaps, frame.rotation * rotation, ~0, QueryTriggerInteraction.Ignore);
            if (count == overlaps.Length) return false;
            for (var index = 0; index < count; index++)
                if (ExitPathObstacle(overlaps[index])) return false;
        }
        return true;
    }

    private void ApplyDoorMask(byte mask)
    {
        var now = Now;
        for (var index = 0; index < 2; index++)
        {
            var bit = 1 << index;
            if ((mask & bit) != 0 && (_doorMask & bit) == 0) _doorOpenedAt[index] = now;
            if ((mask & bit) == 0) _doorOpenedAt[index] = 0;
        }
        if (_doorMask != mask)
        {
            // Keep the brakes on through the closing animation as well.
            if ((_doorMask & ~mask) != 0) _doorsClosedAt = now + DoorOpeningTime;
            _doorMask = mask;
            _nextSnapshot = 0;
        }
        _body?.SetDoorOpen((mask & PortDoor) != 0, (mask & StarboardDoor) != 0);
    }

    private bool DoorVisibleReady(byte door) => _body != null && DoorsSafe(_body.State)
        && door is PortDoor or StarboardDoor && (_doorMask & door) != 0
        && Now - _doorOpenedAt[DoorIndex(door)] >= DoorOpeningTime
        && _body.DoorOpen(door == StarboardDoor);

    private void HoldDoorAfterTransition(Peer peer)
    {
        if (peer.Phase is SeatPhase.None or SeatPhase.Seated || peer.Door == 0
            || _body == null || !DoorsSafe(_body.State)) return;
        var index = DoorIndex(peer.Door);
        _doorHoldUntil[index] = Math.Max(_doorHoldUntil[index], Now + 450);
    }

    private bool PlayerAttached(Peer peer)
    {
        if (ReferenceEquals(peer, _local)) return _ridingLease == peer.Lease && _rider?.Attached == true;
        if (_body == null || !TryActor(peer, out var position, out _, out _)) return false;
        var seatOrigin = _body.Root.transform.TransformPoint(AircraftLayout.Seat(peer.Seat) + Vector3.down * .8f);
        return (position - seatOrigin).sqrMagnitude <= .25f;
    }

    private void RetainSeat(Peer peer)
    {
        peer.Phase = SeatPhase.Seated;
        peer.EmergencyExit = false;
        peer.Exit = null;
        peer.TransitionExpiresAt = peer.DoorReadyAt = 0;
    }

    private void CancelBoarding(Peer peer)
    {
        // The actor may already be attached while its acknowledgement is in transit.
        // Do not re-enable its collision in the hull when a qualified exit is available.
        if (PlayerAttached(peer))
        {
            if (TryDeparture(peer.Seat, true, out var exit)) BeginExit(peer, exit, true);
            else RetainSeat(peer);
        }
        else ReleaseSeat(peer, null);
    }

    private void SyncSeatTransition(long now)
    {
        var lease = IsClient ? _clientLease : _local?.Lease ?? Guid.Empty;
        if (lease == Guid.Empty || _body == null || _rider == null) return;
        var phase = IsClient ? _clientPhase : _local?.Phase ?? SeatPhase.None;
        var door = IsClient ? _clientDoor : _local?.Door ?? 0;
        if (phase == SeatPhase.Seated && _exitedLease == lease)
        {
            // An exit acknowledgement can be lost while the host conservatively
            // keeps the occupied lease. The player has already completed this exit.
            if (_pending == null) Command("cancel");
            return;
        }
        var emergency = IsClient
            ? Now - _lastSnapshot < 1000 && (!_body.State.Grounded || EmergencyExitAvailable(_body.State) || _rider.Faulted)
            : _local?.EmergencyExit == true;
        if (phase == SeatPhase.Leaving && _exitedLease != lease && _ridingLease == lease
            && (emergency || DoorVisibleReady(door)))
        {
            var exit = IsClient ? _clientExit : _local?.Exit;
            if (!exit.HasValue) return;
            if (!_rider.End(exit, true)) return;
            _ridingLease = Guid.Empty;
            _exitedLease = lease;
            _nextSeatStatus = 0;
        }
        var attached = phase == SeatPhase.Boarding && _ridingLease == lease && _rider.Attached;
        var exited = phase == SeatPhase.Leaving && _exitedLease == lease;
        if ((!attached && !exited) || now < _nextSeatStatus) return;
        _nextSeatStatus = now + 200;
        if (IsClient) _event?.SeatStatus(++_controlSequence, lease, attached);
        else if (_local != null) ReceiveSeatStatus(_local, ++_controlSequence, lease, attached);
    }

    private void ReceiveSeatStatus(Peer peer, long sequence, Guid lease, bool attached)
    {
        if (_body == null || peer.Seat < 0 || lease == Guid.Empty || peer.Lease != lease
            || !ReferenceEquals(_seats[peer.Seat], peer) || sequence <= peer.LastSeatSequence
            || !TryActor(peer, out var position, out _, out _)) return;
        if (attached)
        {
            if (peer.Phase != SeatPhase.Boarding || Now < peer.DoorReadyAt || !DoorVisibleReady(peer.Door)) return;
            var seatOrigin = _body.Root.transform.TransformPoint(AircraftLayout.Seat(peer.Seat) + Vector3.down * .8f);
            if ((position - seatOrigin).sqrMagnitude > 2.5f * 2.5f) return;
            peer.LastSeatSequence = sequence;
            HoldDoorAfterTransition(peer);
            peer.Phase = SeatPhase.Seated;
            peer.BoardedAt = Now;
            peer.TransitionExpiresAt = peer.DoorReadyAt = 0;
            if (_entryDoorMask != 0 && !HasUnseatedCabinPlayer(true))
                for (var side = 0; side < 2; side++) _entryDoorUntil[side] = Now;
            TransitionResult(peer, "Boarded. Securing the cabin door.");
        }
        else
        {
            if (peer.Phase != SeatPhase.Leaving || !peer.Exit.HasValue) return;
            peer.LastSeatSequence = sequence;
            ReleaseSeat(peer, peer.Exit);
            TransitionResult(peer, "You have left the cabin.");
        }
        RefreshDoorTargets(Now);
    }

    private void RecordExitEvidence(Peer peer, Vector3 exit, long now)
    {
        if (now < peer.NextExitEvidence || !TryActor(peer, out _, out _, out _) || peer.Lease == Guid.Empty) return;
        peer.NextExitEvidence = now + 50;
        var history = _seatHistory[peer.Seat];
        if (history.Lease != peer.Lease || history.Epoch != _epoch || history.Aircraft != _planeId)
            history.Begin(peer.Lease, _epoch, _planeId);
        history.Record(exit, now);
    }

    private void TransitionResult(Peer peer, string result)
    {
        peer.Result = result;
        if (ReferenceEquals(peer, _local)) _feedback = result;
        _nextSnapshot = 0;
    }

    private void ResetDoors()
    {
        _body?.SetDoorOpen(false, false);
        _exitRestBody = null;
        _exitRestSince = _nextExitRestCheck = 0;
        _doorMask = _clientDoor = 0;
        _clientPhase = SeatPhase.None;
        _doorsClosedAt = _nextSeatStatus = 0;
        _exitedLease = Guid.Empty;
        _entryDoorMask = 0;
        _nextEntryDoorCheck = 0;
        Array.Clear(_entryDoorUntil);
        _doorClearance = null;
        Array.Clear(_doorOpenedAt);
        Array.Clear(_doorHoldUntil);
    }
}
