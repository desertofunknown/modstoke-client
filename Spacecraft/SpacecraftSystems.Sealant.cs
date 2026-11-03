// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Client;

internal enum SpacecraftCompartment : byte { Cabin = 0, Airlock = 1, Entry = 2 }

internal readonly record struct SpacecraftSealantState(float CabinLeak, float AirlockLeak, float EntryLeak,
    float CabinSeconds, float AirlockSeconds, float EntrySeconds)
{
    internal float Leak(SpacecraftCompartment compartment) => compartment switch
    {
        SpacecraftCompartment.Cabin => CabinLeak,
        SpacecraftCompartment.Airlock => AirlockLeak,
        SpacecraftCompartment.Entry => EntryLeak,
        _ => 0
    };

    internal float Remaining(SpacecraftCompartment compartment) => compartment switch
    {
        SpacecraftCompartment.Cabin => CabinSeconds,
        SpacecraftCompartment.Airlock => AirlockSeconds,
        SpacecraftCompartment.Entry => EntrySeconds,
        _ => 0
    };
}

internal sealed partial class ClientSpacecraftSystems
{
    internal const float SealantCycleSeconds = 6, SealantLeakReduction = .25f;
    private const float SealantPumpKw = .3f;
    private float _cabinSealSeconds, _airlockSealSeconds, _entrySealSeconds;

    private SpacecraftSealantState SealantState => new(_cabinLeak, _airlockLeak, _entryLeak,
        _cabinSealSeconds, _airlockSealSeconds, _entrySealSeconds);

    internal static bool ValidSealantState(SpacecraftSealantState state)
        => InRange(state.CabinLeak, 2) && InRange(state.AirlockLeak, 2) && InRange(state.EntryLeak, 2)
            && InRange(state.CabinSeconds, SealantCycleSeconds) && InRange(state.AirlockSeconds, SealantCycleSeconds)
            && InRange(state.EntrySeconds, SealantCycleSeconds);

    internal static bool TryReadSealantCommand(string command, out SpacecraftCompartment compartment)
    {
        switch (command)
        {
            case "seal-cabin": compartment = SpacecraftCompartment.Cabin; return true;
            case "seal-airlock": compartment = SpacecraftCompartment.Airlock; return true;
            case "seal-entry": compartment = SpacecraftCompartment.Entry; return true;
            default: compartment = default; return false;
        }
    }

    private float SealantLoadKw(float dt, SpacecraftTelemetry flight)
    {
        if (flight.Travel != SpacecraftTravel.None || _lifeSupport <= .1f) return 0;
        // Average only the work remaining in this interval; a finishing cartridge
        // cannot charge the full pump rate after its cycle has completed.
        return SealantPumpKw * (MathF.Min(dt, _cabinSealSeconds) + MathF.Min(dt, _airlockSealSeconds)
            + MathF.Min(dt, _entrySealSeconds)) / dt;
    }

    private void AdvanceSealant(float dt, SpacecraftTelemetry flight)
    {
        if (flight.Travel != SpacecraftTravel.None || _lifeSupport <= .1f || !Powered(SpacecraftCircuit.LifeSupport)) return;
        // Pump work is paid electrically and enters the hull thermal stores.
        AdvanceSealant(ref _cabinSealSeconds, ref _cabinLeak, dt);
        AdvanceSealant(ref _airlockSealSeconds, ref _airlockLeak, dt);
        AdvanceSealant(ref _entrySealSeconds, ref _entryLeak, dt);
    }

    private static void AdvanceSealant(ref float remaining, ref float leak, float dt)
    {
        if (remaining <= 0) return;
        float seconds = MathF.Min(dt, remaining);
        remaining -= seconds;
        if (remaining == 0) leak = MathF.Max(0, leak - SealantLeakReduction);
    }

    private bool StartSealant(SpacecraftCompartment compartment, SpacecraftTelemetry flight, out string feedback)
    {
        if (flight.Travel != SpacecraftTravel.None)
        { feedback = "Wait for the journey to finish before starting a sealant cycle."; return false; }
        var sealant = SealantState;
        if (sealant.Remaining(compartment) > 0)
        { feedback = "A sealant cartridge is already loaded in this compartment."; return false; }
        if (sealant.Leak(compartment) <= 0)
        { feedback = "This compartment has no pressure leak to seal."; return false; }
        if (_lifeSupport <= .1f)
        { feedback = "Life support is too damaged to operate the sealant pump."; return false; }
        var electrical = SolveElectrical(ElectricalInterval, flight, WarpCharge(ElectricalInterval, flight));
        if (!SpacecraftElectrical.IsPowered(electrical, SpacecraftCircuit.LifeSupport))
        { feedback = "Power the life support circuit before starting a sealant cycle."; return false; }
        if (_lineSpares == 0)
        { feedback = "A sealant cycle requires one line spare aboard."; return false; }
        _lineSpares--;
        switch (compartment)
        {
            case SpacecraftCompartment.Cabin: _cabinSealSeconds = SealantCycleSeconds; break;
            case SpacecraftCompartment.Airlock: _airlockSealSeconds = SealantCycleSeconds; break;
            case SpacecraftCompartment.Entry: _entrySealSeconds = SealantCycleSeconds; break;
        }
        feedback = $"{compartment} sealant cycle started. Line spares: {_lineSpares}.";
        return true;
    }
}
