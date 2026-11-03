// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftService
{
    private const int HullChunkSize = 93, HullChunksPerSend = 5, HullSendPasses = 3;
    private const long HullSendInterval = 100, HullRefreshInterval = 2000;
    private static readonly Lazy<int> StationHullTargetCount = new(() =>
    {
        var layout = BoxStationLayout.Current;
        int count = BoxStationHull.GetStructures(layout).Length;
        for (int target = 0; target < layout.Cells.Length * 3; target++)
            if (BoxStationHull.ValidTarget(layout, target)) count++;
        return count;
    });
    private HullSnapshot? _outgoingHull;
    private HullAssembly? _incomingHull;
    private BoxStationHullState? _hullState;
    private Guid _hullStateEpoch, _hullStateToken, _hullStateShip;
    private long _hullSequence, _incomingHullSequence, _receivedHullSequence;
    private long _nextHullSnapshot, _nextHullSendAt;

    private BoxStationHullState? CurrentHullState => !_ready ? null
        : !IsClient ? _stationSystems.Hull.State
        : _shipId == Guid.Empty || _hullStateShip != _shipId || _hullStateEpoch != _epoch || _hullStateToken != _token
            ? null : _hullState;

    private void ResetHullTransport()
    {
        _outgoingHull = null;
        _incomingHull = null;
        _hullState = null;
        _hullStateEpoch = _hullStateToken = _hullStateShip = Guid.Empty;
        _hullSequence = _incomingHullSequence = _receivedHullSequence = 0;
        _nextHullSnapshot = _nextHullSendAt = 0;
    }

    private void PublishHullState()
    {
        if (!IsClient && _ready) _nextHullSnapshot = 0;
    }

    private void FlushHullSnapshots()
    {
        long now = Now;
        if (_incomingHull is { } incoming && now - incoming.StartedAt >= incoming.Timeout)
            _incomingHull = null;
        if (IsClient || !_ready || _shipId == Guid.Empty)
        {
            _outgoingHull = null;
            return;
        }
        if (_outgoingHull is { } previous && (previous.Epoch != _epoch || previous.Ship != _shipId))
            _outgoingHull = null;
        if (_outgoingHull == null && now >= _nextHullSnapshot)
        {
            _nextHullSnapshot = now + HullRefreshInterval;
            if (_peers.Count == 0) return;
            var state = _stationSystems.Hull.State;
            if (!BoxStationHull.ValidState(BoxStationLayout.Current, state))
                throw new InvalidDataException("Invalid station hull telemetry.");
            var captured = new HullSnapshot(state, _epoch, _shipId, checked(++_hullSequence));
            foreach (var pair in _peers)
                if (PeerCurrent(pair.Value)) captured.Peers.Add(new(pair.Key, pair.Value));
            if (captured.Peers.Count > 0) _outgoingHull = captured;
        }
        if (_outgoingHull is not { } snapshot || _event == null || now < _nextHullSendAt) return;
        _nextHullSendAt = now + HullSendInterval;
        for (int index = snapshot.Peers.Count - 1; index >= 0; index--)
        {
            var pending = snapshot.Peers[index];
            var peer = pending.Peer;
            if (!_peers.TryGetValue(pending.Session, out var current) || !ReferenceEquals(current, peer)
                || peer.Token != pending.Token || !PeerCurrent(peer))
            {
                snapshot.Peers.RemoveAt(index);
                continue;
            }
            // Repeat the same immutable sequence so dropped chunks can be recovered.
            // Finish all passes before capturing later damage, even under continuous impacts.
            int total = snapshot.ChunkCount * HullSendPasses;
            int passEnd = (pending.Chunk / snapshot.ChunkCount + 1) * snapshot.ChunkCount;
            int end = Math.Min(passEnd, pending.Chunk + HullChunksPerSend);
            while (pending.Chunk < end)
            {
                _event.Hull(peer, pending.Token, snapshot, pending.Chunk % snapshot.ChunkCount);
                pending.Chunk++;
            }
            if (pending.Chunk == total) snapshot.Peers.RemoveAt(index);
        }
        if (snapshot.Peers.Count == 0) _outgoingHull = null;
    }

    private void ReceiveHull(Message message)
    {
        if (!IsClient || !_ready || message.Epoch != _epoch || message.Token != _token
            || message.Ship == Guid.Empty || message.Ship != _shipId
            || message.Sequence <= _receivedHullSequence || message.Sequence < _incomingHullSequence
            || message.Hull is not { } chunk) return;
        if (_hullState is { } current && chunk.Revision < current.Revision
            || _incomingHull is { } previous && chunk.Revision < previous.Revision) return;
        long now = Now;
        if (message.Sequence > _incomingHullSequence)
        {
            _incomingHullSequence = message.Sequence;
            _incomingHull = new(message.Epoch, message.Token, message.Ship, chunk, now);
        }
        var incoming = _incomingHull;
        // An expired or conflicting sequence cannot restart its assembly timeout.
        if (incoming == null) return;
        if (incoming.Epoch != message.Epoch || incoming.Token != message.Token || incoming.Ship != message.Ship
            || incoming.Revision != chunk.Revision || incoming.Panels.Length != chunk.Total
            || now - incoming.StartedAt >= incoming.Timeout)
        {
            _incomingHull = null;
            return;
        }
        int index = chunk.Offset / HullChunkSize;
        if (incoming.Chunks[index])
        {
            if (!incoming.Panels.AsSpan(chunk.Offset, chunk.Panels.Length).SequenceEqual(chunk.Panels))
                _incomingHull = null;
            return;
        }
        chunk.Panels.CopyTo(incoming.Panels, chunk.Offset);
        incoming.Chunks[index] = true;
        if (++incoming.ReceivedChunks != incoming.Chunks.Length) return;
        _incomingHull = null;
        var state = new BoxStationHullState(incoming.Revision, ImmutableArray.CreateRange(incoming.Panels));
        if (!BoxStationHull.ValidState(BoxStationLayout.Current, state)) return;
        if (_hullState is { } existing)
        {
            if (state.Revision < existing.Revision) return;
            if (state.Revision == existing.Revision)
            {
                if (!state.Panels.AsSpan().SequenceEqual(existing.Panels.AsSpan())) return;
                state = existing;
            }
        }
        _receivedHullSequence = message.Sequence;
        _hullState = state;
        _hullStateEpoch = message.Epoch;
        _hullStateToken = message.Token;
        _hullStateShip = message.Ship;
    }

    private static int HullChunkCount(int panels) => Math.Max(1, (panels + HullChunkSize - 1) / HullChunkSize);

    private sealed class HullSnapshot(BoxStationHullState state, Guid epoch, Guid ship, long sequence)
    {
        internal readonly BoxStationHullState State = state;
        internal readonly Guid Epoch = epoch, Ship = ship;
        internal readonly long Sequence = sequence;
        internal readonly int ChunkCount = HullChunkCount(state.Panels.Length);
        internal readonly List<StationPeer> Peers = new();
    }

    private sealed class HullChunk(long revision, int total, int offset, BoxStationPanelState[] panels)
    {
        internal readonly long Revision = revision;
        internal readonly int Total = total, Offset = offset;
        internal readonly BoxStationPanelState[] Panels = panels;
    }

    private sealed class HullAssembly(Guid epoch, Guid token, Guid ship, HullChunk chunk, long startedAt)
    {
        internal readonly Guid Epoch = epoch, Token = token, Ship = ship;
        internal readonly long Revision = chunk.Revision, StartedAt = startedAt;
        // Allow every resend pass plus scheduling slack for the full source-valid target count.
        internal readonly long Timeout = 3000 + 4 * HullSendInterval * HullSendPasses
            * ((HullChunkCount(chunk.Total) + HullChunksPerSend - 1) / HullChunksPerSend);
        internal readonly BoxStationPanelState[] Panels = new BoxStationPanelState[chunk.Total];
        internal readonly bool[] Chunks = new bool[HullChunkCount(chunk.Total)];
        internal int ReceivedChunks;
    }

    private sealed partial class SpacecraftEvent
    {
        // Header: 2 + 16 + 16 + 8 + 16 + 4 + 32 + 8 + 4 + 4 + 2 = 112 bytes.
        // Each int target, byte integrity, bool foam and byte wall kind adds 7 bytes: 112 + 93 * 7 = 763.
        internal void Hull(Peer peer, Guid token, HullSnapshot snapshot, int chunk) =>
            Emit(peer, (Token: token, Snapshot: snapshot, Chunk: chunk), static (writer, state) =>
        {
            var value = state.Snapshot.State;
            Head(writer, Kind.Hull); GuidValue(writer, state.Snapshot.Epoch); GuidValue(writer, state.Token);
            writer.Write(state.Snapshot.Sequence); GuidValue(writer, state.Snapshot.Ship);
            writer.Write(OrbitalStationOrbit.BodyId); writer.Write(StationLayoutFingerprint.Value);
            writer.Write(value.Revision); writer.Write(value.Panels.Length);
            int offset = state.Chunk * HullChunkSize;
            int count = Math.Min(HullChunkSize, value.Panels.Length - offset);
            writer.Write(offset); writer.Write(checked((ushort)count));
            for (int index = offset; index < offset + count; index++)
            {
                var panel = value.Panels[index];
                writer.Write(panel.Target); writer.Write(panel.Integrity); writer.Write(panel.Foam);
                writer.Write((byte)panel.WallKind);
            }
        });

        private static HullChunk ReadHull(BinaryReader reader)
        {
            if (reader.ReadInt32() != OrbitalStationOrbit.BodyId) throw new InvalidDataException();
            Span<byte> fingerprint = stackalloc byte[32];
            ReadExactly(reader, fingerprint);
            if (!fingerprint.SequenceEqual(StationLayoutFingerprint.Value)) throw new InvalidDataException();
            long revision = reader.ReadInt64();
            int total = reader.ReadInt32(), offset = reader.ReadInt32(), count = reader.ReadUInt16();
            if (revision < 0 || revision == 0 && total != 0
                || total < 0 || total > StationHullTargetCount.Value || offset < 0
                || offset % HullChunkSize != 0 || (total == 0 ? offset != 0 : offset >= total)
                || count != Math.Min(HullChunkSize, total - offset)) throw new InvalidDataException();
            var layout = BoxStationLayout.Current;
            var panels = new BoxStationPanelState[count];
            int previous = -1;
            for (int index = 0; index < panels.Length; index++)
            {
                int target = reader.ReadInt32();
                byte integrity = reader.ReadByte();
                bool foam = Boolean(reader);
                var wallKind = (BoxStationWallKind)reader.ReadByte();
                var panel = new BoxStationPanelState(target, integrity, foam, wallKind);
                if (target <= previous || wallKind is < BoxStationWallKind.None or > BoxStationWallKind.Reinforced
                    || !BoxStationHull.ValidPanelState(layout, panel))
                    throw new InvalidDataException();
                panels[index] = panel;
                previous = target;
            }
            return new(revision, total, offset, panels);
        }
    }
}
