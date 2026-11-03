// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Modstoke.Sons;
using Vector3 = System.Numerics.Vector3;

namespace Modstoke.Client;

internal sealed partial class ClientDroneService
{
    private void UpdateTerrainObservation(Kit kit, long now)
    {
        // This server-world SDK read is supplementary. Single-player keeps its existing surface ray.
        if (!BoltNetwork.isRunning || !BoltNetwork.isServer || kit.Body == null
            || kit.Body.Root == null || kit.Body.Root.WasCollected || !kit.Body.Root)
        {
            kit.LoadedTerrainHeight = null;
            kit.LoadedTerrainObservedAt = kit.NextTerrainObservation = 0;
            return;
        }
        if (kit.TerrainObservationEpoch != _epoch)
        {
            kit.TerrainObservationEpoch = _epoch;
            kit.LoadedTerrainHeight = null;
            kit.LoadedTerrainObservedAt = kit.NextTerrainObservation = 0;
        }
        var position = kit.Body.Position;
        var dx = position.x - kit.TerrainObservationPosition.x;
        var dz = position.z - kit.TerrainObservationPosition.z;
        if (dx * dx + dz * dz > 100f)
        {
            kit.LoadedTerrainHeight = null;
            kit.LoadedTerrainObservedAt = 0;
        }
        if (now < kit.NextTerrainObservation) return;
        kit.NextTerrainObservation = now + 500;
        kit.LoadedTerrainHeight = null;
        kit.LoadedTerrainObservedAt = 0;
        if (!ServerWorld.TryReadTerrainHeight(new Vector3(position.x, position.y, position.z), out var sample, out _)) return;
        // Copy values only; the SDK's native terrain observation belongs to its capture frame.
        kit.LoadedTerrainHeight = sample.Height;
        kit.TerrainObservationPosition = position;
        kit.LoadedTerrainObservedAt = now;
    }

    private static void ResetObservations(Kit kit)
    {
        kit.SolarObservationEpoch = kit.TerrainObservationEpoch = Guid.Empty;
        kit.NextSolarObstruction = kit.NextTerrainObservation = kit.LoadedTerrainObservedAt = 0;
        kit.SolarObstructionAvailable = kit.SolarObstructed = kit.Charging = false;
        kit.SolarStatus = "";
        kit.LoadedTerrainHeight = null;
    }
}
