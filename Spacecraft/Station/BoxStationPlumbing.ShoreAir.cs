// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Client;

internal sealed partial class BoxStationPlumbing
{
    private bool _shoreAirSourceResolved;
    private int _shoreAirSource = -1;

    // The caller holds the computer gate on the main thread inside Power.Begin/End.
    internal ShoreAirResult RefillShipAir(ref SpacecraftAtmosphere reserve, double maximumMoles,
        ref StationGas room, BoxStationPower power, int area, float seconds)
    {
        if (!float.IsFinite(seconds) || seconds <= 0) return new(ShoreAirStatus.Waiting);
        if (!reserve.TryReceive(0, 0, 0, 0, maximumMoles, out _)) return new(ShoreAirStatus.UnsafeGas);
        double headroom = maximumMoles - reserve.Moles;
        if (headroom <= maximumMoles * 1e-12) return new(ShoreAirStatus.Full);
        int network = ShoreAirSource();
        if (network < 0) return new(ShoreAirStatus.NoSupply);
        var source = _networks[network];
        if (!source.TryAddThermalEnergy(0) || !WithinBounds(source)) return new(ShoreAirStatus.UnsafeGas);
        if (source.Moles <= 0) return new(ShoreAirStatus.NoSupply);
        // The ship has only three species. Reject the whole homogeneous intake;
        // silently filtering unsupported species would discard matter and heat.
        if (source.PlasmaMoles > 0 || source.N2OMoles > 0 || source.WaterVaporMoles > 0
            || source.TritiumMoles > 0 || source.BZMoles > 0
            || source.OxygenFraction is < .19 or > .23 || source.CO2Fraction > .001
            || source.TemperatureKelvin is < 273.15 or > 313.15) return new(ShoreAirStatus.UnsafeGas);
        double available = source.Moles - SpacecraftShoreAir.MinimumIntakeKpa * source.Volume
            / (StationGas.GasConstant * source.TemperatureKelvin);
        if (available <= source.Moles * 1e-12) return new(ShoreAirStatus.NoSupply);
        double requestedMoles = Math.Min((double)SpacecraftShoreAir.MaximumMolesPerSecond * seconds,
            Math.Min(headroom, available)) * (1 - 1e-12);
        if (requestedMoles <= 0) return new(ShoreAirStatus.NoSupply);
        double sinkHeadroom = HeatingHeadroom(room);
        double idle = (double)SpacecraftShoreAir.MotorWatts * seconds;
        if (sinkHeadroom < idle) return new(ShoreAirStatus.Cooling);
        if (!TryPrepareShoreAir(source, reserve, requestedMoles, maximumMoles,
            out var changedSource, out var changedReserve, out double transferred)) return new(ShoreAirStatus.UnsafeGas);

        // Isothermal nRT ln(Pout/Pin): bound both pressures over the full packet
        // before reducing flow, so a watt, sink or partial-payment limit cannot underpay.
        double outlet = Math.Max(reserve.Moles * SpacecraftAtmosphere.GasConstant
            * reserve.TemperatureKelvin / reserve.Volume, changedReserve.Moles
            * SpacecraftAtmosphere.GasConstant * changedReserve.TemperatureKelvin / changedReserve.Volume);
        double compressionPerMole = StationGas.GasConstant * 1000 * source.TemperatureKelvin
            * Math.Log(Math.Max(1, outlet / changedSource.Pressure)) / SpacecraftShoreAir.Efficiency;
        double electricLimit = Math.Min((double)SpacecraftShoreAir.MaximumWatts * seconds, sinkHeadroom);
        if (compressionPerMole > 0)
            requestedMoles = Math.Min(requestedMoles,
                Math.Max(0, electricLimit - idle) / compressionPerMole * (1 - 1e-12));
        if (requestedMoles <= 0) return new(ShoreAirStatus.Cooling);
        double requestedElectric = idle + requestedMoles * compressionPerMole;
        var changedRoom = room;
        if (!double.IsFinite(requestedElectric) || requestedElectric > electricLimit
            || !changedRoom.TryAddThermalEnergy(requestedElectric) || !WithinBounds(changedRoom))
            return new(ShoreAirStatus.Cooling);
        if (!TryPrepareShoreAir(source, reserve, requestedMoles, maximumMoles,
            out changedSource, out changedReserve, out transferred)) return new(ShoreAirStatus.UnsafeGas);

        double paid = power.Draw(area, BoxStationPowerChannel.Equipment, requestedElectric);
        if (paid <= 0) return new(ShoreAirStatus.NoPower);
        if (paid < requestedElectric)
        {
            changedRoom = room;
            if (!changedRoom.TryAddThermalEnergy(paid) || !WithinBounds(changedRoom))
                throw new InvalidOperationException("Shore air compressor rejected prevalidated heat rejection.");
            room = changedRoom;
            if (paid < idle || paid == idle && compressionPerMole > 0)
                return new(ShoreAirStatus.NoPower, Joules: paid);
            double paidMoles = compressionPerMole > 0
                ? Math.Min(requestedMoles, (paid - idle) / compressionPerMole * (1 - 1e-12)) : requestedMoles;
            if (!TryPrepareShoreAir(source, reserve, paidMoles, maximumMoles,
                out changedSource, out changedReserve, out transferred))
                return new(ShoreAirStatus.UnsafeGas, Joules: paid);
        }

        // StationGas and SpacecraftAtmosphere use different molar heat capacities.
        // Transfer exact sensible joules; all paid motor/compression work heats the room.
        room = changedRoom;
        _networks[network] = changedSource;
        reserve = changedReserve;
        return new(maximumMoles - reserve.Moles <= maximumMoles * 1e-12
            ? ShoreAirStatus.Full : ShoreAirStatus.Filling, transferred, paid);
    }

    private int ShoreAirSource()
    {
        if (_shoreAirSourceResolved) return _shoreAirSource;
        _shoreAirSourceResolved = true;
        for (int i = 0; i < _definitions.Nodes.Length; i++)
        {
            var node = _definitions.Nodes[i];
            if (node.Port != SpacecraftShoreAir.SupplyPort || node.Cell != SpacecraftShoreAir.SupplyCell) continue;
            int network = _definitions.NodeNetworks[i];
            if (network >= 0 && network < _networks.Length) _shoreAirSource = network;
            break;
        }
        return _shoreAirSource;
    }

    private static bool TryPrepareShoreAir(in StationGas source, in SpacecraftAtmosphere reserve,
        double moles, double maximumMoles, out StationGas changedSource,
        out SpacecraftAtmosphere changedReserve, out double transferred)
    {
        changedSource = source;
        changedReserve = default;
        var packet = StationGas.Vacuum(source.Volume);
        transferred = changedSource.TransferTo(ref packet, moles);
        return transferred > 0 && changedSource.TryAddThermalEnergy(0) && WithinBounds(changedSource)
            && changedSource.Pressure >= SpacecraftShoreAir.MinimumIntakeKpa
            && reserve.TryReceive(packet.OxygenMoles, packet.NitrogenMoles, packet.CO2Moles,
                packet.ThermalJoules, maximumMoles, out changedReserve);
    }
}
