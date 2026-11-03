// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Modstoke.Client;

internal enum BoxStationStructureKind { Window, Grille }

internal readonly record struct BoxStationStructure(int Id, int SourceObject, int Cell, int Direction,
    BoxStationStructureKind Kind, byte InitialIntegrity, bool Tinted, bool Plasma);

internal sealed partial class BoxStationHull
{
    internal const byte BrokenGrilleIntegrity = 40;
    // Source integrity sets relative strength; joules per point are a 3D impact adaptation, not source armor.
    private const double StructureJoulesPerPoint = 2_500;
    private static readonly ConditionalWeakTable<BoxStationLayout, StructureDefinitions> LayoutStructures = new();

    private sealed class StructureDefinitions
    {
        internal required ImmutableArray<BoxStationStructure> Structures { get; init; }
        internal required ImmutableArray<BoxStationStructure>[] Cells { get; init; }
        internal required int[] Indices { get; init; }
        internal required ImmutableArray<double> BreakJoules { get; init; }
        internal required BoxStationBarrier[] RetainedBarriers { get; init; }
        internal required BoxStationCellFlags[] RetainedFlags { get; init; }
    }

    internal static ImmutableArray<BoxStationStructure> GetStructures(BoxStationLayout layout)
        => LayoutStructures.GetValue(layout, BuildStructureDefinitions).Structures;

    internal static ImmutableArray<BoxStationStructure> StructuresAt(BoxStationLayout layout, int cell)
        => cell < 0 || cell >= layout.Cells.Length ? ImmutableArray<BoxStationStructure>.Empty
            : LayoutStructures.GetValue(layout, BuildStructureDefinitions).Cells[cell];

    internal static int StructureTarget(BoxStationLayout layout, int id)
    {
        var definitions = LayoutStructures.GetValue(layout, BuildStructureDefinitions);
        return id >= 0 && id < definitions.Indices.Length && definitions.Indices[id] >= 0
            ? layout.Cells.Length * 3 + id : -1;
    }

    internal static bool TryStructure(BoxStationLayout layout, int target, out BoxStationStructure structure)
    {
        structure = default;
        int first = layout.Cells.Length * 3;
        if (target < first) return false;
        int id = target - first;
        var definitions = LayoutStructures.GetValue(layout, BuildStructureDefinitions);
        if (id >= definitions.Indices.Length || definitions.Indices[id] < 0) return false;
        structure = definitions.Structures[definitions.Indices[id]];
        return true;
    }

    internal byte StructureIntegrity(int id)
    {
        var definitions = LayoutStructures.GetValue(_layout, BuildStructureDefinitions);
        if (id < 0 || id >= definitions.Indices.Length || definitions.Indices[id] < 0)
            throw new ArgumentOutOfRangeException(nameof(id));
        int index = FindPanel(_layout.Cells.Length * 3 + id);
        return index >= 0 ? _state.Panels[index].Integrity : definitions.Structures[definitions.Indices[id]].InitialIntegrity;
    }

    internal bool StructureSolid(BoxStationStructure structure)
        => StructureIntegrity(structure.Id) > (structure.Kind == BoxStationStructureKind.Grille ? BrokenGrilleIntegrity : 0);

    internal BoxStationBarrier EffectiveBarrier(int cell, int direction)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cell);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(cell, _layout.Cells.Length);
        ArgumentOutOfRangeException.ThrowIfNegative(direction);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(direction, 4);
        var original = SourceBarrier(_layout.Cells[cell], direction);
        var definitions = LayoutStructures.GetValue(_layout, BuildStructureDefinitions);
        var barrier = (original & ~(BoxStationBarrier.Window | BoxStationBarrier.Grille))
            | definitions.RetainedBarriers[cell * 4 + direction] | LiveStructureBarrier(cell, direction);
        int neighbor = StructureNeighbor(_layout, cell, direction);
        if (neighbor >= 0) barrier |= LiveStructureBarrier(neighbor, (direction + 2) % 4);
        bool wall = (_layout.Cells[cell].Flags & BoxStationCellFlags.Wall) != 0;
        bool neighborWall = neighbor >= 0 && (_layout.Cells[neighbor].Flags & BoxStationCellFlags.Wall) != 0;
        if ((wall || neighborWall) && (!wall || WallBreached(cell)) && (!neighborWall || WallBreached(neighbor)))
            barrier &= ~BoxStationBarrier.Wall;
        if (ConstructedWall(cell) || neighbor >= 0 && ConstructedWall(neighbor)) barrier |= BoxStationBarrier.Wall;
        return barrier;
    }

    internal BoxStationCellFlags EffectiveFlags(int cell)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cell);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(cell, _layout.Cells.Length);
        var original = _layout.Cells[cell].Flags;
        var definitions = LayoutStructures.GetValue(_layout, BuildStructureDefinitions);
        var flags = (original & ~(BoxStationCellFlags.FullWindow | BoxStationCellFlags.Grille))
            | definitions.RetainedFlags[cell];
        foreach (var structure in definitions.Cells[cell])
        {
            if (!StructureSolid(structure)) continue;
            if (structure.Kind == BoxStationStructureKind.Grille) flags |= BoxStationCellFlags.Grille;
            else if (structure.Direction == 5) flags |= BoxStationCellFlags.FullWindow;
        }
        if ((original & BoxStationCellFlags.Wall) != 0 && WallBreached(cell))
            flags = (flags & ~BoxStationCellFlags.Wall) | BoxStationCellFlags.Floor | BoxStationCellFlags.Roof;
        if (ConstructedWall(cell)) flags |= BoxStationCellFlags.Wall;
        // Full-tile floor windows retain the same enclosed height as the surrounding 3D deck.
        if (FloorBackedWindow(original)) flags |= BoxStationCellFlags.Roof;
        return flags;
    }

    private BoxStationBarrier LiveStructureBarrier(int cell, int direction)
    {
        int facing = direction switch { 0 => 1, 1 => 4, 2 => 2, _ => 8 };
        var barrier = BoxStationBarrier.None;
        foreach (var structure in StructuresAt(_layout, cell))
        {
            if ((structure.Direction != 5 && structure.Direction != facing) || !StructureSolid(structure)) continue;
            barrier |= structure.Kind == BoxStationStructureKind.Window ? BoxStationBarrier.Window : BoxStationBarrier.Grille;
        }
        return barrier;
    }

    private static double StructureBreakJoules(BoxStationLayout layout, int id)
    {
        var definitions = LayoutStructures.GetValue(layout, BuildStructureDefinitions);
        return definitions.BreakJoules[definitions.Indices[id]];
    }

    private static StructureDefinitions BuildStructureDefinitions(BoxStationLayout layout)
    {
        const string window = "/obj/structure/window", grille = "/obj/structure/grille",
            spawner = "/obj/effect/spawner/structure/window";
        var structures = ImmutableArray.CreateBuilder<BoxStationStructure>();
        var energies = ImmutableArray.CreateBuilder<double>();
        var cellLists = new List<BoxStationStructure>?[layout.Cells.Length];
        var indices = new int[layout.Objects.Length * 2];
        Array.Fill(indices, -1);
        var knownBarriers = new BoxStationBarrier[layout.Cells.Length * 4];
        var retainedBarriers = new BoxStationBarrier[knownBarriers.Length];
        var knownFlags = new BoxStationCellFlags[layout.Cells.Length];
        var retainedFlags = new BoxStationCellFlags[layout.Cells.Length];
        foreach (var source in layout.Objects)
        {
            string type = layout.Types[source.Type];
            bool spawned = SourceFamily(type, spawner), glass = SourceFamily(type, window), grating = SourceFamily(type, grille);
            if (!spawned && !glass && !grating) continue;
            int points = type switch
            {
                window => 25,
                window + "/reinforced" or window + "/reinforced/tinted" => 75,
                window + "/plasma/reinforced" => 500,
                spawner or window + "/fulltile" => 50,
                spawner + "/reinforced" or window + "/reinforced/fulltile" => 150,
                spawner + "/reinforced/tinted" or window + "/reinforced/tinted/fulltile" => 75,
                spawner + "/plasma/reinforced" or window + "/plasma/reinforced/fulltile" => 1000,
                grille or grille + "/broken" => 50,
                _ => 0
            };
            if (points == 0 || !StructureSourceDefaults(source, type, spawned))
            {
                // An unsupported contributor must survive damage to a recognized object sharing its edge.
                var barrier = (spawned || glass ? BoxStationBarrier.Window : BoxStationBarrier.None)
                    | (spawned || grating ? BoxStationBarrier.Grille : BoxStationBarrier.None);
                MarkStructureEdges(layout, retainedBarriers, source.Cell, 5, barrier);
                retainedFlags[source.Cell] |= (spawned || glass ? BoxStationCellFlags.FullWindow : BoxStationCellFlags.None)
                    | (spawned || grating ? BoxStationCellFlags.Grille : BoxStationCellFlags.None);
                continue;
            }
            bool full = spawned || type.EndsWith("/fulltile", StringComparison.Ordinal);
            if (spawned || glass)
            {
                Add(new(source.Id * 2, source.Id, source.Cell, full ? 5 : source.Direction,
                    BoxStationStructureKind.Window, Capacity, type.Contains("/tinted", StringComparison.Ordinal),
                    type.Contains("/plasma/", StringComparison.Ordinal)), points);
            }
            if (spawned || grating)
                Add(new(source.Id * 2 + 1, source.Id, source.Cell, 5, BoxStationStructureKind.Grille,
                    type == grille + "/broken" ? BrokenGrilleIntegrity : Capacity, false, false), 50);
        }
        var cells = new ImmutableArray<BoxStationStructure>[layout.Cells.Length];
        for (int cell = 0; cell < cells.Length; cell++)
        {
            cells[cell] = cellLists[cell] is { } list ? ImmutableArray.CreateRange(list) : ImmutableArray<BoxStationStructure>.Empty;
            var original = layout.Cells[cell];
            retainedFlags[cell] = original.Flags & (BoxStationCellFlags.FullWindow | BoxStationCellFlags.Grille)
                & (~knownFlags[cell] | retainedFlags[cell]);
            for (int direction = 0; direction < 4; direction++)
            {
                int edge = cell * 4 + direction;
                retainedBarriers[edge] = SourceBarrier(original, direction) & (BoxStationBarrier.Window | BoxStationBarrier.Grille)
                    & (~knownBarriers[edge] | retainedBarriers[edge]);
            }
        }
        return new()
        {
            Structures = structures.ToImmutable(), Cells = cells, Indices = indices, BreakJoules = energies.ToImmutable(),
            RetainedBarriers = retainedBarriers, RetainedFlags = retainedFlags
        };

        void Add(BoxStationStructure structure, int points)
        {
            indices[structure.Id] = structures.Count;
            structures.Add(structure);
            energies.Add(points * StructureJoulesPerPoint);
            (cellLists[structure.Cell] ??= new()).Add(structure);
            if (structure.Kind == BoxStationStructureKind.Grille && structure.InitialIntegrity <= BrokenGrilleIntegrity) return;
            var barrier = structure.Kind == BoxStationStructureKind.Window ? BoxStationBarrier.Window : BoxStationBarrier.Grille;
            MarkStructureEdges(layout, knownBarriers, structure.Cell, structure.Direction, barrier);
            if (structure.Kind == BoxStationStructureKind.Grille) knownFlags[structure.Cell] |= BoxStationCellFlags.Grille;
            else if (structure.Direction == 5) knownFlags[structure.Cell] |= BoxStationCellFlags.FullWindow;
        }
    }

    private static bool StructureSourceDefaults(BoxStationObject source, string type, bool spawned)
    {
        if (!spawned && source.Direction is not (1 or 4 or 2 or 8 or 5)) return false;
        string raw = source.Raw.Trim();
        int direction = spawned ? 0 : 2;
        if (raw == type) return source.Direction == direction || type.EndsWith("/fulltile", StringComparison.Ordinal) && source.Direction == 5;
        if (!raw.StartsWith(type + "{", StringComparison.Ordinal) || !raw.EndsWith('}')) return false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (string setting in raw.Substring(type.Length + 1, raw.Length - type.Length - 2).Split(';'))
        {
            string[] pair = setting.Split('=');
            if (pair.Length != 2 || !names.Add(pair[0].Trim())) return false;
            string name = pair[0].Trim(), value = pair[1].Trim();
            if (name == "dir")
            {
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out direction)) return false;
            }
            else if (spawned ? name != "pixel_w" || value != "1"
                : !((name == "layer" && value == "2.9") || (name == "pixel_y" && value == "2"))) return false;
        }
        return source.Direction == direction;
    }

    private static bool SourceFamily(string type, string family)
        => type == family || type.StartsWith(family + "/", StringComparison.Ordinal);

    private static void MarkStructureEdges(BoxStationLayout layout, BoxStationBarrier[] barriers, int cell, int facing,
        BoxStationBarrier barrier)
    {
        for (int direction = 0; direction < 4; direction++)
        {
            int sourceDirection = direction switch { 0 => 1, 1 => 4, 2 => 2, _ => 8 };
            if (facing != 5 && facing != sourceDirection) continue;
            barriers[cell * 4 + direction] |= barrier;
            int neighbor = StructureNeighbor(layout, cell, direction);
            if (neighbor >= 0) barriers[neighbor * 4 + (direction + 2) % 4] |= barrier;
        }
    }

    private static BoxStationBarrier SourceBarrier(BoxStationCell cell, int direction)
        => direction switch { 0 => cell.North, 1 => cell.East, 2 => cell.South, _ => cell.West };

    private static int StructureNeighbor(BoxStationLayout layout, int cell, int direction)
    {
        int x = cell % layout.Width, row = cell / layout.Width;
        return direction switch
        {
            0 when row > 0 => cell - layout.Width,
            1 when x + 1 < layout.Width => cell + 1,
            2 when row + 1 < layout.Height => cell + layout.Width,
            3 when x > 0 => cell - 1,
            _ => -1
        };
    }
}
