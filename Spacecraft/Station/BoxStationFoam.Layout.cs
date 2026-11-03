// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using Vector3 = System.Numerics.Vector3;

namespace Modstoke.Client;

internal readonly record struct BoxStationFoamItem(int Object, int Cell, Vector3 Position);

internal sealed partial class BoxStationFoam
{
    private sealed class Definitions
    {
        internal required ImmutableArray<BoxStationFoamItem> Items { get; init; }
        internal required bool[] FloorCells { get; init; }
        internal required List<int>?[] CellDoors { get; init; }
    }

    internal static ImmutableArray<BoxStationFoamItem> GetItems(BoxStationLayout layout)
        => LayoutDefinitions.GetValue(layout, BuildDefinitions).Items;

    private static Definitions BuildDefinitions(BoxStationLayout layout)
    {
        const string itemType = "/obj/item/grenade/chem_grenade/smart_metal_foam";
        var items = ImmutableArray.CreateBuilder<BoxStationFoamItem>();
        var floorCells = new bool[layout.Cells.Length];
        var cellDoors = new List<int>?[layout.Cells.Length];
        foreach (var cell in layout.Cells)
            floorCells[cell.Id] = (cell.Flags & (BoxStationCellFlags.Floor | BoxStationCellFlags.Catwalk)) != 0
                && (cell.Flags & (BoxStationCellFlags.Wall | BoxStationCellFlags.FullWindow | BoxStationCellFlags.Grille)) == 0;
        foreach (var door in layout.Doors)
            (cellDoors[door.Cell] ??= new List<int>()).Add(door.Id);
        foreach (var source in layout.Objects)
        {
            string type = layout.Types[source.Type];
            if (type != itemType && !type.StartsWith(itemType + "/", StringComparison.Ordinal)) continue;
            if (type != itemType || !ValidDropPosition(layout, CellPosition(layout, source.Cell)))
                throw new InvalidDataException($"Station foam grenade {source.Id} has an unsupported source or floor position.");
            var offset = SourceOffset(source, type);
            items.Add(new(source.Id, source.Cell, CellPosition(layout, source.Cell) + offset));
        }
        // Co-located source objects remain distinct; sprite offsets retain their source axes.
        for (int i = 0; i < items.Count; i++)
        {
            int before = 0, count = 0;
            for (int j = 0; j < items.Count; j++)
                if (items[j].Cell == items[i].Cell) { count++; if (j < i) before++; }
            var position = items[i].Position;
            position.X += (before - (count - 1) * .5f) * .18f;
            items[i] = items[i] with { Position = position };
        }
        return new() { Items = items.ToImmutable(), FloorCells = floorCells, CellDoors = cellDoors };
    }

    private static Vector3 SourceOffset(BoxStationObject source, string type)
    {
        string raw = source.Raw.Trim();
        var offset = new Vector3(0, 1.06f, 0);
        if (raw == type) return offset;
        if (!raw.StartsWith(type + "{", StringComparison.Ordinal) || !raw.EndsWith('}'))
            throw new InvalidDataException($"Station foam grenade {source.Id} has an unsupported source override.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (string setting in raw.Substring(type.Length + 1, raw.Length - type.Length - 2).Split(';'))
        {
            string[] pair = setting.Split('=');
            if (pair.Length != 2)
                throw new InvalidDataException($"Station foam grenade {source.Id} has an invalid source setting.");
            string name = pair[0].Trim();
            if (name is not ("pixel_x" or "pixel_y") || !names.Add(name)
                || !int.TryParse(pair[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                || value is < -16 or > 16)
                throw new InvalidDataException($"Station foam grenade {source.Id} has an unsupported source setting.");
            if (name == "pixel_x") offset.X = value * (BoxStationLayout.TileSize / 32);
            else offset.Z = value * (BoxStationLayout.TileSize / 32);
        }
        return offset;
    }

    internal static bool ValidDropPosition(BoxStationLayout layout, Vector3 position)
        => BoxStationFirefighting.ValidDropPosition(layout, position);

    internal static bool ValidAnchorPosition(BoxStationLayout layout, int target, Vector3 position)
    {
        if (!BoxStationHull.ValidPanelTarget(layout, target) || !float.IsFinite(position.X)
            || !float.IsFinite(position.Y) || !float.IsFinite(position.Z)
            || !LayoutDefinitions.GetValue(layout, BuildDefinitions).FloorCells[target / 2]) return false;
        float height = (target & 1) == 0 ? 0 : BoxStationLayout.DeckHeight;
        var offset = position - CellPosition(layout, target / 2);
        float rim = Math.Max(MathF.Abs(offset.X), MathF.Abs(offset.Z));
        return MathF.Abs(position.Y - height) <= .001f && rim >= 1 && rim <= BoxStationLayout.TileSize / 2;
    }

    private static Vector3 CellPosition(BoxStationLayout layout, int index)
    {
        var cell = layout.Cells[index];
        return new((cell.X - layout.Width / 2) * BoxStationLayout.TileSize, 0,
            (layout.Height / 2 - cell.Row) * BoxStationLayout.TileSize);
    }

    private bool CanCross(int first, int second, int direction, ReadOnlySpan<float> doorAmounts, ImmutableArray<int> builtDoors)
    {
        int cardinal = direction switch { 1 => 0, 4 => 1, 2 => 2, _ => 3 };
        var barrier = _hull.EffectiveBarrier(first, cardinal);
        int reverse = direction switch { 1 => 2, 4 => 8, 2 => 1, _ => 4 };
        int edge = cardinal switch { 0 => first * 2, 1 => first * 2 + 1, 2 => second * 2, _ => second * 2 + 1 };
        int built = BoxStationDoors.Index(_layout, builtDoors, _layout.Doors.Length + edge);
        if (built >= 0 && (!float.IsFinite(doorAmounts[built]) || doorAmounts[built] < .99f || doorAmounts[built] > 1)) return false;
        return (barrier & (BoxStationBarrier.Wall | BoxStationBarrier.Window | BoxStationBarrier.GasOnly)) == 0
            && DoorsOpen(first, direction, doorAmounts) && DoorsOpen(second, reverse, doorAmounts);
    }

    private bool DoorsOpen(int cell, int direction, ReadOnlySpan<float> doorAmounts)
    {
        var doors = _definitions.CellDoors[cell];
        if (doors is null) return true;
        foreach (int index in doors)
        {
            var door = _layout.Doors[index];
            if (!door.Edge || door.Direction == direction)
            {
                float amount = doorAmounts[index];
                if (!float.IsFinite(amount) || amount < .99f || amount > 1) return false;
            }
        }
        return true;
    }
}
