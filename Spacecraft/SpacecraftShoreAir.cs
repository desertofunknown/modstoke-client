// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

namespace Modstoke.Client;

internal enum ShoreAirStatus : byte { Off, Waiting, Filling, Full, NoSupply, UnsafeGas, NoPower, Cooling }

internal readonly record struct ShoreAirResult(ShoreAirStatus Status, double Moles = 0, double Joules = 0);

internal static class SpacecraftShoreAir
{
    internal const int SupplyPort = 18420, SupplyCell = 30280;
    internal const float MaximumMolesPerSecond = 8;
    internal const int MaximumWatts = 12000, MotorWatts = 120;
    internal const double Efficiency = .65, MinimumIntakeKpa = 100;
}
