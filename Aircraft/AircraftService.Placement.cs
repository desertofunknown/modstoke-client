// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientAircraftService
{
    private const float MinimumGroundUp = .984808f; // Ten degrees.
    private const float MinimumExitGroundUp = .819152f; // Thirty-five degrees.
    private const float DepartureClearance = 24;
    private ClientAircraftBody? _exitRestBody;
    private Vector3 _exitRestPosition;
    private Quaternion _exitRestRotation;
    private long _exitRestSince, _nextExitRestCheck;

    private bool TryPlacement(Vector3 actorPosition, Quaternion actorRotation,
        out Vector3 position, out Quaternion rotation, out string failure)
    {
        position = default;
        rotation = Quaternion.identity;
        failure = "Player position is not ready. Try again after moving onto solid ground.";
        if (!Finite(actorPosition) || !ValidRotation(actorRotation)) return false;
        var forward = actorRotation * Vector3.forward;
        forward.y = 0;
        if (forward.sqrMagnitude < .1f) return false;
        forward.Normalize();
        var right = Vector3.Cross(Vector3.up, forward);
        ReadOnlySpan<Vector2> candidates = stackalloc Vector2[]
        {
            new(0, 18), new(0, 22), new(0, 14),
            new(-4, 18), new(4, 18), new(-4, 22), new(4, 22)
        };
        string? clearanceFailure = null;
        foreach (var candidate in candidates)
        {
            var center = actorPosition + forward * candidate.y + right * candidate.x;
            if (!TryGroundPose(center, forward, out position, out rotation, out failure)) continue;
            if (!BoardingClear(position, rotation, out failure)
                || !DepartureClear(position, rotation, forward, out failure))
            {
                clearanceFailure ??= failure;
                continue;
            }
            failure = "";
            return true;
        }
        failure = clearanceFailure ?? "No clear spawn 14-22 m ahead. " + failure;
        return false;
    }

    private bool TryGroundPose(Vector3 center, Vector3 forward,
        out Vector3 position, out Quaternion rotation, out string failure)
    {
        position = default;
        rotation = Quaternion.LookRotation(forward, Vector3.up);
        failure = "No solid, level ground at this position. Face along a wide, dry clearing.";
        if (!TryGround(center + Vector3.up * 3, 6, out var middle)
            || MathF.Abs(middle.point.y - center.y) > 3) return false;

        position = new Vector3(center.x, middle.point.y + 1.03f, center.z);
        Span<Vector3> supports = stackalloc Vector3[3];
        // Fit the plane beneath the actual tires, then resample their shifted
        // positions after tilting. Terrain beyond the gear need not be flat.
        for (var pass = 0; pass < 3; pass++)
        {
            for (var wheel = 0; wheel < 3; wheel++)
            {
                var foot = AircraftLayout.Wheel(wheel) - Vector3.up * AircraftLayout.WheelRadius;
                var sample = position + rotation * foot;
                failure = "All three wheels need solid ground. Move clear of ledges or steep terrain.";
                if (!TryGround(sample + Vector3.up * 2, 4, out var hit)) return false;
                failure = "Could not verify dry ground under the wheels. Move away from water.";
                if (!DryGround(hit.point)) return false;
                supports[wheel] = hit.point;
            }
            var normal = Vector3.Cross(supports[2] - supports[0], supports[1] - supports[0]);
            failure = "The wheel supports are too steep. Find a gentler slope (under 10 degrees).";
            if (!Finite(normal) || normal.sqrMagnitude < .01f) return false;
            normal.Normalize();
            if (normal.y <= MinimumGroundUp) return false;
            rotation = Quaternion.LookRotation(Vector3.ProjectOnPlane(forward, normal), normal);
            var height = 0f;
            for (var wheel = 0; wheel < 3; wheel++)
            {
                var foot = AircraftLayout.Wheel(wheel) - Vector3.up * AircraftLayout.WheelRadius;
                height += supports[wheel].y - (rotation * foot).y;
            }
            position.y = height / 3 + .03f / normal.y;
        }
        // Confirm the final pose at every tire, including tire-side clearance.
        // The small gap lets suspension settle without spawning inside terrain.
        var up = rotation * Vector3.up;
        for (var wheel = 0; wheel < 3; wheel++)
        {
            var tire = position + rotation * AircraftLayout.Wheel(wheel);
            var foot = tire - up * AircraftLayout.WheelRadius;
            failure = "The ground beneath the wheels is too uneven. Shift to a smoother patch.";
            if (!TryGround(foot + Vector3.up * .3f, .6f, out var hit)) return false;
            var gap = Vector3.Dot(foot - hit.point, up);
            if (!float.IsFinite(gap) || gap < .005f || gap > .08f) return false;
            failure = "Could not verify dry ground under the wheels. Move away from water.";
            if (!DryGround(hit.point)) return false;
            failure = "A wheel is obstructed. Move clear of rocks, logs or other objects.";
            if (Physics.CheckSphere(tire, AircraftLayout.WheelRadius, ~0, QueryTriggerInteraction.Ignore))
                return false;
        }
        // Preserve obstacles in the query, but check the airframe's volumes
        // rather than requiring a whole clearing to share one ground height.
        failure = "The airframe is obstructed. Leave space around the wings, nose and tail.";
        foreach (var hull in AircraftLayout.Hulls)
            if (Physics.CheckBox(position + rotation * hull.Center, hull.Size * .5f + Vector3.one * .15f,
                rotation, ~0, QueryTriggerInteraction.Ignore)) return false;
        failure = "";
        return true;
    }

    private bool DepartureClear(Vector3 position, Quaternion rotation, Vector3 forward, out string failure)
    {
        // A parked footprint can fit between trees while leaving no way to taxi.
        // Follow the ground ahead, checking support and the space swept between
        // poses. This checks the first 24 m, not the entire takeoff runway.
        const float step = 3;
        for (var distance = step; distance <= DepartureClearance; distance += step)
        {
            if (!TryGroundPose(position + forward * step, forward,
                out var nextPosition, out var nextRotation, out var routeFailure))
            {
                failure = "Taxi route blocked within 24 m. " + routeFailure;
                return false;
            }
            failure = "The first 24 m ahead are obstructed. Leave the full wing width clear of trees, rocks and structures.";
            var travel = nextPosition - position;
            var localTravel = Quaternion.Inverse(rotation) * travel;
            var halfTravel = new Vector3(MathF.Abs(localTravel.x), MathF.Abs(localTravel.y),
                MathF.Abs(localTravel.z)) * .5f;
            var halfAngle = Quaternion.Angle(rotation, nextRotation) * Mathf.Deg2Rad * .5f;
            var turn = 2 * MathF.Sin(halfAngle);
            var wheelArc = 1 - MathF.Cos(halfAngle);
            foreach (var hull in AircraftLayout.Hulls)
            {
                // Bound rotation about the aircraft origin as well as translation;
                // endpoint checks alone can miss a tree between sampled poses.
                var padding = .15f + turn * (hull.Center.magnitude + hull.Size.magnitude * .5f);
                if (Physics.CheckBox(position + rotation * hull.Center + travel * .5f,
                    hull.Size * .5f + halfTravel + Vector3.one * padding,
                    rotation, ~0, QueryTriggerInteraction.Ignore)) return false;
            }
            for (var wheel = 0; wheel < 3; wheel++)
            {
                var local = AircraftLayout.Wheel(wheel);
                failure = "A wheel's taxi path is obstructed. Move clear of rocks, logs and other objects.";
                if (Physics.CheckCapsule(position + rotation * local, nextPosition + nextRotation * local,
                    ClientAircraftBody.GearStopRadius + wheelArc * local.magnitude,
                    ~0, QueryTriggerInteraction.Ignore)) return false;
                // Check wheel-track support between the fitted poses too. Half-metre
                // spacing is narrower than a tire; do not infer a smooth track
                // from just the three-metre airframe-envelope endpoints.
                for (var sample = 1; sample < 6; sample++)
                {
                    var fraction = sample / 6f;
                    var frame = Quaternion.Slerp(rotation, nextRotation, fraction);
                    var foot = Vector3.Lerp(position, nextPosition, fraction)
                        + frame * (local - Vector3.up * AircraftLayout.WheelRadius);
                    failure = "The first 24 m ahead have uneven or wet wheel tracks. Choose a smooth, dry departure route.";
                    if (!TryGround(foot + Vector3.up * .4f, .8f, out var support)
                        || MathF.Abs(foot.y - support.point.y) > .12f || !DryGround(support.point)) return false;
                }
            }
            position = nextPosition;
            rotation = nextRotation;
        }
        failure = "";
        return true;
    }

    private bool BoardingClear(Vector3 position, Quaternion rotation, out string failure)
    {
        failure = "Both cabin doors are blocked. Leave dry standing room beside at least one door.";
        for (var side = 0; side < 2; side++)
        {
            var candidate = position + rotation * AircraftLayout.Exit(side);
            if (!TryGround(candidate + Vector3.up * 2, 4, out var ground, MinimumExitGroundUp)
                || MathF.Abs(ground.point.y - candidate.y) > 1.5f
                || !ExitFootprintSupported(ground.point)) continue;
            var feet = ground.point + Vector3.up * .1f;
            if (!Physics.CheckCapsule(feet + Vector3.up * .35f, feet + Vector3.up * 1.5f,
                .35f, ~0, QueryTriggerInteraction.Ignore))
            {
                failure = "";
                return true;
            }
        }
        return false;
    }

    private bool TryExit(int seat, out Vector3 position)
    {
        position = default;
        if (_body == null || seat is < 0 or > 2) return false;
        var flight = _body.State;
        if (!ExitSafe(flight, true)) return false;
        if (!flight.Grounded) return TryOverturnedExit(seat, flight, out position);
        var door = AircraftLayout.Exit(seat);
        for (var side = 0; side < 2; side++)
        {
            var local = side == 0 ? door : new Vector3(-door.x, door.y, door.z);
            for (var sample = 0; sample < 6; sample++)
            {
                var offset = new Vector3(MathF.Sign(local.x) * (sample < 3 ? 0 : .6f), 0,
                    sample % 3 == 0 ? 0 : sample % 3 == 1 ? .65f : -.65f);
                var candidate = _body.Root.transform.TransformPoint(local + offset);
                if (!TryGround(candidate + Vector3.up * 2, 4, out var ground, MinimumExitGroundUp)
                    || MathF.Abs(ground.point.y - candidate.y) > 1.5f
                    || !ExitFootprintSupported(ground.point)) continue;
                var feet = ground.point + Vector3.up * .1f;
                // Both endpoints are sphere centres. The capsule's bottom stays above
                // supporting ground rather than intersecting it and rejecting every exit.
                if (!ExitPositionClear(feet)
                    || !ExitPathClear(_body.Root.transform.TransformPoint(AircraftLayout.Seat(seat)
                        + Vector3.down * .8f), feet, ExitPathObstacle)) continue;
                position = feet;
                return true;
            }
        }
        return false;
    }

    private static bool EmergencyExitAvailable(AircraftFlightState flight) =>
        flight.Health < 1 || flight.Fuel <= 0 || !flight.Grounded
            && (!flight.Engine || (flight.Rotation * Vector3.up).y < .35f);

    private bool TryDeparture(int seat, bool emergency, out Vector3 position)
    {
        if (TryExit(seat, out position)) return true;
        return emergency && _body != null && TryEmergencyExit(_body, seat, out position, ExitPathObstacle);
    }

    internal static bool ExitPositionClear(Vector3 feet) => Finite(feet)
        && Physics.OverlapCapsule(feet + Vector3.up * .35f, feet + Vector3.up * 1.5f,
            .35f, ~0, QueryTriggerInteraction.Ignore).Length == 0;

    internal static bool TryEmergencyExit(ClientAircraftBody body, int seat, out Vector3 position,
        Func<Collider, bool> obstacle)
    {
        position = default;
        if (!body.Root || seat is < 0 or > 2) return false;
        var frame = body.Root.transform;
        var flight = body.State;
        if (!Finite(flight.Position) || !ValidRotation(flight.Rotation)) return false;
        var cabin = frame.TransformPoint(AircraftLayout.Hulls[0].Center) - Vector3.up * .7f;
        // The seated foot origin can be below terrain in a belly landing. Start
        // the exterior corridor at cabin height, not inside the supporting ground.
        var start = cabin + Vector3.up * .7f;
        var right = Vector3.ProjectOnPlane(frame.right, Vector3.up);
        if (right.sqrMagnitude < .1f) right = Vector3.Cross(Vector3.up, frame.forward);
        right.Normalize();
        var forward = Vector3.Cross(right, Vector3.up);
        var preferredSide = MathF.Sign(AircraftLayout.Exit(seat).x);
        // Search beside the cabin, then beyond a rolled wing. These are bounded
        // egress positions, not ground teleports: native gravity resumes afterward.
        for (var height = 0; height < 4; height++)
            for (var radius = 0; radius < 5; radius++)
                for (var side = 0; side < 2; side++)
                    for (var along = 0; along < 5; along++)
                    {
                        var distance = radius switch { 0 => 1.8f, 1 => 2.4f, 2 => 3.2f, 3 => 4.5f, _ => 6f };
                        var longitudinal = along == 0 ? 0 : ((along + 1) / 2) * .7f * (along % 2 == 1 ? 1 : -1);
                        var feet = cabin + right * (side == 0 ? preferredSide : -preferredSide) * distance
                            + forward * longitudinal + Vector3.up * (height * .75f);
                        if (!ExitPositionClear(feet) || !ExitPathClear(start, feet, obstacle)) continue;
                        position = feet;
                        return true;
                    }
        return false;
    }

    private bool ExitSafe(AircraftFlightState flight, bool refreshSupport = false)
    {
        if (flight.Grounded)
        {
            _exitRestSince = _nextExitRestCheck = 0;
            return flight.Velocity.sqrMagnitude < 4;
        }
        if (_body == null || !OverturnedExitMotionSafe(flight))
        {
            _exitRestSince = _nextExitRestCheck = 0;
            return false;
        }
        var now = Now;
        if (!ReferenceEquals(_exitRestBody, _body)
            || (flight.Position - _exitRestPosition).sqrMagnitude > .01f
            || Quaternion.Angle(flight.Rotation, _exitRestRotation) > 3)
        {
            _exitRestBody = _body;
            _exitRestPosition = flight.Position;
            _exitRestRotation = flight.Rotation;
            _exitRestSince = _nextExitRestCheck = 0;
        }
        // A hull resting on its belly or wing can leave every tire unloaded.
        // Qualify its actual support separately, regardless of orientation,
        // without changing flight or boarding state.
        if (refreshSupport || now >= _nextExitRestCheck)
        {
            _nextExitRestCheck = now + 100;
            if (!OverturnedHullSupported(flight)) _exitRestSince = 0;
            else if (_exitRestSince == 0) _exitRestSince = now;
        }
        return _exitRestSince != 0 && now - _exitRestSince >= 750;
    }

    private static bool OverturnedExitMotionSafe(AircraftFlightState flight) =>
        Finite(flight.Position) && ValidRotation(flight.Rotation)
        && RecoveryMotionSafe(flight);

    private bool OverturnedHullSupported(AircraftFlightState flight)
    {
        var down = Quaternion.Inverse(flight.Rotation) * Vector3.down;
        foreach (var hull in AircraftLayout.Hulls)
        {
            var half = hull.Size * .5f;
            // The lower face centre covers flat contact; corners cover a hull
            // resting on an edge. Accept only support within 12 cm of the hull.
            var lower = MathF.Abs(down.x) > MathF.Abs(down.y) && MathF.Abs(down.x) > MathF.Abs(down.z)
                ? new Vector3(MathF.Sign(down.x) * half.x, 0, 0)
                : MathF.Abs(down.y) > MathF.Abs(down.z)
                    ? new Vector3(0, MathF.Sign(down.y) * half.y, 0)
                    : new Vector3(0, 0, MathF.Sign(down.z) * half.z);
            for (var sample = 0; sample < 9; sample++)
            {
                var corner = sample - 1;
                var local = sample == 0 ? lower : new Vector3(
                    (corner & 1) == 0 ? -half.x : half.x,
                    (corner & 2) == 0 ? -half.y : half.y,
                    (corner & 4) == 0 ? -half.z : half.z);
                var contact = flight.Position + flight.Rotation * (hull.Center + local);
                if (TryGround(contact + Vector3.up * .15f, .30f, out var ground, .35f)
                    && MathF.Abs(ground.point.y - contact.y) <= .12f) return true;
            }
        }
        return false;
    }

    private bool TryOverturnedExit(int seat, AircraftFlightState flight, out Vector3 position)
    {
        position = default;
        var cabin = flight.Position + flight.Rotation * AircraftLayout.Hulls[0].Center;
        var right = Vector3.ProjectOnPlane(flight.Rotation * Vector3.right, Vector3.up);
        if (right.sqrMagnitude < .1f)
            right = Vector3.Cross(Vector3.up, flight.Rotation * Vector3.forward);
        right.Normalize();
        var forward = Vector3.Cross(right, Vector3.up);
        var preferredSide = MathF.Sign(AircraftLayout.Exit(seat).x);
        for (var side = 0; side < 2; side++)
        {
            for (var sample = 0; sample < 14; sample++)
            {
                var along = sample % 7;
                var longitudinal = along == 0 ? 0 : ((along + 1) / 2) * .7f * (along % 2 == 1 ? 1 : -1);
                var candidate = cabin + right * (side == 0 ? preferredSide : -preferredSide)
                    * (sample < 7 ? 1.8f : 2.4f) + forward * longitudinal;
                if (!TryGround(candidate + Vector3.up * 2.5f, 5, out var ground, MinimumExitGroundUp)
                    || MathF.Abs(ground.point.y - cabin.y) > 2.5f
                    || !ExitFootprintSupported(ground.point)) continue;
                var feet = ground.point + Vector3.up * .1f;
                // A rolled wing can occupy an otherwise dry standing position.
                if (!ExitPositionClear(feet)
                    || !ExitPathClear(cabin - Vector3.up * .7f, feet, ExitPathObstacle)) continue;
                position = feet;
                return true;
            }
        }
        return false;
    }

    private static bool ExitPathClear(Vector3 start, Vector3 feet, Func<Collider, bool> obstacle)
    {
        var travel = feet - start;
        var distance = travel.magnitude;
        if (distance < .01f) return false;
        var bottom = start + Vector3.up * .35f;
        var top = start + Vector3.up * 1.5f;
        // Casts need not report a collider already overlapping their origin.
        foreach (var collider in Physics.OverlapCapsule(bottom, top, .35f, ~0, QueryTriggerInteraction.Ignore))
            if (obstacle(collider)) return false;
        var hits = Physics.CapsuleCastAll(bottom, top,
            .35f, travel / distance, distance, ~0, QueryTriggerInteraction.Ignore);
        foreach (var hit in hits)
            if (obstacle(hit.collider)) return false;
        return true;
    }

    private bool ExitPathObstacle(Collider collider)
    {
        if (collider == null || !collider || collider.transform.IsChildOf(_body!.Root.transform)) return false;
        // Remote occupants retain native colliders. Their exact current seat
        // bindings may overlap the cabin corridor, but never ignore outsiders.
        var entity = collider.GetComponentInParent<BoltEntity>();
        if (entity != null && entity && entity.isAttached)
            for (var seat = 0; seat < _seats.Length; seat++)
            {
                var peer = _seats[seat];
                if (peer != null && peer.Seat == seat && peer.Lease != Guid.Empty
                    && TryActor(peer, out _, out _, out var actor) && actor != 0
                    && actor == entity.networkId.PackedValue) return false;
            }
        return true;
    }

    private bool ExitFootprintSupported(Vector3 center)
    {
        if (!DryGround(center)) return false;
        for (var sample = 0; sample < 4; sample++)
        {
            var offset = sample switch
            {
                0 => Vector3.right * .22f,
                1 => Vector3.left * .22f,
                2 => Vector3.forward * .22f,
                _ => Vector3.back * .22f
            };
            if (!TryGround(center + offset + Vector3.up * .5f, 1, out var support, MinimumExitGroundUp)
                || MathF.Abs(support.point.y - center.y) > .20f
                || !DryGround(support.point)) return false;
        }
        return true;
    }

    private bool TryGround(Vector3 origin, float distance, out RaycastHit ground,
        float minimumGroundUp = MinimumGroundUp)
    {
        ground = default;
        // Exit rays may start inside the cabin; select the nearest real support
        // beneath them while ignoring this aircraft's own colliders.
        var hits = Physics.RaycastAll(origin, Vector3.down, distance, ~0, QueryTriggerInteraction.Ignore);
        var nearest = float.PositiveInfinity;
        foreach (var hit in hits)
        {
            var collider = hit.collider;
            if (collider == null || !collider || !Finite(hit.point)) continue;
            if (_body != null && collider.transform.IsChildOf(_body.Root.transform)) continue;
            if (hit.distance >= nearest) continue;
            nearest = hit.distance;
            ground = hit;
        }
        if (!float.IsFinite(nearest) || ground.normal.y <= minimumGroundUp) return false;
        var support = ground.collider;
        if (support.attachedRigidbody != null) return false;
        var terrain = support.GetComponent("TerrainCollider");
        if (terrain != null && terrain && terrain.Pointer == support.Pointer) return true;
        // These are the same static mesh surfaces used by the drone survey.
        var mesh = support.GetComponent<MeshCollider>();
        if (mesh != null && mesh && mesh.Pointer == support.Pointer && support.gameObject.isStatic) return true;
        // Construction elements need not carry Unity's static flag. Their actual
        // upward surface and footprint support are still checked above.
        for (var node = support.transform; node != null && node; node = node.parent)
        {
            var element = node.GetComponent("StructureElement");
            if (element != null && element) return true;
        }
        return false;
    }

    private static bool DryGround(Vector3 ground)
    {
        var waterLayer = LayerMask.NameToLayer("Water");
        if (waterLayer < 0) return false;
        // Include triggers: native lakes and the ocean use water volumes.
        return !Physics.CheckBox(ground + Vector3.up * .05f, new Vector3(.3f, .05f, .3f),
            Quaternion.identity, 1 << waterLayer, QueryTriggerInteraction.Collide);
    }
}
