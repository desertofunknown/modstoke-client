// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class OrbitalStationModel
{
    internal int ZoneAt(Vector3 worldChest)
    {
        if (!Visible || !Finite(worldChest)) return -1;
        var point = Root.transform.InverseTransformPoint(worldChest);
        if (!Finite(point) || point.y is <= 0 or >= 3.4f) return -1;
        if (MathF.Abs(point.x) <= 5.72f && MathF.Abs(point.z) <= 5.72f
            || MathF.Abs(point.x) <= 1.3f && MathF.Abs(point.z) <= 6
            || MathF.Abs(point.z) <= 1.3f && MathF.Abs(point.x) <= 6) return 0;
        for (var i = 0; i < 4; i++)
        {
            var arm = i switch
            {
                0 => point,
                1 => new Vector3(-point.z, point.y, point.x),
                2 => new Vector3(-point.x, point.y, -point.z),
                _ => new Vector3(point.z, point.y, -point.x)
            };
            if (MathF.Abs(arm.x) <= 1.3f && arm.z is > 6 and <= 12) return i + 1;
            if (MathF.Abs(arm.x) <= 4.72f && arm.z is > 12.28f and < 25.72f
                || MathF.Abs(arm.x) <= 1.3f && arm.z is > 12 and <= 26) return i + 5;
        }
        return MathF.Abs(point.x) <= 1.3f && point.z is > 26 and <= 32 ? 9 : -1;
    }

    internal bool TryWalkSurface(Vector3 worldFeet, out Vector3 surface)
    {
        surface = default;
        if (!Finite(worldFeet) || !GeometryLive()) return false;
        var up = Root.transform.up;
        var ray = new Ray(worldFeet + up * .7f, -up);
        var nearest = 2.2f;
        BoxCollider? support = null;
        var point = Vector3.zero;
        var normal = Vector3.zero;
        foreach (var solid in _solids)
        {
            // Collider.Raycast ignores a shape containing its origin. Treat that
            // case as blocked rather than seeing through a closed leaf or wall.
            var origin = solid.transform.InverseTransformPoint(ray.origin) - solid.center;
            var half = solid.size * .5f;
            if (MathF.Abs(origin.x) < half.x && MathF.Abs(origin.y) < half.y && MathF.Abs(origin.z) < half.z)
                return false;
            if (!solid.Raycast(ray, out var hit, nearest)) continue;
            nearest = hit.distance;
            support = solid;
            point = hit.point;
            normal = hit.normal;
        }
        if (support == null || Vector3.Dot(normal, up) <= .7f) return false;
        foreach (var floor in _walkSurfaces)
        {
            if (floor.Pointer != support.Pointer) continue;
            surface = point;
            return true;
        }
        return false;
    }

    internal bool CrewPathClear(Vector3 worldBottom, Vector3 worldTop, Vector3 worldTravel)
    {
        if (!Finite(worldBottom) || !Finite(worldTop) || !Finite(worldTravel) || !GeometryLive()) return false;
        foreach (var solid in _solids)
            if (!ClientSpacecraftBody.CrewBoxClear(solid, worldBottom, worldTop, worldTravel)) return false;
        return true;
    }

    internal bool ContainsWalkArea(Vector3 worldFeet)
    {
        if (!TryWalkSurface(worldFeet, out var surface)) return false;
        var frame = Root.transform;
        var height = Vector3.Dot(worldFeet - surface, frame.up);
        if (height is < -.05f or > .7f) return false;
        if (ZoneAt(worldFeet + frame.up * .9f) >= 0) return true;
        var point = frame.InverseTransformPoint(worldFeet);
        return MathF.Abs(point.x) <= 13 && point.z is >= 32 and <= 68;
    }

    internal Transform DoorFrame(int index)
    {
        if ((uint)index >= DoorCount) throw new ArgumentOutOfRangeException(nameof(index));
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _doorFrames[index];
    }

    internal float DoorAmount(int index)
    {
        if ((uint)index >= DoorCount) throw new ArgumentOutOfRangeException(nameof(index));
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _doorAmounts[index];
    }

    internal Vector3 DoorControlPoint(int index, bool back) =>
        DoorFrame(index).TransformPoint(new Vector3(1.245f, 1.4f, back ? -.32f : .32f));

    internal bool DoorObstructed(int index, ReadOnlySpan<Vector3> worldFeet)
    {
        var frame = DoorFrame(index);
        if (!GeometryLive() || !frame) return true;
        foreach (var feet in worldFeet)
        {
            if (!Finite(feet)) return true;
            var point = frame.InverseTransformPoint(feet);
            if (!Finite(point)) return true;
            // Include both leaves' full closing sweep, from their pockets to
            // the centre seam. Capsule feet use the actual deck plane at y=0.
            var x = Mathf.Max(0, MathF.Abs(point.x) - 2.7f);
            var z = Mathf.Max(0, MathF.Abs(point.z) - .06f);
            var low = point.y + ClientSpacecraftBody.CrewCapsuleBottom;
            var high = point.y + ClientSpacecraftBody.CrewCapsuleTop;
            var y = Mathf.Max(0, Mathf.Max(low - 2.8f, -high));
            if (x * x + y * y + z * z <= ClientSpacecraftBody.CrewCapsuleRadius * ClientSpacecraftBody.CrewCapsuleRadius)
                return true;
        }
        return false;
    }

    private bool GeometryLive()
    {
        if (!Visible || _disposed || _solids.Count == 0 || _walkSurfaces.Count == 0) return false;
        foreach (var solid in _solids)
            if (!solid || !solid.enabled || !solid.gameObject.activeInHierarchy) return false;
        return true;
    }

    private static bool Finite(Vector3 point) =>
        float.IsFinite(point.x) && float.IsFinite(point.y) && float.IsFinite(point.z);
}
