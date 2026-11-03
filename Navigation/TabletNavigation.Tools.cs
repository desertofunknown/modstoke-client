// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using TMPro;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientTabletNavigation
{
    private readonly List<Rect> _labelBounds = new(128);
    private RectTransform? _destinationEdge;
    private RectTransform? _destinationEdgeArrow;
    private TextMeshProUGUI? _destinationEdgeText;
    private string _fieldFeedback = "";
    private float _fieldFeedbackUntil;
    private string _fieldConfirmation = "";
    private float _fieldConfirmationUntil;

    private void FieldFeedback(string message)
    {
        _fieldFeedback = message;
        _fieldFeedbackUntil = Time.unscaledTime + 4f;
        RefreshFieldStatus();
    }

    private void ClearFieldConfirmation()
    {
        _fieldConfirmation = "";
        _fieldConfirmationUntil = 0f;
        _fieldFeedbackUntil = 0f;
    }

    private void ConfirmFieldRemoval(string key, string prompt, ClientFieldAction action)
    {
        if (_fieldChanged == null) return;
        if (key == "trail" && _field.Trail.Count == 0) { FieldFeedback("No recorded trail to clear."); return; }
        if (_fieldConfirmation != key || Time.unscaledTime >= _fieldConfirmationUntil)
        {
            _fieldConfirmation = key;
            _fieldConfirmationUntil = Time.unscaledTime + 4f;
            FieldFeedback(prompt + " Tap again.");
            return;
        }
        ClearFieldConfirmation();
        _fieldChanged(action);
    }

    private void PinCurrentLocation()
    {
        if (!_navigable) { FieldFeedback("Return to surface GPS to save location."); return; }
        if (_field.Places.Count >= 128) { FieldFeedback("Pin limit reached. Remove a pin first."); return; }
        if (_fieldChanged == null) return;
        var place = new ClientFieldPlace(Guid.NewGuid(), "My location " + (_field.Places.Count + 1),
            new(_position.x, _position.y, _position.z), "Saved from live surface GPS.", "My location");
        _selectedPlace = place.Id;
        _fieldChanged(new("place", Place: place));
        FieldFeedback("Location submitted with GPS height.");
    }

    private void SelectNearestPin()
    {
        if (!_navigable) { FieldFeedback("Nearest pin needs a live GPS position."); return; }
        ClientFieldPlace? nearest = null;
        var best = float.PositiveInfinity;
        foreach (var place in _field.Places)
        {
            if (!TryFieldUv(place.Position, out var uv) || !InCoverage(uv)) continue;
            var dx = place.Position.X - _position.x;
            var dz = place.Position.Z - _position.z;
            var distance = dx * dx + dz * dz;
            if (distance >= best) continue;
            best = distance;
            nearest = place;
        }
        if (nearest == null) { FieldFeedback("No saved pins inside map coverage."); return; }
        _selectedPlace = nearest.Id;
        CyclePlace(0);
        FieldFeedback("Nearest pin · " + MapDistance(Mathf.Sqrt(best)) + " direct");
    }

    private void PreviousRouteStep()
    {
        if (SelectedRoute() is not { } route || route.Places.Count == 0)
        { FieldFeedback("Choose a route with saved pin steps."); return; }
        if (_routeStep <= 0) { FieldFeedback(_routeStep == 0 ? "This is the first route step." : "Start the route before stepping back."); return; }
        _routeStep--;
        _selectedPlace = route.Places[_routeStep];
        Destination(_selectedPlace);
        RefreshFieldStatus();
    }

    private void RemoveLastRouteStep()
    {
        if (_fieldChanged == null) return;
        if (SelectedRoute() is not { } route || route.Places.Count == 0)
        { FieldFeedback("No route step to remove."); return; }
        var places = new List<Guid>(route.Places);
        places.RemoveAt(places.Count - 1);
        // Stop active guidance only if its current step is being removed.
        if (_routeStep >= places.Count)
        {
            if (_field.DestinationId == route.Places[^1]) Destination(null);
            _routeStep = -1;
        }
        _fieldChanged(new("route", Route: route with { Places = places.AsReadOnly() }));
        FieldFeedback("Removing last route step; pin stays saved.");
    }

    private void ReverseSelectedRoute()
    {
        if (_fieldChanged == null) return;
        if (SelectedRoute() is not { } route || route.Places.Count < 2)
        { FieldFeedback("Add at least two steps to reverse."); return; }
        var places = new List<Guid>(route.Places);
        places.Reverse();
        if (_routeStep >= 0 && _routeStep < route.Places.Count && _field.DestinationId == route.Places[_routeStep]) Destination(null);
        _routeStep = -1;
        _fieldChanged(new("route", Route: route with { Places = places.AsReadOnly() }));
        FieldFeedback("Reversing route. START begins return trip.");
    }

    private void FitSelectedRoute()
    {
        if (!_navigable) { FieldFeedback("Route overview needs surface GPS."); return; }
        if (SelectedRoute() is not { } route || route.Places.Count == 0)
        { FieldFeedback("Add pins to a route before fitting it."); return; }
        var min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
        var max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
        foreach (var id in route.Places)
        {
            if (FindPlace(id) is not { } place || !TryFieldUv(place.Position, out var uv) || !InCoverage(uv))
            { FieldFeedback("A route pin is outside map coverage."); return; }
            min = Vector2.Min(min, uv);
            max = Vector2.Max(max, uv);
        }
        // North-up gives a stable overview. Close the panel so the whole fitted route stays visible.
        var span = max - min;
        var side = _size.magnitude;
        _targetZoom = Mathf.Clamp(Mathf.Min((_size.x - 200f) / (Mathf.Max(.001f, span.x) * side),
            (_size.y - 340f) / (Mathf.Max(.001f, span.y) * side)), MinimumZoom, MaximumZoom);
        _zoom = _targetZoom;
        _headingUp = false;
        _rotation = 0f;
        _following = false;
        _dragging = false;
        _zoomAnchorActive = false;
        _mapSize = Vector2.one * (side * _zoom);
        _center = (min + max) * .5f;
        _fieldPanelOpen = false;
        ClearFieldConfirmation();
        _placeName?.DeactivateInputField();
        DeactivateNotebookInputs();
        _fieldPanel!.gameObject.SetActive(false);
    }

    private static string MapDistance(float distance) => distance >= 1000f
        ? (distance / 1000f).ToString("0.0", CultureInfo.InvariantCulture) + " km"
        : distance.ToString("0", CultureInfo.InvariantCulture) + " m";

    private void BuildDestinationEdge()
    {
        _destinationEdge = Image(_root!, "OffscreenDestination", MapChrome, Vector2.zero, new Vector2(142f, 76f)).rectTransform;
        _destinationEdgeArrow = Image(_destinationEdge, "Direction", new Color(1f, .8f, .35f, 1f),
            new Vector2(0f, 16f), new Vector2(22f, 28f)).rectTransform;
        _destinationEdgeArrow.GetComponent<UnityEngine.UI.Image>().sprite = _arrowSprite;
        _destinationEdgeText = Label(_destinationEdge, "Distance", "", 18f, MapInk,
            new Vector2(0f, -20f), new Vector2(134f, 28f));
        _destinationEdge.gameObject.SetActive(false);
    }

    private void PositionDestinationEdge()
    {
        if (_destinationEdge == null) return;
        var place = FindPlace(_field.DestinationId);
        if (!_navigable || place == null || !TryFieldUv(place.Position, out var uv) || !InCoverage(uv)
            || InView(UvToScreen(uv), 48f))
        { _destinationEdge.gameObject.SetActive(false); return; }
        var point = UvToScreen(uv);
        var left = -_size.x * .5f + 88f;
        if (_fieldPanelOpen) left += 372f * _fieldPanel!.localScale.x;
        var right = _size.x * .5f - 88f;
        var bottom = -_size.y * .5f + 164f;
        var top = _size.y * .5f - 222f;
        if (left >= right || bottom >= top) { _destinationEdge.gameObject.SetActive(false); return; }
        var origin = new Vector2((left + right) * .5f, (bottom + top) * .5f);
        var delta = point - origin;
        var extent = new Vector2((right - left) * .5f, (top - bottom) * .5f);
        var factor = Mathf.Max(Mathf.Abs(delta.x) / extent.x, Mathf.Abs(delta.y) / extent.y);
        _destinationEdge.anchoredPosition = origin + delta / Mathf.Max(1f, factor);
        _destinationEdgeArrow!.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(-delta.x, delta.y) * Mathf.Rad2Deg);
        var dx = place.Position.X - _position.x;
        var dz = place.Position.Z - _position.z;
        SetText(_destinationEdgeText!, MapDistance(Mathf.Sqrt(dx * dx + dz * dz)));
        _destinationEdge.gameObject.SetActive(true);
    }

    private bool ReserveMapLabel(Vector2 center, Vector2 size)
    {
        var bounds = new Rect(center - size * .5f, size);
        if (!InView(new Vector2(bounds.xMin, bounds.yMin), 6f)
            || !InView(new Vector2(bounds.xMax, bounds.yMax), 6f)
            || bounds.yMax > _size.y * .5f - 100f) return false;
        if (bounds.Overlaps(new Rect(-370f, -_size.y * .5f + 68f, 740f, 48f))
            || bounds.Overlaps(new Rect(_size.x * .5f - 100f, _size.y * .5f - 180f, 100f, 100f))) return false;
        if (_fieldPanelOpen && _fieldPanel != null)
        {
            var scale = _fieldPanel.localScale.x;
            if (bounds.Overlaps(new Rect(-_size.x * .5f + 24f, _size.y * .5f - 104f - 592f * scale,
                372f * scale, 592f * scale))) return false;
        }
        if (_destinationEdge != null && _destinationEdge.gameObject.activeSelf
            && bounds.Overlaps(new Rect(_destinationEdge.anchoredPosition - new Vector2(71f, 38f), new Vector2(142f, 76f)))) return false;
        foreach (var occupied in _labelBounds) if (occupied.Overlaps(bounds)) return false;
        _labelBounds.Add(bounds);
        return true;
    }

    private void DeclutterMapLabels()
    {
        _labelBounds.Clear();
        // Selected and destination pins claim space before ordinary saved pins and native labels.
        for (var priority = 2; priority >= 0; priority--)
        {
            var used = 0;
            foreach (var place in _field.Places)
            {
                if (!TryFieldUv(place.Position, out var uv) || !InCoverage(uv)) continue;
                if (used >= _fieldPins.Count) break;
                var pin = _fieldPins[used++];
                var importance = place.Id == _selectedPlace ? 2 : place.Id == _field.DestinationId ? 1 : 0;
                if (importance != priority) continue;
                var point = UvToScreen(uv);
                var scale = pin.Root.localScale.x;
                var visible = InView(point, 25f) && ReserveMapLabel(point + new Vector2(0f, -29f * scale), new Vector2(174f, 32f) * scale);
                pin.Label.gameObject.SetActive(visible);
            }
        }
        for (var i = 0; i < _activeMarkers; i++)
        {
            var marker = _markers[i];
            var point = UvToScreen(marker.Uv);
            marker.LabelBackground.gameObject.SetActive(_showMarkers && InView(point, 25f) && marker.Label.text.Length > 0
                && ReserveMapLabel(point + new Vector2(0f, -40f), new Vector2(160f, 34f)));
        }
    }
}
