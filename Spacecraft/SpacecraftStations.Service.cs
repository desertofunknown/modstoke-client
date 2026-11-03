// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Globalization;
using TMPro;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftStations
{
    private static readonly PanelSurface SystemServiceSurface = new(new(-3.4718596f, .30f, -2.43f), Vector3.right, 420, 160);
    private static readonly Vector3 BusRotorRest = new(-.14f, -.035f, -.010f);
    private static readonly Vector3[] ServicePortPositions = { new(0, -.035f, -.010f), new(.14f, -.035f, -.010f) };
    private static readonly Color ServiceInk = new(.12f, .15f, .18f, 1);
    private readonly SystemServiceReadout[] _systemServiceReadouts = new SystemServiceReadout[2];
    private Station _systemServicePanel = null!;
    private Transform _busRotor = null!;
    private StationButton _busIsolateKey = null!, _busConnectKey = null!;
    private TextMeshProUGUI _busSelection = null!, _busSupply = null!;
    private float _busAngle;
    private bool _busAngleInitialized;

    private static bool IsSystemServiceCommand(string command)
        => command is "bus-isolate" or "bus-connect" or "service-power" or "service-life";

    private string RepairControlPrompt(StationButton button, ClientSpacecraftView view)
        => CanUse(button, view) ? _manualRepairPrompt(button.Action) : BlockedReason(button, view);

    private static SpacecraftControlContact SystemServiceContact(string command, bool isolated)
    {
        var surface = SystemServiceSurface;
        if (command is "bus-isolate" or "bus-connect")
            return KeyControlContact(surface.Position + surface.Rotation * (BusRotorRest + Vector3.back * .0136f),
                surface.Rotation * Quaternion.Euler(0, 0, isolated ? 90 : 0));
        var rest = ServicePortPositions[command == "service-power" ? 0 : 1];
        return KeyControlContact(surface.Position + surface.Rotation * (rest + Vector3.back * .0007f), surface.Rotation);
    }

    private void BuildSystemServicePanel()
    {
        var shell = Lit("System service housing", new(.46f, .49f, .47f, 1));
        shell.SetFloat("_Metallic", .15f); shell.SetFloat("_Smoothness", .42f);
        _panelFinish.Apply(shell, ClientDeviceSurfaceFinish.Surface.Polymer);
        var rotorMaterial = Lit("Main bus isolator", new(.64f, .22f, .035f, 1));
        rotorMaterial.SetFloat("_Metallic", .05f); rotorMaterial.SetFloat("_Smoothness", .38f);
        _panelFinish.Apply(rotorMaterial, ClientDeviceSurfaceFinish.Surface.Polymer);
        var display = Lit("System service display covers", new(.012f, .022f, .025f, 1));
        display.SetFloat("_Metallic", .08f); display.SetFloat("_Smoothness", .64f);
        _panelFinish.Apply(display, ClientDeviceSurfaceFinish.Surface.Polymer);

        var panel = _systemServicePanel = AddPanel("Systems service", SystemServiceSurface, default);
        panel.Canvas.localPosition = new(0, 0, .0015f);
        AddPanelModelPart(panel.Frame, "system-service-shell", shell);
        AddPanelModelPart(panel.Frame, "system-service-trim", _panelMetal);
        AddPanelModelPart(panel.Frame, "system-service-gasket", _panelRubber);
        AddPanelModelPart(panel.Frame, "system-service-glass", display);
        Label(panel.Canvas, "Main bus title", "MAIN BUS", 16, 22, 108, 9, 7, Ink);
        _busSelection = Label(panel.Canvas, "Bus selection", "SYNC", 16, 34, 108, 11, 8, Muted);
        _busSupply = Label(panel.Canvas, "Bus supply", "SYNC", 16, 48, 108, 10, 6, Muted);
        SetPanelPrompt(panel, new Rect(16, 66, 388, 18), 5);
        panel.Prompt.color = ServiceInk;
        PlaceServiceLegend(panel.Prompt, -.0005f);
        _busRotor = AddPanelModelPart(panel.Frame, "system-service-rotor", rotorMaterial).transform;
        _busRotor.localPosition = BusRotorRest;
        var rotorContact = new PanelContact(_busRotor,
            new Bounds(new(0, 0, -.0048f), new(.044f, .044f, .0176f)),
            new Bounds(new(0, 0, -.0132f), new(.009f, .030f, .0008f)));
        _busIsolateKey = AddPanelButton(panel, "bus-isolate", "ISOLATE BUS", new Rect(48, 93, 44, 44), 5, paint: false);
        _busConnectKey = AddPanelButton(panel, "bus-connect", "CONNECT BUS", new Rect(48, 93, 44, 44), 5, paint: false);
        _busIsolateKey.Contact = _busConnectKey.Contact = rotorContact;
        _busIsolateKey.Label.gameObject.SetActive(false);
        _busConnectKey.Label.gameObject.SetActive(false);
        SetPanelKeyVisible(_busConnectKey, false);
        PlaceServiceFocus(_busIsolateKey, -.024f);
        PlaceServiceFocus(_busConnectKey, -.024f);

        for (int index = 0; index < _systemServiceReadouts.Length; index++)
        {
            float x = 156 + index * 140;
            string title = index == 0 ? "POWER" : "LIFE SUPPORT";
            string command = index == 0 ? "service-power" : "service-life";
            Label(panel.Canvas, title + " title", title, x, 22, 108, 9, 7, Ink);
            var condition = Label(panel.Canvas, title + " condition", "CONDITION / SYNC", x, 34, 108, 11, 8, Muted);
            var status = Label(panel.Canvas, title + " status", "SYNC", x, 48, 108, 10, 6, Muted);
            condition.enableWordWrapping = status.enableWordWrapping = false;
            var legend = Label(panel.Canvas, title + " service legend", "SERVICE", x + 34, 140, 40, 10, 5, ServiceInk);
            legend.alignment = TextAlignmentOptions.Center;
            PlaceServiceLegend(legend, -.0004f);
            var button = AddPanelModelPart(panel.Frame, index == 0 ? "system-service-screw" : "system-service-fitting", _panelMetal).transform;
            button.localPosition = ServicePortPositions[index];
            var key = AddPanelButton(panel, command, "SERVICE " + title, new Rect(x + 38, 99, 32, 32), 5, paint: false);
            key.Label.gameObject.SetActive(false);
            key.Contact = new PanelContact(button,
                new Bounds(new(0, 0, .00165f), new(.032f, .032f, .0047f)),
                new Bounds(new(0, 0, -.0003f), new(.030f, .030f, .0008f)));
            _systemServiceReadouts[index] = new SystemServiceReadout(condition, status);
            PlaceServiceFocus(key, -.0109f);
        }
    }

    private void PlaceServiceLegend(TextMeshProUGUI label, float z)
    {
        var position = label.rectTransform.localPosition;
        position.z = (z - _systemServicePanel.Canvas.localPosition.z) * 1000;
        label.rectTransform.localPosition = position;
    }

    private void PlaceServiceFocus(StationButton key, float z)
    {
        var position = key.Key.localPosition;
        position.z = (z - _systemServicePanel.Canvas.localPosition.z) * 1000;
        key.Key.localPosition = position;
    }

    private bool BusRotorSettled(bool isolated)
        => _busAngleInitialized && Mathf.Abs(_busAngle - (isolated ? 90 : 0)) < .5f;

    private bool CanUseSystemService(string command, ClientSpacecraftView view)
    {
        if (view.Boarding || !view.CabinWalking && view.Seat >= 0) return false;
        var systems = view.Flight.Systems;
        return command switch
        {
            "bus-isolate" => !systems.BusIsolated && !view.Flight.Powered && !systems.GeneratorEnabled && BusRotorSettled(false),
            "bus-connect" => systems.BusIsolated && !view.Flight.Powered && !systems.GeneratorEnabled && BusRotorSettled(true),
            _ => false
        };
    }

    private string SystemServiceBlockedReason(string command, ClientSpacecraftView view)
    {
        if (view.Boarding || !view.CabinWalking && view.Seat >= 0) return "Leave the seat to use the systems service panel";
        var systems = view.Flight.Systems;
        if (command is "bus-isolate" or "bus-connect")
        {
            if (view.Flight.Powered) return "Turn the engine off before operating the main bus isolator";
            if (systems.GeneratorEnabled) return "Turn the generator off before operating the main bus isolator";
            if (!BusRotorSettled(systems.BusIsolated)) return "Wait for the isolator to reach its stop";
            return systems.BusIsolated ? "Main bus is isolated" : "Main bus is connected";
        }
        return "Use the matching tool at the service fitting";
    }

    private void UpdateSystemServiceControls(ClientSpacecraftView view)
    {
        var systems = view.Flight.Systems;
        bool ready = view.Ready && view.Available && systems.Ready;
        float target = systems.BusIsolated ? 90 : 0;
        if (!_busAngleInitialized) _busAngle = target;
        else _busAngle = Mathf.MoveTowards(_busAngle, target, Time.unscaledDeltaTime * 360);
        _busAngleInitialized = ready;
        _busRotor.localRotation = Quaternion.Euler(0, 0, _busAngle);
        SetPanelKeyVisible(_busIsolateKey, !systems.BusIsolated);
        SetPanelKeyVisible(_busConnectKey, systems.BusIsolated);
    }

    private void UpdateSystemServiceReadouts(ClientSpacecraftView view)
    {
        var systems = view.Flight.Systems;
        bool ready = view.Ready && view.Available && systems.Ready;
        _busSelection.text = !ready ? "SYNC" : systems.BusIsolated ? "ISOLATED" : "CONNECTED";
        _busSelection.color = !ready ? Muted : systems.BusIsolated ? Amber : PanelCyan;
        _busSupply.text = !ready ? "SYNC" : systems.BusIsolated ? "SOURCE CUT"
            : systems.Electrical.LoadKw > 0 || systems.Electrical.GenerationKw > 0 ? "LIVE" : "NO POWER";
        _busSupply.color = !ready ? Muted : systems.BusIsolated ? Amber : Ink;
        for (int index = 0; index < _systemServiceReadouts.Length; index++)
        {
            var unit = _systemServiceReadouts[index];
            float integrity = index == 0 ? systems.PowerIntegrity01 : systems.LifeSupportIntegrity01;
            bool healthy = integrity >= 1;
            bool isolated = index == 0 ? ClientSpacecraftSystems.PowerServiceIsolated(systems, view.Flight.Powered)
                : ClientSpacecraftSystems.LifeServiceIsolated(systems, view.Flight.Powered);
            bool stocked = systems.CircuitSpares > 0 && (index == 0 || systems.LineSpares > 0);
            unit.Condition.text = ready ? "CONDITION " + Mathf.FloorToInt(integrity * 100).ToString(CultureInfo.InvariantCulture) + "%"
                : "CONDITION / SYNC";
            unit.Condition.color = !ready ? Muted : healthy ? PanelCyan : integrity <= (index == 0 ? .08f : .1f) ? Critical : Amber;
            bool travelling = view.Flight.Travel != SpacecraftTravel.None;
            unit.Status.text = !ready ? "SYNC" : healthy ? "HEALTHY" : travelling ? "TRAVEL"
                : !isolated ? "ISOLATE" : !stocked ? "NO KITS" : "READY";
            unit.Status.color = !ready ? Muted : healthy || !travelling && isolated && stocked ? PanelCyan : Amber;
        }
    }

    private void ResetSystemServiceControls()
    {
        _busAngleInitialized = false;
    }

    private sealed record SystemServiceReadout(TextMeshProUGUI Condition, TextMeshProUGUI Status);
}
