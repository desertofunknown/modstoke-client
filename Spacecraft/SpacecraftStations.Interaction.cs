// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftStations
{
    private StationButton? _pressed;
    private SpacecraftControlContact _pressContact;
    private string _pressCrewRequest = "";
    private IntPtr _pressActor;
    private int _pressSeat;
    private Station? _controlFeedbackStation;
    private string _controlFeedback = "";
    private float _controlFeedbackUntil;

    private bool CanReachControl(StationButton button, ClientSpacecraftView view)
        => button.Action == "load-spares" || ClientSpacecraftSystems.TryManualRepair(button.Action, out _, out _)
            || _rider.CanReachControl(_body, ButtonContact(button, view));

    private void UpdateControlPress(ClientSpacecraftView view, bool acceptsInput)
    {
        if (!acceptsInput)
        {
            CancelControlPress();
            return;
        }
        if (_pressed is { } button)
        {
            if (!ReferenceEquals(button, _hovered) || !CanUse(button, view)
                || LocalPlayer.Transform.Pointer != _pressActor || view.Seat != _pressSeat
                || !string.Equals(button.CrewRequestKey, _pressCrewRequest, StringComparison.Ordinal))
            {
                CancelControlPress();
                return;
            }
            var contact = ButtonContact(button, view);
            if ((contact.Position - _pressContact.Position).sqrMagnitude > .000025f
                || Quaternion.Angle(contact.Rotation, _pressContact.Rotation) > 1)
            {
                CancelControlPress();
                return;
            }
            if (!_rider.ControlHandActive)
            {
                SetControlFeedback(button.Station, _rider.ControlPressFailure);
                _pressed = null;
                return;
            }
            if (!_rider.ControlPressReached(_body)) return;
            _pressed = null;
            _rider.EndControlPress(_body);
            Activate(button, view);
            return;
        }
        if (_hovered != null && ClientSpacecraftSystems.TryManualRepair(_hovered.Action, out _, out _))
        {
            if (CanUse(_hovered, view) && Input.GetMouseButton(0))
            {
                if (Input.GetMouseButtonDown(0))
                {
                    _commandStation = _hovered.Station;
                    _feedbackBefore = view.Feedback;
                    _feedbackUntil = Time.unscaledTime + 12;
                }
                _manualRepair(_hovered.Action);
            }
            return;
        }
        if (_hovered == null || !Input.GetKeyDown(KeyCode.E) || !CanUse(_hovered, view)) return;
        // Depositing a held kit frees the control hand only after the host accepts its custody transfer.
        if (_hovered.Action == "load-spares") { Activate(_hovered, view); return; }
        var target = ButtonContact(_hovered, view);
        if (!_rider.BeginControlPress(_body, target, out var reason))
        {
            SetControlFeedback(_hovered.Station, reason);
            return;
        }
        _pressed = _hovered;
        _pressContact = target;
        _pressCrewRequest = _hovered.CrewRequestKey;
        _pressActor = LocalPlayer.Transform.Pointer;
        _pressSeat = view.Seat;
        _controlFeedbackUntil = 0;
    }

    private SpacecraftControlContact ButtonContact(StationButton button, ClientSpacecraftView view)
    {
        var frame = _body.Root.transform;
        if (button.Contact != null || button.Action is "apu-guard-open" or "apu-guard-close")
            return LocalControlContact(button.Action, frame.InverseTransformPoint(LocalPlayer.Transform.position), view.Flight.Systems);
        var station = button.Station;
        var point = station.Canvas.TransformPoint(new Vector3(button.Bounds.center.x - station.Width / 2,
            station.Height / 2 - button.Bounds.center.y, 0));
        return KeyControlContact(frame.InverseTransformPoint(point), Quaternion.Inverse(frame.rotation) * station.Frame.rotation,
            indexDown: station.Kind == "Navigation");
    }

    private void CancelControlPress()
    {
        bool ownsPress = _pressed != null;
        _pressed = null;
        _pressCrewRequest = "";
        _pressActor = IntPtr.Zero;
        if (ownsPress) _rider.EndControlPress(_body, immediate: true);
    }

    private void SetControlFeedback(Station station, string message)
    {
        _controlFeedbackStation = station;
        _controlFeedback = message;
        _controlFeedbackUntil = Time.unscaledTime + 3;
    }

    private void ShowControlFeedback()
    {
        if (_controlFeedbackStation == null || _controlFeedback.Length == 0 || Time.unscaledTime >= _controlFeedbackUntil) return;
        var station = _controlFeedbackStation;
        if (station == _apuPanel) station = _apuStatusPanel;
        else if (station == _coolantRepairPanel) station = _valveFeedbackPanel;
        else if (station == _sparesPanel) station = _valveFeedbackPanel;
        else if (station == _engineServicePanel) station = _valveFeedbackPanel;
        else foreach (var valve in _valves)
            if (valve.Station == station) { station = _valveFeedbackPanel; break; }
        if (IsSealantPanel(station)) { station.Prompt.fontSize = 4.5f; station.Prompt.color = Ink; }
        station.Prompt.text = _controlFeedback;
    }
}
