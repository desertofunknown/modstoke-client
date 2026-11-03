// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Endnight.Extensions;
using Modstoke.Sons;
using Sons.Gameplay.GameSetup;
using Sons.Save;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftService
{
    private WorldRecordContext? _world;
    private IntPtr _saveManager;
    private int _transportMode = -1;
    private bool _loading;

    private void StartLifecycle()
    {
        _transportMode = !BoltNetwork.isRunning ? 0 : BoltNetwork.isServer ? 1 : 2;
        SetOfflineRegistration(_transportMode == 0);
        SaveLifecycle.Lifecycle += OnSaveLifecycle;
        GameRuntime.WorldChanged += OnWorldChanged;
    }

    private void StopLifecycle()
    {
        SaveLifecycle.Lifecycle -= OnSaveLifecycle;
        GameRuntime.WorldChanged -= OnWorldChanged;
        SetOfflineRegistration(false);
        _transportMode = -1;
    }

    private bool RefreshWorld()
    {
        var mode = !BoltNetwork.isRunning ? 0 : BoltNetwork.isServer ? 1 : 2;
        if (_transportMode != mode)
        {
            ResetWorld(preservePendingLoad: _loading || !_ready && _offlineLoadComplete);
            _transportMode = mode;
            if (!_loading) SetOfflineRegistration(mode == 0);
        }
        if (_loading) { _status = "Waiting for world loading to finish."; return false; }
        if (GameRuntime.IsTitleScene)
        {
            if (_ready || _body != null || _expedition.Active) ResetWorld();
            _status = "Waiting for a playable world."; return false;
        }
        if (!SaveGameManager.TryGetInstance(out var manager) || manager == null || manager.WasCollected || !manager)
        {
            _status = "Waiting for the current world's save manager."; return false;
        }
        if (manager._loadingInProgress)
        {
            if (_ready || _body != null || _expedition.Active) ResetWorld();
            _status = "Waiting for world loading to finish."; return false;
        }
        if (_saveManager != IntPtr.Zero && _saveManager != manager.Pointer) ResetWorld();
        _saveManager = manager.Pointer;
        if (IsClient)
        {
            var server = BoltNetwork.server;
            if (server == null || server.WasCollected)
            { _status = "Waiting for the current server connection."; return false; }
            if (_serverPointer != server.Pointer || _serverConnection != server.ConnectionId)
            {
                ResetWorld(); _saveManager = manager.Pointer;
                _serverPointer = server.Pointer; _serverConnection = server.ConnectionId;
                _nonce = Guid.NewGuid();
            }
            return true;
        }
        if (BoltNetwork.isRunning)
        {
            if (!WorldRecords.TryGetCurrent(out var world, out var status))
            {
                if (status == WorldRecordStatus.InvalidSavedData) return InvalidSave();
                _status = "Waiting for current world identity: " + status + "."; return false;
            }
            if (!ReferenceEquals(_world, world))
            {
                ResetWorld(); _saveManager = manager.Pointer; _world = world;
                var read = WorldRecords.ReadJson(world, SaveOwner, SaveName, out var json);
                if (read != WorldRecordStatus.NotFound
                    && (read != WorldRecordStatus.Success || !LoadShip(json, world.WorldId)))
                    return InvalidSave();
                BeginAuthority();
            }
            return _ready && !_loadInvalid;
        }
        if (_loadInvalid) return false;
        if (!_ready)
        {
            if (!LocalAlive() || LocalPlayer.Inventory == null || LocalPlayer.Inventory.WasCollected || !LocalPlayer.Inventory)
            { _status = "Waiting for the local player."; return false; }
            if (GameSetupManager.GameLoadedFromSave() && !_offlineLoadComplete)
            { _status = "Waiting for this world's spacecraft save record."; return false; }
            if (_pendingOffline != null)
            {
                if (!RestoreShipRecord(_pendingOffline, _pendingOffline.World)) return InvalidSave();
                _offlineWorld = _pendingOffline.World;
                _pendingOffline = null;
            }
            if (_offlineWorld == Guid.Empty) _offlineWorld = Guid.NewGuid();
            BeginAuthority();
        }
        return _ready;
    }

    private void BeginAuthority()
    {
        _orbits ??= new(OrbitalStationOrbit.Bodies(OrbitalCatalog.Current));
        _epoch = Guid.NewGuid(); _ready = true;
        _status = _restoreNavigation ? "Restoring the saved spacecraft."
            : _savedShip == null ? "Spaceflight ready. One shared spacecraft belongs to this world."
            : "Spaceflight ready. The ship's saved reserves and condition are available for deployment.";
    }

    private void OnWorldChanged(WorldEvent value)
    {
        if (!OnThread || !_started || _loading || value.IsInWorld || !GameRuntime.IsTitleScene) return;
        ResetWorld();
    }

    private void OnSaveLifecycle(SaveLifecycleEvent value)
    {
        if (!OnThread || !_started) return;
        if (value.Operation == "Load" && value.Phase == "Before")
        {
            ResetWorld(); _loading = true;
            SetOfflineRegistration(!value.SaveGameType.StartsWith("Multiplayer", StringComparison.OrdinalIgnoreCase));
        }
        else if (value.Operation == "Load" && value.Phase == "After")
        {
            _loading = false;
            _offlineLoadComplete = value.Success == true
                && !value.SaveGameType.StartsWith("Multiplayer", StringComparison.OrdinalIgnoreCase);
            if (value.Success != true) { _pendingOffline = null; InvalidSave(); }
        }
        else if (value.Operation == "Save" && value.Phase == "Before")
        {
            // Keep an invalid offline registration alive: the registry retains
            // failed-load protection for that registration and save slot.
            SetOfflineRegistration(!BoltNetwork.isRunning);
            StoreWorld();
        }
    }

    private void ResetWorld(bool preservePendingLoad = false)
    {
        var loading = preservePendingLoad && _loading;
        var complete = preservePendingLoad && _offlineLoadComplete;
        var pending = preservePendingLoad ? _pendingOffline : null;
        var invalid = preservePendingLoad && _loadInvalid;
        ReturnLocal("World changed. The previous expedition was closed.");
        _worldAvailable = _serviceHeld = _holdPrepared = false;
        _heldAt = _retryServiceAt = _nextFaultLog = _nextHoldLog = 0;
        _faultOperation = null;
        _faultSignature = "";
        _faultRepeats = 0;
        ResetInteractions();
        ResetMovementEvidence();
        _attachmentRecoverySequence = _receivedAttachmentRecovery = 0;
        _surfaceRecoverySequence = _receivedSurfaceRecovery = 0;
        _surfaceLease = Guid.Empty;
        _awaitingAttachmentRecovery = false;
        _cockpit?.Dispose(); _cockpit = null;
        _ready = false;
        _peers.Clear(); _crew.Clear(); Array.Clear(_seats); Array.Clear(_actorIds); Array.Clear(_walking);
        Array.Clear(_interactions); Array.Clear(_interactionUntil);
        _local = null; _pending = null; _clientSeat = -1; _clientExit = null;
        _clientWalking = false; _clientCabinStandLocal = null;
        _clientReturn = _handledClientReturn = false;
        _boardingCommand = null; _assetsLoading = false;
        _cancelBoardingRequested = false;
        _shipId = _epoch = _nonce = _token = _clientLease = _ridingLease = _rejectedLease = Guid.Empty;
        _helloEpoch = _helloToken = Guid.Empty;
        _helloSequence = _pendingHelloSequence = 0;
        RemoveBody();
        _systems.Reset();
        ResetStationSystems();
        ResetEva();
        ResetOrbits();
        ReleaseOrbitalStation();
        _stationFailed = false;
        _savedShip = null;
        _offlineWorld = Guid.Empty;
        _loading = loading;
        _offlineLoadComplete = complete;
        _pendingOffline = pending;
        _loadInvalid = invalid;
        _world = null; _saveManager = _serverPointer = IntPtr.Zero; _serverConnection = 0;
        _travel = SpacecraftTravel.None; _travelStarted = 0;
        _nextHello = _nextSnapshot = _nextControl = _nextSeatStatus = _lastSnapshot = 0;
        _commandSequence = _controlSequence = _seatSequence = _snapshotSequence = _receivedSequence = 0;
        _frameRevision = 0;
        _receivedFlightTime = 0;
        _cancelledBoardingSequence = 0;
        try { _destination.Dispose(); }
        finally { _destination = new ClientSpacecraftWorld(); }
        _status = "Waiting for the current world.";
    }

    private void ClearClientShip()
    {
        _cancelBoardingRequested = false;
        _receivedAttachmentRecovery = 0;
        _receivedSurfaceRecovery = 0;
        _surfaceLease = Guid.Empty;
        _awaitingAttachmentRecovery = false;
        ResetPortableTransport();
        ResetHullTransport();
        ReleaseOrbitalStation();
        _stationFailed = false;
        _evaState = null; _lastEvaAt = 0;
        _clientSeat = -1; _clientLease = _ridingLease = Guid.Empty;
        _clientExit = null; _clientReturn = _handledClientReturn = false; Array.Clear(_actorIds);
        _clientWalking = false; _clientCabinStandLocal = null; Array.Clear(_walking);
        Array.Clear(_interactions);
        _shipId = Guid.Empty; RemoveBody();
    }

    private void RemoveBody()
    {
        CancelOrbitalStationPreparation();
        ResetManualRepairs();
        CancelShoreServicePress();
        _shoreServiceRequest = 0;
        _shoreServiceFeedbackUntil = 0;
        _systems.SetShoreConnection(Guid.Empty, Guid.Empty);
        Array.Clear(_apuGuardContacts);
        ReleaseFirefightingViews();
        DisposeToolRack();
        DisposeShipFirefighting();
        _fireEffects?.Dispose(); _fireEffects = null;
        DisposeEvaViews();
        Array.Clear(_evaLeases); _evaState = null; _nextEvaSnapshot = 0;
        HideOrbitalStation();
        _stationAssist = false;
        _spaceFrame = null; _spaceFrameTime = 0; _spaceCruiseVelocity = default;
        _restoreNavigation = false;
        _nextRestoreAttempt = 0;
        ResetAccess();
        _storage?.Dispose(); _storage = null;
        _nextCargoAttach = _cargoAttachUntil = 0;
        _stations?.Dispose(); _stations = null;
        Array.Clear(_consolePoses);
        var body = _body; _body = null;
        if (body != null)
        {
            body.Impact -= OnShipImpact;
            body.CollisionImpact -= OnStationCollision;
        }
        var audio = _audio; _audio = null;
        var effects = _effects; _effects = null;
        var shorePower = _shorePower; _shorePower = null;
        try { shorePower?.Dispose(); }
        catch (Exception error) { Console.WriteLine("Spacecraft shore power cleanup: " + error); }
        try { audio?.Dispose(); }
        catch (Exception error) { Console.WriteLine("Spacecraft audio cleanup: " + error.Message); }
        try { effects?.Dispose(); }
        catch (Exception error) { Console.WriteLine("Spacecraft effect cleanup: " + error.Message); }
        try { _cabin.Reset(); }
        catch (Exception error) { Console.WriteLine("Spacecraft cabin cleanup: " + error.Message); }
        if (body == null) return;
        try { _occupants?.Remove(body); }
        catch (Exception error) { Console.WriteLine("Spacecraft occupant cleanup: " + error.Message); }
        try { body.Dispose(); }
        catch (Exception error) { Console.WriteLine("Spacecraft body cleanup: " + error.Message); }
    }

    private bool ReturnLocal(string message)
    {
        _surfaceLease = Guid.Empty;
        var active = _expedition.Active;
        if (active && !_expedition.ReturnRequested) Console.WriteLine("Spacecraft island return: " + message);
        if (!IsClient) PrepareReturnMovement(_local);
        else if (active && _ready && !_clientReturn) CancelLocalSeat();
        // End retains same-actor protection in the rider; the guard independently
        // checks world, native object and life identity before moving a player.
        try { _rider?.End(active ? _expedition.ReturnPosition : null, true); }
        catch (Exception error) { Console.WriteLine("Spacecraft rider cleanup: " + error.Message); }
        _ridingLease = Guid.Empty;
        if (!active) return true;
        var restored = _expedition.Restore();
        if (restored) { _expedition.End(); _entryPrepared = false; }
        _feedback = restored ? message : "Island return is pending: " + _expedition.Status;
        return restored;
    }

    private void CancelLocalSeat()
    {
        _cancelBoardingRequested = false;
        if (IsClient)
        {
            _clientSeat = -1; _clientLease = Guid.Empty;
            _clientWalking = false; _clientCabinStandLocal = null;
            if (_pending?.Action == ActionCode.Cancel) return;
            _pending = null; _boardingCommand = null;
            if (_ready)
            {
                _pending = new Pending(ActionCode.Cancel, ++_commandSequence);
                _event?.Request(ActionCode.Cancel, _pending.Sequence, false);
            }
        }
        else if (_local != null)
        {
            ReleaseSeat(_local, null, true);
            if (!_expedition.Active) _crew.Remove(_local);
        }
    }

    private void HandleRiderFault()
    {
        if (_rider?.Faulted != true) return;
        var reason = _rider.FailureReason;
        if (Now >= _nextRiderRetry)
        {
            _nextRiderRetry = Now + 500;
            if (_rider.TryResume()) return;
        }
        _feedback = reason + " Ship controls are paused while the player recovers.";
    }

    private long _nextCabinRetry, _nextRiderRetry;

    private void SuspendAttachment(Peer peer)
    {
        if (peer.Seat is < 0 or > 2 || peer.Lease == Guid.Empty
            || !ReferenceEquals(_seats[peer.Seat], peer)) return;
        peer.AttachmentSuspended = true;
        peer.Attached = false;
        peer.Input = SpacecraftControls.Parked;
        peer.LastControl = 0;
        ClearConsolePose(peer);
        ClearStationInteraction(peer.Seat);
        _nextHeldFirefighting = 0;
    }

    private Vector3 CrewPosition(Peer peer, Vector3 observed)
    {
        if (ReferenceEquals(peer, _local) && _body != null && _rider != null
            && _rider.TryAttachedPosition(_body, LocalPlayer.Transform, out var attached)) return attached;
        return observed;
    }

    private void SyncRider()
    {
        var seat = IsClient ? _clientSeat : _local?.Seat ?? -1;
        var lease = IsClient ? _clientLease : _local?.Lease ?? Guid.Empty;
        var returnHome = IsClient ? _clientReturn : _local?.ForceReturn == true;
        if (returnHome)
        {
            var restored = ReturnLocal("Your seat was released. Returned to the island.");
            if (restored && IsClient) { _handledClientReturn = true; _clientReturn = false; }
            if (restored && !IsClient && _local != null) { _local.ForceReturn = false; _crew.Remove(_local); }
            return;
        }
        if (_cancelBoardingRequested) return;
        if (_ridingLease != Guid.Empty && (_ridingLease != lease || seat < 0 || _body == null))
        {
            var exit = IsClient ? _clientExit : _local?.Exit;
            if (exit.HasValue)
            {
                BeginSurfaceExit(_ridingLease, exit.Value);
                _rider?.End(exit, true); _ridingLease = Guid.Empty;
            }
            else ReturnLocal("Your seat changed. Returned to the island.");
        }
        if (seat < 0 || lease == Guid.Empty || lease == _rejectedLease
            || _body == null || _rider == null || _rider.Restoring) return;
        if (lease == _ridingLease)
        {
            if (_rider.Suspended) return;
            var walking = IsClient ? _clientWalking : _local?.Walking == true;
            if (walking == _rider.Walking || !_rider.Attached) return;
            // A standing point authorizes one transition. Repeated snapshots must
            // not pull a walking actor back to the point beside their reserved berth.
            var standing = IsClient ? _clientCabinStandLocal : _local?.CabinStandLocal;
            var changed = _body.Ready && (walking
                ? standing.HasValue && Finite(standing.Value)
                    && _rider.BeginCabinWalk(_body, _body.Root.transform.TransformPoint(standing.Value))
                : _rider.ResumeSeat(_body, seat));
            if (!changed)
            {
                var reason = string.IsNullOrEmpty(_rider.FailureReason)
                    ? "The cabin transition could not complete." : _rider.FailureReason;
                // A blocked aisle is not a lost actor or lease. Ask the host to
                // retain the previous mode; its movement evidence still validates it.
                if (!_rider.Faulted && _pending == null && Now >= _nextCabinRetry)
                {
                    _nextCabinRetry = Now + 500;
                    Dispatch(walking ? seat switch
                    {
                        0 => ActionCode.Pilot,
                        1 => ActionCode.Passenger,
                        _ => ActionCode.PassengerRear
                    } : ActionCode.Stand);
                }
                _feedback = reason;
            }
            return;
        }
        if (!_body.Ready || !_destination.Ensure() || !Prepared)
        {
            _feedback = "Waiting for the ship and expedition protection before boarding.";
            return;
        }
        if (Now < _nextCabinRetry) return;
        _nextCabinRetry = Now + 500;
        // Keep the preparation result on the tablet until a valid seat is granted.
        _closeDevices?.Invoke();
        if (_rider.Begin(_body, seat))
        { _ridingLease = lease; _surfaceLease = Guid.Empty; }
        else
        {
            // Focus, hands and native player components may finish preparing
            // after the host grants the lease. A refusal has not moved the actor.
            _feedback = _rider.FailureReason;
        }
    }

    private bool RefreshLocal()
    {
        if (IsClient || !_ready) return false;
        if (_local != null)
        {
            var identity = ReadPeerIdentity(_local);
            if (identity == PeerIdentity.Current && TryActor(_local, out _, out _, out _)) return true;
            if (identity != PeerIdentity.Retired)
            { SuspendAttachment(_local); return false; }
            ReleaseSeat(_local, null, true); _crew.Remove(_local);
            RetireMovementEvidence(_local);
            ReturnLocal("The local player changed. The previous expedition was closed.");
        }
        _local = null;
        if (!LocalAlive()) return false;
        if (BoltNetwork.isRunning)
        {
            if (!LocalHost.TryGetPlayer(out var host)
                || !ServerWorld.TryCaptureLocalPlayerBinding(out var binding, out _)) return false;
            _local = new Peer { Binding = binding, Owner = host.SteamId, ActorPointer = LocalPlayer.Transform.Pointer };
        }
        else _local = new Peer { ActorPointer = LocalPlayer.Transform.Pointer };
        return TryActor(_local, out _, out _, out _);
    }

    private enum PeerIdentity { Current, Unavailable, Retired }

    private bool PeerCurrent(Peer peer) => ReadPeerIdentity(peer) == PeerIdentity.Current;

    private PeerIdentity ReadPeerIdentity(Peer peer)
    {
        var session = peer.Session;
        if (session?.ClosedAt.HasValue == true) return PeerIdentity.Retired;
        if (!_ready) return PeerIdentity.Unavailable;
        if (session == null)
        {
            if (!ReferenceEquals(peer, _local) || IsClient) return PeerIdentity.Retired;
            var transform = LocalPlayer.Transform;
            if (transform == null || transform.WasCollected || !transform) return PeerIdentity.Unavailable;
            if (transform.Pointer != peer.ActorPointer) return PeerIdentity.Retired;
            var stats = LocalPlayer.Stats;
            if (stats == null || stats.WasCollected || !stats) return PeerIdentity.Unavailable;
            return SpacecraftLife.Read(stats) switch
            {
                PlayerLifeState.Alive => PeerIdentity.Current,
                PlayerLifeState.Dead => PeerIdentity.Retired,
                _ => PeerIdentity.Unavailable
            };
        }
        // A failed native read or disabled tracking is not a disconnect. SDK
        // resolution may retire a handle during a read, so check it again after failure.
        if (!BoltNetwork.isRunning || !BoltNetwork.isServer || !session.IsCurrent
            || !ServerSessions.TryReadIdentity(session, out var identity, out _))
            return session.ClosedAt.HasValue ? PeerIdentity.Retired : PeerIdentity.Unavailable;
        if (identity.GameSteamId != peer.Owner) return PeerIdentity.Retired;
        if (ClientConnections.Contains(peer.Pointer, peer.Connection)) return PeerIdentity.Current;
        return session.ClosedAt.HasValue ? PeerIdentity.Retired : PeerIdentity.Unavailable;
    }

    private static Transform? ActorTransform(Peer peer)
    {
        if (peer.Session == null) return LocalPlayer.Transform;
        if (peer.Binding == null || peer.Binding.NetworkId == 0) return null;
        var entity = BoltNetwork.FindEntity(new Bolt.NetworkId(peer.Binding.NetworkId));
        return entity != null && !entity.WasCollected && entity && entity.isAttached ? entity.transform : null;
    }

    private bool TryActor(Peer peer, out Vector3 position, out Quaternion rotation, out ulong actorId)
    {
        position = default; rotation = Quaternion.identity; actorId = 0;
        if (!PeerCurrent(peer)) return false;
        if (peer.Session == null && (!LocalAlive() || LocalPlayer.Transform.Pointer != peer.ActorPointer)) return false;
        if (!BoltNetwork.isRunning)
        {
            position = LocalPlayer.Transform.position; rotation = LocalPlayer.Transform.rotation;
            return Finite(position) && ValidRotation(rotation);
        }
        if (peer.Binding == null || !ServerWorld.TryReadPlayerBinding(peer.Binding, out var actor, out _)
            || !ServerWorld.TryReadPlayerLife(peer.Binding, out var life, out _)
            || life.LifeState != PlayerLifeState.Alive) return false;
        position = new(actor.Position.X, actor.Position.Y, actor.Position.Z);
        rotation = new(actor.Rotation.X, actor.Rotation.Y, actor.Rotation.Z, actor.Rotation.W);
        actorId = peer.Binding.NetworkId;
        return Finite(position) && ValidRotation(rotation);
    }

    private static bool LocalAlive() => LocalPlayer.Transform != null && !LocalPlayer.Transform.WasCollected && LocalPlayer.Transform
        && LocalPlayer.Stats != null && !LocalPlayer.Stats.WasCollected
        && SpacecraftLife.Read(LocalPlayer.Stats) == PlayerLifeState.Alive;

    private bool TrySurvivalActor(Peer peer, out Vector3 position, out ulong actorId)
    {
        position = default; actorId = 0;
        if (peer.Seat is < 0 or > 2 || !ReferenceEquals(_seats[peer.Seat], peer) || peer.Lease == Guid.Empty
            || peer.AttachedAt == 0 || !peer.EntryPrepared || peer.ForceReturn || _body?.Ready != true
            || !_movement.TryGetValue(peer, out var evidence) || !evidence.Active || evidence.ReturnIssued
            || evidence.Ship != _shipId || !MovementCaptureRetained(peer, evidence)) return false;
        if (TryActor(peer, out position, out _, out actorId)) return actorId == evidence.Actor;

        // Incapacitated crew still breathe. This observation never grants input,
        // movement, work or a replacement actor access to the retained lease.
        if (peer.Session == null)
        {
            if (_rider?.Incapacitated != true
                || !_rider.TryAttachedPosition(_body, LocalPlayer.Transform, out position)) return false;
            if (!BoltNetwork.isRunning) return evidence.Actor == 0;
        }
        else if (!PeerCurrent(peer)) return false;
        if (peer.Binding == null || peer.Binding.NetworkId != evidence.Actor
            || (peer.Session == null ? !peer.Binding.IsLocalPlayer
                : !ReferenceEquals(peer.Binding.PlayerSession, peer.Session))
            || !ServerWorld.TryReadPlayerLife(peer.Binding, out var life, out _)
            || life.LifeState != PlayerLifeState.Dying) return false;
        if (peer.Session != null)
            position = new(life.Entity.Position.X, life.Entity.Position.Y, life.Entity.Position.Z);
        actorId = evidence.Actor;
        return Finite(position);
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y)
        && float.IsFinite(value.z) && value.sqrMagnitude < 4e10f;
    private static bool ValidRotation(Quaternion value) => float.IsFinite(value.x) && float.IsFinite(value.y)
        && float.IsFinite(value.z) && float.IsFinite(value.w)
        && MathF.Abs(value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w - 1) < .02f;
}
