// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientDroneService
{
    internal DroneCameraVision CameraVision => ViewedBody?.CameraVision ?? DroneCameraVision.Color;
    internal string CameraVisionStatus => ViewedBody?.CameraVisionStatus ?? "Camera sensors unavailable without a live drone link.";

    // Acceptance records a selection; the render callback reports the actual mode and status.
    internal bool SetCameraVision(DroneCameraVision mode)
    {
        if (!OnThread || !_started || !Enum.IsDefined(mode)) return false;
        var body = ViewedBody;
        if (body == null) return false;
        body.SetCameraVision(mode);
        return true;
    }

    private ClientDroneBody? ViewedBody
    {
        get
        {
            if (!OnThread || !_started || !_viewVisible || !Snapshot.Ready || !HasRadioContact(Snapshot)) return null;
            ClientDroneBody? body;
            if (IsClient)
            {
                if (!_replicas.TryGetValue(Snapshot.DroneId, out var replica) || Now - replica.LastSeen > 1500) return null;
                body = replica.Body;
            }
            else
            {
                if (!_kits.TryGetValue(_localOwner, out var kit) || kit.Id != Snapshot.DroneId) return null;
                body = kit.Body;
            }
            return body != null && body.Root && body.Root.activeInHierarchy ? body : null;
        }
    }

    internal void SetCameraView(bool immersive, float pitch, float zoom)
    {
        if (!OnThread || !_started) return;
        var body = ViewedBody;
        if (body == null)
        {
            DisableCameras();
            return;
        }
        body.SetCameraView(immersive, pitch, zoom);
    }

    internal (float Heading, float Pitch, float Zoom) CameraInstruments => ViewedBody is { } body
        ? (body.Heading, body.CameraPitch, body.CameraZoom) : (float.NaN, float.NaN, float.NaN);

    internal ClientDroneSurveyObservation ObserveSurvey()
    {
        var body = ViewedBody;
        if (body == null)
            return ClientDroneSurveyObservation.Unavailable("A live onboard camera is required.");

        var position = body.Position;
        if (!Finite(position)) return ClientDroneSurveyObservation.Unavailable("Aircraft position unavailable.");
        ClientFieldPoint? pilot = null;
        if (LocalAlive() && Finite(LocalPlayer.Transform.position))
        {
            var player = LocalPlayer.Transform.position;
            pilot = new(player.x, player.y, player.z);
        }
        var observation = new ClientDroneSurveyObservation(Snapshot.DroneId, true,
            new(position.x, position.y, position.z), pilot, body.Heading,
            float.IsFinite(Snapshot.Range) ? Math.Max(0, RadioRange - Snapshot.Range) : float.NaN,
            null, 0, "Camera unavailable.");
        var lens = body.Root.transform.Find("Drone onboard camera");
        var camera = lens != null && lens ? lens.GetComponent<Camera>() : null;
        if (camera == null || !camera || !camera.isActiveAndEnabled) return observation;

        // Observe a local surface along the onboard camera's center ray.
        // The result never becomes a landing or flight command.
        var ray = camera.ViewportPointToRay(new Vector3(.5f, .5f, 0));
        if (!Physics.Raycast(ray, out var hit, 250, camera.cullingMask, QueryTriggerInteraction.Ignore))
            return observation with { SurfaceStatus = "No surface return within 250 m." };
        var collider = hit.collider;
        if (collider == null || !collider || !Finite(hit.point)) return observation;
        var target = collider.transform;
        if (target.IsChildOf(body.Root.transform)
            || LocalPlayer.Transform != null && LocalPlayer.Transform && target.IsChildOf(LocalPlayer.Transform)
            || collider.attachedRigidbody != null || collider.GetComponentInParent<BoltEntity>() != null)
            return observation with { SurfaceStatus = "Aircraft, actors and moving objects cannot be marked." };

        // Terrain is optional in this mod's references. Resolve its native component by name;
        // static mesh surfaces use the already referenced physics module.
        var terrain = collider.GetComponent("TerrainCollider");
        var mesh = collider.GetComponent<MeshCollider>();
        var terrainReturn = terrain != null && terrain && terrain.Pointer == collider.Pointer;
        var staticReturn = mesh != null && mesh && mesh.Pointer == collider.Pointer && collider.gameObject.isStatic;
        if (!terrainReturn && !staticReturn)
            return observation with { SurfaceStatus = "This surface does not support survey marks." };
        return observation with { Surface = new(hit.point.x, hit.point.y, hit.point.z), SurfaceDistance = Vector3.Distance(camera.transform.position, hit.point),
            SurfaceStatus = terrainReturn ? "Terrain observed" : "Static surface observed" };
    }
}

internal sealed record ClientDroneSurveyObservation(string DroneId, bool Live, ClientFieldPoint? Aircraft,
    ClientFieldPoint? Pilot, float Heading, float RadioMargin, ClientFieldPoint? Surface,
    float SurfaceDistance, string SurfaceStatus)
{
    internal static ClientDroneSurveyObservation Unavailable(string reason) =>
        new("", false, null, null, float.NaN, float.NaN, null, 0, reason);
}
