// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftService
{
    private const int FoamChunkSize = 13, FoamChunksPerSend = 3;
    private const long FoamSendInterval = 100;
    private FoamSnapshot? _outgoingFoam;
    private FoamAssembly? _incomingFoam;
    private long _incomingFoamSequence, _nextFoamSendAt;

    private BoxStationFoamState? CurrentFoamState => CurrentPortablesState?.Foam;

    private void ResetFoamTransport()
    {
        _outgoingFoam = null;
        _incomingFoam = null;
        _incomingFoamSequence = _nextFoamSendAt = 0;
    }

    private void MarkFoamDirty() => MarkPortablesDirty();

    private void SendFoamSnapshots()
    {
        long now = Now;
        ExpirePortableAssembly(now);
        if (_outgoingFoam is not { } snapshot || _event == null || now < _nextFoamSendAt) return;
        _nextFoamSendAt = now + FoamSendInterval;
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
            // Complete the captured transfer before publishing later timer or holder changes.
            int end = Math.Min(snapshot.ChunkCount, pending.Chunk + FoamChunksPerSend);
            while (pending.Chunk < end)
            {
                _event.Foam(peer, pending.Token, snapshot, pending.Chunk);
                pending.Chunk++;
            }
            if (pending.Chunk == snapshot.ChunkCount) snapshot.Peers.RemoveAt(index);
        }
        if (snapshot.Peers.Count == 0) _outgoingFoam = null;
    }

    private void ReceiveFoam(Message message)
    {
        if (message.Foam is not { } chunk || !BeginPortableReceive(message)) return;
        long now = Now;
        if (message.Sequence > _incomingFoamSequence)
        {
            _incomingFoamSequence = message.Sequence;
            _incomingFoam = new(message.Epoch, message.Token, message.Ship, chunk.Total, _incomingPortables!.StartedAt);
        }
        var incoming = _incomingFoam;
        if (incoming == null) return;
        if (incoming.Epoch != message.Epoch || incoming.Token != message.Token || incoming.Ship != message.Ship
            || incoming.Items.Length != chunk.Total || now - incoming.StartedAt >= PortableTransportLayout.Timeout)
        {
            DiscardPortableAssembly();
            return;
        }
        int index = chunk.Offset / FoamChunkSize;
        if (incoming.Chunks[index])
        {
            if (!SameFoamItems(incoming.Items.AsSpan(chunk.Offset, chunk.Items.Length), chunk.Items))
                DiscardPortableAssembly();
            return;
        }
        chunk.Items.CopyTo(incoming.Items, chunk.Offset);
        incoming.Chunks[index] = true;
        if (++incoming.ReceivedChunks != incoming.Chunks.Length) return;
        var state = new BoxStationFoamState(BoxStationLayout.Current.LayoutHash, ImmutableArray.CreateRange(incoming.Items));
        if (!BoxStationFoam.ValidState(BoxStationLayout.Current, state))
        {
            DiscardPortableAssembly();
            return;
        }
        _incomingPortables!.Foam = state;
        TryCommitPortables();
    }

    private static bool SameFoamItems(ReadOnlySpan<BoxStationFoamItemState> left, ReadOnlySpan<BoxStationFoamItemState> right)
    {
        if (left.Length != right.Length) return false;
        for (int index = 0; index < left.Length; index++)
        {
            var a = left[index]; var b = right[index];
            if (a.Place != b.Place || a.Actor != b.Actor || a.Holder != b.Holder || a.Target != b.Target
                || a.Storage != b.Storage || a.Slot != b.Slot
                || BitConverter.SingleToInt32Bits(a.RemainingSeconds) != BitConverter.SingleToInt32Bits(b.RemainingSeconds)
                || BitConverter.SingleToInt32Bits(a.Position.X) != BitConverter.SingleToInt32Bits(b.Position.X)
                || BitConverter.SingleToInt32Bits(a.Position.Y) != BitConverter.SingleToInt32Bits(b.Position.Y)
                || BitConverter.SingleToInt32Bits(a.Position.Z) != BitConverter.SingleToInt32Bits(b.Position.Z)) return false;
        }
        return true;
    }

    private static int FoamChunkCount(int items) => Math.Max(1, (items + FoamChunkSize - 1) / FoamChunkSize);

    private sealed class FoamSnapshot(BoxStationFoamState state, Guid epoch, Guid ship, long sequence)
    {
        internal readonly BoxStationFoamState State = state;
        internal readonly Guid Epoch = epoch, Ship = ship;
        internal readonly long Sequence = sequence;
        internal readonly int ChunkCount = FoamChunkCount(state.Items.Length);
        internal readonly List<StationPeer> Peers = new();
    }

    private sealed class FoamChunk(int total, int offset, BoxStationFoamItemState[] items)
    {
        internal readonly int Total = total, Offset = offset;
        internal readonly BoxStationFoamItemState[] Items = items;
    }

    private sealed class FoamAssembly(Guid epoch, Guid token, Guid ship, int total, long startedAt)
    {
        internal readonly Guid Epoch = epoch, Token = token, Ship = ship;
        internal readonly long StartedAt = startedAt;
        internal readonly BoxStationFoamItemState[] Items = new BoxStationFoamItemState[total];
        internal readonly bool[] Chunks = new bool[FoamChunkCount(total)];
        internal int ReceivedChunks;
    }

    private sealed partial class SpacecraftEvent
    {
        // Header: 2 + 16 + 16 + 8 + 16 + 4 + 32 + 4 + 4 + 2 = 104 bytes.
        // Each item adds 50 bytes: 104 + 13 * 50 = 754, below the 768-byte limit.
        internal void Foam(Peer peer, Guid token, FoamSnapshot snapshot, int chunk) =>
            Emit(peer, (Token: token, Snapshot: snapshot, Chunk: chunk), static (writer, state) =>
        {
            var value = state.Snapshot.State;
            Head(writer, Kind.Foam); GuidValue(writer, state.Snapshot.Epoch); GuidValue(writer, state.Token);
            writer.Write(state.Snapshot.Sequence); GuidValue(writer, state.Snapshot.Ship);
            writer.Write(OrbitalStationOrbit.BodyId); writer.Write(StationLayoutFingerprint.Value);
            writer.Write(value.Items.Length);
            int offset = state.Chunk * FoamChunkSize;
            int count = Math.Min(FoamChunkSize, value.Items.Length - offset);
            writer.Write(offset); writer.Write(checked((ushort)count));
            for (int index = offset; index < offset + count; index++)
            {
                var item = value.Items[index];
                writer.Write((byte)item.Place);
                writer.Write(item.Position.X); writer.Write(item.Position.Y); writer.Write(item.Position.Z);
                writer.Write(item.Actor); GuidValue(writer, item.Holder);
                writer.Write(item.Target); writer.Write(item.RemainingSeconds);
                writer.Write(item.Storage); writer.Write(item.Slot);
            }
        });

        private static FoamChunk ReadFoam(BinaryReader reader)
        {
            if (reader.ReadInt32() != OrbitalStationOrbit.BodyId) throw new InvalidDataException();
            Span<byte> fingerprint = stackalloc byte[32];
            ReadExactly(reader, fingerprint);
            if (!fingerprint.SequenceEqual(StationLayoutFingerprint.Value)) throw new InvalidDataException();
            var layout = BoxStationLayout.Current;
            int total = reader.ReadInt32(), offset = reader.ReadInt32(), count = reader.ReadUInt16();
            if (total != BoxStationFoam.GetItems(layout).Length || offset < 0 || offset % FoamChunkSize != 0
                || (total == 0 ? offset != 0 : offset >= total)
                || count != Math.Min(FoamChunkSize, total - offset)) throw new InvalidDataException();
            var items = new BoxStationFoamItemState[count];
            for (int index = 0; index < items.Length; index++)
            {
                var place = (BoxStationFoamPlace)reader.ReadByte();
                var position = Vector(reader);
                ulong actor = reader.ReadUInt64(); var holder = GuidValue(reader);
                int target = reader.ReadInt32();
                float remaining = Range(reader, 0, Math.Max(BoxStationFoam.FuseSeconds, BoxStationFoam.FoamSeconds));
                int storage = reader.ReadInt32(); byte slot = reader.ReadByte();
                var point = new System.Numerics.Vector3(position.x, position.y, position.z);
                if (place is < BoxStationFoamPlace.Rack or > BoxStationFoamPlace.Stored || !Finite(position)
                    || place != BoxStationFoamPlace.Stored && (storage != -1 || slot != 0)
                    || place == BoxStationFoamPlace.Stored && (actor != 0 || holder != Guid.Empty
                        || !BoxStationStorage.ValidMembership(layout, storage, slot, point))) throw new InvalidDataException();
                items[index] = new(place, point, actor, holder, target, remaining, storage, slot);
            }
            return new(total, offset, items);
        }
    }
}
