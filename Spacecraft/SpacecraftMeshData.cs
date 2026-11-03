// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.IO;
using System.Text;
using UnityEngine;

namespace Modstoke.Client;

internal sealed record SpacecraftMeshData(string MaterialName, Color Color, float Metallic, float Roughness,
    float Emission, bool Textured, string Name, Vector3[] Vertices, Vector3[] Normals, Vector2[] Uv, int[] Triangles)
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    // MSA1 corner data restricted to one material and one root mesh; the caller owns the stream.
    internal static SpacecraftMeshData Read(Stream stream, string assetName)
    {
        if (!stream.CanSeek || stream.Length is < 64 or > 8 * 1024 * 1024)
            throw new InvalidDataException("The spacecraft model size is invalid: " + assetName);
        using var reader = new BinaryReader(stream, StrictUtf8, leaveOpen: true);
        if (reader.ReadUInt32() != 0x3141534d || reader.ReadUInt32() != 1 ||
            reader.ReadUInt32() != 1 || reader.ReadUInt32() != 1)
            throw new InvalidDataException("The spacecraft model layout is invalid: " + assetName);
        var materialName = ReadName(reader, assetName);
        var color = new Color(Number(reader, 0, 1, assetName), Number(reader, 0, 1, assetName),
            Number(reader, 0, 1, assetName), Number(reader, 0, 1, assetName));
        var metallic = Number(reader, 0, 1, assetName);
        var roughness = Number(reader, 0, 1, assetName);
        var emission = Number(reader, 0, 10, assetName);
        var textured = reader.ReadByte();
        if (textured > 1) throw new InvalidDataException("The spacecraft finish is invalid: " + assetName);
        var name = ReadName(reader, assetName);
        if (reader.ReadInt32() != -1) throw new InvalidDataException("The spacecraft root is invalid: " + assetName);
        for (var i = 0; i < 3; i++)
            if (Number(reader, -20, 20, assetName) != 0)
                throw new InvalidDataException("The spacecraft origin is invalid: " + assetName);
        if (reader.ReadInt32() != 0) throw new InvalidDataException("The spacecraft material index is invalid: " + assetName);
        var count = reader.ReadUInt32();
        if (count is < 3 or > 120000 || count % 3 != 0 || stream.Length - stream.Position != (long)count * 32)
            throw new InvalidDataException("The spacecraft mesh size is invalid: " + assetName);
        var vertices = new Vector3[count];
        var normals = new Vector3[count];
        var uv = new Vector2[count];
        var triangles = new int[count];
        for (var i = 0; i < count; i++)
        {
            vertices[i] = new Vector3(Number(reader, -20, 20, assetName), Number(reader, -20, 20, assetName), Number(reader, -20, 20, assetName));
            normals[i] = new Vector3(Number(reader, -1.01f, 1.01f, assetName), Number(reader, -1.01f, 1.01f, assetName), Number(reader, -1.01f, 1.01f, assetName));
            if (normals[i].sqrMagnitude is < .5f or > 1.5f)
                throw new InvalidDataException("The spacecraft normal is invalid: " + assetName);
            uv[i] = new Vector2(Number(reader, -32, 32, assetName), Number(reader, -32, 32, assetName));
            triangles[i] = i;
        }
        return new SpacecraftMeshData(materialName, color, metallic, roughness, emission, textured == 1,
            name, vertices, normals, uv, triangles);
    }

    private static string ReadName(BinaryReader reader, string assetName)
    {
        var length = reader.ReadUInt16();
        if (length is < 1 or > 120) throw new InvalidDataException("The spacecraft label is invalid: " + assetName);
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException("The spacecraft label is truncated: " + assetName);
        var name = StrictUtf8.GetString(bytes);
        foreach (var ch in name)
            if (char.IsControl(ch)) throw new InvalidDataException("The spacecraft label contains a control character: " + assetName);
        return name;
    }

    private static float Number(BinaryReader reader, float minimum, float maximum, string assetName)
    {
        var value = reader.ReadSingle();
        if (!float.IsFinite(value) || value < minimum || value > maximum)
            throw new InvalidDataException("The spacecraft model contains an invalid number: " + assetName);
        return value;
    }
}
