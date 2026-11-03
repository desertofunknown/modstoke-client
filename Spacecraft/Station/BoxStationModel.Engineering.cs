// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class BoxStationModel
{
    private const float EngineeringCullDistance = 36, EngineeringDetailDistance = 12;
    private ImmutableArray<BoxStationToolSource> _engineeringSources = ImmutableArray<BoxStationToolSource>.Empty;
    private EngineeringVisual?[] _engineeringTools = Array.Empty<EngineeringVisual?>();
    private Mesh[,] _engineeringMeshes = new Mesh[5, 2];
    private BoxStationEngineeringState? _engineeringState;
    private bool _engineeringStateComplete;
    private Vector3 _engineeringDetailEye;
    private float _engineeringDetailTime;
    private bool _engineeringDetailDirty;

    internal BoxStationMaterialState? MaterialState => _portablesCurrent && !_portableUpdatePending ? _materialState : null;
    internal BoxStationEngineeringState? EngineeringState => _portablesCurrent && !_portableUpdatePending ? _engineeringState : null;

    internal static Vector3 EngineeringCarryGrip(BoxStationToolKind kind) => kind switch
    {
        BoxStationToolKind.Screwdriver => new(.010f, .036f, -.085f),
        BoxStationToolKind.Crowbar => new(.010f, .033f, -.180f),
        BoxStationToolKind.Wirecutters => new(.010f, .028f, -.065f),
        BoxStationToolKind.Wrench => new(.010f, .033f, -.085f),
        BoxStationToolKind.Welder => new(.010f, .180f, -.075f),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    internal static Vector3 EngineeringWorkTip(BoxStationToolKind kind) => kind switch
    {
        BoxStationToolKind.Screwdriver => new(0, .0173f, .108f),
        BoxStationToolKind.Crowbar => new(0, .082f, .140f),
        BoxStationToolKind.Wirecutters => new(0, .013f, .074f),
        BoxStationToolKind.Wrench => new(0, .014f, .070f),
        BoxStationToolKind.Welder => new(0, .082f, .228f),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private IEnumerable<bool> BuildEngineering()
    {
        string[] parts = { "tool-screwdriver", "tool-crowbar", "tool-wirecutters", "tool-wrench", "tool-welder" };
        for (int part = 0; part < parts.Length; part++)
            for (int lod = 0; lod < 2; lod++)
            {
                _engineeringMeshes[part, lod] = CreateMesh(_parts[parts[part] + (lod == 0 ? "" : "-lod")]);
                yield return false;
            }
        _engineeringSources = BoxStationEngineering.GetSources(_layout);
        _engineeringTools = new EngineeringVisual?[_engineeringSources.Length];
    }

    private void UpdateEngineering(BoxStationEngineeringState? state)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var visual in _engineeringTools)
            if (visual is { Held: true }) SetEngineeringVisible(visual, false);
        if (_engineeringStateComplete && _engineeringState == state) return;
        var next = state is { } candidate && BoxStationEngineering.ValidState(_layout, candidate) ? state : null;
        _engineeringStateComplete = false;
        _engineeringDetailDirty = true;
        for (int i = 0; i < _engineeringTools.Length; i++)
        {
            if (_engineeringTools[i] is not { } visual) continue;
            if (next is not { } live) { SetEngineeringVisible(visual, false); continue; }
            var item = live.Tools[i];
            visual.Held = item.Place == BoxStationToolPlace.Held;
            if (!visual.Held && visual.Frame.parent == null) visual.Frame.SetParent(Root.transform, false);
            if (item.Place is BoxStationToolPlace.Held or BoxStationToolPlace.ShipStored) SetEngineeringVisible(visual, false);
            else if (item.Place == BoxStationToolPlace.Stored)
            {
                if (StoredPortablePose(item.Storage, item.Slot, _storedEquipmentBaseOffsets[visual.Part], out var position, out var rotation))
                {
                    visual.Frame.localPosition = position;
                    visual.Frame.localRotation = rotation;
                }
                else SetEngineeringVisible(visual, false);
            }
            else
            {
                visual.Frame.localPosition = new(item.Position.X, item.Position.Y, item.Position.Z);
                visual.Frame.localRotation = Quaternion.identity;
            }
        }
        _engineeringState = next;
        _engineeringStateComplete = true;
    }

    internal Vector3 EngineeringToolPoint(int id)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_engineeringState is not { } state || (uint)id >= state.Tools.Length)
            throw new ArgumentOutOfRangeException(nameof(id));
        var item = state.Tools[id];
        var grip = EngineeringCarryGrip(_engineeringSources[id].Kind);
        if (item.Place == BoxStationToolPlace.Stored)
        {
            if (StoredPortablePose(item.Storage, item.Slot, _storedEquipmentBaseOffsets[(int)_engineeringSources[id].Kind],
                out var position, out var rotation)) return Root.transform.TransformPoint(position + rotation * grip);
            throw new InvalidOperationException("The stored engineering tool is not visible.");
        }
        if (item.Place == BoxStationToolPlace.Held && _engineeringTools[id] is { Active: true } held)
            return held.Frame.TransformPoint(grip);
        if (item.Place == BoxStationToolPlace.ShipStored)
            throw new InvalidOperationException("The ship-stored engineering tool has no station projection.");
        return Root.transform.TransformPoint(new Vector3(item.Position.X, item.Position.Y, item.Position.Z) + grip);
    }

    internal void SetHeldEngineeringPose(int id, Vector3 position, Quaternion rotation, bool visible)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_portablesCurrent || _engineeringState is not { } state || (uint)id >= state.Tools.Length
            || state.Tools[id].Place != BoxStationToolPlace.Held) return;
        var visual = _engineeringTools[id];
        if (visible)
        {
            if (!Finite(position) || !float.IsFinite(rotation.x) || !float.IsFinite(rotation.y)
                || !float.IsFinite(rotation.z) || !float.IsFinite(rotation.w)
                || Quaternion.Dot(rotation, rotation) is < .5f or > 1.5f)
                throw new ArgumentException("The held engineering tool projection is invalid.");
            visual ??= _engineeringTools[id] = CreateEngineeringVisual(id);
            visual.Held = true;
            // A carried tool stays visible when the distant station projection is hidden.
            if (visual.Frame.parent != null) visual.Frame.SetParent(null, true);
            SetEngineeringDetail(visual, true);
            visual.Frame.SetPositionAndRotation(position, rotation.normalized);
        }
        if (visual != null) SetEngineeringVisible(visual, visible);
    }

    internal bool TryEngineeringStructureTarget(Ray worldRay, float reach, out int target, out Vector3 worldContact)
    {
        target = -1;
        worldContact = default;
        if (!Visible || _engineeringState is not { } state || state.HullRevision != _hull.State.Revision
            || !Finite(worldRay.origin) || !Finite(worldRay.direction) || worldRay.direction.sqrMagnitude < .000001f
            || !float.IsFinite(reach) || reach <= 0 || reach > 6) return false;
        var direction = worldRay.direction.normalized;
        var ray = new Ray(Root.transform.InverseTransformPoint(worldRay.origin), Root.transform.InverseTransformDirection(direction));
        int cell = _layout.CellAt(ray.origin);
        if (cell < 0) return false;
        int radius = (int)MathF.Ceiling(reach / BoxStationLayout.TileSize) + 1;
        int x = cell % _layout.Width, row = cell / _layout.Width;
        float nearest = reach;
        for (int r = Math.Max(0, row - radius); r <= Math.Min(_layout.Height - 1, row + radius); r++)
            for (int c = Math.Max(0, x - radius); c <= Math.Min(_layout.Width - 1, x + radius); c++)
                foreach (var structure in BoxStationHull.StructuresAt(_layout, r * _layout.Width + c))
                {
                    if (_hull.StructureIntegrity(structure.Id) == 0) continue;
                    int candidate = BoxStationHull.StructureTarget(_layout, structure.Id);
                    for (int plane = 0; plane < MaterialStructurePlaneCount(candidate); plane++)
                        if (MaterialStructureBounds(candidate, plane, out var bounds)
                            && !bounds.Contains(ray.origin) && bounds.IntersectRay(ray, out float distance)
                            && distance >= 0 && distance <= nearest)
                        {
                            nearest = distance;
                            target = candidate;
                        }
                }
        if (target < 0) return false;
        worldContact = worldRay.origin + direction * nearest;
        return Finite(worldContact);
    }

    internal bool TryEngineeringWallTarget(Ray worldRay, float reach, out int target, out Vector3 worldContact)
        => TryConstructionTarget(worldRay, reach, false, out target, out worldContact);

    internal bool WallWorkContact(int target, Vector3 localContact)
        => ConstructionWorkContact(target, localContact);

    internal bool TryEngineeringBundleTarget(Ray worldRay, float reach, BoxStationMaterial material,
        out int id, out Vector3 worldContact)
    {
        id = -1;
        worldContact = default;
        if (!Visible || _materialState is not { } state || state.HullRevision != _hull.State.Revision
            || !Finite(worldRay.origin) || !Finite(worldRay.direction) || worldRay.direction.sqrMagnitude < .000001f
            || !float.IsFinite(reach) || reach <= 0 || reach > 6) return false;
        var direction = worldRay.direction.normalized;
        float nearest = reach;
        foreach (var bundle in state.Bundles)
        {
            if (bundle.Place != BoxStationMaterialPlace.Dropped || bundle.Material != material) continue;
            var contact = MaterialBundlePoint(bundle.Id);
            var delta = contact - worldRay.origin;
            float distance = delta.magnitude, along = Vector3.Dot(delta, direction);
            if (distance <= .01f || distance > nearest || along <= 0
                || (delta - direction * along).sqrMagnitude > .25f * .25f) continue;
            nearest = distance;
            id = bundle.Id;
            worldContact = contact;
        }
        return id >= 0;
    }

    private void UpdateEngineeringDetail(Vector3 eye)
    {
        if (_engineeringState is not { } state) return;
        if (!_detailIncomplete && !_engineeringDetailDirty && Time.unscaledTime < _engineeringDetailTime
            && (eye - _engineeringDetailEye).sqrMagnitude < 1) return;
        _engineeringDetailDirty = false;
        _engineeringDetailEye = eye;
        _engineeringDetailTime = Time.unscaledTime + .2f;
        int budget = 6;
        for (int id = 0; id < state.Tools.Length; id++)
        {
            var item = state.Tools[id];
            if (item.Place is BoxStationToolPlace.Held or BoxStationToolPlace.ShipStored) continue;
            var position = new Vector3(item.Position.X, item.Position.Y, item.Position.Z);
            var visual = _engineeringTools[id];
            var rotation = Quaternion.identity;
            if (item.Place == BoxStationToolPlace.Stored && !StoredPortablePose(item.Storage, item.Slot,
                _storedEquipmentBaseOffsets[(int)_engineeringSources[id].Kind], out position, out rotation))
            {
                if (visual != null) SetEngineeringVisible(visual, false);
                continue;
            }
            float distance = (position - eye).sqrMagnitude;
            if (distance >= EngineeringCullDistance * EngineeringCullDistance)
            {
                if (visual != null)
                {
                    SetEngineeringVisible(visual, false);
                    UnityEngine.Object.Destroy(visual.Frame.gameObject);
                    _engineeringTools[id] = null;
                }
                continue;
            }
            if (visual == null)
            {
                if (budget == 0) { _engineeringDetailDirty = true; continue; }
                budget--;
                visual = _engineeringTools[id] = CreateEngineeringVisual(id);
            }
            visual.Held = false;
            visual.Frame.localPosition = position;
            visual.Frame.localRotation = rotation;
            SetEngineeringDetail(visual, distance < EngineeringDetailDistance * EngineeringDetailDistance);
            SetEngineeringVisible(visual, true);
        }
    }

    private EngineeringVisual CreateEngineeringVisual(int id)
    {
        int part = (int)_engineeringSources[id].Kind;
        var frame = Node("Engineering tool " + id, Root.transform);
        frame.gameObject.SetActive(false);
        try
        {
            Render(frame, _engineeringMeshes[part, 1], _surface!);
            return new(frame, frame.GetComponent<MeshFilter>(), part);
        }
        catch { UnityEngine.Object.Destroy(frame.gameObject); throw; }
    }

    private static void SetEngineeringVisible(EngineeringVisual visual, bool visible)
    {
        if (visual.Active == visible) return;
        visual.Frame.gameObject.SetActive(visible);
        visual.Active = visible;
    }

    private void SetEngineeringDetail(EngineeringVisual visual, bool detail)
    {
        if (!_detailIncomplete && visual.Detail == detail) return;
        visual.Filter.sharedMesh = _engineeringMeshes[visual.Part, detail ? 0 : 1];
        visual.Detail = detail;
    }

    private void ReleaseEngineering()
    {
        foreach (var visual in _engineeringTools)
            if (visual != null && visual.Frame)
            {
                SetEngineeringVisible(visual, false);
                UnityEngine.Object.Destroy(visual.Frame.gameObject);
            }
        _engineeringTools = Array.Empty<EngineeringVisual?>();
        _engineeringSources = ImmutableArray<BoxStationToolSource>.Empty;
        _engineeringMeshes = new Mesh[5, 2];
        _engineeringState = null;
        _engineeringStateComplete = false;
    }

    private sealed class EngineeringVisual(Transform frame, MeshFilter filter, int part)
    {
        internal readonly Transform Frame = frame;
        internal readonly MeshFilter Filter = filter;
        internal readonly int Part = part;
        internal bool Active, Detail, Held;
    }
}
