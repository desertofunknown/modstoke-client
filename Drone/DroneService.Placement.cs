// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Modstoke.Sons;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientDroneService
{
    private ClientDronePlacementPreview? _placementPreview;
    private long _nextPlacement;
    private long _nextPlacementObservation;
    private bool _placementVisualFailed;

    internal ClientDronePlacementView? Placement { get; private set; }
    internal Texture? PlacementFeed => _placementPreview?.Feed;

    private void UpdatePlacement(long now)
    {
        if (!_viewVisible || !Snapshot.Ready || !Snapshot.InventoryDrone || Snapshot.Deployed || !LocalAlive())
        { HidePlacement(); return; }
        if (now < _nextPlacement) return;
        _nextPlacement = now + 100;
        var player = LocalPlayer.Transform;
        var clear = TryDeploymentPose(player.position, player.rotation, out var position, out var rotation,
            out var reason, out var surface);
        Placement = new(true, surface, clear,
            clear ? "Clear footprint. The host checks again when you deploy." : reason);
        if (!surface) { _placementPreview?.Hide(); return; }
        if (_placementVisualFailed) return;
        try
        {
            _placementPreview ??= new ClientDronePlacementPreview();
            _placementPreview.Show(position, rotation, clear);
        }
        catch (Exception error)
        {
            _placementPreview?.Dispose();
            _placementPreview = null;
            _placementVisualFailed = true;
            Console.WriteLine("Modstoke deployment preview is unavailable (" + error.GetType().Name + ").");
        }
    }

    private void HidePlacement()
    {
        _placementPreview?.Hide();
        Placement = null;
        _nextPlacement = 0;
    }

    private void ResetPlacement()
    {
        _placementPreview?.Dispose();
        _placementPreview = null;
        _placementVisualFailed = false;
        Placement = null;
        _nextPlacement = 0;
        _nextPlacementObservation = 0;
    }

    private bool TryDeploymentPose(Vector3 playerPosition, Quaternion playerRotation,
        out Vector3 position, out Quaternion rotation, out string reason)
        => TryDeploymentPose(playerPosition, playerRotation, out position, out rotation, out reason, out _,
            observeObstruction: true);

    private bool TryDeploymentPose(Vector3 playerPosition, Quaternion playerRotation,
        out Vector3 position, out Quaternion rotation, out string reason, out bool surface,
        bool observeObstruction = false)
    {
        surface = false;
        position = default;
        rotation = Quaternion.identity;
        reason = "No ground found in front of you. Face a nearby patch of ground.";
        var forward = playerRotation * Vector3.forward;
        forward.y = 0;
        if (forward.sqrMagnitude < .1f) forward = Vector3.forward;
        forward.Normalize();
        // Extra headroom reaches rising terrain; the lower bound and interaction range stay local.
        var origin = playerPosition + forward * 1.6f + Vector3.up * 2f;
        if (!Physics.Raycast(origin, Vector3.down, out var ground, 5f, ~0, QueryTriggerInteraction.Ignore))
            return false;
        surface = true;
        position = ground.point + ground.normal * (-ClientDroneModel.GroundY + .04f);
        var alongGround = Vector3.ProjectOnPlane(forward, ground.normal);
        rotation = alongGround.sqrMagnitude > .01f
            ? Quaternion.LookRotation(alongGround, ground.normal) : Quaternion.LookRotation(forward);
        if (Vector3.Distance(playerPosition, ground.point) > InteractionRange)
        {
            reason = "That ground is out of reach. Move closer before deploying.";
            return false;
        }
        if (ground.normal.y < ClientDroneFirmware.MinimumArmUp)
        {
            reason = "That slope is too steep to arm the drone. Find a flatter patch.";
            return false;
        }

        var highest = 0f;
        var lowest = 0f;
        // Probe each motor foot, matching the actual four-point landing support.
        for (var corner = 0; corner < 4; corner++)
        {
            var motor = ClientDroneModel.MotorPosition(corner);
            var contact = ground.point + rotation * new Vector3(motor.x, 0, motor.z);
            if (!Physics.Raycast(contact + ground.normal * .35f, -ground.normal, out var support,
                .7f, ~0, QueryTriggerInteraction.Ignore))
            {
                reason = "The landing feet need support. Move away from the edge.";
                return false;
            }
            var offset = Vector3.Dot(support.point - contact, ground.normal);
            highest = Mathf.Max(highest, offset);
            lowest = Mathf.Min(lowest, offset);
            if (support.normal.y < ClientDroneFirmware.MinimumArmUp || highest - lowest > .06f)
            {
                reason = "Ground is too uneven under the landing feet. Try a smoother patch.";
                return false;
            }
        }

        // Leave 4 cm below the lowest hardware for a short physical settle.
        position = ground.point + ground.normal * (-ClientDroneModel.GroundY + .04f + highest);
        if (Vector3.Distance(playerPosition, position) > InteractionRange)
        {
            reason = "That ground is out of reach. Move closer before deploying.";
            return false;
        }
        // Check the whole airframe in the same frame as the ground and the deployed body.
        // Keep the supporting terrain in this query: bumps and other colliders must still block it.
        var center = position + ground.normal * -.02f;
        var halfExtents = new Vector3(.55f, .205f, .55f);
        if (Physics.CheckBox(center, halfExtents, rotation, ~0, QueryTriggerInteraction.Ignore))
        {
            reason = "The drone needs more clearance. Move away from rocks, trees or structures.";
            if (observeObstruction) ObservePlacementObstruction(center, halfExtents, rotation);
            return false;
        }
        reason = "";
        return true;
    }

    private void ObservePlacementObstruction(Vector3 center, Vector3 halfExtents, Quaternion rotation)
    {
        var now = Now;
        if (now < _nextPlacementObservation) return;
        _nextPlacementObservation = now + 1000;
        try
        {
            if (!BoltNetwork.isRunning || !BoltNetwork.isServer) return;
            // This later observation may see a different scene. It never overrides CheckBox's rejection.
            ServerWorld.TryOverlapBox(new(center.x, center.y, center.z),
                new(halfExtents.x, halfExtents.y, halfExtents.z), new(rotation.x, rotation.y, rotation.z, rotation.w),
                ~0, CollisionTriggers.Ignore, out var overlap, out var status);
            var message = FormattableString.Invariant($"Modstoke drone deployment blocked at ({center.x:F2}, {center.y:F2}, {center.z:F2}); overlap observation: {status}.");
            if ((status is CollisionReadStatus.Success or CollisionReadStatus.BufferFull)
                && overlap is { Colliders.Count: > 0 })
            {
                var first = overlap.Colliders[0];
                message += FormattableString.Invariant($" Reported colliders={overlap.Colliders.Count}, possibly truncated={overlap.MayBeTruncated}.");
                message += FormattableString.Invariant($" First reported collider={first.ColliderInstanceId}, layer={first.ColliderLayer}, bounds center={first.BoundsCenter:F2}, extents={first.BoundsExtents:F2}.");
            }
            Console.WriteLine(message);
        }
        catch (Exception error)
        {
            Console.WriteLine("Modstoke drone deployment obstruction observation failed: " + error);
        }
    }
}
