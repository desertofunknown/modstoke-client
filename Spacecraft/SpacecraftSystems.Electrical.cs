// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftSystems
{
    internal const float ShoreCouplingSeconds = 1.2f;
    private const double BatteryJoules = SpacecraftElectrical.BatteryCapacityKwh * 3_600_000d;
    private double _electricalSeconds, _loadKj, _generationKj, _batteryKj, _shoreJoules;
    private float _shoreLimitKw, _shoreCoupling01;
    private bool _shoreAvailable;
    private SpacecraftElectricalState _measuredElectrical;
    private bool _hasElectricalReading;

    private void ClearElectricalInterval()
    {
        _electricalSeconds = _loadKj = _generationKj = _batteryKj = _shoreJoules = 0;
        _shoreLimitKw = 0;
        _shoreAvailable = false;
        _shoreFuelAvailable = false;
        _shoreCoolantAvailable = false;
    }

    internal void DisconnectShorePower()
    {
        ResetElectricalReadings();
        RefreshElectrical(_flight);
    }

    private void ResetElectricalReadings()
    {
        ClearElectricalInterval();
        _shoreCoupling01 = 0;
        ResetShoreAir();
        ResetShoreFuel();
        ResetShoreCoolant();
        _measuredElectrical = default;
        _hasElectricalReading = false;
    }

    private void RecordElectricalInterval(float dt)
    {
        _electricalSeconds += dt;
        _loadKj += (double)_electrical.LoadKw * dt;
        _generationKj += (double)_electrical.GenerationKw * dt;
        _batteryKj += (double)_electrical.BatteryKw * dt;
        float supply = !_busIsolated && _power > .08f ? _power : 0;
        // A completed interval may be coalesced. Its most restrictive substep
        // bounds shore charging without reusing time already spent by the APU.
        float residual = SpacecraftElectrical.BatteryChargeKw * supply - MathF.Max(0, -_electrical.BatteryKw);
        _shoreLimitKw = MathF.Min(_shoreLimitKw, MathF.Max(0, residual));
    }

    internal bool TryTakeShoreChargeRequest(float stationDt, out double joules)
    {
        joules = 0;
        if (_busIsolated || !_shoreAvailable) return false;
        _shoreAvailable = false;
        double seconds = Math.Min(_electricalSeconds, stationDt);
        joules = Math.Min((1d - _battery) * BatteryJoules, _shoreLimitKw * 1000d * seconds);
        return true;
    }

    // The caller holds the computer gate across station debit and this credit.
    // Requested energy already respects capacity and the completed charge interval.
    internal void CreditShoreCharge(double joules)
    {
        if (!_busIsolated && joules > 0)
        {
            double target = Math.Min(1d, _battery + joules / BatteryJoules);
            float next = (float)target;
            // Round down so stored charge never exceeds supplied energy.
            if (next > target) next = MathF.BitDecrement(next);
            _shoreJoules = ((double)next - _battery) * BatteryJoules;
            _battery = next;
        }
        SettleElectricalInterval();
        RefreshElectrical(_flight);
    }

    private void SettleElectricalInterval()
    {
        if (_electricalSeconds <= 0) return;
        float shore = (float)(_shoreJoules / (_electricalSeconds * 1000));
        _measuredElectrical = _electrical with
        {
            LoadKw = (float)(_loadKj / _electricalSeconds),
            GenerationKw = (float)(_generationKj / _electricalSeconds),
            ShoreKw = shore,
            BatteryKw = Math.Clamp((float)(_batteryKj / _electricalSeconds) - shore,
                -SpacecraftElectrical.BatteryChargeKw, SpacecraftElectrical.BatteryDischargeKw)
        };
        _hasElectricalReading = true;
    }

    private void CancelShoreCharge()
    {
        _shoreAirAvailable = false;
        _shoreFuelAvailable = false;
        if (!_shoreAvailable) return;
        _shoreAvailable = false;
        SettleElectricalInterval();
    }

    private void ApplyElectricalReadings()
    {
        if (!_hasElectricalReading) return;
        // A docked worker interval is not settled until the station pays its
        // shore transfer. Keep the last complete reading while it is pending.
        _electrical = _electrical with
        {
            LoadKw = _measuredElectrical.LoadKw,
            GenerationKw = _measuredElectrical.GenerationKw,
            BatteryKw = _measuredElectrical.BatteryKw,
            ShoreKw = _measuredElectrical.ShoreKw
        };
    }
}
