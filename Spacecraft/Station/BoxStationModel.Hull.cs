// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class BoxStationModel
{
    private const float HullThickness = .18f, HullApertureHalf = 1;
    private readonly Dictionary<int, HullSolid> _hullSolids = new();
    private readonly List<int>?[] _hullRows;
    private BoxStationHull _hull;
    private Material? _hullBreach;

    internal BoxStationHullState HullState => _hull.State;

    // Called on the Unity thread after a complete snapshot has been assembled.
    internal bool ApplyHull(BoxStationHullState state)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        FinishGeometryRetirement();
        var previous = _hull.State;
        if (state.Revision < previous.Revision || state.Panels.IsDefault) return false;
        if (state.Revision == previous.Revision)
        {
            if (state.Panels == previous.Panels) return true;
            if (state.Panels.Length != previous.Panels.Length) return false;
            for (int i = 0; i < state.Panels.Length; i++)
                if (state.Panels[i] != previous.Panels[i]) return false;
            return true;
        }

        var next = new BoxStationHull(_layout);
        if (!next.TryApply(state)) return false;
        var rows = new HashSet<int>();
        var wallRows = new HashSet<int>();
        var chunks = new HashSet<int>();
        int oldIndex = 0, newIndex = 0;
        while (oldIndex < previous.Panels.Length || newIndex < state.Panels.Length)
        {
            int oldTarget = oldIndex < previous.Panels.Length ? previous.Panels[oldIndex].Target : int.MaxValue;
            int newTarget = newIndex < state.Panels.Length ? state.Panels[newIndex].Target : int.MaxValue;
            int target = Math.Min(oldTarget, newTarget);
            byte initial = BoxStationHull.InitialIntegrity(_layout, target);
            var initialKind = BoxStationHull.InitialWallKind(_layout, target);
            var oldPanel = oldTarget == target ? previous.Panels[oldIndex++] : new(target, initial, false, initialKind);
            var newPanel = newTarget == target ? state.Panels[newIndex++] : new(target, initial, false, initialKind);
            if (BoxStationHull.TryStructure(_layout, target, out var structure))
            {
                if (StructureStage(structure, oldPanel.Integrity) != StructureStage(structure, newPanel.Integrity))
                    AddStructureChanges(structure.Cell, wallRows, chunks);
                continue;
            }
            bool wasBreached = oldPanel.Integrity == 0, breached = newPanel.Integrity == 0;
            bool surfaceChanged = wasBreached != breached || oldPanel.Foam != newPanel.Foam;
            if (!surfaceChanged && oldPanel.WallKind == newPanel.WallKind) continue;
            int wall = BoxStationHull.WallCell(_layout, target);
            if (wall >= 0)
            {
                if (!surfaceChanged)
                {
                    int wallRow = wall / _layout.Width, wallX = wall % _layout.Width;
                    chunks.Add(wallRow / ChunkSize * ChunkSize * _layout.Width + wallX / ChunkSize * ChunkSize);
                    continue;
                }
                rows.Add(wall / _layout.Width * 2);
                rows.Add(wall / _layout.Width * 2 + 1);
                AddStructureChanges(wall, wallRows, chunks);
                continue;
            }
            int cell = target / 2, row = cell / _layout.Width, x = cell % _layout.Width;
            if (wasBreached != breached) rows.Add(row * 2 + (target & 1));
            chunks.Add(row / ChunkSize * ChunkSize * _layout.Width + x / ChunkSize * ChunkSize);
        }
        if (chunks.Count == 0)
        {
            _hull = next;
            return true;
        }

        var replacements = new List<(Chunk Previous, Chunk Next)>();
        var collision = new List<(int Row, List<int> Solids)>();
        var wallCollision = new List<(int Row, List<int> Solids)>();
        try
        {
            foreach (var chunk in _chunks)
            {
                if (!chunks.Contains(chunk.Row * _layout.Width + chunk.X)) continue;
                var replacement = new Chunk(chunk.X, chunk.Row);
                replacements.Add((chunk, replacement));
                replacement.Coarse = BuildChunk(replacement, true, next, false);
                if (chunk.Detail == null) continue;
                replacement.Detail = BuildChunk(replacement, false, next, false);
            }
            foreach (int row in rows)
                collision.Add((row, BuildHullRow(row / 2, (row & 1) != 0, next, false)));
            foreach (int row in wallRows)
                wallCollision.Add((row, BuildWallRow(row, next, false)));
            foreach (var row in collision)
                foreach (int index in row.Solids) _solids[index]!.enabled = true;
            foreach (var row in wallCollision)
                foreach (int index in row.Solids) _solids[index]!.enabled = true;
            foreach (var replacement in replacements)
            {
                if (replacement.Next.Coarse != null) replacement.Next.Coarse.Node.SetActive(replacement.Next.Detail == null);
                if (replacement.Next.Detail != null) replacement.Next.Detail.Node.SetActive(true);
            }
        }
        catch
        {
            foreach (var replacement in replacements)
            {
                if (replacement.Next.Coarse != null) QueueBatchRetirement(replacement.Next.Coarse);
                if (replacement.Next.Detail != null) QueueBatchRetirement(replacement.Next.Detail);
            }
            foreach (var row in collision)
                foreach (int index in row.Solids) QueueSolidRetirement(index);
            foreach (var row in wallCollision)
                foreach (int index in row.Solids) QueueSolidRetirement(index);
            throw;
        }

        // Every replacement is active before the old geometry can be retired.
        foreach (var row in collision)
        {
            if (_hullRows[row.Row] is { } old)
                foreach (int index in old) QueueSolidRetirement(index);
            _hullRows[row.Row] = row.Solids;
        }
        foreach (var row in wallCollision)
        {
            if (_wallRows[row.Row] is { } old)
                foreach (int index in old) QueueSolidRetirement(index);
            _wallRows[row.Row] = row.Solids;
        }
        foreach (var replacement in replacements)
        {
            var chunk = replacement.Previous;
            if (chunk.Coarse != null) QueueBatchRetirement(chunk.Coarse);
            if (chunk.Detail != null) QueueBatchRetirement(chunk.Detail);
            chunk.Coarse = replacement.Next.Coarse;
            chunk.Detail = replacement.Next.Detail;
        }
        _hull = next;
        FinishGeometryRetirement();
        return true;
    }

    internal bool TryHullImpact(Collider collider, Vector3 worldPoint, out int target)
    {
        target = -1;
        if (!Visible || !collider || !collider.enabled || !collider.gameObject.activeInHierarchy || !Finite(worldPoint)
            || !_hullSolids.TryGetValue(collider.GetInstanceID(), out var span)) return false;
        var solid = _solids[span.Index];
        if (solid == null || !solid || solid != collider) return false;
        var local = Root.transform.InverseTransformPoint(worldPoint);
        if (!Finite(local)) return false;
        var offset = local - solid.center;
        var half = solid.size * .5f;
        const float tolerance = .015f;
        if (MathF.Abs(offset.x) > half.x + tolerance || MathF.Abs(offset.y) > half.y + tolerance
            || MathF.Abs(offset.z) > half.z + tolerance) return false;
        float plane = span.Roof ? BoxStationLayout.DeckHeight : 0;
        if (span.Roof ? local.y < plane - tolerance || local.y > plane + HullThickness + tolerance
            : local.y < -HullThickness - tolerance || local.y > tolerance) return false;
        // A contact on the closed edge of a merged run belongs to that run's end tile.
        int x = Math.Clamp(X(local.x), span.X, span.End - 1);
        int cell = span.Row * _layout.Width + x;
        if (InHullAperture(cell, local, span.Roof)) return false;
        int panel = cell * 2 + (span.Roof ? 1 : 0);
        if (!BoxStationHull.ValidPanelTarget(_layout, panel)) return false;
        target = panel;
        return true;
    }

    private bool InHullAperture(int cell, Vector3 local, bool roof)
    {
        if (!_hull.Breached(cell, roof)) return false;
        var offset = local - _layout.CellPosition(cell);
        return MathF.Abs(offset.x) < HullApertureHalf && MathF.Abs(offset.z) < HullApertureHalf;
    }

    private List<int> BuildHullRow(int row, bool roof, BoxStationHull hull, bool enabled)
    {
        var solids = new List<int>();
        try
        {
            int x = 0;
            while (x < _layout.Width)
            {
                int cell = row * _layout.Width + x;
                bool supportOnly = HullSupportOnly(cell, roof, hull);
                if (!supportOnly && !BoxStationHull.ValidPanelTarget(_layout, cell * 2 + (roof ? 1 : 0))) { x++; continue; }
                bool breached = !supportOnly && hull.Breached(cell, roof);
                int end = x + 1;
                while (end < _layout.Width)
                {
                    int nextCell = row * _layout.Width + end;
                    if (HullSupportOnly(nextCell, roof, hull) != supportOnly
                        || !supportOnly && (!BoxStationHull.ValidPanelTarget(_layout, nextCell * 2 + (roof ? 1 : 0))
                            || hull.Breached(nextCell, roof) != breached)) break;
                    end++;
                }
                var position = _layout.CellPosition(cell);
                float tile = BoxStationLayout.TileSize;
                float width = (end - x) * tile;
                float left = position.x - tile * .5f;
                position.x = left + width * .5f;
                position.y = roof ? BoxStationLayout.DeckHeight + HullThickness * .5f : -HullThickness * .5f;
                if (supportOnly)
                {
                    // Exposed wall support and floor-backed window ceilings have no original panel target.
                    AddSolid(Root.transform, position, new(width, HullThickness, tile), enabled, out int index);
                    solids.Add(index);
                }
                else if (!breached) AddHullSolid(position, new(width, HullThickness, tile), row, x, end, roof, enabled, solids);
                else
                {
                    float rim = tile * .5f - HullApertureHalf;
                    float edge = HullApertureHalf + rim * .5f;
                    AddHullSolid(position + Vector3.forward * edge, new(width, HullThickness, rim), row, x, end, roof, enabled, solids);
                    AddHullSolid(position - Vector3.forward * edge, new(width, HullThickness, rim), row, x, end, roof, enabled, solids);
                    AddHullSolid(new(left + rim * .5f, position.y, position.z), new(rim, HullThickness, HullApertureHalf * 2),
                        row, x, x + 1, roof, enabled, solids);
                    AddHullSolid(new(left + width - rim * .5f, position.y, position.z), new(rim, HullThickness, HullApertureHalf * 2),
                        row, end - 1, end, roof, enabled, solids);
                    // Adjacent panel rims share one rib; the aperture itself stays clear in both detail levels.
                    for (int boundary = x + 1; boundary < end; boundary++)
                        AddHullSolid(new(left + (boundary - x) * tile, position.y, position.z),
                            new(rim * 2, HullThickness, HullApertureHalf * 2), row, boundary - 1, boundary + 1, roof, enabled, solids);
                }
                x = end;
            }
            return solids;
        }
        catch { ReleaseSolidRow(solids); throw; }
    }

    private bool HullSupportOnly(int cell, bool roof, BoxStationHull hull)
    {
        if (hull.WallBreached(cell)) return true;
        const BoxStationCellFlags windowFloor = BoxStationCellFlags.Floor | BoxStationCellFlags.FullWindow;
        var flags = _layout.Cells[cell].Flags;
        return roof && (flags & (windowFloor | BoxStationCellFlags.Wall | BoxStationCellFlags.Roof)) == windowFloor;
    }

    private void AddHullSolid(Vector3 center, Vector3 size, int row, int x, int end, bool roof, bool enabled, List<int> solids)
    {
        var solid = AddSolid(Root.transform, center, size, enabled, out int index);
        solids.Add(index);
        _hullSolids.Add(solid.GetInstanceID(), new(index, row, x, end, roof));
    }

    private void ReleaseSolidRow(List<int> solids)
    {
        foreach (int index in solids)
            QueueSolidRetirement(index);
        solids.Clear();
        FinishGeometryRetirement();
    }

    private void LoadHullMaterial(Shader shader)
    {
        _hullBreach = new Material(shader) { name = "Ruptured hull finish", enableInstancing = true };
        _hullBreach.SetColor("_BaseColor", Color.white);
        _hullBreach.SetTexture("_BaseColorMap", LoadTexture("hull-breach-base", false));
        _hullBreach.SetTexture("_MaskMap", LoadTexture("hull-breach-mask", true));
        _hullBreach.SetTexture("_NormalMap", LoadTexture("hull-breach-normal", true, true));
        _hullBreach.SetFloat("_NormalMapSpace", 0); _hullBreach.SetFloat("_NormalScale", 1);
        _hullBreach.SetFloat("_MetallicRemapMin", 0); _hullBreach.SetFloat("_MetallicRemapMax", 1);
        _hullBreach.SetFloat("_SmoothnessRemapMin", 0); _hullBreach.SetFloat("_SmoothnessRemapMax", 1);
        _hullBreach.SetFloat("_AORemapMin", 0); _hullBreach.SetFloat("_AORemapMax", 1);
        _hullBreach.EnableKeyword("_MASKMAP");
        _hullBreach.EnableKeyword("_NORMALMAP");
        _hullBreach.EnableKeyword("_NORMALMAP_TANGENT_SPACE");
    }

    private readonly record struct HullSolid(int Index, int Row, int X, int End, bool Roof);
}
