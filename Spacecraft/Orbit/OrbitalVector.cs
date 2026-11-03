// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Client;

internal readonly record struct OrbitalVector(double X, double Y, double Z)
{
    internal bool IsFinite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z);
    internal double LengthSquared => X * X + Y * Y + Z * Z;
    internal double Length => Math.Sqrt(LengthSquared);

    public static OrbitalVector operator +(OrbitalVector first, OrbitalVector second)
        => new(first.X + second.X, first.Y + second.Y, first.Z + second.Z);

    public static OrbitalVector operator -(OrbitalVector first, OrbitalVector second)
        => new(first.X - second.X, first.Y - second.Y, first.Z - second.Z);

    public static OrbitalVector operator -(OrbitalVector value) => new(-value.X, -value.Y, -value.Z);

    public static OrbitalVector operator *(OrbitalVector value, double scale)
        => new(value.X * scale, value.Y * scale, value.Z * scale);

    public static OrbitalVector operator *(double scale, OrbitalVector value) => value * scale;

    public static OrbitalVector operator /(OrbitalVector value, double divisor)
        => new(value.X / divisor, value.Y / divisor, value.Z / divisor);

    internal static double Dot(OrbitalVector first, OrbitalVector second)
        => first.X * second.X + first.Y * second.Y + first.Z * second.Z;

    internal static OrbitalVector Cross(OrbitalVector first, OrbitalVector second)
        => new(first.Y * second.Z - first.Z * second.Y,
            first.Z * second.X - first.X * second.Z,
            first.X * second.Y - first.Y * second.X);
}

internal readonly record struct OrbitalState(int Id, OrbitalVector Position, OrbitalVector Velocity);
