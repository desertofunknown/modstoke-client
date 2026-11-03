// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace Modstoke.Client;

internal sealed partial class ClientTerminalView
{
    private float _pageReveal = 1f;
    private float _pageRevealAt;
    private float _nextButtonVisuals;
    private Image? _actionToast;
    private TextMeshProUGUI? _actionToastText;

    private void BuildActionFeedback()
    {
        _actionToast = RoundedPanel(_applicationLayer, "ActionNotice", Chrome, 208f, 714f, 1024f, 68f);
        var input = _actionToast.gameObject.AddComponent<CanvasGroup>();
        input.interactable = false;
        input.blocksRaycasts = false;
        _actionToastText = Text(_actionToast.transform, "Message", "", 22f, ChromeInk, 20f, 7f, 984f, 54f);
        _actionToastText.alignment = TextAlignmentOptions.MidlineLeft;
        _actionToastText.overflowMode = TextOverflowModes.Ellipsis;
        _actionToast.gameObject.SetActive(false);
    }

    internal void SetActionFeedback(string message)
    {
        if (_disposed || _actionToast == null || _actionToastText == null) return;
        _actionToastText.text = message;
        _actionToast.gameObject.SetActive(message.Length > 0 && !ContentObscured
            && FullscreenMode == ClientFullscreenMode.None);
    }

    private void RevealPage()
    {
        _pageRevealAt = Time.unscaledTime;
        _pageReveal = _preferences.ReducedMotion || DesktopCaptured ? 1f : .78f;
        RefreshDesktopInput();
    }

    private void UpdatePresentation()
    {
        UpdateShellNavigation();
        RefreshComfortWidgets();
        if (_pageReveal < 1f)
        {
            var progress = _preferences.ReducedMotion || DesktopCaptured
                || FullscreenMode != ClientFullscreenMode.None ? 1f
                : Mathf.Clamp01((Time.unscaledTime - _pageRevealAt) / .14f);
            _pageReveal = Mathf.Lerp(.78f, 1f, Mathf.SmoothStep(0f, 1f, progress));
            RefreshDesktopInput();
        }
        if (Time.unscaledTime < _nextButtonVisuals) return;
        _nextButtonVisuals = Time.unscaledTime + .1f;
        RefreshButtonVisuals();
    }
}
