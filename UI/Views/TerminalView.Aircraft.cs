// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Globalization;
using TMPro;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientTerminalView
{
    private RectTransform _hangarSurface = null!;
    private TextMeshProUGUI _hangarState = null!, _hangarStatus = null!, _hangarFeedback = null!;
    private TextMeshProUGUI _hangarSeats = null!, _hangarDistance = null!, _hangarFuel = null!, _hangarHealth = null!;
    private TextMeshProUGUI _hangarNext = null!;
    private ButtonWidget _hangarDeploy = null!, _hangarPilot = null!, _hangarPassenger = null!;
    private ButtonWidget _hangarService = null!, _hangarRecover = null!, _hangarExit = null!;

    internal bool HangarVisible => !_disposed && !ContentObscured && _page?.App == ClientTerminalApp.Hangar
        && _hangarSurface != null && _hangarSurface.gameObject.activeInHierarchy;

    private void BuildHangar()
    {
        _hangarSurface = FieldSurface("Hangar");
        var aircraft = RoundedPanel(_hangarSurface, "Aircraft", Surface, 0, 0, 836, 308).rectTransform;
        Icon(aircraft, ClientTerminalApp.Hangar, Mint, 26, 22, 72);
        Text(aircraft, "Title", "ISLAND AIRCRAFT", 20, Muted, 122, 20, 680, 30, true);
        _hangarState = HangarText(aircraft, "State", "Waiting for aircraft service", 31, Ink, 122, 54, 680, 48, true);
        _hangarStatus = HangarText(aircraft, "Status", "", 22, Muted, 26, 104, 784, 64);
        Panel(aircraft, "Rule", Rule, 26, 180, 784, 1);
        _hangarSeats = HangarReadout(aircraft, "Cabin", "SEATS USED", 26, 192);
        _hangarDistance = HangarReadout(aircraft, "Distance", "DISTANCE", 224, 192);
        _hangarFuel = HangarReadout(aircraft, "Fuel", "FUEL", 422, 192);
        _hangarHealth = HangarReadout(aircraft, "Airframe", "AIRFRAME", 620, 192);
        var actions = RoundedPanel(_hangarSurface, "Boarding", Surface, 856, 0, 472, 308).rectTransform;
        Text(actions, "Title", "READY TO GO", 20, Muted, 22, 10, 428, 30, true);
        _hangarDeploy = HangarButton(actions, "Deploy", "Deploy shared plane", 22, 48, 428, "deploy");
        _hangarPilot = HangarButton(actions, "Pilot", "Board as pilot", 22, 110, 428, "board-pilot");
        _hangarPassenger = HangarButton(actions, "Passenger", "Board as passenger", 22, 172, 428, "board-passenger");
        _hangarExit = HangarButton(actions, "Exit", "Request exit", 22, 234, 428, "exit");

        var next = RoundedPanel(_hangarSurface, "NextStep", Surface, 0, 320, 1328, 78).rectTransform;
        _hangarNext = HangarText(next, "Guidance", "", 23, Ink, 24, 6, 1280, 30, true);
        _hangarFeedback = HangarText(next, "Feedback", "", 20, Muted, 24, 38, 1280, 34);
        var guide = RoundedPanel(_hangarSurface, "FlightGuide", Surface, 0, 410, 836, 190).rectTransform;
        Text(guide, "Title", "AT THE CONTROLS", 20, Muted, 24, 14, 788, 28, true);
        HangarText(guide, "Controls",
            "W / S  Pitch     A / D  Roll     Q / E  Yaw\nShift / Ctrl  Throttle     Space  Brake     F  Flaps\nI  Engine     L  Lights     Tab  Mouse controls     X  Request exit",
            21, Ink, 24, 48, 788, 90);
        HangarText(guide, "Checklist", "Clear runway • Start engine • Build airspeed • Pull up gently", 20, Mint, 24, 146, 788, 32);
        var ground = RoundedPanel(_hangarSurface, "GroundCrew", Surface, 856, 410, 472, 190).rectTransform;
        Text(ground, "Title", "GROUND CREW", 20, Muted, 22, 14, 428, 28, true);
        _hangarService = HangarButton(ground, "Service", "Service plane", 22, 52, 208, "service");
        _hangarRecover = HangarButton(ground, "Recover", "Recover plane", 242, 52, 208, "recover");
        _hangarService.Label.fontSize = _hangarRecover.Label.fontSize = 20;
        HangarText(ground, "Requirements", "Park nearby. Engine off, cabin empty.\nRecovery is available to the host.", 20, Muted, 22, 108, 428, 76);
        SetAircraft(Plugin.Aircraft.View);
    }

    private TextMeshProUGUI HangarText(Transform parent, string name, string value, float size, Color color,
        float x, float y, float width, float height, bool bold = false)
    {
        var text = Text(parent, name, value, size, color, x, y, width, height, bold);
        text.overflowMode = TextOverflowModes.Ellipsis;
        return text;
    }

    private TextMeshProUGUI HangarReadout(Transform parent, string name, string label, float x, float y)
    {
        Text(parent, name + "Label", label, 19, Muted, x, y, 184, 28);
        return HangarText(parent, name, "—", 29, Ink, x, y + 36, 184, 46, true);
    }

    private ButtonWidget HangarButton(Transform parent, string name, string label, float x, float y, float width, string action)
        => Button(parent, name, label, x, y, width, 54, () =>
        {
            if (HangarVisible) _command("aircraft", action);
        });

    private void SetHangarVisible(bool visible)
    {
        if (_disposed || _hangarSurface == null) return;
        _hangarSurface.gameObject.SetActive(visible);
        if (visible) SetAircraft(Plugin.Aircraft.View);
    }

    internal void SetAircraft(ClientAircraftView state)
    {
        if (_disposed || _hangarSurface == null || _hangarState == null) return;
        var known = state.Ready && state.Available;
        var aboard = known && state.Seat >= 0;
        var nearby = known && float.IsFinite(state.Distance) && state.Distance <= 8;
        var stopped = known && state.Flight.Grounded && state.Flight.Velocity.sqrMagnitude < 4;
        var boarding = nearby && stopped && !aboard;
        var empty = state.Occupants == 0;
        var groundCrew = nearby && stopped && empty && !state.Flight.Engine;
        _hangarState.text = !state.Ready ? "Aircraft service offline" : !state.Available ? "No plane deployed"
            : aboard ? state.Seat == 0 ? "You are the pilot" : "Passenger aboard"
            : state.Flight.Health <= .08f ? "Airframe disabled" : state.Flight.Stall ? "Aircraft stalled"
            : !state.Flight.Grounded ? "In flight" : !stopped ? "Taxiing" : "Parked";
        _hangarState.color = !state.Ready || known && (state.Flight.Stall || state.Flight.Health <= .08f) ? Amber : Ink;
        _hangarStatus.text = state.Status;
        _hangarFeedback.text = string.IsNullOrWhiteSpace(state.Feedback) ? "Requests are checked by the host." : state.Feedback;
        _hangarSeats.text = known ? Math.Clamp(state.Occupants, 0, 3).ToString(CultureInfo.InvariantCulture) + " / 3" : "—";
        _hangarDistance.text = known && float.IsFinite(state.Distance) ? state.Distance.ToString("0", CultureInfo.InvariantCulture) + " m" : "—";
        _hangarFuel.text = known ? HangarPercent(state.Flight.Fuel) : "—";
        _hangarHealth.text = known ? HangarPercent(state.Flight.Health) : "—";
        _hangarDeploy.Button.interactable = state.Ready && !state.Available;
        _hangarPilot.Button.interactable = boarding && !state.PilotOccupied && state.Occupants < 3;
        _hangarPassenger.Button.interactable = boarding && state.Occupants - (state.PilotOccupied ? 1 : 0) < 2;
        _hangarPilot.Label.text = state.PilotOccupied ? "Pilot seat occupied" : "Board as pilot";
        var still = known && ClientAircraftService.RecoveryMotionSafe(state.Flight);
        _hangarExit.Button.interactable = aboard;
        _hangarService.Button.interactable = groundCrew;
        _hangarRecover.Button.interactable = nearby && !aboard && empty && !state.Flight.Engine
            && state.CanRecover && still;
        _hangarNext.text = !state.Ready ? "Join a compatible host to use shared aircraft."
            : !state.Available ? "Face a large dry clearing with room ahead to taxi. Placement checks nearby positions and wheel support."
            : aboard ? "Close the tablet to use the cockpit. X requests exit or cancels boarding."
            : !nearby ? "Approach the plane to board or service it (within 8 m)."
            : !stopped ? "Wait for the plane to park before boarding."
            : state.Occupants >= 3 ? "The cabin is full. Wait for a seat to become available."
            : "Choose the pilot seat or join your crew as a passenger.";
    }

    private static string HangarPercent(float value) => float.IsFinite(value)
        ? (Mathf.Clamp01(value) * 100).ToString("0", CultureInfo.InvariantCulture) + "%" : "—";
}
