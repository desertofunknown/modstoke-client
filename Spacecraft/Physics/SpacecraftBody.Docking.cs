// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftBody
{
    private bool _docked;
    private Vector3 _dockVelocity;
    private Vector3 _dockAngularVelocity;
    private Il2CppReferenceArray<Collider>? _dockOverlaps;

    internal bool TryDock(Vector3 position, Quaternion rotation, Vector3 velocity, Vector3 angularVelocity)
    {
        if (ServiceSuspended || !Ready || !_authoritative || _docked || _region != SpacecraftRegion.Space
            || _travel != SpacecraftTravel.None || !Finite(position) || !Valid(rotation)
            || !Finite(velocity) || !Finite(angularVelocity)) return false;
        if (!DockPoseClear(position, rotation.normalized)) return false;
        ReleaseParking();
        _body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        _body.isKinematic = true;
        _body.useGravity = false;
        _docked = true;
        _powered = _grounded = false;
        _dampeners = true;
        _translation = _rotationInput = Vector3.zero;
        _boostActive = false;
        SetDockPose(position, rotation, velocity, angularVelocity, true);
        return true;
    }

    private bool DockPoseClear(Vector3 position, Quaternion rotation)
    {
        var hits = _dockOverlaps ??= new Il2CppReferenceArray<Collider>(size: 64);
        var delta = rotation * Quaternion.Inverse(Root.transform.rotation);
        try
        {
            foreach (var solid in _solids) if (!Clear(solid)) return false;
            foreach (var floor in _walkSurfaces) if (!Clear(floor)) return false;
            return true;
        }
        finally
        {
            for (var i = 0; i < hits.Length; i++) hits[i] = null!;
        }

        bool Clear(BoxCollider part)
        {
            if (!part || !part.enabled || !part.gameObject.activeInHierarchy || part.isTrigger) return true;
            var partFrame = part.transform;
            var center = position + rotation * Root.transform.InverseTransformPoint(partFrame.TransformPoint(part.center));
            var size = Vector3.Scale(part.size, partFrame.lossyScale) * .5f;
            var count = Physics.OverlapBoxNonAlloc(center, size, hits, delta * partFrame.rotation, ~0,
                QueryTriggerInteraction.Ignore);
            if (count == hits.Length) return false;
            for (var i = 0; i < count; i++)
            {
                var other = hits[i];
                if (!other || other.transform.IsChildOf(Root.transform) || Physics.GetIgnoreCollision(part, other)) continue;
                return false;
            }
            return true;
        }
    }

    // The station and latched craft must receive poses from the same orbital step.
    // Velocities remain in the current native frame for telemetry and release.
    internal void SetDockPose(Vector3 position, Quaternion rotation, Vector3 velocity,
        Vector3 angularVelocity, bool teleport)
    {
        if (!Finite(position) || !Valid(rotation) || !Finite(velocity) || !Finite(angularVelocity))
            throw new ArgumentException("The spacecraft dock pose or velocity is invalid.");
        if (ServiceSuspended || !Ready || !_docked) return;
        rotation = rotation.normalized;
        if (teleport)
        {
            var interpolation = _body.interpolation;
            _body.interpolation = RigidbodyInterpolation.None;
            try
            {
                _body.position = position;
                _body.rotation = rotation;
                Root.transform.SetPositionAndRotation(position, rotation);
            }
            finally
            {
                _body.interpolation = interpolation;
            }
        }
        else
        {
            _body.MovePosition(position);
            _body.MoveRotation(rotation);
        }
        _dockVelocity = velocity;
        _dockAngularVelocity = angularVelocity;
        if (!_authoritative)
            _replica = _replica with
            {
                Position = position, Rotation = rotation, Velocity = velocity, AngularVelocity = angularVelocity
            };
    }

    internal bool Undock()
    {
        if (ServiceSuspended || !Ready || !_authoritative || !_docked) return false;
        _body.isKinematic = false;
        _body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        _body.useGravity = false;
        _body.velocity = _dockVelocity;
        _body.angularVelocity = _dockAngularVelocity;
        _docked = false;
        _dockVelocity = _dockAngularVelocity = Vector3.zero;
        return true;
    }
}
