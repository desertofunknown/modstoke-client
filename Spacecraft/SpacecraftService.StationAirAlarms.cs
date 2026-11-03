// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Globalization;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftService
{
    private static bool ValidStationAirMode(int alarm, ushort setting)
        => (uint)alarm < (uint)BoxStationAirAlarms.GetDevices(BoxStationLayout.Current).Length
            && setting <= byte.MaxValue && BoxStationAirAlarms.ValidMode((BoxStationAirMode)setting);

    private static bool TryStationAirMode(string command, out int alarm, out ushort setting)
    {
        const string prefix = "station-air-mode-";
        alarm = -1;
        setting = 0;
        if (!command.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var fields = command.AsSpan(prefix.Length);
        int separator = fields.IndexOf('-');
        return separator > 0
            && int.TryParse(fields[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out alarm)
            && ushort.TryParse(fields[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out setting)
            && ValidStationAirMode(alarm, setting);
    }

    private string ExecuteStationAirMode(Peer peer, int alarm, ushort setting, Vector3 actorPosition)
    {
        if (!ValidStationAirMode(alarm, setting)) return "Invalid air alarm setting.";
        string failure = StationControlAccess(peer, actorPosition);
        if (failure.Length != 0) return failure;
        failure = ReachStationControl(peer, actorPosition, _orbitalStation!.AirAlarmControlPoint(alarm, (BoxStationAirMode)setting));
        if (failure.Length != 0) return failure;
        if (!_stationSystems.Alarms.SetMode(alarm, (BoxStationAirMode)setting, _stationSystems.Plumbing, _stationSystems.Power))
            return "This air alarm's environment circuit has no power.";
        _nextStationSnapshot = 0;
        return "Command sent to this area's powered vents and scrubbers.";
    }
}
