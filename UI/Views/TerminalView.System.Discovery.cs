// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Events;

namespace Modstoke.Client;

internal sealed partial class ClientTerminalView
{
    private sealed record SettingsTarget(string Title, string Keywords, int Section, float Offset);
    private readonly SettingsTarget[] _settingsTargets =
    {
        new("Appearance & theme", "display colors field light amber", 0, 90f),
        new("Screen brightness", "display dim night brightness", 0, 290f),
        new("Text size & motion", "display accessibility large normal reduced animation", 0, 550f),
        new("Display calibration", "display readability sample contrast", 0, 790f),
        new("Message alerts & volume", "sound notification mute quiet loud", 1, 90f),
        new("Notification tone & preview", "sound pulse chime radio listen", 1, 350f),
        new("Channel sounds", "expedition tribe direct messages alerts", 1, 580f),
        new("Alert presets", "sound all direct only quiet channels", 1, 1190f),
        new("Why are alerts quiet?", "sound troubleshoot silent muted volume fix", 1, 1410f),
        new("Satellite link", "satcom transceiver relay reconnect connection", 2, 0f),
        new("About & developer information", "version diagnostics runtime", 3, 0f),
        new("Operating mode comparison", "patrol camp silent brightness volume", 4, 550f),
        new("Watch HUD & shortcut", "keyboard key f3 f9 f11 immersive minimal standard", 5, 100f),
        new("Watch appearance & privacy", "theme wrist night readings", 5, 690f),
        new("System & storage", "runtime storage saved notebook reminders", 6, 0f)
    };
    private readonly Dictionary<int, ScrollArea> _settingsScrolls = new();
    private readonly List<ButtonWidget> _settingsResults = new();
    private RectTransform _settingsSearchRoot = null!;
    private TMP_InputField _settingsSearch = null!;
    private ScrollArea _settingsResultScroll = null!;
    private TextMeshProUGUI _settingsSearchSummary = null!;
    private TextMeshProUGUI _modeComparison = null!;
    private TextMeshProUGUI _soundDiagnosis = null!;
    private ButtonWidget _soundResolve = null!;
    private readonly List<ButtonWidget> _alertPresets = new();
    private RectTransform _shadeModePicker = null!;
    private readonly Dictionary<ClientTabletMode, ButtonWidget> _shadeModeChoices = new();
    private bool ShadeModePickerOpen => _shadeModePicker != null && _shadeModePicker
        && _shadeModePicker.gameObject.activeInHierarchy;

    private void BuildSettingsDiscovery(ScrollArea display, ScrollArea sound, ScrollArea modes, ScrollArea watch)
    {
        _settingsScrolls.Add(0, display);
        _settingsScrolls.Add(1, sound);
        _settingsScrolls.Add(4, modes);
        _settingsScrolls.Add(5, watch);
        _settingsScrolls.Add(6, _runtimeScroll);
        BuildAlertHelpers(sound.Content);
        sound.Height(1686f);
        _settingsSearchRoot = RoundedPanel(_settingsSurface, "SettingsFinder", Surface, 284f, 0f, 1044f, 548f).rectTransform;
        Text(_settingsSearchRoot, "Heading", "Find a setting", 32f, Ink, 32f, 20f, 720f, 48f, true);
        Button(_settingsSearchRoot, "Close", "Back", 832f, 20f, 180f, 48f,
            () => SelectSettingsSection(_settingsSection));
        _settingsSearch = Input(_settingsSearchRoot, "", 80);
        Place(_settingsSearch.GetComponent<RectTransform>(), 32f, 82f, 790f, 56f);
        var hint = Text(_settingsSearch.textViewport, "Hint", "Try brightness, quiet, watch…", 24f, Muted, 0f, 0f, 750f, 42f);
        Stretch(hint.rectTransform, 0f, 0f, 0f, 0f);
        hint.alignment = TextAlignmentOptions.MidlineLeft;
        _settingsSearch.placeholder = hint;
        _settingsSearch.onValueChanged.AddListener((UnityAction<string>)new Action<string>(_ => FilterSettings()));
        Button(_settingsSearchRoot, "Clear", "Clear", 842f, 82f, 170f, 56f, () =>
        {
            _settingsSearch.text = "";
            _settingsSearch.ActivateInputField();
        });
        _settingsSearchSummary = Text(_settingsSearchRoot, "Results", "", 22f, Muted, 32f, 150f, 980f, 42f);
        _settingsResultScroll = Scroll(_settingsSearchRoot, "Matches", 24f, 198f, 996f, 326f);
        for (var index = 0; index < _settingsTargets.Length; index++)
        {
            var target = _settingsTargets[index];
            _settingsResults.Add(Button(_settingsResultScroll.Content, "Result" + index, target.Title,
                8f, index * 66f, 956f, 56f, () => OpenSettingsTarget(target.Section, target.Offset)));
        }
        FilterSettings();
        _settingsSearchRoot.gameObject.SetActive(false);
    }

    private void OpenSettingsSearch()
    {
        if (_disposed || ContentObscured) return;
        CommitSlider();
        ClearPreferenceReset();
        foreach (var page in _settingsPages) page.gameObject.SetActive(false);
        _settingsSearchRoot.gameObject.SetActive(true);
        FilterSettings();
        _settingsSearch.ActivateInputField();
    }

    private void FilterSettings()
    {
        var terms = _settingsSearch.text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var count = 0;
        for (var index = 0; index < _settingsTargets.Length; index++)
        {
            var target = _settingsTargets[index];
            var haystack = target.Title + " " + target.Keywords;
            var matches = true;
            foreach (var term in terms)
                if (!haystack.Contains(term, StringComparison.OrdinalIgnoreCase)) { matches = false; break; }
            var row = _settingsResults[index];
            row.Image.gameObject.SetActive(matches);
            if (matches) Place(row.Image.rectTransform, 8f, count++ * 66f, 956f, 56f);
        }
        _settingsSearchSummary.text = count == 0 ? "No matching settings. Try a shorter phrase or Clear."
            : count + " settings  ·  Select a result to jump to its controls";
        _settingsResultScroll.Height(Math.Max(1, count) * 66f);
        _settingsResultScroll.Reset();
    }

    private void OpenSettingsTarget(int section, float offset)
    {
        if (_disposed || DesktopCaptured) return;
        SetShadeAmount(0f);
        SelectApp(ClientTerminalApp.Settings);
        SelectSettingsSection(section);
        if (!_settingsScrolls.TryGetValue(section, out var scroll)) return;
        scroll.Scroll.StopMovement();
        var maximum = Math.Max(0f, scroll.Content.sizeDelta.y - scroll.Scroll.viewport.rect.height);
        scroll.Content.anchoredPosition = new Vector2(0f, Math.Clamp(offset - 24f, 0f, maximum));
    }

    internal void DeactivateSettingsInputs()
    {
        if (_settingsSearch != null) _settingsSearch.DeactivateInputField();
    }

    private void BuildModeComparison(RectTransform parent)
    {
        Panel(parent, "ComparisonRule", Rule, 32f, 550f, 980f, 2f);
        Text(parent, "ComparisonHeading", "Your settings in each mode", 27f, Ink, 32f, 574f, 980f, 42f, true);
        _modeComparison = Text(parent, "ComparisonValues", "", 24f, Ink, 32f, 636f, 980f, 184f);
    }

    private void BuildAlertHelpers(RectTransform parent)
    {
        Text(parent, "AlertPresetsHeading", "Channel presets", 27f, Ink, 32f, 1190f, 980f, 42f, true);
        var labels = new[] { "All channels", "Direct only", "Quiet channels" };
        for (var index = 0; index < labels.Length; index++)
        {
            var preset = index;
            _alertPresets.Add(Button(parent, "AlertPreset" + index, labels[index], 32f + index * 328f,
                1250f, 310f, 64f, () => ApplyAlertPreset(preset)));
        }
        Text(parent, "PresetHelp", "Applies to Expedition, Tribe and Direct sounds. Your master alert switch, volume and operating mode stay as selected.",
            22f, Muted, 32f, 1330f, 980f, 64f);
        Text(parent, "DiagnosisHeading", "Why are alerts quiet?", 27f, Ink, 32f, 1410f, 980f, 42f, true);
        _soundDiagnosis = Text(parent, "Diagnosis", "", 23f, Muted, 32f, 1468f, 980f, 100f);
        _soundResolve = Button(parent, "ResolveSound", "", 32f, 1590f, 600f, 64f, ResolveSoundSuppression);
        Button(parent, "ReviewChannels", "Review channels", 652f, 1590f, 360f, 64f,
            () => OpenSettingsTarget(1, 580f));
    }

    private void ApplyAlertPreset(int preset)
    {
        EditPreference("expedition-alerts", (preset == 0).ToString());
        EditPreference("tribe-alerts", (preset == 0).ToString());
        EditPreference("direct-alerts", (preset != 2).ToString());
    }

    private void ResolveSoundSuppression()
    {
        if (_preferences.Mode == ClientTabletMode.Silent) OpenSettingsTarget(4, 0f);
        else if (!_preferences.Notifications) EditPreference("notifications", "true");
        else if (_preferences.Volume <= 0f) EditPreference("volume", "0.35");
        else OpenSettingsTarget(1, 580f);
    }

    private void RefreshSettingsContext()
    {
        if (_modeComparison == null || _soundDiagnosis == null) return;
        var p = _preferences;
        _settingsTabs[0].Label.text = "Display\n" + p.Theme + " · " + Percent(p.EffectiveBrightness);
        _settingsTabs[1].Label.text = "Sound & alerts\n" + (p.Notifications && p.EffectiveVolume > 0f ? Percent(p.EffectiveVolume) : "Quiet");
        _settingsTabs[2].Label.text = "Satellite\n" + SatelliteCaption(_settingsSatellite.State);
        _settingsTabs[3].Label.text = "About\nVersion & details";
        _settingsTabs[4].Label.text = "Operating mode\n" + p.Mode;
        _settingsTabs[5].Label.text = "Watch & HUD\n" + p.HudMode + " · " + p.WatchKey;
        _settingsTabs[6].Label.text = "System\nStorage & activity";
        _modeComparison.text = "Chosen levels: screen " + Percent(p.Brightness) + "  ·  volume " + Percent(p.Volume)
            + "\nPatrol / Camp: screen " + Percent(p.Brightness) + "  ·  volume " + Percent(p.Volume)
            + "\nSilent: screen " + Percent(Math.Min(p.Brightness, .55f)) + "  ·  volume 0%"
            + "\nActive: " + p.Mode + "  ·  Message alerts " + (p.Notifications ? "enabled" : "quiet");
        var quiet = new List<string>();
        if (p.Mode == ClientTabletMode.Silent) quiet.Add("Silent mode mutes all alert audio.");
        if (!p.Notifications) quiet.Add("The message alert switch is quiet.");
        if (p.Volume <= 0f) quiet.Add("Alert volume is 0%.");
        if (!p.ExpeditionAlerts && !p.TribeAlerts && !p.DirectAlerts) quiet.Add("All channel sounds are quiet.");
        _soundDiagnosis.text = quiet.Count == 0
            ? "Master audio is enabled. Individual quiet channels still remain silent; incoming messages stay in the inbox."
            : string.Join(" ", quiet);
        _soundResolve.Label.text = p.Mode == ClientTabletMode.Silent ? "Choose an audible mode"
            : !p.Notifications ? "Enable message alerts" : p.Volume <= 0f ? "Set volume to 35%" : "Review channel switches";
        SetPreferenceChoice(_alertPresets[0], p.ExpeditionAlerts && p.TribeAlerts && p.DirectAlerts);
        SetPreferenceChoice(_alertPresets[1], !p.ExpeditionAlerts && !p.TribeAlerts && p.DirectAlerts);
        SetPreferenceChoice(_alertPresets[2], !p.ExpeditionAlerts && !p.TribeAlerts && !p.DirectAlerts);
    }

    private void BuildShadeModePicker()
    {
        _shadeModePicker = Node(_shadeRoot, "ModePicker");
        Stretch(_shadeModePicker, 0f, 0f, 0f, 0f);
        var dismiss = Button(_shadeModePicker, "DismissModes", "", 0f, 0f, 1392f, 852f,
            () => CloseShadeModePicker());
        dismiss.Image.color = new Color(0f, 0f, 0f, .6f);
        var panel = RoundedPanel(_shadeModePicker, "ModeChoices", Surface, 40f, 142f, 630f, 340f).rectTransform;
        Text(panel, "Heading", "Choose operating mode", 27f, Ink, 20f, 16f, 590f, 42f, true);
        var index = 0;
        foreach (var mode in new[] { ClientTabletMode.Patrol, ClientTabletMode.Camp, ClientTabletMode.Silent })
        {
            var choice = mode;
            _shadeModeChoices.Add(mode, Button(panel, "Choose" + mode, mode.ToString(), 20f, 72f + index++ * 82f, 590f, 70f, () =>
            {
                EditPreference("mode", choice.ToString());
                CloseShadeModePicker();
            }));
        }
        _shadeModePicker.gameObject.SetActive(false);
    }

    private bool CloseShadeModePicker()
    {
        if (_shadeModePicker == null || !_shadeModePicker || !_shadeModePicker.gameObject.activeSelf) return false;
        _shadeModePicker.gameObject.SetActive(false);
        return true;
    }

    private void OpenShadeModePicker()
    {
        if (_disposed || DesktopCaptured || !_shadeCapture || _shadeDragging) return;
        RefreshShadeModePicker();
        _shadeModePicker.gameObject.SetActive(true);
    }

    private void RefreshShadeModePicker()
    {
        foreach (var pair in _shadeModeChoices)
        {
            pair.Value.Label.text = pair.Key + (pair.Key == ClientTabletMode.Silent ? " · Dim & mute" : " · Your chosen levels");
            SetPreferenceChoice(pair.Value, pair.Key == _preferences.Mode);
        }
    }
}
