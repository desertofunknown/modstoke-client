// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Client;

// A well-mixed rigid volume. Species are moles, sensible thermal energy is joules,
// volume is m³ and derived pressure is kPa. Nitrogen includes the minor inert gases.
internal partial struct SpacecraftAtmosphere
{
    internal const double GasConstant = .00831446261815324;
    internal const double ReferenceTemperature = 295.15;
    internal const double RestingOxygenMolesPerSecond = .00035;
    // Approximate constant-volume molar heat capacities near room temperature,
    // J/(mol K). Combustion heats the solid hotspot before gas exchange.
    // Phase changes and pressure-work dynamics are omitted.
    private const double OxygenHeatCapacity = 20.8, NitrogenHeatCapacity = 20.8, CO2HeatCapacity = 28.8;
    private readonly double _volume;
    private double _oxygen, _nitrogen, _co2, _energy;

    internal SpacecraftAtmosphere(double volume, double pressure, double temperatureC,
        double oxygenFraction = .209, double co2Fraction = .0004)
    {
        _volume = volume;
        double temperature = temperatureC + 273.15;
        double moles = pressure * volume / (GasConstant * temperature);
        _oxygen = moles * oxygenFraction;
        _co2 = moles * co2Fraction;
        _nitrogen = moles - _oxygen - _co2;
        _energy = (_oxygen * OxygenHeatCapacity + _nitrogen * NitrogenHeatCapacity
            + _co2 * CO2HeatCapacity) * temperature;
    }

    private SpacecraftAtmosphere(double volume, SavedSpacecraftAtmosphere saved)
    {
        _volume = volume;
        _oxygen = saved.OxygenMoles;
        _nitrogen = saved.NitrogenMoles;
        _co2 = saved.CO2Moles;
        _energy = saved.ThermalJoules;
    }

    internal readonly SavedSpacecraftAtmosphere Capture() => new()
    {
        OxygenMoles = _oxygen, NitrogenMoles = _nitrogen, CO2Moles = _co2, ThermalJoules = _energy
    };

    internal static bool TryRestore(SavedSpacecraftAtmosphere? saved, double volume,
        double maximumPressure, double maximumMoles, out SpacecraftAtmosphere atmosphere)
    {
        atmosphere = default;
        if (saved == null) return false;
        var restored = new SpacecraftAtmosphere(volume, saved);
        if (!restored.ValidState(maximumPressure, maximumMoles)) return false;
        atmosphere = restored;
        return true;
    }

    internal readonly bool TryReceive(double oxygen, double nitrogen, double co2, double joules,
        double maximumMoles, out SpacecraftAtmosphere atmosphere)
    {
        atmosphere = default;
        if (!double.IsFinite(oxygen) || oxygen < 0 || !double.IsFinite(nitrogen) || nitrogen < 0
            || !double.IsFinite(co2) || co2 < 0 || !double.IsFinite(joules) || joules < 0
            || !double.IsFinite(maximumMoles) || maximumMoles < 0
            || !double.IsFinite(_volume) || _volume <= 0) return false;
        var received = this;
        received.Add(new Gas(oxygen, nitrogen, co2, joules));
        if (!received.ValidState(double.PositiveInfinity, maximumMoles)) return false;
        atmosphere = received;
        return true;
    }

    private readonly bool ValidState(double maximumPressure, double maximumMoles)
    {
        if (!double.IsFinite(_oxygen) || _oxygen < 0 || !double.IsFinite(_nitrogen) || _nitrogen < 0
            || !double.IsFinite(_co2) || _co2 < 0 || !double.IsFinite(_energy) || _energy < 0) return false;
        double moles = Moles, capacity = HeatCapacity;
        if (!double.IsFinite(moles) || moles > maximumMoles || !double.IsFinite(capacity)) return false;
        if (moles == 0)
        {
            if (_energy != 0) return false;
        }
        else
        {
            double temperature = TemperatureKelvin;
            double pressure = moles * GasConstant * temperature / _volume;
            if (!double.IsFinite(temperature) || temperature is < 173.15 or > 373.15
                || !double.IsFinite(pressure) || pressure > maximumPressure) return false;
        }
        return true;
    }

    internal readonly double Volume => _volume;
    internal readonly double Moles => _oxygen + _nitrogen + _co2;
    internal readonly double OxygenMoles => _oxygen;
    internal readonly double HeatCapacity => _oxygen * OxygenHeatCapacity
        + _nitrogen * NitrogenHeatCapacity + _co2 * CO2HeatCapacity;
    internal readonly double TemperatureKelvin => HeatCapacity > 0 ? _energy / HeatCapacity : ReferenceTemperature;
    internal readonly float Temperature => (float)(TemperatureKelvin - 273.15);
    internal readonly float Pressure => (float)(Moles * GasConstant * TemperatureKelvin / _volume);
    internal readonly float OxygenFraction => Moles > 0 ? (float)(_oxygen / Moles) : 0;
    internal readonly float CO2Fraction => Moles > 0 ? (float)(_co2 / Moles) : 0;
    internal static double ReferenceMoles(double pressureVolume)
        => pressureVolume / (GasConstant * ReferenceTemperature);

    internal static void Equalize(ref SpacecraftAtmosphere first, ref SpacecraftAtmosphere second, double amount)
    {
        // Exchange finite packets from the pre-step donors. Mixing species and
        // energy per volume conserves both even when the initial temperatures differ.
        double volume = first._volume + second._volume;
        var fromFirst = first.TakeFraction(amount * second._volume / volume);
        var fromSecond = second.TakeFraction(amount * first._volume / volume);
        first.Add(fromSecond);
        second.Add(fromFirst);
    }

    internal void Vent(double pressure, double temperatureC, double amount)
    {
        // The exterior is an unbounded source/sink; an evacuated exterior adds no gas.
        TakeFraction(amount);
        var incoming = new SpacecraftAtmosphere(_volume, pressure, temperatureC);
        Add(incoming.TakeFraction(amount));
    }

    internal void Scrub(double maximumMoles, double targetFraction)
    {
        double excess = Math.Max(0, (_co2 - Moles * targetFraction) / (1 - targetFraction));
        Take(0, 0, Math.Min(excess, maximumMoles));
    }

    internal void Breathe(double requestedMoles)
    {
        double used = Math.Min(_oxygen, requestedMoles);
        Take(used, 0, 0);
        // Metabolism supplies the carbon and warm exhaled gas; water and retained
        // oxygen are outside this dry-gas model. Preserve the tuned respiratory quotient.
        double exhaled = used * .85;
        Add(new Gas(0, 0, exhaled, exhaled * CO2HeatCapacity * 310.15));
    }

    internal readonly double MolesToPressure(double pressure, double oxygenFraction, double co2Fraction,
        double sourceTemperature)
    {
        if (pressure <= Pressure) return 0;
        double capacity = oxygenFraction * OxygenHeatCapacity
            + (1 - oxygenFraction - co2Fraction) * NitrogenHeatCapacity + co2Fraction * CO2HeatCapacity;
        double heatCapacity = HeatCapacity;
        if (heatCapacity == 0) return pressure * _volume / (GasConstant * sourceTemperature);
        // Solve P V = (n + x) R (U + x Cv Ts) / (C + x Cv) for the added moles.
        // The sign-aware root avoids cancellation for a nearly full compartment.
        double a = GasConstant * capacity * sourceTemperature;
        double b = GasConstant * (_energy + Moles * capacity * sourceTemperature) - pressure * _volume * capacity;
        double c = GasConstant * Moles * _energy - pressure * _volume * heatCapacity;
        double root = Math.Sqrt(b * b - 4 * a * c);
        return b >= 0 ? -2 * c / (b + root) : (root - b) / (2 * a);
    }

    internal double SupplyTo(ref SpacecraftAtmosphere destination, double requestedMoles,
        double oxygenFraction, double co2Fraction)
    {
        double oxygen = requestedMoles * oxygenFraction;
        double co2 = requestedMoles * co2Fraction;
        double nitrogen = Math.Max(0, requestedMoles - oxygen - co2);
        double fraction = 1;
        if (oxygen > 0) fraction = Math.Min(fraction, _oxygen / oxygen);
        if (nitrogen > 0) fraction = Math.Min(fraction, _nitrogen / nitrogen);
        if (co2 > 0) fraction = Math.Min(fraction, _co2 / co2);
        var gas = Take(oxygen * fraction, nitrogen * fraction, co2 * fraction);
        destination.Add(gas);
        return gas.Oxygen + gas.Nitrogen + gas.CO2;
    }

    internal void ChangeTemperature(double delta) => _energy += HeatCapacity * delta;

    // Return the representable transfer so a finite thermal donor retains any excess.
    internal double AddThermalEnergy(double joules)
    {
        if (!double.IsFinite(joules)) throw new ArgumentOutOfRangeException(nameof(joules));
        double capacity = HeatCapacity;
        if (capacity <= 0 || joules == 0) return 0;
        double minimum = Math.BitIncrement(173.15 * capacity);
        double maximum = Math.BitDecrement(Math.Min(373.15, 250 * _volume / (Moles * GasConstant)) * capacity);
        if (maximum < minimum) return 0;
        double requested = joules > 0 ? Math.Min(joules, Math.Max(0, maximum - _energy))
            : Math.Max(joules, Math.Min(0, minimum - _energy));
        double next = _energy + requested;
        double accepted = next - _energy;
        if (accepted > requested && requested > 0) next = Math.BitDecrement(next);
        else if (accepted < requested && requested < 0) next = Math.BitIncrement(next);
        accepted = next - _energy;
        if (requested > 0 && accepted < 0 || requested < 0 && accepted > 0) return 0;
        var candidate = this;
        candidate._energy = next;
        if (!candidate.ValidState(250, double.MaxValue)) return 0;
        _energy = next;
        return accepted;
    }

    private Gas TakeFraction(double fraction)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        return Take(_oxygen * fraction, _nitrogen * fraction, _co2 * fraction);
    }

    private Gas Take(double oxygen, double nitrogen, double co2)
    {
        oxygen = Math.Min(_oxygen, oxygen);
        nitrogen = Math.Min(_nitrogen, nitrogen);
        co2 = Math.Min(_co2, co2);
        double energy = oxygen == _oxygen && nitrogen == _nitrogen && co2 == _co2 ? _energy
            : Math.Min(_energy, (oxygen * OxygenHeatCapacity + nitrogen * NitrogenHeatCapacity
                + co2 * CO2HeatCapacity) * TemperatureKelvin);
        _oxygen = Math.Max(0, _oxygen - oxygen);
        _nitrogen = Math.Max(0, _nitrogen - nitrogen);
        _co2 = Math.Max(0, _co2 - co2);
        _energy = Moles > 0 ? Math.Max(0, _energy - energy) : 0;
        return new Gas(oxygen, nitrogen, co2, energy);
    }

    private void Add(Gas gas)
    {
        _oxygen += gas.Oxygen;
        _nitrogen += gas.Nitrogen;
        _co2 += gas.CO2;
        _energy += gas.Energy;
    }

    private readonly record struct Gas(double Oxygen, double Nitrogen, double CO2, double Energy);
}
