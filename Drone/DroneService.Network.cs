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
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientDroneService
{
    private readonly Rate _receiveRate = new();
    private readonly Dictionary<long, PendingCommand> _pendingCommands = new();
    private readonly PendingCommand[] _pendingCommandSnapshot = new PendingCommand[8];
    private readonly KeyValuePair<string, Replica>[] _replicaSnapshot = new KeyValuePair<string, Replica>[MaxPeers];
    private bool _pendingCommandSnapshotInUse, _replicaSnapshotInUse;
    private readonly Dictionary<string, long> _bodySequences = new(StringComparer.Ordinal);
    private long _nextControl, _lastWelcomeAt;
    private long _nextHelloDiagnostic;
    private long _lastResult;
    private bool _viewVisible;
    private ClientDroneReadiness? _remoteReadiness;
    private string _detailsDroneId = "";
    private long _detailsAt, _lastDetailsSequence;

    internal void SetViewVisible(bool visible)
    {
        _viewVisible = visible;
        if (!visible) { DisableCameras(); HidePlacement(); }
    }

    private void UpdateClient(long now, ClientTabletPreferencesSnapshot? audioPreferences)
    {
        if ((_token == Guid.Empty || now - System.Math.Max(_lastOwnerSnapshot, _lastWelcomeAt) > 3000) && now >= _nextHello)
        {
            _nextHello = now + 1000;
            _event?.Hello();
        }
        // Nested callbacks need their own snapshot; reset must not clear an active one.
        var ownsCommandSnapshot = !_pendingCommandSnapshotInUse;
        var commands = ownsCommandSnapshot ? _pendingCommandSnapshot : new PendingCommand[8];
        var commandCount = 0;
        if (ownsCommandSnapshot) _pendingCommandSnapshotInUse = true;
        try
        {
            foreach (var pending in _pendingCommands.Values) commands[commandCount++] = pending;
            for (var i = 0; i < commandCount; i++)
            {
                var pending = commands[i];
                if (!_pendingCommands.TryGetValue(pending.Sequence, out var current) || !ReferenceEquals(current, pending)) continue;
                if (now < pending.NextSend) continue;
                if (pending.Attempts >= 5)
                {
                    _pendingCommands.Remove(pending.Sequence);
                    if (pending.Sequence == _pilotStartSequence)
                    {
                        _pilotStartPending = false;
                        _pilotRequested = false;
                        _clientLease = Guid.Empty;
                        DisableCameras();
                    }
                    _status = "Command outcome unknown. Check the drone state before retrying.";
                    continue;
                }
                pending.NextSend = now + 500;
                pending.Attempts++;
                _event?.SendRequest(pending.Command, pending.Sequence);
            }
        }
        finally
        {
            Array.Clear(commands, 0, commandCount);
            if (ownsCommandSnapshot) _pendingCommandSnapshotInUse = false;
        }
        var connected = _ready && _token != Guid.Empty && now - _lastOwnerSnapshot <= 1500;
        var linked = connected && _remoteView.Link;
        if (!linked && !_pilotStartPending && _pilotRequested) { _pilotRequested = false; _clientLease = Guid.Empty; DisableCameras(); }
        if (_pilotRequested && linked && _clientLease != Guid.Empty && now >= _nextControl)
        {
            _nextControl = now + 50;
            if (now - _inputAt <= ControlTimeout) _event?.Control(++_controlSequence, _clientLease, _input);
        }
        var ownsReplicaSnapshot = !_replicaSnapshotInUse;
        var replicas = ownsReplicaSnapshot ? _replicaSnapshot : new KeyValuePair<string, Replica>[MaxPeers];
        var replicaCount = 0;
        if (ownsReplicaSnapshot) _replicaSnapshotInUse = true;
        try
        {
            foreach (var pair in _replicas) replicas[replicaCount++] = pair;
            for (var i = 0; i < replicaCount; i++)
            {
                var pair = replicas[i];
                if (now - pair.Value.LastSeen > 3000)
                { pair.Value.Body.Dispose(); _replicas.Remove(pair.Key); continue; }
                pair.Value.Body.Update(Mathf.Min(Time.deltaTime, .1f), audioPreferences);
            }
        }
        finally
        {
            Array.Clear(replicas, 0, replicaCount);
            if (ownsReplicaSnapshot) _replicaSnapshotInUse = false;
        }
        var connectionStatus = !_ready || _token == Guid.Empty ? "Waiting for the host drone connection."
            : _lastClientSnapshot == 0 ? "Host connected. Waiting for inventory."
            : "Drone inventory updates are stale. Waiting for the host.";
        Snapshot = _remoteView with { Ready = connected, Status = connected ? _status : connectionStatus,
            Piloting = linked && _clientLease != Guid.Empty && _pilotRequested, Link = linked,
            Readiness = linked && _detailsDroneId == _remoteView.DroneId && now - _detailsAt <= 1500
                ? _remoteReadiness : null, LastContact = _lastContact,
            TerrainSurvey = CurrentRemoteTerrainSurvey(linked, now) };
    }

    private void SendSnapshots(long now)
    {
        if (_event == null || !BoltNetwork.isRunning || !BoltNetwork.isServer) return;
        var sequence = ++_snapshotSequence;
        foreach (var peer in _peers.Values)
        {
            if (!PeerCurrent(peer)) continue;
            if (_kits.TryGetValue(peer.Owner, out var kit))
            {
                var view = View(kit, peer, peer.LastResult);
                _event.Owner(peer, sequence, view);
                _event.Details(peer, sequence, kit, view);
                if (view.TerrainSurvey is { } survey && survey.RecordedAt > peer.LastTerrainSurveyAt)
                {
                    _event.TerrainSurvey(peer, sequence, survey);
                    peer.LastTerrainSurveyAt = survey.RecordedAt;
                }
            }
            foreach (var active in _kits.Values)
                if (active.Body?.Root is { } root && !root.WasCollected && root) _event.Body(peer, sequence, active);
            foreach (var id in _removed.Keys) _event.Removed(peer, sequence, id);
        }
        // Managed Dictionary removal preserves active enumeration on the pinned runtime.
        foreach (var entry in _removed)
            if (now >= entry.Value) _removed.Remove(entry.Key);
    }

    private Peer? ResolvePeer(BoltConnection connection)
    {
        if (!_ready || !BoltNetwork.isRunning || !BoltNetwork.isServer || connection == null || connection.WasCollected) return null;
        foreach (var peer in _peers.Values)
            if (peer.Pointer == connection.Pointer && peer.Connection == connection.ConnectionId && PeerCurrent(peer)) return peer;
        return null;
    }

    private void ReceiveHello(BoltConnection connection, Guid nonce)
    {
        if (nonce == Guid.Empty || connection == null || connection.WasCollected) return;
        if (!_ready || _epoch == Guid.Empty || _event == null)
        {
            ReportHelloUnavailable("Host world is not ready: " + _status);
            return;
        }
        var peer = ResolvePeer(connection);
        if (peer != null)
        {
            if (!peer.Commands.Take(Now, 4, 4)) return;
            if (peer.Nonce != nonce)
            {
                // A client service can restart while its native player connection stays alive.
                EndLease(peer);
                peer.Nonce = nonce;
                peer.Token = Guid.NewGuid();
                peer.LastCommand = peer.LastControlSequence = peer.LastControl = peer.LastTerrainSurveyAt = 0;
                peer.LastResult = "Ready.";
            }
            _event.Welcome(peer);
            return;
        }
        if (_peers.Count >= MaxPeers) { ReportHelloUnavailable("The drone peer limit is reached."); return; }
        if (!ClientConnections.Contains(connection.Pointer, connection.ConnectionId))
        { ReportHelloUnavailable("The native client connection is no longer current."); return; }
        foreach (var session in ServerSessions.Sessions)
        {
            if (!session.IsCurrent || session.ConnectionId != connection.ConnectionId) continue;
            if (!ServerSessions.TryReadIdentity(session, out var identity, out var identityStatus))
            { ReportHelloUnavailable("Player identity is unavailable: " + identityStatus + "."); return; }
            // Listen hosts use the transport identity; optional registration can be absent or different.
            var owner = identity.GameSteamId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (owner == _localOwner || _peers.Values.Any(value => value.Owner == owner && PeerCurrent(value)))
            { ReportHelloUnavailable("The player already has a current drone peer."); return; }
            if (!ServerWorld.TryCapturePlayerBinding(session, out var binding, out var bindingStatus))
            { ReportHelloUnavailable("Player binding is unavailable: " + bindingStatus + "."); return; }
            if (!ReferenceEquals(identity.Session, session) || binding.IsLocalPlayer
                || !ReferenceEquals(binding.PlayerSession, session))
            { ReportHelloUnavailable("Player binding does not match the current client session."); return; }
            peer = new Peer(owner, identity.GameSteamId) { Session = session, Binding = binding, Pointer = connection.Pointer,
                Connection = connection.ConnectionId, Nonce = nonce };
            if (!EnsureKit(owner))
            { ReportHelloUnavailable("Inventory kit could not be assigned: the world drone inventory limit is reached."); return; }
            _peers[session.SessionId] = peer;
            _event.Welcome(peer);
            Console.WriteLine("Modstoke drone admitted a new peer for connection " + connection.ConnectionId + ".");
            return;
        }
        ReportHelloUnavailable("No current player session matches the native client connection.");
    }

    private void ReportHelloUnavailable(string reason)
    {
        var now = Now;
        if (now < _nextHelloDiagnostic) return;
        // One deadline for the whole service, retained across world resets.
        _nextHelloDiagnostic = now + 30000;
        Console.WriteLine("Modstoke drone Hello could not be admitted: " + reason);
    }

    private void Receive(Message message, BoltConnection connection)
    {
        if (BoltNetwork.isServer)
        {
            if (message.Kind == Kind.Hello) { ReceiveHello(connection, message.Nonce); return; }
            if (!_ready) return;
            var peer = ResolvePeer(connection);
            if (peer == null || message.Epoch != _epoch || message.Token != peer.Token
                || !peer.Packets.Take(Now, 45, 12)) return;
            if (message.Kind == Kind.Command)
            {
                var previousLease = peer.Lease;
                var result = ApplyCommand(peer, message.Sequence, message.Command);
                _event?.Result(peer, message.Sequence, result);
                if (message.Command == ActionCode.PilotStart && peer.Lease != Guid.Empty && peer.Lease != previousLease
                    && _kits.TryGetValue(peer.Owner, out var kit))
                    _event?.Owner(peer, ++_snapshotSequence, View(kit, peer, peer.LastResult));
            }
            else if (message.Kind == Kind.Control) ApplyControl(peer, message.Lease, message.Sequence, message.Input);
            return;
        }
        if (!IsClient || connection == null || connection.WasCollected || connection.Pointer != _serverPointer
            || connection.ConnectionId != _serverConnection) return;
        var server = BoltNetwork.server;
        if (server == null || server.WasCollected || server.Pointer != connection.Pointer || server.ConnectionId != connection.ConnectionId) return;
        if (message.Kind == Kind.Welcome)
        {
            if (message.Nonce != _nonce || message.Epoch == Guid.Empty || message.Token == Guid.Empty) return;
            if (_ready)
            {
                if (message.Epoch == _epoch && message.Token == _token) return;
                if (Now - System.Math.Max(_lastOwnerSnapshot, _lastWelcomeAt) <= 3000) return;
                var saveManager = _saveManager;
                var nonce = _nonce;
                ResetWorld();
                _saveManager = saveManager;
                _serverPointer = connection.Pointer;
                _serverConnection = connection.ConnectionId;
                _nonce = nonce;
            }
            _epoch = message.Epoch;
            _token = message.Token;
            _lastWelcomeAt = Now;
            RefreshTerrainSurveySession();
            _ready = true;
            _status = "Host connected. Waiting for inventory.";
            return;
        }
        if (!_ready || message.Epoch != _epoch || message.Token != _token) return;
        if (message.Kind == Kind.Owner && message.Sequence > _lastClientSnapshot)
        {
            var receivedAt = Now;
            var nextOwner = message.View!;
            if (receivedAt - _lastOwnerSnapshot > TerrainSurveyLifetime || nextOwner.DroneId != _remoteView.DroneId
                || nextOwner.Deployed != _remoteView.Deployed || nextOwner.Link != _remoteView.Link)
            {
                _remoteTerrainSurvey = null;
                _terrainSurveyOwnerSequenceFloor = message.Sequence;
                _remoteReadiness = null;
                _detailsDroneId = "";
                _detailsAt = 0;
            }
            _lastClientSnapshot = message.Sequence;
            _lastOwnerSnapshot = receivedAt;
            _remoteView = nextOwner;
            if (!HasRadioContact(_remoteView) || _remoteTerrainSurvey?.DroneId != _remoteView.DroneId)
                _remoteTerrainSurvey = null;
            if (_lastContact?.DroneId != _remoteView.DroneId) _lastContact = null;
            var previousLease = _clientLease;
            _clientLease = message.Lease;
            if (_clientLease != Guid.Empty)
            {
                _pilotStartPending = false;
                _pendingCommands.Remove(_pilotStartSequence);
                // Acknowledge the exact owner grant without waiting for a UI/input frame.
                if (_clientLease != previousLease && _pilotRequested && _viewVisible && _remoteView.Link)
                {
                    _event?.Control(++_controlSequence, _clientLease, DronePilotInput.Neutral);
                    _nextControl = receivedAt;
                }
            }
            if (_pendingCommands.Count == 0) _status = _remoteView.Status;
            if (!nextOwner.Deployed)
            {
                _lastContact = null;
                _remoteReadiness = null;
                _detailsDroneId = "";
                var id = nextOwner.DroneId;
                if (!_bodySequences.TryGetValue(id, out var previous) || previous < message.Sequence)
                    _bodySequences[id] = message.Sequence;
                RemoveReplica(id);
            }
        }
        else if (message.Kind == Kind.Details && message.Sequence > _lastDetailsSequence
            && message.Sequence >= _lastClientSnapshot && message.Id == _remoteView.DroneId)
        {
            _lastDetailsSequence = message.Sequence;
            _detailsAt = Now;
            _detailsDroneId = message.Id;
            _remoteReadiness = message.Readiness;
            if (message.HasPosition && _remoteView.Deployed && _remoteView.Link)
                RecordContact(message.Id, message.Position, _detailsAt);
        }
        else if (message.Kind == Kind.TerrainSurvey) ReceiveTerrainSurvey(message);
        else if (message.Kind == Kind.Body)
        {
            if (_bodySequences.TryGetValue(message.Id, out var previous) && previous >= message.Sequence) return;
            if (!_bodySequences.ContainsKey(message.Id) && _bodySequences.Count >= MaxOwners) return;
            _bodySequences[message.Id] = message.Sequence;
            if (!message.Deployed) { RemoveReplica(message.Id); return; }
            if (_replicas.TryGetValue(message.Id, out var stale)
                && (stale.Body.Root == null || stale.Body.Root.WasCollected || !stale.Body.Root))
                RemoveReplica(message.Id);
            if (!_replicas.TryGetValue(message.Id, out var replica))
            {
                if (_replicas.Count >= MaxPeers) return;
                replica = new Replica(new ClientDroneBody(message.Id, message.Position, message.Rotation, false));
                _replicas.Add(message.Id, replica);
            }
            replica.LastSeen = Now;
            replica.Sequence = message.Sequence;
            replica.Body.SetReplica(message.Position, message.Rotation, message.Velocity, message.Charge, message.Health,
                message.Armed, message.Mode);
            replica.Body.SetSolarDeployed(message.Solar);
            replica.Body.SetSolarCharging(message.Mode == "Solar charging");
        }
        else if (message.Kind == Kind.Result && message.Sequence <= _commandSequence)
        {
            // Success can arrive without its lease snapshot. Keep the bounded retry
            // deadline until an Owner packet actually supplies that lease.
            if (message.Sequence != _pilotStartSequence || message.Status != "Pilot link connected." || !_pilotStartPending)
                _pendingCommands.Remove(message.Sequence);
            if (message.Sequence == _pilotStartSequence && message.Status != "Pilot link connected.")
            { _pilotStartPending = false; _pilotRequested = false; }
            if (message.Sequence > _lastResult) { _lastResult = message.Sequence; _status = message.Status; }
        }
    }

    private void RemoveReplica(string id)
    {
        if (_replicas.Remove(id, out var replica)) replica.Body.Dispose();
    }

    private sealed class PendingCommand(long sequence, ActionCode command)
    {
        internal readonly long Sequence = sequence;
        internal readonly ActionCode Command = command;
        internal long NextSend = Now + 500;
        internal int Attempts = 1;
    }

    // Details and TerrainSurvey are optional companion packets. Existing v1 layouts
    // stay intact; older clients ignore new kinds and older hosts leave those observations unknown.
    private enum Kind : byte { Hello = 1, Welcome, Command, Control, Owner, Body, Result, Details, TerrainSurvey }
    private sealed class Message
    {
        internal Kind Kind;
        internal Guid Nonce, Epoch, Token, Lease;
        internal long Sequence;
        internal ActionCode Command;
        internal DronePilotInput Input;
        internal ClientDroneView? View;
        internal ClientDroneReadiness? Readiness;
        internal ClientDroneTerrainSurvey? TerrainSurvey;
        internal string Id = "", Mode = "", Status = "";
        internal bool Deployed, Armed, Solar, HasPosition;
        internal Vector3 Position, Velocity;
        internal Quaternion Rotation;
        internal float Charge, Health;
    }

    private sealed class DroneEvent(ClientDroneService service) : Packets.NetEvent
    {
        private const int MaxPayload = 800;
        private static readonly UTF8Encoding Utf8 = new(false, true);
        public override string Id => "modstoke.client.drone.v2";
        public override bool AllowClientRelay => false;

        public override void Read(UdpPacket packet, BoltConnection fromConnection)
        {
            if (!service.OnThread || !service._started || !BoltNetwork.isRunning || packet == null || packet.WasCollected
                || BoltNetwork.isServer && !service._receiveRate.Take(Now, MaxPeers * 45, MaxPeers * 12)) return;
            try
            {
                var header = BoltNetwork.isServer ? 7 : 11;
                // UdpPacket.Size is in bits; the native envelope and our payload length are bytes.
                if (packet.Size < (header + 4) * 8 || packet.Size > (header + 2 + MaxPayload) * 8) return;
                var length = packet.ReadUShort();
                if (length < 2 || length > MaxPayload || packet.Size != (header + 2 + length) * 8) return;
                var data = new byte[length];
                for (var i = 0; i < length; i++) data[i] = packet.ReadByte();
                using var stream = new MemoryStream(data, false);
                using var reader = new BinaryReader(stream, Utf8);
                var message = Parse(reader);
                if (message == null || stream.Position != stream.Length) return;
                if (BoltNetwork.isServer ? message.Kind is not (Kind.Hello or Kind.Command or Kind.Control)
                    : message.Kind is Kind.Hello or Kind.Command or Kind.Control) return;
                service.Receive(message, fromConnection);
            }
            catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException or OverflowException) { }
        }

        internal void Hello() => Emit(null, service, static (writer, state) =>
        { Head(writer, Kind.Hello); GuidValue(writer, state._nonce); });
        internal void Welcome(Peer peer) =>
            Emit(peer, (Event: this, Peer: peer), static (writer, state) =>
        { Head(writer, Kind.Welcome); GuidValue(writer, state.Peer.Nonce); state.Event.Envelope(writer, state.Peer); });
        internal bool Request(ActionCode command, long sequence)
        {
            // The newer release sequence supersedes older requests at the host.
            // Reserve its delivery even when the ordinary command queue is full.
            if (command == ActionCode.PilotStop) service._pendingCommands.Clear();
            if (service._pendingCommands.Count >= 8) return false;
            service._pendingCommands.Add(sequence, new PendingCommand(sequence, command));
            SendRequest(command, sequence);
            return true;
        }
        internal void SendRequest(ActionCode command, long sequence) =>
            Emit(null, (Event: this, Command: command, Sequence: sequence), static (writer, state) =>
        { Head(writer, Kind.Command); state.Event.Envelope(writer, null); writer.Write(state.Sequence); writer.Write((byte)state.Command); });
        internal void Control(long sequence, Guid lease, DronePilotInput input) =>
            Emit(null, (Event: this, Sequence: sequence, Lease: lease, Input: input), static (writer, state) =>
        {
            Head(writer, Kind.Control); state.Event.Envelope(writer, null); writer.Write(state.Sequence); GuidValue(writer, state.Lease);
            writer.Write(state.Input.Forward); writer.Write(state.Input.Right); writer.Write(state.Input.Climb); writer.Write(state.Input.Yaw);
        });
        internal void Result(Peer peer, long sequence, string status) =>
            Emit(peer, (Event: this, Peer: peer, Sequence: sequence, Status: status), static (writer, state) =>
        { Head(writer, Kind.Result); state.Event.Envelope(writer, state.Peer); writer.Write(state.Sequence); Text(writer, state.Status); });
        internal void Owner(Peer peer, long sequence, ClientDroneView view) =>
            Emit(peer, (Event: this, Peer: peer, Sequence: sequence, View: view), static (writer, state) =>
        {
            var (sender, peer, sequence, view) = state;
            Head(writer, Kind.Owner); sender.Envelope(writer, peer); writer.Write(sequence); GuidValue(writer, peer.Lease);
            Text(writer, view.DroneId); Text(writer, view.PackAId); Text(writer, view.PackBId);
            writer.Write(view.PackACharge); writer.Write(view.PackBCharge); writer.Write(view.Health);
            writer.Write(view.PackAInstalled); writer.Write(view.Deployed); writer.Write(view.Armed); writer.Write(view.Charging); writer.Write(view.Link);
            writer.Write(view.Speed); writer.Write(view.Altitude); writer.Write(view.Range);
            Text(writer, view.Mode); Text(writer, view.Status);
        });
        internal void Body(Peer peer, long sequence, Kit kit) =>
            Emit(peer, (Event: this, Peer: peer, Sequence: sequence, Kit: kit), static (writer, state) =>
        {
            var (sender, peer, sequence, kit) = state;
            Head(writer, Kind.Body); sender.Envelope(writer, peer); writer.Write(sequence); Text(writer, kit.Id); writer.Write(true);
            var body = kit.Body!;
            Vector(writer, body.Position); writer.Write(body.Rotation.x); writer.Write(body.Rotation.y);
            writer.Write(body.Rotation.z); writer.Write(body.Rotation.w); Vector(writer, body.Velocity);
            writer.Write(body.BatteryCharge); writer.Write(body.Health); writer.Write(body.Armed); writer.Write(kit.Solar);
            Text(writer, body.SolarMoving ? body.SolarStatus
                : kit.Solar ? kit.Charging ? "Solar charging" : "Solar waiting" : body.Mode);
        });
        internal void Details(Peer peer, long sequence, Kit kit, ClientDroneView view) =>
            Emit(peer, (Event: this, Peer: peer, Sequence: sequence, Kit: kit, View: view), static (writer, state) =>
        {
            var (sender, peer, sequence, kit, view) = state;
            Head(writer, Kind.Details); sender.Envelope(writer, peer); writer.Write(sequence); Text(writer, kit.Id);
            writer.Write(view.Readiness != null);
            if (view.Readiness is { } ready)
            { writer.Write(ready.Grounded); writer.Write(ready.Upright); writer.Write(ready.SolarDeployed); }
            var contact = HasRadioContact(view) && kit.Body?.Root is { } root && !root.WasCollected && root;
            writer.Write(contact);
            if (contact) Vector(writer, kit.Body!.Position);
        });
        internal void Removed(Peer peer, long sequence, string id) =>
            Emit(peer, (Event: this, Peer: peer, Sequence: sequence, Id: id), static (writer, state) =>
        { Head(writer, Kind.Body); state.Event.Envelope(writer, state.Peer); writer.Write(state.Sequence); Text(writer, state.Id); writer.Write(false); });

        internal void TerrainSurvey(Peer peer, long sequence, ClientDroneTerrainSurvey survey) =>
            Emit(peer, (Event: this, Peer: peer, Sequence: sequence, Survey: survey), static (writer, state) =>
        {
            var (sender, peer, sequence, survey) = state;
            Head(writer, Kind.TerrainSurvey); sender.Envelope(writer, peer); writer.Write(sequence); Text(writer, survey.DroneId);
            writer.Write((byte)survey.Status);
            Vector(writer, new Vector3(survey.Position.X, survey.Position.Y, survey.Position.Z));
            if (survey.Status == ClientDroneTerrainSurveyStatus.Sampled)
            {
                var normal = survey.Normal!;
                writer.Write(survey.SlopeDegrees!.Value);
                Vector(writer, new Vector3(normal.X, normal.Y, normal.Z));
            }
        });

        private void Emit<TState>(Peer? peer, TState state, Action<BinaryWriter, TState> write)
        {
            if (!service._started || !BoltNetwork.isRunning || peer != null && !service.PeerCurrent(peer)) return;
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Utf8, true)) write(writer, state);
            if (stream.Length > MaxPayload) return;
            var length = (int)stream.Length;
            var data = stream.GetBuffer();
            var packet = peer == null ? NewPacket(length + 2, GlobalTargets.OnlyServer) : NewPacket(length + 2, peer.Connection);
            packet.Packet.WriteUShort((ushort)length);
            for (var index = 0; index < length; index++) packet.Packet.WriteByte(data[index]);
            Send(packet);
        }

        private void Envelope(BinaryWriter writer, Peer? peer)
        { GuidValue(writer, service._epoch); GuidValue(writer, peer?.Token ?? service._token); }
        private static void Head(BinaryWriter writer, Kind kind) { writer.Write((byte)1); writer.Write((byte)kind); }
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
            value ??= "";
            var length = Utf8.GetByteCount(value);
            if (length > 192) throw new InvalidDataException();
            Span<byte> data = stackalloc byte[length];
            Utf8.GetBytes(value, data);
            writer.Write((byte)length); writer.Write(data);
        }
        private static string Text(BinaryReader reader, int max)
        {
            var length = reader.ReadByte();
            if (length > max) throw new InvalidDataException();
            Span<byte> data = stackalloc byte[length];
            ReadExactly(reader, data);
            return Utf8.GetString(data);
        }
        private static void ReadExactly(BinaryReader reader, Span<byte> destination)
        {
            var read = 0;
            while (read < destination.Length)
            {
                var count = reader.Read(destination[read..]);
                if (count == 0) throw new EndOfStreamException();
                read += count;
            }
        }
        private static void Vector(BinaryWriter writer, Vector3 value) { writer.Write(value.x); writer.Write(value.y); writer.Write(value.z); }
        private static Vector3 Vector(BinaryReader reader) => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        private static bool Boolean(BinaryReader reader) => reader.ReadByte() switch { 0 => false, 1 => true, _ => throw new InvalidDataException() };

        private static Message? Parse(BinaryReader reader)
        {
            if (reader.ReadByte() != 1) return null;
            var message = new Message { Kind = (Kind)reader.ReadByte() };
            if (message.Kind == Kind.Hello) { message.Nonce = GuidValue(reader); return message; }
            if (message.Kind == Kind.Welcome) message.Nonce = GuidValue(reader);
            message.Epoch = GuidValue(reader); message.Token = GuidValue(reader);
            if (message.Kind == Kind.Welcome) return message;
            message.Sequence = reader.ReadInt64();
            if (message.Sequence <= 0) return null;
            switch (message.Kind)
            {
                case Kind.Command:
                    message.Command = (ActionCode)reader.ReadByte();
                    if (message.Command > ActionCode.PilotStop) return null;
                    break;
                case Kind.Control:
                    message.Lease = GuidValue(reader);
                    message.Input = new DronePilotInput(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    if (!ValidInput(message.Input)) return null;
                    break;
                case Kind.Result: message.Status = Text(reader, 192); break;
                case Kind.Details:
                    message.Id = Text(reader, 32);
                    if (!ValidId(message.Id)) return null;
                    if (Boolean(reader)) message.Readiness = new(Boolean(reader), Boolean(reader), Boolean(reader));
                    message.HasPosition = Boolean(reader);
                    if (message.HasPosition)
                    {
                        message.Position = Vector(reader);
                        if (!Finite(message.Position) || message.Readiness == null) return null;
                    }
                    break;
                case Kind.TerrainSurvey:
                    message.Id = Text(reader, 32);
                    var terrainStatus = (ClientDroneTerrainSurveyStatus)reader.ReadByte();
                    var terrainPosition = Vector(reader);
                    if (!ValidId(message.Id) || terrainStatus > ClientDroneTerrainSurveyStatus.Sampled || !Finite(terrainPosition)) return null;
                    float? terrainSlope = null;
                    ClientFieldPoint? terrainNormal = null;
                    if (terrainStatus == ClientDroneTerrainSurveyStatus.Sampled)
                    {
                        var slope = reader.ReadSingle();
                        var normal = Vector(reader);
                        if (!float.IsFinite(slope) || slope < 0 || slope > 180 || !Finite(normal)
                            || System.Math.Abs(normal.sqrMagnitude - 1f) > .001f) return null;
                        terrainSlope = slope;
                        terrainNormal = new(normal.x, normal.y, normal.z);
                    }
                    message.TerrainSurvey = new(message.Id, new(terrainPosition.x, terrainPosition.y, terrainPosition.z),
                        terrainStatus, terrainSlope, terrainNormal, 0, true);
                    break;
                case Kind.Owner:
                    message.Lease = GuidValue(reader);
                    var id = Text(reader, 32); var a = Text(reader, 32); var b = Text(reader, 32);
                    var ca = reader.ReadSingle(); var cb = reader.ReadSingle(); var health = reader.ReadSingle();
                    var installedA = Boolean(reader); var deployed = Boolean(reader); var armed = Boolean(reader); var charging = Boolean(reader);
                    var link = Boolean(reader);
                    var speed = reader.ReadSingle(); var altitude = reader.ReadSingle(); var range = reader.ReadSingle();
                    var mode = Text(reader, 32); var status = Text(reader, 192);
                    if (!ValidId(id) || !ValidId(a) || !ValidId(b) || id == a || a == b || id == b
                        || !Unit(ca) || !Unit(cb) || !Unit(health) || !float.IsFinite(speed) || speed < 0 || speed > 1000
                        || !float.IsFinite(altitude) || altitude < 0 || altitude > 10000
                        || !float.IsFinite(range) || range < 0 || range > 200000) return null;
                    message.View = new ClientDroneView(true, status, !deployed, 1, deployed, armed,
                        message.Lease != Guid.Empty, installedA ? ca : cb, health, speed, altitude, range, mode, charging,
                        id, installedA ? a : b, a, ca, installedA, b, cb, !installedA, link);
                    break;
                case Kind.Body:
                    message.Id = Text(reader, 32); message.Deployed = Boolean(reader);
                    if (!ValidId(message.Id)) return null;
                    if (!message.Deployed) break;
                    message.Position = Vector(reader);
                    message.Rotation = new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    message.Velocity = Vector(reader); message.Charge = reader.ReadSingle(); message.Health = reader.ReadSingle();
                    message.Armed = Boolean(reader); message.Solar = Boolean(reader); message.Mode = Text(reader, 32);
                    if (!Finite(message.Position) || !Finite(message.Velocity) || message.Velocity.sqrMagnitude > 1000000
                        || !ValidRotation(message.Rotation) || !Unit(message.Charge) || !Unit(message.Health)) return null;
                    break;
                default: return null;
            }
            return message;
        }
    }
}
