// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Client;

internal enum SpacecraftValve
{
    CabinSupply = 0,
    AirlockSupply = 1,
    CabinScrubber = 2,
    AirlockScrubber = 3,
    CabinCooling = 4,
    AirlockCooling = 5,
    EngineFuel = 6,
    ApuFuel = 7,
    WarpFuel = 8,
    Radiator = 9,
    CoolantFeed = 10,
    AirlockVent = 11
}

internal enum SpacecraftAirlockPhase { Idle, Sealing, Equalizing, PassageRequested, Fault }
internal enum SpacecraftAirlockTarget { None, Cabin, Entry }
internal enum SpacecraftAirlockFault
{
    None, Cancelled, Obstructed, PowerLost, TravelStarted, OverrideEnabled,
    SealLost, SealTimeout, PressureTimeout
}

internal readonly record struct SpacecraftSystemState(float CabinPressure, float OxygenFraction,
    float CabinTemperatureC, float OxygenReserve01, float Fuel01, float Battery01,
    bool ScrubberEnabled, bool GeneratorEnabled, bool InnerDoorOpen, bool EmergencyOverride,
    float CO2Fraction, float AirlockPressure, float WarpCharge01, bool Ready = true,
    bool AirlockVenting = false, float AirlockOxygenFraction = .209f, float AirlockCO2Fraction = .0004f,
    float AirlockTemperatureC = 22, SpacecraftRegion SelectedDestination = SpacecraftRegion.TestWorld,
    SpacecraftHullState Hull = default, float EngineIntegrity01 = 1, float PowerIntegrity01 = 1,
    float LifeSupportIntegrity01 = 1, float WarpIntegrity01 = 1,
    bool CabinDoorOpen = true, bool CrewLocked = false,
    ushort ValveMask = 0x7ff, float Coolant01 = 1, bool ApuIsolated = false,
    float AirFeedPressure = 0, float FuelFeedPressure = 0, float CoolantPressure = 0,
    float EntryPressure = 101, float EntryOxygenFraction = .209f, float EntryCO2Fraction = .0004f,
    float EntryTemperatureC = 22,
    SpacecraftAirlockPhase AirlockCycle = SpacecraftAirlockPhase.Idle,
    SpacecraftAirlockTarget AirlockTarget = SpacecraftAirlockTarget.None,
    SpacecraftAirlockFault AirlockFault = SpacecraftAirlockFault.None,
    SpacecraftElectricalState Electrical = default, ushort LineFaultMask = 0,
    bool ApuGuardOpen = false, float ApuGuardAmount = 0,
    byte CircuitSpares = 0, byte LineSpares = 0, float ShoreCoupling01 = 0,
    bool ShoreAirEnabled = false, ShoreAirStatus ShoreAirStatus = ShoreAirStatus.Off,
    float ShoreAirMolesPerSecond = 0, ushort ShoreAirWatts = 0,
    bool ShoreFuelEnabled = false, ShoreFuelStatus ShoreFuelStatus = ShoreFuelStatus.Off,
    bool ShoreCoolantEnabled = false, ShoreCoolantStatus ShoreCoolantStatus = ShoreCoolantStatus.Off,
    bool CoolantLeak = false, SpacecraftSealantState Sealant = default, bool BusIsolated = false, byte HullPlates = 0,
    bool WarpDischargeEnabled = false, SpacecraftThermalState Thermal = default, SpacecraftFireState Fire = default)
{
    internal float HullIntegrity01 => Hull.Integrity01;
}

internal readonly record struct SpacecraftTelemetry(bool Powered, bool Grounded, SpacecraftRegion Region,
    SpacecraftTravel Travel, bool DoorOpen, float DoorAmount, float Effort,
    float SpeedSquared, float AngularSpeedSquared, int CrewCount, bool InnerDoorClosed, int AirlockCrewCount,
    bool CabinDoorClosed = true, int EntryCrewCount = 0, bool ShoreConnected = false,
    SpacecraftRadiation Radiation = default);

// Only the host advances these consumables and accepts station commands. The worker
// owns numeric state only; Unity supplies actual door seals through copied telemetry.
internal sealed partial class ClientSpacecraftSystems
{
    internal const byte CircuitSparesCapacity = 12, LineSparesCapacity = 24, HullPlatesCapacity = 12;
    // Effective free-air volumes for the cabin, A01-A02 corridor and aft entry.
    // These tune circulation times and exclude fittings; they are not mesh measurements.
    private const float CabinVolume = 75, AirlockVolume = 12, EntryVolume = 24;
    private const float NormalPressure = 101, NormalOxygen = .209f, NormalCO2 = .0004f;
    private const float GasReserveCapacity = 12000; // Equivalent kPa m³ at 22 C.
    private const float OuterVentRate = 2.8f, ValveVentRate = .38f;
    private const float EqualizationRate = .8f, EqualizationTolerance = 3;
    private const float SealTimeout = 5, EqualizationTimeout = 60;
    private const float WarpFuel = .035f, WarpBattery = .08f;
    private const float ElectricalInterval = .05f, WarpChargeEnergyKwh = .45f;
    private const float CircuitControlsKw = .15f, AirPumpKw = 5, ScrubberKw = .8f, ThermalKw = 8;
    private const float FuelPumpKw = 2, EngineControlsKw = 8, AirlockActuatorsKw = 2, CabinLightingKw = .6f;
    private const float AirFeedTarget = 250, FuelFeedTarget = 180, CoolantTarget = 160;
    private const float ApuGuardSeconds = .35f;
    private SpacecraftAtmosphere _cabin, _airlock, _entry, _reserve;
    private float _fuel, _battery, _warp, _coolant;
    private float _apuGuardAmount;
    private SpacecraftHullState _hull;
    private float _engine, _power, _lifeSupport, _warpIntegrity;
    private float _cabinLeak, _airlockLeak, _entryLeak, _coolantLeak;
    private bool _scrubber, _generator, _innerOpen, _cabinOpen, _override, _venting, _apuIsolated, _apuGuardOpen;
    private bool _busIsolated;
    private ushort _valveMask, _lineFaultMask;
    private SpacecraftTravel _travel;
    private SpacecraftRegion _destination;
    private SpacecraftAirlockPhase _airlockCycle;
    private SpacecraftAirlockTarget _airlockTarget;
    private SpacecraftAirlockFault _airlockFault;
    private float _airlockPhaseSeconds;
    private byte _closedCircuits, _trippedCircuits;
    private byte _circuitSpares, _lineSpares, _hullPlates;
    private SpacecraftElectricalState _electrical;
    private SpacecraftTelemetry _flight;

    internal ClientSpacecraftSystems() => Reset();
    internal bool CanThrust => PropulsionAvailable(State);
    internal static bool PropulsionAvailable(in SpacecraftSystemState state) => state.Ready
        && state.HullIntegrity01 > .05f && state.EngineIntegrity01 > .15f && state.PowerIntegrity01 > .08f
        && state.Fuel01 > .00001f && SpacecraftElectrical.IsPowered(state.Electrical, SpacecraftCircuit.Propulsion)
        && (state.ValveMask & ~state.LineFaultMask & (1 << (int)SpacecraftValve.EngineFuel)) != 0
        && state.FuelFeedPressure > 1;
    private float WarpFuelCost => WarpFuel / MathF.Max(.35f, _warpIntegrity);
    private bool AirlockCycleActive => _airlockCycle is SpacecraftAirlockPhase.Sealing or SpacecraftAirlockPhase.Equalizing;
    private float Reserve01 => Unit((float)(_reserve.Moles / SpacecraftAtmosphere.ReferenceMoles(GasReserveCapacity)));
    private bool Powered(SpacecraftCircuit circuit) => SpacecraftElectrical.IsPowered(_electrical, circuit);
    private float AirFeedPressure => _lifeSupport > .1f
        && Powered(SpacecraftCircuit.LifeSupport)
        && (ConnectionUsable(SpacecraftValve.CabinSupply) || ConnectionUsable(SpacecraftValve.AirlockSupply))
        ? AirFeedTarget * _power * _lifeSupport * Unit(Reserve01 / .05f) : 0;
    private float FuelFeedPressure
    {
        get
        {
            bool apuFeed = !_apuIsolated && ConnectionUsable(SpacecraftValve.ApuFuel);
            if (_power <= .08f || (!ConnectionUsable(SpacecraftValve.EngineFuel)
                && !apuFeed && !ConnectionUsable(SpacecraftValve.WarpFuel))) return 0;
            // The APU self-primes; engine and warp pumps belong to their own
            // branches. This gauge reports the common manifold pressure.
            float pump = Powered(SpacecraftCircuit.Propulsion) && ConnectionUsable(SpacecraftValve.EngineFuel)
                || Powered(SpacecraftCircuit.Warp) && ConnectionUsable(SpacecraftValve.WarpFuel)
                || (_generator && apuFeed) ? _power : 0;
            return FuelFeedTarget * pump * Unit(_fuel / .02f);
        }
    }
    // Pressure is measured on the circulating side of the coolant loop.
    private float CoolantPressure => _lifeSupport > .1f && ConnectionUsable(SpacecraftValve.CoolantFeed)
        && Powered(SpacecraftCircuit.Thermal)
        && ConnectionUsable(SpacecraftValve.Radiator)
        && (ConnectionUsable(SpacecraftValve.CabinCooling) || ConnectionUsable(SpacecraftValve.AirlockCooling))
        ? CoolantTarget * _power * _lifeSupport * Unit(_coolant / .2f) : 0;
    internal SpacecraftSystemState State => new(_cabin.Pressure, _cabin.OxygenFraction, _cabin.Temperature,
        Reserve01, _fuel, _battery, _scrubber, _generator, _innerOpen, _override,
        _cabin.CO2Fraction, _airlock.Pressure, _warp, true, _venting,
        _airlock.OxygenFraction, _airlock.CO2Fraction, _airlock.Temperature, _destination,
        _hull, _engine, _power, _lifeSupport, _warpIntegrity,
        CabinDoorOpen: _cabinOpen, ValveMask: _valveMask, Coolant01: _coolant, ApuIsolated: _apuIsolated,
        AirFeedPressure: AirFeedPressure, FuelFeedPressure: FuelFeedPressure, CoolantPressure: CoolantPressure,
        EntryPressure: _entry.Pressure, EntryOxygenFraction: _entry.OxygenFraction,
        EntryCO2Fraction: _entry.CO2Fraction, EntryTemperatureC: _entry.Temperature,
        AirlockCycle: _airlockCycle, AirlockTarget: _airlockTarget, AirlockFault: _airlockFault,
        Electrical: _electrical, LineFaultMask: _lineFaultMask,
        ApuGuardOpen: _apuGuardOpen, ApuGuardAmount: _apuGuardAmount,
        CircuitSpares: _circuitSpares, LineSpares: _lineSpares, ShoreCoupling01: _shoreCoupling01,
        ShoreAirEnabled: _shoreAirEnabled, ShoreAirStatus: _shoreAirStatus,
        ShoreAirMolesPerSecond: _shoreAirMolesPerSecond, ShoreAirWatts: _shoreAirWatts,
        ShoreFuelEnabled: _shoreFuelEnabled, ShoreFuelStatus: _shoreFuelStatus,
        ShoreCoolantEnabled: _shoreCoolantEnabled, ShoreCoolantStatus: _shoreCoolantStatus,
        CoolantLeak: _coolantLeak > 0, Sealant: SealantState, BusIsolated: _busIsolated, HullPlates: _hullPlates,
        WarpDischargeEnabled: _warpDischargeEnabled, Thermal: ThermalState, Fire: FireState);

    internal void Reset()
    {
        _cabin = new SpacecraftAtmosphere(CabinVolume, NormalPressure, 22);
        _airlock = new SpacecraftAtmosphere(AirlockVolume, NormalPressure, 22);
        _entry = new SpacecraftAtmosphere(EntryVolume, NormalPressure, 22);
        _reserve = new SpacecraftAtmosphere(1, GasReserveCapacity, 22);
        _fuel = _battery = _coolant = 1;
        ResetThermal();
        ResetFire();
        _circuitSpares = CircuitSparesCapacity;
        _lineSpares = LineSparesCapacity;
        _hullPlates = 0;
        _warp = 0;
        _warpDischargeEnabled = false;
        _apuGuardAmount = 0;
        _valveMask = 0x7ff;
        _scrubber = true;
        _generator = _innerOpen = _override = _venting = _apuIsolated = _apuGuardOpen = false;
        _busIsolated = false;
        _cabinOpen = true;
        _travel = SpacecraftTravel.None;
        _destination = SpacecraftRegion.TestWorld;
        _airlockCycle = SpacecraftAirlockPhase.Idle;
        _airlockTarget = SpacecraftAirlockTarget.None;
        _airlockFault = SpacecraftAirlockFault.None;
        _airlockPhaseSeconds = 0;
        _closedCircuits = SpacecraftElectrical.AllMask;
        _flight = default;
        ResetElectricalReadings();
        Repair();
        RefreshElectrical(_flight);
    }

    internal SpacecraftSystemState Step(float dt, SpacecraftTelemetry flight)
    {
        if (!float.IsFinite(dt) || dt <= 0) return State;
        ClearElectricalInterval();
        bool shoreConnected = flight.ShoreConnected && flight.Region == SpacecraftRegion.Space
            && flight.Travel == SpacecraftTravel.None;
        if (!shoreConnected) _shoreCoupling01 = 0;
        BeginShoreAirInterval(shoreConnected);
        BeginShoreFuelInterval(shoreConnected, flight);
        BeginShoreCoolantInterval(shoreConnected, flight);
        // The entire completed interval must begin with physical contact.
        _shoreAvailable = !_busIsolated && shoreConnected && _travel == SpacecraftTravel.None && _shoreCoupling01 >= 1;
        _shoreLimitKw = SpacecraftElectrical.BatteryChargeKw;
        _flight = flight;
        if (flight.Travel != _travel && flight.Travel is SpacecraftTravel.Destination or SpacecraftTravel.Moon or SpacecraftTravel.Return)
        {
            _fuel = MathF.Max(0, _fuel - WarpFuelCost);
            _battery = MathF.Max(0, _battery - WarpBattery);
            _warp = 0;
        }
        _travel = flight.Travel;
        var remaining = MathF.Min(dt, .5f);
        int crew = Math.Clamp(flight.CrewCount, 0, 64);
        int airlockCrew = Math.Clamp(flight.AirlockCrewCount, 0, crew);
        int entryCrew = Math.Clamp(flight.EntryCrewCount, 0, crew - airlockCrew);
        while (remaining > 0)
        {
            var step = MathF.Min(remaining, ElectricalInterval);
            Advance(step, flight, crew - airlockCrew - entryCrew, airlockCrew, entryCrew);
            remaining -= step;
        }
        if (!_shoreAvailable) SettleElectricalInterval();
        RefreshElectrical(flight);
        return State;
    }

    private void Advance(float dt, SpacecraftTelemetry flight, int cabinCrew, int airlockCrew, int entryCrew)
    {
        if (flight.ShoreConnected && flight.Region == SpacecraftRegion.Space && flight.Travel == SpacecraftTravel.None)
            _shoreCoupling01 = MathF.Min(1, _shoreCoupling01 + dt / ShoreCouplingSeconds);
        _apuGuardAmount = MoveTowards(_apuGuardAmount, _apuGuardOpen ? 1 : 0, dt / ApuGuardSeconds);
        float outsidePressure = Breathable(flight.Region) ? NormalPressure : 0;
        float outsideTemperature = OutsideTemperature(flight.Region);
        float door = float.IsFinite(flight.DoorAmount) ? Unit(flight.DoorAmount) : 0;
        // A queued opening request cannot move gas through an unpowered sealed door.
        bool cabinConnected = !flight.CabinDoorClosed;
        bool entryConnected = !flight.InnerDoorClosed;
        double exchange = 1 - Math.Exp(-4 * dt);
        if (cabinConnected) SpacecraftAtmosphere.Equalize(ref _cabin, ref _airlock, exchange);
        if (entryConnected) SpacecraftAtmosphere.Equalize(ref _airlock, ref _entry, exchange);
        if (door > 0) Vent(ref _entry, outsidePressure, outsideTemperature, OuterVentRate * door, dt);
        bool ventConnected = ConnectionUsable(SpacecraftValve.AirlockVent);
        if (ventConnected) Vent(ref _airlock, outsidePressure, outsideTemperature, ValveVentRate, dt);
        if (_cabinLeak > 0) Vent(ref _cabin, outsidePressure, outsideTemperature, _cabinLeak, dt);
        if (_airlockLeak > 0) Vent(ref _airlock, outsidePressure, outsideTemperature, _airlockLeak, dt);
        if (_entryLeak > 0) Vent(ref _entry, outsidePressure, outsideTemperature, _entryLeak, dt);

        if (ConnectionUsable(SpacecraftValve.EngineFuel))
            _fuel = MathF.Max(0, _fuel - (1 - _engine) * .00008f * dt);
        // An intact loop recirculates its charge. Closing the feed isolates
        // the reservoir from damaged plumbing; service replaces losses.
        if (ConnectionUsable(SpacecraftValve.CoolantFeed))
            _coolant = MathF.Max(0, _coolant - _coolantLeak * dt);

        float warpCharge = WarpCharge(dt, flight);
        _electrical = SolveElectrical(dt, flight, warpCharge);
        RecordElectricalInterval(dt);
        _fuel = MathF.Max(0, _fuel - _electrical.GenerationKw * dt
            / (SpacecraftElectrical.FuelElectricalCapacityKwh * 3600));
        _battery = Unit(_battery - _electrical.BatteryKw * dt
            / (SpacecraftElectrical.BatteryCapacityKwh * 3600));
        AdvanceSealant(dt, flight);
        float effort = flight.Powered && CanThrust ? MathF.Min(Effort(flight), _fuel / (.0004f * dt)) : 0;
        _fuel = MathF.Max(0, _fuel - effort * .0004f * dt);
        double storedWarpJoules = 0;
        if (Powered(SpacecraftCircuit.Warp) && warpCharge > 0)
        {
            float charge = MathF.Min(warpCharge, _fuel / .004f);
            double target = Math.Min(1, (double)_warp + charge);
            float next = (float)target;
            if (next > target) next = MathF.BitDecrement(next);
            double credited = next - (double)_warp;
            _warp = next;
            storedWarpJoules = credited * WarpChargeEnergyKwh * 3_600_000d;
            _fuel = MathF.Max(0, _fuel - (float)(credited * .004));
        }
        if (_warpIntegrity <= .35f) _warp = MathF.Max(0, _warp - dt * .05f);
        AdvanceWarpDischarge(dt, flight);

        Breathe(ref _cabin, cabinCrew, dt);
        Breathe(ref _airlock, airlockCrew, dt);
        Breathe(ref _entry, entryCrew, dt);
        float support = _lifeSupport > .1f ? _lifeSupport : 0;
        if (Powered(SpacecraftCircuit.LifeSupport) && support > 0 && _scrubber)
        {
            bool cabinScrubber = ConnectionUsable(SpacecraftValve.CabinScrubber);
            bool airlockScrubber = ConnectionUsable(SpacecraftValve.AirlockScrubber);
            if (cabinScrubber) Scrub(ref _cabin, .016f * support * dt);
            if (airlockScrubber)
            {
                Scrub(ref _airlock, .01f * support * dt);
                Scrub(ref _entry, .01f * support * dt);
            }
        }
        if (Powered(SpacecraftCircuit.LifeSupport) && support > 0 && _reserve.Moles > 0)
        {
            // Stop pumping an open vacuum path; an emergency override does not
            // make the finite supply continuously empty itself through a hatch.
            bool outerSealed = door <= .01f && !flight.DoorOpen;
            bool chamberVenting = ventConnected || entryConnected && !outerSealed;
            bool cabinVenting = cabinConnected && chamberVenting;
            bool entryVenting = !outerSealed || entryConnected && ventConnected;
            if (ConnectionUsable(SpacecraftValve.CabinSupply) && (!cabinVenting || Breathable(flight.Region)))
                Supply(ref _cabin, .65f * (AirFeedPressure / AirFeedTarget) * dt);
            if (ConnectionUsable(SpacecraftValve.AirlockSupply))
            {
                if (!chamberVenting && !AirlockCycleActive) Supply(ref _airlock, 5 * (AirFeedPressure / AirFeedTarget) * dt);
                if (!entryVenting) Supply(ref _entry, 5 * (AirFeedPressure / AirFeedTarget) * dt);
            }
        }
        AdvanceThermal(dt, flight, cabinCrew, airlockCrew, entryCrew, effort, storedWarpJoules);
        AdvanceFire(dt);
        AdvanceAirlockCycle(dt, flight);
    }

    private static float Effort(SpacecraftTelemetry flight)
        => float.IsFinite(flight.Effort) ? Math.Clamp(flight.Effort, 0, 1.8f) : 0;

    private float WarpCharge(float dt, SpacecraftTelemetry flight)
    {
        if (_warpDischargeEnabled || flight.Travel != SpacecraftTravel.None || _power <= .08f || _warpIntegrity <= .35f
            || _warp >= 1 || _fuel <= WarpFuelCost || !ConnectionUsable(SpacecraftValve.WarpFuel)) return 0;
        float feed = _power * Unit(_fuel / .02f);
        return feed * FuelFeedTarget > 1 ? MathF.Min(1 - _warp, dt / 45 * _warpIntegrity * feed) : 0;
    }

    private SpacecraftElectricalState SolveElectrical(float dt, SpacecraftTelemetry flight, float warpCharge)
    {
        if (_busIsolated)
            return new(_closedCircuits, _trippedCircuits, 0, (byte)(_closedCircuits & ~_trippedCircuits), 0, 0, 0);
        float lifeSupport = CircuitControlsKw, thermal = CircuitControlsKw, propulsion = CircuitControlsKw;
        float warp = CircuitControlsKw + warpCharge * WarpChargeEnergyKwh * 3600 / dt;
        if (_lifeSupport > .1f)
        {
            lifeSupport += SealantLoadKw(dt, flight);
            if (_reserve.Moles > 0 && (ConnectionUsable(SpacecraftValve.CabinSupply) || ConnectionUsable(SpacecraftValve.AirlockSupply)))
                lifeSupport += AirPumpKw;
            if (_scrubber && (ConnectionUsable(SpacecraftValve.CabinScrubber) || ConnectionUsable(SpacecraftValve.AirlockScrubber)))
                lifeSupport += ScrubberKw;
            if (_coolant > 0 && ConnectionUsable(SpacecraftValve.CoolantFeed) && ConnectionUsable(SpacecraftValve.Radiator)
                && (ConnectionUsable(SpacecraftValve.CabinCooling) || ConnectionUsable(SpacecraftValve.AirlockCooling)))
                thermal += ThermalKw;
        }
        if (_fuel > 0 && ConnectionUsable(SpacecraftValve.EngineFuel))
            propulsion += FuelPumpKw + (flight.Powered && _hull.Integrity01 > .05f && _engine > .15f
                ? EngineControlsKw * Effort(flight) : 0);
        if (_fuel > 0 && ConnectionUsable(SpacecraftValve.WarpFuel)) warp += FuelPumpKw;
        // Pressurized pumps and energized actuators draw their rated load even
        // while idle. This also covers manual door motion outside this worker.
        var loads = new SpacecraftElectricalLoads(lifeSupport, thermal, propulsion, warp,
            AirlockActuatorsKw, CabinLightingKw);
        bool generator = _generator && !_apuIsolated && ConnectionUsable(SpacecraftValve.ApuFuel);
        return SpacecraftElectrical.Solve(_closedCircuits, _trippedCircuits, _power,
            _battery, _fuel, generator, loads, dt);
    }

    private void RefreshElectrical(SpacecraftTelemetry flight)
    {
        _electrical = SolveElectrical(ElectricalInterval, flight, WarpCharge(ElectricalInterval, flight));
        ApplyElectricalReadings();
        if (AirlockCycleActive && !Powered(SpacecraftCircuit.Airlock))
            StopAirlockCycle(SpacecraftAirlockFault.PowerLost);
    }

    private bool StartAirlockCycle(SpacecraftAirlockTarget target, SpacecraftTelemetry flight, out string feedback)
    {
        if (AirlockCycleActive)
        { feedback = "An airlock cycle is already active. Cancel it before choosing another passage."; return false; }
        if (_override)
        { feedback = "Restore the interlocks before starting an automatic airlock cycle."; return false; }
        if (_venting)
        { feedback = "Close the exterior vent before starting an airlock cycle."; return false; }
        if (flight.Travel != SpacecraftTravel.None)
        { feedback = "Wait for the journey to finish before cycling the airlock."; return false; }
        if (!Powered(SpacecraftCircuit.Airlock))
        { feedback = "Restore the airlock circuit before cycling the airlock."; return false; }
        _airlockTarget = target;
        _airlockCycle = SpacecraftAirlockPhase.Sealing;
        _airlockFault = SpacecraftAirlockFault.None;
        _airlockPhaseSeconds = 0;
        _innerOpen = _cabinOpen = false;
        feedback = target == SpacecraftAirlockTarget.Cabin
            ? "Airlock cycle to cabin started. Keep both doorways clear."
            : "Airlock cycle to entry started. Keep both doorways clear.";
        return true;
    }

    private void AdvanceAirlockCycle(float dt, SpacecraftTelemetry flight)
    {
        if (!AirlockCycleActive) return;
        if (_override) { StopAirlockCycle(SpacecraftAirlockFault.OverrideEnabled); return; }
        if (flight.Travel != SpacecraftTravel.None) { StopAirlockCycle(SpacecraftAirlockFault.TravelStarted); return; }
        if (!Powered(SpacecraftCircuit.Airlock)) { StopAirlockCycle(SpacecraftAirlockFault.PowerLost); return; }
        if (_innerOpen || _cabinOpen || _venting) { StopAirlockCycle(SpacecraftAirlockFault.SealLost); return; }
        _airlockPhaseSeconds += dt;
        bool sealedDoors = flight.InnerDoorClosed && flight.CabinDoorClosed;
        if (_airlockCycle == SpacecraftAirlockPhase.Sealing)
        {
            if (sealedDoors)
            {
                _airlockCycle = SpacecraftAirlockPhase.Equalizing;
                _airlockPhaseSeconds = 0;
            }
            else if (_airlockPhaseSeconds >= SealTimeout) StopAirlockCycle(SpacecraftAirlockFault.SealTimeout);
            return;
        }
        if (!sealedDoors) { StopAirlockCycle(SpacecraftAirlockFault.SealLost); return; }
        // The selected door's bypass is open only in Equalizing. Stop/fault/cancel
        // therefore closes it without changing manual valves or occupied doorways.
        ref var target = ref (_airlockTarget == SpacecraftAirlockTarget.Cabin ? ref _cabin : ref _entry);
        SpacecraftAtmosphere.Equalize(ref _airlock, ref target, 1 - Math.Exp(-EqualizationRate * dt));
        // Check after ordinary supply, leaks and heat, so a target changing pressure
        // cannot satisfy a stale comparison before the passage request is issued.
        if (MathF.Abs(_airlock.Pressure - target.Pressure) <= EqualizationTolerance)
        {
            _airlockCycle = SpacecraftAirlockPhase.PassageRequested;
            if (_airlockTarget == SpacecraftAirlockTarget.Cabin) _cabinOpen = true;
            else _innerOpen = true;
        }
        else if (_airlockPhaseSeconds >= EqualizationTimeout)
            StopAirlockCycle(SpacecraftAirlockFault.PressureTimeout);
    }

    private void StopAirlockCycle(SpacecraftAirlockFault reason)
    {
        _airlockCycle = SpacecraftAirlockPhase.Fault;
        _airlockFault = reason;
        _airlockPhaseSeconds = 0;
    }

    private static void Vent(ref SpacecraftAtmosphere air, float outsidePressure, float outsideTemperature, float rate, float dt)
        => air.Vent(outsidePressure, outsideTemperature, 1 - Math.Exp(-rate * dt));

    // Instantaneous kPa/s from the same vent model, not a render-frame derivative
    // of intermittently published pressure snapshots.
    internal static float Outflow(SpacecraftFlightState state)
    {
        if (!state.Systems.Ready || !float.IsFinite(state.Systems.EntryPressure)
            || !float.IsFinite(state.DoorAmount)) return 0;
        var outside = Breathable(state.Region) ? NormalPressure : 0;
        var difference = Math.Clamp(state.Systems.EntryPressure - outside, 0, NormalPressure);
        return difference * OuterVentRate * Unit(state.DoorAmount);
    }

    private static void Scrub(ref SpacecraftAtmosphere air, float amount)
        => air.Scrub(SpacecraftAtmosphere.ReferenceMoles(amount * air.Volume), NormalCO2);

    private static void Breathe(ref SpacecraftAtmosphere air, int crew, float dt)
        => air.Breathe(SpacecraftAtmosphere.RestingOxygenMolesPerSecond * crew * dt);

    private void Supply(ref SpacecraftAtmosphere air, float amount)
    {
        double deficit = air.MolesToPressure(NormalPressure, NormalOxygen, NormalCO2, _reserve.TemperatureKelvin);
        if (deficit <= 0) return;
        double targetOxygen = NormalPressure * NormalOxygen * air.Volume
            / (SpacecraftAtmosphere.GasConstant * air.TemperatureKelvin);
        double oxygen = Math.Clamp((targetOxygen - air.OxygenMoles) / deficit, 0, 1);
        double co2 = Math.Min(NormalCO2, 1 - oxygen);
        double supplied = Math.Min(air.MolesToPressure(Math.Min(NormalPressure, AirFeedPressure),
            oxygen, co2, _reserve.TemperatureKelvin), SpacecraftAtmosphere.ReferenceMoles(amount * air.Volume));
        if (supplied <= 0) return;
        // The reserve holds separately metered species. Depleted oxygen cannot be
        // manufactured from inert gas, and each drawn packet carries its own heat.
        _reserve.SupplyTo(ref air, supplied, oxygen, co2);
    }

    internal bool CanOpenOuter(SpacecraftTelemetry flight, out string reason)
        => CanOpenOuter(State, flight.Region, flight.InnerDoorClosed, flight.CabinDoorClosed, out reason);

    internal static bool CanOpenOuter(SpacecraftSystemState state, SpacecraftRegion region,
        bool innerDoorClosed, bool cabinDoorClosed, out string reason)
    {
        reason = "Outer door is ready.";
        if (!state.Ready) { reason = "Ship computer is unavailable."; return false; }
        if (state.EmergencyOverride) return true;
        if (!Breathable(region) && (state.InnerDoorOpen || !innerDoorClosed)
            && (state.CabinDoorOpen || !cabinDoorClosed))
        { reason = "Seal a cabin boundary before opening the rear ramp to vacuum."; return false; }
        float outside = Breathable(region) ? NormalPressure : 0;
        if (!(MathF.Abs(state.EntryPressure - outside) <= 12))
        { reason = "Equalize the entry with the exterior through the airlock vent first, or deliberately override the interlock."; return false; }
        return true;
    }

    internal static bool CanOpenInnerDoor(SpacecraftSystemState state, bool cabinDoorClosed, out string reason)
    {
        reason = "Entry hatch is ready.";
        if (!state.Ready) { reason = "Ship computer is unavailable."; return false; }
        if (state.EmergencyOverride) return true;
        if (state.CabinDoorOpen || !cabinDoorClosed)
        { reason = "Close the cabin door and wait for its seal before opening the entry hatch."; return false; }
        if (!(MathF.Abs(state.EntryPressure - state.AirlockPressure) <= 12))
        { reason = "Equalize the airlock and entry pressure before opening the entry hatch."; return false; }
        return true;
    }

    internal static bool CanOpenCabinDoor(SpacecraftSystemState state, bool innerDoorClosed, out string reason)
    {
        reason = "Cabin door is ready.";
        if (!state.Ready) { reason = "Ship computer is unavailable."; return false; }
        if (state.EmergencyOverride) return true;
        if (state.InnerDoorOpen || !innerDoorClosed || state.AirlockVenting)
        { reason = "Close the entry hatch, wait for its seal and stop the vent before opening the cabin door."; return false; }
        if (!(MathF.Abs(state.CabinPressure - state.AirlockPressure) <= 12))
        { reason = "Equalize the cabin and airlock pressure before opening the cabin door."; return false; }
        return true;
    }

    // Host-only cancellation of a closing leaf that has not sealed. An occupied
    // doorway must be released even if normal opening interlocks have since changed.
    internal void ReopenObstructedDoor(bool cabin)
    {
        if (AirlockCycleActive) StopAirlockCycle(SpacecraftAirlockFault.Obstructed);
        if (cabin) _cabinOpen = true;
        else _innerOpen = true;
    }

    internal bool CanWarp(out string reason, bool starting = true) => CanWarp(State, out reason, starting);

    internal static bool CanWarp(SpacecraftSystemState state, out string reason, bool starting = true)
    {
        reason = "Warp drive is ready.";
        if (!state.Ready) { reason = "Computer telemetry is unavailable."; return false; }
        if (state.WarpDischargeEnabled)
        { reason = "Stop warp discharge before a jump."; return false; }
        if (!(state.Hull.MinimumIntegrity01 > .3f)) { reason = "Hull section damage prevents a jump; repair the damaged compartment."; return false; }
        if (!(state.PowerIntegrity01 > .08f)) { reason = "The power bus has failed; repair the ship."; return false; }
        if (!(state.WarpIntegrity01 > .35f)) { reason = "The warp drive is damaged; repair the ship."; return false; }
        if (!SpacecraftElectrical.IsPowered(state.Electrical, SpacecraftCircuit.Warp))
        { reason = "Restore power to the warp circuit before a jump."; return false; }
        if ((state.ValveMask & (1 << (int)SpacecraftValve.WarpFuel)) == 0)
        { reason = "Open the warp fuel valve before a jump."; return false; }
        if ((state.LineFaultMask & (1 << (int)SpacecraftValve.WarpFuel)) != 0)
        { reason = "Repair the warp fuel connection before a jump."; return false; }
        if (!(state.FuelFeedPressure is > 1 and <= FuelFeedTarget))
        { reason = "Restore fuel feed pressure before a jump."; return false; }
        // The countdown has already consumed the charge and jump costs.
        if (!starting) return true;
        if (!(state.WarpCharge01 >= .999f)) { reason = "Warp drive is still charging."; return false; }
        if (!(state.Fuel01 >= WarpFuel / MathF.Max(.35f, state.WarpIntegrity01)))
        { reason = "Insufficient fuel for the jump."; return false; }
        if (!(state.Battery01 >= WarpBattery + .02f)) { reason = "Insufficient battery reserve for the jump."; return false; }
        return true;
    }

    internal static bool TryReadValveCommand(string command, out int index, out bool open)
    {
        index = -1;
        open = false;
        if (command == null || command.Length is < 12 or > 14
            || !command.StartsWith("valve-", StringComparison.Ordinal)) return false;
        var value = command.AsSpan(6);
        if (value[0] is < '0' or > '9') return false;
        int number = value[0] - '0';
        int digits = 1;
        if (value[0] == '1' && value[1] is '0' or '1')
        {
            number = 10 + value[1] - '0';
            digits = 2;
        }
        bool opening = value[digits..].SequenceEqual("-open");
        if (!opening && !value[digits..].SequenceEqual("-close")) return false;
        index = number;
        open = opening;
        return true;
    }

    private bool ValveOpen(SpacecraftValve valve) => (_valveMask & (1 << (int)valve)) != 0;

    // Protective couplings fail closed; the selected valve position remains visible.
    private bool ConnectionUsable(SpacecraftValve valve)
        => ValveOpen(valve) && (_lineFaultMask & (1 << (int)valve)) == 0;

    internal static bool TryReadCircuitCommand(string command, out int index, out bool open)
    {
        index = -1;
        open = false;
        if (command == null || command.Length is < 14 or > 15
            || !command.StartsWith("circuit-", StringComparison.Ordinal)
            || command[8] is < '0' or > '5') return false;
        bool opening = command.AsSpan(9).SequenceEqual("-open");
        if (!opening && !command.AsSpan(9).SequenceEqual("-close")) return false;
        index = command[8] - '0';
        open = opening;
        return true;
    }

    internal static bool TryReadCircuitRepairCommand(string command, out int index)
        => TryReadRepairCommand(command, "circuit-", 5, out index);

    internal static bool TryReadLineRepairCommand(string command, out int index)
        => TryReadRepairCommand(command, "line-", 11, out index);

    private static bool TryReadRepairCommand(string command, string prefix, int maximum, out int index)
    {
        index = -1;
        if (command == null || !command.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var value = command.AsSpan(prefix.Length);
        if (value.Length is < 8 or > 9 || value[0] is < '0' or > '9') return false;
        int number = value[0] - '0';
        int digits = 1;
        if (value.Length == 9)
        {
            if (value[0] != '1' || value[1] is < '0' or > '1') return false;
            number = 10 + value[1] - '0';
            digits = 2;
        }
        if (number > maximum || !value[digits..].SequenceEqual("-repair")) return false;
        index = number;
        return true;
    }

    private static bool CanRepairCircuit(SpacecraftCircuit circuit, in SpacecraftSystemState state,
        SpacecraftTravel travel, out string feedback)
    {
        if (travel != SpacecraftTravel.None)
        { feedback = "Wait for the journey to finish before repairing a circuit."; return false; }
        int bit = 1 << (int)circuit;
        string name = SpacecraftElectrical.CircuitName(circuit);
        if ((state.Electrical.TrippedMask & bit) == 0)
        { feedback = $"{name} circuit is already healthy."; return false; }
        if ((state.Electrical.ClosedMask & bit) != 0)
        { feedback = $"Isolate the {name} circuit before repairing it."; return false; }
        if (state.CircuitSpares == 0)
        { feedback = "No circuit spares remain aboard; repair requires one circuit spare."; return false; }
        feedback = string.Empty;
        return true;
    }

    private bool RepairCircuit(SpacecraftCircuit circuit, SpacecraftTelemetry flight, out string feedback)
    {
        var state = State;
        if (!CanRepairCircuit(circuit, state, flight.Travel, out feedback)) return false;
        int bit = 1 << (int)circuit;
        string name = SpacecraftElectrical.CircuitName(circuit);
        _circuitSpares--;
        _trippedCircuits = (byte)(_trippedCircuits & ~bit);
        feedback = $"{name} circuit repaired; breaker left open. Circuit spares: {_circuitSpares}.";
        return true;
    }

    internal static bool LineNeedsRepair(in SpacecraftSystemState state, SpacecraftValve valve)
        => (state.LineFaultMask & (1 << (int)valve)) != 0
            || valve == SpacecraftValve.CoolantFeed && state.CoolantLeak;

    internal static bool CoolantRepairIsolated(in SpacecraftSystemState state, bool enginePowered)
        => CoolantRepairIsolated(state, enginePowered, out _);

    internal static bool CoolantRepairIsolated(in SpacecraftSystemState state, bool enginePowered,
        out string feedback)
    {
        if ((state.ValveMask & (1 << (int)SpacecraftValve.CoolantFeed)) != 0)
        { feedback = "Close the coolant feed valve before repairing its seal."; return false; }
        if ((state.ValveMask & (1 << (int)SpacecraftValve.Radiator)) != 0)
        { feedback = "Close the radiator valve before repairing the coolant seal."; return false; }
        if (enginePowered)
        { feedback = "Turn the engine off before repairing the coolant seal."; return false; }
        if (state.GeneratorEnabled)
        { feedback = "Turn the generator off before repairing the coolant seal."; return false; }
        if ((state.Electrical.ClosedMask & (1 << (int)SpacecraftCircuit.Thermal)) != 0)
        { feedback = "Open the thermal breaker before repairing the coolant seal."; return false; }
        if ((state.Electrical.ClosedMask & (1 << (int)SpacecraftCircuit.Warp)) != 0)
        { feedback = "Open the warp breaker before repairing the coolant seal."; return false; }
        if (state.ShoreCoolantEnabled)
        { feedback = "Turn shore coolant off before repairing the coolant seal."; return false; }
        feedback = string.Empty;
        return true;
    }

    private static bool CanRepairLine(SpacecraftValve valve, in SpacecraftSystemState state,
        bool enginePowered, SpacecraftTravel travel, out string feedback)
    {
        if (travel != SpacecraftTravel.None)
        { feedback = "Wait for the journey to finish before repairing a connection."; return false; }
        int bit = 1 << (int)valve;
        if (!LineNeedsRepair(state, valve))
        { feedback = $"{valve} connection is already healthy."; return false; }
        if (valve == SpacecraftValve.CoolantFeed)
        {
            if (!CoolantRepairIsolated(state, enginePowered, out feedback)) return false;
        }
        else if ((state.ValveMask & bit) != 0)
        { feedback = $"Close the {valve} valve to isolate its connection before repairing it."; return false; }
        if (state.LineSpares == 0)
        { feedback = "No line spares remain aboard; repair requires one line spare."; return false; }
        feedback = string.Empty;
        return true;
    }

    private bool RepairLine(SpacecraftValve valve, SpacecraftTelemetry flight, out string feedback)
    {
        var state = State;
        if (!CanRepairLine(valve, state, flight.Powered, flight.Travel, out feedback)) return false;
        int bit = 1 << (int)valve;
        _lineSpares--;
        _lineFaultMask = (ushort)(_lineFaultMask & ~bit);
        if (valve == SpacecraftValve.CoolantFeed)
        {
            _coolantLeak = 0;
            feedback = $"Coolant seal repaired; feed and radiator valves left closed. Line spares: {_lineSpares}.";
        }
        else feedback = $"{valve} connection repaired; valve left closed. Line spares: {_lineSpares}.";
        return true;
    }

    private bool SetCircuit(SpacecraftCircuit circuit, bool open, out string feedback)
    {
        int bit = 1 << (int)circuit;
        string name = SpacecraftElectrical.CircuitName(circuit);
        if (!open && circuit == SpacecraftCircuit.Warp && _warpDischargeEnabled)
        { feedback = "Stop warp discharge before closing the warp breaker."; return false; }
        if (!open && (_trippedCircuits & bit) != 0)
        { feedback = $"{name} circuit is damaged; repair it before closing the breaker."; return false; }
        _closedCircuits = (byte)(open ? _closedCircuits & ~bit : _closedCircuits | bit);
        feedback = open ? $"{name} circuit isolated." : $"{name} breaker closed; available power determines supply.";
        return true;
    }

    private bool SetValve(SpacecraftValve valve, bool open, SpacecraftTelemetry flight, out string feedback)
    {
        if (valve == SpacecraftValve.WarpFuel && open && _warpDischargeEnabled)
        { feedback = "Stop warp discharge before opening the warp fuel valve."; return false; }
        if (valve == SpacecraftValve.AirlockVent && AirlockCycleActive)
        { feedback = "Cancel the airlock cycle before changing the exterior vent."; return false; }
        if (valve == SpacecraftValve.AirlockVent && open && !_venting
            && !_override && (_cabinOpen || !flight.CabinDoorClosed))
        { feedback = "Seal the cabin door before venting the airlock."; return false; }
        int bit = 1 << (int)valve;
        _valveMask = (ushort)(open ? _valveMask | bit : _valveMask & ~bit);
        if (valve == SpacecraftValve.AirlockVent)
        {
            _venting = open;
            feedback = open && !ConnectionUsable(valve) ? "Airlock vent selected open; its damaged connection blocks flow."
                : open ? "Airlock vent open; pressure is equalizing with the exterior."
                : "Airlock vent closed; powered life support can refill it.";
        }
        else feedback = open && !ConnectionUsable(valve)
            ? $"{valve} valve selected open; its damaged connection blocks flow."
            : $"{valve} valve {(open ? "open" : "closed")}.";
        return true;
    }

    private static bool CanService(SpacecraftTelemetry flight) => flight.Region == SpacecraftRegion.Island
        && flight.Travel == SpacecraftTravel.None && flight.Grounded && flight.DoorOpen
        && float.IsFinite(flight.DoorAmount) && flight.DoorAmount is >= .95f and <= 1
        && float.IsFinite(flight.SpeedSquared) && flight.SpeedSquared is >= 0 and <= .25f
        && float.IsFinite(flight.AngularSpeedSquared) && flight.AngularSpeedSquared is >= 0 and <= .04f;

    internal bool TryCommand(string command, SpacecraftTelemetry flight, out string feedback)
    {
        // Component service must not settle electrical consumption or alter other system state.
        if (command == "service-engine") return ServiceEngine(flight, out feedback);
        if (command == "service-warp") return ServiceWarp(flight, out feedback);
        if (command == "service-power") return ServicePower(flight, out feedback);
        if (command == "service-life") return ServiceLife(flight, out feedback);
        if (TryReadHullRepairCommand(command, out var hullSection)) return ServiceHull(hullSection, flight, out feedback);
        if (command == "bus-isolate" || command == "bus-connect")
            return SetBusIsolated(command == "bus-isolate", flight, out feedback);
        _flight = flight;
        bool accepted = ApplyCommand(command, flight, out feedback);
        if (accepted)
        {
            CancelShoreCharge();
            CheckShoreFuelSafety(flight);
            CheckShoreCoolantSafety(flight);
            RefreshElectrical(flight);
        }
        return accepted;
    }

    private bool ApplyCommand(string command, SpacecraftTelemetry flight, out string feedback)
    {
        if (AirlockCycleActive && (command == "inner-door" || command == "cabin-door"))
        { feedback = "Cancel the airlock cycle before operating a pressure door manually."; return false; }
        if (TryReadValveCommand(command, out int valve, out bool open))
            return SetValve((SpacecraftValve)valve, open, flight, out feedback);
        if (TryReadCircuitCommand(command, out int circuit, out open))
            return SetCircuit((SpacecraftCircuit)circuit, open, out feedback);
        if (TryReadCircuitRepairCommand(command, out circuit))
            return RepairCircuit((SpacecraftCircuit)circuit, flight, out feedback);
        if (TryReadLineRepairCommand(command, out valve))
            return RepairLine((SpacecraftValve)valve, flight, out feedback);
        switch (command)
        {
            case "warp-discharge-start":
                return SetWarpDischarge(true, flight, out feedback);
            case "warp-discharge-stop":
                return SetWarpDischarge(false, flight, out feedback);
            case "seal-cabin":
                return StartSealant(SpacecraftCompartment.Cabin, flight, out feedback);
            case "seal-airlock":
                return StartSealant(SpacecraftCompartment.Airlock, flight, out feedback);
            case "seal-entry":
                return StartSealant(SpacecraftCompartment.Entry, flight, out feedback);
            case "shore-air":
                return ToggleShoreAir(flight, out feedback);
            case "shore-air-start":
                return SetShoreAir(true, flight, out feedback);
            case "shore-air-stop":
                return SetShoreAir(false, flight, out feedback);
            case "shore-fuel":
                return SetShoreFuel(!_shoreFuelEnabled, flight, out feedback);
            case "shore-fuel-start":
                return SetShoreFuel(true, flight, out feedback);
            case "shore-fuel-stop":
                return SetShoreFuel(false, flight, out feedback);
            case "shore-coolant":
                return SetShoreCoolant(!_shoreCoolantEnabled, flight, out feedback);
            case "shore-coolant-start":
                return SetShoreCoolant(true, flight, out feedback);
            case "shore-coolant-stop":
                return SetShoreCoolant(false, flight, out feedback);
            case "airlock-to-cabin":
                return StartAirlockCycle(SpacecraftAirlockTarget.Cabin, flight, out feedback);
            case "airlock-to-entry":
                return StartAirlockCycle(SpacecraftAirlockTarget.Entry, flight, out feedback);
            case "airlock-cancel":
                if (!AirlockCycleActive)
                { feedback = "No airlock cycle is active."; return false; }
                StopAirlockCycle(SpacecraftAirlockFault.Cancelled);
                feedback = "Airlock cycle cancelled; the equalization bypass is closed.";
                return true;
            case "inner-door":
                if (!_innerOpen && !CanOpenInnerDoor(State, flight.CabinDoorClosed, out feedback)) return false;
                _innerOpen = !_innerOpen;
                feedback = DoorFeedback("Entry hatch", _innerOpen);
                return true;
            case "cabin-door":
                if (!_cabinOpen && !CanOpenCabinDoor(State, flight.InnerDoorClosed, out feedback)) return false;
                _cabinOpen = !_cabinOpen;
                feedback = DoorFeedback("Cabin door", _cabinOpen);
                return true;
            case "override":
                if (AirlockCycleActive) StopAirlockCycle(SpacecraftAirlockFault.OverrideEnabled);
                _override = !_override;
                feedback = _override ? "Emergency override enabled. Interlocked doors can open; cabin pressure can be lost."
                    : "Airlock interlock restored. Existing open doors must still be closed manually.";
                return true;
            case "scrubber":
                _scrubber = !_scrubber;
                feedback = _scrubber ? "CO2 scrubbers enabled; electrical power is required." : "CO2 scrubbers disabled.";
                return true;
            case "generator":
                if (!_generator && _busIsolated)
                { feedback = "Connect the main bus before starting the generator."; return false; }
                if (!_generator && _apuIsolated)
                { feedback = "APU is isolated; reset it during island servicing before restarting the generator."; return false; }
                _generator = !_generator;
                feedback = _generator ? "Generator enabled; an open APU fuel valve and intact power bus are required to supply the circuits." : "Generator disabled.";
                return true;
            case "apu-guard-open":
            case "apu-guard-close":
                bool openGuard = command == "apu-guard-open";
                if (_apuGuardOpen == openGuard)
                { feedback = openGuard ? "APU guard is already opening or open." : "APU guard is already closing or closed."; return false; }
                _apuGuardOpen = openGuard;
                feedback = openGuard ? "APU guard opening." : "APU guard closing; the isolation lever is locked.";
                return true;
            case "apu-isolate":
                if (!_apuGuardOpen || _apuGuardAmount < 1)
                { feedback = "Fully open the APU guard before operating the isolation lever."; return false; }
                if (_apuIsolated)
                { feedback = "APU is already isolated."; return false; }
                _apuIsolated = true;
                _generator = false;
                feedback = "APU isolated; generator stopped and restart inhibited until an island service reset.";
                return true;
            case "apu-reset":
                if (!_apuGuardOpen || _apuGuardAmount < 1)
                { feedback = "Fully open the APU guard before resetting the isolation lever."; return false; }
                if (!_apuIsolated)
                { feedback = "APU isolation is already reset."; return false; }
                if (!CanService(flight))
                { feedback = "APU reset requires the ship parked on the island with its rear ramp fully open."; return false; }
                _apuIsolated = false;
                feedback = "APU isolation reset; the generator control is available.";
                return true;
            case "vent":
                return SetValve(SpacecraftValve.AirlockVent, !_venting, flight, out feedback);
            case "service-supplies":
                if (!CanService(flight))
                { feedback = "Development servicing requires the ship parked on the island with its rear ramp fully open."; return false; }
                _reserve = new SpacecraftAtmosphere(1, GasReserveCapacity, 22);
                _fuel = _battery = _coolant = 1;
                _circuitSpares = CircuitSparesCapacity;
                _lineSpares = LineSparesCapacity;
                Repair();
                ResetThermal();
                feedback = "Development service complete: systems repaired; gas, fuel, coolant, battery and repair spares restored.";
                return true;
            case "select-island": _destination = SpacecraftRegion.Island; break;
            case "select-world": _destination = SpacecraftRegion.TestWorld; break;
            case "select-moon": _destination = SpacecraftRegion.Moon; break;
            default: feedback = "Unknown spacecraft system command."; return false;
        }
        feedback = _destination switch
        {
            SpacecraftRegion.Island => "Route selected: island.",
            SpacecraftRegion.Moon => "Route selected: moon.",
            _ => "Route selected: forest world."
        };
        return true;
    }

    private string DoorFeedback(string name, bool open)
    {
        if (Powered(SpacecraftCircuit.Airlock)) return $"{name} {(open ? "opening" : "closing")}.";
        return _override ? $"{name} {(open ? "opening" : "closing")} on manual drive."
            : $"{name} {(open ? "open" : "close")} requested; restore the airlock circuit to move it.";
    }

    internal static bool Breathable(SpacecraftRegion region)
        => region is SpacecraftRegion.Island or SpacecraftRegion.TestWorld;

    internal bool ApplyImpact(float energyJoules, float localX, float localY, float localZ)
    {
        if (!float.IsFinite(energyJoules) || energyJoules <= 20000
            || !float.IsFinite(localX) || !float.IsFinite(localY) || !float.IsFinite(localZ)
            || MathF.Abs(localX) > 14 || MathF.Abs(localY) > 10 || MathF.Abs(localZ) > 22) return false;
        // Host collision admission supplies newly added impact energy in joules.
        // 3 MJ removes one section integrity unit; the per-impact cap and compartment
        // equipment zones are gameplay tuning, not structural certification.
        float damage = Math.Clamp((energyJoules - 20000) / 3000000, 0, .65f);
        var compartment = HullCompartment(localZ);
        AddImpactFireHeat(compartment, energyJoules);
        _hull = _hull.WithIntegrity(compartment, MathF.Max(0, _hull.Integrity(compartment) - damage));
        _engine = MathF.Max(0, _engine - damage * (localZ < -6 ? 1.1f : .15f));
        _power = MathF.Max(0, _power - damage * (localX < -.8f ? 1 : .2f));
        _lifeSupport = MathF.Max(0, _lifeSupport - damage * (localX > .8f ? 1 : .2f));
        _warpIntegrity = MathF.Max(0, _warpIntegrity - damage * (localZ is >= -6 and <= 2 ? .9f : .15f));
        _coolantLeak = MathF.Min(.02f, _coolantLeak + damage * (localX > .8f ? .0012f : .0003f));
        if (compartment == SpacecraftCompartment.Entry) _entryLeak = MathF.Min(2, _entryLeak + damage * .32f);
        else if (compartment == SpacecraftCompartment.Airlock) _airlockLeak = MathF.Min(2, _airlockLeak + damage * .24f);
        else _cabinLeak = MathF.Min(2, _cabinLeak + damage * .24f);
        if (damage >= .08f)
        {
            SpacecraftCircuit circuit = localZ < -9.1f ? SpacecraftCircuit.Propulsion
                : localZ < -4.84f ? SpacecraftCircuit.Airlock
                : localX > .8f ? SpacecraftCircuit.LifeSupport
                : localX < -.8f ? SpacecraftCircuit.Thermal
                : localZ <= 2 ? SpacecraftCircuit.Warp : SpacecraftCircuit.Cabin;
            _trippedCircuits |= (byte)(1 << (int)circuit);
            // These coarse equipment zones choose one protective coupling, not a
            // mesh segment or an extra leak. Repeated hits can fault the same line.
            SpacecraftValve line = circuit switch
            {
                SpacecraftCircuit.Propulsion => localX < 0 ? SpacecraftValve.ApuFuel : SpacecraftValve.EngineFuel,
                SpacecraftCircuit.Airlock => localY > 1 ? SpacecraftValve.AirlockVent
                    : localX > .8f ? SpacecraftValve.AirlockSupply
                    : localX < -.8f ? SpacecraftValve.AirlockCooling : SpacecraftValve.AirlockScrubber,
                SpacecraftCircuit.LifeSupport => localY > 1 ? SpacecraftValve.CabinScrubber : SpacecraftValve.CabinSupply,
                SpacecraftCircuit.Thermal => localY > 1 ? SpacecraftValve.Radiator
                    : localY < 0 ? SpacecraftValve.CoolantFeed : SpacecraftValve.CabinCooling,
                SpacecraftCircuit.Warp => SpacecraftValve.WarpFuel,
                _ => SpacecraftValve.CabinSupply
            };
            _lineFaultMask |= (ushort)(1 << (int)line);
        }
        CancelShoreCharge();
        RefreshElectrical(_flight);
        return true;
    }

    private void Repair()
    {
        _hull = new(1, 1, 1);
        _engine = _power = _lifeSupport = _warpIntegrity = 1;
        _cabinLeak = _airlockLeak = _entryLeak = _coolantLeak = 0;
        _cabinSealSeconds = _airlockSealSeconds = _entrySealSeconds = 0;
        _trippedCircuits = 0;
        _lineFaultMask = 0;
    }

    private static float Unit(float value) => Math.Clamp(value, 0, 1);
    private static float MoveTowards(float from, float to, float maximumDelta)
        => MathF.Abs(to - from) <= maximumDelta ? to : from + MathF.CopySign(maximumDelta, to - from);

    private static float OutsideTemperature(SpacecraftRegion region) => region switch
    {
        SpacecraftRegion.Island => 18,
        SpacecraftRegion.TestWorld => 12,
        SpacecraftRegion.Moon => -60,
        _ => -80
    };
}
