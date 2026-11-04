// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using Modstoke.Sons;
using Sons.Inventory;
using Sons.Items.Core;
using Sons.Save;
using SonsSdk;
using TheForest.Items.Inventory;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Client;

// Local native inventory entries. Only the ordinary GPS item is ever equipped.
internal sealed partial class ClientDeviceInventory : IDisposable
{
    internal const int TabletItemId = 742101;
    internal const int WatchItemId = 742102;
    private static ClientDeviceInventory? _active;
    private readonly Func<bool> _selectTablet;
    private readonly Harmony _patches = new("modstoke.client.device-inventory");
    private readonly List<MethodInfo> _targets = new();
    private readonly DeviceSave _save;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private ManualLogSource? _log;
    private string _setupStage = "waiting for inventory";
    private PlayerInventory? _inventory;
    private ItemInstanceManager? _manager;
    private ItemDatabase? _database;
    private ItemData? _tabletData;
    private ItemData? _watchData;
    private DeviceLayout? _tabletLayout;
    private DeviceLayout? _watchLayout;
    private bool _started;
    private bool _saveRegistered;
    private bool _failed;
    private int _failures;
    private float _retryAt;
    private bool _applyOwnership;
    private bool _tabletOwned = true;
    private bool _watchOwned = true;
    private bool _watchWorn = true;
    private float _nextBind;
    private string? _matStatus;
    private int _selectionFrame = -1;

    internal ClientDeviceInventory(Func<bool> selectTablet, Func<bool>? selectDrone = null, Func<bool>? selectController = null)
    {
        _selectTablet = selectTablet;
        _selectDrone = selectDrone;
        _selectController = selectController;
        _save = new DeviceSave(this);
    }

    internal bool HasTablet { get; private set; }
    internal bool HasWatch { get; private set; }
    internal bool WatchWorn => HasWatch && _watchWorn;
    internal int SessionRevision { get; private set; }
    internal bool HasLiveInventory => SameInventory(LocalPlayer.Inventory) && IsLoadedInventory(_inventory);
    internal string Status { get; private set; } = "Waiting for the local inventory.";

    internal void Start()
    {
        if (_started) return;
        if (_active != null) throw new InvalidOperationException("Device inventory is already registered.");
        _active = this;
        try
        {
            _log = BepInEx.Logging.Logger.CreateLogSource("Modstoke Client inventory");
            InstallHooks();
            SonsSaveTools.Register(_save);
            _saveRegistered = true;
            _started = true;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal void Update()
    {
        if (!_started || Environment.CurrentManagedThreadId != _thread) return;
        if (_failed)
        {
            // A stale native layout must not take the tablet away for the rest of the session.
            if (_failures > 3 || Time.unscaledTime < _retryAt) return;
            _failed = false;
            _nextBind = 0;
            _log?.LogInfo("Retrying field device setup.");
        }
        try
        {
            _setupStage = "checking world readiness";
            if (SaveGameManager.LoadInProgress()) return;
            var inventory = LocalPlayer.Inventory;
            if (!IsLoadedInventory(inventory))
            {
                if (_inventory is not null)
                {
                    ReleaseInventory();
                    ResetOwnership();
                }
                Status = "Waiting for the local inventory.";
                return;
            }
            if (!SameInventory(inventory))
            {
                _setupStage = "releasing the previous inventory";
                ReleaseInventory();
                // IsInWorld describes the current view. Opening the inventory must
                // not remove its items or reset the character's saved ownership.
                if (!GameRuntime.State.IsInWorld) return;
                if (Time.unscaledTime < _nextBind) return;
                _nextBind = Time.unscaledTime + 1;
                _setupStage = "registering device definitions";
                if (!TryRegisterDefinitions()) return;
                _setupStage = "locating the inventory mat";
                if (!BindInventory(inventory)) return;
                _applyOwnership = true;
            }
            if (_applyOwnership)
            {
                _setupStage = "adding the tablet";
                ApplyCount(TabletItemId, _tabletOwned);
                _setupStage = "adding the watch";
                ApplyCount(WatchItemId, _watchOwned);
                _setupStage = "adding the drone controller";
                ApplyCount(ControllerItemId, _controllerOwned);
                _setupStage = "refreshing the tablet model";
                _tabletLayout!.Refresh();
                _setupStage = "refreshing the watch model";
                _watchLayout!.Refresh();
                _setupStage = "refreshing the drone controller model";
                _controllerLayout!.Refresh();
                _applyOwnership = false;
                _log?.LogInfo("Field devices added to the inventory mat.");
            }
            _setupStage = "refreshing the drone kit";
            ApplyDroneKit();
            _setupStage = "reading device ownership";
            HasTablet = _manager!.GetItemCount(TabletItemId) > 0;
            HasWatch = _manager.GetItemCount(WatchItemId) > 0;
            HasController = _manager.GetItemCount(ControllerItemId) > 0;
            _tabletOwned = HasTablet;
            _watchOwned = HasWatch;
            _controllerOwned = HasController;
            Status = HasTablet && HasWatch
                ? "Field tablet and watch are in your inventory. Select the watch to wear or remove it."
                : "Field devices reflect this player's saved inventory.";
        }
        catch (Exception error)
        {
            _failed = true;
            _failures++;
            _retryAt = Time.unscaledTime + 2;
            HasTablet = HasWatch = HasController = false;
            Status = "Field inventory unavailable (" + _setupStage + "): " + error.Message;
            _log?.LogError("Setup stopped while " + _setupStage + ": " + error);
            ReleaseInventory();
            ReleaseDefinitions();
        }
    }

    private bool SameInventory(PlayerInventory? inventory) => inventory != null && inventory && _inventory != null && _inventory
        && inventory.Pointer == _inventory.Pointer && _manager && inventory._itemInstanceManager
        && _manager != null && inventory._itemInstanceManager.Pointer == _manager.Pointer;

    private static bool IsLoadedInventory(PlayerInventory? inventory)
    {
        if (inventory == null || !inventory || !inventory._itemInstanceManager) return false;
        var scene = inventory.gameObject.scene;
        return scene.IsValid() && scene.isLoaded;
    }

    private void ApplyCount(int id, bool owned)
    {
        var count = _manager!.GetItemCount(id);
        if (owned && count == 0)
        {
            // Keep the item out of pickup, automatic equip and network possession callbacks.
            if (!_manager.TryAddItems(id, 1, true))
                throw new InvalidOperationException("The native inventory rejected a field device.");
        }
        else if (!owned && count > 0)
            _manager.RemoveItems(id, count, true);
    }

    private void ResetOwnership()
    {
        SetDroneKit(false, false, 0);
        _tabletOwned = _watchOwned = _watchWorn = _controllerOwned = true;
        _applyOwnership = true;
        HasTablet = HasWatch = HasController = false;
    }

    private void BeginLoad()
    {
        // A load can replace a character while retaining the same inventory component.
        ReleaseInventory();
        ResetOwnership();
        _failed = false;
        _failures = 0;
        _nextBind = 0;
        SessionRevision++;
    }

    private bool OwnsDefinition(int id)
    {
        var expected = id switch
        {
            TabletItemId => _tabletData,
            WatchItemId => _watchData,
            DroneItemId => _droneData,
            DroneBatteryItemId => _droneBatteryData,
            ControllerItemId => _controllerData,
            _ => null
        };
        return expected != null && expected && ItemDatabaseManager._itemsCache != null
            && ItemDatabaseManager._itemsCache.TryGetValue(id, out var actual)
            && actual && actual.Pointer == expected.Pointer;
    }

    private bool OwnsGroup(InventoryLayoutItemGroup group)
    {
        if (!group || !HasLiveInventory) return false;
        return (_tabletLayout != null && _tabletLayout.Group && group.Pointer == _tabletLayout.Group.Pointer)
            || (_watchLayout != null && _watchLayout.Group && group.Pointer == _watchLayout.Group.Pointer)
            || (_droneLayout != null && _droneLayout.Group && group.Pointer == _droneLayout.Group.Pointer)
            || (_controllerLayout != null && _controllerLayout.Group && group.Pointer == _controllerLayout.Group.Pointer)
            || (_droneBatteryLayout != null && _droneBatteryLayout.Group && group.Pointer == _droneBatteryLayout.Group.Pointer);
    }

    private void Select(InventoryLayoutItemGroup group)
    {
        // Failed cleanup can leave a guarded native group alive after selection stops.
        if (!_started || _failed) return;
        if (_selectionFrame == Time.frameCount || !OwnsGroup(group) || _inventory == null || !_inventory
            || GameRuntime.State.PauseMenuOpen || SaveGameManager.LoadInProgress()) return;
        var highlighted = false;
        foreach (var item in group._layoutItems)
            if (item && item.IsHighlighted) { highlighted = true; break; }
        if (!highlighted || _manager!.GetItemCount(group._itemId) < 1) return;
        _selectionFrame = Time.frameCount;
        if (group._itemId == TabletItemId)
        {
            if (!_selectTablet()) return;
        }
        else if (group._itemId == WatchItemId)
            _watchWorn = !_watchWorn;
        else if (group._itemId == ControllerItemId)
        {
            if (!HasController || _selectController == null || !_selectController()) return;
        }
        else if (!_droneKitReady || _selectDrone == null || !_selectDrone()) return;
        _inventory.Close();
    }

    private void ReleaseInventory()
    {
        // A destroyed Unity component still marks the binding we must retire.
        if (_inventory is not null) SessionRevision++;
        HasTablet = HasWatch = HasController = false;
        // Native OnDisable unhooks these exact groups before their entries disappear.
        _tabletLayout?.Dispose();
        _watchLayout?.Dispose();
        _controllerLayout?.Dispose();
        _droneLayout?.Dispose();
        _droneBatteryLayout?.Dispose();
        _tabletLayout = _watchLayout = null;
        _controllerLayout = null;
        _droneLayout = _droneBatteryLayout = null;
        _anchor = null;
        _applyDroneKit = true;
        if (_manager != null && _manager)
        {
            foreach (var id in new[] { TabletItemId, WatchItemId, DroneItemId, DroneBatteryItemId, ControllerItemId })
                if (OwnsDefinition(id))
                    _manager.RemoveAll(id, true);
        }
        _manager = null;
        _inventory = null;
    }

    public void Dispose()
    {
        _started = false;
        ReleaseInventory();
        if (_saveRegistered)
        {
            SonsSaveTools.Unregister(_save);
            _saveRegistered = false;
        }
        foreach (var target in _targets)
            _patches.Unpatch(target, HarmonyPatchType.All, _patches.Id);
        _targets.Clear();
        ReleaseDefinitions();
        if (ReferenceEquals(_active, this)) _active = null;
        if (_log != null)
        {
            BepInEx.Logging.Logger.Sources.Remove(_log);
            _log.Dispose();
            _log = null;
        }
    }
}
