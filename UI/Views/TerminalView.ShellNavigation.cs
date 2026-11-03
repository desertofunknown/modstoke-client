// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Modstoke.Client;

internal sealed partial class ClientTerminalView
{
    private readonly Dictionary<ClientTerminalApp, ShellReturnState> _shellReturnStates = new();
    private ShellReturnState? _shellRestore;
    private ShellReturnState? _shellFocusRestore;
    private GameObject? _shellLauncherOpener;
    private GameObject? _shellLastAppSelection;
    private ClientTerminalApp? _shellLastFocusApp;
    private bool _shellSkipCapture;
    private bool _shellLayoutInitialized;
    private bool _shellLargeText;
    private long _shellGeneration = long.MinValue;

    private sealed record ShellScrollPosition(ScrollArea Area, float Offset);
    private sealed record ShellReturnState(ClientTerminalApp App, string Provider, Guid? Scope,
        IReadOnlyList<ShellScrollPosition> Scrolls, GameObject? Selection, string? FieldId);

    internal void ResetShellSession(long generation)
    {
        if (_shellGeneration == generation) return;
        _shellGeneration = generation;
        _shellReturnStates.Clear();
        _shellRestore = _shellFocusRestore = null;
        _shellLauncherOpener = null;
        _shellLastAppSelection = null;
        _shellLastFocusApp = null;
        _shellSkipCapture = true;
        _desktopReplacementApp = null;
        _desktopWakeClockTarget = null;
        _desktopFavoriteOrder = null;
    }

    // Render calls this before changing the active page or rebuilding provider widgets.
    private void BeforeShellPageChange(ClientTerminalPage page, bool resetScroll)
    {
        _shellRestore = null;
        var changed = _page == null || _page.App != page.App || _page.ProviderId != page.ProviderId;
        if (changed && _page != null && !_shellSkipCapture) CaptureShellReturnState(_page);
        _shellSkipCapture = false;
        if (changed)
        {
            if (_shellReturnStates.TryGetValue(page.App, out var saved) && ShellScopeMatches(saved, page))
                _shellRestore = saved;
        }
        else if (resetScroll)
            _shellReturnStates.Remove(page.App);
        if (changed)
        {
            _shellFocusRestore = null;
            _shellLastAppSelection = null;
            _shellLastFocusApp = null;
        }
    }

    // The presentation update follows all app snapshots and their content-height changes.
    private void AfterShellPageRender(ClientTerminalPage page, bool resetScroll)
    {
        var saved = _shellRestore;
        _shellRestore = null;
        if (saved == null || !ShellScopeMatches(saved, page)) return;
        _messagesScrollToEnd = false;
        _shellFocusRestore = saved;
    }

    private void CaptureShellReturnState(ClientTerminalPage page)
    {
        // A covered app has no current viewport or input focus to replace its saved position with.
        if (ContentObscured) return;
        var positions = new List<ShellScrollPosition>();
        foreach (var area in _scrollAreas)
            if (area.Scroll != null && area.Content != null && area.Root.gameObject.activeInHierarchy
                && area.Root.transform.IsChildOf(_applicationLayer))
                positions.Add(new ShellScrollPosition(area, area.Content.anchoredPosition.y));
        var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
        if (!ShellControlAvailable(selected) || !selected!.transform.IsChildOf(_applicationLayer))
            selected = _shellLastFocusApp == page.App && ShellControlAvailable(_shellLastAppSelection)
                ? _shellLastAppSelection : null;
        string? fieldId = null;
        if (selected != null)
            foreach (var field in _fields)
                if (field.Input != null && field.Input.gameObject == selected) { fieldId = field.Id; break; }
        _shellReturnStates[page.App] = new ShellReturnState(page.App, page.ProviderId, page.Inputs?.Scope,
            positions, selected, fieldId);
    }

    private static bool ShellScopeMatches(ShellReturnState saved, ClientTerminalPage page) =>
        saved.App == page.App && string.Equals(saved.Provider, page.ProviderId, StringComparison.Ordinal)
        && saved.Scope == page.Inputs?.Scope;

    private bool ShellControlAvailable(GameObject? selected)
    {
        if (selected == null || !selected || !selected.activeInHierarchy) return false;
        foreach (var (control, _) in _comfortFocus)
            if (control != null && control.gameObject == selected) return control.IsInteractable();
        return false;
    }

    private bool ShellControlInViewport(GameObject selected)
    {
        var control = selected.transform.Cast<RectTransform>();
        foreach (var area in _scrollAreas)
        {
            if (area.Scroll == null || area.Scroll.viewport == null || area.Content == null
                || !control.IsChildOf(area.Content)) continue;
            var bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(area.Scroll.viewport, control);
            var viewport = area.Scroll.viewport.rect;
            if (bounds.min.y < viewport.yMin - 1f || bounds.max.y > viewport.yMax + 1f) return false;
        }
        return true;
    }

    private void RememberShellLauncherFocus()
    {
        var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
        _shellLauncherOpener = ShellControlAvailable(selected) && selected!.transform.IsChildOf(_home)
            ? selected : null;
    }

    private void RestoreShellLauncherFocus()
    {
        var selected = _shellLauncherOpener;
        _shellLauncherOpener = null;
        if (!ContentObscured && ShellControlAvailable(selected) && EventSystem.current != null)
            EventSystem.current.SetSelectedGameObject(selected);
    }

    internal bool TryHandleShellBack()
    {
        if (_disposed || FullscreenMode != ClientFullscreenMode.None) return false;
        if (CloseShadeModePicker()) return true;
        if (_shadeCapture) { SetShadeAmount(0f); return true; }
        if (_desktopLocked) return false;
        if (_taskSwitcherOpen)
        {
            if (_desktopClearPending) DesktopSetClearPending(false);
            else DesktopCommand("recents-close");
            return true;
        }
        if (_desktopReplacementApp.HasValue && _appDrawer.gameObject.activeInHierarchy)
        {
            _desktopReplacementApp = null;
            DesktopFilterApps();
            return true;
        }
        if (_page?.App == ClientTerminalApp.Settings && _settingsSearchRoot != null
            && _settingsSearchRoot.gameObject.activeInHierarchy)
        {
            SelectSettingsSection(_settingsSection);
            var opener = _settingsSurface.Find("FindSettings");
            if (opener != null && EventSystem.current != null)
                EventSystem.current.SetSelectedGameObject(opener.gameObject);
            return true;
        }
        if (_page?.App == ClientTerminalApp.Journal && CloseFieldPlacePickerIfOpen()) return true;
        if (_page?.App == ClientTerminalApp.Home && CloseFieldNoticeReaderIfOpen())
        {
            var opener = _home.Find("ReadNotifications");
            if (opener != null && EventSystem.current != null)
                EventSystem.current.SetSelectedGameObject(opener.gameObject);
            return true;
        }
        return _page?.App == ClientTerminalApp.Home && CloseLauncherPanelIfOpen();
    }

    private void UpdateShellNavigation()
    {
        if (_shellFocusRestore is { } saved && _page != null && !ContentObscured)
        {
            _shellFocusRestore = null;
            if (ShellScopeMatches(saved, _page))
            {
                foreach (var position in saved.Scrolls)
                {
                    var area = position.Area;
                    if (area.Scroll == null || area.Content == null || area.Scroll.viewport == null) continue;
                    var maximum = Math.Max(0f, area.Content.rect.height - area.Scroll.viewport.rect.height);
                    area.Scroll.StopMovement();
                    area.Content.anchoredPosition = new Vector2(0f, Math.Clamp(position.Offset, 0f, maximum));
                }
                var system = EventSystem.current;
                var current = system != null ? system.currentSelectedGameObject : null;
                // A fresh click inside the returned app wins over a remembered selection.
                if (!ShellControlAvailable(current) || !current!.transform.IsChildOf(_applicationLayer))
                {
                    var target = saved.Selection;
                    if (saved.FieldId != null)
                    {
                        target = null;
                        foreach (var field in _fields)
                            if (field.Id == saved.FieldId) { target = field.Input.gameObject; break; }
                    }
                    if (system != null && ShellControlAvailable(target) && ShellControlInViewport(target!))
                        system.SetSelectedGameObject(target);
                }
            }
        }
        if (_shellHomeLabel == null) return;
        var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
        if (!ContentObscured && ShellControlAvailable(selected) && selected!.transform.IsChildOf(_applicationLayer))
        {
            _shellLastAppSelection = selected;
            _shellLastFocusApp = _page?.App;
        }
        _shellHomeLabel.color = _page?.App == ClientTerminalApp.Home && !_taskSwitcherOpen ? ChromeMint : ChromeInk;
        _shellRecentsLabel.color = _taskSwitcherOpen ? ChromeMint : ChromeInk;
        _shellBackLabel.text = _desktopClearPending ? "Cancel" : _taskSwitcherOpen ? "Return"
            : _desktopReplacementApp.HasValue ? "Cancel"
            : _settingsSearchRoot != null && _settingsSearchRoot.gameObject.activeInHierarchy ? "Close finder"
            : _appDrawer.gameObject.activeInHierarchy || _missionEditor.gameObject.activeInHierarchy
                || _fieldNoticeReader != null && _fieldNoticeReader.gameObject.activeInHierarchy ? "Close panel" : "Back";
    }

    private void RefreshShellTextLayout(ClientTabletTextSize size)
    {
        var large = size == ClientTabletTextSize.Large;
        if (_shellLayoutInitialized && _shellLargeText == large) return;
        _shellLayoutInitialized = true;
        _shellLargeText = large;
        if (_desktopSearchSummary != null) DesktopFilterApps(resetScroll: false);
        if (_desktopRecentScroll != null) LayoutRecentApps();
    }
}
