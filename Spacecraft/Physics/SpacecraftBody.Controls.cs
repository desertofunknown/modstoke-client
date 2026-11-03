// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftBody
{
    private Il2CppStructArray<RaycastHit>? _controlHits;

    internal bool CanSeeControl(Vector3 origin, Vector3 contact, int seat, Transform? actor, Rigidbody? actorBody,
        BoxCollider? housing = null)
    {
        if (!Ready || !Finite(origin) || !Finite(contact) || seat is < -1 or > 1) return false;
        var delta = contact - origin;
        var length = delta.magnitude;
        if (length > 3) return false;
        if (length <= .01f) return true;
        var direction = delta / length;
        var end = contact - direction * .01f;
        // Read current authored poses: the late-frame seat/camera can be ahead of
        // PhysX's last synchronized transform on a moving or interpolated ship.
        foreach (var solid in _solids)
        {
            if (!solid) return false;
            if (IsSeatCollider(solid, seat) || !solid.enabled || !solid.gameObject.activeInHierarchy) continue;
            if (ControlSegmentHits(solid, origin, end)) return false;
        }
        foreach (var floor in _walkSurfaces)
        {
            if (!floor) return false;
            if (floor.enabled && floor.gameObject.activeInHierarchy && ControlSegmentHits(floor, origin, end)) return false;
        }
        if (_evaRackSolid != null && _evaRackSolid && _evaRackSolid.enabled && _evaRackSolid.gameObject.activeInHierarchy
            && ControlSegmentHits(_evaRackSolid, origin, end)) return false;
        if (_toolRackSolid != null && _toolRackSolid && _toolRackSolid.enabled && _toolRackSolid.gameObject.activeInHierarchy
            && (housing == null || housing.Pointer != _toolRackSolid.Pointer)
            && ControlSegmentHits(_toolRackSolid, origin, end)) return false;

        var hits = _controlHits ??= new Il2CppStructArray<RaycastHit>(size: 32);
        var count = Physics.RaycastNonAlloc(origin, direction, hits, length - .01f, ~0, QueryTriggerInteraction.Ignore);
        if (count == hits.Length) return false;
        for (var i = 0; i < count; i++)
        {
            var collider = hits[i].collider;
            if (!collider || actor != null && actor && collider.transform.IsChildOf(actor)
                || actorBody != null && actorBody && collider.attachedRigidbody == actorBody) continue;
            if (AuthoredControlCollider(collider)) continue;
            return false;
        }
        return true;
    }

    private static bool ControlSegmentHits(BoxCollider box, Vector3 origin, Vector3 end)
    {
        var frame = box.transform;
        var start = frame.InverseTransformPoint(origin);
        var delta = frame.InverseTransformPoint(end) - start;
        var bounds = new Bounds(box.center, box.size);
        if (bounds.Contains(start)) return true;
        var length = delta.magnitude;
        return length > .00001f && bounds.IntersectRay(new Ray(start, delta / length), out var distance)
            && distance >= 0 && distance <= length;
    }

    private bool AuthoredControlCollider(Collider collider)
    {
        foreach (var solid in _solids)
            if (solid && solid.Pointer == collider.Pointer) return true;
        foreach (var floor in _walkSurfaces)
            if (floor && floor.Pointer == collider.Pointer) return true;
        return _evaRackSolid != null && _evaRackSolid && _evaRackSolid.Pointer == collider.Pointer
            || _toolRackSolid != null && _toolRackSolid && _toolRackSolid.Pointer == collider.Pointer;
    }
}
