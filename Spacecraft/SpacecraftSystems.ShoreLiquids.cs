// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftSystems
{
    private static bool TryCreditShoreLiquid(float current, int capacity, out float next)
    {
        double unit = 1d / capacity;
        next = current;
        if (!float.IsFinite(current) || current < 0 || 1d - current < unit) return false;
        double target = current + unit;
        next = (float)target;
        // One debited station unit must cover the representable ship reserve increment.
        if (next > target) next = MathF.BitDecrement(next);
        return next > current && next <= 1;
    }

    private static bool TryPumpShoreLiquidUnit(BoxStationSystems station, int area, int watts, double unitSeconds,
        ref double paidSeconds, ref double remainingInterval, out bool powered)
    {
        double remaining = unitSeconds - paidSeconds;
        double pumping = Math.Min(remainingInterval, remaining);
        double request = pumping * watts;
        double supplied = station.Power.Draw(area, BoxStationPowerChannel.Equipment, request);
        powered = supplied > 0;
        if (!powered) return false;
        // Clamp a fully paid final slice to avoid leaving a unit unfinished through rounding.
        paidSeconds = supplied >= request && pumping >= remaining ? unitSeconds
            : Math.Min(unitSeconds, paidSeconds + supplied / watts);
        if (paidSeconds < unitSeconds) return false;
        paidSeconds = 0;
        remainingInterval -= pumping;
        return true;
    }
}
