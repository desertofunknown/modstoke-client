// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Vector3 = System.Numerics.Vector3;

namespace Modstoke.Client;

internal enum BoxStationToolKind : byte { Screwdriver, Crowbar, Wirecutters, Wrench, Welder }
internal enum BoxStationToolPlace : byte { Source, Dropped, Held, Stored, ShipStored }
internal enum BoxStationEngineeringControl : byte { Take, Drop, ToggleWelder, Refill, ShipTake, ShipStore }
internal enum BoxStationWorkMode : byte { Repair, Dismantle }
internal enum BoxStationWorkTarget : byte { Structure, Bundle, Construction, Wall, WallConstruction, DoorConstruction }

internal readonly record struct BoxStationToolSource(int Object, int Cell, Vector3 Position,
    BoxStationToolKind Kind, float Speed, byte FuelCapacity);
internal readonly record struct BoxStationTool(int Id, BoxStationToolPlace Place, Vector3 Position,
    ulong Actor, Guid Holder, byte Fuel, bool Lit, float BurnSeconds, int Storage = -1, byte Slot = 0, byte ShipSlot = byte.MaxValue);
internal readonly record struct BoxStationMaintenance([property: JsonRequired] int StructureId,
    [property: JsonRequired] byte Stage, [property: JsonRequired] bool Anchored,
    [property: JsonRequired] float HeatRemaining, [property: JsonRequired] byte Integrity);
internal readonly record struct BoxStationWorkIntent(int Tool, BoxStationWorkTarget Kind, int Target, BoxStationWorkMode Mode, Vector3 Contact);
internal readonly record struct BoxStationWork(Guid Holder, ulong Actor, BoxStationWorkIntent Intent, Vector3 Feet, float Elapsed, float Duration);
internal readonly record struct BoxStationEngineeringState(string LayoutHash, long HullRevision,
    ImmutableArray<BoxStationTool> Tools, ImmutableArray<BoxStationMaintenance> Maintenance, ImmutableArray<BoxStationWork> Work,
    ImmutableArray<ushort> TankFuel);

internal sealed partial class BoxStationEngineering
{
    internal const int MaximumWork = 3;
    internal const byte NoShipSlot = byte.MaxValue, ShipSlotCount = 5;
    internal const float FuelSeconds = 26, HeatSeconds = 30;
    private static readonly ConditionalWeakTable<BoxStationLayout, Definitions> LayoutDefinitions = new();
    private readonly BoxStationLayout _layout;
    private readonly BoxStationHull _hull;
    private readonly BoxStationHullRepair _repair;
    private readonly Func<int, byte, BoxStationWallKind, bool> _restoreWall;
    private readonly Func<BoxStationDoorConstructionState, bool> _commitDoors;
    private readonly Definitions _definitions;
    private readonly BoxStationTool[] _tools;
    private readonly ushort[] _tankFuel;
    private readonly BoxStationMaintenance[] _maintenance;
    private readonly List<int> _heated = new();
    private readonly List<Job> _jobs = new(MaximumWork);
    private long _hullRevision = -1;
    private BoxStationEngineeringState _state;
    private bool _dirty = true;

    internal int ActiveWorkCount => _jobs.Count;

    internal BoxStationEngineering(BoxStationLayout layout, BoxStationHull hull, BoxStationHullRepair repair, bool seedSource,
        Func<int, byte, BoxStationWallKind, bool> restoreWall, Func<BoxStationDoorConstructionState, bool> commitDoors)
    {
        _layout = layout;
        _hull = hull;
        _repair = repair;
        _restoreWall = restoreWall;
        _commitDoors = commitDoors;
        _definitions = LayoutDefinitions.GetValue(layout, BuildDefinitions);
        _tools = new BoxStationTool[_definitions.Sources.Length];
        _tankFuel = new ushort[_definitions.FuelTanks.Length];
        _maintenance = new BoxStationMaintenance[_definitions.Structures.Length];
        for (int i = 0; i < _tools.Length; i++)
        {
            var source = _definitions.Sources[i];
            _tools[i] = new(i, BoxStationToolPlace.Source, source.Position, 0, Guid.Empty,
                seedSource ? source.FuelCapacity : (byte)0, false, 0);
        }
        for (int i = 0; i < _maintenance.Length; i++)
            _maintenance[i] = InitialMaintenance(layout, _definitions.Structures[i]);
        if (seedSource)
            for (int i = 0; i < _tankFuel.Length; i++) _tankFuel[i] = (ushort)_definitions.FuelTanks[i].Capacity;
        SynchronizeHull();
    }

    internal BoxStationEngineeringState State
    {
        get
        {
            SynchronizeHull();
            PruneJobs();
            if (_dirty)
            {
                var maintenance = ImmutableArray.CreateBuilder<BoxStationMaintenance>();
                for (int i = 0; i < _maintenance.Length; i++)
                    if (_maintenance[i].Integrity != 0 && _maintenance[i] != InitialMaintenance(_layout, _definitions.Structures[i]))
                        maintenance.Add(_maintenance[i]);
                var work = ImmutableArray.CreateBuilder<BoxStationWork>(_jobs.Count);
                foreach (var job in _jobs) work.Add(job.Work);
                _state = new(_layout.LayoutHash, _hullRevision, ImmutableArray.CreateRange(_tools),
                    maintenance.ToImmutable(), work.MoveToImmutable(), ImmutableArray.CreateRange(_tankFuel));
                _dirty = false;
            }
            return _state;
        }
    }

    internal int Held(Guid holder)
    {
        if (holder == Guid.Empty) return -1;
        for (int i = 0; i < _tools.Length; i++)
            if (_tools[i].Place == BoxStationToolPlace.Held && _tools[i].Holder == holder) return i;
        return -1;
    }

    internal string Take(int id, Guid holder, ulong actor, Vector3 drop, BoxStationMaterialState? storageState = null)
    {
        if (id < 0 || id >= _tools.Length || _tools[id].Place is BoxStationToolPlace.Held or BoxStationToolPlace.ShipStored)
            return "Tool is no longer available.";
        if (holder == Guid.Empty || Held(holder) >= 0) return "You are already holding a station tool.";
        foreach (var tool in _tools)
            if (actor != 0 && tool.Place == BoxStationToolPlace.Held && tool.Actor == actor) return "You are already holding a station tool.";
        if (_tools[id].Place == BoxStationToolPlace.Stored
            && (storageState is not { } materials || !BoxStationStorage.IsOpen(_layout, materials, _tools[id].Storage)))
            return "Open the storage first.";
        if (!BoxStationFirefighting.ValidDropPosition(_layout, drop)) return "Stand on clear station flooring to take tools.";
        Cancel(holder);
        _tools[id] = _tools[id] with
        {
            Place = BoxStationToolPlace.Held, Position = drop, Actor = actor, Holder = holder, Storage = -1, Slot = 0, ShipSlot = NoShipSlot
        };
        _dirty = true;
        return ToolName(_definitions.Sources[id].Kind) + " taken.";
    }

    internal string TakeShipTool(int id, Guid holder, ulong actor)
    {
        if (id < 0 || id >= _tools.Length || _tools[id].Place != BoxStationToolPlace.ShipStored)
            return "Tool is no longer available.";
        if (holder == Guid.Empty || Held(holder) >= 0) return "You are already holding a station tool.";
        foreach (var tool in _tools)
            if (actor != 0 && tool.Place == BoxStationToolPlace.Held && tool.Actor == actor) return "You are already holding a station tool.";
        Cancel(holder);
        _tools[id] = _tools[id] with { Place = BoxStationToolPlace.Held, Actor = actor, Holder = holder };
        _dirty = true;
        return ToolName(_definitions.Sources[id].Kind) + " taken.";
    }

    internal string StoreShipTool(Guid holder, ulong actor)
    {
        int id = Held(holder);
        if (id < 0) return "You are not holding a station tool.";
        var held = _tools[id];
        if (held.Actor != actor) return "This tool is not held by your current actor.";
        if (held.Lit) return "Switch off the welder before storing it.";
        byte slot = (byte)_definitions.Sources[id].Kind;
        foreach (var tool in _tools)
            if (tool.Id != id && tool.ShipSlot == slot) return "This ship tool slot is occupied.";
        Cancel(holder);
        _tools[id] = held with
        {
            Place = BoxStationToolPlace.ShipStored, Actor = 0, Holder = Guid.Empty, Storage = -1, Slot = 0, ShipSlot = slot
        };
        _dirty = true;
        return "Tool stored aboard ship.";
    }

    internal string Drop(Guid holder, Vector3 drop)
    {
        int id = Held(holder);
        if (id < 0) return "You are not holding a station tool.";
        if (!BoxStationFirefighting.ValidDropPosition(_layout, drop)) return "Drop the tool on clear station flooring.";
        Cancel(holder);
        _tools[id] = _tools[id] with
        {
            Place = BoxStationToolPlace.Dropped, Position = drop, Actor = 0, Holder = Guid.Empty, ShipSlot = NoShipSlot
        };
        _dirty = true;
        return "Tool dropped.";
    }

    internal string StoreTool(Guid holder, int storage, byte slot, BoxStationMaterialState materials)
    {
        int id = Held(holder);
        if (id < 0) return "You are not holding a station tool.";
        if (_tools[id].Lit) return "Switch off the welder before storing it.";
        if (!BoxStationStorage.TryDefinition(_layout, storage, out var definition)) return "Unknown tool storage.";
        if (!BoxStationStorage.IsOpen(_layout, materials, storage)) return "Open the storage first.";
        var position = BoxStationStorage.Position(_layout, definition);
        if (!BoxStationStorage.ValidMembership(_layout, storage, slot, position)) return "Invalid storage slot.";
        foreach (var tool in _tools)
            if (tool.Place == BoxStationToolPlace.Stored && tool.Storage == storage && tool.Slot == slot)
                return "This storage slot is occupied.";
        Cancel(holder);
        _tools[id] = _tools[id] with
        {
            Place = BoxStationToolPlace.Stored, Position = position, Actor = 0, Holder = Guid.Empty,
            Storage = storage, Slot = slot, ShipSlot = NoShipSlot
        };
        _dirty = true;
        return "Tool stored.";
    }

    internal string ToggleWelder(Guid holder)
    {
        int id = Held(holder);
        if (id < 0 || _definitions.Sources[id].Kind != BoxStationToolKind.Welder) return "Hold a welding tool first.";
        var tool = _tools[id];
        if (!tool.Lit && tool.Fuel == 0) return "The welding tool is empty.";
        Cancel(holder);
        _tools[id] = tool with { Lit = !tool.Lit };
        _dirty = true;
        return tool.Lit ? "Welder switched off." : "Welder lit.";
    }

    internal string Refill(int tank, Guid holder, ulong actor)
    {
        if (tank < 0 || tank >= _tankFuel.Length) return "Unknown station fuel tank.";
        if (SpacecraftShoreFuel.IsSupply(_definitions.FuelTanks[tank])) return "This tank outlet is connected to shore fuel service.";
        int id = Held(holder);
        if (id < 0 || _definitions.Sources[id].Kind != BoxStationToolKind.Welder) return "Hold a welding tool first.";
        var tool = _tools[id];
        if (tool.Actor != actor) return "This welding tool is not held by your current actor.";
        if (tool.Lit) return "Switch off the welder before refilling it.";
        int headroom = _definitions.Sources[id].FuelCapacity - tool.Fuel;
        if (headroom <= 0) return "Welder is already full.";
        if (_tankFuel[tank] == 0) return "Station fuel tank is empty.";
        int units = Math.Min(headroom, _tankFuel[tank]);
        Cancel(holder);
        _tankFuel[tank] -= (ushort)units;
        _tools[id] = tool with { Fuel = (byte)(tool.Fuel + units) };
        _dirty = true;
        return $"Welder refilled by {units} fuel units.";
    }

    internal int FuelUnits(int tank) => tank >= 0 && tank < _tankFuel.Length ? _tankFuel[tank] : 0;

    // The station's owning thread holds the ship computer gate across debit and credit.
    internal bool TryTakeFuel(int tank, int units)
    {
        if (units <= 0 || tank < 0 || tank >= _tankFuel.Length || units > _tankFuel[tank]) return false;
        _tankFuel[tank] -= (ushort)units;
        _dirty = true;
        return true;
    }

    internal void ObserveHeld(Guid holder, Vector3 drop)
    {
        int id = Held(holder);
        if (id < 0 || _tools[id].Position == drop && _tools[id].ShipSlot == NoShipSlot
            || !BoxStationFirefighting.ValidDropPosition(_layout, drop)) return;
        _tools[id] = _tools[id] with { Position = drop, ShipSlot = NoShipSlot };
        _dirty = true;
    }

    internal void Release(Guid holder)
    {
        Cancel(holder);
        int id = Held(holder);
        if (id < 0) return;
        var tool = _tools[id];
        _tools[id] = tool with
        {
            Place = tool.ShipSlot == NoShipSlot ? BoxStationToolPlace.Dropped : BoxStationToolPlace.ShipStored,
            Actor = 0, Holder = Guid.Empty, Lit = false
        };
        _dirty = true;
    }

    internal void Cancel(Guid holder)
    {
        for (int i = _jobs.Count - 1; i >= 0; i--)
            if (_jobs[i].Work.Holder == holder) { _jobs.RemoveAt(i); _dirty = true; }
    }

    internal void RecordRebuild(int target, byte previousIntegrity)
    {
        if (!BoxStationHull.TryStructure(_layout, target, out var structure)) return;
        int index = _definitions.Indices[structure.Id];
        var previous = _maintenance[index];
        SynchronizeHull();
        if (_hull.StructureIntegrity(structure.Id) != BoxStationHull.Capacity) return;
        bool anchored = structure.Kind == BoxStationStructureKind.Grille && previousIntegrity != 0 && previous.Anchored;
        _maintenance[index] = new(structure.Id, 0, anchored, 0, BoxStationHull.Capacity);
        _heated.Remove(index);
        CancelTarget(target);
        _dirty = true;
    }

    private void SynchronizeHull()
    {
        if (_hullRevision == _hull.State.Revision) return;
        for (int i = 0; i < _maintenance.Length; i++)
        {
            var previous = _maintenance[i];
            byte integrity = _hull.StructureIntegrity(previous.StructureId);
            if (integrity == previous.Integrity) continue;
            _maintenance[i] = integrity == 0 ? new(previous.StructureId, 0, false, 0, 0)
                : previous with { Integrity = integrity };
            if (integrity == 0) _heated.Remove(i);
            CancelTarget(BoxStationHull.StructureTarget(_layout, previous.StructureId));
        }
        _hullRevision = _hull.State.Revision;
        _dirty = true;
    }

    private void CancelTarget(int target)
    {
        for (int i = _jobs.Count - 1; i >= 0; i--)
            if (_jobs[i].Work.Intent.Kind != BoxStationWorkTarget.Bundle && _jobs[i].Work.Intent.Target == target)
            { _jobs.RemoveAt(i); _dirty = true; }
    }

    internal static string ToolName(BoxStationToolKind kind) => kind switch
    {
        BoxStationToolKind.Screwdriver => "Screwdriver",
        BoxStationToolKind.Crowbar => "Crowbar",
        BoxStationToolKind.Wirecutters => "Wirecutters",
        BoxStationToolKind.Wrench => "Wrench",
        BoxStationToolKind.Welder => "Welder",
        _ => "Unknown tool"
    };
}
