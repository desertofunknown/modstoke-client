// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Vector3 = System.Numerics.Vector3;

namespace Modstoke.Client;

internal enum BoxStationFoamControl : byte
{
    Take = 0,
    Drop = 1,
    Deploy = 2
}

internal enum BoxStationFoamPlace : byte
{
    Rack = 0,
    Dropped = 1,
    Held = 2,
    Armed = 3,
    Foaming = 4,
    Spent = 5,
    Stored = 6
}

internal readonly record struct BoxStationFoamItemState(BoxStationFoamPlace Place, Vector3 Position,
    ulong Actor, Guid Holder, int Target, float RemainingSeconds, int Storage = -1, byte Slot = 0);

internal readonly record struct BoxStationFoamState(string LayoutHash, ImmutableArray<BoxStationFoamItemState> Items);

internal sealed partial class BoxStationFoam
{
    internal const int MaxTargets = 8;
    internal const float FuseSeconds = 5, FoamSeconds = 3;
    private static readonly ConditionalWeakTable<BoxStationLayout, Definitions> LayoutDefinitions = new();
    private readonly BoxStationLayout _layout;
    private readonly BoxStationHull _hull;
    private readonly Definitions _definitions;
    private readonly BoxStationFoamItemState[] _items;
    private readonly int[] _collectedTargets;
    private readonly int[] _collectedCounts;

    internal BoxStationFoamState State => new(_layout.LayoutHash, ImmutableArray.CreateRange(_items));

    internal BoxStationFoam(BoxStationLayout layout, BoxStationHull hull, bool seedSource = true)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(hull);
        _layout = layout;
        _hull = hull;
        _definitions = LayoutDefinitions.GetValue(layout, BuildDefinitions);
        _items = new BoxStationFoamItemState[_definitions.Items.Length];
        _collectedTargets = new int[_items.Length * MaxTargets];
        _collectedCounts = new int[_items.Length];
        for (int i = 0; i < _items.Length; i++)
            _items[i] = new(seedSource ? BoxStationFoamPlace.Rack : BoxStationFoamPlace.Spent,
                _definitions.Items[i].Position, 0, Guid.Empty, -1, 0);
    }

    internal int Held(Guid holder)
    {
        if (holder == Guid.Empty) return -1;
        for (int i = 0; i < _items.Length; i++)
            if (_items[i].Place == BoxStationFoamPlace.Held && _items[i].Holder == holder) return i;
        return -1;
    }

    internal string Take(int index, Guid holder, ulong actor, Vector3 dropPosition,
        BoxStationMaterialState? storageState = null)
    {
        if (index < 0 || index >= _items.Length) return "Unknown foam grenade.";
        if (holder == Guid.Empty) return "Foam grenade holder is unavailable.";
        if (Held(holder) >= 0) return "You are already holding a foam grenade.";
        if (actor != 0)
            foreach (var item in _items)
                if (item.Place == BoxStationFoamPlace.Held && item.Actor == actor)
                    return "You are already holding a foam grenade.";
        if (_items[index].Place is not (BoxStationFoamPlace.Rack or BoxStationFoamPlace.Dropped or BoxStationFoamPlace.Stored))
            return "Foam grenade is no longer available.";
        if (_items[index].Place == BoxStationFoamPlace.Stored
            && (storageState is not { } materials || !BoxStationStorage.IsOpen(_layout, materials, _items[index].Storage)))
            return "Open the storage first.";
        if (!ValidDropPosition(_layout, dropPosition)) return "Stand on clear station flooring to take the foam grenade.";
        _items[index] = new(BoxStationFoamPlace.Held, dropPosition, actor, holder, -1, 0);
        return "Foam grenade taken.";
    }

    internal string StoreFoam(Guid holder, int storage, byte slot, BoxStationMaterialState materials)
    {
        if (!BoxStationStorage.TryDefinition(_layout, storage, out var definition)) return "Unknown storage.";
        if (!BoxStationStorage.IsOpen(_layout, materials, storage)) return "Open the storage first.";
        if (slot >= BoxStationStorage.Capacity(definition)) return "Unknown storage slot.";
        int index = Held(holder);
        if (index < 0) return "You are not holding a foam grenade.";
        if (_items[index].Target != -1 || _items[index].RemainingSeconds != 0) return "Only inactive foam grenades can be stored.";
        foreach (var stored in _items)
            if (stored.Place == BoxStationFoamPlace.Stored && stored.Storage == storage && stored.Slot == slot)
                return "This storage slot is occupied.";
        _items[index] = _items[index] with
        {
            Place = BoxStationFoamPlace.Stored, Position = BoxStationStorage.Position(_layout, definition),
            Actor = 0, Holder = Guid.Empty, Storage = storage, Slot = slot
        };
        return "Foam grenade stored.";
    }

    internal string Drop(Guid holder, Vector3 dropPosition)
    {
        int index = Held(holder);
        if (index < 0) return "You are not holding a foam grenade.";
        if (!ValidDropPosition(_layout, dropPosition)) return "Drop the foam grenade on clear station flooring.";
        _items[index] = new(BoxStationFoamPlace.Dropped, dropPosition, 0, Guid.Empty, -1, 0);
        return "Foam grenade dropped.";
    }

    internal string Deploy(Guid holder, int target, Vector3 anchorPosition)
    {
        int index = Held(holder);
        if (index < 0) return "You are not holding a foam grenade.";
        if (!ValidAnchorPosition(_layout, target, anchorPosition) || !_hull.Breached(target / 2, (target & 1) != 0))
            return "Attach the foam grenade to a broken panel rim.";
        _items[index] = new(BoxStationFoamPlace.Armed, anchorPosition, 0, Guid.Empty, target, FuseSeconds);
        return "Foam grenade armed. Five-second fuse.";
    }

    internal void ObserveHeld(Guid holder, Vector3 dropPosition)
    {
        int index = Held(holder);
        if (index >= 0 && ValidDropPosition(_layout, dropPosition))
            _items[index] = _items[index] with { Position = dropPosition };
    }

    internal void Release(Guid holder)
    {
        int index = Held(holder);
        if (index >= 0)
            _items[index] = _items[index] with { Place = BoxStationFoamPlace.Dropped, Actor = 0, Holder = Guid.Empty };
    }

    internal void Step(float dt)
    {
        if (!float.IsFinite(dt) || dt < 0 || dt > .1f) throw new ArgumentOutOfRangeException(nameof(dt));
        if (dt == 0) return;
        Array.Clear(_collectedCounts);
        for (int i = 0; i < _items.Length; i++)
        {
            var item = _items[i];
            if (item.Place == BoxStationFoamPlace.Armed)
            {
                float remaining = item.RemainingSeconds - dt;
                _items[i] = remaining > 0 ? item with { RemainingSeconds = remaining }
                    : item with { Place = BoxStationFoamPlace.Foaming, RemainingSeconds = FoamSeconds + remaining };
            }
            else if (item.Place == BoxStationFoamPlace.Foaming && item.RemainingSeconds > 0)
                _items[i] = item with { RemainingSeconds = Math.Max(0, item.RemainingSeconds - dt) };
        }
    }

    internal bool Ready(int index)
        => index >= 0 && index < _items.Length && _items[index].Place == BoxStationFoamPlace.Foaming
            && _items[index].RemainingSeconds == 0;

    internal int CollectTargets(int index, ReadOnlySpan<float> doorAmounts, Span<int> output, ImmutableArray<int> builtDoors = default)
    {
        builtDoors = builtDoors.IsDefault ? ImmutableArray<int>.Empty : builtDoors;
        if (!BoxStationDoors.ValidEdges(_layout, builtDoors) || doorAmounts.Length != _layout.Doors.Length + builtDoors.Length)
            throw new ArgumentException("Expected one opening per station door.", nameof(doorAmounts));
        if (output.Length < MaxTargets)
            throw new ArgumentException("Expected room for all foam targets.", nameof(output));
        if (index < 0 || index >= _items.Length) return 0;
        _collectedCounts[index] = 0;
        if (!Ready(index)) return 0;
        // Two cardinal steps visit at most thirteen cells, independently of station size.
        Span<int> cells = stackalloc int[13];
        Span<byte> depths = stackalloc byte[13];
        cells[0] = _items[index].Target / 2;
        depths[0] = 0;
        int queued = 1, count = 0, layer = _items[index].Target & 1;
        for (int next = 0; next < queued; next++)
        {
            int cell = cells[next], target = cell * 2 + layer;
            if (BoxStationHull.ValidPanelTarget(_layout, target) && _hull.Breached(cell, layer != 0))
            {
                output[count] = target;
                _collectedTargets[index * MaxTargets + count++] = target;
                if (count == MaxTargets) break;
            }
            if (depths[next] == 2) continue;
            var source = _layout.Cells[cell];
            for (int cardinal = 0; cardinal < 4; cardinal++)
            {
                int neighbor, direction;
                switch (cardinal)
                {
                    case 0:
                        if (source.Row == 0) continue;
                        neighbor = cell - _layout.Width; direction = 1;
                        break;
                    case 1:
                        if (source.X + 1 == _layout.Width) continue;
                        neighbor = cell + 1; direction = 4;
                        break;
                    case 2:
                        if (source.Row + 1 == _layout.Height) continue;
                        neighbor = cell + _layout.Width; direction = 2;
                        break;
                    default:
                        if (source.X == 0) continue;
                        neighbor = cell - 1; direction = 8;
                        break;
                }
                var flags = _hull.EffectiveFlags(neighbor);
                bool supported = (flags & (BoxStationCellFlags.Floor | BoxStationCellFlags.Catwalk)) != 0
                    && (flags & (BoxStationCellFlags.Wall | BoxStationCellFlags.FullWindow | BoxStationCellFlags.Grille)) == 0;
                if (!supported
                    || !CanCross(cell, neighbor, direction, doorAmounts, builtDoors)) continue;
                bool visited = false;
                for (int prior = 0; prior < queued; prior++)
                    if (cells[prior] == neighbor) { visited = true; break; }
                if (visited) continue;
                cells[queued] = neighbor;
                depths[queued++] = (byte)(depths[next] + 1);
            }
        }
        _collectedCounts[index] = count;
        return count;
    }

    // The authority filters the collected shortlist for physical clearance before completion.
    internal int Complete(int index, ReadOnlySpan<int> targets)
    {
        if (targets.Length > MaxTargets) throw new ArgumentException("Too many foam targets.", nameof(targets));
        if (!Ready(index)) return 0;
        _items[index] = _items[index] with { Place = BoxStationFoamPlace.Spent };
        int patched = 0;
        foreach (int target in targets)
            for (int i = 0; i < _collectedCounts[index]; i++)
                if (_collectedTargets[index * MaxTargets + i] == target)
                {
                    if (_hull.SealFoam(target)) patched++;
                    break;
                }
        _collectedCounts[index] = 0;
        return patched;
    }

    internal static bool ValidState(BoxStationLayout layout, BoxStationFoamState state)
    {
        var definitions = LayoutDefinitions.GetValue(layout, BuildDefinitions);
        if (state.LayoutHash != layout.LayoutHash || state.Items.IsDefault || state.Items.Length != definitions.Items.Length) return false;
        var holders = new HashSet<Guid>();
        var actors = new HashSet<ulong>();
        var storageSlots = new HashSet<(int Storage, byte Slot)>();
        for (int i = 0; i < state.Items.Length; i++)
        {
            var item = state.Items[i];
            if (!float.IsFinite(item.RemainingSeconds) || item.RemainingSeconds < 0
                || item.Place != BoxStationFoamPlace.Stored && (item.Storage != -1 || item.Slot != 0)
                || item.Place != BoxStationFoamPlace.Held && (item.Holder != Guid.Empty || item.Actor != 0)) return false;
            switch (item.Place)
            {
                case BoxStationFoamPlace.Rack:
                    if (item.Target != -1 || item.RemainingSeconds != 0 || item.Position != definitions.Items[i].Position) return false;
                    break;
                case BoxStationFoamPlace.Dropped:
                    if (item.Target != -1 || item.RemainingSeconds != 0 || !ValidDropPosition(layout, item.Position)) return false;
                    break;
                case BoxStationFoamPlace.Held:
                    if (item.Target != -1 || item.RemainingSeconds != 0 || item.Holder == Guid.Empty
                        || !holders.Add(item.Holder) || item.Actor != 0 && !actors.Add(item.Actor)
                        || !ValidDropPosition(layout, item.Position)) return false;
                    break;
                case BoxStationFoamPlace.Armed:
                    if (item.RemainingSeconds <= 0 || item.RemainingSeconds > FuseSeconds
                        || !ValidAnchorPosition(layout, item.Target, item.Position)) return false;
                    break;
                case BoxStationFoamPlace.Foaming:
                    if (item.RemainingSeconds > FoamSeconds || !ValidAnchorPosition(layout, item.Target, item.Position)) return false;
                    break;
                case BoxStationFoamPlace.Spent:
                    if (item.RemainingSeconds != 0 || (item.Target == -1
                        ? item.Position != definitions.Items[i].Position : !ValidAnchorPosition(layout, item.Target, item.Position))) return false;
                    break;
                case BoxStationFoamPlace.Stored:
                    if (item.Target != -1 || item.RemainingSeconds != 0
                        || !BoxStationStorage.ValidMembership(layout, item.Storage, item.Slot, item.Position)
                        || !storageSlots.Add((item.Storage, item.Slot))) return false;
                    break;
                default:
                    return false;
            }
        }
        return true;
    }

    internal SavedBoxStationFoam Capture()
    {
        var saved = new SavedBoxStationFoam
        {
            LayoutHash = _layout.LayoutHash, ItemObjects = new int[_items.Length], Place = new BoxStationFoamPlace[_items.Length],
            PositionX = new float[_items.Length], PositionY = new float[_items.Length], PositionZ = new float[_items.Length],
            Target = new int[_items.Length], RemainingSeconds = new float[_items.Length],
            Storage = new int[_items.Length], Slot = new byte[_items.Length]
        };
        for (int i = 0; i < _items.Length; i++)
        {
            var item = _items[i];
            saved.ItemObjects[i] = _definitions.Items[i].Object;
            // Session custody is released only in the saved copy, at its last eligible floor position.
            saved.Place[i] = item.Place == BoxStationFoamPlace.Held ? BoxStationFoamPlace.Dropped : item.Place;
            saved.PositionX[i] = item.Position.X;
            saved.PositionY[i] = item.Position.Y;
            saved.PositionZ[i] = item.Position.Z;
            saved.Target[i] = item.Target;
            saved.RemainingSeconds[i] = item.RemainingSeconds;
            saved.Storage[i] = item.Storage;
            saved.Slot[i] = item.Slot;
        }
        return saved;
    }

    internal bool TryRestore(SavedBoxStationFoam? saved)
    {
        if (saved is null || saved.Version is < 1 or > 2 || saved.LayoutHash != _layout.LayoutHash
            || saved.ItemObjects?.Length != _items.Length || saved.Place?.Length != _items.Length
            || saved.PositionX?.Length != _items.Length || saved.PositionY?.Length != _items.Length
            || saved.PositionZ?.Length != _items.Length || saved.Target?.Length != _items.Length
            || saved.RemainingSeconds?.Length != _items.Length) return false;
        if (saved.Version < 2 ? saved.StorageSpecified || saved.SlotSpecified
            : saved.Storage?.Length != _items.Length || saved.Slot?.Length != _items.Length) return false;
        var items = ImmutableArray.CreateBuilder<BoxStationFoamItemState>(_items.Length);
        for (int i = 0; i < _items.Length; i++)
        {
            if (saved.ItemObjects[i] != _definitions.Items[i].Object || saved.Place[i] == BoxStationFoamPlace.Held
                || saved.Version < 2 && saved.Place[i] == BoxStationFoamPlace.Stored) return false;
            items.Add(new(saved.Place[i], new(saved.PositionX[i], saved.PositionY[i], saved.PositionZ[i]),
                0, Guid.Empty, saved.Target[i], saved.RemainingSeconds[i], saved.Storage?[i] ?? -1, saved.Slot?[i] ?? 0));
        }
        var candidate = new BoxStationFoamState(_layout.LayoutHash, items.MoveToImmutable());
        if (!ValidState(_layout, candidate)) return false;
        candidate.Items.CopyTo(_items);
        Array.Clear(_collectedCounts);
        return true;
    }
}

internal sealed class SavedBoxStationFoam
{
    [JsonRequired]
    public int Version { get; set; } = 2;
    [JsonRequired]
    public string? LayoutHash { get; set; }
    [JsonRequired]
    public int[]? ItemObjects { get; set; }
    [JsonRequired]
    public BoxStationFoamPlace[]? Place { get; set; }
    [JsonRequired]
    public float[]? PositionX { get; set; }
    [JsonRequired]
    public float[]? PositionY { get; set; }
    [JsonRequired]
    public float[]? PositionZ { get; set; }
    [JsonRequired]
    public int[]? Target { get; set; }
    [JsonRequired]
    public float[]? RemainingSeconds { get; set; }
    private int[]? _storage;
    public int[]? Storage
    {
        get => _storage;
        set { _storage = value; StorageSpecified = true; }
    }
    internal bool StorageSpecified { get; private set; }
    private byte[]? _slot;
    public byte[]? Slot
    {
        get => _slot;
        set { _slot = value; SlotSpecified = true; }
    }
    internal bool SlotSpecified { get; private set; }
}
