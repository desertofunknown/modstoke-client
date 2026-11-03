// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Client;

internal enum DroneCameraVision { Color, Night, Thermal }

internal sealed record ClientDroneReadiness(bool Grounded, bool Upright, bool SolarDeployed);

internal sealed record ClientDroneContact(string DroneId, ClientFieldPoint Position, long ObservedAt)
{
    internal double AgeSeconds => Math.Max(0, (Environment.TickCount64 - ObservedAt) / 1000d);
}

internal sealed record ClientDronePlacementView(bool Active, bool HasSurface, bool Clear, string Status);

internal enum ClientDroneTerrainSurveyStatus : byte { Unavailable, SurfaceUnavailable, Sampled }

internal sealed record ClientDroneTerrainSurvey(string DroneId, ClientFieldPoint Position,
    ClientDroneTerrainSurveyStatus Status, float? SlopeDegrees, ClientFieldPoint? Normal,
    long RecordedAt, bool FromRemoteHost);

internal readonly record struct DronePilotInput(float Forward, float Right, float Climb, float Yaw)
{
    internal static DronePilotInput Neutral => default;
}

internal sealed record ClientDroneView(
    bool Ready, string Status, bool InventoryDrone, int SparePacks,
    bool Deployed, bool Armed, bool Piloting, float Charge, float Health,
    float Speed, float Altitude, float Range, string Mode, bool Charging,
    string DroneId = "", string InstalledPackId = "", string PackAId = "",
    float PackACharge = 0, bool PackAInstalled = false, string PackBId = "",
    float PackBCharge = 0, bool PackBInstalled = false, bool Link = false,
    ClientDroneReadiness? Readiness = null, ClientDroneContact? LastContact = null,
    bool? SurfaceClearanceAvailable = null, float? LoadedTerrainHeight = null, string SolarStatus = "",
    long LoadedTerrainObservedAt = 0, ClientDroneTerrainSurvey? TerrainSurvey = null)
{
    internal static ClientDroneView Unavailable(string status) => new(false, status, false, 0,
        false, false, false, 0, 0, 0, 0, 0, "Offline", false);
}
