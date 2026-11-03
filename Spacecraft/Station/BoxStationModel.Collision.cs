// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class BoxStationModel
{
    private readonly List<SolidRegistration> _solidRegistrations = new();
    private readonly Stack<int> _freeSolids = new();
    private readonly List<(int Index, BoxCollider Solid)> _retiredSolids = new();
    private readonly List<GameObject> _retiredNodes = new();
    private readonly List<Batch> _retiredBatches = new();

    internal bool GeometryRetirementPending => _retiredSolids.Count != 0 || _retiredNodes.Count != 0 || _retiredBatches.Count != 0;

    private void QueueSolidRetirement(int index)
    {
        if (_solids[index] is { } solid) _retiredSolids.Add((index, solid));
    }

    private void QueueNodeRetirement(GameObject node) => _retiredNodes.Add(node);
    private void QueueBatchRetirement(Batch batch) => _retiredBatches.Add(batch);

    private void FinishGeometryRetirement()
    {
        while (_retiredSolids.Count != 0)
        {
            var entry = _retiredSolids[^1];
            // An index may have been reused after another owner completed the same retirement.
            if (ReferenceEquals(_solids[entry.Index], entry.Solid)) RemoveSolid(entry.Index);
            _retiredSolids.RemoveAt(_retiredSolids.Count - 1);
        }
        while (_retiredNodes.Count != 0)
        {
            var node = _retiredNodes[^1];
            if (node) { node.SetActive(false); UnityEngine.Object.Destroy(node); }
            _retiredNodes.RemoveAt(_retiredNodes.Count - 1);
        }
        while (_retiredBatches.Count != 0)
        {
            ReleaseBatch(_retiredBatches[^1]);
            _retiredBatches.RemoveAt(_retiredBatches.Count - 1);
        }
    }

    internal int AtmosphereCellAt(Vector3 worldChest)
    {
        if (!Visible || !Finite(worldChest)) return -1;
        var point = Root.transform.InverseTransformPoint(worldChest);
        if (!Finite(point) || point.y is <= 0 or >= BoxStationLayout.DeckHeight) return -1;
        int cell = _layout.CellAt(point);
        if (cell < 0) return -1;
        var source = _layout.GetCell(cell);
        if ((_hull.EffectiveFlags(cell) & (BoxStationCellFlags.Wall | BoxStationCellFlags.FullWindow | BoxStationCellFlags.Grille)) != 0)
            return -1;
        return source.Zone >= 0 || _hull.CavityActive(cell) ? cell : -1;
    }

    internal bool TryWalkSurface(Vector3 worldFeet, out Vector3 surface)
    {
        surface = default;
        if (!Visible || !Finite(worldFeet)) return false;
        var local = Root.transform.InverseTransformPoint(worldFeet);
        if (!Finite(local) || local.y is < -.35f or > .7f) return false;
        int cell = _layout.CellAt(local);
        bool supported = false;
        if (cell >= 0)
        {
            var flags = EffectiveFlags(_layout.GetCell(cell), _hull);
            if ((flags & (BoxStationCellFlags.Wall | BoxStationCellFlags.FullWindow | BoxStationCellFlags.Grille)) != 0)
                return false;
            if (InHullAperture(cell, local, false)) return false;
            supported = (flags & (BoxStationCellFlags.Floor | BoxStationCellFlags.Catwalk)) != 0;
        }
        if (!supported && !DockSurfaceAt(local)) return false;
        // Check the whole resting capsule; a door or wall cannot become a support surface.
        var up = Root.transform.up;
        surface = Root.transform.TransformPoint(new Vector3(local.x, 0, local.z));
        return CrewPathClear(surface + up * ClientSpacecraftBody.CrewCapsuleBottom,
            surface + up * ClientSpacecraftBody.CrewCapsuleTop, Vector3.zero);
    }

    internal bool CrewPathClear(Vector3 worldBottom, Vector3 worldTop, Vector3 worldTravel)
    {
        if (!Visible || !Finite(worldBottom) || !Finite(worldTop) || !Finite(worldTravel)) return false;
        var frame = Root.transform;
        var a = frame.InverseTransformPoint(worldBottom);
        var b = frame.InverseTransformPoint(worldTop);
        var travel = frame.InverseTransformDirection(worldTravel);
        if (!Finite(a) || !Finite(b) || !Finite(travel) || a.sqrMagnitude > 25_000_000
            || b.sqrMagnitude > 25_000_000 || travel.sqrMagnitude > 25_000_000) return false;
        float radius = ClientSpacecraftBody.CrewCapsuleRadius + ClientSpacecraftBody.CrewClearance;
        var min = Vector3.Min(Vector3.Min(a, b), Vector3.Min(a + travel, b + travel)) - Vector3.one * radius;
        var max = Vector3.Max(Vector3.Max(a, b), Vector3.Max(a + travel, b + travel)) + Vector3.one * radius;
        int xMin = X(min.x), xMax = X(max.x), rowMin = Row(max.z), rowMax = Row(min.z);
        // One swept query uses each collider once, even when a merged deck spans many grid cells.
        if (_query == int.MaxValue) { Array.Clear(_visited); _query = 0; }
        _query++;
        foreach (int index in _outsideSolids)
            if (!CrewSolidClear(index, worldBottom, worldTop, worldTravel)) return false;
        if (xMax < 0 || rowMax < 0 || xMin >= _layout.Width || rowMin >= _layout.Height) return true;
        for (int row = Math.Max(0, rowMin); row <= Math.Min(rowMax, _layout.Height - 1); row++)
            for (int x = Math.Max(0, xMin); x <= Math.Min(xMax, _layout.Width - 1); x++)
            {
                int cell = row * _layout.Width + x;
                if ((_hull.EffectiveFlags(cell) & (BoxStationCellFlags.FullWindow | BoxStationCellFlags.Grille)) != 0)
                {
                    // Source full-cell density applies to crew even when a grille's impact box is one thin plane.
                    var center = _layout.CellPosition(cell) + Vector3.up * (BoxStationLayout.DeckHeight * .5f);
                    if (!ClientSpacecraftBody.CrewBoxClear(a - center, b - center, travel,
                        new(BoxStationLayout.TileSize * .5f, BoxStationLayout.DeckHeight * .5f, BoxStationLayout.TileSize * .5f)))
                        return false;
                }
                var indices = _cellSolids[cell];
                if (indices == null) continue;
                foreach (int index in indices)
                    if (!CrewSolidClear(index, worldBottom, worldTop, worldTravel)) return false;
            }
        return true;
    }

    private bool CrewSolidClear(int index, Vector3 worldBottom, Vector3 worldTop, Vector3 worldTravel)
    {
        if (_visited[index] == _query) return true;
        _visited[index] = _query;
        var solid = _solids[index];
        return solid == null || !solid || !solid.enabled || !solid.gameObject.activeInHierarchy
            || ClientSpacecraftBody.CrewBoxClear(solid, worldBottom, worldTop, worldTravel);
    }

    private IEnumerable<bool> BuildCollision()
    {
        for (int row = 0; row < _layout.Height; row++)
        {
            int x = 0;
            while (x < _layout.Width)
            {
                var cell = _layout.GetCell(x, row);
                bool catwalk = (cell.Flags & (BoxStationCellFlags.Wall | BoxStationCellFlags.Floor)) == 0
                    && (cell.Flags & BoxStationCellFlags.Catwalk) != 0;
                int end = x + 1;
                while (end < _layout.Width)
                {
                    var next = _layout.GetCell(end, row).Flags;
                    bool nextCatwalk = (next & (BoxStationCellFlags.Wall | BoxStationCellFlags.Floor)) == 0
                        && (next & BoxStationCellFlags.Catwalk) != 0;
                    if (nextCatwalk != catwalk) break;
                    end++;
                }
                var position = _layout.CellPosition(cell.Id);
                position.x += (end - x - 1) * BoxStationLayout.TileSize * .5f;
                float width = (end - x) * BoxStationLayout.TileSize;
                if (catwalk) AddSolid(Root.transform, position - Vector3.up * .09f, new(width, .18f, 2.4f));
                x = end;
                yield return false;
            }
            _hullRows[row * 2] = BuildHullRow(row, false, _hull, true);
            yield return false;
            _hullRows[row * 2 + 1] = BuildHullRow(row, true, _hull, true);
            yield return false;
            _wallRows[row] = BuildWallRow(row, _hull, true);
            yield return false;
        }
    }

    private BoxCollider AddSolid(Transform parent, Vector3 center, Vector3 size)
        => AddSolid(parent, center, size, true, out _);

    private BoxCollider AddSolid(Transform parent, Vector3 center, Vector3 size, bool enabled, out int index)
    {
        // Keep root-local hull bounds exact when rebuilding a station far from the origin.
        var bounds = new Bounds(center, size);
        if (parent != Root.transform)
        {
            // Rest-pose horizontal bounds also cover the vertically retracting leaves.
            bounds = new Bounds(Root.transform.InverseTransformPoint(parent.TransformPoint(center)), Vector3.zero);
            for (int corner = 0; corner < 8; corner++)
            {
                var offset = new Vector3((corner & 1) == 0 ? -.5f : .5f,
                    (corner & 2) == 0 ? -.5f : .5f, (corner & 4) == 0 ? -.5f : .5f);
                bounds.Encapsulate(Root.transform.InverseTransformPoint(parent.TransformPoint(center + Vector3.Scale(size, offset))));
            }
        }
        var solid = parent.gameObject.AddComponent<BoxCollider>();
        index = -1;
        try
        {
            solid.enabled = false;
            solid.center = center;
            solid.size = size;
            solid.sharedMaterial = _contact;
            if (_freeSolids.Count > 0)
            {
                index = _freeSolids.Pop();
                _solids[index] = solid;
                _visited[index] = 0;
            }
            else
            {
                index = _solids.Count;
                _solids.Add(solid);
                _solidRegistrations.Add(default);
                if (_visited.Length < _solids.Count)
                    Array.Resize(ref _visited, Math.Max(_solids.Count, Math.Max(16, _visited.Length * 2)));
            }
            int xMin = X(bounds.min.x - .001f), xMax = X(bounds.max.x + .001f);
            int rowMin = Row(bounds.max.z + .001f), rowMax = Row(bounds.min.z - .001f);
            bool outside = xMin < 0 || xMax >= _layout.Width || rowMin < 0 || rowMax >= _layout.Height;
            _solidRegistrations[index] = new(xMin, xMax, rowMin, rowMax, outside, solid.GetInstanceID());
            if (outside) _outsideSolids.Add(index);
            for (int row = Math.Max(0, rowMin); row <= Math.Min(_layout.Height - 1, rowMax); row++)
                for (int x = Math.Max(0, xMin); x <= Math.Min(_layout.Width - 1, xMax); x++)
                    (_cellSolids[row * _layout.Width + x] ??= new List<int>(4)).Add(index);
            solid.enabled = enabled;
            return solid;
        }
        catch
        {
            if (index >= 0) QueueSolidRetirement(index);
            else UnityEngine.Object.Destroy(solid);
            throw;
        }
    }

    private void RemoveSolid(int index)
    {
        var solid = _solids[index];
        if (solid is null) return;
        if (solid) { solid.enabled = false; UnityEngine.Object.Destroy(solid); }
        var cells = _solidRegistrations[index];
        _hullSolids.Remove(cells.ColliderId);
        _wallSolids.Remove(cells.ColliderId);
        _structureSolids.Remove(cells.ColliderId);
        _constructionSolids.Remove(cells.ColliderId);
        _doorConstructionSolids.Remove(cells.ColliderId);
        if (cells.Outside) _outsideSolids.Remove(index);
        for (int row = Math.Max(0, cells.RowMin); row <= Math.Min(_layout.Height - 1, cells.RowMax); row++)
            for (int x = Math.Max(0, cells.XMin); x <= Math.Min(_layout.Width - 1, cells.XMax); x++)
                _cellSolids[row * _layout.Width + x]?.Remove(index);
        _solids[index] = null;
        _solidRegistrations[index] = default;
        _visited[index] = 0;
        _freeSolids.Push(index);
    }

    private readonly record struct SolidRegistration(int XMin, int XMax, int RowMin, int RowMax, bool Outside, int ColliderId);

    private static int X(float x) => (int)MathF.Floor(x / BoxStationLayout.TileSize + 127.5f);
    private static int Row(float z) => (int)MathF.Floor(127.5f - z / BoxStationLayout.TileSize);
}
