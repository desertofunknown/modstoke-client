// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using UnityEngine;
using UnityEngine.Rendering;

namespace Modstoke.Client;

internal sealed partial class ClientDroneModel
{
    private void FlattenInventory(bool battery)
    {
        // The mat uses XY silhouettes facing -Z. Uniform miniaturization preserves
        // the physical proportions while retaining the established selection footprint.
        var scale = Vector3.one * (battery ? .25f : .148f);
        var pose = Matrix4x4.TRS(Vector3.zero, Quaternion.Euler(-90, 0, 0), scale);
        var combined = new Geometry();
        foreach (var part in _inventoryParts)
        {
            // Accumulate local poses, avoiding precision loss from a distant player origin.
            var local = Matrix4x4.identity;
            var node = part.Transform;
            while (node != _root!.transform)
            {
                local = Matrix4x4.TRS(node.localPosition, node.localRotation, node.localScale) * local;
                node = node.parent;
            }
            combined.Append(part.Shape, pose * local);
        }
        _inventoryParts.Clear();
        if (!battery) combined.CenterAt(new Vector3(0, 0, -.018f));
        // All articulation was baked once. Only the root renderer participates in
        // native selection/highlighting, and inventory never runs the flight visual update.
        for (var i = _root!.transform.childCount - 1; i >= 0; i--)
        {
            var child = _root.transform.GetChild(i).gameObject;
            child.SetActive(false);
            UnityEngine.Object.Destroy(child);
        }
        _gimbal = null;
        PublishMesh(_root, combined, battery ? "Drone pack inventory mesh" : "Folded field drone inventory mesh",
            ShadowCastingMode.Off);
    }
}
