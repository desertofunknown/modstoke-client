// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftBody
{
    internal const float CrewSpeed = 3, CrewRadius = 40;
    internal const float CrewCapsuleRadius = .32f, CrewCapsuleBottom = .35f, CrewCapsuleTop = 1.5f;
    internal const float CrewClearance = .001f;

    internal static bool StoppedInSpace(SpacecraftFlightState state) => state.Region == SpacecraftRegion.Space
        && state.Travel == SpacecraftTravel.None && state.Velocity.sqrMagnitude <= .25f
        && state.AngularVelocity.sqrMagnitude <= .0025f;

    // The retained berth authorizes a bounded excursion, not unrestricted flight.
    // Both the local mover and host evidence use the same hatch transitions.
    internal bool CrewStep(Vector3 from, Vector3 to, bool outside, out bool nextOutside)
    {
        nextOutside = outside;
        if (!TelemetryCurrent || State.Region != SpacecraftRegion.Space || State.Travel != SpacecraftTravel.None
            || !Finite(from) || !Finite(to)) return false;
        var interior = InsideHull(to + Vector3.up * .9f);
        if (State.Docked)
        {
            if (!DockedStationCurrent && (outside || !interior)) return false;
            var frame = Root.transform;
            if (!ContainsCrewWalkArea(frame.TransformPoint(from))
                || !ContainsCrewWalkArea(frame.TransformPoint(to))) return false;
        }
        else if (to.sqrMagnitude > CrewRadius * CrewRadius) return false;
        if (outside == interior)
        {
            if (State.DoorAmount < .99f || !CrossesPlane(from, to, EntryFloorAft.z, out var crossing)
                || crossing.x is < -1.05f or > .95f || crossing.y is < -2.76f or > -.7f) return false;
            nextOutside = !interior;
        }
        if (!nextOutside && !CrewCapsuleInCabin(to, State.DoorAmount >= .99f)) return false;
        if (!outside || !nextOutside)
        {
            if (CrossesPlane(from, to, -9.1f, out var inner)
                && (_model!.InnerDoorAmount < .8f || inner.x is < -.7f or > .5f)) return false;
            if (CrossesPlane(from, to, -4.84f, out var cabin)
                && (CabinDoorAmount < .8f || cabin.x is < -.7f or > .5f)) return false;
        }
        else if (InsideHull((from + to) * .5f + Vector3.up * .9f)) return false;
        return CrewPathClear(from, to);
    }

    internal bool CrewCabinStep(Vector3 from, Vector3 to) => Ready && Finite(from) && Finite(to)
        && CrewCapsuleInCabin(to, false) && CrewPathClear(from, to);

    internal bool CrewCapsuleClear(Vector3 feet) => CrewPathClear(feet, feet);

    internal bool CrewPathClear(Vector3 from, Vector3 to) => CrewPathClear(from, to, -1);

    internal bool CrewSeatPathClear(Vector3 from, Vector3 to, int seat)
    {
        if (!Ready || seat < 0 || seat >= _chairs.Length || !Finite(from) || !Finite(to)) return false;
        var origin = SpacecraftLayout.Seat(seat) + SeatAdjustmentOffset(seat);
        if (new Vector2(from.x - origin.x, from.z - origin.z).sqrMagnitude > 3.24f
            || new Vector2(to.x - origin.x, to.z - origin.z).sqrMagnitude > 3.24f
            || MathF.Abs(from.y - origin.y) > 1.65f || MathF.Abs(to.y - origin.y) > 1.65f) return false;
        return CrewPathClear(CrewSeatFeet(from), CrewSeatFeet(to), seat);
    }

    // The seated animation puts its actor pivot below the deck. During a berth
    // transfer the clearance envelope rests on the deck and exempts its chair assembly.
    internal static Vector3 CrewSeatFeet(Vector3 feet) => new(feet.x, Mathf.Max(feet.y, -1.16f), feet.z);

    internal bool IsSeatCollider(Collider collider, int seat) => seat >= 0 && seat < _chairs.Length
        && collider && _chairs[seat] && collider.Pointer == _chairs[seat].Pointer;

    internal bool IsSeatTransitionCollider(Collider collider, int seat) => IsSeatCollider(collider, seat)
        || _model?.IsSeatConsoleCollider(collider, seat) == true;

    internal bool CrewCapsuleInCabin(Vector3 feet, bool openRear)
    {
        // Bounding each hemisphere also accounts for the tapered canopy and
        // narrow corridor, where a centre point alone can fit outside the walls.
        for (var end = 0; end < 2; end++)
        {
            var center = feet + Vector3.up * (end == 0 ? CrewCapsuleBottom : CrewCapsuleTop);
            for (var corner = 0; corner < 8; corner++)
            {
                var point = center + new Vector3((corner & 1) == 0 ? -CrewCapsuleRadius : CrewCapsuleRadius,
                    (corner & 2) == 0 ? -CrewCapsuleRadius : CrewCapsuleRadius,
                    (corner & 4) == 0 ? -CrewCapsuleRadius : CrewCapsuleRadius);
                if (openRear && point.z < EntryFloorAft.z && point.z >= EntryFloorAft.z - CrewCapsuleRadius * 2
                    && point.x is >= -1.37f and <= 1.27f && point.y is >= -2.8f and <= 1.15f) continue;
                if (!InsideHull(point)) return false;
            }
        }
        return true;
    }

    private bool CrewPathClear(Vector3 from, Vector3 to, int seat)
    {
        if (!Ready || !Finite(from) || !Finite(to)) return false;
        var frame = Root.transform;
        var bottom = frame.TransformPoint(from + Vector3.up * CrewCapsuleBottom);
        var top = frame.TransformPoint(from + Vector3.up * CrewCapsuleTop);
        var travel = frame.TransformVector(to - from);
        foreach (var solid in _solids)
        {
            if (!solid) return false;
            if (!solid.enabled || !solid.gameObject.activeInHierarchy || IsSeatTransitionCollider(solid, seat)) continue;
            if (!CrewBoxClear(solid, bottom, top, travel)) return false;
        }
        foreach (var floor in _walkSurfaces)
        {
            if (!floor) return false;
            if (floor.enabled && floor.gameObject.activeInHierarchy && !CrewBoxClear(floor, bottom, top, travel)) return false;
        }
        if (_evaRackSolid != null && (!_evaRackSolid || !_evaRackSolid.enabled
            || !CrewBoxClear(_evaRackSolid, bottom, top, travel))) return false;
        if (_toolRackSolid != null && (!_toolRackSolid || !_toolRackSolid.enabled
            || !CrewBoxClear(_toolRackSolid, bottom, top, travel))) return false;
        if (_extinguisherRackSolid != null && (!_extinguisherRackSolid || !_extinguisherRackSolid.enabled
            || !CrewBoxClear(_extinguisherRackSolid, bottom, top, travel))) return false;
        return !DockedStationCurrent || _dockedStation!.CrewPathClear(bottom, top, travel);
    }

    private BoxCollider? _evaRackSolid;
    internal void SetEvaRackSolid(BoxCollider? solid) => _evaRackSolid = solid;
    private BoxCollider? _toolRackSolid;
    internal void SetToolRackSolid(BoxCollider? solid) => _toolRackSolid = solid;
    private BoxCollider? _extinguisherRackSolid;
    internal void SetExtinguisherRackSolid(BoxCollider? solid) => _extinguisherRackSolid = solid;

    internal static bool CrewBoxClear(BoxCollider box, Vector3 bottom, Vector3 top, Vector3 travel)
    {
        // Query authored transforms directly so remote IgnoreCollision pairs and
        // deferred native transform synchronization cannot hide an obstacle.
        // Work in the box's orthonormal frame; the ramp plates carry their size
        // in transform scale while the hull and door boxes use collider size.
        var frame = box.transform;
        var inverse = Quaternion.Inverse(frame.rotation);
        var center = frame.TransformPoint(box.center);
        var low = inverse * (bottom - center);
        var high = inverse * (top - center);
        var delta = inverse * travel;
        var scale = frame.lossyScale;
        var half = Vector3.Scale(box.size * .5f, new Vector3(MathF.Abs(scale.x), MathF.Abs(scale.y), MathF.Abs(scale.z)));
        return CrewBoxClear(low, high, delta, half);
    }

    internal static bool CrewBoxClear(Vector3 low, Vector3 high, Vector3 delta, Vector3 half)
    {
        if (!Finite(low) || !Finite(high) || !Finite(delta) || !Finite(half)) return false;
        var radius = Vector3.one * (CrewCapsuleRadius + CrewClearance);
        var min = Vector3.Min(Vector3.Min(low, high), Vector3.Min(low + delta, high + delta)) - radius;
        var max = Vector3.Max(Vector3.Max(low, high), Vector3.Max(low + delta, high + delta)) + radius;
        if (min.x > half.x || max.x < -half.x || min.y > half.y || max.y < -half.y
            || min.z > half.z || max.z < -half.z) return true;
        var at = 0f;
        for (var iteration = 0; iteration < 16; iteration++)
        {
            var separation = CrewBoxSeparation(low + delta * at, high + delta * at, half);
            var distance = separation.magnitude;
            var clearance = distance - CrewCapsuleRadius;
            if (!float.IsFinite(clearance) || clearance <= 0) return false;
            // Distance to a convex box is convex along this translation. Its
            // supporting plane gives a conservative advance without thin-wall
            // sampling gaps, and permits motion parallel to ordinary floor contact.
            var closing = -Vector3.Dot(separation / distance, delta);
            if (closing <= 0) return true;
            // Allow retreat from an existing non-overlapping margin violation,
            // but never accept an approaching step that enters that margin.
            clearance -= CrewClearance;
            if (clearance <= 0) return false;
            if (clearance > closing * (1 - at)) return true;
            at += clearance / closing;
        }
        return false;
    }

    private static Vector3 CrewBoxSeparation(Vector3 from, Vector3 to, Vector3 half)
    {
        var direction = to - from;
        var best = Vector3.zero;
        var distance = float.PositiveInfinity;
        var start = 0f;
        // The segment crosses at most six box planes. Within each interval its
        // squared distance is a quadratic, whose minimum is evaluated exactly.
        for (var interval = 0; interval < 7; interval++)
        {
            var end = 1f;
            for (var axis = 0; axis < 3; axis++)
            {
                if (MathF.Abs(direction[axis]) < .0000001f) continue;
                var near = (-half[axis] - from[axis]) / direction[axis];
                var far = (half[axis] - from[axis]) / direction[axis];
                if (near > start && near < end) end = near;
                if (far > start && far < end) end = far;
            }
            var middle = from + direction * ((start + end) * .5f);
            var squared = 0f;
            var product = 0f;
            for (var axis = 0; axis < 3; axis++)
            {
                var edge = middle[axis] < -half[axis] ? -half[axis]
                    : middle[axis] > half[axis] ? half[axis] : float.NaN;
                if (float.IsNaN(edge)) continue;
                squared += direction[axis] * direction[axis];
                product += (from[axis] - edge) * direction[axis];
            }
            var at = squared > 0 ? Mathf.Clamp(-product / squared, start, end) : start;
            var point = from + direction * at;
            var separation = point - new Vector3(Mathf.Clamp(point.x, -half.x, half.x),
                Mathf.Clamp(point.y, -half.y, half.y), Mathf.Clamp(point.z, -half.z, half.z));
            if (separation.sqrMagnitude < distance) { best = separation; distance = separation.sqrMagnitude; }
            if (end >= 1 || distance == 0) break;
            start = end;
        }
        return best;
    }

    private static bool CrossesPlane(Vector3 from, Vector3 to, float z, out Vector3 crossing)
    {
        crossing = default;
        if ((from.z < z) == (to.z < z) || Mathf.Abs(to.z - from.z) < .00001f) return false;
        crossing = Vector3.Lerp(from, to, (z - from.z) / (to.z - from.z));
        return true;
    }

    internal Vector3 CrewAirflow(Vector3 localFeet)
    {
        if (!TelemetryCurrent || !Finite(localFeet)) return Vector3.zero;
        var state = State;
        if (state.DoorAmount <= .01f || localFeet.z is < -18 or > 4
            || Mathf.Abs(localFeet.x) > 3.5f || localFeet.y is < -3 or > 1) return Vector3.zero;
        var rate = ClientSpacecraftSystems.Outflow(state);
        if (rate <= 0) return Vector3.zero;
        if (localFeet.z > -9.1f)
        {
            if (InnerDoorClosed) return Vector3.zero;
            rate *= Mathf.SmoothStep(0, 1, _model!.InnerDoorAmount);
            if (localFeet.z > -4.84f) rate *= Mathf.SmoothStep(0, 1, CabinDoorAmount);
        }
        var strength = Mathf.Clamp01(rate / 100) * 7
            / (1 + Mathf.Max(0, localFeet.z + 13) * .18f);
        return new Vector3(-Mathf.Clamp(localFeet.x, -1, 1) * .2f, 0, -1).normalized * strength;
    }
}
