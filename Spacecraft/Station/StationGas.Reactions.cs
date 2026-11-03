// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Client;

// ReactionJoules includes all committed heat; CombustionJoules is its fire-producing subset.
internal readonly record struct StationGasReaction(double CombustionJoules, double ReactionJoules);

internal partial struct StationGas
{
    private const double ReactionInterval = .5, IgnitionTemperature = 373.15;
    private const double N2OChemicalJoules = 200_000, PlasmaChemicalJoules = 3_280_000,
        TritiumChemicalJoules = 280_000, BZChemicalJoules = 6_560_000;

    internal readonly double ChemicalJoules => _n2o * N2OChemicalJoules + _plasma * PlasmaChemicalJoules
        + _tritium * TritiumChemicalJoules + _bz * BZChemicalJoules;

    internal StationGasReaction React(double dt)
    {
        if (!double.IsFinite(dt) || dt <= 0 || dt > .1 || !WithinReactionBounds() || Moles == 0)
            return default;

        // Source calls advance 0.5 s. Use their extents as rates over bounded
        // steps, with fixed mole/energy gates and BZ yield scaled per 2.5 m³.
        // Candidate presence and ignition temperature are sampled before reactions;
        // each body uses the current mixture. New fuel can react on the next step.
        double step = dt / ReactionInterval, cells = _volume / SourceCellVolume;
        if (cells <= 0) return default;
        double temperature = TemperatureKelvin;
        bool decompose = _n2o > 0 && temperature >= 1400;
        bool burnTritium = _tritium > 0 && _oxygen > 0 && temperature >= IgnitionTemperature;
        bool burnPlasma = _plasma > 0 && _oxygen > 0 && temperature >= IgnitionTemperature;

        double reaction = FormBZ(step, cells);
        if (decompose) reaction += DecomposeN2O(step, cells);
        double combustion = burnTritium ? BurnTritium(step, cells) : 0;
        if (burnPlasma) combustion += BurnPlasma(step, cells);
        // These reactions omit condensation, advanced chemistry and tritium radiation.
        return new(combustion, reaction + combustion);
    }

    private double FormBZ(double step, double cells)
    {
        if (_n2o < 10 * cells || _plasma < 10 * cells || Pressure <= 0) return 0;
        double extent = Math.Min(cells * 10.1325 / Pressure / Math.Max(_plasma / _n2o, 1),
            Math.Min(_n2o, _plasma / 2)) * step;
        if (extent <= 0) return 0;
        var changed = this;
        changed._n2o -= extent;
        changed._plasma -= 2 * extent;
        changed._bz += extent;
        // Retain the BZ product instead of the source's terminal BZ-to-oxygen
        // conversion: 2 plasma + N2O -> BZ releases exactly 200 kJ per extent.
        return CommitReaction(changed, N2OChemicalJoules * extent);
    }

    private double DecomposeN2O(double step, double cells)
    {
        if (_n2o < .01 * cells) return 0;
        double temperature = TemperatureKelvin;
        // Preserve the source polynomial's zero rate at and above 100,000 K.
        double fraction = Math.Max(0, .00002 * (temperature - .00001 * temperature * temperature));
        double extent = Math.Min(_n2o, fraction * _n2o * step);
        if (extent <= 0) return 0;
        var changed = this;
        changed._n2o -= extent;
        changed._nitrogen += extent;
        changed._oxygen += .5 * extent;
        return CommitReaction(changed, N2OChemicalJoules * extent);
    }

    private double BurnTritium(double step, double cells)
    {
        if (_tritium < .01 * cells || _oxygen < .01 * cells) return 0;
        // Use actual source fuel consumption, not its inflated "burned fuel"
        // counter. Both branches pay 0.5 O2 per tritium and produce one vapor.
        double sourceExtent = _oxygen < _tritium || _energy < 2_000_000 * cells
            ? _oxygen / 100 : _tritium / 10;
        double extent = Math.Min(sourceExtent * step, Math.Min(_tritium, _oxygen * 2));
        if (extent <= 0) return 0;
        var changed = this;
        changed._tritium -= extent;
        changed._oxygen -= .5 * extent;
        changed._waterVapor += extent;
        return CommitReaction(changed, TritiumChemicalJoules * extent);
    }

    private double BurnPlasma(double step, double cells)
    {
        if (_plasma < .01 * cells || _oxygen < .01 * cells) return 0;
        double scale = Math.Clamp((TemperatureKelvin - IgnitionTemperature) / 1270, 0, 1);
        if (scale <= 0) return 0;
        double oxygenCost = 1.4 - scale;
        double sourceExtent = scale * Math.Min(_plasma, _oxygen / 10) / 9;
        if (sourceExtent <= .0003 * cells) return 0;
        double extent = Math.Min(sourceExtent * step, Math.Min(_plasma, _oxygen / oxygenCost));
        if (extent <= 0) return 0;
        bool supersaturated = _oxygen / _plasma > 96;
        var changed = this;
        changed._plasma -= extent;
        changed._oxygen -= oxygenCost * extent;
        if (supersaturated)
        {
            changed._tritium += extent;
        }
        else
        {
            changed._co2 += extent;
            changed._waterVapor += .25 * extent;
        }
        // Normal combustion releases 3.28 MJ/mol. The tritium branch retains
        // 0.28 MJ/mol in its product and releases the source's 3 MJ/mol.
        double heat = PlasmaChemicalJoules - (supersaturated ? TritiumChemicalJoules : 0);
        return CommitReaction(changed, heat * extent);
    }

    private double CommitReaction(StationGas changed, double joules)
    {
        if (!double.IsFinite(joules) || joules <= 0) return 0;
        double released = ChemicalJoules - changed.ChemicalJoules;
        if (!double.IsFinite(released) || released <= 0) return 0;
        double budget = Math.Min(joules, released);
        changed._energy = _energy + budget;
        if (!double.IsFinite(changed._energy)) return 0;
        // Tiny extents must debit representable chemical energy before adding
        // heat. Round toward that paid budget, including the sensible addition.
        if (changed._energy - _energy > budget) changed._energy = Math.BitDecrement(changed._energy);
        double added = changed._energy - _energy;
        if (added <= 0 || added > budget || !changed.WithinReactionBounds()) return 0;
        this = changed;
        return added;
    }

    private readonly bool WithinReactionBounds()
        => HasFiniteState() && Pressure <= BoxStationSystems.MaximumPressure
            && Moles <= _volume * BoxStationSystems.MaximumMolesPerCubicMeter;
}
