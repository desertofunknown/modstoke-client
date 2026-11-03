// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Bolt;
using Endnight.Extensions;
using Modstoke.Sons;
using SonsSdk.Networking;
using UdpKit;

namespace Modstoke.Client;

internal sealed partial class ClientTabletPresence
{
    private long _nextHelloDiagnostic;

    private void Receive(Message message, BoltConnection connection)
    {
        if (_mode == 1)
        {
            if (connection == null || connection.WasCollected) return;
            var peer = _peers.Values.FirstOrDefault(value => value.Pointer == connection.Pointer
                && value.Connection == connection.ConnectionId && Current(value));
            if (message.Kind == Kind.Hello)
            {
                if (message.Nonce == Guid.Empty) return;
                if (peer == null)
                {
                    if (_peers.Count >= MaxPeers)
                    { ReportHelloUnavailable("The device presence peer limit is reached."); return; }
                    var session = ServerSessions.Sessions.FirstOrDefault(value => value.IsCurrent
                        && value.ConnectionId == connection.ConnectionId);
                    if (session == null)
                    { ReportHelloUnavailable("No current player session matches the native connection."); return; }
                    if (!ServerSessions.TryReadIdentity(session, out var identity, out var identityStatus))
                    { ReportHelloUnavailable("Player identity is unavailable: " + identityStatus + "."); return; }
                    peer = new Peer { Session = session, SteamId = identity.GameSteamId,
                        Pointer = connection.Pointer, Connection = connection.ConnectionId };
                    if (!Current(peer))
                    { ReportHelloUnavailable("The player identity or native connection changed."); return; }
                    var bindingStatus = RefreshBinding(peer);
                    if (peer.Binding == null)
                    { ReportHelloUnavailable("Player binding is unavailable: " + bindingStatus + "."); return; }
                    _peers[session.SessionId] = peer;
                    UnityEngine.Debug.Log("Modstoke device presence admitted a new peer for connection " + connection.ConnectionId + ".");
                }
                if (!peer.Packets.Take(Now, 12, 12)) return;
                if (peer.Nonce != message.Nonce)
                {
                    peer.Nonce = message.Nonce; peer.Token = Guid.NewGuid(); peer.Sequence = 0;
                    peer.HideDevices();
                }
                peer.Seen = Now;
                _event?.Welcome(peer);
                return;
            }
            if (peer == null || message.Kind != Kind.State || message.Epoch != _epoch || message.Token != peer.Token
                || message.Sequence <= peer.Sequence || !peer.Packets.Take(Now, 12, 12)) return;
            RefreshBinding(peer);
            if (peer.Binding == null) return;
            peer.Sequence = message.Sequence; peer.Seen = peer.StateAt = Now;
            peer.Phase = message.Phase; peer.Touch = message.Touch;
            peer.WatchPhase = message.WatchPhase; peer.WatchTouch = message.WatchTouch;
            peer.Controller = message.Controller;
            return;
        }
        if (_mode != 2 || connection == null || connection.WasCollected
            || connection.Pointer != _serverPointer || connection.ConnectionId != _serverConnection) return;
        var server = BoltNetwork.server;
        if (server == null || server.WasCollected || server.Pointer != connection.Pointer
            || server.ConnectionId != connection.ConnectionId) return;
        if (message.Kind == Kind.Welcome)
        {
            if (message.Nonce != _nonce || message.Token == Guid.Empty || message.Epoch == Guid.Empty) return;
            if (_token != Guid.Empty && (_epoch != message.Epoch || _token != message.Token)) return;
            _epoch = message.Epoch; _token = message.Token; _lastSnapshot = Now;
            return;
        }
        if (message.Kind != Kind.Snapshot || _token == Guid.Empty || message.Epoch != _epoch
            || message.Token != _token || message.Sequence <= _receivedSequence) return;
        _receivedSequence = message.Sequence; _lastSnapshot = Now;
        Apply(message.Rows, _lastSnapshot);
    }

    private void ReportHelloUnavailable(string reason)
    {
        var now = Now;
        if (now < _nextHelloDiagnostic) return;
        _nextHelloDiagnostic = now + 30000;
        UnityEngine.Debug.LogWarning("Modstoke device presence Hello could not be admitted: " + reason);
    }

    private static void WriteGuid(BinaryWriter writer, Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        value.TryWriteBytes(bytes);
        writer.Write(bytes);
    }

    private static Guid ReadGuid(BinaryReader reader)
    {
        Span<byte> bytes = stackalloc byte[16];
        var read = 0;
        while (read < bytes.Length)
        {
            var count = reader.Read(bytes.Slice(read));
            if (count == 0) throw new EndOfStreamException();
            read += count;
        }
        return new Guid(bytes);
    }

    private enum Kind : byte { Hello = 1, Welcome, State, Snapshot }

    private sealed class Message
    {
        internal Kind Kind;
        internal Guid Nonce, Epoch, Token;
        internal long Sequence;
        internal ClientTabletPresencePhase Phase;
        internal ClientWatchPresencePhase WatchPhase;
        internal bool Touch, WatchTouch, Controller;
        internal StateRow[] Rows = Array.Empty<StateRow>();
    }

    private sealed class PresenceEvent(ClientTabletPresence service) : Packets.NetEvent
    {
        private const int Maximum = 640;
        public override string Id => "modstoke.client.device-presence.v3";
        public override bool AllowClientRelay => false;

        public override void Read(UdpPacket packet, BoltConnection fromConnection)
        {
            if (!service.OnThread || !service._started || service._mode == 0 || service._loading) return;
            try
            {
                if (!BoltNetwork.isRunning || packet == null || packet.WasCollected
                    || BoltNetwork.isServer && !service._ingress.Take(Now, MaxPeers * 12, MaxPeers * 12)) return;
                // UdpPacket.Size is in bits; envelope and payload lengths are bytes.
                var header = BoltNetwork.isServer ? 7 : 11;
                if (packet.Size < (header + 4) * 8 || packet.Size > (header + 2 + Maximum) * 8) return;
                var length = packet.ReadUShort();
                if (length < 2 || length > Maximum || packet.Size != (header + 2 + length) * 8) return;
                var data = new byte[length];
                for (var i = 0; i < length; i++) data[i] = packet.ReadByte();
                using var stream = new MemoryStream(data, false);
                using var reader = new BinaryReader(stream);
                Message? message;
                try { message = Parse(reader); }
                catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException or OverflowException)
                { return; } // Invalid wire data is rejected without disabling the service.
                if (message == null || stream.Position != stream.Length) return;
                if (BoltNetwork.isServer ? message.Kind is not (Kind.Hello or Kind.State)
                    : message.Kind is not (Kind.Welcome or Kind.Snapshot)) return;
                service.Receive(message, fromConnection);
            }
            catch (Exception error) { service.Fail(error); }
        }

        internal void Hello() => Emit(null, service, static (writer, owner) =>
        { Head(writer, Kind.Hello); WriteGuid(writer, owner._nonce); });
        internal void Welcome(Peer peer) => Emit(peer, (Event: this, Peer: peer), static (writer, state) =>
        { Head(writer, Kind.Welcome); WriteGuid(writer, state.Peer.Nonce); state.Event.Envelope(writer, state.Peer); });
        internal void State(long sequence, ClientTabletPresencePhase phase, bool touch,
            ClientWatchPresencePhase watchPhase, bool watchTouch, bool controller) =>
            Emit(null, (Event: this, Sequence: sequence, Phase: phase, Touch: touch,
                WatchPhase: watchPhase, WatchTouch: watchTouch, Controller: controller), static (writer, state) =>
        {
            var (sender, sequence, phase, touch, watchPhase, watchTouch, controller) = state;
            Head(writer, Kind.State); sender.Envelope(writer, null); writer.Write(sequence);
            writer.Write((byte)phase); writer.Write(touch); writer.Write((byte)watchPhase); writer.Write(watchTouch);
            writer.Write(controller);
        });
        internal void Snapshot(Peer peer, long sequence, StateRow[] rows, int count) =>
            Emit(peer, (Event: this, Peer: peer, Sequence: sequence, Rows: rows, Count: count), static (writer, state) =>
        {
            var (sender, peer, sequence, rows, count) = state;
            Head(writer, Kind.Snapshot); sender.Envelope(writer, peer); writer.Write(sequence); writer.Write((byte)count);
            for (var i = 0; i < count; i++)
            {
                var row = rows[i];
                writer.Write(row.Actor); WriteGuid(writer, row.Generation); writer.Write((byte)row.Phase); writer.Write(row.Touch);
                writer.Write((byte)row.WatchPhase); writer.Write(row.WatchTouch);
                writer.Write(row.Controller);
            }
        });

        private void Emit<TState>(Peer? peer, TState state, Action<BinaryWriter, TState> write)
        {
            if (!service._started || !BoltNetwork.isRunning || peer != null && !service.Current(peer)) return;
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true)) write(writer, state);
            if (stream.Length > Maximum) return;
            var length = (int)stream.Length;
            var bytes = stream.GetBuffer();
            var packet = peer == null ? NewPacket(length + 2, GlobalTargets.OnlyServer)
                : NewPacket(length + 2, peer.Connection);
            packet.Packet.WriteUShort((ushort)length);
            for (var i = 0; i < length; i++) packet.Packet.WriteByte(bytes[i]);
            Send(packet);
        }

        private static void Head(BinaryWriter writer, Kind kind) { writer.Write((byte)3); writer.Write((byte)kind); }
        private void Envelope(BinaryWriter writer, Peer? peer)
        { WriteGuid(writer, service._epoch); WriteGuid(writer, peer?.Token ?? service._token); }
        private static bool Boolean(BinaryReader reader) => reader.ReadByte() switch
        { 0 => false, 1 => true, _ => throw new InvalidDataException() };

        private static Message? Parse(BinaryReader reader)
        {
            if (reader.ReadByte() != 3) return null;
            var message = new Message { Kind = (Kind)reader.ReadByte() };
            if (message.Kind == Kind.Hello) { message.Nonce = ReadGuid(reader); return message; }
            if (message.Kind == Kind.Welcome) message.Nonce = ReadGuid(reader);
            message.Epoch = ReadGuid(reader); message.Token = ReadGuid(reader);
            if (message.Kind == Kind.Welcome) return message;
            message.Sequence = reader.ReadInt64();
            if (message.Sequence <= 0) return null;
            if (message.Kind == Kind.State)
            {
                message.Phase = (ClientTabletPresencePhase)reader.ReadByte(); message.Touch = Boolean(reader);
                message.WatchPhase = (ClientWatchPresencePhase)reader.ReadByte(); message.WatchTouch = Boolean(reader);
                message.Controller = Boolean(reader);
                if (!ValidState(message.Phase, message.Touch, message.WatchPhase, message.WatchTouch, message.Controller)) return null;
            }
            else if (message.Kind == Kind.Snapshot)
            {
                var count = reader.ReadByte();
                if (count > MaxPeers + 1) return null;
                message.Rows = new StateRow[count];
                var actors = new HashSet<ulong>();
                for (var i = 0; i < count; i++)
                {
                    var actor = reader.ReadUInt64(); var generation = ReadGuid(reader);
                    var phase = (ClientTabletPresencePhase)reader.ReadByte(); var touch = Boolean(reader);
                    var watchPhase = (ClientWatchPresencePhase)reader.ReadByte(); var watchTouch = Boolean(reader);
                    var controller = Boolean(reader);
                    if (actor == 0 || generation == Guid.Empty || !actors.Add(actor)
                        || phase == ClientTabletPresencePhase.Hidden && watchPhase == ClientWatchPresencePhase.Hidden
                        || !ValidState(phase, touch, watchPhase, watchTouch, controller)) return null;
                    message.Rows[i] = new(actor, generation, phase, touch, watchPhase, watchTouch, controller);
                }
            }
            else return null;
            return message;
        }

        private static bool ValidState(ClientTabletPresencePhase phase, bool touch, ClientWatchPresencePhase watchPhase,
            bool watchTouch, bool controller)
            => phase <= ClientTabletPresencePhase.Holstering && watchPhase <= ClientWatchPresencePhase.Inspecting
                && (!touch || phase == ClientTabletPresencePhase.Open)
                && (!watchTouch || watchPhase == ClientWatchPresencePhase.Inspecting)
                && (!controller || phase != ClientTabletPresencePhase.Hidden)
                && (phase == ClientTabletPresencePhase.Hidden || watchPhase != ClientWatchPresencePhase.Inspecting);
    }
}
