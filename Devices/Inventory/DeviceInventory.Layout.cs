// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Endnight.Rendering;
using Modstoke.Sons;
using Sons.Inventory;
using Sons.Items.Core;
using SonsSdk;
using TheForest.Items.Inventory;
using UnityEngine;
using UnityEngine.Localization.Settings;

namespace Modstoke.Client;

internal sealed partial class ClientDeviceInventory
{
    private readonly List<Texture2D> _icons = new();
    private enum DeviceKind { Tablet, Watch, Drone, DroneBattery, Controller }

    private bool TryRegisterDefinitions()
    {
        var database = ItemDatabaseManager._instance;
        if (!database || ItemDatabaseManager._itemsCache == null) return false;
        if (_database != null && (!_database || _database.Pointer != database.Pointer)) ReleaseDefinitions();
        if (_tabletData != null && _tabletData && _watchData != null && _watchData
            && _controllerData != null && _controllerData
            && _droneData != null && _droneData && _droneBatteryData != null && _droneBatteryData)
        {
            if (!OwnsDefinition(TabletItemId) || !OwnsDefinition(WatchItemId)
                || !OwnsDefinition(ControllerItemId)
                || !OwnsDefinition(DroneItemId) || !OwnsDefinition(DroneBatteryItemId))
                throw new InvalidOperationException("Another item registration replaced a field device.");
            return true;
        }
        var donor = ItemDatabaseManager.ItemById(479); // Feather: no held-item behavior or modules.
        if (!donor || donor._uiData == null) return false;
        // RegisterItem writes translated labels immediately. World activation can
        // precede the Items table becoming available; leave registration pending.
        _setupStage = "waiting for item labels";
        if (LocalizationSettings.StringDatabase == null || LocalizationTools.ItemsTable == null)
        {
            Status = "Waiting for inventory item labels.";
            return false;
        }
        var registeredDefinitions = ItemRegistration.GetRegisteredItemDefinitions();
        foreach (var id in new[] { TabletItemId, WatchItemId, DroneItemId, DroneBatteryItemId, ControllerItemId })
        {
            foreach (var definition in registeredDefinitions)
                if (definition.ItemId == id)
                    throw new InvalidOperationException("Field-device item ID " + id + " is reserved by another mod.");
            if (ItemDatabaseManager._itemsCache.ContainsKey(id))
                throw new InvalidOperationException("Field-device item ID " + id + " is already registered.");
            foreach (var item in database._itemDataList)
                if (item && item._id == id)
                    throw new InvalidOperationException("Field-device item ID " + id + " is already in the database.");
        }
        _database = database;
        _setupStage = "creating the tablet definition";
        _tabletData = CreateDefinition(donor, TabletItemId, "Field tablet",
            "Modstoke field tablet. Open your map, messages and field apps.", DeviceKind.Tablet);
        _setupStage = "creating the watch definition";
        _watchData = CreateDefinition(donor, WatchItemId, "Field watch",
            "Modstoke field watch. Select to wear or remove; inspect it while worn.", DeviceKind.Watch);
        _setupStage = "creating the drone controller definition";
        _controllerData = CreateDefinition(donor, ControllerItemId, "Drone controller",
            "Dedicated handheld drone controller. Open the flight display and controls.", DeviceKind.Controller);
        _setupStage = "creating the drone kit definitions";
        _droneData = CreateDefinition(donor, DroneItemId, "Field drone",
            "Unarmed field quadcopter. Open the tablet Drone app to deploy and fly.", DeviceKind.Drone);
        _droneBatteryData = CreateDefinition(donor, DroneBatteryItemId, "Rechargeable drone pack",
            "Spare drone battery pack. Manage replacement and charging in the tablet Drone app.", DeviceKind.DroneBattery);
        _setupStage = "registering tablet labels";
        ItemTools.RegisterItem(_tabletData);
        _setupStage = "registering watch labels";
        ItemTools.RegisterItem(_watchData);
        _setupStage = "registering drone controller labels";
        ItemTools.RegisterItem(_controllerData);
        _setupStage = "registering drone kit labels";
        ItemTools.RegisterItem(_droneData);
        ItemTools.RegisterItem(_droneBatteryData);
        return true;
    }

    private ItemData CreateDefinition(ItemData donor, int id, string title, string description, DeviceKind kind)
    {
        var data = UnityEngine.Object.Instantiate(donor);
        try
        {
            data.name = "Modstoke " + title + " definition";
            data._id = id;
            data._name = data._editorName = title;
            data._type = global::Sons.Items.Core.Types.Special;
            data._maxAmount = kind == DeviceKind.DroneBattery ? 2 : 1;
            data._eachInstanceIsUnique = false;
            data._canBeHotKeyed = data._allowQuickSelect = false;
            data._canBeSpawned = false;
            data._excludeFromSpawnAll = true;
            data._canNotBeStored = true;
            data._preventEquipFromInventoryView = true;
            data._dropsOnDeath = data._alwaysDropOnUnequip = false;
            data._hasFirstLook = false;
            data._isPerishable = data._isVolumeContainer = data._isDismemberedLimb = false;
            data._isGPSLocator = data._canBePlated = data._isUpgradeableArrow = false;
            data._hasBloodReveal = data._hasVisualVariant = data._isStructureElement = false;
            data._pickupPrefab = data._pickupBundlePrefab = data._heldPrefab = data._propPrefab = null;
            data._ownedIfOtherItemIsOwned = new();
            // Select allocation by length, not the native-pointer constructor.
            data._ownedStatEffect = new(size: 0);
            data._equippedStatEffect = new(size: 0);
            data._usedStatEffect = new(size: 0);
            data._tags = new();
            data._aliases = new();
            var icon = kind == DeviceKind.Controller ? CreateControllerIcon()
                : kind == DeviceKind.Drone || kind == DeviceKind.DroneBattery
                    ? CreateDroneIcon(kind == DeviceKind.DroneBattery) : CreateIcon(kind == DeviceKind.Watch);
            _icons.Add(icon);
            // ItemUiData is a native managed class, not a separately cloned Unity asset.
            data._uiData = new ItemUiData
            {
                _itemId = id,
                _title = title,
                _titlePlural = title,
                _description = description,
                _descriptionShowFor = ItemUiData.AllowedDifficulties.All,
                _hasDescription = true,
                _translationKey = "MODSTOKE_FIELD_" + kind.ToString().ToUpperInvariant(),
                _leftClick = ItemUiData.LeftClickCommands.use,
                _rightClick = ItemUiData.RightClickCommands.None,
                _amountDisplay = kind == DeviceKind.DroneBattery ? donor._uiData._amountDisplay : ItemUiData.AmountDisplay.None,
                _applyCustomActionText = true,
                _leftActionCustomText = kind == DeviceKind.Watch ? "Wear / remove"
                    : kind == DeviceKind.Controller ? "Use controller"
                    : kind == DeviceKind.Tablet ? "Open tablet" : "Open Drone app",
                _icon = icon,
                _outlineIcon = icon,
                _uiColors = donor._uiData._uiColors
            };
            return data;
        }
        catch
        {
            UnityEngine.Object.Destroy(data);
            throw;
        }
    }

    private static Texture2D CreateIcon(bool watch)
    {
        var pixels = new Color[64 * 64];
        for (var y = 0; y < 64; y++)
        for (var x = 0; x < 64; x++)
        {
            var frame = watch ? x >= 14 && x < 50 && y >= 15 && y < 49
                : x >= 5 && x < 59 && y >= 12 && y < 52;
            var strap = watch && x >= 24 && x < 40 && y >= 2 && y < 62;
            var screen = watch ? x >= 19 && x < 45 && y >= 20 && y < 44
                : x >= 11 && x < 53 && y >= 18 && y < 46;
            pixels[y * 64 + x] = screen ? new Color(.22f, .38f, .29f, 1)
                : frame ? new Color(.60f, .65f, .46f, 1)
                : strap ? new Color(.28f, .32f, .23f, 1) : Color.clear;
        }
        return CreateIconTexture(watch ? "Field watch inventory icon" : "Field tablet inventory icon", pixels);
    }

    private static Texture2D CreateIconTexture(string name, Color[] pixels)
    {
        var texture = new Texture2D(64, 64, TextureFormat.RGBA32, false)
        {
            name = name,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };
        texture.SetPixels(pixels);
        // The inventory copies item icons when it builds its UI, which needs readable pixels.
        texture.Apply(false, false);
        return texture;
    }

    private bool BindInventory(PlayerInventory inventory)
    {
        InventoryLayoutItemGroup? anchor = null;
        foreach (var candidate in inventory.GetComponentsInChildren<InventoryLayoutItemGroup>(true))
            if (candidate && candidate._itemId == 412 && candidate.transform.parent
                && candidate._layoutItems != null && candidate._layoutItems.Count > 0 && candidate._layoutItems[0])
            { anchor = candidate; break; }
        if (anchor == null || !anchor) return false;
        _anchor = anchor;
        // Keep partial layouts reachable if initialization or cleanup fails.
        _setupStage = "creating the tablet model";
        _tabletLayout = new DeviceLayout();
        if (!_tabletLayout.TryInitialize(anchor, _tabletData!, DeviceKind.Tablet, Select, out var status))
            return WaitForMat(status);
        _setupStage = "creating the watch model";
        _watchLayout = new DeviceLayout();
        if (!_watchLayout.TryInitialize(anchor, _watchData!, DeviceKind.Watch, Select, out status))
            return WaitForMat(status);
        _setupStage = "creating the drone controller model";
        _controllerLayout = new DeviceLayout();
        if (!_controllerLayout.TryInitialize(anchor, _controllerData!, DeviceKind.Controller, Select, out status))
            return WaitForMat(status);
        _setupStage = "creating the drone kit models";
        _droneLayout = new DeviceLayout();
        if (!_droneLayout.TryInitialize(anchor, _droneData!, DeviceKind.Drone, Select, out status))
            return WaitForMat(status);
        _droneBatteryLayout = new DeviceLayout();
        if (!_droneBatteryLayout.TryInitialize(anchor, _droneBatteryData!, DeviceKind.DroneBattery, Select, out status))
            return WaitForMat(status);
        // Bound only once every layout exists, so a refused attempt never publishes a session.
        _inventory = inventory;
        _manager = inventory._itemInstanceManager;
        _matStatus = null;
        _applyDroneKit = true;
        SessionRevision++;
        return true;
    }

    // A joining player's mat can refuse new layouts for a while; retry on the bind timer instead of stopping.
    private bool WaitForMat(string status)
    {
        if (status != _matStatus)
            _log?.LogWarning("Field devices are waiting for the inventory mat (" + _setupStage + "): " + status);
        _matStatus = status;
        Status = "Waiting for the inventory mat.";
        ReleaseInventory();
        return false;
    }

    private void RemoveDefinition(ItemData? data)
    {
        if (data == null || !data) return;
        var cache = ItemDatabaseManager._itemsCache;
        if (cache != null && cache.TryGetValue(data._id, out var current)
            && current && current.Pointer == data.Pointer)
            cache.Remove(data._id);
        if (_database != null && _database)
        {
            var items = _database._itemDataList;
            for (var i = items.Count - 1; i >= 0; i--)
                if (items[i] && items[i].Pointer == data.Pointer) items.RemoveAt(i);
        }
        UnityEngine.Object.Destroy(data);
    }

    private void ReleaseDefinitions()
    {
        RemoveDefinition(_tabletData);
        RemoveDefinition(_watchData);
        RemoveDefinition(_controllerData);
        RemoveDefinition(_droneData);
        RemoveDefinition(_droneBatteryData);
        _tabletData = _watchData = null;
        _controllerData = null;
        _droneData = _droneBatteryData = null;
        _database = null;
        foreach (var icon in _icons) if (icon) UnityEngine.Object.Destroy(icon);
        _icons.Clear();
    }

    private sealed class DeviceLayout : IDisposable
    {
        private InventoryLayout? _layout;
        private ClientTabletModel? _tablet;
        private ClientDroneControllerModel? _controller;
        private ClientWristWatchModel? _watch;
        private DroneInventoryModel? _drone;
        private InventoryPrimaryActionBinding? _primaryAction;
        private readonly List<Material> _glass = new();
        internal InventoryLayoutItemGroup Group => _layout?.Group!;

        internal bool TryInitialize(InventoryLayoutItemGroup anchor, ItemData data, DeviceKind kind,
            Action<InventoryLayoutItemGroup> onInteract, out string status)
        {
            if (!Inventory.TryCreateLayout(data, anchor, out _layout, out status))
                return false;
            var root = _layout.Root;
            var layer = root.layer;
            root.transform.localPosition = anchor.transform.localPosition + (kind switch
            {
                DeviceKind.Watch => new Vector3(-.085f, .027f, -.08f),
                DeviceKind.Tablet => new Vector3(-1.164f, .026f, -.451f),
                DeviceKind.Controller => new Vector3(-1.164f, .027f, -.680f),
                DeviceKind.Drone => new Vector3(-.955f, .027f, -.451f),
                _ => new Vector3(-.955f, .027f, -.565f)
            });
            var itemObject = _layout.Item.gameObject;
            itemObject.transform.localRotation = Quaternion.Euler(90, 0, 0);
            Transform visuals;
            if (kind == DeviceKind.Watch)
            {
                _watch = new ClientWristWatchModel(itemObject.transform, Quaternion.identity, Vector3.zero, layer, .027f);
                visuals = _watch.Root.transform;
                Glass(_watch.Screen, .044f, .046f, layer);
            }
            else if (kind == DeviceKind.Tablet)
            {
                _tablet = new ClientTabletModel(itemObject.transform, .12f, .085f, layer, null);
                visuals = _tablet.Root.transform;
                Glass(visuals, .12f * ClientTabletModel.DisplayScale, .085f * ClientTabletModel.DisplayScale, layer);
            }
            else if (kind == DeviceKind.Controller)
            {
                _controller = new ClientDroneControllerModel(itemObject.transform, .12f, .085f, layer, null);
                visuals = _controller.Root.transform;
                Glass(visuals, .12f * ClientDroneControllerModel.DisplayScale, .085f * ClientDroneControllerModel.DisplayScale, layer);
            }
            else
            {
                _drone = new DroneInventoryModel(itemObject.transform, layer, kind == DeviceKind.DroneBattery);
                visuals = _drone.Root.transform;
            }
            // Initialize installs MouseEventsProxy on renderer objects. Put the hit
            // collider on the authored root renderer so that exact native route handles it.
            var collider = visuals.gameObject.AddComponent<BoxCollider>();
            collider.size = kind switch
            {
                DeviceKind.Watch => new Vector3(.066f, .079f, .062f),
                DeviceKind.Tablet => new Vector3(.158f, .118f, .037f),
                DeviceKind.Controller => ClientDroneControllerModel.VisualBounds(.12f, .085f).size,
                DeviceKind.Drone => new Vector3(.148f, .137f, .072f),
                _ => new Vector3(.045f, .070f, .029f)
            };
            collider.center = kind switch
            {
                DeviceKind.Tablet => new Vector3(.002f, 0, .009f),
                DeviceKind.Controller => ClientDroneControllerModel.VisualBounds(.12f, .085f).center,
                DeviceKind.Drone => new Vector3(0, 0, -.018f),
                _ => Vector3.zero
            };
            // Native new-item sheen replaces these authored materials with a
            // white surface until hover. Keep their finish and normal grey-out.
            foreach (var renderer in itemObject.GetComponentsInChildren<Renderer>(true))
                renderer.gameObject.AddComponent<ExcludeRenderableFrom>()._excludedFrom = ExcludeRenderableFrom.Type.Sheen;
            if (!Inventory.TryBindPrimaryAction(Group, onInteract, out _primaryAction, out status))
                throw new InvalidOperationException(status);
            return true;
        }

        internal void Refresh()
        {
            if (!TryRefresh(out var status)) throw new InvalidOperationException(status);
        }

        internal bool TryRefresh(out string status)
        {
            status = "The inventory layout was released.";
            return _layout != null && _layout.TryRefresh(out status);
        }

        internal void Hide()
        {
            if (_layout != null && !_layout.TryHide(out var status)) throw new InvalidOperationException(status);
        }

        private void Glass(Transform parent, float width, float height, int layer)
        {
            var shader = Shader.Find("HDRP/Lit") ?? Shader.Find("Standard");
            if (!shader) throw new InvalidOperationException("The inventory display shader is unavailable.");
            var material = new Material(shader) { name = "Field device sleeping display" };
            _glass.Add(material);
            // Sleeping displays use scene-lit dark cover glass, like their casing.
            var color = new Color(.008f, .014f, .016f, 1);
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);
            if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 0);
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", .88f);
            if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", .88f);
            if (material.HasProperty("_SupportDecals"))
            {
                material.SetFloat("_SupportDecals", 0);
                material.EnableKeyword("_DISABLE_DECALS");
            }
            var plane = GameObject.CreatePrimitive(PrimitiveType.Quad);
            plane.name = "Field device inventory glass";
            plane.layer = layer;
            plane.transform.SetParent(parent, false);
            plane.transform.localPosition = new Vector3(0, 0, -.0008f);
            plane.transform.localScale = new Vector3(width, height, 1);
            var collider = plane.GetComponent<Collider>();
            if (collider) { collider.enabled = false; UnityEngine.Object.Destroy(collider); }
            var renderer = plane.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = true;
        }

        public void Dispose()
        {
            Hide();
            _primaryAction?.Dispose();
            _primaryAction = null;
            _tablet?.Dispose();
            _controller?.Dispose();
            _watch?.Dispose();
            _drone?.Dispose();
            _tablet = null;
            _controller = null;
            _watch = null;
            _drone = null;
            _layout?.Dispose();
            _layout = null;
            foreach (var material in _glass) if (material) UnityEngine.Object.Destroy(material);
            _glass.Clear();
        }
    }
}
