// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using TheForest.Utils;
using UnityEngine;
using UnityEngine.Rendering;

namespace Modstoke.Client;

internal sealed class SpacecraftToolRack : IDisposable
{
    private static readonly Vector3 Mount = new(-3.445f, -.25f, -3.93f);
    private static readonly Quaternion ToolRotation = Quaternion.Euler(-90, 0, 0);
    private static readonly string[] ToolNames = ["screwdriver", "crowbar", "wirecutters", "wrench", "welder"];
    private static readonly Bay[] Bays =
    [
        new(new(-.29f, .02f, -.04f), new(new(-.29f, .001f, -.0573f), new(.034f, .254f, .0346f)),
            new(new(-.29f, .001f, -.016f), new(.092f, .278f, .003f))),
        new(new(-.15f, .035f, -.04f), new(new(-.15f, -.004176f, -.0834503f), new(.038f, .541648f, .0869005f)),
            new(new(-.15f, -.004176f, -.016f), new(.092f, .565648f, .003f))),
        new(new(-.01f, -.02f, -.04f), new(new(-.01f, -.0432558f, -.0527f), new(.0742789f, .2022866f, .0254f)),
            new(new(-.01f, -.0432558f, -.016f), new(.107f, .2262866f, .003f))),
        new(new(.13f, .015f, -.04f), new(new(.1259997f, -.020003f, -.0539981f), new(.0739994f, .2519939f, .0279961f)),
            new(new(.13f, -.020003f, -.016f), new(.108f, .2759939f, .003f))),
        new(new(.275f, .01f, -.04f), new(new(.275f, .0335f, -.129f), new(.106f, .409f, .178f)),
            new(new(.275f, .0335f, -.016f), new(.129f, .433f, .003f)))
    ];

    private readonly record struct Bay(Vector3 Position, Bounds Tool, Bounds Plate);
    private sealed class ToolVisual(Transform frame)
    {
        internal readonly Transform Frame = frame;
        internal int Seen = -1;
    }

    private readonly ClientSpacecraftBody _body;
    private readonly Action<BoxStationEngineeringControl, int> _command;
    private readonly ImmutableArray<BoxStationToolSource> _sources = BoxStationEngineering.GetSources(BoxStationLayout.Current);
    private readonly ClientDeviceSurfaceFinish _finish = new();
    private readonly List<Mesh> _meshes = new();
    private readonly List<Material> _materials = new();
    private readonly List<Texture2D> _textures = new();
    private readonly Dictionary<int, ToolVisual> _tools = new();
    private readonly Mesh?[] _toolMeshes = new Mesh?[BoxStationEngineering.ShipSlotCount];
    private readonly int[] _slots = new int[BoxStationEngineering.ShipSlotCount];
    private GameObject? _root;
    private Material? _toolMaterial;
    private BoxStationEngineeringState? _state;
    private int _promptSlot = -1, _promptMode = -1;
    private string _prompt = "";
    private bool _disposed;
    internal BoxCollider Solid { get; private set; } = null!;

    internal SpacecraftToolRack(ClientSpacecraftBody body, Action<BoxStationEngineeringControl, int> command)
    {
        _body = body;
        _command = command;
        Array.Fill(_slots, -1);
        try
        {
            _root = new GameObject("Engineering tool rack") { layer = body.Root.layer };
            _root.transform.SetParent(body.Root.transform, false);
            _root.transform.localPosition = Mount;
            _root.transform.localRotation = Quaternion.Euler(0, -90, 0);
            AddPart("shell", Finish("Tool rack housing", new(.38f, .41f, .39f, 1), .15f, .42f, ClientDeviceSurfaceFinish.Surface.Polymer));
            AddPart("trim", Finish("Tool rack fittings", new(.32f, .35f, .37f, 1), .8f, .46f, ClientDeviceSurfaceFinish.Surface.Metal));
            AddPart("gasket", Finish("Tool rack cushions", new(.018f, .020f, .022f, 1), 0, .22f, ClientDeviceSurfaceFinish.Surface.Rubber));
            Solid = _root.AddComponent<BoxCollider>();
            Solid.center = new(0, 0, -.060122f);
            Solid.size = new(.74f, .62f, .189756f);
        }
        catch { Dispose(); throw; }
    }

    internal void ApplyState(BoxStationEngineeringState? state)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_state != state)
        {
            Array.Fill(_slots, -1);
            if (state is { } changed)
                foreach (var tool in changed.Tools)
                    if (tool.ShipSlot != BoxStationEngineering.NoShipSlot) _slots[tool.ShipSlot] = tool.Id;
            _state = state;
        }
        if (state is not { } current) return;
        foreach (int id in _slots)
        {
            if (id < 0) continue;
            var tool = current.Tools[id];
            if (tool.Place != BoxStationToolPlace.ShipStored) continue;
            var visual = Visual(tool.Id);
            if (visual.Frame.parent != _root!.transform) visual.Frame.SetParent(_root.transform, false);
            visual.Frame.localPosition = Bays[tool.ShipSlot].Position;
            visual.Frame.localRotation = ToolRotation;
            Show(visual);
        }
    }

    internal void SetHeldToolPose(int id, Vector3 position, Quaternion rotation, bool visible)
    {
        if (_disposed || _state is not { } state || (uint)id >= state.Tools.Length
            || state.Tools[id].Place != BoxStationToolPlace.Held) return;
        if (!visible)
        {
            if (_tools.TryGetValue(id, out var hidden))
            {
                hidden.Seen = -1;
                if (hidden.Frame.gameObject.activeSelf) hidden.Frame.gameObject.SetActive(false);
            }
            return;
        }
        if (!float.IsFinite(position.x) || !float.IsFinite(position.y) || !float.IsFinite(position.z)
            || !float.IsFinite(rotation.x) || !float.IsFinite(rotation.y) || !float.IsFinite(rotation.z)
            || !float.IsFinite(rotation.w) || Quaternion.Dot(rotation, rotation) is < .5f or > 1.5f)
            throw new ArgumentException("The carried tool pose is invalid.");
        var visual = Visual(id);
        if (visual.Frame.parent != null) visual.Frame.SetParent(null, true);
        visual.Frame.SetPositionAndRotation(position, rotation.normalized);
        Show(visual);
    }

    private static void Show(ToolVisual visual)
    {
        visual.Seen = Time.frameCount;
        if (!visual.Frame.gameObject.activeSelf) visual.Frame.gameObject.SetActive(true);
    }

    internal void FinishFrame()
    {
        foreach (var visual in _tools.Values)
            if (visual.Seen != Time.frameCount && visual.Frame.gameObject.activeSelf) visual.Frame.gameObject.SetActive(false);
    }

    internal Vector3 ControlPoint(int slot, bool stored)
    {
        var bounds = stored ? Bays[slot].Tool : Bays[slot].Plate;
        return _root!.transform.TransformPoint(new(bounds.center.x, bounds.center.y, bounds.min.z - .001f));
    }

    internal bool Interact(Guid holder, ulong actor, bool interactive, out string prompt)
    {
        prompt = "";
        if (!interactive || _state is not { } state || !ClientSpacecraftRider.WorldInputAvailable || Cursor.visible) return false;
        var camera = LocalPlayer.MainCam;
        if (camera == null || !camera || !camera.isActiveAndEnabled) return false;
        var frame = _root!.transform;
        var ray = new Ray(frame.InverseTransformPoint(camera.transform.position), frame.InverseTransformDirection(camera.transform.forward));
        if (ray.direction.z <= .1f) return false;
        int selected = -1;
        float nearest = 2.4f;
        for (int slot = 0; slot < Bays.Length; slot++)
        {
            bool stored = _slots[slot] >= 0 && state.Tools[_slots[slot]].Place == BoxStationToolPlace.ShipStored;
            var bounds = stored ? Bays[slot].Tool : Bays[slot].Plate;
            if (bounds.IntersectRay(ray, out float distance) && distance >= 0 && distance <= nearest)
            { nearest = distance; selected = slot; }
        }
        if (selected < 0) return false;
        var contact = frame.TransformPoint(ray.GetPoint(nearest));
        if (!_body.CanSeeControl(camera.transform.position, contact, -1, LocalPlayer.Transform, null, Solid)) return false;
        int held = -1;
        foreach (var tool in state.Tools)
            if (tool.Place == BoxStationToolPlace.Held && tool.Holder == holder && tool.Actor == actor) { held = tool.Id; break; }
        int item = _slots[selected];
        bool canStore = held >= 0 && (int)_sources[held].Kind == selected && (item < 0 || item == held);
        bool canTake = held < 0 && item >= 0 && state.Tools[item].Place == BoxStationToolPlace.ShipStored;
        int mode = canStore ? state.Tools[held].Lit ? 0 : 1 : canTake ? 2 : item >= 0 ? 3 : 4;
        if (_promptSlot != selected || _promptMode != mode)
        {
            _promptSlot = selected; _promptMode = mode;
            var name = ToolNames[selected];
            _prompt = mode switch
            {
                0 => "Extinguish the welder before storing it", 1 => "E · Store " + name, 2 => "E · Take " + name,
                3 => "This " + name + " is in use", _ => name + " rack empty"
            };
        }
        prompt = _prompt;
        if (Input.GetKeyDown(KeyCode.E))
        {
            if (canStore && !state.Tools[held].Lit) _command(BoxStationEngineeringControl.ShipStore, 0);
            else if (canTake) _command(BoxStationEngineeringControl.ShipTake, item);
        }
        return true;
    }

    private ToolVisual Visual(int id)
    {
        if (_tools.TryGetValue(id, out var visual)) return visual;
        int kind = (int)_sources[id].Kind;
        var mesh = _toolMeshes[kind] ??= LoadMesh("box-tool-" + ToolNames[kind]);
        var node = new GameObject("Repair " + ToolNames[kind]) { layer = _root!.layer };
        node.transform.SetParent(_root.transform, false);
        visual = new(node.transform);
        _tools.Add(id, visual);
        node.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = node.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = ToolMaterial();
        renderer.shadowCastingMode = ShadowCastingMode.On;
        renderer.receiveShadows = true;
        return visual;
    }

    private Material ToolMaterial()
    {
        if (_toolMaterial != null) return _toolMaterial;
        var material = _toolMaterial = Lit("Engineering tools", Color.white, 1, 1);
        material.SetTexture("_BaseColorMap", LoadTexture("box-atlas", false));
        material.SetTexture("_MaskMap", LoadTexture("box-atlas-mask", true));
        material.SetFloat("_MetallicRemapMin", 0); material.SetFloat("_MetallicRemapMax", 1);
        material.SetFloat("_SmoothnessRemapMin", 0); material.SetFloat("_SmoothnessRemapMax", 1);
        material.SetFloat("_AORemapMin", 0); material.SetFloat("_AORemapMax", 1);
        material.EnableKeyword("_MASKMAP");
        return material;
    }

    private void AddPart(string part, Material material)
    {
        var mesh = LoadMesh("spacecraft-tool-rack-" + part);
        var node = new GameObject("Tool rack " + part) { layer = _root!.layer };
        node.transform.SetParent(_root.transform, false);
        node.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = node.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.On;
        renderer.receiveShadows = true;
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
        if (!shader) throw new InvalidOperationException("The tool rack requires the game's HDRP/Lit shader.");
        var material = new Material(shader) { name = name, enableInstancing = true };
        _materials.Add(material);
        material.SetColor("_BaseColor", color);
        material.SetFloat("_Metallic", metallic); material.SetFloat("_Smoothness", smoothness);
        return material;
    }

    private Texture2D LoadTexture(string name, bool linear)
    {
        using var stream = Resource(name + ".png");
        if (!stream.CanSeek || stream.Length is < 24 or > 16 * 1024 * 1024)
            throw new InvalidDataException("Invalid tool texture size: " + name);
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        if (BinaryPrimitives.ReadUInt64BigEndian(bytes) != 0x89504e470d0a1a0aUL
            || BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(12)) != 0x49484452
            || BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16)) != 2048
            || BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20)) != 2048)
            throw new InvalidDataException("Invalid tool texture dimensions: " + name);
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear) { name = name };
        _textures.Add(texture);
        if (!ImageConversion.LoadImage(texture, bytes, false) || texture.width != 2048 || texture.height != 2048)
            throw new InvalidDataException("Could not decode tool texture: " + name);
        texture.wrapMode = TextureWrapMode.Clamp; texture.filterMode = FilterMode.Trilinear; texture.anisoLevel = 8;
        texture.Apply(true, true);
        return texture;
    }

    private static Stream Resource(string name) => typeof(SpacecraftToolRack).Assembly
        .GetManifestResourceStream("Modstoke.Client.Assets." + name)
        ?? throw new InvalidDataException("The tool rack asset is missing: " + name);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _state = null;
        foreach (var tool in _tools.Values) if (tool.Frame) UnityEngine.Object.Destroy(tool.Frame.gameObject);
        _tools.Clear();
        if (Solid != null && Solid) Solid.enabled = false;
        if (_root != null && _root) UnityEngine.Object.Destroy(_root);
        foreach (var mesh in _meshes) UnityEngine.Object.Destroy(mesh);
        foreach (var material in _materials) UnityEngine.Object.Destroy(material);
        foreach (var texture in _textures) UnityEngine.Object.Destroy(texture);
        _finish.Dispose();
    }
}
