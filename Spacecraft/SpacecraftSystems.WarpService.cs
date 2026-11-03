// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftSystems
{
    internal const float WarpServiceIntegrityGain = .25f, WarpDischargeSeconds = 30;
    private bool _warpDischargeEnabled;

    internal static bool WarpDischargeIsolated(in SpacecraftSystemState state, out string feedback)
    {
        if ((state.Electrical.ClosedMask & (1 << (int)SpacecraftCircuit.Warp)) != 0)
        { feedback = "Open the warp breaker before discharging or servicing the drive."; return false; }
        if ((state.ValveMask & (1 << (int)SpacecraftValve.WarpFuel)) != 0)
        { feedback = "Close the warp fuel valve before discharging or servicing the drive."; return false; }
        feedback = string.Empty;
        return true;
    }

    private bool SetWarpDischarge(bool enabled, SpacecraftTelemetry flight, out string feedback)
    {
        if (flight.Travel != SpacecraftTravel.None)
        { feedback = "Wait for the journey to finish before operating the warp discharge circuit."; return false; }
        if (enabled && !WarpDischargeIsolated(State, out feedback)) return false;
        if (_warpDischargeEnabled == enabled)
        { feedback = enabled ? "Warp discharge is already selected." : "Warp discharge is already off."; return false; }
        _warpDischargeEnabled = enabled;
        feedback = enabled ? "Warp discharge selected; the isolated reserve will bleed down."
            : "Warp discharge stopped; breaker and fuel valve selections retained.";
        return true;
    }

    private void AdvanceWarpDischarge(float dt, SpacecraftTelemetry flight)
    {
        if (!_warpDischargeEnabled || flight.Travel != SpacecraftTravel.None
            || !WarpDischargeIsolated(State, out _)) return;
        // The normalized reserve is a maintenance model, not recoverable electrical energy.
        _warp -= MathF.Min(_warp, dt / WarpDischargeSeconds);
    }

    internal static bool WarpServiceIsolated(in SpacecraftSystemState state, bool enginePowered)
        => WarpServiceIsolated(state, enginePowered, out _);

    internal static bool WarpServiceIsolated(in SpacecraftSystemState state, bool enginePowered,
        out string feedback)
    {
        if (enginePowered)
        { feedback = "Turn the engine off before servicing the warp drive."; return false; }
        if (state.GeneratorEnabled)
        { feedback = "Turn the generator off before servicing the warp drive."; return false; }
        if (!WarpDischargeIsolated(state, out feedback)) return false;
        if (state.ShoreFuelEnabled)
        { feedback = "Turn shore fuel off before servicing the warp drive."; return false; }
        if (!state.WarpDischargeEnabled)
        { feedback = "Select warp discharge before servicing the drive."; return false; }
        if (state.WarpCharge01 != 0)
        { feedback = "Wait for the warp reserve to discharge fully before servicing the drive."; return false; }
        feedback = string.Empty;
        return true;
    }

    private static bool CanServiceWarp(in SpacecraftSystemState state, bool enginePowered,
        SpacecraftTravel travel, out string feedback)
    {
        if (travel != SpacecraftTravel.None)
        { feedback = "Wait for the journey to finish before servicing the warp drive."; return false; }
        if (state.WarpIntegrity01 >= 1)
        { feedback = "Warp drive is already healthy."; return false; }
        if (!WarpServiceIsolated(state, enginePowered, out feedback)) return false;
        if (state.CircuitSpares == 0 || state.LineSpares == 0)
        { feedback = "Warp drive service requires one circuit spare and one line spare aboard."; return false; }
        feedback = string.Empty;
        return true;
    }

    private bool ServiceWarp(SpacecraftTelemetry flight, out string feedback)
    {
        var state = State;
        if (!CanServiceWarp(state, flight.Powered, flight.Travel, out feedback)) return false;
        _circuitSpares--;
        _lineSpares--;
        _warpIntegrity = MathF.Min(1, _warpIntegrity + WarpServiceIntegrityGain);
        feedback = $"Warp drive serviced to {_warpIntegrity:P0} integrity. Circuit spares: {_circuitSpares}; line spares: {_lineSpares}.";
        return true;
    }
}
