// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftSystems
{
    private bool _shoreAirEnabled, _shoreAirAvailable;
    private ShoreAirStatus _shoreAirStatus;
    private float _shoreAirMolesPerSecond;
    private ushort _shoreAirWatts;

    internal void ResetShoreAir()
    {
        _shoreAirEnabled = _shoreAirAvailable = false;
        _shoreAirStatus = ShoreAirStatus.Off;
        _shoreAirMolesPerSecond = 0;
        _shoreAirWatts = 0;
    }

    private void BeginShoreAirInterval(bool connected)
    {
        if (!connected) { ResetShoreAir(); return; }
        _shoreAirAvailable = _shoreAirEnabled && _shoreCoupling01 >= 1;
        if (_shoreAirAvailable) return;
        _shoreAirMolesPerSecond = 0;
        _shoreAirWatts = 0;
        if (_shoreAirEnabled) _shoreAirStatus = ShoreAirStatus.Waiting;
    }

    private bool ToggleShoreAir(SpacecraftTelemetry flight, out string message)
        => SetShoreAir(!_shoreAirEnabled, flight, out message);

    private bool SetShoreAir(bool enabled, SpacecraftTelemetry flight, out string message)
    {
        if (!enabled)
        {
            ResetShoreAir();
            message = "Air service stopped.";
            return true;
        }
        if (!flight.ShoreConnected || flight.Region != SpacecraftRegion.Space || flight.Travel != SpacecraftTravel.None)
        { message = "Dock at the station berth before requesting air service."; return false; }
        if (_shoreAirEnabled)
        { message = "Air service already requested."; return true; }
        ResetShoreAir();
        if (MaximumReserveMoles - _reserve.Moles <= MaximumReserveMoles * 1e-12)
        {
            _shoreAirStatus = ShoreAirStatus.Full;
            message = "Air reserve full.";
            return true;
        }
        _shoreAirEnabled = true;
        _shoreAirStatus = ShoreAirStatus.Waiting;
        message = "Air service requested.";
        return true;
    }

    internal void RefillFromStation(BoxStationSystems station, float dt)
    {
        if (!_shoreAirAvailable) return;
        _shoreAirAvailable = false;
        float seconds = (float)Math.Min(_electricalSeconds, dt);
        if (seconds <= 0) return;
        var result = station.RefillShipAir(ref _reserve, MaximumReserveMoles, seconds);
        _shoreAirStatus = result.Status;
        _shoreAirMolesPerSecond = (float)Math.Min(SpacecraftShoreAir.MaximumMolesPerSecond, result.Moles / seconds);
        _shoreAirWatts = (ushort)Math.Clamp(Math.Round(result.Joules / seconds), 0, SpacecraftShoreAir.MaximumWatts);
        if (result.Status is ShoreAirStatus.Full or ShoreAirStatus.UnsafeGas) _shoreAirEnabled = false;
    }
}
