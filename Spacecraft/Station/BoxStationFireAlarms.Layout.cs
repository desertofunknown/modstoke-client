// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Immutable;
using System.IO;

namespace Modstoke.Client;

internal sealed partial class BoxStationFireAlarms
{
    private sealed class Definitions
    {
        internal required ImmutableArray<BoxStationFireAlarmDevice> Devices { get; init; }
        internal required ImmutableArray<int>[] DoorAreas { get; init; }
        internal required ImmutableArray<int>[] AreaDoors { get; init; }
        internal required bool[] AlwaysUnpowered { get; init; }
    }

    private static Definitions BuildDefinitions(BoxStationLayout layout)
    {
        const string alarmType = "/obj/machinery/firealarm";
        var devices = ImmutableArray.CreateBuilder<BoxStationFireAlarmDevice>();
        foreach (var item in layout.Objects)
        {
            string type = layout.Types[item.Type];
            if (type != alarmType && !type.StartsWith(alarmType + "/", StringComparison.Ordinal)) continue;
            if (type != alarmType)
                throw new InvalidDataException($"Station fire alarm {item.Id} has an unsupported subtype.");
            var cell = layout.Cells[item.Cell];
            int direction = item.Direction == 0 ? 2 : item.Direction;
            if (cell.Zone < 0 || direction is not (1 or 2 or 4 or 8))
                throw new InvalidDataException($"Station fire alarm {item.Id} has an invalid compartment or direction.");
            devices.Add(new(item.Id, item.Cell, cell.Area, cell.Zone, direction));
        }

        var doorAreas = new ImmutableArray<int>[layout.Doors.Length];
        var areaDoors = new ImmutableArray<int>[layout.Areas.Length];
        var areaBuilders = new ImmutableArray<int>.Builder[layout.Areas.Length];
        var alwaysUnpowered = new bool[layout.Areas.Length];
        for (int area = 0; area < layout.Areas.Length; area++)
        {
            string type = layout.Areas[area];
            alwaysUnpowered[area] = type == "/area/space" || type.StartsWith("/area/space/", StringComparison.Ordinal);
            areaBuilders[area] = ImmutableArray.CreateBuilder<int>();
        }
        foreach (var door in layout.Doors)
        {
            if (door.Kind != "firedoor")
            {
                doorAreas[door.Id] = ImmutableArray<int>.Empty;
                continue;
            }
            var cell = layout.Cells[door.Cell];
            var areas = ImmutableArray.CreateBuilder<int>(5);
            areas.Add(cell.Area);
            // Source registration uses cardinal open turf types, not atmospheric
            // passability, door ports or direction. Own area is unconditional.
            AddOpenArea(layout, areas, cell.X, cell.Row - 1);
            AddOpenArea(layout, areas, cell.X, cell.Row + 1);
            AddOpenArea(layout, areas, cell.X + 1, cell.Row);
            AddOpenArea(layout, areas, cell.X - 1, cell.Row);
            doorAreas[door.Id] = areas.ToImmutable();
            foreach (int area in areas) areaBuilders[area].Add(door.Id);
        }
        for (int area = 0; area < areaDoors.Length; area++) areaDoors[area] = areaBuilders[area].ToImmutable();
        return new()
        {
            Devices = devices.ToImmutable(), DoorAreas = doorAreas, AreaDoors = areaDoors,
            AlwaysUnpowered = alwaysUnpowered
        };
    }

    private static void AddOpenArea(BoxStationLayout layout, ImmutableArray<int>.Builder areas, int x, int row)
    {
        // The source helper clamps outward map-edge lookups to the edge cell.
        var cell = layout.GetCell(Math.Clamp(x, 0, layout.Width - 1), Math.Clamp(row, 0, layout.Height - 1));
        string type = layout.Types[cell.TurfType];
        if ((type == "/turf/open" || type.StartsWith("/turf/open/", StringComparison.Ordinal)) && !areas.Contains(cell.Area))
            areas.Add(cell.Area);
    }
}
