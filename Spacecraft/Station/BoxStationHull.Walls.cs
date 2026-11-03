// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace Modstoke.Client;

internal sealed partial class BoxStationHull
{
    // Normalized integrity and effective rupture energies are 3D gameplay tuning, in joules.
    private const double OrdinaryWallBreakJoules = 1_500_000, ReinforcedWallBreakJoules = 3_000_000;
    private static readonly ConditionalWeakTable<BoxStationLayout, WallDefinitions> LayoutWalls = new();

    private sealed class WallDefinitions
    {
        internal required ImmutableArray<int> Cells { get; init; }
        internal required int[] Indices { get; init; }
        internal required ImmutableArray<BoxStationWallKind> Kinds { get; init; }
        internal required ImmutableArray<int> BuildableCells { get; init; }
        internal required bool[] Buildable { get; init; }
    }

    internal static ImmutableArray<int> WallCells(BoxStationLayout layout)
        => LayoutWalls.GetValue(layout, BuildWallDefinitions).Cells;

    internal static int WallIndex(BoxStationLayout layout, int cell)
        => cell < 0 || cell >= layout.Cells.Length ? -1 : LayoutWalls.GetValue(layout, BuildWallDefinitions).Indices[cell];

    internal static ImmutableArray<int> BuildableWallCells(BoxStationLayout layout)
        => LayoutWalls.GetValue(layout, BuildWallDefinitions).BuildableCells;

    internal static int WallTarget(BoxStationLayout layout, int cell)
    {
        if (cell < 0 || cell >= layout.Cells.Length) return -1;
        var definitions = LayoutWalls.GetValue(layout, BuildWallDefinitions);
        return definitions.Indices[cell] >= 0 || definitions.Buildable[cell] ? layout.Cells.Length * 2 + cell : -1;
    }

    internal static int WallCell(BoxStationLayout layout, int target)
    {
        int first = layout.Cells.Length * 2;
        if (target < first || target - first >= layout.Cells.Length) return -1;
        int cell = target - first;
        return WallTarget(layout, cell) >= 0 ? cell : -1;
    }

    internal bool WallBreached(int cell)
    {
        if (WallIndex(_layout, cell) < 0) return false;
        int target = _layout.Cells.Length * 2 + cell;
        int index = FindPanel(target);
        return index >= 0 && _state.Panels[index].Integrity == 0;
    }

    private bool ConstructedWall(int cell)
    {
        if (!LayoutWalls.GetValue(_layout, BuildWallDefinitions).Buildable[cell]) return false;
        int index = FindPanel(_layout.Cells.Length * 2 + cell);
        return index >= 0 && _state.Panels[index].Integrity > 0;
    }

    internal BoxStationWallKind WallKind(int target)
    {
        int index = FindPanel(target);
        return index >= 0 ? _state.Panels[index].WallKind : InitialWallKind(_layout, target);
    }

    internal static BoxStationWallKind InitialWallKind(BoxStationLayout layout, int target)
    {
        int cell = target - layout.Cells.Length * 2;
        if ((uint)cell >= (uint)layout.Cells.Length) return BoxStationWallKind.None;
        var definitions = LayoutWalls.GetValue(layout, BuildWallDefinitions);
        int index = definitions.Indices[cell];
        return index < 0 ? BoxStationWallKind.None : definitions.Kinds[index];
    }

    private static bool ValidWallKind(BoxStationLayout layout, int target, byte integrity, BoxStationWallKind kind)
    {
        var initial = InitialWallKind(layout, target);
        if (initial != BoxStationWallKind.None || WallCell(layout, target) < 0 || integrity == 0) return kind == initial;
        return kind is BoxStationWallKind.Steel or BoxStationWallKind.Reinforced;
    }

    internal bool RestoreWall(int target, byte expectedIntegrity, BoxStationWallKind kind)
    {
        if (expectedIntegrity >= Capacity || WallCell(_layout, target) < 0
            || _state.Revision == long.MaxValue || !ValidWallKind(_layout, target, Capacity, kind)) return false;
        int index = FindPanel(target);
        byte integrity = index < 0 ? InitialIntegrity(_layout, target) : _state.Panels[index].Integrity;
        if (integrity != expectedIntegrity || integrity > 0 && kind != WallKind(target)) return false;
        return SetPanel(index, new(target, Capacity, false, kind));
    }

    internal bool RemoveWall(int target, byte expectedIntegrity)
    {
        if (expectedIntegrity is 0 or > Capacity || WallCell(_layout, target) < 0
            || _state.Revision == long.MaxValue) return false;
        int index = FindPanel(target);
        byte integrity = index < 0 ? InitialIntegrity(_layout, target) : _state.Panels[index].Integrity;
        if (integrity != expectedIntegrity) return false;
        return SetPanel(index, new(target, 0, false, InitialWallKind(_layout, target)));
    }

    private static double WallBreakJoules(BoxStationWallKind kind)
        => kind == BoxStationWallKind.Reinforced ? ReinforcedWallBreakJoules : OrdinaryWallBreakJoules;

    private static WallDefinitions BuildWallDefinitions(BoxStationLayout layout)
    {
        var cells = ImmutableArray.CreateBuilder<int>();
        var kinds = ImmutableArray.CreateBuilder<BoxStationWallKind>();
        var indices = new int[layout.Cells.Length];
        Array.Fill(indices, -1);
        for (int cell = 0; cell < layout.Cells.Length; cell++)
        {
            var source = layout.Cells[cell];
            if ((source.Flags & BoxStationCellFlags.Wall) == 0) continue;
            var kind = layout.Types[source.TurfType] switch
            {
                "/turf/closed/wall" => BoxStationWallKind.Steel,
                "/turf/closed/wall/r_wall" => BoxStationWallKind.Reinforced,
                _ => BoxStationWallKind.None
            };
            if (kind == BoxStationWallKind.None) continue;
            indices[cell] = cells.Count;
            cells.Add(cell);
            kinds.Add(kind);
        }
        var buildable = new bool[layout.Cells.Length];
        const BoxStationCellFlags blocked = BoxStationCellFlags.Wall | BoxStationCellFlags.FullWindow
            | BoxStationCellFlags.Grille | BoxStationCellFlags.Catwalk | BoxStationCellFlags.Lattice;
        for (int cell = 0; cell < layout.Cells.Length; cell++)
        {
            var source = layout.Cells[cell];
            buildable[cell] = source.Zone >= 0 && (source.Flags & (BoxStationCellFlags.Floor | blocked)) == BoxStationCellFlags.Floor;
        }
        foreach (var door in layout.Doors) buildable[door.Cell] = false;
        foreach (var source in layout.Objects)
            if (buildable[source.Cell] && BlocksWallConstruction(layout.Types[source.Type], source.Raw)) buildable[source.Cell] = false;
        var buildableCells = ImmutableArray.CreateBuilder<int>();
        for (int cell = 0; cell < buildable.Length; cell++)
            if (buildable[cell]) buildableCells.Add(cell);
        return new()
        {
            Cells = cells.ToImmutable(), Indices = indices, Kinds = kinds.ToImmutable(),
            BuildableCells = buildableCells.ToImmutable(), Buildable = buildable
        };
    }

    private static bool BlocksWallConstruction(string type, string raw)
    {
        // Buried utilities remain beneath the slab; surface devices and source fixtures need their volume.
        if (WallSourceFamily(type, "/obj/machinery"))
            return type != "/obj/machinery/power/terminal"
                && !WallSourceFamily(type, "/obj/machinery/atmospherics/pipe/simple")
                && !WallSourceFamily(type, "/obj/machinery/atmospherics/pipe/manifold")
                && !WallSourceFamily(type, "/obj/machinery/atmospherics/pipe/manifold4w")
                && !WallSourceFamily(type, "/obj/machinery/atmospherics/pipe/layer_manifold");
        if (WallSourceFamily(type, "/obj/structure"))
            return !WallSourceFamily(type, "/obj/structure/cable") && !WallSourceFamily(type, "/obj/structure/disposalpipe")
                && !(type == "/obj/structure/girder" && raw == type);
        return WallSourceFamily(type, "/obj/effect/spawner/structure") || type == "/obj/effect/spawner/xmastree";
    }

    private static bool WallSourceFamily(string type, string parent)
        => type == parent || type.StartsWith(parent + "/", StringComparison.Ordinal);
}
