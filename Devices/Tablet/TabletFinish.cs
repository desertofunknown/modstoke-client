// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using UnityEngine;

namespace Modstoke.Client;

internal sealed class ClientTabletFinish : IDisposable
{
    private readonly ClientDeviceSurfaceFinish _surface = new();

    internal ClientTabletFinish(Material shell, Material metal, Material rubber, Material controls)
    {
        try
        {
            _surface.ApplyShell(shell);
            _surface.Apply(metal, ClientDeviceSurfaceFinish.Surface.Metal);
            _surface.Apply(rubber, ClientDeviceSurfaceFinish.Surface.Rubber);
            _surface.Apply(controls, ClientDeviceSurfaceFinish.Surface.Rubber);
        }
        catch (Exception error)
        {
            // Keep any successfully assigned maps alive until the model is disposed.
            Debug.LogWarning("Modstoke Client: some casing surface detail is unavailable: " + error.Message);
        }
    }

    public void Dispose()
    {
        _surface.Dispose();
    }
}
