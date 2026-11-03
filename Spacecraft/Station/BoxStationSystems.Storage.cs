// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;

namespace Modstoke.Client;

internal readonly record struct BoxStationPortableState(BoxStationMaterialState Materials,
    BoxStationEngineeringState Engineering, BoxStationFirefightingState Firefighting,
    BoxStationFoamState Foam, BoxStationRepairState Repair);

internal sealed partial class BoxStationSystems
{
    internal BoxStationPortableState Portables => new(Repair.Materials, Engineering.State,
        Firefighting.State, Foam.State, Repair.State);

    internal static bool ValidPortables(BoxStationLayout layout, BoxStationPortableState state)
    {
        if (!BoxStationHullRepair.ValidMaterials(layout, state.Materials)
            || !BoxStationEngineering.ValidState(layout, state.Engineering)
            || !BoxStationFirefighting.ValidState(layout, state.Firefighting)
            || !BoxStationFoam.ValidState(layout, state.Foam)
            || !BoxStationHullRepair.ValidState(layout, state.Repair)
            || state.Engineering.HullRevision != state.Materials.HullRevision
            || !BoxStationHullRepair.ValidMaterialTransfers(layout, state.Repair, state.Materials)) return false;
        int count = BoxStationStorage.GetDefinitions(layout).Length;
        Span<byte> occupied = count <= 256 ? stackalloc byte[count] : new byte[count];
        occupied.Clear();
        var holders = new HashSet<Guid>();
        var actors = new HashSet<ulong>();
        foreach (var item in state.Materials.Bundles)
            if (!AcceptCustody(item.Place == BoxStationMaterialPlace.Held, item.Holder, item.Actor,
                    item.Storage, item.Slot, occupied, holders, actors)) return false;
        foreach (var item in state.Engineering.Tools)
            if (!AcceptCustody(item.Place == BoxStationToolPlace.Held, item.Holder, item.Actor,
                    item.Storage, item.Slot, occupied, holders, actors)) return false;
        foreach (var item in state.Firefighting.Items)
            if (!AcceptCustody(item.Place == BoxStationExtinguisherPlace.Held, item.Holder, item.Actor,
                    item.Storage, item.Slot, occupied, holders, actors)) return false;
        foreach (var item in state.Foam.Items)
            if (!AcceptCustody(item.Place == BoxStationFoamPlace.Held, item.Holder, item.Actor,
                    item.Storage, item.Slot, occupied, holders, actors)) return false;
        foreach (var item in state.Repair.Packs)
            if (!AcceptCustody(item.Place == BoxStationRepairPlace.Held, item.Holder, item.Actor,
                    item.Storage, item.Slot, occupied, holders, actors)) return false;
        return true;
    }

    private static bool AcceptCustody(bool held, Guid holder, ulong actor, int storage, byte slot,
        Span<byte> occupied, HashSet<Guid> holders, HashSet<ulong> actors)
    {
        if (held && (!holders.Add(holder) || actor != 0 && !actors.Add(actor))) return false;
        if (storage < 0) return true;
        int mask = 1 << slot;
        if ((occupied[storage] & mask) != 0) return false;
        occupied[storage] |= (byte)mask;
        return true;
    }

    internal static bool StorageHasRoom(BoxStationLayout layout, BoxStationPortableState state, int storage)
        => EmptyStorageSlot(layout, state, storage) >= 0;

    private static int EmptyStorageSlot(BoxStationLayout layout, BoxStationPortableState state, int storage)
    {
        if (!BoxStationStorage.TryDefinition(layout, storage, out var definition)
            || state.Materials.Bundles.IsDefault || state.Engineering.Tools.IsDefault
            || state.Firefighting.Items.IsDefault || state.Foam.Items.IsDefault || state.Repair.Packs.IsDefault) return -1;
        int occupied = 0;
        foreach (var item in state.Materials.Bundles)
            if (item.Storage == storage) occupied |= 1 << item.Slot;
        foreach (var item in state.Engineering.Tools)
            if (item.Storage == storage) occupied |= 1 << item.Slot;
        foreach (var item in state.Firefighting.Items)
            if (item.Storage == storage) occupied |= 1 << item.Slot;
        foreach (var item in state.Foam.Items)
            if (item.Storage == storage) occupied |= 1 << item.Slot;
        foreach (var item in state.Repair.Packs)
            if (item.Storage == storage) occupied |= 1 << item.Slot;
        for (int slot = 0; slot < BoxStationStorage.Capacity(definition); slot++)
            if ((occupied & (1 << slot)) == 0) return slot;
        return -1;
    }

    internal string StorePortable(Guid holder, int storage)
    {
        var state = Portables;
        if (!ValidPortables(Layout, state)) return "Storage state is not current.";
        if (!BoxStationStorage.IsOpen(Layout, state.Materials, storage)) return "Open the storage first.";
        int slot = EmptyStorageSlot(Layout, state, storage);
        if (slot < 0) return "This storage is full.";
        if (Repair.HeldMaterials(holder) >= 0)
        {
            string result = Repair.StoreMaterials(holder, storage, (byte)slot);
            if (Repair.HeldMaterials(holder) < 0) Engineering.Cancel(holder);
            return result;
        }
        if (Engineering.Held(holder) >= 0) return Engineering.StoreTool(holder, storage, (byte)slot, state.Materials);
        if (Firefighting.Held(holder) >= 0) return Firefighting.StoreExtinguisher(holder, storage, (byte)slot, state.Materials);
        if (Foam.Held(holder) >= 0) return Foam.StoreFoam(holder, storage, (byte)slot, state.Materials);
        if (Repair.Held(holder) >= 0) return Repair.StorePack(holder, storage, (byte)slot, state.Materials);
        return "Take an item first.";
    }
}
