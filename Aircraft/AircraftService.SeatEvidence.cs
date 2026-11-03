// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Modstoke.Loader;
using Modstoke.Sons;
using UnityEngine;
using NumericsVector = System.Numerics.Vector3;

namespace Modstoke.Client;

internal sealed partial class ClientAircraftService
{
    // The registry does not grant authority. Admin queries this host-only predicate
    // with its own native player observation and still handles every nonmatch normally.
    private const string SeatEvidenceService = "modstoke.client.aircraft-seat.v1";
    private const int SeatHistoryCount = 32;
    private const long SeatHistoryLifetime = 1200;
    private const float SeatToleranceSquared = 2.5f * 2.5f;
    private readonly SeatHistory[] _seatHistory = { new(), new(), new() };
    private static readonly Tuple<bool, Guid> NoSeatEvidence = Tuple.Create(false, Guid.Empty);
    private Func<Guid, ulong, NumericsVector, Tuple<bool, Guid>>? _seatProvider;
    private long _nextSeatSample;

    private void RegisterSeatEvidence()
    {
        _seatProvider = (session, actor, position) =>
        {
            var aircraft = MatchSeatEvidence(session, actor, position);
            return aircraft.Item1 ? aircraft : Plugin.Spacecraft.MatchMovementEvidence(session, actor, position);
        };
        if (!Modding.TryRegisterService(SeatEvidenceService, _seatProvider))
            throw new InvalidOperationException("Aircraft seat evidence is already registered.");
    }

    private void UnregisterSeatEvidence()
    {
        if (_seatProvider != null
            && Modding.TryGetService<Func<Guid, ulong, NumericsVector, Tuple<bool, Guid>>>(SeatEvidenceService, out var current)
            && ReferenceEquals(current, _seatProvider))
            Modding.TryUnregisterService(SeatEvidenceService);
        _seatProvider = null;
        foreach (var history in _seatHistory) history.Reset();
    }

    private void RecordSeatEvidence()
    {
        if (IsClient || !_ready || _body == null) return;
        var now = Now;
        if (now < _nextSeatSample) return;
        _nextSeatSample = now + 50;
        for (var seat = 0; seat < 3; seat++)
        {
            var peer = _seats[seat];
            var history = _seatHistory[seat];
            if (peer == null || peer.Lease == Guid.Empty || !TryActor(peer, out _, out _, out _))
            { history.Reset(); continue; }
            if (history.Lease != peer.Lease || history.Epoch != _epoch || history.Aircraft != _planeId)
                history.Begin(peer.Lease, _epoch, _planeId);
            // Match the actual native player origin used by ClientAircraftRider.
            var position = _body.Root.transform.TransformPoint(AircraftLayout.Seat(seat) + Vector3.down * .8f);
            if (!Finite(position)) { history.Reset(); continue; }
            history.Record(position, now);
        }
    }

    private Tuple<bool, Guid> MatchSeatEvidence(Guid session, ulong actor, NumericsVector observed)
    {
        if (!OnThread || !_started || !_ready || IsClient || !BoltNetwork.isRunning || !BoltNetwork.isServer
            || _body == null || !_body.Root || _planeId == Guid.Empty || actor == 0) return NoSeatEvidence;
        var point = new Vector3(observed.X, observed.Y, observed.Z);
        if (!Finite(point)) return NoSeatEvidence;
        var now = Now;
        for (var seat = 0; seat < 3; seat++)
        {
            var peer = _seats[seat];
            if (peer == null || peer.Lease == Guid.Empty || peer.Binding == null || !PeerCurrent(peer)
                || peer.Binding.NetworkId != actor
                || (peer.Session == null ? session != Guid.Empty || !peer.Binding.IsLocalPlayer
                    : session == Guid.Empty || peer.Session.SessionId != session)
                || !ServerWorld.TryReadPlayerLife(peer.Binding, out var life, out _)
                || life.LifeState != PlayerLifeState.Alive) continue;
            var history = _seatHistory[seat];
            if (history.Lease != peer.Lease || history.Epoch != _epoch || history.Aircraft != _planeId)
                return NoSeatEvidence;
            if (now < history.Last || now - history.Last > 250) return history.NoMatch;
            for (var i = 0; i < history.Count; i++)
            {
                var index = (history.Index - 1 - i + SeatHistoryCount) % SeatHistoryCount;
                var age = now - history.Times[index];
                if (age < 0 || age > SeatHistoryLifetime) continue;
                if ((point - history.Positions[index]).sqrMagnitude <= SeatToleranceSquared) return history.Match;
            }
            return history.NoMatch;
        }
        // An acknowledged exit must not lose its exact approved endpoint before
        // the next movement sample. This does not qualify later airborne motion.
        var departed = session == Guid.Empty ? _local
            : _peers.TryGetValue(session, out var remote) ? remote : null;
        if (departed?.Departure is not { } departure) return NoSeatEvidence;
        if (now < departure.At || now - departure.At > SeatHistoryLifetime
            || departure.Epoch != _epoch || departure.Aircraft != _planeId
            || !ReferenceEquals(departed.Binding, departure.Binding) || !PeerCurrent(departed))
        { departed.Departure = null; return NoSeatEvidence; }
        if (departure.Binding.NetworkId != actor
            || (departed.Session == null ? session != Guid.Empty || !departure.Binding.IsLocalPlayer
                : departed.Session.SessionId != session)
            || !ServerWorld.TryReadPlayerLife(departure.Binding, out var departedLife, out _)
            || departedLife.LifeState != PlayerLifeState.Alive) return NoSeatEvidence;
        return (point - departure.Position).sqrMagnitude <= SeatToleranceSquared ? departure.Match : NoSeatEvidence;
    }

    private sealed class DepartureEvidence(PlayerHandle binding, Guid lease, Guid epoch,
        Guid aircraft, Vector3 position, long at)
    {
        internal readonly PlayerHandle Binding = binding;
        internal readonly Guid Epoch = epoch, Aircraft = aircraft;
        internal readonly Vector3 Position = position;
        internal readonly long At = at;
        internal readonly Tuple<bool, Guid> Match = Tuple.Create(true, lease);
    }

    private sealed class SeatHistory
    {
        internal readonly Vector3[] Positions = new Vector3[SeatHistoryCount];
        internal readonly long[] Times = new long[SeatHistoryCount];
        internal Guid Lease, Epoch, Aircraft;
        internal int Index, Count;
        internal long Last;
        internal Tuple<bool, Guid> Match = NoSeatEvidence, NoMatch = NoSeatEvidence;
        internal void Reset()
        { Lease = Epoch = Aircraft = Guid.Empty; Index = Count = 0; Last = 0; Match = NoMatch = NoSeatEvidence; }
        internal void Begin(Guid lease, Guid epoch, Guid aircraft)
        {
            Reset(); Lease = lease; Epoch = epoch; Aircraft = aircraft;
            Match = Tuple.Create(true, lease); NoMatch = Tuple.Create(false, lease);
        }

        internal void Record(Vector3 position, long now)
        {
            Positions[Index] = position;
            Times[Index] = now;
            Index = (Index + 1) % SeatHistoryCount;
            Count = Math.Min(SeatHistoryCount, Count + 1);
            Last = now;
        }
    }
}
