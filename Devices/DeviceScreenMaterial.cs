// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Modstoke.Client;

internal static class ClientDeviceScreenMaterial
{
    internal static readonly Color NeutralColor = new(.025f, .045f, .047f, 1);
    private static readonly int UnlitColor = Shader.PropertyToID("_UnlitColor");
    private static readonly int UnlitMap = Shader.PropertyToID("_UnlitColorMap");
    private static readonly int EmissiveColor = Shader.PropertyToID("_EmissiveColor");
    private static readonly int EmissiveMap = Shader.PropertyToID("_EmissiveColorMap");
    private static readonly int ExposureWeight = Shader.PropertyToID("_EmissiveExposureWeight");
    private static readonly int MainMap = Shader.PropertyToID("_MainTex");
    private static readonly int Tint = Shader.PropertyToID("_Color");
    private static readonly Dictionary<int, DisplayProperties> Properties = new();

    private readonly record struct DisplayProperties(bool Emissive, bool UnlitMap, bool MainMap, bool Tint);

    private static DisplayProperties GetProperties(Material material)
    {
        var id = material.shader.GetInstanceID();
        if (Properties.TryGetValue(id, out var properties)) return properties;
        properties = new DisplayProperties(
            material.HasProperty(UnlitColor) && material.HasProperty(EmissiveColor)
                && material.HasProperty(EmissiveMap) && material.HasProperty(ExposureWeight),
            material.HasProperty(UnlitMap), material.HasProperty(MainMap), material.HasProperty(Tint));
        Properties.Add(id, properties);
        return properties;
    }

    internal static Material CreateDisplayMaterial(string name)
    {
        var shader = Shader.Find("HDRP/Unlit") ?? Shader.Find("Unlit/Texture");
        if (!shader) throw new InvalidOperationException("The field device display shader is unavailable.");
        var material = new Material(shader) { name = name };
        try
        {
            ConfigureOwnedUiMaterial(material);
            SetDisplay(material, NeutralColor, null);
            return material;
        }
        catch
        {
            UnityEngine.Object.Destroy(material);
            throw;
        }
    }

    internal static void SetDisplay(Material material, Color color, Texture? texture)
    {
        var properties = GetProperties(material);
        if (properties.Emissive)
        {
            // Pinned HDRP strips opaque emissive-map variants; textures use its base map.
            var textured = texture != null && properties.UnlitMap;
            material.SetColor(UnlitColor, textured ? color : Color.black);
            if (properties.UnlitMap) material.SetTexture(UnlitMap, texture);
            material.SetColor(EmissiveColor, textured ? Color.black : color);
            material.SetTexture(EmissiveMap, null);
            material.SetFloat(ExposureWeight, 0);
            material.DisableKeyword("_EMISSIVE_COLOR_MAP");
        }
        else
        {
            // Built-in Unlit/Texture has no tint; its idle screen is plain black.
            if (properties.MainMap) material.SetTexture(MainMap,
                texture != null ? texture : properties.Tint ? Texture2D.whiteTexture : Texture2D.blackTexture);
            if (properties.Tint) material.SetColor(Tint, color);
        }
    }

    internal static void ConfigureRenderer(Renderer renderer)
    {
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.lightProbeUsage = LightProbeUsage.Off;
        renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
    }

    internal static void ConfigureOwnedUiMaterial(Material material)
    {
        // Preserve the UI/TMP shader, atlas and masking. These flags only apply
        // when supported; they cannot opt a canvas out of camera post-processing.
        if (material.HasProperty(ExposureWeight)) material.SetFloat(ExposureWeight, 0);
        if (material.HasProperty("_SupportDecals"))
        {
            material.SetFloat("_SupportDecals", 0);
            material.EnableKeyword("_DISABLE_DECALS");
        }
    }
}
