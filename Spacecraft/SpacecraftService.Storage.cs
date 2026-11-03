// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftService
{
    private void UpdateStorage()
    {
        if (_body == null || _storage == null) return;
        if (!IsClient && !_storage.Aboard && _body.State.Region == SpacecraftRegion.Island
            && Now < _cargoAttachUntil && Now >= _nextCargoAttach)
        {
            _nextCargoAttach = Now + 1000;
            var anchor = _islandPosition + _islandRotation * (SpacecraftLayout.Entry + Vector3.back * 4);
            if (_storage.TryAttach(_body, anchor, _islandRotation, out var status)) _cargoAttachUntil = 0;
            _feedback = status;
        }
        _storage.Update(_body);
        if (_storage.Root != null) _body.IgnoreContactRoot(_storage.Root);
    }

    private void ObserveStorage(ulong id, bool aboard, Vector3 parkPosition, Quaternion parkRotation)
    {
        if (!IsClient) return;
        if (_body == null) { _storage?.Park(); return; }
        _storage ??= new ClientSpacecraftStorage();
        _storage.Observe(_body, id, aboard, parkPosition, parkRotation);
    }

    private void OnShipImpact(float energyJoules, Vector3 localPoint)
    {
        if (!_started || !_ready || IsClient || _body?.Ready != true) return;
        _systems.Impact(energyJoules, localPoint.x, localPoint.y, localPoint.z);
        _body.SetSystemsState(SystemsState(_systems.State));
    }
}
