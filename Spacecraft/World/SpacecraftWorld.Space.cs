// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftWorld
{
    private void BuildCelestialBodies()
    {
        _stars = Child("Inertial stars", _spaceSky!.transform);
        BuildStars();
        _celestialRoot = Child("Solar system", _spaceSky.transform);
        _celestialSphere = CelestialSphere();
        var bodies = OrbitalCatalog.Current.Bodies;
        _celestialBodies = new CelestialBody[bodies.Count];
        _celestialPositions = new OrbitalVector[bodies.Count];
        _celestialDistances = new double[bodies.Count];
        _celestialOrder = new int[bodies.Count];
        for (int i = 0; i < bodies.Count; i++)
        {
            _celestialBodies[i] = new(bodies[i]);
            _celestialOrder[i] = i;
            if (bodies[i].Id == 10) _sunIndex = i;
            // Prepare the two mapped surfaces during the existing destination warmup.
            if (bodies[i].Id is 399 or 301) PrepareCelestial(_celestialBodies[i]);
        }
    }

    private void BuildStars()
    {
        var groups = new[] { new Geometry(), new Geometry(), new Geometry() };
        for (var i = 0; i < 1800; i++)
        {
            var y = 2 * Hash(i, 307) - 1;
            var longitude = Hash(i, 311) * 2 * MathF.PI;
            var radial = MathF.Sqrt(1 - y * y);
            var direction = new Vector3(radial * MathF.Cos(longitude), y, radial * MathF.Sin(longitude));
            var right = Vector3.Cross(direction, MathF.Abs(y) > .95f ? Vector3.right : Vector3.up).normalized;
            var up = Vector3.Cross(right, direction);
            var size = .00022f + .00068f * MathF.Pow(Hash(i, 313), 6);
            var center = direction * .96f;
            right *= size;
            up *= size;
            // Quads face inward and share three materials, avoiding thousands
            // of renderers or lights for a camera-centered star field.
            groups[i % groups.Length].Quad(center - right - up, center + right - up,
                center + right + up, center - right + up);
        }
        var colors = new[] { new Color(.78f, .88f, 1), new Color(.38f, .50f, .75f), new Color(1, .75f, .48f) };
        for (var i = 0; i < groups.Length; i++)
        {
            var group = groups[i];
            var mesh = CreateMesh("Distant stars", group.Vertices.ToArray(), group.Triangles.ToArray());
            AttachMesh("Distant stars", _stars!.transform, mesh,
                Display("Starlight", colors[i]), false, false);
        }
    }

    private Mesh CelestialSphere()
    {
        const int columns = 128;
        const int rows = 64;
        var vertices = new Vector3[(columns + 1) * (rows + 1)];
        var uv = new Vector2[vertices.Length];
        var triangles = new int[columns * rows * 6];
        var t = 0;
        for (var row = 0; row <= rows; row++)
        {
            var latitude = row * MathF.PI / rows;
            for (var column = 0; column <= columns; column++)
            {
                var longitude = column * 2 * MathF.PI / columns;
                var index = row * (columns + 1) + column;
                vertices[index] = new Vector3(MathF.Sin(latitude) * MathF.Cos(longitude),
                    MathF.Cos(latitude), -MathF.Sin(latitude) * MathF.Sin(longitude));
                uv[index] = new Vector2(column / (float)columns, 1 - row / (float)rows);
                if (row == rows || column == columns) continue;
                triangles[t++] = index; triangles[t++] = index + columns + 1; triangles[t++] = index + 1;
                triangles[t++] = index + 1; triangles[t++] = index + columns + 1; triangles[t++] = index + columns + 2;
            }
        }
        _celestialVertices = vertices;
        return CreateMesh("Celestial sphere", vertices, triangles, uv, vertices);
    }
}
