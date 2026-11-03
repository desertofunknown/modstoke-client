// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace Modstoke.Client;

internal sealed partial class BoxStationPlumbingControls
{
    private static readonly string[] ThermalNames = ["MIN", "-5 K", "+5 K", "MAX", "ROOM"];
    private static readonly Color Cold = new(.42f, .78f, 1, 1), Hot = new(1, .52f, .35f, 1);
    private Material? _thermalGraphics;

    private static bool IsThermal(BoxStationPlumbingKind kind)
        => kind is BoxStationPlumbingKind.Heater or BoxStationPlumbingKind.Freezer;

    private void SendThermalTarget(Pad pad, BoxStationPlumbingState state)
    {
        int current = Mathf.RoundToInt(state.ThermalTargetKelvin * 100);
        int minimum = Mathf.RoundToInt(state.ThermalMinimumKelvin * 100);
        int maximum = Mathf.RoundToInt(state.ThermalMaximumKelvin * 100);
        int target = pad.SelectedButton switch
        {
            1 => minimum, 2 => current - 500, 3 => current + 500, 4 => maximum, 5 => 29315,
            _ => current
        };
        target = Math.Clamp(target, minimum, maximum);
        if (target == current) return;
        _command("station-thermal-target-" + pad.Device.ToString(CultureInfo.InvariantCulture) + "-"
            + pad.SelectedButton.ToString(CultureInfo.InvariantCulture) + "-" + target.ToString(CultureInfo.InvariantCulture));
    }

    private void BindThermalPad(Pad pad, int index)
    {
        pad.SelectedButton = 0;
        pad.Frame.SetParent(_model.PlumbingControlFrame(index), false);
        pad.Frame.localPosition = BoxStationModel.ThermalButtonCenter(0);
        pad.Frame.localRotation = Quaternion.Euler(0, 180, 0);
        pad.Display.gameObject.SetActive(false);
        pad.Thermal ??= CreateThermalDisplay(pad.Frame);
        var display = pad.Thermal;
        display.Canvas.gameObject.SetActive(true);
        bool heater = _devices[index].Kind == BoxStationPlumbingKind.Heater;
        display.Title.text = heater ? "PIPE HEATER  /  RESISTIVE" : "PIPE FREEZER  /  HEAT PUMP";
        display.Title.color = heater ? Hot : Cold;
        pad.ButtonCanvas.gameObject.SetActive(true);
        FitThermalKey(pad.ButtonCanvas, pad.Button, 0);
        for (int i = 0; i < pad.Selectors.Length; i++)
        {
            bool active = i < ThermalNames.Length;
            pad.SelectorCanvases[i].gameObject.SetActive(active);
            if (!active) continue;
            FitThermalKey(pad.SelectorCanvases[i], pad.Selectors[i], i + 1);
            pad.Selectors[i].text = ThermalNames[i];
        }
    }

    private static void FitThermalKey(RectTransform canvas, TextMeshProUGUI label, int button)
    {
        canvas.localPosition = Quaternion.Euler(0, 180, 0) * (BoxStationModel.ThermalButtonCenter(button) - BoxStationModel.ThermalButtonCenter(0));
        canvas.sizeDelta = new(280, 100);
        label.rectTransform.anchoredPosition = new(6, -6);
        label.rectTransform.sizeDelta = new(268, 88);
        label.fontSize = 29;
    }

    private ThermalDisplay CreateThermalDisplay(Transform frame)
    {
        var canvas = Canvas(frame, "Thermal instrument glass");
        canvas.localPosition = Quaternion.Euler(0, 180, 0) * (new Vector3(0, 1.36f, .601f) - BoxStationModel.ThermalButtonCenter(0));
        canvas.sizeDelta = new(980, 500);
        ThermalBand(canvas, "Temperature field", new(12, 49, 956, 137), new(.025f, .045f, .055f, 1));
        ThermalBand(canvas, "Temperature divider", new(490, 57, 2, 118), new(.16f, .25f, .28f, 1));
        ThermalBand(canvas, "Power rule", new(20, 193, 940, 2), new(.16f, .25f, .28f, 1));
        ThermalBand(canvas, "Control rule", new(20, 411, 940, 2), new(.16f, .25f, .28f, 1));
        var status = ThermalBand(canvas, "Operating state strip", new(14, 303, 5, 91), Muted);
        ThermalLabel(canvas, "Pipe caption", "PIPE TEMPERATURE", 22, 57, 440, 25, 20, Muted);
        ThermalLabel(canvas, "Target caption", "TARGET", 516, 57, 440, 25, 20, Muted);
        ThermalLabel(canvas, "Preset instruction", "TARGET KEYS: MIN / ROOM 293.15 K / MAX", 22, 468, 936, 23, 19, Muted);
        return new ThermalDisplay(canvas, status,
            ThermalLabel(canvas, "Device identity", "", 22, 8, 936, 33, 28, Ink),
            ThermalLabel(canvas, "Current temperature", "", 22, 84, 448, 60, 55, Ink),
            ThermalLabel(canvas, "Target temperature", "", 516, 84, 442, 60, 55, Ink),
            ThermalLabel(canvas, "Pipe pressure", "", 22, 150, 448, 29, 24, Muted),
            ThermalLabel(canvas, "Target range", "", 516, 150, 442, 29, 23, Muted),
            ThermalLabel(canvas, "Actual electric demand", "", 22, 208, 468, 36, 27, Ink),
            ThermalLabel(canvas, "Actual thermal transfer", "", 516, 208, 442, 36, 27, Ink),
            ThermalLabel(canvas, "Heat rejection", "", 22, 254, 936, 31, 23, Muted),
            ThermalLabel(canvas, "Operating status", "", 32, 306, 916, 40, 30, Ink),
            ThermalLabel(canvas, "Power selection", "", 32, 352, 916, 34, 22, Muted),
            ThermalLabel(canvas, "Control feedback", "", 22, 426, 936, 35, 23, Muted));
    }

    private TextMeshProUGUI ThermalLabel(Transform parent, string name, string value, float x, float y, float width, float height, float size, Color color)
    {
        var label = Label(parent, name, color);
        label.text = value;
        label.rectTransform.anchoredPosition = new(x, -y);
        label.rectTransform.sizeDelta = new(width, height);
        label.fontSize = size;
        return label;
    }

    private Image ThermalBand(Transform parent, string name, Rect bounds, Color color)
    {
        if (_thermalGraphics == null)
        {
            _thermalGraphics = new Material(Graphic.defaultGraphicMaterial) { name = "Thermal display graphics" };
            _materials.Add(_thermalGraphics);
            _thermalGraphics.SetInt("unity_GUIZTestMode", (int)CompareFunction.LessEqual);
            if (_thermalGraphics.HasProperty("_ZTestMode")) _thermalGraphics.SetInt("_ZTestMode", (int)CompareFunction.LessEqual);
            ClientDeviceScreenMaterial.ConfigureOwnedUiMaterial(_thermalGraphics);
        }
        var rect = new GameObject(name).AddComponent<RectTransform>();
        rect.gameObject.layer = parent.gameObject.layer;
        rect.SetParent(parent, false);
        rect.anchorMin = rect.anchorMax = rect.pivot = new(0, 1);
        rect.anchoredPosition = new(bounds.x, -bounds.y);
        rect.sizeDelta = new(bounds.width, bounds.height);
        // Positive local Z goes into the glass because the control canvas faces -Z.
        rect.localPosition += new Vector3(0, 0, .3f);
        var image = rect.gameObject.AddComponent<Image>();
        image.material = _thermalGraphics;
        image.color = color;
        image.raycastTarget = false;
        return image;
    }

    private void UpdateThermalReadout(Pad pad, BoxStationState? state, bool interactive, bool focused, string feedback)
    {
        var display = pad.Thermal!;
        bool heater = _devices[pad.Device].Kind == BoxStationPlumbingKind.Heater;
        Color accent = heater ? Hot : Cold;
        if (state is { } snapshot)
        {
            var value = snapshot.Plumbing[pad.Device];
            bool empty = value.ThermalCurrentKelvin <= 0;
            display.Current.text = empty ? "NO AIR" : Number(value.ThermalCurrentKelvin, "F2") + " K";
            display.Target.text = Number(value.ThermalTargetKelvin, "F2") + " K";
            display.Pressure.text = Number(value.InputPressure, "F2") + " kPa";
            display.Range.text = Number(value.ThermalMinimumKelvin, "F2") + " - " + Number(value.ThermalMaximumKelvin, "F2") + " K";
            display.Electric.text = "LAST DRAW " + Number(value.ElectricWatts / 1000, "F1") + " / 20 kW";
            float transfer = MathF.Abs(value.ThermalWatts);
            display.Transfer.text = (heater ? "LAST HEAT " : "LAST COOL ")
                + Number(transfer / (transfer >= 1_000_000 ? 1_000_000 : 1000), "F1") + (transfer >= 1_000_000 ? " MW" : " kW");
            display.Sink.text = heater ? "HEAT SOURCE: ELECTRIC ELEMENT / SINK: N/A"
                : value.ThermalSinkAvailable ? "HEAT REJECTION: ROOM AIR AVAILABLE" : "HEAT REJECTION: BLOCKED / NO ROOM CAPACITY";
            string status;
            Color statusColor;
            if (!value.Enabled) { status = "OFF"; statusColor = Muted; }
            else if (!value.Connected) { status = "DISCONNECTED PIPE"; statusColor = Amber; }
            else if (empty) { status = "NO AIR IN PIPE"; statusColor = Amber; }
            else if (!heater && !value.ThermalSinkAvailable) { status = "BLOCKED HEAT SINK"; statusColor = Amber; }
            else if (!value.Powered) { status = "NO ELECTRIC POWER"; statusColor = Amber; }
            else if (value.ThermalActive) { status = heater ? "HEATING" : "COOLING"; statusColor = accent; }
            else if (heater ? value.ThermalCurrentKelvin < value.ThermalTargetKelvin - .01f : value.ThermalCurrentKelvin > value.ThermalTargetKelvin + .01f)
            { status = "IDLE / AWAITING THERMAL WORK"; statusColor = Muted; }
            else { status = "TARGET SATISFIED / IDLE"; statusColor = Green; }
            display.Status.text = status;
            display.Status.color = statusColor;
            display.StatusStrip.color = statusColor;
            display.Selection.text = "REQUEST: " + (value.Enabled ? "ON" : "OFF") + " / OUTPUT: "
                + (value.ThermalPowerLimited ? value.ElectricWatts > 0 ? "PARTIAL SUPPLY" : "NO SUPPLY"
                    : value.ThermalActive ? "WORK RECORDED" : "IDLE");
            display.Selection.color = value.ThermalPowerLimited ? Amber : Muted;
            pad.Button.text = value.Enabled ? "ON" : "OFF";
            pad.Button.color = focused && pad.SelectedButton == 0 ? Amber : value.Enabled ? Green : Muted;
            for (int i = 0; i < ThermalNames.Length; i++)
            {
                bool selected = i switch
                {
                    0 => MathF.Abs(value.ThermalTargetKelvin - value.ThermalMinimumKelvin) < .005f,
                    3 => MathF.Abs(value.ThermalTargetKelvin - value.ThermalMaximumKelvin) < .005f,
                    4 => MathF.Abs(value.ThermalTargetKelvin - 293.15f) < .005f,
                    _ => false
                };
                pad.Selectors[i].color = focused && pad.SelectedButton == i + 1 ? Amber : selected ? accent : Ink;
            }
        }
        else
        {
            display.Current.text = display.Target.text = "-- K";
            display.Pressure.text = "-- kPa";
            display.Range.text = "RANGE -- K";
            display.Electric.text = "LAST DRAW -- kW";
            display.Transfer.text = heater ? "LAST HEAT -- kW" : "LAST COOL -- kW";
            display.Sink.text = heater ? "HEAT SOURCE: ELECTRIC ELEMENT" : "HEAT REJECTION: STATUS UNAVAILABLE";
            display.Status.text = "AWAITING HOST";
            display.Status.color = display.StatusStrip.color = Muted;
            display.Selection.text = "POWER REQUEST: UNKNOWN";
            display.Selection.color = Muted;
            pad.Button.text = "--";
            pad.Button.color = Muted;
            foreach (var label in pad.Selectors) label.color = Muted;
        }
        display.Hint.text = focused && feedback.Length > 0 ? feedback : state == null ? "AWAITING HOST" : !interactive ? "READ ONLY"
            : focused ? pad.SelectedButton == 0 ? "E / CLICK: TOGGLE POWER" : "E / CLICK: " + ThermalNames[pad.SelectedButton - 1]
            : "AIM AT A PHYSICAL CONTROL";
        display.Hint.color = focused ? Amber : Muted;
    }

    private static string Number(float value, string format)
        => value.ToString(MathF.Abs(value) >= 10_000 ? "0.0E+0" : format, CultureInfo.InvariantCulture);

    private sealed record ThermalDisplay(RectTransform Canvas, Image StatusStrip, TextMeshProUGUI Title,
        TextMeshProUGUI Current, TextMeshProUGUI Target, TextMeshProUGUI Pressure, TextMeshProUGUI Range,
        TextMeshProUGUI Electric, TextMeshProUGUI Transfer, TextMeshProUGUI Sink, TextMeshProUGUI Status,
        TextMeshProUGUI Selection, TextMeshProUGUI Hint);
}
