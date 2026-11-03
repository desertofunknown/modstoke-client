// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Modstoke.Client;

internal sealed partial class ClientTabletNavigation
{
    private static readonly Color MapChrome = new(.035f, .075f, .09f, .96f);
    private static readonly Color MapInk = new(.92f, .97f, .96f, 1f);
    private static readonly Color MapAccent = new(.49f, .94f, .82f, 1f);
    private static readonly Color MapMuted = new(.65f, .77f, .78f, 1f);
    private ClientTabletTypography? _typography;
    private ClientTabletSurfaces? _surfaces;
    private Texture2D? _circleTexture;
    private Texture2D? _arrowTexture;
    private Sprite? _circleSprite;
    private Sprite? _arrowSprite;
    private Image? _terrain;
    private RectTransform? _markerLayer;
    private RectTransform? _player;
    private RectTransform? _playerHeading;
    private RectTransform? _northArrow;
    private RectTransform? _compass;
    private RectTransform? _footer;
    private RectTransform? _unavailable;
    private RectTransform? _scaleGroup;
    private RectTransform? _scaleBar;
    private RectTransform? _scaleEnd;
    private TextMeshProUGUI? _unavailableTitle;
    private TextMeshProUGUI? _unavailableDetail;
    private TextMeshProUGUI? _positionText;
    private TextMeshProUGUI? _headingText;
    private TextMeshProUGUI? _scaleText;
    private TextMeshProUGUI? _followLabel;
    private TextMeshProUGUI? _orientationLabel;
    private TextMeshProUGUI? _markersLabel;

    private void BuildUi()
    {
        _typography = new ClientTabletTypography();
        _surfaces = new ClientTabletSurfaces();
        MakeSymbols();
        _root = Node(_viewport!, "LandscapeNavigation");
        Fill(_root);
        _root.gameObject.AddComponent<RectMask2D>();
        var background = _root.gameObject.AddComponent<Image>();
        background.color = MapChrome;
        background.raycastTarget = true;
        _terrain = Image(_root, "Terrain", Color.white, Vector2.zero, Vector2.one);
        _terrain.sprite = _source!.sprite;
        _terrain.type = UnityEngine.UI.Image.Type.Simple;
        _terrain.preserveAspect = false;
        _markerLayer = Node(_root, "AuthorizedLocators");
        Fill(_markerLayer);
        _fieldLayer = Node(_root, "LocalFieldOverlays");
        Fill(_fieldLayer);
        _fieldLines = Node(_fieldLayer, "TrailAndRouteLines");
        Fill(_fieldLines);

        _player = Node(_root, "YourPosition");
        _player.sizeDelta = new Vector2(62f, 62f);
        var halo = Image(_player, "Halo", new Color(.1f, .9f, .75f, .2f), Vector2.zero, new Vector2(70f, 70f));
        halo.sprite = _circleSprite;
        var disc = Image(_player, "Disc", MapChrome, Vector2.zero, new Vector2(48f, 48f));
        disc.sprite = _circleSprite;
        var arrow = Image(_player, "Heading", MapAccent, new Vector2(0f, 1f), new Vector2(34f, 42f));
        arrow.sprite = _arrowSprite;
        _playerHeading = arrow.rectTransform;

        _unavailable = Node(_root, "NavigationUnavailable");
        Fill(_unavailable);
        var unavailableBackground = _unavailable.gameObject.AddComponent<Image>();
        unavailableBackground.color = MapChrome;
        unavailableBackground.raycastTarget = false;
        _unavailableTitle = Label(_unavailable, "Title", "", 34f, MapInk, new Vector2(0f, 32f), new Vector2(900f, 54f), true);
        _unavailableDetail = Label(_unavailable, "Detail", "", 25f, MapMuted, new Vector2(0f, -40f), new Vector2(1000f, 90f));
        _unavailableDetail.enableWordWrapping = true;

        _followLabel = Control("Follow", "FOLLOWING", 24f, 180f, Recenter);
        _orientationLabel = Control("Orientation", "NORTH UP", 220f, 196f, ToggleHeading);
        _markersLabel = Control("Markers", "MARKERS ON", 432f, 188f, ToggleMarkers);
        _compass = Node(_root, "NorthIndicator");
        _compass.sizeDelta = new Vector2(72f, 72f);
        var compassDisc = Image(_compass, "Disc", MapChrome, Vector2.zero, new Vector2(72f, 72f));
        compassDisc.sprite = _circleSprite;
        _northArrow = Image(_compass, "NorthArrow", MapAccent, new Vector2(0f, 8f), new Vector2(22f, 30f)).rectTransform;
        _northArrow.GetComponent<Image>().sprite = _arrowSprite;
        Label(_compass, "North", "N", 19f, MapInk, new Vector2(0f, -23f), new Vector2(40f, 24f), true);

        _footer = Node(_root, "NavigationStatus");
        var footerImage = _footer.gameObject.AddComponent<Image>();
        footerImage.color = MapChrome;
        footerImage.raycastTarget = false;
        _positionText = Label(_footer, "Position", "", 23f, MapInk, Vector2.zero, new Vector2(590f, 38f), role: TabletTextRole.Status);
        _positionText.alignment = TextAlignmentOptions.MidlineLeft;
        _headingText = Label(_footer, "Heading", "", 23f, MapAccent, Vector2.zero, new Vector2(340f, 38f), role: TabletTextRole.Status);
        _headingText.alignment = TextAlignmentOptions.MidlineLeft;
        _scaleGroup = Node(_footer, "MapScale");
        _scaleGroup.sizeDelta = new Vector2(200f, 54f);
        _scaleText = Label(_scaleGroup, "Distance", "", 21f, MapInk, new Vector2(0f, 10f), new Vector2(200f, 25f), role: TabletTextRole.Status);
        _scaleBar = Image(_scaleGroup, "Bar", MapInk, new Vector2(0f, -13f), new Vector2(150f, 3f)).rectTransform;
        _scaleBar.pivot = new Vector2(0f, .5f);
        _scaleBar.anchoredPosition = new Vector2(-75f, -13f);
        Image(_scaleGroup, "Start", MapInk, new Vector2(-75f, -9f), new Vector2(3f, 11f));
        _scaleEnd = Image(_scaleGroup, "End", MapInk, new Vector2(75f, -9f), new Vector2(3f, 11f)).rectTransform;
        BuildFieldUi();
    }

    private void LayoutChrome()
    {
        _compass!.anchoredPosition = new Vector2(_size.x * .5f - 56f, _size.y * .5f - 132f);
        _footer!.sizeDelta = new Vector2(_size.x, 64f);
        _footer.anchoredPosition = new Vector2(0f, -_size.y * .5f + 32f);
        _positionText!.rectTransform.anchoredPosition = new Vector2(-_size.x * .5f + 24f + 295f, 0f);
        _headingText!.rectTransform.anchoredPosition = new Vector2(-_size.x * .5f + 630f + 170f, 0f);
        _scaleGroup!.anchoredPosition = new Vector2(_size.x * .5f - 124f, 0f);
        LayoutFieldUi();
    }

    private void UpdateControlLabels()
    {
        SetText(_followLabel!, _following ? "FOLLOWING" : "RECENTER");
        _followLabel!.color = _following ? MapAccent : MapInk;
        SetText(_orientationLabel!, _headingUp ? (_headingAvailable ? "HEADING UP" : "NORTH (NO HDG)") : "NORTH UP");
        SetText(_markersLabel!, _showMarkers ? "MARKERS ON" : "MARKERS OFF");
        _markersLabel!.color = _showMarkers ? MapAccent : MapMuted;
    }

    private void RefreshStatus()
    {
        var culture = CultureInfo.InvariantCulture;
        _positionText!.text = "X " + _position.x.ToString("0", culture) + "   Z " + _position.z.ToString("0", culture)
            + "   WORLD Y " + _position.y.ToString("0", culture);
        _headingText!.text = (_headingAvailable ? NotebookBearing(_heading) + "° " + Cardinal(_heading) : "NO HEADING")
            + (_following ? "  ·  LIVE" : "  ·  PANNING");
        if (!InCoverage(_playerUv)) _headingText.text = "OUTSIDE MAP COVERAGE";
        var worldPerPixel = Vector2.Scale(ScreenToUvDelta(new Vector2(1f, 0f)), _worldSpan).magnitude;
        var desired = worldPerPixel * 150f;
        var decade = Mathf.Pow(10f, Mathf.Floor(Mathf.Log10(desired)));
        var fraction = desired / decade;
        var distance = (fraction >= 5f ? 5f : fraction >= 2f ? 2f : 1f) * decade;
        var width = distance / worldPerPixel;
        _scaleBar!.sizeDelta = new Vector2(width, 3f);
        _scaleEnd!.anchoredPosition = new Vector2(-75f + width, -9f);
        _scaleText!.rectTransform.anchoredPosition = new Vector2(-75f + width * .5f, 10f);
        _scaleText.text = distance >= 1000f ? (distance / 1000f).ToString("0.#", culture) + " km"
            : distance.ToString("0.#", culture) + " m";
    }

    private static string Cardinal(float heading) => ((int)Mathf.Floor((heading + 22.5f) / 45f) % 8) switch
    { 0 => "N", 1 => "NE", 2 => "E", 3 => "SE", 4 => "S", 5 => "SW", 6 => "W", _ => "NW" };

    private TextMeshProUGUI Control(string name, string caption, float left, float width, Action action)
    {
        var image = Image(_root!, name, MapChrome, Vector2.zero, new Vector2(width, 58f));
        var rect = image.rectTransform;
        rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = new Vector2(left, -24f);
        image.raycastTarget = true;
        var button = image.gameObject.AddComponent<Button>();
        button.targetGraphic = image;
        var colors = button.colors;
        colors.normalColor = Color.white;
        colors.highlightedColor = new Color(.72f, .94f, .88f, 1f);
        colors.pressedColor = new Color(.5f, .78f, .7f, 1f);
        colors.selectedColor = Color.white;
        colors.fadeDuration = .08f;
        button.colors = colors;
        button.onClick.AddListener((UnityAction)new Action(action));
        _surfaces!.Apply(image, rounded: true);
        var label = Label(rect, "Label", caption, 20f, MapInk, Vector2.zero, new Vector2(width - 16f, 58f), role: TabletTextRole.Control);
        return label;
    }

    private static RectTransform Node(Transform parent, string name)
    {
        var go = new GameObject(name) { layer = parent.gameObject.layer };
        go.transform.SetParent(parent, false);
        var rect = go.AddComponent<RectTransform>();
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(.5f, .5f);
        return rect;
    }

    private static void Fill(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = rect.offsetMax = Vector2.zero;
    }

    private Image Image(Transform parent, string name, Color color, Vector2 position, Vector2 size)
    {
        var rect = Node(parent, name);
        rect.sizeDelta = size;
        rect.anchoredPosition = position;
        var image = rect.gameObject.AddComponent<Image>();
        image.color = color;
        image.raycastTarget = false;
        _surfaces!.Apply(image);
        return image;
    }

    private TextMeshProUGUI Label(Transform parent, string name, string value, float size, Color color,
        Vector2 position, Vector2 bounds, bool bold = false, TabletTextRole? role = null)
    {
        var rect = Node(parent, name);
        rect.anchoredPosition = position;
        rect.sizeDelta = bounds;
        var text = rect.gameObject.AddComponent<TextMeshProUGUI>();
        _typography!.Apply(text, role ?? (bold ? TabletTextRole.Heading : TabletTextRole.Body));
        text.text = value;
        text.fontSize = size;
        text.color = color;
        text.richText = false;
        text.enableWordWrapping = false;
        text.overflowMode = TextOverflowModes.Ellipsis;
        text.alignment = TextAlignmentOptions.Center;
        text.margin = Vector4.zero;
        text.raycastTarget = false;
        return text;
    }

    private static void SetText(TextMeshProUGUI text, string value)
    {
        if (text.text != value) text.text = value;
    }

    private void MakeSymbols()
    {
        const int side = 64;
        _circleTexture = new Texture2D(side, side, TextureFormat.RGBA32, true);
        _arrowTexture = new Texture2D(side, side, TextureFormat.RGBA32, true);
        _circleTexture.name = "Tablet GPS disc";
        _arrowTexture.name = "Tablet GPS heading";
        _circleTexture.wrapMode = _arrowTexture.wrapMode = TextureWrapMode.Clamp;
        _circleTexture.filterMode = _arrowTexture.filterMode = FilterMode.Trilinear;
        for (var y = 0; y < side; y++)
        for (var x = 0; x < side; x++)
        {
            var dx = x + .5f - side * .5f;
            var dy = y + .5f - side * .5f;
            _circleTexture.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(31.5f - Mathf.Sqrt(dx * dx + dy * dy))));
            var py = (y + .5f) / side;
            var px = Mathf.Abs((x + .5f) / side - .5f);
            var outer = (.94f - py) * .44f - px;
            var notch = py - (.31f - px * .5f);
            var alpha = Mathf.Clamp01(Mathf.Min(outer, notch) * side + .5f);
            _arrowTexture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
        }
        _circleTexture.Apply(true, true);
        _arrowTexture.Apply(true, true);
        _circleSprite = Sprite.Create(_circleTexture, new Rect(0f, 0f, side, side), new Vector2(.5f, .5f));
        _arrowSprite = Sprite.Create(_arrowTexture, new Rect(0f, 0f, side, side), new Vector2(.5f, .5f));
    }

    private void DisposeUiResources()
    {
        if (_circleSprite != null) UnityEngine.Object.Destroy(_circleSprite);
        if (_arrowSprite != null) UnityEngine.Object.Destroy(_arrowSprite);
        if (_circleTexture != null) UnityEngine.Object.Destroy(_circleTexture);
        if (_arrowTexture != null) UnityEngine.Object.Destroy(_arrowTexture);
        _circleSprite = null;
        _arrowSprite = null;
        _circleTexture = null;
        _arrowTexture = null;
        _typography?.Dispose();
        _typography = null;
        _surfaces?.Dispose();
        _surfaces = null;
    }
}
