// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Endnight.Extensions;
using Modstoke.Sons;
using Sons.Gameplay.GameSetup;
using Sons.Save;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientDroneService
{
    private const string SaveOwner = "modstoke.client";
    private const string SaveName = "drone-fleet-v1";
    private static readonly JsonSerializerOptions SaveOptions = new() { MaxDepth = 8 };
    private int _transportMode = -1;

    private bool RefreshWorld()
    {
        var client = IsClient;
        var transportMode = !BoltNetwork.isRunning ? 0 : BoltNetwork.isServer ? 1 : 2;
        if (_transportMode != transportMode)
        {
            // The native transport may change during the load whose callback supplied this record.
            ResetWorld(preservePendingLoad: _loading || !_ready && _offlineLoadComplete);
            _transportMode = transportMode;
            if (!_loading) SetOfflineRegistration(transportMode == 0);
        }
        if (_loading) { _status = "Waiting for world loading to finish."; return false; }
        if (!SaveGameManager.TryGetInstance(out var manager) || manager == null || manager.WasCollected || !manager
            || manager._loadingInProgress)
        {
            if (_ready) ResetWorld();
            _status = "Waiting for the world save manager.";
            return false;
        }
        if (_saveManager != IntPtr.Zero && _saveManager != manager.Pointer) ResetWorld();
        _saveManager = manager.Pointer;
        if (client)
        {
            var server = BoltNetwork.server;
            if (server == null || server.WasCollected) { ResetWorld(); return false; }
            if (_serverPointer != server.Pointer || _serverConnection != server.ConnectionId)
            {
                ResetWorld();
                _saveManager = manager.Pointer;
                _serverPointer = server.Pointer;
                _serverConnection = server.ConnectionId;
                _nonce = Guid.NewGuid();
            }
            // Client readiness is established only by the matching server welcome.
            return true;
        }
        if (BoltNetwork.isRunning)
        {
            if (!BoltNetwork.isServer)
            {
                if (_ready) ResetWorld();
                _status = "Waiting for authoritative world records.";
                return false;
            }
            if (!WorldRecords.TryGetCurrent(out var world, out var status))
            {
                if (_ready) ResetWorld();
                _status = "Drone inventory is waiting for world records: " + status + ".";
                return false;
            }
            if (!ReferenceEquals(_world, world))
            {
                ResetWorld();
                _saveManager = manager.Pointer;
                _world = world;
                var read = WorldRecords.ReadJson(world, SaveOwner, SaveName, out var json);
                if (read != WorldRecordStatus.NotFound
                    && (read != WorldRecordStatus.Success || !LoadFleet(json, world.WorldId)))
                { _loadInvalid = true; _status = "Drone save data is unavailable or invalid; inventory was preserved."; return false; }
                BeginAuthority();
            }
            return _ready && !_loadInvalid;
        }
        if (GameRuntime.IsTitleScene)
        {
            if (_ready) ResetWorld();
            _status = "Waiting for a playable world.";
            return false;
        }
        if (!_ready && (LocalPlayer.Inventory == null || LocalPlayer.Inventory.WasCollected || !LocalPlayer.Inventory))
        { _status = "Waiting for the local player inventory."; return false; }
        if (_loadInvalid) return false;
        if (!_ready)
        {
            if (GameSetupManager.GameLoadedFromSave() && !_offlineLoadComplete)
            { _status = "Waiting for this world's drone save record."; return false; }
            if (_pendingOffline != null)
            {
                if (!RestoreFleet(_pendingOffline, _pendingOffline.World))
                { _loadInvalid = true; _status = "The drone save record is invalid; inventory was preserved."; return false; }
                _offlineWorld = _pendingOffline.World;
                _pendingOffline = null;
            }
            if (_offlineWorld == Guid.Empty) _offlineWorld = Guid.NewGuid();
            BeginAuthority();
        }
        return _ready;
    }

    private Guid _offlineWorld;

    private void BeginAuthority()
    {
        _epoch = Guid.NewGuid();
        _ready = true;
        _status = "Drone ready. One aircraft and two rechargeable packs belong to this world.";
        foreach (var kit in _kits.Values)
            if (kit.Deployed && !CreateBody(kit, kit.Position, kit.Rotation))
            {
                _loadInvalid = true;
                _ready = false;
                _status = "A saved drone could not be restored. Saved inventory was preserved.";
                foreach (var active in _kits.Values)
                { active.Native?.Dispose(); active.Native = null; active.Body?.Dispose(); active.Body = null; }
                return;
            }
    }

    private void RefreshLocalPeer()
    {
        if (IsClient || !_ready) return;
        if (_localPeer != null && PeerCurrent(_localPeer) && TryActor(_localPeer, out _, out _)) return;
        if (_localPeer != null) EndLease(_localPeer);
        _localPeer = null;
        _localOwner = "";
        if (BoltNetwork.isRunning)
        {
            if (!LocalHost.TryGetPlayer(out var host))
            {
                _status = "Drone inventory is waiting for the host player's identity and live inventory.";
                return;
            }
            if (!ServerWorld.TryCaptureLocalPlayerBinding(out var binding, out var bindingStatus))
            {
                _status = "Drone inventory is waiting for the host player's attachment: " + bindingStatus + ".";
                return;
            }
            _localOwner = host.SteamId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _localPeer = new Peer(_localOwner, host.SteamId) { Binding = binding, Host = host };
        }
        else
        {
            if (!LocalAlive()) { _status = "Waiting for a living local player and inventory."; return; }
            _localOwner = "offline-player";
            _localPeer = new Peer(_localOwner, 0);
        }
        _status = EnsureKit(_localOwner)
            ? "Drone ready. One aircraft and two rechargeable packs belong to this world."
            : "Drone inventory is unavailable because this world's aircraft limit has been reached.";
    }

    private bool EnsureKit(string owner)
    {
        if (_kits.ContainsKey(owner)) return true;
        if (_kits.Count >= MaxOwners) return false;
        _kits.Add(owner, new Kit { Owner = owner });
        StoreWorld();
        return true;
    }

    private bool PeerCurrent(Peer peer)
    {
        if (!_ready || _loading) return false;
        if (peer.Session == null) return ReferenceEquals(peer, _localPeer) && !IsClient
            && (!BoltNetwork.isRunning || peer.Host is { } host && host.IsCurrent
                && host.SteamId == peer.SteamId);
        if (!BoltNetwork.isRunning || !BoltNetwork.isServer || !peer.Session.IsCurrent
            || peer.Session.ConnectionId != peer.Connection || peer.Owner == _localOwner) return false;
        var session = peer.Session;
        if (!ServerSessions.TryReadGameSteamId(session, out var steamId, out _)
            || !ReferenceEquals(session, peer.Session) || steamId != peer.SteamId) return false;
        return ClientConnections.Contains(peer.Pointer, peer.Connection);
    }

    private bool TryActor(Peer peer, out Vector3 position, out Quaternion rotation)
    {
        position = default;
        rotation = Quaternion.identity;
        if (!PeerCurrent(peer)) return false;
        if (!BoltNetwork.isRunning)
        {
            if (!LocalAlive()) return false;
            position = LocalPlayer.Transform.position;
            rotation = LocalPlayer.Transform.rotation;
            return Finite(position);
        }
        if (peer.Binding == null || peer.Binding.IsLocalPlayer != (peer.Session == null)
            || !ReferenceEquals(peer.Binding.PlayerSession, peer.Session)
            || !ServerWorld.TryReadPlayerLife(peer.Binding, out var life, out _)
            || life.LifeState != PlayerLifeState.Alive) return false;
        var actor = life.Entity;
        position = new Vector3(actor.Position.X, actor.Position.Y, actor.Position.Z);
        rotation = new Quaternion(actor.Rotation.X, actor.Rotation.Y, actor.Rotation.Z, actor.Rotation.W);
        return Finite(position);
    }

    private static bool LocalAlive() => LocalPlayer.Transform != null && LocalPlayer.Transform
        && LocalPlayer.Stats != null && LocalPlayer.Stats._dyingState == PlayerStats.DyingState.Alive
        && !LocalPlayer.Stats._playerIsDowned && !LocalPlayer.Stats._respawning;

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
            ResetWorld();
            _loading = true;
            SetOfflineRegistration(!value.SaveGameType.StartsWith("Multiplayer", StringComparison.OrdinalIgnoreCase));
        }
        else if (value.Operation == "Load" && value.Phase == "After")
        {
            _loading = false;
            _offlineLoadComplete = value.Success == true
                && !value.SaveGameType.StartsWith("Multiplayer", StringComparison.OrdinalIgnoreCase);
            if (value.Success != true) _pendingOffline = null;
        }
        else if (value.Operation == "Save" && value.Phase == "Before")
        {
            SetOfflineRegistration(!BoltNetwork.isRunning && _ready && !_loadInvalid && !_loading);
            StoreWorld();
        }
    }

    private void SetOfflineRegistration(bool active)
    {
        if (active) _saveRegistration ??= SaveRegistry.Register(new OfflineSave(this));
        else { _saveRegistration?.Dispose(); _saveRegistration = null; }
    }

    private void ResetWorld(bool preservePendingLoad = false)
    {
        var loading = preservePendingLoad && _loading;
        var offlineLoadComplete = preservePendingLoad && _offlineLoadComplete;
        var pendingOffline = preservePendingLoad ? _pendingOffline : null;
        var loadInvalid = preservePendingLoad && _loadInvalid;
        ResetPlacement();
        ResetRemoteTerrainSurvey();
        _lastContact = null;
        _remoteReadiness = null;
        _detailsDroneId = "";
        _detailsAt = _lastDetailsSequence = 0;
        foreach (var kit in _kits.Values) { kit.Native?.Dispose(); kit.Body?.Dispose(); }
        foreach (var replica in _replicas.Values) replica.Body.Dispose();
        _kits.Clear(); _replicas.Clear(); _peers.Clear(); _removed.Clear();
        _world = null;
        _localPeer = null;
        _localOwner = "";
        _ready = false;
        _loadInvalid = loadInvalid;
        _loading = loading;
        _offlineLoadComplete = offlineLoadComplete;
        _pendingOffline = pendingOffline;
        _offlineWorld = Guid.Empty;
        _epoch = _nonce = _token = _clientLease = Guid.Empty;
        _serverPointer = _saveManager = IntPtr.Zero;
        _serverConnection = 0;
        _pilotRequested = false;
        _pilotStartPending = false;
        _pilotStartSequence = 0;
        _pendingCommands.Clear();
        // Active snapshots stay intact until their owning update clears them in finally.
        if (!_peerSnapshotInUse) Array.Clear(_peerSnapshot, 0, _peerSnapshot.Length);
        if (!_pendingCommandSnapshotInUse) Array.Clear(_pendingCommandSnapshot, 0, _pendingCommandSnapshot.Length);
        if (!_replicaSnapshotInUse) Array.Clear(_replicaSnapshot, 0, _replicaSnapshot.Length);
        _bodySequences.Clear();
        _nextControl = _lastResult = _lastWelcomeAt = _inputAt = 0;
        _input = default;
        _commandSequence = _controlSequence = _snapshotSequence = _lastClientSnapshot = 0;
        _lastOwnerSnapshot = _nextHello = _nextNetwork = _nextPersistence = 0;
        if (!loadInvalid) _status = "Waiting for the current world.";
        _remoteView = Snapshot = ClientDroneView.Unavailable(_status);
    }

    private SavedFleet CaptureFleet(Guid world)
    {
        foreach (var kit in _kits.Values) SyncKit(kit);
        return new SavedFleet { World = world, Kits = _kits.Values.Select(kit => new SavedKit
        {
            Owner = kit.Owner, Id = kit.Id, PackA = kit.PackA, PackB = kit.PackB, ChargeA = kit.ChargeA,
            ChargeB = kit.ChargeB, Health = kit.Health, Installed = kit.Installed, Deployed = kit.Deployed,
            X = kit.Position.x, Y = kit.Position.y, Z = kit.Position.z,
            Qx = kit.Rotation.x, Qy = kit.Rotation.y, Qz = kit.Rotation.z, Qw = kit.Rotation.w
        }).ToArray() };
    }

    private void StoreWorld()
    {
        if (!_ready || _loading || _loadInvalid || IsClient || _world == null) return;
        var result = WorldRecords.WriteJson(_world, SaveOwner, SaveName,
            JsonSerializer.Serialize(CaptureFleet(_world.WorldId), SaveOptions));
        if (result != WorldRecordStatus.Success)
            _status = "Drone changes are not yet accepted by the world save.";
    }

    private bool LoadFleet(string json, Guid world) =>
        TryReadFleet(json, out var fleet) && RestoreFleet(fleet, world);

    private static bool TryReadFleet(string json, out SavedFleet? fleet)
    {
        fleet = null;
        if (json == null || json.Length > 160000) return false;
        try
        {
            fleet = JsonSerializer.Deserialize<SavedFleet>(json, SaveOptions);
            return fleet != null;
        }
        catch (JsonException) { return false; }
    }

    private bool RestoreFleet(SavedFleet? data, Guid world)
    {
        if (data == null || data.Version != 1 || world == Guid.Empty || data.World != world
            || data.Kits == null || data.Kits.Length > MaxOwners) return false;
        var staged = new Dictionary<string, Kit>(StringComparer.Ordinal);
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in data.Kits)
        {
            if (item == null || string.IsNullOrEmpty(item.Owner) || item.Owner.Length > 32
                || item.Owner != "offline-player" && (!ulong.TryParse(item.Owner,
                    System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var steam)
                    || steam == 0 || item.Owner != steam.ToString(System.Globalization.CultureInfo.InvariantCulture))
                || !ValidId(item.Id) || !ValidId(item.PackA) || !ValidId(item.PackB)
                || !identities.Add(item.Id) || !identities.Add(item.PackA) || !identities.Add(item.PackB)
                || !Unit(item.ChargeA) || !Unit(item.ChargeB) || !Unit(item.Health) || item.Installed is < 0 or > 1) return false;
            var position = new Vector3(item.X, item.Y, item.Z);
            var rotation = new Quaternion(item.Qx, item.Qy, item.Qz, item.Qw);
            if (!Finite(position) || !ValidRotation(rotation)) return false;
            if (!staged.TryAdd(item.Owner, new Kit { Owner = item.Owner, Id = item.Id, PackA = item.PackA,
                PackB = item.PackB, ChargeA = item.ChargeA, ChargeB = item.ChargeB, Health = item.Health,
                Installed = item.Installed, Deployed = item.Deployed, Position = position, Rotation = rotation.normalized })) return false;
        }
        if (staged.Values.Count(kit => kit.Deployed) > MaxPeers) return false;
        foreach (var pair in staged) _kits.Add(pair.Key, pair.Value);
        return true;
    }

    private static bool Unit(float value) => float.IsFinite(value) && value >= 0 && value <= 1;
    private static bool ValidId(string value) => value != null && value.Length == 32 && Guid.TryParseExact(value, "N", out var id) && id != Guid.Empty;
    private static bool ValidRotation(Quaternion value)
    {
        var length = value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w;
        return float.IsFinite(length) && length > .9f && length < 1.1f;
    }

    private sealed class OfflineSave(ClientDroneService service) : ISaveable
    {
        public string ModId => SaveOwner;
        public string Name => SaveName + "-singleplayer";
        public bool IncludeInPlayerSave => false;
        public string Save()
        {
            if (!service.OnThread || BoltNetwork.isRunning || !service._ready || service._offlineWorld == Guid.Empty)
                throw new InvalidOperationException("No single-player drone world is active.");
            return JsonSerializer.Serialize(service.CaptureFleet(service._offlineWorld), SaveOptions);
        }
        public void Load(string data)
        {
            if (!service.OnThread || BoltNetwork.isRunning
                || !service._loading && (service._ready || !service._offlineLoadComplete)) return;
            if (!TryReadFleet(data, out var fleet))
            {
                service._loadInvalid = true;
                service._status = "The drone save record could not be read.";
                return;
            }
            service._pendingOffline = fleet;
        }
    }

    public sealed class SavedFleet
    {
        public int Version { get; set; } = 1;
        public Guid World { get; set; }
        public SavedKit[] Kits { get; set; } = Array.Empty<SavedKit>();
    }
    public sealed class SavedKit
    {
        public string Owner { get; set; } = "";
        public string Id { get; set; } = "";
        public string PackA { get; set; } = "";
        public string PackB { get; set; } = "";
        public float ChargeA { get; set; }
        public float ChargeB { get; set; }
        public float Health { get; set; }
        public int Installed { get; set; }
        public bool Deployed { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float Qx { get; set; }
        public float Qy { get; set; }
        public float Qz { get; set; }
        public float Qw { get; set; } = 1;
    }
}
