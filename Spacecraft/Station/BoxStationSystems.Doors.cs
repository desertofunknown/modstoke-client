// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Immutable;

namespace Modstoke.Client;

internal sealed partial class BoxStationSystems
{
    private ImmutableArray<int> _builtDoors;
    private long _doorRevision;
    internal ImmutableArray<BoxStationDoor> DoorDefinitions { get; private set; }

    internal bool TryDoorIndex(int id, out int index)
    {
        index = BoxStationDoors.Index(Layout, _builtDoors, id);
        return index >= 0;
    }

    internal bool TrySetDoorConstruction(BoxStationDoorConstructionState state)
    {
        if (_doorRevision == long.MaxValue || state.Revision != _doorRevision + 1
            || !BoxStationHullRepair.ValidDoorConstruction(Layout, state)) return false;
        var edges = BoxStationDoors.CompleteEdges(state);
        if (_builtDoors.AsSpan().SequenceEqual(edges.AsSpan()))
        {
            _doorRevision = state.Revision;
            return true;
        }
        for (int index = Layout.Doors.Length; index < DoorCount; index++)
        {
            if (BoxStationDoors.Index(Layout, edges, DoorDefinitions[index].Id) >= 0) continue;
            if (_doors[index] < .999f || _cycling[index]) return false;
        }
        var definitions = BoxStationDoors.Definitions(Layout, edges);
        var amounts = new float[definitions.Length];
        var targets = new bool[definitions.Length];
        var cycles = new bool[definitions.Length];
        var partners = new int[definitions.Length];
        Array.Fill(partners, -1);
        for (int index = 0; index < definitions.Length; index++)
        {
            int previous = BoxStationDoors.Index(Layout, _builtDoors, definitions[index].Id);
            if (previous < 0) continue;
            amounts[index] = _doors[previous];
            targets[index] = _targets[previous];
            cycles[index] = _cycling[previous];
            if (index < Layout.Doors.Length) partners[index] = _partners[previous];
        }
        var links = BuildLinks(Layout, definitions);
        if (!_rooms.TryPrepareDoors(Hull.State, edges, out var rooms)) return false;
        // All allocations and gas conservation checks precede this joint publication.
        _rooms.CommitDoors(rooms, edges);
        _builtDoors = edges;
        _doorRevision = state.Revision;
        DoorDefinitions = definitions;
        _doors = amounts;
        _targets = targets;
        _cycling = cycles;
        _partners = partners;
        (_links, _doorLinks) = links;
        _flowHullRevision = -1;
        return true;
    }
}
