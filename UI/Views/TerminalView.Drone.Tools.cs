// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using TMPro;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientTerminalView
{
    private RectTransform? _droneTools, _immersiveDroneTools, _droneGuide, _immersiveDroneGuide;
    private RectTransform _droneInstrumentBar = null!;
    private TextMeshProUGUI _droneTabletInstruments = null!, _droneVisionAlertText = null!;
    private readonly List<TextMeshProUGUI> _droneToolDetails = new();
    private readonly List<ButtonWidget> _droneCameraButtons = new();
    private readonly List<(ButtonWidget Button, DroneCameraVision Mode)> _droneVisionButtons = new();
    private readonly List<(ButtonWidget Button, string Tab)> _droneToolTabs = new();
    private readonly List<RectTransform> _droneCameraControls = new();
    private readonly List<ButtonWidget> _droneContactButtons = new();
    private readonly List<ButtonWidget> _droneDetailPages = new();
    private readonly List<ButtonWidget> _droneGuideButtons = new();
    private string _droneToolTab = "Camera", _droneCameraCommand = "", _droneInstrumentText = "Camera instruments unavailable";
    private DroneCameraVision _droneCameraVision = DroneCameraVision.Color;
    private string _droneVisionStatus = "Waiting for a live onboard camera feed.";
    private bool _droneGuides, _droneCompactHud = true;
    private int _droneReportPage;
    private string _droneReportContent = "";
    private RectTransform? _droneFullscreenControls, _droneVisionAlert;
    private ButtonWidget? _droneHudButton, _droneFullscreenPilot;
    private TextMeshProUGUI? _droneControlState, _droneExitGuidance;

    private RectTransform DroneHudPanel(Transform parent, string name, float x, float y, float width, float height)
    {
        var panel = Panel(parent, name, new Color(.025f, .035f, .04f, .7f), x, y, width, height);
        panel.sprite = null;
        panel.type = UnityEngine.UI.Image.Type.Simple;
        panel.raycastTarget = false;
        return panel.rectTransform;
    }

    private TextMeshProUGUI DroneHudReadout(Transform parent, string name, string label, float x)
    {
        var heading = ImmersiveText(parent, name + "Label", label, 14, x, 6, 152, 20);
        heading.color = ImmersiveMuted;
        var value = ImmersiveText(parent, name, "—", 23, x, 26, 152, 30, true);
        value.enableWordWrapping = false;
        value.enableAutoSizing = true;
        value.fontSizeMin = 18;
        value.fontSizeMax = 23;
        return value;
    }

    private ButtonWidget DroneHudButton(Transform parent, string name, string label, float x, float y, float width, Action action)
    {
        var button = DroneToolButton(parent, name, label, x, y, width, action);
        Place(button.Image.rectTransform, x, y, width, 38);
        button.Image.sprite = null;
        button.Image.type = UnityEngine.UI.Image.Type.Simple;
        button.Image.color = new Color(.025f, .035f, .04f, .72f);
        Place(button.Label.rectTransform, 12, 0, width - 24, 38);
        button.Label.fontSize = 17;
        return button;
    }

    internal string ConsumeDroneCameraCommand()
    {
        var command = _droneCameraCommand;
        _droneCameraCommand = "";
        return HasDroneCamera ? command : "";
    }

    internal void SetDroneVision(DroneCameraVision mode, string status)
    {
        if (_disposed) return;
        _droneCameraVision = mode;
        _droneVisionStatus = status;
        UpdateDroneTools();
    }

    private void OpenDroneTools(bool immersive)
    {
        if (!DroneVisible) return;
        _droneTools?.gameObject.SetActive(!immersive);
        _immersiveDroneTools?.gameObject.SetActive(immersive);
        UpdateDroneTools();
    }

    private void BuildDroneTools(RectTransform parent, bool immersive)
    {
        var width = immersive ? 760 : 820;
        var image = immersive ? Panel(parent, "DroneCameraTools", Chrome, 40, 60, width, 372)
            : RoundedPanel(parent, "DroneCameraTools", Chrome, 40, 60, width, 372);
        var panel = image.rectTransform;
        if (immersive)
        {
            image.sprite = null;
            image.type = UnityEngine.UI.Image.Type.Simple;
            ImmersiveAnchor(panel, .5f, 0, width, 372);
            panel.pivot = new Vector2(.5f, 1);
            panel.anchoredPosition = new Vector2(0, 186);
        }
        if (immersive) _immersiveDroneTools = panel; else _droneTools = panel;
        panel.GetComponent<UnityEngine.UI.Image>().raycastTarget = true;
        var tabs = new[] { "Camera", "Survey", "Flight", "Checks", "Contact", "Report" };
        for (var i = 0; i < tabs.Length; i++)
        {
            var tab = tabs[i];
            var button = DroneToolButton(panel, tab, tab, 18 + i * (immersive ? 98 : 108), 14, immersive ? 92 : 102, () =>
            { _droneToolTab = tab; _droneReportPage = 0; UpdateDroneTools(); });
            button.Label.fontSize = 17;
            _droneToolTabs.Add((button, tab));
        }
        DroneToolButton(panel, "Close", "Close", immersive ? 614 : 670, 14, immersive ? 128 : 132, () =>
        {
            panel.gameObject.SetActive(false);
            UpdateDroneTools();
        });
        var details = DroneText(panel, "Details", "", 20, ChromeInk, 22, immersive ? 60 : 66, width - 44, 240);
        details.richText = false;
        details.overflowMode = TextOverflowModes.Ellipsis;
        _droneToolDetails.Add(details);
        var controls = Node(panel, "CameraActions");
        Place(controls, 18, immersive ? 164 : 174, width - 36, immersive ? 140 : 176);
        _droneCameraControls.Add(controls);
        var actions = new[] { ("Tilt up 10°", "tilt-up"), ("Tilt down 10°", "tilt-down"),
            ("Zoom −", "zoom-out"), ("Zoom +", "zoom-in"), ("Forward view", "level"),
            ("Ground survey", "survey"), ("Reset aim", "reset") };
        for (var i = 0; i < actions.Length; i++)
        {
            var action = actions[i];
            var button = DroneToolButton(controls, action.Item2, action.Item1,
                i % 4 * (immersive ? 183 : 198), i / 4 * (immersive ? 46 : 58), immersive ? 175 : 190,
                () => { if (HasDroneCamera) _droneCameraCommand = action.Item2; });
            _droneCameraButtons.Add(button);
        }
        _droneGuideButtons.Add(DroneToolButton(controls, "Framing", "Framing: off", immersive ? 549 : 594,
            immersive ? 46 : 58, immersive ? 175 : 190,
            () => { _droneGuides = !_droneGuides; UpdateDroneTools(); }));
        DroneText(controls, "VisionLabel", "VISION", immersive ? 16 : 18, ChromeMuted, 4, immersive ? 100 : 124, 110, 30);
        var visionModes = new[] { (DroneCameraVision.Color, "vision-color"),
            (DroneCameraVision.Night, "vision-night"), (DroneCameraVision.Thermal, "vision-thermal") };
        for (var i = 0; i < visionModes.Length; i++)
        {
            var vision = visionModes[i];
            var button = DroneToolButton(controls, vision.Item2, vision.Item1.ToString(),
                124 + i * (immersive ? 202 : 220), immersive ? 92 : 116, immersive ? 194 : 210,
                () => { if (HasDroneCamera) _droneCameraCommand = vision.Item2; });
            _droneVisionButtons.Add((button, vision.Item1));
        }
        var save = DroneToolButton(panel, "RecoveryPin", "Save recovery pin", 18, 316, 240,
            () => { if (DroneVisible && _droneState.LastContact != null) _command("drone", "navigate-recovery"); });
        _droneContactButtons.Add(save);
        var previous = DroneToolButton(panel, "PreviousReport", "Previous", 18, 316, 170,
            () => { _droneReportPage = Math.Max(0, _droneReportPage - 1); UpdateDroneTools(); });
        var next = DroneToolButton(panel, "NextReport", "Next", 200, 316, 170,
            () => { _droneReportPage++; UpdateDroneTools(); });
        _droneDetailPages.Add(previous); _droneDetailPages.Add(next);
        BuildDroneSurveyActions(panel, immersive);
        if (immersive) LayoutDroneHud();
        panel.gameObject.SetActive(false);
    }

    private ButtonWidget DroneToolButton(Transform parent, string name, string label, float x, float y, float width, Action action)
    {
        var button = Button(parent, name, label, x, y, width, 42,
            () => { if (DroneVisible) action(); });
        button.Image.color = ChromeRaised;
        button.Label.color = ChromeInk;
        button.Label.fontSize = 19;
        if (_immersiveDroneTools != null && button.Image.transform.IsChildOf(_immersiveDroneTools))
        {
            Place(button.Image.rectTransform, x, y, width, 36);
            button.Image.sprite = null;
            button.Image.type = UnityEngine.UI.Image.Type.Simple;
            Place(button.Label.rectTransform, 10, 0, width - 20, 36);
            button.Label.fontSize = 17;
        }
        return button;
    }

    private RectTransform BuildDroneGuide(RectTransform parent)
    {
        var guide = Node(parent, "FramingGuide");
        Stretch(guide, 0, 0, 0, 0);
        for (var i = 1; i <= 2; i++)
        {
            var vertical = Panel(guide, "Vertical" + i, new Color(1, 1, 1, .35f), 0, 0, 1, 1);
            vertical.raycastTarget = false;
            vertical.sprite = null;
            vertical.type = UnityEngine.UI.Image.Type.Simple;
            vertical.rectTransform.anchorMin = new Vector2(i / 3f, 0);
            vertical.rectTransform.anchorMax = new Vector2(i / 3f, 1);
            vertical.rectTransform.offsetMin = new Vector2(-.5f, 0);
            vertical.rectTransform.offsetMax = new Vector2(.5f, 0);
            var horizontal = Panel(guide, "Horizontal" + i, new Color(1, 1, 1, .35f), 0, 0, 1, 1);
            horizontal.raycastTarget = false;
            horizontal.sprite = null;
            horizontal.type = UnityEngine.UI.Image.Type.Simple;
            horizontal.rectTransform.anchorMin = new Vector2(0, i / 3f);
            horizontal.rectTransform.anchorMax = new Vector2(1, i / 3f);
            horizontal.rectTransform.offsetMin = new Vector2(0, -.5f);
            horizontal.rectTransform.offsetMax = new Vector2(0, .5f);
        }
        guide.gameObject.SetActive(false);
        return guide;
    }

    private void UpdateDroneTools()
    {
        var camera = HasDroneCamera;
        _droneGuide?.gameObject.SetActive(camera && _droneGuides);
        _immersiveDroneGuide?.gameObject.SetActive(camera && _droneGuides);
        if (!DroneVisible)
        {
            _droneTools?.gameObject.SetActive(false);
            _immersiveDroneTools?.gameObject.SetActive(false);
            _droneCameraCommand = "";
        }
        foreach (var button in _droneCameraButtons)
        {
            button.Button.interactable = camera;
            button.Label.color = camera ? ChromeInk : ChromeMuted;
        }
        foreach (var entry in _droneToolTabs)
        {
            entry.Button.Image.color = entry.Tab == _droneToolTab ? Mint : ChromeRaised;
            entry.Button.Label.color = entry.Tab == _droneToolTab ? Chrome : ChromeInk;
        }
        foreach (var entry in _droneVisionButtons)
        {
            var selected = camera && entry.Mode == _droneCameraVision;
            entry.Button.Button.interactable = camera;
            entry.Button.Image.color = selected ? Mint : ChromeRaised;
            entry.Button.Label.color = !camera ? ChromeMuted : selected ? Chrome : ChromeInk;
        }
        foreach (var button in _droneGuideButtons) button.Label.text = _droneGuides ? "Framing: on" : "Framing: off";
        UpdateDroneSurveyControls(camera);
        foreach (var controls in _droneCameraControls) controls.gameObject.SetActive(_droneToolTab == "Camera");
        foreach (var button in _droneContactButtons)
        {
            button.Image.gameObject.SetActive(_droneToolTab == "Contact");
            button.Button.interactable = _droneState.LastContact != null;
        }
        var report = (_droneState.Status ?? "").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        if (_droneReportContent != report) { _droneReportContent = report; _droneReportPage = 0; }
        var pages = Math.Max(1, (report.Length + 199) / 200);
        _droneReportPage = Math.Clamp(_droneReportPage, 0, pages - 1);
        for (var i = 0; i < _droneDetailPages.Count; i++)
        {
            var button = _droneDetailPages[i];
            button.Image.gameObject.SetActive(_droneToolTab == "Report");
            button.Button.interactable = i % 2 == 0 ? _droneReportPage > 0 : _droneReportPage + 1 < pages;
        }
        string text;
        if (_droneToolTab == "Survey") text = DroneSurveyDetails();
        else if (_droneToolTab == "Flight") text = _droneSortieReport;
        else if (_droneToolTab == "Checks")
        {
            text = _droneChecklistTitle.text + " · " + _droneChecklistNote.text;
            for (var i = 0; i < _droneCheckLabels.Length; i++) text += "\n" + _droneCheckLabels[i].text + ": " + _droneCheckValues[i].text;
            if (_droneState.Deployed) text += "\nHandling: " + DroneHandlingDetail();
            if (_droneState.Deployed && !_droneState.Link) text = "No radio link. Current checks and aircraft state are unknown.\n\nMove closer to reconnect; use Contact for the recorded position.";
            else if (_droneState.Armed && _droneState.Mode != "Ready") text = "Preflight checks apply before take-off.\nAircraft is armed / in flight.\nLand and wait for motors to stop before handling.\n\n" + _droneSafety.text;
        }
        else if (_droneToolTab == "Contact")
        {
            var contact = _droneState.LastContact;
            text = contact == null ? "No last-known contact recorded for this drone."
                : "LAST-KNOWN CONTACT · " + DroneContactAge(contact.AgeSeconds) + " ago\n"
                    + "X " + DroneNumber(contact.Position.X, " m") + "   Y " + DroneNumber(contact.Position.Y, " m")
                    + "   Z " + DroneNumber(contact.Position.Z, " m")
                    + "\n\nThis is a recorded position, not a live tracker.\nThe aircraft may have moved since contact.\nSave a fixed recovery pin for the map or watch.";
        }
        else if (_droneToolTab == "Report")
        {
            var start = _droneReportPage * 200;
            text = "LAST OPERATION REPORT · " + (_droneReportPage + 1).ToString(CultureInfo.InvariantCulture) + " / " + pages.ToString(CultureInfo.InvariantCulture)
                + "\n\n" + (report.Length == 0 ? "No operation report." : report.Substring(start, Math.Min(200, report.Length - start)));
        }
        else text = camera ? _droneInstrumentText + "\nSensor: " + _droneCameraVision + " · " + _droneVisionStatus
            : _droneState.Deployed ? "Camera controls unavailable.\n" + _droneVisionStatus
                : "Placement preview uses its own fixed camera.\nDeploy the drone to use onboard camera controls.";
        foreach (var detail in _droneToolDetails)
        {
            var immersive = detail.transform.parent == _immersiveDroneTools;
            detail.text = text + (!immersive && camera && _droneToolTab == "Camera"
                ? "\nFullscreen: ↑ / ↓ tilt · Wheel zoom · R reset aim · V vision" : "");
            detail.fontSize = immersive || _droneToolTab is "Camera" or "Checks" or "Flight" or "Survey" ? 18f : 20f;
            detail.rectTransform.sizeDelta = new Vector2(immersive ? 716 : 776, _droneToolTab == "Camera" ? 100 : 240);
        }
        if (_immersiveDroneTools != null)
            _immersiveDroneTools.sizeDelta = new Vector2(760, _droneToolTab == "Camera" ? 310 : 372);
        if (_droneControlState != null)
        {
            _droneControlState.text = !_droneState.Deployed ? "PREVIEW · controls inactive"
                : !_droneState.Link ? "NO LINK · telemetry unavailable"
                : _droneState.Mode.Contains("landing", StringComparison.OrdinalIgnoreCase) ? "LINK LIVE · automatic descent"
                : !_droneState.Piloting ? "LINK LIVE · controls disconnected"
                : !_droneState.Armed ? "LINK LIVE · motors stopped"
                : "LINK LIVE · " + ((DroneSurveyPrecision || UnityEngine.Input.GetKey(KeyCode.LeftShift) || UnityEngine.Input.GetKey(KeyCode.RightShift))
                    ? "PRECISION 35%" : "MANUAL CONTROL");
            _droneControlState.color = _droneState.Deployed && !_droneState.Link ? ImmersiveAlert : ImmersiveMuted;
        }
        if (_droneVisionAlert != null)
        {
            var failure = "";
            if (camera && _droneCameraVision == DroneCameraVision.Color)
            {
                if (_droneVisionStatus.StartsWith("Night camera unavailable;", StringComparison.Ordinal))
                    failure = "Night unavailable; using color.";
                else if (_droneVisionStatus.StartsWith("Thermal camera unavailable;", StringComparison.Ordinal))
                    failure = "Thermal unavailable; using color.";
            }
            _droneVisionAlertText.text = failure;
            _droneVisionAlert.gameObject.SetActive(failure.Length != 0);
        }
        var controlsVisible = !_droneCompactHud && _immersiveDroneTools?.gameObject.activeSelf != true;
        _droneFullscreenControls?.gameObject.SetActive(controlsVisible);
        if (_droneHudButton != null)
        {
            _droneHudButton.Label.text = controlsVisible ? "Hide controls" : "Controls";
            _droneHudButton.Image.color = controlsVisible ? Mint : ChromeRaised;
            _droneHudButton.Label.color = controlsVisible ? Chrome : ChromeInk;
        }
        if (_droneFullscreenPilot != null)
        {
            var action = _droneNextAction == _dronePilot ? _droneLand : _dronePilot;
            _droneFullscreenPilot.Label.text = action.Label.text;
            _droneFullscreenPilot.Button.interactable = action.Button.interactable;
            _droneFullscreenPilot.Label.color = action.Button.interactable ? ImmersiveInk : ImmersiveMuted;
        }
    }

    private string DroneHandlingDetail()
    {
        var state = _droneState;
        if (!state.Link || !float.IsFinite(state.Range) || state.Range < 0 || !float.IsFinite(state.Speed) || state.Speed < 0) return "Telemetry unknown";
        if (state.Armed) return "Wait for motors to stop";
        if (state.Range > 4) return DroneNumber(state.Range - 4, " m closer to handle");
        if (state.Speed > .5f) return "Wait until stopped";
        if (state.Readiness == null) return "Ground check unknown";
        if (!state.Readiness.Grounded) return "Wait until grounded";
        if (state.Mode is "Solar unfolding" or "Solar folding") return "Wait for panels to stop moving";
        return state.Readiness.SolarDeployed || state.Charging || state.Mode is "Solar charging" or "Solar waiting"
            ? "Fold solar panels before pickup / pack swap" : "Nearby, grounded and stopped";
    }
}
