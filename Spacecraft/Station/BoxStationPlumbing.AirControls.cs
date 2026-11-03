// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

namespace Modstoke.Client;

internal sealed partial class BoxStationPlumbing
{
    private readonly byte[] _ventChecks;
    private readonly float[] _ventExternalPressure;
    private readonly bool[] _scrubbing;
    private readonly bool[] _wideNet;
    private readonly StationGasSpecies[] _scrubSpecies;

    internal void ApplyAirMode(int area, BoxStationAirMode mode, BoxStationPower power)
    {
        if (area < 0 || area >= _definitions.AirReceivers.Length) return;
        if (!BoxStationAirAlarms.ValidMode(mode)) return;
        foreach (int index in _definitions.AirReceivers[area])
        {
            // Commands are accepted once by powered receivers; later power recovery
            // does not replay an alarm's selected mode.
            if (!Powered(index, power)) continue;
            if (Devices[index].Kind == BoxStationPlumbingKind.Vent)
            {
                bool on = mode is BoxStationAirMode.Filtering or BoxStationAirMode.Draught
                    or BoxStationAirMode.Contaminated or BoxStationAirMode.Refill;
                _enabled[index] = on;
                if (!on) continue;
                _ventChecks[index] = 1;
                _ventExternalPressure[index] = mode switch
                {
                    BoxStationAirMode.Draught => (float)(2 * Atmosphere),
                    BoxStationAirMode.Refill => (float)(3 * Atmosphere),
                    _ => (float)Atmosphere
                };
                continue;
            }

            _enabled[index] = mode != BoxStationAirMode.Off;
            if (mode == BoxStationAirMode.Off) continue;
            _scrubbing[index] = mode is BoxStationAirMode.Filtering or BoxStationAirMode.Refill
                or BoxStationAirMode.Contaminated;
            _wideNet[index] = mode is BoxStationAirMode.Panic or BoxStationAirMode.Cycle
                or BoxStationAirMode.Contaminated;
            if (mode is BoxStationAirMode.Filtering or BoxStationAirMode.Refill)
                _scrubSpecies[index] = StationGasSpecies.CO2;
            else if (mode == BoxStationAirMode.Contaminated)
                _scrubSpecies[index] = StationGasSpecies.CO2 | StationGasSpecies.Plasma | StationGasSpecies.N2O
                    | StationGasSpecies.WaterVapor | StationGasSpecies.Tritium | StationGasSpecies.BZ;
        }
    }

    private void SetInitialAirControl(int index, byte[] checks, float[] pressure,
        bool[] scrubbing, StationGasSpecies[] species)
    {
        var flow = _definitions.Flow[index];
        if (Devices[index].Kind == BoxStationPlumbingKind.Vent)
        {
            checks[index] = (byte)flow.PressureChecks;
            pressure[index] = (float)flow.ExternalBound;
        }
        else if (Devices[index].Kind == BoxStationPlumbingKind.Scrubber)
        {
            scrubbing[index] = flow.Scrubbing;
            species[index] = StationGasSpecies.CO2;
        }
    }

    private static bool ValidAirControl(BoxStationPlumbingKind kind, byte checks, float pressure,
        bool scrubbing, bool wideNet, StationGasSpecies species)
    {
        if (kind == BoxStationPlumbingKind.Vent)
        {
            if (checks > 3 || !float.IsFinite(pressure) || pressure < 0
                || pressure > BoxStationSystems.MaximumPressure) return false;
        }
        else if (checks != 0 || pressure != 0) return false;
        return kind == BoxStationPlumbingKind.Scrubber
            ? (species & ~StationGasSpecies.All) == 0
            : !scrubbing && !wideNet && species == 0;
    }

    private static int SelectedSpeciesCount(StationGasSpecies species)
    {
        int count = 0, bits = (int)species;
        while (bits != 0)
        {
            bits &= bits - 1;
            count++;
        }
        return count;
    }
}
