// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UI;

namespace Modstoke.Client;

internal sealed partial class ClientTerminalView
{
    private ClientDroneSurveyObservation _droneSurvey = ClientDroneSurveyObservation.Unavailable("A live onboard camera is required.");
    private ClientFieldPlace? _droneSurveyDestination;
    private readonly List<ButtonWidget> _droneSurveyCaptureButtons = new(), _droneSurveyPanelButtons = new(), _dronePrecisionButtons = new();
    private readonly List<ButtonWidget> _droneTerrainButtons = new();
    private readonly List<RectTransform> _droneSurveyReticles = new();
    private readonly List<Image> _droneSurveyReticleLines = new();
    private bool _droneSurveyPrecision, _droneTerrainDetails;
    private string _droneSortieReport = "No flight observed this session.", _droneSurveyFeedback = "";

    internal bool DroneSurveyPrecision => DroneVisible && _droneSurveyPrecision;

    private bool HasDroneSurvey => HasDroneCamera && _droneSurvey.Live && _droneSurvey.DroneId == _droneState.DroneId;

    internal void ResetDroneSurvey()
    {
        _droneSurveyPrecision = false;
        _droneTerrainDetails = false;
        _droneSurvey = ClientDroneSurveyObservation.Unavailable("A live onboard camera is required.");
        _droneSurveyDestination = null;
        _droneSortieReport = "No flight observed this session.";
        _droneSurveyFeedback = "";
    }

    internal void SetDroneSurvey(ClientDroneSurveyObservation observation, ClientFieldPlace? destination,
        string sortieReport, string feedback)
    {
        if (_disposed) return;
        _droneSurvey = observation;
        _droneSurveyDestination = destination;
        _droneDestinationName = destination?.Name ?? "";
        _droneSortieReport = sortieReport;
        _droneSurveyFeedback = feedback;
        RefreshDroneSurveyDestination();
        if (_immersiveRoot != null) _immersiveScoutTarget.text = _droneDestination.text;
        UpdateDroneTools();
    }

    private void BuildDroneSurveyOverlay(RectTransform feed, bool immersive)
    {
        var reticle = Node(feed, "SurveyReticle");
        reticle.anchorMin = reticle.anchorMax = reticle.pivot = new Vector2(.5f, .5f);
        reticle.anchoredPosition = Vector2.zero;
        reticle.sizeDelta = new Vector2(26, 26);
        foreach (var horizontal in new[] { false, true })
        {
            var line = Panel(reticle, horizontal ? "Horizontal" : "Vertical", ChromeInk,
                horizontal ? 0 : 12, horizontal ? 12 : 0, horizontal ? 26 : 2, horizontal ? 2 : 26);
            line.raycastTarget = false;
            line.sprite = null;
            line.type = Image.Type.Simple;
            _droneSurveyReticleLines.Add(line);
        }
        _droneSurveyReticles.Add(reticle);
        reticle.gameObject.SetActive(false);
        if (!immersive)
        {
            var button = BuildDroneSurveyCapture(feed, 0, 0, 178, 34);
            var rect = button.Image.rectTransform;
            rect.anchorMin = rect.anchorMax = rect.pivot = Vector2.one;
            rect.anchoredPosition = new Vector2(-12, -44);
        }
    }

    private ButtonWidget BuildDroneSurveyCapture(Transform parent, float x, float y, float width, float height)
    {
        var button = Button(parent, "MarkSurveySurface", "Mark surface", x, y, width, height,
            () => { if (HasDroneSurvey && _droneSurvey.Surface != null) _command("drone", "save-survey"); });
        button.Image.color = ChromeRaised;
        button.Label.color = ChromeInk;
        button.Label.fontSize = 17;
        if (parent == _immersiveDroneTools || parent.parent == _immersiveDrone)
        {
            button.Image.sprite = null;
            button.Image.type = Image.Type.Simple;
            Place(button.Label.rectTransform, 12, 0, width - 24, height);
        }
        _droneSurveyCaptureButtons.Add(button);
        return button;
    }

    private void BuildDroneSurveyActions(RectTransform panel, bool immersive)
    {
        var save = BuildDroneSurveyCapture(panel, 18, 316, immersive ? 224 : 240, immersive ? 36 : 42);
        _droneSurveyPanelButtons.Add(save);
        var precision = DroneToolButton(panel, "SurveyPrecision", "Precision: off", immersive ? 252 : 274, 316, immersive ? 224 : 240,
            () => { _droneSurveyPrecision = !_droneSurveyPrecision; UpdateDroneTools(); });
        _dronePrecisionButtons.Add(precision);
        var terrain = DroneToolButton(panel, "TerrainProfile", "Terrain profile", immersive ? 486 : 530, 316, immersive ? 256 : 272,
            () => { _droneTerrainDetails = !_droneTerrainDetails; UpdateDroneTools(); });
        _droneTerrainButtons.Add(terrain);
    }

    private void UpdateDroneSurveyControls(bool camera)
    {
        var current = HasDroneSurvey;
        var surface = current && _droneSurvey.Surface != null;
        foreach (var reticle in _droneSurveyReticles) reticle.gameObject.SetActive(current);
        foreach (var line in _droneSurveyReticleLines) line.color = surface ? ChromeMint : new Color(1, 1, 1, .65f);
        foreach (var button in _droneSurveyCaptureButtons)
        {
            button.Image.gameObject.SetActive(camera);
            button.Button.interactable = surface;
            button.Label.text = surface ? "Mark · " + DroneNumber(_droneSurvey.SurfaceDistance, " m") : "No survey return";
            button.Label.color = surface ? ChromeMint : ChromeMuted;
        }
        foreach (var button in _droneSurveyPanelButtons)
            button.Image.gameObject.SetActive(_droneToolTab == "Survey" && !_droneTerrainDetails);
        foreach (var button in _dronePrecisionButtons)
        {
            button.Image.gameObject.SetActive(_droneToolTab == "Survey" && !_droneTerrainDetails);
            button.Label.text = _droneSurveyPrecision ? "Precision: latched" : "Precision: off";
            button.Image.color = _droneSurveyPrecision ? Mint : ChromeRaised;
            button.Label.color = _droneSurveyPrecision ? Chrome : ChromeInk;
        }
        foreach (var button in _droneTerrainButtons)
        {
            button.Image.gameObject.SetActive(_droneToolTab == "Survey");
            button.Label.text = _droneTerrainDetails ? "Camera survey" : "Terrain profile";
            button.Image.color = _droneTerrainDetails ? Mint : ChromeRaised;
            button.Label.color = _droneTerrainDetails ? Chrome : ChromeInk;
        }
        RefreshDroneSurveyDestination();
    }

    private void RefreshDroneSurveyDestination()
    {
        if (_droneDestination == null) return;
        var target = _droneSurveyDestination;
        var name = _droneDestinationName.Length > 24 ? _droneDestinationName[..23] + "…" : _droneDestinationName;
        _droneDestination.text = name.Length == 0 ? "Choose a scout target on the map."
            : HasDroneSurvey && target != null && target.Name == _droneDestinationName
                ? "Scout " + name + " · " + DroneSurveyDirection(target.Position)
                : "Scout " + name + " · aircraft guidance unavailable";
    }

    private string DroneSurveyDetails()
    {
        if (_droneTerrainDetails) return DroneTerrainSurveyDetails();
        if (!HasDroneSurvey)
            return "Survey observations need a live onboard camera.\n\nChoose a saved map pin as a scout target. Flight remains manual.\nPrecision latches the existing 35% input scale for this drone session.";
        var target = _droneSurveyDestination == null ? "No scout target selected"
            : "Scout: " + DroneSurveyDirection(_droneSurveyDestination.Position);
        var pilot = _droneSurvey.Pilot == null ? "Pilot position unavailable" : "Pilot: " + DroneSurveyDirection(_droneSurvey.Pilot);
        var margin = float.IsFinite(_droneSurvey.RadioMargin)
            ? "Radio margin " + DroneNumber(_droneSurvey.RadioMargin, " m") + (_droneSurvey.RadioMargin <= 25 ? " · approach the pilot" : "")
            : "Radio margin unavailable";
        var surface = _droneSurvey.Surface == null ? _droneSurvey.SurfaceStatus
            : _droneSurvey.SurfaceStatus + " · " + DroneNumber(_droneSurvey.SurfaceDistance, " m from lens")
                + "\nX " + DroneNumber(_droneSurvey.Surface.X, "") + "  Y " + DroneNumber(_droneSurvey.Surface.Y, "") + "  Z " + DroneNumber(_droneSurvey.Surface.Z, "");
        return target + "\n" + pilot + " · " + margin + "\n" + surface
            + "\n\nBearings / distances are horizontal. Radio uses 3D distance.\nMarks are local observations; clearance and routes are unverified."
            + (_droneSurveyFeedback.Length == 0 ? "\nMark saves a private pin without changing your destination." : "\n" + _droneSurveyFeedback);
    }

    private string DroneTerrainSurveyDetails()
    {
        const string title = "HOST HEIGHTMAP BENEATH AIRCRAFT\n";
        const string limits = "\n\nOne point only; caves, holes, meshes and landing clearance are unclassified.\nCamera surface marks remain separate. Flight stays manual.";
        if (!_droneState.Ready || !_droneState.Deployed || !_droneState.Link)
            return title + "A live drone link is required." + limits;
        var survey = _droneState.TerrainSurvey;
        if (survey == null || survey.DroneId != _droneState.DroneId)
            return title + "No current slope observation supplied by the host." + limits;
        var age = Math.Max(0, (Environment.TickCount64 - survey.RecordedAt) / 1000d);
        if (age > 1.5) return title + "The last slope observation has expired." + limits;
        var freshness = (survey.FromRemoteHost ? "received " : "observed ")
            + age.ToString("0.0", CultureInfo.InvariantCulture) + "s ago";
        var location = "\nSample X " + DroneNumber(survey.Position.X, "") + " / Z " + DroneNumber(survey.Position.Z, "")
            + " · " + freshness;
        if (survey.Status != ClientDroneTerrainSurveyStatus.Sampled
            || survey.SlopeDegrees is not { } slope || survey.Normal is not { } normal)
            return title + (survey.Status == ClientDroneTerrainSurveyStatus.SurfaceUnavailable
                ? "Heightmap sample unavailable here." : "Terrain observation unavailable.") + location + limits;
        var direction = "no downhill bearing";
        if (normal.Y > 0 && normal.X * normal.X + normal.Z * normal.Z > .000001f)
        {
            var bearing = Mathf.Repeat(MathF.Atan2(normal.X, normal.Z) * Mathf.Rad2Deg, 360);
            var cardinal = ((int)MathF.Floor((bearing + 22.5f) / 45f) % 8) switch
            { 0 => "N", 1 => "NE", 2 => "E", 3 => "SE", 4 => "S", 5 => "SW", 6 => "W", _ => "NW" };
            direction = "downhill " + cardinal;
        }
        return title + "Slope " + slope.ToString("0.0", CultureInfo.InvariantCulture) + "° · " + direction + location + limits;
    }

    private string DroneSurveyDirection(ClientFieldPoint target)
    {
        if (_droneSurvey.Aircraft is not { } position || !float.IsFinite(_droneSurvey.Heading)) return "Position unknown";
        var dx = target.X - position.X;
        var dz = target.Z - position.Z;
        var distance = MathF.Sqrt(dx * dx + dz * dz);
        if (distance < 3) return "Within 3 m horizontally";
        var bearing = Mathf.Repeat(MathF.Atan2(dx, dz) * Mathf.Rad2Deg, 360);
        var turn = Mathf.DeltaAngle(_droneSurvey.Heading, bearing);
        var direction = MathF.Abs(turn) < 12 ? "Ahead" : MathF.Abs(turn) > 165 ? "Behind"
            : (turn > 0 ? "Right " : "Left ") + MathF.Abs(turn).ToString("0", CultureInfo.InvariantCulture) + "°";
        return direction + " · " + distance.ToString("0", CultureInfo.InvariantCulture) + " m";
    }
}
