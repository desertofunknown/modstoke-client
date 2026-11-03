// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Globalization;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftService
{
    private static bool ValidStationRoomThermal(int device, ushort setting)
        => (uint)device < (uint)BoxStationRoomThermal.GetDevices(BoxStationLayout.Current).Length
            && setting <= byte.MaxValue && BoxStationRoomThermal.ValidControl((BoxStationRoomThermalControl)setting);

    private static bool TryStationRoomThermal(string command, out int device, out ushort setting)
    {
        const string prefix = "station-room-thermal-";
        device = -1;
        setting = 0;
        if (!command.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var fields = command.AsSpan(prefix.Length);
        int separator = fields.IndexOf('-');
        return separator > 0
            && int.TryParse(fields[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out device)
            && ushort.TryParse(fields[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out setting)
            && ValidStationRoomThermal(device, setting);
    }

    private string ExecuteStationRoomThermal(Peer peer, int device, ushort setting, Vector3 actorPosition)
    {
        if (!ValidStationRoomThermal(device, setting)) return "Invalid room conditioner control.";
        string failure = StationControlAccess(peer, actorPosition);
        if (failure.Length != 0) return failure;
        var control = (BoxStationRoomThermalControl)setting;
        failure = ReachStationControl(peer, actorPosition, _orbitalStation!.RoomThermalControlPoint(device, control));
        if (failure.Length != 0) return failure;
        if (!_stationSystems.RoomThermal.ApplyControl(device, control))
            return control switch
            {
                BoxStationRoomThermalControl.Power => "Install a charged cell, close the maintenance hatch and close heat rejection before switching on.",
                BoxStationRoomThermalControl.Mode => "Open the maintenance hatch before changing the conditioner mode.",
                BoxStationRoomThermalControl.Cell => "Open the maintenance hatch and stop charging before moving this conditioner's cell.",
                BoxStationRoomThermalControl.Charge => "Open the maintenance hatch, dock the cell and switch off the conditioner before charging.",
                BoxStationRoomThermalControl.TargetDown or BoxStationRoomThermalControl.TargetUp => "Open the maintenance hatch to adjust the target within 0 to 60 C.",
                _ => "This conditioner cannot accept that control in its current state."
            };
        _nextStationSnapshot = 0;
        return "Room conditioner setting changed.";
    }
}
