// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using UnityEngine;

namespace Modstoke.Client;

internal static class SpacecraftSeatConsole
{
    internal const int ButtonCount = 18;
    internal const float KeyTravel = .0012f;
    internal static readonly Vector2 HalfSize = new(.016f, .012f);
    internal static readonly Vector3 MountOffset = new(-.316f, .110f, .170f);
    internal static readonly Quaternion PanelRotation = Quaternion.AngleAxis(-7, Vector3.forward)
        * Quaternion.AngleAxis(-7, Vector3.right);

    internal readonly record struct Button(SpacecraftConsoleControl Kind, string Command, string Title,
        Vector3 Translation = default, Vector3 Rotation = default, bool Boost = false);

    internal static readonly Button[] Buttons =
    {
        new(SpacecraftConsoleControl.Power, "power", "ENGINE"),
        new(SpacecraftConsoleControl.Dampeners, "dampeners", "DAMP"),
        new(SpacecraftConsoleControl.Gear, "gear", "GEAR"),
        new(SpacecraftConsoleControl.Door, "door", "RAMP"),
        new(SpacecraftConsoleControl.Launch, "launch", "LAUNCH"),
        new(SpacecraftConsoleControl.EngageRoute, "engage-route", "ROUTE"),
        new(SpacecraftConsoleControl.Travel, "travel", "WORLD"),
        new(SpacecraftConsoleControl.Moon, "moon", "MOON"),
        new(SpacecraftConsoleControl.Return, "return", "ISLAND"),
        new(SpacecraftConsoleControl.StationAssist, "station-assist", "MATCH"),
        new(SpacecraftConsoleControl.StationDock, "station-dock", "LATCH"),
        new(SpacecraftConsoleControl.Boost, "BOOST", "BOOST", Vector3.forward, Boost: true),
        new(SpacecraftConsoleControl.FlightUp, "UP", "UP", Vector3.up),
        new(SpacecraftConsoleControl.YawLeft, "YAW L", "YAW L", Rotation: Vector3.down),
        new(SpacecraftConsoleControl.BankLeft, "BANK L", "BANK L", Rotation: Vector3.forward),
        new(SpacecraftConsoleControl.FlightDown, "DN", "DOWN", Vector3.down),
        new(SpacecraftConsoleControl.YawRight, "YAW R", "YAW R", Rotation: Vector3.up),
        new(SpacecraftConsoleControl.BankRight, "BANK R", "BANK R", Rotation: Vector3.back)
    };

    internal static Vector3 Position(int index) => new((index % 3 - 1) * .050f, .0044f,
        .100f - index / 3 * .040f);

    internal static int Index(SpacecraftConsoleControl kind) => kind switch
    {
        SpacecraftConsoleControl.Power => 0,
        SpacecraftConsoleControl.Dampeners => 1,
        SpacecraftConsoleControl.Gear => 2,
        SpacecraftConsoleControl.Door => 3,
        SpacecraftConsoleControl.Launch => 4,
        SpacecraftConsoleControl.EngageRoute => 5,
        SpacecraftConsoleControl.Travel => 6,
        SpacecraftConsoleControl.Moon => 7,
        SpacecraftConsoleControl.Return => 8,
        SpacecraftConsoleControl.StationAssist => 9,
        SpacecraftConsoleControl.StationDock => 10,
        SpacecraftConsoleControl.Boost => 11,
        SpacecraftConsoleControl.FlightUp => 12,
        SpacecraftConsoleControl.YawLeft => 13,
        SpacecraftConsoleControl.BankLeft => 14,
        SpacecraftConsoleControl.FlightDown => 15,
        SpacecraftConsoleControl.YawRight => 16,
        SpacecraftConsoleControl.BankRight => 17,
        _ => -1
    };
}
