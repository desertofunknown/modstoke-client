// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Modstoke.Sons;
using TheForest.Utils;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace Modstoke.Client;

internal sealed class SpacecraftEvaRack : IDisposable
{
    private const float Width = 280, Height = 560, PixelScale = .00075f, ScreenDepth = -.002f;
    private static readonly Vector3 Mount = new(-2.20f, -1.235f, -4.68f);
    private static readonly Rect WearBounds = new(12, 330, 256, 66);
    private static readonly Rect ServiceBounds = new(12, 410, 256, 58);
    private static readonly Color Ink = new(.92f, .94f, .95f, 1);
    private static readonly Color Muted = new(.55f, .61f, .64f, 1);
    private static readonly Color Green = new(.38f, .83f, .67f, 1);
    private static readonly Color Amber = new(1, .72f, .28f, 1);
    private static readonly Color KeyFace = new(.04f, .065f, .07f, 1);
    private readonly ClientSpacecraftBody _body;
    private readonly Action<int, bool> _command;
    private readonly List<Material> _materials = new();
    private readonly List<Texture2D> _textures = new();
    private readonly Dictionary<int, Material> _fonts = new();
    private readonly ClientTabletTypography _typography = new();
    private readonly Il2CppStructArray<RaycastHit> _hits = new(size: 32);
    private readonly Pad[] _pads = new Pad[SpacecraftEvaSuit.Count];
    private GameObject? _root;
    private Mesh? _mesh;
    private Material _graphics = null!, _glass = null!;
    private int _hovered = -1;
    private bool _hoveredService, _disposed;
    private float _nextReadout;

    internal BoxCollider Solid { get; private set; } = null!;
    internal Material SuitMaterial { get; private set; } = null!;

    internal SpacecraftEvaRack(ClientSpacecraftBody body, Action<int, bool> command)
    {
        _body = body;
        _command = command;
        try
        {
            _root = new GameObject("EVA pressure suit rack") { layer = body.Root.layer };
            _root.transform.SetParent(body.Root.transform, false);
            _root.transform.localPosition = Mount;
            var shader = Shader.Find("HDRP/Lit");
            if (!shader) throw new InvalidOperationException("The EVA rack requires the game's HDRP/Lit shader.");
            var material = new Material(shader) { name = "EVA rack finish", enableInstancing = true };
            SuitMaterial = material;
            _materials.Add(material);
            material.SetColor("_BaseColor", Color.white);
            material.SetTexture("_BaseColorMap", LoadTexture("atlas", false));
            material.SetTexture("_MaskMap", LoadTexture("atlas-mask", true));
            material.SetFloat("_MetallicRemapMin", 0);
            material.SetFloat("_MetallicRemapMax", 1);
            material.SetFloat("_SmoothnessRemapMin", 0);
            material.SetFloat("_SmoothnessRemapMax", 1);
            material.SetFloat("_AORemapMin", 0);
            material.SetFloat("_AORemapMax", 1);
            material.EnableKeyword("_MASKMAP");
            _mesh = LoadMesh();
            _root.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var renderer = _root.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.On;
            renderer.receiveShadows = true;
            Solid = _root.AddComponent<BoxCollider>();
            Solid.center = new(0, .95f, .15f);
            Solid.size = new(.9f, 1.9f, .30f);
            _glass = ClientDeviceScreenMaterial.CreateDisplayMaterial("EVA rack glass");
            _materials.Add(_glass);
            ClientDeviceScreenMaterial.SetDisplay(_glass, new(.012f, .018f, .021f, 1), null);
            _graphics = new Material(UnityEngine.UI.Graphic.defaultGraphicMaterial) { name = "EVA rack graphics" };
            _materials.Add(_graphics);
            Depth(_graphics);
            for (int slot = 0; slot < _pads.Length; slot++) AddPad(slot);
        }
        catch { Dispose(); throw; }
    }

    private void AddPad(int slot)
    {
        var root = _root!;
        var frame = new GameObject("Suit bay " + (slot + 1)).transform;
        frame.SetParent(root.transform, false);
        frame.gameObject.layer = root.layer;
        frame.localPosition = new((1 - slot) * .26f, 1.55f, .310f);
        frame.localRotation = Quaternion.Euler(0, 180, 0);
        var pad = new Pad(frame);
        _pads[slot] = pad;
        var glass = GameObject.CreatePrimitive(PrimitiveType.Cube);
        glass.name = "Suit bay glass";
        glass.layer = frame.gameObject.layer;
        glass.transform.SetParent(frame, false);
        glass.transform.localScale = new(.218f, .430f, .002f);
        var collider = glass.GetComponent<Collider>();
        if (collider != null) { collider.enabled = false; UnityEngine.Object.Destroy(collider); }
        var renderer = glass.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = _glass;
        ClientDeviceScreenMaterial.ConfigureRenderer(renderer);
        var canvas = new GameObject("Suit bay readout").AddComponent<RectTransform>();
        canvas.SetParent(frame, false);
        canvas.gameObject.layer = frame.gameObject.layer;
        canvas.localPosition = new(0, 0, ScreenDepth);
        canvas.localScale = Vector3.one * PixelScale;
        canvas.sizeDelta = new(Width, Height);
        canvas.gameObject.AddComponent<Canvas>().renderMode = RenderMode.WorldSpace;
        Label(canvas, "Bay number", "EVA / " + (slot + 1).ToString("00", CultureInfo.InvariantCulture), 12, 12, 256, 32, 26, Ink);
        Graphic(canvas, "Header rule", new(12, 51, 256, 2), Muted);
        pad.Status = Label(canvas, "Custody", "AWAITING HOST", 12, 65, 256, 29, 22, Amber);
        pad.Reserves = Label(canvas, "Finite reserves", "", 12, 107, 256, 111, 21, Ink);
        pad.Reserves.enableWordWrapping = true;
        pad.Atmosphere = Label(canvas, "Suit atmosphere", "", 12, 231, 256, 66, 20, Muted);
        pad.Atmosphere.enableWordWrapping = true;
        pad.WearFace = Graphic(canvas, "Suit custody control", WearBounds, KeyFace);
        pad.Wear = Label(canvas, "Don or return", "OFFLINE", 20, 338, 240, 50, 25, Muted);
        pad.Wear.alignment = TextAlignmentOptions.Center;
        pad.ServiceFace = Graphic(canvas, "Service control", ServiceBounds, KeyFace);
        pad.Service = Label(canvas, "Service", "SERVICE", 20, 416, 240, 46, 22, Muted);
        pad.Service.alignment = TextAlignmentOptions.Center;
        pad.Hint = Label(canvas, "Interaction", "READ ONLY", 12, 484, 256, 24, 17, Muted);
        pad.Feedback = Label(canvas, "Response", "", 12, 484, 256, 70, 16, Amber);
        pad.Feedback.enableWordWrapping = true;
    }

    internal Vector3 ControlPoint(int slot, bool service)
    {
        if ((uint)slot >= _pads.Length) throw new ArgumentOutOfRangeException(nameof(slot));
        var bounds = service ? ServiceBounds : WearBounds;
        return _pads[slot].Frame.TransformPoint(new((bounds.center.x - Width / 2) * PixelScale,
            (Height / 2 - bounds.center.y) * PixelScale, ScreenDepth));
    }

    // The owner supplies fresh state and calls after the walking camera's final pose.
    internal bool Update(SpacecraftEvaSlotState[]? state, int localSeat, bool interactive, string feedback)
    {
        if (_disposed || !_body.Ready || _root == null || !_root) return false;
        bool acceptsInput = interactive && ClientSpacecraftRider.WorldInputAvailable && !Cursor.visible;
        var camera = LocalPlayer.MainCam;
        int hovered = -1;
        bool service = false;
        if (acceptsInput && camera is not null && camera && camera.isActiveAndEnabled) hovered = FindPad(camera, out service);
        if (Time.unscaledTime >= _nextReadout || hovered != _hovered || service != _hoveredService)
        {
            _nextReadout = Time.unscaledTime + .2f;
            for (int i = 0; i < _pads.Length; i++) UpdateReadout(i, state, localSeat, interactive, hovered, service, feedback);
        }
        _hovered = hovered;
        _hoveredService = service;
        if (hovered < 0 || !(Input.GetKeyDown(KeyCode.E) || Input.GetMouseButtonDown(0))) return false;
        if (CanUse(state, localSeat, hovered, service)) _command(hovered, service);
        return true;
    }

    private int FindPad(Camera camera, out bool service)
    {
        var ray = new Ray(camera.transform.position, camera.transform.forward);
        int selected = -1;
        service = false;
        float nearest = 2.4f;
        for (int i = 0; i < _pads.Length; i++)
        {
            var frame = _pads[i].Frame;
            if (Vector3.Dot(ray.direction, frame.forward) <= .1f) continue;
            var plane = new Plane(frame.forward, frame.TransformPoint(new(0, 0, ScreenDepth)));
            if (!plane.Raycast(ray, out float distance) || distance < 0 || distance > nearest) continue;
            var point = frame.InverseTransformPoint(ray.GetPoint(distance));
            var pixel = new Vector2(point.x / PixelScale + Width / 2, Height / 2 - point.y / PixelScale);
            bool hitService = ServiceBounds.Contains(pixel);
            if (!hitService && !WearBounds.Contains(pixel)) continue;
            selected = i;
            service = hitService;
            nearest = distance;
        }
        if (selected < 0) return -1;
        int count = Physics.RaycastNonAlloc(ray, _hits, Mathf.Max(0, nearest - .003f), ~0, QueryTriggerInteraction.Ignore);
        if (count == _hits.Length) return -1;
        var player = LocalPlayer.Transform;
        for (int i = 0; i < count; i++)
        {
            var collider = _hits[i].collider;
            if (collider != null && (player == null || !collider.transform.IsChildOf(player))) return -1;
        }
        return selected;
    }

    private static bool CanUse(SpacecraftEvaSlotState[]? state, int seat, int slot, bool service)
    {
        if (state is not { Length: SpacecraftEvaSuit.Count } || seat is < 0 or >= SpacecraftEvaSuit.Count) return false;
        if (service) return state[slot].Seat == -1;
        if (state[slot].Seat == seat) return true;
        if (state[slot].Seat != -1) return false;
        foreach (var value in state) if (value.Seat == seat) return false;
        return true;
    }

    private void UpdateReadout(int slot, SpacecraftEvaSlotState[]? state, int seat, bool interactive,
        int hovered, bool service, string feedback)
    {
        var pad = _pads[slot];
        bool current = state is { Length: SpacecraftEvaSuit.Count };
        bool wear = interactive && CanUse(state, seat, slot, false);
        bool refill = interactive && CanUse(state, seat, slot, true);
        bool focused = hovered == slot;
        pad.Status.text = !current ? "AWAITING HOST" : state![slot].Seat == -1 ? "IN RACK"
            : state[slot].Seat == seat ? "WORN / YOU" : "WORN / CREW " + (state[slot].Seat + 1);
        pad.Status.color = current ? Green : Amber;
        pad.Wear.text = !current ? "OFFLINE" : state![slot].Seat == seat && seat >= 0 ? "RETURN"
            : state[slot].Seat == -1 ? "DON SUIT" : "IN USE";
        pad.Wear.color = wear ? Ink : Muted;
        pad.Service.color = refill ? Ink : Muted;
        pad.WearFace.color = focused && !service && wear ? new(.08f, .22f, .20f, 1) : KeyFace;
        pad.ServiceFace.color = focused && service && refill ? new(.08f, .22f, .20f, 1) : KeyFace;
        pad.Hint.text = !interactive ? "READ ONLY" : !current ? "CONTROLS OFFLINE"
            : focused && (service ? refill : wear) ? "E / LEFT CLICK" : "AIM AT CONTROL";
        pad.Hint.color = focused && (service ? refill : wear) ? Green : Muted;
        pad.Hint.gameObject.SetActive(!focused || string.IsNullOrEmpty(feedback));
        pad.Feedback.text = focused ? feedback : "";
        if (!current) { pad.Reserves.text = ""; pad.Atmosphere.text = ""; return; }
        var suit = state![slot].Suit;
        pad.Reserves.text = "O2  " + suit.OxygenMoles.ToString("F1", CultureInfo.InvariantCulture) + " mol\n"
            + "BAT  " + SpacecraftEvaHud.Percent(suit.BatteryKwh, SpacecraftEvaSuit.BatteryCapacityKwh) + "\n"
            + "FILTER  " + SpacecraftEvaHud.Percent(suit.FilterMoles, SpacecraftEvaSuit.FilterCapacityMoles);
        pad.Atmosphere.text = suit.Pressure.ToString("F1", CultureInfo.InvariantCulture) + " kPa\nCO2  "
            + (suit.CO2Fraction * 100).ToString("F2", CultureInfo.InvariantCulture) + "%";
        pad.Atmosphere.color = SpacecraftEvaHud.Alarm(suit) == "NOMINAL" ? Muted : Amber;
    }

    private Mesh LoadMesh()
    {
        using var stream = Resource("rack.msa");
        var data = SpacecraftMeshData.Read(stream, "EVA rack");
        var mesh = new Mesh();
        _mesh = mesh;
        mesh.name = data.Name;
        mesh.indexFormat = data.Vertices.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
        mesh.vertices = data.Vertices;
        mesh.normals = data.Normals;
        mesh.uv = data.Uv;
        mesh.SetTriangles(data.Triangles, 0);
        mesh.RecalculateBounds();
        mesh.RecalculateTangents();
        mesh.UploadMeshData(true);
        return mesh;
    }

    private Texture2D LoadTexture(string asset, bool linear)
    {
        using var stream = Resource(asset + ".png");
        if (!stream.CanSeek || stream.Length is < 24 or > 8 * 1024 * 1024)
            throw new InvalidDataException("The EVA texture size is invalid: " + asset);
        using var bytes = new MemoryStream((int)stream.Length);
        stream.CopyTo(bytes);
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear);
        _textures.Add(texture);
        texture.name = "EVA " + asset;
        if (!ImageConversion.LoadImage(texture, bytes.ToArray(), false) || texture.width != 2048 || texture.height != 2048)
            throw new InvalidDataException("The EVA texture could not be decoded: " + asset);
        texture.wrapMode = TextureWrapMode.Clamp;
        texture.filterMode = FilterMode.Trilinear;
        texture.anisoLevel = 8;
        texture.Apply(true, true);
        return texture;
    }

    private static Stream Resource(string name) => typeof(SpacecraftEvaRack).Assembly.GetManifestResourceStream(
        "Modstoke.Client.Assets.eva-" + name) ?? throw new InvalidOperationException("The EVA asset is missing: " + name);

    private Image Graphic(Transform parent, string name, Rect bounds, Color color)
    {
        var image = Node(parent, name, bounds.x, bounds.y, bounds.width, bounds.height).gameObject.AddComponent<Image>();
        image.material = _graphics;
        image.color = color;
        image.raycastTarget = false;
        return image;
    }

    private TextMeshProUGUI Label(Transform parent, string name, string value, float x, float y, float width,
        float height, float size, Color color)
    {
        var text = Node(parent, name, x, y, width, height).gameObject.AddComponent<TextMeshProUGUI>();
        _typography.Apply(text, TabletTextRole.Control);
        var source = text.fontSharedMaterial;
        if (source != null)
        {
            int id = source.GetInstanceID();
            if (!_fonts.TryGetValue(id, out var material))
            {
                material = new Material(source) { name = "EVA rack text" };
                _materials.Add(material);
                var shader = Shader.Find("TextMeshPro/Distance Field");
                if (shader != null) material.shader = shader;
                Depth(material);
                _fonts.Add(id, material);
            }
            text.fontSharedMaterial = material;
        }
        text.transform.localPosition += new Vector3(0, 0, -.4f);
        text.isOverlay = false;
        text.text = value;
        text.fontSize = size;
        text.color = color;
        text.richText = false;
        text.enableWordWrapping = false;
        text.raycastTarget = false;
        text.overflowMode = TextOverflowModes.Ellipsis;
        text.alignment = TextAlignmentOptions.MidlineLeft;
        return text;
    }

    private static RectTransform Node(Transform parent, string name, float x, float y, float width, float height)
    {
        var rect = new GameObject(name).AddComponent<RectTransform>();
        rect.gameObject.layer = parent.gameObject.layer;
        rect.SetParent(parent, false);
        rect.anchorMin = rect.anchorMax = rect.pivot = new(0, 1);
        rect.anchoredPosition = new(x, -y);
        rect.sizeDelta = new(width, height);
        return rect;
    }

    private static void Depth(Material material)
    {
        material.SetInt("unity_GUIZTestMode", (int)CompareFunction.LessEqual);
        if (material.HasProperty("_ZTestMode")) material.SetInt("_ZTestMode", (int)CompareFunction.LessEqual);
        ClientDeviceScreenMaterial.ConfigureOwnedUiMaterial(material);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_root != null && _root) { _root.SetActive(false); UnityEngine.Object.Destroy(_root); }
        _root = null;
        if (_mesh) UnityEngine.Object.Destroy(_mesh);
        _mesh = null;
        foreach (var material in _materials) if (material) UnityEngine.Object.Destroy(material);
        _materials.Clear();
        foreach (var texture in _textures) if (texture) UnityEngine.Object.Destroy(texture);
        _textures.Clear();
        _fonts.Clear();
        _typography.Dispose();
    }

    private sealed class Pad(Transform frame)
    {
        internal readonly Transform Frame = frame;
        internal TextMeshProUGUI Status = null!, Reserves = null!, Atmosphere = null!, Wear = null!, Service = null!, Hint = null!, Feedback = null!;
        internal Image WearFace = null!, ServiceFace = null!;
    }
}
