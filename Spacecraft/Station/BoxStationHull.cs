// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Modstoke.Client;

internal enum BoxStationWallKind : byte { Unspecified, None, Steel, Reinforced }

internal readonly record struct BoxStationPanelState(
    [property: JsonRequired] int Target, [property: JsonRequired] byte Integrity, bool Foam = false,
    BoxStationWallKind WallKind = BoxStationWallKind.None);

internal readonly record struct BoxStationHullState(long Revision, ImmutableArray<BoxStationPanelState> Panels);

internal sealed partial class BoxStationHull
{
    internal const byte Capacity = 100;
    internal const byte FoamCapacity = 20;
    internal const float MinimumImpactJoules = 20_000, MaximumImpactJoules = 6_000_000;
    internal const double ApertureArea = 4;
    // Effective panel rupture energies are gameplay tuning, in joules.
    private const double FloorBreakJoules = 1_000_000, RoofBreakJoules = 750_000;
    private readonly BoxStationLayout _layout;
    private BoxStationHullState _state = new(0, ImmutableArray<BoxStationPanelState>.Empty);
    private BoxStationRooms? _rooms;

    internal BoxStationHullState State => _state;

    internal BoxStationHull(BoxStationLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        _layout = layout;
    }

    internal void AttachRooms(BoxStationRooms rooms)
    {
        if (!ReferenceEquals(rooms.Layout, _layout) || _rooms is not null)
            throw new InvalidOperationException("The hull already has a room owner or belongs to another layout.");
        _rooms = rooms;
    }

    internal bool ApplyImpact(int target, float joules)
    {
        if (!TryImpactIntegrity(target, joules, out byte remaining)) return false;
        int index = FindPanel(target);
        var kind = remaining == 0 ? InitialWallKind(_layout, target) : WallKind(target);
        var panel = new BoxStationPanelState(target, remaining, index >= 0 && _state.Panels[index].Foam, kind);
        return SetPanel(index, panel);
    }

    internal bool TryImpactIntegrity(int target, float joules, out byte remaining)
    {
        remaining = 0;
        if (!ValidTarget(_layout, target) || !float.IsFinite(joules)
            || joules <= MinimumImpactJoules || joules > MaximumImpactJoules
            || _state.Revision == long.MaxValue) return false;
        int index = FindPanel(target);
        byte integrity = index >= 0 ? _state.Panels[index].Integrity : InitialIntegrity(_layout, target);
        if (integrity == 0) return false;
        int wallCell = WallCell(_layout, target);
        bool structureTarget = TryStructure(_layout, target, out var structure);
        double breakJoules = structureTarget ? StructureBreakJoules(_layout, structure.Id)
            : wallCell >= 0 ? WallBreakJoules(WallKind(target))
            : (target & 1) == 0 ? FloorBreakJoules : RoofBreakJoules;
        int debit = Math.Min(integrity, (int)Math.Ceiling((joules - MinimumImpactJoules) / breakJoules * Capacity));
        remaining = (byte)(integrity - debit);
        if (structureTarget && structure.Kind == BoxStationStructureKind.Grille && integrity > BrokenGrilleIntegrity
            && remaining < BrokenGrilleIntegrity) remaining = BrokenGrilleIntegrity;
        return true;
    }

    internal bool RemoveStructure(int target)
    {
        if (!TryStructure(_layout, target, out _) || _state.Revision == long.MaxValue) return false;
        int index = FindPanel(target);
        if (index >= 0 && _state.Panels[index].Integrity == 0) return false;
        return SetPanel(index, new(target, 0));
    }

    internal bool RestoreStructure(int target)
    {
        if (!TryStructure(_layout, target, out var structure) || _state.Revision == long.MaxValue) return false;
        int index = FindPanel(target);
        byte integrity = index >= 0 ? _state.Panels[index].Integrity : structure.InitialIntegrity;
        if (integrity == Capacity) return false;
        return SetPanel(index, new(target, Capacity));
    }

    private bool SetPanel(int index, BoxStationPanelState panel)
    {
        byte previous = index >= 0 ? _state.Panels[index].Integrity : InitialIntegrity(_layout, panel.Target);
        bool original = !panel.Foam && panel.Integrity == InitialIntegrity(_layout, panel.Target)
            && panel.WallKind == InitialWallKind(_layout, panel.Target);
        var panels = original ? (index >= 0 ? _state.Panels.RemoveAt(index) : _state.Panels)
            : index >= 0 ? _state.Panels.SetItem(index, panel) : _state.Panels.Insert(~index, panel);
        bool changesRooms = (previous == 0) != (panel.Integrity == 0)
            && (WallCell(_layout, panel.Target) >= 0
                || TryStructure(_layout, panel.Target, out var structure) && structure.Kind == BoxStationStructureKind.Window);
        return Commit(new(_state.Revision + 1, panels), changesRooms);
    }

    private bool Commit(BoxStationHullState state, bool changesRooms = true)
    {
        BoxStationRooms.RoomState? rooms = null;
        if (changesRooms && _rooms is not null && !_rooms.TryPrepare(state, out rooms)) return false;
        // All allocations and gas validation precede these synchronous publications.
        _state = state;
        _rooms?.Commit(rooms);
        return true;
    }

    internal byte Integrity(int target)
    {
        if (!ValidTarget(_layout, target)) throw new ArgumentOutOfRangeException(nameof(target));
        int index = FindPanel(target);
        return index >= 0 ? _state.Panels[index].Integrity : InitialIntegrity(_layout, target);
    }

    internal bool Breached(int cell, bool roof)
    {
        if (cell < 0 || cell >= _layout.Cells.Length) return false;
        int target = cell * 2 + (roof ? 1 : 0);
        if (!ValidPanelTarget(_layout, target)) return false;
        int index = FindPanel(target);
        return index >= 0 && _state.Panels[index].Integrity == 0;
    }

    internal bool Foam(int cell, bool roof)
    {
        if (cell < 0 || cell >= _layout.Cells.Length) return false;
        int target = cell * 2 + (roof ? 1 : 0);
        if (!ValidPanelTarget(_layout, target)) return false;
        int index = FindPanel(target);
        return index >= 0 && _state.Panels[index].Foam;
    }

    internal bool SealFoam(int target)
    {
        if (!ValidPanelTarget(_layout, target) || _state.Revision == long.MaxValue) return false;
        int index = FindPanel(target);
        if (index < 0 || _state.Panels[index].Integrity != 0) return false;
        return SetPanel(index, new(target, FoamCapacity, true));
    }

    internal bool RestorePanel(int target)
    {
        if (!ValidPanelTarget(_layout, target) || _state.Revision == long.MaxValue) return false;
        int index = FindPanel(target);
        if (index < 0) return false;
        return SetPanel(index, new(target, Capacity));
    }

    internal static bool ValidTarget(BoxStationLayout layout, int target)
        => ValidPanelTarget(layout, target) || WallCell(layout, target) >= 0 || TryStructure(layout, target, out _);

    internal static byte InitialIntegrity(BoxStationLayout layout, int target)
    {
        if (TryStructure(layout, target, out var structure)) return structure.InitialIntegrity;
        int cell = WallCell(layout, target);
        return cell >= 0 && WallIndex(layout, cell) < 0 ? (byte)0 : Capacity;
    }

    internal static bool ValidPanelTarget(BoxStationLayout layout, int target)
    {
        if (target < 0 || target / 2 >= layout.Cells.Length) return false;
        var flags = layout.Cells[target / 2].Flags;
        var panel = (target & 1) == 0 ? BoxStationCellFlags.Floor : BoxStationCellFlags.Roof;
        return (flags & BoxStationCellFlags.Wall) == 0 && (flags & panel) != 0;
    }

    internal static bool ValidState(BoxStationLayout layout, BoxStationHullState state)
    {
        if (state.Revision < 0 || state.Panels.IsDefault
            || state.Revision == 0 && !state.Panels.IsEmpty) return false;
        int previous = -1;
        foreach (var panel in state.Panels)
        {
            if (panel.Target <= previous || !ValidPanelState(layout, panel)) return false;
            previous = panel.Target;
        }
        return true;
    }

    internal static bool ValidPanelState(BoxStationLayout layout, BoxStationPanelState panel)
    {
        if (!ValidTarget(layout, panel.Target) || !ValidWallKind(layout, panel.Target, panel.Integrity, panel.WallKind)
            || (panel.Foam ? panel.Integrity > FoamCapacity : panel.Integrity > Capacity
                || panel.Integrity == InitialIntegrity(layout, panel.Target)
                    && panel.WallKind == InitialWallKind(layout, panel.Target))
            || panel.Foam && !ValidPanelTarget(layout, panel.Target)) return false;
        return true;
    }

    internal bool TryApply(BoxStationHullState state)
    {
        return ValidState(_layout, state) && Commit(state);
    }

    private int FindPanel(int target)
    {
        int low = 0, high = _state.Panels.Length - 1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            int candidate = _state.Panels[middle].Target;
            if (candidate == target) return middle;
            if (candidate < target) low = middle + 1;
            else high = middle - 1;
        }
        return ~low;
    }

    internal SavedBoxStationHull Capture()
    {
        var panels = new SavedBoxStationPanel[_state.Panels.Length];
        for (int i = 0; i < panels.Length; i++)
        {
            var panel = _state.Panels[i];
            panels[i] = new() { Target = panel.Target, Integrity = panel.Integrity, Foam = panel.Foam, WallKind = panel.WallKind };
        }
        return new() { LayoutHash = _layout.LayoutHash, Revision = _state.Revision, Panels = panels };
    }

    internal bool TryRestore(SavedBoxStationHull? saved)
    {
        if (saved is null || saved.Version is < 1 or > 7 || saved.LayoutHash != _layout.LayoutHash || saved.Panels is null) return false;
        var panels = ImmutableArray.CreateBuilder<BoxStationPanelState>(saved.Panels.Length);
        foreach (var panel in saved.Panels)
        {
            if (panel is null) return false;
            int cell = WallCell(_layout, panel.Target);
            if ((saved.Version == 1 ? panel.FoamSpecified : !panel.Foam.HasValue)
                || (saved.Version < 7 ? panel.WallKindSpecified : !panel.WallKind.HasValue)
                || saved.Version < 3 && !ValidPanelTarget(_layout, panel.Target)
                || saved.Version < 4 && TryStructure(_layout, panel.Target, out _)
                || saved.Version < 6 && cell >= 0 && WallIndex(_layout, cell) < 0
                || saved.Version < 5 && panel.Foam != true
                    && panel.Integrity >= InitialIntegrity(_layout, panel.Target)) return false;
            var kind = saved.Version >= 7 ? panel.WallKind!.Value : InitialWallKind(_layout, panel.Target);
            if (saved.Version < 7 && cell >= 0 && panel.Integrity > 0 && kind == BoxStationWallKind.None)
                kind = BoxStationWallKind.Steel;
            panels.Add(new(panel.Target, panel.Integrity, panel.Foam ?? false, kind));
        }
        return TryApply(new(saved.Revision, panels.MoveToImmutable()));
    }
}

internal sealed class SavedBoxStationPanel
{
    private bool? _foam;
    private BoxStationWallKind? _wallKind;

    [JsonRequired]
    public int Target { get; set; }
    [JsonRequired]
    public byte Integrity { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Foam
    {
        get => _foam;
        set { _foam = value; FoamSpecified = true; }
    }
    internal bool FoamSpecified { get; private set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BoxStationWallKind? WallKind
    {
        get => _wallKind;
        set { _wallKind = value; WallKindSpecified = true; }
    }
    internal bool WallKindSpecified { get; private set; }
}

internal sealed class SavedBoxStationHull
{
    [JsonRequired]
    public int Version { get; set; } = 7;
    [JsonRequired]
    public string? LayoutHash { get; set; }
    [JsonRequired]
    public long Revision { get; set; }
    [JsonRequired]
    public SavedBoxStationPanel[]? Panels { get; set; }
}
