// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Modstoke.Client;

internal sealed class ClientDroneControllerModel : IDisposable
{
    internal const float DisplayScale = 0.88f;
    private const float DeckOffset = 0.009f;

    internal static Bounds VisualBounds(float width, float height)
    {
        // Includes the protective rails, stick caps and angled antenna tips. The
        // original left/bottom contact surfaces remain inside their existing bounds.
        var halfWidth = width * 0.5f + Math.Max(0.0145f, height * 0.035f);
        var lower = -height * 0.5f - 0.0145f;
        var upper = Math.Max(height * 0.5f + 0.0145f, height * 1.07f + DeckOffset);
        var front = Math.Min(-0.0058f, -height * 0.155f);
        var back = Math.Max(0.024f, height * 0.10f);
        return new Bounds(new Vector3(0, (lower + upper) * 0.5f, (front + back) * 0.5f),
            new Vector3(halfWidth * 2, upper - lower, back - front));
    }

    private readonly List<Material> _materials = new();
    private ClientTabletFinish? _finish;
    private Mesh? _mesh;
    private bool _disposed;

    internal GameObject Root { get; private set; } = null!;

    internal ClientDroneControllerModel(Transform parent, float width, float height, int layer, Material? referenceMaterial)
    {
        if (!float.IsFinite(width) || width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (!float.IsFinite(height) || height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (!parent) throw new ArgumentNullException(nameof(parent));
        if (layer < 0 || layer > 31) throw new ArgumentOutOfRangeException(nameof(layer));

        try
        {
            Root = new GameObject("Drone field controller casing") { layer = layer };
            Root.transform.SetParent(parent, false);
            var shader = Shader.Find("HDRP/Lit");
            AddMaterial(shader, referenceMaterial, "Gunmetal controller polymer", new Color(0.064f, 0.070f, 0.074f), 0f, 0.25f);
            AddMaterial(shader, referenceMaterial, "Charcoal controller hardware", new Color(0.105f, 0.115f, 0.123f), 0.30f, 0.32f);
            AddMaterial(shader, referenceMaterial, "Black controller elastomer", new Color(0.013f, 0.016f, 0.019f), 0f, 0.15f);
            AddMaterial(shader, referenceMaterial, "Recessed controller controls", new Color(0.048f, 0.055f, 0.060f), 0f, 0.27f);
            _finish = new ClientTabletFinish(_materials[0], _materials[1], _materials[2], _materials[3]);

            var geometry = new CasingMesh();
            // Glass remains at Z=0, with the physical controls above its aperture.
            // Width/height retain the support-hand fit; the display occupies the smaller aperture.
            geometry.Housing(width, height);
            geometry.DisplaySurround(width, height);
            geometry.Shell(0, 0, width + 0.009f, height + 0.009f, 0.013f,
                0.0216f, 0.0234f, 0.0006f, 0, 2);

            foreach (var xSign in new[] { -1f, 1f })
            {
                geometry.CornerGuard(width, height, xSign, -1);
                geometry.Fastener(xSign * (width * 0.5f - 0.003f),
                    -height * 0.5f + 0.003f);
            }

            geometry.ControlDeck(width, height);
            geometry.Recess(0, -height * 0.5f - 0.0005f,
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

            _mesh = new Mesh { name = "Angular drone field controller casing" };
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
            : throw new InvalidOperationException("No lit material is available for the drone controller casing.");
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
            // Retain the fitted display crown and exact existing glass aperture.
            shoulder[0].x = shoulder[7].x = displayWidth * 0.5f + 0.0054f;
            crown[0].x = crown[7].x = displayWidth * 0.5f + 0.0052f;
            shoulder[1].y = shoulder[2].y = Math.Min(shoulder[1].y, height * 0.5f - 0.005f);
            var lip = Octagon(displayWidth + 0.010f, displayHeight + 0.010f, 0.004f, -0.0035f);
            var gasket = Octagon(displayWidth + 0.006f, displayHeight + 0.006f, 0.0025f, -0.0014f);
            // Even the clipped aperture corners clear the complete rectangular glass footprint.
            var opening = Octagon(displayWidth + 0.003f, displayHeight + 0.003f, 0.0015f, -0.0003f);
            // The molded crown separates the broad face from the inset rubber gasket.
            // Four samples at each outer corner collapse to the unchanged inner corner.
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

        internal void ControlDeck(float width, float height)
        {
            // This separate controller grows above the screen. Its angular control deck
            // and antennas never enter the glass aperture or the left/bottom hand fit.
            var start = _vertices.Count;
            var deckY = height * 0.665f;
            var rings = new[] {
                DeckRing(0.012f, -0.025f), DeckRing(0, -0.013f),
                DeckRing(0, 0.088f), DeckRing(0.012f, 0.10f)
            };
            Solid(0, deckY, rings, 0, new[] { 0, 0, 0 }, smoothEdges: false);

            // The central raised slab narrows into a V between the two control banks.
            var outline = new[] {
                new Vector2(width * 0.14f, height * 0.795f),
                new Vector2(-width * 0.14f, height * 0.795f),
                new Vector2(-width * 0.08f, height * 0.64f),
                new Vector2(0, height * 0.535f),
                new Vector2(width * 0.08f, height * 0.64f)
            };
            var face = new Vector3[outline.Length];
            var back = new Vector3[outline.Length];
            var center = new Vector3(0, height * 0.69f, Slope(height * 0.69f));
            for (var i = 0; i < outline.Length; i++)
            {
                face[i] = new Vector3(outline[i].x, outline[i].y, Slope(outline[i].y));
                back[i] = new Vector3(outline[i].x, outline[i].y, -height * 0.022f);
            }
            for (var i = 0; i < face.Length; i++)
                Triangle(center, face[(i + 1) % face.Length], face[i], 0);
            Join(face, back, 0);

            // Five shallow grille slots follow the slab's actual slope.
            for (var i = -2; i <= 2; i++)
            {
                var x = i * height * 0.017f;
                var y0 = height * 0.70f;
                var y1 = height * 0.755f;
                var half = height * 0.003f;
                Quad(new Vector3(x - half, y0, Slope(y0) - height * 0.001f),
                    new Vector3(x - half, y1, Slope(y1) - height * 0.001f),
                    new Vector3(x + half, y1, Slope(y1) - height * 0.001f),
                    new Vector3(x + half, y0, Slope(y0) - height * 0.001f), 2);
            }

            foreach (var sign in new[] { -1f, 1f })
            {
                Joystick(sign * (width * 0.5f - height * 0.14f), height * 0.715f, height);
                RotaryKnob(sign * width * 0.22f, height * 0.695f, height);
                RoundButton(sign * (width * 0.5f - height * 0.15f), height * 0.565f, height);
                RoundButton(sign * (width * 0.5f - height * 0.28f), height * 0.565f, height);
                ToggleSwitch(sign * width * 0.21f, height * 0.575f, height);
                DeckFastener(sign * width * 0.30f, height * 0.79f, height);
                DeckFastener(sign * width * 0.46f, height * 0.535f, height);

                // Raised shoulder rails protect the sticks without covering the screen.
                var rail = new[] {
                    new Vector3(sign * (width * 0.5f + height * 0.005f), height * 0.61f, -height * 0.023f),
                    new Vector3(sign * (width * 0.5f + height * 0.005f), height * 0.735f, -height * 0.09f),
                    new Vector3(sign * (width * 0.5f - height * 0.055f), height * 0.80f, -height * 0.10f),
                    new Vector3(sign * (width * 0.5f - height * 0.30f), height * 0.80f, -height * 0.10f),
                    new Vector3(sign * (width * 0.5f - height * 0.30f), height * 0.80f, -height * 0.025f)
                };
                for (var i = 0; i < rail.Length - 1; i++)
                    Tube(rail[i], rail[i + 1], height * 0.012f, height * 0.012f, 1);
                Antenna(sign, width, height);
            }

            // The inherited glass rail has a fixed nine-millimetre shoulder. Anchor
            // above it while scaling all new hardware together for the inventory prop.
            for (var i = start; i < _vertices.Count; i += 3)
            {
                var normal = Unit(Vector3.Cross(_vertices[i + 1] - _vertices[i], _vertices[i + 2] - _vertices[i]));
                for (var j = 0; j < 3; j++)
                {
                    var vertex = _vertices[i + j];
                    vertex.y += DeckOffset;
                    _vertices[i + j] = vertex;
                    _uv[i + j] = Math.Abs(normal.z) > 0.5f ? new Vector2(vertex.x, vertex.y)
                        : Math.Abs(normal.x) > Math.Abs(normal.y) ? new Vector2(vertex.z, vertex.y)
                        : new Vector2(vertex.x, vertex.z);
                }
            }

            Vector3[] DeckRing(float inset, float z)
            {
                var ring = Octagon(width + height * (0.07f - inset * 2),
                    height * (0.32f - inset * 2), height * (0.06f - inset * 0.5f), height * z);
                for (var i = 0; i < ring.Length; i++) ring[i].y += deckY;
                return ring;
            }

            float Slope(float y) => -height * (0.029f + (y / height - 0.535f) * (0.046f / 0.26f));
        }

        private void Joystick(float x, float y, float scale)
        {
            Revolve(x, y, scale, new[] {
                new Vector2(0.073f, -0.040f), new Vector2(0.082f, -0.033f),
                new Vector2(0.082f, -0.023f)
            }, 1);
            Revolve(x, y, scale, new[] {
                new Vector2(0.067f, -0.042f), new Vector2(0.067f, -0.038f)
            }, 3);
            // Alternating radii form the rubber gaiter's concentric accordion folds.
            Revolve(x, y, scale, new[] {
                new Vector2(0.022f, -0.093f), new Vector2(0.030f, -0.085f),
                new Vector2(0.028f, -0.081f), new Vector2(0.048f, -0.077f),
                new Vector2(0.052f, -0.068f), new Vector2(0.046f, -0.064f),
                new Vector2(0.060f, -0.060f), new Vector2(0.063f, -0.050f),
                new Vector2(0.054f, -0.041f)
            }, 2);
            Tube(new Vector3(x, y, -scale * 0.09f), new Vector3(x, y, -scale * 0.129f),
                scale * 0.011f, scale * 0.012f, 1);
            Revolve(x, y, scale, new[] {
                new Vector2(0.025f, -0.153f), new Vector2(0.037f, -0.151f),
                new Vector2(0.041f, -0.143f), new Vector2(0.039f, -0.131f),
                new Vector2(0.025f, -0.125f)
            }, 2, frontInset: scale * 0.004f);
        }

        private void RotaryKnob(float x, float y, float scale)
        {
            Revolve(x, y, scale, new[] {
                new Vector2(0.035f, -0.080f), new Vector2(0.043f, -0.074f),
                new Vector2(0.043f, -0.046f), new Vector2(0.046f, -0.040f),
                new Vector2(0.046f, -0.026f)
            }, 3);
            for (var i = 0; i < 12; i++)
            {
                var angle = i * Mathf.PI / 6;
                var dx = Mathf.Cos(angle) * scale * 0.043f;
                var dy = Mathf.Sin(angle) * scale * 0.043f;
                Tube(new Vector3(x + dx, y + dy, -scale * 0.070f),
                    new Vector3(x + dx, y + dy, -scale * 0.049f), scale * 0.0023f, scale * 0.0023f, 2, 6);
            }
            Shell(x, y + scale * 0.017f, scale * 0.005f, scale * 0.023f, scale * 0.002f,
                -scale * 0.081f, -scale * 0.078f, scale * 0.0005f, 1, 1);
        }

        private void RoundButton(float x, float y, float scale)
        {
            Revolve(x, y, scale, new[] {
                new Vector2(0.023f, -0.038f), new Vector2(0.028f, -0.032f),
                new Vector2(0.028f, -0.023f)
            }, 1);
            Revolve(x, y, scale, new[] {
                new Vector2(0.017f, -0.050f), new Vector2(0.020f, -0.046f),
                new Vector2(0.020f, -0.035f)
            }, 2);
        }

        private void ToggleSwitch(float x, float y, float scale)
        {
            Revolve(x, y, scale, new[] {
                new Vector2(0.020f, -0.036f), new Vector2(0.024f, -0.030f),
                new Vector2(0.024f, -0.023f)
            }, 1);
            var stem = new Vector3(x, y, -scale * 0.036f);
            var tip = new Vector3(x, y + scale * 0.014f, -scale * 0.077f);
            Tube(stem, tip, scale * 0.005f, scale * 0.007f, 1, 8);
            Tube(tip - new Vector3(0, scale * 0.003f, -scale * 0.009f), tip,
                scale * 0.009f, scale * 0.009f, 2, 8);
        }

        private void DeckFastener(float x, float y, float height)
        {
            var start = _vertices.Count;
            var scale = height / 0.22f;
            Fastener(0, 0);
            for (var i = start; i < _vertices.Count; i++)
            {
                var vertex = _vertices[i];
                _vertices[i] = new Vector3(x + vertex.x * scale, y + vertex.y * scale,
                    -height * 0.031f + (vertex.z + 0.0054f) * scale);
                _uv[i] = new Vector2(_vertices[i].x, _vertices[i].y);
            }
        }

        private void Antenna(float sign, float width, float height)
        {
            var x = sign * (width * 0.5f - height * 0.055f);
            var a = new Vector3(x, height * 0.805f, height * 0.04f);
            var b = new Vector3(x + sign * height * 0.005f, height * 0.86f, height * 0.04f);
            var c = new Vector3(x + sign * height * 0.016f, height * 0.94f, height * 0.04f);
            var d = new Vector3(x + sign * height * 0.037f, height * 1.035f, height * 0.038f);
            var tip = new Vector3(x + sign * height * 0.045f, height * 1.06f, height * 0.036f);
            Tube(a, b, height * 0.030f, height * 0.022f, 1);
            Tube(b, c, height * 0.015f, height * 0.011f, 2);
            Tube(c, d, height * 0.011f, height * 0.008f, 2);
            Tube(d, tip, height * 0.008f, height * 0.007f, 2);

            // The base has a flexible ribbed strain relief, with collars at the two
            // tapered shaft joints. Both aerials lean away from the centerline.
            var axis = Unit(c - b);
            for (var i = 0; i < 4; i++)
            {
                var center = b + (c - b) * (0.08f + i * 0.13f);
                var radius = height * (0.020f - i * 0.0014f);
                Tube(center - axis * height * 0.004f, center + axis * height * 0.004f, radius, radius, 2);
            }
            Tube(c - axis * height * 0.005f, c + axis * height * 0.005f, height * 0.013f, height * 0.013f, 3);
            var upperAxis = Unit(tip - d);
            Tube(d - upperAxis * height * 0.004f, d + upperAxis * height * 0.004f,
                height * 0.010f, height * 0.010f, 3);
        }

        private void Revolve(float x, float y, float scale, Vector2[] profile, int material, float frontInset = 0)
        {
            var rings = new Vector3[profile.Length][];
            const int segments = 16;
            for (var row = 0; row < profile.Length; row++)
            {
                rings[row] = new Vector3[segments];
                for (var i = 0; i < segments; i++)
                {
                    var angle = i * Mathf.PI * 2 / segments;
                    rings[row][i] = new Vector3(x + Mathf.Cos(angle) * profile[row].x * scale,
                        y + Mathf.Sin(angle) * profile[row].x * scale, profile[row].y * scale);
                }
            }
            var front = new Vector3(x, y, rings[0][0].z + frontInset);
            var back = new Vector3(x, y, rings[rings.Length - 1][0].z);
            for (var i = 0; i < segments; i++)
            {
                var next = (i + 1) % segments;
                Triangle(front, rings[0][next], rings[0][i], material);
                Triangle(back, rings[rings.Length - 1][i], rings[rings.Length - 1][next], material);
            }
            for (var row = 0; row < rings.Length - 1; row++) Join(rings[row], rings[row + 1], material, smooth: true);
        }

        private void Tube(Vector3 start, Vector3 end, float startRadius, float endRadius, int material, int segments = 12)
        {
            var axis = Unit(end - start);
            var u = Unit(Vector3.Cross(axis, Vector3.forward));
            if (u.sqrMagnitude == 0) u = Vector3.right;
            var v = Vector3.Cross(axis, u);
            var a = new Vector3[segments];
            var b = new Vector3[segments];
            for (var i = 0; i < segments; i++)
            {
                var angle = i * Mathf.PI * 2 / segments;
                var radial = u * Mathf.Cos(angle) + v * Mathf.Sin(angle);
                a[i] = start + radial * startRadius;
                b[i] = end + radial * endRadius;
            }
            Join(a, b, material, smooth: true);
            for (var i = 0; i < segments; i++)
            {
                var next = (i + 1) % segments;
                Triangle(start, a[next], a[i], material);
                Triangle(end, b[i], b[next], material);
            }
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

        private void Solid(float x, float y, Vector3[][] rings, int faceMaterial, int[] bandMaterials, bool smoothEdges = true)
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
                Join(rings[band], rings[band + 1], bandMaterials[band], smooth: smoothEdges);
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
            mesh.indexFormat = _vertices.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
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
