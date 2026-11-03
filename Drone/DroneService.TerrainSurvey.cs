// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Modstoke.Sons;
using Vector3 = System.Numerics.Vector3;

namespace Modstoke.Client;

internal sealed partial class ClientDroneService
{
    private const long TerrainSurveyInterval = 1000;
    private const long TerrainSurveyLifetime = 1500;
    private Guid _terrainSurveySessionEpoch;
    private long _lastTerrainSurveySequence, _terrainSurveyOwnerSequenceFloor;
    private ClientDroneTerrainSurvey? _remoteTerrainSurvey;

    private ClientDroneTerrainSurvey? ObserveHostTerrainSurvey(Kit kit, bool linked, long now)
    {
        if (kit.TerrainSurveyEpoch != _epoch)
        {
            ResetTerrainSurvey(kit);
            kit.TerrainSurveyEpoch = _epoch;
        }
        if (!BoltNetwork.isRunning || !BoltNetwork.isServer || !linked || kit.Body == null
            || kit.Body.Root == null || kit.Body.Root.WasCollected || !kit.Body.Root
            || kit.Body.BatteryCharge <= .0001f || kit.Body.Health <= 0)
        {
            kit.TerrainSurvey = null;
            return null;
        }
        if (kit.TerrainSurvey is { } previous && now - previous.RecordedAt > TerrainSurveyLifetime)
            kit.TerrainSurvey = null;
        if (now < kit.NextTerrainSurvey) return kit.TerrainSurvey;
        // At most 16 deployed drones, one four-unit read each per second. The SDK's
        // 256-unit frame budget is shared; a failed reservation waits for the next slot.
        kit.NextTerrainSurvey = now + TerrainSurveyInterval;
        var position = kit.Body.Position;
        var point = new ClientFieldPoint(position.x, position.y, position.z);
        if (ServerWorld.TryReadTerrainSurface(new Vector3(position.x, position.y, position.z),
            out var surface, out var status))
        {
            // The heightmap uses X/Z only. This sample does not select a cave floor or a mesh surface.
            kit.TerrainSurvey = new(kit.Id, point, ClientDroneTerrainSurveyStatus.Sampled,
                surface.SlopeDegrees, new(surface.Normal.X, surface.Normal.Y, surface.Normal.Z), now, false);
        }
        else
        {
            kit.TerrainSurvey = new(kit.Id, point,
                status == CollisionReadStatus.SurfaceUnavailable
                    ? ClientDroneTerrainSurveyStatus.SurfaceUnavailable : ClientDroneTerrainSurveyStatus.Unavailable,
                null, null, now, false);
        }
        return kit.TerrainSurvey;
    }

    private static void ResetTerrainSurvey(Kit kit)
    {
        kit.TerrainSurvey = null;
        kit.NextTerrainSurvey = 0;
        kit.TerrainSurveyEpoch = Guid.Empty;
    }

    private void ResetRemoteTerrainSurvey()
    {
        _remoteTerrainSurvey = null;
        _lastTerrainSurveySequence = _terrainSurveyOwnerSequenceFloor = 0;
        _terrainSurveySessionEpoch = Guid.Empty;
    }

    private void RefreshTerrainSurveySession()
    {
        if (_terrainSurveySessionEpoch == _epoch) return;
        ResetRemoteTerrainSurvey();
        _terrainSurveySessionEpoch = _epoch;
    }

    private ClientDroneTerrainSurvey? CurrentRemoteTerrainSurvey(bool linked, long now)
    {
        RefreshTerrainSurveySession();
        if (!linked || !HasRadioContact(_remoteView) || _remoteTerrainSurvey is not { } survey
            || survey.DroneId != _remoteView.DroneId || now - survey.RecordedAt > TerrainSurveyLifetime)
            _remoteTerrainSurvey = null;
        return _remoteTerrainSurvey;
    }

    private void ReceiveTerrainSurvey(Message message)
    {
        RefreshTerrainSurveySession();
        var now = Now;
        if (message.Sequence <= _lastTerrainSurveySequence || message.Sequence < _terrainSurveyOwnerSequenceFloor
            || now - _lastOwnerSnapshot > TerrainSurveyLifetime
            || !HasRadioContact(_remoteView) || message.Id != _remoteView.DroneId) return;
        _lastTerrainSurveySequence = message.Sequence;
        // Host and client monotonic clocks are unrelated; this timestamp describes receipt only.
        _remoteTerrainSurvey = message.TerrainSurvey! with { RecordedAt = now };
    }
}
