// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Modstoke.Client;

internal enum CharacterSex : byte { Male, Female }

// Everything other players need to rebuild your character. Colours are opaque multipliers
// over the neutral textures, and shape values run from 0 to 1.
internal sealed record CharacterAppearance
{
    internal static readonly string[] MaleShapes = { "Build", "Soft_Tissue" };
    internal static readonly string[] FemaleShapes =
    {
        "Muscle_Tone", "Soft_Tissue", "Hip_Fullness", "Waist_Fullness", "Glute_Fullness",
        "Thigh_Fullness", "Breast_Shape", "SkinnyMorph"
    };
    // Both body types' values travel together, so switching type keeps the other's sliders.
    private static readonly string[] AllShapes =
    {
        "Build", "Soft_Tissue", "Muscle_Tone", "Hip_Fullness", "Waist_Fullness", "Glute_Fullness",
        "Thigh_Fullness", "Breast_Shape", "SkinnyMorph"
    };
    private const int Length = 19 + 9;

    public CharacterSex Sex { get; init; }
    public Color32 Skin { get; init; } = new(226, 188, 160, 255);
    public Color32 Eyes { get; init; } = new(80, 112, 150, 255);
    public byte Hair { get; init; }
    public Color32 HairColor { get; init; } = new(72, 52, 38, 255);
    public Color32 Underwear { get; init; } = new(40, 42, 48, 255);
    public bool NoUnderwear { get; init; }
    public IReadOnlyDictionary<string, float> Shapes { get; init; } = new Dictionary<string, float>(StringComparer.Ordinal);

    internal IReadOnlyList<string> ShapeNames => Sex == CharacterSex.Female ? FemaleShapes : MaleShapes;

    // The body package's neutral shape for each body type, used until the player moves a slider.
    private static readonly Dictionary<string, float> MaleDefaults = new(StringComparer.Ordinal) { ["Build"] = .554f };
    private static readonly Dictionary<string, float> FemaleDefaults = new(StringComparer.Ordinal)
    {
        ["Muscle_Tone"] = .08f, ["Soft_Tissue"] = .3f, ["Hip_Fullness"] = .15f, ["Waist_Fullness"] = .35f,
        ["Glute_Fullness"] = .45f, ["Thigh_Fullness"] = .35f, ["Breast_Shape"] = .8f
    };

    internal float Shape(string name) => Shapes.TryGetValue(name, out var value) ? value
        : (Sex == CharacterSex.Female ? FemaleDefaults : MaleDefaults).TryGetValue(name, out var fallback) ? fallback : 0;

    // 28 bytes: version, sex, four RGB colours, hair style, flags, three reserved bytes and nine shape values.
    internal byte[] Encode()
    {
        using var stream = new MemoryStream(Length);
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)2);
        writer.Write((byte)Sex);
        Write(writer, Skin);
        Write(writer, Eyes);
        Write(writer, HairColor);
        Write(writer, Underwear);
        writer.Write(Hair);
        writer.Write(NoUnderwear ? 1u : 0u);
        foreach (var name in AllShapes) writer.Write((byte)Math.Round(Math.Clamp(Shape(name), 0, 1) * 255));
        return stream.ToArray();
    }

    internal static bool TryDecode(ReadOnlySpan<byte> data, out CharacterAppearance appearance)
    {
        appearance = new CharacterAppearance();
        if (data.Length != Length || data[0] != 2 || data[1] > (byte)CharacterSex.Female
            || data[15] > 1 || data[16] != 0 || data[17] != 0 || data[18] != 0) return false;
        var shapes = new Dictionary<string, float>(StringComparer.Ordinal);
        for (var i = 0; i < AllShapes.Length; i++) shapes[AllShapes[i]] = data[19 + i] / 255f;
        appearance = new CharacterAppearance
        {
            Sex = (CharacterSex)data[1],
            Skin = Read(data, 2),
            Eyes = Read(data, 5),
            HairColor = Read(data, 8),
            Underwear = Read(data, 11),
            Hair = data[14],
            NoUnderwear = data[15] == 1,
            Shapes = shapes
        };
        return true;
    }

    private static void Write(BinaryWriter writer, Color32 color)
    {
        writer.Write(color.r);
        writer.Write(color.g);
        writer.Write(color.b);
    }

    private static Color32 Read(ReadOnlySpan<byte> data, int offset) =>
        new(data[offset], data[offset + 1], data[offset + 2], 255);
}
