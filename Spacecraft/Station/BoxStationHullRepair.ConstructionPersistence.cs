// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Modstoke.Client;

internal sealed partial class BoxStationHullRepair
{
    private static bool ValidConstruction(BoxStationLayout layout, BoxStationConstructionDefinition definition, BoxStationConstruction value)
    {
        if (value.Cell != definition.Cell || value.Stage > BoxStationFrameStage.Wall
            || value.Integrity > BoxStationHull.Capacity
            || (value.Integrity == 0) != (value.Stage == BoxStationFrameStage.Empty)) return false;
        if (value.Stage == BoxStationFrameStage.Wall)
            return BoxStationHull.WallTarget(layout, definition.Cell) >= 0
                && value.WallKind is BoxStationWallKind.Steel or BoxStationWallKind.Reinforced
                && (!definition.OriginalWall || value.WallKind == (definition.Reinforced ? BoxStationWallKind.Reinforced : BoxStationWallKind.Steel))
                && value.WorkStage <= (value.WallKind == BoxStationWallKind.Reinforced ? 6 : 0);
        return value.WallKind == BoxStationWallKind.None && value.WorkStage == 0
            && (value.Stage is not (BoxStationFrameStage.ReinforcedStruts or BoxStationFrameStage.ReinforcedGirder)
            || !definition.OriginalWall || definition.Reinforced);
    }

    internal static bool ValidConstructionStates(BoxStationLayout layout, ImmutableArray<BoxStationConstruction> states)
    {
        var definitions = LayoutConstruction.GetValue(layout, BuildConstructionDefinitions);
        if (states.IsDefault || states.Length > definitions.Sources.Length) return false;
        int previous = -1;
        foreach (var value in states)
        {
            if (value.Cell <= previous || value.Cell >= definitions.Indices.Length || definitions.Indices[value.Cell] < 0) return false;
            previous = value.Cell;
            var definition = definitions.Sources[definitions.Indices[value.Cell]];
            if (!ValidConstruction(layout, definition, value) || value == InitialConstruction(definition)) return false;
        }
        return true;
    }

    internal static bool ValidConstructionStates(BoxStationLayout layout, ImmutableArray<BoxStationConstruction> states, BoxStationHullState hull)
    {
        if (!BoxStationHull.ValidState(layout, hull) || !ValidConstructionStates(layout, states)) return false;
        int row = 0, panel = 0;
        foreach (var definition in GetConstructionDefinitions(layout))
        {
            while (row < states.Length && states[row].Cell < definition.Cell) row++;
            var value = row < states.Length && states[row].Cell == definition.Cell ? states[row] : InitialConstruction(definition);
            if (BoxStationHull.WallTarget(layout, definition.Cell) < 0) continue;
            while (panel < hull.Panels.Length && hull.Panels[panel].Target < definition.Target) panel++;
            byte integrity = panel < hull.Panels.Length && hull.Panels[panel].Target == definition.Target
                ? hull.Panels[panel].Integrity : BoxStationHull.InitialIntegrity(layout, definition.Target);
            if (integrity != (value.Stage == BoxStationFrameStage.Wall ? value.Integrity : 0)) return false;
            var kind = panel < hull.Panels.Length && hull.Panels[panel].Target == definition.Target
                ? hull.Panels[panel].WallKind : BoxStationHull.InitialWallKind(layout, definition.Target);
            if (value.Stage == BoxStationFrameStage.Wall && value.WallKind != kind) return false;
        }
        return true;
    }

    private ImmutableArray<BoxStationConstruction> CaptureConstruction()
    {
        var rows = ImmutableArray.CreateBuilder<BoxStationConstruction>();
        for (int i = 0; i < _construction.Length; i++)
            if (_construction[i] != InitialConstruction(_constructionDefinitions.Sources[i])) rows.Add(_construction[i]);
        return rows.ToImmutable();
    }

    private static BoxStationConstructionBudget InstalledConstruction(BoxStationConstruction[] states)
    {
        var installed = new BoxStationConstructionBudget();
        for (int i = 0; i < states.Length; i++)
            installed = AddConstructionUnits(installed, ConstructionUnits(states[i]));
        return installed;
    }

    private static BoxStationConstructionBudget InstalledConstruction(BoxStationLayout layout, ImmutableArray<BoxStationConstruction> states)
    {
        var installed = new BoxStationConstructionBudget();
        int row = 0;
        foreach (var definition in GetConstructionDefinitions(layout))
        {
            while (row < states.Length && states[row].Cell < definition.Cell) row++;
            var value = row < states.Length && states[row].Cell == definition.Cell ? states[row] : InitialConstruction(definition);
            installed = AddConstructionUnits(installed, ConstructionUnits(value));
        }
        return installed;
    }

    private static bool BoundedConstructionBudget(BoxStationConstructionBudget budget, BoxStationConstructionBudget maximum)
        => budget.Steel >= 0 && budget.Steel <= maximum.Steel && budget.Plasteel >= 0 && budget.Plasteel <= maximum.Plasteel;

    private static bool ValidConstructionMaterials(BoxStationLayout layout, BoxStationMaterialState state)
    {
        int maximumImports = 0;
        foreach (var source in GetSources(layout)) maximumImports += source.Sheets;
        if (state.ImportedSteel < 0 || state.ImportedSteel > maximumImports
            || !BoundedConstructionBudget(state.ConstructionBudget, GetConstructionBudget(layout))
            || !ValidConstructionStates(layout, state.Construction) || !ValidFloors(layout, state.Floors)) return false;
        var entitlement = AddConstructionUnits(state.ConstructionBudget, new(state.ImportedSteel, 0));
        int coverings = GetFloorCells(layout).Length;
        if (!BoundedConstructionBudget(state.ConstructionInstalled, entitlement)
            || !BoundedConstructionBudget(state.ConstructionWaste, entitlement)
            || state.ExportedSteel < 0 || state.ExportedSteel > entitlement.Steel
            || state.ConsumedFloorTiles < 0 || state.ConsumedFloorTiles > 4L * entitlement.Steel + coverings
            || state.ConstructionInstalled != AddConstructionUnits(InstalledConstruction(layout, state.Construction),
                new(state.DoorConstruction.Assemblies.Length * 4, 0))) return false;
        long steel = (long)state.ConstructionInstalled.Steel + state.ConstructionWaste.Steel
            + state.Fabrication.Steel + state.Fabrication.ProducedLine + state.Fabrication.ProducedCircuit + state.ExportedSteel;
        long plasteel = (long)state.ConstructionInstalled.Plasteel + state.ConstructionWaste.Plasteel;
        long floorTiles = state.ConsumedFloorTiles;
        var sources = GetMaterialSources(layout);
        for (int i = 0; i < state.Stocks.Length; i++)
        {
            if (sources[i].Material == BoxStationMaterial.Steel) steel += state.Stocks[i];
            else if (sources[i].Material == BoxStationMaterial.Plasteel) plasteel += state.Stocks[i];
        }
        foreach (var bundle in state.Bundles)
        {
            if (bundle.Material == BoxStationMaterial.Steel) steel += bundle.Count;
            else if (bundle.Material == BoxStationMaterial.Plasteel) plasteel += bundle.Count;
            else if (bundle.Material == BoxStationMaterial.FloorTiles) floorTiles += bundle.Count;
        }
        foreach (var value in state.Construction)
        {
            steel += value.SteelYield;
            plasteel += value.PlasteelYield;
        }
        return 4 * steel + floorTiles + coverings - state.Floors.Removed.Length + state.Floors.LostTiles
            == 4L * entitlement.Steel + coverings && plasteel == entitlement.Plasteel;
    }

    internal static bool ValidMaterialTransfers(BoxStationLayout layout, BoxStationRepairState repair, BoxStationMaterialState materials)
    {
        if (!ValidState(layout, repair) || !ValidMaterials(layout, materials)) return false;
        long exported = 0;
        foreach (ushort count in repair.ExportedSteel) exported += count;
        return exported == materials.ImportedSteel;
    }

    private static SavedBoxStationConstruction MigrateConstruction(BoxStationLayout layout, BoxStationHullState hull)
    {
        var rows = ImmutableArray.CreateBuilder<BoxStationConstruction>();
        var installed = new BoxStationConstructionBudget();
        int panel = 0;
        foreach (var definition in GetConstructionDefinitions(layout))
        {
            var value = SurvivingConstruction(definition, hull, ref panel);
            installed = AddConstructionUnits(installed, ConstructionUnits(value));
            if (value != InitialConstruction(definition)) rows.Add(value);
        }
        var budget = installed;
        foreach (var source in GetMaterialSources(layout))
            if (source.Material == BoxStationMaterial.Plasteel) budget = AddConstructionUnits(budget, new(0, source.Count));
        return new() { Budget = budget, Installed = installed, Waste = default, ImportedSteel = 0, Rows = rows.ToImmutable().AsSpan().ToArray() };
    }

    private static BoxStationConstruction SurvivingConstruction(BoxStationConstructionDefinition definition, BoxStationHullState hull, ref int panel)
    {
        var value = InitialConstruction(definition);
        if (!definition.OriginalWall) return value;
        while (panel < hull.Panels.Length && hull.Panels[panel].Target < definition.Target) panel++;
        byte integrity = panel < hull.Panels.Length && hull.Panels[panel].Target == definition.Target
            ? hull.Panels[panel].Integrity : BoxStationHull.Capacity;
        return value with { Stage = integrity == 0 ? BoxStationFrameStage.Empty : BoxStationFrameStage.Wall, Integrity = integrity,
            WallKind = integrity == 0 ? BoxStationWallKind.None : value.WallKind };
    }
}

internal sealed class SavedBoxStationConstruction
{
    [JsonRequired]
    public BoxStationConstructionBudget Budget { get; set; }
    [JsonRequired]
    public BoxStationConstructionBudget Installed { get; set; }
    [JsonRequired]
    public BoxStationConstructionBudget Waste { get; set; }
    [JsonRequired]
    public int ImportedSteel { get; set; }
    [JsonRequired]
    public BoxStationConstruction[]? Rows { get; set; }
    private int? _consumedFloorTiles;
    public int? ConsumedFloorTiles
    {
        get => _consumedFloorTiles;
        set { _consumedFloorTiles = value; ConsumedFloorTilesSpecified = true; }
    }
    internal bool ConsumedFloorTilesSpecified { get; private set; }
}
