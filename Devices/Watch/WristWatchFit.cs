// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Client;

internal sealed class ClientWristWatchFit
{
    private readonly List<(IntPtr Piece, IntPtr Instance)> _clothingPieces = new();
    private IntPtr _rig;
    private IntPtr _hand;
    private IntPtr _forearm;
    private IntPtr _clothing;
    private Vector2 _fit;
    private Vector2 _center;
    private Vector2[]? _contour;
    private int _rendererCount;
    private int _sampleCount;
    private bool _fitted;

    internal Vector2 Center { get; private set; }
    internal Vector2[]? Contour { get; private set; }

    internal static IEnumerable<SkinnedMeshRenderer> Renderers(Transform rig, Transform hand, Transform forearm)
    {
        // Equipped clothes live beside PlayerAnimator. Their bones are retargeted
        // into this rig, but their renderers remain under ClothingSystem.
        var clothing = LocalPlayer.ClothingSystem;
        var clothingRoot = clothing != null && !clothing.transform.IsChildOf(rig)
            ? clothing.transform : null;
        foreach (var owner in new[] { rig, clothingRoot })
        {
            if (owner == null) continue;
            foreach (var skin in owner.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (skin == null || !skin.enabled || !skin.gameObject.activeInHierarchy || skin.sharedMesh == null) continue;
                foreach (var bone in skin.bones)
                    if (bone != null && (bone == hand || bone == forearm || bone.IsChildOf(forearm)))
                    {
                        yield return skin;
                        break;
                    }
            }
        }
    }

    internal Vector2 Measure(Transform rig, Transform hand, Transform forearm,
        Quaternion inHand, Vector3 offset, float bareRadius, out int rendererCount, out int sampleCount,
        bool measureSurface = true)
    {
        var clothing = LocalPlayer.ClothingSystem;
        var pieces = clothing != null ? clothing._clothingHookupData : null;
        var pieceCount = pieces?.Count ?? 0;
        var clothingPointer = clothing != null ? clothing.Pointer : IntPtr.Zero;
        var sameAppearance = _fitted && _rig == rig.Pointer && _hand == hand.Pointer
            && _forearm == forearm.Pointer && _clothing == clothingPointer && _clothingPieces.Count == pieceCount;
        for (var i = 0; sameAppearance && i < pieceCount; i++)
        {
            var piece = pieces![i];
            var instance = piece?.Instance;
            sameAppearance = _clothingPieces[i] == (piece?.ClothingPiece?.Pointer ?? IntPtr.Zero,
                instance != null ? instance.Pointer : IntPtr.Zero);
        }
        // Item animations deform or temporarily hide the same sleeve. They do
        // not change its fitted size. Only a new garment or rig needs a new fit.
        if (sameAppearance)
        {
            Center = _center;
            Contour = _contour;
            rendererCount = _rendererCount;
            sampleCount = _sampleCount;
            return _fit;
        }
        // Attachment can occur with a bent wrist. Keep its size provisional
        // until the raised pose can measure a neutral sleeve cross-section.
        // A new outfit must also finish loading before it can be measured.
        if (!measureSurface || clothing != null && (clothing._clothingIdsCurrentlyLoading?.Count > 0
            || clothing._delayedBlendShapeDataToApply?.Count > 0))
        {
            Center = Vector2.zero;
            Contour = null;
            rendererCount = sampleCount = 0;
            return new Vector2(bareRadius + .002f, bareRadius * .68f + .002f);
        }
        var fit = MeasureSurface(rig, hand, forearm, inHand, offset, bareRadius,
            out var center, out var contour, out rendererCount, out sampleCount);
        Center = center;
        Contour = contour;
        if (sampleCount > 0)
        {
            _rig = rig.Pointer;
            _hand = hand.Pointer;
            _forearm = forearm.Pointer;
            _clothing = clothingPointer;
            _clothingPieces.Clear();
            for (var i = 0; i < pieceCount; i++)
            {
                var piece = pieces![i];
                var instance = piece?.Instance;
                _clothingPieces.Add((piece?.ClothingPiece?.Pointer ?? IntPtr.Zero,
                    instance != null ? instance.Pointer : IntPtr.Zero));
            }
            _fit = fit;
            _center = center;
            _contour = contour;
            _rendererCount = rendererCount;
            _sampleCount = sampleCount;
            _fitted = true;
        }
        return fit;
    }

    internal void Reset()
    {
        _fitted = false;
        _rig = _hand = _forearm = _clothing = IntPtr.Zero;
        _clothingPieces.Clear();
        _rendererCount = _sampleCount = 0;
        _fit = _center = Center = default;
        _contour = Contour = null;
    }

    // Measure only the sleeve and wrist underneath the strap. Including the
    // wider palm or distant sleeve folds makes an oversized cuff.
    private static Vector2 MeasureSurface(Transform rig, Transform hand, Transform forearm,
        Quaternion inHand, Vector3 offset, float bareRadius, out Vector2 fittedCenter,
        out Vector2[]? fittedContour, out int rendererCount, out int sampleCount)
    {
        rendererCount = sampleCount = 0;
        fittedCenter = Vector2.zero;
        fittedContour = null;
        var inverse = Quaternion.Inverse(hand.rotation * inHand);
        var center = hand.position + hand.rotation * offset;
        var points = new List<Vector2>();
        var baked = new Mesh { name = "Watch cuff fitting" };
        try
        {
            foreach (var skin in Renderers(rig, hand, forearm))
            {
                var weights = skin.sharedMesh.boneWeights;
                var bones = skin.bones;
                if (weights == null || weights.Length != skin.sharedMesh.vertexCount || bones == null) continue;
                var wristBones = new bool[bones.Length];
                for (var i = 0; i < bones.Length; i++)
                {
                    var bone = bones[i];
                    wristBones[i] = bone != null && (bone == forearm || bone.IsChildOf(forearm))
                        && bone != hand && !bone.IsChildOf(hand);
                }
                rendererCount++;
                // Compensate renderer scale during baking. The world matrix
                // below restores it once, including scaled clothing roots.
                skin.BakeMesh(baked, true);
                var matrix = skin.transform.localToWorldMatrix;
                var vertices = baked.vertices;
                if (vertices.Length != weights.Length) { baked.Clear(); continue; }
                for (var i = 0; i < vertices.Length; i++)
                {
                    // A full-body renderer also contains the opposite hand.
                    // Proximity alone includes it when a two-handed item brings
                    // both hands together. Fit only forearm-owned vertices;
                    // curled fingers and hand-owned palm vertices do not count.
                    if (!BelongsToWrist(weights[i], wristBones)) continue;
                    var point = inverse * (matrix.MultiplyPoint3x4(vertices[i]) - center);
                    if (!float.IsFinite(point.x) || !float.IsFinite(point.y) || !float.IsFinite(point.z)
                        || Mathf.Abs(point.y) > ClientWristWatchModel.StrapWidth * .5f + .002f
                        || Mathf.Abs(point.x) > .09f || Mathf.Abs(point.z) > .09f) continue;
                    points.Add(new Vector2(point.x, point.z));
                }
                baked.Clear();
            }
        }
        finally { UnityEngine.Object.Destroy(baked); }
        sampleCount = points.Count;
        if (sampleCount == 0) return new Vector2(bareRadius + .002f, bareRadius * .68f + .002f);

        // The wrist bone is not necessarily at the center of the sleeve. Fit
        // around the measured cross-section instead of mirroring its farther
        // side across that bone and leaving the opposite side of the band loose.
        var minimum = points[0];
        var maximum = points[0];
        foreach (var point in points)
        {
            minimum = Vector2.Min(minimum, point);
            maximum = Vector2.Max(maximum, point);
        }
        fittedCenter = (minimum + maximum) * .5f;
        // A diagonal sleeve fold should not enlarge every side of the strap.
        // Each supporting plane clears the actual cross-section independently;
        // their intersections form a convex band around every accepted sample.
        const int segments = 32;
        var normals = new Vector2[segments];
        var supports = new float[segments];
        for (var i = 0; i < segments; i++)
        {
            var angle = i * Mathf.PI * 2 / segments;
            var normal = new Vector2(Mathf.Sin(angle), Mathf.Cos(angle));
            normals[i] = normal;
            var bareX = bareRadius * normal.x;
            var bareZ = bareRadius * .68f * normal.y;
            var support = Mathf.Sqrt(bareX * bareX + bareZ * bareZ);
            foreach (var point in points)
                support = Mathf.Max(support, Vector2.Dot(normal, point - fittedCenter));
            supports[i] = support + .002f;
        }
        fittedContour = new Vector2[segments];
        var extent = Vector2.zero;
        for (var i = 0; i < segments; i++)
        {
            var next = (i + 1) % segments;
            var a = normals[i];
            var b = normals[next];
            var determinant = a.x * b.y - a.y * b.x;
            var point = new Vector2((supports[i] * b.y - a.y * supports[next]) / determinant,
                (a.x * supports[next] - supports[i] * b.x) / determinant);
            fittedContour[i] = point;
            extent = Vector2.Max(extent, new Vector2(Mathf.Abs(point.x), Mathf.Abs(point.y)));
        }
        return extent;
    }

    private static bool BelongsToWrist(BoneWeight weight, bool[] wristBones)
    {
        float Owned(int index, float amount) => index >= 0 && index < wristBones.Length && wristBones[index] ? amount : 0;
        return Owned(weight.boneIndex0, weight.weight0) + Owned(weight.boneIndex1, weight.weight1)
            + Owned(weight.boneIndex2, weight.weight2) + Owned(weight.boneIndex3, weight.weight3) >= .5f;
    }
}
