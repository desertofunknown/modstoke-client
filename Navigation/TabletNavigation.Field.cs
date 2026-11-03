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

internal sealed partial class ClientTabletNavigation
{
    // This state belongs to the held tablet session, never to native GPS or the persisted profile.
    internal sealed class ViewState
    {
        internal Vector2 Center = new(.5f, .5f);
        internal float Zoom = 1.6f;
        internal bool Following = true;
        internal bool HeadingUp;
        internal bool Markers = true;
    }

    private sealed record FieldPin(RectTransform Root, Image Disc, TextMeshProUGUI Label);
    private readonly List<FieldPin> _fieldPins = new(128);
    private readonly List<RectTransform> _trailLines = new(511);
    private readonly List<RectTransform> _routeLines = new(127);
    private ClientFieldProfile _field = new();
    private Action<ClientFieldAction>? _fieldChanged;
    private Action<ClientFieldPlace>? _sharePlace;
    private ViewState? _viewState;
    private Guid? _selectedPlace;
    private Guid? _pendingFieldFocus;
    private Guid? _selectedRoute;
    private Guid? _namePlace;
    private int _routeStep = -1;
    private bool _fieldPanelOpen;
    private bool _fieldRoutesOpen;
    private bool _longPressHandled;
    private float _pressTime;
    private Vector2 _pressUv;
    private Vector2 _fieldOriginUv;
    private Vector2 _fieldEastUv;
    private Vector2 _fieldNorthUv;
    private RectTransform? _fieldLayer;
    private RectTransform? _fieldLines;
    private RectTransform? _fieldPanel;
    private RectTransform? _fieldPinPane;
    private RectTransform? _fieldRoutePane;
    private TextMeshProUGUI? _pinsTab;
    private TextMeshProUGUI? _routesTab;
    private TextMeshProUGUI? _fieldHint;
    private TextMeshProUGUI? _placeDetail;
    private TextMeshProUGUI? _routeDetail;
    private TextMeshProUGUI? _routePinDetail;
    private TextMeshProUGUI? _trailLabel;
    private TextMeshProUGUI? _guidance;
    private TMP_InputField? _placeName;

    internal void RestoreView(ViewState state)
    {
        _viewState = state;
        _center = state.Center;
        _zoom = _targetZoom = state.Zoom;
        _following = state.Following;
        _headingUp = state.HeadingUp;
        _showMarkers = state.Markers;
    }

    private void SaveView()
    {
        if (_viewState == null) return;
        _viewState.Center = _center;
        _viewState.Zoom = _targetZoom;
        _viewState.Following = _following;
        _viewState.HeadingUp = _headingUp;
        _viewState.Markers = _showMarkers;
    }

    internal void SetFieldNavigation(ClientFieldProfile profile, Action<ClientFieldAction>? changed,
        Action<ClientFieldPlace>? share)
    {
        var previousPlace = FindPlace(_selectedPlace);
        var previousRoute = SelectedRoute();
        if (!ReferenceEquals(_field.Places, profile.Places) || !ReferenceEquals(_field.Routes, profile.Routes))
            _notebookResultsDirty = true;
        _field = profile;
        _fieldChanged = changed;
        _sharePlace = share;
        // Preserve newly submitted IDs while their action is awaiting the next snapshot, but never
        // retain a selection that disappeared from an acknowledged profile.
        if (previousPlace != null && FindPlace(_selectedPlace) == null) _selectedPlace = null;
        var route = SelectedRoute();
        if (previousRoute != null && route == null) _selectedRoute = null;
        if (_routeStep >= 0 && (route == null || previousRoute == null || _routeStep >= route.Places.Count
            || _routeStep >= previousRoute.Places.Count || route.Places[_routeStep] != previousRoute.Places[_routeStep]))
            _routeStep = -1;
        if (_fieldPanel != null) RefreshFieldStatus();
    }

    internal void ResetFieldSelection()
    {
        ClearFieldConfirmation();
        _pendingFieldFocus = null;
        _selectedPlace = _selectedRoute = _namePlace = null;
        _routeStep = -1;
        _notebookQuery = "";
        _notebookPage = 0;
        _notebookResultsDirty = true;
        _nameRoute = null;
        _routeName?.SetTextWithoutNotify("");
        _fieldPanelOpen = false;
        ShowFieldRoutes(false);
        _dragging = false;
        _longPressHandled = false;
        _placeName?.DeactivateInputField();
        if (_placeName != null) _placeName.text = "";
        if (_fieldPanel != null) _fieldPanel.gameObject.SetActive(false);
        RefreshFieldStatus();
    }

    internal void FocusFieldDestination(Guid id) => _pendingFieldFocus = id;

    private void ApplyPendingFieldFocus()
    {
        // Only acknowledged places can be selected. Wait for the visible map's live projection
        // before consuming the handoff, including when the map has not been attached yet.
        if (_pendingFieldFocus is not { } id || !_navigable || !_visible || _root == null
            || !_root.gameObject.activeInHierarchy || _fieldPanel == null
            || FindPlace(id) is not { } place || !TryFieldUv(place.Position, out _)) return;
        _selectedPlace = place.Id;
        _fieldPanelOpen = true;
        _fieldPanel.gameObject.SetActive(true);
        ShowFieldRoutes(false);
        CyclePlace(0);
        _pendingFieldFocus = null;
    }

    private ClientFieldPlace? FindPlace(Guid? id)
    {
        if (!id.HasValue) return null;
        foreach (var place in _field.Places) if (place.Id == id.Value) return place;
        return null;
    }

    private ClientFieldRoute? SelectedRoute()
    {
        foreach (var route in _field.Routes) if (route.Id == _selectedRoute) return route;
        return null;
    }

    private void BuildFieldUi()
    {
        Control("Field", "FIELD PINS", 636f, 176f, () =>
        {
            _fieldPanelOpen = !_fieldPanelOpen;
            if (!_fieldPanelOpen)
            {
                _placeName!.DeactivateInputField();
                DeactivateNotebookInputs();
                ClearFieldConfirmation();
            }
            _fieldPanel!.gameObject.SetActive(_fieldPanelOpen);
            RefreshFieldStatus();
        });
        var guidance = Image(_root!, "DestinationGuidance", MapChrome, Vector2.zero, new Vector2(740f, 48f));
        guidance.rectTransform.anchorMin = guidance.rectTransform.anchorMax = new Vector2(.5f, 0f);
        guidance.rectTransform.anchoredPosition = new Vector2(0f, 92f);
        _guidance = Label(guidance.transform, "Guidance", "", 22f, MapAccent, Vector2.zero, new Vector2(716f, 44f));
        BuildDestinationEdge();
        _fieldPanel = Image(_root!, "FieldNotebook", MapChrome, Vector2.zero, new Vector2(372f, 592f)).rectTransform;
        _fieldPanel.GetComponent<Image>().raycastTarget = true;
        _fieldPanel.anchorMin = _fieldPanel.anchorMax = _fieldPanel.pivot = new Vector2(0f, 1f);
        _fieldPanel.anchoredPosition = new Vector2(24f, -104f);
        FieldLabel("Title", "FIELD NOTEBOOK", 12f, 22f, MapAccent);
        _fieldHint = FieldLabel("Hint", "Hold the map to drop a pin.", 38f, 16f, MapMuted);
        _fieldHint.rectTransform.sizeDelta = new Vector2(348f, 34f);
        _fieldHint.enableWordWrapping = true;
        _pinsTab = FieldButton("PinsTab", "PINS", 12f, 76f, 78f, () => ShowFieldRoutes(false));
        _routesTab = FieldButton("RoutesTab", "ROUTES", 98f, 76f, 86f, () => ShowFieldRoutes(true));
        FieldButton("FindPins", "FIND", 192f, 76f, 78f, () => OpenNotebookBrowser(NotebookBrowser.Pins));
        FieldButton("Close", "CLOSE", 278f, 76f, 82f, () =>
        {
            _fieldPanelOpen = false;
            ClearFieldConfirmation();
            _placeName!.DeactivateInputField();
            DeactivateNotebookInputs();
            _fieldPanel.gameObject.SetActive(false);
        });
        _fieldPinPane = Node(_fieldPanel, "Pins");
        Fill(_fieldPinPane);
        _fieldRoutePane = Node(_fieldPanel, "Routes");
        Fill(_fieldRoutePane);
        var nameImage = Image(_fieldPinPane, "PinName", new Color(.08f, .14f, .16f, 1f), Vector2.zero, new Vector2(348f, 44f));
        FieldPosition(nameImage.rectTransform, 12f, 122f);
        nameImage.raycastTarget = true;
        _placeName = nameImage.gameObject.AddComponent<TMP_InputField>();
        _placeName.targetGraphic = nameImage;
        _placeName.richText = false;
        _placeName.lineType = TMP_InputField.LineType.SingleLine;
        _placeName.characterLimit = 64;
        _placeName.customCaretColor = true;
        _placeName.caretColor = MapAccent;
        var viewport = Node(nameImage.transform, "TextViewport");
        Fill(viewport);
        viewport.offsetMin = new Vector2(8f, 3f);
        viewport.offsetMax = new Vector2(-8f, -3f);
        viewport.gameObject.AddComponent<RectMask2D>();
        var nameText = Label(viewport, "Name", "", 21f, MapInk, Vector2.zero, new Vector2(332f, 34f));
        Fill(nameText.rectTransform);
        nameText.alignment = TextAlignmentOptions.MidlineLeft;
        _placeName.textViewport = viewport;
        _placeName.textComponent = nameText;
        _placeDetail = FieldLabel("PinDetail", "No pin selected", 170f, 18f, MapMuted, _fieldPinPane);
        _placeDetail.rectTransform.sizeDelta = new Vector2(348f, 62f);
        _placeDetail.fontSize = 16f;
        FieldButton("Navigate", "NAVIGATE", 12f, 236f, 348f, () =>
        {
            if (FindPlace(_selectedPlace) is { } place)
            {
                _routeStep = -1;
                Destination(place.Id);
            }
        }, _fieldPinPane);
        FieldButton("UseOnWatch", "Use on watch", 12f, 286f, 348f, () =>
        {
            if (FindPlace(_selectedPlace) is { } place)
                _fieldChanged?.Invoke(new("watch-navigation", Target: place.Id.ToString("N")));
        }, _fieldPinPane);
        FieldButton("ScoutWithDrone", "Scout with drone", 12f, 336f, 348f, () =>
        {
            if (FindPlace(_selectedPlace) is { } place)
                _fieldChanged?.Invoke(new("scout-destination", Target: place.Id.ToString("N")));
        }, _fieldPinPane);
        FieldButton("PinNotes", "NOTES", 12f, 386f, 168f, () =>
        {
            if (FindPlace(_selectedPlace) is { } place)
                _fieldChanged?.Invoke(new("place-notes", Target: place.Id.ToString("N")));
        }, _fieldPinPane);
        FieldButton("Share", "SHARE…", 192f, 386f, 168f, () =>
        {
            if (FindPlace(_selectedPlace) is { } place) _sharePlace?.Invoke(place);
        }, _fieldPinPane);
        FieldButton("Rename", "SAVE NAME", 12f, 436f, 168f, RenamePlace, _fieldPinPane);
        FieldButton("Delete", "DELETE PIN", 192f, 436f, 168f, () =>
        {
            if (_selectedPlace is { } id) ConfirmFieldRemoval("pin:" + id, "Delete pin? Keep notes unlinked; remove route steps.", new("remove-place", Target: id.ToString()));
        }, _fieldPinPane);
        FieldButton("Previous", "< PREVIOUS PIN", 12f, 486f, 168f, () => CyclePlace(-1), _fieldPinPane);
        FieldButton("Next", "NEXT PIN >", 192f, 486f, 168f, () => CyclePlace(1), _fieldPinPane);
        FieldButton("PinHere", "PIN MY LOCATION", 12f, 536f, 168f, PinCurrentLocation, _fieldPinPane);
        FieldButton("NearestPin", "NEAREST PIN", 192f, 536f, 168f, SelectNearestPin, _fieldPinPane);
        _routeName = NotebookInput(_fieldRoutePane, "RouteName", "Route name", 12f, 122f, 238f, 64);
        FieldButton("RouteRename", "SAVE", 260f, 122f, 100f, RenameRoute, _fieldRoutePane);
        _routePinDetail = FieldLabel("RoutePin", "Choose a pin in Pins", 186f, 16f, MapMuted, _fieldRoutePane);
        _routeDetail = FieldLabel("RouteDetail", "No route selected", 162f, 17f, MapAccent, _fieldRoutePane);
        _routePinDetail.rectTransform.sizeDelta = _routeDetail.rectTransform.sizeDelta = new Vector2(348f, 24f);
        FieldButton("RouteChoose", "CHOOSE ROUTE", 12f, 216f, 168f, () => OpenNotebookBrowser(NotebookBrowser.Routes), _fieldRoutePane);
        FieldButton("RouteNew", "NEW ROUTE", 192f, 216f, 168f, NewRoute, _fieldRoutePane);
        FieldButton("RouteAdd", "ADD PIN STEP", 12f, 266f, 168f, AddRouteStep, _fieldRoutePane);
        FieldButton("RouteNext", "START / NEXT", 192f, 266f, 168f, AdvanceRoute, _fieldRoutePane);
        FieldButton("ClearDestination", "CLEAR DEST.", 12f, 316f, 168f, () => Destination(null), _fieldRoutePane);
        _trailLabel = FieldButton("Trail", "RECORD TRAIL", 192f, 316f, 168f,
            () => _fieldChanged?.Invoke(new("trail-toggle")), _fieldRoutePane);
        FieldButton("ClearTrail", "CLEAR TRAIL", 12f, 366f, 168f,
            () => ConfirmFieldRemoval("trail", "Clear recorded trail?", new("trail-clear")), _fieldRoutePane);
        FieldButton("DeleteRoute", "DELETE ROUTE", 192f, 366f, 168f, () =>
        {
            if (_selectedRoute is { } route) ConfirmFieldRemoval("route:" + route, "Delete selected route?", new("remove-route", Target: route.ToString()));
        }, _fieldRoutePane);
        FieldButton("RouteBack", "PREVIOUS STEP", 12f, 416f, 168f, PreviousRouteStep, _fieldRoutePane);
        FieldButton("RouteTrim", "REMOVE LAST", 192f, 416f, 168f, RemoveLastRouteStep, _fieldRoutePane);
        FieldButton("RouteFit", "FIT ROUTE", 12f, 466f, 168f, FitSelectedRoute, _fieldRoutePane);
        FieldButton("RouteReverse", "REVERSE ROUTE", 192f, 466f, 168f, ReverseSelectedRoute, _fieldRoutePane);
        FieldButton("RouteSteps", "VIEW STEPS", 12f, 516f, 348f, () => OpenNotebookBrowser(NotebookBrowser.Steps), _fieldRoutePane);
        _notebookTrailStatus = FieldLabel("TrailHint", "", 556f, 14f, MapMuted, _fieldRoutePane);
        _notebookTrailStatus.rectTransform.sizeDelta = new Vector2(348f, 36f);
        BuildNotebookBrowser();
        ShowFieldRoutes(_fieldRoutesOpen);
        _fieldPanel.gameObject.SetActive(_fieldPanelOpen);
        RefreshFieldStatus();
    }

    private void ShowFieldRoutes(bool routes)
    {
        ClearFieldConfirmation();
        _fieldRoutesOpen = routes;
        _placeName?.DeactivateInputField();
        DeactivateNotebookInputs();
        _notebookBrowser = NotebookBrowser.None;
        if (_notebookBrowserPane != null) _notebookBrowserPane.gameObject.SetActive(false);
        if (_fieldPinPane != null) _fieldPinPane.gameObject.SetActive(!routes);
        if (_fieldRoutePane != null) _fieldRoutePane.gameObject.SetActive(routes);
        if (_pinsTab != null) _pinsTab.color = routes ? MapMuted : MapAccent;
        if (_routesTab != null) _routesTab.color = routes ? MapAccent : MapMuted;
    }

    private void LayoutFieldUi()
    {
        // Keep the compact panel clear of the footer on shorter landscape viewports.
        var scale = Mathf.Min(1f, Mathf.Max(.5f, (_size.y - 180f) / 592f));
        _fieldPanel!.localScale = new Vector3(scale, scale, 1f);
    }

    private static void FieldPosition(RectTransform rect, float left, float top)
    {
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = new Vector2(left, -top);
    }

    private TextMeshProUGUI FieldLabel(string name, string text, float top, float size, Color color, Transform? parent = null)
    {
        var label = Label(parent ?? _fieldPanel!, name, text, size, color, Vector2.zero, new Vector2(348f, 30f));
        FieldPosition(label.rectTransform, 12f, top);
        label.alignment = TextAlignmentOptions.MidlineLeft;
        return label;
    }

    private TextMeshProUGUI FieldButton(string name, string text, float left, float top, float width, Action action,
        Transform? parent = null)
    {
        var image = Image(parent ?? _fieldPanel!, name, new Color(.09f, .19f, .21f, 1f), Vector2.zero, new Vector2(width, 38f));
        FieldPosition(image.rectTransform, left, top);
        image.raycastTarget = true;
        var button = image.gameObject.AddComponent<Button>();
        button.targetGraphic = image;
        button.onClick.AddListener((UnityAction)new Action(action));
        var caption = Label(image.transform, "Caption", text, 18f, MapInk, Vector2.zero, new Vector2(width - 10f, 36f));
        _notebookControls[name] = (button, caption);
        return caption;
    }

    private bool FieldChromeHit(Vector2 point)
    {
        // The guidance strip and panel are UI, never a map gesture target.
        var bottom = point.y + _size.y * .5f;
        if (bottom >= 68f && bottom <= 116f && Mathf.Abs(point.x) <= 370f) return true;
        if (_destinationEdge != null && _destinationEdge.gameObject.activeSelf
            && new Rect(_destinationEdge.anchoredPosition - new Vector2(71f, 38f), new Vector2(142f, 76f)).Contains(point)) return true;
        if (!_fieldPanelOpen || _fieldPanel == null) return false;
        var local = _fieldPanel.InverseTransformPoint(_root!.TransformPoint(point));
        return _fieldPanel.rect.Contains(new Vector2(local.x, local.y));
    }

    private void CreateMapPin(Vector2 uv)
    {
        if (_fieldChanged == null || _field.Places.Count >= 128 || !InCoverage(uv)) return;
        var x = _fieldEastUv;
        var z = _fieldNorthUv;
        var delta = uv - _fieldOriginUv;
        var determinant = x.x * z.y - z.x * x.y;
        if (!float.IsFinite(determinant) || Mathf.Abs(determinant) < 1e-12f) return;
        // The native map has no height. Y=0 is presentation-only, never a travel/teleport target.
        var point = new ClientFieldPoint((delta.x * z.y - delta.y * z.x) / determinant, 0f,
            (x.x * delta.y - x.y * delta.x) / determinant);
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Z)
            || Mathf.Abs(point.X) > 1_000_000f || Mathf.Abs(point.Z) > 1_000_000f) return;
        var place = new ClientFieldPlace(Guid.NewGuid(), "Map pin " + (_field.Places.Count + 1), point,
            "Map pin; surface height is unknown.", "Map pin");
        _selectedPlace = place.Id;
        _fieldPanelOpen = true;
        _fieldPanel!.gameObject.SetActive(true);
        ShowFieldRoutes(false);
        _fieldChanged(new("place", Place: place));
    }

    private void SelectFieldPin(Vector2 point)
    {
        var bestDistance = 32f * 32f;
        ClientFieldPlace? selected = null;
        foreach (var place in _field.Places)
        {
            if (!TryFieldUv(place.Position, out var uv) || !InCoverage(uv)) continue;
            var pin = UvToScreen(uv);
            if (!InView(pin, 25f)) continue;
            var distance = (pin - point).sqrMagnitude;
            if (distance > bestDistance) continue;
            bestDistance = distance;
            selected = place;
        }
        if (selected == null) return;
        ClearFieldConfirmation();
        _selectedPlace = selected.Id;
        _fieldPanelOpen = true;
        _fieldPanel!.gameObject.SetActive(true);
        ShowFieldRoutes(false);
        RefreshFieldStatus();
    }

    private void CyclePlace(int direction)
    {
        ClearFieldConfirmation();
        if (_field.Places.Count == 0) return;
        var index = direction > 0 ? -1 : 0;
        for (var i = 0; i < _field.Places.Count; i++) if (_field.Places[i].Id == _selectedPlace) { index = i; break; }
        index = (index + direction + _field.Places.Count) % _field.Places.Count;
        var place = _field.Places[index];
        _selectedPlace = place.Id;
        if (_navigable && TryFieldUv(place.Position, out var uv))
        {
            _following = false;
            _zoomAnchorActive = false;
            _center = uv;
        }
        RefreshFieldStatus();
    }

    private void RenamePlace()
    {
        if (FindPlace(_selectedPlace) is not { } place) return;
        var name = _placeName!.text.Trim();
        if (name.Length is < 1 or > 64) return;
        foreach (var character in name) if (char.IsControl(character)) return;
        _fieldChanged?.Invoke(new("place", Place: place with { Name = name }));
    }

    private void Destination(Guid? id)
    {
        _fieldChanged?.Invoke(new("destination", Target: id?.ToString() ?? ""));
        if (!id.HasValue) _routeStep = -1;
    }

    private void NewRoute()
    {
        if (_field.Routes.Count >= 32 || _fieldChanged == null) return;
        var route = new ClientFieldRoute(Guid.NewGuid(), "Route " + (_field.Routes.Count + 1), Array.Empty<Guid>());
        _selectedRoute = route.Id;
        _routeStep = -1;
        _fieldChanged(new("route", Route: route));
    }

    private void AddRouteStep()
    {
        if (SelectedRoute() is not { } route || FindPlace(_selectedPlace) is not { } place || route.Places.Count >= 128) return;
        var places = new List<Guid>(route.Places) { place.Id };
        _fieldChanged?.Invoke(new("route", Route: route with { Places = places.AsReadOnly() }));
    }

    private void AdvanceRoute()
    {
        if (SelectedRoute() is not { } route || route.Places.Count == 0) return;
        if (_routeStep + 1 >= route.Places.Count)
        {
            Destination(null);
            return;
        }
        _routeStep++;
        _selectedPlace = route.Places[_routeStep];
        Destination(_selectedPlace);
        RefreshFieldStatus();
    }

    private void RefreshFieldStatus()
    {
        if (_guidance == null) return;
        var place = FindPlace(_selectedPlace);
        if (_namePlace != place?.Id)
        {
            _placeName!.DeactivateInputField();
            _namePlace = place?.Id;
            _placeName.text = place?.Name ?? "";
        }
        _placeName!.interactable = place != null && _fieldChanged != null;
        _placeDetail!.text = NotebookPinDetail(place);
        _fieldHint!.text = Time.unscaledTime < _fieldFeedbackUntil ? _fieldFeedback
            : _field.Places.Count + "/128 pins · Private until shared";
        _routePinDetail!.text = place == null ? "Choose a pin in Pins" : "Pin: " + place.Name;
        var route = SelectedRoute();
        _routeDetail!.text = route == null ? "Routes · Choose or create one"
            : route.Places.Count + "/128 steps" + (_routeStep >= 0 ? " · Step " + (_routeStep + 1) : " · Not started");
        _trailLabel!.text = _field.RecordTrail ? "STOP TRAIL" : "RECORD TRAIL";
        RefreshNotebook(route, place);
        var destination = FindPlace(_field.DestinationId);
        if (destination == null)
        {
            _guidance.text = "Hold map to add a pin · Tap a pin for actions";
            return;
        }
        if (!_navigable) { _guidance.text = destination.Name + " · Waiting for surface GPS"; return; }
        var dx = destination.Position.X - _position.x;
        var dz = destination.Position.Z - _position.z;
        var distance = Mathf.Sqrt(dx * dx + dz * dz);
        var bearing = Mathf.Repeat(Mathf.Atan2(dx, dz) * Mathf.Rad2Deg, 360f);
        var units = distance >= 1000f ? (distance / 1000f).ToString("0.0", CultureInfo.InvariantCulture) + " km"
            : distance.ToString("0", CultureInfo.InvariantCulture) + " m";
        var turn = Mathf.DeltaAngle(_heading, bearing);
        var direction = Mathf.Abs(turn) < 15f ? "Ahead" : Mathf.Abs(turn) > 165f ? "Behind you"
            : (turn > 0f ? "Right " : "Left ") + Mathf.Abs(turn).ToString("0", CultureInfo.InvariantCulture) + "°";
        var name = destination.Name.Length > 20 ? destination.Name[..19] + "…" : destination.Name;
        _guidance.text = name + " · " + units + " · "
            + (distance <= 15f ? "NEAR PIN · Check terrain / height" : (_headingAvailable ? direction + " · " : "Bearing ")
                + NotebookBearing(bearing) + "° " + Cardinal(bearing) + " · Direct");
    }

    private void PositionFieldOverlays()
    {
        var used = 0;
        foreach (var place in _field.Places)
        {
            if (used >= 128) break;
            if (!TryFieldUv(place.Position, out var uv) || !InCoverage(uv)) continue;
            if (used == _fieldPins.Count)
            {
                var root = Node(_fieldLayer!, "PrivatePin");
                root.sizeDelta = new Vector2(40f, 40f);
                var disc = Image(root, "Pin", MapAccent, Vector2.zero, new Vector2(30f, 30f));
                disc.sprite = _circleSprite;
                var label = Label(root, "Name", "", 18f, MapInk, new Vector2(0f, -29f), new Vector2(170f, 28f));
                _fieldPins.Add(new(root, disc, label));
            }
            var pin = _fieldPins[used++];
            var point = UvToScreen(uv);
            pin.Root.anchoredPosition = point;
            pin.Root.gameObject.SetActive(InView(point, 25f));
            pin.Disc.color = place.Id == _field.DestinationId ? new Color(1f, .8f, .35f, 1f) : MapAccent;
            pin.Root.localScale = place.Id == _selectedPlace ? new Vector3(1.15f, 1.15f, 1f) : Vector3.one;
            SetText(pin.Label, place.Name);
        }
        for (var i = used; i < _fieldPins.Count; i++) _fieldPins[i].Root.gameObject.SetActive(false);
        var trailCount = Math.Min(511, Math.Max(0, _field.Trail.Count - 1));
        for (var i = 0; i < trailCount; i++) PositionFieldLine(_trailLines, i, _field.Trail[i], _field.Trail[i + 1],
            new Color(.42f, .85f, .89f, .75f), 3f);
        HideLinesAfter(_trailLines, trailCount);
        var route = SelectedRoute();
        var routeCount = route == null ? 0 : Math.Min(127, Math.Max(0, route.Places.Count - 1));
        for (var i = 0; i < routeCount; i++)
        {
            var start = FindPlace(route!.Places[i]);
            var end = FindPlace(route.Places[i + 1]);
            if (start != null && end != null) PositionFieldLine(_routeLines, i, start.Position, end.Position,
                new Color(1f, .8f, .35f, .65f), 3f);
            else if (i < _routeLines.Count) _routeLines[i].gameObject.SetActive(false);
        }
        HideLinesAfter(_routeLines, routeCount);
    }

    private void PositionFieldLine(List<RectTransform> pool, int index, ClientFieldPoint start, ClientFieldPoint end,
        Color color, float width)
    {
        while (pool.Count <= index)
            pool.Add(Image(_fieldLines!, "Segment", color, Vector2.zero, new Vector2(1f, width)).rectTransform);
        var line = pool[index];
        if (!TryFieldUv(start, out var first) || !TryFieldUv(end, out var second))
        { line.gameObject.SetActive(false); return; }
        var a = UvToScreen(first);
        var b = UvToScreen(second);
        var delta = b - a;
        line.gameObject.SetActive(delta.sqrMagnitude > 1f);
        line.anchoredPosition = (a + b) * .5f;
        line.sizeDelta = new Vector2(delta.magnitude, width);
        line.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
    }

    private static void HideLinesAfter(List<RectTransform> lines, int count)
    {
        for (var i = count; i < lines.Count; i++) lines[i].gameObject.SetActive(false);
    }

    private bool TryFieldUv(ClientFieldPoint point, out Vector2 uv)
    {
        // Reuse the native X/Z basis calibrated with the live map each frame. No per-pin interop calls.
        uv = _fieldOriginUv + _fieldEastUv * point.X + _fieldNorthUv * point.Z;
        return float.IsFinite(uv.x) && float.IsFinite(uv.y);
    }

    private void ClearFieldUi()
    {
        _fieldPins.Clear();
        _trailLines.Clear();
        _routeLines.Clear();
        _fieldLayer = _fieldLines = _fieldPanel = null;
        _fieldPinPane = _fieldRoutePane = null;
        _pinsTab = _routesTab = _routePinDetail = null;
        _fieldHint = _placeDetail = _routeDetail = _trailLabel = _guidance = null;
        _placeName = null;
        ClearNotebookUi();
        _namePlace = null;
        _destinationEdge = null;
        _destinationEdgeArrow = null;
        _destinationEdgeText = null;
        _labelBounds.Clear();
        ClearFieldConfirmation();
    }
}
