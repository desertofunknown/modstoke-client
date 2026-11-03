// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftService
{
    private Il2CppStructArray<RaycastHit>? _placementGroundHits;
    private Il2CppReferenceArray<Collider>? _placementStandingOverlaps;

    private bool TryPlacement(Vector3 actorPosition, Quaternion actorRotation,
        out Vector3 position, out Quaternion rotation, out string failure)
    {
        position = default; rotation = Quaternion.identity;
        failure = "Face a large dry clearing before deploying the spacecraft.";
        if (!Finite(actorPosition) || !ValidRotation(actorRotation)) return false;
        var forward = Vector3.ProjectOnPlane(actorRotation * Vector3.forward, Vector3.up);
        if (forward.sqrMagnitude < .1f) return false;
        forward.Normalize(); rotation = Quaternion.LookRotation(forward, Vector3.up);
        var center = actorPosition + forward * 26;
        if (!TryGround(center + Vector3.up * 8, 16, out var middle)
            || MathF.Abs(middle.point.y - actorPosition.y) > 6) return false;
        var highest = middle.point.y;
        var lowest = highest;
        // The native hull is almost 20 by 29 metres: validate its full footprint.
        for (var x = -1; x <= 1; x++)
        for (var z = -1; z <= 1; z++)
        {
            var sample = center + rotation * new Vector3(x * 9.8f, 0, -2.5f + z * 14.5f);
            if (!TryGround(sample + Vector3.up * 8, 16, out var ground) || !DryGround(ground.point)) return false;
            highest = MathF.Max(highest, ground.point.y); lowest = MathF.Min(lowest, ground.point.y);
        }
        failure = "The spacecraft needs a level clearing at least 20 by 30 metres wide.";
        if (highest - lowest > .7f) return false;
        position = new Vector3(center.x, highest - SpacecraftLayout.GroundY + .15f, center.z);
        failure = "The spacecraft hull would overlap an obstacle. Choose an open clearing.";
        if (Physics.CheckBox(position + rotation * SpacecraftLayout.BoundsCenter,
            SpacecraftLayout.BoundsSize * .5f + new Vector3(.2f, 0, .2f), rotation, ~0, QueryTriggerInteraction.Ignore)) return false;
        failure = "The rear boarding ramp needs clear supported ground.";
        var entry = position + rotation * SpacecraftLayout.Entry;
        if (!TryGround(entry + Vector3.up * 3, 7, out var rampGround) || !DryGround(rampGround.point)
            || MathF.Abs(rampGround.point.y - highest) > 1 || !ClearStanding(rampGround.point + Vector3.up * .12f)) return false;
        failure = ""; return true;
    }

    private bool TryIslandReturnAnchor(out Vector3 position, out Quaternion rotation)
    {
        position = default; rotation = Quaternion.identity;
        if (_body?.Ready != true || _body.State.Region != SpacecraftRegion.Island || !Stationary(_body.State)) return false;
        rotation = Quaternion.LookRotation(Vector3.ProjectOnPlane(_body.Root.transform.forward, Vector3.up), Vector3.up);
        // Keep a grounded escape point behind the ramp, independently of where
        // the player stands while choosing a chair. No player relocation occurs.
        for (var row = 0; row < 3; row++)
        for (var side = 0; side < 3; side++)
        {
            var offset = new Vector3(side == 1 ? -1.25f : side == 2 ? 1.25f : 0, 0, -3.7f - row * 1.5f);
            var entry = _body.Root.transform.TransformPoint(SpacecraftLayout.Entry + offset);
            if (!TryGround(entry + Vector3.up * 3, 8, out var hit) || hit.point.y >= 2000
                || !DryGround(hit.point)) continue;
            var feet = hit.point + Vector3.up * .12f;
            if (_body.ContainsCabin(feet) || !ClearStanding(feet, false)) continue;
            position = feet;
            return true;
        }
        return false;
    }

    private bool ClearStanding(Vector3 feet, bool ignoreSpacecraft = true)
    {
        var colliders = _placementStandingOverlaps ??= new Il2CppReferenceArray<Collider>(size: 64);
        try
        {
            var count = Physics.OverlapCapsuleNonAlloc(feet + Vector3.up * .35f, feet + Vector3.up * 1.5f,
                .35f, colliders, ~0, QueryTriggerInteraction.Ignore);
            if (count == colliders.Length)
            {
                colliders = Physics.OverlapCapsule(feet + Vector3.up * .35f, feet + Vector3.up * 1.5f,
                    .35f, ~0, QueryTriggerInteraction.Ignore);
                count = colliders.Length;
            }
            for (var i = 0; i < count; i++)
            {
                var collider = colliders[i];
                if (collider == null || !collider) continue;
                // The ramp is intentionally walkable; actual ground support is still required.
                if (ignoreSpacecraft && _body != null && collider.transform.IsChildOf(_body.Root.transform)) continue;
                return false;
            }
            return true;
        }
        finally
        {
            for (var i = 0; i < _placementStandingOverlaps.Length; i++) _placementStandingOverlaps[i] = null!;
        }
    }

    private bool TryGround(Vector3 origin, float distance, out RaycastHit ground)
    {
        ground = default;
        var nearest = float.PositiveInfinity;
        var hits = _placementGroundHits ??= new Il2CppStructArray<RaycastHit>(size: 32);
        var hitCount = Physics.RaycastNonAlloc(origin, Vector3.down, hits, distance, ~0, QueryTriggerInteraction.Ignore);
        if (hitCount == hits.Length)
        {
            // The closest hit must also block placement when it is unsuitable support.
            hits = Physics.RaycastAll(origin, Vector3.down, distance, ~0, QueryTriggerInteraction.Ignore);
            hitCount = hits.Length;
        }
        for (var h = 0; h < hitCount; h++)
        {
            var hit = hits[h];
            if (hit.collider == null || !hit.collider || !float.IsFinite(hit.distance)
                || hit.distance < 0 || hit.distance >= nearest) continue;
            if (_body != null && hit.collider.transform.IsChildOf(_body.Root.transform)) continue;
            nearest = hit.distance; ground = hit;
        }
        if (!float.IsFinite(nearest) || ground.normal.y < .97f || ground.collider.attachedRigidbody != null) return false;
        var support = ground.collider;
        var terrain = support.GetComponent("TerrainCollider");
        if (terrain != null && terrain && terrain.Pointer == support.Pointer) return true;
        var mesh = support.GetComponent<MeshCollider>();
        return mesh != null && mesh && mesh.Pointer == support.Pointer && support.gameObject.isStatic;
    }

    private static bool DryGround(Vector3 point)
    {
        var water = LayerMask.NameToLayer("Water");
        return water >= 0 && !Physics.CheckBox(point + Vector3.up * .05f, new Vector3(.3f, .05f, .3f),
            Quaternion.identity, 1 << water, QueryTriggerInteraction.Collide);
    }
}
