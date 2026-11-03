// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Runtime.CompilerServices;
using Vector3 = System.Numerics.Vector3;

namespace Modstoke.Client;

internal sealed partial class BoxStationHullRepair
{
    private static readonly ConditionalWeakTable<BoxStationLayout, ConstructionDefinitions> LayoutConstruction = new();

    private sealed class ConstructionDefinitions
    {
        internal required ImmutableArray<BoxStationConstructionDefinition> Sources { get; init; }
        internal required int[] Indices { get; init; }
        internal required BoxStationConstructionBudget Budget { get; init; }
    }

    internal static ImmutableArray<BoxStationConstructionDefinition> GetConstructionDefinitions(BoxStationLayout layout)
        => LayoutConstruction.GetValue(layout, BuildConstructionDefinitions).Sources;

    internal static BoxStationConstructionBudget GetConstructionBudget(BoxStationLayout layout)
        => LayoutConstruction.GetValue(layout, BuildConstructionDefinitions).Budget;

    internal static bool TryConstruction(BoxStationLayout layout, int target, out BoxStationConstructionDefinition definition)
    {
        definition = default;
        int cell = target - layout.Cells.Length * 2;
        if (cell < 0 || cell >= layout.Cells.Length) return false;
        var definitions = LayoutConstruction.GetValue(layout, BuildConstructionDefinitions);
        int index = definitions.Indices[cell];
        if (index < 0) return false;
        definition = definitions.Sources[index];
        return true;
    }

    internal static BoxStationConstruction ConstructionAt(BoxStationLayout layout, BoxStationMaterialState state, int target)
    {
        if (!TryConstruction(layout, target, out var definition)) return default;
        int low = 0, high = state.Construction.IsDefault ? -1 : state.Construction.Length - 1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            var value = state.Construction[middle];
            if (value.Cell == definition.Cell) return value;
            if (value.Cell < definition.Cell) low = middle + 1;
            else high = middle - 1;
        }
        return InitialConstruction(definition);
    }

    private static BoxStationConstruction InitialConstruction(BoxStationConstructionDefinition definition)
    {
        var stage = definition.OriginalWall ? BoxStationFrameStage.Wall
            : definition.OriginalGirder ? BoxStationFrameStage.Girder : BoxStationFrameStage.Empty;
        var kind = definition.OriginalWall ? definition.Reinforced ? BoxStationWallKind.Reinforced : BoxStationWallKind.Steel : BoxStationWallKind.None;
        return new(definition.Cell, stage, 0, stage == BoxStationFrameStage.Empty ? (byte)0 : BoxStationHull.Capacity, 0, 0, kind);
    }

    private static BoxStationConstructionBudget ConstructionUnits(BoxStationConstruction value)
        => value.Stage switch
        {
            BoxStationFrameStage.Empty => default,
            BoxStationFrameStage.Wall => value.WallKind == BoxStationWallKind.Reinforced ? new(2, 2) : new(4, 0),
            BoxStationFrameStage.ReinforcedGirder or BoxStationFrameStage.ReinforcedStruts => new(2, 1),
            _ => new(2, 0)
        };

    private static BoxStationConstructionBudget AddConstructionUnits(BoxStationConstructionBudget a, BoxStationConstructionBudget b)
        => new(checked(a.Steel + b.Steel), checked(a.Plasteel + b.Plasteel));

    private static ConstructionDefinitions BuildConstructionDefinitions(BoxStationLayout layout)
    {
        var sources = new List<BoxStationConstructionDefinition>();
        foreach (int cell in BoxStationHull.WallCells(layout))
        {
            var source = layout.Cells[cell];
            var position = new Vector3((source.X - layout.Width / 2) * BoxStationLayout.TileSize, 0,
                (layout.Height / 2 - source.Row) * BoxStationLayout.TileSize);
            sources.Add(new(cell, layout.Cells.Length * 2 + cell, true,
                layout.Types[source.TurfType] == "/turf/closed/wall/r_wall", position, false));
        }
        foreach (var source in layout.Objects)
        {
            string type = layout.Types[source.Type];
            if (type != "/obj/structure/girder") continue;
            var cell = layout.Cells[source.Cell];
            var position = new Vector3((cell.X - layout.Width / 2) * BoxStationLayout.TileSize, 0,
                (layout.Height / 2 - cell.Row) * BoxStationLayout.TileSize) + SourceOffset(source, type);
            sources.Add(new(source.Cell, layout.Cells.Length * 2 + source.Cell, false, false, position, true));
        }
        var occupied = new bool[layout.Cells.Length];
        foreach (var source in sources) occupied[source.Cell] = true;
        foreach (int cell in BoxStationHull.BuildableWallCells(layout))
        {
            if (occupied[cell]) continue;
            occupied[cell] = true;
            var source = layout.Cells[cell];
            var position = new Vector3((source.X - layout.Width / 2) * BoxStationLayout.TileSize, 0,
                (layout.Height / 2 - source.Row) * BoxStationLayout.TileSize);
            sources.Add(new(cell, layout.Cells.Length * 2 + cell, false, false, position, false));
        }
        sources.Sort(static (a, b) => a.Cell.CompareTo(b.Cell));
        var indices = new int[layout.Cells.Length];
        Array.Fill(indices, -1);
        var budget = new BoxStationConstructionBudget();
        for (int i = 0; i < sources.Count; i++)
        {
            var source = sources[i];
            if (indices[source.Cell] >= 0)
                throw new InvalidDataException($"Station construction cell {source.Cell} has more than one original frame.");
            indices[source.Cell] = i;
            budget = AddConstructionUnits(budget, ConstructionUnits(InitialConstruction(source)));
        }
        foreach (var source in GetMaterialSources(layout))
            if (source.Material == BoxStationMaterial.Plasteel)
                budget = AddConstructionUnits(budget, new(0, source.Count));
        return new() { Sources = ImmutableArray.CreateRange(sources), Indices = indices, Budget = budget };
    }
}
