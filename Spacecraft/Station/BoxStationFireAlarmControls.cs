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

internal sealed class BoxStationFireAlarmControls : IDisposable
{
    private const int MaximumPads = 4;
    private const float Width = 520, Height = 620, PixelScale = .001f, ScreenDepth = -.152f, NearbyDistance = 6;
    private static readonly Color Ink = new(.92f, .94f, .95f, 1), Muted = new(.55f, .59f, .62f, 1);
    private static readonly Color Green = new(.38f, .83f, .67f, 1), Amber = new(1, .72f, .28f, 1), Red = new(1, .36f, .29f, 1);
    private static readonly BoxStationFireAlarmControl[] Controls =
        { BoxStationFireAlarmControl.Raise, BoxStationFireAlarmControl.Reset, BoxStationFireAlarmControl.Detection };
    private readonly BoxStationModel _model;
    private readonly Action<int, BoxStationFireAlarmControl> _command;
    private readonly ImmutableArray<BoxStationFireAlarmDevice> _devices;
    private readonly List<Pad> _pads = new(MaximumPads);
    private readonly Dictionary<(int X, int Z), List<int>> _cells = new();
    private readonly Vector3[] _positions;
    private readonly int[] _nearest = new int[MaximumPads];
    private readonly float[] _distances = new float[MaximumPads];
    private readonly List<Material> _materials = new();
    private readonly Dictionary<int, Material> _fonts = new();
    private readonly ClientTabletTypography _typography = new();
    private readonly Il2CppStructArray<RaycastHit> _hits = new(size: 32);
    private Material? _graphics;
    private float _nextNearby, _nextReadout;
    private Pad? _hovered;
    private int _hoveredControl = -1;
    private bool _disposed;

    internal BoxStationFireAlarmControls(BoxStationModel model, Action<int, BoxStationFireAlarmControl> command)
    {
        _model = model;
        _command = command;
        _devices = BoxStationFireAlarms.GetDevices(model.Layout);
        _positions = new Vector3[_devices.Length];
        for (int i = 0; i < _devices.Length; i++)
        {
            var point = model.FireAlarmControlFrame(i).localPosition;
            _positions[i] = point;
            var key = (Mathf.FloorToInt(point.x / NearbyDistance), Mathf.FloorToInt(point.z / NearbyDistance));
            if (!_cells.TryGetValue(key, out var entries)) _cells.Add(key, entries = new());
            entries.Add(i);
        }
    }

    internal void Update(BoxStationState? state, bool interactive, string feedback)
    {
        if (_disposed || !_model.Visible) return;
        if (state is { } snapshot && (snapshot.LayoutHash != _model.Layout.LayoutHash
            || snapshot.FireAlarms.IsDefault || snapshot.FireAlarms.Length != _devices.Length
            || snapshot.FireAreas.IsDefault || snapshot.FireAreas.Length != _model.Layout.Areas.Length
            || snapshot.Zones.Length != _model.Layout.Compartments.Length)) state = null;
        var camera = LocalPlayer.MainCam;
        bool cameraLive = camera is not null && camera && camera.isActiveAndEnabled;
        if (Time.unscaledTime >= _nextNearby && cameraLive)
        {
            _nextNearby = Time.unscaledTime + .2f;
            UpdateNearby(camera!.transform.position);
        }
        bool acceptsInput = interactive && state != null && ClientSpacecraftRider.WorldInputAvailable && !Cursor.visible;
        int control = -1;
        var hovered = acceptsInput && cameraLive ? FindPad(camera!, out control) : null;
        if (hovered != null && !state!.Value.FireAlarms[hovered.Alarm].Powered) hovered = null;
        if (hovered != null && (Input.GetKeyDown(KeyCode.E) || Input.GetMouseButtonDown(0)))
        {
            var alarm = state!.Value.FireAlarms[hovered.Alarm];
            if (Controls[control] != BoxStationFireAlarmControl.Raise || alarm.CooldownSeconds <= 0)
                _command(hovered.Alarm, Controls[control]);
            _nextReadout = 0;
        }
        if (Time.unscaledTime >= _nextReadout || !ReferenceEquals(hovered, _hovered) || control != _hoveredControl)
        {
            _nextReadout = Time.unscaledTime + .2f;
            foreach (var pad in _pads)
                if (pad.Active) UpdateReadout(pad, state, interactive, ReferenceEquals(pad, hovered) ? control : -1, feedback);
        }
        _hovered = hovered;
        _hoveredControl = control;
    }

    private void UpdateNearby(Vector3 worldEye)
    {
        var eye = _model.Root.transform.InverseTransformPoint(worldEye);
        int x = Mathf.FloorToInt(eye.x / NearbyDistance), z = Mathf.FloorToInt(eye.z / NearbyDistance), count = 0;
        for (int dz = -1; dz <= 1; dz++)
        for (int dx = -1; dx <= 1; dx++)
        {
            if (!_cells.TryGetValue((x + dx, z + dz), out var entries)) continue;
            foreach (int index in entries)
            {
                float distance = (_positions[index] - eye).sqrMagnitude;
                if (distance > NearbyDistance * NearbyDistance) continue;
                int slot = count;
                while (slot > 0 && distance < _distances[slot - 1]) slot--;
                if (slot >= MaximumPads) continue;
                for (int i = Math.Min(count, MaximumPads - 1); i > slot; i--)
                {
                    _nearest[i] = _nearest[i - 1];
                    _distances[i] = _distances[i - 1];
                }
                _nearest[slot] = index;
                _distances[slot] = distance;
                count = Math.Min(count + 1, MaximumPads);
            }
        }
        while (_pads.Count < count) AddPad();
        for (int i = 0; i < _pads.Count; i++)
        {
            var pad = _pads[i];
            bool active = i < count;
            if (active && pad.Alarm != _nearest[i])
            {
                pad.Alarm = _nearest[i];
                pad.Frame.SetParent(_model.FireAlarmControlFrame(pad.Alarm), false);
                pad.Frame.localPosition = Vector3.zero;
                pad.Frame.localRotation = Quaternion.Euler(0, 180, 0);
                var device = _devices[pad.Alarm];
                string area = _model.Layout.Areas[device.Area];
                pad.Area.text = area[(area.LastIndexOf('/') + 1)..].Replace('_', ' ').ToUpperInvariant();
                pad.Identity.text = "FIRE " + (pad.Alarm + 1).ToString("000", CultureInfo.InvariantCulture);
                pad.Area.text += " / AREA " + device.Area;
            }
            if (pad.Active != active) pad.Frame.gameObject.SetActive(active);
            pad.Active = active;
        }
        _nextReadout = 0;
    }

    private void AddPad()
    {
        var frame = new GameObject("Fire alarm controls").transform;
        frame.SetParent(_model.Root.transform, false);
        frame.gameObject.layer = frame.parent.gameObject.layer;
        var pad = new Pad(frame);
        _pads.Add(pad);
        var canvas = new GameObject("Fire alarm fitted display").AddComponent<RectTransform>();
        canvas.SetParent(frame, false);
        canvas.gameObject.layer = frame.gameObject.layer;
        canvas.localPosition = new(0, 0, ScreenDepth);
        canvas.localScale = Vector3.one * PixelScale;
        canvas.sizeDelta = new(Width, Height);
        canvas.gameObject.AddComponent<Canvas>().renderMode = RenderMode.WorldSpace;
        Graphic(canvas, "Temperature band", new(35, 119, 450, 32), new(.025f, .05f, .06f, 1));
        Graphic(canvas, "Latch band", new(35, 153, 450, 27), new(.065f, .025f, .025f, 1));
        pad.LatchStrip = Graphic(canvas, "Area latch indicator", new(37, 157, 3, 19), Muted);
        pad.LatchStrip.transform.localPosition += new Vector3(0, 0, -.1f);
        pad.Lens = Graphic(canvas, "Fire alarm status lens", new(370, 26, 90, 16), Muted);
        pad.Identity = Readout(canvas, "Alarm identity", 70, 20, 16, Ink);
        pad.Area = Readout(canvas, "Controlled area", 94, 18, 13, Muted);
        pad.Sample = Readout(canvas, "Sampled temperature", 122, 25, 20, Ink);
        pad.Latch = Readout(canvas, "Area latch", 156, 20, 15, Ink);
        pad.Sensor = Readout(canvas, "Detector condition", 182, 18, 13, Muted);
        pad.Power = Readout(canvas, "Power and cooldown", 206, 18, 13, Muted);
        pad.Hint = Readout(canvas, "Control instruction", 234, 32, 13, Ink);
        pad.Feedback = Readout(canvas, "Host response", 272, 36, 12, Amber);
        pad.Hint.enableWordWrapping = pad.Feedback.enableWordWrapping = true;
        for (int i = 0; i < 2; i++)
        {
            var bounds = ButtonBounds(Controls[i]);
            pad.Names[i] = Label(canvas, i == 0 ? "Raise command" : "Reset command", i == 0 ? "RAISE" : "RESET",
                bounds.x + 8, bounds.y + 12, bounds.width - 16, bounds.height - 24, 20, Ink);
            pad.Names[i].alignment = TextAlignmentOptions.Center;
        }
        pad.Names[2] = Label(canvas, "Detection toggle caption", "DETECTION", 50, 515, 255, 20, 16, Ink);
        pad.Detection = Label(canvas, "Detection toggle state", "--", 50, 539, 255, 18, 13, Muted);
    }

    private TextMeshProUGUI Readout(Transform parent, string name, float y, float height, float size, Color color)
    {
        var text = Label(parent, name, "", 47, y, 426, height, size, color);
        text.transform.localPosition += new Vector3(0, 0, 6.4f);
        return text;
    }

    private static Rect ButtonBounds(BoxStationFireAlarmControl control)
    {
        var center = BoxStationModel.FireAlarmButtonCenter(control);
        var size = BoxStationModel.FireAlarmButtonSize(control) / PixelScale;
        return new(Width / 2 - center.x / PixelScale - size.x / 2,
            Height / 2 - center.y / PixelScale - size.y / 2, size.x, size.y);
    }

    private Pad? FindPad(Camera camera, out int control)
    {
        control = -1;
        var ray = new Ray(camera.transform.position, camera.transform.forward);
        Pad? selected = null;
        float nearest = 2.4f;
        foreach (var pad in _pads)
        {
            if (!pad.Active || Vector3.Dot(ray.direction, pad.Frame.forward) <= .1f) continue;
            for (int i = 0; i < Controls.Length; i++)
            {
                var center = BoxStationModel.FireAlarmButtonCenter(Controls[i]);
                var plane = new Plane(pad.Frame.forward, pad.Frame.TransformPoint(new(0, 0, -center.z)));
                if (!plane.Raycast(ray, out float distance) || distance < 0 || distance > nearest) continue;
                var point = pad.Frame.InverseTransformPoint(ray.GetPoint(distance));
                var pixel = new Vector2(point.x / PixelScale + Width / 2, Height / 2 - point.y / PixelScale);
                if (!ButtonBounds(Controls[i]).Contains(pixel)) continue;
                selected = pad;
                nearest = distance;
                control = i;
            }
        }
        if (selected == null) return null;
        int count = Physics.RaycastNonAlloc(ray, _hits, MathF.Max(0, nearest - .003f), ~0, QueryTriggerInteraction.Ignore);
        if (count == _hits.Length) return null;
        var player = LocalPlayer.Transform;
        for (int i = 0; i < count; i++)
        {
            var collider = _hits[i].collider;
            if (collider != null && (player == null || !collider.transform.IsChildOf(player))) return null;
        }
        return selected;
    }

    private void UpdateReadout(Pad pad, BoxStationState? state, bool interactive, int focused, string feedback)
    {
        var device = _devices[pad.Alarm];
        bool powered = state is { } live && live.FireAlarms[pad.Alarm].Powered;
        bool ready = false;
        if (state is { } snapshot)
        {
            var alarm = snapshot.FireAlarms[pad.Alarm];
            var area = snapshot.FireAreas[device.Area];
            ready = alarm.CooldownSeconds <= 0;
            _model.SetFireAlarmDetection(pad.Alarm, alarm.Detecting);
            pad.Sample.text = "ZONE " + device.Zone + "  TEMP " + (powered
                && BoxStationSystems.TryGetAtmosphere(_model.Layout, snapshot, device.Cell, out var gas)
                ? gas.TemperatureC.ToString("F1", CultureInfo.InvariantCulture) + " C" : "-- C");
            pad.Latch.text = "AREA LATCH: " + (area.Latched ? "ACTIVE" : "CLEAR");
            pad.Latch.color = pad.LatchStrip.color = area.Latched ? Red : Muted;
            pad.Lens.color = !powered ? new(.025f, .012f, .012f, 1) : area.Latched ? Red : alarm.Detecting ? Green : Amber;
            pad.Sensor.text = "DETECTOR " + (alarm.Detecting ? "ON" : "OFF") + " / "
                + (!powered ? "SENSOR OFFLINE" : !alarm.Detecting ? "NOT MONITORING"
                    : alarm.ThermalHazard == 2 ? "HOT" : alarm.ThermalHazard == 1 ? "COLD" : "NORMAL");
            pad.Sensor.color = powered && (!alarm.Detecting || alarm.ThermalHazard != 0) ? Amber : Muted;
            pad.Power.text = !powered ? "ENVIRONMENT POWER OFFLINE" : ready ? "POWER ON / RAISE READY"
                : "POWER ON / RAISE WAIT " + alarm.CooldownSeconds.ToString("F1", CultureInfo.InvariantCulture) + "s";
            pad.Detection.text = alarm.Detecting ? "ON" : "OFF";
            pad.Detection.color = !powered ? Muted : alarm.Detecting ? Green : Amber;
        }
        else
        {
            pad.Sample.text = "ZONE " + device.Zone + "  TEMP -- C";
            pad.Latch.text = "TELEMETRY UNAVAILABLE";
            pad.Latch.color = pad.LatchStrip.color = Amber;
            pad.Lens.color = new(.025f, .012f, .012f, 1);
            pad.Sensor.text = "DETECTOR STATUS UNAVAILABLE";
            pad.Sensor.color = Muted;
            pad.Power.text = "CONTROLS UNAVAILABLE";
            pad.Detection.text = "--";
            pad.Detection.color = Muted;
        }
        pad.Hint.text = !powered ? "CONTROLS UNAVAILABLE" : !interactive ? "READ ONLY"
            : focused == 0 ? ready ? "E / CLICK: RAISE AREA FIRE ALARM" : "RAISE COOLDOWN ACTIVE"
            : focused == 1 ? "E / CLICK: RESET AREA LATCH"
            : focused == 2 ? "E / CLICK: TOGGLE DETECTION" : "AIM AT RAISE, RESET OR DETECTOR";
        pad.Hint.color = focused == 0 ? Red : Muted;
        pad.Feedback.text = focused >= 0 && feedback.Length > 0 ? feedback : "";
        for (int i = 0; i < Controls.Length; i++)
            pad.Names[i].color = !powered || i == 0 && !ready ? Muted : i == focused ? i == 0 ? Red : Green : Ink;
    }

    private Image Graphic(Transform parent, string name, Rect bounds, Color color)
    {
        if (_graphics == null)
        {
            _graphics = new Material(UnityEngine.UI.Graphic.defaultGraphicMaterial) { name = "Fire alarm display graphics" };
            _materials.Add(_graphics);
            _graphics.SetInt("unity_GUIZTestMode", (int)CompareFunction.LessEqual);
            if (_graphics.HasProperty("_ZTestMode")) _graphics.SetInt("_ZTestMode", (int)CompareFunction.LessEqual);
            ClientDeviceScreenMaterial.ConfigureOwnedUiMaterial(_graphics);
        }
        var rect = new GameObject(name).AddComponent<RectTransform>();
        rect.gameObject.layer = parent.gameObject.layer;
        rect.SetParent(parent, false);
        rect.anchorMin = rect.anchorMax = rect.pivot = new(0, 1);
        rect.anchoredPosition = new(bounds.x, -bounds.y);
        rect.sizeDelta = new(bounds.width, bounds.height);
        rect.localPosition += new Vector3(0, 0, 6.4f);
        var image = rect.gameObject.AddComponent<Image>();
        image.material = _graphics;
        image.color = color;
        image.raycastTarget = false;
        return image;
    }

    private TextMeshProUGUI Label(Transform parent, string name, string value, float x, float y, float width, float height, float size, Color color)
    {
        var rect = new GameObject(name).AddComponent<RectTransform>();
        rect.gameObject.layer = parent.gameObject.layer;
        rect.SetParent(parent, false);
        rect.anchorMin = rect.anchorMax = rect.pivot = new(0, 1);
        rect.anchoredPosition = new(x, -y);
        rect.sizeDelta = new(width, height);
        var text = rect.gameObject.AddComponent<TextMeshProUGUI>();
        _typography.Apply(text, TabletTextRole.Control);
        var source = text.fontSharedMaterial;
        if (source != null)
        {
            int id = source.GetInstanceID();
            if (!_fonts.TryGetValue(id, out var material))
            {
                material = new Material(source) { name = "Fire alarm control text" };
                _materials.Add(material);
                var shader = Shader.Find("TextMeshPro/Distance Field");
                if (shader != null) material.shader = shader;
                material.SetInt("unity_GUIZTestMode", (int)CompareFunction.LessEqual);
                if (material.HasProperty("_ZTestMode")) material.SetInt("_ZTestMode", (int)CompareFunction.LessEqual);
                ClientDeviceScreenMaterial.ConfigureOwnedUiMaterial(material);
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var pad in _pads) if (pad.Frame) UnityEngine.Object.Destroy(pad.Frame.gameObject);
        _pads.Clear();
        foreach (var material in _materials) if (material) UnityEngine.Object.Destroy(material);
        _materials.Clear();
        _fonts.Clear();
        _graphics = null;
        _hovered = null;
        _typography.Dispose();
    }

    private sealed class Pad(Transform frame)
    {
        internal readonly Transform Frame = frame;
        internal int Alarm = -1;
        internal bool Active;
        internal TextMeshProUGUI Identity = null!, Area = null!, Sample = null!, Latch = null!, Sensor = null!,
            Power = null!, Hint = null!, Feedback = null!, Detection = null!;
        internal Image LatchStrip = null!, Lens = null!;
        internal readonly TextMeshProUGUI[] Names = new TextMeshProUGUI[3];
    }
}

