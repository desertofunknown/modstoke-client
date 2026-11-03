// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Client;

internal sealed partial class BoxStationPlumbing
{
    internal const ushort MaximumInjectorLitresPerSecond = 400;
    private readonly ushort[] _injectorLitresPerSecond;
    private readonly double[] _injectorPulseRemaining;

    internal bool SetInjectorRate(int index, ushort litresPerSecond, BoxStationPower power, out string feedback)
    {
        if (index < 0 || index >= Devices.Length || Devices[index].Kind != BoxStationPlumbingKind.Injector
            || litresPerSecond > MaximumInjectorLitresPerSecond)
        { feedback = "Invalid injector setting."; return false; }
        if (!Powered(index, power)) { feedback = "Injector control has no power."; return false; }
        if (_injectorLitresPerSecond[index] == litresPerSecond)
        { feedback = "Injector rate is unchanged."; return false; }
        _injectorLitresPerSecond[index] = litresPerSecond;
        feedback = $"Injector set to {litresPerSecond} L/s.";
        return true;
    }

    internal bool PulseInjector(int index, BoxStationRooms rooms, BoxStationPower power, out string feedback)
    {
        CheckRooms(rooms);
        if (index < 0 || index >= Devices.Length || Devices[index].Kind != BoxStationPlumbingKind.Injector)
        { feedback = "Injector is unavailable."; return false; }
        if (!Powered(index, power)) { feedback = "Injector control has no power."; return false; }
        if (!_definitions.Flow[index].Connected) { feedback = "Injector inlet is disconnected."; return false; }
        if (_enabled[index]) { feedback = "Switch off continuous injection before pulsing."; return false; }
        if (_injectorPulseRemaining[index] > 0) { feedback = "Injector pulse is in progress."; return false; }
        double released = Inject(index, SourceInterval, rooms);
        _injectorPulseRemaining[index] = SourceInterval;
        feedback = released > 0 ? "Injector pulse released gas." : "Injector pulsed; no gas transferred.";
        return true;
    }

    private double Inject(int index, double seconds, BoxStationRooms rooms)
    {
        var flow = _definitions.Flow[index];
        ref var pipe = ref _networks[flow.Input];
        double requested = SampleMoles(pipe, _injectorLitresPerSecond[index] / 1000d, seconds);
        if (requested <= 0) return 0;
        int node = rooms.NodeAtCell(Devices[index].Cell);
        if (node < 0) return pipe.Exhaust(requested);
        double before = pipe.Moles;
        // Injectors have no pressure target or backpressure cutoff. Retain only
        // the simulation's numeric receiver bounds while conserving mixture heat.
        Transfer(ref pipe, ref rooms.Gas(node), requested, BoxStationSystems.MaximumPressure);
        return before - pipe.Moles;
    }
}
