// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using UnityEngine;

namespace Modstoke.Client;

// Low-speed engineering model for the Kestrel's authored dimensions.
// Coefficients are estimates, not measured performance data for a real aircraft.
internal static class ClientAircraftAerodynamics
{
    internal const float SeaLevelDensity = 1.225f;
    internal const float WingArea = 16;
    internal const float AspectRatio = AircraftLayout.Span * AircraftLayout.Span / WingArea;
    internal const float SpanEfficiency = .8f;
    internal const float InducedDragFactor = 1 / (MathF.PI * AspectRatio * SpanEfficiency);
    internal const float MeanChord = WingArea / AircraftLayout.Span;

    internal static float Density(float altitude)
    {
        // Standard tropospheric lapse rate; continue continuously into the
        // isothermal layer. World Y already supplies the aircraft altimeter.
        altitude = Math.Clamp(altitude, -500, 20000);
        var troposphereHeight = MathF.Min(11000, altitude);
        var density = SeaLevelDensity * MathF.Pow(1 - .0065f * troposphereHeight / 288.15f, 4.25588f);
        return altitude <= 11000 ? density : density * MathF.Exp(-(altitude - 11000) / 6341.62f);
    }

    internal static float IndicatedKnots(AircraftFlightState state)
    {
        // At these subsonic speeds equivalent airspeed approximates IAS.
        // Axial pitot response deliberately does not count sideways motion.
        var speed = MathF.Max(0, Vector3.Dot(state.Velocity - state.WindVelocity, state.Rotation * Vector3.forward));
        return speed * MathF.Sqrt(Density(state.Position.y) / SeaLevelDensity) * 1.943844f;
    }

    internal static float NoseSteering(float yaw, float speed) =>
        yaw * Mathf.Lerp(28, 7, Mathf.Clamp01(speed / 28));

    internal static float GroundEffect(float height)
    {
        // Smooth fit to the FAA wing-height/span examples: negligible at one
        // span, about 24% less induced drag at a quarter span, 48% at a tenth.
        var ratio = MathF.Max(0, height) / AircraftLayout.Span;
        return Math.Clamp(1 - MathF.Exp(-4 * MathF.Pow(ratio, .73f)), .15f, 1);
    }

    internal static void Polar(float alpha, float zeroLift, float slope, float stallAngle,
        float inducedFactor, float profileDrag, out float lift, out float drag, out float separation)
    {
        // Remains bounded through reverse flow and tumbles. Blending both
        // coefficients avoids a discontinuous stall impulse or negative drag.
        var stalled = Mathf.Clamp01((MathF.Abs(alpha) - stallAngle) / .23f);
        separation = stalled * stalled * (3 - 2 * stalled);
        var attached = slope * (Math.Clamp(alpha, -stallAngle, stallAngle) - zeroLift);
        var sin = MathF.Sin(alpha);
        lift = Mathf.Lerp(attached, MathF.Sin(2 * alpha), separation);
        drag = Mathf.Lerp(profileDrag + inducedFactor * attached * attached,
            .06f + 1.8f * sin * sin, separation);
    }

    internal static float PropellerInducedSpeed(float airspeed, float usefulPower, float density)
    {
        if (usefulPower <= 0) return 0;
        const float diskArea = MathF.PI * .9f * .9f;
        // Actuator disk: P = 2 rho A vi (V + vi)^2. The static solution is an
        // upper bound; six Newton steps converge without a division by V at rest.
        var powerPerFlow = usefulPower / (2 * density * diskArea);
        var induced = MathF.Cbrt(powerPerFlow);
        for (var i = 0; i < 6; i++)
        {
            var diskSpeed = airspeed + induced;
            induced -= (induced * diskSpeed * diskSpeed - powerPerFlow)
                / (diskSpeed * (airspeed + 3 * induced));
        }
        return MathF.Max(0, induced);
    }
}
