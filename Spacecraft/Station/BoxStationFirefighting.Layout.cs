// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using Vector3 = System.Numerics.Vector3;

namespace Modstoke.Client;

internal readonly record struct BoxStationExtinguisherCabinet(int Object, int Cell, int Direction);
internal readonly record struct BoxStationWaterTank(int Object, int Cell, bool High, float Capacity);
internal readonly record struct BoxStationExtinguisherItem(int Object, int Cell, int Cabinet);

internal sealed partial class BoxStationFirefighting
{
    private sealed class Definitions
    {
        internal required ImmutableArray<BoxStationExtinguisherCabinet> Cabinets { get; init; }
        internal required ImmutableArray<BoxStationWaterTank> Tanks { get; init; }
        internal required ImmutableArray<BoxStationExtinguisherItem> Items { get; init; }
        internal required ImmutableArray<bool> DropCells { get; init; }
    }

    internal static ImmutableArray<BoxStationExtinguisherCabinet> GetCabinets(BoxStationLayout layout)
        => LayoutDefinitions.GetValue(layout, BuildDefinitions).Cabinets;

    internal static ImmutableArray<BoxStationWaterTank> GetTanks(BoxStationLayout layout)
        => LayoutDefinitions.GetValue(layout, BuildDefinitions).Tanks;

    internal static ImmutableArray<BoxStationExtinguisherItem> GetItems(BoxStationLayout layout)
        => LayoutDefinitions.GetValue(layout, BuildDefinitions).Items;

    private static Definitions BuildDefinitions(BoxStationLayout layout)
    {
        const string cabinetType = "/obj/structure/extinguisher_cabinet";
        const string itemType = "/obj/item/extinguisher";
        const string tankType = "/obj/structure/reagent_dispensers/watertank";
        var cabinets = ImmutableArray.CreateBuilder<BoxStationExtinguisherCabinet>();
        var tanks = ImmutableArray.CreateBuilder<BoxStationWaterTank>();
        var items = ImmutableArray.CreateBuilder<BoxStationExtinguisherItem>();
        var dropCells = new bool[layout.Cells.Length];
        foreach (var cell in layout.Cells)
            dropCells[cell.Id] = (cell.Flags & (BoxStationCellFlags.Floor | BoxStationCellFlags.Catwalk)) != 0
                && (cell.Flags & (BoxStationCellFlags.Wall | BoxStationCellFlags.FullWindow | BoxStationCellFlags.Grille)) == 0;
        foreach (var door in layout.Doors) dropCells[door.Cell] = false;

        // Preserve source object order, including two separate loose items on the same cell.
        foreach (var source in layout.Objects)
        {
            string type = layout.Types[source.Type];
            if (type == cabinetType || type.StartsWith(cabinetType + "/", StringComparison.Ordinal))
            {
                if (type != cabinetType || cabinets.Count >= short.MaxValue)
                    throw new InvalidDataException($"Station extinguisher cabinet {source.Id} has an unsupported subtype or identity.");
                ValidateCabinetSource(source, type);
                int direction = source.Direction == 0 ? 2 : source.Direction;
                if (direction is not (1 or 2 or 4 or 8))
                    throw new InvalidDataException($"Station extinguisher cabinet {source.Id} has an invalid direction.");
                items.Add(new(source.Id, source.Cell, cabinets.Count));
                cabinets.Add(new(source.Id, source.Cell, direction));
            }
            else if (type == itemType || type.StartsWith(itemType + "/", StringComparison.Ordinal))
            {
                if (type != itemType || source.Raw.Trim() != type || !dropCells[source.Cell])
                    throw new InvalidDataException($"Station extinguisher {source.Id} has an unsupported source or floor position.");
                items.Add(new(source.Id, source.Cell, -1));
            }
            else if (type == tankType || type.StartsWith(tankType + "/", StringComparison.Ordinal))
            {
                bool high = type == tankType + "/high";
                if ((!high && type != tankType) || source.Raw.Trim() != type)
                    throw new InvalidDataException($"Station water tank {source.Id} has an unsupported subtype or source override.");
                tanks.Add(new(source.Id, source.Cell, high, high ? 100000 : 1000));
            }
        }
        return new()
        {
            Cabinets = cabinets.ToImmutable(), Tanks = tanks.ToImmutable(), Items = items.ToImmutable(),
            DropCells = ImmutableArray.CreateRange(dropCells)
        };
    }

    private static void ValidateCabinetSource(BoxStationObject source, string type)
    {
        string raw = source.Raw.Trim();
        if (raw == type) return;
        if (!raw.StartsWith(type + "{", StringComparison.Ordinal) || !raw.EndsWith('}'))
            throw new InvalidDataException($"Station extinguisher cabinet {source.Id} has an unsupported source override.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (string setting in raw.Substring(type.Length + 1, raw.Length - type.Length - 2).Split(';'))
        {
            string[] pair = setting.Split('=');
            if (pair.Length != 2)
                throw new InvalidDataException($"Station extinguisher cabinet {source.Id} has an invalid source setting.");
            string name = pair[0].Trim();
            if (name is not ("pixel_x" or "pixel_y" or "dir") || !names.Add(name)
                || !int.TryParse(pair[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                || value is < -64 or > 64 || name == "dir" && value != source.Direction)
                throw new InvalidDataException($"Station extinguisher cabinet {source.Id} has an unsupported source setting.");
        }
    }

    // Positions are station-local floor coordinates; held items retain the last eligible position.
    internal static bool ValidDropPosition(BoxStationLayout layout, Vector3 position)
        => ValidDropPosition(layout, LayoutDefinitions.GetValue(layout, BuildDefinitions), position);

    private static bool ValidDropPosition(BoxStationLayout layout, Definitions definitions, Vector3 position)
    {
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z)
            || MathF.Abs(position.Y) > .001f) return false;
        double x = Math.Floor((double)position.X / BoxStationLayout.TileSize + layout.Width / 2 + .5);
        double row = Math.Floor(layout.Height / 2 - (double)position.Z / BoxStationLayout.TileSize + .5);
        if (x < 0 || x >= layout.Width || row < 0 || row >= layout.Height) return false;
        return definitions.DropCells[(int)row * layout.Width + (int)x];
    }

    private static Vector3 CellPosition(BoxStationLayout layout, int index)
    {
        var cell = layout.Cells[index];
        return new((cell.X - layout.Width / 2) * BoxStationLayout.TileSize, 0,
            (layout.Height / 2 - cell.Row) * BoxStationLayout.TileSize);
    }
}
