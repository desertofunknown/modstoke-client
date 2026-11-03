// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Modstoke.Sons;

namespace Modstoke.Client;

internal sealed record ClientPackEquipmentSlot(ItemEquipmentSlot Slot, string Name, bool? CurrentFound, int? CurrentItemId,
    string CurrentName, bool? NextFound, int? NextItemId, string NextName, bool CanBrowseCurrent, bool CanStowCurrent,
    ClientPackReadStatus Status);

internal sealed record ClientPackLoadoutSnapshot
{
    internal ClientPackReadStatus Status { get; init; } = ClientPackReadStatus.NotObserved;
    internal string Message { get; init; } = "Waiting for equipment readings.";
    internal long ObservedAt { get; init; }
    internal IReadOnlyList<ClientPackEquipmentSlot> Slots { get; init; } = Array.Empty<ClientPackEquipmentSlot>();
}

internal sealed record ClientPackAmmunitionEntry(int? ItemId, string Name, int? Count, bool? Owned,
    ClientPackReadStatus Status, bool CanBrowse);

internal sealed record ClientPackAmmunitionSnapshot
{
    internal ClientPackReadStatus Status { get; init; } = ClientPackReadStatus.NotObserved;
    internal string Message { get; init; } = "Select an item to read ammunition information.";
    internal int ItemId { get; init; }
    internal int? ReportedCount { get; init; }
    internal bool Truncated { get; init; }
    internal long MetadataObservedAt { get; init; }
    internal long StockObservedAt { get; init; }
    internal IReadOnlyList<ClientPackAmmunitionEntry> Entries { get; init; } = Array.Empty<ClientPackAmmunitionEntry>();
}

internal sealed partial class ClientPackApp
{
    private ClientPackLoadoutSnapshot _loadout = new();
    private ClientPackAmmunitionSnapshot _ammunition = new();
    private ItemAmmunitionSnapshot? _ammunitionDefinition;
    private int[] _ammunitionItemIds = Array.Empty<int>();
    private long _ammunitionObservedAt;
    private bool _stowNeedsAttention;
    private string _stowMessage = ClientPackAppSnapshot.DefaultStowMessage;

    internal bool CanStowItem(ItemEquipmentSlot slot, int itemId) => CanChangeInventory && itemId > 0
        && _loadout.Slots.Any(row => row.Slot == slot && row.CurrentItemId == itemId && row.CanStowCurrent);

    internal bool BeginStow(ItemEquipmentSlot slot, int itemId)
    {
        if (!CanStowItem(slot, itemId)) return false;
        _actionInProgress = true;
        _stowMessage = "Putting away the item…";
        _stowNeedsAttention = false;
        // Consume equipment and stock observations before native callbacks can re-enter.
        InvalidateStowReadings();
        Publish();
        return true;
    }

    internal void FinishStow(long generation, ItemEquipmentSlot slot, bool confirmed, EquipmentStowResult? result)
    {
        if (!OnThread || _disposed || generation != _generation) return;
        _actionInProgress = false;
        _stowNeedsAttention = !confirmed;
        _stowMessage = confirmed ? EquipmentSlotName(slot) + " slot is now empty."
            : result is { InvocationAttempted: false }
                ? "Nothing was put away. Finish your current action or use the game's inventory."
                : "The put-away result could not be confirmed. Check your equipment before trying again.";
        InvalidateStowReadings();
        Publish();
    }

    internal void RefuseStow(ItemEquipmentSlot slot, int itemId, string message)
    {
        if (!CanStowItem(slot, itemId)) return;
        _stowMessage = message;
        _stowNeedsAttention = true;
        Publish();
    }

    private void InvalidateStowReadings()
    {
        _loadout = new() { Message = "Refreshing equipment after the put-away request." };
        ClearStock(ClientPackReadStatus.NotObserved, "Refreshing stock after the put-away request.");
        _readRequested = true;
    }

    private void ResetStowMessage()
    {
        _stowMessage = ClientPackAppSnapshot.DefaultStowMessage;
        _stowNeedsAttention = false;
    }

    private void ResetEquipment()
    {
        _loadout = new();
        ResetAmmunition();
    }

    private void ResetAmmunition()
    {
        _ammunitionDefinition = null;
        _ammunitionItemIds = Array.Empty<int>();
        _ammunitionObservedAt = 0;
        _ammunition = new();
    }

    private void ReadLoadout()
    {
        if (!Inventory.TryCaptureEquipmentSlots(out var entries, out _))
        {
            _loadout = new() { Status = ClientPackReadStatus.Unavailable,
                Message = "Equipment is unavailable. Refresh after your current activity finishes." };
            return;
        }
        var rows = entries.Select(entry => new ClientPackEquipmentSlot(entry.Slot, EquipmentSlotName(entry.Slot),
            entry.CurrentItemFound, entry.CurrentItemId, EquipmentItemName(entry.CurrentItemFound, entry.CurrentItemId),
            entry.NextItemFound, entry.NextItemId, EquipmentItemName(entry.NextItemFound, entry.NextItemId),
            entry.CurrentItemId.HasValue && CanBrowseItem(entry.CurrentItemId.Value), CanStow(entry),
            entry.CurrentReadSucceeded && entry.NextReadSucceeded ? ClientPackReadStatus.Ready
                : ClientPackReadStatus.Partial)).ToArray();
        // Current/next observations are useful independently of IsSlotLocked.
        // That native predicate can be a placeholder and conveys no readiness.
        var partial = rows.Any(row => row.Status != ClientPackReadStatus.Ready);
        _loadout = new()
        {
            Status = partial ? ClientPackReadStatus.Partial : ClientPackReadStatus.Ready,
            Message = partial ? "Some equipment readings are unavailable. Unknown slots are not empty."
                : "Current and pending items are shown separately.",
            ObservedAt = Now, Slots = Array.AsReadOnly(rows)
        };
    }

    private bool CanStow(InventoryEquipmentSlotCapture entry) => entry.ReadSucceeded
        && entry.Slot is ItemEquipmentSlot.Chest or ItemEquipmentSlot.Feet or ItemEquipmentSlot.FullBody or ItemEquipmentSlot.Eyes
        && entry.CurrentItemFound == true && entry.CurrentItemId > 0 && entry.NextItemFound == false
        && entry.NativeSlotLockResult == false
        && _catalogItems.FirstOrDefault(item => item.Id == entry.CurrentItemId)
            is { CanNotBeStored: false, AlwaysDropOnUnequip: false };

    private void ReadAmmunition()
    {
        if (_selectedItemId == 0) { ResetAmmunition(); return; }
        if (_ammunitionDefinition == null || _ammunitionDefinition.ItemId != _selectedItemId)
        {
            _ammunitionDefinition = ItemCatalog.CaptureAmmunition(_selectedItemId);
            _ammunitionObservedAt = Now;
            _ammunitionItemIds = _ammunitionDefinition.CompatibleAmmoCount.HasValue
                ? _ammunitionDefinition.CompatibleAmmoItemIds.Where(id => id > 0)
                    .Select(id => id.GetValueOrDefault()).Distinct().ToArray()
                : Array.Empty<int>();
        }
        var definition = _ammunitionDefinition;
        if (!definition.CompatibleAmmoCount.HasValue)
        {
            _ammunition = new() { Status = ClientPackReadStatus.Unavailable, ItemId = _selectedItemId,
                MetadataObservedAt = _ammunitionObservedAt, Message = "Ammunition compatibility is unavailable for this item." };
            return;
        }
        var ids = _ammunitionItemIds;
        IReadOnlyList<InventoryItemCapture> stock = Array.Empty<InventoryItemCapture>();
        var readStock = ids.Length == 0 || Inventory.TryCaptureItems(ids, out stock, out _,
            includeEquipped: true, checkNextSlot: false);
        var counts = stock.ToDictionary(entry => entry.ItemId);
        var rows = definition.CompatibleAmmoItemIds.Select(id =>
        {
            var knownId = id > 0;
            var entry = knownId && counts.TryGetValue(id.GetValueOrDefault(), out var captured) ? captured : null;
            var state = !knownId ? ClientPackReadStatus.Uncertain
                : entry is not { ReadSucceeded: true } ? ClientPackReadStatus.Unavailable
                : entry.Count < 0 ? ClientPackReadStatus.Uncertain : ClientPackReadStatus.Ready;
            return new ClientPackAmmunitionEntry(id, knownId ? ItemName(id.GetValueOrDefault()) : "Unknown ammunition entry",
                entry is { Count: >= 0 } ? entry.Count : null, entry?.OwnsItem, state,
                knownId && CanBrowseItem(id.GetValueOrDefault()));
        }).ToArray();
        var partial = !definition.Ready || rows.Any(row => row.Status != ClientPackReadStatus.Ready);
        var empty = definition.Ready && definition.CompatibleAmmoCount == 0;
        _ammunition = new()
        {
            Status = empty ? ClientPackReadStatus.Empty : partial ? ClientPackReadStatus.Partial : ClientPackReadStatus.Ready,
            ItemId = _selectedItemId, ReportedCount = definition.CompatibleAmmoCount, Truncated = definition.Truncated,
            MetadataObservedAt = _ammunitionObservedAt, StockObservedAt = readStock && ids.Length > 0 ? Now : 0,
            Entries = Array.AsReadOnly(rows),
            Message = empty ? "This item's definition lists no compatible ammunition."
                : rows.Length == 0 ? "No ammunition entries could be confirmed for this item."
                : definition.Truncated ? "The ammunition list is partial. Stock is shown for the available entries."
                : partial ? "Some ammunition or stock readings are unavailable."
                : "Compatible ammunition and your current stock."
        };
    }

    private bool CanBrowseItem(int itemId) => _catalogItems.Any(item => item.Id == itemId);
    private string ItemName(int itemId) => _catalogItems.FirstOrDefault(item => item.Id == itemId)?.DisplayName
        ?? "Item " + itemId.ToString(CultureInfo.InvariantCulture);
    private string EquipmentItemName(bool? found, int? itemId) => found == false ? "Empty"
        : found == true && itemId.HasValue ? ItemName(itemId.Value) : "Unknown";

    private static string EquipmentSlotName(ItemEquipmentSlot slot) => slot switch
    {
        ItemEquipmentSlot.RightHand => "Right hand",
        ItemEquipmentSlot.LeftHand => "Left hand",
        ItemEquipmentSlot.Chest => "Chest",
        ItemEquipmentSlot.Feet => "Feet",
        ItemEquipmentSlot.FullBody => "Full body",
        ItemEquipmentSlot.Eyes => "Eyes",
        _ => "Unknown slot"
    };
}
