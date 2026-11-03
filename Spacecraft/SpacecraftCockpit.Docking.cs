// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Globalization;
using TMPro;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftCockpit
{
    private void BuildNativeDocking(SeatDisplay display, RectTransform systems)
    {
        display.NativeDocking = Node(systems, "Station docking", 0, 140, 148, 96);
        var title = Text(display.NativeDocking, "Dock heading", 8, 3, 132, 12, 9, Cyan);
        title.text = "STATION BERTH / m";
        display.DockRange = Text(display.NativeDocking, "Berth range", 8, 17, 64, 14, 9, Ink);
        display.DockVelocity = Text(display.NativeDocking, "Relative speed", 74, 17, 66, 14, 9, Ink);
        display.DockVelocity.alignment = TextAlignmentOptions.MidlineRight;
        display.DockX = Text(display.NativeDocking, "Berth X offset", 8, 32, 44, 13, 8, Muted);
        display.DockY = Text(display.NativeDocking, "Berth Y offset", 52, 32, 44, 13, 8, Muted);
        display.DockZ = Text(display.NativeDocking, "Berth Z offset", 96, 32, 44, 13, 8, Muted);
        display.DockAlignment = Text(display.NativeDocking, "Berth alignment", 8, 46, 68, 13, 9, Ink);
        display.DockStatus = Text(display.NativeDocking, "Latch state", 78, 46, 62, 13, 8, Amber);
        display.DockStatus.alignment = TextAlignmentOptions.MidlineRight;
        display.NativeDocking.gameObject.SetActive(false);
    }

    private void UpdateNativeDockingVisibility(SeatDisplay display, ClientSpacecraftView state)
    {
        var visible = state.Ready && state.Available && state.Flight.Region == SpacecraftRegion.Space
            && (state.Flight.Docked || state.Station is { CollisionReady: true } or { Assist: true });
        if (display.NativeDocking.gameObject.activeSelf == visible) return;
        display.NativeDocking.gameObject.SetActive(visible);
        display.NativeReserves.gameObject.SetActive(!visible);
        _nativeTelemetryAt = 0;
    }

    private void UpdateNativeDocking(SeatDisplay display, ClientSpacecraftView state)
    {
        var contact = state.Station;
        var assistLabel = display.DockAssist.Label!;
        var latchLabel = display.DockLatch.Label!;
        SetNativeText(assistLabel, contact?.Assist == true ? "HOLD" : "MATCH", assistLabel.color);
        SetNativeText(latchLabel, state.Flight.Docked ? "RELEASE" : "LATCH", latchLabel.color);
        if (!display.NativeDocking.gameObject.activeSelf) return;
        SetNativeText(display.DockRange, "R " + DockNumber(contact?.Distance, "0.00") + "m", Ink);
        SetNativeText(display.DockVelocity, "V " + DockNumber(contact?.RelativeSpeed, "0.00") + "m/s", Ink);
        SetNativeText(display.DockX, "X" + DockOffset(contact?.BerthOffset.x), Muted);
        SetNativeText(display.DockY, "Y" + DockOffset(contact?.BerthOffset.y), Muted);
        SetNativeText(display.DockZ, "Z" + DockOffset(contact?.BerthOffset.z), Muted);
        SetNativeText(display.DockAlignment, "ALIGN " + DockNumber(contact?.Alignment, "0.0") + "°", Ink);
        SetNativeText(display.DockStatus, state.Flight.Docked ? "LATCHED" : contact?.CanOperateDock == true ? "READY" : "APPROACH",
            state.Flight.Docked || contact?.CanOperateDock == true ? Cyan : Amber);
    }

    private static string DockNumber(double? value, string format)
        => value is { } number && double.IsFinite(number)
            ? number.ToString(Math.Abs(number) >= 100 ? "0" : format, CultureInfo.InvariantCulture) : "—";

    private static string DockOffset(float? value)
        => value is { } number && float.IsFinite(number)
            ? number.ToString(Mathf.Abs(number) >= 100 ? "+0;-0;0" : "+0.0;-0.0;0.0", CultureInfo.InvariantCulture) : "—";

    private string DockingBlockedReason(string command, ClientSpacecraftView state)
    {
        if (!_rider.Seated || _rider.Seat != 0) return "The seated pilot operates docking controls.";
        if (state.Station is not { } station || state.Flight.Region != SpacecraftRegion.Space)
            return "Approach the station to acquire the local berth.";
        if (command == "station-assist") return state.Flight.Docked ? "Release the latch before matching station velocity."
            : !state.Flight.Powered ? "Start the engine before matching station velocity."
            : "Close the ramp and seat all crew before matching station velocity.";
        if (!state.Flight.GearDeployed || state.Flight.GearAmount < .999f) return "Fully deploy landing gear before operating the latch.";
        if (state.Flight.DoorOpen || state.Flight.DoorAmount > .001f) return "Close the rear ramp before operating the latch.";
        if (state.Flight.Docked) return "Seat all crew before releasing the latch.";
        if (!station.CollisionReady) return "Wait for the station's local berth to become ready.";
        if (station.Distance > .35) return "Align the ship within 0.35 m of the berth.";
        if (station.RelativeSpeed > .3) return "Reduce station-relative speed below 0.3 m/s.";
        if (station.Alignment > 3) return "Align the ship within 3° of the berth.";
        return "Match station rotation and seat all crew before engaging the latch.";
    }
}
