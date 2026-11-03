// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Text.Json.Serialization;

namespace Modstoke.Client;

internal enum BoxStationPlumbingKind : byte { Valve, Pump, Vent, Scrubber, Filter, Mixer, Heater, Freezer, Injector }

internal readonly record struct BoxStationPlumbingDevice(int Object, int Cell, int Area,
    int Direction, int Layer, BoxStationPlumbingKind Kind, bool RequiresPower, bool Flipped = false);

internal readonly record struct BoxStationRadiator(int Object, int Cell, int Network, int Direction, int Layer);

internal readonly record struct BoxStationPlumbingState(float InputPressure, float OutputPressure,
    bool Enabled, bool Powered, float FilteredPressure = 0, StationGasSpecies FilterSpecies = 0,
    ushort FilterLitresPerSecond = 0, bool Connected = true, float SecondaryInputPressure = 0,
    ushort MixerFirstBasisPoints = 0, float MixerTargetPressure = 0,
    byte VentChecks = 0, float VentExternalPressure = 0, bool Scrubbing = false,
    bool WideNet = false, StationGasSpecies ScrubSpecies = 0,
    float ThermalTargetKelvin = 0, float ThermalMinimumKelvin = 0, float ThermalMaximumKelvin = 0,
    float ThermalCurrentKelvin = 0, float ElectricWatts = 0, float ThermalWatts = 0,
    bool ThermalActive = false, bool ThermalSinkAvailable = false, bool ThermalPowerLimited = false,
    ushort InjectorLitresPerSecond = 0, bool InjectorPulsing = false);

internal sealed partial class BoxStationPlumbing
{
    private const double SourceInterval = .5, Atmosphere = 101.325;
    private const double TransportWatts = 60, TransportPortVolume = .2;
    internal const double FilterOutputPressure = 4500;
    internal const float MaximumMixerPressure = 4500;
    // Source MAX_TRANSFER_RATE is 200 litres per 0.5-second atmos step.
    internal const ushort MaximumFilterLitresPerSecond = 400;
    private readonly BoxStationLayout _layout;
    private readonly Definitions _definitions;
    private readonly StationGas[] _networks;
    private readonly bool[] _enabled;
    private readonly StationGasSpecies[] _filterSpecies;
    private readonly ushort[] _filterLitresPerSecond;
    private readonly ushort[] _mixerFirstBasisPoints;
    private readonly float[] _mixerTargetPressure;
    private readonly byte[] _scrubberNeighbors;
    private BoxStationRooms? _scrubberRooms;
    private long _scrubberTopologyRevision;

    internal ImmutableArray<BoxStationPlumbingDevice> Devices => _definitions.Devices;
    internal ImmutableArray<BoxStationRadiator> Radiators => _definitions.Radiators;
    internal int NetworkCount => _networks.Length;

    internal BoxStationPlumbing(BoxStationLayout layout, bool seedSource)
    {
        ArgumentNullException.ThrowIfNull(layout);
        _layout = layout;
        _definitions = LayoutDefinitions.GetValue(layout, BuildDefinitions);
        _networks = new StationGas[_definitions.Volumes.Length];
        _enabled = (bool[])_definitions.InitialEnabled.Clone();
        _filterSpecies = new StationGasSpecies[Devices.Length];
        _filterLitresPerSecond = new ushort[Devices.Length];
        _mixerFirstBasisPoints = new ushort[Devices.Length];
        _mixerTargetPressure = new float[Devices.Length];
        _scrubberNeighbors = new byte[Devices.Length];
        _injectorLitresPerSecond = new ushort[Devices.Length];
        _injectorPulseRemaining = new double[Devices.Length];
        _ventChecks = new byte[Devices.Length];
        _ventExternalPressure = new float[Devices.Length];
        _scrubbing = new bool[Devices.Length];
        _wideNet = new bool[Devices.Length];
        _scrubSpecies = new StationGasSpecies[Devices.Length];
        _thermalTargetKelvin = new float[Devices.Length];
        _electricWatts = new float[Devices.Length];
        _thermalWatts = new float[Devices.Length];
        _thermalPowerLimited = new bool[Devices.Length];
        _radiatorKelvin = new double[Radiators.Length];
        Array.Fill(_radiatorKelvin, StationGas.ReferenceTemperature);
        _radiatorGasWatts = new float[Radiators.Length];
        _radiatorExternalWatts = new float[Radiators.Length];
        _radiatorSolarWatts = new float[Radiators.Length];
        _radiatorEnvironmentAvailable = new bool[Radiators.Length];
        for (int i = 0; i < Devices.Length; i++)
        {
            _filterSpecies[i] = _definitions.Flow[i].FilterSpecies;
            _filterLitresPerSecond[i] = _definitions.Flow[i].FilterLitresPerSecond;
            _mixerFirstBasisPoints[i] = _definitions.Flow[i].MixerFirstBasisPoints;
            if (Devices[i].Kind == BoxStationPlumbingKind.Mixer)
                _mixerTargetPressure[i] = (float)_definitions.Flow[i].TargetPressure;
            SetInitialAirControl(i, _ventChecks, _ventExternalPressure, _scrubbing, _scrubSpecies);
            _thermalTargetKelvin[i] = _definitions.Flow[i].ThermalTargetKelvin;
            _injectorLitresPerSecond[i] = _definitions.Flow[i].InjectorLitresPerSecond;
        }
        for (int i = 0; i < _networks.Length; i++) _networks[i] = StationGas.Vacuum(_definitions.Volumes[i]);
        if (!seedSource) return;
        foreach (var tank in _definitions.Tanks)
        {
            // Source litres become m³. Use the simulation's R so the initial
            // 25-atmosphere tank pressure is exact under the local SI model.
            double moles = 25 * Atmosphere * tank.Volume / (StationGas.GasConstant * StationGas.ReferenceTemperature);
            double oxygen = tank.Plasma ? 0 : moles * .2;
            double nitrogen = tank.Plasma ? 0 : moles * .8;
            double plasma = tank.Plasma ? moles : 0;
            double energy = (oxygen * 20 + nitrogen * 20 + plasma * 200) * StationGas.ReferenceTemperature;
            if (!StationGas.TryRestore(new[] { oxygen, nitrogen, 0, plasma, 0, energy }, tank.Volume,
                BoxStationSystems.MaximumPressure, tank.Volume * BoxStationSystems.MaximumMolesPerCubicMeter, out var gas))
                throw new InvalidDataException("Station pressure tank has invalid initial gas.");
            gas.TransferTo(ref _networks[tank.Network], gas.Moles);
        }
    }

    internal bool Toggle(int index, BoxStationPower power)
    {
        if (index < 0 || index >= Devices.Length) return false;
        var device = Devices[index];
        if (device.Kind == BoxStationPlumbingKind.Valve && device.RequiresPower
            && !power.GetPowered(device.Area, _definitions.Flow[index].Channel)) return false;
        _enabled[index] = !_enabled[index];
        ClearThermalWork(index);
        return true;
    }

    internal bool SetFilterSpecies(int index, StationGasSpecies species)
    {
        if (index < 0 || index >= Devices.Length || Devices[index].Kind != BoxStationPlumbingKind.Filter
            || !ValidFilterSpecies(species) || _filterSpecies[index] == species) return false;
        _filterSpecies[index] = species;
        return true;
    }

    internal bool SetFilterRate(int index, ushort litresPerSecond)
    {
        if (index < 0 || index >= Devices.Length || Devices[index].Kind != BoxStationPlumbingKind.Filter
            || litresPerSecond > MaximumFilterLitresPerSecond || _filterLitresPerSecond[index] == litresPerSecond) return false;
        _filterLitresPerSecond[index] = litresPerSecond;
        return true;
    }

    internal static bool ValidFilterSpecies(StationGasSpecies species)
        => species is 0 or StationGasSpecies.Oxygen or StationGasSpecies.Nitrogen
            or StationGasSpecies.CO2 or StationGasSpecies.Plasma or StationGasSpecies.N2O
            or StationGasSpecies.WaterVapor or StationGasSpecies.Tritium or StationGasSpecies.BZ;

    internal bool SetMixerRatio(int index, ushort firstBasisPoints)
    {
        if (index < 0 || index >= Devices.Length || Devices[index].Kind != BoxStationPlumbingKind.Mixer
            || firstBasisPoints > 10_000 || _mixerFirstBasisPoints[index] == firstBasisPoints) return false;
        _mixerFirstBasisPoints[index] = firstBasisPoints;
        return true;
    }

    internal bool SetMixerPressure(int index, float pressure)
    {
        if (index < 0 || index >= Devices.Length || Devices[index].Kind != BoxStationPlumbingKind.Mixer
            || !float.IsFinite(pressure) || pressure < 0 || pressure > MaximumMixerPressure
            || _mixerTargetPressure[index] == pressure) return false;
        _mixerTargetPressure[index] = pressure;
        return true;
    }

    internal ImmutableArray<BoxStationPlumbingState> Snapshot(BoxStationRooms rooms, BoxStationPower power)
    {
        CheckRooms(rooms);
        var states = ImmutableArray.CreateBuilder<BoxStationPlumbingState>(Devices.Length);
        for (int i = 0; i < Devices.Length; i++)
        {
            var flow = _definitions.Flow[i];
            int node = rooms.NodeAtCell(Devices[i].Cell);
            double room = node >= 0 ? rooms.Gas(node).Pressure : 0;
            double input = flow.Input >= 0 ? _networks[flow.Input].Pressure : room;
            double output = flow.Output >= 0 ? _networks[flow.Output].Pressure : room;
            double third = flow.Third >= 0 ? _networks[flow.Third].Pressure : 0;
            bool mixer = Devices[i].Kind == BoxStationPlumbingKind.Mixer;
            double filtered = mixer ? 0 : third;
            bool powered = Powered(i, power);
            var state = new BoxStationPlumbingState((float)input, (float)output, _enabled[i], powered, (float)filtered,
                _filterSpecies[i], _filterLitresPerSecond[i], flow.Connected, mixer ? (float)third : 0,
                _mixerFirstBasisPoints[i], _mixerTargetPressure[i], _ventChecks[i], _ventExternalPressure[i],
                _scrubbing[i], _wideNet[i], _scrubSpecies[i],
                InjectorLitresPerSecond: _injectorLitresPerSecond[i], InjectorPulsing: _injectorPulseRemaining[i] > 0);
            states.Add(ThermalState(i, state, rooms));
        }
        return states.MoveToImmutable();
    }

    // Must execute between Power.BeginStep and EndStep. Continuous flow is
    // calibrated to the source's 0.5-second atmos process, with finite port samples.
    internal void Step(double dt, BoxStationRooms rooms, BoxStationPower power)
    {
        CheckRooms(rooms);
        if (!double.IsFinite(dt) || dt < 0 || dt > 1) throw new ArgumentOutOfRangeException(nameof(dt));
        if (dt == 0) return;
        RefreshScrubberNeighbors(rooms);
        double fraction = dt / SourceInterval;
        for (int i = 0; i < Devices.Length; i++)
        {
            ClearThermalWork(i);
            _injectorPulseRemaining[i] = Math.Max(0, _injectorPulseRemaining[i] - dt);
            if (!_enabled[i]) continue;
            var device = Devices[i];
            var flow = _definitions.Flow[i];
            if (device.Kind == BoxStationPlumbingKind.Valve)
            {
                // An open digital valve remains an open pipe if its controls lose power.
                if (flow.Input == flow.Output) continue;
                var first = _networks[flow.Input];
                var second = _networks[flow.Output];
                StationGas.Equalize(ref first, ref second, 1 - Math.Exp(-fraction));
                if (!WithinBounds(first) || !WithinBounds(second)) continue;
                _networks[flow.Input] = first;
                _networks[flow.Output] = second;
                continue;
            }
            if (IsThermal(device.Kind))
            {
                if (flow.Connected && !flow.Welded) ConditionPipe(i, dt, rooms, power);
                continue;
            }
            if (!Powered(i, power) || flow.Welded) continue;
            if (!flow.Connected)
            {
                if (device.Kind is not (BoxStationPlumbingKind.Filter or BoxStationPlumbingKind.Mixer or BoxStationPlumbingKind.Injector)) _enabled[i] = false;
                continue;
            }
            if (device.Kind == BoxStationPlumbingKind.Injector)
            {
                Inject(i, dt, rooms);
                continue;
            }
            // Match the siphoning scrubber's nominal drive; high-volume vents
            // draw proportionally more instead of moving extra gas for free.
            double watts = TransportWatts * flow.PortVolume / TransportPortVolume;
            int neighbors = 0;
            if (device.Kind == BoxStationPlumbingKind.Scrubber)
            {
                neighbors = _wideNet[i] ? _scrubberNeighbors[i] : 0;
                watts = _scrubbing[i] ? 10 + 10 * SelectedSpeciesCount(_scrubSpecies[i]) : TransportWatts;
                watts *= 1 + neighbors * neighbors / 2d;
            }
            double seconds = power.Draw(device.Area, flow.Channel, watts * dt) / watts;
            if (seconds <= 0) continue;
            double poweredFraction = seconds / SourceInterval;
            if (device.Kind == BoxStationPlumbingKind.Pump)
            {
                Pump(flow, poweredFraction);
            }
            else if (device.Kind == BoxStationPlumbingKind.Vent)
            {
                Vent(device.Cell, flow with { PressureChecks = _ventChecks[i], ExternalBound = _ventExternalPressure[i] }, poweredFraction, rooms);
            }
            else if (device.Kind == BoxStationPlumbingKind.Scrubber)
            {
                Scrub(device.Cell, flow, _scrubbing[i] ? _scrubSpecies[i] : StationGasSpecies.All,
                    neighbors + 1, poweredFraction, rooms);
            }
            else if (device.Kind == BoxStationPlumbingKind.Filter)
            {
                Filter(flow, _filterSpecies[i], _filterLitresPerSecond[i], seconds);
            }
            else if (device.Kind == BoxStationPlumbingKind.Mixer)
            {
                Mix(flow, _mixerFirstBasisPoints[i], _mixerTargetPressure[i], poweredFraction);
            }
        }
        if (dt <= .1) ReactPipes(dt);
        else
        {
            int reactionSteps = (int)Math.Ceiling(dt / .1);
            double reactionDt = dt / reactionSteps;
            for (int step = 0; step < reactionSteps; step++) ReactPipes(reactionDt);
        }
    }

    private void ReactPipes(double dt)
    {
        for (int network = 0; network < _networks.Length; network++) _networks[network].React(dt);
    }

    private void Pump(FlowDefinition flow, double fraction)
    {
        if (flow.Input == flow.Output) return;
        ref var input = ref _networks[flow.Input];
        ref var output = ref _networks[flow.Output];
        double delta = flow.TargetPressure - output.Pressure;
        if (delta <= 0 || input.Moles <= 0) return;
        double requested = delta * flow.PortVolume / (StationGas.GasConstant * input.TemperatureKelvin) * fraction;
        requested = Math.Min(requested, SampleMoles(input, flow.PortVolume, fraction));
        Transfer(ref input, ref output, requested, flow.TargetPressure);
    }

    private void Vent(int cell, FlowDefinition flow, double fraction, BoxStationRooms rooms)
    {
        int node = rooms.NodeAtCell(cell);
        int network = flow.Releasing ? flow.Input : flow.Output;
        ref var pipe = ref _networks[network];
        double roomPressure = node >= 0 ? rooms.Gas(node).Pressure : 0;
        double delta = 10_000;
        if (flow.Releasing)
        {
            if ((flow.PressureChecks & 1) != 0) delta = Math.Min(delta, flow.ExternalBound - roomPressure);
            if ((flow.PressureChecks & 2) != 0) delta = Math.Min(delta, pipe.Pressure - flow.InternalBound);
            if (delta <= 0 || pipe.Moles <= 0) return;
            double requested = delta * StationGas.SourceCellVolume / (StationGas.GasConstant * pipe.TemperatureKelvin) * fraction;
            requested = Math.Min(requested, SampleMoles(pipe, flow.PortVolume, fraction));
            if ((flow.PressureChecks & 2) != 0)
                requested = Math.Min(requested, pipe.Moles * Math.Max(0, 1 - flow.InternalBound / pipe.Pressure));
            if (node < 0) pipe.Exhaust(requested);
            else Transfer(ref pipe, ref rooms.Gas(node), requested,
                (flow.PressureChecks & 1) != 0 ? flow.ExternalBound : BoxStationSystems.MaximumPressure);
        }
        else
        {
            if (node < 0) return;
            ref var room = ref rooms.Gas(node);
            if ((flow.PressureChecks & 1) != 0) delta = Math.Min(delta, roomPressure - flow.ExternalBound);
            if ((flow.PressureChecks & 2) != 0) delta = Math.Min(delta, flow.InternalBound - pipe.Pressure);
            if (delta <= 0 || room.Moles <= 0) return;
            double requested = delta * flow.PortVolume / (StationGas.GasConstant * room.TemperatureKelvin) * fraction;
            requested = Math.Min(requested, SampleMoles(room, StationGas.SourceCellVolume, fraction));
            if ((flow.PressureChecks & 1) != 0)
                requested = Math.Min(requested, room.Moles * Math.Max(0, 1 - flow.ExternalBound / roomPressure));
            Transfer(ref room, ref pipe, requested,
                (flow.PressureChecks & 2) != 0 ? flow.InternalBound : BoxStationSystems.MaximumPressure);
        }
    }

    private void Scrub(int cell, FlowDefinition flow, StationGasSpecies species, int tiles, double fraction, BoxStationRooms rooms)
    {
        int node = rooms.NodeAtCell(cell);
        if (node < 0) return;
        ref var room = ref rooms.Gas(node);
        ref var pipe = ref _networks[flow.Output];
        if (room.Moles <= 0 || pipe.Pressure >= 50 * Atmosphere) return;
        // A homogeneous compartment supplies one finite sample per reachable turf.
        // The shared donor and receiver bounds prevent duplicated gas or energy.
        double volume = Math.Min(StationGas.SourceCellVolume, flow.SampleVolume * fraction) * tiles;
        double requested = room.SelectedMoles(species) * Math.Min(1, volume / room.Volume);
        requested = Math.Min(requested, room.TransferMolesToPressure(pipe, 50 * Atmosphere, species));
        requested = Math.Min(requested, CapacityMoles(pipe));
        room.TransferSelectedTo(ref pipe, requested, species);
    }

    private void Filter(FlowDefinition flow, StationGasSpecies species, ushort litresPerSecond, double dt)
    {
        ref var input = ref _networks[flow.Input];
        ref var output = ref _networks[flow.Output];
        if (input.Moles <= 0 || litresPerSecond == 0 || output.Pressure >= FilterOutputPressure) return;
        double sample = Math.Min(1, dt * litresPerSecond / 1000 / input.Volume);
        if (flow.Third == flow.Output)
        {
            // A common outlet receives one unsplit sample, sharing its pressure
            // capacity instead of granting both streams the same free space.
            if (flow.Input != flow.Output)
                Transfer(ref input, ref output, input.Moles * sample, FilterOutputPressure);
            return;
        }

        var remainderSpecies = StationGasSpecies.All & ~species;
        double selected = input.SelectedMoles(species) * sample;
        double remainder = input.SelectedMoles(remainderSpecies) * sample;
        // Both quantities use the original mixture. A port returning to the
        // input is a no-op; rejected species remain there without resampling.
        if (flow.Input != flow.Third)
            TransferFiltered(ref input, ref _networks[flow.Third], selected, species);
        if (flow.Input != flow.Output)
            TransferFiltered(ref input, ref output, remainder, remainderSpecies);
    }

    private static void TransferFiltered(ref StationGas donor, ref StationGas receiver,
        double requested, StationGasSpecies species)
    {
        requested = Math.Min(requested, donor.TransferMolesToPressure(receiver, FilterOutputPressure, species));
        requested = Math.Min(requested, CapacityMoles(receiver));
        donor.TransferSelectedTo(ref receiver, requested, species);
    }

    private void Mix(FlowDefinition flow, ushort firstBasisPoints, float pressure, double fraction)
    {
        var output = _networks[flow.Output];
        if (output.Pressure >= pressure) return;
        double firstShare = firstBasisPoints / 10_000d, secondShare = 1 - firstShare;
        // A positive-share return to the receiver cannot supply an independent
        // blend stream. Leave the entire blend unchanged rather than bias it.
        if (firstShare > 0 && flow.Input == flow.Output || secondShare > 0 && flow.Third == flow.Output) return;
        var first = _networks[flow.Input];
        if (flow.Input == flow.Third)
        {
            // Two inlets on one network sample that mixture once, sharing its gas.
            Transfer(ref first, ref output, SampleMoles(first, flow.PortVolume, fraction), pressure);
            if (!WithinBounds(first) || !WithinBounds(output)) return;
            _networks[flow.Input] = first;
            _networks[flow.Output] = output;
            return;
        }

        var second = _networks[flow.Third];
        double available = CapacityMoles(output);
        if (firstShare > 0) available = Math.Min(available, SampleMoles(first, flow.PortVolume, fraction) / firstShare);
        if (secondShare > 0) available = Math.Min(available, SampleMoles(second, flow.PortVolume, fraction) / secondShare);
        if (available <= 0) return;

        // Concentrations are molar shares. Combining candidate packets before the
        // pressure solve retains that ratio for differing temperatures and species.
        var blend = StationGas.Vacuum(output.Volume);
        var firstSample = first;
        var secondSample = second;
        firstSample.TransferTo(ref blend, available * firstShare);
        secondSample.TransferTo(ref blend, available * secondShare);
        double requested = blend.TransferMolesToPressure(output, pressure);
        if (requested <= 0) return;
        first.TransferTo(ref output, requested * firstShare);
        second.TransferTo(ref output, requested * secondShare);
        if (!WithinBounds(first) || !WithinBounds(second) || !WithinBounds(output)) return;
        if (firstShare > 0) _networks[flow.Input] = first;
        if (secondShare > 0) _networks[flow.Third] = second;
        _networks[flow.Output] = output;
    }

    private static void Transfer(ref StationGas donor, ref StationGas receiver, double requested, double pressure)
    {
        requested = Math.Min(requested, donor.TransferMolesToPressure(receiver, Math.Min(pressure, BoxStationSystems.MaximumPressure)));
        requested = Math.Min(requested, CapacityMoles(receiver));
        donor.TransferTo(ref receiver, requested);
    }

    private static double CapacityMoles(in StationGas gas)
        => Math.Max(0, gas.Volume * BoxStationSystems.MaximumMolesPerCubicMeter - gas.Moles);

    private static double SampleMoles(in StationGas gas, double volume, double fraction)
        => gas.Moles * Math.Min(1, volume * fraction / gas.Volume);

    private bool Powered(int index, BoxStationPower power)
        => !Devices[index].RequiresPower || power.GetPowered(Devices[index].Area, _definitions.Flow[index].Channel);

    private void CheckRooms(BoxStationRooms rooms)
    {
        ArgumentNullException.ThrowIfNull(rooms);
        if (!ReferenceEquals(rooms.Layout, _layout))
            throw new ArgumentException("Expected rooms for this station layout.", nameof(rooms));
    }

    private static bool WithinBounds(in StationGas gas)
        => gas.Pressure <= BoxStationSystems.MaximumPressure
            && gas.Moles <= gas.Volume * BoxStationSystems.MaximumMolesPerCubicMeter
            && gas.TemperatureKelvin <= StationGas.MaximumTemperature;

    internal static bool ValidState(BoxStationLayout layout, ImmutableArray<BoxStationPlumbingState> states)
    {
        var definitions = LayoutDefinitions.GetValue(layout, BuildDefinitions);
        var devices = definitions.Devices;
        if (states.IsDefault || states.Length != devices.Length) return false;
        for (int i = 0; i < states.Length; i++)
        {
            var state = states[i];
            if (!float.IsFinite(state.InputPressure) || state.InputPressure < 0 || state.InputPressure > BoxStationSystems.MaximumPressure
                || !float.IsFinite(state.OutputPressure) || state.OutputPressure < 0 || state.OutputPressure > BoxStationSystems.MaximumPressure
                || !devices[i].RequiresPower && !state.Powered || state.Connected != definitions.Flow[i].Connected) return false;
            if (devices[i].Kind == BoxStationPlumbingKind.Filter)
            {
                if (!float.IsFinite(state.FilteredPressure) || state.FilteredPressure < 0
                    || state.FilteredPressure > BoxStationSystems.MaximumPressure || !ValidFilterSpecies(state.FilterSpecies)
                    || state.FilterLitresPerSecond > MaximumFilterLitresPerSecond) return false;
            }
            else if (state.FilteredPressure != 0 || state.FilterSpecies != 0 || state.FilterLitresPerSecond != 0) return false;
            if (devices[i].Kind == BoxStationPlumbingKind.Mixer)
            {
                if (!float.IsFinite(state.SecondaryInputPressure) || state.SecondaryInputPressure < 0
                    || state.SecondaryInputPressure > BoxStationSystems.MaximumPressure || state.MixerFirstBasisPoints > 10_000
                    || !float.IsFinite(state.MixerTargetPressure) || state.MixerTargetPressure < 0
                    || state.MixerTargetPressure > MaximumMixerPressure) return false;
            }
            else if (state.SecondaryInputPressure != 0 || state.MixerFirstBasisPoints != 0 || state.MixerTargetPressure != 0) return false;
            if (!ValidAirControl(devices[i].Kind, state.VentChecks, state.VentExternalPressure,
                state.Scrubbing, state.WideNet, state.ScrubSpecies)) return false;
            if (!ValidThermalState(devices[i].Kind, state)) return false;
            if (devices[i].Kind == BoxStationPlumbingKind.Injector
                ? state.InjectorLitresPerSecond > MaximumInjectorLitresPerSecond
                : state.InjectorLitresPerSecond != 0 || state.InjectorPulsing) return false;
        }
        return true;
    }

    internal BoxStationPlumbingSave Capture()
    {
        var values = new double[_networks.Length][];
        for (int i = 0; i < values.Length; i++) values[i] = _networks[i].CaptureValues();
        var objects = new int[Devices.Length];
        for (int i = 0; i < objects.Length; i++) objects[i] = Devices[i].Object;
        var radiatorObjects = new int[Radiators.Length];
        for (int i = 0; i < radiatorObjects.Length; i++) radiatorObjects[i] = Radiators[i].Object;
        return new()
        {
            Version = 8, LayoutHash = _layout.LayoutHash, NetworkPorts = (int[])_definitions.NetworkPorts.Clone(),
            Gas = values, DeviceObjects = objects, Enabled = (bool[])_enabled.Clone(),
            FilterSpecies = (StationGasSpecies[])_filterSpecies.Clone(),
            FilterLitresPerSecond = (ushort[])_filterLitresPerSecond.Clone(),
            MixerFirstBasisPoints = (ushort[])_mixerFirstBasisPoints.Clone(),
            MixerTargetPressure = (float[])_mixerTargetPressure.Clone(),
            VentChecks = (byte[])_ventChecks.Clone(), VentExternalPressure = (float[])_ventExternalPressure.Clone(),
            Scrubbing = (bool[])_scrubbing.Clone(), WideNet = (bool[])_wideNet.Clone(),
            ScrubSpecies = (StationGasSpecies[])_scrubSpecies.Clone(),
            ThermalTargetKelvin = (float[])_thermalTargetKelvin.Clone(),
            RadiatorObjects = radiatorObjects, RadiatorKelvin = (double[])_radiatorKelvin.Clone(),
            InjectorLitresPerSecond = (ushort[])_injectorLitresPerSecond.Clone(),
            InjectorPulseRemaining = (double[])_injectorPulseRemaining.Clone()
        };
    }

    internal bool TryRestore(BoxStationPlumbingSave? saved)
    {
        if (saved is null || saved.Version is < 1 or > 8 || saved.LayoutHash != _layout.LayoutHash) return false;
        var definitions = saved.Version < 8
            ? BuildDefinitions(_layout, includeFilters: saved.Version >= 2, includeMixers: saved.Version >= 3,
                includeThermal: saved.Version >= 5, includeRadiators: saved.Version >= 6, includeInjectors: false)
            : _definitions;
        if (saved.NetworkPorts?.Length != definitions.Volumes.Length || saved.Gas?.Length != definitions.Volumes.Length
            || saved.DeviceObjects?.Length != definitions.Devices.Length || saved.Enabled?.Length != definitions.Devices.Length
            || (saved.Version == 1 ? saved.FilterSpecies is not null || saved.FilterLitresPerSecond is not null
                : saved.FilterSpecies?.Length != definitions.Devices.Length || saved.FilterLitresPerSecond?.Length != definitions.Devices.Length)
            || (saved.Version < 3 ? saved.MixerFirstBasisPoints is not null || saved.MixerTargetPressure is not null
                : saved.MixerFirstBasisPoints?.Length != definitions.Devices.Length || saved.MixerTargetPressure?.Length != definitions.Devices.Length)
            || (saved.Version < 4 ? saved.VentChecks is not null || saved.VentExternalPressure is not null
                    || saved.Scrubbing is not null || saved.WideNet is not null || saved.ScrubSpecies is not null
                : saved.VentChecks?.Length != definitions.Devices.Length || saved.VentExternalPressure?.Length != definitions.Devices.Length
                    || saved.Scrubbing?.Length != definitions.Devices.Length || saved.WideNet?.Length != definitions.Devices.Length
                    || saved.ScrubSpecies?.Length != definitions.Devices.Length)
            || (saved.Version < 5 ? saved.ThermalTargetKelvin is not null
                : saved.ThermalTargetKelvin?.Length != definitions.Devices.Length)
            || (saved.Version < 6 ? saved.RadiatorObjects is not null || saved.RadiatorKelvin is not null
                : saved.RadiatorObjects?.Length != Radiators.Length || saved.RadiatorKelvin?.Length != Radiators.Length)
            || (saved.Version < 8 ? saved.InjectorLitresPerSecond is not null || saved.InjectorPulseRemaining is not null
                : saved.InjectorLitresPerSecond?.Length != definitions.Devices.Length
                    || saved.InjectorPulseRemaining?.Length != definitions.Devices.Length)) return false;
        var gases = new StationGas[definitions.Volumes.Length];
        for (int i = 0; i < gases.Length; i++)
            if (saved.NetworkPorts[i] != definitions.NetworkPorts[i]
                || saved.Gas[i]?.Length != (saved.Version >= 7 ? 9 : 6)
                || !StationGas.TryRestore(saved.Gas[i], definitions.Volumes[i], BoxStationSystems.MaximumPressure,
                    definitions.Volumes[i] * BoxStationSystems.MaximumMolesPerCubicMeter, out gases[i])
                || gases[i].TemperatureKelvin > StationGas.MaximumTemperature) return false;
        for (int i = 0; i < definitions.Devices.Length; i++)
            if (saved.DeviceObjects[i] != definitions.Devices[i].Object) return false;
        var radiatorKelvin = new double[Radiators.Length];
        Array.Fill(radiatorKelvin, StationGas.ReferenceTemperature);
        if (saved.Version >= 6)
        {
            for (int i = 0; i < Radiators.Length; i++)
            {
                double kelvin = saved.RadiatorKelvin![i];
                if (saved.RadiatorObjects![i] != Radiators[i].Object || !double.IsFinite(kelvin)
                    || kelvin < 2.7 || kelvin > MaximumRadiatorKelvin) return false;
                radiatorKelvin[i] = kelvin;
            }
        }

        var enabled = (bool[])_definitions.InitialEnabled.Clone();
        var species = new StationGasSpecies[Devices.Length];
        var rates = new ushort[Devices.Length];
        var ratios = new ushort[Devices.Length];
        var pressures = new float[Devices.Length];
        var ventChecks = new byte[Devices.Length];
        var ventPressure = new float[Devices.Length];
        var scrubbing = new bool[Devices.Length];
        var wideNet = new bool[Devices.Length];
        var scrubSpecies = new StationGasSpecies[Devices.Length];
        var thermalTargets = new float[Devices.Length];
        var injectorRates = new ushort[Devices.Length];
        var injectorPulses = new double[Devices.Length];
        int savedIndex = 0;
        for (int i = 0; i < Devices.Length; i++)
        {
            bool filter = Devices[i].Kind == BoxStationPlumbingKind.Filter;
            bool mixer = Devices[i].Kind == BoxStationPlumbingKind.Mixer;
            species[i] = _definitions.Flow[i].FilterSpecies;
            rates[i] = _definitions.Flow[i].FilterLitresPerSecond;
            ratios[i] = _definitions.Flow[i].MixerFirstBasisPoints;
            pressures[i] = mixer ? (float)_definitions.Flow[i].TargetPressure : 0;
            SetInitialAirControl(i, ventChecks, ventPressure, scrubbing, scrubSpecies);
            thermalTargets[i] = _definitions.Flow[i].ThermalTargetKelvin;
            injectorRates[i] = _definitions.Flow[i].InjectorLitresPerSecond;
            if (saved.Version == 1 && filter || saved.Version < 3 && mixer
                || saved.Version < 5 && IsThermal(Devices[i].Kind)
                || saved.Version < 8 && Devices[i].Kind == BoxStationPlumbingKind.Injector) continue;
            if (savedIndex >= saved.DeviceObjects.Length || saved.DeviceObjects[savedIndex] != Devices[i].Object) return false;
            enabled[i] = saved.Enabled[savedIndex];
            if (saved.Version >= 2)
            {
                species[i] = saved.FilterSpecies![savedIndex];
                rates[i] = saved.FilterLitresPerSecond![savedIndex];
                if (filter ? !ValidFilterSpecies(species[i]) || rates[i] > MaximumFilterLitresPerSecond
                    : species[i] != 0 || rates[i] != 0) return false;
            }
            if (saved.Version >= 3)
            {
                ratios[i] = saved.MixerFirstBasisPoints![savedIndex];
                pressures[i] = saved.MixerTargetPressure![savedIndex];
                if (mixer ? ratios[i] > 10_000 || !float.IsFinite(pressures[i]) || pressures[i] < 0
                    || pressures[i] > MaximumMixerPressure : ratios[i] != 0 || pressures[i] != 0) return false;
            }
            if (saved.Version >= 4)
            {
                ventChecks[i] = saved.VentChecks![savedIndex];
                ventPressure[i] = saved.VentExternalPressure![savedIndex];
                scrubbing[i] = saved.Scrubbing![savedIndex];
                wideNet[i] = saved.WideNet![savedIndex];
                scrubSpecies[i] = saved.ScrubSpecies![savedIndex];
                if (!ValidAirControl(Devices[i].Kind, ventChecks[i], ventPressure[i],
                    scrubbing[i], wideNet[i], scrubSpecies[i])) return false;
            }
            if (saved.Version >= 5)
            {
                thermalTargets[i] = saved.ThermalTargetKelvin![savedIndex];
                if (IsThermal(Devices[i].Kind) ? !ValidThermalTarget(Devices[i].Kind, thermalTargets[i])
                    : thermalTargets[i] != 0) return false;
            }
            if (saved.Version >= 8)
            {
                injectorRates[i] = saved.InjectorLitresPerSecond![savedIndex];
                injectorPulses[i] = saved.InjectorPulseRemaining![savedIndex];
                if (!double.IsFinite(injectorPulses[i]) || (Devices[i].Kind == BoxStationPlumbingKind.Injector
                    ? injectorRates[i] > MaximumInjectorLitresPerSecond || injectorPulses[i] < 0 || injectorPulses[i] > SourceInterval
                    : injectorRates[i] != 0 || injectorPulses[i] != 0)) return false;
            }
            savedIndex++;
        }
        if (savedIndex != saved.DeviceObjects.Length) return false;
        if (saved.Version < 8)
        {
            if (!TryMigrateGas(definitions, gases, out var migrated)) return false;
            gases = migrated;
        }
        gases.CopyTo(_networks, 0);
        enabled.CopyTo(_enabled, 0);
        species.CopyTo(_filterSpecies, 0);
        rates.CopyTo(_filterLitresPerSecond, 0);
        ratios.CopyTo(_mixerFirstBasisPoints, 0);
        pressures.CopyTo(_mixerTargetPressure, 0);
        ventChecks.CopyTo(_ventChecks, 0);
        ventPressure.CopyTo(_ventExternalPressure, 0);
        scrubbing.CopyTo(_scrubbing, 0);
        wideNet.CopyTo(_wideNet, 0);
        scrubSpecies.CopyTo(_scrubSpecies, 0);
        thermalTargets.CopyTo(_thermalTargetKelvin, 0);
        injectorRates.CopyTo(_injectorLitresPerSecond, 0);
        injectorPulses.CopyTo(_injectorPulseRemaining, 0);
        Array.Clear(_electricWatts);
        Array.Clear(_thermalWatts);
        Array.Clear(_thermalPowerLimited);
        radiatorKelvin.CopyTo(_radiatorKelvin, 0);
        Array.Clear(_radiatorGasWatts);
        Array.Clear(_radiatorExternalWatts);
        Array.Clear(_radiatorSolarWatts);
        Array.Clear(_radiatorEnvironmentAvailable);
        return true;
    }

    private bool TryMigrateGas(Definitions legacy, StationGas[] gases, out StationGas[] migrated)
    {
        migrated = new StationGas[_networks.Length];
        for (int i = 0; i < migrated.Length; i++) migrated[i] = StationGas.Vacuum(_definitions.Volumes[i]);
        var currentNodes = new Dictionary<int, int>();
        for (int i = 0; i < _definitions.Nodes.Length; i++) currentNodes.Add(_definitions.Nodes[i].Port, i);
        var shares = new Dictionary<int, double>[gases.Length];
        for (int i = 0; i < shares.Length; i++) shares[i] = new();
        for (int i = 0; i < legacy.Nodes.Length; i++)
        {
            var node = legacy.Nodes[i];
            if (!currentNodes.TryGetValue(node.Port, out int current) || _definitions.Nodes[current] != node) return false;
            int network = _definitions.NodeNetworks[current];
            var volumes = shares[legacy.NodeNetworks[i]];
            volumes.TryGetValue(network, out double volume);
            volumes[network] = volume + node.Volume;
        }

        // Added endpoints can merge networks or isolate previously unambiguous
        // ports. Each old network was homogeneous, so split its species and energy
        // by the old volumes retained in each new network. New port volume is vacuum.
        for (int i = 0; i < gases.Length; i++)
        {
            var donor = gases[i];
            double remainingVolume = 0;
            foreach (double volume in shares[i].Values) remainingVolume += volume;
            int remainingShares = shares[i].Count;
            if (remainingShares == 0) return false;
            foreach (var share in shares[i])
            {
                double requested = --remainingShares == 0 ? donor.Moles
                    : donor.Moles * Math.Min(1, share.Value / remainingVolume);
                donor.TransferTo(ref migrated[share.Key], requested);
                remainingVolume -= share.Value;
            }
        }
        for (int i = 0; i < migrated.Length; i++)
        {
            if (!StationGas.TryRestore(migrated[i].CaptureValues(), _definitions.Volumes[i], BoxStationSystems.MaximumPressure,
                _definitions.Volumes[i] * BoxStationSystems.MaximumMolesPerCubicMeter, out var validated)
                || validated.TemperatureKelvin > StationGas.MaximumTemperature) return false;
            migrated[i] = validated;
        }
        return true;
    }
}

internal sealed class BoxStationPlumbingSave
{
    [JsonRequired]
    public int Version { get; set; } = 8;
    [JsonRequired]
    public string? LayoutHash { get; set; }
    [JsonRequired]
    public int[]? NetworkPorts { get; set; }
    // Version 7 appends water vapor, tritium and BZ after the legacy six values.
    [JsonRequired]
    public double[][]? Gas { get; set; }
    [JsonRequired]
    public int[]? DeviceObjects { get; set; }
    [JsonRequired]
    public bool[]? Enabled { get; set; }
    // Version 2 adds filters, version 3 mixers, version 4 air controls, and version 5 thermomachines.
    // Each requires its setting arrays, with zeroes for unrelated device kinds.
    public StationGasSpecies[]? FilterSpecies { get; set; }
    public ushort[]? FilterLitresPerSecond { get; set; }
    public ushort[]? MixerFirstBasisPoints { get; set; }
    public float[]? MixerTargetPressure { get; set; }
    public byte[]? VentChecks { get; set; }
    public float[]? VentExternalPressure { get; set; }
    public bool[]? Scrubbing { get; set; }
    public bool[]? WideNet { get; set; }
    public StationGasSpecies[]? ScrubSpecies { get; set; }
    public float[]? ThermalTargetKelvin { get; set; }
    // Version 6 adds passive metal temperatures aligned by source object.
    public int[]? RadiatorObjects { get; set; }
    public double[]? RadiatorKelvin { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ushort[]? InjectorLitresPerSecond { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double[]? InjectorPulseRemaining { get; set; }
}
