// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class BoxStationModel
{
    private readonly Dictionary<int, StructureSolid> _structureSolids = new();

    internal bool IsMaterialRebuildCollider(Collider collider, int target)
    {
        if (!collider || !_structureSolids.TryGetValue(collider.GetInstanceID(), out var component)
            || !BoxStationHull.TryStructure(_layout, target, out var replacement)
            || !BoxStationHull.TryStructure(_layout, component.Target, out var existing)) return false;
        // A source window's grille occupies its authored frame. Other source
        // contributors, machinery and crew still have to clear the replacement.
        return component.Target == target || existing.SourceObject == replacement.SourceObject;
    }

    internal bool TryStructureImpact(Collider collider, Vector3 worldPoint, out int target)
    {
        target = -1;
        if (!Visible || !collider || !collider.enabled || !collider.gameObject.activeInHierarchy || !Finite(worldPoint)
            || !_structureSolids.TryGetValue(collider.GetInstanceID(), out var component)) return false;
        var solid = _solids[component.Index];
        if (solid == null || !solid || solid != collider
            || !BoxStationHull.TryStructure(_layout, component.Target, out var structure)
            || !_hull.StructureSolid(structure)) return false;
        var local = Root.transform.InverseTransformPoint(worldPoint);
        if (!Finite(local)) return false;
        var offset = local - solid.center;
        var half = solid.size * .5f;
        const float tolerance = .015f;
        if (MathF.Abs(offset.x) > half.x + tolerance || MathF.Abs(offset.y) > half.y + tolerance
            || MathF.Abs(offset.z) > half.z + tolerance) return false;
        target = component.Target;
        return true;
    }

    private void AddStructures(int cell, BoxStationHull hull, string suffix, MeshBuilder opaque,
        MeshBuilder panes, MeshBuilder tinted, MeshBuilder plasma)
    {
        var position = _layout.CellPosition(cell);
        foreach (var structure in BoxStationHull.StructuresAt(_layout, cell))
        {
            byte integrity = hull.StructureIntegrity(structure.Id);
            float inset = StructureInset(structure);
            if (structure.Kind == BoxStationStructureKind.Window)
            {
                // Recoverable debris is owned by the material inventory projection.
                if (integrity == 0) continue;
                var glazing = structure.Tinted ? tinted : structure.Plasma ? plasma : panes;
                bool full = structure.Direction == 5;
                if (full)
                {
                    float scale = 1 - inset * 2 / BoxStationLayout.TileSize;
                    opaque.Add(_parts["window-full" + suffix], position, Quaternion.identity, new(scale, 1, scale));
                }
                else
                {
                    int direction = PortDirection(structure.Direction);
                    opaque.Add(_parts["window-edge" + suffix], WindowEdge(structure, direction, inset), Turn(direction));
                }
                int first = full ? 0 : PortDirection(structure.Direction), end = full ? 4 : first + 1;
                for (int direction = first; direction < end; direction++)
                {
                    var edge = WindowEdge(structure, direction, inset);
                    glazing.Pane(edge + Vector3.up * 1.6f, Turn(direction), 2.12f - (full ? inset * 2 : 0), 2.64f);
                }
            }
            else if (integrity > 0)
            {
                bool broken = integrity <= BoxStationHull.BrokenGrilleIntegrity;
                // Authored broken exterior fixtures stay anchored; newly collapsed debris needs deck support.
                if (broken && (hull.Breached(cell, false)
                    || structure.InitialIntegrity > BoxStationHull.BrokenGrilleIntegrity
                        && (hull.EffectiveFlags(cell) & (BoxStationCellFlags.Floor | BoxStationCellFlags.Catwalk)) == 0)) continue;
                string part = broken ? "grille-broken" : "grille-edge";
                bool centered = CenteredGrille(structure);
                for (int plane = 0; plane < (centered ? 1 : 4); plane++)
                {
                    int direction = centered ? WindowDirection(cell) : plane;
                    opaque.Add(_parts[part + suffix], GrillePlane(structure, direction, centered, inset), Turn(direction));
                }
            }
        }
    }

    private void AddStructureSolids(int cell, BoxStationHull hull, bool enabled, List<int> solids)
    {
        float tile = BoxStationLayout.TileSize, height = BoxStationLayout.DeckHeight;
        foreach (var structure in BoxStationHull.StructuresAt(_layout, cell))
        {
            if (!hull.StructureSolid(structure)) continue;
            float inset = StructureInset(structure);
            if (structure.Kind == BoxStationStructureKind.Window && structure.Direction == 5)
            {
                AddStructureSolid(structure, _layout.CellPosition(cell) + Vector3.up * (height * .5f),
                    new(tile - inset * 2, height, tile - inset * 2), enabled, solids);
                continue;
            }
            bool grille = structure.Kind == BoxStationStructureKind.Grille;
            bool centered = grille && CenteredGrille(structure);
            int first = grille ? 0 : PortDirection(structure.Direction), end = grille && !centered ? 4 : first + 1;
            for (int plane = first; plane < end; plane++)
            {
                int direction = centered ? WindowDirection(cell) : plane;
                var position = grille ? GrillePlane(structure, direction, centered, inset) : WindowEdge(structure, direction, inset);
                AddStructureSolid(structure, position + Vector3.up * (height * .5f),
                    direction % 2 == 0 ? new(tile, height, .1f) : new(.1f, height, tile), enabled, solids);
            }
        }
    }

    private void AddStructureSolid(BoxStationStructure structure, Vector3 center, Vector3 size, bool enabled, List<int> solids)
    {
        var solid = AddSolid(Root.transform, center, size, enabled, out int index);
        solids.Add(index);
        _structureSolids.Add(solid.GetInstanceID(), new(index, BoxStationHull.StructureTarget(_layout, structure.Id)));
    }

    private Vector3 WindowEdge(BoxStationStructure structure, int direction, float inset)
        => _layout.CellPosition(structure.Cell) + Turn(direction)
            * new Vector3(0, 0, (structure.Direction == 5 ? 1.15f : BoxStationLayout.TileSize * .5f - .02f) - inset);

    private Vector3 GrillePlane(BoxStationStructure structure, int direction, bool centered, float inset)
        => _layout.CellPosition(structure.Cell) + Turn(direction) * new Vector3(0, 0, centered ? -inset : 1.15f - inset);

    private bool CenteredGrille(BoxStationStructure structure)
        => BoxStationHull.TryStructure(_layout, BoxStationHull.StructureTarget(_layout, structure.SourceObject * 2), out var window)
            && window.Kind == BoxStationStructureKind.Window && window.Direction == 5;

    private float StructureInset(BoxStationStructure structure)
    {
        int layer = 0;
        foreach (var other in BoxStationHull.StructuresAt(_layout, structure.Cell))
        {
            if (other.Id == structure.Id) break;
            if (other.Kind == structure.Kind && other.Direction == structure.Direction) layer++;
        }
        // Preserve separate source contributors without coplanar panes on opposite tile edges.
        return layer * .02f;
    }

    private static int StructureStage(BoxStationStructure structure, byte integrity)
        => integrity == 0 ? 0 : structure.Kind == BoxStationStructureKind.Grille
            && integrity <= BoxStationHull.BrokenGrilleIntegrity ? 1 : 2;

    private void AddStructureChanges(int cell, HashSet<int> rows, HashSet<int> chunks)
    {
        // An edge contributor can affect the neighboring row or chunk as well as its source cell.
        for (int direction = -1; direction < 4; direction++)
        {
            int affected = direction < 0 ? cell : Neighbor(cell, direction);
            if (affected < 0) continue;
            int row = affected / _layout.Width, x = affected % _layout.Width;
            rows.Add(row);
            chunks.Add(row / ChunkSize * ChunkSize * _layout.Width + x / ChunkSize * ChunkSize);
        }
    }

    private readonly record struct StructureSolid(int Index, int Target);
}
