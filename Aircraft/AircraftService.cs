// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using Endnight.Extensions;
using Modstoke.Sons;
using SonsSdk.Networking;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Client;

// One shared test airframe per world. Only the host mutates its physics or seats.
internal sealed partial class ClientAircraftService
{
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly Dictionary<Guid, Peer> _peers = new();
    private readonly KeyValuePair<Guid, Peer>[] _peerSnapshot = new KeyValuePair<Guid, Peer>[16];
    private bool _peerSnapshotInUse;
    private readonly Peer?[] _seats = new Peer?[3];
    private readonly ulong[] _actorIds = new ulong[3];
    private readonly Rate _receiveRate = new();
    private ClientAircraftBody? _body;
    private ClientAircraftRider? _rider;
    private ClientAircraftOccupants? _occupants;
    private ClientAircraftCockpit? _cockpit;
    private AircraftEvent? _event;
    private Peer? _local;
    private Guid _planeId, _epoch, _nonce, _token, _clientLease, _ridingLease, _rejectedLease;
    private IntPtr _serverPointer;
    private uint _serverConnection;
    private bool _started, _ready;
    private long _nextHello, _nextSnapshot, _nextControl, _lastSnapshot, _lastWelcome;
    private long _commandSequence, _controlSequence, _snapshotSequence, _receivedSequence;
    private int _clientSeat = -1;
    private Vector3? _clientExit;
    private string _status = "Waiting for the world.", _feedback = "";
    private Pending? _pending;
    private Action? _closeDevices;
    private bool IsClient => BoltNetwork.isRunning && !BoltNetwork.isServer;
    private bool OnThread => Environment.CurrentManagedThreadId == _thread;
    private static long Now => Environment.TickCount64;
    internal bool UsingAircraft => _rider?.Active == true || _rider?.Restoring == true
        || (_ready && (IsClient ? _clientSeat >= 0 : _local?.Seat >= 0));
    internal ClientAircraftView View { get; private set; } =
        new(false, false, "Waiting for world.", "", -1, 0, 0, default, false, false);

    internal void Start(Func<bool> acquire, Action release, Action closeDevices)
    {
        if (_started) return;
        if (!Packets.SupportsClientRelayPolicy)
            throw new InvalidOperationException("Aircraft requires authenticated host routing.");
        _closeDevices = closeDevices;
        _rider = new ClientAircraftRider(acquire, release);
        _occupants = new ClientAircraftOccupants();
        _started = true;
        _event = new AircraftEvent(this);
        Packets.Register(_event);
        StartPersistence();
        RegisterSeatEvidence();
    }

    internal void Stop()
    {
        if (!_started) return;
        try { StoreWorld(); }
        finally
        {
            _started = false;
            UnregisterSeatEvidence();
            StopPersistence();
            if (_event != null) Packets.Unregister(_event);
            _event = null;
            ResetWorld();
            _rider?.Dispose(); _rider = null;
            _occupants?.Dispose(); _occupants = null;
            _cockpit?.Dispose(); _cockpit = null;
        }
    }

    internal void Update(ClientTabletPreferencesSnapshot? audio)
    {
        try { UpdateCore(audio); }
        catch (Exception error) { Fail(error); }
    }

    private void UpdateCore(ClientTabletPreferencesSnapshot? audio)
    {
        if (!_started || !OnThread) return;
        _occupants?.Update();
        if (!RefreshWorld())
        {
            ShowInteraction("");
            _rider?.Update();
            PublishView();
            return;
        }
        var now = Now;
        if (IsClient)
        {
            if (now >= _nextHello)
            { _nextHello = now + (_ready ? 2000 : 1000); _event?.Hello(); }
            if (_ready && now - Math.Max(_lastSnapshot, _lastWelcome) > 5000)
            {
                // Do not retain a visual seat lease after its host has disappeared.
                _clientSeat = -1; _clientLease = Guid.Empty;
                _ready = false; _status = "Aircraft host is not responding.";
                RemoveBody();
            }
            if (_pending != null && now >= _pending.Next)
            {
                if (_pending.Attempts++ >= 5)
                { _feedback = "Request timed out. Check the aircraft state before trying again."; _pending = null; }
                else { _pending.Next = now + 500; _event?.Request(_pending.Action, _pending.Sequence); }
            }
        }
        else
        {
            RefreshLocal();
            // Nested callbacks and larger worlds retain their own complete snapshot.
            var ownsPeerSnapshot = !_peerSnapshotInUse && _peers.Count <= _peerSnapshot.Length;
            var peers = ownsPeerSnapshot ? _peerSnapshot : _peers.ToArray();
            var peerCount = ownsPeerSnapshot ? 0 : peers.Length;
            if (ownsPeerSnapshot) _peerSnapshotInUse = true;
            try
            {
                if (ownsPeerSnapshot)
                {
                    foreach (var pair in _peers)
                    {
                        peers[peerCount] = pair;
                        peerCount++;
                    }
                }
                for (var i = 0; i < peerCount; i++)
                {
                    var pair = peers[i];
                    var peer = pair.Value;
                    if (!PeerCurrent(peer) || now - peer.LastSeen > 6000)
                    { ReleaseSeat(peer, null); _peers.Remove(pair.Key); continue; }
                    if (!TryActor(peer, out _, out _, out _))
                    {
                        ReleaseSeat(peer, null);
                        // A fresh native attachment needs a fresh binding and seat lease.
                        peer.Binding = null;
                        if (now < peer.NextBindingRetry) continue;
                        peer.NextBindingRetry = now + 250;
                        peer.Binding = ServerWorld.TryCapturePlayerBinding(peer.Session!, out var binding, out _) ? binding : null;
                    }
                    else peer.NextBindingRetry = 0;
                }
            }
            finally
            {
                Array.Clear(peers, 0, peerCount);
                if (ownsPeerSnapshot) _peerSnapshotInUse = false;
            }
            for (var i = 0; i < _seats.Length; i++)
            {
                var peer = _seats[i];
                if (peer == null) { _actorIds[i] = 0; continue; }
                if (!TryActor(peer, out var position, out _, out var actor))
                { ReleaseSeat(peer, null); continue; }
                _actorIds[i] = peer.Phase == SeatPhase.Boarding ? 0 : actor;
                // Allow attachment time and ordinary network lag, not arbitrary teleports.
                if (_body != null && peer.Phase != SeatPhase.Leaving && now - peer.BoardedAt > 4000
                    && (position - _body.Root.transform.TransformPoint(AircraftLayout.Seat(i))).sqrMagnitude > 225)
                    ReleaseSeat(peer, null);
            }
            UpdateDoorTransitions(now);
            CaptureParkedPosition();
            if (now >= _nextSnapshot)
            {
                _nextSnapshot = now + 100;
                var sequence = ++_snapshotSequence;
                foreach (var peer in _peers.Values) _event?.Snapshot(peer, sequence);
            }
        }
        SyncRider();
        _rider?.Update();
        SyncSeatTransition(now);
        if (_rider?.PointerActive == true && _rider.Seat == 0 && _rider.Camera != null
            && _body != null && Input.GetMouseButtonDown(0)
            && (UnityEngine.EventSystems.EventSystem.current == null
                || !UnityEngine.EventSystems.EventSystem.current.IsPointerOverGameObject())
            && _body.TryCockpitControl(_rider.Camera.ScreenPointToRay(Input.mousePosition), out var cockpitCommand))
            _rider.SetCockpitControl(cockpitCommand);
        if (_rider?.ExitRequested == true)
        { _rider.ConsumeExitRequest(); Command("exit"); }
        if (_rider?.Faulted == true && _ridingLease != Guid.Empty && _rejectedLease != _ridingLease
            && (!IsClient || _pending == null))
        {
            _feedback = _rider.FailureReason;
            _rejectedLease = _ridingLease;
            Command("cancel");
        }
        if (_rider?.Active == true && _rider.Seat == 0)
        {
            if (IsClient && _ready && now >= _nextControl)
            { _nextControl = now + 50; _event?.Control(++_controlSequence, _clientLease, _rider.Controls); }
            else if (!IsClient && _local?.Seat == 0)
            { _local.Input = _rider.Controls; _local.LastControl = now; }
        }
        _body?.Update(Time.unscaledDeltaTime, audio);
        RecordSeatEvidence();
        if (_body != null)
        {
            byte visibleOccupants = 0;
            for (var seat = 0; seat < _actorIds.Length; seat++)
                if (_actorIds[seat] != 0) visibleOccupants |= (byte)(1 << seat);
            if (_rider?.Attached == true) visibleOccupants |= (byte)(1 << _rider.Seat);
            _body.SetVisibleOccupants(visibleOccupants);
            _occupants?.SetOccupants(_body, _actorIds);
        }
        if (_rider?.Active == true && _body != null)
        {
            _cockpit ??= new ClientAircraftCockpit(command =>
            {
                if (command == "exit") Command("exit");
                else _rider?.SetCockpitControl(command switch
                { "throttle+" => "throttle-up", "throttle-" => "throttle-down", _ => command });
            });
            _cockpit.Update(_body.State, _rider.Seat, true, _feedback);
            _cockpit.SetMouseControlsVisible(_rider.PointerActive);
        }
        else _cockpit?.Update(default, -1, false);
        UpdateInteractions();
        PublishView();
    }

    internal void FixedUpdate()
    {
        try { FixedUpdateCore(); }
        catch (Exception error) { Fail(error); }
    }

    private void FixedUpdateCore()
    {
        if (!_started || !_ready || _body == null) return;
        var pilot = _seats[0];
        var fresh = !IsClient && pilot != null && pilot.Phase == SeatPhase.Seated
            && PeerCurrent(pilot) && Now - pilot.LastControl < 800;
        var input = fresh ? pilot!.Input : AircraftControls.Parked;
        if (!IsClient && (_doorMask != 0 || Now < _doorsClosedAt || !_body.DoorsClosed
            || _body.State.Grounded && HasUnseatedCabinPlayer()))
            input = input with { Throttle = 0, Brake = true };
        if (!IsClient)
        {
            _body.SetPilot(pilot?.Lease ?? Guid.Empty);
            byte occupants = 0;
            for (var i = 0; i < _seats.Length; i++)
                if (_seats[i] != null) occupants |= (byte)(1 << i);
            _body.SetOccupants(occupants);
        }
        _body.FixedUpdate(input, fresh);
    }

    internal void LateUpdate()
    {
        if (!_started) return;
        try { _rider?.LateUpdate(); _occupants?.LateUpdate(); }
        catch (Exception error) { Fail(error); }
    }

    internal string Command(string command)
    {
        try { return CommandCore(command); }
        catch (Exception error) { Fail(error); return _feedback; }
    }

    private string CommandCore(string command)
    {
        if (Plugin.Spacecraft.LocalRegion != SpacecraftRegion.Island
            && command is "deploy" or "board-pilot" or "board-passenger"
                or "sit-pilot" or "sit-passenger-front" or "sit-passenger-rear"
                or "open-port-door" or "open-starboard-door")
            return _feedback = "Return to the island before using the Hangar. Your spacecraft handles this expedition.";
        var action = command switch
        {
            "deploy" => ActionCode.Deploy, "board-pilot" => ActionCode.Pilot,
            "board-passenger" => ActionCode.Passenger, "exit" => ActionCode.Exit,
            "service" => ActionCode.Service, "recover" => ActionCode.Recover,
            "sit-pilot" => ActionCode.SitPilot, "sit-passenger-front" => ActionCode.SitPassengerFront,
            "sit-passenger-rear" => ActionCode.SitPassengerRear,
            "open-port-door" => ActionCode.OpenPortDoor, "open-starboard-door" => ActionCode.OpenStarboardDoor,
            "close-port-door" => ActionCode.ClosePortDoor, "close-starboard-door" => ActionCode.CloseStarboardDoor,
            "cancel" => ActionCode.Cancel, _ => (ActionCode)255
        };
        if (!_started || !_ready || action == (ActionCode)255) return _feedback = "Hangar service is unavailable.";
        if (IsClient)
        {
            if (_pending != null) return _feedback = "Waiting for the host's previous reply.";
            _pending = new Pending(action, ++_commandSequence);
            _event?.Request(action, _pending.Sequence);
            return _feedback = "Request sent to the host.";
        }
        RefreshLocal();
        return _feedback = _local == null ? "A living local player is required." : ApplyCommand(_local, ++_commandSequence, action);
    }

    private void Fail(Exception error)
    {
        Console.WriteLine("Modstoke aircraft service failed: " + error);
        try { Stop(); }
        catch (Exception cleanup) { Console.WriteLine("Modstoke aircraft cleanup: " + cleanup); }
        _status = "Aircraft service stopped after an error. Other field devices remain available.";
        _feedback = "The aircraft error was logged. Restart the game after checking the Client installation.";
        View = new(false, false, _status, _feedback, -1, 0, 0, default, false, false);
    }

    private string ApplyCommand(Peer peer, long sequence, ActionCode action)
    {
        if (!PeerCurrent(peer) || sequence <= 0) return "The player session changed.";
        if (sequence == peer.LastCommand) return peer.Result;
        if (sequence < peer.LastCommand) return "This request has already expired.";
        if (!peer.Commands.Take(Now, 4, 4)) return "Please wait before trying again.";
        peer.LastCommand = sequence;
        peer.Result = Execute(peer, action);
        if (action == ActionCode.Deploy)
            UnityEngine.Debug.Log("Modstoke aircraft deployment: " + peer.Result);
        else if (action == ActionCode.Exit)
            UnityEngine.Debug.Log("Modstoke aircraft exit: " + peer.Result);
        return peer.Result;
    }

    private string Execute(Peer peer, ActionCode action)
    {
        if (action == ActionCode.Cancel)
        {
            if (peer.Phase == SeatPhase.Boarding) CancelBoarding(peer);
            else if (peer.Seat >= 0 && _body != null && TryActor(peer, out var position, out _, out _)
                && (PlayerAttached(peer) || (position - _body.State.Position).sqrMagnitude <= 225))
            {
                if (peer.Phase is SeatPhase.OpeningExit or SeatPhase.Leaving) return "The cabin exit is in progress.";
                if (!TryDeparture(peer.Seat, true, out var faultExit))
                    return "Emergency exit obstructed. Try again when clear of the obstruction.";
                BeginExit(peer, faultExit, true);
            }
            else ReleaseSeat(peer, null);
            return peer.Seat < 0 ? "Boarding cancelled." : "Emergency egress requested. Falling and collision damage still apply.";
        }
        if (!TryActor(peer, out var actorPosition, out var actorRotation, out _)) return "A living player is required.";
        if (action == ActionCode.Deploy)
        {
            if (_body != null) return "A shared plane is already deployed.";
            if (!TryPlacement(actorPosition, actorRotation, out var position, out var rotation, out var failure))
                return failure;
            _planeId = Guid.NewGuid();
            _body = new ClientAircraftBody(_planeId.ToString("N"), position, rotation, true);
            RememberParking(position, rotation);
            StoreWorld();
            return "Kestrel K3 deployed nearby ahead. First 24 m of taxi space checked; inspect the rest of your runway. Approach a clear cabin door.";
        }
        if (_body == null) return "Deploy a plane first.";
        var flight = _body.State;
        var parked = flight.Grounded && flight.Velocity.sqrMagnitude < 4;
        if (action == ActionCode.Exit)
        {
            if (peer.Seat < 0) return "You are not seated.";
            if (peer.Phase == SeatPhase.Boarding)
            {
                if (PlayerAttached(peer)) RetainSeat(peer);
                else
                {
                    CancelBoarding(peer);
                    return peer.Seat < 0 ? "Boarding cancelled." : "Remain seated until the cabin exit is clear and the plane has stopped.";
                }
            }
            var emergency = EmergencyExitAvailable(flight) || !flight.Grounded && OverturnedHullSupported(flight);
            if (peer.Phase == SeatPhase.Leaving) return "Leaving the airframe.";
            if (peer.Phase == SeatPhase.OpeningExit && !emergency)
                return "The cabin door is opening. Please wait.";
            if (!emergency && !ExitSafe(flight)) return "Land and let the plane settle before leaving the cabin.";
            if (!TryDeparture(peer.Seat, emergency, out var exit)) return "The cabin exit is obstructed. Try again when clear.";
            BeginExit(peer, exit, emergency);
            return emergency ? "Leaving the airframe. Falling and collision damage still apply." : "Opening the cabin door.";
        }
        if ((actorPosition - flight.Position).sqrMagnitude > 64) return "Move within 8 metres of the plane.";
        if (action == ActionCode.Recover)
        {
            if (peer.Session != null) return "Only the hosting player can recover the plane.";
            if (!RecoveryMotionSafe(flight)) return "Let the plane stop moving before recovery.";
            if (_seats.Any(value => value != null) || flight.Engine || HasUnseatedCabinPlayer(true))
                return "Empty the cabin and stop the engine first.";
            // A stuck or overturned hull may have no loaded wheel contacts.
            // Recovery must not require the same ground state as boarding.
            RemoveBody(); _planeId = Guid.Empty; StoreWorld();
            return "Shared plane recovered. Face a clear runway before deploying again.";
        }
        if (!parked) return "The plane must be on the ground and below 2 m/s.";
        if (action is ActionCode.OpenPortDoor or ActionCode.OpenStarboardDoor or ActionCode.ClosePortDoor or ActionCode.CloseStarboardDoor)
            return ChangeEntryDoor(peer, actorPosition, action);
        if (action is ActionCode.Pilot or ActionCode.Passenger
            or ActionCode.SitPilot or ActionCode.SitPassengerFront or ActionCode.SitPassengerRear)
        {
            if (peer.Seat >= 0) return "Leave your current seat before changing seats.";
            if (flight.Health <= .08f) return "The airframe is disabled. Service it before boarding.";
            var exactChair = action is ActionCode.SitPilot or ActionCode.SitPassengerFront or ActionCode.SitPassengerRear;
            var seat = action switch
            {
                ActionCode.Pilot or ActionCode.SitPilot => 0,
                ActionCode.SitPassengerFront => 1,
                ActionCode.SitPassengerRear => 2,
                _ => _seats[1] == null ? 1 : 2
            };
            if (_seats[seat] != null) return exactChair ? "That chair is occupied."
                : seat == 0 ? "The pilot seat is occupied." : "Both passenger seats are occupied.";
            if (exactChair)
            {
                var actor = ActorTransform(peer);
                if (!_body.CanReachSeat(actorPosition, seat, actor, SeatReachOrigin(actorPosition, actor)))
                    return "Move beside that chair or its open doorway with a clear path before sitting.";
            }
            peer.Seat = seat; peer.Lease = Guid.NewGuid(); peer.BoardedAt = Now; peer.Exit = null;
            peer.Departure = null;
            peer.Input = AircraftControls.Parked; peer.LastControl = peer.LastControlSequence = 0;
            _seats[seat] = peer;
            BeginBoarding(peer, actorPosition);
            return seat == 0 ? "Pilot seat reserved. Opening the cabin door." : "Passenger seat reserved. Opening the cabin door.";
        }
        if (_seats.Any(value => value != null) || flight.Engine || HasUnseatedCabinPlayer(true))
            return "Empty the cabin and stop the engine first.";
        if (action == ActionCode.Service)
        {
            _body.Service(); StoreWorld();
            return "Test support: airframe repaired and fuel topped up.";
        }
        return "Unknown aircraft request.";
    }

    internal static bool RecoveryMotionSafe(AircraftFlightState flight) =>
        Finite(flight.Velocity) && Finite(flight.AngularVelocity)
        && flight.Velocity.sqrMagnitude < .25f && flight.AngularVelocity.sqrMagnitude < .01f;

    private void ReleaseSeat(Peer peer, Vector3? exit)
    {
        if (peer.Phase == SeatPhase.Leaving && peer.Lease != Guid.Empty && exit is { } point
            && peer.Binding != null && Finite(point))
            peer.Departure = new DepartureEvidence(peer.Binding, peer.Lease, _epoch, _planeId, point, Now);
        HoldDoorAfterTransition(peer);
        if (peer.Seat >= 0 && peer.Seat < 3 && ReferenceEquals(_seats[peer.Seat], peer))
        { _seats[peer.Seat] = null; _actorIds[peer.Seat] = 0; }
        peer.Seat = -1; peer.Lease = Guid.Empty; peer.Exit = exit;
        peer.Input = AircraftControls.Parked; peer.LastControl = 0;
        peer.Phase = SeatPhase.None; peer.Door = 0;
        peer.EmergencyExit = false;
        peer.NextExitEvidence = 0;
        peer.DoorReadyAt = peer.TransitionExpiresAt = peer.LastSeatSequence = 0;
    }

    private void SyncRider()
    {
        var seat = IsClient ? _clientSeat : _local?.Seat ?? -1;
        var lease = IsClient ? _clientLease : _local?.Lease ?? Guid.Empty;
        if (_ridingLease != Guid.Empty && (_ridingLease != lease || seat < 0 || _body == null))
        {
            if (_rider != null && !_rider.End(IsClient ? _clientExit : _local?.Exit, true)) return;
            _ridingLease = Guid.Empty;
        }
        if (seat < 0 || lease == Guid.Empty || lease == _ridingLease || lease == _rejectedLease
            || _body == null || _rider == null || _rider.Restoring) return;
        var phase = IsClient ? _clientPhase : _local?.Phase ?? SeatPhase.None;
        var door = IsClient ? _clientDoor : _local?.Door ?? 0;
        if (phase != SeatPhase.Boarding || !DoorVisibleReady(door)) return;
        _closeDevices?.Invoke();
        if (_rider.Begin(_body, seat, out var reason)) _ridingLease = lease;
        else
        {
            _rejectedLease = lease;
            Command("cancel"); _feedback = reason;
        }
    }

    private void PublishView()
    {
        var distance = 0f;
        if (_body != null && LocalPlayer.Transform != null && LocalPlayer.Transform)
            distance = Vector3.Distance(LocalPlayer.Transform.position, _body.State.Position);
        var occupants = IsClient ? _actorIds.Count(id => id != 0) : _seats.Count(peer => peer != null);
        View = new(_ready, _body != null, _status, _feedback,
            IsClient ? _clientSeat : _local?.Seat ?? -1, occupants, distance,
            _body?.State ?? default, IsClient ? _actorIds[0] != 0 : _seats[0] != null, !IsClient);
    }

    private void RemoveBody(bool restoreEquipment = false)
    {
        ResetInteractions();
        ResetDoors();
        var body = _body;
        try { if (body != null) _occupants?.Remove(body); }
        finally
        {
            try { _rider?.Teardown(restoreEquipment); }
            finally { _body = null; body?.Dispose(); }
        }
    }

    private void RefreshLocal()
    {
        if (IsClient || !_ready) return;
        if (_local != null && TryActor(_local, out _, out _, out _)) return;
        if (_local != null) ReleaseSeat(_local, null);
        _local = null;
        if (BoltNetwork.isRunning)
        {
            if (!LocalHost.TryGetPlayer(out var host)
                || !ServerWorld.TryCaptureLocalPlayerBinding(out var binding, out _)) return;
            _local = new Peer { Binding = binding, Owner = host.SteamId };
        }
        else if (LocalAlive()) _local = new Peer();
    }

    private bool PeerCurrent(Peer peer)
    {
        if (!_ready) return false;
        if (peer.Session == null) return ReferenceEquals(peer, _local) && !IsClient;
        if (!BoltNetwork.isRunning || !BoltNetwork.isServer || !peer.Session.IsCurrent
            || !ServerSessions.TryReadIdentity(peer.Session, out var identity, out _)
            || identity.GameSteamId != peer.Owner) return false;
        return ClientConnections.Contains(peer.Pointer, peer.Connection);
    }

    private bool TryActor(Peer peer, out Vector3 position, out Quaternion rotation, out ulong actorId)
    {
        position = default; rotation = Quaternion.identity; actorId = 0;
        if (!PeerCurrent(peer)) return false;
        if (!BoltNetwork.isRunning)
        {
            if (!LocalAlive()) return false;
            position = LocalPlayer.Transform.position; rotation = LocalPlayer.Transform.rotation;
            return Finite(position);
        }
        if (peer.Binding == null || !ServerWorld.TryReadPlayerLife(peer.Binding, out var life, out _)
            || life.LifeState != PlayerLifeState.Alive) return false;
        var actor = life.Entity;
        position = new(actor.Position.X, actor.Position.Y, actor.Position.Z);
        rotation = new(actor.Rotation.X, actor.Rotation.Y, actor.Rotation.Z, actor.Rotation.W);
        actorId = peer.Binding.NetworkId;
        return Finite(position);
    }

    private static bool LocalAlive() => LocalPlayer.Transform != null && LocalPlayer.Transform
        && LocalPlayer.Stats != null && LocalPlayer.Stats._dyingState == PlayerStats.DyingState.Alive
        && !LocalPlayer.Stats._playerIsDowned && !LocalPlayer.Stats._respawning;
    private static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y)
        && float.IsFinite(value.z) && value.sqrMagnitude < 4e10f;
    private static bool ValidRotation(Quaternion value) => float.IsFinite(value.x) && float.IsFinite(value.y)
        && float.IsFinite(value.z) && float.IsFinite(value.w)
        && MathF.Abs(value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w - 1) < .02f;

    private sealed class Peer
    {
        internal ServerSession? Session;
        internal PlayerHandle? Binding;
        internal ulong Owner;
        internal uint Connection;
        internal IntPtr Pointer;
        internal Guid Nonce, Token = Guid.NewGuid(), Lease;
        internal int Seat = -1;
        internal SeatPhase Phase;
        internal byte Door;
        internal Vector3? Exit;
        internal bool EmergencyExit;
        internal long LastCommand, LastControlSequence, LastControl, BoardedAt, LastSeen;
        internal long DoorReadyAt, TransitionExpiresAt, LastSeatSequence;
        internal long NextExitEvidence;
        internal long NextBindingRetry;
        internal DepartureEvidence? Departure;
        internal AircraftControls Input = AircraftControls.Parked;
        internal string Result = "Ready.";
        internal readonly Rate Commands = new(), Controls = new(), Packets = new();
    }
    private sealed class Rate
    {
        private double _tokens;
        private long _last;
        internal bool Take(long now, double rate, double capacity)
        {
            _tokens = _last == 0 ? capacity : Math.Min(capacity, _tokens + Math.Max(0, now - _last) * rate / 1000);
            _last = now;
            if (_tokens < 1) return false;
            _tokens--; return true;
        }
    }
    private enum ActionCode : byte
    {
        Deploy, Pilot, Passenger, Exit, Service, Recover, Cancel,
        SitPilot, SitPassengerFront, SitPassengerRear,
        OpenPortDoor, OpenStarboardDoor, ClosePortDoor, CloseStarboardDoor
    }
    private sealed class Pending(ActionCode action, long sequence)
    {
        internal readonly ActionCode Action = action;
        internal readonly long Sequence = sequence;
        internal int Attempts = 1;
        internal long Next = Now + 500;
    }
}
