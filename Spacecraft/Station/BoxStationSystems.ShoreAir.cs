// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

namespace Modstoke.Client;

internal sealed partial class BoxStationSystems
{
    // Called inside the station power step with the ship computer gate held.
    internal ShoreAirResult RefillShipAir(ref SpacecraftAtmosphere reserve, double maximumMoles, float seconds)
    {
        int cell = SpacecraftShoreAir.SupplyCell;
        int room = _rooms.NodeAtCell(cell);
        if (room < 0) return new(ShoreAirStatus.Cooling);
        return Plumbing.RefillShipAir(ref reserve, maximumMoles, ref _rooms.Gas(room),
            Power, Layout.Cells[cell].Area, seconds);
    }
}
