// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using UnityEngine;

namespace Modstoke.Client;

internal enum SpacecraftRegion : byte { Island, Space, TestWorld, Moon }
internal enum SpacecraftTravel : byte { None, Launch, Destination, Return, Moon }
internal enum SpacecraftConsoleControl : byte
{
    None, Power, Dampeners, Door, Flight, Launch, Travel, Return,
    FlightUp, FlightDown, YawLeft, YawRight, BankLeft, BankRight, Boost, Gear, Moon, EngageRoute,
    StationDock, StationAssist
}

// Normalized local thruster and attitude commands. +Z forward, +X right, +Y up.
internal readonly record struct SpacecraftControls(Vector3 Translation, Vector3 Rotation,
    bool Powered, bool Dampeners, bool Boost, bool DoorOpen = true,
    SpacecraftConsoleControl ConsoleControl = SpacecraftConsoleControl.None, float ConsoleReach = 0,
    bool GearDeployed = true)
{
    internal static SpacecraftControls Parked => new(Vector3.zero, Vector3.zero, false, true, false);
}

internal readonly record struct SpacecraftConsolePose(ulong Actor, SpacecraftConsoleControl Control, float Reach)
{
    internal bool Active => Control != SpacecraftConsoleControl.None && Reach > 0;
}

internal readonly record struct SpacecraftFlightState(Vector3 Position, Quaternion Rotation,
    Vector3 Velocity, Vector3 AngularVelocity, bool Powered, bool Grounded, bool Dampeners,
    SpacecraftRegion Region, SpacecraftTravel Travel, float TravelProgress,
    bool DoorOpen = true, float DoorAmount = 1,
    bool GearDeployed = true, float GearAmount = 1,
    Vector3 TranslationInput = default, Vector3 RotationInput = default, bool BoostActive = false,
    SpacecraftSystemState Systems = default, float InnerDoorAmount = 0, float CabinDoorAmount = 1,
    bool Docked = false);

internal sealed record ClientSpacecraftView(bool Ready, bool Available, string Status, string Feedback,
    int Seat, int Occupants, float Distance, SpacecraftFlightState Flight, bool PilotOccupied,
    bool ExpeditionActive, int SurveyCount, bool Loading = false, bool Boarding = false,
    bool CanRecover = false, bool CanBoardPilot = false, bool CanBoardPassenger = false,
    bool CabinWalking = false, bool IsHost = false, bool CrewAccess = true, string CrewRequest = "", string CrewRequestKey = "",
    OrbitalChartView? Orbit = null, OrbitalStationView? Station = null);

internal static class SpacecraftLayout
{
    internal const float GroundY = -3.865f;
    internal static readonly Vector3 BoundsCenter = new(0, .519f, -2.521f);
    internal static readonly Vector3 BoundsSize = new(19.391f, 8.768f, 28.493f);
    internal static readonly Vector3 SpaceOrigin = new(0, 5000, 0);
    internal static readonly Vector3 WorldOrigin = new(0, 2500, 0);
    internal const float WorldRadius = 220;
    internal static readonly Vector3 MoonOrigin = new(3200, 2500, 0);
    internal const float MoonRadius = 220;
    internal static Vector3 SurfaceOrigin(SpacecraftRegion region) => region == SpacecraftRegion.Moon ? MoonOrigin : WorldOrigin;
    internal static float SurfaceRadius(SpacecraftRegion region) => region == SpacecraftRegion.Moon ? MoonRadius : WorldRadius;
    internal static Vector3 Seat(int index) => index switch
    {
        0 => new(.961f, -.64f, 4.210f),
        1 => new(-1.179f, -.64f, 4.210f),
        _ => new(.969f, -.64f, 2.893f)
    };
    // Native chair yaw is the unoccupied basis; the model's seat frame applies
    // the occupied swivel to the chair, collider and crew targets together.
    internal static Quaternion SeatRotation(int index) => Quaternion.Euler(0,
        index switch { 0 => 1.243f, 1 => 73.960f, _ => -40.080f }, 0);
    internal static readonly Vector3 SeatEyeOffset = new(0, .94f, .04f);
    internal static Vector3 Eye(int index) => Seat(index) + SeatRotation(index) * SeatEyeOffset;
    internal static readonly Vector3 Entry = new(0, -2.60f, -17.8f);
    // Static wall mount between the A02 frame and scanner; the -X face carries the keys.
    internal static readonly Bounds CabinEntryControl = new(new(.8685f, .15f, -5.159f), new(.117f, .188f, .208f));
    internal static readonly Bounds EngineeringControl = new(new(-.975f, .15f, -1.12f), new(.09f, .47f, 1.31f));
    internal static readonly Vector3 EngineeringControlPosition = EngineeringControl.center - Vector3.right * EngineeringControl.extents.x;
    internal static readonly Quaternion EngineeringControlRotation = Quaternion.LookRotation(Vector3.right, Vector3.up);
    internal static readonly Bounds EngineeringForwardSupport = new(new(-.9125f, -.3225f, -.94f), new(.083f, .735f, .11f));
    internal static readonly Bounds EngineeringAftSupport = new(new(-.9125f, -.3225f, -1.30f), new(.083f, .735f, .11f));
    internal static readonly Bounds NavigationControl = new(new(-.12f, -.05f, .2825f), new(.99f, .45f, .035f));
    internal static readonly Vector3 NavigationControlPosition = NavigationControl.center + Vector3.forward * NavigationControl.extents.z;
    internal static readonly Quaternion NavigationControlRotation = Quaternion.LookRotation(Vector3.back, Vector3.up);
    internal static readonly Bounds NavigationPortSupport = new(new(-.40f, -.44f, .27f), new(.11f, .52f, .025f));
    internal static readonly Bounds NavigationStarboardSupport = new(new(.16f, -.44f, .27f), new(.11f, .52f, .025f));
    internal static readonly Bounds ApuControl = new(new(2.4645f, .50f, 3.60f), new(.049f, .110f, .058f));
    internal static readonly Vector3 ApuControlPosition = ApuControl.center - Vector3.right * ApuControl.extents.x;
    internal static readonly Bounds ApuControlBacking = new(new(2.4886405f, .495f, 3.60f), new(.016281f, .210f, .150f));
    internal static readonly Bounds ApuServiceCover = new(new(2.42515f, -.955f, 2.81277f), new(.0537f, .200f, .150f));
    internal static AircraftCrewTargets Crew(Transform frame, int seat)
    {
        var p = Seat(seat);
        var rotation = SeatRotation(seat);
        return new(frame.TransformPoint(p), frame.TransformPoint(p + rotation * new Vector3(-.22f, -.70f, .35f)),
            frame.TransformPoint(p + rotation * new Vector3(.22f, -.70f, .35f)),
            frame.TransformPoint(p + rotation * new Vector3(-.28f, .12f, .50f)),
            frame.TransformPoint(p + rotation * new Vector3(.28f, .12f, .50f)), frame.rotation * rotation, false);
    }
}
