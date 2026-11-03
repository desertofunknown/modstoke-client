// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Text.Json.Serialization;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftService
{
    private bool _restoreNavigation;
    private long _nextRestoreAttempt;

    private static Vector3 SceneOrigin(SpacecraftRegion region) => region switch
    {
        SpacecraftRegion.Space => SpacecraftLayout.SpaceOrigin,
        SpacecraftRegion.Moon => SpacecraftLayout.MoonOrigin,
        SpacecraftRegion.TestWorld => SpacecraftLayout.WorldOrigin,
        _ => Vector3.zero
    };

    private OrbitalVector FramePosition(SpacecraftFlightState flight)
    {
        var local = flight.Position - SceneOrigin(flight.Region);
        return new(local.x, local.y, local.z);
    }

    private SavedNavigation CaptureNavigation(SpacecraftFlightState flight)
    {
        var position = FramePosition(flight);
        var saved = new SavedNavigation
        {
            Region = flight.Region, Position = new[] { position.X, position.Y, position.Z },
            Rotation = new[] { flight.Rotation.x, flight.Rotation.y, flight.Rotation.z, flight.Rotation.w },
            Velocity = new[] { flight.Velocity.x, flight.Velocity.y, flight.Velocity.z },
            AngularVelocity = new[] { flight.AngularVelocity.x, flight.AngularVelocity.y, flight.AngularVelocity.z },
            Powered = flight.Powered, Dampeners = flight.Dampeners,
            IslandPosition = new[] { _islandPosition.x, _islandPosition.y, _islandPosition.z },
            IslandRotation = new[] { _islandRotation.x, _islandRotation.y, _islandRotation.z, _islandRotation.w },
            Coast = flight.Region == SpacecraftRegion.Space ? CaptureCoast() : null
        };
        if (!ValidNavigation(saved)) throw new InvalidOperationException("The spacecraft's navigation state cannot be saved.");
        return saved;
    }

    private static bool ValidNavigation(SavedNavigation value)
    {
        if (!Enum.IsDefined(value.Region) || value.Position?.Length != 3 || value.Rotation?.Length != 4
            || value.Velocity?.Length != 3 || value.AngularVelocity?.Length != 3
            || value.IslandPosition?.Length != 3 || value.IslandRotation?.Length != 4) return false;
        var position = SavedPosition(value);
        var velocity = SavedVector(value.Velocity);
        var angular = SavedVector(value.AngularVelocity);
        var island = SavedVector(value.IslandPosition);
        double limit = value.Region == SpacecraftRegion.Space ? OrbitalSimulation.MaximumPosition : 1e7;
        var residual = new OrbitalVector(velocity.x, velocity.y, velocity.z);
        double speedLimit = value.Region == SpacecraftRegion.Space ? 2 * OrbitalSimulation.MaximumSpeed : 1000;
        if (!(position.IsFinite && position.LengthSquared <= limit * limit
            && residual.IsFinite && residual.LengthSquared <= speedLimit * speedLimit
            && Finite(angular) && angular.sqrMagnitude <= 400
            && ValidRotation(SavedRotation(value.Rotation))
            && Finite(island) && island.y < 2000 && island.sqrMagnitude <= 1e14f
            && ValidRotation(SavedRotation(value.IslandRotation)))) return false;
        if (value.Coast == null) return true;
        if (value.Region != SpacecraftRegion.Space || !TryRestoreCoast(value.Coast, out var frame)) return false;
        return OrbitalPoseValid(frame.ToInertialPosition(position), frame.ToInertialVelocity(position, residual));
    }

    private void RestoreSavedVessel()
    {
        if (!_restoreNavigation || _body != null || Now < _nextRestoreAttempt) return;
        _nextRestoreAttempt = Now + 500;
        var saved = _savedShip!;
        // Keep a saved latch out of the physics world until its berth exists.
        if (saved.Docked == true && _orbitalStation == null)
        {
            _status = "Preparing the saved spacecraft's station berth.";
            return;
        }
        var navigation = saved.Navigation!;
        if (!ClientSpacecraftBody.PrepareAssets(out var status))
        {
            _status = "Waiting to restore the saved spacecraft: " + status;
            return;
        }
        if (!_destination.Ensure())
        { _status = "Waiting for saved spacecraft terrain. Its saved position is retained."; return; }
        if (!_systems.TryRestore(saved.Systems!))
        { InvalidSave(); return; }
        var position = SavedPosition(navigation);
        var scenePosition = navigation.Region == SpacecraftRegion.Space ? SpacecraftLayout.SpaceOrigin
            : SceneOrigin(navigation.Region) + new Vector3((float)position.X, (float)position.Y, (float)position.Z);
        var rotation = SavedRotation(navigation.Rotation!);
        var velocity = SavedVector(navigation.Velocity!);
        var angular = SavedVector(navigation.AngularVelocity!);
        OrbitalFrame? coast = null;
        var cruise = default(OrbitalVector);
        if (navigation.Region == SpacecraftRegion.Space)
        {
            OrbitalFrame frame;
            if (navigation.Coast == null)
            {
                // Schema 3 stored a displacement in the rotating Earth-orbit frame.
                // Preserve its physical velocity and spin when migrating to inertial axes.
                frame = SurfaceFrame(SpacecraftRegion.Space);
                cruise = frame.ToInertialVelocity(position, default);
                var spin = frame.ToLocalDirection(frame.AngularVelocity);
                angular += new Vector3((float)spin.X, (float)spin.Y, (float)spin.Z);
            }
            else
            {
                if (!TryRestoreCoast(navigation.Coast, out frame))
                    throw new InvalidOperationException("The saved spacecraft reference frame is invalid.");
                cruise = SavedVector(navigation.Coast.CruiseVelocity!);
            }
            coast = new OrbitalFrame(frame.ToInertialPosition(position),
                frame.ToInertialVelocity(position, default), default, frame.Right, frame.Up, frame.Forward, default);
            if (!OrbitalPoseValid(coast.Value.Origin, coast.Value.ToInertialVelocity(default,
                    new(velocity.x, velocity.y, velocity.z))))
                throw new InvalidOperationException("The saved spacecraft is outside the orbital coordinate domain.");
        }
        var previousFrame = (_spaceFrame, _spaceFrameTime, _spaceCruiseVelocity, _frameRevision);
        var previousIsland = (_islandPosition, _islandRotation);
        var previousShip = _shipId;
        var previousAssist = _stationAssist;
        ClientSpacecraftBody? body = null;
        try
        {
            body = new ClientSpacecraftBody(scenePosition, rotation, true);
            if (!body.Ready) throw new InvalidOperationException("The saved spacecraft could not be constructed.");
            body.SetSystemsState(SystemsState(_systems.State));
            body.RestoreActuators(saved.RearDoorOpen, saved.RearDoorAmount, saved.InnerDoorAmount,
                saved.CabinDoorAmount, saved.GearDeployed, saved.GearAmount);
            body.RestoreNavigation(navigation.Region, scenePosition, rotation, velocity, angular,
                navigation.Powered, navigation.Dampeners);
            _body = body;
            _shipId = saved.Id;
            _islandPosition = SavedVector(navigation.IslandPosition!);
            _islandRotation = SavedRotation(navigation.IslandRotation!);
            _spaceFrame = null;
            _spaceFrameTime = 0;
            if (coast is { } reference) AnchorSpaceFrame(reference.Origin, reference.Velocity, reference);
            _spaceCruiseVelocity = cruise;
            _stationAssist = false;
            _frameRevision = checked(_frameRevision + 1);
            // The pending flag continues to exclude this projection from saves,
            // commands and simulation until the native berth is also complete.
            UpdateOrbitalStation(teleport: true, restoring: true);
            Physics.SyncTransforms();
            RestoreDockedVessel();
            Physics.SyncTransforms();
            // Cargo remains in its native saved shelf on the island. Loading a
            // vessel must not replace that shelf or duplicate its contents.
            _storage = new ClientSpacecraftStorage();
            body.Impact += OnShipImpact;
            body.CollisionImpact += OnStationCollision;
            _nextCargoAttach = _cargoAttachUntil = 0;
            _nextSnapshot = _nextRestoreAttempt = 0;
            _migrateStationDock = false;
            _restoreNavigation = false;
            _status = "The saved spacecraft has been restored.";
        }
        catch
        {
            if (ReferenceEquals(_body, body)) _body = null;
            _shipId = previousShip;
            (_islandPosition, _islandRotation) = previousIsland;
            (_spaceFrame, _spaceFrameTime, _spaceCruiseVelocity, _frameRevision) = previousFrame;
            _stationAssist = previousAssist;
            try { HideOrbitalStation(); }
            catch (Exception error) { Console.WriteLine("Saved spacecraft station cleanup: " + error); }
            if (body != null)
            {
                body.Impact -= OnShipImpact;
                body.CollisionImpact -= OnStationCollision;
                try { if (body.Root) body.Root.SetActive(false); }
                catch (Exception error) { Console.WriteLine("Saved spacecraft collision cleanup: " + error); }
                try { body.Dispose(); }
                catch (Exception error) { Console.WriteLine("Saved spacecraft body cleanup: " + error); }
            }
            throw;
        }
    }

    private static OrbitalVector SavedPosition(SavedNavigation value)
        => new(value.Position![0], value.Position[1], value.Position[2]);

    private static Vector3 SavedVector(float[] value) => new(value[0], value[1], value[2]);
    private static OrbitalVector SavedVector(double[] value) => new(value[0], value[1], value[2]);
    private static Quaternion SavedRotation(float[] value) => new(value[0], value[1], value[2], value[3]);

    private SavedCoast CaptureCoast()
    {
        var frame = CurrentSpaceFrame();
        return new()
        {
            Origin = SavedVector(frame.Origin), Velocity = SavedVector(frame.Velocity),
            Right = SavedVector(frame.Right), Up = SavedVector(frame.Up), Forward = SavedVector(frame.Forward),
            CruiseVelocity = SavedVector(_spaceCruiseVelocity)
        };
    }

    private static double[] SavedVector(OrbitalVector value) => new[] { value.X, value.Y, value.Z };

    private static bool OrbitalPoseValid(OrbitalVector position, OrbitalVector velocity) => position.IsFinite
        && position.LengthSquared <= OrbitalSimulation.MaximumPosition * OrbitalSimulation.MaximumPosition
        && velocity.IsFinite && velocity.LengthSquared <= OrbitalSimulation.MaximumSpeed * OrbitalSimulation.MaximumSpeed;

    private static bool TryRestoreCoast(SavedCoast value, out OrbitalFrame frame)
    {
        frame = default;
        if (value.Origin?.Length != 3 || value.Velocity?.Length != 3 || value.Right?.Length != 3
            || value.Up?.Length != 3 || value.Forward?.Length != 3 || value.CruiseVelocity?.Length != 3) return false;
        var origin = SavedVector(value.Origin);
        var velocity = SavedVector(value.Velocity);
        var cruise = SavedVector(value.CruiseVelocity);
        if (!origin.IsFinite || !velocity.IsFinite || !cruise.IsFinite
            || origin.LengthSquared > OrbitalSimulation.MaximumPosition * OrbitalSimulation.MaximumPosition
            || velocity.LengthSquared > OrbitalSimulation.MaximumSpeed * OrbitalSimulation.MaximumSpeed
            || cruise.LengthSquared > OrbitalSimulation.MaximumSpeed * OrbitalSimulation.MaximumSpeed) return false;
        try
        {
            frame = new(origin, velocity, default, SavedVector(value.Right), SavedVector(value.Up), SavedVector(value.Forward), default);
            return true;
        }
        catch (ArgumentException) { return false; }
    }

    // Coast captures the inertial frame at the saved orbital time. Position and
    // velocity remain local to it; no player pose or live seat lease is persisted.
    public sealed class SavedNavigation
    {
        [JsonRequired] public SpacecraftRegion Region { get; set; }
        [JsonRequired] public double[]? Position { get; set; }
        [JsonRequired] public float[]? Rotation { get; set; }
        [JsonRequired] public float[]? Velocity { get; set; }
        [JsonRequired] public float[]? AngularVelocity { get; set; }
        [JsonRequired] public bool Powered { get; set; }
        [JsonRequired] public bool Dampeners { get; set; }
        [JsonRequired] public float[]? IslandPosition { get; set; }
        [JsonRequired] public float[]? IslandRotation { get; set; }
        public SavedCoast? Coast { get; set; }
    }

    public sealed class SavedCoast
    {
        [JsonRequired] public double[]? Origin { get; set; }
        [JsonRequired] public double[]? Velocity { get; set; }
        [JsonRequired] public double[]? Right { get; set; }
        [JsonRequired] public double[]? Up { get; set; }
        [JsonRequired] public double[]? Forward { get; set; }
        [JsonRequired] public double[]? CruiseVelocity { get; set; }
    }
}
