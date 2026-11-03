// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Immutable;
using System.Globalization;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftService
{
    private BoxStationSystems _stationSystems = new(BoxStationLayout.Current);
    private BoxStationState? _stationState;
    private OrbitalStationControls? _stationControls;
    private BoxStationPowerControls? _stationPowerControls;
    private BoxStationSmesControls? _stationSmesControls;
    private BoxStationPlumbingControls? _stationPlumbingControls;
    private BoxStationAirAlarmControls? _stationAirAlarmControls;
    private BoxStationFireAlarmControls? _stationFireAlarmControls;
    private BoxStationRoomThermalControls? _stationRoomThermalControls;
    private BoxStationFireEffects? _stationFireEffects;
    private long _nextStationSnapshot, _lastStationAt, _stationSequence, _receivedStationSequence;
    private BoxStationHullState _checkedRoomHull;
    private ImmutableArray<BoxStationRoomSpan> _checkedRoomSpans;
    private ImmutableArray<int> _checkedRoomDoors;
    private int _checkedRoomCount;
    private bool _stationRoomsMatch;

    private BoxStationState? CurrentStationState
    {
        get
        {
            if (!_ready || IsClient && (_lastStationAt == 0 || Now - _lastStationAt > _stationFreshness)
                || _stationState is not { } station || CurrentHullState is not { } hull
                || station.HullRevision != hull.Revision
                || CurrentMaterialsState is not { } materials || materials.HullRevision != hull.Revision
                || station.Cables.Revision != materials.CableRevision
                || !StationDoorsMatch(station, materials)
                || station.RoomSpans.IsDefault || station.Zones.IsDefault) return null;
            if (_checkedRoomHull.Revision != hull.Revision || _checkedRoomCount != station.Zones.Length
                || !_checkedRoomHull.Panels.AsSpan().SequenceEqual(hull.Panels.AsSpan())
                || !_checkedRoomSpans.AsSpan().SequenceEqual(station.RoomSpans.AsSpan())
                || !_checkedRoomDoors.AsSpan().SequenceEqual(station.BuiltDoors.AsSpan()))
            {
                _checkedRoomHull = hull;
                _checkedRoomSpans = station.RoomSpans;
                _checkedRoomDoors = station.BuiltDoors;
                _checkedRoomCount = station.Zones.Length;
                _stationRoomsMatch = BoxStationRooms.Matches(BoxStationLayout.Current, hull, station.RoomSpans,
                    station.Zones.Length, station.BuiltDoors);
            }
            return _stationRoomsMatch ? station : null;
        }
    }

    private bool StationHullCurrent => !_stationFailed && !_stationPoseFailed
        && _orbitalStation is { ConstructionReady: true, GeometryRetirementPending: false }
        && CurrentStationState is { } station && CurrentHullState is { } hull
        && station.HullRevision == hull.Revision
        && CurrentMaterialsState is { } materials && materials.HullRevision == hull.Revision
        && station.Cables.Revision == materials.CableRevision
        && StationDoorsMatch(station, materials)
        && _orbitalStation?.HullState.Revision == hull.Revision && _orbitalStation.CableRevision == station.Cables.Revision
        && _orbitalStation.FloorRevision == materials.Floors.Revision
        && _orbitalStation.DoorRevision == station.DoorRevision && _orbitalStation.MaterialState == materials;

    private static bool StationDoorsMatch(BoxStationState station, BoxStationMaterialState materials)
    {
        if (station.DoorRevision != materials.DoorConstruction.Revision || station.BuiltDoors.IsDefault
            || materials.DoorConstruction.Assemblies.IsDefault) return false;
        int complete = 0;
        foreach (var assembly in materials.DoorConstruction.Assemblies)
            if (assembly.Stage == BoxStationDoorStage.Complete)
            {
                if (complete >= station.BuiltDoors.Length || station.BuiltDoors[complete++] != assembly.Edge) return false;
            }
        return complete == station.BuiltDoors.Length;
    }

    private void ResetStationSystems()
    {
        _systems.SetShoreConnection(Guid.Empty, Guid.Empty);
        _stationSystems = new BoxStationSystems(BoxStationLayout.Current);
        _migrateStationDock = false;
        ResetStationTransport();
        ResetPortableTransport();
        ResetHullTransport();
        _nextHeldFirefighting = 0;
        _stationState = null;
        _checkedRoomHull = default;
        _checkedRoomSpans = default;
        _checkedRoomDoors = default;
        _checkedRoomCount = 0;
        _stationRoomsMatch = false;
        _nextStationSnapshot = _lastStationAt = _stationSequence = _receivedStationSequence = 0;
        _stationSolarSample = double.NaN;
        _stationRadiation = default;
    }

    private void StepStationSystems(float dt)
    {
        FlushStationSnapshots();
        FlushHullSnapshots();
        UpdateShoreConnection();
        if (IsClient) return;
        Span<int> occupantCells = stackalloc int[64];
        int occupantCount = 0;
        Span<bool> obstructed = stackalloc bool[_stationSystems.DoorCount];
        obstructed.Clear();
        if (_orbitalStation?.Visible == true)
        {
            var up = _orbitalStation.Root.transform.up;
            foreach (var peer in _seats)
            {
                if (peer == null || !peer.Walking || WearingEva(peer)
                    || !TrySurvivalActor(peer, out var point, out _)) continue;
                int cell = _orbitalStation.AtmosphereCellAt(CrewPosition(peer, point) + up * .9f);
                if (cell >= 0 && occupantCount < occupantCells.Length) occupantCells[occupantCount++] = cell;
            }
            // Obstructions include current actors without a berth. Door safety
            // must not depend on whether someone is authorized to operate it.
            Span<Vector3> feet = stackalloc Vector3[17];
            int count = 0;
            if (_local != null && (TryActor(_local, out var local, out _, out _)
                || TrySurvivalActor(_local, out local, out _))) feet[count++] = CrewPosition(_local, local);
            foreach (var peer in _peers.Values)
                if (count < feet.Length && (TryActor(peer, out var point, out _, out _)
                    || TrySurvivalActor(peer, out point, out _))) feet[count++] = point;
            // Fire alarms can begin closing an open door during this step.
            if (_stationFailed || _orbitalStation.GeometryRetirementPending
                || _orbitalStation.DoorRevision != _stationSystems.Repair.DoorConstruction.Revision) obstructed.Fill(true);
            else
                for (int door = 0; door < obstructed.Length; door++)
                    if (_stationSystems.DoorAmount(door) > 0
                        && (!_stationSystems.DoorOpen(door) || _stationSystems.DoorDefinitions[door].Kind == "firedoor"))
                        obstructed[door] = _orbitalStation.DoorObstructed(door, feet[..count]);
        }
        ValidateEngineeringWork();
        long hullRevision = _stationSystems.Hull.State.Revision;
        int workCount = _stationSystems.Engineering.ActiveWorkCount;
        _stationSystems.Step(dt, occupantCells[..occupantCount], obstructed, StationRadiationEnvironment(),
            _serviceDockedShip ??= ServiceDockedShip);
        if (_stationSystems.Hull.State.Revision != hullRevision || _stationSystems.Engineering.ActiveWorkCount != workCount)
            PublishEngineeringChanges(hullRevision);
        ResolveFoamSeals();
        if (_orbitalStation?.Visible == true && !_stationFailed)
        {
            try
            {
                if (_orbitalStation.DoorRevision != _stationSystems.Repair.DoorConstruction.Revision
                    && _orbitalStation.HullState.Revision == _stationSystems.Hull.State.Revision
                    && _orbitalStation.CableRevision == _stationSystems.Power.CableState.Revision)
                {
                    if (!_orbitalStation.UpdateDoors(_stationSystems.State))
                        throw new InvalidOperationException("Station door projection rejected its current state.");
                    var portables = _stationSystems.Portables;
                    _orbitalStation.UpdatePortables(portables);
                    if (_orbitalStation.PortableState != portables)
                        throw new InvalidOperationException("Station portable projection rejected its current state.");
                }
                if (_orbitalStation.DoorRevision == _stationSystems.Repair.DoorConstruction.Revision)
                    for (int door = 0; door < _stationSystems.DoorCount; door++)
                        _orbitalStation.SetDoor(door, _stationSystems.DoorAmount(door));
            }
            catch (Exception error) { StationProjectionFailed(error); }
        }
        bool topologyChanged = _stationState is not { } previous
            || previous.HullRevision != _stationSystems.Hull.State.Revision
            || previous.Cables.Revision != _stationSystems.Power.CableState.Revision
            || previous.DoorRevision != _stationSystems.Repair.DoorConstruction.Revision;
        if (topologyChanged) _stationState = _stationSystems.State;
        if (Now < _nextStationSnapshot || StationSnapshotPending) return;
        _nextStationSnapshot = Now + 1000;
        if (!topologyChanged) _stationState = _stationSystems.State;
        QueueStationSnapshot(_stationState!.Value);
    }

    private void UpdateStationBoarding()
    {
        var station = _orbitalStation;
        var state = StationHullCurrent ? CurrentStationState : null;
        if (station?.Visible == true && state is { } snapshot && IsClient)
        {
            try
            {
                if (!station.UpdateDoors(snapshot))
                    throw new InvalidOperationException("Station door projection rejected its current state.");
                for (int door = 0; door < snapshot.Doors.Length; door++)
                    station.SetDoor(door, snapshot.Doors[door].Amount);
            }
            catch (Exception error)
            {
                StationProjectionFailed(error);
                state = null;
            }
        }
        if (_body is { } body)
            body.SetDockedStation(station?.Visible == true && (state.HasValue || body.State.Docked) ? station : null);
    }

    private void UpdateStationControls()
    {
        if (_orbitalStation?.Visible != true) { CancelShoreServicePress(); UpdateFabricationControls(false); return; }
        _stationControls ??= new OrbitalStationControls(_orbitalStation, door => Command("station-door-" + door));
        _stationPowerControls ??= new BoxStationPowerControls(_orbitalStation, (apc, channel) =>
            Command((channel == BoxStationPowerChannel.Equipment ? "station-equipment-"
                : channel == BoxStationPowerChannel.Lighting ? "station-lighting-" : "station-environment-") + apc));
        _stationSmesControls ??= new BoxStationSmesControls(_orbitalStation, (device, control) =>
            Command("station-smes-" + device + "-" + (byte)control));
        _stationPlumbingControls ??= new BoxStationPlumbingControls(_orbitalStation, command => Command(command));
        _stationAirAlarmControls ??= new BoxStationAirAlarmControls(_orbitalStation, (alarm, mode) =>
            Command("station-air-mode-" + alarm + "-" + (byte)mode));
        _stationFireAlarmControls ??= new BoxStationFireAlarmControls(_orbitalStation, (alarm, control) =>
            Command("station-fire-alarm-" + alarm + "-" + (byte)control));
        _stationRoomThermalControls ??= new BoxStationRoomThermalControls(_orbitalStation, (device, control) =>
            Command("station-room-thermal-" + device + "-" + (byte)control));
        bool interactive = _ready && _body?.TelemetryCurrent == true && _body.State.Docked && StationHullCurrent
            && _rider is { Active: true, Attached: true, Walking: true } && LocalCrewAccess && LocalAlive();
        var holder = LocalFirefightingHolder;
        bool holdingRepair = HoldingRepairTiles(holder), holdingMaterials = HoldingMaterials(holder),
            holdingFoam = false, holdingExtinguisher = false;
        if (holder != Guid.Empty)
        {
            if (CurrentFoamState is { } foam)
                foreach (var item in foam.Items)
                    if (item.Place == BoxStationFoamPlace.Held && item.Holder == holder) { holdingFoam = true; break; }
            if (CurrentFirefightingState is { } firefighting)
                foreach (var item in firefighting.Items)
                    if (item.Place == BoxStationExtinguisherPlace.Held && item.Holder == holder) { holdingExtinguisher = true; break; }
        }
        bool holdingEngineering = HoldingEngineering(holder);
        if (holdingEngineering && _body != null && TheForest.Utils.LocalPlayer.Transform != null
            && _body.ContainsCabin(TheForest.Utils.LocalPlayer.Transform.position)) interactive = false;
        bool holdingEquipment = holdingEngineering || holdingRepair || holdingFoam || holdingExtinguisher;
        bool fabricationInput = UpdateFabricationControls(interactive);
        if (fabricationInput) interactive = false;
        if (holdingEquipment && UpdateMaterialControls(interactive, true)) interactive = false;
        if (UpdateEngineeringControls(interactive && !holdingMaterials && !holdingRepair && !holdingFoam && !holdingExtinguisher)) interactive = false;
        if (!holdingEquipment && UpdateMaterialControls(interactive)) interactive = false;
        if (UpdateRepairControls(interactive && !holdingEngineering && !holdingMaterials && !holdingFoam && !holdingExtinguisher)) interactive = false;
        if (UpdateFoamControls(interactive && !holdingEngineering && !holdingMaterials && !holdingRepair && !holdingExtinguisher)) interactive = false;
        if (UpdateFirefightingControls(interactive && !holdingEngineering && !holdingMaterials && !holdingRepair && !holdingFoam)) interactive = false;
        _materialInput |= fabricationInput;
        if (UpdateShoreServiceControls(interactive && !holdingEquipment && !holdingMaterials)) interactive = false;
        var state = StationHullCurrent ? CurrentStationState : null;
        _stationControls.Update(state, interactive, _feedback);
        _stationPowerControls.Update(state, interactive, _feedback);
        _stationSmesControls.Update(state, interactive, _feedback);
        _stationPlumbingControls.Update(state, interactive, _feedback);
        _stationAirAlarmControls.Update(state, interactive, _feedback);
        _stationFireAlarmControls.Update(state, interactive, _feedback);
        _stationRoomThermalControls.Update(state, interactive, _feedback);
    }

    private static bool TryStationDoor(string command, out int door)
    {
        const string prefix = "station-door-";
        door = -1;
        return command.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(command.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out door)
            && ((uint)door < (uint)BoxStationLayout.Current.Doors.Length
                || door >= BoxStationLayout.Current.Doors.Length
                && BoxStationDoors.ValidEdge(BoxStationLayout.Current, door - BoxStationLayout.Current.Doors.Length));
    }

    private string ExecuteStationDoor(Peer peer, int stableId, Vector3 actorPosition)
    {
        if (!_stationSystems.TryDoorIndex(stableId, out int door)) return "Unknown station door.";
        string failure = StationControlAccess(peer, actorPosition);
        if (failure.Length != 0) return failure;
        if (!_orbitalStation!.TryDoorIndex(stableId, out int projectedDoor) || projectedDoor != door)
            return "Station geometry is updating.";
        var eye = actorPosition + _orbitalStation!.Root.transform.up * 1.3f;
        var target = default(Vector3);
        float nearest = float.PositiveInfinity;
        for (int gate = 0; gate < _orbitalStation.DoorGateCount(door); gate++)
        {
            var frame = _orbitalStation.DoorGateFrame(door, gate);
            bool back = frame.InverseTransformPoint(actorPosition).z < 0;
            var point = _orbitalStation.DoorControlPoint(door, back, gate)
                + frame.TransformVector(new(-.013f, -.078f, 0));
            float distance = (point - eye).sqrMagnitude;
            if (distance >= nearest) continue;
            nearest = distance;
            target = point;
        }
        failure = ReachStationControl(peer, actorPosition, target);
        if (failure.Length != 0) return failure;
        _nextStationSnapshot = 0;
        return _stationSystems.CommandDoor(door);
    }
}
