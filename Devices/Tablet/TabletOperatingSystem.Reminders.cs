// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Modstoke.Client;

internal sealed record ClientReminderAction(string Kind, Guid Id = default, string Text = "", int Minutes = 0,
    Guid ClockRequestId = default);

internal sealed record ClientTabletReminder(Guid Id, string Label, string State, int RemainingSeconds);

internal sealed record ClientTabletReminderSnapshot
{
    internal IReadOnlyList<ClientTabletReminder> Items { get; init; } = Array.Empty<ClientTabletReminder>();
    internal long CompletionId { get; init; }
    internal string Status { get; init; } = "";
    internal Guid ClockRequestId { get; init; }
    internal bool ClockRequestAccepted { get; init; }
}

internal sealed partial class ClientTabletOperatingSystem
{
    private const int ReminderLimit = 8;
    private readonly List<SessionReminder> _sessionReminders = new(ReminderLimit);
    private long _reminderCompletionId;
    private string _reminderStatus = "";
    private Guid _clockReminderRequestId;
    private bool _clockReminderRequestAccepted;

    public bool Reminder(ClientReminderAction action)
    {
        var normalized = NormalizeReminderAction(action);
        return normalized is not null && FieldAction(new ClientFieldAction("reminder", Reminder: normalized));
    }

    private static ClientReminderAction? NormalizeReminderAction(ClientReminderAction? action)
    {
        if (action is null || action.Text is null || action.Text.Length > 300) return null;
        foreach (var character in action.Text)
            if (char.IsControl(character)) return null;

        switch (action.Kind)
        {
            case "add":
                var label = action.Text.Trim();
                if (action.Id != Guid.Empty || label.Length is < 1 or > 48 || action.Minutes is < 1 or > 120)
                    return null;
                action = action with { Text = label };
                break;
            case "dismiss":
            case "snooze":
            case "extend":
                if (action.Id == Guid.Empty || action.Text.Length != 0 || action.Minutes != 0) return null;
                break;
            case "dismiss-due":
            case "snooze-due":
                if (action.Id != Guid.Empty || action.Minutes != 0) return null;
                var ids = action.Text.Split(',');
                if (ids.Length is < 1 or > ReminderLimit) return null;
                for (var index = 0; index < ids.Length; index++)
                {
                    if (!Guid.TryParseExact(ids[index], "N", out var parsed) || parsed == Guid.Empty) return null;
                    ids[index] = parsed.ToString("N");
                }
                action = action with { Text = string.Join(",", ids) };
                break;
            default:
                return null;
        }

        return action;
    }

    private void ApplyReminder(ClientReminderAction action)
    {
        var normalized = NormalizeReminderAction(action);
        if (normalized is null) return;
        action = normalized;
        // Only correlated composer requests replace the last composer outcome.
        if (action.ClockRequestId != Guid.Empty)
        {
            _clockReminderRequestId = action.ClockRequestId;
            _clockReminderRequestAccepted = false;
        }
        UpdateReminders();
        if (action.Kind is "dismiss-due" or "snooze-due")
        {
            var affected = 0;
            var deadline = Stopwatch.GetTimestamp() + 300 * Stopwatch.Frequency;
            for (var item = _sessionReminders.Count - 1; item >= 0; item--)
            {
                var due = _sessionReminders[item];
                if (!due.Due || !action.Text.Contains(due.Id.ToString("N"), StringComparison.Ordinal)) continue;
                _notifications.RemoveAll(notification => notification.Key == "reminder:" + due.Id);
                if (action.Kind == "dismiss-due") _sessionReminders.RemoveAt(item);
                else
                {
                    due.Due = false;
                    due.Deadline = deadline;
                    due.RemainingSeconds = 300;
                }
                affected++;
            }
            _reminderStatus = affected == 0 ? "No reminders are due."
                : affected + (affected == 1 ? " reminder " : " reminders ")
                    + (action.Kind == "dismiss-due" ? "dismissed." : "snoozed for 5 minutes.");
            return;
        }
        if (action.Kind == "add")
        {
            if (_sessionReminders.Count >= ReminderLimit)
            {
                _reminderStatus = "All 8 reminder slots are in use. Dismiss one before adding another.";
                return;
            }

            var seconds = action.Minutes * 60;
            _sessionReminders.Add(new SessionReminder(Guid.NewGuid(), action.Text,
                Stopwatch.GetTimestamp() + seconds * Stopwatch.Frequency, seconds));
            if (action.ClockRequestId != Guid.Empty) _clockReminderRequestAccepted = true;
            _reminderStatus = action.Text + " · scheduled in " + action.Minutes + (action.Minutes == 1 ? " minute." : " minutes.");
            return;
        }

        var index = _sessionReminders.FindIndex(item => item.Id == action.Id);
        if (index < 0)
        {
            _reminderStatus = "That reminder is no longer available.";
            return;
        }

        var reminder = _sessionReminders[index];
        switch (action.Kind)
        {
            case "dismiss":
                _sessionReminders.RemoveAt(index);
                _notifications.RemoveAll(item => item.Key == "reminder:" + reminder.Id);
                _reminderStatus = reminder.Label + (reminder.Due ? " · dismissed." : " · cancelled.");
                break;
            case "extend":
                if (reminder.Due)
                {
                    _reminderStatus = reminder.Label + " is due. Use Snooze to restart it.";
                    return;
                }
                if (reminder.RemainingSeconds > 115 * 60)
                {
                    _reminderStatus = "Reminders can be scheduled at most 120 minutes ahead.";
                    return;
                }
                reminder.Deadline += 300 * Stopwatch.Frequency;
                reminder.RemainingSeconds += 300;
                _reminderStatus = reminder.Label + " · added 5 minutes.";
                break;
            case "snooze":
                if (!reminder.Due)
                {
                    _reminderStatus = "Only due reminders can be snoozed.";
                    return;
                }
                reminder.Due = false;
                reminder.RemainingSeconds = 300;
                reminder.Deadline = Stopwatch.GetTimestamp() + 300 * Stopwatch.Frequency;
                _notifications.RemoveAll(item => item.Key == "reminder:" + reminder.Id);
                _reminderStatus = reminder.Label + " · snoozed for 5 minutes.";
                break;
        }
    }

    private bool RemindersPending => _sessionReminders.Exists(item => !item.Due);

    private bool UpdateReminders()
    {
        var now = Stopwatch.GetTimestamp();
        var changed = false;
        foreach (var reminder in _sessionReminders)
        {
            if (reminder.Due) continue;
            var seconds = WatchSeconds(Math.Max(0, reminder.Deadline - now));
            if (seconds != reminder.RemainingSeconds)
            {
                reminder.RemainingSeconds = seconds;
                changed = true;
            }
            if (seconds != 0) continue;

            reminder.Due = true;
            _reminderCompletionId++;
            _reminderStatus = reminder.Label + " is due. Dismiss it or snooze for 5 minutes.";
            AddNotification("reminder:" + reminder.Id, reminder.Label,
                "Reminder due. Open Reminders to dismiss or snooze.", ClientTerminalApp.Watch);
            changed = true;
        }
        return changed;
    }

    private void ResetReminders()
    {
        foreach (var reminder in _sessionReminders)
            _notifications.RemoveAll(item => item.Key == "reminder:" + reminder.Id);
        _sessionReminders.Clear();
        _reminderCompletionId = 0;
        _reminderStatus = "";
        _clockReminderRequestId = Guid.Empty;
        _clockReminderRequestAccepted = false;
    }

    private ClientTabletReminderSnapshot CaptureReminders()
    {
        var now = Stopwatch.GetTimestamp();
        var items = new ClientTabletReminder[_sessionReminders.Count];
        for (var index = 0; index < _sessionReminders.Count; index++)
        {
            var reminder = _sessionReminders[index];
            items[index] = new ClientTabletReminder(reminder.Id, reminder.Label,
                reminder.Due ? "due" : "scheduled",
                reminder.Due ? 0 : WatchSeconds(Math.Max(0, reminder.Deadline - now)));
        }
        // Stable insertion sort preserves creation order for equal deadlines.
        for (var index = 1; index < items.Length; index++)
        {
            var current = items[index];
            var position = index;
            while (position > 0 && ClockReminderOrder(current, items[position - 1]) < 0)
            {
                items[position] = items[position - 1];
                position--;
            }
            items[position] = current;
        }
        return new ClientTabletReminderSnapshot
        {
            Items = Array.AsReadOnly(items),
            CompletionId = _reminderCompletionId,
            Status = _reminderStatus,
            ClockRequestId = _clockReminderRequestId,
            ClockRequestAccepted = _clockReminderRequestAccepted
        };
    }

    private static int ClockReminderOrder(ClientTabletReminder left, ClientTabletReminder right)
    {
        if ((left.State == "due") != (right.State == "due")) return left.State == "due" ? -1 : 1;
        return left.RemainingSeconds.CompareTo(right.RemainingSeconds);
    }

    private sealed class SessionReminder(Guid id, string label, long deadline, int remainingSeconds)
    {
        internal Guid Id { get; } = id;
        internal string Label { get; } = label;
        internal long Deadline { get; set; } = deadline;
        internal int RemainingSeconds { get; set; } = remainingSeconds;
        internal bool Due { get; set; }
    }
}
