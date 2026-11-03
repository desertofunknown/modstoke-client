// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Modstoke.Client;

internal sealed partial class ClientTerminalView
{
    private Color Bezel = new(0.055f, 0.077f, 0.069f, 1f);
    private Color Chrome = new(0.071f, 0.106f, 0.102f, 1f);
    private Color ChromeRaised = new(0.19f, 0.29f, 0.25f, 1f);
    private Color ChromeInk = new(0.95f, 0.98f, 0.96f, 1f);
    private Color ChromeMuted = new(0.68f, 0.78f, 0.73f, 1f);
    private Color ChromeMint = new(0.65f, 0.91f, 0.73f, 1f);
    private Color Screen = new(0.105f, 0.145f, 0.133f, 1f);
    private Color Surface = new(0.15f, 0.20f, 0.18f, 1f);
    private Color Raised = new(0.22f, 0.29f, 0.25f, 1f);
    private Color Rule = new(0.36f, 0.47f, 0.40f, 1f);
    private Color Ink = new(0.91f, 0.95f, 0.91f, 1f);
    private Color Muted = new(0.74f, 0.82f, 0.76f, 1f);
    private Color Mint = new(0.63f, 0.88f, 0.70f, 1f);
    private Color Amber = new(0.98f, 0.79f, 0.43f, 1f);
    private Color ReviewSurface = new(0.30f, 0.25f, 0.15f, 1f);
    private Color Dim = new(0.47f, 0.57f, 0.50f, 1f);

    private readonly ClientTabletTypography _typography = new();
    private readonly ClientTabletSurfaces _surfaces = new();
    private readonly System.Collections.Generic.Dictionary<TextMeshProUGUI, float> _textSizes = new();
    private readonly System.Collections.Generic.List<ButtonWidget> _buttons = new();
    private readonly System.Collections.Generic.HashSet<TextMeshProUGUI> _controlLabels = new();
    private readonly System.Collections.Generic.HashSet<TextMeshProUGUI> _shellReflowLabels = new();
    private readonly System.Collections.Generic.List<ScrollArea> _scrollAreas = new();
    private ClientTabletTextSize? _appliedTextSize;

    internal void ApplyTextSize(ClientTabletTextSize size)
    {
        RefreshShellTextLayout(size);
        foreach (var graphic in _ownedGraphics)
        {
            if (graphic is not TextMeshProUGUI text || text == null) continue;
            var added = _textSizes.TryAdd(text, text.fontSize);
            if (!added && _appliedTextSize == size) continue;
            var baseline = _textSizes[text];
            var control = _controlLabels.Contains(text);
            var reflowed = _shellReflowLabels.Contains(text);
            text.enableAutoSizing = !reflowed && (control || size == ClientTabletTextSize.Large);
            text.fontSizeMax = size == ClientTabletTextSize.Large ? baseline * 1.16f : baseline;
            text.fontSizeMin = reflowed ? text.fontSizeMax
                : control && size != ClientTabletTextSize.Large ? Math.Min(baseline, Math.Max(18f, baseline * .82f)) : baseline;
            text.fontSize = size == ClientTabletTextSize.Large ? text.fontSizeMax : baseline;
        }
        _appliedTextSize = size;
    }

    private void SetShellTextBaseline(TextMeshProUGUI text, float size)
    {
        _textSizes[text] = size;
        _shellReflowLabels.Add(text);
        text.enableAutoSizing = false;
        text.fontSize = _shellLargeText ? size * 1.16f : size;
        text.fontSizeMin = _shellLargeText ? size * 1.16f : size;
        text.fontSizeMax = text.fontSize;
    }

    private sealed class ScrollArea
    {
        internal ScrollArea(Image root, RectTransform content, ScrollRect scroll, float width)
        { Root = root; Content = content; Scroll = scroll; Width = width; }
        internal Image Root { get; }
        internal RectTransform Content { get; }
        internal ScrollRect Scroll { get; }
        internal float Width { get; set; }

        internal void Height(float height) => Content.sizeDelta = new Vector2(Width, Math.Max(1f, height));
        internal void Reset() { Scroll.StopMovement(); Content.anchoredPosition = Vector2.zero; }
    }

    private sealed class ButtonWidget
    {
        internal ButtonWidget(Image image, Button button, TextMeshProUGUI label)
        { Image = image; Button = button; Label = label; }
        internal Image Image { get; }
        internal Button Button { get; }
        internal TextMeshProUGUI Label { get; }
        internal bool? PaintedInteractable { get; set; }
        internal TextMeshProUGUI? SelectionBadge { get; set; }
    }

    private void RefreshButtonVisuals()
    {
        for (var index = _buttons.Count - 1; index >= 0; index--)
        {
            var widget = _buttons[index];
            if (widget.Button == null || widget.Label == null)
            {
                if (widget.Label is { } label) _controlLabels.Remove(label);
                _buttons.RemoveAt(index);
                continue;
            }
            if (!widget.Button.gameObject.activeInHierarchy) continue;
            var interactable = widget.Button.interactable;
            if (widget.PaintedInteractable == interactable) continue;
            // Renderer alpha leaves the palette color intact for theme changes.
            widget.Label.canvasRenderer.SetAlpha(interactable ? 1f : 0.62f);
            widget.PaintedInteractable = interactable;
        }
    }

    private static RectTransform Node(Transform parent, string name)
    {
        var go = new GameObject(name) { layer = parent.gameObject.layer };
        go.transform.SetParent(parent, false);
        return go.AddComponent<RectTransform>();
    }

    private Image Panel(Transform parent, string name, Color color, float x, float y, float width, float height)
    {
        var rect = Node(parent, name);
        Place(rect, x, y, width, height);
        var image = rect.gameObject.AddComponent<Image>();
        image.color = color;
        image.raycastTarget = false;
        _surfaces.Apply(image);
        _ownedGraphics.Add(image);
        return image;
    }

    private Image RoundedPanel(Transform parent, string name, Color color, float x, float y, float width, float height)
    {
        var image = Panel(parent, name, color, x, y, width, height);
        Round(image);
        return image;
    }

    private void Round(Image image) => _surfaces.Apply(image, rounded: true);

    private void DisposeWidgetResources()
    {
        _buttons.Clear();
        _controlLabels.Clear();
        _shellReflowLabels.Clear();
        _scrollAreas.Clear();
        _comfortFocus.Clear();
        _comfortSelected = null;
        _preferenceResets.Clear();
        _shellReturnStates.Clear();
        _shellRestore = _shellFocusRestore = null;
        _shellLauncherOpener = null;
        _textSizes.Clear();
        _surfaces.Dispose();
        _typography.Dispose();
    }

    private static void Place(RectTransform rect, float x, float y, float width, float height)
    {
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = new Vector2(x, -y);
        rect.sizeDelta = new Vector2(width, height);
    }

    private static void Stretch(RectTransform rect, float left, float top, float right, float bottom)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(left, bottom);
        rect.offsetMax = new Vector2(-right, -top);
    }

    private TextMeshProUGUI Text(Transform parent, string name, string value, float size, Color color,
        float x, float y, float width, float height, bool bold = false, TabletTextRole? role = null)
    {
        var rect = Node(parent, name);
        var text = rect.gameObject.AddComponent<TextMeshProUGUI>();
        _typography.Apply(text, role ?? (bold ? TabletTextRole.Heading : TabletTextRole.Body));
        text.text = value ?? "";
        text.richText = false;
        text.fontSize = size;
        text.color = color;
        text.alignment = TextAlignmentOptions.TopLeft;
        text.enableWordWrapping = true;
        text.overflowMode = TextOverflowModes.Overflow;
        text.margin = Vector4.zero;
        text.raycastTarget = false;
        _ownedGraphics.Add(text);
        Place(text.rectTransform, x, y, width, height);
        return text;
    }

    private static float TextHeight(TextMeshProUGUI text, float width, float minimum = 0f)
    {
        if (string.IsNullOrEmpty(text.text)) return minimum;
        return Math.Max(minimum, (float)Math.Ceiling(text.GetPreferredValues(text.text, width, float.PositiveInfinity).y) + 4f);
    }

    private bool CanActivateControl(Selectable control)
    {
        if (!IsAlive || !_device.activeInHierarchy || control == null || !control || !control.enabled || !control.gameObject.activeInHierarchy
            || !control.IsInteractable() || ShadeGestureOwnsInput) return false;
        return !_shadeCapture || !ShadeModePickerOpen || control.transform.IsChildOf(_shadeModePicker);
    }

    private ButtonWidget Button(Transform parent, string name, string label, float x, float y,
        float width, float height, Action action)
    {
        var image = RoundedPanel(parent, name, Raised, x, y, width, height);
        image.raycastTarget = true;
        var button = image.gameObject.AddComponent<Button>();
        button.targetGraphic = image;
        var colors = button.colors;
        colors.normalColor = new Color(0.92f, 0.92f, 0.92f, 1f);
        colors.highlightedColor = Color.white;
        colors.selectedColor = new Color(0.98f, 0.98f, 0.98f, 1f);
        colors.pressedColor = new Color(0.76f, 0.76f, 0.76f, 1f);
        colors.disabledColor = new Color(0.60f, 0.60f, 0.60f, 1f);
        colors.fadeDuration = 0.10f;
        button.colors = colors;
        button.onClick.AddListener((UnityAction)new Action(() =>
        {
            if (!CanActivateControl(button)) return;
            TouchFeedback?.Invoke();
            action();
        }));
        var text = Text(image.transform, "Label", label, 23f, Ink, 16f, 0f, width - 32f, height,
            role: TabletTextRole.Control);
        text.alignment = TextAlignmentOptions.MidlineLeft;
        text.overflowMode = TextOverflowModes.Ellipsis;
        var widget = new ButtonWidget(image, button, text);
        _buttons.Add(widget);
        _controlLabels.Add(text);
        TrackComfortFocus(button, image);
        return widget;
    }

    private ScrollArea Scroll(Transform parent, string name, float x, float y, float width, float height)
    {
        var root = Panel(parent, name, Surface, x, y, width, height);
        root.raycastTarget = true;
        var scroll = root.gameObject.AddComponent<ScrollRect>();
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.inertia = !_preferences.ReducedMotion;
        scroll.decelerationRate = .08f;
        scroll.scrollSensitivity = 32f;
        var viewport = Node(root.transform, "Viewport");
        Stretch(viewport, 0f, 0f, 14f, 0f);
        viewport.gameObject.AddComponent<RectMask2D>();
        var content = Node(viewport, "Content");
        Place(content, 0f, 0f, width - 14f, height);
        scroll.viewport = viewport;
        scroll.content = content;

        var track = Panel(root.transform, "ScrollTrack", Screen, 0f, 0f, 6f, height);
        track.rectTransform.anchorMin = new Vector2(1f, 0f);
        track.rectTransform.anchorMax = Vector2.one;
        track.rectTransform.offsetMin = new Vector2(-14f, 6f);
        track.rectTransform.offsetMax = new Vector2(0f, -6f);
        track.raycastTarget = true;
        var handle = Panel(track.transform, "Thumb", Rule, 0f, 0f, 6f, 24f);
        Stretch(handle.rectTransform, 0f, 0f, 0f, 0f);
        handle.raycastTarget = true;
        var bar = track.gameObject.AddComponent<Scrollbar>();
        bar.direction = Scrollbar.Direction.BottomToTop;
        bar.handleRect = handle.rectTransform;
        bar.targetGraphic = handle;
        scroll.verticalScrollbar = bar;
        scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
        var area = new ScrollArea(root, content, scroll, width - 14f);
        _scrollAreas.Add(area);
        return area;
    }

    private TMP_InputField Input(Transform parent, string value, int maximumLength)
    {
        var image = RoundedPanel(parent, "Input", Screen, 24f, 0f, FormWidth, 58f);
        image.raycastTarget = true;
        var input = image.gameObject.AddComponent<TMP_InputField>();
        input.targetGraphic = image;
        var colors = input.colors;
        colors.disabledColor = new Color(0.95f, 0.96f, 0.97f, 1f);
        input.colors = colors;
        input.richText = false;
        input.lineType = TMP_InputField.LineType.SingleLine;
        input.characterLimit = maximumLength;
        input.customCaretColor = true;
        input.caretColor = Mint;
        input.caretWidth = 2;
        input.selectionColor = new Color(0.48f, 0.72f, 0.55f, 0.35f);
        input.onFocusSelectAll = false;
        input.restoreOriginalTextOnEscape = false;
        var viewport = Node(image.transform, "TextViewport");
        Stretch(viewport, 12f, 6f, 12f, 6f);
        viewport.gameObject.AddComponent<RectMask2D>();
        input.textViewport = viewport;
        var text = Text(viewport, "Value", "", 26f, Ink, 0f, 0f, FormWidth - 24f, 46f);
        text.enableWordWrapping = false;
        text.alignment = TextAlignmentOptions.MidlineLeft;
        Stretch(text.rectTransform, 0f, 0f, 0f, 0f);
        input.textComponent = text;
        input.text = value;
        TrackComfortFocus(input, image);
        return input;
    }

    private Color AppColor(ClientTerminalApp app) => app switch
    {
        ClientTerminalApp.Navigation or ClientTerminalApp.Drone => new Color(0.08f, 0.43f, 0.52f, 1f),
        ClientTerminalApp.Messages => new Color(0.17f, 0.40f, 0.57f, 1f),
        ClientTerminalApp.Tribe => new Color(0.14f, 0.40f, 0.65f, 1f),
        ClientTerminalApp.Maps => new Color(0.09f, 0.43f, 0.34f, 1f),
        ClientTerminalApp.Services => new Color(0.57f, 0.32f, 0.12f, 1f),
        ClientTerminalApp.Companions => new Color(0.44f, 0.31f, 0.61f, 1f),
        ClientTerminalApp.Devices => new Color(0.36f, 0.43f, 0.35f, 1f),
        ClientTerminalApp.Settings => new Color(0.37f, 0.43f, 0.47f, 1f),
        ClientTerminalApp.Journal => new Color(.49f, .38f, .21f, 1f),
        ClientTerminalApp.Health => new Color(.55f, .24f, .27f, 1f),
        ClientTerminalApp.Watch => new Color(.23f, .40f, .38f, 1f),
        ClientTerminalApp.Games => new Color(.38f, .30f, .56f, 1f),
        ClientTerminalApp.Pack => new Color(.42f, .38f, .20f, 1f),
        ClientTerminalApp.Hangar => new Color(.30f, .43f, .55f, 1f),
        ClientTerminalApp.Spaceflight => new Color(.30f, .29f, .56f, 1f),
        _ => Ink
    };

    // Simple native geometry stays legible without a font glyph or external texture dependency.
    private void Icon(Transform parent, ClientTerminalApp app, Color color, float x, float y, float size)
    {
        var root = Node(parent, app + "Icon");
        Place(root, x, y, size, size);
        var scale = size / 64f;
        void Stroke(float ax, float ay, float bx, float by)
        {
            var dx = (bx - ax) * scale;
            var dy = (by - ay) * scale;
            var line = Panel(root, "Stroke", color, ax * scale, ay * scale,
                MathF.Sqrt(dx * dx + dy * dy), Math.Max(1.5f, 4f * scale));
            line.rectTransform.pivot = new Vector2(0f, 0.5f);
            line.rectTransform.localEulerAngles = new Vector3(0f, 0f, -MathF.Atan2(dy, dx) * Mathf.Rad2Deg);
        }
        void Ring(float cx, float cy, float radius)
        {
            for (var index = 0; index < 16; index++)
            {
                var first = index * MathF.PI / 8f;
                var second = (index + 1) * MathF.PI / 8f;
                Stroke(cx + MathF.Cos(first) * radius, cy + MathF.Sin(first) * radius,
                    cx + MathF.Cos(second) * radius, cy + MathF.Sin(second) * radius);
            }
        }
        switch (app)
        {
            case ClientTerminalApp.Spaceflight:
                Stroke(32, 5, 43, 23); Stroke(43, 23, 43, 43); Stroke(43, 43, 21, 43);
                Stroke(21, 43, 21, 23); Stroke(21, 23, 32, 5); Ring(32, 26, 5);
                Stroke(21, 32, 10, 48); Stroke(10, 48, 21, 43);
                Stroke(43, 32, 54, 48); Stroke(54, 48, 43, 43);
                Stroke(27, 49, 27, 56); Stroke(37, 49, 37, 56); Stroke(32, 49, 32, 61);
                break;
            case ClientTerminalApp.Hangar:
                Stroke(32, 5, 32, 57); Stroke(32, 19, 7, 35); Stroke(7, 35, 7, 41);
                Stroke(7, 41, 32, 32); Stroke(32, 19, 57, 35); Stroke(57, 35, 57, 41);
                Stroke(57, 41, 32, 32); Stroke(32, 46, 20, 55); Stroke(20, 55, 44, 55);
                Stroke(44, 55, 32, 46);
                break;
            case ClientTerminalApp.Pack:
                Stroke(23, 14, 23, 7); Stroke(23, 7, 41, 7); Stroke(41, 7, 41, 14);
                Stroke(14, 14, 50, 14); Stroke(50, 14, 55, 55); Stroke(55, 55, 9, 55); Stroke(9, 55, 14, 14);
                Stroke(15, 28, 49, 28); Stroke(21, 37, 43, 37); Stroke(43, 37, 43, 49);
                Stroke(43, 49, 21, 49); Stroke(21, 49, 21, 37);
                break;
            case ClientTerminalApp.Journal:
                Stroke(12, 8, 53, 8); Stroke(53, 8, 53, 56); Stroke(53, 56, 12, 56); Stroke(12, 56, 12, 8);
                Stroke(20, 8, 20, 56); Stroke(27, 23, 44, 23); Stroke(27, 34, 44, 34); Stroke(27, 45, 38, 45);
                break;
            case ClientTerminalApp.Health:
                Stroke(25, 8, 39, 8); Stroke(39, 8, 39, 25); Stroke(39, 25, 56, 25);
                Stroke(56, 25, 56, 39); Stroke(56, 39, 39, 39); Stroke(39, 39, 39, 56);
                Stroke(39, 56, 25, 56); Stroke(25, 56, 25, 39); Stroke(25, 39, 8, 39);
                Stroke(8, 39, 8, 25); Stroke(8, 25, 25, 25); Stroke(25, 25, 25, 8);
                break;
            case ClientTerminalApp.Watch:
                Ring(32, 32, 19); Stroke(22, 14, 22, 3); Stroke(22, 3, 42, 3); Stroke(42, 3, 42, 14);
                Stroke(22, 50, 22, 61); Stroke(22, 61, 42, 61); Stroke(42, 61, 42, 50);
                Stroke(32, 20, 32, 33); Stroke(32, 33, 42, 36);
                break;
            case ClientTerminalApp.Games:
                Stroke(15, 18, 49, 18); Stroke(49, 18, 59, 48); Stroke(59, 48, 45, 48);
                Stroke(45, 48, 39, 40); Stroke(39, 40, 25, 40); Stroke(25, 40, 19, 48);
                Stroke(19, 48, 5, 48); Stroke(5, 48, 15, 18);
                Stroke(15, 30, 29, 30); Stroke(22, 23, 22, 37); Ring(43, 28, 3); Ring(50, 36, 3);
                break;
            case ClientTerminalApp.Messages:
                Stroke(6f, 10f, 58f, 10f); Stroke(58f, 10f, 58f, 45f);
                Stroke(58f, 45f, 28f, 45f); Stroke(28f, 45f, 14f, 57f);
                Stroke(14f, 57f, 14f, 45f); Stroke(14f, 45f, 6f, 45f); Stroke(6f, 45f, 6f, 10f);
                Stroke(16f, 23f, 48f, 23f); Stroke(16f, 33f, 39f, 33f);
                break;
            case ClientTerminalApp.Drone:
                Ring(14f, 14f, 10f); Ring(50f, 14f, 10f);
                Ring(14f, 50f, 10f); Ring(50f, 50f, 10f);
                Stroke(14f, 14f, 50f, 50f); Stroke(50f, 14f, 14f, 50f);
                Ring(32f, 32f, 8f);
                break;
            case ClientTerminalApp.Navigation:
                Ring(32f, 32f, 26f);
                Stroke(23f, 42f, 32f, 16f); Stroke(32f, 16f, 41f, 42f);
                Stroke(41f, 42f, 32f, 36f); Stroke(32f, 36f, 23f, 42f);
                break;
            case ClientTerminalApp.Home:
                Stroke(5f, 30f, 32f, 7f); Stroke(32f, 7f, 59f, 30f);
                Stroke(13f, 25f, 13f, 56f); Stroke(13f, 56f, 51f, 56f); Stroke(51f, 56f, 51f, 25f);
                Stroke(26f, 56f, 26f, 37f); Stroke(26f, 37f, 38f, 37f); Stroke(38f, 37f, 38f, 56f);
                break;
            case ClientTerminalApp.Tribe:
                Ring(32f, 17f, 9f); Ring(10f, 26f, 6f); Ring(54f, 26f, 6f);
                Stroke(17f, 56f, 17f, 43f); Stroke(17f, 43f, 24f, 34f);
                Stroke(24f, 34f, 40f, 34f); Stroke(40f, 34f, 47f, 43f); Stroke(47f, 43f, 47f, 56f);
                Stroke(3f, 53f, 3f, 40f); Stroke(3f, 40f, 11f, 37f);
                Stroke(61f, 53f, 61f, 40f); Stroke(61f, 40f, 53f, 37f);
                break;
            case ClientTerminalApp.Maps:
                Stroke(5f, 17f, 23f, 8f); Stroke(23f, 8f, 41f, 17f); Stroke(41f, 17f, 59f, 8f);
                Stroke(5f, 17f, 5f, 55f); Stroke(23f, 8f, 23f, 46f);
                Stroke(41f, 17f, 41f, 55f); Stroke(59f, 8f, 59f, 46f);
                Stroke(5f, 55f, 23f, 46f); Stroke(23f, 46f, 41f, 55f); Stroke(41f, 55f, 59f, 46f);
                break;
            case ClientTerminalApp.Settings:
                Ring(32f, 32f, 19f); Ring(32f, 32f, 7f);
                for (var index = 0; index < 8; index++)
                {
                    var angle = index * MathF.PI / 4f;
                    Stroke(32f + MathF.Cos(angle) * 20f, 32f + MathF.Sin(angle) * 20f,
                        32f + MathF.Cos(angle) * 29f, 32f + MathF.Sin(angle) * 29f);
                }
                break;
            case ClientTerminalApp.Services:
                Stroke(8f, 14f, 56f, 14f); Stroke(56f, 14f, 56f, 54f);
                Stroke(56f, 54f, 8f, 54f); Stroke(8f, 54f, 8f, 14f);
                Stroke(22f, 14f, 22f, 6f); Stroke(22f, 6f, 42f, 6f); Stroke(42f, 6f, 42f, 14f);
                Stroke(8f, 30f, 56f, 30f); Stroke(27f, 27f, 37f, 27f); Stroke(32f, 27f, 32f, 37f);
                break;
            case ClientTerminalApp.Devices:
                Stroke(6f, 8f, 43f, 8f); Stroke(43f, 8f, 43f, 42f);
                Stroke(43f, 42f, 6f, 42f); Stroke(6f, 42f, 6f, 8f);
                Stroke(15f, 51f, 32f, 51f); Stroke(24f, 42f, 24f, 51f);
                Stroke(43f, 30f, 58f, 30f); Stroke(58f, 30f, 58f, 58f);
                Stroke(58f, 58f, 43f, 58f); Stroke(43f, 58f, 43f, 30f);
                break;
            case ClientTerminalApp.Companions:
                Ring(25f, 16f, 10f);
                Stroke(6f, 57f, 6f, 43f); Stroke(6f, 43f, 16f, 33f); Stroke(16f, 33f, 32f, 33f);
                Stroke(36f, 39f, 47f, 34f); Stroke(47f, 34f, 58f, 39f);
                Stroke(58f, 39f, 56f, 51f); Stroke(56f, 51f, 47f, 59f);
                Stroke(47f, 59f, 38f, 51f); Stroke(38f, 51f, 36f, 39f);
                break;
        }
    }

    private void DestroyChildren(RectTransform parent)
    {
        var system = EventSystem.current;
        var selected = system != null ? system.currentSelectedGameObject : null;
        if (selected != null && selected.transform.IsChildOf(parent) && !system!.alreadySelecting)
            system.SetSelectedGameObject(null);
        for (var index = _ownedGraphics.Count - 1; index >= 0; index--)
        {
            var graphic = _ownedGraphics[index];
            if (graphic == null || !graphic || graphic.transform.IsChildOf(parent))
            {
                if (graphic is TextMeshProUGUI text)
                {
                    _textSizes.Remove(text);
                    _shellReflowLabels.Remove(text);
                }
                _ownedGraphics.RemoveAt(index);
            }
        }
        for (var index = parent.childCount - 1; index >= 0; index--)
        {
            var child = parent.GetChild(index).gameObject;
            child.SetActive(false);
            UnityEngine.Object.Destroy(child);
        }
    }
}
