// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Bolt;
using SonsSdk.Networking;
using UdpKit;

namespace Modstoke.Client;

internal sealed record ClientRemoteAppearance(Guid Generation, byte[] Data, long Seen);

// Character appearance rides the device-presence handshake: the server only relays
// descriptors from admitted peers, keyed by their current player attachment.
internal sealed partial class ClientTabletPresence
{
    private const int AppearanceMaximumBytes = 64;
    private const long AppearanceLifetime = 15000;
    private readonly Dictionary<Peer, AppearanceSlot> _appearancePeers = new();
    private readonly Dictionary<ulong, ClientRemoteAppearance> _appearances = new();
    private readonly Dictionary<ulong, (Guid Generation, long Until)> _appearanceRemovals = new();
    private Guid _appearanceEpoch, _appearanceToken;
    private AppearanceEvent? _appearanceEvent;
    private byte[] _localAppearance = Array.Empty<byte>();
    private byte[]? _sentAppearance;
    private long _appearanceSequence, _appearanceSentAt, _appearanceReceived, _nextAppearanceRows;

    internal IReadOnlyDictionary<ulong, ClientRemoteAppearance> Appearances => _appearances;

    // An empty descriptor means this player shows the game's own model.
    internal void SetLocalAppearance(byte[]? data)
    {
        if (OnThread) _localAppearance = data is { Length: <= AppearanceMaximumBytes } ? data : Array.Empty<byte>();
    }

    private void StartAppearance()
    {
        if (_appearanceEvent != null) return;
        _appearanceEvent = new AppearanceEvent(this);
        Packets.Register(_appearanceEvent);
    }

    private void StopAppearance()
    {
        var channel = _appearanceEvent;
        _appearanceEvent = null;
        if (channel != null) Packets.Unregister(channel);
        ClearAppearance();
    }

    private void ClearAppearance()
    {
        _appearancePeers.Clear();
        _appearances.Clear();
        _appearanceRemovals.Clear();
        _appearanceEpoch = _appearanceToken = Guid.Empty;
        _sentAppearance = null;
        _appearanceSequence = _appearanceSentAt = _appearanceReceived = _nextAppearanceRows = 0;
    }

    private void UpdateAppearance(long now)
    {
        if (_appearanceEvent == null) return;
        if (_mode == 2)
        {
            // A new presence session restarts both sides' sequences.
            if (_appearanceEpoch != _epoch || _appearanceToken != _token)
            {
                _appearanceEpoch = _epoch;
                _appearanceToken = _token;
                _appearanceReceived = 0;
                _sentAppearance = null;
                _appearances.Clear();
            }
            foreach (var pair in _appearances.ToArray())
                if (now - pair.Value.Seen > AppearanceLifetime) _appearances.Remove(pair.Key);
            if (_token == Guid.Empty) return;
            if (_sentAppearance != null && _sentAppearance.AsSpan().SequenceEqual(_localAppearance)
                && now - _appearanceSentAt < 4000) return;
            _appearanceEvent.Set(++_appearanceSequence, _localAppearance);
            _sentAppearance = _localAppearance;
            _appearanceSentAt = now;
            return;
        }
        if (_mode != 1 || now < _nextAppearanceRows) return;
        _nextAppearanceRows = now + 250;
        if (_host != null) Slot(_host).Data = _localAppearance;
        foreach (var (peer, slot) in _appearancePeers.ToArray())
        {
            var gone = !ReferenceEquals(peer, _host) && (!_peers.ContainsValue(peer) || !Current(peer));
            var actor = gone || peer.Binding == null ? 0 : peer.Binding.NetworkId;
            // Tell other players to drop a body whose owner left, rebound or went back to the game model.
            if (slot.Actor != 0 && (slot.Actor != actor || slot.Data.Length == 0))
                _appearanceRemovals[slot.Actor] = (slot.Generation, now + AppearanceLifetime);
            slot.Actor = slot.Data.Length == 0 ? 0 : actor;
            slot.Generation = peer.Generation;
            if (slot.Actor != 0) _appearanceRemovals.Remove(slot.Actor);
            if (gone) _appearancePeers.Remove(peer);
        }
        foreach (var (actor, removal) in _appearanceRemovals.ToArray())
            if (now >= removal.Until) _appearanceRemovals.Remove(actor);
        var rows = _appearancePeers.Values.Where(slot => slot.Actor != 0)
            .Select(slot => (slot.Actor, slot.Generation, slot.Data)).ToList();
        ApplyAppearances(rows, now);
        rows.AddRange(_appearanceRemovals.Select(pair => (pair.Key, pair.Value.Generation, Array.Empty<byte>())));
        if (rows.Count == 0) return;
        // One row per receiver per tick keeps every packet small; each row repeats within a few seconds.
        foreach (var receiver in _peers.Values)
        {
            if (!Current(receiver)) continue;
            var own = receiver.Binding?.NetworkId ?? 0;
            var slot = Slot(receiver);
            for (var attempt = 0; attempt < rows.Count; attempt++)
            {
                var (actor, generation, data) = rows[slot.Cursor++ % rows.Count];
                if (actor == own) continue;
                _appearanceEvent.Row(receiver, ++_appearanceSequence, actor, generation, data);
                break;
            }
        }
    }

    private void ApplyAppearances(IEnumerable<(ulong Actor, Guid Generation, byte[] Data)> rows, long now)
    {
        var active = new HashSet<ulong>();
        foreach (var (actor, generation, data) in rows)
        {
            active.Add(actor);
            _appearances[actor] = new ClientRemoteAppearance(generation, data, now);
        }
        foreach (var actor in _appearances.Keys.ToArray())
            if (!active.Contains(actor)) _appearances.Remove(actor);
    }

    private AppearanceSlot Slot(Peer peer)
    {
        if (!_appearancePeers.TryGetValue(peer, out var slot)) _appearancePeers[peer] = slot = new AppearanceSlot();
        return slot;
    }

    private void ReceiveAppearance(AppearanceMessage message, BoltConnection connection)
    {
        if (_mode == 1)
        {
            if (message.Kind != AppearanceKind.Set || connection == null || connection.WasCollected) return;
            var peer = _peers.Values.FirstOrDefault(value => value.Pointer == connection.Pointer
                && value.Connection == connection.ConnectionId && Current(value));
            if (peer == null || message.Epoch != _epoch || message.Token != peer.Token) return;
            var slot = Slot(peer);
            // A peer that said Hello again restarts its sequence under a new token.
            if (slot.Token != peer.Token)
            {
                slot.Token = peer.Token;
                slot.Sequence = 0;
                slot.Data = Array.Empty<byte>();
            }
            if (message.Sequence <= slot.Sequence || !slot.Packets.Take(Now, 2, 4)) return;
            if (message.Data.Length > 0 && !CharacterAppearance.TryDecode(message.Data, out _)) return;
            slot.Sequence = message.Sequence;
            slot.Data = message.Data;
            return;
        }
        if (_mode != 2 || message.Kind != AppearanceKind.Row || connection == null || connection.WasCollected
            || connection.Pointer != _serverPointer || connection.ConnectionId != _serverConnection
            || _token == Guid.Empty || message.Epoch != _epoch || message.Token != _token
            || message.Sequence <= _appearanceReceived) return;
        _appearanceReceived = message.Sequence;
        if (message.Data.Length == 0) _appearances.Remove(message.Actor);
        else if (CharacterAppearance.TryDecode(message.Data, out _))
            _appearances[message.Actor] = new ClientRemoteAppearance(message.Generation, message.Data, Now);
    }

    private sealed class AppearanceSlot
    {
        internal byte[] Data = Array.Empty<byte>();
        internal long Sequence;
        internal int Cursor;
        internal Guid Token, Generation;
        internal ulong Actor;
        internal readonly Rate Packets = new();
    }

    private enum AppearanceKind : byte { Set = 1, Row }

    private sealed class AppearanceMessage
    {
        internal AppearanceKind Kind;
        internal Guid Epoch, Token, Generation;
        internal long Sequence;
        internal ulong Actor;
        internal byte[] Data = Array.Empty<byte>();
    }

    private sealed class AppearanceEvent(ClientTabletPresence service) : Packets.NetEvent
    {
        private const int Maximum = 160;
        public override string Id => "modstoke.client.appearance.v1";
        public override bool AllowClientRelay => false;

        public override void Read(UdpPacket packet, BoltConnection fromConnection)
        {
            if (!service.OnThread || !service._started || service._mode == 0 || service._loading) return;
            try
            {
                if (!BoltNetwork.isRunning || packet == null || packet.WasCollected
                    || BoltNetwork.isServer && !service._ingress.Take(Now, MaxPeers * 12, MaxPeers * 12)) return;
                var header = BoltNetwork.isServer ? 7 : 11;
                if (packet.Size < (header + 4) * 8 || packet.Size > (header + 2 + Maximum) * 8) return;
                var length = packet.ReadUShort();
                if (length < 2 || length > Maximum || packet.Size != (header + 2 + length) * 8) return;
                var data = new byte[length];
                for (var i = 0; i < length; i++) data[i] = packet.ReadByte();
                using var stream = new MemoryStream(data, false);
                using var reader = new BinaryReader(stream);
                AppearanceMessage? message;
                try { message = Parse(reader); }
                catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException or OverflowException)
                { return; }
                if (message == null || stream.Position != stream.Length) return;
                if (BoltNetwork.isServer != (message.Kind == AppearanceKind.Set)) return;
                service.ReceiveAppearance(message, fromConnection);
            }
            catch (Exception error) { service.Fail(error); }
        }

        internal void Set(long sequence, byte[] data) => Emit(null, (sequence, data), (writer, state) =>
        {
            Head(writer, AppearanceKind.Set);
            WriteGuid(writer, service._epoch);
            WriteGuid(writer, service._token);
            writer.Write(state.sequence);
            writer.Write((byte)state.data.Length);
            writer.Write(state.data);
        });

        internal void Row(Peer receiver, long sequence, ulong actor, Guid generation, byte[] data) =>
            Emit(receiver, (receiver, sequence, actor, generation, data), (writer, state) =>
        {
            Head(writer, AppearanceKind.Row);
            WriteGuid(writer, service._epoch);
            WriteGuid(writer, state.receiver.Token);
            writer.Write(state.sequence);
            writer.Write(state.actor);
            WriteGuid(writer, state.generation);
            writer.Write((byte)state.data.Length);
            writer.Write(state.data);
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

        private static void Head(BinaryWriter writer, AppearanceKind kind) { writer.Write((byte)1); writer.Write((byte)kind); }

        private static AppearanceMessage? Parse(BinaryReader reader)
        {
            if (reader.ReadByte() != 1) return null;
            var message = new AppearanceMessage { Kind = (AppearanceKind)reader.ReadByte() };
            if (message.Kind is not (AppearanceKind.Set or AppearanceKind.Row)) return null;
            message.Epoch = ReadGuid(reader);
            message.Token = ReadGuid(reader);
            message.Sequence = reader.ReadInt64();
            if (message.Sequence <= 0) return null;
            if (message.Kind == AppearanceKind.Row)
            {
                message.Actor = reader.ReadUInt64();
                message.Generation = ReadGuid(reader);
                if (message.Actor == 0 || message.Generation == Guid.Empty) return null;
            }
            int length = reader.ReadByte();
            if (length > AppearanceMaximumBytes) return null;
            message.Data = reader.ReadBytes(length);
            return message.Data.Length == length ? message : null;
        }
    }
}
