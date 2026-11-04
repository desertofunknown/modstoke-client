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

internal readonly record struct SpacecraftCrewInteraction(ulong Actor, Vector3 LocalTarget, float Reach)
{
    internal Quaternion? LocalRotation { get; init; }
    internal bool Press { get; init; }
    // Local sample metadata; the wire carries the target and guard state together.
    internal float? GuardAmount { get; init; }

    internal bool Active => float.IsFinite(Reach) && Reach > 0 && Reach <= 1
        && float.IsFinite(LocalTarget.x) && float.IsFinite(LocalTarget.y) && float.IsFinite(LocalTarget.z);

    internal SpacecraftCrewInteraction SampleGuard(float amount)
        => this with { GuardAmount = Active && LocalRotation.HasValue
            && ClientSpacecraftStations.IsApuGuardContact(LocalTarget, amount) ? amount : null };

    internal static SpacecraftCrewInteraction Advance(SpacecraftCrewInteraction current, SpacecraftCrewInteraction requested, float dt)
    {
        dt = Mathf.Clamp(dt, 0, .05f);
        if (!requested.Active) return current with { Reach = Mathf.MoveTowards(current.Reach, 0, dt / .25f) };
        var same = current.Actor == requested.Actor && current.Press == requested.Press
            && current.LocalRotation.HasValue == requested.LocalRotation.HasValue
            && (!current.LocalRotation.HasValue || Quaternion.Angle(current.LocalRotation.Value, requested.LocalRotation!.Value) < 1)
            && (current.LocalTarget - requested.LocalTarget).sqrMagnitude < .0001f;
        if (!same && current.Active && current.Actor == requested.Actor && current.Press == requested.Press
            && current.GuardAmount is { } from && requested.GuardAmount is { } to
            && current.LocalRotation is { } rotation && requested.LocalRotation is { } targetRotation)
        {
            var contact = ClientSpacecraftStations.MoveApuGuardContact(
                new(current.LocalTarget, rotation, current.Press), from, to);
            same = Quaternion.Angle(contact.Rotation, targetRotation) < 1
                && (contact.Position - requested.LocalTarget).sqrMagnitude < .0001f;
        }
        return requested with { Reach = Mathf.MoveTowards(same ? current.Reach : 0, requested.Reach, dt / .18f) };
    }
}

// A cosmetic layer over the remote rig, after native animation and IK. Never
// writes remote entity transforms, animator parameters, solvers or network state.
internal sealed class ClientSpacecraftOccupants : IDisposable
{
    private readonly Dictionary<ClientSpacecraftBody, Occupant?[]> _ships = new();
    private readonly List<ClientSpacecraftBody> _removed = new();

    internal void Set(ClientSpacecraftBody body, ulong[] actorIds, bool[]? walking = null,
        SpacecraftCrewInteraction[]? interactions = null)
    {
        if (actorIds == null || actorIds.Length != 3) throw new ArgumentException("A spacecraft has three seats.", nameof(actorIds));
        if (walking != null && walking.Length != 3) throw new ArgumentException("A spacecraft has three crew berths.", nameof(walking));
        if (interactions != null && interactions.Length != 3) throw new ArgumentException("A spacecraft has three crew interactions.", nameof(interactions));
        if (!_ships.TryGetValue(body, out var seats))
        {
            if (actorIds[0] == 0 && actorIds[1] == 0 && actorIds[2] == 0) return;
            seats = new Occupant?[3];
            _ships.Add(body, seats);
        }
        for (var i = 0; i < seats.Length; i++)
        {
            if ((seats[i]?.Actor ?? 0) != actorIds[i])
            {
                seats[i]?.Dispose();
                seats[i] = actorIds[i] == 0 ? null : new Occupant(actorIds[i], body, i);
            }
            seats[i]?.SetWalking(walking?[i] == true);
            seats[i]?.SetInteraction(interactions?[i] ?? default);
            seats[i]?.Update();
        }
        if (seats[0] == null && seats[1] == null && seats[2] == null) _ships.Remove(body);
    }

    internal void Remove(ClientSpacecraftBody body)
    {
        if (!_ships.Remove(body, out var seats)) return;
        DisposeSeats(seats);
    }

    internal void SetHeldExtinguisher(BoxStationFirefightingHands owner, BoxStationModel? station, SpacecraftHeldStationTool held)
    {
        if (!held.Active || held.Actor == 0) return;
        foreach (var seats in _ships.Values)
            foreach (var occupant in seats)
                if (occupant?.Actor == held.Actor) occupant.SetHeldExtinguisher(owner, station, held);
    }

    internal bool TryHeldExtinguisherPose(BoxStationFirefightingHands owner, SpacecraftHeldStationTool held,
        out Vector3 position, out Quaternion rotation)
    {
        position = default; rotation = Quaternion.identity;
        if (held.Actor == 0) return false;
        foreach (var seats in _ships.Values)
            foreach (var occupant in seats)
                if (occupant?.Actor == held.Actor
                    && occupant.TryHeldExtinguisherPose(owner, held, out position, out rotation)) return true;
        return false;
    }

    internal void ClearHeldExtinguishers(BoxStationFirefightingHands owner)
    {
        foreach (var seats in _ships.Values)
            foreach (var occupant in seats) occupant?.ClearHeldExtinguisher(owner);
    }

    internal void Update()
    {
        _removed.Clear();
        foreach (var pair in _ships)
        {
            if (!pair.Key.Ready || !Live(pair.Key.Root)) { _removed.Add(pair.Key); continue; }
            foreach (var occupant in pair.Value) occupant?.Update();
        }
        foreach (var body in _removed) Remove(body);
        _removed.Clear();
    }

    // Shared late frame callback must call this after the remote tablet/watch layer.
    internal void LateUpdate()
    {
        foreach (var pair in _ships)
        {
            if (!pair.Key.Ready || !Live(pair.Key.Root)) continue;
            for (var seat = 0; seat < pair.Value.Length; seat++)
                pair.Value[seat]?.Render(seat);
        }
    }

    public void Dispose()
    {
        Exception? failure = null;
        foreach (var seats in _ships.Values)
            try { DisposeSeats(seats); } catch (Exception error) { failure ??= error; }
        _ships.Clear(); _removed.Clear();
        if (failure != null) throw failure;
    }

    private static void DisposeSeats(Occupant?[] seats)
    {
        Exception? failure = null;
        foreach (var seat in seats)
            try { seat?.Dispose(); } catch (Exception error) { failure ??= error; }
        if (failure != null) throw failure;
    }

    private sealed class Occupant(ulong actor, ClientSpacecraftBody spacecraft, int seat) : IDisposable
    {
        internal readonly ulong Actor = actor;
        private Pose? _pose;
        private Collisions? _collisions;
        private float _nextBind;
        private bool _retired;
        private float _sitBlend;
        private int _sitFrame = -1;
        private int _updateFrame = -1;
        private bool _walking;
        private SpacecraftCrewInteraction _interaction;
        private SpacecraftCrewInteraction _renderInteraction;
        private BoxStationModel? _extinguisherStation;
        private BoxStationFirefightingHands? _extinguisherOwner;
        private SpacecraftHeldStationTool _extinguisher;
        private Vector3 _extinguisherPosition;
        private Quaternion _extinguisherRotation;
        private int _extinguisherFrame = -1, _extinguisherPoseFrame = -1;

        internal void SetWalking(bool walking)
        {
            if (_walking == walking) return;
            _pose?.Restore();
            _walking = walking;
            _interaction = default;
            _renderInteraction = default;
            ClearHeldExtinguisher(_extinguisherOwner);
            if (!walking) _sitBlend = 0;
            if (_sitBlend <= 0) spacecraft.ResetSeatAdjustment(seat);
        }

        internal void SetInteraction(SpacecraftCrewInteraction interaction)
            => _interaction = _walking && interaction.Actor == Actor && interaction.Active
                ? interaction.SampleGuard(spacecraft.State.Systems.ApuGuardAmount) : default;

        internal void SetHeldExtinguisher(BoxStationFirefightingHands owner, BoxStationModel? station, SpacecraftHeldStationTool held)
        {
            if (held.Actor != Actor || !CanHoldExtinguisher(station, held)) return;
            if (!ReferenceEquals(owner, _extinguisherOwner) || !_extinguisher.SameHolder(held))
                ClearHeldExtinguisher(_extinguisherOwner);
            _extinguisherOwner = owner;
            _extinguisherStation = station;
            _extinguisher = held;
            _extinguisherFrame = Time.frameCount;
        }

        internal bool TryHeldExtinguisherPose(BoxStationFirefightingHands owner, SpacecraftHeldStationTool held,
            out Vector3 position, out Quaternion rotation)
        {
            position = _extinguisherPosition; rotation = _extinguisherRotation;
            return _extinguisherPoseFrame == Time.frameCount && ReferenceEquals(owner, _extinguisherOwner)
                && _extinguisher.SameHolder(held) && CanHoldExtinguisher(_extinguisherStation, held);
        }

        internal void ClearHeldExtinguisher(BoxStationFirefightingHands? owner)
        {
            if (!ReferenceEquals(owner, _extinguisherOwner)) return;
            if (_extinguisherPoseFrame >= 0) _pose?.Restore();
            _extinguisherOwner = null;
            _extinguisherStation = null;
            _extinguisher = default;
            _extinguisherFrame = _extinguisherPoseFrame = -1;
        }

        private bool CanHoldExtinguisher(BoxStationModel? station, SpacecraftHeldStationTool held)
            => !_retired && !IsLocal && _walking && _sitBlend <= 0 && spacecraft.Ready
                && spacecraft.TelemetryCurrent && spacecraft.State.Travel == SpacecraftTravel.None
                && (held.Kind == SpacecraftHeldStationToolKind.Engineering
                    || station == null && held.Kind == SpacecraftHeldStationToolKind.Extinguisher
                    || spacecraft.State.Docked && station?.Visible == true)
                && _pose?.CanHoldExtinguisher(station, spacecraft, held) == true;

        internal void Update()
        {
            if (_updateFrame == Time.frameCount) return;
            _updateFrame = Time.frameCount;
            // A headless host still settles occupied chairs without a render rig.
            if (!Live(LocalPlayer.MainCam)) AdvanceSit();
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
            _extinguisherPoseFrame = -1;
            if (_retired || _pose != null && _collisions != null || Time.unscaledTime < _nextBind) return;
            _nextBind = Time.unscaledTime + .5f;
            _collisions ??= Collisions.TryCreate(spacecraft, Actor);
            _pose ??= Pose.TryCreate(Actor);
        }

        internal void Render(int seat)
        {
            _extinguisherPoseFrame = -1;
            if (_pose?.Current != true) return;
            // Start with the bound pose so late rig discovery cannot skip the sit.
            AdvanceSit();
            if (_walking)
            {
                if (_sitBlend > 0)
                    _pose.Render(spacecraft.SeatRestTargets(seat), seat, Mathf.SmoothStep(0, 1, _sitBlend));
                else if (_extinguisherFrame == Time.frameCount && _extinguisherOwner != null
                    && (_extinguisher.Kind == SpacecraftHeldStationToolKind.Extinguisher
                        || _extinguisher.Kind == SpacecraftHeldStationToolKind.Engineering && _extinguisher.WorkProgress >= 0
                        || !_interaction.Active && !_renderInteraction.Active)
                    && CanHoldExtinguisher(_extinguisherStation, _extinguisher))
                {
                    _renderInteraction = default;
                    var up = spacecraft.State.Docked && _extinguisherStation != null
                        ? _extinguisherStation.Root.transform.up : spacecraft.Root.transform.up;
                    if (_pose.RenderHeldExtinguisher(_extinguisher, up,
                        out _extinguisherPosition, out _extinguisherRotation))
                        _extinguisherPoseFrame = Time.frameCount;
                }
                else
                {
                    _renderInteraction = SpacecraftCrewInteraction.Advance(_renderInteraction, _interaction, Time.unscaledDeltaTime);
                    if (_renderInteraction.Active)
                        _pose.RenderRightHand(spacecraft.Root.transform.TransformPoint(_renderInteraction.LocalTarget),
                            spacecraft.Root.transform.up, _renderInteraction.Reach,
                            _renderInteraction.LocalRotation is { } rotation ? spacecraft.Root.transform.rotation * rotation : null,
                            _renderInteraction.Press);
                }
                return;
            }
            var pose = spacecraft.ConsolePose(seat, Actor);
            var targets = ClientSpacecraftRider.CrewTargets(spacecraft, seat, pose.Control, pose.Reach);
            _pose.Render(targets, seat, Mathf.SmoothStep(0, 1, _sitBlend));
        }

        private void AdvanceSit()
        {
            // The local rider owns its holster/sit timing, not this replica clock.
            if (IsLocal || _retired || _sitFrame == Time.frameCount) return;
            _sitFrame = Time.frameCount;
            _sitBlend = Mathf.MoveTowards(_sitBlend, _walking ? 0 : 1,
                Mathf.Clamp(Time.unscaledDeltaTime, 0, .05f) / (_walking ? .55f : .72f));
            spacecraft.SetSeatSitProgress(seat, _sitBlend);
        }

        private bool IsLocal => Live(LocalPlayer.Entity) && LocalPlayer.Entity.isAttached
            && LocalPlayer.Entity.networkId.PackedValue == Actor;

        public void Dispose()
        {
            var pose = _pose; _pose = null;
            var collisions = _collisions; _collisions = null;
            try { pose?.Dispose(); }
            finally
            {
                try { collisions?.Dispose(); }
                finally { spacecraft.ResetSeatAdjustment(seat); }
            }
        }
    }

    // Physics is independent of the render rig and also runs on a headless host.
    private sealed class Collisions : IDisposable
    {
        private readonly ClientSpacecraftBody _spacecraft;
        private readonly BoltEntity _entity;
        private readonly Bolt.Entity _attachment;
        private readonly ulong _actor;
        private readonly List<ColliderState> _ships = new(), _players = new();
        private readonly List<(ColliderState Ship, ColliderState Player)> _changed = new();
        private float _nextReconcile;

        private Collisions(ClientSpacecraftBody spacecraft, BoltEntity entity)
        {
            _spacecraft = spacecraft; _entity = entity;
            _attachment = entity._entity; _actor = entity.networkId.PackedValue;
        }

        internal static Collisions? TryCreate(ClientSpacecraftBody spacecraft, ulong actor)
        {
            if (!BoltNetwork.isRunning || !spacecraft.Ready || !Live(spacecraft.Root)) return null;
            var entity = BoltNetwork.FindEntity(new Bolt.NetworkId(actor));
            if (!Live(entity) || !entity.isAttached || Same(entity, LocalPlayer.Entity)
                || entity._entity == null || entity._entity.WasCollected) return null;
            // The landing ramp collider moves below the ship root. Include it
            // with the fixed hull while this remote actor owns an occupied seat.
            var ships = spacecraft.Root.GetComponentsInChildren<Collider>(true);
            var hierarchy = entity.GetComponentsInChildren<Collider>(true);
            if (ships == null || ships.WasCollected || ships.Length > 128
                || hierarchy == null || hierarchy.WasCollected) return null;
            // Carried equipment adds hundreds of trigger colliders, more so before a player's
            // first equip; only solid colliders can push the hull.
            var players = new System.Collections.Generic.List<Collider>();
            foreach (var collider in hierarchy)
                if (Live(collider) && !collider.isTrigger) players.Add(collider);
            if (players.Count > 512) return null;
            var lease = new Collisions(spacecraft, entity);
            try
            {
                foreach (var ship in ships) if (Live(ship)) lease._ships.Add(new ColliderState(ship));
                foreach (var player in players) if (Live(player)) lease._players.Add(new ColliderState(player));
                foreach (var ship in lease._ships)
                    foreach (var player in lease._players)
                    {
                        if (Physics.GetIgnoreCollision(ship.Collider, player.Collider)) continue;
                        // Retain the pair before the native call, including a partial failure.
                        lease._changed.Add((ship, player));
                        ship.Partners.Add(player); player.Partners.Add(ship);
                        Physics.IgnoreCollision(ship.Collider, player.Collider, true);
                    }
                lease._nextReconcile = Time.unscaledTime + .5f;
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

        internal bool Current => SameAttachment && _spacecraft.Ready && Live(_spacecraft.Root);

        internal void Maintain()
        {
            // Unity clears pair ignores when a collider is deactivated. Native
            // avatar animation can reactivate one while its seat is still occupied.
            // Read activation once per collider, not once per ship/player pair.
            foreach (var ship in _ships) ship.Refresh();
            foreach (var player in _players) player.Refresh();
            var reapply = false;
            foreach (var ship in _ships) reapply |= ship.NeedsReapply();
            foreach (var player in _players) reapply |= player.NeedsReapply();
            // Reconcile individual pair changes made outside this lease as well.
            var reconcile = Time.unscaledTime >= _nextReconcile;
            if (!reapply && !reconcile) return;
            if (reconcile) _nextReconcile = Time.unscaledTime + .5f;
            foreach (var pair in _changed)
                if (pair.Ship.Active && pair.Player.Active
                    && (pair.Ship.Reapply || pair.Player.Reapply
                        || reconcile && !Physics.GetIgnoreCollision(pair.Ship.Collider, pair.Player.Collider)))
                    Physics.IgnoreCollision(pair.Ship.Collider, pair.Player.Collider, true);
        }

        public void Dispose()
        {
            Exception? failure = null;
            foreach (var pair in _changed)
                try
                {
                    if (Live(pair.Ship.Collider) && Live(pair.Player.Collider)
                        && Physics.GetIgnoreCollision(pair.Ship.Collider, pair.Player.Collider))
                        Physics.IgnoreCollision(pair.Ship.Collider, pair.Player.Collider, false);
                }
                catch (Exception error) { failure ??= error; }
            _changed.Clear(); _ships.Clear(); _players.Clear();
            if (failure != null) throw failure;
        }

        private sealed class ColliderState
        {
            internal readonly Collider Collider;
            internal readonly List<ColliderState> Partners = new();
            internal bool Active, Reapply;
            private ColliderState? _activePartner;

            internal ColliderState(Collider collider)
            {
                Collider = collider;
                Active = collider.enabled && collider.gameObject.activeInHierarchy;
            }

            internal void Refresh()
            {
                var active = Live(Collider) && Collider.enabled && Collider.gameObject.activeInHierarchy;
                Reapply = active && !Active;
                Active = active;
            }

            internal bool NeedsReapply()
            {
                if (!Active || Reapply) return Reapply;
                if (_activePartner?.Active != true)
                {
                    _activePartner = null;
                    foreach (var partner in Partners)
                        if (partner.Active) { _activePartner = partner; break; }
                }
                // Deactivation clears every ignore involving this collider. One
                // active owned pair detects even an off/on cycle between frames.
                return Reapply = _activePartner != null
                    && !Physics.GetIgnoreCollision(Collider, _activePartner.Collider);
            }
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

        internal void Render(AircraftCrewTargets targets, int seat, float sitBlend)
        {
            if (_frame == Time.frameCount || !Current) return;
            _frame = Time.frameCount;
            Restore();
            _crew.Render(targets, seat, sitBlend);
            HideHeldItems();
        }

        internal void RenderRightHand(Vector3 target, Vector3 up, float reach, Quaternion? rotation, bool press)
        {
            if (_frame == Time.frameCount || !Current) return;
            _frame = Time.frameCount;
            Restore();
            _crew.RenderRightHand(target, up, reach, rotation, press);
            HideHeldItems();
        }

        internal bool CanHoldExtinguisher(BoxStationModel? station, ClientSpacecraftBody spacecraft, SpacecraftHeldStationTool held)
        {
            if (!Current) return false;
            var state = _entity.GetState<IPlayerState>();
            return state != null && !state.WasCollected && state.Dying == 0
                && (held.IsShipSupply || held.Kind == SpacecraftHeldStationToolKind.Engineering
                    || station == null && held.Kind == SpacecraftHeldStationToolKind.Extinguisher
                    ? spacecraft.ContainsCrewWalkArea(_entity.transform.position)
                    : station?.ContainsWalkArea(_entity.transform.position) == true);
        }

        internal bool RenderHeldExtinguisher(SpacecraftHeldStationTool held, Vector3 up,
            out Vector3 position, out Quaternion rotation)
        {
            position = default; rotation = Quaternion.identity;
            if (_frame == Time.frameCount || !Current) return false;
            _frame = Time.frameCount;
            Restore();
            bool rendered = held.Render(_crew, _references.rightUpperArm.position, _entity.transform.rotation,
                up, out position, out rotation);
            if (rendered) HideHeldItems();
            return rendered;
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
