// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class BoxStationModel
{
    private const float MaterialCullDistance = 36, MaterialDetailDistance = 12;
    private ImmutableArray<BoxStationMaterialSource> _materialSources = ImmutableArray<BoxStationMaterialSource>.Empty;
    private readonly Dictionary<int, MaterialVisual> _materialBundles = new();
    private readonly Dictionary<int, BoxStationPendingYield> _materialPending = new();
    private readonly Dictionary<(int Structure, BoxStationMaterial Kind), MaterialVisual> _materialYields = new();
    private readonly List<int> _retiredMaterialBundles = new();
    private readonly List<(int Structure, BoxStationMaterial Kind)> _retiredMaterialYields = new();
    private MaterialVisual?[] _materialStocks = Array.Empty<MaterialVisual?>();
    private Mesh[,] _materialMeshes = new Mesh[9, 2];
    private Mesh[,] _floorTileDetails = new Mesh[2, 2];
    private Vector3[][] _materialContactVertices = new Vector3[18][];
    private Bounds[] _materialContactBounds = new Bounds[9];
    private float[] _materialBaseOffsets = new float[9];
    private BoxStationMaterialState? _materialState;
    private bool _materialStateComplete;
    private Vector3 _materialDetailEye;
    private float _materialDetailTime;
    private bool _materialDetailDirty;

    internal static Vector3 MaterialCarryGrip(BoxStationMaterial material) => material switch
    {
        BoxStationMaterial.Rods => new(.01f, .025f, 0),
        BoxStationMaterial.FloorTiles => RepairCarryGrip,
        BoxStationMaterial.Cable => new(.13f, .03f, 0),
        BoxStationMaterial.DoorElectronics => new(.055f, .012f, 0),
        BoxStationMaterial.LineSpare or BoxStationMaterial.CircuitSpare => new(0, .178f, -.025f),
        BoxStationMaterial.Steel or BoxStationMaterial.Plasteel => new(.297f, -.024f, 0),
        BoxStationMaterial.GlassShards or BoxStationMaterial.PlasmaGlassShards => new(.123f, -.026f, 0),
        _ => new(.247f, -.024f, 0)
    };

    private IEnumerable<bool> BuildMaterials()
    {
        string[] parts = { "material-sheet", "material-rods", "material-shard", "repair-sheet" };
        for (int part = 0; part < parts.Length; part++)
            for (int lod = 0; lod < 2; lod++)
            {
                _materialMeshes[part, lod] = CreateMesh(_parts[parts[part] + (lod == 0 ? "" : "-lod")]);
                yield return false;
            }
        for (int lod = 0; lod < 2; lod++)
        {
            _materialMeshes[4, lod] = CreateMesh(_parts["floor-tile" + (lod == 0 ? "" : "-lod")]);
            _floorTileDetails[0, lod] = CreateMesh(_parts["floor-tile-hardware" + (lod == 0 ? "" : "-lod")]);
            _floorTileDetails[1, lod] = CreateMesh(_parts["floor-tile-inset" + (lod == 0 ? "" : "-lod")]);
            yield return false;
        }
        for (int lod = 0; lod < 2; lod++)
        {
            _materialMeshes[5, lod] = CreateMesh(_parts["cable-coil" + (lod == 0 ? "" : "-lod")]);
            yield return false;
        }
        for (int lod = 0; lod < 2; lod++)
        {
            string suffix = lod == 0 ? "" : "-lod";
            var module = new MeshBuilder();
            module.Add(_parts["door-service-module" + suffix], new(0, -.149f, 0), Quaternion.Euler(-90, 0, 0));
            var mesh = module.Create("Door electronics module");
            _meshes.Add(mesh);
            _materialMeshes[6, lod] = mesh;
            yield return false;
        }
        string[] contactParts = { "material-sheet", "material-rods", "material-shard", "repair-sheet", "floor-tile", "cable-coil", "door-service-module" };
        for (int part = 0; part < contactParts.Length; part++)
            for (int lod = 0; lod < 2; lod++)
            {
                var vertices = _parts[contactParts[part] + (lod == 0 ? "" : "-lod")].Vertices;
                if (part == 6)
                {
                    var rotated = new Vector3[vertices.Length];
                    var rotation = Quaternion.Euler(-90, 0, 0);
                    for (int i = 0; i < vertices.Length; i++) rotated[i] = new Vector3(0, -.149f, 0) + rotation * vertices[i];
                    vertices = rotated;
                }
                _materialContactVertices[part * 2 + lod] = vertices;
                if (lod == 0) _materialContactBounds[part] = _materialMeshes[part, lod].bounds;
                float offset = -_materialMeshes[part, lod].bounds.min.y;
                _materialBaseOffsets[part] = lod == 0 ? offset : Math.Max(_materialBaseOffsets[part], offset);
                yield return false;
            }
        foreach (bool endFrame in BuildSpareMaterials()) yield return endFrame;
        _materialSources = BoxStationHullRepair.GetMaterialSources(_layout);
        _materialStocks = new MaterialVisual?[_materialSources.Length];
    }

    internal Vector3 MaterialStockPoint(int source)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)source >= _materialSources.Length) throw new ArgumentOutOfRangeException(nameof(source));
        var item = _materialSources[source];
        int count = _materialState?.Stocks[source] ?? 0;
        return Root.transform.TransformPoint(MaterialPoint(item.Position, item.Material, count));
    }

    internal Vector3 MaterialBundlePoint(int id)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!TryMaterialBundle(id, out var item)) throw new ArgumentOutOfRangeException(nameof(id));
        if (item.Place == BoxStationMaterialPlace.Stored)
        {
            if (TryStoredMaterialPoint(id, out var point)) return point;
            throw new InvalidOperationException("The stored material is not visible.");
        }
        if (item.Place == BoxStationMaterialPlace.Held && _materialBundles.TryGetValue(id, out var held) && held.Active)
            return held.Frame.TransformPoint(MaterialCarryGrip(item.Material));
        return Root.transform.TransformPoint(MaterialPoint(item.Position, item.Material, item.Count));
    }

    internal Vector3 MaterialYieldPoint(int structureId, BoxStationMaterial material)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (BoxStationHullRepair.TryConstruction(_layout, structureId, out var definition))
            return Root.transform.TransformPoint(ConstructionYieldPosition(definition, material)
                + Vector3.up * MaterialTop(material, ConstructionYieldCount(structureId, material)));
        if (!BoxStationHull.TryStructure(_layout, BoxStationHull.StructureTarget(_layout, structureId), out var structure))
            throw new ArgumentOutOfRangeException(nameof(structureId));
        int count = _materialPending.TryGetValue(structureId, out var pending) ? MaterialYieldCount(pending, material) : 0;
        return Root.transform.TransformPoint(MaterialYieldPosition(structure, material) + Vector3.up * MaterialTop(material, count));
    }

    private Vector3 MaterialYieldPosition(BoxStationStructure structure, BoxStationMaterial material)
    {
        bool grille = structure.Kind == BoxStationStructureKind.Grille;
        int direction = grille ? WindowDirection(structure.Cell)
            : structure.Direction == 5 ? 0 : PortDirection(structure.Direction);
        float inset = StructureInset(structure);
        var position = grille ? GrillePlane(structure, direction, CenteredGrille(structure), inset)
            : WindowEdge(structure, direction, inset);
        float offset = material == BoxStationMaterial.Rods ? .3f
            : material is BoxStationMaterial.GlassShards or BoxStationMaterial.PlasmaGlassShards ? -.3f : 0;
        // Exterior salvage stays anchored to its source component without adding deck support.
        return position + Turn(direction) * new Vector3(offset, 0, -.24f);
    }

    private bool UpdateMaterials(BoxStationMaterialState? state)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var visual in _materialBundles.Values)
            if (visual.Held) SetMaterialVisible(visual, false);
        bool unchanged = _materialStateComplete && _materialState == state;
        var next = state;
        if (!unchanged && next is { } candidate && (candidate.HullRevision != _hull.State.Revision
            || candidate.DoorConstruction.Revision != _doorRevision
            || !BoxStationDoors.CompleteEdges(candidate.DoorConstruction).AsSpan().SequenceEqual(_builtDoors.AsSpan())
            || !BoxStationHullRepair.ValidMaterials(_layout, candidate, _hull.State, _cableState))) return false;
        _materialStateComplete = false;
        if (next is { } current && !UpdateFloors(current.Floors)) return false;
        // Bundle and storage helpers need the working state before the whole projection is complete.
        _materialState = next;
        UpdateStorage();
        if (next is { } construction)
        {
            UpdateConstruction(next);
            UpdateDoorConstruction(construction.DoorConstruction);
        }
        FinishGeometryRetirement();
        if (unchanged)
        {
            _materialStateComplete = true;
            return true;
        }
        _materialDetailDirty = true;
        _materialPending.Clear();
        if (next is { } live)
            foreach (var pending in live.Pending) _materialPending.Add(pending.StructureId, pending);
        for (int source = 0; source < _materialStocks.Length; source++)
            if (_materialStocks[source] is { } stock)
            {
                SetMaterialLayers(stock, next?.Stocks[source] ?? 0);
                if (stock.Layers == 0) SetMaterialVisible(stock, false);
            }
        _retiredMaterialBundles.Clear();
        foreach (var pair in _materialBundles)
        {
            if (!TryMaterialBundle(pair.Key, out var item) || item.Material != pair.Value.Material)
            {
                _retiredMaterialBundles.Add(pair.Key);
                continue;
            }
            var visual = pair.Value;
            visual.Held = item.Place == BoxStationMaterialPlace.Held;
            SetMaterialLayers(visual, item.Count);
            if (visual.Held) SetMaterialVisible(visual, false);
            else if (item.Place == BoxStationMaterialPlace.Stored)
            {
                if (StoredMaterialPose(item, out var position, out var rotation))
                {
                    SetMaterialPosition(visual, position);
                    visual.Frame.localRotation = rotation;
                }
                else SetMaterialVisible(visual, false);
            }
            else SetMaterialPosition(visual, new(item.Position.X, item.Position.Y, item.Position.Z));
        }
        foreach (int id in _retiredMaterialBundles)
        {
            DestroyMaterialVisual(_materialBundles[id]);
            _materialBundles.Remove(id);
        }
        _retiredMaterialYields.Clear();
        foreach (var pair in _materialYields)
        {
            int count = BoxStationHullRepair.TryConstruction(_layout, pair.Key.Structure, out _)
                ? ConstructionYieldCount(pair.Key.Structure, pair.Key.Kind)
                : _materialPending.TryGetValue(pair.Key.Structure, out var pending) ? MaterialYieldCount(pending, pair.Key.Kind) : 0;
            if (count == 0)
                _retiredMaterialYields.Add(pair.Key);
            else SetMaterialLayers(pair.Value, count);
        }
        foreach (var key in _retiredMaterialYields)
        {
            DestroyMaterialVisual(_materialYields[key]);
            _materialYields.Remove(key);
        }
        _materialStateComplete = true;
        return true;
    }

    internal void SetHeldMaterialPose(int id, Vector3 position, Quaternion rotation, bool visible)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // IDs survive snapshot sorting; retiring a consumed bundle cannot hide a reused visual slot.
        if (!_portablesCurrent || !TryMaterialBundle(id, out var item) || item.Place != BoxStationMaterialPlace.Held) return;
        _materialBundles.TryGetValue(id, out var visual);
        if (visible)
        {
            if (!Finite(position) || !float.IsFinite(rotation.x) || !float.IsFinite(rotation.y)
                || !float.IsFinite(rotation.z) || !float.IsFinite(rotation.w)
                || Quaternion.Dot(rotation, rotation) is < .5f or > 1.5f)
                throw new ArgumentException("The held material projection is invalid.");
            if (visual == null)
            {
                visual = CreateMaterialVisual("Material bundle " + id, item.Material);
                _materialBundles.Add(id, visual);
            }
            visual.Held = true;
            // Flat tiles use the repair stack grip; hanging sheets fit one piece inside the finger curl.
            SetMaterialLayers(visual, item.Material == BoxStationMaterial.FloorTiles ? item.Count : 1);
            SetMaterialDetail(visual, true);
            visual.Frame.SetPositionAndRotation(position, rotation.normalized);
        }
        if (visual != null) SetMaterialVisible(visual, visible);
    }

    private bool TryMaterialBundle(int id, out BoxStationMaterialBundle bundle)
    {
        bundle = default;
        if (_materialState is not { } state) return false;
        int low = 0, high = state.Bundles.Length - 1;
        while (low <= high)
        {
            int middle = (low + high) / 2;
            var candidate = state.Bundles[middle];
            if (candidate.Id == id) { bundle = candidate; return true; }
            if (candidate.Id < id) low = middle + 1;
            else high = middle - 1;
        }
        return false;
    }

    internal int MaterialStructurePlaneCount(int target)
        => !BoxStationHull.TryStructure(_layout, target, out var structure) ? 0
            : structure.Kind == BoxStationStructureKind.Grille && !CenteredGrille(structure) ? 4 : 1;

    internal bool MaterialStructureBounds(int target, int plane, out Bounds bounds)
    {
        bounds = default;
        if (!BoxStationHull.TryStructure(_layout, target, out var structure)
            || plane < 0 || plane >= MaterialStructurePlaneCount(target)) return false;
        float tile = BoxStationLayout.TileSize, height = BoxStationLayout.DeckHeight;
        float inset = StructureInset(structure);
        if (structure.Kind == BoxStationStructureKind.Window && structure.Direction == 5)
        {
            bounds = new(_layout.CellPosition(structure.Cell) + Vector3.up * (height * .5f),
                new(tile - inset * 2, height, tile - inset * 2));
            return true;
        }
        bool grille = structure.Kind == BoxStationStructureKind.Grille;
        bool centered = grille && CenteredGrille(structure);
        int direction = centered ? WindowDirection(structure.Cell) : grille ? plane : PortDirection(structure.Direction);
        var position = grille ? GrillePlane(structure, direction, centered, inset) : WindowEdge(structure, direction, inset);
        bounds = new(position + Vector3.up * (height * .5f),
            direction % 2 == 0 ? new(tile, height, .1f) : new(.1f, height, tile));
        return true;
    }

    internal bool TryMaterialStructureTarget(Ray worldRay, float reach, BoxStationMaterial material,
        out int target, out Vector3 worldContact)
    {
        target = -1;
        worldContact = default;
        if (!Visible || _materialState is not { } state || state.HullRevision != _hull.State.Revision
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
                    byte integrity = _hull.StructureIntegrity(structure.Id);
                    int candidate = BoxStationHull.StructureTarget(_layout, structure.Id);
                    if (BoxStationHullRepair.MaterialCost(_layout, candidate, integrity) == 0
                        || BoxStationHullRepair.StructureMaterial(_layout, structure) != material) continue;
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

    internal bool TryMaterialYieldTarget(Ray worldRay, float reach, out int structureId,
        out BoxStationMaterial material, out int count, out Vector3 worldContact)
    {
        structureId = -1; material = default; count = 0; worldContact = default;
        if (!Visible || _materialState == null || !Finite(worldRay.origin) || !Finite(worldRay.direction)
            || worldRay.direction.sqrMagnitude < .000001f || !float.IsFinite(reach) || reach <= 0 || reach > 6) return false;
        var origin = Root.transform.InverseTransformPoint(worldRay.origin);
        var direction = Root.transform.InverseTransformDirection(worldRay.direction.normalized);
        int cell = _layout.CellAt(origin);
        if (cell < 0) return false;
        int radius = (int)MathF.Ceiling(reach / BoxStationLayout.TileSize) + 1;
        int x = cell % _layout.Width, row = cell / _layout.Width;
        float nearest = reach;
        for (int r = Math.Max(0, row - radius); r <= Math.Min(_layout.Height - 1, row + radius); r++)
            for (int c = Math.Max(0, x - radius); c <= Math.Min(_layout.Width - 1, x + radius); c++)
                foreach (var structure in BoxStationHull.StructuresAt(_layout, r * _layout.Width + c))
                {
                    if (!_materialPending.TryGetValue(structure.Id, out var pending)) continue;
                    for (int part = 0; part < 3; part++)
                    {
                        if (structure.Kind == BoxStationStructureKind.Grille && part != 2) continue;
                        var kind = MaterialYieldKind(structure, part);
                        int amount = MaterialYieldCount(pending, kind);
                        if (amount == 0) continue;
                        var point = MaterialYieldPosition(structure, kind) + Vector3.up * MaterialTop(kind, amount);
                        var delta = point - origin;
                        float distance = delta.magnitude, along = Vector3.Dot(delta, direction);
                        if (distance > nearest || along <= 0 || (delta - direction * along).sqrMagnitude > .25f * .25f) continue;
                        nearest = distance;
                        structureId = structure.Id; material = kind; count = amount;
                        worldContact = Root.transform.TransformPoint(point);
                    }
                }
        SelectConstructionYield(origin, direction, ref nearest, ref structureId, ref material, ref count, ref worldContact);
        return structureId >= 0;
    }

    private static Vector3 MaterialPoint(System.Numerics.Vector3 position, BoxStationMaterial material, int count)
        => new(position.X, position.Y + MaterialTop(material, count), position.Z);

    private static float MaterialTop(BoxStationMaterial material, int count)
        => Math.Max(0, Math.Min(count, MaterialLayerLimit(material)) - 1) * MaterialPitch(material) + (material switch
        {
            BoxStationMaterial.Rods => .025f,
            BoxStationMaterial.FloorTiles => .018f,
            BoxStationMaterial.Cable => .06f,
            BoxStationMaterial.DoorElectronics => .029f,
            BoxStationMaterial.LineSpare or BoxStationMaterial.CircuitSpare => .195f,
            BoxStationMaterial.GlassShards or BoxStationMaterial.PlasmaGlassShards => .010f,
            _ => .014f
        });

    private static float MaterialPitch(BoxStationMaterial material) => material switch
    {
        BoxStationMaterial.Rods => .027f,
        BoxStationMaterial.FloorTiles => .019f,
        BoxStationMaterial.Cable => .062f,
        BoxStationMaterial.DoorElectronics => .032f,
        BoxStationMaterial.LineSpare or BoxStationMaterial.CircuitSpare => .205f,
        _ => .014f
    };

    private static int MaterialLayerLimit(BoxStationMaterial material) => material switch
    {
        BoxStationMaterial.FloorTiles => 4,
        BoxStationMaterial.LineSpare or BoxStationMaterial.CircuitSpare => 3,
        _ => 6
    };

    private BoxStationMaterial MaterialYieldKind(BoxStationStructure structure, int part)
        => part == 2 ? BoxStationMaterial.Rods : part == 1
            ? structure.Plasma ? BoxStationMaterial.PlasmaGlassShards : BoxStationMaterial.GlassShards
            : BoxStationHullRepair.StructureMaterial(_layout, structure);

    private static int MaterialYieldCount(BoxStationPendingYield pending, BoxStationMaterial material)
        => material == BoxStationMaterial.Rods ? pending.Rods
            : material is BoxStationMaterial.GlassShards or BoxStationMaterial.PlasmaGlassShards ? pending.Shards : pending.Sheets;

    private void UpdateMaterialsDetail(Vector3 eye)
    {
        if (_materialState is not { } state) return;
        if (!_detailIncomplete && !_materialDetailDirty && Time.unscaledTime < _materialDetailTime
            && (eye - _materialDetailEye).sqrMagnitude < 1) return;
        _materialDetailDirty = false;
        _materialDetailEye = eye;
        _materialDetailTime = Time.unscaledTime + .2f;
        int budget = 6;
        UpdateConstructionDetail(eye, ref budget);
        for (int source = 0; source < _materialSources.Length; source++)
        {
            var item = _materialSources[source];
            var position = new Vector3(item.Position.X, item.Position.Y, item.Position.Z);
            var visual = _materialStocks[source];
            ShowMaterial(ref visual, position, item.Material, state.Stocks[source], eye, ref budget, "Material stock ", source);
            _materialStocks[source] = visual;
        }
        foreach (var item in state.Bundles)
        {
            if (item.Place == BoxStationMaterialPlace.Held) continue;
            var position = new Vector3(item.Position.X, item.Position.Y, item.Position.Z);
            _materialBundles.TryGetValue(item.Id, out var visual);
            var rotation = Quaternion.identity;
            if (item.Place == BoxStationMaterialPlace.Stored && !StoredMaterialPose(item, out position, out rotation))
            {
                if (visual != null) SetMaterialVisible(visual, false);
                continue;
            }
            ShowMaterial(ref visual, position, item.Material, item.Count, eye, ref budget, "Material bundle ", item.Id);
            if (visual != null)
            {
                _materialBundles[item.Id] = visual;
                visual.Frame.localRotation = rotation;
            }
            else _materialBundles.Remove(item.Id);
        }
        _retiredMaterialYields.Clear();
        foreach (var pair in _materialYields)
            if ((pair.Value.Position - eye).sqrMagnitude >= MaterialCullDistance * MaterialCullDistance)
                _retiredMaterialYields.Add(pair.Key);
        foreach (var key in _retiredMaterialYields)
        {
            DestroyMaterialVisual(_materialYields[key]);
            _materialYields.Remove(key);
        }
        UpdateConstructionYields(eye, ref budget);
        int cell = _layout.CellAt(eye);
        if (cell < 0) return;
        int radius = (int)MathF.Ceiling(MaterialCullDistance / BoxStationLayout.TileSize) + 1;
        int x = cell % _layout.Width, row = cell / _layout.Width;
        for (int r = Math.Max(0, row - radius); r <= Math.Min(_layout.Height - 1, row + radius); r++)
            for (int c = Math.Max(0, x - radius); c <= Math.Min(_layout.Width - 1, x + radius); c++)
                foreach (var structure in BoxStationHull.StructuresAt(_layout, r * _layout.Width + c))
                {
                    if (!_materialPending.TryGetValue(structure.Id, out var pending)) continue;
                    for (int part = 0; part < 3; part++)
                    {
                        var kind = MaterialYieldKind(structure, part);
                        if (structure.Kind == BoxStationStructureKind.Grille && part != 2) continue;
                        int count = MaterialYieldCount(pending, kind);
                        if (count == 0) continue;
                        var key = (structure.Id, kind);
                        _materialYields.TryGetValue(key, out var visual);
                        ShowMaterial(ref visual, MaterialYieldPosition(structure, kind), kind, count, eye, ref budget,
                            "Structural salvage ", structure.Id);
                        if (visual != null) _materialYields[key] = visual;
                        else _materialYields.Remove(key);
                    }
                }
    }

    private void ShowMaterial(ref MaterialVisual? visual, Vector3 position, BoxStationMaterial material,
        int count, Vector3 eye, ref int budget, string name, int id)
    {
        float distance = (position - eye).sqrMagnitude;
        if (count == 0 || distance >= MaterialCullDistance * MaterialCullDistance)
        {
            if (visual != null) { DestroyMaterialVisual(visual); visual = null; }
            return;
        }
        bool created = visual == null;
        if (created)
        {
            if (budget == 0) { _materialDetailDirty = true; return; }
            budget--;
            visual = CreateMaterialVisual(name + id, material);
        }
        try
        {
            visual!.Held = false;
            SetMaterialLayers(visual, count);
            SetMaterialPosition(visual, position);
            SetMaterialDetail(visual, distance < MaterialDetailDistance * MaterialDetailDistance);
            SetMaterialVisible(visual, true);
        }
        catch
        {
            if (created)
            {
                DestroyMaterialVisual(visual!);
                visual = null;
            }
            throw;
        }
    }

    private MaterialVisual CreateMaterialVisual(string name, BoxStationMaterial material)
    {
        int part = MaterialPart(material);
        int spare = part >= 7 ? part - 7 : -1;
        var finish = spare >= 0 ? _spareMaterials[spare, 0] : material == BoxStationMaterial.DoorElectronics ? _doorServiceMaterial!
            : material == BoxStationMaterial.Cable ? _cableMaterial! : material == BoxStationMaterial.FloorTiles ? _floorCoverMaterial!
            : material == BoxStationMaterial.Steel ? _steelMaterial!
            : material == BoxStationMaterial.Plasteel ? _plasteelMaterial! : part == 1 ? _surface! : material is BoxStationMaterial.PlasmaGlass
            or BoxStationMaterial.ReinforcedPlasmaGlass or BoxStationMaterial.PlasmaGlassShards ? _plasmaGlass! : _glass!;
        var frame = Node(name, Root.transform);
        frame.gameObject.SetActive(false);
        try
        {
            var filters = new MeshFilter[MaterialLayerLimit(material)];
            var hardware = material == BoxStationMaterial.FloorTiles ? new MeshFilter[filters.Length] : Array.Empty<MeshFilter>();
            var insets = material == BoxStationMaterial.FloorTiles ? new MeshFilter[filters.Length] : Array.Empty<MeshFilter>();
            var spareParts = spare >= 0 ? new MeshFilter[filters.Length, 4] : new MeshFilter[0, 0];
            for (int layer = 0; layer < filters.Length; layer++)
            {
                var node = Node("Layer " + (layer + 1), frame);
                node.localPosition = Vector3.up * (layer * MaterialPitch(material));
                Render(node, _materialMeshes[part, 1], finish);
                filters[layer] = node.GetComponent<MeshFilter>();
                if (material == BoxStationMaterial.FloorTiles)
                {
                    var fittings = Node("Captive fasteners", node);
                    Render(fittings, _floorTileDetails[0, 1], _deckHardwareMaterial!);
                    hardware[layer] = fittings.GetComponent<MeshFilter>();
                    var grip = Node("Grip inserts", node);
                    Render(grip, _floorTileDetails[1, 1], _floorInsetMaterial!);
                    insets[layer] = grip.GetComponent<MeshFilter>();
                }
                if (spare >= 0)
                    for (int detail = 1; detail < SpareParts.Length; detail++)
                    {
                        var fitting = Node("Repair kit " + SpareParts[detail], node);
                        Render(fitting, _spareMeshes[spare, detail, 1], _spareMaterials[spare, detail]);
                        spareParts[layer, detail - 1] = fitting.GetComponent<MeshFilter>();
                    }
                node.gameObject.SetActive(false);
            }
            return new(frame, filters, part, material, hardware, insets, spareParts);
        }
        catch { UnityEngine.Object.Destroy(frame.gameObject); throw; }
    }

    private static int MaterialPart(BoxStationMaterial material)
        => material == BoxStationMaterial.CircuitSpare ? 8 : material == BoxStationMaterial.LineSpare ? 7
            : material == BoxStationMaterial.DoorElectronics ? 6 : material == BoxStationMaterial.Cable ? 5
            : material == BoxStationMaterial.FloorTiles ? 4 : material is BoxStationMaterial.Steel or BoxStationMaterial.Plasteel ? 3
            : material == BoxStationMaterial.Rods ? 1 : material is BoxStationMaterial.GlassShards or BoxStationMaterial.PlasmaGlassShards ? 2 : 0;

    private bool StoredMaterialPose(BoxStationMaterialBundle item, out Vector3 position, out Quaternion rotation)
    {
        position = default; rotation = Quaternion.identity;
        return item.Place == BoxStationMaterialPlace.Stored && StoredPortablePose(item.Storage, item.Slot,
            _materialBaseOffsets[MaterialPart(item.Material)], out position, out rotation);
    }

    internal bool TryStoredMaterialPoint(int id, out Vector3 contact)
    {
        contact = default;
        if (!TryMaterialBundle(id, out var item) || !StoredMaterialPose(item, out var position, out var rotation)) return false;
        var vertices = _materialContactVertices[MaterialPart(item.Material) * 2];
        var point = vertices[0];
        foreach (var vertex in vertices)
            if (vertex.z > point.z || vertex.z == point.z && vertex.y > point.y) point = vertex;
        int layers = Math.Min(item.Count, MaterialLayerLimit(item.Material));
        contact = Root.transform.TransformPoint(position + rotation * (point + Vector3.up * ((layers - 1) * MaterialPitch(item.Material))));
        return true;
    }

    internal bool TryStoredMaterialContact(int id, Vector3 worldEye, out Vector3 contact)
    {
        contact = default;
        if (!Finite(worldEye) || !TryMaterialBundle(id, out var item)
            || !StoredMaterialPose(item, out var position, out var rotation)) return false;
        var worldPosition = Root.transform.TransformPoint(position);
        var worldRotation = Root.transform.rotation * rotation;
        int part = MaterialPart(item.Material);
        int layers = Math.Min(item.Count, MaterialLayerLimit(item.Material));
        return StoredMeshEyeContact(_materialContactVertices[part * 2], _materialContactBounds[part],
            layers, MaterialPitch(item.Material), worldPosition, worldRotation, worldEye, out contact);
    }

    internal bool TryStoredMaterialTarget(Ray ray, float reach, out int id, out Vector3 contact)
    {
        id = -1; contact = default;
        if (!Visible || _materialState is not { } state || !Finite(ray.origin) || !Finite(ray.direction)
            || ray.direction.sqrMagnitude < .000001f || !float.IsFinite(reach) || reach <= 0 || reach > 6) return false;
        ray.direction = ray.direction.normalized;
        float nearest = reach;
        foreach (var item in state.Bundles)
            if (StoredMaterialPose(item, out var position, out var rotation)
                && StoredMeshContact(_materialContactVertices[MaterialPart(item.Material) * 2],
                    _materialContactBounds[MaterialPart(item.Material)],
                    Math.Min(item.Count, MaterialLayerLimit(item.Material)), MaterialPitch(item.Material),
                    Root.transform.TransformPoint(position), Root.transform.rotation * rotation, ray, nearest, out var point))
            {
                nearest = Vector3.Distance(ray.origin, point);
                id = item.Id; contact = point;
            }
        return id >= 0;
    }

    private static void SetMaterialPosition(MaterialVisual visual, Vector3 position)
    {
        visual.Frame.localPosition = position;
        visual.Frame.localRotation = Quaternion.identity;
        visual.Position = position;
    }

    private static void SetMaterialVisible(MaterialVisual visual, bool visible)
    {
        if (visual.Active == visible) return;
        visual.Frame.gameObject.SetActive(visible);
        visual.Active = visible;
    }

    private static void SetMaterialLayers(MaterialVisual visual, int count)
    {
        int layers = Math.Min(count, visual.Filters.Length);
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

    private void SetMaterialDetail(MaterialVisual visual, bool detail)
    {
        if (!_detailIncomplete && visual.Detail == detail) return;
        int lod = detail ? 0 : 1;
        foreach (var filter in visual.Filters) filter.sharedMesh = _materialMeshes[visual.Part, lod];
        foreach (var filter in visual.FloorHardware) filter.sharedMesh = _floorTileDetails[0, lod];
        foreach (var filter in visual.FloorInsets) filter.sharedMesh = _floorTileDetails[1, lod];
        if (visual.Part >= 7)
            for (int layer = 0; layer < visual.SpareParts.GetLength(0); layer++)
                for (int part = 0; part < visual.SpareParts.GetLength(1); part++)
                    visual.SpareParts[layer, part].sharedMesh = _spareMeshes[visual.Part - 7, part + 1, lod];
        visual.Detail = detail;
    }

    private static void DestroyMaterialVisual(MaterialVisual visual)
    {
        SetMaterialVisible(visual, false);
        UnityEngine.Object.Destroy(visual.Frame.gameObject);
    }

    private void ReleaseMaterials()
    {
        _materialStocks = Array.Empty<MaterialVisual?>();
        _materialBundles.Clear();
        _materialPending.Clear();
        _materialYields.Clear();
        _retiredMaterialBundles.Clear();
        _retiredMaterialYields.Clear();
        _materialSources = ImmutableArray<BoxStationMaterialSource>.Empty;
        _materialMeshes = new Mesh[9, 2];
        _floorTileDetails = new Mesh[2, 2];
        _materialContactVertices = new Vector3[18][];
        _materialContactBounds = new Bounds[9];
        _materialBaseOffsets = new float[9];
        _materialState = null;
        _materialStateComplete = false;
    }

    private sealed class MaterialVisual(Transform frame, MeshFilter[] filters, int part, BoxStationMaterial material,
        MeshFilter[] floorHardware, MeshFilter[] floorInsets, MeshFilter[,] spareParts)
    {
        internal readonly Transform Frame = frame;
        internal readonly MeshFilter[] Filters = filters;
        internal readonly MeshFilter[] FloorHardware = floorHardware;
        internal readonly MeshFilter[] FloorInsets = floorInsets;
        internal readonly MeshFilter[,] SpareParts = spareParts;
        internal readonly int Part = part;
        internal readonly BoxStationMaterial Material = material;
        internal Vector3 Position;
        internal int Layers;
        internal bool Active, Detail, Held;
    }
}
