// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Modstoke.Client;

internal sealed partial class ClientTerminal
{
    private readonly DroneObservedSortie _droneSortie = new();
    private long _droneSurveyGeneration = -1, _nextDroneSortieSample, _droneSurveyQueuedAt;
    private string _droneSurveyId = "", _droneSurveyFeedback = "";
    private ClientFieldPlace? _queuedDroneSurveyPin;

    private void UpdateDroneSurveySession(ClientDroneView state)
    {
        if (_droneSurveyGeneration != _osGeneration || state.DroneId.Length > 0 && _droneSurveyId != state.DroneId)
        {
            _droneSurveyGeneration = _osGeneration;
            _droneSurveyId = state.DroneId;
            _droneSortie.Reset();
            _queuedDroneSurveyPin = null;
            _droneSurveyQueuedAt = 0;
            _droneSurveyFeedback = "";
            _view?.ResetDroneSurvey();
        }
        var now = Environment.TickCount64;
        var os = _os.Snapshot;
        if (_queuedDroneSurveyPin is { } pin && os.Generation == _osGeneration && os.Field.Places.Any(place => place.Id == pin.Id))
        {
            _droneSurveyFeedback = pin.Name + " added to your private notebook.";
            _queuedDroneSurveyPin = null;
        }
        else if (_queuedDroneSurveyPin != null && now - _droneSurveyQueuedAt > 5000)
        {
            _queuedDroneSurveyPin = null;
            _droneSurveyFeedback = "Pin addition is unconfirmed. Check your notebook before retrying.";
        }
        if (now < _nextDroneSortieSample) return;
        _nextDroneSortieSample = now + 100;
        _droneSortie.Observe(state, now);
    }

    private void SaveDroneSurveyPin()
    {
        if (_app != ClientTerminalApp.Drone || !_open || _contentObscured || _view?.DroneVisible != true) return;
        var observation = Plugin.Drones.ObserveSurvey();
        var os = _os.Snapshot;
        if (!observation.Live || observation.Surface is not { } point)
        { DroneSurveyFeedback(observation.SurfaceStatus); return; }
        if (os.Generation != _osGeneration)
        { DroneSurveyFeedback("Wait for the current world's notebook."); return; }
        if (_queuedDroneSurveyPin != null)
        { DroneSurveyFeedback("Wait for the previous survey pin to reach the notebook."); return; }
        var existing = os.Field.Places.FirstOrDefault(place => place.Category == "Drone survey"
            && DronePointDistance(place.Position, point) < 2);
        if (existing != null)
        { DroneSurveyFeedback("This surface is already marked as " + existing.Name + "."); return; }
        if (os.Field.Places.Count >= 128)
        { DroneSurveyFeedback("Remove a saved place before adding a survey pin."); return; }
        var number = 1;
        while (os.Field.Places.Any(place => place.Name == "Drone survey " + number.ToString(CultureInfo.InvariantCulture))) number++;
        var name = "Drone survey " + number.ToString(CultureInfo.InvariantCulture);
        var clock = _watchSnapshot.Clock;
        var note = "Observed through the onboard camera at " + (clock.Available ? clock.DayText + " · " + clock.TimeText : "an unknown island time")
            + ". Local surface return at " + observation.SurfaceDistance.ToString("0", CultureInfo.InvariantCulture)
            + " m. This fixed observation does not establish landing clearance or a safe route.";
        var pin = new ClientFieldPlace(Guid.NewGuid(), name, point, note, "Drone survey");
        if (!_os.FieldAction(new("place", Place: pin)))
        { DroneSurveyFeedback("The notebook is busy. Try saving the observation again."); return; }
        _queuedDroneSurveyPin = pin;
        _droneSurveyQueuedAt = Environment.TickCount64;
        DroneSurveyFeedback(name + " queued for your private notebook.");
    }

    private void DroneSurveyFeedback(string message)
    {
        _droneSurveyFeedback = message;
        Feedback(message);
    }

    private static double DronePointDistance(ClientFieldPoint a, ClientFieldPoint b)
    {
        var dx = (double)a.X - b.X;
        var dy = (double)a.Y - b.Y;
        var dz = (double)a.Z - b.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private sealed class DroneObservedSortie
    {
        private readonly Queue<string> _events = new();
        private bool _started, _finished, _linked, _interrupted, _travelObserved;
        private long _lastAt, _lastContactAt;
        private ClientFieldPoint? _lastPosition;
        private double _connectedSeconds, _distance;
        private float _maximumRange, _maximumSpeed, _startCharge, _lastCharge;
        private string _mode = "", _end = "", _lastEvent = "";

        internal string Report
        {
            get
            {
                if (!_started) return "No flight observed this session.\n\nA report begins when the connected drone is observed flying. It records only received telemetry and does not reconstruct gaps.";
                return (_finished ? "LAST OBSERVED FLIGHT" : "CURRENT OBSERVED FLIGHT")
                    + " · " + (_finished ? _end : _linked ? "Receiving telemetry" : "Telemetry paused")
                    + "\nConnected time " + Duration(_connectedSeconds) + " · Observed travel "
                    + (_travelObserved ? _distance.ToString("0", CultureInfo.InvariantCulture) + " m" : "unavailable")
                    + "\nMax pilot distance " + _maximumRange.ToString("0", CultureInfo.InvariantCulture) + " m · Max speed " + _maximumSpeed.ToString("0.0", CultureInfo.InvariantCulture) + " m/s"
                    + "\nPack " + (_startCharge * 100).ToString("0", CultureInfo.InvariantCulture) + "% → " + (_lastCharge * 100).ToString("0", CultureInfo.InvariantCulture) + "%"
                    + (_interrupted ? " · Includes telemetry gaps" : " · Since first observation")
                    + "\n\n" + string.Join("\n", _events);
            }
        }

        internal void Reset()
        {
            _events.Clear();
            _started = _finished = _linked = _interrupted = _travelObserved = false;
            _lastAt = _lastContactAt = 0;
            _lastPosition = null;
            _connectedSeconds = _distance = 0;
            _maximumRange = _maximumSpeed = _startCharge = _lastCharge = 0;
            _mode = _end = _lastEvent = "";
        }

        internal void Observe(ClientDroneView state, long now)
        {
            var live = state.Ready && state.Deployed && state.Link;
            var flying = live && state.Armed && (state.Mode == "Flying" || state.Readiness?.Grounded == false);
            if (flying && (!_started || _finished))
            {
                Reset();
                _started = true;
                _startCharge = _lastCharge = state.Charge;
                Add("Flight first observed");
            }
            if (!_started || _finished) return;
            if (!live)
            {
                if (_linked) { Add("Telemetry interrupted"); _interrupted = true; }
                _linked = false;
                _lastAt = _lastContactAt = 0;
                _lastPosition = null;
                if (state.Ready && !state.Deployed) Finish("Packed away");
                return;
            }
            if (!_linked && _interrupted) Add("Telemetry resumed");
            if (_lastAt != 0 && now - _lastAt <= 1500) _connectedSeconds += Math.Max(0, now - _lastAt) / 1000d;
            else if (_lastAt != 0) { _interrupted = true; _lastPosition = null; Add("Sampling gap"); }
            _lastAt = now;
            _linked = true;
            _maximumRange = Math.Max(_maximumRange, state.Range);
            _maximumSpeed = Math.Max(_maximumSpeed, state.Speed);
            _lastCharge = state.Charge;
            if (state.LastContact is { } contact && contact.DroneId == state.DroneId && contact.AgeSeconds <= 1.5
                && contact.ObservedAt > _lastContactAt)
            {
                if (_lastPosition != null && contact.ObservedAt - _lastContactAt <= 1500)
                {
                    _distance += DronePointDistance(_lastPosition, contact.Position);
                    _travelObserved = true;
                }
                _lastPosition = contact.Position;
                _lastContactAt = contact.ObservedAt;
            }
            else if (state.LastContact == null || state.LastContact.AgeSeconds > 1.5) _lastPosition = null;
            if (_mode != state.Mode) { _mode = state.Mode; Add(_mode); }
            if (!state.Armed) Finish(state.Mode == "Landed" || state.Readiness?.Grounded == true
                ? "Landed" : "Motors stopped; touchdown unconfirmed");
        }

        private void Finish(string reason) { _finished = true; _end = reason; Add(reason); }
        private void Add(string value)
        {
            if (_lastEvent == value) return;
            _lastEvent = value;
            if (_events.Count >= 6) _events.Dequeue();
            _events.Enqueue(Duration(_connectedSeconds) + "  " + value);
        }
        private static string Duration(double seconds) => ((long)Math.Max(0, seconds) / 60).ToString("00", CultureInfo.InvariantCulture)
            + ":" + ((long)Math.Max(0, seconds) % 60).ToString("00", CultureInfo.InvariantCulture);
    }
}
