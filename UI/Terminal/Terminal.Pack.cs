// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Modstoke.Sons;

namespace Modstoke.Client;

internal sealed partial class ClientTerminal
{
    private void StowPackItem(ItemEquipmentSlot slot, int itemId)
    {
        if (!_tablet.IsOpen || !_pack.CanStowItem(slot, itemId)) return;
        var generation = _pack.Snapshot.Generation;
        var requested = false;
        try
        {
            // The tablet still owns the original hand items it will restore on close.
            if (_tablet.ReservesInventoryItem(itemId))
            {
                _pack.RefuseStow(slot, itemId, "Put away the tablet before changing this item.");
                return;
            }
            if (!_pack.BeginStow(slot, itemId)) return;
            requested = true;
            var confirmed = Inventory.TryStowEquipmentSlot(slot, itemId, out var result);
            _pack.FinishStow(generation, slot, confirmed, result);
            if (!confirmed)
                _log?.LogWarning($"Pack stow for {slot}, item {itemId}: {result.Detail} "
                    + $"Invocation attempted={result.InvocationAttempted}, slot empty={result.ObservedEmpty}.");
        }
        catch (Exception error)
        {
            if (requested) _pack.FinishStow(generation, slot, false, null);
            else _pack.RefuseStow(slot, itemId, "Pack could not check this item. Nothing was put away.");
            _log?.LogError($"Pack stow for {slot}, item {itemId} failed: {error}");
        }
        finally
        {
            _nextRefresh = 0;
        }
    }

    private void DropPackItem(int itemId)
    {
        if (!_tablet.IsOpen || !_pack.CanDropItem(itemId)) return;
        var generation = _pack.Snapshot.Generation;
        var requested = false;
        try
        {
            // Closing the tablet restores these exact hand items. They must remain in its custody.
            if (_tablet.ReservesInventoryItem(itemId))
            {
                _pack.RefuseDrop(itemId, "Put away the tablet before dropping this item.");
                return;
            }
            if (!_pack.BeginDrop(itemId)) return;
            requested = true;
            var confirmed = Inventory.TryDropCountedItem(itemId, out _, out var result);
            _pack.FinishDrop(generation, itemId, confirmed, result);
            if (!confirmed)
                _log?.LogWarning($"Pack drop for item {itemId}: {result.Detail} "
                    + $"Debit attempted={result.DebitAttempted}, accepted={result.NativeDebitAccepted}, observed={result.DebitObserved}; "
                    + $"spawn attempted={result.SpawnAttempted}, returned={result.SpawnReturned}, pickup observed={result.PickupObserved}; "
                    + $"count before={result.BeforeCount}, after={result.AfterCount}.");
        }
        catch (Exception error)
        {
            if (requested) _pack.FinishDrop(generation, itemId, false, null);
            else _pack.RefuseDrop(itemId, "Pack could not check this item. Nothing was dropped.");
            _log?.LogError("Pack drop for item " + itemId + " failed: " + error);
        }
        finally
        {
            _nextRefresh = 0;
        }
    }

    private static bool IsNativeCraftingMatVisible()
    {
        return Inventory.TryReadView(out var view, out _) && view.IsInventoryOpen;
    }

    private void DraftPackSupplies()
    {
        if (!_pack.TryBuildSupplyDraft(out var title, out var body, out var status))
        {
            Feedback(status);
            return;
        }

        var os = _os.Snapshot;
        if (!os.Ready || os.Generation != _osGeneration) return;
        RememberNoteDraft(os.Field);
        if (_noteDrafts.ContainsKey(Guid.Empty))
        {
            SelectNoteDraft(null);
            Select(ClientTerminalApp.Journal);
            Feedback("Your unsaved new note is still here. Save or discard it before preparing a supply list.");
            return;
        }
        if (os.Field.Notes.Count >= 128)
        {
            Feedback("Your notebook is full. Remove a saved note before preparing a supply list.");
            return;
        }

        SelectNoteDraft(null);
        _noteTitle = title;
        _noteBody = body;
        _notePlaceId = _noteBrowsePlaceId = null;
        RememberNoteDraft(os.Field);
        Select(ClientTerminalApp.Journal);
        Feedback("Supply list prepared as a Notebook draft. Choose Save note to keep it.");
        _nextRefresh = 0;
    }
}
