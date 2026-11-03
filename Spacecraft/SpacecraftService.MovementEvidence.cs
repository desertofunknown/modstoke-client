// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Modstoke.Sons;
using UnityEngine;
using NumericsVector = System.Numerics.Vector3;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftService
{
    private const int MovementHistoryCount = 32;
    private const long MovementHistoryLifetime = 1200;
    private const float SeatToleranceSquared = 2.5f * 2.5f;
    private const long EndpointLifetime = 10000;
    private static readonly Tuple<bool, Guid> NoMovementEvidence = Tuple.Create(false, Guid.Empty);
    private readonly Dictionary<Peer, MovementEvidence> _movement = new();
    private readonly MovementSeatHistory[] _movementSeats = { new(), new(), new() };
    private readonly MovementShipFrame[] _movementFrames = new MovementShipFrame[MovementHistoryCount];
    private int _movementFrameIndex, _movementFrameCount;
    private Guid _movementShip;
    private long _nextMovementSample;

    // Called after host validation of a nearby actor and a grounded ramp exit.
    // The island origin is never supplied by a packet or replaced on destination reboarding.
    private bool BeginMovementExpedition(Peer peer, Vector3 position, Quaternion rotation)
    {
        if (IsClient || _body == null || !_body.Ready || _shipId == Guid.Empty) return false;
        if (_movement.TryGetValue(peer, out var prior) && CurrentMovement(peer, prior)
            && prior.Active && !prior.ReturnIssued)
            return (prior.IslandPosition - position).sqrMagnitude <= .75f * .75f;
        if (_body.State.Region != SpacecraftRegion.Island || position.y >= 2000
            || !Finite(position) || !ValidRotation(rotation)
            || !TryActor(peer, out var current, out _, out var actor)
            || !(_body.ContainsCabin(current) || AtRampApproach(current))
            || (current - position).sqrMagnitude > 60 * 60) return false;
        if (_movementShip != _shipId) ResetMovementEvidence();
        _movementShip = _shipId;
        if (!TryGround(position + Vector3.up * .25f, 3, out var ground)) return false;
        _movement[peer] = new MovementEvidence(peer, _epoch, _shipId, actor, position)
        { FootActive = true, FootPoint = current, FootSurface = _body.TryWalkSurface(current, out var surface)
            ? surface.y : ground.point.y, FootAt = Now };
        return true;
    }

    private bool MovementCaptureMatches(Peer peer, MovementEvidence evidence) =>
        evidence.Epoch == _epoch && evidence.Ship == _movementShip && evidence.Expedition != Guid.Empty
        && ReferenceEquals(evidence.Binding, peer.Binding) && evidence.Session == (peer.Session?.SessionId ?? Guid.Empty)
        && evidence.ActorPointer == peer.ActorPointer;

    private bool MovementCaptureRetained(Peer peer, MovementEvidence evidence) =>
        MovementCaptureMatches(peer, evidence) && ReadPeerIdentity(peer) != PeerIdentity.Retired;

    private bool MovementCaptureCurrent(Peer peer, MovementEvidence evidence) =>
        MovementCaptureMatches(peer, evidence) && PeerCurrent(peer);

    private bool CurrentMovement(Peer peer, MovementEvidence evidence)
    {
        if (!MovementCaptureCurrent(peer, evidence)
            || !TryActor(peer, out _, out _, out var actor) || actor != evidence.Actor) return false;
        return !BoltNetwork.isRunning || peer.Binding != null && peer.Binding.NetworkId == evidence.Actor
            && (peer.Session == null ? peer.Binding.IsLocalPlayer : !peer.Binding.IsLocalPlayer
                && ReferenceEquals(peer.Binding.PlayerSession, peer.Session));
    }

    private void RecordMovementEvidence(bool force = false)
    {
        if (!OnThread || !_started || !_ready || IsClient || _body == null || !_body.Ready) return;
        var now = Now;
        if (!force && now < _nextMovementSample) return;
        _nextMovementSample = now + 50;
        var frame = CurrentMovementFrame(now);
        _movementFrames[_movementFrameIndex] = frame;
        _movementFrameIndex = (_movementFrameIndex + 1) % MovementHistoryCount;
        _movementFrameCount = Math.Min(MovementHistoryCount, _movementFrameCount + 1);
        for (var seat = 0; seat < 3; seat++)
        {
            var peer = _seats[seat];
            var history = _movementSeats[seat];
            if (peer == null || peer.Lease == Guid.Empty || !_movement.TryGetValue(peer, out var evidence)
                || !evidence.Active || !MovementCaptureRetained(peer, evidence))
            { history.Reset(); continue; }
            if (!CurrentMovement(peer, evidence)) continue;
            if (history.Lease != peer.Lease || history.Epoch != _epoch || history.Ship != _shipId)
                history.Begin(peer.Lease, _epoch, _shipId);
            var point = SeatMovementOrigin(seat);
            if (!Finite(point)) { history.Reset(); continue; }
            history.Positions[history.Index] = point;
            history.Frames[history.Index] = frame;
            history.Index = (history.Index + 1) % MovementHistoryCount;
            history.Count = Math.Min(MovementHistoryCount, history.Count + 1);
            history.Last = now;
        }
        foreach (var pair in _movement)
        {
            var peer = pair.Key; var evidence = pair.Value;
            if (!CurrentMovement(peer, evidence) || !TryActor(peer, out var point, out _, out _)) continue;
            if (peer.Walking)
            {
                if (CabinWalkingMember(peer, evidence)) AcceptCabinSample(evidence, point, now);
                continue;
            }
            // Keep a short rollback window while a client attempts to sit. It
            // grants no new movement; the previous cabin path must still validate.
            if (now - peer.BoardedAt >= 3000) evidence.CabinActive = false;
            if (evidence.EndpointActive)
            {
                var near = (point - evidence.Endpoint).sqrMagnitude <= (evidence.EndpointReturn ? 16 : 2.5f * 2.5f);
                if (now > evidence.EndpointExpires || evidence.EndpointArrived && !near) evidence.EndpointActive = false;
                if (near) { evidence.EndpointArrived = true; evidence.DepartureActive = false; }
            }
            if (!WalkingMember(peer, evidence) || evidence.FootFailed) continue;
            if (!evidence.FootActive)
            {
                // Wait for the native actor to reach the specific host-approved exit.
                // Its previous seat position cannot seed a walking baseline.
                if (!evidence.EndpointActive || evidence.EndpointReturn || now > evidence.EndpointExpires
                    || (point - evidence.Endpoint).sqrMagnitude > 2.5f * 2.5f
                    || !SurfaceWalkPoint(point, evidence.SurfaceRegion, out var surface)) continue;
                evidence.FootActive = true; evidence.FootPoint = point; evidence.FootSurface = surface.y;
                evidence.FootAt = now;
            }
            else if (now - evidence.FootAt >= 250) AcceptWalkingSample(evidence, point, now);
        }
    }

    private Vector3 SeatMovementOrigin(int seat) => _body!.Root.transform.TransformPoint(
        SpacecraftLayout.Seat(seat) + _body.SeatAdjustmentOffset(seat) + Vector3.down * .8f);

    private bool SeatedActorCurrent(Peer peer, Vector3 point)
    {
        if (!OnThread || !_started || !_ready || IsClient || _body?.Ready != true || !Finite(point)
            || peer.Walking || peer.Seat is < 0 or > 2 || peer.Lease == Guid.Empty
            || !ReferenceEquals(_seats[peer.Seat], peer) || !peer.EntryPrepared || peer.ForceReturn
            || !_movement.TryGetValue(peer, out var evidence) || !evidence.Active || evidence.ReturnIssued
            || evidence.Ship != _shipId || !CurrentMovement(peer, evidence)) return false;
        var history = _movementSeats[peer.Seat];
        var now = Now;
        return history.Lease == peer.Lease && history.Epoch == _epoch && history.Ship == _shipId
            && ((point - SeatMovementOrigin(peer.Seat)).sqrMagnitude <= SeatToleranceSquared
                || now >= history.Last && now - history.Last <= 250
                    && SeatHistoryContains(history, peer.Seat, point, now));
    }

    private bool SeatHistoryContains(MovementSeatHistory history, int seat, Vector3 point, long now)
    {
        var newer = CurrentMovementFrame(now);
        var to = SeatMovementOrigin(seat);
        if ((point - to).sqrMagnitude <= SeatToleranceSquared) return true;
        for (var i = 0; i < history.Count; i++)
        {
            var index = (history.Index - 1 - i + MovementHistoryCount) % MovementHistoryCount;
            var older = history.Frames[index];
            if (older.At > now || now - older.At > MovementHistoryLifetime) break;
            var from = history.Positions[index];
            if ((point - from).sqrMagnitude <= SeatToleranceSquared) return true;
            if (ContinuousMovementSpan(older, newer))
            {
                var travel = to - from;
                var fraction = travel.sqrMagnitude > .000001f
                    ? Mathf.Clamp01(Vector3.Dot(point - from, travel) / travel.sqrMagnitude) : 1;
                if ((point - Vector3.Lerp(from, to, fraction)).sqrMagnitude <= SeatToleranceSquared) return true;
            }
            newer = older;
            to = from;
        }
        return false;
    }

    // Called only for the host-selected standing point, before sending the walk
    // transition. A client position cannot establish or renew this baseline.
    private bool PrepareCabinMovement(Peer peer, Vector3 localPoint)
    {
        if (_body?.Ready != true || peer.Seat is < 0 or > 2 || peer.Lease == Guid.Empty
            || !ReferenceEquals(_seats[peer.Seat], peer) || !peer.Attached || !peer.EntryPrepared
            || !_movement.TryGetValue(peer, out var evidence) || !evidence.Active || evidence.ReturnIssued
            || !CurrentMovement(peer, evidence) || !CabinWalkPoint(localPoint)) return false;
        RecordDepartureOrigin(peer, evidence);
        evidence.CabinSeatLocal = _body.Root.transform.InverseTransformPoint(SeatMovementOrigin(peer.Seat));
        evidence.CabinLocal = evidence.CabinObserved = localPoint;
        evidence.CabinAt = evidence.CabinObservedAt = Now;
        evidence.CabinLease = peer.Lease;
        evidence.CabinActive = true;
        evidence.CabinFailed = false;
        evidence.CabinEntered = false;
        evidence.CabinOutside = false;
        evidence.CabinDelay = 0;
        evidence.FootActive = false;
        return true;
    }

    private bool CabinWalkingMember(Peer peer, MovementEvidence evidence) => peer.Walking
        && peer.Seat is >= 0 and <= 2 && peer.Lease != Guid.Empty
        && ReferenceEquals(_seats[peer.Seat], peer) && peer.CabinStandLocal.HasValue
        && peer.EntryPrepared && !peer.ForceReturn && _body?.Ready == true
        && evidence.Active && !evidence.ReturnIssued && evidence.Ship == _shipId
        && evidence.CabinActive && evidence.CabinLease == peer.Lease && CurrentMovement(peer, evidence);

    private bool CabinWalkerCarried(Peer peer)
    {
        if (!peer.Attached || !peer.Prepared || peer.AttachmentSuspended || _body?.TelemetryCurrent != true) return false;
        if (ReferenceEquals(peer, _local))
        {
            if (_rider is not { Attached: true, Walking: true, Faulted: false, Suspended: false, Incapacitated: false }) return false;
        }
        else if (Now - peer.ReadyAt > 3000 || Now - peer.LastSeen > 6000) return false;
        return TryActor(peer, out var position, out _, out _) && CabinActorCurrent(peer, position, requireInterior: true);
    }

    // Read-only validation shared by the host's distance checks and attachment
    // acknowledgement. The host-issued walking lease, not an earlier ACK, is authority.
    private bool CabinActorCurrent(Peer peer, Vector3 worldPosition, bool requireInterior = false)
    {
        if (!OnThread || !_started || !_ready || IsClient || !Finite(worldPosition)
            || !_movement.TryGetValue(peer, out var evidence) || !CabinWalkingMember(peer, evidence)
            || evidence.CabinFailed || requireInterior && evidence.CabinOutside) return false;
        var now = Now;
        if (AcceptCabinSample(evidence, worldPosition, now, false, requireInterior)) return true;
        if (evidence.CabinEntered || !evidence.DepartureActive || now > evidence.DepartureExpires) return false;
        var frame = _body!.Root.transform;
        var current = frame.InverseTransformPoint(worldPosition);
        if ((current - evidence.CabinSeatLocal).sqrMagnitude <= 1
            && _body!.CrewSeatPathClear(evidence.CabinSeatLocal, current, peer.Seat)) return true;
        var newer = CurrentMovementFrame(now);
        for (var i = 0; i < _movementFrameCount; i++)
        {
            var index = (_movementFrameIndex - 1 - i + MovementHistoryCount) % MovementHistoryCount;
            var older = _movementFrames[index];
            if (older.At > now || now - older.At > MovementHistoryLifetime) break;
            var local = CabinPointOnSegment(worldPosition, evidence.CabinSeatLocal, older, newer, out _);
            // The seated point can overlap its chair; this short allowance is
            // only for that retained berth, never another part of the cabin.
            if ((local - evidence.CabinSeatLocal).sqrMagnitude <= 1
                && _body!.CrewSeatPathClear(evidence.CabinSeatLocal, local, peer.Seat)) return true;
            newer = older;
        }
        return false;
    }

    private bool CabinWalkPoint(Vector3 localPoint)
    {
        if (_body?.Ready != true || !Finite(localPoint)) return false;
        var point = _body.Root.transform.TransformPoint(localPoint);
        if (!_body.CrewCabinStep(localPoint, localPoint) || !_body.TryWalkSurface(point, out var surface)) return false;
        var height = Vector3.Dot(point - surface, _body.Root.transform.up);
        return height >= -.05f && height <= .4f;
    }

    private bool AcceptCabinSample(MovementEvidence evidence, Vector3 point, long now, bool commit = true,
        bool requireInterior = false)
    {
        if (_body?.Ready != true || !evidence.CabinActive || evidence.CabinFailed || !Finite(point)) return false;
        var elapsed = now - evidence.CabinAt;
        if (elapsed < 0) return false;
        if (!evidence.CabinEntered && elapsed > EndpointLifetime)
        { if (commit) evidence.CabinFailed = true; return false; }
        if (evidence.CabinEntered && now - evidence.CabinObservedAt > MovementHistoryLifetime)
        { if (commit) evidence.CabinFailed = true; return false; }
        var delay = evidence.CabinDelay;
        // Keep a stable observation delay once acquired. Repeatedly choosing the
        // closest local point would mistake fore/aft walking for network latency.
        var local = default(Vector3);
        var matched = evidence.CabinEntered && CabinPointAt(point, now - delay, now, out local)
            && CabinStepAllowed(evidence, local, elapsed) && CabinPositionAllowed(evidence, local);
        if (!matched) matched = FindCabinPoint(evidence, point, now, elapsed, out local, out delay);
        // Reject a transient mismatch without replacing the accepted baseline.
        // Recovery still requires a valid step before its observation expires.
        if (!matched || requireInterior && !_body.CrewCapsuleInCabin(local, false)) return false;
        // The old seat remains covered only by the short departure allowance
        // until the actor reaches the particular host-selected standing point.
        if (commit)
        {
            if (_body.State.Region == SpacecraftRegion.Space && evidence.CabinEntered)
            {
                if (!_body.CrewStep(evidence.CabinObserved, local, evidence.CabinOutside, out var outside)) return false;
                evidence.CabinOutside = outside;
            }
            // Track accepted geometry at the sampling cadence without renewing
            // the separate 250ms movement allowance. Turning around a corner
            // follows those segments instead of cutting a chord through the wall.
            evidence.CabinObserved = local;
            evidence.CabinObservedAt = now;
            evidence.CabinDelay = delay;
            if (!evidence.CabinEntered || elapsed >= 250)
            {
                evidence.CabinEntered = true;
                evidence.CabinLocal = local;
                evidence.CabinAt = now;
            }
        }
        return true;
    }

    private bool CabinPositionAllowed(MovementEvidence evidence, Vector3 local)
    {
        if (_body?.Ready != true) return false;
        if (_body.State.Region == SpacecraftRegion.Space && evidence.CabinEntered)
            return _body.CrewStep(evidence.CabinObserved, local, evidence.CabinOutside, out _);
        return CabinWalkPoint(local) && _body.CrewCabinStep(evidence.CabinObserved, local);
    }

    private bool FindCabinPoint(MovementEvidence evidence, Vector3 point, long now, long elapsed,
        out Vector3 local, out long delay)
    {
        var frame = _body!.Root.transform;
        local = frame.InverseTransformPoint(point);
        delay = 0;
        var matched = CabinStepAllowed(evidence, local, elapsed) && CabinPositionAllowed(evidence, local);
        var closest = matched ? evidence.CabinEntered ? evidence.CabinDelay : (local - evidence.CabinLocal).sqrMagnitude
            : float.PositiveInfinity;
        var newer = CurrentMovementFrame(now);
        for (var i = 0; i < _movementFrameCount; i++)
        {
            var index = (_movementFrameIndex - 1 - i + MovementHistoryCount) % MovementHistoryCount;
            var older = _movementFrames[index];
            if (older.At > now || now - older.At > MovementHistoryLifetime) break;
            var candidate = CabinPointOnSegment(point, evidence.CabinLocal, older, newer, out var fraction);
            var candidateDelay = now - older.At - (long)((newer.At - older.At) * fraction);
            var distance = evidence.CabinEntered ? Math.Abs(candidateDelay - evidence.CabinDelay)
                : (candidate - evidence.CabinLocal).sqrMagnitude;
            if (distance < closest && CabinStepAllowed(evidence, candidate, elapsed) && CabinPositionAllowed(evidence, candidate))
            { closest = distance; local = candidate; delay = candidateDelay; matched = true; }
            newer = older;
        }
        return matched;
    }

    private static Vector3 CabinPointOnSegment(Vector3 point, Vector3 expectedLocal,
        MovementShipFrame older, MovementShipFrame newer, out float fraction)
    {
        var from = older.Position + older.Rotation * expectedLocal;
        var to = newer.Position + newer.Rotation * expectedLocal;
        var travel = to - from;
        fraction = travel.sqrMagnitude > .000001f
            ? Mathf.Clamp01(Vector3.Dot(point - from, travel) / travel.sqrMagnitude) : 1;
        // Relocations and frame rebases only match their recorded endpoints,
        // never intermediate points in the gap between coordinate frames.
        if (!ContinuousMovementSpan(older, newer))
            fraction = (point - from).sqrMagnitude < (point - to).sqrMagnitude ? 0 : 1;
        var rotation = Quaternion.Slerp(older.Rotation, newer.Rotation, fraction);
        var position = Vector3.Lerp(older.Position, newer.Position, fraction);
        return Quaternion.Inverse(rotation) * (point - position);
    }

    private bool CabinPointAt(Vector3 point, long at, long now, out Vector3 local)
    {
        local = default;
        if (at > now || now - at > MovementHistoryLifetime) return false;
        var newer = CurrentMovementFrame(now);
        for (var i = 0; i < _movementFrameCount; i++)
        {
            var index = (_movementFrameIndex - 1 - i + MovementHistoryCount) % MovementHistoryCount;
            var older = _movementFrames[index];
            if (older.At > now || now - older.At > MovementHistoryLifetime) break;
            if (at < older.At) { newer = older; continue; }
            var duration = newer.At - older.At;
            var fraction = duration > 0 ? (at - older.At) / (float)duration : 1;
            if (fraction is > 0 and < 1 && !ContinuousMovementSpan(older, newer))
                return false;
            var rotation = Quaternion.Slerp(older.Rotation, newer.Rotation, fraction);
            var position = Vector3.Lerp(older.Position, newer.Position, fraction);
            local = Quaternion.Inverse(rotation) * (point - position);
            return true;
        }
        return false;
    }

    private MovementShipFrame CurrentMovementFrame(long now)
    {
        var frame = _body!.Root.transform;
        return new(frame.position, frame.rotation, _body.State.Velocity, now, _frameRevision);
    }

    private static bool ContinuousMovementSpan(MovementShipFrame older, MovementShipFrame newer)
    {
        var duration = newer.At - older.At;
        if (older.Revision != newer.Revision || duration <= 0 || duration > MovementHistoryLifetime
            || !Finite(older.Position) || !Finite(newer.Position)
            || !ValidRotation(older.Rotation) || !ValidRotation(newer.Rotation)) return false;
        var speed = MathF.Max(older.Velocity.magnitude, newer.Velocity.magnitude);
        if (!float.IsFinite(speed) || speed > 2 * OrbitalSimulation.MaximumSpeed) return false;
        // Physics steps and wall-clock observations can straddle one fixed tick.
        // Recorded velocity also covers flight above the rebase threshold while awaiting an ACK.
        var seconds = duration / 1000f + Math.Clamp(Time.fixedDeltaTime, 0, .1f);
        return (newer.Position - older.Position).magnitude <= speed * seconds + .5f;
    }

    private bool CabinStepAllowed(MovementEvidence evidence, Vector3 local, long elapsed)
    {
        var delta = local - evidence.CabinLocal;
        if (!evidence.CabinEntered) return delta.sqrMagnitude <= .8f * .8f;
        var allowance = 3 * Math.Min(elapsed, 1000) / 1000f + .25f;
        if (_body?.State.Region == SpacecraftRegion.Space) return delta.sqrMagnitude <= allowance * allowance;
        return new Vector2(delta.x, delta.z).sqrMagnitude <= allowance * allowance
            && MathF.Abs(delta.y) <= .5f + allowance * .25f;
    }

    private void PrepareExitMovement(Peer peer, Vector3 exit)
    {
        if (peer.Seat < 0 || peer.Lease == Guid.Empty || _body == null
            || !_movement.TryGetValue(peer, out var evidence) || !CurrentMovement(peer, evidence)) return;
        if (evidence.EndpointLease == peer.Lease) return;
        evidence.Endpoint = exit; evidence.EndpointExpires = Now + EndpointLifetime;
        evidence.EndpointLease = peer.Lease; evidence.EndpointActive = true; evidence.EndpointReturn = false;
        evidence.EndpointArrived = false;
        evidence.SurfaceRegion = _body.State.Region;
        evidence.SurfaceRecovery = null;
        evidence.SurfaceCompleted = 0;
        RecordDepartureOrigin(peer, evidence);
        evidence.CabinActive = false;
        evidence.FootActive = evidence.FootFailed = false;
        evidence.Active = peer.EntryPrepared;
    }

    private void PrepareReturnMovement(Peer? peer)
    {
        if (peer == null || !_movement.TryGetValue(peer, out var evidence)
            || !CurrentMovement(peer, evidence) || evidence.ReturnIssued) return;
        // This deadline belongs to the expedition, not to a client command sequence.
        // Repeated cancellation, heartbeat and failure callbacks cannot renew it.
        evidence.ReturnIssued = true;
        evidence.Endpoint = evidence.IslandPosition;
        evidence.EndpointExpires = Now + EndpointLifetime;
        evidence.EndpointActive = evidence.EndpointReturn = true;
        evidence.EndpointArrived = false;
        RecordDepartureOrigin(peer, evidence);
        evidence.FootActive = false;
    }

    private void RecordDepartureOrigin(Peer peer, MovementEvidence evidence)
    {
        if (peer.Walking && evidence.CabinActive && !evidence.CabinFailed && _body?.Ready == true
            && evidence.CabinLease == peer.Lease && Now - evidence.CabinAt <= 250)
            evidence.Departure = _body.Root.transform.TransformPoint(evidence.CabinLocal);
        else if (peer.Seat >= 0 && peer.Lease != Guid.Empty && _body?.Ready == true)
            evidence.Departure = SeatMovementOrigin(peer.Seat);
        else if (evidence.FootActive && !evidence.FootFailed && Now - evidence.FootAt <= 250)
            evidence.Departure = evidence.FootPoint;
        else return;
        evidence.DepartureExpires = Now + MovementHistoryLifetime;
        evidence.DepartureActive = true;
    }

    private bool ConfirmMovementReturn(Peer peer, Vector3 point)
    {
        if (!_movement.TryGetValue(peer, out var evidence) || !CurrentMovement(peer, evidence)
            || !evidence.ReturnIssued || (point - evidence.IslandPosition).sqrMagnitude > 16) return false;
        evidence.Active = evidence.FootActive = false;
        return true;
    }

    private bool WalkingMember(Peer peer, MovementEvidence evidence) => evidence.Active && !evidence.ReturnIssued
        && peer.Seat < 0 && !peer.ForceReturn && peer.EntryPrepared && _body?.Ready == true
        && (evidence.SurfaceRegion == SpacecraftRegion.Island || _crew.Contains(peer)) && evidence.Ship == _shipId
        && CurrentMovement(peer, evidence) && _destination.Ready;

    private bool SurfaceWalkPoint(Vector3 point, SpacecraftRegion region, out Vector3 surface)
    {
        surface = default;
        if (!Finite(point)) return false;
        if (_body?.Ready == true && _body.State.Region == region)
        {
            if (Stationary(_body.State) && _body.TryWalkSurface(point, out surface))
            {
                var heightOnDeck = point.y - surface.y;
                return heightOnDeck >= -.35f && heightOnDeck <= 2.3f;
            }
            if (_body.ContainsCabin(point)) return false;
            if (region == SpacecraftRegion.Island)
            {
                if ((point - _body.State.Position).sqrMagnitude > 40 * 40
                    || !TryGround(point + Vector3.up * .25f, 4, out var ground)) return false;
                surface = ground.point;
                var heightAbove = point.y - surface.y;
                return heightAbove >= -1 && heightAbove <= 3;
            }
        }
        if (!SurfaceGround(point, region, out surface)) return false;
        var height = point.y - surface.y;
        return height >= -1 && height <= 5;
    }

    private bool AcceptWalkingSample(MovementEvidence evidence, Vector3 point, long now, bool commit = true)
    {
        if (!evidence.FootActive || evidence.FootFailed) return false;
        var elapsed = now - evidence.FootAt;
        if (elapsed < 0 || !SurfaceWalkPoint(point, evidence.SurfaceRegion, out var surface))
        { if (commit) evidence.FootFailed = true; return false; }
        var seconds = Math.Min(elapsed, 1000) / 1000f;
        var delta = point - evidence.FootPoint;
        var horizontal = new Vector2(delta.x, delta.z).magnitude;
        var verticalLimit = 10 * seconds + .15f + MathF.Abs(surface.y - evidence.FootSurface);
        if (horizontal > 12 * seconds + .15f || MathF.Abs(delta.y) > verticalLimit)
        { if (commit) evidence.FootFailed = true; return false; }
        // Calls between samples can match the bounded path, but cannot spend the
        // position slack again by advancing the baseline every frame.
        if (commit && elapsed >= 250)
        { evidence.FootPoint = point; evidence.FootSurface = surface.y; evidence.FootAt = now; }
        return true;
    }

    internal Tuple<bool, Guid> MatchMovementEvidence(Guid session, ulong actor, NumericsVector observed)
    {
        if (!OnThread || !_started || !_ready || !ServiceRunning || IsClient || !BoltNetwork.isRunning || !BoltNetwork.isServer
            || actor == 0 || _epoch == Guid.Empty || _movementShip == Guid.Empty) return NoMovementEvidence;
        var point = new Vector3(observed.X, observed.Y, observed.Z);
        if (!Finite(point)) return NoMovementEvidence;
        var now = Now;
        foreach (var pair in _movement)
        {
            var peer = pair.Key; var evidence = pair.Value;
            if (evidence.Session != session || evidence.Actor != actor || !CurrentMovement(peer, evidence)) continue;
            if (evidence.EndpointActive && now <= evidence.EndpointExpires
                && (point - evidence.Endpoint).sqrMagnitude <= (evidence.EndpointReturn ? 16 : 2.5f * 2.5f))
            {
                if (!evidence.EndpointReturn && evidence.SurfaceRegion is (SpacecraftRegion.TestWorld or SpacecraftRegion.Moon)
                    && (!SurfaceWalkPoint(point, evidence.SurfaceRegion, out _)
                        || evidence.SurfaceRecovery != null
                            && (point - evidence.Endpoint).sqrMagnitude > .35f * .35f)) return NoMovementEvidence;
                return evidence.Match;
            }
            if (evidence.DepartureActive && now <= evidence.DepartureExpires
                && (point - evidence.Departure).sqrMagnitude <= 2.5f * 2.5f
                && (!peer.Walking || CabinActorCurrent(peer, point))) return evidence.Match;
            if (peer.Walking)
                return CabinWalkingMember(peer, evidence) && AcceptCabinSample(evidence, point, now, false)
                    ? evidence.Match : NoMovementEvidence;
            if (peer.Seat is >= 0 and <= 2 && peer.Lease != Guid.Empty && ReferenceEquals(_seats[peer.Seat], peer)
                && _body?.Ready == true && evidence.Active && evidence.Ship == _shipId)
            {
                var history = _movementSeats[peer.Seat];
                if (history.Lease != peer.Lease || history.Epoch != _epoch || history.Ship != _shipId
                    || now < history.Last || now - history.Last > 250) return NoMovementEvidence;
                if (!peer.Attached)
                {
                    // Sitting interpolates from the nearby aisle into this exact seat.
                    if (Stationary(_body.State) && _body.CanReachSeat(point, peer.Seat, ActorTransform(peer))
                        && SurfaceWalkPoint(point, _body.State.Region, out _)) return history.Match;
                    var origin = SeatMovementOrigin(peer.Seat);
                    var along = Mathf.Clamp(Vector3.Dot(point - origin, Vector3.up), 0, 1.2f);
                    return (point - (origin + Vector3.up * along)).sqrMagnitude <= .8f * .8f
                        ? history.Match : NoMovementEvidence;
                }
                return SeatHistoryContains(history, peer.Seat, point, now) ? history.Match : NoMovementEvidence;
            }
            if (WalkingMember(peer, evidence) && AcceptWalkingSample(evidence, point, now, false)) return evidence.Match;
            return NoMovementEvidence;
        }
        return NoMovementEvidence;
    }

    private void RetireMovementEvidence(Peer peer)
    {
        _movement.Remove(peer);
        for (var seat = 0; seat < 3; seat++)
            if (ReferenceEquals(_seats[seat], peer)) _movementSeats[seat].Reset();
    }

    private void ResetMovementEvidence()
    {
        _movement.Clear(); _movementShip = Guid.Empty; _nextMovementSample = 0;
        _movementFrameIndex = _movementFrameCount = 0;
        foreach (var history in _movementSeats) history.Reset();
    }

    private sealed class MovementEvidence
    {
        internal readonly PlayerHandle? Binding;
        internal readonly Guid Session, Epoch, Ship, Expedition = Guid.NewGuid();
        internal readonly ulong Actor;
        internal readonly IntPtr ActorPointer;
        internal readonly Vector3 IslandPosition;
        internal readonly Tuple<bool, Guid> Match;
        internal bool Active = true, ReturnIssued, EndpointActive, EndpointReturn, EndpointArrived, DepartureActive, FootActive, FootFailed;
        internal bool CabinActive, CabinFailed, CabinEntered, CabinOutside;
        internal Guid EndpointLease, CabinLease;
        internal SpacecraftRegion SurfaceRegion;
        internal SurfaceRecovery? SurfaceRecovery;
        internal long SurfaceCompleted;
        internal Vector3 Endpoint, Departure, FootPoint, CabinLocal, CabinObserved, CabinSeatLocal;
        internal float FootSurface;
        internal long EndpointExpires, DepartureExpires, FootAt, CabinAt, CabinObservedAt, CabinDelay;
        internal MovementEvidence(Peer peer, Guid epoch, Guid ship, ulong actor, Vector3 island)
        {
            Binding = peer.Binding; Session = peer.Session?.SessionId ?? Guid.Empty;
            Epoch = epoch; Ship = ship; Actor = actor; ActorPointer = peer.ActorPointer;
            IslandPosition = island; Match = Tuple.Create(true, Expedition);
        }
    }

    private readonly record struct MovementShipFrame(Vector3 Position, Quaternion Rotation, Vector3 Velocity,
        long At, long Revision);

    private sealed class MovementSeatHistory
    {
        internal readonly Vector3[] Positions = new Vector3[MovementHistoryCount];
        internal readonly MovementShipFrame[] Frames = new MovementShipFrame[MovementHistoryCount];
        internal Guid Lease, Epoch, Ship;
        internal int Index, Count;
        internal long Last;
        internal Tuple<bool, Guid> Match = NoMovementEvidence;
        internal void Reset() { Lease = Epoch = Ship = Guid.Empty; Index = Count = 0; Last = 0; Match = NoMovementEvidence; }
        internal void Begin(Guid lease, Guid epoch, Guid ship)
        { Reset(); Lease = lease; Epoch = epoch; Ship = ship; Match = Tuple.Create(true, lease); }
    }
}
