// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftCockpit
{
    private PanelControl? _flightControl;

    private void AddFlightControl(SeatDisplay display)
    {
        var frame = new GameObject("Pilot stick contact").transform;
        frame.gameObject.layer = display.Root.layer;
        frame.SetParent(display.Root.transform, false);
        var drag = new GameObject("Pilot stick drag plane").transform;
        drag.gameObject.layer = display.Root.layer;
        drag.SetParent(display.Root.transform, false);
        var size = ClientSpacecraftBody.FlightControlHalfSize;
        _flightControl = new PanelControl(display.Seat, SpacecraftConsoleControl.Flight, "stick", frame, size,
            null, null, ControlHighlight(frame, "Pilot stick", size.x * 2000, size.y * 2000),
            Vector3.zero, Vector3.zero, false) { DragFrame = drag };
        _controls.Add(_flightControl);
        UpdateFlightControl();
    }

    private void UpdateFlightControl()
    {
        if (_flightControl == null || _body?.Ready != true) return;
        var pick = _body.FlightControlPick;
        var drag = _body.FlightControlDrag;
        _flightControl.Frame.SetPositionAndRotation(pick.position, pick.rotation);
        // A moving handle must not feed its deflection back into mouse displacement.
        _flightControl.DragFrame!.SetPositionAndRotation(drag.position, drag.rotation);
    }
}
