// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.IO;
using System.Text.Json.Serialization;
using Vector3 = System.Numerics.Vector3;

namespace Modstoke.Client;

internal enum BoxStationFabricationRecipe : byte { None, Line, Circuit }
internal enum BoxStationFabricationStatus : byte { Idle, Working, NoPower, NoCooling, OutputBlocked }

internal readonly record struct BoxStationFabricationState(
    [property: JsonRequired] int Machine,
    [property: JsonRequired] ushort Steel, [property: JsonRequired] ushort Cable,
    [property: JsonRequired] ushort LineStock, [property: JsonRequired] ushort CircuitStock,
    [property: JsonRequired] int ProducedLine, [property: JsonRequired] int ProducedCircuit,
    [property: JsonRequired] int ExportedLine, [property: JsonRequired] int ExportedCircuit,
    [property: JsonRequired] BoxStationFabricationRecipe Recipe,
    [property: JsonRequired] double WorkJoules, [property: JsonRequired] BoxStationFabricationStatus Status);

internal sealed partial class BoxStationHullRepair
{
    internal const int FabricationMachine = 13515, FabricationCell = 34764;
    internal const int FabricationSteelCapacity = 50, FabricationCableCapacity = 30,
        FabricationLineCapacity = 24, FabricationCircuitCapacity = 12;
    // Modstoke gameplay recipes: one steel sheet per line kit; one steel sheet and two cable units per circuit kit.
    internal const double FabricationWatts = 1200;
    internal static readonly BoxStationFabricationState EmptyFabrication = new(FabricationMachine,
        0, 0, 0, 0, 0, 0, 0, 0, BoxStationFabricationRecipe.None, 0, BoxStationFabricationStatus.Idle);
    private BoxStationFabricationState _fabrication = EmptyFabrication;
    private int _fabricationArea;

    internal static double FabricationWork(BoxStationFabricationRecipe recipe) => recipe switch
    {
        BoxStationFabricationRecipe.Line => 9600,
        BoxStationFabricationRecipe.Circuit => 14400,
        _ => 0
    };

    internal static bool TryFabricationDefinition(BoxStationLayout layout, out BoxStationObject definition)
    {
        definition = default;
        if (FabricationMachine >= layout.Objects.Length || FabricationCell >= layout.Cells.Length) return false;
        var source = layout.Objects[FabricationMachine];
        if (source.Id != FabricationMachine || source.Cell != FabricationCell
            || (uint)source.Type >= (uint)layout.Types.Length || layout.Types[source.Type] != "/obj/machinery/autolathe") return false;
        definition = source;
        return true;
    }

    private void InitializeFabrication()
    {
        if (!TryFabricationDefinition(_layout, out var definition))
            throw new InvalidDataException("Station fabrication machine does not match the mapped autolathe.");
        _fabricationArea = _layout.Cells[definition.Cell].Area;
        _fabrication = EmptyFabrication;
    }

    internal string LoadFabricationInput(Guid holder, ulong actor)
    {
        int slot = HeldMaterialSlot(holder);
        if (!_materialsInitialized || slot < 0) return "Hold steel sheets or cable to load the fabricator.";
        var bundle = _materialBundles[slot];
        if (bundle.Actor != actor) return "Material holder is unavailable.";
        int capacity = bundle.Material == BoxStationMaterial.Steel ? FabricationSteelCapacity - _fabrication.Steel
            : bundle.Material == BoxStationMaterial.Cable ? FabricationCableCapacity - _fabrication.Cable : -1;
        if (capacity < 0) return "The fabricator accepts steel sheets and cable.";
        if (capacity == 0) return "This fabricator input is full.";
        ushort taken = (ushort)Math.Min(capacity, bundle.Count);
        _materialBundles[slot] = bundle.Count == taken ? default : bundle with { Count = (ushort)(bundle.Count - taken) };
        _fabrication = bundle.Material == BoxStationMaterial.Steel
            ? _fabrication with { Steel = (ushort)(_fabrication.Steel + taken) }
            : _fabrication with { Cable = (ushort)(_fabrication.Cable + taken) };
        _materialsDirty = true;
        return "Materials loaded into the fabricator.";
    }

    internal string UnloadFabricationInput(Guid holder, ulong actor, BoxStationMaterial material, Vector3 dropPosition)
    {
        if (_fabrication.Recipe != BoxStationFabricationRecipe.None) return "Cancel the current job before unloading its inputs.";
        ushort count = material == BoxStationMaterial.Steel ? _fabrication.Steel
            : material == BoxStationMaterial.Cable ? _fabrication.Cable : (ushort)0;
        if (count == 0) return "No matching fabricator input is available.";
        string? rejection = PrepareFabricationPickup(holder, actor, dropPosition, out int slot);
        if (rejection is not null) return rejection;
        _materialBundles[slot] = new(_nextMaterialBundleId++, material, count,
            BoxStationMaterialPlace.Held, dropPosition, actor, holder);
        _fabrication = material == BoxStationMaterial.Steel ? _fabrication with { Steel = 0 } : _fabrication with { Cable = 0 };
        _materialsDirty = true;
        return "Fabricator input taken.";
    }

    internal string StartFabrication(BoxStationFabricationRecipe recipe)
    {
        if (!_materialsInitialized || recipe is not (BoxStationFabricationRecipe.Line or BoxStationFabricationRecipe.Circuit))
            return "Select a line or circuit repair kit.";
        if (_fabrication.Recipe != BoxStationFabricationRecipe.None)
            return _fabrication.Recipe == recipe ? "This fabrication job is already selected." : "Cancel the current fabrication job first.";
        if (_fabrication.Steel < 1 || recipe == BoxStationFabricationRecipe.Circuit && _fabrication.Cable < 2)
            return recipe == BoxStationFabricationRecipe.Line ? "A line repair kit requires one steel sheet."
                : "A circuit repair kit requires one steel sheet and two cable units.";
        if (FabricationOutputFull(_fabrication, recipe)) return "Take the completed repair kits before starting another job.";
        if ((recipe == BoxStationFabricationRecipe.Line ? _fabrication.ProducedLine : _fabrication.ProducedCircuit) == int.MaxValue)
            return "The fabrication production ledger is full.";
        _fabrication = _fabrication with { Recipe = recipe, WorkJoules = 0, Status = BoxStationFabricationStatus.Working };
        _materialsDirty = true;
        return "Fabrication job selected.";
    }

    internal string CancelFabrication()
    {
        if (!_materialsInitialized || _fabrication.Recipe == BoxStationFabricationRecipe.None) return "No fabrication job is selected.";
        _fabrication = _fabrication with { Recipe = BoxStationFabricationRecipe.None, WorkJoules = 0, Status = BoxStationFabricationStatus.Idle };
        _materialsDirty = true;
        return "Fabrication cancelled. Input materials retained; spent work discarded.";
    }

    internal string TakeFabricationOutput(Guid holder, ulong actor, BoxStationMaterial material, Vector3 dropPosition)
    {
        ushort count = material == BoxStationMaterial.LineSpare ? _fabrication.LineStock
            : material == BoxStationMaterial.CircuitSpare ? _fabrication.CircuitStock : (ushort)0;
        if (count == 0) return "No matching repair kits are ready.";
        string? rejection = PrepareFabricationPickup(holder, actor, dropPosition, out int slot);
        if (rejection is not null) return rejection;
        _materialBundles[slot] = new(_nextMaterialBundleId++, material, count,
            BoxStationMaterialPlace.Held, dropPosition, actor, holder);
        _fabrication = material == BoxStationMaterial.LineSpare
            ? _fabrication with { LineStock = 0 } : _fabrication with { CircuitStock = 0 };
        _materialsDirty = true;
        return "Repair kits taken.";
    }

    private string? PrepareFabricationPickup(Guid holder, ulong actor, Vector3 dropPosition, out int slot)
    {
        slot = -1;
        if (Held(holder) >= 0 || HasHeldActor(actor)) return "Put down the floor tiles before taking materials.";
        return PrepareMaterialPickup(holder, actor, dropPosition, out slot);
    }

    // The caller secures ship headroom under its owner lock before committing this exact held bundle.
    internal bool TryExportSpares(Guid holder, ulong actor, int expectedBundleId, BoxStationMaterial kind,
        int maximum, out int taken, out string feedback)
    {
        taken = 0;
        feedback = "Hold matching steel sheets or repair kits to restock the ship.";
        if (!_materialsInitialized || maximum <= 0
            || kind is not (BoxStationMaterial.Steel or BoxStationMaterial.LineSpare or BoxStationMaterial.CircuitSpare)) return false;
        int slot = HeldMaterialSlot(holder);
        if (slot < 0) return false;
        var bundle = _materialBundles[slot];
        if (bundle.Id != expectedBundleId || bundle.Actor != actor || bundle.Material != kind || bundle.Count == 0) return false;
        int count = Math.Min(maximum, bundle.Count);
        long exported = count + (kind switch
        {
            BoxStationMaterial.Steel => (long)_shipExportedSteel,
            BoxStationMaterial.LineSpare => _fabrication.ExportedLine,
            _ => _fabrication.ExportedCircuit
        });
        long available = kind switch
        {
            BoxStationMaterial.Steel => (long)_constructionBudget.Steel + _importedSteel,
            BoxStationMaterial.LineSpare => _fabrication.ProducedLine,
            _ => _fabrication.ProducedCircuit
        };
        if (exported > available || exported > int.MaxValue) { feedback = "Repair material export ledger is unavailable."; return false; }
        var remaining = bundle.Count == count ? default : bundle with { Count = (ushort)(bundle.Count - count) };
        var fabrication = kind switch
        {
            BoxStationMaterial.LineSpare => _fabrication with { ExportedLine = (int)exported },
            BoxStationMaterial.CircuitSpare => _fabrication with { ExportedCircuit = (int)exported },
            _ => _fabrication
        };
        _materialBundles[slot] = remaining;
        _fabrication = fabrication;
        if (kind == BoxStationMaterial.Steel) _shipExportedSteel = (int)exported;
        _materialsDirty = true;
        taken = count;
        feedback = "Ship repair spares restocked.";
        return true;
    }

    internal void StepFabrication(double dt, BoxStationPower power, BoxStationRooms rooms)
    {
        if (!ReferenceEquals(rooms.Layout, _layout)) throw new ArgumentException("Expected rooms for this station layout.", nameof(rooms));
        if (!_materialsInitialized || _fabrication.Recipe == BoxStationFabricationRecipe.None || !double.IsFinite(dt) || dt <= 0) return;
        dt = Math.Min(dt, .1);
        if (FabricationOutputFull(_fabrication, _fabrication.Recipe))
        {
            SetFabricationStatus(BoxStationFabricationStatus.OutputBlocked);
            return;
        }
        int node = rooms.NodeAtCell(FabricationCell);
        if (node < 0)
        {
            SetFabricationStatus(BoxStationFabricationStatus.NoCooling);
            return;
        }
        var gas = rooms.Gas(node);
        double headroom = FabricationHeatingHeadroom(gas);
        double remaining = FabricationWork(_fabrication.Recipe) - _fabrication.WorkJoules;
        double requested = Math.Min(remaining, Math.Min(FabricationWatts * dt, headroom));
        var changed = gas;
        if (requested <= 0 || !changed.TryAddThermalEnergy(requested) || !ValidFabricationSink(changed))
        {
            SetFabricationStatus(BoxStationFabricationStatus.NoCooling);
            return;
        }
        double paid = power.Draw(_fabricationArea, BoxStationPowerChannel.Equipment, requested);
        if (paid <= 0)
        {
            SetFabricationStatus(BoxStationFabricationStatus.NoPower);
            return;
        }
        if (paid < requested)
        {
            changed = gas;
            if (!changed.TryAddThermalEnergy(paid) || !ValidFabricationSink(changed))
                throw new InvalidOperationException("Station fabricator rejected prevalidated thermal energy.");
        }
        rooms.Gas(node) = changed;
        double work = _fabrication.WorkJoules + paid;
        if (work >= FabricationWork(_fabrication.Recipe))
        {
            bool line = _fabrication.Recipe == BoxStationFabricationRecipe.Line;
            _fabrication = _fabrication with
            {
                Steel = (ushort)(_fabrication.Steel - 1),
                Cable = (ushort)(_fabrication.Cable - (line ? 0 : 2)),
                LineStock = (ushort)(_fabrication.LineStock + (line ? 1 : 0)),
                CircuitStock = (ushort)(_fabrication.CircuitStock + (line ? 0 : 1)),
                ProducedLine = _fabrication.ProducedLine + (line ? 1 : 0),
                ProducedCircuit = _fabrication.ProducedCircuit + (line ? 0 : 1),
                Recipe = BoxStationFabricationRecipe.None, WorkJoules = 0, Status = BoxStationFabricationStatus.Idle
            };
        }
        else
        {
            _fabrication = _fabrication with { WorkJoules = work,
                Status = paid < requested ? BoxStationFabricationStatus.NoPower : BoxStationFabricationStatus.Working };
        }
        _materialsDirty = true;
    }

    private void SetFabricationStatus(BoxStationFabricationStatus status)
    {
        if (_fabrication.Status == status) return;
        _fabrication = _fabrication with { Status = status };
        _materialsDirty = true;
    }

    private static bool FabricationOutputFull(BoxStationFabricationState state, BoxStationFabricationRecipe recipe)
        => recipe == BoxStationFabricationRecipe.Line ? state.LineStock >= FabricationLineCapacity : state.CircuitStock >= FabricationCircuitCapacity;

    private static bool ValidFabricationSink(StationGas gas)
        => gas.HeatCapacity > 0 && gas.TryAddThermalEnergy(0)
            && gas.Pressure <= BoxStationSystems.MaximumPressure
            && gas.Moles <= gas.Volume * BoxStationSystems.MaximumMolesPerCubicMeter
            && gas.TemperatureKelvin <= StationGas.MaximumTemperature;

    private static double FabricationHeatingHeadroom(StationGas gas)
    {
        if (!ValidFabricationSink(gas)) return 0;
        double limit = Math.Min(StationGas.MaximumTemperature,
            BoxStationSystems.MaximumPressure * gas.Volume / (gas.Moles * StationGas.GasConstant));
        // Match the plumbing thermal margin before paying for work at a gas limit.
        return Math.Max(0, gas.HeatCapacity * limit - gas.ThermalJoules) * (1 - 1e-12);
    }

    internal static bool ValidFabrication(BoxStationLayout layout, BoxStationFabricationState state)
    {
        if (!TryFabricationDefinition(layout, out _) || state.Machine != FabricationMachine
            || state.Steel > FabricationSteelCapacity || state.Cable > FabricationCableCapacity
            || state.LineStock > FabricationLineCapacity || state.CircuitStock > FabricationCircuitCapacity
            || state.ProducedLine < 0 || state.ProducedCircuit < 0 || state.ExportedLine < 0 || state.ExportedCircuit < 0
            || state.LineStock + (long)state.ExportedLine > state.ProducedLine
            || state.CircuitStock + (long)state.ExportedCircuit > state.ProducedCircuit
            || state.Recipe > BoxStationFabricationRecipe.Circuit || state.Status > BoxStationFabricationStatus.OutputBlocked
            || !double.IsFinite(state.WorkJoules) || state.WorkJoules < 0) return false;
        long steel = GetConstructionBudget(layout).Steel;
        foreach (var source in GetSources(layout)) steel += source.Sheets;
        var materials = LayoutMaterials.GetValue(layout, BuildMaterialDefinitions);
        if (state.Steel + (long)state.ProducedLine + state.ProducedCircuit > steel
            || state.Cable + 2L * state.ProducedCircuit > materials.CableBudget) return false;
        if (state.Recipe == BoxStationFabricationRecipe.None)
            return state.WorkJoules == 0 && state.Status == BoxStationFabricationStatus.Idle;
        return state.Steel >= 1 && (state.Recipe != BoxStationFabricationRecipe.Circuit || state.Cable >= 2)
            && state.WorkJoules < FabricationWork(state.Recipe) && state.Status != BoxStationFabricationStatus.Idle;
    }
}
