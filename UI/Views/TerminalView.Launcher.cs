// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.Events;

namespace Modstoke.Client;

internal sealed partial class ClientTerminalView
{
    private RectTransform _appDrawer = null!;
    private RectTransform _missionEditor = null!;
    private TextMeshProUGUI _launcherClock = null!;
    private TextMeshProUGUI _launcherDate = null!;
    private TextMeshProUGUI _missionSummary = null!;
    private ButtonWidget _pinEditingButton = null!;
    private bool _pinEditing;
    private int _desktopCategory;
    private string? _desktopFavoriteOrder;
    private ClientTerminalApp? _desktopSingleMatch;
    private TextMeshProUGUI _desktopSearchSummary = null!;
    private TextMeshProUGUI _desktopDrawerHint = null!;
    private ButtonWidget _desktopClearSearch = null!;
    private readonly List<ButtonWidget> _desktopCategories = new();
    private ScrollArea _desktopFavoriteList = null!;
    private TextMeshProUGUI _desktopFavoriteHeading = null!;
    private TextMeshProUGUI _desktopCandidateHeading = null!;
    private TextMeshProUGUI _desktopFavoriteEmpty = null!;
    private ButtonWidget _desktopCancelReplace = null!;
    private ClientTerminalApp? _desktopReplacementApp;
    private readonly List<DesktopFavoriteRow> _desktopFavoriteRows = new();

    private sealed record DesktopFavoriteRow(RectTransform Root, TextMeshProUGUI Name,
        ButtonWidget Earlier, ButtonWidget Later, ButtonWidget Remove)
    {
        internal ClientTerminalApp App { get; set; }
    }

    private ButtonWidget LauncherApp(Transform parent, ClientTerminalApp app,
        float x, float y, float width, float height, bool compact = false)
    {
        var card = Button(parent, "App" + app, AppName(app), x, y, width, height, () => SelectApp(app));
        card.Image.color = Screen;
        var iconSize = compact ? 68f : 80f;
        var icon = RoundedPanel(card.Image.transform, "Icon", AppColor(app), (width - iconSize) / 2f, 10f, iconSize, iconSize);
        Icon(icon.transform, app, Color.white, (iconSize - 46f) / 2f, (iconSize - 46f) / 2f, 46f);
        Place(card.Label.rectTransform, 12f, compact ? 84f : 100f, width - 24f, 36f);
        card.Label.fontSize = compact ? 24f : 25f;
        card.Label.alignment = TextAlignmentOptions.Center;
        card.Label.enableWordWrapping = false;
        card.Label.overflowMode = TextOverflowModes.Ellipsis;
        return card;
    }

    private void OpenAppDrawer()
    {
        if (_disposed || ContentObscured) return;
        RememberShellLauncherFocus();
        _missionInput.DeactivateInputField();
        _missionEditor.gameObject.SetActive(false);
        _appDrawer.gameObject.SetActive(true);
        _appDrawer.SetAsLastSibling();
        SetPinEditing(false);
        _appSearch.ActivateInputField();
    }

    private void SetPinEditing(bool editing)
    {
        _pinEditing = editing;
        _desktopReplacementApp = null;
        _pinEditingButton.Label.text = editing ? "Finish arranging" : "Arrange favorites";
        DesktopFilterApps();
    }

    private void CloseLauncherPanels()
    {
        _appSearch.DeactivateInputField();
        _missionInput.DeactivateInputField();
        _appDrawer.gameObject.SetActive(false);
        _missionEditor.gameObject.SetActive(false);
        _desktopReplacementApp = null;
        RestoreShellLauncherFocus();
    }

    private bool CloseLauncherPanelIfOpen()
    {
        if (CloseFieldNoticeReaderIfOpen()) return true;
        if (_page?.App != ClientTerminalApp.Home
            || !_appDrawer.gameObject.activeSelf && !_missionEditor.gameObject.activeSelf) return false;
        CloseLauncherPanels();
        return true;
    }

    private void BuildMissionEditor()
    {
        DesktopBuildLauncherTools();
        _missionEditor = RoundedPanel(_home, "MissionEditor", Surface, 0f, 0f, 1328f, 688f).rectTransform;
        _missionEditor.GetComponent<UnityEngine.UI.Image>().raycastTarget = true;
        Text(_missionEditor, "Heading", "Your next step", 40f, Ink, 40f, 36f, 1000f, 60f, true);
        Text(_missionEditor, "Hint", "Keep a short objective on your home screen and watch.", 26f,
            Muted, 40f, 114f, 1248f, 52f);
        _missionInput = FieldInput(_missionEditor, 40f, 200f, 1248f, 86f, 256, false);
        _missionInput.textComponent.fontSize = 30f;
        _missionInput.onValueChanged.AddListener((UnityAction<string>)new Action<string>(value => EditFieldText("mission", value)));
        _missionFeedback = Text(_missionEditor, "Status", "Changes save as you type.", 25f, Muted,
            40f, 320f, 1248f, 100f);
        _missionFeedback.overflowMode = TextOverflowModes.Ellipsis;
        Button(_missionEditor, "Clear", "Clear objective", 40f, 550f, 340f, 72f,
            () => FieldCommand("mission-clear"));
        var done = Button(_missionEditor, "Done", "Done", 948f, 550f, 340f, 72f, CloseLauncherPanels);
        done.Image.color = Mint;
        done.Label.color = Screen;
    }

    private void OpenMissionEditor()
    {
        if (_disposed || ContentObscured) return;
        RememberShellLauncherFocus();
        _appSearch.DeactivateInputField();
        _appDrawer.gameObject.SetActive(false);
        _missionEditor.gameObject.SetActive(true);
        _missionEditor.SetAsLastSibling();
    }

    private void RefreshMissionGlance() => _missionSummary.text = string.IsNullOrWhiteSpace(_fieldMissionAccepted)
        ? "Choose your next objective" : _fieldMissionAccepted;

    private void DesktopBuildLauncherTools()
    {
        // Keep the established drawer and its profile-backed favorite commands.
        Place(_drawerGrid.Root.rectTransform, 0f, 236f, 1328f, 360f);
        _desktopDrawerHint = _appDrawer.Find("Hint").GetComponent<TextMeshProUGUI>();
        Place(_desktopDrawerHint.rectTransform, 24f, 646f, 1280f, 32f);
        _desktopDrawerHint.fontSize = 21f;
        _desktopSearchSummary = Text(_appDrawer, "SearchSummary", "", 22f, Muted,
            24f, 602f, 1280f, 36f);
        _desktopSearchSummary.alignment = TextAlignmentOptions.Center;
        _desktopSearchSummary.overflowMode = TextOverflowModes.Ellipsis;
        var categories = new[] { "All apps", "Favorites", "Field", "People", "System" };
        for (var index = 0; index < categories.Length; index++)
        {
            var category = index;
            var choice = Button(_appDrawer, "Category" + index, categories[index],
                24f + index * 206f, 176f, 194f, 48f, () =>
                {
                    if (_disposed || ContentObscured) return;
                    _desktopCategory = category;
                    DesktopFilterApps();
                });
            choice.Label.fontSize = 23f;
            choice.Label.alignment = TextAlignmentOptions.Center;
            _desktopCategories.Add(choice);
        }
        _desktopClearSearch = Button(_appDrawer, "ClearSearch", "Clear", 1070f, 176f, 234f, 48f, () =>
        {
            if (_disposed || ContentObscured) return;
            _appSearch.text = "";
            _appSearch.ActivateInputField();
        });
        _desktopClearSearch.Label.alignment = TextAlignmentOptions.Center;
        BuildDesktopFavoriteEditor();
        for (var index = 0; index < _drawerPins.Count; index++)
        {
            var app = FieldApps[index];
            var pin = _drawerPins[index];
            pin.Button.onClick.RemoveAllListeners();
            pin.Button.onClick.AddListener((UnityAction)new Action(() =>
            {
                if (!CanActivateControl(pin.Button)) return;
                TouchFeedback?.Invoke();
                DesktopFavoriteToggle(app);
            }));
        }
        _appSearch.onSubmit.AddListener((UnityAction<string>)new Action<string>(_ =>
        {
            if (!_pinEditing && !string.IsNullOrWhiteSpace(_appSearch.text)
                && _desktopSingleMatch is { } app && _appDrawer.gameObject.activeInHierarchy)
                SelectApp(app);
        }));
        var clockShortcut = Button(_home, "ClockShortcut", "Open Clock ›", 0f, 0f, 1000f, 104f,
            () => SelectApp(ClientTerminalApp.Watch));
        clockShortcut.Image.color = Color.clear;
        Place(clockShortcut.Label.rectTransform, 796f, 38f, 204f, 42f);
        clockShortcut.Label.fontSize = 23f;
        clockShortcut.Label.color = Mint;
        Place(_launcherDate.rectTransform, 300f, 38f, 480f, 42f);
        _launcherDate.overflowMode = TextOverflowModes.Ellipsis;
    }

    private void DesktopFilterApps(bool resetScroll = true)
    {
        if (_drawerApps.Count != FieldApps.Length) return;
        LayoutDesktopDrawer();
        var query = _appSearch.text.Trim();
        var tokens = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var visibleApps = new List<int>();
        _desktopSingleMatch = null;
        for (var index = 0; index < FieldApps.Length; index++)
        {
            var app = FieldApps[index];
            var searchText = AppName(app) + " " + app + " " + DesktopAppDescription(app);
            var matches = DesktopCategoryMatches(app);
            foreach (var token in tokens)
                matches &= searchText.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
            _drawerApps[index].Image.gameObject.SetActive(matches);
            _drawerApps[index].Button.interactable = !_pinEditing;
            _drawerPins[index].Image.gameObject.SetActive(matches && _pinEditing);
            if (!matches) continue;
            visibleApps.Add(index);
            _desktopSingleMatch = app;
        }
        if (_desktopCategory == 1)
            visibleApps.Sort((left, right) => _pinnedApps.IndexOf(FieldApps[left]).CompareTo(_pinnedApps.IndexOf(FieldApps[right])));
        var visible = visibleApps.Count;
        // The catalog scrolls as apps are added; retain readable cards at each text size.
        var columns = _pinEditing ? 2 : _shellLargeText ? 4 : 7;
        var width = (_drawerGrid.Width - 28f - (columns - 1) * 20f) / columns;
        var height = _shellLargeText ? 176f : 132f;
        for (var index = 0; index < visibleApps.Count; index++)
        {
            var appIndex = visibleApps[index];
            var card = _drawerApps[appIndex];
            var x = 14f + index % columns * (width + 20f);
            var y = 4f + index / columns * (height + 12f);
            Place(card.Image.rectTransform, x, y, width, height);
            var icon = card.Image.transform.Find("Icon").Cast<RectTransform>();
            Place(icon, (width - 68f) / 2f, 10f, 68f, 68f);
            Place(card.Label.rectTransform, 12f, _shellLargeText ? 88f : 84f, width - 24f, _shellLargeText ? 76f : 36f);
            card.Label.enableWordWrapping = _shellLargeText;
            SetShellTextBaseline(card.Label, 24f);
            var pinWidth = _shellLargeText ? 64f : 50f;
            var pinHeight = _shellLargeText ? 58f : 46f;
            Place(_drawerPins[appIndex].Image.rectTransform, x + width - pinWidth, y, pinWidth, pinHeight);
            Place(_drawerPins[appIndex].Label.rectTransform, 0f, 0f, pinWidth, pinHeight);
            var unread = card.Image.transform.Find("Unread");
            if (unread != null) Place(unread.Cast<RectTransform>(), width - 48f, 8f, 42f, 32f);
        }
        if (visible != 1) _desktopSingleMatch = null;
        _drawerEmpty.text = _desktopCategory == 1 && _pinnedApps.Count == 0
            ? "No saved favorites. Choose All apps, then Arrange favorites."
            : "No matching apps in this category. Try All apps or clear your search.";
        _drawerEmpty.gameObject.SetActive(visible == 0);
        _drawerGrid.Height(8f + (visible + columns - 1) / columns * (height + 12f));
        if (resetScroll) _drawerGrid.Reset();
        else
        {
            var offset = _drawerGrid.Content.anchoredPosition;
            offset.y = Math.Clamp(offset.y, 0f, Math.Max(0f, _drawerGrid.Content.rect.height - _drawerGrid.Scroll.viewport.rect.height));
            _drawerGrid.Content.anchoredPosition = offset;
        }
        for (var index = 0; index < _desktopCategories.Count; index++)
        {
            _desktopCategories[index].Image.color = index == _desktopCategory ? Raised : Surface;
            _desktopCategories[index].Label.color = index == _desktopCategory ? Mint : Ink;
        }
        if (_desktopSearchSummary == null) return;
        RefreshDesktopFavorites();
        _desktopClearSearch.Button.interactable = query.Length > 0;
        _desktopClearSearch.Label.color = query.Length > 0 ? Ink : Muted;
        _desktopSearchSummary.text = _pinEditing
            ? _desktopReplacementApp is { } replacement
                ? "Choose which favorite to replace with " + AppName(replacement) + ". Cancel keeps your current apps."
                : _pinnedApps.Count.ToString(CultureInfo.InvariantCulture) + " / 8 favorites · ↑ / ↓ changes Home order · + adds · − removes"
            : visible.ToString(CultureInfo.InvariantCulture) + (visible == 1 ? " app" : " apps")
                + (query.Length > 0 ? " found" : " in this category");
        _desktopDrawerHint.text = _pinEditing
            ? _pinnedApps.Count >= 8 ? "Home is full. Choose + on another app, then choose the favorite it replaces."
                : "Favorite changes save immediately. Finish arranging to browse."
            : _desktopSingleMatch.HasValue && query.Length > 0 ? "Press Enter to open the matching app"
            : "Search by name or purpose: timer, notes, chat, navigation…";
    }

    private bool DesktopCategoryMatches(ClientTerminalApp app) => _desktopCategory switch
    {
        1 => _pinnedApps.Contains(app),
        2 => app is ClientTerminalApp.Navigation or ClientTerminalApp.Maps or ClientTerminalApp.Journal
            or ClientTerminalApp.Health or ClientTerminalApp.Watch or ClientTerminalApp.Drone or ClientTerminalApp.Pack or ClientTerminalApp.Hangar or ClientTerminalApp.Spaceflight,
        3 => app is ClientTerminalApp.Messages or ClientTerminalApp.Tribe or ClientTerminalApp.Companions,
        4 => app is ClientTerminalApp.Services or ClientTerminalApp.Devices or ClientTerminalApp.Settings
            or ClientTerminalApp.Games,
        _ => true
    };

    private void DesktopRefreshLauncher()
    {
        var order = string.Join(",", _pinnedApps);
        if (order == _desktopFavoriteOrder) return;
        _desktopFavoriteOrder = order;
        if (_desktopReplacementApp is { } replacement && (_pinnedApps.Contains(replacement) || _pinnedApps.Count < 8))
            _desktopReplacementApp = null;
        DesktopFilterApps(resetScroll: false);
    }

    private void BuildDesktopFavoriteEditor()
    {
        _desktopFavoriteHeading = Text(_appDrawer, "FavoriteOrderHeading", "HOME ORDER", 21f, Mint, 24f, 236f, 390f, 32f, true);
        _desktopCandidateHeading = Text(_appDrawer, "CandidateHeading", "CHOOSE APPS", 21f, Mint, 680f, 236f, 620f, 32f, true);
        _desktopCancelReplace = Button(_appDrawer, "CancelReplacement", "Cancel", 442f, 232f, 202f, 36f, () =>
        {
            _desktopReplacementApp = null;
            DesktopFilterApps();
        });
        _desktopCancelReplace.Label.alignment = TextAlignmentOptions.Center;
        _desktopFavoriteList = Scroll(_appDrawer, "FavoriteOrder", 16f, 274f, 628f, 322f);
        _desktopFavoriteList.Root.color = Screen;
        _desktopFavoriteEmpty = Text(_desktopFavoriteList.Content, "Empty", "Home is showing the starter apps. Add a favorite to choose your own Home layout.",
            25f, Muted, 16f, 20f, 576f, 180f);
        for (var index = 0; index < 8; index++)
        {
            var slot = index;
            var root = RoundedPanel(_desktopFavoriteList.Content, "Favorite" + index, Surface, 8f, 0f, 590f, 74f).rectTransform;
            var name = Text(root, "Name", "", 23f, Ink, 14f, 8f, 280f, 58f, true);
            name.alignment = TextAlignmentOptions.MidlineLeft;
            name.overflowMode = TextOverflowModes.Ellipsis;
            var earlier = Button(root, "Earlier", "↑", 304f, 10f, 64f, 54f, () => DesktopMoveFavorite(slot, -1));
            var later = Button(root, "Later", "↓", 376f, 10f, 64f, 54f, () => DesktopMoveFavorite(slot, 1));
            var remove = Button(root, "Remove", "Remove", 448f, 10f, 128f, 54f, () => DesktopRemoveOrReplaceFavorite(slot));
            earlier.Label.alignment = later.Label.alignment = remove.Label.alignment = TextAlignmentOptions.Center;
            earlier.Label.fontSize = later.Label.fontSize = 30f;
            remove.Label.fontSize = 20f;
            _desktopFavoriteRows.Add(new DesktopFavoriteRow(root, name, earlier, later, remove));
        }
    }

    private void LayoutDesktopDrawer()
    {
        var width = _pinEditing ? 664f : 1328f;
        Place(_drawerGrid.Root.rectTransform, _pinEditing ? 664f : 0f, _pinEditing ? 274f : 236f,
            width, _pinEditing ? 322f : 360f);
        _drawerGrid.Width = width - 14f;
        _drawerGrid.Content.sizeDelta = new Vector2(_drawerGrid.Width, _drawerGrid.Content.sizeDelta.y);
        Place(_drawerEmpty.rectTransform, _pinEditing ? 680f : 24f, _pinEditing ? 290f : 240f,
            _pinEditing ? 616f : 1280f, 110f);
        if (_desktopFavoriteList == null) return;
        _desktopFavoriteList.Root.gameObject.SetActive(_pinEditing);
        _desktopFavoriteHeading.gameObject.SetActive(_pinEditing);
        _desktopCandidateHeading.gameObject.SetActive(_pinEditing);
        _desktopCancelReplace.Image.gameObject.SetActive(_pinEditing && _desktopReplacementApp.HasValue);
    }

    private void RefreshDesktopFavorites()
    {
        if (_desktopFavoriteList == null) return;
        var rowHeight = _shellLargeText ? 96f : 84f;
        for (var index = 0; index < _desktopFavoriteRows.Count; index++)
        {
            var row = _desktopFavoriteRows[index];
            var visible = index < _pinnedApps.Count;
            row.Root.gameObject.SetActive(visible);
            if (!visible) continue;
            var replacing = _desktopReplacementApp.HasValue;
            row.App = _pinnedApps[index];
            Place(row.Root, 8f, index * rowHeight, 590f, rowHeight - 10f);
            row.Name.text = (index + 1).ToString(CultureInfo.InvariantCulture) + "  " + AppName(_pinnedApps[index]);
            Place(row.Name.rectTransform, 14f, 8f, 280f, rowHeight - 26f);
            SetShellTextBaseline(row.Name, 23f);
            row.Earlier.Button.interactable = !replacing && index > 0;
            row.Later.Button.interactable = !replacing && index + 1 < _pinnedApps.Count;
            row.Remove.Label.text = replacing ? "Replace" : "Remove";
            row.Remove.Image.color = replacing ? Mint : Raised;
            row.Remove.Label.color = replacing ? Screen : Ink;
            foreach (var button in new[] { row.Earlier, row.Later, row.Remove })
            {
                var rect = button.Image.rectTransform;
                Place(rect, rect.anchoredPosition.x, (rowHeight - 64f) / 2f, rect.sizeDelta.x, 54f);
            }
        }
        _desktopFavoriteEmpty.gameObject.SetActive(_pinnedApps.Count == 0);
        _desktopFavoriteList.Height(Math.Max(322f, _pinnedApps.Count * rowHeight));
        var position = _desktopFavoriteList.Content.anchoredPosition;
        position.y = Math.Clamp(position.y, 0f, Math.Max(0f, _desktopFavoriteList.Content.rect.height - 322f));
        _desktopFavoriteList.Content.anchoredPosition = position;
        _desktopCancelReplace.Image.gameObject.SetActive(_pinEditing && _desktopReplacementApp.HasValue);
    }

    private bool CanArrangeDesktopFavorites => !_disposed && !ContentObscured && _pinEditing
        && _appDrawer.gameObject.activeInHierarchy;

    private void DesktopFavoriteToggle(ClientTerminalApp app)
    {
        if (!CanArrangeDesktopFavorites) return;
        if (_pinnedApps.Contains(app))
        {
            _desktopReplacementApp = null;
            FieldCommand("favorite-remove:" + app);
            DesktopFilterApps(resetScroll: false);
        }
        else if (_pinnedApps.Count < 8)
            FieldCommand("favorite-add:" + app);
        else
        {
            _desktopReplacementApp = app;
            DesktopFilterApps();
        }
    }

    private void DesktopMoveFavorite(int slot, int direction)
    {
        if (!CanArrangeDesktopFavorites || _desktopReplacementApp.HasValue || slot < 0 || slot >= _desktopFavoriteRows.Count) return;
        var app = _desktopFavoriteRows[slot].App;
        var current = _pinnedApps.IndexOf(app);
        if (current < 0 || current + direction < 0 || current + direction >= _pinnedApps.Count) return;
        FieldCommand("favorite-move:" + app + ":" + direction.ToString(CultureInfo.InvariantCulture));
    }

    private void DesktopRemoveOrReplaceFavorite(int slot)
    {
        if (!CanArrangeDesktopFavorites || slot < 0 || slot >= _desktopFavoriteRows.Count) return;
        var app = _desktopFavoriteRows[slot].App;
        if (!_pinnedApps.Contains(app)) return;
        if (_desktopReplacementApp is { } replacement)
        {
            _desktopReplacementApp = null;
            FieldCommand("favorite-replace:" + app + ":" + replacement);
            DesktopFilterApps();
        }
        else FieldCommand("favorite-remove:" + app);
    }

    private static string DesktopAppDescription(ClientTerminalApp app) => app switch
    {
        ClientTerminalApp.Navigation => "Map navigation and routes",
        ClientTerminalApp.Maps => "Saved places and waypoints",
        ClientTerminalApp.Journal => "Notes and field notebook",
        ClientTerminalApp.Messages => "Chat and conversations",
        ClientTerminalApp.Watch => "Timer, stopwatch and reminders",
        ClientTerminalApp.Health => "Vitals, food and hydration",
        ClientTerminalApp.Tribe => "Team and membership",
        ClientTerminalApp.Companions => "Followers and jobs",
        ClientTerminalApp.Services => "Server services and requests",
        ClientTerminalApp.Devices => "Connected field equipment",
        ClientTerminalApp.Games => "Arcade games and scores",
        ClientTerminalApp.Settings => "Display, sound and preferences",
        ClientTerminalApp.Drone => "Camera, flight and scouting",
        ClientTerminalApp.Pack => "Inventory, equipment and crafting supplies",
        ClientTerminalApp.Hangar => "Shared plane, pilot and passenger seats, aircraft service",
        ClientTerminalApp.Spaceflight => "Spacecraft, pilot and passenger seats, space travel, test world survey and island return",
        _ => "Tablet home"
    };
}
