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
    private RectTransform _spaceflightSurface = null!;
    private TextMeshProUGUI _spaceflightState = null!, _spaceflightStatus = null!, _spaceflightFeedback = null!;
    private TextMeshProUGUI _spaceflightRegion = null!, _spaceflightSeats = null!, _spaceflightDistance = null!;
    private TextMeshProUGUI _spaceflightRoute = null!, _spaceflightNext = null!, _spaceflightSurvey = null!;
    private TextMeshProUGUI _spaceflightSystems = null!;
    private readonly Dictionary<string, ButtonWidget> _spaceflightActions = new();

    internal bool SpaceflightVisible => !_disposed && !ContentObscured && _page?.App == ClientTerminalApp.Spaceflight
        && _spaceflightSurface != null && _spaceflightSurface.gameObject.activeInHierarchy;

    private void BuildSpaceflight()
    {
        _spaceflightSurface = FieldSurface("Spaceflight");
        var ship = RoundedPanel(_spaceflightSurface, "Spacecraft", Surface, 0, 0, 836, 276).rectTransform;
        Icon(ship, ClientTerminalApp.Spaceflight, Mint, 24, 20, 64);
        Text(ship, "Title", "SPACECRAFT", 20, Muted, 112, 16, 700, 28, true);
        _spaceflightState = HangarText(ship, "State", "Waiting for spacecraft", 30, Ink, 112, 48, 700, 44, true);
        _spaceflightStatus = HangarText(ship, "Status", "", 21, Muted, 24, 98, 788, 42);
        _spaceflightSystems = HangarText(ship, "Systems", "", 17, Muted, 24, 143, 788, 24);
        Panel(ship, "Rule", Rule, 24, 170, 788, 1);
        _spaceflightRegion = SpaceflightReadout(ship, "Region", "REGION", 24, 186, 240);
        _spaceflightSeats = SpaceflightReadout(ship, "Seats", "SEATS USED", 290, 186, 220);
        _spaceflightDistance = SpaceflightReadout(ship, "Ramp", "TO BOARDING RAMP", 552, 186, 260);

        var boarding = RoundedPanel(_spaceflightSurface, "Boarding", Surface, 856, 0, 472, 276).rectTransform;
        Text(boarding, "Title", "YOUR CREW", 20, Muted, 22, 12, 428, 28, true);
        SpaceflightButton(boarding, "deploy", "Deploy spacecraft", 22, 48, 428);
        SpaceflightButton(boarding, "board-pilot", "Board as pilot", 22, 104, 428);
        SpaceflightButton(boarding, "board-passenger", "Board as passenger", 22, 160, 428);
        SpaceflightButton(boarding, "stand", "Walk cabin", 22, 216, 208);
        SpaceflightButton(boarding, "exit", "Disembark", 242, 216, 208);

        var guidance = RoundedPanel(_spaceflightSurface, "Guidance", Surface, 0, 288, 1328, 100).rectTransform;
        _spaceflightNext = HangarText(guidance, "Next", "", 22, Ink, 24, 8, 1280, 30, true);
        _spaceflightFeedback = HangarText(guidance, "Feedback", "", 21, Muted, 24, 40, 1280, 52);

        var route = RoundedPanel(_spaceflightSurface, "Route", Surface, 0, 400, 836, 200).rectTransform;
        Text(route, "Title", "SPACE  /  WORLD  /  MOON  /  ISLAND", 20, Muted, 24, 12, 788, 28, true);
        _spaceflightRoute = HangarText(route, "Progress", "", 22, Ink, 24, 46, 788, 30, true);
        SpaceflightButton(route, "launch", "Launch", 24, 86, 188);
        SpaceflightButton(route, "travel", "World", 224, 86, 188);
        SpaceflightButton(route, "moon", "Moon", 424, 86, 188);
        SpaceflightButton(route, "return", "Island", 624, 86, 188);
        HangarText(route, "FlightHint", "Look at a control, then press Tab to use the cursor.\nPilot flies; copilot manages systems. Land before disembarking.", 19, Muted, 24, 146, 788, 44);

        var ground = RoundedPanel(_spaceflightSurface, "Exploration", Surface, 856, 400, 472, 200).rectTransform;
        Text(ground, "Title", "ON THE SURFACE", 20, Muted, 22, 12, 428, 28, true);
        _spaceflightSurvey = HangarText(ground, "Survey", "", 20, Ink, 22, 46, 428, 30);
        SpaceflightButton(ground, "survey", "Survey site", 22, 86, 208);
        SpaceflightButton(ground, "recover", "Recover", 242, 86, 208);
        HangarText(ground, "ReturnHint", "Explore on foot, then board to return.\nAway from home? Recovery returns you.", 19, Muted, 22, 146, 428, 46);
        SetSpacecraft(Plugin.Spacecraft.View);
    }

    private TextMeshProUGUI SpaceflightReadout(Transform parent, string name, string label,
        float x, float y, float width)
    {
        Text(parent, name + "Label", label, 18, Muted, x, y, width, 24);
        return HangarText(parent, name, "—", 26, Ink, x, y + 28, width, 42, true);
    }

    private void SpaceflightButton(Transform parent, string action, string label, float x, float y, float width)
    {
        var button = Button(parent, action, label, x, y, width, 48, () =>
        {
            if (SpaceflightVisible) _command("spacecraft", action);
        });
        button.Label.fontSize = 21;
        button.Label.overflowMode = TextOverflowModes.Ellipsis;
        _spaceflightActions.Add(action, button);
    }

    private void SetSpaceflightVisible(bool visible)
    {
        if (_disposed || _spaceflightSurface == null) return;
        _spaceflightSurface.gameObject.SetActive(visible);
        if (visible) SetSpacecraft(Plugin.Spacecraft.View);
    }

    internal void SetSpacecraft(ClientSpacecraftView state)
    {
        if (_disposed || _spaceflightSurface == null) return;
        var rider = Plugin.Spacecraft.Rider;
        var boarding = state.Boarding;
        var known = state.Ready && state.Available;
        var aboard = state.Seat >= 0;
        var nearby = known && float.IsFinite(state.Distance) && state.Distance <= 6;
        var parked = SpacecraftPresentation.Parked(state);
        var region = Plugin.Spacecraft.LocalRegion;
        _spaceflightState.text = SpacecraftPresentation.State(state, boarding);
        _spaceflightState.color = state.Ready ? Ink : Amber;
        _spaceflightStatus.text = state.Status;
        var flight = state.Flight;
        _spaceflightSystems.text = !known ? "" : "ENGINE " + (flight.Powered ? "ON" : "OFF")
            + "   ·   DAMPENERS " + (flight.Dampeners ? "ON" : "OFF")
            + "   ·   RAMP " + (flight.DoorOpen ? flight.DoorAmount >= .99f ? "OPEN" : "OPENING"
                : flight.DoorAmount <= .01f ? "SEALED" : "CLOSING")
            + "   ·   GEAR " + (flight.GearDeployed ? flight.GearAmount >= .99f ? "DOWN" : "EXTENDING"
                : flight.GearAmount <= .01f ? "UP" : "RETRACTING");
        _spaceflightFeedback.text = string.IsNullOrWhiteSpace(state.Feedback)
            ? "Requests are checked by the host." : state.Feedback;
        _spaceflightRegion.text = known || state.ExpeditionActive ? SpacecraftPresentation.Region(region) : "—";
        _spaceflightSeats.text = known ? Math.Clamp(state.Occupants, 0, 3).ToString(CultureInfo.InvariantCulture) + " / 3" : "—";
        _spaceflightDistance.text = known && float.IsFinite(state.Distance)
            ? state.Distance.ToString("0", CultureInfo.InvariantCulture) + " m" : "—";
        _spaceflightRoute.text = known ? SpacecraftPresentation.Route(state.Flight) : "Deploy and board to begin your journey.";
        _spaceflightSurvey.text = "Sites surveyed: " + Math.Max(0, state.SurveyCount).ToString(CultureInfo.InvariantCulture);
        foreach (var (action, button) in _spaceflightActions)
            button.Button.interactable = SpacecraftPresentation.CanRequest(action, state, rider?.Attached == true, boarding,
                region, nearby);
        _spaceflightActions["board-pilot"].Label.text = state.CabinWalking && state.Seat == 0 ? "Return to pilot seat"
            : state.PilotOccupied ? "Pilot seat occupied" : "Board as pilot";
        _spaceflightActions["board-passenger"].Label.text = state.CabinWalking && state.Seat > 0
            ? state.Seat == 1 ? "Return to copilot seat" : "Return to your seat" : "Board as passenger";
        _spaceflightActions["stand"].Label.text = state.CabinWalking ? "Walking in cabin" : "Walk cabin";
        _spaceflightActions["launch"].Label.text = flight.Region is SpacecraftRegion.TestWorld or SpacecraftRegion.Moon
            ? "Ascend to orbit" : "Launch";
        _spaceflightActions["exit"].Label.text = boarding ? "Cancel boarding" : "Disembark";
        _spaceflightActions["recover"].Label.text = state.ExpeditionActive ? "Return safely" : "Recover ship";
        _spaceflightNext.text = !state.Ready ? ""
            : state.Loading ? "Preparing the spacecraft. Its model and cabin are still loading."
            : boarding ? "Preparing return protection and your seat. X cancels boarding."
            : state.Flight.Travel != SpacecraftTravel.None ? "Travel is in progress. Stay seated while the route completes."
            : state.CabinWalking ? "Move around the cabin. Use your seat action or look at your chair and press E to sit again."
            : aboard ? "Close the tablet. Aim at a control, then press Tab to use the cursor. X leaves the chair; land before disembarking."
            : region == SpacecraftRegion.Moon ? "Explore the lunar surface, then return to the spacecraft for your next route."
            : region == SpacecraftRegion.TestWorld ? "Explore the landing site and survey its landmarks. Board again to return."
            : !state.Available ? "Face a wide, clear landing area and deploy the spacecraft."
            : !nearby ? "Walk through the rear ramp to the cabin. Look at a chair and press E to sit."
            : !parked ? "Wait for the spacecraft to park before boarding."
            : state.Occupants >= 3 ? "The cabin is full. Wait for a seat to become available."
            : "Pause outside the ramp to prepare your return, then walk inside. Look at a chair and press E.";
    }
}

// Presentation gates explain the current snapshot. The host validates every request.
internal static class SpacecraftPresentation
{
    internal static bool Parked(ClientSpacecraftView state) => state.Ready && state.Available
        && state.Flight.Grounded && state.Flight.Velocity.sqrMagnitude < 1
        && state.Flight.AngularVelocity.sqrMagnitude < .04f
        && state.Flight.Travel == SpacecraftTravel.None;

    internal static string Region(SpacecraftRegion region) => region switch
    {
        SpacecraftRegion.Space => "Space",
        SpacecraftRegion.TestWorld => "Unknown world",
        SpacecraftRegion.Moon => "Moon",
        _ => "Island"
    };

    internal static string State(ClientSpacecraftView state, bool boarding) => !state.Ready ? "Spaceflight unavailable"
        : state.Loading ? "Preparing spacecraft…"
        : boarding ? "Boarding spacecraft…"
        : !state.Available ? "No spacecraft deployed"
        : state.Flight.Travel != SpacecraftTravel.None ? "Travel countdown"
        : state.Flight.Docked ? "Docked at station"
        : state.CabinWalking ? state.Flight.Region == SpacecraftRegion.Space ? "Zero gravity" : "Walking in the cabin"
        : state.Seat == 0 ? "You are the pilot"
        : state.Seat == 1 ? "You are the copilot"
        : state.Seat > 0 ? "Passenger aboard"
        : Parked(state) ? "Parked"
        : state.Flight.Region == SpacecraftRegion.Space ? "In space"
        : state.Flight.Region is SpacecraftRegion.TestWorld or SpacecraftRegion.Moon ? "Approaching the surface" : "In flight";

    internal static string Route(SpacecraftFlightState flight)
    {
        if (flight.Docked) return "Station latch engaged · Release from the pilot's systems monitor.";
        if (flight.Travel == SpacecraftTravel.None)
            return flight.Region switch
            {
                SpacecraftRegion.Space => "In space · Choose world, moon or island.",
                SpacecraftRegion.TestWorld => "Unknown world · Land and survey the wetlands.",
                SpacecraftRegion.Moon => "Moon · Deploy landing gear and find a clear landing site.",
                _ => "Island · Close the ramp, power up and launch."
            };
        var progress = float.IsFinite(flight.TravelProgress) ? Mathf.Clamp01(flight.TravelProgress) : 0;
        var destination = flight.Travel switch
        {
            SpacecraftTravel.Launch => "Launch to space",
            SpacecraftTravel.Destination => "Travel to world",
            SpacecraftTravel.Moon => "Travel to moon",
            _ => "Return to island"
        };
        return destination + " · Countdown " + (progress * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
    }

    internal static bool CanRequest(string action, ClientSpacecraftView state, bool attached, bool boarding,
        SpacecraftRegion region, bool nearby)
    {
        var known = state.Ready && state.Available;
        var parked = Parked(state);
        var aboard = state.Seat >= 0;
        var crew = known && state.Seat is 0 or 1 && attached && !boarding && !state.CabinWalking;
        var idle = state.Flight.Travel == SpacecraftTravel.None;
        var sealedCabin = !state.Flight.DoorOpen && state.Flight.DoorAmount <= .01f;
        var travelReady = crew && state.PilotOccupied && state.Flight.Powered && idle && sealedCabin && !state.Flight.Docked;
        var dockingPilot = crew && state.Seat == 0 && (state.IsHost || state.CrewAccess)
            && idle && state.Flight.Region == SpacecraftRegion.Space;
        var resumeSeat = known && aboard && attached && state.CabinWalking && !boarding && idle;
        return action switch
        {
            "deploy" => state.Ready && !state.Available && !state.ExpeditionActive && !boarding && !state.Loading,
            "board-pilot" => resumeSeat && state.Seat == 0 || parked && !aboard && !boarding && state.CanBoardPilot,
            "board-passenger" => resumeSeat && state.Seat > 0 || parked && !aboard && !boarding && state.CanBoardPassenger,
            "exit" => boarding || aboard && parked && !state.Flight.Powered && region != SpacecraftRegion.Space,
            "stand" => known && aboard && attached && !state.CabinWalking && !boarding && idle
                && (sealedCabin || (region == SpacecraftRegion.Space
                    ? ClientSpacecraftBody.StoppedInSpace(state.Flight) : parked)),
            "station-dock" => dockingPilot && state.Station is { CanOperateDock: true },
            "station-assist" => dockingPilot && state.Station is { } station
                && (station.Assist || station.CanMatchVelocity),
            "power" => crew && idle && !state.Flight.Docked,
            "dampeners" => crew && idle && !state.Flight.Docked && state.Station?.Assist != true,
            "gear" => crew && idle && !state.Flight.Docked && (!state.Flight.Grounded || !state.Flight.GearDeployed),
            "door" => crew && idle && (state.Flight.DoorOpen || state.Flight.Systems.EmergencyOverride
                || (parked || ClientSpacecraftBody.StoppedInSpace(state.Flight))
                    && (!state.Flight.Powered || region == SpacecraftRegion.Space)),
            "launch" => travelReady && state.Flight.Region != SpacecraftRegion.Space,
            "travel" => travelReady && state.Flight.Region == SpacecraftRegion.Space,
            "moon" => travelReady && state.Flight.Region == SpacecraftRegion.Space,
            "return" => travelReady && state.Flight.Region != SpacecraftRegion.Island,
            "engage-route" => travelReady && state.Flight.Systems.Ready
                && state.Flight.Systems.SelectedDestination != state.Flight.Region,
            "survey" => state.Ready && !aboard && !boarding && region == SpacecraftRegion.TestWorld,
            "recover" => state.CanRecover,
            _ => false
        };
    }
}
