// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Client;

internal sealed partial class BoxStationPlumbing
{
    private const double ThermalPortVolume = .2, ThermalReservoirCapacity = 5000;
    private const double FreezerEfficiency = .35, MinimumFreezerLiftKelvin = 20;
    internal const float ThermalRatedWatts = 20_000, MaximumFreezerCop = 4;
    internal const float NeutralThermalKelvin = 293.15f;
    private readonly float[] _thermalTargetKelvin;
    private readonly float[] _electricWatts;
    private readonly float[] _thermalWatts;
    private readonly bool[] _thermalPowerLimited;

    internal static bool IsThermal(BoxStationPlumbingKind kind)
        => kind is BoxStationPlumbingKind.Heater or BoxStationPlumbingKind.Freezer;

    internal static (float Minimum, float Maximum) ThermalRange(BoxStationPlumbingKind kind)
        => kind switch
        {
            BoxStationPlumbingKind.Heater => (NeutralThermalKelvin, 573.15f),
            BoxStationPlumbingKind.Freezer => (73.15f, NeutralThermalKelvin),
            _ => (0, 0)
        };

    internal static bool ValidThermalTarget(BoxStationPlumbingKind kind, float kelvin)
    {
        var range = ThermalRange(kind);
        return IsThermal(kind) && float.IsFinite(kelvin) && kelvin >= range.Minimum && kelvin <= range.Maximum;
    }

    internal bool SetThermalTarget(int index, float kelvin)
    {
        if (index < 0 || index >= Devices.Length || !ValidThermalTarget(Devices[index].Kind, kelvin)
            || _thermalTargetKelvin[index] == kelvin) return false;
        _thermalTargetKelvin[index] = kelvin;
        ClearThermalWork(index);
        return true;
    }

    private void ClearThermalWork(int index)
    {
        _electricWatts[index] = 0;
        _thermalWatts[index] = 0;
        _thermalPowerLimited[index] = false;
    }

    private BoxStationPlumbingState ThermalState(int index, BoxStationPlumbingState state, BoxStationRooms rooms)
    {
        var kind = Devices[index].Kind;
        if (!IsThermal(kind)) return state;
        var flow = _definitions.Flow[index];
        var gas = _networks[flow.Input];
        var range = ThermalRange(kind);
        int node = rooms.NodeAtCell(Devices[index].Cell);
        bool sink = kind == BoxStationPlumbingKind.Freezer && node >= 0 && HeatingHeadroom(rooms.Gas(node)) > 0;
        // Supply, sink and pipe gas can change after this machine's turn. Work
        // telemetry describes that completed turn, not a promise of future flow.
        bool active = _electricWatts[index] > 0 && _thermalWatts[index] != 0;
        return state with
        {
            ThermalTargetKelvin = _thermalTargetKelvin[index], ThermalMinimumKelvin = range.Minimum,
            ThermalMaximumKelvin = range.Maximum, ThermalCurrentKelvin = gas.HeatCapacity > 0 ? (float)gas.TemperatureKelvin : 0,
            ElectricWatts = active ? _electricWatts[index] : 0, ThermalWatts = active ? _thermalWatts[index] : 0,
            ThermalActive = active, ThermalSinkAvailable = sink, ThermalPowerLimited = _thermalPowerLimited[index]
        };
    }

    private void ConditionPipe(int index, double dt, BoxStationRooms rooms, BoxStationPower power)
    {
        var device = Devices[index];
        var flow = _definitions.Flow[index];
        var pipe = _networks[flow.Input];
        double capacity = pipe.HeatCapacity;
        if (capacity <= 0 || !WithinBounds(pipe)) return;
        bool heater = device.Kind == BoxStationPlumbingKind.Heater;
        double delta = _thermalTargetKelvin[index] - pipe.TemperatureKelvin;
        if (heater ? delta <= 0 : delta >= 0) return;

        // The source's replenished reservoir acts on its 200 L port, not the
        // entire lumped network. Exponentiation preserves the uncapped response
        // across smaller steps; electrical and sink limits depend on step state.
        double localCapacity = capacity * Math.Min(1, flow.PortVolume / pipe.Volume);
        double response = localCapacity / capacity * ThermalReservoirCapacity / (localCapacity + ThermalReservoirCapacity);
        double heat = capacity * Math.Abs(delta) * (1 - Math.Pow(1 - response, dt / SourceInterval));
        if (heater)
        {
            double requested = Math.Min(heat, Math.Min(ThermalRatedWatts * dt, HeatingHeadroom(pipe)));
            if (requested <= 0) return;
            var changed = pipe;
            if (!changed.TryAddThermalEnergy(requested) || !WithinBounds(changed)) return;
            double paid = power.Draw(device.Area, flow.Channel, requested);
            _thermalPowerLimited[index] = paid < requested;
            if (paid <= 0) return;
            if (paid < requested)
            {
                changed = pipe;
                if (!changed.TryAddThermalEnergy(paid))
                    throw new InvalidOperationException("Station heater rejected prevalidated thermal energy.");
            }
            _networks[flow.Input] = changed;
            _electricWatts[index] = _thermalWatts[index] = (float)(paid / dt);
            return;
        }

        int node = rooms.NodeAtCell(device.Cell);
        if (node < 0) return;
        var room = rooms.Gas(node);
        double sinkHeadroom = HeatingHeadroom(room);
        if (sinkHeadroom <= 0) return;
        // Bound efficiency over the whole proposed exchange: using only the
        // initial temperatures would undercharge large cold-side temperature drops.
        double coldBound = Math.Max(_thermalTargetKelvin[index], pipe.TemperatureKelvin - heat / capacity);
        double hotBound = room.TemperatureKelvin
            + Math.Min(sinkHeadroom, heat + ThermalRatedWatts * dt) / room.HeatCapacity;
        double cop = Math.Min(MaximumFreezerCop, FreezerEfficiency * coldBound
            / Math.Max(MinimumFreezerLiftKelvin, hotBound - coldBound));
        double requestedElectric = Math.Min(heat / cop, Math.Min(ThermalRatedWatts * dt, sinkHeadroom / (cop + 1)));
        if (requestedElectric <= 0) return;
        var changedPipe = pipe;
        var changedRoom = room;
        if (!changedPipe.TryAddThermalEnergy(-requestedElectric * cop) || !WithinBounds(changedPipe)
            || !changedRoom.TryAddThermalEnergy(requestedElectric * (cop + 1)) || !WithinBounds(changedRoom)) return;

        // A freezer rejects both the extracted pipe heat and all paid electrical
        // work into occupied room gas. Vacuum has no finite heat sink here.
        double supplied = power.Draw(device.Area, flow.Channel, requestedElectric);
        _thermalPowerLimited[index] = supplied < requestedElectric;
        if (supplied <= 0) return;
        if (supplied < requestedElectric)
        {
            changedPipe = pipe;
            changedRoom = room;
            if (!changedPipe.TryAddThermalEnergy(-supplied * cop) || !changedRoom.TryAddThermalEnergy(supplied * (cop + 1)))
                throw new InvalidOperationException("Station freezer rejected prevalidated thermal exchange.");
        }
        _networks[flow.Input] = changedPipe;
        rooms.Gas(node) = changedRoom;
        _electricWatts[index] = (float)(supplied / dt);
        _thermalWatts[index] = (float)(-supplied * cop / dt);
    }

    private static double HeatingHeadroom(in StationGas gas)
    {
        if (gas.HeatCapacity <= 0 || !WithinBounds(gas)) return 0;
        double limit = Math.Min(StationGas.MaximumTemperature,
            BoxStationSystems.MaximumPressure * gas.Volume / (gas.Moles * StationGas.GasConstant));
        // Leave roundoff room before paying for work at a pressure/temperature cap.
        return Math.Max(0, gas.HeatCapacity * limit - gas.ThermalJoules) * (1 - 1e-12);
    }

    private static bool ValidThermalState(BoxStationPlumbingKind kind, BoxStationPlumbingState state)
    {
        if (!IsThermal(kind))
            return state.ThermalTargetKelvin == 0 && state.ThermalMinimumKelvin == 0 && state.ThermalMaximumKelvin == 0
                && state.ThermalCurrentKelvin == 0 && state.ElectricWatts == 0 && state.ThermalWatts == 0
                && !state.ThermalActive && !state.ThermalSinkAvailable && !state.ThermalPowerLimited;
        var range = ThermalRange(kind);
        if (!ValidThermalTarget(kind, state.ThermalTargetKelvin) || state.ThermalMinimumKelvin != range.Minimum
            || state.ThermalMaximumKelvin != range.Maximum || !float.IsFinite(state.ThermalCurrentKelvin)
            || state.ThermalCurrentKelvin < 0 || state.ThermalCurrentKelvin > StationGas.MaximumTemperature
            || !float.IsFinite(state.ElectricWatts) || state.ElectricWatts < 0 || state.ElectricWatts > ThermalRatedWatts
            || !float.IsFinite(state.ThermalWatts)) return false;
        if (kind == BoxStationPlumbingKind.Heater)
        {
            if (state.ThermalSinkAvailable || state.ThermalWatts != state.ElectricWatts) return false;
        }
        else if (state.ThermalWatts > 0 || -state.ThermalWatts > state.ElectricWatts * MaximumFreezerCop) return false;
        if (state.ThermalPowerLimited && (!state.Enabled || !state.Connected)) return false;
        if (!state.ThermalActive) return state.ElectricWatts == 0 && state.ThermalWatts == 0;
        return state.Enabled && state.Connected && state.ElectricWatts > 0 && state.ThermalWatts != 0;
    }
}
