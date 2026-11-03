// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Immutable;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Vector3 = System.Numerics.Vector3;

namespace Modstoke.Client;

internal readonly record struct BoxStationFloorState(long Revision, int LostTiles, ImmutableArray<ushort> Removed);

internal sealed partial class BoxStationHullRepair
{
    private static readonly ConditionalWeakTable<BoxStationLayout, FloorDefinitions> LayoutFloors = new();
    private BoxStationFloorState _floors = new(0, 0, ImmutableArray<ushort>.Empty);
    private long _floorHullRevision = -1;

    private sealed class FloorDefinitions
    {
        internal required ImmutableArray<ushort> Cells { get; init; }
        internal required bool[] Covered { get; init; }
    }

    internal static ImmutableArray<ushort> GetFloorCells(BoxStationLayout layout)
        => LayoutFloors.GetValue(layout, BuildFloorDefinitions).Cells;

    internal static bool FloorCovering(BoxStationLayout layout, int cell)
        => cell >= 0 && cell < layout.Cells.Length && LayoutFloors.GetValue(layout, BuildFloorDefinitions).Covered[cell];

    internal static bool FloorRemoved(BoxStationFloorState state, int cell)
        => !state.Removed.IsDefault && FindRemovedFloor(state.Removed, cell) >= 0;

    private static int FindRemovedFloor(ImmutableArray<ushort> removed, int cell)
    {
        int low = 0, high = removed.Length - 1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            if (removed[middle] == cell) return middle;
            if (removed[middle] < cell) low = middle + 1;
            else high = middle - 1;
        }
        return ~low;
    }

    internal static bool ValidFloors(BoxStationLayout layout, BoxStationFloorState state)
    {
        int count = GetFloorCells(layout).Length;
        long maximum = count + 4L * GetConstructionBudget(layout).Steel;
        foreach (var source in GetSources(layout)) maximum += 4L * source.Sheets;
        if (state.Revision < 0 || state.LostTiles < 0 || state.LostTiles > maximum
            || state.Removed.IsDefault || state.Removed.Length > count
            || state.Revision == 0 && (state.LostTiles != 0 || !state.Removed.IsEmpty)) return false;
        int previous = -1;
        foreach (ushort cell in state.Removed)
        {
            if (cell <= previous || !FloorCovering(layout, cell)) return false;
            previous = cell;
        }
        return true;
    }

    internal static bool ValidFloors(BoxStationLayout layout, BoxStationFloorState state, BoxStationHullState hull)
    {
        if (!ValidFloors(layout, state) || !BoxStationHull.ValidState(layout, hull)) return false;
        foreach (var panel in hull.Panels)
            if (MissingFloorDeck(layout, panel) && !FloorRemoved(state, panel.Target / 2)) return false;
        return true;
    }

    private static bool MissingFloorDeck(BoxStationLayout layout, BoxStationPanelState panel)
        => panel.Target >= 0 && panel.Target < layout.Cells.Length * 2 && (panel.Target & 1) == 0
            && (panel.Integrity == 0 || panel.Foam) && FloorCovering(layout, panel.Target / 2);

    private bool IntactFloorDeck(int cell)
        => cell >= 0 && cell < _layout.Cells.Length && (_hull.EffectiveFlags(cell) & BoxStationCellFlags.Floor) != 0
            && !_hull.Breached(cell, false) && !_hull.Foam(cell, false);

    private bool SynchronizeFloors()
    {
        if (!_materialsInitialized) return true;
        var hull = _hull.State;
        if (_floorHullRevision == hull.Revision) return true;
        ImmutableArray<ushort>.Builder? removed = null;
        int lost = _floors.LostTiles;
        foreach (var panel in hull.Panels)
        {
            if (!MissingFloorDeck(_layout, panel) || FloorRemoved(_floors, panel.Target / 2)) continue;
            if (_floors.Revision == long.MaxValue || lost == int.MaxValue) return false;
            removed ??= _floors.Removed.ToBuilder();
            removed.Add((ushort)(panel.Target / 2));
            lost++;
        }
        if (removed is not null)
        {
            removed.Sort();
            var state = new BoxStationFloorState(_floors.Revision + 1, lost, removed.ToImmutable());
            _floors = state;
            _materialsDirty = true;
        }
        _floorHullRevision = hull.Revision;
        return true;
    }

    private bool ApplyFloorImpact(int target, float joules, byte remaining)
    {
        var floors = _floors;
        int cell = target / 2;
        if (remaining == 0 && (target & 1) == 0 && FloorCovering(_layout, cell) && !FloorRemoved(floors, cell))
        {
            if (floors.Revision == long.MaxValue || floors.LostTiles == int.MaxValue) return false;
            floors = new(floors.Revision + 1, floors.LostTiles + 1,
                floors.Removed.Insert(~FindRemovedFloor(floors.Removed, cell), (ushort)cell));
        }
        if (!_hull.ApplyImpact(target, joules)) return false;
        _floors = floors;
        _floorHullRevision = _hull.State.Revision;
        _materialsDirty = true;
        return true;
    }

    internal bool TryRemoveFloor(int cell, Vector3 drop, out string result)
    {
        result = "Floor covering state is unavailable.";
        if (!_materialsInitialized || !SynchronizeFloors()) return false;
        if (!FloorCovering(_layout, cell)) { result = "This floor covering cannot be removed."; return false; }
        if (!IntactFloorDeck(cell)) { result = "Repair the pressure deck before working on its covering."; return false; }
        int removed = FindRemovedFloor(_floors.Removed, cell);
        if (removed >= 0) { result = "This floor covering is already removed."; return false; }
        if (_floors.Revision == long.MaxValue) { result = "Floor covering changes are exhausted."; return false; }
        if (!ValidDropPosition(_layout, drop)) { result = "Stand on clear station flooring to recover the tile."; return false; }
        if (!TryPrepareMaterialSalvage(BoxStationMaterial.FloorTiles, drop, out int slot, out var tile, out bool allocate, out result)) return false;
        var floors = new BoxStationFloorState(_floors.Revision + 1, _floors.LostTiles, _floors.Removed.Insert(~removed, (ushort)cell));
        _materialBundles[slot] = tile;
        if (allocate) _nextMaterialBundleId++;
        _floors = floors;
        _materialsDirty = true;
        result = "Floor covering removed. One tile recovered.";
        return true;
    }

    internal bool TryInstallFloor(int cell, Guid holder, ulong actor, out string result)
    {
        result = "Floor covering state is unavailable.";
        if (!_materialsInitialized || !SynchronizeFloors()) return false;
        if (!FloorCovering(_layout, cell)) { result = "This deck has no original metal floor covering."; return false; }
        if (!IntactFloorDeck(cell)) { result = "Repair the pressure deck before installing its covering."; return false; }
        int removed = FindRemovedFloor(_floors.Removed, cell);
        if (removed < 0) { result = "This floor covering is already installed."; return false; }
        if (_floors.Revision == long.MaxValue) { result = "Floor covering changes are exhausted."; return false; }
        int slot = HeldMaterialSlot(holder);
        if (slot < 0) { result = "You are not holding floor tiles."; return false; }
        var tile = _materialBundles[slot];
        if (tile.Actor != actor || tile.Material != BoxStationMaterial.FloorTiles || tile.Count == 0)
        { result = "You are not holding an available floor tile."; return false; }
        if (Held(holder) >= 0 || HasHeldActor(actor)) { result = "Put down the other floor tiles first."; return false; }
        var remaining = tile.Count == 1 ? default : tile with { Count = (ushort)(tile.Count - 1) };
        var floors = new BoxStationFloorState(_floors.Revision + 1, _floors.LostTiles, _floors.Removed.RemoveAt(removed));
        _materialBundles[slot] = remaining;
        _floors = floors;
        _materialsDirty = true;
        result = "Floor covering installed. One tile used.";
        return true;
    }

    private static BoxStationFloorState MigrateFloors(BoxStationLayout layout, BoxStationHullState hull)
    {
        var removed = ImmutableArray.CreateBuilder<ushort>();
        foreach (var panel in hull.Panels)
            if (MissingFloorDeck(layout, panel)) removed.Add((ushort)(panel.Target / 2));
        return new(removed.Count == 0 ? 0 : 1, removed.Count, removed.ToImmutable());
    }

    private static FloorDefinitions BuildFloorDefinitions(BoxStationLayout layout)
    {
        const string plasteel = "/turf/open/floor/plasteel";
        var cells = ImmutableArray.CreateBuilder<ushort>();
        var covered = new bool[layout.Cells.Length];
        for (int i = 0; i < layout.Cells.Length; i++)
        {
            string turf = layout.Types[layout.Cells[i].TurfType];
            if (turf != plasteel && !turf.StartsWith(plasteel + "/", StringComparison.Ordinal)) continue;
            if (i > ushort.MaxValue || !BoxStationHull.ValidPanelTarget(layout, i * 2))
                throw new InvalidDataException($"Station floor covering {i} has no supported pressure deck.");
            covered[i] = true;
            cells.Add((ushort)i);
        }
        return new() { Cells = cells.ToImmutable(), Covered = covered };
    }
}

internal sealed class SavedBoxStationFloors
{
    [JsonRequired]
    public long Revision { get; set; }
    [JsonRequired]
    public int LostTiles { get; set; }
    [JsonRequired]
    public ushort[]? Removed { get; set; }
}
