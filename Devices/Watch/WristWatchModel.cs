// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Modstoke.Client;

internal sealed class ClientWristWatchModel : IDisposable
{
    internal const float StrapWidth = .026f;
    private readonly List<Material> _materials = new();
    private readonly List<Mesh> _meshes = new();
    private readonly ClientDeviceSurfaceFinish _finish = new();
    private Transform? _primary;
    private Transform? _secondary;
    private bool _disposed;
    internal GameObject Root { get; private set; } = null!;
    internal Transform Screen { get; private set; } = null!;

    internal ClientWristWatchModel(Transform hand, Quaternion rotation, Vector3 offset, int layer, float radius,
        float? fittedDepth = null, Vector2? fittedCenter = null, Vector2[]? fittedContour = null)
    {
        try
        {
            Root = new GameObject("Modstoke field watch") { layer = layer };
            Root.transform.SetParent(hand, false);
            Root.transform.localRotation = rotation;
            var scale = hand.lossyScale;
            var center = fittedCenter ?? Vector2.zero;
            offset += rotation * new Vector3(center.x, 0, center.y);
            Root.transform.localPosition = new Vector3(offset.x / scale.x, offset.y / scale.y, offset.z / scale.z);
            Root.transform.localScale = new Vector3(1 / scale.x, 1 / scale.y, 1 / scale.z);
            var shader = Shader.Find("HDRP/Lit");
            if (shader == null) throw new InvalidOperationException("The watch's lit material is unavailable.");
            AddMaterial(shader, "Watch dark olive polymer", new Color(.08f, .10f, .075f), 0, .25f);
            AddMaterial(shader, "Watch rubber strap", new Color(.014f, .018f, .015f), 0, .2f);
            AddMaterial(shader, "Watch charcoal coated hardware", new Color(.055f, .065f, .060f), 0, .25f);
            AddMaterial(shader, "Watch charcoal crown", new Color(.035f, .043f, .038f), 0, .25f);
            _finish.ApplyShell(_materials[0]);
            _finish.Apply(_materials[1], ClientDeviceSurfaceFinish.Surface.Rubber);
            _finish.Apply(_materials[2], ClientDeviceSurfaceFinish.Surface.Metal);
            _finish.Apply(_materials[3], ClientDeviceSurfaceFinish.Surface.Polymer);
            var depth = fittedDepth ?? radius * .68f;
            var surface = -depth - .013f;
            var shape = new Geometry();
            shape.Band(radius, depth, StrapWidth, .0023f, fittedContour);
            // The flat back sits on the dorsal strap. The rim has a real open aperture.
            shape.EdgedSolid(.053f, .059f, .007f, -depth - .003f, surface + .00715f, .00035f, 0);
            shape.Solid(.0528f, .0588f, .0069f, surface + .00715f, surface + .00655f, 1);
            shape.EdgedSolid(.053f, .059f, .007f, surface + .00655f, surface + .0025f, .00045f, 0);
            shape.BevelRim(.057f, .063f, .008f, .052f, .058f, .006f, surface + .003f, surface - .0002f, 0);
            shape.BevelRim(.052f, .058f, .006f, .045f, .047f, .002f, surface - .0002f, surface, 2);
            shape.BevelRim(.045f, .047f, .002f, .044f, .046f, .001f, surface, surface + .0004f, 1, preserveOpening: true);
            foreach (var sign in new[] { -1f, 1f })
            {
                // The lug grows from a seated case-back bridge. Both ends overlap
                // their parent solids; its footprint stays inside the existing lugs.
                shape.EdgedSolid(.028f, .006f, .0018f, -depth + .0015f, -depth - .0055f,
                    .0006f, 0, 0, sign * .0305f);
                shape.EdgedSolid(.033f, .009f, .0014f, -depth + .0035f, -depth - .0025f,
                    .0006f, 0, 0, sign * .035f);
                shape.Box(new Vector3(0, sign * .035f, -depth - .003f), new Vector3(.027f, .002f, .001f), 2);
                foreach (var x in new[] { -.0145f, .0145f })
                {
                    shape.EdgedSolid(.0027f, .0038f, .00065f, -depth - .0022f, -depth - .0033f,
                        .0002f, 2, x, sign * .035f);
                    shape.Box(new Vector3(x, sign * .035f, -depth - .0034f), new Vector3(.00035f, .0018f, .0002f), 1);
                }
                foreach (var x in new[] { -.023f, .023f })
                {
                    shape.Solid(.0027f, .0027f, .0007f, surface + .002f, surface - .0004f, 2, x, sign * .025f);
                    shape.Box(new Vector3(x, sign * .025f, surface - .0006f), new Vector3(.0018f, .00035f, .0003f), 1);
                }
            }
            // Shallow molded ribs follow the accepted cuff surface instead of forming raised beads.
            for (var i = 0; i < 7; i++)
            {
                var angle = (i + 1) * Mathf.PI / 8;
                foreach (var side in new[] { -1f, 1f })
                {
                    shape.StrapRib(side * angle, radius, depth, fittedContour);
                }
            }
            var buckle = Geometry.BandPoint(0, radius, depth, 0, fittedContour);
            // An open metal frame and captive tongue stay inside the original clasp envelope.
            foreach (var side in new[] { -1f, 1f })
            {
                shape.EdgedSolid(.003f, .016f, .00065f, buckle.y + .006f, buckle.y + .0015f,
                    .00035f, 2, side * .0135f);
                shape.EdgedSolid(.024f, .003f, .0005f, buckle.y + .006f, buckle.y + .0015f,
                    .00035f, 2, 0, side * .0065f);
            }
            shape.EdgedSolid(.024f, .010f, .001f, buckle.y + .0048f, buckle.y + .0025f, .0003f, 1);
            shape.EdgedSolid(.020f, .0017f, .0003f, buckle.y + .0058f, buckle.y + .0046f,
                .00025f, 2, -.002f);
            Publish(Root, shape);
            _primary = Button("Watch select crown", .014f, surface + .006f, 3);
            _secondary = Button("Watch back key", -.014f, surface + .006f, 2);
            var screen = new GameObject("Watch glass") { layer = layer };
            screen.transform.SetParent(Root.transform, false);
            screen.transform.localPosition = new Vector3(0, 0, surface - .0004f);
            Screen = screen.transform;
        }
        catch { Dispose(); throw; }
    }

    private Transform Button(string name, float y, float z, int material)
    {
        var button = new GameObject(name) { layer = Root.layer };
        button.transform.SetParent(Root.transform, false);
        button.transform.localPosition = new Vector3(.0295f, y, z);
        var shape = new Geometry();
        if (material == 3) shape.Crown();
        else
        {
            shape.EdgedSolid(.006f, .012f, .002f, .003f, -.003f, .00055f, material);
            // Raised grip bars stop short of the rounded key ends.
            foreach (var row in new[] { -.003f, 0f, .003f })
                shape.EdgedSolid(.00045f, .0016f, .00018f, .0023f, -.0023f,
                    .0001f, 1, .00305f, row);
        }
        Publish(button, shape);
        return button.transform;
    }

    internal void SetControls(bool primary, bool secondary)
    {
        Press(_primary, primary);
        Press(_secondary, secondary);
    }

    private static void Press(Transform? control, bool down)
    {
        if (control == null) return;
        var position = control.localPosition;
        // Fast mechanical take-up and a softer spring return. Exponential easing is
        // independent of frame rate and unscaled time keeps menus responsive.
        var target = down ? .0285f : .0295f;
        var blend = 1 - Mathf.Exp(-(down ? 48f : 26f) * Time.unscaledDeltaTime);
        position.x = Mathf.Lerp(position.x, target, blend);
        if (Mathf.Abs(position.x - target) < .00001f) position.x = target;
        control.localPosition = position;
    }

    private void AddMaterial(Shader shader, string name, Color color, float metal, float smoothness)
    {
        var material = new Material(shader) { name = name };
        _materials.Add(material);
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
        if (material.HasProperty("_Color")) material.SetColor("_Color", color);
        if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", metal);
        if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", smoothness);
    }

    private void Publish(GameObject parent, Geometry geometry)
    {
        var mesh = new Mesh { name = "Field watch instrument mesh" };
        _meshes.Add(mesh);
        geometry.Apply(mesh);
        parent.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = parent.AddComponent<MeshRenderer>();
        renderer.sharedMaterials = _materials.ToArray();
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (Root != null) { Root.SetActive(false); UnityEngine.Object.Destroy(Root); }
        foreach (var mesh in _meshes) if (mesh != null) UnityEngine.Object.Destroy(mesh);
        foreach (var material in _materials) if (material != null) UnityEngine.Object.Destroy(material);
        _finish.Dispose();
        _meshes.Clear();
        _materials.Clear();
        _primary = _secondary = null;
    }

    private sealed class Geometry
    {
        private readonly List<Vector3> _vertices = new();
        private readonly List<Vector3> _normals = new();
        private readonly List<Vector2> _uv = new();
        private readonly List<int>[] _triangles = { new(), new(), new(), new() };

        private void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, int material,
            Vector3? na = null, Vector3? nb = null, Vector3? nc = null, Vector3? nd = null)
        {
            Triangle(a, b, c, material, na, nb, nc);
            Triangle(a, c, d, material, na, nc, nd);
        }

        private void Triangle(Vector3 a, Vector3 b, Vector3 c, int material,
            Vector3? na = null, Vector3? nb = null, Vector3? nc = null)
        {
            var face = Vector3.Cross(b - a, c - a);
            var length = face.magnitude;
            if (length == 0) return;
            face /= length;
            var points = new[] { a, b, c };
            var normals = new[] { na ?? face, nb ?? face, nc ?? face };
            for (var i = 0; i < 3; i++)
            {
                _triangles[material].Add(_vertices.Count);
                _vertices.Add(points[i]);
                _normals.Add(normals[i]);
                _uv.Add(SurfaceUv(points[i], face));
            }
        }

        private static Vector2 SurfaceUv(Vector3 point, Vector3 normal)
        {
            var x = Mathf.Abs(normal.x);
            var y = Mathf.Abs(normal.y);
            var z = Mathf.Abs(normal.z);
            // Metres, shared across coplanar faces and their cap triangulation.
            return y >= x && y >= z ? new Vector2(point.x, point.z)
                : x >= z ? new Vector2(point.z, point.y) : new Vector2(point.x, point.y);
        }

        private static Vector3 Unit(Vector3 value)
        {
            var length = value.magnitude;
            return length > 0 ? value / length : Vector3.zero;
        }

        private static Vector3[] Ring(float width, float height, float cut, float z, float x = 0, float y = 0,
            bool clipped = false)
        {
            var w = width * .5f;
            var h = height * .5f;
            if (cut <= 0) return new[]
            {
                new Vector3(x + w, y + h, z), new Vector3(x + w, y - h, z),
                new Vector3(x - w, y - h, z), new Vector3(x - w, y + h, z)
            };
            cut = Mathf.Min(cut, Mathf.Min(w, h) * .95f);
            var result = new Vector3[20];
            for (var corner = 0; corner < 4; corner++)
            for (var step = 0; step <= 4; step++)
            {
                var angle = (90 - corner * 90 - step * 22.5f) * Mathf.Deg2Rad;
                var cx = x + (corner < 2 ? 1 : -1) * (w - cut);
                var cy = y + (corner == 0 || corner == 3 ? 1 : -1) * (h - cut);
                result[corner * 5 + step] = new Vector3(cx + Mathf.Cos(angle) * cut, cy + Mathf.Sin(angle) * cut, z);
                if (clipped)
                {
                    var start = (90 - corner * 90) * Mathf.Deg2Rad;
                    var end = start - Mathf.PI * .5f;
                    var t = step * .25f;
                    result[corner * 5 + step] = new Vector3(cx + ((1 - t) * Mathf.Cos(start) + t * Mathf.Cos(end)) * cut,
                        cy + ((1 - t) * Mathf.Sin(start) + t * Mathf.Sin(end)) * cut, z);
                }
            }
            return result;
        }

        private void Join(Vector3[] outer, Vector3[] inner, int material, bool smooth = false)
        {
            for (var i = 0; i < outer.Length; i++)
            {
                var next = (i + 1) % outer.Length;
                if (!smooth)
                {
                    Quad(outer[i], outer[next], inner[next], inner[i], material);
                    continue;
                }
                Vector3 Normal(int index)
                {
                    var before = (index + outer.Length - 1) % outer.Length;
                    var after = (index + 1) % outer.Length;
                    var tangent = Unit(outer[index] - outer[before]) + Unit(outer[after] - outer[index]);
                    return Unit(Vector3.Cross(tangent, inner[index] - outer[index]));
                }
                var a = Normal(i);
                var b = Normal(next);
                Quad(outer[i], outer[next], inner[next], inner[i], material, a, b, b, a);
            }
        }

        internal void Solid(float width, float height, float cut, float back, float front, int material, float x = 0, float y = 0)
        {
            var top = Ring(width, height, cut, front, x, y);
            var bottom = Ring(width, height, cut, back, x, y);
            Join(bottom, top, material, smooth: cut > 0);
            Caps(top, bottom, material);
        }

        internal void EdgedSolid(float width, float height, float cut, float back, float front, float bevel,
            int material, float x = 0, float y = 0)
        {
            var insetCut = Mathf.Max(.00001f, cut - bevel);
            var top = Ring(width - bevel * 2, height - bevel * 2, insetCut, front, x, y);
            var upper = Ring(width, height, cut, front + bevel, x, y);
            var lower = Ring(width, height, cut, back - bevel, x, y);
            var bottom = Ring(width - bevel * 2, height - bevel * 2, insetCut, back, x, y);
            Join(bottom, lower, material, smooth: true);
            Join(lower, upper, material, smooth: true);
            Join(upper, top, material, smooth: true);
            Caps(top, bottom, material);
        }

        private void Caps(Vector3[] top, Vector3[] bottom, int material)
        {
            for (var i = 1; i < top.Length - 1; i++)
            {
                Triangle(top[0], top[i], top[i + 1], material);
                Triangle(bottom[0], bottom[i + 1], bottom[i], material);
            }
        }

        internal void BevelRim(float width, float height, float cut, float innerWidth, float innerHeight,
            float innerCut, float back, float front, int material, bool preserveOpening = false)
            => Join(Ring(width, height, cut, back), Ring(innerWidth, innerHeight, innerCut, front, clipped: preserveOpening), material, smooth: true);

        internal void Crown()
        {
            // A captive shaft, sealing collar, fluted grip and inset end button share
            // the original travel axis. Only the grip has the shallow axial flutes.
            var profile = new[] {
                new Vector2(-.003f, .0026f), new Vector2(-.0018f, .0026f),
                new Vector2(-.0016f, .0041f), new Vector2(-.0011f, .0045f),
                new Vector2(.0021f, .0045f), new Vector2(.0027f, .0041f),
                new Vector2(.003f, .0034f), new Vector2(.0028f, .0027f)
            };
            const int segments = 32;
            var rings = new Vector3[profile.Length][];
            for (var row = 0; row < profile.Length; row++)
            {
                rings[row] = new Vector3[segments];
                for (var i = 0; i < segments; i++)
                {
                    var angle = i * Mathf.PI * 2 / segments;
                    var radius = profile[row].y - ((row == 3 || row == 4) && i % 2 == 0 ? .00024f : 0);
                    rings[row][i] = new Vector3(profile[row].x, Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius);
                }
                if (row > 0) Join(rings[row - 1], rings[row], row < 3 ? 1 : row < 6 ? 2 : 3, smooth: row != 4);
            }
            for (var i = 0; i < segments; i++)
            {
                var next = (i + 1) % segments;
                Triangle(new Vector3(profile[0].x, 0, 0), rings[0][next], rings[0][i], 1);
                Triangle(new Vector3(profile[7].x, 0, 0), rings[7][i], rings[7][next], 3);
            }
        }

        internal void Box(Vector3 center, Vector3 size, int material)
            => Solid(size.x, size.y, 0, center.z + size.z * .5f, center.z - size.z * .5f, material, center.x, center.y);

        internal static Vector2 BandPoint(float angle, float radius, float depth, float clearance, Vector2[]? contour)
        {
            var direction = new Vector2(Mathf.Sin(angle), Mathf.Cos(angle));
            if (contour == null)
                return new Vector2(direction.x * (radius + clearance), direction.y * (depth + clearance));
            var distance = float.PositiveInfinity;
            for (var i = 0; i < contour.Length; i++)
            {
                var edge = contour[(i + 1) % contour.Length] - contour[i];
                var normal = new Vector2(-edge.y, edge.x).normalized;
                var facing = Vector2.Dot(normal, direction);
                if (facing > 0)
                    distance = Mathf.Min(distance, (Vector2.Dot(normal, contour[i]) + clearance) / facing);
            }
            return direction * distance;
        }

        internal void StrapRib(float angle, float radius, float depth, Vector2[]? contour)
        {
            Vector3 Point(float offset, float y, bool outer)
            {
                var point = BandPoint(angle + offset, radius, depth, outer ? .00265f : .00225f, contour);
                return new Vector3(point.x, y, point.y);
            }
            const float halfAngle = .026f;
            const float halfWidth = .0115f;
            var a = Point(-halfAngle, -halfWidth, true);
            var b = Point(halfAngle, -halfWidth, true);
            var c = Point(halfAngle, halfWidth, true);
            var d = Point(-halfAngle, halfWidth, true);
            var e = Point(-halfAngle, -halfWidth, false);
            var f = Point(halfAngle, -halfWidth, false);
            var g = Point(halfAngle, halfWidth, false);
            var h = Point(-halfAngle, halfWidth, false);
            Quad(a, b, c, d, 1);
            Quad(d, c, g, h, 1);
            Quad(e, f, b, a, 1);
            Quad(a, d, h, e, 1);
            Quad(b, f, g, c, 1);
            Quad(h, g, f, e, 1);
        }

        internal void Band(float radius, float depth, float width, float thickness, Vector2[]? contour)
        {
            var count = contour?.Length ?? 32;
            var outside = contour != null ? OffsetContour(contour, thickness) : null;
            for (var i = 0; i < count; i++)
            {
                var next = (i + 1) % count;
                Vector3 Point(int index, float y, bool outer)
                {
                    var point = contour != null ? (outer ? outside![index] : contour[index])
                        : BandPoint(index * Mathf.PI * 2 / count, radius, depth, outer ? thickness : 0, null);
                    return new Vector3(point.x, y, point.y);
                }
                var half = width * .5f;
                Vector3 Normal(int index, bool outer)
                {
                    var before = (index + count - 1) % count;
                    var after = (index + 1) % count;
                    var tangent = Unit(Point(index, 0, outer) - Point(before, 0, outer))
                        + Unit(Point(after, 0, outer) - Point(index, 0, outer));
                    return Unit(Vector3.Cross(tangent, Vector3.up));
                }
                var a = Normal(i, true);
                var b = Normal(next, true);
                Quad(Point(i, -half, true), Point(next, -half, true), Point(next, half, true), Point(i, half, true), 1, a, b, b, a);
                a = -Normal(i, false);
                b = -Normal(next, false);
                Quad(Point(i, half, false), Point(next, half, false), Point(next, -half, false), Point(i, -half, false), 1, a, b, b, a);
                Quad(Point(i, half, true), Point(next, half, true), Point(next, half, false), Point(i, half, false), 1);
                Quad(Point(i, -half, false), Point(next, -half, false), Point(next, -half, true), Point(i, -half, true), 1);
            }
        }

        private static Vector2[] OffsetContour(Vector2[] contour, float thickness)
        {
            var result = new Vector2[contour.Length];
            for (var i = 0; i < contour.Length; i++)
            {
                var before = (contour[i] - contour[(i + contour.Length - 1) % contour.Length]).normalized;
                var after = (contour[(i + 1) % contour.Length] - contour[i]).normalized;
                var a = new Vector2(-before.y, before.x);
                var b = new Vector2(-after.y, after.x);
                result[i] = contour[i] + (a + b) * (thickness / (1 + Vector2.Dot(a, b)));
            }
            return result;
        }

        internal void Apply(Mesh mesh)
        {
            mesh.vertices = _vertices.ToArray();
            mesh.normals = _normals.ToArray();
            mesh.uv = _uv.ToArray();
            mesh.subMeshCount = _triangles.Length;
            for (var i = 0; i < _triangles.Length; i++) mesh.SetTriangles(_triangles[i].ToArray(), i);
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
        }
    }
}
