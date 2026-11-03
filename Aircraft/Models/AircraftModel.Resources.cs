// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace Modstoke.Client;

internal sealed partial class ClientAircraftModel
{
    private const string ResourcePrefix = "Modstoke.Client.Assets.";
    private const int MaximumAssetBytes = 64 * 1024 * 1024;
    private const int MaximumVertices = 1600000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private void Load()
    {
        var root = _root!;
        var shader = Shader.Find("HDRP/Lit");
        if (!shader) throw new InvalidOperationException("The aircraft requires the game's HDRP/Lit shader.");
        using var stream = Resource("aircraft-kestrel.msa");
        if (!stream.CanSeek || stream.Length > MaximumAssetBytes || stream.Length < 16)
            throw new InvalidDataException("The aircraft model size is invalid.");
        using var reader = new BinaryReader(stream, StrictUtf8, leaveOpen: false);
        // MSA1 has explicit bounded strings, parent-before-child nodes and
        // unindexed triangle corners (position, normal, UV). No external paths.
        if (reader.ReadUInt32() != 0x3141534d || reader.ReadUInt32() != 1)
            throw new InvalidDataException("The aircraft model version is unsupported.");
        var materialCount = Count(reader, 1, 32, "materials");
        var nodeCount = Count(reader, 1, 192, "nodes");
        LoadSurfaceAtlas("base", 0);
        LoadSurfaceAtlas("normal", 1);
        LoadSurfaceAtlas("mask", 2);
        for (var i = 0; i < materialCount; i++)
        {
            var name = ReadName(reader);
            var color = new Color(Number(reader, 0, 1), Number(reader, 0, 1),
                Number(reader, 0, 1), Number(reader, 0, 1));
            var metal = Number(reader, 0, 1);
            var roughness = Number(reader, 0, 1);
            var emission = Number(reader, 0, 10);
            var textured = reader.ReadByte();
            if (textured > 4) throw new InvalidDataException("The aircraft finish flag is invalid.");
            var material = new Material(shader);
            // Register ownership before any material API can fail.
            _materials.Add(material);
            material.name = "Kestrel " + name;
            _finishes.Add(name, material);
            ColorMaterial(material, color);
            material.SetFloat("_Metallic", metal);
            material.SetFloat("_Smoothness", 1 - roughness);
            material.SetColor("_EmissiveColor", color * emission);
            if (textured > 0)
            {
                material.SetTexture("_BaseColorMap", _surfaces[textured - 1, 0]);
                material.SetTexture("_NormalMap", _surfaces[textured - 1, 1]);
                material.SetTexture("_MaskMap", _surfaces[textured - 1, 2]);
                material.SetFloat("_NormalScale", .45f);
                material.SetFloat("_NormalMapSpace", 0);
                material.SetFloat("_MetallicRemapMin", 0);
                material.SetFloat("_MetallicRemapMax", 1);
                material.SetFloat("_SmoothnessRemapMin", 0);
                material.SetFloat("_SmoothnessRemapMax", 1);
                material.SetFloat("_AORemapMin", 0);
                material.SetFloat("_AORemapMax", 1);
                material.EnableKeyword("_NORMALMAP_TANGENT_SPACE");
                material.EnableKeyword("_NORMALMAP");
                material.EnableKeyword("_MASKMAP");
            }
            if (color.a < 1) Transparent(material);
        }

        var nodes = new Transform[nodeCount];
        var totalVertices = 0;
        for (var i = 0; i < nodeCount; i++)
        {
            var name = ReadName(reader);
            var parent = reader.ReadInt32();
            if ((i == 0 && parent != -1) || (i > 0 && (parent < 0 || parent >= i)))
                throw new InvalidDataException("The aircraft node hierarchy is invalid.");
            var position = Vector(reader, 20);
            var materialIndex = reader.ReadInt32();
            var vertexCount = Count(reader, 0, MaximumVertices, "vertices");
            totalVertices = checked(totalVertices + vertexCount);
            if (totalVertices > MaximumVertices || vertexCount % 3 != 0 ||
                (vertexCount == 0 && materialIndex != -1) ||
                (vertexCount > 0 && (materialIndex < 0 || materialIndex >= materialCount)) ||
                (long)vertexCount * 32 > stream.Length - stream.Position)
                throw new InvalidDataException("The aircraft mesh bounds are invalid.");
            var node = new GameObject(name) { layer = root.layer };
            node.transform.SetParent(parent < 0 ? root.transform : nodes[parent], false);
            node.transform.localPosition = position;
            nodes[i] = node.transform;
            if (vertexCount == 0)
            {
                _parts.Add(name, node.transform);
                continue;
            }
            var vertices = new Vector3[vertexCount];
            var normals = new Vector3[vertexCount];
            var uv = new Vector2[vertexCount];
            var indices = new int[vertexCount];
            for (var j = 0; j < vertexCount; j++)
            {
                vertices[j] = Vector(reader, 20);
                normals[j] = Vector(reader, 1.01f);
                if (normals[j].sqrMagnitude < .5f || normals[j].sqrMagnitude > 1.5f)
                    throw new InvalidDataException("The aircraft mesh normal is invalid.");
                uv[j] = new Vector2(Number(reader, -32, 32), Number(reader, -32, 32));
                indices[j] = j;
            }
            var mesh = new Mesh
            {
                name = "Kestrel " + name,
                indexFormat = vertexCount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16
            };
            _meshes.Add(mesh);
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.uv = uv;
            mesh.SetTriangles(indices, 0);
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            node.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = node.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = _materials[materialIndex];
            var transparent = _materials[materialIndex].GetFloat("_SurfaceType") > .5f;
            renderer.shadowCastingMode = transparent ? ShadowCastingMode.Off : ShadowCastingMode.On;
            renderer.receiveShadows = !transparent;
            mesh.UploadMeshData(true);
        }
        if (stream.Position != stream.Length)
            throw new InvalidDataException("The aircraft model contains trailing data.");
        foreach (var name in new[] { "Propeller", "PropellerBlades", "PropellerBlur", "WheelPort",
            "WheelStarboard", "WheelNose", "NoseGearSteering", "AileronPort", "AileronStarboard", "ElevatorPort",
            "ElevatorStarboard", "Rudder", "FlapPort", "FlapStarboard", "AttitudeCard",
            "GaugeAirspeed", "GaugeAltitude", "GaugeRpm", "GaugeFuel", "GaugeHealth",
            "GaugeHeading", "GaugeVerticalSpeed", "StickPort", "StickStarboard", "ThrottleLever",
            "DoorPort", "DoorStarboard", "PedalPortLeft", "PedalPortRight",
            "PedalStarboardLeft", "PedalStarboardRight", "SwitchEngine", "SwitchLights",
            "SwitchFlaps", "SwitchBrake", "SwitchStarter", "SeatHarness0", "SeatHarness1",
            "SeatHarness2", "SeatHarnessIdle0", "SeatHarnessIdle1", "SeatHarnessIdle2" })
            if (!_parts.ContainsKey(name)) throw new InvalidDataException("The aircraft is missing its " + name + " pivot.");
    }

    private static Stream Resource(string name) =>
        typeof(ClientAircraftModel).Assembly.GetManifestResourceStream(ResourcePrefix + name)
        ?? throw new InvalidOperationException("The aircraft resource is missing: " + name);

    private static string ReadName(BinaryReader reader)
    {
        var length = reader.ReadUInt16();
        if (length is < 1 or > 120) throw new InvalidDataException("The aircraft name length is invalid.");
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException("The aircraft name is truncated.");
        var name = StrictUtf8.GetString(bytes);
        foreach (var ch in name)
            if (char.IsControl(ch)) throw new InvalidDataException("The aircraft name contains a control character.");
        return name;
    }

    private static int Count(BinaryReader reader, int minimum, int maximum, string kind)
    {
        var value = reader.ReadUInt32();
        if (value < minimum || value > maximum)
            throw new InvalidDataException("The aircraft " + kind + " count is invalid.");
        return (int)value;
    }

    private static float Number(BinaryReader reader, float minimum, float maximum)
    {
        var value = reader.ReadSingle();
        if (!float.IsFinite(value) || value < minimum || value > maximum)
            throw new InvalidDataException("An aircraft model number is invalid.");
        return value;
    }

    private static Vector3 Vector(BinaryReader reader, float bound) =>
        new(Number(reader, -bound, bound), Number(reader, -bound, bound), Number(reader, -bound, bound));

    private void LoadSurfaceAtlas(string channel, int channelIndex)
    {
        using var stream = Resource("aircraft-kestrel-surface-" + channel + ".png");
        if (!stream.CanSeek || stream.Length is < 24 or > 64 * 1024 * 1024)
            throw new InvalidDataException("The aircraft finish size is invalid.");
        using var bytes = new MemoryStream((int)stream.Length);
        stream.CopyTo(bytes);
        // Only the base color is sRGB. Decode one atlas at a time and isolate its
        // tiles before mip generation; atlas quadrants must never bleed or wrap
        // into other finishes. The desktop BC3 payload is about 64 MiB including
        // mipmaps across all twelve 2048px tiles (256 MiB uncompressed RGBA32).
        var atlas = new Texture2D(2, 2, TextureFormat.RGBA32, false, channelIndex != 0);
        try
        {
            if (!ImageConversion.LoadImage(atlas, bytes.ToArray(), false) ||
                atlas.width != 4096 || atlas.height != 4096)
                throw new InvalidDataException("The aircraft finish could not be decoded.");
            var source = atlas.GetPixels32().ToArray();
            var pixels = new Color32[2048 * 2048];
            for (var kind = 0; kind < 4; kind++)
            {
                var x = (kind % 2) * 2048;
                var y = (kind / 2) * 2048;
                for (var row = 0; row < 2048; row++)
                    Array.Copy(source, (y + row) * 4096 + x, pixels, row * 2048, 2048);
                if (channelIndex == 1)
                {
                    // HDRP's DXT5nm unpack uses X in alpha and Y in green.
                    // Keep RGB source PNGs unchanged for DCC normal mapping.
                    for (var p = 0; p < pixels.Length; p++)
                        pixels[p] = new Color32(255, pixels[p].g, 0, pixels[p].r);
                }
                var texture = new Texture2D(2048, 2048, TextureFormat.RGBA32, true, channelIndex != 0);
                _textures.Add(texture);
                _surfaces[kind, channelIndex] = texture;
                texture.name = "Kestrel surface " + (kind + 1) + " " + channel;
                texture.wrapMode = TextureWrapMode.Repeat;
                texture.filterMode = FilterMode.Trilinear;
                texture.anisoLevel = 8;
                texture.SetPixels32(pixels);
                texture.Apply(true, false);
                texture.Compress(true);
                texture.Apply(false, true);
            }
        }
        finally
        {
            UnityEngine.Object.Destroy(atlas);
        }
    }

    private static void ColorMaterial(Material material, Color color)
    {
        material.SetColor("_BaseColor", color);
    }

    private static void Transparent(Material material)
    {
        // Match HDRP's alpha-blended, double-sided transparent state. Glass has
        // no shadow/depth prepass, so the cabin remains visible from either side.
        material.SetFloat("_SurfaceType", 1);
        material.SetFloat("_BlendMode", 0);
        material.SetFloat("_SrcBlend", (float)BlendMode.One);
        material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        material.SetFloat("_AlphaSrcBlend", (float)BlendMode.One);
        material.SetFloat("_AlphaDstBlend", (float)BlendMode.OneMinusSrcAlpha);
        material.SetFloat("_ZWrite", 0);
        material.SetFloat("_TransparentZWrite", 0);
        material.SetFloat("_ZTestDepthEqualForOpaque", (float)CompareFunction.LessEqual);
        material.SetFloat("_ZTestTransparent", (float)CompareFunction.LessEqual);
        material.SetFloat("_CullMode", (float)CullMode.Off);
        material.SetFloat("_CullModeForward", (float)CullMode.Off);
        material.SetFloat("_DoubleSidedEnable", 1);
        material.SetFloat("_DoubleSidedNormalMode", 2);
        material.SetVector("_DoubleSidedConstants", new Vector4(1, 1, 1, 0));
        material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        material.EnableKeyword("_BLENDMODE_ALPHA");
        material.EnableKeyword("_ENABLE_FOG_ON_TRANSPARENT");
        material.EnableKeyword("_DOUBLESIDED_ON");
        material.SetShaderPassEnabled("TransparentDepthPrepass", false);
        material.SetShaderPassEnabled("TransparentDepthPostpass", false);
        material.SetShaderPassEnabled("ShadowCaster", false);
        material.renderQueue = (int)RenderQueue.Transparent;
    }
}
