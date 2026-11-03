// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Client;

internal partial struct StationGas
{
    internal static void FlowThroughOpening(ref StationGas first, ref StationGas second, double area, double seconds)
    {
        if (!double.IsFinite(area) || area < 0) throw new ArgumentOutOfRangeException(nameof(area));
        if (!double.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        if (area == 0 || seconds == 0) return;

        double firstPressure = first.Pressure, secondPressure = second.Pressure;
        if (firstPressure > secondPressure) first.DischargeTo(ref second, area, seconds);
        else if (secondPressure > firstPressure) second.DischargeTo(ref first, area, seconds);

        // Effective bidirectional exchange at .05 m/s keeps species and heat mixing
        // after pressures balance. It scales with aperture area and reservoir size.
        double exchange = .05 * area * (1 / first._volume + 1 / second._volume);
        Equalize(ref first, ref second, 1 - Math.Exp(-exchange * seconds));
    }

    internal double ExhaustToVacuum(double area, double seconds)
    {
        if (!double.IsFinite(area) || area < 0) throw new ArgumentOutOfRangeException(nameof(area));
        if (!double.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        double moles = Moles;
        if (area == 0 || seconds == 0 || moles == 0) return 0;

        double rate = OpeningRate(area, 0);
        // Integrate donor depletion without overdrawing the volume. Exhaust retains
        // this model's sensible-energy-only convention, without pressure-work cooling.
        return Exhaust(moles * (1 - Math.Exp(-rate * seconds)));
    }

    private void DischargeTo(ref StationGas receiver, double area, double seconds)
    {
        double rate = OpeningRate(area, receiver.Pressure / Pressure);
        double requested = Moles * (1 - Math.Exp(-rate * seconds));
        TransferTo(ref receiver, Math.Min(requested, EqualPressureMoles(receiver)));
    }

    private readonly double EqualPressureMoles(in StationGas receiver)
    {
        double moles = Moles, volume = _volume + receiver._volume;
        double firstWeight = _volume / volume, secondWeight = receiver._volume / volume;
        if (receiver.HeatCapacity == 0) return moles * secondWeight;

        double temperature = TemperatureKelvin, perMoleEnergy = _energy / moles;
        // Solve Pa(x) = Pb(x) for a packet retaining the donor's composition and
        // temperature. The receiver's temperature changes as its sensible energy mixes.
        double a = perMoleEnergy;
        double b = firstWeight * (receiver._energy + receiver.Moles * perMoleEnergy)
            - secondWeight * (_energy - temperature * receiver.HeatCapacity);
        double c = receiver.HeatCapacity * (_volume * secondWeight) * (Pressure - receiver.Pressure) / GasConstant;
        if (c == 0 && b >= 0) return 0;
        double root = Math.Sqrt(b * b + 4 * a * c);
        double transfer = b >= 0 ? 2 * c / (b + root) : (root - b) / (2 * a);
        return Math.Clamp(transfer, 0, moles);
    }

    private readonly double OpeningRate(double area, double pressureRatio)
    {
        double moles = Moles;
        // kg/mol; fictional plasma and BZ use an air-equivalent transport mass.
        double molarMass = (_oxygen * .031998 + _nitrogen * .028014 + _co2 * .044009
            + _plasma * .029 + _n2o * .044013 + _waterVapor * WaterMolarMass
            + _tritium * .006032 + _bz * .029) / moles;
        double gasConstant = GasConstant * 1000;
        double gamma = 1 + gasConstant * moles / HeatCapacity;
        double sonicSpeed = Math.Sqrt(gamma * gasConstant * TemperatureKelvin / molarMass);
        double criticalRatio = Math.Pow(2 / (gamma + 1), gamma / (gamma - 1));
        double flow;
        if (pressureRatio <= criticalRatio)
            flow = Math.Pow(2 / (gamma + 1), (gamma + 1) / (2 * (gamma - 1)));
        else
        {
            double densityRatio = Math.Pow(pressureRatio, 1 / gamma);
            flow = Math.Sqrt(2 / (gamma - 1) * densityRatio * densityRatio
                * Math.Max(0, 1 - Math.Pow(pressureRatio, (gamma - 1) / gamma)));
        }
        // Isentropic compressible flow with the existing sharp-aperture Cd = .62.
        return .62 * area * sonicSpeed * flow / _volume;
    }
}
