// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using UnityEngine;

namespace Modstoke.Client;

// Airframe coordinates are metres: +Z nose, +X starboard, +Y up.
// Origin is the main cabin floor. Positive pitch pulls up; positive roll banks right.
internal readonly record struct AircraftControls(float Pitch, float Roll, float Yaw,
    float Throttle, bool Brake, byte Flaps, bool Engine, bool Lights)
{
    internal static AircraftControls Parked => new(0, 0, 0, 0, true, 0, false, false);
    internal AircraftInteraction Interaction { get; init; }
    internal uint InteractionSequence { get; init; }
}

internal enum AircraftInteraction : byte { None, Engine, Lights, Flaps, Brake, Starter, Throttle }

internal readonly record struct AircraftFlightState(Vector3 Position, Quaternion Rotation,
    Vector3 Velocity, Vector3 AngularVelocity, float Throttle, float Rpm, float Fuel,
    float Health, bool Engine, bool Grounded, bool Stall, byte Flaps, bool Lights,
    float PitchControl, float RollControl, float YawControl,
    // World-space atmospheric velocity in m/s, moving toward this direction, sampled at the center of mass.
    Vector3 WindVelocity, bool Brake, AircraftInteraction Interaction, uint InteractionSequence, float InteractionAge);

// World-space contact points from the rendered controls, shared by local and remote rigs.
internal readonly record struct AircraftCrewTargets(Vector3 Pelvis, Vector3 LeftFoot, Vector3 RightFoot,
    Vector3 LeftHand, Vector3 RightHand, Quaternion FrameRotation, bool PilotReaching)
{
    internal Quaternion? RightReachRotation { get; init; }
    internal float RightHandGrip { get; init; }
    // +Z follows the extended index; +Y faces the palm. The caller blends
    // LeftHand from resting palm to fingertip contact using SmoothStep(LeftHandPress).
    internal Quaternion? LeftReachRotation { get; init; }
    internal float LeftHandPress { get; init; }
}

internal static class AircraftLayout
{
    internal const float Span = 10.6f;
    internal const float Length = 7.4f;
    internal const float WheelRadius = .32f;
    // Conservative exterior envelopes for placement, clearance and crash egress.
    // The cabin envelope is replaced by the hollow shell below for collision.
    internal static readonly (Vector3 Center, Vector3 Size)[] Hulls =
    {
        (new(0, .70f, .05f), new(1.42f, 1.42f, 2.65f)),
        (new(0, .47f, 2.05f), new(1.08f, .94f, 1.35f)),
        (new(0, .55f, -2.6f), new(.46f, .52f, 2.8f)),
        (new(-2.95f, 1.49f, .30f), new(4.7f, .17f, 1.55f)),
        (new(2.95f, 1.49f, .30f), new(4.7f, .17f, 1.55f)),
        (new(0, .64f, -3.55f), new(3.3f, .12f, 1f)),
        (new(0, 1.38f, -3.65f), new(.14f, 1.5f, .75f))
    };
    // Existing Kestrel floor, liner, door jambs and roof, in authored metres.
    internal static readonly (Vector3 Center, Vector3 Size, float Pitch)[] CabinShell =
    {
        (new(0, .025f, -.05f), new(1.05f, .07f, 2.62f), 0),
        (new(0, .58f, -1.32f), new(1.10f, 1.12f, .05f), 0),
        (new(0, 1.25f, -1.32f), new(1.10f, .22f, .05f), 0),
        (new(0, .25f, 1.40f), new(1.12f, .50f, .035f), 0),
        (new(0, .70f, 1.155f), new(1.22f, .58f, .10f), 0),
        (new(0, .98f, 1.40f), new(1.10f, .96f, .06f), 0),
        (new(0, 1.38f, -1.05f), new(1.18f, .06f, .56f), -10),
        (new(0, 1.44f, -.02f), new(1.36f, .06f, 1.60f), 0),
        (new(0, 1.31f, 1.08f), new(1.24f, .06f, .66f), 23)
    };
    internal static Vector3 DoorHinge(int side) => new(side == 0 ? -.71f : .71f, .70f, .98f);
    internal static readonly Vector3 DoorCenter = new(0, .06f, -.705f);
    internal static readonly Vector3 DoorSize = new(.06f, 1.26f, 1.43f);
    internal static Vector3 Seat(int index) => index switch
    {
        0 => new(-.32f, .36f, .58f),
        1 => new(.32f, .36f, .58f),
        _ => new(0, .36f, -.66f)
    };
    internal static Vector3 Eye(int index) => Seat(index) + new Vector3(0, .78f, .05f);
    internal static Vector3 Exit(int index) => new(index == 1 ? 1.8f : -1.8f, -.75f, 0);
    internal static Vector3 Wheel(int index) => index switch
    {
        0 => new(-1.05f, -.68f, .15f),
        1 => new(1.05f, -.68f, .15f),
        _ => new(0, -.68f, 2.15f)
    };
}

internal sealed record ClientAircraftView(bool Ready, bool Available, string Status, string Feedback,
    int Seat, int Occupants, float Distance, AircraftFlightState Flight, bool PilotOccupied, bool CanRecover);
