// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Client;

internal readonly record struct SpacecraftThermalState(float CabinHullKelvin, float AirlockHullKelvin,
    float EntryHullKelvin, float CoolantKelvin, bool EnvironmentAvailable = false)
{
    internal const double MinimumKelvin = 2.7, MaximumKelvin = 10000, InitialKelvin = 295.15;
    internal static bool ValidTemperature(double kelvin)
        => double.IsFinite(kelvin) && kelvin is >= MinimumKelvin and <= MaximumKelvin;
}

// Incoming shortwave and longwave irradiance, W/m², already weighted by surface view.
internal readonly record struct SpacecraftRadiation(bool Available, double HullSolar,
    double RadiatorSolar, double HullInfrared, double RadiatorInfrared);

internal sealed partial class ClientSpacecraftSystems
{
    // Effective thermal masses and contacts tune this compartment model; these are
    // not measured construction masses or mesh surface areas.
    private const double CabinHullCapacity = 1_500_000, AirlockHullCapacity = 300_000, EntryHullCapacity = 600_000;
    private const double CabinHullArea = 100, AirlockHullArea = 25, EntryHullArea = 45, CoolantRadiatorArea = 40;
    private const double CabinBulkheadConductance = 120, EntryBulkheadConductance = 60;
    private const double CabinGasConductance = 75, AirlockGasConductance = 12, EntryGasConductance = 24;
    private const double CabinCoolantConductance = 60, AirlockCoolantConductance = 12, EntryCoolantConductance = 24;
    private const double HullEmissivity = .85, HullAbsorptivity = .2;
    private const double CoolantEmissivity = .9, CoolantAbsorptivity = .15;
    private const double HullConvection = 3, CoolantConvection = 5, StefanBoltzmann = 5.670374419e-8;
    private const double CoolingCutoffKelvin = 353.15, MaximumCoolingCop = 3;
    // The service reserve is 1000 abstract units, not litres or kilograms. Its
    // liquid contribution changes with charge; the dry plumbing mass remains.
    private const double DryCoolantCapacity = 80_000, FullCoolantCapacity = 320_000;
    // Retained-heat gameplay coefficients exclude energy leaving in exhaust.
    private const double ApuRetainedHeatRatio = .5, PropulsionRetainedWatts = 15_000, CrewSensibleWatts = 80;
    private double _cabinHullKelvin, _airlockHullKelvin, _entryHullKelvin, _coolantKelvin;
    private bool _thermalEnvironmentAvailable;

    internal SpacecraftThermalState ThermalState => new((float)_cabinHullKelvin, (float)_airlockHullKelvin,
        (float)_entryHullKelvin, (float)_coolantKelvin, _thermalEnvironmentAvailable);

    private void ResetThermal()
    {
        _cabinHullKelvin = _airlockHullKelvin = _entryHullKelvin = _coolantKelvin = SpacecraftThermalState.InitialKelvin;
        _thermalEnvironmentAvailable = false;
    }

    private static double CoolantHeatCapacity(float charge) => DryCoolantCapacity + FullCoolantCapacity * charge;

    // The station has already paid for this transfer. Leaks instead remove liquid
    // at the current temperature, so reducing charge alone preserves loop temperature.
    private void RefillCoolantThermal(float previous, float next)
    {
        if (!float.IsFinite(previous) || !float.IsFinite(next) || previous < 0 || next < previous || next > 1)
            throw new ArgumentOutOfRangeException(nameof(next));
        double incomingCapacity = FullCoolantCapacity * ((double)next - previous);
        _coolantKelvin += (SpacecraftThermalState.InitialKelvin - _coolantKelvin)
            * incomingCapacity / CoolantHeatCapacity(next);
    }

    private void AdvanceThermal(float dt, SpacecraftTelemetry flight, int cabinCrew, int airlockCrew,
        int entryCrew, float effort, double storedWarpJoules)
    {
        bool breathable = Breathable(flight.Region);
        var radiation = flight.Radiation;
        _thermalEnvironmentAvailable = breathable || radiation.Available;
        if (!breathable && radiation.Available
            && (!ValidIrradiance(radiation.HullSolar) || !ValidIrradiance(radiation.RadiatorSolar)
                || !ValidIrradiance(radiation.HullInfrared) || !ValidIrradiance(radiation.RadiatorInfrared)))
            throw new ArgumentOutOfRangeException(nameof(flight), "Spacecraft radiation must be finite and nonnegative.");

        double coolantCapacity = CoolantHeatCapacity(_coolant);
        ExchangeInternalHeat(dt * .5, coolantCapacity);
        AddCrewHeat(ref _cabin, ref _cabinHullKelvin, CabinHullCapacity, cabinCrew * CrewSensibleWatts * dt);
        AddCrewHeat(ref _airlock, ref _airlockHullKelvin, AirlockHullCapacity, airlockCrew * CrewSensibleWatts * dt);
        AddCrewHeat(ref _entry, ref _entryHullKelvin, EntryHullCapacity, entryCrew * CrewSensibleWatts * dt);

        double conditioningWork = 0;
        // The solver reserves this entire branch before workloads run. Pressure
        // includes power, coolant charge, integrity and both common-loop valves.
        double work = ThermalKw * 1000d * dt * (CoolantPressure / CoolantTarget);
        if (work > 0)
        {
            double volume = CabinVolume + AirlockVolume + EntryVolume;
            if (ConnectionUsable(SpacecraftValve.CabinCooling))
                conditioningWork += ConditionAir(ref _cabin, work * CabinVolume / volume, coolantCapacity);
            if (ConnectionUsable(SpacecraftValve.AirlockCooling))
            {
                conditioningWork += ConditionAir(ref _airlock, work * AirlockVolume / volume, coolantCapacity);
                conditioningWork += ConditionAir(ref _entry, work * EntryVolume / volume, coolantCapacity);
            }
        }

        // Unused conditioning work still belongs to the served electrical load.
        // Only energy actually stored by warp charging is excluded from waste heat.
        double electricalHeat = Math.Max(0, _electrical.LoadKw * 1000d * dt - storedWarpJoules - conditioningWork);
        double generatedHeat = electricalHeat + _electrical.GenerationKw * 1000d * dt * ApuRetainedHeatRatio
            + PropulsionRetainedWatts * effort * dt;
        double totalVolume = CabinVolume + AirlockVolume + EntryVolume;
        AddHullHeat(ref _cabinHullKelvin, CabinHullCapacity, generatedHeat * CabinVolume / totalVolume);
        AddHullHeat(ref _airlockHullKelvin, AirlockHullCapacity, generatedHeat * AirlockVolume / totalVolume);
        AddHullHeat(ref _entryHullKelvin, EntryHullCapacity, generatedHeat * EntryVolume / totalVolume);

        if (_thermalEnvironmentAvailable)
        {
            double ambient = OutsideTemperature(flight.Region) + 273.15;
            _cabinHullKelvin = ExchangeExteriorHeat(_cabinHullKelvin, CabinHullCapacity, CabinHullArea,
                HullAbsorptivity, HullEmissivity, radiation.HullSolar, radiation.HullInfrared, HullConvection, breathable, ambient, dt);
            _airlockHullKelvin = ExchangeExteriorHeat(_airlockHullKelvin, AirlockHullCapacity, AirlockHullArea,
                HullAbsorptivity, HullEmissivity, radiation.HullSolar, radiation.HullInfrared, HullConvection, breathable, ambient, dt);
            _entryHullKelvin = ExchangeExteriorHeat(_entryHullKelvin, EntryHullCapacity, EntryHullArea,
                HullAbsorptivity, HullEmissivity, radiation.HullSolar, radiation.HullInfrared, HullConvection, breathable, ambient, dt);
            // This is an effective common loop, not a separately simulated panel.
            if (ConnectionUsable(SpacecraftValve.Radiator) && ConnectionUsable(SpacecraftValve.CoolantFeed))
                _coolantKelvin = ExchangeExteriorHeat(_coolantKelvin, coolantCapacity, CoolantRadiatorArea,
                    CoolantAbsorptivity, CoolantEmissivity, radiation.RadiatorSolar, radiation.RadiatorInfrared,
                    CoolantConvection, breathable, ambient, dt);
        }
        ExchangeInternalHeat(dt * .5, coolantCapacity);
    }

    private void ExchangeInternalHeat(double dt, double coolantCapacity)
    {
        ExchangeSolidHeat(ref _cabinHullKelvin, CabinHullCapacity, ref _airlockHullKelvin, AirlockHullCapacity,
            CabinBulkheadConductance, dt);
        ExchangeSolidHeat(ref _airlockHullKelvin, AirlockHullCapacity, ref _entryHullKelvin, EntryHullCapacity,
            EntryBulkheadConductance, dt);
        if (ConnectionUsable(SpacecraftValve.CoolantFeed))
        {
            ExchangeSolidHeat(ref _cabinHullKelvin, CabinHullCapacity, ref _coolantKelvin, coolantCapacity,
                CabinCoolantConductance, dt);
            ExchangeSolidHeat(ref _airlockHullKelvin, AirlockHullCapacity, ref _coolantKelvin, coolantCapacity,
                AirlockCoolantConductance, dt);
            ExchangeSolidHeat(ref _entryHullKelvin, EntryHullCapacity, ref _coolantKelvin, coolantCapacity,
                EntryCoolantConductance, dt);
        }
        ExchangeGasHeat(ref _cabin, ref _cabinHullKelvin, CabinHullCapacity, CabinGasConductance, dt);
        ExchangeGasHeat(ref _airlock, ref _airlockHullKelvin, AirlockHullCapacity, AirlockGasConductance, dt);
        ExchangeGasHeat(ref _entry, ref _entryHullKelvin, EntryHullCapacity, EntryGasConductance, dt);
    }

    private static double ConductiveHeat(double first, double firstCapacity, double second,
        double secondCapacity, double conductance, double dt)
    {
        double inverseCapacity = 1 / firstCapacity + 1 / secondCapacity;
        return (first - second) / inverseCapacity * (1 - Math.Exp(-conductance * inverseCapacity * dt));
    }

    private static void ExchangeSolidHeat(ref double first, double firstCapacity, ref double second,
        double secondCapacity, double conductance, double dt)
    {
        double heat = ConductiveHeat(first, firstCapacity, second, secondCapacity, conductance, dt);
        first -= heat / firstCapacity;
        second += heat / secondCapacity;
    }

    private static void ExchangeGasHeat(ref SpacecraftAtmosphere air, ref double hull,
        double hullCapacity, double conductance, double dt)
    {
        if (air.HeatCapacity <= 0) return;
        double heat = ConductiveHeat(hull, hullCapacity, air.TemperatureKelvin, air.HeatCapacity, conductance, dt);
        // A gas temperature or pressure limit leaves unaccepted energy in the hull.
        hull -= air.AddThermalEnergy(heat) / hullCapacity;
    }

    private static void AddCrewHeat(ref SpacecraftAtmosphere air, ref double hull, double hullCapacity, double joules)
    {
        double accepted = air.AddThermalEnergy(joules);
        AddHullHeat(ref hull, hullCapacity, Math.Max(0, joules - accepted));
    }

    private static void AddHullHeat(ref double hull, double capacity, double joules)
    {
        if (joules > (SpacecraftThermalState.MaximumKelvin - hull) * capacity)
            throw new InvalidOperationException("Spacecraft retained heat exceeded the 10000 K thermal model limit.");
        hull += joules / capacity;
    }

    private double ConditionAir(ref SpacecraftAtmosphere air, double work, double coolantCapacity)
    {
        double capacity = air.HeatCapacity;
        if (capacity <= 0 || work <= 0) return 0;
        double temperature = air.TemperatureKelvin;
        double target = SpacecraftThermalState.InitialKelvin;
        if (temperature < target)
            return air.AddThermalEnergy(Math.Min(work, (target - temperature) * capacity));
        if (temperature <= target || _coolantKelvin >= CoolingCutoffKelvin) return 0;

        double cop = Math.Min(MaximumCoolingCop, temperature / Math.Max(1, _coolantKelvin - temperature));
        double cooling = Math.Min((temperature - target) * capacity, work * cop);
        // Leave one representable step so rejection cannot round above the cutoff.
        double headroom = Math.Max(0, (Math.BitDecrement(CoolingCutoffKelvin) - _coolantKelvin) * coolantCapacity);
        cooling = Math.Min(cooling, headroom / (1 + 1 / cop));
        double removed = -air.AddThermalEnergy(-cooling);
        double used = removed / cop;
        _coolantKelvin += (removed + used) / coolantCapacity;
        return used;
    }

    private static bool ValidIrradiance(double value) => double.IsFinite(value) && value >= 0;

    private static double ExchangeExteriorHeat(double before, double capacity, double area,
        double absorptivity, double emissivity, double solar, double infrared,
        double convection, bool breathable, double ambient, double dt)
    {
        if (breathable) return ambient + (before - ambient) * Math.Exp(-convection * area * dt / capacity);
        double emission = StefanBoltzmann * emissivity * area;
        double incoming = area * (absorptivity * solar + emissivity * infrared)
            + emission * Fourth(SpacecraftThermalState.MinimumKelvin);
        double maximum = SpacecraftThermalState.MaximumKelvin;
        double maximumResidual = capacity * (maximum - before) - dt * (incoming - emission * Fourth(maximum));
        if (!double.IsFinite(incoming) || maximumResidual < 0)
            throw new InvalidOperationException("Spacecraft radiation exceeded the 10000 K thermal model limit.");

        // Backward Euler has one monotone root. Bracketed Newton iteration keeps
        // stiff radiative exchange inside the finite node's temperature domain.
        double low = SpacecraftThermalState.MinimumKelvin, high = maximum, value = before;
        for (int iteration = 0; iteration < 32; iteration++)
        {
            double residual = capacity * (value - before) - dt * (incoming - emission * Fourth(value));
            if (Math.Abs(residual) <= 1e-7 || high - low <= 1e-10) break;
            if (residual > 0) high = value; else low = value;
            double next = value - residual / (capacity + 4 * dt * emission * value * value * value);
            value = next > low && next < high ? next : (low + high) * .5;
        }
        return value;
    }

    private static double Fourth(double value) { double square = value * value; return square * square; }
}
