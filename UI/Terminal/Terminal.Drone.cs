// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Linq;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientTerminal
{
    private bool _inventoryDroneRequested;
    private float _nextDroneDisplay;
    private float _droneCameraPitch = 12;
    private float _droneCameraZoom = 1;
    private string _cameraDroneId = "";
    private DroneCameraVision _droneRequestedVision = DroneCameraVision.Color;

    private bool AcceptInventoryDrone()
    {
        if (!AcceptInventoryTablet()) return false;
        _inventoryDroneRequested = true;
        return true;
    }

    private bool AcceptInventoryController()
    {
        if (!AcceptInventoryPresentation(true)) return false;
        _inventoryDroneRequested = true;
        return true;
    }

    private void UpdateDroneInput()
    {
        var visible = !_failed && _open && _app == ClientTerminalApp.Drone
            && Application.isFocused && CanUseDesktop() && !_contentObscured && _view?.DroneVisible == true;
        Plugin.Drones.SetViewVisible(visible);
        UpdateDroneSurveySession(Plugin.Drones.Snapshot);
        if (!Plugin.Drones.Snapshot.Ready || !Plugin.Drones.Snapshot.Deployed)
            _droneRequestedVision = DroneCameraVision.Color;
        var pilot = visible && Plugin.Drones.PilotRequested && Plugin.Drones.Snapshot.Piloting && !IsEditingText();
        if (_app == ClientTerminalApp.Drone) SetArcadeNavigation(pilot || visible && DroneFullscreen);
        var precision = _view?.DroneSurveyPrecision == true || Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift) ? .35f : 1;
        var input = pilot ? new DronePilotInput(
            Axis(KeyCode.W, KeyCode.S) * precision, Axis(KeyCode.D, KeyCode.A) * precision,
            Axis(KeyCode.Space, KeyCode.LeftControl) * precision, Axis(KeyCode.E, KeyCode.Q) * precision)
            : DronePilotInput.Neutral;
        Plugin.Drones.SetPilot(pilot, input);
        if (visible)
        {
            if (_cameraDroneId != Plugin.Drones.Snapshot.DroneId)
            {
                _cameraDroneId = Plugin.Drones.Snapshot.DroneId;
                _droneCameraPitch = 12;
                _droneCameraZoom = 1;
                SetDroneCameraVision(DroneCameraVision.Color);
                _view?.ConsumeDroneCameraCommand();
            }
            switch (_view?.ConsumeDroneCameraCommand())
            {
                case "tilt-up": _droneCameraPitch -= 10; break;
                case "tilt-down": _droneCameraPitch += 10; break;
                case "zoom-in": _droneCameraZoom += .2f; break;
                case "zoom-out": _droneCameraZoom -= .2f; break;
                case "reset": _droneCameraPitch = 12; _droneCameraZoom = 1; break;
                case "level": _droneCameraPitch = 0; break;
                case "survey": _droneCameraPitch = 80; break;
                case "vision-color": SetDroneCameraVision(DroneCameraVision.Color); break;
                case "vision-night": SetDroneCameraVision(DroneCameraVision.Night); break;
                case "vision-thermal": SetDroneCameraVision(DroneCameraVision.Thermal); break;
            }
            _droneCameraPitch = Mathf.Clamp(_droneCameraPitch, -20, 80);
            _droneCameraZoom = Mathf.Clamp(_droneCameraZoom, 1, 3);
            if (DroneFullscreen && !IsEditingText())
            {
                _droneCameraPitch = Mathf.Clamp(_droneCameraPitch
                    + Axis(KeyCode.DownArrow, KeyCode.UpArrow) * 45 * Mathf.Min(Time.unscaledDeltaTime, .05f), -20, 80);
                _droneCameraZoom = Mathf.Clamp(_droneCameraZoom + Input.mouseScrollDelta.y * .2f, 1, 3);
                if (Input.GetKeyDown(KeyCode.R)) { _droneCameraPitch = 12; _droneCameraZoom = 1; }
                if (_deviceInput.Captured && Input.GetKeyDown(KeyCode.V))
                    SetDroneCameraVision(_droneRequestedVision switch
                    {
                        DroneCameraVision.Color => DroneCameraVision.Night,
                        DroneCameraVision.Night => DroneCameraVision.Thermal,
                        _ => DroneCameraVision.Color
                    });
            }
            Plugin.Drones.SetCameraView(DroneFullscreen, _droneCameraPitch, _droneCameraZoom);
        }
        if (!visible || Time.unscaledTime < _nextDroneDisplay) return;
        _nextDroneDisplay = Time.unscaledTime + .1f;
        _view?.SetDrone(Plugin.Drones.Snapshot,
            Plugin.Drones.Snapshot.Deployed ? Plugin.Drones.CameraFeed : Plugin.Drones.PlacementFeed, Plugin.Drones.Placement);
        _view?.SetDroneVision(Plugin.Drones.CameraVision, Plugin.Drones.CameraVisionStatus);
        var camera = Plugin.Drones.CameraInstruments;
        _view?.SetFlightInstruments(camera.Heading, camera.Pitch, camera.Zoom);
        var os = _os.Snapshot;
        var destination = os.Generation == _osGeneration
            ? os.Field.Places.FirstOrDefault(place => place.Id == os.Field.DestinationId) : null;
        _view?.SetDroneSurvey(Plugin.Drones.ObserveSurvey(), destination, _droneSortie.Report, _droneSurveyFeedback);
    }

    private void SetDroneCameraVision(DroneCameraVision mode)
    {
        _nextDroneDisplay = 0;
        if (!Plugin.Drones.SetCameraVision(mode))
        {
            Feedback("Camera selection rejected: a live drone link is required.");
            return;
        }
        // Cycle requested modes even when one is unavailable and the feed falls back to color.
        _droneRequestedVision = mode;
        _log?.LogInfo("Drone camera " + mode + " request accepted. " + Plugin.Drones.CameraVisionStatus);
    }

    private static float Axis(KeyCode positive, KeyCode negative) =>
        (Input.GetKey(positive) ? 1f : 0f) - (Input.GetKey(negative) ? 1f : 0f);

    private void NavigateToDroneContact()
    {
        var state = Plugin.Drones.Snapshot;
        var contact = state.LastContact;
        if (contact == null || contact.DroneId != state.DroneId)
        { Feedback("No last-contact location is available for this drone."); return; }
        var os = _os.Snapshot;
        if (os.Generation != _osGeneration)
        { Feedback("Wait for the current world notebook."); return; }
        // Navigation saves a fixed observation only after the player requests it.
        // Never move a saved pin that may have been renamed or added to a route.
        var note = "Last radio contact with drone " + contact.DroneId + ". Saved as a fixed location; the aircraft may have moved.";
        var place = os.Field.Places.FirstOrDefault(item => item.Category == "Recovery snapshot"
            && item.Note == note && item.Position == contact.Position)
            ?? new ClientFieldPlace(Guid.NewGuid(), "Drone last contact", contact.Position, note, "Recovery snapshot");
        if (os.Field.Places.Count >= 128 && !os.Field.Places.Any(item => item.Id == place.Id))
        { Feedback("Remove a saved place before adding a recovery pin."); return; }
        NavigateToPlace(place);
    }
}
