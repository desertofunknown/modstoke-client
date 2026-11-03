// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Collections.Generic;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientDroneModel
{
    private sealed class Geometry
    {
        private const int Segments = 32;
        private readonly List<Vector3> _vertices = new();
        private readonly List<Vector3> _normals = new();
        private readonly List<Vector2> _uv = new();
        private readonly List<int>[] _triangles = { new(), new(), new(), new(), new(), new(), new() };

        private void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, int material,
            Vector3? normalA = null, Vector3? normalB = null, Vector3? normalC = null, Vector3? normalD = null)
        {
            // Rounded caps converge at their corners and optical center.
            if ((a - d).sqrMagnitude < 1e-16f)
            {
                Triangle(a, b, c, material, normalA, normalB, normalC);
                return;
            }
            if ((b - c).sqrMagnitude < 1e-16f)
            {
                Triangle(a, b, d, material, normalA, normalB, normalD);
                return;
            }
            var start = _vertices.Count;
            var normal = FaceNormal(a, b, c);
            _vertices.AddRange(new[] { a, b, c, d });
            _normals.AddRange(new[] { normalA ?? normal, normalB ?? normal, normalC ?? normal, normalD ?? normal });
            // Metre-scaled UVs keep the molded grain consistent on large and small panels.
            foreach (var point in new[] { a, b, c, d })
                _uv.Add(SurfaceUv(point, normal));
            _triangles[material].AddRange(new[] { start, start + 1, start + 2, start, start + 2, start + 3 });
        }

        private void Triangle(Vector3 a, Vector3 b, Vector3 c, int material,
            Vector3? normalA = null, Vector3? normalB = null, Vector3? normalC = null)
        {
            if (Vector3.Cross(b - a, c - a).sqrMagnitude < 1e-24f) return;
            var start = _vertices.Count;
            var normal = FaceNormal(a, b, c);
            _normals.AddRange(new[] { normalA ?? normal, normalB ?? normal, normalC ?? normal });
            foreach (var point in new[] { a, b, c })
            {
                _vertices.Add(point);
                _uv.Add(SurfaceUv(point, normal));
            }
            _triangles[material].AddRange(new[] { start, start + 1, start + 2 });
        }

        private static Vector2 SurfaceUv(Vector3 point, Vector3 normal)
        {
            // Shared planar projection prevents visible wedges between coplanar cap triangles.
            var x = Mathf.Abs(normal.x);
            var y = Mathf.Abs(normal.y);
            var z = Mathf.Abs(normal.z);
            return y >= x && y >= z ? new Vector2(point.x, point.z)
                : x >= z ? new Vector2(point.z, point.y) : new Vector2(point.x, point.y);
        }

        private static Vector3 FaceNormal(Vector3 a, Vector3 b, Vector3 c)
        {
            // Unity's normalizing epsilon exceeds the area of small screw and seam faces.
            return Unit(Vector3.Cross(b - a, c - a));
        }

        private static Vector3 Unit(Vector3 vector)
            => vector.magnitude > 1e-12f ? vector / vector.magnitude : Vector3.up;

        internal void Append(Geometry source, Matrix4x4 transform)
        {
            var first = _vertices.Count;
            var normals = transform.inverse.transpose;
            for (var i = 0; i < source._vertices.Count; i++)
            {
                _vertices.Add(transform.MultiplyPoint3x4(source._vertices[i]));
                _normals.Add(normals.MultiplyVector(source._normals[i]).normalized);
                _uv.Add(source._uv[i]);
            }
            for (var material = 0; material < _triangles.Length; material++)
                foreach (var index in source._triangles[material]) _triangles[material].Add(first + index);
        }

        internal void CenterAt(Vector3 center)
        {
            var minimum = _vertices[0];
            var maximum = minimum;
            foreach (var vertex in _vertices)
            {
                minimum = Vector3.Min(minimum, vertex);
                maximum = Vector3.Max(maximum, vertex);
            }
            var offset = center - (minimum + maximum) * .5f;
            for (var i = 0; i < _vertices.Count; i++) _vertices[i] += offset;
        }

        internal void Box(Vector3 center, Vector3 size, int material, Quaternion? rotation = null)
        {
            var q = rotation ?? Quaternion.identity;
            var x = q * Vector3.right * size.x * .5f;
            var y = q * Vector3.up * size.y * .5f;
            var z = q * Vector3.forward * size.z * .5f;
            Quad(center - x - y - z, center - x + y - z, center + x + y - z, center + x - y - z, material);
            Quad(center + x - y + z, center + x + y + z, center - x + y + z, center - x - y + z, material);
            Quad(center - x - y + z, center - x + y + z, center - x + y - z, center - x - y - z, material);
            Quad(center + x - y - z, center + x + y - z, center + x + y + z, center + x - y + z, material);
            Quad(center - x + y - z, center - x + y + z, center + x + y + z, center + x + y - z, material);
            Quad(center - x - y + z, center - x - y - z, center + x - y - z, center + x - y + z, material);
        }

        internal void BevelBox(Vector3 center, Vector3 size, float bevel, int material, Quaternion? rotation = null)
        {
            var q = rotation ?? Quaternion.identity;
            bevel = Mathf.Min(bevel, Mathf.Min(size.x, Mathf.Min(size.y, size.z)) * .45f);
            // Small hardware keeps a single chamfer; broad molded edges get a true
            // radius. Analytic normals meet the planar faces without a dark seam.
            var steps = bevel >= .006f ? 3 : 1;
            var count = 4 * (steps + 1);
            var levels = steps * 2 + 2;
            var loops = new Vector3[levels][];
            var normals = new Vector3[levels][];
            var core = size * .5f - Vector3.one * bevel;
            for (var level = 0; level < levels; level++)
            {
                var upper = level > steps;
                var angle = (upper ? level - steps - 1 : level - steps) * Mathf.PI * .5f / steps;
                var radial = Mathf.Cos(angle);
                var vertical = Mathf.Sin(angle);
                loops[level] = new Vector3[count];
                normals[level] = new Vector3[count];
                for (var corner = 0; corner < 4; corner++)
                for (var step = 0; step <= steps; step++)
                {
                    var a = (corner + step / (float)steps) * Mathf.PI * .5f;
                    var direction = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                    var cornerX = corner == 0 || corner == 3 ? core.x : -core.x;
                    var cornerZ = corner < 2 ? core.z : -core.z;
                    var normal = direction * radial + Vector3.up * vertical;
                    var index = corner * (steps + 1) + step;
                    loops[level][index] = center + q * (new Vector3(cornerX, upper ? core.y : -core.y, cornerZ) + normal * bevel);
                    normals[level][index] = q * normal;
                }
            }
            for (var i = 0; i < count; i++)
            {
                var next = (i + 1) % count;
                for (var level = 0; level < levels - 1; level++)
                    Quad(loops[level][i], loops[level + 1][i], loops[level + 1][next], loops[level][next], material,
                        normals[level][i], normals[level + 1][i], normals[level + 1][next], normals[level][next]);
                Triangle(center + q * Vector3.up * size.y * .5f, loops[levels - 1][next], loops[levels - 1][i], material);
                Triangle(center - q * Vector3.up * size.y * .5f, loops[0][i], loops[0][next], material);
            }
        }

        internal void Loft(Vector3 center, (float Z, float Width, float Bottom, float Top)[] sections,
            int material, Quaternion? rotation = null, int? undersideMaterial = null)
        {
            // Retained cross sections control the shell crown and mating envelope.
            // The rounded rectangular section leaves useful flat panel areas.
            const int around = 32;
            const int between = 3;
            var q = rotation ?? Quaternion.identity;
            var length = (sections.Length - 1) * between + 1;
            var points = new Vector3[length, around];
            var normals = new Vector3[length, around];
            for (var ring = 0; ring < length; ring++)
            {
                var station = Mathf.Min(ring / between, sections.Length - 2);
                var t = (ring - station * between) / (float)between;
                var a = sections[Mathf.Max(station - 1, 0)];
                var b = sections[station];
                var c = sections[station + 1];
                var d = sections[Mathf.Min(station + 2, sections.Length - 1)];
                var width = SectionCurve(a.Width, b.Width, c.Width, d.Width, t);
                var bottom = SectionCurve(a.Bottom, b.Bottom, c.Bottom, d.Bottom, t);
                var top = SectionCurve(a.Top, b.Top, c.Top, d.Top, t);
                var z = Mathf.Lerp(b.Z, c.Z, t);
                for (var p = 0; p < around; p++)
                {
                    var angle = p * Mathf.PI * 2 / around;
                    var x = Mathf.Cos(angle);
                    var y = Mathf.Sin(angle);
                    x = Mathf.Sign(x) * Mathf.Pow(Mathf.Abs(x), .45f);
                    y = Mathf.Sign(y) * Mathf.Pow(Mathf.Abs(y), .45f);
                    points[ring, p] = center + q * new Vector3(x * width, (top + bottom) * .5f + y * (top - bottom) * .5f, z);
                }
            }
            for (var ring = 0; ring < length; ring++)
            for (var p = 0; p < around; p++)
            {
                var across = points[ring, (p + 1) % around] - points[ring, (p + around - 1) % around];
                var along = points[Mathf.Min(ring + 1, length - 1), p] - points[Mathf.Max(ring - 1, 0), p];
                normals[ring, p] = Unit(Vector3.Cross(across, along));
            }
            for (var ring = 0; ring < length - 1; ring++)
            for (var p = 0; p < around; p++)
            {
                var next = (p + 1) % around;
                var surface = p >= around / 2 ? undersideMaterial ?? material : material;
                Quad(points[ring, p], points[ring, next], points[ring + 1, next], points[ring + 1, p], surface,
                    normals[ring, p], normals[ring, next], normals[ring + 1, next], normals[ring + 1, p]);
            }
            var first = sections[0];
            var last = sections[sections.Length - 1];
            var start = center + q * new Vector3(0, (first.Bottom + first.Top) * .5f, first.Z);
            var end = center + q * new Vector3(0, (last.Bottom + last.Top) * .5f, last.Z);
            for (var p = 0; p < around; p++)
            {
                var next = (p + 1) % around;
                var surface = p >= around / 2 ? undersideMaterial ?? material : material;
                Triangle(start, points[0, next], points[0, p], surface);
                Triangle(end, points[length - 1, p], points[length - 1, next], surface);
            }
        }

        internal void FacetedLoft(Vector3 center, (float Z, float Width, float Bottom, float Top)[] sections,
            int material, Quaternion? rotation = null, int? undersideMaterial = null)
        {
            // Eight flat faces retain the folded-shell planes and the arm's bent
            // profile. The bevel follows each section without smoothing its corners.
            var q = rotation ?? Quaternion.identity;
            var rings = new Vector3[sections.Length][];
            for (var i = 0; i < sections.Length; i++)
            {
                var section = sections[i];
                var bevel = Mathf.Min(section.Width * .22f, (section.Top - section.Bottom) * .22f);
                var x = section.Width;
                var low = section.Bottom;
                var high = section.Top;
                var z = section.Z;
                rings[i] = new[] {
                    new Vector3(x, high - bevel, z), new Vector3(x - bevel, high, z),
                    new Vector3(-x + bevel, high, z), new Vector3(-x, high - bevel, z),
                    new Vector3(-x, low + bevel, z), new Vector3(-x + bevel, low, z),
                    new Vector3(x - bevel, low, z), new Vector3(x, low + bevel, z) };
                for (var p = 0; p < 8; p++) rings[i][p] = center + q * rings[i][p];
            }
            for (var i = 0; i < sections.Length - 1; i++)
            for (var p = 0; p < 8; p++)
                Quad(rings[i][p], rings[i][(p + 1) % 8], rings[i + 1][(p + 1) % 8], rings[i + 1][p],
                    p >= 4 && p <= 6 ? undersideMaterial ?? material : material);
            var first = sections[0];
            var last = sections[sections.Length - 1];
            var start = center + q * new Vector3(0, (first.Bottom + first.Top) * .5f, first.Z);
            var end = center + q * new Vector3(0, (last.Bottom + last.Top) * .5f, last.Z);
            for (var p = 0; p < 8; p++)
            {
                Triangle(start, rings[0][(p + 1) % 8], rings[0][p], material);
                Triangle(end, rings[sections.Length - 1][p], rings[sections.Length - 1][(p + 1) % 8], material);
            }
        }

        private static float SectionCurve(float a, float b, float c, float d, float t)
        {
            var value = .5f * (2 * b + (c - a) * t + (2 * a - 5 * b + 4 * c - d) * t * t
                + (-a + 3 * b - 3 * c + d) * t * t * t);
            return Mathf.Clamp(value, Mathf.Min(b, c), Mathf.Max(b, c));
        }

        internal void Beam(Vector3 start, Vector3 end, float width, float height, int material)
            => BevelBox((start + end) * .5f, new Vector3(width, height, (end - start).magnitude),
                Mathf.Min(width, height) * .2f, material, Quaternion.LookRotation(end - start));

        internal void Cylinder(Vector3 center, float radius, float height, int material, Quaternion? rotation = null)
        {
            var q = rotation ?? Quaternion.identity;
            var up = q * Vector3.up * (height * .5f);
            for (var i = 0; i < Segments; i++)
            {
                var a = i * Mathf.PI * 2 / Segments;
                var b = (i + 1) * Mathf.PI * 2 / Segments;
                var p = q * new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                var n = q * new Vector3(Mathf.Cos(b), 0, Mathf.Sin(b));
                Quad(center + p * radius - up, center + p * radius + up, center + n * radius + up,
                    center + n * radius - up, material, p, p, n, n);
                Triangle(center + up, center + n * radius + up, center + p * radius + up, material);
                Triangle(center - up, center + p * radius - up, center + n * radius - up, material);
            }
        }

        internal void Ring(Vector3 center, float outer, float inner, float height, int material, Quaternion? rotation = null)
        {
            var q = rotation ?? Quaternion.identity;
            var y = q * Vector3.up * (height * .5f);
            for (var i = 0; i < Segments; i++)
            {
                var a = i * Mathf.PI * 2 / Segments;
                var b = (i + 1) * Mathf.PI * 2 / Segments;
                var p = q * new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                var n = q * new Vector3(Mathf.Cos(b), 0, Mathf.Sin(b));
                Quad(center + p * outer - y, center + p * outer + y, center + n * outer + y, center + n * outer - y,
                    material, p, p, n, n);
                Quad(center + n * inner - y, center + n * inner + y, center + p * inner + y, center + p * inner - y,
                    material, -n, -n, -p, -p);
                Quad(center + p * inner + y, center + n * inner + y, center + n * outer + y, center + p * outer + y, material);
                Quad(center + n * inner - y, center + p * inner - y, center + p * outer - y, center + n * outer - y, material);
            }
        }

        internal void Lens(Vector3 center, float radius, float depth, int material, Quaternion rotation)
        {
            const int rings = 5;
            for (var ring = 0; ring < rings; ring++)
            {
                var outer = 1 - ring / (float)rings;
                var inner = 1 - (ring + 1) / (float)rings;
                for (var p = 0; p < Segments; p++)
                {
                    var a = p * Mathf.PI * 2 / Segments;
                    var b = (p + 1) * Mathf.PI * 2 / Segments;
                    Vector3 Point(float r, float angle) => center + rotation * new Vector3(
                        Mathf.Cos(angle) * radius * r, depth * (1 - r * r), Mathf.Sin(angle) * radius * r);
                    Vector3 Normal(float r, float angle) => rotation * Unit(new Vector3(
                        2 * depth * r * Mathf.Cos(angle) / radius, 1, 2 * depth * r * Mathf.Sin(angle) / radius));
                    Quad(Point(outer, a), Point(inner, a), Point(inner, b), Point(outer, b), material,
                        Normal(outer, a), Normal(inner, a), Normal(inner, b), Normal(outer, b));
                }
            }
        }

        internal void Blade(float sign, float handedness, int tipMaterial)
        {
            // Cambered airfoil sections twist from root to tip. Counter-rotating
            // pairs use the mirrored pitch while retaining the existing rotor pivots.
            var radii = new[] { .014f, .026f, .050f, .080f, .108f, .128f, .137f };
            var chords = new[] { .010f, .019f, .027f, .025f, .019f, .012f, .003f };
            var sweeps = new[] { 0f, -.001f, -.004f, -.008f, -.014f, -.020f, -.023f };
            var pitches = new[] { 24f, 22f, 18f, 14f, 11f, 9f, 8f };
            var chordSamples = new[] { 0f, .08f, .24f, .47f, .73f, 1f };
            var points = new Vector3[radii.Length, chordSamples.Length * 2 - 2];
            var normals = new Vector3[radii.Length, chordSamples.Length * 2 - 2];
            var contour = points.GetLength(1);
            for (var section = 0; section < radii.Length; section++)
            for (var p = 0; p < contour; p++)
            {
                // Reverse the section traversal when mirroring the chord so both
                // rotor handednesses retain outward face winding and camber.
                var profile = handedness < 0 ? (contour - p) % contour : p;
                var upper = profile < chordSamples.Length;
                var t = chordSamples[upper ? profile : contour - profile];
                var chord = chords[section];
                var thickness = chord * .48f * (.2969f * Mathf.Sqrt(t) - .1260f * t
                    - .3516f * t * t + .2843f * t * t * t - .1036f * t * t * t * t);
                var camber = chord * .025f * Mathf.Sin(t * Mathf.PI);
                var pitch = pitches[section] * Mathf.Deg2Rad;
                var y = camber + (upper ? thickness : -thickness);
                var z = (t - .35f) * chord;
                points[section, p] = new Vector3(radii[section] * sign,
                    .001f + y * Mathf.Cos(pitch) - z * Mathf.Sin(pitch),
                    (sweeps[section] + y * Mathf.Sin(pitch) + z * Mathf.Cos(pitch)) * sign * handedness);
            }
            for (var section = 0; section < radii.Length; section++)
            for (var p = 0; p < contour; p++)
            {
                var along = points[Mathf.Min(section + 1, radii.Length - 1), p] - points[Mathf.Max(section - 1, 0), p];
                var across = points[section, (p + 1) % contour] - points[section, (p + contour - 1) % contour];
                normals[section, p] = Unit(Vector3.Cross(across, along));
            }
            for (var section = 0; section < radii.Length - 1; section++)
            for (var p = 0; p < contour; p++)
            {
                var next = (p + 1) % contour;
                var material = section >= radii.Length - 3 ? tipMaterial : 1;
                Quad(points[section, p], points[section, next], points[section + 1, next], points[section + 1, p], material,
                    normals[section, p], normals[section, next], normals[section + 1, next], normals[section + 1, p]);
            }
            for (var p = 1; p < contour - 1; p++)
            {
                Triangle(points[0, 0], points[0, p + 1], points[0, p], 1);
                Triangle(points[radii.Length - 1, 0], points[radii.Length - 1, p], points[radii.Length - 1, p + 1], tipMaterial);
            }
        }

        internal Material[] Apply(Mesh mesh, List<Material> materials)
        {
            mesh.indexFormat = _vertices.Count > 65535
                ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.vertices = _vertices.ToArray();
            mesh.normals = _normals.ToArray();
            mesh.uv = _uv.ToArray();
            var used = new List<Material>();
            var count = 0;
            foreach (var triangles in _triangles) if (triangles.Count > 0) count++;
            mesh.subMeshCount = count;
            var subMesh = 0;
            for (var i = 0; i < _triangles.Length; i++)
            {
                if (_triangles[i].Count == 0) continue;
                mesh.SetTriangles(_triangles[i].ToArray(), subMesh++);
                used.Add(materials[i]);
            }
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return used.ToArray();
        }
    }
}
