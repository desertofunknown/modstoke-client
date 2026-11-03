// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Modstoke.Client;

// Main-thread leases share physical maps across held, inventory and remote
// equipment. Materials remain model-owned; releasing one copy cannot invalidate
// another device's finish.
internal sealed class ClientDeviceSurfaceFinish : IDisposable
{
    internal enum Surface { Polymer, Rubber, Metal, Composite, Solar }
    private const int Size = 1024;
    private const float TileMetres = .064f;
    private static readonly Texture2D?[,] Maps = new Texture2D?[5, 3];
    private static readonly int[] Users = new int[5];
    private readonly bool[] _leases = new bool[5];
    private bool _disposed;

    internal void ApplyShell(Material material, Surface surface = Surface.Polymer) => Apply(material, surface);

    internal void Apply(Material material, Surface surface)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ClientDeviceSurfaceFinish));
        if (!material.HasProperty("_MaskMap") || !material.HasProperty("_NormalMap")) return;
        var kind = (int)surface;
        if (!_leases[kind])
        {
            if (Users[kind] == 0) Load(surface);
            Users[kind]++;
            _leases[kind] = true;
        }
        var metallic = material.HasProperty("_Metallic") ? material.GetFloat("_Metallic") : 0;
        var smoothness = material.HasProperty("_Smoothness") ? material.GetFloat("_Smoothness") : .3f;
        SetMap(material, "_BaseColorMap", Maps[kind, 0]);
        SetMap(material, "_NormalMap", Maps[kind, 1]);
        SetMap(material, "_MaskMap", Maps[kind, 2]);
        // HDRP uses the base UV transform for normal/mask; all are in metres.
        material.SetFloat("_NormalMapSpace", 0);
        material.SetFloat("_NormalScale", 1);
        material.SetFloat("_MetallicRemapMin", 0);
        material.SetFloat("_MetallicRemapMax", Mathf.Clamp01(metallic));
        material.SetFloat("_SmoothnessRemapMin", Mathf.Max(0, smoothness - .12f));
        material.SetFloat("_SmoothnessRemapMax", Mathf.Min(1, smoothness + .12f));
        material.SetFloat("_AORemapMin", 0);
        material.SetFloat("_AORemapMax", 1);
        material.DisableKeyword("_DETAIL_MAP");
        material.EnableKeyword("_NORMALMAP_TANGENT_SPACE");
        material.EnableKeyword("_NORMALMAP");
        material.EnableKeyword("_MASKMAP");
        if (material.HasProperty("_SpecularAAScreenSpaceVariance")) material.SetFloat("_SpecularAAScreenSpaceVariance", .15f);
        if (material.HasProperty("_SpecularAAThreshold")) material.SetFloat("_SpecularAAThreshold", .2f);
        material.EnableKeyword("_ENABLE_GEOMETRIC_SPECULAR_AA");
    }

    private static void SetMap(Material material, string property, Texture2D? map)
    {
        material.SetTexture(property, map);
        material.SetTextureScale(property, Vector2.one / TileMetres);
        material.SetTextureOffset(property, Vector2.zero);
    }

    private static void Load(Surface surface)
    {
        var kind = (int)surface;
        var channels = new[] { "base", "normal", "mask" };
        try
        {
            for (var channel = 0; channel < channels.Length; channel++)
            {
                var name = "device-" + surface.ToString().ToLowerInvariant() + "-" + channels[channel] + ".png";
                using var resource = typeof(ClientDeviceSurfaceFinish).Assembly.GetManifestResourceStream("Modstoke.Client.Assets." + name)
                    ?? throw new InvalidOperationException("The equipment surface is missing: " + name);
                using var bytes = new MemoryStream();
                resource.CopyTo(bytes);
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true, channel != 0);
                Maps[kind, channel] = texture;
                texture.name = "Field equipment " + surface + " " + channels[channel];
                texture.wrapMode = TextureWrapMode.Repeat;
                texture.filterMode = FilterMode.Trilinear;
                texture.anisoLevel = 8;
                if (!ImageConversion.LoadImage(texture, bytes.ToArray(), false) || texture.width != Size || texture.height != Size)
                    throw new InvalidDataException("The equipment surface could not be decoded: " + name);
                if (channel == 1)
                {
                    // Keep authoring PNGs standard RGB +Y. Desktop HDRP's
                    // UnpackNormalmapRGorAG requires X in alpha and Y in green.
                    var pixels = texture.GetPixels32().ToArray();
                    for (var i = 0; i < pixels.Length; i++)
                        pixels[i] = new Color32(255, pixels[i].g, 0, pixels[i].r);
                    texture.SetPixels32(pixels);
                }
                texture.Apply(true, false);
                texture.Compress(true);
                texture.Apply(false, true);
            }
        }
        catch { ReleaseMaps(kind); throw; }
    }

    private static void ReleaseMaps(int kind)
    {
        for (var channel = 0; channel < 3; channel++)
        {
            if (Maps[kind, channel]) UnityEngine.Object.Destroy(Maps[kind, channel]);
            Maps[kind, channel] = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        for (var kind = 0; kind < _leases.Length; kind++)
        {
            if (!_leases[kind]) continue;
            _leases[kind] = false;
            if (--Users[kind] == 0) ReleaseMaps(kind);
        }
    }
}
