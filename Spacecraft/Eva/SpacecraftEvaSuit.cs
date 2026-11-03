// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Text.Json.Serialization;

namespace Modstoke.Client;

internal readonly record struct SpacecraftEvaSuitState(float Pressure, float OxygenFraction,
    float CO2Fraction, float TemperatureC, float OxygenMoles, float BatteryKwh,
    float FilterMoles, bool Powered);

// The host owns each physical suit and advances it only while worn. Docking or
// transferring custody preserves its gas, battery and remaining scrubber capacity.
internal sealed class SpacecraftEvaSuit
{
    internal const int Count = 3;
    internal const double BatteryCapacityKwh = .6, FilterCapacityMoles = 8;
    private const double LoopVolume = .06, ReserveVolume = .004;
    private const double LoopPressure = 60, ReservePressure = 15000;
    private const double OxygenFraction = .35, CO2Fraction = .0004;
    private const double LoadKw = .12, ThermalWatts = 90;
    // Effective suit/occupant thermal mass in J/K and insulation conductance in W/K.
    private const double ThermalCapacity = 12000, Conductance = .5;
    private static readonly double InitialLoopMoles = SpacecraftAtmosphere.ReferenceMoles(LoopPressure * LoopVolume);
    private static readonly double MaximumReserveMoles = SpacecraftAtmosphere.ReferenceMoles(ReservePressure * ReserveVolume);
    private static readonly double MaximumLoopMoles = 250 * LoopVolume / (SpacecraftAtmosphere.GasConstant * 373.15);
    private SpacecraftAtmosphere _loop = new(LoopVolume, LoopPressure, 22, OxygenFraction, CO2Fraction);
    private SpacecraftAtmosphere _reserve = new(ReserveVolume, ReservePressure, 22, 1, 0);
    private double _batteryKwh = BatteryCapacityKwh, _filterMoles = FilterCapacityMoles;

    internal SpacecraftEvaSuitState State => new(_loop.Pressure, _loop.OxygenFraction,
        _loop.CO2Fraction, _loop.Temperature, (float)_reserve.OxygenMoles,
        (float)_batteryKwh, (float)_filterMoles, (float)_batteryKwh > 0);

    internal void Step(float dt, float effort, float ambientTemperatureC)
    {
        if (!float.IsFinite(dt) || dt <= 0 || !float.IsFinite(effort)
            || !float.IsFinite(ambientTemperatureC)) return;
        double seconds = Math.Min(dt, .1), exertion = Math.Clamp(effort, 1, 4);
        // Clamping the environment to 100 C made the 90 W cooler defeat every
        // fire: insulation admitted at most 39 W above the 22 C setpoint.
        double ambient = Math.Clamp(ambientTemperatureC, -273.15, StationGas.MaximumTemperature - 273.15);
        double temperature = _loop.TemperatureKelvin - 273.15;
        double poweredSeconds = Math.Min(seconds, _batteryKwh * 3600 / LoadKw);
        _batteryKwh = Math.Max(0, _batteryKwh - LoadKw * poweredSeconds / 3600);

        _loop.Breathe(SpacecraftAtmosphere.RestingOxygenMolesPerSecond * exertion * seconds);
        if (poweredSeconds > 0 && _filterMoles > 0)
        {
            double before = _loop.Moles;
            _loop.Scrub(Math.Min(_filterMoles, .002 * poweredSeconds), CO2Fraction);
            _filterMoles = Math.Max(0, _filterMoles - (before - _loop.Moles));
        }

        // A mechanical regulator uses the warmer donor/loop temperature so a
        // cold gas packet cannot cause an overshoot of the 21 kPa oxygen target.
        double targetOxygen = 21 * LoopVolume / (SpacecraftAtmosphere.GasConstant
            * Math.Max(_loop.TemperatureKelvin, _reserve.TemperatureKelvin));
        double refill = Math.Min(Math.Max(0, targetOxygen - _loop.OxygenMoles), .01 * seconds);
        refill = Math.Min(refill, Math.Max(0, MaximumLoopMoles - _loop.Moles));
        _reserve.SupplyTo(ref _loop, refill, 1, 0);

        double capacity = ThermalCapacity + _loop.HeatCapacity;
        double mixedTemperature = _loop.TemperatureKelvin - 273.15;
        temperature += (mixedTemperature - temperature) * _loop.HeatCapacity / capacity;
        temperature += (ambient - temperature) * (1 - Math.Exp(-Conductance * seconds / capacity));
        double thermalLimit = ThermalWatts * poweredSeconds / capacity;
        temperature += Math.Clamp(22 - temperature, -thermalLimit, thermalLimit);
        _loop.ChangeTemperature(Math.Clamp(temperature, -100, 100) - mixedTemperature);
    }

    internal SavedSpacecraftEvaSuit Capture() => new()
    {
        Loop = _loop.Capture(), Reserve = _reserve.Capture(),
        BatteryKwh = _batteryKwh, FilterMoles = _filterMoles
    };

    internal static bool TryRestore(SavedSpacecraftEvaSuit? saved, out SpacecraftEvaSuit suit)
    {
        suit = null!;
        if (saved == null || saved.Version != 1
            || !InRange(saved.BatteryKwh, BatteryCapacityKwh)
            || !InRange(saved.FilterMoles, FilterCapacityMoles)
            || !SpacecraftAtmosphere.TryRestore(saved.Loop, LoopVolume, 250, MaximumLoopMoles, out var loop)
            || !SpacecraftAtmosphere.TryRestore(saved.Reserve, ReserveVolume, double.MaxValue,
                MaximumReserveMoles, out var reserve)
            || saved.Reserve!.NitrogenMoles != 0 || saved.Reserve.CO2Moles != 0) return false;
        suit = new SpacecraftEvaSuit
        {
            _loop = loop, _reserve = reserve,
            _batteryKwh = saved.BatteryKwh, _filterMoles = saved.FilterMoles
        };
        return true;
    }

    internal static bool ValidState(SpacecraftEvaSuitState state)
        => InRange(state.Pressure, 250) && InRange(state.OxygenFraction, 1)
            && InRange(state.CO2Fraction, 1) && state.OxygenFraction + state.CO2Fraction <= 1.000001f
            && float.IsFinite(state.TemperatureC) && state.TemperatureC is >= -100 and <= 100
            && InRange(state.OxygenMoles, (float)MaximumReserveMoles)
            && InRange(state.BatteryKwh, (float)BatteryCapacityKwh)
            && InRange(state.FilterMoles, (float)FilterCapacityMoles)
            && state.Powered == (state.BatteryKwh > 0);

    // The caller must first validate physical rack custody and the ship's powered
    // supply/scrubber path. All finite resource transfers commit together.
    internal bool TryService(ref SpacecraftAtmosphere supply, ref float shipBattery01, out string message)
    {
        var source = supply;
        var loop = _loop;
        var reserve = _reserve;
        double loopBefore = loop.Moles;
        loop.Scrub(double.MaxValue, CO2Fraction);
        double removedCO2 = loopBefore - loop.Moles;
        var gas = loop.Capture();
        double oxygen = Math.Max(0, InitialLoopMoles * OxygenFraction - gas.OxygenMoles);
        double nitrogen = Math.Max(0, InitialLoopMoles * (1 - OxygenFraction - CO2Fraction) - gas.NitrogenMoles);
        double co2 = Math.Max(0, InitialLoopMoles * CO2Fraction - gas.CO2Moles);
        double loopFill = oxygen + nitrogen + co2;
        double reserveFill = Math.Max(0, MaximumReserveMoles - reserve.OxygenMoles);
        var available = source.Capture();
        if (available.OxygenMoles < oxygen + reserveFill || available.NitrogenMoles < nitrogen
            || available.CO2Moles < co2 || loop.Moles + loopFill > MaximumLoopMoles)
        {
            message = "EVA service needs more spacecraft breathing-gas reserve.";
            return false;
        }
        source.SupplyTo(ref loop, oxygen, 1, 0);
        source.SupplyTo(ref loop, nitrogen, 0, 0);
        source.SupplyTo(ref loop, co2, 0, 1);
        source.SupplyTo(ref reserve, reserveFill, 1, 0);

        // The existing regenerative scrubber exhaust is a CO2 sink. Regeneration
        // costs 72 kJ/mol; charging is 90% efficient and compression 60% efficient.
        double regeneration = (FilterCapacityMoles - _filterMoles + removedCO2) * .02;
        double compression = reserveFill * SpacecraftAtmosphere.GasConstant * 1000
            * supply.TemperatureKelvin * Math.Max(0, Math.Log(ReservePressure / Math.Max(.001, supply.Pressure))) / .6;
        double conditioning = Math.Abs(SpacecraftAtmosphere.ReferenceTemperature - loop.TemperatureKelvin)
            * (ThermalCapacity + loop.HeatCapacity)
            + Math.Abs(SpacecraftAtmosphere.ReferenceTemperature - reserve.TemperatureKelvin) * reserve.HeatCapacity;
        double energy = (BatteryCapacityKwh - _batteryKwh) / .9 + regeneration
            + (compression + conditioning) / 3600000 + .002;
        double remaining = shipBattery01 * (double)SpacecraftElectrical.BatteryCapacityKwh - energy;
        if (!double.IsFinite(remaining) || remaining < 0)
        {
            message = "EVA service needs more spacecraft battery energy.";
            return false;
        }
        loop.ChangeTemperature(SpacecraftAtmosphere.ReferenceTemperature - loop.TemperatureKelvin);
        reserve.ChangeTemperature(SpacecraftAtmosphere.ReferenceTemperature - reserve.TemperatureKelvin);
        float battery = (float)(remaining / SpacecraftElectrical.BatteryCapacityKwh);
        // Ship storage is float; round the debit conservatively rather than
        // allowing repeated small transactions to mint battery energy.
        if (battery * (double)SpacecraftElectrical.BatteryCapacityKwh > remaining)
            battery = MathF.BitDecrement(battery);
        supply = source;
        shipBattery01 = battery;
        _loop = loop;
        _reserve = reserve;
        _batteryKwh = BatteryCapacityKwh;
        _filterMoles = FilterCapacityMoles;
        message = "EVA suit serviced from spacecraft gas and battery reserves; filter regenerated.";
        return true;
    }

    private static bool InRange(double value, double maximum)
        => double.IsFinite(value) && value >= 0 && value <= maximum;
}

internal sealed class SavedSpacecraftEvaSuit
{
    [JsonRequired]
    public int Version { get; set; } = 1;
    [JsonRequired]
    public SavedSpacecraftAtmosphere? Loop { get; set; }
    [JsonRequired]
    public SavedSpacecraftAtmosphere? Reserve { get; set; }
    [JsonRequired]
    public double BatteryKwh { get; set; }
    [JsonRequired]
    public double FilterMoles { get; set; }
}
