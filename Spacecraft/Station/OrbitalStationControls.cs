// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Modstoke.Loader;
using Modstoke.Sons;
using TheForest.Utils;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace Modstoke.Client;

internal sealed class OrbitalStationControls : IDisposable
{
    private const float Width = 320, Height = 440, PixelScale = .00075f, ScreenDepth = -.013f;
    private static readonly Rect ControlBounds = new(18, 292, 284, 64);
    private static readonly Color Ink = new(.92f, .94f, .95f, 1);
    private static readonly Color Muted = new(.55f, .59f, .62f, 1);
    private static readonly Color Green = new(.38f, .83f, .67f, 1);
    private static readonly Color Amber = new(1, .72f, .28f, 1);
    private static readonly Color KeyFace = new(.04f, .065f, .07f, 1);
    private const float ControlDistance = 10, RefreshInterval = .2f;
    private const int MaximumPads = 16;
    private readonly BoxStationModel _model;
    private readonly Action<int> _command;
    private readonly List<Pad> _pads = new(MaximumPads);
    private readonly Dictionary<(int X, int Z), List<int>> _cells = new();
    private ControlSource[] _sources = Array.Empty<ControlSource>();
    private ImmutableArray<BoxStationDoor> _indexedDoors;
    private readonly ImmutableArray<int>[] _fireDoorAreas;
    private readonly int[] _nearest = new int[MaximumPads];
    private readonly float[] _distances = new float[MaximumPads];
    private readonly List<Material> _materials = new();
    private readonly Dictionary<int, Material> _fonts = new();
    private readonly ClientTabletTypography _typography = new();
    private readonly Il2CppStructArray<RaycastHit> _hits = new(size: 32);
    private Material _bezel = null!, _surface = null!, _graphics = null!;
    private Pad? _hovered;
    private float _nextReadout, _nextNearby;
    private bool _disposed;

    // The owner supplies fresh authority state and calls Update after the final camera pose.
    internal OrbitalStationControls(BoxStationModel model, Action<int> command)
    {
        _model = model;
        _command = command;
        _fireDoorAreas = new ImmutableArray<int>[model.Layout.Doors.Length];
        for (int door = 0; door < _fireDoorAreas.Length; door++)
            _fireDoorAreas[door] = BoxStationFireAlarms.GetDoorAreas(model.Layout, door);
        try
        {
            var shader = Shader.Find("HDRP/Lit");
            if (!shader) throw new InvalidOperationException("The station control surface shader is unavailable.");
            _bezel = new Material(shader) { name = "Station control housing" };
            _materials.Add(_bezel);
            _bezel.SetColor("_BaseColor", new Color(.12f, .15f, .17f, 1));
            _bezel.SetFloat("_Metallic", .45f);
            _bezel.SetFloat("_Smoothness", .35f);
            _surface = ClientDeviceScreenMaterial.CreateDisplayMaterial("Station control glass");
            _materials.Add(_surface);
            ClientDeviceScreenMaterial.SetDisplay(_surface, new Color(.012f, .018f, .021f, 1), null);
            _graphics = new Material(UnityEngine.UI.Graphic.defaultGraphicMaterial) { name = "Station control graphics" };
            _materials.Add(_graphics);
            Depth(_graphics);
            model.DoorsChanging += ReleaseDoorBindings;
            IndexDoors();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void ReleaseDoorBindings()
    {
        foreach (var pad in _pads)
        {
            pad.Frame.gameObject.SetActive(false);
            pad.Frame.SetParent(_model.Root.transform, false);
            pad.Active = false;
            pad.Source = -1;
        }
        _hovered = null;
        _cells.Clear();
        _sources = Array.Empty<ControlSource>();
        _indexedDoors = default;
        _nextNearby = _nextReadout = 0;
    }

    private void IndexDoors()
    {
        var definitions = _model.DoorDefinitions;
        int count = 0;
        for (int door = 0; door < definitions.Length; door++) count += _model.DoorGateCount(door) * 2;
        _sources = new ControlSource[count];
        _cells.Clear();
        int source = 0;
        for (int door = 0; door < definitions.Length; door++)
        for (int gate = 0; gate < _model.DoorGateCount(door); gate++)
        {
            IndexControl(source++, door, false, gate);
            IndexControl(source++, door, true, gate);
        }
        _indexedDoors = definitions;
    }

    private void IndexControl(int index, int door, bool back, int gate)
    {
        var position = _model.Root.transform.InverseTransformPoint(_model.DoorControlPoint(door, back, gate));
        var key = (Mathf.FloorToInt(position.x / ControlDistance), Mathf.FloorToInt(position.z / ControlDistance));
        if (!_cells.TryGetValue(key, out var entries)) _cells.Add(key, entries = new List<int>());
        entries.Add(index);
        _sources[index] = new(_model.DoorDefinitions[door].Id, back, gate, position);
    }

    private void UpdateNearby(Vector3 eye)
    {
        var localEye = _model.Root.transform.InverseTransformPoint(eye);
        int x = Mathf.FloorToInt(localEye.x / ControlDistance), z = Mathf.FloorToInt(localEye.z / ControlDistance);
        int count = 0;
        for (int dz = -1; dz <= 1; dz++)
        for (int dx = -1; dx <= 1; dx++)
        {
            if (!_cells.TryGetValue((x + dx, z + dz), out var entries)) continue;
            foreach (int source in entries)
            {
                float distance = (_sources[source].Position - localEye).sqrMagnitude;
                if (distance > ControlDistance * ControlDistance) continue;
                int slot = count;
                while (slot > 0 && distance < _distances[slot - 1]) slot--;
                if (slot >= MaximumPads) continue;
                int last = Math.Min(count, MaximumPads - 1);
                for (int i = last; i > slot; i--)
                {
                    _nearest[i] = _nearest[i - 1];
                    _distances[i] = _distances[i - 1];
                }
                _nearest[slot] = source;
                _distances[slot] = distance;
                count = Math.Min(count + 1, MaximumPads);
            }
        }
        while (_pads.Count < count) AddPad();
        for (int i = 0; i < _pads.Count; i++)
        {
            var pad = _pads[i];
            bool active = i < count;
            if (active && pad.Source != _nearest[i]) BindPad(pad, _nearest[i]);
            if (pad.Active != active) pad.Frame.gameObject.SetActive(active);
            pad.Active = active;
        }
        _nextReadout = 0;
    }

    private void BindPad(Pad pad, int index)
    {
        var source = _sources[index];
        if (!_model.TryDoorIndex(source.Door, out int doorIndex)) throw new InvalidOperationException("The door control source is unavailable.");
        pad.Source = index;
        pad.Door = source.Door;
        pad.Frame.SetParent(_model.DoorGateFrame(doorIndex, source.Gate), false);
        pad.Frame.gameObject.layer = pad.Frame.parent.gameObject.layer;
        pad.Frame.position = _model.DoorControlPoint(doorIndex, source.Back, source.Gate);
        // Both screens face into the aperture from the fixed jamb, clear of the leaf travel.
        pad.Frame.localRotation = Quaternion.Euler(0, 90, 0);
        var door = _model.DoorDefinitions[doorIndex];
        pad.Identification.text = "D" + (source.Door + 1).ToString("0000", CultureInfo.InvariantCulture) + " / " + door.Kind.ToUpperInvariant();
        pad.LocalZone = pad.OtherZone = -2;
        pad.LocalCell = pad.OtherCell = -1;
        var frame = _model.DoorGateFrame(doorIndex, source.Gate);
        float side = Vector3.Dot(pad.Frame.position - frame.position, frame.forward);
        bool opposed = door.Ports.Length == 2;
        foreach (var port in door.Ports)
        {
            var direction = port.Direction switch
            {
                1 => Vector3.forward, 2 => Vector3.back, 4 => Vector3.right, 8 => Vector3.left, _ => Vector3.zero
            };
            float facing = Vector3.Dot(_model.Root.transform.TransformDirection(direction), frame.forward);
            if (MathF.Abs(facing) < .5f) opposed = false;
            if (facing * side > 0) { pad.LocalZone = port.Zone; pad.LocalCell = port.Cell; }
            else { pad.OtherZone = port.Zone; pad.OtherCell = port.Cell; }
        }
        pad.Range = !opposed || pad.LocalZone == -2 || pad.OtherZone == -2;
        pad.LocalCaption.text = pad.Range ? "LOWEST CONNECTED PRESSURE" : "THIS SIDE";
        pad.OtherCaption.text = pad.Range ? "HIGHEST CONNECTED PRESSURE" : "OTHER SIDE";
        pad.LocalName.text = pad.Range ? "ALL CONNECTED PORTS" : ZoneName(pad.LocalZone);
        pad.OtherName.text = pad.Range ? "ALL CONNECTED PORTS" : ZoneName(pad.OtherZone);
    }

    private void AddPad()
    {
        var frame = new GameObject("Door control").transform;
        frame.SetParent(_model.Root.transform, false);
        frame.gameObject.layer = frame.parent.gameObject.layer;
        var pad = new Pad(frame);
        _pads.Add(pad);
        Box(frame, "Mounting housing", new(0, 0, .025f), new(.26f, .35f, .06f), _bezel);
        Box(frame, "Recessed glass", new(0, 0, -.008f), new(.24f, .33f, .008f), _surface);
        Box(frame, "Upper bezel", new(0, .17f, -.01f), new(.26f, .01f, .01f), _bezel);
        Box(frame, "Lower bezel", new(0, -.17f, -.01f), new(.26f, .01f, .01f), _bezel);
        Box(frame, "Left bezel", new(-.125f, 0, -.01f), new(.01f, .33f, .01f), _bezel);
        Box(frame, "Right bezel", new(.125f, 0, -.01f), new(.01f, .33f, .01f), _bezel);
        var canvas = new GameObject("Door control legends").AddComponent<RectTransform>();
        canvas.SetParent(frame, false);
        canvas.gameObject.layer = frame.gameObject.layer;
        canvas.localPosition = new(0, 0, ScreenDepth);
        canvas.localScale = Vector3.one * PixelScale;
        canvas.sizeDelta = new(Width, Height);
        canvas.gameObject.AddComponent<Canvas>().renderMode = RenderMode.WorldSpace;
        pad.Identification = Label(canvas, "Door identification", "", 18, 17, 284, 26, 21, Ink);
        Graphic(canvas, "Header rule", 18, 55, 284, 2, Muted);
        pad.LocalCaption = Label(canvas, "Local caption", "THIS SIDE", 18, 67, 284, 18, 14, Muted);
        pad.LocalName = Label(canvas, "Local compartment", "", 18, 87, 284, 26, 18, Ink);
        pad.Local = Label(canvas, "Local pressure", "AWAITING HOST", 18, 113, 284, 27, 21, Ink);
        pad.OtherCaption = Label(canvas, "Other side caption", "OTHER SIDE", 18, 145, 284, 18, 14, Muted);
        pad.OtherName = Label(canvas, "Other compartment", "", 18, 165, 284, 26, 18, Ink);
        pad.Other = Label(canvas, "Other side pressure", "AWAITING HOST", 18, 191, 284, 27, 21, Ink);
        pad.State = Label(canvas, "Door state", "AWAITING HOST", 18, 220, 284, 26, 21, Amber);
        pad.Detail = Label(canvas, "Interlock state", "CONTROLS OFFLINE", 18, 250, 284, 19, 16, Muted);
        pad.Fire = Label(canvas, "Fire latch state", "", 18, 272, 284, 17, 13, Amber);
        pad.Face = Graphic(canvas, "Door command face", 18, 292, 284, 64, KeyFace);
        pad.Command = Label(canvas, "Door command", "OFFLINE", 28, 302, 264, 44, 22, Muted);
        pad.Command.alignment = TextAlignmentOptions.Center;
        pad.Hint = Label(canvas, "Control instruction", "READ ONLY", 18, 369, 284, 21, 16, Muted);
        pad.Feedback = Label(canvas, "Door response", "", 18, 398, 284, 34, 14, Amber);
        pad.Feedback.enableWordWrapping = true;
    }

    internal void Update(BoxStationState? state, bool interactive, string feedback)
    {
        if (_disposed || !_model.Visible) return;
        if (_indexedDoors != _model.DoorDefinitions) IndexDoors();
        if (state is { } snapshot && (snapshot.LayoutHash != _model.Layout.LayoutHash
            || snapshot.DoorRevision != _model.DoorRevision || snapshot.Doors.Length != _model.DoorDefinitions.Length
            || snapshot.Zones.Length != _model.Layout.Compartments.Length)) state = null;
        var camera = LocalPlayer.MainCam;
        bool cameraLive = camera is not null && camera && camera.isActiveAndEnabled;
        if (Time.unscaledTime >= _nextNearby)
        {
            _nextNearby = Time.unscaledTime + RefreshInterval;
            if (cameraLive) UpdateNearby(camera!.transform.position);
        }
        var acceptsInput = interactive && state is { Powered: true } && ClientSpacecraftRider.WorldInputAvailable && !Cursor.visible;
        // Walking retains its owner input lease; interactive is restricted to that walking owner.
        var hovered = acceptsInput && cameraLive ? FindPad(camera!) : null;
        if (Time.unscaledTime >= _nextReadout)
        {
            _nextReadout = Time.unscaledTime + RefreshInterval;
            foreach (var pad in _pads)
                if (pad.Active) UpdateReadout(pad, state, interactive, ReferenceEquals(pad, hovered), feedback);
        }
        else if (!ReferenceEquals(hovered, _hovered))
        {
            if (_hovered is { Active: true }) UpdateFocus(_hovered, state, interactive, false, "");
            if (hovered != null) UpdateFocus(hovered, state, interactive, true, feedback);
        }
        _hovered = hovered;
        if (hovered != null && CanUse(hovered.Door, state)
            && (Input.GetKeyDown(KeyCode.E) || Input.GetMouseButtonDown(0))) _command(hovered.Door);
    }

    private Pad? FindPad(Camera camera)
    {
        var ray = new Ray(camera.transform.position, camera.transform.forward);
        Pad? selected = null;
        float nearest = 2.4f;
        foreach (var pad in _pads)
        {
            if (!pad.Active) continue;
            if (Vector3.Dot(ray.direction, pad.Frame.forward) <= .1f) continue;
            var plane = new Plane(pad.Frame.forward, pad.Frame.TransformPoint(new(0, 0, ScreenDepth)));
            if (!plane.Raycast(ray, out float distance) || distance < 0 || distance > nearest) continue;
            var point = pad.Frame.InverseTransformPoint(ray.GetPoint(distance));
            var pixel = new Vector2(point.x / PixelScale + Width / 2, Height / 2 - point.y / PixelScale);
            if (!ControlBounds.Contains(pixel)) continue;
            selected = pad;
            nearest = distance;
        }
        if (selected == null) return null;
        int count = Physics.RaycastNonAlloc(ray, _hits, Mathf.Max(0, nearest - .003f), ~0, QueryTriggerInteraction.Ignore);
        // A full buffer cannot establish that an unseen nearer obstacle is absent.
        if (count == _hits.Length) return null;
        var player = LocalPlayer.Transform;
        for (int i = 0; i < count; i++)
        {
            var collider = _hits[i].collider;
            if (collider != null && (player == null || !collider.transform.IsChildOf(player))) return null;
        }
        return selected;
    }

    private bool CanUse(int door, BoxStationState? state)
    {
        if (state is not { Powered: true } value || !_model.TryDoorIndex(door, out int index)) return false;
        // An interlocked door can request a cycle that first seals its paired door.
        return value.Doors[index].Powered;
    }

    private void UpdateReadout(Pad pad, BoxStationState? state, bool interactive, bool focused, string feedback)
    {
        if (!_model.TryDoorIndex(pad.Door, out int index)) state = null;
        PressureRange(pad.Door, state, out float minimum, out float maximum);
        pad.Local.text = pad.Range ? RangeLabel(minimum, state) : PressureLabel(pad.LocalCell, state);
        pad.Other.text = pad.Range ? RangeLabel(maximum, state) : PressureLabel(pad.OtherCell, state);
        bool online = state is { Powered: true } powered && powered.Doors[index].Powered;
        bool usable = interactive && CanUse(pad.Door, state);
        string action = "OFFLINE", status = "AWAITING HOST", detail = "CONTROLS OFFLINE";
        if (state is { } value)
        {
            var door = value.Doors[index];
            bool open = door.TargetOpen;
            float amount = door.Amount;
            bool cycling = door.Cycling;
            status = amount >= .999f ? "OPEN" : amount <= .001f ? "SEALED" : open ? "OPENING" : "CLOSING";
            if (!online && amount > .001f && amount < .999f) status = "STOPPED";
            if (cycling) status = online ? "CYCLING" : "CYCLE PAUSED";
            else if (online && door.Interlocked && open && amount < .999f) status = "INTERLOCKED";
            if (!online) detail = value.Powered ? "DOOR POWER OFFLINE" : "NO STATION POWER";
            else if (door.Interlocked) detail = "DOOR INTERLOCK ACTIVE";
            else if (cycling) detail = "EQUALIZING PRESSURE";
            else if (maximum - minimum > 5) detail = "PRESSURE DIFFERENCE";
            else detail = "PRESSURE MATCHED";
            action = !online ? "OFFLINE" : cycling ? "CANCEL CYCLE" : open ? "CLOSE DOOR"
                : door.Interlocked ? "CYCLE / OPEN"
                : maximum - minimum > 5 ? "EQUALIZE / OPEN" : "OPEN DOOR";
        }
        pad.State.text = status;
        pad.State.color = online && state is { } current && !current.Doors[index].Interlocked ? Green : Amber;
        pad.Detail.text = detail;
        UpdateFireReadout(pad, state);
        pad.Command.text = action;
        pad.Command.color = usable ? Ink : Muted;
        UpdateFocus(pad, state, interactive, focused, feedback);
    }

    private void UpdateFireReadout(Pad pad, BoxStationState? state)
    {
        if ((uint)pad.Door >= _fireDoorAreas.Length) { pad.Fire.text = ""; return; }
        var areas = _fireDoorAreas[pad.Door];
        if (areas.IsEmpty) { pad.Fire.text = ""; return; }
        if (state is not { } snapshot || snapshot.FireAreas.IsDefault || snapshot.FireAreas.Length != _model.Layout.Areas.Length)
        {
            pad.Fire.text = "FIRE STATUS UNAVAILABLE";
            pad.Fire.color = Amber;
            return;
        }
        bool latched = false;
        float reclose = float.MaxValue;
        foreach (int area in areas)
        {
            var fire = snapshot.FireAreas[area];
            if (!fire.Latched) continue;
            latched = true;
            reclose = Math.Min(reclose, fire.RecloseSeconds);
        }
        bool released = snapshot.Doors[pad.Door].TargetOpen || snapshot.Doors[pad.Door].Cycling;
        pad.Fire.text = !latched ? "FIRE LATCH CLEAR" : released
            ? "FIRE LATCH / RELEASE " + reclose.ToString("F1", CultureInfo.InvariantCulture) + "s" : "FIRE LATCH ACTIVE";
        pad.Fire.color = latched ? Amber : Muted;
    }

    private void UpdateFocus(Pad pad, BoxStationState? state, bool interactive, bool focused, string feedback)
    {
        bool online = CanUse(pad.Door, state), usable = interactive && online;
        pad.Face.color = focused && usable ? new Color(.08f, .22f, .20f, 1) : KeyFace;
        pad.Hint.text = !interactive ? "READ ONLY" : !online ? "CONTROLS OFFLINE"
            : focused ? "E / LEFT CLICK TO OPERATE" : "AIM AT CONTROL TO OPERATE";
        pad.Hint.color = focused && usable ? Green : Muted;
        pad.Feedback.text = focused ? feedback : "";
    }

    private string ZoneName(int zone) => zone < 0 ? "VACUUM" : _model.Layout.Compartments[zone].Name;

    private string PressureLabel(int cell, BoxStationState? state)
    {
        if (state is not { } value) return "AWAITING HOST";
        return PressureAt(cell, value).ToString("F1", CultureInfo.InvariantCulture) + " kPa";
    }

    private float PressureAt(int cell, BoxStationState state)
        => BoxStationSystems.TryGetAtmosphere(_model.Layout, state, cell, out var gas) ? gas.Pressure : 0;

    private static string RangeLabel(float pressure, BoxStationState? state) => state.HasValue
        ? pressure.ToString("F1", CultureInfo.InvariantCulture) + " kPa" : "AWAITING HOST";

    private void PressureRange(int stableId, BoxStationState? state, out float minimum, out float maximum)
    {
        minimum = maximum = 0;
        if (state is not { } value || !_model.TryDoorIndex(stableId, out int index)) return;
        var door = _model.DoorDefinitions[index];
        minimum = maximum = PressureAt(door.Cell, value);
        foreach (var port in door.Ports)
        {
            float pressure = PressureAt(port.Cell, value);
            minimum = Math.Min(minimum, pressure);
            maximum = Math.Max(maximum, pressure);
        }
    }

    private Image Graphic(Transform parent, string name, float x, float y, float width, float height, Color color)
    {
        var image = Node(parent, name, x, y, width, height).gameObject.AddComponent<Image>();
        image.material = _graphics;
        image.color = color;
        image.raycastTarget = false;
        return image;
    }

    private TextMeshProUGUI Label(Transform parent, string name, string value, float x, float y, float width, float height,
        float size, Color color)
    {
        var text = Node(parent, name, x, y, width, height).gameObject.AddComponent<TextMeshProUGUI>();
        _typography.Apply(text, TabletTextRole.Control);
        var source = text.fontSharedMaterial;
        if (source != null)
        {
            int id = source.GetInstanceID();
            if (!_fonts.TryGetValue(id, out var material))
            {
                material = new Material(source) { name = "Station control text" };
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

    private static void Box(Transform parent, string name, Vector3 position, Vector3 size, Material material)
    {
        var node = GameObject.CreatePrimitive(PrimitiveType.Cube);
        node.name = name;
        node.layer = parent.gameObject.layer;
        node.transform.SetParent(parent, false);
        node.transform.localPosition = position;
        node.transform.localScale = size;
        var collider = node.GetComponent<Collider>();
        if (collider != null) { collider.enabled = false; UnityEngine.Object.Destroy(collider); }
        var renderer = node.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        ClientDeviceScreenMaterial.ConfigureRenderer(renderer);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _model.DoorsChanging -= ReleaseDoorBindings;
        foreach (var pad in _pads)
            if (pad.Frame != null) UnityEngine.Object.Destroy(pad.Frame.gameObject);
        _pads.Clear();
        _cells.Clear();
        _sources = Array.Empty<ControlSource>();
        _indexedDoors = default;
        _hovered = null;
        foreach (var material in _materials)
            if (material != null) UnityEngine.Object.Destroy(material);
        _materials.Clear();
        _fonts.Clear();
        _typography.Dispose();
    }

    private readonly record struct ControlSource(int Door, bool Back, int Gate, Vector3 Position);

    private sealed class Pad(Transform frame)
    {
        internal int Source = -1, Door, LocalZone, OtherZone, LocalCell, OtherCell;
        internal bool Active, Range;
        internal readonly Transform Frame = frame;
        internal TextMeshProUGUI Identification = null!, LocalCaption = null!, OtherCaption = null!, LocalName = null!, OtherName = null!;
        internal TextMeshProUGUI Local = null!, Other = null!, State = null!, Detail = null!, Fire = null!, Command = null!, Hint = null!, Feedback = null!;
        internal Image Face = null!;
    }
}
