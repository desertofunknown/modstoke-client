// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace Modstoke.Client;

internal enum BodyRegion : byte
{
    Head, Neck, Torso, UpperArms, Forearms, Hands, Hips, UpperLegs, LowerLegs, Feet,
    Underwear, Eyes, Mouth, Brows, Hair, Adult
}

// AdultSkin is never stored in a file: the runtime uses it for adult parts with the add-on's textures.
internal enum BodyMaterial : byte { SkinBody, SkinHead, Eye, EyeLens, Teeth, Brows, Hair, Underwear, AdultSkin = 100 }

internal sealed record BodyBone(string Name, Matrix4x4 BindPose);
internal sealed record BodyExtraBone(string Name, string Parent, Vector3 Position, Quaternion Rotation);
internal sealed record BodySubmesh(BodyRegion Region, BodyMaterial Material, int[] Indices);
internal sealed record BodyShape(string Name, int[] Vertices, Vector3[] Positions, Vector3[] Normals);

// Reads the character pipeline's .mcb body files. Every count is
// checked before allocation, so a damaged or hostile file fails here, not inside Unity.
internal sealed class BodyFile
{
    private const int MaximumBytes = 64 * 1024 * 1024;
    private const int MaximumVertices = 200_000;

    private BodyFile(string name) => Name = name;

    internal string Name { get; }
    internal List<BodyBone> Bones { get; } = new();
    internal List<BodyExtraBone> Extras { get; } = new();
    internal Vector3[] Positions { get; private set; } = Array.Empty<Vector3>();
    internal Vector3[] Normals { get; private set; } = Array.Empty<Vector3>();
    internal Vector4[]? Tangents { get; private set; }
    internal Vector2[] Uv0 { get; private set; } = Array.Empty<Vector2>();
    internal Vector2[]? Uv1 { get; private set; }
    internal BoneWeight[] Weights { get; private set; } = Array.Empty<BoneWeight>();
    internal List<BodySubmesh> Submeshes { get; } = new();
    internal List<BodyShape> Shapes { get; } = new();

    internal static BodyFile Read(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("Body file is missing.", path);
        if (info.Length is < 16 or > MaximumBytes) throw new InvalidDataException("Body file size is outside 16 bytes to 64 MiB.");
        using var reader = new BinaryReader(new MemoryStream(File.ReadAllBytes(path)), Encoding.UTF8);
        if (reader.ReadUInt32() != 0x3142434D) throw new InvalidDataException("Not a version 1 body file.");
        var flags = reader.ReadUInt32();
        var file = new BodyFile(Text(reader));

        int boneCount = reader.ReadUInt16();
        Require(boneCount is > 0 and <= 255, "A body file needs 1 to 255 bones.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < boneCount; i++)
        {
            var bone = new BodyBone(Text(reader), Matrix(reader));
            Require(names.Add(bone.Name), "Bone names must be unique.");
            file.Bones.Add(bone);
        }
        int extraCount = reader.ReadUInt16();
        Require(extraCount <= 64 && boneCount + extraCount <= 255, "Too many extra bones.");
        for (var i = 0; i < extraCount; i++)
        {
            var extra = new BodyExtraBone(Text(reader), Text(reader), Vector(reader),
                new Quaternion(Single(reader), Single(reader), Single(reader), Single(reader)));
            // Extra bones are created at runtime, so one must never shadow a rig bone.
            Require(names.Add(extra.Name), "Extra bone names must not repeat another bone.");
            file.Extras.Add(extra);
            file.Bones.Add(new BodyBone(extra.Name, Matrix(reader)));
        }

        var vertexCount = reader.ReadUInt32();
        Require(vertexCount is > 0 and <= MaximumVertices, "A body file needs 1 to 200,000 vertices.");
        var count = (int)vertexCount;
        file.Positions = ReadArray(count, () => Vector(reader));
        file.Normals = ReadArray(count, () => Vector(reader));
        if ((flags & 1) != 0)
            file.Tangents = ReadArray(count, () => new Vector4(Single(reader), Single(reader), Single(reader), Single(reader)));
        file.Uv0 = ReadArray(count, () => new Vector2(Single(reader), Single(reader)));
        if ((flags & 2) != 0)
            file.Uv1 = ReadArray(count, () => new Vector2(Single(reader), Single(reader)));
        var bones = file.Bones.Count;
        file.Weights = ReadArray(count, () =>
        {
            Span<int> indices = stackalloc int[4];
            Span<int> values = stackalloc int[4];
            var sum = 0;
            for (var slot = 0; slot < 4; slot++)
            {
                indices[slot] = reader.ReadByte();
                reader.ReadByte();
                values[slot] = reader.ReadUInt16();
                Require(indices[slot] < bones || values[slot] == 0, "A vertex weight names a missing bone.");
                sum += values[slot];
            }
            Require(Math.Abs(sum - 65535) <= 4, "Vertex weights must add up to one.");
            var weight = new BoneWeight();
            for (var slot = 0; slot < 4; slot++)
            {
                var bone = values[slot] == 0 ? 0 : indices[slot];
                var value = values[slot] / (float)sum;
                switch (slot)
                {
                    case 0: weight.boneIndex0 = bone; weight.weight0 = value; break;
                    case 1: weight.boneIndex1 = bone; weight.weight1 = value; break;
                    case 2: weight.boneIndex2 = bone; weight.weight2 = value; break;
                    default: weight.boneIndex3 = bone; weight.weight3 = value; break;
                }
            }
            return weight;
        });

        int submeshCount = reader.ReadByte();
        Require(submeshCount is > 0 and <= 16, "A body file needs 1 to 16 submeshes.");
        for (var i = 0; i < submeshCount; i++)
        {
            var region = reader.ReadByte();
            var material = reader.ReadByte();
            Require(region <= (byte)BodyRegion.Adult && material <= (byte)BodyMaterial.Underwear, "Unknown region or material.");
            var indexCount = reader.ReadUInt32();
            Require(indexCount > 0 && indexCount % 3 == 0 && indexCount <= vertexCount * 12, "Invalid submesh index count.");
            var indices = new int[indexCount];
            for (var index = 0; index < indices.Length; index++)
            {
                var value = reader.ReadUInt32();
                Require(value < vertexCount, "A triangle names a missing vertex.");
                indices[index] = (int)value;
            }
            file.Submeshes.Add(new BodySubmesh((BodyRegion)region, (BodyMaterial)material, indices));
        }

        int shapeCount = reader.ReadUInt16();
        Require(shapeCount <= 128, "Too many blend shapes.");
        var shapeNames = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < shapeCount; i++)
        {
            var name = Text(reader);
            Require(shapeNames.Add(name), "Blend shape names must be unique.");
            var deltaCount = reader.ReadUInt32();
            Require(deltaCount <= vertexCount, "A blend shape moves more vertices than the mesh has.");
            var vertices = new int[deltaCount];
            var positions = new Vector3[deltaCount];
            var normals = new Vector3[deltaCount];
            for (var delta = 0; delta < vertices.Length; delta++)
            {
                var vertex = reader.ReadUInt32();
                Require(vertex < vertexCount, "A blend shape names a missing vertex.");
                vertices[delta] = (int)vertex;
                positions[delta] = Vector(reader);
                normals[delta] = Vector(reader);
            }
            file.Shapes.Add(new BodyShape(name, vertices, positions, normals));
        }
        Require(reader.BaseStream.Position == reader.BaseStream.Length, "The body file has trailing data.");
        return file;
    }

    internal IEnumerable<BodyRegion> Regions
    {
        get
        {
            var seen = new HashSet<BodyRegion>();
            foreach (var submesh in Submeshes)
                if (seen.Add(submesh.Region)) yield return submesh.Region;
        }
    }

    // One compact mesh per region, so clothing can hide a region on one player while
    // every player shares the same uploaded meshes.
    internal Mesh CreateRegionMesh(BodyRegion region, out BodyMaterial[] materials)
    {
        var parts = Submeshes.FindAll(submesh => submesh.Region == region);
        if (parts.Count == 0) throw new ArgumentException("The body file has no such region.", nameof(region));
        var remap = new Dictionary<int, int>();
        var source = new List<int>();
        foreach (var part in parts)
            foreach (var index in part.Indices)
                if (remap.TryAdd(index, source.Count)) source.Add(index);

        var mesh = new Mesh { name = Name + "." + region };
        try
        {
            mesh.indexFormat = source.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.vertices = Pick(Positions, source);
            mesh.normals = Pick(Normals, source);
            if (Tangents != null) mesh.tangents = Pick(Tangents, source);
            mesh.uv = Pick(Uv0, source);
            if (Uv1 != null) mesh.uv2 = Pick(Uv1, source);
            mesh.boneWeights = Pick(Weights, source);
            var bindposes = new Matrix4x4[Bones.Count];
            for (var i = 0; i < bindposes.Length; i++) bindposes[i] = Bones[i].BindPose;
            mesh.bindposes = bindposes;
            mesh.subMeshCount = parts.Count;
            materials = new BodyMaterial[parts.Count];
            for (var i = 0; i < parts.Count; i++)
            {
                var indices = new int[parts[i].Indices.Length];
                for (var index = 0; index < indices.Length; index++) indices[index] = remap[parts[i].Indices[index]];
                mesh.SetTriangles(indices, i, false);
                materials[i] = parts[i].Material;
            }
            foreach (var shape in Shapes)
            {
                Vector3[]? positions = null;
                Vector3[]? normals = null;
                for (var i = 0; i < shape.Vertices.Length; i++)
                {
                    if (!remap.TryGetValue(shape.Vertices[i], out var vertex)) continue;
                    positions ??= new Vector3[source.Count];
                    normals ??= new Vector3[source.Count];
                    positions[vertex] = shape.Positions[i];
                    normals[vertex] = shape.Normals[i];
                }
                if (positions != null) mesh.AddBlendShapeFrame(shape.Name, 100, positions, normals, null);
            }
            mesh.RecalculateBounds();
            mesh.UploadMeshData(true);
            return mesh;
        }
        catch
        {
            UnityEngine.Object.Destroy(mesh);
            throw;
        }
    }

    private static T[] Pick<T>(T[] values, List<int> source)
    {
        var picked = new T[source.Count];
        for (var i = 0; i < picked.Length; i++) picked[i] = values[source[i]];
        return picked;
    }

    private static T[] ReadArray<T>(int count, Func<T> read)
    {
        var values = new T[count];
        for (var i = 0; i < count; i++) values[i] = read();
        return values;
    }

    private static string Text(BinaryReader reader)
    {
        int length = reader.ReadUInt16();
        Require(length is > 0 and <= 256, "Names are 1 to 256 bytes.");
        return Encoding.UTF8.GetString(reader.ReadBytes(length));
    }

    private static float Single(BinaryReader reader)
    {
        var value = reader.ReadSingle();
        Require(float.IsFinite(value), "The body file contains a value that is not a finite number.");
        return value;
    }

    private static Vector3 Vector(BinaryReader reader) => new(Single(reader), Single(reader), Single(reader));

    private static Matrix4x4 Matrix(BinaryReader reader)
    {
        var matrix = new Matrix4x4();
        for (var i = 0; i < 16; i++) matrix[i] = Single(reader);
        return matrix;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
