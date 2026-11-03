// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using TMPro;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftStations
{
    private static readonly PanelSurface CoolantRepairSurface = new(new(2.432f, -.460f, 2.294966f), Vector3.left, 160, 56);
    private static readonly Vector3 CoolantRepairButtonRest = new(.052f, 0, -.010f);
    private Station _coolantRepairPanel = null!;
    private StationButton _coolantRepairKey = null!;
    private Transform _coolantRepairButton = null!;

    private static SpacecraftControlContact CoolantRepairContact()
        => KeyControlContact(CoolantRepairSurface.Position + CoolantRepairSurface.Rotation * (CoolantRepairButtonRest + Vector3.back * .000525f),
            CoolantRepairSurface.Rotation);

    private void BuildCoolantRepairPanel()
    {
        _coolantRepairPanel = AddPanel("Coolant seal", CoolantRepairSurface, new Rect(17, 29, 87, 10), 6);
        var panel = _coolantRepairPanel;
        // The label sits within the authored pocket; the key focus follows the separate button face.
        panel.Canvas.localPosition = new Vector3(0, 0, .0015f);
        panel.Readout.enableWordWrapping = false;
        Label(panel.Canvas, "Coolant seal title", "COOLANT SEAL", 17, 12, 87, 10, 7, Ink)
            .alignment = TextAlignmentOptions.TopLeft;
        AddPanelModelPart(panel.Frame, "coolant-seal-shell", _panelPaint);
        AddPanelModelPart(panel.Frame, "coolant-seal-trim", _panelMetal);
        AddPanelModelPart(panel.Frame, "coolant-seal-gasket", _panelRubber);
        _coolantRepairButton = AddPanelModelPart(panel.Frame, "system-service-fitting", _panelMetal).transform;
        _coolantRepairButton.localPosition = CoolantRepairButtonRest;
        _coolantRepairButton.localScale = Vector3.one * .75f;
        _coolantRepairKey = AddPanelButton(panel, "line-10-repair", "REPAIR COOLANT SEAL", new Rect(120, 16, 24, 24), 6, paint: false);
        _coolantRepairKey.Contact = new PanelContact(_coolantRepairButton,
            new Bounds(new Vector3(0, 0, .00165f), new Vector3(.032f, .032f, .0047f)),
            new Bounds(new Vector3(0, 0, -.0003f), new Vector3(.030f, .030f, .0008f)));
        _coolantRepairKey.Label.gameObject.SetActive(false);
        PositionCoolantRepairFocus();
    }

    private void UpdateCoolantRepairReadout(ClientSpacecraftView view)
    {
        var systems = view.Flight.Systems;
        var ready = view.Ready && view.Available && systems.Ready;
        var fault = PanelLineFaulted(systems, SpacecraftValve.CoolantFeed);
        var isolated = ready && ClientSpacecraftSystems.CoolantRepairIsolated(systems, view.Flight.Powered);
        _coolantRepairPanel.Readout.text = !ready ? "SYNC" : systems.CoolantLeak
            ? fault ? "LEAK + FAULT" : isolated ? "LEAK / ISOLATED" : "LEAK"
            : fault ? "FAULT" : "SEALED";
        _coolantRepairPanel.Readout.color = !ready ? Muted : systems.CoolantLeak && (!isolated || fault) ? Critical
            : fault || systems.CoolantLeak ? Amber : PanelCyan;
    }

    private void PositionCoolantRepairFocus()
    {
        var position = _coolantRepairKey.Key.localPosition;
        position.z = (_coolantRepairButton.localPosition.z - .0007f - _coolantRepairPanel.Canvas.localPosition.z) * 1000;
        _coolantRepairKey.Key.localPosition = position;
    }
}
