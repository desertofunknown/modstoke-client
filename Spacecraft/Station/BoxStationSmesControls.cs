// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Modstoke.Sons;
using TheForest.Utils;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace Modstoke.Client;

internal sealed class BoxStationSmesControls : IDisposable
{
    private const int MaximumPads = 4;
    private const float Width = 720, Height = 300, PixelScale = .001f, CanvasHeight = 1.49f;
    private const float ScreenDepth = -.446f, NearbyDistance = 6, KeyWidth = .12f, KeyHeight = .030f;
    private static readonly Color Ink = new(.92f, .94f, .95f, 1), Muted = new(.55f, .59f, .62f, 1);
    private static readonly Color Green = new(.38f, .83f, .67f, 1), Amber = new(1, .72f, .28f, 1);
    private static readonly Color KeyFill = new(.125f, .20f, .24f, 1), KeyBorder = new(.32f, .39f, .42f, 1);
    private static readonly Color DisabledFill = new(.08f, .12f, .14f, 1), DisabledBorder = new(.17f, .23f, .26f, 1);
    private static readonly Color HoverFill = new(.10f, .26f, .22f, 1);
    private static readonly string[] KeyNames = { "--", "-10", "+10", "MIN", "MAX", "--", "-10", "+10", "MIN", "MAX" };
    private readonly BoxStationModel _model;
    private readonly Action<int, BoxStationSmesControl> _command;
    private readonly ImmutableArray<BoxStationSmes> _devices;
    private readonly List<Pad> _pads = new(MaximumPads);
    private readonly Vector3[] _positions;
    private readonly int[] _nearest = new int[MaximumPads];
    private readonly float[] _distances = new float[MaximumPads];
    private readonly List<Material> _materials = new();
    private readonly Dictionary<int, Material> _fonts = new();
    private readonly ClientTabletTypography _typography = new();
    private readonly Il2CppStructArray<RaycastHit> _hits = new(size: 32);
    private Material? _graphics;
    private float _nextNearby, _nextReadout;
    private int _hoveredControl = -1;
    private Pad? _hovered;
    private bool _available, _interactive, _disposed;

    internal BoxStationSmesControls(BoxStationModel model, Action<int, BoxStationSmesControl> command)
    {
        _model = model;
        _command = command;
        _devices = BoxStationPower.GetSmes(model.Layout);
        _positions = new Vector3[_devices.Length];
        for (int i = 0; i < _positions.Length; i++)
            _positions[i] = model.Root.transform.InverseTransformPoint(model.SmesControlFrame(i).TransformPoint(new(0, CanvasHeight, -ScreenDepth)));
    }

    internal void Update(BoxStationState? state, bool interactive, string feedback)
    {
        if (_disposed) return;
        if (state is { } snapshot && (snapshot.LayoutHash != _model.Layout.LayoutHash
            || !BoxStationPower.ValidSmesState(_model.Layout, snapshot.Smes, snapshot.Cables))) state = null;
        if (!_model.Visible) { _hovered = null; _hoveredControl = -1; return; }
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
        if (hovered != null && ControlEnabled(state!.Value.Smes[hovered.Device], _devices[hovered.Device], control)
            && (Input.GetKeyDown(KeyCode.E) || Input.GetMouseButtonDown(0)))
        {
            _command(hovered.Device, (BoxStationSmesControl)control);
            _nextReadout = 0;
        }
        if (Time.unscaledTime >= _nextReadout || !ReferenceEquals(hovered, _hovered) || control != _hoveredControl
            || _available != (state != null) || _interactive != interactive)
        {
            _nextReadout = Time.unscaledTime + .2f;
            foreach (var pad in _pads)
                if (pad.Active) UpdateReadout(pad, state, interactive, ReferenceEquals(pad, hovered) ? control : -1, feedback);
        }
        _hovered = hovered;
        _hoveredControl = control;
        _available = state != null;
        _interactive = interactive;
    }

    private void UpdateNearby(Vector3 worldEye)
    {
        var eye = _model.Root.transform.InverseTransformPoint(worldEye);
        int count = 0;
        for (int index = 0; index < _positions.Length; index++)
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
        while (_pads.Count < count) AddPad();
        for (int i = 0; i < _pads.Count; i++)
        {
            var pad = _pads[i];
            bool active = i < count;
            if (active && pad.Device != _nearest[i])
            {
                pad.Device = _nearest[i];
                pad.Frame.SetParent(_model.SmesControlFrame(pad.Device), false);
                pad.Frame.localPosition = Vector3.zero;
                pad.Frame.localRotation = Quaternion.Euler(0, 180, 0);
            }
            if (pad.Active != active) pad.Frame.gameObject.SetActive(active);
            pad.Active = active;
        }
        _nextReadout = 0;
    }

    private void AddPad()
    {
        var frame = new GameObject("SMES controls").transform;
        frame.SetParent(_model.Root.transform, false);
        frame.gameObject.layer = frame.parent.gameObject.layer;
        var pad = new Pad(frame);
        _pads.Add(pad);
        var canvas = new GameObject("SMES fitted display").AddComponent<RectTransform>();
        canvas.SetParent(frame, false);
        canvas.gameObject.layer = frame.gameObject.layer;
        canvas.localPosition = new(0, CanvasHeight, ScreenDepth);
        canvas.localScale = Vector3.one * PixelScale;
        canvas.sizeDelta = new(Width, Height);
        canvas.gameObject.AddComponent<Canvas>().renderMode = RenderMode.WorldSpace;
        Graphic(canvas, "Display backing", new(0, 0, Width, Height), new(20f / 255, 35f / 255, 40f / 255, 1), .2f);
        pad.Header = Label(canvas, "Stored energy", new(20, 15, 680, 30), 21, Ink);
        pad.ChargeTrack = Graphic(canvas, "Charge track", new(20, 50, 680, 4), KeyBorder);
        pad.Charge = Graphic(canvas, "Stored charge", new(20, 50, 0, 4), Green, -.1f);
        Graphic(canvas, "Input divider", new(20, 62, 680, 1), DisabledBorder);
        Graphic(canvas, "Output divider", new(20, 167, 680, 1), DisabledBorder);
        pad.Input = Label(canvas, "Input flow and limit", new(20, 70, 680, 30), 21, Ink);
        pad.Output = Label(canvas, "Output flow and limit", new(20, 175, 680, 30), 21, Ink);
        pad.Status = Label(canvas, "Connection status", new(20, 267, 680, 26), 18, Muted);
        for (int i = 0; i < KeyNames.Length; i++)
        {
            var bounds = ButtonBounds(i);
            pad.Borders[i] = Graphic(canvas, "SMES key border " + i, bounds, KeyBorder);
            pad.Fills[i] = Graphic(canvas, "SMES key fill " + i,
                new(bounds.x + 1, bounds.y + 1, bounds.width - 2, bounds.height - 2), KeyFill, -.1f);
            pad.Keys[i] = Label(canvas, "SMES key " + i, bounds, 19, Ink);
            pad.Keys[i].alignment = TextAlignmentOptions.Center;
            pad.Keys[i].text = KeyNames[i];
        }
    }

    private static Rect ButtonBounds(int control)
    {
        var center = BoxStationModel.SmesButtonCenter((BoxStationSmesControl)control);
        // The outward-facing canvas reverses source X; hit and reach centers stay in the machine frame.
        return new(Width / 2 - (center.x + KeyWidth / 2) / PixelScale,
            Height / 2 - (center.y - CanvasHeight + KeyHeight / 2) / PixelScale,
            KeyWidth / PixelScale, KeyHeight / PixelScale);
    }

    private Pad? FindPad(Camera camera, out int control)
    {
        control = -1;
        var ray = new Ray(camera.transform.position, camera.transform.forward);
        Pad? selected = null;
        float nearest = 2.4f;
        foreach (var pad in _pads)
        {
            if (!pad.Active) continue;
            var frame = _model.SmesControlFrame(pad.Device);
            if (Vector3.Dot(ray.direction, frame.forward) >= -.1f) continue;
            var plane = new Plane(frame.forward, _model.SmesControlPoint(pad.Device, BoxStationSmesControl.InputToggle));
            if (!plane.Raycast(ray, out float distance) || distance < 0 || distance > nearest) continue;
            var point = frame.InverseTransformPoint(ray.GetPoint(distance));
            for (int i = 0; i < KeyNames.Length; i++)
            {
                var center = BoxStationModel.SmesButtonCenter((BoxStationSmesControl)i);
                if (MathF.Abs(point.x - center.x) > KeyWidth / 2 || MathF.Abs(point.y - center.y) > KeyHeight / 2) continue;
                selected = pad;
                nearest = distance;
                control = i;
                break;
            }
        }
        if (selected == null) return null;
        // The solid includes the raised bezel in front of the recessed glass.
        int count = Physics.RaycastNonAlloc(ray, _hits, MathF.Max(0, nearest - .04f), ~0, QueryTriggerInteraction.Ignore);
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
        var device = _devices[pad.Device];
        string identity = "SMES " + (pad.Device + 1).ToString("00", CultureInfo.InvariantCulture);
        double capacity = device.CapacityJoules / 3_600_000d;
        if (state is { } snapshot)
        {
            var value = snapshot.Smes[pad.Device];
            pad.Header.text = identity + "   " + Number(capacity > 0 ? value.ChargeKwh / capacity * 100 : 0, "F0")
                + "%   " + Number(value.ChargeKwh) + " / " + Number(capacity) + " kWh";
            pad.Header.color = Ink;
            pad.Charge.rectTransform.sizeDelta = new(680 * Mathf.Clamp01(capacity > 0 ? (float)(value.ChargeKwh / capacity) : 0), 4);
            pad.ChargeTrack.color = KeyBorder;
            pad.Input.text = "INPUT   " + Number(value.InputKw) + " kW   LIMIT " + Number(value.InputLimitKw) + " kW";
            pad.Output.text = "OUTPUT   " + Number(value.OutputKw) + " kW   LIMIT " + Number(value.OutputLimitKw) + " kW";
            pad.Input.color = value.InputKw > 0 ? Green : Ink;
            pad.Output.color = value.OutputKw > 0 ? Green : Ink;
            pad.Keys[0].text = (value.Enabled & 1) != 0 ? "AUTO" : "OFF";
            pad.Keys[5].text = (value.Enabled & 2) != 0 ? "ON" : "OFF";
            BoxStationPower.SmesConnections(_model.Layout, snapshot.Cables, pad.Device, out bool input, out bool output);
            pad.Status.text = focused >= 0 && feedback.Length > 0 ? feedback
                : "IN " + (input ? "CONNECTED" : "NO FEED")
                    + "   /   OUT " + (output ? "CONNECTED" : "NO FEED");
            pad.Status.color = !input || !output ? Amber : Muted;
            for (int i = 0; i < pad.Keys.Length; i++)
                UpdateKey(pad, i, interactive && ControlEnabled(value, device, i), i == focused);
        }
        else
        {
            pad.Header.text = identity + "   --%   -- / " + Number(capacity) + " kWh";
            pad.Header.color = Muted;
            pad.Charge.rectTransform.sizeDelta = new(0, 4);
            pad.ChargeTrack.color = DisabledBorder;
            pad.Input.text = "INPUT   -- kW   LIMIT -- kW";
            pad.Output.text = "OUTPUT   -- kW   LIMIT -- kW";
            pad.Input.color = pad.Output.color = Muted;
            pad.Keys[0].text = pad.Keys[5].text = "--";
            pad.Status.text = "UNAVAILABLE";
            pad.Status.color = Amber;
            for (int i = 0; i < pad.Keys.Length; i++) UpdateKey(pad, i, false, false);
        }
    }

    private static void UpdateKey(Pad pad, int index, bool enabled, bool focused)
    {
        pad.Keys[index].color = !enabled ? Muted : focused ? Green : Ink;
        pad.Borders[index].color = !enabled ? DisabledBorder : focused ? Green : KeyBorder;
        pad.Fills[index].color = !enabled ? DisabledFill : focused ? HoverFill : KeyFill;
    }

    private static bool ControlEnabled(BoxStationSmesState state, BoxStationSmes device, int control)
    {
        double limit = control < 5 ? state.InputLimitKw : state.OutputLimitKw;
        float maximum = (float)((control < 5 ? device.InputMaximumWatts : device.OutputMaximumWatts) / 1000d);
        return (control % 5) switch { 1 or 3 => limit > 0, 2 or 4 => limit < maximum, _ => true };
    }

    private static string Number(double value, string format = "F1") => value.ToString(format, CultureInfo.InvariantCulture);

    private Image Graphic(Transform parent, string name, Rect bounds, Color color, float depth = 0)
    {
        if (_graphics == null)
        {
            _graphics = new Material(UnityEngine.UI.Graphic.defaultGraphicMaterial) { name = "SMES display graphics" };
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
        // Keep opaque graphics above the glass and behind the text in the outward-facing canvas.
        rect.localPosition += new Vector3(0, 0, depth);
        var image = rect.gameObject.AddComponent<Image>();
        image.material = _graphics;
        image.color = color;
        image.raycastTarget = false;
        return image;
    }

    private TextMeshProUGUI Label(Transform parent, string name, Rect bounds, float size, Color color)
    {
        var rect = new GameObject(name).AddComponent<RectTransform>();
        rect.gameObject.layer = parent.gameObject.layer;
        rect.SetParent(parent, false);
        rect.anchorMin = rect.anchorMax = rect.pivot = new(0, 1);
        rect.anchoredPosition = new(bounds.x, -bounds.y);
        rect.sizeDelta = new(bounds.width, bounds.height);
        var text = rect.gameObject.AddComponent<TextMeshProUGUI>();
        _typography.Apply(text, TabletTextRole.Control);
        var source = text.fontSharedMaterial;
        if (source != null)
        {
            int id = source.GetInstanceID();
            if (!_fonts.TryGetValue(id, out var material))
            {
                material = new Material(source) { name = "SMES display text" };
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
        text.text = "";
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
        _graphics = null;
        _fonts.Clear();
        _hovered = null;
        _typography.Dispose();
    }

    private sealed class Pad(Transform frame)
    {
        internal readonly Transform Frame = frame;
        internal int Device = -1;
        internal bool Active;
        internal TextMeshProUGUI Header = null!, Input = null!, Output = null!, Status = null!;
        internal readonly TextMeshProUGUI[] Keys = new TextMeshProUGUI[10];
        internal Image Charge = null!, ChargeTrack = null!;
        internal readonly Image[] Borders = new Image[10], Fills = new Image[10];
    }
}
