// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Sons.Items.Core;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientDeviceInventory
{
    internal const int ControllerItemId = 742105;
    private readonly Func<bool>? _selectController;
    private ItemData? _controllerData;
    private DeviceLayout? _controllerLayout;
    private bool _controllerOwned = true;
    internal bool HasController { get; private set; }

    private static Texture2D CreateControllerIcon()
    {
        var pixels = new Color[64 * 64];
        var shell = new Color(.28f, .32f, .23f, 1);
        var dark = new Color(.10f, .13f, .12f, 1);
        var screen = new Color(.22f, .38f, .29f, 1);
        for (var y = 0; y < 64; y++)
        for (var x = 0; x < 64; x++)
        {
            var color = Color.clear;
            if (x >= 9 && x < 55 && y >= 7 && y < 46) color = shell;
            if (x >= 14 && x < 50 && y >= 12 && y < 29) color = screen;
            if (((x - 20) * (x - 20) + (y - 37) * (y - 37) <= 20)
                || ((x - 44) * (x - 44) + (y - 37) * (y - 37) <= 20)) color = dark;
            if (((x >= 13 && x < 17) || (x >= 47 && x < 51)) && y >= 46 && y < 61) color = dark;
            if (x >= 29 && x < 35 && y >= 35 && y < 39) color = dark;
            pixels[y * 64 + x] = color;
        }
        return CreateIconTexture("Drone controller inventory icon", pixels);
    }
}
