// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;
using NumericsRotation = System.Numerics.Quaternion;
using NumericsVector = System.Numerics.Vector3;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftModel
{
    private void InitializeFlightControlBoot(Transform housing, Vector3 pivot, Material material)
    {
        using var stream = FlightControlResource("boot.msa");
        var data = SpacecraftMeshData.Read(stream, "boot");
        var mesh = new Mesh { name = "Flight control flexible boot" };
        _flightControlMeshes.Add(mesh);
        _flightControlBoot = new FlightControlBoot(mesh, data);
        AddMeshPart(mesh.name, mesh, material, housing, pivot, Quaternion.identity);
    }

    private sealed class FlightControlBoot
    {
        private readonly record struct Vertex(NumericsVector Position, NumericsVector Normal,
            NumericsVector Tangent, float Handedness, float Weight, float Slope);

        private readonly Mesh _mesh;
        private readonly Vertex[] _rest;
        private readonly Il2CppStructArray<Vector3> _positions;
        private readonly Il2CppStructArray<Vector3> _normals;
        private readonly Il2CppStructArray<Vector4> _tangents;
        private float _pitch, _roll;

        internal FlightControlBoot(Mesh mesh, SpacecraftMeshData data)
        {
            _mesh = mesh;
            // MSA stores triangle corners. Weld only identical surface attributes
            // so the animated boot does not deform each shared corner repeatedly.
            var indices = new Dictionary<(Vector3 Position, Vector3 Normal, Vector2 Uv), int>();
            var corners = new List<(Vector3 Position, Vector3 Normal, Vector2 Uv)>();
            var triangles = new int[data.Vertices.Length];
            for (var i = 0; i < triangles.Length; i++)
            {
                var key = (data.Vertices[i], data.Normals[i], data.Uv[i]);
                if (!indices.TryGetValue(key, out var index))
                {
                    index = corners.Count;
                    indices.Add(key, index);
                    corners.Add(key);
                }
                triangles[i] = index;
            }

            _rest = new Vertex[corners.Count];
            _positions = new Il2CppStructArray<Vector3>(corners.Count);
            _normals = new Il2CppStructArray<Vector3>(corners.Count);
            var uv = new Vector2[corners.Count];
            var radiusSquared = 0f;
            for (var i = 0; i < corners.Count; i++)
            {
                var corner = corners[i];
                _positions[i] = corner.Position;
                _normals[i] = corner.Normal.normalized;
                uv[i] = corner.Uv;
                radiusSquared = Mathf.Max(radiusSquared, corner.Position.sqrMagnitude);
            }
            mesh.MarkDynamic();
            mesh.indexFormat = corners.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.vertices = _positions;
            mesh.normals = _normals;
            mesh.uv = uv;
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateTangents();
            _tangents = mesh.tangents;
            // Every displaced point is a convex blend of p and Qp, so this
            // radius bounds every angle without a per-frame bounds scan.
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * (2 * Mathf.Sqrt(radiusSquared)));
            for (var i = 0; i < corners.Count; i++)
            {
                var corner = corners[i];
                // Metres above the gimbal: fixed flange through 8 mm, fully
                // grip-bound cuff from 37 mm. Both ends have zero shear slope.
                var t = Mathf.Clamp01((corner.Position.y - .008f) / .029f);
                var weight = t * t * (3 - 2 * t);
                var slope = 6 * t * (1 - t) / .029f;
                var tangent = _tangents[i];
                _rest[i] = new Vertex(new(corner.Position.x, corner.Position.y, corner.Position.z),
                    NumericsVector.Normalize(new(corner.Normal.x, corner.Normal.y, corner.Normal.z)),
                    new(tangent.x, tangent.y, tangent.z), tangent.w, weight, slope);
            }
        }

        internal void Update(float pitch, float roll)
        {
            if (pitch == _pitch && roll == _roll) return;
            _pitch = pitch;
            _roll = roll;
            var rotation = NumericsRotation.CreateFromAxisAngle(NumericsVector.UnitZ, roll * (MathF.PI / 180))
                * NumericsRotation.CreateFromAxisAngle(NumericsVector.UnitX, pitch * (MathF.PI / 180));
            var dx = NumericsVector.Transform(NumericsVector.UnitX, rotation) - NumericsVector.UnitX;
            var dy = NumericsVector.Transform(NumericsVector.UnitY, rotation) - NumericsVector.UnitY;
            var dz = NumericsVector.Transform(NumericsVector.UnitZ, rotation) - NumericsVector.UnitZ;
            for (var i = 0; i < _rest.Length; i++)
            {
                var v = _rest[i];
                if (v.Weight == 0) continue;
                var displacement = dx * v.Position.X + dy * v.Position.Y + dz * v.Position.Z;
                var position = v.Position + v.Weight * displacement;
                var x = NumericsVector.UnitX + v.Weight * dx;
                var y = NumericsVector.UnitY + v.Weight * dy + v.Slope * displacement;
                var z = NumericsVector.UnitZ + v.Weight * dz;
                // The bounded stick travel keeps this deformation orientation
                // preserving. Cofactors give the inverse-transpose normal;
                // dividing by its positive determinant cancels on normalization.
                var normal = NumericsVector.Normalize(NumericsVector.Cross(y, z) * v.Normal.X +
                    NumericsVector.Cross(z, x) * v.Normal.Y + NumericsVector.Cross(x, y) * v.Normal.Z);
                var tangent = x * v.Tangent.X + y * v.Tangent.Y + z * v.Tangent.Z;
                tangent = NumericsVector.Normalize(tangent - normal * NumericsVector.Dot(normal, tangent));
                _positions[i] = new Vector3 { x = position.X, y = position.Y, z = position.Z };
                _normals[i] = new Vector3 { x = normal.X, y = normal.Y, z = normal.Z };
                _tangents[i] = new Vector4 { x = tangent.X, y = tangent.Y, z = tangent.Z, w = v.Handedness };
            }
            _mesh.vertices = _positions;
            _mesh.normals = _normals;
            _mesh.tangents = _tangents;
        }
    }
}
