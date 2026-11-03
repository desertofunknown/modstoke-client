// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using UnityEngine;

namespace Modstoke.Client;

[Flags]
internal enum BoxStationCellFlags
{
    None = 0,
    Floor = 1,
    Wall = 2,
    FullWindow = 4,
    Grille = 8,
    Roof = 16,
    Catwalk = 32,
    Lattice = 64
}

[Flags]
internal enum BoxStationBarrier
{
    None = 0,
    Wall = 1,
    Window = 2,
    DoorControlled = 4,
    Grille = 8,
    GasOnly = 16
}

internal readonly record struct BoxStationCell(int Id, int X, int Row, int TurfType, int Area,
    BoxStationCellFlags Flags, int Zone, BoxStationBarrier North, BoxStationBarrier East,
    BoxStationBarrier South, BoxStationBarrier West);

internal readonly record struct BoxStationPort(int Direction, int Zone, int Cell);

internal readonly record struct BoxStationDoor(int Id, int Cell, string Kind, int Direction,
    bool Edge, bool InitiallyOpen, bool Transparent, int Zone, int ZoneA, int ZoneB,
    ImmutableArray<BoxStationPort> Ports, int Group, int SourceType, int SourceObject, string Name);

internal readonly record struct BoxStationCompartment(int Id, double Volume, bool Exterior,
    int CellCount, ImmutableArray<int> Areas, string Name);

internal readonly record struct BoxStationObject(int Id, int Cell, int Type, int Direction, string Raw);

internal readonly record struct BoxStationDoorGroup(int Id, ImmutableArray<int> Doors);

internal readonly record struct BoxStationInitialGas(int Cell, int Mix);

internal sealed class BoxStationLayout
{
    internal const float TileSize = 2.4f, DeckHeight = 3.2f;
    internal const float DoorWidth = 2.08f, DoorHeight = 2.65f;
    private const int GridSize = 255, Center = 127;
    private static readonly Lazy<BoxStationLayout> Instance = new(Load);
    internal static BoxStationLayout Current => Instance.Value;

    internal int Width => GridSize;
    internal int Height => GridSize;
    internal string SourceCommit { get; }
    internal string SourceSha256 { get; }
    internal string LayoutHash { get; }
    internal ImmutableArray<string> Types { get; }
    internal ImmutableArray<string> Areas { get; }
    internal ImmutableArray<BoxStationCell> Cells { get; }
    internal ImmutableArray<BoxStationDoor> Doors { get; }
    internal ImmutableArray<BoxStationCompartment> Compartments { get; }
    internal ImmutableArray<BoxStationObject> Objects { get; }
    internal ImmutableArray<BoxStationDoorGroup> Groups { get; }
    internal ImmutableArray<string> GasMixes { get; }
    internal ImmutableArray<BoxStationInitialGas> InitialGas { get; }
    internal int ExteriorFloorCells { get; }
    // Source declarations and future utility metadata remain inert JSON, never reflected types or code.
    internal JsonElement TurfOverrides { get; }
    internal JsonElement AreaOverrides { get; }
    internal JsonElement SourceInitialization { get; }
    internal JsonElement CycleLinks { get; }

    private BoxStationLayout(JsonElement root)
    {
        if (Integer(root, "version") != 1 || Integer(root, "width") != GridSize
            || Integer(root, "height") != GridSize || Number(root, "tileSize") != 2.4
            || Number(root, "deckHeight") != 3.2)
            throw Invalid("unsupported version, dimensions or units");
        SourceCommit = Hex(root, "sourceCommit", 40);
        SourceSha256 = Hex(root, "sourceSha256", 64);
        LayoutHash = Hex(root, "layoutHash", 64);
        ValidateHash(root);
        Types = Strings(root, "types", 65_536);
        Areas = Strings(root, "areas", 65_536);
        GasMixes = Strings(root, "gasMixes", 65_536);

        var compartments = RequiredArray(root, "compartments", 1, GridSize * GridSize);
        var cells = RequiredArray(root, "cells", GridSize * GridSize, GridSize * GridSize);
        var cellBuilder = ImmutableArray.CreateBuilder<BoxStationCell>(cells.GetArrayLength());
        var zoneCounts = new int[compartments.GetArrayLength()];
        int cellId = 0, floorCount = 0, exteriorFloorCount = 0;
        foreach (var cell in cells.EnumerateArray())
        {
            if (cell.ValueKind != JsonValueKind.Array || cell.GetArrayLength() != 8)
                throw Invalid($"cell {cellId} must contain eight integers");
            int turf = Index(cell[0], Types.Length, "cell turf"), area = Index(cell[1], Areas.Length, "cell area");
            int flags = ReadInteger(cell[2], "cell flags"), zone = Zone(cell[3], zoneCounts.Length);
            if ((flags & ~127) != 0 || !Types[turf].StartsWith("/turf/", StringComparison.Ordinal))
                throw Invalid($"cell {cellId} has invalid flags or turf");
            if (zone >= 0) zoneCounts[zone]++;
            if ((flags & (int)BoxStationCellFlags.Floor) != 0)
            {
                floorCount++;
                if (zone == -1 && (flags & (int)(BoxStationCellFlags.Wall | BoxStationCellFlags.FullWindow)) == 0)
                    exteriorFloorCount++;
            }
            cellBuilder.Add(new(cellId, cellId % GridSize, cellId / GridSize, turf, area,
                (BoxStationCellFlags)flags, zone, Barrier(cell[4]), Barrier(cell[5]), Barrier(cell[6]), Barrier(cell[7])));
            cellId++;
        }
        Cells = cellBuilder.MoveToImmutable();
        ExteriorFloorCells = Integer(root, "exteriorFloorCells");
        if (ExteriorFloorCells != exteriorFloorCount) throw Invalid("exterior floor count does not match the grid");
        var initialGas = RequiredArray(root, "initialGas", floorCount, floorCount);
        var gasBuilder = ImmutableArray.CreateBuilder<BoxStationInitialGas>(floorCount);
        var gasCells = new HashSet<int>();
        foreach (var gas in initialGas.EnumerateArray())
        {
            if (gas.ValueKind != JsonValueKind.Array || gas.GetArrayLength() != 2)
                throw Invalid("initial gas entry must contain cell and mix indices");
            int index = Index(gas[0], Cells.Length, "initial gas cell");
            int mix = Index(gas[1], GasMixes.Length, "initial gas mix");
            if (!gasCells.Add(index) || (Cells[index].Flags & BoxStationCellFlags.Floor) == 0)
                throw Invalid("initial gas entry duplicates a cell or references a non-floor cell");
            gasBuilder.Add(new(index, mix));
        }
        InitialGas = gasBuilder.MoveToImmutable();

        var compartmentBuilder = ImmutableArray.CreateBuilder<BoxStationCompartment>(zoneCounts.Length);
        int zoneId = 0;
        foreach (var compartment in compartments.EnumerateArray())
        {
            int count = Integer(compartment, "cells");
            double volume = Number(compartment, "volume");
            bool exterior = Boolean(compartment, "exterior");
            if (Integer(compartment, "id") != zoneId || count <= 0 || count != zoneCounts[zoneId]
                || exterior || Math.Abs(volume - count * 2.4 * 2.4 * 3.2) > .001)
                throw Invalid($"compartment {zoneId} has invalid identity, extent or volume");
            var zoneAreas = Indices(compartment, "areas", Areas.Length, Areas.Length);
            if (zoneAreas.IsEmpty) throw Invalid($"compartment {zoneId} has no area");
            compartmentBuilder.Add(new(zoneId, volume, exterior, count, zoneAreas, Text(compartment, "name", 512)));
            zoneId++;
        }
        Compartments = compartmentBuilder.MoveToImmutable();

        var objects = RequiredArray(root, "objects", 0, 1_000_000);
        var objectBuilder = ImmutableArray.CreateBuilder<BoxStationObject>(objects.GetArrayLength());
        foreach (var item in objects.EnumerateArray())
        {
            int index = Index(Required(item, "cell"), Cells.Length, "object cell");
            int type = Index(Required(item, "type"), Types.Length, "object type");
            int direction = Direction(Required(item, "dir"), false);
            if (!Types[type].StartsWith("/obj/", StringComparison.Ordinal) && !Types[type].StartsWith("/mob/", StringComparison.Ordinal))
                throw Invalid($"object {objectBuilder.Count} has an invalid source type");
            objectBuilder.Add(new(objectBuilder.Count, index, type, direction, Text(item, "raw", 1_048_576)));
        }
        Objects = objectBuilder.MoveToImmutable();

        var doors = RequiredArray(root, "doors", 0, Cells.Length * 4);
        var groups = RequiredArray(root, "groups", 0, doors.GetArrayLength());
        var doorBuilder = ImmutableArray.CreateBuilder<BoxStationDoor>(doors.GetArrayLength());
        foreach (var door in doors.EnumerateArray())
        {
            int index = Index(Required(door, "cell"), Cells.Length, "door cell");
            int type = Index(Required(door, "sourceType"), Types.Length, "door type");
            int source = Index(Required(door, "sourceObject"), Objects.Length, "door source object");
            int group = Integer(door, "group");
            string kind = Text(door, "kind", 64);
            if (kind is not ("airlock" or "windoor" or "firedoor" or "poddoor" or "morgue" or "mineral"))
                throw Invalid($"door {doorBuilder.Count} has an unsupported kind");
            if (group < -1 || group >= groups.GetArrayLength() || Objects[source].Cell != index || Objects[source].Type != type)
                throw Invalid($"door {doorBuilder.Count} has invalid source or group");
            var ports = RequiredArray(door, "ports", 0, 4);
            var portBuilder = ImmutableArray.CreateBuilder<BoxStationPort>(ports.GetArrayLength());
            foreach (var port in ports.EnumerateArray())
            {
                int portCell = Index(Required(port, "cell"), Cells.Length, "door port cell");
                int portZone = Zone(Required(port, "zone"), Compartments.Length);
                if (portZone != Cells[portCell].Zone) throw Invalid("door port does not match its cell zone");
                portBuilder.Add(new(Direction(Required(port, "direction"), true), portZone, portCell));
            }
            int zone = Zone(Required(door, "zone"), Compartments.Length);
            if (zone != Cells[index].Zone) throw Invalid($"door {doorBuilder.Count} does not match its cell zone");
            doorBuilder.Add(new(doorBuilder.Count, index, kind,
                Direction(Required(door, "direction"), true), Boolean(door, "edge"),
                Boolean(door, "initiallyOpen"), Boolean(door, "transparent"),
                zone,
                Zone(Required(door, "zoneA"), Compartments.Length),
                Zone(Required(door, "zoneB"), Compartments.Length), portBuilder.MoveToImmutable(),
                group, type, source, Text(door, "name", 512)));
        }
        Doors = doorBuilder.MoveToImmutable();

        var groupBuilder = ImmutableArray.CreateBuilder<BoxStationDoorGroup>(groups.GetArrayLength());
        foreach (var group in groups.EnumerateArray())
        {
            int id = Integer(group, "id");
            if (id != groupBuilder.Count) throw Invalid("door group identities are not consecutive");
            var members = Indices(group, "doors", Doors.Length, Doors.Length);
            if (members.IsEmpty) throw Invalid($"door group {id} is empty");
            foreach (int door in members)
                if (Doors[door].Group != id) throw Invalid($"door group {id} has inconsistent membership");
            groupBuilder.Add(new(id, members));
        }
        Groups = groupBuilder.MoveToImmutable();
        foreach (var door in Doors)
            if (door.Group >= 0 && !Groups[door.Group].Doors.Contains(door.Id))
                throw Invalid($"door {door.Id} is missing from its group");
        ValidateTopology();
        ValidateOverrides(root, "turfOverrides");
        ValidateOverrides(root, "areaOverrides");
        foreach (var initialization in RequiredArray(root, "sourceInitialization", 0, Objects.Length).EnumerateArray())
        {
            Index(Required(initialization, "object"), Objects.Length, "initialization object");
            Text(initialization, "behavior", 512);
        }
        TurfOverrides = Required(root, "turfOverrides").Clone();
        AreaOverrides = Required(root, "areaOverrides").Clone();
        SourceInitialization = Required(root, "sourceInitialization").Clone();
        CycleLinks = RequiredArray(root, "cycleLinks", 0, Objects.Length).Clone();
    }

    internal BoxStationCell GetCell(int x, int row)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(x);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(x, GridSize);
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, GridSize);
        return Cells[row * GridSize + x];
    }

    internal BoxStationCell GetCell(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Cells.Length);
        return Cells[index];
    }

    // Cell centers occupy integer tile coordinates; the map boundary is half a tile beyond them.
    internal int CellAt(Vector3 localPosition)
    {
        if (!float.IsFinite(localPosition.x) || !float.IsFinite(localPosition.y) || !float.IsFinite(localPosition.z)) return -1;
        double x = Math.Floor((double)localPosition.x / TileSize + Center + .5);
        double row = Math.Floor(Center - (double)localPosition.z / TileSize + .5);
        if (x < 0 || x >= GridSize || row < 0 || row >= GridSize) return -1;
        return (int)row * GridSize + (int)x;
    }

    internal Vector3 CellPosition(int index, float height = 0)
    {
        var cell = GetCell(index);
        if (!float.IsFinite(height) || MathF.Abs(height) > 1_000_000) throw new ArgumentOutOfRangeException(nameof(height));
        return new((cell.X - Center) * TileSize, height, (Center - cell.Row) * TileSize);
    }

    private void ValidateTopology()
    {
        foreach (var cell in Cells)
        {
            if (cell.X + 1 < GridSize && cell.East != Cells[cell.Id + 1].West
                || cell.Row + 1 < GridSize && cell.South != Cells[cell.Id + GridSize].North)
                throw Invalid($"cell {cell.Id} has an asymmetric edge barrier");
            if (cell.Zone >= 0 && !Compartments[cell.Zone].Areas.Contains(cell.Area))
                throw Invalid($"cell {cell.Id} is missing from its compartment areas");
        }
    }

    private void ValidateOverrides(JsonElement root, string name)
    {
        foreach (var item in RequiredArray(root, name, 0, Cells.Length).EnumerateArray())
        {
            Index(Required(item, "cell"), Cells.Length, name);
            Text(item, "raw", 1_048_576);
        }
    }

    private static BoxStationLayout Load()
    {
        using var stream = typeof(BoxStationLayout).Assembly.GetManifestResourceStream("Modstoke.Client.Assets.box-layout.json")
            ?? throw Invalid("embedded resource is missing");
        if (stream.Length is <= 0 or > 134_217_728) throw Invalid("embedded resource size is invalid");
        try
        {
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 32 });
            return new(document.RootElement);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The Box Station layout contains invalid JSON.", exception);
        }
    }

    private static void ValidateHash(JsonElement root)
    {
        using var canonical = new MemoryStream();
        using (var writer = new Utf8JsonWriter(canonical)) WriteCanonical(writer, root, true);
        string actual = Convert.ToHexString(SHA256.HashData(canonical.GetBuffer().AsSpan(0, checked((int)canonical.Length))));
        if (!string.Equals(actual, Text(root, "layoutHash", 64), StringComparison.OrdinalIgnoreCase))
            throw Invalid("layout content hash does not match");
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value, bool root = false)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var properties = new List<JsonProperty>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Invalid($"duplicate property '{property.Name}'");
                if (!root || property.Name != "layoutHash") properties.Add(property);
            }
            properties.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
            writer.WriteStartObject();
            foreach (var property in properties)
            {
                writer.WritePropertyName(property.Name);
                WriteCanonical(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item);
            writer.WriteEndArray();
        }
        else
        {
            // Preserve the compiler's number spelling and UTF-8 string escapes in the hash contract.
            writer.WriteRawValue(value.GetRawText(), skipInputValidation: true);
        }
    }

    private static JsonElement Required(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var property))
            throw Invalid($"missing property '{name}'");
        return property;
    }

    private static JsonElement RequiredArray(JsonElement value, string name, int minimum, int maximum)
    {
        var array = Required(value, name);
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() < minimum || array.GetArrayLength() > maximum)
            throw Invalid($"'{name}' has an invalid array size");
        return array;
    }

    private static int ReadInteger(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int result))
            throw Invalid($"'{name}' must be an integer");
        return result;
    }

    private static int Integer(JsonElement value, string name) => ReadInteger(Required(value, name), name);

    private static double Number(JsonElement value, string name)
    {
        var number = Required(value, name);
        if (number.ValueKind != JsonValueKind.Number || !number.TryGetDouble(out double result) || !double.IsFinite(result))
            throw Invalid($"'{name}' must be a finite number");
        return result;
    }

    private static bool Boolean(JsonElement value, string name)
    {
        var boolean = Required(value, name);
        if (boolean.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid($"'{name}' must be a boolean");
        return boolean.GetBoolean();
    }

    private static string Text(JsonElement value, string name, int maximum)
    {
        var text = Required(value, name);
        if (text.ValueKind != JsonValueKind.String) throw Invalid($"'{name}' must be a string");
        string result = text.GetString()!;
        if (result.Length > maximum) throw Invalid($"'{name}' is too long");
        return result;
    }

    private static string Hex(JsonElement value, string name, int length)
    {
        string result = Text(value, name, length);
        if (result.Length != length) throw Invalid($"'{name}' has an invalid length");
        foreach (char digit in result)
            if (!Uri.IsHexDigit(digit)) throw Invalid($"'{name}' must be hexadecimal");
        return result;
    }

    private static ImmutableArray<string> Strings(JsonElement root, string name, int maximum)
    {
        var values = RequiredArray(root, name, 1, maximum);
        var builder = ImmutableArray.CreateBuilder<string>(values.GetArrayLength());
        string? previous = null;
        foreach (var item in values.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) throw Invalid($"'{name}' must contain strings");
            string text = item.GetString()!;
            if (text.Length is 0 or > 1024 || previous != null && StringComparer.Ordinal.Compare(previous, text) >= 0)
                throw Invalid($"'{name}' must contain sorted unique names");
            builder.Add(text);
            previous = text;
        }
        return builder.MoveToImmutable();
    }

    private static ImmutableArray<int> Indices(JsonElement root, string name, int count, int maximum)
    {
        var values = RequiredArray(root, name, 0, maximum);
        var builder = ImmutableArray.CreateBuilder<int>(values.GetArrayLength());
        var unique = new HashSet<int>();
        foreach (var item in values.EnumerateArray())
        {
            int index = Index(item, count, name);
            if (!unique.Add(index)) throw Invalid($"'{name}' contains a duplicate index");
            builder.Add(index);
        }
        return builder.MoveToImmutable();
    }

    private static int Index(JsonElement value, int count, string name)
    {
        int index = ReadInteger(value, name);
        if (index < 0 || index >= count) throw Invalid($"'{name}' index is outside its table");
        return index;
    }

    private static int Zone(JsonElement value, int count)
    {
        int zone = ReadInteger(value, "zone");
        if (zone < -1 || zone >= count) throw Invalid("zone index is outside its table");
        return zone;
    }

    private static BoxStationBarrier Barrier(JsonElement value)
    {
        int barrier = ReadInteger(value, "barrier");
        if ((barrier & ~31) != 0) throw Invalid("unknown edge barrier flags");
        return (BoxStationBarrier)barrier;
    }

    private static int Direction(JsonElement value, bool cardinal)
    {
        int direction = ReadInteger(value, "direction");
        if (cardinal ? direction is not (1 or 2 or 4 or 8) : direction is < 0 or > 15)
            throw Invalid("invalid source direction");
        return direction;
    }

    private static InvalidDataException Invalid(string detail) => new($"The Box Station layout is invalid: {detail}.");
}
