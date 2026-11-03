// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class BoxStationModel
{
    private readonly Dictionary<int, BoxStationConstructionDefinition> _constructionCells = new();
    private readonly List<BoxStationConstructionDefinition> _mappedConstruction = new();
    private Dictionary<int, BoxStationConstruction> _constructionYields = new();
    private Dictionary<int, ConstructionVisual> _constructionFrames = new();
    private readonly Dictionary<int, ConstructionVisual> _constructionDetails = new();
    private readonly Dictionary<int, (int Index, int Target)> _constructionSolids = new();
    private readonly List<int> _retiredConstruction = new();
    private Mesh[,] _constructionMeshes = new Mesh[2, 2];
    private Material? _constructionMaterial, _steelMaterial, _plasteelMaterial;
    private BoxStationMaterialState? _constructionState;
    private bool _constructionPending;

    internal bool ConstructionReady => _constructionState.HasValue && !_constructionPending;

    private static readonly ConstructionBar[] ConstructionBars =
    {
        new(new(-1.025f, 1.59f, 0), new(.21f, 3.18f, .295f), 0),
        new(new(1.025f, 1.59f, 0), new(.21f, 3.18f, .295f), 0),
        new(new(0, .14f, 0), new(1.87f, .18f, .20f), 0),
        new(new(0, 3.04f, 0), new(1.87f, .18f, .20f), 0),
        new(new(0, 1.59f, .047f), new(.115f, 3.2179497f, .07f), -34.875328f),
        new(new(-.77f, .37f, -.035f), new(.075f, .52345f, .07f), -133.45184f),
        new(new(.77f, .37f, -.035f), new(.075f, .52345f, .07f), 133.45184f),
        new(new(-.77f, 2.81f, -.035f), new(.075f, .52345f, .07f), -46.54816f),
        new(new(.77f, 2.81f, -.035f), new(.075f, .52345f, .07f), 46.54816f),
        new(new(0, 1.59f, -.045f), new(.135f, 3.2179497f, .078f), 34.875328f)
    };

    private IEnumerable<bool> BuildConstruction()
    {
        int count = 0;
        foreach (var definition in BoxStationHullRepair.GetConstructionDefinitions(_layout))
        {
            _constructionCells.Add(definition.Cell, definition);
            if (definition.OriginalGirder) _mappedConstruction.Add(definition);
            if (++count % 128 == 0) yield return false;
        }
        string[] parts = { "girder", "girder-reinforcement" };
        for (int part = 0; part < 2; part++)
            for (int lod = 0; lod < 2; lod++)
            {
                _constructionMeshes[part, lod] = CreateMesh(_parts[parts[part] + (lod == 0 ? "" : "-lod")]);
                yield return false;
            }
    }

    private void UpdateConstruction(BoxStationMaterialState? state)
    {
        FinishGeometryRetirement();
        if (_constructionState == state && !_constructionPending) return;
        foreach (bool _ in ApplyConstruction(state)) { }
    }

    private IEnumerable<bool> ApplyConstruction(BoxStationMaterialState? state)
    {
        FinishGeometryRetirement();
        if (_constructionState == state && !_constructionPending) yield break;
        var frames = new Dictionary<int, ConstructionVisual>();
        var yields = new Dictionary<int, BoxStationConstruction>();
        var staged = new List<ConstructionVisual>();
        var retired = new List<ConstructionVisual>();
        bool committed = false;
        try
        {
            foreach (var pair in _constructionFrames)
            {
                var row = state is { } live ? BoxStationHullRepair.ConstructionAt(_layout, live, pair.Key) : default;
                if (IsFrame(row.Stage) && ReinforcedFrame(row.Stage) == pair.Value.Reinforced)
                    frames.Add(pair.Key, pair.Value);
                else retired.Add(pair.Value);
                yield return false;
            }
            if (state is { } current)
            {
                // Collision belongs to the full authoritative state, independently of camera detail and culling.
                foreach (var definition in _mappedConstruction)
                {
                    var row = BoxStationHullRepair.ConstructionAt(_layout, current, definition.Target);
                    EnsureConstructionFrame(frames, staged, definition, row);
                    yield return false;
                }
                foreach (var row in current.Construction)
                {
                    if (row.SteelYield > 0 || row.PlasteelYield > 0) yields.Add(row.Cell, row);
                    EnsureConstructionFrame(frames, staged, _constructionCells[row.Cell], row);
                    yield return false;
                }
            }
            // Keep the completed structure until every replacement can carry collision.
            foreach (var visual in staged)
            {
                visual.Node.SetActive(true);
                yield return false;
            }
            _constructionFrames = frames;
            _constructionYields = yields;
            _constructionPending = true;
            committed = true;
            foreach (var visual in retired) DestroyConstructionFrame(visual);
            FinishGeometryRetirement();
            _constructionState = state;
            _constructionPending = false;
        }
        finally
        {
            if (!committed)
            {
                foreach (var visual in staged) DestroyConstructionFrame(visual);
                FinishGeometryRetirement();
            }
        }
    }

    private void EnsureConstructionFrame(Dictionary<int, ConstructionVisual> frames, List<ConstructionVisual> staged,
        BoxStationConstructionDefinition definition, BoxStationConstruction row)
    {
        if (!IsFrame(row.Stage) || frames.ContainsKey(definition.Target)) return;
        var visual = CreateConstructionFrame(definition, ReinforcedFrame(row.Stage));
        staged.Add(visual);
        frames.Add(definition.Target, visual);
    }

    private static bool IsFrame(BoxStationFrameStage stage)
        => stage is BoxStationFrameStage.Girder or BoxStationFrameStage.DisplacedGirder
            or BoxStationFrameStage.ReinforcedStruts or BoxStationFrameStage.ReinforcedGirder;

    private static bool ReinforcedFrame(BoxStationFrameStage stage)
        => stage is BoxStationFrameStage.ReinforcedStruts or BoxStationFrameStage.ReinforcedGirder;

    private static Vector3 ConstructionPosition(BoxStationConstructionDefinition definition)
        => new(definition.Position.X, definition.Position.Y, definition.Position.Z);

    private ConstructionVisual CreateConstructionFrame(BoxStationConstructionDefinition definition, bool reinforced)
    {
        var frame = Node("Construction frame " + definition.Cell, Root.transform);
        var visual = new ConstructionVisual(definition.Target, frame, reinforced);
        try
        {
            visual.Node.SetActive(false);
            frame.localPosition = ConstructionPosition(definition);
            frame.localRotation = Turn(WindowDirection(definition.Cell));
            for (int bar = 0; bar < (reinforced ? ConstructionBars.Length : 5); bar++)
            {
                var shape = ConstructionBars[bar];
                var node = Node("Bar " + bar, frame);
                node.localPosition = shape.Center;
                node.localRotation = Quaternion.Euler(0, 0, shape.Angle);
                var collider = AddSolid(node, Vector3.zero, shape.Size, true, out int index);
                visual.Solids.Add(index);
                _constructionSolids.Add(collider.GetInstanceID(), (index, definition.Target));
            }
            return visual;
        }
        catch { DestroyConstructionFrame(visual); throw; }
    }

    private void DestroyConstructionFrame(ConstructionVisual visual)
    {
        if (_constructionDetails.TryGetValue(visual.Target, out var detail) && ReferenceEquals(detail, visual))
            _constructionDetails.Remove(visual.Target);
        foreach (int index in visual.Solids) QueueSolidRetirement(index);
        QueueNodeRetirement(visual.Node);
    }

    private void UpdateConstructionDetail(Vector3 eye, ref int budget)
    {
        _retiredConstruction.Clear();
        foreach (var visual in _constructionDetails.Values)
            if (visual.Detail == null || (visual.Frame.localPosition - eye).sqrMagnitude >= MaterialCullDistance * MaterialCullDistance)
            {
                if (visual.Detail != null)
                {
                    visual.Detail.gameObject.SetActive(false);
                    UnityEngine.Object.Destroy(visual.Detail.gameObject);
                }
                visual.Detail = null;
                visual.Base = visual.Reinforcement = null;
                _retiredConstruction.Add(visual.Target);
            }
        foreach (int target in _retiredConstruction) _constructionDetails.Remove(target);
        int cell = _layout.CellAt(eye);
        if (cell < 0) return;
        int radius = (int)MathF.Ceiling(MaterialCullDistance / BoxStationLayout.TileSize) + 1;
        int x = cell % _layout.Width, row = cell / _layout.Width;
        for (int r = Math.Max(0, row - radius); r <= Math.Min(_layout.Height - 1, row + radius); r++)
            for (int c = Math.Max(0, x - radius); c <= Math.Min(_layout.Width - 1, x + radius); c++)
            {
                if (!_constructionCells.TryGetValue(r * _layout.Width + c, out var definition)
                    || !_constructionFrames.TryGetValue(definition.Target, out var visual)) continue;
                float distance = (visual.Frame.localPosition - eye).sqrMagnitude;
                if (distance >= MaterialCullDistance * MaterialCullDistance) continue;
                int lod = distance < MaterialDetailDistance * MaterialDetailDistance ? 0 : 1;
                if (visual.Detail == null)
                {
                    if (budget == 0) { _materialDetailDirty = true; continue; }
                    budget--;
                    var detail = Node("Girder", visual.Frame);
                    try
                    {
                        detail.gameObject.SetActive(false);
                        Render(detail, _constructionMeshes[0, lod], _constructionMaterial!);
                        var baseFilter = detail.GetComponent<MeshFilter>();
                        MeshFilter? reinforcement = null;
                        if (visual.Reinforced)
                        {
                            var overlay = Node("Reinforcement", detail);
                            Render(overlay, _constructionMeshes[1, lod], _constructionMaterial!);
                            reinforcement = overlay.GetComponent<MeshFilter>();
                        }
                        detail.gameObject.SetActive(true);
                        _constructionDetails.Add(definition.Target, visual);
                        visual.Detail = detail;
                        visual.Base = baseFilter;
                        visual.Reinforcement = reinforcement;
                    }
                    catch
                    {
                        detail.gameObject.SetActive(false);
                        UnityEngine.Object.Destroy(detail.gameObject);
                        throw;
                    }
                }
                else
                {
                    visual.Base!.sharedMesh = _constructionMeshes[0, lod];
                    if (visual.Reinforcement != null) visual.Reinforcement.sharedMesh = _constructionMeshes[1, lod];
                }
            }
    }

    internal bool IsConstructionCollider(Collider collider, int target)
        => collider && _constructionSolids.TryGetValue(collider.GetInstanceID(), out var solid)
            && solid.Target == target && _solids[solid.Index] == collider;

    internal bool TryConstructionImpact(Collider collider, Vector3 worldPoint, out int target)
    {
        target = -1;
        if (!Visible || !collider || !collider.enabled || !collider.gameObject.activeInHierarchy || !Finite(worldPoint)
            || !_constructionSolids.TryGetValue(collider.GetInstanceID(), out var component)) return false;
        var solid = _solids[component.Index];
        if (solid == null || !solid || solid != collider) return false;
        var local = solid.transform.InverseTransformPoint(worldPoint) - solid.center;
        var half = solid.size * .5f;
        if (!Finite(local) || MathF.Abs(local.x) > half.x + .015f || MathF.Abs(local.y) > half.y + .015f
            || MathF.Abs(local.z) > half.z + .015f) return false;
        target = component.Target;
        return true;
    }

    internal bool ConstructionWorkContact(int target, Vector3 localContact)
    {
        if (!Finite(localContact) || _constructionState is not { } state || state.HullRevision != _hull.State.Revision
            || !BoxStationHullRepair.TryConstruction(_layout, target, out var definition)) return false;
        var construction = BoxStationHullRepair.ConstructionAt(_layout, state, target);
        var point = localContact - ConstructionPosition(definition);
        if (construction.Stage == BoxStationFrameStage.Empty)
            return MathF.Abs(point.y) <= .02f && MathF.Abs(point.x) <= BoxStationLayout.TileSize * .5f
                && MathF.Abs(point.z) <= BoxStationLayout.TileSize * .5f;
        if (construction.Stage == BoxStationFrameStage.Wall)
            return _hull.Integrity(target) > 0 && ConstructionFace(point - Vector3.up * (BoxStationLayout.DeckHeight * .5f),
                new(BoxStationLayout.TileSize, BoxStationLayout.DeckHeight, BoxStationLayout.TileSize));
        point = Quaternion.Inverse(Turn(WindowDirection(definition.Cell))) * point;
        for (int bar = 0; bar < (ReinforcedFrame(construction.Stage) ? ConstructionBars.Length : 5); bar++)
        {
            var shape = ConstructionBars[bar];
            var relative = Quaternion.Inverse(Quaternion.Euler(0, 0, shape.Angle)) * (point - shape.Center);
            if (ConstructionFace(relative, shape.Size)) return true;
        }
        return false;
    }

    private static bool ConstructionFace(Vector3 point, Vector3 size)
    {
        var half = size * .5f;
        float x = MathF.Abs(point.x), y = MathF.Abs(point.y), z = MathF.Abs(point.z);
        return x <= half.x + .02f && y <= half.y + .02f && z <= half.z + .02f
            && (MathF.Abs(x - half.x) <= .02f || MathF.Abs(y - half.y) <= .02f || MathF.Abs(z - half.z) <= .02f);
    }

    internal bool TryConstructionMaterialTarget(Ray worldRay, float reach, BoxStationMaterial kind,
        out int target, out Vector3 worldContact)
        => TryConstructionTarget(worldRay, reach, true, out target, out worldContact, kind);

    private bool TryConstructionTarget(Ray worldRay, float reach, bool material, out int target, out Vector3 worldContact,
        BoxStationMaterial kind = default)
    {
        target = -1; worldContact = default;
        if (!Visible || _constructionState is not { } state || state.HullRevision != _hull.State.Revision
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
            {
                if (!_constructionCells.TryGetValue(r * _layout.Width + c, out var definition)) continue;
                var construction = BoxStationHullRepair.ConstructionAt(_layout, state, definition.Target);
                if (material && !BoxStationHullRepair.ResolveConstructionMaterial(_layout, construction, kind, out _, out _, out _)) continue;
                var origin = ray.origin - ConstructionPosition(definition);
                if (construction.Stage == BoxStationFrameStage.Empty)
                {
                    if (!material || MathF.Abs(ray.direction.y) < .00001f) continue;
                    float distance = -origin.y / ray.direction.y;
                    var point = origin + ray.direction * distance;
                    if (distance < 0 || distance > nearest || MathF.Abs(point.x) > BoxStationLayout.TileSize * .5f
                        || MathF.Abs(point.z) > BoxStationLayout.TileSize * .5f) continue;
                    nearest = distance; target = definition.Target;
                }
                else if (construction.Stage == BoxStationFrameStage.Wall)
                {
                    var bounds = new Bounds(Vector3.up * (BoxStationLayout.DeckHeight * .5f),
                        new(BoxStationLayout.TileSize, BoxStationLayout.DeckHeight, BoxStationLayout.TileSize));
                    if (!bounds.Contains(origin) && bounds.IntersectRay(new(origin, ray.direction), out float distance)
                        && distance >= 0 && distance <= nearest) { nearest = distance; target = definition.Target; }
                }
                else
                {
                    var yaw = Quaternion.Inverse(Turn(WindowDirection(definition.Cell)));
                    origin = yaw * origin;
                    var frameDirection = yaw * ray.direction;
                    for (int bar = 0; bar < (ReinforcedFrame(construction.Stage) ? ConstructionBars.Length : 5); bar++)
                    {
                        var shape = ConstructionBars[bar];
                        var inverse = Quaternion.Inverse(Quaternion.Euler(0, 0, shape.Angle));
                        var local = new Ray(inverse * (origin - shape.Center), inverse * frameDirection);
                        var bounds = new Bounds(Vector3.zero, shape.Size);
                        if (!bounds.Contains(local.origin) && bounds.IntersectRay(local, out float distance)
                            && distance >= 0 && distance <= nearest) { nearest = distance; target = definition.Target; }
                    }
                }
            }
        if (target < 0) return false;
        worldContact = worldRay.origin + direction * nearest;
        return Finite(worldContact);
    }

    private void LoadConstructionMaterials(Shader shader)
    {
        _constructionMaterial = new Material(shader) { name = "Station girder steel", enableInstancing = true };
        _constructionMaterial.SetColor("_BaseColor", Color.white);
        _constructionMaterial.SetTexture("_BaseColorMap", LoadTexture("girder-albedo", false));
        _constructionMaterial.SetTexture("_MaskMap", LoadTexture("girder-mask", true));
        _constructionMaterial.SetTexture("_NormalMap", LoadTexture("girder-normal", true, true));
        _constructionMaterial.SetFloat("_NormalMapSpace", 0); _constructionMaterial.SetFloat("_NormalScale", 1);
        _constructionMaterial.SetFloat("_MetallicRemapMin", 0); _constructionMaterial.SetFloat("_MetallicRemapMax", 1);
        _constructionMaterial.SetFloat("_SmoothnessRemapMin", 0); _constructionMaterial.SetFloat("_SmoothnessRemapMax", 1);
        _constructionMaterial.SetFloat("_AORemapMin", 0); _constructionMaterial.SetFloat("_AORemapMax", 1);
        _constructionMaterial.EnableKeyword("_MASKMAP");
        _constructionMaterial.EnableKeyword("_NORMALMAP");
        _constructionMaterial.EnableKeyword("_NORMALMAP_TANGENT_SPACE");
        _steelMaterial = new Material(_repairMaterial!) { name = "Station steel sheets" };
        _plasteelMaterial = new Material(_repairMaterial!) { name = "Station plasteel sheets" };
        _plasteelMaterial.SetColor("_BaseColor", new Color(.67f, .56f, .76f, 1));
    }

    private int ConstructionYieldCount(int target, BoxStationMaterial material)
    {
        if (_constructionState is not { } state) return 0;
        var row = BoxStationHullRepair.ConstructionAt(_layout, state, target);
        return material == BoxStationMaterial.Steel ? row.SteelYield
            : material == BoxStationMaterial.Plasteel ? row.PlasteelYield : 0;
    }

    private Vector3 ConstructionYieldPosition(BoxStationConstructionDefinition definition, BoxStationMaterial material)
        => ConstructionPosition(definition) + new Vector3(material == BoxStationMaterial.Steel ? -.36f : .36f, .015f, .38f);

    private void SelectConstructionYield(Vector3 origin, Vector3 direction, ref float nearest, ref int target,
        ref BoxStationMaterial material, ref int count, ref Vector3 worldContact)
    {
        int cell = _layout.CellAt(origin);
        if (cell < 0) return;
        int radius = (int)MathF.Ceiling(nearest / BoxStationLayout.TileSize) + 1;
        int x = cell % _layout.Width, r0 = cell / _layout.Width;
        for (int r = Math.Max(0, r0 - radius); r <= Math.Min(_layout.Height - 1, r0 + radius); r++)
        for (int c = Math.Max(0, x - radius); c <= Math.Min(_layout.Width - 1, x + radius); c++)
        {
            if (!_constructionYields.TryGetValue(r * _layout.Width + c, out var row)) continue;
            var definition = _constructionCells[row.Cell];
            for (int part = 0; part < 2; part++)
            {
                var kind = part == 0 ? BoxStationMaterial.Steel : BoxStationMaterial.Plasteel;
                int amount = part == 0 ? row.SteelYield : row.PlasteelYield;
                if (amount == 0) continue;
                var point = ConstructionYieldPosition(definition, kind) + Vector3.up * MaterialTop(kind, amount);
                var delta = point - origin;
                float distance = delta.magnitude, along = Vector3.Dot(delta, direction);
                if (distance > nearest || along <= 0 || (delta - direction * along).sqrMagnitude > .25f * .25f) continue;
                nearest = distance; target = definition.Target; material = kind; count = amount;
                worldContact = Root.transform.TransformPoint(point);
            }
        }
    }

    private void UpdateConstructionYields(Vector3 eye, ref int budget)
    {
        int cell = _layout.CellAt(eye);
        if (cell < 0) return;
        int radius = (int)MathF.Ceiling(MaterialCullDistance / BoxStationLayout.TileSize) + 1;
        int x = cell % _layout.Width, r0 = cell / _layout.Width;
        for (int r = Math.Max(0, r0 - radius); r <= Math.Min(_layout.Height - 1, r0 + radius); r++)
        for (int c = Math.Max(0, x - radius); c <= Math.Min(_layout.Width - 1, x + radius); c++)
        {
            if (!_constructionYields.TryGetValue(r * _layout.Width + c, out var row)) continue;
            var definition = _constructionCells[row.Cell];
            for (int part = 0; part < 2; part++)
            {
                var kind = part == 0 ? BoxStationMaterial.Steel : BoxStationMaterial.Plasteel;
                int count = part == 0 ? row.SteelYield : row.PlasteelYield;
                if (count == 0) continue;
                var key = (definition.Target, kind);
                _materialYields.TryGetValue(key, out var visual);
                ShowMaterial(ref visual, ConstructionYieldPosition(definition, kind), kind, count, eye, ref budget,
                    "Construction salvage ", definition.Cell);
                if (visual != null) _materialYields[key] = visual;
                else _materialYields.Remove(key);
            }
        }
    }

    private void ReleaseConstruction()
    {
        foreach (var frame in _constructionFrames.Values) DestroyConstructionFrame(frame);
        FinishGeometryRetirement();
        _constructionFrames.Clear();
        _constructionDetails.Clear();
        _constructionCells.Clear();
        _mappedConstruction.Clear();
        _constructionYields.Clear();
        _constructionSolids.Clear();
        _retiredConstruction.Clear();
        _constructionState = null;
        _constructionPending = false;
        _constructionMeshes = new Mesh[2, 2];
        if (_constructionMaterial) UnityEngine.Object.Destroy(_constructionMaterial);
        if (_steelMaterial) UnityEngine.Object.Destroy(_steelMaterial);
        if (_plasteelMaterial) UnityEngine.Object.Destroy(_plasteelMaterial);
        _constructionMaterial = _steelMaterial = _plasteelMaterial = null;
    }

    private readonly record struct ConstructionBar(Vector3 Center, Vector3 Size, float Angle);

    private sealed class ConstructionVisual(int target, Transform frame, bool reinforced)
    {
        internal readonly int Target = target;
        internal readonly GameObject Node = frame.gameObject;
        internal readonly Transform Frame = frame;
        internal readonly bool Reinforced = reinforced;
        internal readonly List<int> Solids = new();
        internal Transform? Detail;
        internal MeshFilter? Base, Reinforcement;
    }
}
