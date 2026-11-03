// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Client;

internal partial struct SpacecraftAtmosphere
{
    internal readonly double OxygenPartialPressure => _oxygen * GasConstant * TemperatureKelvin / _volume;

    // Carbon enters from the finite solid inventory. Chemical heat belongs to
    // that donor; this conversion preserves the gas's existing sensible energy.
    internal double BurnCarbon(double requestedMoles)
    {
        if (!double.IsFinite(requestedMoles) || requestedMoles < 0)
            throw new ArgumentOutOfRangeException(nameof(requestedMoles));
        if (requestedMoles == 0 || _oxygen <= 0) return 0;

        // CO2 has a larger heat capacity, so even an isomolar conversion can
        // cool the gas to its lower bound before the oxygen is exhausted.
        double capacityRoom = Math.Max(0, _energy / 173.15 - HeatCapacity);
        double budget = Math.Min(requestedMoles, Math.Min(_oxygen, capacityRoom / (CO2HeatCapacity - OxygenHeatCapacity)));
        for (int attempt = 0; attempt < 3 && budget > 0; attempt++)
        {
            double oxygen = _oxygen - budget;
            if (_oxygen - oxygen > budget) oxygen = Math.BitIncrement(oxygen);
            double burned = _oxygen - oxygen;
            double co2 = _co2 + burned;
            if (co2 - _co2 > burned) co2 = Math.BitDecrement(co2);
            double produced = co2 - _co2;
            if (burned <= 0 || produced <= 0) return 0;
            // Both species must represent the same mole transfer, including
            // tiny extents near a store's floating-point resolution.
            if (produced != burned) { budget = produced; continue; }
            var candidate = this;
            candidate._oxygen = oxygen;
            candidate._co2 = co2;
            if (!candidate.ValidState(250, double.MaxValue)) return 0;
            this = candidate;
            return burned;
        }
        return 0;
    }
}
