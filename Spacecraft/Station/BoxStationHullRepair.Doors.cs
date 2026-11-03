// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Vector3 = System.Numerics.Vector3;

namespace Modstoke.Client;

internal enum BoxStationDoorStage : byte { Empty, Frame, Anchored, Wired, Electronics, Complete }
internal enum BoxStationDoorOperation : byte
{
    None, BuildFrame, Anchor, Unanchor, Wire, Unwire, InstallElectronics, RemoveElectronics, Finish, OpenPanel, DisassembleFrame
}

internal readonly record struct BoxStationDoorAssembly([property: JsonRequired] int Edge,
    [property: JsonRequired] BoxStationDoorStage Stage);
internal readonly record struct BoxStationDoorConstructionState([property: JsonRequired] long Revision,
    [property: JsonRequired] ImmutableArray<BoxStationDoorAssembly> Assemblies);

internal sealed partial class BoxStationHullRepair
{
    private BoxStationDoorConstructionState _doorConstruction = new(0, ImmutableArray<BoxStationDoorAssembly>.Empty);
    private int _doorElectronicsBudget;

    internal BoxStationDoorConstructionState DoorConstruction => _doorConstruction;

    internal BoxStationDoorAssembly DoorAt(int target) => DoorAt(_layout, _doorConstruction, target);

    internal static BoxStationDoorAssembly DoorAt(BoxStationLayout layout, BoxStationDoorConstructionState state, int target)
    {
        int edge = BoxStationDoors.EdgeFromTarget(layout, target);
        if (!BoxStationDoors.ValidEdge(layout, edge)) return new(-1, BoxStationDoorStage.Empty);
        int index = FindDoorAssembly(state.Assemblies, edge);
        return index >= 0 ? state.Assemblies[index] : new(edge, BoxStationDoorStage.Empty);
    }

    internal static bool ValidDoorConstruction(BoxStationLayout layout, BoxStationDoorConstructionState state)
    {
        if (state.Revision < 0 || state.Assemblies.IsDefault || state.Assemblies.Length > BoxStationDoors.MaximumAssemblies
            || state.Revision == 0 && !state.Assemblies.IsEmpty) return false;
        int previous = -1;
        foreach (var assembly in state.Assemblies)
        {
            if (assembly.Edge <= previous || assembly.Stage is < BoxStationDoorStage.Frame or > BoxStationDoorStage.Complete
                || !BoxStationDoors.ValidEdge(layout, assembly.Edge)) return false;
            previous = assembly.Edge;
        }
        return true;
    }

    internal static bool DoorOccupiesCell(BoxStationLayout layout, BoxStationDoorConstructionState state, int cell)
    {
        if (cell < 0 || cell >= layout.Cells.Length || state.Assemblies.IsDefault) return false;
        foreach (var assembly in state.Assemblies)
            if (BoxStationDoors.TryCells(layout, assembly.Edge, out int first, out int second)
                && (cell == first || cell == second)) return true;
        return false;
    }

    internal bool ResolveDoorMaterial(Guid holder, int target, out BoxStationDoorOperation operation, out int cost, out float seconds)
    {
        operation = BoxStationDoorOperation.None;
        cost = 0;
        seconds = 0;
        int slot = HeldMaterialSlot(holder);
        if (!_materialsInitialized || slot < 0 || _doorConstruction.Revision == long.MaxValue) return false;
        var assembly = DoorAt(target);
        return DoorSiteClear(assembly.Edge)
            && (assembly.Stage != BoxStationDoorStage.Empty || _doorConstruction.Assemblies.Length < BoxStationDoors.MaximumAssemblies)
            && ResolveDoorMaterial(_layout, assembly, _materialBundles[slot].Material, out operation, out cost, out seconds)
            && _materialBundles[slot].Count >= cost;
    }

    internal static bool ResolveDoorMaterial(BoxStationLayout layout, BoxStationDoorAssembly assembly, BoxStationMaterial material,
        out BoxStationDoorOperation operation, out int cost, out float seconds)
    {
        operation = BoxStationDoorOperation.None;
        cost = 0;
        seconds = 0;
        if (!BoxStationDoors.ValidEdge(layout, assembly.Edge)) return false;
        if (assembly.Stage == BoxStationDoorStage.Empty && material == BoxStationMaterial.Steel)
        { operation = BoxStationDoorOperation.BuildFrame; cost = 4; }
        else if (assembly.Stage == BoxStationDoorStage.Anchored && material == BoxStationMaterial.Cable)
        { operation = BoxStationDoorOperation.Wire; cost = 1; }
        else if (assembly.Stage == BoxStationDoorStage.Wired && material == BoxStationMaterial.DoorElectronics)
        { operation = BoxStationDoorOperation.InstallElectronics; cost = 1; }
        if (operation == BoxStationDoorOperation.None) return false;
        seconds = 4;
        return true;
    }

    internal static bool ResolveDoorTool(BoxStationLayout layout, BoxStationDoorAssembly assembly, BoxStationToolKind tool,
        BoxStationWorkMode mode, out BoxStationDoorOperation operation, out float seconds)
    {
        operation = BoxStationDoorOperation.None;
        seconds = 0;
        if (!BoxStationDoors.ValidEdge(layout, assembly.Edge) || mode > BoxStationWorkMode.Dismantle) return false;
        if (mode == BoxStationWorkMode.Repair)
        {
            if (assembly.Stage == BoxStationDoorStage.Frame && tool == BoxStationToolKind.Wrench)
                operation = BoxStationDoorOperation.Anchor;
            else if (assembly.Stage == BoxStationDoorStage.Electronics && tool == BoxStationToolKind.Screwdriver)
                operation = BoxStationDoorOperation.Finish;
        }
        else
        {
            if (assembly.Stage == BoxStationDoorStage.Complete && tool == BoxStationToolKind.Screwdriver)
                operation = BoxStationDoorOperation.OpenPanel;
            else if (assembly.Stage == BoxStationDoorStage.Electronics && tool == BoxStationToolKind.Crowbar)
                operation = BoxStationDoorOperation.RemoveElectronics;
            else if (assembly.Stage == BoxStationDoorStage.Wired && tool == BoxStationToolKind.Wirecutters)
                operation = BoxStationDoorOperation.Unwire;
            else if (assembly.Stage == BoxStationDoorStage.Anchored && tool == BoxStationToolKind.Wrench)
                operation = BoxStationDoorOperation.Unanchor;
            else if (assembly.Stage == BoxStationDoorStage.Frame && tool == BoxStationToolKind.Welder)
                operation = BoxStationDoorOperation.DisassembleFrame;
        }
        if (operation == BoxStationDoorOperation.None) return false;
        seconds = 4;
        return true;
    }

    internal bool CommitDoorConstruction(Guid holder, int target, BoxStationDoorAssembly expected, BoxStationMaterialBundle material,
        BoxStationDoorOperation operation, Func<BoxStationDoorConstructionState, bool> commitDoors)
    {
        if (!_materialsInitialized || holder == Guid.Empty || _doorConstruction.Revision == long.MaxValue) return false;
        var current = DoorAt(target);
        if (current != expected || !DoorSiteClear(current.Edge)) return false;
        int index = FindDoorAssembly(_doorConstruction.Assemblies, current.Edge);
        bool consumes = operation is BoxStationDoorOperation.BuildFrame or BoxStationDoorOperation.Wire or BoxStationDoorOperation.InstallElectronics;
        int materialSlot = -1;
        var remaining = default(BoxStationMaterialBundle);
        if (consumes)
        {
            materialSlot = HeldMaterialSlot(holder);
            if (materialSlot < 0 || _materialBundles[materialSlot] != material
                || !ResolveDoorMaterial(_layout, current, material.Material, out var resolved, out int cost, out _)
                || resolved != operation || material.Count < cost) return false;
            remaining = material.Count == cost ? default : material with { Count = (ushort)(material.Count - cost) };
        }
        else if (material != default || !DoorToolOperation(_layout, current, operation)) return false;

        BoxStationDoorStage stage;
        int refund = 0;
        var refundMaterial = default(BoxStationMaterial);
        switch (operation)
        {
            case BoxStationDoorOperation.BuildFrame: stage = BoxStationDoorStage.Frame; break;
            case BoxStationDoorOperation.Anchor: stage = BoxStationDoorStage.Anchored; break;
            case BoxStationDoorOperation.Unanchor: stage = BoxStationDoorStage.Frame; break;
            case BoxStationDoorOperation.Wire: stage = BoxStationDoorStage.Wired; break;
            case BoxStationDoorOperation.Unwire:
                stage = BoxStationDoorStage.Anchored; refund = 1; refundMaterial = BoxStationMaterial.Cable; break;
            case BoxStationDoorOperation.InstallElectronics: stage = BoxStationDoorStage.Electronics; break;
            case BoxStationDoorOperation.RemoveElectronics:
                stage = BoxStationDoorStage.Wired; refund = 1; refundMaterial = BoxStationMaterial.DoorElectronics; break;
            case BoxStationDoorOperation.Finish: stage = BoxStationDoorStage.Complete; break;
            case BoxStationDoorOperation.OpenPanel: stage = BoxStationDoorStage.Electronics; break;
            case BoxStationDoorOperation.DisassembleFrame:
                stage = BoxStationDoorStage.Empty; refund = 4; refundMaterial = BoxStationMaterial.Steel; break;
            default: return false;
        }
        if (index < 0 && _doorConstruction.Assemblies.Length >= BoxStationDoors.MaximumAssemblies) return false;
        var assembly = current with { Stage = stage };
        var rows = stage == BoxStationDoorStage.Empty ? _doorConstruction.Assemblies.RemoveAt(index)
            : index < 0 ? _doorConstruction.Assemblies.Insert(~index, assembly) : _doorConstruction.Assemblies.SetItem(index, assembly);
        var next = new BoxStationDoorConstructionState(_doorConstruction.Revision + 1, rows);
        int salvageSlot = -1;
        var salvage = default(BoxStationMaterialBundle);
        bool allocate = false;
        if (refund != 0)
        {
            BoxStationDoors.TryCells(_layout, current.Edge, out int first, out _);
            var cell = _layout.Cells[first];
            var drop = new Vector3((cell.X - _layout.Width / 2) * BoxStationLayout.TileSize, 0,
                (_layout.Height / 2 - cell.Row) * BoxStationLayout.TileSize);
            if (!ValidDropPosition(_layout, drop)
                || !TryPrepareMaterialSalvage(refundMaterial, refund, drop, out salvageSlot, out salvage, out allocate, out _)) return false;
        }
        // Topology may reject a closed or cycling door. Custody and refunds remain untouched until it succeeds.
        if (!commitDoors(next)) return false;
        if (consumes) _materialBundles[materialSlot] = remaining;
        if (refund != 0)
        {
            _materialBundles[salvageSlot] = salvage;
            if (allocate) _nextMaterialBundleId++;
        }
        _doorConstruction = next;
        _materialsDirty = true;
        return true;
    }

    private bool DoorSiteClear(int edge)
    {
        if (!BoxStationDoors.ValidEdge(_layout, edge) || !BoxStationDoors.TryCells(_layout, edge, out int first, out int second)
            || !IntactFloorDeck(first) || !IntactFloorDeck(second)) return false;
        return DoorCellClear(first) && DoorCellClear(second);
    }

    private bool DoorCellClear(int cell)
    {
        int index = _constructionDefinitions.Indices[cell];
        return (_hull.EffectiveFlags(cell) & BoxStationCellFlags.Wall) == 0
            && (index < 0 || _construction[index].Stage == BoxStationFrameStage.Empty);
    }

    private static bool DoorToolOperation(BoxStationLayout layout, BoxStationDoorAssembly assembly, BoxStationDoorOperation operation)
    {
        var mode = operation is BoxStationDoorOperation.Anchor or BoxStationDoorOperation.Finish
            ? BoxStationWorkMode.Repair : BoxStationWorkMode.Dismantle;
        var tool = operation switch
        {
            BoxStationDoorOperation.Anchor or BoxStationDoorOperation.Unanchor => BoxStationToolKind.Wrench,
            BoxStationDoorOperation.Unwire => BoxStationToolKind.Wirecutters,
            BoxStationDoorOperation.RemoveElectronics => BoxStationToolKind.Crowbar,
            BoxStationDoorOperation.Finish or BoxStationDoorOperation.OpenPanel => BoxStationToolKind.Screwdriver,
            _ => BoxStationToolKind.Welder
        };
        return ResolveDoorTool(layout, assembly, tool, mode, out var resolved, out _) && resolved == operation;
    }

    private static int FindDoorAssembly(ImmutableArray<BoxStationDoorAssembly> assemblies, int edge)
    {
        int low = 0, high = assemblies.IsDefault ? -1 : assemblies.Length - 1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            if (assemblies[middle].Edge == edge) return middle;
            if (assemblies[middle].Edge < edge) low = middle + 1;
            else high = middle - 1;
        }
        return ~low;
    }

    private static bool ValidDoorConstructionFootprints(BoxStationLayout layout, BoxStationMaterialState state)
    {
        foreach (var assembly in state.DoorConstruction.Assemblies)
        {
            if (!BoxStationDoors.TryCells(layout, assembly.Edge, out int first, out int second)
                || ConstructionAt(layout, state, layout.Cells.Length * 2 + first).Stage != BoxStationFrameStage.Empty
                || ConstructionAt(layout, state, layout.Cells.Length * 2 + second).Stage != BoxStationFrameStage.Empty) return false;
        }
        return true;
    }
}
