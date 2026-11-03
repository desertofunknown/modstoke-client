// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Modstoke.Client;

internal sealed record ClientTabletAppActivity(ClientTerminalApp App, string State, long ForegroundSeconds, int Opens);

internal sealed record ClientTabletRuntimeSnapshot
{
    internal long UptimeSeconds { get; init; }
    internal long ProcessedCommands { get; init; }
    internal long RejectedCommands { get; init; }
    internal int PendingCommands { get; init; }
    internal int QueueCapacity { get; init; } = 128;
    internal double LastSliceMilliseconds { get; init; }
    internal IReadOnlyList<ClientTabletAppActivity> Apps { get; init; } = Array.Empty<ClientTabletAppActivity>();
}

internal sealed partial class ClientTabletOperatingSystem
{
    private const int CommandsPerSlice = 32;
    private const double SliceMilliseconds = 4;
    private static readonly ClientTabletPreferencesSnapshot DefaultDevicePreferences =
        new(ClientTabletTheme.Field, 1f, true, .35f, ClientTabletTone.Pulse, false);
    private readonly Dictionary<ClientTerminalApp, AppActivity> _appActivity = new();
    private ClientTabletPreferences? _devicePreferenceStore;
    private PreferenceChange? _pendingPreferenceChange;
    private long _runtimeStarted;
    private long _processedCommands;
    private long _rejectedCommands;
    private long _lastRuntimePublication;
    private double _lastSliceMilliseconds;
    private ClientTerminalApp? _foregroundApp;
    private long _foregroundSince;

    private ClientTabletPreferencesSnapshot DevicePreferences => _devicePreferenceStore?.Snapshot ?? DefaultDevicePreferences;

    private bool RuntimeDiagnosticsVisible => _ready && _visible && !_locked && !_taskSwitcherOpen
        && !_contentObscured && _activeApp == ClientTerminalApp.Settings;

    internal bool QueuePreference(string key, string value, out string status)
    {
        lock (_gate)
        {
            if (_devicePreferenceStore == null || !Accepting)
            {
                status = "The device operating system is unavailable.";
                return RejectCommand();
            }
            if (!_devicePreferenceStore.ValidateChange(key, value, out status)) return RejectCommand();
            if (_operations.Count >= QueueLimit)
            {
                status = "The device command queue is full; try again.";
                return RejectCommand();
            }
            _operations.Enqueue(new Operation(OperationKind.Preference, _requestedGeneration,
                ClientTerminalApp.Home, false, key, Target: value));
            _devicePreferenceStore.PreferenceQueued();
            status = "Tablet preference queued for saving.";
            _wakeup.Set();
            return true;
        }
    }

    // Called by the watch command handler under _gate. The worker saves it immediately after
    // releasing the gate, before processing another watch command derived from the saved state.
    private void QueueWatchPreference(string key, string value)
    {
        if (_devicePreferenceStore == null || _pendingPreferenceChange.HasValue
            || !_devicePreferenceStore.ValidateChange(key, value, out _))
        {
            _rejectedCommands++;
            return;
        }
        _pendingPreferenceChange = new PreferenceChange(key, value);
        _devicePreferenceStore.PreferenceQueued();
    }

    private void ProcessPreferencePersistence()
    {
        PreferenceChange? change;
        lock (_gate)
        {
            change = _pendingPreferenceChange;
            _pendingPreferenceChange = null;
        }
        if (change is not { } pending || _devicePreferenceStore == null) return;
        try { _devicePreferenceStore.Change(pending.Key, pending.Value, out _); }
        finally { _devicePreferenceStore.PreferenceCompleted(); }
    }

    private bool RejectCommand()
    {
        lock (_gate) _rejectedCommands++;
        return false;
    }

    private void EnqueuePendingReset()
    {
        if (_pendingResetGeneration is not { } generation || _operations.Count >= QueueLimit) return;
        _operations.Enqueue(new Operation(OperationKind.Reset, generation, ClientTerminalApp.Home, false, string.Empty));
        _pendingResetGeneration = null;
    }

    private AppActivity ActivityFor(ClientTerminalApp app)
    {
        if (!_appActivity.TryGetValue(app, out var activity))
            _appActivity.Add(app, activity = new AppActivity());
        return activity;
    }

    private void RecordAppOpen(ClientTerminalApp app)
    {
        var activity = ActivityFor(app);
        if (activity.Opens < int.MaxValue) activity.Opens++;
    }

    private void UpdateForegroundActivity()
    {
        var now = Stopwatch.GetTimestamp();
        if (_foregroundApp is { } previous && _appActivity.TryGetValue(previous, out var activity))
            activity.ForegroundTicks += Math.Max(0, now - _foregroundSince);
        _foregroundApp = _ready && !_disposed && _visible && !_locked && !_taskSwitcherOpen && !_contentObscured
            ? _activeApp : null;
        _foregroundSince = now;
        if (_foregroundApp is { } current)
        {
            var currentActivity = ActivityFor(current);
            if (currentActivity.Opens == 0) currentActivity.Opens = 1;
        }
    }

    private void ResetRuntimeActivity()
    {
        _foregroundApp = null;
        _foregroundSince = Stopwatch.GetTimestamp();
        _appActivity.Clear();
    }

    private bool UpdateRuntimeDiagnostics()
    {
        if (!RuntimeDiagnosticsVisible) return false;
        var now = Stopwatch.GetTimestamp();
        if (now - _lastRuntimePublication < Stopwatch.Frequency) return false;
        _lastRuntimePublication = now;
        return true;
    }

    private ClientTabletRuntimeSnapshot CaptureRuntime()
    {
        UpdateForegroundActivity();
        var apps = new List<ClientTabletAppActivity>(_appActivity.Count);
        foreach (var app in Enum.GetValues<ClientTerminalApp>())
            if (_appActivity.TryGetValue(app, out var activity))
                apps.Add(new ClientTabletAppActivity(app, _foregroundApp == app ? "Active" : "Suspended",
                    activity.ForegroundTicks / Stopwatch.Frequency, activity.Opens));
        apps.Sort((left, right) =>
        {
            if ((left.App == _activeApp) != (right.App == _activeApp)) return left.App == _activeApp ? -1 : 1;
            var leftRecent = _recentApps.IndexOf(left.App);
            var rightRecent = _recentApps.IndexOf(right.App);
            var order = (leftRecent < 0 ? int.MaxValue : leftRecent).CompareTo(rightRecent < 0 ? int.MaxValue : rightRecent);
            return order != 0 ? order : left.App.CompareTo(right.App);
        });
        return new ClientTabletRuntimeSnapshot
        {
            UptimeSeconds = _started ? Math.Max(0, Stopwatch.GetTimestamp() - _runtimeStarted) / Stopwatch.Frequency : 0,
            ProcessedCommands = _processedCommands,
            RejectedCommands = _rejectedCommands,
            PendingCommands = _operations.Count,
            QueueCapacity = QueueLimit,
            LastSliceMilliseconds = _lastSliceMilliseconds,
            Apps = apps.AsReadOnly()
        };
    }

    private sealed class AppActivity
    {
        internal long ForegroundTicks;
        internal int Opens;
    }

    private readonly record struct PreferenceChange(string Key, string Value);
}
