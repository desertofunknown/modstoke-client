// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace Modstoke.Client;

internal sealed partial class OrbitalStationModel : IDisposable
{
    internal const int DoorCount = 10;
    private static readonly string[] ModuleNames = { "hub", "corridor", "room", "cap", "door-frame", "door-left", "door-right", "dock" };
    private static readonly FrameSettingsField[] CaptureDisabledEffects =
    {
        FrameSettingsField.ReflectionProbe, FrameSettingsField.PlanarProbe,
        FrameSettingsField.SkyReflection, FrameSettingsField.SSR,
        FrameSettingsField.TransparentSSR, FrameSettingsField.SSGI,
        FrameSettingsField.ProbeVolume, FrameSettingsField.AtmosphericScattering,
        FrameSettingsField.Volumetrics, FrameSettingsField.VolumetricClouds,
        FrameSettingsField.ExposureControl, FrameSettingsField.Postprocess,
        FrameSettingsField.CustomPostProcess, FrameSettingsField.CustomPass
    };

    private readonly Dictionary<string, Mesh> _meshes = new(StringComparer.Ordinal);
    private readonly List<Texture2D> _textures = new();
    private readonly Transform[,] _doors = new Transform[DoorCount, 2];
    private readonly Transform[] _doorFrames = new Transform[DoorCount];
    private readonly float[] _doorAmounts = new float[DoorCount];
    private readonly List<BoxCollider> _solids = new();
    private readonly List<BoxCollider> _walkSurfaces = new();
    private readonly List<LocalReflection> _reflections = new();
    private readonly List<Light> _lights = new();
    private GameObject? _root;
    private Rigidbody? _body;
    private Material? _material;
    private PhysicMaterial? _contact;
    private bool _disposed;
    private bool _powered = true;
    private bool _reflectionDirty = true;
    private float _reflectionElapsed;

    internal GameObject Root => _root ?? throw new ObjectDisposedException(nameof(OrbitalStationModel));
    internal bool Visible => _root != null && _root && _root.activeSelf;

    // Construction, pose changes and disposal belong to the Unity main thread.
    internal OrbitalStationModel()
    {
        try
        {
            var collision = LoadCollision();
            foreach (var name in ModuleNames) LoadMesh(name);
            LoadMaterial();
            _contact = new PhysicMaterial("Station contact")
            {
                dynamicFriction = .65f, staticFriction = .8f, bounciness = 0,
                frictionCombine = PhysicMaterialCombine.Maximum,
                bounceCombine = PhysicMaterialCombine.Minimum
            };
            _root = new GameObject("Orbital station");
            _root.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(_root);
            _body = _root.AddComponent<Rigidbody>();
            _body.isKinematic = true;
            _body.useGravity = false;
            _body.mass = 200000;
            _body.interpolation = RigidbodyInterpolation.Interpolate;
            _body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            BuildLayout(collision);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void BuildLayout(Dictionary<string, Bounds[]> collision)
    {
        AddPart("Hub", "hub", Root.transform, Vector3.zero, Quaternion.identity, collision);
        AddLighting("Hub", new(0, 2.9f, 0), Quaternion.identity, new(12, 3.4f, 12), 10, 6500);
        for (var i = 0; i < 4; i++)
        {
            var rotation = Quaternion.Euler(0, i * 90, 0);
            AddPart("Corridor " + i, "corridor", Root.transform, rotation * new Vector3(0, 0, 9), rotation, collision);
            AddPart("Room " + i, "room", Root.transform, rotation * new Vector3(0, 0, 19), rotation, collision);
            AddDoor(i, rotation * new Vector3(0, 0, 6), rotation, collision);
            AddDoor(i + 4, rotation * new Vector3(0, 0, 12), rotation, collision);
            if (i != 0)
            {
                AddPart("Outer cap " + i, "cap", Root.transform, rotation * new Vector3(0, 0, 26), rotation, collision);
                AddPart("Cap pockets " + i, "door-frame", Root.transform, rotation * new Vector3(0, 0, 26), rotation, collision);
            }
            AddLighting("Corridor " + i, rotation * new Vector3(0, 2.9f, 9), rotation, new(3.2f, 3.4f, 6), 4.5f, 1800);
            AddLighting("Room " + i, rotation * new Vector3(0, 2.9f, 19), rotation, new(10, 3.4f, 14), 10, 6500);
        }
        AddDoor(8, new(0, 0, 26), Quaternion.identity, collision);
        AddPart("Dock airlock", "corridor", Root.transform, new(0, 0, 29), Quaternion.identity, collision);
        AddDoor(9, new(0, 0, 32), Quaternion.identity, collision);
        AddLighting("Dock airlock", new(0, 2.9f, 29), Quaternion.identity, new(3.2f, 3.4f, 6), 4.5f, 1800);
        AddPart("Dock approach", "dock", Root.transform, new(0, 0, 41), Quaternion.identity, collision);
        AddPart("Dock berth", "dock", Root.transform, new(0, 0, 59), Quaternion.identity, collision);
    }

    private Transform AddPart(string name, string module, Transform parent, Vector3 position, Quaternion rotation,
        Dictionary<string, Bounds[]> collision)
    {
        var node = AddNode(name, parent, position, rotation);
        node.gameObject.AddComponent<MeshFilter>().sharedMesh = _meshes[module];
        var renderer = node.gameObject.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = _material;
        renderer.shadowCastingMode = ShadowCastingMode.On;
        renderer.receiveShadows = true;
        // These boxes describe solid panels, never the bounds of a hollow module.
        foreach (var box in collision[module])
        {
            var collider = node.gameObject.AddComponent<BoxCollider>();
            collider.center = box.center;
            collider.size = box.size;
            collider.sharedMaterial = _contact;
            _solids.Add(collider);
            // Authored deck plates and pocket sills meet at the same floor height.
            if (MathF.Abs(box.max.y) <= .01f && Vector3.Dot(node.up, Root.transform.up) > .7f)
                _walkSurfaces.Add(collider);
        }
        return node;
    }

    private void AddDoor(int index, Vector3 position, Quaternion rotation, Dictionary<string, Bounds[]> collision)
    {
        var frame = AddNode("Pressure door " + index, Root.transform, position, rotation);
        _doorFrames[index] = frame;
        AddPart("Fixed pockets", "door-frame", frame, Vector3.zero, Quaternion.identity, collision);
        _doors[index, 0] = AddPart("Left leaf", "door-left", frame, Vector3.zero, Quaternion.identity, collision);
        _doors[index, 1] = AddPart("Right leaf", "door-right", frame, Vector3.zero, Quaternion.identity, collision);
    }

    private static Transform AddNode(string name, Transform parent, Vector3 position, Quaternion rotation)
    {
        var node = new GameObject(name) { layer = parent.gameObject.layer };
        node.transform.SetParent(parent, false);
        node.transform.localPosition = position;
        node.transform.localRotation = rotation;
        return node.transform;
    }

    internal void SetVisible(bool visible)
    {
        if (Visible == visible) return;
        Root.SetActive(visible);
        _reflectionDirty = true;
        _reflectionElapsed = 1;
    }

    internal void SetPose(Vector3 position, Quaternion rotation, bool teleport)
    {
        if (!float.IsFinite(position.x) || !float.IsFinite(position.y) || !float.IsFinite(position.z)
            || !float.IsFinite(rotation.x) || !float.IsFinite(rotation.y) || !float.IsFinite(rotation.z)
            || !float.IsFinite(rotation.w) || Quaternion.Dot(rotation, rotation) is < .5f or > 1.5f)
            throw new ArgumentException("The orbital station pose is invalid.");
        ObjectDisposedException.ThrowIf(_disposed, this);
        rotation = rotation.normalized;
        if (!teleport)
        {
            _body!.MovePosition(position);
            _body.MoveRotation(rotation);
            return;
        }
        var interpolation = _body!.interpolation;
        _body.interpolation = RigidbodyInterpolation.None;
        try
        {
            _body.position = position;
            _body.rotation = rotation;
            Root.transform.SetPositionAndRotation(position, rotation);
        }
        finally
        {
            _body.interpolation = interpolation;
        }
        _reflectionDirty = true;
    }

    internal void SetDoor(int index, float amount)
    {
        if ((uint)index >= DoorCount) throw new ArgumentOutOfRangeException(nameof(index));
        if (!float.IsFinite(amount)) throw new ArgumentOutOfRangeException(nameof(amount));
        ObjectDisposedException.ThrowIf(_disposed, this);
        amount = Mathf.Clamp01(amount);
        if (_doorAmounts[index] == amount) return;
        _doorAmounts[index] = amount;
        _doors[index, 0].localPosition = new(-1.4f * amount, 0, 0);
        _doors[index, 1].localPosition = new(1.4f * amount, 0, 0);
        _reflectionDirty = true;
    }

    private void LoadMesh(string name)
    {
        using var stream = Resource(name + ".msa");
        var data = SpacecraftMeshData.Read(stream, name);
        var mesh = new Mesh();
        _meshes.Add(name, mesh);
        mesh.name = "Station " + name;
        mesh.indexFormat = data.Vertices.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
        mesh.vertices = data.Vertices;
        mesh.normals = data.Normals;
        mesh.uv = data.Uv;
        mesh.SetTriangles(data.Triangles, 0);
        mesh.RecalculateBounds();
        mesh.RecalculateTangents();
        mesh.UploadMeshData(true);
    }

    private void LoadMaterial()
    {
        var shader = Shader.Find("HDRP/Lit");
        if (!shader) throw new InvalidOperationException("The orbital station requires the game's HDRP/Lit shader.");
        _material = new Material(shader) { name = "Station panels", enableInstancing = true };
        _material.SetColor("_BaseColor", Color.white);
        _material.SetTexture("_BaseColorMap", LoadTexture("atlas", false));
        _material.SetTexture("_MaskMap", LoadTexture("atlas-mask", true));
        _material.SetFloat("_MetallicRemapMin", 0);
        _material.SetFloat("_MetallicRemapMax", 1);
        _material.SetFloat("_SmoothnessRemapMin", 0);
        _material.SetFloat("_SmoothnessRemapMax", 1);
        _material.SetFloat("_AORemapMin", 0);
        _material.SetFloat("_AORemapMax", 1);
        _material.EnableKeyword("_MASKMAP");
    }

    private Texture2D LoadTexture(string name, bool linear)
    {
        using var stream = Resource(name + ".png");
        if (!stream.CanSeek || stream.Length is < 24 or > 16 * 1024 * 1024)
            throw new InvalidDataException("The station texture size is invalid: " + name);
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        if (BinaryPrimitives.ReadUInt64BigEndian(bytes) != 0x89504e470d0a1a0aUL
            || BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(12)) != 0x49484452
            || BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16)) != 2048
            || BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20)) != 2048)
            throw new InvalidDataException("The station texture dimensions are invalid: " + name);
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear);
        _textures.Add(texture);
        texture.name = "Station " + name;
        if (!ImageConversion.LoadImage(texture, bytes, false) || texture.width != 2048 || texture.height != 2048)
            throw new InvalidDataException("The station texture could not be decoded: " + name);
        texture.wrapMode = TextureWrapMode.Clamp;
        texture.filterMode = FilterMode.Trilinear;
        texture.anisoLevel = 8;
        texture.Apply(true, true);
        return texture;
    }

    private static Dictionary<string, Bounds[]> LoadCollision()
    {
        using var stream = Resource("collision.json");
        if (!stream.CanSeek || stream.Length is < 32 or > 1024 * 1024)
            throw new InvalidDataException("The station collision manifest size is invalid.");
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 8 });
        var root = document.RootElement;
        RequireProperties(root, "Version", "Modules");
        if (!root.GetProperty("Version").TryGetInt32(out var version) || version != 1)
            throw new InvalidDataException("The station collision version is unsupported.");
        var modules = root.GetProperty("Modules");
        if (modules.ValueKind != JsonValueKind.Array || modules.GetArrayLength() != ModuleNames.Length)
            throw new InvalidDataException("The station collision modules are invalid.");
        var result = new Dictionary<string, Bounds[]>(StringComparer.Ordinal);
        foreach (var module in modules.EnumerateArray())
        {
            RequireProperties(module, "Name", "Boxes");
            var name = module.GetProperty("Name").GetString();
            if (name == null || Array.IndexOf(ModuleNames, name) < 0 || result.ContainsKey(name))
                throw new InvalidDataException("The station collision module name is invalid or duplicated.");
            var boxes = module.GetProperty("Boxes");
            if (boxes.ValueKind != JsonValueKind.Array || boxes.GetArrayLength() is < 1 or > 256)
                throw new InvalidDataException("The station collision panel count is invalid: " + name);
            var bounds = new Bounds[boxes.GetArrayLength()];
            var index = 0;
            foreach (var box in boxes.EnumerateArray())
            {
                RequireProperties(box, "Center", "Size");
                var center = ReadVector(box.GetProperty("Center"), false);
                var size = ReadVector(box.GetProperty("Size"), true);
                bounds[index++] = new Bounds(center, size);
            }
            result.Add(name, bounds);
        }
        return result;
    }

    private static void RequireProperties(JsonElement element, string first, string second)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The station collision manifest contains an invalid object.");
        var fields = 0;
        foreach (var property in element.EnumerateObject())
        {
            var flag = property.NameEquals(first) ? 1 : property.NameEquals(second) ? 2 : 0;
            if (flag == 0 || (fields & flag) != 0)
                throw new InvalidDataException("The station collision manifest contains an unknown or duplicate field.");
            fields |= flag;
        }
        if (fields != 3) throw new InvalidDataException("The station collision manifest is missing a field.");
    }

    private static Vector3 ReadVector(JsonElement element, bool size)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != 3)
            throw new InvalidDataException("A station collision vector is invalid.");
        var vector = Vector3.zero;
        for (var i = 0; i < 3; i++)
        {
            if (element[i].ValueKind != JsonValueKind.Number || !element[i].TryGetSingle(out var value)
                || !float.IsFinite(value) || (size ? value <= 0 || value > 40 : value < -20 || value > 20))
                throw new InvalidDataException("A station collision dimension is invalid.");
            vector[i] = value;
        }
        return vector;
    }

    private static Stream Resource(string name) => typeof(OrbitalStationModel).Assembly.GetManifestResourceStream(
        "Modstoke.Client.Assets.station-" + name)
        ?? throw new InvalidOperationException("The station asset is missing: " + name);

    private void AddLighting(string name, Vector3 position, Quaternion rotation, Vector3 size, float range, float intensity)
    {
        var node = AddNode(name + " ceiling light", Root.transform, position, rotation);
        var light = node.gameObject.AddComponent<Light>();
        _lights.Add(light);
        light.type = LightType.Point;
        light.color = new Color(.85f, .92f, 1);
        light.range = range;
        light.shadows = LightShadows.None;
        light.enabled = false;
        var hd = node.gameObject.AddComponent<HDAdditionalLightData>();
        hd.type = HDLightType.Point;
        hd.lightUnit = LightUnit.Lumen;
        hd.intensity = intensity;
        AddReflection(name, new(position.x, 1.7f, position.z), rotation, size);
    }

    private void AddReflection(string name, Vector3 position, Quaternion rotation, Vector3 size)
    {
        var node = AddNode(name + " reflection", Root.transform, position, rotation);
        node.gameObject.SetActive(false);
        var legacy = node.gameObject.AddComponent<ReflectionProbe>();
        legacy.mode = ReflectionProbeMode.Realtime;
        legacy.refreshMode = ReflectionProbeRefreshMode.ViaScripting;
        legacy.boxProjection = true;
        legacy.size = size;
        var probe = node.gameObject.AddComponent<HDAdditionalReflectionData>();
        _reflections.Add(new(probe, size * .5f + Vector3.one * .25f));
        probe.mode = ProbeSettings.Mode.Realtime;
        probe.realtimeMode = ProbeSettings.RealtimeMode.OnDemand;
        probe.timeSlicing = false;
        probe.useInfluenceVolumeAsProxyVolume = true;
        probe.weight = 1;
        probe.multiplier = 1;
        var influence = probe.influenceVolume;
        influence.shape = InfluenceShape.Box;
        influence.boxSize = size;
        influence.boxBlendDistancePositive = Vector3.one * .2f;
        influence.boxBlendDistanceNegative = Vector3.one * .2f;

        // Copy the native field; the generated settingsRaw ref cannot represent this boxed value.
        var settings = probe.m_ProbeSettings;
        var resolution = settings.cubeResolution;
        resolution.useOverride = true;
        resolution.@override = CubeReflectionResolution.CubeReflectionResolution128;
        settings.cubeResolution = resolution;
        var camera = settings.cameraSettings;
        var clearing = camera.bufferClearing;
        clearing.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
        clearing.backgroundColorHDR = Color.black;
        clearing.clearDepth = true;
        camera.bufferClearing = clearing;
        var frustum = camera.frustum;
        frustum.nearClipPlaneRaw = .05f;
        frustum.farClipPlaneRaw = size.magnitude * .5f + 1;
        camera.frustum = frustum;
        var culling = camera.culling;
        culling.cullingMask = 1 << Root.layer;
        culling.useOcclusionCulling = false;
        camera.culling = culling;
        var volumes = camera.volumes;
        volumes.layerMask = 0;
        volumes.anchorOverride = node;
        camera.volumes = volumes;
        camera.probeLayerMask = 0;
        var frame = FrameSettings.NewDefaultRealtimeReflectionProbe();
        var mask = new FrameSettingsOverrideMask();
        foreach (var field in CaptureDisabledEffects)
        {
            frame.SetEnabled(field, false);
            mask.mask[(uint)field] = true;
        }
        camera.renderingPathCustomFrameSettings = frame;
        camera.renderingPathCustomFrameSettingsOverrideMask = mask;
        camera.customRenderingSettings = true;
        settings.cameraSettings = camera;
        probe.m_ProbeSettings = settings;
        probe.PrepareCulling();
    }

    internal void UpdateView(float dt)
    {
        if (!Visible) return;
        var camera = Camera.main;
        foreach (var light in _lights)
            light.enabled = _powered && camera != null && (light.transform.position - camera.transform.position).sqrMagnitude < 1600;
        _reflectionElapsed += Mathf.Max(0, dt);
        var refresh = _reflectionDirty && _reflectionElapsed >= 1;
        foreach (var reflection in _reflections)
        {
            var probe = reflection.Probe;
            var point = camera != null ? probe.transform.InverseTransformPoint(camera.transform.position) : Vector3.zero;
            var half = reflection.HalfSize;
            // Only the current room and an adjacent doorway capture. A radial
            // proximity test activates almost every cubemap from the central hub.
            var active = camera != null && MathF.Abs(point.x) <= half.x
                && MathF.Abs(point.y) <= half.y && MathF.Abs(point.z) <= half.z;
            var activated = active && !probe.gameObject.activeSelf;
            if (probe.gameObject.activeSelf != active) probe.gameObject.SetActive(active);
            if (active && (activated || refresh)) probe.RequestRenderNextUpdate();
        }
        if (refresh)
        {
            _reflectionDirty = false;
            _reflectionElapsed = 0;
        }
    }

    internal void SetPowered(bool powered)
    {
        if (_powered == powered) return;
        _powered = powered;
        _reflectionDirty = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_root != null && _root) _root.SetActive(false);
        foreach (var reflection in _reflections)
        {
            var probe = reflection.Probe;
            if (!probe) continue;
            // The shipped player does not release HDProbe render buffers on destruction.
            probe.realtimeTextureRTH?.Release();
            probe.realtimeDepthTextureRTH?.Release();
            probe.m_RealtimeTexture = null;
            probe.m_RealtimeDepthBuffer = null;
        }
        _reflections.Clear();
        _lights.Clear();
        _solids.Clear();
        _walkSurfaces.Clear();
        Array.Clear(_doorFrames);
        Array.Clear(_doors);
        if (_root != null && _root) UnityEngine.Object.Destroy(_root);
        _root = null;
        _body = null;
        foreach (var mesh in _meshes.Values) if (mesh) UnityEngine.Object.Destroy(mesh);
        _meshes.Clear();
        foreach (var texture in _textures) if (texture) UnityEngine.Object.Destroy(texture);
        _textures.Clear();
        if (_material) UnityEngine.Object.Destroy(_material);
        _material = null;
        if (_contact) UnityEngine.Object.Destroy(_contact);
        _contact = null;
    }

    private readonly record struct LocalReflection(HDAdditionalReflectionData Probe, Vector3 HalfSize);
}
