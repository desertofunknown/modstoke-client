// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Vector3 = System.Numerics.Vector3;

namespace Modstoke.Client;

internal sealed partial class BoxStationEngineering
{
    private enum Operation : byte { None, Anchor, Remove, Heat, Bolts, Pop, Cut, Unframe, Secure, Repair, Rebuild, Recycle }
    private readonly record struct Job(BoxStationWork Work, BoxStationMaintenance Expected, BoxStationMaterialBundle Material, Operation Operation,
        BoxStationConstruction ExpectedConstruction, BoxStationConstructionOperation ConstructionOperation,
        BoxStationDoorAssembly ExpectedDoor, BoxStationDoorOperation DoorOperation);

    internal bool TryBegin(Guid holder, ulong actor, BoxStationWorkIntent intent, Vector3 feet, out string result)
    {
        SynchronizeHull();
        PruneJobs();
        result = "This tool cannot perform that operation.";
        if (holder == Guid.Empty || !ValidIntent(_layout, intent) || !ValidFeet(_layout, feet)) return false;
        Cancel(holder);
        foreach (var active in _jobs)
            if (active.Work.Actor == actor
                || active.Work.Intent.Kind != BoxStationWorkTarget.Bundle && intent.Kind != BoxStationWorkTarget.Bundle
                && active.Work.Intent.Target == intent.Target)
            { result = "This work is already in progress."; return false; }
        if (_jobs.Count >= MaximumWork) { result = "Too many station jobs are in progress."; return false; }

        var maintenance = default(BoxStationMaintenance);
        var material = default(BoxStationMaterialBundle);
        var construction = default(BoxStationConstruction);
        var constructionOperation = BoxStationConstructionOperation.None;
        var door = default(BoxStationDoorAssembly);
        var doorOperation = BoxStationDoorOperation.None;
        Operation operation;
        float duration;
        if (intent.Kind == BoxStationWorkTarget.DoorConstruction && intent.Tool == -1)
        {
            if (Held(holder) >= 0 || !FindMaterial(_repair.HeldMaterials(holder), out material)
                || material.Place != BoxStationMaterialPlace.Held || material.Holder != holder || material.Actor != actor
                || !_repair.ResolveDoorMaterial(holder, intent.Target, out doorOperation, out _, out duration))
            { result = "Hold enough matching material for this door assembly."; return false; }
            door = _repair.DoorAt(intent.Target);
            operation = Operation.None;
        }
        else if (intent.Kind == BoxStationWorkTarget.WallConstruction)
        {
            if (Held(holder) >= 0 || !FindMaterial(_repair.HeldMaterials(holder), out material)
                || material.Place != BoxStationMaterialPlace.Held || material.Holder != holder || material.Actor != actor
                || !_repair.ResolveConstructionMaterial(holder, intent.Target, out constructionOperation, out _, out duration))
            { result = "Hold enough matching material for this frame."; return false; }
            construction = _repair.ConstructionAt(intent.Target);
            operation = Operation.None;
        }
        else if (intent.Kind == BoxStationWorkTarget.Construction)
        {
            if (Held(holder) >= 0 || !FindMaterial(_repair.HeldMaterials(holder), out material)
                || material.Place != BoxStationMaterialPlace.Held || material.Holder != holder || material.Actor != actor
                || !BoxStationHull.TryStructure(_layout, intent.Target, out var structure)) return false;
            maintenance = _maintenance[_definitions.Indices[structure.Id]];
            int cost = BoxStationHullRepair.MaterialCost(_layout, intent.Target, maintenance.Integrity);
            if (cost == 0 || material.Material != BoxStationHullRepair.StructureMaterial(_layout, structure) || material.Count < cost)
            { result = "Hold enough matching material for this structure."; return false; }
            operation = Operation.Rebuild;
            duration = structure.Kind == BoxStationStructureKind.Grille && maintenance.Integrity == 0 ? 1 : 0;
        }
        else
        {
            if (Held(holder) != intent.Tool || _tools[intent.Tool].Actor != actor) return false;
            var tool = _tools[intent.Tool];
            var source = _definitions.Sources[intent.Tool];
            if (source.Kind == BoxStationToolKind.Welder && (!tool.Lit || tool.Fuel == 0))
            { result = "Light the welding tool first."; return false; }
            if (intent.Kind == BoxStationWorkTarget.DoorConstruction)
            {
                door = _repair.DoorAt(intent.Target);
                if (!BoxStationHullRepair.ResolveDoorTool(_layout, door, source.Kind, intent.Mode, out doorOperation, out duration))
                    return false;
                operation = Operation.None;
                duration *= source.Speed;
            }
            else if (intent.Kind == BoxStationWorkTarget.Bundle)
            {
                if (source.Kind != BoxStationToolKind.Welder || !FindMaterial(intent.Target, out material)
                    || material.Place != BoxStationMaterialPlace.Dropped
                    || material.Material is not (BoxStationMaterial.GlassShards or BoxStationMaterial.PlasmaGlassShards)) return false;
                operation = Operation.Recycle;
                duration = 0;
            }
            else if (intent.Kind == BoxStationWorkTarget.Wall)
            {
                construction = _repair.ConstructionAt(intent.Target);
                operation = Operation.None;
                if (!_repair.ResolveConstruction(intent.Target, source.Kind, intent.Mode, out constructionOperation, out duration))
                {
                    if (construction.Stage != BoxStationFrameStage.Wall || construction.Integrity is 0 or >= BoxStationHull.Capacity
                        || source.Kind != BoxStationToolKind.Welder || intent.Mode != BoxStationWorkMode.Repair) return false;
                    maintenance = maintenance with { Integrity = construction.Integrity };
                    operation = Operation.Repair;
                    duration = WallRepairDuration(construction.WallKind);
                    if (duration == 0) return false;
                }
                duration *= source.Speed;
            }
            else
            {
                BoxStationHull.TryStructure(_layout, intent.Target, out var structure);
                maintenance = _maintenance[_definitions.Indices[structure.Id]];
                operation = Resolve(_layout, structure, maintenance, source.Kind, intent.Mode, out duration);
                if (operation == Operation.None) return false;
                duration *= source.Speed;
            }
        }
        var job = new Job(new(holder, actor, intent, feet, 0, duration), maintenance, material, operation,
            construction, constructionOperation, door, doorOperation);
        if (duration == 0) return Commit(job, out result);
        _jobs.Add(job);
        _dirty = true;
        result = "Work started.";
        return true;
    }

    internal void Step(float dt)
    {
        if (!float.IsFinite(dt) || dt < 0) throw new ArgumentOutOfRangeException(nameof(dt));
        SynchronizeHull();
        for (int i = 0; i < _tools.Length; i++)
        {
            var tool = _tools[i];
            if (!tool.Lit || dt == 0) continue;
            float elapsed = tool.BurnSeconds + dt;
            int consumed = (int)Math.Min(tool.Fuel, Math.Floor(elapsed / FuelSeconds));
            byte fuel = (byte)(tool.Fuel - consumed);
            _tools[i] = tool with { Fuel = fuel, Lit = fuel != 0, BurnSeconds = fuel == 0 ? 0 : (float)(elapsed - consumed * FuelSeconds) };
            _dirty = true;
        }
        for (int heated = _heated.Count - 1; heated >= 0; heated--)
        {
            int i = _heated[heated];
            var state = _maintenance[i];
            if (dt == 0) continue;
            float heat = MathF.Max(0, state.HeatRemaining - dt);
            _maintenance[i] = state with { HeatRemaining = heat, Stage = heat == 0 ? (byte)8 : (byte)7 };
            if (heat == 0)
            {
                _heated.RemoveAt(heated);
                CancelTarget(BoxStationHull.StructureTarget(_layout, state.StructureId));
            }
            _dirty = true;
        }
        PruneJobs();
        // Remove the completed job before committing: hull synchronization can cancel other jobs.
        for (int i = _jobs.Count - 1; i >= 0; i--)
        {
            if (i >= _jobs.Count) continue;
            var job = _jobs[i];
            job = job with { Work = job.Work with { Elapsed = MathF.Min(job.Work.Duration, job.Work.Elapsed + dt) } };
            _dirty |= dt != 0;
            if (job.Work.Elapsed < job.Work.Duration) { _jobs[i] = job; continue; }
            _jobs.RemoveAt(i);
            Commit(job, out _);
        }
    }

    private void PruneJobs()
    {
        for (int i = _jobs.Count - 1; i >= 0; i--)
            if (!JobValid(_jobs[i])) { _jobs.RemoveAt(i); _dirty = true; }
    }

    private bool JobValid(Job job)
    {
        var work = job.Work;
        var intent = work.Intent;
        if (MaterialWork(intent))
        {
            if (Held(work.Holder) >= 0 || !FindMaterial(job.Material.Id, out var material)
                || !SameMaterial(material, job.Material) || _repair.HeldMaterials(work.Holder) != material.Id) return false;
        }
        else
        {
            if (Held(work.Holder) != intent.Tool) return false;
            var tool = _tools[intent.Tool];
            if (tool.Actor != work.Actor || _definitions.Sources[intent.Tool].Kind == BoxStationToolKind.Welder
                && (!tool.Lit || tool.Fuel == 0)) return false;
        }
        if (intent.Kind == BoxStationWorkTarget.Bundle)
            return FindMaterial(job.Material.Id, out var bundle) && SameMaterial(bundle, job.Material) && bundle.Position == job.Material.Position;
        if (intent.Kind == BoxStationWorkTarget.DoorConstruction)
        {
            var currentDoor = _repair.DoorAt(intent.Target);
            if (currentDoor != job.ExpectedDoor) return false;
            if (intent.Tool == -1)
                return _repair.ResolveDoorMaterial(work.Holder, intent.Target, out var operation, out _, out float seconds)
                    && operation == job.DoorOperation && seconds == work.Duration;
            var source = _definitions.Sources[intent.Tool];
            return BoxStationHullRepair.ResolveDoorTool(_layout, currentDoor, source.Kind, intent.Mode, out var toolOperation, out float toolSeconds)
                && toolOperation == job.DoorOperation && toolSeconds * source.Speed == work.Duration;
        }
        if (intent.Kind is BoxStationWorkTarget.Wall or BoxStationWorkTarget.WallConstruction)
        {
            var currentConstruction = _repair.ConstructionAt(intent.Target);
            var expected = job.ExpectedConstruction;
            return currentConstruction.Cell == expected.Cell && currentConstruction.Stage == expected.Stage
                && currentConstruction.WorkStage == expected.WorkStage && currentConstruction.Integrity == expected.Integrity
                && currentConstruction.WallKind == expected.WallKind;
        }
        if (!BoxStationHull.TryStructure(_layout, intent.Target, out var structure)) return false;
        var current = _maintenance[_definitions.Indices[structure.Id]];
        return current.Integrity == job.Expected.Integrity && current.Stage == job.Expected.Stage && current.Anchored == job.Expected.Anchored;
    }

    private static bool SameMaterial(BoxStationMaterialBundle a, BoxStationMaterialBundle b)
        => a.Id == b.Id && a.Material == b.Material && a.Count == b.Count && a.Place == b.Place && a.Holder == b.Holder && a.Actor == b.Actor;

    private bool FindMaterial(int id, out BoxStationMaterialBundle material)
    {
        foreach (var bundle in _repair.Materials.Bundles)
            if (bundle.Id == id) { material = bundle; return true; }
        material = default;
        return false;
    }

    private bool Commit(Job job, out string result)
    {
        SynchronizeHull();
        result = "Work interrupted.";
        if (!JobValid(job)) return false;
        int target = job.Work.Intent.Target;
        if (job.Work.Intent.Kind == BoxStationWorkTarget.DoorConstruction)
        {
            if (!_repair.CommitDoorConstruction(job.Work.Holder, target, job.ExpectedDoor, job.Material, job.DoorOperation, _commitDoors))
                return false;
            CancelTarget(target);
            _dirty = true;
            result = "Door work completed.";
            return true;
        }
        if (job.Operation == Operation.Recycle)
        {
            result = _repair.RecycleDroppedMaterials(target);
            return FindMaterial(target, out var recycled) && recycled.Material != job.Material.Material;
        }
        if (job.Work.Intent.Kind is BoxStationWorkTarget.Wall or BoxStationWorkTarget.WallConstruction)
        {
            bool completed = job.ConstructionOperation != BoxStationConstructionOperation.None
                ? _repair.CommitConstruction(job.Work.Holder, target, job.ExpectedConstruction, job.Material, job.ConstructionOperation, _restoreWall)
                : _restoreWall(target, job.Expected.Integrity, job.ExpectedConstruction.WallKind);
            if (!completed) return false;
            SynchronizeHull();
            CancelTarget(target);
            _dirty = true;
            result = job.Operation == Operation.Repair ? "Wall repaired." : "Work completed.";
            return true;
        }
        BoxStationHull.TryStructure(_layout, target, out var structure);
        int index = _definitions.Indices[structure.Id];
        var state = _maintenance[index];
        if (job.Operation == Operation.Rebuild)
        {
            long revision = _hull.State.Revision;
            result = _repair.RebuildStructure(job.Work.Holder, target);
            if (revision == _hull.State.Revision) return false;
            RecordRebuild(target, state.Integrity);
            return true;
        }
        if (job.Operation == Operation.Remove)
        {
            long revision = _hull.State.Revision;
            result = _repair.DismantleStructure(target);
            SynchronizeHull();
            return revision != _hull.State.Revision;
        }
        if (job.Operation == Operation.Repair)
        {
            if (!_hull.RestoreStructure(target)) return false;
            SynchronizeHull();
            result = "Window repaired.";
            return true;
        }
        _maintenance[index] = job.Operation switch
        {
            Operation.Anchor => state with { Anchored = !state.Anchored },
            Operation.Heat => state with { Stage = 7, HeatRemaining = HeatSeconds },
            Operation.Bolts => state with { Stage = 6, HeatRemaining = 0 },
            Operation.Pop => state with { Stage = 5 },
            Operation.Cut => state with { Stage = 4 },
            Operation.Unframe => state with { Stage = 0, Anchored = false },
            Operation.Secure => state with { Stage = 8 },
            _ => state
        };
        if (job.Operation == Operation.Heat) _heated.Add(index);
        else if (job.Operation == Operation.Bolts) _heated.Remove(index);
        CancelTarget(target);
        _dirty = true;
        result = "Work completed.";
        return true;
    }

    private static Operation Resolve(BoxStationLayout layout, BoxStationStructure structure, BoxStationMaintenance state,
        BoxStationToolKind tool, BoxStationWorkMode mode, out float duration)
    {
        duration = 0;
        if (state.Integrity == 0) return Operation.None;
        if (structure.Kind == BoxStationStructureKind.Grille)
            return tool == BoxStationToolKind.Wirecutters ? Operation.Remove : tool == BoxStationToolKind.Screwdriver ? Operation.Anchor : Operation.None;
        if (tool == BoxStationToolKind.Welder && mode == BoxStationWorkMode.Repair && state.Integrity < BoxStationHull.Capacity)
        { duration = 4; return Operation.Repair; }
        bool reinforced = Reinforced(layout, structure);
        if (state.Stage == 0)
        {
            if (tool == BoxStationToolKind.Screwdriver) { duration = 3; return Operation.Anchor; }
            if (!state.Anchored && tool == BoxStationToolKind.Wrench) { duration = 3; return Operation.Remove; }
            if (reinforced && state.Anchored && tool == BoxStationToolKind.Crowbar) { duration = 10; return Operation.Secure; }
            return Operation.None;
        }
        if (!reinforced) return Operation.None;
        bool plasma = structure.Plasma;
        switch (state.Stage)
        {
            case 8 when tool == BoxStationToolKind.Welder && mode == BoxStationWorkMode.Dismantle:
                duration = plasma ? 18 : 15; return Operation.Heat;
            case 7 when tool == BoxStationToolKind.Screwdriver:
                duration = plasma ? 8 : 5; return Operation.Bolts;
            case 6 when tool == BoxStationToolKind.Crowbar:
                duration = plasma ? 5 : 4; return Operation.Pop;
            case 5 when tool == BoxStationToolKind.Wirecutters:
                duration = plasma ? 3 : 2; return Operation.Cut;
            case 4 when tool == BoxStationToolKind.Wrench:
                duration = plasma ? 5 : 4; return Operation.Unframe;
            default: return Operation.None;
        }
    }
}
