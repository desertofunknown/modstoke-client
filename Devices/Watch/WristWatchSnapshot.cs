// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Modstoke.Client;

internal enum ClientWatchCommand { Left, Right, Up, Down, Activate, Back, Home, QuickSettings }

internal enum ClientWatchPage { Home, Apps, Health, Navigation, Notices, Countdown, Stopwatch, Mission, QuickSettings, About, Reminders }

internal enum ClientWatchTheme { Field, Ice, Amber }
internal enum ClientWatchPendingAction { None, ResetCountdown, ResetStopwatch, DismissReminder }

// Device state copied by the tablet worker. There are no Unity objects here.
internal sealed record ClientWatchOsSnapshot
{
    internal const int VitalCount = 5;
    internal const int NoticeLimit = 8;
    internal const int LapLimit = 8;
    internal static readonly IReadOnlyList<ClientWatchPage> Apps = Array.AsReadOnly(new[]
    {
        ClientWatchPage.Health, ClientWatchPage.Navigation, ClientWatchPage.Notices, ClientWatchPage.Countdown,
        ClientWatchPage.Stopwatch, ClientWatchPage.Mission, ClientWatchPage.QuickSettings, ClientWatchPage.About,
        ClientWatchPage.Reminders
    });
    internal bool Ready { get; init; }
    internal long Generation { get; init; }
    internal bool Awake { get; init; }
    internal ClientWatchPage Page { get; init; }
    internal ClientWatchPage SelectedApp { get; init; } = ClientWatchPage.Health;
    internal int Vital { get; init; }
    internal int Notice { get; init; }
    internal int Setting { get; init; }
    internal ClientWatchTheme Theme { get; init; }
    internal bool NightMode { get; init; }
    internal bool HidePrivatePreviews { get; init; }
    internal int CountdownPresetSeconds { get; init; } = 300;
    internal bool StopwatchRunning { get; init; }
    internal long StopwatchTenths { get; init; }
    internal IReadOnlyList<long> StopwatchLaps { get; init; } = Array.Empty<long>();
    internal int SelectedLap { get; init; }
    internal IReadOnlyList<ClientTabletReminder> Reminders { get; init; } = Array.Empty<ClientTabletReminder>();
    internal int SelectedReminder { get; init; }
    internal int WatchReadingPage { get; init; }
    internal ClientWatchPendingAction WatchPendingAction { get; init; }
    internal long WatchFirstLapNumber { get; init; } = 1;
    internal long WatchLapBaselineTenths { get; init; }
    internal string WatchReminderFeedback { get; init; } = "";
    internal string WatchPreferenceFeedback { get; init; } = "";
    internal bool WatchHealthOverview { get; init; }
    internal bool WatchCompassOnly { get; init; }
    internal bool WatchLiveLap { get; init; } = true;
    internal int WatchHelpPage { get; init; }
    internal string WatchNoticeContent { get; init; } = "";
    internal int WatchNoticeCount { get; init; }
    internal int WatchConfirmationSeconds { get; init; }
    internal string WatchConfirmationLabel { get; init; } = "";
    internal static readonly IReadOnlyList<string> WatchHelp = Array.AsReadOnly(new[]
    {
        "HOME\nLeft: timer\nRight: laps\nUp: reminders\nDown: notices",
        "READING\nNotices: up/down\nchanges pages.\nLeft/right changes\nthe notice.",
        "TIMER\nIdle: up/down\nadjust minutes.\nLeft/right picks\na preset.",
        "STOPWATCH\nRight: record lap\nUp: live split\nLeft: lap history\nDown: reset",
        "PRIVACY\nQ opens setup.\nHide previews to\nconceal notices,\nroute and agenda.",
        "FIELD TOOLS\nTimers use real\ntime. Route range\nis direct, flat\ndistance only."
    });

    // Fixed, short lines keep every part of an objective or notice reachable on the wrist.
    // Text elements avoid splitting a surrogate pair or combining character between pages.
    internal static IReadOnlyList<string> WatchReadingPages(string? content)
    {
        var pages = new List<string>();
        var page = new StringBuilder();
        var line = new StringBuilder();
        var columns = 0;
        var lines = 0;
        var word = new List<string>();
        void EndLine()
        {
            if (lines > 0) page.Append('\n');
            page.Append(line);
            line.Clear();
            columns = 0;
            if (++lines < 4) return;
            pages.Add(page.ToString());
            page.Clear();
            lines = 0;
        }
        void EndWord()
        {
            if (word.Count == 0) return;
            if (columns > 0 && columns + 1 + word.Count > 12) EndLine();
            if (columns > 0) { line.Append(' '); columns++; }
            foreach (var element in word)
            {
                if (columns == 12) EndLine();
                line.Append(element);
                columns++;
            }
            word.Clear();
        }
        var elements = StringInfo.GetTextElementEnumerator(content ?? "");
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            if (element is "\r" or "\n" or "\r\n") { EndWord(); EndLine(); continue; }
            if (string.IsNullOrWhiteSpace(element)) EndWord();
            else word.Add(element);
        }
        EndWord();
        if (line.Length > 0) EndLine();
        if (page.Length > 0) pages.Add(page.ToString());
        if (pages.Count == 0) pages.Add("");
        return pages;
    }
}

// Factors are 0..1; null means the local source is unavailable, never zero.
internal sealed record ClientWristWatchVitals
{
    internal ClientHealthObservation Condition { get; init; } = new();
    internal float? Health { get; init; }
    internal float? MaximumHealth { get; init; }
    internal float? Stamina { get; init; }
    internal float? Fullness { get; init; }
    internal float? Hydration { get; init; }
    internal float? Rest { get; init; }
    internal float? Air { get; init; }
}

internal sealed record ClientWristWatchSnapshot
{
    internal ClientWatchOsSnapshot Os { get; init; } = new();
    internal ClientWristWatchVitals Vitals { get; init; } = new();
    internal float? HeadingDegrees { get; init; }
    internal float? WaypointBearingDegrees { get; init; }
    internal float? WaypointDistanceMetres { get; init; }
    internal string WaypointLabel { get; init; } = "";
    internal string Mission { get; init; } = "";
    internal ClientIslandClock Clock { get; init; }
    internal IReadOnlyList<string> Notifications { get; init; } = Array.Empty<string>();
    internal string TimerState { get; init; } = "Idle";
    internal int? TimerRemainingSeconds { get; init; }
    internal bool PrivateContentAvailable { get; init; } = true;
    internal bool ShowPrivatePreviews => PrivateContentAvailable && !Os.HidePrivatePreviews;
}
