// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Globalization;
using Modstoke.Sons;

namespace Modstoke.Client;

internal readonly record struct ClientIslandClock(bool Available, int Day, int MinuteOfDay)
{
    internal static ClientIslandClock FromSnapshot(ClockSnapshot? time) => time == null
        || time.GameTime < TimeSpan.Zero || time.EffectiveTime < TimeSpan.Zero ? default
        : new(true, time.GameTime.Days, time.EffectiveTime.Hours * 60 + time.EffectiveTime.Minutes);

    internal long Key => Available ? (long)Day * 1440 + MinuteOfDay : -2;
    internal string TimeText => Available
        ? (MinuteOfDay / 60).ToString("00", CultureInfo.InvariantCulture) + ":"
            + (MinuteOfDay % 60).ToString("00", CultureInfo.InvariantCulture)
        : "--:--";
    internal string DayText => Available ? "Day " + Day.ToString(CultureInfo.InvariantCulture) : "Island time unavailable";
}

internal sealed partial class ClientTerminal
{
    // Sample only on Unity's thread; views receive plain copied values, never a native clock holder.
    private static ClientIslandClock ReadIslandClock()
    {
        if (!GameRuntime.State.IsInWorld || !EnvironmentReads.TryReadClock(out var time, out _)) return default;
        return ClientIslandClock.FromSnapshot(time);
    }

    private void InvokeClock(string command)
    {
        ClientReminderAction? action = null;
        if (command.StartsWith("reminder-add:", StringComparison.Ordinal))
        {
            var value = command[13..];
            var separator = value.IndexOf(':');
            if (separator > 0 && int.TryParse(value[..separator], NumberStyles.None,
                CultureInfo.InvariantCulture, out var minutes))
                action = new("add", Text: value[(separator + 1)..], Minutes: minutes, ClockRequestId: Guid.NewGuid());
        }
        else if (command.StartsWith("reminder-snooze:", StringComparison.Ordinal)
            && Guid.TryParse(command[16..], out var snooze))
            action = new("snooze", snooze);
        else if (command.StartsWith("reminder-dismiss:", StringComparison.Ordinal)
            && Guid.TryParse(command[17..], out var dismiss))
            action = new("dismiss", dismiss);
        else if (command.StartsWith("reminder-extend:", StringComparison.Ordinal)
            && Guid.TryParse(command[16..], out var extend))
            action = new("extend", extend);
        else if (command.StartsWith("reminder-snooze-due:", StringComparison.Ordinal))
            action = new("snooze-due", Text: command["reminder-snooze-due:".Length..]);
        else if (command.StartsWith("reminder-dismiss-due:", StringComparison.Ordinal))
            action = new("dismiss-due", Text: command["reminder-dismiss-due:".Length..]);

        var accepted = action != null && _os.Reminder(action);
        if (action is { Kind: "add" })
            _view?.ClockReminderRequestQueued(action.ClockRequestId, action.Text, accepted);
        Feedback(accepted
            ? "Reminder request queued. Its status appears in Clock."
            : "Reminder could not be queued. Enter a short label and try again.");
        _nextRefresh = 0;
    }
}
