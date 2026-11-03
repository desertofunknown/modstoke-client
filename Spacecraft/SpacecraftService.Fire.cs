// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftService
{
    private SpacecraftFireEffects? _fireEffects;

    private void UpdateShipFireEffects()
    {
        if (_body?.Ready != true) return;
        _fireEffects ??= new SpacecraftFireEffects(_body);
        var camera = LocalPlayer.MainCam;
        bool visible = _ready && _body.TelemetryCurrent && camera != null && camera && camera.isActiveAndEnabled;
        _fireEffects.Update(visible ? _body.State.Systems.Fire : default,
            visible ? camera!.transform.position : _body.Root.transform.position, Time.unscaledDeltaTime);
    }
}
