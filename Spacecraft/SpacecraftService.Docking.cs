// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftService
{
    private bool _migrateStationDock;
    private Action<BoxStationSystems, float>? _serviceDockedShip;

    private void UpdateShoreConnection()
    {
        bool connected = _ready && !IsClient && _body?.Ready == true && _body.TelemetryCurrent
            && _body.State.Docked && _body.State.Travel == SpacecraftTravel.None
            && _orbitalStation?.Visible == true && !_stationFailed && TryStationPose(out var dock)
            && (_body.State.Position - dock.BerthPosition).sqrMagnitude <= .35f * .35f
            && Quaternion.Angle(_body.State.Rotation, dock.BerthRotation) <= 3;
        _systems.SetShoreConnection(connected ? _epoch : Guid.Empty, connected ? _shipId : Guid.Empty);
    }

    private void ServiceDockedShip(BoxStationSystems station, float dt)
    {
        if (_body == null) return;
        // The exterior deck is wired to the original Escape Hallway APC. Door
        // reconstruction changes dense indices, so retain the source-layout cell.
        var layout = BoxStationLayout.Current;
        int area = layout.Cells[layout.Doors[BoxStationDock.Door].Cell].Area;
        if (_systems.ServiceFromStation(station, area, SpacecraftShoreFuel.SupplyTank, SpacecraftShoreCoolant.SupplyTank, _body.State, dt))
            MarkEngineeringDirty();
    }

    private static string DockCaptureFailure(SpacecraftFlightState flight, StationPose dock)
    {
        if (flight.Travel != SpacecraftTravel.None) return "JOURNEY ACTIVE";
        if (!flight.GearDeployed || flight.GearAmount < .999f) return "DEPLOY LANDING GEAR";
        if (flight.DoorOpen || flight.DoorAmount > .001f) return "CLOSE REAR RAMP";
        if ((flight.Position - dock.BerthPosition).sqrMagnitude > .35f * .35f) return "ALIGN WITH BERTH";
        if (Quaternion.Angle(flight.Rotation, dock.BerthRotation) > 3) return "ALIGN SHIP ATTITUDE";
        if ((flight.Velocity - dock.BerthVelocity).sqrMagnitude > .3f * .3f) return "REDUCE RELATIVE SPEED";
        if ((flight.AngularVelocity - dock.AngularVelocity).sqrMagnitude > .02f * .02f) return "STOP ROTATION";
        return "";
    }

    private bool DockCrewSeated()
    {
        if (IsClient)
        {
            foreach (var walking in _walking) if (walking) return false;
            return !_clientWalking;
        }
        foreach (var peer in _seats)
            if (peer != null && (!peer.Attached || peer.Walking || !peer.Prepared)) return false;
        return !HasUnseatedCabinPlayer();
    }

    private string ExecuteDocking(Peer peer, ActionCode action, Vector3 actorPosition)
    {
        if (_body == null || peer.Seat != 0 || !ReferenceEquals(_seats[0], peer)
            || !peer.Attached || peer.Walking || !peer.Prepared || peer.Lease == System.Guid.Empty
            || (_body.Root.transform.TransformPoint(SpacecraftLayout.Seat(0)) - actorPosition).sqrMagnitude > 9)
            return "Only the seated pilot can operate the docking controls.";
        var flight = _body.State;
        if (flight.Region != SpacecraftRegion.Space || flight.Travel != SpacecraftTravel.None)
            return "Finish the journey in space before using station docking.";
        if (action == ActionCode.StationAssist && _stationAssist)
        {
            // Hold the current inertial velocity when leaving station-relative
            // flight. Changing this reference must travel with a new descriptor revision.
            var frame = CurrentSpaceFrame();
            _spaceCruiseVelocity = frame.ToInertialVelocity(FramePosition(flight),
                new(flight.Velocity.x, flight.Velocity.y, flight.Velocity.z));
            _stationAssist = false;
            _frameRevision = checked(_frameRevision + 1);
            DockingGesture(peer, SpacecraftConsoleControl.StationAssist);
            return "Holding the current inertial velocity. Station matching disengaged.";
        }
        if (!TryStationPose(out var dock) || _orbitalStation?.Visible != true || _stationFailed)
            return "Current station collision and navigation data are required.";
        if (!CrewReady(out var reason)) return "Docking refused: " + reason;
        if (action == ActionCode.StationAssist)
        {
            if (flight.Docked) return "Release the docking latch before matching station velocity.";
            if (!flight.Powered || !_systems.CanThrust) return "Switch on the engine and restore propulsion first.";
            _stationAssist = true;
            _body.SetPower(true, true);
            DockingGesture(peer, SpacecraftConsoleControl.StationAssist);
            return "Dampeners now match station velocity. Align with the berth using the thrusters.";
        }
        if (flight.Docked)
        {
            if (!flight.GearDeployed || flight.GearAmount < .999f) return "Fully deploy the landing gear before releasing the latch.";
            // The latch has retained end-of-step velocities. Release without
            // queuing another kinematic move or resetting the completed pose.
            if (!_body.Undock()) return "The docking latch could not be released.";
            _systems.SetShoreConnection(Guid.Empty, Guid.Empty);
            _body.SetPower(false, true);
            _stationAssist = true;
            DockingGesture(peer, SpacecraftConsoleControl.StationDock);
            return "Docking latch released. Orbital velocity retained; switch on the engine to depart.";
        }
        var failure = DockCaptureFailure(flight, dock);
        if (failure.Length != 0) return failure + ". Capture requires 0.35 m, 0.3 m/s and 3 degrees or less.";
        if (!_body.TryDock(dock.BerthPosition, dock.BerthRotation, dock.BerthVelocity, dock.AngularVelocity))
            return "Capture refused. Clear the berth and the ship's hull before engaging the latch.";
        _stationAssist = false;
        UpdateShoreConnection();
        Physics.SyncTransforms();
        RecordMovementEvidence(true);
        DockingGesture(peer, SpacecraftConsoleControl.StationDock);
        return "Docking latch engaged. Engines inhibited. Depressurize the entry before opening the ramp; the dock is in vacuum.";
    }

    private void DockingGesture(Peer peer, SpacecraftConsoleControl control)
    {
        ReplayConsolePose(peer, control);
        _nextSnapshot = 0;
    }

    private void RestoreDockedVessel()
    {
        if (_savedShip?.Docked != true) return;
        if (_body == null || !TryStationPose(out var dock, restoring: true) || _orbitalStation?.Visible != true)
            throw new System.InvalidOperationException("The saved station berth is unavailable.");
        // Legacy station geometry used a different berth. Validate that saved
        // latch before transferring the unoccupied vessel to the replacement dock.
        var savedPosition = _migrateStationDock ? dock.Position + dock.Rotation * new Vector3(0, 3.6425f, 54) : dock.BerthPosition;
        var savedRotation = _migrateStationDock ? dock.Rotation : dock.BerthRotation;
        if ((_body.State.Position - savedPosition).sqrMagnitude > 1
            || Quaternion.Angle(_body.State.Rotation, savedRotation) > 1
            || !_body.TryDock(dock.BerthPosition, dock.BerthRotation, dock.BerthVelocity, dock.AngularVelocity))
            throw new System.InvalidOperationException("The saved docking berth is unavailable or disagrees with the vessel position.");
    }
}
