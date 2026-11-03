// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class BoxStationModel
{
    private Transform[] _plumbingFrames = Array.Empty<Transform>();

    internal Transform PlumbingControlFrame(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)index >= _plumbingFrames.Length) throw new ArgumentOutOfRangeException(nameof(index));
        return _plumbingFrames[index];
    }

    internal Vector3 PlumbingControlPoint(int index)
    {
        var frame = PlumbingControlFrame(index);
        return frame.TransformPoint(PlumbingButtonCenter(BoxStationPlumbing.GetDevices(_layout)[index].Kind));
    }

    internal static Vector3 PlumbingButtonCenter(BoxStationPlumbingKind kind) => kind switch
    {
        BoxStationPlumbingKind.Valve => new(0, .70f, .326f),
        BoxStationPlumbingKind.Pump => new(.255f, .58f, .376f),
        BoxStationPlumbingKind.Filter => new(0, 1.03f, .571f),
        BoxStationPlumbingKind.Mixer => new(0, 1.01f, .571f),
        BoxStationPlumbingKind.Heater or BoxStationPlumbingKind.Freezer => ThermalButtonCenter(0),
        BoxStationPlumbingKind.Vent or BoxStationPlumbingKind.Scrubber => new(.175f, .101f, .145f),
        BoxStationPlumbingKind.Injector => new(0, .16f, -.275f),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    internal static Vector3 MixerButtonCenter(int button) => button switch
    {
        0 => new(.36f, .94f, .571f),
        1 => new(0, .94f, .571f),
        2 => new(-.30f, .94f, .571f),
        3 => new(.26f, .83f, .571f),
        4 => new(-.26f, .83f, .571f),
        5 => new(.26f, .71f, .571f),
        6 => new(-.26f, .71f, .571f),
        _ => throw new ArgumentOutOfRangeException(nameof(button))
    };

    internal static Vector3 FilterButtonCenter(int button) => button switch
    {
        0 => new(.39f, .98f, .571f),
        >= 1 and <= 6 => new(.26f - (button - 1) % 3 * .26f, button <= 3 ? .84f : .72f, .571f),
        7 => new(-.13f, .98f, .571f),
        8 => new(-.39f, .98f, .571f),
        _ => throw new ArgumentOutOfRangeException(nameof(button))
    };

    private IEnumerable<bool> BuildPlumbingMachinery()
    {
        var devices = BoxStationPlumbing.GetDevices(_layout);
        _plumbingFrames = new Transform[devices.Length];
        foreach (bool endFrame in BuildInjectorMeshes()) yield return endFrame;
        for (int i = 0; i < devices.Length; i++)
        {
            var device = devices[i];
            var position = _layout.CellPosition(device.Cell);
            var rotation = Turn(PortDirection(device.Direction == 0 ? 2 : device.Direction));
            var frame = Node("Plumbing control " + i, Root.transform);
            frame.localPosition = position;
            frame.localRotation = rotation;
            _plumbingFrames[i] = frame;
            if (device.Kind == BoxStationPlumbingKind.Valve)
            {
                AddMachine(new(device.Cell, "machine-valve", position, rotation, Vector3.one, -1));
                _machineSolids.Add(new(position, rotation, new(0, .425f, 0), new(.65f, .85f, .65f)));
            }
            else if (device.Kind == BoxStationPlumbingKind.Pump)
            {
                AddMachine(new(device.Cell, "machine-pump", position, rotation, Vector3.one, -1));
                _machineSolids.Add(new(position, rotation, new(0, .425f, 0), new(.8f, .85f, .75f)));
            }
            else if (device.Kind is BoxStationPlumbingKind.Filter or BoxStationPlumbingKind.Mixer)
            {
                string machine = device.Kind == BoxStationPlumbingKind.Mixer ? "machine-mixer" : "machine-filter";
                AddMachine(new(device.Cell, machine, position, rotation, Vector3.one, -1));
                var ports = position + Vector3.up * ((device.Layer - 1) * .10f);
                AddMachine(new(device.Cell, machine + (device.Flipped ? "-ports-flipped" : "-ports"), ports, rotation, Vector3.one, -1));
                _machineSolids.Add(new(position, rotation, new(0, .045f, 0), new(1.4f, .09f, 1.45f)));
                _machineSolids.Add(new(position, rotation, new(0, .73f, -.12f), new(1.14f, 1.28f, .86f)));
                // The control face must remain ahead of collision for both camera and host reach rays.
                _machineSolids.Add(new(position, rotation, new(0, 1.035f, .48f), new(1.08f, .81f, .16f)));
                _machineSolids.Add(new(ports, rotation, new(0, .18f, 0), new(.26f, .26f, 2.4f)));
                _machineSolids.Add(new(ports, rotation, new(.62f, .18f, 0), new(1.16f, .26f, .26f)));
            }
            else if (device.Kind is BoxStationPlumbingKind.Heater or BoxStationPlumbingKind.Freezer)
                AddThermalMachinery(device, position, rotation);
            else if (device.Kind == BoxStationPlumbingKind.Injector)
                foreach (bool endFrame in BuildInjector(i, frame, position, rotation)) yield return endFrame;
            yield return false;
        }
        int scanned = 0;
        foreach (var source in _layout.Objects)
        {
            if (++scanned % 64 == 0) yield return false;
            if (!_layout.Types[source.Type].StartsWith("/obj/machinery/atmospherics/components/unary/tank/", StringComparison.Ordinal)) continue;
            var position = _layout.CellPosition(source.Cell);
            var rotation = Turn(PortDirection(source.Direction == 0 ? 2 : source.Direction));
            AddMachine(new(source.Cell, "machine-tank", position, rotation, Vector3.one, -1));
            _machineSolids.Add(new(position, rotation, new(0, 1.5f, 0), new(2.3f, 3, 2.3f)));
            yield return false;
        }
    }

    private void ReleasePlumbingMachinery()
    {
        _plumbingFrames = Array.Empty<Transform>();
        ReleaseInjectors();
    }
}
