// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftModel
{
    private readonly List<Mesh> _airlockMeshes = new();
    private readonly List<Texture2D> _airlockTextures = new();
    private readonly List<Transform> _airlockSweeps = new();

    private void BindAirlockSeals()
    {
        var shader = Shader.Find("HDRP/Lit");
        if (!shader) throw new InvalidOperationException("The airlock requires the game's HDRP/Lit shader.");
        var material = new Material(shader) { name = "Airlock pressure seals", enableInstancing = true };
        _materials.Add(material);
        material.SetColor("_BaseColor", Color.white);
        material.SetTexture("_BaseColorMap", LoadAirlockTexture("atlas", false));
        material.SetTexture("_MaskMap", LoadAirlockTexture("atlas-mask", true));
        material.SetFloat("_MetallicRemapMin", 0);
        material.SetFloat("_MetallicRemapMax", 1);
        material.SetFloat("_SmoothnessRemapMin", 0);
        material.SetFloat("_SmoothnessRemapMax", 1);
        material.SetFloat("_AORemapMin", 0);
        material.SetFloat("_AORemapMax", 1);
        material.EnableKeyword("_MASKMAP");

        var gasket = LoadAirlockMesh("gasket");
        var threshold = LoadAirlockMesh("threshold");
        var sweep = LoadAirlockMesh("sweep");
        var meeting = LoadAirlockMesh("meeting-seal");
        var header = LoadAirlockMesh("header-backing");
        FitDoor("A01", -.094f, -8.984795f, .7921903f, 1, _innerDoorFrames);
        FitDoor("A02", -.094027f, -4.84f, .7951963f, -1, _cabinDoorFrames);

        void FitDoor(string name, float seam, float plane, float headerY, int chamberSide, Transform[] leaves)
        {
            var chamberRotation = Quaternion.Euler(0, chamberSide > 0 ? 0 : 180, 0);
            AddMeshPart(name + " perimeter gasket", gasket, material, _root!.transform,
                new(seam, -1.26f, plane + chamberSide * .077f), chamberRotation);
            AddMeshPart(name + " flush sill", threshold, material, _root.transform,
                new(seam, -1.26f, plane), Quaternion.identity);
            AddMeshPart(name + " forward header backing", header, material, _root.transform,
                new(seam, headerY, plane + .079f), Quaternion.identity);
            AddMeshPart(name + " aft header backing", header, material, _root.transform,
                new(seam, headerY, plane - .079f), Quaternion.Euler(0, 180, 0));
            for (var i = 0; i < leaves.Length; i++)
            {
                // The mesh origin is the closed leaf's floor center; the owned
                // unit-scale frame already carries its native closing translation.
                var position = new Vector3(seam + (i == 0 ? .373f : -.373f), -1.26f, plane);
                _airlockSweeps.Add(AddMeshPart(name + " lower leaf seal " + i, sweep, material, leaves[i],
                    position - leaves[i].localPosition, Quaternion.identity));
            }
            AddMeshPart(name + " meeting seal", meeting, material, leaves[0],
                new Vector3(seam - .005f, -1.26f, plane + chamberSide * .075f) - leaves[0].localPosition,
                chamberRotation);
        }
    }

    private static Transform AddMeshPart(string name, Mesh mesh, Material material, Transform parent,
        Vector3 position, Quaternion rotation)
    {
        var part = new GameObject(name) { layer = parent.gameObject.layer };
        part.transform.SetParent(parent, false);
        part.transform.localPosition = position;
        part.transform.localRotation = rotation;
        part.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = part.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.On;
        renderer.receiveShadows = true;
        return part.transform;
    }

    private Mesh LoadAirlockMesh(string asset)
    {
        using var stream = AirlockResource(asset + ".msa");
        var data = SpacecraftMeshData.Read(stream, asset);
        var mesh = new Mesh();
        _airlockMeshes.Add(mesh);
        mesh.name = "Airlock " + data.Name;
        mesh.indexFormat = data.Vertices.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
        mesh.vertices = data.Vertices;
        mesh.normals = data.Normals;
        mesh.uv = data.Uv;
        mesh.SetTriangles(data.Triangles, 0);
        mesh.RecalculateBounds();
        mesh.RecalculateTangents();
        mesh.UploadMeshData(true);
        return mesh;
    }

    private Texture2D LoadAirlockTexture(string asset, bool linear)
    {
        using var stream = AirlockResource(asset + ".png");
        if (!stream.CanSeek || stream.Length is < 24 or > 8 * 1024 * 1024)
            throw new InvalidDataException("The airlock texture size is invalid: " + asset);
        using var bytes = new MemoryStream((int)stream.Length);
        stream.CopyTo(bytes);
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear);
        _airlockTextures.Add(texture);
        texture.name = "Airlock " + asset;
        if (!ImageConversion.LoadImage(texture, bytes.ToArray(), false) || texture.width != 512 || texture.height != 512)
            throw new InvalidDataException("The airlock texture could not be decoded: " + asset);
        texture.wrapMode = TextureWrapMode.Clamp;
        texture.filterMode = FilterMode.Trilinear;
        texture.anisoLevel = 8;
        texture.Apply(true, true);
        return texture;
    }

    private static Stream AirlockResource(string name) => typeof(ClientSpacecraftModel).Assembly.GetManifestResourceStream(
        "Modstoke.Client.Assets.spacecraft-airlock-" + name)
        ?? throw new InvalidOperationException("The airlock asset is missing: " + name);

    private void AddAirlockSealCollision(PhysicMaterial material, List<BoxCollider> solids)
    {
        foreach (var sweep in _airlockSweeps)
            AddMeshCollision(sweep, sweep.parent, material, solids);
    }

    private void ReleaseAirlockSeals()
    {
        _airlockSweeps.Clear();
        foreach (var mesh in _airlockMeshes) if (mesh) UnityEngine.Object.Destroy(mesh);
        _airlockMeshes.Clear();
        foreach (var texture in _airlockTextures) if (texture) UnityEngine.Object.Destroy(texture);
        _airlockTextures.Clear();
    }
}
