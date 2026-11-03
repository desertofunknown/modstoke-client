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
    private enum NotebookBrowser { None, Pins, Routes, Steps }
    private sealed record NotebookResult(Guid Id, int Step = -1);
    private readonly Dictionary<string, (Button Button, TextMeshProUGUI Caption)> _notebookControls = new(StringComparer.Ordinal);
    private readonly List<NotebookResult> _notebookResults = new(128);
    private readonly List<TextMeshProUGUI> _notebookRows = new(5);
    private NotebookBrowser _notebookBrowser;
    private RectTransform? _notebookBrowserPane;
    private TMP_InputField? _notebookSearch;
    private TMP_InputField? _routeName;
    private TextMeshProUGUI? _notebookSummary;
    private TextMeshProUGUI? _notebookFooter;
    private TextMeshProUGUI? _notebookTrailStatus;
    private Guid? _nameRoute;
    private string _notebookQuery = "";
    private int _notebookPage;
    private bool _notebookNearest;
    private bool _notebookResultsDirty = true;
    private bool _notebookHadPosition;

    private TMP_InputField NotebookInput(Transform parent, string name, string placeholder, float left, float top,
        float width, int limit)
    {
        var image = Image(parent, name, new Color(.08f, .14f, .16f, 1f), Vector2.zero, new Vector2(width, 38f));
        FieldPosition(image.rectTransform, left, top);
        image.raycastTarget = true;
        var input = image.gameObject.AddComponent<TMP_InputField>();
        input.targetGraphic = image;
        input.richText = false;
        input.lineType = TMP_InputField.LineType.SingleLine;
        input.characterLimit = limit;
        input.customCaretColor = true;
        input.caretColor = MapAccent;
        var viewport = Node(image.transform, "Viewport");
        Fill(viewport);
        viewport.offsetMin = new Vector2(8f, 3f);
        viewport.offsetMax = new Vector2(-8f, -3f);
        viewport.gameObject.AddComponent<RectMask2D>();
        var text = Label(viewport, "Text", "", 18f, MapInk, Vector2.zero, Vector2.zero);
        Fill(text.rectTransform);
        text.alignment = TextAlignmentOptions.MidlineLeft;
        var hint = Label(viewport, "Placeholder", placeholder, 17f, MapMuted, Vector2.zero, Vector2.zero);
        Fill(hint.rectTransform);
        hint.alignment = TextAlignmentOptions.MidlineLeft;
        input.textViewport = viewport;
        input.textComponent = text;
        input.placeholder = hint;
        return input;
    }

    private void BuildNotebookBrowser()
    {
        _notebookBrowserPane = Node(_fieldPanel!, "BrowseSavedNavigation");
        Fill(_notebookBrowserPane);
        _notebookSearch = NotebookInput(_notebookBrowserPane, "Search", "Search saved names / notes", 12f, 122f, 266f, 64);
        _notebookSearch.onValueChanged.AddListener((UnityAction<string>)new Action<string>(query =>
        {
            _notebookQuery = query;
            _notebookPage = 0;
            _notebookResultsDirty = true;
            RefreshNotebookBrowser();
        }));
        FieldButton("BrowserClear", "CLEAR", 286f, 122f, 74f, () => _notebookSearch.text = "", _notebookBrowserPane);
        _notebookSummary = FieldLabel("Results", "", 166f, 16f, MapAccent, _notebookBrowserPane);
        for (var i = 0; i < 5; i++)
        {
            var row = i;
            var label = FieldButton("BrowserRow" + i, "", 12f, 204f + i * 52f, 348f,
                () => SelectNotebookResult(row), _notebookBrowserPane);
            var buttonRect = label.transform.parent.GetComponent<RectTransform>();
            buttonRect.sizeDelta = new Vector2(348f, 46f);
            label.rectTransform.sizeDelta = new Vector2(330f, 44f);
            label.fontSize = 17f;
            label.alignment = TextAlignmentOptions.MidlineLeft;
            _notebookRows.Add(label);
        }
        FieldButton("BrowserPrevious", "< PREVIOUS", 12f, 466f, 168f, () => MoveNotebookPage(-1), _notebookBrowserPane);
        FieldButton("BrowserNext", "NEXT >", 192f, 466f, 168f, () => MoveNotebookPage(1), _notebookBrowserPane);
        FieldButton("BrowserBack", "BACK", 12f, 512f, 168f, () => ShowFieldRoutes(_fieldRoutesOpen), _notebookBrowserPane);
        FieldButton("BrowserSort", "SORT: NAME", 192f, 512f, 168f, () =>
        {
            if (_notebookBrowser != NotebookBrowser.Pins) return;
            _notebookNearest = !_notebookNearest;
            _notebookResultsDirty = true;
            _notebookPage = 0;
            RefreshNotebookBrowser();
        }, _notebookBrowserPane);
        _notebookFooter = FieldLabel("BrowserHint", "", 552f, 14f, MapMuted, _notebookBrowserPane);
        _notebookFooter.rectTransform.sizeDelta = new Vector2(348f, 38f);
        _notebookFooter.enableWordWrapping = true;
        _notebookBrowserPane.gameObject.SetActive(false);
    }

    private void OpenNotebookBrowser(NotebookBrowser browser)
    {
        ClearFieldConfirmation();
        DeactivateNotebookInputs();
        _placeName?.DeactivateInputField();
        _notebookBrowser = browser;
        _notebookQuery = "";
        _notebookPage = 0;
        _notebookResultsDirty = true;
        _notebookSearch!.SetTextWithoutNotify("");
        if (_notebookSearch.placeholder is TextMeshProUGUI hint)
            hint.text = browser == NotebookBrowser.Routes ? "Search route names" : "Search names / categories / notes";
        _fieldPanelOpen = true;
        _fieldPanel!.gameObject.SetActive(true);
        _fieldPinPane!.gameObject.SetActive(false);
        _fieldRoutePane!.gameObject.SetActive(false);
        _notebookBrowserPane!.gameObject.SetActive(true);
        RefreshNotebookBrowser();
    }

    private void MoveNotebookPage(int direction)
    {
        _notebookPage = Math.Clamp(_notebookPage + direction, 0, Math.Max(0, (_notebookResults.Count - 1) / 5));
        RefreshNotebookBrowser();
    }

    private static bool NotebookMatches(string[] terms, params string[] fields)
    {
        foreach (var term in terms)
        {
            var found = false;
            foreach (var field in fields)
                if (field.Contains(term, StringComparison.OrdinalIgnoreCase)) { found = true; break; }
            if (!found) return false;
        }
        return true;
    }

    private void RebuildNotebookResults()
    {
        _notebookResultsDirty = false;
        _notebookHadPosition = _navigable;
        _notebookResults.Clear();
        var terms = _notebookQuery.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (_notebookBrowser == NotebookBrowser.Routes)
        {
            var routes = new List<ClientFieldRoute>();
            foreach (var route in _field.Routes) if (NotebookMatches(terms, route.Name)) routes.Add(route);
            routes.Sort((a, b) => NotebookNameOrder(a.Name, a.Id, b.Name, b.Id));
            foreach (var route in routes) _notebookResults.Add(new(route.Id));
        }
        else if (_notebookBrowser == NotebookBrowser.Steps)
        {
            if (SelectedRoute() is not { } route) return;
            for (var i = 0; i < route.Places.Count; i++)
                if (FindPlace(route.Places[i]) is { } place && NotebookMatches(terms, place.Name, place.Category, place.Note))
                    _notebookResults.Add(new(place.Id, i));
        }
        else
        {
            var places = new List<ClientFieldPlace>();
            foreach (var place in _field.Places)
                if (NotebookMatches(terms, place.Name, place.Category, place.Note)) places.Add(place);
            places.Sort((a, b) =>
            {
                if (_notebookNearest && _navigable)
                {
                    var distanceOrder = NotebookPinDistance(a).CompareTo(NotebookPinDistance(b));
                    if (distanceOrder != 0) return distanceOrder;
                }
                return NotebookNameOrder(a.Name, a.Id, b.Name, b.Id);
            });
            foreach (var place in places) _notebookResults.Add(new(place.Id));
        }
    }

    private static int NotebookNameOrder(string a, Guid aId, string b, Guid bId)
    {
        var order = StringComparer.OrdinalIgnoreCase.Compare(a, b);
        return order != 0 ? order : aId.CompareTo(bId);
    }

    private void RefreshNotebookBrowser()
    {
        if (_notebookBrowser == NotebookBrowser.None || _notebookSummary == null) return;
        if (_notebookResultsDirty || _notebookHadPosition != _navigable) RebuildNotebookResults();
        var pages = Math.Max(1, (_notebookResults.Count + 4) / 5);
        _notebookPage = Math.Clamp(_notebookPage, 0, pages - 1);
        var total = _notebookBrowser == NotebookBrowser.Routes ? _field.Routes.Count
            : _notebookBrowser == NotebookBrowser.Steps ? SelectedRoute()?.Places.Count ?? 0 : _field.Places.Count;
        _notebookSummary.text = _notebookResults.Count == 0
            ? (total == 0 ? "Nothing saved here yet" : "No matches · Clear or change search")
            : _notebookResults.Count + "/" + total + " " + _notebookBrowser.ToString().ToLowerInvariant()
                + " · Page " + (_notebookPage + 1) + "/" + pages;
        for (var i = 0; i < _notebookRows.Count; i++)
        {
            var resultIndex = _notebookPage * 5 + i;
            var caption = _notebookRows[i];
            caption.transform.parent.gameObject.SetActive(resultIndex < _notebookResults.Count);
            if (resultIndex >= _notebookResults.Count) continue;
            var result = _notebookResults[resultIndex];
            if (_notebookBrowser == NotebookBrowser.Routes)
            {
                var route = NotebookFindRoute(result.Id);
                if (route == null) continue;
                caption.text = NotebookShort(route.Name, 34) + "\n" + route.Places.Count + " steps · " + NotebookRouteLength(route);
            }
            else if (FindPlace(result.Id) is { } place)
            {
                var prefix = result.Step >= 0 ? (result.Step + 1) + ". " : "";
                caption.text = prefix + NotebookShort(place.Name, result.Step >= 0 ? 30 : 34) + "\n"
                    + (_navigable ? MapDistance(NotebookPinDistance(place)) + " direct" : "Distance needs GPS")
                    + (result.Step >= 0 && _routeStep == result.Step ? " · CURRENT" : place.Id == _field.DestinationId ? " · DEST." : "");
            }
            NotebookEnabled("BrowserRow" + i, _notebookBrowser != NotebookBrowser.Steps || _fieldChanged != null);
            if ((_notebookBrowser == NotebookBrowser.Routes ? result.Id == _selectedRoute : result.Id == _selectedPlace)
                && _notebookControls["BrowserRow" + i].Button.interactable) caption.color = MapAccent;
        }
        NotebookEnabled("BrowserPrevious", _notebookPage > 0);
        NotebookEnabled("BrowserNext", _notebookPage + 1 < pages);
        NotebookEnabled("BrowserClear", _notebookQuery.Length > 0);
        NotebookEnabled("BrowserSort", _notebookBrowser == NotebookBrowser.Pins);
        _notebookControls["BrowserSort"].Button.gameObject.SetActive(_notebookBrowser == NotebookBrowser.Pins);
        _notebookControls["BrowserSort"].Caption.text = _notebookNearest ? "SORT: NEAREST" : "SORT: NAME";
        _notebookFooter!.text = _notebookBrowser == NotebookBrowser.Steps ? NotebookRouteSummary(SelectedRoute())
            : _notebookBrowser == NotebookBrowser.Routes ? "Select a route to inspect or edit."
            : _notebookNearest && !_navigable ? "No GPS · Using name order until signal returns."
            : _notebookNearest ? "Nearest order refreshes when reopened. Tap a pin."
            : "Search name, category and note. Tap a pin to inspect.";
    }

    private void SelectNotebookResult(int row)
    {
        var index = _notebookPage * 5 + row;
        if (index < 0 || index >= _notebookResults.Count) return;
        var result = _notebookResults[index];
        if (_notebookBrowser == NotebookBrowser.Routes)
        {
            if (NotebookFindRoute(result.Id) == null) return;
            if (_selectedRoute != result.Id) _routeStep = -1;
            _selectedRoute = result.Id;
            ShowFieldRoutes(true);
            RefreshFieldStatus();
        }
        else if (_notebookBrowser == NotebookBrowser.Steps)
        {
            var route = SelectedRoute();
            if (_fieldChanged == null || route == null || result.Step < 0 || result.Step >= route.Places.Count
                || route.Places[result.Step] != result.Id || FindPlace(result.Id) == null) return;
            _routeStep = result.Step;
            _selectedPlace = result.Id;
            Destination(result.Id);
            ShowFieldRoutes(true);
            FieldFeedback("Step " + (result.Step + 1) + " selected. Progress is manual.");
        }
        else
        {
            if (FindPlace(result.Id) == null) return;
            _selectedPlace = result.Id;
            ShowFieldRoutes(false);
            CyclePlace(0);
        }
    }

    private ClientFieldRoute? NotebookFindRoute(Guid id)
    {
        foreach (var route in _field.Routes) if (route.Id == id) return route;
        return null;
    }

    private void RenameRoute()
    {
        if (_fieldChanged == null || SelectedRoute() is not { } route || _routeName == null) return;
        var name = _routeName.text.Trim();
        if (!NotebookValidName(name)) { FieldFeedback("Use 1–64 characters for a route name."); return; }
        if (name == route.Name) { FieldFeedback("Route name is unchanged."); return; }
        _fieldChanged(new("route", Route: route with { Name = name }));
        FieldFeedback("Route name submitted.");
    }

    private static bool NotebookValidName(string name)
    {
        if (name.Length is < 1 or > 64) return false;
        foreach (var c in name) if (char.IsControl(c)) return false;
        return true;
    }

    private void RefreshNotebook(ClientFieldRoute? route, ClientFieldPlace? place)
    {
        if (_routeName == null) return;
        if (_nameRoute != route?.Id)
        {
            _routeName.DeactivateInputField();
            _nameRoute = route?.Id;
            _routeName.SetTextWithoutNotify(route?.Name ?? "");
        }
        var writable = _fieldChanged != null;
        var selected = place != null;
        var steps = route?.Places.Count ?? 0;
        _routeName.interactable = route != null && writable;
        foreach (var name in new[] { "Navigate", "UseOnWatch", "ScoutWithDrone", "Delete", "PinNotes" }) NotebookEnabled(name, selected && writable);
        var noteCount = 0;
        if (place != null) foreach (var note in _field.Notes) if (note.PlaceId == place.Id) noteCount++;
        _notebookControls["PinNotes"].Caption.text = "NOTES · " + noteCount;
        NotebookEnabled("Share", selected && _sharePlace != null);
        NotebookEnabled("Rename", selected && writable && NotebookValidName(_placeName!.text.Trim()) && _placeName.text.Trim() != place!.Name);
        NotebookEnabled("Previous", _field.Places.Count > 1);
        NotebookEnabled("Next", _field.Places.Count > 1);
        NotebookEnabled("PinHere", writable && _navigable && _field.Places.Count < 128);
        NotebookEnabled("NearestPin", _navigable && _field.Places.Count > 0);
        NotebookEnabled("RouteNew", writable && _field.Routes.Count < 32);
        NotebookEnabled("RouteChoose", _field.Routes.Count > 0);
        NotebookEnabled("RouteRename", writable && route != null && NotebookValidName(_routeName.text.Trim()) && _routeName.text.Trim() != route.Name);
        NotebookEnabled("RouteAdd", writable && selected && route != null && steps < 128);
        NotebookEnabled("RouteNext", writable && steps > 0);
        _notebookControls["RouteNext"].Caption.text = _routeStep < 0 ? "START ROUTE" : _routeStep + 1 >= steps ? "FINISH ROUTE" : "NEXT STEP";
        NotebookEnabled("ClearDestination", writable && _field.DestinationId.HasValue);
        NotebookEnabled("Trail", writable);
        NotebookEnabled("ClearTrail", writable && _field.Trail.Count > 0);
        NotebookEnabled("DeleteRoute", writable && route != null);
        NotebookEnabled("RouteBack", writable && _routeStep > 0);
        NotebookEnabled("RouteTrim", writable && steps > 0);
        NotebookEnabled("RouteFit", _navigable && steps > 0);
        NotebookEnabled("RouteReverse", writable && steps > 1);
        NotebookEnabled("RouteSteps", steps > 0);
        var trailDistance = 0f;
        for (var i = 1; i < _field.Trail.Count; i++) trailDistance += NotebookDistance(_field.Trail[i - 1], _field.Trail[i]);
        _notebookTrailStatus!.text = (_field.RecordTrail ? (_navigable ? "REC enabled" : "REC needs GPS") : "Trail stopped")
            + " · " + _field.Trail.Count + "/512 samples\n" + MapDistance(trailDistance) + " retained · Surface only";
        RefreshNotebookBrowser();
    }

    private void NotebookEnabled(string name, bool enabled)
    {
        if (!_notebookControls.TryGetValue(name, out var control)) return;
        control.Button.interactable = enabled;
        control.Caption.color = enabled ? MapInk : MapMuted;
    }

    private string NotebookPinDetail(ClientFieldPlace? place)
    {
        if (place == null) return "Select a pin or hold the map\nFIND searches saved places and notes";
        var coordinates = "X " + place.Position.X.ToString("0", CultureInfo.InvariantCulture) + "  Z "
            + place.Position.Z.ToString("0", CultureInfo.InvariantCulture);
        // Map-marked and sender-reported pins lack height. Recorded Y is never terrain proof.
        var height = ClientFieldLocationText.HeightUnknown(place) ? "Height unknown" : "Recorded Y " + place.Position.Y.ToString("0", CultureInfo.InvariantCulture);
        var live = "Distance / bearing need surface GPS";
        if (_navigable)
        {
            var bearing = Mathf.Repeat(Mathf.Atan2(place.Position.X - _position.x, place.Position.Z - _position.z) * Mathf.Rad2Deg, 360f);
            live = MapDistance(NotebookPinDistance(place)) + " direct · " + NotebookBearing(bearing) + "° " + Cardinal(bearing);
        }
        return coordinates + "\n" + live + "\n" + height + " · " + place.Category;
    }

    private static string NotebookShort(string value, int length) => value.Length <= length ? value : value[..(length - 1)] + "…";
    private static string NotebookBearing(float bearing) => (Mathf.RoundToInt(bearing) % 360).ToString("000", CultureInfo.InvariantCulture);
    private float NotebookPinDistance(ClientFieldPlace place)
    {
        var dx = place.Position.X - _position.x;
        var dz = place.Position.Z - _position.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }
    private static float NotebookDistance(ClientFieldPoint a, ClientFieldPoint b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }
    private bool NotebookRouteDistance(ClientFieldRoute route, int first, out float distance)
    {
        distance = 0f;
        for (var i = first; i < route.Places.Count - 1; i++)
        {
            var a = FindPlace(route.Places[i]);
            var b = FindPlace(route.Places[i + 1]);
            if (a == null || b == null) return false;
            distance += NotebookDistance(a.Position, b.Position);
        }
        return true;
    }
    private string NotebookRouteLength(ClientFieldRoute route) => NotebookRouteDistance(route, 0, out var total)
        ? MapDistance(total) + " straight-line" : "Missing pin";
    private string NotebookRouteSummary(ClientFieldRoute? route)
    {
        if (route == null) return "Choose a route first.";
        var text = NotebookRouteDistance(route, 0, out var total) ? "Total " + MapDistance(total) : "Missing route pin";
        if (_routeStep >= 0 && _routeStep < route.Places.Count && _navigable
            && FindPlace(route.Places[_routeStep]) is { } target && _field.DestinationId == target.Id
            && NotebookRouteDistance(route, _routeStep, out var remaining))
            text += " · " + MapDistance(NotebookPinDistance(target) + remaining) + " left";
        return text + "\nStraight-line estimates · Tap step to navigate";
    }

    private void DeactivateNotebookInputs()
    {
        _notebookSearch?.DeactivateInputField();
        _routeName?.DeactivateInputField();
    }

    private void ClearNotebookUi()
    {
        _notebookControls.Clear();
        _notebookRows.Clear();
        _notebookResults.Clear();
        _notebookResultsDirty = true;
        _notebookBrowser = NotebookBrowser.None;
        _notebookBrowserPane = null;
        _notebookSearch = _routeName = null;
        _notebookSummary = _notebookFooter = _notebookTrailStatus = null;
        _nameRoute = null;
        _playerHeading = null;
    }
}
