// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Modstoke.Loader;
using Modstoke.Sons;
using TheForest.Utils;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

namespace Modstoke.Client;

internal sealed class BoxStationPowerControls : IDisposable
{
    private const int MaximumPads = 4;
    private const float Width = 500, Height = 800, PixelScale = .001f, ScreenDepth = -.181f, NearbyDistance = 6;
    private static readonly Color Ink = new(.92f, .94f, .95f, 1), Muted = new(.55f, .59f, .62f, 1);
    private static readonly Color Green = new(.38f, .83f, .67f, 1), Amber = new(1, .72f, .28f, 1);
    private static readonly BoxStationPowerChannel[] Channels =
        { BoxStationPowerChannel.Equipment, BoxStationPowerChannel.Lighting, BoxStationPowerChannel.Environment };
    private static readonly string[] ChannelNames = { "EQUIPMENT", "LIGHTING", "ENVIRONMENT" };
    private readonly BoxStationModel _model;
    private readonly Action<int, BoxStationPowerChannel> _command;
    private readonly List<Pad> _pads = new(MaximumPads);
    private readonly Dictionary<(int X, int Z), List<int>> _cells = new();
    private readonly Vector3[] _positions;
    private readonly int[] _nearest = new int[MaximumPads];
    private readonly float[] _distances = new float[MaximumPads];
    private readonly List<Material> _materials = new();
    private readonly Dictionary<int, Material> _fonts = new();
    private readonly ClientTabletTypography _typography = new();
    private readonly Il2CppStructArray<RaycastHit> _hits = new(size: 32);
    private float _nextNearby, _nextReadout;
    private Pad? _hovered;
    private int _hoveredChannel = -1;
    private bool _disposed;

    internal BoxStationPowerControls(BoxStationModel model, Action<int, BoxStationPowerChannel> command)
    {
        _model = model;
        _command = command;
        var apcs = BoxStationPower.GetApcs(model.Layout);
        _positions = new Vector3[apcs.Length];
        for (int i = 0; i < apcs.Length; i++)
        {
            var point = model.Root.transform.InverseTransformPoint(model.ApcControlPoint(i, BoxStationPowerChannel.Lighting));
            _positions[i] = point;
            var key = (Mathf.FloorToInt(point.x / NearbyDistance), Mathf.FloorToInt(point.z / NearbyDistance));
            if (!_cells.TryGetValue(key, out var entries)) _cells.Add(key, entries = new());
            entries.Add(i);
        }
    }

    internal void Update(BoxStationState? state, bool interactive, string feedback)
    {
        if (_disposed || !_model.Visible) return;
        if (state is { } snapshot && (snapshot.LayoutHash != _model.Layout.LayoutHash || snapshot.Power.Length != _positions.Length)) state = null;
        var camera = LocalPlayer.MainCam;
        bool cameraLive = camera is not null && camera && camera.isActiveAndEnabled;
        if (Time.unscaledTime >= _nextNearby && cameraLive)
        {
            _nextNearby = Time.unscaledTime + .2f;
            UpdateNearby(camera!.transform.position);
        }
        // Manual breakers remain operable without electrical supply; actor authority is supplied by the owner.
        bool acceptsInput = interactive && state != null && ClientSpacecraftRider.WorldInputAvailable && !Cursor.visible;
        int channel = -1;
        var hovered = acceptsInput && cameraLive ? FindPad(camera!, out channel) : null;
        if (Time.unscaledTime >= _nextReadout || !ReferenceEquals(hovered, _hovered) || channel != _hoveredChannel)
        {
            _nextReadout = Time.unscaledTime + .2f;
            foreach (var pad in _pads)
                if (pad.Active) UpdateReadout(pad, state, interactive, ReferenceEquals(pad, hovered) ? channel : -1, feedback);
        }
        _hovered = hovered;
        _hoveredChannel = channel;
        if (hovered != null && (Input.GetKeyDown(KeyCode.E) || Input.GetMouseButtonDown(0)))
            _command(hovered.Apc, Channels[channel]);
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
            if (active && pad.Apc != _nearest[i])
            {
                pad.Apc = _nearest[i];
                pad.Frame.SetParent(_model.ApcControlFrame(pad.Apc), false);
                pad.Frame.localPosition = Vector3.zero;
                pad.Frame.localRotation = Quaternion.Euler(0, 180, 0);
                var apc = BoxStationPower.GetApcs(_model.Layout)[pad.Apc];
                string area = _model.Layout.Areas[apc.Area];
                pad.Area.text = area[(area.LastIndexOf('/') + 1)..].Replace('_', ' ').ToUpperInvariant();
                pad.Identity.text = "APC " + (pad.Apc + 1).ToString("000", CultureInfo.InvariantCulture);
            }
            if (pad.Active != active) pad.Frame.gameObject.SetActive(active);
            pad.Active = active;
        }
        _nextReadout = 0;
    }

    private void AddPad()
    {
        var frame = new GameObject("APC controls").transform;
        frame.SetParent(_model.Root.transform, false);
        frame.gameObject.layer = frame.parent.gameObject.layer;
        var pad = new Pad(frame);
        _pads.Add(pad);
        var canvas = new GameObject("APC legends").AddComponent<RectTransform>();
        canvas.SetParent(frame, false);
        canvas.gameObject.layer = frame.gameObject.layer;
        canvas.localPosition = new(0, 0, ScreenDepth);
        canvas.localScale = Vector3.one * PixelScale;
        canvas.sizeDelta = new(Width, Height);
        canvas.gameObject.AddComponent<Canvas>().renderMode = RenderMode.WorldSpace;
        pad.Identity = Label(canvas, "APC identity", "", 40, 64, 420, 28, 23, Ink);
        pad.Area = Label(canvas, "Controlled area", "", 40, 98, 420, 27, 20, Muted);
        pad.Charge = Label(canvas, "Stored charge", "AWAITING HOST", 40, 133, 420, 28, 23, Ink);
        pad.Hint = Label(canvas, "Breaker instruction", "READ ONLY", 40, 173, 420, 24, 17, Muted);
        foreach (var label in new[] { pad.Identity, pad.Area, pad.Charge, pad.Hint })
            label.transform.localPosition += new Vector3(0, 0, 25);
        for (int i = 0; i < Channels.Length; i++)
        {
            var bounds = ButtonBounds(i);
            pad.Names[i] = Label(canvas, "Breaker " + ChannelNames[i], ChannelNames[i], bounds.x + 8, bounds.y + 4, 225, bounds.height - 8, 19, Ink);
            pad.Status[i] = Label(canvas, "Breaker supply", "--", bounds.x + 235, bounds.y + 4, 127, bounds.height - 8, 18, Muted);
            pad.Status[i].alignment = TextAlignmentOptions.MidlineRight;
        }
    }

    private static Rect ButtonBounds(int channel)
    {
        var center = BoxStationModel.ApcButtonCenter(Channels[channel]);
        return new(65, Height / 2 - center.y / PixelScale - 30.5f, 370, 61);
    }

    private Pad? FindPad(Camera camera, out int channel)
    {
        channel = -1;
        var ray = new Ray(camera.transform.position, camera.transform.forward);
        Pad? selected = null;
        float nearest = 2.4f;
        foreach (var pad in _pads)
        {
            if (!pad.Active || Vector3.Dot(ray.direction, pad.Frame.forward) <= .1f) continue;
            var plane = new Plane(pad.Frame.forward, pad.Frame.TransformPoint(new(0, 0, ScreenDepth)));
            if (!plane.Raycast(ray, out float distance) || distance < 0 || distance > nearest) continue;
            var point = pad.Frame.InverseTransformPoint(ray.GetPoint(distance));
            var pixel = new Vector2(point.x / PixelScale + Width / 2, Height / 2 - point.y / PixelScale);
            for (int i = 0; i < Channels.Length; i++)
            {
                if (!ButtonBounds(i).Contains(pixel)) continue;
                selected = pad;
                nearest = distance;
                channel = i;
                break;
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
        if (state is { } snapshot)
        {
            var power = snapshot.Power[pad.Apc];
            double capacity = BoxStationPower.GetApcs(_model.Layout)[pad.Apc].CapacityJoules / 3_600_000d;
            pad.Charge.text = power.ChargeKwh.ToString("F3", CultureInfo.InvariantCulture) + " / "
                + capacity.ToString("F3", CultureInfo.InvariantCulture) + " kWh";
            for (int i = 0; i < Channels.Length; i++)
            {
                byte mask = (byte)Channels[i];
                bool enabled = (power.Enabled & mask) != 0, available = (power.Available & mask) != 0;
                pad.Status[i].text = !enabled ? "OFF" : available ? "ON" : "NO FEED";
                pad.Status[i].color = !enabled ? Muted : available ? Green : Amber;
            }
        }
        else
        {
            pad.Charge.text = "AWAITING HOST";
            foreach (var status in pad.Status) { status.text = "--"; status.color = Muted; }
        }
        for (int i = 0; i < Channels.Length; i++) pad.Names[i].color = i == focused ? Green : Ink;
        pad.Hint.text = focused >= 0 && feedback.Length > 0 ? feedback : !interactive ? "READ ONLY"
            : state == null ? "AWAITING HOST" : focused >= 0 ? "E / CLICK: TOGGLE BREAKER" : "AIM AT A BREAKER";
        pad.Hint.color = focused >= 0 ? Green : Muted;
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
                material = new Material(source) { name = "APC control text" };
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
        _hovered = null;
        _typography.Dispose();
    }

    private sealed class Pad(Transform frame)
    {
        internal readonly Transform Frame = frame;
        internal int Apc = -1;
        internal bool Active;
        internal TextMeshProUGUI Identity = null!, Area = null!, Charge = null!, Hint = null!;
        internal readonly TextMeshProUGUI[] Names = new TextMeshProUGUI[3], Status = new TextMeshProUGUI[3];
    }
}
