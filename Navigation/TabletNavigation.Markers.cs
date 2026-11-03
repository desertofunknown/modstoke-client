// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Modstoke.Client;

internal sealed partial class ClientTabletNavigation
{
    private const int MaximumMarkers = 512;
    private readonly List<MapMarker> _markers = new();
    private int _activeMarkers;

    private sealed class MapMarker
    {
        internal required RectTransform Root;
        internal required Image Background;
        internal required RawImage Outline;
        internal required RawImage Icon;
        internal required Image LabelBackground;
        internal required TextMeshProUGUI Label;
        internal RectTransform? NativeVisual;
        internal UnityEngine.UI.Text[] NativeLabels = Array.Empty<UnityEngine.UI.Text>();
        internal Vector2 Uv;
    }

    private void RefreshMarkers()
    {
        var used = 0;
        var locators = _system._gpsLocators;
        var visuals = _system._gpsLocatorVisuals;
        // Parallel lists are the native locator-to-visual association. Never guess through a rebuild.
        if (_showMarkers && locators != null && visuals != null && locators.Count == visuals.Count)
        {
            var count = Math.Min(locators.Count, MaximumMarkers);
            for (var i = 0; i < count; i++)
            {
                var locator = locators[i];
                var visual = visuals[i];
                if (locator == null || visual == null || !locator.IsActive || !visual.gameObject.activeInHierarchy) continue;
                var iconData = locator.IconData;
                if (iconData == null || iconData._icon == null || !TryWorldUv(locator.Position(), out var uv) || !InCoverage(uv)) continue;
                if (used == _markers.Count) _markers.Add(CreateMarker());
                var marker = _markers[used++];
                marker.Uv = uv;
                marker.Icon.texture = iconData._icon;
                marker.Icon.color = iconData._iconColor;
                marker.Outline.texture = iconData._iconOutline;
                marker.Outline.color = iconData._iconOutlineColor;
                marker.Outline.gameObject.SetActive(iconData._iconOutline != null);
                // Icon-only points retain their transparent silhouette; pins retain their native colour.
                marker.Background.gameObject.SetActive((int)locator.UIStyle != 2);
                marker.Background.color = iconData._backgroundColor;
                var iconScale = float.IsFinite(locator.IconScale) ? Mathf.Clamp(locator.IconScale, .7f, 1.4f) : 1f;
                marker.Icon.rectTransform.sizeDelta = new Vector2(30f, 30f) * iconScale;
                marker.Outline.rectTransform.sizeDelta = new Vector2(34f, 34f) * iconScale;
                if (!Same(marker.NativeVisual, visual))
                {
                    marker.NativeVisual = visual;
                    var texts = visual.GetComponentsInChildren<UnityEngine.UI.Text>(true);
                    marker.NativeLabels = new UnityEngine.UI.Text[Math.Min(texts.Length, 16)];
                    for (var t = 0; t < marker.NativeLabels.Length; t++) marker.NativeLabels[t] = texts[t];
                }
                var label = "";
                for (var t = 0; t < marker.NativeLabels.Length; t++)
                {
                    var text = marker.NativeLabels[t];
                    if (text == null || !text.enabled || !text.gameObject.activeInHierarchy || text.color.a <= .01f
                        || string.IsNullOrWhiteSpace(text.text)) continue;
                    label = text.text;
                    break;
                }
                SetText(marker.Label, label);
                marker.LabelBackground.gameObject.SetActive(label.Length > 0);
            }
        }
        for (var i = used; i < _activeMarkers; i++) _markers[i].Root.gameObject.SetActive(false);
        _activeMarkers = used;
    }

    private void PositionMarkers()
    {
        for (var i = 0; i < _activeMarkers; i++)
        {
            var marker = _markers[i];
            var point = UvToScreen(marker.Uv);
            var visible = _showMarkers && InView(point, 25f);
            marker.Root.gameObject.SetActive(visible);
            if (visible) marker.Root.anchoredPosition = point;
        }
    }

    private void HideMarkers()
    {
        for (var i = 0; i < _activeMarkers; i++) _markers[i].Root.gameObject.SetActive(false);
        _activeMarkers = 0;
    }

    private MapMarker CreateMarker()
    {
        var root = Node(_markerLayer!, "Locator");
        root.sizeDelta = new Vector2(48f, 48f);
        var background = Image(root, "Pin", Color.white, Vector2.zero, new Vector2(44f, 44f));
        background.sprite = _circleSprite;
        var outline = MarkerImage(root, "IconOutline", 34f);
        var icon = MarkerImage(root, "Icon", 30f);
        var labelBackground = Image(root, "LabelSurface", MapChrome, new Vector2(0f, -40f), new Vector2(156f, 30f));
        var label = Label(labelBackground.transform, "NativeLabel", "", 19f, MapInk, Vector2.zero, new Vector2(146f, 28f));
        return new MapMarker
        {
            Root = root, Background = background, Outline = outline, Icon = icon,
            LabelBackground = labelBackground, Label = label
        };
    }

    private static RawImage MarkerImage(Transform parent, string name, float side)
    {
        var rect = Node(parent, name);
        rect.sizeDelta = new Vector2(side, side);
        var image = rect.gameObject.AddComponent<RawImage>();
        image.raycastTarget = false;
        return image;
    }
}
