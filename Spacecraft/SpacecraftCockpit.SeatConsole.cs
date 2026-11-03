// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftCockpit
{
    private void BuildSeatConsoleControls(SeatDisplay display)
    {
        foreach (var button in SpacecraftSeatConsole.Buttons)
        {
            var contact = _body!.SeatConsoleContact(display.Seat, button.Kind)
                ?? throw new InvalidOperationException("The chair console contact is missing: " + button.Title);
            var frame = new GameObject(button.Title + " key contact") { layer = display.Root.layer }.transform;
            frame.SetParent(display.Root.transform, false);
            frame.SetPositionAndRotation(contact.position, contact.rotation);
            var size = SpacecraftSeatConsole.HalfSize;
            var width = size.x * 2000;
            var height = size.y * 2000;
            var face = WorldCanvas(frame, button.Title + " key display", Vector3.back * .00035f,
                Quaternion.identity, width, height);
            var label = Text(face, "Label", 1, 2, width - 2, height - 7, 5, Ink);
            label.text = button.Title;
            label.alignment = TextAlignmentOptions.Center;
            var lamp = Node(face, "State", 7, height - 4, width - 14, 1.1f).gameObject.AddComponent<Image>();
            lamp.material = WorldGraphic();
            lamp.raycastTarget = false;
            var control = new PanelControl(display.Seat, button.Kind, button.Command, frame, size, null, label,
                ControlHighlight(frame, button.Title, width, height, .0005f), button.Translation,
                button.Rotation, button.Boost) { FollowFrame = contact, LampGraphic = lamp };
            _controls.Add(control);
            if (button.Kind == SpacecraftConsoleControl.StationAssist) display.DockAssist = control;
            if (button.Kind == SpacecraftConsoleControl.StationDock) display.DockLatch = control;
        }
    }

    private void UpdateSeatConsoleControls()
    {
        foreach (var control in _controls)
        {
            if (control.FollowFrame is not { } contact) continue;
            if (!contact) { control.Frame.gameObject.SetActive(false); continue; }
            control.Frame.SetPositionAndRotation(contact.position, contact.rotation);
        }
    }
}
