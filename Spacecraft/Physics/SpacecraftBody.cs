// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftBody : IDisposable
{
    // Playable thruster tuning for this game craft, not a physical specification.
    private const float Mass = 12000;
    private const float FootY = -3.6225f;
    private static readonly Vector3 EntryFloorAft = new(.0015f, -2.7761f, -14.6526f);
    private static readonly Vector3 EntryFloorForward = new(-.05f, -1.26f, -8.95f);
    private static readonly Vector3[] Feet =
    {
        new(0, FootY, 7.11f), new(-4.95f, FootY, -7.76f), new(4.95f, FootY, -7.76f)
    };
    private readonly bool _authoritative;
    private Rigidbody _body = null!;
    private PhysicMaterial? _contact;
    private ClientSpacecraftModel? _model;
    private SpacecraftFlightState _replica;
    private SpacecraftSystemState _systems;
    private long _replicaAt;
    private SpacecraftRegion _region;
    private SpacecraftTravel _travel;
    private float _travelProgress;
    private bool _powered;
    private bool _dampeners = true;
    private bool _doorOpen = true;
    private bool _gearDeployed = true;
    private bool _parked;
    private readonly SpacecraftConsolePose[] _consolePoses = new SpacecraftConsolePose[2];
    private readonly SpacecraftConsolePose[] _consoleTargets = new SpacecraftConsolePose[2];
    private bool _grounded;
    private Vector3 _translation;
    private Vector3 _rotationInput;
    private bool _boostActive;
    private readonly List<BoxCollider> _walkSurfaces = new();
    private readonly List<BoxCollider> _solids = new();
    private readonly BoxCollider[] _landingPads = new BoxCollider[3];
    private readonly BoxCollider[] _chairs = new BoxCollider[3];
    private int _localAdjustmentSeat = -1;
    private long _nextRampAlignmentAt;
    private bool _disposed;
    private ServiceHold? _serviceHold;
    private bool _serviceHoldApplied;
    private Il2CppStructArray<RaycastHit>? _supportHits;
    private Il2CppStructArray<RaycastHit>? _rampHits;
    private Il2CppStructArray<RaycastHit>? _seatHits;
    private Il2CppReferenceArray<Collider>? _standingOverlaps;

    internal GameObject Root { get; private set; } = null!;
    internal bool Ready => !_disposed && Root && _body && _model != null && _model.Ready;
    internal bool ServiceSuspended { get; private set; }
    internal bool TelemetryCurrent => !ServiceSuspended && Ready && (_authoritative
        || _replicaAt > 0 && Environment.TickCount64 - _replicaAt <= 1000);
    internal float ReplicaAge => Math.Clamp((Environment.TickCount64 - _replicaAt) / 1000f, 0, .12f);
    internal bool DoorClosed => Ready && !State.DoorOpen && _model!.DoorAmount <= .01f;
    internal bool InnerDoorClosed => Ready && _model!.InnerDoorClosed;
    internal float InnerDoorAmount => Ready ? _model!.InnerDoorAmount : 0;
    internal bool CabinDoorClosed => Ready && _model!.CabinDoorClosed;
    internal float CabinDoorAmount => Ready ? _model!.CabinDoorAmount : 0;
    internal SpacecraftFlightState State => !Ready ? default : !_authoritative
        ? _replica with
        {
            Position = _serviceHold?.Position ?? _body.position,
            Rotation = _serviceHold?.Rotation ?? _body.rotation, Systems = _systems
        }
        : new(_serviceHold?.Position ?? _body.position, _serviceHold?.Rotation ?? _body.rotation,
            _docked ? _dockVelocity : _serviceHold?.Velocity ?? _body.velocity,
            _docked ? _dockAngularVelocity : _serviceHold?.AngularVelocity ?? _body.angularVelocity,
            _powered, _grounded, _dampeners, _region, _travel, _travelProgress,
            _doorOpen, _model!.DoorAmount,
            GearDeployed: _gearDeployed, GearAmount: _model.GearAmount,
            TranslationInput: _translation, RotationInput: _rotationInput, BoostActive: _boostActive, Systems: _systems,
            InnerDoorAmount: _model.InnerDoorAmount, CabinDoorAmount: _model.CabinDoorAmount, Docked: _docked);

    internal static bool PrepareAssets(out string status) => ClientSpacecraftModel.PrepareAssets(out status);

    internal void SetServiceSuspended(bool suspended)
    {
        if (_disposed) return;
        if (suspended)
        {
            ServiceSuspended = true;
            DisableNpcNavigation();
            if (_serviceHoldApplied) return;
            if (!Root || !_body) throw new InvalidOperationException("The spacecraft body is unavailable for suspension.");
            _serviceHold ??= new ServiceHold(_body, _body.position, _body.rotation, _body.velocity, _body.angularVelocity,
                _body.isKinematic, _body.collisionDetectionMode, _body.interpolation);
            ApplyServiceHold();
            return;
        }
        if (!ServiceSuspended) return;
        if (!Root || !_body) throw new InvalidOperationException("The spacecraft body is unavailable for resumption.");
        SynchronizeDamageReceivers();
        if (_serviceHold is { } hold)
        {
            if (!hold.Body || hold.Body.Pointer != _body.Pointer)
                throw new InvalidOperationException("The suspended spacecraft body is no longer owned.");
            _serviceHoldApplied = false;
            try
            {
                hold.Body.isKinematic = hold.Kinematic;
                hold.Body.collisionDetectionMode = hold.CollisionMode;
                hold.Body.interpolation = hold.Interpolation;
                if (!hold.Kinematic)
                {
                    hold.Body.velocity = hold.Velocity;
                    hold.Body.angularVelocity = hold.AngularVelocity;
                }
            }
            catch (Exception error)
            {
                try { ApplyServiceHold(); }
                catch (Exception holdError)
                {
                    throw new AggregateException("Spacecraft resumption and suspension restoration failed.", error, holdError);
                }
                throw;
            }
        }
        _serviceHold = null;
        ServiceSuspended = false;
    }

    private void ApplyServiceHold()
    {
        if (_serviceHold is not { } hold || !hold.Body || !_body || hold.Body.Pointer != _body.Pointer || !Root)
            throw new InvalidOperationException("The suspended spacecraft body is no longer owned.");
        hold.Body.interpolation = RigidbodyInterpolation.None;
        hold.Body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        hold.Body.isKinematic = true;
        hold.Body.position = hold.Position;
        hold.Body.rotation = hold.Rotation;
        // Replace any queued dock or replica movement from the previous fixed step.
        hold.Body.MovePosition(hold.Position);
        hold.Body.MoveRotation(hold.Rotation);
        Root.transform.SetPositionAndRotation(hold.Position, hold.Rotation);
        _serviceHoldApplied = true;
    }

    private readonly record struct ServiceHold(Rigidbody Body, Vector3 Position, Quaternion Rotation,
        Vector3 Velocity, Vector3 AngularVelocity, bool Kinematic,
        CollisionDetectionMode CollisionMode, RigidbodyInterpolation Interpolation);

    internal ClientSpacecraftBody(Vector3 position, Quaternion rotation, bool authoritative)
    {
        if (!Finite(position) || !Valid(rotation)) throw new ArgumentException("The spacecraft pose is invalid.");
        if (!PrepareAssets(out var status)) throw new InvalidOperationException(status);
        _authoritative = authoritative;
        try
        {
            // The game scene owns the root; unloading it also removes the ship.
            Root = new GameObject("Modstoke spacecraft");
            Root.transform.SetPositionAndRotation(position, rotation.normalized);
            _contact = new PhysicMaterial("Spacecraft contact")
            {
                dynamicFriction = .65f, staticFriction = .8f, bounciness = 0,
                frictionCombine = PhysicMaterialCombine.Maximum,
                bounceCombine = PhysicMaterialCombine.Minimum
            };
            _body = Root.AddComponent<Rigidbody>();
            _body.mass = Mass;
            _body.drag = 0;
            _body.angularDrag = 0;
            _body.isKinematic = !authoritative;
            _body.useGravity = false;
            _body.interpolation = RigidbodyInterpolation.Interpolate;
            _body.collisionDetectionMode = authoritative ? CollisionDetectionMode.ContinuousDynamic : CollisionDetectionMode.Discrete;
            _body.maxAngularVelocity = 1.2f;
            // The entry incline meets the native ramp heel; the airlock and
            // cabin share one level plane. Their side walls bound the
            // narrow corridor; the wider floor also covers the outer cabin grates.
            AddWalkSurface(new Vector3(-.02f, -1.26f, -8.95f), new Vector3(-.02f, -1.26f, 9.4f), 7.2f);
            AddWalkSurface(EntryFloorAft, EntryFloorForward, 3.9f);
            // Separate underside plates follow the native belly faces,
            // below the cabin and entry floor and clear of the boarding ramp.
            AddHull(new Vector3(0, -2.787f, -12.043f), new Vector3(4.09f, .12f, 4.55f));
            var bellyAft = new Vector3(0, -2.848f, -9.738f);
            var bellyForward = new Vector3(0, -2.455f, -6.373f);
            var bellyRotation = Quaternion.LookRotation(bellyForward - bellyAft, Vector3.up);
            AddHull((bellyAft + bellyForward) * .5f + bellyRotation * Vector3.up * .06f,
                new Vector3(4.09f, .12f, (bellyForward - bellyAft).magnitude + .06f), bellyRotation);
            AddHull(new Vector3(0, -2.395f, -2.758f), new Vector3(4.77f, .12f, 7.17f));
            // The gaps between the forward plates retain the vent and front gear well.
            AddHull(new Vector3(0, -2.39535f, 1.875f), new Vector3(1.5f, .12f, 2.13f));
            AddHull(new Vector3(0, -2.39535f, 5.12f), new Vector3(1.5f, .12f, 1.76f));
            AddHull(new Vector3(0, -2.39535f, 8.89f), new Vector3(1.5f, .12f, 1.32f));
            for (var side = -1; side <= 1; side += 2)
            {
                AddHull(new Vector3(side * 1.5675f, -2.39535f, 5.18f), new Vector3(1.635f, .12f, 8.74f));
                // Extrude the sloping lower side face inward from the native skin.
                var chamferRotation = Quaternion.LookRotation(new Vector3(-side * .00766f, .0012f, .99997f),
                    new Vector3(-side * .4662f, .88467f, -.00463f));
                AddHull(new Vector3(side * 2.856f, -2.162f, -2.755f),
                    new Vector3(.9f, .12f, 7.12f), chamferRotation);
            }
            // Separate thin panels enclose the cockpit without a convex canopy
            // or a solid hull volume occupying its seats and centre aisle.
            var visorAft = new Vector3(0, 2.12f, 4.02f);
            var visorForward = new Vector3(0, -.76f, 9.5f);
            var visorRotation = Quaternion.LookRotation(visorForward - visorAft, Vector3.up);
            AddHull((visorAft + visorForward) * .5f + visorRotation * Vector3.up * .1f,
                new Vector3(5.4f, .2f, (visorForward - visorAft).magnitude + .04f), visorRotation);
            AddHull(new Vector3(0, -.6f, 10.5f), new Vector3(4.7f, 2.1f, 2.4f));
            AddHull(new Vector3(0, 2.3f, -5.1f), new Vector3(7.25f, .3f, 18.4f));
            AddHull(new Vector3(-.09f, 1.25f, -6.89f), new Vector3(2.3f, .2f, 4.58f));
            for (var side = -1; side <= 1; side += 2)
            {
                var cockpitX = side < 0 ? -2.56f : 2.53f;
                AddWall(new Vector3(cockpitX, 0, 4.06f),
                    new Vector3(side * .65f, 0, 9.5f), -1.45f, 2.35f, .22f);
                // The lower walls join the individual native panels without
                // filling the shoulder between the cabin and the cockpit.
                AddHull(new Vector3(side < 0 ? -3.56f : 3.36f, -.02f, -1.52f),
                    new Vector3(.12f, 2.55f, 6.4f));
                AddHull(new Vector3(side < 0 ? -3.18f : 3.04f, 1.77f, -1.52f),
                    new Vector3(.75f, .95f, 6.4f));
                AddHull(new Vector3(side * 6.1f, -.5f, -3), new Vector3(4.8f, 1.35f, 17));
                AddHull(new Vector3(side * 5.2f, 1.4f, -13.6f), new Vector3(2.7f, 3.8f, 3.8f));
            }
            for (var i = 0; i < Feet.Length; i++)
                _landingPads[i] = AddHull(Feet[i] + Vector3.up * .16f,
                    new Vector3(i == 0 ? 1.401f : 1.736f, .32f, i == 0 ? 2.149f : 2.826f), Quaternion.identity);
            // Native airlock panels are near X +/-1, not the wider aft entry's
            // +/-2. Each solid side also covers its end jambs, leaving the 1.86m
            // central opening and both retracted door leaves unobstructed.
            for (var side = -1; side <= 1; side += 2)
            {
                AddHull(new Vector3(-.05f + side * 2.02f, -.7f, -11.5f), new Vector3(.18f, 3.2f, 5.7f));
                // Solid material outside the narrow airlock reaches the cabin
                // bulkhead, closing the spaces beside both sides of that door.
                AddHull(new Vector3(-.09f + side * 2.24f, .45f, -6.89f), new Vector3(2.62f, 3.8f, 4.58f));
            }
            var entryControl = SpacecraftLayout.CabinEntryControl;
            // Keep the backing 4 mm inside the visible face, as on the flight consoles.
            AddHull(entryControl.center + Vector3.right * .002f, entryControl.size - Vector3.right * .004f);
            var engineeringControl = SpacecraftLayout.EngineeringControl;
            AddHull(engineeringControl.center + Vector3.right * .002f, engineeringControl.size - Vector3.right * .004f);
            AddHull(SpacecraftLayout.EngineeringForwardSupport.center, SpacecraftLayout.EngineeringForwardSupport.size);
            AddHull(SpacecraftLayout.EngineeringAftSupport.center, SpacecraftLayout.EngineeringAftSupport.size);
            var navigationControl = SpacecraftLayout.NavigationControl;
            AddHull(navigationControl.center - Vector3.forward * .002f, navigationControl.size - Vector3.forward * .004f);
            AddHull(SpacecraftLayout.NavigationPortSupport.center, SpacecraftLayout.NavigationPortSupport.size);
            AddHull(SpacecraftLayout.NavigationStarboardSupport.center, SpacecraftLayout.NavigationStarboardSupport.size);
            var apuControl = SpacecraftLayout.ApuControl;
            AddHull(apuControl.center + Vector3.right * .002f, apuControl.size - Vector3.right * .004f);
            AddHull(SpacecraftLayout.ApuControlBacking.center, SpacecraftLayout.ApuControlBacking.size);
            AddHull(SpacecraftLayout.ApuServiceCover.center, SpacecraftLayout.ApuServiceCover.size);
            AddConsoleShell(false);
            AddConsoleShell(true);
            for (var seat = 0; seat < _chairs.Length; seat++)
                _chairs[seat] = AddHull(SpacecraftLayout.Seat(seat) + new Vector3(0, .12f, -.07f),
                    new Vector3(.80f, 1.55f, 1.06f), SpacecraftLayout.SeatRotation(seat));
            _body.centerOfMass = new Vector3(0, -.8f, -2.5f);
            _body.inertiaTensorRotation = Quaternion.identity;
            _body.inertiaTensor = new Vector3(720000, 1040000, 420000);
            _model = new ClientSpacecraftModel(Root.transform);
            _model.AddCabinCollision(_contact, _solids);
            for (var i = 0; i < _landingPads.Length; i++)
            {
                _landingPads[i].transform.SetParent(_model.GearFrame(i), true);
                // The native struts lean forward above the pads. Bound their
                // lower and upper sections separately so the mounts stay solid
                // without filling the full foot-to-mount bounding box.
                var lowerStrut = AddHull(i == 0 ? new Vector3(0, -2.8f, 7.063f)
                    : new Vector3(Feet[i].x, -2.25f, -7.98f),
                    i == 0 ? new Vector3(.94f, 1, 1.16f) : new Vector3(1.1f, 2.1f, 1.83f), Quaternion.identity);
                lowerStrut.transform.SetParent(_model.GearFrame(i), true);
                var upperStrut = AddHull(i == 0 ? new Vector3(0, -2.111f, 6.579f)
                    : new Vector3(Feet[i].x, -.734f, -7.022f),
                    i == 0 ? new Vector3(.75f, .378f, .58f) : new Vector3(.84f, .932f, 1.354f), Quaternion.identity);
                upperStrut.transform.SetParent(_model.GearFrame(i), true);
            }
            for (var seat = 0; seat < _chairs.Length; seat++)
            {
                _chairs[seat].transform.SetParent(_model.SeatFrame(seat), true);
                _model.SetSeatSitProgress(seat, 0);
            }
            // Two coplanar plates reach the entry floor without upright tread
            // faces. The outer section folds onto the main ramp before closing.
            _model.BoardingRamp.sharedMaterial = _contact;
            _walkSurfaces.Add(_model.BoardingRamp);
            _model.BoardingExtension.sharedMaterial = _contact;
            _walkSurfaces.Add(_model.BoardingExtension);
            _replica = new(position, rotation.normalized, Vector3.zero, Vector3.zero,
                false, false, true, SpacecraftRegion.Island, SpacecraftTravel.None, 0);
            if (authoritative)
            {
                AttachDamage();
                AttachNpcNavigation();
            }
        }
        catch { Dispose(); throw; }
    }

    private BoxCollider AddHull(Vector3 center, Vector3 size, Quaternion? rotation = null)
    {
        var owner = Root;
        if (rotation.HasValue)
        {
            owner = new GameObject("Spacecraft collision") { layer = Root.layer };
            owner.transform.SetParent(Root.transform, false);
            owner.transform.localPosition = center;
            owner.transform.localRotation = rotation.Value;
        }
        var collider = owner.AddComponent<BoxCollider>();
        collider.center = rotation.HasValue ? Vector3.zero : center;
        collider.size = size;
        collider.sharedMaterial = _contact;
        _solids.Add(collider);
        return collider;
    }

    private void AddConsoleShell(bool mirrored)
    {
        var slopeNormal = new Vector3(0, .764839f, -.644221f);
        var slopeUp = new Vector3(0, .644221f, .764839f);
        // The main skin has recessed monitors; its full rectangular bounds would fill their lenses.
        void Slope(float left, float right, float bottom, float top)
        {
            var center = new Vector3((left + right) * .5f, -.490655f, 5.150840f)
                + slopeUp * ((bottom + top) * .5f);
            AddConsolePlate(center, slopeNormal, slopeUp, right - left, top - bottom, mirrored);
        }
        Slope(.280393f, .356590f, -.252947f, .252750f);
        Slope(.356590f, .868930f, -.252947f, -.107310f);
        Slope(.604990f, .620530f, -.107310f, .252750f);
        Slope(.868930f, 1.209910f, -.252947f, .252750f);
        Slope(1.209910f, 1.258200f, -.252947f, -.064100f);
        Slope(1.209910f, 1.460000f, .092370f, .252750f);
        Slope(1.460000f, 1.530000f, .180000f, .252750f);
        AddConsolePlate(new Vector3(1.335763f, -.479986f, 5.160336f), slopeNormal, slopeUp,
            .196220f, .144197f, mirrored);
        var monitorNormal = new Vector3(0, .694097f, -.719882f);
        var monitorUp = new Vector3(0, .719882f, .694097f);
        AddConsolePlate(new Vector3(.481214f, -.445505f, 5.263464f), monitorNormal, monitorUp,
            .227729f, .324979f, mirrored);
        AddConsolePlate(new Vector3(.744313f, -.445505f, 5.263464f), monitorNormal, monitorUp,
            .227729f, .324979f, mirrored);
        AddConsolePlate(new Vector3(1.091686f, -.769f, 5.352680f), Vector3.down, Vector3.forward,
            1.622585f, .789820f, mirrored);
        AddConsolePlate(new Vector3(1.091686f, -.313443f, 5.555245f), Vector3.up, Vector3.forward,
            1.622585f, .384691f, mirrored);
        AddConsolePlate(new Vector3(1.006645f, -.999235f, 5.506761f), Vector3.back, Vector3.up,
            1.441249f, .440470f, mirrored);
        AddConsoleSide(new Vector3(.278715f, 0, 4.957770f), new Vector3(.278715f, 0, 5.747610f),
            Vector3.left, -.767322f, -.328067f, mirrored);
        AddConsoleSide(new Vector3(1.260163f, 0, 4.957364f), new Vector3(1.905445f, 0, 5.728523f),
            new Vector3(.766923f, 0, -.641739f), -.767342f, -.327952f, mirrored);
    }

    private void AddConsoleSide(Vector3 front, Vector3 rear, Vector3 normal, float bottom, float top, bool mirrored)
    {
        // Short inward steps follow the side's tapered upper edge without filling the space above the panel.
        const int sections = 8;
        var direction = rear - front;
        for (var index = 0; index < sections; index++)
        {
            var start = front + direction * (index / (float)sections);
            var end = front + direction * ((index + 1) / (float)sections);
            var upper = Mathf.Min(top, -.490655f + (start.z - 5.150840f) * (.644221f / .764839f));
            var center = (start + end) * .5f;
            center.y = (bottom + upper) * .5f;
            AddConsolePlate(center, normal, Vector3.up, direction.magnitude / sections, upper - bottom, mirrored);
        }
    }

    private void AddConsolePlate(Vector3 surface, Vector3 normal, Vector3 up, float width, float height, bool mirrored)
    {
        if (mirrored)
        {
            surface.x = -.208021671f - surface.x;
            normal.x = -normal.x;
            up.x = -up.x;
        }
        normal.Normalize();
        // Keep the outward collision face 4 mm inside the rendered shell and all thickness inward.
        const float thickness = .035f;
        AddHull(surface - normal * (.004f + thickness * .5f), new Vector3(width, height, thickness),
            Quaternion.LookRotation(normal, up));
    }

    private void AddWalkSurface(Vector3 aft, Vector3 forward, float width)
    {
        var direction = forward - aft;
        var rotation = Quaternion.LookRotation(direction, Vector3.up);
        var surface = AddHull((aft + forward) * .5f - rotation * Vector3.up * .08f,
            new Vector3(width, .16f, direction.magnitude + .025f), rotation);
        _walkSurfaces.Add(surface);
        _solids.Remove(surface);
    }

    private void AddWall(Vector3 aft, Vector3 forward, float bottom, float top, float thickness)
    {
        var direction = forward - aft;
        var center = (aft + forward) * .5f;
        center.y = (bottom + top) * .5f;
        AddHull(center, new Vector3(thickness, top - bottom, direction.magnitude + .04f),
            Quaternion.LookRotation(direction, Vector3.up));
    }

    // Accelerations use Unity world axes in m/s^2; hover excludes velocity-dependent frame forces.
    internal void FixedUpdate(float dt, SpacecraftControls controls, Vector3 acceleration, Vector3 hoverAcceleration,
        Vector3 frameVelocity = default, Vector3 angularReference = default)
    {
        if (ServiceSuspended || !Ready) return;
        dt = float.IsFinite(dt) ? Math.Clamp(dt, .001f, .1f) : .02f;
        if (!_authoritative) { InterpolateReplica(dt); return; }
        if (!Finite(acceleration) || acceleration.sqrMagnitude > 100000000f)
            throw new ArgumentException("Acceleration must be finite and at most 10000 m/s^2.", nameof(acceleration));
        if (!Finite(hoverAcceleration) || hoverAcceleration.sqrMagnitude > 100000000f)
            throw new ArgumentException("Hover acceleration must be finite and at most 10000 m/s^2.", nameof(hoverAcceleration));
        if (!WithinMagnitude(frameVelocity, 2 * OrbitalSimulation.MaximumSpeed))
            throw new ArgumentException("Frame velocity must be finite and within twice the orbital speed limit.", nameof(frameVelocity));
        if (!WithinMagnitude(angularReference, 20))
            throw new ArgumentException("The attitude reference must be finite and within 20 rad/s.", nameof(angularReference));
        ApplyPendingDamage();
        if (ServiceSuspended || !Ready) return;
        _doorOpen = controls.DoorOpen;
        if (_docked) SetGear(controls.GearDeployed);
        if (_docked || _travel != SpacecraftTravel.None)
        {
            DisableNpcNavigation();
            _grounded = false;
            _translation = _rotationInput = Vector3.zero;
            _boostActive = false;
            return;
        }
        bool thrustAvailable = _powered && ClientSpacecraftSystems.PropulsionAvailable(_systems);
        _translation = thrustAvailable ? Input(controls.Translation) : Vector3.zero;
        _rotationInput = thrustAvailable ? Input(controls.Rotation) : Vector3.zero;
        _boostActive = thrustAvailable && controls.Boost;
        _body.useGravity = false;
        _grounded = Supported();
        SetGear(controls.GearDeployed);
        UpdateParking(controls);
        UpdateNpcNavigation();
        if (!_body.isKinematic) _body.AddForce(acceleration, ForceMode.Acceleration);
        if (!thrustAvailable) return;
        var engine = Fraction(_systems.EngineIntegrity01);
        if (_dampeners) _body.AddForce(hoverAcceleration * engine, ForceMode.Acceleration);
        var boost = _boostActive ? 1.8f : 1;
        var thrust = Vector3.Scale(_translation, new Vector3(7, 8, 12)) * boost;
        // Frame velocity is the reference frame's velocity minus the inertial cruise target, in native world axes.
        var controlVelocity = _body.velocity + frameVelocity;
        var localVelocity = Root.transform.InverseTransformDirection(controlVelocity);
        if (_dampeners)
        {
            thrust.x -= localVelocity.x * (MathF.Abs(_translation.x) < .02f ? 1.4f : .18f);
            thrust.y -= localVelocity.y * (MathF.Abs(_translation.y) < .02f ? 1.6f : .18f);
            thrust.z -= localVelocity.z * (MathF.Abs(_translation.z) < .02f ? 1.2f : .15f);
        }
        _body.AddRelativeForce(thrust * engine, ForceMode.Acceleration);
        var angular = Root.transform.InverseTransformDirection(_body.angularVelocity - (_dampeners ? angularReference : Vector3.zero));
        var requestedRate = Vector3.Scale(_rotationInput, new Vector3(.42f, .48f, .58f));
        var torque = _dampeners ? (requestedRate - angular) * 3.5f : requestedRate * 1.4f - angular * .15f;
        _body.AddRelativeTorque(torque * engine, ForceMode.Acceleration);
        var speed = controlVelocity.magnitude;
        var speedLimit = controls.Boost ? 95 : 65;
        if (speed > speedLimit && (_region != SpacecraftRegion.Space || _dampeners))
            _body.AddForce(-controlVelocity / speed * ((speed - speedLimit) * 2 * engine), ForceMode.Acceleration);
    }

    private bool Supported()
    {
        // Ground contact and safe boarding speed are separate conditions. The
        // service applies its movement limits even while the pads are supported.
        if (_region == SpacecraftRegion.Space || Root.transform.up.y < .94f || _model!.GearAmount < .99f) return false;
        var supportedFeet = 0;
        for (var foot = 0; foot < Feet.Length; foot++)
        {
            // Uneven terrain may support two pads while the third is above it.
            // Sample the actual pad footprint, not only its centre, but retain
            // the short contact tolerance rather than treating hovering as landed.
            var halfX = _landingPads[foot].size.x * .5f - .08f;
            var halfZ = _landingPads[foot].size.z * .5f - .08f;
            for (var sample = 0; sample < 5; sample++)
            {
                var offset = sample switch
                {
                    1 => new Vector3(-halfX, 0, -halfZ),
                    2 => new Vector3(halfX, 0, -halfZ),
                    3 => new Vector3(-halfX, 0, halfZ),
                    4 => new Vector3(halfX, 0, halfZ),
                    _ => Vector3.zero
                };
                if (!HasStaticFootContact(_model.GearFrame(foot).TransformPoint(Feet[foot] + offset))) continue;
                supportedFeet++;
                break;
            }
            if (supportedFeet >= 2) return true;
        }
        return false;
    }

    private bool HasStaticFootContact(Vector3 foot, float clearance = 0)
    {
        var origin = foot + Vector3.up * .25f;
        var hits = _supportHits ??= new Il2CppStructArray<RaycastHit>(size: 32);
        var reach = .32f + clearance;
        var hitCount = Physics.RaycastNonAlloc(origin, Vector3.down, hits, reach, ~0, QueryTriggerInteraction.Ignore);
        if (hitCount == hits.Length)
        {
            // A full buffer can omit a nearer obstruction.
            hits = Physics.RaycastAll(origin, Vector3.down, reach, ~0, QueryTriggerInteraction.Ignore);
            hitCount = hits.Length;
        }
        var nearest = float.PositiveInfinity;
        RaycastHit support = default;
        for (var h = 0; h < hitCount; h++)
        {
            var hit = hits[h];
            if (!hit.collider || hit.collider.transform.IsChildOf(Root.transform) || hit.distance >= nearest) continue;
            nearest = hit.distance;
            support = hit;
        }
        // A nearer unsuitable object blocks support; do not see through it.
        // Also reject ground appreciably above the pad, not just far below it.
        return nearest >= .18f && nearest <= reach && support.normal.y >= .8f && StaticSupport(support.collider);
    }

    private static bool StaticSupport(Collider collider)
    {
        if (collider.attachedRigidbody != null) return false;
        var terrain = collider.GetComponent("TerrainCollider");
        if (terrain && terrain.Pointer == collider.Pointer) return true;
        var mesh = collider.GetComponent<MeshCollider>();
        return mesh && mesh.Pointer == collider.Pointer && collider.gameObject.isStatic;
    }

    private void InterpolateReplica(float dt)
    {
        if (ServiceSuspended || _replicaAt == 0 || _docked) return;
        var age = ReplicaAge;
        var target = _replica.Position + _replica.Velocity * age;
        var rotation = _replica.Rotation;
        var spin = _replica.AngularVelocity.magnitude;
        if (spin > .001f)
            rotation = Quaternion.AngleAxis(spin * age * Mathf.Rad2Deg, _replica.AngularVelocity / spin) * rotation;
        var blend = 1 - Mathf.Exp(-16 * dt);
        _body.MovePosition(Vector3.Lerp(_body.position, target, blend));
        _body.MoveRotation(Quaternion.Slerp(_body.rotation, rotation, blend));
    }

    internal void SetReplica(SpacecraftFlightState state, bool snap = false)
    {
        if (ServiceSuspended || _authoritative || !Ready || !Finite(state.Position) || !Valid(state.Rotation)
            || !Finite(state.Velocity) || !Finite(state.AngularVelocity)
            || !Enum.IsDefined(state.Region) || !Enum.IsDefined(state.Travel)
            || state.Docked && (state.Region != SpacecraftRegion.Space || state.Travel != SpacecraftTravel.None)) return;
        var active = state.Powered && !state.Docked && state.Travel == SpacecraftTravel.None;
        state = state with
        {
            Powered = state.Powered && !state.Docked, Grounded = state.Grounded && !state.Docked,
            Rotation = state.Rotation.normalized, TravelProgress = Fraction(state.TravelProgress),
            DoorAmount = Fraction(state.DoorAmount), GearAmount = Fraction(state.GearAmount),
            InnerDoorAmount = Fraction(state.InnerDoorAmount), CabinDoorAmount = Fraction(state.CabinDoorAmount),
            TranslationInput = active ? Input(state.TranslationInput) : Vector3.zero,
            RotationInput = active ? Input(state.RotationInput) : Vector3.zero,
            BoostActive = active && state.BoostActive
        };
        if (_replicaAt == 0)
        {
            _model!.SetDoorAmount(state.DoorAmount);
            _model.SetGearAmount(state.GearAmount);
            _model.UpdateAirlock(state.InnerDoorAmount, 2);
            _model.UpdateCabinDoor(state.CabinDoorAmount, 2);
        }
        if (snap || _replicaAt == 0 || _replica.Region != state.Region || (_body.position - state.Position).sqrMagnitude > 10000)
        {
            var interpolation = _body.interpolation;
            _body.interpolation = RigidbodyInterpolation.None;
            try
            {
                _body.position = state.Position;
                _body.rotation = state.Rotation;
                Root.transform.SetPositionAndRotation(state.Position, state.Rotation);
            }
            finally
            {
                _body.interpolation = interpolation;
            }
        }
        _docked = state.Docked;
        _body.isKinematic = true;
        _body.collisionDetectionMode = _docked ? CollisionDetectionMode.ContinuousSpeculative : CollisionDetectionMode.Discrete;
        _replica = state;
        _systems = state.Systems;
        _replicaAt = Environment.TickCount64;
    }

    internal void Rebase(Vector3 position, Vector3 velocityShift = default)
    {
        if (!Finite(position)) throw new ArgumentException("The spacecraft position is invalid.", nameof(position));
        if (!WithinMagnitude(velocityShift, 2 * OrbitalSimulation.MaximumSpeed))
            throw new ArgumentException("Velocity shift must be finite and within twice the orbital speed limit.", nameof(velocityShift));
        if (ServiceSuspended || !Ready || !_authoritative || _region != SpacecraftRegion.Space || _travel != SpacecraftTravel.None
            || _body.isKinematic && !_docked)
            throw new InvalidOperationException("The spacecraft is not ready for a space-frame rebase.");
        DisableNpcNavigation();
        var interpolation = _body.interpolation;
        _body.interpolation = RigidbodyInterpolation.None;
        try
        {
            _body.position = position;
            Root.transform.position = position;
            if (_docked) _dockVelocity -= velocityShift;
            else _body.velocity -= velocityShift;
        }
        finally
        {
            _body.interpolation = interpolation;
        }
    }

    internal void Transition(SpacecraftRegion region, Vector3 position, Quaternion rotation)
    {
        if (ServiceSuspended || !Ready || !Finite(position) || !Valid(rotation) || !Enum.IsDefined(region)) return;
        DisableNpcNavigation();
        _region = region;
        ReleaseParking();
        if (_docked)
        {
            _docked = false;
            _dockVelocity = _dockAngularVelocity = Vector3.zero;
            if (_authoritative && _travel == SpacecraftTravel.None)
            {
                _body.isKinematic = false;
                _body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            }
            else _body.collisionDetectionMode = CollisionDetectionMode.Discrete;
        }
        _grounded = false;
        _translation = _rotationInput = Vector3.zero;
        _boostActive = false;
        if (!_body.isKinematic) { _body.velocity = Vector3.zero; _body.angularVelocity = Vector3.zero; }
        _body.position = position;
        var normalizedRotation = rotation.normalized;
        _body.rotation = normalizedRotation;
        Root.transform.SetPositionAndRotation(position, normalizedRotation);
        _body.useGravity = false;
        _replica = _replica with
        {
            Position = position, Rotation = normalizedRotation, Velocity = Vector3.zero,
            AngularVelocity = Vector3.zero, Grounded = false, Region = region, Docked = false
        };
        if (!_authoritative) _replicaAt = Environment.TickCount64;
    }

    internal void RestoreNavigation(SpacecraftRegion region, Vector3 position, Quaternion rotation,
        Vector3 velocity, Vector3 angularVelocity, bool powered, bool dampeners)
    {
        if (ServiceSuspended || !Ready || !_authoritative)
            throw new InvalidOperationException("The spacecraft is not ready for navigation restoration.");
        if (!Enum.IsDefined(region)) throw new ArgumentOutOfRangeException(nameof(region));
        if (!Finite(position) || !Valid(rotation)) throw new ArgumentException("The spacecraft pose is invalid.");
        var maximumSpeed = region == SpacecraftRegion.Space ? 2 * OrbitalSimulation.MaximumSpeed : 1000;
        if (!WithinMagnitude(velocity, maximumSpeed))
            throw new ArgumentException($"Velocity must be finite and at most {maximumSpeed} m/s.", nameof(velocity));
        if (!Finite(angularVelocity) || angularVelocity.sqrMagnitude > 400f)
            throw new ArgumentException("Angular velocity must be finite and at most 20 rad/s.", nameof(angularVelocity));
        Transition(region, position, rotation);
        SetTravel(SpacecraftTravel.None, 0);
        _body.velocity = velocity;
        _body.angularVelocity = angularVelocity;
        SetPower(powered, dampeners);
    }

    internal void SetTravel(SpacecraftTravel travel, float progress)
    {
        if (ServiceSuspended || !Ready || !_authoritative || !Enum.IsDefined(travel) || _docked) return;
        _travel = travel;
        ReleaseParking();
        _travelProgress = Fraction(progress);
        if (travel != SpacecraftTravel.None)
        {
            _translation = _rotationInput = Vector3.zero;
            _boostActive = false;
            if (!_body.isKinematic) { _body.velocity = Vector3.zero; _body.angularVelocity = Vector3.zero; }
            _body.collisionDetectionMode = CollisionDetectionMode.Discrete;
            _body.isKinematic = true;
            _body.useGravity = false;
            _powered = true;
            _grounded = false;
        }
        else
        {
            _body.isKinematic = false;
            _body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            _body.useGravity = false;
        }
    }

    internal void SetConsolePoses(SpacecraftConsolePose pilot, SpacecraftConsolePose copilot)
    {
        SetConsolePose(0, pilot);
        SetConsolePose(1, copilot);
    }

    private void SetConsolePose(int seat, SpacecraftConsolePose pose)
    {
        if (!TelemetryCurrent || !pose.Active || !Enum.IsDefined(pose.Control) || !float.IsFinite(pose.Reach))
            pose = default;
        else pose = pose with { Reach = Mathf.Clamp01(pose.Reach) };
        var current = _consolePoses[seat];
        if (current.Actor != pose.Actor || current.Control != pose.Control)
            _consolePoses[seat] = pose with { Reach = 0 };
        _consoleTargets[seat] = pose;
    }

    internal SpacecraftConsolePose ConsolePose(int seat) => TelemetryCurrent && seat is >= 0 and <= 1
        ? _consolePoses[seat] : default;

    internal SpacecraftConsolePose ConsolePose(int seat, ulong actor)
    {
        var pose = ConsolePose(seat);
        return pose.Actor == actor ? pose : default;
    }

    internal void UpdateVisuals(float dt)
    {
        if (ServiceSuspended || !Ready) return;
        var elapsed = float.IsFinite(dt) ? Math.Clamp(dt, 0, .1f) : 0;
        if (!TelemetryCurrent) SetConsolePoses(default, default);
        for (var seat = 0; seat < _consolePoses.Length; seat++)
            _consolePoses[seat] = _consoleTargets[seat] with
            { Reach = Mathf.MoveTowards(_consolePoses[seat].Reach, _consoleTargets[seat].Reach, elapsed * 5) };
        var translation = _authoritative ? _translation : _replica.TranslationInput;
        _model!.SetConsolePoses(_consolePoses[0], _consolePoses[1]);
        _model.Update(State, translation, elapsed, _authoritative);
        float doorStep = _authoritative ? elapsed * ClientSpacecraftModel.DoorDrive(_systems) : elapsed * 2.5f;
        // A sealed leaf rechecks its opening request after power or pressure changes.
        // Once unsealed, obstruction recovery must remain free to reopen the passage.
        var innerStep = _authoritative && _systems.InnerDoorOpen && _model.InnerDoorClosed
            && !ClientSpacecraftSystems.CanOpenInnerDoor(_systems, _model.CabinDoorClosed, out _) ? 0 : doorStep;
        var cabinStep = _authoritative && _systems.CabinDoorOpen && _model.CabinDoorClosed
            && !ClientSpacecraftSystems.CanOpenCabinDoor(_systems, _model.InnerDoorClosed, out _) ? 0 : doorStep;
        _model.UpdateAirlock(_authoritative ? _systems.InnerDoorOpen ? 1 : 0 : _replica.InnerDoorAmount, innerStep);
        _model.UpdateCabinDoor(_authoritative ? _systems.CabinDoorOpen ? 1 : 0 : _replica.CabinDoorAmount, cabinStep);
        AlignBoardingRamp();
    }

    private void AlignBoardingRamp()
    {
        var state = State;
        if (!state.Grounded && !state.Docked || state.Travel != SpacecraftTravel.None || _model!.DoorAmount < .999f
            || !state.Docked && (Root.transform.up.y < .94f || state.Velocity.sqrMagnitude > .64f
                || state.AngularVelocity.sqrMagnitude > .0225f)) return;
        var now = Environment.TickCount64;
        if (now < _nextRampAlignmentAt) return;
        _nextRampAlignmentAt = now + 250;
        var origin = Root.transform.TransformPoint(new Vector3(-.05f, -1.76f, -18.5f));
        var up = state.Docked ? Root.transform.up : Vector3.up;
        var nearest = float.PositiveInfinity;
        RaycastHit support = default;
        var hits = _rampHits ??= new Il2CppStructArray<RaycastHit>(size: 32);
        var hitCount = Physics.RaycastNonAlloc(origin, -up, hits, 3.8f, ~0, QueryTriggerInteraction.Ignore);
        if (hitCount == hits.Length)
        {
            hits = Physics.RaycastAll(origin, -up, 3.8f, ~0, QueryTriggerInteraction.Ignore);
            hitCount = hits.Length;
        }
        for (var h = 0; h < hitCount; h++)
        {
            var hit = hits[h];
            if (!hit.collider || hit.collider.transform.IsChildOf(Root.transform) || hit.distance >= nearest) continue;
            nearest = hit.distance;
            support = hit;
        }
        // A latched craft shares the station's motion; otherwise only static
        // terrain can support the ramp. The nearest obstruction still wins.
        if (!float.IsFinite(nearest) || Vector3.Dot(support.normal, up) < .8f) return;
        var supportBody = support.collider.attachedRigidbody;
        if (!(state.Docked && supportBody && supportBody.isKinematic) && !StaticSupport(support.collider)) return;
        var toeHeight = Root.transform.InverseTransformPoint(support.point + Root.transform.up * .06f).y;
        // Keep the entry incline below 32 degrees; an unsupported drop is not
        // converted into a walkway, and no player pose is changed here.
        if (toeHeight < -5f || toeHeight > -2.75f) return;
        _model.SetBoardingRampToe(toeHeight);
    }

    internal void SetDoorOpen(bool open) { if (!ServiceSuspended && Ready && _authoritative) _doorOpen = open; }

    internal void SetSystemsState(SpacecraftSystemState state)
    {
        if (!ServiceSuspended && Ready) _systems = state;
    }

    internal void RestoreActuators(bool rearOpen, float rearAmount, float innerAmount,
        float cabinAmount, bool gearDeployed, float gearAmount)
    {
        if (ServiceSuspended || !Ready || !_authoritative)
            throw new InvalidOperationException("The spacecraft is not ready for actuator restoration.");
        _doorOpen = rearOpen;
        _gearDeployed = gearDeployed;
        _model!.SetDoorAmount(rearAmount);
        _model.UpdateAirlock(innerAmount, 2);
        _model.UpdateCabinDoor(cabinAmount, 2);
        _model.SetGearAmount(gearAmount);
    }

    internal void SetPower(bool powered, bool dampeners)
    {
        if (ServiceSuspended || !Ready || !_authoritative) return;
        _powered = powered && !_docked;
        _dampeners = dampeners;
        if (!_powered)
        {
            _translation = _rotationInput = Vector3.zero;
            _boostActive = false;
        }
    }

    internal bool SetGear(bool deployed)
    {
        if (ServiceSuspended || !Ready || !_authoritative || _docked && !deployed) return false;
        if (_gearDeployed == deployed && (deployed || _model!.GearAmount <= .001f)) return true;
        if (!deployed)
        {
            if (_grounded) { _gearDeployed = true; return false; }
            for (var i = 0; i < _landingPads.Length; i++)
            {
                var pad = _landingPads[i];
                // The front foot's rigid fold sweeps below its deployed pad.
                // Require clearance for that arc as well as unloaded feet.
                var half = pad.size * .5f;
                for (var sample = 0; sample < 5; sample++)
                {
                    var offset = sample switch
                    {
                        1 => new Vector3(-half.x, -half.y, -half.z),
                        2 => new Vector3(half.x, -half.y, -half.z),
                        3 => new Vector3(-half.x, -half.y, half.z),
                        4 => new Vector3(half.x, -half.y, half.z),
                        _ => new Vector3(0, -half.y, 0)
                    };
                    if (HasStaticFootContact(pad.transform.TransformPoint(pad.center + offset), .75f))
                    {
                        _gearDeployed = true;
                        return false;
                    }
                }
            }
        }
        _gearDeployed = deployed;
        return true;
    }

    private void UpdateParking(SpacecraftControls controls)
    {
        var flying = _powered && (Input(controls.Translation).sqrMagnitude > .0004f
            || Input(controls.Rotation).sqrMagnitude > .0004f || !_dampeners);
        if (!_grounded || flying || _travel != SpacecraftTravel.None)
        {
            ReleaseParking();
            return;
        }
        if (_parked || _body.velocity.sqrMagnitude > .16f || _body.angularVelocity.sqrMagnitude > .0025f) return;
        // Ground-supported parking prevents walking capsules from moving a
        // resting hull. Flight input or loss of support releases it immediately.
        _body.velocity = Vector3.zero;
        _body.angularVelocity = Vector3.zero;
        _body.constraints = RigidbodyConstraints.FreezeAll;
        _parked = true;
    }

    private void ReleaseParking()
    {
        DisableNpcNavigation();
        if (!_parked) return;
        _body.constraints = RigidbodyConstraints.None;
        _parked = false;
    }

    internal void SetSeatSitProgress(int seat, float progress, bool localRider = false)
    {
        if (!Ready || seat < 0 || seat >= _chairs.Length) return;
        if (localRider) _localAdjustmentSeat = seat;
        else if (_localAdjustmentSeat == seat) return;
        _model!.SetSeatSitProgress(seat, Fraction(progress));
    }

    internal void ResetSeatAdjustment(int seat, bool localRider = false)
    {
        if (localRider && _localAdjustmentSeat != seat) return;
        SetSeatSitProgress(seat, 0, localRider);
        if (localRider) _localAdjustmentSeat = -1;
    }

    // Ship-local seat displacement includes rotation around the chair's pivot.
    internal Vector3 SeatAdjustmentOffset(int seat) => Ready && seat >= 0 && seat < _chairs.Length
        ? Root.transform.InverseTransformPoint(_model!.SeatFrame(seat).TransformPoint(SpacecraftLayout.Seat(seat)))
            - SpacecraftLayout.Seat(seat) : Vector3.zero;

    internal AircraftCrewTargets SeatRestTargets(int seat) => SpacecraftLayout.Crew(_model!.SeatFrame(seat), seat);

    internal Transform FlightControlGrip => _model!.FlightControlGrip;
    internal Transform? SeatConsoleContact(int seat, SpacecraftConsoleControl control)
        => _model!.SeatConsoleContact(seat, control);
    internal Transform FlightControlPick => _model!.FlightControlPick;
    internal Transform FlightControlDrag => _model!.FlightControlDrag;
    internal static Vector2 FlightControlHalfSize => ClientSpacecraftModel.FlightControlHalfSize;

    internal bool TryCrewTargets(int seat, ulong actor, out AircraftCrewTargets targets)
    {
        targets = default;
        if (!Ready || seat < 0 || seat > 2) return false;
        var pose = ConsolePose(seat, actor);
        targets = ClientSpacecraftRider.CrewTargets(this, seat, pose.Control, pose.Reach);
        return true;
    }

    internal bool TryWalkSurface(Vector3 worldPosition, out Vector3 surface)
    {
        surface = default;
        if (!Ready || !Finite(worldPosition)) return false;
        // Query only the finite authored walking surfaces. Ground searches never
        // recurse through the ship's own Rigidbody or accept seats as its floor.
        var up = Root.transform.up;
        var ray = new Ray(worldPosition + up * 2.6f, -up);
        var nearest = float.PositiveInfinity;
        foreach (var floor in _walkSurfaces)
        {
            if ((floor == _model!.BoardingRamp || floor == _model.BoardingExtension) && _model.DoorAmount < .99f) continue;
            if (!floor || !floor.Raycast(ray, out var hit, 6) || Vector3.Dot(hit.normal, up) < .7f || hit.distance >= nearest) continue;
            nearest = hit.distance;
            surface = hit.point;
        }
        return float.IsFinite(nearest);
    }

    internal bool ContainsCabin(Vector3 worldPosition)
    {
        if (!TryWalkSurface(worldPosition, out var surface)) return false;
        var up = Root.transform.up;
        var height = Vector3.Dot(worldPosition - surface, up);
        return height >= -.35f && height <= 2.3f
            && IsInsideCabin(worldPosition + up * CrewCapsuleBottom)
            && IsInsideCabin(worldPosition + up * CrewCapsuleTop);
    }

    internal bool IsInsideCabin(Vector3 worldPoint)
    {
        if (!ContainsCabinAir(worldPoint)) return false;
        foreach (var solid in _solids)
        {
            if (!solid || !solid.enabled) continue;
            var local = solid.transform.InverseTransformPoint(worldPoint) - solid.center;
            var half = solid.size * .5f;
            if (MathF.Abs(local.x) < half.x && MathF.Abs(local.y) < half.y && MathF.Abs(local.z) < half.z) return false;
        }
        return true;
    }

    // Furniture blocks movement, not the atmosphere of the surrounding compartment.
    internal bool ContainsCabinAir(Vector3 worldPoint) => Ready && Finite(worldPoint)
        && InsideHull(Root.transform.InverseTransformPoint(worldPoint));

    private static bool InsideHull(Vector3 point)
    {
        if (point.z < EntryFloorAft.z || point.z > 9.4f) return false;
        var floor = point.z < EntryFloorForward.z
            ? Mathf.Lerp(EntryFloorAft.y, EntryFloorForward.y,
                Mathf.InverseLerp(EntryFloorAft.z, EntryFloorForward.z, point.z)) : EntryFloorForward.y;
        var ceiling = point.z < -4.7f ? 1.15f : point.z > 4.02f
            ? Mathf.Lerp(2.12f, -.76f, (point.z - 4.02f) / 5.48f) : 2.12f;
        var minX = point.z < -8.95f ? -1.98f : point.z < -4.7f ? -1.05f : point.z < 1.65f ? -3.47f : -2.49f;
        var maxX = point.z < -8.95f ? 1.9f : point.z < -4.7f ? .91f : point.z < 1.65f ? 3.26f : 2.47f;
        if (point.z > 4.06f)
        {
            var taper = (point.z - 4.06f) / 5.44f;
            minX = Mathf.Lerp(-2.49f, -.54f, taper);
            maxX = Mathf.Lerp(2.47f, .54f, taper);
        }
        return point.y >= floor - .05f && point.y <= ceiling && point.x >= minX && point.x <= maxX;
    }

    internal bool IsInAirlock(Vector3 worldPoint)
    {
        if (!ContainsCabinAir(worldPoint)) return false;
        return Root.transform.InverseTransformPoint(worldPoint).z is >= -9.1f and < -4.84f;
    }

    internal bool IsInEntryVestibule(Vector3 worldPoint) => ContainsCabinAir(worldPoint)
        && Root.transform.InverseTransformPoint(worldPoint).z < -9.1f;

    internal bool AtInnerDoor(Vector3 worldPoint)
    {
        if (!Ready || !Finite(worldPoint)) return false;
        var point = Root.transform.InverseTransformPoint(worldPoint);
        // Include a standing capsule on either side of the sliding leaves.
        return point.x is >= -1.45f and <= 1.3f && point.y is >= -1.65f and <= 1.7f
            && point.z is >= -9.6f and <= -8.4f;
    }

    internal bool AtOuterDoor(Vector3 worldPoint)
    {
        if (!Ready || !Finite(worldPoint)) return false;
        var point = Root.transform.InverseTransformPoint(worldPoint);
        // Include the capsule and the ramp's full ground-fitted boarding route.
        return point.x is >= -1.5f and <= 1.4f && point.y is >= -5.35f and <= .9f
            && point.z is >= -19f and <= -13.5f;
    }

    internal bool CanReachSeat(Vector3 actorPosition, int seat, Transform? actor = null)
        => CanReachSeat(actorPosition, seat, out _, actor);

    internal bool CanReachSeat(Vector3 actorPosition, int seat, out string reason, Transform? actor = null)
    {
        reason = "The chair is unavailable.";
        if (!Ready || seat < 0 || seat >= _chairs.Length) return false;
        reason = "Enter the cabin to reach the chair.";
        if (!ContainsCabin(actorPosition)) return false;
        var local = Root.transform.InverseTransformPoint(actorPosition);
        var offset = local - SpacecraftLayout.Seat(seat) - SeatAdjustmentOffset(seat);
        reason = "Move closer to the chair.";
        if (offset.x * offset.x + offset.z * offset.z > 3.24f || MathF.Abs(offset.y) > 1.65f) return false;
        var origin = actorPosition + Root.transform.up * 1.1f;
        var target = Root.transform.TransformPoint(SpacecraftLayout.Seat(seat) + SeatAdjustmentOffset(seat) + Vector3.up * .3f);
        var seated = SpacecraftLayout.Seat(seat) + SeatAdjustmentOffset(seat) + Vector3.down * .8f;
        reason = "The path to the chair is blocked.";
        if (!CrewSeatPathClear(local, seated, seat)) return false;
        reason = "The chair is obstructed.";
        if (!SeatLineClear(origin, target, seat, actor)) return false;
        reason = "";
        return true;
    }

    internal bool TryFindSeat(Ray gaze, Vector3 actorPosition, out int seat, Transform? actor = null)
    {
        seat = -1;
        if (!Ready || !ContainsCabin(actorPosition) || !Finite(gaze.origin)
            || !Finite(gaze.direction) || gaze.direction.sqrMagnitude < .01f) return false;
        gaze.direction = gaze.direction.normalized;
        var nearest = 3f;
        for (var index = 0; index < _chairs.Length; index++)
        {
            var chair = _chairs[index];
            if (!chair || !chair.enabled) continue;
            var frame = _model!.SeatArtwork(index);
            var bounds = _model.SeatInteractionBounds(index);
            bounds.Expand(.12f);
            var localRay = new Ray(frame.InverseTransformPoint(gaze.origin), frame.InverseTransformVector(gaze.direction));
            if (!bounds.IntersectRay(localRay, out var localDistance) || localDistance < 0) continue;
            var target = frame.TransformPoint(localRay.GetPoint(localDistance));
            var distance = Vector3.Distance(gaze.origin, target);
            if (distance > nearest || !SeatLineClear(gaze.origin, target, index, actor)) continue;
            nearest = distance;
            seat = index;
        }
        return seat >= 0;
    }

    private bool SeatLineClear(Vector3 origin, Vector3 target, int seat, Transform? actor)
    {
        var direction = target - origin;
        var distance = direction.magnitude;
        if (distance < .01f) return true;
        var hits = _seatHits ??= new Il2CppStructArray<RaycastHit>(size: 32);
        var hitCount = Physics.RaycastNonAlloc(origin, direction / distance, hits, distance, ~0, QueryTriggerInteraction.Ignore);
        if (hitCount == hits.Length)
        {
            hits = Physics.RaycastAll(origin, direction / distance, distance, ~0, QueryTriggerInteraction.Ignore);
            hitCount = hits.Length;
        }
        for (var h = 0; h < hitCount; h++)
        {
            var hit = hits[h];
            var collider = hit.collider;
            if (!collider || IsSeatTransitionCollider(collider, seat)) continue;
            // Hands, equipment and the actor's own capsule are not obstacles to
            // that actor's seat interaction. Other players and the hull still are.
            if (actor != null && actor && collider.transform.IsChildOf(actor)) continue;
            if (hit.distance < distance - .025f) return false;
        }
        return true;
    }

    internal bool TryCabinStandingPoint(int seat, Transform? actor, out Vector3 position)
    {
        position = default;
        if (!Ready || seat < 0 || seat >= _chairs.Length) return false;
        var seatPoint = SpacecraftLayout.Seat(seat);
        for (var sample = 0; sample < 6; sample++)
        {
            var local = sample switch
            {
                0 => new Vector3(-.06f, -1.26f, seatPoint.z),
                1 => new Vector3(-.06f, -1.26f, seatPoint.z - .55f),
                2 => new Vector3(-.06f, -1.26f, 5.25f),
                3 => new Vector3(-.06f, -1.26f, 1.05f),
                4 => new Vector3(1.95f, -1.26f, 4.95f),
                _ => new Vector3(-2.2f, -1.26f, 3.15f)
            };
            if (!TryWalkSurface(Root.transform.TransformPoint(local), out var surface)) continue;
            var up = Root.transform.up;
            var feet = surface + up * .1f;
            local = Root.transform.InverseTransformPoint(feet);
            var seated = seatPoint + SeatAdjustmentOffset(seat) + Vector3.down * .8f;
            if (!CrewCabinStep(local, local) || !CrewSeatPathClear(seated, local, seat)) continue;
            var clear = true;
            // Only the exiting actor is ignored. Native furniture, the roof,
            // other occupants and external objects still block restoration.
            var overlaps = _standingOverlaps ??= new Il2CppReferenceArray<Collider>(size: 64);
            try
            {
                var count = Physics.OverlapCapsuleNonAlloc(feet + up * CrewCapsuleBottom,
                    feet + up * CrewCapsuleTop, CrewCapsuleRadius, overlaps, ~0, QueryTriggerInteraction.Ignore);
                if (count < 0 || count >= overlaps.Length) continue;
                for (var i = 0; i < count; i++)
                {
                    var collider = overlaps[i];
                    if (collider && actor != null && actor && collider.transform.IsChildOf(actor)) continue;
                    clear = false;
                    break;
                }
            }
            finally
            {
                // Release native object references even after an early exit or failed query.
                for (var i = 0; i < _standingOverlaps.Length; i++) _standingOverlaps[i] = null!;
            }
            if (!clear) continue;
            position = feet;
            return true;
        }
        return false;
    }

    private static Vector3 Input(Vector3 value) => Finite(value) ? Vector3.ClampMagnitude(value, 1) : Vector3.zero;
    private static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
    private static bool WithinMagnitude(Vector3 value, double maximum)
        => Finite(value) && (double)value.x * value.x + (double)value.y * value.y + (double)value.z * value.z <= maximum * maximum;
    private static float Fraction(float value) => float.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
    private static bool Valid(Quaternion value) => float.IsFinite(value.x) && float.IsFinite(value.y)
        && float.IsFinite(value.z) && float.IsFinite(value.w)
        && value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w is > .001f and < 100;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _serviceHold = null;
        _serviceHoldApplied = ServiceSuspended = false;
        _supportHits = _rampHits = _seatHits = _controlHits = null;
        _standingOverlaps = _dockOverlaps = null;
        DisposeNpcNavigation();
        DisposeDamage();
        if (Root) Root.SetActive(false);
        _model?.Dispose();
        _model = null;
        if (Root) UnityEngine.Object.Destroy(Root);
        if (_contact) UnityEngine.Object.Destroy(_contact);
        _contact = null;
    }
}
