// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Globalization;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftService
{
    private double _stationSolarSample = double.NaN;
    private BoxStationRadiation _stationRadiation;

    private static bool IsStationPower(ActionCode action) => action is >= ActionCode.StationEquipment and <= ActionCode.StationEnvironment;
    private static BoxStationPowerChannel StationChannel(ActionCode action)
        => (BoxStationPowerChannel)(1 << ((int)action - (int)ActionCode.StationEquipment));

    private static bool TryStationPower(string command, out int apc, out ActionCode action)
    {
        string prefix;
        if (command.StartsWith("station-equipment-", StringComparison.Ordinal))
        { prefix = "station-equipment-"; action = ActionCode.StationEquipment; }
        else if (command.StartsWith("station-lighting-", StringComparison.Ordinal))
        { prefix = "station-lighting-"; action = ActionCode.StationLighting; }
        else if (command.StartsWith("station-environment-", StringComparison.Ordinal))
        { prefix = "station-environment-"; action = ActionCode.StationEnvironment; }
        else { apc = -1; action = default; return false; }
        return int.TryParse(command.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out apc)
            && (uint)apc < (uint)BoxStationPower.GetApcs(BoxStationLayout.Current).Length;
    }

    private string ExecuteStationPower(Peer peer, int apc, BoxStationPowerChannel channel, Vector3 actorPosition)
    {
        if ((uint)apc >= (uint)_stationSystems.Power.Apcs.Length) return "Unknown station power controller.";
        string failure = StationControlAccess(peer, actorPosition);
        if (failure.Length != 0) return failure;
        var target = _orbitalStation!.ApcControlPoint(apc, channel);
        failure = ReachStationControl(peer, actorPosition, target);
        if (failure.Length != 0) return failure;
        if (!_stationSystems.Power.Toggle(apc, channel)) return "The circuit could not be changed.";
        _nextStationSnapshot = 0;
        return "Area circuit breaker changed.";
    }

    private string StationControlAccess(Peer peer, Vector3 actorPosition)
    {
        if (_body?.TelemetryCurrent != true || !_body.State.Docked || _body.State.Travel != SpacecraftTravel.None
            || _orbitalStation?.Visible != true || _stationFailed || !TryStationPose(out _))
            return "Latch the ship to a current station berth first.";
        if (!peer.Attached || !peer.Walking || !peer.Prepared || !peer.EntryPrepared
            || peer.Seat is < 0 or > 2 || !ReferenceEquals(_seats[peer.Seat], peer)
            || peer.Lease == Guid.Empty || !CabinActorCurrent(peer, actorPosition))
            return "Control out of reach.";
        return string.Empty;
    }

    private string ReachStationControl(Peer peer, Vector3 actorPosition, Vector3 target, bool animate = true)
    {
        var eye = actorPosition + _orbitalStation!.Root.transform.up * 1.3f;
        var delta = target - eye;
        if (delta.sqrMagnitude > 2.4f * 2.4f) return "Move within reach of this control.";
        var hits = _accessHits ??= new Il2CppStructArray<RaycastHit>(size: 32);
        int count = Physics.RaycastNonAlloc(eye, delta.normalized, hits, Mathf.Max(0, delta.magnitude - .04f),
            ~0, QueryTriggerInteraction.Ignore);
        if (count < 0 || count >= hits.Length) return "The control is obstructed.";
        var actor = ActorTransform(peer);
        for (int index = 0; index < count; index++)
        {
            var collider = hits[index].collider;
            if (!collider || actor != null && actor && collider.transform.IsChildOf(actor)) continue;
            return "The control is obstructed.";
        }
        if (animate && TryActor(peer, out _, out _, out var actorId))
            RecordStationInteraction(peer, actorId, target);
        return string.Empty;
    }

    private void RecordStationInteraction(Peer peer, ulong actor, Vector3 target)
    {
        _interactions[peer.Seat] = new(actor, _body!.Root.transform.InverseTransformPoint(target), 1);
        _apuGuardContacts[peer.Seat] = null;
        _interactionUntil[peer.Seat] = Now + 650;
        _nextSnapshot = 0;
    }

    private BoxStationRadiation StationRadiationEnvironment()
    {
        if (_orbits == null || _orbits.PendingSeconds >= 1) return default;
        double time = _orbits.SampleTime;
        if (time >= _stationSolarSample && time - _stationSolarSample < .5) return _stationRadiation;
        _stationSolarSample = time;
        _stationRadiation = default;
        var bodies = OrbitalCatalog.Current.Bodies;
        int sun = -1, earth = -1;
        for (int i = 0; i < bodies.Count; i++)
        {
            if (bodies[i].Id == 10) sun = i;
            else if (bodies[i].Id == 399) earth = i;
        }
        if (sun < 0 || earth < 0) return default;
        var station = _orbits.Sample(OrbitalStationOrbit.Index);
        var planet = _orbits.Sample(earth);
        var toSun = _orbits.Sample(sun).Position - station.Position;
        var up = station.Position - planet.Position;
        double distance = toSun.Length, height = up.Length;
        if (!double.IsFinite(distance) || !double.IsFinite(height)
            || distance <= bodies[sun].Radius || height <= bodies[earth].Radius) return default;
        var direction = toSun / distance;
        up /= height;
        var velocity = station.Velocity - planet.Velocity;
        var tangent = velocity - up * OrbitalVector.Dot(velocity, up);
        double speed = tangent.Length;
        if (!double.IsFinite(speed) || speed < .0001) return default;
        var right = tangent / speed;
        var forward = OrbitalVector.Cross(right, up);
        var localSun = new OrbitalVector(OrbitalVector.Dot(direction, right),
            OrbitalVector.Dot(direction, up), OrbitalVector.Dot(direction, forward));
        // Same attitude as TryStationPose. Spherical point-source eclipses omit
        // penumbra; radiator-local structure shadows are handled by the pipe model.
        bool eclipsed = false;
        for (int i = 0; i < bodies.Count; i++)
        {
            if (i == sun) continue;
            var offset = _orbits.Sample(i).Position - station.Position;
            double along = OrbitalVector.Dot(offset, direction);
            double radius = bodies[i].Radius;
            if (offset.LengthSquared <= radius * radius) return default;
            if (along > 0 && along < distance && (offset - direction * along).LengthSquared < radius * radius)
                eclipsed = true;
        }
        const double astronomicalUnit = 149_597_870_700;
        double ratio = astronomicalUnit / distance;
        double solar = eclipsed ? 0 : BoxStationRadiation.SolarReference * ratio * ratio;
        // Uniform 240 W/m² outgoing Earth longwave, projected onto the nadir-facing
        // plate. Weather, reflected sunlight and non-Earth planetary heating are omitted.
        double disk = bodies[earth].Radius / height;
        _stationRadiation = new(true, time, localSun, solar, 240 * disk * disk);
        return _stationRadiation;
    }
}
