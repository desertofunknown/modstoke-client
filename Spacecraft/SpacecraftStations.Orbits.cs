// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using TMPro;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftStations
{
    private const double AstronomicalUnit = 149597870700;
    private const double EclipticObliquity = 23.4392911 * Math.PI / 180;
    private const float SystemProjectionScale = 1.1f;
    private static readonly Vector3 SystemProjectionPosition = new(-.45f, .55f, -2.0f);
    private readonly Dictionary<int, OrbitalState> _orbitStates = new();
    private readonly List<OrbitMarker> _orbitMarkers = new();
    private Transform _orbitRoot = null!, _moonDetail = null!;
    private TextMeshProUGUI _orbitWaiting = null!, _moonDetailTitle = null!;
    private OrbitMarker _detailEarth = null!, _detailMoon = null!;
    private LineRenderer _moonSeparation = null!;
    private Transform _stationContact = null!;
    private TextMeshProUGUI _stationRange = null!, _stationClosure = null!, _stationStatus = null!;
    private bool _orbitHasTick, _orbitReady, _moonDetailReady;
    private long _orbitTick;
    private string _orbitCaption = "AWAITING ORBIT DATA";

    private void BuildSystemChart(Transform parent)
    {
        // All chart objects are children of the station root. Lines and text use
        // station-owned shared materials, released by Dispose with the other UI.
        _orbitRoot = ChartNode(parent, "Live solar positions");
        _orbitWaiting = MapLabel(parent, "AWAITING ORBIT DATA", Muted, new Vector3(0, .08f, 0));
        _orbitWaiting.fontSize = 17;
        var ids = new[] { 10, 199, 299, 399, 499, 599, 699, 799, 899, 999 };
        var names = new[] { "SUN", "MERCURY", "VENUS", "EARTH", "MARS", "JUPITER", "SATURN", "URANUS", "NEPTUNE", "PLUTO" };
        for (var index = 0; index < ids.Length; index++)
        {
            var label = index < 5 ? new Vector3(-.30f, .08f, .20f - index * .10f)
                : new Vector3(.30f, .08f, .20f - (index - 5) * .10f);
            _orbitMarkers.Add(AddOrbitMarker(_orbitRoot, ids[index], names[index], label, index == 0 ? .011f : .006f));
        }
        _moonDetail = ChartNode(_orbitRoot, "Earth Moon detail");
        _moonDetail.localPosition = new Vector3(-.45f, .06f, -.33f);
        _detailEarth = AddOrbitMarker(_moonDetail, 399, "EARTH", new Vector3(-.13f, .01f, -.035f), .007f);
        _detailMoon = AddOrbitMarker(_moonDetail, 301, "MOON", new Vector3(-.13f, .01f, .035f), .005f);
        _moonSeparation = Line(_moonDetail, "Earth Moon separation", new Vector3[2], false, _orbitLine, .0007f);
        _moonDetailTitle = MapLabel(_moonDetail, "EARTH / MOON DETAIL", Muted, new Vector3(0, .01f, -.065f));
        _moonDetailTitle.fontSize = 15;
        BuildStationContact(parent);
        _orbitRoot.gameObject.SetActive(false);
    }

    private void BuildStationContact(Transform parent)
    {
        // Keep this local contact below the system name rails and opposite the
        // Earth/Moon inset; its icon is a readout, not a solar-position marker.
        _stationContact = ChartNode(parent, "Orbital station contact");
        _stationContact.localPosition = new Vector3(.34f, .08f, -.32f);
        var icon = ChartNode(_stationContact, "Station contact icon");
        icon.localPosition = new Vector3(-.15f, 0, 0);
        Ring(icon, "Station contact ring", .011f, Vector3.right, Vector3.forward, _cyanLine, .0012f);
        Line(_stationContact, "Station contact leader", new[] { new Vector3(-.135f, 0, 0), new Vector3(-.115f, 0, 0) },
            false, _orbitLine, .0007f);
        ContactLabel("ORBITAL STATION", NavigationCyan, 0);
        _stationRange = ContactLabel("RANGE", NavigationCyan, -.04f);
        _stationClosure = ContactLabel("RELATIVE SPEED", NavigationCyan, -.08f);
        _stationStatus = ContactLabel("UNAVAILABLE", Muted, -.12f);
        _stationContact.gameObject.SetActive(false);

        TextMeshProUGUI ContactLabel(string title, Color color, float z)
        {
            var label = MapLabel(_stationContact, title, color, new Vector3(0, 0, z));
            label.fontSize = 18;
            label.rectTransform.sizeDelta = new Vector2(360, 40);
            label.transform.parent.GetComponent<RectTransform>().sizeDelta = new Vector2(360, 40);
            return label;
        }
    }

    private void UpdateStationContact(OrbitalStationView? contact)
    {
        _stationContact.gameObject.SetActive(contact.HasValue);
        if (!contact.HasValue) return;
        var station = contact.Value;
        _stationRange.text = "RANGE " + (station.Distance < 1000
            ? station.Distance.ToString("N0", CultureInfo.InvariantCulture) + " m"
            : (station.Distance / 1000).ToString("N1", CultureInfo.InvariantCulture) + " km");
        _stationClosure.text = Math.Abs(station.ClosingSpeed) < .05 ? "RELATIVE 0.0 m/s"
            : (station.ClosingSpeed > 0 ? "CLOSING " : "OPENING ")
                + Math.Abs(station.ClosingSpeed).ToString("N1", CultureInfo.InvariantCulture) + " m/s";
        _stationStatus.text = station.Status;
        _stationStatus.color = station.CollisionReady ? Amber : Muted;
    }

    private OrbitMarker AddOrbitMarker(Transform parent, int id, string name, Vector3 labelPosition, float radius)
    {
        var node = ChartNode(parent, name + " position");
        var material = id == 10 ? _amberLine : _cyanLine;
        Ring(node, "Body marker", radius, Vector3.right, Vector3.forward, material, .0016f);
        Ring(node, "Body elevation", radius, Vector3.right, Vector3.up, material, .001f);
        var selected = Ring(node, "Selected destination", radius + .005f, Vector3.right, Vector3.forward, _amberLine, .0016f).transform;
        var current = Ring(node, "Current region", radius + .009f, Vector3.right, Vector3.forward, _selectedLine, .001f).transform;
        selected.gameObject.SetActive(false); current.gameObject.SetActive(false);
        var title = MapLabel(parent, name, NavigationCyan, labelPosition);
        title.fontSize = 18;
        var leaderEnd = labelPosition + Vector3.right * (labelPosition.x < 0 ? .045f : -.045f);
        var leader = Line(parent, name + " label leader", new[] { Vector3.zero, leaderEnd }, false, _orbitLine, .0005f);
        return new OrbitMarker(id, name, node, title, leader, selected, current);
    }

    private static Transform ChartNode(Transform parent, string name)
    {
        var node = new GameObject(name) { layer = parent.gameObject.layer };
        node.transform.SetParent(parent, false);
        return node.transform;
    }

    private void UpdateSystemChart(ClientSpacecraftView view)
    {
        UpdateStationContact(view.Station);
        var orbit = view.Orbit;
        if (orbit == null || !double.IsFinite(orbit.EpochJulianDateTdb))
        {
            _orbitHasTick = _orbitReady = _moonDetailReady = false;
            _orbitStates.Clear();
            _orbitRoot.gameObject.SetActive(false);
            _orbitWaiting.transform.parent.gameObject.SetActive(true);
            _orbitCaption = "AWAITING ORBIT DATA";
            return;
        }
        if (!_orbitHasTick || orbit.Tick != _orbitTick)
            RefreshSystemPositions(orbit);
        if (!_orbitReady) return;

        var systems = view.Flight.Systems;
        var selected = systems.Ready ? OrbitDestination(systems.SelectedDestination) : 0;
        var current = systems.Ready ? OrbitDestination(view.Flight.Region) : 0;
        foreach (var marker in _orbitMarkers)
        {
            // The Moon shares Earth's unresolved solar-system marker; the detail
            // below separates their actual positions without inventing a route.
            UpdateOrbitSelection(marker, selected == 301 ? 399 : selected, current == 301 ? 399 : current);
        }
        var showDetail = _moonDetailReady && (selected is 399 or 301 || current is 399 or 301);
        _moonDetail.gameObject.SetActive(showDetail);
        if (showDetail)
        {
            UpdateOrbitSelection(_detailEarth, selected, current);
            UpdateOrbitSelection(_detailMoon, selected, current);
            _moonSeparation.sharedMaterial = current != selected && (current is 399 or 301) && (selected is 399 or 301)
                ? _amberLine : _orbitLine;
        }
    }

    private void RefreshSystemPositions(OrbitalChartView orbit)
    {
        _orbitTick = orbit.Tick;
        _orbitHasTick = true;
        _orbitStates.Clear();
        foreach (var state in orbit.States)
            if (state.Position.IsFinite) _orbitStates[state.Id] = state;
        if (!_orbitStates.TryGetValue(10, out var sun))
        {
            _orbitReady = _moonDetailReady = false;
            _orbitRoot.gameObject.SetActive(false);
            _orbitWaiting.transform.parent.gameObject.SetActive(true);
            _orbitCaption = "AWAITING ORBIT DATA / SUN POSITION UNAVAILABLE";
            return;
        }
        var extent = 50 * AstronomicalUnit;
        foreach (var marker in _orbitMarkers)
        {
            if (_orbitStates.TryGetValue(marker.Id, out var state))
            {
                var distance = (state.Position - sun.Position).Length;
                if (double.IsFinite(distance)) extent = Math.Max(extent, distance);
            }
        }
        var shown = 0;
        foreach (var marker in _orbitMarkers)
        {
            var hasState = _orbitStates.TryGetValue(marker.Id, out var state);
            var relative = state.Position - sun.Position;
            var distance = relative.Length;
            marker.Available = hasState && double.IsFinite(distance) && (distance > 0 || marker.Id == 10);
            marker.Node.gameObject.SetActive(marker.Available);
            marker.Leader.gameObject.SetActive(marker.Available);
            marker.Title.text = marker.Available ? marker.Name : marker.Name + " / NO DATA";
            if (!marker.Available) continue;
            // Compress radius only; the three-dimensional direction is preserved.
            // Marker sizes and name rails are presentation, not physical body radii.
            var radius = .23 * Math.Log(1 + distance / (.1 * AstronomicalUnit))
                / Math.Log(1 + extent / (.1 * AstronomicalUnit));
            var position = distance > 0 ? EclipticPosition(relative, radius / distance) : Vector3.zero;
            SetOrbitPosition(marker, position + new Vector3(0, .06f, 0));
            shown++;
        }
        _moonDetailReady = _orbitStates.TryGetValue(399, out var earth) && _orbitStates.ContainsKey(301);
        if (_moonDetailReady)
        {
            var separation = _orbitStates[301].Position - earth.Position;
            var distance = separation.Length;
            _moonDetailReady = double.IsFinite(distance) && distance > 0;
            if (_moonDetailReady)
            {
                var offset = EclipticPosition(separation, .065 / distance);
                SetOrbitPosition(_detailEarth, -offset * .5f);
                SetOrbitPosition(_detailMoon, offset * .5f);
                _detailEarth.Available = _detailMoon.Available = true;
                _moonSeparation.SetPosition(0, _detailEarth.Node.localPosition);
                _moonSeparation.SetPosition(1, _detailMoon.Node.localPosition);
                _moonDetailTitle.text = "ENLARGED / " + (distance / 1000).ToString("N0", CultureInfo.InvariantCulture) + " km";
            }
        }
        var covered = 0;
        foreach (var body in orbit.Bodies)
            if (_orbitStates.ContainsKey(body.Id)) covered++;
        var julianDate = orbit.EpochJulianDateTdb + orbit.Tick / 86400.0;
        _orbitCaption = "JD " + julianDate.ToString("F5", CultureInfo.InvariantCulture) + " TDB / "
            + covered.ToString(CultureInfo.InvariantCulture) + "/" + orbit.Bodies.Count.ToString(CultureInfo.InvariantCulture)
            + " TRACKED BODIES / SUN-RELATIVE / J2000 ECLIPTIC / " + shown.ToString(CultureInfo.InvariantCulture) + " CONTACTS";
        _orbitReady = true;
        _orbitWaiting.transform.parent.gameObject.SetActive(false);
        _orbitRoot.gameObject.SetActive(true);
    }

    private static Vector3 EclipticPosition(OrbitalVector equatorial, double scale)
    {
        var cosine = Math.Cos(EclipticObliquity);
        var sine = Math.Sin(EclipticObliquity);
        return new Vector3((float)(equatorial.X * scale),
            (float)((equatorial.Z * cosine - equatorial.Y * sine) * scale),
            (float)((equatorial.Y * cosine + equatorial.Z * sine) * scale));
    }

    private static void SetOrbitPosition(OrbitMarker marker, Vector3 position)
    {
        marker.Node.localPosition = position;
        marker.Leader.SetPosition(0, position);
    }

    private static int OrbitDestination(SpacecraftRegion region) => region switch
    {
        SpacecraftRegion.Island => 399,
        SpacecraftRegion.Moon => 301,
        _ => 0
    };

    private static void UpdateOrbitSelection(OrbitMarker marker, int selected, int current)
    {
        marker.Selected.gameObject.SetActive(marker.Available && marker.Id == selected);
        marker.Current.gameObject.SetActive(marker.Available && marker.Id == current);
        marker.Title.color = !marker.Available ? Muted : marker.Id == selected ? Amber
            : marker.Id == current ? Green : NavigationCyan;
    }

    private sealed class OrbitMarker
    {
        internal readonly int Id;
        internal readonly string Name;
        internal readonly Transform Node, Selected, Current;
        internal readonly TextMeshProUGUI Title;
        internal readonly LineRenderer Leader;
        internal bool Available;

        internal OrbitMarker(int id, string name, Transform node, TextMeshProUGUI title,
            LineRenderer leader, Transform selected, Transform current)
        {
            Id = id; Name = name; Node = node; Title = title;
            Leader = leader; Selected = selected; Current = current;
        }
    }
}
