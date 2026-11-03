// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Globalization;
using TMPro;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftStations
{
    private static readonly PanelSurface SparesSurface = new(new(2.47282f, .110f, 2.400f), Vector3.left, 220, 44);
    private static readonly Vector3 SparesButtonRest = new(.079f, 0, -.010f);
    private Station _sparesPanel = null!;
    private StationButton _sparesKey = null!;
    private Transform _sparesButton = null!;
    private TextMeshProUGUI _sparesLine = null!, _sparesCircuit = null!, _sparesHull = null!;
    private float _sparesPressedUntil;

    private static SpacecraftControlContact SparesContact()
        => KeyControlContact(SparesSurface.Position + SparesSurface.Rotation * SparesButtonRest, SparesSurface.Rotation);

    private void BuildSparesPanel()
    {
        _sparesPanel = AddPanel("Repair stores", SparesSurface, new Rect(0, 0, 0, 0), 7);
        var panel = _sparesPanel;
        panel.Canvas.localPosition = new(0, 0, .0015f);
        Label(panel.Canvas, "Repair stores title", "REPAIR STORES", 18, 7, 138, 7, 5.5f, Ink);
        _sparesLine = Label(panel.Canvas, "Line stock", "LINE / SYNC", 18, 14, 138, 7, 5.5f, Muted);
        _sparesCircuit = Label(panel.Canvas, "Circuit stock", "CIRCUIT / SYNC", 18, 21, 138, 7, 5.5f, Muted);
        _sparesHull = Label(panel.Canvas, "Hull plate stock", "PLATES / SYNC", 18, 28, 138, 7, 5.5f, Muted);
        _sparesLine.enableWordWrapping = _sparesCircuit.enableWordWrapping = _sparesHull.enableWordWrapping = false;
        _sparesLine.alignment = _sparesCircuit.alignment = _sparesHull.alignment = TextAlignmentOptions.TopLeft;
        AddPanelModelPart(panel.Frame, "spares-store-shell", _panelPaint);
        AddPanelModelPart(panel.Frame, "spares-store-trim", _panelMetal);
        AddPanelModelPart(panel.Frame, "spares-store-gasket", _panelRubber);
        _sparesButton = AddPanelModelPart(panel.Frame, "spares-store-button", _panelRubber).transform;
        _sparesButton.localPosition = SparesButtonRest;
        _sparesKey = AddPanelButton(panel, "load-spares", "LOAD", new Rect(177, 10, 24, 24), 5, paint: false);
        _sparesKey.Contact = new PanelContact(_sparesButton,
            new Bounds(new(0, 0, .0019f), new(.024f, .024f, .0038f)),
            new Bounds(new(0, 0, .0004f), new(.022f, .022f, .0008f)));
        PositionSparesFocus();
    }

    private void UpdateSparesReadout(ClientSpacecraftView view)
    {
        var systems = view.Flight.Systems;
        bool ready = view.Ready && view.Available && systems.Ready;
        _sparesLine.text = ready ? "LINE " + systems.LineSpares.ToString(CultureInfo.InvariantCulture) + " / 24" : "LINE / SYNC";
        _sparesCircuit.text = ready ? "CIRCUIT " + systems.CircuitSpares.ToString(CultureInfo.InvariantCulture) + " / 12" : "CIRCUIT / SYNC";
        _sparesHull.text = ready ? "PLATES " + systems.HullPlates.ToString(CultureInfo.InvariantCulture) + " / 12" : "PLATES / SYNC";
        _sparesLine.color = !ready ? Muted : systems.LineSpares == 0 ? Amber : Ink;
        _sparesCircuit.color = !ready ? Muted : systems.CircuitSpares == 0 ? Amber : Ink;
        _sparesHull.color = !ready ? Muted : systems.HullPlates == 0 ? Amber : Ink;
    }

    private void UpdateSparesButton(ClientSpacecraftView view, ReadOnlySpan<SpacecraftCrewInteraction> interactions)
    {
        bool ready = view.Ready && view.Available && view.Flight.Systems.Ready;
        if (!ready) _sparesPressedUntil = 0;
        float press = PanelKeyPress(ready, _sparesPressedUntil, SparesContact(), interactions);
        _sparesButton.localPosition = SparesButtonRest + Vector3.forward * (.002f * press);
        PositionSparesFocus();
    }

    private void PositionSparesFocus()
    {
        var position = _sparesKey.Key.localPosition;
        position.z = (_sparesButton.localPosition.z - .0002f - _sparesPanel.Canvas.localPosition.z) * 1000;
        _sparesKey.Key.localPosition = position;
    }
}
