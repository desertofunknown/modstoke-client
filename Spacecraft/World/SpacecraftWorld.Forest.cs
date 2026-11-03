// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftWorld
{
    // Original arrangement inspired by visible terrain and plants, not a named
    // canonical location. All positions and mesh recipes are client deterministic.
    private readonly record struct WetBasin(Vector2 Center, Vector2 Radius, float Level);
    private static readonly WetBasin[] Basins =
    {
        new(new Vector2(-110, 70), new Vector2(37, 29), -.25f),
        new(new Vector2(98, 92), new Vector2(43, 34), -.8f),
        new(new Vector2(-65, -110), new Vector2(30, 24), -.45f)
    };

    private static float BasinDistance(Vector2 p, WetBasin basin)
    {
        var x = (p.x - basin.Center.x) / basin.Radius.x;
        var z = (p.y - basin.Center.y) / basin.Radius.y;
        return Mathf.Sqrt(x * x + z * z);
    }

    private static float Height(float x, float z)
    {
        var radius = Mathf.Sqrt(x * x + z * z);
        if (radius <= LandingRadius) return 0;
        var clear = Smooth(LandingRadius, 65, radius);
        var broad = 1.3f * Mathf.Sin(x * .024f) + .9f * Mathf.Cos(z * .032f)
            + .65f * Mathf.Sin((x + z) * .041f);
        var shelf = 7 * Mound(x - 16, z + 132, 60);
        // Offset fronts and broken crests leave open saddles between three
        // escarpments. None follows the circular edge of the play footprint.
        var northFront = z - (146 + 12 * Mathf.Sin(x * .031f) + 7 * Mathf.Sin(x * .083f + .6f));
        var north = Smooth(0, 29, northFront) * Smooth(-190, -150, x) * (1 - Smooth(112, 175, x))
            * (44 + 13 * Mathf.Sin(x * .039f + .7f) + 6 * Mathf.Sin(x * .113f));
        var eastFront = x - (153 + 10 * Mathf.Sin(z * .037f + .5f));
        var east = Smooth(0, 26, eastFront) * Smooth(19, 65, z) * (1 - Smooth(152, 195, z))
            * (43 + 14 * Mathf.Cos(z * .057f) + 5 * Mathf.Sin(z * .147f));
        var westFront = -x - (166 + 8 * Mathf.Sin(z * .051f));
        var west = Smooth(0, 24, westFront) * Smooth(-172, -128, z) * (1 - Smooth(-31, 14, z))
            * (27 + 9 * Mathf.Sin(z * .063f + .4f));
        var escarpment = Mathf.Max(north, Mathf.Max(east, west));
        var strata = (1.15f * Mathf.Sin(escarpment * .73f + x * .031f)
            + .55f * Mathf.Sin(x * .51f + z * .09f) * Mathf.Cos(z * .38f)) * Smooth(7, 24, escarpment);
        var height = (broad + shelf + escarpment + strata
            + .12f * Mathf.Sin(x * .19f) * Mathf.Cos(z * .23f)) * clear;
        var point = new Vector2(x, z);
        foreach (var basin in Basins)
        {
            var distance = BasinDistance(point, basin);
            if (distance >= 1.12f) continue;
            var floor = basin.Level - .49f + .07f * Mathf.Sin(x * .13f) * Mathf.Cos(z * .16f);
            height = Mathf.Lerp(height, floor, 1 - Smooth(.77f, 1.12f, distance));
        }
        return height;
    }

    private Material Surface(Shader shader, string label, string asset, int size)
    {
        var material = Lit(shader, label, Color.white, .5f);
        material.SetTexture("_BaseColorMap", LoadTexture(asset, size, size, TextureWrapMode.Repeat));
        material.SetTexture("_NormalMap", LoadTexture(asset + "-normal", size, size, TextureWrapMode.Repeat, true));
        material.SetTexture("_MaskMap", LoadTexture(asset + "-mask", size, size, TextureWrapMode.Repeat, true));
        material.SetFloat("_NormalMapSpace", 0);
        material.SetFloat("_NormalScale", .8f);
        material.SetFloat("_MetallicRemapMin", 0);
        material.SetFloat("_MetallicRemapMax", 0);
        material.SetFloat("_SmoothnessRemapMin", 0);
        material.SetFloat("_SmoothnessRemapMax", 1);
        material.SetFloat("_AORemapMin", 0);
        material.SetFloat("_AORemapMax", 1);
        material.EnableKeyword("_NORMALMAP_TANGENT_SPACE");
        material.EnableKeyword("_NORMALMAP");
        material.EnableKeyword("_MASKMAP");
        return material;
    }

    private void BuildSites(Shader shader)
    {
        var bark = Surface(shader, "Rain-darkened deadwood", "bark", 1024);
        var foliage = Lit(shader, "Dark wet fronds", new Color(.035f, .069f, .056f), .48f);
        var plant = LoadProp(shader, "plant", false);
        var stone = LoadProp(shader, "rock", true);
        var water = Lit(shader, "Shallow black-teal pools", new Color(.018f, .031f, .029f), .94f);
        var ripples = Lit(shader, "Quiet rain rings", new Color(.045f, .066f, .061f), .87f);
        var trunks = Batches(); var branches = Batches(); var leaves = Batches();

        for (var i = 0; i < 225; i++)
        {
            var angle = i * 2.399963f;
            var radius = 56 + 147 * Mathf.Sqrt(Hash(i, 71));
            var p = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
            if (NearRoute(p, 5.6f) || NearSite(p, 7) || Height(p.x, p.y) > 18) continue;
            var patch = .5f + .5f * Mathf.Sin(p.x * .051f + Mathf.Sin(p.y * .044f));
            if (patch < .2f && i % 3 != 0) continue;
            var group = Quadrant(p);
            Tree(trunks[group], branches[group], Ground(p), 6 + 18 * Hash(i, 73), .26f + .61f * Hash(i, 79), angle, i);
        }

        // Banks carry the densest low growth, with smaller scattered patches in
        // the woodland. Explicit exclusions leave walking corridors readable.
        for (var i = 0; i < 600; i++)
        {
            Vector2 p;
            var angle = i * 2.399963f;
            if (i < 420)
            {
                var basin = Basins[i % Basins.Length];
                var ring = .99f + .25f * Hash(i, 83);
                p = basin.Center + new Vector2(Mathf.Cos(angle) * basin.Radius.x, Mathf.Sin(angle) * basin.Radius.y) * ring;
            }
            else if (i < 550)
            {
                var route = i % SurveySites.Length;
                var t = .43f + .54f * Hash(i, 83);
                var direction = (Route(route, t + .01f) - Route(route, t)).normalized;
                p = Route(route, t) + new Vector2(-direction.y, direction.x)
                    * ((i % 2 == 0 ? 1 : -1) * (4.4f + 6.5f * Hash(i, 89)));
            }
            else
            {
                var radius = 55 + 133 * Mathf.Sqrt(Hash(i, 89));
                p = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
            }
            var size = .9f + .9f * Hash(i, 97);
            var reach = size * 2.35f;
            if (p.magnitude < 47 || p.magnitude > 202 || NearRoute(p, 2.5f + reach)
                || NearSite(p, 3.5f + reach) || Height(p.x, p.y) > 20) continue;
            var position = Ground(p);
            Fern(leaves[Quadrant(p)], position, size, angle, i);
        }

        for (var basinIndex = 0; basinIndex < Basins.Length; basinIndex++)
        {
            var basin = Basins[basinIndex];
            var count = basinIndex == 1 ? 17 : 10;
            for (var i = 0; i < count; i++)
            {
                var seed = basinIndex * 97 + i;
                var angle = i * 2.399963f + basinIndex;
                var radius = .22f + .49f * Mathf.Sqrt(Hash(seed, 101));
                var p = basin.Center + new Vector2(Mathf.Cos(angle) * basin.Radius.x, Mathf.Sin(angle) * basin.Radius.y) * radius;
                if (NearRoute(p, 6) || NearSite(p, 8)) continue;
                var size = .8f + .9f * Hash(seed, 103);
                var position = new Vector3(p.x, basin.Level - size * .045f, p.y);
                PlaceProp(plant, "Ribbed wetland organism", position,
                    new Vector3(size, size * (1.35f + .22f * Hash(seed, 107)), size * (.9f + .14f * Hash(seed, 109))), angle);
            }
        }

        for (var i = 0; i < 98; i++)
        {
            var angle = i * 2.399963f + .6f;
            var radius = 57 + 140 * Mathf.Sqrt(Hash(i, 109));
            var p = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
            if (NearRoute(p, 5) || NearSite(p, 6) || InPool(p)) continue;
            PlaceRock(stone, p, 1.3f + 3.7f * Hash(i, 113), .45f + 1.7f * Hash(i, 127), angle);
        }
        for (var i = 0; i < 7; i++)
        {
            var p = SurveySites[2] + new Vector2(-21 + i * 6, -12 - 3 * Mathf.Sin(i));
            PlaceRock(stone, p, 5 + Hash(i, 131) * 2, 1.5f + .7f * Hash(i, 137), i * .33f);
        }
        foreach (var p in new[] { new Vector2(-123, 102), new Vector2(72, 125), new Vector2(-83, -137) })
        {
            var start = Ground(p) + new Vector3(0, .55f, 0);
            var end = Ground(p + new Vector2(12, 4)) + Vector3.up * .48f;
            Tube(branches[Quadrant(p)], Curve(start, (start + end) * .5f + Vector3.up * .5f, end, 12), .65f, .38f, 9);
        }

        for (var group = 0; group < 4; group++)
        {
            var suffix = " " + group;
            AttachGeometry("Crooked trunks" + suffix, _root!.transform, trunks[group], bark, true);
            AttachGeometry("Branches and roots" + suffix, _root.transform, branches[group], bark, false);
            AttachGeometry("Bank fronds" + suffix, _root.transform, leaves[group], foliage, false);
        }
        BuildWater(water, ripples);
        BuildBeacons(shader);
    }

    private static Geometry[] Batches() => new[] { new Geometry(), new Geometry(), new Geometry(), new Geometry() };
    private static int Quadrant(Vector2 p) => (p.x < 0 ? 1 : 0) + (p.y < 0 ? 2 : 0);
    private static bool InPool(Vector2 p)
    {
        foreach (var basin in Basins) if (BasinDistance(p, basin) < 1.04f) return true;
        return false;
    }

    private static Vector3[] Curve(Vector3 start, Vector3 bend, Vector3 end, int segments)
    {
        var points = new Vector3[segments + 1];
        for (var i = 0; i <= segments; i++)
        {
            var t = i / (float)segments;
            points[i] = start * ((1 - t) * (1 - t)) + bend * (2 * (1 - t) * t) + end * (t * t);
        }
        return points;
    }

    // Shared indexed rings and explicit radial normals keep trunks and basket
    // ribs smooth without welding unrelated leaves, branches or rock strata.
    private static void Tube(Geometry geometry, Vector3[] points, float startRadius, float endRadius, int sides)
    {
        var first = geometry.Vertices.Count;
        var axis = (points[1] - points[0]).normalized;
        var right = Vector3.Cross(axis, Mathf.Abs(axis.y) > .9f ? Vector3.right : Vector3.up).normalized;
        var length = 0f;
        for (var ring = 0; ring < points.Length; ring++)
        {
            if (ring > 0) length += (points[ring] - points[ring - 1]).magnitude;
            var tangent = (points[Mathf.Min(points.Length - 1, ring + 1)] - points[Mathf.Max(0, ring - 1)]).normalized;
            right = (right - tangent * Vector3.Dot(right, tangent)).normalized;
            var around = Vector3.Cross(tangent, right).normalized;
            var radius = Mathf.Lerp(startRadius, endRadius, ring / (float)(points.Length - 1));
            for (var side = 0; side <= sides; side++)
            {
                var angle = side * 2 * Mathf.PI / sides;
                var normal = right * Mathf.Cos(angle) + around * Mathf.Sin(angle);
                geometry.Vertices.Add(points[ring] + normal * radius);
                geometry.Normals.Add(normal);
                geometry.UV.Add(new Vector2(side / (float)sides * 2 * Mathf.PI * startRadius / .85f, length / 2.4f));
                if (ring == points.Length - 1 || side == sides) continue;
                var a = first + ring * (sides + 1) + side;
                var b = a + sides + 1;
                geometry.Triangles.Add(a); geometry.Triangles.Add(a + 1); geometry.Triangles.Add(b + 1);
                geometry.Triangles.Add(a); geometry.Triangles.Add(b + 1); geometry.Triangles.Add(b);
            }
        }
    }

    private void Tree(Geometry trunk, Geometry branches, Vector3 ground, float height, float radius, float angle, int seed)
    {
        var direction = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle));
        var across = new Vector3(-direction.z, 0, direction.x);
        var phase = Hash(seed, 173) * 2 * Mathf.PI;
        var lean = .07f + .13f * Hash(seed, 179);
        var line = new Vector3[12];
        for (var section = 0; section < line.Length; section++)
        {
            var t = section / 11f;
            line[section] = ground + Vector3.up * (height * t - .18f)
                + direction * height * (lean * t * t + .034f * Mathf.Sin(t * 7 + phase) * t)
                + across * height * (.045f * Mathf.Sin(t * 5 + phase) * t);
        }
        Tube(trunk, line, radius, radius * .09f, 8);
        var branchCount = 4 + Mathf.FloorToInt(Hash(seed, 181) * 3);
        for (var branch = 0; branch < branchCount; branch++)
        {
            var t = .24f + .60f * (branch + .25f + .45f * Hash(seed + branch, 191)) / branchCount;
            var attach = line[Mathf.RoundToInt(t * 11)];
            var turn = angle + branch * 2.399963f + Hash(seed + branch, 139) * 1.8f;
            var outwards = new Vector3(Mathf.Cos(turn), 0, Mathf.Sin(turn));
            var reach = height * (.12f + .14f * Hash(seed + branch, 149)) * (1 - t * .35f);
            var end = attach + outwards * reach + Vector3.up * reach * (-.28f + 1.05f * Hash(seed + branch, 193));
            var bend = attach + outwards * reach * .44f + Vector3.up * reach * .3f;
            var branchLine = Curve(attach, bend, end, 6);
            Tube(branches, branchLine, radius * (.38f - t * .23f), .014f, 6);
            if (branch % 2 == seed % 2)
            {
                var fork = branchLine[3];
                var forkEnd = fork + outwards * reach * .45f + across * reach * (branch % 2 == 0 ? .37f : -.37f)
                    + Vector3.up * reach * (.12f + .4f * Hash(seed + branch, 197));
                Tube(branches, Curve(fork, (fork + forkEnd) * .5f + Vector3.up * reach * .12f, forkEnd, 4), radius * .075f, .008f, 4);
            }
        }
        for (var root = 0; root < 5; root++)
        {
            var turn = angle + root * 2 * Mathf.PI / 5;
            var spread = new Vector3(Mathf.Cos(turn), 0, Mathf.Sin(turn)) * radius * 4;
            var middle = Ground(new Vector2(ground.x + spread.x * .5f, ground.z + spread.z * .5f), .12f);
            var end = Ground(new Vector2(ground.x + spread.x, ground.z + spread.z), -.1f);
            Tube(branches, Curve(ground + Vector3.up * .6f, middle, end, 6), radius * .42f, .025f, 6);
        }
    }

    private static void Leaf(Geometry geometry, Vector3 start, Vector3 tip, float width)
    {
        var direction = tip - start;
        var side = Vector3.Cross(direction, Vector3.up).normalized;
        var bend = Vector3.up * direction.magnitude * .15f;
        var firstCenter = Vector3.Lerp(start, tip, .33f) + bend * .88f;
        var secondCenter = Vector3.Lerp(start, tip, .70f) + bend * .84f;
        var points = new[] { start, firstCenter - side * width, firstCenter + side * width,
            secondCenter - side * width * .63f, secondCenter + side * width * .63f, tip };
        var front = new[] { 0, 2, 1, 1, 2, 4, 1, 4, 3, 3, 4, 5 };
        var centers = new[] { start, firstCenter, secondCenter, tip };
        // Shared stations give each leaf a bowed silhouette and changing normal;
        // the duplicated underside keeps it visible with ordinary opaque Lit.
        for (var face = 0; face < 2; face++)
        {
            var first = geometry.Vertices.Count;
            for (var vertex = 0; vertex < points.Length; vertex++)
            {
                var section = vertex == 0 ? 0 : vertex == 5 ? 3 : vertex < 3 ? 1 : 2;
                var tangent = centers[Mathf.Min(3, section + 1)] - centers[Mathf.Max(0, section - 1)];
                var normal = Vector3.Cross(side, tangent).normalized;
                geometry.Vertices.Add(points[vertex]);
                geometry.Normals.Add(face == 0 ? normal : -normal);
                geometry.UV.Add(new Vector2(section / 3f, vertex % 2));
            }
            for (var triangle = 0; triangle < front.Length; triangle += 3)
            {
                geometry.Triangles.Add(first + front[triangle]);
                geometry.Triangles.Add(first + front[triangle + (face == 0 ? 1 : 2)]);
                geometry.Triangles.Add(first + front[triangle + (face == 0 ? 2 : 1)]);
            }
        }
    }

    private static void Fern(Geometry geometry, Vector3 origin, float scale, float angle, int seed)
    {
        for (var frond = 0; frond < 4; frond++)
        {
            var turn = angle + frond * 1.57f + .4f * Hash(seed + frond, 199);
            var direction = new Vector3(Mathf.Cos(turn), 0, Mathf.Sin(turn));
            var side = new Vector3(-direction.z, 0, direction.x);
            var length = scale * (1.7f + Hash(seed + frond, 151) * .65f);
            var points = Curve(origin, origin + direction * length * .27f + Vector3.up * scale * 1.15f,
                origin + direction * length + Vector3.up * scale * .16f, 7);
            Tube(geometry, points, .012f * scale, .004f * scale, 3);
            for (var pair = 1; pair <= 5; pair++)
            {
                var stem = points[pair];
                var reach = scale * (.59f - pair * .068f) * (.9f + .2f * Hash(seed + pair + frond, 211));
                var sweep = direction * scale * (.23f + pair * .025f) - Vector3.up * scale * .07f;
                Leaf(geometry, stem, stem + side * reach + sweep, reach * .19f);
                Leaf(geometry, stem + direction * scale * .045f, stem - side * reach * .91f + sweep, reach * .18f);
            }
        }
    }

    private void BuildWater(Material water, Material rippleMaterial)
    {
        for (var index = 0; index < Basins.Length; index++)
        {
            var basin = Basins[index];
            var surface = new Geometry();
            for (var z = -40; z < 40; z += 2)
                for (var x = -48; x < 48; x += 2)
                {
                    var a = basin.Center + new Vector2(x, z);
                    var b = a + new Vector2(0, 2);
                    var c = a + new Vector2(2, 2);
                    var d = a + new Vector2(2, 0);
                    WaterTriangle(surface, basin, a, b, c);
                    WaterTriangle(surface, basin, a, c, d);
                }
            AttachGeometry("Shallow pool " + index, _root!.transform, surface, water, false, false);
            var rings = new Geometry();
            for (var ring = 0; ring < 17; ring++)
            {
                var angle = ring * 2.399963f;
                var p = basin.Center + new Vector2(Mathf.Cos(angle) * basin.Radius.x, Mathf.Sin(angle) * basin.Radius.y) * (.16f + .48f * Hash(ring, 163));
                var radius = .25f + .6f * Hash(ring, 167);
                for (var step = 0; step < 24; step++)
                {
                    var a = step * 2 * Mathf.PI / 24;
                    var b = (step + 1) * 2 * Mathf.PI / 24;
                    var center = new Vector3(p.x, basin.Level + .006f, p.y);
                    var innerA = center + new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * radius;
                    var innerB = center + new Vector3(Mathf.Cos(b), 0, Mathf.Sin(b)) * radius;
                    var outerA = center + new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * (radius + .018f);
                    var outerB = center + new Vector3(Mathf.Cos(b), 0, Mathf.Sin(b)) * (radius + .018f);
                    rings.Quad(innerA, innerB, outerB, outerA);
                }
            }
            AttachGeometry("Pool rain rings " + index, _root.transform, rings, rippleMaterial, false, false);
        }
    }

    private void WaterTriangle(Geometry geometry, WetBasin basin, Vector2 a, Vector2 b, Vector2 c)
    {
        if (BasinDistance(a, basin) > 1.04f || BasinDistance(b, basin) > 1.04f || BasinDistance(c, basin) > 1.04f) return;
        var input = new[] { Ground(a), Ground(b), Ground(c) };
        foreach (var point in input) if (basin.Level - point.y > .65f) return;
        var clipped = new Vector3[4];
        var count = 0;
        // Clip at the shore instead of keeping only whole grid cells. The water
        // is presentation over the collision floor, never a swimming surface.
        for (var edge = 0; edge < 3; edge++)
        {
            var start = input[edge];
            var end = input[(edge + 1) % 3];
            var startDepth = basin.Level - start.y - .025f;
            var endDepth = basin.Level - end.y - .025f;
            if (startDepth >= 0) clipped[count++] = new Vector3(start.x, basin.Level, start.z);
            if ((startDepth >= 0) == (endDepth >= 0)) continue;
            var crossing = Vector3.Lerp(start, end, startDepth / (startDepth - endDepth));
            clipped[count++] = new Vector3(crossing.x, basin.Level, crossing.z);
        }
        for (var triangle = 1; triangle < count - 1; triangle++)
            geometry.Triangle(clipped[0], clipped[triangle], clipped[triangle + 1]);
    }

    private void BuildBeacons(Shader shader)
    {
        var metal = Lit(shader, "Expedition marker metal", new Color(.15f, .17f, .16f), .35f);
        for (var i = 0; i < SurveySites.Length; i++)
        {
            var position = Ground(SurveySites[i]);
            _beaconPositions[i] = SpacecraftLayout.WorldOrigin + position + Vector3.up;
            var beacon = new Geometry();
            AddColumn(beacon, position, .34f, .23f, 0, 1);
            AddColumn(beacon, position + Vector3.up * .23f, .065f, 1.45f, 0, 1);
            AttachGeometry(SurveyNames[i] + " marker", _root!.transform, beacon, metal, true);
            var glow = new Geometry();
            AddColumn(glow, position + Vector3.up * 1.68f, .13f, .18f, 0, 1);
            var marker = Display("Survey marker " + (i + 1), new Color(.38f, .36f, .23f));
            _beaconMaterials[i] = marker;
            AttachGeometry(SurveyNames[i] + " signal", _root.transform, glow, marker, false, false);
        }
        var guides = new Geometry();
        for (var i = 0; i < 8; i++)
        {
            var angle = i * 2 * Mathf.PI / 8;
            AddColumn(guides, new Vector3(Mathf.Cos(angle) * 32, .03f, Mathf.Sin(angle) * 32), .12f, .10f, 0, 1);
        }
        for (var route = 0; route < SurveySites.Length; route++)
            for (var step = 5; step < 13; step++)
            {
                var p = Route(route, step / 13f);
                if (p.magnitude < 46) continue;
                var direction = (Route(route, step / 13f + .01f) - p).normalized;
                p += new Vector2(-direction.y, direction.x) * 2.9f;
                AddColumn(guides, Ground(p), .055f, .38f, 0, 1);
            }
        AttachGeometry("Restrained expedition route marks", _root!.transform, guides,
            Display("Expedition warm marks", new Color(.24f, .23f, .16f)), false, false);
    }
}
