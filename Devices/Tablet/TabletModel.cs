// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Modstoke.Client;

internal sealed class ClientTabletModel : IDisposable
{
    internal const float DisplayScale = 0.88f;

    private readonly List<Material> _materials = new();
    private ClientTabletFinish? _finish;
    private Mesh? _mesh;
    private bool _disposed;

    internal GameObject Root { get; private set; } = null!;

    internal ClientTabletModel(Transform parent, float width, float height, int layer, Material? referenceMaterial)
    {
        if (!float.IsFinite(width) || width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (!float.IsFinite(height) || height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (!parent) throw new ArgumentNullException(nameof(parent));
        if (layer < 0 || layer > 31) throw new ArgumentOutOfRangeException(nameof(layer));

        try
        {
            Root = new GameObject("Field tablet casing") { layer = layer };
            Root.transform.SetParent(parent, false);
            var shader = Shader.Find("HDRP/Lit");
            AddMaterial(shader, referenceMaterial, "Dark olive instrument polymer", new Color(0.10f, 0.12f, 0.09f), 0f, 0.25f);
            AddMaterial(shader, referenceMaterial, "Charcoal coated hardware", new Color(0.055f, 0.065f, 0.060f), 0f, 0.25f);
            AddMaterial(shader, referenceMaterial, "Black elastomer guards", new Color(0.014f, 0.018f, 0.015f), 0f, 0.15f);
            AddMaterial(shader, referenceMaterial, "Recessed instrument controls", new Color(0.055f, 0.065f, 0.055f), 0f, 0.31f);
            _finish = new ClientTabletFinish(_materials[0], _materials[1], _materials[2], _materials[3]);

            var geometry = new CasingMesh();
            // Glass remains at Z=0. Only the annular front frame reaches towards the viewer.
            // Width/height retain the support-hand fit; the display occupies the smaller aperture.
            geometry.Housing(width, height);
            geometry.DisplaySurround(width, height);
            geometry.Shell(0, 0, width + 0.009f, height + 0.009f, 0.013f,
                0.0216f, 0.0234f, 0.0006f, 0, 2);

            foreach (var xSign in new[] { -1f, 1f })
            foreach (var ySign in new[] { -1f, 1f })
            {
                geometry.CornerGuard(width, height, xSign, ySign);
                geometry.Fastener(xSign * (width * 0.5f - 0.003f),
                    ySign * (height * 0.5f - 0.003f));
            }

            geometry.Controls(width, height);
            geometry.Speaker(-width * 0.08f, height * 0.5f + 0.0005f, Math.Min(0.066f, width * 0.36f));
            geometry.Sensor(width * 0.23f, height * 0.5f + 0.0005f);
            geometry.Recess(-width * 0.27f, -height * 0.5f - 0.0005f,
                0.022f, 0.009f, 0.004f, 0.002f);
            // Assembly breaks stop at the gasket. The long left edge stays free for the thumb.
            foreach (var sign in new[] { -1f, 1f })
            {
                geometry.Shell(sign * width * 0.31f, height * 0.5f + 0.0005f,
                    0.00055f, 0.014f, 0.00015f, -0.0037f, -0.0034f, 0.00005f, 2, 2);
                geometry.Shell(sign * width * 0.31f, -height * 0.5f - 0.0005f,
                    0.00055f, 0.014f, 0.00015f, -0.0037f, -0.0034f, 0.00005f, 2, 2);
                geometry.RearGrip(sign * width * 0.29f, height);
            }
            geometry.ServiceCover(width, height);
            geometry.PortCover(width * 0.5f + 0.012f, -height * 0.19f, height * 0.16f);
            geometry.PortCover(width * 0.5f + 0.012f, height * 0.15f, height * 0.22f);

            _mesh = new Mesh { name = "Rugged instrument tablet casing" };
            geometry.Apply(_mesh);
            Root.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var renderer = Root.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = _materials.ToArray();
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = true;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void AddMaterial(Shader? shader, Material? reference, string name, Color color, float metal, float smoothness)
    {
        var material = shader ? new Material(shader) : reference ? new Material(reference)
            : throw new InvalidOperationException("No lit material is available for the tablet casing.");
        _materials.Add(material);
        material.name = name;
        foreach (var property in new[] { "_BaseColorMap", "_BaseMap", "_MainTex" })
            if (material.HasProperty(property)) material.SetTexture(property, null);
        foreach (var property in new[] { "_BaseColor", "_Color" })
            if (material.HasProperty(property)) material.SetColor(property, color);
        if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", metal);
        if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", smoothness);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (Root)
        {
            Root.SetActive(false);
            UnityEngine.Object.Destroy(Root);
        }
        if (_mesh) UnityEngine.Object.Destroy(_mesh);
        _finish?.Dispose();
        _finish = null;
        foreach (var material in _materials)
            if (material) UnityEngine.Object.Destroy(material);
        _materials.Clear();
    }

    private sealed class CasingMesh
    {
        private readonly List<Vector3> _vertices = new();
        private readonly List<Vector3> _normals = new();
        private readonly List<Vector2> _uv = new();
        private readonly List<int>[] _triangles = { new(), new(), new(), new() };

        internal void Housing(float width, float height)
        {
            // A sealed split runs around the waist of the two assembled case halves.
            var rings = new[] {
                RoundedOctagon(width + 0.020f, height + 0.020f, 0.016f, 0.002f),
                RoundedOctagon(width + 0.024f, height + 0.024f, 0.018f, 0.004f),
                RoundedOctagon(width + 0.024f, height + 0.024f, 0.018f, 0.0125f),
                RoundedOctagon(width + 0.0235f, height + 0.0235f, 0.01775f, 0.0127f),
                RoundedOctagon(width + 0.0235f, height + 0.0235f, 0.01775f, 0.0132f),
                RoundedOctagon(width + 0.024f, height + 0.024f, 0.018f, 0.0134f),
                RoundedOctagon(width + 0.024f, height + 0.024f, 0.018f, 0.020f),
                RoundedOctagon(width + 0.020f, height + 0.020f, 0.016f, 0.022f)
            };
            Solid(0, 0, rings, 0, new[] { 0, 0, 2, 2, 2, 0, 0 });
        }

        internal void DisplaySurround(float width, float height)
        {
            // A rolled shoulder gives the front a graduated highlight without widening
            // the rail or moving the support-hand contact surface.
            var perimeter = new[] {
                RoundedOctagon(width + 0.014f, height + 0.014f, 0.014f, -0.0035f),
                RoundedOctagon(width + 0.018f, height + 0.018f, 0.016f, -0.0031f),
                RoundedOctagon(width + 0.0216f, height + 0.0216f, 0.0172f, -0.0018f),
                RoundedOctagon(width + 0.0234f, height + 0.0234f, 0.0178f, 0.0004f),
                RoundedOctagon(width + 0.024f, height + 0.024f, 0.018f, 0.004f)
            };
            JoinShoulder(perimeter);
            var displayWidth = width * DisplayScale;
            var displayHeight = height * DisplayScale;
            var shoulder = Octagon(displayWidth + 0.016f, displayHeight + 0.016f, 0.007f, -0.0035f);
            var crown = Octagon(displayWidth + 0.012f, displayHeight + 0.012f, 0.005f, -0.0042f);
            // The right-hand crown narrows beside the key pocket so its rising face
            // ends before the pocket's outer bevel begins, including the rounded ends.
            shoulder[0].x = shoulder[7].x = displayWidth * 0.5f + 0.0054f;
            crown[0].x = crown[7].x = displayWidth * 0.5f + 0.0052f;
            // The shortest supported casing also needs room below the speaker pocket.
            shoulder[1].y = shoulder[2].y = Math.Min(shoulder[1].y, height * 0.5f - 0.005f);
            var lip = Octagon(displayWidth + 0.010f, displayHeight + 0.010f, 0.004f, -0.0035f);
            var gasket = Octagon(displayWidth + 0.006f, displayHeight + 0.006f, 0.0025f, -0.0014f);
            // Even the clipped aperture corners clear the complete rectangular glass footprint.
            var opening = Octagon(displayWidth + 0.003f, displayHeight + 0.003f, 0.0015f, -0.0003f);
            // The molded crown separates the broad face from the inset rubber gasket.
            // Four samples at each outer corner collapse to the unchanged inner corner.
            // The display aperture and the key/speaker clearance edits remain exact.
            var shoulderSamples = new Vector3[perimeter[0].Length];
            for (var i = 0; i < shoulderSamples.Length; i++) shoulderSamples[i] = shoulder[i / 4];
            Join(perimeter[0], shoulderSamples, 0, reverse: true);
            Join(shoulder, crown, 0, reverse: true);
            Join(crown, lip, 0, reverse: true);
            Join(lip, gasket, 2, reverse: true);
            Join(gasket, opening, 3, reverse: true);
        }

        internal void CornerGuard(float width, float height, float xSign, float ySign)
        {
            var halfWidth = width * 0.5f;
            var halfHeight = height * 0.5f;
            var outer = new[] {
                GuardContour(0.013f, 0.021f, -0.005f),
                GuardContour(0.0145f, 0.022f, -0.001f),
                GuardContour(0.0145f, 0.022f, 0.021f),
                GuardContour(0.013f, 0.021f, 0.024f)
            };
            var inner = new[] {
                GuardContour(0.0055f, 0.0175f, -0.005f),
                GuardContour(0.0045f, 0.017f, -0.001f),
                GuardContour(0.0045f, 0.017f, 0.021f),
                GuardContour(0.0055f, 0.0175f, 0.024f)
            };
            Join(outer[0], inner[0], 2, reverse: true, closed: false);
            Join(outer[3], inner[3], 2, closed: false);
            for (var band = 0; band < 3; band++)
            {
                Join(outer[band], outer[band + 1], 2, closed: false);
                Join(inner[band], inner[band + 1], 2, reverse: true, closed: false);
                Quad(outer[band][0], outer[band + 1][0], inner[band + 1][0], inner[band][0], 2);
                Quad(outer[band][3], inner[band][3], inner[band + 1][3], outer[band + 1][3], 2);
            }

            // Short molded ribs live on the top/bottom guard flanges, clear of the
            // glass and the long left rail used by the supporting thumb.
            for (var i = 0; i < 3; i++)
                Shell(xSign * (halfWidth - 0.025f + i * 0.006f),
                    ySign * (halfHeight + 0.009f), 0.0022f, 0.006f, 0.0008f,
                    -0.0057f, -0.0047f, 0.00025f, 2, 2);

            Vector3[] GuardContour(float offset, float cut, float z)
            {
                var x = halfWidth + offset;
                var y = halfHeight + offset;
                var points = new[] {
                    new Vector3(x * xSign, (halfHeight - 0.026f) * ySign, z),
                    new Vector3(x * xSign, (y - cut) * ySign, z),
                    new Vector3((x - cut) * xSign, y * ySign, z),
                    new Vector3((halfWidth - 0.030f) * xSign, y * ySign, z)
                };
                if (xSign * ySign < 0) Array.Reverse(points);
                return points;
            }
        }

        internal void Fastener(float x, float y)
        {
            Shell(x, y, 0.008f, 0.008f, 0.0035f, -0.0047f, -0.0033f, 0.00025f, 3, 0);
            // The socket is an actual recessed hex with a bottom, not a dark strip
            // floating over a filled screw head. Twelve sectors are enough at this size.
            var head = new Vector3[12];
            var shoulder = new Vector3[12];
            var seat = new Vector3[12];
            var socket = new Vector3[12];
            var well = new Vector3[12];
            for (var i = 0; i < head.Length; i++)
            {
                var angle = i * Mathf.PI / 6;
                var direction = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0);
                head[i] = new Vector3(x, y, -.0054f) + direction * .0022f;
                shoulder[i] = new Vector3(x, y, -.00515f) + direction * .00245f;
                seat[i] = new Vector3(x, y, -.0045f) + direction * .00245f;
                var socketRadius = i % 2 == 0 ? .00125f : .00125f * .8660254f;
                socket[i] = new Vector3(x, y, -.0054f) + direction * socketRadius;
                well[i] = new Vector3(x, y, -.00495f) + direction * socketRadius;
            }
            Join(head, shoulder, 1, smooth: true);
            Join(shoulder, seat, 1, smooth: true);
            Join(head, socket, 1, reverse: true);
            Join(socket, well, 3, reverse: true);
            for (var i = 0; i < well.Length; i++)
                Triangle(new Vector3(x, y, well[0].z), well[(i + 1) % well.Length], well[i], 2);
        }

        internal void Controls(float width, float height)
        {
            // The pocket's protective rim stands above all three key caps; its left edge
            // stays beyond the display gasket. No controls protrude into the support grip.
            var x = width * DisplayScale * 0.5f + 0.0155f;
            Recess(x, 0, 0.020f, height * 0.45f, 0.0035f, 0.0022f);
            for (var i = -1; i <= 1; i++)
            {
                var y = i * height * 0.14f;
                Shell(x, y, 0.0134f, height * 0.10f, 0.0018f,
                    -0.00525f, -0.0039f, 0.00045f, 3, 2);
                Shell(x, y, 0.0056f, 0.0008f, 0.00025f,
                    -0.0055f, -0.0052f, 0.00008f, 1, 1);
                if (i > 0)
                    Shell(x, y, 0.0008f, 0.0056f, 0.00025f,
                        -0.0055f, -0.0052f, 0.00008f, 1, 1);
                if (i < 0)
                    foreach (var sign in new[] { -1f, 1f })
                        Shell(x, y + sign * 0.0021f, 0.0056f, 0.0008f, 0.00025f,
                            -0.0055f, -0.0052f, 0.00008f, 1, 1);
            }
        }

        internal void Speaker(float x, float y, float width)
        {
            Recess(x, y, width, 0.010f, 0.003f, 0.002f);
            // Keep the grille and sensor separate on the smaller inventory casing.
            var halfCount = (int)((width - 0.010f) / 0.0086f);
            for (var i = -halfCount; i <= halfCount; i++)
                Shell(x + i * 0.0043f, y, 0.0016f, 0.006f, 0.0005f,
                    -0.0051f, -0.0039f, 0.00025f, 0, 0);
        }

        internal void RearGrip(float x, float height)
        {
            // The same pad footprint and maximum depth retain the original grip envelope.
            Shell(x, 0, 0.022f, height * 0.60f, 0.007f,
                0.0232f, 0.02355f, 0.0001f, 2, 2);
            for (var i = -4; i <= 4; i++)
                Shell(x, i * height * 0.055f, 0.017f, 0.0014f, 0.00055f,
                    0.02345f, 0.024f, 0.00018f, 2, 2);
        }

        internal void ServiceCover(float width, float height)
        {
            // A gasketed access plate sits between the rear grip pads. Its hardware
            // stays below their contact plane and the existing corner guards.
            var panelWidth = width * 0.58f - 0.026f;
            var panelHeight = height * 0.65f;
            Shell(0, 0, panelWidth, panelHeight, 0.004f,
                0.0233f, 0.02355f, 0.00008f, 2, 2);
            Shell(0, 0, panelWidth - 0.0014f, panelHeight - 0.0014f, 0.0033f,
                0.0235f, 0.02375f, 0.00008f, 0, 0);
            foreach (var xSign in new[] { -1f, 1f })
            foreach (var ySign in new[] { -1f, 1f })
            {
                var x = xSign * (panelWidth * 0.5f - 0.004f);
                var y = ySign * (panelHeight * 0.5f - 0.004f);
                Shell(x, y, 0.0052f, 0.0052f, 0.0023f,
                    0.0237f, 0.02382f, 0.00004f, 3, 3);
                Shell(x, y, 0.0037f, 0.0037f, 0.00165f,
                    0.02379f, 0.02394f, 0.00005f, 1, 1);
                Shell(x, y, 0.0023f, 0.00055f, 0.00018f,
                    0.02391f, 0.02398f, 0.00002f, 2, 2);
            }

            // Molded battery identification, with a shallow outlined body and terminal.
            // This physical service marking is independent of the live screen UI.
            var labelY = -panelHeight * 0.22f;
            var outline = Ring(0, labelY, 0.009f, 0.005f, 0.0006f, 0.02380f);
            var inset = Ring(0, labelY, 0.0082f, 0.0042f, 0.0003f, 0.02380f);
            Join(outline, inset, 3);
            Shell(0.0048f, labelY, 0.0008f, 0.002f, 0.0002f,
                0.02375f, 0.02380f, 0.000015f, 3, 3);
            Shell(0, labelY, 0.0022f, 0.0004f, 0.0001f,
                0.02375f, 0.02380f, 0.000015f, 3, 3);
            Shell(0, labelY, 0.0004f, 0.0022f, 0.0001f,
                0.02375f, 0.02380f, 0.000015f, 3, 3);
        }

        internal void Sensor(float x, float y)
        {
            Recess(x, y, 0.019f, 0.008f, 0.0035f, 0.0015f);
            Shell(x - 0.004f, y, 0.0046f, 0.0046f, 0.0021f,
                -0.0048f, -0.0039f, 0.0002f, 1, 3);
            Shell(x - 0.004f, y, 0.003f, 0.003f, 0.0014f,
                -0.0050f, -0.0047f, 0.00008f, 3, 3);
            Shell(x + 0.004f, y, 0.0026f, 0.0026f, 0.0012f,
                -0.0048f, -0.0039f, 0.0002f, 3, 2);
        }

        internal void Recess(float x, float y, float width, float height, float radius, float rim)
        {
            var outer = Ring(x, y, width - 0.0008f, height - 0.0008f, radius - 0.0004f, -0.0058f);
            var opening = Ring(x, y, width - rim * 2, height - rim * 2,
                Math.Max(radius - rim, 0.0003f), -0.0058f);
            var well = Ring(x, y, width - rim * 2, height - rim * 2,
                Math.Max(radius - rim, 0.0003f), -0.0040f);
            Join(outer, Ring(x, y, width, height, radius, -0.0035f), 0);
            Join(outer, opening, 0, reverse: true);
            Join(opening, well, 2, reverse: true);
            for (var i = 0; i < well.Length; i++)
                Triangle(new Vector3(x, y, well[0].z), well[(i + 1) % well.Length], well[i], 2);
        }

        internal void PortCover(float side, float y, float length)
        {
            var start = _vertices.Count;
            // Model in the same local plane as the housing, then turn onto the right rail.
            // The shallow rim, rubber flap and pull tab project at most 1 mm from the rail.
            Shell(0, 0, 0.011f, length, 0.002f, -0.0003f, 0.001f, 0.0002f, 1, 0);
            Shell(0, 0, 0.0085f, length - 0.003f, 0.0015f, -0.0006f, 0.0002f, 0.0002f, 2, 2);
            Shell(0, -length * 0.28f, 0.0055f, 0.002f, 0.0006f,
                -0.00094f, -0.0004f, 0.00015f, 0, 2);
            // Integral tether hinge and two thumb grooves identify the flap's opening edge.
            Shell(0, length * 0.34f, 0.0064f, 0.0012f, 0.0004f,
                -0.00085f, -0.00045f, 0.0001f, 2, 2);
            foreach (var sign in new[] { -1f, 1f })
                Shell(sign * 0.0012f, -length * 0.28f, 0.00045f, 0.0014f, 0.00015f,
                    -0.001f, -0.0009f, 0.00003f, 3, 3);
            for (var i = start; i < _vertices.Count; i++)
            {
                var vertex = _vertices[i];
                var normal = _normals[i];
                // A proper rotation (not a reflection) preserves triangle winding.
                _vertices[i] = new Vector3(side - vertex.z, y + vertex.y, 0.013f + vertex.x);
                _normals[i] = new Vector3(-normal.z, normal.y, normal.x);
            }
        }

        internal void Shell(float x, float y, float width, float height, float radius,
            float front, float back, float bevel, int faceMaterial, int edgeMaterial)
        {
            // Four rounded rectangles create a closed solid with real front/back chamfers.
            bevel = Math.Min(bevel, Math.Min((back - front) * 0.45f, Math.Min(width, height) * 0.2f));
            radius = Math.Min(radius, Math.Min(width, height) * 0.45f);
            var rings = new[] {
                Ring(x, y, width - bevel * 2, height - bevel * 2, Math.Max(radius - bevel, 0), front),
                Ring(x, y, width, height, radius, front + bevel),
                Ring(x, y, width, height, radius, back - bevel),
                Ring(x, y, width - bevel * 2, height - bevel * 2, Math.Max(radius - bevel, 0), back)
            };
            Solid(x, y, rings, faceMaterial, new[] { edgeMaterial, edgeMaterial, edgeMaterial });
        }

        private void Join(Vector3[] a, Vector3[] b, int material, bool reverse = false, bool closed = true, bool smooth = false)
        {
            var count = closed ? a.Length : a.Length - 1;
            for (var i = 0; i < count; i++)
            {
                var next = (i + 1) % a.Length;
                if (smooth)
                {
                    var before = (i + a.Length - 1) % a.Length;
                    var after = (next + 1) % a.Length;
                    var face = Unit(Vector3.Cross(a[next] - a[i], b[next] - a[i]));
                    var n0 = Unit(Vector3.Cross(Unit(a[next] - a[i]) + Unit(a[i] - a[before]), b[i] - a[i]));
                    var n1 = Unit(Vector3.Cross(Unit(a[after] - a[next]) + Unit(a[next] - a[i]), b[next] - a[next]));
                    if (n0.sqrMagnitude == 0) n0 = face;
                    if (n1.sqrMagnitude == 0) n1 = face;
                    Triangle(a[i], a[next], b[next], material, n0, n1, n1);
                    Triangle(a[i], b[next], b[i], material, n0, n1, n0);
                }
                else if (reverse) Quad(a[i], b[i], b[next], a[next], material);
                else Quad(a[i], a[next], b[next], b[i], material);
            }
        }

        private void JoinShoulder(Vector3[][] rings)
        {
            // Share the normal at each profile level as well as around the outline.
            // This is one molded roll; the separate gasket and front crown stay sharp.
            Vector3 Normal(int row, int index)
            {
                var ring = rings[row];
                var before = (index + ring.Length - 1) % ring.Length;
                var after = (index + 1) % ring.Length;
                var tangent = Unit(ring[index] - ring[before]) + Unit(ring[after] - ring[index]);
                if (row == 0) return Vector3.back;
                var profile = row == rings.Length - 1 ? Vector3.forward
                    : Unit(rings[row][index] - rings[row - 1][index])
                        + Unit(rings[row + 1][index] - rings[row][index]);
                return Unit(Vector3.Cross(tangent, profile));
            }
            for (var row = 0; row < rings.Length - 1; row++)
            for (var i = 0; i < rings[row].Length; i++)
            {
                var next = (i + 1) % rings[row].Length;
                var a = Normal(row, i);
                var b = Normal(row, next);
                var c = Normal(row + 1, next);
                var d = Normal(row + 1, i);
                Triangle(rings[row][i], rings[row][next], rings[row + 1][next], 0, a, b, c);
                Triangle(rings[row][i], rings[row + 1][next], rings[row + 1][i], 0, a, c, d);
            }
        }

        private void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, int material)
        {
            Triangle(a, b, c, material);
            Triangle(a, c, d, material);
        }

        private static Vector3 Unit(Vector3 value)
        {
            var length = value.magnitude;
            return length > 0 ? value / length : Vector3.zero;
        }

        private static Vector3[] Octagon(float width, float height, float cut, float z)
        {
            var x = width * 0.5f;
            var y = height * 0.5f;
            return new[] {
                new Vector3(x, y - cut, z), new Vector3(x - cut, y, z),
                new Vector3(-x + cut, y, z), new Vector3(-x, y - cut, z),
                new Vector3(-x, -y + cut, z), new Vector3(-x + cut, -y, z),
                new Vector3(x - cut, -y, z), new Vector3(x, -y + cut, z)
            };
        }

        private static Vector3[] RoundedOctagon(float width, float height, float cut, float z)
        {
            var corners = Octagon(width, height, cut, z);
            var result = new Vector3[corners.Length * 4];
            // Quadratic corner rolls remain inside the original clipped silhouette.
            // Only its eight direction changes gain samples; long rails stay single faces.
            for (var i = 0; i < corners.Length; i++)
            {
                var corner = corners[i];
                var a = corner + (corners[(i + 7) % 8] - corner).normalized * .002f;
                var b = corner + (corners[(i + 1) % 8] - corner).normalized * .002f;
                for (var step = 0; step < 4; step++)
                {
                    var t = step / 3f;
                    result[i * 4 + step] = (1 - t) * (1 - t) * a + 2 * t * (1 - t) * corner + t * t * b;
                }
            }
            return result;
        }

        private void Solid(float x, float y, Vector3[][] rings, int faceMaterial, int[] bandMaterials)
        {
            var frontRing = rings[0];
            var backRing = rings[rings.Length - 1];
            for (var i = 0; i < frontRing.Length; i++)
            {
                var next = (i + 1) % frontRing.Length;
                Triangle(new Vector3(x, y, frontRing[0].z), frontRing[next], frontRing[i], faceMaterial);
                Triangle(new Vector3(x, y, backRing[0].z), backRing[i], backRing[next], faceMaterial);
            }
            for (var band = 0; band < bandMaterials.Length; band++)
                Join(rings[band], rings[band + 1], bandMaterials[band], smooth: true);
        }

        private static Vector3[] Ring(float x, float y, float width, float height, float radius, float z)
        {
            var points = new Vector3[20];
            for (var corner = 0; corner < 4; corner++)
            for (var step = 0; step <= 4; step++)
            {
                var angle = (corner * 90f + step * 22.5f) * Mathf.Deg2Rad;
                var cx = x + (corner == 0 || corner == 3 ? 1 : -1) * (width * 0.5f - radius);
                var cy = y + (corner < 2 ? 1 : -1) * (height * 0.5f - radius);
                points[corner * 5 + step] = new Vector3(cx + Mathf.Cos(angle) * radius, cy + Mathf.Sin(angle) * radius, z);
            }
            return points;
        }

        private void Triangle(Vector3 a, Vector3 b, Vector3 c, int material,
            Vector3? normalA = null, Vector3? normalB = null, Vector3? normalC = null)
        {
            var normal = Vector3.Cross(b - a, c - a);
            var length = normal.magnitude;
            if (length == 0) return;
            // Unity's Normalize epsilon discards valid millimetre-scale bevel faces.
            normal /= length;
            var points = new[] { a, b, c };
            var normals = new[] { normalA ?? normal, normalB ?? normal, normalC ?? normal };
            for (var i = 0; i < 3; i++)
            {
                var vertex = points[i];
                _triangles[material].Add(_vertices.Count);
                _vertices.Add(vertex);
                _normals.Add(normals[i]);
                // Planar mapping on each face's dominant axis, in physical metres.
                _uv.Add(Math.Abs(normal.z) > 0.5f ? new Vector2(vertex.x, vertex.y)
                    : Math.Abs(normal.x) > Math.Abs(normal.y) ? new Vector2(vertex.z, vertex.y)
                    : new Vector2(vertex.x, vertex.z));
            }
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
