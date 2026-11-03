// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftBody
{
    private BoxStationModel? _dockedStation;

    internal bool CrewDeckGravity => State.Region != SpacecraftRegion.Space || State.Docked;

    private bool DockedStationCurrent
    {
        get
        {
            if (!Ready || !State.Docked || _dockedStation?.Visible != true) return false;
            var station = _dockedStation.Root.transform;
            var offset = station.InverseTransformPoint(Root.transform.position) - BoxStationDock.Position;
            return offset.sqrMagnitude <= .25f
                && Quaternion.Angle(station.rotation * BoxStationDock.Rotation, Root.transform.rotation) <= 1;
        }
    }

    internal void SetDockedStation(BoxStationModel? model) => _dockedStation = model;

    internal bool TryCrewWalkSurface(Vector3 worldFeet, out Vector3 surface)
    {
        if (!State.Docked) return TryWalkSurface(worldFeet, out surface);
        surface = default;
        if (!Ready || !Finite(worldFeet)) return false;
        var stationCurrent = DockedStationCurrent;
        var frame = Root.transform;
        var up = frame.up;
        var ray = new Ray(worldFeet + up * .3f, -up);
        var nearest = float.PositiveInfinity;
        foreach (var floor in _walkSurfaces)
        {
            var ramp = floor == _model!.BoardingRamp || floor == _model.BoardingExtension;
            if (ramp && (!stationCurrent || State.DoorAmount < .99f)) continue;
            if (!floor || !floor.enabled || !floor.gameObject.activeInHierarchy
                || !floor.Raycast(ray, out var hit, .8f) || Vector3.Dot(hit.normal, up) < .7f) continue;
            var distance = Mathf.Abs(Vector3.Dot(worldFeet - hit.point, up) - .1f);
            if (distance > .35f || distance >= nearest) continue;
            var feet = frame.InverseTransformPoint(hit.point + up * .1f);
            if (!ramp && !CrewCapsuleInCabin(feet, stationCurrent && State.DoorAmount >= .99f)) continue;
            nearest = distance;
            surface = hit.point;
        }
        if (stationCurrent && _dockedStation!.TryWalkSurface(worldFeet, out var stationSurface))
        {
            var distance = Mathf.Abs(Vector3.Dot(worldFeet - stationSurface, up) - .1f);
            if (distance <= .35f && distance < nearest
                && _dockedStation.ContainsWalkArea(stationSurface + up * .1f))
            {
                nearest = distance;
                surface = stationSurface;
            }
        }
        return float.IsFinite(nearest);
    }

    internal bool ContainsCrewWalkArea(Vector3 worldFeet)
    {
        if (!State.Docked) return ContainsCabin(worldFeet);
        if (!TryCrewWalkSurface(worldFeet, out var surface)) return false;
        // Bound observed feet around their supported pose. A step allowance at
        // each endpoint alone would permit repeated upward moves above a deck.
        return Mathf.Abs(Vector3.Dot(worldFeet - surface, Root.transform.up) - .1f) <= .12f;
    }
}
