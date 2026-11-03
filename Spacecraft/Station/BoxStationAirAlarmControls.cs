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

internal sealed class BoxStationAirAlarmControls : IDisposable
{
    private const int MaximumPads = 4;
    private const ushort TritiumAndBzReadings = 128 | 256;
    private const float Width = 920, Height = 1200, PixelScale = .001f, ScreenDepth = -.205f, NearbyDistance = 6;
    private static readonly Color Ink = new(.92f, .94f, .95f, 1), Muted = new(.55f, .59f, .62f, 1);
    private static readonly Color Green = new(.38f, .83f, .67f, 1), Amber = new(1, .72f, .28f, 1), Red = new(1, .36f, .29f, 1);
    private static readonly BoxStationAirMode[] Modes =
    {
        BoxStationAirMode.Filtering, BoxStationAirMode.Draught, BoxStationAirMode.Panic, BoxStationAirMode.Cycle,
        BoxStationAirMode.Off, BoxStationAirMode.Siphon, BoxStationAirMode.Contaminated, BoxStationAirMode.Refill
    };
    private static readonly string[] ModeNames = { "FILTERING", "DRAUGHT", "PANIC", "CYCLE", "OFF", "SIPHON", "CONTAMINATED", "REFILL" };
    private static readonly string[] SensorNames = { "PRESS", "TEMP", "O2", "N2", "CO2", "PLASMA", "N2O", "TRITIUM", "BZ" };
    private readonly BoxStationModel _model;
    private readonly Action<int, BoxStationAirMode> _command;
    private readonly ImmutableArray<BoxStationAirAlarmDevice> _devices;
    private readonly int[][] _receivers;
    private readonly List<Pad> _pads = new(MaximumPads);
    private readonly Dictionary<(int X, int Z), List<int>> _cells = new();
    private readonly Vector3[] _positions;
    private readonly int[] _nearest = new int[MaximumPads];
    private readonly float[] _distances = new float[MaximumPads];
    private readonly List<Material> _materials = new();
    private readonly Dictionary<int, Material> _fonts = new();
    private readonly ClientTabletTypography _typography = new();
    private readonly Il2CppStructArray<RaycastHit> _hits = new(size: 32);
    private float _nextNearby, _nextReadout, _confirmUntil;
    private int _confirmAlarm = -1, _confirmMode = -1, _hoveredMode = -1;
    private Pad? _hovered;
    private Material? _graphics;
    private bool _disposed;

    internal BoxStationAirAlarmControls(BoxStationModel model, Action<int, BoxStationAirMode> command)
    {
        _model = model;
        _command = command;
        _devices = BoxStationAirAlarms.GetDevices(model.Layout);
        _positions = new Vector3[_devices.Length];
        var receivers = new List<int>[model.Layout.Areas.Length];
        var plumbing = BoxStationPlumbing.GetDevices(model.Layout);
        for (int i = 0; i < plumbing.Length; i++)
        {
            var device = plumbing[i];
            if (device.Kind is not (BoxStationPlumbingKind.Vent or BoxStationPlumbingKind.Scrubber)) continue;
            (receivers[device.Area] ??= new()).Add(i);
        }
        _receivers = new int[receivers.Length][];
        for (int i = 0; i < receivers.Length; i++) _receivers[i] = receivers[i]?.ToArray() ?? Array.Empty<int>();
        for (int i = 0; i < _devices.Length; i++)
        {
            var point = model.Root.transform.InverseTransformPoint(model.AirAlarmControlPoint(i));
            _positions[i] = point;
            var key = (Mathf.FloorToInt(point.x / NearbyDistance), Mathf.FloorToInt(point.z / NearbyDistance));
            if (!_cells.TryGetValue(key, out var entries)) _cells.Add(key, entries = new());
            entries.Add(i);
        }
    }

    internal void Update(BoxStationState? state, bool interactive, string feedback)
    {
        if (_disposed) return;
        if (!_model.Visible) { CancelConfirmation(); return; }
        if (state is { } snapshot && (snapshot.LayoutHash != _model.Layout.LayoutHash
            || snapshot.Alarms.Length != _devices.Length || snapshot.Zones.Length != _model.Layout.Compartments.Length
            || snapshot.Plumbing.Length != BoxStationPlumbing.GetDevices(_model.Layout).Length)) state = null;
        var camera = LocalPlayer.MainCam;
        bool cameraLive = camera is not null && camera && camera.isActiveAndEnabled;
        if (Time.unscaledTime >= _nextNearby && cameraLive)
        {
            _nextNearby = Time.unscaledTime + .2f;
            UpdateNearby(camera!.transform.position);
        }
        bool acceptsInput = interactive && state != null && ClientSpacecraftRider.WorldInputAvailable && !Cursor.visible;
        int mode = -1;
        var hovered = acceptsInput && cameraLive ? FindPad(camera!, out mode) : null;
        if (hovered != null && !state!.Value.Alarms[hovered.Alarm].Powered) hovered = null;
        if (_confirmAlarm >= 0 && (hovered == null || hovered.Alarm != _confirmAlarm
            || mode != _confirmMode || Time.unscaledTime >= _confirmUntil)) CancelConfirmation();
        if (hovered != null && (Input.GetKeyDown(KeyCode.E) || Input.GetMouseButtonDown(0)))
        {
            bool vacuum = Modes[mode] is BoxStationAirMode.Panic or BoxStationAirMode.Cycle or BoxStationAirMode.Siphon;
            if (vacuum && _confirmAlarm < 0)
            {
                _confirmAlarm = hovered.Alarm;
                _confirmMode = mode;
                _confirmUntil = Time.unscaledTime + 4;
            }
            else
            {
                CancelConfirmation();
                _command(hovered.Alarm, Modes[mode]);
            }
            _nextReadout = 0;
        }
        if (Time.unscaledTime >= _nextReadout || !ReferenceEquals(hovered, _hovered) || mode != _hoveredMode)
        {
            _nextReadout = Time.unscaledTime + .2f;
            foreach (var pad in _pads)
                if (pad.Active) UpdateReadout(pad, state, interactive, ReferenceEquals(pad, hovered) ? mode : -1, feedback);
        }
        _hovered = hovered;
        _hoveredMode = mode;
    }

    private void CancelConfirmation()
    {
        if (_confirmAlarm >= 0) _nextReadout = 0;
        _confirmAlarm = _confirmMode = -1;
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
                pad.Frame.SetParent(_model.AirAlarmControlFrame(pad.Alarm), false);
                pad.Frame.localPosition = Vector3.zero;
                pad.Frame.localRotation = Quaternion.Euler(0, 180, 0);
                var device = _devices[pad.Alarm];
                string area = _model.Layout.Areas[device.Area];
                pad.Area.text = area[(area.LastIndexOf('/') + 1)..].Replace('_', ' ').ToUpperInvariant();
                pad.Identity.text = "AIR ALARM " + (pad.Alarm + 1).ToString("000", CultureInfo.InvariantCulture);
                pad.Scope.text = "SAMPLE: ZONE " + device.Zone + "  /  COMMAND: AREA " + device.Area;
            }
            if (pad.Active != active) pad.Frame.gameObject.SetActive(active);
            pad.Active = active;
        }
        _nextReadout = 0;
    }

    private void AddPad()
    {
        var frame = new GameObject("Air alarm controls").transform;
        frame.SetParent(_model.Root.transform, false);
        frame.gameObject.layer = frame.parent.gameObject.layer;
        var pad = new Pad(frame);
        _pads.Add(pad);
        var canvas = new GameObject("Air alarm fitted display").AddComponent<RectTransform>();
        canvas.SetParent(frame, false);
        canvas.gameObject.layer = frame.gameObject.layer;
        canvas.localPosition = new(0, 0, ScreenDepth);
        canvas.localScale = Vector3.one * PixelScale;
        canvas.sizeDelta = new(Width, Height);
        canvas.gameObject.AddComponent<Canvas>().renderMode = RenderMode.WorldSpace;
        Graphic(canvas, "Primary telemetry band", new(70, 189, 780, 145), new(.025f, .05f, .06f, 1));
        Graphic(canvas, "Alarm status band", new(70, 340, 780, 76), new(.035f, .065f, .075f, 1));
        Graphic(canvas, "Control feedback band", new(70, 540, 780, 76), new(.025f, .05f, .06f, 1));
        foreach (float y in new[] { 185f, 337f, 419f, 534f })
            Graphic(canvas, "Display section rule", new(75, y, 770, 1.5f), new(.15f, .23f, .25f, 1));
        pad.StatusBar = Graphic(canvas, "Alarm severity strip", new(75, 346, 4, 62), Green);
        pad.StatusBar.transform.localPosition += new Vector3(0, 0, -.1f);
        pad.StatusLens = Graphic(canvas, "Alarm status lens", new(767.5f, 38, 95, 14), Green);
        pad.Identity = Readout(canvas, "Identity", 96, 26, Ink);
        pad.Area = Readout(canvas, "Department", 126, 22, Muted);
        pad.Scope = Readout(canvas, "Sampling and command scope", 155, 20, Muted);
        pad.Pressure = Readout(canvas, "Pressure and temperature", 190, 27, Ink);
        pad.Oxygen = Readout(canvas, "Oxygen nitrogen and carbon dioxide", 219, 23, Ink);
        pad.Trace = Readout(canvas, "Plasma nitrous oxide and water vapor", 247, 22, Muted);
        pad.Hazards = Readout(canvas, "Tritium and BZ", 275, 23, Ink);
        pad.Combustion = Readout(canvas, "Actual combustion power density", 303, 22, Muted);
        pad.Severity = Readout(canvas, "Local and department severity", 347, 23, Green);
        pad.Sensors = Readout(canvas, "Threshold warning summary", 383, 20, Amber);
        foreach (var label in new[] { pad.Severity, pad.Sensors })
        {
            label.rectTransform.anchoredPosition += new Vector2(15, 0);
            label.rectTransform.sizeDelta -= new Vector2(15, 0);
        }
        pad.SelectedMode = Readout(canvas, "Selected mode", 423, 24, Ink);
        pad.Receivers = Readout(canvas, "Department receivers", 460, 20, Muted);
        pad.Hint = Readout(canvas, "Interaction instruction", 497, 23, Ink);
        pad.Feedback = Readout(canvas, "Command feedback", 534, 20, Muted);
        for (int i = 0; i < Modes.Length; i++)
        {
            var bounds = ButtonBounds(i);
            pad.Names[i] = Label(canvas, "Mode " + ModeNames[i], ModeNames[i], bounds.x + 12, bounds.y + 8, 265, bounds.height - 16, 21, Ink);
            pad.Status[i] = Label(canvas, "Mode selection", "", bounds.x + 278, bounds.y + 8, 68, bounds.height - 16, 17, Green);
            pad.Status[i].alignment = TextAlignmentOptions.MidlineRight;
        }
    }

    private Image Graphic(Transform parent, string name, Rect bounds, Color color)
    {
        if (_graphics == null)
        {
            _graphics = new Material(UnityEngine.UI.Graphic.defaultGraphicMaterial) { name = "Air alarm display graphics" };
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
        rect.localPosition += new Vector3(0, 0, 14.3f);
        var image = rect.gameObject.AddComponent<Image>();
        image.material = _graphics;
        image.color = color;
        image.raycastTarget = false;
        return image;
    }

    private TextMeshProUGUI Readout(Transform parent, string name, float y, float size, Color color)
    {
        var text = Label(parent, name, "", 75, y, 770, 30, size, color);
        text.transform.localPosition += new Vector3(0, 0, 14);
        return text;
    }

    private static Rect ButtonBounds(int button)
    {
        var center = BoxStationModel.AirAlarmButtonCenter(button);
        return new(Width / 2 - center.x / PixelScale - 180, Height / 2 - center.y / PixelScale - 40, 360, 80);
    }

    private Pad? FindPad(Camera camera, out int mode)
    {
        mode = -1;
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
            for (int i = 0; i < Modes.Length; i++)
            {
                if (!ButtonBounds(i).Contains(pixel)) continue;
                selected = pad;
                nearest = distance;
                mode = i;
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
        var device = _devices[pad.Alarm];
        bool powered = state is { } live && live.Alarms[pad.Alarm].Powered;
        int selected = state is { } current ? Array.IndexOf(Modes, current.Alarms[pad.Alarm].Mode) : -1;
        if (powered && state is { } snapshot
            && BoxStationSystems.TryGetAtmosphere(_model.Layout, snapshot, device.Cell, out var gas))
        {
            var alarm = snapshot.Alarms[pad.Alarm];
            pad.Pressure.text = "PRESS " + Number(gas.Pressure) + " kPa     TEMP " + Number(gas.TemperatureC) + " C";
            double nitrogen = gas.Pressure <= 0 ? 0 : Math.Max(0, 1 - gas.OxygenFraction - gas.CO2Fraction - gas.PlasmaFraction
                - gas.N2OFraction - gas.WaterVaporFraction - gas.TritiumFraction - gas.BZFraction);
            pad.Oxygen.text = "O2 " + Number(gas.OxygenFraction * 100) + "%   N2 " + Number(nitrogen * 100)
                + "%   CO2 " + Number(gas.CO2Fraction * 100, "F3") + "%";
            pad.Trace.text = "PLASMA " + Number(gas.PlasmaFraction * 100, "F3") + "%   N2O " + Number(gas.N2OFraction * 100, "F3")
                + "%   VAPOR " + Number(gas.WaterVaporFraction * 100, "F3") + "%";
            pad.Hazards.text = "TRITIUM " + Number(gas.TritiumFraction * 100, "F3") + "%   BZ " + Number(gas.BZFraction * 100, "F3") + "%";
            pad.Hazards.color = (alarm.DangerMask & TritiumAndBzReadings) != 0 ? Red
                : (alarm.WarningMask & TritiumAndBzReadings) != 0 ? Amber : Ink;
            float combustion = gas.CombustionWattsPerCubicMeter;
            pad.Combustion.text = "LAST COMBUSTION " + Number(combustion >= 1_000_000 ? combustion / 1_000_000
                : combustion >= 1000 ? combustion / 1000 : combustion, combustion > 0 && combustion < .1f ? "0.0E+0" : "F1")
                + (combustion >= 1_000_000 ? " MW/m3" : combustion >= 1000 ? " kW/m3" : " W/m3");
            pad.Combustion.color = combustion > 0 ? Red : Muted;
            pad.Severity.text = device.ThresholdsDisabled ? "MONITOR ONLY / LIMITS DISABLED"
                : "ALARM LIMITS: LOCAL " + Severity(alarm.Danger) + " / AREA " + Severity(alarm.AreaDanger);
            pad.Severity.color = alarm.Danger >= 2 || alarm.AreaDanger >= 2 ? Red
                : device.ThresholdsDisabled || alarm.Danger > 0 || alarm.AreaDanger > 0 ? Amber : Green;
            pad.Sensors.text = device.ThresholdsDisabled ? "Department: " + Severity(alarm.AreaDanger) + " / no local limits"
                : alarm.DangerMask != 0 ? "DANGER: " + Sensors(alarm.DangerMask)
                : alarm.WarningMask != 0 ? "WARNING: " + Sensors(alarm.WarningMask) : "LOCAL THRESHOLDS: NO WARNINGS";
            pad.Sensors.color = alarm.DangerMask != 0 ? Red : Amber;
            int enabled = 0, unavailable = 0;
            foreach (int receiver in _receivers[device.Area])
            {
                var status = snapshot.Plumbing[receiver];
                if (status.Enabled) enabled++;
                if (!status.Powered || !status.Connected) unavailable++;
            }
            pad.Receivers.text = "AREA DEVICES " + _receivers[device.Area].Length + " / ENABLED " + enabled + " / UNAVAILABLE " + unavailable;
        }
        else
        {
            pad.Pressure.text = "PRESS -- kPa     TEMP -- C";
            pad.Oxygen.text = "O2 --%   N2 --%   CO2 --%";
            pad.Trace.text = "PLASMA --%   N2O --%   VAPOR --%";
            pad.Hazards.text = "TRITIUM --%   BZ --%";
            pad.Hazards.color = Muted;
            pad.Combustion.text = "LAST COMBUSTION -- W/m3";
            pad.Combustion.color = Muted;
            pad.Severity.text = state == null ? "TELEMETRY UNAVAILABLE" : "NO POWER / CONTROLLER UNAVAILABLE";
            pad.Severity.color = Amber;
            pad.Sensors.text = "LOCAL TELEMETRY UNAVAILABLE";
            pad.Sensors.color = Muted;
            pad.Receivers.text = "AREA DEVICES " + _receivers[device.Area].Length + " / STATUS UNAVAILABLE";
        }
        pad.SelectedMode.text = "SELECTED MODE: " + (selected >= 0 ? ModeNames[selected] : "--");
        pad.StatusBar.color = pad.Severity.color;
        pad.StatusLens.color = powered ? pad.Severity.color : new(.015f, .02f, .02f, 1);
        bool confirming = _confirmAlarm == pad.Alarm;
        bool vacuum = focused >= 0 && Modes[focused] is BoxStationAirMode.Panic or BoxStationAirMode.Cycle or BoxStationAirMode.Siphon;
        pad.Hint.text = confirming ? "DRAINS ROOM AIR: PRESS AGAIN TO CONFIRM"
            : !powered ? "MODE CONTROL UNAVAILABLE" : !interactive ? "READ ONLY"
            : vacuum ? "VACUUM HAZARD / E OR CLICK TO ARM"
            : focused >= 0 ? "E / CLICK: SEND AREA COMMAND" : "AIM AT A MODE KEY";
        pad.Hint.color = confirming || vacuum ? Red : Muted;
        pad.Feedback.text = confirming ? "Same key within 4 seconds; look away to cancel"
            : focused >= 0 && feedback.Length > 0 ? feedback : "";
        for (int i = 0; i < Modes.Length; i++)
        {
            pad.Names[i].color = !powered ? Muted : i == focused ? vacuum ? Red : Green : Ink;
            pad.Status[i].text = i == selected ? "SET" : "";
            pad.Status[i].color = powered ? Green : Muted;
        }
    }

    private static string Number(double value, string format = "F1")
        => value.ToString(Math.Abs(value) >= 10_000 ? "0.0E+0" : format, CultureInfo.InvariantCulture);
    private static string Severity(byte danger) => danger >= 2 ? "DANGER" : danger == 1 ? "WARNING" : "NORMAL";

    private static string Sensors(ushort mask)
    {
        string result = "";
        for (int i = 0; i < SensorNames.Length; i++)
            if ((mask & (1 << i)) != 0) result += (result.Length == 0 ? "" : " ") + SensorNames[i];
        return result;
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
                material = new Material(source) { name = "Air alarm control text" };
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
        internal TextMeshProUGUI Identity = null!, Area = null!, Scope = null!, Pressure = null!, Oxygen = null!, Trace = null!, Hazards = null!, Combustion = null!,
            Severity = null!, Sensors = null!, SelectedMode = null!, Receivers = null!, Hint = null!, Feedback = null!;
        internal Image StatusBar = null!, StatusLens = null!;
        internal readonly TextMeshProUGUI[] Names = new TextMeshProUGUI[8], Status = new TextMeshProUGUI[8];
    }
}
