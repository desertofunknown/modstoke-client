// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class BoxStationModel
{
    private static readonly string[] FabricationParts = { "shell", "metal", "rubber", "glass", "accent", "interior", "key" };
    private static readonly string[] SpareParts = { "shell", "metal", "rubber", "contents", "accent" };
    private readonly ClientDeviceSurfaceFinish _fabricationFinish = new();
    private readonly List<Material> _fabricationOwnedMaterials = new();
    private readonly Material[] _fabricationMaterials = new Material[7];
    private readonly Material[,] _spareMaterials = new Material[2, 5];
    private Mesh[,] _fabricationMeshes = new Mesh[7, 2];
    private Mesh[,,] _spareMeshes = new Mesh[2, 5, 2];
    private Transform? _fabricationFrame;
    private readonly Transform[] _fabricationKeys = new Transform[8];
    private readonly MeshFilter[] _fabricationFilters = new MeshFilter[6];
    private readonly MeshFilter[] _fabricationKeyFilters = new MeshFilter[8];
    private readonly float[] _fabricationPressUntil = new float[8], _fabricationKeyAmounts = new float[8];
    private bool _fabricationDetail;

    internal Transform FabricationControlFrame(int machine)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (machine != BoxStationHullRepair.FabricationMachine) throw new ArgumentOutOfRangeException(nameof(machine));
        return _fabricationFrame ?? throw new InvalidOperationException("The station fabricator is unavailable.");
    }

    internal Transform FabricationKeyFrame(int machine, BoxStationMaterialControl control)
    {
        FabricationControlFrame(machine);
        return _fabricationKeys[FabricationButtonIndex(control)];
    }

    internal Vector3 FabricationControlPoint(int machine, BoxStationMaterialControl control)
        => FabricationControlFrame(machine).TransformPoint(FabricationButtonCenter(control));

    internal static Vector3 FabricationButtonCenter(BoxStationMaterialControl control)
    {
        int button = FabricationButtonIndex(control);
        return new(-.42f + button % 4 * .28f, button < 4 ? .93f : .81f, .47f);
    }

    private static int FabricationButtonIndex(BoxStationMaterialControl control)
    {
        int button = (int)control - (int)BoxStationMaterialControl.LoadFabricator;
        if ((uint)button >= 8) throw new ArgumentOutOfRangeException(nameof(control));
        return button;
    }

    // Called for accepted interactions; snapshot receipt alone does not imply a key was pressed.
    internal void PressFabricationControl(int machine, BoxStationMaterialControl control)
    {
        FabricationControlFrame(machine);
        _fabricationPressUntil[FabricationButtonIndex(control)] = Time.unscaledTime + .14f;
    }

    internal bool TryFabricationControlTarget(Ray ray, float reach, out BoxStationMaterialControl control, out Vector3 contact)
    {
        control = default; contact = default;
        if (!Visible || _fabricationFrame == null || !Finite(ray.origin) || !Finite(ray.direction)
            || ray.direction.sqrMagnitude < .000001f || !float.IsFinite(reach) || reach <= 0 || reach > 6
            || Vector3.Dot(ray.direction.normalized, _fabricationFrame.forward) >= -.1f) return false;
        ray.direction = ray.direction.normalized;
        var vertices = _parts["station-autolathe-key"].Vertices;
        var bounds = _fabricationMeshes[6, 0].bounds;
        float nearest = reach;
        bool found = false;
        for (int button = 0; button < _fabricationKeys.Length; button++)
        {
            var key = _fabricationKeys[button];
            if (!StoredMeshContact(vertices, bounds, 1, 0, key.position, key.rotation, ray, nearest, out var point)) continue;
            nearest = Vector3.Distance(ray.origin, point);
            control = (BoxStationMaterialControl)((int)BoxStationMaterialControl.LoadFabricator + button);
            contact = point;
            found = true;
        }
        return found;
    }

    private IEnumerable<bool> LoadFabricationParts()
    {
        foreach (string part in FabricationParts)
            foreach (bool endFrame in LoadFabricationPart("station-autolathe-" + part)) yield return endFrame;
        foreach (string kind in new[] { "line", "circuit" })
            foreach (string part in SpareParts)
                foreach (bool endFrame in LoadFabricationPart("station-" + kind + "-spare-" + part)) yield return endFrame;
    }

    private IEnumerable<bool> LoadFabricationPart(string name)
    {
        foreach (string suffix in new[] { "", "-lod" })
        {
            using var stream = Resource(name + suffix + ".msa");
            _parts.Add(name + suffix, SpacecraftMeshData.Read(stream, name + suffix));
            yield return true;
        }
    }

    private IEnumerable<bool> BuildFabricationMachinery()
    {
        if (!BoxStationHullRepair.TryFabricationDefinition(_layout, out var source))
            throw new InvalidDataException("The station fabricator does not match the mapped autolathe.");
        var frame = _fabricationFrame = Node("Autolathe", Root.transform);
        frame.localPosition = _layout.CellPosition(source.Cell);
        frame.localRotation = Turn(PortDirection(source.Direction == 0 ? 2 : source.Direction));
        for (int part = 0; part < FabricationParts.Length; part++)
            for (int lod = 0; lod < 2; lod++)
            {
                _fabricationMeshes[part, lod] = CreateMesh(_parts["station-autolathe-" + FabricationParts[part] + (lod == 0 ? "" : "-lod")]);
                yield return false;
            }
        for (int part = 0; part < _fabricationFilters.Length; part++)
        {
            var node = Node("Autolathe " + FabricationParts[part], frame);
            Render(node, _fabricationMeshes[part, 1], _fabricationMaterials[part]);
            _fabricationFilters[part] = node.GetComponent<MeshFilter>();
            yield return false;
        }
        for (int button = 0; button < _fabricationKeys.Length; button++)
        {
            var node = _fabricationKeys[button] = Node("Autolathe key " + button, frame);
            node.localPosition = FabricationButtonCenter((BoxStationMaterialControl)((int)BoxStationMaterialControl.LoadFabricator + button));
            Render(node, _fabricationMeshes[6, 1], _fabricationMaterials[6]);
            _fabricationKeyFilters[button] = node.GetComponent<MeshFilter>();
            yield return false;
        }
        _machineSolids.Add(new(frame.localPosition, frame.localRotation, new(-.568f, .81f, .0125f), new(.114f, 1.48f, .865f)));
        _machineSolids.Add(new(frame.localPosition, frame.localRotation, new(.568f, .81f, .0125f), new(.114f, 1.48f, .865f)));
        _machineSolids.Add(new(frame.localPosition, frame.localRotation, new(0, .115f, 0), new(1.02f, .13f, .76f)));
        _machineSolids.Add(new(frame.localPosition, frame.localRotation, new(0, .78f, -.345f), new(1.02f, 1.27f, .07f)));
        // Raised keys and recessed feed openings remain ahead of their backing solids.
        _machineSolids.Add(new(frame.localPosition, frame.localRotation, new(0, .87f, .366f), new(1.02f, .255f, .164f)));
        _machineSolids.Add(new(frame.localPosition, frame.localRotation, new(0, 1.49f, 0), new(1.04f, .12f, .70f)));
        _machineSolids.Add(new(frame.localPosition, frame.localRotation, new(0, 1.105f, .354f), new(1.01f, .11f, .18f)));
        _machineSolids.Add(new(frame.localPosition, frame.localRotation, new(0, 1.443f, .354f), new(1.01f, .074f, .18f)));
        _machineSolids.Add(new(frame.localPosition, frame.localRotation, new(0, 1.27f, .427f), new(.505f, .245f, .014f)));
        _machineSolids.Add(new(frame.localPosition, frame.localRotation, new(-.397f, 1.284f, .326f), new(.23f, .27f, .012f)));
        _machineSolids.Add(new(frame.localPosition, frame.localRotation, new(.397f, 1.284f, .326f), new(.23f, .27f, .012f)));
        _machineSolids.Add(new(frame.localPosition, frame.localRotation, new(0, .692f, .015f), new(.98f, .035f, .66f)));
        _machineSolids.Add(new(frame.localPosition, frame.localRotation, new(0, .303f, .02f), new(.98f, .025f, .64f)));
        _machineSolids.Add(new(frame.localPosition, frame.localRotation, new(0, .5f, .407f), new(.95f, .335f, .005f)));
    }

    private void UpdateFabricationDetail(Vector3 eye)
    {
        if (_fabricationFrame == null) return;
        bool detail = (_fabricationFrame.localPosition - eye).sqrMagnitude < 20 * 20;
        if (_detailIncomplete || _fabricationDetail != detail)
        {
            for (int part = 0; part < _fabricationFilters.Length; part++)
                _fabricationFilters[part].sharedMesh = _fabricationMeshes[part, detail ? 0 : 1];
            foreach (var filter in _fabricationKeyFilters) filter.sharedMesh = _fabricationMeshes[6, detail ? 0 : 1];
            _fabricationDetail = detail;
        }
        float step = Math.Clamp(Time.unscaledDeltaTime, 0, .1f) * .04f;
        for (int button = 0; button < _fabricationKeys.Length; button++)
        {
            float target = Time.unscaledTime < _fabricationPressUntil[button] ? .003f : 0;
            float amount = Mathf.MoveTowards(_fabricationKeyAmounts[button], target, step);
            if (!_detailIncomplete && amount == _fabricationKeyAmounts[button]) continue;
            _fabricationKeys[button].localPosition = FabricationButtonCenter(
                (BoxStationMaterialControl)((int)BoxStationMaterialControl.LoadFabricator + button)) - Vector3.forward * amount;
            _fabricationKeyAmounts[button] = amount;
        }
    }

    private IEnumerable<bool> BuildSpareMaterials()
    {
        for (int kind = 0; kind < 2; kind++)
        {
            int materialPart = 7 + kind;
            for (int lod = 0; lod < 2; lod++)
            {
                var vertices = new List<Vector3>();
                for (int part = 0; part < SpareParts.Length; part++)
                {
                    string name = "station-" + (kind == 0 ? "line" : "circuit") + "-spare-" + SpareParts[part] + (lod == 0 ? "" : "-lod");
                    var data = _parts[name];
                    _spareMeshes[kind, part, lod] = CreateMesh(data);
                    vertices.AddRange(data.Vertices);
                    yield return false;
                }
                _materialMeshes[materialPart, lod] = _spareMeshes[kind, 0, lod];
                _materialContactVertices[materialPart * 2 + lod] = vertices.ToArray();
                var bounds = new Bounds(vertices[0], Vector3.zero);
                foreach (var vertex in vertices) bounds.Encapsulate(vertex);
                if (lod == 0) _materialContactBounds[materialPart] = bounds;
                float offset = -bounds.min.y;
                _materialBaseOffsets[materialPart] = lod == 0 ? offset : Math.Max(_materialBaseOffsets[materialPart], offset);
            }
        }
    }

    private void LoadFabricationMaterials(Shader shader)
    {
        _fabricationMaterials[0] = FabricationMaterial(shader, "Autolathe casing", new(.115f, .14f, .155f, 1), .22f, .42f, ClientDeviceSurfaceFinish.Surface.Polymer);
        _fabricationMaterials[1] = FabricationMaterial(shader, "Autolathe hardware", new(.39f, .43f, .46f, 1), .85f, .55f, ClientDeviceSurfaceFinish.Surface.Metal);
        _fabricationMaterials[2] = FabricationMaterial(shader, "Autolathe seals", new(.018f, .024f, .027f, 1), 0, .24f, ClientDeviceSurfaceFinish.Surface.Rubber);
        _fabricationMaterials[3] = new Material(_glass!) { name = "Autolathe glazing" };
        _fabricationOwnedMaterials.Add(_fabricationMaterials[3]);
        _fabricationMaterials[4] = FabricationMaterial(shader, "Autolathe markings", new(.62f, .285f, .045f, 1), .12f, .4f, ClientDeviceSurfaceFinish.Surface.Polymer);
        _fabricationMaterials[5] = FabricationMaterial(shader, "Autolathe interior", new(.045f, .058f, .065f, 1), .6f, .4f, ClientDeviceSurfaceFinish.Surface.Metal);
        _fabricationMaterials[6] = FabricationMaterial(shader, "Autolathe keys", new(.095f, .12f, .14f, 1), .1f, .27f, ClientDeviceSurfaceFinish.Surface.Polymer);
        for (int kind = 0; kind < 2; kind++)
        {
            _spareMaterials[kind, 0] = FabricationMaterial(shader, kind == 0 ? "Line kit case" : "Circuit kit case",
                kind == 0 ? new(.055f, .14f, .16f, 1) : new(.16f, .13f, .085f, 1), .22f, .42f, ClientDeviceSurfaceFinish.Surface.Polymer);
            _spareMaterials[kind, 1] = _fabricationMaterials[1];
            _spareMaterials[kind, 2] = _fabricationMaterials[2];
            _spareMaterials[kind, 3] = FabricationMaterial(shader, kind == 0 ? "Line kit fittings" : "Circuit kit components",
                kind == 0 ? new(.08f, .30f, .32f, 1) : new(.045f, .23f, .12f, 1), 0, .3f,
                kind == 0 ? ClientDeviceSurfaceFinish.Surface.Rubber : ClientDeviceSurfaceFinish.Surface.Polymer);
            _spareMaterials[kind, 4] = FabricationMaterial(shader, kind == 0 ? "Line kit identification" : "Circuit kit identification",
                kind == 0 ? new(.12f, .50f, .50f, 1) : new(.65f, .32f, .06f, 1), .12f, .4f, ClientDeviceSurfaceFinish.Surface.Polymer);
        }
    }

    private Material FabricationMaterial(Shader shader, string name, Color color, float metallic, float smoothness,
        ClientDeviceSurfaceFinish.Surface surface)
    {
        var material = new Material(shader) { name = name, enableInstancing = true };
        _fabricationOwnedMaterials.Add(material);
        material.SetColor("_BaseColor", color);
        material.SetFloat("_Metallic", metallic);
        material.SetFloat("_Smoothness", smoothness);
        _fabricationFinish.Apply(material, surface);
        return material;
    }

    private void ReleaseFabricationMachinery()
    {
        _fabricationFrame = null;
        Array.Clear(_fabricationKeys);
        Array.Clear(_fabricationFilters);
        Array.Clear(_fabricationKeyFilters);
        _fabricationMeshes = new Mesh[7, 2];
        _spareMeshes = new Mesh[2, 5, 2];
        foreach (var material in _fabricationOwnedMaterials) if (material) UnityEngine.Object.Destroy(material);
        _fabricationOwnedMaterials.Clear();
        Array.Clear(_fabricationMaterials);
        Array.Clear(_spareMaterials);
        _fabricationFinish.Dispose();
    }
}
