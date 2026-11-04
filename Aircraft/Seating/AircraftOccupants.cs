// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Il2CppInterop.Runtime.InteropTypes;
using RootMotion;
using RootMotion.FinalIK;
using Sons.Multiplayer.Client;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Client;

// A cosmetic layer over the remote rig, after native animation and IK. Never
// writes remote entity transforms, animator parameters, solvers or network state.
internal sealed class ClientAircraftOccupants : IDisposable
{
    private readonly Dictionary<ClientAircraftBody, Occupant?[]> _planes = new();
    private readonly List<ClientAircraftBody> _removed = new();

    internal void SetOccupants(ClientAircraftBody body, ulong[] actorIds)
    {
        if (actorIds == null || actorIds.Length != 3) throw new ArgumentException("An aircraft has three seats.", nameof(actorIds));
        if (!_planes.TryGetValue(body, out var seats))
        {
            if (actorIds[0] == 0 && actorIds[1] == 0 && actorIds[2] == 0) return;
            seats = new Occupant?[3];
            _planes.Add(body, seats);
        }
        for (var i = 0; i < seats.Length; i++)
        {
            if ((seats[i]?.Actor ?? 0) == actorIds[i]) continue;
            seats[i]?.Dispose();
            seats[i] = actorIds[i] == 0 ? null : new Occupant(actorIds[i], body);
            seats[i]?.Update();
        }
        if (seats[0] == null && seats[1] == null && seats[2] == null) _planes.Remove(body);
    }

    internal void Remove(ClientAircraftBody body)
    {
        if (!_planes.Remove(body, out var seats)) return;
        DisposeSeats(seats);
    }

    internal void Update()
    {
        _removed.Clear();
        foreach (var pair in _planes)
        {
            if (!Live(pair.Key.Root)) { _removed.Add(pair.Key); continue; }
            foreach (var occupant in pair.Value) occupant?.Update();
        }
        foreach (var body in _removed) Remove(body);
        _removed.Clear();
    }

    // Shared late frame callback must call this after the remote tablet/watch layer.
    internal void LateUpdate()
    {
        foreach (var pair in _planes)
        {
            if (!Live(pair.Key.Root)) continue;
            for (var seat = 0; seat < pair.Value.Length; seat++)
                pair.Value[seat]?.Render(seat);
        }
    }

    public void Dispose()
    {
        Exception? failure = null;
        foreach (var seats in _planes.Values)
            try { DisposeSeats(seats); } catch (Exception error) { failure ??= error; }
        _planes.Clear(); _removed.Clear();
        if (failure != null) throw failure;
    }

    private static void DisposeSeats(Occupant?[] seats)
    {
        Exception? failure = null;
        foreach (var seat in seats)
            try { seat?.Dispose(); } catch (Exception error) { failure ??= error; }
        if (failure != null) throw failure;
    }

    private sealed class Occupant(ulong actor, ClientAircraftBody aircraft) : IDisposable
    {
        internal readonly ulong Actor = actor;
        private Pose? _pose;
        private Collisions? _collisions;
        private float _nextBind;
        private bool _retired;

        internal void Update()
        {
            if (_collisions != null && !_collisions.Current)
            {
                _retired = !_collisions.SameAttachment;
                _collisions.Dispose(); _collisions = null;
            }
            _collisions?.Maintain();
            if (_pose != null)
            {
                if (!_pose.Current)
                {
                    _retired |= !_pose.SameAttachment;
                    _pose.Dispose(); _pose = null;
                }
                else _pose.Restore();
            }
            if (_retired || _pose != null && _collisions != null || Time.unscaledTime < _nextBind) return;
            _nextBind = Time.unscaledTime + .5f;
            _collisions ??= Collisions.TryCreate(aircraft, Actor);
            _pose ??= Pose.TryCreate(Actor);
        }

        internal void Render(int seat)
        {
            if (_pose?.Current == true && aircraft.TryCrewTargets(seat, out var targets)) _pose.Render(targets, seat);
        }

        public void Dispose()
        {
            var pose = _pose; _pose = null;
            var collisions = _collisions; _collisions = null;
            try { pose?.Dispose(); }
            finally { collisions?.Dispose(); }
        }
    }

    // Physics is independent of the render rig and also runs on a headless host.
    private sealed class Collisions : IDisposable
    {
        private readonly ClientAircraftBody _aircraft;
        private readonly BoltEntity _entity;
        private readonly Bolt.Entity _attachment;
        private readonly ulong _actor;
        private readonly List<(Collider Plane, Collider Player)> _changed = new();

        private Collisions(ClientAircraftBody aircraft, BoltEntity entity)
        {
            _aircraft = aircraft; _entity = entity;
            _attachment = entity._entity; _actor = entity.networkId.PackedValue;
        }

        internal static Collisions? TryCreate(ClientAircraftBody aircraft, ulong actor)
        {
            if (!BoltNetwork.isRunning || !Live(aircraft.Body)) return null;
            var entity = BoltNetwork.FindEntity(new Bolt.NetworkId(actor));
            if (!Live(entity) || !entity.isAttached || Same(entity, LocalPlayer.Entity)
                || entity._entity == null || entity._entity.WasCollected) return null;
            var planes = aircraft.Body.GetComponentsInChildren<Collider>(true);
            var hierarchy = entity.GetComponentsInChildren<Collider>(true);
            if (planes == null || planes.WasCollected || planes.Length > 48
                || hierarchy == null || hierarchy.WasCollected) return null;
            // Carried equipment adds hundreds of trigger colliders, more so before a player's
            // first equip; only solid colliders can push the airframe.
            var players = new System.Collections.Generic.List<Collider>();
            foreach (var collider in hierarchy)
                if (Live(collider) && !collider.isTrigger) players.Add(collider);
            if (players.Count > 512) return null;
            var lease = new Collisions(aircraft, entity);
            try
            {
                foreach (var plane in planes)
                    foreach (var player in players)
                    {
                        if (!Live(plane) || !Live(player) || Physics.GetIgnoreCollision(plane, player)) continue;
                        // Retain the pair before the native call, including a partial failure.
                        lease._changed.Add((plane, player));
                        Physics.IgnoreCollision(plane, player, true);
                    }
                return lease;
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }

        internal bool SameAttachment
        {
            get
            {
                try
                {
                    return BoltNetwork.isRunning && Live(_entity) && _entity.isAttached
                        && _entity.networkId.PackedValue == _actor && Same(_entity._entity, _attachment)
                        && Same(BoltNetwork.FindEntity(new Bolt.NetworkId(_actor)), _entity);
                }
                catch (Exception) { return false; }
            }
        }

        internal bool Current => SameAttachment && Live(_aircraft.Body) && Live(_aircraft.Root);

        internal void Maintain()
        {
            // Unity clears pair ignores when a collider is deactivated. Native
            // avatar animation can reactivate one while its seat is still occupied.
            foreach (var pair in _changed)
                if (Live(pair.Plane) && Live(pair.Player) && pair.Plane.enabled && pair.Player.enabled
                    && pair.Plane.gameObject.activeInHierarchy && pair.Player.gameObject.activeInHierarchy
                    && !Physics.GetIgnoreCollision(pair.Plane, pair.Player))
                    Physics.IgnoreCollision(pair.Plane, pair.Player, true);
        }

        public void Dispose()
        {
            Exception? failure = null;
            foreach (var pair in _changed)
                try
                {
                    if (Live(pair.Plane) && Live(pair.Player) && Physics.GetIgnoreCollision(pair.Plane, pair.Player))
                        Physics.IgnoreCollision(pair.Plane, pair.Player, false);
                }
                catch (Exception error) { failure ??= error; }
            _changed.Clear();
            if (failure != null) throw failure;
        }
    }

    private sealed class Pose : IDisposable
    {
        private readonly BoltEntity _entity;
        private readonly Bolt.Entity _attachment;
        private readonly ulong _actor;
        private readonly CoopPlayerRemoteSetup _setup;
        private readonly Animator _animator;
        private readonly BipedIK _biped;
        private readonly BipedReferences _references;
        private readonly Transform _mesh;
        private readonly ClientAircraftCrewPose _crew;
        private readonly List<Renderer> _hidden = new();
        private bool _disposed;
        private int _frame = -1;

        private Pose(BoltEntity entity, CoopPlayerRemoteSetup setup, Animator animator,
            BipedIK biped, BipedReferences references, ClientAircraftCrewPose crew)
        {
            _entity = entity; _attachment = entity._entity; _actor = entity.networkId.PackedValue;
            _setup = setup; _animator = animator; _biped = biped; _references = references;
            _mesh = animator.transform; _crew = crew;
        }

        internal static Pose? TryCreate(ulong actor)
        {
            if (!Live(LocalPlayer.MainCam) || !BoltNetwork.isRunning) return null;
            var entity = BoltNetwork.FindEntity(new Bolt.NetworkId(actor));
            if (!Live(entity) || !entity.isAttached || Same(entity, LocalPlayer.Entity)
                || entity._entity == null || entity._entity.WasCollected) return null;
            var setups = entity.GetComponentsInChildren<CoopPlayerRemoteSetup>(true);
            if (setups == null || setups.WasCollected || setups.Length > 8) return null;
            CoopPlayerRemoteSetup? setup = null;
            foreach (var candidate in setups)
            {
                if (!Live(candidate) || !Same(candidate._entity, entity)) continue;
                if (setup != null) return null;
                setup = candidate;
            }
            if (!Live(setup) || !Live(setup._playerAnimator)) return null;
            var animator = setup._playerAnimator;
            if (!animator.isActiveAndEnabled || Same(animator.transform, entity.transform)
                || !animator.transform.IsChildOf(entity.transform)) return null;
            var bipeds = animator.GetComponents<BipedIK>();
            if (bipeds == null || bipeds.WasCollected || bipeds.Length != 1 || !Live(bipeds[0])) return null;
            var biped = bipeds[0]; var references = biped.references;
            var crew = ClientAircraftCrewPose.TryCreate(entity.transform, biped, false);
            return crew == null ? null : new Pose(entity, setup, animator, biped, references, crew);
        }

        internal bool SameAttachment
        {
            get
            {
                try
                {
                    return BoltNetwork.isRunning && Live(_entity) && _entity.isAttached
                        && !Same(_entity, LocalPlayer.Entity) && _entity.networkId.PackedValue == _actor
                        && Same(_entity._entity, _attachment)
                        && Same(BoltNetwork.FindEntity(new Bolt.NetworkId(_actor)), _entity);
                }
                catch (Exception) { return false; }
            }
        }

        internal bool Current
        {
            get
            {
                try
                {
                    if (_disposed || !SameAttachment || !Live(_setup) || !Same(_setup._entity, _entity)
                        || !Live(_animator) || !_animator.isActiveAndEnabled || !Same(_setup._playerAnimator, _animator)
                        || !Live(_biped) || !Same(_biped.references, _references) || !Live(_mesh)
                        || !Same(_animator.transform, _mesh) || !Same(_biped.transform, _mesh)) return false;
                    return _crew.Current;
                }
                catch (Exception) { return false; }
            }
        }

        internal void Render(AircraftCrewTargets targets, int seat)
        {
            if (_frame == Time.frameCount || !Current) return;
            _frame = Time.frameCount;
            Restore();
            _crew.Render(targets, seat);
            HideHeldItems();
        }

        private void HideHeldItems()
        {
            var items = _setup._equippedItemInstances; var held = _setup._currentHeldItems;
            if (items == null || items.WasCollected || items.Count > 32
                || held == null || held.WasCollected || held.Count > 32) return;
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null || item.WasCollected || !held.Contains(item._itemID)) continue;
                var itemObject = item._ItemObject_k__BackingField;
                if (!Live(itemObject)) continue;
                var renderers = itemObject.GetComponentsInChildren<Renderer>(true);
                if (renderers == null || renderers.WasCollected || renderers.Length > 32) continue;
                foreach (var renderer in renderers)
                    if (Live(renderer) && renderer.enabled) { _hidden.Add(renderer); renderer.enabled = false; }
            }
        }

        internal void Restore()
        {
            // A network ID is reusable. Old pose state must never touch its replacement.
            if (!SameAttachment) { _hidden.Clear(); return; }
            Exception? failure = null;
            foreach (var renderer in _hidden)
                try { if (Live(renderer) && !renderer.enabled) renderer.enabled = true; }
                catch (Exception error) { failure ??= error; }
            _hidden.Clear();
            try { _crew.Restore(); }
            catch (Exception error) { failure ??= error; }
            if (failure != null) throw failure;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Restore();
        }
    }

    private static bool Live([NotNullWhen(true)] UnityEngine.Object? value) => value != null && !value.WasCollected && value;
    private static bool Same(Il2CppObjectBase? first, Il2CppObjectBase? second)
        => first != null && second != null && !first.WasCollected && !second.WasCollected && first.Pointer == second.Pointer;
}
