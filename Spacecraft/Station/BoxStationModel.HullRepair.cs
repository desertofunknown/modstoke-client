// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class BoxStationModel
{
    internal static readonly Vector3 RepairCarryGrip = new(.135f, .025f, 0);
    private const float RepairCullDistance = 36, RepairDetailDistance = 12;
    private ImmutableArray<BoxStationRepairSource> _repairSources = ImmutableArray<BoxStationRepairSource>.Empty;
    private RepairVisual?[] _repairStocks = Array.Empty<RepairVisual?>(), _repairPacks = Array.Empty<RepairVisual?>();
    private readonly List<int> _repairHeldPacks = new();
    private Mesh[,] _repairMeshes = new Mesh[2, 2];
    private Material? _repairMaterial;
    private BoxStationRepairState? _repairState;
    private bool _repairStateComplete;
    private Vector3 _repairDetailEye;
    private float _repairDetailTime;
    private bool _repairDetailDirty;

    private IEnumerable<bool> BuildHullRepair()
    {
        string[] parts = { "repair-sheet", "repair-tile" };
        for (int part = 0; part < parts.Length; part++)
            for (int lod = 0; lod < 2; lod++)
            {
                _repairMeshes[part, lod] = CreateMesh(_parts[parts[part] + (lod == 0 ? "" : "-lod")]);
                yield return false;
            }
        _repairSources = BoxStationHullRepair.GetSources(_layout);
        _repairStocks = new RepairVisual?[_repairSources.Length];
        _repairPacks = new RepairVisual?[BoxStationHullRepair.MaxPacks(_layout)];
        for (int i = 0; i < _repairSources.Length; i++)
        {
            var source = _repairSources[i];
            if (source.Sheets == 0) continue;
            var visual = _repairStocks[i] = CreateRepairVisual("Metal sheet stock " + i, 0, 6, .013f);
            SetRepairPosition(visual, new(source.Position.X, source.Position.Y, source.Position.Z), RepairSourceRotation(i));
            yield return false;
        }
    }

    internal Vector3 RepairStockPoint(int source)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)source >= _repairSources.Length) throw new ArgumentOutOfRangeException(nameof(source));
        var position = _repairSources[source].Position;
        int sheets = _repairState?.Sheets[source] ?? 0;
        float height = sheets > 0 ? (Math.Min(sheets, 6) - 1) * .013f + .012f : 0;
        return Root.transform.TransformPoint(new Vector3(position.X, position.Y + height, position.Z));
    }

    internal Vector3 RepairPackPoint(int pack)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_repairState is not { } state || (uint)pack >= state.Packs.Length)
            throw new ArgumentOutOfRangeException(nameof(pack));
        var item = state.Packs[pack];
        if (item.Place == BoxStationRepairPlace.Stored)
        {
            if (StoredPortablePose(item.Storage, item.Slot, _storedEquipmentBaseOffsets[7], out var position, out var rotation))
                return Root.transform.TransformPoint(position + rotation * new Vector3(0, Math.Max(0, item.Tiles - 1) * .019f + .018f, 0));
            throw new InvalidOperationException("The stored repair tiles are not visible.");
        }
        if (item.Place == BoxStationRepairPlace.Held && _repairPacks[pack] is { Active: true } held)
            return held.Frame.TransformPoint(RepairCarryGrip);
        return Root.transform.TransformPoint(new Vector3(item.Position.X,
            item.Position.Y + Math.Max(0, item.Tiles - 1) * .019f + .018f, item.Position.Z));
    }

    private void UpdateHullRepair(BoxStationRepairState? state)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        bool current = state is { } snapshot && snapshot.LayoutHash == _layout.LayoutHash
            && !snapshot.Sheets.IsDefault && snapshot.Sheets.Length == _repairSources.Length
            && !snapshot.Packs.IsDefault && snapshot.Packs.Length <= _repairPacks.Length;
        var next = current ? state : null;
        // The single hands coordinator must renew every held projection after state freshness is checked.
        foreach (int pack in _repairHeldPacks)
            if (_repairPacks[pack] is { } held) SetRepairVisible(held, false);
        if (_repairStateComplete && _repairState == next) return;
        var previous = _repairState;
        _repairStateComplete = false;
        _repairHeldPacks.Clear();
        _repairDetailDirty = true;
        for (int source = 0; source < _repairStocks.Length; source++)
        {
            if (_repairStocks[source] is not { } stock) continue;
            SetRepairLayers(stock, current ? Math.Min((int)next!.Value.Sheets[source], 6) : 0);
            if (stock.Layers == 0) SetRepairVisible(stock, false);
        }
        int count = Math.Max(previous?.Packs.Length ?? 0, next?.Packs.Length ?? 0);
        for (int pack = 0; pack < count; pack++)
        {
            var visual = _repairPacks[pack];
            if (!current)
            {
                if (visual != null) SetRepairVisible(visual, false);
                continue;
            }
            if (pack >= next!.Value.Packs.Length || next.Value.Packs[pack].Place == BoxStationRepairPlace.Spent)
            {
                ReleaseRepairPack(pack);
                continue;
            }
            var item = next.Value.Packs[pack];
            if (item.Place == BoxStationRepairPlace.Held) _repairHeldPacks.Add(pack);
            if (visual == null) continue;
            SetRepairLayers(visual, item.Tiles);
            if (item.Place == BoxStationRepairPlace.Held) SetRepairVisible(visual, false);
            else if (item.Place == BoxStationRepairPlace.Stored)
            {
                if (StoredPortablePose(item.Storage, item.Slot, _storedEquipmentBaseOffsets[7], out var position, out var rotation))
                    SetRepairPosition(visual, position, rotation);
                else SetRepairVisible(visual, false);
            }
            else
                SetRepairPosition(visual, new(item.Position.X, item.Position.Y, item.Position.Z),
                    item.Place == BoxStationRepairPlace.Source ? RepairSourceRotation(item.Source) : Quaternion.identity);
        }
        _repairState = next;
        _repairStateComplete = true;
    }

    internal void SetHeldRepairPose(int pack, Vector3 position, Quaternion rotation, bool visible)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)pack >= _repairPacks.Length) throw new ArgumentOutOfRangeException(nameof(pack));
        // A retiring pose cannot hide a bundle that authority has already stored, dropped or consumed.
        if (!_portablesCurrent || _repairState is not { } state || pack >= state.Packs.Length
            || state.Packs[pack].Place != BoxStationRepairPlace.Held) return;
        var visual = _repairPacks[pack];
        if (visible)
        {
            if (!Finite(position) || !float.IsFinite(rotation.x) || !float.IsFinite(rotation.y)
                || !float.IsFinite(rotation.z) || !float.IsFinite(rotation.w)
                || Quaternion.Dot(rotation, rotation) is < .5f or > 1.5f)
                throw new ArgumentException("The held repair bundle projection is invalid.");
            visual ??= _repairPacks[pack] = CreateRepairVisual("Hull repair tiles " + pack, 1, 4, .019f);
            SetRepairLayers(visual, state.Packs[pack].Tiles);
            SetRepairDetail(visual, true);
            visual.Frame.SetPositionAndRotation(position, rotation.normalized);
            visual.Positioned = false;
        }
        if (visual != null) SetRepairVisible(visual, visible);
    }

    internal bool TryRepairTarget(Ray worldRay, float reach, out int target, out Vector3 worldContact)
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
            int cell = _layout.CellAt(contact), panel = cell * 2 + layer;
            if (cell < 0 || !BoxStationHull.ValidPanelTarget(_layout, panel)) continue;
            var offset = contact - _layout.CellPosition(cell);
            if (MathF.Abs(offset.x) >= HullApertureHalf || MathF.Abs(offset.z) >= HullApertureHalf) continue;
            nearest = distance;
            target = _hull.Integrity(panel) < BoxStationHull.Capacity || _hull.Foam(cell, layer != 0) ? panel : -1;
        }
        if (target < 0) return false;
        worldContact = worldRay.origin + direction * nearest;
        return Finite(worldContact);
    }

    private void UpdateHullRepairDetail(Vector3 eye)
    {
        if (_repairState is not { } state) return;
        if (!_detailIncomplete && !_repairDetailDirty && Time.unscaledTime < _repairDetailTime
            && (eye - _repairDetailEye).sqrMagnitude < 1) return;
        _repairDetailDirty = false;
        _repairDetailEye = eye;
        _repairDetailTime = Time.unscaledTime + .2f;
        for (int source = 0; source < _repairStocks.Length; source++)
        {
            if (_repairStocks[source] is not { } stock || stock.Layers == 0) continue;
            float distance = (stock.Position - eye).sqrMagnitude;
            SetRepairVisible(stock, distance < RepairCullDistance * RepairCullDistance);
            if (stock.Active) SetRepairDetail(stock, distance < RepairDetailDistance * RepairDetailDistance);
        }
        int budget = 4;
        for (int pack = 0; pack < state.Packs.Length; pack++)
        {
            var item = state.Packs[pack];
            if (item.Place is BoxStationRepairPlace.Held or BoxStationRepairPlace.Spent) continue;
            var position = new Vector3(item.Position.X, item.Position.Y, item.Position.Z);
            var rotation = item.Place == BoxStationRepairPlace.Source ? RepairSourceRotation(item.Source) : Quaternion.identity;
            if (item.Place == BoxStationRepairPlace.Stored && !StoredPortablePose(item.Storage, item.Slot,
                _storedEquipmentBaseOffsets[7], out position, out rotation))
            {
                if (_repairPacks[pack] is { } stored) SetRepairVisible(stored, false);
                continue;
            }
            float distance = (position - eye).sqrMagnitude;
            if (distance >= RepairCullDistance * RepairCullDistance)
            {
                ReleaseRepairPack(pack);
                continue;
            }
            var visual = _repairPacks[pack];
            if (visual == null)
            {
                if (budget == 0) { _repairDetailDirty = true; continue; }
                budget--;
                visual = _repairPacks[pack] = CreateRepairVisual("Hull repair tiles " + pack, 1, 4, .019f);
            }
            SetRepairLayers(visual, item.Tiles);
            SetRepairPosition(visual, position, rotation);
            SetRepairDetail(visual, distance < RepairDetailDistance * RepairDetailDistance);
            SetRepairVisible(visual, true);
        }
    }

    private RepairVisual CreateRepairVisual(string name, int part, int layers, float pitch)
    {
        var frame = Node(name, Root.transform);
        frame.gameObject.SetActive(false);
        try
        {
            var filters = new MeshFilter[layers];
            for (int layer = 0; layer < layers; layer++)
            {
                var node = Node("Layer " + (layer + 1), frame);
                node.localPosition = Vector3.up * (layer * pitch);
                Render(node, _repairMeshes[part, 1], _repairMaterial!);
                filters[layer] = node.GetComponent<MeshFilter>();
                node.gameObject.SetActive(false);
            }
            // Portable stock follows authority and hand poses, not a separate local rigidbody simulation.
            return new(frame, filters, part);
        }
        catch { UnityEngine.Object.Destroy(frame.gameObject); throw; }
    }

    private Quaternion RepairSourceRotation(int source)
    {
        int direction = _layout.Objects[_repairSources[source].Object].Direction;
        float angle = direction switch { 1 => 0, 5 => 45, 4 => 90, 6 => 135, 0 or 2 => 180,
            10 => 225, 8 => 270, 9 => 315, _ => 0 };
        return Quaternion.Euler(0, angle, 0);
    }

    private static void SetRepairPosition(RepairVisual visual, Vector3 position, Quaternion rotation)
    {
        if (visual.Positioned && visual.Position == position && visual.Rotation == rotation) return;
        visual.Positioned = false;
        visual.Frame.localPosition = position;
        visual.Frame.localRotation = rotation;
        visual.Position = position;
        visual.Rotation = rotation;
        visual.Positioned = true;
    }

    private static void SetRepairVisible(RepairVisual visual, bool visible)
    {
        if (visual.Active == visible) return;
        visual.Frame.gameObject.SetActive(visible);
        visual.Active = visible;
    }

    private static void SetRepairLayers(RepairVisual visual, int layers)
    {
        // Retain the completed prefix if a later layer fails or the next snapshot changes quantity.
        while (visual.Layers > layers)
        {
            visual.Filters[visual.Layers - 1].gameObject.SetActive(false);
            visual.Layers--;
        }
        while (visual.Layers < layers)
        {
            visual.Filters[visual.Layers].gameObject.SetActive(true);
            visual.Layers++;
        }
    }

    private void SetRepairDetail(RepairVisual visual, bool detail)
    {
        if (!_detailIncomplete && visual.Detail == detail) return;
        foreach (var filter in visual.Filters) filter.sharedMesh = _repairMeshes[visual.Part, detail ? 0 : 1];
        visual.Detail = detail;
    }

    private void ReleaseRepairPack(int pack)
    {
        if (_repairPacks[pack] is not { } visual) return;
        SetRepairVisible(visual, false);
        UnityEngine.Object.Destroy(visual.Frame.gameObject);
        _repairPacks[pack] = null;
    }

    private void LoadRepairMaterial(Shader shader)
    {
        _repairMaterial = new Material(shader) { name = "Hull repair stock finish", enableInstancing = true };
        _repairMaterial.SetColor("_BaseColor", Color.white);
        _repairMaterial.SetTexture("_BaseColorMap", LoadTexture("repair-base", false));
        _repairMaterial.SetTexture("_MaskMap", LoadTexture("repair-mask", true));
        _repairMaterial.SetTexture("_NormalMap", LoadTexture("repair-normal", true, true));
        _repairMaterial.SetFloat("_NormalMapSpace", 0); _repairMaterial.SetFloat("_NormalScale", 1);
        _repairMaterial.SetFloat("_MetallicRemapMin", 0); _repairMaterial.SetFloat("_MetallicRemapMax", 1);
        _repairMaterial.SetFloat("_SmoothnessRemapMin", 0); _repairMaterial.SetFloat("_SmoothnessRemapMax", 1);
        _repairMaterial.SetFloat("_AORemapMin", 0); _repairMaterial.SetFloat("_AORemapMax", 1);
        _repairMaterial.EnableKeyword("_MASKMAP");
        _repairMaterial.EnableKeyword("_NORMALMAP");
        _repairMaterial.EnableKeyword("_NORMALMAP_TANGENT_SPACE");
    }

    private void ReleaseHullRepair()
    {
        _repairStocks = _repairPacks = Array.Empty<RepairVisual?>();
        _repairHeldPacks.Clear();
        _repairSources = ImmutableArray<BoxStationRepairSource>.Empty;
        _repairState = null;
        _repairStateComplete = false;
        _repairMeshes = new Mesh[2, 2];
        if (_repairMaterial) UnityEngine.Object.Destroy(_repairMaterial);
        _repairMaterial = null;
    }

    private sealed class RepairVisual(Transform frame, MeshFilter[] filters, int part)
    {
        internal readonly Transform Frame = frame;
        internal readonly MeshFilter[] Filters = filters;
        internal readonly int Part = part;
        internal Vector3 Position;
        internal Quaternion Rotation;
        internal int Layers;
        internal bool Positioned, Active, Detail;
    }
}
