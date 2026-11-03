// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using TMPro;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientTerminalView
{
    private bool _desktopLocked = true;
    private bool _taskSwitcherOpen;
    private bool _wakeDragging;
    private float _wakeDragStart;
    private int _desktopUnread;
    private long _desktopClockMinute = -1;
    private RectTransform _desktopNavigation = null!;
    private RectTransform _wakeScreen = null!;
    private RectTransform _recentScreen = null!;
    private RectTransform _recentGrid = null!;
    private ScrollArea _desktopRecentScroll = null!;
    private TextMeshProUGUI _shellBackLabel = null!;
    private TextMeshProUGUI _shellHomeLabel = null!;
    private TextMeshProUGUI _shellRecentsLabel = null!;
    private TextMeshProUGUI _wakeClock = null!;
    private TextMeshProUGUI _wakeDate = null!;
    private TextMeshProUGUI _wakeWatch = null!;
    private TextMeshProUGUI _wakeUnread = null!;
    private TextMeshProUGUI _wakeConnection = null!;
    private TextMeshProUGUI _recentEmpty = null!;
    private ButtonWidget _clearRecents = null!;
    private readonly List<ClientTerminalApp> _recentOrder = new();
    private readonly List<ButtonWidget> _recentCards = new();
    private readonly List<TextMeshProUGUI> _recentStates = new();
    private IReadOnlyList<ClientTabletAppActivity> _desktopActivities = Array.Empty<ClientTabletAppActivity>();
    private TextMeshProUGUI _desktopRecentHint = null!;
    private TextMeshProUGUI _desktopSwipeHint = null!;
    private RectTransform _desktopSwipeProgress = null!;
    private ButtonWidget _desktopResume = null!;
    private ButtonWidget _desktopRecentHome = null!;
    private ButtonWidget _desktopCancelClear = null!;
    private ButtonWidget _desktopWakeMessages = null!;
    private ButtonWidget _desktopWakeClock = null!;
    private ButtonWidget _desktopWakeReminder = null!;
    private bool _desktopReady;
    private bool? _desktopWakeClockTarget;
    private bool _desktopClearPending;
    private float _desktopWakeStartX;

    private bool DesktopCaptured => _desktopLocked || _taskSwitcherOpen;

    private void BuildDesktop()
    {
        _desktopNavigation = Node(_device.transform, "SystemNavigation");
        Place(_desktopNavigation, 0f, 0f, 1440f, 900f);
        Panel(_desktopNavigation, "NavigationBar", Chrome, 24f, 798f, 1392f, 78f);
        var back = Button(_desktopNavigation, "Back", "‹", 390f, 804f, 180f, 60f,
            () => { if (!TryHandleShellBack()) DesktopCommand("back"); });
        var home = Button(_desktopNavigation, "Home", "", 630f, 804f, 180f, 60f, () =>
        {
            CloseFieldNoticeReaderIfOpen();
            CloseLauncherPanels();
            if (!_disposed && !_desktopLocked) _select(ClientTerminalApp.Home);
        });
        var homeRing = RoundedPanel(home.Image.transform, "HomeRing", ChromeInk, 78f, 4f, 24f, 24f);
        RoundedPanel(homeRing.transform, "Center", Chrome, 4f, 4f, 16f, 16f);
        var recents = Button(_desktopNavigation, "RecentApps", "", 870f, 804f, 180f, 60f,
            () => DesktopCommand(_taskSwitcherOpen ? "recents-close" : "recents"));
        Panel(recents.Image.transform, "RecentOutline", ChromeInk, 78f, 4f, 24f, 24f);
        Panel(recents.Image.transform, "RecentCenter", Chrome, 82f, 8f, 16f, 16f);
        foreach (var button in new[] { back, home, recents })
        {
            button.Image.color = Chrome;
            button.Label.color = ChromeInk;
            button.Label.fontSize = 38f;
            Place(button.Label.rectTransform, 0f, -7f, 180f, 44f);
            button.Label.alignment = TextAlignmentOptions.Center;
        }
        _shellBackLabel = Text(back.Image.transform, "Caption", "Back", 18f, ChromeInk, 0f, 34f, 180f, 26f);
        _shellHomeLabel = Text(home.Image.transform, "Caption", "Home", 18f, ChromeInk, 0f, 34f, 180f, 26f);
        _shellRecentsLabel = Text(recents.Image.transform, "Caption", "Recent apps", 18f, ChromeInk, 0f, 34f, 180f, 26f);
        foreach (var caption in new[] { _shellBackLabel, _shellHomeLabel, _shellRecentsLabel })
        {
            caption.alignment = TextAlignmentOptions.Center;
            caption.enableWordWrapping = false;
            SetShellTextBaseline(caption, 18f);
        }

        _wakeScreen = Panel(_device.transform, "WakeScreen", Screen, 24f, 72f, 1392f, 804f).rectTransform;
        _wakeScreen.GetComponent<UnityEngine.UI.Image>().raycastTarget = true;
        Text(_wakeScreen, "Brand", "MODSTOKE", 27f, Ink, 64f, 42f, 800f, 42f, role: TabletTextRole.Display);
        Text(_wakeScreen, "TabletLabel", "Field tablet", 24f, Muted, 64f, 88f, 800f, 36f);
        _wakeClock = Text(_wakeScreen, "Clock", "", 116f, Ink, 196f, 132f, 1000f, 150f, role: TabletTextRole.Display);
        _wakeClock.alignment = TextAlignmentOptions.Center;
        _wakeDate = Text(_wakeScreen, "Date", "", 30f, Muted, 196f, 294f, 1000f, 48f);
        _wakeDate.alignment = TextAlignmentOptions.Center;
        _desktopWakeMessages = Button(_wakeScreen, "MessageSummary", "Messages  ›", 196f, 370f, 484f, 90f,
            () => DesktopWakeApp(ClientTerminalApp.Messages));
        _desktopWakeClock = Button(_wakeScreen, "ClockSummary", "Clock  ›", 712f, 370f, 484f, 90f,
            () => DesktopWakeApp(ClientTerminalApp.Watch, reminders: false));
        foreach (var entry in new[] { _desktopWakeMessages, _desktopWakeClock })
        {
            entry.Image.color = Surface;
            Place(entry.Label.rectTransform, 26f, 10f, 432f, 34f);
            SetShellTextBaseline(entry.Label, 24f);
        }
        _wakeUnread = Text(_desktopWakeMessages.Image.transform, "UnreadCount", "No unread messages", 22f,
            Muted, 26f, 48f, 432f, 32f);
        _wakeWatch = Text(_desktopWakeClock.Image.transform, "FieldWatch", "No countdown running", 22f, Muted,
            26f, 48f, 432f, 32f);
        _wakeUnread.overflowMode = _wakeWatch.overflowMode = TextOverflowModes.Ellipsis;
        _desktopWakeReminder = Button(_wakeScreen, "ReminderSummary", "Reminders  ›", 196f, 476f, 1000f, 64f,
            () => DesktopWakeApp(ClientTerminalApp.Watch, reminders: true));
        _desktopWakeReminder.Image.color = Surface;
        SetShellTextBaseline(_desktopWakeReminder.Label, 23f);
        _desktopResume = Button(_wakeScreen, "Resume", "Resume", 396f, 560f, 600f, 76f,
            () => DesktopCommand("unlock"));
        _desktopResume.Image.color = Mint;
        _desktopResume.Label.color = Screen;
        _desktopResume.Label.fontSize = 30f;
        _desktopResume.Label.alignment = TextAlignmentOptions.Center;
        _desktopSwipeHint = Text(_wakeScreen, "SwipeHint", "or swipe up", 24f, Muted, 396f, 642f, 600f, 38f);
        _desktopSwipeHint.alignment = TextAlignmentOptions.Center;
        RoundedPanel(_wakeScreen, "SwipeTrack", Raised, 396f, 692f, 600f, 6f);
        _desktopSwipeProgress = RoundedPanel(_wakeScreen, "SwipeProgress", Mint, 396f, 692f, 0f, 6f).rectTransform;
        _wakeConnection = Text(_wakeScreen, "ServerConnection", "", 23f, Muted, 196f, 722f, 1000f, 38f);
        _wakeConnection.alignment = TextAlignmentOptions.Center;
        _wakeConnection.overflowMode = TextOverflowModes.Ellipsis;

        _recentScreen = Panel(_device.transform, "RecentAppsScreen", Screen, 24f, 72f, 1392f, 804f).rectTransform;
        _recentScreen.GetComponent<UnityEngine.UI.Image>().raycastTarget = true;
        Text(_recentScreen, "Title", "Recent apps", 38f, Ink, 64f, 38f, 520f, 52f, true);
        _desktopRecentHint = Text(_recentScreen, "Subtitle", "Pick up where you left off.", 24f, Muted, 64f, 104f, 1264f, 44f);
        _clearRecents = Button(_recentScreen, "ClearRecentApps", "Clear recent apps", 822f, 40f, 280f, 58f,
            DesktopClearRecents);
        _clearRecents.Label.alignment = TextAlignmentOptions.Center;
        _desktopCancelClear = Button(_recentScreen, "CancelClear", "Cancel", 606f, 40f, 200f, 58f,
            () => DesktopSetClearPending(false));
        _desktopCancelClear.Image.gameObject.SetActive(false);
        Button(_recentScreen, "CloseRecentApps", "Done", 1118f, 40f, 210f, 58f,
            () => DesktopCommand("recents-close"));
        _desktopRecentScroll = Scroll(_recentScreen, "RecentApps", 64f, 164f, 1264f, 516f);
        _desktopRecentScroll.Root.color = Screen;
        _recentGrid = _desktopRecentScroll.Content;
        _recentEmpty = Text(_recentScreen, "Empty", "Apps you open will appear here.", 28f,
            Muted, 196f, 330f, 1000f, 80f);
        _recentEmpty.alignment = TextAlignmentOptions.Center;
        _desktopRecentHome = Button(_recentScreen, "BrowseApps", "Browse apps on Home", 446f, 430f, 500f, 72f, () =>
        {
            if (!_disposed && !_desktopLocked && _taskSwitcherOpen) _select(ClientTerminalApp.Home);
        });
        _desktopRecentHome.Label.alignment = TextAlignmentOptions.Center;
        Text(_recentScreen, "ServiceHint", "Closing recent apps leaves timers, reminders and saved notes intact.",
            22f, Muted, 64f, 686f, 1264f, 38f).alignment = TextAlignmentOptions.Center;
        _desktopNavigation.SetAsLastSibling();
        _recentScreen.gameObject.SetActive(false);
        RefreshDesktopInput();
        RefreshDesktopStatus();
        UpdateDesktopClock();
    }

    internal void SetDesktopState(ClientTabletOsSnapshot snapshot)
    {
        if (_disposed) return;
        var wasCaptured = DesktopCaptured;
        var wasSwitcherOpen = _taskSwitcherOpen;
        _desktopLocked = !snapshot.Ready || snapshot.Locked;
        _desktopReady = snapshot.Ready;
        _taskSwitcherOpen = !_desktopLocked && snapshot.TaskSwitcherOpen;
        _desktopUnread = Math.Max(0, snapshot.UnreadMessages);
        _desktopActivities = snapshot.Runtime.Apps;
        _desktopResume.Label.text = snapshot.Ready ? "Resume " + AppName(snapshot.ActiveApp) : "Tablet starting…";
        _desktopResume.Button.interactable = snapshot.Ready;
        _desktopWakeMessages.Button.interactable = snapshot.Ready;
        _desktopWakeClock.Button.interactable = snapshot.Ready;
        _desktopWakeReminder.Button.interactable = snapshot.Ready;
        if (wasSwitcherOpen != _taskSwitcherOpen) DesktopSetClearPending(false);
        var remaining = Math.Max(0, snapshot.Watch.RemainingSeconds);
        var watchTime = (remaining / 60).ToString("00", CultureInfo.InvariantCulture) + ":"
            + (remaining % 60).ToString("00", CultureInfo.InvariantCulture);
        _wakeWatch.text = snapshot.Watch.State switch
        {
            ClientTabletWatchState.Running => watchTime + " remaining",
            ClientTabletWatchState.Paused => "Paused at " + watchTime,
            ClientTabletWatchState.Elapsed => "Timer complete",
            _ => "No countdown running"
        };
        _wakeWatch.color = snapshot.Watch.State is ClientTabletWatchState.Running or ClientTabletWatchState.Elapsed
            ? Mint : Muted;
        var firstReminder = snapshot.Ready && snapshot.Reminders.Items.Count > 0 ? snapshot.Reminders.Items[0] : null;
        _desktopWakeReminder.Label.text = firstReminder == null ? "Reminders  ·  Create a reminder  ›"
            : firstReminder.State == "due" ? "Due now  ·  " + firstReminder.Label + "  ›"
            : "Next reminder  ·  " + firstReminder.Label + "  ·  "
                + Math.Max(1, (firstReminder.RemainingSeconds + 59) / 60).ToString(CultureInfo.InvariantCulture) + "m  ›";
        _desktopWakeReminder.Label.color = firstReminder?.State == "due" ? Amber : Ink;
        if (!snapshot.Ready) _desktopWakeClockTarget = null;
        else if (!_desktopLocked && snapshot.ActiveApp == ClientTerminalApp.Watch && _desktopWakeClockTarget is { } reminderTarget)
        {
            SelectClockTab(reminderTarget);
            _desktopWakeClockTarget = null;
        }
        else if (!_desktopLocked) _desktopWakeClockTarget = null;
        if (DesktopCaptured)
        {
            SetShadeAmount(0f);
            if (!wasCaptured)
            {
                CommitSlider();
                _messageInput.DeactivateInputField();
                DeactivateFieldInputs();
                DeactivateInboxInputs();
                DeactivateSettingsInputs();
                foreach (var field in _fields) field.Input.DeactivateInputField();
            }
        }
        if (!_desktopLocked) DesktopResetWakeGesture();
        _wakeScreen.gameObject.SetActive(_desktopLocked);
        _recentScreen.gameObject.SetActive(_taskSwitcherOpen);
        // Returning to the same app does not trigger Render's page-change restoration.
        if (wasSwitcherOpen && !DesktopCaptured && _shellFocusRestore == null && _page != null
            && _shellReturnStates.TryGetValue(_page.App, out var saved) && ShellScopeMatches(saved, _page))
        {
            _messagesScrollToEnd = false;
            _shellFocusRestore = saved;
        }
        RefreshRecentApps(snapshot.RecentApps, snapshot.ActiveApp);
        DesktopRefreshLauncher();
        RefreshDesktopInput();
        RefreshDesktopStatus();
        UpdateDesktopClock();
    }

    private void RefreshDesktopInput()
    {
        var appInput = !_shadeCapture && !DesktopCaptured;
        _applicationInput.interactable = appInput;
        _applicationInput.blocksRaycasts = appInput;
        _applicationInput.alpha = DesktopCaptured ? 0f : _pageReveal;
        if (_desktopNavigation != null) _desktopNavigation.gameObject.SetActive(!_shadeCapture && !_desktopLocked);
    }

    private void DesktopCommand(string command)
    {
        if (_disposed || _desktopLocked && command != "unlock") return;
        CommitSlider();
        if (command == "unlock") _desktopWakeClockTarget = null;
        if (command == "recents" && _page != null) CaptureShellReturnState(_page);
        if (command == "recents-clear") _shellReturnStates.Clear();
        else if (command.StartsWith("dismiss:", StringComparison.Ordinal)
            && Enum.TryParse<ClientTerminalApp>(command[8..], out var dismissed))
            _shellReturnStates.Remove(dismissed);
        _command("os", command);
        SetShadeAmount(0f);
    }

    private void DesktopWakeApp(ClientTerminalApp app, bool? reminders = null)
    {
        if (_disposed || !_desktopReady || !_desktopLocked) return;
        _desktopWakeClockTarget = reminders;
        DesktopResetWakeGesture();
        _command("os", "open:" + app);
    }

    private void DesktopClearRecents()
    {
        if (_disposed || _desktopLocked || !_taskSwitcherOpen || _recentOrder.Count == 0) return;
        if (!_desktopClearPending)
        {
            DesktopSetClearPending(true);
            return;
        }
        DesktopSetClearPending(false);
        DesktopCommand("recents-clear");
    }

    private void DesktopSetClearPending(bool pending)
    {
        _desktopClearPending = pending;
        _clearRecents.Label.text = pending ? "Confirm clear" : "Clear recent apps";
        _desktopCancelClear.Image.gameObject.SetActive(pending);
        _desktopRecentHint.text = pending
            ? "Close every recent app? Saved content and running timers will stay."
            : "Pick up where you left off. Tap a card to return; dismiss to close it.";
    }

    private void RefreshDesktopStatus()
    {
        if (_wakeUnread == null) return;
        _wakeUnread.text = _desktopUnread == 0 ? "No unread messages"
            : _desktopUnread == 1 ? "1 unread message"
            : _desktopUnread.ToString(CultureInfo.InvariantCulture) + " unread messages";
        _wakeConnection.text = _page == null ? "Server connection pending"
            : _page.Connected ? "Connected to server" : "Server not connected";
    }

    private void UpdateDesktopClock()
    {
        if (_wakeClock == null) return;
        var minute = _islandClock.Key;
        if (_desktopClockMinute == minute) return;
        _desktopClockMinute = minute;
        _wakeClock.text = _islandClock.TimeText;
        _wakeDate.text = _islandClock.DayText;
        _launcherClock.text = _wakeClock.text;
        _launcherDate.text = _islandClock.DayText;
    }

    private void RefreshRecentApps(IReadOnlyList<ClientTerminalApp> apps, ClientTerminalApp activeApp)
    {
        var count = Math.Min(8, apps.Count);
        var changed = count != _recentOrder.Count;
        for (var index = 0; index < count && !changed; index++) changed = apps[index] != _recentOrder[index];
        if (changed)
        {
            DesktopSetClearPending(false);
            DestroyChildren(_recentGrid);
            _recentOrder.Clear();
            _recentCards.Clear();
            _recentStates.Clear();
            for (var index = 0; index < count; index++)
            {
                var app = apps[index];
                _recentOrder.Add(app);
                var root = Node(_recentGrid, "Recent" + app);
                var card = Button(root, "Open", AppName(app), 0f, 0f, 304f, 196f, () =>
                    {
                        if (!_disposed && !_desktopLocked && _taskSwitcherOpen) _select(app);
                    });
                var tile = RoundedPanel(card.Image.transform, "IconTile", AppColor(app), 124f, 8f, 56f, 56f);
                Icon(tile.transform, app, Color.white, 10f, 10f, 36f);
                Place(card.Label.rectTransform, 16f, 70f, 272f, 36f);
                card.Label.fontSize = 27f;
                card.Label.alignment = TextAlignmentOptions.Center;
                var purpose = Text(card.Image.transform, "Purpose", DesktopAppDescription(app), 20f, Muted,
                    12f, 108f, 280f, 48f);
                purpose.alignment = TextAlignmentOptions.Center;
                purpose.overflowMode = TextOverflowModes.Ellipsis;
                var state = Text(card.Image.transform, "State", "", 20f, Muted, 12f, 160f, 280f, 30f);
                state.alignment = TextAlignmentOptions.Center;
                state.overflowMode = TextOverflowModes.Ellipsis;
                var dismiss = Button(root, "Dismiss", "Dismiss", 0f, 202f, 304f, 44f, () =>
                {
                    if (_taskSwitcherOpen) DesktopCommand("dismiss:" + app);
                });
                dismiss.Label.fontSize = 22f;
                dismiss.Label.alignment = TextAlignmentOptions.Center;
                _recentCards.Add(card);
                _recentStates.Add(state);
            }
            LayoutRecentApps();
        }
        for (var index = 0; index < _recentCards.Count; index++)
        {
            var current = _recentOrder[index] == activeApp;
            _recentCards[index].Image.color = current ? Raised : Surface;
            var activity = "Recent app";
            foreach (var item in _desktopActivities)
                if (item.App == _recentOrder[index]) { activity = item.State; break; }
            _recentStates[index].text = _recentOrder[index] == ClientTerminalApp.Messages && _desktopUnread > 0
                ? _desktopUnread.ToString(CultureInfo.InvariantCulture) + " unread · " + activity
                : (current ? "Return · " : "") + activity;
            _recentStates[index].color = current ? Mint : Muted;
        }
        _recentEmpty.gameObject.SetActive(count == 0);
        _desktopRecentHome.Image.gameObject.SetActive(count == 0);
        _clearRecents.Button.interactable = count > 0;
        _clearRecents.Label.color = count > 0 ? Ink : Muted;
    }

    private void LayoutRecentApps()
    {
        var columns = _shellLargeText ? 2 : 4;
        var width = (_desktopRecentScroll.Width - (columns - 1) * 16f) / columns;
        var openHeight = _shellLargeText ? 220f : 196f;
        var dismissHeight = _shellLargeText ? 54f : 44f;
        var rowHeight = openHeight + dismissHeight + 26f;
        for (var index = 0; index < _recentCards.Count; index++)
        {
            var card = _recentCards[index];
            var root = card.Image.transform.parent.Cast<RectTransform>();
            Place(root, index % columns * (width + 16f), index / columns * rowHeight, width, rowHeight - 20f);
            Place(card.Image.rectTransform, 0f, 0f, width, openHeight);
            Place(card.Label.rectTransform, 16f, 70f, width - 32f, _shellLargeText ? 44f : 36f);
            SetShellTextBaseline(card.Label, 27f);
            var icon = card.Image.transform.Find("IconTile").Cast<RectTransform>();
            Place(icon, (width - 56f) / 2f, 8f, 56f, 56f);
            var purpose = card.Image.transform.Find("Purpose").GetComponent<TextMeshProUGUI>();
            Place(purpose.rectTransform, 12f, _shellLargeText ? 118f : 108f, width - 24f, _shellLargeText ? 58f : 48f);
            SetShellTextBaseline(purpose, 20f);
            Place(_recentStates[index].rectTransform, 12f, _shellLargeText ? 180f : 160f, width - 24f, 32f);
            SetShellTextBaseline(_recentStates[index], 20f);
            var dismiss = root.Find("Dismiss").Cast<RectTransform>();
            Place(dismiss, 0f, openHeight + 6f, width, dismissHeight);
            var label = dismiss.Find("Label").GetComponent<TextMeshProUGUI>();
            Place(label.rectTransform, 16f, 0f, width - 32f, dismissHeight);
            SetShellTextBaseline(label, 22f);
        }
        var rows = (_recentCards.Count + columns - 1) / columns;
        _desktopRecentScroll.Height(Math.Max(516f, rows * rowHeight - 20f));
        var offset = _desktopRecentScroll.Content.anchoredPosition;
        offset.y = Math.Clamp(offset.y, 0f, Math.Max(0f, _desktopRecentScroll.Content.rect.height - 516f));
        _desktopRecentScroll.Content.anchoredPosition = offset;
    }

    private static string AppName(ClientTerminalApp app) => app switch
    {
        ClientTerminalApp.Home => "Home",
        ClientTerminalApp.Navigation => "GPS",
        ClientTerminalApp.Maps => "Places",
        ClientTerminalApp.Journal => "Notebook",
        ClientTerminalApp.Watch => "Clock",
        _ => app.ToString()
    };

    private bool UpdateDesktopPointer(Vector2? normalizedBottomLeft, bool pressed, bool held, bool released)
    {
        if (!DesktopCaptured) return false;
        if (!_desktopLocked || !normalizedBottomLeft.HasValue)
        {
            DesktopResetWakeGesture();
            return true;
        }
        var point = normalizedBottomLeft.Value;
        var x = point.x * 1440f;
        var y = (1f - point.y) * 900f;
        if (pressed && x >= 24f && x <= 1416f && y >= 72f && y <= 876f)
        {
            _wakeDragging = true;
            _wakeDragStart = y;
            _desktopWakeStartX = x;
        }
        if (_wakeDragging && (held || released))
        {
            var distance = Math.Max(0f, _wakeDragStart - y);
            var vertical = distance > Math.Abs(x - _desktopWakeStartX);
            var progress = vertical ? Mathf.Clamp01(distance / 110f) : 0f;
            _desktopSwipeProgress.sizeDelta = new Vector2(600f * progress, 6f);
            _desktopSwipeHint.text = progress > 0f ? "Keep swiping up to resume" : "or swipe up";
            if (progress >= 1f && _desktopResume.Button.interactable)
            {
                DesktopResetWakeGesture();
                DesktopCommand("unlock");
            }
        }
        if (released || !held && !pressed) DesktopResetWakeGesture();
        return true;
    }

    private void DesktopResetWakeGesture()
    {
        _wakeDragging = false;
        _desktopSwipeProgress.sizeDelta = new Vector2(0f, 6f);
        _desktopSwipeHint.text = "or swipe up";
    }
}
