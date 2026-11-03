// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Modstoke.Sons;

namespace Modstoke.Client;

internal sealed class ClientTabletGpsPose : IDisposable
{
    private const int GpsLayer = 5;
    private readonly PlayerAnimationLayerSuppression _suppression;

    private ClientTabletGpsPose(PlayerAnimationLayerSuppression suppression)
    {
        _suppression = suppression;
    }

    internal static bool TryAcquire(out ClientTabletGpsPose? pose)
    {
        var acquired = PlayerAnimationLayers.TrySuppressLayer(GpsLayer, out var suppression, out _);
        pose = suppression == null ? null : new ClientTabletGpsPose(suppression);
        return acquired;
    }

    internal bool Owned => _suppression.IsCurrent;

    public void Dispose() => _suppression.Dispose();
}
