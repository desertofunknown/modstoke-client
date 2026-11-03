// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftService
{
    // Surface patches use spherical radii and an equatorial epoch meridian,
    // independent of the native game map.
    private const double OrbitAltitude = 200_000;
    private int _earthOrbitIndex = -1, _moonOrbitIndex = -1;
    private OrbitalFrame? _spaceFrame;
    private OrbitalVector _spaceCruiseVelocity;
    private double _spaceFrameTime, _receivedFlightTime;
    private long _frameRevision;

    private void BeginFlightFrame(SpacecraftRegion originRegion = SpacecraftRegion.Island)
    {
        _spaceFrame = null;
        _stationAssist = false;
        _spaceCruiseVelocity = default;
        _spaceFrameTime = 0;
        if (_body?.State.Region == SpacecraftRegion.Space)
        {
            var orbit = SurfaceFrame(originRegion, orbital: true);
            AnchorSpaceFrame(orbit.Origin, orbit.Velocity, orbit);
            _spaceCruiseVelocity = orbit.Velocity;
        }
        _frameRevision = checked(_frameRevision + 1);
    }

    private void AnchorSpaceFrame(OrbitalVector origin, OrbitalVector velocity, OrbitalFrame basis)
    {
        if (origin.LengthSquared > OrbitalSimulation.MaximumPosition * OrbitalSimulation.MaximumPosition
            || velocity.LengthSquared > OrbitalSimulation.MaximumSpeed * OrbitalSimulation.MaximumSpeed)
            throw new InvalidOperationException("The spacecraft reference frame is outside the orbital domain.");
        _spaceFrame = new(origin, velocity, default, basis.Right, basis.Up, basis.Forward, default);
        _spaceFrameTime = _orbits!.SampleTime;
    }

    private OrbitalFrame CurrentSpaceFrame() => SpaceFrameAt(_orbits!.SampleTime);

    private OrbitalFrame SpaceFrameAt(double time)
    {
        var frame = _spaceFrame ?? throw new InvalidOperationException("The spacecraft has no inertial reference frame.");
        var elapsed = time - _spaceFrameTime;
        if (!double.IsFinite(elapsed) || elapsed < 0)
            throw new InvalidOperationException("The spacecraft reference frame is ahead of the orbital clock.");
        // Only the coordinate origin coasts here. PhysX integrates every vessel force.
        var origin = frame.Origin + frame.Velocity * elapsed;
        if (!origin.IsFinite || origin.LengthSquared > OrbitalSimulation.MaximumPosition * OrbitalSimulation.MaximumPosition)
            throw new InvalidOperationException("The spacecraft has left the supported orbital coordinate domain.");
        return new(origin, frame.Velocity, default, frame.Right, frame.Up, frame.Forward, default);
    }

    private void RebaseSpace()
    {
        var body = _body!;
        var flight = body.State;
        if (flight.Region != SpacecraftRegion.Space || flight.Travel != SpacecraftTravel.None) return;
        var offset = flight.Position - SpacecraftLayout.SpaceOrigin;
        if (offset.sqrMagnitude <= 400 * 400 && flight.Velocity.sqrMagnitude <= 200 * 200) return;
        // Do not start another coordinate shift while a crew member is still
        // acknowledging the previous one. Native actors retain the old endpoints briefly.
        foreach (var peer in _seats)
            if (peer?.Session != null && peer.FrameRevision != _frameRevision) return;
        var frame = CurrentSpaceFrame();
        var next = frame.ToInertialPosition(new(offset.x, offset.y, offset.z));
        var velocityShift = flight.Velocity;
        var velocity = frame.ToInertialVelocity(default, new(velocityShift.x, velocityShift.y, velocityShift.z));
        if (!next.IsFinite || next.LengthSquared > OrbitalSimulation.MaximumPosition * OrbitalSimulation.MaximumPosition)
            throw new InvalidOperationException("The spacecraft has left the supported orbital coordinate domain.");
        if (!velocity.IsFinite || velocity.LengthSquared > OrbitalSimulation.MaximumSpeed * OrbitalSimulation.MaximumSpeed)
            throw new InvalidOperationException("The spacecraft has left the supported orbital velocity domain.");
        var revision = checked(_frameRevision + 1);
        RecordMovementEvidence(true);
        body.Rebase(SpacecraftLayout.SpaceOrigin, velocityShift);
        AnchorSpaceFrame(next, velocity, frame);
        _frameRevision = revision;
        _storage?.Update(body);
        _rider?.Rebase(-offset);
        UpdateOrbitalStation(teleport: true);
        Physics.SyncTransforms();
        RecordMovementEvidence(true);
        foreach (var peer in _seats) if (peer != null) peer.DistanceGrace = Now + 10000;
        _nextSnapshot = 0;
    }

    private void FlightAcceleration(SpacecraftFlightState flight, out Vector3 acceleration, out Vector3 hover)
    {
        // The fictional expedition has no astronomical catalog entry yet.
        if (flight.Region == SpacecraftRegion.TestWorld)
        { acceleration = Physics.gravity; hover = -acceleration; return; }
        var simulation = _orbits ?? throw new InvalidOperationException("Spacecraft gravity requires an active orbital world.");
        var frame = flight.Region == SpacecraftRegion.Space ? CurrentSpaceFrame() : SurfaceFrame(flight.Region);
        var position = FramePosition(flight);
        var velocity = new OrbitalVector(flight.Velocity.x, flight.Velocity.y, flight.Velocity.z);
        var gravity = simulation.SampleGravity(frame.ToInertialPosition(position));
        acceleration = LocalAcceleration(frame.ToLocalAcceleration(position, velocity, gravity));
        // On surfaces, hover follows the rotating patch. In space, assist holds
        // the arrival frame's inertial cruise velocity rather than an Earth orbit.
        hover = LocalAcceleration(-frame.ToLocalAcceleration(position, default, gravity));
    }

    private OrbitalFrame SurfaceFrame(SpacecraftRegion region, bool orbital = false)
    {
        var simulation = _orbits ?? throw new InvalidOperationException("Spacecraft navigation requires an active orbital world.");
        var catalog = OrbitalCatalog.Current;
        if (_earthOrbitIndex < 0 || _moonOrbitIndex < 0)
        {
            for (int i = 0; i < catalog.Bodies.Count; i++)
            {
                if (catalog.Bodies[i].Id == 399) _earthOrbitIndex = i;
                if (catalog.Bodies[i].Id == 301) _moonOrbitIndex = i;
            }
        }
        bool moon = region == SpacecraftRegion.Moon;
        bool orbit = orbital || region == SpacecraftRegion.Space;
        int index = moon ? _moonOrbitIndex : _earthOrbitIndex;
        if (index < 0) throw new InvalidOperationException("The spacecraft's reference body is absent from the orbital catalog.");
        var body = catalog.Bodies[index];
        double radius = body.Radius + (orbit ? OrbitAltitude : 0);
        double angularSpeed = orbit ? Math.Sqrt(body.Mu / (radius * radius * radius))
            : 2 * Math.PI / (moon ? OrbitalCatalog.MoonRotationPeriod : OrbitalCatalog.EarthRotationPeriod);
        double angle = Math.IEEERemainder(simulation.SampleTime * angularSpeed, 2 * Math.PI);
        return OrbitalFrame.Surface(simulation.Sample(index), simulation.Acceleration(index), radius, angle, angularSpeed);
    }

    private static Vector3 LocalAcceleration(OrbitalVector value)
    {
        if (!value.IsFinite || value.LengthSquared > 1e8)
            throw new InvalidOperationException("Spacecraft frame acceleration is outside its physical range.");
        return new((float)value.X, (float)value.Y, (float)value.Z);
    }

    private OrbitalSkyView? CelestialView()
    {
        if (_orbits == null || IsClient && (_lastOrbitAt == 0 || Now - _lastOrbitAt > 30000)) return null;
        var region = LocalRegion;
        if (region == SpacecraftRegion.TestWorld) return null;
        double time = _orbits.SampleTime;
        if (region == SpacecraftRegion.Space)
        {
            if (_spaceFrame == null || _body == null || !_body.TelemetryCurrent) return null;
            // Use the same bounded prediction horizon as the replica's native pose.
            if (IsClient) time = _receivedFlightTime + _body.ReplicaAge;
            return new(_orbits, SpaceFrameAt(time), time, SpacecraftLayout.SpaceOrigin);
        }
        return new(_orbits, SurfaceFrame(region), time,
            region == SpacecraftRegion.Island ? Vector3.zero : SpacecraftLayout.MoonOrigin);
    }

    private Vector3 CruiseVelocityOffset()
    {
        if (_stationAssist && TryStationPose(out var station)) return -station.BerthVelocity;
        if (_spaceFrame is not { } frame) return Vector3.zero;
        var value = frame.ToLocalDirection(frame.Velocity - _spaceCruiseVelocity);
        return new((float)value.X, (float)value.Y, (float)value.Z);
    }

    private SpacecraftFlightState FlightTelemetry(SpacecraftFlightState flight) => flight.Docked
        ? flight with { Velocity = Vector3.zero, AngularVelocity = Vector3.zero }
        : flight.Region == SpacecraftRegion.Space
        ? flight with { Velocity = flight.Velocity + CruiseVelocityOffset() } : flight;
}
