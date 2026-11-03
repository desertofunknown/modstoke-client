// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using UnityEngine;
using GasVector = System.Numerics.Vector3;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftService
{
    private BoxStationFabricationControls? _fabricationControls;

    private string ExecuteStationFabrication(Peer peer, int machine, BoxStationMaterialControl control, Vector3 feet, ulong actor)
    {
        var model = _orbitalStation!;
        var point = model.FabricationControlPoint(machine, control);
        string failure = ReachStationControl(peer, feet, point, animate: false);
        if (failure.Length != 0) return failure;
        GasVector drop = default;
        if (control is BoxStationMaterialControl.UnloadFabricatorSteel or BoxStationMaterialControl.UnloadFabricatorCable
            or BoxStationMaterialControl.TakeFabricatorLine or BoxStationMaterialControl.TakeFabricatorCircuit)
        {
            if (HasHeldMaterials(peer.Lease, actor)) return "Put down the material bundle first.";
            if (!TryStationToolDrop(peer, feet, out drop, SpacecraftHeldStationToolKind.Material))
                return "There is no clear deck within reach for the bundle.";
        }
        CancelEngineeringWork(peer);
        var repair = _stationSystems.Repair;
        var previous = repair.Materials.Fabrication;
        string result = control switch
        {
            BoxStationMaterialControl.LoadFabricator => repair.LoadFabricationInput(peer.Lease, actor),
            BoxStationMaterialControl.UnloadFabricatorSteel => repair.UnloadFabricationInput(peer.Lease, actor, BoxStationMaterial.Steel, drop),
            BoxStationMaterialControl.UnloadFabricatorCable => repair.UnloadFabricationInput(peer.Lease, actor, BoxStationMaterial.Cable, drop),
            BoxStationMaterialControl.FabricateLine => repair.StartFabrication(BoxStationFabricationRecipe.Line),
            BoxStationMaterialControl.FabricateCircuit => repair.StartFabrication(BoxStationFabricationRecipe.Circuit),
            BoxStationMaterialControl.CancelFabrication => repair.CancelFabrication(),
            BoxStationMaterialControl.TakeFabricatorLine => repair.TakeFabricationOutput(peer.Lease, actor, BoxStationMaterial.LineSpare, drop),
            BoxStationMaterialControl.TakeFabricatorCircuit => repair.TakeFabricationOutput(peer.Lease, actor, BoxStationMaterial.CircuitSpare, drop),
            _ => "Invalid fabricator control."
        };
        if (repair.Materials.Fabrication != previous) RecordStationInteraction(peer, actor, point);
        return result;
    }

    private bool UpdateFabricationControls(bool interactive)
    {
        var model = _orbitalStation;
        if (model?.Visible != true)
        {
            _fabricationControls?.Update(null, false, _feedback);
            return false;
        }
        _fabricationControls ??= new BoxStationFabricationControls(model, CommandStationMaterials);
        var state = StationHullCurrent ? CurrentMaterialsState : null;
        if (state != null && _body?.TelemetryCurrent == true && _body.State.Docked
            && _body.State.Travel == SpacecraftTravel.None)
        {
            foreach (var interaction in _interactions)
            {
                if (!interaction.Active || interaction.Actor == 0) continue;
                var point = _body.Root.transform.TransformPoint(interaction.LocalTarget);
                for (var control = BoxStationMaterialControl.LoadFabricator;
                    control <= BoxStationMaterialControl.TakeFabricatorCircuit; control++)
                {
                    var target = model.FabricationControlPoint(BoxStationHullRepair.FabricationMachine, control);
                    if ((target - point).sqrMagnitude > .000025f) continue;
                    model.PressFabricationControl(BoxStationHullRepair.FabricationMachine, control);
                    break;
                }
            }
        }
        return _fabricationControls.Update(state, interactive, _feedback);
    }
}
