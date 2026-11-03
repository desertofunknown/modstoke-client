// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientAircraftBody
{
    private readonly GearContact[] _gearContacts = new GearContact[3];
    private readonly GearGroundMotion[] _gearGroundMotion = new GearGroundMotion[3];
    private Il2CppStructArray<RaycastHit>? _gearRaycastHits;

    private struct GearContact
    {
        internal Vector3 Point;
        internal Vector3 Normal;
        internal float Load;
        internal int Wheel;
        internal int Ground;
    }

    private struct GearGroundMotion
    {
        internal Rigidbody? Body;
        internal Vector3 LinearDelta;
        internal Vector3 AngularDelta;
    }

    private void ApplyLandingGear(bool brake, float dt)
    {
        var transform = Root.transform;
        var contactCount = 0;
        var groundCount = 0;
        var loadedContacts = 0;
        Array.Clear(_gearGroundMotion, 0, _gearGroundMotion.Length);
        for (var wheel = 0; wheel < 3; wheel++)
        {
            var origin = transform.TransformPoint(AircraftLayout.Wheel(wheel) + Vector3.up * .20f);
            const float reach = .68f;
            var direction = -transform.up;
            var hits = _gearRaycastHits ??= new Il2CppStructArray<RaycastHit>(size: 32);
            var hitCount = Physics.RaycastNonAlloc(origin, direction, hits, reach, ~0, QueryTriggerInteraction.Ignore);
            if (hitCount == hits.Length)
            {
                // A full buffer can omit the nearest non-self hit. Preserve the complete query.
                hits = Physics.RaycastAll(origin, direction, reach, ~0, QueryTriggerInteraction.Ignore);
                hitCount = hits.Length;
            }
            var nearest = reach + 1;
            RaycastHit contact = default;
            for (var h = 0; h < hitCount; h++)
            {
                var candidate = hits[h];
                var collider = candidate.collider;
                if (!collider || collider.attachedRigidbody == Body || collider.transform.IsChildOf(transform)
                    || candidate.distance >= nearest) continue;
                contact = candidate;
                nearest = candidate.distance;
            }
            // An unsuitable near face still blocks the tire ray; it cannot reach
            // through that obstruction to use a more convenient surface behind it.
            if (nearest > reach || Vector3.Dot(contact.normal, transform.up) < .35f) continue;
            var movingGround = contact.rigidbody;
            var velocity = Body.GetPointVelocity(contact.point);
            if (movingGround) velocity -= movingGround.GetPointVelocity(contact.point);
            var normalSpeed = Vector3.Dot(velocity, contact.normal);
            var spring = wheel == 2 ? 10000f : 18000f;
            var damper = wheel == 2 ? 1700f : 2800f;
            var load = Math.Clamp((reach - nearest) * spring - normalSpeed * damper, 0, 24000);
            if (load <= 0) continue;
            if (load > 5) loadedContacts++;
            var ground = -1;
            if (movingGround)
            {
                for (var g = 0; g < groundCount; g++)
                    if (_gearGroundMotion[g].Body == movingGround) { ground = g; break; }
                if (ground < 0)
                {
                    ground = groundCount++;
                    _gearGroundMotion[ground] = new GearGroundMotion
                    {
                        Body = movingGround,
                        LinearDelta = !movingGround.isKinematic && movingGround.useGravity
                            ? Physics.gravity * dt : Vector3.zero
                    };
                }
            }
            _gearContacts[contactCount++] = new GearContact
            {
                Point = contact.point, Normal = contact.normal, Load = load, Wheel = wheel, Ground = ground
            };
        }
        _grounded = loadedContacts > 0 && transform.up.y > .35f;

        // AddForce does not immediately change velocity. Predict this step's
        // gravity and all suspension reactions before calculating tire slip.
        var linearDelta = Body.useGravity ? Physics.gravity * dt : Vector3.zero;
        var angularDelta = Vector3.zero;
        for (var i = 0; i < contactCount; i++)
        {
            var contact = _gearContacts[i];
            ApplyGearForce(contact, contact.Normal * contact.Load, dt, ref linearDelta, ref angularDelta);
        }
        for (var i = 0; i < contactCount; i++)
        {
            var contact = _gearContacts[i];
            var velocity = Body.GetPointVelocity(contact.Point) + linearDelta
                + Vector3.Cross(angularDelta, contact.Point - Body.worldCenterOfMass);
            Rigidbody? groundBody = null;
            if (contact.Ground >= 0)
            {
                var motion = _gearGroundMotion[contact.Ground];
                groundBody = motion.Body;
                if (groundBody != null && groundBody)
                    velocity -= groundBody.GetPointVelocity(contact.Point) + motion.LinearDelta
                        + Vector3.Cross(motion.AngularDelta, contact.Point - groundBody.worldCenterOfMass);
            }
            var steering = contact.Wheel == 2
                ? ClientAircraftAerodynamics.NoseSteering(_yaw, Body.velocity.magnitude) : 0;
            var wheelForward = Quaternion.AngleAxis(steering, transform.up) * transform.forward;
            wheelForward = Vector3.ProjectOnPlane(wheelForward, contact.Normal).normalized;
            var wheelRight = Vector3.Cross(contact.Normal, wheelForward);

            // Unit-impulse point responses include translation and rotation of
            // both bodies. The second axis sees the first axis's proposed impulse.
            var sideResponse = GearPointImpulseResponse(Body, contact.Point, wheelRight)
                + GearPointImpulseResponse(groundBody, contact.Point, wheelRight);
            var sideInverseMass = Vector3.Dot(wheelRight, sideResponse);
            var sideImpulse = Math.Clamp(-Vector3.Dot(velocity, wheelRight) / sideInverseMass,
                -contact.Load * .85f * dt, contact.Load * .85f * dt);
            var forwardResponse = GearPointImpulseResponse(Body, contact.Point, wheelForward)
                + GearPointImpulseResponse(groundBody, contact.Point, wheelForward);
            var forwardInverseMass = Vector3.Dot(wheelForward, forwardResponse);
            var longitudinal = Vector3.Dot(velocity + sideResponse * sideImpulse, wheelForward);
            var rolling = brake ? .72f : .018f;
            var forwardImpulse = Math.Clamp(-longitudinal / forwardInverseMass,
                -contact.Load * rolling * dt, contact.Load * rolling * dt);
            var friction = (wheelRight * sideImpulse + wheelForward * forwardImpulse) / dt;
            var limit = contact.Load * .90f;
            if (friction.sqrMagnitude > limit * limit) friction = friction.normalized * limit;
            ApplyGearForce(contact, friction, dt, ref linearDelta, ref angularDelta);
        }
    }

    private void ApplyGearForce(GearContact contact, Vector3 force, float dt,
        ref Vector3 linearDelta, ref Vector3 angularDelta)
    {
        Body.AddForceAtPosition(force, contact.Point, ForceMode.Force);
        var impulse = force * dt;
        linearDelta += impulse / Body.mass;
        angularDelta += GearInverseInertia(Body, Vector3.Cross(contact.Point - Body.worldCenterOfMass, impulse));
        if (contact.Ground < 0) return;
        ref var motion = ref _gearGroundMotion[contact.Ground];
        var ground = motion.Body;
        if (ground == null || !ground || ground.isKinematic) return;
        ground.AddForceAtPosition(-force, contact.Point, ForceMode.Force);
        motion.LinearDelta -= impulse / ground.mass;
        motion.AngularDelta -= GearInverseInertia(ground, Vector3.Cross(contact.Point - ground.worldCenterOfMass, impulse));
    }

    private static Vector3 GearPointImpulseResponse(Rigidbody? body, Vector3 point, Vector3 impulse)
    {
        if (body == null || !body || body.isKinematic) return Vector3.zero;
        var arm = point - body.worldCenterOfMass;
        return impulse / body.mass + Vector3.Cross(GearInverseInertia(body, Vector3.Cross(arm, impulse)), arm);
    }

    private static Vector3 GearInverseInertia(Rigidbody body, Vector3 torqueImpulse)
    {
        var orientation = body.rotation * body.inertiaTensorRotation;
        var local = Quaternion.Inverse(orientation) * torqueImpulse;
        var inertia = body.inertiaTensor;
        // A zero principal inertia represents an infinite (locked) moment.
        local = new Vector3(inertia.x > 0 ? local.x / inertia.x : 0,
            inertia.y > 0 ? local.y / inertia.y : 0, inertia.z > 0 ? local.z / inertia.z : 0);
        return orientation * local;
    }
}
