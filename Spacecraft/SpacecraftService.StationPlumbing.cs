// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Globalization;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftService
{
    private static bool TryStationPlumbing(string command, out int device)
    {
        const string prefix = "station-plumbing-";
        device = -1;
        return command.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(command.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out device)
            && (uint)device < (uint)BoxStationPlumbing.GetDevices(BoxStationLayout.Current).Length;
    }

    private static bool IsStationFilter(ActionCode action)
        => action is ActionCode.StationFilterSpecies or ActionCode.StationFilterRate;

    private static bool IsStationMixer(ActionCode action)
        => action is ActionCode.StationMixerRatio or ActionCode.StationMixerPressure;

    private static bool IsStationThermal(ActionCode action)
        => action is >= ActionCode.StationThermalMinimum and <= ActionCode.StationThermalRoom;

    private static bool IsStationInjector(ActionCode action)
        => action is ActionCode.StationInjectorRate or ActionCode.StationInjectorPulse;

    private static bool IsStationPlumbingSetting(ActionCode action)
        => IsStationFilter(action) || IsStationMixer(action) || IsStationThermal(action) || IsStationInjector(action);

    private static bool IsStationPlumbing(ActionCode action)
        => action == ActionCode.StationPlumbing || IsStationPlumbingSetting(action);

    private static bool ValidStationPlumbingSetting(ActionCode action, int device, ushort setting)
    {
        var devices = BoxStationPlumbing.GetDevices(BoxStationLayout.Current);
        if ((uint)device >= (uint)devices.Length) return false;
        var kind = devices[device].Kind;
        if (IsStationThermal(action))
        {
            if (!BoxStationPlumbing.ValidThermalTarget(kind, setting / 100f)) return false;
            var range = BoxStationPlumbing.ThermalRange(kind);
            return action switch
            {
                ActionCode.StationThermalMinimum => setting == (ushort)MathF.Round(range.Minimum * 100),
                ActionCode.StationThermalMaximum => setting == (ushort)MathF.Round(range.Maximum * 100),
                ActionCode.StationThermalRoom => setting == 29315,
                _ => true
            };
        }
        return action switch
        {
            ActionCode.StationFilterSpecies => kind == BoxStationPlumbingKind.Filter && setting <= byte.MaxValue
                && BoxStationPlumbing.ValidFilterSpecies((StationGasSpecies)setting),
            ActionCode.StationFilterRate => kind == BoxStationPlumbingKind.Filter
                && setting <= BoxStationPlumbing.MaximumFilterLitresPerSecond,
            ActionCode.StationMixerRatio => kind == BoxStationPlumbingKind.Mixer && setting <= 10_000,
            ActionCode.StationMixerPressure => kind == BoxStationPlumbingKind.Mixer
                && setting <= BoxStationPlumbing.MaximumMixerPressure * 10,
            ActionCode.StationInjectorRate => kind == BoxStationPlumbingKind.Injector
                && setting <= BoxStationPlumbing.MaximumInjectorLitresPerSecond,
            ActionCode.StationInjectorPulse => kind == BoxStationPlumbingKind.Injector && setting == 0,
            _ => false
        };
    }

    private static bool TryStationPlumbingSetting(string command, out int device, out ushort setting, out ActionCode action)
    {
        const string speciesPrefix = "station-filter-gas-", ratePrefix = "station-filter-rate-",
            ratioPrefix = "station-mixer-ratio-", pressurePrefix = "station-mixer-pressure-",
            thermalPrefix = "station-thermal-target-", injectorRatePrefix = "station-injector-rate-",
            injectorPulsePrefix = "station-injector-pulse-";
        device = -1;
        setting = 0;
        action = (ActionCode)255;
        if (command.StartsWith(injectorPulsePrefix, StringComparison.Ordinal))
        {
            action = ActionCode.StationInjectorPulse;
            return int.TryParse(command.AsSpan(injectorPulsePrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out device)
                && ValidStationPlumbingSetting(action, device, setting);
        }
        if (command.StartsWith(thermalPrefix, StringComparison.Ordinal))
        {
            var thermal = command.AsSpan(thermalPrefix.Length);
            int deviceEnd = thermal.IndexOf('-');
            if (deviceEnd <= 0 || !int.TryParse(thermal[..deviceEnd], NumberStyles.None, CultureInfo.InvariantCulture, out device)) return false;
            thermal = thermal[(deviceEnd + 1)..];
            int keyEnd = thermal.IndexOf('-');
            if (keyEnd <= 0 || !byte.TryParse(thermal[..keyEnd], NumberStyles.None, CultureInfo.InvariantCulture, out byte key)
                || key is < 1 or > 5
                || !ushort.TryParse(thermal[(keyEnd + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out setting)) return false;
            action = (ActionCode)((int)ActionCode.StationThermalMinimum + key - 1);
            return ValidStationPlumbingSetting(action, device, setting);
        }
        (ActionCode Action, int PrefixLength) choice = command.StartsWith(speciesPrefix, StringComparison.Ordinal) ? (ActionCode.StationFilterSpecies, speciesPrefix.Length)
            : command.StartsWith(ratePrefix, StringComparison.Ordinal) ? (ActionCode.StationFilterRate, ratePrefix.Length)
            : command.StartsWith(ratioPrefix, StringComparison.Ordinal) ? (ActionCode.StationMixerRatio, ratioPrefix.Length)
            : command.StartsWith(pressurePrefix, StringComparison.Ordinal) ? (ActionCode.StationMixerPressure, pressurePrefix.Length)
            : command.StartsWith(injectorRatePrefix, StringComparison.Ordinal) ? (ActionCode.StationInjectorRate, injectorRatePrefix.Length)
            : ((ActionCode)255, 0);
        action = choice.Action;
        if (action == (ActionCode)255) return false;
        var fields = command.AsSpan(choice.PrefixLength);
        int separator = fields.IndexOf('-');
        return separator > 0
            && int.TryParse(fields[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out device)
            && ushort.TryParse(fields[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out setting)
            && ValidStationPlumbingSetting(action, device, setting);
    }

    private string ExecuteStationPlumbing(Peer peer, ActionCode action, int device, ushort setting, Vector3 actorPosition)
    {
        if ((uint)device >= (uint)_stationSystems.Plumbing.Devices.Length) return "Unknown station gas fitting.";
        if (IsStationPlumbingSetting(action) && !ValidStationPlumbingSetting(action, device, setting)) return "Invalid gas fitting setting.";
        string failure = StationControlAccess(peer, actorPosition);
        if (failure.Length != 0) return failure;
        var contact = IsStationThermal(action)
            ? _orbitalStation!.ThermalControlPoint(device, (int)action - (int)ActionCode.StationThermalMinimum + 1)
            : _stationSystems.Plumbing.Devices[device].Kind == BoxStationPlumbingKind.Injector
                ? _orbitalStation!.InjectorControlPoint(device, action == ActionCode.StationInjectorRate ? 2
                    : action == ActionCode.StationInjectorPulse ? 1 : 0)
            : _orbitalStation!.PlumbingControlPoint(device);
        failure = ReachStationControl(peer, actorPosition, contact);
        if (failure.Length != 0) return failure;
        if (IsStationInjector(action))
        {
            string feedback;
            bool accepted = action == ActionCode.StationInjectorRate
                ? _stationSystems.Plumbing.SetInjectorRate(device, setting, _stationSystems.Power, out feedback)
                : _stationSystems.PulseInjector(device, out feedback);
            if (accepted) _nextStationSnapshot = 0;
            return feedback;
        }
        bool changed = action switch
        {
            ActionCode.StationFilterSpecies => _stationSystems.Plumbing.SetFilterSpecies(device, (StationGasSpecies)setting),
            ActionCode.StationFilterRate => _stationSystems.Plumbing.SetFilterRate(device, setting),
            ActionCode.StationMixerRatio => _stationSystems.Plumbing.SetMixerRatio(device, setting),
            ActionCode.StationMixerPressure => _stationSystems.Plumbing.SetMixerPressure(device, setting / 10f),
            _ when IsStationThermal(action) => _stationSystems.Plumbing.SetThermalTarget(device, setting / 100f),
            _ => _stationSystems.Plumbing.Toggle(device, _stationSystems.Power)
        };
        if (!changed)
            return IsStationPlumbingSetting(action) ? "The gas fitting already has that setting."
                : "This fitting's control circuit has no power.";
        _nextStationSnapshot = 0;
        return action switch
        {
            ActionCode.StationFilterRate => "Filter throughput set to " + setting + " L/s.",
            ActionCode.StationFilterSpecies => "Filter gas selection changed.",
            ActionCode.StationMixerRatio => "Mixer inlet A set to " + (setting / 100f).ToString("0.##", CultureInfo.InvariantCulture) + " mol %.",
            ActionCode.StationMixerPressure => "Mixer target set to " + (setting / 10f).ToString("0.0", CultureInfo.InvariantCulture) + " kPa.",
            _ when IsStationThermal(action) => "Pipe temperature target set to " + (setting / 100f).ToString("0.00", CultureInfo.InvariantCulture) + " K.",
            _ => "Gas fitting setting changed."
        };
    }
}
