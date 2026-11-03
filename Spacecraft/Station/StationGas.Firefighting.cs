// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Client;

// Cooling is heat taken from the original gas; newly formed vapor stays in the mixture.
internal readonly record struct StationWaterSpray(
    double VaporMoles, double LiquidKilograms, double LiquidKelvin, double GasCoolingJoules);

internal partial struct StationGas
{
    private const double WaterKilogramsPerUnit = .1, LiquidWaterHeatCapacity = 4184,
        WaterBoilingKelvin = 373.15, WaterVaporizationJoulesPerKilogram = 2_256_000,
        WaterMolarMass = .01801528;

    internal StationWaterSpray ApplyWaterSpray(double units)
    {
        if (!double.IsFinite(units) || units < 0 || units > 5)
            throw new ArgumentOutOfRangeException(nameof(units));

        double water = units * WaterKilogramsPerUnit;
        var unchanged = new StationWaterSpray(0, water, ReferenceTemperature, 0);
        if (water == 0 || !WithinReactionBounds() || Moles == 0) return unchanged;
        double temperature = TemperatureKelvin, capacity = HeatCapacity;
        if (temperature <= ReferenceTemperature) return unchanged;

        double liquidCapacity = water * LiquidWaterHeatCapacity;
        double warmToBoiling = liquidCapacity * (WaterBoilingKelvin - ReferenceTemperature);
        double availableAtBoiling = capacity * (temperature - WaterBoilingKelvin);
        var changed = this;
        double vaporMoles = 0, liquid = water, liquidKelvin;

        // Fixed normal boiling and constant heat capacities are authored approximations
        // for the whole compartment, without pressure-dependent boiling or condensation.
        if (availableAtBoiling <= warmToBoiling)
        {
            double uptake = liquidCapacity * (temperature - ReferenceTemperature)
                * (capacity / (capacity + liquidCapacity));
            changed._energy = _energy - uptake;
            if (_energy - changed._energy > uptake) changed._energy = Math.BitIncrement(changed._energy);
            double removed = _energy - changed._energy;
            if (removed <= 0) return unchanged;
            liquidKelvin = ReferenceTemperature + removed / liquidCapacity;
            if (liquidKelvin <= ReferenceTemperature || liquidKelvin > WaterBoilingKelvin) return unchanged;
        }
        else
        {
            double evaporated = Math.Min(water,
                (availableAtBoiling - warmToBoiling) / WaterVaporizationJoulesPerKilogram);
            double requestedMoles = evaporated / WaterMolarMass;
            changed._waterVapor = _waterVapor + requestedMoles;
            vaporMoles = changed._waterVapor - _waterVapor;
            if (vaporMoles > requestedMoles || vaporMoles * WaterMolarMass > water)
            {
                changed._waterVapor = Math.BitDecrement(changed._waterVapor);
                vaporMoles = changed._waterVapor - _waterVapor;
            }
            evaporated = vaporMoles * WaterMolarMass;
            if (vaporMoles <= 0 || vaporMoles > requestedMoles || evaporated > water
                || changed.HeatCapacity <= capacity) return unchanged;
            liquid = water - evaporated;
            liquidKelvin = WaterBoilingKelvin;

            // Gas stores C*T. With incoming liquid at zero reference energy, each
            // new vapor mole also carries M*(Cl*(Tb-Tin)+L)-Cv*Tb outside that store.
            // Debit this offset and the residual liquid's warming; the remaining
            // sensible store sets shared superheat without charging Cv*Tb twice.
            double vaporOffset = WaterMolarMass * (LiquidWaterHeatCapacity
                * (WaterBoilingKelvin - ReferenceTemperature) + WaterVaporizationJoulesPerKilogram)
                - WaterVaporHeatCapacity * WaterBoilingKelvin;
            double debit = liquid * LiquidWaterHeatCapacity * (liquidKelvin - ReferenceTemperature)
                + vaporMoles * vaporOffset;
            changed._energy = _energy - debit;
            if (changed._energy >= _energy) return unchanged;
            // Pay the phase reference before crediting vapor. Rounding may remove
            // one extra energy ulp; the receipt uses the committed cooling below.
            if (_energy - changed._energy < debit) changed._energy = Math.BitDecrement(changed._energy);
        }

        if (!changed.WithinReactionBounds() || changed._energy >= _energy
            || changed.TemperatureKelvin < ReferenceTemperature
            || changed.TemperatureKelvin >= temperature) return unchanged;

        double cooling = _energy - changed._energy
            + (changed.HeatCapacity - capacity) * changed.TemperatureKelvin;
        if (!double.IsFinite(cooling) || cooling <= 0 || !double.IsFinite(liquidKelvin)) return unchanged;
        this = changed;
        return new(vaporMoles, liquid, liquidKelvin, cooling);
    }
}
