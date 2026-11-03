// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Globalization;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftService
{
    private static bool ValidStationSmes(int device, ushort setting)
        => (uint)device < (uint)BoxStationPower.GetSmes(BoxStationLayout.Current).Length
            && setting <= byte.MaxValue && BoxStationPower.ValidSmesControl((BoxStationSmesControl)setting);

    private static bool TryStationSmes(string command, out int device, out ushort setting)
    {
        const string prefix = "station-smes-";
        device = -1;
        setting = 0;
        if (!command.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var fields = command.AsSpan(prefix.Length);
        int separator = fields.IndexOf('-');
        return separator > 0
            && int.TryParse(fields[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out device)
            && ushort.TryParse(fields[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out setting)
            && ValidStationSmes(device, setting);
    }

    private string ExecuteStationSmes(Peer peer, int device, ushort setting, Vector3 actorPosition)
    {
        if (!ValidStationSmes(device, setting)) return "Invalid storage power control.";
        string failure = StationControlAccess(peer, actorPosition);
        if (failure.Length != 0) return failure;
        var control = (BoxStationSmesControl)setting;
        failure = ReachStationControl(peer, actorPosition, _orbitalStation!.SmesControlPoint(device, control));
        if (failure.Length != 0) return failure;
        if (!_stationSystems.Power.ControlSmes(device, control)) return "The storage setting could not be changed.";
        _nextStationSnapshot = 0;
        return "Storage power setting changed.";
    }
}
