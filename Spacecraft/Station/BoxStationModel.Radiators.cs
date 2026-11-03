// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class BoxStationModel
{
    private Material? _radiator;

    private IEnumerable<bool> BuildRadiatorMachinery()
    {
        foreach (var panel in BoxStationPlumbing.GetRadiators(_layout))
        {
            bool bent = panel.Direction is 5 or 6 or 9 or 10;
            int turn = panel.Direction switch { 4 or 8 or 6 => 1, 10 => 2, 9 => 3, _ => 0 };
            var rotation = Turn(turn);
            var position = _layout.CellPosition(panel.Cell) + Vector3.up * ((panel.Layer - 1) * .10f);
            AddMachine(new(panel.Cell, bent ? "machine-radiator-bent" : "machine-radiator-straight",
                position, rotation, Vector3.one, -1, Radiator: true));
            // Nominal coated area is 1.28 x 2.00 m per side; tube and folded edges add no simulated area.
            _machineSolids.Add(new(position, rotation, new(0, .075f, 0), new(1.28f, .15f, 2.00f)));
            if (bent)
            {
                _machineSolids.Add(new(position, rotation, new(0, .18f, .65f), new(.25f, .25f, 1.10f)));
                _machineSolids.Add(new(position, rotation, new(.65f, .18f, 0), new(1.10f, .25f, .25f)));
                _machineSolids.Add(new(position, rotation, new(.12f, .18f, .12f), new(.40f, .25f, .40f)));
            }
            else _machineSolids.Add(new(position, rotation, new(0, .18f, 0), new(.25f, .25f, 2.40f)));
            yield return false;
        }
        int scanned = 0;
        foreach (var source in _layout.Objects)
        {
            if (++scanned % 64 == 0) yield return false;
            string type = _layout.Types[source.Type];
            const string junction = "/obj/machinery/atmospherics/pipe/heat_exchanging/junction";
            if (type != junction && !type.StartsWith(junction + "/", StringComparison.Ordinal)) continue;
            int layer = BoxStationPlumbing.PipeLayer(source, type);
            var position = _layout.CellPosition(source.Cell) + Vector3.up * ((layer - 1) * .10f);
            var rotation = Turn(PortDirection(source.Direction == 0 ? 2 : source.Direction));
            // The bare +Z socket is the HE family; the insulated opposite socket is standard pipe.
            AddMachine(new(source.Cell, "machine-radiator-junction", position, rotation, Vector3.one, -1, Radiator: true));
            _machineSolids.Add(new(position, rotation, new(0, .18f, 0), new(.30f, .30f, 2.40f)));
            _machineSolids.Add(new(position, rotation, new(0, .18f, 0), new(.42f, .36f, .58f)));
            yield return false;
        }
    }

    private void LoadRadiatorMaterial(Shader shader)
    {
        _radiator = new Material(shader) { name = "Radiator ceramic and alloy", enableInstancing = true };
        _radiator.SetColor("_BaseColor", Color.white);
        _radiator.SetTexture("_BaseColorMap", LoadTexture("radiator-base", false));
        _radiator.SetTexture("_MaskMap", LoadTexture("radiator-mask", true));
        _radiator.SetTexture("_NormalMap", LoadTexture("radiator-normal", true, true));
        _radiator.SetFloat("_NormalMapSpace", 0); _radiator.SetFloat("_NormalScale", 1);
        _radiator.SetFloat("_MetallicRemapMin", 0); _radiator.SetFloat("_MetallicRemapMax", 1);
        _radiator.SetFloat("_SmoothnessRemapMin", 0); _radiator.SetFloat("_SmoothnessRemapMax", 1);
        _radiator.SetFloat("_AORemapMin", 0); _radiator.SetFloat("_AORemapMax", 1);
        _radiator.EnableKeyword("_MASKMAP");
        _radiator.EnableKeyword("_NORMALMAP");
        _radiator.EnableKeyword("_NORMALMAP_TANGENT_SPACE");
    }
}
