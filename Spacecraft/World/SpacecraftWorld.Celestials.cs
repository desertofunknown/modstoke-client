// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.IO;
using System.Linq;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace Modstoke.Client;

internal readonly record struct OrbitalSkyView(OrbitalSimulation Orbits, OrbitalFrame Frame,
    double Time, Vector3 LocalOrigin);

internal sealed partial class ClientSpacecraftWorld
{
    private GameObject? _celestialRoot, _stars;
    private Mesh? _celestialSphere;
    private Vector3[]? _celestialVertices;
    private CelestialBody[]? _celestialBodies;
    private OrbitalVector[]? _celestialPositions;
    private double[]? _celestialDistances;
    private int[]? _celestialOrder;
    private int _sunIndex = -1;
    private CelestialBody? _shadingBody;
    private OrbitalVector _shadingLight;
    private int _shadingRow;

    private sealed class CelestialBody(OrbitalBody definition)
    {
        internal readonly OrbitalBody Definition = definition;
        internal GameObject? Node;
        internal Mesh? Mesh;
        internal Il2CppStructArray<Vector3>? Vertices;
        internal Texture2D? Texture;
        internal CelestialSurface? Surface;
        internal OrbitalVector Light, BakedLight;
        internal bool Lit;
        internal int Width, Height;
    }

    private void UpdateCelestials(OrbitalSkyView? view, Camera? camera, SpacecraftRegion region)
    {
        if (_celestialBodies == null || _celestialRoot == null || _stars == null) return;
        var active = view.HasValue && camera != null && region is SpacecraftRegion.Space or SpacecraftRegion.Moon;
        _celestialRoot.SetActive(active);
        _stars.SetActive(active);
        if (view is not { } sky || _sunIndex < 0) return;

        // Catalog packets arrive less often than vessel poses. Extrapolation is
        // presentation-only, bounded to the existing orbital freshness window.
        double dt = sky.Time - sky.Orbits.SampleTime;
        if (!double.IsFinite(dt) || Math.Abs(dt) > 30)
        {
            _celestialRoot.SetActive(false);
            _stars.SetActive(false);
            return;
        }
        for (int i = 0; i < _celestialBodies.Length; i++)
        {
            var sample = sky.Orbits.Sample(i);
            _celestialPositions![i] = sample.Position
                + (sample.Velocity + sky.Orbits.Acceleration(i) * (.5 * dt)) * dt;
        }
        var sunlight = _celestialPositions![_sunIndex];
        var observer = default(OrbitalVector);
        if (active)
        {
            var offset = camera!.transform.position - sky.LocalOrigin;
            observer = sky.Frame.ToInertialPosition(new(offset.x, offset.y, offset.z));
            _stars.transform.localRotation = Quaternion.LookRotation(
                Native(sky.Frame.ToLocalDirection(new(0, 0, 1))),
                Native(sky.Frame.ToLocalDirection(new(0, 1, 0))));
        }
        for (int i = 0; i < _celestialBodies.Length; i++)
        {
            var body = _celestialBodies[i];
            BodyAxes(body.Definition.Id, sky.Time, out var right, out var up, out var forward);
            var toSun = sunlight - _celestialPositions[i];
            if (toSun.LengthSquared > 0)
            {
                toSun /= Math.Sqrt(toSun.LengthSquared);
                body.Light = new(OrbitalVector.Dot(toSun, right), OrbitalVector.Dot(toSun, up),
                    OrbitalVector.Dot(toSun, forward));
            }
            if (!active) continue;
            var toBody = _celestialPositions[i] - observer;
            _celestialDistances![i] = Math.Sqrt(toBody.LengthSquared);
        }
        if (active)
        {
            // Nested angular shells give disjoint bodies a deterministic front-to-back
            // order without putting a near planet's proxy geometry through the cabin.
            for (int i = 1; i < _celestialOrder!.Length; i++)
            {
                int body = _celestialOrder[i], j = i - 1;
                while (j >= 0 && _celestialDistances![_celestialOrder[j]] > _celestialDistances![body])
                { _celestialOrder[j + 1] = _celestialOrder[j]; j--; }
                _celestialOrder[j + 1] = body;
            }
            double halfHeight = Math.Tan(camera!.fieldOfView * Math.PI / 360);
            double minRadius = halfHeight * .3 / Math.Max(1, camera.pixelHeight);
            double halfDiagonal = Math.Atan(halfHeight * Math.Sqrt(1 + camera.aspect * camera.aspect));
            var cameraForward = camera.transform.forward;
            var viewDirection = new OrbitalVector(cameraForward.x, cameraForward.y, cameraForward.z);
            for (int order = 0; order < _celestialOrder.Length; order++)
            {
                int i = _celestialOrder[order];
                var body = _celestialBodies[i];
                double distance = _celestialDistances![i], radius = body.Definition.Radius;
                var direction = distance > 0
                    ? sky.Frame.ToLocalDirection((_celestialPositions[i] - observer) / distance) : default;
                bool visible = distance > radius && radius / distance >= minRadius
                    && !(region == SpacecraftRegion.Moon && body.Definition.Id == 301)
                    && OrbitalVector.Dot(direction, viewDirection) >= Math.Cos(halfDiagonal + Math.Asin(radius / distance));
                if (body.Node != null) body.Node.SetActive(visible);
                if (!visible) continue;
                PrepareCelestial(body);
                body.Node!.SetActive(true);
                BodyAxes(body.Definition.Id, sky.Time, out var right, out var up, out var forward);
                ProjectCelestial(body, direction, radius / distance,
                    sky.Frame.ToLocalDirection(right), sky.Frame.ToLocalDirection(up),
                    sky.Frame.ToLocalDirection(forward), .70 + .20 * order / Math.Max(1, _celestialOrder.Length - 1));
            }
        }
        UpdateCelestialLighting();
    }

    private void ProjectCelestial(CelestialBody body, OrbitalVector direction, double ratio,
        OrbitalVector right, OrbitalVector up, OrbitalVector forward, double depth)
    {
        var vertices = body.Vertices!;
        for (int i = 0; i < _celestialVertices!.Length; i++)
        {
            var normal = _celestialVertices[i];
            var ray = direction + (right * normal.x + up * normal.y + forward * normal.z) * ratio;
            vertices[i] = Native(ray * (depth / Math.Sqrt(ray.LengthSquared)));
        }
        body.Mesh!.vertices = vertices;
    }

    private void PrepareCelestial(CelestialBody body)
    {
        if (body.Node != null) return;
        var definition = body.Definition;
        var mesh = UnityEngine.Object.Instantiate(_celestialSphere!);
        mesh.name = definition.Name + " apparent surface";
        _meshes.Add(mesh);
        mesh.MarkDynamic();
        mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 2);
        var material = Display(definition.Name, definition.Id == 10 ? new Color(6, 5.5f, 4.7f) : Color.black);
        body.Node = AttachMesh(definition.Name, _celestialRoot!.transform, mesh, material, false, false);
        body.Node.SetActive(false);
        body.Mesh = mesh;
        body.Vertices = new Il2CppStructArray<Vector3>(_celestialVertices!.Length);
        if (definition.Id == 10) return;

        Color32[] albedo;
        string? name = definition.Id switch
        {
            199 => "mercury.jpg", 299 => "venus-atmosphere.jpg", 399 => "earth.png",
            301 => "moon.jpg", 499 => "mars.jpg", 599 => "jupiter.jpg", 699 => "saturn.jpg",
            799 => "uranus.jpg", 899 => "neptune.jpg", _ => null
        };
        bool mapped = name != null;
        body.Width = mapped ? 2048 : 256; body.Height = body.Width / 2;
        var texture = new Texture2D(mapped ? 2 : body.Width, mapped ? 2 : body.Height,
            TextureFormat.RGBA32, true, false);
        _textures.Add(texture);
        texture.name = definition.Name + " illuminated surface";
        if (name != null)
        {
            using var stream = typeof(ClientSpacecraftWorld).Assembly.GetManifestResourceStream(
                "Modstoke.Client.Assets.spacecraft-celestial-" + name)
                ?? throw new InvalidDataException("Missing celestial surface: " + name);
            if (stream.Length is < 24 or > 8 * 1024 * 1024)
                throw new InvalidDataException("Invalid celestial surface size: " + name);
            var bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            if (!ImageConversion.LoadImage(texture, bytes, false) || texture.width != 2048 || texture.height != 1024)
                throw new InvalidDataException("Invalid celestial surface image: " + name);
            albedo = texture.GetPixels32().ToArray();
        }
        else
        {
            // Unmapped catalog bodies retain simple albedo, rather than invented terrain.
            albedo = new Color32[body.Width * body.Height];
            Array.Fill(albedo, BodyColor(definition.Id));
        }
        texture.wrapModeU = TextureWrapMode.Repeat;
        texture.wrapModeV = TextureWrapMode.Clamp;
        texture.filterMode = FilterMode.Trilinear;
        texture.anisoLevel = 8;
        body.Texture = texture;
        body.Surface = new(albedo, body.Width, body.Height);
    }

    private void UpdateCelestialLighting()
    {
        if (_shadingBody == null)
        {
            foreach (int index in _celestialOrder!)
            {
                var body = _celestialBodies![index];
                if (body.Surface == null || body.Light.LengthSquared < .99
                    || body.Lit && OrbitalVector.Dot(body.Light, body.BakedLight) > .99996) continue;
                _shadingBody = body; _shadingLight = body.Light; _shadingRow = 0;
                break;
            }
        }
        if (_shadingBody is not { } pending) return;
        // At most 16K texels across the entire sky per frame, with no partial uploads.
        int rows = Math.Min(pending.Height - _shadingRow, 16384 / pending.Width);
        var pixels = pending.Surface!.ShadeRows(_shadingLight, _shadingRow, rows);
        _shadingRow += rows;
        if (_shadingRow != pending.Height) return;
        pending.Texture!.SetPixels32(pixels);
        pending.Texture.Apply(true, false);
        ClientDeviceScreenMaterial.SetDisplay(pending.Node!.GetComponent<MeshRenderer>().sharedMaterial,
            Color.white, pending.Texture);
        pending.BakedLight = _shadingLight; pending.Lit = true;
        _shadingBody = null;
    }

    private static void BodyAxes(int id, double time, out OrbitalVector right, out OrbitalVector up,
        out OrbitalVector forward)
    {
        double period = id == 399 ? OrbitalCatalog.EarthRotationPeriod : id == 301 ? OrbitalCatalog.MoonRotationPeriod : 0;
        double angle = period == 0 ? 0 : Math.IEEERemainder(time / period, 1) * 2 * Math.PI;
        // Match the existing equatorial surface frame; these are not IAU pole/libration models.
        // Texture center is longitude zero and east increases to the right.
        double c = Math.Cos(angle), s = Math.Sin(angle);
        right = new(-c, -s, 0); up = new(0, 0, 1); forward = new(-s, c, 0);
    }

    private static Color32 BodyColor(int id) => id switch
    {
        299 => new(217, 200, 158, 255), 499 => new(172, 105, 72, 255),
        599 => new(189, 166, 139, 255), 699 => new(202, 187, 145, 255),
        799 => new(139, 195, 202, 255), 899 => new(92, 131, 194, 255),
        _ => new(156, 151, 143, 255)
    };

    private static Vector3 Native(OrbitalVector value) => new((float)value.X, (float)value.Y, (float)value.Z);

    private void ReleaseCelestials()
    {
        _shadingBody = null; _shadingRow = 0;
        _celestialBodies = null; _celestialVertices = null; _celestialPositions = null;
        _celestialOrder = null; _celestialDistances = null; _celestialSphere = null;
        _celestialRoot = _stars = null; _sunIndex = -1;
    }
}
