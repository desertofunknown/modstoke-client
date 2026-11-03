// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.IO;

namespace Modstoke.Client;

internal enum ShoreCoolantStatus : byte { Off, Waiting, Filling, Limit, NoSupply, NoPower, Unsafe }

internal static class SpacecraftShoreCoolant
{
    // Station water is compatible makeup for the abstract ship coolant reserve.
    // This gameplay conversion does not model coolant chemistry, volume or temperature.
    internal const int ShipCoolantUnitsCapacity = 1000;
    internal const int MaximumUnitsPerSecond = 1, PumpWatts = 100;
    internal const int SupplyObject = 6579, SupplyCell = 27208;
    internal static readonly int SupplyTank = ResolveSupplyTank();

    internal static bool IsSupply(BoxStationWaterTank tank) => tank.Object == SupplyObject && tank.Cell == SupplyCell;

    private static int ResolveSupplyTank()
    {
        var tanks = BoxStationFirefighting.GetTanks(BoxStationLayout.Current);
        for (int i = 0; i < tanks.Length; i++)
            if (IsSupply(tanks[i]) && tanks[i].Capacity == ShipCoolantUnitsCapacity)
                return i;
        throw new InvalidDataException("The station coolant-service supply tank does not match its layout.");
    }
}
