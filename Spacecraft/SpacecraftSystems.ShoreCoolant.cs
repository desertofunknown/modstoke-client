// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftSystems
{
    private const double ShoreCoolantUnitSeconds = 1d / SpacecraftShoreCoolant.MaximumUnitsPerSecond;
    private const int ShoreCoolantValveMask = (1 << (int)SpacecraftValve.CoolantFeed) | (1 << (int)SpacecraftValve.Radiator);
    private bool _shoreCoolantEnabled, _shoreCoolantAvailable;
    private ShoreCoolantStatus _shoreCoolantStatus;
    private double _shoreCoolantPumpSeconds;

    internal void ResetShoreCoolant()
    {
        _shoreCoolantEnabled = _shoreCoolantAvailable = false;
        _shoreCoolantStatus = ShoreCoolantStatus.Off;
        _shoreCoolantPumpSeconds = 0;
    }

    private bool SafeForShoreCoolant(SpacecraftTelemetry flight) => !flight.Powered && !_generator
        && (_valveMask & ShoreCoolantValveMask) == 0 && WarpCharge(ElectricalInterval, flight) <= 0;

    internal void CheckShoreCoolantSafety(SpacecraftTelemetry flight)
    {
        if (!_shoreCoolantEnabled) return;
        if (!flight.ShoreConnected || flight.Region != SpacecraftRegion.Space || flight.Travel != SpacecraftTravel.None)
            ResetShoreCoolant();
        else if (!SafeForShoreCoolant(flight))
        {
            ResetShoreCoolant();
            _shoreCoolantStatus = ShoreCoolantStatus.Unsafe;
        }
    }

    private void BeginShoreCoolantInterval(bool connected, SpacecraftTelemetry flight)
    {
        if (!connected) { ResetShoreCoolant(); return; }
        CheckShoreCoolantSafety(flight);
        _shoreCoolantAvailable = _shoreCoolantEnabled && _shoreCoupling01 >= 1 && _travel == SpacecraftTravel.None;
        if (_shoreCoolantAvailable || !_shoreCoolantEnabled) return;
        _shoreCoolantPumpSeconds = 0;
        _shoreCoolantStatus = ShoreCoolantStatus.Waiting;
    }

    private bool SetShoreCoolant(bool enabled, SpacecraftTelemetry flight, out string message)
    {
        if (!enabled)
        {
            ResetShoreCoolant();
            message = "Coolant service stopped.";
            return true;
        }
        if (!flight.ShoreConnected || flight.Region != SpacecraftRegion.Space || flight.Travel != SpacecraftTravel.None)
        { message = "Dock at the station berth before requesting coolant service."; return false; }
        if (!SafeForShoreCoolant(flight))
        { message = "Stop the engine and generator, close the coolant feed and radiator valves and stop warp charging before coolant service."; return false; }
        if (_shoreCoolantEnabled)
        { message = "Coolant service already requested."; return true; }
        ResetShoreCoolant();
        if (!TryCreditShoreLiquid(_coolant, SpacecraftShoreCoolant.ShipCoolantUnitsCapacity, out _))
        {
            _shoreCoolantStatus = ShoreCoolantStatus.Limit;
            message = "Coolant reserve has less than one water unit of room.";
            return true;
        }
        _shoreCoolantEnabled = true;
        _shoreCoolantStatus = ShoreCoolantStatus.Waiting;
        message = "Coolant service requested.";
        return true;
    }

    // The main-thread computer gate covers station debit and ship credit together.
    internal bool RefillCoolantFromStation(BoxStationSystems station, int area, int tank, float dt)
    {
        CheckShoreCoolantSafety(_flight);
        if (!_shoreCoolantAvailable) return false;
        // Consume this completed worker interval once, including partial power delivery.
        _shoreCoolantAvailable = false;
        double seconds = Math.Min(_electricalSeconds, dt);
        bool changed = false;
        while (true)
        {
            if (!TryCreditShoreLiquid(_coolant, SpacecraftShoreCoolant.ShipCoolantUnitsCapacity, out float next))
            {
                ResetShoreCoolant();
                _shoreCoolantStatus = ShoreCoolantStatus.Limit;
                return changed;
            }
            if (station.Firefighting.WaterUnits(tank) < 1)
            {
                ResetShoreCoolant();
                _shoreCoolantStatus = ShoreCoolantStatus.NoSupply;
                return changed;
            }
            if (seconds <= 0) return changed;
            bool complete = TryPumpShoreLiquidUnit(station, area, SpacecraftShoreCoolant.PumpWatts, ShoreCoolantUnitSeconds,
                ref _shoreCoolantPumpSeconds, ref seconds, out bool powered);
            _shoreCoolantStatus = powered ? ShoreCoolantStatus.Filling : ShoreCoolantStatus.NoPower;
            if (!complete) return changed;
            if (!station.Firefighting.TryTakeWater(tank, 1))
            {
                ResetShoreCoolant();
                _shoreCoolantStatus = ShoreCoolantStatus.NoSupply;
                return changed;
            }
            RefillCoolantThermal(_coolant, next);
            _coolant = next;
            changed = true;
        }
    }
}
