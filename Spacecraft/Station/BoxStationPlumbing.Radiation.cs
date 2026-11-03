// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Immutable;

namespace Modstoke.Client;

internal readonly record struct BoxStationRadiatorState(float SurfaceKelvin, float GasKelvin,
    float Pressure, float GasWatts, float ExternalWatts, float SolarWatts, bool EnvironmentAvailable);

// Direction is in the station's radial-up attitude; irradiances are W/m².
internal readonly record struct BoxStationRadiation(bool Available, double SampleTime,
    OrbitalVector SunDirection, double SolarIrradiance, double PlanetInfrared)
{
    internal const double SolarReference = 1361;
    internal double CollectorExposure => Available
        ? Math.Min(1, Math.Max(0, SunDirection.Y) * SolarIrradiance / SolarReference) : 0;
}

internal sealed partial class BoxStationPlumbing
{
    // Each authored panel has two 1.28 x 2 m coated faces. Ribs and tubing do not
    // add unoccluded emitting area. These are design parameters, not TG constants.
    private const double RadiatorFaceArea = 2.56, RadiatorCapacity = 20 * 900;
    private const double RadiatorEmissivity = .88, RadiatorAbsorptivity = .20, GasMetalConductance = 120;
    private const double StefanBoltzmann = 5.670374419e-8, BackgroundKelvin = 2.7;
    internal const float MaximumRadiatorKelvin = 1000;
    internal const float MaximumRadiatorGasWatts = (float)(GasMetalConductance * StationGas.MaximumTemperature);
    private readonly double[] _radiatorKelvin;
    private readonly float[] _radiatorGasWatts, _radiatorExternalWatts, _radiatorSolarWatts;
    private readonly bool[] _radiatorEnvironmentAvailable;
    private (double Up, double Down)[]? _radiatorSky;
    private double[] _radiatorSunVisibility = Array.Empty<double>();
    private double _radiatorVisibilitySample = double.NaN;

    internal static int GetRadiatorForDevice(BoxStationLayout layout, int device)
    {
        var definitions = LayoutDefinitions.GetValue(layout, BuildDefinitions);
        if ((uint)device >= definitions.Devices.Length) return -1;
        var flow = definitions.Flow[device];
        for (int i = 0; i < definitions.Radiators.Length; i++)
        {
            int network = definitions.Radiators[i].Network;
            if (network == flow.Input || network == flow.Output) return i;
        }
        return -1;
    }

    internal ImmutableArray<BoxStationRadiatorState> RadiatorSnapshot()
    {
        var states = ImmutableArray.CreateBuilder<BoxStationRadiatorState>(Radiators.Length);
        for (int i = 0; i < Radiators.Length; i++)
        {
            var gas = _networks[Radiators[i].Network];
            states.Add(new((float)_radiatorKelvin[i], gas.HeatCapacity > 0 ? (float)gas.TemperatureKelvin : 0,
                (float)gas.Pressure, _radiatorGasWatts[i], _radiatorExternalWatts[i],
                _radiatorSolarWatts[i], _radiatorEnvironmentAvailable[i]));
        }
        return states.MoveToImmutable();
    }

    internal static bool ValidRadiatorState(BoxStationRadiatorState state)
        => float.IsFinite(state.SurfaceKelvin) && state.SurfaceKelvin is >= 2.7f and <= MaximumRadiatorKelvin
            && float.IsFinite(state.GasKelvin) && state.GasKelvin >= 0 && state.GasKelvin <= StationGas.MaximumTemperature
            && float.IsFinite(state.Pressure) && state.Pressure >= 0 && state.Pressure <= BoxStationSystems.MaximumPressure
            && float.IsFinite(state.GasWatts) && Math.Abs(state.GasWatts) <= MaximumRadiatorGasWatts
            && float.IsFinite(state.ExternalWatts) && Math.Abs(state.ExternalWatts) <= 1_000_000
            && float.IsFinite(state.SolarWatts) && state.SolarWatts is >= 0 and <= 1_000_000
            && (state.EnvironmentAvailable || state.ExternalWatts == 0 && state.SolarWatts == 0);

    internal void StepRadiators(double dt, BoxStationRadiation environment)
    {
        if (!double.IsFinite(dt) || dt < 0 || dt > 1) throw new ArgumentOutOfRangeException(nameof(dt));
        if (dt == 0) return;
        EnsureRadiatorExposure(environment);
        for (int i = 0; i < Radiators.Length; i++)
        {
            // Internal exchange remains finite when orbital samples are unavailable.
            // A missing sample must not silently become a cold, permanently dark sky.
            double gasHeat = ExchangeRadiatorGas(i, dt * .5);
            _radiatorExternalWatts[i] = _radiatorSolarWatts[i] = 0;
            _radiatorEnvironmentAvailable[i] = environment.Available;
            if (environment.Available)
            {
                var view = _radiatorSky![i];
                double solar = RadiatorAbsorptivity * RadiatorFaceArea * Math.Abs(environment.SunDirection.Y)
                    * environment.SolarIrradiance * _radiatorSunVisibility[i];
                double emission = StefanBoltzmann * RadiatorEmissivity * RadiatorFaceArea * (view.Up + view.Down);
                // Earth is a uniform longwave source below this Earth-orbiting deck.
                // Structural lower-face visibility scales its projected disk factor.
                double incoming = solar + RadiatorEmissivity * RadiatorFaceArea * view.Down * environment.PlanetInfrared
                    + emission * Fourth(BackgroundKelvin);
                double before = _radiatorKelvin[i];
                double after = RadiatorTemperature(before, incoming, emission, dt);
                _radiatorKelvin[i] = after;
                _radiatorSolarWatts[i] = (float)solar;
                _radiatorExternalWatts[i] = (float)((after - before) * RadiatorCapacity / dt);
            }
            gasHeat += ExchangeRadiatorGas(i, dt * .5);
            // Positive gas watts removes heat from gas; positive external watts adds
            // energy to the panel. Neither quantity is an electrical load.
            _radiatorGasWatts[i] = (float)(gasHeat / dt);
        }
    }

    private double ExchangeRadiatorGas(int index, double dt)
    {
        int network = Radiators[index].Network;
        var gas = _networks[network];
        double capacity = gas.HeatCapacity;
        if (capacity <= 0 || !WithinBounds(gas)) return 0;
        double inverseCapacity = 1 / capacity + 1 / RadiatorCapacity;
        double heat = (gas.TemperatureKelvin - _radiatorKelvin[index]) / inverseCapacity
            * (1 - Math.Exp(-GasMetalConductance * inverseCapacity * dt));
        double transferLimit = GasMetalConductance * Math.Abs(gas.TemperatureKelvin - _radiatorKelvin[index]) * dt;
        heat = Math.Clamp(heat, -transferLimit, transferLimit);
        if (heat > 0)
            heat = Math.Min(heat, (MaximumRadiatorKelvin - _radiatorKelvin[index]) * RadiatorCapacity);
        else
            heat = -Math.Min(-heat, Math.Min(HeatingHeadroom(gas),
                (_radiatorKelvin[index] - BackgroundKelvin) * RadiatorCapacity));
        double metal = Math.Clamp(_radiatorKelvin[index] + heat / RadiatorCapacity, BackgroundKelvin, MaximumRadiatorKelvin);
        // Keep both bodies on the same representable transfer within the conductive budget.
        double represented = (metal - _radiatorKelvin[index]) * RadiatorCapacity;
        if (heat > 0 && represented > heat) metal = Math.BitDecrement(metal);
        else if (heat < 0 && represented < heat) metal = Math.BitIncrement(metal);
        heat = (metal - _radiatorKelvin[index]) * RadiatorCapacity;
        var changed = gas;
        if (!changed.TryAddThermalEnergy(-heat) || !WithinBounds(changed)) return 0;
        _networks[network] = changed;
        _radiatorKelvin[index] = metal;
        return heat;
    }

    private static double RadiatorTemperature(double before, double incoming, double emission, double dt)
    {
        if (emission == 0) return Math.Clamp(before + incoming * dt / RadiatorCapacity, BackgroundKelvin, MaximumRadiatorKelvin);
        // Backward Euler has a unique monotone root. The equilibrium brackets the
        // step with the previous temperature, so large thermal loads cannot overshoot.
        double equilibrium = Math.Clamp(Math.Sqrt(Math.Sqrt(incoming / emission)), BackgroundKelvin, MaximumRadiatorKelvin);
        double low = Math.Min(before, equilibrium), high = Math.Max(before, equilibrium), value = before;
        for (int iteration = 0; iteration < 12; iteration++)
        {
            double residual = RadiatorCapacity * (value - before) - dt * (incoming - emission * Fourth(value));
            if (Math.Abs(residual) <= 1e-7 || high - low <= 1e-10) break;
            if (residual > 0) high = value; else low = value;
            double next = value - residual / (RadiatorCapacity + 4 * dt * emission * value * value * value);
            value = next > low && next < high ? next : (low + high) * .5;
        }
        return Math.Clamp(value, BackgroundKelvin, MaximumRadiatorKelvin);
    }

    private static double Fourth(double value) { double square = value * value; return square * square; }

    private void EnsureRadiatorExposure(BoxStationRadiation environment)
    {
        if (_radiatorSky == null)
        {
            _radiatorSky = new (double, double)[Radiators.Length];
            _radiatorSunVisibility = new double[Radiators.Length];
            // Fixed cosine-weighted quadrature estimates the static hull view.
            // Moving doors, equipment shadows and reflections are not included.
            for (int i = 0; i < Radiators.Length; i++)
            {
                var panel = Radiators[i];
                double upper = 0, lower = 0;
                for (int ray = 0; ray < 16; ray++)
                {
                    double vertical = Math.Sqrt((ray + .5) / 16), horizontal = Math.Sqrt(1 - vertical * vertical);
                    double angle = ray * 2.399963229728653;
                    var direction = new OrbitalVector(horizontal * Math.Cos(angle), vertical, horizontal * Math.Sin(angle));
                    if (RadiatorRayClear(panel, direction)) upper += 1.0 / 16;
                    if (RadiatorRayClear(panel, direction with { Y = -vertical })) lower += 1.0 / 16;
                }
                _radiatorSky[i] = (upper, lower * UnderPanelTransmission(panel.Cell));
            }
        }
        if (!environment.Available || environment.SampleTime == _radiatorVisibilitySample) return;
        _radiatorVisibilitySample = environment.SampleTime;
        for (int i = 0; i < Radiators.Length; i++)
            _radiatorSunVisibility[i] = RadiatorRayClear(Radiators[i], environment.SunDirection)
                ? environment.SunDirection.Y < 0 ? UnderPanelTransmission(Radiators[i].Cell) : 1 : 0;
    }

    private double UnderPanelTransmission(int cell)
    {
        var flags = _layout.Cells[cell].Flags;
        // Open gratings have a finite obscured area; use the authored deck family.
        return (flags & BoxStationCellFlags.Catwalk) != 0 ? .75
            : (flags & BoxStationCellFlags.Lattice) != 0 ? .90 : 1;
    }

    private bool RadiatorRayClear(BoxStationRadiator panel, OrbitalVector direction)
    {
        const double tile = BoxStationLayout.TileSize, roof = BoxStationLayout.DeckHeight;
        int width = _layout.Width, rows = _layout.Height;
        int x = panel.Cell % width, row = panel.Cell / width;
        double dx = direction.X, dr = -direction.Z, dy = direction.Y;
        double height = (dy >= 0 ? .102 : .088) + (panel.Layer - 1) * .1;
        double stepX = dx == 0 ? double.PositiveInfinity : tile / Math.Abs(dx);
        double stepRow = dr == 0 ? double.PositiveInfinity : tile / Math.Abs(dr);
        double nextX = stepX * .5, nextRow = stepRow * .5;
        double end = dy > 0 ? (roof - height) / dy : dy < 0 ? -height / dy : double.PositiveInfinity;
        for (int step = 0; step <= width + rows && x >= 0 && x < width && row >= 0 && row < rows; step++)
        {
            var cell = _layout.Cells[row * width + x];
            double exit = Math.Min(end, Math.Min(nextX, nextRow));
            if ((cell.Flags & (BoxStationCellFlags.Wall | BoxStationCellFlags.FullWindow)) != 0) return false;
            if (exit == end)
                return dy > 0 ? (cell.Flags & BoxStationCellFlags.Roof) == 0
                    : dy < 0 ? (cell.Flags & BoxStationCellFlags.Floor) == 0 : true;
            var barrier = nextX <= nextRow
                ? dx > 0 ? cell.East : cell.West
                : dr > 0 ? cell.South : cell.North;
            if ((barrier & (BoxStationBarrier.Wall | BoxStationBarrier.Window)) != 0) return false;
            if (nextX <= exit) { x += Math.Sign(dx); nextX += stepX; }
            if (nextRow <= exit) { row += Math.Sign(dr); nextRow += stepRow; }
        }
        return true;
    }
}

