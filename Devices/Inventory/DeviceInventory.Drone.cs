// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Sons.Items.Core;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientDeviceInventory
{
    internal const int DroneItemId = 742103;
    internal const int DroneBatteryItemId = 742104;
    private readonly Func<bool>? _selectDrone;
    private ItemData? _droneData;
    private ItemData? _droneBatteryData;
    private DeviceLayout? _droneLayout;
    private DeviceLayout? _droneBatteryLayout;
    private bool _droneKitReady;
    private bool _droneInInventory;
    private int _droneSparePacks;
    private bool _applyDroneKit;

    // Presentation only: authority grants, consumes and saves the kit. No starter
    // ownership is inferred from local inventory or the field-device save file.
    internal void SetDroneKit(bool ready, bool droneInInventory, int sparePacks)
    {
        var drone = ready && droneInInventory;
        var packs = ready ? Math.Clamp(sparePacks, 0, 2) : 0;
        if (_droneKitReady == ready && _droneInInventory == drone && _droneSparePacks == packs) return;
        _droneKitReady = ready;
        _droneInInventory = drone;
        _droneSparePacks = packs;
        _applyDroneKit = true;
    }

    private void ApplyDroneKit()
    {
        var droneCount = _droneInInventory ? 1 : 0;
        var currentDrone = _manager!.GetItemCount(DroneItemId);
        var currentPacks = _manager.GetItemCount(DroneBatteryItemId);
        if (!_applyDroneKit && currentDrone == droneCount && currentPacks == _droneSparePacks) return;
        // Disable first when removing the last instance. Native initialization must
        // not bind an empty group before an owned instance can supply its geometry.
        if (droneCount == 0) _droneLayout!.Hide();
        if (_droneSparePacks == 0) _droneBatteryLayout!.Hide();
        ApplyDroneCount(DroneItemId, currentDrone, droneCount);
        ApplyDroneCount(DroneBatteryItemId, currentPacks, _droneSparePacks);
        if (droneCount > 0) _droneLayout!.Refresh();
        if (_droneSparePacks > 0) _droneBatteryLayout!.Refresh();
        _applyDroneKit = false;
    }

    private void ApplyDroneCount(int id, int current, int desired)
    {
        if (current > desired) _manager!.RemoveItems(id, current - desired, true);
        else if (current < desired && !_manager!.TryAddItems(id, desired - current, true))
            throw new InvalidOperationException("The native inventory rejected the drone kit projection.");
    }

    private static Texture2D CreateDroneIcon(bool battery)
    {
        var pixels = new Color[64 * 64];
        var shell = new Color(.52f, .58f, .36f, 1);
        var dark = new Color(.12f, .15f, .12f, 1);
        var amber = new Color(.83f, .43f, .12f, 1);
        var rotorXs = new[] { 15, 49 };
        var rotorYs = new[] { 16, 48 };
        for (var y = 0; y < 64; y++)
        for (var x = 0; x < 64; x++)
        {
            var color = Color.clear;
            if (battery)
            {
                if (x >= 17 && x < 47 && y >= 8 && y < 54) color = dark;
                if (x >= 19 && x < 45 && y >= 10 && y < 52) color = shell;
                if (x >= 25 && x < 39 && y >= 8 && y < 13) color = amber;
                if (x >= 24 && x < 40 && y >= 53 && y < 59) color = dark;
                if (x >= 26 && x < 38 && x % 3 == 2 && y >= 55 && y < 58) color = shell;
                if (x >= 23 && x < 35 && y >= 24 && y < 42) color = dark;
                if (x >= 25 && x < 33 && (y == 28 || y == 32 || y == 36)) color = shell;
                if (x >= 38 && x < 42 && y >= 26 && y < 41 && y % 4 < 2) color = dark;
            }
            else
            {
                if (Math.Abs(Math.Abs(x - 32) - Math.Abs(y - 32)) <= 2
                    && Math.Abs(x - 32) < 20 && Math.Abs(y - 32) < 20) color = shell;
                foreach (var cx in rotorXs)
                foreach (var cy in rotorYs)
                {
                    var radius = (x - cx) * (x - cx) + (y - cy) * (y - cy);
                    if (radius <= 81 && (radius >= 36 || Math.Abs(y - cy) <= 1)) color = dark;
                }
                if (x >= 25 && x < 40 && y >= 22 && y < 44) color = shell;
                if (x >= 27 && x < 38 && y >= 35 && y < 40) color = amber;
                if (x >= 29 && x < 36 && y >= 18 && y < 24) color = dark;
            }
            pixels[y * 64 + x] = color;
        }
        return CreateIconTexture(battery ? "Rechargeable drone pack inventory icon" : "Field drone inventory icon", pixels);
    }

    private sealed class DroneInventoryModel : IDisposable
    {
        private ClientDroneModel? _model;
        internal GameObject Root { get; private set; } = null!;

        internal DroneInventoryModel(Transform parent, int layer, bool battery)
        {
            try
            {
                _model = ClientDroneModel.CreateInventory(parent, battery);
                Root = _model.Root;
                Root.layer = layer;
            }
            catch { Dispose(); throw; }
        }

        public void Dispose()
        {
            _model?.Dispose();
            _model = null;
        }
    }
}
