// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using Vector3 = System.Numerics.Vector3;

namespace Modstoke.Client;

internal enum BoxStationStorageKind : byte { Closet, Crate }
internal readonly record struct BoxStationStorageDefinition(int Id, int Object, int Cell, int Direction,
    BoxStationStorageKind Kind, string Name);

internal static class BoxStationStorage
{
    internal const float HalfWidth = .675f, HalfDepth = .45f, ClosetHeight = 2.2f, CrateHeight = .75f, OpenAngle = -105f;
    private static readonly ConditionalWeakTable<BoxStationLayout, Definitions> LayoutDefinitions = new();

    private sealed class Definitions
    {
        internal required ImmutableArray<BoxStationStorageDefinition> Storage { get; init; }
    }

    internal static ImmutableArray<BoxStationStorageDefinition> GetDefinitions(BoxStationLayout layout)
        => LayoutDefinitions.GetValue(layout, BuildDefinitions).Storage;

    internal static bool TryDefinition(BoxStationLayout layout, int id, out BoxStationStorageDefinition definition)
    {
        var definitions = GetDefinitions(layout);
        definition = (uint)id < (uint)definitions.Length ? definitions[id] : default;
        return (uint)id < (uint)definitions.Length;
    }

    internal static int Capacity(BoxStationStorageDefinition definition) => definition.Kind == BoxStationStorageKind.Closet ? 6 : 2;
    internal static bool IsOpen(BoxStationLayout layout, BoxStationMaterialState materials, int storage)
        => materials.LayoutHash == layout.LayoutHash && !materials.StorageOpen.IsDefault
            && materials.StorageOpen.Length == GetDefinitions(layout).Length
            && (uint)storage < (uint)materials.StorageOpen.Length && materials.StorageOpen[storage];

    internal static bool ValidMembership(BoxStationLayout layout, int storage, byte slot, Vector3 position)
        => TryDefinition(layout, storage, out var definition) && slot < Capacity(definition)
            && position == Position(layout, definition);

    internal static float Height(BoxStationStorageDefinition definition) => definition.Kind == BoxStationStorageKind.Closet ? ClosetHeight : CrateHeight;
    internal static Vector3 HandlePosition(BoxStationStorageDefinition definition)
        => definition.Kind == BoxStationStorageKind.Closet ? new(.48f, 1.12f, .475f) : new(0, .70f, .475f);
    internal static Vector3 HingePosition(BoxStationStorageDefinition definition)
        => definition.Kind == BoxStationStorageKind.Closet ? new(-.65f, 0, HalfDepth) : new(0, CrateHeight, -HalfDepth);

    internal static Vector3 SlotPosition(BoxStationStorageDefinition definition, int slot)
    {
        if ((uint)slot >= (uint)Capacity(definition)) throw new ArgumentOutOfRangeException(nameof(slot));
        return new((slot & 1) == 0 ? -.335f : .335f, .10f + slot / 2 * .67f, 0);
    }

    internal static Vector3 Position(BoxStationLayout layout, BoxStationStorageDefinition definition)
    {
        var cell = layout.Cells[definition.Cell];
        return new((cell.X - layout.Width / 2) * BoxStationLayout.TileSize, 0,
            (layout.Height / 2 - cell.Row) * BoxStationLayout.TileSize);
    }

    private static Definitions BuildDefinitions(BoxStationLayout layout)
    {
        const string closet = "/obj/structure/closet", crate = closet + "/crate";
        var occupied = new bool[layout.Cells.Length];
        var counts = new int[layout.Cells.Length];
        foreach (var source in layout.Objects)
        {
            string type = layout.Types[source.Type];
            if (Family(type, closet)) counts[source.Cell]++;
            if (Family(type, "/obj/item") || Family(type, "/mob") || Family(type, "/obj/effect/spawner")) occupied[source.Cell] = true;
        }
        var definitions = ImmutableArray.CreateBuilder<BoxStationStorageDefinition>();
        foreach (var source in layout.Objects)
        {
            string type = layout.Types[source.Type];
            if (type is not (closet or crate) || occupied[source.Cell] || counts[source.Cell] != 1) continue;
            var kind = type == closet ? BoxStationStorageKind.Closet : BoxStationStorageKind.Crate;
            string name = SourceName(source, type, kind == BoxStationStorageKind.Closet ? "Closet" : "Crate");
            int direction = source.Direction;
            if (direction == 0)
                direction = ClearNeighbor(layout, source.Cell, 2) ? 2 : ClearNeighbor(layout, source.Cell, 1) ? 1
                    : ClearNeighbor(layout, source.Cell, 4) ? 4 : ClearNeighbor(layout, source.Cell, 8) ? 8 : 0;
            var definition = new BoxStationStorageDefinition(definitions.Count, source.Id, source.Cell, direction, kind, name);
            if (direction is not (1 or 2 or 4 or 8) || !BoxStationHullRepair.ValidDropPosition(layout, Position(layout, definition)))
                throw new InvalidDataException($"Station storage {source.Id} has no supported floor position or facing.");
            definitions.Add(definition);
        }
        return new() { Storage = definitions.ToImmutable() };
    }

    private static bool Family(string type, string parent) => type == parent || type.StartsWith(parent + "/", StringComparison.Ordinal);

    private static bool ClearNeighbor(BoxStationLayout layout, int cell, int direction)
    {
        var source = layout.Cells[cell];
        int next = direction switch
        {
            1 when source.Row > 0 => cell - layout.Width,
            2 when source.Row + 1 < layout.Height => cell + layout.Width,
            4 when source.X + 1 < layout.Width => cell + 1,
            8 when source.X > 0 => cell - 1,
            _ => -1
        };
        if (next < 0) return false;
        var neighbor = layout.Cells[next];
        if ((neighbor.Flags & (BoxStationCellFlags.Floor | BoxStationCellFlags.Catwalk)) == 0
            || (neighbor.Flags & (BoxStationCellFlags.Wall | BoxStationCellFlags.FullWindow | BoxStationCellFlags.Grille)) != 0) return false;
        var barrier = direction switch
        {
            1 => source.North | neighbor.South,
            2 => source.South | neighbor.North,
            4 => source.East | neighbor.West,
            _ => source.West | neighbor.East
        };
        return barrier == BoxStationBarrier.None;
    }

    private static string SourceName(BoxStationObject source, string type, string name)
    {
        string raw = source.Raw.Trim();
        if (raw == type) return name;
        if (!raw.StartsWith(type + "{", StringComparison.Ordinal) || !raw.EndsWith('}'))
            throw new InvalidDataException($"Station storage {source.Id} has an unsupported source override.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (string setting in raw.Substring(type.Length + 1, raw.Length - type.Length - 2).Split(';'))
        {
            string[] pair = setting.Split('=');
            if (pair.Length != 2 || !names.Add(pair[0].Trim()))
                throw new InvalidDataException($"Station storage {source.Id} has an invalid source setting.");
            string key = pair[0].Trim(), value = pair[1].Trim();
            if (key == "name" && value.Length is > 2 and <= 130 && value[0] == '"' && value[^1] == '"'
                && !value.AsSpan(1, value.Length - 2).Contains('"') && !value.Contains('\\')) name = value.Substring(1, value.Length - 2);
            else if (key != "dir" || !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int direction)
                || direction != source.Direction)
                throw new InvalidDataException($"Station storage {source.Id} has an unsupported source setting.");
        }
        return name;
    }
}
