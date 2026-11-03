// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Immutable;

namespace Modstoke.Client;

internal sealed partial class BoxStationHullRepair
{
    private ImmutableArray<bool> _storageOpen;

    internal string ToggleMaterialStorage(int storage)
    {
        if (!_materialsInitialized || !BoxStationStorage.TryDefinition(_layout, storage, out _)) return "Unknown material storage.";
        bool open = !_storageOpen[storage];
        var updated = _storageOpen.SetItem(storage, open);
        _storageOpen = updated;
        _materialsDirty = true;
        return open ? "Storage opened." : "Storage closed.";
    }

    internal string StoreMaterials(Guid holder, int storage, byte slot)
    {
        if (!_materialsInitialized || !BoxStationStorage.TryDefinition(_layout, storage, out var definition)) return "Unknown material storage.";
        if (!_storageOpen[storage]) return "Open the storage first.";
        int held = HeldMaterialSlot(holder);
        if (held < 0) return "You are not holding construction materials.";
        if (slot >= BoxStationStorage.Capacity(definition)) return "Unknown storage slot.";
        foreach (var bundle in _materialBundles)
            if (bundle.Place == BoxStationMaterialPlace.Stored && bundle.Storage == storage && bundle.Slot == slot)
                return "This storage slot is occupied.";
        var updated = _materialBundles[held] with
        {
            Place = BoxStationMaterialPlace.Stored, Position = BoxStationStorage.Position(_layout, definition),
            Actor = 0, Holder = Guid.Empty, Storage = storage, Slot = slot
        };
        _materialBundles[held] = updated;
        _materialsDirty = true;
        return "Materials stored.";
    }

}
