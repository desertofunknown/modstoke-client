// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Globalization;
using TMPro;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftStations
{
    private static readonly PanelSurface WarpServiceSurface = new(new(2.46678f, .340f, 3.580f), Vector3.left, 280, 100);
    private static readonly Vector3 WarpDischargeRotorRest = new(-.092f, 0, -.010f);
    private static readonly Vector3 WarpServiceFittingRest = new(.104f, -.008f, -.010f);
    private Station _warpServicePanel = null!;
    private Transform _warpDischargeRotor = null!;
    private StationButton _warpDischargeStartKey = null!, _warpDischargeStopKey = null!;
    private TextMeshProUGUI _warpServiceCharge = null!, _warpServiceCondition = null!, _warpServiceStatus = null!;
    private float _warpDischargeAngle;
    private bool _warpDischargeAngleInitialized;

    private static bool IsWarpServiceCommand(string command)
        => command is "service-warp" or "warp-discharge-start" or "warp-discharge-stop";

    private static SpacecraftControlContact WarpServiceContact(string command, bool discharge)
    {
        var surface = WarpServiceSurface;
        if (command == "service-warp")
            return KeyControlContact(surface.Position + surface.Rotation * (WarpServiceFittingRest + Vector3.back * .0007f),
                surface.Rotation);
        return KeyControlContact(surface.Position + surface.Rotation * (WarpDischargeRotorRest + Vector3.back * .0145f),
            surface.Rotation * Quaternion.Euler(0, 0, discharge ? 45 : -45));
    }

    private void BuildWarpServicePanel()
    {
        var shell = Finish("Warp service housing", new(.46f, .49f, .47f, 1), .15f, .42f, ClientDeviceSurfaceFinish.Surface.Polymer);
        var trim = Finish("Warp service hardware", new(.32f, .35f, .37f, 1), .8f, .46f, ClientDeviceSurfaceFinish.Surface.Metal);
        var gasket = Finish("Warp service seals", new(.018f, .020f, .022f, 1), 0, .22f, ClientDeviceSurfaceFinish.Surface.Rubber);
        var glass = Finish("Warp service display cover", new(.012f, .022f, .025f, 1), .08f, .64f, ClientDeviceSurfaceFinish.Surface.Polymer);
        var rotor = Finish("Warp discharge selector", new(.64f, .22f, .035f, 1), .05f, .38f, ClientDeviceSurfaceFinish.Surface.Polymer);
        var panel = _warpServicePanel = AddPanel("Warp service", WarpServiceSurface, default);
        panel.Canvas.localPosition = new(0, 0, .0015f);
        AddPanelModelPart(panel.Frame, "warp-service-shell", shell);
        AddPanelModelPart(panel.Frame, "warp-service-trim", trim);
        AddPanelModelPart(panel.Frame, "warp-service-gasket", gasket);
        AddPanelModelPart(panel.Frame, "warp-service-glass", glass);
        _warpDischargeRotor = AddPanelModelPart(panel.Frame, "warp-service-rotor", rotor).transform;
        _warpDischargeRotor.localPosition = WarpDischargeRotorRest;
        var rotorContact = new PanelContact(_warpDischargeRotor,
            new Bounds(new(0, 0, -.00525f), new(.042f, .042f, .0185f)),
            new Bounds(new(0, 0, -.0145f), new(.008f, .026f, .0008f)));
        _warpDischargeStartKey = AddPanelButton(panel, "warp-discharge-start", "SELECT BLEED", new Rect(27, 29, 42, 42), 5, paint: false);
        _warpDischargeStopKey = AddPanelButton(panel, "warp-discharge-stop", "SELECT RUN", new Rect(27, 29, 42, 42), 5, paint: false);
        _warpDischargeStartKey.Contact = _warpDischargeStopKey.Contact = rotorContact;
        _warpDischargeStartKey.Label.gameObject.SetActive(false);
        _warpDischargeStopKey.Label.gameObject.SetActive(false);
        SetWarpServiceDepth(_warpDischargeStartKey.Key, -.025f);
        SetWarpServiceDepth(_warpDischargeStopKey.Key, -.025f);
        SetPanelKeyVisible(_warpDischargeStopKey, false);

        var fitting = AddPanelModelPart(panel.Frame, "system-service-fitting", trim).transform;
        fitting.localPosition = WarpServiceFittingRest;
        var service = AddPanelButton(panel, "service-warp", "SERVICE WARP DRIVE", new Rect(228, 42, 32, 32), 5, paint: false);
        service.Label.gameObject.SetActive(false);
        service.Contact = new PanelContact(fitting,
            new Bounds(new(0, 0, .00165f), new(.026846787f, .031f, .0047f)),
            new Bounds(new(0, 0, -.0003f), new(.0268f, .031f, .0008f)));
        SetWarpServiceDepth(service.Key, -.0109f);

        Label(panel.Canvas, "Warp service title", "WARP SERVICE", 96, 20, 108, 7, 5.5f, Ink);
        _warpServiceCharge = Label(panel.Canvas, "Warp charge", "CHARGE --", 96, 32, 108, 11, 7.5f, Muted);
        _warpServiceCondition = Label(panel.Canvas, "Warp condition", "CONDITION --", 96, 47, 108, 10, 6.5f, Muted);
        _warpServiceStatus = Label(panel.Canvas, "Warp service status", "SYNC", 96, 63, 108, 9, 5.5f, Muted);
        _warpServiceCharge.enableWordWrapping = _warpServiceCondition.enableWordWrapping = _warpServiceStatus.enableWordWrapping = false;
        var bleed = Label(panel.Canvas, "Bleed position", "BLEED", 16, 8, 31, 6, 4, ServiceInk);
        var run = Label(panel.Canvas, "Run position", "RUN", 52, 8, 27, 6, 4, ServiceInk);
        var access = Label(panel.Canvas, "Warp service access", "DRIVE ACCESS", 222, 76, 43, 7, 4, ServiceInk);
        SetWarpServiceDepth(bleed.rectTransform, -.0011f);
        SetWarpServiceDepth(run.rectTransform, -.0011f);
        SetWarpServiceDepth(access.rectTransform, -.0011f);
        SetPanelPrompt(panel, new Rect(4, 104, 272, 24), 5);

        Material Finish(string name, Color color, float metallic, float smoothness, ClientDeviceSurfaceFinish.Surface surface)
        {
            var material = Lit(name, color);
            material.SetFloat("_Metallic", metallic);
            material.SetFloat("_Smoothness", smoothness);
            _panelFinish.Apply(material, surface);
            return material;
        }
    }

    private void SetWarpServiceDepth(Transform target, float z)
    {
        var position = target.localPosition;
        position.z = (z - _warpServicePanel.Canvas.localPosition.z) * 1000;
        target.localPosition = position;
    }

    private bool WarpDischargeSettled(bool discharge)
        => _warpDischargeAngleInitialized && Mathf.Abs(_warpDischargeAngle - (discharge ? 45 : -45)) < .5f;

    private bool CanUseWarpService(string command, ClientSpacecraftView view)
    {
        if (view.Boarding || !view.CabinWalking && view.Seat >= 0) return false;
        var systems = view.Flight.Systems;
        if (!WarpDischargeSettled(systems.WarpDischargeEnabled)) return false;
        return command == "warp-discharge-start"
            ? !systems.WarpDischargeEnabled && ClientSpacecraftSystems.WarpDischargeIsolated(systems, out _)
            : command == "warp-discharge-stop" && systems.WarpDischargeEnabled;
    }

    private string WarpServiceBlockedReason(string command, ClientSpacecraftView view)
    {
        if (view.Boarding || !view.CabinWalking && view.Seat >= 0) return "Leave the seat to use the warp service panel";
        var systems = view.Flight.Systems;
        if (!WarpDischargeSettled(systems.WarpDischargeEnabled)) return "Wait for the selector to reach its stop";
        if (command == "warp-discharge-start" && !ClientSpacecraftSystems.WarpDischargeIsolated(systems, out var reason)) return reason;
        return systems.WarpDischargeEnabled ? "Warp bleed is selected" : "Warp selector is in RUN";
    }

    private void UpdateWarpServiceControls(ClientSpacecraftView view)
    {
        var systems = view.Flight.Systems;
        bool ready = view.Ready && view.Available && systems.Ready;
        float target = systems.WarpDischargeEnabled ? 45 : -45;
        _warpDischargeAngle = !_warpDischargeAngleInitialized ? target
            : Mathf.MoveTowards(_warpDischargeAngle, target, Time.unscaledDeltaTime * 360);
        _warpDischargeAngleInitialized = ready;
        _warpDischargeRotor.localRotation = Quaternion.Euler(0, 0, _warpDischargeAngle);
        SetPanelKeyVisible(_warpDischargeStartKey, !systems.WarpDischargeEnabled);
        SetPanelKeyVisible(_warpDischargeStopKey, systems.WarpDischargeEnabled);
    }

    private void UpdateWarpServiceReadout(ClientSpacecraftView view)
    {
        var systems = view.Flight.Systems;
        bool ready = view.Ready && view.Available && systems.Ready;
        bool charged = systems.WarpCharge01 > 0;
        bool healthy = systems.WarpIntegrity01 >= 1;
        bool travelling = view.Flight.Travel != SpacecraftTravel.None;
        bool isolated = ClientSpacecraftSystems.WarpServiceIsolated(systems, view.Flight.Powered);
        bool stocked = systems.CircuitSpares > 0 && systems.LineSpares > 0;
        // A rounded-down zero must not imply that a charged drive is safe to service.
        _warpServiceCharge.text = ready ? "CHARGE " + Mathf.CeilToInt(systems.WarpCharge01 * 100).ToString(CultureInfo.InvariantCulture) + "%"
            : "CHARGE --";
        _warpServiceCharge.color = !ready ? Muted : charged ? Amber : PanelCyan;
        _warpServiceCondition.text = ready ? "CONDITION " + Mathf.FloorToInt(systems.WarpIntegrity01 * 100).ToString(CultureInfo.InvariantCulture) + "%"
            : "CONDITION --";
        _warpServiceCondition.color = !ready ? Muted : healthy ? PanelCyan : systems.WarpIntegrity01 <= .35f ? Critical : Amber;
        _warpServiceStatus.text = !ready ? "SYNC" : travelling ? "TRAVEL"
            : systems.WarpDischargeEnabled && charged ? "BLEEDING"
            : healthy ? systems.WarpDischargeEnabled ? "DISCHARGED" : "HEALTHY"
            : !isolated ? "ISOLATE" : !stocked ? "NO KITS" : "READY";
        _warpServiceStatus.color = !ready ? Muted : !travelling && (healthy && !systems.WarpDischargeEnabled || isolated && stocked)
            ? PanelCyan : Amber;
    }
}
