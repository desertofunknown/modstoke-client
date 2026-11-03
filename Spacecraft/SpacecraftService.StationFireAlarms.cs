// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Globalization;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftService
{
    private static bool ValidStationFireAlarm(int alarm, ushort setting)
        => (uint)alarm < (uint)BoxStationFireAlarms.GetDevices(BoxStationLayout.Current).Length
            && setting <= byte.MaxValue && BoxStationFireAlarms.ValidControl((BoxStationFireAlarmControl)setting);

    private static bool TryStationFireAlarm(string command, out int alarm, out ushort setting)
    {
        const string prefix = "station-fire-alarm-";
        alarm = -1;
        setting = 0;
        if (!command.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var fields = command.AsSpan(prefix.Length);
        int separator = fields.IndexOf('-');
        return separator > 0
            && int.TryParse(fields[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out alarm)
            && ushort.TryParse(fields[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out setting)
            && ValidStationFireAlarm(alarm, setting);
    }

    private string ExecuteStationFireAlarm(Peer peer, int alarm, ushort setting, Vector3 actorPosition)
    {
        if (!ValidStationFireAlarm(alarm, setting)) return "Invalid fire alarm control.";
        string failure = StationControlAccess(peer, actorPosition);
        if (failure.Length != 0) return failure;
        var control = (BoxStationFireAlarmControl)setting;
        failure = ReachStationControl(peer, actorPosition, _orbitalStation!.FireAlarmControlPoint(alarm, control));
        if (failure.Length != 0) return failure;
        string result = _stationSystems.FireAlarms.Command(alarm, control, _stationSystems.Power);
        _nextStationSnapshot = 0;
        return result;
    }
}
