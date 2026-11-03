// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using Modstoke.Sons;
using TheForest.Utils;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace Modstoke.Client;

// Physical switches share the crew solver's ship-local contact points. System
// controls are shared by the front seats; displayed positions follow host state.
internal sealed partial class ClientSpacecraftCockpit : IDisposable
{
    private static readonly Color Ink = new(.94f, .97f, .98f, 1);
    private static readonly Color Muted = new(.58f, .63f, .66f, 1);
    private static readonly Color Cyan = new(.32f, .83f, .9f, 1);
    private static readonly Color Charcoal = new(.018f, .023f, .026f, 1);
    private static readonly Color Amber = new(1, .78f, .36f, 1);
    private static readonly Color Red = new(1, .32f, .24f, 1);
    private readonly ClientSpacecraftRider _rider;
    private readonly Action<string> _command;
    private readonly List<PanelControl> _controls = new();
    private readonly List<Material> _materials = new();
    private readonly Dictionary<int, Material> _worldFonts = new();
    private readonly Dictionary<int, Material> _glassFonts = new();
    private Material? _worldGraphic;
    private ClientTabletTypography? _typography;
    private GameObject? _root, _world;
    private readonly List<SeatDisplay> _displays = new(2);
    private ClientSpacecraftBody? _body;
    private RectTransform _strip = null!;
    private GraphicRaycaster _raycaster = null!;
    private TextMeshProUGUI _summary = null!, _feedback = null!;
    private TextMeshProUGUI _crewRole = null!, _crewAuthority = null!;
    private Button _exit = null!, _stand = null!, _recover = null!;
    private ClientSpacecraftView? _state;
    private PanelControl? _gesture, _held, _hovered;
    private Vector2 _stickGrab;
    private float _gestureAt, _heldAt, _contactAt, _contactBlockedUntil;
    private int _interactionSeat = -1;
    private uint _interactionRevision;
    private bool _dispatched, _rightDrag, _disposed, _visible;

    internal ClientSpacecraftCockpit(ClientSpacecraftRider rider, Action<string> command)
    {
        _rider = rider; _command = command;
        try
        {
            _typography = new ClientTabletTypography();
            _root = new GameObject("Spacecraft crew information");
            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 120;
            var scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 720);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
            _raycaster = _root.AddComponent<GraphicRaycaster>();
            _raycaster.enabled = false;
            _strip = Node(_root.transform, "Crew information", 0, 0, 1160, 58);
            _strip.anchorMin = _strip.anchorMax = new Vector2(.5f, 1);
            _strip.pivot = new Vector2(.5f, 1);
            _strip.anchoredPosition = new Vector2(0, -12);
            var background = _strip.gameObject.AddComponent<Image>();
            background.color = new Color(.025f, .029f, .032f, .94f);
            background.raycastTarget = false;
            var accent = Node(_strip, "Station accent", 0, 0, 3, 58).gameObject.AddComponent<Image>();
            accent.color = Cyan; accent.raycastTarget = false;
            var divider = Node(_strip, "Station divider", 144, 10, 1, 38).gameObject.AddComponent<Image>();
            divider.color = new Color(.24f, .28f, .3f, 1); divider.raycastTarget = false;
            _crewRole = Text(_strip, "Station", 16, 7, 120, 24, 16, Ink);
            _crewAuthority = Text(_strip, "Authority", 16, 32, 120, 18, 10, Muted);
            _summary = Text(_strip, "Route", 160, 7, 532, 24, 16, Ink);
            _feedback = Text(_strip, "Response", 160, 32, 532, 22, 14, Muted);
            _stand = AddCrewButton("Leave chair", "stand", 706, 132, () => _command("stand"));
            _exit = AddCrewButton("Disembark", "exit", 846, 128, () => _command("exit"));
            _recover = AddCrewButton("Island return", "recover", 982, 162, () => _command("recover"));
            _root.SetActive(false);
        }
        catch { Dispose(); throw; }
    }

    private bool AcceptsInput => !_disposed && _visible && _rider.Seated && _rider.PointerActive
        && ClientSpacecraftRider.WorldInputAvailable;

    internal void Update(ClientSpacecraftView state, ClientSpacecraftBody? body = null)
    {
        if (_disposed || _root == null) return;
        _state = state;
        body ??= _rider.Spacecraft;
        if (_interactionSeat != _rider.Seat || _interactionRevision != _rider.SeatRevision)
        {
            CancelInteraction();
            _interactionSeat = _rider.Seat;
            _interactionRevision = _rider.SeatRevision;
        }
        if (!ReferenceEquals(_body, body) || _world == null || !_world)
        {
            DestroyPanel();
            if (body?.Ready == true && body.Root) BuildPanel(body);
        }
        _visible = _rider.Active && !state.CabinWalking && !_rider.Faulted && GameRuntime.State.IsInWorld
            && Application.isFocused && !GameRuntime.State.PauseMenuOpen;
        _root.SetActive(_visible);
        _raycaster.enabled = AcceptsInput;
        if (_world != null) _world.SetActive(body?.Ready == true && GameRuntime.State.IsInWorld);
        if (_displays.Count != 0) UpdateNativeControls(state);
        if (!_visible) { CancelInteraction(); return; }

        var boarding = state.Boarding || !_rider.Seated;
        SetNativeText(_crewRole, _rider.Seat == 0 ? "PILOT" : _rider.Seat == 1 ? "COPILOT" : "PASSENGER", Ink);
        var access = state.IsHost || state.CrewAccess;
        SetNativeText(_crewAuthority, boarding ? "TAKING SEAT" : !state.Ready || !state.Available ? "STATE SYNC"
            : _rider.Seat > 1 ? "CABIN" : !access ? "ACCESS REQUIRED"
            : _rider.Seat == 0 ? "FLIGHT CONTROL" : "SYSTEMS ONLY", !access ? Amber : Muted);
        _summary.text = state.Ready && state.Available && !boarding ? SpacecraftPresentation.Route(state.Flight)
            : SpacecraftPresentation.State(state, boarding);
        _summary.color = state.Ready ? Ink : Amber;
        var contactBlocked = Time.unscaledTime < _contactBlockedUntil;
        _feedback.text = contactBlocked ? "The control could not be reached."
            : string.IsNullOrWhiteSpace(state.Feedback) ? state.Status : state.Feedback;
        _feedback.color = contactBlocked || !string.IsNullOrWhiteSpace(state.Feedback) ? Amber : Muted;
        _stand.gameObject.SetActive(AcceptsInput);
        _exit.gameObject.SetActive(AcceptsInput && state.Flight.Region != SpacecraftRegion.Space);
        _recover.gameObject.SetActive(AcceptsInput && state.ExpeditionActive);
        _exit.interactable = CanRequest("exit", state);
        _stand.interactable = CanRequest("stand", state);
        _recover.interactable = state.CanRecover && state.ExpeditionActive;

        if (AcceptsInput && _rider.Seat <= 1 && _displays.Count != 0) UpdateInteraction(state);
        else CancelInteraction();
        if (AcceptsInput && Mouse.current != null)
        {
            if (OverCrewButton(_exit, Mouse.current.position.ReadValue()) && !_exit.interactable)
                SetNativeText(_feedback, BlockedReason("exit", state), Amber);
            else if (OverCrewButton(_stand, Mouse.current.position.ReadValue()) && !_stand.interactable)
                SetNativeText(_feedback, BlockedReason("stand", state), Amber);
            else if (_hovered != null && !CanRequest(_hovered.Command, state))
                SetNativeText(_feedback, BlockedReason(_hovered.Command, state), Amber);
        }
    }

    private void BuildPanel(ClientSpacecraftBody body)
    {
        _body = body;
        _world = new GameObject("Spacecraft physical consoles") { layer = body.Root.layer };
        try
        {
            _world.transform.SetParent(body.Root.transform, false);
            for (var seat = 0; seat < 2; seat++)
            {
                var root = new GameObject(seat == 0 ? "Pilot chair controls and instruments" : "Copilot chair controls and instruments")
                    { layer = body.Root.layer };
                var display = new SeatDisplay(seat, root);
                _displays.Add(display);
                root.transform.SetParent(_world.transform, false);
                BuildNativeControls(display);
                // Follow the native glass plane; offset toward the viewer to avoid its baked decal.
                var rotation = Quaternion.Euler(-18.7f, 0, 0);
                var glass = WorldCanvas(root.transform, "Glass flight instruments",
                    new Vector3(seat == 0 ? .6916f : -.8996f, .071f, 5.484f) - rotation * Vector3.forward * .008f,
                    rotation, 610, 610);
                BuildGlassDisplay(display, glass);
            }
            _nativeTelemetryAt = 0;
        }
        catch { DestroyPanel(); throw; }
    }

    private void BuildGlassDisplay(SeatDisplay display, RectTransform screen)
    {
        display.GlassBearing = -1;
        // Tint the readout regions only; the central attitude window stays transparent.
        DisplayPanel(screen, "Heading backing", 20, 16, 574, 104, new Color(.018f, .023f, .026f, .46f));
        DisplayPanel(screen, "Speed backing", 20, 184, 132, 134, new Color(.018f, .023f, .026f, .68f));
        DisplayPanel(screen, "Vertical speed backing", 466, 184, 128, 134, new Color(.018f, .023f, .026f, .68f));
        DisplayPanel(screen, "Systems backing", 20, 409, 574, 146, new Color(.018f, .023f, .026f, .78f));
        GlassLine(screen, "Top rule", 28, 14, 554, 1, Cyan);
        display.GlassMode = GlassText(screen, "Flight mode", 28, 24, 330, 28, 18, Ink);
        display.GlassHeading = GlassText(screen, "Heading", 400, 24, 182, 28, 22, Ink);
        display.GlassHeading.alignment = TextAlignmentOptions.MidlineRight;
        var compass = Node(screen, "Heading tape", 36, 64, 538, 52);
        compass.gameObject.AddComponent<RectMask2D>();
        display.GlassCompass = Node(compass, "Bearings", 0, 0, 600, 52);
        display.GlassCompass.anchorMin = display.GlassCompass.anchorMax = display.GlassCompass.pivot = new Vector2(.5f, .5f);
        display.GlassCompass.anchoredPosition = Vector2.zero;
        for (var i = 0; i < display.GlassBearings.Length; i++)
        {
            var x = 300 + (i - 4) * 56;
            var bearing = GlassText(display.GlassCompass, "Bearing", x - 23, 2, 46, 24, 16, Ink);
            bearing.alignment = TextAlignmentOptions.Center;
            display.GlassBearings[i] = bearing;
            GlassLine(display.GlassCompass, "Heading tick", x, 30, 1, 10, Ink);
            GlassLine(display.GlassCompass, "Half heading tick", x + 28, 35, 1, 5, Muted);
        }
        GlassLine(compass, "Heading datum", 267, 42, 4, 9, Cyan);
        var attitude = Node(screen, "Glass attitude", 154, 148, 302, 240);
        attitude.gameObject.AddComponent<RectMask2D>();
        display.GlassHorizon = Node(attitude, "Glass horizon", 0, 0, 600, 600);
        display.GlassHorizon.anchorMin = display.GlassHorizon.anchorMax = display.GlassHorizon.pivot = new Vector2(.5f, .5f);
        display.GlassHorizon.anchoredPosition = Vector2.zero;
        GlassLine(display.GlassHorizon, "Left horizon", 168, 299, 110, 2, Ink);
        GlassLine(display.GlassHorizon, "Right horizon", 322, 299, 110, 2, Ink);
        for (var pitch = -40; pitch <= 40; pitch += 10)
        {
            if (pitch == 0) continue;
            var y = 300 - pitch * 3;
            GlassLine(display.GlassHorizon, "Left pitch mark", 242, y, 34, 1, Ink);
            GlassLine(display.GlassHorizon, "Right pitch mark", 324, y, 34, 1, Ink);
            GlassLine(display.GlassHorizon, "Pitch end", 357, pitch > 0 ? y : y - 5, 1, 5, Ink);
            var mark = GlassText(display.GlassHorizon, "Pitch", 366, y - 11, 40, 22, 16, Ink);
            mark.text = pitch.ToString("+0;-0", CultureInfo.InvariantCulture);
        }
        GlassLine(attitude, "Left datum", 95, 119, 39, 3, Cyan);
        GlassLine(attitude, "Right datum", 168, 119, 39, 3, Cyan);
        GlassLine(attitude, "Center datum", 148, 117, 6, 6, Ink);
        var label = GlassText(screen, "Speed label", 28, 192, 116, 24, 16, Ink); label.text = "SPEED";
        display.GlassSpeed = GlassText(screen, "Speed", 28, 224, 116, 54, 36, Ink);
        display.GlassSpeed.enableAutoSizing = true; display.GlassSpeed.fontSizeMin = 18; display.GlassSpeed.fontSizeMax = 36;
        label = GlassText(screen, "Speed unit", 28, 289, 116, 22, 15, Ink); label.text = "m/s";
        GlassLine(screen, "Speed datum", 28, 282, 116, 1, Ink);
        label = GlassText(screen, "Vertical label", 474, 192, 112, 24, 16, Ink); label.text = "VERT SPD";
        display.GlassVerticalSpeed = GlassText(screen, "Vertical speed", 474, 224, 112, 54, 30, Ink);
        display.GlassVerticalSpeed.enableAutoSizing = true; display.GlassVerticalSpeed.fontSizeMin = 16; display.GlassVerticalSpeed.fontSizeMax = 30;
        label = GlassText(screen, "Vertical unit", 474, 289, 112, 22, 15, Ink); label.text = "m/s";
        GlassLine(screen, "Vertical datum", 474, 282, 112, 1, Ink);
        GlassLine(screen, "Systems rule", 28, 409, 554, 1, Cyan);
        display.GlassSystems = GlassSystem(screen, "ENGINE", 28);
        display.GlassDampeners = GlassSystem(screen, "DAMPENERS", 170);
        display.GlassDoors = GlassSystem(screen, "RAMP", 312);
        display.GlassGear = GlassSystem(screen, "GEAR", 454);
        display.GlassGuidance = GlassText(screen, "Flight status", 28, 503, 554, 48, 18, Ink);
        display.GlassGuidance.enableWordWrapping = true;
        label = GlassText(screen, "Station", 28, 568, 554, 22, 14, Ink);
        label.text = display.Seat == 0 ? "PILOT / FLIGHT CONTROL" : "COPILOT / SYSTEMS ONLY";
        GlassLine(screen, "Bottom rule", 28, 596, 554, 1, Cyan);
    }

    private TextMeshProUGUI GlassSystem(RectTransform screen, string title, float x)
    {
        var label = GlassText(screen, title, x, 422, 128, 22, 14, Ink);
        label.text = title;
        DisplayPanel(screen, title + " frame", x, 448, 128, 32, new Color(.22f, .29f, .31f, 1));
        DisplayPanel(screen, title + " inset", x + 1, 449, 126, 30, Charcoal);
        var value = GlassText(screen, title + " state", x + 6, 450, 116, 28, 20, Ink);
        value.alignment = TextAlignmentOptions.Center;
        value.enableAutoSizing = true; value.fontSizeMin = 14; value.fontSizeMax = 20;
        return value;
    }

    private TextMeshProUGUI GlassText(Transform parent, string name, float x, float y, float width, float height, float size, Color color)
        => Text(parent, name, x, y, width, height, size, color, glass: true);

    private void GlassLine(Transform parent, string name, float x, float y, float width, float height, Color color)
    {
        // A local dark stroke preserves contrast on bright glass without hiding the view.
        DisplayPanel(parent, name + " outline", x - 1, y - 1, width + 2, height + 2, Charcoal);
        DisplayPanel(parent, name, x, y, width, height, color);
    }

    private void UpdateGlassDisplay(SeatDisplay display, ClientSpacecraftView state, bool known)
    {
        SetNativeText(display.GlassSpeed, display.Speed.text, display.Speed.color);
        SetNativeText(display.GlassVerticalSpeed, display.VerticalSpeed.text, display.VerticalSpeed.color);
        SetNativeText(display.GlassHeading, display.Heading.text, display.Heading.color);
        SetNativeText(display.GlassMode, display.FlightMode.text, display.FlightMode.color);
        SetNativeText(display.GlassSystems, display.Engine.text, display.Engine.color);
        SetNativeText(display.GlassDampeners, display.Dampeners.text, display.Dampeners.color);
        SetNativeText(display.GlassDoors, display.Doors.text, display.Doors.color);
        SetNativeText(display.GlassGear, display.Gear.text, display.Gear.color);
        var flight = state.Flight;
        var status = !known || !flight.Systems.Ready ? "AWAITING HOST TELEMETRY"
            : flight.Travel != SpacecraftTravel.None ? SpacecraftPresentation.Route(flight)
            : flight.DoorOpen || flight.DoorAmount > .01f ? "REAR RAMP UNSEALED / THRUST INHIBITED"
            : !flight.Powered ? "ENGINE OFF"
            : SpacecraftPresentation.Parked(state) ? "PARKED / FLIGHT READY"
            : !flight.GearDeployed && flight.Region != SpacecraftRegion.Space ? "GEAR UP"
            : flight.Dampeners ? "MANUAL FLIGHT / DAMPENERS ON" : "MANUAL FLIGHT / DAMPENERS OFF";
        var caution = !known || !flight.Systems.Ready || flight.DoorOpen || flight.DoorAmount > .01f
            || !flight.Powered || !flight.Dampeners;
        SetNativeText(display.GlassGuidance, status, caution ? Amber : Ink);
    }

    private void BuildFlightDisplay(SeatDisplay display, RectTransform screen)
    {
        var line = new Color(.35f, .43f, .46f, 1);
        DisplayPanel(screen, "Velocity field", 5, 34, 138, 45, new Color(.035f, .05f, .055f, 1));
        var label = Text(screen, "Speed label", 8, 36, 64, 14, 9, Muted); label.text = "VEL  m/s";
        display.Speed = Text(screen, "Speed", 8, 50, 64, 28, 21, Ink);
        display.Speed.enableAutoSizing = true; display.Speed.fontSizeMin = 11; display.Speed.fontSizeMax = 21;
        label = Text(screen, "Vertical label", 78, 36, 62, 14, 9, Muted); label.text = "V/S  m/s";
        display.VerticalSpeed = Text(screen, "Vertical speed", 78, 50, 62, 28, 19, Ink);
        display.VerticalSpeed.enableAutoSizing = true; display.VerticalSpeed.fontSizeMin = 11; display.VerticalSpeed.fontSizeMax = 19;
        DisplayPanel(screen, "Velocity divider", 73, 38, 1, 35, line);
        DisplayPanel(screen, "Attitude rim", 7, 83, 134, 86, line);
        var attitude = Node(screen, "Attitude", 8, 84, 132, 84);
        DisplayPanel(attitude, "Attitude backing", 0, 0, 132, 84, Charcoal);
        attitude.gameObject.AddComponent<RectMask2D>();
        display.Horizon = Node(attitude, "Horizon", 0, 0, 400, 400);
        display.Horizon.anchorMin = display.Horizon.anchorMax = display.Horizon.pivot = new Vector2(.5f, .5f);
        display.Horizon.anchoredPosition = Vector2.zero;
        DisplayPanel(display.Horizon, "Sky", 0, 0, 400, 200, new Color(.085f, .14f, .17f, 1));
        DisplayPanel(display.Horizon, "Ground", 0, 200, 400, 200, new Color(.04f, .045f, .05f, 1));
        DisplayPanel(display.Horizon, "Horizon line", 0, 199, 400, 1, Ink);
        for (var pitch = -40; pitch <= 40; pitch += 10)
        {
            if (pitch == 0) continue;
            var y = 200 - pitch;
            DisplayPanel(display.Horizon, "Left pitch mark", 176, y, 16, 1, line);
            DisplayPanel(display.Horizon, "Right pitch mark", 208, y, 16, 1, line);
            if (pitch % 20 != 0) continue;
            var mark = Text(display.Horizon, "Pitch", 228, y - 6, 24, 12, 8, Ink);
            mark.text = pitch.ToString("+0;-0", CultureInfo.InvariantCulture);
        }
        for (var bank = -60; bank <= 60; bank += 30)
        {
            var angle = bank * Mathf.Deg2Rad;
            var tick = Node(attitude, "Bank tick", 66 + Mathf.Sin(angle) * 39, 43 - Mathf.Cos(angle) * 39,
                1, bank == 0 ? 5 : 3);
            tick.pivot = new Vector2(.5f, 0);
            tick.localRotation = Quaternion.Euler(0, 0, bank);
            var image = tick.gameObject.AddComponent<Image>();
            image.color = Ink; image.material = WorldGraphic(); image.raycastTarget = false;
        }
        DisplayPanel(attitude, "Left datum", 40, 41, 18, 2, Cyan);
        DisplayPanel(attitude, "Right datum", 74, 41, 18, 2, Cyan);
        DisplayPanel(attitude, "Center datum", 64, 40, 4, 4, Ink);
        DisplayPanel(screen, "Heading field", 8, 172, 132, 17, new Color(.035f, .05f, .055f, 1));
        display.Heading = Text(screen, "Heading", 8, 172, 132, 17, 12, Ink);
        display.Heading.alignment = TextAlignmentOptions.Center;
        display.FlightMode = Text(screen, "Flight mode", 8, 193, 132, 15, 10, Ink);
        DisplayPanel(screen, "Command rule", 8, 210, 132, 1, Muted);
        display.NativeAscent = Text(screen, "Ascent command", 8, 215, 64, 13, 10, Muted);
        display.NativeRetro = Text(screen, "Retro command", 78, 215, 62, 13, 10, Muted);
    }

    private void DisplayPanel(Transform parent, string name, float x, float y, float width, float height, Color color)
    {
        var image = Node(parent, name, x, y, width, height).gameObject.AddComponent<Image>();
        image.color = color;
        image.material = WorldGraphic();
        image.raycastTarget = false;
    }

    private void UpdateFlightDisplay(SeatDisplay display, ClientSpacecraftView state, bool known)
    {
        var flight = state.Flight;
        SetNativeText(display.Speed, known ? Number(flight.Velocity.magnitude) : "SYNC", known ? Ink : Muted);
        SetNativeText(display.VerticalSpeed, known && float.IsFinite(flight.Velocity.y)
            ? flight.Velocity.y.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) : "SYNC", known ? Ink : Muted);
        display.Horizon.gameObject.SetActive(known);
        display.GlassHorizon.gameObject.SetActive(known);
        if (!known)
        {
            display.GlassCompass.gameObject.SetActive(false);
            SetNativeText(display.Heading, "ATTITUDE SYNC", Muted);
            return;
        }
        var forward = flight.Rotation * Vector3.forward;
        var up = Quaternion.Inverse(flight.Rotation) * Vector3.up;
        var headingKnown = forward.x * forward.x + forward.z * forward.z > .0001f;
        var heading = Mathf.Repeat(Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg, 360);
        SetNativeText(display.Heading, headingKnown ? "HDG "
            + ((int)Mathf.Repeat(heading + .5f, 360)).ToString("000", CultureInfo.InvariantCulture)
            : "HDG ---", Ink);
        display.GlassCompass.gameObject.SetActive(headingKnown);
        if (headingKnown)
        {
            var bearing = Mathf.FloorToInt(heading / 10) * 10;
            display.GlassCompass.anchoredPosition = new Vector2(-(heading - bearing) * 5.6f, 0);
            if (display.GlassBearing != bearing)
            {
                display.GlassBearing = bearing;
                for (var i = 0; i < display.GlassBearings.Length; i++)
                    SetNativeText(display.GlassBearings[i], ((int)Mathf.Repeat(bearing + (i - 4) * 10, 360))
                        .ToString("000", CultureInfo.InvariantCulture), Ink);
            }
        }
        var roll = -Mathf.Atan2(up.x, up.y) * Mathf.Rad2Deg;
        var pitch = Mathf.Asin(Mathf.Clamp(forward.y, -1, 1)) * Mathf.Rad2Deg;
        var rotation = Quaternion.Euler(0, 0, roll);
        display.Horizon.localRotation = rotation;
        display.Horizon.anchoredPosition = rotation * new Vector3(0, -pitch, 0);
        display.GlassHorizon.localRotation = rotation;
        display.GlassHorizon.anchoredPosition = rotation * new Vector3(0, -pitch * 3, 0);
    }

    private void UpdateInteraction(ClientSpacecraftView state)
    {
        var mouse = Mouse.current;
        if (mouse == null) { CancelInteraction(); return; }
        var screen = mouse.position.ReadValue();
        // Only actual crew buttons intercept console clicks. Status
        // text is presentation and must never turn into a dead interaction area.
        var overCrew = OverCrewButton(_exit, screen) || OverCrewButton(_stand, screen)
            || OverCrewButton(_recover, screen);
        _hovered = overCrew ? null : FindNativeControl(screen);
        if (_held != null)
        {
            var held = _rightDrag ? mouse.rightButton.isPressed : mouse.leftButton.isPressed;
            if (!held || overCrew || !CanRequest(_held.Command, state)
                || (!ReferenceEquals(_held, _hovered) && !CanSeeControl(_held))
                || !TryNativePoint(_held.DragFrame ?? _held.Frame, screen, out var contact, out _)
                || Mathf.Abs(contact.x - _stickGrab.x) > .28f || Mathf.Abs(contact.y - _stickGrab.y) > .28f)
            { CancelInteraction(); return; }
            var reach = Mathf.SmoothStep(0, 1, (Time.unscaledTime - _heldAt) / .20f);
            _rider.SetConsoleInteraction(_held.Kind, reach);
            var reached = _held.Kind == SpacecraftConsoleControl.Flight ? reach >= 1
                : _body != null && _rider.ConsolePressReached(_body, _held.Kind);
            if (!reached && Time.unscaledTime - _heldAt >= 1)
            { _contactBlockedUntil = Time.unscaledTime + 2; CancelInteraction(); return; }
            var translation = _held.Translation; var rotation = _held.Rotation;
            if (_held.Command == "stick")
            {
                var delta = Vector2.ClampMagnitude((contact - _stickGrab) / .085f, 1);
                if (_rightDrag) rotation = new Vector3(-delta.y, delta.x, 0);
                else translation = new Vector3(delta.x, 0, delta.y);
            }
            _rider.SetPanelFlight(reached ? translation : Vector3.zero, reached ? rotation : Vector3.zero,
                reached && _held.Boost, _rightDrag);
            return;
        }
        _rider.SetPanelFlight(Vector3.zero, Vector3.zero, false);
        if (_gesture != null)
        {
            if ((!ReferenceEquals(_gesture, _hovered) && !CanSeeControl(_gesture))
                || !_dispatched && !CanRequest(_gesture.Command, state)) { CancelInteraction(); return; }
            var now = Time.unscaledTime;
            if (_dispatched)
            {
                var elapsed = now - _contactAt;
                if (elapsed >= .46f) { CancelInteraction(); return; }
                _rider.SetConsoleInteraction(_gesture.Kind, elapsed < .18f ? 1
                    : Mathf.SmoothStep(1, 0, (elapsed - .18f) / .28f));
                return;
            }
            _rider.SetConsoleInteraction(_gesture.Kind, Mathf.SmoothStep(0, 1, (now - _gestureAt) / .22f));
            if (_body != null && _rider.ConsolePressReached(_body, _gesture.Kind))
            {
                _dispatched = true;
                _contactAt = now;
                _command(_gesture.Command);
            }
            else if (now - _gestureAt >= 1)
            { _contactBlockedUntil = now + 2; CancelInteraction(); }
            return;
        }
        _rider.SetConsoleInteraction(SpacecraftConsoleControl.None, 0);
        var control = _hovered;
        if (control == null || !mouse.leftButton.wasPressedThisFrame && !mouse.rightButton.wasPressedThisFrame
            || !CanRequest(control.Command, state)) return;
        _contactBlockedUntil = 0;
        if (control.FlightInput)
        {
            if (!mouse.leftButton.wasPressedThisFrame && control.Command != "stick") return;
            if (!TryNativePoint(control.DragFrame ?? control.Frame, screen, out _stickGrab, out _)) return;
            _held = control; _heldAt = Time.unscaledTime;
            _rightDrag = control.Command == "stick" && mouse.rightButton.wasPressedThisFrame;
        }
        else if (mouse.leftButton.wasPressedThisFrame)
        { _gesture = control; _gestureAt = Time.unscaledTime; _dispatched = false; }
    }

    private bool CanRequest(string command, ClientSpacecraftView state)
    {
        if (command is "exit" or "stand") return SpacecraftPresentation.CanRequest(command, state, _rider.Attached,
            state.Boarding || !_rider.Attached, state.Flight.Region, false);
        if (!state.IsHost && !state.CrewAccess) return false;
        var pilot = state.Ready && state.Available && _rider.Seated && _rider.Seat == 0;
        if (command is "station-dock" or "station-assist" && !pilot) return false;
        if (command is "stick" or "UP" or "DN" or "YAW L" or "YAW R" or "BANK L" or "BANK R" or "BOOST")
            return pilot && state.Flight.Powered && state.Flight.DoorAmount <= .01f && !state.Flight.DoorOpen
                && state.Flight.Travel == SpacecraftTravel.None;
        return SpacecraftPresentation.CanRequest(command, state, _rider.Attached,
            state.Boarding || !_rider.Attached, state.Flight.Region, false);
    }

    private string BlockedReason(string command, ClientSpacecraftView state)
    {
        var flight = state.Flight;
        if (!state.Ready || !state.Available) return "Waiting for the host's spacecraft state.";
        if (command is not ("exit" or "stand") && !state.IsHost && !state.CrewAccess)
            return "Crew authorization required. Request access at the card reader.";
        if (flight.Travel != SpacecraftTravel.None) return "Stay seated until travel completes.";
        if (command is "station-dock" or "station-assist")
            return DockingBlockedReason(command, state);
        if (command == "dampeners" && state.Station?.Assist == true)
            return "Select HOLD on the chair console before switching off the dampeners.";
        if (flight.Docked && command is "power" or "dampeners" or "gear" or "launch" or "travel" or "moon" or "return" or "engage-route")
            return "Release the docking latch before changing flight systems or starting travel.";
        if (command == "stand") return flight.Region == SpacecraftRegion.Space
            ? "Close the rear ramp or stop the ship before standing."
            : "Seal the rear ramp before walking inside a moving spacecraft.";
        if (command == "exit")
            return flight.Region == SpacecraftRegion.Space ? "Land on a surface before disembarking."
                : !SpacecraftPresentation.Parked(state) ? "Land and stop the spacecraft before disembarking."
                : "Power down the engine before disembarking.";
        if (!_rider.Seated || _rider.Seat > 1) return "The pilot and copilot operate the systems console.";
        if (_rider.Seat == 1 && command is "stick" or "UP" or "DN" or "YAW L" or "YAW R" or "BANK L" or "BANK R" or "BOOST")
            return "The pilot flies; the copilot manages systems and navigation.";
        if (command == "gear") return "Lift clear of the ground before retracting the landing gear.";
        if (command == "door")
            return flight.Region == SpacecraftRegion.Space ? "Stop the ship, seal the inner hatch and equalize the airlock."
                : !SpacecraftPresentation.Parked(state) ? "Land and stop before operating the rear doors."
                : "Power down the engine before opening the rear doors.";
        if (!flight.Powered) return "Start the engine to enable flight controls.";
        if (flight.DoorOpen) return "Close the rear doors before engaging thrust.";
        if (flight.DoorAmount > .01f) return "Rear doors are closing. Thrust unlocks when they seal.";
        if (!state.PilotOccupied && command is "launch" or "travel" or "moon" or "return" or "engage-route")
            return "The pilot must return to the flight seat before navigation can engage.";
        return command switch
        {
            "launch" => "The spacecraft is already in space.",
            "travel" => "Reach space before selecting the world destination.",
            "moon" => "Reach space before selecting the moon destination.",
            "return" => "The spacecraft is already at the island.",
            "engage-route" => "Select a different destination at the navigation table, then return to a flight seat.",
            _ => "Waiting for the seat and flight controls to be ready."
        };
    }

    private void CancelInteraction()
    {
        _held = _gesture = _hovered = null; _dispatched = false;
        _rider.SetPanelFlight(Vector3.zero, Vector3.zero, false);
        _rider.SetConsoleInteraction(SpacecraftConsoleControl.None, 0);
    }

    private Material Finish(string name, Color color)
    {
        var material = ClientDeviceScreenMaterial.CreateDisplayMaterial(name);
        _materials.Add(material);
        ClientDeviceScreenMaterial.SetDisplay(material, color, null);
        return material;
    }

    private static Transform Box(Transform parent, string name, Vector3 position, Vector3 size, Material material)
    {
        var node = GameObject.CreatePrimitive(PrimitiveType.Cube);
        node.name = name; node.layer = parent.gameObject.layer;
        node.transform.SetParent(parent, false); node.transform.localPosition = position; node.transform.localScale = size;
        var collider = node.GetComponent<Collider>();
        if (collider != null) { collider.enabled = false; UnityEngine.Object.Destroy(collider); }
        var renderer = node.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        return node.transform;
    }

    private static RectTransform WorldCanvas(Transform parent, string name, Vector3 position, Quaternion rotation, float width, float height)
    {
        var rect = new GameObject(name).AddComponent<RectTransform>();
        rect.gameObject.layer = parent.gameObject.layer;
        rect.SetParent(parent, false);
        var canvas = rect.gameObject.AddComponent<Canvas>(); canvas.renderMode = RenderMode.WorldSpace;
        rect.localPosition = position; rect.localRotation = rotation;
        rect.localScale = Vector3.one * .001f; rect.sizeDelta = new Vector2(width, height);
        return rect;
    }

    private TextMeshProUGUI PanelText(RectTransform parent, string label, float x, float y, float width, float height, float size, Color color)
    {
        var text = Text(parent, label, (x - width / 2) * 1000 + parent.sizeDelta.x / 2,
            parent.sizeDelta.y / 2 - (y + height / 2) * 1000, width * 1000, height * 1000, size, color);
        text.text = label; text.alignment = TextAlignmentOptions.Center;
        return text;
    }

    private Button AddCrewButton(string title, string command, float x, float width, Action action)
    {
        var node = Node(_strip, title, x, 9, width, 38);
        var graphic = node.gameObject.AddComponent<Image>(); graphic.color = new Color(.13f, .15f, .16f, 1);
        var button = node.gameObject.AddComponent<Button>(); button.targetGraphic = graphic;
        var colors = button.colors;
        colors.highlightedColor = new Color(.62f, .85f, .88f, 1);
        colors.pressedColor = Cyan;
        colors.disabledColor = new Color(.38f, .4f, .42f, .7f);
        button.colors = colors;
        var label = Text(node, "Label", 3, 2, width - 6, 34, 16, Ink);
        label.alignment = TextAlignmentOptions.Center; label.text = title;
        button.onClick.AddListener((UnityAction)new Action(() =>
        {
            var state = _state;
            if (!AcceptsInput || state == null || !button.interactable) return;
            if (command == "recover" ? state.CanRecover && state.ExpeditionActive : CanRequest(command, state))
            { CancelInteraction(); action(); }
        }));
        return button;
    }

    private static bool OverCrewButton(Button button, Vector2 screen)
        => button.gameObject.activeInHierarchy
            && RectTransformUtility.RectangleContainsScreenPoint(button.transform.Cast<RectTransform>(), screen, null);

    private TextMeshProUGUI Text(Transform parent, string name, float x, float y, float width, float height, float size, Color color,
        bool glass = false)
    {
        var node = Node(parent, name, x, y, width, height);
        var text = node.gameObject.AddComponent<TextMeshProUGUI>();
        _typography!.Apply(text, TabletTextRole.Control);
        var canvas = parent.GetComponentInParent<Canvas>();
        if (canvas != null && canvas.renderMode == RenderMode.WorldSpace)
        {
            text.isOverlay = false;
            var source = text.fontSharedMaterial;
            if (source != null)
            {
                var id = source.GetInstanceID();
                var fonts = glass ? _glassFonts : _worldFonts;
                if (!fonts.TryGetValue(id, out var material))
                {
                    material = new Material(source) { name = glass ? "Glass instrument text" : "Cockpit instrument text" };
                    _materials.Add(material);
                    var shader = Shader.Find("TextMeshPro/Distance Field");
                    if (shader != null) material.shader = shader;
                    if (glass)
                    {
                        material.EnableKeyword("OUTLINE_ON");
                        material.SetColor("_OutlineColor", Charcoal);
                        material.SetFloat("_OutlineWidth", .22f);
                        material.SetFloat("_OutlineSoftness", .03f);
                    }
                    SetWorldDepth(material);
                    fonts.Add(id, material);
                }
                text.fontSharedMaterial = material;
            }
        }
        text.fontSize = size; text.color = color; text.richText = false; text.enableWordWrapping = false;
        text.overflowMode = TextOverflowModes.Ellipsis; text.raycastTarget = false;
        text.alignment = TextAlignmentOptions.MidlineLeft;
        if (glass) text.UpdateMeshPadding();
        return text;
    }

    private static RectTransform Node(Transform parent, string name, float x, float y, float width, float height)
    {
        var rect = new GameObject(name).AddComponent<RectTransform>();
        rect.gameObject.layer = parent.gameObject.layer;
        rect.SetParent(parent, false); rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
        rect.anchoredPosition = new Vector2(x, -y); rect.sizeDelta = new Vector2(width, height);
        return rect;
    }

    private static string Number(float value) => float.IsFinite(value) ? value.ToString("0.0", CultureInfo.InvariantCulture) : "—";

    private Material WorldGraphic()
    {
        if (_worldGraphic != null) return _worldGraphic;
        _worldGraphic = new Material(Graphic.defaultGraphicMaterial) { name = "Cockpit display symbology" };
        _materials.Add(_worldGraphic);
        SetWorldDepth(_worldGraphic);
        return _worldGraphic;
    }

    private static void SetWorldDepth(Material material)
    {
        // Crew bodies and the native cockpit must occlude world instruments.
        material.SetInt("unity_GUIZTestMode", (int)CompareFunction.LessEqual);
        if (material.HasProperty("_ZTestMode")) material.SetInt("_ZTestMode", (int)CompareFunction.LessEqual);
        ClientDeviceScreenMaterial.ConfigureOwnedUiMaterial(material);
    }

    private void DestroyPanel()
    {
        CancelInteraction();
        foreach (var display in _displays)
            if (display.Root != null) { display.Root.SetActive(false); UnityEngine.Object.Destroy(display.Root); }
        _displays.Clear();
        if (_world != null) { _world.SetActive(false); UnityEngine.Object.Destroy(_world); }
        _world = null; _body = null; _flightControl = null; _controls.Clear();
        foreach (var material in _materials) if (material != null) UnityEngine.Object.Destroy(material);
        _materials.Clear();
        _worldFonts.Clear(); _glassFonts.Clear(); _worldGraphic = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _visible = false; _state = null;
        DestroyPanel();
        if (_exit != null) _exit.onClick.RemoveAllListeners();
        if (_stand != null) _stand.onClick.RemoveAllListeners();
        if (_recover != null) _recover.onClick.RemoveAllListeners();
        if (_root != null) { _root.SetActive(false); UnityEngine.Object.Destroy(_root); }
        _root = null; _typography?.Dispose(); _typography = null;
    }

    private sealed record PanelControl(int Seat, SpacecraftConsoleControl Kind, string Command, Transform Frame, Vector2 HalfSize,
        Material? Lamp, TextMeshProUGUI? Label, GameObject Highlight, Vector3 Translation, Vector3 Rotation, bool Boost)
    {
        internal Transform? DragFrame;
        internal Transform? FollowFrame;
        internal Image? LampGraphic;
        internal bool FlightInput => Command == "stick" || Translation.sqrMagnitude > 0 || Rotation.sqrMagnitude > 0 || Boost;
        internal int LampState = -1, LabelState = -1;
        internal bool Highlighted;
    }
}
