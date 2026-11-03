// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Client;

internal readonly record struct SpacecraftHullState(float Cabin, float Airlock, float Entry)
{
    internal float Integrity01 => (Cabin + Airlock + Entry) / 3;
    internal float MinimumIntegrity01 => MathF.Min(Cabin, MathF.Min(Airlock, Entry));

    internal float Integrity(SpacecraftCompartment compartment) => compartment switch
    {
        SpacecraftCompartment.Cabin => Cabin,
        SpacecraftCompartment.Airlock => Airlock,
        SpacecraftCompartment.Entry => Entry,
        _ => throw new ArgumentOutOfRangeException(nameof(compartment))
    };

    internal SpacecraftHullState WithIntegrity(SpacecraftCompartment compartment, float integrity) => compartment switch
    {
        SpacecraftCompartment.Cabin => this with { Cabin = integrity },
        SpacecraftCompartment.Airlock => this with { Airlock = integrity },
        SpacecraftCompartment.Entry => this with { Entry = integrity },
        _ => throw new ArgumentOutOfRangeException(nameof(compartment))
    };
}

internal sealed partial class ClientSpacecraftSystems
{
    internal static SpacecraftCompartment HullCompartment(float localZ) => localZ < -9.1f
        ? SpacecraftCompartment.Entry : localZ < -4.84f ? SpacecraftCompartment.Airlock : SpacecraftCompartment.Cabin;

    internal static bool TryReadHullRepairCommand(string command, out SpacecraftCompartment compartment)
    {
        switch (command)
        {
            case "service-hull": compartment = SpacecraftCompartment.Cabin; return true;
            case "service-hull-airlock": compartment = SpacecraftCompartment.Airlock; return true;
            case "service-hull-entry": compartment = SpacecraftCompartment.Entry; return true;
            default: compartment = default; return false;
        }
    }
}
