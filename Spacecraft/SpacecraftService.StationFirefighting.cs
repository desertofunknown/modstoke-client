// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using GasVector = System.Numerics.Vector3;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftService
{
    private BoxStationFirefightingControls? _firefightingControls;
    private BoxStationFirefightingHands? _firefightingHands;
    private BoxStationWaterEffects? _waterEffects;
    private Il2CppReferenceArray<Collider>? _firefightingOverlaps;
    private Il2CppStructArray<RaycastHit>? _waterHits;
    private long _nextHeldFirefighting;
    private bool _firefightingInput;

    private Guid LocalFirefightingHolder => IsClient ? _clientLease : _local?.Lease ?? Guid.Empty;

    private static bool ValidStationFirefighting(int target, ushort setting, Vector3 aim)
    {
        if (setting > (ushort)BoxStationFirefightingControl.ShipSpray || !Finite(aim)) return false;
        var control = (BoxStationFirefightingControl)setting;
        if (control is BoxStationFirefightingControl.Spray or BoxStationFirefightingControl.ShipSpray)
            return target == 0 && aim.sqrMagnitude is >= .98f and <= 1.02f;
        if (aim.x != 0 || aim.y != 0 || aim.z != 0) return false;
        var layout = BoxStationLayout.Current;
        return control switch
        {
            BoxStationFirefightingControl.TakeCabinet or BoxStationFirefightingControl.ToggleCabinet
                or BoxStationFirefightingControl.StoreCabinet => (uint)target < (uint)BoxStationFirefighting.GetCabinets(layout).Length,
            BoxStationFirefightingControl.TakeDropped or BoxStationFirefightingControl.ShipTake
                => (uint)target < (uint)BoxStationFirefighting.GetItems(layout).Length,
            BoxStationFirefightingControl.Refill => (uint)target < (uint)BoxStationFirefighting.GetTanks(layout).Length,
            _ => target == 0
        };
    }

    private void CommandFirefighting(BoxStationFirefightingControl control, int target, Vector3 aim)
    {
        if (!_ready || !ValidStationFirefighting(target, (ushort)control, aim)) return;
        Dispatch(ActionCode.StationFirefighting, stationTarget: target, stationSetting: (ushort)control, stationAim: aim);
    }

    private string ExecuteStationFirefighting(Peer peer, int target, ushort setting, Vector3 aim, Vector3 actorPosition)
    {
        if (!ValidStationFirefighting(target, setting, aim)) return "Invalid firefighting control.";
        var control = (BoxStationFirefightingControl)setting;
        if (control is BoxStationFirefightingControl.ShipTake or BoxStationFirefightingControl.ShipStore
            or BoxStationFirefightingControl.ShipSpray)
            return ExecuteShipFirefighting(peer, control, target, aim);
        if (TryActor(peer, out var feet, out _, out _) && _body?.ContainsCabin(feet) == true)
        {
            if (control == BoxStationFirefightingControl.Safety)
                return ExecuteShipFirefighting(peer, control, target, aim);
            if (control == BoxStationFirefightingControl.Drop) return "Return the extinguisher to its cradle.";
            if (control == BoxStationFirefightingControl.Spray) return "Aim the extinguisher again from inside the ship.";
        }
        string failure = StationControlAccess(peer, actorPosition);
        if (failure.Length != 0) return failure;
        if (!_orbitalStation!.ContainsWalkArea(actorPosition)) return "Stand on the station deck to use its firefighting equipment.";
        var tools = _stationSystems.Firefighting;
        if (!TryActor(peer, out _, out _, out var actor)) return "The player attachment changed.";
        if (control is BoxStationFirefightingControl.TakeCabinet or BoxStationFirefightingControl.TakeDropped)
        {
            if (HasHeldMaterials(peer.Lease, actor)) return "Put down the material bundle first.";
            if (HasHeldEngineering(peer.Lease, actor)) return "Put down the engineering tool first.";
            foreach (var item in _stationSystems.Foam.State.Items)
                if (item.Place == BoxStationFoamPlace.Held
                    && (item.Holder == peer.Lease || actor != 0 && item.Actor == actor))
                    return "Put down the sealant canister first.";
            foreach (var pack in _stationSystems.Repair.State.Packs)
                if (pack.Place == BoxStationRepairPlace.Held
                    && (pack.Holder == peer.Lease || actor != 0 && pack.Actor == actor))
                    return "Put down the tile bundle first.";
        }
        if (control is BoxStationFirefightingControl.TakeCabinet or BoxStationFirefightingControl.ToggleCabinet
            or BoxStationFirefightingControl.StoreCabinet or BoxStationFirefightingControl.TakeDropped or BoxStationFirefightingControl.Refill)
        {
            Vector3 contact;
            if (control == BoxStationFirefightingControl.TakeDropped)
            {
                var item = tools.State.Items[target];
                if (item.Place == BoxStationExtinguisherPlace.Stored)
                {
                    if (!StationStorageCurrent() || !_orbitalStation.TryStoredPortableContact(
                        SpacecraftHeldStationToolKind.Extinguisher, target,
                        actorPosition + _orbitalStation.Root.transform.up * 1.3f, out contact))
                        return "That stored extinguisher is not accessible.";
                }
                else if (item.Place == BoxStationExtinguisherPlace.Dropped)
                    contact = _orbitalStation.Root.transform.TransformPoint(new Vector3(item.Position.X, 0, item.Position.Z)
                        + BoxStationModel.ExtinguisherCarryGrip);
                else return "That extinguisher is no longer available.";
            }
            else contact = _orbitalStation.FirefightingControlPoint(control, target);
            failure = ReachStationControl(peer, actorPosition, contact);
            if (failure.Length != 0) return failure;
        }
        if (control == BoxStationFirefightingControl.Spray) return SprayStationWater(peer, actorPosition, aim.normalized);
        if (control == BoxStationFirefightingControl.StoreCabinet)
        {
            var point = _orbitalStation.Root.transform.InverseTransformPoint(
                _orbitalStation.CabinetFrame(target).TransformPoint(new Vector3(0, -.32f, 0)));
            if (!StationDoorDropClear(point, .2f)) return "The cabinet is obstructed by a door frame.";
        }
        var drop = default(GasVector);
        if (control is BoxStationFirefightingControl.TakeCabinet or BoxStationFirefightingControl.TakeDropped
            or BoxStationFirefightingControl.Drop)
            if (!TryStationToolDrop(peer, actorPosition, out drop)) return "There is no clear station floor within reach.";
        string result = control switch
        {
            BoxStationFirefightingControl.TakeCabinet => tools.TakeCabinet(target, peer.Lease, actor, drop),
            BoxStationFirefightingControl.ToggleCabinet => tools.ToggleCabinet(target),
            BoxStationFirefightingControl.StoreCabinet => tools.StoreCabinet(target, peer.Lease),
            BoxStationFirefightingControl.TakeDropped => tools.TakeDropped(target, peer.Lease, actor, drop, _stationSystems.Repair.Materials),
            BoxStationFirefightingControl.Drop => tools.Drop(peer.Lease, drop),
            BoxStationFirefightingControl.Safety => tools.ToggleSafety(peer.Lease),
            BoxStationFirefightingControl.Refill => tools.Refill(target, peer.Lease),
            _ => "Unknown firefighting control."
        };
        MarkFirefightingDirty();
        return result;
    }

    private void ReleaseFirefighting(Peer peer, bool retainEngineering = false)
    {
        if (IsClient) return;
        CancelEngineeringWork(peer);
        int engineering = _stationSystems.Engineering.Held(peer.Lease);
        if (engineering >= 0)
        {
            var tool = _stationSystems.Engineering.State.Tools[engineering];
            if (!retainEngineering || !TryActor(peer, out _, out _, out var actor) || tool.Actor != actor)
                _stationSystems.Engineering.Release(peer.Lease);
            else if (tool.Lit) _stationSystems.Engineering.ToggleWelder(peer.Lease);
            MarkEngineeringDirty();
        }
        int extinguisher = _stationSystems.Firefighting.Held(peer.Lease);
        if (extinguisher >= 0)
        {
            var item = _stationSystems.Firefighting.State.Items[extinguisher];
            if (!retainEngineering || !TryActor(peer, out _, out _, out var actor) || item.Actor != actor)
            {
                _stationSystems.Firefighting.Release(peer.Lease);
                MarkFirefightingDirty();
            }
        }
        if (_stationSystems.Foam.Held(peer.Lease) >= 0)
        {
            _stationSystems.Foam.Release(peer.Lease);
            MarkFoamDirty();
        }
        if (_stationSystems.Repair.Held(peer.Lease) >= 0)
        {
            _stationSystems.Repair.Release(peer.Lease);
            MarkRepairDirty();
        }
        if (_stationSystems.Repair.HeldMaterials(peer.Lease) >= 0)
        {
            _stationSystems.Repair.ReleaseMaterials(peer.Lease);
            MarkMaterialsDirty();
        }
    }

    private void ObserveHeldFirefighting()
    {
        if (IsClient || Now < _nextHeldFirefighting) return;
        _nextHeldFirefighting = Now + 100;
        foreach (var peer in _seats)
        {
            if (peer == null) continue;
            int foam = _stationSystems.Foam.Held(peer.Lease);
            int repair = _stationSystems.Repair.Held(peer.Lease);
            int material = _stationSystems.Repair.HeldMaterials(peer.Lease);
            int engineering = _stationSystems.Engineering.Held(peer.Lease);
            int extinguisher = _stationSystems.Firefighting.Held(peer.Lease);
            if (engineering < 0 && material < 0 && repair < 0 && foam < 0 && extinguisher < 0) continue;
            var identity = ReadPeerIdentity(peer);
            if (identity == PeerIdentity.Retired || !HasCrewAccess(peer))
            { ReleaseFirefighting(peer); continue; }
            if (identity == PeerIdentity.Unavailable) SuspendAttachment(peer);
            if (!TryActor(peer, out var feet, out _, out var actor))
            {
                if (!CanRetainSuspendedShipPortable(peer))
                { ReleaseFirefighting(peer); continue; }
                // Retained identity can hold an existing tool, never perform work
                // or establish a new position while the native observation is missing.
                actor = _movement[peer].Actor;
            }
            bool materialCurrent = material < 0;
            if (material >= 0)
                foreach (var bundle in _stationSystems.Repair.Materials.Bundles)
                    if (bundle.Id == material && bundle.Place == BoxStationMaterialPlace.Held
                        && bundle.Holder == peer.Lease && bundle.Actor == actor)
                    { materialCurrent = true; break; }
            if (engineering >= 0 && _stationSystems.Engineering.State.Tools[engineering].Actor != actor
                || !materialCurrent
                || repair >= 0 && _stationSystems.Repair.State.Packs[repair].Actor != actor
                || foam >= 0 && _stationSystems.Foam.State.Items[foam].Actor != actor
                || extinguisher >= 0 && _stationSystems.Firefighting.State.Items[extinguisher].Actor != actor)
            { ReleaseFirefighting(peer); continue; }
            if (peer.AttachmentSuspended)
            {
                if (!CanRetainSuspendedShipPortable(peer))
                { ReleaseFirefighting(peer); continue; }
                // Keep physical custody while attachment recovers, but stop all held-tool work.
                CancelEngineeringWork(peer);
                if (_manualRepairInputs[peer.Seat] is { } input) StopManualRepair(input);
                if (engineering >= 0 && _stationSystems.Engineering.State.Tools[engineering].Lit)
                {
                    _stationSystems.Engineering.ToggleWelder(peer.Lease);
                    MarkEngineeringDirty();
                }
                continue;
            }
            bool stationAccess = StationControlAccess(peer, feet).Length == 0;
            bool stationWalk = stationAccess && _orbitalStation!.ContainsWalkArea(feet);
            if (!(engineering >= 0 && CanRetainShipEngineeringTool(peer, actor, feet))
                && !(extinguisher >= 0 && CanRetainShipExtinguisher(peer, actor, feet))
                && !(stationAccess && (stationWalk || CarryingShipSpares(peer, actor, feet))))
            { ReleaseFirefighting(peer); continue; }
            // Docked walk surfaces include the cabin; only a return to station flooring releases a rack reservation.
            if (!stationWalk || (engineering >= 0 || extinguisher >= 0) && _body?.ContainsCabin(feet) == true) continue;
            var kind = engineering >= 0 ? SpacecraftHeldStationToolKind.Engineering : material >= 0 ? SpacecraftHeldStationToolKind.Material : repair >= 0 ? SpacecraftHeldStationToolKind.RepairTiles
                : foam >= 0 ? SpacecraftHeldStationToolKind.Foam : SpacecraftHeldStationToolKind.Extinguisher;
            if (TryStationToolDrop(peer, feet, out var drop, kind))
            {
                if (engineering >= 0) _stationSystems.Engineering.ObserveHeld(peer.Lease, drop);
                else if (material >= 0) _stationSystems.Repair.ObserveHeldMaterials(peer.Lease, drop);
                else if (repair >= 0) _stationSystems.Repair.ObserveHeld(peer.Lease, drop);
                else if (foam >= 0) _stationSystems.Foam.ObserveHeld(peer.Lease, drop);
                else _stationSystems.Firefighting.ObserveHeld(peer.Lease, drop);
            }
        }
    }

    private bool CanRetainShipEngineeringTool(Peer peer, ulong actor, Vector3 feet)
    {
        if (!CanRetainShipPortable(peer, feet)) return false;
        foreach (var tool in _stationSystems.Engineering.State.Tools)
            if (tool.Place == BoxStationToolPlace.Held && tool.Holder == peer.Lease && tool.Actor == actor) return true;
        return false;
    }

    private bool TryStationToolDrop(Peer peer, Vector3 feet, out GasVector drop,
        SpacecraftHeldStationToolKind kind = SpacecraftHeldStationToolKind.Extinguisher)
    {
        drop = default;
        if (_orbitalStation?.Visible != true) return false;
        var frame = _orbitalStation.Root.transform;
        var local = frame.InverseTransformPoint(feet);
        if (!Finite(local) || local.y is < -.05f or > .7f) return false;
        var actor = ActorTransform(peer);
        var facing = actor != null && actor ? frame.InverseTransformDirection(actor.forward) : Vector3.forward;
        facing.y = 0;
        facing = facing.sqrMagnitude > .1f ? facing.normalized : Vector3.forward;
        var right = Vector3.Cross(Vector3.up, facing);
        var overlaps = _firefightingOverlaps ??= new Il2CppReferenceArray<Collider>(size: 32);
        var half = kind switch
        {
            SpacecraftHeldStationToolKind.Engineering => new Vector3(.13f, .11f, .33f),
            SpacecraftHeldStationToolKind.Material => new Vector3(.25f, .09f, .33f),
            SpacecraftHeldStationToolKind.RepairTiles => new Vector3(.19f, .04f, .19f),
            SpacecraftHeldStationToolKind.Foam => new Vector3(.05f, .12f, .05f),
            _ => new Vector3(.13f, .29f, .13f)
        };
        float height = half.y + .02f;
        for (int candidate = 0; candidate < 4; candidate++)
        {
            var point = new Vector3(local.x, 0, local.z) + (candidate switch
            { 0 => facing * .55f, 1 => right * .55f, 2 => -right * .55f, _ => Vector3.zero });
            var value = new GasVector(point.x, 0, point.z);
            float radius = MathF.Max(half.x, half.z);
            if (!StationToolFloorClear(point, radius) || !RepairDropClear(point, radius)
                || !MaterialsDropClear(point, radius) || !EngineeringDropClear(point, radius)) continue;
            // Keep the saved tool's volume clear. Ignore only its holder,
            // not machinery, another player, or a door leaf currently crossing the floor.
            int count = Physics.OverlapBoxNonAlloc(frame.TransformPoint(point + Vector3.up * height),
                half, overlaps, frame.rotation, ~0, QueryTriggerInteraction.Ignore);
            bool clear = count >= 0 && count < overlaps.Length;
            for (int i = 0; i < overlaps.Length; i++)
            {
                var collider = overlaps[i];
                if (i < count && collider && !(actor != null && actor && collider.transform.IsChildOf(actor))) clear = false;
                overlaps[i] = null!;
            }
            if (!clear) continue;
            // A free volume on the other side of a thin wall is not a reachable drop.
            var eye = feet + frame.up * 1.3f;
            var destination = frame.TransformPoint(point + Vector3.up * height);
            var delta = destination - eye;
            if (!TraceWater(peer, eye, delta.normalized, delta.magnitude, out var distance)
                || distance + .015f < delta.magnitude) continue;
            drop = value;
            return true;
        }
        return false;
    }

    private bool StationToolFloorClear(Vector3 point, float radius)
    {
        var layout = _stationSystems.Layout;
        if (!StationDoorDropClear(point, radius)) return false;
        for (int sample = 0; sample < 5; sample++)
        {
            var support = point;
            if (sample > 0)
                support += new Vector3((sample & 1) == 0 ? -radius : radius, 0, sample <= 2 ? -radius : radius);
            if (!BoxStationFirefighting.ValidDropPosition(layout, new(support.x, 0, support.z))) return false;
            int cell = layout.CellAt(support);
            if (!_stationSystems.Hull.Breached(cell, false)) continue;
            var center = layout.CellPosition(cell);
            if (MathF.Abs(support.x - center.x) < 1 && MathF.Abs(support.z - center.z) < 1) return false;
        }
        return true;
    }

    private bool StationDoorDropClear(Vector3 point, float radius)
    {
        if (_orbitalStation?.Visible != true) return false;
        var layout = _stationSystems.Layout;
        foreach (var assembly in _stationSystems.Repair.DoorConstruction.Assemblies)
        {
            if (!_orbitalStation.DoorConstructionBounds(BoxStationDoors.Target(layout, assembly.Edge), out var bounds))
                return false;
            if (MathF.Abs(point.x - bounds.center.x) < radius + bounds.extents.x
                && MathF.Abs(point.z - bounds.center.z) < radius + bounds.extents.z) return false;
        }
        return true;
    }

    private string SprayStationWater(Peer peer, Vector3 feet, Vector3 aim)
    {
        if (!StationHullCurrent) return "Station atmosphere telemetry is unavailable.";
        var frame = _orbitalStation!.Root.transform;
        var actor = ActorTransform(peer);
        if (actor == null || !actor) return "The player attachment changed.";
        var originWorld = feet + frame.up * 1.25f + actor.right * .22f + actor.forward * .32f;
        var eye = feet + frame.up * 1.3f;
        var muzzle = originWorld - eye;
        if (!TraceWater(peer, eye, muzzle.normalized, muzzle.magnitude, out var muzzleRange)
            || muzzleRange + .01f < muzzle.magnitude) return "Move the extinguisher clear of the obstruction.";
        var origin = frame.InverseTransformPoint(originWorld);
        if (!FirefightingPoint(origin)) return "Aim from inside the station deck.";
        var right = Vector3.Cross(MathF.Abs(aim.y) > .95f ? Vector3.forward : Vector3.up, aim).normalized;
        var up = Vector3.Cross(aim, right).normalized;
        var endpoints = new Vector3[5];
        Span<int> cells = stackalloc int[5];
        cells.Fill(-1);
        for (int ray = 0; ray < endpoints.Length; ray++)
        {
            var direction = (aim + (ray switch
            { 1 => right * .06f, 2 => -right * .06f, 3 => up * .06f, 4 => -up * .06f, _ => Vector3.zero })).normalized;
            float limit = 6;
            for (int step = 1; step <= 50; step++)
                if (!FirefightingPoint(origin + direction * (step * .12f))) { limit = (step - 1) * .12f; break; }
            if (!TraceWater(peer, originWorld, frame.TransformDirection(direction), limit, out var length))
                return "The spray path could not be resolved.";
            endpoints[ray] = origin + direction * length;
            if (!FirefightingPoint(endpoints[ray])) return "Aim within the station deck.";
            // A stream deposits its finite share into the first hot compartment it
            // reaches. Compartment gas is well mixed; this is not a per-cell fire solver.
            for (float distance = 0; distance <= length; distance += .12f)
            {
                int cell = _orbitalStation.AtmosphereCellAt(frame.TransformPoint(origin + direction * distance));
                if (cell >= 0 && _stationSystems.GetAtmosphere(cell).TemperatureC > 20)
                { cells[ray] = cell; break; }
            }
        }
        if (!_stationSystems.Firefighting.TrySpray(peer.Lease, out int item, out float units, out string status)) return status;
        double share = units / 5d;
        for (int ray = 0; ray < cells.Length; ray++)
            if (cells[ray] >= 0) _stationSystems.ApplyWaterSpray(cells[ray], share);
        _nextStationSnapshot = 0;
        BroadcastFirefightingBurst(item, origin, endpoints, units);
        return status;
    }

    private bool TraceWater(Peer peer, Vector3 origin, Vector3 direction, float maximum, out float length)
    {
        length = maximum;
        if (!Finite(origin) || !Finite(direction) || !float.IsFinite(maximum) || maximum < 0) return false;
        if (maximum <= .001f) return true;
        if (direction.sqrMagnitude is < .98f or > 1.02f) return false;
        var hits = _waterHits ??= new Il2CppStructArray<RaycastHit>(size: 64);
        int count = Physics.RaycastNonAlloc(origin, direction, hits, maximum, ~0, QueryTriggerInteraction.Ignore);
        if (count < 0 || count >= hits.Length) return false;
        var actor = ActorTransform(peer);
        for (int i = 0; i < count; i++)
        {
            var hit = hits[i];
            if (!hit.collider || actor != null && actor && hit.collider.transform.IsChildOf(actor)) continue;
            if (!float.IsFinite(hit.distance) || hit.distance < 0) return false;
            length = MathF.Min(length, MathF.Max(0, hit.distance - .01f));
        }
        return true;
    }

    private void PrepareFirefightingHands()
    {
        if (_orbitalStation?.Visible != true)
        {
            _firefightingControls?.Update(null, Guid.Empty, false, "");
            _foamControls?.Hide();
            _repairControls?.Hide();
            _materialControls?.Hide();
            _engineeringControls?.Hide();
            _orbitalStation?.UpdatePortables(null);
            if (_toolRack == null || _extinguisherRack == null)
            {
                _firefightingHands?.Dispose(); _firefightingHands = null;
                return;
            }
            if (_firefightingHands?.UsesRack(_toolRack, _extinguisherRack) != true)
            {
                _firefightingHands?.Dispose();
                _firefightingHands = new BoxStationFirefightingHands(_toolRack, _extinguisherRack, _stationSystems.Layout);
            }
            var frame = _body is { Ready: true, TelemetryCurrent: true } && _body.State.Travel == SpacecraftTravel.None
                ? _body.Root.transform : null;
            _firefightingHands.UpdateShip(frame != null ? CurrentPortablesState : null, LocalFirefightingHolder,
                _rider, _occupants, CurrentManualRepairs, frame);
            return;
        }
        _orbitalStation.UpdatePortables(CurrentPortablesState);
        var portables = _orbitalStation.PortableState;
        if (_firefightingHands?.UsesStation(_orbitalStation) != true)
        {
            _firefightingHands?.Dispose();
            _firefightingHands = new BoxStationFirefightingHands(_orbitalStation);
        }
        var ship = _body is { Ready: true, TelemetryCurrent: true } && _body.State.Travel == SpacecraftTravel.None
            ? _body.Root.transform : null;
        _firefightingHands.Update(portables?.Firefighting, LocalFirefightingHolder, _rider, _occupants,
            portables?.Foam, portables?.Repair, portables?.Materials, portables?.Engineering, CurrentManualRepairs, ship);
    }

    private bool UpdateFirefightingControls(bool interactive)
    {
        _firefightingInput = false;
        if (_orbitalStation?.Visible != true) return false;
        if (LocalAlive() && _body?.ContainsCabin(TheForest.Utils.LocalPlayer.Transform.position) == true)
            interactive = false;
        _firefightingControls ??= new BoxStationFirefightingControls(_orbitalStation, CommandFirefighting);
        return _firefightingInput = _firefightingControls.Update(CurrentFirefightingState,
            LocalFirefightingHolder, interactive, _feedback);
    }

    private void ShowFirefightingBurst(BoxStationWaterBurst burst)
    {
        var direction = (burst.Endpoints[0] - burst.Origin).normalized;
        if (burst.Ship)
        {
            if (_body is not { Ready: true, TelemetryCurrent: true } || _extinguisherRack == null) return;
            _shipWaterEffects ??= new BoxStationWaterEffects(_body, SetShipExtinguisherSpray);
            _shipWaterEffects.Show(burst);
            if (_firefightingHands?.UsesStation(_orbitalStation) == true)
                direction = _orbitalStation!.Root.transform.InverseTransformDirection(_body.Root.transform.TransformDirection(direction));
        }
        else
        {
            if (_orbitalStation?.Visible != true) return;
            _waterEffects ??= new BoxStationWaterEffects(_orbitalStation);
            _waterEffects.Show(burst);
            if (_firefightingHands?.UsesStation(_orbitalStation) != true) return;
        }
        _firefightingHands?.PlayBurst(burst.Item, direction);
    }

    private void ReleaseFirefightingViews()
    {
        _firefightingHands?.Dispose(); _firefightingHands = null;
        _firefightingControls?.Dispose(); _firefightingControls = null;
        _foamControls?.Dispose(); _foamControls = null;
        _repairControls?.Dispose(); _repairControls = null;
        _materialControls?.Dispose(); _materialControls = null;
        _engineeringControls?.Dispose(); _engineeringControls = null;
        _waterEffects?.Dispose(); _waterEffects = null;
        _shipWaterEffects?.Dispose(); _shipWaterEffects = null;
        _nextShipSpray = 0;
        _firefightingInput = false;
        _foamInput = false;
        _repairInput = false;
        _materialInput = false;
        _engineeringInput = false;
    }
}
