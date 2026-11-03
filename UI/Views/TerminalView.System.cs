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
    private const float ShadeHeight = 820f;
    private const float CompactShadeAmount = .68f;
    private RectTransform _shadeExtra = null!;
    private RectTransform _shadeExtraViewport = null!;
    private CanvasGroup _shadeExtraInput = null!;
    private RectTransform _shadeFooter = null!;
    private ButtonWidget _shadeExpand = null!;
    private readonly List<Graphic> _ownedGraphics = new();
    private readonly List<ButtonWidget> _settingsTabs = new();
    private readonly List<RectTransform> _settingsPages = new();
    private readonly Dictionary<ClientTabletTheme, ButtonWidget> _themeChoices = new();
    private readonly Dictionary<ClientTabletTone, ButtonWidget> _toneChoices = new();
    private readonly Dictionary<ClientTabletMode, ButtonWidget> _modeChoices = new();
    private readonly Dictionary<ClientWatchHudMode, ButtonWidget> _hudChoices = new();
    private readonly Dictionary<ClientWatchKey, ButtonWidget> _watchKeyChoices = new();
    private readonly Dictionary<ClientTabletTextSize, ButtonWidget> _textSizeChoices = new();
    private readonly Dictionary<string, ButtonWidget> _channelAlertChoices = new();
    private ButtonWidget _reducedMotionToggle = null!;
    private ClientTabletPreferencesSnapshot _preferences = new(ClientTabletTheme.Field, 1f, true, .35f, ClientTabletTone.Pulse, false);
    private ClientTabletTheme _paintedTheme = ClientTabletTheme.Field;
    private RectTransform _settingsSurface = null!;
    private TextMeshProUGUI _preferencesStatus = null!;
    private TextMeshProUGUI _brightnessValue = null!;
    private TextMeshProUGUI _volumeValue = null!;
    private TextMeshProUGUI _displayModeStatus = null!;
    private TextMeshProUGUI _soundModeStatus = null!;
    private TextMeshProUGUI _aboutVersion = null!;
    private TextMeshProUGUI _diagnosticsText = null!;
    private ScrollArea _diagnostics = null!;
    private ButtonWidget _notificationsToggle = null!;
    private ButtonWidget _developerToggle = null!;
    private Slider _brightnessSlider = null!;
    private Slider _volumeSlider = null!;
    private Image _brightnessDim = null!;
    private RectTransform _shadeRoot = null!;
    private RectTransform _shadeViewport = null!;
    private RectTransform _shadeSheet = null!;
    private Image _shadeScrim = null!;
    private TextMeshProUGUI _shadeClock = null!;
    private TextMeshProUGUI _shadeDate = null!;
    private TextMeshProUGUI _shadeNotifications = null!;
    private TextMeshProUGUI _shadeSystem = null!;
    private TextMeshProUGUI _shadeBrightnessValue = null!;
    private TextMeshProUGUI _shadeVolumeValue = null!;
    private TextMeshProUGUI _watchStatus = null!;
    private ButtonWidget _watchPause = null!;
    private ButtonWidget _watchCancel = null!;
    private ButtonWidget _markAllRead = null!;
    private readonly List<ButtonWidget> _watchPresets = new();
    private ClientTabletWatchSnapshot _watch;
    private ButtonWidget _shadeDnd = null!;
    private ButtonWidget _shadeTone = null!;
    private ButtonWidget _shadeTheme = null!;
    private ButtonWidget _shadeMode = null!;
    private ButtonWidget _shadeMessage = null!;
    private ScrollArea _shadeNotificationList = null!;
    private readonly List<ClientTabletNotification> _shadeFieldNotifications = new();
    private readonly List<ButtonWidget> _shadeFieldCards = new();
    private readonly List<ButtonWidget> _shadeFieldDismiss = new();
    private readonly List<TextMeshProUGUI> _shadeFieldTitles = new();
    private readonly List<TextMeshProUGUI> _shadeFieldDetails = new();
    private int _shadeNotificationTotal;
    private Slider _shadeBrightness = null!;
    private Slider _shadeVolume = null!;
    private int _settingsSection;
    private int _unreadMessages;
    private string _latestMessage = "";
    private bool _preferencesUpdating;
    private bool _shadeCapture;
    private bool _shadeDragging;
    private int _shadeGestureEndFrame = -1;
    private bool _shadeMoved;
    private float _shadeAmount;
    private float _shadeDragStart;
    private float _shadeDragAmount;
    private float _shadeDragRestoreAmount;
    private bool _shadeSettling;
    private float _shadeSettleFrom;
    private float _shadeTarget;
    private float _shadeSettleAt;
    private long _clockMinute = -1;
    private string _pendingSliderKey = "";
    private float _pendingSliderValue;

    internal bool ContentObscured => _shadeCapture || DesktopCaptured;
    private bool ShadeGestureOwnsInput => _shadeDragging || _shadeGestureEndFrame == Time.frameCount;

    private void SelectApp(ClientTerminalApp app)
    {
        if (_disposed || _shadeCapture || DesktopCaptured) return;
        CloseLauncherPanels();
        _select(app);
    }

    private void BuildSettings()
    {
        _settingsSurface = Node(_applicationLayer, "Settings");
        Place(_settingsSurface, 56f, 176f, WorkspaceWidth, WorkspaceHeight);
        Button(_settingsSurface, "FindSettings", "Find a setting", 0f, 8f, 260f, 56f, OpenSettingsSearch);
        var sections = new[] { "Display", "Sound & alerts", "Satellite", "About", "Operating mode", "Watch & HUD", "System" };
        for (var index = 0; index < sections.Length; index++)
        {
            var selected = index;
            var tab = Button(_settingsSurface, "SettingsTab" + index, sections[index], 0f,
                78f + index * 70f, 260f, 60f, () => SelectSettingsSection(selected));
            tab.Label.fontSize = 21f;
            tab.Label.enableWordWrapping = false;
            tab.Label.overflowMode = TextOverflowModes.Ellipsis;
            _settingsTabs.Add(tab);
            var page = RoundedPanel(_settingsSurface, "SettingsPage" + index, Surface, 284f, 0f, 1044f, 548f).rectTransform;
            _settingsPages.Add(page);
        }
        _preferencesStatus = Text(_settingsSurface, "PreferencesStatus", "", 20f, Muted,
            300f, 562f, 1012f, 34f);
        _preferencesStatus.overflowMode = TextOverflowModes.Ellipsis;

        var displayScroll = Scroll(_settingsPages[0], "DisplaySettings", 0f, 0f, 1044f, 548f);
        var display = displayScroll.Content;
        Text(display, "DisplayTitle", "Display", 34f, Ink, 32f, 24f, 980f, 46f, true);
        Text(display, "ThemeLabel", "Appearance", 24f, Ink, 32f, 100f, 980f, 36f, true);
        var themeIndex = 0;
        foreach (var theme in new[] { ClientTabletTheme.Field, ClientTabletTheme.Light, ClientTabletTheme.Amber })
        {
            var choice = theme;
            _themeChoices.Add(theme, Button(display, "Theme" + theme, theme.ToString(),
                32f + themeIndex++ * 328f, 152f, 310f, 70f, () => EditPreference("theme", choice.ToString())));
        }
        Panel(display, "DisplayRule", Rule, 32f, 258f, 980f, 2f);
        Text(display, "BrightnessLabel", "Brightness", 26f, Ink, 32f, 290f, 700f, 38f, true);
        _brightnessValue = Text(display, "BrightnessValue", "100%", 26f, Ink, 850f, 290f, 162f, 38f, true);
        _brightnessValue.alignment = TextAlignmentOptions.TopRight;
        _brightnessSlider = PreferenceSlider(display, "Brightness", "brightness", 32f, 344f, 980f, .35f, 1f);
        _displayModeStatus = Text(display, "DisplayHelp", "Dim the screen for night-time use.",
            23f, Muted, 32f, 430f, 980f, 72f);
        Panel(display, "AccessibilityRule", Rule, 32f, 522f, 980f, 2f);
        Text(display, "TextSizeLabel", "Text size", 26f, Ink, 32f, 550f, 980f, 40f, true);
        var sizeIndex = 0;
        foreach (var size in new[] { ClientTabletTextSize.Normal, ClientTabletTextSize.Large })
        {
            var choice = size;
            _textSizeChoices.Add(size, Button(display, "TextSize" + size, size.ToString(),
                32f + sizeIndex++ * 328f, 610f, 310f, 70f, () => EditPreference("text-size", choice.ToString())));
        }
        Text(display, "ReducedMotionLabel", "Reduced motion", 26f, Ink, 32f, 716f, 700f, 40f, true);
        _reducedMotionToggle = Button(display, "ReducedMotion", "Off", 816f, 706f, 196f, 64f,
            () => EditPreference("reduced-motion", (!_preferences.ReducedMotion).ToString()));
        BuildDisplayCalibration(display);
        BuildPreferenceReset(display, 0, 1150f);
        displayScroll.Height(1300f);

        var soundScroll = Scroll(_settingsPages[1], "SoundSettings", 0f, 0f, 1044f, 548f);
        var sound = soundScroll.Content;
        Text(sound, "SoundTitle", "Sound & alerts", 34f, Ink, 32f, 24f, 980f, 46f, true);
        Text(sound, "NotificationsLabel", "Message alerts", 26f, Ink, 32f, 100f, 680f, 38f, true);
        Text(sound, "NotificationsHelp", "Quiet alerts keep messages in your inbox.", 23f, Muted,
            32f, 142f, 740f, 36f);
        _notificationsToggle = Button(sound, "NotificationsToggle", "On", 816f, 106f, 196f, 60f,
            () => EditPreference("notifications", (!_preferences.Notifications).ToString()));
        Panel(sound, "SoundRule", Rule, 32f, 192f, 980f, 2f);
        Text(sound, "VolumeLabel", "Alert volume", 26f, Ink, 32f, 218f, 700f, 38f, true);
        _volumeValue = Text(sound, "VolumeValue", "35%", 26f, Ink, 850f, 218f, 162f, 38f, true);
        _volumeValue.alignment = TextAlignmentOptions.TopRight;
        _volumeSlider = PreferenceSlider(sound, "Volume", "volume", 32f, 270f, 980f, 0f, 1f);
        Text(sound, "ToneLabel", "Notification tone", 24f, Ink, 32f, 350f, 700f, 36f, true);
        var toneIndex = 0;
        foreach (var tone in new[] { ClientTabletTone.Pulse, ClientTabletTone.Chime, ClientTabletTone.Radio })
        {
            var choice = tone;
            _toneChoices.Add(tone, Button(sound, "Tone" + tone, tone.ToString(), 32f + toneIndex++ * 248f,
                402f, 230f, 60f, () => EditPreference("tone", choice.ToString())));
        }
        Button(sound, "PreviewTone", "Preview", 776f, 402f, 236f, 60f,
            () => _command("settings", "preview-tone"));
        _soundModeStatus = Text(sound, "SoundModeStatus", "", 21f, Muted, 32f, 480f, 980f, 48f);
        Panel(sound, "ChannelRule", Rule, 32f, 550f, 980f, 2f);
        Text(sound, "ChannelHeading", "Channel sounds", 26f, Ink, 32f, 580f, 980f, 40f, true);
        Text(sound, "ChannelHelp", "Quiet channels still keep their messages in your inbox.", 22f, Muted, 32f, 630f, 980f, 58f);
        var channelIndex = 0;
        foreach (var channel in new[] { "expedition", "tribe", "direct" })
        {
            var choice = channel;
            var y = 704f + channelIndex++ * 104f;
            var label = channel == "expedition" ? "Expedition" : channel == "tribe" ? "Tribe" : "Direct messages";
            RoundedPanel(sound, "ChannelCard" + channel, Raised, 32f, y, 980f, 88f);
            Text(sound, "ChannelLabel" + channel, label, 26f, Ink, 52f, y + 24f, 700f, 40f, true);
            _channelAlertChoices.Add(channel, Button(sound, "ChannelAlerts" + channel, "On", 816f, y + 12f, 176f, 64f,
                () => EditPreference(choice + "-alerts", (!ChannelAlertsEnabled(choice)).ToString())));
        }
        BuildPreferenceReset(sound, 1, 1040f);
        soundScroll.Height(1190f);

        var modeScroll = Scroll(_settingsPages[4], "ModeSettings", 0f, 0f, 1044f, 548f);
        var modes = modeScroll.Content;
        Text(modes, "ModeTitle", "Operating mode", 34f, Ink, 32f, 24f, 980f, 46f, true);
        var modeIndex = 0;
        foreach (var mode in new[] { ClientTabletMode.Patrol, ClientTabletMode.Camp, ClientTabletMode.Silent })
        {
            var choice = mode;
            var y = 104f + modeIndex++ * 118f;
            _modeChoices.Add(mode, Button(modes, "Mode" + mode, mode.ToString(), 32f, y, 244f, 76f,
                () => EditPreference("mode", choice.ToString())));
            var description = mode switch
            {
                ClientTabletMode.Camp => "At camp: use your chosen brightness and alert settings.",
                ClientTabletMode.Silent => "Dim the screen to at most 55% and mute alerts. Messages still arrive; Preview uses your chosen volume.",
                _ => "On patrol: use your chosen brightness and alert settings."
            };
            Text(modes, "ModeHelp" + mode, description, 23f, Muted, 304f, y, 708f, 92f);
        }
        Text(modes, "ModeHelp", "Modes keep your manual settings. Connection and game time are unchanged.",
            22f, Muted, 32f, 470f, 980f, 58f);
        BuildModeComparison(modes);
        modeScroll.Height(854f);

        var watchScroll = Scroll(_settingsPages[5], "WatchSettings", 0f, 0f, 1044f, 548f);
        var watchSettings = watchScroll.Content;
        Text(watchSettings, "WatchSettingsTitle", "Watch & HUD", 34f, Ink, 32f, 24f, 980f, 46f, true);
        Text(watchSettings, "HudHeading", "Game HUD", 26f, Ink, 32f, 104f, 980f, 40f, true);
        var hudIndex = 0;
        foreach (var hud in new[] { ClientWatchHudMode.Standard, ClientWatchHudMode.Minimal, ClientWatchHudMode.Immersive })
        {
            var choice = hud;
            _hudChoices.Add(hud, Button(watchSettings, "Hud" + hud, hud.ToString(),
                32f + hudIndex++ * 328f, 164f, 310f, 70f, () => EditPreference("hud", choice.ToString())));
        }
        Text(watchSettings, "HudHelp", "Standard keeps the game HUD. Minimal keeps low-resource warnings. Immersive uses the watch for supported readings while it is available.",
            23f, Muted, 32f, 260f, 980f, 108f);
        Panel(watchSettings, "WatchKeyRule", Rule, 32f, 398f, 980f, 2f);
        Text(watchSettings, "WatchKeyHeading", "Raise watch", 26f, Ink, 32f, 430f, 980f, 40f, true);
        var keyIndex = 0;
        foreach (var key in new[] { ClientWatchKey.F3, ClientWatchKey.F9, ClientWatchKey.F11, ClientWatchKey.None })
        {
            var choice = key;
            _watchKeyChoices.Add(key, Button(watchSettings, "WatchKey" + key, key.ToString(),
                32f + keyIndex++ * 248f, 490f, 230f, 70f, () => EditPreference("watch-key", choice.ToString())));
        }
        Text(watchSettings, "WatchKeyHelp", "Choose a free key for the wrist watch. None disables its keyboard shortcut.",
            23f, Muted, 32f, 586f, 980f, 80f);
        BuildWatchPreferences(watchSettings);
        BuildPreferenceReset(watchSettings, 5, 1130f);
        watchScroll.Height(1280f);

        BuildSatelliteSettings(_settingsPages[2]);
        BuildRuntimeSettings(_settingsPages[6]);
        var about = _settingsPages[3];
        Text(about, "AboutTitle", "Modstoke", 34f, Ink, 32f, 24f, 980f, 46f, true);
        Text(about, "Attribution", "by OpenACAI", 23f, Mint, 32f, 78f, 980f, 34f, true);
        _aboutVersion = Text(about, "Version", "Field tablet", 21f, Muted, 32f, 126f, 980f, 34f);
        _aboutVersion.overflowMode = TextOverflowModes.Ellipsis;
        Text(about, "AboutDescription", "An independent field tablet for Sons Of The Forest.\nModstoke is not affiliated with or endorsed by Endnight Games.",
            22f, Muted, 32f, 174f, 980f, 72f);
        Panel(about, "AboutRule", Rule, 32f, 270f, 980f, 2f);
        Text(about, "DeveloperLabel", "Developer information", 24f, Ink, 32f, 294f, 700f, 34f, true);
        _developerToggle = Button(about, "DeveloperToggle", "Hidden", 816f, 290f, 196f, 56f,
            () => EditPreference("developer", (!_preferences.DeveloperOptions).ToString()));
        _diagnostics = Scroll(about, "Diagnostics", 24f, 368f, 996f, 156f);
        _diagnostics.Root.color = Screen;
        _diagnosticsText = Text(_diagnostics.Content, "DiagnosticsText", "", 22f, Muted, 16f, 14f, 944f, 100f, role: TabletTextRole.Status);
        BuildSettingsDiscovery(displayScroll, soundScroll, modeScroll, watchScroll);
        SelectSettingsSection(0);
        _settingsSurface.gameObject.SetActive(false);
    }

    private void SelectSettingsSection(int section)
    {
        DeactivateSettingsInputs();
        if (_settingsSearchRoot != null) _settingsSearchRoot.gameObject.SetActive(false);
        CommitSlider();
        ClearPreferenceReset();
        _settingsSection = section;
        for (var index = 0; index < _settingsPages.Count; index++)
        {
            _settingsPages[index].gameObject.SetActive(index == section);
            _settingsTabs[index].Image.color = index == section ? Raised : Screen;
            _settingsTabs[index].Label.color = index == section ? Ink : Muted;
        }
    }

    private bool ChannelAlertsEnabled(string channel) => channel switch
    {
        "expedition" => _preferences.ExpeditionAlerts,
        "tribe" => _preferences.TribeAlerts,
        "direct" => _preferences.DirectAlerts,
        _ => true
    };

    private Slider PreferenceSlider(Transform parent, string name, string preference, float x, float y, float width,
        float minimum, float maximum)
    {
        var totalWidth = width;
        width -= 144f;
        var hit = Panel(parent, name, Color.clear, x, y, width, 54f);
        hit.raycastTarget = true;
        var track = Panel(hit.transform, "Track", Rule, 18f, 22f, width - 36f, 10f);
        _surfaces.Apply(track, track: true);
        var fillArea = Node(hit.transform, "FillArea");
        Place(fillArea, 18f, 22f, width - 36f, 10f);
        var fill = Panel(fillArea, "Fill", Mint, 0f, 0f, 0f, 0f);
        _surfaces.Apply(fill, track: true);
        // Slider drives the anchors. Fixed width here would be added to the driven width.
        Stretch(fill.rectTransform, 0f, 0f, 0f, 0f);
        var handleArea = Node(hit.transform, "HandleArea");
        Place(handleArea, 18f, 27f, width - 36f, 0f);
        var handle = RoundedPanel(handleArea, "Handle", Ink, 0f, 0f, 36f, 36f);
        handle.rectTransform.pivot = new Vector2(.5f, .5f);
        handle.raycastTarget = true;
        var slider = hit.gameObject.AddComponent<Slider>();
        slider.minValue = minimum;
        slider.maxValue = maximum;
        slider.wholeNumbers = false;
        slider.fillRect = fill.rectTransform;
        slider.handleRect = handle.rectTransform;
        slider.targetGraphic = handle;
        slider.direction = Slider.Direction.LeftToRight;
        slider.onValueChanged.AddListener((UnityAction<float>)new Action<float>(value =>
        {
            if (_preferencesUpdating || !CanActivateControl(slider)
                || _shadeCapture && !hit.transform.IsChildOf(_shadeSheet)) return;
            if (_pendingSliderKey.Length > 0 && _pendingSliderKey != preference) CommitSlider();
            _pendingSliderKey = preference;
            _pendingSliderValue = value;
            RefreshDisplayLevels();
        }));
        Button(parent, name + "Decrease", "−", x + totalWidth - 132f, y, 60f, 54f,
            () => StepPreferenceLevel(preference, -.05f, minimum, maximum));
        Button(parent, name + "Increase", "+", x + totalWidth - 64f, y, 60f, 54f,
            () => StepPreferenceLevel(preference, .05f, minimum, maximum));
        TrackComfortFocus(slider, hit);
        return slider;
    }

    internal void SetPreferences(ClientTabletPreferencesSnapshot preferences, string status, string diagnostics)
    {
        if (_disposed) return;
        _preferences = preferences;
        ApplyTheme(preferences.Theme);
        RefreshDisplayLevels();
        _notificationsToggle.Label.text = preferences.Notifications ? "On" : "Quiet";
        _notificationsToggle.Image.color = preferences.Notifications ? Mint : Raised;
        _notificationsToggle.Label.color = preferences.Notifications ? Screen : Ink;
        _developerToggle.Label.text = preferences.DeveloperOptions ? "Visible" : "Hidden";
        _developerToggle.Image.color = preferences.DeveloperOptions ? Mint : Raised;
        _developerToggle.Label.color = preferences.DeveloperOptions ? Screen : Ink;
        foreach (var pair in _themeChoices)
        {
            pair.Value.Label.text = pair.Key.ToString();
            pair.Value.Image.color = pair.Key == preferences.Theme ? Mint : Raised;
            pair.Value.Label.color = pair.Key == preferences.Theme ? Screen : Ink;
            MarkPreferenceSelection(pair.Value, pair.Key == preferences.Theme);
        }
        foreach (var pair in _toneChoices)
        {
            pair.Value.Label.text = pair.Key.ToString();
            pair.Value.Image.color = pair.Key == preferences.Tone ? Mint : Raised;
            pair.Value.Label.color = pair.Key == preferences.Tone ? Screen : Ink;
            MarkPreferenceSelection(pair.Value, pair.Key == preferences.Tone);
        }
        foreach (var pair in _modeChoices)
        {
            pair.Value.Image.color = pair.Key == preferences.Mode ? Mint : Raised;
            pair.Value.Label.color = pair.Key == preferences.Mode ? Screen : Ink;
            MarkPreferenceSelection(pair.Value, pair.Key == preferences.Mode);
        }
        foreach (var pair in _hudChoices) SetPreferenceChoice(pair.Value, pair.Key == preferences.HudMode);
        foreach (var pair in _watchKeyChoices) SetPreferenceChoice(pair.Value, pair.Key == preferences.WatchKey);
        foreach (var pair in _watchThemeChoices) SetPreferenceChoice(pair.Value, pair.Key == preferences.WatchTheme);
        _watchNightChoice.Label.text = preferences.WatchNight ? "On" : "Off";
        _watchPrivacyChoice.Label.text = preferences.WatchPrivate ? "Hidden" : "Shown";
        SetPreferenceChoice(_watchNightChoice, preferences.WatchNight);
        SetPreferenceChoice(_watchPrivacyChoice, preferences.WatchPrivate);
        foreach (var pair in _textSizeChoices) SetPreferenceChoice(pair.Value, pair.Key == preferences.TextSize);
        foreach (var pair in _channelAlertChoices)
        {
            var enabled = ChannelAlertsEnabled(pair.Key);
            pair.Value.Label.text = enabled ? "On" : "Quiet";
            SetPreferenceChoice(pair.Value, enabled);
        }
        _reducedMotionToggle.Label.text = preferences.ReducedMotion ? "On" : "Off";
        SetPreferenceChoice(_reducedMotionToggle, preferences.ReducedMotion);
        _preferencesStatus.text = status ?? "";
        var information = diagnostics ?? "";
        var firstBreak = information.IndexOf('\n');
        _aboutVersion.text = firstBreak < 0 ? information : information[..firstBreak];
        if (string.IsNullOrWhiteSpace(_aboutVersion.text)) _aboutVersion.text = "Modstoke field tablet";
        _diagnosticsText.text = preferences.DeveloperOptions ? information : "Enable developer information to show runtime and connection details.";
        var diagnosticsY = 14f;
        PlaceText(_diagnosticsText, 16f, ref diagnosticsY, 944f, 14f);
        _diagnostics.Height(diagnosticsY);
        RefreshSettingsContext();
        RefreshNotificationShade();
        RefreshButtonVisuals();
    }

    private void SetPreferenceChoice(ButtonWidget choice, bool selected)
    {
        choice.Image.color = selected ? Mint : Raised;
        choice.Label.color = selected ? Screen : Ink;
        MarkPreferenceSelection(choice, selected);
    }

    private void EditPreference(string key, string value)
    {
        if (_disposed || _preferencesUpdating) return;
        var result = _edit("settings", Guid.Empty, key, value);
        if (_disposed) return;
        _preferencesStatus.text = string.IsNullOrWhiteSpace(result.Detail) ? result.Status : result.Detail;
    }

    private void RefreshDisplayLevels()
    {
        var brightness = _pendingSliderKey == "brightness" ? _pendingSliderValue : _preferences.Brightness;
        var volume = _pendingSliderKey == "volume" ? _pendingSliderValue : _preferences.Volume;
        var levels = _preferences with { Brightness = brightness, Volume = volume };
        _preferencesUpdating = true;
        try
        {
            _brightnessSlider.value = brightness;
            _shadeBrightness.value = brightness;
            _volumeSlider.value = volume;
            _shadeVolume.value = volume;
        }
        finally { _preferencesUpdating = false; }
        _brightnessValue.text = Percent(brightness);
        _shadeBrightnessValue.text = Percent(levels.EffectiveBrightness);
        _volumeValue.text = Percent(volume);
        _shadeVolumeValue.text = Percent(levels.EffectiveVolume);
        _displayModeStatus.text = levels.Mode == ClientTabletMode.Silent
            ? "Silent mode: " + Percent(levels.EffectiveBrightness) + " active brightness. Your manual brightness is kept."
            : "Dim the screen for night-time use.";
        _soundModeStatus.text = levels.Mode == ClientTabletMode.Silent
            ? "Silent mode mutes alerts. Preview uses your chosen volume."
            : "Preview uses your chosen volume, even with message alerts quiet.";
        _brightnessDim.color = new Color(0f, 0f, 0f, (1f - levels.EffectiveBrightness) * .70f);
    }

    private void CommitSlider()
    {
        if (_pendingSliderKey.Length == 0) return;
        var key = _pendingSliderKey;
        var value = _pendingSliderValue;
        _pendingSliderKey = "";
        EditPreference(key, Number(value));
        if (!_disposed) RefreshDisplayLevels();
    }

    private void BuildNotificationShade()
    {
        _shadeRoot = Node(_device.transform, "NotificationShade");
        Place(_shadeRoot, 24f, 24f, 1392f, 852f);
        _shadeRoot.gameObject.AddComponent<RectMask2D>();
        var dismiss = Button(_shadeRoot, "ShadeDismiss", "", 0f, 0f, 1392f, 852f, () =>
        {
            if (!_shadeDragging) SettleShade(0f);
        });
        _shadeScrim = dismiss.Image;
        _shadeViewport = Node(_shadeRoot, "ShadeViewport");
        Place(_shadeViewport, 0f, 0f, 1392f, ShadeHeight);
        _shadeViewport.gameObject.AddComponent<RectMask2D>();
        _shadeSheet = RoundedPanel(_shadeViewport, "ShadeSheet", Surface, 0f, 0f, 1392f, ShadeHeight).rectTransform;
        _shadeSheet.GetComponent<Image>().raycastTarget = true;
        _shadeExtraViewport = Node(_shadeSheet, "ExpandedSettingsViewport");
        Place(_shadeExtraViewport, 0f, 312f, 1392f, 296f);
        _shadeExtraViewport.gameObject.AddComponent<RectMask2D>();
        _shadeExtra = Node(_shadeExtraViewport, "ExpandedQuickSettings");
        Place(_shadeExtra, 0f, -312f, 1392f, ShadeHeight);
        _shadeExtraInput = _shadeExtra.gameObject.AddComponent<CanvasGroup>();
        _shadeClock = Text(_shadeSheet, "Clock", "", 46f, Ink, 40f, 24f, 250f, 62f, role: TabletTextRole.Display);
        _shadeDate = Text(_shadeSheet, "Date", "", 24f, Muted, 40f, 90f, 790f, 36f);
        var settings = Button(_shadeSheet, "QuickSettings", "Settings", 1098f, 54f, 250f, 58f, () =>
        {
            _select(ClientTerminalApp.Settings);
            SetShadeAmount(0f);
        });
        Icon(settings.Image.transform, ClientTerminalApp.Settings, Ink, 18f, 17f, 24f);
        Place(settings.Label.rectTransform, 60f, 0f, 174f, 58f);
        Button(_shadeSheet, "SleepTablet", "Lock", 888f, 54f, 190f, 58f, () => DesktopCommand("lock"));
        _shadeMode = Button(_shadeSheet, "QuickMode", "Mode: Patrol", 40f, 150f, 420f, 84f, OpenShadeModePicker);
        _shadeDnd = Button(_shadeSheet, "DoNotDisturb", "Alerts on", 480f, 150f, 420f, 84f,
            () => EditPreference("notifications", (!_preferences.Notifications).ToString()));
        _shadeTone = Button(_shadeExtra, "QuickTone", "Tone: Pulse", 40f, 320f, 644f, 72f, () =>
        {
            var tone = _preferences.Tone == ClientTabletTone.Pulse ? ClientTabletTone.Chime
                : _preferences.Tone == ClientTabletTone.Chime ? ClientTabletTone.Radio : ClientTabletTone.Pulse;
            EditPreference("tone", tone.ToString());
        });
        _shadeTheme = Button(_shadeExtra, "QuickTheme", "Theme: Field", 704f, 320f, 644f, 72f, () =>
        {
            var theme = _preferences.Theme == ClientTabletTheme.Field ? ClientTabletTheme.Light
                : _preferences.Theme == ClientTabletTheme.Light ? ClientTabletTheme.Amber : ClientTabletTheme.Field;
            EditPreference("theme", theme.ToString());
        });
        _shadeSatellite = Button(_shadeSheet, "QuickSatellite", "SATCOM", 920f, 150f, 306f, 84f,
            () => _command("os", "satellite-toggle"));
        Button(_shadeSheet, "SatelliteSettings", "Link\ndetails", 1238f, 150f, 110f, 84f,
            () => OpenSettingsTarget(2, 0f));
        foreach (var tile in new[] { _shadeMode, _shadeDnd, _shadeSatellite, _shadeTone, _shadeTheme })
        {
            tile.Label.fontSize = 29f;
            tile.Label.alignment = TextAlignmentOptions.Center;
        }
        Button(_shadeSheet, "BrightnessSettings", "Display ›", 40f, 242f, 230f, 48f,
            () => OpenSettingsTarget(0, 290f));
        _shadeBrightness = PreferenceSlider(_shadeSheet, "QuickBrightness", "brightness", 288f, 238f, 910f, .35f, 1f);
        _shadeBrightnessValue = Text(_shadeSheet, "BrightnessValue", "100%", 24f, Ink, 1220f, 250f, 128f, 32f, role: TabletTextRole.Status);
        _shadeBrightnessValue.alignment = TextAlignmentOptions.TopRight;
        Button(_shadeExtra, "SoundSettings", "Sound ›", 40f, 412f, 230f, 48f,
            () => OpenSettingsTarget(1, 218f));
        _shadeVolume = PreferenceSlider(_shadeExtra, "QuickVolume", "volume", 288f, 408f, 910f, 0f, 1f);
        _shadeVolumeValue = Text(_shadeExtra, "VolumeValue", "35%", 24f, Ink, 1220f, 420f, 128f, 32f, role: TabletTextRole.Status);
        _shadeVolumeValue.alignment = TextAlignmentOptions.TopRight;
        Panel(_shadeExtra, "WatchRule", Rule, 40f, 480f, 1308f, 2f);
        Button(_shadeExtra, "ClockShortcut", "Clock ›", 40f, 490f, 250f, 48f, () =>
        {
            SetShadeAmount(0f);
            SelectApp(ClientTerminalApp.Watch);
        });
        _watchStatus = Text(_shadeExtra, "WatchStatus", "Not running", 27f, Mint, 316f, 496f, 1032f, 36f, true);
        var durations = new[] { 5, 15, 30 };
        for (var index = 0; index < durations.Length; index++)
        {
            var minutes = durations[index];
            _watchPresets.Add(Button(_shadeExtra, "Watch" + minutes, minutes + " min",
                40f + index * 200f, 548f, 184f, 52f,
                () => _command("os", "watch-start:" + (minutes * 60).ToString(CultureInfo.InvariantCulture))));
        }
        _watchPause = Button(_shadeExtra, "PauseWatch", "Pause", 840f, 548f, 230f, 52f,
            () => _command("os", _watch.State == ClientTabletWatchState.Paused ? "watch-resume" : "watch-pause"));
        _watchCancel = Button(_shadeExtra, "CancelWatch", "Cancel", 1090f, 548f, 258f, 52f,
            () => _command("os", "watch-cancel"));
        Panel(_shadeExtra, "NotificationsRule", Rule, 40f, 606f, 1308f, 2f);
        _shadeNotifications = Text(_shadeSheet, "NotificationsHeading", "Notifications", 24f, Ink, 40f, 574f, 980f, 36f, true);
        _markAllRead = Button(_shadeSheet, "MarkAllRead", "Mark all read", 1046f, 564f, 302f, 48f,
            () => _command("os", "messages-read"));
        _shadeNotificationList = Scroll(_shadeSheet, "NotificationCards", 40f, 622f, 1308f, 106f);
        _shadeMessage = Button(_shadeNotificationList.Content, "MessageNotification", "No unread messages", 0f, 0f, 1288f, 100f, () =>
        {
            if (_disposed || DesktopCaptured || !_shadeCapture) return;
            _select(ClientTerminalApp.Messages);
            SetShadeAmount(0f);
        });
        var messageIcon = RoundedPanel(_shadeMessage.Image.transform, "MessageIcon", AppColor(ClientTerminalApp.Messages),
            20f, 20f, 60f, 60f);
        Icon(messageIcon.transform, ClientTerminalApp.Messages, Color.white, 14f, 14f, 32f);
        _shadeMessage.Label.fontSize = 25f;
        _shadeMessage.Label.enableWordWrapping = true;
        Place(_shadeMessage.Label.rectTransform, 104f, 8f, 1160f, 84f);
        for (var index = 0; index < 4; index++)
        {
            var slot = index;
            var card = Button(_shadeNotificationList.Content, "FieldNotification" + index, "", 0f, (index + 1) * 112f, 1092f, 100f,
                () => InvokeShadeNotification(slot, dismiss: false));
            var title = Text(card.Image.transform, "Title", "", 24f, Ink, 20f, 8f, 1052f, 34f, true);
            var detail = Text(card.Image.transform, "Detail", "", 22f, Muted, 20f, 48f, 1052f, 40f);
            title.overflowMode = detail.overflowMode = TextOverflowModes.Ellipsis;
            var dismissCard = Button(_shadeNotificationList.Content, "DismissNotification" + index, "Dismiss", 1108f, (index + 1) * 112f, 180f, 100f,
                () => InvokeShadeNotification(slot, dismiss: true));
            _shadeFieldCards.Add(card);
            _shadeFieldTitles.Add(title);
            _shadeFieldDetails.Add(detail);
            _shadeFieldDismiss.Add(dismissCard);
            card.Image.gameObject.SetActive(false);
            dismissCard.Image.gameObject.SetActive(false);
        }
        _shadeNotificationList.Height(100f);
        _shadeFooter = Node(_shadeSheet, "ShadeFooter");
        _shadeSystem = Text(_shadeFooter, "RuntimeStatus", "", 22f, Muted, 40f, 8f, 930f, 32f);
        _shadeSystem.overflowMode = TextOverflowModes.Ellipsis;
        _shadeExpand = Button(_shadeFooter, "Expand", "More controls", 1046f, 0f, 302f, 48f,
            () => SettleShade(_shadeAmount > .85f ? CompactShadeAmount : 1f));
        RoundedPanel(_shadeFooter, "CollapseHandle", Muted, 630f, 54f, 132f, 5f);
        BuildShadeModePicker();
        SetShadeAmount(0f);
        SetWatch(default);
    }

    // The owner supplies coordinates already projected onto the tablet's 1440 x 900 canvas.
    internal void UpdatePointer(Vector2? normalizedBottomLeft, bool pressed, bool held, bool released)
    {
        if (_disposed) return;
        UpdateClock();
        UpdatePresentation();
        AnimateShade();
        if (UpdateDesktopPointer(normalizedBottomLeft, pressed, held, released)) return;
        if (released || !held && !pressed) CommitSlider();
        if (!normalizedBottomLeft.HasValue)
        {
            CancelPointer();
            return;
        }
        if (ShadeModePickerOpen) return;
        var point = normalizedBottomLeft.Value;
        var x = point.x * 1440f;
        var y = (1f - point.y) * 900f;
        if (pressed)
        {
            CancelShadeDrag();
            var statusGesture = !_shadeCapture && x >= 24f && x < 1248f && y >= 24f && y <= 72f;
            var sheetBottom = 24f + ShadeHeight * _shadeAmount;
            var shadeGesture = _shadeCapture && x >= 24f && x <= 1416f
                && (y >= 24f && y < 146f && x < 880f
                    || x >= 450f && x <= 950f && y >= sheetBottom - 58f && y <= sheetBottom);
            if (statusGesture || shadeGesture)
            {
                _shadeDragRestoreAmount = _shadeSettling ? _shadeTarget : _shadeAmount;
                _shadeSettling = false;
                _shadeDragging = true;
                _shadeMoved = false;
                _shadeDragStart = y;
                _shadeDragAmount = _shadeAmount;
                ApplyShadeAmount(_shadeAmount);
                _messageInput.DeactivateInputField();
                DeactivateFieldInputs();
                DeactivateInboxInputs();
                DeactivateSettingsInputs();
                foreach (var field in _fields) field.Input.DeactivateInputField();
            }
        }
        if (_shadeDragging && (held || released))
        {
            var distance = y - _shadeDragStart;
            if (Math.Abs(distance) > 7f) _shadeMoved = true;
            if (_shadeMoved) ApplyShadeAmount(Math.Clamp(_shadeDragAmount + distance / ShadeHeight, 0f, 1f));
        }
        if (released) EndShadeDrag();
        else if (_shadeDragging && !held && !pressed) CancelShadeDrag();
    }

    internal void CancelPointer()
    {
        if (!IsAlive) return;
        CancelShadeDrag();
        if (_shadeSettling) SetShadeAmount(_shadeTarget);
        DesktopResetWakeGesture();
        CommitSlider();
    }

    private void CancelShadeDrag()
    {
        if (!_shadeDragging) return;
        // A lost pointer is not a tap. Restore the position the interrupted gesture owned.
        SetShadeAmount(_shadeDragRestoreAmount);
    }

    private void EndShadeDrag()
    {
        if (!_shadeDragging) return;
        ReleaseShadePointer();
        float target;
        if (!_shadeMoved)
            target = _shadeDragAmount < .1f ? CompactShadeAmount : _shadeDragAmount < .85f ? 1f : 0f;
        else if (_shadeDragAmount < .1f)
            target = _shadeAmount > .85f ? 1f : _shadeAmount > .12f ? CompactShadeAmount : 0f;
        else if (_shadeAmount > _shadeDragAmount + .05f)
            target = 1f;
        else if (_shadeAmount < _shadeDragAmount - .05f)
            target = _shadeDragAmount > .85f && _shadeAmount > .32f ? CompactShadeAmount : 0f;
        else
            target = _shadeDragAmount > .85f ? 1f : CompactShadeAmount;
        SettleShade(target);
    }

    private void ReleaseShadePointer()
    {
        // Native button callbacks can run after this frame's coordinate gesture finishes.
        if (_shadeDragging) _shadeGestureEndFrame = Time.frameCount;
        _shadeDragging = false;
    }

    private void SetShadeAmount(float amount)
    {
        ReleaseShadePointer();
        _shadeMoved = false;
        _shadeSettling = false;
        ApplyShadeAmount(amount);
    }

    private void SettleShade(float amount)
    {
        ReleaseShadePointer();
        _shadeMoved = false;
        if (_preferences.ReducedMotion || amount == _shadeAmount) { SetShadeAmount(amount); return; }
        _shadeTarget = amount;
        _shadeSettleFrom = _shadeAmount;
        _shadeSettleAt = Time.unscaledTime;
        _shadeSettling = true;
    }

    private void AnimateShade()
    {
        if (!_shadeSettling) return;
        var progress = Mathf.Clamp01((Time.unscaledTime - _shadeSettleAt) / .18f);
        ApplyShadeAmount(Mathf.Lerp(_shadeSettleFrom, _shadeTarget, Mathf.SmoothStep(0, 1, progress)));
        if (progress >= 1f) _shadeSettling = false;
    }

    private void ApplyShadeAmount(float amount)
    {
        _shadeAmount = DesktopCaptured ? 0f : Math.Clamp(amount, 0f, 1f);
        if (_shadeAmount == 0f) CloseShadeModePicker();
        if (DesktopCaptured) ReleaseShadePointer();
        var captured = _shadeDragging || _shadeAmount > 0f;
        var captureChanged = captured != _shadeCapture;
        _shadeCapture = captured;
        if (captureChanged) _command("os", captured ? "content-covered" : "content-visible");
        RefreshDesktopInput();
        _shadeRoot.gameObject.SetActive(_shadeCapture);
        Place(_shadeViewport, 0f, 0f, 1392f, ShadeHeight * _shadeAmount);
        _shadeScrim.color = new Color(0f, 0f, 0f, .62f * _shadeAmount);
        LayoutQuickShade();
        if (_shadeCapture) RefreshNotificationShade();
    }

    private void LayoutQuickShade()
    {
        var expansion = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(CompactShadeAmount, 1f, _shadeAmount));
        _shadeExtraViewport.gameObject.SetActive(expansion > 0f);
        Place(_shadeExtraViewport, 0f, 312f, 1392f, 296f * expansion);
        _shadeExtraInput.alpha = expansion;
        _shadeExtraInput.interactable = expansion >= .999f && !_shadeDragging;
        _shadeExtraInput.blocksRaycasts = _shadeExtraInput.interactable;
        Place(_shadeNotifications.rectTransform, 40f, Mathf.Lerp(322f, 610f, expansion), 980f, 36f);
        Place(_markAllRead.Image.rectTransform, 1046f, Mathf.Lerp(314f, 602f, expansion), 302f, 48f);
        Place(_shadeNotificationList.Root.rectTransform, 40f, Mathf.Lerp(368f, 650f, expansion),
            1308f, Mathf.Lerp(116f, 104f, expansion));
        Place(_shadeFooter, 0f, Mathf.Lerp(496f, 758f, expansion), 1392f, 60f);
        _shadeExpand.Label.text = _shadeAmount > .85f ? "Fewer controls" : "More controls";
    }

    internal void SetWatch(ClientTabletWatchSnapshot watch)
    {
        _watch = watch;
        if (_watchStatus == null) return;
        var active = watch.State is ClientTabletWatchState.Running or ClientTabletWatchState.Paused;
        _watchStatus.text = watch.State switch
        {
            ClientTabletWatchState.Running => WatchTime(watch.RemainingSeconds) + "  ·  Running",
            ClientTabletWatchState.Paused => WatchTime(watch.RemainingSeconds) + "  ·  Paused",
            ClientTabletWatchState.Elapsed => "Watch complete",
            _ => "Not running"
        };
        foreach (var preset in _watchPresets) preset.Button.interactable = !active;
        _watchPause.Button.interactable = active;
        _watchPause.Label.text = watch.State == ClientTabletWatchState.Paused ? "Resume" : "Pause";
        _watchCancel.Button.interactable = watch.State != ClientTabletWatchState.Idle;
        _watchCancel.Label.text = watch.State == ClientTabletWatchState.Elapsed ? "Dismiss" : "Cancel";
    }

    private static string WatchTime(int seconds) => (Math.Max(0, seconds) / 60).ToString("00", CultureInfo.InvariantCulture)
        + ":" + (Math.Max(0, seconds) % 60).ToString("00", CultureInfo.InvariantCulture);

    internal void SetNotificationSummary(int unread, string latestLine)
    {
        if (_disposed) return;
        _unreadMessages = Math.Max(0, unread);
        _latestMessage = latestLine ?? "";
        var badge = unread <= 0 ? "" : unread > 99 ? "99+" : unread.ToString(CultureInfo.InvariantCulture);
        _messagesUnread.text = badge;
        _messagesUnreadBadge.gameObject.SetActive(unread > 0);
        _messagesHomeUnread.text = badge;
        RefreshNotificationShade();
    }

    internal void SetFieldNotificationShade(IReadOnlyList<ClientTabletNotification> notifications)
    {
        if (_disposed || _shadeNotificationList == null) return;
        var count = Math.Min(4, notifications.Count);
        var changed = _shadeNotificationTotal != notifications.Count || _shadeFieldNotifications.Count != count;
        for (var index = 0; !changed && index < count; index++)
            changed = _shadeFieldNotifications[index] != notifications[notifications.Count - 1 - index];
        if (!changed) return;
        _shadeNotificationTotal = notifications.Count;
        _shadeFieldNotifications.Clear();
        for (var index = 0; index < _shadeFieldCards.Count; index++)
        {
            var shown = index < count;
            _shadeFieldCards[index].Image.gameObject.SetActive(shown);
            _shadeFieldDismiss[index].Image.gameObject.SetActive(shown);
            if (!shown) continue;
            var notification = notifications[notifications.Count - 1 - index];
            _shadeFieldNotifications.Add(notification);
            _shadeFieldTitles[index].text = notification.Title;
            _shadeFieldDetails[index].text = notification.Detail;
        }
        _shadeMessage.Image.gameObject.SetActive(true);
        _shadeNotificationList.Height((count + 1) * 112f - 12f);
        _shadeNotificationList.Reset();
        RefreshNotificationShade();
    }

    private void InvokeShadeNotification(int slot, bool dismiss)
    {
        if (_disposed || DesktopCaptured || !_shadeCapture || _shadeDragging || slot >= _shadeFieldNotifications.Count) return;
        var id = _shadeFieldNotifications[slot].Id.ToString(CultureInfo.InvariantCulture);
        _command("field", (dismiss ? "dismiss:" : "notification:") + id);
        if (!dismiss && !_disposed) SetShadeAmount(0f);
    }

    private void RefreshNotificationShade()
    {
        if (_shadeNotifications == null) return;
        var alertsAudible = _preferences.Notifications && _preferences.EffectiveVolume > 0f;
        _shadeDnd.Label.text = _preferences.Notifications ? alertsAudible ? "Alerts on" : "Alerts muted" : "Alerts quiet";
        _shadeDnd.Image.color = alertsAudible ? Mint : Raised;
        _shadeDnd.Label.color = alertsAudible ? Screen : Ink;
        _shadeMode.Label.text = "Mode: " + _preferences.Mode;
        RefreshShadeModePicker();
        _shadeMode.Image.color = _preferences.Mode == ClientTabletMode.Silent ? Raised : Mint;
        _shadeMode.Label.color = _preferences.Mode == ClientTabletMode.Silent ? Ink : Screen;
        _shadeTone.Label.text = "Tone: " + _preferences.Tone;
        _shadeTheme.Label.text = "Theme: " + _preferences.Theme;
        _shadeNotifications.text = _shadeNotificationTotal > 0
            ? "Alerts: " + _shadeNotificationTotal + (_shadeNotificationTotal > 4 ? " (latest 4)" : "")
                + "  ·  Inbox: " + _unreadMessages + " unread"
            : _unreadMessages == 0 ? "Notifications" : "Notifications  ·  " + _unreadMessages + " unread";
        _markAllRead.Button.interactable = _unreadMessages > 0;
        _shadeMessage.Label.text = _unreadMessages > 0
            ? "Open inbox  ·  " + _unreadMessages + " unread\n" + _latestMessage
            : "You're up to date\nOpen Messages to view your conversations.";
        _shadeSystem.text = _link.text + "  ·  Swipe up to close";
        UpdateClock();
    }

    private void UpdateClock()
    {
        UpdateDesktopClock();
        if (_shadeClock == null) return;
        var minute = _islandClock.Key;
        if (minute == _clockMinute) return;
        _clockMinute = minute;
        _shadeClock.text = _islandClock.TimeText;
        _shadeDate.text = _islandClock.DayText + "  /  Island time";
    }

    private static string Percent(float value) => Math.Round(value * 100f).ToString(CultureInfo.InvariantCulture) + "%";
    private static string Number(float value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private Color[] Palette() => new[] { Bezel, Chrome, ChromeRaised, ChromeInk, ChromeMuted, ChromeMint,
        Screen, Surface, Raised, Rule, Ink, Muted, Mint, Amber, ReviewSurface, Dim };

    private void ApplyTheme(ClientTabletTheme theme)
    {
        if (_paintedTheme == theme) return;
        var before = Palette();
        if (theme == ClientTabletTheme.Light)
        {
            Bezel = new(.055f, .077f, .089f, 1f); Chrome = new(.075f, .125f, .16f, 1f);
            ChromeRaised = new(.19f, .30f, .36f, 1f); ChromeInk = new(.95f, .98f, .99f, 1f);
            ChromeMuted = new(.74f, .83f, .87f, 1f); ChromeMint = new(.61f, .92f, .78f, 1f);
            Screen = new(.81f, .86f, .85f, 1f); Surface = new(.94f, .96f, .95f, 1f);
            Raised = new(.79f, .87f, .85f, 1f); Rule = new(.38f, .51f, .46f, 1f);
            Ink = new(.075f, .14f, .12f, 1f); Muted = new(.28f, .37f, .33f, 1f);
            Mint = new(.08f, .40f, .28f, 1f); Amber = new(.49f, .27f, .055f, 1f);
            ReviewSurface = new(.99f, .89f, .65f, 1f); Dim = new(.39f, .45f, .41f, 1f);
        }
        else if (theme == ClientTabletTheme.Amber)
        {
            Bezel = new(.08f, .065f, .045f, 1f); Chrome = new(.11f, .084f, .05f, 1f);
            ChromeRaised = new(.32f, .23f, .11f, 1f); ChromeInk = new(.99f, .92f, .75f, 1f);
            ChromeMuted = new(.78f, .67f, .47f, 1f); ChromeMint = new(.99f, .76f, .33f, 1f);
            Screen = new(.15f, .117f, .075f, 1f); Surface = new(.20f, .16f, .10f, 1f);
            Raised = new(.29f, .23f, .14f, 1f); Rule = new(.47f, .37f, .22f, 1f);
            Ink = new(.96f, .88f, .69f, 1f); Muted = new(.85f, .76f, .58f, 1f);
            Mint = new(.99f, .77f, .38f, 1f); Amber = new(1f, .81f, .39f, 1f);
            ReviewSurface = new(.35f, .25f, .10f, 1f); Dim = new(.57f, .47f, .32f, 1f);
        }
        else
        {
            Bezel = new(.055f, .077f, .069f, 1f); Chrome = new(.071f, .106f, .102f, 1f);
            ChromeRaised = new(.19f, .29f, .25f, 1f); ChromeInk = new(.95f, .98f, .96f, 1f);
            ChromeMuted = new(.68f, .78f, .73f, 1f); ChromeMint = new(.65f, .91f, .73f, 1f);
            Screen = new(.105f, .145f, .133f, 1f); Surface = new(.15f, .20f, .18f, 1f);
            Raised = new(.22f, .29f, .25f, 1f); Rule = new(.36f, .47f, .40f, 1f);
            Ink = new(.91f, .95f, .91f, 1f); Muted = new(.74f, .82f, .76f, 1f);
            Mint = new(.63f, .88f, .70f, 1f); Amber = new(.98f, .79f, .43f, 1f);
            ReviewSurface = new(.30f, .25f, .15f, 1f); Dim = new(.47f, .57f, .50f, 1f);
        }
        var after = Palette();
        for (var index = _ownedGraphics.Count - 1; index >= 0; index--)
        {
            var graphic = _ownedGraphics[index];
            if (graphic == null || !graphic) { _ownedGraphics.RemoveAt(index); continue; }
            for (var color = 0; color < before.Length; color++)
            {
                if (graphic.color != before[color]) continue;
                graphic.color = after[color];
                break;
            }
        }
        _paintedTheme = theme;
        _messageInput.caretColor = Mint;
        foreach (var field in _fields) field.Input.caretColor = Mint;
        SelectSettingsSection(_settingsSection);
        RefreshMessengerChannels();
    }
}
