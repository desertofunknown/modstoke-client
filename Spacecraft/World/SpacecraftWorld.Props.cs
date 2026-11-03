// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftWorld
{
    private readonly record struct WorldProp(Mesh Mesh, Material Material, Mesh? Collision);

    private WorldProp LoadProp(Shader shader, string asset, bool collision)
    {
        var mesh = LoadPropMesh(asset);
        var surface = Surface(shader, "Expedition " + asset, asset, 2048);
        surface.enableInstancing = true;
        return new WorldProp(mesh, surface, collision ? LoadPropMesh(asset + "-collision") : null);
    }

    // The authored props use the existing MSA1 corner layout, restricted here to
    // one atlas and one root mesh. Instances share GPU and collision resources.
    private Mesh LoadPropMesh(string asset)
    {
        using var stream = typeof(ClientSpacecraftWorld).Assembly.GetManifestResourceStream(
            "Modstoke.Client.Assets.spacecraft-world-" + asset + ".msa")
            ?? throw new InvalidOperationException("The expedition model is missing: " + asset);
        var data = SpacecraftMeshData.Read(stream, "world-" + asset);
        return CreateMesh("Authored expedition " + asset, data.Vertices, data.Triangles, data.Uv, data.Normals);
    }

    private void PlaceProp(WorldProp prop, string name, Vector3 position, Vector3 scale, float angle)
    {
        var node = AttachMesh(name, _root!.transform, prop.Mesh, prop.Material, false);
        node.transform.localPosition = position;
        node.transform.localRotation = Quaternion.Euler(0, angle * Mathf.Rad2Deg, 0);
        node.transform.localScale = scale;
        if (prop.Collision != null)
        {
            node.isStatic = true;
            node.AddComponent<MeshCollider>().sharedMesh = prop.Collision;
        }
    }

    private void PlaceRock(WorldProp rock, Vector2 point, float radius, float height, float angle)
    {
        var position = Ground(point);
        // Bury the lower edge in sloping ground without stretching the authored
        // silhouette or creating an invisible terrace around it.
        var low = position.y;
        for (var i = 0; i < 4; i++)
        {
            var turn = angle + i * Mathf.PI * .5f;
            var edge = point + new Vector2(Mathf.Cos(turn), Mathf.Sin(turn)) * radius * .7f;
            low = Mathf.Min(low, Ground(edge).y);
        }
        position.y -= Mathf.Min(height * .35f, (position.y - low) * .5f) + .08f;
        PlaceProp(rock, "Fractured wet outcrop", position, new Vector3(radius, height, radius * .67f), angle);
    }
}
