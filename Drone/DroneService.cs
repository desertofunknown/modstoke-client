// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using Modstoke.Sons;
using Sons.Save;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientDroneService
{
    private const int MaxOwners = 128;
    private const int MaxPeers = 16;
    private const float InteractionRange = 4;
    private const float RadioRange = 180;
    private const long ControlTimeout = 800;
    private const long PilotConnectTimeout = 3000;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly Dictionary<string, Kit> _kits = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, Peer> _peers = new();
    private readonly Peer[] _peerSnapshot = new Peer[MaxPeers];
    private bool _peerSnapshotInUse;
    private readonly Dictionary<string, Replica> _replicas = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _removed = new(StringComparer.Ordinal);
    private DroneEvent? _event;
    private IDisposable? _saveRegistration;
    private WorldRecordContext? _world;
    private Guid _epoch;
    private Guid _nonce;
    private Guid _token;
    private Guid _clientLease;
    private IntPtr _serverPointer;
    private uint _serverConnection;
    private IntPtr _saveManager;
    private bool _started;
    private bool _ready;
    private bool _loading;
    private bool _offlineLoadComplete;
    private bool _loadInvalid;
    private bool _pilotRequested;
    private bool _pilotStartPending;
    private long _pilotStartSequence;
    private DronePilotInput _input;
    private long _inputAt;
    private long _nextNetwork;
    private long _nextHello;
    private long _lastOwnerSnapshot;
    private long _lastClientSnapshot;
    private long _nextPersistence;
    private long _commandSequence;
    private long _controlSequence;
    private long _snapshotSequence;
    private string _localOwner = "";
    private string _status = "Waiting for a world.";
    private SavedFleet? _pendingOffline;
    private Peer? _localPeer;
    private ClientDroneView _remoteView = ClientDroneView.Unavailable("Waiting for drone handshake.");

    internal ClientDroneView Snapshot { get; private set; } = ClientDroneView.Unavailable("Waiting for a world.");
    internal ClientDroneView InventoryKit => Snapshot;
    internal bool PilotRequested => _pilotRequested;
    internal Texture? CameraFeed => ViewedBody?.CameraFeed(true);

    private bool OnThread => Environment.CurrentManagedThreadId == _thread;
    private static bool IsClient => BoltNetwork.isRunning && BoltNetwork.isClient && !BoltNetwork.isServer;
    private static long Now => Environment.TickCount64;

    internal void Start()
    {
        if (!OnThread || _started) return;
        if (!SonsSdk.Networking.Packets.SupportsClientRelayPolicy)
        {
            Snapshot = ClientDroneView.Unavailable("Update Modstoke Loader and SDK to use drones.");
            return;
        }
        _event = new DroneEvent(this);
        _transportMode = !BoltNetwork.isRunning ? 0 : BoltNetwork.isServer ? 1 : 2;
        SonsSdk.Networking.Packets.Register(_event);
        _started = true;
        SetOfflineRegistration(!BoltNetwork.isRunning);
        SaveLifecycle.Lifecycle += OnSaveLifecycle;
        GameRuntime.WorldChanged += OnWorldChanged;
    }

    internal void Stop()
    {
        if (!OnThread || !_started) return;
        StoreWorld();
        _started = false;
        SaveLifecycle.Lifecycle -= OnSaveLifecycle;
        GameRuntime.WorldChanged -= OnWorldChanged;
        _saveRegistration?.Dispose();
        _saveRegistration = null;
        if (_event != null) SonsSdk.Networking.Packets.Unregister(_event);
        _event = null;
        ResetWorld();
        ResetRemoteTerrainSurvey();
    }

    internal void Update(ClientTabletPreferencesSnapshot? audioPreferences = null)
    {
        if (!OnThread || !_started) return;
        if (!RefreshWorld())
        {
            ResetRemoteTerrainSurvey();
            Snapshot = ClientDroneView.Unavailable(_status);
            HidePlacement();
            return;
        }
        RefreshTerrainSurveySession();
        var now = Now;
        if (IsClient)
        {
            UpdateClient(now, audioPreferences);
            UpdatePlacement(now);
            return;
        }
        RefreshLocalPeer();
        // Nested callbacks retain their own snapshot until their update finishes.
        var ownsPeerSnapshot = !_peerSnapshotInUse && _peers.Count <= _peerSnapshot.Length;
        var peers = ownsPeerSnapshot ? _peerSnapshot : _peers.Values.ToArray();
        var peerCount = ownsPeerSnapshot ? 0 : peers.Length;
        if (ownsPeerSnapshot) _peerSnapshotInUse = true;
        try
        {
            if (ownsPeerSnapshot)
            {
                foreach (var peer in _peers.Values)
                {
                    peers[peerCount] = peer;
                    peerCount++;
                }
            }
            for (var i = 0; i < peerCount; i++)
            {
                var peer = peers[i];
                if (!PeerCurrent(peer)) { EndLease(peer); _peers.Remove(peer.Session!.SessionId); continue; }
                if (peer.Binding == null || !ServerWorld.TryValidatePlayerBinding(peer.Binding, out _))
                {
                    EndLease(peer);
                    peer.Binding = ServerWorld.TryCapturePlayerBinding(peer.Session!, out var binding, out _) ? binding : null;
                }
            }
        }
        finally
        {
            Array.Clear(peers, 0, peerCount);
            if (ownsPeerSnapshot) _peerSnapshotInUse = false;
        }
        var solarHour = ReadSolarHour();
        foreach (var kit in _kits.Values)
        {
            var body = kit.Body;
            if (body == null) continue;
            if (!body.Root)
            {
                var pilot = FindPilot(kit.Owner);
                if (pilot != null) EndLease(pilot);
                ResetObservations(kit);
                ResetTerrainSurvey(kit);
                continue;
            }
            body.Update(Mathf.Min(Time.deltaTime, .1f), audioPreferences);
            SyncKit(kit);
            UpdateSolar(kit, now, solarHour);
            kit.Native?.Update(body.Armed);
        }
        if (now >= _nextNetwork)
        {
            _nextNetwork = now + 100;
            SendSnapshots(now);
        }
        if (now >= _nextPersistence) { _nextPersistence = now + 1000; StoreWorld(); }
        _kits.TryGetValue(_localOwner, out var local);
        if (local != null && (local.Body == null || local.Body.Root)) UpdateTerrainObservation(local, now);
        Snapshot = local != null ? View(local, _localPeer, _status) : ClientDroneView.Unavailable(_status);
        if (local != null)
        {
            if (!Snapshot.Deployed || _lastContact?.DroneId != Snapshot.DroneId) _lastContact = null;
            if (HasRadioContact(Snapshot) && local.Body != null) RecordContact(local.Id, local.Body.Position, now);
            Snapshot = Snapshot with { LastContact = _lastContact };
        }
        UpdatePlacement(now);
    }

    internal void FixedUpdate()
    {
        if (!OnThread || !_started || !_ready || _loading) return;
        if (IsClient)
        {
            foreach (var replica in _replicas.Values) replica.Body.FixedUpdate(DronePilotInput.Neutral, false);
            return;
        }
        var now = Now;
        if (_localPeer != null && _pilotRequested && now - _inputAt <= ControlTimeout)
            ApplyControl(_localPeer, _localPeer.Lease, ++_controlSequence, _input);
        foreach (var kit in _kits.Values)
        {
            if (kit.Body == null) continue;
            var pilot = FindPilot(kit.Owner);
            if (!kit.Body.Root)
            {
                if (pilot != null) EndLease(pilot);
                continue;
            }
            var controlPeer = pilot ?? kit.ControlPeer;
            var linked = controlPeer != null && TryActor(controlPeer, out var position, out _)
                && Vector3.Distance(position, kit.Body.Position) <= RadioRange;
            var fresh = pilot != null && linked
                && (now - pilot.LastControl <= ControlTimeout || now < pilot.ControlPendingUntil);
            // An expired input lease cannot retain a climb or bank command. A live
            // owner keeps position hold; only loss of the radio owner requires landing.
            if (!fresh && pilot != null) EndLease(pilot, linked ? LeaseEnd.Hold : LeaseEnd.LinkLost);
            else if (!linked && controlPeer != null) EndLease(controlPeer);
            var input = fresh ? pilot!.Input : DronePilotInput.Neutral;
            if (fresh && now < pilot!.TakeoffUntil && input.Climb == 0) input = input with { Climb = .5f };
            kit.Body.FixedUpdate(input, linked);
            SyncKit(kit);
        }
    }

    internal void SetPilot(bool active, DronePilotInput input)
    {
        if (!OnThread || !_started) return;
        _inputAt = Now;
        _input = ValidInput(input) ? input : DronePilotInput.Neutral;
        // The view has not received its first lease snapshot yet. A neutral UI frame
        // must not cancel the explicit connect command while it is in flight.
        if (!active && _pilotStartPending && _viewVisible) return;
        if (_pilotRequested == active) return;
        _pilotRequested = active;
        if (!active) CancelPilotStart();
        Submit(active ? "pilot-start" : "pilot-stop");
        if (active) _pilotRequested = IsClient ? _pilotStartPending : _localPeer != null && _localPeer.Lease != Guid.Empty;
        if (!IsClient && _kits.TryGetValue(_localOwner, out var local))
            Snapshot = View(local, _localPeer, _status) with { LastContact = _lastContact };
        if (!active) DisableCameras();
    }

    internal string Command(string action)
    {
        if (!OnThread || !_started) return "Drone service is unavailable.";
        if (action == "pilot")
        {
            // A deliberate disconnect must bypass the neutral-frame pending guard.
            var resumeLanding = Snapshot.Mode == "Landing";
            if (_pilotRequested) CancelPilotStart();
            if (resumeLanding) _pilotRequested = false;
            SetPilot(resumeLanding || !_pilotRequested, DronePilotInput.Neutral);
            return _status;
        }
        if (action == "land") { _pilotRequested = false; CancelPilotStart(); DisableCameras(); }
        return Submit(action);
    }

    private void CancelPilotStart()
    {
        _pilotStartPending = false;
        if (_pilotStartSequence > 0) _pendingCommands.Remove(_pilotStartSequence);
        _pilotStartSequence = 0;
    }

    private string Submit(string action)
    {
        if (!_ready) return _status = "Wait for the current world and drone handshake.";
        if (!TryAction(action, out var command)) return _status = "Unknown drone command.";
        if (IsClient)
        {
            if (_token == Guid.Empty || Now - _lastOwnerSnapshot > 1500)
                return _status = "Drone link is unavailable.";
            var sequence = ++_commandSequence;
            if (_event?.Request(command, sequence) != true) return _status = "Wait for pending drone commands.";
            if (command == ActionCode.PilotStart) { _pilotStartPending = true; _pilotStartSequence = sequence; }
            else if (command is ActionCode.PilotStop or ActionCode.Land) _pilotStartPending = false;
            return _status = "Command sent; waiting for the host.";
        }
        RefreshLocalPeer();
        if (_localPeer == null) return _status = "A living local player is required.";
        return _status = ApplyCommand(_localPeer, ++_commandSequence, command);
    }

    private string ApplyCommand(Peer peer, long sequence, ActionCode command)
    {
        if (!PeerCurrent(peer) || sequence <= 0) return "The player session changed.";
        if (sequence == peer.LastCommand) return peer.LastResult;
        if (sequence < peer.LastCommand) return "Old command rejected.";
        // Releasing this owner's controls must remain available after a burst of commands.
        if (command != ActionCode.PilotStop && !peer.Commands.Take(Now, 4, 4))
            return "Wait before sending another command.";
        peer.LastCommand = sequence;
        return peer.LastResult = ExecuteCommand(peer, command);
    }

    private string ExecuteCommand(Peer peer, ActionCode command)
    {
        if (!_kits.TryGetValue(peer.Owner, out var kit)) return "Drone inventory is unavailable.";
        if (command == ActionCode.PilotStop)
        {
            var linked = kit.Body is { } heldBody && heldBody.Root && TryActor(peer, out var ownerPosition, out _)
                && Vector3.Distance(ownerPosition, heldBody.Position) <= RadioRange;
            EndLease(peer, linked ? LeaseEnd.Hold : LeaseEnd.LinkLost);
            if (!linked) return "Link lost. Landing requested.";
            if (kit.Body?.Armed != true) return "Controls released.";
            return kit.Body.Mode is "Hovering" or "Ready"
                ? "Controls released. The drone will hold position." : kit.Body.Mode + ".";
        }
        if (command == ActionCode.Land)
        {
            EndLease(peer, LeaseEnd.Land);
            if (kit.Body?.Armed != true) return "The drone motors are stopped.";
            return kit.Body.Mode == "Landing"
                ? "Landing requested. Resume controls to cancel a manual landing." : kit.Body.Mode + ".";
        }
        if (!TryActor(peer, out var position, out var rotation)) { EndLease(peer); return "A living player is required."; }
        if (command == ActionCode.Deploy)
        {
            if (kit.Body != null || kit.Deployed) return "The drone is already deployed.";
            if (_kits.Values.Count(value => value.Body != null) >= MaxPeers) return "The world drone limit is reached.";
            if (!TryDeploymentPose(position, rotation, out var spawn, out var spawnRotation, out var reason))
                return reason;
            if (!CreateBody(kit, spawn, spawnRotation)) return "The drone could not be deployed; inventory is unchanged.";
            StoreWorld();
            return "Drone deployed. Let it settle, then connect controls and arm the motors.";
        }
        if (kit.Body == null) return "Deploy the drone first.";
        var body = kit.Body;
        if (!body.Root) { EndLease(peer); return "The deployed drone is unavailable."; }
        var distance = Vector3.Distance(position, body.Position);
        if (command == ActionCode.PilotStart)
        {
            if (distance > RadioRange || body.Health <= 0 || kit.Solar) return "The drone cannot establish a flight link here.";
            if (!body.SolarFolded) return "Wait for the solar panels to finish folding.";
            if (!body.ResumeControl()) return "A safety landing is in progress. Wait for touchdown before reconnecting.";
            kit.ControlPeer = peer;
            if (peer.Lease == Guid.Empty)
            {
                peer.Lease = Guid.NewGuid();
                peer.Input = DronePilotInput.Neutral;
                peer.LastControl = Now;
                // The guest must receive the grant before it can send a lease-bound control.
                peer.ControlPendingUntil = peer.Session != null ? Now + PilotConnectTimeout : 0;
            }
            return "Pilot link connected.";
        }
        if (distance > InteractionRange) return "Move within four metres of your drone.";
        if (command is ActionCode.Arm or ActionCode.Takeoff)
        {
            if (kit.Solar) return "Fold the solar panels before arming.";
            if (!body.SolarFolded) return "Wait for the solar panels to finish folding.";
            if (peer.Lease == Guid.Empty) return "Connect pilot controls before arming.";
            body.Arm();
            if (body.Armed && command == ActionCode.Takeoff) peer.TakeoffUntil = Now + 1500;
            return body.Armed ? "Motors armed. Climb to take off." : "Arming requires a healthy drone, charged pack and level ground.";
        }
        if (body.Armed || !body.Grounded || body.Velocity.sqrMagnitude > .25f)
            return "Land and stop the drone before handling it.";
        if (body.SolarMoving) return "Wait for the solar panels to finish moving.";
        if (command == ActionCode.Recover)
        {
            if (!body.SolarFolded) return "Fold the solar panels before packing the drone.";
            EndLease(peer);
            SyncKit(kit);
            RemoveBody(kit);
            StoreWorld();
            return "Drone returned to inventory.";
        }
        if (command == ActionCode.ReplacePack)
        {
            if (kit.Solar) return "Fold the solar panels before swapping packs.";
            SyncKit(kit);
            kit.Installed = 1 - kit.Installed;
            body.BatteryCharge = kit.Installed == 0 ? kit.ChargeA : kit.ChargeB;
            StoreWorld();
            return "Rechargeable pack swapped.";
        }
        if (command == ActionCode.Solar)
        {
            EndLease(peer);
            kit.Solar = !kit.Solar;
            kit.SolarObstructionAvailable = false;
            body.SetSolarDeployed(kit.Solar);
            return kit.Solar ? "Unfolding solar panels. Charging requires daylight and clear sky." : "Folding solar panels.";
        }
        return "Unsupported drone command.";
    }

    private void ApplyControl(Peer peer, Guid lease, long sequence, DronePilotInput input)
    {
        if (!PeerCurrent(peer) || lease == Guid.Empty || lease != peer.Lease || sequence <= peer.LastControlSequence
            || !ValidInput(input) || !peer.Controls.Take(Now, 35, 8)) return;
        peer.LastControlSequence = sequence;
        if (!_kits.TryGetValue(peer.Owner, out var kit) || kit.Body == null || !kit.Body.Root || kit.Solar
            || !TryActor(peer, out var position, out _) || Vector3.Distance(position, kit.Body.Position) > RadioRange)
        { EndLease(peer); return; }
        peer.Input = input;
        peer.LastControl = Now;
        peer.ControlPendingUntil = 0;
    }

    private enum LeaseEnd { LinkLost, Hold, Land }

    private void EndLease(Peer peer, LeaseEnd reason = LeaseEnd.LinkLost)
    {
        if (peer.Lease != Guid.Empty && peer.LastResult == "Pilot link connected.")
            peer.LastResult = "Pilot controls released. Connect controls again.";
        peer.Lease = Guid.Empty;
        peer.ControlPendingUntil = 0;
        peer.TakeoffUntil = 0;
        peer.Input = DronePilotInput.Neutral;
        if (ReferenceEquals(peer, _localPeer))
        {
            _pilotRequested = false;
            _input = DronePilotInput.Neutral;
            _inputAt = 0;
        }
        // A departed connection must not change a replacement session's flight.
        if (FindPilot(peer.Owner) != null || !_kits.TryGetValue(peer.Owner, out var kit)
            || kit.ControlPeer != null && !ReferenceEquals(kit.ControlPeer, peer)) return;
        kit.ControlPeer = reason == LeaseEnd.LinkLost ? null : peer;
        if (reason == LeaseEnd.Hold) kit.Body?.Hold();
        else kit.Body?.Land(reason == LeaseEnd.Land ? ClientDroneFirmware.LandingReason.Manual
            : ClientDroneFirmware.LandingReason.LinkLost);
    }

    private Peer? FindPilot(string owner)
    {
        if (_localPeer?.Owner == owner && _localPeer.Lease != Guid.Empty && PeerCurrent(_localPeer)) return _localPeer;
        foreach (var peer in _peers.Values)
            if (peer.Owner == owner && peer.Lease != Guid.Empty && PeerCurrent(peer)) return peer;
        return null;
    }

    private bool CreateBody(Kit kit, Vector3 position, Quaternion rotation)
    {
        ClientDroneBody? body = null;
        ClientDroneNative? native = null;
        try
        {
            body = new ClientDroneBody(kit.Id, position, rotation, true);
            body.BatteryCharge = kit.Installed == 0 ? kit.ChargeA : kit.ChargeB;
            body.Health = kit.Health;
            native = new ClientDroneNative(body.Root, damage =>
            {
                if (!float.IsFinite(damage) || damage <= 0 || kit.Body != body) return;
                body.Health = Mathf.Clamp01(body.Health - damage);
                if (body.Health <= 0) body.StopMotors();
            }, body.Health);
        }
        catch (Exception error)
        {
            native?.Dispose();
            body?.Dispose();
            Console.WriteLine("Modstoke drone deployment failed (" + error.GetType().Name + ").");
            return false;
        }
        kit.Body = body;
        kit.ControlPeer = null;
        kit.Native = native;
        kit.Deployed = true;
        kit.Position = position;
        kit.Rotation = rotation;
        kit.Solar = false;
        ResetObservations(kit);
        ResetTerrainSurvey(kit);
        _removed.Remove(kit.Id);
        return true;
    }

    private void RemoveBody(Kit kit)
    {
        kit.Native?.Dispose();
        kit.Native = null;
        kit.Body?.Dispose();
        kit.Body = null;
        kit.ControlPeer = null;
        kit.Deployed = false;
        kit.Solar = false;
        ResetObservations(kit);
        ResetTerrainSurvey(kit);
        _removed[kit.Id] = Now + 3000;
    }

    private static void SyncKit(Kit kit)
    {
        // A destroyed native root exposes fallback zero/identity values. Keep the
        // last live pose and deployed ownership until the world can restore it.
        if (kit.Body == null || !kit.Body.Root) return;
        kit.Position = kit.Body.Position;
        kit.Rotation = kit.Body.Rotation;
        kit.Health = Mathf.Clamp01(kit.Body.Health);
        if (kit.Installed == 0) kit.ChargeA = Mathf.Clamp01(kit.Body.BatteryCharge);
        else kit.ChargeB = Mathf.Clamp01(kit.Body.BatteryCharge);
    }

    private ClientDroneView View(Kit kit, Peer? peer, string status)
    {
        var body = kit.Body;
        if (body != null && !body.Root) body = null;
        var actorPosition = Vector3.zero;
        var actorAvailable = peer != null && peer.Owner == kit.Owner && TryActor(peer, out actorPosition, out _);
        var range = body != null && actorAvailable ? Vector3.Distance(actorPosition, body.Position) : 0;
        var linked = body != null && actorAvailable && range <= RadioRange;
        var terrainSurvey = ObserveHostTerrainSurvey(kit, linked, Now);
        var clearanceAvailable = false;
        var altitude = 0f;
        if (body != null && Physics.Raycast(body.Position, Vector3.down, out var hit, 1000, ~0, QueryTriggerInteraction.Ignore))
        {
            clearanceAvailable = true;
            altitude = hit.distance;
        }
        return new ClientDroneView(true, status, !kit.Deployed, 1, kit.Deployed, body?.Armed ?? false,
            linked && peer != null && peer.Lease != Guid.Empty, kit.Installed == 0 ? kit.ChargeA : kit.ChargeB,
            kit.Health, body?.Velocity.magnitude ?? 0, altitude, range,
            body?.SolarMoving == true ? body.SolarStatus
                : kit.Solar ? kit.Charging ? "Solar charging" : "Solar waiting" : body?.Mode ?? (kit.Deployed ? "Unavailable" : "Stored"), kit.Charging,
            kit.Id, kit.Installed == 0 ? kit.PackA : kit.PackB, kit.PackA, kit.ChargeA, kit.Installed == 0,
            kit.PackB, kit.ChargeB, kit.Installed == 1, linked,
            body != null && linked
                ? new ClientDroneReadiness(body.Grounded, body.Root.transform.up.y >= ClientDroneFirmware.MinimumArmUp, kit.Solar)
                : null,
            SurfaceClearanceAvailable: clearanceAvailable, LoadedTerrainHeight: kit.LoadedTerrainHeight,
            SolarStatus: kit.SolarStatus, LoadedTerrainObservedAt: kit.LoadedTerrainObservedAt,
            TerrainSurvey: terrainSurvey);
    }

    private void DisableCameras()
    {
        foreach (var kit in _kits.Values) kit.Body?.CameraFeed(false);
        foreach (var replica in _replicas.Values) replica.Body.CameraFeed(false);
    }

    private static bool ValidInput(DronePilotInput value) => FiniteAxis(value.Forward) && FiniteAxis(value.Right)
        && FiniteAxis(value.Climb) && FiniteAxis(value.Yaw);
    private static bool FiniteAxis(float value) => float.IsFinite(value) && Math.Abs(value) <= 1;
    private static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z)
        && Math.Abs(value.x) <= 100000 && Math.Abs(value.y) <= 100000 && Math.Abs(value.z) <= 100000;

    private sealed class Kit
    {
        internal string Owner = "";
        internal string Id = Guid.NewGuid().ToString("N");
        internal string PackA = Guid.NewGuid().ToString("N");
        internal string PackB = Guid.NewGuid().ToString("N");
        internal float ChargeA = 1, ChargeB = 1, Health = 1;
        internal int Installed;
        internal bool Deployed, Solar, Charging;
        internal Guid SolarObservationEpoch, TerrainObservationEpoch;
        internal long NextSolarObstruction, NextTerrainObservation, LoadedTerrainObservedAt;
        internal bool SolarObstructionAvailable, SolarObstructed;
        internal string SolarStatus = "";
        internal float? LoadedTerrainHeight;
        internal Vector3 SolarObservationPosition, TerrainObservationPosition;
        internal Guid TerrainSurveyEpoch;
        internal long NextTerrainSurvey;
        internal ClientDroneTerrainSurvey? TerrainSurvey;
        internal Vector3 Position;
        internal Quaternion Rotation = Quaternion.identity;
        internal ClientDroneBody? Body;
        internal Peer? ControlPeer;
        internal ClientDroneNative? Native;
    }

    private sealed class Peer(string owner, ulong steamId)
    {
        internal ServerSession? Session;
        internal PlayerHandle? Binding;
        internal HostPlayer? Host;
        internal readonly string Owner = owner;
        internal readonly ulong SteamId = steamId;
        internal IntPtr Pointer;
        internal uint Connection;
        internal Guid Nonce, Token = Guid.NewGuid(), Lease;
        internal long LastCommand, LastControlSequence, LastControl, TakeoffUntil;
        internal long ControlPendingUntil;
        internal long LastTerrainSurveyAt;
        internal string LastResult = "Ready.";
        internal DronePilotInput Input;
        internal readonly Rate Commands = new(), Controls = new(), Packets = new();
    }

    private sealed class Replica(ClientDroneBody body)
    {
        internal readonly ClientDroneBody Body = body;
        internal long Sequence, LastSeen;
    }

    private sealed class Rate
    {
        private double _tokens;
        private long _last;
        internal bool Take(long now, double perSecond, double capacity)
        {
            _tokens = _last == 0 ? capacity : Math.Min(capacity, _tokens + Math.Max(0, now - _last) * perSecond / 1000);
            _last = now;
            if (_tokens < 1) return false;
            _tokens--;
            return true;
        }
    }

    private enum ActionCode : byte { Deploy, Arm, Takeoff, Land, Recover, ReplacePack, Solar, PilotStart, PilotStop }
    private static bool TryAction(string value, out ActionCode command)
    {
        command = value switch { "deploy" => ActionCode.Deploy, "arm" => ActionCode.Arm, "takeoff" => ActionCode.Takeoff,
            "land" => ActionCode.Land, "recover" => ActionCode.Recover, "replace-pack" => ActionCode.ReplacePack,
            "solar-charge" => ActionCode.Solar, "pilot-start" => ActionCode.PilotStart, "pilot-stop" => ActionCode.PilotStop,
            _ => (ActionCode)255 };
        return command != (ActionCode)255;
    }
}
