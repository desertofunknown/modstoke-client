// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class BoxStationModel
{
    private readonly Dictionary<int, WallSolid> _wallSolids = new();
    private readonly List<int>?[] _wallRows;

    internal bool TryWallImpact(Collider collider, Vector3 worldPoint, out int target)
    {
        target = -1;
        if (!Visible || !collider || !collider.enabled || !collider.gameObject.activeInHierarchy || !Finite(worldPoint)
            || !_wallSolids.TryGetValue(collider.GetInstanceID(), out var span)) return false;
        var solid = _solids[span.Index];
        if (solid == null || !solid || solid != collider) return false;
        var local = Root.transform.InverseTransformPoint(worldPoint);
        if (!Finite(local)) return false;
        var offset = local - solid.center;
        var half = solid.size * .5f;
        const float tolerance = .015f;
        if (MathF.Abs(offset.x) > half.x + tolerance || MathF.Abs(offset.y) > half.y + tolerance
            || MathF.Abs(offset.z) > half.z + tolerance) return false;
        // Keep boundary contacts on this collider's current run, including its closed end faces.
        int x = Math.Clamp(X(local.x), span.X, span.End - 1);
        int cell = span.Row * _layout.Width + x;
        if (_hull.WallBreached(cell)) return false;
        target = BoxStationHull.WallTarget(_layout, cell);
        return target >= 0;
    }

    private static BoxStationCellFlags EffectiveFlags(BoxStationCell cell, BoxStationHull hull)
        => hull.EffectiveFlags(cell.Id);

    private List<int> BuildWallRow(int row, BoxStationHull hull, bool enabled)
    {
        var solids = new List<int>();
        try
        {
            int x = 0;
            float tile = BoxStationLayout.TileSize, height = BoxStationLayout.DeckHeight;
            while (x < _layout.Width)
            {
                var cell = _layout.GetCell(x, row);
                if ((EffectiveFlags(cell, hull) & BoxStationCellFlags.Wall) == 0) { x++; continue; }
                int end = x + 1;
                while (end < _layout.Width
                    && (EffectiveFlags(_layout.GetCell(end, row), hull) & BoxStationCellFlags.Wall) != 0) end++;
                var position = _layout.CellPosition(cell.Id);
                position.x += (end - x - 1) * tile * .5f;
                var solid = AddSolid(Root.transform, position + Vector3.up * (height * .5f),
                    new((end - x) * tile, height, tile), enabled, out int index);
                solids.Add(index);
                _wallSolids.Add(solid.GetInstanceID(), new(index, row, x, end));
                x = end;
            }
            for (x = 0; x < _layout.Width; x++)
            {
                var cell = _layout.GetCell(x, row);
                var flags = EffectiveFlags(cell, hull);
                if ((flags & BoxStationCellFlags.Wall) != 0) continue;
                AddStructureSolids(cell.Id, hull, enabled, solids);
            }
            return solids;
        }
        catch { ReleaseSolidRow(solids); throw; }
    }

    private readonly record struct WallSolid(int Index, int Row, int X, int End);
}
