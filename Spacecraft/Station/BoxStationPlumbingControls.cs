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

namespace Modstoke.Client;

internal sealed partial class BoxStationPlumbingControls : IDisposable
{
    private const int MaximumPads = 4;
    private const float PixelScale = .001f, NearbyDistance = 6;
    private static readonly Color Ink = new(.92f, .94f, .95f, 1), Muted = new(.55f, .59f, .62f, 1);
    private static readonly Color Green = new(.38f, .83f, .67f, 1), Amber = new(1, .72f, .28f, 1);
    private static readonly byte[][] FilterSpecies = [[0, 1, 2, 4, 8], [16, 32, 64, 128, 0]];
    private static readonly string[][] FilterNames =
        [["BYPASS", "OXYGEN", "NITROGEN", "CO2", "PLASMA", "MORE"], ["N2O", "VAPOR", "TRITIUM", "BZ", "BYPASS", "BACK"]];
    private static readonly string[] MixerNames = ["A -1%", "A +1%", "-10 kPa", "+10 kPa", "ATM", "MAX"];
    private readonly BoxStationModel _model;
    private readonly Action<string> _command;
    private readonly ImmutableArray<BoxStationPlumbingDevice> _devices;
    private readonly int[] _radiatorsByDevice;
    private readonly Vector3[] _positions;
    private readonly List<Pad> _pads = new(MaximumPads);
    private readonly Dictionary<(int X, int Z), List<int>> _cells = new();
    private readonly int[] _nearest = new int[MaximumPads];
    private readonly float[] _distances = new float[MaximumPads];
    private readonly List<Material> _materials = new();
    private readonly Dictionary<int, Material> _fonts = new();
    private readonly ClientTabletTypography _typography = new();
    private readonly Il2CppStructArray<RaycastHit> _hits = new(size: 32);
    private float _nextNearby, _nextReadout;
    private Pad? _hovered;
    private int _hoveredButton = -1;
    private bool _disposed;

    internal BoxStationPlumbingControls(BoxStationModel model, Action<string> command)
    {
        _model = model;
        _command = command;
        _devices = BoxStationPlumbing.GetDevices(model.Layout);
        _radiatorsByDevice = new int[_devices.Length];
        _positions = new Vector3[_devices.Length];
        for (int i = 0; i < _devices.Length; i++)
        {
            _radiatorsByDevice[i] = _devices[i].Kind == BoxStationPlumbingKind.Pump
                ? BoxStationPlumbing.GetRadiatorForDevice(model.Layout, i) : -1;
            var point = model.Root.transform.InverseTransformPoint(model.PlumbingControlPoint(i));
            _positions[i] = point;
            var key = (Mathf.FloorToInt(point.x / NearbyDistance), Mathf.FloorToInt(point.z / NearbyDistance));
            if (!_cells.TryGetValue(key, out var entries)) _cells.Add(key, entries = new());
            entries.Add(i);
        }
    }

    internal void Update(BoxStationState? state, bool interactive, string feedback)
    {
        if (_disposed || !_model.Visible) return;
        if (state is { } snapshot && (snapshot.LayoutHash != _model.Layout.LayoutHash || snapshot.Plumbing.Length != _devices.Length)) state = null;
        _model.UpdateInjectors(state);
        var camera = LocalPlayer.MainCam;
        bool cameraLive = camera is not null && camera && camera.isActiveAndEnabled;
        if (Time.unscaledTime >= _nextNearby && cameraLive)
        {
            _nextNearby = Time.unscaledTime + .2f;
            UpdateNearby(camera!.transform.position);
        }
        bool acceptsInput = interactive && state != null && ClientSpacecraftRider.WorldInputAvailable && !Cursor.visible;
        var hovered = acceptsInput && cameraLive ? FindPad(camera!) : null;
        if (Time.unscaledTime >= _nextReadout || !ReferenceEquals(hovered, _hovered) || (hovered?.SelectedButton ?? -1) != _hoveredButton)
        {
            _nextReadout = Time.unscaledTime + .2f;
            foreach (var pad in _pads)
                if (pad.Active) UpdateReadout(pad, state, interactive, ReferenceEquals(pad, hovered), feedback);
        }
        _hovered = hovered;
        _hoveredButton = hovered?.SelectedButton ?? -1;
        if (hovered != null && CanUse(hovered.Device, state) && _devices[hovered.Device].Kind == BoxStationPlumbingKind.Injector
            && hovered.SelectedButton >= 2 && Input.mouseScrollDelta.y != 0)
            SendInjectorRate(hovered, state!.Value.Plumbing[hovered.Device], Input.mouseScrollDelta.y > 0 ? 1 : -1);
        else if (hovered != null && CanUse(hovered.Device, state)
            && (Input.GetKeyDown(KeyCode.E) || Input.GetMouseButtonDown(0))) SendCommand(hovered, state!.Value);
    }

    private void SendCommand(Pad pad, BoxStationState state)
    {
        string index = pad.Device.ToString(CultureInfo.InvariantCulture);
        var kind = _devices[pad.Device].Kind;
        if (kind == BoxStationPlumbingKind.Injector)
            SendInjectorCommand(pad, state.Plumbing[pad.Device]);
        else if (IsThermal(kind) && pad.SelectedButton != 0)
            SendThermalTarget(pad, state.Plumbing[pad.Device]);
        else if (kind == BoxStationPlumbingKind.Mixer && pad.SelectedButton != 0)
        {
            var value = state.Plumbing[pad.Device];
            if (pad.SelectedButton <= 2)
            {
                int ratio = Math.Clamp(value.MixerFirstBasisPoints + (pad.SelectedButton == 1 ? -100 : 100), 0, 10000);
                if (ratio != value.MixerFirstBasisPoints) _command("station-mixer-ratio-" + index + "-" + ratio.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                int current = Mathf.RoundToInt(value.MixerTargetPressure * 10);
                int pressure = pad.SelectedButton switch
                {
                    3 => current - 100, 4 => current + 100, 5 => 1013, _ => 45000
                };
                pressure = Math.Clamp(pressure, 0, (int)(BoxStationPlumbing.MaximumMixerPressure * 10));
                if (pressure / 10f != value.MixerTargetPressure) _command("station-mixer-pressure-" + index + "-" + pressure.ToString(CultureInfo.InvariantCulture));
            }
        }
        else if (kind != BoxStationPlumbingKind.Filter || pad.SelectedButton == 0)
            _command("station-plumbing-" + index);
        else if (pad.SelectedButton == 6)
        {
            pad.FilterPage = 1 - pad.FilterPage;
            for (int i = 0; i < 6; i++) pad.Selectors[i].text = FilterNames[pad.FilterPage][i];
            _nextReadout = 0;
        }
        else if (pad.SelectedButton < 6)
            _command("station-filter-gas-" + index + "-" + FilterSpecies[pad.FilterPage][pad.SelectedButton - 1].ToString(CultureInfo.InvariantCulture));
        else
        {
            int current = state.Plumbing[pad.Device].FilterLitresPerSecond;
            int rate = Math.Clamp(current + (pad.SelectedButton == 7 ? -50 : 50), 0, BoxStationPlumbing.MaximumFilterLitresPerSecond);
            if (rate != current) _command("station-filter-rate-" + index + "-" + rate.ToString(CultureInfo.InvariantCulture));
        }
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
            if (active && pad.Device != _nearest[i]) BindPad(pad, _nearest[i]);
            if (pad.Active != active) pad.Frame.gameObject.SetActive(active);
            pad.Active = active;
        }
        _nextReadout = 0;
    }

    private void BindPad(Pad pad, int index)
    {
        pad.Device = index;
        pad.FilterPage = 0;
        var kind = _devices[index].Kind;
        if (kind == BoxStationPlumbingKind.Injector)
        {
            BindInjectorPad(pad, index);
            return;
        }
        pad.Title.gameObject.SetActive(true);
        pad.Input.gameObject.SetActive(true);
        pad.Output.gameObject.SetActive(true);
        pad.State.gameObject.SetActive(true);
        pad.Hint.gameObject.SetActive(true);
        if (pad.InjectorScreen != null) pad.InjectorScreen.gameObject.SetActive(false);
        if (IsThermal(kind))
        {
            BindThermalPad(pad, index);
            return;
        }
        pad.Display.gameObject.SetActive(true);
        if (pad.Thermal != null) pad.Thermal.Canvas.gameObject.SetActive(false);
        bool filter = kind == BoxStationPlumbingKind.Filter;
        bool mixer = kind == BoxStationPlumbingKind.Mixer;
        bool configurable = filter || mixer;
        bool radiator = _radiatorsByDevice[index] >= 0;
        pad.SelectedButton = 0;
        bool floor = kind is BoxStationPlumbingKind.Vent or BoxStationPlumbingKind.Scrubber;
        var center = BoxStationModel.PlumbingButtonCenter(kind);
        var rotation = floor ? Quaternion.LookRotation(Vector3.down, Vector3.back) : Quaternion.Euler(0, 180, 0);
        pad.Frame.SetParent(_model.PlumbingControlFrame(index), false);
        pad.Frame.localPosition = center;
        pad.Frame.localRotation = rotation;
        Vector3 display = kind switch
        {
            BoxStationPlumbingKind.Valve => new(0, .37f, .326f),
            BoxStationPlumbingKind.Pump => new(-.06f, .62f, .361f),
            BoxStationPlumbingKind.Filter => new(0, 1.23f, .561f),
            BoxStationPlumbingKind.Mixer => new(0, 1.21f, .561f),
            _ => new(-.06f, .1005f, .14f)
        };
        var size = mixer ? new Vector2(960, 340) : filter ? new Vector2(960, 300) : floor ? new Vector2(300, 150) : new Vector2(420, kind == BoxStationPlumbingKind.Valve ? 180 : 200);
        pad.Display.localPosition = Quaternion.Inverse(rotation) * (display - center);
        pad.Display.sizeDelta = size;
        int rows = mixer || radiator ? 8 : filter ? 7 : 5;
        float margin = floor ? 7 : 10, row = (size.y - margin * 2) / rows;
        pad.Filtered.gameObject.SetActive(configurable || radiator);
        pad.Setting.gameObject.SetActive(configurable || radiator);
        pad.Target.gameObject.SetActive(mixer || radiator);
        pad.Target.color = Ink;
        for (int i = 0; i < rows; i++)
        {
            var label = radiator
                ? i switch { 0 => pad.Title, 1 => pad.Input, 2 => pad.Output, 3 => pad.Filtered, 4 => pad.Setting, 5 => pad.Target, 6 => pad.State, _ => pad.Hint }
                : mixer
                ? i switch { 0 => pad.Title, 1 => pad.Input, 2 => pad.Filtered, 3 => pad.Output, 4 => pad.Setting, 5 => pad.Target, 6 => pad.State, _ => pad.Hint }
                : filter
                ? i switch { 0 => pad.Title, 1 => pad.Input, 2 => pad.Output, 3 => pad.Filtered, 4 => pad.Setting, 5 => pad.State, _ => pad.Hint }
                : i switch { 0 => pad.Title, 1 => pad.Input, 2 => pad.Output, 3 => pad.State, _ => pad.Hint };
            var rect = label.rectTransform;
            rect.anchoredPosition = new(margin, -margin - i * row);
            rect.sizeDelta = new(size.x - margin * 2, row);
            label.fontSize = radiator ? i == 0 ? 19 : 18 : configurable ? i == 0 ? 32 : 31 : floor ? 17 : i == 0 ? 22 : 21;
        }
        pad.Hint.fontSize = radiator ? 16 : configurable ? 26 : floor ? 14 : 17;
        pad.Title.text = kind switch
        {
            BoxStationPlumbingKind.Valve => "GAS VALVE", BoxStationPlumbingKind.Pump => radiator ? "COOLING LOOP PUMP" : "GAS PUMP",
            BoxStationPlumbingKind.Vent => "VENT",
            BoxStationPlumbingKind.Filter => "GAS FILTER / LAYER " + _devices[index].Layer.ToString(CultureInfo.InvariantCulture),
            BoxStationPlumbingKind.Mixer => "GAS MIXER / LAYER " + _devices[index].Layer.ToString(CultureInfo.InvariantCulture),
            _ => "SCRUBBER"
        };
        pad.ButtonCanvas.gameObject.SetActive(kind != BoxStationPlumbingKind.Valve);
        var buttonSize = mixer ? new Vector2(220, 80) : filter ? new Vector2(200, 80) : floor ? new Vector2(100, 60) : new Vector2(120, 80);
        pad.ButtonCanvas.sizeDelta = buttonSize;
        pad.ButtonCanvas.localPosition = configurable ? Quaternion.Inverse(rotation) * ((mixer ? BoxStationModel.MixerButtonCenter(0) : BoxStationModel.FilterButtonCenter(0)) - center) : Vector3.zero;
        pad.Button.rectTransform.anchoredPosition = new(2, -2);
        pad.Button.rectTransform.sizeDelta = buttonSize - new Vector2(4, 4);
        pad.Button.fontSize = configurable ? 32 : floor ? 16 : 19;
        for (int i = 0; i < pad.Selectors.Length; i++)
        {
            var label = pad.Selectors[i];
            bool active = filter || mixer && i < MixerNames.Length;
            label.transform.parent.gameObject.SetActive(active);
            if (!active) continue;
            var canvas = pad.SelectorCanvases[i];
            canvas.localPosition = Quaternion.Inverse(rotation) * ((mixer ? BoxStationModel.MixerButtonCenter(i + 1) : BoxStationModel.FilterButtonCenter(i + 1)) - center);
            canvas.sizeDelta = new(220, 80);
            label.rectTransform.anchoredPosition = new(3, -3);
            label.rectTransform.sizeDelta = new(214, 74);
            label.fontSize = 32;
            label.text = mixer ? MixerNames[i] : i < 6 ? FilterNames[pad.FilterPage][i] : i == 6 ? "-50 L/s" : "+50 L/s";
        }
    }

    private void AddPad()
    {
        var frame = new GameObject("Plumbing controls").transform;
        frame.SetParent(_model.Root.transform, false);
        frame.gameObject.layer = frame.parent.gameObject.layer;
        var pad = new Pad(frame);
        _pads.Add(pad);
        pad.Display = Canvas(frame, "Plumbing readout");
        pad.Title = Label(pad.Display, "Machine", Ink);
        pad.Input = Label(pad.Display, "Inlet pressure", Ink);
        pad.Output = Label(pad.Display, "Outlet pressure", Ink);
        pad.Filtered = Label(pad.Display, "Selected outlet pressure", Ink);
        pad.Setting = Label(pad.Display, "Filter setting", Ink);
        pad.Target = Label(pad.Display, "Mixer target pressure", Ink);
        pad.State = Label(pad.Display, "Operating state", Muted);
        pad.Hint = Label(pad.Display, "Control instruction", Muted);
        pad.ButtonCanvas = Canvas(frame, "Plumbing switch");
        pad.Button = Label(pad.ButtonCanvas, "Switch position", Ink);
        pad.Button.alignment = TextAlignmentOptions.Center;
        for (int i = 0; i < pad.Selectors.Length; i++)
        {
            var canvas = Canvas(frame, "Filter selector " + i);
            pad.SelectorCanvases[i] = canvas;
            pad.Selectors[i] = Label(canvas, "Selector legend", Ink);
            pad.Selectors[i].alignment = TextAlignmentOptions.Center;
        }
    }

    private Pad? FindPad(Camera camera)
    {
        var player = LocalPlayer.Transform;
        if (player == null || !player) return null;
        var actorEye = player.position + _model.Root.transform.up * 1.3f;
        var ray = new Ray(camera.transform.position, camera.transform.forward);
        Pad? selected = null;
        float nearest = 2.4f;
        foreach (var pad in _pads)
        {
            if (!pad.Active || Vector3.Dot(ray.direction, pad.Frame.forward) <= .1f) continue;
            var kind = _devices[pad.Device].Kind;
            if (kind == BoxStationPlumbingKind.Injector)
            {
                if (!FindInjectorControl(pad, ray, actorEye, nearest, out int injectorButton, out float injectorDistance)) continue;
                selected = pad;
                pad.SelectedButton = injectorButton;
                nearest = injectorDistance;
                continue;
            }
            if (!IsThermal(kind) && (pad.Frame.position - actorEye).sqrMagnitude > 2.4f * 2.4f) continue;
            var plane = new Plane(pad.Frame.forward, pad.Frame.position);
            if (!plane.Raycast(ray, out float distance) || distance < 0 || distance > nearest) continue;
            var point = pad.Frame.InverseTransformPoint(ray.GetPoint(distance));
            int button = 0;
            bool inside = kind == BoxStationPlumbingKind.Valve ? point.x * point.x + point.y * point.y <= .15f * .15f
                : kind == BoxStationPlumbingKind.Pump ? MathF.Abs(point.x) <= .06f && MathF.Abs(point.y) <= .04f
                : MathF.Abs(point.x) <= .05f && MathF.Abs(point.y) <= .03f;
            if (IsThermal(kind))
            {
                inside = false;
                for (int i = 0; i < 6; i++)
                {
                    var center = Quaternion.Inverse(pad.Frame.localRotation) * (BoxStationModel.ThermalButtonCenter(i) - BoxStationModel.ThermalButtonCenter(0));
                    if (MathF.Abs(point.x - center.x) > .14f || MathF.Abs(point.y - center.y) > .05f) continue;
                    if ((_model.ThermalControlPoint(pad.Device, i) - actorEye).sqrMagnitude > 2.4f * 2.4f) continue;
                    inside = true;
                    button = i;
                    break;
                }
            }
            else if (kind is BoxStationPlumbingKind.Filter or BoxStationPlumbingKind.Mixer)
            {
                bool mixer = kind == BoxStationPlumbingKind.Mixer;
                inside = false;
                for (int i = 0; i < (mixer ? 7 : 9); i++)
                {
                    var center = Quaternion.Inverse(pad.Frame.localRotation) * ((mixer ? BoxStationModel.MixerButtonCenter(i) : BoxStationModel.FilterButtonCenter(i)) - BoxStationModel.PlumbingButtonCenter(kind));
                    if (MathF.Abs(point.x - center.x) > (!mixer && i == 0 ? .10f : .11f) || MathF.Abs(point.y - center.y) > .04f) continue;
                    inside = true;
                    button = i;
                    break;
                }
            }
            if (!inside) continue;
            selected = pad;
            pad.SelectedButton = button;
            nearest = distance;
        }
        if (selected == null) return null;
        if (!ControlRayClear(ray, MathF.Max(0, nearest - .003f), player)) return null;
        // The same physical contact drives local reach, host reach and the hand pose.
        var selectedKind = _devices[selected.Device].Kind;
        var contact = IsThermal(selectedKind) ? _model.ThermalControlPoint(selected.Device, selected.SelectedButton)
            : selectedKind == BoxStationPlumbingKind.Injector ? _model.InjectorControlPoint(selected.Device, selected.SelectedButton)
            : selected.Frame.position;
        var delta = contact - actorEye;
        if (!ControlRayClear(new Ray(actorEye, delta.normalized), MathF.Max(0, delta.magnitude - .04f), player)) return null;
        return selected;
    }

    private bool ControlRayClear(Ray ray, float distance, Transform player)
    {
        int count = Physics.RaycastNonAlloc(ray, _hits, distance, ~0, QueryTriggerInteraction.Ignore);
        if (count < 0 || count >= _hits.Length) return false;
        for (int i = 0; i < count; i++)
        {
            var collider = _hits[i].collider;
            if (collider != null && !collider.transform.IsChildOf(player)) return false;
        }
        return true;
    }

    private void UpdateReadout(Pad pad, BoxStationState? state, bool interactive, bool focused, string feedback)
    {
        var device = _devices[pad.Device];
        if (device.Kind == BoxStationPlumbingKind.Injector)
        {
            UpdateInjectorReadout(pad, state);
            return;
        }
        if (IsThermal(device.Kind))
        {
            UpdateThermalReadout(pad, state, interactive, focused, feedback);
            return;
        }
        bool valve = device.Kind == BoxStationPlumbingKind.Valve;
        bool filter = device.Kind == BoxStationPlumbingKind.Filter;
        bool mixer = device.Kind == BoxStationPlumbingKind.Mixer;
        bool configurable = filter || mixer;
        if (state is { } snapshot)
        {
            var value = snapshot.Plumbing[pad.Device];
            pad.Input.text = (valve ? "SIDE A  " : filter ? "FEED  " : mixer ? "INLET A  " : "IN  ") + Number(value.InputPressure, "F1") + " kPa";
            pad.Output.text = (valve ? "SIDE B  " : filter ? "REMAINDER  " : "OUT  ") + Number(value.OutputPressure, "F1") + " kPa";
            string position = valve ? value.Enabled ? "OPEN" : "CLOSED" : value.Enabled ? "ON" : "OFF";
            pad.State.text = position + (!device.RequiresPower ? " / MANUAL" : value.Powered ? " / POWERED" : " / NO POWER");
            pad.State.color = value.Enabled && (value.Powered || !device.RequiresPower) ? Green : value.Enabled ? Amber : Muted;
            pad.Button.text = value.Enabled ? "ON" : "OFF";
            if (filter)
            {
                pad.Filtered.text = "SELECTED OUT  " + Number(value.FilteredPressure, "F1") + " kPa";
                pad.Setting.text = FilterName(value.FilterSpecies) + " / " + value.FilterLitresPerSecond.ToString(CultureInfo.InvariantCulture) + " L/s";
                if (!value.Connected) pad.State.text += " / DISCONNECTED";
                if (!value.Connected) pad.State.color = Amber;
                for (int i = 0; i < 8; i++)
                    pad.Selectors[i].color = focused && pad.SelectedButton == i + 1 ? Amber
                        : i < 5 && FilterSpecies[pad.FilterPage][i] == (byte)value.FilterSpecies ? Green : Ink;
            }
            else if (mixer)
            {
                pad.Filtered.text = "INLET B  " + Number(value.SecondaryInputPressure, "F1") + " kPa";
                pad.Setting.text = "MOL%  A " + (value.MixerFirstBasisPoints / 100f).ToString("F2", CultureInfo.InvariantCulture)
                    + " / B " + ((10000 - value.MixerFirstBasisPoints) / 100f).ToString("F2", CultureInfo.InvariantCulture);
                pad.Target.text = "TARGET  " + Number(value.MixerTargetPressure, "F1") + " kPa";
                if (!value.Connected) pad.State.text += " / DISCONNECTED";
                if (!value.Connected) pad.State.color = Amber;
                for (int i = 0; i < MixerNames.Length; i++)
                    pad.Selectors[i].color = focused && pad.SelectedButton == i + 1 ? Amber : Ink;
            }
        }
        else
        {
            pad.Input.text = valve ? "SIDE A  -- kPa" : filter ? "FEED  -- kPa" : mixer ? "INLET A  -- kPa" : "IN  -- kPa";
            pad.Output.text = valve ? "SIDE B  -- kPa" : filter ? "REMAINDER  -- kPa" : "OUT  -- kPa";
            pad.State.text = "AWAITING HOST";
            pad.State.color = Muted;
            pad.Button.text = "--";
            pad.Filtered.text = mixer ? "INLET B  -- kPa" : "SELECTED OUT  -- kPa";
            pad.Setting.text = mixer ? "MOL%  A -- / B --" : "-- / -- L/s";
            pad.Target.text = "TARGET  -- kPa";
            foreach (var selector in pad.Selectors) selector.color = Muted;
        }
        int radiator = _radiatorsByDevice[pad.Device];
        if (radiator >= 0)
        {
            // This is one connected panel, not a loop average or an aggregate cooling output.
            if (state is { } current && radiator < current.Radiators.Length)
            {
                var sample = current.Radiators[radiator];
                pad.Filtered.text = "SAMPLE PANEL  " + Number(sample.SurfaceKelvin, "F2") + " K";
                pad.Setting.text = sample.GasKelvin > 0
                    ? "SAMPLE GAS  " + Number(sample.GasKelvin, "F2") + " K / " + Number(sample.Pressure, "F1") + " kPa"
                    : "SAMPLE GAS  NO AIR";
                pad.Target.text = sample.EnvironmentAvailable ? "ORBIT ENV: CURRENT" : "ORBIT ENV: UNAVAILABLE";
                pad.Target.color = sample.EnvironmentAvailable ? Muted : Amber;
            }
            else
            {
                pad.Filtered.text = "SAMPLE PANEL  -- K";
                pad.Setting.text = "SAMPLE GAS  -- K / -- kPa";
                pad.Target.text = "ORBIT ENV: UNAVAILABLE";
                pad.Target.color = Muted;
            }
        }
        bool usable = CanUse(pad.Device, state);
        var focusColor = usable ? Green : Amber;
        pad.Button.color = focused && (!configurable || pad.SelectedButton == 0) ? focusColor : Ink;
        pad.Title.color = focused ? focusColor : Ink;
        pad.Hint.text = focused && feedback.Length > 0 ? feedback : !interactive ? "READ ONLY" : state == null ? "AWAITING HOST"
            : !usable ? "NO POWER TO VALVE" : focused ? configurable && pad.SelectedButton != 0 ? "E / CLICK: " + pad.Selectors[pad.SelectedButton - 1].text : valve ? "E / CLICK: TURN VALVE" : "E / CLICK: TOGGLE"
            : valve ? "AIM AT HANDWHEEL" : configurable ? "AIM AT A CONTROL" : "AIM AT SWITCH";
        pad.Hint.color = focused ? focusColor : Muted;
    }

    private bool CanUse(int index, BoxStationState? state)
        => state is { } value && (_devices[index].Kind != BoxStationPlumbingKind.Valve
            || !_devices[index].RequiresPower || value.Plumbing[index].Powered);

    private static string FilterName(StationGasSpecies species) => species switch
    {
        0 => "BYPASS", StationGasSpecies.Oxygen => "OXYGEN", StationGasSpecies.Nitrogen => "NITROGEN",
        StationGasSpecies.CO2 => "CO2", StationGasSpecies.Plasma => "PLASMA", StationGasSpecies.N2O => "N2O",
        StationGasSpecies.WaterVapor => "WATER VAPOR", StationGasSpecies.Tritium => "TRITIUM", StationGasSpecies.BZ => "BZ",
        _ => "UNKNOWN GAS"
    };

    private static RectTransform Canvas(Transform parent, string name)
    {
        var canvas = new GameObject(name).AddComponent<RectTransform>();
        canvas.SetParent(parent, false);
        canvas.gameObject.layer = parent.gameObject.layer;
        canvas.localScale = Vector3.one * PixelScale;
        canvas.gameObject.AddComponent<UnityEngine.Canvas>().renderMode = RenderMode.WorldSpace;
        return canvas;
    }

    private TextMeshProUGUI Label(Transform parent, string name, Color color)
    {
        var rect = new GameObject(name).AddComponent<RectTransform>();
        rect.gameObject.layer = parent.gameObject.layer;
        rect.SetParent(parent, false);
        rect.anchorMin = rect.anchorMax = rect.pivot = new(0, 1);
        var text = rect.gameObject.AddComponent<TextMeshProUGUI>();
        _typography.Apply(text, TabletTextRole.Control);
        var source = text.fontSharedMaterial;
        if (source != null)
        {
            int id = source.GetInstanceID();
            if (!_fonts.TryGetValue(id, out var material))
            {
                material = new Material(source) { name = "Plumbing control text" };
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
        text.isOverlay = false;
        text.text = "";
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
        internal int SelectedButton;
        internal int FilterPage;
        internal bool Active;
        internal ThermalDisplay? Thermal;
        internal RectTransform? InjectorScreen;
        internal RectTransform Display = null!, ButtonCanvas = null!;
        internal TextMeshProUGUI Title = null!, Input = null!, Output = null!, Filtered = null!, Setting = null!, Target = null!, State = null!, Hint = null!, Button = null!;
        internal readonly TextMeshProUGUI[] Selectors = new TextMeshProUGUI[8];
        internal readonly RectTransform[] SelectorCanvases = new RectTransform[8];
    }
}
