// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class BoxStationModel
{
    private Material? _thermal;

    internal Vector3 ThermalControlPoint(int index, int button)
        => PlumbingControlFrame(index).TransformPoint(ThermalButtonCenter(button));

    internal static Vector3 ThermalButtonCenter(int button) => button switch
    {
        0 => new(.34f, 1.00f, .611f),
        1 => new(.34f, .85f, .611f),
        2 => new(0, 1.00f, .611f),
        3 => new(-.34f, 1.00f, .611f),
        4 => new(-.34f, .85f, .611f),
        5 => new(0, .85f, .611f),
        _ => throw new ArgumentOutOfRangeException(nameof(button))
    };

    private void AddThermalMachinery(BoxStationPlumbingDevice device, Vector3 position, Quaternion rotation)
    {
        string body = device.Kind == BoxStationPlumbingKind.Heater ? "machine-thermal-heater" : "machine-thermal-freezer";
        AddMachine(new(device.Cell, body, position, rotation, Vector3.one, -1, true));
        var port = position + Vector3.up * ((device.Layer - 1) * .10f);
        AddMachine(new(device.Cell, "machine-thermal-port", port, rotation, Vector3.one, -1, true));
        _machineSolids.Add(new(position, rotation, new(0, .06f, 0), new(1.28f, .12f, 1.18f)));
        _machineSolids.Add(new(position, rotation, new(0, .92f, -.02f), new(1.18f, 1.60f, 1.06f)));
        // Both key faces and the display sit ahead of the housing's collision plane.
        _machineSolids.Add(new(position, rotation, new(0, 1.23f, .55f), new(1.14f, 1.02f, .08f)));
        _machineSolids.Add(new(port, rotation, new(0, .18f, .85f), new(.30f, .30f, .70f)));
    }

    private void LoadThermalMaterial(Shader shader)
    {
        _thermal = new Material(shader) { name = "Thermal machinery finish", enableInstancing = true };
        _thermal.SetColor("_BaseColor", Color.white);
        _thermal.SetTexture("_BaseColorMap", LoadTexture("thermal-base", false));
        _thermal.SetTexture("_MaskMap", LoadTexture("thermal-mask", true));
        _thermal.SetTexture("_NormalMap", LoadTexture("thermal-normal", true, true));
        _thermal.SetFloat("_NormalMapSpace", 0);
        _thermal.SetFloat("_NormalScale", 1);
        _thermal.SetFloat("_MetallicRemapMin", 0); _thermal.SetFloat("_MetallicRemapMax", 1);
        _thermal.SetFloat("_SmoothnessRemapMin", 0); _thermal.SetFloat("_SmoothnessRemapMax", 1);
        _thermal.SetFloat("_AORemapMin", 0); _thermal.SetFloat("_AORemapMax", 1);
        _thermal.EnableKeyword("_MASKMAP");
        _thermal.EnableKeyword("_NORMALMAP");
        _thermal.EnableKeyword("_NORMALMAP_TANGENT_SPACE");
    }
}
