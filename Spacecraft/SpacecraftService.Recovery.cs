// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftService
{
    private long _attachmentRecoverySequence, _receivedAttachmentRecovery;
    private long _recoverySnapshot;
    private Guid _recoveringLease;
    private bool _awaitingAttachmentRecovery;

    private sealed record AttachmentRecovery(long Sequence, Guid Lease, Vector3 Point, bool Walking);

    private void UpdateAttachmentRecovery(Peer peer, long now)
    {
        if (!peer.AttachmentSuspended || peer.Lease == Guid.Empty || peer.Seat is < 0 or > 2
            || !ReferenceEquals(_seats[peer.Seat], peer) || peer.ForceReturn || !peer.EntryPrepared
            || _body?.Ready != true || _travel != SpacecraftTravel.None
            || !_movement.TryGetValue(peer, out var evidence) || !evidence.Active || evidence.ReturnIssued
            || evidence.Ship != _shipId || !CurrentMovement(peer, evidence)) return;
        if (peer.Recovery == null)
        {
            // Only an already attached actor may be corrected. Boarding itself
            // must still follow the ordinary physical approach to the chair.
            if (peer.AttachedAt == 0) return;
            Vector3 local;
            if (peer.Walking)
            {
                if (!evidence.CabinActive || !evidence.CabinEntered || evidence.CabinLease != peer.Lease) return;
                local = evidence.CabinObserved;
                if (!CabinPositionAllowed(evidence, local)) return;
            }
            else local = _body.Root.transform.InverseTransformPoint(SeatMovementOrigin(peer.Seat));
            if (!Finite(local)) return;
            peer.Recovery = new(checked(++_attachmentRecoverySequence), peer.Lease, local, peer.Walking);
            peer.NextRecovery = 0;
        }
        var recovery = peer.Recovery;
        if (recovery.Lease != peer.Lease || recovery.Walking != peer.Walking || now < peer.NextRecovery) return;
        peer.NextRecovery = now + 500;
        if (ReferenceEquals(peer, _local))
        {
            if (_ridingLease != peer.Lease || _rider == null) return;
            if (_rider.TryCorrectAttachment(_body, recovery.Point, recovery.Walking)) _nextSeatStatus = 0;
        }
        else _event?.AttachmentRecovery(peer, recovery.Sequence, recovery.Point, recovery.Walking);
    }

    private void ReceiveAttachmentRecovery(Message message)
    {
        if (!IsClient || !_ready || message.Sequence <= _receivedAttachmentRecovery
            || message.Ship != _shipId || message.Lease != _clientLease || message.Lease != _ridingLease
            || message.Lease == Guid.Empty || message.FrameRevision != _frameRevision
            || message.RecoveryWalking != _clientWalking || _clientSeat is < 0 or > 2
            || _receivedSequence > message.RecoverySnapshot && _actorIds[_clientSeat] != 0
            || _body is not { Ready: true, TelemetryCurrent: true } || _rider == null
            || !_expedition.Current || _expedition.ReturnRequested) return;
        _rider.SetSuspended(true);
        if (!_rider.TryCorrectAttachment(_body, message.RecoveryPoint, message.RecoveryWalking))
        {
            _feedback = "The ship's recovery position is blocked or not ready. Holding your current position.";
            return;
        }
        _receivedAttachmentRecovery = message.Sequence;
        _recoveringLease = message.Lease;
        _recoverySnapshot = message.RecoverySnapshot;
        _awaitingAttachmentRecovery = true;
        _nextSeatStatus = 0;
        _feedback = "Position restored aboard the ship. Waiting for host confirmation.";
    }

    private void TryRestoreAttachmentEvidence(Peer peer, Vector3 observed)
    {
        if (!peer.AttachmentSuspended || peer.Recovery is not { } recovery || !recovery.Walking
            || recovery.Lease != peer.Lease || !peer.Walking || !Finite(observed)
            || _body?.Ready != true || !_movement.TryGetValue(peer, out var evidence)
            || !CabinWalkingMember(peer, evidence) || !evidence.CabinEntered
            || !CabinPositionAllowed(evidence, recovery.Point)) return;
        var now = Now;
        if (!RecoveryObservation(recovery.Point, observed, now, out var local, out var delay)
            || !CabinPositionAllowed(evidence, local)) return;
        if (_body.State.Region == SpacecraftRegion.Space)
        {
            if (!_body.CrewStep(evidence.CabinObserved, local, evidence.CabinOutside, out var outside)) return;
            evidence.CabinOutside = outside;
        }
        // Refresh evidence only at the host-issued endpoint. Elapsed time and
        // a client attachment claim cannot establish a new walking baseline.
        evidence.CabinLocal = evidence.CabinObserved = local;
        evidence.CabinAt = evidence.CabinObservedAt = now;
        evidence.CabinDelay = delay;
        evidence.CabinFailed = false;
    }

    private bool RecoveryObservation(Vector3 target, Vector3 observed, long now, out Vector3 local, out long delay)
    {
        delay = 0;
        var newer = CurrentMovementFrame(now);
        local = Quaternion.Inverse(newer.Rotation) * (observed - newer.Position);
        if ((local - target).sqrMagnitude <= .35f * .35f) return true;
        for (int i = 0; i < _movementFrameCount; i++)
        {
            int index = (_movementFrameIndex - 1 - i + MovementHistoryCount) % MovementHistoryCount;
            var older = _movementFrames[index];
            if (older.At > now || now - older.At > MovementHistoryLifetime) break;
            local = CabinPointOnSegment(observed, target, older, newer, out var fraction);
            if ((local - target).sqrMagnitude <= .35f * .35f)
            {
                delay = now - older.At - (long)((newer.At - older.At) * fraction);
                return true;
            }
            newer = older;
        }
        return false;
    }
}
