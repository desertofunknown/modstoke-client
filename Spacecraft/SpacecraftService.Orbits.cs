// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Text.Json.Serialization;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftService
{
    private const int OrbitChunkSize = 10;
    private OrbitalSimulation? _orbits;
    private OrbitalChartView? _orbitView;
    private OrbitalState[]? _incomingOrbits;
    private bool[]? _incomingOrbitChunks;
    private long _incomingOrbitSequence, _incomingOrbitTick, _incomingOrbitAt;
    private double _incomingOrbitPending;
    private long _receivedOrbitSequence, _lastOrbitAt, _orbitSequence;
    private long _orbitViewTick = -1;
    private int _incomingOrbitCount;

    private void ResetOrbits()
    {
        _orbits = null; _orbitView = null; _orbitViewTick = -1;
        _incomingOrbits = null; _incomingOrbitChunks = null; _incomingOrbitCount = 0;
        _incomingOrbitSequence = _incomingOrbitTick = _incomingOrbitAt = 0;
        _receivedOrbitSequence = _lastOrbitAt = _orbitSequence = 0;
        _incomingOrbitPending = 0;
        _earthOrbitIndex = _moonOrbitIndex = -1;
    }

    private void UpdateOrbits()
    {
        if (!_ready) return;
        if (IsClient && (_lastOrbitAt == 0 || Now - _lastOrbitAt > 30_000))
        { _orbitView = null; return; }
        var catalog = OrbitalCatalog.Current;
        _orbits ??= new(OrbitalStationOrbit.Bodies(catalog));
        if (_orbitViewTick == _orbits.Tick && _orbitView != null) return;
        _orbitViewTick = _orbits.Tick;
        var states = new OrbitalState[catalog.Bodies.Count];
        for (int i = 0; i < states.Length; i++) states[i] = _orbits.State(i);
        _orbitView = new(_orbits.Tick, catalog.EpochJulianDateTdb, catalog.Bodies, Array.AsReadOnly(states));
    }

    private void AdvanceOrbits(float dt)
    {
        if (IsClient && (_lastOrbitAt == 0 || Now - _lastOrbitAt > 30_000)) return;
        _orbits ??= new(OrbitalStationOrbit.Bodies(OrbitalCatalog.Current));
        _orbits.Advance(dt);
    }

    private void SendOrbits(Peer peer, long now)
    {
        if (_orbits == null || now < peer.NextOrbitSnapshot) return;
        peer.NextOrbitSnapshot = now + 15_000;
        long sequence = ++_orbitSequence;
        // All chunks are produced synchronously from the same committed tick.
        for (int offset = 0; offset < _orbits.Count; offset += OrbitChunkSize)
            _event?.Orbits(peer, sequence, offset);
    }

    private void ReceiveOrbits(Message message)
    {
        var catalog = OrbitalCatalog.Current;
        if (message.Sequence <= _receivedOrbitSequence || message.OrbitStates == null
            || message.OrbitTotal != catalog.Bodies.Count + 1
            || !message.OrbitCatalog.AsSpan().SequenceEqual(catalog.Fingerprint)) return;
        if (message.Sequence < _incomingOrbitSequence) return;
        if (message.Sequence != _incomingOrbitSequence)
        {
            _incomingOrbitSequence = message.Sequence;
            _incomingOrbitTick = message.OrbitTick;
            _incomingOrbitPending = message.OrbitPending;
            _incomingOrbitAt = Now;
            _incomingOrbits = new OrbitalState[message.OrbitTotal];
            _incomingOrbitChunks = new bool[(message.OrbitTotal + OrbitChunkSize - 1) / OrbitChunkSize];
            _incomingOrbitCount = 0;
        }
        if (message.OrbitTick != _incomingOrbitTick || message.OrbitPending != _incomingOrbitPending
            || Now - _incomingOrbitAt > 5000) return;
        int chunk = message.OrbitOffset / OrbitChunkSize;
        if (_incomingOrbitChunks![chunk]) return;
        for (int i = 0; i < message.OrbitStates.Length; i++)
        {
            int index = message.OrbitOffset + i;
            int id = index < catalog.Bodies.Count ? catalog.Bodies[index].Id : OrbitalStationOrbit.BodyId;
            if (message.OrbitStates[i].Id != id) return;
        }
        Array.Copy(message.OrbitStates, 0, _incomingOrbits!, message.OrbitOffset, message.OrbitStates.Length);
        _incomingOrbitChunks[chunk] = true;
        _incomingOrbitCount += message.OrbitStates.Length;
        if (_incomingOrbitCount != message.OrbitTotal) return;
        var simulation = _orbits ?? new OrbitalSimulation(OrbitalStationOrbit.Bodies(catalog));
        if (!simulation.Restore(message.OrbitTick, message.OrbitPending, _incomingOrbits!)) return;
        _orbits = simulation;
        _receivedOrbitSequence = message.Sequence;
        _lastOrbitAt = Now;
        _orbitViewTick = -1;
        _incomingOrbits = null; _incomingOrbitChunks = null; _incomingOrbitCount = 0;
    }

    private SavedOrbits CaptureOrbits()
    {
        var simulation = _orbits ?? throw new InvalidOperationException("No authoritative orbital world is active.");
        var states = new SavedOrbit[simulation.Count];
        for (int i = 0; i < states.Length; i++)
        {
            var state = simulation.State(i);
            states[i] = new() { Id = state.Id,
                Position = new[] { state.Position.X, state.Position.Y, state.Position.Z },
                Velocity = new[] { state.Velocity.X, state.Velocity.Y, state.Velocity.Z } };
        }
        return new() { Catalog = OrbitalCatalog.Current.Hash, Tick = simulation.Tick,
            PendingSeconds = simulation.PendingSeconds, Bodies = states, StationVersion = 1 };
    }

    private static bool TryRestoreOrbits(SavedOrbits? saved, int worldVersion, out OrbitalSimulation simulation)
    {
        var catalog = OrbitalCatalog.Current;
        simulation = new(OrbitalStationOrbit.Bodies(catalog));
        bool legacy = worldVersion < 5;
        if (saved == null || saved.Catalog != catalog.Hash || saved.Bodies == null
            || saved.StationVersion != (legacy ? (int?)null : 1)
            || saved.Bodies.Length != (legacy ? catalog.Bodies.Count : simulation.Count)) return false;
        var states = new OrbitalState[saved.Bodies.Length];
        for (int i = 0; i < states.Length; i++)
        {
            var state = saved.Bodies[i];
            if (state == null || state.Position?.Length != 3 || state.Velocity?.Length != 3) return false;
            states[i] = new(state.Id, new(state.Position[0], state.Position[1], state.Position[2]),
                new(state.Velocity[0], state.Velocity[1], state.Velocity[2]));
        }
        if (legacy)
        {
            var previous = new OrbitalSimulation(catalog.Bodies);
            if (!previous.Restore(saved.Tick, saved.PendingSeconds, states)) return false;
            Array.Resize(ref states, simulation.Count);
            for (int i = 0; i < catalog.Bodies.Count; i++)
            {
                if (catalog.Bodies[i].Id != 399) continue;
                // Saved states belong to Tick; pending fractional time is sampled after restore.
                states[^1] = OrbitalStationOrbit.Seed(catalog, previous.State(i), saved.Tick);
                break;
            }
        }
        return simulation.Restore(saved.Tick, saved.PendingSeconds, states);
    }

    public sealed class SavedOrbits
    {
        [JsonRequired] public string Catalog { get; set; } = "";
        [JsonRequired] public long Tick { get; set; }
        [JsonRequired] public double PendingSeconds { get; set; }
        [JsonRequired] public SavedOrbit[]? Bodies { get; set; }
        public int? StationVersion { get; set; }
    }

    public sealed class SavedOrbit
    {
        [JsonRequired] public int Id { get; set; }
        [JsonRequired] public double[]? Position { get; set; }
        [JsonRequired] public double[]? Velocity { get; set; }
    }
}
