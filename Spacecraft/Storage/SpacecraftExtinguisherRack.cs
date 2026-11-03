// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TheForest.Utils;
using UnityEngine;
using UnityEngine.Rendering;

namespace Modstoke.Client;

internal sealed class SpacecraftExtinguisherRack : IDisposable
{
    private static readonly Vector3 Mount = new(-3.445f, -.15f, -3.35f);
    private static readonly Vector3 StoredPosition = new(0, -.30f, -.13f);
    private static readonly Quaternion StoredRotation = Quaternion.Euler(0, 180, 0);
    private static readonly Bounds BottleBounds = new(new(-.017182f, -.01f, -.137f), new(.217364f, .58f, .22f));
    private static readonly Bounds PlateBounds = new(new(0, 0, -.019f), new(.25f, .65f, .006f));
    private static readonly string[] Parts = ["body", "lever", "pin", "nozzle"];
    private static readonly Vector3[] PartPositions =
        [Vector3.zero, new(0, .535f, 0), new(-.043f, .523f, .005f), new(.087f, .425f, .035f)];

    private sealed class ExtinguisherVisual(Transform frame)
    {
        internal readonly Transform Frame = frame;
        internal Transform Lever = null!, Pin = null!;
        internal int Seen = -1;
        internal float Spray;
    }

    private readonly ClientSpacecraftBody _body;
    private readonly Action<BoxStationFirefightingControl, int, Vector3> _command;
    private readonly ClientDeviceSurfaceFinish _finish = new();
    private readonly List<Mesh> _meshes = new();
    private readonly List<Material> _materials = new();
    private readonly List<Texture2D> _textures = new();
    private readonly Dictionary<int, ExtinguisherVisual> _items = new();
    private readonly Mesh?[] _itemMeshes = new Mesh?[4];
    private GameObject? _root;
    private Material? _itemMaterial;
    private BoxStationFirefightingState? _state;
    private int _reserved = -1;
    private bool _disposed;
    internal BoxCollider Solid { get; private set; } = null!;

    internal SpacecraftExtinguisherRack(ClientSpacecraftBody body,
        Action<BoxStationFirefightingControl, int, Vector3> command)
    {
        _body = body;
        _command = command;
        try
        {
            _root = new GameObject("Extinguisher cradle") { layer = body.Root.layer };
            _root.transform.SetParent(body.Root.transform, false);
            _root.transform.localPosition = Mount;
            _root.transform.localRotation = Quaternion.Euler(0, -90, 0);
            AddPart("Cradle housing", _root.transform, LoadMesh("spacecraft-extinguisher-rack-shell"),
                Finish("Extinguisher cradle housing", new(.38f, .41f, .39f, 1), .15f, .42f, ClientDeviceSurfaceFinish.Surface.Polymer));
            AddPart("Cradle fittings", _root.transform, LoadMesh("spacecraft-extinguisher-rack-trim"),
                Finish("Extinguisher cradle fittings", new(.32f, .35f, .37f, 1), .8f, .46f, ClientDeviceSurfaceFinish.Surface.Metal));
            AddPart("Cradle cushions", _root.transform, LoadMesh("spacecraft-extinguisher-rack-gasket"),
                Finish("Extinguisher cradle cushions", new(.018f, .020f, .022f, 1), 0, .22f, ClientDeviceSurfaceFinish.Surface.Rubber));
            Solid = _root.AddComponent<BoxCollider>();
            Solid.center = new(0, 0, -.084251f);
            Solid.size = new(.28f, .68f, .279498f);
        }
        catch { Dispose(); throw; }
    }

    internal void ApplyState(BoxStationFirefightingState? state)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_state != state)
        {
            _reserved = -1;
            if (state is { } changed)
                for (int i = 0; i < changed.Items.Length; i++)
                    if (changed.Items[i].ShipSlot == 0) { _reserved = i; break; }
            _state = state;
            foreach (var pair in _items)
            {
                if (state is not { } current || (uint)pair.Key >= current.Items.Length)
                {
                    Hide(pair.Value);
                    continue;
                }
                var item = current.Items[pair.Key];
                var visual = pair.Value;
                if (visual.Pin.gameObject.activeSelf != item.Safety) visual.Pin.gameObject.SetActive(item.Safety);
                if (item.Place != BoxStationExtinguisherPlace.Held || item.Safety) SetSpray(visual, 0);
                if (item.Place is not (BoxStationExtinguisherPlace.Held or BoxStationExtinguisherPlace.ShipStored)) Hide(visual);
            }
        }
        if (state is not { } stored || _reserved < 0
            || stored.Items[_reserved].Place != BoxStationExtinguisherPlace.ShipStored) return;
        var bottle = Visual(_reserved);
        if (bottle.Frame.parent != _root!.transform) bottle.Frame.SetParent(_root.transform, false);
        bottle.Frame.localPosition = StoredPosition;
        bottle.Frame.localRotation = StoredRotation;
        Show(bottle);
    }

    internal void SetHeldExtinguisherPose(int id, Vector3 position, Quaternion rotation, bool visible)
    {
        if (_disposed || _state is not { } state || (uint)id >= state.Items.Length
            || state.Items[id].Place != BoxStationExtinguisherPlace.Held) return;
        if (!visible)
        {
            if (_items.TryGetValue(id, out var hidden)) Hide(hidden);
            return;
        }
        if (!float.IsFinite(position.x) || !float.IsFinite(position.y) || !float.IsFinite(position.z)
            || !float.IsFinite(rotation.x) || !float.IsFinite(rotation.y) || !float.IsFinite(rotation.z)
            || !float.IsFinite(rotation.w) || Quaternion.Dot(rotation, rotation) is < .5f or > 1.5f)
            throw new ArgumentException("The carried extinguisher pose is invalid.");
        var visual = Visual(id);
        if (visual.Frame.parent != null) visual.Frame.SetParent(null, true);
        visual.Frame.SetPositionAndRotation(position, rotation.normalized);
        Show(visual);
    }

    // Only committed bursts drive the lever; a cooldown does not represent continued spraying.
    internal void SetExtinguisherSpray(int id, float amount)
    {
        if (!float.IsFinite(amount) || amount is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(amount));
        if (_disposed || !_items.TryGetValue(id, out var visual)) return;
        if (_state is not { } state || (uint)id >= state.Items.Length
            || state.Items[id].Place != BoxStationExtinguisherPlace.Held || state.Items[id].Safety) amount = 0;
        SetSpray(visual, amount);
    }

    private static void SetSpray(ExtinguisherVisual visual, float amount)
    {
        if (visual.Spray == amount) return;
        visual.Spray = amount;
        visual.Lever.localRotation = Quaternion.Euler(-10 * amount, 0, 0);
    }

    private static void Show(ExtinguisherVisual visual)
    {
        visual.Seen = Time.frameCount;
        if (!visual.Frame.gameObject.activeSelf) visual.Frame.gameObject.SetActive(true);
    }

    private static void Hide(ExtinguisherVisual visual)
    {
        visual.Seen = -1;
        SetSpray(visual, 0);
        if (visual.Frame.gameObject.activeSelf) visual.Frame.gameObject.SetActive(false);
    }

    internal void FinishFrame()
    {
        foreach (var visual in _items.Values)
            if (visual.Seen != Time.frameCount) Hide(visual);
    }

    internal Vector3 ControlPoint(bool take)
    {
        var bounds = take ? BottleBounds : PlateBounds;
        return _root!.transform.TransformPoint(new(bounds.center.x, bounds.center.y, bounds.min.z - .001f));
    }

    internal bool Interact(Guid holder, ulong actor, bool interactive, out string prompt)
    {
        prompt = "";
        if (!interactive || _state is not { } state || holder == Guid.Empty
            || !ClientSpacecraftRider.WorldInputAvailable || Cursor.visible) return false;
        var camera = LocalPlayer.MainCam;
        if (camera == null || !camera || !camera.isActiveAndEnabled) return false;
        bool stored = _reserved >= 0 && state.Items[_reserved].Place == BoxStationExtinguisherPlace.ShipStored;
        var frame = _root!.transform;
        var ray = new Ray(frame.InverseTransformPoint(camera.transform.position), frame.InverseTransformDirection(camera.transform.forward));
        var bounds = stored ? BottleBounds : PlateBounds;
        if (ray.direction.z <= .1f || !bounds.IntersectRay(ray, out float distance) || distance < 0 || distance > 2.4f) return false;
        var contact = frame.TransformPoint(ray.GetPoint(distance));
        if (!_body.CanSeeControl(camera.transform.position, contact, -1, LocalPlayer.Transform, null, Solid)) return false;
        int held = -1;
        for (int i = 0; i < state.Items.Length; i++)
            if (state.Items[i].Place == BoxStationExtinguisherPlace.Held
                && state.Items[i].Holder == holder && state.Items[i].Actor == actor) { held = i; break; }
        bool canStore = held >= 0 && (_reserved < 0 || _reserved == held);
        bool canTake = held < 0 && stored;
        prompt = canStore ? "E · Store extinguisher" : canTake ? "E · Take extinguisher"
            : stored ? "Your hands are occupied" : _reserved >= 0 ? "This extinguisher is in use" : "Extinguisher cradle empty";
        if (Input.GetKeyDown(KeyCode.E))
        {
            if (canStore) _command(BoxStationFirefightingControl.ShipStore, 0, Vector3.zero);
            else if (canTake) _command(BoxStationFirefightingControl.ShipTake, _reserved, Vector3.zero);
        }
        return true;
    }

    private ExtinguisherVisual Visual(int id)
    {
        if (_items.TryGetValue(id, out var visual)) return visual;
        var node = new GameObject("Extinguisher " + id) { layer = _root!.layer };
        node.transform.SetParent(_root.transform, false);
        // Register the frame before loading assets so failures retain an owned cleanup path.
        visual = new(node.transform);
        _items.Add(id, visual);
        var material = ItemMaterial();
        for (int part = 0; part < Parts.Length; part++)
        {
            var mesh = _itemMeshes[part] ??= LoadMesh("box-machine-extinguisher-" + Parts[part]);
            var child = AddPart("Extinguisher " + Parts[part], node.transform, mesh, material);
            child.localPosition = PartPositions[part];
            if (part == 1) visual.Lever = child;
            else if (part == 2) visual.Pin = child;
        }
        visual.Pin.gameObject.SetActive(_state!.Value.Items[id].Safety);
        return visual;
    }

    private Transform AddPart(string name, Transform parent, Mesh mesh, Material material)
    {
        var node = new GameObject(name) { layer = _root!.layer };
        node.transform.SetParent(parent, false);
        node.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = node.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.On;
        renderer.receiveShadows = true;
        return node.transform;
    }

    private Material ItemMaterial()
    {
        if (_itemMaterial != null) return _itemMaterial;
        var material = _itemMaterial = Lit("Firefighting equipment finish", Color.white, 1, 1);
        material.SetTexture("_BaseColorMap", LoadTexture("box-firefighting-base", false));
        material.SetTexture("_MaskMap", LoadTexture("box-firefighting-mask", true));
        material.SetTexture("_NormalMap", LoadTexture("box-firefighting-normal", true, true));
        material.SetFloat("_NormalMapSpace", 0); material.SetFloat("_NormalScale", 1);
        material.SetFloat("_MetallicRemapMin", 0); material.SetFloat("_MetallicRemapMax", 1);
        material.SetFloat("_SmoothnessRemapMin", 0); material.SetFloat("_SmoothnessRemapMax", 1);
        material.SetFloat("_AORemapMin", 0); material.SetFloat("_AORemapMax", 1);
        material.EnableKeyword("_MASKMAP");
        material.EnableKeyword("_NORMALMAP");
        material.EnableKeyword("_NORMALMAP_TANGENT_SPACE");
        return material;
    }

    private Mesh LoadMesh(string name)
    {
        using var stream = Resource(name + ".msa");
        var data = SpacecraftMeshData.Read(stream, name);
        var mesh = new Mesh { name = data.Name, indexFormat = data.Vertices.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
        _meshes.Add(mesh);
        mesh.vertices = data.Vertices; mesh.normals = data.Normals; mesh.uv = data.Uv;
        mesh.SetTriangles(data.Triangles, 0);
        mesh.RecalculateBounds(); mesh.RecalculateTangents(); mesh.UploadMeshData(true);
        return mesh;
    }

    private Material Finish(string name, Color color, float metallic, float smoothness, ClientDeviceSurfaceFinish.Surface surface)
    {
        var material = Lit(name, color, metallic, smoothness);
        _finish.Apply(material, surface);
        return material;
    }

    private Material Lit(string name, Color color, float metallic, float smoothness)
    {
        var shader = Shader.Find("HDRP/Lit");
        if (!shader) throw new InvalidOperationException("The extinguisher cradle requires the game's HDRP/Lit shader.");
        var material = new Material(shader) { name = name, enableInstancing = true };
        _materials.Add(material);
        material.SetColor("_BaseColor", color);
        material.SetFloat("_Metallic", metallic); material.SetFloat("_Smoothness", smoothness);
        return material;
    }

    private Texture2D LoadTexture(string name, bool linear, bool normal = false)
    {
        using var stream = Resource(name + ".png");
        if (!stream.CanSeek || stream.Length is < 24 or > 16 * 1024 * 1024)
            throw new InvalidDataException("Invalid extinguisher texture size: " + name);
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        if (BinaryPrimitives.ReadUInt64BigEndian(bytes) != 0x89504e470d0a1a0aUL
            || BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(12)) != 0x49484452
            || BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16)) != 2048
            || BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20)) != 2048)
            throw new InvalidDataException("Invalid extinguisher texture dimensions: " + name);
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear) { name = name };
        _textures.Add(texture);
        if (!ImageConversion.LoadImage(texture, bytes, false) || texture.width != 2048 || texture.height != 2048)
            throw new InvalidDataException("Could not decode extinguisher texture: " + name);
        if (normal)
        {
            // The shared source uses RGB +Y normals; desktop HDRP reads X from alpha and Y from green.
            var pixels = texture.GetPixels32().ToArray();
            for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(255, pixels[i].g, 0, pixels[i].r);
            texture.SetPixels32(pixels);
        }
        texture.wrapMode = TextureWrapMode.Clamp; texture.filterMode = FilterMode.Trilinear; texture.anisoLevel = 8;
        texture.Apply(true, true);
        return texture;
    }

    private static Stream Resource(string name) => typeof(SpacecraftExtinguisherRack).Assembly
        .GetManifestResourceStream("Modstoke.Client.Assets." + name)
        ?? throw new InvalidDataException("The extinguisher cradle asset is missing: " + name);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _state = null;
        foreach (var item in _items.Values) if (item.Frame) UnityEngine.Object.Destroy(item.Frame.gameObject);
        _items.Clear();
        if (Solid != null && Solid) Solid.enabled = false;
        if (_root != null && _root) UnityEngine.Object.Destroy(_root);
        foreach (var mesh in _meshes) UnityEngine.Object.Destroy(mesh);
        foreach (var material in _materials) UnityEngine.Object.Destroy(material);
        foreach (var texture in _textures) UnityEngine.Object.Destroy(texture);
        _finish.Dispose();
    }
}
