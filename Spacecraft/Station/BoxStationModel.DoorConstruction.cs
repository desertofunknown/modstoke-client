// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class BoxStationModel
{
    private Dictionary<int, DoorAssemblyVisual> _doorAssemblies = new();
    private Dictionary<int, (int Index, int Target)> _doorConstructionSolids = new();
    private readonly Mesh[,] _doorServiceMeshes = new Mesh[3, 2];
    private Material? _doorServiceMaterial;
    private BoxStationDoorConstructionState? _doorConstructionState;

    private static readonly Bounds[] DoorFrameBounds =
    {
        new(new(-1.12f, 1.6f, 0), new(.16f, 3.2f, .3f)),
        new(new(1.12f, 1.6f, 0), new(.16f, 3.2f, .3f)),
        new(new(0, 2.925f, 0), new(BoxStationLayout.DoorWidth, .55f, .3f)),
        new(new(-1.12f, .025f, 0), new(.16f, .05f, .36f)),
        new(new(1.12f, .025f, 0), new(.16f, .05f, .36f)),
        new(new(1.12f, 1.35f, 0), new(.16f, .7f, .36f))
    };

    internal bool TryDoorIndex(int stableId, out int index)
    {
        index = BoxStationDoors.Index(_layout, _builtDoors, stableId);
        return index >= 0;
    }

    internal bool UpdateDoors(BoxStationState state)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        FinishGeometryRetirement();
        if (!ValidDoorProjection(state)) return false;
        if (_builtDoors.AsSpan().SequenceEqual(state.BuiltDoors.AsSpan()))
        { _doorRevision = state.DoorRevision; return true; }
        foreach (bool _ in ReplaceDoors(state)) { }
        return true;
    }

    private bool ValidDoorProjection(BoxStationState state)
    {
        if (state.LayoutHash != _layout.LayoutHash || state.DoorRevision < _doorRevision
            || !BoxStationDoors.ValidEdges(_layout, state.BuiltDoors)
            || state.Doors.Length != _layout.Doors.Length + state.BuiltDoors.Length) return false;
        bool same = _builtDoors.AsSpan().SequenceEqual(state.BuiltDoors.AsSpan());
        if (state.DoorRevision == _doorRevision && !same) return false;
        if (same) return true;
        for (int index = _layout.Doors.Length; index < state.Doors.Length; index++)
            if (!float.IsFinite(state.Doors[index].Amount) || state.Doors[index].Amount is < 0 or > 1) return false;
        return true;
    }

    private IEnumerable<bool> ReplaceDoors(BoxStationState state)
    {
        FinishGeometryRetirement();
        var definitions = BoxStationDoors.Definitions(_layout, state.BuiltDoors);
        var frames = new Transform[definitions.Length];
        var gates = new List<Transform>[definitions.Length];
        var leaves = new List<DoorLeaf>[definitions.Length];
        var solids = new List<int>[definitions.Length];
        var amounts = new float[definitions.Length];
        var constructionSolids = new Dictionary<int, (int Index, int Target)>(_doorConstructionSolids);
        int mapped = _layout.Doors.Length;
        var oldSolids = _doorSolids;
        var oldNodes = new List<GameObject>();
        for (int index = mapped; index < _doorGates.Length; index++)
            foreach (var frame in _doorGates[index]) oldNodes.Add(frame.gameObject);
        var stagedNodes = new List<GameObject>(state.BuiltDoors.Length);
        Array.Copy(_doorFrames, frames, mapped);
        Array.Copy(_doorGates, gates, mapped);
        Array.Copy(_leaves, leaves, mapped);
        Array.Copy(_doorSolids, solids, mapped);
        Array.Copy(_doorAmounts, amounts, mapped);
        bool committed = false;
        try
        {
            for (int index = mapped; index < definitions.Length; index++)
            {
                var definition = definitions[index];
                int edge = state.BuiltDoors[index - mapped];
                gates[index] = new List<Transform>(1);
                leaves[index] = new List<DoorLeaf>(1);
                solids[index] = new List<int>(4);
                stagedNodes.Add(BuildDoorGate(definition, BoxStationDoors.Center(_layout, edge), Turn(BoxStationDoors.Direction(edge)),
                    _doorMeshes, _distantDoorMeshes, gates[index], leaves[index], solids[index], constructionSolids, false));
                frames[index] = gates[index][0];
                float amount = state.Doors[index].Amount;
                foreach (var leaf in leaves[index]) SetDoorPose(leaf, amount);
                amounts[index] = amount;
                yield return false;
            }

            // Control pads leave the old parents only after every replacement is ready.
            DoorsChanging?.Invoke();
            foreach (var node in stagedNodes) node.SetActive(true);

            _doorFrames = frames;
            _doorGates = gates;
            _leaves = leaves;
            _doorSolids = solids;
            _doorAmounts = amounts;
            _doorConstructionSolids = constructionSolids;
            _doorDefinitions = definitions;
            _builtDoors = state.BuiltDoors;
            _doorRevision = state.DoorRevision;
            committed = true;
            QueueDoorRetirement(oldNodes, oldSolids, mapped);
            FinishGeometryRetirement();
        }
        finally
        {
            if (!committed)
            {
                QueueDoorRetirement(stagedNodes, solids, mapped);
                FinishGeometryRetirement();
            }
        }
    }

    private void QueueDoorRetirement(List<GameObject> nodes, List<int>[] solids, int first)
    {
        for (int index = first; index < solids.Length; index++)
            if (solids[index] is { } row)
                foreach (int solid in row) QueueSolidRetirement(solid);
        foreach (var node in nodes) QueueNodeRetirement(node);
    }

    private GameObject CreateDoorNode(string name, bool active)
    {
        var node = new GameObject(name);
        try
        {
            node.SetActive(active);
            node.layer = Root.layer;
            node.transform.SetParent(Root.transform, false);
            return node;
        }
        catch
        {
            QueueNodeRetirement(node);
            FinishGeometryRetirement();
            throw;
        }
    }

    private BoxCollider AddDoorSolid(List<int> solids, Dictionary<int, (int Index, int Target)> constructionSolids,
        BoxStationDoor door, Transform parent, Vector3 center, Vector3 size)
    {
        var collider = AddSolid(parent, center, size, true, out int solid);
        solids.Add(solid);
        if (door.Id >= _layout.Doors.Length)
            constructionSolids.Add(collider.GetInstanceID(), (solid, BoxStationDoors.Target(_layout, door.Id - _layout.Doors.Length)));
        return collider;
    }

    private void RemoveDoorConstructionSolid(int index) => RemoveSolid(index);

    private void UpdateDoorConstruction(BoxStationDoorConstructionState state)
    {
        FinishGeometryRetirement();
        if (_doorConstructionState == state) return;
        foreach (bool _ in ApplyDoorConstruction(state)) { }
    }

    private IEnumerable<bool> ApplyDoorConstruction(BoxStationDoorConstructionState state)
    {
        FinishGeometryRetirement();
        if (_doorConstructionState == state) yield break;
        var assemblies = new Dictionary<int, DoorAssemblyVisual>(_doorAssemblies);
        var constructionSolids = new Dictionary<int, (int Index, int Target)>(_doorConstructionSolids);
        var retired = new List<DoorAssemblyVisual>();
        var staged = new List<DoorAssemblyVisual>(state.Assemblies.Length);
        bool committed = false;
        try
        {
            foreach (var pair in _doorAssemblies)
            {
                var assembly = BoxStationHullRepair.DoorAt(_layout, state, BoxStationDoors.Target(_layout, pair.Key));
                if (assembly.Stage != pair.Value.Stage)
                {
                    retired.Add(pair.Value);
                    assemblies.Remove(pair.Key);
                }
                yield return false;
            }
            foreach (var assembly in state.Assemblies)
            {
                if (assembly.Stage is not (BoxStationDoorStage.Empty or BoxStationDoorStage.Complete)
                    && !assemblies.ContainsKey(assembly.Edge))
                {
                    var visual = CreateDoorAssembly(assembly, constructionSolids);
                    staged.Add(visual);
                    assemblies.Add(assembly.Edge, visual);
                }
                yield return false;
            }
            foreach (var visual in staged) visual.Node.SetActive(true);
            _doorAssemblies = assemblies;
            _doorConstructionSolids = constructionSolids;
            _doorConstructionState = state;
            committed = true;
            foreach (var visual in retired) QueueDoorAssemblyRetirement(visual);
            FinishGeometryRetirement();
        }
        finally
        {
            if (!committed)
            {
                foreach (var visual in staged) QueueDoorAssemblyRetirement(visual);
                FinishGeometryRetirement();
            }
        }
    }

    private DoorAssemblyVisual CreateDoorAssembly(BoxStationDoorAssembly assembly,
        Dictionary<int, (int Index, int Target)> constructionSolids)
    {
        var node = CreateDoorNode("Pressure door assembly " + assembly.Edge, false);
        DoorAssemblyVisual? visual = null;
        try
        {
            var frame = node.transform;
            visual = new DoorAssemblyVisual(node, frame, assembly.Stage);
            frame.localPosition = BoxStationDoors.Center(_layout, assembly.Edge);
            frame.localRotation = Turn(BoxStationDoors.Direction(assembly.Edge));
            RenderDoorPart(frame, _doorMeshes.Frame, _distantDoorMeshes.Frame, _airlock!);
            int count = DoorAssemblyBoundsCount(assembly.Stage);
            int target = BoxStationDoors.Target(_layout, assembly.Edge);
            for (int part = 0; part < count; part++)
            {
                var bounds = DoorFrameBounds[part];
                var collider = AddSolid(frame, bounds.center, bounds.size, true, out int index);
                visual.Solids.Add(index);
                constructionSolids.Add(collider.GetInstanceID(), (index, target));
            }
            if (assembly.Stage >= BoxStationDoorStage.Anchored)
            {
                EnsureDoorServiceMeshes();
                for (int side = -1; side <= 1; side += 2)
                {
                    var anchor = Node("Floor anchor", frame);
                    anchor.localPosition = new(side * 1.12f, .025f, 0);
                    RenderDoorPart(anchor, _doorServiceMeshes[0, 0], _doorServiceMeshes[0, 1], _deckHardwareMaterial!);
                }
            }
            if (assembly.Stage >= BoxStationDoorStage.Wired)
                for (int side = -1; side <= 1; side += 2)
                {
                    var wiring = Node("Service wiring", frame);
                    wiring.localPosition = new(1.12f, 1.35f, 0);
                    wiring.localRotation = Quaternion.Euler(0, side > 0 ? 0 : 180, 0);
                    RenderDoorPart(wiring, _doorServiceMeshes[1, 0], _doorServiceMeshes[1, 1], _doorServiceMaterial!);
                    if (assembly.Stage < BoxStationDoorStage.Electronics) continue;
                    var electronics = Node("Door control module", frame);
                    electronics.localPosition = wiring.localPosition;
                    electronics.localRotation = wiring.localRotation;
                    RenderDoorPart(electronics, _doorServiceMeshes[2, 0], _doorServiceMeshes[2, 1], _doorServiceMaterial!);
                }
            return visual;
        }
        catch
        {
            if (visual != null) QueueDoorAssemblyRetirement(visual);
            else QueueNodeRetirement(node);
            FinishGeometryRetirement();
            throw;
        }
    }

    private void EnsureDoorServiceMeshes()
    {
        for (int lod = 0; lod < 2; lod++)
        {
            string suffix = lod == 0 ? "" : "-lod";
            if (_doorServiceMeshes[0, lod] == null)
                _doorServiceMeshes[0, lod] = FitDoorPart(_parts["cable-hardware" + suffix], new(.15f, .05f, .34f));
            if (_doorServiceMeshes[1, lod] == null)
                _doorServiceMeshes[1, lod] = CreateMesh(_parts["door-service-wiring" + suffix]);
            if (_doorServiceMeshes[2, lod] == null)
                _doorServiceMeshes[2, lod] = CreateMesh(_parts["door-service-module" + suffix]);
        }
    }

    private Mesh FitDoorPart(SpacecraftMeshData data, Vector3 size)
    {
        var bounds = new Bounds(data.Vertices[0], Vector3.zero);
        foreach (var vertex in data.Vertices) bounds.Encapsulate(vertex);
        var scale = new Vector3(size.x / bounds.size.x, size.y / bounds.size.y, size.z / bounds.size.z);
        var builder = new MeshBuilder();
        builder.Add(data, -Vector3.Scale(bounds.center, scale), Quaternion.identity, scale);
        var mesh = builder.Create("Door service hardware");
        _meshes.Add(mesh);
        return mesh;
    }

    private static int DoorAssemblyBoundsCount(BoxStationDoorStage stage)
        => stage is BoxStationDoorStage.Wired or BoxStationDoorStage.Electronics ? 6
            : stage == BoxStationDoorStage.Anchored ? 5 : 3;

    private void DestroyDoorAssembly(DoorAssemblyVisual visual)
    {
        foreach (int index in visual.Solids) RemoveDoorConstructionSolid(index);
        QueueNodeRetirement(visual.Node);
        FinishGeometryRetirement();
    }

    private void QueueDoorAssemblyRetirement(DoorAssemblyVisual visual)
    {
        foreach (int index in visual.Solids) QueueSolidRetirement(index);
        QueueNodeRetirement(visual.Node);
    }

    private bool DoorConstructionCurrent => _materialState is { } state && state.HullRevision == _hull.State.Revision
        && state.DoorConstruction.Revision == _doorRevision && _doorConstructionState == state.DoorConstruction;

    internal Vector3 DoorConstructionPosition(int target)
    {
        int edge = BoxStationDoors.EdgeFromTarget(_layout, target);
        if (!BoxStationDoors.ValidEdge(_layout, edge)) throw new ArgumentOutOfRangeException(nameof(target));
        return BoxStationDoors.Center(_layout, edge);
    }

    internal bool DoorConstructionBounds(int target, out Bounds bounds)
    {
        bounds = default;
        int edge = BoxStationDoors.EdgeFromTarget(_layout, target);
        if (!BoxStationDoors.ValidEdge(_layout, edge)) return false;
        bounds = new(BoxStationDoors.Center(_layout, edge) + Vector3.up * 1.6f,
            BoxStationDoors.Direction(edge) == 0 ? new(2.4f, 3.2f, .36f) : new(.36f, 3.2f, 2.4f));
        return true;
    }

    internal bool DoorConstructionCell(int cell)
    {
        if (_doorConstructionState is not { } state) return false;
        foreach (var assembly in state.Assemblies)
            if (BoxStationDoors.TryCells(_layout, assembly.Edge, out int first, out int second)
                && (cell == first || cell == second)) return true;
        return false;
    }

    internal bool IsDoorConstructionCollider(Collider collider, int target)
        => collider && _doorConstructionSolids.TryGetValue(collider.GetInstanceID(), out var solid)
            && solid.Target == target && _solids[solid.Index] == collider;

    internal bool DoorConstructionContact(int target, Vector3 localContact)
    {
        int edge = BoxStationDoors.EdgeFromTarget(_layout, target);
        if (!Finite(localContact) || !DoorConstructionCurrent || !BoxStationDoors.ValidEdge(_layout, edge)) return false;
        var assembly = BoxStationHullRepair.DoorAt(_layout, _doorConstructionState!.Value, target);
        var point = Quaternion.Inverse(Turn(BoxStationDoors.Direction(edge)))
            * (localContact - BoxStationDoors.Center(_layout, edge));
        if (assembly.Stage == BoxStationDoorStage.Empty)
            return MathF.Abs(point.y) <= .02f && MathF.Abs(point.x) <= 1.2f && MathF.Abs(point.z) <= .35f;
        for (int part = 0; part < DoorAssemblyBoundsCount(assembly.Stage); part++)
        {
            var bounds = DoorFrameBounds[part];
            if (ConstructionFace(point - bounds.center, bounds.size)) return true;
        }
        return false;
    }

    internal bool TryDoorConstructionTarget(Vector3 localEye, Vector3 localDirection, BoxStationMaterial? material,
        out int target, out Vector3 localContact)
    {
        target = -1; localContact = default;
        if (!Visible || !DoorConstructionCurrent || !Finite(localEye) || !Finite(localDirection)
            || localDirection.sqrMagnitude < .000001f) return false;
        var direction = localDirection.normalized;
        float nearest = 2.4f;
        var state = _doorConstructionState!.Value;
        foreach (var assembly in state.Assemblies)
        {
            if (material is { } kind
                && !BoxStationHullRepair.ResolveDoorMaterial(_layout, assembly, kind, out _, out _, out _)) continue;
            var inverse = Quaternion.Inverse(Turn(BoxStationDoors.Direction(assembly.Edge)));
            var ray = new Ray(inverse * (localEye - BoxStationDoors.Center(_layout, assembly.Edge)), inverse * direction);
            for (int part = 0; part < DoorAssemblyBoundsCount(assembly.Stage); part++)
            {
                var bounds = DoorFrameBounds[part];
                if (!bounds.Contains(ray.origin) && bounds.IntersectRay(ray, out float distance) && distance >= 0 && distance <= nearest)
                {
                    nearest = distance;
                    target = BoxStationDoors.Target(_layout, assembly.Edge);
                }
            }
        }
        if (material == BoxStationMaterial.Steel && MathF.Abs(direction.y) >= .00001f)
        {
            float distance = -localEye.y / direction.y;
            if (distance >= 0 && distance <= nearest)
            {
                var point = localEye + direction * distance;
                int cell = _layout.CellAt(point);
                if (cell >= 0)
                {
                    var offset = point - _layout.CellPosition(cell);
                    bool northSouth = MathF.Abs(offset.z) >= MathF.Abs(offset.x);
                    int edgeCell = northSouth ? offset.z >= 0 ? cell : Neighbor(cell, 2)
                        : offset.x >= 0 ? cell : Neighbor(cell, 3);
                    int edge = edgeCell * 2 + (northSouth ? 0 : 1);
                    int candidate = BoxStationDoors.Target(_layout, edge);
                    if (edgeCell >= 0 && BoxStationDoors.ValidEdge(_layout, edge)
                        && BoxStationHullRepair.DoorAt(_layout, state, candidate).Stage == BoxStationDoorStage.Empty
                        && DoorConstructionContact(candidate, point))
                    {
                        target = candidate;
                        nearest = distance;
                    }
                }
            }
        }
        if (target < 0) return false;
        localContact = localEye + direction * nearest;
        return true;
    }

    private void ReleaseDoorConstruction()
    {
        foreach (var visual in _doorAssemblies.Values) DestroyDoorAssembly(visual);
        _doorAssemblies.Clear();
        _doorConstructionSolids.Clear();
        _doorConstructionState = null;
        if (_doorServiceMaterial) UnityEngine.Object.Destroy(_doorServiceMaterial);
        _doorServiceMaterial = null;
    }

    private sealed class DoorAssemblyVisual(GameObject node, Transform frame, BoxStationDoorStage stage)
    {
        internal readonly GameObject Node = node;
        internal readonly Transform Frame = frame;
        internal readonly BoxStationDoorStage Stage = stage;
        internal readonly List<int> Solids = new();
    }
}
