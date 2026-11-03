// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.IO;

namespace Modstoke.Client;

internal enum ShoreFuelStatus : byte { Off, Waiting, Filling, Limit, NoSupply, NoPower, Unsafe }

internal static class SpacecraftShoreFuel
{
    // Industrial fuel is a shared game resource, not a physical fuel volume or mass.
    internal const int ShipFuelUnitsCapacity = 1000;
    internal const int MaximumUnitsPerSecond = 2, PumpWatts = 300;
    internal const int SupplyObject = 6582, SupplyCell = 27210;
    internal static readonly int SupplyTank = ResolveSupplyTank();

    internal static bool IsSupply(BoxStationFuelTank tank) => tank.Object == SupplyObject && tank.Cell == SupplyCell;

    private static int ResolveSupplyTank()
    {
        var tanks = BoxStationEngineering.GetFuelTanks(BoxStationLayout.Current);
        for (int i = 0; i < tanks.Length; i++)
            if (IsSupply(tanks[i]))
                return i;
        throw new InvalidDataException("The station fuel-service supply tank does not match its layout.");
    }
}
