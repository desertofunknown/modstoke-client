// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.IO;
using System.Linq;
using System.Text;
using Endnight.Extensions;
using Modstoke.Sons;
using SonsSdk.Networking;
using UdpKit;
using UnityEngine;
using GlobalTargets = Bolt.GlobalTargets;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftService
{
    private Peer? ResolvePeer(BoltConnection connection) => _peers.Values.FirstOrDefault(peer =>
        peer.Pointer == connection.Pointer && peer.Connection == connection.ConnectionId && PeerCurrent(peer));

    private void Hello(BoltConnection connection, Guid nonce, long sequence, bool requestOrbits)
    {
        if (nonce == Guid.Empty || sequence <= 0 || !BoltNetwork.isServer || !_ready) return;
        var peer = _peers.Values.FirstOrDefault(value => value.Pointer == connection.Pointer
            && value.Connection == connection.ConnectionId);
        if (peer != null)
        {
            if (!PeerCurrent(peer)) return;
            if (!peer.Commands.Take(Now, 4, 4)) return;
            if (peer.Nonce != nonce)
            {
                // A delayed Hello cannot replace a live generation. The new client
                // must echo a fresh server token before its predecessor is retired.
                if (peer.PendingNonce != nonce || peer.PendingHelloSequence != sequence || Now >= peer.PendingHelloUntil)
                {
                    peer.PendingNonce = nonce; peer.PendingHelloSequence = sequence;
                    peer.PendingToken = Guid.NewGuid(); peer.PendingHelloUntil = Now + 5000;
                }
                _event?.Welcome(peer, nonce, peer.PendingToken, sequence, false);
                return;
            }
            if (sequence < peer.HelloSequence) return;
            if (sequence > peer.HelloSequence) ClearPendingHello(peer);
            peer.HelloSequence = sequence;
            peer.LastSeen = Now;
            if (requestOrbits && Now >= peer.NextOrbitSnapshot - 13_000) peer.NextOrbitSnapshot = 0;
            _event?.Welcome(peer, nonce, peer.Token, sequence, false); return;
        }
        if (_peers.Count >= 16) return;
        if (!ClientConnections.Contains(connection.Pointer, connection.ConnectionId)) return;
        foreach (var session in ServerSessions.Sessions)
        {
            if (!session.IsCurrent || session.ConnectionId != connection.ConnectionId) continue;
            if (!ServerSessions.TryReadIdentity(session, out var identity, out _)
                || _peers.Values.Any(value => value.Owner == identity.GameSteamId
                    && ReadPeerIdentity(value) != PeerIdentity.Retired)
                || !ServerWorld.TryCapturePlayerBinding(session, out var binding, out _)) return;
            peer = new Peer { Session = session, Binding = binding, Owner = identity.GameSteamId,
                Connection = connection.ConnectionId, Pointer = connection.Pointer, Nonce = nonce,
                HelloSequence = sequence, LastSeen = Now };
            _peers.Add(session.SessionId, peer);
            PublishHullState();
            MarkFoamDirty();
            MarkRepairDirty();
            MarkMaterialsDirty();
            MarkEngineeringDirty();
            _event?.Welcome(peer, nonce, peer.Token, sequence, false); return;
        }
    }

    private static void ClearPendingHello(Peer peer)
    {
        peer.PendingNonce = peer.PendingToken = Guid.Empty;
        peer.PendingHelloSequence = peer.PendingHelloUntil = 0;
    }

    private void ConfirmHello(BoltConnection connection, Message message)
    {
        if (!_ready || message.Epoch != _epoch) return;
        var peer = ResolvePeer(connection);
        if (peer == null || !peer.Commands.Take(Now, 4, 4)) return;
        if (message.Nonce != peer.Nonce || message.Token != peer.Token)
        {
            if (message.Nonce != peer.PendingNonce || message.Token != peer.PendingToken
                || message.Sequence != peer.PendingHelloSequence || Now >= peer.PendingHelloUntil) return;
            ReleaseSeat(peer, null, true); _crew.Remove(peer);
            RetireMovementEvidence(peer);
            peer.Nonce = message.Nonce; peer.Token = message.Token;
            peer.HelloSequence = message.Sequence;
            peer.CrewApproved = false; peer.CrewRequestedAt = 0;
            peer.LastCommand = peer.LastControlSequence = peer.LastSeatSequence = 0;
            peer.Engineering = new();
            peer.FrameRevision = 0;
            peer.NextOrbitSnapshot = 0;
            PublishHullState();
            MarkFoamDirty();
            MarkRepairDirty();
            MarkMaterialsDirty();
            MarkEngineeringDirty();
            peer.Result = "Reconnected to the shared spacecraft. Return protection must be prepared again.";
        }
        else if (message.Sequence != peer.HelloSequence) return;
        ClearPendingHello(peer);
        peer.LastSeen = Now;
        _event?.Welcome(peer, peer.Nonce, peer.Token, message.Sequence, true);
        _event?.Snapshot(peer, ++_snapshotSequence);
    }

    private void Receive(Message message, BoltConnection connection)
    {
        if (!ServiceRunning || connection == null || connection.WasCollected) return;
        if (BoltNetwork.isServer)
        {
            if (message.Kind == Kind.Hello) { Hello(connection, message.Nonce, message.Sequence, message.OrbitRequested); return; }
            if (message.Kind == Kind.HelloConfirm) { ConfirmHello(connection, message); return; }
            if (!_ready) return;
            var peer = ResolvePeer(connection);
            if (peer == null || message.Epoch != _epoch || message.Token != peer.Token
                || !peer.Packets.Take(Now, 45, 12)) return;
            peer.LastSeen = Now;
            if (message.Kind == Kind.Command)
            {
                // Readiness claims never grant authority: the request still needs
                // the exact connection, current live actor, distance and seat lease.
                if (message.Sequence > peer.LastCommand)
                {
                    ClearPendingHello(peer);
                    peer.Prepared = message.Prepared; peer.ReadyAt = Now;
                    if (!peer.Prepared) ClearConsolePose(peer);
                }
                ApplyCommand(peer, message.Sequence, message.Action,
                    stationTarget: message.StationTarget, stationSetting: message.StationSetting,
                    stationAim: message.StationAim, stationLease: message.Lease);
                _event?.Snapshot(peer, ++_snapshotSequence);
            }
            else if (message.Kind == Kind.EngineeringWork && message.Ship == _shipId && _shipId != Guid.Empty
                && peer.Lease != Guid.Empty && message.Lease == peer.Lease)
                ReceiveEngineeringWork(peer, message);
            else if (message.Kind == Kind.ShipRepairInput && message.Ship == _shipId && _shipId != Guid.Empty
                && peer.Lease != Guid.Empty && message.Lease == peer.Lease)
                ReceiveManualRepairInput(peer, message);
            else if (message.Kind == Kind.SeatStatus && message.Sequence > peer.LastSeatSequence
                && message.Lease == peer.Lease && message.FrameRevision <= _frameRevision
                && TryActor(peer, out var position, out _, out _))
            {
                ClearPendingHello(peer);
                peer.LastSeatSequence = message.Sequence;
                peer.FrameRevision = Math.Max(peer.FrameRevision, message.FrameRevision);
                peer.Prepared = message.Prepared; peer.ReadyAt = Now;
                var retainedDuringJump = peer.Attached && Now <= peer.DistanceGrace;
                TryRestoreAttachmentEvidence(peer, position);
                var actorAtSeat = ActorAtSeat(peer);
                peer.Attached = peer.Seat >= 0 && message.Attached && (retainedDuringJump || actorAtSeat);
                if (peer.Attached && actorAtSeat)
                {
                    peer.AttachedAt = Now;
                    peer.AttachmentSuspended = false;
                    peer.Recovery = null;
                }
                if (!peer.Prepared || !peer.Attached) ClearConsolePose(peer);
                if (peer.ForceReturn && !message.Prepared && !message.Attached
                    && TryActor(peer, out var returned, out _, out _) && ConfirmMovementReturn(peer, returned))
                { peer.ForceReturn = false; _crew.Remove(peer); }
            }
            else if (message.Kind == Kind.Control && peer.Seat == 0 && !peer.Walking && peer.Attached && peer.Prepared
                && HasCrewAccess(peer) && ReferenceEquals(_seats[0], peer) && peer.Lease != Guid.Empty && message.Lease == peer.Lease
                && message.Sequence > peer.LastControlSequence && peer.Controls.Take(Now, 35, 8)
                && TryActor(peer, out _, out _, out var pilotActor))
            {
                ClearPendingHello(peer);
                peer.Input = message.Controls; peer.LastControl = Now; peer.LastControlSequence = message.Sequence;
                ReceiveConsolePose(peer, message.Controls, pilotActor);
            }
            else if (message.Kind == Kind.ConsolePose && peer.Seat == 1 && !peer.Walking && peer.Attached && peer.Prepared
                && HasCrewAccess(peer) && ReferenceEquals(_seats[1], peer) && peer.Lease != Guid.Empty && message.Lease == peer.Lease
                && message.Sequence > peer.LastControlSequence && peer.Controls.Take(Now, 35, 8)
                && TryActor(peer, out _, out _, out var copilotActor))
            {
                ClearPendingHello(peer);
                peer.LastControlSequence = message.Sequence;
                ReceiveConsolePose(peer, message.Controls, copilotActor);
            }
            return;
        }
        if (!IsClient || connection.Pointer != _serverPointer || connection.ConnectionId != _serverConnection) return;
        var server = BoltNetwork.server;
        if (server == null || server.WasCollected || server.Pointer != connection.Pointer
            || server.ConnectionId != connection.ConnectionId) return;
        if (message.Kind == Kind.Welcome)
        {
            if (message.Nonce != _nonce || _pendingHelloSequence == 0 || message.Sequence != _pendingHelloSequence) return;
            if (!message.HelloConfirmed)
            {
                _helloEpoch = message.Epoch; _helloToken = message.Token;
                _event?.ConfirmHello();
                return;
            }
            if (message.Epoch != _helloEpoch || message.Token != _helloToken) return;
            _pendingHelloSequence = 0;
            _helloEpoch = _helloToken = Guid.Empty;
            if (message.Epoch != _epoch || message.Token != _token)
            {
                ReturnLocal("The spacecraft authority changed. Returned to the island.");
                ClearClientShip(); _receivedSequence = 0; _pending = null;
                _frameRevision = 0;
                _receivedFlightTime = 0;
                ResetOrbits();
                _stationState = null; _lastStationAt = _receivedStationSequence = 0;
                ResetStationTransport();
                ResetPortableTransport();
                ResetHullTransport();
                ReleaseOrbitalStation();
                _stationFailed = false;
                _evaState = null; _lastEvaAt = _receivedEvaSequence = 0;
                _commandSequence = _controlSequence = _seatSequence = 0;
                _cancelledBoardingSequence = 0;
            }
            _epoch = message.Epoch; _token = message.Token;
            _ready = true; _status = "Connected to the host's shared spacecraft."; return;
        }
        if (!_ready || message.Epoch != _epoch || message.Token != _token) return;
        if (message.Kind == Kind.AttachmentRecovery) { ReceiveAttachmentRecovery(message); return; }
        if (message.Kind == Kind.SurfaceRecovery) { ReceiveSurfaceRecovery(message); return; }
        if (message.Kind == Kind.Orbits) { ReceiveOrbits(message); return; }
        if (message.Kind == Kind.Station) { ReceiveStation(message); return; }
        if (message.Kind == Kind.Hull) { ReceiveHull(message); return; }
        if (message.Kind == Kind.Foam) { ReceiveFoam(message); return; }
        if (message.Kind == Kind.Repair) { ReceiveRepair(message); return; }
        if (message.Kind == Kind.Materials) { ReceiveMaterials(message); return; }
        if (message.Kind == Kind.Engineering) { ReceiveEngineering(message); return; }
        if (message.Kind == Kind.ShipRepairState) { ReceiveManualRepairState(message); return; }
        if (message.Kind == Kind.Eva) { ReceiveEva(message); return; }
        if (message.Kind is Kind.FirefightingSnapshot or Kind.FirefightingBurst) { ReceiveFirefighting(message); return; }
        if (message.Kind != Kind.Snapshot || message.Sequence <= _receivedSequence) return;
        if (message.Ship != Guid.Empty && message.FlightTime < _receivedFlightTime) return;
        if (_shipId == message.Ship && message.Ship != Guid.Empty
            && (message.FrameRevision < _frameRevision
                || message.FrameRevision == _frameRevision
                    && (!SameSpaceFrame(message.SpaceFrame, _spaceFrame) || message.SpaceFrameTime != _spaceFrameTime
                        || message.SpaceCruiseVelocity != _spaceCruiseVelocity))) return;
        _receivedSequence = message.Sequence; _lastSnapshot = Now;
        if (_pending != null && message.Acknowledged >= _pending.Sequence)
        {
            var preparing = _pending.Action == ActionCode.Prepare;
            _pending = null; _feedback = message.Result;
            if (preparing)
                ConfirmEntryPreparation(message.EntryPrepared, message.EntryAnchor);
        }
        else if (_pending == null && message.Acknowledged == _commandSequence) _feedback = message.Result;
        if (_shipId != message.Ship || message.Ship == Guid.Empty)
        {
            if (_ridingLease != Guid.Empty || _expedition.Active && _shipId != Guid.Empty)
                ReturnLocal("The shared spacecraft was removed. Returned to the island.");
            ClearClientShip(); _shipId = message.Ship;
        }
        if (message.Ship != Guid.Empty) _receivedFlightTime = message.FlightTime;
        // Keep existing custody while cancellation is pending. A delayed grant
        // must neither start a rider nor erase the lease awaiting its safe exit.
        var cancelledBoarding = message.Acknowledged < _cancelledBoardingSequence;
        if (!cancelledBoarding)
        {
            var retainedWalking = message.Seat is >= 0 and < 3
                && message.Seat == _clientSeat && message.Lease == _clientLease
                && message.Lease != Guid.Empty && message.Actors[message.Seat] == 0 && _clientWalking;
            _clientSeat = message.Seat;
            _clientLease = message.Lease;
            _clientWalking = retainedWalking || message.Seat >= 0 && message.Walking[message.Seat];
            if (!retainedWalking) _clientCabinStandLocal = _clientWalking ? message.CabinStandLocal : null;
            if (_clientLease != _recoveringLease || message.Sequence > _recoverySnapshot
                && _clientSeat is >= 0 and < 3 && message.Actors[_clientSeat] != 0)
                _awaitingAttachmentRecovery = false;
            _clientExit = message.Exit;
            _receivedSurfaceRecovery = Math.Max(_receivedSurfaceRecovery, message.SurfaceCompleted);
            if (!message.ForceReturn) _handledClientReturn = false;
            _clientReturn = message.ForceReturn && !_handledClientReturn;
            if (_cancelBoardingRequested && (_clientSeat < 0 || _clientWalking))
                _cancelBoardingRequested = false;
        }
        Array.Copy(message.Actors, _actorIds, 3);
        Array.Copy(message.Walking, _walking, 3);
        Array.Copy(message.Interactions, _interactions, 3);
        Array.Copy(message.ConsolePoses, _consolePoses, 2);
        if (_shipId == Guid.Empty)
        {
            ObserveStorage(message.StorageId, message.StorageAboard, message.StorageParkPosition, message.StorageParkRotation);
            _clientCrewAccess = message.CrewAccess;
            return;
        }
        var priorPosition = _body?.State.Position;
        var changedFrame = _frameRevision != message.FrameRevision;
        var sameSpace = _body?.State.Region == SpacecraftRegion.Space && message.Flight.Region == SpacecraftRegion.Space;
        if (_body == null)
        {
            if (!ClientSpacecraftBody.PrepareAssets(out var assetStatus))
            {
                _assetsLoading = assetStatus.StartsWith("Loading ", StringComparison.Ordinal); _status = assetStatus;
                _clientCrewAccess = message.CrewAccess; return;
            }
            _assetsLoading = false;
            _body = new ClientSpacecraftBody(message.Flight.Position, message.Flight.Rotation, false);
            if (!_body.Ready)
            {
                RemoveBody(); _status = "Waiting for native spacecraft assets.";
                _clientCrewAccess = message.CrewAccess; return;
            }
            _body.Transition(message.Flight.Region, message.Flight.Position, message.Flight.Rotation);
        }
        else if (_body.State.Region != message.Flight.Region
            || (_body.State.Position - message.Flight.Position).sqrMagnitude > 2500)
            _body.Transition(message.Flight.Region, message.Flight.Position, message.Flight.Rotation);
        _body.SetReplica(message.Flight, changedFrame);
        _frameRevision = message.FrameRevision;
        _spaceFrame = message.SpaceFrame;
        _spaceFrameTime = message.SpaceFrameTime;
        _spaceCruiseVelocity = message.SpaceCruiseVelocity;
        _stationAssist = message.StationAssist;
        ObserveStorage(message.StorageId, message.StorageAboard, message.StorageParkPosition, message.StorageParkRotation);
        if (changedFrame)
        {
            if (sameSpace && priorPosition.HasValue) _rider?.Rebase(message.Flight.Position - priorPosition.Value);
            UpdateOrbitalStation(teleport: true);
            Physics.SyncTransforms();
            _nextSeatStatus = 0;
        }
        _status = "Connected to the host's shared spacecraft.";
        _clientCrewAccess = message.CrewAccess;
    }

    private static bool SameSpaceFrame(OrbitalFrame? first, OrbitalFrame? second)
    {
        if (first is not { } left) return !second.HasValue;
        return second is { } right && left.Origin == right.Origin && left.Velocity == right.Velocity
            && left.Right == right.Right && left.Up == right.Up && left.Forward == right.Forward;
    }

    private bool ActorAtSeat(Peer peer) => _body != null && peer.Seat is >= 0 and <= 2
        && TryActor(peer, out var position, out _, out _)
        && (peer.Walking
            ? CabinActorCurrent(peer, position)
            : SeatedActorCurrent(peer, position));

    private enum Kind : byte { Hello = 1, Welcome, Command, Control, Snapshot, SeatStatus, Orbits, Station, Eva, FirefightingSnapshot, FirefightingBurst, Hull, Foam, Repair, Materials, Engineering, EngineeringWork, ConsolePose, ShipRepairInput, ShipRepairState, AttachmentRecovery, SurfaceRecovery, HelloConfirm }
    private sealed class Message
    {
        internal Kind Kind;
        internal Guid Nonce, Epoch, Token, Ship, Lease;
        internal long Sequence, Acknowledged;
        internal ActionCode Action;
        internal SpacecraftControls Controls;
        internal bool StationAssist;
        internal StationChunk? Station;
        internal HullChunk? Hull;
        internal FoamChunk? Foam;
        internal RepairChunk? Repair;
        internal MaterialsChunk? Materials;
        internal EngineeringChunk? Engineering;
        internal long EngineeringGesture;
        internal bool EngineeringActive;
        internal BoxStationWorkIntent? EngineeringIntent;
        internal long ShipRepairGesture;
        internal bool ShipRepairActive;
        internal int ShipRepairTool;
        internal SpacecraftManualRepairWork[]? ShipRepairs;
        internal int StationTarget = -1;
        internal ushort StationSetting;
        internal Vector3 StationAim;
        internal FirefightingChunk? Firefighting;
        internal BoxStationWaterBurst? FirefightingBurst;
        internal SpacecraftEvaSlotState[]? Eva;
        internal SpacecraftFlightState Flight;
        internal double FlightTime;
        internal bool Attached, Prepared, ForceReturn, CrewAccess;
        internal Vector3 RecoveryPoint;
        internal bool RecoveryWalking;
        internal long RecoverySnapshot;
        internal SpacecraftRegion SurfaceRegion;
        internal long SurfaceCompleted;
        internal Vector3 SurfacePoint;
        internal bool EntryPrepared;
        internal Vector3 EntryAnchor;
        internal int Seat = -1;
        internal ulong[] Actors = new ulong[3];
        internal bool[] Walking = new bool[3];
        internal SpacecraftCrewInteraction[] Interactions = new SpacecraftCrewInteraction[3];
        internal SpacecraftConsolePose[] ConsolePoses = new SpacecraftConsolePose[2];
        internal Vector3? CabinStandLocal;
        internal Vector3? Exit;
        internal ulong StorageId;
        internal bool StorageAboard;
        internal Vector3 StorageParkPosition;
        internal Quaternion StorageParkRotation = Quaternion.identity;
        internal string Result = "";
        internal bool OrbitRequested;
        internal bool HelloConfirmed;
        internal long OrbitTick;
        internal double OrbitPending;
        internal long FrameRevision;
        internal OrbitalFrame? SpaceFrame;
        internal double SpaceFrameTime;
        internal OrbitalVector SpaceCruiseVelocity;
        internal int OrbitOffset, OrbitTotal;
        internal byte[] OrbitCatalog = Array.Empty<byte>();
        internal OrbitalState[]? OrbitStates;
    }

    private sealed partial class SpacecraftEvent(ClientSpacecraftService service) : Packets.NetEvent
    {
        // Vessel snapshots and ten-body orbital chunks share the bounded transport.
        private const int Limit = 768;
        private static readonly UTF8Encoding Utf8 = new(false, true);
        // Schema version is checked before any payload enters the service.
        public override string Id => "modstoke.client.spacecraft.v61";
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
                if (BoltNetwork.isServer ? message.Kind is not (Kind.Hello or Kind.HelloConfirm or Kind.Command or Kind.Control or Kind.SeatStatus or Kind.EngineeringWork or Kind.ConsolePose or Kind.ShipRepairInput)
                    : message.Kind is not (Kind.Welcome or Kind.Snapshot or Kind.Orbits or Kind.Station or Kind.Eva
                        or Kind.FirefightingSnapshot or Kind.FirefightingBurst or Kind.Hull or Kind.Foam or Kind.Repair or Kind.Materials or Kind.Engineering or Kind.ShipRepairState or Kind.AttachmentRecovery or Kind.SurfaceRecovery)) return;
                try { service.Receive(message, fromConnection); }
                catch (Exception error) { service.Fail(error, ServiceOperation.Network); }
            }
            catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException or OverflowException)
            { /* Reject malformed packets before they reach native objects or authority. */ }
        }

        internal void Hello()
        {
            if (service._pendingHelloSequence == 0) service._pendingHelloSequence = ++service._helloSequence;
            Emit(null, service, static (writer, state) =>
            {
                Head(writer, Kind.Hello); GuidValue(writer, state._nonce);
                writer.Write(state._pendingHelloSequence); writer.Write(state._orbitView == null);
            });
        }

        internal void ConfirmHello() => Emit(null, service, static (writer, state) =>
        {
            Head(writer, Kind.HelloConfirm); GuidValue(writer, state._nonce);
            GuidValue(writer, state._helloEpoch); GuidValue(writer, state._helloToken);
            writer.Write(state._pendingHelloSequence);
        });

        internal void Orbits(Peer peer, long sequence, int offset) =>
            Emit(peer, (Event: this, Owner: service, Peer: peer, Sequence: sequence, Offset: offset), static (writer, state) =>
        {
            var owner = state.Owner;
            var simulation = owner._orbits!;
            Head(writer, Kind.Orbits); state.Event.Envelope(writer, state.Peer);
            writer.Write(state.Sequence); writer.Write(simulation.Tick);
            writer.Write(simulation.PendingSeconds);
            writer.Write(OrbitalCatalog.Current.Fingerprint);
            writer.Write((ushort)simulation.Count); writer.Write((ushort)state.Offset);
            int count = Math.Min(OrbitChunkSize, simulation.Count - state.Offset);
            writer.Write((byte)count);
            for (int i = 0; i < count; i++)
            {
                var body = simulation.State(state.Offset + i);
                writer.Write(body.Id);
                OrbitVector(writer, body.Position); OrbitVector(writer, body.Velocity);
            }
        });
        internal void Welcome(Peer peer, Guid nonce, Guid token, long sequence, bool confirmed) =>
            Emit(peer, (Epoch: service._epoch, Nonce: nonce, Token: token, Sequence: sequence, Confirmed: confirmed), static (writer, state) =>
        {
            Head(writer, Kind.Welcome); GuidValue(writer, state.Nonce);
            GuidValue(writer, state.Epoch); GuidValue(writer, state.Token);
            writer.Write(state.Sequence); writer.Write(state.Confirmed);
        });
        internal void Request(ActionCode action, long sequence, bool prepared, int stationTarget = -1,
            ushort stationSetting = 0, Vector3 stationAim = default, Guid stationLease = default) =>
            Emit(null, (Event: this, Action: action, Sequence: sequence, Prepared: prepared,
                Target: stationTarget, Setting: stationSetting, Aim: stationAim, Lease: stationLease), static (writer, state) =>
        {
            Head(writer, Kind.Command); state.Event.Envelope(writer, null);
            writer.Write(state.Sequence); writer.Write((byte)state.Action); writer.Write(state.Prepared);
            if (state.Action == ActionCode.StationShoreAir)
            {
                if (!ValidStationShoreAir(state.Setting) || state.Lease == Guid.Empty) throw new InvalidDataException();
                GuidValue(writer, state.Lease); writer.Write(checked((byte)state.Setting));
                return;
            }
            if (state.Action == ActionCode.StationShoreFuel)
            {
                if (!ValidStationShoreFuel(state.Setting) || state.Lease == Guid.Empty) throw new InvalidDataException();
                GuidValue(writer, state.Lease); writer.Write(checked((byte)state.Setting));
                return;
            }
            if (state.Action == ActionCode.StationShoreCoolant)
            {
                if (!ValidStationShoreCoolant(state.Setting) || state.Lease == Guid.Empty) throw new InvalidDataException();
                GuidValue(writer, state.Lease); writer.Write(checked((byte)state.Setting));
                return;
            }
            if (state.Action is ActionCode.StationCable or ActionCode.StationFloor)
            {
                bool valid = state.Action == ActionCode.StationCable
                    ? ValidStationCable(state.Target, state.Setting, state.Aim)
                    : ValidStationFloor(state.Target, state.Setting, state.Aim);
                if (!valid || state.Lease == Guid.Empty) throw new InvalidDataException();
                GuidValue(writer, state.Lease);
                writer.Write(checked((ushort)state.Target)); writer.Write(checked((byte)state.Setting)); Vector(writer, state.Aim);
                return;
            }
            if (state.Action == ActionCode.StationEngineering)
            {
                if (!ValidStationEngineering(state.Target, state.Setting, state.Aim) || state.Lease == Guid.Empty)
                    throw new InvalidDataException();
                GuidValue(writer, state.Lease);
                writer.Write(state.Target); writer.Write(checked((byte)state.Setting));
                return;
            }
            if (state.Action == ActionCode.StationMaterials)
            {
                if (!ValidStationMaterials(state.Target, state.Setting, state.Aim)) throw new InvalidDataException();
                if (state.Lease == Guid.Empty) throw new InvalidDataException();
                GuidValue(writer, state.Lease);
                writer.Write(state.Target); writer.Write(state.Setting); Vector(writer, state.Aim);
                return;
            }
            if (state.Action == ActionCode.StationRepair)
            {
                if (!ValidStationRepair(state.Target, state.Setting, state.Aim)) throw new InvalidDataException();
                if (state.Lease == Guid.Empty) throw new InvalidDataException();
                GuidValue(writer, state.Lease);
                writer.Write(state.Target); writer.Write(state.Setting); Vector(writer, state.Aim);
                return;
            }
            if (state.Action == ActionCode.StationFoam)
            {
                if (!ValidStationFoam(state.Target, state.Setting, state.Aim)) throw new InvalidDataException();
                if (state.Lease == Guid.Empty) throw new InvalidDataException();
                GuidValue(writer, state.Lease);
                writer.Write(state.Target); writer.Write(state.Setting); Vector(writer, state.Aim);
                return;
            }
            if (state.Action == ActionCode.StationFirefighting)
            {
                if (!ValidStationFirefighting(state.Target, state.Setting, state.Aim)) throw new InvalidDataException();
                if (state.Lease == Guid.Empty) throw new InvalidDataException();
                GuidValue(writer, state.Lease);
                writer.Write(checked((ushort)state.Target)); writer.Write(state.Setting); Vector(writer, state.Aim);
                return;
            }
            if (state.Action == ActionCode.StationDoor)
            {
                var layout = BoxStationLayout.Current;
                if (state.Target < 0 || state.Target >= layout.Doors.Length
                    && !BoxStationDoors.ValidEdge(layout, state.Target - layout.Doors.Length)) throw new InvalidDataException();
                writer.Write(state.Target);
                return;
            }
            if (state.Action is ActionCode.StationAirMode or ActionCode.StationRoomThermal or ActionCode.StationFireAlarm or ActionCode.StationSmes
                || IsStationPlumbing(state.Action) || IsStationPower(state.Action))
                writer.Write(checked((ushort)state.Target));
            if (IsStationPlumbingSetting(state.Action)
                || state.Action is ActionCode.StationAirMode or ActionCode.StationRoomThermal or ActionCode.StationFireAlarm or ActionCode.StationSmes) writer.Write(state.Setting);
        });
        internal void Control(long sequence, Guid lease, SpacecraftControls input) =>
            Emit(null, (Event: this, Sequence: sequence, Lease: lease, Input: input), static (writer, state) =>
        {
            var (sender, sequence, lease, input) = state;
            Head(writer, Kind.Control); sender.Envelope(writer, null); writer.Write(sequence); GuidValue(writer, lease);
            Vector(writer, input.Translation); Vector(writer, input.Rotation);
            writer.Write(input.Powered); writer.Write(input.Dampeners); writer.Write(input.Boost);
            writer.Write(input.DoorOpen); writer.Write((byte)input.ConsoleControl); writer.Write(input.ConsoleReach);
            writer.Write(input.GearDeployed);
        });
        internal void SeatStatus(long sequence, Guid lease, bool attached, bool prepared) =>
            Emit(null, (Event: this, Sequence: sequence, Lease: lease, Attached: attached, Prepared: prepared,
                Frame: service._frameRevision), static (writer, state) =>
        {
            var (sender, sequence, lease, attached, prepared, frame) = state;
            Head(writer, Kind.SeatStatus); sender.Envelope(writer, null); writer.Write(sequence);
            GuidValue(writer, lease); writer.Write(attached); writer.Write(prepared);
            writer.Write(frame);
        });
        internal void ConsolePose(long sequence, Guid lease, SpacecraftControls input) =>
            Emit(null, (Event: this, Sequence: sequence, Lease: lease, Input: input), static (writer, state) =>
        {
            var (sender, sequence, lease, input) = state;
            Head(writer, Kind.ConsolePose); sender.Envelope(writer, null); writer.Write(sequence); GuidValue(writer, lease);
            writer.Write((byte)input.ConsoleControl); writer.Write(input.ConsoleReach);
        });
        internal void AttachmentRecovery(Peer peer, long sequence, Vector3 localPoint, bool walking) =>
            Emit(peer, (Event: this, Service: service, Peer: peer, Sequence: sequence, Point: localPoint, Walking: walking), static (writer, state) =>
        {
            var (sender, owner, peer, sequence, point, walking) = state;
            Head(writer, Kind.AttachmentRecovery); sender.Envelope(writer, peer); writer.Write(sequence);
            GuidValue(writer, owner._shipId); GuidValue(writer, peer.Lease); writer.Write(owner._frameRevision);
            writer.Write(owner._snapshotSequence);
            Vector(writer, point); writer.Write(walking);
        });
        internal void SurfaceRecovery(Peer peer, long sequence, Guid lease, SpacecraftRegion region, Vector3 point) =>
            Emit(peer, (Event: this, Service: service, Peer: peer, Sequence: sequence, Lease: lease, Region: region, Point: point), static (writer, state) =>
        {
            var (sender, owner, peer, sequence, lease, region, point) = state;
            Head(writer, Kind.SurfaceRecovery); sender.Envelope(writer, peer); writer.Write(sequence);
            GuidValue(writer, owner._shipId); GuidValue(writer, lease);
            writer.Write((byte)region); Vector(writer, point);
        });
        internal void Snapshot(Peer peer, long sequence) =>
            Emit(peer, (Event: this, Service: service, Peer: peer, Sequence: sequence), static (writer, state) =>
        {
            var (sender, owner, peer, sequence) = state;
            Head(writer, Kind.Snapshot); sender.Envelope(writer, peer); writer.Write(sequence);
            writer.Write(peer.LastCommand); Text(writer, peer.Result);
            Vector3? entryAnchor = peer.EntryPrepared && owner._movement.TryGetValue(peer, out var entry)
                && entry.Active && !entry.ReturnIssued && owner.CurrentMovement(peer, entry)
                ? entry.IslandPosition : null;
            writer.Write(entryAnchor.HasValue);
            if (entryAnchor is { } anchor) Vector(writer, anchor);
            GuidValue(writer, owner._body == null ? Guid.Empty : owner._shipId);
            writer.Write((sbyte)peer.Seat); GuidValue(writer, peer.Lease); writer.Write(peer.ForceReturn);
            writer.Write(owner.HasCrewAccess(peer));
            for (var i = 0; i < 3; i++) writer.Write(owner._actorIds[i]);
            for (var i = 0; i < 3; i++) writer.Write(owner._walking[i]);
            for (var i = 0; i < 3; i++)
            {
                var interaction = owner._interactions[i];
                // Actor identity already belongs to this snapshot's berth. Compact
                // cosmetic poses keep the worst-case snapshot inside 768 bytes.
                if (!interaction.Active || interaction.Actor == 0 || interaction.Actor != owner._actorIds[i] || !owner._walking[i])
                { writer.Write((byte)0); continue; }
                var oriented = interaction.LocalRotation.HasValue;
                writer.Write((byte)(1 | (oriented ? 2 : 0) | (oriented && interaction.Press ? 4 : 0)));
                Vector(writer, interaction.LocalTarget);
                writer.Write((byte)Mathf.Clamp(Mathf.RoundToInt(interaction.Reach * 255), 1, 255));
                if (oriented) ContactRotation(writer, interaction.LocalRotation!.Value);
            }
            var cabinStand = peer.Seat >= 0 && owner._walking[peer.Seat] ? peer.CabinStandLocal : null;
            writer.Write(cabinStand.HasValue);
            if (cabinStand.HasValue) Vector(writer, cabinStand.Value);
            writer.Write(peer.Exit.HasValue);
            if (peer.Exit.HasValue)
            {
                Vector(writer, peer.Exit.Value);
                writer.Write(owner._movement.TryGetValue(peer, out var movement) ? movement.SurfaceCompleted : 0L);
            }
            writer.Write(owner._storage?.NetworkId ?? 0);
            writer.Write(owner._body != null && owner._storage?.Aboard == true);
            Vector(writer, owner._storage?.ParkPosition ?? Vector3.zero);
            Rotation(writer, owner._storage?.ParkRotation ?? Quaternion.identity);
            if (owner._body == null) return;
            var value = owner._body.State;
            if (owner._spaceFrame.HasValue != (value.Region == SpacecraftRegion.Space)
                || !double.IsFinite(owner._spaceFrameTime) || owner._spaceFrameTime < 0
                || owner._spaceFrameTime > OrbitalSimulation.MaximumTick
                || !owner._spaceCruiseVelocity.IsFinite
                || owner._spaceCruiseVelocity.LengthSquared > OrbitalSimulation.MaximumSpeed * OrbitalSimulation.MaximumSpeed
                || !owner._spaceFrame.HasValue && (owner._spaceFrameTime != 0 || owner._spaceCruiseVelocity != default))
                throw new InvalidDataException();
            writer.Write(owner._frameRevision); writer.Write(owner._spaceFrame.HasValue);
            if (owner._spaceFrame is { } frame)
            {
                if (frame.Acceleration != default || frame.AngularVelocity != default) throw new InvalidDataException();
                OrbitVector(writer, frame.Origin); OrbitVector(writer, frame.Velocity);
                OrbitVector(writer, frame.Right); OrbitVector(writer, frame.Up); OrbitVector(writer, frame.Forward);
                OrbitVector(writer, owner._spaceCruiseVelocity);
                writer.Write(owner._spaceFrameTime);
            }
            writer.Write(owner._orbits!.SampleTime);
            Vector(writer, value.Position); Rotation(writer, value.Rotation);
            Vector(writer, value.Velocity); Vector(writer, value.AngularVelocity);
            writer.Write(value.Powered); writer.Write(value.Grounded); writer.Write(value.Dampeners);
            writer.Write((byte)value.Region); writer.Write((byte)value.Travel); writer.Write(value.TravelProgress);
            writer.Write(value.DoorOpen); writer.Write(value.DoorAmount);
            for (var seat = 0; seat < 2; seat++)
            {
                var pose = owner.CurrentConsolePose(seat);
                writer.Write((byte)(pose.Active ? pose.Control : SpacecraftConsoleControl.None));
                writer.Write(pose.Active ? (byte)Mathf.Clamp(Mathf.RoundToInt(pose.Reach * 255), 1, 255) : (byte)0);
            }
            writer.Write(value.GearDeployed); writer.Write(value.GearAmount);
            writer.Write(Range(value.InnerDoorAmount, 0, 1)); writer.Write(Range(value.CabinDoorAmount, 0, 1));
            Vector(writer, value.TranslationInput); Vector(writer, value.RotationInput); writer.Write(value.BoostActive);
            Systems(writer, value.Systems);
            writer.Write(value.Docked); writer.Write(owner._stationAssist);
        });

        private void Emit<TState>(Peer? peer, TState state, Action<BinaryWriter, TState> write)
        {
            if (!service._started || !BoltNetwork.isRunning || peer != null && !service.PeerCurrent(peer)) return;
            if (peer == null && (!service.IsClient || service._serverPointer == IntPtr.Zero)) return;
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Utf8, true)) write(writer, state);
            if (stream.Length > Limit) throw new InvalidOperationException("Spacecraft packet exceeded its transport budget.");
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
        private static void Head(BinaryWriter writer, Kind kind) { writer.Write((byte)78); writer.Write((byte)kind); }
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
            // Asset failures can include native diagnostic text. Keep complete UTF-8
            // characters within the packet budget without breaking service cleanup.
            // Keep room for system state and the completed surface correction.
            Span<byte> bytes = stackalloc byte[61];
            Utf8.GetEncoder().Convert(value.AsSpan(), bytes, true, out _, out var used, out _);
            writer.Write((byte)used); writer.Write(bytes[..used]);
        }
        private static string Text(BinaryReader reader)
        {
            var size = reader.ReadByte(); if (size > 61) throw new InvalidDataException();
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
        private static void OrbitVector(BinaryWriter writer, OrbitalVector value)
        { writer.Write(value.X); writer.Write(value.Y); writer.Write(value.Z); }
        private static OrbitalVector OrbitVector(BinaryReader reader, double maximum)
        {
            var value = new OrbitalVector(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble());
            return value.IsFinite && value.LengthSquared <= maximum * maximum
                ? value : throw new InvalidDataException("Invalid orbital vector.");
        }
        private static void Rotation(BinaryWriter writer, Quaternion value)
        { writer.Write(value.x); writer.Write(value.y); writer.Write(value.z); writer.Write(value.w); }
        private static Quaternion Rotation(BinaryReader reader) => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        private static void ContactRotation(BinaryWriter writer, Quaternion value)
        {
            if (!ValidRotation(value)) throw new InvalidDataException("Invalid crew contact rotation.");
            value = value.normalized;
            writer.Write((short)Mathf.RoundToInt(value.x * 32767));
            writer.Write((short)Mathf.RoundToInt(value.y * 32767));
            writer.Write((short)Mathf.RoundToInt(value.z * 32767));
            writer.Write((short)Mathf.RoundToInt(value.w * 32767));
        }
        private static Quaternion ContactRotation(BinaryReader reader)
        {
            var value = new Quaternion(reader.ReadInt16() / 32767f, reader.ReadInt16() / 32767f,
                reader.ReadInt16() / 32767f, reader.ReadInt16() / 32767f);
            var length = value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w;
            if (length is < .9999f or > 1.0001f) throw new InvalidDataException("Invalid crew contact rotation.");
            return value.normalized;
        }
        private static bool Boolean(BinaryReader reader) => reader.ReadByte() switch
        { 0 => false, 1 => true, _ => throw new InvalidDataException() };
        private static bool Axis(float value) => float.IsFinite(value) && value >= -1 && value <= 1;
        private static bool Axes(Vector3 value) => Axis(value.x) && Axis(value.y) && Axis(value.z);
        private static bool CabinPoint(Vector3 value) => Finite(value) && value.x is >= -4 and <= 4
            && value.y is >= -3 and <= 2.5f && value.z is >= -14.7f and <= 9.4f;
        private static bool InteractionPoint(Vector3 value) => Finite(value) && value.x is >= -5 and <= 5
            && value.y is >= -3 and <= 3 && value.z is >= -15 and <= 10;
        private static bool StationInteractionPoint(Vector3 value)
        {
            if (!Finite(value)) return false;
            var station = BoxStationDock.Position + BoxStationDock.Rotation * value;
            const float halfWidth = 255 * BoxStationLayout.TileSize * .5f;
            return MathF.Abs(station.x) <= halfWidth && MathF.Abs(station.z) <= halfWidth
                && station.y is >= 0 and <= BoxStationLayout.DeckHeight;
        }

        private static void Systems(BinaryWriter writer, SpacecraftSystemState value)
        {
            writer.Write(value.CabinPressure); writer.Write(value.OxygenFraction); writer.Write(value.CabinTemperatureC);
            writer.Write(value.OxygenReserve01); writer.Write(value.Fuel01); writer.Write(value.Battery01);
            ushort flags = (ushort)((value.ScrubberEnabled ? 1 : 0) | (value.GeneratorEnabled ? 2 : 0)
                | (value.InnerDoorOpen ? 4 : 0) | (value.EmergencyOverride ? 8 : 0)
                | (value.Ready ? 16 : 0) | (value.AirlockVenting ? 32 : 0)
                | (value.CabinDoorOpen ? 64 : 0) | (value.CrewLocked ? 128 : 0)
                | (value.ApuIsolated ? 256 : 0) | (value.ApuGuardOpen ? 512 : 0) | (value.BusIsolated ? 1024 : 0)
                | (value.WarpDischargeEnabled ? 2048 : 0) | (value.Thermal.EnvironmentAvailable ? 4096 : 0));
            writer.Write(flags);
            writer.Write(value.CO2Fraction); writer.Write(value.AirlockPressure); writer.Write(value.WarpCharge01);
            writer.Write(value.AirlockOxygenFraction); writer.Write(value.AirlockCO2Fraction);
            writer.Write(value.AirlockTemperatureC); writer.Write((byte)value.SelectedDestination);
            // Round down so a damaged section never appears fully repaired.
            writer.Write((ushort)MathF.Floor(Range(value.Hull.Cabin, 0, 1) * 65535));
            writer.Write((ushort)MathF.Floor(Range(value.Hull.Airlock, 0, 1) * 65535));
            writer.Write((ushort)MathF.Floor(Range(value.Hull.Entry, 0, 1) * 65535));
            writer.Write(value.EngineIntegrity01); writer.Write(value.PowerIntegrity01);
            writer.Write(value.LifeSupportIntegrity01); writer.Write(value.WarpIntegrity01);
            if (value.ValveMask > 0x0fff) throw new InvalidDataException();
            writer.Write(value.ValveMask); writer.Write(Range(value.Coolant01, 0, 1));
            writer.Write(Range(value.AirFeedPressure, 0, 250)); writer.Write(Range(value.FuelFeedPressure, 0, 180));
            writer.Write(Range(value.CoolantPressure, 0, 160));
            writer.Write(Range(value.EntryPressure, 0, 250)); writer.Write(Range(value.EntryOxygenFraction, 0, 1));
            writer.Write(Range(value.EntryCO2Fraction, 0, 1)); writer.Write(Range(value.EntryTemperatureC, -100, 100));
            if (!AirlockStateValid(value.AirlockCycle, value.AirlockTarget, value.AirlockFault))
                throw new InvalidDataException();
            writer.Write((byte)value.AirlockCycle); writer.Write((byte)value.AirlockTarget); writer.Write((byte)value.AirlockFault);
            var electrical = value.Electrical;
            if (!SpacecraftElectrical.IsValid(electrical)) throw new InvalidDataException();
            writer.Write(electrical.ClosedMask); writer.Write(electrical.TrippedMask);
            writer.Write(electrical.PoweredMask); writer.Write(electrical.ShedMask);
            writer.Write(electrical.LoadKw); writer.Write(electrical.GenerationKw); writer.Write(electrical.BatteryKw);
            writer.Write(electrical.ShoreKw);
            writer.Write((byte)MathF.Floor(Range(value.ShoreCoupling01, 0, 1) * 255));
            if (!ShoreAirStateValid(value.ShoreAirEnabled, value.ShoreAirStatus,
                value.ShoreAirMolesPerSecond, value.ShoreAirWatts)) throw new InvalidDataException();
            writer.Write((byte)((value.ShoreAirEnabled ? 0x80 : 0) | (byte)value.ShoreAirStatus));
            writer.Write(value.ShoreAirMolesPerSecond); writer.Write(value.ShoreAirWatts);
            if (value.LineFaultMask > 0x0fff) throw new InvalidDataException();
            writer.Write((ushort)(value.LineFaultMask | (value.CoolantLeak ? 0x1000 : 0)));
            // Do not expose a fully open guard to clients before its host hinge reaches the stop.
            writer.Write((byte)MathF.Floor(Range(value.ApuGuardAmount, 0, 1) * 255));
            if (value.CircuitSpares > ClientSpacecraftSystems.CircuitSparesCapacity
                || value.LineSpares > ClientSpacecraftSystems.LineSparesCapacity
                || value.HullPlates > ClientSpacecraftSystems.HullPlatesCapacity) throw new InvalidDataException();
            writer.Write(value.CircuitSpares); writer.Write(value.LineSpares); writer.Write(value.HullPlates);
            if (!ShoreFuelStateValid(value.ShoreFuelEnabled, value.ShoreFuelStatus)
                || !ShoreCoolantStateValid(value.ShoreCoolantEnabled, value.ShoreCoolantStatus)) throw new InvalidDataException();
            // Both services share one byte to preserve the full snapshot's transport budget.
            writer.Write((byte)((value.ShoreFuelEnabled ? 0x08 : 0) | (byte)value.ShoreFuelStatus
                | (value.ShoreCoolantEnabled ? 0x80 : 0) | (byte)value.ShoreCoolantStatus << 4));
            WriteSealant(writer, value.Sealant);
            WriteTemperature(writer, value.Thermal.CabinHullKelvin);
            WriteTemperature(writer, value.Thermal.AirlockHullKelvin);
            WriteTemperature(writer, value.Thermal.EntryHullKelvin);
            WriteTemperature(writer, value.Thermal.CoolantKelvin);
            WriteFireZone(writer, value.Fire.Cabin, 250000);
            WriteFireZone(writer, value.Fire.Airlock, 40000);
            WriteFireZone(writer, value.Fire.Entry, 80000);
        }

        private static void WriteFireZone(BinaryWriter writer, SpacecraftFireZone value, float maximumWatts)
        {
            writer.Write((ushort)MathF.Round(Range(value.Fuel01, 0, 1) * 65535));
            WriteTemperature(writer, value.HotspotKelvin);
            float watts = Range(value.PowerWatts, 0, maximumWatts);
            // Preserve active flames even when their power is below one quantization step.
            writer.Write(watts > 0 ? (byte)MathF.Max(1, MathF.Round(watts / maximumWatts * 255)) : (byte)0);
        }

        private static SpacecraftFireZone ReadFireZone(BinaryReader reader, float maximumWatts)
            => new(reader.ReadUInt16() / 65535f, ReadTemperature(reader), reader.ReadByte() / 255f * maximumWatts);

        private static void WriteTemperature(BinaryWriter writer, float kelvin)
            => writer.Write((ushort)MathF.Round((Range(kelvin, 2.7f, 10000) - 2.7f) * (65535f / 9997.3f)));

        private static float ReadTemperature(BinaryReader reader) => Math.Clamp(2.7f + reader.ReadUInt16() * (9997.3f / 65535), 2.7f, 10000);

        private static void WriteSealant(BinaryWriter writer, SpacecraftSealantState value)
        {
            if (!ClientSpacecraftSystems.ValidSealantState(value)) throw new InvalidDataException();
            // Round up: a small active leak or unfinished cycle must not appear cleared.
            writer.Write((ushort)MathF.Ceiling(value.CabinLeak * (65535f / 2)));
            writer.Write((ushort)MathF.Ceiling(value.AirlockLeak * (65535f / 2)));
            writer.Write((ushort)MathF.Ceiling(value.EntryLeak * (65535f / 2)));
            writer.Write((byte)MathF.Ceiling(value.CabinSeconds * (255f / ClientSpacecraftSystems.SealantCycleSeconds)));
            writer.Write((byte)MathF.Ceiling(value.AirlockSeconds * (255f / ClientSpacecraftSystems.SealantCycleSeconds)));
            writer.Write((byte)MathF.Ceiling(value.EntrySeconds * (255f / ClientSpacecraftSystems.SealantCycleSeconds)));
        }

        private static SpacecraftSealantState ReadSealant(BinaryReader reader) => new(
            reader.ReadUInt16() / 65535f * 2, reader.ReadUInt16() / 65535f * 2, reader.ReadUInt16() / 65535f * 2,
            reader.ReadByte() / 255f * ClientSpacecraftSystems.SealantCycleSeconds,
            reader.ReadByte() / 255f * ClientSpacecraftSystems.SealantCycleSeconds,
            reader.ReadByte() / 255f * ClientSpacecraftSystems.SealantCycleSeconds);

        private static SpacecraftSystemState Systems(BinaryReader reader)
        {
            var pressure = Range(reader, 0, 250); var oxygen = Range(reader, 0, 1);
            var temperature = Range(reader, -100, 100);
            var reserve = Range(reader, 0, 1); var fuel = Range(reader, 0, 1); var battery = Range(reader, 0, 1);
            var flags = reader.ReadUInt16();
            if ((flags & ~0x1fff) != 0) throw new InvalidDataException();
            var scrubber = (flags & 1) != 0; var generator = (flags & 2) != 0;
            var innerDoor = (flags & 4) != 0; var emergencyOverride = (flags & 8) != 0;
            var ready = (flags & 16) != 0; var venting = (flags & 32) != 0;
            var cabinDoorOpen = (flags & 64) != 0; var crewLocked = (flags & 128) != 0;
            var apuIsolated = (flags & 256) != 0; var apuGuardOpen = (flags & 512) != 0;
            var busIsolated = (flags & 1024) != 0; var warpDischargeEnabled = (flags & 2048) != 0;
            var co2 = Range(reader, 0, 1); var airlockPressure = Range(reader, 0, 250); var warp = Range(reader, 0, 1);
            var airlockOxygen = Range(reader, 0, 1); var airlockCo2 = Range(reader, 0, 1);
            var airlockTemperature = Range(reader, -100, 100);
            var destination = (SpacecraftRegion)reader.ReadByte();
            var hull = new SpacecraftHullState(reader.ReadUInt16() / 65535f,
                reader.ReadUInt16() / 65535f, reader.ReadUInt16() / 65535f);
            var engineIntegrity = Range(reader, 0, 1);
            var powerIntegrity = Range(reader, 0, 1); var lifeSupportIntegrity = Range(reader, 0, 1);
            var warpIntegrity = Range(reader, 0, 1);
            var valveMask = reader.ReadUInt16(); var coolant = Range(reader, 0, 1);
            var airFeedPressure = Range(reader, 0, 250); var fuelFeedPressure = Range(reader, 0, 180);
            var coolantPressure = Range(reader, 0, 160);
            var entryPressure = Range(reader, 0, 250); var entryOxygen = Range(reader, 0, 1);
            var entryCo2 = Range(reader, 0, 1); var entryTemperature = Range(reader, -100, 100);
            var cycle = (SpacecraftAirlockPhase)reader.ReadByte();
            var target = (SpacecraftAirlockTarget)reader.ReadByte();
            var fault = (SpacecraftAirlockFault)reader.ReadByte();
            var electrical = new SpacecraftElectricalState(reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte(),
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            var shoreCoupling = reader.ReadByte() / 255f;
            var shoreAir = reader.ReadByte();
            if ((shoreAir & 0x78) != 0) throw new InvalidDataException();
            var shoreAirEnabled = (shoreAir & 0x80) != 0;
            var shoreAirStatus = (ShoreAirStatus)(shoreAir & 7);
            var shoreAirMolesPerSecond = reader.ReadSingle(); var shoreAirWatts = reader.ReadUInt16();
            if (!ShoreAirStateValid(shoreAirEnabled, shoreAirStatus, shoreAirMolesPerSecond, shoreAirWatts))
                throw new InvalidDataException();
            var lineFaultFlags = reader.ReadUInt16();
            if ((lineFaultFlags & 0xe000) != 0) throw new InvalidDataException();
            var lineFaultMask = (ushort)(lineFaultFlags & 0x0fff);
            var coolantLeak = (lineFaultFlags & 0x1000) != 0;
            var apuGuardAmount = reader.ReadByte() / 255f;
            var circuitSpares = reader.ReadByte(); var lineSpares = reader.ReadByte();
            var hullPlates = reader.ReadByte();
            var shoreServices = reader.ReadByte();
            var shoreFuelEnabled = (shoreServices & 0x08) != 0;
            var shoreFuelStatus = (ShoreFuelStatus)(shoreServices & 7);
            var shoreCoolantEnabled = (shoreServices & 0x80) != 0;
            var shoreCoolantStatus = (ShoreCoolantStatus)((shoreServices >> 4) & 7);
            if (!ShoreFuelStateValid(shoreFuelEnabled, shoreFuelStatus)
                || !ShoreCoolantStateValid(shoreCoolantEnabled, shoreCoolantStatus)) throw new InvalidDataException();
            var sealant = ReadSealant(reader);
            var thermal = new SpacecraftThermalState(ReadTemperature(reader), ReadTemperature(reader),
                ReadTemperature(reader), ReadTemperature(reader), (flags & 4096) != 0);
            var fire = new SpacecraftFireState(ReadFireZone(reader, 250000), ReadFireZone(reader, 40000),
                ReadFireZone(reader, 80000));
            if (oxygen + co2 > 1.0001f || airlockOxygen + airlockCo2 > 1.0001f
                || entryOxygen + entryCo2 > 1.0001f
                || !AirlockStateValid(cycle, target, fault)
                || !SpacecraftElectrical.IsValid(electrical)
                || busIsolated && generator
                || warpDischargeEnabled
                    && ((electrical.ClosedMask & (1 << (int)SpacecraftCircuit.Warp)) != 0
                        || (valveMask & (1 << (int)SpacecraftValve.WarpFuel)) != 0)
                || valveMask > 0x0fff
                || circuitSpares > ClientSpacecraftSystems.CircuitSparesCapacity
                || lineSpares > ClientSpacecraftSystems.LineSparesCapacity
                || hullPlates > ClientSpacecraftSystems.HullPlatesCapacity
                || destination is not (SpacecraftRegion.Island or SpacecraftRegion.TestWorld or SpacecraftRegion.Moon))
                throw new InvalidDataException();
            return new(pressure, oxygen, temperature, reserve, fuel, battery, scrubber, generator,
                innerDoor, emergencyOverride, co2, airlockPressure, warp, ready, venting,
                airlockOxygen, airlockCo2, airlockTemperature, destination,
                Hull: hull, EngineIntegrity01: engineIntegrity, PowerIntegrity01: powerIntegrity,
                LifeSupportIntegrity01: lifeSupportIntegrity, WarpIntegrity01: warpIntegrity,
                CabinDoorOpen: cabinDoorOpen, CrewLocked: crewLocked,
                ValveMask: valveMask, Coolant01: coolant, ApuIsolated: apuIsolated,
                AirFeedPressure: airFeedPressure, FuelFeedPressure: fuelFeedPressure, CoolantPressure: coolantPressure,
                EntryPressure: entryPressure, EntryOxygenFraction: entryOxygen,
                EntryCO2Fraction: entryCo2, EntryTemperatureC: entryTemperature,
                AirlockCycle: cycle, AirlockTarget: target, AirlockFault: fault, Electrical: electrical,
                LineFaultMask: lineFaultMask, ApuGuardOpen: apuGuardOpen, ApuGuardAmount: apuGuardAmount,
                CircuitSpares: circuitSpares, LineSpares: lineSpares, ShoreCoupling01: shoreCoupling,
                ShoreAirEnabled: shoreAirEnabled, ShoreAirStatus: shoreAirStatus,
                ShoreAirMolesPerSecond: shoreAirMolesPerSecond, ShoreAirWatts: shoreAirWatts,
                ShoreFuelEnabled: shoreFuelEnabled, ShoreFuelStatus: shoreFuelStatus,
                ShoreCoolantEnabled: shoreCoolantEnabled, ShoreCoolantStatus: shoreCoolantStatus,
                CoolantLeak: coolantLeak, Sealant: sealant, BusIsolated: busIsolated, HullPlates: hullPlates,
                WarpDischargeEnabled: warpDischargeEnabled, Thermal: thermal, Fire: fire);
        }

        private static bool ShoreFuelStateValid(bool enabled, ShoreFuelStatus status) => status switch
        {
            ShoreFuelStatus.Waiting or ShoreFuelStatus.Filling or ShoreFuelStatus.NoPower => enabled,
            ShoreFuelStatus.Off or ShoreFuelStatus.Limit or ShoreFuelStatus.NoSupply or ShoreFuelStatus.Unsafe => !enabled,
            _ => false
        };

        private static bool ShoreCoolantStateValid(bool enabled, ShoreCoolantStatus status) => status switch
        {
            ShoreCoolantStatus.Waiting or ShoreCoolantStatus.Filling or ShoreCoolantStatus.NoPower => enabled,
            ShoreCoolantStatus.Off or ShoreCoolantStatus.Limit or ShoreCoolantStatus.NoSupply or ShoreCoolantStatus.Unsafe => !enabled,
            _ => false
        };

        private static bool ShoreAirStateValid(bool enabled, ShoreAirStatus status, float molesPerSecond, ushort watts) =>
            float.IsFinite(molesPerSecond) && molesPerSecond is >= 0 and <= SpacecraftShoreAir.MaximumMolesPerSecond
                && watts <= SpacecraftShoreAir.MaximumWatts
                && (status switch
                {
                    ShoreAirStatus.Filling => enabled,
                    ShoreAirStatus.Full => !enabled,
                    ShoreAirStatus.Off or ShoreAirStatus.UnsafeGas => !enabled && molesPerSecond == 0,
                    ShoreAirStatus.Waiting or ShoreAirStatus.NoSupply or ShoreAirStatus.NoPower or ShoreAirStatus.Cooling =>
                        enabled && molesPerSecond == 0,
                    _ => false
                });

        private static bool AirlockStateValid(SpacecraftAirlockPhase phase, SpacecraftAirlockTarget target,
            SpacecraftAirlockFault fault) => phase is >= SpacecraftAirlockPhase.Idle and <= SpacecraftAirlockPhase.Fault
                && target is >= SpacecraftAirlockTarget.None and <= SpacecraftAirlockTarget.Entry
                && fault is >= SpacecraftAirlockFault.None and <= SpacecraftAirlockFault.PressureTimeout
                && (phase == SpacecraftAirlockPhase.Idle ? target == SpacecraftAirlockTarget.None : target != SpacecraftAirlockTarget.None)
                && (phase == SpacecraftAirlockPhase.Fault ? fault != SpacecraftAirlockFault.None : fault == SpacecraftAirlockFault.None);

        private static float Range(BinaryReader reader, float minimum, float maximum) =>
            Range(reader.ReadSingle(), minimum, maximum);

        private static float Range(float value, float minimum, float maximum)
        {
            return float.IsFinite(value) && value >= minimum && value <= maximum
                ? value : throw new InvalidDataException();
        }

        private static Message? Parse(BinaryReader reader)
        {
            if (reader.ReadByte() != 78) return null;
            var value = new Message { Kind = (Kind)reader.ReadByte() };
            if (value.Kind == Kind.Hello)
            {
                value.Nonce = GuidValue(reader); value.Sequence = reader.ReadInt64(); value.OrbitRequested = Boolean(reader);
                return value.Nonce != Guid.Empty && value.Sequence > 0 ? value : null;
            }
            if (value.Kind is Kind.Welcome or Kind.HelloConfirm) value.Nonce = GuidValue(reader);
            value.Epoch = GuidValue(reader); value.Token = GuidValue(reader);
            if (value.Epoch == Guid.Empty || value.Token == Guid.Empty) return null;
            value.Sequence = reader.ReadInt64(); if (value.Sequence <= 0) return null;
            if (value.Kind is Kind.Welcome or Kind.HelloConfirm)
            {
                if (value.Kind == Kind.Welcome) value.HelloConfirmed = Boolean(reader);
                return value.Nonce != Guid.Empty ? value : null;
            }
            switch (value.Kind)
            {
                case Kind.SurfaceRecovery:
                    value.Ship = GuidValue(reader); value.Lease = GuidValue(reader);
                    value.SurfaceRegion = (SpacecraftRegion)reader.ReadByte(); value.SurfacePoint = Vector(reader);
                    return value.Ship == Guid.Empty || value.Lease == Guid.Empty
                        || value.SurfaceRegion is not (SpacecraftRegion.TestWorld or SpacecraftRegion.Moon)
                        || !Finite(value.SurfacePoint) ? null : value;
                case Kind.AttachmentRecovery:
                    value.Ship = GuidValue(reader); value.Lease = GuidValue(reader);
                    value.FrameRevision = reader.ReadInt64();
                    value.RecoverySnapshot = reader.ReadInt64();
                    value.RecoveryPoint = Vector(reader); value.RecoveryWalking = Boolean(reader);
                    return value.Ship == Guid.Empty || value.Lease == Guid.Empty || value.FrameRevision < 0 || value.RecoverySnapshot < 0
                        || !Finite(value.RecoveryPoint) ? null : value;
                case Kind.ShipRepairInput:
                    value.Ship = GuidValue(reader); value.Lease = GuidValue(reader);
                    value.ShipRepairGesture = reader.ReadInt64(); value.ShipRepairActive = Boolean(reader);
                    value.Action = (ActionCode)reader.ReadByte(); value.ShipRepairTool = reader.ReadInt32();
                    string? repairCommand = ManualRepairCommand(value.Action);
                    if (value.Ship == Guid.Empty || value.Lease == Guid.Empty || value.ShipRepairGesture <= 0
                        || repairCommand == null || !ValidManualRepairTool(repairCommand, value.ShipRepairTool)) return null;
                    return value;
                case Kind.ShipRepairState:
                    value.Ship = GuidValue(reader);
                    if (value.Ship == Guid.Empty) return null;
                    value.ShipRepairs = ReadShipRepairs(reader);
                    return value;
                case Kind.EngineeringWork:
                    value.Ship = GuidValue(reader); value.Lease = GuidValue(reader);
                    value.EngineeringGesture = reader.ReadInt64(); value.EngineeringActive = Boolean(reader);
                    if (value.Ship == Guid.Empty || value.Lease == Guid.Empty || value.EngineeringGesture <= 0) return null;
                    if (value.EngineeringActive) value.EngineeringIntent = ReadEngineeringIntent(reader);
                    return value;
                case Kind.Engineering:
                    value.Ship = GuidValue(reader);
                    if (value.Ship == Guid.Empty) return null;
                    value.Engineering = ReadEngineering(reader);
                    return value;
                case Kind.Materials:
                    value.Ship = GuidValue(reader);
                    if (value.Ship == Guid.Empty) return null;
                    value.Materials = ReadMaterials(reader);
                    return value;
                case Kind.Repair:
                    value.Ship = GuidValue(reader);
                    if (value.Ship == Guid.Empty) return null;
                    value.Repair = ReadRepair(reader);
                    return value;
                case Kind.Foam:
                    value.Ship = GuidValue(reader);
                    if (value.Ship == Guid.Empty) return null;
                    value.Foam = ReadFoam(reader);
                    return value;
                case Kind.Hull:
                    value.Ship = GuidValue(reader);
                    if (value.Ship == Guid.Empty) return null;
                    value.Hull = ReadHull(reader);
                    return value;
                case Kind.FirefightingSnapshot:
                    value.Ship = GuidValue(reader);
                    if (value.Ship == Guid.Empty) return null;
                    value.Firefighting = ReadFirefighting(reader);
                    return value;
                case Kind.FirefightingBurst:
                    value.Ship = GuidValue(reader);
                    if (value.Ship == Guid.Empty) return null;
                    value.FirefightingBurst = ReadFirefightingBurst(reader, value.Sequence);
                    return value;
                case Kind.Eva:
                    value.Ship = GuidValue(reader);
                    value.Eva = ReadEva(reader, value.Ship);
                    return value;
                case Kind.Station:
                    value.Station = ReadStation(reader);
                    return value;
                case Kind.Orbits:
                    value.OrbitTick = reader.ReadInt64();
                    if (value.OrbitTick is < 0 or > OrbitalSimulation.MaximumTick) return null;
                    value.OrbitPending = reader.ReadDouble();
                    if (!double.IsFinite(value.OrbitPending) || value.OrbitPending < 0
                        || value.OrbitPending > OrbitalSimulation.MaximumPendingSeconds
                        || value.OrbitPending > OrbitalSimulation.MaximumTick - value.OrbitTick) return null;
                    value.OrbitCatalog = new byte[32]; ReadExactly(reader, value.OrbitCatalog);
                    value.OrbitTotal = reader.ReadUInt16(); value.OrbitOffset = reader.ReadUInt16();
                    int count = reader.ReadByte();
                    if (value.OrbitTotal is < 1 or > OrbitalSimulation.MaximumBodies
                        || value.OrbitOffset >= value.OrbitTotal || value.OrbitOffset % OrbitChunkSize != 0
                        || count != Math.Min(OrbitChunkSize, value.OrbitTotal - value.OrbitOffset)) return null;
                    value.OrbitStates = new OrbitalState[count];
                    for (int i = 0; i < count; i++)
                        value.OrbitStates[i] = new(reader.ReadInt32(),
                            OrbitVector(reader, OrbitalSimulation.MaximumPosition),
                            OrbitVector(reader, OrbitalSimulation.MaximumSpeed));
                    return value;
                case Kind.Command:
                    value.Action = (ActionCode)reader.ReadByte(); value.Prepared = Boolean(reader);
                    if (!Enum.IsDefined(value.Action)) return null;
                    if (value.Action == ActionCode.StationShoreAir)
                    {
                        value.Lease = GuidValue(reader);
                        if (value.Lease == Guid.Empty) return null;
                        value.StationSetting = reader.ReadByte();
                        return ValidStationShoreAir(value.StationSetting) ? value : null;
                    }
                    if (value.Action == ActionCode.StationShoreFuel)
                    {
                        value.Lease = GuidValue(reader);
                        if (value.Lease == Guid.Empty) return null;
                        value.StationSetting = reader.ReadByte();
                        return ValidStationShoreFuel(value.StationSetting) ? value : null;
                    }
                    if (value.Action == ActionCode.StationShoreCoolant)
                    {
                        value.Lease = GuidValue(reader);
                        if (value.Lease == Guid.Empty) return null;
                        value.StationSetting = reader.ReadByte();
                        return ValidStationShoreCoolant(value.StationSetting) ? value : null;
                    }
                    if (value.Action is ActionCode.StationCable or ActionCode.StationFloor)
                    {
                        value.Lease = GuidValue(reader);
                        if (value.Lease == Guid.Empty) return null;
                        value.StationTarget = reader.ReadUInt16(); value.StationSetting = reader.ReadByte();
                        value.StationAim = Vector(reader);
                        bool valid = value.Action == ActionCode.StationCable
                            ? ValidStationCable(value.StationTarget, value.StationSetting, value.StationAim)
                            : ValidStationFloor(value.StationTarget, value.StationSetting, value.StationAim);
                        return valid ? value : null;
                    }
                    if (value.Action == ActionCode.StationEngineering)
                    {
                        value.Lease = GuidValue(reader);
                        if (value.Lease == Guid.Empty) return null;
                        value.StationTarget = reader.ReadInt32(); value.StationSetting = reader.ReadByte();
                        return ValidStationEngineering(value.StationTarget, value.StationSetting, value.StationAim) ? value : null;
                    }
                    if (value.Action == ActionCode.StationMaterials)
                    {
                        value.Lease = GuidValue(reader);
                        if (value.Lease == Guid.Empty) return null;
                        value.StationTarget = reader.ReadInt32(); value.StationSetting = reader.ReadUInt16();
                        value.StationAim = Vector(reader);
                        return ValidStationMaterials(value.StationTarget, value.StationSetting, value.StationAim) ? value : null;
                    }
                    if (value.Action == ActionCode.StationRepair)
                    {
                        value.Lease = GuidValue(reader);
                        if (value.Lease == Guid.Empty) return null;
                        value.StationTarget = reader.ReadInt32(); value.StationSetting = reader.ReadUInt16();
                        value.StationAim = Vector(reader);
                        return ValidStationRepair(value.StationTarget, value.StationSetting, value.StationAim) ? value : null;
                    }
                    if (value.Action == ActionCode.StationFoam)
                    {
                        value.Lease = GuidValue(reader);
                        if (value.Lease == Guid.Empty) return null;
                        value.StationTarget = reader.ReadInt32(); value.StationSetting = reader.ReadUInt16();
                        value.StationAim = Vector(reader);
                        return ValidStationFoam(value.StationTarget, value.StationSetting, value.StationAim) ? value : null;
                    }
                    if (value.Action == ActionCode.StationFirefighting)
                    {
                        value.Lease = GuidValue(reader);
                        if (value.Lease == Guid.Empty) return null;
                        value.StationTarget = reader.ReadUInt16(); value.StationSetting = reader.ReadUInt16();
                        value.StationAim = Vector(reader);
                        return ValidStationFirefighting(value.StationTarget, value.StationSetting, value.StationAim) ? value : null;
                    }
                    if (value.Action == ActionCode.StationDoor)
                    {
                        value.StationTarget = reader.ReadInt32();
                        var layout = BoxStationLayout.Current;
                        return value.StationTarget >= 0 && (value.StationTarget < layout.Doors.Length
                            || BoxStationDoors.ValidEdge(layout, value.StationTarget - layout.Doors.Length)) ? value : null;
                    }
                    if (value.Action is ActionCode.StationAirMode or ActionCode.StationRoomThermal or ActionCode.StationFireAlarm or ActionCode.StationSmes
                        || IsStationPlumbing(value.Action) || IsStationPower(value.Action))
                    {
                        value.StationTarget = reader.ReadUInt16();
                        int targets = IsStationPlumbing(value.Action) ? BoxStationPlumbing.GetDevices(BoxStationLayout.Current).Length
                            : value.Action == ActionCode.StationAirMode ? BoxStationAirAlarms.GetDevices(BoxStationLayout.Current).Length
                            : value.Action == ActionCode.StationRoomThermal ? BoxStationRoomThermal.GetDevices(BoxStationLayout.Current).Length
                            : value.Action == ActionCode.StationSmes ? BoxStationPower.GetSmes(BoxStationLayout.Current).Length
                            : value.Action == ActionCode.StationFireAlarm ? BoxStationFireAlarms.GetDevices(BoxStationLayout.Current).Length
                            : BoxStationPower.GetApcs(BoxStationLayout.Current).Length;
                        if (value.StationTarget >= targets) return null;
                    }
                    if (IsStationPlumbingSetting(value.Action))
                    {
                        value.StationSetting = reader.ReadUInt16();
                        if (!ValidStationPlumbingSetting(value.Action, value.StationTarget, value.StationSetting)) return null;
                    }
                    else if (value.Action == ActionCode.StationAirMode)
                    {
                        value.StationSetting = reader.ReadUInt16();
                        if (!ValidStationAirMode(value.StationTarget, value.StationSetting)) return null;
                    }
                    else if (value.Action == ActionCode.StationRoomThermal)
                    {
                        value.StationSetting = reader.ReadUInt16();
                        if (!ValidStationRoomThermal(value.StationTarget, value.StationSetting)) return null;
                    }
                    else if (value.Action == ActionCode.StationSmes)
                    {
                        value.StationSetting = reader.ReadUInt16();
                        if (!ValidStationSmes(value.StationTarget, value.StationSetting)) return null;
                    }
                    else if (value.Action == ActionCode.StationFireAlarm)
                    {
                        value.StationSetting = reader.ReadUInt16();
                        if (!ValidStationFireAlarm(value.StationTarget, value.StationSetting)) return null;
                    }
                    return value;
                case Kind.Control:
                    value.Lease = GuidValue(reader);
                    var translation = Vector(reader); var rotationInput = Vector(reader);
                    var powered = Boolean(reader); var dampeners = Boolean(reader); var boost = Boolean(reader);
                    var doorOpen = Boolean(reader); var console = (SpacecraftConsoleControl)reader.ReadByte();
                    var reach = reader.ReadSingle();
                    var gearDeployed = Boolean(reader);
                    if (value.Lease == Guid.Empty || !Axes(translation) || !Axes(rotationInput)
                        || !Enum.IsDefined(console) || !float.IsFinite(reach) || reach < 0 || reach > 1) return null;
                    value.Controls = new(translation, rotationInput, powered, dampeners, boost, doorOpen, console, reach,
                        GearDeployed: gearDeployed); return value;
                case Kind.SeatStatus:
                    value.Lease = GuidValue(reader); value.Attached = Boolean(reader); value.Prepared = Boolean(reader);
                    value.FrameRevision = reader.ReadInt64();
                    return value.FrameRevision < 0 || value.Attached && value.Lease == Guid.Empty ? null : value;
                case Kind.ConsolePose:
                    value.Lease = GuidValue(reader);
                    var poseControl = (SpacecraftConsoleControl)reader.ReadByte(); var poseReach = reader.ReadSingle();
                    if (value.Lease == Guid.Empty || !ConsoleControlAllowed(1, poseControl)
                        || !float.IsFinite(poseReach) || poseReach < 0 || poseReach > 1
                        || poseControl == SpacecraftConsoleControl.None && poseReach != 0) return null;
                    value.Controls = SpacecraftControls.Parked with { ConsoleControl = poseControl, ConsoleReach = poseReach };
                    return value;
                case Kind.Snapshot:
                    value.Acknowledged = reader.ReadInt64(); if (value.Acknowledged < 0) return null;
                    value.Result = Text(reader);
                    value.EntryPrepared = Boolean(reader);
                    if (value.EntryPrepared)
                    { value.EntryAnchor = Vector(reader); if (!Finite(value.EntryAnchor) || value.EntryAnchor.y >= 2000) return null; }
                    value.Ship = GuidValue(reader);
                    value.Seat = reader.ReadSByte(); value.Lease = GuidValue(reader); value.ForceReturn = Boolean(reader);
                    value.CrewAccess = Boolean(reader);
                    if (value.Seat < -1 || value.Seat > 2 || (value.Seat == -1) != (value.Lease == Guid.Empty)
                        || value.Seat >= 0 && value.ForceReturn) return null;
                    for (var i = 0; i < 3; i++) value.Actors[i] = reader.ReadUInt64();
                    for (var i = 0; i < 3; i++)
                    {
                        value.Walking[i] = Boolean(reader);
                        if (value.Walking[i] && value.Actors[i] == 0) return null;
                    }
                    for (var i = 0; i < 3; i++)
                    {
                        var flags = reader.ReadByte();
                        if (flags == 0) continue;
                        if (flags is not (1 or 3 or 7) || value.Actors[i] == 0 || !value.Walking[i]) return null;
                        var target = Vector(reader); var interactionReach = reader.ReadByte();
                        if (!(InteractionPoint(target) || value.Ship != Guid.Empty && StationInteractionPoint(target))
                            || interactionReach == 0) return null;
                        value.Interactions[i] = new(value.Actors[i], target, interactionReach / 255f)
                        {
                            LocalRotation = (flags & 2) != 0 ? ContactRotation(reader) : null,
                            Press = (flags & 4) != 0
                        };
                    }
                    if (Boolean(reader))
                    {
                        var cabinStand = Vector(reader);
                        if (!CabinPoint(cabinStand)) return null;
                        value.CabinStandLocal = cabinStand;
                    }
                    if (value.CabinStandLocal.HasValue != (value.Seat >= 0 && value.Walking[value.Seat])) return null;
                    if (Boolean(reader))
                    {
                        var exit = Vector(reader); value.SurfaceCompleted = reader.ReadInt64();
                        if (!Finite(exit) || value.SurfaceCompleted < 0) return null;
                        value.Exit = exit;
                    }
                    if (value.Seat >= 0 && value.Exit.HasValue) return null;
                    value.StorageId = reader.ReadUInt64(); value.StorageAboard = Boolean(reader);
                    value.StorageParkPosition = Vector(reader); value.StorageParkRotation = Rotation(reader);
                    if (!Finite(value.StorageParkPosition) || !ValidRotation(value.StorageParkRotation)
                        || value.StorageAboard && (value.StorageId == 0 || value.Ship == Guid.Empty)) return null;
                    if (value.Ship == Guid.Empty)
                        return value.Seat == -1 && value.Actors.All(actor => actor == 0)
                            && !value.Walking.Any(walking => walking) ? value : null;
                    value.FrameRevision = reader.ReadInt64();
                    if (value.FrameRevision <= 0) return null;
                    if (Boolean(reader))
                    {
                        var origin = OrbitVector(reader, OrbitalSimulation.MaximumPosition);
                        var frameVelocity = OrbitVector(reader, OrbitalSimulation.MaximumSpeed);
                        var right = OrbitVector(reader, 2); var up = OrbitVector(reader, 2); var forward = OrbitVector(reader, 2);
                        value.SpaceFrame = new OrbitalFrame(origin, frameVelocity, default, right, up, forward, default);
                        value.SpaceCruiseVelocity = OrbitVector(reader, OrbitalSimulation.MaximumSpeed);
                        value.SpaceFrameTime = reader.ReadDouble();
                        if (!double.IsFinite(value.SpaceFrameTime) || value.SpaceFrameTime < 0
                            || value.SpaceFrameTime > OrbitalSimulation.MaximumTick) return null;
                    }
                    value.FlightTime = reader.ReadDouble();
                    if (!double.IsFinite(value.FlightTime) || value.FlightTime < 0
                        || value.FlightTime > OrbitalSimulation.MaximumTick
                        || value.SpaceFrame.HasValue && value.SpaceFrameTime > value.FlightTime) return null;
                    var position = Vector(reader); var rotation = Rotation(reader);
                    var velocity = Vector(reader); var angular = Vector(reader);
                    var on = Boolean(reader); var grounded = Boolean(reader); var damping = Boolean(reader);
                    var region = (SpacecraftRegion)reader.ReadByte(); var travel = (SpacecraftTravel)reader.ReadByte();
                    var progress = reader.ReadSingle();
                    var open = Boolean(reader); var doorAmount = reader.ReadSingle();
                    for (var seat = 0; seat < 2; seat++)
                    {
                        var control = (SpacecraftConsoleControl)reader.ReadByte(); var consoleReach = reader.ReadByte();
                        if (!ConsoleControlAllowed(seat, control)
                            || (control == SpacecraftConsoleControl.None) != (consoleReach == 0)) return null;
                        if (consoleReach == 0) continue;
                        if (value.Actors[seat] == 0 || value.Walking[seat]) return null;
                        value.ConsolePoses[seat] = new(value.Actors[seat], control, consoleReach / 255f);
                    }
                    var gear = Boolean(reader); var gearAmount = reader.ReadSingle();
                    var innerDoorAmount = Range(reader, 0, 1); var cabinDoorAmount = Range(reader, 0, 1);
                    var translationInput = Vector(reader); var flightRotationInput = Vector(reader); var boostActive = Boolean(reader);
                    var systems = Systems(reader);
                    var docked = Boolean(reader); value.StationAssist = Boolean(reader);
                    foreach (var interaction in value.Interactions)
                        if (!docked && !InteractionPoint(interaction.LocalTarget)) return null;
                    if (docked && (region != SpacecraftRegion.Space || travel != SpacecraftTravel.None
                            || on || grounded || !gear || gearAmount < .999f || translationInput != Vector3.zero
                            || flightRotationInput != Vector3.zero || boostActive || value.StationAssist)
                        || value.StationAssist && (region != SpacecraftRegion.Space || travel != SpacecraftTravel.None)) return null;
                    var localVelocity = new OrbitalVector(velocity.x, velocity.y, velocity.z);
                    if (!Finite(position) || !ValidRotation(rotation) || !localVelocity.IsFinite
                        || region != SpacecraftRegion.Space && velocity.sqrMagnitude > 1e6f
                        || !Finite(angular) || angular.sqrMagnitude > 400 || !Enum.IsDefined(region)
                        || !Enum.IsDefined(travel) || !float.IsFinite(progress) || progress < 0 || progress > 1
                        || travel == SpacecraftTravel.None && progress != 0
                        || !float.IsFinite(doorAmount) || doorAmount < 0 || doorAmount > 1
                        || !float.IsFinite(gearAmount) || gearAmount < 0 || gearAmount > 1
                        || !Axes(translationInput) || !Axes(flightRotationInput)
                        || value.SpaceFrame.HasValue != (region == SpacecraftRegion.Space)) return null;
                    if (value.SpaceFrame is { } frame)
                    {
                        var currentOrigin = frame.Origin + frame.Velocity * (value.FlightTime - value.SpaceFrameTime);
                        var localPosition = new OrbitalVector((double)position.x - SpacecraftLayout.SpaceOrigin.x,
                            (double)position.y - SpacecraftLayout.SpaceOrigin.y, (double)position.z - SpacecraftLayout.SpaceOrigin.z);
                        var inertialPosition = currentOrigin + frame.ToInertialDirection(localPosition);
                        if (!inertialPosition.IsFinite
                            || inertialPosition.LengthSquared > OrbitalSimulation.MaximumPosition * OrbitalSimulation.MaximumPosition)
                            return null;
                        if (localVelocity.LengthSquared > 4 * OrbitalSimulation.MaximumSpeed * OrbitalSimulation.MaximumSpeed)
                            return null;
                        var inertialVelocity = frame.ToInertialVelocity(default, localVelocity);
                        if (!inertialVelocity.IsFinite
                            || inertialVelocity.LengthSquared > OrbitalSimulation.MaximumSpeed * OrbitalSimulation.MaximumSpeed)
                            return null;
                    }
                    value.Flight = new(position, rotation, velocity, angular, on, grounded, damping, region, travel, progress,
                        open, doorAmount, GearDeployed: gear, GearAmount: gearAmount,
                        TranslationInput: translationInput, RotationInput: flightRotationInput, BoostActive: boostActive,
                        Systems: systems, InnerDoorAmount: innerDoorAmount, CabinDoorAmount: cabinDoorAmount, Docked: docked);
                    return value;
                default: return null;
            }
        }
    }
}
