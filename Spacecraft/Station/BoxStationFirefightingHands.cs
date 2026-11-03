// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Immutable;
using UnityEngine;

namespace Modstoke.Client;

internal enum SpacecraftHeldStationToolKind : byte { Extinguisher, Foam, RepairTiles, Material, Engineering }

internal readonly record struct SpacecraftHeldStationTool(SpacecraftHeldStationToolKind Kind, int Item, ulong Actor, Guid Holder,
    Vector3 Aim, float AimAmount, BoxStationMaterial Material = default, BoxStationToolKind Engineering = default,
    Vector3 WorkContact = default, float WorkProgress = -1)
{
    internal bool Active => Holder != Guid.Empty;

    internal bool IsShipRepairKit => Kind == SpacecraftHeldStationToolKind.Material
        && Material is BoxStationMaterial.LineSpare or BoxStationMaterial.CircuitSpare;

    internal bool IsShipSupply => IsShipRepairKit
        || Kind == SpacecraftHeldStationToolKind.Material && Material == BoxStationMaterial.Steel;

    internal bool SameHolder(SpacecraftHeldStationTool other)
        => Active && other.Active && Kind == other.Kind && Item == other.Item && Actor == other.Actor && Holder == other.Holder;

    internal bool Render(ClientAircraftCrewPose crew, Vector3 shoulder, Quaternion bodyRotation,
        Vector3 up, out Vector3 position, out Quaternion rotation)
    {
        position = default;
        rotation = bodyRotation;
        if (Kind == SpacecraftHeldStationToolKind.Engineering)
        {
            var grip = BoxStationModel.EngineeringCarryGrip(Engineering);
            var toolContact = shoulder + bodyRotation * new Vector3(-.025f, -.19f, .32f);
            if (WorkProgress >= 0)
            {
                var direction = WorkContact - toolContact;
                if (direction.sqrMagnitude > .0001f)
                {
                    var normal = Vector3.ProjectOnPlane(up, direction.normalized);
                    if (normal.sqrMagnitude < .01f) normal = bodyRotation * Vector3.forward;
                    rotation = Quaternion.LookRotation(direction, normal);
                    float cycle = Mathf.Sin(WorkProgress * Mathf.PI * 12);
                    rotation *= Quaternion.Euler(0, 0, cycle * 3);
                    toolContact = WorkContact - rotation * (BoxStationModel.EngineeringWorkTip(Engineering) - grip)
                        - rotation * Vector3.forward * (Mathf.Abs(cycle) * .008f);
                }
            }
            var toolPalm = Quaternion.LookRotation(rotation * Vector3.left, rotation * Vector3.down);
            if (!crew.RenderRightHandGrip(toolContact, up, toolPalm, out var toolActualContact)) return false;
            position = toolActualContact - rotation * grip;
            return true;
        }
        if (Kind is SpacecraftHeldStationToolKind.RepairTiles or SpacecraftHeldStationToolKind.Material)
        {
            var tileContact = shoulder + bodyRotation * new Vector3(-.025f, -.21f, .34f);
            if (Kind == SpacecraftHeldStationToolKind.Material && Material != BoxStationMaterial.FloorTiles && WorkProgress >= 0)
                tileContact = Vector3.Lerp(tileContact, WorkContact, .55f);
            var tilePalm = Quaternion.LookRotation(bodyRotation * Vector3.left, bodyRotation * Vector3.down);
            if (!crew.RenderRightHandGrip(tileContact, up, tilePalm, out var tileActualContact)) return false;
            // Sheets hang from their upper edge; repair kits retain their upright handle grip.
            if (Kind == SpacecraftHeldStationToolKind.Material && !IsShipRepairKit
                && Material is not (BoxStationMaterial.Rods or BoxStationMaterial.FloorTiles))
                rotation = bodyRotation * Quaternion.Euler(0, 0, 90);
            position = tileActualContact - rotation * (Kind == SpacecraftHeldStationToolKind.Material
                ? BoxStationModel.MaterialCarryGrip(Material) : BoxStationModel.RepairCarryGrip);
            return true;
        }
        if (Kind == SpacecraftHeldStationToolKind.Foam)
        {
            var foamContact = shoulder + bodyRotation * new Vector3(-.025f, -.19f, .30f);
            // Keep the small canister upright, with the palm against its rear grip.
            var foamPalm = Quaternion.LookRotation(bodyRotation * Vector3.left, bodyRotation * Vector3.forward);
            if (!crew.RenderRightHandGrip(foamContact, up, foamPalm, out var foamActualContact)) return false;
            position = foamActualContact - rotation * BoxStationModel.FoamCarryGrip;
            return true;
        }
        if (AimAmount > 0)
        {
            var localAim = Quaternion.Inverse(bodyRotation) * Aim;
            float pitch = Mathf.Atan2(-localAim.y, new Vector2(localAim.x, localAim.z).magnitude) * Mathf.Rad2Deg;
            float yaw = Mathf.Atan2(localAim.x, localAim.z) * Mathf.Rad2Deg;
            // A rigid hose cannot follow steep downward aim without swinging the bottle
            // through the torso. This cosmetic limit never changes the committed rays.
            var aimed = bodyRotation * Quaternion.Euler(Mathf.Clamp(pitch, -65, 20), Mathf.Clamp(yaw, -60, 60), 0);
            rotation = Quaternion.Slerp(bodyRotation, aimed, AimAmount);
        }
        float swing = Mathf.Max(0, Vector3.Dot(rotation * Vector3.up, bodyRotation * Vector3.forward));
        float forward = Mathf.Max(.30f, .26f + swing * BoxStationModel.ExtinguisherCarryGrip.y);
        var contact = shoulder + bodyRotation * new Vector3(-.025f, -.11f, forward);
        // The authored carry rail runs fore/aft; fingers curl across it with the palm down.
        var palm = Quaternion.LookRotation(rotation * Vector3.left, rotation * Vector3.down);
        if (!crew.RenderRightHandGrip(contact, up, palm, out var actualContact)) return false;
        position = actualContact - rotation * BoxStationModel.ExtinguisherCarryGrip;
        return true;
    }
}

// Requests poses from the existing rig owners; it never binds or writes player bones itself.
internal sealed class BoxStationFirefightingHands : IDisposable
{
    private readonly BoxStationModel? _station;
    private readonly SpacecraftToolRack? _rack;
    private readonly SpacecraftExtinguisherRack? _extinguisherRack;
    private readonly SpacecraftHeldStationTool[] _held;
    private readonly bool[] _local, _conflicted;
    private readonly int _extinguisherCount, _repairOffset, _materialOffset, _engineeringOffset;
    private readonly ImmutableArray<BoxStationToolSource> _engineeringSources;
    private readonly int[] _active;
    private int _count, _activeCount;
    private readonly Vector3[] _aim;
    private readonly float[] _aimUntil, _aimAmount;
    private BoxStationFirefightingState? _validated;
    private BoxStationFoamState? _validatedFoam;
    private BoxStationRepairState? _validatedRepair;
    private BoxStationMaterialState? _validatedMaterials;
    private BoxStationEngineeringState? _validatedEngineering;
    private long _validatedHull = -1;
    private ClientSpacecraftRider? _rider;
    private ClientSpacecraftOccupants? _occupants;
    private int _frame = -1;
    private bool _disposed;

    internal bool UsesStation(BoxStationModel? station) => !_disposed && _station != null && ReferenceEquals(_station, station);
    internal bool UsesRack(SpacecraftToolRack rack, SpacecraftExtinguisherRack extinguisherRack)
        => !_disposed && ReferenceEquals(_rack, rack) && ReferenceEquals(_extinguisherRack, extinguisherRack);

    internal BoxStationFirefightingHands(BoxStationModel station) : this(station.Layout)
    {
        _station = station;
    }

    internal BoxStationFirefightingHands(SpacecraftToolRack rack, SpacecraftExtinguisherRack extinguisherRack,
        BoxStationLayout layout) : this(layout)
    {
        _rack = rack;
        _extinguisherRack = extinguisherRack;
    }

    private BoxStationFirefightingHands(BoxStationLayout layout)
    {
        _extinguisherCount = BoxStationFirefighting.GetItems(layout).Length;
        _repairOffset = _extinguisherCount + BoxStationFoam.GetItems(layout).Length;
        _materialOffset = _repairOffset + BoxStationHullRepair.MaxPacks(layout);
        _engineeringOffset = _materialOffset + BoxStationHullRepair.MaxMaterialBundles;
        _engineeringSources = BoxStationEngineering.GetSources(layout);
        int count = _engineeringOffset + _engineeringSources.Length;
        _held = new SpacecraftHeldStationTool[count];
        _active = new int[count];
        _local = new bool[count];
        _conflicted = new bool[count];
        _aim = new Vector3[count];
        _aimUntil = new float[count];
        _aimAmount = new float[count];
    }

    internal void Update(BoxStationFirefightingState? state, Guid localHolder,
        ClientSpacecraftRider? rider, ClientSpacecraftOccupants? occupants, BoxStationFoamState? foamState = null,
        BoxStationRepairState? repairState = null, BoxStationMaterialState? materialsState = null,
        BoxStationEngineeringState? engineeringState = null,
        ReadOnlySpan<SpacecraftManualRepairWork> repairs = default, Transform? ship = null)
    {
        if (_disposed || _station == null) return;
        Prepare(rider, occupants);
        if (_station.PortableState == null)
        {
            Clear();
            return;
        }
        if (state is { } current && _validated != current && !BoxStationFirefighting.ValidState(_station.Layout, current))
            state = null;
        if (foamState is { } foam && _validatedFoam != foam && !BoxStationFoam.ValidState(_station.Layout, foam))
            foamState = null;
        if (repairState is { } repair && _validatedRepair != repair && !BoxStationHullRepair.ValidState(_station.Layout, repair))
            repairState = null;
        if (materialsState is { } materials && _validatedMaterials != materials
            && !BoxStationHullRepair.ValidMaterials(_station.Layout, materials)) materialsState = null;
        var hull = _station.HullState;
        if (engineeringState is { } engineering && (_validatedEngineering != engineering
                || _validatedMaterials != materialsState || _validatedHull != hull.Revision)
            && (materialsState is not { } stock || stock.HullRevision != hull.Revision
                || engineering.HullRevision != hull.Revision
                || !BoxStationEngineering.ValidState(_station.Layout, engineering, hull, stock))) engineeringState = null;
        _validatedHull = hull.Revision;
        UpdateState(state, foamState, repairState, materialsState, engineeringState);
        PreparePoses(localHolder, repairs, ship);
    }

    // The service supplies the coherent, freshness-checked portable assembly used by the rack.
    internal void UpdateShip(BoxStationPortableState? state, Guid localHolder,
        ClientSpacecraftRider? rider, ClientSpacecraftOccupants? occupants,
        ReadOnlySpan<SpacecraftManualRepairWork> repairs = default, Transform? ship = null)
    {
        if (_disposed || _rack == null || _extinguisherRack == null) return;
        Prepare(rider, occupants);
        if (state is not { } current || ship == null || !ship)
        {
            Clear();
            return;
        }
        UpdateState(current.Firefighting, current.Foam, current.Repair, current.Materials, current.Engineering);
        PreparePoses(localHolder, repairs, ship);
    }

    private void Prepare(ClientSpacecraftRider? rider, ClientSpacecraftOccupants? occupants)
    {
        if (!ReferenceEquals(_rider, rider)) _rider?.ClearHeldExtinguisher(this);
        if (!ReferenceEquals(_occupants, occupants)) _occupants?.ClearHeldExtinguishers(this);
        _rider = rider; _occupants = occupants;
        _frame = Time.frameCount;
    }

    private void UpdateState(BoxStationFirefightingState? state, BoxStationFoamState? foamState,
        BoxStationRepairState? repairState, BoxStationMaterialState? materialsState,
        BoxStationEngineeringState? engineeringState)
    {
        if (_validated != state || _validatedFoam != foamState || _validatedRepair != repairState
            || _validatedMaterials != materialsState || _validatedEngineering != engineeringState)
            UpdateHeld(state, foamState, repairState, materialsState, engineeringState);
        _validated = state;
        _validatedFoam = foamState;
        _validatedRepair = repairState;
        _validatedMaterials = materialsState;
        _validatedEngineering = engineeringState;
    }

    private void PreparePoses(Guid localHolder, ReadOnlySpan<SpacecraftManualRepairWork> repairs, Transform? ship)
    {
        float dt = Mathf.Clamp(Time.unscaledDeltaTime, 0, .05f);
        for (int i = 0; i < _activeCount; i++)
        {
            int item = _active[i];
            var held = _held[item];
            if (_station == null && held.Kind is not (SpacecraftHeldStationToolKind.Engineering
                or SpacecraftHeldStationToolKind.Extinguisher)) continue;
            _local[item] = localHolder != Guid.Empty && held.Holder == localHolder;
            if (held.Kind == SpacecraftHeldStationToolKind.Extinguisher)
            {
                _aimAmount[item] = Mathf.MoveTowards(_aimAmount[item], Time.unscaledTime < _aimUntil[item] ? 1 : 0,
                    dt / (Time.unscaledTime < _aimUntil[item] ? .06f : .18f));
                var frame = _station?.Root.transform ?? ship!;
                held = held with { Aim = frame.TransformDirection(_aim[item]), AimAmount = _aimAmount[item] };
                _held[item] = held;
            }
            else if (held.Kind is SpacecraftHeldStationToolKind.Engineering or SpacecraftHeldStationToolKind.Material)
            {
                held = held with { WorkProgress = -1, WorkContact = default };
                if (held.Kind == SpacecraftHeldStationToolKind.Engineering && ship != null && ship)
                    foreach (var work in repairs)
                        if (work.Tool == held.Item && work.Holder == held.Holder && work.Actor == held.Actor)
                        {
                            held = held with
                            {
                                WorkContact = ship.TransformPoint(work.LocalContact),
                                WorkProgress = Mathf.Clamp01(work.Progress)
                            };
                            break;
                        }
                if (held.WorkProgress < 0 && _station?.Visible == true && _validatedEngineering is { } live
                    && live.HullRevision == _station.HullState.Revision)
                    foreach (var work in live.Work)
                        if (work.Holder == held.Holder && work.Actor == held.Actor
                            && (held.Kind == SpacecraftHeldStationToolKind.Engineering ? work.Intent.Tool == held.Item
                                : work.Intent.Tool == -1 && work.Intent.Kind is BoxStationWorkTarget.WallConstruction
                                    or BoxStationWorkTarget.Construction or BoxStationWorkTarget.DoorConstruction))
                        {
                            var contact = work.Intent.Contact;
                            held = held with
                            {
                                WorkContact = _station.Root.transform.TransformPoint(new(contact.X, contact.Y, contact.Z)),
                                WorkProgress = Mathf.Clamp01(work.Elapsed / work.Duration)
                            };
                            break;
                        }
                _held[item] = held;
            }
            if (_local[item]) _rider?.SetHeldExtinguisher(this, _station, held);
            else if (held.Actor != 0) _occupants?.SetHeldExtinguisher(this, _station, held);
        }
    }

    private void UpdateHeld(BoxStationFirefightingState? state, BoxStationFoamState? foamState,
        BoxStationRepairState? repairState, BoxStationMaterialState? materialsState,
        BoxStationEngineeringState? engineeringState)
    {
        bool changed = false;
        int count = _engineeringOffset + (engineeringState?.Tools.Length ?? 0);
        int previousCount = _count;
        _count = count;
        Array.Clear(_conflicted, 0, Math.Max(count, previousCount));
        _activeCount = 0;
        for (int item = 0; item < Math.Max(count, previousCount); item++)
        {
            SpacecraftHeldStationTool held = default;
            if (item < _extinguisherCount && state is { } fire)
            {
                var source = fire.Items[item];
                if (source.Place == BoxStationExtinguisherPlace.Held)
                    held = new(SpacecraftHeldStationToolKind.Extinguisher, item, source.Actor, source.Holder, default, 0);
            }
            else if (item >= _extinguisherCount && item < _repairOffset && foamState is { } liveFoam)
            {
                int index = item - _extinguisherCount;
                var source = liveFoam.Items[index];
                if (source.Place == BoxStationFoamPlace.Held)
                    held = new(SpacecraftHeldStationToolKind.Foam, index, source.Actor, source.Holder, default, 0);
            }
            else if (item >= _repairOffset && item < _materialOffset && repairState is { } liveRepair
                && item - _repairOffset < liveRepair.Packs.Length)
            {
                int index = item - _repairOffset;
                var source = liveRepair.Packs[index];
                if (source.Place == BoxStationRepairPlace.Held)
                    held = new(SpacecraftHeldStationToolKind.RepairTiles, index, source.Actor, source.Holder, default, 0);
            }
            else if (item >= _materialOffset && item < _engineeringOffset && materialsState is { } liveMaterials
                && item - _materialOffset < liveMaterials.Bundles.Length)
            {
                var source = liveMaterials.Bundles[item - _materialOffset];
                if (source.Place == BoxStationMaterialPlace.Held)
                    held = new(SpacecraftHeldStationToolKind.Material, source.Id, source.Actor, source.Holder, default, 0, source.Material);
            }
            else if (item >= _engineeringOffset && item < count && engineeringState is { } liveEngineering)
            {
                int index = item - _engineeringOffset;
                var source = liveEngineering.Tools[index];
                if (source.Place == BoxStationToolPlace.Held)
                    held = new(SpacecraftHeldStationToolKind.Engineering, source.Id, source.Actor, source.Holder,
                        default, 0, Engineering: _engineeringSources[index].Kind);
            }
            if (!_held[item].SameHolder(held))
            {
                changed |= _held[item].Active;
                if (_held[item].Active) SetPose(_held[item], default, Quaternion.identity, false);
                _aim[item] = default;
                _aimUntil[item] = _aimAmount[item] = 0;
            }
            _held[item] = held;
            if (held.Active) _active[_activeCount++] = item;
        }
        // Independent snapshot streams can briefly disagree about which tool owns a hand.
        for (int i = 0; i < _activeCount; i++)
        {
            int item = _active[i];
            var held = _held[item];
            for (int j = i + 1; j < _activeCount; j++)
            {
                int other = _active[j];
                if (held.Holder == _held[other].Holder || held.Actor != 0 && held.Actor == _held[other].Actor)
                    _conflicted[item] = _conflicted[other] = true;
            }
            changed |= _conflicted[item];
        }
        if (changed)
        {
            _rider?.ClearHeldExtinguisher(this);
            _occupants?.ClearHeldExtinguishers(this);
        }
        int visibleCount = 0;
        for (int i = 0; i < _activeCount; i++)
        {
            int item = _active[i];
            var held = _held[item];
            if (_conflicted[item])
            {
                SetPose(held, default, Quaternion.identity, false);
                _held[item] = default;
                _aim[item] = default;
                _aimUntil[item] = _aimAmount[item] = 0;
                continue;
            }
            _active[visibleCount++] = item;
        }
        _activeCount = visibleCount;
    }

    // Only a committed host receipt may aim the carried assembly, in its station or ship frame.
    internal void PlayBurst(int item, Vector3 localDirection)
    {
        if (_disposed || _validated == null || (uint)item >= _extinguisherCount || !_held[item].Active
            || !float.IsFinite(localDirection.x) || !float.IsFinite(localDirection.y)
            || !float.IsFinite(localDirection.z) || localDirection.sqrMagnitude is < .5f or > 1.5f) return;
        _aim[item] = localDirection.normalized;
        _aimUntil[item] = Time.unscaledTime + .28f;
    }

    internal void LateUpdate()
    {
        if (_disposed) return;
        if (_frame != Time.frameCount) { Clear(); return; }
        for (int i = 0; i < _activeCount; i++)
        {
            int item = _active[i];
            var held = _held[item];
            if (!held.Active || _station == null && held.Kind is not (SpacecraftHeldStationToolKind.Engineering
                or SpacecraftHeldStationToolKind.Extinguisher)) continue;
            Vector3 position = default;
            Quaternion rotation = Quaternion.identity;
            bool visible = _local[item]
                ? _rider?.TryHeldExtinguisherPose(this, held, out position, out rotation) == true
                : held.Actor != 0 && _occupants?.TryHeldExtinguisherPose(this, held, out position, out rotation) == true;
            SetPose(held, position, rotation, visible);
        }
    }

    private void SetPose(SpacecraftHeldStationTool held, Vector3 position, Quaternion rotation, bool visible)
    {
        if (_station == null)
        {
            if (held.Kind == SpacecraftHeldStationToolKind.Engineering)
                _rack?.SetHeldToolPose(held.Item, position, rotation, visible);
            else if (held.Kind == SpacecraftHeldStationToolKind.Extinguisher)
                _extinguisherRack?.SetHeldExtinguisherPose(held.Item, position, rotation, visible);
            return;
        }
        if (held.Kind == SpacecraftHeldStationToolKind.Foam)
            _station.SetHeldFoamPose(held.Item, position, rotation, visible);
        else if (held.Kind == SpacecraftHeldStationToolKind.RepairTiles)
            _station.SetHeldRepairPose(held.Item, position, rotation, visible);
        else if (held.Kind == SpacecraftHeldStationToolKind.Material)
            _station.SetHeldMaterialPose(held.Item, position, rotation, visible);
        else if (held.Kind == SpacecraftHeldStationToolKind.Engineering)
            _station.SetHeldEngineeringPose(held.Item, position, rotation, visible);
        else _station.SetHeldExtinguisherPose(held.Item, position, rotation, visible);
    }

    private void Clear()
    {
        _validated = null;
        _validatedFoam = null;
        _validatedRepair = null;
        _validatedMaterials = null;
        _validatedEngineering = null;
        _rider?.ClearHeldExtinguisher(this);
        _occupants?.ClearHeldExtinguishers(this);
        for (int item = 0; item < _count; item++)
        {
            if (_held[item].Active) SetPose(_held[item], default, Quaternion.identity, false);
            _held[item] = default;
            _aim[item] = default;
            _aimUntil[item] = _aimAmount[item] = 0;
        }
        _count = _activeCount = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Clear();
        _rider = null; _occupants = null;
    }
}
