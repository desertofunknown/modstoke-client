// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Client;

internal enum SpacecraftCircuit
{
    LifeSupport = 0,
    Thermal = 1,
    Propulsion = 2,
    Warp = 3,
    Airlock = 4,
    Cabin = 5
}

// Closed records the operator's breaker selection; a trip opens the contact until
// repair. BatteryKw is positive during discharge and negative during charging.
internal readonly record struct SpacecraftElectricalState(byte ClosedMask, byte TrippedMask,
    byte PoweredMask, byte ShedMask, float LoadKw, float GenerationKw, float BatteryKw, float ShoreKw = 0);

internal readonly record struct SpacecraftElectricalLoads(float LifeSupportKw, float ThermalKw,
    float PropulsionKw, float WarpKw, float AirlockKw, float CabinKw);

internal static class SpacecraftElectrical
{
    internal const byte AllMask = 63;
    internal const float BatteryCapacityKwh = 12, FuelElectricalCapacityKwh = 240;
    internal const float GeneratorCapacityKw = 72, BatteryDischargeKw = 60, BatteryChargeKw = 36;
    private const float SupervisorKw = .12f, DamagedBusLossKw = 12;

    internal static bool IsPowered(SpacecraftElectricalState state, SpacecraftCircuit circuit)
        => (state.PoweredMask & (1 << (int)circuit)) != 0;

    internal static string CircuitName(SpacecraftCircuit circuit) => circuit switch
    {
        SpacecraftCircuit.LifeSupport => "Life support",
        SpacecraftCircuit.Thermal => "Thermal",
        SpacecraftCircuit.Propulsion => "Propulsion",
        SpacecraftCircuit.Warp => "Warp drive",
        SpacecraftCircuit.Airlock => "Airlock",
        SpacecraftCircuit.Cabin => "Cabin lighting",
        _ => "Unknown circuit"
    };

    internal static bool IsValid(SpacecraftElectricalState state)
    {
        if ((state.ClosedMask | state.TrippedMask | state.PoweredMask | state.ShedMask) > AllMask)
            return false;
        int connected = state.ClosedMask & ~state.TrippedMask;
        return (state.PoweredMask & state.ShedMask) == 0
            && (state.PoweredMask | state.ShedMask) == connected
            && float.IsFinite(state.LoadKw) && state.LoadKw is >= 0 and <= GeneratorCapacityKw + BatteryDischargeKw
            && float.IsFinite(state.GenerationKw) && state.GenerationKw is >= 0 and <= GeneratorCapacityKw
            && float.IsFinite(state.ShoreKw) && state.ShoreKw is >= 0 and <= BatteryChargeKw
            && float.IsFinite(state.BatteryKw) && state.BatteryKw is >= -BatteryChargeKw and <= BatteryDischargeKw
            && MathF.Abs(state.GenerationKw + state.ShoreKw + state.BatteryKw - state.LoadKw) <= .01f;
    }

    // Reserve a complete interval before workloads run. Unsupported branches shed
    // as a whole; APU fuel and battery energy bound the available source rates.
    internal static SpacecraftElectricalState Solve(byte closed, byte tripped, float integrity,
        float battery01, float fuel01, bool generator, SpacecraftElectricalLoads loads, float dt)
    {
        byte connected = (byte)(closed & ~tripped);
        float supply = integrity > .08f ? integrity : 0;
        float generation = generator ? MathF.Min(GeneratorCapacityKw * supply,
            fuel01 * FuelElectricalCapacityKwh * 3600 / dt) : 0;
        float discharge = MathF.Min(BatteryDischargeKw * supply, battery01 * BatteryCapacityKwh * 3600 / dt);
        float available = generation + discharge;
        float load = MathF.Min(available, SupervisorKw + DamagedBusLossKw * (1 - integrity));
        available -= load;
        byte powered = 0;
        Feed(SpacecraftCircuit.LifeSupport, loads.LifeSupportKw, connected, ref available, ref load, ref powered);
        Feed(SpacecraftCircuit.Airlock, loads.AirlockKw, connected, ref available, ref load, ref powered);
        Feed(SpacecraftCircuit.Thermal, loads.ThermalKw, connected, ref available, ref load, ref powered);
        Feed(SpacecraftCircuit.Cabin, loads.CabinKw, connected, ref available, ref load, ref powered);
        Feed(SpacecraftCircuit.Propulsion, loads.PropulsionKw, connected, ref available, ref load, ref powered);
        Feed(SpacecraftCircuit.Warp, loads.WarpKw, connected, ref available, ref load, ref powered);
        float charge = MathF.Min(BatteryChargeKw * supply, (1 - battery01) * BatteryCapacityKwh * 3600 / dt);
        generation = MathF.Min(generation, load + charge);
        return new SpacecraftElectricalState(closed, tripped, powered, (byte)(connected & ~powered),
            load, generation, load - generation);
    }

    private static void Feed(SpacecraftCircuit circuit, float demand, byte connected,
        ref float available, ref float load, ref byte powered)
    {
        int bit = 1 << (int)circuit;
        if ((connected & bit) == 0 || available < demand) return;
        available -= demand;
        load += demand;
        powered |= (byte)bit;
    }
}
