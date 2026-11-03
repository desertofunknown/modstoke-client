// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Text.Json.Serialization;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftSystems
{
    private static readonly double MaximumReserveMoles = new SpacecraftAtmosphere(1, GasReserveCapacity, 22).Moles;

    internal SavedSpacecraftSystems Capture() => new()
    {
        Cabin = _cabin.Capture(), Airlock = _airlock.Capture(), Entry = _entry.Capture(), Reserve = _reserve.Capture(),
        Fuel = _fuel, Battery = _battery, WarpCharge = _warp, Coolant = _coolant,
        Thermal = new() { CabinHullKelvin = _cabinHullKelvin, AirlockHullKelvin = _airlockHullKelvin,
            EntryHullKelvin = _entryHullKelvin, CoolantKelvin = _coolantKelvin },
        Fire = CaptureFire(),
        WarpDischargeEnabled = _warpDischargeEnabled,
        HullIntegrity = _hull.Integrity01,
        HullSections = new() { Cabin = _hull.Cabin, Airlock = _hull.Airlock, Entry = _hull.Entry },
        EngineIntegrity = _engine, PowerIntegrity = _power,
        LifeSupportIntegrity = _lifeSupport, WarpIntegrity = _warpIntegrity,
        CabinLeak = _cabinLeak, AirlockLeak = _airlockLeak, EntryLeak = _entryLeak, CoolantLeak = _coolantLeak,
        CabinSealSeconds = _cabinSealSeconds, AirlockSealSeconds = _airlockSealSeconds, EntrySealSeconds = _entrySealSeconds,
        ScrubberEnabled = _scrubber, GeneratorEnabled = _generator, BusIsolated = _busIsolated,
        InnerDoorOpen = _innerOpen, CabinDoorOpen = _cabinOpen, EmergencyOverride = _override,
        AirlockVenting = _venting, ApuIsolated = _apuIsolated, ValveMask = _valveMask,
        ApuGuardOpen = _apuGuardOpen, ApuGuardAmount = _apuGuardAmount,
        ClosedCircuits = _closedCircuits, TrippedCircuits = _trippedCircuits, LineFaultMask = _lineFaultMask,
        CircuitSpares = _circuitSpares, LineSpares = _lineSpares, HullPlates = _hullPlates,
        SelectedDestination = (int)_destination, AirlockCycle = (int)_airlockCycle,
        AirlockTarget = (int)_airlockTarget, AirlockFault = (int)_airlockFault
    };

    internal static bool IsValid(SavedSpacecraftSystems? saved)
        => TryRead(saved, out _, out _, out _, out _);

    internal bool TryRestore(SavedSpacecraftSystems? saved)
    {
        if (saved == null || !TryRead(saved, out var cabin, out var airlock, out var entry, out var reserve))
            return false;
        _cabin = cabin;
        _airlock = airlock;
        _entry = entry;
        _reserve = reserve;
        _fuel = saved.Fuel;
        _battery = saved.Battery;
        _warp = saved.WarpCharge;
        _warpDischargeEnabled = saved.Version >= 8 && saved.WarpDischargeEnabled!.Value;
        _coolant = saved.Coolant;
        // Older saves retain their gas heat and finite supplies; only the new metal stores need an initial temperature.
        _cabinHullKelvin = saved.Thermal?.CabinHullKelvin ?? cabin.TemperatureKelvin;
        _airlockHullKelvin = saved.Thermal?.AirlockHullKelvin ?? airlock.TemperatureKelvin;
        _entryHullKelvin = saved.Thermal?.EntryHullKelvin ?? entry.TemperatureKelvin;
        _coolantKelvin = saved.Thermal?.CoolantKelvin ?? 295.15;
        _thermalEnvironmentAvailable = false;
        if (saved.Version < 11) ResetFire();
        else RestoreFire(saved.Fire!);
        // Old saves carry no impact location; retain their damage in all sections.
        _hull = saved.Version < 9 ? new(saved.HullIntegrity, saved.HullIntegrity, saved.HullIntegrity)
            : new(saved.HullSections!.Cabin, saved.HullSections.Airlock, saved.HullSections.Entry);
        _engine = saved.EngineIntegrity;
        _power = saved.PowerIntegrity;
        _lifeSupport = saved.LifeSupportIntegrity;
        _warpIntegrity = saved.WarpIntegrity;
        _cabinLeak = saved.CabinLeak;
        _airlockLeak = saved.AirlockLeak;
        _entryLeak = saved.EntryLeak;
        _coolantLeak = saved.CoolantLeak;
        _cabinSealSeconds = saved.Version < 5 ? 0 : saved.CabinSealSeconds!.Value;
        _airlockSealSeconds = saved.Version < 5 ? 0 : saved.AirlockSealSeconds!.Value;
        _entrySealSeconds = saved.Version < 5 ? 0 : saved.EntrySealSeconds!.Value;
        _scrubber = saved.ScrubberEnabled;
        _generator = saved.GeneratorEnabled;
        _busIsolated = saved.Version >= 6 && saved.BusIsolated!.Value;
        _innerOpen = saved.InnerDoorOpen;
        _cabinOpen = saved.CabinDoorOpen;
        _override = saved.EmergencyOverride;
        _venting = saved.AirlockVenting;
        _apuIsolated = saved.ApuIsolated;
        _apuGuardOpen = saved.Version >= 3 && saved.ApuGuardOpen!.Value;
        _apuGuardAmount = saved.Version < 3 ? 0 : saved.ApuGuardAmount!.Value;
        _valveMask = saved.ValveMask;
        // Version 1 predates protective coupling faults; migrate healthy lines
        // without replenishing resources or changing the saved manual controls.
        _lineFaultMask = saved.Version == 1 ? (ushort)0 : saved.LineFaultMask!.Value;
        _closedCircuits = saved.ClosedCircuits;
        _trippedCircuits = saved.TrippedCircuits;
        // Older saves predate finite repair stock; subsequent captures retain the migrated count.
        _circuitSpares = saved.Version < 4 ? CircuitSparesCapacity : saved.CircuitSpares!.Value;
        _lineSpares = saved.Version < 4 ? LineSparesCapacity : saved.LineSpares!.Value;
        _hullPlates = saved.Version < 7 ? (byte)0 : saved.HullPlates!.Value;
        _destination = (SpacecraftRegion)saved.SelectedDestination;
        _airlockCycle = (SpacecraftAirlockPhase)saved.AirlockCycle;
        _airlockTarget = (SpacecraftAirlockTarget)saved.AirlockTarget;
        _airlockFault = (SpacecraftAirlockFault)saved.AirlockFault;
        // A restored world has no continuous door telemetry or elapsed cycle time.
        // Cancel its bypass without changing the saved manual controls or gas.
        if (AirlockCycleActive) StopAirlockCycle(SpacecraftAirlockFault.Cancelled);
        _airlockPhaseSeconds = 0;
        _travel = SpacecraftTravel.None;
        _flight = default;
        ResetElectricalReadings();
        RefreshElectrical(_flight);
        return true;
    }

    private static bool TryRead(SavedSpacecraftSystems? saved, out SpacecraftAtmosphere cabin,
        out SpacecraftAtmosphere airlock, out SpacecraftAtmosphere entry, out SpacecraftAtmosphere reserve)
    {
        cabin = airlock = entry = reserve = default;
        if (saved == null || saved.Version is < 1 or > 11
            || (saved.Version == 1 ? saved.LineFaultMask.HasValue
                : !saved.LineFaultMask.HasValue || saved.LineFaultMask.Value > 0xfff)
            || (saved.Version < 3 ? saved.ApuGuardOpen.HasValue || saved.ApuGuardAmount.HasValue
                : !saved.ApuGuardOpen.HasValue || !saved.ApuGuardAmount.HasValue || !InRange(saved.ApuGuardAmount.Value, 1))
            || (saved.Version < 4 ? saved.CircuitSpares.HasValue || saved.LineSpares.HasValue
                : !saved.CircuitSpares.HasValue || !saved.LineSpares.HasValue
                    || saved.CircuitSpares.Value > CircuitSparesCapacity || saved.LineSpares.Value > LineSparesCapacity)
            || (saved.Version < 5 ? saved.CabinSealSeconds.HasValue || saved.AirlockSealSeconds.HasValue || saved.EntrySealSeconds.HasValue
                : !saved.CabinSealSeconds.HasValue || !saved.AirlockSealSeconds.HasValue || !saved.EntrySealSeconds.HasValue
                    || !InRange(saved.CabinSealSeconds.Value, SealantCycleSeconds)
                    || !InRange(saved.AirlockSealSeconds.Value, SealantCycleSeconds)
                    || !InRange(saved.EntrySealSeconds.Value, SealantCycleSeconds))
            || (saved.Version < 6 ? saved.BusIsolated.HasValue : !saved.BusIsolated.HasValue)
            || (saved.Version < 7 ? saved.HullPlatesSpecified
                : !saved.HullPlates.HasValue || saved.HullPlates.Value > HullPlatesCapacity)
            || (saved.Version < 8 ? saved.WarpDischargeEnabledSpecified : !saved.WarpDischargeEnabled.HasValue)
            || !ValidSavedHull(saved)
            || !ValidSavedThermal(saved)
            || (saved.Version < 11 ? saved.FireSpecified : !ValidSavedFire(saved.Fire))
            || !InRange(saved.Fuel, 1) || !InRange(saved.Battery, 1) || !InRange(saved.WarpCharge, 1)
            || !InRange(saved.Coolant, 1) || !InRange(saved.HullIntegrity, 1) || !InRange(saved.EngineIntegrity, 1)
            || !InRange(saved.PowerIntegrity, 1) || !InRange(saved.LifeSupportIntegrity, 1)
            || !InRange(saved.WarpIntegrity, 1) || !InRange(saved.CabinLeak, 2)
            || !InRange(saved.AirlockLeak, 2) || !InRange(saved.EntryLeak, 2) || !InRange(saved.CoolantLeak, .02f)
            || saved.ValveMask > 0xfff || (saved.ClosedCircuits | saved.TrippedCircuits) > SpacecraftElectrical.AllMask
            || saved.AirlockVenting != ((saved.ValveMask & (1 << (int)SpacecraftValve.AirlockVent)) != 0)
            || saved.ApuIsolated && saved.GeneratorEnabled
            || saved.BusIsolated == true && saved.GeneratorEnabled
            || saved.WarpDischargeEnabled == true
                && ((saved.ClosedCircuits & (1 << (int)SpacecraftCircuit.Warp)) != 0
                    || (saved.ValveMask & (1 << (int)SpacecraftValve.WarpFuel)) != 0)
            || (saved.SelectedDestination != (int)SpacecraftRegion.Island
                && saved.SelectedDestination != (int)SpacecraftRegion.TestWorld
                && saved.SelectedDestination != (int)SpacecraftRegion.Moon)
            || !ValidAirlock(saved)) return false;
        return SpacecraftAtmosphere.TryRestore(saved.Cabin, CabinVolume, 250, double.MaxValue, out cabin)
            && SpacecraftAtmosphere.TryRestore(saved.Airlock, AirlockVolume, 250, double.MaxValue, out airlock)
            && SpacecraftAtmosphere.TryRestore(saved.Entry, EntryVolume, 250, double.MaxValue, out entry)
            && SpacecraftAtmosphere.TryRestore(saved.Reserve, 1, double.MaxValue,
                MaximumReserveMoles, out reserve);
    }

    private static bool ValidSavedHull(SavedSpacecraftSystems saved)
    {
        if (saved.Version < 9) return !saved.HullSectionsSpecified;
        var hull = saved.HullSections;
        return hull != null && InRange(hull.Cabin, 1) && InRange(hull.Airlock, 1) && InRange(hull.Entry, 1)
            && MathF.Abs(saved.HullIntegrity - new SpacecraftHullState(hull.Cabin, hull.Airlock, hull.Entry).Integrity01) <= .000001f;
    }

    private static bool ValidSavedThermal(SavedSpacecraftSystems saved)
    {
        if (saved.Version < 10) return !saved.ThermalSpecified;
        var thermal = saved.Thermal;
        return thermal != null && SpacecraftThermalState.ValidTemperature(thermal.CabinHullKelvin)
            && SpacecraftThermalState.ValidTemperature(thermal.AirlockHullKelvin)
            && SpacecraftThermalState.ValidTemperature(thermal.EntryHullKelvin)
            && SpacecraftThermalState.ValidTemperature(thermal.CoolantKelvin);
    }

    private static bool ValidAirlock(SavedSpacecraftSystems saved)
    {
        if (saved.AirlockCycle is < (int)SpacecraftAirlockPhase.Idle or > (int)SpacecraftAirlockPhase.Fault
            || saved.AirlockTarget is < (int)SpacecraftAirlockTarget.None or > (int)SpacecraftAirlockTarget.Entry
            || saved.AirlockFault is < (int)SpacecraftAirlockFault.None or > (int)SpacecraftAirlockFault.PressureTimeout)
            return false;
        if (saved.AirlockCycle == (int)SpacecraftAirlockPhase.Idle)
            return saved.AirlockTarget == (int)SpacecraftAirlockTarget.None
                && saved.AirlockFault == (int)SpacecraftAirlockFault.None;
        if (saved.AirlockTarget == (int)SpacecraftAirlockTarget.None) return false;
        if ((saved.AirlockCycle == (int)SpacecraftAirlockPhase.Sealing
                || saved.AirlockCycle == (int)SpacecraftAirlockPhase.Equalizing)
            && (saved.InnerDoorOpen || saved.CabinDoorOpen || saved.EmergencyOverride || saved.AirlockVenting))
            return false;
        return saved.AirlockCycle == (int)SpacecraftAirlockPhase.Fault
            ? saved.AirlockFault != (int)SpacecraftAirlockFault.None
            : saved.AirlockFault == (int)SpacecraftAirlockFault.None;
    }

    private static bool InRange(float value, float maximum)
        => float.IsFinite(value) && value >= 0 && value <= maximum;
}

internal sealed class SavedSpacecraftSystems
{
    [JsonRequired]
    public int Version { get; set; } = 11;
    [JsonRequired]
    public SavedSpacecraftAtmosphere? Cabin { get; set; }
    [JsonRequired]
    public SavedSpacecraftAtmosphere? Airlock { get; set; }
    [JsonRequired]
    public SavedSpacecraftAtmosphere? Entry { get; set; }
    [JsonRequired]
    public SavedSpacecraftAtmosphere? Reserve { get; set; }
    [JsonRequired]
    public float Fuel { get; set; }
    [JsonRequired]
    public float Battery { get; set; }
    [JsonRequired]
    public float WarpCharge { get; set; }
    // Version 8 requires the discharge selection; earlier saves leave it off.
    private bool? _warpDischargeEnabled;
    public bool? WarpDischargeEnabled
    {
        get => _warpDischargeEnabled;
        set { _warpDischargeEnabled = value; WarpDischargeEnabledSpecified = true; }
    }
    internal bool WarpDischargeEnabledSpecified { get; private set; }
    [JsonRequired]
    public float Coolant { get; set; }
    private SavedSpacecraftThermal? _thermal;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SavedSpacecraftThermal? Thermal
    {
        get => _thermal;
        set { _thermal = value; ThermalSpecified = true; }
    }
    internal bool ThermalSpecified { get; private set; }
    private SavedSpacecraftFire? _fire;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SavedSpacecraftFire? Fire
    {
        get => _fire;
        set { _fire = value; FireSpecified = true; }
    }
    internal bool FireSpecified { get; private set; }
    [JsonRequired]
    public float HullIntegrity { get; set; }
    private SavedSpacecraftHull? _hullSections;
    public SavedSpacecraftHull? HullSections
    {
        get => _hullSections;
        set { _hullSections = value; HullSectionsSpecified = true; }
    }
    internal bool HullSectionsSpecified { get; private set; }
    [JsonRequired]
    public float EngineIntegrity { get; set; }
    [JsonRequired]
    public float PowerIntegrity { get; set; }
    [JsonRequired]
    public float LifeSupportIntegrity { get; set; }
    [JsonRequired]
    public float WarpIntegrity { get; set; }
    [JsonRequired]
    public float CabinLeak { get; set; }
    [JsonRequired]
    public float AirlockLeak { get; set; }
    [JsonRequired]
    public float EntryLeak { get; set; }
    [JsonRequired]
    public float CoolantLeak { get; set; }
    // Version 5 retains loaded cartridges as remaining work, including zero for idle units.
    public float? CabinSealSeconds { get; set; }
    public float? AirlockSealSeconds { get; set; }
    public float? EntrySealSeconds { get; set; }
    [JsonRequired]
    public bool ScrubberEnabled { get; set; }
    [JsonRequired]
    public bool GeneratorEnabled { get; set; }
    // Version 6 requires the manual bus selection; earlier saves restore it connected.
    public bool? BusIsolated { get; set; }
    [JsonRequired]
    public bool InnerDoorOpen { get; set; }
    [JsonRequired]
    public bool CabinDoorOpen { get; set; }
    [JsonRequired]
    public bool EmergencyOverride { get; set; }
    [JsonRequired]
    public bool AirlockVenting { get; set; }
    [JsonRequired]
    public bool ApuIsolated { get; set; }
    // Earlier versions had only a local, non-persistent guard animation.
    public bool? ApuGuardOpen { get; set; }
    public float? ApuGuardAmount { get; set; }
    [JsonRequired]
    public ushort ValveMask { get; set; }
    // Optional only for version 1; version 2 validation requires this field.
    public ushort? LineFaultMask { get; set; }
    [JsonRequired]
    public byte ClosedCircuits { get; set; }
    [JsonRequired]
    public byte TrippedCircuits { get; set; }
    // Version 4 requires both counts, including zero when a stock is depleted.
    public byte? CircuitSpares { get; set; }
    public byte? LineSpares { get; set; }
    // Version 7 requires plate stock; earlier saves start without imported steel.
    private byte? _hullPlates;
    public byte? HullPlates
    {
        get => _hullPlates;
        set { _hullPlates = value; HullPlatesSpecified = true; }
    }
    internal bool HullPlatesSpecified { get; private set; }
    [JsonRequired]
    public int SelectedDestination { get; set; }
    [JsonRequired]
    public int AirlockCycle { get; set; }
    [JsonRequired]
    public int AirlockTarget { get; set; }
    [JsonRequired]
    public int AirlockFault { get; set; }
}

internal sealed class SavedSpacecraftHull
{
    [JsonRequired]
    public float Cabin { get; set; }
    [JsonRequired]
    public float Airlock { get; set; }
    [JsonRequired]
    public float Entry { get; set; }
}

internal sealed class SavedSpacecraftThermal
{
    [JsonRequired]
    public double CabinHullKelvin { get; set; }
    [JsonRequired]
    public double AirlockHullKelvin { get; set; }
    [JsonRequired]
    public double EntryHullKelvin { get; set; }
    [JsonRequired]
    public double CoolantKelvin { get; set; }
}

internal sealed class SavedSpacecraftAtmosphere
{
    [JsonRequired]
    public double OxygenMoles { get; set; }
    [JsonRequired]
    public double NitrogenMoles { get; set; }
    [JsonRequired]
    public double CO2Moles { get; set; }
    [JsonRequired]
    public double ThermalJoules { get; set; }
}
