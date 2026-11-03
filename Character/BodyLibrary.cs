// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.IO;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace Modstoke.Client;

internal sealed record BodyPart(BodyRegion Region, Mesh Mesh, BodyMaterial[] Materials);

internal sealed class BodyModel
{
    internal BodyModel(CharacterSex sex, int lod, BodyFile file, List<BodyPart> parts)
    {
        Sex = sex;
        Lod = lod;
        Bones = file.Bones;
        Extras = file.Extras;
        Parts = parts;
    }

    internal CharacterSex Sex { get; }
    internal int Lod { get; }
    internal IReadOnlyList<BodyBone> Bones { get; }
    internal IReadOnlyList<BodyExtraBone> Extras { get; }
    internal IReadOnlyList<BodyPart> Parts { get; }
}

// Meshes and textures from the Modstoke.Characters package, loaded once and shared by
// every player who uses them. Only the game thread touches Unity objects here.
internal sealed class BodyLibrary : IDisposable
{
    private readonly string? _root;
    private readonly string? _adultRoot;
    private readonly string? _hdRoot;
    private readonly string? _uhdRoot;
    private int _tier = 2048;
    private readonly Dictionary<(CharacterSex, int, bool), BodyModel> _models = new();
    private readonly Dictionary<string, Texture2D?> _textures = new(StringComparer.Ordinal);
    private readonly HashSet<(CharacterSex, int, bool)> _failed = new();

    internal BodyLibrary()
    {
        var client = Path.GetDirectoryName(typeof(BodyLibrary).Assembly.Location);
        if (string.IsNullOrEmpty(client)) return;
        _root = Path.GetFullPath(Path.Combine(client, "..", "Modstoke.Characters"));
        _adultRoot = Path.GetFullPath(Path.Combine(client, "..", "Modstoke.Characters.Adult"));
        _hdRoot = Path.GetFullPath(Path.Combine(client, "..", "Modstoke.Characters.HD"));
        _uhdRoot = Path.GetFullPath(Path.Combine(client, "..", "Modstoke.Characters.UHD"));
    }

    internal string Status { get; private set; } = "The character package has not been checked.";
    internal bool Installed => _root != null && Directory.Exists(Path.Combine(_root, "bodies"));
    internal bool HdInstalled => _hdRoot != null && Directory.Exists(Path.Combine(_hdRoot, "textures"));
    internal bool UhdInstalled => _uhdRoot != null && Directory.Exists(Path.Combine(_uhdRoot, "textures"));

    // The largest texture size in use: 2048 (base), 4096 (HD pack) or 8192 (UHD pack). Changing
    // it drops cached maps; the caller rebuilds bodies so their materials pick up the new ones.
    internal int Tier
    {
        get => _tier;
        set
        {
            if (value == _tier) return;
            _tier = value;
            DisposeTextures();
        }
    }

    internal bool AdultInstalled => Installed && _adultRoot != null && Directory.Exists(Path.Combine(_adultRoot, "bodies"));

    internal bool TryGet(CharacterSex sex, int lod, out BodyModel model) => TryGet(sex, lod, false, out model);

    // Adult parts come only from the separate Modstoke.Characters.Adult package.
    internal bool TryGet(CharacterSex sex, int lod, bool adult, out BodyModel model)
    {
        if (_models.TryGetValue((sex, lod, adult), out model!)) return true;
        var root = adult ? _adultRoot : _root;
        if (root == null || _failed.Contains((sex, lod, adult))) return false;
        var name = (sex == CharacterSex.Female ? "female" : "male") + (adult ? ".adult.lod" : ".body.lod") + lod;
        var parts = new List<BodyPart>();
        try
        {
            var file = BodyFile.Read(Path.Combine(root, "bodies", name + ".mcb"));
            // Each package may only carry its own kind of parts.
            foreach (var submesh in file.Submeshes)
                if ((submesh.Region == BodyRegion.Adult) != adult)
                    throw new InvalidDataException("The file carries parts that belong to the other package.");
            foreach (var region in file.Regions)
                parts.Add(new BodyPart(region, file.CreateRegionMesh(region, out var materials), materials));
            model = new BodyModel(sex, lod, file, parts);
            _models[(sex, lod, adult)] = model;
            Status = "Character package loaded.";
            return true;
        }
        // Unity rejects some data only while building, as Il2Cpp exceptions; none may stop the Client.
        catch (Exception error)
        {
            foreach (var part in parts) UnityEngine.Object.Destroy(part.Mesh);
            _failed.Add((sex, lod, adult));
            Status = name + " could not be loaded: " + error.Message;
            Log?.LogWarning(Status);
            model = null!;
            return false;
        }
    }

    // Missing maps are allowed; the cloned native material keeps its own map for that slot.
    // An adult view prefers the add-on's own copy of a map (the uncovered body textures);
    // otherwise the optional HD pack's 4K maps win over the base package's 2K ones.
    internal Texture2D? Texture(CharacterSex sex, string part, string map, bool adultView = false)
    {
        var name = (sex == CharacterSex.Female ? "female" : "male") + "." + part + "." + map;
        string? path = null;
        var packs = new List<string?>();
        if ((adultView || part == "adult") && _adultRoot != null)
        {
            if (_tier >= 8192) packs.Add(Path.Combine(_adultRoot, "textures-8k"));
            if (_tier >= 4096) packs.Add(Path.Combine(_adultRoot, "textures-4k"));
            packs.Add(Path.Combine(_adultRoot, "textures"));
        }
        if (part != "adult")
        {
            if (_tier >= 8192 && _uhdRoot != null) packs.Add(Path.Combine(_uhdRoot, "textures"));
            if (_tier >= 4096 && _hdRoot != null) packs.Add(Path.Combine(_hdRoot, "textures"));
            if (_root != null) packs.Add(Path.Combine(_root, "textures"));
        }
        foreach (var folder in packs)
        {
            if (folder == null || !File.Exists(Path.Combine(folder, name + ".mtx"))) continue;
            path = Path.Combine(folder, name + ".mtx");
            break;
        }
        var key = path ?? name;
        if (_textures.TryGetValue(key, out var cached)) return cached;
        Texture2D? texture = null;
        try
        {
            if (path != null) texture = ReadTexture(path, name);
        }
        catch (Exception error)
        {
            // A broken map leaves the copied native texture in place.
            if (texture != null && texture) UnityEngine.Object.Destroy(texture);
            texture = null;
            Log?.LogWarning(name + " could not be loaded: " + error.Message);
        }
        _textures[key] = texture;
        return texture;
    }

    internal BepInEx.Logging.ManualLogSource? Log { get; set; }

    // .mtx files are compressed offline (pipeline compress_textures.py), so loading is a copy
    // into a GPU-format texture with no decoding or compression on the game thread.
    private static Texture2D ReadTexture(string path, string name)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 20 || bytes[0] != 'M' || bytes[1] != 'T' || bytes[2] != 'X' || bytes[3] != '1')
            throw new InvalidDataException("not a texture file");
        var width = BitConverter.ToInt32(bytes, 4);
        var height = BitConverter.ToInt32(bytes, 8);
        int mips = BitConverter.ToUInt16(bytes, 12);
        var kind = bytes[14];
        var length = BitConverter.ToInt32(bytes, 16);
        if (width is < 4 or > 8192 || height is < 4 or > 8192 || kind > 3 || length != bytes.Length - 20)
            throw new InvalidDataException("unsupported size or format");
        var format = kind switch { 2 => TextureFormat.BC5, 3 => TextureFormat.BC4, _ => TextureFormat.BC7 };
        var block = kind == 3 ? 8 : 16;
        long expected = 0;
        for (int level = 0, w = width, h = height; level < mips; level++, w = Math.Max(1, w / 2), h = Math.Max(1, h / 2))
            expected += (long)((w + 3) / 4) * ((h + 3) / 4) * block;
        if (mips < 1 || expected != length) throw new InvalidDataException("the mip data does not match its size");
        var texture = new Texture2D(width, height, format, mips, kind != 0) { name = name };
        try
        {
            var pin = System.Runtime.InteropServices.GCHandle.Alloc(bytes, System.Runtime.InteropServices.GCHandleType.Pinned);
            try { texture.LoadRawTextureData(pin.AddrOfPinnedObject() + 20, length); }
            finally { pin.Free(); }
            texture.wrapMode = TextureWrapMode.Repeat;
            texture.filterMode = FilterMode.Trilinear;
            texture.anisoLevel = 8;
            texture.Apply(false, true);
            return texture;
        }
        catch
        {
            UnityEngine.Object.Destroy(texture);
            throw;
        }
    }

    private void DisposeTextures()
    {
        foreach (var texture in _textures.Values)
            if (texture != null && texture) UnityEngine.Object.Destroy(texture);
        _textures.Clear();
    }

    public void Dispose()
    {
        foreach (var model in _models.Values)
            foreach (var part in model.Parts)
                if (part.Mesh) UnityEngine.Object.Destroy(part.Mesh);
        DisposeTextures();
        _models.Clear();
        _failed.Clear();
    }
}
