// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Text.Json.Serialization;

namespace Modstoke.Client;

internal readonly record struct SpacecraftFireZone(float Fuel01, float HotspotKelvin, float PowerWatts)
{
    internal bool Active => PowerWatts > 0;
}

internal readonly record struct SpacecraftFireState(SpacecraftFireZone Cabin, SpacecraftFireZone Airlock,
    SpacecraftFireZone Entry)
{
    internal bool Active => Cabin.Active || Airlock.Active || Entry.Active;

    internal SpacecraftFireZone Section(SpacecraftCompartment compartment) => compartment switch
    {
        SpacecraftCompartment.Cabin => Cabin,
        SpacecraftCompartment.Airlock => Airlock,
        SpacecraftCompartment.Entry => Entry,
        _ => throw new ArgumentOutOfRangeException(nameof(compartment))
    };
}

internal sealed partial class ClientSpacecraftSystems
{
    // Effective furnishings expressed as solid carbon, not measured material stocks.
    private const double CabinFireFuel = 600, AirlockFireFuel = 120, EntryFireFuel = 240;
    private const double CabinHotspotCapacity = 3000, AirlockHotspotCapacity = 1000, EntryHotspotCapacity = 1500;
    private const double FireIgnitionKelvin = 473.15, CarbonReactionJoules = 393500;
    private FireZone _cabinFire, _airlockFire, _entryFire;

    internal SpacecraftFireState FireState => new(_cabinFire.State(CabinFireFuel),
        _airlockFire.State(AirlockFireFuel), _entryFire.State(EntryFireFuel));

    private void ResetFire()
    {
        _cabinFire = new() { FuelMoles = CabinFireFuel, HotspotKelvin = SpacecraftThermalState.InitialKelvin };
        _airlockFire = new() { FuelMoles = AirlockFireFuel, HotspotKelvin = SpacecraftThermalState.InitialKelvin };
        _entryFire = new() { FuelMoles = EntryFireFuel, HotspotKelvin = SpacecraftThermalState.InitialKelvin };
    }

    private void AdvanceFire(float dt)
    {
        if (!float.IsFinite(dt) || dt <= 0) throw new ArgumentOutOfRangeException(nameof(dt));
        double cabinHeat = AdvanceFireZone(ref _cabinFire, ref _cabin, ref _cabinHullKelvin,
            CabinHullCapacity, CabinHotspotCapacity, 100, 30, 250000, dt);
        double airlockHeat = AdvanceFireZone(ref _airlockFire, ref _airlock, ref _airlockHullKelvin,
            AirlockHullCapacity, AirlockHotspotCapacity, 20, 6, 40000, dt);
        double entryHeat = AdvanceFireZone(ref _entryFire, ref _entry, ref _entryHullKelvin,
            EntryHullCapacity, EntryHotspotCapacity, 40, 12, 80000, dt);
        ApplyFireDamage(SpacecraftCompartment.Cabin, cabinHeat);
        ApplyFireDamage(SpacecraftCompartment.Airlock, airlockHeat);
        ApplyFireDamage(SpacecraftCompartment.Entry, entryHeat);
    }

    private static double AdvanceFireZone(ref FireZone fire, ref SpacecraftAtmosphere air, ref double hull,
        double hullCapacity, double hotspotCapacity, double hullConductance, double gasConductance,
        double maximumWatts, double dt)
    {
        ValidateHotspot(fire.HotspotKelvin);
        fire.PowerWatts = 0;
        ExchangeFireHeat(ref fire.HotspotKelvin, hotspotCapacity, ref hull, hullCapacity,
            ref air, hullConductance, gasConductance, dt * .5);
        double heat = BurnFireFuel(ref fire, ref air, hotspotCapacity, maximumWatts, dt);
        ExchangeFireHeat(ref fire.HotspotKelvin, hotspotCapacity, ref hull, hullCapacity,
            ref air, hullConductance, gasConductance, dt * .5);
        ValidateHotspot(fire.HotspotKelvin);
        if (CanBurn(fire, air)) fire.PowerWatts = heat / dt;
        return heat;
    }

    private static void ExchangeFireHeat(ref double hotspot, double hotspotCapacity, ref double hull,
        double hullCapacity, ref SpacecraftAtmosphere air, double hullConductance, double gasConductance, double dt)
    {
        ExchangeSolidHeat(ref hotspot, hotspotCapacity, ref hull, hullCapacity, hullConductance, dt);
        ExchangeGasHeat(ref air, ref hotspot, hotspotCapacity, gasConductance, dt);
    }

    private static bool CanBurn(FireZone fire, SpacecraftAtmosphere air)
        => fire.FuelMoles > 0 && fire.HotspotKelvin > FireIgnitionKelvin && air.OxygenPartialPressure > 5;

    private static double BurnFireFuel(ref FireZone fire, ref SpacecraftAtmosphere air,
        double hotspotCapacity, double maximumWatts, double dt)
    {
        if (!CanBurn(fire, air)) return 0;
        double watts = maximumWatts * Math.Clamp((fire.HotspotKelvin - FireIgnitionKelvin) / 200, 0, 1)
            * Math.Clamp((air.OxygenPartialPressure - 5) / 7, 0, 1);
        double room = (Math.BitDecrement(SpacecraftThermalState.MaximumKelvin) - fire.HotspotKelvin) * hotspotCapacity;
        double requested = Math.Min(fire.FuelMoles, Math.Min(watts * dt, Math.Max(0, room)) / CarbonReactionJoules);
        double fuel = fire.FuelMoles - requested;
        if (fire.FuelMoles - fuel > requested) fuel = Math.BitIncrement(fuel);
        requested = fire.FuelMoles - fuel;
        if (requested <= 0) return 0;

        var candidate = air;
        double burned = candidate.BurnCarbon(requested);
        fuel = fire.FuelMoles - burned;
        // Commit solid fuel and gas together; rejected or unrepresentable
        // reactions cannot contribute heat or damage.
        if (burned <= 0 || fire.FuelMoles - fuel != burned) return 0;
        double heat = burned * CarbonReactionJoules;
        double hotspot = fire.HotspotKelvin + heat / hotspotCapacity;
        ValidateHotspot(hotspot);
        fire.FuelMoles = fuel;
        fire.HotspotKelvin = hotspot;
        air = candidate;
        return heat;
    }

    private void ApplyFireDamage(SpacecraftCompartment compartment, double joules)
    {
        if (joules <= 0) return;
        float hullDamage = (float)(joules / 60000000), equipmentDamage = (float)(joules / 120000000);
        _hull = _hull.WithIntegrity(compartment, MathF.Max(0, _hull.Integrity(compartment) - hullDamage));
        switch (compartment)
        {
            case SpacecraftCompartment.Cabin:
                _cabinLeak = MathF.Min(2, _cabinLeak + equipmentDamage);
                _power = MathF.Max(0, _power - equipmentDamage);
                break;
            case SpacecraftCompartment.Airlock:
                _airlockLeak = MathF.Min(2, _airlockLeak + equipmentDamage);
                _lifeSupport = MathF.Max(0, _lifeSupport - equipmentDamage);
                break;
            case SpacecraftCompartment.Entry:
                _entryLeak = MathF.Min(2, _entryLeak + equipmentDamage);
                _engine = MathF.Max(0, _engine - equipmentDamage);
                break;
        }
    }

    private void AddImpactFireHeat(SpacecraftCompartment compartment, float admittedJoules)
    {
        if (!float.IsFinite(admittedJoules) || admittedJoules < 0)
            throw new ArgumentOutOfRangeException(nameof(admittedJoules));
        double heat = admittedJoules * .15;
        switch (compartment)
        {
            case SpacecraftCompartment.Cabin: AddHotspotHeat(ref _cabinFire, CabinHotspotCapacity, heat); break;
            case SpacecraftCompartment.Airlock: AddHotspotHeat(ref _airlockFire, AirlockHotspotCapacity, heat); break;
            case SpacecraftCompartment.Entry: AddHotspotHeat(ref _entryFire, EntryHotspotCapacity, heat); break;
            default: throw new ArgumentOutOfRangeException(nameof(compartment));
        }
    }

    private static void AddHotspotHeat(ref FireZone fire, double capacity, double joules)
    {
        double next = fire.HotspotKelvin + joules / capacity;
        ValidateHotspot(next);
        fire.HotspotKelvin = next;
    }

    private static void ValidateHotspot(double kelvin)
    {
        if (!SpacecraftThermalState.ValidTemperature(kelvin))
            throw new InvalidOperationException("Spacecraft fire hotspot exceeded the 2.7 to 10000 K thermal model range.");
    }

    // The caller owns water withdrawal and the systems gate; this only moves heat.
    internal double SprayFire(SpacecraftCompartment compartment, float actualWaterUnits)
    {
        if (!float.IsFinite(actualWaterUnits) || actualWaterUnits is < 0 or > 5)
            throw new ArgumentOutOfRangeException(nameof(actualWaterUnits));
        return compartment switch
        {
            SpacecraftCompartment.Cabin => SprayFireZone(ref _cabinFire, CabinHotspotCapacity,
                ref _cabinHullKelvin, CabinHullCapacity, actualWaterUnits),
            SpacecraftCompartment.Airlock => SprayFireZone(ref _airlockFire, AirlockHotspotCapacity,
                ref _airlockHullKelvin, AirlockHullCapacity, actualWaterUnits),
            SpacecraftCompartment.Entry => SprayFireZone(ref _entryFire, EntryHotspotCapacity,
                ref _entryHullKelvin, EntryHullCapacity, actualWaterUnits),
            _ => throw new ArgumentOutOfRangeException(nameof(compartment))
        };
    }

    private static double SprayFireZone(ref FireZone fire, double hotspotCapacity,
        ref double hull, double hullCapacity, float waterUnits)
    {
        double floor = Math.Max(SpacecraftThermalState.InitialKelvin, hull);
        if (waterUnits <= 0 || fire.HotspotKelvin <= floor) return 0;
        // Coarse wet-surface/runoff transport into the local hull; no vapor
        // chemistry or heat deletion. Both finite nodes limit the cooling dose.
        double equalizing = (fire.HotspotKelvin - hull) / (1 / hotspotCapacity + 1 / hullCapacity);
        double heat = Math.Min(waterUnits * 120000d,
            Math.Min(equalizing, (fire.HotspotKelvin - floor) * hotspotCapacity));
        fire.HotspotKelvin -= heat / hotspotCapacity;
        hull += heat / hullCapacity;
        if (fire.HotspotKelvin <= FireIgnitionKelvin) fire.PowerWatts = 0;
        return heat;
    }

    private SavedSpacecraftFire CaptureFire() => new()
    {
        Cabin = _cabinFire.Capture(), Airlock = _airlockFire.Capture(), Entry = _entryFire.Capture()
    };

    private static bool ValidSavedFire(SavedSpacecraftFire? saved)
        => saved != null && ValidSavedFireZone(saved.Cabin, CabinFireFuel)
            && ValidSavedFireZone(saved.Airlock, AirlockFireFuel) && ValidSavedFireZone(saved.Entry, EntryFireFuel);

    private static bool ValidSavedFireZone(SavedSpacecraftFireZone? saved, double maximumFuel)
        => saved != null && double.IsFinite(saved.FuelMoles) && saved.FuelMoles >= 0 && saved.FuelMoles <= maximumFuel
            && SpacecraftThermalState.ValidTemperature(saved.HotspotKelvin);

    private void RestoreFire(SavedSpacecraftFire saved)
    {
        _cabinFire = new() { FuelMoles = saved.Cabin!.FuelMoles, HotspotKelvin = saved.Cabin.HotspotKelvin };
        _airlockFire = new() { FuelMoles = saved.Airlock!.FuelMoles, HotspotKelvin = saved.Airlock.HotspotKelvin };
        _entryFire = new() { FuelMoles = saved.Entry!.FuelMoles, HotspotKelvin = saved.Entry.HotspotKelvin };
    }

    private struct FireZone
    {
        internal double FuelMoles, HotspotKelvin, PowerWatts;
        internal readonly SpacecraftFireZone State(double maximumFuel)
            => new((float)(FuelMoles / maximumFuel), (float)HotspotKelvin, (float)PowerWatts);
        internal readonly SavedSpacecraftFireZone Capture() => new() { FuelMoles = FuelMoles, HotspotKelvin = HotspotKelvin };
    }
}

internal sealed class SavedSpacecraftFire
{
    [JsonRequired]
    public SavedSpacecraftFireZone? Cabin { get; set; }
    [JsonRequired]
    public SavedSpacecraftFireZone? Airlock { get; set; }
    [JsonRequired]
    public SavedSpacecraftFireZone? Entry { get; set; }
}

internal sealed class SavedSpacecraftFireZone
{
    [JsonRequired]
    public double FuelMoles { get; set; }
    [JsonRequired]
    public double HotspotKelvin { get; set; }
}
