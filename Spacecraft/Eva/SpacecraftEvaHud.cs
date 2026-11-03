// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Globalization;
using Modstoke.Sons;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Modstoke.Client;

internal sealed class SpacecraftEvaHud : IDisposable
{
    private static readonly Color Ink = new(.92f, .96f, .97f, 1);
    private static readonly Color Muted = new(.61f, .69f, .72f, 1);
    private static readonly Color Green = new(.38f, .83f, .67f, 1);
    private static readonly Color Amber = new(1, .72f, .28f, 1);
    private readonly ClientTabletTypography _typography = new();
    private GameObject? _root;
    private TextMeshProUGUI _status = null!, _resources = null!, _atmosphere = null!;
    private float _nextReadout;
    private bool _disposed;

    internal SpacecraftEvaHud()
    {
        try
        {
            _root = new GameObject("EVA suit readout");
            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 115;
            var scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new(1280, 720);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
            var panel = Node(_root.transform, "Suit instruments", 0, 0, 300, 138);
            panel.anchorMin = panel.anchorMax = panel.pivot = new(1, 1);
            panel.anchoredPosition = new(-18, -86);
            var background = panel.gameObject.AddComponent<Image>();
            background.color = new(.016f, .025f, .029f, .88f);
            background.raycastTarget = false;
            _status = Label(panel, "Suit status", 14, 9, 272, 23, 16, Green);
            _resources = Label(panel, "Suit reserves", 14, 39, 272, 48, 15, Ink);
            _resources.enableWordWrapping = true;
            _atmosphere = Label(panel, "Breathing loop", 14, 92, 272, 36, 13, Muted);
            _atmosphere.enableWordWrapping = true;
            _root.SetActive(false);
        }
        catch { Dispose(); throw; }
    }

    // The owner supplies only fresh state belonging to the current local actor lease.
    internal void Update(SpacecraftEvaSlotState[]? state, int localSeat)
    {
        if (_disposed || _root == null || !_root) return;
        int slot = -1;
        if (state is { Length: SpacecraftEvaSuit.Count } && localSeat is >= 0 and < SpacecraftEvaSuit.Count)
            for (int i = 0; i < state.Length; i++)
                if (state[i].Seat == localSeat) { slot = i; break; }
        bool visible = slot >= 0 && GameRuntime.State.IsInWorld && !GameRuntime.State.PauseMenuOpen;
        bool newlyVisible = visible && !_root.activeSelf;
        _root.SetActive(visible);
        if (!visible || !newlyVisible && Time.unscaledTime < _nextReadout) return;
        _nextReadout = Time.unscaledTime + .2f;
        var suit = state![slot].Suit;
        string alarm = Alarm(suit);
        _status.text = "EVA " + (slot + 1).ToString("00", CultureInfo.InvariantCulture) + " / " + alarm;
        _status.color = alarm == "NOMINAL" ? Green : Amber;
        _resources.text = "O2 RESERVE  " + suit.OxygenMoles.ToString("F1", CultureInfo.InvariantCulture) + " mol\n"
            + "BAT " + Percent(suit.BatteryKwh, SpacecraftEvaSuit.BatteryCapacityKwh)
            + "   FILTER " + Percent(suit.FilterMoles, SpacecraftEvaSuit.FilterCapacityMoles);
        _atmosphere.text = suit.Pressure.ToString("F1", CultureInfo.InvariantCulture) + " kPa   O2 "
            + (suit.OxygenFraction * 100).ToString("F1", CultureInfo.InvariantCulture) + "%\nCO2 "
            + (suit.CO2Fraction * 100).ToString("F2", CultureInfo.InvariantCulture) + "%   "
            + suit.TemperatureC.ToString("F1", CultureInfo.InvariantCulture) + " C";
        _atmosphere.color = suit.CO2Fraction >= .005f || suit.Pressure < 40 ? Amber : Muted;
    }

    internal static string Alarm(SpacecraftEvaSuitState suit)
    {
        if (!suit.Powered) return "POWER DEPLETED";
        if (suit.Pressure < 40) return "LOW PRESSURE";
        float oxygen = suit.Pressure * suit.OxygenFraction;
        if (oxygen < 16) return "LOW OXYGEN";
        if (suit.CO2Fraction >= .005f) return "HIGH CO2";
        if (suit.OxygenMoles < 2) return "LOW O2 RESERVE";
        if (suit.BatteryKwh < SpacecraftEvaSuit.BatteryCapacityKwh * .15) return "LOW BATTERY";
        if (suit.FilterMoles < SpacecraftEvaSuit.FilterCapacityMoles * .15) return "FILTER LOW";
        if (suit.TemperatureC is < 5 or > 40) return "TEMPERATURE";
        return "NOMINAL";
    }

    internal static string Percent(float value, double capacity)
        => Math.Clamp(value / capacity * 100, 0, 100).ToString("F0", CultureInfo.InvariantCulture) + "%";

    private TextMeshProUGUI Label(Transform parent, string name, float x, float y, float width,
        float height, float size, Color color)
    {
        var text = Node(parent, name, x, y, width, height).gameObject.AddComponent<TextMeshProUGUI>();
        _typography.Apply(text, TabletTextRole.Control);
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
        rect.SetParent(parent, false);
        rect.anchorMin = rect.anchorMax = rect.pivot = new(0, 1);
        rect.anchoredPosition = new(x, -y);
        rect.sizeDelta = new(width, height);
        return rect;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_root != null && _root) { _root.SetActive(false); UnityEngine.Object.Destroy(_root); }
        _root = null;
        _typography.Dispose();
    }
}
