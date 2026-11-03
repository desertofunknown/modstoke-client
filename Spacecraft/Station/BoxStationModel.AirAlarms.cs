// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class BoxStationModel
{
    private Transform[] _airAlarmFrames = Array.Empty<Transform>();

    internal Transform AirAlarmControlFrame(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)index >= _airAlarmFrames.Length) throw new ArgumentOutOfRangeException(nameof(index));
        return _airAlarmFrames[index];
    }

    internal Vector3 AirAlarmControlPoint(int index)
        => AirAlarmControlFrame(index).TransformPoint(new(0, -.31f, .205f));

    internal Vector3 AirAlarmControlPoint(int index, BoxStationAirMode mode)
    {
        int button = mode switch
        {
            BoxStationAirMode.Filtering => 0,
            BoxStationAirMode.Draught => 1,
            BoxStationAirMode.Panic => 2,
            BoxStationAirMode.Cycle => 3,
            BoxStationAirMode.Off => 4,
            BoxStationAirMode.Siphon => 5,
            BoxStationAirMode.Contaminated => 6,
            BoxStationAirMode.Refill => 7,
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
        return AirAlarmControlFrame(index).TransformPoint(AirAlarmButtonCenter(button));
    }

    internal static Vector3 AirAlarmButtonCenter(int button)
    {
        if ((uint)button >= 8) throw new ArgumentOutOfRangeException(nameof(button));
        return new(button % 2 == 0 ? .21f : -.21f, -.16f - button / 2 * .11f, .205f);
    }

    private IEnumerable<bool> BuildAirAlarmMachinery()
    {
        var devices = BoxStationAirAlarms.GetDevices(_layout);
        _airAlarmFrames = new Transform[devices.Length];
        for (int i = 0; i < devices.Length; i++)
        {
            var source = devices[i];
            Mount(source.Cell, source.Direction, 1.55f, out var position, out var rotation);
            var frame = Node("Air alarm " + i, Root.transform);
            frame.localPosition = position;
            frame.localRotation = rotation;
            _airAlarmFrames[i] = frame;
            AddMachine(new(source.Cell, "machine-airalarm", position, rotation, Vector3.one, -1));
            // Keep both the fitted display and the common command point ahead of the housing.
            _machineSolids.Add(new(position, rotation, new(0, 0, .09f), new(.94f, 1.20f, .18f)));
            yield return false;
        }
    }

    private void ReleaseAirAlarmMachinery() => _airAlarmFrames = Array.Empty<Transform>();
}
