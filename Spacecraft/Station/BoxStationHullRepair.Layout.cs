// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using Vector3 = System.Numerics.Vector3;

namespace Modstoke.Client;

internal readonly record struct BoxStationRepairSource(int Object, int Cell, Vector3 Position, ushort Sheets, byte Tiles);

internal sealed partial class BoxStationHullRepair
{
    private sealed class Definitions
    {
        internal required ImmutableArray<BoxStationRepairSource> Sources { get; init; }
        internal required int MaxPacks { get; init; }
    }

    internal static ImmutableArray<BoxStationRepairSource> GetSources(BoxStationLayout layout)
        => LayoutDefinitions.GetValue(layout, BuildDefinitions).Sources;

    internal static int MaxPacks(BoxStationLayout layout)
        => LayoutDefinitions.GetValue(layout, BuildDefinitions).MaxPacks;

    internal static bool ValidDropPosition(BoxStationLayout layout, Vector3 position)
        => BoxStationFirefighting.ValidDropPosition(layout, position);

    private static Definitions BuildDefinitions(BoxStationLayout layout)
    {
        const string metal = "/obj/item/stack/sheet/metal", tile = "/obj/item/stack/tile/plasteel";
        var sources = new List<BoxStationRepairSource>();
        int maxPacks = 0;
        foreach (var source in layout.Objects)
        {
            string type = layout.Types[source.Type];
            if (type != metal && !type.StartsWith(metal + "/", StringComparison.Ordinal)
                && type != tile && !type.StartsWith(tile + "/", StringComparison.Ordinal)) continue;
            ushort sheets = type switch
            {
                metal => 1,
                metal + "/five" => 5,
                metal + "/fifty" => 50,
                tile => 0,
                _ => throw new InvalidDataException($"Station repair source {source.Id} has an unsupported material type.")
            };
            byte tiles = (byte)(type == tile ? 1 : 0);
            var cell = layout.Cells[source.Cell];
            var position = new Vector3((cell.X - layout.Width / 2) * BoxStationLayout.TileSize, 0,
                (layout.Height / 2 - cell.Row) * BoxStationLayout.TileSize) + SourceOffset(source, type);
            sources.Add(new(source.Id, source.Cell, position, sheets, tiles));
            maxPacks = checked(maxPacks + sheets + tiles);
        }
        sources.Sort(static (a, b) => a.Object.CompareTo(b.Object));
        // Separate co-located stacks while retaining the source sprite offsets on their original axes.
        for (int i = 0; i < sources.Count; i++)
        {
            int before = 0, count = 0;
            for (int j = 0; j < sources.Count; j++)
                if (sources[j].Cell == sources[i].Cell) { count++; if (j < i) before++; }
            var position = sources[i].Position;
            position.X += (before - (count - 1) * .5f) * .68f;
            sources[i] = sources[i] with { Position = position };
        }
        return new() { Sources = ImmutableArray.CreateRange(sources), MaxPacks = maxPacks };
    }

    private static Vector3 SourceOffset(BoxStationObject source, string type)
    {
        string raw = source.Raw.Trim();
        var offset = Vector3.Zero;
        if (raw == type) return offset;
        if (!raw.StartsWith(type + "{", StringComparison.Ordinal) || !raw.EndsWith('}'))
            throw new InvalidDataException($"Station repair source {source.Id} has an unsupported source override.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (string setting in raw.Substring(type.Length + 1, raw.Length - type.Length - 2).Split(';'))
        {
            string[] pair = setting.Split('=');
            if (pair.Length != 2)
                throw new InvalidDataException($"Station repair source {source.Id} has an invalid source setting.");
            string name = pair[0].Trim();
            if (name is not ("pixel_x" or "pixel_y" or "dir") || !names.Add(name)
                || !int.TryParse(pair[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                || (name == "dir" ? value != source.Direction : value is < -16 or > 16))
                throw new InvalidDataException($"Station repair source {source.Id} has an unsupported source setting.");
            if (name == "pixel_x") offset.X = value * (BoxStationLayout.TileSize / 32);
            else if (name == "pixel_y") offset.Z = value * (BoxStationLayout.TileSize / 32);
        }
        return offset;
    }
}
