// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftSystems
{
    internal const float EngineServiceIntegrityGain = .25f;

    internal static bool EngineServiceIsolated(in SpacecraftSystemState state, bool enginePowered)
        => EngineServiceIsolated(state, enginePowered, out _);

    internal static bool EngineServiceIsolated(in SpacecraftSystemState state, bool enginePowered,
        out string feedback)
    {
        if (enginePowered)
        { feedback = "Turn the engine off before servicing it."; return false; }
        if (state.GeneratorEnabled)
        { feedback = "Turn the generator off before servicing the engine."; return false; }
        if ((state.ValveMask & (1 << (int)SpacecraftValve.EngineFuel)) != 0)
        { feedback = "Close the engine fuel valve before servicing the engine."; return false; }
        if ((state.ValveMask & (1 << (int)SpacecraftValve.ApuFuel)) != 0)
        { feedback = "Close the APU fuel valve before servicing the engine."; return false; }
        if ((state.ValveMask & (1 << (int)SpacecraftValve.WarpFuel)) != 0)
        { feedback = "Close the warp fuel valve before servicing the engine."; return false; }
        if ((state.Electrical.ClosedMask & (1 << (int)SpacecraftCircuit.Propulsion)) != 0)
        { feedback = "Open the propulsion breaker before servicing the engine."; return false; }
        if ((state.Electrical.ClosedMask & (1 << (int)SpacecraftCircuit.Warp)) != 0)
        { feedback = "Open the warp breaker before servicing the engine."; return false; }
        if (state.ShoreFuelEnabled)
        { feedback = "Turn shore fuel off before servicing the engine."; return false; }
        feedback = string.Empty;
        return true;
    }

    private static bool CanServiceEngine(in SpacecraftSystemState state, bool enginePowered,
        SpacecraftTravel travel, out string feedback)
    {
        if (travel != SpacecraftTravel.None)
        { feedback = "Wait for the journey to finish before servicing the engine."; return false; }
        if (state.EngineIntegrity01 >= 1)
        { feedback = "Engine is already healthy."; return false; }
        if (!EngineServiceIsolated(state, enginePowered, out feedback)) return false;
        if (state.CircuitSpares == 0 || state.LineSpares == 0)
        { feedback = "Engine service requires one circuit spare and one line spare aboard."; return false; }
        feedback = string.Empty;
        return true;
    }

    private bool ServiceEngine(SpacecraftTelemetry flight, out string feedback)
    {
        var state = State;
        if (!CanServiceEngine(state, flight.Powered, flight.Travel, out feedback)) return false;
        _circuitSpares--;
        _lineSpares--;
        _engine = MathF.Min(1, _engine + EngineServiceIntegrityGain);
        feedback = $"Engine serviced to {_engine:P0} integrity. Circuit spares: {_circuitSpares}; line spares: {_lineSpares}.";
        return true;
    }
}
