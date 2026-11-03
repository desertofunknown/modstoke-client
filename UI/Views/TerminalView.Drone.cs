// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Modstoke.Client;

internal sealed partial class ClientTerminalView
{
    private RectTransform _droneSurface = null!;
    private RawImage _droneFeed = null!;
    private TextMeshProUGUI _droneNoFeed = null!, _droneLink = null!, _droneStatus = null!;
    private TextMeshProUGUI _droneMode = null!, _droneBattery = null!, _droneHealth = null!;
    private TextMeshProUGUI _droneAltitude = null!, _droneVelocity = null!, _droneRange = null!;
    private TextMeshProUGUI _dronePacks = null!, _droneInventory = null!, _droneControls = null!;
    private ButtonWidget _droneDeploy = null!, _droneArm = null!, _dronePilot = null!, _droneLand = null!;
    private ButtonWidget _droneRecover = null!, _droneReplacePack = null!, _droneSolar = null!;
    private ButtonWidget _droneMap = null!;
    private ButtonWidget _droneFullscreen = null!;
    private TextMeshProUGUI _droneSafety = null!;
    private ButtonWidget? _droneNextAction;
    private TextMeshProUGUI _droneDestination = null!;
    private string _droneDestinationName = "";
    private ClientDroneView _droneState = ClientDroneView.Unavailable("Drone services are unavailable.");

    internal bool DroneVisible => !_disposed && !ContentObscured && _page?.App == ClientTerminalApp.Drone
        && _droneSurface != null && _droneSurface.gameObject.activeInHierarchy;

    internal bool CanFullscreenDrone => DroneVisible && _droneState.Ready
        && (_droneState.Deployed ? _droneState.Link : _droneState.InventoryDrone)
        && _droneFeed != null && _droneFeed.enabled && _droneFeed.texture != null && _droneFeed.texture;

    private bool HasDroneCamera => CanFullscreenDrone && _droneState.Deployed;

    private void BuildDrone()
    {
        _droneSurface = FieldSurface("Drone");
        var camera = Panel(_droneSurface, "CameraViewport", Chrome, 0f, 0f, 900f, 506.25f).rectTransform;
        camera.gameObject.AddComponent<RectMask2D>();
        var feed = Node(camera, "OnboardCamera");
        Place(feed, 0f, 0f, 900f, 506.25f);
        _droneFeed = feed.gameObject.AddComponent<RawImage>();
        _droneFeed.raycastTarget = false;
        _droneFeed.color = Color.white;
        // The service owns both preview and onboard textures.
        _droneFeed.enabled = false;
        _droneNoFeed = DroneText(camera, "Unavailable", "Camera unavailable", 30f, ChromeInk,
            48f, 188f, 804f, 112f);
        _droneNoFeed.alignment = TextAlignmentOptions.Center;
        Panel(camera, "CameraHeader", Chrome, 0f, 0f, 900f, 44f);
        _droneCameraLabel = DroneText(camera, "CameraLabel", "ONBOARD CAMERA", 20f, ChromeMuted, 18f, 8f, 300f, 30f);
        _droneLink = DroneText(camera, "ControlLink", "NO LINK", 18f, ChromeMuted, 322f, 8f, 328f, 30f);
        _droneLink.alignment = TextAlignmentOptions.TopRight;
        _droneFullscreen = Button(camera, "Fullscreen", "Fullscreen camera", 668f, 5f, 214f, 34f,
            () => { if (CanFullscreenDrone) _command("fullscreen", "drone"); });
        _droneFullscreen.Image.color = ChromeRaised;
        _droneFullscreen.Label.color = ChromeInk;
        _droneFullscreen.Label.fontSize = 18f;
        Panel(camera, "CameraControls", Chrome, 0f, 442f, 900f, 64.25f);
        _droneControls = DroneText(camera, "FlightControls", "", 19f,
            ChromeInk, 18f, 448f, 864f, 52f);
        _droneControls.richText = false;
        BuildDroneReadiness(camera);
        var status = RoundedPanel(_droneSurface, "DroneStatus", Surface, 0f, 522f, 900f, 78f);
        _droneStatus = DroneText(status.transform, "Status", "", 18f, Ink, 18f, 4f, 640f, 46f);
        _droneStatus.richText = false;
        _droneNoFeed.richText = false;
        _droneDestination = DroneText(status.transform, "ScoutTarget", "", 18f, Muted, 18f, 50f, 640f, 24f);
        _droneDestination.richText = false;
        _droneDestination.enableWordWrapping = false;
        _droneMap = Button(status.transform, "ReturnToMap", "Return to map", 676f, 22f, 206f, 34f,
            () => { if (DroneVisible) SelectApp(ClientTerminalApp.Navigation); });
        _droneMap.Label.fontSize = 18f;
        Place(_droneMap.Image.rectTransform, 676f, 42f, 206f, 32f);
        var tools = Button(status.transform, "CameraTools", "Camera / details", 676f, 4f, 206f, 32f,
            () => OpenDroneTools(false));
        tools.Label.fontSize = 18f;
        _droneGuide = BuildDroneGuide(_droneFeed.rectTransform);
        BuildDroneSurveyOverlay(_droneFeed.rectTransform, false);
        _droneInstrumentBar = Panel(camera, "InstrumentBar", Chrome, 0f, 406f, 900f, 34f).rectTransform;
        _droneTabletInstruments = DroneText(camera, "CameraInstruments", "", 17f, ChromeInk, 18f, 410f, 864f, 28f);
        _droneTabletInstruments.raycastTarget = false;
        BuildDroneTools(camera, false);

        var telemetry = RoundedPanel(_droneSurface, "FlightTelemetry", Surface, 920f, 0f, 408f, 304f).rectTransform;
        _droneMode = DroneText(telemetry, "Mode", "Offline", 30f, Ink, 18f, 10f, 372f, 40f, true);
        _droneSafety = DroneText(telemetry, "FlightSafety", "", 18f, Muted, 18f, 52f, 372f, 28f);
        _droneBattery = DroneReadout(telemetry, "Battery", "BATTERY", 18f, 88f);
        _droneHealth = DroneReadout(telemetry, "Health", "AIRFRAME", 214f, 88f);
        _droneAltitude = DroneReadout(telemetry, "Altitude", "CLEARANCE", 18f, 158f);
        _droneVelocity = DroneReadout(telemetry, "Velocity", "VELOCITY", 214f, 158f);
        _droneRange = DroneReadout(telemetry, "Range", "DISTANCE", 18f, 228f);
        _dronePacks = DroneReadout(telemetry, "Packs", "SPARE PACKS", 214f, 228f);

        _droneDeploy = DroneButton("Deploy", "Deploy", 920f, 320f, 196f, "deploy");
        _droneArm = DroneButton("Arm", "Arm motors", 1132f, 320f, 196f, "arm");
        _dronePilot = DroneButton("Pilot", "Connect controls", 920f, 382f, 196f, "pilot");
        _dronePilot.Label.fontSize = 18f;
        _droneLand = DroneButton("Land", "Land", 1132f, 382f, 196f, "land");
        _droneRecover = DroneButton("Recover", "Pick up", 920f, 444f, 196f, "recover");
        _droneReplacePack = DroneButton("ReplacePack", "Replace pack", 1132f, 444f, 196f, "replace-pack");
        _droneSolar = DroneButton("SolarCharge", "Start solar charging", 920f, 506f, 408f, "solar-charge");
        _droneInventory = DroneText(_droneSurface, "Inventory", "", 17f, Muted, 920f, 560f, 408f, 40f);
        SetDroneDestination(_droneDestinationName);
        SetDrone(_droneState, null);
    }

    internal void SetDroneDestination(string name)
    {
        if (_disposed) return;
        _droneDestinationName = name?.Trim() ?? "";
        if (_droneDestination == null) return;
        RefreshDroneSurveyDestination();
    }

    private TextMeshProUGUI DroneReadout(Transform parent, string name, string label, float x, float y)
    {
        DroneText(parent, name + "Label", label, 18f, Muted, x, y, 176f, 26f);
        return DroneText(parent, name, "—", 28f, Ink, x, y + 27f, 176f, 36f, true);
    }

    private TextMeshProUGUI DroneText(Transform parent, string name, string value, float size, Color color,
        float x, float y, float width, float height, bool bold = false)
    {
        var text = Text(parent, name, value, size, color, x, y, width, height, bold);
        text.overflowMode = TextOverflowModes.Ellipsis;
        return text;
    }

    private ButtonWidget DroneButton(string name, string label, float x, float y, float width, string action)
        => Button(_droneSurface, name, label, x, y, width, 50f, () =>
        {
            if (!DroneVisible) return;
            _command("drone", action == "arm" && _droneState.Armed ? "takeoff" : action);
        });

    internal void SetDrone(ClientDroneView state, Texture? feed, ClientDronePlacementView? placement = null)
    {
        if (_disposed || _droneSurface == null) return;
        _droneState = state;
        var available = state.Ready && (state.InventoryDrone || state.Deployed);
        var linked = state.Ready && state.Deployed && state.Link;
        // A disconnected aircraft's cached values are not current flight telemetry.
        var telemetryKnown = available && (!state.Deployed || linked);
        var preview = state.Ready && state.InventoryDrone && !state.Deployed && placement?.Active == true;
        var cameraAvailable = (linked || preview) && feed != null && feed;
        _droneCameraLabel.text = preview ? "DEPLOYMENT PREVIEW" : "ONBOARD CAMERA";
        _droneFeed.texture = cameraAvailable ? feed : null;
        _droneFeed.enabled = cameraAvailable;
        _droneFullscreen.Button.interactable = cameraAvailable;
        _droneFullscreen.Label.text = cameraAvailable ? preview ? "Fullscreen preview" : "Fullscreen camera" : "No camera feed";
        _droneFullscreen.Label.color = cameraAvailable ? ChromeInk : ChromeMuted;
        _droneNoFeed.gameObject.SetActive(!cameraAvailable);
        _droneNoFeed.text = !state.Ready ? "Drone service unavailable\n" + state.Status
            : !state.Deployed ? state.InventoryDrone ? preview ? "Preview camera unavailable" : "Drone stowed\nAim at clear, level ground."
                : "No drone available\nA drone is required before deployment."
            : !linked ? "Control link unavailable\nNo live camera feed."
            : "Camera unavailable\nWaiting for the onboard feed.";
        _droneLink.text = !state.Ready ? "SERVICE UNAVAILABLE" : !state.Deployed ? "NOT DEPLOYED"
            : !linked ? "LINK UNAVAILABLE" : state.Piloting ? "CONTROLS CONNECTED" : "RADIO LINK AVAILABLE";
        _droneLink.color = linked ? ChromeMint : ChromeMuted;
        _droneMode.text = !state.Ready ? "Offline" : state.Deployed && !linked ? "Link lost" : state.Mode;
        _droneMode.richText = false;
        _droneStatus.text = "Last report: " + state.Status;
        _droneStatus.color = state.Ready ? Ink : Amber;
        _droneBattery.text = telemetryKnown ? DronePercent(state.Charge) : "—";
        _droneBattery.color = telemetryKnown && state.Charge <= .2f ? Amber : Ink;
        _droneHealth.text = telemetryKnown ? DronePercent(state.Health) : "—";
        _droneHealth.color = telemetryKnown && state.Health < .35f ? Amber : Ink;
        // Drone v1 has no availability flag; a legacy zero cannot establish measured clearance.
        var clearanceKnown = state.SurfaceClearanceAvailable ?? state.Altitude > 0f;
        _droneAltitude.text = linked ? clearanceKnown ? DroneNumber(state.Altitude, " m") : "Unknown" : "—";
        _droneVelocity.text = linked ? DroneNumber(state.Speed, " m/s") : "—";
        _droneRange.text = linked ? DroneNumber(state.Range, " m") : "—";
        _dronePacks.text = state.Ready ? Math.Max(0, state.SparePacks).ToString(CultureInfo.InvariantCulture) : "—";
        _droneInventory.text = !state.Ready ? "Inventory unavailable" : state.Deployed && !linked ? "Drone deployed · state unknown"
            : state.Charging ? "Solar charging active"
            : state.Deployed ? "Drone deployed" : state.InventoryDrone ? "Drone in inventory" : "No drone in inventory";
        var solarMoving = state.Mode is "Solar unfolding" or "Solar folding";
        var solar = state.Readiness?.SolarDeployed == true || state.Charging || state.Mode is "Solar charging" or "Solar waiting" or "Solar unfolding";
        if (linked && solar && !state.Charging) _droneInventory.text = "Solar panels unfolded";
        if (linked && solarMoving) _droneInventory.text = state.Mode == "Solar unfolding" ? "Unfolding solar panels" : "Folding solar panels";
        if (linked && (solar || solarMoving) && state.SolarStatus.Length != 0)
            _droneInventory.text = state.SolarStatus;
        else if (linked && !solar && !solarMoving && state.LoadedTerrainHeight is { } terrainHeight
            && float.IsFinite(terrainHeight) && state.LoadedTerrainObservedAt > 0)
        {
            var age = Math.Max(0, (Environment.TickCount64 - state.LoadedTerrainObservedAt) / 1000d);
            _droneInventory.text = "Terrain elevation " + DroneNumber(terrainHeight, " m")
                + " · " + age.ToString("0.0", CultureInfo.InvariantCulture) + "s ago";
        }
        var landing = state.Mode.Contains("landing", StringComparison.OrdinalIgnoreCase);
        var manualLanding = state.Mode == "Landing";
        var nearby = float.IsFinite(state.Range) && state.Range >= 0f && state.Range <= 4f;
        var stopped = float.IsFinite(state.Speed) && state.Speed >= 0f && state.Speed <= .5f;
        var grounded = state.Readiness?.Grounded != false;
        var upright = state.Readiness?.Upright != false;
        var handling = linked && !state.Armed && nearby && stopped && grounded && !solarMoving;
        SetDroneButton(_droneDeploy, state.Ready && state.InventoryDrone && !state.Deployed
            && (placement?.Active != true || placement.HasSurface && placement.Clear));
        _droneArm.Label.text = state.Armed ? "Take off" : "Arm motors";
        SetDroneButton(_droneArm, linked && state.Piloting && nearby && !landing && !solar && !solarMoving
            && grounded && upright
            && (state.Armed ? state.Mode == "Ready" : float.IsFinite(state.Health) && float.IsFinite(state.Charge)
                && state.Health >= .35f && state.Charge >= .15f));
        _dronePilot.Label.text = manualLanding || state.Armed && !state.Piloting ? "Resume control"
            : state.Piloting ? state.Armed ? "Hold position" : "Disconnect controls" : "Connect controls";
        SetDroneButton(_dronePilot, linked && float.IsFinite(state.Health) && float.IsFinite(state.Charge)
            && state.Health > 0f && state.Charge > 0f && !solar && !solarMoving
            && (!landing || manualLanding && state.Health >= .35f && state.Charge >= .12f));
        _droneLand.Label.text = landing ? "Landing…" : "Land";
        SetDroneButton(_droneLand, linked && state.Armed && !landing);
        SetDroneButton(_droneRecover, handling && !solar);
        SetDroneButton(_droneReplacePack, handling && !solar && state.SparePacks > 0);
        _droneSolar.Label.text = solar ? "Fold solar panels" : "Start solar charging";
        SetDroneButton(_droneSolar, handling);
        SetDroneButton(_droneMap, true);
        DroneUpdateSafetyAndPacks(state, telemetryKnown, linked, landing);
        UpdateDroneReadiness(state, placement, linked, solar, nearby, stopped);
        _droneNextAction = _droneArm;
        string guidance;
        if (!state.Ready || !available)
        { guidance = "Drone unavailable. Return to the map to continue on foot."; _droneNextAction = _droneMap; }
        else if (!state.Deployed)
        { guidance = placement?.Active == true && (!placement.HasSurface || !placement.Clear)
                ? "Reposition the preview: " + placement.Status
                : "Next: deploy on clear, level ground. The host rechecks placement.";
            _droneNextAction = _droneDeploy; }
        else if (!linked)
        { guidance = state.LastContact != null ? "Link unavailable. Save the last-known contact as a recovery pin."
                : "Link unavailable. No recorded contact; move closer to reconnect.";
            _droneNextAction = state.LastContact != null ? _droneRecoveryPin : _droneMap; }
        else if (landing)
        { guidance = manualLanding && _dronePilot.Button.interactable
                ? "Landing in progress. Resume control to cancel the descent."
                : "Automatic landing. Wait for motors to stop before approaching.";
            _droneNextAction = manualLanding && _dronePilot.Button.interactable ? _dronePilot : _droneLand; }
        else if (solarMoving)
            guidance = "Wait for the solar panels to finish moving before handling or arming.";
        else if (solar)
        { guidance = nearby ? "Next: fold the stopped drone's solar panels." : DroneApproachGuidance(state.Range, "fold the solar panels");
            _droneNextAction = _droneSolar; }
        else if (state.Armed && state.Piloting && state.Mode == "Ready")
        { guidance = nearby ? "Next: take off nearby, or hold Space to climb."
                : "Take-off button needs a position within 4 m. Connected controls: hold Space to climb.";
            _droneNextAction = _droneArm; }
        else if (state.Armed && !state.Piloting)
        { guidance = "Holding position. Resume control to fly, or choose Land."; _droneNextAction = _dronePilot; }
        else if (state.Armed)
        { guidance = "WASD  Tilt · Q / E  Yaw · Space / Left Ctrl  Climb / descend · Land to finish."; _droneNextAction = _droneLand; }
        else if (state.Mode == "Landed" && !state.Piloting)
        { guidance = nearby ? "Next: pick up once grounded with motors stopped."
                : DroneApproachGuidance(state.Range, "pick up the stopped drone"); _droneNextAction = _droneRecover; }
        else if (!float.IsFinite(state.Health) || !float.IsFinite(state.Charge))
            guidance = "Flight checks unknown. Wait for battery and airframe telemetry.";
        else if (state.Health < .35f || state.Charge < .15f)
        { var spareCharge = state.PackAInstalled ? state.PackBCharge : state.PackBInstalled ? state.PackACharge : float.NaN;
            guidance = state.Health < .35f ? "Airframe below 35%. Flight is blocked; recover the stopped drone."
                : "Battery below 15%. Swap to a charged spare or use solar charging after landing.";
            _droneNextAction = state.Health < .35f ? _droneRecover
                : state.SparePacks > 0 && float.IsFinite(spareCharge) && spareCharge >= .15f ? _droneReplacePack : _droneSolar; }
        else if (!grounded || !upright)
            guidance = "Flight check blocked. The drone must be grounded and upright before arming.";
        else if (!state.Piloting)
        { guidance = "Next: connect controls to prepare for flight."; _droneNextAction = _dronePilot; }
        else if (!nearby)
            guidance = DroneApproachGuidance(state.Range, "arm the motors");
        else
        { guidance = state.Readiness == null ? "Ground checks unknown. Request arming nearby; the host validates readiness."
                : "Next: arm motors within 4 m. The host rechecks flight readiness.";
            _droneNextAction = _droneArm; }
        _droneControls.text = guidance + "\n" + (state.Piloting && state.Armed && !landing
            ? (DroneSurveyPrecision ? "Survey precision latched · " : "Shift: precision · ") + "Fullscreen: wheel zoom, ↑ / ↓ camera tilt, R reset."
            : "Map or close: hold position. Use Land to descend. Safety landings take priority.");
        if (_droneNextAction?.Button.interactable == true)
        {
            _droneNextAction.Image.color = Mint;
            _droneNextAction.Label.color = Chrome;
        }
        UpdateImmersiveDrone();
        UpdateDroneTools();
    }

    private void SetDroneButton(ButtonWidget button, bool enabled)
    {
        button.Button.interactable = enabled;
        button.Image.color = Raised;
        button.Label.color = enabled ? Ink : Dim;
    }

    private void DroneUpdateSafetyAndPacks(ClientDroneView state, bool telemetryKnown, bool linked, bool landing)
    {
        var warning = false;
        string safety;
        if (!state.Ready) safety = "Flight controller unavailable";
        else if (state.Deployed && !linked) { safety = "Telemetry unavailable · move closer"; warning = true; }
        else if (!state.InventoryDrone && !state.Deployed) safety = "A drone is required";
        else if (landing) { safety = "Landing · wait for motors to stop"; warning = true; }
        else if (!float.IsFinite(state.Health) || !float.IsFinite(state.Charge))
        { safety = "Flight telemetry unknown"; warning = true; }
        else if (state.Health < .35f)
        { safety = state.Armed ? "Airframe damage · land now" : "Airframe below 35% · flight blocked"; warning = true; }
        else if (state.Charge < .15f)
        { safety = state.Armed ? "Critical battery · land now" : "Low battery · swap or solar charge"; warning = true; }
        else if (state.Charge <= .2f)
        { safety = state.Armed ? "Low battery · plan to land" : "Low battery · charge before flight"; warning = true; }
        else safety = state.Armed ? "MOTORS ARMED · keep clear" : "Motors stopped";
        _droneSafety.text = safety;
        _droneSafety.color = warning ? Amber : Muted;

        _droneReplacePack.Label.fontSize = 18f;
        _droneReplacePack.Label.text = "Replace pack";
        if (!state.Ready || (!state.InventoryDrone && !state.Deployed)) return;
        var knownPacks = state.PackAId.Length != 0 && state.PackBId.Length != 0
            && state.PackAInstalled != state.PackBInstalled;
        if (!knownPacks) { _droneInventory.text += "\nPack details unavailable"; return; }
        var packA = telemetryKnown ? DronePercent(state.PackACharge) : "—";
        var packB = telemetryKnown ? DronePercent(state.PackBCharge) : "—";
        _droneInventory.text += "\nA " + packA + (state.PackAInstalled ? " fitted" : " spare")
            + " · B " + packB + (state.PackBInstalled ? " fitted" : " spare");
        _droneReplacePack.Label.text = state.PackAInstalled ? "Swap to B · " + packB : "Swap to A · " + packA;
    }

    private void LayoutDroneHud()
    {
        // Keep the shared fullscreen shell; arrange only the drone-owned instruments.
        var mode = _immersiveFlightMode.transform.parent.GetComponent<RectTransform>();
        var header = mode.parent.GetComponent<RectTransform>();
        header.anchorMin = header.anchorMax = header.pivot = Vector2.one;
        header.anchoredPosition = new Vector2(-32, -24);
        mode.SetParent(_immersiveDrone, false);
        Place(mode, 32, 24, 440, 64);
        Place(_immersiveFlightMode.rectTransform, 12, 6, 416, 28);
        _immersiveFlightMode.fontSize = 21;
        _immersiveFlightMode.enableWordWrapping = false;
        _immersiveFlightMode.enableAutoSizing = true;
        _immersiveFlightMode.fontSizeMin = 17;
        _immersiveFlightMode.fontSizeMax = 21;
        Place(_droneControlState!.rectTransform, 12, 36, 416, 22);

        var instruments = _immersiveInstruments.transform.parent.GetComponent<RectTransform>();
        instruments.SetParent(_immersiveDrone, false);
        Place(instruments, 32, 94, 440, 56);
        Place(_immersiveInstruments.rectTransform, 12, 6, 416, 44);
        _immersiveInstruments.fontSize = 17;
        _immersiveInstruments.enableWordWrapping = false;
        Place(_immersiveLowBattery.rectTransform, 242, 6, 186, 22);

        _droneVisionAlert = DroneHudPanel(_immersiveDrone, "CameraStatus", 32, 156, 440, 32);
        _droneVisionAlertText = ImmersiveText(_droneVisionAlert, "Status", "", 17, 12, 4, 416, 24);
        _droneVisionAlertText.color = ImmersiveAlert;
        _droneVisionAlertText.enableWordWrapping = false;
        _droneVisionAlert.gameObject.SetActive(false);

        var telemetry = _immersiveBattery.transform.parent.GetComponent<RectTransform>();
        telemetry.GetComponent<UnityEngine.UI.Image>().color = Color.clear;
        telemetry.anchorMin = Vector2.zero;
        telemetry.anchorMax = new Vector2(1, 0);
        telemetry.pivot = Vector2.zero;
        telemetry.offsetMin = new Vector2(32, 24);
        telemetry.offsetMax = new Vector2(-32, 136);
        var readouts = DroneHudPanel(telemetry, "TelemetryBacking", 8, 0, 840, 58);
        readouts.SetAsFirstSibling();
        var warnings = DroneHudPanel(telemetry, "FlightStatusBacking", 8, 64, 840, 48);
        warnings.SetAsFirstSibling();
        Place(_immersiveFlightStatus.rectTransform, 24, 66, 792, 22);
        Place(_immersiveScoutTarget.rectTransform, 24, 90, 792, 22);
        _immersiveDroneAction.Image.rectTransform.anchorMin = _immersiveDroneAction.Image.rectTransform.anchorMax = Vector2.one;
        _immersiveDroneAction.Image.rectTransform.anchoredPosition = new Vector2(-518, 0);
        _droneFullscreenPilot!.Image.rectTransform.anchorMin = _droneFullscreenPilot.Image.rectTransform.anchorMax = Vector2.one;
        _droneFullscreenPilot.Image.rectTransform.anchoredPosition = new Vector2(-260, 0);

        _droneFullscreenControls!.sizeDelta = new Vector2(812, 152);
        _droneFullscreenControls.anchorMin = _droneFullscreenControls.anchorMax = _droneFullscreenControls.pivot = Vector2.zero;
        _droneFullscreenControls.anchoredPosition = new Vector2(40, 150);
        _droneExitGuidance!.transform.SetParent(_droneFullscreenControls, false);
        Place(_droneExitGuidance.rectTransform, 18, 104, 776, 44);
    }

    private void DroneUpdateImmersiveSafety(bool telemetryKnown)
    {
        if (FullscreenMode != ClientFullscreenMode.Drone || _immersiveRoot == null) return;
        var lowBattery = telemetryKnown && float.IsFinite(_droneState.Charge) && _droneState.Charge <= .2f;
        _immersiveBattery.color = lowBattery ? ImmersiveAlert : ImmersiveInk;
        _immersiveAirframe.color = telemetryKnown && float.IsFinite(_droneState.Health) && _droneState.Health < .35f
            ? ImmersiveAlert : ImmersiveInk;
        _immersiveLowBattery.gameObject.SetActive(lowBattery);
        _immersiveFlightStatus.richText = false;
        _immersiveFlightStatus.text = _droneSafety.text;
        _immersiveFlightStatus.color = _droneSafety.color == Amber ? ImmersiveAlert : ImmersiveMuted;
        _immersiveFlightMode.richText = false;
        _immersiveNoFeed.richText = false;
        _immersiveDroneAction.Image.color = _droneNextAction?.Button.interactable == true ? Mint : ChromeRaised;
        _immersiveDroneAction.Label.color = _droneNextAction?.Button.interactable == true ? Chrome : ChromeMuted;
    }

    private static string DroneApproachGuidance(float range, string action) => !float.IsFinite(range) || range < 0f
        ? "Distance unknown. Wait for telemetry before you " + action + "."
        : "Move " + DroneNumber(Math.Max(0f, range - 4f), " m") + " closer (within 4 m) to " + action + ".";

    private static string DronePercent(float value) => float.IsFinite(value)
        ? (Math.Clamp(value, 0f, 1f) * 100f).ToString("0", CultureInfo.InvariantCulture) + "%" : "—";

    private static string DroneNumber(float value, string unit) => float.IsFinite(value)
        ? value.ToString("0.0", CultureInfo.InvariantCulture) + unit : "—";
}
