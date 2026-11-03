// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Endnight.Extensions;
using Modstoke.Sons;
using Sons.Gameplay.GameSetup;
using Sons.Save;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientAircraftService
{
    private const string SaveOwner = "modstoke.client";
    private const string SaveName = "aircraft-v1";
    private static readonly JsonSerializerOptions SaveOptions = new() { MaxDepth = 8 };
    private IDisposable? _saveRegistration;
    private WorldRecordContext? _world;
    private IntPtr _saveManager;
    private int _transportMode = -1;
    private bool _loading, _offlineLoadComplete, _loadInvalid, _hasParking;
    private Guid _offlineWorld;
    private SavedAircraftWorld? _pendingOffline;
    private Vector3 _parkedPosition;
    private Quaternion _parkedRotation = Quaternion.identity;

    private void StartPersistence()
    {
        _transportMode = !BoltNetwork.isRunning ? 0 : BoltNetwork.isServer ? 1 : 2;
        SetOfflineRegistration(_transportMode == 0);
        SaveLifecycle.Lifecycle += OnSaveLifecycle;
        GameRuntime.WorldChanged += OnWorldChanged;
    }

    private void StopPersistence()
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
        if (!SaveGameManager.TryGetInstance(out var manager) || manager == null || manager.WasCollected
            || !manager || manager._loadingInProgress)
        {
            if (_ready) ResetWorld();
            _status = "Waiting for the world save manager.";
            return false;
        }
        if (_saveManager != IntPtr.Zero && _saveManager != manager.Pointer) ResetWorld();
        _saveManager = manager.Pointer;
        if (IsClient)
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
            // Only the matching host welcome can make a client ready.
            return true;
        }
        if (BoltNetwork.isRunning)
        {
            if (!WorldRecords.TryGetCurrent(out var world, out var status))
            {
                if (_ready) ResetWorld();
                _status = "Aircraft is waiting for world records: " + status + ".";
                return false;
            }
            if (!ReferenceEquals(_world, world))
            {
                ResetWorld();
                _saveManager = manager.Pointer;
                _world = world;
                var read = WorldRecords.ReadJson(world, SaveOwner, SaveName, out var json);
                if (read != WorldRecordStatus.NotFound
                    && (read != WorldRecordStatus.Success || !LoadAircraft(json, world.WorldId)))
                    return InvalidSave();
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
            { _status = "Waiting for this world's aircraft save record."; return false; }
            if (_pendingOffline != null)
            {
                if (!RestoreAircraft(_pendingOffline, _pendingOffline.World)) return InvalidSave();
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
        _epoch = Guid.NewGuid();
        _ready = true;
        _status = "Hangar ready. One shared plane belongs to this world.";
    }

    private bool InvalidSave()
    {
        _loadInvalid = true;
        _ready = false;
        _status = "The aircraft save record is unavailable or invalid. Saved data was preserved.";
        return false;
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
            ResetWorld();
            _loading = true;
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
        var complete = preservePendingLoad && _offlineLoadComplete;
        var pending = preservePendingLoad ? _pendingOffline : null;
        var invalid = preservePendingLoad && _loadInvalid;
        // Revoke logical authority before callbacks can observe the old world.
        _ready = false;
        _peers.Clear();
        // An active snapshot keeps its original entries until its finally block.
        if (!_peerSnapshotInUse) Array.Clear(_peerSnapshot, 0, _peerSnapshot.Length);
        Array.Clear(_seats);
        Array.Clear(_actorIds);
        _local = null;
        _pending = null;
        _clientSeat = -1;
        _clientExit = null;
        ResetDoors();
        _planeId = _epoch = _nonce = _token = _clientLease = _ridingLease = _rejectedLease = Guid.Empty;
        try { RemoveBody(); }
        catch (Exception error) { Console.WriteLine("Aircraft body cleanup: " + error); }
        try { _cockpit?.Update(default, -1, false); }
        catch (Exception error) { Console.WriteLine("Aircraft cockpit cleanup: " + error); }
        _world = null;
        _saveManager = _serverPointer = IntPtr.Zero;
        _serverConnection = 0;
        _offlineWorld = Guid.Empty;
        _loading = loading;
        _offlineLoadComplete = complete;
        _pendingOffline = pending;
        _loadInvalid = invalid;
        _hasParking = false;
        _parkedPosition = default;
        _parkedRotation = Quaternion.identity;
        _nextHello = _nextSnapshot = _nextControl = _lastSnapshot = _lastWelcome = 0;
        _commandSequence = _controlSequence = _snapshotSequence = _receivedSequence = 0;
        _status = "Waiting for the current world.";
        _feedback = "";
        View = new(false, false, _status, "", -1, 0, 0, default, false, false);
    }

    private void CaptureParkedPosition()
    {
        if (_body == null || IsClient) return;
        var flight = _body.State;
        if (flight.Grounded && flight.Velocity.sqrMagnitude < 4
            && Finite(flight.Position) && ValidRotation(flight.Rotation))
            RememberParking(flight.Position, flight.Rotation);
    }

    private void RememberParking(Vector3 position, Quaternion rotation)
    {
        _parkedPosition = position;
        _parkedRotation = rotation;
        _hasParking = true;
    }

    private SavedAircraftWorld CaptureAircraft(Guid world)
    {
        CaptureParkedPosition();
        var saved = new SavedAircraftWorld { World = world };
        if (_body == null) return saved;
        var flight = _body.State;
        if (!_hasParking || _planeId == Guid.Empty || !Finite(_parkedPosition)
            || !ValidRotation(_parkedRotation) || !Unit(flight.Fuel) || !Unit(flight.Health))
            throw new InvalidOperationException("The aircraft has no valid parked save state.");
        saved.Aircraft = new SavedAircraft
        {
            Id = _planeId, Fuel = flight.Fuel, Health = flight.Health,
            X = _parkedPosition.x, Y = _parkedPosition.y, Z = _parkedPosition.z,
            Qx = _parkedRotation.x, Qy = _parkedRotation.y, Qz = _parkedRotation.z, Qw = _parkedRotation.w
        };
        return saved;
    }

    private void StoreWorld()
    {
        if (!_ready || _loading || _loadInvalid || IsClient || _world == null) return;
        var result = WorldRecords.WriteJson(_world, SaveOwner, SaveName,
            JsonSerializer.Serialize(CaptureAircraft(_world.WorldId), SaveOptions));
        if (result != WorldRecordStatus.Success)
            _status = "Aircraft changes are not yet accepted by the world save.";
    }

    private bool LoadAircraft(string json, Guid world)
    {
        if (json == null || json.Length > 4096) return false;
        try { return RestoreAircraft(JsonSerializer.Deserialize<SavedAircraftWorld>(json, SaveOptions), world); }
        catch (JsonException) { return false; }
    }

    private bool RestoreAircraft(SavedAircraftWorld? data, Guid world)
    {
        if (data == null || data.Version != 1 || world == Guid.Empty || data.World != world) return false;
        var saved = data.Aircraft;
        if (saved == null) return true;
        var position = new Vector3(saved.X, saved.Y, saved.Z);
        var rotation = new Quaternion(saved.Qx, saved.Qy, saved.Qz, saved.Qw);
        if (saved.Id == Guid.Empty || !Finite(position) || !ValidRotation(rotation)
            || !Unit(saved.Fuel) || !Unit(saved.Health)) return false;
        ClientAircraftBody? restored = null;
        try
        {
            restored = new ClientAircraftBody(saved.Id.ToString("N"), position, rotation.normalized, true);
            restored.Restore(saved.Fuel, saved.Health);
            _body = restored;
            _planeId = saved.Id;
            RememberParking(position, rotation.normalized);
            return true;
        }
        catch (Exception error)
        {
            try { restored?.Dispose(); }
            catch (Exception cleanup) { Console.WriteLine("Aircraft restore cleanup: " + cleanup); }
            Console.WriteLine("Saved aircraft restoration failed: " + error);
            return false;
        }
    }

    private static bool Unit(float value) => float.IsFinite(value) && value >= 0 && value <= 1;

    private sealed class OfflineSave(ClientAircraftService service) : ISaveable
    {
        public string ModId => SaveOwner;
        public string Name => SaveName + "-singleplayer";
        public bool IncludeInPlayerSave => false;
        public string Save()
        {
            if (!service.OnThread || BoltNetwork.isRunning || !service._ready || service._loading
                || service._loadInvalid || service._offlineWorld == Guid.Empty)
                throw new InvalidOperationException("No single-player aircraft world is active.");
            return JsonSerializer.Serialize(service.CaptureAircraft(service._offlineWorld), SaveOptions);
        }
        public void Load(string data)
        {
            if (!service.OnThread || BoltNetwork.isRunning
                || !service._loading && (service._ready || !service._offlineLoadComplete)) return;
            try
            {
                if (data == null || data.Length > 4096) throw new JsonException();
                service._pendingOffline = JsonSerializer.Deserialize<SavedAircraftWorld>(data, SaveOptions)
                    ?? throw new JsonException();
            }
            catch (JsonException) { service.InvalidSave(); }
        }
    }

    public sealed class SavedAircraftWorld
    {
        [JsonRequired]
        public int Version { get; set; } = 1;
        [JsonRequired]
        public Guid World { get; set; }
        [JsonRequired]
        public SavedAircraft? Aircraft { get; set; }
    }

    public sealed class SavedAircraft
    {
        [JsonRequired]
        public Guid Id { get; set; }
        [JsonRequired]
        public float Fuel { get; set; }
        [JsonRequired]
        public float Health { get; set; }
        [JsonRequired]
        public float X { get; set; }
        [JsonRequired]
        public float Y { get; set; }
        [JsonRequired]
        public float Z { get; set; }
        [JsonRequired]
        public float Qx { get; set; }
        [JsonRequired]
        public float Qy { get; set; }
        [JsonRequired]
        public float Qz { get; set; }
        [JsonRequired]
        public float Qw { get; set; } = 1;
    }
}
