// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Modstoke.Client;

internal sealed partial class ClientTabletOperatingSystem
{
    private static readonly IReadOnlyList<ClientWatchPage> WristApps = ClientWatchOsSnapshot.Apps;
    private static readonly int[] WristCountdownPresets = { 60, 300, 600, 900, 1800, 3600 };
    private bool _wristAwake;
    private ClientWatchPage _wristPage;
    private ClientWatchPage _wristSettingsReturn;
    private int _wristApp;
    private int _wristVital;
    private int _wristNotice;
    private int _wristSetting;
    private int _wristReminder;
    private int _wristLap;
    private readonly List<long> _wristStopwatchLaps = new();
    private bool _wristStopwatchRunning;
    private long _wristStopwatchStarted;
    private long _wristStopwatchTicks;
    private long _wristStopwatchTenths;
    private long _wristFirstLapNumber = 1;
    private long _wristLapBaselineTenths;
    private int _wristReadingPage;
    private string _wristReadingContent = "";
    private ClientWatchPendingAction _wristPendingAction;
    private Guid _wristPendingReminder;
    private Guid _wristSelectedReminderId;
    private long _wristConfirmationDeadline;
    private string _wristReminderFeedback = "";
    private bool _wristHealthOverview;
    private bool _wristCompassOnly;
    private bool _wristLiveLap = true;
    private int _wristHelpPage;
    private int _wristCountdownSeconds = 300;
    private long _wristSelectedNoticeId;
    private int _wristConfirmationSeconds;
    private string _wristConfirmationLabel = "";
    private readonly Dictionary<string, int> _wristReadingPositions = new(StringComparer.Ordinal);

    public bool SetWristWatchAwake(bool awake) =>
        Post(OperationKind.WristWatchAwake, ClientTerminalApp.Home, awake, string.Empty);

    public bool WatchCommand(ClientWatchCommand command) => Enum.IsDefined(command)
        && Post(OperationKind.WristWatchCommand, ClientTerminalApp.Home, false, string.Empty, watchCommand: command);

    // Time advances in standby; only a visible reading needs frequent publication.
    private bool WristWatchTicking => _wristPendingAction != ClientWatchPendingAction.None
        || _wristStopwatchRunning
            && ((_wristAwake && (_wristPage == ClientWatchPage.Stopwatch
                    || _wristPage == ClientWatchPage.Apps && WristApps[_wristApp] == ClientWatchPage.Stopwatch))
                || (_visible && !_locked && !_taskSwitcherOpen && !_contentObscured && _activeApp == ClientTerminalApp.Watch));

    private bool UpdateWristWatch()
    {
        var confirmationExpired = _wristPendingAction != ClientWatchPendingAction.None
            && (!_wristAwake || Stopwatch.GetTimestamp() >= _wristConfirmationDeadline);
        if (confirmationExpired) _wristPendingAction = ClientWatchPendingAction.None;
        var confirmationSeconds = WristConfirmationSeconds();
        var confirmationChanged = confirmationSeconds != _wristConfirmationSeconds;
        _wristConfirmationSeconds = confirmationSeconds;
        var tenths = WristStopwatchTenths();
        if (_wristStopwatchTenths == tenths) return confirmationExpired || confirmationChanged;
        _wristStopwatchTenths = tenths;
        return confirmationExpired || confirmationChanged || WristWatchTicking;
    }

    private long WristStopwatchTenths()
    {
        var ticks = _wristStopwatchTicks;
        if (_wristStopwatchRunning) ticks += Math.Max(0, Stopwatch.GetTimestamp() - _wristStopwatchStarted);
        return (long)(ticks / (double)Stopwatch.Frequency * 10);
    }

    private ClientWatchOsSnapshot CaptureWristWatch()
    {
        var preferences = DevicePreferences;
        var reminders = CaptureReminders().Items;
        ResolveWristReminderSelection(reminders);
        ResolveWristNoticeSelection();
        if (!_wristAwake) _wristPendingAction = ClientWatchPendingAction.None;
        SyncWristReadingPosition();
        return new()
        {
            Ready = _ready,
            Generation = _generation,
            Awake = _wristAwake,
            Page = _wristAwake ? _wristPage : ClientWatchPage.Home,
            SelectedApp = WristApps[_wristApp],
            Vital = _wristVital,
            Notice = Math.Clamp(_wristNotice, 0, Math.Max(0, Math.Min(ClientWatchOsSnapshot.NoticeLimit, _notifications.Count) - 1)),
            Setting = _wristSetting,
            Theme = preferences.WatchTheme,
            NightMode = preferences.WatchNight,
            HidePrivatePreviews = preferences.WatchPrivate,
            CountdownPresetSeconds = _wristCountdownSeconds,
            StopwatchRunning = _wristStopwatchRunning,
            StopwatchTenths = WristStopwatchTenths(),
            StopwatchLaps = Array.AsReadOnly(_wristStopwatchLaps.ToArray()),
            SelectedLap = _wristLap,
            Reminders = reminders,
            SelectedReminder = Math.Clamp(_wristReminder, 0, Math.Max(0, reminders.Count - 1)),
            WatchReadingPage = _wristReadingPage,
            WatchPendingAction = _wristPendingAction,
            WatchFirstLapNumber = _wristFirstLapNumber,
            WatchLapBaselineTenths = _wristLapBaselineTenths,
            WatchReminderFeedback = preferences.WatchPrivate ? "" : _wristReminderFeedback,
            WatchPreferenceFeedback = WristPreferenceFeedback(),
            WatchHealthOverview = _wristHealthOverview,
            WatchCompassOnly = _wristCompassOnly,
            WatchLiveLap = _wristLiveLap,
            WatchHelpPage = _wristHelpPage,
            WatchNoticeContent = preferences.WatchPrivate ? "" : SelectedWristNoticeContent(),
            WatchNoticeCount = preferences.WatchPrivate ? 0 : Math.Min(ClientWatchOsSnapshot.NoticeLimit, _notifications.Count),
            WatchConfirmationSeconds = WristConfirmationSeconds(),
            WatchConfirmationLabel = preferences.WatchPrivate && _wristPendingAction == ClientWatchPendingAction.DismissReminder
                ? "" : _wristConfirmationLabel
        };
    }

    private void ResetWristWatch()
    {
        _wristAwake = false;
        _wristPage = _wristSettingsReturn = ClientWatchPage.Home;
        _wristApp = _wristVital = _wristNotice = _wristSetting = _wristReminder = 0;
        ResetWristStopwatch();
        _wristReadingPage = 0;
        _wristReadingContent = _wristReminderFeedback = "";
        _wristPendingAction = ClientWatchPendingAction.None;
        _wristPendingReminder = Guid.Empty;
        _wristSelectedReminderId = Guid.Empty;
        _wristConfirmationDeadline = 0;
        _wristHealthOverview = _wristCompassOnly = false;
        _wristHelpPage = _wristConfirmationSeconds = 0;
        _wristCountdownSeconds = 300;
        _wristSelectedNoticeId = 0;
        _wristConfirmationLabel = "";
        _wristReadingPositions.Clear();
    }

    private void ApplyWristWatchCommand(ClientWatchCommand command)
    {
        if (!_wristAwake) return;
        if (_wristPendingAction != ClientWatchPendingAction.None)
        {
            var pending = _wristPendingAction;
            _wristPendingAction = ClientWatchPendingAction.None;
            if (command == ClientWatchCommand.Activate && Stopwatch.GetTimestamp() < _wristConfirmationDeadline)
            {
                switch (pending)
                {
                    case ClientWatchPendingAction.ResetCountdown: ApplyWristCountdown(ClientWatchCommand.Down, confirmed: true); break;
                    case ClientWatchPendingAction.ResetStopwatch: ApplyWristStopwatch(ClientWatchCommand.Down, confirmed: true); break;
                    case ClientWatchPendingAction.DismissReminder:
                        if (!DevicePreferences.WatchPrivate)
                        {
                            var present = WristReminderPresent(_wristPendingReminder);
                            ApplyReminder(new ClientReminderAction("dismiss", _wristPendingReminder));
                            _wristReminderFeedback = present ? "Reminder dismissed." : "That reminder is no longer available.";
                        }
                        break;
                }
            }
            if (command is not ClientWatchCommand.Home and not ClientWatchCommand.QuickSettings) return;
        }
        if (command == ClientWatchCommand.Home)
        {
            _wristPage = ClientWatchPage.Home;
            return;
        }
        if (command == ClientWatchCommand.QuickSettings)
        {
            if (_wristPage == ClientWatchPage.QuickSettings) _wristPage = _wristSettingsReturn;
            else OpenWristSettings();
            return;
        }
        if (command == ClientWatchCommand.Back)
        {
            _wristPage = _wristPage switch
            {
                ClientWatchPage.Home or ClientWatchPage.Apps => ClientWatchPage.Home,
                ClientWatchPage.QuickSettings => _wristSettingsReturn,
                _ => ClientWatchPage.Apps
            };
            return;
        }

        var delta = command is ClientWatchCommand.Left or ClientWatchCommand.Up ? -1
            : command is ClientWatchCommand.Right or ClientWatchCommand.Down ? 1 : 0;
        switch (_wristPage)
        {
            case ClientWatchPage.Home:
                OpenWristApp(command switch
                {
                    ClientWatchCommand.Left => ClientWatchPage.Countdown,
                    ClientWatchCommand.Right => ClientWatchPage.Stopwatch,
                    ClientWatchCommand.Up => ClientWatchPage.Reminders,
                    ClientWatchCommand.Down => ClientWatchPage.Notices,
                    _ => ClientWatchPage.Apps
                });
                if (command == ClientWatchCommand.Up) ApplyWristReminder(ClientWatchCommand.Up);
                if (command == ClientWatchCommand.Down) _wristSelectedNoticeId = 0;
                break;
            case ClientWatchPage.Apps:
                if (delta != 0) _wristApp = Wrap(_wristApp + delta, WristApps.Count);
                else if (command == ClientWatchCommand.Activate)
                {
                    if (WristApps[_wristApp] == ClientWatchPage.QuickSettings) OpenWristSettings();
                    else _wristPage = WristApps[_wristApp];
                }
                break;
            case ClientWatchPage.Health:
                if (command == ClientWatchCommand.Activate) _wristHealthOverview = !_wristHealthOverview;
                if (delta != 0) _wristVital = Wrap(_wristVital + delta, ClientWatchOsSnapshot.VitalCount);
                break;
            case ClientWatchPage.Navigation:
                if (command == ClientWatchCommand.Activate) _wristCompassOnly = !_wristCompassOnly;
                break;
            case ClientWatchPage.About:
                if (delta != 0 || command == ClientWatchCommand.Activate)
                    _wristHelpPage = Wrap(_wristHelpPage + (delta == 0 ? 1 : delta), ClientWatchOsSnapshot.WatchHelp.Count + 1);
                break;
            case ClientWatchPage.Notices:
                if (DevicePreferences.WatchPrivate) break;
                ResolveWristNoticeSelection();
                var count = Math.Min(ClientWatchOsSnapshot.NoticeLimit, _notifications.Count);
                if (command is ClientWatchCommand.Up or ClientWatchCommand.Down)
                    MoveWristReadingPage(command == ClientWatchCommand.Up ? -1 : 1);
                else if (count > 0 && (delta != 0 || command == ClientWatchCommand.Activate))
                {
                    _wristNotice = Wrap(Math.Clamp(_wristNotice, 0, count - 1) + (delta == 0 ? 1 : delta), count);
                    _wristSelectedNoticeId = _notifications[_notifications.Count - 1 - _wristNotice].Id;
                }
                break;
            case ClientWatchPage.Mission:
                if (!DevicePreferences.WatchPrivate && (delta != 0 || command == ClientWatchCommand.Activate))
                    MoveWristReadingPage(delta == 0 ? 1 : delta);
                break;
            case ClientWatchPage.Countdown:
                ApplyWristCountdown(command);
                break;
            case ClientWatchPage.Stopwatch:
                ApplyWristStopwatch(command);
                break;
            case ClientWatchPage.Reminders:
                ApplyWristReminder(command);
                break;
            case ClientWatchPage.QuickSettings:
                if (delta != 0) _wristSetting = Wrap(_wristSetting + delta, 3);
                else if (command == ClientWatchCommand.Activate)
                {
                    var preferences = DevicePreferences;
                    switch (_wristSetting)
                    {
                        case 0:
                            QueueWatchPreference("watch-theme", ((ClientWatchTheme)Wrap((int)preferences.WatchTheme + 1, 3)).ToString());
                            break;
                        case 1: QueueWatchPreference("watch-night", (!preferences.WatchNight).ToString()); break;
                        case 2: QueueWatchPreference("watch-private", (!preferences.WatchPrivate).ToString()); break;
                    }
                }
                break;
        }
    }

    private void OpenWristSettings()
    {
        _wristSettingsReturn = _wristPage;
        _wristPage = ClientWatchPage.QuickSettings;
    }

    private void OpenWristApp(ClientWatchPage page)
    {
        _wristPage = page;
        for (var index = 0; index < WristApps.Count; index++)
            if (WristApps[index] == page) { _wristApp = index; break; }
    }

    private void ApplyWristCountdown(ClientWatchCommand command, bool confirmed = false)
    {
        // Reuse the tablet timer operations so both devices share a single deadline and completion notice.
        UpdateWatch();
        if ((_watchState is ClientTabletWatchState.Idle or ClientTabletWatchState.Elapsed)
            && (command is ClientWatchCommand.Up or ClientWatchCommand.Down) && !confirmed)
        {
            _wristCountdownSeconds = Math.Clamp(_wristCountdownSeconds + (command == ClientWatchCommand.Up ? 60 : -60), 60, 3600);
            return;
        }
        if (command == ClientWatchCommand.Down)
        {
            if (!confirmed && (_watchState is ClientTabletWatchState.Running or ClientTabletWatchState.Paused))
            {
                ConfirmWristAction(ClientWatchPendingAction.ResetCountdown);
                return;
            }
            Apply(new Operation(OperationKind.CancelWatch, _generation, ClientTerminalApp.Home, false, string.Empty));
        }
        else if (command == ClientWatchCommand.Activate)
        {
            var kind = _watchState switch
            {
                ClientTabletWatchState.Running => OperationKind.PauseWatch,
                ClientTabletWatchState.Paused => OperationKind.ResumeWatch,
                _ => OperationKind.StartWatch
            };
            Apply(new Operation(kind, _generation, ClientTerminalApp.Home, false, string.Empty,
                Seconds: _wristCountdownSeconds));
        }
        else if ((_watchState is ClientTabletWatchState.Idle or ClientTabletWatchState.Elapsed)
            && (command is ClientWatchCommand.Left or ClientWatchCommand.Right))
        {
            var next = command == ClientWatchCommand.Right ? WristCountdownPresets[0] : WristCountdownPresets[^1];
            if (command == ClientWatchCommand.Right)
            {
                foreach (var preset in WristCountdownPresets)
                    if (preset > _wristCountdownSeconds) { next = preset; break; }
            }
            else
            {
                for (var index = WristCountdownPresets.Length - 1; index >= 0; index--)
                    if (WristCountdownPresets[index] < _wristCountdownSeconds) { next = WristCountdownPresets[index]; break; }
            }
            _wristCountdownSeconds = next;
        }
    }

    private void ApplyWristStopwatch(ClientWatchCommand command, bool confirmed = false)
    {
        if (command == ClientWatchCommand.Down)
        {
            if (!confirmed && (_wristStopwatchRunning || WristStopwatchTenths() > 0 || _wristStopwatchLaps.Count > 0))
            {
                ConfirmWristAction(ClientWatchPendingAction.ResetStopwatch);
                return;
            }
            ResetWristStopwatch();
        }
        else if (command == ClientWatchCommand.Activate)
        {
            var now = Stopwatch.GetTimestamp();
            if (_wristStopwatchRunning) _wristStopwatchTicks += Math.Max(0, now - _wristStopwatchStarted);
            else _wristStopwatchStarted = now;
            _wristStopwatchRunning = !_wristStopwatchRunning;
        }
        else if (command == ClientWatchCommand.Right && _wristStopwatchRunning)
        {
            if (_wristStopwatchLaps.Count == ClientWatchOsSnapshot.LapLimit)
            {
                _wristLapBaselineTenths = _wristStopwatchLaps[0];
                _wristFirstLapNumber++;
                _wristStopwatchLaps.RemoveAt(0);
            }
            _wristStopwatchLaps.Add(WristStopwatchTenths());
            _wristLap = _wristStopwatchLaps.Count - 1;
            _wristLiveLap = true;
        }
        else if (command == ClientWatchCommand.Up) _wristLiveLap = true;
        else if (_wristStopwatchLaps.Count > 0 && command is ClientWatchCommand.Left or ClientWatchCommand.Right)
        {
            _wristLap = _wristLiveLap ? _wristStopwatchLaps.Count - 1
                : Wrap(_wristLap + (command == ClientWatchCommand.Left ? -1 : 1), _wristStopwatchLaps.Count);
            _wristLiveLap = false;
        }
    }

    private void ResetWristStopwatch()
    {
        _wristStopwatchRunning = false;
        _wristStopwatchStarted = _wristStopwatchTicks = _wristStopwatchTenths = 0;
        _wristStopwatchLaps.Clear();
        _wristLap = 0;
        _wristFirstLapNumber = 1;
        _wristLapBaselineTenths = 0;
        _wristLiveLap = true;
    }

    private void ApplyWristReminder(ClientWatchCommand command)
    {
        if (DevicePreferences.WatchPrivate) return;
        var reminders = CaptureReminders().Items;
        if (reminders.Count == 0) return;
        ResolveWristReminderSelection(reminders);
        if (command is ClientWatchCommand.Left or ClientWatchCommand.Right)
        {
            _wristReminder = Wrap(_wristReminder + (command == ClientWatchCommand.Left ? -1 : 1), reminders.Count);
            _wristSelectedReminderId = reminders[_wristReminder].Id;
            _wristReminderFeedback = "";
            return;
        }
        if (command == ClientWatchCommand.Up)
        {
            // Rotate through due reminders first, then select the nearest scheduled deadline.
            for (var step = 1; step <= reminders.Count; step++)
            {
                var candidate = Wrap(_wristReminder + step, reminders.Count);
                if (reminders[candidate].State != "due") continue;
                _wristReminder = candidate;
                _wristSelectedReminderId = reminders[_wristReminder].Id;
                _wristReminderFeedback = "Due reminder selected.";
                return;
            }
            for (var index = 0; index < reminders.Count; index++)
                if (reminders[index].RemainingSeconds < reminders[_wristReminder].RemainingSeconds)
                    _wristReminder = index;
            _wristReminderFeedback = "Next reminder selected.";
            _wristSelectedReminderId = reminders[_wristReminder].Id;
            return;
        }
        var selected = reminders[_wristReminder];
        if (command == ClientWatchCommand.Down)
        {
            _wristPendingReminder = selected.Id;
            ConfirmWristAction(ClientWatchPendingAction.DismissReminder);
        }
        else if (command == ClientWatchCommand.Activate && selected.State == "due")
        {
            ApplyReminder(new ClientReminderAction("snooze", selected.Id));
            _wristReminderFeedback = "Reminder snoozed for 5 minutes.";
        }
    }

    private bool WristReminderPresent(Guid id)
    {
        foreach (var reminder in CaptureReminders().Items) if (reminder.Id == id) return true;
        return false;
    }

    private void ResolveWristReminderSelection(IReadOnlyList<ClientTabletReminder> reminders)
    {
        for (var index = 0; index < reminders.Count; index++)
        {
            if (reminders[index].Id != _wristSelectedReminderId) continue;
            _wristReminder = index;
            return;
        }
        _wristReminder = Math.Clamp(_wristReminder, 0, Math.Max(0, reminders.Count - 1));
        _wristSelectedReminderId = reminders.Count == 0 ? Guid.Empty : reminders[_wristReminder].Id;
    }

    private void ConfirmWristAction(ClientWatchPendingAction action)
    {
        _wristPendingAction = action;
        _wristConfirmationDeadline = Stopwatch.GetTimestamp() + 10 * Stopwatch.Frequency;
        _wristConfirmationLabel = "";
        if (action == ClientWatchPendingAction.DismissReminder)
            foreach (var reminder in CaptureReminders().Items)
                if (reminder.Id == _wristPendingReminder) { _wristConfirmationLabel = reminder.Label; break; }
    }

    private int WristConfirmationSeconds() => _wristPendingAction == ClientWatchPendingAction.None ? 0
        : Math.Max(0, (int)Math.Ceiling((_wristConfirmationDeadline - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency));

    private void ResolveWristNoticeSelection()
    {
        var count = Math.Min(ClientWatchOsSnapshot.NoticeLimit, _notifications.Count);
        for (var index = 0; index < count; index++)
            if (_notifications[_notifications.Count - 1 - index].Id == _wristSelectedNoticeId)
            { _wristNotice = index; return; }
        _wristNotice = 0;
        _wristSelectedNoticeId = count == 0 ? 0 : _notifications[^1].Id;
    }

    private string SelectedWristNoticeContent()
    {
        if (_notifications.Count == 0) return "";
        var index = Math.Clamp(_wristNotice, 0, Math.Min(ClientWatchOsSnapshot.NoticeLimit, _notifications.Count) - 1);
        var notice = _notifications[_notifications.Count - 1 - index];
        return notice.Title + " · " + notice.Detail;
    }

    private string WristReadingContent()
    {
        if (DevicePreferences.WatchPrivate) return "";
        if (_wristPage == ClientWatchPage.Mission) return _field.Mission;
        return _wristPage == ClientWatchPage.Notices ? SelectedWristNoticeContent() : "";
    }

    private void MoveWristReadingPage(int delta)
    {
        SyncWristReadingPosition();
        _wristReadingPage = Wrap(_wristReadingPage + delta, ClientWatchOsSnapshot.WatchReadingPages(_wristReadingContent).Count);
    }

    private void SyncWristReadingPosition()
    {
        var content = WristReadingContent();
        if (!string.Equals(content, _wristReadingContent, StringComparison.Ordinal))
        {
            if (_wristReadingContent.Length > 0)
            {
                if (_wristReadingPositions.Count >= 16 && !_wristReadingPositions.ContainsKey(_wristReadingContent))
                {
                    string? oldest = null;
                    foreach (var key in _wristReadingPositions.Keys) { oldest = key; break; }
                    if (oldest != null) _wristReadingPositions.Remove(oldest);
                }
                _wristReadingPositions[_wristReadingContent] = _wristReadingPage;
            }
            _wristReadingContent = content;
            _wristReadingPage = _wristReadingPositions.TryGetValue(content, out var page) ? page : 0;
        }
    }

    private string WristPreferenceFeedback()
    {
        // Do not expose exception text, paths or user data on a worn device.
        if (_devicePreferenceStore == null) return "SETTINGS UNAVAILABLE";
        var status = _devicePreferenceStore.Status;
        if (status.Contains("queued", StringComparison.OrdinalIgnoreCase)) return "SAVING…";
        if (status.StartsWith("Saved", StringComparison.Ordinal)) return "SAVED LOCALLY";
        if (status.Contains("already set", StringComparison.Ordinal)) return "ALREADY SET";
        if (status.StartsWith("Loaded", StringComparison.Ordinal)) return "LOCAL SETTINGS";
        if (status.StartsWith("No saved", StringComparison.Ordinal)) return "USING DEFAULTS";
        return "SAVE UNAVAILABLE\nSEE TABLET";
    }

    private static int Wrap(int value, int count) => (value % count + count) % count;
}
