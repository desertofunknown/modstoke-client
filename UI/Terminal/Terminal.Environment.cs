// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Modstoke.Sons;

namespace Modstoke.Client;

// These SDK snapshots contain copied values only; no game objects reach the view.
internal sealed record ClientEnvironmentSnapshot
{
    internal static ClientEnvironmentSnapshot Empty { get; } = new();
    internal long SampledAt { get; init; }
    internal ClockSnapshot? Clock { get; init; }
    internal EnvironmentReadStatus ClockStatus { get; init; } = EnvironmentReadStatus.Unavailable;
    internal WeatherSnapshot? Weather { get; init; }
    internal EnvironmentReadStatus WeatherStatus { get; init; } = EnvironmentReadStatus.Unavailable;
    internal SeasonSnapshot? Season { get; init; }
    internal EnvironmentReadStatus SeasonStatus { get; init; } = EnvironmentReadStatus.Unavailable;
}

internal sealed partial class ClientTerminal
{
    private ClientEnvironmentSnapshot _environmentSnapshot = ClientEnvironmentSnapshot.Empty;
    private long _nextEnvironmentSample;
    private long _environmentGeneration = -1;
    private int _environmentScene = -1;
    private bool _environmentVisible;

    // Called from the terminal's Unity-thread update after device visibility is refreshed.
    private void UpdateEnvironment()
    {
        var state = GameRuntime.State;
        var os = _os.Snapshot;
        if (!state.IsInWorld || !os.Ready || os.Generation != _osGeneration)
        {
            ResetEnvironment();
            return;
        }
        if (_environmentGeneration != _osGeneration || _environmentScene != state.SceneEventCount)
        {
            ResetEnvironment();
            _environmentGeneration = _osGeneration;
            _environmentScene = state.SceneEventCount;
        }

        var visible = (_open && _tablet.IsOpen) || _wristWatch.IsInspecting;
        if (!visible)
        {
            if (_environmentVisible)
            {
                _environmentVisible = false;
                ClearEnvironmentReadings();
            }
            return;
        }
        _environmentVisible = true;
        var now = Environment.TickCount64;
        if (now < _nextEnvironmentSample) return;
        _nextEnvironmentSample = now + 1000;

        // Each source has its own availability. A missing season must not hide a valid clock.
        if (!EnvironmentReads.TryReadClock(out var clock, out var clockStatus)) clock = null;
        if (clock != null && !ClientIslandClock.FromSnapshot(clock).Available)
        {
            clock = null;
            clockStatus = EnvironmentReadStatus.Failed;
        }
        if (!EnvironmentReads.TryReadWeather(out var weather, out var weatherStatus)) weather = null;
        if (!EnvironmentReads.TryReadSeason(out var season, out var seasonStatus)) season = null;

        state = GameRuntime.State;
        os = _os.Snapshot;
        if (!state.IsInWorld || !os.Ready || _environmentGeneration != _osGeneration
            || _environmentScene != state.SceneEventCount || os.Generation != _osGeneration)
        {
            ResetEnvironment();
            return;
        }
        _environmentSnapshot = new ClientEnvironmentSnapshot
        {
            SampledAt = Environment.TickCount64,
            Clock = clock, ClockStatus = clockStatus,
            Weather = weather, WeatherStatus = weatherStatus,
            Season = season, SeasonStatus = seasonStatus
        };
        _view?.SetEnvironment(_environmentSnapshot);
    }

    private void ResetEnvironment()
    {
        _environmentGeneration = -1;
        _environmentScene = -1;
        _environmentVisible = false;
        // Preserve the deadline so rapid device/context changes cannot increase native read frequency.
        ClearEnvironmentReadings();
    }

    private void ClearEnvironmentReadings()
    {
        if (ReferenceEquals(_environmentSnapshot, ClientEnvironmentSnapshot.Empty)) return;
        _environmentSnapshot = ClientEnvironmentSnapshot.Empty;
        _view?.SetEnvironment(_environmentSnapshot);
    }
}
