// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftSystems
{
    internal const float PowerServiceIntegrityGain = .25f, LifeServiceIntegrityGain = .25f;
    internal const float HullServiceIntegrityGain = .125f;
    private const float HullServiceLeakReduction = .25f;

    internal static bool TryManualRepair(string command, out BoxStationToolKind tool, out float seconds)
    {
        tool = default;
        seconds = 0;
        if (TryReadHullRepairCommand(command, out _))
        {
            tool = BoxStationToolKind.Welder;
            seconds = 8;
            return true;
        }
        if (command == "service-power" || TryReadCircuitRepairCommand(command, out _))
        {
            tool = BoxStationToolKind.Screwdriver;
            seconds = 4;
            return true;
        }
        if (command == "service-life" || command == "service-engine")
        {
            tool = BoxStationToolKind.Wrench;
            seconds = 6;
            return true;
        }
        if (command == "service-warp")
        {
            tool = BoxStationToolKind.Wrench;
            seconds = 8;
            return true;
        }
        if (TryReadLineRepairCommand(command, out _))
        {
            tool = BoxStationToolKind.Wrench;
            seconds = 4;
            return true;
        }
        return false;
    }

    internal static bool CanManualRepair(string command, in SpacecraftSystemState state,
        bool enginePowered, SpacecraftTravel travel, out string feedback)
    {
        if (state.Fire.Active)
        { feedback = "Extinguish the onboard fire before starting repairs."; return false; }
        if (TryReadHullRepairCommand(command, out var compartment))
            return CanServiceHull(compartment, state, enginePowered, travel, out feedback);
        switch (command)
        {
            case "service-power": return CanServicePower(state, enginePowered, travel, out feedback);
            case "service-life": return CanServiceLife(state, enginePowered, travel, out feedback);
            case "service-engine": return CanServiceEngine(state, enginePowered, travel, out feedback);
            case "service-warp": return CanServiceWarp(state, enginePowered, travel, out feedback);
        }
        if (TryReadCircuitRepairCommand(command, out int circuit))
            return CanRepairCircuit((SpacecraftCircuit)circuit, state, travel, out feedback);
        if (TryReadLineRepairCommand(command, out int valve))
            return CanRepairLine((SpacecraftValve)valve, state, enginePowered, travel, out feedback);
        feedback = "Unknown manual repair command.";
        return false;
    }

    internal static bool ManualRepairTargetUnchanged(string command, in SpacecraftSystemState before,
        in SpacecraftSystemState after)
    {
        if (TryReadHullRepairCommand(command, out var compartment))
            return before.Hull.Integrity(compartment) == after.Hull.Integrity(compartment)
                && before.Sealant.Leak(compartment) == after.Sealant.Leak(compartment);
        switch (command)
        {
            case "service-power": return before.PowerIntegrity01 == after.PowerIntegrity01;
            case "service-life": return before.LifeSupportIntegrity01 == after.LifeSupportIntegrity01;
            case "service-engine": return before.EngineIntegrity01 == after.EngineIntegrity01;
            case "service-warp": return before.WarpIntegrity01 == after.WarpIntegrity01;
        }
        if (TryReadCircuitRepairCommand(command, out int circuit))
        {
            int bit = 1 << circuit;
            return (before.Electrical.TrippedMask & bit) == (after.Electrical.TrippedMask & bit)
                && (before.Electrical.ClosedMask & bit) == (after.Electrical.ClosedMask & bit);
        }
        if (TryReadLineRepairCommand(command, out int valve))
        {
            int bit = 1 << valve;
            return (before.LineFaultMask & bit) == (after.LineFaultMask & bit)
                && (valve != (int)SpacecraftValve.CoolantFeed || before.CoolantLeak == after.CoolantLeak);
        }
        return false;
    }

    private bool SetBusIsolated(bool isolated, SpacecraftTelemetry flight, out string feedback)
    {
        if (flight.Travel != SpacecraftTravel.None)
        { feedback = "Wait for the journey to finish before operating the main bus isolator."; return false; }
        if (flight.Powered)
        { feedback = "Turn the engine off before operating the main bus isolator."; return false; }
        if (_generator)
        { feedback = "Turn the generator off before operating the main bus isolator."; return false; }
        if (_busIsolated == isolated)
        { feedback = isolated ? "Main bus is already isolated." : "Main bus is already connected."; return false; }
        _busIsolated = isolated;
        _flight = flight;
        // Completed energy is already paid. Clear unpaid charging and stale readings,
        // retaining the elapsed duration shared by independently powered shore fluids.
        _shoreAvailable = false;
        _shoreLimitKw = 0;
        _loadKj = _generationKj = _batteryKj = _shoreJoules = 0;
        _measuredElectrical = default;
        _hasElectricalReading = false;
        RefreshElectrical(flight);
        feedback = isolated ? "Main bus isolated; battery and generator supply and battery charging are disconnected."
            : "Main bus connected; existing breaker and valve selections retained.";
        return true;
    }

    internal static bool PowerServiceIsolated(in SpacecraftSystemState state, bool enginePowered)
        => PowerServiceIsolated(state, enginePowered, out _);

    internal static bool PowerServiceIsolated(in SpacecraftSystemState state, bool enginePowered,
        out string feedback)
    {
        if (enginePowered)
        { feedback = "Turn the engine off before servicing the power system."; return false; }
        if (state.GeneratorEnabled)
        { feedback = "Turn the generator off before servicing the power system."; return false; }
        if (!state.BusIsolated)
        { feedback = "Isolate the main bus before servicing the power system."; return false; }
        if (state.Electrical.ClosedMask != 0)
        { feedback = "Open all six breakers before servicing the power system."; return false; }
        if ((state.ValveMask & (1 << (int)SpacecraftValve.ApuFuel)) != 0)
        { feedback = "Close the APU fuel valve before servicing the power system."; return false; }
        feedback = string.Empty;
        return true;
    }

    internal static bool LifeServiceIsolated(in SpacecraftSystemState state, bool enginePowered)
        => LifeServiceIsolated(state, enginePowered, out _);

    internal static bool LifeServiceIsolated(in SpacecraftSystemState state, bool enginePowered,
        out string feedback)
    {
        if (enginePowered)
        { feedback = "Turn the engine off before servicing life support."; return false; }
        if (state.GeneratorEnabled)
        { feedback = "Turn the generator off before servicing life support."; return false; }
        if (!state.BusIsolated)
        { feedback = "Isolate the main bus before servicing life support."; return false; }
        const int circuits = (1 << (int)SpacecraftCircuit.LifeSupport) | (1 << (int)SpacecraftCircuit.Thermal);
        if ((state.Electrical.ClosedMask & circuits) != 0)
        { feedback = "Open the life support and thermal breakers before servicing life support."; return false; }
        const int valves = (1 << (int)SpacecraftValve.CabinSupply) | (1 << (int)SpacecraftValve.AirlockSupply)
            | (1 << (int)SpacecraftValve.CabinScrubber) | (1 << (int)SpacecraftValve.AirlockScrubber)
            | (1 << (int)SpacecraftValve.CoolantFeed) | (1 << (int)SpacecraftValve.Radiator)
            | (1 << (int)SpacecraftValve.CabinCooling) | (1 << (int)SpacecraftValve.AirlockCooling);
        if ((state.ValveMask & valves) != 0)
        { feedback = "Close supply, scrubber and cooling valves, coolant feed and radiator."; return false; }
        if (state.ShoreAirEnabled)
        { feedback = "Turn shore air off before servicing life support."; return false; }
        if (state.ShoreCoolantEnabled)
        { feedback = "Turn shore coolant off before servicing life support."; return false; }
        feedback = string.Empty;
        return true;
    }

    private static bool HullServiceIsolated(in SpacecraftSystemState state, bool enginePowered,
        out string feedback)
    {
        if (enginePowered)
        { feedback = "Turn the engine off before welding the hull."; return false; }
        if (state.GeneratorEnabled)
        { feedback = "Turn the generator off before welding the hull."; return false; }
        const int valves = (1 << (int)SpacecraftValve.EngineFuel) | (1 << (int)SpacecraftValve.ApuFuel);
        if ((state.ValveMask & valves) != 0)
        { feedback = "Close the engine and APU fuel valves before welding the hull."; return false; }
        if (state.ShoreFuelEnabled)
        { feedback = "Turn shore fuel off before welding the hull."; return false; }
        feedback = string.Empty;
        return true;
    }

    private static bool CanServiceHull(SpacecraftCompartment compartment, in SpacecraftSystemState state, bool enginePowered,
        SpacecraftTravel travel, out string feedback)
    {
        if (travel != SpacecraftTravel.None)
        { feedback = "Wait for the journey to finish before welding the hull."; return false; }
        if (state.Hull.Integrity(compartment) >= 1 && state.Sealant.Leak(compartment) <= 0)
        { feedback = "This hull section is already sound and sealed."; return false; }
        if (state.Sealant.Remaining(compartment) > 0)
        { feedback = "Wait for this compartment's sealant cycle to finish before welding."; return false; }
        if (!HullServiceIsolated(state, enginePowered, out feedback)) return false;
        if (state.HullPlates == 0)
        { feedback = "Hull welding requires one steel hull plate aboard."; return false; }
        feedback = string.Empty;
        return true;
    }

    private bool ServiceHull(SpacecraftCompartment compartment, SpacecraftTelemetry flight, out string feedback)
    {
        var state = State;
        if (!CanServiceHull(compartment, state, flight.Powered, flight.Travel, out feedback)) return false;
        _hullPlates--;
        _hull = _hull.WithIntegrity(compartment, MathF.Min(1, _hull.Integrity(compartment) + HullServiceIntegrityGain));
        switch (compartment)
        {
            case SpacecraftCompartment.Cabin: _cabinLeak = MathF.Max(0, _cabinLeak - HullServiceLeakReduction); break;
            case SpacecraftCompartment.Airlock: _airlockLeak = MathF.Max(0, _airlockLeak - HullServiceLeakReduction); break;
            case SpacecraftCompartment.Entry: _entryLeak = MathF.Max(0, _entryLeak - HullServiceLeakReduction); break;
        }
        feedback = $"{compartment} hull welded to {_hull.Integrity(compartment):P0}. Plates: {_hullPlates}.";
        return true;
    }

    private static bool CanServicePower(in SpacecraftSystemState state, bool enginePowered,
        SpacecraftTravel travel, out string feedback)
    {
        if (travel != SpacecraftTravel.None)
        { feedback = "Wait for the journey to finish before servicing the power system."; return false; }
        if (state.PowerIntegrity01 >= 1)
        { feedback = "Power system is already healthy."; return false; }
        if (!PowerServiceIsolated(state, enginePowered, out feedback)) return false;
        if (state.CircuitSpares == 0)
        { feedback = "Power service requires one circuit spare aboard."; return false; }
        feedback = string.Empty;
        return true;
    }

    private bool ServicePower(SpacecraftTelemetry flight, out string feedback)
    {
        var state = State;
        if (!CanServicePower(state, flight.Powered, flight.Travel, out feedback)) return false;
        _circuitSpares--;
        _power = MathF.Min(1, _power + PowerServiceIntegrityGain);
        feedback = $"Power system serviced to {_power:P0} integrity. Circuit spares: {_circuitSpares}.";
        return true;
    }

    private static bool CanServiceLife(in SpacecraftSystemState state, bool enginePowered,
        SpacecraftTravel travel, out string feedback)
    {
        if (travel != SpacecraftTravel.None)
        { feedback = "Wait for the journey to finish before servicing life support."; return false; }
        if (state.LifeSupportIntegrity01 >= 1)
        { feedback = "Life support is already healthy."; return false; }
        if (!LifeServiceIsolated(state, enginePowered, out feedback)) return false;
        if (state.CircuitSpares == 0 || state.LineSpares == 0)
        { feedback = "Life support service requires one circuit spare and one line spare aboard."; return false; }
        feedback = string.Empty;
        return true;
    }

    private bool ServiceLife(SpacecraftTelemetry flight, out string feedback)
    {
        var state = State;
        if (!CanServiceLife(state, flight.Powered, flight.Travel, out feedback)) return false;
        _circuitSpares--;
        _lineSpares--;
        _lifeSupport = MathF.Min(1, _lifeSupport + LifeServiceIntegrityGain);
        feedback = $"Life support serviced to {_lifeSupport:P0} integrity. Circuit spares: {_circuitSpares}; line spares: {_lineSpares}.";
        return true;
    }
}
