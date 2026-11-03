// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class BoxStationModel
{
    private ImmutableArray<BoxStationStorageDefinition> _storageDefinitions = ImmutableArray<BoxStationStorageDefinition>.Empty;
    private Transform[] _storageFrames = Array.Empty<Transform>(), _storageClosures = Array.Empty<Transform>();
    private MeshFilter[][] _storageFilters = Array.Empty<MeshFilter[]>();
    private Mesh[,] _storageMeshes = new Mesh[4, 2];
    private readonly Dictionary<int, int> _storageColliders = new();
    private float[] _storageAmounts = Array.Empty<float>();
    private bool[] _storageDetail = Array.Empty<bool>();
    private bool _storagePoseKnown;
    private float _storageUpdateTime;
    private Material? _storageMaterial;
    private BoxStationPortableState? _portableState;
    private BoxStationPortableState? _completedPortableState;
    private bool _portablesCurrent;
    private bool _portableUpdatePending;
    private readonly Vector3[][] _storedEquipmentVertices = new Vector3[8][];
    private readonly Bounds[] _storedEquipmentBounds = new Bounds[8];
    private readonly float[] _storedEquipmentBaseOffsets = new float[8];

    internal ImmutableArray<BoxStationStorageDefinition> StorageDefinitions => _storageDefinitions;
    internal BoxStationPortableState? PortableState => _portablesCurrent && !_portableUpdatePending ? _portableState : null;

    internal void SuspendPortableProjection()
    {
        _portablesCurrent = false;
        _portableUpdatePending = true;
        _materialDetailDirty = _engineeringDetailDirty = _repairDetailDirty = true;
    }

    internal void UpdatePortables(BoxStationPortableState? state)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            FinishGeometryRetirement();
            bool cached = state == _completedPortableState && !_portableUpdatePending;
            bool current = state is { } candidate && (cached
                || BoxStationSystems.ValidPortables(_layout, candidate))
                && candidate.Materials.HullRevision == _hull.State.Revision
                && candidate.Materials.CableRevision == _cableState.Revision
                && candidate.Materials.DoorConstruction.Revision == _doorRevision
                && (cached || BoxStationDoors.CompleteEdges(candidate.Materials.DoorConstruction).AsSpan().SequenceEqual(_builtDoors.AsSpan())
                    && BoxStationHullRepair.ValidMaterials(_layout, candidate.Materials, _hull.State, _cableState)
                    && UpdateFloors(candidate.Materials.Floors));
            if (_portablesCurrent != current)
                _materialDetailDirty = _engineeringDetailDirty = _repairDetailDirty = true;
            _portablesCurrent = current;
            if (!current && _portableUpdatePending) return;
            var next = current ? state : _completedPortableState;
            if (next is not { } projection) return;
            // Storage fitting reads working custody; public state stays unavailable until the full projection succeeds.
            _portableUpdatePending = true;
            _portableState = projection;
            if (!UpdateMaterials(projection.Materials))
                throw new InvalidOperationException("Station materials rejected the portable projection.");
            UpdateEngineering(projection.Engineering);
            UpdateFirefighting(projection.Firefighting);
            UpdateFoam(projection.Foam);
            UpdateHullRepair(projection.Repair);
            UpdateStoredPortables();
            _completedPortableState = projection;
            _portableUpdatePending = false;
        }
        catch
        {
            SuspendPortableProjection();
            throw;
        }
    }

    internal Transform StorageFrame(int storage)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)storage >= _storageFrames.Length) throw new ArgumentOutOfRangeException(nameof(storage));
        return _storageFrames[storage];
    }

    internal Vector3 StorageHandlePoint(int storage)
    {
        var frame = StorageFrame(storage);
        var definition = _storageDefinitions[storage];
        var handle = BoxStationStorage.HandlePosition(definition);
        if (definition.Kind == BoxStationStorageKind.Closet)
        {
            frame = _storageClosures[storage];
            handle -= BoxStationStorage.HingePosition(definition);
        }
        return frame.TransformPoint(new(handle.X, handle.Y, handle.Z));
    }

    internal bool StorageOpen(int storage) => Visible && _portablesCurrent && (uint)storage < _storageAmounts.Length
        && _materialState is { } state && state.StorageOpen[storage] && _storageAmounts[storage] == 1;

    internal bool StorageSettled(int storage) => Visible && _portablesCurrent && (uint)storage < _storageAmounts.Length
        && _materialState is { } state && _storageAmounts[storage] == (state.StorageOpen[storage] ? 1 : 0);

    internal float StorageAmount(int storage)
    {
        StorageFrame(storage);
        return _storageAmounts[storage];
    }

    internal bool OwnsStorageCollider(int storage, Collider collider)
        => collider && _storageColliders.TryGetValue(collider.GetInstanceID(), out int owner) && owner == storage;

    // The envelope includes the interior so a closure cannot trap an actor already inside.
    internal Bounds StorageSweepBounds(int storage)
    {
        var frame = StorageFrame(storage);
        var bounds = StorageLocalSweep(_storageDefinitions[storage]);
        var result = new Bounds(frame.localPosition + frame.localRotation * bounds.center, Vector3.zero);
        for (int corner = 0; corner < 8; corner++)
        {
            var sign = new Vector3((corner & 1) == 0 ? -1 : 1, (corner & 2) == 0 ? -1 : 1, (corner & 4) == 0 ? -1 : 1);
            result.Encapsulate(frame.localPosition + frame.localRotation * (bounds.center + Vector3.Scale(bounds.extents, sign)));
        }
        return result;
    }

    private static Bounds StorageLocalSweep(BoxStationStorageDefinition definition)
        => definition.Kind == BoxStationStorageKind.Closet
            ? new(new(-.17f, 1.115f, .66f), new(1.71f, 2.17f, 2.23f))
            : new(new(0, .865f, -.10f), new(1.37f, 1.67f, 1.20f));

    private IEnumerable<bool> BuildStorage()
    {
        foreach (bool endFrame in BuildStoredEquipmentContacts()) yield return endFrame;
        string[] names = { "closet-body", "closet-door", "crate-body", "crate-lid" };
        for (int part = 0; part < names.Length; part++)
            for (int lod = 0; lod < 2; lod++)
            {
                _storageMeshes[part, lod] = CreateMesh(_parts["storage-" + names[part] + (lod == 0 ? "" : "-lod")]);
                yield return false;
            }
        _storageDefinitions = BoxStationStorage.GetDefinitions(_layout);
        int count = _storageDefinitions.Length;
        _storageFrames = new Transform[count];
        _storageClosures = new Transform[count];
        _storageFilters = new MeshFilter[count][];
        _storageAmounts = new float[count];
        _storageDetail = new bool[count];
        for (int i = 0; i < count; i++)
        {
            var definition = _storageDefinitions[i];
            bool closet = definition.Kind == BoxStationStorageKind.Closet;
            var frame = _storageFrames[i] = Node(definition.Name, Root.transform);
            frame.localPosition = _layout.CellPosition(definition.Cell);
            frame.localRotation = Turn(PortDirection(definition.Direction));
            int part = closet ? 0 : 2;
            Render(frame, _storageMeshes[part, 1], _storageMaterial!);
            var hinge = BoxStationStorage.HingePosition(definition);
            var closure = _storageClosures[i] = Node(closet ? "Door" : "Lid", frame);
            closure.localPosition = new(hinge.X, hinge.Y, hinge.Z);
            Render(closure, _storageMeshes[part + 1, 1], _storageMaterial!);
            _storageFilters[i] = new[] { frame.GetComponent<MeshFilter>(), closure.GetComponent<MeshFilter>() };
            float height = BoxStationStorage.Height(definition);
            AddStorageSolid(i, frame, new(-.66f, height * .5f, 0), new(.03f, height, .9f));
            AddStorageSolid(i, frame, new(.66f, height * .5f, 0), new(.03f, height, .9f));
            AddStorageSolid(i, frame, new(0, height * .5f, -.435f), new(1.29f, height, .03f));
            int shelves = closet ? 3 : 1;
            for (int shelf = 0; shelf < shelves; shelf++)
                AddStorageSolid(i, frame, new(0, BoxStationStorage.SlotPosition(definition, shelf * 2).Y - .02f, 0), new(1.29f, .04f, .84f));
            if (closet) AddStorageSolid(i, frame, new(0, 2.185f, 0), new(1.29f, .03f, .9f));
            else AddStorageSolid(i, frame, new(0, .375f, .435f), new(1.29f, .75f, .03f));

            // Register all swept cells once; the actual collider stays tight to the moving panel.
            var sweep = StorageLocalSweep(definition);
            var solid = AddStorageSolid(i, closure, sweep.center - closure.localPosition, sweep.size);
            solid.center = closet ? new(.65f, 1.10f, 0) : new(0, 0, .45f);
            solid.size = closet ? new(1.3f, 2.06f, .03f) : new(1.35f, .03f, .9f);
            yield return false;
        }
    }

    private BoxCollider AddStorageSolid(int storage, Transform frame, Vector3 center, Vector3 size)
    {
        var collider = AddSolid(frame, center, size);
        _storageColliders.Add(collider.GetInstanceID(), storage);
        return collider;
    }

    internal void UpdateStorageMotion()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Visible) UpdateStorage(true);
    }

    private void UpdateStorage(bool advance = false)
    {
        float now = Time.unscaledTime;
        float step = advance ? Math.Clamp(now - _storageUpdateTime, 0, .1f) * 2.5f : 0;
        if (advance || !_storagePoseKnown) _storageUpdateTime = now;
        if (!_portablesCurrent || _materialState is not { } state) return;
        for (int i = 0; i < _storageFrames.Length; i++)
        {
            float target = state.StorageOpen[i] ? 1 : 0;
            float amount = !_storagePoseKnown ? target : Mathf.MoveTowards(_storageAmounts[i], target, step);
            if (!_storagePoseKnown || amount != _storageAmounts[i])
            {
                float angle = BoxStationStorage.OpenAngle * amount;
                _storageClosures[i].localRotation = _storageDefinitions[i].Kind == BoxStationStorageKind.Closet
                    ? Quaternion.Euler(0, angle, 0) : Quaternion.Euler(angle, 0, 0);
                _materialDetailDirty = _engineeringDetailDirty = _repairDetailDirty = true;
            }
            _storageAmounts[i] = amount;
        }
        _storagePoseKnown = true;
        UpdateStoredPortables();
    }

    private void UpdateStorageDetail(Vector3 eye)
    {
        for (int i = 0; i < _storageFrames.Length; i++)
        {
            bool detail = (_storageFrames[i].localPosition - eye).sqrMagnitude < 20 * 20;
            if (!_detailIncomplete && _storageDetail[i] == detail) continue;
            int part = _storageDefinitions[i].Kind == BoxStationStorageKind.Closet ? 0 : 2;
            for (int p = 0; p < 2; p++) _storageFilters[i][p].sharedMesh = _storageMeshes[part + p, detail ? 0 : 1];
            _storageDetail[i] = detail;
        }
    }

    internal bool TryStorageTarget(Ray ray, float reach, out int storage, out Vector3 contact)
    {
        storage = -1; contact = default;
        if (!Visible || !_portablesCurrent || _materialState == null || !Finite(ray.origin) || !Finite(ray.direction)
            || ray.direction.sqrMagnitude < .000001f || !float.IsFinite(reach) || reach <= 0 || reach > 6) return false;
        var direction = ray.direction.normalized;
        float nearest = reach;
        for (int i = 0; i < _storageFrames.Length; i++)
        {
            var point = StorageHandlePoint(i);
            var delta = point - ray.origin;
            float distance = delta.magnitude, along = Vector3.Dot(delta, direction);
            if (distance > nearest || along <= 0 || (delta - along * direction).sqrMagnitude > .18f * .18f) continue;
            nearest = distance; storage = i; contact = point;
        }
        return storage >= 0;
    }

    private void LoadStorageMaterial(Shader shader)
    {
        _storageMaterial = new Material(shader) { name = "Station storage finish", enableInstancing = true };
        ConfigurePanelMaterial(_storageMaterial, "storage");
    }

    private IEnumerable<bool> BuildStoredEquipmentContacts()
    {
        string[] tools = { "tool-screwdriver", "tool-crowbar", "tool-wirecutters", "tool-wrench", "tool-welder" };
        for (int part = 0; part < tools.Length; part++)
        {
            BuildStoredEquipmentContact(part, new[] { tools[part] }, new[] { Vector3.zero });
            yield return false;
        }
        BuildStoredEquipmentContact(5,
            new[] { "machine-extinguisher-body", "machine-extinguisher-lever", "machine-extinguisher-pin", "machine-extinguisher-nozzle" },
            new[] { Vector3.zero, ExtinguisherLeverPivot, ExtinguisherPinPivot, new Vector3(.087f, .425f, .035f) });
        yield return false;
        BuildStoredEquipmentContact(6, new[] { "foam-canister", "foam-cap", "foam-lever" },
            new[] { Vector3.zero, FoamCapPivot, FoamLeverPivot });
        yield return false;
        BuildStoredEquipmentContact(7, new[] { "repair-tile" }, new[] { Vector3.zero });
        yield return false;
    }

    private void BuildStoredEquipmentContact(int part, string[] names, Vector3[] pivots)
    {
        var vertices = new List<Vector3>();
        float minimum = float.MaxValue;
        for (int lod = 0; lod < 2; lod++)
            for (int child = 0; child < names.Length; child++)
                foreach (var vertex in _parts[names[child] + (lod == 0 ? "" : "-lod")].Vertices)
                {
                    var point = vertex + pivots[child];
                    minimum = Math.Min(minimum, point.y);
                    if (lod == 0) vertices.Add(point);
                }
        var bounds = new Bounds(vertices[0], Vector3.zero);
        foreach (var vertex in vertices) bounds.Encapsulate(vertex);
        _storedEquipmentVertices[part] = vertices.ToArray();
        _storedEquipmentBounds[part] = bounds;
        _storedEquipmentBaseOffsets[part] = -minimum;
    }

    private bool StoredPortablePose(int storage, byte slot, float baseOffset, out Vector3 position, out Quaternion rotation)
    {
        position = default; rotation = Quaternion.identity;
        if (!StorageOpen(storage)) return false;
        var frame = _storageFrames[storage];
        var point = BoxStationStorage.SlotPosition(_storageDefinitions[storage], slot);
        rotation = frame.localRotation;
        position = frame.localPosition + rotation * new Vector3(point.X, point.Y + baseOffset, point.Z);
        return true;
    }

    private void UpdateStoredPortables()
    {
        if (_portableState is not { } state) return;
        foreach (var item in state.Materials.Bundles)
            if (item.Place == BoxStationMaterialPlace.Stored && _materialBundles.TryGetValue(item.Id, out var material))
            {
                if (!StoredMaterialPose(item, out var position, out var rotation)) SetMaterialVisible(material, false);
                else { SetMaterialPosition(material, position); material.Frame.localRotation = rotation; }
            }
        for (int id = 0; id < state.Engineering.Tools.Length; id++)
        {
            var item = state.Engineering.Tools[id];
            if (item.Place != BoxStationToolPlace.Stored || _engineeringTools[id] is not { } visual) continue;
            if (!StoredPortablePose(item.Storage, item.Slot, _storedEquipmentBaseOffsets[visual.Part], out var position, out var rotation))
                SetEngineeringVisible(visual, false);
            else { visual.Frame.localPosition = position; visual.Frame.localRotation = rotation; }
        }
        for (int id = 0; id < state.Firefighting.Items.Length; id++)
        {
            var item = state.Firefighting.Items[id];
            if (item.Place != BoxStationExtinguisherPlace.Stored) continue;
            var frame = _extinguisherFrames[id];
            bool visible = StoredPortablePose(item.Storage, item.Slot, _storedEquipmentBaseOffsets[5], out var position, out var rotation);
            if (visible) { frame.localPosition = position; frame.localRotation = rotation; }
            if (frame.gameObject.activeSelf != visible) frame.gameObject.SetActive(visible);
        }
        for (int id = 0; id < state.Foam.Items.Length; id++)
        {
            var item = state.Foam.Items[id];
            if (item.Place != BoxStationFoamPlace.Stored) continue;
            var frame = _foamFrames[id];
            bool visible = StoredPortablePose(item.Storage, item.Slot, _storedEquipmentBaseOffsets[6], out var position, out var rotation);
            if (visible) { frame.localPosition = position; frame.localRotation = rotation; }
            if (frame.gameObject.activeSelf != visible) frame.gameObject.SetActive(visible);
        }
        for (int id = 0; id < state.Repair.Packs.Length; id++)
        {
            var item = state.Repair.Packs[id];
            if (item.Place != BoxStationRepairPlace.Stored || _repairPacks[id] is not { } visual) continue;
            if (!StoredPortablePose(item.Storage, item.Slot, _storedEquipmentBaseOffsets[7], out var position, out var rotation))
                SetRepairVisible(visual, false);
            else SetRepairPosition(visual, position, rotation);
        }
    }

    private bool StoredPortableGeometry(SpacecraftHeldStationToolKind kind, int id, out int part,
        out Vector3 position, out Quaternion rotation, out int layers)
    {
        part = -1; position = default; rotation = Quaternion.identity; layers = 1;
        if (_portableState is not { } state || !_portablesCurrent) return false;
        int storage;
        byte slot;
        switch (kind)
        {
            case SpacecraftHeldStationToolKind.Engineering:
                if ((uint)id >= state.Engineering.Tools.Length) return false;
                var tool = state.Engineering.Tools[id];
                if (tool.Place != BoxStationToolPlace.Stored) return false;
                storage = tool.Storage; slot = tool.Slot; part = (int)_engineeringSources[id].Kind;
                break;
            case SpacecraftHeldStationToolKind.Extinguisher:
                if ((uint)id >= state.Firefighting.Items.Length) return false;
                var extinguisher = state.Firefighting.Items[id];
                if (extinguisher.Place != BoxStationExtinguisherPlace.Stored) return false;
                storage = extinguisher.Storage; slot = extinguisher.Slot; part = 5;
                break;
            case SpacecraftHeldStationToolKind.Foam:
                if ((uint)id >= state.Foam.Items.Length) return false;
                var foam = state.Foam.Items[id];
                if (foam.Place != BoxStationFoamPlace.Stored) return false;
                storage = foam.Storage; slot = foam.Slot; part = 6;
                break;
            case SpacecraftHeldStationToolKind.RepairTiles:
                if ((uint)id >= state.Repair.Packs.Length) return false;
                var pack = state.Repair.Packs[id];
                if (pack.Place != BoxStationRepairPlace.Stored) return false;
                storage = pack.Storage; slot = pack.Slot; part = 7; layers = pack.Tiles;
                break;
            default: return false;
        }
        return StoredPortablePose(storage, slot, _storedEquipmentBaseOffsets[part], out position, out rotation);
    }

    internal bool TryStoredPortableContact(SpacecraftHeldStationToolKind kind, int id, Vector3 worldEye, out Vector3 contact)
    {
        contact = default;
        if (kind == SpacecraftHeldStationToolKind.Material) return TryStoredMaterialContact(id, worldEye, out contact);
        if (!Finite(worldEye) || !StoredPortableGeometry(kind, id, out int part, out var position, out var rotation, out int layers))
            return false;
        var worldPosition = Root.transform.TransformPoint(position);
        var worldRotation = Root.transform.rotation * rotation;
        return StoredMeshEyeContact(_storedEquipmentVertices[part], _storedEquipmentBounds[part], layers, .019f,
            worldPosition, worldRotation, worldEye, out contact);
    }

    internal bool TryStoredPortableTarget(Ray ray, float reach, out SpacecraftHeldStationToolKind kind, out int id, out Vector3 contact)
    {
        kind = default; id = -1; contact = default;
        if (!Visible || !_portablesCurrent || _portableState is not { } state || !Finite(ray.origin) || !Finite(ray.direction)
            || ray.direction.sqrMagnitude < .000001f || !float.IsFinite(reach) || reach <= 0 || reach > 6) return false;
        ray.direction = ray.direction.normalized;
        float nearest = reach;
        if (TryStoredMaterialTarget(ray, nearest, out id, out contact))
        {
            kind = SpacecraftHeldStationToolKind.Material;
            nearest = Vector3.Distance(ray.origin, contact);
        }
        for (int item = 0; item < state.Engineering.Tools.Length; item++)
            SelectStoredPortable(SpacecraftHeldStationToolKind.Engineering, item, ray, ref nearest, ref kind, ref id, ref contact);
        for (int item = 0; item < state.Firefighting.Items.Length; item++)
            SelectStoredPortable(SpacecraftHeldStationToolKind.Extinguisher, item, ray, ref nearest, ref kind, ref id, ref contact);
        for (int item = 0; item < state.Foam.Items.Length; item++)
            SelectStoredPortable(SpacecraftHeldStationToolKind.Foam, item, ray, ref nearest, ref kind, ref id, ref contact);
        for (int item = 0; item < state.Repair.Packs.Length; item++)
            SelectStoredPortable(SpacecraftHeldStationToolKind.RepairTiles, item, ray, ref nearest, ref kind, ref id, ref contact);
        return id >= 0;
    }

    private void SelectStoredPortable(SpacecraftHeldStationToolKind candidateKind, int candidateId, Ray ray,
        ref float nearest, ref SpacecraftHeldStationToolKind kind, ref int id, ref Vector3 contact)
    {
        if (!StoredPortableGeometry(candidateKind, candidateId, out int part, out var position, out var rotation, out int layers)
            || !StoredMeshContact(_storedEquipmentVertices[part], _storedEquipmentBounds[part], layers, .019f,
                Root.transform.TransformPoint(position), Root.transform.rotation * rotation, ray, nearest, out var point)) return;
        nearest = Vector3.Distance(ray.origin, point);
        kind = candidateKind; id = candidateId; contact = point;
    }

    private static bool StoredMeshEyeContact(Vector3[] vertices, Bounds bounds, int layers, float pitch,
        Vector3 worldPosition, Quaternion worldRotation, Vector3 worldEye, out Vector3 contact)
    {
        contact = default;
        var eye = Quaternion.Inverse(worldRotation) * (worldEye - worldPosition);
        float nearest = float.MaxValue;
        var point = Vector3.zero;
        for (int layer = 0; layer < layers; layer++)
            foreach (var vertex in vertices)
            {
                var candidate = vertex + Vector3.up * (layer * pitch);
                float distance = (candidate - eye).sqrMagnitude;
                if (distance >= nearest) continue;
                nearest = distance; point = candidate;
            }
        var delta = worldPosition + worldRotation * point - worldEye;
        if (delta.sqrMagnitude < .000001f) return false;
        return StoredMeshContact(vertices, bounds, layers, pitch, worldPosition, worldRotation,
            new(worldEye, delta.normalized), delta.magnitude + .002f, out contact);
    }

    private static bool StoredMeshContact(Vector3[] vertices, Bounds bounds, int layers, float pitch,
        Vector3 worldPosition, Quaternion worldRotation, Ray worldRay, float reach, out Vector3 contact)
    {
        contact = default;
        var inverseRotation = Quaternion.Inverse(worldRotation);
        var ray = new Ray(inverseRotation * (worldRay.origin - worldPosition), inverseRotation * worldRay.direction);
        float nearest = reach;
        bool found = false;
        for (int layer = 0; layer < layers; layer++)
        {
            var localRay = new Ray(ray.origin - Vector3.up * (layer * pitch), ray.direction);
            if (!bounds.IntersectRay(localRay, out float entry) || entry > nearest) continue;
            for (int i = 0; i < vertices.Length; i += 3)
            {
                var a = vertices[i];
                var edge1 = vertices[i + 1] - a;
                var edge2 = vertices[i + 2] - a;
                var cross = Vector3.Cross(localRay.direction, edge2);
                float determinant = Vector3.Dot(edge1, cross);
                if (MathF.Abs(determinant) < .0000001f) continue;
                float inverse = 1 / determinant;
                var delta = localRay.origin - a;
                float u = Vector3.Dot(delta, cross) * inverse;
                if (u < -.00001f || u > 1.00001f) continue;
                var q = Vector3.Cross(delta, edge1);
                float v = Vector3.Dot(localRay.direction, q) * inverse;
                if (v < -.00001f || u + v > 1.00001f) continue;
                float distance = Vector3.Dot(edge2, q) * inverse;
                if (distance < 0 || distance > nearest) continue;
                nearest = distance; found = true;
            }
        }
        if (found) contact = worldRay.origin + worldRay.direction * nearest;
        return found;
    }

    private void ReleaseStorage()
    {
        _storageDefinitions = ImmutableArray<BoxStationStorageDefinition>.Empty;
        _storageFrames = _storageClosures = Array.Empty<Transform>();
        _storageFilters = Array.Empty<MeshFilter[]>();
        _storageMeshes = new Mesh[4, 2];
        _storageAmounts = Array.Empty<float>();
        _storageDetail = Array.Empty<bool>();
        _storageColliders.Clear();
        _storagePoseKnown = false;
        _storageUpdateTime = 0;
        _portableState = null;
        _completedPortableState = null;
        _portablesCurrent = false;
        _portableUpdatePending = false;
        Array.Clear(_storedEquipmentVertices);
        if (_storageMaterial) UnityEngine.Object.Destroy(_storageMaterial);
        _storageMaterial = null;
    }
}
