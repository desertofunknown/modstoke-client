// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientAircraftBody : IDisposable
{
    internal const float GearStopRadius = .22f;
    private const float EmptyMass = 610;
    private const float FuelMass = 60;
    private const float OccupantMass = 80;
    private static readonly Vector3 EmptyCenter = new(0, .48f, .55f);
    private static readonly Vector3 FuelCenter = new(0, 1.30f, .20f);
    private static readonly Vector3 EmptyInertia = new(1180, 2780, 1700);
    private readonly bool _authority;
    private readonly ClientAircraftWind _wind = new();
    private Vector3 _windVelocity;
    private ClientAircraftModel? _model;
    private ClientAircraftAudio? _audio;
    private PhysicMaterial? _contactMaterial;
    private AircraftFlightState _replica;
    private long _replicaAt;
    private float _fuel = 1;
    private float _health = 1;
    private float _throttle;
    private float _rpm;
    private float _pitch;
    private float _roll;
    private float _yaw;
    private float _flapExtension;
    private float _massFuel = -1;
    private byte _occupantMask;
    private byte _massOccupants = byte.MaxValue;
    private float _nextGroundEffect;
    private Il2CppStructArray<RaycastHit>? _groundEffectRaycastHits;
    private float _portGroundEffect = 1;
    private float _starboardGroundEffect = 1;
    private float _lastImpact = -1;
    private float _impactDamage;
    private byte _flaps;
    private bool _engine;
    private bool _lights;
    private bool _brake = true;
    private Guid _pilotLease;
    private uint _lastRequestedInteraction;
    private uint _interactionSequence;
    private AircraftInteraction _interaction;
    private float _interactionAge = 2;
    private bool _grounded;
    private bool _stall;
    private bool _stepped;
    private bool _disposed;
    private readonly BoxCollider[,] _chairs = new BoxCollider[3, 2];
    private readonly BoxCollider[] _doors = new BoxCollider[2];
    private readonly bool[] _doorTargets = new bool[2];
    private readonly float[] _doorAngles = new float[2];
    private Il2CppStructArray<RaycastHit>? _interactionHits;
    private Il2CppReferenceArray<Collider>? _interactionOverlaps;

    internal GameObject Root { get; private set; } = null!;
    internal Rigidbody Body { get; private set; } = null!;
    internal AircraftFlightState State => !Root || !Body ? default : !_authority
        ? _replica with { Position = Body.position, Rotation = Body.rotation }
        : new AircraftFlightState(Body.position, Body.rotation, Body.velocity, Body.angularVelocity,
            _throttle, _rpm, _fuel, _health, _engine, _grounded, _stall, _flaps, _lights,
            _pitch, _roll, _yaw, _windVelocity, _brake, _interaction, _interactionSequence, _interactionAge);

    internal ClientAircraftBody(string id, Vector3 position, Quaternion rotation, bool authority)
    {
        _authority = authority;
        try
        {
            Root = new GameObject("Modstoke aircraft " + id);
            Root.transform.SetPositionAndRotation(position, rotation);
            Body = Root.AddComponent<Rigidbody>();
            Body.mass = EmptyMass + FuelMass;
            Body.drag = 0;
            Body.angularDrag = 0;
            Body.useGravity = authority;
            Body.isKinematic = !authority;
            Body.interpolation = RigidbodyInterpolation.Interpolate;
            Body.collisionDetectionMode = authority ? CollisionDetectionMode.ContinuousDynamic : CollisionDetectionMode.Discrete;
            Body.maxAngularVelocity = 4;
            _contactMaterial = new PhysicMaterial("Aircraft contact")
            {
                dynamicFriction = .24f,
                staticFriction = .28f,
                bounciness = .02f,
                frictionCombine = PhysicMaterialCombine.Minimum,
                bounceCombine = PhysicMaterialCombine.Minimum
            };
            for (var i = 1; i < AircraftLayout.Hulls.Length; i++)
            {
                var hull = AircraftLayout.Hulls[i];
                AddHull(hull.Center, hull.Size);
            }
            AddCabinCollision();
            for (var i = 0; i < 3; i++)
            {
                // These stops protect a fully compressed gear leg. The raycast tire
                // supplies the normal rolling contact and suspension.
                var stop = Root.AddComponent<SphereCollider>();
                stop.center = AircraftLayout.Wheel(i);
                stop.radius = GearStopRadius;
                stop.sharedMaterial = _contactMaterial;
            }
            UpdateMass();
            if (!global::Modstoke.Sons.GameRuntime.Environment.IsDedicatedServer)
                _model = new ClientAircraftModel(Root.transform);
            if (authority) ClientAircraftCollisionRelay.Attach(Root, this);
            _replica = new AircraftFlightState(position, rotation, Vector3.zero, Vector3.zero,
                0, 0, 1, 1, false, false, false, 0, false, 0, 0, 0, Vector3.zero,
                true, AircraftInteraction.None, 0, 2);
        }
        catch { Dispose(); throw; }
    }

    private void AddHull(Vector3 center, Vector3 size)
    {
        var collider = Root.AddComponent<BoxCollider>();
        collider.center = center;
        collider.size = size;
        collider.sharedMaterial = _contactMaterial;
    }

    private BoxCollider AddCabinPart(string name, Vector3 center, Vector3 size, float pitch = 0)
    {
        var part = new GameObject(name) { layer = Root.layer };
        part.transform.SetParent(Root.transform, false);
        part.transform.localPosition = center;
        part.transform.localRotation = Quaternion.Euler(pitch, 0, 0);
        var collider = part.AddComponent<BoxCollider>();
        collider.size = size;
        collider.sharedMaterial = _contactMaterial;
        return collider;
    }

    private void AddCabinCollision()
    {
        foreach (var part in AircraftLayout.CabinShell)
            AddCabinPart("Cabin shell", part.Center, part.Size, part.Pitch);
        for (var side = 0; side < 2; side++)
        {
            var sign = side == 0 ? -1 : 1;
            AddCabinPart("Rear cabin side", new(sign * .65f, .71f, -.91f), new(.07f, 1.30f, .94f));
            AddCabinPart("Forward cabin side", new(sign * .64f, .68f, 1.22f), new(.07f, 1.20f, .34f));
            AddCabinPart("Forward door jamb", new(sign * .70f, .75f, 1.02f), new(.08f, 1.30f, .07f));
            AddCabinPart("Rear door jamb", new(sign * .71f, .75f, -.47f), new(.08f, 1.30f, .06f));
            AddCabinPart("Door header", new(sign * .69f, 1.42f, .275f), new(.10f, .06f, 1.45f));
            AddCabinPart("Door sill", new(sign * .63f, .065f, .275f), new(.26f, .13f, 1.45f));
            AddCabinPart("Lower cabin liner", new(sign * .59f, -.005f, -.80f), new(.20f, .08f, 1.05f));
            AddCabinPart("Boarding tread", new(sign * .91f, -.25f, -.22f), new(.24f, .025f, .27f));
            var door = AddCabinPart("Cabin door " + side, AircraftLayout.DoorHinge(side), AircraftLayout.DoorSize);
            door.center = AircraftLayout.DoorCenter;
            _doors[side] = door;
        }
        for (var seat = 0; seat < 3; seat++)
        {
            var point = AircraftLayout.Seat(seat);
            _chairs[seat, 0] = AddCabinPart("Chair cushion " + seat, point + new Vector3(0, -.06f, 0), new(.46f, .12f, .44f));
            _chairs[seat, 1] = AddCabinPart("Chair back " + seat, point + new Vector3(0, .29f, -.25f), new(.45f, .56f, .08f), 5.73f);
        }
    }

    internal void FixedUpdate(AircraftControls input, bool fresh)
    {
        if (_disposed || !Root || !Body) return;
        var dt = Math.Clamp(Time.fixedDeltaTime, .001f, .1f);
        if (!_authority)
        {
            if (_replicaAt == 0) return;
            var age = Math.Clamp((Environment.TickCount64 - _replicaAt) / 1000f, 0, .15f);
            var target = _replica.Position + _replica.Velocity * age;
            var rotation = _replica.Rotation;
            var spin = _replica.AngularVelocity.magnitude;
            if (spin > .001f)
                rotation = Quaternion.AngleAxis(spin * age * Mathf.Rad2Deg, _replica.AngularVelocity / spin) * rotation;
            var blend = 1 - Mathf.Exp(-18 * dt);
            Body.MovePosition(Vector3.Lerp(Body.position, target, blend));
            Body.MoveRotation(Quaternion.Slerp(Body.rotation, rotation, blend));
            return;
        }
        _stepped = true;
        _interactionAge = MathF.Min(2, _interactionAge + dt);
        if (fresh)
        {
            _engine = input.Engine && _fuel > 0 && _health > .08f;
            _lights = input.Lights;
            _flaps = (byte)Math.Clamp((int)input.Flaps, 0, 2);
            if (input.Interaction != AircraftInteraction.None
                && unchecked((int)(input.InteractionSequence - _lastRequestedInteraction)) > 0)
            {
                _lastRequestedInteraction = input.InteractionSequence;
                _interaction = input.Interaction;
                _interactionSequence++;
                _interactionAge = 0;
            }
        }
        if (_fuel <= 0 || _health <= .08f) _engine = false;
        _pitch = Mathf.MoveTowards(_pitch, fresh ? Axis(input.Pitch) : 0, dt * 3.2f);
        _roll = Mathf.MoveTowards(_roll, fresh ? Axis(input.Roll) : 0, dt * 4f);
        _yaw = Mathf.MoveTowards(_yaw, fresh ? Axis(input.Yaw) : 0, dt * 3f);
        _flapExtension = Mathf.MoveTowards(_flapExtension, _flaps * .5f, dt * (16f / 30));
        // A lost pilot command idles the engine and centers the surfaces. Gravity,
        // airspeed and the tail still determine the flight path.
        _throttle = Mathf.MoveTowards(_throttle, fresh && _engine ? Fraction(input.Throttle) : 0, dt * .9f);
        UpdateMass();
        _wind.Advance(dt, Body);
        _windVelocity = _wind.Sample(Body.worldCenterOfMass);
        var propellerPoint = Root.transform.TransformPoint(new Vector3(0, .47f, 2.65f));
        var forwardSpeed = Vector3.Dot(Body.GetPointVelocity(propellerPoint) - _wind.Sample(propellerPoint),
            Root.transform.forward);
        var targetRpm = _engine ? 850 + 1850 * _throttle : MathF.Min(950, MathF.Abs(forwardSpeed) * 19);
        _rpm = Mathf.MoveTowards(_rpm, targetRpm, dt * (_engine ? 1100 : 850));
        _brake = fresh ? input.Brake : _grounded && Body.velocity.sqrMagnitude < 16;
        ApplyLandingGear(_brake, dt);
        var density = ClientAircraftAerodynamics.Density(Body.position.y);
        var slipstream = ApplyPropeller(forwardSpeed, density, dt);
        ApplyAerodynamics(density, slipstream);
    }

    private float ApplyPropeller(float forwardSpeed, float density, float dt)
    {
        if (_engine)
        {
            var spool = Math.Clamp((_rpm - 500) / 2200, 0, 1);
            var shaftPower = (2000 + 96000 * _throttle) * spool
                * (density / ClientAircraftAerodynamics.SeaLevelDensity) * (.35f + .65f * _health);
            var usefulPower = shaftPower * .82f;
            // The momentum model covers forward flight/static thrust. Treat
            // reverse inflow as static, rather than dividing by negative power flow.
            var advanceSpeed = MathF.Max(0, forwardSpeed);
            var induced = ClientAircraftAerodynamics.PropellerInducedSpeed(advanceSpeed, usefulPower, density);
            var thrust = usefulPower / MathF.Max(.1f, advanceSpeed + induced);
            Body.AddForceAtPosition(Root.transform.forward * thrust,
                Root.transform.TransformPoint(new Vector3(0, .47f, 2.65f)), ForceMode.Force);
            // Shaft reaction opposes the displayed propeller's rotation.
            Body.AddRelativeTorque(new Vector3(0, 0, -shaftPower / MathF.Max(80, _rpm * (MathF.PI / 30))),
                ForceMode.Force);
            _fuel = MathF.Max(0, _fuel - dt * (.12f + .88f * _throttle) / 5400);
            if (_fuel <= 0) _engine = false;
            return induced;
        }
        // A windmilling propeller extracts energy during an engine-off glide.
        var propDrag = .5f * density * forwardSpeed * MathF.Abs(forwardSpeed)
            * Mathf.Lerp(.025f, .18f, Mathf.Clamp01(_rpm / 950));
        Body.AddForce(-Root.transform.forward * propDrag, ForceMode.Force);
        return 0;
    }

    private void ApplyAerodynamics(float density, float slipstream)
    {
        var transform = Root.transform;
        var velocity = Body.velocity - _windVelocity;
        var speed = velocity.magnitude;
        _stall = false;
        UpdateGroundEffect();
        var condition = .65f + .35f * _health;
        var downwashSum = 0f;
        for (var side = -1; side <= 1; side += 2)
        {
            var ground = side < 0 ? _portGroundEffect : _starboardGroundEffect;
            // Three strips per half-wing give the roots, flaps and ailerons
            // their own local airflow, including rotation and dihedral.
            var span = new Vector3(.99863f, side * .05234f, 0); // 3 degrees
            var normal = Vector3.Cross(Vector3.forward, span);
            var worldSpan = transform.TransformDirection(span);
            var slope = 2 * MathF.PI / (1 + 2 * ground
                / (ClientAircraftAerodynamics.AspectRatio * ClientAircraftAerodynamics.SpanEfficiency));
            for (var strip = 0; strip < 3; strip++)
            {
                var point = transform.TransformPoint(new Vector3(side * (strip + .5f) * AircraftLayout.Span / 6, 1.49f, .60f));
                var air = transform.InverseTransformDirection(Body.GetPointVelocity(point) - _wind.Sample(point));
                air -= span * Vector3.Dot(air, span);
                var squaredSpeed = air.sqrMagnitude;
                if (squaredSpeed < .01f) continue;
                var alpha = MathF.Atan2(-Vector3.Dot(air, normal), air.z) + .035f - strip * .013f;
                var flap = strip < 2 ? _flapExtension : 0;
                var aileron = strip == 2 ? -side * _roll * .10f : 0;
                var stallAngle = .28f - .025f * flap;
                ClientAircraftAerodynamics.Polar(alpha, -.035f - flap * .16f - aileron,
                    slope, stallAngle, ClientAircraftAerodynamics.InducedDragFactor * ground,
                    .024f + .12f * flap + .012f * MathF.Abs(aileron / .1f),
                    out var cl, out var cd, out var separation);
                cl *= condition;
                cd += (1 - _health) * .05f;
                var flow = transform.TransformDirection(air) / MathF.Sqrt(squaredSpeed);
                var lift = Vector3.Cross(flow, worldSpan);
                var pressureArea = .5f * density * squaredSpeed * ClientAircraftAerodynamics.WingArea / 6;
                Body.AddForceAtPosition((lift * cl - flow * cd) * pressureArea, point, ForceMode.Force);
                // Cambered wings also exert a nose-down pitching moment.
                Body.AddRelativeTorque(new Vector3(pressureArea * ClientAircraftAerodynamics.MeanChord
                    * (.04f + flap * .025f) * (1 - separation), 0, 0), ForceMode.Force);
                downwashSum += cl * ground * (1 - separation);
                _stall |= !_grounded && squaredSpeed > 64 && air.z > 0 && MathF.Abs(alpha) > stallAngle;
            }
        }
        // Downwash reduces tail incidence; propeller wake increases local
        // control authority even while nearly stationary. No attitude servo.
        var downwash = 2 * (downwashSum / 6) * ClientAircraftAerodynamics.InducedDragFactor;
        var tailPoint = transform.TransformPoint(new Vector3(0, .64f, -3.55f));
        var tailAir = transform.InverseTransformDirection(Body.GetPointVelocity(tailPoint) - _wind.Sample(tailPoint));
        tailAir.y += MathF.Max(0, tailAir.z) * downwash;
        tailAir.z += slipstream * 1.2f;
        var tailSpeed = MathF.Sqrt(tailAir.y * tailAir.y + tailAir.z * tailAir.z);
        if (tailSpeed > .1f)
        {
            var alpha = MathF.Atan2(-tailAir.y, tailAir.z) + .012f - _pitch * .35f;
            ClientAircraftAerodynamics.Polar(alpha, 0, 3.3f, .32f, .09f, .018f,
                out var cl, out var cd, out _);
            var flow = transform.TransformDirection(new Vector3(0, tailAir.y, tailAir.z)) / tailSpeed;
            var pressureArea = .5f * density * tailSpeed * tailSpeed * 2.4f;
            Body.AddForceAtPosition((Vector3.Cross(flow, transform.right) * cl * condition
                - flow * cd) * pressureArea, tailPoint, ForceMode.Force);
        }
        var finPoint = transform.TransformPoint(new Vector3(0, 1.30f, -3.60f));
        var finAir = transform.InverseTransformDirection(Body.GetPointVelocity(finPoint) - _wind.Sample(finPoint));
        finAir.z += slipstream * .85f;
        var finSpeed = MathF.Sqrt(finAir.x * finAir.x + finAir.z * finAir.z);
        if (finSpeed > .1f)
        {
            var alpha = -MathF.Atan2(finAir.x, finAir.z) - _yaw * .28f;
            ClientAircraftAerodynamics.Polar(alpha, 0, 2.4f, .35f, .25f, .018f,
                out var cl, out var cd, out _);
            var flow = transform.TransformDirection(new Vector3(finAir.x, 0, finAir.z)) / finSpeed;
            var sideways = Vector3.Cross(transform.up, flow);
            Body.AddForceAtPosition((sideways * cl * condition - flow * cd) * (.5f * density * finSpeed * finSpeed * 1.45f),
                finPoint, ForceMode.Force);
        }
        if (speed < .1f) return;
        var localVelocity = transform.InverseTransformDirection(velocity);
        Body.AddForce(-velocity / speed * (.5f * density * speed * speed * .32f), ForceMode.Force);
        Body.AddRelativeForce(new Vector3(-localVelocity.x * MathF.Abs(localVelocity.x) * 3.6f,
            -localVelocity.y * MathF.Abs(localVelocity.y) * 1.5f, 0) * (.5f * density), ForceMode.Force);
        // Roll/pitch/yaw damping now comes from each surface's point velocity.
    }

    private void UpdateGroundEffect()
    {
        if (Time.fixedTime < _nextGroundEffect) return;
        _nextGroundEffect = Time.fixedTime + .1f;
        _portGroundEffect = SampleGroundEffect(-2.65f);
        _starboardGroundEffect = SampleGroundEffect(2.65f);
    }

    private float SampleGroundEffect(float x)
    {
        var transform = Root.transform;
        if (transform.up.y < .5f) return 1;
        var point = transform.TransformPoint(new Vector3(x, 1.49f, .60f));
        var nearest = AircraftLayout.Span;
        var hits = _groundEffectRaycastHits ??= new Il2CppStructArray<RaycastHit>(size: 32);
        var hitCount = Physics.RaycastNonAlloc(point, Vector3.down, hits, nearest, ~0, QueryTriggerInteraction.Ignore);
        if (hitCount == hits.Length)
        {
            // A full buffer can omit the nearest eligible hit. Preserve the complete query.
            hits = Physics.RaycastAll(point, Vector3.down, nearest, ~0, QueryTriggerInteraction.Ignore);
            hitCount = hits.Length;
        }
        for (var i = 0; i < hitCount; i++)
        {
            var hit = hits[i];
            var collider = hit.collider;
            if (!collider || collider.attachedRigidbody == Body
                || collider.transform.IsChildOf(transform) || hit.normal.y < .7f) continue;
            nearest = MathF.Min(nearest, hit.distance);
        }
        return nearest < AircraftLayout.Span ? ClientAircraftAerodynamics.GroundEffect(nearest) : 1;
    }

    internal void SetOccupants(byte mask) => _occupantMask = (byte)(mask & 7);

    internal void SetVisibleOccupants(byte mask) => _model?.SetOccupants(mask);

    internal void SetPilot(Guid lease)
    {
        if (!_authority || lease == _pilotLease) return;
        _pilotLease = lease;
        _lastRequestedInteraction = 0;
        _interaction = AircraftInteraction.None;
        _interactionAge = 2;
    }

    internal bool TryCrewTargets(int seat, out AircraftCrewTargets targets)
    {
        targets = default;
        return !_disposed && _model != null && _model.TryCrewTargets(seat, out targets);
    }

    private void UpdateMass()
    {
        var fuel = _fuel * FuelMass;
        if (_massOccupants == _occupantMask && MathF.Abs(_massFuel - fuel) < .05f) return;
        _massFuel = fuel;
        _massOccupants = _occupantMask;
        var mass = EmptyMass + fuel;
        var weightedCenter = EmptyCenter * EmptyMass + FuelCenter * fuel;
        for (var seat = 0; seat < 3; seat++)
        {
            if ((_occupantMask & (1 << seat)) == 0) continue;
            mass += OccupantMass;
            weightedCenter += (AircraftLayout.Seat(seat) + Vector3.up * .35f) * OccupantMass;
        }
        var center = weightedCenter / mass;
        var inertia = EmptyInertia + PointInertia(EmptyMass, EmptyCenter - center)
            + PointInertia(fuel, FuelCenter - center);
        for (var seat = 0; seat < 3; seat++)
        {
            if ((_occupantMask & (1 << seat)) != 0)
                inertia += PointInertia(OccupantMass, AircraftLayout.Seat(seat) + Vector3.up * .35f - center);
        }
        Body.mass = mass;
        Body.centerOfMass = center;
        // Diagonal principal-axis approximation of the airframe and payload.
        Body.inertiaTensorRotation = Quaternion.identity;
        Body.inertiaTensor = inertia;
    }

    private static Vector3 PointInertia(float mass, Vector3 offset) => new(
        mass * (offset.y * offset.y + offset.z * offset.z),
        mass * (offset.x * offset.x + offset.z * offset.z),
        mass * (offset.x * offset.x + offset.y * offset.y));

    internal void Update(float dt, ClientTabletPreferencesSnapshot? preferences)
    {
        if (_disposed || !Root || !Body) return;
        var elapsed = float.IsFinite(dt) ? Math.Clamp(dt, 0, .1f) : 0;
        var state = State;
        for (var side = 0; side < 2; side++)
        {
            var angle = Mathf.MoveTowards(_doorAngles[side], _doorTargets[side] ? 65 : 0, 100 * elapsed);
            if (angle == _doorAngles[side]) continue;
            _doorAngles[side] = angle;
            _doors[side].transform.localRotation = Quaternion.Euler(0, (side == 0 ? 1 : -1) * angle, 0);
        }
        _model?.SetDoorAngles(_doorAngles[0], _doorAngles[1]);
        _model?.Update(state, elapsed);
        if (preferences != null)
        {
            _audio ??= new ClientAircraftAudio();
            _audio.Update(state, preferences);
        }
        else if (_audio != null)
        {
            _audio.Dispose();
            _audio = null;
        }
    }

    internal bool TryCockpitControl(Ray ray, out string command)
    {
        command = "";
        return !_disposed && _model != null && _model.TryCockpitControl(ray, out command);
    }

    internal void SetDoorOpen(bool port, bool starboard)
    {
        if (_disposed) return;
        _doorTargets[0] = port;
        _doorTargets[1] = starboard;
    }

    internal bool DoorOpen(bool starboard) => !_disposed && _doorAngles[starboard ? 1 : 0] >= 64.9f;
    internal bool DoorsClosed => !_disposed && _doorAngles[0] <= .1f && _doorAngles[1] <= .1f;

    internal bool ContainsCabin(Vector3 actorPosition)
    {
        if (_disposed || !Root || !Finite(actorPosition)) return false;
        var local = Root.transform.InverseTransformPoint(actorPosition);
        return MathF.Abs(local.x) <= .69f && local.y >= -.12f && local.y <= .65f
            && local.z >= -1.27f && local.z <= 1.08f;
    }

    internal bool CanReachSeat(Vector3 actorPosition, int seat, Transform? actor, Vector3? upperBodyOrigin)
    {
        if (_disposed || !Root || seat is < 0 or > 2 || !Finite(actorPosition)) return false;
        var target = Root.transform.TransformPoint(AircraftLayout.Seat(seat) + Vector3.up * .28f);
        var origin = actorPosition + Vector3.up * .85f;
        // Preserve the low-origin reach envelope even when looking over the sill.
        var distance = (target - origin).sqrMagnitude;
        if (!float.IsFinite(distance) || distance > 2.5f * 2.5f) return false;
        if (InteractionLineClear(origin, target, actor, seat)) return true;
        return upperBodyOrigin is { } upper && Finite(upper)
            && (target - upper).sqrMagnitude <= 2.5f * 2.5f
            && InteractionLineClear(upper, target, actor, seat);
    }

    internal bool TryFindSeat(Ray gaze, Vector3 actorPosition, Transform? actor, out int seat)
    {
        seat = -1;
        // A real light-aircraft cabin is too low for a standing player capsule.
        // Permit reaching its exact chair through the open doorway; hull and
        // door occlusion still apply on both the client and the host.
        if (_disposed || !Root || !Finite(actorPosition) || !Finite(gaze.origin)
            || !Finite(gaze.direction) || gaze.direction.sqrMagnitude < .01f) return false;
        var frame = Root.transform;
        var ray = new Ray(frame.InverseTransformPoint(gaze.origin), frame.InverseTransformDirection(gaze.direction.normalized));
        var nearest = 3f;
        for (var index = 0; index < 3; index++)
        {
            var bounds = new Bounds(AircraftLayout.Seat(index) + new Vector3(0, .30f, -.04f), new Vector3(.50f, .84f, .58f));
            bounds.Expand(.12f);
            if (!bounds.IntersectRay(ray, out var distance) || distance < 0 || distance > nearest
                || !InteractionLineClear(gaze.origin, gaze.origin + gaze.direction.normalized * distance, actor, index)) continue;
            seat = index;
            nearest = distance;
        }
        return seat >= 0;
    }

    internal bool CanReachDoor(Vector3 actorPosition, int side, Transform? actor)
    {
        if (_disposed || !Root || side is < 0 or > 1 || !Finite(actorPosition)) return false;
        var origin = actorPosition + Vector3.up * .85f;
        var target = _doors[side].transform.TransformPoint(_doors[side].center);
        return (target - origin).sqrMagnitude <= 2.5f * 2.5f
            && InteractionLineClear(origin, target, actor, door: side);
    }

    internal bool TryFindDoor(Ray gaze, Vector3 actorPosition, Transform? actor, out int side)
    {
        side = -1;
        if (_disposed || !Root || !Finite(gaze.origin) || !Finite(gaze.direction)) return false;
        var nearest = 3f;
        for (var index = 0; index < 2; index++)
        {
            if (!_doors[index].Raycast(gaze, out var hit, nearest) || !CanReachDoor(actorPosition, index, actor)
                || !InteractionLineClear(gaze.origin, hit.point, actor, door: index)) continue;
            side = index;
            nearest = hit.distance;
        }
        return side >= 0;
    }

    private bool InteractionLineClear(Vector3 origin, Vector3 target, Transform? actor, int seat = -1, int door = -1)
    {
        bool Obstacle(Collider collider)
        {
            if (collider == null || !collider) return false;
            if (actor != null && actor && collider.transform.IsChildOf(actor)) return false;
            if (door >= 0 && collider.Pointer == _doors[door].Pointer) return false;
            return seat < 0 || collider.Pointer != _chairs[seat, 0].Pointer && collider.Pointer != _chairs[seat, 1].Pointer;
        }
        var overlaps = _interactionOverlaps ??= new Il2CppReferenceArray<Collider>(size: 32);
        var overlapCount = Physics.OverlapSphereNonAlloc(origin, .025f, overlaps, ~0, QueryTriggerInteraction.Ignore);
        if (overlapCount == overlaps.Length) return false;
        for (var index = 0; index < overlapCount; index++)
            if (Obstacle(overlaps[index])) return false;
        var travel = target - origin;
        var distance = travel.magnitude;
        if (distance < .01f) return true;
        var hits = _interactionHits ??= new Il2CppStructArray<RaycastHit>(size: 32);
        var count = Physics.RaycastNonAlloc(origin, travel / distance, hits, distance, ~0, QueryTriggerInteraction.Ignore);
        if (count == hits.Length) return false;
        for (var index = 0; index < count; index++)
            if (Obstacle(hits[index].collider)) return false;
        return true;
    }

    internal void SetReplica(AircraftFlightState state)
    {
        if (_authority || _disposed || !Body || !Finite(state.Position) || !Finite(state.Velocity)
            || !Finite(state.AngularVelocity) || !Finite(state.WindVelocity) || state.WindVelocity.sqrMagnitude > 2500
            || !float.IsFinite(state.Rotation.x)
            || !float.IsFinite(state.Rotation.y) || !float.IsFinite(state.Rotation.z)
            || !float.IsFinite(state.Rotation.w)) return;
        var rotationLength = state.Rotation.x * state.Rotation.x + state.Rotation.y * state.Rotation.y
            + state.Rotation.z * state.Rotation.z + state.Rotation.w * state.Rotation.w;
        if (rotationLength < .001f || !float.IsFinite(rotationLength)) return;
        _replica = state with
        {
            Rotation = state.Rotation.normalized,
            Throttle = Fraction(state.Throttle),
            Rpm = float.IsFinite(state.Rpm) ? Math.Clamp(state.Rpm, 0, 3000) : 0,
            Fuel = Fraction(state.Fuel), Health = Fraction(state.Health),
            Flaps = (byte)Math.Clamp((int)state.Flaps, 0, 2),
            PitchControl = Axis(state.PitchControl), RollControl = Axis(state.RollControl), YawControl = Axis(state.YawControl)
        };
        if (_replicaAt == 0 || (Body.position - state.Position).sqrMagnitude > 144)
        {
            Body.position = _replica.Position;
            Body.rotation = _replica.Rotation;
        }
        _replicaAt = Environment.TickCount64;
    }

    internal void Restore(float fuel, float health)
    {
        if (_disposed || !_authority || _stepped || _engine || !Body || Body.velocity.sqrMagnitude > .01f) return;
        _fuel = Fraction(fuel);
        _health = Fraction(health);
    }

    // The service owns authorization and the parked, unoccupied prerequisite.
    internal void Service()
    {
        if (_disposed || !_authority) return;
        _fuel = 1;
        _health = 1;
    }

    internal void Contact(Collision collision)
    {
        if (_disposed || !_authority || !Body) return;
        // Normal collision impulse measures the actual velocity change. A fast,
        // smooth runway roll must not be damaged merely for having forward speed.
        // Collision.impulse is an unstripping stub in this game's IL2CPP build.
        // The native contact pair still exposes the same summed impulse.
        // Depenetration and sustained support can produce a large solver impulse
        // without a damaging approach speed. Read the native header, as for impulse.
        var relativeSpeed = collision.m_Header.m_RelativeVelocity.magnitude;
        var deltaVelocity = MathF.Min(collision.m_Pair.ImpulseSum.magnitude / Body.mass, relativeSpeed);
        if (!float.IsFinite(deltaVelocity) || deltaVelocity <= 3.2f) return;
        // Compound hull contacts and OnCollisionStay belong to one impact until
        // damaging contact subsides. Charge increases in severity, not every callback.
        if (Time.fixedTime - _lastImpact >= .12f) _impactDamage = 0;
        _lastImpact = Time.fixedTime;
        var damage = Math.Clamp((deltaVelocity - 3.2f) * .065f, 0, 1);
        if (damage <= _impactDamage) return;
        _health = MathF.Max(0, _health - (damage - _impactDamage));
        _impactDamage = damage;
        if (_health <= .08f) _engine = false;
        _audio?.Impact(State, Math.Clamp(deltaVelocity / 16, 0, 1));
    }

    private static float Fraction(float value) => float.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
    private static float Axis(float value) => float.IsFinite(value) ? Math.Clamp(value, -1, 1) : 0;
    private static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gearRaycastHits = null;
        _groundEffectRaycastHits = null;
        try { _audio?.Dispose(); }
        finally
        {
            _audio = null;
            if (Root) ClientAircraftCollisionRelay.Detach(Root);
            try { _model?.Dispose(); }
            finally
            {
                _model = null;
                if (Root) { Root.SetActive(false); UnityEngine.Object.Destroy(Root); }
                if (_contactMaterial) UnityEngine.Object.Destroy(_contactMaterial);
                _contactMaterial = null;
            }
        }
    }
}
