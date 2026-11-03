// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Modstoke.Client;

internal enum ClientFullscreenMode { None, Drone, Arcade }

internal sealed partial class ClientTerminalView
{
    private GameObject? _immersiveRoot;
    private RectTransform _immersiveCanvas = null!, _immersiveDrone = null!, _immersiveArcade = null!;
    private RawImage _immersiveFeed = null!;
    private TextMeshProUGUI _immersiveNoFeed = null!, _immersiveFlightMode = null!, _immersiveFlightStatus = null!;
    private TextMeshProUGUI _immersiveBattery = null!, _immersiveAirframe = null!, _immersiveAltitude = null!;
    private TextMeshProUGUI _immersiveSpeed = null!, _immersiveRange = null!, _immersiveInstruments = null!;
    private TextMeshProUGUI _immersiveLowBattery = null!, _immersiveFlightHints = null!;
    private TextMeshProUGUI _immersiveGameTitle = null!, _immersiveGameStage = null!, _immersiveGameScore = null!;
    private TextMeshProUGUI _immersiveGameHealth = null!, _immersiveGameStatus = null!, _immersiveGameHints = null!;
    private ButtonWidget _immersiveGameContinue = null!, _immersiveGamePause = null!;
    private ButtonWidget _immersiveDroneAction = null!;
    private TextMeshProUGUI _immersiveScoutTarget = null!;
    private Transform? _gamesOriginalParent;
    private int _gamesOriginalSibling;
    private Vector2 _gamesOriginalAnchorMin, _gamesOriginalAnchorMax, _gamesOriginalPivot, _gamesOriginalSize;
    private Vector3 _gamesOriginalPosition, _gamesOriginalScale;
    private Quaternion _gamesOriginalRotation;

    private static readonly Color ImmersiveInk = new(.94f, .98f, 1f, 1f);
    private static readonly Color ImmersiveMuted = new(.65f, .77f, .81f, 1f);
    private static readonly Color ImmersivePanel = new(.025f, .045f, .06f, .94f);
    private static readonly Color ImmersiveAlert = new(1f, .7f, .3f, 1f);

    internal ClientFullscreenMode FullscreenMode { get; private set; }

    internal bool EnterFullscreen(ClientFullscreenMode mode)
    {
        if (!IsAlive || ContentObscured) return false;
        if (mode == ClientFullscreenMode.Drone ? !DroneVisible
            : mode != ClientFullscreenMode.Arcade || _page?.App != ClientTerminalApp.Games
                || _gamesSurface == null || !_gamesSurface || !_gamesSurface.gameObject.activeInHierarchy) return false;
        if (FullscreenMode == mode) return true;
        ExitFullscreen();
        BuildImmersive();
        FullscreenMode = mode;
        _immersiveDrone.gameObject.SetActive(mode == ClientFullscreenMode.Drone);
        _immersiveArcade.gameObject.SetActive(mode == ClientFullscreenMode.Arcade);
        if (mode == ClientFullscreenMode.Arcade)
        {
            var board = _gamesBoard.rectTransform;
            _gamesOriginalParent = board.parent;
            _gamesOriginalSibling = board.GetSiblingIndex();
            _gamesOriginalAnchorMin = board.anchorMin;
            _gamesOriginalAnchorMax = board.anchorMax;
            _gamesOriginalPivot = board.pivot;
            _gamesOriginalSize = board.sizeDelta;
            _gamesOriginalPosition = board.anchoredPosition3D;
            _gamesOriginalRotation = board.localRotation;
            _gamesOriginalScale = board.localScale;
            board.SetParent(_immersiveArcade, false);
            board.SetAsFirstSibling();
            board.anchorMin = board.anchorMax = board.pivot = new Vector2(.5f, .5f);
            board.localRotation = Quaternion.identity;
            board.sizeDelta = new Vector2(ClientTabletGames.Width, ClientTabletGames.Height);
            UpdateImmersiveGames();
        }
        else UpdateImmersiveDrone();
        _immersiveRoot!.SetActive(true);
        Canvas.ForceUpdateCanvases();
        LayoutImmersiveBoard();
        return true;
    }

    internal void ExitFullscreen()
    {
        // Restore the borrowed board before disabling or destroying its temporary parent.
        var originalParent = _gamesOriginalParent;
        _gamesOriginalParent = null;
        FullscreenMode = ClientFullscreenMode.None;
        if (originalParent != null && originalParent && _gamesBoard != null && _gamesBoard)
        {
            var board = _gamesBoard.rectTransform;
            board.SetParent(originalParent, false);
            board.SetSiblingIndex(_gamesOriginalSibling);
            board.anchorMin = _gamesOriginalAnchorMin;
            board.anchorMax = _gamesOriginalAnchorMax;
            board.pivot = _gamesOriginalPivot;
            board.sizeDelta = _gamesOriginalSize;
            board.anchoredPosition3D = _gamesOriginalPosition;
            board.localRotation = _gamesOriginalRotation;
            board.localScale = _gamesOriginalScale;
        }
        if (_immersiveDroneTools != null && _immersiveDroneTools) _immersiveDroneTools.gameObject.SetActive(false);
        if (_immersiveFeed != null && _immersiveFeed) _immersiveFeed.texture = null;
        if (_immersiveRoot != null && _immersiveRoot) _immersiveRoot.SetActive(false);
    }

    internal void DisposeImmersive()
    {
        // Both screens are retiring. Do not reparent the board into a destroyed
        // tablet; let its current owning root destroy it with the other controls.
        _gamesOriginalParent = null;
        FullscreenMode = ClientFullscreenMode.None;
        var root = _immersiveRoot;
        _immersiveRoot = null;
        if (root != null && root)
        {
            try { root.SetActive(false); }
            finally { UnityEngine.Object.Destroy(root); }
        }
    }

    private void BuildImmersive()
    {
        if (_immersiveRoot != null) return;
        _immersiveRoot = new GameObject("ModstokeFullscreen") { layer = _device.layer };
        _immersiveRoot.SetActive(false);
        _immersiveCanvas = _immersiveRoot.AddComponent<RectTransform>();
        var canvas = _immersiveRoot.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 32000;
        var scaler = _immersiveRoot.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1440f, 900f);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
        _immersiveRoot.AddComponent<GraphicRaycaster>();
        var background = Panel(_immersiveCanvas, "Backdrop", new Color(.008f, .014f, .02f, 1f), 0, 0, 1440, 900);
        // Tablet surfaces have feathered edges; the fullscreen backdrop must be opaque to its edges.
        background.sprite = null;
        background.type = Image.Type.Simple;
        Stretch(background.rectTransform, 0, 0, 0, 0);
        background.raycastTarget = true;
        _immersiveDrone = Node(_immersiveCanvas, "DroneFlight");
        Stretch(_immersiveDrone, 0, 0, 0, 0);
        var feed = Node(_immersiveDrone, "CameraFeed");
        Stretch(feed, 0, 0, 0, 0);
        _immersiveFeed = feed.gameObject.AddComponent<RawImage>();
        _immersiveFeed.raycastTarget = false;
        var fit = feed.gameObject.AddComponent<AspectRatioFitter>();
        fit.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
        fit.aspectRatio = 16f / 9f;
        _immersiveDroneGuide = BuildDroneGuide(feed);
        BuildDroneSurveyOverlay(feed, true);
        var unavailable = Node(_immersiveDrone, "Unavailable");
        ImmersiveAnchor(unavailable, .5f, 0, 1440, 140);
        _immersiveNoFeed = ImmersiveText(unavailable, "Message", "", 28, 80, 0, 1280, 140);
        _immersiveNoFeed.alignment = TextAlignmentOptions.Center;
        var flightTop = Node(_immersiveDrone, "FlightHeader");
        ImmersiveAnchor(flightTop, 1f, -24, 1376, 84);
        var mode = DroneHudPanel(flightTop, "FlightMode", 0, 0, 380, 84);
        _immersiveFlightMode = ImmersiveText(mode, "Mode", "DRONE CAMERA", 23, 18, 12, 344, 32, true);
        _droneControlState = ImmersiveText(mode, "ControlLink", "", 16, 18, 48, 344, 24);
        _droneControlState.richText = false;
        _droneControlState.enableWordWrapping = false;
        var instruments = DroneHudPanel(flightTop, "CameraInstruments", 392, 0, 440, 84);
        _immersiveInstruments = ImmersiveText(instruments, "Instruments", "HDG —\nTILT —    ZOOM —", 18, 18, 12, 404, 60);
        _immersiveLowBattery = ImmersiveText(instruments, "BatteryAlert", "LOW BATTERY", 16, 232, 12, 190, 24, true);
        _immersiveLowBattery.alignment = TextAlignmentOptions.TopRight;
        _immersiveLowBattery.color = ImmersiveAlert;
        DroneHudButton(flightTop, "CameraTools", "Camera / survey", 844, 0, 180, () => OpenDroneTools(true));
        _droneHudButton = DroneHudButton(flightTop, "ControlHelp", "Controls", 1036, 0, 146, () =>
        {
            if (_immersiveDroneTools?.gameObject.activeSelf == true)
            {
                _immersiveDroneTools.gameObject.SetActive(false);
                _droneCompactHud = false;
            }
            else _droneCompactHud = !_droneCompactHud;
            UpdateDroneTools();
        });
        DroneHudButton(flightTop, "ExitFullscreen", "Tablet / Esc", 1194, 0, 182,
            () => _command("fullscreen", "exit"));
        DroneHudButton(flightTop, "ReturnToMap", "Map · hold position", 844, 46, 254,
            () => SelectApp(ClientTerminalApp.Navigation));
        BuildDroneSurveyCapture(flightTop, 1110, 46, 266, 38);
        var flightBottom = DroneHudPanel(_immersiveDrone, "FlightTelemetry", 0, 0, 1376, 132);
        ImmersiveAnchor(flightBottom, 0f, 24, 1376, 132);
        _immersiveBattery = DroneHudReadout(flightBottom, "Battery", "BATTERY", 24);
        _immersiveAirframe = DroneHudReadout(flightBottom, "Airframe", "AIRFRAME", 190);
        _immersiveAltitude = DroneHudReadout(flightBottom, "Altitude", "CLEARANCE", 356);
        _immersiveSpeed = DroneHudReadout(flightBottom, "Speed", "SPEED", 522);
        _immersiveRange = DroneHudReadout(flightBottom, "Range", "RANGE", 688);
        _immersiveDroneAction = DroneHudButton(flightBottom, "NextAction", "", 858, 18, 246, () =>
        {
            if (FullscreenMode != ClientFullscreenMode.Drone || !DroneVisible) return;
            var action = _droneNextAction;
            if (action != null && action.Button.interactable) action.Button.onClick.Invoke();
        });
        _immersiveDroneAction.Image.color = Mint;
        _immersiveDroneAction.Label.color = Chrome;
        _droneFullscreenPilot = DroneHudButton(flightBottom, "FlightControl", "Hold position", 1116, 18, 236, () =>
        {
            if (FullscreenMode != ClientFullscreenMode.Drone || !DroneVisible) return;
            var action = _droneNextAction == _dronePilot ? _droneLand : _dronePilot;
            if (action.Button.interactable) action.Button.onClick.Invoke();
        });
        _droneExitGuidance = ImmersiveText(flightBottom, "ExitGuidance",
            "Map / close: hold position · Land: descend\nEsc: tablet with controls still connected", 16, 858, 74, 494, 44);
        _droneExitGuidance.color = ImmersiveMuted;
        _immersiveScoutTarget = ImmersiveText(flightBottom, "ScoutTarget", "", 16, 24, 72, 812, 22);
        _immersiveScoutTarget.enableWordWrapping = false;
        _immersiveScoutTarget.richText = false;
        _immersiveScoutTarget.color = ImmersiveMuted;
        _immersiveFlightStatus = ImmersiveText(flightBottom, "Status", "", 17, 24, 100, 812, 24);
        _immersiveFlightStatus.enableWordWrapping = false;
        _droneFullscreenControls = DroneHudPanel(_immersiveDrone, "FlightControls", 0, 0, 812, 112);
        _droneFullscreenControls.anchorMin = _droneFullscreenControls.anchorMax = _droneFullscreenControls.pivot = new Vector2(.5f, 0);
        _droneFullscreenControls.anchoredPosition = new Vector2(-282, 168);
        var controlsTitle = ImmersiveText(_droneFullscreenControls, "Title", "FLIGHT / CAMERA CONTROLS", 15, 18, 10, 776, 22, true);
        controlsTitle.color = ImmersiveMuted;
        _immersiveFlightHints = ImmersiveText(_droneFullscreenControls, "Controls", "", 18, 18, 36, 776, 68);
        _immersiveFlightHints.richText = false;
        _droneFullscreenControls.gameObject.SetActive(!_droneCompactHud);
        BuildDroneTools(_immersiveDrone, true);

        _immersiveArcade = Node(_immersiveCanvas, "Arcade");
        Stretch(_immersiveArcade, 0, 0, 0, 0);
        var gameTop = ImmersiveBar(_immersiveArcade, "ArcadeHeader", true, 116);
        _immersiveGameTitle = ImmersiveText(gameTop, "Game", "", 25, 32, 12, 500, 36, true);
        _immersiveGameStage = ImmersiveText(gameTop, "Stage", "", 22, 32, 58, 700, 36);
        _immersiveGameScore = ImmersiveText(gameTop, "Score", "", 25, 760, 14, 400, 36, true);
        _immersiveGameHealth = ImmersiveText(gameTop, "Health", "", 22, 760, 60, 400, 32);
        ImmersiveExit(gameTop);
        var gameBottom = ImmersiveBar(_immersiveArcade, "ArcadeControls", false, 138);
        _immersiveGameStatus = ImmersiveText(gameBottom, "Status", "", 21, 32, 12, 1376, 30, true);
        _immersiveGameHints = ImmersiveText(gameBottom, "Controls", "", 20, 32, 54, 780, 68);
        _immersiveGamePause = ImmersiveButton(gameBottom, "Pause", "Pause / P", 832, 64, 168, () => GameCommand("pause"));
        _immersiveGameContinue = ImmersiveButton(gameBottom, "Continue", "Start / Enter", 1012, 64, 192, () => GameCommand("continue"));
        ImmersiveButton(gameBottom, "Restart", "Restart run", 1216, 64, 192, () => GameCommand("restart"));
    }

    private RectTransform ImmersiveBar(Transform parent, string name, bool top, float height)
    {
        var bar = Panel(parent, name, ImmersivePanel, 0, 0, 1440, height).rectTransform;
        ImmersiveAnchor(bar, top ? 1f : 0f, 0, 1440, height);
        return bar;
    }

    private static void ImmersiveAnchor(RectTransform rect, float vertical, float y, float width, float height)
    {
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(.5f, vertical);
        rect.anchoredPosition = new Vector2(0, y);
        rect.sizeDelta = new Vector2(width, height);
    }

    private TextMeshProUGUI ImmersiveText(Transform parent, string name, string value, float size,
        float x, float y, float width, float height, bool bold = false)
        => GameText(parent, name, value, size, ImmersiveInk, x, y, width, height, bold);

    private ButtonWidget ImmersiveButton(Transform parent, string name, string label, float x, float y,
        float width, Action action)
    {
        var button = Button(parent, name, label, x, y, width, 48, action);
        button.Image.color = new Color(.12f, .22f, .27f, 1f);
        button.Label.color = ImmersiveInk;
        button.Label.fontSize = 21;
        return button;
    }

    private void ImmersiveExit(Transform parent)
        => ImmersiveButton(parent, "ExitFullscreen", "Exit / Esc", 1224, 14, 184,
            () => _command("fullscreen", "exit"));

    private void LayoutImmersiveBoard()
    {
        if (FullscreenMode != ClientFullscreenMode.Arcade || _gamesOriginalParent == null) return;
        var size = _immersiveCanvas.rect.size;
        var scale = Math.Min((size.x - 64f) / ClientTabletGames.Width, (size.y - 286f) / ClientTabletGames.Height);
        if (scale <= 0f) return;
        _gamesBoard.rectTransform.localScale = new Vector3(scale, scale, 1f);
        _gamesBoard.rectTransform.anchoredPosition3D = new Vector3(0, 11f, 0);
    }

    private void UpdateImmersiveDrone()
    {
        if (FullscreenMode != ClientFullscreenMode.Drone || _immersiveRoot == null) return;
        _immersiveFeed.texture = _droneFeed.texture;
        _immersiveFeed.enabled = _droneFeed.enabled;
        _immersiveNoFeed.gameObject.SetActive(!_droneFeed.enabled);
        _immersiveNoFeed.text = _droneNoFeed.text;
        _immersiveFlightMode.text = !_droneState.Deployed && _droneFeed.enabled ? "DEPLOYMENT PREVIEW" : _droneMode.text.ToUpperInvariant();
        _immersiveBattery.text = _droneBattery.text;
        _immersiveAirframe.text = _droneHealth.text;
        _immersiveAltitude.text = _droneAltitude.text;
        _immersiveSpeed.text = _droneVelocity.text;
        _immersiveRange.text = _droneRange.text;
        _immersiveFlightStatus.text = _droneSafety.text;
        _immersiveDroneAction.Button.interactable = _droneNextAction?.Button.interactable == true;
        _immersiveDroneAction.Label.text = _droneNextAction?.Label.text ?? "";
        _immersiveScoutTarget.text = _droneDestination.text;
        _immersiveFlightHints.text = !_droneState.Deployed ? "Aim the placement preview on the tablet.\nDeploy before adjusting the onboard camera."
            : !_droneState.Link || !_droneFeed.enabled ? "No live camera. Move closer to restore the radio link.\nCamera and flight controls are unavailable."
            : _droneState.Mode.Contains("landing", StringComparison.OrdinalIgnoreCase)
            ? "Use Resume control to cancel a manual landing. Safety landings take priority.\n↑ / ↓ camera tilt · Wheel zoom · R reset aim · V vision"
            : _droneState.Piloting && _droneState.Armed
            ? "WASD tilt · Q / E yaw · Space / Left Ctrl climb / descend\nShift precision · ↑ / ↓ tilt · Wheel zoom · R reset aim · V vision"
            : "Connect or resume controls to fly. Use Land to descend.\n↑ / ↓ camera tilt · Wheel zoom · R reset aim · V vision";
        var telemetryKnown = _droneState.Ready && (_droneState.Deployed ? _droneState.Link : _droneState.InventoryDrone);
        DroneUpdateImmersiveSafety(telemetryKnown);
        UpdateDroneTools();
    }

    internal void SetFlightInstruments(float heading, float pitch, float zoom)
    {
        if (_disposed) return;
        var available = HasDroneCamera;
        var normalizedHeading = (heading % 360f + 360f) % 360f;
        var bearing = available && float.IsFinite(heading)
            ? (((int)MathF.Round(normalizedHeading)) % 360).ToString("000", CultureInfo.InvariantCulture) + "° "
                + (((int)((normalizedHeading + 22.5f) / 45f) % 8) switch
                { 0 => "N", 1 => "NE", 2 => "E", 3 => "SE", 4 => "S", 5 => "SW", 6 => "W", _ => "NW" })
            : "—";
        var tilt = available && float.IsFinite(pitch) ? Math.Abs(pitch).ToString("0", CultureInfo.InvariantCulture) + "° "
            + (pitch > .5f ? "down" : pitch < -.5f ? "up" : "level") : "—";
        var magnification = available && float.IsFinite(zoom) ? zoom.ToString("0.0", CultureInfo.InvariantCulture) + "x" : "—";
        _droneInstrumentText = $"HDG {bearing} · TILT {tilt} · ZOOM {magnification}";
        var vision = available ? _droneCameraVision.ToString().ToUpperInvariant() : "—";
        if (_droneTabletInstruments != null) _droneTabletInstruments.text = _droneInstrumentText + " · " + vision;
        if (_immersiveRoot != null) _immersiveInstruments.text = $"HDG {bearing}\nTILT {tilt}    ZOOM {magnification}    {vision}";
        UpdateDroneTools();
    }

    private void UpdateImmersiveGames()
    {
        if (FullscreenMode != ClientFullscreenMode.Arcade || _immersiveRoot == null) return;
        _immersiveGameTitle.text = _gamesTitle.text;
        _immersiveGameStage.text = _gamesStage.text;
        _immersiveGameScore.text = _gamesScore.text;
        _immersiveGameHealth.text = _gamesHealth.text;
        _immersiveGameStatus.text = _gamesStatus.text;
        _immersiveGameHints.text = _gamesControlsText + "\nEnter  Start / continue  ·  P  Pause  ·  Esc  Return to tablet";
        _immersiveGamePause.Button.interactable = _gamesPause.Button.interactable;
        _immersiveGameContinue.Button.interactable = _gamesContinue.Button.interactable;
        _immersiveGameContinue.Label.text = _gamesContinue.Label.text;
        LayoutImmersiveBoard();
    }
}
