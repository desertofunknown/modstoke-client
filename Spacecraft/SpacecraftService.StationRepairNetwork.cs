// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftService
{
    private const int RepairStockChunkSize = 164, RepairPackChunkSize = 14, RepairChunksPerSend = 3;
    private const long RepairSendInterval = 100;
    private RepairSnapshot? _outgoingRepair;
    private RepairAssembly? _incomingRepair;
    private long _incomingRepairSequence, _nextRepairSendAt;

    private BoxStationRepairState? CurrentRepairState => CurrentPortablesState?.Repair;

    private void ResetRepairTransport()
    {
        _outgoingRepair = null;
        _incomingRepair = null;
        _incomingRepairSequence = _nextRepairSendAt = 0;
    }

    private void MarkRepairDirty() => MarkPortablesDirty();

    private void SendRepairSnapshots()
    {
        long now = Now;
        ExpirePortableAssembly(now);
        if (_outgoingRepair is not { } snapshot || _event == null || now < _nextRepairSendAt) return;
        _nextRepairSendAt = now + RepairSendInterval;
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
            // Stock consumption and appended pack identities belong to one complete capture.
            int end = Math.Min(snapshot.ChunkCount, pending.Chunk + RepairChunksPerSend);
            while (pending.Chunk < end)
            {
                _event.Repair(peer, pending.Token, snapshot, pending.Chunk);
                pending.Chunk++;
            }
            if (pending.Chunk == snapshot.ChunkCount) snapshot.Peers.RemoveAt(index);
        }
        if (snapshot.Peers.Count == 0) _outgoingRepair = null;
    }

    private void ReceiveRepair(Message message)
    {
        if (message.Repair is not { } chunk || !BeginPortableReceive(message)) return;
        long now = Now;
        if (message.Sequence > _incomingRepairSequence)
        {
            _incomingRepairSequence = message.Sequence;
            _incomingRepair = new(message.Epoch, message.Token, message.Ship, chunk.SourceCount, chunk.PackCount, _incomingPortables!.StartedAt);
        }
        var incoming = _incomingRepair;
        if (incoming == null) return;
        if (incoming.Epoch != message.Epoch || incoming.Token != message.Token || incoming.Ship != message.Ship
            || incoming.Sheets.Length != chunk.SourceCount || incoming.Packs.Length != chunk.PackCount
            || now - incoming.StartedAt >= PortableTransportLayout.Timeout)
        {
            DiscardPortableAssembly();
            return;
        }
        int index = chunk.Section == 0 ? chunk.Offset / RepairStockChunkSize
            : RepairStockChunkCount(chunk.SourceCount) + chunk.Offset / RepairPackChunkSize;
        if (incoming.Chunks[index])
        {
            bool same = chunk.Section == 0
                ? incoming.Sheets.AsSpan(chunk.Offset, chunk.Sheets.Length).SequenceEqual(chunk.Sheets)
                    && incoming.ExportedSteel.AsSpan(chunk.Offset, chunk.ExportedSteel.Length).SequenceEqual(chunk.ExportedSteel)
                : SameRepairPacks(incoming.Packs.AsSpan(chunk.Offset, chunk.Packs.Length), chunk.Packs);
            if (!same) DiscardPortableAssembly();
            return;
        }
        if (chunk.Section == 0)
        {
            chunk.Sheets.CopyTo(incoming.Sheets, chunk.Offset);
            chunk.ExportedSteel.CopyTo(incoming.ExportedSteel, chunk.Offset);
        }
        else chunk.Packs.CopyTo(incoming.Packs, chunk.Offset);
        incoming.Chunks[index] = true;
        if (++incoming.ReceivedChunks != incoming.Chunks.Length) return;
        var state = new BoxStationRepairState(BoxStationLayout.Current.LayoutHash,
            ImmutableArray.CreateRange(incoming.Sheets), ImmutableArray.CreateRange(incoming.Packs),
            ImmutableArray.CreateRange(incoming.ExportedSteel));
        if (!BoxStationHullRepair.ValidState(BoxStationLayout.Current, state))
        {
            DiscardPortableAssembly();
            return;
        }
        _incomingPortables!.Repair = state;
        TryCommitPortables();
    }

    private static bool SameRepairPacks(ReadOnlySpan<BoxStationRepairPackState> left, ReadOnlySpan<BoxStationRepairPackState> right)
    {
        if (left.Length != right.Length) return false;
        for (int index = 0; index < left.Length; index++)
        {
            var a = left[index]; var b = right[index];
            if (a.Source != b.Source || a.Tiles != b.Tiles || a.Place != b.Place || a.Actor != b.Actor || a.Holder != b.Holder
                || a.Storage != b.Storage || a.Slot != b.Slot
                || BitConverter.SingleToInt32Bits(a.Position.X) != BitConverter.SingleToInt32Bits(b.Position.X)
                || BitConverter.SingleToInt32Bits(a.Position.Y) != BitConverter.SingleToInt32Bits(b.Position.Y)
                || BitConverter.SingleToInt32Bits(a.Position.Z) != BitConverter.SingleToInt32Bits(b.Position.Z)) return false;
        }
        return true;
    }

    private static int RepairStockChunkCount(int sources) => Math.Max(1, (sources + RepairStockChunkSize - 1) / RepairStockChunkSize);
    private static int RepairPackChunkCount(int packs) => Math.Max(1, (packs + RepairPackChunkSize - 1) / RepairPackChunkSize);
    private static long RepairTransferTimeout(int sources, int packs) => 1500 + 4 * RepairSendInterval
        * ((RepairStockChunkCount(sources) + RepairPackChunkCount(packs) + RepairChunksPerSend - 1) / RepairChunksPerSend);

    private static class RepairTransportLayout
    {
        internal static readonly ImmutableArray<BoxStationRepairSource> Sources = BoxStationHullRepair.GetSources(BoxStationLayout.Current);
        internal static readonly int MaxPacks = BoxStationHullRepair.MaxPacks(BoxStationLayout.Current);
    }

    private sealed class RepairSnapshot(BoxStationRepairState state, Guid epoch, Guid ship, long sequence)
    {
        internal readonly BoxStationRepairState State = state;
        internal readonly Guid Epoch = epoch, Ship = ship;
        internal readonly long Sequence = sequence;
        internal readonly int StockChunkCount = RepairStockChunkCount(state.Sheets.Length);
        internal readonly int ChunkCount = RepairStockChunkCount(state.Sheets.Length) + RepairPackChunkCount(state.Packs.Length);
        internal readonly List<StationPeer> Peers = new();
    }

    private sealed class RepairChunk(int sourceCount, int packCount, byte section, int offset,
        ushort[] sheets, BoxStationRepairPackState[] packs, ushort[] exportedSteel)
    {
        internal readonly int SourceCount = sourceCount, PackCount = packCount, Offset = offset;
        internal readonly byte Section = section;
        internal readonly ushort[] Sheets = sheets;
        internal readonly ushort[] ExportedSteel = exportedSteel;
        internal readonly BoxStationRepairPackState[] Packs = packs;
    }

    private sealed class RepairAssembly(Guid epoch, Guid token, Guid ship, int sources, int packs, long startedAt)
    {
        internal readonly Guid Epoch = epoch, Token = token, Ship = ship;
        internal readonly long StartedAt = startedAt;
        internal readonly ushort[] Sheets = new ushort[sources];
        internal readonly ushort[] ExportedSteel = new ushort[sources];
        internal readonly BoxStationRepairPackState[] Packs = new BoxStationRepairPackState[packs];
        internal readonly bool[] Chunks = new bool[RepairStockChunkCount(sources) + RepairPackChunkCount(packs)];
        internal int ReceivedChunks;
    }

    private sealed partial class SpacecraftEvent
    {
        // Header: 2 + 16 + 16 + 8 + 16 + 4 + 32 + 4 + 4 + 1 + 4 + 2 = 109 bytes.
        // Stocks and exports: 109 + 164 * 4 = 765; packs: 109 + 14 * 47 = 767.
        internal void Repair(Peer peer, Guid token, RepairSnapshot snapshot, int chunk) =>
            Emit(peer, (Token: token, Snapshot: snapshot, Chunk: chunk), static (writer, state) =>
        {
            var value = state.Snapshot.State;
            Head(writer, Kind.Repair); GuidValue(writer, state.Snapshot.Epoch); GuidValue(writer, state.Token);
            writer.Write(state.Snapshot.Sequence); GuidValue(writer, state.Snapshot.Ship);
            writer.Write(OrbitalStationOrbit.BodyId); writer.Write(StationLayoutFingerprint.Value);
            writer.Write(value.Sheets.Length); writer.Write(value.Packs.Length);
            bool stock = state.Chunk < state.Snapshot.StockChunkCount;
            writer.Write((byte)(stock ? 0 : 1));
            int offset = stock ? state.Chunk * RepairStockChunkSize
                : (state.Chunk - state.Snapshot.StockChunkCount) * RepairPackChunkSize;
            int count = stock ? Math.Min(RepairStockChunkSize, value.Sheets.Length - offset)
                : Math.Min(RepairPackChunkSize, value.Packs.Length - offset);
            writer.Write(offset); writer.Write(checked((ushort)count));
            if (stock)
            {
                for (int index = offset; index < offset + count; index++)
                {
                    writer.Write(value.Sheets[index]); writer.Write(value.ExportedSteel[index]);
                }
                return;
            }
            for (int index = offset; index < offset + count; index++)
            {
                var pack = value.Packs[index];
                writer.Write(pack.Source); writer.Write(pack.Tiles); writer.Write((byte)pack.Place);
                writer.Write(pack.Position.X); writer.Write(pack.Position.Y); writer.Write(pack.Position.Z);
                writer.Write(pack.Actor); GuidValue(writer, pack.Holder);
                writer.Write(pack.Storage); writer.Write(pack.Slot);
            }
        });

        private static RepairChunk ReadRepair(BinaryReader reader)
        {
            if (reader.ReadInt32() != OrbitalStationOrbit.BodyId) throw new InvalidDataException();
            Span<byte> fingerprint = stackalloc byte[32];
            ReadExactly(reader, fingerprint);
            if (!fingerprint.SequenceEqual(StationLayoutFingerprint.Value)) throw new InvalidDataException();
            var sources = RepairTransportLayout.Sources;
            int sourceCount = reader.ReadInt32(), packCount = reader.ReadInt32();
            byte section = reader.ReadByte();
            int offset = reader.ReadInt32(), count = reader.ReadUInt16();
            if (sourceCount != sources.Length || packCount < 0 || packCount > RepairTransportLayout.MaxPacks
                || section > 1) throw new InvalidDataException();
            int total = section == 0 ? sourceCount : packCount;
            int size = section == 0 ? RepairStockChunkSize : RepairPackChunkSize;
            if (offset < 0 || offset % size != 0 || (total == 0 ? offset != 0 : offset >= total)
                || count != Math.Min(size, total - offset)) throw new InvalidDataException();
            if (section == 0)
            {
                var sheets = new ushort[count];
                var exportedSteel = new ushort[count];
                for (int index = 0; index < count; index++)
                {
                    ushort remaining = reader.ReadUInt16(), exported = reader.ReadUInt16();
                    if (remaining + exported > sources[offset + index].Sheets) throw new InvalidDataException();
                    sheets[index] = remaining;
                    exportedSteel[index] = exported;
                }
                return new(sourceCount, packCount, section, offset, sheets, Array.Empty<BoxStationRepairPackState>(), exportedSteel);
            }
            var packs = new BoxStationRepairPackState[count];
            for (int index = 0; index < count; index++)
            {
                int source = reader.ReadInt32();
                byte tiles = reader.ReadByte();
                var place = (BoxStationRepairPlace)reader.ReadByte();
                var position = Vector(reader);
                ulong actor = reader.ReadUInt64(); var holder = GuidValue(reader);
                int storage = reader.ReadInt32(); byte slot = reader.ReadByte();
                var point = new System.Numerics.Vector3(position.x, position.y, position.z);
                if (source < 0 || source >= sources.Length || tiles > 4
                    || place is < BoxStationRepairPlace.Source or > BoxStationRepairPlace.Stored || !Finite(position)
                    || place != BoxStationRepairPlace.Stored && (storage != -1 || slot != 0)
                    || place == BoxStationRepairPlace.Stored && (tiles == 0 || actor != 0 || holder != Guid.Empty
                        || !BoxStationStorage.ValidMembership(BoxStationLayout.Current, storage, slot, point))) throw new InvalidDataException();
                packs[index] = new(source, tiles, place, point, actor, holder, storage, slot);
            }
            return new(sourceCount, packCount, section, offset, Array.Empty<ushort>(), packs, Array.Empty<ushort>());
        }
    }
}
