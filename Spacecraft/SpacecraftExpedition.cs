// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Threading;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Modstoke.Sons;
using Sons.Player;
using Sons.Save;
using Sons.Settings;
using TheForest.Items.Inventory;
using TheForest.Utils;
using UnityEngine;
using SavedPlayerState = global::Sons.Player.PlayerState;

namespace Modstoke.Client;

// The native island/session stays loaded during travel. Only serialized location
// entries are projected; native PlayerState data and change events remain untouched.
internal sealed class ClientSpacecraftExpedition : IDisposable
{
    private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("Modstoke spacecraft return");
    private static ClientSpacecraftExpedition? _owner;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly Harmony _patches = new("modstoke.client.spacecraft.return." + Guid.NewGuid().ToString("N"));
    private MethodInfo? _target, _postfix;
    private Capture? _capture;
    private bool _started, _disposed, _loading, _surfaceFailureLogged;
    private long _loadGeneration, _epoch, _captureSerializations;
    private IntPtr _serializingCapture;

    internal bool Ready => _started && !_disposed && !_loading && ReferenceEquals(_owner, this);
    internal bool Active => _capture != null;
    internal bool Current => OnThread && _capture is { } capture
        && capture.Identity(this) == IdentityState.Current && capture.Alive;
    internal bool OnSafeIslandGround => Current && _capture is { } capture
        && capture.OnIsland(this) && IslandSupport(capture.Player);
    internal string Status { get; private set; } = "Spacecraft return protection has not started.";
    internal string SaveWarning => _capture is { } capture ? Volatile.Read(ref capture.SaveWarning) : "";
    internal Vector3? ReturnPosition => _capture?.Position;
    internal bool ReturnRequested { get; private set; }
    private bool OnThread => Environment.CurrentManagedThreadId == _thread;

    internal bool TryReadPosition(out Vector3 position, out bool retained)
    {
        position = default;
        retained = false;
        if (!OnThread || _capture is not { } capture) return false;
        var identity = capture.Identity(this);
        // Retained custody can preserve the environment through missing native
        // references, but does not authorize position, control or save operations.
        retained = identity != IdentityState.Changed;
        // Downed crew still occupy their current world; this observation does
        // not grant the living-player authority required by Current.
        if (identity != IdentityState.Current || capture.Life is not (PlayerLifeState.Alive or PlayerLifeState.Dying)) return false;
        position = capture.Player.position;
        return Finite(position);
    }

    internal bool Start()
    {
        if (!OnThread || _disposed) return false;
        if (_started) return Ready;
        if (_owner != null && !ReferenceEquals(_owner, this))
        { Status = "Another spacecraft return guard is still active."; return false; }
        try
        {
            _target = AccessTools.DeclaredMethod(typeof(SavedPlayerState), nameof(SavedPlayerState.OnSerialize), Type.EmptyTypes)
                ?? throw new MissingMethodException(typeof(SavedPlayerState).FullName, nameof(SavedPlayerState.OnSerialize));
            if (_target.IsStatic || _target.ReturnType != typeof(PlayerStateData))
                throw new InvalidOperationException("The player save signature is not supported.");
            _postfix = AccessTools.DeclaredMethod(typeof(ClientSpacecraftExpedition), nameof(AfterSerialize))
                ?? throw new MissingMethodException(nameof(AfterSerialize));
            _patches.Patch(_target, postfix: new HarmonyMethod(_postfix) { priority = Priority.Last });
            _owner = this;
            _started = true;
            SaveLifecycle.Lifecycle += OnSaveLifecycle;
            GameRuntime.WorldChanged += OnWorldChanged;
            Status = "Island return protection is ready.";
            return true;
        }
        catch (Exception error)
        {
            RemoveHooks();
            Status = "Spacecraft travel is unavailable because player save protection could not start.";
            Log.LogError(Status + " " + error);
            return false;
        }
    }

    // Call before the rider takes control. Reboarding never replaces the island anchor.
    internal bool Begin(Vector3 returnPosition, Quaternion returnRotation)
    {
        if (!OnThread || !Ready) return false;
        if (_capture != null) return Current && !ReturnRequested;
        try
        {
            if (!GameRuntime.State.IsInWorld || GameRuntime.IsTitleScene || LocalPlayer.IsInCaves
                || !SaveGameManager.TryGetInstance(out var manager) || !Live(manager) || manager._loadingInProgress
                || !SavedPlayerState.TryGetInstance(out var state) || !Live(state) || !Valid(state._data))
                return Refuse("Enter the island world before boarding the spacecraft.");
            var player = LocalPlayer.Transform;
            var body = LocalPlayer.Rigidbody;
            var inventory = LocalPlayer.Inventory;
            var stats = LocalPlayer.Stats;
            var movement = LocalPlayer.FpCharacter;
            if (!Live(player) || !Live(body) || !Live(inventory) || !Live(stats) || !Live(movement)
                || inventory.CurrentView != PlayerInventory.PlayerViews.World || !movement.isActiveAndEnabled
                || movement.Locked || body.isKinematic || !IslandPosition(player.position)
                || !ValidRotation(player.rotation) || !((body.position - player.position).sqrMagnitude <= .25f))
                return Refuse("Finish your current action before boarding the spacecraft.");
            if (!IslandPosition(returnPosition) || !ValidRotation(returnRotation)
                || (returnPosition - player.position).sqrMagnitude > 60f * 60f || !AnchorSupport(returnPosition, player))
                return Refuse("A nearby supported island return point is required before boarding.");

            var capture = new Capture(this, ++_epoch, state, manager, player, body, inventory, stats, movement,
                returnPosition, returnRotation);
            if (capture.Identity(this) != IdentityState.Current || !capture.Alive)
                return Refuse("Your current player or world is not ready for travel.");
            var observed = _captureSerializations;
            PlayerStateData saved;
            _serializingCapture = state.Pointer;
            try { saved = state.OnSerialize(); }
            finally { _serializingCapture = IntPtr.Zero; }
            // This ordinary native serialization also confirms that the exact hook
            // runs before any transient spacecraft position can be written.
            if (_captureSerializations == observed || !Valid(saved)
                || capture.Identity(this) != IdentityState.Current || !capture.Alive
                || !((player.position - capture.ActorPosition).sqrMagnitude <= .01f) || LocalPlayer.IsInCaves)
                return Refuse("The current island save location could not be captured safely.");
            capture.CaptureLocations(saved);
            capture.Fallback = Project(saved, capture);
            if (capture.Identity(this) != IdentityState.Current || !capture.Alive)
                return Refuse("Your player changed while preparing the island return.");
            _capture = capture;
            _surfaceFailureLogged = false;
            ReturnRequested = false;
            Status = "Island return saved for this expedition.";
            return true;
        }
        catch (Exception error)
        {
            Status = "Spacecraft travel was refused because the island save could not be protected.";
            Log.LogError(Status + " " + error);
            return false;
        }
    }

    internal void Update()
    {
        if (!OnThread || _disposed || _capture == null) return;
        var identity = _capture.Identity(this);
        if (identity == IdentityState.Changed) Invalidate();
        else if (identity == IdentityState.Unavailable)
        {
            // Retain the capture while native identity is temporarily unreadable.
            // Current remains false, so this does not authorize travel or writes.
            Status = "Waiting for your player's expedition identity.";
        }
        else if (!_capture.Alive) _capture.ReleaseReturn();
        // Inventory views and revivable downing retain the same expedition capture.
    }

    internal bool TryRelocateSurface(Vector3 position)
    {
        if (!OnThread || !Ready || ReturnRequested || _capture is not { } capture || !Finite(position)
            || position.y is <= 2000 or >= 4000) return false;
        try
        {
            if (capture.Identity(this) != IdentityState.Current || !capture.Alive || !capture.CanRelocate
                || !capture.ClearAt(position, requireIsland: false)
                || capture.Identity(this) != IdentityState.Current) return false;
            capture.Player.position = position;
            capture.Body.position = position;
            capture.Body.velocity = capture.Body.angularVelocity = Vector3.zero;
            Physics.SyncTransforms();
            _surfaceFailureLogged = false;
            return true;
        }
        catch (Exception error)
        {
            if (!_surfaceFailureLogged) Log.LogWarning("Surface position recovery is pending: " + error);
            _surfaceFailureLogged = true;
            return false;
        }
    }

    // The service must release its rider first. Never restore a replacement or dead actor.
    internal bool Restore()
    {
        if (!OnThread || _capture is not { } capture) return false;
        try
        {
            ReturnRequested = true;
            if (capture.Identity(this) != IdentityState.Current || !capture.Alive || capture.Body.isKinematic)
                return false;
            capture.HoldReturn();
            capture.Player.SetPositionAndRotation(capture.Position, capture.Rotation);
            capture.Body.position = capture.Position;
            capture.Body.rotation = capture.Rotation;
            capture.Body.velocity = Vector3.zero;
            capture.Body.angularVelocity = Vector3.zero;
            if (!capture.ReturnClear())
            {
                Status = "Waiting for solid ground and clearance at the island return point.";
                return false;
            }
            if (capture.Identity(this) != IdentityState.Current || !capture.Alive
                || (capture.Player.position - capture.Position).sqrMagnitude > .01f
                || (capture.Body.position - capture.Position).sqrMagnitude > .01f)
                return false;
            capture.ReleaseReturn();
            Status = "Returned to the retained island location.";
            return true;
        }
        catch (Exception error)
        {
            ReturnRequested = true;
            const string failed = "The island return did not finish. Player save protection remains active.";
            if (Status != failed) Log.LogError(failed + " " + error);
            Status = failed;
            return false;
        }
    }

    internal void End()
    {
        if (!OnThread || _capture is not { } capture || !capture.OnIsland(this)) return;
        capture.ReleaseReturn();
        _capture = null;
        ++_epoch;
        ReturnRequested = false;
        Status = "Island return confirmed.";
        if (_disposed) RemoveHooks();
    }

    public void Dispose()
    {
        if (!OnThread || _disposed) return;
        if (_capture != null)
        {
            if (_capture.Identity(this) == IdentityState.Changed) Invalidate();
            else if (!_capture.OnIsland(this)) Restore();
            End();
            _capture?.ReleaseReturn();
        }
        _disposed = true;
        Unsubscribe();
        if (_capture == null) RemoveHooks();
        else
        {
            // A failed return must not expose off-island coordinates. No timer or
            // movement survives disposal; the exact save postfix retires itself
            // when the actor is replaced or an island return is observed.
            ReturnRequested = true;
            Status = "Return protection is retained until this player reaches the island or leaves the session.";
            Log.LogWarning(Status);
        }
    }

    private bool Refuse(string reason) { Status = reason; return false; }

    private void OnSaveLifecycle(SaveLifecycleEvent value)
    {
        if (!OnThread || !_started || _disposed || value.Operation != "Load") return;
        if (value.Phase == "Before")
        {
            ++_loadGeneration;
            _loading = true;
            Invalidate();
        }
        else if (value.Phase == "After") _loading = false;
    }

    private void OnWorldChanged(WorldEvent value)
    {
        if (OnThread && _started && !_disposed && _capture != null
            && (GameRuntime.IsTitleScene || _capture.Identity(this) == IdentityState.Changed)) Invalidate();
    }

    private void Invalidate()
    {
        _capture?.ReleaseReturn();
        _capture = null;
        ++_epoch;
        ReturnRequested = false;
        Status = "The expedition ended because the player or world changed.";
        if (_disposed) RemoveHooks();
    }

    private void Unsubscribe()
    {
        SaveLifecycle.Lifecycle -= OnSaveLifecycle;
        GameRuntime.WorldChanged -= OnWorldChanged;
    }

    private void RemoveHooks()
    {
        Unsubscribe();
        if (_target != null && _postfix != null) _patches.Unpatch(_target, _postfix);
        _target = _postfix = null;
        _started = false;
        if (ReferenceEquals(_owner, this)) _owner = null;
    }

    private static void AfterSerialize(SavedPlayerState __instance, ref PlayerStateData __result)
    {
        var owner = _owner;
        if (owner == null) return;
        if (owner.OnThread && Valid(__instance) && __instance.Pointer == owner._serializingCapture)
            ++owner._captureSerializations;
        var capture = owner._capture;
        if (capture == null) return;
        if (!owner.OnThread)
        {
            // Do not call Unity identity/physics APIs from a serializer worker.
            // The exact retained state/data pair still has a protected snapshot.
            if (Same(__instance, capture.State) && Same(__instance._data, capture.Data))
                UseFallback(owner, capture, ref __result,
                    new InvalidOperationException("Player serialization ran outside the owning Unity thread."));
            return;
        }
        var identity = capture.Identity(owner);
        if (identity == IdentityState.Changed) { owner.Invalidate(); return; }
        // An unrelated serializer never receives this actor's retained state.
        if (!Same(__instance, capture.State) || !Same(__instance._data, capture.Data)) return;
        if (owner._disposed && capture.OnIsland(owner))
        { owner._capture = null; owner.RemoveHooks(); return; }
        try
        {
            // The exact serializer/data pair owns this save even while current
            // movement references are unavailable. Project only its location entries.
            var projected = Project(__result, capture);
            if (!ReferenceEquals(owner._capture, capture)) return;
            var after = capture.Identity(owner);
            if (after == IdentityState.Changed) { owner.Invalidate(); return; }
            var data = __instance._data;
            if (Valid(data) && !Same(data, capture.Data)) { owner.Invalidate(); return; }
            if (!Same(data, capture.Data))
                throw new InvalidOperationException("The expedition save data became unavailable during serialization.");
            __result = projected;
        }
        catch (Exception error)
        {
            UseFallback(owner, capture, ref __result, error);
            return;
        }
        try
        {
            // Keep a separate DTO, entries and arrays for the next fallback.
            var fallback = Project(__result, capture);
            Volatile.Write(ref capture.Fallback, fallback);
            if (Interlocked.Exchange(ref capture.SaveWarning, "").Length != 0)
                owner.Status = "Player save protection recovered.";
        }
        catch (Exception error)
        {
            // The current projected result is still valid if backup allocation fails.
            const string failed = "Player save projection succeeded, but its protected backup could not be refreshed.";
            if (Interlocked.Exchange(ref capture.SaveWarning, failed) != failed)
                Log.LogError(failed + " " + error);
            owner.Status = failed;
        }
    }

    private static void UseFallback(ClientSpacecraftExpedition owner, Capture capture,
        ref PlayerStateData result, Exception error)
    {
        // Use the latest protected copy before diagnostics. Saving a safe island
        // location does not require moving the live player or releasing crew custody.
        result = Volatile.Read(ref capture.Fallback)!;
        const string failed = "Player save protection used its last protected snapshot; newer player-state changes may not be saved.";
        if (Interlocked.Exchange(ref capture.SaveWarning, failed) != failed)
            Log.LogError(failed + " " + error);
        owner.Status = failed;
    }

    private static PlayerStateData Project(PlayerStateData source, Capture capture)
    {
        if (!Valid(source) || !Valid(source._entries))
            throw new InvalidOperationException("The native player save is unavailable.");
        var result = new PlayerStateData { Version = source.Version, _entries = new(), NamedInts = null };
        var entries = source._entries;
        var count = entries.Count;
        var copied = new bool[capture.Keys.Length];
        for (var i = 0; i < count; ++i)
        {
            var entry = entries[i];
            var key = entry == null ? -1 : capture.LocationIndex(entry.Name);
            if (key < 0) result._entries.Add(entry == null ? null! : Clone(entry));
            else if (!copied[key])
            {
                if (capture.Locations[key] is { } retained) result._entries.Add(Clone(retained));
                copied[key] = true;
            }
        }
        for (var i = 0; i < copied.Length; ++i)
            if (!copied[i] && capture.Locations[i] is { } retained) result._entries.Add(Clone(retained));
        if (!Same(entries, source._entries) || entries.Count != count)
            throw new InvalidOperationException("The native player entries changed during serialization.");
        var named = source.NamedInts;
        if (named != null)
        {
            if (!Valid(named)) throw new InvalidOperationException("The native named player state is unavailable.");
            result.NamedInts = new();
            var namedCount = named.Count;
            for (var i = 0; i < namedCount; ++i) result.NamedInts.Add(named[i]);
            if (!Same(named, source.NamedInts) || named.Count != namedCount)
                throw new InvalidOperationException("The native named player state changed during serialization.");
        }
        return result;
    }

    private static PersistentValue Clone(PersistentValue source)
    {
        if (!Valid(source)) throw new InvalidOperationException("A native player save entry is unavailable.");
        var copy = new PersistentValue
        {
            Name = source.Name, NameKey = source.NameKey, SettingType = source.SettingType,
            Version = source.Version, BoolValue = source.BoolValue, IntValue = source.IntValue,
            FloatValue = source.FloatValue, StringValue = source.StringValue,
            Protected = source.Protected, IsSet = source.IsSet, FloatArrayValue = null
        };
        var floats = source.FloatArrayValue;
        if (floats != null)
        {
            if (!Valid(floats)) throw new InvalidOperationException("A native player save array is unavailable.");
            var values = new Il2CppStructArray<float>(size: floats.Length);
            for (var i = 0; i < floats.Length; ++i) values[i] = floats[i];
            copy.FloatArrayValue = values;
        }
        return copy;
    }

    private enum IdentityState { Current, Changed, Unavailable }

    private sealed class Capture
    {
        internal readonly SavedPlayerState State;
        internal readonly PlayerStateData Data;
        internal readonly Transform Player;
        internal readonly Rigidbody Body;
        internal readonly Vector3 ActorPosition;
        internal readonly Vector3 Position;
        internal readonly Quaternion Rotation;
        internal readonly string[] Keys;
        internal readonly PersistentValue?[] Locations = new PersistentValue?[6];
        internal PlayerStateData? Fallback;
        internal string SaveWarning = "";
        private readonly PlayerInventory _inventory;
        private readonly PlayerStats _stats;
        private readonly FirstPersonCharacter _movement;
        private readonly SaveGameManager _manager;
        private readonly BoltEntity? _entity;
        private readonly Bolt.Entity? _attachment;
        private readonly IntPtr _server;
        private readonly uint _serverConnection;
        private readonly int _mode;
        private readonly long _generation, _epoch;
        private bool _returnHeld, _movementEnabled, _movementLocked;
        private RigidbodyConstraints _constraints;
        private long _nextReturnCheck;
        private Il2CppReferenceArray<Collider>? _returnOverlaps;

        internal Capture(ClientSpacecraftExpedition owner, long epoch, SavedPlayerState state, SaveGameManager manager,
            Transform player, Rigidbody body, PlayerInventory inventory, PlayerStats stats, FirstPersonCharacter movement,
            Vector3 returnPosition, Quaternion returnRotation)
        {
            State = state; Data = state._data; Player = player; Body = body;
            _inventory = inventory; _stats = stats; _movement = movement; _manager = manager;
            ActorPosition = player.position; Position = returnPosition; Rotation = returnRotation;
            _mode = NetworkMode;
            _generation = owner._loadGeneration; _epoch = epoch;
            _entity = _mode == 0 ? null : LocalPlayer.Entity;
            _attachment = _entity?._entity;
            var server = _mode == 2 ? BoltNetwork.server : null;
            _server = server?.Pointer ?? IntPtr.Zero;
            _serverConnection = server?.ConnectionId ?? 0;
            Keys = new[] { SavedPlayerState.PlayerPositionKey, SavedPlayerState.PlayerRotationKey, SavedPlayerState.PlayerAreaMaskKey,
                SavedPlayerState.PlayerLocalToRaftPositionKey, SavedPlayerState.PlayerLocalToRaftRotationKey, SavedPlayerState.PlayerLocalToRaftUniqueIdKey };
        }

        internal PlayerLifeState? Life
        {
            get
            {
                try { return Live(_stats) && Same(_stats, LocalPlayer.Stats)
                    ? SpacecraftLife.Read(_stats) : null; }
                catch (Exception) { return null; }
            }
        }

        internal bool Alive => Life == PlayerLifeState.Alive;

        internal IdentityState Identity(ClientSpacecraftExpedition owner)
        {
            try
            {
                if (owner._loading || owner._loadGeneration != _generation || owner._epoch != _epoch
                    || GameRuntime.IsTitleScene || !Live(State) || !Valid(Data) || !Live(_manager)
                    || !Live(Player) || !Live(Body) || !Live(_inventory) || !Live(_stats) || !Live(_movement)
                    || _mode != 0 && (!Live(_entity) || !Valid(_attachment))
                    || _mode == 2 && _server == IntPtr.Zero
                    || _mode != NetworkMode || _manager._loadingInProgress
                    || Life == PlayerLifeState.Dead) return IdentityState.Changed;

                var hasState = SavedPlayerState.TryGetInstance(out var state) && Live(state);
                var data = State._data;
                var hasManager = SaveGameManager.TryGetInstance(out var manager) && Live(manager);
                var player = LocalPlayer.Transform; var body = LocalPlayer.Rigidbody;
                var inventory = LocalPlayer.Inventory; var stats = LocalPlayer.Stats;
                var movement = LocalPlayer.FpCharacter;
                // A missing current reference does not replace a still-live capture.
                // Check all available identities before deciding that recovery must wait.
                if (hasState && !Same(State, state) || Valid(data) && !Same(Data, data)
                    || hasManager && !Same(_manager, manager)
                    || Live(player) && !Same(Player, player) || Live(body) && !Same(Body, body)
                    || Live(inventory) && !Same(_inventory, inventory) || Live(stats) && !Same(_stats, stats)
                    || Live(movement) && !Same(_movement, movement)) return IdentityState.Changed;
                var unavailable = !hasState || !Valid(data) || !hasManager || !Live(player) || !Live(body)
                    || !Live(inventory) || !Live(stats) || !Live(movement);
                if (_mode != 0)
                {
                    var entity = LocalPlayer.Entity;
                    var attachment = _entity!._entity;
                    if (Live(entity) && !Same(_entity, entity)
                        || Valid(attachment) && !Same(_attachment, attachment)) return IdentityState.Changed;
                    unavailable |= !Live(entity) || !Valid(attachment) || !_entity!.isAttached;
                }
                if (_mode == 2)
                {
                    var server = BoltNetwork.server;
                    if (!Valid(server)) unavailable = true;
                    else if (server.Pointer != _server || server.ConnectionId != _serverConnection)
                        return IdentityState.Changed;
                }
                return unavailable ? IdentityState.Unavailable : IdentityState.Current;
            }
            catch (Exception) { return IdentityState.Unavailable; }
        }

        internal bool OnIsland(ClientSpacecraftExpedition owner)
        {
            try { return Identity(owner) == IdentityState.Current && Alive && !LocalPlayer.IsInCaves
                && IslandPosition(Player.position) && !Body.isKinematic && Body.detectCollisions && !_returnHeld
                && (Body.position - Player.position).sqrMagnitude <= .25f
                && IslandSupport(Player); }
            catch (Exception) { return false; }
        }

        internal void HoldReturn()
        {
            if (!_returnHeld)
            {
                _constraints = Body.constraints;
                _movementEnabled = _movement.enabled;
                _movementLocked = _movement.Locked;
                _returnHeld = true;
                _nextReturnCheck = 0;
            }
            _movement.enabled = false;
            _movement.Locked = true;
            Body.constraints = RigidbodyConstraints.FreezeAll;
        }

        internal void ReleaseReturn()
        {
            if (!_returnHeld) return;
            // Release only this hold on the same native components, including
            // when a scene change has already invalidated the expedition lease.
            try
            {
                if (Live(Player) && Same(Player, LocalPlayer.Transform)
                    && Live(Body) && Same(Body, LocalPlayer.Rigidbody)
                    && Body.constraints == RigidbodyConstraints.FreezeAll)
                    Body.constraints = _constraints;
            }
            finally
            {
                if (!Live(Player) || !Same(Player, LocalPlayer.Transform)
                    || !Live(_movement) || !Same(_movement, LocalPlayer.FpCharacter))
                    _returnHeld = false;
                else if (Life is { } life)
                {
                    // Downing and get-up own native controls until revival finishes.
                    if (life == PlayerLifeState.Alive && !_stats._healing && !_stats._isHealing)
                    {
                        if (_movement.Locked) _movement.Locked = _movementLocked;
                        if (!_movement.enabled) _movement.enabled = _movementEnabled;
                    }
                    _returnHeld = false;
                }
            }
        }

        internal bool ReturnClear()
        {
            var now = Environment.TickCount64;
            if (now < _nextReturnCheck) return false;
            _nextReturnCheck = now + 250;
            return ClearAt(Position, requireIsland: true);
        }

        internal bool CanRelocate => !_returnHeld && !Body.isKinematic && _movement.enabled && !_movement.Locked;

        internal bool ClearAt(Vector3 position, bool requireIsland)
        {
            var capsule = _movement._primaryCollider;
            if (!Body.detectCollisions || !Live(capsule) || !capsule.enabled
                || !capsule.gameObject.activeInHierarchy || !Same(capsule.attachedRigidbody, Body)) return false;
            Physics.SyncTransforms();
            if (!StaticSupport(position, Player, requireIsland)) return false;
            var frame = capsule.transform;
            var scale = frame.lossyScale;
            scale = new Vector3(MathF.Abs(scale.x), MathF.Abs(scale.y), MathF.Abs(scale.z));
            var direction = capsule.direction;
            if (direction < 0 || direction > 2 || !Finite(scale)) return false;
            var axis = direction == 0 ? Vector3.right : direction == 1 ? Vector3.up : Vector3.forward;
            var radius = capsule.radius * Mathf.Max(scale[(direction + 1) % 3], scale[(direction + 2) % 3]);
            var height = capsule.height * scale[direction];
            var center = frame.TransformPoint(capsule.center) + position - Player.position;
            if (!Finite(center) || !float.IsFinite(radius) || radius <= 0
                || !float.IsFinite(height) || height <= 0) return false;
            var offset = frame.TransformDirection(axis).normalized * Mathf.Max(0, height * .5f - radius);
            // Allow normal resting contact, not a capsule penetrating a newly loaded wall.
            var skin = Mathf.Min(.02f, radius * .1f);
            var overlaps = _returnOverlaps ??= new(size: 64);
            try
            {
                var count = Physics.OverlapCapsuleNonAlloc(center - offset, center + offset,
                    radius - skin, overlaps, ~0, QueryTriggerInteraction.Ignore);
                if (count < 0 || count >= overlaps.Length) return false;
                for (var i = 0; i < count; i++)
                {
                    var other = overlaps[i];
                    if (!Live(other) || !Same(other.attachedRigidbody, Body)
                        && !Same(other.transform, Player) && !other.transform.IsChildOf(Player)) return false;
                }
                return true;
            }
            finally { for (var i = 0; i < overlaps.Length; i++) overlaps[i] = null!; }
        }

        internal int LocationIndex(string name)
        {
            for (var i = 0; i < Keys.Length; ++i)
                if (string.Equals(name, Keys[i], StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        internal void CaptureLocations(PlayerStateData source)
        {
            var entries = source._entries;
            if (!Valid(entries)) throw new InvalidOperationException("The island save has no player entries.");
            var count = entries.Count;
            for (var i = 0; i < Keys.Length; ++i)
            {
                if (string.IsNullOrWhiteSpace(Keys[i]) || LocationIndex(Keys[i]) != i)
                    throw new InvalidOperationException("The native location keys are unavailable or ambiguous.");
                var entry = source.GetEntry(Keys[i]);
                var found = 0;
                foreach (var candidate in entries)
                    if (candidate != null && LocationIndex(candidate.Name) == i)
                    {
                        if (!Same(entry, candidate)) throw new InvalidOperationException("A location key did not resolve to its native entry.");
                        ++found;
                    }
                if (entry == null ? found != 0 : found != 1)
                    throw new InvalidOperationException("The island save has ambiguous location entries.");
                Locations[i] = entry == null ? null : Clone(entry);
                var floats = Locations[i]?.FloatArrayValue;
                if (floats != null)
                    foreach (var number in floats)
                        if (!float.IsFinite(number)) throw new InvalidOperationException("An island location contains an invalid number.");
            }
            if (!Same(entries, source._entries) || entries.Count != count)
                throw new InvalidOperationException("The island location entries changed while being captured.");
            // Native serialized vectors do not require IsSet. Validate their
            // captured values, shape and type before replacing the retained pose.
            var position = Locations[0]?.FloatArrayValue;
            var rotation = Locations[1]?.FloatArrayValue;
            if (Locations[0] == null || Locations[1] == null
                || (int)Locations[0]!.SettingType != 4 || (int)Locations[1]!.SettingType != 4
                || position == null || position.Length != 3 || rotation == null || rotation.Length != 4
                || !IslandPosition(new Vector3(position[0], position[1], position[2]))
                || !ValidRotation(new Quaternion(rotation[0], rotation[1], rotation[2], rotation[3])))
                throw new InvalidOperationException("The native island position or rotation is missing or invalid.");

            var anchorPosition = new Il2CppStructArray<float>(size: 3);
            anchorPosition[0] = Position.x; anchorPosition[1] = Position.y; anchorPosition[2] = Position.z;
            var anchorRotation = new Il2CppStructArray<float>(size: 4);
            anchorRotation[0] = Rotation.x; anchorRotation[1] = Rotation.y;
            anchorRotation[2] = Rotation.z; anchorRotation[3] = Rotation.w;
            Locations[0]!.FloatArrayValue = anchorPosition;
            Locations[1]!.FloatArrayValue = anchorRotation;

            // Native non-raft saves use an empty GUID and zero local transforms.
            // Only edit these retained clones; a raft ID would override the anchor on load.
            for (var i = 3; i <= 4; ++i)
                if (Locations[i] is { } local)
                {
                    if ((int)local.SettingType != 4 || local.FloatArrayValue == null || local.FloatArrayValue.Length != i)
                        throw new InvalidOperationException("The native raft location has an unsupported shape.");
                    local.FloatArrayValue = new Il2CppStructArray<float>(size: i);
                }
            if (Locations[5] is { } raftId)
            {
                if ((int)raftId.SettingType != 3)
                    throw new InvalidOperationException("The native raft identity has an unsupported type.");
                raftId.StringValue = Guid.Empty.ToString("D");
            }
        }
    }

    // Both authored surface destinations start at 2500 m; space starts at 5000 m.
    // Cabin walking must not turn any of those positions into a new island anchor.
    private static bool IslandPosition(Vector3 value) => Finite(value) && value.y < 2000;
    private static bool AnchorSupport(Vector3 position, Transform player) => StaticSupport(position, player, requireIsland: true);

    private static bool StaticSupport(Vector3 position, Transform player, bool requireIsland)
    {
        var hits = Physics.RaycastAll(position + Vector3.up * .25f, Vector3.down,
            .75f, ~0, QueryTriggerInteraction.Ignore);
        var nearest = float.PositiveInfinity;
        RaycastHit support = default;
        foreach (var hit in hits)
        {
            var collider = hit.collider;
            if (!Live(collider) || !float.IsFinite(hit.distance) || hit.distance < 0 || hit.distance >= nearest
                || collider.transform.IsChildOf(player)) continue;
            nearest = hit.distance;
            support = hit;
        }
        var ground = support.collider;
        if (!Live(ground) || !(support.normal.y >= .5f) || requireIsland && !IslandPosition(support.point)
            || ground.attachedRigidbody) return false;
        var terrain = ground.GetComponent("TerrainCollider");
        if (Live(terrain) && terrain.Pointer == ground.Pointer) return true;
        var mesh = ground.GetComponent<MeshCollider>();
        return Live(mesh) && mesh.Pointer == ground.Pointer && ground.gameObject.isStatic;
    }
    private static bool IslandSupport(Transform player)
    {
        // Grounding flags can still describe the previous seated frame. Confirm
        // nearby static support, excluding the player's own collision hierarchy.
        var hits = Physics.RaycastAll(player.position + Vector3.up * .25f, Vector3.down,
            3f, ~0, QueryTriggerInteraction.Ignore);
        foreach (var hit in hits)
        {
            var collider = hit.collider;
            if (Live(collider) && hit.normal.y >= .5f && IslandPosition(hit.point)
                && !collider.transform.IsChildOf(player) && !collider.attachedRigidbody) return true;
        }
        return false;
    }
    private static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
    private static bool ValidRotation(Quaternion value) => float.IsFinite(value.x) && float.IsFinite(value.y)
        && float.IsFinite(value.z) && float.IsFinite(value.w)
        && MathF.Abs(value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w - 1) < .01f;
    private static int NetworkMode => !BoltNetwork.isRunning ? 0 : BoltNetwork.isServer ? 1 : 2;
    private static bool Valid([NotNullWhen(true)] Il2CppObjectBase? value) => value != null && !value.WasCollected && value.Pointer != IntPtr.Zero;
    private static bool Live([NotNullWhen(true)] UnityEngine.Object? value) => Valid(value) && value;
    private static bool Same(Il2CppObjectBase? first, Il2CppObjectBase? second)
        => Valid(first) && Valid(second) && first.Pointer == second.Pointer;
}
