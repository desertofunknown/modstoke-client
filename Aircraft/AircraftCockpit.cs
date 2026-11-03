// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Modstoke.Client;

// The aircraft's physical dashboard owns flight instruments. This small strip provides
// accessible switches and reminders; the rider owns cursor focus and the Tab binding.
internal sealed class ClientAircraftCockpit : IDisposable
{
    private static readonly Color Ink = new(.94f, .97f, .95f, 1);
    private static readonly Color Muted = new(.73f, .82f, .80f, 1);
    private static readonly Color Amber = new(1, .78f, .36f, 1);
    private readonly Action<string> _command;
    private readonly List<CockpitButton> _buttons = new();
    private ClientTabletTypography? _typography;
    private GameObject? _root;
    private RectTransform _strip = null!;
    private RectTransform _switches = null!;
    private GraphicRaycaster _raycaster = null!;
    private TextMeshProUGUI _summary = null!, _feedback = null!, _hint = null!;
    private bool _disposed, _visible;
    private int _seat = -1;
    private bool? _appliedMouseControls;
    private DisplayState? _display;

    internal bool MouseControlsVisible { get; private set; }
    internal bool IsAvailable => !_disposed && _root != null && _strip != null
        && _switches != null && _summary != null && _feedback != null && _hint != null && _raycaster != null;

    internal ClientAircraftCockpit(Action<string> command)
    {
        _command = command;
        try
        {
            _typography = new ClientTabletTypography();
            _root = new GameObject("Aircraft cockpit controls");
            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 120;
            var scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 720);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
            _raycaster = _root.AddComponent<GraphicRaycaster>();
            _raycaster.enabled = false;
            _strip = Node(_root.transform, "Checklist", 0, 0, 1160, 138);
            _strip.anchorMin = _strip.anchorMax = new Vector2(.5f, 0);
            _strip.pivot = new Vector2(.5f, 0);
            _strip.anchoredPosition = new Vector2(0, 14);
            var background = _strip.gameObject.AddComponent<Image>();
            background.color = new Color(.035f, .055f, .060f, .92f);
            background.raycastTarget = false;
            _summary = Text(_strip, "Status", 16, 10, 1128, 26, 19, Ink);
            _feedback = Text(_strip, "Request feedback", 16, 38, 1128, 46, 18, Amber);
            _feedback.enableWordWrapping = true;
            _hint = Text(_strip, "Controls", 16, 86, 1128, 44, 16, Muted);
            _switches = Node(_strip, "Switches", 56, 138, 1048, 46);
            AddButton("engine", "Start engine", 0, 138);
            AddButton("flaps", "Flaps", 148, 122);
            AddButton("lights", "Lights", 280, 118);
            AddButton("throttle-", "Throttle −", 408, 132);
            AddButton("throttle+", "Throttle +", 550, 132);
            AddButton("brake", "Toggle brake", 692, 132);
            AddButton("exit", "Request exit", 834, 214);
            _root.SetActive(false);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal void SetMouseControlsVisible(bool visible)
    {
        var available = IsAvailable;
        MouseControlsVisible = visible && available && _visible;
        if (!available) { _appliedMouseControls = null; return; }
        if (_appliedMouseControls == MouseControlsVisible) return;
        _switches.gameObject.SetActive(MouseControlsVisible);
        _raycaster.enabled = MouseControlsVisible;
        _strip.sizeDelta = new Vector2(1160, MouseControlsVisible ? 198 : 138);
        _appliedMouseControls = MouseControlsVisible;
    }

    internal void Update(AircraftFlightState state, int seat, bool visible, string feedback = "")
    {
        if (!IsAvailable)
        {
            _visible = MouseControlsVisible = false;
            _appliedMouseControls = null;
            _display = null;
            if (_root != null) _root.SetActive(false);
            return;
        }
        var wasVisible = _visible;
        _visible = visible && seat is >= 0 and <= 2;
        _seat = _visible ? seat : -1;
        if (_visible != wasVisible) _root!.SetActive(_visible);
        if (!_visible) { _display = null; SetMouseControlsVisible(false); return; }
        SetMouseControlsVisible(MouseControlsVisible);
        var warning = state.Health <= 0 ? "AIRFRAME DAMAGED"
            : state.Stall ? "STALL — Lower the nose and build airspeed"
            : state.Fuel <= .1f ? "LOW FUEL"
            : state.Grounded ? state.Engine ? "Check the runway before applying throttle" : "Start engine when the runway is clear"
            : "Keep a safe airspeed; use the dashboard instruments";
        var display = new DisplayState(seat, MouseControlsVisible, warning,
            state.Stall || state.Health <= 0 || state.Fuel <= .1f,
            string.IsNullOrWhiteSpace(feedback) ? "Exit requests are checked by the host." : feedback,
            state.Engine, state.Flaps, state.Lights);
        if (_display == display) return;
        var pilot = seat == 0;
        _summary.text = (pilot ? "PILOT" : "PASSENGER " + seat.ToString(CultureInfo.InvariantCulture))
            + "  ·  " + warning;
        _summary.color = display.Alert ? Amber : Ink;
        _feedback.text = display.Feedback;
        _hint.text = pilot
            ? MouseControlsVisible
                ? "Tab returns to flight controls  ·  X requests exit"
                : "W/S pitch  ·  A/D roll  ·  Q/E yaw  ·  Shift/Ctrl throttle  ·  Space brake\nF flaps  ·  I engine  ·  L lights  ·  Tab controls  ·  X exit request / cancel boarding"
            : "Pilot controls the aircraft  ·  Tab opens controls  ·  X exit request / cancel boarding";
        foreach (var button in _buttons)
        {
            if (button.Button == null || button.Label == null) continue;
            button.Button.interactable = button.Command == "exit" || pilot;
            button.Label.color = button.Button.interactable ? Ink : Muted;
            button.Label.text = button.Command switch
            {
                "engine" => state.Engine ? "Stop engine" : "Start engine",
                "flaps" => "Flaps " + state.Flaps.ToString(CultureInfo.InvariantCulture),
                "lights" => state.Lights ? "Lights on" : "Lights off",
                "brake" => "Toggle brake",
                "exit" => "Request exit",
                _ => button.Title
            };
        }
        _display = display;
    }

    private void AddButton(string command, string title, float x, float width)
    {
        var node = Node(_switches, command, x, 0, width, 46);
        var image = node.gameObject.AddComponent<Image>();
        image.color = new Color(.16f, .24f, .25f, 1);
        image.raycastTarget = true;
        var button = node.gameObject.AddComponent<Button>();
        button.targetGraphic = image;
        var label = Text(node, "Label", 5, 3, width - 10, 40, 18, Ink);
        label.alignment = TextAlignmentOptions.Center;
        label.text = title;
        button.onClick.AddListener((UnityAction)new Action(() =>
        {
            if (!IsAvailable || !_visible || !MouseControlsVisible || button == null || !button.interactable
                || command != "exit" && _seat != 0) return;
            _command(command);
        }));
        _buttons.Add(new CockpitButton(button, label, command, title));
    }

    private TextMeshProUGUI Text(Transform parent, string name, float x, float y,
        float width, float height, float size, Color color)
    {
        var node = Node(parent, name, x, y, width, height);
        var text = node.gameObject.AddComponent<TextMeshProUGUI>();
        _typography!.Apply(text, TabletTextRole.Control);
        text.fontSize = size;
        text.color = color;
        text.richText = false;
        text.enableWordWrapping = false;
        text.overflowMode = TextOverflowModes.Ellipsis;
        text.raycastTarget = false;
        text.alignment = TextAlignmentOptions.MidlineLeft;
        return text;
    }

    private static RectTransform Node(Transform parent, string name, float x, float y, float width, float height)
    {
        var rect = new GameObject(name).AddComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
        rect.anchoredPosition = new Vector2(x, -y);
        rect.sizeDelta = new Vector2(width, height);
        return rect;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _visible = MouseControlsVisible = false;
        _appliedMouseControls = null;
        _display = null;
        foreach (var button in _buttons)
            if (button.Button != null) button.Button.onClick.RemoveAllListeners();
        _buttons.Clear();
        if (_root != null)
        {
            _root.SetActive(false);
            UnityEngine.Object.Destroy(_root);
        }
        _root = null;
        _typography?.Dispose();
        _typography = null;
    }

    private readonly record struct DisplayState(int Seat, bool MouseControls, string Warning, bool Alert,
        string Feedback, bool Engine, byte Flaps, bool Lights);

    private sealed record CockpitButton(Button Button, TextMeshProUGUI Label, string Command, string Title);
}
