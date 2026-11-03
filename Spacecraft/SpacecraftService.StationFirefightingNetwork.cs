// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using UnityEngine;

namespace Modstoke.Client;

internal readonly record struct BoxStationWaterBurst(int Item, long Sequence, Vector3 Origin, Vector3[] Endpoints,
    float Units, bool Ship = false);

internal sealed partial class ClientSpacecraftService
{
    private const int FirefightingChunkSize = 12;
    private FirefightingSnapshot? _outgoingFirefighting;
    private FirefightingAssembly? _incomingFirefighting;
    private long _incomingFirefightingSequence, _nextFirefightingSendAt;
    private long _firefightingBurstSequence, _receivedFirefightingBurstSequence;

    private BoxStationFirefightingState? CurrentFirefightingState => CurrentPortablesState?.Firefighting;

    private void ResetFirefightingTransport()
    {
        ReleaseFirefightingViews();
        _firefightingBurstSequence = _receivedFirefightingBurstSequence = 0;
        _outgoingFirefighting = null;
        _incomingFirefighting = null;
        _incomingFirefightingSequence = _nextFirefightingSendAt = 0;
    }

    private void MarkFirefightingDirty() => MarkPortablesDirty();

    private void SendFirefightingSnapshots()
    {
        long now = Now;
        ExpirePortableAssembly(now);
        if (_outgoingFirefighting is not { } snapshot || _event == null || now < _nextFirefightingSendAt) return;
        _nextFirefightingSendAt = now + 100;
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
            int end = Math.Min(snapshot.ChunkCount, pending.Chunk + 3);
            while (pending.Chunk < end)
            {
                _event.Firefighting(peer, pending.Token, snapshot, pending.Chunk);
                pending.Chunk++;
            }
            if (pending.Chunk == snapshot.ChunkCount) snapshot.Peers.RemoveAt(index);
        }
        if (snapshot.Peers.Count == 0) _outgoingFirefighting = null;
    }

    private void ReceiveFirefighting(Message message)
    {
        if (!IsClient || !_ready || message.Epoch != _epoch || message.Token != _token
            || message.Ship == Guid.Empty || message.Ship != _shipId) return;
        if (message.Kind == Kind.FirefightingBurst)
        {
            if (message.Sequence <= _receivedFirefightingBurstSequence || message.FirefightingBurst is not { } burst) return;
            _receivedFirefightingBurstSequence = message.Sequence;
            ShowFirefightingBurst(burst);
            return;
        }
        if (message.Firefighting is not { } chunk || !BeginPortableReceive(message)) return;
        long now = Now;
        if (message.Sequence > _incomingFirefightingSequence)
        {
            _incomingFirefightingSequence = message.Sequence;
            _incomingFirefighting = new(message.Ship, _incomingPortables!.StartedAt);
        }
        var incoming = _incomingFirefighting;
        if (incoming == null) return;
        if (incoming.Ship != message.Ship || now - incoming.StartedAt >= PortableTransportLayout.Timeout)
        {
            DiscardPortableAssembly();
            return;
        }
        int index = chunk.Section == 0 ? 0 : 1 + chunk.Offset / FirefightingChunkSize;
        if (incoming.Chunks[index] is { } existing)
        {
            if (!SameFirefightingChunk(existing, chunk)) DiscardPortableAssembly();
            return;
        }
        incoming.Chunks[index] = chunk;
        if (++incoming.ReceivedChunks != incoming.Chunks.Length) return;
        var items = ImmutableArray.CreateBuilder<BoxStationExtinguisherState>(BoxStationFirefighting.GetItems(BoxStationLayout.Current).Length);
        for (int part = 1; part < incoming.Chunks.Length; part++) items.AddRange(incoming.Chunks[part]!.Items);
        var metadata = incoming.Chunks[0]!;
        var state = new BoxStationFirefightingState(BoxStationLayout.Current.LayoutHash, items.MoveToImmutable(),
            ImmutableArray.CreateRange(metadata.CabinetOpen), ImmutableArray.CreateRange(metadata.TankWater));
        if (!BoxStationFirefighting.ValidState(BoxStationLayout.Current, state))
        {
            DiscardPortableAssembly();
            return;
        }
        _incomingPortables!.Firefighting = state;
        TryCommitPortables();
    }

    private static bool SameFirefightingChunk(FirefightingChunk left, FirefightingChunk right)
    {
        if (!left.CabinetOpen.AsSpan().SequenceEqual(right.CabinetOpen)
            || left.Items.Length != right.Items.Length || left.TankWater.Length != right.TankWater.Length) return false;
        for (int index = 0; index < left.TankWater.Length; index++)
            if (!SameFirefightingFloat(left.TankWater[index], right.TankWater[index])) return false;
        for (int index = 0; index < left.Items.Length; index++)
        {
            var a = left.Items[index]; var b = right.Items[index];
            if (a.Safety != b.Safety || a.Place != b.Place || a.Cabinet != b.Cabinet || a.Actor != b.Actor || a.Holder != b.Holder
                || a.Storage != b.Storage || a.Slot != b.Slot || a.ShipSlot != b.ShipSlot
                || !SameFirefightingFloat(a.Water, b.Water) || !SameFirefightingFloat(a.CooldownSeconds, b.CooldownSeconds)
                || !SameFirefightingFloat(a.Position.X, b.Position.X) || !SameFirefightingFloat(a.Position.Y, b.Position.Y)
                || !SameFirefightingFloat(a.Position.Z, b.Position.Z)) return false;
        }
        return true;
    }

    private static bool SameFirefightingFloat(float left, float right) =>
        BitConverter.SingleToInt32Bits(left) == BitConverter.SingleToInt32Bits(right);

    private void BroadcastFirefightingBurst(int item, Vector3 origin, Vector3[] endpoints, float units, bool ship = false)
    {
        if (IsClient || !_ready || _shipId == Guid.Empty) return;
        var burst = new BoxStationWaterBurst(item, checked(++_firefightingBurstSequence), origin, endpoints, units, ship);
        if (!ValidFirefightingBurst(burst)) throw new InvalidDataException("Invalid water burst.");
        burst = burst with { Endpoints = (Vector3[])endpoints.Clone() };
        MarkFirefightingDirty();
        ShowFirefightingBurst(burst);
        foreach (var peer in _peers.Values)
            if (PeerCurrent(peer)) _event?.FirefightingBurst(peer, _shipId, burst);
    }

    private static bool ValidFirefightingBurst(BoxStationWaterBurst burst)
    {
        if (burst.Item < 0 || burst.Item >= BoxStationFirefighting.GetItems(BoxStationLayout.Current).Length
            || burst.Sequence <= 0 || !float.IsFinite(burst.Units) || burst.Units < 1 || burst.Units > 5
            || burst.Endpoints == null || burst.Endpoints.Length != 5 || !FirefightingPoint(burst.Origin, burst.Ship)) return false;
        foreach (var endpoint in burst.Endpoints)
            if (!FirefightingPoint(endpoint, burst.Ship) || (endpoint - burst.Origin).sqrMagnitude > 6.01f * 6.01f) return false;
        return true;
    }

    private static bool FirefightingPoint(Vector3 point, bool ship = false) => Finite(point) && (ship
        ? MathF.Abs(point.x) <= 64 && MathF.Abs(point.y) <= 64 && MathF.Abs(point.z) <= 64
        : point.y >= 0 && point.y <= BoxStationLayout.DeckHeight && BoxStationLayout.Current.CellAt(point) >= 0);

    private sealed class FirefightingSnapshot(BoxStationFirefightingState state, Guid epoch, Guid ship, long sequence)
    {
        internal readonly BoxStationFirefightingState State = state;
        internal readonly Guid Epoch = epoch, Ship = ship;
        internal readonly long Sequence = sequence;
        internal readonly int ChunkCount = 1 + (state.Items.Length + FirefightingChunkSize - 1) / FirefightingChunkSize;
        internal readonly List<StationPeer> Peers = new();
    }

    private sealed class FirefightingChunk(byte section, int offset, BoxStationExtinguisherState[] items,
        bool[] cabinetOpen, float[] tankWater)
    {
        internal readonly byte Section = section;
        internal readonly int Offset = offset;
        internal readonly BoxStationExtinguisherState[] Items = items;
        internal readonly bool[] CabinetOpen = cabinetOpen;
        internal readonly float[] TankWater = tankWater;
    }

    private sealed class FirefightingAssembly(Guid ship, long startedAt)
    {
        internal readonly Guid Ship = ship;
        internal readonly long StartedAt = startedAt;
        internal readonly FirefightingChunk?[] Chunks = new FirefightingChunk?[1
            + (BoxStationFirefighting.GetItems(BoxStationLayout.Current).Length + FirefightingChunkSize - 1) / FirefightingChunkSize];
        internal int ReceivedChunks;
    }

    private sealed partial class SpacecraftEvent
    {
        // The 104-byte header and twelve 54-byte items total 752 bytes, below the 768-byte limit.
        internal void Firefighting(Peer peer, Guid token, FirefightingSnapshot snapshot, int chunk) =>
            Emit(peer, (Token: token, Snapshot: snapshot, Chunk: chunk), static (writer, state) =>
        {
            var value = state.Snapshot.State;
            Head(writer, Kind.FirefightingSnapshot); GuidValue(writer, state.Snapshot.Epoch); GuidValue(writer, state.Token);
            writer.Write(state.Snapshot.Sequence); GuidValue(writer, state.Snapshot.Ship); WriteFirefightingLayout(writer);
            writer.Write(checked((ushort)value.Items.Length)); writer.Write(checked((ushort)value.CabinetOpen.Length));
            writer.Write(checked((ushort)value.TankWater.Length));
            int offset = state.Chunk == 0 ? 0 : (state.Chunk - 1) * FirefightingChunkSize;
            int count = state.Chunk == 0 ? 0 : Math.Min(FirefightingChunkSize, value.Items.Length - offset);
            writer.Write((byte)(state.Chunk == 0 ? 0 : 1)); writer.Write(checked((ushort)offset)); writer.Write((byte)count);
            if (state.Chunk == 0)
            {
                foreach (bool open in value.CabinetOpen) writer.Write(open);
                foreach (float water in value.TankWater) writer.Write(water);
                return;
            }
            for (int index = offset; index < offset + count; index++)
            {
                var item = value.Items[index];
                writer.Write(item.Water); writer.Write(item.Safety); writer.Write(item.CooldownSeconds);
                writer.Write((byte)item.Place); writer.Write(item.Cabinet);
                writer.Write(item.Position.X); writer.Write(item.Position.Y); writer.Write(item.Position.Z);
                writer.Write(item.Actor); GuidValue(writer, item.Holder);
                writer.Write(item.Storage); writer.Write(item.Slot); writer.Write(item.ShipSlot);
            }
        });

        internal void FirefightingBurst(Peer peer, Guid ship, BoxStationWaterBurst burst) =>
            Emit(peer, (Event: this, Peer: peer, Ship: ship, Burst: burst), static (writer, state) =>
        {
            var value = state.Burst;
            Head(writer, Kind.FirefightingBurst); state.Event.Envelope(writer, state.Peer);
            writer.Write(value.Sequence); GuidValue(writer, state.Ship); WriteFirefightingLayout(writer);
            writer.Write(checked((ushort)value.Item)); writer.Write(value.Units); writer.Write((byte)value.Endpoints.Length);
            Vector(writer, value.Origin);
            foreach (var endpoint in value.Endpoints) Vector(writer, endpoint);
            writer.Write(value.Ship);
        });

        private static void WriteFirefightingLayout(BinaryWriter writer)
        {
            writer.Write(OrbitalStationOrbit.BodyId);
            writer.Write(StationLayoutFingerprint.Value);
        }

        private static void ReadFirefightingLayout(BinaryReader reader)
        {
            if (reader.ReadInt32() != OrbitalStationOrbit.BodyId) throw new InvalidDataException();
            Span<byte> fingerprint = stackalloc byte[32];
            ReadExactly(reader, fingerprint);
            if (!fingerprint.SequenceEqual(StationLayoutFingerprint.Value)) throw new InvalidDataException();
        }

        private static FirefightingChunk ReadFirefighting(BinaryReader reader)
        {
            ReadFirefightingLayout(reader);
            var layout = BoxStationLayout.Current;
            int items = reader.ReadUInt16(), cabinets = reader.ReadUInt16(), tanks = reader.ReadUInt16();
            if (items != BoxStationFirefighting.GetItems(layout).Length || cabinets != BoxStationFirefighting.GetCabinets(layout).Length
                || tanks != BoxStationFirefighting.GetTanks(layout).Length) throw new InvalidDataException();
            byte section = reader.ReadByte();
            int offset = reader.ReadUInt16(), count = reader.ReadByte();
            if (section == 0)
            {
                if (offset != 0 || count != 0) throw new InvalidDataException();
                var open = new bool[cabinets]; var water = new float[tanks];
                for (int index = 0; index < open.Length; index++) open[index] = Boolean(reader);
                var definitions = BoxStationFirefighting.GetTanks(layout);
                for (int index = 0; index < water.Length; index++) water[index] = Range(reader, 0, definitions[index].Capacity);
                return new(section, offset, Array.Empty<BoxStationExtinguisherState>(), open, water);
            }
            if (section != 1 || offset >= items || offset % FirefightingChunkSize != 0
                || count != Math.Min(FirefightingChunkSize, items - offset)) throw new InvalidDataException();
            var states = new BoxStationExtinguisherState[count];
            for (int index = 0; index < count; index++)
            {
                float water = Range(reader, 0, BoxStationFirefighting.ExtinguisherCapacity);
                bool safety = Boolean(reader);
                float cooldown = Range(reader, 0, BoxStationFirefighting.SprayCooldownSeconds);
                var place = (BoxStationExtinguisherPlace)reader.ReadByte();
                short cabinet = reader.ReadInt16();
                var position = Vector(reader);
                ulong actor = reader.ReadUInt64(); var holder = GuidValue(reader);
                int storage = reader.ReadInt32(); byte slot = reader.ReadByte(), shipSlot = reader.ReadByte();
                var point = new System.Numerics.Vector3(position.x, position.y, position.z);
                if (place is < BoxStationExtinguisherPlace.Cabinet or > BoxStationExtinguisherPlace.ShipStored
                    || cabinet < -1 || cabinet >= cabinets || !FirefightingPoint(position)
                    || place != BoxStationExtinguisherPlace.Stored && (storage != -1 || slot != 0)
                    || place == BoxStationExtinguisherPlace.Stored && (!safety || cabinet != -1 || actor != 0 || holder != Guid.Empty
                        || !BoxStationStorage.ValidMembership(layout, storage, slot, point))
                    || shipSlot != BoxStationFirefighting.NoShipSlot
                        && (shipSlot >= BoxStationFirefighting.ShipSlotCount
                            || place is not (BoxStationExtinguisherPlace.ShipStored or BoxStationExtinguisherPlace.Held))
                    || place == BoxStationExtinguisherPlace.ShipStored
                        && (shipSlot == BoxStationFirefighting.NoShipSlot || !safety || cabinet != -1 || actor != 0
                            || holder != Guid.Empty || !BoxStationFirefighting.ValidDropPosition(layout, point)))
                    throw new InvalidDataException();
                states[index] = new(water, safety, cooldown, place, cabinet, point, actor, holder, storage, slot, shipSlot);
            }
            return new(section, offset, states, Array.Empty<bool>(), Array.Empty<float>());
        }

        private static BoxStationWaterBurst ReadFirefightingBurst(BinaryReader reader, long sequence)
        {
            ReadFirefightingLayout(reader);
            int item = reader.ReadUInt16(); float units = reader.ReadSingle();
            if (reader.ReadByte() != 5) throw new InvalidDataException();
            var origin = Vector(reader); var endpoints = new Vector3[5];
            for (int index = 0; index < endpoints.Length; index++) endpoints[index] = Vector(reader);
            var burst = new BoxStationWaterBurst(item, sequence, origin, endpoints, units, Boolean(reader));
            return ValidFirefightingBurst(burst) ? burst : throw new InvalidDataException();
        }
    }
}
