// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Modstoke.Client;

internal sealed class ClientWristWatchDisplay : IDisposable
{
    internal const int VitalCount = ClientWatchOsSnapshot.VitalCount;
    private static readonly string[] VitalNames = { "STAMINA", "HYDRATION", "FULLNESS", "REST", "LUNG AIR" };
    private static readonly string[] Directions = { "NORTH", "NORTH EAST", "EAST", "SOUTH EAST", "SOUTH", "SOUTH WEST", "WEST", "NORTH WEST" };
    private static readonly string[] ShortDirections = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
    private readonly ClientTabletTypography _typography = new();
    private readonly ClientTabletSurfaces _surfaces = new();
    private GameObject? _root;
    private Canvas? _canvas;
    private Image _glass = null!;
    private Image _divider = null!;
    private Image _vitalTrack = null!;
    private Image _appCard = null!;
    private Image _primaryControl = null!;
    private GameObject _appIndicators = null!;
    private readonly GameObject[] _appIcons = new GameObject[ClientWatchOsSnapshot.Apps.Count];
    private readonly Image[] _appPositions = new Image[ClientWatchOsSnapshot.Apps.Count];
    private readonly List<Image> _iconStrokes = new();
    private GameObject _headerControls = null!;
    private readonly TextMeshProUGUI[] _headerLabels = new TextMeshProUGUI[3];
    private TextMeshProUGUI _clock = null!;
    private TextMeshProUGUI _title = null!;
    private TextMeshProUGUI _main = null!;
    private TextMeshProUGUI _caption = null!;
    private TextMeshProUGUI _detail = null!;
    private TextMeshProUGUI _hint = null!;
    private TextMeshProUGUI _controls = null!;
    private TextMeshProUGUI _arrow = null!;
    private TextMeshProUGUI _navigation = null!;
    private GameObject _health = null!;
    private TextMeshProUGUI _vitalLabel = null!;
    private TextMeshProUGUI _vitalValue = null!;
    private Image _vitalBar = null!;
    private Color _ink;
    private Color _muted;
    private Color _accent;
    private Color _amber;
    private Color _low;
    private ClientWatchTheme? _paletteTheme;
    private bool _paletteNightMode;
    private bool _disposed;
    private string? _watchReadingContent;
    private IReadOnlyList<string> _watchReadingPages = Array.Empty<string>();
    internal bool IsAvailable => !_disposed && _root != null && _root.activeInHierarchy
        && _canvas != null && _canvas.isActiveAndEnabled;

    internal ClientWristWatchDisplay(Transform mount, Camera camera, int layer)
    {
        try
        {
            _root = new GameObject("Field watch display") { layer = layer };
            _root.transform.SetParent(mount, false);
            var canvas = _root.AddComponent<Canvas>();
            _canvas = canvas;
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = camera;
            var rect = _root.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(320, 344);
            // +Y follows the forearm; the raised wrist reads with fingers to the right.
            rect.localRotation = Quaternion.Euler(0, 0, 90);
            rect.localScale = Vector3.one * (.044f / 344);
            _root.AddComponent<CanvasScaler>().dynamicPixelsPerUnit = 1;
            _glass = Panel(rect, "Instrument glass", Color.black, 0, 0, 320, 344);
            _appCard = Panel(rect, "Selected app card", Color.white, 14, 62, 292, 194, rounded: true);
            for (var i = 0; i < _appIcons.Length; i++)
                _appIcons[i] = AppIcon(_appCard.transform, ClientWatchOsSnapshot.Apps[i]);
            _appCard.gameObject.SetActive(false);
            _appIndicators = Node(rect, "App position", 0, 260, 320, 5).gameObject;
            for (var i = 0; i < _appPositions.Length; i++)
                _appPositions[i] = Panel(_appIndicators.transform, "App position " + i, Color.white,
                    (320 - (_appPositions.Length - 1) * 18 - 10) / 2f + i * 18, 0, 10, 4, rounded: true);
            _appIndicators.SetActive(false);
            _title = Text(rect, "App", 30, 18, 8, 184, 42, TabletTextRole.Heading);
            _clock = Text(rect, "Island time", 27, 204, 9, 98, 42);
            _clock.alignment = TextAlignmentOptions.TopRight;
            _headerControls = Node(rect, "Watch navigation", 0, 0, 320, 50).gameObject;
            for (var i = 0; i < _headerLabels.Length; i++)
            {
                var text = Text(_headerControls.transform, "Watch navigation " + i, 23, 8 + i * 104, 8, 96, 42, TabletTextRole.Heading);
                text.alignment = TextAlignmentOptions.Center;
                text.text = i == 0 ? "BACK" : i == 1 ? "H HOME" : "Q SETUP";
                _headerLabels[i] = text;
            }
            _headerControls.SetActive(false);
            _divider = Panel(rect, "Top divider", Color.white, 18, 51, 284, 2);
            _main = Text(rect, "Main reading", 88, 18, 54, 284, 132, TabletTextRole.Display);
            _main.alignment = TextAlignmentOptions.Center;
            _main.enableAutoSizing = true;
            _main.fontSizeMin = 62;
            _main.fontSizeMax = 88;
            _caption = Text(rect, "Reading label", 31, 18, 186, 284, 44);
            _caption.alignment = TextAlignmentOptions.Center;
            _detail = Text(rect, "App content", 36, 18, 75, 284, 186, TabletTextRole.Body);
            _detail.enableWordWrapping = true;
            _detail.maxVisibleLines = 4;
            _arrow = Text(rect, "Waypoint arrow", 72, 18, 68, 78, 104);
            _arrow.alignment = TextAlignmentOptions.Center;
            _arrow.text = "▲";
            _arrow.rectTransform.pivot = new Vector2(.5f, .5f);
            _arrow.rectTransform.anchoredPosition = new Vector2(57, -120);
            _navigation = Text(rect, "Waypoint", 28, 18, 230, 284, 70);
            _navigation.alignment = TextAlignmentOptions.Center;
            _navigation.enableWordWrapping = true;
            _navigation.maxVisibleLines = 2;
            _health = Node(rect, "Selected vital", 0, 216, 320, 84).gameObject;
            _vitalLabel = Text(_health.transform, "Vital", 28, 18, 0, 284, 30, TabletTextRole.Heading);
            _vitalLabel.alignment = TextAlignmentOptions.MidlineLeft;
            _vitalValue = Text(_health.transform, "Vital reading", 44, 18, 28, 284, 48, TabletTextRole.Display);
            _vitalValue.alignment = TextAlignmentOptions.MidlineRight;
            _vitalValue.enableAutoSizing = true;
            _vitalValue.fontSizeMin = 40;
            _vitalValue.fontSizeMax = 44;
            _vitalTrack = Panel(_health.transform, "Vital track", Color.white, 18, 77, 284, 7);
            _vitalBar = Panel(_health.transform, "Vital value", Color.white, 18, 77, 284, 7);
            _hint = Text(rect, "Secondary control", 25, 18, 265, 284, 36);
            _hint.alignment = TextAlignmentOptions.Center;
            _primaryControl = Panel(rect, "Primary control surface", Color.white, 18, 302, 284, 38, rounded: true);
            _primaryControl.gameObject.SetActive(false);
            _controls = Text(rect, "Primary control", 28, 18, 303, 284, 36, TabletTextRole.Heading);
            _controls.alignment = TextAlignmentOptions.Center;
        }
        catch { Dispose(); throw; }
    }

    // Normalized from the bottom-left of the display, matching the watch's physical touch surface.
    internal static Vector2 TouchPoint(ClientWatchCommand command) => command switch
    {
        ClientWatchCommand.Left => new(.13f, .50f),
        ClientWatchCommand.Right => new(.87f, .50f),
        ClientWatchCommand.Up => new(.50f, .74f),
        ClientWatchCommand.Down => new(.50f, .22f),
        ClientWatchCommand.Activate => new(.50f, .08f),
        ClientWatchCommand.Back => new(.175f, .92f),
        ClientWatchCommand.Home => new(.50f, .92f),
        ClientWatchCommand.QuickSettings => new(.825f, .92f),
        _ => new(.50f, .50f)
    };

    internal void Refresh(ClientWristWatchSnapshot snapshot, bool inspecting)
    {
        if (_disposed || _root == null) return;
        var os = snapshot.Os;
        ApplyPalette(os);
        ResetLayout();
        var time = snapshot.Clock;
        _clock.text = time.TimeText;
        if (!os.Ready)
        {
            _title.text = "WATCH";
            _main.text = "—";
            _caption.text = "UNAVAILABLE";
            return;
        }
        var awake = inspecting && os.Awake;
        var page = awake ? os.Page : ClientWatchPage.Home;
        _title.text = PageName(page).ToUpperInvariant();
        switch (page)
        {
            case ClientWatchPage.Home: Home(snapshot, time, awake); break;
            case ClientWatchPage.Apps: Apps(snapshot); break;
            case ClientWatchPage.Health:
                if (os.WatchHealthOverview) HealthOverview(snapshot.Vitals, os.Vital);
                else Health(snapshot.Vitals, os.Vital);
                break;
            case ClientWatchPage.Navigation: Navigation(snapshot); break;
            case ClientWatchPage.Notices: Notices(snapshot); break;
            case ClientWatchPage.Countdown: Countdown(snapshot); break;
            case ClientWatchPage.Stopwatch: Stopwatch(os); break;
            case ClientWatchPage.Mission: Mission(snapshot); break;
            case ClientWatchPage.QuickSettings: QuickSettings(os); break;
            case ClientWatchPage.About: About(os); break;
            case ClientWatchPage.Reminders: Reminders(snapshot); break;
        }
        if (awake && os.WatchPendingAction != ClientWatchPendingAction.None)
            WatchConfirmation(snapshot);
        if (!awake) _hint.text = _controls.text = "";
        _title.gameObject.SetActive(!awake);
        _clock.gameObject.SetActive(!awake);
        _headerControls.SetActive(awake);
        _primaryControl.gameObject.SetActive(awake && _controls.text.Length > 0);
    }

    private void ResetLayout()
    {
        _title.gameObject.SetActive(true);
        _clock.gameObject.SetActive(true);
        _headerControls.SetActive(false);
        _main.gameObject.SetActive(true);
        Place(_main, 18, 54, 284, 132);
        _main.fontSize = 88;
        _main.fontSizeMax = 88;
        _main.fontSizeMin = 62;
        _main.color = _ink;
        _main.text = "";
        Place(_caption, 18, 186, 284, 44);
        _caption.text = "";
        _caption.color = _muted;
        _caption.fontSize = 31;
        _detail.gameObject.SetActive(false);
        Place(_detail, 18, 75, 284, 186);
        _detail.alignment = TextAlignmentOptions.TopLeft;
        _detail.enableWordWrapping = true;
        _detail.fontSize = 36;
        _detail.enableAutoSizing = false;
        _detail.maxVisibleLines = 4;
        _detail.text = "";
        _detail.color = _ink;
        _health.SetActive(false);
        _appCard.gameObject.SetActive(false);
        _appIndicators.SetActive(false);
        _primaryControl.gameObject.SetActive(false);
        _navigation.gameObject.SetActive(false);
        Place(_navigation, 18, 230, 284, 70);
        _navigation.fontSize = 28;
        _navigation.maxVisibleLines = 2;
        _navigation.color = _ink;
        _navigation.text = "";
        _arrow.gameObject.SetActive(false);
        _arrow.rectTransform.localRotation = Quaternion.identity;
        _hint.text = "";
        Place(_hint, 18, 265, 284, 36);
        _hint.fontSize = 25;
        _controls.text = "";
        _controls.fontSize = 28;
        _controls.color = _accent;
    }

    private void Home(ClientWristWatchSnapshot snapshot, ClientIslandClock time, bool awake)
    {
        _title.text = "MODSTOKE";
        _clock.text = awake ? "HOME" : "SLEEP";
        _main.text = time.TimeText;
        _caption.text = time.Available ? "HOME · " + time.DayText.ToUpperInvariant() : "HOME · TIME —";
        _caption.fontSize = 28;
        Place(_caption, 18, 186, 284, 36);
        _navigation.gameObject.SetActive(true);
        _navigation.text = (snapshot.TimerState is "Running" or "Paused") && snapshot.TimerRemainingSeconds is >= 0
            ? (snapshot.TimerState == "Paused" ? "PAUSED " : "TIMER ") + Duration(snapshot.TimerRemainingSeconds.Value)
            : snapshot.TimerState == "Elapsed" ? "TIMER FINISHED"
            : snapshot.Os.StopwatchRunning ? "STOPWATCH RUNNING"
            : !awake ? "STANDBY" : "FIELD WATCH";
        var due = WatchDueCount(snapshot.Os);
        var agenda = !snapshot.ShowPrivatePreviews ? snapshot.Os.HidePrivatePreviews ? "PREVIEWS HIDDEN" : "TABLET UNAVAILABLE"
            : due > 0 ? due + " REMINDER" + (due == 1 ? "" : "S") + " DUE"
            : snapshot.Os.Reminders.Count > 0 ? "NEXT IN " + Duration(WatchNextReminderSeconds(snapshot.Os))
            : snapshot.Notifications.Count > 0 ? snapshot.Notifications.Count + " NOTICES" : "NO REMINDERS";
        _navigation.text += "\n" + agenda;
        _navigation.fontSize = 24;
        _navigation.color = snapshot.ShowPrivatePreviews && due > 0 ? _amber : _ink;
        Place(_navigation, 18, 225, 284, 68);
        _controls.text = "ENTER  APPS";
        if (awake)
        {
            Place(_main, 18, 54, 284, 100);
            Place(_caption, 18, 153, 284, 36);
            Place(_navigation, 18, 190, 284, 62);
            _navigation.fontSize = 22;
            _hint.fontSize = 21;
            _hint.text = "←TIMER →LAPS ↑DUE ↓NEWS";
        }
    }

    private void Apps(ClientWristWatchSnapshot snapshot)
    {
        var os = snapshot.Os;
        _appCard.gameObject.SetActive(true);
        _appIndicators.SetActive(true);
        var position = 1;
        for (var i = 0; i < _appIcons.Length; i++)
        {
            var selected = ClientWatchOsSnapshot.Apps[i] == os.SelectedApp;
            if (selected) position = i + 1;
            _appIcons[i].SetActive(selected);
            _appPositions[i].color = selected ? _accent : Dim(_accent, .40f);
        }
        _main.gameObject.SetActive(false);
        _detail.gameObject.SetActive(true);
        _detail.alignment = TextAlignmentOptions.Center;
        _detail.fontSize = 44;
        _detail.maxVisibleLines = 1;
        Place(_detail, 24, 155, 272, 62);
        _detail.text = os.SelectedApp == ClientWatchPage.QuickSettings ? "Setup" : PageName(os.SelectedApp);
        _caption.text = WatchAppStatus(snapshot);
        Place(_caption, 18, 216, 284, 38);
        _caption.fontSize = 23;
        _hint.text = "↑↓  " + position + " / " + _appIcons.Length + "  SELECT";
        _controls.text = "ENTER  OPEN";
    }

    private void Health(ClientWristWatchVitals vitals, int selected)
    {
        _main.text = Number(vitals.Health);
        Place(_main, 18, 54, 284, 102);
        _navigation.gameObject.SetActive(true);
        Place(_navigation, 18, 156, 284, 30);
        _navigation.fontSize = 23;
        _navigation.maxVisibleLines = 1;
        _navigation.text = vitals.Condition.WatchSummary;
        _navigation.color = vitals.Condition.Urgent ? _low : vitals.Condition.NeedsAttention ? _amber : _muted;
        _caption.text = vitals.MaximumHealth is { } maximum && float.IsFinite(maximum) && maximum > 0
            ? "HEALTH / " + maximum.ToString("0", CultureInfo.InvariantCulture) : "HEALTH / —";
        Place(_caption, 18, 186, 284, 30);
        _caption.fontSize = 27;
        if (vitals.Health is { } health && float.IsFinite(health) && health >= 0
            && vitals.MaximumHealth is { } max && float.IsFinite(max) && max > 0 && health / max < .25f)
        {
            _main.color = _low;
            _caption.text = "LOW HEALTH / " + max.ToString("0", CultureInfo.InvariantCulture);
        }
        var index = Math.Clamp(selected, 0, VitalCount - 1);
        var value = Vital(vitals, index);
        var known = value is { } factor && float.IsFinite(factor) && factor >= 0;
        var amount = known ? Mathf.Clamp01(value!.Value) : 0;
        _health.SetActive(true);
        _vitalLabel.text = VitalNames[index] + "  " + (index + 1) + "/" + VitalCount;
        _vitalBar.gameObject.SetActive(known);
        _vitalBar.rectTransform.sizeDelta = new Vector2(284 * amount, 7);
        _vitalBar.color = amount < .20f ? _low : amount < .35f ? _amber : _accent;
        _vitalValue.color = known ? _vitalBar.color : _ink;
        _vitalValue.fontSizeMin = 29;
        _vitalValue.fontSizeMax = 40;
        _vitalValue.text = known ? (amount < .20f ? "LOW · " : amount < .35f ? "CAUTION · " : "")
            + Math.Round(amount * 100).ToString(CultureInfo.InvariantCulture) + "%" : "UNAVAILABLE";
        _controls.text = "ENTER  ALL VITALS";
    }

    private void HealthOverview(ClientWristWatchVitals vitals, int selected)
    {
        ReadingPage();
        _caption.text = vitals.Condition.WatchSummary;
        _caption.fontSize = 23;
        _caption.color = vitals.Condition.Urgent ? _low : vitals.Condition.NeedsAttention ? _amber : _muted;
        _detail.fontSize = 26;
        _detail.maxVisibleLines = 5;
        _detail.enableWordWrapping = false;
        var details = "";
        for (var index = 0; index < VitalCount; index++)
        {
            var value = Vital(vitals, index);
            var known = value is >= 0 and <= 1;
            var amount = known ? value!.Value : 0;
            details += (index == 0 ? "" : "\n") + (selected == index ? "> " : "  ")
                + VitalNames[index] + " " + (known ? (amount < .35f ? "! " : "")
                    + Math.Round(amount * 100).ToString(CultureInfo.InvariantCulture) + "%" : "—");
        }
        _detail.text = details;
        _hint.fontSize = 22;
        _hint.text = "↑↓ SELECT · ! BELOW 35%";
        _controls.text = "ENTER  DETAIL";
    }

    private void Navigation(ClientWristWatchSnapshot snapshot)
    {
        _title.text = "NAVIGATE";
        var heading = Degrees(snapshot.HeadingDegrees);
        _main.text = heading is { } direction ? WatchBearing(direction) : "—";
        _caption.text = heading is { } bearing ? "COMPASS · " + Directions[(int)MathF.Round(bearing / 45) % 8] : "COMPASS · NO HEADING";
        _caption.fontSize = 25;
        _navigation.gameObject.SetActive(true);
        Place(_navigation, 18, 230, 284, 70);
        var waypoint = snapshot.ShowPrivatePreviews ? Degrees(snapshot.WaypointBearingDegrees) : null;
        _controls.text = snapshot.Os.WatchCompassOnly ? "ENTER  ROUTE" : "ENTER  COMPASS";
        _navigation.text = !snapshot.PrivateContentAvailable ? "Route unavailable"
            : snapshot.Os.HidePrivatePreviews ? "Route preview hidden" : waypoint == null ? "NO ROUTE DATA" : snapshot.WaypointLabel ?? "";
        if (snapshot.Os.WatchCompassOnly)
        {
            Place(_navigation, 18, 230, 284, 64);
            _navigation.fontSize = 25;
            _navigation.text = "COMPASS VIEW\n" + (waypoint != null ? "Route kept selected" : "No route bearing");
        }
        else if (waypoint != null)
        {
            Place(_caption, 18, 186, 284, 32);
            Place(_navigation, 18, 220, 284, 36);
            _navigation.maxVisibleLines = 1;
            Place(_hint, 18, 264, 284, 34);
            _hint.fontSize = 22;
            _hint.text = heading is { } compass ? WatchTurn(compass, waypoint.Value)
                : "TARGET " + WatchBearing(waypoint.Value) + " " + ShortDirections[(int)MathF.Round(waypoint.Value / 45) % 8];
            var distance = snapshot.WaypointDistanceMetres;
            var known = distance is >= 0 && float.IsFinite(distance.Value);
            _main.text = known ? distance!.Value >= 1000
                ? (distance.Value / 1000).ToString("0.0", CultureInfo.InvariantCulture)
                : distance.Value.ToString("0", CultureInfo.InvariantCulture) : "—";
            var near = known && distance!.Value <= 15;
            _caption.text = near ? "NEAR PIN · FLAT m"
                : known ? distance!.Value >= 1000 ? "DIRECT FLAT km" : "DIRECT FLAT m" : "ROUTE · NO DISTANCE";
            if (near)
            {
                _caption.color = _amber;
                _hint.text = "CHECK HEIGHT / TERRAIN";
            }
            if (heading is { } h && waypoint is { } w)
            {
                _arrow.gameObject.SetActive(true);
                _arrow.rectTransform.localRotation = Quaternion.Euler(0, 0, -Mathf.DeltaAngle(h, w));
                Place(_main, 109, 54, 193, 132);
                _main.fontSizeMin = 54;
            }
        }
    }

    private void Notices(ClientWristWatchSnapshot snapshot)
    {
        ReadingPage();
        if (!PrivatePreview(snapshot)) return;
        var count = snapshot.Os.WatchNoticeCount;
        if (count == 0)
        {
            _caption.text = "NO NOTICES";
            _detail.text = "No notices.";
            return;
        }
        var index = Math.Clamp(snapshot.Os.Notice, 0, count - 1);
        _caption.text = "NOTICE " + (index + 1) + " OF " + count;
        WatchReading(snapshot.Os.WatchNoticeContent, snapshot.Os.WatchReadingPage);
        _controls.text = "ENTER  NEXT";
    }

    private void Countdown(ClientWristWatchSnapshot snapshot)
    {
        _title.text = "TIMER";
        var idle = snapshot.TimerState is "Idle" or "Elapsed";
        _main.text = idle ? Duration(snapshot.Os.CountdownPresetSeconds)
            : snapshot.TimerRemainingSeconds is >= 0 ? Duration(snapshot.TimerRemainingSeconds.Value) : "—:—";
        _caption.text = snapshot.TimerState switch
        { "Idle" => "SET TIMER", "Running" => "TIMER RUNNING", "Paused" => "TIMER PAUSED", "Elapsed" => "FINISHED · NEXT TIMER", _ => "UNAVAILABLE" };
        _caption.fontSize = 25;
        if (snapshot.TimerState == "Elapsed") _main.color = _amber;
        _hint.fontSize = 23;
        _hint.text = idle
            ? "↑↓ 1 MIN · ←→ PRESETS"
            : "↓ RESET";
        _controls.text = snapshot.TimerState switch
        { "Running" => "ENTER  PAUSE", "Paused" => "ENTER  RESUME", "Idle" or "Elapsed" => "ENTER  START", _ => "" };
    }

    private void Stopwatch(ClientWatchOsSnapshot os)
    {
        _title.text = "STOPWATCH";
        var tenths = Math.Max(0, os.StopwatchTenths);
        _main.text = StopwatchTime(tenths);
        Place(_main, 18, 54, 284, 100);
        _main.fontSizeMax = 82;
        Place(_caption, 18, 156, 284, 38);
        _caption.fontSize = 27;
        _caption.text = (os.StopwatchRunning ? "RUNNING" : tenths == 0 ? "READY" : "PAUSED")
            + (os.StopwatchLaps.Count > 0 ? " · " + (os.WatchFirstLapNumber + os.StopwatchLaps.Count - 1) + " LAPS" : "");
        _caption.color = os.StopwatchRunning ? _accent : _muted;
        _navigation.gameObject.SetActive(true);
        Place(_navigation, 18, 196, 284, 66);
        _navigation.fontSize = 24;
        if (os.WatchLiveLap)
        {
            var last = os.StopwatchLaps.Count == 0 ? 0 : os.StopwatchLaps[^1];
            var live = Math.Max(0, tenths - last);
            _navigation.text = "CURRENT " + StopwatchTime(live);
            if (os.StopwatchLaps.Count > 0)
            {
                var previous = os.StopwatchLaps.Count < 2 ? os.WatchLapBaselineTenths : os.StopwatchLaps[^2];
                var difference = live - (last - previous);
                _navigation.text += "\nVS LAST " + (difference >= 0 ? "+" : "−") + StopwatchTime(Math.Abs(difference));
            }
            else _navigation.text += "\nNO COMPLETED LAP";
        }
        else if (os.StopwatchLaps.Count > 0)
        {
            var index = Math.Clamp(os.SelectedLap, 0, os.StopwatchLaps.Count - 1);
            var prior = index == 0 ? os.WatchLapBaselineTenths : os.StopwatchLaps[index - 1];
            var split = Math.Max(0, os.StopwatchLaps[index] - prior);
            _navigation.text = "LAP " + (os.WatchFirstLapNumber + index) + " · " + StopwatchTime(split)
                + "\nTOTAL " + StopwatchTime(os.StopwatchLaps[index]);
        }
        else _navigation.text = "NO LAPS";
        _hint.text = os.StopwatchRunning ? "←LOG ↑LIVE →LAP ↓RESET"
            : os.StopwatchLaps.Count > 0 ? "←→LOG ↑LIVE ↓RESET" : "↓ RESET";
        _hint.fontSize = 19;
        _controls.text = os.StopwatchRunning ? "ENTER  PAUSE" : tenths == 0 ? "ENTER  START" : "ENTER  RESUME";
    }

    private void Reminders(ClientWristWatchSnapshot snapshot)
    {
        ReadingPage();
        if (!PrivatePreview(snapshot)) return;
        var reminders = snapshot.Os.Reminders;
        if (reminders.Count == 0)
        {
            _caption.text = "NO REMINDERS";
            _detail.text = "Create one in\nClock on your\ntablet.";
            _hint.text = WatchReminderResult(snapshot.Os.WatchReminderFeedback);
            return;
        }
        var index = Math.Clamp(snapshot.Os.SelectedReminder, 0, reminders.Count - 1);
        var reminder = reminders[index];
        var due = reminder.State == "due";
        _caption.text = "REMINDER " + (index + 1) + " / " + reminders.Count;
        _detail.fontSize = 32;
        _detail.maxVisibleLines = int.MaxValue;
        Place(_detail, 18, 98, 284, 100);
        _detail.text = Limit(reminder.Label, 70);
        _navigation.gameObject.SetActive(true);
        Place(_navigation, 18, 202, 284, 62);
        _navigation.fontSize = 23;
        _navigation.text = due ? "DUE NOW" : "IN " + Duration(Math.Max(0, reminder.RemainingSeconds));
        var feedback = WatchReminderResult(snapshot.Os.WatchReminderFeedback);
        if (feedback.Length > 0) _navigation.text += "\n" + feedback;
        _navigation.color = due ? _amber : _ink;
        _hint.fontSize = 21;
        _hint.text = "←→ LIST · ↑ NEXT · ↓ REMOVE";
        _controls.fontSize = 25;
        _controls.text = due ? "ENTER  SNOOZE 5M" : "";
    }

    private void Mission(ClientWristWatchSnapshot snapshot)
    {
        ReadingPage();
        if (!PrivatePreview(snapshot)) return;
        _caption.text = "OBJECTIVE";
        if (string.IsNullOrWhiteSpace(snapshot.Mission))
        {
            _detail.text = "No objective.\nSet on tablet.";
            _hint.text = "TABLET PREVIEW";
        }
        else
        {
            WatchReading(snapshot.Mission, snapshot.Os.WatchReadingPage);
            _controls.text = "ENTER  NEXT PAGE";
        }
    }

    private void ReadingPage()
    {
        _main.gameObject.SetActive(false);
        Place(_caption, 18, 60, 284, 34);
        _caption.fontSize = 26;
        _detail.gameObject.SetActive(true);
        Place(_detail, 18, 98, 284, 164);
        _detail.fontSize = 32;
        // Let the text rectangle produce an ellipsis rather than silently hiding later lines.
        _detail.maxVisibleLines = int.MaxValue;
    }

    private void WatchReading(string content, int selectedPage)
    {
        if (!string.Equals(content, _watchReadingContent, StringComparison.Ordinal))
        {
            _watchReadingContent = content;
            _watchReadingPages = ClientWatchOsSnapshot.WatchReadingPages(content);
        }
        var pages = _watchReadingPages;
        var index = Math.Clamp(selectedPage, 0, pages.Count - 1);
        _detail.text = pages[index];
        _detail.enableAutoSizing = true;
        _detail.fontSizeMin = 22;
        _detail.fontSizeMax = 28;
        _hint.fontSize = 23;
        _hint.text = "↑↓ PAGE " + (index + 1) + "/" + pages.Count
            + (_caption.text.StartsWith("NOTICE", StringComparison.Ordinal) ? " · ←→ LIST" : "");
    }

    private void WatchConfirmation(ClientWristWatchSnapshot snapshot)
    {
        ResetLayout();
        ReadingPage();
        _caption.text = "CONFIRM ACTION";
        if (snapshot.Os.WatchPendingAction == ClientWatchPendingAction.DismissReminder && !PrivatePreview(snapshot)) return;
        _detail.text = snapshot.Os.WatchPendingAction switch
        {
            ClientWatchPendingAction.ResetCountdown => "Reset shared timer?\n" + snapshot.TimerState + "\n"
                + (snapshot.TimerRemainingSeconds is >= 0 ? Duration(snapshot.TimerRemainingSeconds.Value) + " remaining" : "Time unavailable"),
            ClientWatchPendingAction.ResetStopwatch => "Clear stopwatch?\n" + StopwatchTime(snapshot.Os.StopwatchTenths)
                + "\n" + snapshot.Os.StopwatchLaps.Count + " retained laps",
            _ => "Dismiss reminder?\n" + Limit(snapshot.Os.WatchConfirmationLabel, 40)
        };
        _detail.fontSize = 26;
        _hint.fontSize = 22;
        _hint.text = "BACK CANCEL · " + snapshot.Os.WatchConfirmationSeconds + "s LEFT";
        _controls.text = "ENTER  CONFIRM";
        _controls.color = _amber;
    }

    private static int WatchDueCount(ClientWatchOsSnapshot os)
    {
        var count = 0;
        foreach (var reminder in os.Reminders) if (reminder.State == "due") count++;
        return count;
    }

    private static int WatchNextReminderSeconds(ClientWatchOsSnapshot os)
    {
        var seconds = int.MaxValue;
        foreach (var reminder in os.Reminders) seconds = Math.Min(seconds, Math.Max(0, reminder.RemainingSeconds));
        return seconds == int.MaxValue ? 0 : seconds;
    }

    private static string WatchAppStatus(ClientWristWatchSnapshot snapshot)
    {
        var os = snapshot.Os;
        var privateApp = os.SelectedApp is ClientWatchPage.Notices or ClientWatchPage.Mission or ClientWatchPage.Reminders;
        if (privateApp && !snapshot.ShowPrivatePreviews)
            return os.HidePrivatePreviews ? "PREVIEWS HIDDEN" : "TABLET UNAVAILABLE";
        return os.SelectedApp switch
        {
            ClientWatchPage.Health => snapshot.Vitals.Condition.NeedsAttention || !snapshot.Vitals.Condition.Available
                ? snapshot.Vitals.Condition.WatchSummary
                : "HEALTH " + Number(snapshot.Vitals.Health) + " / " + Number(snapshot.Vitals.MaximumHealth),
            ClientWatchPage.Navigation => snapshot.ShowPrivatePreviews && Degrees(snapshot.WaypointBearingDegrees) != null
                ? "ROUTE SELECTED" : Degrees(snapshot.HeadingDegrees) != null ? "COMPASS READY" : "NO HEADING",
            ClientWatchPage.Notices => snapshot.Notifications.Count == 0 ? "NO NOTICES" : snapshot.Notifications.Count + " RECENT NOTICES",
            ClientWatchPage.Countdown => snapshot.TimerState switch
            {
                "Running" => "RUNNING " + (snapshot.TimerRemainingSeconds is >= 0 ? Duration(snapshot.TimerRemainingSeconds.Value) : "—"),
                "Paused" => "PAUSED " + (snapshot.TimerRemainingSeconds is >= 0 ? Duration(snapshot.TimerRemainingSeconds.Value) : "—"),
                "Elapsed" => "TIMER FINISHED",
                "Idle" => "READY · " + Duration(os.CountdownPresetSeconds),
                _ => "TIMER UNAVAILABLE"
            },
            ClientWatchPage.Stopwatch => (os.StopwatchRunning ? "RUNNING " : os.StopwatchTenths == 0 ? "READY " : "PAUSED ") + StopwatchTime(os.StopwatchTenths),
            ClientWatchPage.Mission => string.IsNullOrWhiteSpace(snapshot.Mission) ? "NO OBJECTIVE" : "OBJECTIVE AVAILABLE",
            ClientWatchPage.QuickSettings => os.Theme.ToString().ToUpperInvariant() + (os.NightMode ? " · NIGHT" : " · DAY"),
            ClientWatchPage.Reminders => WatchDueCount(os) > 0 ? WatchDueCount(os) + " DUE NOW"
                : os.Reminders.Count == 0 ? "NO REMINDERS" : "NEXT IN " + Duration(WatchNextReminderSeconds(os)),
            _ => "LOCAL DEVICE TOOLS"
        };
    }

    private static string WatchBearing(float degrees) => ((int)MathF.Round(degrees) % 360).ToString("000", CultureInfo.InvariantCulture) + "°";

    private static string WatchTurn(float heading, float target)
    {
        var delta = Mathf.DeltaAngle(heading, target);
        var turn = MathF.Abs(delta);
        if (turn <= 12) return "AHEAD · " + WatchBearing(target);
        if (turn >= 165) return "BEHIND · " + WatchBearing(target);
        return (delta < 0 ? "LEFT " : "RIGHT ") + MathF.Round(turn).ToString(CultureInfo.InvariantCulture) + "° TO ROUTE";
    }

    private static string WatchReminderResult(string result) => result switch
    {
        "Reminder dismissed." => "REMINDER DISMISSED",
        "Reminder snoozed for 5 minutes." => "SNOOZED FOR 5 MIN",
        "That reminder is no longer available." => "REMINDER NO LONGER HERE",
        "Only due reminders can be snoozed." => "NOT DUE YET",
        "Due reminder selected." => "DUE REMINDER SELECTED",
        "Next reminder selected." => "NEXT REMINDER SELECTED",
        _ => ""
    };

    private bool PrivatePreview(ClientWristWatchSnapshot snapshot)
    {
        if (snapshot.ShowPrivatePreviews) return true;
        _caption.text = snapshot.Os.HidePrivatePreviews ? "PREVIEWS HIDDEN" : "PRIVATE DATA";
        _detail.text = snapshot.Os.HidePrivatePreviews ? "Show previews\nin watch setup." : "Unavailable\nright now.";
        _hint.text = "Q  OPEN SETUP";
        return false;
    }

    private void QuickSettings(ClientWatchOsSnapshot os)
    {
        _title.text = "SETUP";
        _main.gameObject.SetActive(false);
        Place(_caption, 18, 65, 284, 44);
        _caption.text = os.Setting switch { 0 => "SETUP · THEME", 1 => "SETUP · NIGHT MODE", _ => "SETUP · PREVIEWS" };
        _caption.fontSize = 27;
        _detail.gameObject.SetActive(true);
        Place(_detail, 18, 116, 284, 89);
        _detail.fontSize = 48;
        _detail.alignment = TextAlignmentOptions.Center;
        _detail.text = os.Setting switch
        { 0 => os.Theme.ToString(), 1 => os.NightMode ? "On" : "Off", _ => os.HidePrivatePreviews ? "Hidden" : "Visible" };
        _navigation.gameObject.SetActive(true);
        Place(_navigation, 18, 207, 284, 58);
        _navigation.text = os.WatchPreferenceFeedback.Length == 0 ? "Device preference" : os.WatchPreferenceFeedback;
        _navigation.fontSize = 23;
        _navigation.color = os.WatchPreferenceFeedback.Contains("UNAVAILABLE", StringComparison.Ordinal) ? _amber : _muted;
        _hint.text = "↑↓  " + (os.Setting + 1) + " / 3  SETTING";
        _controls.text = "ENTER  CHANGE";
    }

    private void About(ClientWatchOsSnapshot os)
    {
        if (os.WatchHelpPage > 0)
        {
            ReadingPage();
            _caption.text = "WATCH GUIDE " + os.WatchHelpPage + "/" + ClientWatchOsSnapshot.WatchHelp.Count;
            _detail.fontSize = 26;
            _detail.maxVisibleLines = 5;
            _detail.text = ClientWatchOsSnapshot.WatchHelp[Math.Clamp(os.WatchHelpPage - 1, 0, ClientWatchOsSnapshot.WatchHelp.Count - 1)];
            _hint.text = "←→ GUIDE PAGE";
            _controls.text = "ENTER  NEXT";
            return;
        }
        _main.gameObject.SetActive(false);
        _detail.gameObject.SetActive(true);
        _detail.fontSize = 39;
        _detail.text = "Modstoke\nField watch\nby OpenACAI";
        Place(_detail, 18, 67, 284, 153);
        Place(_caption, 18, 229, 284, 44);
        _caption.fontSize = 27;
        _caption.text = "ABOUT · FIELD TOOLS";
        _controls.text = "ENTER  WATCH GUIDE";
    }

    private void ApplyPalette(ClientWatchOsSnapshot os)
    {
        if (_paletteTheme == os.Theme && _paletteNightMode == os.NightMode) return;
        _paletteTheme = os.Theme;
        _paletteNightMode = os.NightMode;
        var brightness = os.NightMode ? .58f : 1f;
        _ink = Dim(new Color(.94f, .98f, .94f), brightness);
        _muted = Dim(new Color(.76f, .85f, .80f), brightness);
        _accent = Dim(os.Theme switch
        {
            ClientWatchTheme.Ice => new Color(.48f, .81f, 1f),
            ClientWatchTheme.Amber => new Color(1f, .73f, .35f),
            _ => new Color(.55f, .95f, .69f)
        }, brightness);
        _amber = Dim(new Color(1f, .73f, .35f), brightness);
        _low = Dim(new Color(1f, .43f, .36f), brightness);
        _glass.color = os.NightMode ? Color.black : os.Theme switch
        {
            ClientWatchTheme.Ice => new Color(.018f, .034f, .055f),
            ClientWatchTheme.Amber => new Color(.050f, .032f, .014f),
            _ => new Color(.018f, .035f, .025f)
        };
        _divider.color = Dim(_accent, .40f);
        _appCard.color = Dim(_accent, .10f);
        _primaryControl.color = Dim(_accent, .18f);
        foreach (var stroke in _iconStrokes) stroke.color = _accent;
        foreach (var label in _headerLabels) label.color = _accent;
        _vitalTrack.color = Dim(_accent, .22f);
        _title.color = _accent;
        _clock.color = _muted;
        _detail.color = _vitalLabel.color = _navigation.color = _ink;
        _arrow.color = _controls.color = _accent;
        _hint.color = _muted;
    }

    private static Color Dim(Color color, float amount) => new(color.r * amount, color.g * amount, color.b * amount, 1);
    private static string PageName(ClientWatchPage page) => page switch
    {
        ClientWatchPage.Home => "Home", ClientWatchPage.Apps => "Apps", ClientWatchPage.Health => "Health",
        ClientWatchPage.Navigation => "Navigation", ClientWatchPage.Notices => "Notices", ClientWatchPage.Countdown => "Countdown",
        ClientWatchPage.Stopwatch => "Stopwatch", ClientWatchPage.Mission => "Mission", ClientWatchPage.QuickSettings => "Quick settings",
        ClientWatchPage.Reminders => "Reminders", _ => "About"
    };
    private static string StopwatchTime(long tenths) => tenths < 36000
        ? (tenths / 600).ToString("00", CultureInfo.InvariantCulture) + ":"
            + (tenths / 10 % 60).ToString("00", CultureInfo.InvariantCulture) + "." + tenths % 10
        : Duration(tenths / 10);
    private static string Duration(long seconds) => seconds >= 3600
        ? (seconds / 3600).ToString(CultureInfo.InvariantCulture) + ":" + (seconds / 60 % 60).ToString("00", CultureInfo.InvariantCulture) + ":" + (seconds % 60).ToString("00", CultureInfo.InvariantCulture)
        : (seconds / 60).ToString("00", CultureInfo.InvariantCulture) + ":" + (seconds % 60).ToString("00", CultureInfo.InvariantCulture);
    private static string Number(float? value) => value is >= 0 && float.IsFinite(value.Value)
        ? value.Value.ToString("0", CultureInfo.InvariantCulture) : "—";
    private static float? Vital(ClientWristWatchVitals vitals, int index) => index switch
    {
        0 => vitals.Stamina, 1 => vitals.Hydration, 2 => vitals.Fullness, 3 => vitals.Rest, _ => vitals.Air
    };
    private static float? Degrees(float? value) => value is { } angle && float.IsFinite(angle) ? Mathf.Repeat(angle, 360) : null;
    private static string Limit(string? value, int length) => string.IsNullOrEmpty(value) ? "" : value.Length <= length ? value : value[..(length - 1)] + "…";

    private static void Place(TextMeshProUGUI text, float x, float y, float width, float height)
    {
        text.rectTransform.anchoredPosition = new Vector2(x, -y);
        text.rectTransform.sizeDelta = new Vector2(width, height);
    }

    // Like the tablet icons, these use owned UI strokes instead of font glyphs or extra textures.
    private GameObject AppIcon(Transform parent, ClientWatchPage page)
    {
        var root = Node(parent, page + " icon", 110, 16, 72, 72);
        const float scale = 72f / 64;
        void Stroke(float ax, float ay, float bx, float by)
        {
            var dx = (bx - ax) * scale;
            var dy = (by - ay) * scale;
            var line = Panel(root, "Icon stroke", Color.white, ax * scale, ay * scale,
                MathF.Sqrt(dx * dx + dy * dy), 4 * scale);
            line.rectTransform.pivot = new Vector2(0, .5f);
            line.rectTransform.localEulerAngles = new Vector3(0, 0, -MathF.Atan2(dy, dx) * Mathf.Rad2Deg);
            _iconStrokes.Add(line);
        }
        void Ring(float cx, float cy, float radius)
        {
            for (var i = 0; i < 16; i++)
            {
                var first = i * MathF.PI / 8;
                var next = (i + 1) * MathF.PI / 8;
                Stroke(cx + MathF.Cos(first) * radius, cy + MathF.Sin(first) * radius,
                    cx + MathF.Cos(next) * radius, cy + MathF.Sin(next) * radius);
            }
        }
        switch (page)
        {
            case ClientWatchPage.Health:
                Stroke(25, 8, 39, 8); Stroke(39, 8, 39, 25); Stroke(39, 25, 56, 25);
                Stroke(56, 25, 56, 39); Stroke(56, 39, 39, 39); Stroke(39, 39, 39, 56);
                Stroke(39, 56, 25, 56); Stroke(25, 56, 25, 39); Stroke(25, 39, 8, 39);
                Stroke(8, 39, 8, 25); Stroke(8, 25, 25, 25); Stroke(25, 25, 25, 8);
                break;
            case ClientWatchPage.Navigation:
                Ring(32, 32, 26);
                Stroke(23, 42, 32, 16); Stroke(32, 16, 41, 42);
                Stroke(41, 42, 32, 36); Stroke(32, 36, 23, 42);
                break;
            case ClientWatchPage.Notices:
                Stroke(6, 10, 58, 10); Stroke(58, 10, 58, 45); Stroke(58, 45, 28, 45);
                Stroke(28, 45, 14, 57); Stroke(14, 57, 14, 45); Stroke(14, 45, 6, 45);
                Stroke(6, 45, 6, 10); Stroke(16, 23, 48, 23); Stroke(16, 33, 39, 33);
                break;
            case ClientWatchPage.Countdown:
                Stroke(12, 8, 52, 8); Stroke(12, 56, 52, 56);
                Stroke(17, 8, 17, 18); Stroke(17, 18, 47, 46); Stroke(47, 46, 47, 56);
                Stroke(47, 8, 47, 18); Stroke(47, 18, 17, 46); Stroke(17, 46, 17, 56);
                break;
            case ClientWatchPage.Stopwatch:
                Ring(32, 36, 21); Stroke(24, 4, 40, 4); Stroke(32, 4, 32, 15);
                Stroke(46, 17, 53, 10); Stroke(32, 23, 32, 37); Stroke(32, 37, 43, 31);
                break;
            case ClientWatchPage.Mission:
                Stroke(14, 58, 14, 8); Stroke(14, 8, 50, 8); Stroke(50, 8, 44, 26);
                Stroke(44, 26, 14, 26); Stroke(8, 58, 24, 58);
                break;
            case ClientWatchPage.QuickSettings:
                Stroke(8, 16, 56, 16); Stroke(22, 9, 22, 23);
                Stroke(8, 32, 56, 32); Stroke(42, 25, 42, 39);
                Stroke(8, 48, 56, 48); Stroke(30, 41, 30, 55);
                break;
            case ClientWatchPage.About:
                Ring(32, 32, 26); Stroke(29, 17, 35, 17);
                Stroke(32, 27, 32, 47); Stroke(24, 47, 40, 47);
                break;
            case ClientWatchPage.Reminders:
                Ring(32, 29, 23); Stroke(32, 14, 32, 29); Stroke(32, 29, 44, 35);
                Stroke(23, 57, 30, 62); Stroke(30, 62, 44, 49);
                break;
        }
        return root.gameObject;
    }

    private static RectTransform Node(Transform parent, string name, float x, float y, float width, float height)
    {
        var node = new GameObject(name) { layer = parent.gameObject.layer };
        node.transform.SetParent(parent, false);
        var rect = node.AddComponent<RectTransform>();
        rect.anchorMin = rect.anchorMax = new Vector2(0, 1);
        rect.pivot = new Vector2(0, 1);
        rect.anchoredPosition = new Vector2(x, -y);
        rect.sizeDelta = new Vector2(width, height);
        return rect;
    }

    private Image Panel(Transform parent, string name, Color color, float x, float y, float width, float height, bool rounded = false)
    {
        var rect = Node(parent, name, x, y, width, height);
        var image = rect.gameObject.AddComponent<Image>();
        image.color = color;
        image.raycastTarget = false;
        _surfaces.Apply(image, rounded);
        return image;
    }

    private TextMeshProUGUI Text(Transform parent, string name, float size, float x, float y, float width, float height,
        TabletTextRole role = TabletTextRole.Status)
    {
        var rect = Node(parent, name, x, y, width, height);
        var text = rect.gameObject.AddComponent<TextMeshProUGUI>();
        _typography.Apply(text, role);
        text.text = "";
        text.fontSize = size;
        text.richText = false;
        text.enableWordWrapping = false;
        text.overflowMode = TextOverflowModes.Ellipsis;
        text.alignment = TextAlignmentOptions.TopLeft;
        text.margin = Vector4.zero;
        text.raycastTarget = false;
        return text;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_root != null) { _root.SetActive(false); UnityEngine.Object.Destroy(_root); }
        _root = null;
        _canvas = null;
        _iconStrokes.Clear();
        _surfaces.Dispose();
        _typography.Dispose();
    }
}
