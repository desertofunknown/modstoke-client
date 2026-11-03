// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Client;

// Kinematics and basis vectors are inertial SI values; angular velocity is radians per second.
// Local axes retain Unity's right/up/forward coordinates without moving the native world.
internal readonly struct OrbitalFrame
{
    private const double BasisTolerance = 1e-10;

    internal OrbitalFrame(OrbitalVector origin, OrbitalVector velocity, OrbitalVector acceleration,
        OrbitalVector right, OrbitalVector up, OrbitalVector forward, OrbitalVector angularVelocity)
    {
        if (!origin.IsFinite || !velocity.IsFinite || !acceleration.IsFinite)
            throw new ArgumentException("Orbital frame position, velocity and acceleration must be finite.");
        if (!angularVelocity.IsFinite || angularVelocity.LengthSquared > 1)
            throw new ArgumentException("Orbital frame angular speed must be finite and at most one radian per second.", nameof(angularVelocity));
        if (!right.IsFinite || !up.IsFinite || !forward.IsFinite
            || Math.Abs(right.LengthSquared - 1) > BasisTolerance
            || Math.Abs(up.LengthSquared - 1) > BasisTolerance
            || Math.Abs(forward.LengthSquared - 1) > BasisTolerance
            || Math.Abs(OrbitalVector.Dot(right, up)) > BasisTolerance
            || Math.Abs(OrbitalVector.Dot(right, forward)) > BasisTolerance
            || Math.Abs(OrbitalVector.Dot(up, forward)) > BasisTolerance
            || (OrbitalVector.Cross(right, up) - forward).LengthSquared > BasisTolerance * BasisTolerance)
            throw new ArgumentException("Orbital frame axes must form a right-handed orthonormal basis.");

        Origin = origin;
        Velocity = velocity;
        Acceleration = acceleration;
        Right = right;
        Up = up;
        Forward = forward;
        AngularVelocity = angularVelocity;
    }

    internal OrbitalVector Origin { get; }
    internal OrbitalVector Velocity { get; }
    internal OrbitalVector Acceleration { get; }
    internal OrbitalVector Right { get; }
    internal OrbitalVector Up { get; }
    internal OrbitalVector Forward { get; }
    internal OrbitalVector AngularVelocity { get; }

    internal OrbitalVector ToInertialPosition(OrbitalVector localPosition)
        => Origin + ToInertialDirection(localPosition);

    internal OrbitalVector ToInertialVelocity(OrbitalVector localPosition, OrbitalVector localVelocity)
        => Velocity + ToInertialDirection(localVelocity)
            + OrbitalVector.Cross(AngularVelocity, ToInertialDirection(localPosition));

    internal OrbitalVector ToLocalPosition(OrbitalVector inertialPosition)
        => ToLocalDirection(inertialPosition - Origin);

    internal OrbitalVector ToLocalVelocity(OrbitalVector inertialPosition, OrbitalVector inertialVelocity)
        => ToLocalDirection(inertialVelocity - Velocity
            - OrbitalVector.Cross(AngularVelocity, inertialPosition - Origin));

    internal OrbitalVector ToLocalDirection(OrbitalVector inertialDirection)
        => new(OrbitalVector.Dot(inertialDirection, Right), OrbitalVector.Dot(inertialDirection, Up),
            OrbitalVector.Dot(inertialDirection, Forward));

    internal OrbitalVector ToInertialDirection(OrbitalVector localDirection)
        => Right * localDirection.X + Up * localDirection.Y + Forward * localDirection.Z;

    internal OrbitalVector ToLocalAcceleration(OrbitalVector localPosition, OrbitalVector localVelocity,
        OrbitalVector inertialAcceleration)
    {
        var offset = ToInertialDirection(localPosition);
        var relativeVelocity = ToInertialDirection(localVelocity);
        // Constant angular velocity has Coriolis and centrifugal terms, but no Euler term.
        return ToLocalDirection(inertialAcceleration - Acceleration
            - 2 * OrbitalVector.Cross(AngularVelocity, relativeVelocity)
            - OrbitalVector.Cross(AngularVelocity, OrbitalVector.Cross(AngularVelocity, offset)));
    }

    // Fictional equatorial surface patch: angle zero is the zero meridian at the epoch.
    // This is a gameplay frame, not a geodetic or IAU orientation model.
    internal static OrbitalFrame Surface(OrbitalState body, OrbitalVector bodyAcceleration,
        double radius, double angle, double angularSpeed)
    {
        if (!body.Position.IsFinite || !body.Velocity.IsFinite || !bodyAcceleration.IsFinite)
            throw new ArgumentException("Surface body position, velocity and acceleration must be finite.");
        if (!double.IsFinite(radius) || radius <= 0 || radius > OrbitalSimulation.MaximumPosition)
            throw new ArgumentException("Surface radius must be positive and within the orbital domain.", nameof(radius));
        if (!double.IsFinite(angle))
            throw new ArgumentException("Surface angle must be finite.", nameof(angle));
        if (!double.IsFinite(angularSpeed) || Math.Abs(angularSpeed) > 1)
            throw new ArgumentException("Surface angular speed must be finite and at most one radian per second.", nameof(angularSpeed));

        double cosine = Math.Cos(angle), sine = Math.Sin(angle);
        var up = new OrbitalVector(cosine, sine, 0);
        var right = new OrbitalVector(-sine, cosine, 0);
        var forward = new OrbitalVector(0, 0, -1);
        var angularVelocity = new OrbitalVector(0, 0, angularSpeed);
        var offset = up * radius;
        var surfaceVelocity = OrbitalVector.Cross(angularVelocity, offset);
        return new OrbitalFrame(body.Position + offset, body.Velocity + surfaceVelocity,
            bodyAcceleration + OrbitalVector.Cross(angularVelocity, surfaceVelocity),
            right, up, forward, angularVelocity);
    }
}
