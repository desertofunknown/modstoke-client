// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Modstoke.Client;

internal sealed partial class BoxStationModel
{
    internal static readonly Vector3 FoamCarryGrip = new(0, .110f, -.030f);
    private static readonly Vector3 FoamCapPivot = new(0, .216f, 0);
    private static readonly Vector3 FoamLeverPivot = new(0, .225f, -.042f);
    private const int FoamCloudCards = 5;
    private readonly MaterialPropertyBlock _foamIndicator = new(), _foamCloudProperties = new();
    private Material? _foamTool, _foamPatch, _foamCloud;
    private Mesh[,] _foamMeshes = new Mesh[4, 2];
    private Transform[] _foamFrames = Array.Empty<Transform>(), _foamLevers = Array.Empty<Transform>();
    private Transform[] _foamRackFrames = Array.Empty<Transform>();
    private MeshFilter[][] _foamFilters = Array.Empty<MeshFilter[]>();
    private MeshFilter[] _foamRackFilters = Array.Empty<MeshFilter>();
    private MeshRenderer[] _foamCaps = Array.Empty<MeshRenderer>();
    private MeshRenderer[][] _foamClouds = Array.Empty<MeshRenderer[]>();
    private Vector3[] _foamSourcePositions = Array.Empty<Vector3>();
    private bool[] _foamHeld = Array.Empty<bool>(), _foamDetail = Array.Empty<bool>(), _foamRackDetail = Array.Empty<bool>();
    private BoxStationFoamState? _foamState;
    private bool _foamCurrent;

    internal Transform FoamFrame(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)index >= _foamFrames.Length) throw new ArgumentOutOfRangeException(nameof(index));
        return _foamFrames[index];
    }

    internal Vector3 FoamControlPoint(int index) => FoamFrame(index).TransformPoint(FoamCarryGrip);

    private IEnumerable<bool> BuildFoamMachinery()
    {
        string[] parts = { "foam-canister", "foam-cap", "foam-lever", "foam-rack" };
        for (int part = 0; part < parts.Length; part++)
            for (int lod = 0; lod < 2; lod++)
            {
                _foamMeshes[part, lod] = CreateMesh(_parts[parts[part] + (lod == 0 ? "" : "-lod")]);
                yield return false;
            }

        var sources = BoxStationFoam.GetItems(_layout);
        _foamFrames = new Transform[sources.Length];
        _foamLevers = new Transform[sources.Length];
        _foamFilters = new MeshFilter[sources.Length][];
        _foamCaps = new MeshRenderer[sources.Length];
        _foamClouds = new MeshRenderer[sources.Length][];
        _foamSourcePositions = new Vector3[sources.Length];
        _foamHeld = new bool[sources.Length];
        _foamDetail = new bool[sources.Length];
        var sourceCells = new HashSet<int>();
        var cloudMesh = CreateFoamCloudMesh();
        for (int i = 0; i < sources.Length; i++)
        {
            var source = sources[i];
            sourceCells.Add(source.Cell);
            _foamSourcePositions[i] = new(source.Position.X, source.Position.Y, source.Position.Z);
            var frame = _foamFrames[i] = Node("Hull foam canister " + i, Root.transform);
            frame.localPosition = _foamSourcePositions[i];
            var filters = _foamFilters[i] = new MeshFilter[3];
            filters[0] = FoamPart("Canister body", frame, 0, Vector3.zero);
            filters[1] = FoamPart("Canister cap", frame, 1, FoamCapPivot);
            filters[2] = FoamPart("Canister lever", frame, 2, FoamLeverPivot);
            _foamCaps[i] = filters[1].GetComponent<MeshRenderer>();
            _foamLevers[i] = filters[2].transform;
            // Custody and deployment own the pose; portable items have no station-owned collider.
            frame.gameObject.SetActive(false);
            var clouds = _foamClouds[i] = new MeshRenderer[FoamCloudCards];
            for (int card = 0; card < clouds.Length; card++)
            {
                var node = Node("Expanding hull foam", Root.transform);
                Render(node, cloudMesh, _foamCloud!);
                var renderer = clouds[card] = node.GetComponent<MeshRenderer>();
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                renderer.lightProbeUsage = LightProbeUsage.Off;
                renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
                node.gameObject.SetActive(false);
            }
            yield return false;
        }

        var racks = new List<Transform>();
        var rackFilters = new List<MeshFilter>();
        int scanned = 0;
        foreach (var source in _layout.Objects)
        {
            if (++scanned % 64 == 0) yield return false;
            if (!sourceCells.Contains(source.Cell) || _layout.Types[source.Type] != "/obj/structure/rack") continue;
            var frame = Node("Hull foam rack " + source.Id, Root.transform);
            frame.localPosition = _layout.CellPosition(source.Cell);
            racks.Add(frame);
            rackFilters.Add(FoamPart("Rack", frame, 3, Vector3.zero));
            AddSolid(frame, new(0, 1.035f, 0), new(1.6f, .05f, 1.2f));
            for (int corner = 0; corner < 4; corner++)
                AddSolid(frame, new((corner & 1) == 0 ? -.75f : .75f, .505f,
                    (corner & 2) == 0 ? -.55f : .55f), new(.06f, 1.01f, .06f));
            yield return false;
        }
        _foamRackFrames = racks.ToArray();
        _foamRackFilters = rackFilters.ToArray();
        _foamRackDetail = new bool[racks.Count];
    }

    private MeshFilter FoamPart(string name, Transform parent, int part, Vector3 position)
    {
        var node = Node(name, parent);
        node.localPosition = position;
        Render(node, _foamMeshes[part, 1], _foamTool!);
        return node.GetComponent<MeshFilter>();
    }

    private void UpdateFoam(BoxStationFoamState? state)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        bool current = state is { } snapshot && snapshot.LayoutHash == _layout.LayoutHash
            && !snapshot.Items.IsDefault && snapshot.Items.Length == _foamFrames.Length;
        _foamCurrent = false;
        for (int i = 0; i < _foamFrames.Length; i++)
        {
            var frame = _foamFrames[i];
            _foamHeld[i] = false;
            bool visible = false, armed = false, deployed = false;
            if (current)
            {
                var item = state!.Value.Items[i];
                armed = item.Place == BoxStationFoamPlace.Armed;
                if (item.Place == BoxStationFoamPlace.Rack)
                {
                    frame.localPosition = _foamSourcePositions[i];
                    frame.localRotation = Quaternion.identity;
                    visible = true;
                }
                else if (item.Place == BoxStationFoamPlace.Dropped)
                {
                    frame.localPosition = new(item.Position.X, item.Position.Y, item.Position.Z);
                    frame.localRotation = Quaternion.identity;
                    visible = true;
                }
                else if (item.Place == BoxStationFoamPlace.Held) _foamHeld[i] = true;
                else if (item.Place == BoxStationFoamPlace.Stored
                    && StoredPortablePose(item.Storage, item.Slot, _storedEquipmentBaseOffsets[6], out var position, out var rotation))
                {
                    frame.localPosition = position;
                    frame.localRotation = rotation;
                    visible = true;
                }
                else if (item.Place is BoxStationFoamPlace.Armed or BoxStationFoamPlace.Foaming or BoxStationFoamPlace.Spent
                    && BoxStationHull.ValidPanelTarget(_layout, item.Target))
                {
                    frame.localPosition = new(item.Position.X, item.Position.Y, item.Position.Z);
                    frame.localRotation = (item.Target & 1) != 0 ? Quaternion.Euler(180, 0, 0) : Quaternion.identity;
                    visible = deployed = true;
                }
            }
            // Hands must supply a fresh pose after every authoritative state update.
            if (frame.gameObject.activeSelf != visible) frame.gameObject.SetActive(visible);
            _foamLevers[i].localRotation = deployed ? Quaternion.Euler(-70, 0, 0) : Quaternion.identity;
            float pulse = armed ? .8f + .6f * Mathf.Sin(Time.unscaledTime * 12) : 0;
            _foamIndicator.SetColor("_EmissiveColor", new Color(1, .18f, .025f) * pulse);
            _foamCaps[i].SetPropertyBlock(_foamIndicator);
            if (!current || state!.Value.Items[i].Place != BoxStationFoamPlace.Foaming)
                foreach (var cloud in _foamClouds[i]) cloud.gameObject.SetActive(false);
        }
        _foamState = current ? state : null;
        _foamCurrent = current;
    }

    internal void SetHeldFoamPose(int item, Vector3 position, Quaternion rotation, bool visible)
    {
        var frame = FoamFrame(item);
        // A retiring hand pose cannot hide a canister already stored, dropped or deployed.
        if (!_portablesCurrent || !_foamCurrent || !_foamHeld[item]) return;
        if (visible)
        {
            if (!Finite(position) || !float.IsFinite(rotation.x) || !float.IsFinite(rotation.y)
                || !float.IsFinite(rotation.z) || !float.IsFinite(rotation.w)
                || Quaternion.Dot(rotation, rotation) is < .5f or > 1.5f)
                throw new ArgumentException("The held foam canister projection is invalid.");
            frame.SetPositionAndRotation(position, rotation.normalized);
        }
        if (frame.gameObject.activeSelf != visible) frame.gameObject.SetActive(visible);
    }

    internal bool TryFoamTarget(Ray worldRay, float reach, out int target, out Vector3 worldContact)
    {
        target = -1;
        worldContact = default;
        if (!Visible || !Finite(worldRay.origin) || !Finite(worldRay.direction) || !float.IsFinite(reach) || reach <= 0) return false;
        float lengthSquared = worldRay.direction.sqrMagnitude;
        if (!float.IsFinite(lengthSquared) || lengthSquared < .000001f) return false;
        var direction = worldRay.direction / Mathf.Sqrt(lengthSquared);
        var origin = Root.transform.InverseTransformPoint(worldRay.origin);
        var localDirection = Root.transform.InverseTransformDirection(direction);
        if (!Finite(origin) || !Finite(localDirection) || MathF.Abs(localDirection.y) < .00001f) return false;
        float nearest = reach;
        for (int layer = 0; layer < 2; layer++)
        {
            float distance = ((layer == 0 ? 0 : BoxStationLayout.DeckHeight) - origin.y) / localDirection.y;
            if (!float.IsFinite(distance) || distance < 0 || distance > nearest) continue;
            var contact = origin + localDirection * distance;
            if (!Finite(contact)) continue;
            int cell = _layout.CellAt(contact);
            if (cell < 0 || !InHullAperture(cell, contact, layer != 0)) continue;
            target = cell * 2 + layer;
            nearest = distance;
        }
        if (target < 0) return false;
        worldContact = worldRay.origin + direction * nearest;
        return Finite(worldContact);
    }

    private void UpdateFoamDetail(Vector3 eye)
    {
        for (int i = 0; i < _foamRackFrames.Length; i++)
        {
            bool detail = (_foamRackFrames[i].localPosition - eye).sqrMagnitude < 20 * 20;
            if (!_detailIncomplete && _foamRackDetail[i] == detail) continue;
            _foamRackFilters[i].sharedMesh = _foamMeshes[3, detail ? 0 : 1];
            _foamRackDetail[i] = detail;
        }
        for (int i = 0; i < _foamFrames.Length; i++)
        {
            var frame = _foamFrames[i];
            if (frame.gameObject.activeSelf)
            {
                bool detail = (frame.localPosition - eye).sqrMagnitude < 20 * 20;
                if (_detailIncomplete || _foamDetail[i] != detail)
                {
                    for (int part = 0; part < _foamFilters[i].Length; part++)
                        _foamFilters[i][part].sharedMesh = _foamMeshes[part, detail ? 0 : 1];
                    _foamDetail[i] = detail;
                }
            }
            DrawFoamCloud(i, eye);
        }
    }

    private void DrawFoamCloud(int index, Vector3 eye)
    {
        var item = _foamState?.Items[index] ?? default;
        bool visible = _foamCurrent && item.Place == BoxStationFoamPlace.Foaming
            && BoxStationHull.ValidPanelTarget(_layout, item.Target) && item.RemainingSeconds > 0;
        var anchor = new Vector3(item.Position.X, item.Position.Y, item.Position.Z);
        visible &= (anchor - eye).sqrMagnitude < 24 * 24;
        float growth = visible ? Mathf.Clamp01(1 - item.RemainingSeconds / BoxStationFoam.FoamSeconds) : 0;
        float side = (item.Target & 1) == 0 ? 1 : -1;
        for (int card = 0; card < _foamClouds[index].Length; card++)
        {
            var renderer = _foamClouds[index][card];
            if (renderer.gameObject.activeSelf != visible) renderer.gameObject.SetActive(visible);
            if (!visible) continue;
            float phase = Mathf.Repeat(Time.unscaledTime * .7f + card * .19f, 1);
            float angle = card * 2.39996f;
            float spread = (.08f + growth * .3f) * phase;
            var position = anchor + new Vector3(Mathf.Cos(angle) * spread, side * (.12f + phase * .32f),
                Mathf.Sin(angle) * spread);
            var toward = eye - position;
            renderer.transform.localPosition = position;
            renderer.transform.localRotation = toward.sqrMagnitude > .0001f ? Quaternion.LookRotation(toward) : Quaternion.identity;
            renderer.transform.localScale = Vector3.one * (.16f + growth * .35f + phase * .2f);
            _foamCloudProperties.SetColor("_BaseColor", new Color(.78f, .82f, .76f, Mathf.Sin(phase * Mathf.PI) * .38f));
            renderer.SetPropertyBlock(_foamCloudProperties);
        }
    }

    private Mesh CreateFoamCloudMesh()
    {
        var mesh = new Mesh { name = "Hull foam cloud card" };
        _meshes.Add(mesh);
        mesh.vertices = new[] { new Vector3(-.5f, -.5f, 0), new Vector3(.5f, -.5f, 0),
            new Vector3(.5f, .5f, 0), new Vector3(-.5f, .5f, 0) };
        mesh.normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward };
        mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
        mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
        mesh.RecalculateBounds();
        mesh.UploadMeshData(true);
        return mesh;
    }

    private void LoadFoamMaterials(Shader shader)
    {
        _foamTool = new Material(shader) { name = "Hull foam equipment finish", enableInstancing = true };
        SetFoamMaterial(_foamTool, "foam-tool");
        _foamPatch = new Material(shader) { name = "Hardened hull foam", enableInstancing = true };
        SetFoamMaterial(_foamPatch, "foam-patch");
        _foamCloud = new Material(_glass!) { name = "Expanding hull foam mist" };
        _foamCloud.SetTexture("_BaseColorMap", LoadTexture("fire-steam", false, width: 256, height: 256));
        _foamCloud.SetColor("_BaseColor", Color.clear);
        _foamCloud.SetFloat("_Metallic", 0); _foamCloud.SetFloat("_Smoothness", 0);
        _foamCloud.SetFloat("_EnableBlendModePreserveSpecularLighting", 0);
    }

    private void SetFoamMaterial(Material material, string name)
    {
        material.SetColor("_BaseColor", Color.white);
        material.SetTexture("_BaseColorMap", LoadTexture(name + "-base", false));
        material.SetTexture("_MaskMap", LoadTexture(name + "-mask", true));
        material.SetTexture("_NormalMap", LoadTexture(name + "-normal", true, true));
        material.SetFloat("_NormalMapSpace", 0); material.SetFloat("_NormalScale", 1);
        material.SetFloat("_MetallicRemapMin", 0); material.SetFloat("_MetallicRemapMax", 1);
        material.SetFloat("_SmoothnessRemapMin", 0); material.SetFloat("_SmoothnessRemapMax", 1);
        material.SetFloat("_AORemapMin", 0); material.SetFloat("_AORemapMax", 1);
        material.EnableKeyword("_MASKMAP");
        material.EnableKeyword("_NORMALMAP");
        material.EnableKeyword("_NORMALMAP_TANGENT_SPACE");
    }

    private void ReleaseFoamMachinery()
    {
        _foamFrames = _foamLevers = _foamRackFrames = Array.Empty<Transform>();
        _foamFilters = Array.Empty<MeshFilter[]>();
        _foamRackFilters = Array.Empty<MeshFilter>();
        _foamCaps = Array.Empty<MeshRenderer>();
        _foamClouds = Array.Empty<MeshRenderer[]>();
        _foamSourcePositions = Array.Empty<Vector3>();
        _foamHeld = _foamDetail = _foamRackDetail = Array.Empty<bool>();
        _foamMeshes = new Mesh[4, 2];
        _foamState = null;
        _foamCurrent = false;
        if (_foamTool) UnityEngine.Object.Destroy(_foamTool);
        if (_foamPatch) UnityEngine.Object.Destroy(_foamPatch);
        if (_foamCloud) UnityEngine.Object.Destroy(_foamCloud);
        _foamTool = _foamPatch = _foamCloud = null;
    }
}
