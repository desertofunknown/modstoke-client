// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Globalization;
using TMPro;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftStations
{
    private static readonly PanelSurface EngineServiceSurface = new(new(2.45282f, .108f, 2.900f), Vector3.left, 220, 40);
    private static readonly Vector3 EngineServiceButtonRest = new(.079f, 0, -.010f);
    private Station _engineServicePanel = null!;
    private StationButton _engineServiceKey = null!;
    private Transform _engineServiceButton = null!;
    private TextMeshProUGUI _engineServiceCondition = null!, _engineServiceStatus = null!;

    private static SpacecraftControlContact EngineServiceContact()
        => KeyControlContact(EngineServiceSurface.Position + EngineServiceSurface.Rotation * (EngineServiceButtonRest + Vector3.back * .000525f),
            EngineServiceSurface.Rotation);

    private void BuildEngineServicePanel()
    {
        _engineServicePanel = AddPanel("Engine service", EngineServiceSurface, new Rect(0, 0, 0, 0), 7);
        var panel = _engineServicePanel;
        panel.Canvas.localPosition = new(0, 0, .0015f);
        Label(panel.Canvas, "Engine service title", "ENGINE SERVICE", 18, 5, 138, 7, 5.5f, Ink);
        _engineServiceCondition = Label(panel.Canvas, "Engine condition", "CONDITION / SYNC", 18, 14, 138, 9, 7, Muted);
        _engineServiceStatus = Label(panel.Canvas, "Engine service status", "SYNC", 18, 25, 138, 7, 5, Muted);
        _engineServiceCondition.enableWordWrapping = _engineServiceStatus.enableWordWrapping = false;
        _engineServiceCondition.alignment = _engineServiceStatus.alignment = TextAlignmentOptions.TopLeft;
        AddPanelModelPart(panel.Frame, "engine-service-shell", _panelPaint);
        AddPanelModelPart(panel.Frame, "engine-service-trim", _panelMetal);
        AddPanelModelPart(panel.Frame, "engine-service-gasket", _panelRubber);
        _engineServiceButton = AddPanelModelPart(panel.Frame, "system-service-fitting", _panelMetal).transform;
        _engineServiceButton.localPosition = EngineServiceButtonRest;
        _engineServiceButton.localScale = Vector3.one * .75f;
        _engineServiceKey = AddPanelButton(panel, "service-engine", "SERVICE", new Rect(177, 8, 24, 24), 3.5f, paint: false);
        _engineServiceKey.Contact = new PanelContact(_engineServiceButton,
            new Bounds(new(0, 0, .00165f), new(.032f, .032f, .0047f)),
            new Bounds(new(0, 0, -.0003f), new(.030f, .030f, .0008f)));
        _engineServiceKey.Label.gameObject.SetActive(false);
        PositionEngineServiceFocus();
    }

    private void UpdateEngineServiceReadout(ClientSpacecraftView view)
    {
        var systems = view.Flight.Systems;
        bool ready = view.Ready && view.Available && systems.Ready;
        bool healthy = systems.EngineIntegrity01 >= 1;
        bool isolated = ready && ClientSpacecraftSystems.EngineServiceIsolated(systems, view.Flight.Powered);
        bool stocked = systems.LineSpares > 0 && systems.CircuitSpares > 0;
        _engineServiceCondition.text = ready ? "CONDITION "
            + Mathf.FloorToInt(systems.EngineIntegrity01 * 100).ToString(CultureInfo.InvariantCulture) + "%" : "CONDITION / SYNC";
        _engineServiceCondition.color = !ready ? Muted : healthy ? PanelCyan
            : systems.EngineIntegrity01 <= .15f ? Critical : Amber;
        _engineServiceStatus.text = !ready ? "SYNC" : healthy ? "HEALTHY"
            : !isolated ? "ISOLATE" : !stocked ? "NO KITS" : "ISOLATED";
        _engineServiceStatus.color = !ready ? Muted : healthy || isolated && stocked ? PanelCyan : Amber;
    }

    private void PositionEngineServiceFocus()
    {
        var position = _engineServiceKey.Key.localPosition;
        position.z = (_engineServiceButton.localPosition.z - .0007f - _engineServicePanel.Canvas.localPosition.z) * 1000;
        _engineServiceKey.Key.localPosition = position;
    }
}
