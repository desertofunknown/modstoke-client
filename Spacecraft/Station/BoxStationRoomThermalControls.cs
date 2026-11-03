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

namespace Modstoke.Client;

internal sealed class BoxStationRoomThermalControls : IDisposable
{
    private const int MaximumPads = 3;
    private const float Width = 1120, Height = 1900, PixelScale = .0005f, CanvasHeight = .55f;
    private const float KeyDepth = -.281f, NearbyDistance = 5;
    private static readonly Color Ink = new(.90f, .94f, .92f, 1), Muted = new(.46f, .53f, .52f, 1);
    private static readonly Color Green = new(.38f, .84f, .67f, 1), Amber = new(1, .72f, .28f, 1);
    private static readonly string[] KeyNames = { "POWER", "MODE", "-1 C", "+1 C", "HATCH", "CELL", "CHARGE", "REJECT" };
    private readonly BoxStationModel _model;
    private readonly Action<int, BoxStationRoomThermalControl> _command;
    private readonly ImmutableArray<BoxStationRoomThermalDevice> _devices;
    private readonly List<Pad> _pads = new(MaximumPads);
    private readonly Vector3[] _positions;
    private readonly int[] _nearest = new int[MaximumPads];
    private readonly float[] _distances = new float[MaximumPads];
    private readonly List<Material> _materials = new();
    private readonly Dictionary<int, Material> _fonts = new();
    private readonly ClientTabletTypography _typography = new();
    private readonly Il2CppStructArray<RaycastHit> _hits = new(size: 32);
    private float _nextNearby, _nextReadout;
    private int _hoveredControl = -1;
    private Pad? _hovered;
    private bool _disposed;

    internal BoxStationRoomThermalControls(BoxStationModel model, Action<int, BoxStationRoomThermalControl> command)
    {
        _model = model;
        _command = command;
        _devices = BoxStationRoomThermal.GetDevices(model.Layout);
        _positions = new Vector3[_devices.Length];
        for (int i = 0; i < _positions.Length; i++)
            _positions[i] = model.RoomThermalControlFrame(i).localPosition;
    }

    internal void Update(BoxStationState? state, bool interactive, string feedback)
    {
        if (_disposed) return;
        if (state is { } snapshot && (snapshot.LayoutHash != _model.Layout.LayoutHash
            || snapshot.RoomThermal.Length != _devices.Length)) state = null;
        _model.UpdateRoomThermal(state);
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
        if (hovered != null && DisabledReason(state!.Value.RoomThermal[hovered.Device], (BoxStationRoomThermalControl)control).Length == 0
            && (Input.GetKeyDown(KeyCode.E) || Input.GetMouseButtonDown(0)))
        {
            _command(hovered.Device, (BoxStationRoomThermalControl)control);
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
                pad.Frame.SetParent(_model.RoomThermalControlFrame(pad.Device), false);
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
        var frame = new GameObject("Room conditioner controls").transform;
        frame.SetParent(_model.Root.transform, false);
        frame.gameObject.layer = frame.parent.gameObject.layer;
        var pad = new Pad(frame);
        _pads.Add(pad);
        var canvas = new GameObject("Room conditioner fitted display").AddComponent<RectTransform>();
        canvas.SetParent(frame, false);
        canvas.gameObject.layer = frame.gameObject.layer;
        canvas.localPosition = new(0, CanvasHeight, KeyDepth);
        canvas.localScale = Vector3.one * PixelScale;
        canvas.sizeDelta = new(Width, Height);
        canvas.gameObject.AddComponent<Canvas>().renderMode = RenderMode.WorldSpace;
        for (int row = 0; row < pad.Readouts.Length; row++)
        {
            var text = Label(canvas, "Telemetry " + row, new(88, 122 + row * 48, 944, 36), row == 0 ? 25 : 24, Ink);
            // Glass lies 5 mm behind the raised key faces.
            text.transform.localPosition += new Vector3(0, 0, 10);
            pad.Readouts[row] = text;
        }
        for (int button = 0; button < KeyNames.Length; button++)
        {
            var bounds = ButtonBounds(button);
            bounds.x += 6; bounds.width -= 12;
            pad.Keys[button] = Label(canvas, "Control " + KeyNames[button], bounds, 23, Ink);
            pad.Keys[button].alignment = TextAlignmentOptions.Center;
            pad.Keys[button].text = KeyNames[button];
        }
    }

    private static Rect ButtonBounds(int button)
    {
        var center = BoxStationModel.RoomThermalButtonCenter((BoxStationRoomThermalControl)button);
        return new(Width / 2 - center.x / PixelScale - 112,
            Height / 2 - (center.y - CanvasHeight) / PixelScale - 52, 224, 104);
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
            var plane = new Plane(pad.Frame.forward, pad.Frame.TransformPoint(new(0, 0, KeyDepth)));
            if (!plane.Raycast(ray, out float distance) || distance < 0 || distance > nearest) continue;
            var point = pad.Frame.InverseTransformPoint(ray.GetPoint(distance));
            var pixel = new Vector2(point.x / PixelScale + Width / 2, Height / 2 - (point.y - CanvasHeight) / PixelScale);
            for (int i = 0; i < KeyNames.Length; i++)
            {
                if (!ButtonBounds(i).Contains(pixel)) continue;
                selected = pad;
                nearest = distance;
                control = i;
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
        var rows = pad.Readouts;
        rows[0].text = "ROOM CONDITIONER " + (pad.Device + 1).ToString("00", CultureInfo.InvariantCulture);
        if (state is { } snapshot)
        {
            var value = snapshot.RoomThermal[pad.Device];
            string operation = value.CellJoules <= 0 ? "CELL EMPTY" : value.SinkLimited ? "COOLING LIMIT / CORE HOT"
                : !value.Enabled ? "OUTPUT OFF" : value.ElectricWatts <= 0 ? "IDLE" : "CONDITIONING";
            rows[1].text = value.Mode.ToString().ToUpperInvariant() + " / " + operation;
            rows[1].color = value.SinkLimited || value.CellJoules <= 0 ? Amber : Ink;
            rows[2].text = (value.RoomKelvin <= 0 ? "ROOM: NO AIR" : "ROOM " + Celsius(value.RoomKelvin) + " C")
                + "   TARGET " + Celsius(value.TargetKelvin) + " C";
            rows[3].text = "CORE " + Celsius(value.CoreKelvin) + " C   CELL "
                + Number(value.CellJoules / BoxStationRoomThermal.CellCapacityJoules * 100, "F0") + "%";
            bool megawatts = MathF.Abs(value.ThermalWatts) >= 1_000_000;
            rows[4].text = "NET ROOM " + Number(value.ThermalWatts / (megawatts ? 1_000_000 : 1000), "+0.0;-0.0;0.0")
                + (megawatts ? " MW / DRAW " : " kW / DRAW ") + Number(value.ElectricWatts / 1000) + " kW";
            string charge = value.Charging && value.ChargeWatts <= 0
                ? value.CellJoules >= BoxStationRoomThermal.CellCapacityJoules ? "CELL FULL" : "CHARGE WAIT / NO GRID FLOW"
                : "GRID CHARGE " + Number(value.ChargeWatts / 1000, "F2") + " kW";
            rows[5].text = (value.CellDocked ? "CRADLE / " : "INSTALLED / ") + charge;
            rows[5].color = value.Charging && value.ChargeWatts <= 0 ? Amber : value.ChargeWatts > 0 ? Green : Ink;
            rows[6].text = "HATCH " + (value.HatchOpen ? "OPEN" : "SHUT") + " / REJECT "
                + (value.RejectHeat ? "TO THIS ROOM" : "CLOSED");
            rows[6].color = value.RejectHeat ? Amber : Muted;
            string reason = focused >= 0 ? DisabledReason(value, (BoxStationRoomThermalControl)focused) : "";
            rows[7].text = !interactive ? "READ ONLY" : reason.Length > 0 ? reason
                : focused >= 0 && feedback.Length > 0 ? feedback
                : focused >= 0 ? ActionHint(value, (BoxStationRoomThermalControl)focused) : "AIM AT KEY / E OR CLICK";
            rows[7].color = reason.Length > 0 ? Amber : Muted;
            for (int i = 0; i < KeyNames.Length; i++)
            {
                bool enabled = interactive && DisabledReason(value, (BoxStationRoomThermalControl)i).Length == 0;
                pad.Keys[i].color = !enabled ? Muted : i == focused ? Green : Ink;
            }
        }
        else
        {
            rows[1].text = "HOST DATA UNAVAILABLE";
            rows[1].color = Amber;
            rows[2].text = "ROOM -- C   TARGET -- C";
            rows[3].text = "CORE -- C   CELL --%";
            rows[4].text = "NET ROOM -- kW / DRAW -- kW";
            rows[5].text = "CELL / GRID CHARGE UNAVAILABLE";
            rows[5].color = Muted;
            rows[6].text = "HATCH / REJECTION STATE UNKNOWN";
            rows[6].color = Muted;
            rows[7].text = "CONTROLS LOCKED / WAIT FOR HOST";
            rows[7].color = Amber;
            foreach (var key in pad.Keys) key.color = Muted;
        }
    }

    private static string DisabledReason(BoxStationRoomThermalState state, BoxStationRoomThermalControl control)
    {
        switch (control)
        {
            case BoxStationRoomThermalControl.Power:
                if (state.Enabled) return "";
                if (state.HatchOpen) return "CLOSE SERVICE HATCH TO RUN";
                if (state.CellDocked) return "INSTALL CELL TO RUN";
                if (state.Charging) return "STOP CHARGING TO RUN";
                if (state.RejectHeat) return "CLOSE HEAT REJECTION PANEL";
                return state.CellJoules <= 0 ? "CELL EMPTY / CHARGE IN CRADLE" : "";
            case BoxStationRoomThermalControl.Mode:
            case BoxStationRoomThermalControl.TargetDown:
            case BoxStationRoomThermalControl.TargetUp:
                if (!state.HatchOpen) return "OPEN HATCH TO CHANGE SETTINGS";
                if (control == BoxStationRoomThermalControl.TargetDown && state.TargetKelvin <= BoxStationRoomThermal.MinimumTargetKelvin)
                    return "MINIMUM TARGET 0 C";
                if (control == BoxStationRoomThermalControl.TargetUp && state.TargetKelvin >= BoxStationRoomThermal.MaximumTargetKelvin)
                    return "MAXIMUM TARGET 60 C";
                return "";
            case BoxStationRoomThermalControl.Cell:
                return !state.HatchOpen ? "OPEN HATCH TO MOVE CELL" : state.Charging ? "STOP CHARGING TO MOVE CELL" : "";
            case BoxStationRoomThermalControl.Charge:
                return !state.HatchOpen ? "OPEN HATCH TO CHARGE" : !state.CellDocked ? "MOVE CELL TO SERVICE CRADLE"
                    : state.Enabled ? "STOP OUTPUT TO CHARGE" : "";
            default:
                return "";
        }
    }

    private static string ActionHint(BoxStationRoomThermalState state, BoxStationRoomThermalControl control) => control switch
    {
        BoxStationRoomThermalControl.Power => state.Enabled ? "E / CLICK: STOP OUTPUT" : "E / CLICK: ENABLE CONDITIONING",
        BoxStationRoomThermalControl.Mode => "E / CLICK: CYCLE AUTO / HEAT / COOL",
        BoxStationRoomThermalControl.TargetDown => "E / CLICK: LOWER TARGET 1 C",
        BoxStationRoomThermalControl.TargetUp => "E / CLICK: RAISE TARGET 1 C",
        BoxStationRoomThermalControl.Hatch => state.HatchOpen ? "E / CLICK: SHUT HATCH / STOP CHARGE" : "E / CLICK: OPEN HATCH / STOP OUTPUT",
        BoxStationRoomThermalControl.Cell => state.CellDocked ? "E / CLICK: INSTALL THE CELL" : "E / CLICK: MOVE CELL TO CRADLE",
        BoxStationRoomThermalControl.Charge => state.Charging ? "E / CLICK: STOP CHARGING" : "E / CLICK: CHARGE FROM AREA GRID",
        BoxStationRoomThermalControl.RejectHeat => state.RejectHeat ? "E / CLICK: CLOSE REJECTION PANEL" : "E / CLICK: RELEASE HEAT TO THIS ROOM",
        _ => ""
    };

    private static string Number(double value, string format = "F1")
        => value.ToString(Math.Abs(value) >= 10_000 ? format[0] == '+' ? "+0.0E+0;-0.0E+0;0.0" : "0.0E+0" : format, CultureInfo.InvariantCulture);
    private static string Celsius(float kelvin) => Number(kelvin - 273.15f);

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
                material = new Material(source) { name = "Room conditioner display text" };
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
        _fonts.Clear();
        _hovered = null;
        _typography.Dispose();
    }

    private sealed class Pad(Transform frame)
    {
        internal readonly Transform Frame = frame;
        internal int Device = -1;
        internal bool Active;
        internal readonly TextMeshProUGUI[] Readouts = new TextMeshProUGUI[8], Keys = new TextMeshProUGUI[8];
    }
}
