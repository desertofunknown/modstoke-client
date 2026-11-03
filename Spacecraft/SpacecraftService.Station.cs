// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using UnityEngine;

namespace Modstoke.Client;

internal readonly record struct OrbitalStationView(Vector3 Position, double Distance, double ClosingSpeed,
    bool CollisionReady, string Status, Vector3 BerthOffset = default, double RelativeSpeed = 0,
    float Alignment = 0, bool Docked = false, bool Assist = false, bool CanOperateDock = false,
    bool CanMatchVelocity = false);

internal sealed partial class ClientSpacecraftService
{
    private BoxStationModel? _orbitalStation;
    private BoxStationModel? _preparingStation;
    private Guid _stationPreparationShip;
    private int _stationPreparationFrame = -1;
    private OrbitalStationView? _stationView;
    private bool _stationFailed;
    private bool _stationAssist;
    private bool _stationPoseFailed;
    private StationRetry _stationDetailRetry, _stationLightingRetry, _stationStorageRetry, _stationEffectsRetry, _stationProjectionRetry;

    private readonly record struct StationPose(Vector3 Position, Quaternion Rotation,
        Vector3 Velocity, Vector3 AngularVelocity)
    {
        internal Vector3 BerthPosition => Position + Rotation * BoxStationDock.Position;
        internal Quaternion BerthRotation => Rotation * BoxStationDock.Rotation;
        internal Vector3 BerthVelocity => Velocity + Vector3.Cross(AngularVelocity, Rotation * BoxStationDock.Position);
    }

    // Called from Update after world readiness, including while the vessel is still on the island.
    private void UpdateOrbitalStationPreparation()
    {
        if (!_started || !OnThread) return;
        bool restoringDock = !IsClient && _restoreNavigation && _savedShip?.Docked == true;
        var ship = restoringDock ? _savedShip!.Id : _shipId;
        bool available = _ready && (restoringDock || _body?.Ready == true) && ship != Guid.Empty;
        if (restoringDock && available) _stationFailed = false;
        if (_preparingStation != null && (!available || ship != _stationPreparationShip))
            CancelOrbitalStationPreparation();
        if (!available || _stationFailed && Now < _stationProjectionRetry.After || _orbitalStation != null
            || _stationPreparationFrame == Time.frameCount) return;
        _stationPreparationFrame = Time.frameCount;
        try
        {
            if (_preparingStation == null)
            {
                _preparingStation = new BoxStationModel(BoxStationLayout.Current);
                _stationPreparationShip = ship;
                return;
            }
            if (_preparingStation.NeedsPreparationState)
            {
                if (CurrentHullState is not { } hull || CurrentStationState is not { } station
                    || CurrentPortablesState is not { } portables
                    || portables.Materials.HullRevision != hull.Revision || station.HullRevision != hull.Revision
                    || portables.Materials.CableRevision != station.Cables.Revision
                    || !StationDoorsMatch(station, portables.Materials)) return;
                _preparingStation.SetPreparationState(hull, station, portables);
                return;
            }
            if (!_preparingStation.PrepareStep()) return;
            _orbitalStation = _preparingStation;
            _preparingStation = null;
            _stationPreparationShip = Guid.Empty;
            _stationFailed = _stationPoseFailed = false;
            _stationProjectionRetry.Succeeded();
        }
        catch (Exception error) { StationProjectionFailed(error); }
    }

    private void UpdateOrbitalStation(bool teleport = false, bool restoring = false)
    {
        if (_orbitalStation is not { } model) { _stationView = null; return; }
        if (!TryStationPose(out var pose, restoring: restoring))
        {
            // Missing orbital telemetry does not prove that the crew left this deck.
            if (_body is { Ready: true, TelemetryCurrent: true } body && body.State.Region != SpacecraftRegion.Space)
                HideOrbitalStation();
            else _stationView = null;
            return;
        }
        long now = Now;
        if (_stationPoseFailed && now < _stationProjectionRetry.After) return;
        bool inRange = (pose.Position - SpacecraftLayout.SpaceOrigin).sqrMagnitude <= 4000 * 4000;
        // Keep physical geometry in the ship's bounded float domain. The orbital
        // tracer remains alive and saved when its native projection is absent.
        try
        {
            if (!inRange) model.SetVisible(false);
            else
            {
                bool snap = teleport || !model.Visible;
                model.SetPose(pose.Position, pose.Rotation, snap);
                if (_body!.State.Docked)
                    _body.SetDockPose(pose.BerthPosition, pose.BerthRotation, pose.BerthVelocity, pose.AngularVelocity, snap);
            }
            _stationPoseFailed = false;
        }
        catch (Exception error)
        {
            _stationPoseFailed = true;
            StationProjectionFailed(error);
            return;
        }
        if (_stationFailed && now < _stationProjectionRetry.After) return;
        try
        {
            var portables = CurrentPortablesState;
            if (!inRange)
            {
                if (portables is not { } carried) return;
                bool heldTool = false;
                foreach (var tool in carried.Engineering.Tools)
                    if (tool.Place == BoxStationToolPlace.Held) { heldTool = true; break; }
                if (!heldTool) return;
            }
            if (CurrentHullState is not { } hull) { model.SuspendPortableProjection(); return; }
            var materials = portables?.Materials;
            var stationState = CurrentStationState;
            bool coherent = materials is { } stock && stock.HullRevision == hull.Revision
                && stationState is { } current && current.HullRevision == hull.Revision
                && stock.CableRevision == current.Cables.Revision && StationDoorsMatch(current, stock);
            if (!coherent) { model.SuspendPortableProjection(); return; }
            if (!model.UpdateCables(stationState!.Value.Cables))
                throw new InvalidOperationException("Station cable projection rejected its current state.");
            if (!model.ApplyHull(hull))
                throw new InvalidOperationException("Station hull projection rejected its current damage state.");
            if (!model.UpdateDoors(stationState.Value))
                throw new InvalidOperationException("Station door projection rejected its current state.");
            model.UpdatePortables(portables);
            if (model.MaterialState != materials || model.PortableState != portables || !model.ConstructionReady
                || model.GeometryRetirementPending)
                throw new InvalidOperationException("Station construction projection has not completed its current state.");
            for (int door = 0; door < stationState.Value.Doors.Length; door++)
                model.SetDoor(door, IsClient ? stationState.Value.Doors[door].Amount : _stationSystems.DoorAmount(door));
            if (inRange && !model.Visible) model.SetVisible(true);
            _stationFailed = false;
            _stationProjectionRetry.Succeeded();
        }
        catch (Exception error) { StationProjectionFailed(error); }
    }

    private void RefreshStationContact()
    {
        if (!TryStationPose(out var pose)) { _stationView = null; return; }
        // Publish after PhysX completes the queued ship/station moves. Reading
        // the old ship against the next station target invents approach error.
        var flight = _body!.State;
        var separation = pose.BerthPosition - flight.Position;
        double distance = separation.magnitude;
        var velocity = pose.BerthVelocity - flight.Velocity;
        double closingSpeed = distance > .01 ? -Vector3.Dot(separation, velocity) / distance : 0;
        bool inRange = (pose.Position - SpacecraftLayout.SpaceOrigin).sqrMagnitude <= 4000 * 4000;
        bool ready = inRange && _orbitalStation?.Visible == true && !_stationFailed && StationHullCurrent;
        var failure = DockCaptureFailure(flight, pose);
        bool crewSeated = DockCrewSeated();
        bool closed = !flight.DoorOpen && flight.DoorAmount <= .001f;
        bool available = ready && flight.Travel == SpacecraftTravel.None && crewSeated && closed;
        _stationView = new(pose.BerthPosition, distance, closingSpeed, ready,
            !inRange ? "OUTSIDE LOCAL TRACKING RANGE" : !ready ? !_stationFailed && _orbitalStation == null
                ? "STATION PREPARING" : "STATION HULL UNAVAILABLE" : flight.Docked ? "LATCHED / VACUUM DOCK"
            : !crewSeated ? "CREW NOT SEATED" : failure.Length == 0 ? "CAPTURE READY" : failure,
            Quaternion.Inverse(pose.BerthRotation) * -separation, velocity.magnitude,
            Quaternion.Angle(flight.Rotation, pose.BerthRotation), flight.Docked, _stationAssist,
            available && flight.GearDeployed && flight.GearAmount >= .999f && (flight.Docked || failure.Length == 0),
            available && !flight.Docked && flight.Powered && ClientSpacecraftSystems.PropulsionAvailable(flight.Systems));
    }

    private bool TryStationPose(out StationPose pose, double ahead = 0, bool restoring = false)
    {
        pose = default;
        if (!_ready || _restoreNavigation && !restoring || _orbits == null || _spaceFrame == null
            || _body?.Ready != true || !_body.TelemetryCurrent || _body.State.Region != SpacecraftRegion.Space
            || IsClient && (_lastOrbitAt == 0 || Now - _lastOrbitAt > 30000)) return false;
        // Hulls and their latch use the same bounded replica clock and frame.
        double time = (IsClient ? _receivedFlightTime + _body.ReplicaAge : _orbits.SampleTime) + ahead;
        double dt = time - _orbits.SampleTime;
        if (!double.IsFinite(time) || time < _spaceFrameTime || Math.Abs(dt) > 30) return false;
        var frame = SpaceFrameAt(time);
        int stationIndex = OrbitalStationOrbit.Index;
        var station = StationSample(stationIndex, dt);
        var local = frame.ToLocalPosition(station.Position);
        if (!local.IsFinite || local.LengthSquared > OrbitalSimulation.MaximumPosition * OrbitalSimulation.MaximumPosition)
            return false;
        if (_earthOrbitIndex < 0)
        {
            var bodies = OrbitalCatalog.Current.Bodies;
            for (int i = 0; i < bodies.Count; i++)
                if (bodies[i].Id == 399) { _earthOrbitIndex = i; break; }
        }
        var earth = StationSample(_earthOrbitIndex, dt);
        var radial = station.Position - earth.Position;
        var velocity = station.Velocity - earth.Velocity;
        double radius = radial.Length;
        if (radius < 1) return false;
        var up = radial / radius;
        double radialSpeed = OrbitalVector.Dot(velocity, up);
        var tangent = velocity - up * radialSpeed;
        double tangentSpeed = tangent.Length;
        if (tangentSpeed < .0001) return false;
        var right = tangent / tangentSpeed;
        var forward = OrbitalVector.Cross(right, up);
        // Differentiate the controlled attitude basis so releasing an offset
        // berth retains both orbital velocity and the deck's rotational motion.
        var acceleration = _orbits.Acceleration(stationIndex) - _orbits.Acceleration(_earthOrbitIndex);
        var upRate = tangent / radius;
        var tangentRate = acceleration - upRate * radialSpeed
            - up * (OrbitalVector.Dot(acceleration, up) + OrbitalVector.Dot(velocity, upRate));
        var rightRate = (tangentRate - right * OrbitalVector.Dot(tangentRate, right)) / tangentSpeed;
        var forwardRate = OrbitalVector.Cross(rightRate, up) + OrbitalVector.Cross(right, upRate);
        var spin = (OrbitalVector.Cross(right, rightRate) + OrbitalVector.Cross(up, upRate)
            + OrbitalVector.Cross(forward, forwardRate)) * .5;
        var rotation = Quaternion.LookRotation(StationVector(frame.ToLocalDirection(forward)),
            StationVector(frame.ToLocalDirection(up)));
        pose = new(SpacecraftLayout.SpaceOrigin + StationVector(local), rotation,
            StationVector(frame.ToLocalVelocity(station.Position, station.Velocity)),
            StationVector(frame.ToLocalDirection(spin - frame.AngularVelocity)));
        return Finite(pose.Position) && Finite(pose.Velocity) && Finite(pose.AngularVelocity);
    }

    private OrbitalState StationSample(int index, double dt)
    {
        var sample = _orbits!.Sample(index);
        var acceleration = _orbits.Acceleration(index);
        return new(sample.Id, sample.Position + (sample.Velocity + acceleration * (.5 * dt)) * dt,
            sample.Velocity + acceleration * dt);
    }

    private static Vector3 StationVector(OrbitalVector value) => new((float)value.X, (float)value.Y, (float)value.Z);

    private void UpdateOrbitalStationView()
    {
        if (_orbitalStation is not { } model) return;
        long now = Now;
        // Storage closures carry colliders and must keep moving when unrelated detail or lighting fails.
        if (now >= _stationStorageRetry.After)
        {
            try
            {
                model.UpdateStorageMotion();
                _stationStorageRetry.Succeeded();
            }
            catch (Exception error) { _stationStorageRetry.Failed(now, "storage motion", error); }
        }
        if (now >= _stationDetailRetry.After)
        {
            try
            {
                var camera = TheForest.Utils.LocalPlayer.MainCam;
                if (camera != null && camera) model.UpdateDetail(camera.transform.position);
                _stationDetailRetry.Succeeded();
            }
            catch (Exception error) { _stationDetailRetry.Failed(now, "detail", error); }
        }
        if (now >= _stationLightingRetry.After)
        {
            try
            {
                var camera = TheForest.Utils.LocalPlayer.MainCam;
                if (camera != null && camera) model.UpdatePower(CurrentStationState, camera.transform.position);
                _stationLightingRetry.Succeeded();
            }
            catch (Exception error) { _stationLightingRetry.Failed(now, "lighting", error); }
        }
        if (now >= _stationEffectsRetry.After)
        {
            try
            {
                if (model.Visible) _stationFireEffects ??= new BoxStationFireEffects(model);
                _stationFireEffects?.Update(CurrentStationState);
                _stationEffectsRetry.Succeeded();
            }
            catch (Exception error) { _stationEffectsRetry.Failed(now, "fire effects", error); }
        }
    }

    private struct StationRetry
    {
        internal long After;
        private long _nextLog;
        private int _failures;

        internal void Succeeded() { After = 0; _failures = 0; }

        internal void Failed(long now, string operation, Exception error)
        {
            _failures = Math.Min(_failures + 1, 5);
            After = now + (500L << (_failures - 1));
            if (now < _nextLog) return;
            _nextLog = now + 30000;
            Console.WriteLine("Orbital station " + operation + " update failed: " + error);
        }
    }

    private void StationProjectionFailed(Exception error)
    {
        _stationFailed = true;
        if (_restoreNavigation)
        {
            ReleaseOrbitalStation();
            throw new InvalidOperationException("The saved orbital station projection could not be restored.", error);
        }
        _orbitalStation?.SuspendPortableProjection();
        if (_orbitalStation == null) CancelOrbitalStationPreparation();
        _stationAssist = false;
        _stationProjectionRetry.Failed(Now, "structure", error);
    }

    private void HideOrbitalStation()
    {
        _body?.SetDockedStation(null);
        _orbitalStation?.SetVisible(false);
        _stationView = null;
    }

    private void ReleaseOrbitalStation()
    {
        _stationDetailRetry = _stationLightingRetry = _stationStorageRetry = _stationEffectsRetry = _stationProjectionRetry = default;
        _stationPoseFailed = false;
        CancelOrbitalStationPreparation();
        _shorePower?.DetachStation();
        ReleaseFirefightingViews();
        _body?.SetDockedStation(null);
        _stationControls?.Dispose();
        _stationControls = null;
        _stationPowerControls?.Dispose();
        _stationPowerControls = null;
        _stationSmesControls?.Dispose();
        _stationSmesControls = null;
        _stationPlumbingControls?.Dispose();
        _stationPlumbingControls = null;
        _stationAirAlarmControls?.Dispose();
        _stationAirAlarmControls = null;
        _stationFireAlarmControls?.Dispose();
        _stationFireAlarmControls = null;
        _stationRoomThermalControls?.Dispose();
        _stationRoomThermalControls = null;
        _fabricationControls?.Dispose();
        _fabricationControls = null;
        _stationFireEffects?.Dispose();
        _stationFireEffects = null;
        var model = _orbitalStation;
        _orbitalStation = null;
        _stationView = null;
        try { model?.Dispose(); }
        catch (Exception error) { Console.WriteLine("Orbital station cleanup failed: " + error); }
    }

    private void CancelOrbitalStationPreparation()
    {
        var preparing = _preparingStation;
        _preparingStation = null;
        _stationPreparationShip = Guid.Empty;
        _stationPreparationFrame = -1;
        try { preparing?.Dispose(); }
        catch (Exception error) { Console.WriteLine("Orbital station preparation cleanup failed: " + error); }
    }
}
