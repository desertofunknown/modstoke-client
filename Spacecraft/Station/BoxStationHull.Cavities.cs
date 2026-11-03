// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace Modstoke.Client;

internal sealed partial class BoxStationHull
{
    private static readonly ConditionalWeakTable<BoxStationLayout, CavityDefinitions> LayoutCavities = new();

    private sealed class CavityDefinitions
    {
        internal required ImmutableArray<int> Cells { get; init; }
        internal required int[] Indices { get; init; }
    }

    internal static ImmutableArray<int> CavityCells(BoxStationLayout layout)
        => LayoutCavities.GetValue(layout, BuildCavityDefinitions).Cells;

    internal static int CavityIndex(BoxStationLayout layout, int cell)
        => cell < 0 || cell >= layout.Cells.Length ? -1 : LayoutCavities.GetValue(layout, BuildCavityDefinitions).Indices[cell];

    internal bool CavityActive(int cell)
        => CavityIndex(_layout, cell) >= 0
            && ((_layout.Cells[cell].Flags & BoxStationCellFlags.Wall) == 0 || WallBreached(cell));

    private static CavityDefinitions BuildCavityDefinitions(BoxStationLayout layout)
    {
        // Appending window cells preserves the gas node identities of existing wall cavities.
        var cells = ImmutableArray.CreateBuilder<int>();
        cells.AddRange(WallCells(layout));
        for (int cell = 0; cell < layout.Cells.Length; cell++)
            if (FloorBackedWindow(layout.Cells[cell].Flags)) cells.Add(cell);
        var indices = new int[layout.Cells.Length];
        Array.Fill(indices, -1);
        for (int index = 0; index < cells.Count; index++) indices[cells[index]] = index;
        return new() { Cells = cells.ToImmutable(), Indices = indices };
    }

    private static bool FloorBackedWindow(BoxStationCellFlags flags)
        => (flags & (BoxStationCellFlags.Floor | BoxStationCellFlags.FullWindow | BoxStationCellFlags.Wall))
            == (BoxStationCellFlags.Floor | BoxStationCellFlags.FullWindow);
}
