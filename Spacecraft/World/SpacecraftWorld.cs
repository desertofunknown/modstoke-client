// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace Modstoke.Client;

// Authored expedition terrain inside the retained game scene. Only its backdrop
// follows the local camera; the shared, static collision surface never moves.
internal sealed partial class ClientSpacecraftWorld : IDisposable
{
    private const int Sectors = 192;
    private const int Rings = 110;
    private const float RingStep = SpacecraftLayout.WorldRadius / Rings;
    private const float LandingRadius = 42;
    private static readonly Vector2[] SurveySites = { new(-110, 70), new(98, 92), new(15, -125) };
    private static readonly Vector2[] RouteBends = { new(-42, 58), new(61, 27), new(-22, -69) };
    private static readonly string[] SurveyNames = { "Flooded grove", "Ribbed wetland", "Rock shelf" };
    private readonly List<Mesh> _meshes = new();
    private readonly List<Material> _materials = new();
    private readonly List<Texture2D> _textures = new();
    private readonly Material?[] _beaconMaterials = new Material?[3];
    private readonly Vector3[] _beaconPositions = new Vector3[3];
    private readonly bool[] _surveyed = new bool[3];
    private GameObject? _root;
    private GameObject? _backdrop;
    private GameObject? _spaceSky;
    private GameObject? _worldSky;
    private Light? _terrainLight;
    private Vector3[]? _terrainVertices;
    private bool _attempted;
    private bool _disposed;
    private long _nextPreparationAt;
    private int _preparationFailures;
    private string _status = "Destination worlds not prepared";

    internal bool Ready => !_disposed && _root != null && _terrainVertices != null;
    internal string Status => Ready ? "Wetland and moon ready · " + SurveyCount + "/3 wetland surveys" : _status;
    internal int SurveyCount { get; private set; }
    internal Vector3 LandingPosition => SpacecraftLayout.WorldOrigin + new Vector3(0, -SpacecraftLayout.GroundY + .1f, 0);

    internal bool Ensure()
    {
        if (Ready) return true;
        if (_attempted || _disposed || Environment.TickCount64 < _nextPreparationAt) return false;
        _attempted = true;
        try
        {
            var shader = Shader.Find("HDRP/Lit");
            if (!shader) throw new InvalidOperationException("The expedition terrain shader is unavailable.");
            _root = new GameObject("Expedition worlds");
            _root.SetActive(false);
            _root.transform.position = SpacecraftLayout.WorldOrigin;
            UnityEngine.Object.DontDestroyOnLoad(_root);
            BuildTerrain(shader);
            BuildSites(shader);
            BuildMoon(shader);
            BuildBackdrop();
            BuildFog();
            BuildSpaceAtmosphere();
            var lightNode = Child("Expedition daylight", _root.transform);
            lightNode.transform.localPosition = new Vector3(-80, 170, -60);
            _terrainLight = lightNode.AddComponent<Light>();
            _terrainLight.type = LightType.Point;
            _terrainLight.color = new Color(.71f, .84f, .83f);
            _terrainLight.range = 380;
            _terrainLight.shadows = LightShadows.None;
            lightNode.AddComponent<HDAdditionalLightData>().intensity = 9000000;
            _terrainLight.enabled = false;
            _root.SetActive(true);
            _preparationFailures = 0;
            _nextPreparationAt = 0;
            return true;
        }
        catch (Exception exception)
        {
            Release();
            _attempted = false;
            _nextPreparationAt = Environment.TickCount64 + (500L << Math.Min(_preparationFailures, 4));
            _preparationFailures = Math.Min(_preparationFailures + 1, 4);
            _status = "Destination worlds unavailable: " + exception.Message;
            return false;
        }
    }

    internal bool TrySurfacePoint(Vector3 point, out Vector3 surface)
    {
        surface = default;
        if (!Ready || !float.IsFinite(point.x) || !float.IsFinite(point.y) || !float.IsFinite(point.z)) return false;
        var moon = new Vector2(point.x - SpacecraftLayout.MoonOrigin.x,
            point.z - SpacecraftLayout.MoonOrigin.z).sqrMagnitude <= SpacecraftLayout.MoonRadius * SpacecraftLayout.MoonRadius;
        var origin = moon ? SpacecraftLayout.MoonOrigin : SpacecraftLayout.WorldOrigin;
        var vertices = moon ? _moonVertices : _terrainVertices;
        if (vertices == null) return false;
        var p = point - origin;
        var radius = Mathf.Sqrt(p.x * p.x + p.z * p.z);
        if (radius > SpacecraftLayout.WorldRadius) return false;
        var angle = Mathf.Atan2(p.z, p.x);
        if (angle < 0) angle += 2 * Mathf.PI;
        var sector = Mathf.Min(Sectors - 1, (int)(angle * Sectors / (2 * Mathf.PI)));
        var ring = Mathf.Max(1, Mathf.CeilToInt(radius / RingStep));
        // Polygon chords can place a point in the next annulus even when its
        // polar radius lies just inside a ring. Check both adjacent annuli.
        for (var candidate = ring; candidate <= Mathf.Min(Rings, ring + 1); candidate++)
        {
            var outer = vertices[Index(candidate, sector)];
            var next = vertices[Index(candidate, sector + 1)];
            if (candidate == 1)
            {
                if (SurfaceTriangle(p, Vector3.zero, next, outer, out var height))
                {
                    surface = new Vector3(point.x, origin.y + height, point.z);
                    return true;
                }
            }
            else
            {
                var inner = vertices[Index(candidate - 1, sector)];
                var innerNext = vertices[Index(candidate - 1, sector + 1)];
                if (SurfaceTriangle(p, inner, innerNext, outer, out var height) ||
                    SurfaceTriangle(p, outer, innerNext, next, out height))
                {
                    surface = new Vector3(point.x, origin.y + height, point.z);
                    return true;
                }
            }
        }
        return false;
    }

    internal void UpdateLocal(SpacecraftRegion region, OrbitalSkyView? sky)
    {
        if (!Ready || _backdrop == null) return;
        var camera = Camera.main;
        UpdateCelestials(sky, camera, region);
        UpdateFog(region, camera);
        UpdateSpaceAtmosphere(region, camera);
        var active = region != SpacecraftRegion.Island && camera != null;
        _backdrop.SetActive(active);
        if (_terrainLight != null) _terrainLight.enabled = region == SpacecraftRegion.TestWorld;
        if (_moonLight != null) _moonLight.enabled = region == SpacecraftRegion.Moon;
        if (!active || camera == null) return;
        _backdrop.transform.position = camera.transform.position;
        // Stay inside the camera far plane; the opaque shell also hides retained
        // island geometry without changing native renderers or camera settings.
        var radius = Mathf.Clamp(camera.farClipPlane * .85f, 10, 1200);
        _backdrop.transform.localScale = Vector3.one * radius;
        if (_spaceSky != null) _spaceSky.SetActive(region is SpacecraftRegion.Space or SpacecraftRegion.Moon);
        if (_worldSky != null) _worldSky.SetActive(region == SpacecraftRegion.TestWorld);
    }

    internal bool TrySurvey(Vector3 playerPosition, out string message)
    {
        message = "Approach an unrecorded survey beacon within 4 m.";
        if (!Ready || !float.IsFinite(playerPosition.x) || !float.IsFinite(playerPosition.y) || !float.IsFinite(playerPosition.z))
            return false;
        var closest = -1;
        var distance = 16f;
        for (var i = 0; i < SurveySites.Length; i++)
        {
            if (_surveyed[i]) continue;
            var squared = (playerPosition - _beaconPositions[i]).sqrMagnitude;
            if (squared > distance) continue;
            distance = squared;
            closest = i;
        }
        if (closest < 0)
        {
            if (SurveyCount == SurveySites.Length) message = "All three sites surveyed. Return to the spacecraft when ready.";
            return false;
        }
        _surveyed[closest] = true;
        SurveyCount++;
        var material = _beaconMaterials[closest];
        if (material != null) ClientDeviceScreenMaterial.SetDisplay(material, new Color(1, .59f, .16f), null);
        message = SurveyNames[closest] + " recorded · " + SurveyCount + "/3";
        return true;
    }

    private void BuildTerrain(Shader shader)
    {
        var terrain = Surface(shader, "Wet slate and moss", "terrain", 2048);
        _terrainVertices = BuildGround(_root!.transform, terrain, false);
    }

    private Vector3[] BuildGround(Transform parent, Material terrain, bool moon)
    {
        var vertices = new Vector3[1 + Rings * Sectors];
        var uv = new Vector2[vertices.Length];
        uv[0] = Vector2.zero;
        var indices = new int[Sectors * 3 + (Rings - 1) * Sectors * 6];
        var t = 0;
        for (var ring = 1; ring <= Rings; ring++)
        {
            for (var sector = 0; sector < Sectors; sector++)
            {
                var angle = sector * 2 * Mathf.PI / Sectors;
                var x = Mathf.Cos(angle) * ring * RingStep;
                var z = Mathf.Sin(angle) * ring * RingStep;
                var index = Index(ring, sector);
                vertices[index] = new Vector3(x, moon ? MoonHeight(x, z) : Height(x, z), z);
                uv[index] = new Vector2(x / 6, z / 6);
                if (ring == 1)
                {
                    indices[t++] = 0; indices[t++] = Index(ring, sector + 1); indices[t++] = index;
                }
                else
                {
                    var inner = Index(ring - 1, sector);
                    var innerNext = Index(ring - 1, sector + 1);
                    indices[t++] = inner; indices[t++] = innerNext; indices[t++] = index;
                    indices[t++] = index; indices[t++] = innerNext; indices[t++] = Index(ring, sector + 1);
                }
            }
        }
        var mesh = CreateMesh(moon ? "Moon crater terrain" : "Expedition terrain", vertices, indices, uv);
        AttachMesh("Walkable plateau", parent, mesh, terrain, true);

        var skirt = new Geometry();
        for (var sector = 0; sector < Sectors; sector++)
        {
            var a = vertices[Index(Rings, sector)];
            var b = vertices[Index(Rings, sector + 1)];
            skirt.Quad(a, b, new Vector3(b.x * .96f, -35, b.z * .96f), new Vector3(a.x * .96f, -35, a.z * .96f));
        }
        AttachGeometry("Plateau cliff", parent, skirt, terrain, true);
        return vertices;
    }

    private void BuildBackdrop()
    {
        _backdrop = Child("Local expedition backdrop", _root!.transform);
        _backdrop.SetActive(false);
        var vertices = new Vector3[65 * 33];
        var uv = new Vector2[vertices.Length];
        var triangles = new List<int>();
        for (var row = 0; row <= 32; row++)
        {
            var latitude = row * Mathf.PI / 32;
            for (var column = 0; column <= 64; column++)
            {
                var longitude = column * 2 * Mathf.PI / 64;
                var index = row * 65 + column;
                vertices[index] = new Vector3(Mathf.Sin(latitude) * Mathf.Cos(longitude), Mathf.Cos(latitude), Mathf.Sin(latitude) * Mathf.Sin(longitude));
                uv[index] = new Vector2(column / 64f, 1 - row / 32f);
                if (row == 32 || column == 64) continue;
                // Inward-facing triangles; this is an opaque local environment.
                triangles.Add(index); triangles.Add(index + 65); triangles.Add(index + 1);
                triangles.Add(index + 1); triangles.Add(index + 65); triangles.Add(index + 66);
            }
        }
        var mesh = CreateMesh("Local sky shell", vertices, triangles.ToArray(), uv);
        var space = Display("Deep space", new Color(.0004f, .0007f, .0018f));
        var world = Display("Expedition atmosphere", Color.white, LoadTexture("destination", 2048, 1024, TextureWrapMode.Repeat));
        _spaceSky = Child("Space environment", _backdrop.transform);
        AttachMesh("Space backdrop", _spaceSky.transform, mesh, space, false, false);
        BuildCelestialBodies();
        _worldSky = AttachMesh("Destination backdrop", _backdrop.transform, mesh, world, false, false);
    }

    private static void AddColumn(Geometry geometry, Vector3 origin, float radius, float height, float rotation, float taper)
    {
        const int sides = 6;
        for (var side = 0; side < sides; side++)
        {
            var a = rotation + side * 2 * Mathf.PI / sides;
            var b = rotation + (side + 1) * 2 * Mathf.PI / sides;
            var bottomA = origin + new Vector3(Mathf.Cos(a) * radius, -.08f, Mathf.Sin(a) * radius);
            var bottomB = origin + new Vector3(Mathf.Cos(b) * radius, -.08f, Mathf.Sin(b) * radius);
            var topA = origin + new Vector3(Mathf.Cos(a) * radius * taper + height * .06f, height, Mathf.Sin(a) * radius * taper);
            var topB = origin + new Vector3(Mathf.Cos(b) * radius * taper + height * .06f, height, Mathf.Sin(b) * radius * taper);
            if (taper == 0)
            {
                var shoulderA = origin + new Vector3(Mathf.Cos(a) * radius * .82f + height * .04f, height * .72f, Mathf.Sin(a) * radius * .82f);
                var shoulderB = origin + new Vector3(Mathf.Cos(b) * radius * .82f + height * .04f, height * .72f, Mathf.Sin(b) * radius * .82f);
                geometry.Quad(bottomA, shoulderA, shoulderB, bottomB);
                geometry.Triangle(shoulderA, topA, shoulderB);
            }
            else
            {
                geometry.Quad(bottomA, topA, topB, bottomB);
                geometry.Triangle(origin + new Vector3(height * .06f, height, 0), topB, topA);
            }
            geometry.Triangle(origin + Vector3.down * .08f, bottomA, bottomB);
        }
    }

    private Vector3 Ground(Vector2 point, float offset = 0)
    {
        var position = SpacecraftLayout.WorldOrigin + new Vector3(point.x, 0, point.y);
        return TrySurfacePoint(position, out var surface)
            ? surface - SpacecraftLayout.WorldOrigin + Vector3.up * offset
            : new Vector3(point.x, Height(point.x, point.y) + offset, point.y);
    }

    private static Vector2 Route(int index, float t) => RouteBends[index] * (2 * (1 - t) * t) + SurveySites[index] * (t * t);
    private static bool NearSite(Vector2 point, float radius)
    {
        foreach (var site in SurveySites) if ((point - site).sqrMagnitude < radius * radius) return true;
        return false;
    }
    private static bool NearRoute(Vector2 point, float radius)
    {
        for (var route = 0; route < SurveySites.Length; route++)
            for (var sample = 0; sample <= 48; sample++)
                if ((Route(route, sample / 48f) - point).sqrMagnitude < radius * radius) return true;
        return false;
    }
    private static float Mound(float x, float z, float width) => Mathf.Exp(-(x * x + z * z) / (width * width));
    private static float Smooth(float from, float to, float value)
    {
        var t = Mathf.Clamp01((value - from) / (to - from));
        return t * t * (3 - 2 * t);
    }
    private static float Hash(int value, int salt)
    {
        var bits = unchecked((uint)value * 747796405u + (uint)salt * 2891336453u);
        bits = ((bits >> (int)((bits >> 28) + 4)) ^ bits) * 277803737u;
        return ((bits >> 22) ^ bits) / (float)uint.MaxValue;
    }
    private static int Index(int ring, int sector) => 1 + (ring - 1) * Sectors + sector % Sectors;
    private static bool SurfaceTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c, out float height)
    {
        var denominator = (b.z - c.z) * (a.x - c.x) + (c.x - b.x) * (a.z - c.z);
        var u = ((b.z - c.z) * (p.x - c.x) + (c.x - b.x) * (p.z - c.z)) / denominator;
        var v = ((c.z - a.z) * (p.x - c.x) + (a.x - c.x) * (p.z - c.z)) / denominator;
        height = u * a.y + v * b.y + (1 - u - v) * c.y;
        return u >= -.0001f && v >= -.0001f && u + v <= 1.0001f;
    }

    private Material Lit(Shader shader, string name, Color color, float smoothness)
    {
        var material = new Material(shader);
        _materials.Add(material);
        material.name = name;
        material.SetColor("_BaseColor", color);
        material.SetFloat("_Metallic", 0);
        material.SetFloat("_Smoothness", smoothness);
        return material;
    }
    private Material Display(string name, Color color, Texture? texture = null)
    {
        var material = ClientDeviceScreenMaterial.CreateDisplayMaterial(name);
        _materials.Add(material);
        ClientDeviceScreenMaterial.SetDisplay(material, color, texture);
        return material;
    }
    private Texture2D LoadTexture(string name, int width, int height, TextureWrapMode wrap, bool linear = false)
    {
        using var stream = typeof(ClientSpacecraftWorld).Assembly.GetManifestResourceStream(
            "Modstoke.Client.Assets.spacecraft-world-" + name + ".png")
            ?? throw new InvalidOperationException("The expedition texture is missing: " + name);
        if (!stream.CanSeek || stream.Length is < 24 or > 8 * 1024 * 1024)
            throw new InvalidDataException("The expedition texture size is invalid: " + name);
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        // Color is sRGB; uncompressed RGB+Y normals (alpha 1) and masks are linear.
        // PNG decoding preserves the bottom-left authored UV orientation.
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear);
        _textures.Add(texture);
        texture.name = "Expedition " + name;
        if (!ImageConversion.LoadImage(texture, bytes.ToArray(), false) ||
            texture.width != width || texture.height != height)
            throw new InvalidDataException("The expedition texture could not be decoded: " + name);
        texture.wrapMode = wrap;
        texture.filterMode = FilterMode.Trilinear;
        texture.anisoLevel = 8;
        texture.Apply(true, true);
        return texture;
    }
    private Mesh CreateMesh(string name, Vector3[] vertices, int[] triangles, Vector2[]? uv = null, Vector3[]? normals = null)
    {
        var mesh = new Mesh { name = name, indexFormat = vertices.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
        _meshes.Add(mesh);
        mesh.vertices = vertices;
        if (uv != null) mesh.uv = uv;
        mesh.SetTriangles(triangles, 0);
        if (normals == null) mesh.RecalculateNormals();
        else mesh.normals = normals;
        if (uv != null) mesh.RecalculateTangents();
        mesh.RecalculateBounds();
        return mesh;
    }
    private GameObject AttachMesh(string name, Transform parent, Mesh mesh, Material material, bool collision, bool lit = true, bool shadows = true)
    {
        var node = Child(name, parent);
        node.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = node.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = lit && shadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
        renderer.receiveShadows = lit;
        if (!lit) ClientDeviceScreenMaterial.ConfigureRenderer(renderer);
        if (collision)
        {
            node.isStatic = true;
            node.AddComponent<MeshCollider>().sharedMesh = mesh;
        }
        return node;
    }
    private void AttachGeometry(string name, Transform parent, Geometry geometry, Material material, bool collision, bool shadows = true)
    {
        if (geometry.Vertices.Count == 0) return;
        AttachMesh(name, parent, CreateMesh(name, geometry.Vertices.ToArray(), geometry.Triangles.ToArray(),
            geometry.UV.ToArray(), geometry.Normals.ToArray()), material, collision, true, shadows);
    }
    private static GameObject Child(string name, Transform parent)
    {
        var node = new GameObject(name) { layer = parent.gameObject.layer };
        node.transform.SetParent(parent, false);
        return node;
    }
    private sealed class Geometry
    {
        internal readonly List<Vector3> Vertices = new();
        internal readonly List<int> Triangles = new();
        internal readonly List<Vector3> Normals = new();
        internal readonly List<Vector2> UV = new();
        internal void Triangle(Vector3 a, Vector3 b, Vector3 c)
        {
            var first = Vertices.Count;
            Vertices.Add(a); Vertices.Add(b); Vertices.Add(c);
            Triangles.Add(first); Triangles.Add(first + 1); Triangles.Add(first + 2);
            var normal = Vector3.Cross(b - a, c - a).normalized;
            Normals.Add(normal); Normals.Add(normal); Normals.Add(normal);
            UV.Add(Project(a, normal)); UV.Add(Project(b, normal)); UV.Add(Project(c, normal));
        }
        private static Vector2 Project(Vector3 point, Vector3 normal)
        {
            if (Mathf.Abs(normal.y) > .65f) return new Vector2(point.x / 6, point.z / 6);
            return Mathf.Abs(normal.x) > Mathf.Abs(normal.z)
                ? new Vector2(point.z / 6, point.y / 6) : new Vector2(point.x / 6, point.y / 6);
        }
        internal void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            Triangle(a, b, c); Triangle(a, c, d);
        }
    }

    private void Release()
    {
        ReleaseCelestials();
        ReleaseFog();
        ReleaseSpaceAtmosphere();
        if (_root != null)
        {
            _root.SetActive(false);
            UnityEngine.Object.Destroy(_root);
        }
        foreach (var mesh in _meshes) if (mesh) UnityEngine.Object.Destroy(mesh);
        foreach (var material in _materials) if (material) UnityEngine.Object.Destroy(material);
        foreach (var texture in _textures) if (texture) UnityEngine.Object.Destroy(texture);
        _meshes.Clear(); _materials.Clear(); _textures.Clear();
        Array.Clear(_beaconMaterials, 0, _beaconMaterials.Length);
        _root = _backdrop = _spaceSky = _worldSky = null;
        _terrainLight = null;
        _moonLight = null;
        _moonVertices = null;
        _terrainVertices = null;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Release();
        _status = "Destination worlds closed";
    }
}
