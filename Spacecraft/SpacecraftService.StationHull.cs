// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftService
{
    private void OnStationCollision(Collider collider, Vector3 worldPoint, float energyJoules)
    {
        if (!_started || !_ready || IsClient || _body?.Ready != true || _stationFailed
            || _orbitalStation?.Visible != true
            || (!_orbitalStation.TryHullImpact(collider, worldPoint, out int target)
                && !_orbitalStation.TryWallImpact(collider, worldPoint, out target)
                && !_orbitalStation.TryStructureImpact(collider, worldPoint, out target)
                && !_orbitalStation.TryConstructionImpact(collider, worldPoint, out target))
            || !_stationSystems.Repair.ApplyImpact(target, energyJoules)) return;
        // Projection is swapped after the body drains all contacts, keeping the
        // collider registry intact for other panels struck in the same step.
        _nextStationSnapshot = 0;
        PublishHullState();
        MarkMaterialsDirty();
    }
}
