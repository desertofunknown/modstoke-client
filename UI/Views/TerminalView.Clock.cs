// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Globalization;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.Events;

namespace Modstoke.Client;

internal sealed partial class ClientTerminalView
{
    private const int ClockReminderLimit = 8;
    private static readonly int[] ClockReminderMinutes = { 5, 15, 30, 60 };
    private readonly ButtonWidget[] _clockPresets = new ButtonWidget[4];
    private readonly ButtonWidget[] _clockDurations = new ButtonWidget[4];
    private readonly ClockReminderRow[] _clockReminderRows = new ClockReminderRow[ClockReminderLimit];
    private RectTransform _clockOverview = null!;
    private RectTransform _clockReminders = null!;
    private ButtonWidget _clockOverviewTab = null!;
    private ButtonWidget _clockRemindersTab = null!;
    private ButtonWidget _clockReset = null!;
    private ButtonWidget _clockQueue = null!;
    private TMP_InputField _clockReminderLabel = null!;
    private ScrollArea _clockReminderList = null!;
    private TextMeshProUGUI _clockReminderStatus = null!;
    private TextMeshProUGUI _clockReminderCount = null!;
    private TextMeshProUGUI _clockReminderEmpty = null!;
    private TextMeshProUGUI _clockStopwatch = null!;
    private int _clockDuration = 5;
    private int _clockReminderCountValue;
    private bool _clockReady;
    private TMP_InputField _clockCustomMinutes = null!;
    private ButtonWidget _clockSnoozeDue = null!;
    private ButtonWidget _clockDismissDue = null!;
    private ButtonWidget _clockNextReminder = null!;
    private bool _clockConfirmDismissDue;
    private int _clockDueCount;
    private Guid _clockPendingRequest;
    private string _clockPendingLabel = "";
    private long _clockGeneration = -1;
    private string _clockConfirmedDueIds = "";

    private sealed class ClockReminderRow
    {
        internal RectTransform Root = null!;
        internal TextMeshProUGUI Label = null!;
        internal TextMeshProUGUI Detail = null!;
        internal ButtonWidget Snooze = null!;
        internal ButtonWidget Dismiss = null!;
        internal Guid Id;
        internal bool Due;
    }

    private void BuildClockApp()
    {
        _clockOverviewTab = Button(_watchSurface, "OverviewTab", "Overview", 0f, 0f, 220f, 52f,
            () => SelectClockTab(false));
        _clockRemindersTab = Button(_watchSurface, "RemindersTab", "Reminders", 236f, 0f, 220f, 52f,
            () => SelectClockTab(true));
        _clockNextReminder = Button(_watchSurface, "NextReminder", "No reminders scheduled", 708f, 0f, 620f, 52f,
            () => SelectClockTab(true));
        _clockNextReminder.Label.richText = false;
        _clockNextReminder.Label.enableWordWrapping = false;
        _clockNextReminder.Label.overflowMode = TextOverflowModes.Ellipsis;
        _clockOverview = Node(_watchSurface, "Overview");
        _clockReminders = Node(_watchSurface, "Reminders");
        Place(_clockOverview, 0f, 68f, 1328f, 532f);
        Place(_clockReminders, 0f, 68f, 1328f, 532f);

        var face = RoundedPanel(_clockOverview, "PairedWatch", Surface, 0f, 0f, 644f, 532f);
        Text(face.transform, "Pairing", "PAIRED FIELD WATCH", 21f, Mint, 32f, 20f, 580f, 32f, true);
        Text(face.transform, "PairingDetail", "Shared countdown", 22f, Muted, 32f, 58f, 580f, 34f);
        _watchTime = Text(face.transform, "Timer", "00:00", 96f, Ink, 32f, 104f, 580f, 118f, role: TabletTextRole.Display);
        _watchTime.alignment = TextAlignmentOptions.Center;
        _watchState = Text(face.transform, "TimerState", "Loading clock…", 26f, Muted, 32f, 230f, 580f, 40f);
        _watchState.alignment = TextAlignmentOptions.Center;
        var presets = new[] { 1, 5, 15, 30 };
        for (var index = 0; index < presets.Length; index++)
        {
            var minutes = presets[index];
            _clockPresets[index] = Button(face.transform, "Preset" + minutes, minutes + " min",
                32f + index * 149f, 290f, 133f, 54f, () => StartClockCountdown(minutes * 60));
            _clockPresets[index].Label.alignment = TextAlignmentOptions.Center;
            _clockPresets[index].Button.interactable = false;
        }
        _watchTimer = Button(face.transform, "TimerControl", "Start timer", 32f, 366f, 354f, 60f, FieldWatchTimer);
        _clockReset = Button(face.transform, "TimerReset", "Reset", 402f, 366f, 210f, 60f, () => WatchCommand("timer.reset"));
        _watchTimer.Button.interactable = _clockReset.Button.interactable = false;
        Text(face.transform, "TimerHint", "Timer controls stay synchronized with your wrist watch.", 22f, Muted, 32f, 450f, 580f, 64f);

        var direction = Button(_clockOverview, "Navigation", "", 668f, 0f, 660f, 210f,
            () => SelectApp(ClientTerminalApp.Navigation));
        Text(direction.Image.transform, "Heading", "NAVIGATION", 20f, Mint, 28f, 18f, 604f, 30f, true);
        _watchNavigation = Text(direction.Image.transform, "Readings", "Heading unavailable", 26f, Ink, 28f, 62f, 604f, 130f);
        _watchNavigation.overflowMode = TextOverflowModes.Ellipsis;
        var notifications = Button(_clockOverview, "Notifications", "", 668f, 226f, 660f, 138f,
            () => WatchCommand("notifications.open"));
        Text(notifications.Image.transform, "Heading", "NOTIFICATIONS", 20f, Mint, 28f, 16f, 604f, 30f, true);
        _watchNotifications = Text(notifications.Image.transform, "Summary", "No notifications", 24f, Ink, 28f, 58f, 604f, 64f);
        _watchNotifications.overflowMode = TextOverflowModes.Ellipsis;
        var stopwatch = RoundedPanel(_clockOverview, "Stopwatch", Surface, 668f, 380f, 660f, 84f);
        Text(stopwatch.transform, "Heading", "WRIST STOPWATCH", 18f, Muted, 28f, 10f, 604f, 26f, true);
        _clockStopwatch = Text(stopwatch.transform, "Readout", "Loading…", 25f, Ink, 28f, 42f, 604f, 34f);
        _clockStopwatch.enableWordWrapping = false;
        _clockStopwatch.overflowMode = TextOverflowModes.Ellipsis;
        Button(_clockOverview, "Health", "View health readings", 668f, 480f, 660f, 52f,
            () => SelectApp(ClientTerminalApp.Health));

        BuildClockReminders();
        BuildClockEnvironment();
        SelectClockTab(false);
    }

    private void BuildClockReminders()
    {
        var composer = RoundedPanel(_clockReminders, "NewReminder", Surface, 0f, 0f, 444f, 468f);
        Text(composer.transform, "Heading", "NEW REMINDER", 20f, Mint, 24f, 18f, 396f, 30f, true);
        Text(composer.transform, "LabelHeading", "Label", 22f, Muted, 24f, 62f, 396f, 30f);
        _clockReminderLabel = FieldInput(composer.transform, 24f, 100f, 396f, 58f, 48, false);
        var placeholder = Text(_clockReminderLabel.textViewport, "Placeholder", "What should we remind you?", 21f, Muted, 0f, 0f, 372f, 46f);
        placeholder.alignment = TextAlignmentOptions.MidlineLeft;
        placeholder.enableWordWrapping = false;
        placeholder.overflowMode = TextOverflowModes.Ellipsis;
        _clockReminderLabel.placeholder = placeholder;
        _clockReminderLabel.onValueChanged.AddListener((UnityAction<string>)new Action<string>(_ => RefreshClockComposer()));
        Text(composer.transform, "DurationHeading", "Remind me in", 22f, Muted, 24f, 176f, 396f, 30f);
        for (var index = 0; index < ClockReminderMinutes.Length; index++)
        {
            var minutes = ClockReminderMinutes[index];
            _clockDurations[index] = Button(composer.transform, "Duration" + minutes, minutes + "m",
                24f + index * 102f, 214f, 90f, 52f, () => SelectClockDuration(minutes));
            _clockDurations[index].Label.alignment = TextAlignmentOptions.Center;
        }
        Text(composer.transform, "CustomMinutesHeading", "Or minutes (1–120)", 21f, Muted, 24f, 282f, 240f, 34f);
        _clockCustomMinutes = FieldInput(composer.transform, 280f, 274f, 140f, 50f, 3, false);
        _clockCustomMinutes.SetTextWithoutNotify(_clockDuration.ToString(CultureInfo.InvariantCulture));
        _clockCustomMinutes.onValueChanged.AddListener((UnityAction<string>)new Action<string>(_ =>
        {
            _clockDuration = int.TryParse(_clockCustomMinutes.text, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
                && minutes is >= 1 and <= 120 ? minutes : 0;
            RefreshClockComposer();
        }));
        _clockQueue = Button(composer.transform, "QueueReminder", "Set reminder", 24f, 338f, 396f, 52f, QueueClockReminder);
        _clockReminderStatus = Text(composer.transform, "Status", "Loading reminders…", 20f, Muted, 24f, 404f, 396f, 54f);
        _clockReminderStatus.richText = false;
        _clockReminderStatus.overflowMode = TextOverflowModes.Ellipsis;
        Text(_clockReminders, "Lifetime", "Real-time minutes. Reminders run with devices put away and end when you leave this world.",
            19f, Muted, 16f, 480f, 420f, 52f);
        _clockReminderCount = Text(_clockReminders, "Count", "REMINDERS", 21f, Muted, 468f, 10f, 860f, 32f, true);
        _clockSnoozeDue = Button(_clockReminders, "SnoozeDue", "Snooze due 5m", 468f, 52f, 412f, 48f, () => ClockBulkReminderAction(false));
        _clockDismissDue = Button(_clockReminders, "DismissDue", "Dismiss due", 896f, 52f, 432f, 48f, () => ClockBulkReminderAction(true));
        _clockReminderList = Scroll(_clockReminders, "ReminderList", 468f, 112f, 860f, 420f);
        _clockReminderEmpty = Text(_clockReminderList.Content, "Empty", "Loading reminders…", 26f, Muted, 24f, 28f, 798f, 100f);
        for (var index = 0; index < _clockReminderRows.Length; index++)
        {
            var row = new ClockReminderRow();
            row.Root = RoundedPanel(_clockReminderList.Content, "Reminder" + index, Raised, 0f, index * 112f, 846f, 100f).rectTransform;
            row.Label = Text(row.Root, "Label", "", 25f, Ink, 20f, 12f, 490f, 36f, true);
            row.Label.enableWordWrapping = false;
            row.Label.richText = false;
            row.Label.overflowMode = TextOverflowModes.Ellipsis;
            row.Detail = Text(row.Root, "Detail", "", 22f, Muted, 20f, 58f, 490f, 32f);
            row.Detail.overflowMode = TextOverflowModes.Ellipsis;
            row.Snooze = Button(row.Root, "Snooze", "Snooze 5m", 526f, 24f, 166f, 52f,
                () => ClockReminderAction(row, true));
            row.Dismiss = Button(row.Root, "Dismiss", "Dismiss", 704f, 24f, 126f, 52f,
                () => ClockReminderAction(row, false));
            row.Root.gameObject.SetActive(false);
            _clockReminderRows[index] = row;
        }
        RefreshClockComposer();
    }

    private void SelectClockTab(bool reminders)
        => SelectClockPage(reminders ? 1 : 0);

    private void SelectClockPage(int page)
    {
        _clockReminderLabel.DeactivateInputField();
        _clockCustomMinutes.DeactivateInputField();
        _clockConfirmDismissDue = false;
        RefreshClockBulkActions();
        _clockOverview.gameObject.SetActive(page == 0);
        _clockReminders.gameObject.SetActive(page == 1);
        _clockConditions.gameObject.SetActive(page == 2);
        SetPreferenceChoice(_clockOverviewTab, page == 0);
        SetPreferenceChoice(_clockRemindersTab, page == 1);
        SetPreferenceChoice(_clockConditionsTab, page == 2);
    }

    private bool ClockCanAct => !_disposed && !ContentObscured && _clockReady && _watchSurface.gameObject.activeInHierarchy;

    private void StartClockCountdown(int seconds)
    {
        if (ClockCanAct && _fieldTimerState is not (ClientTabletWatchState.Running or ClientTabletWatchState.Paused))
            _command("os", "watch-start:" + seconds.ToString(CultureInfo.InvariantCulture));
    }

    private void SelectClockDuration(int minutes)
    {
        if (!ClockCanAct || _clockReminderCountValue >= ClockReminderLimit) return;
        _clockDuration = minutes;
        _clockCustomMinutes.SetTextWithoutNotify(minutes.ToString(CultureInfo.InvariantCulture));
        RefreshClockComposer();
    }

    private void QueueClockReminder()
    {
        if (!ClockCanAct || !_clockQueue.Button.interactable) return;
        _clockReminderLabel.DeactivateInputField();
        _clockCustomMinutes.DeactivateInputField();
        _command("clock", "reminder-add:" + _clockDuration.ToString(CultureInfo.InvariantCulture) + ":" + _clockReminderLabel.text.Trim());
    }

    private void ClockReminderAction(ClockReminderRow row, bool snooze)
    {
        if (!ClockCanAct || !row.Root.gameObject.activeInHierarchy || (snooze && !row.Snooze.Button.interactable)) return;
        _clockConfirmDismissDue = false;
        RefreshClockBulkActions();
        _command("clock", (snooze ? row.Due ? "reminder-snooze:" : "reminder-extend:" : "reminder-dismiss:") + row.Id.ToString("D"));
    }

    private void ClockBulkReminderAction(bool dismiss)
    {
        if (!ClockCanAct || _clockDueCount == 0) return;
        if (_clockConfirmDismissDue)
        {
            _clockConfirmDismissDue = false;
            RefreshClockBulkActions();
            if (dismiss) _command("clock", "reminder-dismiss-due:" + _clockConfirmedDueIds);
            return;
        }
        if (dismiss)
        {
            _clockConfirmDismissDue = true;
            _clockConfirmedDueIds = ClockDueReminderIds();
            RefreshClockBulkActions();
        }
        else _command("clock", "reminder-snooze-due:" + ClockDueReminderIds());
    }

    private string ClockDueReminderIds() => string.Join(",", _clockReminderRows
        .Where(row => row.Due && row.Root.gameObject.activeSelf).Select(row => row.Id.ToString("N")));

    private void RefreshClockBulkActions()
    {
        _clockSnoozeDue.Button.interactable = _clockDismissDue.Button.interactable = _clockReady && _clockDueCount > 0;
        _clockSnoozeDue.Label.text = _clockConfirmDismissDue ? "Cancel" : "Snooze due 5m";
        _clockDismissDue.Label.text = _clockConfirmDismissDue ? "Confirm dismiss " + _clockDueCount : "Dismiss due";
    }

    internal void ClockReminderRequestQueued(Guid id, string label, bool accepted)
    {
        if (_disposed) return;
        _clockPendingRequest = accepted ? id : Guid.Empty;
        _clockPendingLabel = accepted ? label : "";
        _clockReminderStatus.text = accepted ? "Scheduling reminder…" : "Could not queue reminder. Your draft is kept.";
        RefreshClockComposer();
    }

    private void RefreshClockComposer()
    {
        var available = _clockReady && _clockReminderCountValue < ClockReminderLimit && _clockPendingRequest == Guid.Empty;
        _clockReminderLabel.interactable = available;
        _clockCustomMinutes.interactable = available;
        _clockQueue.Button.interactable = available && _clockDuration > 0 && !string.IsNullOrWhiteSpace(_clockReminderLabel.text);
        _clockQueue.Label.text = _clockPendingRequest != Guid.Empty ? "Scheduling…" : _clockDuration == 0 ? "Enter 1–120 minutes" : "Set reminder";
        for (var index = 0; index < _clockDurations.Length; index++)
        {
            _clockDurations[index].Button.interactable = available;
            SetPreferenceChoice(_clockDurations[index], ClockReminderMinutes[index] == _clockDuration);
        }
    }

    internal void SetClockState(ClientTabletOsSnapshot snapshot)
    {
        if (_disposed) return;
        _clockReady = snapshot.Ready;
        if (_clockGeneration != snapshot.Generation || !_clockReady)
        {
            _clockGeneration = snapshot.Generation;
            _clockPendingRequest = Guid.Empty;
            _clockPendingLabel = "";
            _clockConfirmDismissDue = false;
        }
        if (_clockPendingRequest != Guid.Empty && snapshot.Reminders.ClockRequestId == _clockPendingRequest)
        {
            if (snapshot.Reminders.ClockRequestAccepted && _clockReminderLabel.text.Trim() == _clockPendingLabel)
                _clockReminderLabel.SetTextWithoutNotify("");
            _clockPendingRequest = Guid.Empty;
            _clockPendingLabel = "";
        }
        var count = _clockReady ? Math.Min(ClockReminderLimit, snapshot.Reminders.Items.Count) : 0;
        var countChanged = _clockReminderCountValue != count;
        _clockReminderCountValue = count;
        var due = 0;
        for (var index = 0; index < count; index++) if (snapshot.Reminders.Items[index].State == "due") due++;
        if (_clockDueCount != due || !snapshot.Visible || snapshot.Locked || snapshot.TaskSwitcherOpen
            || ContentObscured || snapshot.ActiveApp != ClientTerminalApp.Watch)
            _clockConfirmDismissDue = false;
        _clockDueCount = due;
        RefreshClockBulkActions();
        if (!_clockReady || count == ClockReminderLimit || !snapshot.Visible || snapshot.Locked
            || snapshot.TaskSwitcherOpen || ContentObscured || snapshot.ActiveApp != ClientTerminalApp.Watch)
        {
            _clockReminderLabel.DeactivateInputField();
            _clockCustomMinutes.DeactivateInputField();
        }
        RefreshClockComposer();
        _watchTimer.Button.interactable = _clockReady;
        _clockReset.Button.interactable = _clockReady && snapshot.Watch.State != ClientTabletWatchState.Idle;
        var active = snapshot.Watch.State is ClientTabletWatchState.Running or ClientTabletWatchState.Paused;
        foreach (var preset in _clockPresets) preset.Button.interactable = _clockReady && !active;
        if (!_clockReady)
        {
            _watchTime.text = "--:--";
            _watchState.text = "Clock unavailable";
        }
        var tenths = Math.Max(0L, snapshot.WristWatch.StopwatchTenths);
        _clockStopwatch.text = !_clockReady ? "Clock unavailable"
            : (tenths / 600).ToString("00", CultureInfo.InvariantCulture) + ":"
                + (tenths / 10 % 60).ToString("00", CultureInfo.InvariantCulture) + "."
                + (tenths % 10).ToString(CultureInfo.InvariantCulture)
                + (snapshot.WristWatch.StopwatchRunning ? " · Running" : " · Stopped")
                + " · " + snapshot.WristWatch.StopwatchLaps.Count.ToString(CultureInfo.InvariantCulture) + " laps";
        _clockRemindersTab.Label.text = due > 0 ? "Reminders (" + due + ")" : "Reminders";
        _clockNextReminder.Label.text = !_clockReady ? "Reminders unavailable" : count == 0 ? "No reminders scheduled · Add one"
            : due > 0 ? due + (due == 1 ? " reminder due · " : " reminders due · ") + snapshot.Reminders.Items[0].Label
            : "Next · " + snapshot.Reminders.Items[0].Label + " · " + WatchTime(snapshot.Reminders.Items[0].RemainingSeconds);
        _clockNextReminder.Button.interactable = _clockReady;
        _clockReminderCount.text = _clockReady ? "REMINDERS · " + count + " / " + ClockReminderLimit + " · " + due + " DUE" : "REMINDERS · UNAVAILABLE";
        _clockReminderStatus.text = !_clockReady ? "Reminders unavailable"
            : _clockPendingRequest != Guid.Empty ? "Scheduling reminder…"
            : count == ClockReminderLimit ? "All 8 slots are in use. Dismiss a reminder to add another."
            : string.IsNullOrWhiteSpace(snapshot.Reminders.Status) ? "Choose a label and a duration." : snapshot.Reminders.Status;
        _clockReminderEmpty.gameObject.SetActive(count == 0);
        _clockReminderEmpty.text = _clockReady ? "No reminders yet.\nSet one to see it here and on your watch." : "Reminders unavailable";
        for (var index = 0; index < _clockReminderRows.Length; index++)
        {
            var row = _clockReminderRows[index];
            row.Root.gameObject.SetActive(index < count);
            if (index >= count) continue;
            var reminder = snapshot.Reminders.Items[index];
            row.Id = reminder.Id;
            row.Due = reminder.State == "due";
            row.Label.text = reminder.Label;
            row.Detail.text = row.Due ? "Due now" : "Scheduled · " + WatchTime(reminder.RemainingSeconds) + " remaining";
            row.Detail.color = row.Due ? Amber : Muted;
            row.Snooze.Label.text = row.Due ? "Snooze 5m" : "+5 minutes";
            row.Snooze.Button.interactable = _clockReady && (row.Due || reminder.RemainingSeconds <= 115 * 60);
            row.Dismiss.Label.text = row.Due ? "Dismiss" : "Cancel";
            row.Dismiss.Button.interactable = _clockReady;
        }
        _clockReminderList.Height(Math.Max(420f, count * 112f - 12f));
        if (countChanged) _clockReminderList.Reset();
    }
}
