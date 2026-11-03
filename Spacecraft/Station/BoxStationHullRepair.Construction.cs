// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Text.Json.Serialization;
using Vector3 = System.Numerics.Vector3;

namespace Modstoke.Client;

internal enum BoxStationFrameStage : byte { Empty, Girder, DisplacedGirder, ReinforcedStruts, ReinforcedGirder, Wall }
internal enum BoxStationConstructionOperation : byte
{
    None, BuildGirder, PlateWall, ReinforceGirder, PlateReinforcedWall, DismantleWall, AdvanceWall,
    ReverseWall, ExposeStruts, SecureStruts, RemoveStruts, DisplaceGirder, AnchorGirder, RemoveGirder, RepairFrame
}

internal readonly record struct BoxStationConstructionBudget([property: JsonRequired] int Steel, [property: JsonRequired] int Plasteel);
internal readonly record struct BoxStationConstructionDefinition(int Cell, int Target, bool OriginalWall, bool Reinforced,
    Vector3 Position, bool OriginalGirder);
internal readonly record struct BoxStationConstruction([property: JsonRequired] int Cell,
    [property: JsonRequired] BoxStationFrameStage Stage, [property: JsonRequired] byte WorkStage,
    [property: JsonRequired] byte Integrity, [property: JsonRequired] ushort SteelYield, [property: JsonRequired] ushort PlasteelYield,
    BoxStationWallKind WallKind = BoxStationWallKind.Unspecified);

internal sealed partial class BoxStationHullRepair
{
    private ConstructionDefinitions _constructionDefinitions = null!;
    private BoxStationConstruction[] _construction = null!;
    private BoxStationConstructionBudget _constructionBudget, _constructionWaste;
    private int _importedSteel;
    private long _constructionHullRevision = -1;

    private void InitializeConstruction()
    {
        _constructionDefinitions = LayoutConstruction.GetValue(_layout, BuildConstructionDefinitions);
        _construction = new BoxStationConstruction[_constructionDefinitions.Sources.Length];
        for (int i = 0; i < _construction.Length; i++)
            _construction[i] = InitialConstruction(_constructionDefinitions.Sources[i]);
    }

    private void SeedConstruction()
    {
        var hull = _hull.State;
        int panel = 0;
        for (int i = 0; i < _construction.Length; i++)
            _construction[i] = SurvivingConstruction(_constructionDefinitions.Sources[i], hull, ref panel);
        _constructionBudget = InstalledConstruction(_construction);
        for (int i = 0; i < _materialStocks.Length; i++)
            if (_materialDefinitions.Sources[i].Material == BoxStationMaterial.Plasteel)
                _constructionBudget = AddConstructionUnits(_constructionBudget, new(0, _materialStocks[i]));
        _constructionHullRevision = hull.Revision;
    }

    private void SynchronizeConstruction()
    {
        var hull = _hull.State;
        if (_constructionHullRevision == hull.Revision) return;
        int panel = 0;
        foreach (var definition in _constructionDefinitions.Sources)
        {
            int index = _constructionDefinitions.Indices[definition.Cell];
            var value = _construction[index];
            if (value.Stage != BoxStationFrameStage.Wall || BoxStationHull.WallTarget(_layout, definition.Cell) < 0) continue;
            while (panel < hull.Panels.Length && hull.Panels[panel].Target < definition.Target) panel++;
            byte integrity = panel < hull.Panels.Length && hull.Panels[panel].Target == definition.Target
                ? hull.Panels[panel].Integrity : BoxStationHull.InitialIntegrity(_layout, definition.Target);
            if (value.Integrity != integrity)
            {
                _construction[index] = value with { Integrity = integrity };
                _materialsDirty = true;
            }
        }
        _constructionHullRevision = hull.Revision;
    }

    internal BoxStationConstruction ConstructionAt(int target)
    {
        if (!TryConstruction(_layout, target, out var definition)) return default;
        SynchronizeConstruction();
        return _construction[_constructionDefinitions.Indices[definition.Cell]];
    }

    internal bool ResolveConstruction(int target, BoxStationToolKind tool, BoxStationWorkMode mode,
        out BoxStationConstructionOperation operation, out float seconds)
    {
        operation = BoxStationConstructionOperation.None;
        seconds = 0;
        return _materialsInitialized && TryConstruction(_layout, target, out var definition)
            && !DoorOccupiesCell(_layout, _doorConstruction, definition.Cell)
            && ResolveConstruction(_layout, ConstructionAt(target), tool, mode, out operation, out seconds);
    }

    internal static bool ResolveConstruction(BoxStationLayout layout, BoxStationConstruction construction,
        BoxStationToolKind tool, BoxStationWorkMode mode, out BoxStationConstructionOperation operation, out float seconds)
    {
        operation = BoxStationConstructionOperation.None;
        seconds = 0;
        if (mode > BoxStationWorkMode.Dismantle || tool > BoxStationToolKind.Welder
            || !TryConstruction(layout, layout.Cells.Length * 2 + construction.Cell, out var definition)
            || !ValidConstruction(layout, definition, construction) || construction.Stage == BoxStationFrameStage.Empty) return false;
        if (construction.Stage == BoxStationFrameStage.Wall)
        {
            if (construction.WallKind == BoxStationWallKind.Steel)
            {
                if (tool != BoxStationToolKind.Welder || mode != BoxStationWorkMode.Dismantle) return false;
                operation = BoxStationConstructionOperation.DismantleWall;
                seconds = 10;
                return true;
            }
            byte stage = construction.WorkStage;
            var required = WallConstructionTool(stage, mode);
            if (tool != required || mode == BoxStationWorkMode.Repair && stage == 0) return false;
            if (mode == BoxStationWorkMode.Dismantle)
            {
                seconds = stage switch { 0 => 0, 1 or 4 => 4, 2 => 6, _ => 10 };
                operation = stage == 6 ? BoxStationConstructionOperation.DismantleWall : BoxStationConstructionOperation.AdvanceWall;
            }
            else
            {
                seconds = stage switch { 1 => 0, 2 or 5 => 4, 3 => 6, 4 => 2, _ => 10 };
                operation = BoxStationConstructionOperation.ReverseWall;
            }
            return true;
        }
        seconds = 4;
        if (tool == BoxStationToolKind.Welder && mode == BoxStationWorkMode.Repair && construction.Integrity < BoxStationHull.Capacity)
            operation = BoxStationConstructionOperation.RepairFrame;
        else if (construction.Stage == BoxStationFrameStage.Girder && tool == BoxStationToolKind.Wrench)
            operation = BoxStationConstructionOperation.DisplaceGirder;
        else if (construction.Stage == BoxStationFrameStage.DisplacedGirder && tool == BoxStationToolKind.Wrench)
            operation = BoxStationConstructionOperation.AnchorGirder;
        else if (construction.Stage == BoxStationFrameStage.DisplacedGirder && tool == BoxStationToolKind.Screwdriver)
            operation = BoxStationConstructionOperation.RemoveGirder;
        else if (construction.Stage == BoxStationFrameStage.ReinforcedGirder && tool == BoxStationToolKind.Screwdriver)
            operation = BoxStationConstructionOperation.ExposeStruts;
        else if (construction.Stage == BoxStationFrameStage.ReinforcedStruts && tool == BoxStationToolKind.Screwdriver)
            operation = BoxStationConstructionOperation.SecureStruts;
        else if (construction.Stage == BoxStationFrameStage.ReinforcedStruts && tool == BoxStationToolKind.Wirecutters)
            operation = BoxStationConstructionOperation.RemoveStruts;
        if (operation != BoxStationConstructionOperation.None) return true;
        seconds = 0;
        return false;
    }

    internal bool ResolveConstructionMaterial(Guid holder, int target, out BoxStationConstructionOperation operation, out int cost, out float seconds)
    {
        operation = BoxStationConstructionOperation.None;
        cost = 0;
        seconds = 0;
        int slot = HeldMaterialSlot(holder);
        return _materialsInitialized && slot >= 0 && TryConstruction(_layout, target, out var definition)
            && !DoorOccupiesCell(_layout, _doorConstruction, definition.Cell)
            && (definition.OriginalWall || BoxStationHull.WallTarget(_layout, definition.Cell) < 0 || IntactFloorDeck(definition.Cell))
            && ResolveConstructionMaterial(_layout, ConstructionAt(target), _materialBundles[slot].Material, out operation, out cost, out seconds)
            && _materialBundles[slot].Count >= cost;
    }

    internal static bool ResolveConstructionMaterial(BoxStationLayout layout, BoxStationConstruction construction, BoxStationMaterial material,
        out BoxStationConstructionOperation operation, out int cost, out float seconds)
    {
        operation = BoxStationConstructionOperation.None;
        cost = 0;
        seconds = 0;
        if (!TryConstruction(layout, layout.Cells.Length * 2 + construction.Cell, out var definition)
            || !ValidConstruction(layout, definition, construction)) return false;
        if (construction.Stage == BoxStationFrameStage.Empty && material == BoxStationMaterial.Steel)
        { operation = BoxStationConstructionOperation.BuildGirder; cost = 2; seconds = 4; }
        else if (construction.Stage == BoxStationFrameStage.Girder)
        {
            if (!definition.Reinforced && BoxStationHull.WallTarget(layout, definition.Cell) >= 0
                && material == BoxStationMaterial.Steel)
            { operation = BoxStationConstructionOperation.PlateWall; cost = 2; seconds = 4; }
            else if ((!definition.OriginalWall || definition.Reinforced) && material == BoxStationMaterial.Plasteel)
            { operation = BoxStationConstructionOperation.ReinforceGirder; cost = 1; seconds = 6; }
        }
        else if (construction.Stage == BoxStationFrameStage.ReinforcedGirder && (!definition.OriginalWall || definition.Reinforced)
            && BoxStationHull.WallTarget(layout, definition.Cell) >= 0 && material == BoxStationMaterial.Plasteel)
        { operation = BoxStationConstructionOperation.PlateReinforcedWall; cost = 1; seconds = 5; }
        return operation != BoxStationConstructionOperation.None;
    }

    internal bool CommitConstruction(Guid holder, int target, BoxStationConstruction expected, BoxStationMaterialBundle material,
        BoxStationConstructionOperation operation, Func<int, byte, BoxStationWallKind, bool> restoreWall)
    {
        if (!_materialsInitialized || holder == Guid.Empty || !TryConstruction(_layout, target, out var definition)
            || DoorOccupiesCell(_layout, _doorConstruction, definition.Cell)) return false;
        var current = ConstructionAt(target);
        if (current.Cell != expected.Cell || current.Stage != expected.Stage || current.WorkStage != expected.WorkStage
            || current.Integrity != expected.Integrity || current.WallKind != expected.WallKind
            || !ValidConstruction(_layout, definition, current)) return false;
        bool consumes = operation is BoxStationConstructionOperation.BuildGirder or BoxStationConstructionOperation.PlateWall
            or BoxStationConstructionOperation.ReinforceGirder or BoxStationConstructionOperation.PlateReinforcedWall;
        int slot = -1, cost = 0;
        var updatedMaterial = default(BoxStationMaterialBundle);
        if (consumes)
        {
            if (!definition.OriginalWall && BoxStationHull.WallTarget(_layout, definition.Cell) >= 0
                && !IntactFloorDeck(definition.Cell)) return false;
            slot = HeldMaterialSlot(holder);
            if (slot < 0 || _materialBundles[slot] != material
                || !ResolveConstructionMaterial(_layout, current, material.Material, out var resolved, out cost, out _)
                || resolved != operation || material.Count < cost) return false;
            updatedMaterial = material.Count == cost ? default : material with { Count = (ushort)(material.Count - cost) };
        }
        else if (!ConstructionToolOperation(_layout, current, operation)) return false;
        var next = current;
        int steel = 0, plasteel = 0;
        switch (operation)
        {
            case BoxStationConstructionOperation.BuildGirder:
                next = current with { Stage = BoxStationFrameStage.Girder, Integrity = BoxStationHull.Capacity }; break;
            case BoxStationConstructionOperation.PlateWall:
            case BoxStationConstructionOperation.PlateReinforcedWall:
                next = current with { Stage = BoxStationFrameStage.Wall, Integrity = BoxStationHull.Capacity,
                    WallKind = operation == BoxStationConstructionOperation.PlateWall ? BoxStationWallKind.Steel : BoxStationWallKind.Reinforced }; break;
            case BoxStationConstructionOperation.ReinforceGirder:
            case BoxStationConstructionOperation.SecureStruts:
                next = current with { Stage = BoxStationFrameStage.ReinforcedGirder }; break;
            case BoxStationConstructionOperation.ExposeStruts:
                next = current with { Stage = BoxStationFrameStage.ReinforcedStruts }; break;
            case BoxStationConstructionOperation.RemoveStruts:
                next = current with { Stage = BoxStationFrameStage.Girder }; plasteel = 1; break;
            case BoxStationConstructionOperation.DismantleWall:
                next = current with { Stage = current.WallKind == BoxStationWallKind.Reinforced ? BoxStationFrameStage.ReinforcedGirder : BoxStationFrameStage.Girder,
                    WorkStage = 0, Integrity = BoxStationHull.Capacity, WallKind = BoxStationWallKind.None };
                if (current.WallKind == BoxStationWallKind.Reinforced) plasteel = 1; else steel = 2;
                break;
            case BoxStationConstructionOperation.AdvanceWall:
                next = current with { WorkStage = (byte)(current.WorkStage + 1) }; break;
            case BoxStationConstructionOperation.ReverseWall:
                next = current with { WorkStage = (byte)(current.WorkStage - 1) }; break;
            case BoxStationConstructionOperation.DisplaceGirder:
                next = current with { Stage = BoxStationFrameStage.DisplacedGirder }; break;
            case BoxStationConstructionOperation.AnchorGirder:
                next = current with { Stage = BoxStationFrameStage.Girder }; break;
            case BoxStationConstructionOperation.RemoveGirder:
                next = current with { Stage = BoxStationFrameStage.Empty, Integrity = 0 }; steel = 2; break;
            case BoxStationConstructionOperation.RepairFrame:
                next = current with { Integrity = BoxStationHull.Capacity }; break;
            default: return false;
        }
        if (current.SteelYield + steel > ushort.MaxValue || current.PlasteelYield + plasteel > ushort.MaxValue) return false;
        next = next with { SteelYield = (ushort)(current.SteelYield + steel), PlasteelYield = (ushort)(current.PlasteelYield + plasteel) };
        // No allocation, custody change or salvage debit follows the gas/hull commit except prepared value assignments.
        if (operation == BoxStationConstructionOperation.DismantleWall && !_hull.RemoveWall(target, current.Integrity)) return false;
        if (operation is BoxStationConstructionOperation.PlateWall or BoxStationConstructionOperation.PlateReinforcedWall
            && !restoreWall(target, 0, next.WallKind)) return false;
        if (consumes) _materialBundles[slot] = updatedMaterial;
        _construction[_constructionDefinitions.Indices[definition.Cell]] = next;
        _materialsDirty = true;
        return true;
    }

    private static bool ConstructionToolOperation(BoxStationLayout layout, BoxStationConstruction current, BoxStationConstructionOperation operation)
    {
        var mode = operation is BoxStationConstructionOperation.ReverseWall or BoxStationConstructionOperation.RepairFrame
            ? BoxStationWorkMode.Repair : BoxStationWorkMode.Dismantle;
        var tool = operation switch
        {
            BoxStationConstructionOperation.AdvanceWall or BoxStationConstructionOperation.ReverseWall => WallConstructionTool(current.WorkStage, mode),
            BoxStationConstructionOperation.ExposeStruts or BoxStationConstructionOperation.SecureStruts
                or BoxStationConstructionOperation.RemoveGirder => BoxStationToolKind.Screwdriver,
            BoxStationConstructionOperation.RemoveStruts => BoxStationToolKind.Wirecutters,
            BoxStationConstructionOperation.DisplaceGirder or BoxStationConstructionOperation.AnchorGirder => BoxStationToolKind.Wrench,
            BoxStationConstructionOperation.DismantleWall => current.WorkStage == 6 ? BoxStationToolKind.Crowbar : BoxStationToolKind.Welder,
            _ => BoxStationToolKind.Welder
        };
        return ResolveConstruction(layout, current, tool, mode, out var resolved, out _) && resolved == operation;
    }

    private static BoxStationToolKind WallConstructionTool(byte stage, BoxStationWorkMode mode)
        => mode == BoxStationWorkMode.Dismantle ? stage switch
        {
            0 => BoxStationToolKind.Wirecutters, 1 => BoxStationToolKind.Screwdriver,
            2 or 5 => BoxStationToolKind.Welder, 3 or 6 => BoxStationToolKind.Crowbar, _ => BoxStationToolKind.Wrench
        } : stage switch
        {
            1 => BoxStationToolKind.Wirecutters, 2 => BoxStationToolKind.Screwdriver,
            3 or 6 => BoxStationToolKind.Welder, 4 => BoxStationToolKind.Crowbar, _ => BoxStationToolKind.Wrench
        };

    private bool ApplyConstructionImpact(int target, float joules)
    {
        if (!_materialsInitialized || !float.IsFinite(joules) || joules <= BoxStationHull.MinimumImpactJoules
            || joules > BoxStationHull.MaximumImpactJoules || !TryConstruction(_layout, target, out var definition)) return false;
        var current = ConstructionAt(target);
        if (current.Stage == BoxStationFrameStage.Empty || !ValidConstruction(_layout, definition, current)) return false;
        bool wall = current.Stage == BoxStationFrameStage.Wall;
        byte remaining;
        if (wall)
        {
            if (!_hull.TryImpactIntegrity(target, joules, out remaining)) return false;
        }
        else
        {
            double breakJoules = current.Stage is BoxStationFrameStage.ReinforcedGirder or BoxStationFrameStage.ReinforcedStruts ? 1_500_000 : 750_000;
            int debit = Math.Min(current.Integrity, (int)Math.Ceiling((joules - BoxStationHull.MinimumImpactJoules) / breakJoules * BoxStationHull.Capacity));
            remaining = (byte)(current.Integrity - debit);
        }
        var next = current with { Integrity = remaining };
        var waste = _constructionWaste;
        if (remaining == 0)
        {
            int steel = wall ? current.WallKind == BoxStationWallKind.Reinforced ? 2 : 3 : 1;
            int plasteel = wall ? current.WallKind == BoxStationWallKind.Reinforced ? 1 : 0
                : current.Stage is BoxStationFrameStage.ReinforcedGirder or BoxStationFrameStage.ReinforcedStruts ? 1 : 0;
            if (current.SteelYield + steel > ushort.MaxValue || current.PlasteelYield + plasteel > ushort.MaxValue) return false;
            waste = AddConstructionUnits(waste, wall && current.WallKind == BoxStationWallKind.Reinforced ? new(0, 1) : new(1, 0));
            next = next with { Stage = BoxStationFrameStage.Empty, WorkStage = 0, WallKind = BoxStationWallKind.None,
                SteelYield = (ushort)(current.SteelYield + steel), PlasteelYield = (ushort)(current.PlasteelYield + plasteel) };
        }
        if (wall && !_hull.ApplyImpact(target, joules)) return false;
        _construction[_constructionDefinitions.Indices[definition.Cell]] = next;
        _constructionWaste = waste;
        _materialsDirty = true;
        return true;
    }

    internal string RecoverConstructionMaterial(int target, BoxStationMaterial material, Guid holder, ulong actor, Vector3 dropPosition)
    {
        if (!TryConstruction(_layout, target, out var definition)) return "Unknown salvage source.";
        var current = ConstructionAt(target);
        int count = material == BoxStationMaterial.Steel ? current.SteelYield : material == BoxStationMaterial.Plasteel ? current.PlasteelYield : 0;
        if (count == 0) return "No matching salvage remains.";
        string? rejection = PrepareMaterialPickup(holder, actor, dropPosition, out int slot);
        if (rejection is not null) return rejection;
        ushort taken = (ushort)Math.Min(MaxMaterialBundleCount, count);
        var next = material == BoxStationMaterial.Steel ? current with { SteelYield = (ushort)(current.SteelYield - taken) }
            : current with { PlasteelYield = (ushort)(current.PlasteelYield - taken) };
        _materialBundles[slot] = new(_nextMaterialBundleId++, material, taken, BoxStationMaterialPlace.Held, dropPosition, actor, holder);
        _construction[_constructionDefinitions.Indices[definition.Cell]] = next;
        _materialsDirty = true;
        return "Salvage recovered.";
    }

    internal string TakeSteelStock(int source, Guid holder, ulong actor, Vector3 dropPosition)
    {
        if (source < 0 || source >= _sheets.Length) return "Unknown metal stock.";
        if (_sheets[source] == 0) return "This stock has no steel sheets remaining.";
        string? rejection = PrepareMaterialPickup(holder, actor, dropPosition, out int slot);
        if (rejection is not null) return rejection;
        ushort count = (ushort)Math.Min(MaxMaterialBundleCount, (int)_sheets[source]);
        var bundle = new BoxStationMaterialBundle(_nextMaterialBundleId, BoxStationMaterial.Steel, count,
            BoxStationMaterialPlace.Held, dropPosition, actor, holder);
        ushort exported = checked((ushort)(_exportedSteel[source] + count));
        int imported = checked(_importedSteel + count);
        _materialBundles[slot] = bundle;
        _nextMaterialBundleId++;
        _sheets[source] -= count;
        _exportedSteel[source] = exported;
        _importedSteel = imported;
        _stateDirty = _materialsDirty = true;
        return "Steel sheets taken.";
    }
}
