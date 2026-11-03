// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Numerics;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftService
{
    private const int EngineeringToolChunkSize = 12, EngineeringMaintenanceChunkSize = 58, EngineeringWorkChunkSize = BoxStationEngineering.MaximumWork;
    private const int EngineeringFuelChunkSize = 321;
    private const int EngineeringChunksPerSend = 3;
    private const long EngineeringSendInterval = 100;
    private EngineeringSnapshot? _outgoingEngineering;
    private EngineeringAssembly? _incomingEngineering;
    private long _incomingEngineeringSequence, _nextEngineeringSendAt;

    private BoxStationEngineeringState? CurrentEngineeringState => CurrentPortablesState?.Engineering;

    private void ResetEngineeringTransport()
    {
        ResetEngineeringInput();
        _outgoingEngineering = null;
        _incomingEngineering = null;
        _incomingEngineeringSequence = _nextEngineeringSendAt = 0;
    }

    private void MarkEngineeringDirty() => MarkPortablesDirty();

    private void SendEngineeringSnapshots()
    {
        long now = Now;
        ExpirePortableAssembly(now);
        if (_outgoingEngineering is not { } snapshot || _event == null || now < _nextEngineeringSendAt) return;
        _nextEngineeringSendAt = now + EngineeringSendInterval;
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
            int end = Math.Min(snapshot.ChunkCount, pending.Chunk + EngineeringChunksPerSend);
            while (pending.Chunk < end)
            {
                _event.Engineering(peer, pending.Token, snapshot, pending.Chunk);
                pending.Chunk++;
            }
            if (pending.Chunk == snapshot.ChunkCount) snapshot.Peers.RemoveAt(index);
        }
        if (snapshot.Peers.Count == 0) _outgoingEngineering = null;
    }

    private void ReceiveEngineering(Message message)
    {
        if (message.Engineering is not { } chunk || !BeginPortableReceive(message)) return;
        if (_portablesState?.Engineering is { } applied && chunk.HullRevision < applied.HullRevision
            || message.Sequence > _incomingEngineeringSequence && _incomingEngineering is { } previous
                && chunk.HullRevision < previous.HullRevision)
        {
            DiscardPortableAssembly();
            return;
        }
        long now = Now;
        if (message.Sequence > _incomingEngineeringSequence)
        {
            _incomingEngineeringSequence = message.Sequence;
            _incomingEngineering = new(message.Epoch, message.Token, message.Ship, chunk, _incomingPortables!.StartedAt);
        }
        var incoming = _incomingEngineering;
        if (incoming == null) return;
        if (incoming.Epoch != message.Epoch || incoming.Token != message.Token || incoming.Ship != message.Ship
            || incoming.HullRevision != chunk.HullRevision || incoming.Tools.Length != chunk.ToolCount
            || incoming.Maintenance.Length != chunk.MaintenanceCount || incoming.Work.Length != chunk.WorkCount
            || incoming.TankFuel.Length != chunk.TankCount
            || now - incoming.StartedAt >= PortableTransportLayout.Timeout)
        {
            DiscardPortableAssembly();
            return;
        }
        int index = chunk.Section switch
        {
            0 => chunk.Offset / EngineeringToolChunkSize,
            1 => EngineeringChunkCount(chunk.ToolCount, EngineeringToolChunkSize) + chunk.Offset / EngineeringMaintenanceChunkSize,
            2 => EngineeringChunkCount(chunk.ToolCount, EngineeringToolChunkSize)
                + EngineeringChunkCount(chunk.MaintenanceCount, EngineeringMaintenanceChunkSize) + chunk.Offset / EngineeringWorkChunkSize,
            _ => EngineeringChunkCount(chunk.ToolCount, EngineeringToolChunkSize)
                + EngineeringChunkCount(chunk.MaintenanceCount, EngineeringMaintenanceChunkSize)
                + EngineeringChunkCount(chunk.WorkCount, EngineeringWorkChunkSize) + chunk.Offset / EngineeringFuelChunkSize
        };
        if (incoming.Chunks[index])
        {
            bool same = chunk.Section switch
            {
                0 => SameEngineeringTools(incoming.Tools.AsSpan(chunk.Offset, chunk.Tools.Length), chunk.Tools),
                1 => SameEngineeringMaintenance(incoming.Maintenance.AsSpan(chunk.Offset, chunk.Maintenance.Length), chunk.Maintenance),
                2 => SameEngineeringWork(incoming.Work.AsSpan(chunk.Offset, chunk.Work.Length), chunk.Work),
                _ => incoming.TankFuel.AsSpan(chunk.Offset, chunk.TankFuel.Length).SequenceEqual(chunk.TankFuel)
            };
            if (!same) DiscardPortableAssembly();
            return;
        }
        if (chunk.Section == 0) chunk.Tools.CopyTo(incoming.Tools, chunk.Offset);
        else if (chunk.Section == 1) chunk.Maintenance.CopyTo(incoming.Maintenance, chunk.Offset);
        else if (chunk.Section == 2) chunk.Work.CopyTo(incoming.Work, chunk.Offset);
        else chunk.TankFuel.CopyTo(incoming.TankFuel, chunk.Offset);
        incoming.Chunks[index] = true;
        if (++incoming.ReceivedChunks != incoming.Chunks.Length) return;
        var state = new BoxStationEngineeringState(BoxStationLayout.Current.LayoutHash, incoming.HullRevision,
            ImmutableArray.CreateRange(incoming.Tools), ImmutableArray.CreateRange(incoming.Maintenance), ImmutableArray.CreateRange(incoming.Work),
            ImmutableArray.CreateRange(incoming.TankFuel));
        // Hull is a separate stream; domain validation checks this capture's embedded integrity.
        if (!BoxStationEngineering.ValidState(BoxStationLayout.Current, state))
        {
            DiscardPortableAssembly();
            return;
        }
        _incomingPortables!.Engineering = state;
        TryCommitPortables();
    }

    private static bool SameEngineeringFloat(float left, float right) =>
        BitConverter.SingleToInt32Bits(left) == BitConverter.SingleToInt32Bits(right);

    private static bool SameEngineeringVector(Vector3 left, Vector3 right) => SameEngineeringFloat(left.X, right.X)
        && SameEngineeringFloat(left.Y, right.Y) && SameEngineeringFloat(left.Z, right.Z);

    private static bool SameEngineeringTools(ReadOnlySpan<BoxStationTool> left, ReadOnlySpan<BoxStationTool> right)
    {
        if (left.Length != right.Length) return false;
        for (int index = 0; index < left.Length; index++)
        {
            var a = left[index]; var b = right[index];
            if (a.Id != b.Id || a.Place != b.Place || a.Actor != b.Actor || a.Holder != b.Holder
                || a.Storage != b.Storage || a.Slot != b.Slot || a.ShipSlot != b.ShipSlot
                || a.Fuel != b.Fuel || a.Lit != b.Lit || !SameEngineeringVector(a.Position, b.Position)
                || !SameEngineeringFloat(a.BurnSeconds, b.BurnSeconds)) return false;
        }
        return true;
    }

    private static bool SameEngineeringMaintenance(ReadOnlySpan<BoxStationMaintenance> left, ReadOnlySpan<BoxStationMaintenance> right)
    {
        if (left.Length != right.Length) return false;
        for (int index = 0; index < left.Length; index++)
        {
            var a = left[index]; var b = right[index];
            if (a.StructureId != b.StructureId || a.Stage != b.Stage || a.Anchored != b.Anchored
                || a.Integrity != b.Integrity || !SameEngineeringFloat(a.HeatRemaining, b.HeatRemaining)) return false;
        }
        return true;
    }

    private static bool SameEngineeringWork(ReadOnlySpan<BoxStationWork> left, ReadOnlySpan<BoxStationWork> right)
    {
        if (left.Length != right.Length) return false;
        for (int index = 0; index < left.Length; index++)
        {
            var a = left[index]; var b = right[index];
            if (a.Holder != b.Holder || a.Actor != b.Actor || a.Intent.Tool != b.Intent.Tool
                || a.Intent.Kind != b.Intent.Kind || a.Intent.Target != b.Intent.Target || a.Intent.Mode != b.Intent.Mode
                || !SameEngineeringVector(a.Intent.Contact, b.Intent.Contact) || !SameEngineeringVector(a.Feet, b.Feet)
                || !SameEngineeringFloat(a.Elapsed, b.Elapsed) || !SameEngineeringFloat(a.Duration, b.Duration)) return false;
        }
        return true;
    }

    private static int EngineeringChunkCount(int count, int size) => Math.Max(1, (count + size - 1) / size);
    private static int EngineeringChunks(int tools, int maintenance, int work, int tanks) => EngineeringChunkCount(tools, EngineeringToolChunkSize)
        + EngineeringChunkCount(maintenance, EngineeringMaintenanceChunkSize) + EngineeringChunkCount(work, EngineeringWorkChunkSize)
        + EngineeringChunkCount(tanks, EngineeringFuelChunkSize);
    private static long EngineeringTransferTimeout(int tools, int maintenance, int work, int tanks) => 1500 + 4 * EngineeringSendInterval
        * ((EngineeringChunks(tools, maintenance, work, tanks) + EngineeringChunksPerSend - 1) / EngineeringChunksPerSend);

    private static class EngineeringTransportLayout
    {
        internal static readonly ImmutableArray<BoxStationToolSource> Sources = BoxStationEngineering.GetSources(BoxStationLayout.Current);
        internal static readonly ImmutableArray<BoxStationFuelTank> FuelTanks = BoxStationEngineering.GetFuelTanks(BoxStationLayout.Current);
        internal static readonly int MaxMaintenance = BoxStationHull.GetStructures(BoxStationLayout.Current).Length;
    }

    private sealed class EngineeringSnapshot(BoxStationEngineeringState state, Guid epoch, Guid ship, long sequence)
    {
        internal readonly BoxStationEngineeringState State = state;
        internal readonly Guid Epoch = epoch, Ship = ship;
        internal readonly long Sequence = sequence;
        internal readonly int ToolChunks = EngineeringChunkCount(state.Tools.Length, EngineeringToolChunkSize);
        internal readonly int MaintenanceChunks = EngineeringChunkCount(state.Maintenance.Length, EngineeringMaintenanceChunkSize);
        internal readonly int WorkChunks = EngineeringChunkCount(state.Work.Length, EngineeringWorkChunkSize);
        internal readonly int ChunkCount = EngineeringChunks(state.Tools.Length, state.Maintenance.Length, state.Work.Length, state.TankFuel.Length);
        internal readonly List<StationPeer> Peers = new();
    }

    private sealed class EngineeringChunk(long hullRevision, int toolCount, int maintenanceCount, int workCount, int tankCount,
        byte section, int offset, BoxStationTool[] tools, BoxStationMaintenance[] maintenance, BoxStationWork[] work, ushort[] tankFuel)
    {
        internal readonly long HullRevision = hullRevision;
        internal readonly int ToolCount = toolCount, MaintenanceCount = maintenanceCount, WorkCount = workCount, TankCount = tankCount, Offset = offset;
        internal readonly byte Section = section;
        internal readonly BoxStationTool[] Tools = tools;
        internal readonly BoxStationMaintenance[] Maintenance = maintenance;
        internal readonly BoxStationWork[] Work = work;
        internal readonly ushort[] TankFuel = tankFuel;
    }

    private sealed class EngineeringAssembly(Guid epoch, Guid token, Guid ship, EngineeringChunk chunk, long startedAt)
    {
        internal readonly Guid Epoch = epoch, Token = token, Ship = ship;
        internal readonly long StartedAt = startedAt, HullRevision = chunk.HullRevision;
        internal readonly BoxStationTool[] Tools = new BoxStationTool[chunk.ToolCount];
        internal readonly BoxStationMaintenance[] Maintenance = new BoxStationMaintenance[chunk.MaintenanceCount];
        internal readonly BoxStationWork[] Work = new BoxStationWork[chunk.WorkCount];
        internal readonly ushort[] TankFuel = new ushort[chunk.TankCount];
        internal readonly bool[] Chunks = new bool[EngineeringChunks(chunk.ToolCount, chunk.MaintenanceCount, chunk.WorkCount, chunk.TankCount)];
        internal int ReceivedChunks;
    }

    private sealed partial class SpacecraftEvent
    {
        // The 125-byte header leaves 643 bytes: tools 12x52, maintenance 58x11, work 3x66, tank fuel 321x2.
        internal void Engineering(Peer peer, Guid token, EngineeringSnapshot snapshot, int chunk) =>
            Emit(peer, (Token: token, Snapshot: snapshot, Chunk: chunk), static (writer, state) =>
        {
            var capture = state.Snapshot;
            var value = capture.State;
            Head(writer, Kind.Engineering); GuidValue(writer, capture.Epoch); GuidValue(writer, state.Token);
            writer.Write(capture.Sequence); GuidValue(writer, capture.Ship);
            writer.Write(OrbitalStationOrbit.BodyId); writer.Write(StationLayoutFingerprint.Value);
            writer.Write(value.HullRevision);
            writer.Write(value.Tools.Length); writer.Write(value.Maintenance.Length); writer.Write(value.Work.Length);
            writer.Write(value.TankFuel.Length);
            byte section = state.Chunk < capture.ToolChunks ? (byte)0
                : state.Chunk < capture.ToolChunks + capture.MaintenanceChunks ? (byte)1
                : state.Chunk < capture.ToolChunks + capture.MaintenanceChunks + capture.WorkChunks ? (byte)2 : (byte)3;
            int offset = section == 0 ? state.Chunk * EngineeringToolChunkSize
                : section == 1 ? (state.Chunk - capture.ToolChunks) * EngineeringMaintenanceChunkSize
                : section == 2 ? (state.Chunk - capture.ToolChunks - capture.MaintenanceChunks) * EngineeringWorkChunkSize
                : (state.Chunk - capture.ToolChunks - capture.MaintenanceChunks - capture.WorkChunks) * EngineeringFuelChunkSize;
            int count = section == 0 ? Math.Min(EngineeringToolChunkSize, value.Tools.Length - offset)
                : section == 1 ? Math.Min(EngineeringMaintenanceChunkSize, value.Maintenance.Length - offset)
                : section == 2 ? Math.Min(EngineeringWorkChunkSize, value.Work.Length - offset)
                : Math.Min(EngineeringFuelChunkSize, value.TankFuel.Length - offset);
            writer.Write(section); writer.Write(offset); writer.Write(checked((ushort)count));
            for (int index = offset; index < offset + count; index++)
            {
                if (section == 0)
                {
                    var tool = value.Tools[index];
                    writer.Write(tool.Id); writer.Write((byte)tool.Place); EngineeringVector(writer, tool.Position);
                    writer.Write(tool.Actor); GuidValue(writer, tool.Holder); writer.Write(tool.Fuel);
                    writer.Write(tool.Lit); writer.Write(tool.BurnSeconds);
                    writer.Write(tool.Storage); writer.Write(tool.Slot); writer.Write(tool.ShipSlot);
                }
                else if (section == 1)
                {
                    var maintenance = value.Maintenance[index];
                    writer.Write(maintenance.StructureId); writer.Write(maintenance.Stage); writer.Write(maintenance.Anchored);
                    writer.Write(maintenance.HeatRemaining); writer.Write(maintenance.Integrity);
                }
                else if (section == 2)
                {
                    var work = value.Work[index];
                    GuidValue(writer, work.Holder); writer.Write(work.Actor); EngineeringIntent(writer, work.Intent);
                    EngineeringVector(writer, work.Feet); writer.Write(work.Elapsed); writer.Write(work.Duration);
                }
                else writer.Write(value.TankFuel[index]);
            }
        });

        // Active input is 105 bytes; release is 83 bytes and carries no unused intent.
        internal void EngineeringWork(long sequence, long gesture, bool active, BoxStationWorkIntent intent) =>
            Emit(null, (Event: this, Owner: service, Sequence: sequence, Gesture: gesture, Active: active, Intent: intent), static (writer, state) =>
        {
            if (state.Sequence <= 0 || state.Gesture <= 0 || state.Owner._shipId == Guid.Empty || state.Owner._clientLease == Guid.Empty
                || state.Active && !BoxStationEngineering.ValidIntent(BoxStationLayout.Current, state.Intent))
                throw new InvalidDataException();
            Head(writer, Kind.EngineeringWork); state.Event.Envelope(writer, null); writer.Write(state.Sequence);
            GuidValue(writer, state.Owner._shipId); GuidValue(writer, state.Owner._clientLease);
            writer.Write(state.Gesture); writer.Write(state.Active);
            if (state.Active) EngineeringIntent(writer, state.Intent);
        });

        private static void EngineeringVector(BinaryWriter writer, Vector3 value)
        {
            writer.Write(value.X); writer.Write(value.Y); writer.Write(value.Z);
        }

        private static Vector3 ReadEngineeringVector(BinaryReader reader)
        {
            var value = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z)) throw new InvalidDataException();
            return value;
        }

        private static void EngineeringIntent(BinaryWriter writer, BoxStationWorkIntent intent)
        {
            writer.Write(intent.Tool); writer.Write((byte)intent.Kind); writer.Write(intent.Target); writer.Write((byte)intent.Mode);
            EngineeringVector(writer, intent.Contact);
        }

        private static BoxStationWorkIntent ReadEngineeringIntent(BinaryReader reader)
        {
            var intent = new BoxStationWorkIntent(reader.ReadInt32(), (BoxStationWorkTarget)reader.ReadByte(),
                reader.ReadInt32(), (BoxStationWorkMode)reader.ReadByte(), ReadEngineeringVector(reader));
            if (!BoxStationEngineering.ValidIntent(BoxStationLayout.Current, intent)) throw new InvalidDataException();
            return intent;
        }

        private static EngineeringChunk ReadEngineering(BinaryReader reader)
        {
            if (reader.ReadInt32() != OrbitalStationOrbit.BodyId) throw new InvalidDataException();
            Span<byte> fingerprint = stackalloc byte[32];
            ReadExactly(reader, fingerprint);
            if (!fingerprint.SequenceEqual(StationLayoutFingerprint.Value)) throw new InvalidDataException();
            long hullRevision = reader.ReadInt64();
            int toolCount = reader.ReadInt32(), maintenanceCount = reader.ReadInt32(), workCount = reader.ReadInt32();
            int tankCount = reader.ReadInt32();
            byte section = reader.ReadByte();
            int offset = reader.ReadInt32(), count = reader.ReadUInt16();
            var sources = EngineeringTransportLayout.Sources;
            var fuelTanks = EngineeringTransportLayout.FuelTanks;
            if (hullRevision < 0 || toolCount != sources.Length
                || maintenanceCount < 0 || maintenanceCount > EngineeringTransportLayout.MaxMaintenance
                || workCount < 0 || workCount > EngineeringWorkChunkSize || tankCount != fuelTanks.Length || section > 3)
                throw new InvalidDataException();
            int total = section == 0 ? toolCount : section == 1 ? maintenanceCount : section == 2 ? workCount : tankCount;
            int size = section == 0 ? EngineeringToolChunkSize : section == 1 ? EngineeringMaintenanceChunkSize
                : section == 2 ? EngineeringWorkChunkSize : EngineeringFuelChunkSize;
            if (offset < 0 || offset % size != 0 || (total == 0 ? offset != 0 : offset >= total)
                || count != Math.Min(size, total - offset)) throw new InvalidDataException();
            var tools = section == 0 ? new BoxStationTool[count] : Array.Empty<BoxStationTool>();
            var maintenance = section == 1 ? new BoxStationMaintenance[count] : Array.Empty<BoxStationMaintenance>();
            var work = section == 2 ? new BoxStationWork[count] : Array.Empty<BoxStationWork>();
            var tankFuel = section == 3 ? new ushort[count] : Array.Empty<ushort>();
            int previous = -1;
            for (int index = 0; index < count; index++)
            {
                if (section == 0)
                {
                    int id = reader.ReadInt32();
                    var place = (BoxStationToolPlace)reader.ReadByte();
                    var position = ReadEngineeringVector(reader);
                    ulong actor = reader.ReadUInt64(); var holder = GuidValue(reader);
                    byte fuel = reader.ReadByte(); bool lit = Boolean(reader); float burn = Range(reader, 0, BoxStationEngineering.FuelSeconds);
                    int storage = reader.ReadInt32(); byte slot = reader.ReadByte(), shipSlot = reader.ReadByte();
                    if (id != offset + index || place is < BoxStationToolPlace.Source or > BoxStationToolPlace.ShipStored
                        || place != BoxStationToolPlace.Stored && (storage != -1 || slot != 0)
                        || place == BoxStationToolPlace.Stored
                            && (lit || !BoxStationStorage.ValidMembership(BoxStationLayout.Current, storage, slot, position))
                        || shipSlot != BoxStationEngineering.NoShipSlot
                            && (shipSlot >= BoxStationEngineering.ShipSlotCount || shipSlot != (byte)sources[id].Kind
                                || place is not (BoxStationToolPlace.ShipStored or BoxStationToolPlace.Held)
                                || !BoxStationFirefighting.ValidDropPosition(BoxStationLayout.Current, position))
                        || place == BoxStationToolPlace.ShipStored && (shipSlot == BoxStationEngineering.NoShipSlot || lit)
                        || fuel > sources[id].FuelCapacity || burn >= BoxStationEngineering.FuelSeconds || lit && fuel == 0
                        || (place == BoxStationToolPlace.Held ? holder == Guid.Empty : holder != Guid.Empty || actor != 0))
                        throw new InvalidDataException();
                    tools[index] = new(id, place, position, actor, holder, fuel, lit, burn, storage, slot, shipSlot);
                }
                else if (section == 1)
                {
                    int id = reader.ReadInt32(); byte stage = reader.ReadByte(); bool anchored = Boolean(reader);
                    float heat = Range(reader, 0, BoxStationEngineering.HeatSeconds); byte integrity = reader.ReadByte();
                    if (id <= previous || BoxStationHull.StructureTarget(BoxStationLayout.Current, id) < 0
                        || stage > 8 || integrity is 0 or > 100) throw new InvalidDataException();
                    maintenance[index] = new(id, stage, anchored, heat, integrity);
                    previous = id;
                }
                else if (section == 2)
                {
                    var holder = GuidValue(reader); ulong actor = reader.ReadUInt64();
                    var intent = ReadEngineeringIntent(reader); var feet = ReadEngineeringVector(reader);
                    float elapsed = Range(reader, 0, 20), duration = Range(reader, 0, 20);
                    if (holder == Guid.Empty || duration <= 0 || elapsed >= duration) throw new InvalidDataException();
                    work[index] = new(holder, actor, intent, feet, elapsed, duration);
                }
                else
                {
                    ushort fuel = reader.ReadUInt16();
                    if (fuel > fuelTanks[offset + index].Capacity) throw new InvalidDataException();
                    tankFuel[index] = fuel;
                }
            }
            return new(hullRevision, toolCount, maintenanceCount, workCount, tankCount, section, offset, tools, maintenance, work, tankFuel);
        }
    }
}
