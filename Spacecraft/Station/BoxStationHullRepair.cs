// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Vector3 = System.Numerics.Vector3;

namespace Modstoke.Client;

internal enum BoxStationRepairControl : byte
{
    Craft = 0,
    Take = 1,
    Drop = 2,
    Replace = 3,
    TakeSheets = 4
}

internal enum BoxStationRepairPlace : byte
{
    Source = 0,
    Dropped = 1,
    Held = 2,
    Spent = 3,
    Stored = 4
}

internal readonly record struct BoxStationRepairPackState(int Source, byte Tiles, BoxStationRepairPlace Place,
    Vector3 Position, ulong Actor, Guid Holder, int Storage = -1, byte Slot = 0);

internal readonly record struct BoxStationRepairState(string LayoutHash, ImmutableArray<ushort> Sheets,
    ImmutableArray<BoxStationRepairPackState> Packs, ImmutableArray<ushort> ExportedSteel);

internal sealed partial class BoxStationHullRepair
{
    private static readonly ConditionalWeakTable<BoxStationLayout, Definitions> LayoutDefinitions = new();
    private readonly BoxStationLayout _layout;
    private readonly BoxStationHull _hull;
    private readonly Definitions _definitions;
    private readonly ushort[] _sheets;
    private readonly ushort[] _exportedSteel;
    private List<BoxStationRepairPackState> _packs = new();
    private BoxStationRepairState _state;
    private bool _stateDirty = true;

    internal BoxStationRepairState State
    {
        get
        {
            if (_stateDirty)
            {
                _state = new(_layout.LayoutHash, ImmutableArray.CreateRange(_sheets), ImmutableArray.CreateRange(_packs),
                    ImmutableArray.CreateRange(_exportedSteel));
                _stateDirty = false;
            }
            return _state;
        }
    }

    internal BoxStationHullRepair(BoxStationLayout layout, BoxStationHull hull, bool seedSource = true)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(hull);
        _layout = layout;
        _hull = hull;
        _definitions = LayoutDefinitions.GetValue(layout, BuildDefinitions);
        _sheets = new ushort[_definitions.Sources.Length];
        _exportedSteel = new ushort[_sheets.Length];
        InitializeMaterials(seedSource);
        // Unseeded instances are private restoration candidates until their complete stock is restored.
        if (!seedSource) return;
        for (int i = 0; i < _sheets.Length; i++)
        {
            var source = _definitions.Sources[i];
            _sheets[i] = source.Sheets;
            if (source.Tiles != 0)
                _packs.Add(new(i, source.Tiles, BoxStationRepairPlace.Source, source.Position, 0, Guid.Empty));
        }
    }

    internal int Held(Guid holder)
    {
        if (holder == Guid.Empty) return -1;
        for (int i = 0; i < _packs.Count; i++)
            if (_packs[i].Place == BoxStationRepairPlace.Held && _packs[i].Holder == holder) return i;
        return -1;
    }

    private bool HasHeldActor(ulong actor)
    {
        if (actor == 0) return false;
        foreach (var pack in _packs)
            if (pack.Place == BoxStationRepairPlace.Held && pack.Actor == actor) return true;
        return false;
    }

    internal string Craft(int source, Guid holder, ulong actor, Vector3 dropPosition)
    {
        if (source < 0 || source >= _sheets.Length) return "Unknown metal stock.";
        if (holder == Guid.Empty) return "Tile holder is unavailable.";
        if (Held(holder) >= 0 || HasHeldActor(actor)) return "You are already holding floor tiles.";
        if (_sheets[source] == 0) return "This stock has no metal sheets remaining.";
        if (_packs.Count >= _definitions.MaxPacks) return "No further tile bundles can be crafted.";
        if (!ValidDropPosition(_layout, dropPosition)) return "Stand on clear station flooring to craft tiles.";
        _packs.Add(new(source, 4, BoxStationRepairPlace.Held, dropPosition, actor, holder));
        _sheets[source]--;
        _stateDirty = true;
        return "One metal sheet crafted into four floor tiles.";
    }

    internal string Take(int pack, Guid holder, ulong actor, Vector3 dropPosition, BoxStationMaterialState? storageState = null)
    {
        if (pack < 0 || pack >= _packs.Count) return "Unknown tile bundle.";
        if (holder == Guid.Empty) return "Tile holder is unavailable.";
        if (Held(holder) >= 0 || HasHeldActor(actor)) return "You are already holding floor tiles.";
        if (_packs[pack].Place is not (BoxStationRepairPlace.Source or BoxStationRepairPlace.Dropped or BoxStationRepairPlace.Stored)
            || _packs[pack].Tiles == 0) return "Tile bundle is no longer available.";
        if (_packs[pack].Place == BoxStationRepairPlace.Stored
            && (storageState is not { } materials || !BoxStationStorage.IsOpen(_layout, materials, _packs[pack].Storage)))
            return "Open the storage first.";
        if (!ValidDropPosition(_layout, dropPosition)) return "Stand on clear station flooring to take the tiles.";
        _packs[pack] = _packs[pack] with { Place = BoxStationRepairPlace.Held, Position = dropPosition, Actor = actor, Holder = holder, Storage = -1, Slot = 0 };
        _stateDirty = true;
        return "Floor tiles taken.";
    }

    internal string Drop(Guid holder, Vector3 dropPosition)
    {
        int index = Held(holder);
        if (index < 0) return "You are not holding floor tiles.";
        if (!ValidDropPosition(_layout, dropPosition)) return "Drop the tiles on clear station flooring.";
        _packs[index] = _packs[index] with { Place = BoxStationRepairPlace.Dropped, Position = dropPosition, Actor = 0, Holder = Guid.Empty };
        _stateDirty = true;
        return "Floor tiles dropped.";
    }

    internal string StorePack(Guid holder, int storage, byte slot, BoxStationMaterialState materials)
    {
        int index = Held(holder);
        if (index < 0) return "You are not holding floor tiles.";
        if (_packs[index].Tiles == 0) return "This tile bundle is empty.";
        if (!BoxStationStorage.TryDefinition(_layout, storage, out var definition)) return "Unknown tile storage.";
        if (!BoxStationStorage.IsOpen(_layout, materials, storage)) return "Open the storage first.";
        var position = BoxStationStorage.Position(_layout, definition);
        if (!BoxStationStorage.ValidMembership(_layout, storage, slot, position)) return "Invalid storage slot.";
        foreach (var pack in _packs)
            if (pack.Place == BoxStationRepairPlace.Stored && pack.Storage == storage && pack.Slot == slot)
                return "This storage slot is occupied.";
        _packs[index] = _packs[index] with
        {
            Place = BoxStationRepairPlace.Stored, Position = position, Actor = 0, Holder = Guid.Empty, Storage = storage, Slot = slot
        };
        _stateDirty = true;
        return "Floor tiles stored.";
    }

    internal string Replace(Guid holder, int target)
    {
        if (!SynchronizeFloors()) return "Floor covering state is unavailable.";
        int index = Held(holder);
        if (index < 0) return "You are not holding floor tiles.";
        var pack = _packs[index];
        if (pack.Tiles == 0) return "This tile bundle is empty.";
        // RestorePanel rejects intact/invalid panels and exhausted revisions before changing the hull.
        // After it succeeds, the value-type debit cannot fail or invoke external code.
        if (!_hull.RestorePanel(target)) return "Aim at a damaged original floor or roof panel.";
        byte tiles = (byte)(pack.Tiles - 1);
        _packs[index] = tiles == 0
            ? pack with { Tiles = 0, Place = BoxStationRepairPlace.Spent, Actor = 0, Holder = Guid.Empty }
            : pack with { Tiles = tiles };
        _stateDirty = true;
        return "Panel replaced. One floor tile used.";
    }

    internal void ObserveHeld(Guid holder, Vector3 dropPosition)
    {
        int index = Held(holder);
        if (index >= 0 && _packs[index].Position != dropPosition && ValidDropPosition(_layout, dropPosition))
        {
            _packs[index] = _packs[index] with { Position = dropPosition };
            _stateDirty = true;
        }
    }

    internal void Release(Guid holder)
    {
        int index = Held(holder);
        if (index >= 0)
        {
            _packs[index] = _packs[index] with { Place = BoxStationRepairPlace.Dropped, Actor = 0, Holder = Guid.Empty };
            _stateDirty = true;
        }
    }

    internal static bool ValidState(BoxStationLayout layout, BoxStationRepairState state)
    {
        var definitions = LayoutDefinitions.GetValue(layout, BuildDefinitions);
        var sources = definitions.Sources;
        if (state.LayoutHash != layout.LayoutHash || state.Sheets.IsDefault || state.Sheets.Length != sources.Length
            || state.ExportedSteel.IsDefault || state.ExportedSteel.Length != sources.Length
            || state.Packs.IsDefault || state.Packs.Length > definitions.MaxPacks) return false;
        var counts = new int[sources.Length];
        var holders = new HashSet<Guid>();
        var actors = new HashSet<ulong>();
        var storageSlots = new HashSet<(int Storage, byte Slot)>();
        int initialPack = 0;
        for (int source = 0; source < sources.Length; source++)
            if (sources[source].Tiles != 0)
            {
                if (initialPack >= state.Packs.Length || state.Packs[initialPack++].Source != source) return false;
            }
        foreach (var pack in state.Packs)
        {
            if (pack.Source < 0 || pack.Source >= sources.Length) return false;
            var source = sources[pack.Source];
            counts[pack.Source]++;
            if (pack.Tiles > (source.Tiles == 0 ? 4 : source.Tiles)
                || (pack.Tiles == 0) != (pack.Place == BoxStationRepairPlace.Spent)
                || pack.Place != BoxStationRepairPlace.Stored && (pack.Storage != -1 || pack.Slot != 0)
                || pack.Place != BoxStationRepairPlace.Held && (pack.Holder != Guid.Empty || pack.Actor != 0)) return false;
            switch (pack.Place)
            {
                case BoxStationRepairPlace.Source:
                    if (source.Tiles == 0 || pack.Position != source.Position) return false;
                    break;
                case BoxStationRepairPlace.Dropped:
                    if (!ValidDropPosition(layout, pack.Position)) return false;
                    break;
                case BoxStationRepairPlace.Held:
                    if (pack.Holder == Guid.Empty || !holders.Add(pack.Holder)
                        || pack.Actor != 0 && !actors.Add(pack.Actor) || !ValidDropPosition(layout, pack.Position)) return false;
                    break;
                case BoxStationRepairPlace.Spent:
                    if (pack.Position != source.Position && !ValidDropPosition(layout, pack.Position)) return false;
                    break;
                case BoxStationRepairPlace.Stored:
                    if (!BoxStationStorage.ValidMembership(layout, pack.Storage, pack.Slot, pack.Position)
                        || !storageSlots.Add((pack.Storage, pack.Slot))) return false;
                    break;
                default:
                    return false;
            }
        }
        for (int i = 0; i < sources.Length; i++)
            if (sources[i].Tiles != 0
                ? state.Sheets[i] != 0 || counts[i] != 1 || state.ExportedSteel[i] != 0
                : state.Sheets[i] + counts[i] + state.ExportedSteel[i] != sources[i].Sheets) return false;
        return true;
    }

    internal SavedBoxStationRepair Capture()
    {
        var saved = new SavedBoxStationRepair
        {
            LayoutHash = _layout.LayoutHash, SourceObjects = new int[_sheets.Length], Sheets = (ushort[])_sheets.Clone(),
            ExportedSteel = (ushort[])_exportedSteel.Clone(),
            PackSources = new int[_packs.Count], Tiles = new byte[_packs.Count], Place = new BoxStationRepairPlace[_packs.Count],
            PositionX = new float[_packs.Count], PositionY = new float[_packs.Count], PositionZ = new float[_packs.Count],
            Storage = new int[_packs.Count], Slot = new byte[_packs.Count]
        };
        for (int i = 0; i < _sheets.Length; i++) saved.SourceObjects[i] = _definitions.Sources[i].Object;
        for (int i = 0; i < _packs.Count; i++)
        {
            var pack = _packs[i];
            saved.PackSources[i] = pack.Source;
            saved.Tiles[i] = pack.Tiles;
            // Saving releases only the copy's session custody, at its last eligible floor position.
            saved.Place[i] = pack.Place == BoxStationRepairPlace.Held ? BoxStationRepairPlace.Dropped : pack.Place;
            saved.PositionX[i] = pack.Position.X;
            saved.PositionY[i] = pack.Position.Y;
            saved.PositionZ[i] = pack.Position.Z;
            saved.Storage[i] = pack.Storage;
            saved.Slot[i] = pack.Slot;
        }
        return saved;
    }

    internal bool TryRestore(SavedBoxStationRepair? saved)
    {
        if (saved is null || saved.Version is not (1 or 2 or 3) || saved.LayoutHash != _layout.LayoutHash
            || saved.SourceObjects?.Length != _sheets.Length || saved.Sheets?.Length != _sheets.Length
            || (saved.Version >= 2 ? saved.ExportedSteel?.Length != _sheets.Length : saved.ExportedSteel is not null)
            || saved.PackSources is null || saved.PackSources.Length > _definitions.MaxPacks) return false;
        int count = saved.PackSources.Length;
        if (saved.Tiles?.Length != count || saved.Place?.Length != count || saved.PositionX?.Length != count
            || saved.PositionY?.Length != count || saved.PositionZ?.Length != count) return false;
        if (saved.Version < 3 ? saved.StorageSpecified || saved.SlotSpecified
            : !saved.StorageSpecified || !saved.SlotSpecified || saved.Storage?.Length != count || saved.Slot?.Length != count) return false;
        for (int i = 0; i < _sheets.Length; i++)
            if (saved.SourceObjects[i] != _definitions.Sources[i].Object) return false;
        var packs = ImmutableArray.CreateBuilder<BoxStationRepairPackState>(count);
        for (int i = 0; i < count; i++)
        {
            if (saved.Place[i] == BoxStationRepairPlace.Held || saved.Version < 3 && saved.Place[i] == BoxStationRepairPlace.Stored) return false;
            packs.Add(new(saved.PackSources[i], saved.Tiles[i], saved.Place[i],
                new(saved.PositionX[i], saved.PositionY[i], saved.PositionZ[i]), 0, Guid.Empty,
                saved.Storage is { } storage ? storage[i] : -1, saved.Slot is { } slots ? slots[i] : (byte)0));
        }
        var candidate = new BoxStationRepairState(_layout.LayoutHash, ImmutableArray.CreateRange(saved.Sheets), packs.MoveToImmutable(),
            ImmutableArray.CreateRange(saved.ExportedSteel ?? new ushort[_sheets.Length]));
        if (!ValidState(_layout, candidate)) return false;
        var restored = new List<BoxStationRepairPackState>(candidate.Packs);
        candidate.Sheets.CopyTo(_sheets);
        candidate.ExportedSteel.CopyTo(_exportedSteel);
        _packs = restored;
        _stateDirty = true;
        return true;
    }
}

internal sealed class SavedBoxStationRepair
{
    [JsonRequired]
    public int Version { get; set; } = 3;
    [JsonRequired]
    public string? LayoutHash { get; set; }
    [JsonRequired]
    public int[]? SourceObjects { get; set; }
    [JsonRequired]
    public ushort[]? Sheets { get; set; }
    public ushort[]? ExportedSteel { get; set; }
    [JsonRequired]
    public int[]? PackSources { get; set; }
    [JsonRequired]
    public byte[]? Tiles { get; set; }
    [JsonRequired]
    public BoxStationRepairPlace[]? Place { get; set; }
    [JsonRequired]
    public float[]? PositionX { get; set; }
    [JsonRequired]
    public float[]? PositionY { get; set; }
    [JsonRequired]
    public float[]? PositionZ { get; set; }
    private int[]? _storage;
    public int[]? Storage
    {
        get => _storage;
        set { _storage = value; StorageSpecified = true; }
    }
    [JsonIgnore]
    internal bool StorageSpecified { get; private set; }
    private byte[]? _slot;
    public byte[]? Slot
    {
        get => _slot;
        set { _slot = value; SlotSpecified = true; }
    }
    [JsonIgnore]
    internal bool SlotSpecified { get; private set; }
}
