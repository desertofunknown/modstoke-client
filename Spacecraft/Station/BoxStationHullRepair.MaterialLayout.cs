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

internal sealed partial class BoxStationHullRepair
{
    private static readonly ConditionalWeakTable<BoxStationLayout, MaterialDefinitions> LayoutMaterials = new();

    private sealed class MaterialDefinitions
    {
        internal required ImmutableArray<BoxStationMaterialSource> Sources { get; init; }
        internal required ImmutableArray<BoxStationStructure> Structures { get; init; }
        internal required int[] Indices { get; init; }
        internal required BoxStationMaterialBudget Budget { get; init; }
        internal required int LegacySourceCount { get; init; }
        internal required int ConstructionSourceCount { get; init; }
        internal required int CableSourceCount { get; init; }
        internal required int CableCount { get; init; }
        internal required int CableSiteCount { get; init; }
        internal required int CableBudget { get; init; }
        internal required int DoorElectronicsBudget { get; init; }
    }

    internal static ImmutableArray<BoxStationMaterialSource> GetMaterialSources(BoxStationLayout layout)
        => LayoutMaterials.GetValue(layout, BuildMaterialDefinitions).Sources;

    internal static BoxStationMaterialBudget GetMaterialBudget(BoxStationLayout layout)
        => LayoutMaterials.GetValue(layout, BuildMaterialDefinitions).Budget;

    internal static BoxStationMaterial StructureMaterial(BoxStationLayout layout, BoxStationStructure structure)
    {
        if (structure.Kind == BoxStationStructureKind.Grille) return BoxStationMaterial.Rods;
        bool reinforced = layout.Types[layout.Objects[structure.SourceObject].Type].Contains("/reinforced", StringComparison.Ordinal);
        return structure.Plasma
            ? reinforced ? BoxStationMaterial.ReinforcedPlasmaGlass : BoxStationMaterial.PlasmaGlass
            : reinforced ? BoxStationMaterial.ReinforcedGlass : BoxStationMaterial.Glass;
    }

    internal static string MaterialName(BoxStationMaterial material) => material switch
    {
        BoxStationMaterial.Rods => "Metal rods",
        BoxStationMaterial.Glass => "Glass sheets",
        BoxStationMaterial.ReinforcedGlass => "Reinforced glass sheets",
        BoxStationMaterial.PlasmaGlass => "Plasma glass sheets",
        BoxStationMaterial.ReinforcedPlasmaGlass => "Reinforced plasma glass sheets",
        BoxStationMaterial.GlassShards => "Glass shards",
        BoxStationMaterial.PlasmaGlassShards => "Plasma glass shards",
        BoxStationMaterial.Steel => "Steel sheets",
        BoxStationMaterial.Plasteel => "Plasteel sheets",
        BoxStationMaterial.FloorTiles => "Floor tiles",
        BoxStationMaterial.Cable => "Cable coil",
        BoxStationMaterial.DoorElectronics => "Airlock electronics",
        BoxStationMaterial.LineSpare => "Line repair kits",
        BoxStationMaterial.CircuitSpare => "Circuit repair kits",
        _ => "Unknown material"
    };

    internal static int MaterialCost(BoxStationLayout layout, int target, byte currentIntegrity)
    {
        if (!BoxStationHull.TryStructure(layout, target, out var structure) || currentIntegrity >= BoxStationHull.Capacity) return 0;
        if (structure.Kind == BoxStationStructureKind.Grille)
            return currentIntegrity == 0 ? 2 : currentIntegrity <= BoxStationHull.BrokenGrilleIntegrity ? 1 : 0;
        return currentIntegrity == 0 ? structure.Direction == 5 ? 2 : 1 : 0;
    }

    private static BoxStationMaterialBudget MaterialUnits(BoxStationMaterial material, int count) => material switch
    {
        BoxStationMaterial.Rods => new(0, 0, count),
        BoxStationMaterial.Glass or BoxStationMaterial.GlassShards => new(count, 0, 0),
        BoxStationMaterial.ReinforcedGlass => new(count, 0, count),
        BoxStationMaterial.PlasmaGlass or BoxStationMaterial.PlasmaGlassShards => new(0, count, 0),
        BoxStationMaterial.ReinforcedPlasmaGlass => new(0, count, count),
        _ => default
    };

    private static int StructureUnits(BoxStationStructure structure, byte integrity)
        => integrity == 0 ? 0 : structure.Kind == BoxStationStructureKind.Grille
            ? integrity > BoxStationHull.BrokenGrilleIntegrity ? 2 : 1 : structure.Direction == 5 ? 2 : 1;

    private static BoxStationMaterialBudget AddMaterialUnits(BoxStationMaterialBudget a, BoxStationMaterialBudget b)
        => new(checked(a.Glass + b.Glass), checked(a.PlasmaGlass + b.PlasmaGlass), checked(a.Rods + b.Rods));

    private static MaterialDefinitions BuildMaterialDefinitions(BoxStationLayout layout)
    {
        const string rods = "/obj/item/stack/rods", sheet = "/obj/item/stack/sheet/";
        var sources = new List<BoxStationMaterialSource>();
        foreach (var source in layout.Objects)
        {
            string type = layout.Types[source.Type];
            string family = type == rods || type.StartsWith(rods + "/", StringComparison.Ordinal) ? rods
                : type.StartsWith(sheet, StringComparison.Ordinal) ? type.Substring(sheet.Length).Split('/')[0] : "";
            BoxStationMaterial material;
            switch (family)
            {
                case rods: material = BoxStationMaterial.Rods; break;
                case "glass": material = BoxStationMaterial.Glass; family = sheet + family; break;
                case "rglass": material = BoxStationMaterial.ReinforcedGlass; family = sheet + family; break;
                case "plasmaglass": material = BoxStationMaterial.PlasmaGlass; family = sheet + family; break;
                case "plasmarglass": material = BoxStationMaterial.ReinforcedPlasmaGlass; family = sheet + family; break;
                case "plasteel": material = BoxStationMaterial.Plasteel; family = sheet + family; break;
                default: continue;
            }
            ushort count = type == family ? (ushort)1 : type.Substring(family.Length) switch
            {
                "/fifty" => (ushort)50,
                "/ten" when material is BoxStationMaterial.Rods or BoxStationMaterial.Plasteel => (ushort)10,
                "/twentyfive" when material == BoxStationMaterial.Rods => (ushort)25,
                _ => throw new InvalidDataException($"Station material source {source.Id} has an unsupported material type.")
            };
            var offset = MaterialSourceOffset(source, type, ref count);
            var cell = layout.Cells[source.Cell];
            var position = new Vector3((cell.X - layout.Width / 2) * BoxStationLayout.TileSize, 0,
                (layout.Height / 2 - cell.Row) * BoxStationLayout.TileSize) + offset;
            sources.Add(new(source.Id, source.Cell, position, material, count));
        }
        sources.Sort(static (a, b) => a.Material == BoxStationMaterial.Plasteel && b.Material != BoxStationMaterial.Plasteel ? 1
            : a.Material != BoxStationMaterial.Plasteel && b.Material == BoxStationMaterial.Plasteel ? -1 : a.Object.CompareTo(b.Object));
        int legacySourceCount = sources.FindIndex(static source => source.Material == BoxStationMaterial.Plasteel);
        if (legacySourceCount < 0) legacySourceCount = sources.Count;
        var budget = new BoxStationMaterialBudget();
        for (int i = 0; i < sources.Count; i++)
        {
            int before = 0, count = 0;
            for (int j = 0; j < sources.Count; j++)
                if (sources[j].Cell == sources[i].Cell) { count++; if (j < i) before++; }
            var position = sources[i].Position;
            position.X += (before - (count - 1) * .5f) * .5f;
            position.Z += .4f;
            var cell = layout.Cells[sources[i].Cell];
            float centerX = (cell.X - layout.Width / 2) * BoxStationLayout.TileSize;
            float centerZ = (layout.Height / 2 - cell.Row) * BoxStationLayout.TileSize;
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Z)
                || MathF.Abs(position.X - centerX) >= BoxStationLayout.TileSize * .5f
                || MathF.Abs(position.Z - centerZ) >= BoxStationLayout.TileSize * .5f)
                throw new InvalidDataException($"Station material source {sources[i].Object} lies outside its original cell.");
            // Original loose stocks in space retain their source anchors; they do not create flooring.
            if ((cell.Flags & BoxStationCellFlags.Floor) != 0 && !ValidDropPosition(layout, position))
                throw new InvalidDataException($"Station material source {sources[i].Object} has an invalid floor position.");
            sources[i] = sources[i] with { Position = position };
            budget = AddMaterialUnits(budget, MaterialUnits(sources[i].Material, sources[i].Count));
        }
        int constructionSourceCount = sources.Count;
        AppendCableSources(layout, sources);
        int cableCount = BoxStationPower.GetCables(layout).Length;
        int cableBudget = cableCount;
        for (int i = constructionSourceCount; i < sources.Count; i++) cableBudget = checked(cableBudget + sources[i].Count);
        int cableSourceCount = sources.Count;
        AppendDoorElectronicsSources(layout, sources);
        int doorElectronicsBudget = sources.Count - cableSourceCount;
        var structures = new List<BoxStationStructure>(BoxStationHull.GetStructures(layout));
        structures.Sort(static (a, b) => a.Id.CompareTo(b.Id));
        var indices = new int[layout.Objects.Length * 2];
        Array.Fill(indices, -1);
        for (int i = 0; i < structures.Count; i++)
        {
            var structure = structures[i];
            indices[structure.Id] = i;
            budget = AddMaterialUnits(budget, MaterialUnits(StructureMaterial(layout, structure), StructureUnits(structure, structure.InitialIntegrity)));
        }
        return new() { Sources = ImmutableArray.CreateRange(sources), Structures = ImmutableArray.CreateRange(structures),
            Indices = indices, Budget = budget, LegacySourceCount = legacySourceCount,
            ConstructionSourceCount = constructionSourceCount, CableSourceCount = cableSourceCount, CableCount = cableCount,
            CableSiteCount = BoxStationPower.GetCableSites(layout).Length, CableBudget = cableBudget,
            DoorElectronicsBudget = doorElectronicsBudget };
    }

    private static void AppendCableSources(BoxStationLayout layout, List<BoxStationMaterialSource> sources)
    {
        const string cable = "/obj/item/stack/cable_coil";
        int start = sources.Count;
        foreach (var source in layout.Objects)
        {
            string type = layout.Types[source.Type];
            if (type != cable && !type.StartsWith(cable + "/", StringComparison.Ordinal)) continue;
            ushort count = type == cable ? (ushort)MaxCableBundleCount : type == cable + "/five" ? (ushort)5
                : throw new InvalidDataException($"Station cable source {source.Id} has an unsupported coil type.");
            var offset = MaterialSourceOffset(source, type, ref count, MaxCableBundleCount);
            var cell = layout.Cells[source.Cell];
            var position = new Vector3((cell.X - layout.Width / 2) * BoxStationLayout.TileSize, 0,
                (layout.Height / 2 - cell.Row) * BoxStationLayout.TileSize) + offset;
            sources.Add(new(source.Id, source.Cell, position, BoxStationMaterial.Cable, count));
        }
        for (int i = start; i < sources.Count; i++)
        {
            int before = 0, count = 0;
            for (int j = start; j < sources.Count; j++)
                if (sources[j].Cell == sources[i].Cell) { count++; if (j < i) before++; }
            var position = sources[i].Position;
            position.X += (before - (count - 1) * .5f) * .5f;
            position.Z -= .4f;
            var cell = layout.Cells[sources[i].Cell];
            float centerX = (cell.X - layout.Width / 2) * BoxStationLayout.TileSize;
            float centerZ = (layout.Height / 2 - cell.Row) * BoxStationLayout.TileSize;
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Z)
                || MathF.Abs(position.X - centerX) >= BoxStationLayout.TileSize * .5f
                || MathF.Abs(position.Z - centerZ) >= BoxStationLayout.TileSize * .5f
                || (cell.Flags & BoxStationCellFlags.Floor) != 0 && !ValidDropPosition(layout, position))
                throw new InvalidDataException($"Station cable source {sources[i].Object} has an invalid source position.");
            sources[i] = sources[i] with { Position = position };
        }
    }

    private static void AppendDoorElectronicsSources(BoxStationLayout layout, List<BoxStationMaterialSource> sources)
    {
        const string electronics = "/obj/item/electronics/airlock";
        int start = sources.Count;
        foreach (var source in layout.Objects)
        {
            string type = layout.Types[source.Type];
            if (type != electronics) continue;
            ushort count = 1;
            var offset = MaterialSourceOffset(source, type, ref count, 1);
            var cell = layout.Cells[source.Cell];
            var position = new Vector3((cell.X - layout.Width / 2) * BoxStationLayout.TileSize, 0,
                (layout.Height / 2 - cell.Row) * BoxStationLayout.TileSize) + offset;
            sources.Add(new(source.Id, source.Cell, position, BoxStationMaterial.DoorElectronics, count));
        }
        for (int i = start; i < sources.Count; i++)
        {
            int before = 0, count = 0;
            for (int j = start; j < sources.Count; j++)
                if (sources[j].Cell == sources[i].Cell) { count++; if (j < i) before++; }
            var position = sources[i].Position;
            position.X += (before - (count - 1) * .5f) * .35f;
            position.Z += .7f;
            var cell = layout.Cells[sources[i].Cell];
            float centerX = (cell.X - layout.Width / 2) * BoxStationLayout.TileSize;
            float centerZ = (layout.Height / 2 - cell.Row) * BoxStationLayout.TileSize;
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Z)
                || MathF.Abs(position.X - centerX) >= BoxStationLayout.TileSize * .5f
                || MathF.Abs(position.Z - centerZ) >= BoxStationLayout.TileSize * .5f
                || (cell.Flags & BoxStationCellFlags.Floor) != 0 && !ValidDropPosition(layout, position))
                throw new InvalidDataException($"Station door electronics source {sources[i].Object} has an invalid source position.");
            sources[i] = sources[i] with { Position = position };
        }
    }

    private static Vector3 MaterialSourceOffset(BoxStationObject source, string type, ref ushort count, int maximum = MaxMaterialBundleCount)
    {
        string raw = source.Raw.Trim();
        var offset = Vector3.Zero;
        if (raw == type) return offset;
        if (!raw.StartsWith(type + "{", StringComparison.Ordinal) || !raw.EndsWith('}'))
            throw new InvalidDataException($"Station material source {source.Id} has an unsupported source override.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (string setting in raw.Substring(type.Length + 1, raw.Length - type.Length - 2).Split(';'))
        {
            string[] pair = setting.Split('=');
            if (pair.Length != 2) throw new InvalidDataException($"Station material source {source.Id} has an invalid source setting.");
            string name = pair[0].Trim();
            if (name is not ("pixel_x" or "pixel_y" or "dir" or "amount") || !names.Add(name)
                || !int.TryParse(pair[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                || (name == "amount" ? value < 1 || value > maximum
                    : name == "dir" ? value != source.Direction : value is < -16 or > 16))
                throw new InvalidDataException($"Station material source {source.Id} has an unsupported source setting.");
            if (name == "pixel_x") offset.X = value * (BoxStationLayout.TileSize / 32);
            else if (name == "pixel_y") offset.Z = value * (BoxStationLayout.TileSize / 32);
            else if (name == "amount") count = (ushort)value;
        }
        return offset;
    }
}
