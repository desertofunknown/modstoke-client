// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Immutable;
using System.Globalization;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using TheForest.Utils;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Modstoke.Client;

internal sealed class BoxStationEngineeringControls : IDisposable
{
    private const float Reach = 2.4f, ReadoutDistance = 6;
    private static readonly Color Ink = new(.92f, .96f, .97f, 1), Muted = new(.61f, .69f, .72f, 1),
        Amber = new(1, .72f, .28f, 1);
    private readonly BoxStationModel _model;
    private readonly Action<BoxStationEngineeringControl, int> _command;
    private readonly Action<BoxStationWorkIntent> _work;
    private readonly Action<int, bool, Vector3> _cable;
    private readonly Action<int, bool, Vector3> _floor;
    private readonly ImmutableArray<BoxStationToolSource> _sources;
    private readonly ImmutableArray<BoxStationFuelTank> _fuelTanks;
    private readonly ClientTabletTypography _typography = new();
    private readonly Il2CppStructArray<RaycastHit> _hits = new(size: 32);
    private BoxStationEngineeringState? _checkedState;
    private BoxStationMaterialState? _checkedMaterials;
    private long _checkedHull = -1;
    private bool _validState, _disposed, _clickAction;
    private GameObject? _hud;
    private TextMeshProUGUI _status = null!, _detail = null!, _hint = null!, _feedback = null!;
    private BoxStationWorkMode _mode;
    private float _nextReadout;
    private int _held = -1, _tool = -1, _target = -1, _cableTarget = -1, _floorTarget = -1;
    private int _fuelTank = -1;
    private BoxStationWorkTarget _targetKind;

    internal BoxStationEngineeringControls(BoxStationModel model,
        Action<BoxStationEngineeringControl, int> command, Action<BoxStationWorkIntent> work,
        Action<int, bool, Vector3> cable, Action<int, bool, Vector3> floor)
    {
        _model = model;
        _command = command;
        _work = work;
        _cable = cable;
        _floor = floor;
        _sources = BoxStationEngineering.GetSources(model.Layout);
        _fuelTanks = BoxStationEngineering.GetFuelTanks(model.Layout);
        try { CreateHud(); }
        catch { Dispose(); throw; }
    }

    // The owner supplies only fresh snapshots and a currently leased holder.
    internal bool Update(BoxStationEngineeringState? state, Guid holder, bool interactive, string feedback)
    {
        if (_disposed) return false;
        if (!Input.GetMouseButton(0)) _clickAction = false;
        var hull = _model.HullState;
        var materials = _model.MaterialState;
        if (_checkedState != state || _checkedHull != hull.Revision || _checkedMaterials != materials)
        {
            _checkedState = state;
            _checkedHull = hull.Revision;
            _checkedMaterials = materials;
            _validState = state is { } candidate && candidate.HullRevision == hull.Revision
                && materials is { } stock && stock.HullRevision == hull.Revision
                && BoxStationEngineering.ValidState(_model.Layout, candidate, hull, stock);
            _nextReadout = 0;
        }
        if (!_validState || _model.PortableState is not { } projected || projected.Engineering != state) state = null;
        var camera = LocalPlayer.MainCam;
        if (!_model.Visible || camera == null || !camera || !camera.isActiveAndEnabled
            || !interactive || !ClientSpacecraftRider.WorldInputAvailable || Cursor.visible)
        {
            Hide();
            return false;
        }
        var ray = new Ray(camera.transform.position, camera.transform.forward);
        var localEye = _model.Root.transform.InverseTransformPoint(ray.origin);
        int held = -1, tool = -1, target = -1, cableTarget = -1, floorTarget = -1, fuelTank = -1;
        BoxStationTool carried = default;
        var targetKind = BoxStationWorkTarget.Structure;
        Vector3 targetContact = default;
        Vector3 cableContact = default;
        Vector3 floorContact = default;
        float nearest = ReadoutDistance;
        if (state is { } live)
        {
            foreach (var item in live.Tools)
            {
                if (item.Place == BoxStationToolPlace.Held && holder != Guid.Empty && item.Holder == holder)
                {
                    held = item.Id;
                    carried = item;
                }
                if (item.Place is BoxStationToolPlace.Held or BoxStationToolPlace.Stored or BoxStationToolPlace.ShipStored
                    || (localEye - new Vector3(item.Position.X, item.Position.Y, item.Position.Z)).sqrMagnitude
                        > (ReadoutDistance + .6f) * (ReadoutDistance + .6f)
                    || !AimAt(ray, _model.EngineeringToolPoint(item.Id), .2f, ref nearest)) continue;
                tool = item.Id;
            }
            if (_model.TryStoredPortableTarget(ray, nearest, out var storedKind, out int stored, out var storedContact)
                && ClearContact(ray.origin, storedContact))
            {
                tool = storedKind == SpacecraftHeldStationToolKind.Engineering ? stored : -1;
                nearest = Vector3.Distance(ray.origin, storedContact);
            }
            for (int i = 0; i < _fuelTanks.Length; i++)
            {
                var frame = _model.FuelTankFrame(i);
                if ((frame.localPosition - localEye).sqrMagnitude > (ReadoutDistance + 1) * (ReadoutDistance + 1)
                    || Vector3.Dot(ray.direction, frame.forward) >= -.1f
                    || !AimAt(ray, _model.FuelTankPoint(i), .18f, ref nearest)) continue;
                fuelTank = i;
                tool = -1;
            }
            if (held >= 0 && fuelTank < 0)
            {
                if (holder != Guid.Empty && Input.GetKeyDown(KeyCode.V))
                {
                    _mode = _mode == BoxStationWorkMode.Repair ? BoxStationWorkMode.Dismantle : BoxStationWorkMode.Repair;
                    _nextReadout = 0;
                }
                float workDistance = Reach;
                if (_model.TryEngineeringStructureTarget(ray, workDistance, out int structure, out var contact)
                    && ClearContact(ray.origin, contact))
                {
                    target = structure;
                    targetContact = contact;
                    workDistance = Vector3.Distance(ray.origin, contact);
                }
                if (_model.TryEngineeringWallTarget(ray, workDistance, out int wall, out var wallContact)
                    && CanWorkConstruction(wall, carried) && ClearContact(ray.origin, wallContact))
                {
                    target = wall;
                    targetKind = BoxStationWorkTarget.Wall;
                    targetContact = wallContact;
                    workDistance = Vector3.Distance(ray.origin, wallContact);
                }
                if (_sources[held].Kind == BoxStationToolKind.Welder && carried.Lit)
                {
                    SelectBundle(ray, BoxStationMaterial.GlassShards, ref workDistance, ref target, ref targetKind, ref targetContact);
                    SelectBundle(ray, BoxStationMaterial.PlasmaGlassShards, ref workDistance, ref target, ref targetKind, ref targetContact);
                }
                if (_model.TryDoorConstructionTarget(localEye, _model.Root.transform.InverseTransformDirection(ray.direction),
                    null, out int door, out var localDoorContact) && CanWorkDoor(door, carried))
                {
                    var worldContact = _model.Root.transform.TransformPoint(localDoorContact);
                    float distance = Vector3.Distance(ray.origin, worldContact);
                    if (float.IsFinite(distance) && distance <= workDistance && ClearContact(ray.origin, worldContact))
                    {
                        target = door;
                        targetKind = BoxStationWorkTarget.DoorConstruction;
                        targetContact = worldContact;
                        workDistance = distance;
                    }
                }
                if (targetKind != BoxStationWorkTarget.DoorConstruction && _sources[held].Kind == BoxStationToolKind.Wirecutters
                    && _model.TryCableTarget(ray, false, out int cableIndex, out var localContact))
                {
                    var worldContact = _model.Root.transform.TransformPoint(localContact);
                    float distance = Vector3.Distance(ray.origin, worldContact);
                    if (float.IsFinite(distance) && distance <= workDistance && ClearContact(ray.origin, worldContact))
                    {
                        cableTarget = cableIndex;
                        cableContact = localContact;
                        target = -1;
                    }
                }
                if (targetKind != BoxStationWorkTarget.DoorConstruction && _sources[held].Kind == BoxStationToolKind.Crowbar
                    && _model.TryFloorTarget(ray, false, out int cell, out var localFloorContact))
                {
                    var worldContact = _model.Root.transform.TransformPoint(localFloorContact);
                    float distance = Vector3.Distance(ray.origin, worldContact);
                    if (float.IsFinite(distance) && distance <= workDistance && ClearContact(ray.origin, worldContact))
                    {
                        floorTarget = cell;
                        floorContact = localFloorContact;
                        target = -1;
                    }
                }
            }
        }
        bool canTake = held < 0 && holder != Guid.Empty && nearest <= Reach;
        bool canRefill = fuelTank >= 0 && held >= 0 && nearest <= Reach && state is { } fuelState
            && !SpacecraftShoreFuel.IsSupply(_fuelTanks[fuelTank])
            && _sources[held].Kind == BoxStationToolKind.Welder && !carried.Lit
            && carried.Fuel < _sources[held].FuelCapacity && fuelState.TankFuel[fuelTank] > 0;
        bool visible = held >= 0 || tool >= 0 || fuelTank >= 0;
        bool changed = held != _held || tool != _tool || target != _target || targetKind != _targetKind
            || cableTarget != _cableTarget || floorTarget != _floorTarget || fuelTank != _fuelTank;
        _held = held; _tool = tool; _target = target; _targetKind = targetKind; _cableTarget = cableTarget;
        _floorTarget = floorTarget;
        _fuelTank = fuelTank;
        if (_hud != null && _hud.activeSelf != visible) { _hud.SetActive(visible); changed = true; }
        if (visible && (changed || Time.unscaledTime >= _nextReadout))
        {
            _nextReadout = Time.unscaledTime + .1f;
            UpdateHud(state, holder, carried, canTake, nearest <= Reach, canRefill, feedback);
        }
        if (state == null || holder == Guid.Empty) return false;
        if (canTake && tool >= 0 && Input.GetKeyDown(KeyCode.E))
        {
            _command(BoxStationEngineeringControl.Take, tool);
            return true;
        }
        if (held < 0) return fuelTank >= 0;
        if (canRefill && Input.GetKeyDown(KeyCode.E))
        {
            _command(BoxStationEngineeringControl.Refill, fuelTank);
            return true;
        }
        if (Input.GetKeyDown(KeyCode.G))
        {
            _command(BoxStationEngineeringControl.Drop, 0);
            return true;
        }
        if (_sources[held].Kind == BoxStationToolKind.Welder && Input.GetKeyDown(KeyCode.R))
        {
            _command(BoxStationEngineeringControl.ToggleWelder, 0);
            return true;
        }
        if (!_clickAction && cableTarget >= 0 && Input.GetMouseButtonDown(0))
        {
            _clickAction = true;
            _cable(cableTarget, false, cableContact);
            return true;
        }
        if (!_clickAction && floorTarget >= 0 && Input.GetMouseButtonDown(0))
        {
            _clickAction = true;
            _floor(floorTarget, false, floorContact);
            return true;
        }
        if (cableTarget < 0 && floorTarget < 0 && !_clickAction && target >= 0 && Input.GetMouseButton(0))
        {
            var contact = _model.Root.transform.InverseTransformPoint(targetContact);
            var intent = new BoxStationWorkIntent(held, targetKind, target, _mode, new(contact.x, contact.y, contact.z));
            if (BoxStationEngineering.ValidIntent(_model.Layout, intent)) _work(intent);
        }
        return fuelTank >= 0 || Input.GetMouseButton(0) || Input.GetKeyDown(KeyCode.V);
    }

    private void SelectBundle(Ray ray, BoxStationMaterial material, ref float distance,
        ref int target, ref BoxStationWorkTarget kind, ref Vector3 contact)
    {
        if (!_model.TryEngineeringBundleTarget(ray, distance, material, out int bundle, out var point)
            || !ClearContact(ray.origin, point)) return;
        distance = Vector3.Distance(ray.origin, point);
        target = bundle;
        kind = BoxStationWorkTarget.Bundle;
        contact = point;
    }

    private bool CanWorkConstruction(int target, BoxStationTool tool)
    {
        if (_model.MaterialState is not { } materials) return false;
        var kind = _sources[tool.Id].Kind;
        if (kind == BoxStationToolKind.Welder && (!tool.Lit || tool.Fuel == 0)) return false;
        var construction = BoxStationHullRepair.ConstructionAt(_model.Layout, materials, target);
        return BoxStationHullRepair.ResolveConstruction(_model.Layout, construction, kind, _mode, out _, out _)
            || construction.Stage == BoxStationFrameStage.Wall && construction.Integrity is > 0 and < 100
                && kind == BoxStationToolKind.Welder && _mode == BoxStationWorkMode.Repair;
    }

    private bool CanWorkDoor(int target, BoxStationTool tool)
    {
        if (_model.MaterialState is not { } materials) return false;
        var kind = _sources[tool.Id].Kind;
        if (kind == BoxStationToolKind.Welder && (!tool.Lit || tool.Fuel == 0)) return false;
        var door = BoxStationHullRepair.DoorAt(_model.Layout, materials.DoorConstruction, target);
        return BoxStationHullRepair.ResolveDoorTool(_model.Layout, door, kind, _mode, out _, out _);
    }

    private bool AimAt(Ray ray, Vector3 contact, float radius, ref float nearest)
    {
        var delta = contact - ray.origin;
        float distance = delta.magnitude, along = Vector3.Dot(delta, ray.direction);
        if (!float.IsFinite(distance) || distance <= .01f || distance > nearest || along <= 0
            || (delta - ray.direction * along).sqrMagnitude > radius * radius || !ClearContact(ray.origin, contact)) return false;
        nearest = distance;
        return true;
    }

    private bool ClearContact(Vector3 eye, Vector3 contact)
    {
        var delta = contact - eye;
        float distance = delta.magnitude;
        if (!float.IsFinite(distance) || distance <= .01f) return false;
        int count = Physics.RaycastNonAlloc(new Ray(eye, delta / distance), _hits,
            Mathf.Max(0, distance - .003f), ~0, QueryTriggerInteraction.Ignore);
        if (count < 0 || count >= _hits.Length) return false;
        var player = LocalPlayer.Transform;
        for (int i = 0; i < count; i++)
        {
            var collider = _hits[i].collider;
            if (collider != null && (player == null || !collider.transform.IsChildOf(player))) return false;
        }
        return true;
    }

    private void UpdateHud(BoxStationEngineeringState? state, Guid holder, BoxStationTool carried,
        bool canTake, bool withinReach, bool canRefill, string feedback)
    {
        _feedback.text = feedback;
        _detail.text = "";
        _status.color = Ink;
        if (_fuelTank >= 0 && state is { } fuelState)
        {
            _status.text = "FUEL DISPENSER";
            _detail.text = fuelState.TankFuel[_fuelTank].ToString(CultureInfo.InvariantCulture) + " / "
                + _fuelTanks[_fuelTank].Capacity.ToString(CultureInfo.InvariantCulture) + " u";
            bool welder = _held >= 0 && _sources[_held].Kind == BoxStationToolKind.Welder;
            if (welder) _detail.text += "   WELDER " + carried.Fuel.ToString(CultureInfo.InvariantCulture)
                + " / " + _sources[_held].FuelCapacity.ToString(CultureInfo.InvariantCulture) + " u";
            _hint.text = !withinReach ? "OUT OF REACH" : SpacecraftShoreFuel.IsSupply(_fuelTanks[_fuelTank]) ? "SHORE SUPPLY CONNECTED"
                : !welder ? "HOLD WELDING TOOL"
                : carried.Lit ? "R: switch off   G: drop" : canRefill ? "E: refill   R: ignition   G: drop"
                : carried.Fuel >= _sources[_held].FuelCapacity ? "TOOL FULL   G: drop" : "TANK EMPTY   G: drop";
            return;
        }
        if (_held < 0)
        {
            _status.text = BoxStationEngineering.ToolName(_sources[_tool].Kind).ToUpperInvariant();
            _hint.text = canTake ? "E: take" : "OUT OF REACH";
            return;
        }
        var kind = _sources[_held].Kind;
        _status.text = BoxStationEngineering.ToolName(kind).ToUpperInvariant() + " — "
            + (_mode == BoxStationWorkMode.Repair ? "REPAIR" : "DISMANTLE");
        if (kind == BoxStationToolKind.Welder)
            _status.text += " — " + carried.Fuel.ToString(CultureInfo.InvariantCulture) + "/"
                + _sources[_held].FuelCapacity.ToString(CultureInfo.InvariantCulture) + " FUEL — " + (carried.Lit ? "LIT" : "OFF");
        if (_cableTarget >= 0)
        {
            _status.text = BoxStationEngineering.ToolName(kind).ToUpperInvariant();
            _detail.text = "EXPOSED CABLE — 1 LENGTH";
            _hint.text = "LMB: cut   G: drop";
            return;
        }
        if (_floorTarget >= 0)
        {
            _status.text = BoxStationEngineering.ToolName(kind).ToUpperInvariant();
            _detail.text = "FLOOR COVERING — 1 TILE";
            _hint.text = "LMB: lift tile   G: drop";
            return;
        }
        _hint.text = (_target >= 0 ? _targetKind == BoxStationWorkTarget.Bundle ? "LMB: recycle   " : "Hold LMB: work   " : "")
            + "V: mode   " + (kind == BoxStationToolKind.Welder ? "R: ignition   " : "") + "G: drop";
        if (_targetKind == BoxStationWorkTarget.Structure && _target >= 0
            && BoxStationHull.TryStructure(_model.Layout, _target, out var structure))
        {
            string condition = "";
            if (state is { } live)
                foreach (var maintenance in live.Maintenance)
                    if (maintenance.StructureId == structure.Id)
                    {
                        condition = !maintenance.Anchored ? " — UNANCHORED" : maintenance.Stage switch
                        {
                            8 => " — SECURED", 7 => " — HEATED", 6 => " — BOLTS RELEASED",
                            5 => " — FRAME OPEN", 4 => " — BARS CUT", _ => " — ANCHORED"
                        };
                        break;
                    }
            _detail.text = (structure.Kind == BoxStationStructureKind.Window ? "WINDOW" : "GRILLE") + condition;
        }
        if (_targetKind == BoxStationWorkTarget.Wall && _target >= 0)
            _detail.text = WallDetail(_target);
        if (_targetKind == BoxStationWorkTarget.DoorConstruction && _target >= 0)
            _detail.text = DoorDetail(_target);
        if (state is { } progress)
            foreach (var job in progress.Work)
                if (job.Holder == holder)
                {
                    _detail.text = (job.Intent.Kind == BoxStationWorkTarget.DoorConstruction
                        ? DoorDetail(job.Intent.Target) + " — WORKING " : job.Intent.Kind == BoxStationWorkTarget.Wall
                            ? WallDetail(job.Intent.Target) + " — WORKING " : "WORKING — ")
                        + Mathf.RoundToInt(Mathf.Clamp01(job.Elapsed / job.Duration) * 100)
                        .ToString(CultureInfo.InvariantCulture) + "%";
                    break;
                }
    }

    private string DoorDetail(int target)
    {
        if (_model.MaterialState is not { } materials) return "";
        var door = BoxStationHullRepair.DoorAt(_model.Layout, materials.DoorConstruction, target);
        return door.Stage switch
        {
            BoxStationDoorStage.Frame => "DOOR FRAME — UNANCHORED",
            BoxStationDoorStage.Anchored => "DOOR FRAME — ANCHORED",
            BoxStationDoorStage.Wired => "DOOR FRAME — WIRED",
            BoxStationDoorStage.Electronics => "DOOR ASSEMBLY — ELECTRONICS INSTALLED",
            BoxStationDoorStage.Complete => "DOOR — ASSEMBLED",
            _ => ""
        };
    }

    private string WallDetail(int target)
    {
        if (_model.MaterialState is not { } materials
            || !BoxStationHullRepair.TryConstruction(_model.Layout, target, out _)) return "";
        var construction = BoxStationHullRepair.ConstructionAt(_model.Layout, materials, target);
        string name = construction.Stage switch
        {
            BoxStationFrameStage.Wall => construction.WallKind == BoxStationWallKind.Reinforced ? "REINFORCED WALL" : "WALL",
            BoxStationFrameStage.Girder => "GIRDER",
            BoxStationFrameStage.DisplacedGirder => "UNANCHORED GIRDER",
            BoxStationFrameStage.ReinforcedStruts => "RELEASED STRUTS",
            BoxStationFrameStage.ReinforcedGirder => "REINFORCED GIRDER",
            _ => "EMPTY FRAME SITE"
        };
        return name + " — " + construction.Integrity.ToString(CultureInfo.InvariantCulture) + "% INTEGRITY"
            + (construction.WorkStage > 0 ? " — STAGE " + construction.WorkStage.ToString(CultureInfo.InvariantCulture) + "/6" : "");
    }

    private void CreateHud()
    {
        _hud = new GameObject("Engineering readout");
        var canvas = _hud.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 117;
        var scaler = _hud.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new(1280, 720);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
        var panel = new GameObject("Engineering status").AddComponent<RectTransform>();
        panel.SetParent(_hud.transform, false);
        panel.anchorMin = panel.anchorMax = panel.pivot = new(.5f, 0);
        panel.anchoredPosition = new(0, 266);
        panel.sizeDelta = new(900, 114);
        _status = Label(panel, "Tool and mode", 0, 24, 18);
        _detail = Label(panel, "Work state", 27, 22, 15);
        _hint = Label(panel, "Controls", 53, 22, 15);
        _feedback = Label(panel, "Host response", 81, 28, 14);
        _detail.color = _hint.color = Muted;
        _feedback.color = Amber;
        _hud.SetActive(false);
    }

    private TextMeshProUGUI Label(Transform parent, string name, float y, float height, float size)
    {
        var rect = new GameObject(name).AddComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.anchorMin = rect.anchorMax = rect.pivot = new(0, 1);
        rect.anchoredPosition = new(0, -y);
        rect.sizeDelta = new(900, height);
        var text = rect.gameObject.AddComponent<TextMeshProUGUI>();
        _typography.Apply(text, TabletTextRole.Control);
        text.fontSize = size; text.color = Ink; text.isOverlay = true;
        text.richText = false; text.enableWordWrapping = false; text.raycastTarget = false;
        text.overflowMode = TextOverflowModes.Ellipsis; text.alignment = TextAlignmentOptions.Center;
        return text;
    }

    internal void Hide()
    {
        if (_hud != null && _hud && _hud.activeSelf) _hud.SetActive(false);
        _held = _tool = _target = _cableTarget = _floorTarget = _fuelTank = -1;
        // Waiting for the matching inventory snapshot must not restart work under a held click.
        if (!Input.GetMouseButton(0)) _clickAction = false;
        _nextReadout = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hud != null && _hud) { _hud.SetActive(false); UnityEngine.Object.Destroy(_hud); }
        _hud = null;
        _typography.Dispose();
    }
}
