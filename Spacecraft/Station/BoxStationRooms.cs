// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Modstoke.Client;

internal readonly record struct BoxStationRoomSpan(
    [property: JsonRequired] int Start, [property: JsonRequired] int Length, [property: JsonRequired] int Node);

internal sealed class BoxStationRooms
{
    internal const double CellVolume = 2.4 * 2.4 * 3.2;
    private RoomState _state;
    private ImmutableArray<int> _builtDoors;

    internal sealed class RoomState
    {
        internal required int[] Nodes { get; init; }
        internal required int[] CellCounts { get; init; }
        internal required byte[] OpenEdges { get; init; }
        internal required ImmutableArray<BoxStationRoomSpan> Spans { get; init; }
        internal required StationGas[] Gas { get; set; }
    }

    internal BoxStationLayout Layout { get; }
    internal int Count => _state.Gas.Length;
    internal long TopologyRevision { get; private set; }
    internal ImmutableArray<BoxStationRoomSpan> Spans => _state.Spans;

    // Construct after restoring the unbound hull. Only source initialization or an
    // explicit gas restore may fill these initially empty room volumes.
    internal BoxStationRooms(BoxStationLayout layout, BoxStationHull hull, ImmutableArray<int> builtDoors = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(hull);
        Layout = layout;
        _builtDoors = builtDoors.IsDefault ? ImmutableArray<int>.Empty : builtDoors;
        if (!BoxStationDoors.ValidEdges(layout, _builtDoors)) throw new ArgumentException("Station door placements are invalid.", nameof(builtDoors));
        _state = Build(layout, hull, _builtDoors);
        hull.AttachRooms(this);
    }

    internal int NodeAtCell(int cell)
        => cell >= 0 && cell < _state.Nodes.Length ? _state.Nodes[cell] : -1;

    // A gas reference is valid only until the next hull mutation or gas restore.
    internal ref StationGas Gas(int node) => ref _state.Gas[node];

    internal int CellCount(int node) => _state.CellCounts[node];

    internal bool OpenEdge(int cell, int direction)
        => cell >= 0 && cell < _state.OpenEdges.Length && direction is >= 0 and < 4
            && (_state.OpenEdges[cell] & (1 << direction)) != 0;

    internal bool TryRestoreGas(ImmutableArray<BoxStationRoomSpan> spans, double[][] values)
    {
        if (values is null || values.Length != Count || spans.IsDefault
            || !spans.AsSpan().SequenceEqual(_state.Spans.AsSpan())) return false;
        var restored = new StationGas[Count];
        for (int node = 0; node < restored.Length; node++)
            if (values[node]?.Length != 9 || !TryGas(values[node], _state.CellCounts[node], out restored[node])) return false;
        _state.Gas = restored;
        return true;
    }

    internal bool TryPrepare(BoxStationHullState hullState, out RoomState? prepared)
        => TryPrepareDoors(hullState, _builtDoors, out prepared);

    internal bool TryPrepareDoors(BoxStationHullState hullState, ImmutableArray<int> builtDoors, out RoomState? prepared)
    {
        prepared = null;
        if (!BoxStationDoors.ValidEdges(Layout, builtDoors)) return false;
        var hull = new BoxStationHull(Layout);
        if (!hull.TryApply(hullState)) return false;
        var next = Build(Layout, hull, builtDoors);
        bool sameNodes = next.Nodes.AsSpan().SequenceEqual(_state.Nodes);
        if (sameNodes && next.OpenEdges.AsSpan().SequenceEqual(_state.OpenEdges)) return true;
        if (TopologyRevision == long.MaxValue) return false;
        if (sameNodes) next.Gas = _state.Gas;
        else if (!TryRemap(next)) return false;
        prepared = next;
        return true;
    }

    internal void CommitDoors(RoomState? prepared, ImmutableArray<int> builtDoors)
    {
        _builtDoors = builtDoors;
        Commit(prepared);
    }

    internal void Commit(RoomState? prepared)
    {
        if (prepared is null) return;
        _state = prepared;
        TopologyRevision++;
    }

    private bool TryRemap(RoomState next)
    {
        var recipients = new Dictionary<int, int>?[Count];
        for (int cell = 0; cell < _state.Nodes.Length; cell++)
        {
            int previous = _state.Nodes[cell], current = next.Nodes[cell];
            if (previous < 0 || current < 0) continue;
            var overlaps = recipients[previous] ??= new();
            overlaps.TryGetValue(current, out int cells);
            overlaps[current] = cells + 1;
        }
        var inventories = new double[next.Gas.Length][];
        for (int node = 0; node < inventories.Length; node++) inventories[node] = new double[9];
        for (int previous = 0; previous < Count; previous++)
        {
            var gas = _state.Gas[previous];
            var overlaps = recipients[previous];
            if (overlaps is null)
            {
                if (gas.Moles != 0 || gas.ThermalJoules != 0) return false;
                continue;
            }
            var nodes = new List<int>(overlaps.Keys);
            nodes.Sort();
            int survivingCells = 0;
            foreach (int node in nodes) survivingCells += overlaps[node];
            var values = gas.CaptureValues();
            var remaining = (double[])values.Clone();
            // Closed cells displace their inventory over the old room's surviving
            // cells. New cells add vacuum volume, never a share of new source gas.
            for (int index = 0; index < nodes.Count; index++)
            {
                int node = nodes[index];
                double fraction = (double)overlaps[node] / survivingCells;
                for (int species = 0; species < values.Length; species++)
                {
                    double amount = index + 1 == nodes.Count ? remaining[species]
                        : Math.Min(remaining[species], values[species] * fraction);
                    remaining[species] -= amount;
                    inventories[node][species] += amount;
                }
            }
        }
        for (int node = 0; node < next.Gas.Length; node++)
            if (!TryGas(inventories[node], next.CellCounts[node], out next.Gas[node])) return false;
        return true;
    }

    private static bool TryGas(ReadOnlySpan<double> values, int cells, out StationGas gas)
    {
        double volume = cells * CellVolume;
        return StationGas.TryRestore(values, volume, BoxStationSystems.MaximumPressure,
            volume * BoxStationSystems.MaximumMolesPerCubicMeter, out gas);
    }

    internal static bool ValidSpans(BoxStationLayout layout, ImmutableArray<BoxStationRoomSpan> spans, int roomCount)
    {
        if (spans.IsDefault || roomCount < 0 || roomCount > layout.Cells.Length
            || spans.Length > layout.Cells.Length || spans.Length < roomCount) return false;
        var seen = new bool[roomCount];
        int end = 0, nextNode = 0, previousNode = -1;
        foreach (var span in spans)
        {
            if (span.Start < end || span.Start >= layout.Cells.Length || span.Length <= 0
                || span.Length > layout.Cells.Length - span.Start || span.Node < 0 || span.Node >= roomCount
                || span.Start == end && span.Node == previousNode) return false;
            if (!seen[span.Node])
            {
                if (span.Node != nextNode) return false;
                seen[span.Node] = true;
                nextNode++;
            }
            end = span.Start + span.Length;
            previousNode = span.Node;
        }
        return nextNode == roomCount;
    }

    internal static bool Matches(BoxStationLayout layout, BoxStationHullState hullState,
        ImmutableArray<BoxStationRoomSpan> spans, int roomCount, ImmutableArray<int> builtDoors = default)
    {
        if (!ValidSpans(layout, spans, roomCount)) return false;
        builtDoors = builtDoors.IsDefault ? ImmutableArray<int>.Empty : builtDoors;
        if (!BoxStationDoors.ValidEdges(layout, builtDoors)) return false;
        var hull = new BoxStationHull(layout);
        if (!hull.TryApply(hullState)) return false;
        var rooms = Build(layout, hull, builtDoors);
        return rooms.Gas.Length == roomCount && rooms.Spans.AsSpan().SequenceEqual(spans.AsSpan());
    }

    private static RoomState Build(BoxStationLayout layout, BoxStationHull hull, ImmutableArray<int> builtDoors)
    {
        var nodes = new int[layout.Cells.Length];
        Array.Fill(nodes, -1);
        for (int cell = 0; cell < nodes.Length; cell++)
        {
            var source = layout.Cells[cell];
            if ((hull.EffectiveFlags(cell) & BoxStationCellFlags.Wall) != 0) continue;
            if (source.Zone >= 0 || hull.CavityActive(cell)) nodes[cell] = -2;
        }
        var gates = DoorEdges(layout, builtDoors);
        var edges = new byte[nodes.Length];
        const BoxStationBarrier blocked = BoxStationBarrier.Wall | BoxStationBarrier.Window
            | BoxStationBarrier.GasOnly | BoxStationBarrier.DoorControlled;
        for (int cell = 0; cell < nodes.Length; cell++)
        {
            if (nodes[cell] == -1) continue;
            for (int direction = 1; direction <= 2; direction++)
            {
                int neighbor = Neighbor(layout, cell, direction), opposite = (direction + 2) % 4;
                if (neighbor < 0 || nodes[neighbor] == -1 || (gates[cell] & (1 << direction)) != 0
                    || ((hull.EffectiveBarrier(cell, direction) | hull.EffectiveBarrier(neighbor, opposite)) & blocked) != 0) continue;
                edges[cell] |= (byte)(1 << direction);
                edges[neighbor] |= (byte)(1 << opposite);
            }
        }
        var counts = new List<int>();
        var queue = new int[nodes.Length];
        for (int cell = 0; cell < nodes.Length; cell++)
        {
            if (nodes[cell] != -2) continue;
            int node = counts.Count, head = 0, tail = 1;
            nodes[cell] = node;
            queue[0] = cell;
            while (head < tail)
            {
                int current = queue[head++];
                for (int direction = 0; direction < 4; direction++)
                {
                    if ((edges[current] & (1 << direction)) == 0) continue;
                    int neighbor = Neighbor(layout, current, direction);
                    if (nodes[neighbor] != -2) continue;
                    nodes[neighbor] = node;
                    queue[tail++] = neighbor;
                }
            }
            counts.Add(tail);
        }
        var spans = ImmutableArray.CreateBuilder<BoxStationRoomSpan>();
        for (int cell = 0; cell < nodes.Length;)
        {
            int node = nodes[cell], start = cell++;
            if (node < 0) continue;
            while (cell < nodes.Length && nodes[cell] == node) cell++;
            spans.Add(new(start, cell - start, node));
        }
        var gas = new StationGas[counts.Count];
        for (int node = 0; node < gas.Length; node++) gas[node] = StationGas.Vacuum(counts[node] * CellVolume);
        return new() { Nodes = nodes, CellCounts = counts.ToArray(), OpenEdges = edges, Spans = spans.ToImmutable(), Gas = gas };
    }

    private static byte[] DoorEdges(BoxStationLayout layout, ImmutableArray<int> builtDoors)
    {
        var edges = new byte[layout.Cells.Length];
        foreach (var door in layout.Doors)
        {
            for (int direction = 0; direction < 4; direction++)
            {
                int facing = direction switch { 0 => 1, 1 => 4, 2 => 2, _ => 8 };
                if (door.Edge && door.Direction != facing) continue;
                edges[door.Cell] |= (byte)(1 << direction);
                int neighbor = Neighbor(layout, door.Cell, direction);
                if (neighbor >= 0) edges[neighbor] |= (byte)(1 << ((direction + 2) % 4));
            }
        }
        foreach (int edge in builtDoors)
        {
            BoxStationDoors.TryCells(layout, edge, out int first, out int second);
            int direction = BoxStationDoors.Direction(edge);
            edges[first] |= (byte)(1 << direction);
            edges[second] |= (byte)(1 << ((direction + 2) % 4));
        }
        return edges;
    }

    private static int Neighbor(BoxStationLayout layout, int cell, int direction)
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
