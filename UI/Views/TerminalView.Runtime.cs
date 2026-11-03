// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TMPro;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientTerminalView
{
    private TextMeshProUGUI _runtimeSummary = null!;
    private TextMeshProUGUI _runtimeServices = null!;
    private TextMeshProUGUI _runtimeEmpty = null!;
    private ScrollArea _runtimeScroll = null!;
    private readonly List<(RectTransform Root, TextMeshProUGUI Name, TextMeshProUGUI Detail, ButtonWidget Open, ButtonWidget Close)> _runtimeApps = new();
    private readonly Dictionary<ClientWatchTheme, ButtonWidget> _watchThemeChoices = new();
    private ButtonWidget _watchNightChoice = null!;
    private ButtonWidget _watchPrivacyChoice = null!;
    private IReadOnlyList<ClientTabletAppActivity> _runtimeActivities = Array.Empty<ClientTabletAppActivity>();
    private bool _clockRuntimeReady;

    private void BuildRuntimeSettings(RectTransform parent)
    {
        _runtimeScroll = Scroll(parent, "SystemDetails", 0, 0, 1044, 548);
        var content = _runtimeScroll.Content;
        Text(content, "Heading", "System", 34, Ink, 32, 24, 980, 46, true);
        Text(content, "Description", "Device status and activity for your tablet and watch.", 23, Muted, 32, 82, 980, 38);
        RoundedPanel(content, "WorkerCard", Raised, 24, 138, 996, 154);
        _runtimeSummary = Text(content, "WorkerSummary", "Starting services…", 24, Ink, 44, 154, 952, 120, role: TabletTextRole.Status);
        _runtimeSummary.overflowMode = TextOverflowModes.Ellipsis;
        Text(content, "ServicesHeading", "TIMERS & STORAGE", 21, Mint, 32, 322, 980, 32, true);
        _runtimeServices = Text(content, "Services", "", 23, Muted, 32, 370, 980, 220);
        _runtimeServices.enableWordWrapping = true;
        _runtimeServices.richText = false;
        _runtimeServices.overflowMode = TextOverflowModes.Ellipsis;
        Text(content, "AppsHeading", "APP ACTIVITY", 21, Mint, 32, 618, 980, 32, true);
        Text(content, "AppsHelp", "Open an app to return to it. Closing an app keeps timers and reminders running for this world session.",
            22, Muted, 32, 666, 980, 66);
        _runtimeEmpty = Text(content, "NoApps", "Open an app to see its activity here.", 24, Muted, 32, 754, 980, 50);
        // One reusable row per built-in app; snapshots only update text and visibility.
        foreach (var _ in Enum.GetValues<ClientTerminalApp>())
        {
            var index = _runtimeApps.Count;
            var card = RoundedPanel(content, "Activity" + index, Raised, 24, 748 + index * 108, 996, 96).rectTransform;
            var name = Text(card, "Name", "", 26, Ink, 20, 10, 500, 36, true);
            var detail = Text(card, "Detail", "", 21, Muted, 20, 52, 500, 32);
            detail.enableWordWrapping = false;
            detail.overflowMode = TextOverflowModes.Ellipsis;
            var open = Button(card, "Open", "Open", 544, 18, 196, 60, () => ClockOpenRuntimeApp(index));
            var close = Button(card, "Close", "Close", 760, 18, 212, 60, () => CloseRuntimeApp(index));
            card.gameObject.SetActive(false);
            _runtimeApps.Add((card, name, detail, open, close));
        }
        _runtimeScroll.Height(820);
    }

    private void CloseRuntimeApp(int index)
    {
        if (ClockRuntimeCanAct(index) && _runtimeActivities[index].App is not (ClientTerminalApp.Home or ClientTerminalApp.Settings))
            _command("os", "dismiss:" + _runtimeActivities[index].App);
    }

    private bool ClockRuntimeCanAct(int index) => !_disposed && _clockRuntimeReady && !ContentObscured
        && index >= 0 && index < _runtimeActivities.Count && _runtimeApps[index].Root.gameObject.activeInHierarchy;

    private void ClockOpenRuntimeApp(int index)
    {
        if (ClockRuntimeCanAct(index) && _runtimeActivities[index].State != "Active")
            SelectApp(_runtimeActivities[index].App);
    }

    internal void SetRuntimeState(ClientTabletOsSnapshot state)
    {
        if (_disposed || _runtimeSummary == null) return;
        var runtime = state.Runtime;
        _clockRuntimeReady = state.Ready && state.Visible && !state.Locked && !state.TaskSwitcherOpen;
        _runtimeSummary.text = !state.Ready ? "Device status · Unavailable"
            : "Device status · Ready\nSession uptime · " + RuntimeDuration(runtime.UptimeSeconds)
                + (_preferences.DeveloperOptions
                    ? "\nQueue · " + runtime.PendingCommands + " / " + runtime.QueueCapacity
                        + "   ·   Processed · " + runtime.ProcessedCommands + "   ·   Rejected · " + runtime.RejectedCommands
                        + "\nLast dispatch · " + runtime.LastSliceMilliseconds.ToString("0.00", CultureInfo.InvariantCulture) + " ms"
                    : string.Empty);
        var due = state.Reminders.Items.Count(item => item.State == "due");
        var next = state.Reminders.Items.Where(item => item.State != "due").OrderBy(item => item.RemainingSeconds).FirstOrDefault();
        var timer = state.Watch.State switch
        {
            ClientTabletWatchState.Running => WatchTime(state.Watch.RemainingSeconds) + " remaining",
            ClientTabletWatchState.Paused => "Paused · " + WatchTime(state.Watch.RemainingSeconds) + " left",
            ClientTabletWatchState.Elapsed => "Finished · open Clock to restart",
            _ => "Ready · no countdown"
        };
        _runtimeServices.text = !state.Ready ? "Timers and storage unavailable." : "Field timer  ·  " + timer
            + "\nReminders  ·  " + state.Reminders.Items.Count + " total · " + due + " due"
            + "\nNext reminder  ·  " + (next == null ? "None scheduled" : next.Label + " in " + RuntimeDuration(next.RemainingSeconds))
            + "\nWrist stopwatch  ·  " + (state.WristWatch.StopwatchRunning ? "Running"
                : state.WristWatch.StopwatchTenths == 0 ? "Ready" : "Paused")
            + " · " + RuntimeDuration(state.WristWatch.StopwatchTenths / 10)
            + "\nNotebook  ·  " + state.StorageStatus
            + "\nDevice settings  ·  " + (_preferencesStatus?.text.Split('\n')[0] ?? "Loading…");
        _runtimeActivities = state.Ready ? runtime.Apps : Array.Empty<ClientTabletAppActivity>();
        for (var index = 0; index < _runtimeApps.Count; index++)
        {
            var row = _runtimeApps[index];
            var exists = index < _runtimeActivities.Count;
            row.Root.gameObject.SetActive(exists);
            if (!exists) continue;
            var app = _runtimeActivities[index];
            row.Name.text = AppName(app.App);
            row.Detail.text = app.State + " · " + RuntimeDuration(app.ForegroundSeconds) + " on screen · "
                + app.Opens + (app.Opens == 1 ? " open" : " opens");
            row.Open.Button.interactable = _clockRuntimeReady && app.State != "Active";
            row.Open.Label.text = app.State == "Active" ? "Open now" : "Open";
            row.Close.Button.interactable = _clockRuntimeReady && app.App is not (ClientTerminalApp.Home or ClientTerminalApp.Settings);
        }
        _runtimeEmpty.text = state.Ready ? "Open an app to see its activity here." : "App activity unavailable";
        _runtimeEmpty.gameObject.SetActive(_runtimeActivities.Count == 0);
        _runtimeScroll.Height(748 + Math.Max(1, _runtimeActivities.Count) * 108 + 24);
    }

    private static string RuntimeDuration(long seconds)
    {
        seconds = Math.Max(0, seconds);
        return seconds >= 3600 ? $"{seconds / 3600}h {seconds / 60 % 60}m"
            : seconds >= 60 ? $"{seconds / 60}m {seconds % 60}s" : seconds + "s";
    }

    private void BuildWatchPreferences(RectTransform parent)
    {
        Panel(parent, "WatchFaceRule", Rule, 32, 696, 980, 2);
        Text(parent, "WatchAppearance", "Watch appearance", 26, Ink, 32, 724, 980, 40, true);
        var index = 0;
        foreach (var theme in Enum.GetValues<ClientWatchTheme>())
        {
            var choice = theme;
            _watchThemeChoices.Add(theme, Button(parent, "WatchTheme" + theme, theme.ToString(),
                32 + index++ * 328, 782, 310, 66, () => EditPreference("watch-theme", choice.ToString())));
        }
        Text(parent, "WatchNight", "Night display", 25, Ink, 32, 886, 700, 40, true);
        _watchNightChoice = Button(parent, "WatchNightChoice", "Off", 816, 874, 196, 60,
            () => EditPreference("watch-night", (!_preferences.WatchNight).ToString()));
        Text(parent, "WatchPrivacy", "Private previews", 25, Ink, 32, 972, 700, 40, true);
        _watchPrivacyChoice = Button(parent, "WatchPrivacyChoice", "Shown", 816, 960, 196, 60,
            () => EditPreference("watch-private", (!_preferences.WatchPrivate).ToString()));
        Text(parent, "WatchPersistence", "Saved on this computer; you can also change these on the watch.",
            21, Muted, 32, 1050, 980, 40);
    }
}
