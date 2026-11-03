// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftCockpit
{
    private float _nativeTelemetryAt;

    private static Vector3 MirrorNativePoint(Vector3 point, int seat)
        => seat == 1 ? new Vector3(-.208021671f - point.x, point.y, point.z) : point;

    private static Quaternion NativeRotation(Vector3 normal)
    {
        normal.Normalize();
        var right = Vector3.ProjectOnPlane(Vector3.right, normal).normalized;
        return Quaternion.LookRotation(-normal, Vector3.Cross(right, normal));
    }

    private void BuildNativeControls(SeatDisplay display)
    {
        var seat = display.Seat;
        BuildSeatConsoleControls(display);
        if (seat == 0) AddFlightControl(display);
        // Faces 9550 and 11306 are the recessed monitor lenses; retain their rounded bezel edges.
        // Swap the mirrored lenses so flight remains on the left at either seat.
        var flightCenter = new Vector3(seat == 1 ? .746292f : .481214f, -.433773f, 5.251296f);
        var systemsCenter = new Vector3(seat == 1 ? .481214f : .746292f, -.433773f, 5.251296f);
        BuildFlightDisplay(display, BuildNativeMonitor(display, "FLIGHT", flightCenter));
        var systems = BuildNativeMonitor(display, "SYSTEMS", systemsCenter);
        BuildNativeSystemsPlan(display, systems);
        display.Dampeners = NativeSystem(systems, "DAMP", 8, 44);
        display.Gear = NativeSystem(systems, "GEAR", 98, 44);
        display.Engine = NativeSystem(systems, "ENGINE", 8, 101);
        display.Doors = NativeSystem(systems, "RAMP", 104, 101, 36);
        DisplayPanel(systems, "Reserves rule", 8, 140, 132, 1, new Color(.24f, .29f, .31f, 1));
        display.NativeReserves = Node(systems, "Reserves and route", 0, 140, 148, 96);
        display.NativeMonitorFuel = NativeReserve(display.NativeReserves, "FUEL", 8, out display.NativeFuelBar);
        display.NativeMonitorBattery = NativeReserve(display.NativeReserves, "BATTERY", 28, out display.NativeBatteryBar);
        display.NativeWarp = NativeReserve(display.NativeReserves, "WARP", 48, out display.NativeWarpBar);
        DisplayPanel(display.NativeReserves, "Route rule", 8, 70, 132, 1, Muted);
        display.NativeRoute = Text(display.NativeReserves, "Selected destination", 8, 75, 132, 13, 9, Ink);
        BuildNativeDocking(display, systems);
    }

    private GameObject ControlHighlight(Transform frame, string title, float w, float h, float surfaceOffset = .003f)
    {
        var contacts = WorldCanvas(frame, title + " contact", Vector3.back * surfaceOffset, Quaternion.identity, 160, 160);
        var highlight = Node(contacts, title + " contact highlight", 80 - w / 2, 80 - h / 2, w, h);
        DisplayPanel(highlight, "Top", 0, 0, w, .7f, Ink);
        DisplayPanel(highlight, "Bottom", 0, h - .7f, w, .7f, Ink);
        DisplayPanel(highlight, "Left", 0, 0, .7f, h, Ink);
        DisplayPanel(highlight, "Right", w - .7f, 0, .7f, h, Ink);
        highlight.gameObject.SetActive(false);
        return highlight.gameObject;
    }

    private RectTransform BuildNativeMonitor(SeatDisplay display, string title, Vector3 center)
    {
        var seat = display.Seat;
        var normal = new Vector3(0, .694097f, -.719882f);
        var canvas = WorldCanvas(display.Root.transform, title + " monitor", MirrorNativePoint(center, seat) + normal * .002f,
            NativeRotation(normal), 148, 236);
        DisplayPanel(canvas, title + " screen", 0, 0, 148, 236, Charcoal);
        DisplayPanel(canvas, "Lens top edge", 0, 0, 148, .8f, new Color(.32f, .36f, .38f, 1));
        DisplayPanel(canvas, "Lens left edge", 0, 0, .8f, 236, new Color(.22f, .26f, .28f, 1));
        DisplayPanel(canvas, "Lens bottom edge", 0, 235, 148, 1, Color.black);
        DisplayPanel(canvas, "Lens right edge", 147, 0, 1, 236, Color.black);
        DisplayPanel(canvas, "Header field", 1, 1, 146, 29, new Color(.045f, .07f, .08f, 1));
        var label = Text(canvas, title, 8, 8, 96, 18, 12, Cyan);
        label.text = title;
        var station = Text(canvas, "Station", 108, 8, 32, 18, 8, Muted);
        station.text = seat == 0 ? "PIL" : "COP";
        station.alignment = TextAlignmentOptions.MidlineRight;
        DisplayPanel(canvas, title + " rule", 8, 30, 132, 1, new Color(.24f, .29f, .31f, 1));
        DisplayPanel(canvas, title + " accent", 8, 30, 24, 1, Cyan);
        return canvas;
    }

    private TextMeshProUGUI NativeReserve(RectTransform screen, string title, float y, out Image bar)
    {
        var label = Text(screen, title, 8, y, 70, 13, 9, Muted);
        label.text = title;
        var value = Text(screen, title + " value", 78, y - 1, 62, 14, 11, Ink);
        value.alignment = TextAlignmentOptions.MidlineRight;
        DisplayPanel(screen, title + " track", 8, y + 14, 132, 3, new Color(.12f, .15f, .17f, 1));
        bar = Node(screen, title + " reserve", 8, y + 14, 132, 3).gameObject.AddComponent<Image>();
        bar.color = Cyan;
        bar.material = WorldGraphic();
        bar.raycastTarget = false;
        bar.rectTransform.localScale = new Vector3(0, 1, 1);
        for (var tick = 1; tick < 4; tick++)
            DisplayPanel(screen, title + " division", 8 + tick * 33, y + 14, 1, 3, Charcoal);
        return value;
    }

    private void BuildNativeSystemsPlan(SeatDisplay display, RectTransform screen)
    {
        var outline = new Color(.42f, .58f, .61f, 1);
        var detail = new Color(.22f, .32f, .35f, 1);
        NativePlanLine(screen, "Hull plan", outline, new[] { new Vector2(69, 40), new Vector2(79, 40),
            new Vector2(88, 56), new Vector2(91, 86), new Vector2(100, 99), new Vector2(100, 126),
            new Vector2(88, 130), new Vector2(60, 130), new Vector2(48, 126), new Vector2(48, 99),
            new Vector2(57, 86), new Vector2(60, 56), new Vector2(69, 40) });
        NativePlanLine(screen, "Flight deck plan", outline, new[] { new Vector2(69, 47), new Vector2(79, 47),
            new Vector2(83, 62), new Vector2(65, 62), new Vector2(69, 47) });
        NativePlanLine(screen, "Cargo bay plan", detail, new[] { new Vector2(63, 71), new Vector2(85, 71),
            new Vector2(85, 116), new Vector2(63, 116), new Vector2(63, 71) });
        DisplayPanel(screen, "Bay bulkhead", 63, 86, 22, .6f, detail);
        DisplayPanel(screen, "Hull centreline", 74, 65, .6f, 53, detail);
        for (var side = 0; side < 2; side++)
        {
            var x = side == 0 ? 51 : 90;
            NativePlanLine(screen, "Engine pod plan", outline, new[] { new Vector2(x, 96), new Vector2(x + 7, 92),
                new Vector2(x + 7, 122), new Vector2(x, 122), new Vector2(x, 96) });
        }
        NativePlanLine(screen, "Gear leader", detail, new[] { new Vector2(140, 74), new Vector2(95, 74),
            new Vector2(87, 98) });
        NativePlanLine(screen, "Engine leader", detail, new[] { new Vector2(8, 130), new Vector2(41, 130),
            new Vector2(54, 116) });
        NativePlanLine(screen, "Ramp leader", detail, new[] { new Vector2(140, 130), new Vector2(106, 130),
            new Vector2(98, 135), new Vector2(74, 135), new Vector2(74, 126) });
        // All pods share the ship's power state; there is no per-engine health telemetry.
        display.NativeEnginePlan = new[] { NativePlanMark(screen, "Port outer engine", 52, 110, 4, 10),
            NativePlanMark(screen, "Port inner engine", 62, 117, 4, 8),
            NativePlanMark(screen, "Starboard inner engine", 82, 117, 4, 8),
            NativePlanMark(screen, "Starboard outer engine", 92, 110, 4, 10) };
        display.NativeGearPlan = new[] { NativePlanMark(screen, "Nose gear", 72, 65, 4, 3),
            NativePlanMark(screen, "Port gear", 59, 97, 4, 3),
            NativePlanMark(screen, "Starboard gear", 85, 97, 4, 3) };
        display.NativeRampPlan = NativePlanMark(screen, "Aft ramp", 69, 124, 10, 3);
    }

    private void NativePlanLine(RectTransform screen, string name, Color color, Vector2[] points)
    {
        for (var i = 1; i < points.Length; i++)
        {
            var delta = points[i] - points[i - 1];
            var line = Node(screen, name, points[i - 1].x, points[i - 1].y, delta.magnitude, .7f);
            line.pivot = new Vector2(0, .5f);
            line.localRotation = Quaternion.Euler(0, 0, -Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
            var image = line.gameObject.AddComponent<Image>();
            image.color = color; image.material = WorldGraphic(); image.raycastTarget = false;
        }
    }

    private Image NativePlanMark(RectTransform screen, string name, float x, float y, float width, float height)
    {
        var image = Node(screen, name, x, y, width, height).gameObject.AddComponent<Image>();
        image.color = Muted; image.material = WorldGraphic(); image.raycastTarget = false;
        return image;
    }

    private TextMeshProUGUI NativeSystem(RectTransform screen, string title, float x, float y, float width = 42)
    {
        var label = Text(screen, title, x, y, width, 12, 8, Muted);
        label.text = title;
        var value = Text(screen, title + " state", x, y + 12, width, 15, 10, Ink);
        if (x > 74) label.alignment = value.alignment = TextAlignmentOptions.MidlineRight;
        value.enableAutoSizing = true; value.fontSizeMin = 7; value.fontSizeMax = 10;
        return value;
    }

    private void UpdateNativeControls(ClientSpacecraftView state)
    {
        UpdateFlightControl();
        UpdateSeatConsoleControls();
        foreach (var display in _displays) UpdateNativeDockingVisibility(display, state);
        var flight = state.Flight;
        var input = _rider.Controls;
        var known = state.Ready && state.Available;
        foreach (var control in _controls)
        {
            var localCrew = _rider.Seated && _rider.Seat == control.Seat;
            var pose = _body?.ConsolePose(control.Seat) ?? default;
            var contact = localCrew ? input.ConsoleControl : pose.Control;
            var reach = localCrew ? input.ConsoleReach : pose.Reach;
            var on = control.Kind switch
            {
                SpacecraftConsoleControl.Power => flight.Powered,
                SpacecraftConsoleControl.Dampeners => flight.Dampeners,
                SpacecraftConsoleControl.Door => flight.DoorOpen && flight.DoorAmount >= .99f,
                SpacecraftConsoleControl.Gear => flight.GearDeployed && flight.GearAmount >= .99f,
                SpacecraftConsoleControl.Launch => flight.Travel == SpacecraftTravel.Launch,
                SpacecraftConsoleControl.Travel => flight.Travel == SpacecraftTravel.Destination,
                SpacecraftConsoleControl.Return => flight.Travel == SpacecraftTravel.Return,
                SpacecraftConsoleControl.Moon => flight.Travel == SpacecraftTravel.Moon,
                SpacecraftConsoleControl.EngageRoute => flight.Travel != SpacecraftTravel.None,
                SpacecraftConsoleControl.StationAssist => state.Station?.Assist == true,
                SpacecraftConsoleControl.StationDock => flight.Docked,
                SpacecraftConsoleControl.FlightUp => flight.TranslationInput.y > .01f,
                SpacecraftConsoleControl.FlightDown => flight.TranslationInput.y < -.01f,
                SpacecraftConsoleControl.YawLeft => flight.RotationInput.y < -.01f,
                SpacecraftConsoleControl.YawRight => flight.RotationInput.y > .01f,
                SpacecraftConsoleControl.BankLeft => flight.RotationInput.z > .01f,
                SpacecraftConsoleControl.BankRight => flight.RotationInput.z < -.01f,
                SpacecraftConsoleControl.Boost => flight.BoostActive,
                SpacecraftConsoleControl.Flight => flight.TranslationInput.sqrMagnitude > .0001f || flight.RotationInput.sqrMagnitude > .0001f,
                _ => false
            };
            var moving = control.Kind == SpacecraftConsoleControl.Door
                ? flight.DoorOpen ? flight.DoorAmount < .99f : flight.DoorAmount > .01f
                : control.Kind == SpacecraftConsoleControl.Gear
                    && (flight.GearDeployed ? flight.GearAmount < .99f : flight.GearAmount > .01f);
            var enabled = localCrew ? CanRequest(control.Command, state) : known;
            var hovered = localCrew && ReferenceEquals(control, _hovered);
            var labelState = hovered ? enabled ? 3 : 2 : enabled ? 1 : 0;
            if (control.Label != null && control.LabelState != labelState)
            {
                control.LabelState = labelState;
                control.Label.color = labelState switch { 3 => Cyan, 2 => Amber, 1 => Ink, _ => Muted };
            }
            var highlighted = hovered || known && contact == control.Kind && reach > .85f;
            if (control.Highlighted != highlighted)
            {
                control.Highlighted = highlighted;
                control.Highlight.SetActive(highlighted);
            }
            var lampState = !known ? 0 : moving ? 3 : on ? 2 : 1;
            if (control.LampState != lampState)
            {
                control.LampState = lampState;
                var color = lampState switch { 3 => Amber, 2 => Cyan, 1 => Muted * .30f, _ => Muted * .16f };
                if (control.Lamp != null) ClientDeviceScreenMaterial.SetDisplay(control.Lamp, color, null);
                if (control.LampGraphic != null) control.LampGraphic.color = color;
            }
        }
        UpdateNativeTelemetry(state);
    }

    private void UpdateNativeTelemetry(ClientSpacecraftView state)
    {
        var now = Time.unscaledTime;
        if (now < _nativeTelemetryAt) return;
        _nativeTelemetryAt = now + .12f;
        foreach (var display in _displays) UpdateNativeTelemetry(display, state);
    }

    private void UpdateNativeTelemetry(SeatDisplay display, ClientSpacecraftView state)
    {
        var flight = state.Flight;
        var known = state.Ready && state.Available;
        var systemsKnown = known && flight.Systems.Ready;
        UpdateFlightDisplay(display, state, known);
        UpdateNativeDocking(display, state);
        SetNativeReserve(display.NativeMonitorFuel, display.NativeFuelBar, flight.Systems.Fuel01, systemsKnown, true);
        SetNativeReserve(display.NativeMonitorBattery, display.NativeBatteryBar, flight.Systems.Battery01, systemsKnown, true);
        SetNativeReserve(display.NativeWarp, display.NativeWarpBar, flight.Systems.WarpCharge01, systemsKnown, false);
        SetNativeText(display.NativeRoute, systemsKnown ? "TO " + SpacecraftPresentation.Region(flight.Systems.SelectedDestination).ToUpperInvariant()
            : "SYSTEMS SYNC", systemsKnown ? Ink : Amber);
        var journey = flight.Travel switch
        {
            SpacecraftTravel.Launch => "LAUNCH",
            SpacecraftTravel.Destination => "WORLD",
            SpacecraftTravel.Return => "ISLAND",
            SpacecraftTravel.Moon => "MOON",
            _ => "STANDBY"
        };
        SetNativeText(display.FlightMode, !known ? "FLIGHT SYNC" : flight.Docked ? "DOCKED" : flight.Travel != SpacecraftTravel.None
            ? journey + " " + NativePercentage(flight.TravelProgress, true)
            : SpacecraftPresentation.Parked(state) ? "PARKED" : "MANUAL FLIGHT", known ? Ink : Amber);
        // These are accepted ship-axis commands, not inferred acceleration or a retrograde autopilot.
        SetNativeText(display.NativeAscent, "ASCENT", !known ? Muted : flight.TranslationInput.y > .01f ? Amber : Muted);
        SetNativeText(display.NativeRetro, "RETRO", !known ? Muted : flight.TranslationInput.z < -.01f ? Amber : Muted);
        SetNativeText(display.Engine, !known ? "SYNC" : flight.Powered ? "ON" : "OFF", known && flight.Powered ? Cyan : Muted);
        SetNativeText(display.Dampeners, !known ? "SYNC" : flight.Dampeners ? "ON" : "OFF", !known ? Muted : flight.Dampeners ? Cyan : Amber);
        var sealedDoor = !flight.DoorOpen && flight.DoorAmount <= .01f;
        SetNativeText(display.Doors, !known ? "SYNC" : sealedDoor ? "SEALED"
            : flight.DoorOpen ? flight.DoorAmount >= .99f ? "OPEN" : "OPENING" : "CLOSING",
            !known ? Muted : sealedDoor ? Cyan : Amber);
        SetNativeText(display.Gear, !known ? "SYNC" : flight.GearDeployed ? flight.GearAmount >= .99f ? "DOWN" : "EXTENDING"
            : flight.GearAmount <= .01f ? "UP" : "RETRACTING",
            !known ? Muted : flight.GearDeployed && flight.GearAmount >= .99f ? Cyan : Amber);
        var engineColor = display.Engine.color;
        var gearColor = display.Gear.color;
        var rampColor = display.Doors.color;
        foreach (var mark in display.NativeEnginePlan)
            if (mark.color != engineColor) mark.color = engineColor;
        foreach (var mark in display.NativeGearPlan)
            if (mark.color != gearColor) mark.color = gearColor;
        if (display.NativeRampPlan.color != rampColor) display.NativeRampPlan.color = rampColor;
        UpdateGlassDisplay(display, state, known);
    }

    private static void SetNativeText(TextMeshProUGUI text, string value, Color color)
    {
        if (text.text != value) text.text = value;
        if (text.color != color) text.color = color;
    }

    private static void SetNativeReserve(TextMeshProUGUI text, Image bar, float value, bool known, bool warnLow)
    {
        known &= float.IsFinite(value);
        var color = !known ? Muted : warnLow && value <= .05f ? Red : warnLow && value <= .15f ? Amber : Cyan;
        SetNativeText(text, NativePercentage(value, known), known && (!warnLow || value > .15f) ? Ink : color);
        if (bar.color != color) bar.color = color;
        var scale = new Vector3(known ? Mathf.Clamp01(value) : 0, 1, 1);
        if (bar.rectTransform.localScale != scale) bar.rectTransform.localScale = scale;
    }

    private static string NativePercentage(float value, bool known)
        => known && float.IsFinite(value) ? (Mathf.Clamp01(value) * 100).ToString("0", CultureInfo.InvariantCulture) + "%" : "SYNC";

    private PanelControl? FindNativeControl(Vector2 screen)
    {
        PanelControl? selected = null;
        var nearest = 2.5f;
        foreach (var control in _controls)
        {
            if (control.Seat != _rider.Seat || !TryNativePoint(control.Frame, screen, out var point, out var distance) || distance >= nearest
                || Mathf.Abs(point.x) > control.HalfSize.x || Mathf.Abs(point.y) > control.HalfSize.y) continue;
            nearest = distance;
            selected = control;
        }
        return selected != null && CanSeeControl(selected) ? selected : null;
    }

    private bool CanSeeControl(PanelControl control)
    {
        var camera = _rider.Camera;
        return control.Seat == _rider.Seat && camera != null && camera && camera.isActiveAndEnabled && control.Frame && control.Frame.gameObject.activeInHierarchy
            && _body?.CanSeeControl(camera.transform.position, control.Frame.position, _rider.Seat,
                TheForest.Utils.LocalPlayer.Transform, TheForest.Utils.LocalPlayer.Rigidbody) == true;
    }

    private bool TryNativePoint(Transform frame, Vector2 screen, out Vector2 point, out float distance)
    {
        point = default; distance = 0;
        var camera = _rider.Camera;
        if (camera == null || !camera || !frame || !frame.gameObject.activeInHierarchy) return false;
        var ray = camera.ScreenPointToRay(new Vector3(screen.x, screen.y, 0));
        if (Vector3.Dot(ray.direction, frame.forward) <= .05f) return false;
        var plane = new Plane(frame.forward, frame.position);
        if (!plane.Raycast(ray, out distance) || distance < 0 || distance > 2.5f) return false;
        var local = frame.InverseTransformPoint(ray.GetPoint(distance));
        point = new Vector2(local.x, local.y);
        return true;
    }

}
