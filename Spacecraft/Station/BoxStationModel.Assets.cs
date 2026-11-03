// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace Modstoke.Client;

internal sealed partial class BoxStationModel
{
    private readonly Queue<PendingTexture> _pendingTextures = new();

    private IEnumerable<bool> LoadParts()
    {
        string[] names = { "floor", "ceiling", "hull-breach", "wall", "window-full", "window-edge", "grille-edge", "grille-broken", "catwalk", "lattice", "airlock-frame", "airlock-leaf", "airlock-slat", "airlock-window-slat", "airlock-window-pane",
            "wall-steel-face", "wall-steel-cap",
            "wall-reinforced-face", "wall-reinforced-cap",
            "storage-closet-body", "storage-closet-door", "storage-crate-body", "storage-crate-lid",
            "door-service-wiring", "door-service-module",
            "machine-air-supply", "machine-scrubber", "machine-apc", "machine-light", "machine-solar", "machine-smes",
            "machine-valve", "machine-pump", "machine-tank", "machine-filter", "machine-filter-ports", "machine-filter-ports-flipped",
            "machine-mixer", "machine-mixer-ports", "machine-mixer-ports-flipped", "machine-airalarm", "machine-firealarm-body", "machine-firealarm-switch",
            "machine-thermal-heater", "machine-thermal-freezer", "machine-thermal-port",
            "injector-body", "injector-valve", "injector-toggle", "injector-pulse", "injector-knob",
            "machine-radiator-straight", "machine-radiator-bent", "machine-radiator-junction",
            "machine-room-thermal-body", "machine-room-thermal-hatch", "machine-room-thermal-cell", "machine-room-thermal-rejection",
            "machine-extinguisher-body", "machine-extinguisher-lever", "machine-extinguisher-pin", "machine-extinguisher-nozzle",
            "machine-extinguisher-cabinet-body", "machine-extinguisher-cabinet-door", "machine-extinguisher-cabinet-window",
            "machine-water-tank-body", "machine-water-tank-valve", "machine-water-tank-high-body", "machine-water-tank-high-valve",
            "machine-fuel-tank-body", "machine-fuel-tank-valve",
            "foam-canister", "foam-cap", "foam-lever", "foam-rack", "foam-patch", "repair-sheet", "repair-tile",
            "material-sheet", "material-rods", "material-shard", "girder", "girder-reinforcement",
            "cable-branch", "cable-riser", "cable-hub", "cable-hardware", "cable-cap", "cable-coil",
            "floor-exposed", "floor-cover", "floor-tile", "floor-base-hardware", "floor-cover-hardware", "floor-cover-inset",
            "floor-tile-hardware", "floor-tile-inset",
            "tool-screwdriver", "tool-crowbar", "tool-wirecutters", "tool-wrench", "tool-welder" };
        foreach (var name in names)
            foreach (var suffix in new[] { "", "-lod" })
            {
                using var stream = Resource(name + suffix + ".msa");
                _parts.Add(name + suffix, SpacecraftMeshData.Read(stream, "box-" + name + suffix));
                yield return true;
            }
        foreach (bool endFrame in LoadFabricationParts()) yield return endFrame;
        using var connectedValve = Resource("machine-water-connected-valve.msa");
        _parts.Add("machine-water-connected-valve", SpacecraftMeshData.Read(connectedValve, "box-machine-water-connected-valve"));
        yield return true;
    }

    private IEnumerable<bool> LoadMaterials()
    {
        var shader = Shader.Find("HDRP/Lit");
        if (!shader) throw new InvalidOperationException("The station requires HDRP/Lit.");
        _surface = new Material(shader) { name = "Box Station panels", enableInstancing = true };
        _surface.SetColor("_BaseColor", Color.white);
        _surface.SetTexture("_BaseColorMap", LoadTexture("atlas", false));
        _surface.SetTexture("_MaskMap", LoadTexture("atlas-mask", true));
        _surface.SetFloat("_MetallicRemapMin", 0); _surface.SetFloat("_MetallicRemapMax", 1);
        _surface.SetFloat("_SmoothnessRemapMin", 0); _surface.SetFloat("_SmoothnessRemapMax", 1);
        _surface.SetFloat("_AORemapMin", 0); _surface.SetFloat("_AORemapMax", 1);
        _surface.EnableKeyword("_MASKMAP");
        // Service strips identify fittings; room lighting and emission are driven separately by station power.
        _surface.SetTexture("_EmissiveColorMap", LoadTexture("atlas-emission", false));
        _surface.SetColor("_EmissiveColor", Color.black);
        _surface.EnableKeyword("_EMISSIVE_COLOR_MAP");
        yield return false;
        LoadAirlockMaterial(shader);
        yield return false;
        LoadWallMaterials(shader);
        yield return false;
        _doorServiceMaterial = new Material(shader) { name = "Pressure door service fittings", enableInstancing = true };
        ConfigurePanelMaterial(_doorServiceMaterial, "door-service", 2048);
        yield return false;
        _glass = new Material(shader) { name = "Station laminated glazing", enableInstancing = true };
        _glass.SetColor("_BaseColor", new Color(.19f, .29f, .32f, .18f));
        _glass.SetFloat("_Metallic", 0); _glass.SetFloat("_Smoothness", .62f);
        _glass.SetFloat("_SurfaceType", 1); _glass.SetFloat("_BlendMode", 0);
        _glass.SetFloat("_SrcBlend", (float)BlendMode.One);
        _glass.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        _glass.SetFloat("_AlphaSrcBlend", (float)BlendMode.One);
        _glass.SetFloat("_AlphaDstBlend", (float)BlendMode.OneMinusSrcAlpha);
        _glass.SetFloat("_ZWrite", 0); _glass.SetFloat("_TransparentZWrite", 0);
        _glass.SetFloat("_ZTestDepthEqualForOpaque", (float)CompareFunction.LessEqual);
        _glass.SetFloat("_ZTestTransparent", (float)CompareFunction.LessEqual);
        _glass.SetFloat("_CullMode", (float)CullMode.Off); _glass.SetFloat("_CullModeForward", (float)CullMode.Off);
        _glass.SetFloat("_DoubleSidedEnable", 1); _glass.SetFloat("_DoubleSidedNormalMode", 2);
        _glass.SetVector("_DoubleSidedConstants", new Vector4(1, 1, 1, 0));
        _glass.EnableKeyword("_SURFACE_TYPE_TRANSPARENT"); _glass.EnableKeyword("_BLENDMODE_ALPHA");
        _glass.EnableKeyword("_DOUBLESIDED_ON"); _glass.EnableKeyword("_ENABLE_FOG_ON_TRANSPARENT");
        _glass.SetShaderPassEnabled("TransparentDepthPrepass", false);
        _glass.SetShaderPassEnabled("TransparentDepthPostpass", false);
        _glass.SetShaderPassEnabled("ShadowCaster", false);
        _glass.renderQueue = (int)RenderQueue.Transparent;
        _tintedGlass = new Material(_glass) { name = "Station smoked glazing" };
        _tintedGlass.SetColor("_BaseColor", new Color(.07f, .09f, .11f, .72f));
        _plasmaGlass = new Material(_glass) { name = "Station plasma glazing" };
        _plasmaGlass.SetColor("_BaseColor", new Color(.38f, .12f, .48f, .3f));
        yield return false;
        LoadThermalMaterial(shader);
        yield return false;
        LoadInjectorMaterial(shader);
        yield return false;
        LoadRadiatorMaterial(shader);
        yield return false;
        LoadRoomThermalMaterial(shader);
        yield return false;
        LoadFabricationMaterials(shader);
        yield return false;
        LoadFireAlarmMaterial(shader);
        yield return false;
        LoadFirefightingMaterial(shader);
        yield return false;
        LoadStorageMaterial(shader);
        yield return false;
        LoadHullMaterial(shader);
        yield return false;
        LoadFoamMaterials(shader);
        yield return false;
        LoadRepairMaterial(shader);
        yield return false;
        LoadConstructionMaterials(shader);
        yield return false;
        LoadCableMaterial(shader);
        yield return false;
        LoadFloorMaterials(shader);
        yield return false;
    }

    private void LoadAirlockMaterial(Shader shader)
    {
        _airlock = new Material(shader) { name = "Station airlock finish", enableInstancing = true };
        _airlock.SetColor("_BaseColor", Color.white);
        _airlock.SetTexture("_BaseColorMap", LoadTexture("airlock-base", false, width: 4096, height: 4096));
        _airlock.SetTexture("_MaskMap", LoadTexture("airlock-mask", true));
        _airlock.SetTexture("_NormalMap", LoadTexture("airlock-normal", true, true));
        _airlock.SetFloat("_NormalMapSpace", 0);
        _airlock.SetFloat("_NormalScale", 1);
        _airlock.SetFloat("_MetallicRemapMin", 0); _airlock.SetFloat("_MetallicRemapMax", 1);
        _airlock.SetFloat("_SmoothnessRemapMin", 0); _airlock.SetFloat("_SmoothnessRemapMax", 1);
        _airlock.SetFloat("_AORemapMin", 0); _airlock.SetFloat("_AORemapMax", 1);
        _airlock.EnableKeyword("_MASKMAP");
        _airlock.EnableKeyword("_NORMALMAP");
        _airlock.EnableKeyword("_NORMALMAP_TANGENT_SPACE");
    }

    private void LoadWallMaterials(Shader shader)
    {
        _steelWall = new Material(shader) { name = "Station wall steel", enableInstancing = true };
        _reinforcedWall = new Material(shader) { name = "Station reinforced wall steel", enableInstancing = true };
        ConfigurePanelMaterial(_steelWall, "wall-steel");
        ConfigurePanelMaterial(_reinforcedWall, "wall-reinforced");
    }

    private void ConfigurePanelMaterial(Material material, string texture, int textureSize = 4096)
    {
        material.SetColor("_BaseColor", Color.white);
        material.SetTexture("_BaseColorMap", LoadTexture(texture + "-albedo", false, width: textureSize, height: textureSize));
        material.SetTexture("_MaskMap", LoadTexture(texture + "-mask", true));
        material.SetTexture("_NormalMap", LoadTexture(texture + "-normal", true, true, textureSize, textureSize));
        material.SetFloat("_NormalMapSpace", 0); material.SetFloat("_NormalScale", 1);
        material.SetFloat("_MetallicRemapMin", 0); material.SetFloat("_MetallicRemapMax", 1);
        material.SetFloat("_SmoothnessRemapMin", 0); material.SetFloat("_SmoothnessRemapMax", 1);
        material.SetFloat("_AORemapMin", 0); material.SetFloat("_AORemapMax", 1);
        material.EnableKeyword("_MASKMAP");
        material.EnableKeyword("_NORMALMAP");
        material.EnableKeyword("_NORMALMAP_TANGENT_SPACE");
    }

    private Texture2D LoadTexture(string name, bool linear, bool normal = false, int width = 2048, int height = 2048)
    {
        // Materials may bind placeholders only while their owning model is private and inactive.
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear);
        _textures.Add(texture);
        texture.name = "Box " + name;
        _pendingTextures.Enqueue(new(texture, name, normal, width, height));
        return texture;
    }

    private IEnumerable<bool> PrepareTexture(PendingTexture pending)
    {
        var (texture, name, normal, width, height) = pending;
        using var stream = Resource(name + ".png");
        if (!stream.CanSeek || stream.Length is < 24 or > 16 * 1024 * 1024)
            throw new InvalidDataException("The station texture size is invalid: " + name);
        var bytes = new byte[(int)stream.Length];
        for (int offset = 0; offset < bytes.Length;)
        {
            int count = Math.Min(256 * 1024, bytes.Length - offset);
            stream.ReadExactly(bytes.AsSpan(offset, count));
            offset += count;
            yield return false;
        }
        if (BinaryPrimitives.ReadUInt64BigEndian(bytes) != 0x89504e470d0a1a0aUL
            || BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(12)) != 0x49484452
            || BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16)) != width
            || BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20)) != height)
            throw new InvalidDataException("The station texture dimensions are invalid: " + name);
        // Decode and final mip/upload are indivisible Unity calls; each gets its own frame.
        if (!ImageConversion.LoadImage(texture, bytes, false) || texture.width != width || texture.height != height)
            throw new InvalidDataException("The station texture could not be decoded: " + name);
        yield return true;
        if (normal)
        {
            // Source PNGs use RGB +Y normals; desktop HDRP decodes X from alpha and Y from green.
            for (int row = 0; row < height; row += 16)
            {
                int rows = Math.Min(16, height - row);
                var pixels = texture.GetPixels(0, row, width, rows);
                {
                    var colors = pixels.AsSpan();
                    for (int i = 0; i < colors.Length; i++) colors[i] = new Color(1, colors[i].g, 0, colors[i].r);
                }
                texture.SetPixels(0, row, width, rows, pixels);
                yield return false;
            }
        }
        yield return true;
        texture.wrapMode = TextureWrapMode.Clamp; texture.filterMode = FilterMode.Trilinear; texture.anisoLevel = 8;
        texture.Apply(true, true);
        yield return true;
    }

    private readonly record struct PendingTexture(Texture2D Texture, string Name, bool Normal, int Width, int Height);

    private static Stream Resource(string name) => typeof(BoxStationModel).Assembly
        .GetManifestResourceStream("Modstoke.Client.Assets.box-" + name)
        ?? throw new InvalidDataException("The station asset is missing: " + name);
}
