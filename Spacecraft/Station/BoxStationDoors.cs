// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Modstoke.Client;

internal static class BoxStationDoors
{
    internal const int MaximumAssemblies = 64;
    private static readonly ConditionalWeakTable<BoxStationLayout, bool[]> Sites = new();

    internal static int Target(BoxStationLayout layout, int edge)
        => ValidEdge(layout, edge) ? layout.Cells.Length * 3 + layout.Objects.Length * 2 + edge : -1;

    internal static int EdgeFromTarget(BoxStationLayout layout, int target)
    {
        int edge = target - layout.Cells.Length * 3 - layout.Objects.Length * 2;
        return ValidEdge(layout, edge) ? edge : -1;
    }

    internal static int Direction(int edge) => edge & 1;

    internal static bool TryCells(BoxStationLayout layout, int edge, out int first, out int second)
    {
        first = second = -1;
        if ((uint)edge >= (uint)layout.Cells.Length * 2) return false;
        int cell = edge / 2;
        if (Direction(edge) == 0)
        {
            if (cell < layout.Width) return false;
            second = cell - layout.Width;
        }
        else
        {
            if (cell % layout.Width == layout.Width - 1) return false;
            second = cell + 1;
        }
        first = cell;
        return true;
    }

    internal static bool ValidEdge(BoxStationLayout layout, int edge)
    {
        if (!TryCells(layout, edge, out int first, out int second)) return false;
        var sites = Sites.GetValue(layout, BuildSites);
        if (!sites[first] || !sites[second]) return false;
        var barrier = Direction(edge) == 0 ? layout.Cells[first].North | layout.Cells[second].South
            : layout.Cells[first].East | layout.Cells[second].West;
        return barrier == BoxStationBarrier.None;
    }

    private static bool[] BuildSites(BoxStationLayout layout)
    {
        var sites = new bool[layout.Cells.Length];
        foreach (int cell in BoxStationHull.BuildableWallCells(layout)) sites[cell] = true;
        return sites;
    }

    internal static Vector3 Center(BoxStationLayout layout, int edge)
    {
        if (!TryCells(layout, edge, out int first, out int second)) throw new ArgumentOutOfRangeException(nameof(edge));
        return (layout.CellPosition(first) + layout.CellPosition(second)) * .5f;
    }

    internal static ImmutableArray<int> CompleteEdges(BoxStationDoorConstructionState state)
    {
        if (state.Assemblies.IsDefault) return default;
        var edges = ImmutableArray.CreateBuilder<int>();
        foreach (var assembly in state.Assemblies)
            if (assembly.Stage == BoxStationDoorStage.Complete) edges.Add(assembly.Edge);
        return edges.ToImmutable();
    }

    internal static bool ValidEdges(BoxStationLayout layout, ImmutableArray<int> edges)
    {
        if (edges.IsDefault || edges.Length > MaximumAssemblies) return false;
        int previous = -1;
        foreach (int edge in edges)
        {
            if (edge <= previous || !ValidEdge(layout, edge)) return false;
            previous = edge;
        }
        return true;
    }

    internal static int Index(BoxStationLayout layout, ImmutableArray<int> edges, int id)
    {
        if ((uint)id < (uint)layout.Doors.Length) return id;
        int edge = id - layout.Doors.Length;
        int low = 0, high = edges.IsDefault ? -1 : edges.Length - 1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            if (edges[middle] == edge) return layout.Doors.Length + middle;
            if (edges[middle] < edge) low = middle + 1;
            else high = middle - 1;
        }
        return -1;
    }

    internal static ImmutableArray<BoxStationDoor> Definitions(BoxStationLayout layout, ImmutableArray<int> edges)
    {
        if (!ValidEdges(layout, edges)) throw new ArgumentException("Station door placements are invalid.", nameof(edges));
        if (edges.Length == 0) return layout.Doors;
        var doors = ImmutableArray.CreateBuilder<BoxStationDoor>(layout.Doors.Length + edges.Length);
        doors.AddRange(layout.Doors);
        foreach (int edge in edges)
        {
            TryCells(layout, edge, out int first, out int second);
            int direction = Direction(edge) == 0 ? 1 : 4, opposite = Direction(edge) == 0 ? 2 : 8;
            int zoneA = layout.Cells[first].Zone, zoneB = layout.Cells[second].Zone;
            var ports = ImmutableArray.Create(new BoxStationPort(opposite, zoneA, first), new BoxStationPort(direction, zoneB, second));
            doors.Add(new(layout.Doors.Length + edge, first, "airlock", direction, true, false, false,
                zoneA, zoneA, zoneB, ports, -1, -1, -1, "Pressure door"));
        }
        return doors.MoveToImmutable();
    }
}
