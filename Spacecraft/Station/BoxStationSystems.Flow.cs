// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json;

namespace Modstoke.Client;

internal sealed partial class BoxStationSystems
{
    private const double DoorBypassArea = .02;

    private static (GasLink[] Links, int[][] DoorLinks) BuildLinks(BoxStationLayout layout, ImmutableArray<BoxStationDoor> definitions)
    {
        var cellDoors = new List<int>?[layout.Cells.Length];
        var doorLinks = new List<int>[definitions.Length];
        for (int index = 0; index < definitions.Length; index++)
        {
            var door = definitions[index];
            (cellDoors[door.Cell] ??= new List<int>()).Add(index);
            doorLinks[index] = new List<int>();
        }
        var links = new List<GasLink>();
        foreach (var cell in layout.Cells)
        {
            if (cell.X + 1 < layout.Width) AddLink(cell, layout.Cells[cell.Id + 1], cell.East, 1, 4, 8);
            if (cell.Row + 1 < layout.Height) AddLink(cell, layout.Cells[cell.Id + layout.Width], cell.South, 2, 2, 1);
        }
        var indices = new int[doorLinks.Length][];
        for (int door = 0; door < indices.Length; door++) indices[door] = doorLinks[door].ToArray();
        return (links.ToArray(), indices);

        void AddLink(BoxStationCell first, BoxStationCell second, BoxStationBarrier barrier, int direction, int forward, int reverse)
        {
            if ((barrier & BoxStationBarrier.GasOnly) != 0) return;
            bool firstWall = (first.Flags & BoxStationCellFlags.Wall) != 0;
            bool secondWall = (second.Flags & BoxStationCellFlags.Wall) != 0;
            int firstIndex = BoxStationHull.CavityIndex(layout, first.Id);
            int secondIndex = BoxStationHull.CavityIndex(layout, second.Id);
            if (firstWall && firstIndex < 0 || secondWall && secondIndex < 0
                || (barrier & BoxStationBarrier.Wall) != 0 && !firstWall && !secondWall) return;
            if (first.Zone < 0 && firstIndex < 0 && second.Zone < 0 && secondIndex < 0) return;
            var gates = new List<int>();
            AddGates(first.Id, forward);
            AddGates(second.Id, reverse);
            int index = links.Count;
            bool initiallyBlocked = firstWall || secondWall || (barrier & BoxStationBarrier.Window) != 0;
            links.Add(new(first.Id, second.Id, direction, initiallyBlocked, gates.ToArray()));
            foreach (int gate in gates) doorLinks[gate].Add(index);

            void AddGates(int cell, int direction)
            {
                var doors = cellDoors[cell];
                if (doors == null) return;
                foreach (int id in doors)
                {
                    var door = definitions[id];
                    if (!door.Edge || door.Direction == direction) gates.Add(id);
                }
            }
        }
    }

    private double LinkOpenArea(GasLink link, int bypassDoor)
    {
        if (!LinkBarriersOpen(link)) return 0;
        double area = (double)BoxStationLayout.TileSize * BoxStationLayout.DeckHeight;
        foreach (int gate in link.Gates)
        {
            double opening = gate == bypassDoor ? DoorBypassArea
                : (double)BoxStationLayout.DoorWidth * BoxStationLayout.DoorHeight * _doors[gate];
            area = Math.Min(area, opening);
            if (area == 0) break;
        }
        return area;
    }

    private bool LinkWillOpen(GasLink link, int door)
    {
        if (!LinkBarriersOpen(link)) return false;
        foreach (int gate in link.Gates)
            if (gate != door && _doors[gate] == 0 && !_targets[gate]) return false;
        return true;
    }

    private bool LinkBarriersOpen(GasLink link)
        => (Hull.EffectiveBarrier(link.First, link.Direction)
            & (BoxStationBarrier.Wall | BoxStationBarrier.Window | BoxStationBarrier.GasOnly)) == 0;

    private void Flow(GasLink link, double area, double seconds)
    {
        int first = _rooms.NodeAtCell(link.First), second = _rooms.NodeAtCell(link.Second);
        if (first == second) return;
        if (first >= 0 && second >= 0)
            StationGas.FlowThroughOpening(ref _rooms.Gas(first), ref _rooms.Gas(second), area, seconds);
        else
        {
            ref var gas = ref _rooms.Gas(first >= 0 ? first : second);
            // The exterior receives gas and its sensible energy, never supplies it.
            gas.ExhaustToVacuum(area, seconds);
        }
    }

    private void FindAirlockPartners()
    {
        var candidates = new int[DoorCount];
        Array.Fill(candidates, -1);
        foreach (var link in Layout.CycleLinks.EnumerateArray())
        {
            if (!TryIndex(link, "door", DoorCount, out int door)
                || !TryIndex(link, "candidateDoor", DoorCount, out int candidate) || door == candidate) continue;
            candidates[door] = candidate;
        }
        for (int door = 0; door < Layout.Doors.Length; door++)
        {
            int partner = candidates[door];
            if (partner <= door || candidates[partner] != door || _partners[door] >= 0 || _partners[partner] >= 0) continue;
            var first = Layout.Doors[door];
            var second = Layout.Doors[partner];
            if (first.Edge || second.Edge || first.Kind != "airlock" || second.Kind != "airlock"
                || first.Ports.Length != 2 || second.Ports.Length != 2) continue;
            int chamber = -1, shared = 0;
            foreach (var firstPort in first.Ports)
                foreach (var secondPort in second.Ports)
                    if (firstPort.Zone >= 0 && firstPort.Zone == secondPort.Zone)
                    {
                        chamber = firstPort.Zone;
                        shared++;
                    }
            if (shared != 1 || chamber == first.Zone || chamber == second.Zone
                || !ChamberSealedBy(chamber, door, partner)) continue;
            _partners[door] = partner;
            _partners[partner] = door;
        }
    }

    private bool ChamberSealedBy(int chamber, int firstDoor, int secondDoor)
    {
        bool firstBoundary = false, secondBoundary = false;
        foreach (var link in _links)
        {
            // Partner discovery describes the intact source station. A latent
            // wall or window opening is not an initial uncontrolled chamber route.
            if (link.InitiallyBlocked) continue;
            int first = Layout.Cells[link.First].Zone, second = Layout.Cells[link.Second].Zone;
            if (first == second || first != chamber && second != chamber) continue;
            if (link.Gates.Length != 1) return false;
            if (link.Gates[0] == firstDoor) firstBoundary = true;
            else if (link.Gates[0] == secondDoor) secondBoundary = true;
            else return false;
        }
        return firstBoundary && secondBoundary && HasSingleOutsideRoute(firstDoor, chamber)
            && HasSingleOutsideRoute(secondDoor, chamber);
    }

    private bool HasSingleOutsideRoute(int door, int chamber)
    {
        int outside = 0, inside = 0;
        foreach (int index in _doorLinks[door])
        {
            var link = _links[index];
            if (link.InitiallyBlocked) continue;
            if (link.Gates.Length != 1) return false;
            if (Layout.Cells[link.First].Zone == chamber || Layout.Cells[link.Second].Zone == chamber) inside++;
            else outside++;
        }
        return inside == 1 && outside == 1;
    }

    private static bool TryIndex(JsonElement value, string name, int count, out int index)
    {
        index = -1;
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out index) && index >= 0 && index < count;
    }

    private readonly record struct GasLink(int First, int Second, int Direction, bool InitiallyBlocked, int[] Gates);
}
