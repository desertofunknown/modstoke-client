// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Vector3 = System.Numerics.Vector3;

namespace Modstoke.Client;

internal enum BoxStationFirefightingControl : byte
{
    TakeCabinet = 0,
    ToggleCabinet = 1,
    StoreCabinet = 2,
    TakeDropped = 3,
    Drop = 4,
    Safety = 5,
    Refill = 6,
    Spray = 7,
    ShipTake = 8,
    ShipStore = 9,
    ShipSpray = 10
}

internal enum BoxStationExtinguisherPlace : byte
{
    Cabinet = 0,
    Dropped = 1,
    Held = 2,
    Stored = 3,
    ShipStored = 4
}

internal readonly record struct BoxStationExtinguisherState(float Water, bool Safety, float CooldownSeconds,
    BoxStationExtinguisherPlace Place, short Cabinet, Vector3 Position, ulong Actor, Guid Holder,
    int Storage = -1, byte Slot = 0, byte ShipSlot = byte.MaxValue);

internal readonly record struct BoxStationFirefightingState(string LayoutHash,
    ImmutableArray<BoxStationExtinguisherState> Items, ImmutableArray<bool> CabinetOpen, ImmutableArray<float> TankWater);

internal sealed partial class BoxStationFirefighting
{
    internal const float ExtinguisherCapacity = 50, BurstUnits = 5, SprayCooldownSeconds = 1.2f;
    internal const byte NoShipSlot = byte.MaxValue, ShipSlotCount = 1;
    private static readonly ConditionalWeakTable<BoxStationLayout, Definitions> LayoutDefinitions = new();
    private readonly BoxStationLayout _layout;
    private readonly Definitions _definitions;
    private readonly BoxStationExtinguisherState[] _items;
    private readonly bool[] _cabinetOpen;
    private readonly float[] _tankWater;

    internal BoxStationFirefightingState State => new(_layout.LayoutHash, ImmutableArray.CreateRange(_items),
        ImmutableArray.CreateRange(_cabinetOpen), ImmutableArray.CreateRange(_tankWater));

    internal BoxStationFirefighting(BoxStationLayout layout, bool seedSource = true)
    {
        ArgumentNullException.ThrowIfNull(layout);
        _layout = layout;
        _definitions = LayoutDefinitions.GetValue(layout, BuildDefinitions);
        _items = new BoxStationExtinguisherState[_definitions.Items.Length];
        _cabinetOpen = new bool[_definitions.Cabinets.Length];
        _tankWater = new float[_definitions.Tanks.Length];
        for (int i = 0; i < _items.Length; i++)
        {
            var source = _definitions.Items[i];
            var position = CellPosition(layout, source.Cell);
            if (source.Cabinet < 0)
            {
                int before = 0, count = 0;
                for (int j = 0; j < _definitions.Items.Length; j++)
                    if (_definitions.Items[j].Cabinet < 0 && _definitions.Items[j].Cell == source.Cell)
                    { count++; if (j < i) before++; }
                position.X += (before - (count - 1) * .5f) * .3f;
            }
            _items[i] = new(seedSource ? ExtinguisherCapacity : 0, true, 0,
                source.Cabinet >= 0 ? BoxStationExtinguisherPlace.Cabinet : BoxStationExtinguisherPlace.Dropped,
                (short)source.Cabinet, position, 0, Guid.Empty);
        }
        if (seedSource)
            for (int i = 0; i < _tankWater.Length; i++) _tankWater[i] = _definitions.Tanks[i].Capacity;
    }

    internal int Held(Guid holder)
    {
        if (holder == Guid.Empty) return -1;
        for (int i = 0; i < _items.Length; i++)
            if (_items[i].Place == BoxStationExtinguisherPlace.Held && _items[i].Holder == holder) return i;
        return -1;
    }

    internal int CabinetItem(int cabinet)
    {
        if (cabinet < 0 || cabinet >= _cabinetOpen.Length) return -1;
        for (int i = 0; i < _items.Length; i++)
            if (_items[i].Place == BoxStationExtinguisherPlace.Cabinet && _items[i].Cabinet == cabinet) return i;
        return -1;
    }

    internal string TakeCabinet(int cabinet, Guid holder, ulong actor, Vector3 dropPosition)
    {
        if (cabinet < 0 || cabinet >= _cabinetOpen.Length) return "Unknown extinguisher cabinet.";
        if (holder == Guid.Empty) return "Extinguisher holder is unavailable.";
        if (Held(holder) >= 0) return "You are already holding an extinguisher.";
        foreach (var held in _items)
            if (actor != 0 && held.Place == BoxStationExtinguisherPlace.Held && held.Actor == actor)
                return "You are already holding an extinguisher.";
        int item = CabinetItem(cabinet);
        if (item < 0) return "Extinguisher cabinet is empty.";
        if (!ValidDropPosition(_layout, _definitions, dropPosition)) return "Stand on clear station flooring to take the extinguisher.";
        Take(item, holder, actor, dropPosition);
        _cabinetOpen[cabinet] = true;
        return "Extinguisher taken.";
    }

    internal string ToggleCabinet(int cabinet)
    {
        if (cabinet < 0 || cabinet >= _cabinetOpen.Length) return "Unknown extinguisher cabinet.";
        _cabinetOpen[cabinet] = !_cabinetOpen[cabinet];
        return _cabinetOpen[cabinet] ? "Extinguisher cabinet opened." : "Extinguisher cabinet closed.";
    }

    internal string StoreCabinet(int cabinet, Guid holder)
    {
        if (cabinet < 0 || cabinet >= _cabinetOpen.Length) return "Unknown extinguisher cabinet.";
        if (!_cabinetOpen[cabinet]) return "Open the extinguisher cabinet first.";
        if (CabinetItem(cabinet) >= 0) return "Extinguisher cabinet is occupied.";
        int item = Held(holder);
        if (item < 0) return "You are not holding an extinguisher.";
        _items[item] = _items[item] with
        {
            Place = BoxStationExtinguisherPlace.Cabinet, Cabinet = (short)cabinet,
            Position = CellPosition(_layout, _definitions.Cabinets[cabinet].Cell), Actor = 0, Holder = Guid.Empty,
            ShipSlot = NoShipSlot
        };
        return "Extinguisher stored.";
    }

    internal string StoreExtinguisher(Guid holder, int storage, byte slot, BoxStationMaterialState materials)
    {
        if (!BoxStationStorage.TryDefinition(_layout, storage, out var definition)) return "Unknown storage.";
        if (!BoxStationStorage.IsOpen(_layout, materials, storage)) return "Open the storage first.";
        if (slot >= BoxStationStorage.Capacity(definition)) return "Unknown storage slot.";
        int item = Held(holder);
        if (item < 0) return "You are not holding an extinguisher.";
        if (!_items[item].Safety) return "Turn on the extinguisher safety before storing it.";
        foreach (var stored in _items)
            if (stored.Place == BoxStationExtinguisherPlace.Stored && stored.Storage == storage && stored.Slot == slot)
                return "This storage slot is occupied.";
        _items[item] = _items[item] with
        {
            Place = BoxStationExtinguisherPlace.Stored, Cabinet = -1,
            Position = BoxStationStorage.Position(_layout, definition), Actor = 0, Holder = Guid.Empty,
            Storage = storage, Slot = slot, ShipSlot = NoShipSlot
        };
        return "Extinguisher stored.";
    }

    internal string TakeDropped(int item, Guid holder, ulong actor, Vector3 dropPosition,
        BoxStationMaterialState? storageState = null)
    {
        if (item < 0 || item >= _items.Length) return "Unknown extinguisher.";
        if (holder == Guid.Empty) return "Extinguisher holder is unavailable.";
        if (Held(holder) >= 0) return "You are already holding an extinguisher.";
        foreach (var held in _items)
            if (actor != 0 && held.Place == BoxStationExtinguisherPlace.Held && held.Actor == actor)
                return "You are already holding an extinguisher.";
        if (_items[item].Place is not (BoxStationExtinguisherPlace.Dropped or BoxStationExtinguisherPlace.Stored))
            return "Extinguisher is no longer available.";
        if (_items[item].Place == BoxStationExtinguisherPlace.Stored
            && (storageState is not { } materials || !BoxStationStorage.IsOpen(_layout, materials, _items[item].Storage)))
            return "Open the storage first.";
        if (!ValidDropPosition(_layout, _definitions, dropPosition)) return "Stand on clear station flooring to take the extinguisher.";
        Take(item, holder, actor, dropPosition);
        return "Extinguisher taken.";
    }

    private void Take(int item, Guid holder, ulong actor, Vector3 dropPosition)
    {
        _items[item] = _items[item] with
        {
            Place = BoxStationExtinguisherPlace.Held, Cabinet = -1,
            Position = dropPosition, Actor = actor, Holder = holder, Storage = -1, Slot = 0, ShipSlot = NoShipSlot
        };
    }

    internal string TakeShipExtinguisher(int id, Guid holder, ulong actor)
    {
        if (id < 0 || id >= _items.Length || _items[id].Place != BoxStationExtinguisherPlace.ShipStored
            || _items[id].ShipSlot != 0) return "Extinguisher is no longer available.";
        if (holder == Guid.Empty) return "Extinguisher holder is unavailable.";
        if (Held(holder) >= 0) return "You are already holding an extinguisher.";
        foreach (var item in _items)
            if (actor != 0 && item.Place == BoxStationExtinguisherPlace.Held && item.Actor == actor)
                return "You are already holding an extinguisher.";
        _items[id] = _items[id] with { Place = BoxStationExtinguisherPlace.Held, Actor = actor, Holder = holder };
        return "Extinguisher taken.";
    }

    internal string StoreShipExtinguisher(Guid holder, ulong actor)
    {
        int id = Held(holder);
        if (id < 0) return "You are not holding an extinguisher.";
        var held = _items[id];
        if (held.Actor != actor) return "This extinguisher is not held by your current actor.";
        if (!held.Safety) return "Turn on the extinguisher safety before storing it.";
        for (int i = 0; i < _items.Length; i++)
            if (i != id && _items[i].ShipSlot == 0) return "The ship extinguisher cradle is occupied.";
        _items[id] = held with
        {
            Place = BoxStationExtinguisherPlace.ShipStored, Cabinet = -1, Actor = 0, Holder = Guid.Empty,
            Storage = -1, Slot = 0, ShipSlot = 0
        };
        return "Extinguisher stored aboard ship.";
    }

    internal string Drop(Guid holder, Vector3 dropPosition)
    {
        int item = Held(holder);
        if (item < 0) return "You are not holding an extinguisher.";
        if (!ValidDropPosition(_layout, _definitions, dropPosition)) return "Drop the extinguisher on clear station flooring.";
        _items[item] = _items[item] with
        {
            Place = BoxStationExtinguisherPlace.Dropped, Position = dropPosition, Actor = 0, Holder = Guid.Empty,
            ShipSlot = NoShipSlot
        };
        return "Extinguisher dropped.";
    }

    internal string ToggleSafety(Guid holder)
    {
        int item = Held(holder);
        if (item < 0) return "You are not holding an extinguisher.";
        _items[item] = _items[item] with { Safety = !_items[item].Safety };
        return _items[item].Safety ? "Extinguisher safety on." : "Extinguisher safety off.";
    }

    internal string Refill(int tank, Guid holder)
    {
        if (tank < 0 || tank >= _tankWater.Length) return "Unknown station water tank.";
        if (SpacecraftShoreCoolant.IsSupply(_definitions.Tanks[tank])) return "This water tank is connected to the ship coolant service.";
        int item = Held(holder);
        if (item < 0) return "You are not holding an extinguisher.";
        float headroom = ExtinguisherCapacity - _items[item].Water;
        if (headroom <= 0) return "Extinguisher is already full.";
        if (_tankWater[tank] <= 0) return "Station water tank is empty.";
        float units = Math.Min(headroom, _tankWater[tank]);
        _tankWater[tank] -= units;
        _items[item] = _items[item] with { Water = _items[item].Water + units };
        return $"Extinguisher refilled by {units:0.##} water units.";
    }

    internal float WaterUnits(int tank) => tank >= 0 && tank < _tankWater.Length ? _tankWater[tank] : 0;

    // The station's owning thread holds the ship computer gate across debit and credit.
    internal bool TryTakeWater(int tank, int units)
    {
        if (units <= 0 || tank < 0 || tank >= _tankWater.Length || units > _tankWater[tank]) return false;
        double target = (double)_tankWater[tank] - units;
        float next = (float)target;
        // Saved tanks may contain fractional stock; rounding must never under-debit.
        if (next > target) next = MathF.BitDecrement(next);
        _tankWater[tank] = next;
        return true;
    }

    internal bool TrySpray(Guid holder, out int item, out float units, out string status)
    {
        item = Held(holder);
        units = 0;
        if (item < 0) status = "You are not holding an extinguisher.";
        else if (_items[item].Safety) status = "Extinguisher safety is on.";
        else if (_items[item].Water < 1) status = "Extinguisher has insufficient water.";
        else if (_items[item].CooldownSeconds > 0) status = "Extinguisher is cooling down.";
        else
        {
            units = Math.Min(BurstUnits, _items[item].Water);
            _items[item] = _items[item] with
            {
                Water = _items[item].Water - units, CooldownSeconds = SprayCooldownSeconds
            };
            status = $"Sprayed {units:0.##} water units.";
            return true;
        }
        return false;
    }

    internal void ObserveHeld(Guid holder, Vector3 dropPosition)
    {
        int item = Held(holder);
        if (item >= 0 && ValidDropPosition(_layout, _definitions, dropPosition))
            _items[item] = _items[item] with { Position = dropPosition, ShipSlot = NoShipSlot };
    }

    internal void Release(Guid holder)
    {
        int item = Held(holder);
        if (item < 0) return;
        var held = _items[item];
        _items[item] = held with
        {
            Place = held.ShipSlot == NoShipSlot ? BoxStationExtinguisherPlace.Dropped : BoxStationExtinguisherPlace.ShipStored,
            Safety = held.ShipSlot != NoShipSlot || held.Safety, Actor = 0, Holder = Guid.Empty
        };
    }

    internal void Step(double dt)
    {
        if (!double.IsFinite(dt) || dt < 0 || dt > 1) throw new ArgumentOutOfRangeException(nameof(dt));
        if (dt == 0) return;
        for (int i = 0; i < _items.Length; i++)
            if (_items[i].CooldownSeconds > 0)
                _items[i] = _items[i] with { CooldownSeconds = (float)Math.Max(0, _items[i].CooldownSeconds - dt) };
    }

    internal static bool ValidState(BoxStationLayout layout, BoxStationFirefightingState state)
    {
        var definitions = LayoutDefinitions.GetValue(layout, BuildDefinitions);
        if (state.LayoutHash != layout.LayoutHash || state.Items.IsDefault || state.Items.Length != definitions.Items.Length
            || state.CabinetOpen.IsDefault || state.CabinetOpen.Length != definitions.Cabinets.Length
            || state.TankWater.IsDefault || state.TankWater.Length != definitions.Tanks.Length) return false;
        var cabinets = new bool[definitions.Cabinets.Length];
        var holders = new HashSet<Guid>();
        var actors = new HashSet<ulong>();
        var storageSlots = new HashSet<(int Storage, byte Slot)>();
        bool shipSlotOccupied = false;
        foreach (var item in state.Items)
        {
            if (!ValidAmount(item.Water, ExtinguisherCapacity) || !ValidAmount(item.CooldownSeconds, SprayCooldownSeconds)
                || item.Place != BoxStationExtinguisherPlace.Stored && (item.Storage != -1 || item.Slot != 0)) return false;
            if (item.ShipSlot != NoShipSlot)
            {
                if (item.ShipSlot >= ShipSlotCount || shipSlotOccupied
                    || item.Place is not (BoxStationExtinguisherPlace.ShipStored or BoxStationExtinguisherPlace.Held)) return false;
                shipSlotOccupied = true;
            }
            switch (item.Place)
            {
                case BoxStationExtinguisherPlace.Cabinet:
                    if (item.Cabinet < 0 || item.Cabinet >= cabinets.Length || cabinets[item.Cabinet]
                        || item.Holder != Guid.Empty || item.Actor != 0
                        || item.Position != CellPosition(layout, definitions.Cabinets[item.Cabinet].Cell)) return false;
                    cabinets[item.Cabinet] = true;
                    break;
                case BoxStationExtinguisherPlace.Dropped:
                    if (item.Cabinet != -1 || item.Holder != Guid.Empty || item.Actor != 0
                        || !ValidDropPosition(layout, definitions, item.Position)) return false;
                    break;
                case BoxStationExtinguisherPlace.Held:
                    if (item.Cabinet != -1 || item.Holder == Guid.Empty || !holders.Add(item.Holder)
                        || item.Actor != 0 && !actors.Add(item.Actor)
                        || !ValidDropPosition(layout, definitions, item.Position)) return false;
                    break;
                case BoxStationExtinguisherPlace.Stored:
                    if (item.Cabinet != -1 || item.Holder != Guid.Empty || item.Actor != 0 || !item.Safety
                        || !BoxStationStorage.ValidMembership(layout, item.Storage, item.Slot, item.Position)
                        || !storageSlots.Add((item.Storage, item.Slot))) return false;
                    break;
                case BoxStationExtinguisherPlace.ShipStored:
                    if (item.Cabinet != -1 || item.Holder != Guid.Empty || item.Actor != 0 || !item.Safety
                        || item.ShipSlot == NoShipSlot || !ValidDropPosition(layout, definitions, item.Position)) return false;
                    break;
                default:
                    return false;
            }
        }
        for (int i = 0; i < state.TankWater.Length; i++)
            if (!ValidAmount(state.TankWater[i], definitions.Tanks[i].Capacity)) return false;
        return true;
    }

    private static bool ValidAmount(float value, float maximum)
        => float.IsFinite(value) && value >= 0 && value <= maximum;

    internal SavedBoxStationFirefighting Capture()
    {
        var saved = new SavedBoxStationFirefighting
        {
            LayoutHash = _layout.LayoutHash, ItemObjects = new int[_items.Length],
            CabinetObjects = new int[_cabinetOpen.Length], TankObjects = new int[_tankWater.Length],
            Water = new float[_items.Length], Safety = new bool[_items.Length], CooldownSeconds = new float[_items.Length],
            Place = new BoxStationExtinguisherPlace[_items.Length], Cabinet = new short[_items.Length],
            PositionX = new float[_items.Length], PositionY = new float[_items.Length], PositionZ = new float[_items.Length],
            CabinetOpen = (bool[])_cabinetOpen.Clone(), TankWater = (float[])_tankWater.Clone(),
            Storage = new int[_items.Length], Slot = new byte[_items.Length], ShipSlot = new byte[_items.Length]
        };
        for (int i = 0; i < _items.Length; i++)
        {
            var item = _items[i];
            // Custody is session-local. Saving never releases the live holder.
            if (item.Place == BoxStationExtinguisherPlace.Held)
                item = item with
                {
                    Place = item.ShipSlot == NoShipSlot ? BoxStationExtinguisherPlace.Dropped : BoxStationExtinguisherPlace.ShipStored,
                    Safety = item.ShipSlot != NoShipSlot || item.Safety
                };
            saved.ItemObjects[i] = _definitions.Items[i].Object;
            saved.Water[i] = item.Water;
            saved.Safety[i] = item.Safety;
            saved.CooldownSeconds[i] = item.CooldownSeconds;
            saved.Place[i] = item.Place;
            saved.Cabinet[i] = item.Cabinet;
            saved.PositionX[i] = item.Position.X;
            saved.PositionY[i] = item.Position.Y;
            saved.PositionZ[i] = item.Position.Z;
            saved.Storage[i] = item.Storage;
            saved.Slot[i] = item.Slot;
            saved.ShipSlot[i] = item.ShipSlot;
        }
        for (int i = 0; i < _cabinetOpen.Length; i++) saved.CabinetObjects[i] = _definitions.Cabinets[i].Object;
        for (int i = 0; i < _tankWater.Length; i++) saved.TankObjects[i] = _definitions.Tanks[i].Object;
        return saved;
    }

    internal bool TryRestore(SavedBoxStationFirefighting? saved)
    {
        if (saved is null || saved.Version is < 1 or > 3 || saved.LayoutHash != _layout.LayoutHash
            || saved.ItemObjects?.Length != _items.Length || saved.CabinetObjects?.Length != _cabinetOpen.Length
            || saved.TankObjects?.Length != _tankWater.Length || saved.Water?.Length != _items.Length
            || saved.Safety?.Length != _items.Length || saved.CooldownSeconds?.Length != _items.Length
            || saved.Place?.Length != _items.Length || saved.Cabinet?.Length != _items.Length
            || saved.PositionX?.Length != _items.Length || saved.PositionY?.Length != _items.Length
            || saved.PositionZ?.Length != _items.Length || saved.CabinetOpen?.Length != _cabinetOpen.Length
            || saved.TankWater?.Length != _tankWater.Length) return false;
        if (saved.Version < 2 ? saved.StorageSpecified || saved.SlotSpecified
            : saved.Storage?.Length != _items.Length || saved.Slot?.Length != _items.Length) return false;
        if (saved.Version < 3 ? saved.ShipSlotSpecified
            : !saved.ShipSlotSpecified || saved.ShipSlot?.Length != _items.Length) return false;
        var items = ImmutableArray.CreateBuilder<BoxStationExtinguisherState>(_items.Length);
        for (int i = 0; i < _items.Length; i++)
        {
            if (saved.ItemObjects[i] != _definitions.Items[i].Object || saved.Place[i] == BoxStationExtinguisherPlace.Held
                || saved.Version < 2 && saved.Place[i] == BoxStationExtinguisherPlace.Stored
                || saved.Version < 3 && saved.Place[i] == BoxStationExtinguisherPlace.ShipStored) return false;
            items.Add(new(saved.Water[i], saved.Safety[i], saved.CooldownSeconds[i], saved.Place[i], saved.Cabinet[i],
                new(saved.PositionX[i], saved.PositionY[i], saved.PositionZ[i]), 0, Guid.Empty,
                saved.Storage?[i] ?? -1, saved.Slot?[i] ?? 0, saved.ShipSlot?[i] ?? NoShipSlot));
        }
        for (int i = 0; i < _cabinetOpen.Length; i++)
            if (saved.CabinetObjects[i] != _definitions.Cabinets[i].Object) return false;
        for (int i = 0; i < _tankWater.Length; i++)
            if (saved.TankObjects[i] != _definitions.Tanks[i].Object) return false;
        var candidate = new BoxStationFirefightingState(_layout.LayoutHash, items.MoveToImmutable(),
            ImmutableArray.CreateRange(saved.CabinetOpen), ImmutableArray.CreateRange(saved.TankWater));
        if (!ValidState(_layout, candidate)) return false;
        candidate.Items.CopyTo(_items);
        candidate.CabinetOpen.CopyTo(_cabinetOpen);
        candidate.TankWater.CopyTo(_tankWater);
        return true;
    }
}

internal sealed class SavedBoxStationFirefighting
{
    [JsonRequired]
    public int Version { get; set; } = 3;
    [JsonRequired]
    public string? LayoutHash { get; set; }
    [JsonRequired]
    public int[]? ItemObjects { get; set; }
    [JsonRequired]
    public int[]? CabinetObjects { get; set; }
    [JsonRequired]
    public int[]? TankObjects { get; set; }
    [JsonRequired]
    public float[]? Water { get; set; }
    [JsonRequired]
    public bool[]? Safety { get; set; }
    [JsonRequired]
    public float[]? CooldownSeconds { get; set; }
    [JsonRequired]
    public BoxStationExtinguisherPlace[]? Place { get; set; }
    [JsonRequired]
    public short[]? Cabinet { get; set; }
    [JsonRequired]
    public float[]? PositionX { get; set; }
    [JsonRequired]
    public float[]? PositionY { get; set; }
    [JsonRequired]
    public float[]? PositionZ { get; set; }
    [JsonRequired]
    public bool[]? CabinetOpen { get; set; }
    [JsonRequired]
    public float[]? TankWater { get; set; }
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
    private byte[]? _shipSlot;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public byte[]? ShipSlot
    {
        get => _shipSlot;
        set { _shipSlot = value; ShipSlotSpecified = true; }
    }
    internal bool ShipSlotSpecified { get; private set; }
}
