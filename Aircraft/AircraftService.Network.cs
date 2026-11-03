// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.IO;
using System.Linq;
using System.Text;
using GlobalTargets = Bolt.GlobalTargets;
using Endnight.Extensions;
using Modstoke.Sons;
using SonsSdk.Networking;
using UdpKit;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientAircraftService
{
    private long _nextHelloDiagnostic;

    private Peer? ResolvePeer(BoltConnection connection)
    {
        foreach (var peer in _peers.Values)
            if (peer.Pointer == connection.Pointer && peer.Connection == connection.ConnectionId && PeerCurrent(peer))
                return peer;
        return null;
    }

    private void Hello(BoltConnection connection, Guid nonce)
    {
        if (nonce == Guid.Empty || !BoltNetwork.isServer) return;
        if (!_ready)
        { ReportHelloUnavailable("Host world is not ready: " + _status); return; }
        var peer = ResolvePeer(connection);
        if (peer != null)
        {
            if (!peer.Commands.Take(Now, 4, 4)) return;
            peer.LastSeen = Now;
            if (peer.Nonce != nonce)
            {
                ReleaseSeat(peer, null);
                peer.Nonce = nonce; peer.Token = Guid.NewGuid();
                peer.LastCommand = peer.LastControlSequence = 0;
                peer.Result = "Connected to the shared hangar.";
            }
            _event?.Welcome(peer);
            return;
        }
        if (_peers.Count >= 16) { ReportHelloUnavailable("The aircraft peer limit is reached."); return; }
        if (!ClientConnections.Contains(connection.Pointer, connection.ConnectionId))
        { ReportHelloUnavailable("The native connection is no longer current."); return; }
        var sessions = ServerSessions.Sessions;
        for (var i = 0; i < sessions.Count; i++)
        {
            var session = sessions[i];
            if (!session.IsCurrent || session.ConnectionId != connection.ConnectionId) continue;
            // Listen hosts use the transport identity; dedicated hosts use registration.
            if (!ServerSessions.TryReadIdentity(session, out var identity, out var identityStatus))
            { ReportHelloUnavailable("Player identity is unavailable: " + identityStatus + "."); return; }
            foreach (var value in _peers.Values)
                if (value.Owner == identity.GameSteamId && PeerCurrent(value))
                { ReportHelloUnavailable("The player already has a current aircraft peer."); return; }
            if (!ServerWorld.TryCapturePlayerBinding(session, out var binding, out var bindingStatus))
            { ReportHelloUnavailable("Player binding is unavailable: " + bindingStatus + "."); return; }
            peer = new Peer { Session = session, Binding = binding, Owner = identity.GameSteamId,
                Connection = connection.ConnectionId, Pointer = connection.Pointer, Nonce = nonce, LastSeen = Now };
            _peers.Add(session.SessionId, peer);
            _event?.Welcome(peer);
            _event?.Snapshot(peer, ++_snapshotSequence);
            UnityEngine.Debug.Log("Modstoke aircraft admitted a new peer for connection " + connection.ConnectionId + ".");
            return;
        }
        ReportHelloUnavailable("No current player session matches the native connection.");
    }

    private void ReportHelloUnavailable(string reason)
    {
        var now = Now;
        if (now < _nextHelloDiagnostic) return;
        _nextHelloDiagnostic = now + 30000;
        UnityEngine.Debug.LogWarning("Modstoke aircraft Hello could not be admitted: " + reason);
    }

    private void Receive(Message message, BoltConnection connection)
    {
        if (connection == null || connection.WasCollected) return;
        if (BoltNetwork.isServer)
        {
            if (message.Kind == Kind.Hello) { Hello(connection, message.Nonce); return; }
            if (!_ready) return;
            var peer = ResolvePeer(connection);
            if (peer == null || message.Epoch != _epoch || message.Token != peer.Token
                || !peer.Packets.Take(Now, 45, 12)) return;
            peer.LastSeen = Now;
            if (message.Kind == Kind.Command)
            {
                ApplyCommand(peer, message.Sequence, message.Action);
                _event?.Snapshot(peer, ++_snapshotSequence);
            }
            else if (message.Kind == Kind.SeatStatus)
                ReceiveSeatStatus(peer, message.Sequence, message.Lease, message.Attached);
            else if (message.Kind == Kind.Control && peer.Seat == 0 && peer.Phase == SeatPhase.Seated
                && ReferenceEquals(_seats[0], peer)
                && peer.Lease != Guid.Empty && message.Lease == peer.Lease
                && message.Sequence > peer.LastControlSequence && peer.Controls.Take(Now, 35, 8)
                && TryActor(peer, out _, out _, out _))
            {
                peer.Input = message.Controls; peer.LastControl = Now; peer.LastControlSequence = message.Sequence;
            }
            return;
        }
        if (!IsClient || connection.Pointer != _serverPointer || connection.ConnectionId != _serverConnection) return;
        var server = BoltNetwork.server;
        if (server == null || server.WasCollected || server.Pointer != connection.Pointer
            || server.ConnectionId != connection.ConnectionId) return;
        if (message.Kind == Kind.Welcome)
        {
            if (message.Nonce != _nonce || message.Epoch == Guid.Empty || message.Token == Guid.Empty) return;
            if (_ready && (message.Epoch != _epoch || message.Token != _token)
                && Now - Math.Max(_lastSnapshot, _lastWelcome) < 3000) return;
            if (message.Epoch != _epoch || message.Token != _token)
            {
                RemoveBody();
                _planeId = Guid.Empty; _clientSeat = -1; _clientLease = _ridingLease = Guid.Empty;
                _receivedSequence = 0; _pending = null;
            }
            _epoch = message.Epoch; _token = message.Token; _lastWelcome = Now;
            _ready = true; _status = "Connected to the shared hangar.";
            return;
        }
        if (!_ready || message.Epoch != _epoch || message.Token != _token
            || message.Kind != Kind.Snapshot || message.Sequence <= _receivedSequence) return;
        _receivedSequence = message.Sequence; _lastSnapshot = Now;
        if (_pending != null && message.Acknowledged >= _pending.Sequence)
        { _pending = null; _feedback = message.Result; }
        else if (_pending == null && message.Acknowledged == _commandSequence)
            _feedback = message.Result;
        if (_planeId != message.Plane || message.Plane == Guid.Empty)
        {
            _ridingLease = Guid.Empty;
            RemoveBody(true); _planeId = message.Plane;
        }
        _clientSeat = message.Seat; _clientLease = message.Lease; _clientExit = message.Exit;
        _clientPhase = message.Phase; _clientDoor = message.Door;
        Array.Copy(message.Actors, _actorIds, 3);
        if (_planeId != Guid.Empty)
        {
            _body ??= new ClientAircraftBody(_planeId.ToString("N"), message.Flight.Position, message.Flight.Rotation, false);
            _body.SetReplica(message.Flight);
            ApplyDoorMask(message.Doors);
        }
    }

    private enum Kind : byte { Hello = 1, Welcome, Command, Control, Snapshot, SeatStatus }
    private sealed class Message
    {
        internal Kind Kind;
        internal Guid Nonce, Epoch, Token, Plane, Lease;
        internal long Sequence, Acknowledged;
        internal ActionCode Action;
        internal AircraftControls Controls;
        internal AircraftFlightState Flight;
        internal SeatPhase Phase;
        internal byte Door, Doors;
        internal bool Attached;
        internal int Seat = -1;
        internal ulong[] Actors = new ulong[3];
        internal Vector3? Exit;
        internal string Result = "";
    }

    private sealed class AircraftEvent(ClientAircraftService service) : Packets.NetEvent
    {
        private const int Limit = 800;
        private static readonly UTF8Encoding Utf8 = new(false, true);
        public override string Id => "modstoke.client.aircraft.v3";
        public override bool AllowClientRelay => false;

        public override void Read(UdpPacket packet, BoltConnection fromConnection)
        {
            if (!service.OnThread || !service._started || !BoltNetwork.isRunning
                || packet == null || packet.WasCollected
                || BoltNetwork.isServer && !service._receiveRate.Take(Now, 720, 192)) return;
            try
            {
                var envelope = BoltNetwork.isServer ? 7 : 11;
                if (packet.Size < (envelope + 4) * 8 || packet.Size > (envelope + Limit + 2) * 8) return;
                var size = packet.ReadUShort();
                if (size < 2 || size > Limit || packet.Size != (envelope + 2 + size) * 8) return;
                var bytes = new byte[size];
                for (var i = 0; i < size; i++) bytes[i] = packet.ReadByte();
                using var stream = new MemoryStream(bytes, false);
                using var reader = new BinaryReader(stream, Utf8);
                var message = Parse(reader);
                if (message == null || stream.Position != stream.Length) return;
                if (BoltNetwork.isServer ? message.Kind is not (Kind.Hello or Kind.Command or Kind.Control or Kind.SeatStatus)
                    : message.Kind is not (Kind.Welcome or Kind.Snapshot)) return;
                try { service.Receive(message, fromConnection); }
                catch (Exception error) { service.Fail(error); }
            }
            catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException or OverflowException)
            { /* Malformed or unsupported packets never reach the simulation. */ }
        }

        internal void Hello() => Emit(null, service, static (writer, state) =>
        { Head(writer, Kind.Hello); GuidValue(writer, state._nonce); });
        internal void Welcome(Peer peer) =>
            Emit(peer, (Event: this, Peer: peer), static (writer, state) =>
        { Head(writer, Kind.Welcome); GuidValue(writer, state.Peer.Nonce); state.Event.Envelope(writer, state.Peer); });
        internal void Request(ActionCode action, long sequence) =>
            Emit(null, (Event: this, Action: action, Sequence: sequence), static (writer, state) =>
        { Head(writer, Kind.Command); state.Event.Envelope(writer, null); writer.Write(state.Sequence); writer.Write((byte)state.Action); });
        internal void Control(long sequence, Guid lease, AircraftControls input) =>
            Emit(null, (Event: this, Sequence: sequence, Lease: lease, Input: input), static (writer, state) =>
        {
            var (sender, sequence, lease, input) = state;
            Head(writer, Kind.Control); sender.Envelope(writer, null); writer.Write(sequence); GuidValue(writer, lease);
            writer.Write(input.Pitch); writer.Write(input.Roll); writer.Write(input.Yaw); writer.Write(input.Throttle);
            writer.Write(input.Brake); writer.Write(input.Flaps); writer.Write(input.Engine); writer.Write(input.Lights);
            writer.Write((byte)input.Interaction); writer.Write(input.InteractionSequence);
        });
        internal void SeatStatus(long sequence, Guid lease, bool attached) =>
            Emit(null, (Event: this, Sequence: sequence, Lease: lease, Attached: attached), static (writer, state) =>
        {
            var (sender, sequence, lease, attached) = state;
            Head(writer, Kind.SeatStatus); sender.Envelope(writer, null); writer.Write(sequence);
            GuidValue(writer, lease); writer.Write(attached);
        });
        internal void Snapshot(Peer peer, long sequence) =>
            Emit(peer, (Event: this, Service: service, Peer: peer, Sequence: sequence), static (writer, state) =>
        {
            var (sender, owner, peer, sequence) = state;
            Head(writer, Kind.Snapshot); sender.Envelope(writer, peer); writer.Write(sequence);
            writer.Write(peer.LastCommand); Text(writer, peer.Result);
            GuidValue(writer, owner._body == null ? Guid.Empty : owner._planeId);
            writer.Write((sbyte)peer.Seat); GuidValue(writer, peer.Lease);
            writer.Write((byte)peer.Phase); writer.Write(peer.Door); writer.Write(owner._doorMask);
            for (var i = 0; i < 3; i++) writer.Write(owner._actorIds[i]);
            writer.Write(peer.Exit.HasValue); if (peer.Exit.HasValue) Vector(writer, peer.Exit.Value);
            if (owner._body == null) return;
            var value = owner._body.State;
            Vector(writer, value.Position); Rotation(writer, value.Rotation); Vector(writer, value.Velocity);
            Vector(writer, value.AngularVelocity);
            writer.Write(value.Throttle); writer.Write(value.Rpm); writer.Write(value.Fuel); writer.Write(value.Health);
            writer.Write(value.Engine); writer.Write(value.Grounded); writer.Write(value.Stall);
            writer.Write(value.Flaps); writer.Write(value.Lights);
            writer.Write(value.PitchControl); writer.Write(value.RollControl); writer.Write(value.YawControl);
            Vector(writer, value.WindVelocity);
            writer.Write(value.Brake); writer.Write((byte)value.Interaction);
            writer.Write(value.InteractionSequence); writer.Write(value.InteractionAge);
        });

        private void Emit<TState>(Peer? peer, TState state, Action<BinaryWriter, TState> write)
        {
            if (!service._started || !BoltNetwork.isRunning || peer != null && !service.PeerCurrent(peer)) return;
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Utf8, true)) write(writer, state);
            if (stream.Length > Limit) throw new InvalidOperationException("Aircraft packet exceeded its transport budget.");
            var length = checked((int)stream.Length);
            var bytes = stream.GetBuffer();
            var packet = peer == null ? NewPacket(length + 2, GlobalTargets.OnlyServer)
                : NewPacket(length + 2, peer.Connection);
            packet.Packet.WriteUShort((ushort)length);
            for (var index = 0; index < length; index++) packet.Packet.WriteByte(bytes[index]);
            Send(packet);
        }
        private void Envelope(BinaryWriter writer, Peer? peer)
        { GuidValue(writer, service._epoch); GuidValue(writer, peer?.Token ?? service._token); }
        private static void Head(BinaryWriter writer, Kind kind) { writer.Write((byte)3); writer.Write((byte)kind); }
        private static void GuidValue(BinaryWriter writer, Guid value)
        {
            Span<byte> bytes = stackalloc byte[16];
            value.TryWriteBytes(bytes);
            writer.Write(bytes);
        }
        private static Guid GuidValue(BinaryReader reader)
        {
            Span<byte> bytes = stackalloc byte[16];
            ReadExactly(reader, bytes);
            return new Guid(bytes);
        }
        private static void Text(BinaryWriter writer, string value)
        {
            var length = Utf8.GetByteCount(value);
            if (length > 192) throw new InvalidDataException();
            Span<byte> bytes = stackalloc byte[length];
            Utf8.GetBytes(value, bytes);
            writer.Write((byte)length); writer.Write(bytes);
        }
        private static string Text(BinaryReader reader)
        {
            var size = reader.ReadByte(); if (size > 192) throw new InvalidDataException();
            Span<byte> bytes = stackalloc byte[size];
            ReadExactly(reader, bytes);
            return Utf8.GetString(bytes);
        }
        private static void ReadExactly(BinaryReader reader, Span<byte> destination)
        {
            var read = 0;
            while (read < destination.Length)
            {
                var count = reader.Read(destination[read..]);
                if (count <= 0) throw new EndOfStreamException();
                read += count;
            }
        }
        private static void Vector(BinaryWriter writer, Vector3 value)
        { writer.Write(value.x); writer.Write(value.y); writer.Write(value.z); }
        private static Vector3 Vector(BinaryReader reader) => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        private static void Rotation(BinaryWriter writer, Quaternion value)
        { writer.Write(value.x); writer.Write(value.y); writer.Write(value.z); writer.Write(value.w); }
        private static Quaternion Rotation(BinaryReader reader) => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        private static bool Boolean(BinaryReader reader) => reader.ReadByte() switch
        { 0 => false, 1 => true, _ => throw new InvalidDataException() };
        private static bool Axis(float value) => float.IsFinite(value) && value >= -1 && value <= 1;

        private static Message? Parse(BinaryReader reader)
        {
            if (reader.ReadByte() != 3) return null;
            var value = new Message { Kind = (Kind)reader.ReadByte() };
            if (value.Kind == Kind.Hello) { value.Nonce = GuidValue(reader); return value; }
            if (value.Kind == Kind.Welcome) value.Nonce = GuidValue(reader);
            value.Epoch = GuidValue(reader); value.Token = GuidValue(reader);
            if (value.Kind == Kind.Welcome) return value;
            value.Sequence = reader.ReadInt64(); if (value.Sequence <= 0) return null;
            switch (value.Kind)
            {
                case Kind.Command:
                    value.Action = (ActionCode)reader.ReadByte();
                    return value.Action <= ActionCode.CloseStarboardDoor ? value : null;
                case Kind.Control:
                    value.Lease = GuidValue(reader);
                    var pitch = reader.ReadSingle(); var roll = reader.ReadSingle();
                    var yaw = reader.ReadSingle(); var throttle = reader.ReadSingle();
                    var brake = Boolean(reader); var flaps = reader.ReadByte();
                    var engine = Boolean(reader); var lights = Boolean(reader);
                    var interaction = (AircraftInteraction)reader.ReadByte();
                    var interactionSequence = reader.ReadUInt32();
                    if (!Axis(pitch) || !Axis(roll) || !Axis(yaw) || !Unit(throttle) || flaps > 2
                        || interaction > AircraftInteraction.Throttle) return null;
                    value.Controls = new(pitch, roll, yaw, throttle, brake, flaps, engine, lights)
                    { Interaction = interaction, InteractionSequence = interactionSequence };
                    return value;
                case Kind.SeatStatus:
                    value.Lease = GuidValue(reader);
                    value.Attached = Boolean(reader);
                    return value.Lease != Guid.Empty ? value : null;
                case Kind.Snapshot:
                    value.Acknowledged = reader.ReadInt64(); if (value.Acknowledged < 0) return null;
                    value.Result = Text(reader); value.Plane = GuidValue(reader);
                    value.Seat = reader.ReadSByte(); value.Lease = GuidValue(reader);
                    if (value.Seat < -1 || value.Seat > 2 || (value.Seat == -1) != (value.Lease == Guid.Empty)) return null;
                    value.Phase = (SeatPhase)reader.ReadByte(); value.Door = reader.ReadByte(); value.Doors = reader.ReadByte();
                    if (value.Phase > SeatPhase.Leaving || (value.Seat == -1) != (value.Phase == SeatPhase.None)
                        || value.Doors > (PortDoor | StarboardDoor)
                        || (value.Seat == -1 ? value.Door != 0 : value.Door is not (PortDoor or StarboardDoor))) return null;
                    for (var i = 0; i < 3; i++) value.Actors[i] = reader.ReadUInt64();
                    if (Boolean(reader))
                    { var exit = Vector(reader); if (!Finite(exit)) return null; value.Exit = exit; }
                    if (value.Phase == SeatPhase.Leaving && !value.Exit.HasValue
                        || value.Seat >= 0 && value.Phase != SeatPhase.Leaving && value.Exit.HasValue) return null;
                    if (value.Plane == Guid.Empty)
                        return value.Seat == -1 && value.Doors == 0 && value.Actors.All(actor => actor == 0) ? value : null;
                    var position = Vector(reader); var rotation = Rotation(reader);
                    var velocity = Vector(reader); var angular = Vector(reader);
                    var t = reader.ReadSingle(); var rpm = reader.ReadSingle();
                    var fuel = reader.ReadSingle(); var health = reader.ReadSingle();
                    var on = Boolean(reader); var grounded = Boolean(reader); var stall = Boolean(reader);
                    var flap = reader.ReadByte(); var light = Boolean(reader);
                    var p = reader.ReadSingle(); var r = reader.ReadSingle(); var y = reader.ReadSingle();
                    var wind = Vector(reader);
                    var braking = Boolean(reader); var gesture = (AircraftInteraction)reader.ReadByte();
                    var gestureSequence = reader.ReadUInt32(); var gestureAge = reader.ReadSingle();
                    if (!Finite(position) || !ValidRotation(rotation) || !Finite(velocity) || velocity.sqrMagnitude > 1e6f
                        || !Finite(angular) || angular.sqrMagnitude > 400 || !Unit(t) || !Unit(fuel) || !Unit(health)
                        || !float.IsFinite(rpm) || rpm < 0 || rpm > 10000 || flap > 2 || !Axis(p) || !Axis(r) || !Axis(y)
                        || !Finite(wind) || wind.sqrMagnitude > 2500 || gesture > AircraftInteraction.Throttle
                        || !float.IsFinite(gestureAge) || gestureAge < 0 || gestureAge > 2) return null;
                    value.Flight = new(position, rotation, velocity, angular, t, rpm, fuel, health, on,
                        grounded, stall, flap, light, p, r, y, wind, braking, gesture, gestureSequence, gestureAge);
                    return value;
                default: return null;
            }
        }
    }
}
