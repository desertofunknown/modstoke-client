// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftService
{
    private SpacecraftRadiation ShipRadiationEnvironment(SpacecraftFlightState flight)
    {
        if (!OnThread || IsClient || _orbits == null || _orbits.PendingSeconds >= 1
            || flight.Travel != SpacecraftTravel.None
            || flight.Region is not (SpacecraftRegion.Space or SpacecraftRegion.Moon)) return default;
        double time = _orbits.SampleTime;
        if (flight.Region == SpacecraftRegion.Space
            && (_spaceFrame == null || time < _spaceFrameTime)) return default;
        var bodies = OrbitalCatalog.Current.Bodies;
        int sun = -1, earth = -1, moon = -1;
        for (int i = 0; i < bodies.Count; i++)
        {
            if (bodies[i].Id == 10) sun = i;
            else if (bodies[i].Id == 399) earth = i;
            else if (bodies[i].Id == 301) moon = i;
        }
        if (sun < 0 || earth < 0 || flight.Region == SpacecraftRegion.Moon && moon < 0) return default;
        var localPosition = FramePosition(flight);
        var rotation = flight.Rotation;
        double rotationLength = (double)rotation.x * rotation.x + (double)rotation.y * rotation.y
            + (double)rotation.z * rotation.z + (double)rotation.w * rotation.w;
        if (!localPosition.IsFinite || !double.IsFinite(rotationLength)
            || Math.Abs(rotationLength - 1) > .001) return default;
        var frame = flight.Region == SpacecraftRegion.Space ? SpaceFrameAt(time) : SurfaceFrame(flight.Region);
        var position = frame.ToInertialPosition(localPosition);
        if (!position.IsFinite) return default;
        var toSun = _orbits.Sample(sun).Position - position;
        var toEarth = _orbits.Sample(earth).Position - position;
        double distance = toSun.Length, earthDistance = toEarth.Length;
        if (!double.IsFinite(distance) || distance <= bodies[sun].Radius
            || !double.IsFinite(earthDistance) || earthDistance <= bodies[earth].Radius) return default;
        var direction = toSun / distance;
        bool eclipsed = false;
        for (int i = 0; i < bodies.Count; i++)
        {
            if (i == sun) continue;
            double radius = bodies[i].Radius;
            var offset = _orbits.Sample(i).Position - position;
            double tolerance = 0;
            if (flight.Region == SpacecraftRegion.Moon && i == moon)
            {
                // Avoid subtracting heliocentric coordinates at the surface. The
                // relative vector retains the exact spherical patch construction.
                offset = -(frame.Up * radius + frame.ToInertialDirection(localPosition));
                tolerance = radius * 1e-12;
            }
            if (!offset.IsFinite || offset.Length < radius - tolerance) return default;
            double along = OrbitalVector.Dot(offset, direction);
            // Point-source, spherical eclipses omit penumbra and local structure shadows.
            if (along > 0 && along < distance && (offset - direction * along).LengthSquared < radius * radius)
                eclipsed = true;
        }
        var right = rotation * Vector3.right;
        var up = rotation * Vector3.up;
        var forward = rotation * Vector3.forward;
        var inertialRight = frame.ToInertialDirection(new(right.x, right.y, right.z));
        var inertialUp = frame.ToInertialDirection(new(up.x, up.y, up.z));
        var inertialForward = frame.ToInertialDirection(new(forward.x, forward.y, forward.z));
        var localSun = new OrbitalVector(OrbitalVector.Dot(direction, inertialRight),
            OrbitalVector.Dot(direction, inertialUp), OrbitalVector.Dot(direction, inertialForward));
        var earthDirection = toEarth / earthDistance;
        var localEarth = new OrbitalVector(OrbitalVector.Dot(earthDirection, inertialRight),
            OrbitalVector.Dot(earthDirection, inertialUp), OrbitalVector.Dot(earthDirection, inertialForward));
        const double astronomicalUnit = 149_597_870_700;
        double ratio = astronomicalUnit / distance;
        double solar = eclipsed ? 0 : 1361 * ratio * ratio;
        // Uniform Earth longwave, without albedo or other planetary infrared sources.
        double disk = bodies[earth].Radius / earthDistance;
        double infrared = 240 * disk * disk;
        // Flux is incident per total emitting area; material absorption belongs to
        // the thermal model. Dorsal/ventral radiator area counts both faces.
        return new(true, solar * ShipHullRadiationView(localSun), solar * .5 * Math.Abs(localSun.Y),
            infrared * ShipHullRadiationView(localEarth), infrared * .5 * Math.Abs(localEarth.Y));
    }

    private static double ShipHullRadiationView(OrbitalVector direction)
        => .20 * Math.Abs(direction.X) + .25 * Math.Abs(direction.Y) + .05 * Math.Abs(direction.Z);
}
