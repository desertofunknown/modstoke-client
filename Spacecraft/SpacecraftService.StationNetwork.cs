// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftService
{
    private const int StationZoneChunkSize = 15, StationDoorChunkSize = 126, StationPowerChunkSize = 100;
    private const int StationPlumbingChunkSize = 24, StationAlarmChunkSize = 78, StationRadiatorChunkSize = 25;
    private const int StationRoomThermalChunkSize = 18;
    private const int StationFireAlarmChunkSize = 100, StationFireAreaChunkSize = 126;
    private const int StationRoomSpanChunkSize = 52, StationChunksPerSend = 5;
    private const int StationSmesChunkSize = 28, StationCableChunkSize = 768 - 138;
    private const long StationSendInterval = 100;
    private static readonly Lazy<byte[]> StationLayoutFingerprint = new(() =>
        Convert.FromHexString(BoxStationLayout.Current.LayoutHash));
    private StationSnapshot? _outgoingStation;
    private StationAssembly? _incomingStation;
    private long _incomingStationSequence, _nextStationSendAt, _stationFreshness;

    private bool StationSnapshotPending => _outgoingStation != null;

    private void ResetStationTransport()
    {
        _outgoingStation = null;
        _incomingStation = null;
        _incomingStationSequence = _nextStationSendAt = _stationFreshness = 0;
    }

    private void QueueStationSnapshot(BoxStationState state)
    {
        if (IsClient || !_ready || _outgoingStation != null) return;
        if (!BoxStationSystems.ValidState(BoxStationLayout.Current, state))
            throw new InvalidDataException("Invalid station telemetry.");
        var snapshot = new StationSnapshot(state, _epoch, checked(++_stationSequence));
        foreach (var pair in _peers)
            if (PeerCurrent(pair.Value)) snapshot.Peers.Add(new(pair.Key, pair.Value));
        if (snapshot.Peers.Count > 0) _outgoingStation = snapshot;
    }

    private void FlushStationSnapshots()
    {
        long now = Now;
        if (_incomingStation is { } incoming && now - incoming.StartedAt >= incoming.Timeout)
            _incomingStation = null;
        if (_outgoingStation is not { } snapshot) return;
        if (IsClient || !_ready || snapshot.Epoch != _epoch)
        {
            _outgoingStation = null;
            return;
        }
        if (_event == null || now < _nextStationSendAt) return;
        _nextStationSendAt = now + StationSendInterval;
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
            int end = Math.Min(snapshot.ChunkCount, pending.Chunk + StationChunksPerSend);
            while (pending.Chunk < end)
            {
                _event.Station(peer, pending.Token, snapshot, pending.Chunk);
                pending.Chunk++;
            }
            if (pending.Chunk == snapshot.ChunkCount) snapshot.Peers.RemoveAt(index);
        }
        if (snapshot.Peers.Count == 0) _outgoingStation = null;
    }

    private void ReceiveStation(Message message)
    {
        if (message.Sequence <= _receivedStationSequence || message.Sequence < _incomingStationSequence
            || message.Station is not { } chunk) return;
        long now = Now;
        if (message.Sequence > _incomingStationSequence)
        {
            _incomingStationSequence = message.Sequence;
            _incomingStation = new(chunk, now);
        }
        var incoming = _incomingStation;
        // A rejected or expired sequence cannot restart its assembly timeout.
        if (incoming == null) return;
        if (now - incoming.StartedAt >= incoming.Timeout
            || incoming.BatteryKwh != chunk.BatteryKwh || incoming.ReserveMoles != chunk.ReserveMoles
            || incoming.Powered != chunk.Powered || incoming.HullRevision != chunk.HullRevision
            || incoming.DoorRevision != chunk.DoorRevision || incoming.BuiltDoors.Length != chunk.BuiltDoorTotal
            || incoming.Zones.Length != chunk.RoomTotal || incoming.RoomSpans.Length != chunk.SpanTotal)
        {
            _incomingStation = null;
            return;
        }
        int index = chunk.Section == 0 ? chunk.Offset / StationZoneChunkSize
            : chunk.Section == 1 ? incoming.ZoneChunkCount + chunk.Offset / StationDoorChunkSize
            : chunk.Section == 2 ? incoming.ZoneChunkCount + incoming.DoorChunkCount + chunk.Offset / StationPowerChunkSize
            : chunk.Section == 3 ? incoming.ZoneChunkCount + incoming.DoorChunkCount + incoming.PowerChunkCount + chunk.Offset / StationPlumbingChunkSize
            : chunk.Section == 4 ? incoming.ZoneChunkCount + incoming.DoorChunkCount + incoming.PowerChunkCount + incoming.PlumbingChunkCount + chunk.Offset / StationAlarmChunkSize
            : chunk.Section == 5 ? incoming.ZoneChunkCount + incoming.DoorChunkCount + incoming.PowerChunkCount + incoming.PlumbingChunkCount + incoming.AlarmChunkCount + chunk.Offset / StationRadiatorChunkSize
            : chunk.Section == 6 ? incoming.ZoneChunkCount + incoming.DoorChunkCount + incoming.PowerChunkCount + incoming.PlumbingChunkCount + incoming.AlarmChunkCount + incoming.RadiatorChunkCount + chunk.Offset / StationRoomThermalChunkSize
            : chunk.Section == 7 ? incoming.ZoneChunkCount + incoming.DoorChunkCount + incoming.PowerChunkCount + incoming.PlumbingChunkCount + incoming.AlarmChunkCount + incoming.RadiatorChunkCount + incoming.RoomThermalChunkCount + chunk.Offset / StationFireAlarmChunkSize
            : chunk.Section == 8 ? incoming.ZoneChunkCount + incoming.DoorChunkCount + incoming.PowerChunkCount + incoming.PlumbingChunkCount + incoming.AlarmChunkCount + incoming.RadiatorChunkCount + incoming.RoomThermalChunkCount + incoming.FireAlarmChunkCount + chunk.Offset / StationFireAreaChunkSize
            : chunk.Section == 9 ? incoming.ZoneChunkCount + incoming.DoorChunkCount + incoming.PowerChunkCount + incoming.PlumbingChunkCount + incoming.AlarmChunkCount + incoming.RadiatorChunkCount + incoming.RoomThermalChunkCount + incoming.FireAlarmChunkCount + incoming.FireAreaChunkCount + chunk.Offset / StationRoomSpanChunkSize
            : chunk.Section == 10 ? incoming.ZoneChunkCount + incoming.DoorChunkCount + incoming.PowerChunkCount + incoming.PlumbingChunkCount + incoming.AlarmChunkCount + incoming.RadiatorChunkCount + incoming.RoomThermalChunkCount + incoming.FireAlarmChunkCount + incoming.FireAreaChunkCount + incoming.RoomSpanChunkCount + chunk.Offset / StationSmesChunkSize
            : chunk.Section == 11 ? incoming.Chunks.Length - 1 - StationCableChunkCount + chunk.Offset / StationCableChunkSize
            : incoming.Chunks.Length - 1;
        if (incoming.Chunks[index])
        {
            bool same = chunk.Section == 0
                ? incoming.Zones.AsSpan(chunk.Offset, chunk.Zones.Length).SequenceEqual(chunk.Zones)
                : chunk.Section == 1 ? incoming.Doors.AsSpan(chunk.Offset, chunk.Doors.Length).SequenceEqual(chunk.Doors)
                : chunk.Section == 2 ? incoming.Power.AsSpan(chunk.Offset, chunk.Power.Length).SequenceEqual(chunk.Power)
                : chunk.Section == 3 ? incoming.Plumbing.AsSpan(chunk.Offset, chunk.Plumbing.Length).SequenceEqual(chunk.Plumbing)
                : chunk.Section == 4 ? incoming.Alarms.AsSpan(chunk.Offset, chunk.Alarms.Length).SequenceEqual(chunk.Alarms)
                : chunk.Section == 5 ? incoming.Radiators.AsSpan(chunk.Offset, chunk.Radiators.Length).SequenceEqual(chunk.Radiators)
                : chunk.Section == 6 ? incoming.RoomThermal.AsSpan(chunk.Offset, chunk.RoomThermal.Length).SequenceEqual(chunk.RoomThermal)
                : chunk.Section == 7 ? incoming.FireAlarms.AsSpan(chunk.Offset, chunk.FireAlarms.Length).SequenceEqual(chunk.FireAlarms)
                : chunk.Section == 8 ? incoming.FireAreas.AsSpan(chunk.Offset, chunk.FireAreas.Length).SequenceEqual(chunk.FireAreas)
                : chunk.Section == 9 ? incoming.RoomSpans.AsSpan(chunk.Offset, chunk.RoomSpans.Length).SequenceEqual(chunk.RoomSpans)
                : chunk.Section == 10 ? incoming.Smes.AsSpan(chunk.Offset, chunk.Smes.Length).SequenceEqual(chunk.Smes)
                : chunk.Section == 11 ? incoming.Cables.AsSpan(chunk.Offset, chunk.Cables.Length).SequenceEqual(chunk.Cables)
                : incoming.BuiltDoors.AsSpan().SequenceEqual(chunk.BuiltDoors);
            if (!same) _incomingStation = null;
            return;
        }
        if (chunk.Section == 0) chunk.Zones.CopyTo(incoming.Zones, chunk.Offset);
        else if (chunk.Section == 1) chunk.Doors.CopyTo(incoming.Doors, chunk.Offset);
        else if (chunk.Section == 2) chunk.Power.CopyTo(incoming.Power, chunk.Offset);
        else if (chunk.Section == 3) chunk.Plumbing.CopyTo(incoming.Plumbing, chunk.Offset);
        else if (chunk.Section == 4) chunk.Alarms.CopyTo(incoming.Alarms, chunk.Offset);
        else if (chunk.Section == 5) chunk.Radiators.CopyTo(incoming.Radiators, chunk.Offset);
        else if (chunk.Section == 6) chunk.RoomThermal.CopyTo(incoming.RoomThermal, chunk.Offset);
        else if (chunk.Section == 7) chunk.FireAlarms.CopyTo(incoming.FireAlarms, chunk.Offset);
        else if (chunk.Section == 8) chunk.FireAreas.CopyTo(incoming.FireAreas, chunk.Offset);
        else if (chunk.Section == 9) chunk.RoomSpans.CopyTo(incoming.RoomSpans, chunk.Offset);
        else if (chunk.Section == 10) chunk.Smes.CopyTo(incoming.Smes, chunk.Offset);
        else if (chunk.Section == 11) chunk.Cables.CopyTo(incoming.Cables, chunk.Offset);
        else chunk.BuiltDoors.CopyTo(incoming.BuiltDoors, chunk.Offset);
        incoming.Chunks[index] = true;
        if (++incoming.ReceivedChunks != incoming.Chunks.Length) return;
        _incomingStation = null;
        var layout = BoxStationLayout.Current;
        var builtDoors = ImmutableArray.CreateRange(incoming.BuiltDoors);
        if (!BoxStationDoors.ValidEdges(layout, builtDoors)) return;
        if (_stationState is { } previousDoors && (incoming.DoorRevision < previousDoors.DoorRevision
            || incoming.DoorRevision == previousDoors.DoorRevision
                && !builtDoors.AsSpan().SequenceEqual(previousDoors.BuiltDoors.AsSpan()))) return;
        var cables = new BoxStationCableState(layout.LayoutHash, BinaryPrimitives.ReadInt64LittleEndian(incoming.Cables),
            ImmutableArray.Create(incoming.Cables, sizeof(long), incoming.Cables.Length - sizeof(long)));
        if (!BoxStationPower.ValidCableState(layout, cables)) return;
        if (_stationState is { } previousState && (cables.Revision < previousState.Cables.Revision
            || cables.Revision == previousState.Cables.Revision
                && !cables.Installed.AsSpan().SequenceEqual(previousState.Cables.Installed.AsSpan()))) return;
        var smes = ImmutableArray.CreateRange(incoming.Smes);
        if (!BoxStationPower.ValidSmesState(layout, smes, cables)) return;
        var state = new BoxStationState(BoxStationLayout.Current.LayoutHash,
            ImmutableArray.CreateRange(incoming.Zones), ImmutableArray.CreateRange(incoming.Doors),
            incoming.BatteryKwh, incoming.ReserveMoles, incoming.Powered, ImmutableArray.CreateRange(incoming.Power),
            ImmutableArray.CreateRange(incoming.Plumbing), ImmutableArray.CreateRange(incoming.Alarms),
            ImmutableArray.CreateRange(incoming.Radiators), ImmutableArray.CreateRange(incoming.RoomThermal),
            ImmutableArray.CreateRange(incoming.FireAlarms), ImmutableArray.CreateRange(incoming.FireAreas), incoming.HullRevision,
            ImmutableArray.CreateRange(incoming.RoomSpans), smes, cables, incoming.DoorRevision, builtDoors);
        if (!BoxStationSystems.ValidState(BoxStationLayout.Current, state)) return;
        _receivedStationSequence = message.Sequence;
        _stationState = state;
        _stationFreshness = incoming.Timeout + 1000;
        _lastStationAt = now;
    }

    private static int StationCableBytes => sizeof(long) + (BoxStationPower.GetCableSites(BoxStationLayout.Current).Length + 7) / 8;
    private static int StationCableChunkCount => (StationCableBytes + StationCableChunkSize - 1) / StationCableChunkSize;

    private static int StationChunkCount(int rooms, int roomSpans, int builtDoors)
    {
        var layout = BoxStationLayout.Current;
        return (rooms + StationZoneChunkSize - 1) / StationZoneChunkSize
            + (layout.Doors.Length + builtDoors + StationDoorChunkSize - 1) / StationDoorChunkSize
            + (BoxStationPower.GetApcs(layout).Length + StationPowerChunkSize - 1) / StationPowerChunkSize
            + (BoxStationPlumbing.GetDevices(layout).Length + StationPlumbingChunkSize - 1) / StationPlumbingChunkSize
            + (BoxStationAirAlarms.GetDevices(layout).Length + StationAlarmChunkSize - 1) / StationAlarmChunkSize
            + (BoxStationPlumbing.GetRadiators(layout).Length + StationRadiatorChunkSize - 1) / StationRadiatorChunkSize
            + (BoxStationRoomThermal.GetDevices(layout).Length + StationRoomThermalChunkSize - 1) / StationRoomThermalChunkSize
            + (BoxStationFireAlarms.GetDevices(layout).Length + StationFireAlarmChunkSize - 1) / StationFireAlarmChunkSize
            + (layout.Areas.Length + StationFireAreaChunkSize - 1) / StationFireAreaChunkSize
            + Math.Max(1, (roomSpans + StationRoomSpanChunkSize - 1) / StationRoomSpanChunkSize)
            + (BoxStationPower.GetSmes(layout).Length + StationSmesChunkSize - 1) / StationSmesChunkSize
            + StationCableChunkCount + 1;
    }

    // Allow the complete layout at the bounded send rate, with scheduling slack.
    private static long StationAssemblyTimeout(int rooms, int roomSpans, int builtDoors) => 3000 + 4 * StationSendInterval
        * ((StationChunkCount(rooms, roomSpans, builtDoors) + StationChunksPerSend - 1L) / StationChunksPerSend);

    private sealed class StationSnapshot(BoxStationState state, Guid epoch, long sequence)
    {
        internal readonly BoxStationState State = state;
        internal readonly Guid Epoch = epoch;
        internal readonly long Sequence = sequence;
        internal readonly int ZoneChunkCount = (state.Zones.Length + StationZoneChunkSize - 1) / StationZoneChunkSize;
        internal readonly int DoorChunkCount = (state.Doors.Length + StationDoorChunkSize - 1) / StationDoorChunkSize;
        internal readonly int PowerChunkCount = (state.Power.Length + StationPowerChunkSize - 1) / StationPowerChunkSize;
        internal readonly int PlumbingChunkCount = (state.Plumbing.Length + StationPlumbingChunkSize - 1) / StationPlumbingChunkSize;
        internal readonly int AlarmChunkCount = (state.Alarms.Length + StationAlarmChunkSize - 1) / StationAlarmChunkSize;
        internal readonly int RadiatorChunkCount = (state.Radiators.Length + StationRadiatorChunkSize - 1) / StationRadiatorChunkSize;
        internal readonly int RoomThermalChunkCount = (state.RoomThermal.Length + StationRoomThermalChunkSize - 1) / StationRoomThermalChunkSize;
        internal readonly int FireAlarmChunkCount = (state.FireAlarms.Length + StationFireAlarmChunkSize - 1) / StationFireAlarmChunkSize;
        internal readonly int FireAreaChunkCount = (state.FireAreas.Length + StationFireAreaChunkSize - 1) / StationFireAreaChunkSize;
        internal readonly int RoomSpanChunkCount = Math.Max(1, (state.RoomSpans.Length + StationRoomSpanChunkSize - 1) / StationRoomSpanChunkSize);
        internal readonly int ChunkCount = StationChunkCount(state.Zones.Length, state.RoomSpans.Length, state.BuiltDoors.Length);
        internal readonly List<StationPeer> Peers = new();
    }

    private sealed class StationPeer(Guid session, Peer peer)
    {
        internal readonly Guid Session = session, Token = peer.Token;
        internal readonly Peer Peer = peer;
        internal int Chunk;
    }

    private sealed class StationChunk(byte section, int offset, float batteryKwh, float reserveMoles,
        bool powered, OrbitalStationAtmosphere[] zones, BoxStationDoorState[] doors, BoxStationPowerState[] power,
        BoxStationPlumbingState[] plumbing, BoxStationAirAlarmState[] alarms, BoxStationRadiatorState[] radiators,
        BoxStationRoomThermalState[] roomThermal, BoxStationFireAlarmState[] fireAlarms, BoxStationFireAreaState[] fireAreas,
        long hullRevision, int roomTotal, int spanTotal, BoxStationRoomSpan[] roomSpans, BoxStationSmesState[] smes, byte[] cables,
        long doorRevision, int builtDoorTotal, int[] builtDoors)
    {
        internal readonly byte Section = section;
        internal readonly int Offset = offset;
        internal readonly float BatteryKwh = batteryKwh, ReserveMoles = reserveMoles;
        internal readonly bool Powered = powered;
        internal readonly long HullRevision = hullRevision;
        internal readonly OrbitalStationAtmosphere[] Zones = zones;
        internal readonly BoxStationDoorState[] Doors = doors;
        internal readonly BoxStationPowerState[] Power = power;
        internal readonly BoxStationPlumbingState[] Plumbing = plumbing;
        internal readonly BoxStationAirAlarmState[] Alarms = alarms;
        internal readonly BoxStationRadiatorState[] Radiators = radiators;
        internal readonly BoxStationRoomThermalState[] RoomThermal = roomThermal;
        internal readonly BoxStationFireAlarmState[] FireAlarms = fireAlarms;
        internal readonly BoxStationFireAreaState[] FireAreas = fireAreas;
        internal readonly int RoomTotal = roomTotal, SpanTotal = spanTotal;
        internal readonly BoxStationRoomSpan[] RoomSpans = roomSpans;
        internal readonly BoxStationSmesState[] Smes = smes;
        internal readonly byte[] Cables = cables;
        internal readonly long DoorRevision = doorRevision;
        internal readonly int BuiltDoorTotal = builtDoorTotal;
        internal readonly int[] BuiltDoors = builtDoors;
    }

    private sealed class StationAssembly(StationChunk chunk, long startedAt)
    {
        internal readonly float BatteryKwh = chunk.BatteryKwh, ReserveMoles = chunk.ReserveMoles;
        internal readonly bool Powered = chunk.Powered;
        internal readonly long HullRevision = chunk.HullRevision;
        internal readonly long DoorRevision = chunk.DoorRevision;
        internal readonly long StartedAt = startedAt;
        internal readonly long Timeout = StationAssemblyTimeout(chunk.RoomTotal, chunk.SpanTotal, chunk.BuiltDoorTotal);
        internal readonly OrbitalStationAtmosphere[] Zones = new OrbitalStationAtmosphere[chunk.RoomTotal];
        internal readonly BoxStationDoorState[] Doors = new BoxStationDoorState[BoxStationLayout.Current.Doors.Length + chunk.BuiltDoorTotal];
        internal readonly int[] BuiltDoors = new int[chunk.BuiltDoorTotal];
        internal readonly BoxStationPowerState[] Power = new BoxStationPowerState[BoxStationPower.GetApcs(BoxStationLayout.Current).Length];
        internal readonly BoxStationPlumbingState[] Plumbing = new BoxStationPlumbingState[BoxStationPlumbing.GetDevices(BoxStationLayout.Current).Length];
        internal readonly BoxStationAirAlarmState[] Alarms = new BoxStationAirAlarmState[BoxStationAirAlarms.GetDevices(BoxStationLayout.Current).Length];
        internal readonly BoxStationRadiatorState[] Radiators = new BoxStationRadiatorState[BoxStationPlumbing.GetRadiators(BoxStationLayout.Current).Length];
        internal readonly BoxStationRoomThermalState[] RoomThermal = new BoxStationRoomThermalState[BoxStationRoomThermal.GetDevices(BoxStationLayout.Current).Length];
        internal readonly BoxStationFireAlarmState[] FireAlarms = new BoxStationFireAlarmState[BoxStationFireAlarms.GetDevices(BoxStationLayout.Current).Length];
        internal readonly BoxStationFireAreaState[] FireAreas = new BoxStationFireAreaState[BoxStationLayout.Current.Areas.Length];
        internal readonly BoxStationRoomSpan[] RoomSpans = new BoxStationRoomSpan[chunk.SpanTotal];
        internal readonly BoxStationSmesState[] Smes = new BoxStationSmesState[BoxStationPower.GetSmes(BoxStationLayout.Current).Length];
        internal readonly byte[] Cables = new byte[StationCableBytes];
        internal readonly int ZoneChunkCount = (chunk.RoomTotal + StationZoneChunkSize - 1) / StationZoneChunkSize;
        internal readonly int DoorChunkCount = (BoxStationLayout.Current.Doors.Length + chunk.BuiltDoorTotal + StationDoorChunkSize - 1) / StationDoorChunkSize;
        internal readonly int PowerChunkCount = (BoxStationPower.GetApcs(BoxStationLayout.Current).Length + StationPowerChunkSize - 1) / StationPowerChunkSize;
        internal readonly int PlumbingChunkCount = (BoxStationPlumbing.GetDevices(BoxStationLayout.Current).Length + StationPlumbingChunkSize - 1) / StationPlumbingChunkSize;
        internal readonly int AlarmChunkCount = (BoxStationAirAlarms.GetDevices(BoxStationLayout.Current).Length + StationAlarmChunkSize - 1) / StationAlarmChunkSize;
        internal readonly int RadiatorChunkCount = (BoxStationPlumbing.GetRadiators(BoxStationLayout.Current).Length + StationRadiatorChunkSize - 1) / StationRadiatorChunkSize;
        internal readonly int RoomThermalChunkCount = (BoxStationRoomThermal.GetDevices(BoxStationLayout.Current).Length + StationRoomThermalChunkSize - 1) / StationRoomThermalChunkSize;
        internal readonly int FireAlarmChunkCount = (BoxStationFireAlarms.GetDevices(BoxStationLayout.Current).Length + StationFireAlarmChunkSize - 1) / StationFireAlarmChunkSize;
        internal readonly int FireAreaChunkCount = (BoxStationLayout.Current.Areas.Length + StationFireAreaChunkSize - 1) / StationFireAreaChunkSize;
        internal readonly int RoomSpanChunkCount = Math.Max(1, (chunk.SpanTotal + StationRoomSpanChunkSize - 1) / StationRoomSpanChunkSize);
        internal readonly bool[] Chunks = new bool[StationChunkCount(chunk.RoomTotal, chunk.SpanTotal, chunk.BuiltDoorTotal)];
        internal int ReceivedChunks;
    }

    private sealed partial class SpacecraftEvent
    {
        // The 138-byte envelope leaves 630 bytes; every section includes its door revision and manifest count.
        internal void Station(Peer peer, Guid token, StationSnapshot snapshot, int chunk) =>
            Emit(peer, (Token: token, Snapshot: snapshot, Chunk: chunk), static (writer, state) =>
        {
            var value = state.Snapshot.State;
            byte section = state.Chunk < state.Snapshot.ZoneChunkCount ? (byte)0
                : state.Chunk < state.Snapshot.ZoneChunkCount + state.Snapshot.DoorChunkCount ? (byte)1
                : state.Chunk < state.Snapshot.ZoneChunkCount + state.Snapshot.DoorChunkCount + state.Snapshot.PowerChunkCount ? (byte)2
                : state.Chunk < state.Snapshot.ZoneChunkCount + state.Snapshot.DoorChunkCount + state.Snapshot.PowerChunkCount + state.Snapshot.PlumbingChunkCount ? (byte)3
                : state.Chunk < state.Snapshot.ZoneChunkCount + state.Snapshot.DoorChunkCount + state.Snapshot.PowerChunkCount + state.Snapshot.PlumbingChunkCount + state.Snapshot.AlarmChunkCount ? (byte)4
                : state.Chunk < state.Snapshot.ZoneChunkCount + state.Snapshot.DoorChunkCount + state.Snapshot.PowerChunkCount + state.Snapshot.PlumbingChunkCount + state.Snapshot.AlarmChunkCount + state.Snapshot.RadiatorChunkCount ? (byte)5
                : state.Chunk < state.Snapshot.ZoneChunkCount + state.Snapshot.DoorChunkCount + state.Snapshot.PowerChunkCount + state.Snapshot.PlumbingChunkCount + state.Snapshot.AlarmChunkCount + state.Snapshot.RadiatorChunkCount + state.Snapshot.RoomThermalChunkCount ? (byte)6
                : state.Chunk < state.Snapshot.ZoneChunkCount + state.Snapshot.DoorChunkCount + state.Snapshot.PowerChunkCount + state.Snapshot.PlumbingChunkCount + state.Snapshot.AlarmChunkCount + state.Snapshot.RadiatorChunkCount + state.Snapshot.RoomThermalChunkCount + state.Snapshot.FireAlarmChunkCount ? (byte)7
                : state.Chunk < state.Snapshot.ZoneChunkCount + state.Snapshot.DoorChunkCount + state.Snapshot.PowerChunkCount + state.Snapshot.PlumbingChunkCount + state.Snapshot.AlarmChunkCount + state.Snapshot.RadiatorChunkCount + state.Snapshot.RoomThermalChunkCount + state.Snapshot.FireAlarmChunkCount + state.Snapshot.FireAreaChunkCount ? (byte)8
                : state.Chunk < state.Snapshot.ZoneChunkCount + state.Snapshot.DoorChunkCount + state.Snapshot.PowerChunkCount + state.Snapshot.PlumbingChunkCount + state.Snapshot.AlarmChunkCount + state.Snapshot.RadiatorChunkCount + state.Snapshot.RoomThermalChunkCount + state.Snapshot.FireAlarmChunkCount + state.Snapshot.FireAreaChunkCount + state.Snapshot.RoomSpanChunkCount ? (byte)9
                : state.Chunk < state.Snapshot.ChunkCount - 1 - StationCableChunkCount ? (byte)10
                : state.Chunk < state.Snapshot.ChunkCount - 1 ? (byte)11 : (byte)12;
            int size = section == 0 ? StationZoneChunkSize : section == 1 ? StationDoorChunkSize
                : section == 2 ? StationPowerChunkSize : section == 3 ? StationPlumbingChunkSize
                : section == 4 ? StationAlarmChunkSize : section == 5 ? StationRadiatorChunkSize
                : section == 6 ? StationRoomThermalChunkSize : section == 7 ? StationFireAlarmChunkSize
                : section == 8 ? StationFireAreaChunkSize : section == 9 ? StationRoomSpanChunkSize
                : section == 10 ? StationSmesChunkSize : section == 11 ? StationCableChunkSize : BoxStationDoors.MaximumAssemblies;
            int offset = (section == 0 ? state.Chunk : section == 1 ? state.Chunk - state.Snapshot.ZoneChunkCount
                : section == 2 ? state.Chunk - state.Snapshot.ZoneChunkCount - state.Snapshot.DoorChunkCount
                : section == 3 ? state.Chunk - state.Snapshot.ZoneChunkCount - state.Snapshot.DoorChunkCount - state.Snapshot.PowerChunkCount
                : section == 4 ? state.Chunk - state.Snapshot.ZoneChunkCount - state.Snapshot.DoorChunkCount - state.Snapshot.PowerChunkCount - state.Snapshot.PlumbingChunkCount
                : section == 5 ? state.Chunk - state.Snapshot.ZoneChunkCount - state.Snapshot.DoorChunkCount - state.Snapshot.PowerChunkCount - state.Snapshot.PlumbingChunkCount - state.Snapshot.AlarmChunkCount
                : section == 6 ? state.Chunk - state.Snapshot.ZoneChunkCount - state.Snapshot.DoorChunkCount - state.Snapshot.PowerChunkCount - state.Snapshot.PlumbingChunkCount - state.Snapshot.AlarmChunkCount - state.Snapshot.RadiatorChunkCount
                : section == 7 ? state.Chunk - state.Snapshot.ZoneChunkCount - state.Snapshot.DoorChunkCount - state.Snapshot.PowerChunkCount - state.Snapshot.PlumbingChunkCount - state.Snapshot.AlarmChunkCount - state.Snapshot.RadiatorChunkCount - state.Snapshot.RoomThermalChunkCount
                : section == 8 ? state.Chunk - state.Snapshot.ZoneChunkCount - state.Snapshot.DoorChunkCount - state.Snapshot.PowerChunkCount - state.Snapshot.PlumbingChunkCount - state.Snapshot.AlarmChunkCount - state.Snapshot.RadiatorChunkCount - state.Snapshot.RoomThermalChunkCount - state.Snapshot.FireAlarmChunkCount
                : section == 9 ? state.Chunk - state.Snapshot.ZoneChunkCount - state.Snapshot.DoorChunkCount - state.Snapshot.PowerChunkCount - state.Snapshot.PlumbingChunkCount - state.Snapshot.AlarmChunkCount - state.Snapshot.RadiatorChunkCount - state.Snapshot.RoomThermalChunkCount - state.Snapshot.FireAlarmChunkCount - state.Snapshot.FireAreaChunkCount
                : section == 10 ? state.Chunk - state.Snapshot.ZoneChunkCount - state.Snapshot.DoorChunkCount - state.Snapshot.PowerChunkCount - state.Snapshot.PlumbingChunkCount - state.Snapshot.AlarmChunkCount - state.Snapshot.RadiatorChunkCount - state.Snapshot.RoomThermalChunkCount - state.Snapshot.FireAlarmChunkCount - state.Snapshot.FireAreaChunkCount - state.Snapshot.RoomSpanChunkCount
                : section == 11 ? state.Chunk - state.Snapshot.ChunkCount + 1 + StationCableChunkCount : 0) * size;
            int total = section == 0 ? value.Zones.Length : section == 1 ? value.Doors.Length
                : section == 2 ? value.Power.Length : section == 3 ? value.Plumbing.Length
                : section == 4 ? value.Alarms.Length : section == 5 ? value.Radiators.Length
                : section == 6 ? value.RoomThermal.Length : section == 7 ? value.FireAlarms.Length
                : section == 8 ? value.FireAreas.Length : section == 9 ? value.RoomSpans.Length
                : section == 10 ? value.Smes.Length : section == 11 ? sizeof(long) + value.Cables.Installed.Length : value.BuiltDoors.Length;
            int count = Math.Min(size, total - offset);
            Head(writer, Kind.Station); GuidValue(writer, state.Snapshot.Epoch); GuidValue(writer, state.Token);
            writer.Write(state.Snapshot.Sequence); writer.Write(OrbitalStationOrbit.BodyId);
            writer.Write(StationLayoutFingerprint.Value);
            writer.Write(section); writer.Write(offset); writer.Write((ushort)count);
            writer.Write(value.Zones.Length); writer.Write(value.Doors.Length); writer.Write(value.Power.Length);
            writer.Write(value.Plumbing.Length); writer.Write(value.Alarms.Length);
            writer.Write(value.BatteryKwh); writer.Write(value.ReserveMoles); writer.Write(value.Powered);
            writer.Write(value.HullRevision);
            writer.Write(value.RoomSpans.Length);
            writer.Write(value.DoorRevision); writer.Write(value.BuiltDoors.Length);
            var plumbingDevices = section == 3 ? BoxStationPlumbing.GetDevices(BoxStationLayout.Current) : default;
            for (int index = offset; index < offset + count; index++)
            {
                if (section == 0)
                {
                    var zone = value.Zones[index];
                    writer.Write(zone.Pressure); writer.Write(zone.OxygenFraction);
                    writer.Write(zone.CO2Fraction); writer.Write(zone.TemperatureC);
                    writer.Write(zone.PlasmaFraction); writer.Write(zone.N2OFraction);
                    writer.Write(zone.WaterVaporFraction); writer.Write(zone.TritiumFraction);
                    writer.Write(zone.BZFraction); writer.Write(zone.CombustionWattsPerCubicMeter);
                }
                else if (section == 1)
                {
                    var door = value.Doors[index];
                    writer.Write(door.Amount);
                    writer.Write((byte)((door.TargetOpen ? 1 : 0) | (door.Cycling ? 2 : 0)
                        | (door.Interlocked ? 4 : 0) | (door.Powered ? 8 : 0)));
                }
                else if (section == 2)
                {
                    var power = value.Power[index];
                    writer.Write(power.ChargeKwh); writer.Write(power.Enabled); writer.Write(power.Available);
                }
                else if (section == 3)
                {
                    var plumbing = value.Plumbing[index];
                    writer.Write(plumbing.InputPressure); writer.Write(plumbing.OutputPressure);
                    writer.Write((byte)((plumbing.Enabled ? 1 : 0) | (plumbing.Powered ? 2 : 0) | (plumbing.Connected ? 4 : 0)));
                    if (plumbingDevices[index].Kind == BoxStationPlumbingKind.Filter)
                    {
                        writer.Write(plumbing.FilteredPressure); writer.Write((byte)plumbing.FilterSpecies);
                        writer.Write(plumbing.FilterLitresPerSecond);
                    }
                    else if (plumbingDevices[index].Kind == BoxStationPlumbingKind.Mixer)
                    {
                        writer.Write(plumbing.SecondaryInputPressure); writer.Write(plumbing.MixerFirstBasisPoints);
                        writer.Write(plumbing.MixerTargetPressure);
                    }
                    else if (plumbingDevices[index].Kind == BoxStationPlumbingKind.Vent)
                    {
                        writer.Write(plumbing.VentChecks); writer.Write(plumbing.VentExternalPressure);
                    }
                    else if (plumbingDevices[index].Kind == BoxStationPlumbingKind.Scrubber)
                    {
                        writer.Write((byte)((plumbing.Scrubbing ? 1 : 0) | (plumbing.WideNet ? 2 : 0)));
                        writer.Write((byte)plumbing.ScrubSpecies);
                    }
                    else if (plumbingDevices[index].Kind == BoxStationPlumbingKind.Injector)
                    {
                        writer.Write((ushort)(plumbing.InjectorLitresPerSecond | (plumbing.InjectorPulsing ? 512 : 0)));
                    }
                    else if (BoxStationPlumbing.IsThermal(plumbingDevices[index].Kind))
                    {
                        writer.Write(plumbing.ThermalTargetKelvin); writer.Write(plumbing.ThermalCurrentKelvin);
                        writer.Write(plumbing.ElectricWatts); writer.Write(plumbing.ThermalWatts);
                        writer.Write((byte)((plumbing.ThermalActive ? 1 : 0) | (plumbing.ThermalSinkAvailable ? 2 : 0)
                            | (plumbing.ThermalPowerLimited ? 4 : 0)));
                    }
                }
                else if (section == 4)
                {
                    var alarm = value.Alarms[index];
                    writer.Write((byte)alarm.Mode); writer.Write(alarm.Danger); writer.Write(alarm.AreaDanger);
                    writer.Write(alarm.Powered); writer.Write(alarm.WarningMask); writer.Write(alarm.DangerMask);
                }
                else if (section == 5)
                {
                    var radiator = value.Radiators[index];
                    writer.Write(radiator.SurfaceKelvin); writer.Write(radiator.GasKelvin); writer.Write(radiator.Pressure);
                    writer.Write(radiator.GasWatts); writer.Write(radiator.ExternalWatts); writer.Write(radiator.SolarWatts);
                    writer.Write(radiator.EnvironmentAvailable);
                }
                else if (section == 6)
                {
                    var thermal = value.RoomThermal[index];
                    writer.Write((byte)thermal.Mode); writer.Write(thermal.Enabled); writer.Write(thermal.HatchOpen);
                    writer.Write(thermal.CellDocked); writer.Write(thermal.Charging); writer.Write(thermal.RejectHeat);
                    writer.Write(thermal.TargetKelvin); writer.Write(thermal.CellJoules); writer.Write(thermal.CoreKelvin);
                    writer.Write(thermal.RoomKelvin); writer.Write(thermal.ElectricWatts); writer.Write(thermal.ThermalWatts);
                    writer.Write(thermal.ChargeWatts); writer.Write(thermal.SinkLimited);
                }
                else if (section == 7)
                {
                    var alarm = value.FireAlarms[index];
                    writer.Write((byte)((alarm.Powered ? 1 : 0) | (alarm.Detecting ? 2 : 0)));
                    writer.Write(alarm.ThermalHazard); writer.Write(alarm.CooldownSeconds);
                }
                else if (section == 8)
                {
                    var area = value.FireAreas[index];
                    writer.Write(area.Latched); writer.Write(area.RecloseSeconds);
                }
                else if (section == 9)
                {
                    var span = value.RoomSpans[index];
                    writer.Write(span.Start); writer.Write(span.Length); writer.Write(span.Node);
                }
                else if (section == 10)
                {
                    var smes = value.Smes[index];
                    writer.Write(smes.ChargeKwh); writer.Write(smes.InputLimitKw); writer.Write(smes.OutputLimitKw);
                    writer.Write(smes.InputKw); writer.Write(smes.OutputKw); writer.Write(smes.Enabled);
                }
                else if (section == 11)
                {
                    writer.Write(index < sizeof(long) ? (byte)((ulong)value.Cables.Revision >> (index * 8))
                        : value.Cables.Installed[index - sizeof(long)]);
                }
                else writer.Write(value.BuiltDoors[index]);
            }
        });

        private static StationChunk ReadStation(BinaryReader reader)
        {
            if (reader.ReadInt32() != OrbitalStationOrbit.BodyId) throw new InvalidDataException();
            Span<byte> fingerprint = stackalloc byte[32];
            ReadExactly(reader, fingerprint);
            if (!fingerprint.SequenceEqual(StationLayoutFingerprint.Value)) throw new InvalidDataException();
            byte section = reader.ReadByte();
            int offset = reader.ReadInt32(), count = reader.ReadUInt16();
            int roomTotal = reader.ReadInt32(), doorTotal = reader.ReadInt32(), powerTotal = reader.ReadInt32();
            int plumbingTotal = reader.ReadInt32(), alarmTotal = reader.ReadInt32();
            var layout = BoxStationLayout.Current;
            var apcs = BoxStationPower.GetApcs(layout);
            var plumbingDevices = BoxStationPlumbing.GetDevices(layout);
            if (section > 12 || roomTotal < 0 || roomTotal > layout.Cells.Length
                || powerTotal != apcs.Length || plumbingTotal != plumbingDevices.Length
                || alarmTotal != BoxStationAirAlarms.GetDevices(layout).Length)
                throw new InvalidDataException();
            float battery = Range(reader.ReadSingle(), 0, BoxStationSystems.BatteryCapacityKwh);
            float reserve = Range(reader.ReadSingle(), 0, (float)(20 * BoxStationSystems.MaximumMolesPerCubicMeter));
            bool powered = Boolean(reader);
            long hullRevision = reader.ReadInt64();
            int spanTotal = reader.ReadInt32();
            long doorRevision = reader.ReadInt64();
            int builtDoorTotal = reader.ReadInt32();
            if (hullRevision < 0 || spanTotal < roomTotal || spanTotal > layout.Cells.Length
                || roomTotal == 0 && spanTotal != 0 || doorRevision < 0
                || builtDoorTotal is < 0 or > BoxStationDoors.MaximumAssemblies
                || doorRevision == 0 && builtDoorTotal != 0 || doorTotal != layout.Doors.Length + builtDoorTotal)
                throw new InvalidDataException();
            int size = section == 0 ? StationZoneChunkSize : section == 1 ? StationDoorChunkSize
                : section == 2 ? StationPowerChunkSize : section == 3 ? StationPlumbingChunkSize
                : section == 4 ? StationAlarmChunkSize : section == 5 ? StationRadiatorChunkSize
                : section == 6 ? StationRoomThermalChunkSize : section == 7 ? StationFireAlarmChunkSize
                : section == 8 ? StationFireAreaChunkSize : section == 9 ? StationRoomSpanChunkSize
                : section == 10 ? StationSmesChunkSize : section == 11 ? StationCableChunkSize : BoxStationDoors.MaximumAssemblies;
            // The validated layout fingerprint fixes the remaining section counts without enlarging the envelope.
            int total = section == 0 ? roomTotal : section == 1 ? doorTotal : section == 2 ? powerTotal
                : section == 3 ? plumbingTotal : section == 4 ? alarmTotal
                : section == 5 ? BoxStationPlumbing.GetRadiators(layout).Length
                : section == 6 ? BoxStationRoomThermal.GetDevices(layout).Length
                : section == 7 ? BoxStationFireAlarms.GetDevices(layout).Length
                : section == 8 ? layout.Areas.Length : section == 9 ? spanTotal
                : section == 10 ? BoxStationPower.GetSmes(layout).Length : section == 11 ? StationCableBytes : builtDoorTotal;
            if (offset < 0 || (total == 0 ? section is not (9 or 12) || offset != 0 : offset >= total)
                || offset % size != 0 || count != Math.Min(size, total - offset))
                throw new InvalidDataException();
            var zones = section == 0 ? new OrbitalStationAtmosphere[count] : Array.Empty<OrbitalStationAtmosphere>();
            var doors = section == 1 ? new BoxStationDoorState[count] : Array.Empty<BoxStationDoorState>();
            var power = section == 2 ? new BoxStationPowerState[count] : Array.Empty<BoxStationPowerState>();
            var plumbing = section == 3 ? new BoxStationPlumbingState[count] : Array.Empty<BoxStationPlumbingState>();
            var alarms = section == 4 ? new BoxStationAirAlarmState[count] : Array.Empty<BoxStationAirAlarmState>();
            var radiators = section == 5 ? new BoxStationRadiatorState[count] : Array.Empty<BoxStationRadiatorState>();
            var roomThermal = section == 6 ? new BoxStationRoomThermalState[count] : Array.Empty<BoxStationRoomThermalState>();
            var fireAlarms = section == 7 ? new BoxStationFireAlarmState[count] : Array.Empty<BoxStationFireAlarmState>();
            var fireAreas = section == 8 ? new BoxStationFireAreaState[count] : Array.Empty<BoxStationFireAreaState>();
            var roomSpans = section == 9 ? new BoxStationRoomSpan[count] : Array.Empty<BoxStationRoomSpan>();
            var smes = section == 10 ? new BoxStationSmesState[count] : Array.Empty<BoxStationSmesState>();
            var cables = section == 11 ? new byte[count] : Array.Empty<byte>();
            var builtDoors = section == 12 ? new int[count] : Array.Empty<int>();
            for (int index = 0; index < zones.Length; index++)
            {
                float pressure = reader.ReadSingle(), oxygen = reader.ReadSingle(), co2 = reader.ReadSingle();
                float temperature = reader.ReadSingle(), plasma = reader.ReadSingle(), n2o = reader.ReadSingle();
                float waterVapor = reader.ReadSingle(), tritium = reader.ReadSingle(), bz = reader.ReadSingle();
                float combustion = reader.ReadSingle();
                var atmosphere = new OrbitalStationAtmosphere(pressure, oxygen, co2, temperature,
                    plasma, n2o, waterVapor, tritium, bz, combustion);
                if (!BoxStationSystems.ValidAtmosphere(atmosphere)) throw new InvalidDataException();
                zones[index] = atmosphere;
            }
            int previousEnd = 0, previousNode = -1;
            for (int index = 0; index < roomSpans.Length; index++)
            {
                int start = reader.ReadInt32(), length = reader.ReadInt32(), node = reader.ReadInt32();
                if (start < previousEnd || start >= layout.Cells.Length || length <= 0
                    || length > layout.Cells.Length - start || node < 0 || node >= roomTotal
                    || start == previousEnd && node == previousNode)
                    throw new InvalidDataException();
                roomSpans[index] = new(start, length, node);
                previousEnd = start + length;
                previousNode = node;
            }
            for (int index = 0; index < doors.Length; index++)
            {
                float amount = Range(reader.ReadSingle(), 0, 1);
                byte flags = reader.ReadByte();
                if ((flags & ~15) != 0) throw new InvalidDataException();
                doors[index] = new(amount, (flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0, (flags & 8) != 0);
            }
            for (int index = 0; index < power.Length; index++)
            {
                float charge = Range(reader.ReadSingle(), 0, (float)(apcs[offset + index].CapacityJoules / 3_600_000d));
                byte enabled = reader.ReadByte(), available = reader.ReadByte();
                if (enabled > 7 || (available & ~enabled) != 0) throw new InvalidDataException();
                power[index] = new(charge, enabled, available);
            }
            for (int index = 0; index < plumbing.Length; index++)
            {
                float input = Range(reader.ReadSingle(), 0, (float)BoxStationSystems.MaximumPressure);
                float output = Range(reader.ReadSingle(), 0, (float)BoxStationSystems.MaximumPressure);
                byte flags = reader.ReadByte();
                if ((flags & ~7) != 0) throw new InvalidDataException();
                float filtered = 0, secondary = 0, target = 0;
                StationGasSpecies species = 0;
                ushort rate = 0, ratio = 0;
                byte ventChecks = 0;
                float ventPressure = 0;
                bool scrubbing = false, wideNet = false;
                StationGasSpecies scrubSpecies = 0;
                float thermalTarget = 0, thermalCurrent = 0, thermalMinimum = 0, thermalMaximum = 0,
                    electricWatts = 0, thermalWatts = 0;
                bool thermalActive = false, thermalSink = false, thermalPowerLimited = false;
                ushort injectorRate = 0;
                bool injectorPulsing = false;
                var kind = plumbingDevices[offset + index].Kind;
                if (kind == BoxStationPlumbingKind.Filter)
                {
                    filtered = Range(reader.ReadSingle(), 0, (float)BoxStationSystems.MaximumPressure);
                    species = (StationGasSpecies)reader.ReadByte();
                    rate = reader.ReadUInt16();
                    if (!BoxStationPlumbing.ValidFilterSpecies(species) || rate > BoxStationPlumbing.MaximumFilterLitresPerSecond)
                        throw new InvalidDataException();
                }
                else if (kind == BoxStationPlumbingKind.Mixer)
                {
                    secondary = Range(reader.ReadSingle(), 0, (float)BoxStationSystems.MaximumPressure);
                    ratio = reader.ReadUInt16();
                    target = Range(reader.ReadSingle(), 0, (float)BoxStationPlumbing.MaximumMixerPressure);
                    if (ratio > 10_000) throw new InvalidDataException();
                }
                else if (kind == BoxStationPlumbingKind.Vent)
                {
                    ventChecks = reader.ReadByte();
                    ventPressure = Range(reader.ReadSingle(), 0, (float)BoxStationSystems.MaximumPressure);
                    if (ventChecks > 3) throw new InvalidDataException();
                }
                else if (kind == BoxStationPlumbingKind.Scrubber)
                {
                    byte scrubFlags = reader.ReadByte();
                    scrubSpecies = (StationGasSpecies)reader.ReadByte();
                    if (scrubFlags > 3 || (scrubSpecies & ~StationGasSpecies.All) != 0) throw new InvalidDataException();
                    scrubbing = (scrubFlags & 1) != 0;
                    wideNet = (scrubFlags & 2) != 0;
                }
                else if (kind == BoxStationPlumbingKind.Injector)
                {
                    ushort injector = reader.ReadUInt16();
                    injectorRate = (ushort)(injector & 511);
                    if ((injector & ~1023) != 0 || injectorRate > BoxStationPlumbing.MaximumInjectorLitresPerSecond)
                        throw new InvalidDataException();
                    injectorPulsing = (injector & 512) != 0;
                }
                else if (BoxStationPlumbing.IsThermal(kind))
                {
                    (thermalMinimum, thermalMaximum) = BoxStationPlumbing.ThermalRange(kind);
                    thermalTarget = Range(reader.ReadSingle(), thermalMinimum, thermalMaximum);
                    thermalCurrent = Range(reader.ReadSingle(), 0, (float)StationGas.MaximumTemperature);
                    electricWatts = Range(reader.ReadSingle(), 0, BoxStationPlumbing.ThermalRatedWatts);
                    thermalWatts = Range(reader.ReadSingle(), -BoxStationPlumbing.ThermalRatedWatts * BoxStationPlumbing.MaximumFreezerCop,
                        BoxStationPlumbing.ThermalRatedWatts);
                    byte thermalFlags = reader.ReadByte();
                    if (thermalFlags > 7) throw new InvalidDataException();
                    thermalActive = (thermalFlags & 1) != 0;
                    thermalSink = (thermalFlags & 2) != 0;
                    thermalPowerLimited = (thermalFlags & 4) != 0;
                }
                plumbing[index] = new(input, output, (flags & 1) != 0, (flags & 2) != 0,
                    filtered, species, rate, (flags & 4) != 0, secondary, ratio, target,
                    ventChecks, ventPressure, scrubbing, wideNet, scrubSpecies,
                    thermalTarget, thermalMinimum, thermalMaximum, thermalCurrent,
                    electricWatts, thermalWatts, thermalActive, thermalSink, thermalPowerLimited,
                    injectorRate, injectorPulsing);
            }
            for (int index = 0; index < alarms.Length; index++)
            {
                var mode = (BoxStationAirMode)reader.ReadByte();
                byte danger = reader.ReadByte(), areaDanger = reader.ReadByte();
                bool operational = Boolean(reader);
                ushort warningMask = reader.ReadUInt16(), dangerMask = reader.ReadUInt16();
                if (!BoxStationAirAlarms.ValidMode(mode) || danger > 2 || areaDanger > 2
                    || warningMask > 511 || dangerMask > 511 || (warningMask & dangerMask) != 0)
                    throw new InvalidDataException();
                alarms[index] = new(mode, danger, areaDanger, operational, warningMask, dangerMask);
            }
            for (int index = 0; index < radiators.Length; index++)
            {
                float surface = Range(reader.ReadSingle(), 2.7f, BoxStationPlumbing.MaximumRadiatorKelvin);
                float gas = Range(reader.ReadSingle(), 0, (float)StationGas.MaximumTemperature);
                float pressure = Range(reader.ReadSingle(), 0, (float)BoxStationSystems.MaximumPressure);
                float gasWatts = Range(reader.ReadSingle(), -BoxStationPlumbing.MaximumRadiatorGasWatts,
                    BoxStationPlumbing.MaximumRadiatorGasWatts);
                float externalWatts = Range(reader.ReadSingle(), -1_000_000, 1_000_000);
                float solarWatts = Range(reader.ReadSingle(), 0, 1_000_000);
                radiators[index] = new(surface, gas, pressure, gasWatts, externalWatts, solarWatts, Boolean(reader));
                if (!BoxStationPlumbing.ValidRadiatorState(radiators[index])) throw new InvalidDataException();
            }
            for (int index = 0; index < roomThermal.Length; index++)
            {
                var mode = (BoxStationRoomThermalMode)reader.ReadByte();
                bool enabled = Boolean(reader), hatchOpen = Boolean(reader), cellDocked = Boolean(reader);
                bool charging = Boolean(reader), rejectHeat = Boolean(reader);
                float target = reader.ReadSingle(), cellJoules = reader.ReadSingle(), core = reader.ReadSingle();
                float room = reader.ReadSingle(), electricWatts = reader.ReadSingle(), thermalWatts = reader.ReadSingle();
                float chargeWatts = reader.ReadSingle();
                roomThermal[index] = new(mode, enabled, hatchOpen, cellDocked, charging, rejectHeat,
                    target, cellJoules, core, room, electricWatts, thermalWatts, chargeWatts, Boolean(reader));
                if (!BoxStationRoomThermal.ValidState(roomThermal[index])) throw new InvalidDataException();
            }
            for (int index = 0; index < fireAlarms.Length; index++)
            {
                byte flags = reader.ReadByte(), hazard = reader.ReadByte();
                float cooldown = Range(reader.ReadSingle(), 0, 6.7f);
                if (flags > 3 || hazard > 2) throw new InvalidDataException();
                fireAlarms[index] = new((flags & 1) != 0, (flags & 2) != 0, hazard, cooldown);
            }
            for (int index = 0; index < fireAreas.Length; index++)
            {
                bool latched = Boolean(reader);
                float reclose = Range(reader.ReadSingle(), 0, 10);
                if (!latched && reclose != 0) throw new InvalidDataException();
                fireAreas[index] = new(latched, reclose);
            }
            var smesDevices = section == 10 ? BoxStationPower.GetSmes(layout) : default;
            for (int index = 0; index < smes.Length; index++)
            {
                var device = smesDevices[offset + index];
                float charge = Range(reader.ReadSingle(), 0, (float)(device.CapacityJoules / 3_600_000));
                float inputLimit = Range(reader.ReadSingle(), 0, (float)(device.InputMaximumWatts / 1000));
                float outputLimit = Range(reader.ReadSingle(), 0, (float)(device.OutputMaximumWatts / 1000));
                float input = Range(reader.ReadSingle(), 0, inputLimit);
                float output = Range(reader.ReadSingle(), 0, outputLimit);
                byte enabled = reader.ReadByte();
                // Connection-dependent flow is validated after the matching cable section is assembled.
                if (enabled > 3 || (enabled & 1) == 0 && input != 0 || (enabled & 2) == 0 && output != 0)
                    throw new InvalidDataException();
                smes[index] = new(charge, inputLimit, outputLimit, input, output, enabled);
            }
            if (cables.Length != 0) ReadExactly(reader, cables);
            int previousEdge = -1;
            for (int index = 0; index < builtDoors.Length; index++)
            {
                int edge = reader.ReadInt32();
                if (edge <= previousEdge || !BoxStationDoors.ValidEdge(layout, edge)) throw new InvalidDataException();
                builtDoors[index] = edge;
                previousEdge = edge;
            }
            return new(section, offset, battery, reserve, powered, zones, doors, power, plumbing, alarms, radiators,
                roomThermal, fireAlarms, fireAreas, hullRevision, roomTotal, spanTotal, roomSpans, smes, cables,
                doorRevision, builtDoorTotal, builtDoors);
        }
    }
}
