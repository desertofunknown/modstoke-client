// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Linq;
using Modstoke.Sons;

namespace Modstoke.Client;

internal sealed partial class ClientPackApp
{
    private bool _dropNeedsAttention;
    private string _dropMessage = ClientPackAppSnapshot.DefaultDropMessage;

    internal bool CanDropItem(int itemId) => CanChangeInventory && itemId > 0 && itemId == _selectedItemId
        && _rows.Any(row => row.Id == itemId && row.Status == ClientPackReadStatus.Ready
            && row.Count > 0 && row.Owned == true && row.Equipped == false);

    internal bool BeginDrop(int itemId)
    {
        if (!CanDropItem(itemId)) return false;
        _actionInProgress = true;
        _dropMessage = "Dropping one item…";
        _dropNeedsAttention = false;
        // Consume the displayed stock before native callbacks can re-enter the UI.
        ClearStock(ClientPackReadStatus.NotObserved, "Refreshing stock after the drop request.");
        _readRequested = true;
        Publish();
        return true;
    }

    internal void FinishDrop(long generation, int itemId, bool confirmed, ItemDropResult? result)
    {
        if (!OnThread || _disposed || generation != _generation) return;
        _actionInProgress = false;
        if (itemId == _selectedItemId)
        {
            _dropNeedsAttention = !confirmed;
            _dropMessage = confirmed ? "One item was removed from your pack and its pickup appeared nearby."
                : result is { DebitAttempted: false, SpawnAttempted: false }
                    ? "Nothing was dropped. Pack cannot drop this item right now; use the game's inventory."
                : result?.DebitObserved == true
                    ? "One item left your pack, but the drop could not be confirmed. Check your pack and the ground before dropping again."
                    : "The drop could not be confirmed. Check your pack and the ground before dropping again.";
        }
        ClearStock(ClientPackReadStatus.NotObserved, "Refreshing stock after the drop request.");
        _readRequested = true;
        Publish();
    }

    internal void RefuseDrop(int itemId, string message)
    {
        if (!CanDropItem(itemId)) return;
        _dropMessage = message;
        _dropNeedsAttention = true;
        Publish();
    }

    private void ResetDropMessage()
    {
        _dropMessage = ClientPackAppSnapshot.DefaultDropMessage;
        _dropNeedsAttention = false;
    }
}
