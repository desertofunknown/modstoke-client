// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using HarmonyLib;
using Sons.Inventory;
using Sons.Items.Core;
using Sons.Save;
using SonsSdk;
using TheForest.Items.Inventory;
using TheForest.Utils;
using UnityEngine.InputSystem;
using InventoryData = TheForest.Items.Inventory.PlayerInventory.PlayerInventorySaveData;
using ManagerData = Sons.Inventory.ItemInstanceManager.ItemInstanceManagerSaveData;

namespace Modstoke.Client;

internal sealed partial class ClientDeviceInventory
{
    private void InstallHooks()
    {
        foreach (var name in new[] { nameof(InventoryLayoutItemGroup.OnAlternateInteractWithItem),
            nameof(InventoryLayoutItemGroup.OnToggleItemToQuickSlot), nameof(InventoryLayoutItemGroup.OnAddItemToMat) })
            Patch(typeof(InventoryLayoutItemGroup), name, new[] { typeof(InputAction.CallbackContext) }, nameof(BeforeOtherInteraction));
        // The CallbackContext drop overload shares a native empty stub with unrelated
        // methods. Detour only the parameterless overload that implements dropping.
        Patch(typeof(InventoryLayoutItemGroup), nameof(InventoryLayoutItemGroup.OnDropItemFromInventory),
            Type.EmptyTypes, nameof(BeforeOtherInteraction));
        Patch(typeof(PlayerInventory), nameof(PlayerInventory.TryEquip),
            new[] { typeof(int), typeof(bool), typeof(bool), typeof(bool) }, nameof(BeforeEquipId));
        // Keep the instance overload native: its small nullable value is passed inline,
        // which the current detour marshaler treats as a pointer. Device selection is
        // handled above, and the definitions exclude native equipment and quick slots.
        Patch(typeof(PlayerInventory), nameof(PlayerInventory.TryAddItemToQuickSlot),
            new[] { typeof(ItemData), typeof(bool) }, nameof(BeforeQuickSlot));
        Patch(typeof(ItemHotkeyController), nameof(ItemHotkeyController.SetItemHotkeyFromInventory),
            new[] { typeof(int), typeof(bool) }, nameof(BeforeHotkey));
        Patch(typeof(PlayerInventory),
            nameof(PlayerInventory.Sons_Save_ISaveGameSerializer_TheForest_Items_Inventory_PlayerInventory_PlayerInventorySaveData__OnSerialize),
            Type.EmptyTypes, nameof(AfterSerialize), true);
        Patch(typeof(PlayerInventory),
            nameof(PlayerInventory.Sons_Save_ISaveGameSerializer_TheForest_Items_Inventory_PlayerInventory_PlayerInventorySaveData__OnDeserialize),
            new[] { typeof(InventoryData) }, nameof(AfterDeserialize), true);
        Patch(typeof(SaveGameManager), nameof(SaveGameManager.Load),
            new[] { typeof(string), typeof(SaveGameType) }, nameof(BeforeLoad), pairedPostfix: nameof(AfterLoad));
    }

    private void Patch(Type type, string name, Type[] parameters, string callback, bool postfix = false, string? pairedPostfix = null)
    {
        var target = AccessTools.DeclaredMethod(type, name, parameters)
            ?? throw new MissingMethodException(type.FullName, name);
        _targets.Add(target);
        var hook = new HarmonyMethod(typeof(ClientDeviceInventory), callback) { priority = Priority.Last };
        if (postfix) _patches.Patch(target, postfix: hook);
        else _patches.Patch(target, prefix: hook, postfix: pairedPostfix == null ? null
            : new HarmonyMethod(typeof(ClientDeviceInventory), pairedPostfix) { priority = Priority.Last });
    }

    private static bool BeforeOtherInteraction(InventoryLayoutItemGroup __instance) =>
        _active == null || !_active.OwnsGroup(__instance);

    private static bool BeforeEquipId(int __0, ref bool __result)
    {
        if (_active == null || !_active.OwnsDefinition(__0)) return true;
        __result = false;
        return false;
    }

    private static bool BeforeQuickSlot(ItemData __0, ref bool __result) =>
        !__0 || BeforeEquipId(__0._id, ref __result);

    private static bool BeforeHotkey()
    {
        var owner = _active;
        var item = owner?._inventory?._hoveredItem;
        return item == null || owner == null || !owner.OwnsDefinition(item._itemID);
    }

    private static void BeforeLoad(bool __runOriginal, out SaveData? __state)
    {
        __state = null;
        if (!__runOriginal || _active == null) return;
        __state = _active._save.Save();
        _active.BeginLoad();
    }

    private static void AfterLoad(bool __result, SaveData? __state)
    {
        if (!__result && __state != null) _active?._save.Load(__state);
    }

    private static void AfterDeserialize(PlayerInventory __instance, bool __runOriginal)
    {
        var owner = _active;
        var local = LocalPlayer.Inventory;
        if (owner == null || !__runOriginal || !local || !__instance || local.Pointer != __instance.Pointer) return;
        owner.ReleaseInventory();
        owner._applyOwnership = true;
    }

    private static void AfterSerialize(PlayerInventory __instance, ref InventoryData __result)
    {
        var owner = _active;
        if (owner == null || __result == null || !owner.SameInventory(__instance)) return;
        // Never edit the result's lists: native code or other mods may retain them.
        // Unknown modules and every unaffected object remain byte-for-byte native data.
        var original = __result;
        var projected = new InventoryData
        {
            Version = original.Version,
            QuickSelect = original.QuickSelect,
            EquippedItems = original.EquippedItems,
            ItemInstanceManagerData = original.ItemInstanceManagerData
        };
        if (original.QuickSelect != null)
        {
            var quick = new PlayerInventory.QuickSelectSaveData { Version = original.QuickSelect.Version, Slots = new() };
            if (original.QuickSelect.Slots == null) quick.Slots = null;
            else foreach (var slot in original.QuickSelect.Slots)
                if (slot == null || !owner.OwnsDefinition(slot.ItemId)) quick.Slots.Add(slot);
            projected.QuickSelect = quick;
        }
        if (original.EquippedItems != null)
        {
            projected.EquippedItems = new();
            foreach (var item in original.EquippedItems)
                if (item == null || !owner.OwnsDefinition(item.ItemId)) projected.EquippedItems.Add(item);
        }
        if (original.ItemInstanceManagerData != null)
        {
            var manager = new ManagerData { Version = original.ItemInstanceManagerData.Version, ItemBlocks = new() };
            if (original.ItemInstanceManagerData.ItemBlocks == null) manager.ItemBlocks = null;
            else foreach (var block in original.ItemInstanceManagerData.ItemBlocks)
                if (block == null || !owner.OwnsDefinition(block.ItemId)) manager.ItemBlocks.Add(block);
            projected.ItemInstanceManagerData = manager;
        }
        __result = projected;
    }

    public sealed class SaveData
    {
        public int Version { get; set; } = 1;
        public bool TabletOwned { get; set; }
        public bool WatchOwned { get; set; }
        public bool WatchWorn { get; set; }
        public bool ControllerOwned { get; set; } = true;
    }

    private sealed class DeviceSave : ICustomJsonSaveable<SaveData>
    {
        private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
        };
        private readonly ClientDeviceInventory _owner;
        internal DeviceSave(ClientDeviceInventory owner) => _owner = owner;
        public string Name => "Modstoke.FieldDevices";
        public bool IncludeInPlayerSave => true;
        public string Serialize(SaveData value) => System.Text.Json.JsonSerializer.Serialize(value, JsonOptions);
        public SaveData Deserialize(string json) => System.Text.Json.JsonSerializer.Deserialize<SaveData>(json, JsonOptions)
            ?? throw new InvalidOperationException("Missing Modstoke field-device save data.");
        public SaveData Save() => new()
        {
            TabletOwned = _owner._tabletOwned,
            WatchOwned = _owner._watchOwned,
            WatchWorn = _owner._watchWorn,
            ControllerOwned = _owner._controllerOwned
        };
        public void Load(SaveData data)
        {
            if (data == null || data.Version != 1)
                throw new InvalidOperationException("Unsupported Modstoke field-device save data.");
            _owner._tabletOwned = data.TabletOwned;
            _owner._watchOwned = data.WatchOwned;
            _owner._watchWorn = data.WatchOwned && data.WatchWorn;
            _owner._controllerOwned = data.ControllerOwned;
            _owner._applyOwnership = true;
        }
    }
}
