// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using BepInEx.Logging;
using Il2CppInterop.Runtime.InteropTypes;
using Modstoke.Sons;
using Sons.Inventory;
using Sons.Items.Core;
using Sons.Save;
using TheForest.Items.Inventory;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Client;

// Borrows both empty hands while native equipment lowers the retained items.
internal sealed class ClientWatchEquipment
{
    private enum HandPhase { Held, Stashing, Stashed, Restoring, Returning, Restored }

    private sealed class HandItem
    {
        internal readonly EquipmentSlot Slot;
        internal readonly ItemInstance Item;
        internal readonly GameObject HeldObject;
        internal readonly int Id;
        internal readonly bool Unique;
        internal HandPhase Phase;
        internal int StoredBeforeEquip;
        internal int FailedEquipFrame;
        internal bool ReturnAttempted;

        internal HandItem(EquipmentSlot slot, ItemInstance item, GameObject heldObject, bool unique)
        {
            Slot = slot;
            Item = item;
            HeldObject = heldObject;
            Id = item._itemID;
            Unique = unique;
        }
    }

    private const float TransitionTimeout = 8;
    private const int BackpackId = 402;
    private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("Modstoke Client equipment");
    private PlayerInventory? _inventory;
    private ItemInstanceManager? _manager;
    private Il2CppObjectBase? _items;
    private Transform? _player;
    private PlayerStats? _stats;
    private SaveGameManager? _saveManager;
    private BoltEntity? _entity;
    private Bolt.Entity? _attachment;
    private HandItem? _left;
    private HandItem? _right;
    private int _load;
    private bool _networked;
    private bool _ready;
    private bool _finishing;
    private bool _nativeCall;
    private float _deadline;

    internal bool Active => _inventory is not null;
    internal bool Ready
    {
        get
        {
            if (!Active || _finishing || _nativeCall) return false;
            try { return AdvanceStash(); }
            catch (Exception error) { LogFailure("checking stowed items", error); Abandon(); return false; }
        }
    }

    internal bool TryStash(out string status)
    {
        status = "Finish your current equipment action before checking the watch.";
        if (Active || _nativeCall) return false;
        try
        {
            var inventory = LocalPlayer.Inventory;
            var player = LocalPlayer.Transform;
            var stats = LocalPlayer.Stats;
            var manager = inventory?._itemInstanceManager;
            var items = manager?._itemsMap;
            if (!CanUse(inventory) || player == null || player.WasCollected || stats == null || stats.WasCollected
                || manager == null || manager.WasCollected || items == null || items.WasCollected
                || GameRuntime.IsTitleScene || !SaveGameManager.TryGetInstance(out var saveManager)
                || saveManager == null || saveManager.WasCollected || saveManager._loadingInProgress
                || !NativeIdle(inventory!)) return false;
            // Validate both items before either native stash can mutate inventory.
            if (!TryCapture(inventory!, EquipmentSlot.LeftHand, inventory!.LeftHandItem, out var left, out status)
                || !TryCapture(inventory, EquipmentSlot.RightHand, inventory.RightHandItem, out var right, out status))
                return false;
            _inventory = inventory;
            _manager = manager;
            _items = items;
            _player = player;
            _stats = stats;
            _saveManager = saveManager;
            _networked = BoltNetwork.isRunning;
            _entity = _networked ? LocalPlayer.Entity : null;
            _attachment = _entity?._entity;
            _left = left;
            _right = right;
            _load = SaveLifecycle.State.BeforeLoadCount;
            _deadline = Time.unscaledTime + TransitionTimeout;
            AdvanceStash();
            if (!Active) return false;
            status = "";
            return true;
        }
        catch (Exception error)
        {
            // A native transition may already have started. Never repeat it.
            LogFailure("stowing items", error);
            Abandon();
            status = "The held items could not be put away for the watch.";
            return false;
        }
    }

    internal void Finish(bool restore = true)
    {
        if (!restore || (_left == null && _right == null)) { Abandon(); return; }
        if (!Active || _finishing) return;
        _finishing = true;
        _ready = false;
        _deadline = Time.unscaledTime + TransitionTimeout;
    }

    internal void UpdateRestore(bool handFree)
    {
        if (!Active || !_finishing || _nativeCall) return;
        try
        {
            if (!Owned()) { LogFailure("equipment ownership changed"); Abandon(); return; }
            // Menus and lost focus do not transfer ownership of the stored item.
            if (!CanUse(_inventory)) { _deadline = Time.unscaledTime + TransitionTimeout; return; }
            if (Time.unscaledTime >= _deadline)
            { LogFailure("equipment restoration timed out"); Abandon(); return; }
            // handFree covers the watch/tablet pose. Native hand readiness is
            // checked here so the first restored item's IK cannot block the next.
            if (!handFree || !NativeIdle(_inventory!)) return;
            if (RestoreHand(_right) && RestoreHand(_left)) Abandon();
        }
        catch (Exception error)
        {
            LogFailure("restoring items", error);
            Abandon();
        }
    }

    internal void Abandon()
    {
        _inventory = null;
        _manager = null;
        _items = null;
        _player = null;
        _stats = null;
        _saveManager = null;
        _entity = null;
        _attachment = null;
        _left = _right = null;
        _ready = _finishing = false;
        _deadline = 0;
    }

    private bool AdvanceStash()
    {
        if (!Owned() || (!_ready && Time.unscaledTime >= _deadline))
        { Abandon(); return false; }
        if (!CanUse(_inventory) || !NativeIdle(_inventory!)) return false;
        if (!StashHand(_right) || !StashHand(_left)) return false;
        _ready = true;
        return true;
    }

    private bool StashHand(HandItem? hand)
    {
        if (hand == null || hand.Phase == HandPhase.Stashed) return true;
        if (hand.Phase == HandPhase.Stashing) return CompleteStash(hand);
        // The first stash can invoke callbacks; recheck the next item's storage
        // capacity and held object before starting its own native transition.
        if (!CanStore(_inventory!, hand.Slot, hand.Item, out _)
            || !HeldObjectMatches(_inventory!, hand))
        { Abandon(); return false; }
        hand.Phase = HandPhase.Stashing;
        _nativeCall = true;
        try
        {
            if (hand.Slot == EquipmentSlot.RightHand) _inventory!.StashRightHandItem(false, true, false);
            else _inventory!.StashLeftHandItem(true, false);
        }
        finally { _nativeCall = false; }
        if (!Owned()) Abandon();
        return false;
    }

    private bool CompleteStash(HandItem hand)
    {
        if (hand.HeldObject != null && hand.HeldObject.activeInHierarchy) return false;
        if (!StillStored(hand)) { Abandon(); return false; }
        hand.Phase = HandPhase.Stashed;
        Log.LogInfo($"Watch stowed {hand.Slot} item {hand.Id}; stored count {StoredCount(hand)}.");
        return true;
    }

    private bool RestoreHand(HandItem? hand)
    {
        // An early key release may leave the second item untouched in its hand.
        if (hand == null || hand.Phase == HandPhase.Held || hand.Phase == HandPhase.Restored) return true;
        if (hand.Phase == HandPhase.Stashing && !CompleteStash(hand)) return false;
        if (hand.Phase == HandPhase.Restoring)
        {
            if (!Same(CurrentItem(hand.Slot), hand.Item)) return false;
            hand.Phase = HandPhase.Restored;
            // The previous-item record belongs to the transfer until equipment
            // has actually committed, not merely accepted a coroutine.
            if (Same(PreviousItem(hand.Slot), hand.Item)) _inventory!.ForgetPreviousItemInSlot(hand.Slot);
            Log.LogInfo($"Watch restored {hand.Slot} item {hand.Id}.");
            return true;
        }
        if (hand.Phase == HandPhase.Returning) return ReturnFailedEquip(hand);
        if (!StillStored(hand)) { Abandon(); return false; }
        var inventory = _inventory!;
        if (!(hand.Slot == EquipmentSlot.RightHand
            ? inventory.CanEquipItemInRightHand(hand.Id) : inventory.CanEquipItemInLeftHand(hand.Id))) return false;
        if (!Owned()) { Abandon(); return false; }
        // An explicit slot selects alternate prop definitions, even when it is
        // the item's usual hand. Use its normal definition and equipment slot.
        // Interop needs a boxed empty nullable rather than a null wrapper.
        var slot = new Il2CppSystem.Nullable<EquipmentSlot>();
        // Native TryEquip removes inventory before looking up this definition.
        if (inventory.GetPropDefinitionForItem(hand.Id, slot) == null)
        { LogFailure("held item definition is unavailable"); Abandon(); return false; }
        if (!Owned() || !StillStored(hand)) { Abandon(); return false; }
        hand.StoredBeforeEquip = StoredCount(hand);
        hand.Phase = HandPhase.Restoring;
        _nativeCall = true;
        try
        {
            if (!inventory.TryEquip(hand.Item, false, false, slot, false, false))
            {
                hand.Phase = HandPhase.Returning;
                hand.FailedEquipFrame = Time.frameCount;
                LogFailure("native equipment request was rejected");
            }
        }
        catch (Exception error)
        {
            // RemoveItem can invoke a failing subscriber before native equip is
            // scheduled. Keep the exact item until its transfer is reconciled.
            hand.Phase = HandPhase.Returning;
            hand.FailedEquipFrame = Time.frameCount;
            LogFailure("native equipment request failed", error);
        }
        finally { _nativeCall = false; }
        return false;
    }

    private bool ReturnFailedEquip(HandItem hand)
    {
        if (Time.frameCount <= hand.FailedEquipFrame) return false;
        // A native operation may have accepted the item before throwing. It
        // owns anything in a current/next slot, including its eventual rollback.
        if (Same(CurrentItem(hand.Slot), hand.Item) || Same(NextItem(hand.Slot), hand.Item))
        { hand.Phase = HandPhase.Restoring; return false; }
        if (TransferReturned(hand))
        { LogFailure("equipment stayed in inventory after a failed restore"); Abandon(); return false; }
        if (!Owned() || !NativeIdle(_inventory!) || CurrentItem(hand.Slot) != null
            || StoredCount(hand) != hand.StoredBeforeEquip - 1)
            return false;
        if (hand.ReturnAttempted) { Abandon(); return false; }
        // Only compensate our failed one-item removal. Never manufacture a new
        // instance, retry a queued equip, or repeat an uncertain AddItem call.
        hand.ReturnAttempted = true;
        _nativeCall = true;
        try { _inventory!.AddItem(hand.Id, 1, true, false, hand.Item); }
        catch (Exception error) { LogFailure("returning a failed equipment transfer", error); }
        finally { _nativeCall = false; }
        LogFailure(SameSession() && TransferReturned(hand) ? "returned item to inventory after a failed restore"
            : "could not confirm return of the retained item");
        Abandon();
        return false;
    }

    private bool Owned() => SameSession()
        && OwnsHand(EquipmentSlot.LeftHand, _left) && OwnsHand(EquipmentSlot.RightHand, _right);

    private bool SameSession() => _inventory != null && !_inventory.WasCollected
        && Same(_inventory, LocalPlayer.Inventory)
        && _player != null && Same(_player, LocalPlayer.Transform)
        && !GameRuntime.IsTitleScene && _load == SaveLifecycle.State.BeforeLoadCount
        && _saveManager != null && !_saveManager.WasCollected
        && SaveGameManager.TryGetInstance(out var saveManager) && Same(_saveManager, saveManager)
        && !saveManager._loadingInProgress && _networked == BoltNetwork.isRunning
        && (!_networked || _entity != null && !_entity.WasCollected && _entity.isAttached
            && Same(_entity, LocalPlayer.Entity) && Same(_attachment, _entity._entity))
        && Same(_manager, _inventory._itemInstanceManager) && Same(_items, _manager!._itemsMap)
        && _stats != null && Same(_stats, LocalPlayer.Stats) && _stats._dyingState == PlayerStats.DyingState.Alive
        && !_stats._playerIsDowned && !_stats._respawning;

    private bool OwnsHand(EquipmentSlot slot, HandItem? hand)
    {
        var next = _inventory!._equipmentSlotsNext;
        if (next == null || next.WasCollected || next.Length <= (int)slot) return false;
        var current = CurrentItem(slot);
        var queued = next[(int)slot];
        if (hand == null) return current == null && queued == null;
        if (hand.Item.WasCollected || hand.Item._itemID != hand.Id) return false;
        if (hand.Phase == HandPhase.Held) return Same(current, hand.Item) && queued == null;
        var previous = PreviousItem(slot);
        if (hand.Phase == HandPhase.Stashing || hand.Phase == HandPhase.Stashed)
            return current == null && queued == null && Same(previous, hand.Item);
        if (previous != null && !Same(previous, hand.Item)) return false;
        if (hand.Phase == HandPhase.Restoring || hand.Phase == HandPhase.Returning)
            return (current == null || Same(current, hand.Item)) && (queued == null || Same(queued, hand.Item));
        return hand.Phase == HandPhase.Restored && Same(current, hand.Item) && queued == null;
    }

    private bool StillStored(HandItem hand)
    {
        var items = _manager?._itemsMap;
        if (hand.Item.WasCollected || hand.Item._itemID != hand.Id
            || items == null || items.WasCollected || !items.TryGetValue(hand.Id, out var stored)
            || stored == null || stored.WasCollected || stored.Count < 1) return false;
        if (!hand.Unique) return true;
        var instances = stored.UniqueInstances;
        if (instances == null || instances.WasCollected) return false;
        for (var index = 0; index < instances.Count; index++)
            if (Same(instances[index], hand.Item)) return true;
        return false;
    }

    private int StoredCount(HandItem hand)
    {
        var items = _manager?._itemsMap;
        return items != null && items.TryGetValue(hand.Id, out var stored) && stored != null ? stored.Count : 0;
    }

    private bool TransferReturned(HandItem hand) => hand.Unique ? StillStored(hand)
        : StoredCount(hand) >= hand.StoredBeforeEquip;

    private ItemInstance? NextItem(EquipmentSlot slot) => _inventory!._equipmentSlotsNext[(int)slot];

    private void LogFailure(string reason, Exception? error = null)
    {
        string Describe(HandItem? hand)
        {
            if (hand == null) return "empty";
            try { return $"{hand.Id}/{hand.Phase}, stored={StoredCount(hand)}, current={CurrentItem(hand.Slot)?._itemID}, next={NextItem(hand.Slot)?._itemID}"; }
            catch { return $"{hand.Id}/{hand.Phase}, inventory unavailable"; }
        }
        Log.LogWarning($"Watch: {reason}; right [{Describe(_right)}], left [{Describe(_left)}]."
            + (error == null ? "" : " " + error));
    }

    private static bool TryCapture(PlayerInventory inventory, EquipmentSlot slot, ItemInstance? item,
        out HandItem? hand, out string status)
    {
        hand = null;
        status = "Finish your current equipment action before checking the watch.";
        if (item == null) return true;
        if (!CanStore(inventory, slot, item, out status)) return false;
        var objects = inventory.InventoryProps?._gameObjectInstances;
        if (objects == null || objects.WasCollected
            || !objects.TryGetValue(item._itemID, out var heldObject) || heldObject == null) return false;
        hand = new HandItem(slot, item, heldObject, item.Data._eachInstanceIsUnique);
        return true;
    }

    private static bool CanStore(PlayerInventory inventory, EquipmentSlot slot, ItemInstance item, out string status)
    {
        status = "Put this item away before checking the watch.";
        if (item.WasCollected) return false;
        var data = item.Data;
        var items = inventory._itemInstanceManager?._itemsMap;
        if (data == null || data._id != item._itemID || data._equipmentSlot != slot
            || data._alwaysDropOnUnequip || data._canNotBeStored || data._maxAmount < 1
            || !inventory.Owns(BackpackId, null) || items == null || items.WasCollected) return false;
        if (items.TryGetValue(item._itemID, out var stored) && stored != null
            && (stored.WasCollected || stored.Count >= data._maxAmount))
        {
            status = slot == EquipmentSlot.RightHand
                ? "Make room to put away the right-hand item before checking the watch."
                : "Make room to put away the left-hand item before checking the watch.";
            return false;
        }
        return true;
    }

    private static bool HeldObjectMatches(PlayerInventory inventory, HandItem hand)
    {
        var objects = inventory.InventoryProps?._gameObjectInstances;
        return objects != null && !objects.WasCollected && hand.HeldObject != null
            && objects.TryGetValue(hand.Id, out var current) && Same(current, hand.HeldObject);
    }

    private ItemInstance? CurrentItem(EquipmentSlot slot) => slot == EquipmentSlot.RightHand
        ? _inventory!.RightHandItem : _inventory!.LeftHandItem;

    private ItemInstance? PreviousItem(EquipmentSlot slot) => slot == EquipmentSlot.RightHand
        ? _inventory!.PreviousRightHandItem : _inventory!.PreviousLeftHandItem;

    private static bool NativeIdle(PlayerInventory inventory) => !LocalPlayer.IsInMidAction
        && inventory._currentEquipState == PlayerInventory.HandEquipState.Idle && NextHandsEmpty(inventory);

    private static bool NextHandsEmpty(PlayerInventory inventory)
    {
        var next = inventory._equipmentSlotsNext;
        return next != null && !next.WasCollected
            && next.Length > Math.Max((int)EquipmentSlot.LeftHand, (int)EquipmentSlot.RightHand)
            && next[(int)EquipmentSlot.LeftHand] == null && next[(int)EquipmentSlot.RightHand] == null;
    }

    private static bool CanUse(PlayerInventory? inventory)
    {
        var stats = LocalPlayer.Stats;
        return inventory != null && inventory.isActiveAndEnabled && LocalPlayer.IsInWorld
            && !GameRuntime.State.PauseMenuOpen && inventory._currentView == PlayerInventory.PlayerViews.World
            && !inventory._IsInQuickSelect_k__BackingField && !inventory._IsThrowing_k__BackingField
            && inventory.HeldOnlyItemController?.HasItem != true
            && stats != null && stats._dyingState == PlayerStats.DyingState.Alive
            && !stats._playerIsDowned && !stats._respawning;
    }

    private static bool Same(Il2CppObjectBase? first, Il2CppObjectBase? second) => first is not null
        && second is not null && !first.WasCollected && !second.WasCollected && first.Pointer == second.Pointer;
}
