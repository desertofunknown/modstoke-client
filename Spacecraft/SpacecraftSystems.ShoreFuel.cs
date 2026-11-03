// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftSystems
{
    private const double ShoreFuelUnitSeconds = 1d / SpacecraftShoreFuel.MaximumUnitsPerSecond;
    private const int ShoreFuelFeedMask = (1 << (int)SpacecraftValve.EngineFuel)
        | (1 << (int)SpacecraftValve.ApuFuel) | (1 << (int)SpacecraftValve.WarpFuel);
    private bool _shoreFuelEnabled, _shoreFuelAvailable;
    private ShoreFuelStatus _shoreFuelStatus;
    private double _shoreFuelPumpSeconds;

    internal void ResetShoreFuel()
    {
        _shoreFuelEnabled = _shoreFuelAvailable = false;
        _shoreFuelStatus = ShoreFuelStatus.Off;
        _shoreFuelPumpSeconds = 0;
    }

    private bool SafeForShoreFuel(SpacecraftTelemetry flight) => !flight.Powered && !_generator
        && (_valveMask & ShoreFuelFeedMask) == 0 && WarpCharge(ElectricalInterval, flight) <= 0;

    internal void CheckShoreFuelSafety(SpacecraftTelemetry flight)
    {
        if (!_shoreFuelEnabled) return;
        if (!flight.ShoreConnected || flight.Region != SpacecraftRegion.Space || flight.Travel != SpacecraftTravel.None)
            ResetShoreFuel();
        else if (!SafeForShoreFuel(flight))
        {
            ResetShoreFuel();
            _shoreFuelStatus = ShoreFuelStatus.Unsafe;
        }
    }

    private void BeginShoreFuelInterval(bool connected, SpacecraftTelemetry flight)
    {
        if (!connected) { ResetShoreFuel(); return; }
        CheckShoreFuelSafety(flight);
        _shoreFuelAvailable = _shoreFuelEnabled && _shoreCoupling01 >= 1 && _travel == SpacecraftTravel.None;
        if (_shoreFuelAvailable || !_shoreFuelEnabled) return;
        _shoreFuelPumpSeconds = 0;
        _shoreFuelStatus = ShoreFuelStatus.Waiting;
    }

    private bool SetShoreFuel(bool enabled, SpacecraftTelemetry flight, out string message)
    {
        if (!enabled)
        {
            ResetShoreFuel();
            message = "Fuel service stopped.";
            return true;
        }
        if (!flight.ShoreConnected || flight.Region != SpacecraftRegion.Space || flight.Travel != SpacecraftTravel.None)
        { message = "Dock at the station berth before requesting fuel service."; return false; }
        if (!SafeForShoreFuel(flight))
        { message = "Stop the engine and generator, close all three fuel feed valves and stop warp charging before refueling."; return false; }
        if (_shoreFuelEnabled)
        { message = "Fuel service already requested."; return true; }
        ResetShoreFuel();
        if (!TryCreditShoreLiquid(_fuel, SpacecraftShoreFuel.ShipFuelUnitsCapacity, out _))
        {
            _shoreFuelStatus = ShoreFuelStatus.Limit;
            message = "Fuel reserve has less than one industrial-fuel unit of room.";
            return true;
        }
        _shoreFuelEnabled = true;
        _shoreFuelStatus = ShoreFuelStatus.Waiting;
        message = "Fuel service requested.";
        return true;
    }

    // The main-thread computer gate covers station debit and ship credit together.
    internal bool RefillFuelFromStation(BoxStationSystems station, int area, int tank, float dt)
    {
        CheckShoreFuelSafety(_flight);
        if (!_shoreFuelAvailable) return false;
        // Consume this completed worker interval once, including partial power delivery.
        _shoreFuelAvailable = false;
        double seconds = Math.Min(_electricalSeconds, dt);
        bool changed = false;
        while (true)
        {
            if (!TryCreditShoreLiquid(_fuel, SpacecraftShoreFuel.ShipFuelUnitsCapacity, out float next))
            {
                ResetShoreFuel();
                _shoreFuelStatus = ShoreFuelStatus.Limit;
                return changed;
            }
            if (station.Engineering.FuelUnits(tank) == 0)
            {
                ResetShoreFuel();
                _shoreFuelStatus = ShoreFuelStatus.NoSupply;
                return changed;
            }
            if (seconds <= 0) return changed;
            bool complete = TryPumpShoreLiquidUnit(station, area, SpacecraftShoreFuel.PumpWatts, ShoreFuelUnitSeconds,
                ref _shoreFuelPumpSeconds, ref seconds, out bool powered);
            _shoreFuelStatus = powered ? ShoreFuelStatus.Filling : ShoreFuelStatus.NoPower;
            if (!complete) return changed;
            if (!station.Engineering.TryTakeFuel(tank, 1))
            {
                ResetShoreFuel();
                _shoreFuelStatus = ShoreFuelStatus.NoSupply;
                return changed;
            }
            _fuel = next;
            changed = true;
        }
    }
}
