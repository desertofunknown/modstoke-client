// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftService
{
    private Guid _surfaceLease;
    private SpacecraftRegion _surfaceRegion;
    private long _surfaceRecoverySequence, _receivedSurfaceRecovery;

    private sealed record SurfaceRecovery(long Sequence, Vector3 Point, long Expires)
    {
        internal long NextSend;
    }

    private bool SurfaceGround(Vector3 point, SpacecraftRegion region, out Vector3 ground)
    {
        ground = default;
        if (!_destination.Ready || !Finite(point)
            || region is not (SpacecraftRegion.TestWorld or SpacecraftRegion.Moon)) return false;
        var offset = point - SpacecraftLayout.SurfaceOrigin(region);
        var radius = SpacecraftLayout.SurfaceRadius(region);
        return new Vector2(offset.x, offset.z).sqrMagnitude <= radius * radius
            && _destination.TrySurfacePoint(point, out ground);
    }

    private void BeginSurfaceExit(Guid lease, Vector3 point)
    {
        _surfaceLease = Guid.Empty;
        _surfaceRegion = SpacecraftRegion.Island;
        if (lease == Guid.Empty || !Finite(point) || point.y is <= 2000 or >= 4000) return;
        var region = (point - SpacecraftLayout.MoonOrigin).sqrMagnitude
            < (point - SpacecraftLayout.WorldOrigin).sqrMagnitude ? SpacecraftRegion.Moon : SpacecraftRegion.TestWorld;
        var offset = point - SpacecraftLayout.SurfaceOrigin(region);
        var radius = SpacecraftLayout.SurfaceRadius(region);
        if (new Vector2(offset.x, offset.z).sqrMagnitude > radius * radius) return;
        _surfaceRegion = region;
        _surfaceLease = lease;
    }

    private void UpdateSurfaceRecovery(long now)
    {
        if (IsClient || !_ready || _body?.Ready != true || !_destination.Ready) return;
        foreach (var pair in _movement)
        {
            var peer = pair.Key; var evidence = pair.Value;
            if (evidence.EndpointLease == Guid.Empty
                || evidence.SurfaceRegion is not (SpacecraftRegion.TestWorld or SpacecraftRegion.Moon)
                || evidence.SurfaceRecovery == null && !evidence.FootFailed
                    && (evidence.FootActive || now <= evidence.EndpointExpires)) continue;
            if (!WalkingMember(peer, evidence) || !TryActor(peer, out var point, out _, out _)) continue;
            var recovery = evidence.SurfaceRecovery;
            if (recovery != null && now <= recovery.Expires
                && (point - recovery.Point).sqrMagnitude <= .35f * .35f
                && SurfaceWalkPoint(point, evidence.SurfaceRegion, out var surface))
            {
                // The native actor must reach this issued endpoint before a
                // failed walking sample can become a fresh movement baseline.
                evidence.FootPoint = point; evidence.FootSurface = surface.y; evidence.FootAt = now;
                evidence.FootActive = true; evidence.FootFailed = false;
                evidence.EndpointActive = evidence.DepartureActive = false;
                evidence.SurfaceCompleted = recovery.Sequence;
                evidence.SurfaceRecovery = null;
                continue;
            }
            if (recovery == null)
            {
                var previous = evidence.FootActive ? evidence.FootPoint : evidence.Endpoint;
                if (!SurfaceGround(previous, evidence.SurfaceRegion, out var ground)) continue;
                recovery = new(checked(++_surfaceRecoverySequence), ground + Vector3.up * .12f, now + EndpointLifetime);
                evidence.SurfaceRecovery = recovery;
            }
            else if (now > recovery.Expires)
            {
                recovery = new(checked(++_surfaceRecoverySequence), recovery.Point, now + EndpointLifetime);
                evidence.SurfaceRecovery = recovery;
            }
            evidence.FootFailed = true;
            evidence.Endpoint = recovery.Point;
            evidence.EndpointExpires = recovery.Expires;
            evidence.EndpointActive = true;
            evidence.EndpointReturn = evidence.EndpointArrived = evidence.DepartureActive = false;
            if (now < recovery.NextSend) continue;
            recovery.NextSend = now + 500;
            if (ReferenceEquals(peer, _local))
            {
                if (_surfaceLease == evidence.EndpointLease && _surfaceRegion == evidence.SurfaceRegion
                    && _rider?.Active != true && _rider?.Restoring != true
                    && _expedition.TryRelocateSurface(recovery.Point))
                    _feedback = "Position restored on the current surface.";
            }
            else _event?.SurfaceRecovery(peer, recovery.Sequence, evidence.EndpointLease, evidence.SurfaceRegion, recovery.Point);
        }
    }

    private void ReceiveSurfaceRecovery(Message message)
    {
        if (!IsClient || !_ready || message.Sequence <= _receivedSurfaceRecovery || message.Ship != _shipId
            || _surfaceLease == Guid.Empty || message.Lease != _surfaceLease || message.SurfaceRegion != _surfaceRegion
            || _clientSeat >= 0 || _rider?.Active == true || _rider?.Restoring == true
            || !_expedition.Current || _expedition.ReturnRequested
            || !SurfaceGround(message.SurfacePoint, _surfaceRegion, out var ground)
            || (message.SurfacePoint - ground).sqrMagnitude > .2f * .2f) return;
        if (!_expedition.TryRelocateSurface(message.SurfacePoint))
        {
            _feedback = "Waiting for clearance at the surface recovery point.";
            return;
        }
        _receivedSurfaceRecovery = message.Sequence;
        _feedback = "Position restored on the current surface.";
    }
}
