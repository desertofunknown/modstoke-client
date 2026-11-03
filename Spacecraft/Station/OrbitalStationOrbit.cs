// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;

namespace Modstoke.Client;

internal static class OrbitalStationOrbit
{
    internal const int BodyId = -1001;
    private const double Altitude = 200_000, PhaseOffset = .00016;

    internal static int Index => OrbitalCatalog.Current.Bodies.Count;

    internal static IReadOnlyList<OrbitalBody> Bodies(OrbitalCatalog catalog)
    {
        var bodies = new OrbitalBody[catalog.Bodies.Count + 1];
        for (int i = 0; i < catalog.Bodies.Count; i++) bodies[i] = catalog.Bodies[i];
        foreach (var body in catalog.Bodies)
        {
            if (body.Id != 399) continue;
            bodies[^1] = new(BodyId, "Orbital station", body.Id, 0, 40, Seed(catalog, body.Initial, 0));
            return Array.AsReadOnly(bodies);
        }
        throw new InvalidOperationException("The orbital station's Earth reference is absent from the catalog.");
    }

    internal static OrbitalState Seed(OrbitalCatalog catalog, OrbitalState earth, double time)
    {
        foreach (var body in catalog.Bodies)
        {
            if (body.Id != 399) continue;
            double radius = body.Radius + Altitude;
            double angularSpeed = Math.Sqrt(body.Mu / (radius * radius * radius));
            double angle = Math.IEEERemainder(time * angularSpeed + PhaseOffset, 2 * Math.PI);
            // Only the seed uses the fictional equatorial frame; the solver owns subsequent motion.
            var frame = OrbitalFrame.Surface(earth, default, radius, angle, angularSpeed);
            return new(BodyId, frame.Origin, frame.Velocity);
        }
        throw new InvalidOperationException("The orbital station's Earth reference is absent from the catalog.");
    }
}
