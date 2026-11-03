// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Globalization;

namespace Modstoke.Client;

[Flags]
internal enum StationGasSpecies : byte
{
    Oxygen = 1,
    Nitrogen = 2,
    CO2 = 4,
    Plasma = 8,
    N2O = 16,
    WaterVapor = 32,
    Tritium = 64,
    BZ = 128,
    All = 255
}

// A well-mixed rigid volume: moles, m³, kelvin, joules and kPa. Transfers conserve
// each species and sensible energy; transfers omit phase changes and pressure work.
internal partial struct StationGas
{
    internal const double GasConstant = .00831446261815324;
    internal const double ReferenceTemperature = 293.15;
    internal const double SourceCellVolume = 2.5;
    internal const double MaximumTemperature = 10_000_000;
    // Source gas definitions use these constant molar heat capacities, J/(mol K).
    private const double OxygenHeatCapacity = 20, NitrogenHeatCapacity = 20,
        CO2HeatCapacity = 30, PlasmaHeatCapacity = 200, N2OHeatCapacity = 40,
        WaterVaporHeatCapacity = 40, TritiumHeatCapacity = 10, BZHeatCapacity = 20;
    private readonly double _volume;
    private double _oxygen, _nitrogen, _co2, _plasma, _n2o, _energy, _waterVapor, _tritium, _bz;

    private StationGas(double volume, double oxygen, double nitrogen, double co2,
        double plasma, double n2o, double energy, double waterVapor, double tritium, double bz)
    {
        _volume = volume;
        _oxygen = oxygen;
        _nitrogen = nitrogen;
        _co2 = co2;
        _plasma = plasma;
        _n2o = n2o;
        _energy = energy;
        _waterVapor = waterVapor;
        _tritium = tritium;
        _bz = bz;
    }

    internal static StationGas Vacuum(double volume)
    {
        if (!double.IsFinite(volume) || volume <= 0)
            throw new ArgumentOutOfRangeException(nameof(volume));
        return new StationGas(volume, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    }

    internal static bool TryCreate(string sourceMix, double volume, out StationGas gas)
    {
        gas = default;
        if (string.IsNullOrWhiteSpace(sourceMix) || !double.IsFinite(volume) || volume <= 0)
            return false;
        double oxygen = 0, nitrogen = 0, co2 = 0, plasma = 0, n2o = 0, waterVapor = 0, tritium = 0, bz = 0;
        double temperature = ReferenceTemperature;
        int seen = 0;
        foreach (string entry in sourceMix.Split(';'))
        {
            int separator = entry.IndexOf('=');
            if (separator <= 0 || !double.TryParse(entry.AsSpan(separator + 1),
                NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                || !double.IsFinite(value) || value < 0) return false;
            string name = entry.Substring(0, separator).Trim();
            int flag;
            switch (name)
            {
                case "o2": flag = 1; oxygen = value; break;
                case "n2": flag = 2; nitrogen = value; break;
                case "co2": flag = 4; co2 = value; break;
                case "plasma": flag = 8; plasma = value; break;
                case "n2o": flag = 16; n2o = value; break;
                case "water_vapor": flag = 32; waterVapor = value; break;
                case "tritium": flag = 64; tritium = value; break;
                case "bz": flag = 128; bz = value; break;
                case "TEMP": flag = 256; temperature = value; break;
                default: return false;
            }
            if ((seen & flag) != 0) return false;
            seen |= flag;
        }
        if (temperature <= 0 || temperature > MaximumTemperature) return false;
        // Source recipes contain mole quantities per 2.5 m³, not percentages.
        double scale = volume / SourceCellVolume;
        var created = new StationGas(volume, oxygen * scale, nitrogen * scale, co2 * scale,
            plasma * scale, n2o * scale, 0, waterVapor * scale, tritium * scale, bz * scale);
        created._energy = created.HeatCapacity * temperature;
        if (!created.HasFiniteState()) return false;
        gas = created;
        return true;
    }

    // Keep the legacy five species and sensible energy at indices 0..5.
    // Geometry supplies volume, and restoration never reseeds gas.
    internal readonly double[] CaptureValues()
        => new[] { _oxygen, _nitrogen, _co2, _plasma, _n2o, _energy, _waterVapor, _tritium, _bz };

    internal static bool TryRestore(ReadOnlySpan<double> values, double volume,
        double maximumPressure, double maximumMoles, out StationGas gas)
    {
        gas = default;
        if ((values.Length != 6 && values.Length != 9) || !double.IsFinite(volume) || volume <= 0
            || !double.IsFinite(maximumPressure) || maximumPressure < 0
            || !double.IsFinite(maximumMoles) || maximumMoles < 0) return false;
        for (int i = 0; i < values.Length; i++)
        {
            if (!double.IsFinite(values[i]) || values[i] < 0) return false;
        }
        var restored = new StationGas(volume, values[0], values[1], values[2],
            values[3], values[4], values[5], values.Length == 9 ? values[6] : 0,
            values.Length == 9 ? values[7] : 0, values.Length == 9 ? values[8] : 0);
        if (!restored.HasFiniteState() || restored.Moles > maximumMoles
            || restored.Pressure > maximumPressure) return false;
        gas = restored;
        return true;
    }

    internal readonly double Volume => _volume;
    internal readonly double Moles => _oxygen + _nitrogen + _co2 + _plasma + _n2o + _waterVapor + _tritium + _bz;
    internal readonly double OxygenMoles => _oxygen;
    internal readonly double NitrogenMoles => _nitrogen;
    internal readonly double CO2Moles => _co2;
    internal readonly double PlasmaMoles => _plasma;
    internal readonly double N2OMoles => _n2o;
    internal readonly double WaterVaporMoles => _waterVapor;
    internal readonly double TritiumMoles => _tritium;
    internal readonly double BZMoles => _bz;
    internal readonly double ThermalJoules => _energy;
    internal readonly double HeatCapacity => _oxygen * OxygenHeatCapacity
        + _nitrogen * NitrogenHeatCapacity + _co2 * CO2HeatCapacity
        + _plasma * PlasmaHeatCapacity + _n2o * N2OHeatCapacity
        + _waterVapor * WaterVaporHeatCapacity + _tritium * TritiumHeatCapacity + _bz * BZHeatCapacity;
    // Empty gas has no thermal state; this reference is only a display convention.
    internal readonly double TemperatureKelvin => HeatCapacity > 0 ? _energy / HeatCapacity : ReferenceTemperature;
    internal readonly double Pressure => Moles * GasConstant * TemperatureKelvin / _volume;
    internal readonly double OxygenFraction => Moles > 0 ? _oxygen / Moles : 0;
    internal readonly double NitrogenFraction => Moles > 0 ? _nitrogen / Moles : 0;
    internal readonly double CO2Fraction => Moles > 0 ? _co2 / Moles : 0;
    internal readonly double PlasmaFraction => Moles > 0 ? _plasma / Moles : 0;
    internal readonly double N2OFraction => Moles > 0 ? _n2o / Moles : 0;
    internal readonly double WaterVaporFraction => Moles > 0 ? _waterVapor / Moles : 0;
    internal readonly double TritiumFraction => Moles > 0 ? _tritium / Moles : 0;
    internal readonly double BZFraction => Moles > 0 ? _bz / Moles : 0;

    internal static void Equalize(ref StationGas first, ref StationGas second, double amount)
    {
        if (!double.IsFinite(amount) || amount <= 0) return;
        amount = Math.Min(amount, 1);
        double volume = first._volume + second._volume;
        // Both packets come from the pre-step donors. At amount=1, species and
        // energy per volume match, including mixtures with different temperatures.
        var fromFirst = first.TakeFraction(amount * second._volume / volume);
        var fromSecond = second.TakeFraction(amount * first._volume / volume);
        first.Add(fromSecond);
        second.Add(fromFirst);
    }

    internal double TransferTo(ref StationGas destination, double requestedMoles)
    {
        if (!double.IsFinite(requestedMoles) || requestedMoles <= 0 || Moles <= 0) return 0;
        var packet = TakeFraction(Math.Min(1, requestedMoles / Moles));
        destination.Add(packet);
        return packet.Moles;
    }

    internal readonly double SelectedMoles(StationGasSpecies species) => Select(species).Moles;

    internal double TransferSelectedTo(ref StationGas destination, double requestedMoles, StationGasSpecies species)
    {
        if (!double.IsFinite(requestedMoles) || requestedMoles <= 0) return 0;
        var selected = Select(species);
        double moles = selected.Moles;
        if (moles <= 0) return 0;
        if (selected.Oxygen == _oxygen && selected.Nitrogen == _nitrogen && selected.CO2 == _co2
            && selected.Plasma == _plasma && selected.N2O == _n2o && selected.WaterVapor == _waterVapor
            && selected.Tritium == _tritium && selected.BZ == _bz) return TransferTo(ref destination, requestedMoles);
        double fraction = Math.Min(1, requestedMoles / moles);
        var packet = new Gas(selected.Oxygen * fraction, selected.Nitrogen * fraction,
            selected.CO2 * fraction, selected.Plasma * fraction, selected.N2O * fraction, selected.Energy * fraction,
            selected.WaterVapor * fraction, selected.Tritium * fraction, selected.BZ * fraction);
        _oxygen -= packet.Oxygen;
        _nitrogen -= packet.Nitrogen;
        _co2 -= packet.CO2;
        _plasma -= packet.Plasma;
        _n2o -= packet.N2O;
        _energy -= packet.Energy;
        _waterVapor -= packet.WaterVapor;
        _tritium -= packet.Tritium;
        _bz -= packet.BZ;
        destination.Add(packet);
        return packet.Moles;
    }

    // Solve the mixed-temperature receiver pressure rather than assuming its gas
    // has the donor's temperature or molar heat capacity. The positive quadratic
    // root is evaluated without subtracting nearly equal positive numbers.
    internal readonly double TransferMolesToPressure(in StationGas destination, double pressure,
        StationGasSpecies species = StationGasSpecies.All)
    {
        if (!double.IsFinite(pressure) || pressure <= destination.Pressure || pressure <= 0) return 0;
        var selected = Select(species);
        double moles = selected.Moles;
        if (moles <= 0) return 0;
        double capacity = (selected.Oxygen * OxygenHeatCapacity + selected.Nitrogen * NitrogenHeatCapacity
            + selected.CO2 * CO2HeatCapacity + selected.Plasma * PlasmaHeatCapacity + selected.N2O * N2OHeatCapacity
            + selected.WaterVapor * WaterVaporHeatCapacity + selected.Tritium * TritiumHeatCapacity
            + selected.BZ * BZHeatCapacity) / moles;
        double energy = selected.Energy / moles;
        double target = pressure * destination.Volume / GasConstant;
        double b = destination.ThermalJoules + destination.Moles * energy - target * capacity;
        double c = destination.Moles * destination.ThermalJoules - target * destination.HeatCapacity;
        double root = Math.Sqrt(Math.Max(0, b * b - 4 * energy * c));
        double amount = b >= 0 ? -2 * c / (b + root) : (root - b) / (2 * energy);
        return Math.Min(moles, Math.Max(0, amount * (1 - 1e-12)));
    }

    private readonly Gas Select(StationGasSpecies species)
    {
        double oxygen = (species & StationGasSpecies.Oxygen) != 0 ? _oxygen : 0;
        double nitrogen = (species & StationGasSpecies.Nitrogen) != 0 ? _nitrogen : 0;
        double co2 = (species & StationGasSpecies.CO2) != 0 ? _co2 : 0;
        double plasma = (species & StationGasSpecies.Plasma) != 0 ? _plasma : 0;
        double n2o = (species & StationGasSpecies.N2O) != 0 ? _n2o : 0;
        double waterVapor = (species & StationGasSpecies.WaterVapor) != 0 ? _waterVapor : 0;
        double tritium = (species & StationGasSpecies.Tritium) != 0 ? _tritium : 0;
        double bz = (species & StationGasSpecies.BZ) != 0 ? _bz : 0;
        double capacity = oxygen * OxygenHeatCapacity + nitrogen * NitrogenHeatCapacity
            + co2 * CO2HeatCapacity + plasma * PlasmaHeatCapacity + n2o * N2OHeatCapacity
            + waterVapor * WaterVaporHeatCapacity + tritium * TritiumHeatCapacity + bz * BZHeatCapacity;
        return new(oxygen, nitrogen, co2, plasma, n2o, capacity * TemperatureKelvin, waterVapor, tritium, bz);
    }

    internal double Exhaust(double requestedMoles)
    {
        if (!double.IsFinite(requestedMoles) || requestedMoles <= 0 || Moles <= 0) return 0;
        return TakeFraction(Math.Min(1, requestedMoles / Moles)).Moles;
    }

    internal double Breathe(double requestedMoles)
    {
        if (!double.IsFinite(requestedMoles) || requestedMoles <= 0) return 0;
        double used = Math.Min(_oxygen, requestedMoles);
        _oxygen -= used;
        _co2 += used;
        // A simplified 1:1 respiratory exchange; body heat and chemistry are not modeled.
        // Retaining sensible energy lets the changed heat capacity determine temperature.
        return used;
    }

    internal bool TryAddThermalEnergy(double joules)
    {
        if (!double.IsFinite(joules)) return false;
        double energy = _energy + joules;
        if (!double.IsFinite(energy) || (Moles == 0 ? energy != 0 : energy <= 0)) return false;
        var changed = this;
        changed._energy = energy;
        if (!changed.HasFiniteState()) return false;
        _energy = energy;
        return true;
    }

    private readonly bool HasFiniteState()
    {
        if (!double.IsFinite(_volume) || _volume <= 0 || _oxygen < 0 || _nitrogen < 0 || _co2 < 0
            || _plasma < 0 || _n2o < 0 || _waterVapor < 0 || _tritium < 0 || _bz < 0) return false;
        double moles = Moles, capacity = HeatCapacity;
        if (!double.IsFinite(moles) || !double.IsFinite(capacity) || !double.IsFinite(_energy)
            || !double.IsFinite(ChemicalJoules)) return false;
        if (moles == 0) return _energy == 0;
        double temperature = TemperatureKelvin;
        return _energy > 0 && capacity > 0 && double.IsFinite(temperature) && temperature > 0
            && temperature <= MaximumTemperature && double.IsFinite(Pressure);
    }

    private Gas TakeFraction(double fraction)
    {
        if (fraction == 1)
        {
            var all = new Gas(_oxygen, _nitrogen, _co2, _plasma, _n2o, _energy, _waterVapor, _tritium, _bz);
            _oxygen = _nitrogen = _co2 = _plasma = _n2o = _energy = _waterVapor = _tritium = _bz = 0;
            return all;
        }
        var packet = new Gas(_oxygen * fraction, _nitrogen * fraction, _co2 * fraction,
            _plasma * fraction, _n2o * fraction, _energy * fraction,
            _waterVapor * fraction, _tritium * fraction, _bz * fraction);
        _oxygen -= packet.Oxygen;
        _nitrogen -= packet.Nitrogen;
        _co2 -= packet.CO2;
        _plasma -= packet.Plasma;
        _n2o -= packet.N2O;
        _energy -= packet.Energy;
        _waterVapor -= packet.WaterVapor;
        _tritium -= packet.Tritium;
        _bz -= packet.BZ;
        return packet;
    }

    private void Add(Gas gas)
    {
        _oxygen += gas.Oxygen;
        _nitrogen += gas.Nitrogen;
        _co2 += gas.CO2;
        _plasma += gas.Plasma;
        _n2o += gas.N2O;
        _energy += gas.Energy;
        _waterVapor += gas.WaterVapor;
        _tritium += gas.Tritium;
        _bz += gas.BZ;
    }

    private readonly record struct Gas(double Oxygen, double Nitrogen, double CO2,
        double Plasma, double N2O, double Energy, double WaterVapor, double Tritium, double BZ)
    {
        internal double Moles => Oxygen + Nitrogen + CO2 + Plasma + N2O + WaterVapor + Tritium + BZ;
    }
}
