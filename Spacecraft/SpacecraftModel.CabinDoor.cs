// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Collections.Generic;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftModel
{
    private readonly Transform[] _cabinDoorFrames = new Transform[2];
    private readonly Transform[] _cabinDoorLeaves = new Transform[2];
    private readonly Vector3[] _cabinDoorClosedPositions = new Vector3[2];
    private Vector3 _cabinDoorAxis;
    private float _cabinDoorAmount;

    internal bool CabinDoorClosed => _cabinDoorAmount <= .01f;
    internal float CabinDoorAmount => _cabinDoorAmount;

    private void BindCabinDoor()
    {
        const string prefix = InteriorKey + "/CabinEntry/SpaceshipDoorA02";
        var hatch = _parts[prefix];
        var seam = _root!.transform.InverseTransformPoint(hatch.position);
        _cabinDoorAxis = _root.transform.InverseTransformVector(hatch.TransformVector(Vector3.forward)).normalized;
        for (var i = 0; i < _cabinDoorFrames.Length; i++)
        {
            var leaf = _parts[prefix + "/SpaceshipDoorAMesh." + (i == 0 ? "002" : "003")];
            // A02's right leaf is tilted in the native damaged pose. Both
            // leaves close square to their native parent, preserving its scale.
            leaf.localRotation = Quaternion.identity;
            _cabinDoorLeaves[i] = leaf;
            // Mesh-local Z follows the rail. Use the straight mating edges,
            // not the latch projections included in the mesh bounds.
            var edge = _root.transform.InverseTransformPoint(
                leaf.TransformPoint(new Vector3(0, 0, i == 0 ? -.0054f : .014f)));
            var frame = new GameObject("Cabin hatch leaf " + i) { layer = _root.layer }.transform;
            frame.SetParent(_root.transform, false);
            leaf.SetParent(frame, true);
            _cabinDoorFrames[i] = frame;
            _cabinDoorClosedPositions[i] = _cabinDoorAxis * Vector3.Dot(seam - edge, _cabinDoorAxis);
            frame.localPosition = _cabinDoorClosedPositions[i];
        }
    }

    internal void UpdateCabinDoor(float target, float dt)
    {
        var amount = Mathf.MoveTowards(_cabinDoorAmount, target, dt * .8f);
        if (amount == _cabinDoorAmount) return;
        _cabinDoorAmount = amount;
        var travel = Mathf.SmoothStep(0, 1, _cabinDoorAmount) * 1.05f;
        for (var i = 0; i < _cabinDoorFrames.Length; i++)
            _cabinDoorFrames[i].localPosition = _cabinDoorClosedPositions[i]
                + _cabinDoorAxis * (i == 0 ? travel : -travel);
    }

    internal void AddCabinDoorCollision(PhysicMaterial material, List<BoxCollider> solids)
    {
        for (var i = 0; i < _cabinDoorLeaves.Length; i++)
            AddMeshCollision(_cabinDoorLeaves[i], _cabinDoorFrames[i], material, solids);
    }

    // Ship-local actor pivot, padded for a standing capsule on either side of
    // the A02 leaf plane at Z -4.84. Bounded comparisons reject nonfinite input.
    internal static bool AtCabinDoorLocal(Vector3 point) =>
        point.x is >= -1.45f and <= 1.3f && point.y is >= -1.65f and <= 1.7f
        && point.z is >= -5.45f and <= -4.25f;
}
