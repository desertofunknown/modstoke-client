// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftService
{
    private string CommandLoadShipSpares()
    {
        if (!_started || !_ready) return _feedback = "Spaceflight service is not ready.";
        if (_pending != null || _boardingCommand.HasValue) return _feedback = "Waiting for the previous spaceflight request.";
        if (CurrentMaterialsState is not { } state) return _feedback = "Waiting for station supply state.";
        var holder = LocalFirefightingHolder;
        foreach (var bundle in state.Bundles)
        {
            if (bundle.Place != BoxStationMaterialPlace.Held || bundle.Holder != holder
                || bundle.Material is not (BoxStationMaterial.LineSpare or BoxStationMaterial.CircuitSpare or BoxStationMaterial.Steel)) continue;
            return Dispatch(ActionCode.StationMaterials, stationTarget: bundle.Id,
                stationSetting: (ushort)BoxStationMaterialControl.LoadShipSpares);
        }
        return _feedback = "Hold a repair kit or steel sheets to load the stores.";
    }

    private string ExecuteShipSpares(Peer peer, int expectedBundleId, Vector3 feet, ulong actor)
    {
        if (_body == null || !AtControl(peer, "load-spares", feet))
            return "Approach the repair stores control inside the ship.";
        if (!_systems.LoadStationSpares(_stationSystems.Repair, _epoch, _shipId, peer.Lease,
                actor, expectedBundleId, _body.State, out string feedback)) return feedback;
        CancelEngineeringWork(peer);
        _body.SetSystemsState(SystemsState(_systems.State));
        RecordStationInteraction(peer, "load-spares", feet);
        MarkMaterialsDirty();
        _nextSnapshot = 0;
        return feedback;
    }

    private bool CarryingShipSpares(Peer peer, ulong actor, Vector3 feet)
    {
        if (_body?.ContainsCrewWalkArea(feet) != true) return false;
        foreach (var bundle in _stationSystems.Repair.Materials.Bundles)
            if (bundle.Place == BoxStationMaterialPlace.Held && bundle.Holder == peer.Lease && bundle.Actor == actor
                && bundle.Material is BoxStationMaterial.LineSpare or BoxStationMaterial.CircuitSpare or BoxStationMaterial.Steel) return true;
        return false;
    }
}
