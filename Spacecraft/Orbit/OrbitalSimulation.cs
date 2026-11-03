// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;

namespace Modstoke.Client;

// Positions, velocities and gravitational parameters are SI in the catalog's inertial frame.
// The owner calls this on one thread; State returns values at Tick, excluding pending time.
internal sealed class OrbitalSimulation
{
    internal const int MaximumBodies = 2048;
    internal const long MaximumTick = 31_557_600_000; // 1000 Julian years at one second per tick.
    internal const double MaximumPendingSeconds = 3600;
    internal const double MaximumPosition = 1e16, MaximumSpeed = 1e7;
    private const int MaximumStepsPerAdvance = 8;
    private readonly OrbitalBody[] _bodies;
    private OrbitalState[] _states, _workStates;
    private OrbitalVector[] _acceleration, _workAcceleration;

    internal OrbitalSimulation(IReadOnlyList<OrbitalBody> bodies)
    {
        ArgumentNullException.ThrowIfNull(bodies);
        if (bodies.Count is < 1 or > MaximumBodies)
            throw new ArgumentException($"An orbital catalog must contain 1 to {MaximumBodies} bodies.", nameof(bodies));
        _bodies = new OrbitalBody[bodies.Count];
        _states = new OrbitalState[bodies.Count];
        _workStates = new OrbitalState[bodies.Count];
        _acceleration = new OrbitalVector[bodies.Count];
        _workAcceleration = new OrbitalVector[bodies.Count];
        for (int i = 0; i < bodies.Count; i++)
        {
            var body = bodies[i];
            if (body == null || !double.IsFinite(body.Mu) || body.Mu < 0
                || !double.IsFinite(body.Radius) || body.Radius < 0 || body.Radius > MaximumPosition)
                throw new ArgumentException($"Orbital body at index {i} has invalid gravity or radius.", nameof(bodies));
            for (int j = 0; j < i; j++)
            {
                if (_bodies[j].Id == body.Id)
                    throw new ArgumentException($"Orbital body ID {body.Id} occurs more than once.", nameof(bodies));
            }
            _bodies[i] = body;
            if (!ValidState(body.Initial, i))
                throw new ArgumentException($"Orbital body {body.Id} has an invalid initial state.", nameof(bodies));
            _states[i] = body.Initial;
        }
        if (!TryAccelerations(_states, _acceleration, out string? error))
            throw new ArgumentException($"Invalid orbital catalog: {error}", nameof(bodies));
    }

    internal long Tick { get; private set; }
    internal double PendingSeconds { get; private set; }
    internal int Count => _states.Length;
    internal OrbitalState State(int index) => _states[index];
    // Whole pending steps remain backlog. Sampling never extrapolates through that backlog.
    internal double SampleFraction => PendingSeconds < 1 ? PendingSeconds : 0;
    internal double SampleTime => Tick + SampleFraction;
    internal OrbitalVector Acceleration(int index) => _acceleration[index];

    internal OrbitalState Sample(int index)
    {
        var state = _states[index];
        double dt = SampleFraction;
        return new(state.Id, state.Position + (state.Velocity + _acceleration[index] * (.5 * dt)) * dt,
            state.Velocity + _acceleration[index] * dt);
    }

    internal void Advance(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0)
            throw new ArgumentOutOfRangeException(nameof(seconds), "Elapsed orbital time must be finite and nonnegative.");
        double pending = PendingSeconds + seconds;
        if (!double.IsFinite(pending) || pending > MaximumPendingSeconds)
            throw new ArgumentOutOfRangeException(nameof(seconds), $"Pending orbital time cannot exceed {MaximumPendingSeconds} seconds.");
        if (pending > MaximumTick - Tick)
            throw new InvalidOperationException($"Orbital time would exceed the {MaximumTick}-second simulation limit.");

        // Commit the elapsed time before stepping so a failed step retains its full backlog.
        PendingSeconds = pending;
        for (int i = 0; i < MaximumStepsPerAdvance && PendingSeconds >= 1; i++)
        {
            Step();
            PendingSeconds -= 1;
        }
    }

    internal bool Restore(long tick, double pendingSeconds, IReadOnlyList<OrbitalState> states)
    {
        if (tick is < 0 or > MaximumTick || !double.IsFinite(pendingSeconds)
            || pendingSeconds < 0 || pendingSeconds > MaximumPendingSeconds
            || pendingSeconds > MaximumTick - tick || states == null || states.Count != Count) return false;
        for (int i = 0; i < Count; i++)
        {
            var state = states[i];
            if (!ValidState(state, i)) return false;
            _workStates[i] = state;
        }
        if (!TryAccelerations(_workStates, _workAcceleration, out _)) return false;
        CommitState();
        Tick = tick;
        PendingSeconds = pendingSeconds;
        return true;
    }

    internal OrbitalVector GravityAt(OrbitalVector position) => GravityAt(position, false);

    // Terrain can penetrate the catalog's mean-radius sphere. A uniform-density
    // interior keeps contact forces finite and continuous instead of a singularity.
    internal OrbitalVector SampleGravity(OrbitalVector position) => GravityAt(position, true);

    private OrbitalVector GravityAt(OrbitalVector position, bool sample)
    {
        if (!WithinMagnitude(position, MaximumPosition))
            throw new ArgumentOutOfRangeException(nameof(position), "Gravity requires a finite position within the orbital domain.");
        var acceleration = default(OrbitalVector);
        for (int i = 0; i < Count; i++)
        {
            var body = _bodies[i];
            if (body.Mu == 0) continue;
            var separation = (sample ? Sample(i).Position : _states[i].Position) - position;
            double distanceSquared = separation.LengthSquared;
            double radiusSquared = body.Radius * body.Radius;
            if (!sample && (distanceSquared <= 0 || distanceSquared < radiusSquared))
                throw new InvalidOperationException($"Gravity at tick {Tick} is undefined at or inside orbital body {body.Id}.");
            double denominator = sample ? Math.Max(distanceSquared, radiusSquared) : distanceSquared;
            if (denominator <= 0)
                throw new InvalidOperationException($"Gravity is undefined at point body {body.Id}.");
            acceleration += separation * (body.Mu / (denominator * Math.Sqrt(denominator)));
            if (!acceleration.IsFinite)
                throw new InvalidOperationException($"Gravity from orbital body {body.Id} is not finite at tick {Tick}.");
        }
        return acceleration;
    }

    private void Step()
    {
        for (int i = 0; i < Count; i++)
        {
            var state = _states[i];
            var velocity = state.Velocity + _acceleration[i] * .5;
            var next = new OrbitalState(state.Id, state.Position + velocity, velocity);
            if (!ValidState(next, i))
                throw new InvalidOperationException($"Orbital body {state.Id} leaves the position or speed limits during drift at tick {Tick}.");
            _workStates[i] = next;
        }
        if (!TryAccelerations(_workStates, _workAcceleration, out string? error))
            throw new InvalidOperationException($"Cannot advance orbital simulation from tick {Tick}: {error}");
        for (int i = 0; i < Count; i++)
        {
            var state = _workStates[i];
            var velocity = state.Velocity + _workAcceleration[i] * .5;
            if (!WithinMagnitude(velocity, MaximumSpeed))
                throw new InvalidOperationException($"Orbital body {state.Id} exceeds the speed limit during the final kick at tick {Tick}.");
            _workStates[i] = new OrbitalState(state.Id, state.Position, velocity);
        }
        CommitState();
        Tick++;
    }

    private bool TryAccelerations(OrbitalState[] states, OrbitalVector[] acceleration, out string? error)
    {
        error = null;
        Array.Clear(acceleration);
        for (int i = 0; i < Count; i++)
        {
            for (int j = i + 1; j < Count; j++)
            {
                var separation = states[j].Position - states[i].Position;
                double distanceSquared = separation.LengthSquared;
                double radius = _bodies[i].Radius + _bodies[j].Radius;
                if (distanceSquared <= 0 || distanceSquared < radius * radius)
                {
                    error = $"Bodies {_bodies[i].Id} and {_bodies[j].Id} overlap or coincide.";
                    return false;
                }
                if (_bodies[i].Mu == 0 && _bodies[j].Mu == 0) continue;
                var direction = separation / Math.Sqrt(distanceSquared);
                // Each pair shares one separation; a tracer feels gravity but contributes none.
                if (_bodies[j].Mu > 0) acceleration[i] += direction * (_bodies[j].Mu / distanceSquared);
                if (_bodies[i].Mu > 0) acceleration[j] -= direction * (_bodies[i].Mu / distanceSquared);
                if (!acceleration[i].IsFinite || !acceleration[j].IsFinite)
                {
                    error = $"Gravity between bodies {_bodies[i].Id} and {_bodies[j].Id} is not finite.";
                    return false;
                }
            }
        }
        return true;
    }

    private bool ValidState(OrbitalState state, int index)
        => state.Id == _bodies[index].Id && WithinMagnitude(state.Position, MaximumPosition)
            && WithinMagnitude(state.Velocity, MaximumSpeed);

    private static bool WithinMagnitude(OrbitalVector value, double maximum)
        => value.IsFinite && value.LengthSquared <= maximum * maximum;

    private void CommitState()
    {
        (_states, _workStates) = (_workStates, _states);
        (_acceleration, _workAcceleration) = (_workAcceleration, _acceleration);
    }
}
