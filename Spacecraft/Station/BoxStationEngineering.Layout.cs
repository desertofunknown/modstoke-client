// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using Vector3 = System.Numerics.Vector3;

namespace Modstoke.Client;

internal readonly record struct BoxStationFuelTank(int Object, int Cell, int Capacity);

internal sealed partial class BoxStationEngineering
{
    private sealed class Definitions
    {
        internal required ImmutableArray<BoxStationToolSource> Sources { get; init; }
        internal required ImmutableArray<BoxStationFuelTank> FuelTanks { get; init; }
        internal required ImmutableArray<BoxStationStructure> Structures { get; init; }
        internal required int[] Indices { get; init; }
    }

    internal static ImmutableArray<BoxStationToolSource> GetSources(BoxStationLayout layout)
        => LayoutDefinitions.GetValue(layout, BuildDefinitions).Sources;

    internal static ImmutableArray<BoxStationFuelTank> GetFuelTanks(BoxStationLayout layout)
        => LayoutDefinitions.GetValue(layout, BuildDefinitions).FuelTanks;

    private static Definitions BuildDefinitions(BoxStationLayout layout)
    {
        const string fuelTankType = "/obj/structure/reagent_dispensers/fueltank";
        var sources = ImmutableArray.CreateBuilder<BoxStationToolSource>();
        var fuelTanks = ImmutableArray.CreateBuilder<BoxStationFuelTank>();
        foreach (var source in layout.Objects)
        {
            string type = layout.Types[source.Type];
            if (type == fuelTankType || type.StartsWith(fuelTankType + "/", StringComparison.Ordinal))
            {
                if (type != fuelTankType || source.Raw.Trim() != type || fuelTanks.Count >= short.MaxValue)
                    throw new InvalidDataException($"Station fuel tank {source.Id} has an unsupported subtype, source override or identity.");
                fuelTanks.Add(new(source.Id, source.Cell, 1000));
                continue;
            }
            BoxStationToolKind? kind = type switch
            {
                "/obj/item/screwdriver" => BoxStationToolKind.Screwdriver,
                "/obj/item/crowbar" or "/obj/item/crowbar/large" => BoxStationToolKind.Crowbar,
                "/obj/item/wirecutters" => BoxStationToolKind.Wirecutters,
                "/obj/item/wrench" or "/obj/item/wrench/medical" => BoxStationToolKind.Wrench,
                "/obj/item/weldingtool" => BoxStationToolKind.Welder,
                _ => null
            };
            if (!kind.HasValue) continue;
            var cell = layout.Cells[source.Cell];
            var position = new Vector3((cell.X - layout.Width / 2) * BoxStationLayout.TileSize, 0,
                (layout.Height / 2 - cell.Row) * BoxStationLayout.TileSize) + SourceOffset(source, type);
            sources.Add(new(source.Id, source.Cell, position, kind.Value,
                type == "/obj/item/crowbar/large" ? .7f : 1, kind == BoxStationToolKind.Welder ? (byte)20 : (byte)0));
        }
        // Source sprites can share a tile; separate their floor models without changing inventory identities.
        for (int i = 0; i < sources.Count; i++)
        {
            int before = 0, count = 0;
            for (int j = 0; j < sources.Count; j++)
                if (sources[j].Cell == sources[i].Cell) { count++; if (j < i) before++; }
            var position = sources[i].Position;
            position.X += (before - (count - 1) * .5f) * .5f;
            sources[i] = sources[i] with { Position = position };
        }
        var structures = BoxStationHull.GetStructures(layout);
        var indices = new int[layout.Objects.Length * 2];
        Array.Fill(indices, -1);
        for (int i = 0; i < structures.Length; i++) indices[structures[i].Id] = i;
        return new() { Sources = sources.ToImmutable(), FuelTanks = fuelTanks.ToImmutable(), Structures = structures, Indices = indices };
    }

    private static Vector3 SourceOffset(BoxStationObject source, string type)
    {
        string raw = source.Raw.Trim();
        var offset = Vector3.Zero;
        if (raw == type) return offset;
        if (!raw.StartsWith(type + "{", StringComparison.Ordinal) || !raw.EndsWith('}'))
            throw new InvalidDataException($"Station tool {source.Id} has an unsupported source override.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (string setting in raw.Substring(type.Length + 1, raw.Length - type.Length - 2).Split(';'))
        {
            string[] pair = setting.Split('=');
            if (pair.Length != 2)
                throw new InvalidDataException($"Station tool {source.Id} has an invalid source setting.");
            string name = pair[0].Trim();
            if (name is not ("pixel_x" or "pixel_y" or "dir") || !names.Add(name)
                || !int.TryParse(pair[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                || (name == "dir" ? value != source.Direction : value is < -32 or > 32))
                throw new InvalidDataException($"Station tool {source.Id} has an unsupported source setting.");
            if (name == "pixel_x") offset.X = value * (BoxStationLayout.TileSize / 32);
            else if (name == "pixel_y") offset.Z = value * (BoxStationLayout.TileSize / 32);
        }
        // Map furniture offsets become floor positions here; retain space for the tool's footprint inside its tile.
        return new(Math.Clamp(offset.X, -.8f, .8f), 0, Math.Clamp(offset.Z, -.8f, .8f));
    }

    private static bool Reinforced(BoxStationLayout layout, BoxStationStructure structure)
        => structure.Kind == BoxStationStructureKind.Window
            && BoxStationHullRepair.StructureMaterial(layout, structure) is BoxStationMaterial.ReinforcedGlass or BoxStationMaterial.ReinforcedPlasmaGlass;

    private static BoxStationMaintenance InitialMaintenance(BoxStationLayout layout, BoxStationStructure structure)
        => new(structure.Id, Reinforced(layout, structure) ? (byte)8 : (byte)0, true, 0, structure.InitialIntegrity);

    // Repair times are gameplay tuning for normalized wall integrity.
    internal static float WallRepairDuration(BoxStationWallKind kind)
        => kind switch { BoxStationWallKind.Steel => 10, BoxStationWallKind.Reinforced => 20, _ => 0 };

    internal static bool ValidIntent(BoxStationLayout layout, BoxStationWorkIntent intent)
    {
        if (intent.Mode is not (BoxStationWorkMode.Repair or BoxStationWorkMode.Dismantle)
            || !LocalPosition(layout, intent.Contact)) return false;
        if (intent.Kind == BoxStationWorkTarget.Construction)
            return intent.Tool == -1 && intent.Mode == BoxStationWorkMode.Repair
                && BoxStationHull.TryStructure(layout, intent.Target, out _);
        if (intent.Kind == BoxStationWorkTarget.WallConstruction)
            return intent.Tool == -1 && intent.Mode == BoxStationWorkMode.Repair
                && BoxStationHullRepair.TryConstruction(layout, intent.Target, out _);
        if (intent.Kind == BoxStationWorkTarget.DoorConstruction)
            return BoxStationDoors.ValidEdge(layout, BoxStationDoors.EdgeFromTarget(layout, intent.Target))
                && (intent.Tool == -1 ? intent.Mode == BoxStationWorkMode.Repair
                    : intent.Tool >= 0 && intent.Tool < GetSources(layout).Length);
        if (intent.Tool < 0 || intent.Tool >= GetSources(layout).Length) return false;
        if (intent.Kind == BoxStationWorkTarget.Wall)
            return BoxStationHullRepair.TryConstruction(layout, intent.Target, out _);
        return intent.Kind == BoxStationWorkTarget.Bundle ? intent.Target > 0
            : intent.Kind == BoxStationWorkTarget.Structure && BoxStationHull.TryStructure(layout, intent.Target, out _);
    }

    private static bool MaterialWork(BoxStationWorkIntent intent)
        => intent.Kind is BoxStationWorkTarget.Construction or BoxStationWorkTarget.WallConstruction
            || intent.Kind == BoxStationWorkTarget.DoorConstruction && intent.Tool == -1;

    private static bool LocalPosition(BoxStationLayout layout, Vector3 position)
        => float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z)
            && MathF.Abs(position.X) <= (layout.Width / 2 + 1) * BoxStationLayout.TileSize
            && MathF.Abs(position.Z) <= (layout.Height / 2 + 1) * BoxStationLayout.TileSize
            && position.Y is >= 0 and <= BoxStationLayout.DeckHeight;

    private static bool ValidFeet(BoxStationLayout layout, Vector3 feet)
    {
        if (!float.IsFinite(feet.X) || !float.IsFinite(feet.Y) || !float.IsFinite(feet.Z)
            || feet.Y is < -.05f or > .7f) return false;
        double x = Math.Floor((double)feet.X / BoxStationLayout.TileSize + layout.Width / 2 + .5);
        double row = Math.Floor(layout.Height / 2 - (double)feet.Z / BoxStationLayout.TileSize + .5);
        if (x < 0 || x >= layout.Width || row < 0 || row >= layout.Height) return false;
        // Breached walls and removed floor-backed structures can support work. The authority's
        // ContainsWalkArea check resolves current hull apertures, doors and capsule clearance.
        return (layout.Cells[(int)row * layout.Width + (int)x].Flags
            & (BoxStationCellFlags.Floor | BoxStationCellFlags.Catwalk | BoxStationCellFlags.Wall)) != 0;
    }
}
