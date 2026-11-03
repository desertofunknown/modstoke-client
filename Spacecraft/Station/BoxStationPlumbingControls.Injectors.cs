// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Globalization;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class BoxStationPlumbingControls
{
    private void BindInjectorPad(Pad pad, int index)
    {
        pad.SelectedButton = 0;
        pad.Frame.SetParent(_model.PlumbingControlFrame(index), false);
        pad.Frame.localPosition = BoxStationModel.PlumbingButtonCenter(BoxStationPlumbingKind.Injector);
        pad.Frame.localRotation = Quaternion.LookRotation(Vector3.down, Vector3.forward);
        pad.Display.gameObject.SetActive(true);
        if (pad.Thermal != null) pad.Thermal.Canvas.gameObject.SetActive(false);
        // Text and its unlit backing sit just above the glass at Y=.1085 without z-fighting.
        pad.Display.localPosition = Quaternion.Inverse(pad.Frame.localRotation)
            * (new Vector3(.04f, .1091f, -.275f) - pad.Frame.localPosition);
        pad.Display.sizeDelta = new(164, 57);
        pad.InjectorScreen ??= ThermalBand(pad.Display, "Injector display glass", new(0, 0, 164, 57),
            new(.012f, .025f, .03f, 1)).rectTransform;
        pad.InjectorScreen.SetAsFirstSibling();
        pad.InjectorScreen.gameObject.SetActive(true);
        pad.ButtonCanvas.gameObject.SetActive(false);
        foreach (var canvas in pad.SelectorCanvases) canvas.gameObject.SetActive(false);
        pad.Title.gameObject.SetActive(false);
        pad.Output.gameObject.SetActive(false);
        pad.Filtered.gameObject.SetActive(false);
        pad.Target.gameObject.SetActive(false);
        pad.Hint.gameObject.SetActive(false);
        pad.Input.gameObject.SetActive(true);
        pad.Setting.gameObject.SetActive(true);
        pad.State.gameObject.SetActive(true);
        for (int i = 0; i < 3; i++)
        {
            var label = i == 0 ? pad.Input : i == 1 ? pad.Setting : pad.State;
            label.rectTransform.anchoredPosition = new(4, -3 - i * 17);
            label.rectTransform.sizeDelta = new(156, 17);
            label.fontSize = 12;
            label.color = Ink;
        }
    }

    private bool FindInjectorControl(Pad pad, Ray ray, Vector3 actorEye, float maximum, out int button, out float distance)
    {
        button = -1;
        distance = maximum;
        var frame = _model.PlumbingControlFrame(pad.Device);
        for (int control = 0; control < 3; control++)
        {
            var center = BoxStationModel.InjectorButtonCenter(control);
            var contact = frame.TransformPoint(center);
            if ((contact - actorEye).sqrMagnitude > 2.4f * 2.4f) continue;
            if (!new Plane(frame.up, contact).Raycast(ray, out float hit) || hit < 0 || hit > distance) continue;
            var offset = frame.InverseTransformPoint(ray.GetPoint(hit)) - center;
            bool inside = control == 0 ? MathF.Abs(offset.x) <= .018f && MathF.Abs(offset.z) <= .025f
                : offset.x * offset.x + offset.z * offset.z <= (control == 1 ? .023f * .023f : .030f * .030f);
            if (!inside) continue;
            button = control == 2 && offset.x >= 0 ? 3 : control;
            distance = hit;
        }
        return button >= 0;
    }

    private void SendInjectorCommand(Pad pad, BoxStationPlumbingState state)
    {
        string index = pad.Device.ToString(CultureInfo.InvariantCulture);
        if (pad.SelectedButton == 0) _command("station-plumbing-" + index);
        else if (pad.SelectedButton == 1) _command("station-injector-pulse-" + index);
        else SendInjectorRate(pad, state, pad.SelectedButton == 2 ? -1 : 1);
    }

    private void SendInjectorRate(Pad pad, BoxStationPlumbingState state, int direction)
    {
        int step = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift) ? 1 : 10;
        int rate = Math.Clamp(state.InjectorLitresPerSecond + direction * step, 0, BoxStationPlumbing.MaximumInjectorLitresPerSecond);
        if (rate == state.InjectorLitresPerSecond) return;
        _command("station-injector-rate-" + pad.Device.ToString(CultureInfo.InvariantCulture) + "-" + rate.ToString(CultureInfo.InvariantCulture));
    }

    private static void UpdateInjectorReadout(Pad pad, BoxStationState? state)
    {
        if (state is not { } snapshot)
        {
            pad.Input.text = "IN  -- kPa";
            pad.Setting.text = "-- L/s";
            pad.State.text = "AWAITING HOST";
            pad.State.color = Muted;
            return;
        }
        var value = snapshot.Plumbing[pad.Device];
        pad.Input.text = "IN  " + Number(value.InputPressure, "F1") + " kPa";
        pad.Setting.text = value.InjectorLitresPerSecond.ToString(CultureInfo.InvariantCulture) + " L/s";
        pad.State.text = value.InjectorPulsing ? "INJECTING" : !value.Connected ? "DISCONNECTED"
            : !value.Powered ? "NO POWER" : value.Enabled ? "OPEN" : "CLOSED";
        pad.State.color = value.InjectorPulsing || value.Enabled && value.Powered && value.Connected ? Green
            : !value.Powered || !value.Connected ? Amber : Muted;
    }
}
