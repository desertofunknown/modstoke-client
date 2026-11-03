// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Modstoke.Client;

internal sealed partial class ClientTerminalView
{
    private static readonly ClientTerminalApp[] FieldApps =
    {
        ClientTerminalApp.Navigation, ClientTerminalApp.Messages, ClientTerminalApp.Maps,
        ClientTerminalApp.Journal, ClientTerminalApp.Health, ClientTerminalApp.Watch,
        ClientTerminalApp.Tribe, ClientTerminalApp.Companions, ClientTerminalApp.Services,
        ClientTerminalApp.Devices, ClientTerminalApp.Games, ClientTerminalApp.Settings, ClientTerminalApp.Drone,
        ClientTerminalApp.Pack, ClientTerminalApp.Hangar, ClientTerminalApp.Spaceflight
    };
    private readonly List<ButtonWidget> _drawerApps = new();
    private readonly List<ButtonWidget> _drawerPins = new();
    private readonly List<ClientTerminalApp> _pinnedApps = new();
    private readonly List<ClientTabletNotification> _fieldNotifications = new();
    private readonly List<ClientFieldNote> _fieldNotes = new();
    private readonly List<ButtonWidget> _noteRows = new();
    private RectTransform _journalSurface = null!;
    private RectTransform _healthSurface = null!;
    private RectTransform _watchSurface = null!;
    private RectTransform _pinnedRoot = null!;
    private ScrollArea _homeNotifications = null!;
    private ScrollArea _drawerGrid = null!;
    private ScrollArea _notebookList = null!;
    private TMP_InputField _appSearch = null!;
    private TMP_InputField _missionInput = null!;
    private TMP_InputField _noteTitle = null!;
    private TMP_InputField _noteBody = null!;
    private TextMeshProUGUI _drawerEmpty = null!;
    private TextMeshProUGUI _notificationCount = null!;
    private TextMeshProUGUI _fieldNavigation = null!;
    private TextMeshProUGUI _fieldNavigationDetail = null!;
    private TextMeshProUGUI _fieldStorage = null!;
    private TextMeshProUGUI _fieldFeedback = null!;
    private TextMeshProUGUI _missionFeedback = null!;
    private TextMeshProUGUI _healthAvailability = null!;
    private readonly List<TextMeshProUGUI> _vitalValues = new();
    private readonly List<TextMeshProUGUI> _vitalStates = new();
    private readonly List<Image> _vitalBars = new();
    private TextMeshProUGUI _healthSource = null!;
    private TextMeshProUGUI _watchTime = null!;
    private TextMeshProUGUI _watchState = null!;
    private TextMeshProUGUI _watchNavigation = null!;
    private TextMeshProUGUI _watchNotifications = null!;
    private ButtonWidget _watchTimer = null!;
    private ButtonWidget _noteDelete = null!;
    private ClientTabletWatchState _fieldTimerState;
    private ClientWristWatchVitals? _lastFieldVitals;
    private Guid? _fieldNoteId;
    private string _fieldTitleAccepted = "";
    private string _fieldBodyAccepted = "";
    private string _fieldMissionAccepted = "";
    private string _fieldMissionSnapshot = "";
    private bool _fieldInputsUpdating;
    private bool _noteDeletePending;
    private bool _fieldStateReceived;

    private TextMeshProUGUI BuildFieldHome()
    {
        _launcherClock = Text(_home, "Clock", "", 76f, Ink, 0f, 0f, 276f, 98f, role: TabletTextRole.Display);
        _launcherDate = Text(_home, "Date", "", 27f, Muted, 300f, 38f, 680f, 42f);
        Button(_home, "AllApps", "All apps", 1108f, 24f, 220f, 64f, OpenAppDrawer)
            .Label.alignment = TextAlignmentOptions.Center;
        var mission = RoundedPanel(_home, "FieldGlance", Surface, 0f, 124f, 644f, 196f);
        Text(mission.transform, "Heading", "YOUR NEXT STEP", 21f, Muted, 24f, 18f, 420f, 30f,
            role: TabletTextRole.Status);
        var editMission = Button(mission.transform, "EditMission", "Edit", 510f, 12f, 110f, 44f, OpenMissionEditor);
        editMission.Image.color = Surface;
        editMission.Label.color = Mint;
        editMission.Label.alignment = TextAlignmentOptions.Center;
        Panel(mission.transform, "HeadingRule", Rule, 24f, 56f, 596f, 1f);
        _missionSummary = Text(mission.transform, "Mission", "Choose your next objective", 29f, Ink, 24f, 64f, 596f, 44f, true);
        _missionSummary.overflowMode = TextOverflowModes.Ellipsis;
        var navigation = Button(mission.transform, "NavigationCard", "", 16f, 116f, 612f, 64f,
            () => SelectApp(ClientTerminalApp.Navigation));
        Icon(navigation.Image.transform, ClientTerminalApp.Navigation, Mint, 16f, 16f, 32f);
        _fieldNavigation = Text(navigation.Image.transform, "Destination", "Explore with GPS", 24f, Ink, 64f, 5f, 528f, 30f, true);
        _fieldNavigationDetail = Text(navigation.Image.transform, "Detail", "Choose a saved place", 20f, Muted, 64f, 35f, 528f, 26f);
        _fieldNavigation.overflowMode = _fieldNavigationDetail.overflowMode = TextOverflowModes.Ellipsis;
        RoundedPanel(_home, "NotificationGlance", Surface, 668f, 124f, 660f, 196f);
        _notificationCount = Text(_home, "NotificationsHeading", "NOTIFICATIONS", 21f, Muted, 692f, 142f, 480f, 30f,
            role: TabletTextRole.Status);
        Panel(_home, "NotificationsRule", Rule, 692f, 180f, 612f, 1f);
        _homeNotifications = Scroll(_home, "Notifications", 680f, 184f, 636f, 124f);
        Text(_home, "PinnedHeading", "YOUR APPS", 21f, Muted, 0f, 344f, 900f, 30f,
            role: TabletTextRole.Status);
        Button(_home, "ArrangeApps", "Arrange", 1108f, 336f, 220f, 48f, () =>
        {
            OpenAppDrawer();
            SetPinEditing(true);
        }).Label.alignment = TextAlignmentOptions.Center;
        _pinnedRoot = Node(_home, "PinnedApps");
        Place(_pinnedRoot, 0f, 392f, 1328f, 288f);

        _appDrawer = RoundedPanel(_home, "AppDrawer", Screen, 0f, 0f, 1328f, 688f).rectTransform;
        _appDrawer.GetComponent<UnityEngine.UI.Image>().raycastTarget = true;
        Text(_appDrawer, "Heading", "All apps", 40f, Ink, 24f, 12f, 700f, 56f, true);
        _pinEditingButton = Button(_appDrawer, "Arrange", "Arrange favorites", 790f, 12f, 300f, 60f,
            () => SetPinEditing(!_pinEditing));
        Button(_appDrawer, "Close", "Done", 1110f, 12f, 194f, 60f, CloseLauncherPanels)
            .Label.alignment = TextAlignmentOptions.Center;
        _appSearch = FieldInput(_appDrawer, 24f, 92f, 1280f, 66f, 64, false);
        var searchHint = Text(_appSearch.textViewport, "Placeholder", "Search apps", 27f, Muted, 0f, 0f, 1200f, 40f);
        searchHint.alignment = TextAlignmentOptions.MidlineLeft;
        _appSearch.placeholder = searchHint;
        _appSearch.onValueChanged.AddListener((UnityAction<string>)new Action<string>(_ => FilterFieldApps()));
        _drawerGrid = Scroll(_appDrawer, "AppGrid", 0f, 176f, 1328f, 440f);
        _drawerGrid.Root.color = Screen;
        TextMeshProUGUI? unread = null;
        for (var index = 0; index < FieldApps.Length; index++)
        {
            var app = FieldApps[index];
            var card = LauncherApp(_drawerGrid.Content, app, 0f, 0f, 200f, 132f, compact: true);
            _drawerApps.Add(card);
            var pin = Button(_drawerGrid.Content, "Pin" + app, "+", 0f, 0f, 50f, 46f,
                () => FieldCommand("favorite:" + app));
            Place(pin.Label.rectTransform, 0f, 0f, 50f, 46f);
            pin.Label.alignment = TextAlignmentOptions.Center;
            pin.Label.fontSize = 24f;
            _drawerPins.Add(pin);
            if (app == ClientTerminalApp.Messages)
            {
                unread = Text(card.Image.transform, "Unread", "", 23f, Mint, 152f, 8f, 46f, 32f, true);
                unread.alignment = TextAlignmentOptions.TopRight;
            }
        }
        _drawerEmpty = Text(_appDrawer, "NoMatches", "No apps match your search.", 28f, Muted, 24f, 240f, 1280f, 70f);
        Text(_appDrawer, "Hint", "Tap an app to open it", 23f, Muted, 24f, 626f, 1280f, 40f)
            .alignment = TextAlignmentOptions.Center;
        BuildMissionEditor();
        FilterFieldApps();
        CloseLauncherPanels();
        RefreshFieldPins(Array.Empty<ClientTerminalApp>());
        return unread!;
    }

    private void FilterFieldApps()
    {
        DesktopFilterApps();
    }

    private void BuildFieldApps()
    {
        _journalSurface = FieldSurface("Notebook");
        _healthSurface = FieldSurface("Health");
        _watchSurface = FieldSurface("Watch");
        Text(_journalSurface, "ListHeading", "YOUR NOTES", 20f, Muted, 16f, 12f, 190f, 30f, true);
        Button(_journalSurface, "NewNote", "New note", 206f, 0f, 154f, 52f, () => FieldCommand("note-new"));
        _notebookList = Scroll(_journalSurface, "Notes", 0f, 68f, 360f, 448f);
        Button(_journalSurface, "RefreshProfile", "Refresh storage", 0f, 532f, 360f, 54f,
            () => FieldCommand("profile-refresh"));
        var editor = RoundedPanel(_journalSurface, "Editor", Surface, 384f, 0f, 944f, 600f);
        Text(editor.transform, "TitleLabel", "TITLE", 19f, Muted, 24f, 16f, 896f, 26f, true);
        _noteTitle = FieldInput(editor.transform, 24f, 48f, 896f, 54f, 64, false);
        _noteBody = FieldInput(editor.transform, 24f, 118f, 896f, 306f, 2048, true);
        _noteTitle.onValueChanged.AddListener((UnityAction<string>)new Action<string>(value => EditFieldText("note-title", value)));
        _noteBody.onValueChanged.AddListener((UnityAction<string>)new Action<string>(value => EditFieldText("note-body", value)));
        var save = Button(editor.transform, "Save", "Save note", 24f, 442f, 224f, 56f, () => FieldCommand("note-save"));
        _notebookSave = save;
        save.Image.color = AppColor(ClientTerminalApp.Journal);
        save.Label.color = Color.white;
        _noteDelete = Button(editor.transform, "Delete", "Delete note", 264f, 442f, 250f, 56f, DeleteFieldNote);
        _noteDelete.Button.interactable = false;
        _fieldFeedback = Text(editor.transform, "EditStatus", "Select a note or start writing a new one.", 20f, Muted, 24f, 510f, 896f, 28f);
        _fieldFeedback.overflowMode = TextOverflowModes.Ellipsis;
        _fieldStorage = Text(editor.transform, "Storage", "Session notebook", 18f, Muted, 24f, 548f, 896f, 44f);
        _fieldStorage.overflowMode = TextOverflowModes.Ellipsis;

        Text(_healthSurface, "Heading", "Vital monitor", 32f, Ink, 24f, 10f, 1280f, 44f, true);
        _healthAvailability = Text(_healthSurface, "Availability", "Waiting for local player readings.", 22f, Muted, 24f, 62f, 1280f, 34f);
        _healthAvailability.overflowMode = TextOverflowModes.Ellipsis;
        var labels = new[] { "HEALTH", "STAMINA", "FULLNESS", "HYDRATION", "REST", "AIR" };
        for (var index = 0; index < labels.Length; index++)
        {
            var card = RoundedPanel(_healthSurface, "Vital" + index, Surface, index % 3 * 448f, 118f + index / 3 * 218f, 432f, 198f);
            Text(card.transform, "Label", labels[index], 20f, Muted, 24f, 18f, 384f, 30f,
                role: TabletTextRole.Status);
            var value = Text(card.transform, "Value", "—", 48f, Ink, 24f, 52f, 384f, 64f, role: TabletTextRole.Display);
            value.overflowMode = TextOverflowModes.Ellipsis;
            _vitalValues.Add(value);
            var state = Text(card.transform, "State", "No reading", 21f, Muted, 24f, 118f, 384f, 32f);
            state.overflowMode = TextOverflowModes.Ellipsis;
            _vitalStates.Add(state);
            RoundedPanel(card.transform, "Capacity", Raised, 24f, 164f, 384f, 10f);
            var level = RoundedPanel(card.transform, "Level", Mint, 24f, 164f, 384f, 10f);
            level.gameObject.SetActive(false);
            _vitalBars.Add(level);
            Panel(card.transform, "LowLevelMark", Rule, 119f, 161f, 2f, 16f);
        }
        _healthSource = Text(_healthSurface, "Source", "Local player readings · Low marker: 25%", 20f, Muted, 24f, 564f, 1280f, 30f);
        _healthSource.overflowMode = TextOverflowModes.Ellipsis;

        BuildClockApp();
        BuildFieldWorkflows();
    }

    private RectTransform FieldSurface(string name)
    {
        var surface = Node(_applicationLayer, name);
        Place(surface, 56f, 176f, WorkspaceWidth, WorkspaceHeight);
        surface.gameObject.SetActive(false);
        return surface;
    }

    private TMP_InputField FieldInput(Transform parent, float x, float y, float width, float height, int limit, bool multiline)
    {
        var input = Input(parent, "", limit);
        Place(input.GetComponent<RectTransform>(), x, y, width, height);
        if (multiline)
        {
            input.lineType = TMP_InputField.LineType.MultiLineNewline;
            input.textComponent.enableWordWrapping = true;
            input.textComponent.alignment = TextAlignmentOptions.TopLeft;
        }
        return input;
    }

    internal void SetFieldState(ClientTabletOsSnapshot state, ClientWristWatchSnapshot watch)
    {
        if (_disposed) return;
        if (!_fieldStateReceived || _fieldMissionSnapshot != state.Field.Mission)
        {
            _fieldMissionSnapshot = state.Field.Mission;
            _fieldMissionAccepted = state.Field.Mission;
            if (!_missionInput.isFocused) SetFieldInput(_missionInput, state.Field.Mission);
            RefreshMissionGlance();
        }
        _fieldStorage.text = state.StorageStatus;
        RefreshFieldPlaces(state.Field.Places);
        RefreshFieldPins(state.Field.Favorites);
        RefreshFieldNotifications(state.Notifications);
        RefreshFieldNotes(state.Field.Notes);
        RefreshFieldWorkflowState();
        var destination = watch.PrivateContentAvailable && !string.IsNullOrWhiteSpace(watch.WaypointLabel)
            ? watch.WaypointLabel : "Explore with GPS";
        var distance = watch.PrivateContentAvailable && watch.WaypointDistanceMetres is { } metres && float.IsFinite(metres)
            ? metres.ToString("0", CultureInfo.InvariantCulture) + " m to destination" : "Choose a saved place to navigate.";
        _fieldNavigation.text = destination;
        _fieldNavigationDetail.text = distance;
        if (_lastFieldVitals != watch.Vitals)
        {
            _lastFieldVitals = watch.Vitals;
            RefreshHealthReadings(watch.Vitals);
        }
        _fieldTimerState = state.Watch.State;
        _watchTime.text = WatchTime(state.Watch.RemainingSeconds);
        _watchState.text = state.Watch.State switch
        {
            ClientTabletWatchState.Running => "Timer running",
            ClientTabletWatchState.Paused => "Timer paused",
            ClientTabletWatchState.Elapsed => "Timer complete",
            _ => "Ready to start"
        };
        _watchTimer.Label.text = state.Watch.State switch
        {
            ClientTabletWatchState.Running => "Pause timer",
            ClientTabletWatchState.Paused => "Resume timer",
            _ => "Start timer"
        };
        _watchTime.color = state.Watch.State == ClientTabletWatchState.Elapsed ? Amber : Ink;
        var heading = watch.HeadingDegrees is { } degrees && float.IsFinite(degrees)
            ? "Heading " + degrees.ToString("0", CultureInfo.InvariantCulture) + "°" : "Heading unavailable";
        _watchNavigation.text = heading + "\n" + destination + "\n" + distance;
        _watchNotifications.text = !watch.PrivateContentAvailable ? "Notifications hidden"
            : state.Notifications.Count == 0 ? "No notifications\nOpen notification center"
            : state.Notifications.Count.ToString(CultureInfo.InvariantCulture) + " notifications\nOpen notification center";
        _fieldStateReceived = true;
    }

    private void RefreshHealthReadings(ClientWristWatchVitals vitals)
    {
        var healthKnown = vitals.Health is { } health && float.IsFinite(health) && health >= 0;
        var maximumKnown = vitals.MaximumHealth is { } maximum && float.IsFinite(maximum) && maximum > 0;
        float? healthFactor = healthKnown && maximumKnown ? vitals.Health!.Value / vitals.MaximumHealth!.Value : null;
        var factors = new[] { healthFactor, vitals.Stamina, vitals.Fullness, vitals.Hydration, vitals.Rest, vitals.Air };
        var labels = new[] { "Health", "Stamina", "Fullness", "Hydration", "Rest", "Air" };
        var low = new List<string>();
        var available = 0;
        for (var index = 0; index < factors.Length; index++)
        {
            var known = factors[index] is { } factor && float.IsFinite(factor) && factor >= 0 && factor <= 1f;
            var amount = known ? Mathf.Clamp01(factors[index]!.Value) : 0f;
            var present = index == 0 ? healthKnown : known;
            if (present) available++;
            var isLow = known && amount <= .25f;
            if (isLow) low.Add(labels[index]);
            _vitalValues[index].text = index == 0
                ? healthKnown ? vitals.Health!.Value.ToString("0", CultureInfo.InvariantCulture) : "—"
                : known ? FieldPercent(factors[index]) : "—";
            _vitalValues[index].color = isLow ? Amber : present ? Ink : Muted;
            var description = index == 0 && maximumKnown
                ? "of " + vitals.MaximumHealth!.Value.ToString("0", CultureInfo.InvariantCulture) + " maximum"
                : index == 0 ? "Maximum unavailable" : "Current level";
            _vitalStates[index].text = !present ? factors[index].HasValue ? "Invalid reading" : "No reading"
                : !known ? "Range unavailable · " + description : isLow ? "Low · " + description : description;
            _vitalStates[index].color = isLow ? Amber : Muted;
            _vitalBars[index].gameObject.SetActive(known && amount > 0f);
            _vitalBars[index].rectTransform.sizeDelta = new Vector2(384f * amount, 10f);
            _vitalBars[index].color = isLow ? Amber : Mint;
        }
        _healthAvailability.text = vitals.Condition.Summary + (available == 0 ? " · Vital readings unavailable"
            : available < factors.Length ? " · Some levels unavailable" : "");
        _healthAvailability.color = vitals.Condition.NeedsAttention || low.Count > 0 ? Amber : Muted;
        _healthSource.text = FieldHealthGuidance(factors) + "\n" + available.ToString(CultureInfo.InvariantCulture)
            + " of 6 levels available · Low marker: 25%";
    }

    private void RefreshFieldPins(IReadOnlyList<ClientTerminalApp> apps)
    {
        if (_fieldStateReceived && SameFieldItems(_pinnedApps, apps)) return;
        _pinnedApps.Clear();
        _pinnedApps.AddRange(apps);
        DestroyChildren(_pinnedRoot);
        var visible = 0;
        // An empty favorites list uses a launcher arrangement without writing profile data.
        IReadOnlyList<ClientTerminalApp> shown = apps.Count == 0 ? new[] { ClientTerminalApp.Navigation, ClientTerminalApp.Messages,
            ClientTerminalApp.Maps, ClientTerminalApp.Journal, ClientTerminalApp.Health,
            ClientTerminalApp.Watch, ClientTerminalApp.Games, ClientTerminalApp.Settings } : apps;
        foreach (var app in shown)
        {
            if (Array.IndexOf(FieldApps, app) < 0 || visible >= 8) continue;
            var index = visible++;
            LauncherApp(_pinnedRoot, app, index % 4 * 332f, index / 4 * 144f, 312f, 144f);
        }
        if (visible == 0)
            Text(_pinnedRoot, "Empty", "Choose favorites in All apps.", 26f, Muted, 0f, 10f, 1328f, 56f);
        for (var index = 0; index < FieldApps.Length; index++)
        {
            var pinned = _pinnedApps.Contains(FieldApps[index]);
            _drawerPins[index].Label.text = pinned ? "−" : "+";
            _drawerPins[index].Label.color = pinned ? Mint : Muted;
        }
    }

    private void RefreshFieldNotifications(IReadOnlyList<ClientTabletNotification> notifications)
    {
        if (_fieldStateReceived && SameFieldItems(_fieldNotifications, notifications)) return;
        _fieldNotifications.Clear();
        _fieldNotifications.AddRange(notifications);
        DestroyChildren(_homeNotifications.Content);
        _notificationCount.text = notifications.Count == 0 ? "ALL CAUGHT UP"
            : "NOTIFICATIONS · " + notifications.Count.ToString(CultureInfo.InvariantCulture);
        if (_fieldNoticeReader != null && _fieldNoticeReader.gameObject.activeSelf) RenderFieldNoticeReader();
        var y = 8f;
        for (var index = notifications.Count - 1; index >= 0; index--)
        {
            var notification = notifications[index];
            var id = notification.Id.ToString(CultureInfo.InvariantCulture);
            var card = Button(_homeNotifications.Content, "Notification" + id, "", 8f, y, 492f, 104f,
                () => OpenFieldNoticeReader(notification.Id));
            var title = Text(card.Image.transform, "Title", notification.Title, 26f, Ink, 16f, 8f, 460f, 38f, true);
            var detail = Text(card.Image.transform, "Detail", notification.Detail, 23f, Muted, 16f, 48f, 460f, 44f);
            title.overflowMode = detail.overflowMode = TextOverflowModes.Ellipsis;
            Button(_homeNotifications.Content, "Dismiss" + id, "Clear", 510f, y, 104f, 104f,
                () => FieldCommand("dismiss:" + id)).Label.fontSize = 22f;
            y += 114f;
        }
        if (notifications.Count == 0)
            Text(_homeNotifications.Content, "Empty", "No new notifications", 29f, Ink, 24f, 30f, 580f, 54f);
        _homeNotifications.Height(Math.Max(124f, y));
        _homeNotifications.Reset();
    }

    private void RefreshFieldNotes(IReadOnlyList<ClientFieldNote> notes)
    {
        if (_fieldStateReceived && SameFieldItems(_fieldNotes, notes)) return;
        _fieldNotes.Clear();
        _fieldNotes.AddRange(notes);
        RenderNotebookBrowser();
        if (_fieldPlacePicker != null && _fieldPlacePicker.gameObject.activeSelf) RenderFieldPlacePicker();
    }

    internal void SetFieldEditor(Guid? id, string title, string body, Guid? placeId = null, Guid? browsePlaceId = null)
    {
        if (_disposed) return;
        var changedSelection = _fieldNoteId != id;
        var changedLocation = _fieldNotePlaceId != placeId;
        _fieldNoteId = id;
        _fieldNotePlaceId = placeId;
        if (_fieldNotePlaceFilter != browsePlaceId)
        {
            _fieldNotePlaceFilter = browsePlaceId;
            _notebookSearch.SetTextWithoutNotify("");
            RenderNotebookBrowser(reset: true);
        }
        if (changedSelection || _fieldTitleAccepted != title) SetFieldInput(_noteTitle, title);
        if (changedSelection || _fieldBodyAccepted != body) SetFieldInput(_noteBody, body);
        _fieldTitleAccepted = title;
        _fieldBodyAccepted = body;
        _noteDelete.Button.interactable = id.HasValue;
        if (changedSelection || changedLocation)
        {
            ResetNoteDelete();
            ClearFieldWorkflowConfirmation();
            CloseFieldPlacePickerIfOpen();
            _fieldFeedback.text = id.HasValue ? "Changes are saved when you choose Save note." : "New note. Choose Save note to keep it.";
        }
        RefreshNoteSelection();
        RefreshFieldLocationUi();
        RefreshFieldWorkflowState();
    }

    private void RefreshNoteSelection()
    {
        for (var index = 0; index < _noteRows.Count; index++)
            _noteRows[index].Image.color = _visibleFieldNotes[index] == _fieldNoteId ? Raised : Screen;
    }

    private static bool SameFieldItems<T>(List<T> current, IReadOnlyList<T> next)
    {
        if (current.Count != next.Count) return false;
        for (var index = 0; index < current.Count; index++)
            if (!EqualityComparer<T>.Default.Equals(current[index], next[index])) return false;
        return true;
    }

    private void EditFieldText(string key, string value)
    {
        if (_disposed || _fieldInputsUpdating || ContentObscured) return;
        var result = _edit("field", Guid.Empty, key, value);
        if (_disposed) return;
        if (!result.Succeeded)
        {
            var feedback = key == "mission" ? _missionFeedback : _fieldFeedback;
            feedback.text = string.IsNullOrWhiteSpace(result.Detail) ? result.Status : result.Detail;
            SetFieldInput(key == "mission" ? _missionInput : key == "note-title" ? _noteTitle : _noteBody,
                key == "mission" ? _fieldMissionAccepted : key == "note-title" ? _fieldTitleAccepted : _fieldBodyAccepted);
            return;
        }
        if (key == "mission")
        {
            _fieldMissionAccepted = value;
            _missionFeedback.text = "Mission updated.";
            RefreshMissionGlance();
        }
        else
        {
            if (key == "note-title") _fieldTitleAccepted = value;
            else _fieldBodyAccepted = value;
            ResetNoteDelete();
            _fieldFeedback.text = "Unsaved changes · Choose Save note to keep them.";
        }
        ClearFieldWorkflowConfirmation();
        RefreshFieldWorkflowState();
    }

    private void SetFieldInput(TMP_InputField input, string value)
    {
        if (input.text == value) return;
        _fieldInputsUpdating = true;
        try { input.text = value; }
        finally { _fieldInputsUpdating = false; }
    }

    private void DeactivateFieldInputs()
    {
        _appSearch.DeactivateInputField();
        _packSearch.DeactivateInputField();
        _missionInput.DeactivateInputField();
        _noteTitle.DeactivateInputField();
        _noteBody.DeactivateInputField();
        _notebookSearch.DeactivateInputField();
        CloseFieldPlacePickerIfOpen();
        if (_fieldNoticeReader != null) _fieldNoticeReader.gameObject.SetActive(false);
        ClearFieldWorkflowConfirmation();
        _clockReminderLabel.DeactivateInputField();
        _clockCustomMinutes.DeactivateInputField();
    }

    private void DeleteFieldNote()
    {
        if (_disposed || ContentObscured || !_fieldNoteId.HasValue) return;
        if (!_noteDeletePending)
        {
            _noteDeletePending = true;
            _noteDelete.Label.text = "Confirm delete";
            _fieldFeedback.text = "Select Confirm delete to permanently remove this note.";
            return;
        }
        ResetNoteDelete();
        FieldCommand("note-delete");
    }

    private void ResetNoteDelete()
    {
        _noteDeletePending = false;
        _noteDelete.Label.text = "Delete note";
    }

    private void FieldCommand(string action)
    {
        if (_disposed || ContentObscured) return;
        ResetNoteDelete();
        ClearFieldWorkflowConfirmation();
        _command("field", action);
    }

    private void WatchCommand(string action)
    {
        if (!_disposed && !ContentObscured) _command("watch", action);
    }

    private void FieldWatchTimer() => WatchCommand(_fieldTimerState switch
    {
        ClientTabletWatchState.Running => "timer.pause",
        ClientTabletWatchState.Paused => "timer.resume",
        _ => "timer.start"
    });

    private static string FieldPercent(float? value) => value is { } factor && float.IsFinite(factor)
        ? (Math.Clamp(factor, 0f, 1f) * 100f).ToString("0", CultureInfo.InvariantCulture) + "%" : "Unavailable";
}
