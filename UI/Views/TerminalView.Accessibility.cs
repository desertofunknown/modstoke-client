// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Modstoke.Client;

internal sealed partial class ClientTerminalView
{
    private readonly List<(Selectable Control, Outline Ring)> _comfortFocus = new();
    private readonly Dictionary<int, (ButtonWidget Reset, ButtonWidget Cancel, TextMeshProUGUI Hint)> _preferenceResets = new();
    private GameObject? _comfortSelected;
    private int _resetSection = -1;
    private float _resetExpires;

    private void TrackComfortFocus(Selectable control, Image image)
    {
        var ring = image.gameObject.AddComponent<Outline>();
        ring.effectColor = Mint;
        ring.effectDistance = new Vector2(3f, -3f);
        ring.useGraphicAlpha = false;
        ring.enabled = false;
        _comfortFocus.Add((control, ring));
    }

    private void RefreshComfortWidgets()
    {
        var system = EventSystem.current;
        var selected = system != null ? system.currentSelectedGameObject : null;
        if (selected != null && selected.transform.IsChildOf(_device.transform))
        {
            var control = selected.GetComponent<Selectable>();
            // CanvasGroup changes can cover an active input without deselecting it.
            if (control != null && !CanActivateControl(control) && !system!.alreadySelecting)
            {
                system.SetSelectedGameObject(null);
                selected = system.currentSelectedGameObject;
            }
        }
        var changed = selected != _comfortSelected;
        _comfortSelected = selected;
        for (var index = _comfortFocus.Count - 1; index >= 0; index--)
        {
            var (control, ring) = _comfortFocus[index];
            if (control == null || ring == null) { _comfortFocus.RemoveAt(index); continue; }
            var focused = selected != null && control.gameObject == selected
                && CanActivateControl(control);
            ring.enabled = focused;
            if (!focused) continue;
            ring.effectColor = Mint;
            if (changed) RevealFocusedControl(control.transform.Cast<RectTransform>());
        }
        for (var index = _scrollAreas.Count - 1; index >= 0; index--)
        {
            var scroll = _scrollAreas[index].Scroll;
            if (scroll == null) { _scrollAreas.RemoveAt(index); continue; }
            if (!scroll.gameObject.activeInHierarchy) continue;
            var inertia = !_preferences.ReducedMotion;
            if (scroll.inertia != inertia)
            {
                scroll.inertia = inertia;
                if (!inertia) scroll.StopMovement();
            }
            var bar = scroll.verticalScrollbar;
            if (bar != null && scroll.viewport != null && scroll.viewport.rect.height > 0f)
                bar.size = Math.Max(bar.size, Math.Min(1f, 30f / scroll.viewport.rect.height));
        }
        if (_resetSection >= 0 && (Time.unscaledTime >= _resetExpires || ContentObscured
            || _page?.App != ClientTerminalApp.Settings)) ClearPreferenceReset();
    }

    private void RevealFocusedControl(RectTransform control)
    {
        foreach (var area in _scrollAreas)
        {
            var scroll = area.Scroll;
            if (scroll == null || !scroll.gameObject.activeInHierarchy || scroll.viewport == null
                || !control.IsChildOf(area.Content)) continue;
            var bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(scroll.viewport, control);
            var viewport = scroll.viewport.rect;
            var shift = bounds.size.y > viewport.height ? viewport.yMax - bounds.max.y
                : bounds.min.y < viewport.yMin ? viewport.yMin - bounds.min.y
                : bounds.max.y > viewport.yMax ? viewport.yMax - bounds.max.y : 0f;
            if (Math.Abs(shift) < 1f) continue;
            var position = area.Content.anchoredPosition;
            position.y = Mathf.Clamp(position.y + shift, 0f,
                Math.Max(0f, area.Content.rect.height - viewport.height));
            scroll.StopMovement();
            area.Content.anchoredPosition = position;
        }
    }

    private void StepPreferenceLevel(string key, float step, float minimum, float maximum)
    {
        if (_disposed || _preferencesUpdating) return;
        if (_pendingSliderKey.Length > 0 && _pendingSliderKey != key) CommitSlider();
        var current = _pendingSliderKey == key ? _pendingSliderValue
            : key == "brightness" ? _preferences.Brightness : _preferences.Volume;
        _pendingSliderKey = key;
        _pendingSliderValue = Math.Clamp(MathF.Round((current + step) * 20f) / 20f, minimum, maximum);
        RefreshDisplayLevels();
    }

    private void BuildDisplayCalibration(Transform parent)
    {
        var sample = RoundedPanel(parent, "DisplaySample", Raised, 32f, 820f, 980f, 206f);
        Text(sample.transform, "SampleTitle", "Readability preview", 32f, Ink, 24f, 16f, 932f, 44f, true);
        Text(sample.transform, "SampleBody", "Check names, directions and smaller details at your normal viewing distance.",
            25f, Ink, 24f, 68f, 932f, 70f);
        Text(sample.transform, "SampleStatus", "N  042°    DISTANCE  1.25 km    08:45", 22f, Mint,
            24f, 152f, 932f, 34f, role: TabletTextRole.Status);
        var values = new[] { .35f, .65f, 1f };
        for (var index = 0; index < values.Length; index++)
        {
            var value = values[index];
            Button(parent, "BrightnessPreset" + index, "Brightness " + Percent(value),
                32f + index * 328f, 1046f, 310f, 64f, () =>
                {
                    CommitSlider();
                    EditPreference("brightness", Number(value));
                });
        }
    }

    private void BuildPreferenceReset(Transform parent, int section, float y)
    {
        var name = PreferenceSectionName(section);
        var reset = Button(parent, "ResetPreferences", "Reset " + name, 32f, y, 648f, 62f,
            () => RequestPreferenceReset(section));
        var cancel = Button(parent, "CancelReset", "Keep settings", 700f, y, 312f, 62f, ClearPreferenceReset);
        cancel.Image.gameObject.SetActive(false);
        var hint = Text(parent, "ResetHint", ResetHint(section), 20f, Muted, 32f, y + 72f, 980f, 60f);
        _preferenceResets.Add(section, (reset, cancel, hint));
    }

    private void RequestPreferenceReset(int section)
    {
        if (_disposed || ContentObscured || _settingsSection != section) return;
        if (_resetSection != section || Time.unscaledTime >= _resetExpires)
        {
            ClearPreferenceReset();
            _resetSection = section;
            _resetExpires = Time.unscaledTime + 10f;
            var widgets = _preferenceResets[section];
            widgets.Reset.Label.text = "Confirm reset " + PreferenceSectionName(section);
            widgets.Cancel.Image.gameObject.SetActive(true);
            widgets.Hint.text = "Press Confirm within 10 seconds to restore this section's defaults.";
            return;
        }
        CommitSlider();
        ClearPreferenceReset();
        var defaults = section switch
        {
            0 => new[] { ("theme", "Field"), ("brightness", "1"), ("text-size", "Normal"), ("reduced-motion", "false") },
            1 => new[] { ("notifications", "true"), ("volume", "0.35"), ("tone", "Pulse"),
                ("expedition-alerts", "true"), ("tribe-alerts", "true"), ("direct-alerts", "true") },
            _ => new[] { ("hud", "Standard"), ("watch-key", "F3"), ("watch-theme", "Field"),
                ("watch-night", "false"), ("watch-private", "false") }
        };
        foreach (var (key, value) in defaults) EditPreference(key, value);
        _preferenceResets[section].Hint.text = "Defaults requested. The selected controls update as each preference is applied.";
    }

    private void ClearPreferenceReset()
    {
        _resetSection = -1;
        foreach (var pair in _preferenceResets)
        {
            pair.Value.Reset.Label.text = "Reset " + PreferenceSectionName(pair.Key);
            pair.Value.Cancel.Image.gameObject.SetActive(false);
            pair.Value.Hint.text = ResetHint(pair.Key);
        }
    }

    private static string PreferenceSectionName(int section) => section switch
    { 0 => "display", 1 => "sound", _ => "watch preferences" };

    private static string ResetHint(int section) => section switch
    {
        0 => "Restores theme, brightness, text size and motion. Other preferences stay as they are.",
        1 => "Restores alert volume, tone and channel alerts. Operating mode stays as it is.",
        _ => "Restores watch appearance, privacy, hotkey and HUD preferences. Timers and reminders are kept."
    };

    private void MarkPreferenceSelection(ButtonWidget widget, bool selected)
    {
        if (widget.SelectionBadge == null)
        {
            var size = widget.Image.rectTransform.sizeDelta;
            widget.SelectionBadge = Text(widget.Image.transform, "SelectedPreference", "SET", 14f, Screen,
                size.x - 48f, size.y - 18f, 42f, 18f, role: TabletTextRole.Status);
            widget.SelectionBadge.alignment = TextAlignmentOptions.Center;
        }
        widget.SelectionBadge.color = Screen;
        widget.SelectionBadge.gameObject.SetActive(selected);
    }
}
