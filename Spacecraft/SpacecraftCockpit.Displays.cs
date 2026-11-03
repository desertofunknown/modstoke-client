// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftCockpit
{
    private sealed class SeatDisplay(int seat, GameObject root)
    {
        internal readonly int Seat = seat;
        internal readonly GameObject Root = root;
        internal TextMeshProUGUI FlightMode = null!, Speed = null!, Heading = null!, VerticalSpeed = null!;
        internal TextMeshProUGUI Engine = null!, Dampeners = null!, Doors = null!, Gear = null!;
        internal RectTransform Horizon = null!;
        internal RectTransform GlassHorizon = null!;
        internal TextMeshProUGUI GlassSpeed = null!, GlassVerticalSpeed = null!, GlassHeading = null!;
        internal TextMeshProUGUI GlassMode = null!, GlassSystems = null!, GlassGuidance = null!;
        internal TextMeshProUGUI GlassDampeners = null!, GlassDoors = null!, GlassGear = null!;
        internal RectTransform GlassCompass = null!;
        internal readonly TextMeshProUGUI[] GlassBearings = new TextMeshProUGUI[9];
        internal int GlassBearing = -1;
        internal TextMeshProUGUI NativeMonitorFuel = null!, NativeMonitorBattery = null!, NativeWarp = null!;
        internal TextMeshProUGUI NativeRoute = null!, NativeAscent = null!, NativeRetro = null!;
        internal Image NativeFuelBar = null!, NativeBatteryBar = null!, NativeWarpBar = null!;
        internal Image[] NativeEnginePlan = null!, NativeGearPlan = null!;
        internal Image NativeRampPlan = null!;
        internal RectTransform NativeReserves = null!, NativeDocking = null!;
        internal TextMeshProUGUI DockRange = null!, DockVelocity = null!, DockAlignment = null!, DockStatus = null!;
        internal TextMeshProUGUI DockX = null!, DockY = null!, DockZ = null!;
        internal PanelControl DockAssist = null!, DockLatch = null!;
    }
}
