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

internal sealed class BoxStationMaterialsControls : IDisposable
{
    private const float Reach = 2.4f, ReadoutDistance = 6;
    private static readonly Color Ink = new(.92f, .96f, .97f, 1), Muted = new(.61f, .69f, .72f, 1),
        Amber = new(1, .72f, .28f, 1);
    private readonly BoxStationModel _model;
    private readonly Action<BoxStationMaterialControl, int, BoxStationMaterial, Vector3> _command;
    private readonly Action<BoxStationWorkIntent> _work;
    private readonly Action<int, bool, Vector3> _cable;
    private readonly Action<int, bool, Vector3> _floor;
    private readonly ImmutableArray<BoxStationMaterialSource> _sources;
    private readonly ClientTabletTypography _typography = new();
    private readonly Il2CppStructArray<RaycastHit> _hits = new(size: 32);
    private BoxStationMaterialState? _checkedState;
    private long _checkedHull = -1;
    private bool _validState, _disposed, _clickAction, _doorBuild, _doorTarget;
    private GameObject? _hud;
    private TextMeshProUGUI _status = null!, _detail = null!, _hint = null!, _feedback = null!;
    private float _nextReadout;
    private int _held = -1, _source = -1, _bundle = -1, _yield = -1, _target = -1, _cableTarget = -1, _floorTarget = -1;
    private int _storage = -1;
    private BoxStationMaterial _yieldMaterial;
    private Guid _holder;

    internal BoxStationMaterialsControls(BoxStationModel model,
        Action<BoxStationMaterialControl, int, BoxStationMaterial, Vector3> command, Action<BoxStationWorkIntent> work,
        Action<int, bool, Vector3> cable, Action<int, bool, Vector3> floor)
    {
        _model = model;
        _command = command;
        _work = work;
        _cable = cable;
        _floor = floor;
        _sources = BoxStationHullRepair.GetMaterialSources(model.Layout);
        try { CreateHud(); }
        catch { Dispose(); throw; }
    }

    // Snapshot freshness and the holder lease belong to the station owner.
    internal bool Update(BoxStationMaterialState? state, Guid holder, bool interactive, string feedback,
        BoxStationPortableState? portables, bool storageOnly)
    {
        if (_disposed) return false;
        if (!Input.GetMouseButton(0)) _clickAction = false;
        _holder = holder;
        var hull = _model.HullState;
        if (_checkedState != state || _checkedHull != hull.Revision)
        {
            _checkedState = state;
            _checkedHull = hull.Revision;
            _validState = state is { } candidate && candidate.HullRevision == hull.Revision
                && BoxStationHullRepair.ValidMaterials(_model.Layout, candidate, hull);
            _nextReadout = 0;
        }
        if (!_validState || portables == null || _model.PortableState != portables) state = null;
        var camera = LocalPlayer.MainCam;
        if (!_model.Visible || camera == null || !camera || !camera.isActiveAndEnabled
            || !interactive || !ClientSpacecraftRider.WorldInputAvailable || Cursor.visible)
        {
            Hide();
            return false;
        }
        var ray = new Ray(camera.transform.position, camera.transform.forward);
        var localEye = _model.Root.transform.InverseTransformPoint(ray.origin);
        int held = -1, source = -1, bundle = -1, yield = -1, target = -1, count = 0, cableTarget = -1, floorTarget = -1;
        int storage = -1;
        BoxStationMaterial material = default;
        BoxStationMaterialBundle carried = default;
        float nearest = ReadoutDistance;
        Vector3 targetContact = default;
        Vector3 cableContact = default;
        Vector3 floorContact = default;
        bool doorTarget = false, storedBundle = false;
        if (state is { } live)
        {
            foreach (var item in live.Bundles)
            {
                if (item.Place == BoxStationMaterialPlace.Held && holder != Guid.Empty && item.Holder == holder)
                {
                    held = item.Id;
                    carried = item;
                }
                if (item.Place != BoxStationMaterialPlace.Dropped || !Nearby(localEye, item.Position)
                    || !AimAt(ray, _model.MaterialBundlePoint(item.Id), .25f, ref nearest)) continue;
                bundle = item.Id; material = item.Material; count = item.Count;
            }
            for (int i = 0; i < _sources.Length; i++)
                if (live.Stocks[i] > 0 && Nearby(localEye, _sources[i].Position)
                    && AimAt(ray, _model.MaterialStockPoint(i), .25f, ref nearest))
                {
                    source = i; bundle = -1; material = _sources[i].Material; count = live.Stocks[i];
                }
            if (_model.TryMaterialYieldTarget(ray, nearest, out int structure, out var kind, out int amount, out var point)
                && ClearContact(ray.origin, point))
            {
                yield = structure; material = kind; count = amount;
                source = bundle = -1;
                nearest = Vector3.Distance(ray.origin, point);
            }
            if (_model.MaterialState == state
                && _model.TryStoredPortableTarget(ray, nearest, out var storedKind, out int stored, out var storedContact)
                && ClearContact(ray.origin, storedContact))
            {
                source = bundle = yield = -1;
                nearest = Vector3.Distance(ray.origin, storedContact);
                if (storedKind == SpacecraftHeldStationToolKind.Material)
                    foreach (var item in live.Bundles)
                        if (item.Id == stored && item.Place == BoxStationMaterialPlace.Stored)
                        {
                            bundle = stored; material = item.Material; count = item.Count;
                            storedBundle = true;
                            break;
                        }
            }
            if (_model.MaterialState == state && _model.TryStorageTarget(ray, nearest, out int container, out var handle)
                && ClearContact(ray.origin, handle))
            {
                storage = container;
                source = bundle = yield = -1;
                storedBundle = false;
                nearest = Vector3.Distance(ray.origin, handle);
            }
            if (held >= 0 && carried.Material == BoxStationMaterial.Steel && holder != Guid.Empty && Input.GetKeyDown(KeyCode.V))
            {
                _doorBuild = !_doorBuild;
                _nextReadout = 0;
            }
            if (held >= 0 && carried.Material == BoxStationMaterial.FloorTiles
                && _model.TryRepairTarget(ray, Reach, out int panel, out var panelContact)
                && ClearContact(ray.origin, panelContact))
            {
                target = panel;
                targetContact = panelContact;
            }
            if (held >= 0 && carried.Material == BoxStationMaterial.FloorTiles && _model.MaterialState == state
                && _model.TryFloorTarget(ray, true, out int cell, out var localFloorContact))
            {
                var worldContact = _model.Root.transform.TransformPoint(localFloorContact);
                float distance = Vector3.Distance(ray.origin, worldContact);
                float repairDistance = target >= 0 ? Vector3.Distance(ray.origin, targetContact) : Reach;
                if (float.IsFinite(distance) && distance <= repairDistance && ClearContact(ray.origin, worldContact))
                {
                    floorTarget = cell;
                    floorContact = localFloorContact;
                    target = -1;
                }
            }
            if (held >= 0 && carried.Material is not (BoxStationMaterial.FloorTiles or BoxStationMaterial.Cable or BoxStationMaterial.DoorElectronics)
                && (carried.Material != BoxStationMaterial.Steel || !_doorBuild)
                && _model.TryMaterialStructureTarget(ray, Reach, carried.Material, out int component, out var contact)
                && ClearContact(ray.origin, contact))
            {
                target = component;
                targetContact = contact;
            }
            if (held >= 0 && carried.Material is BoxStationMaterial.Steel or BoxStationMaterial.Plasteel
                && (carried.Material != BoxStationMaterial.Steel || !_doorBuild)
                && _model.TryConstructionMaterialTarget(ray, Reach, carried.Material, out int construction, out var constructionContact)
                && ClearContact(ray.origin, constructionContact)
                && BoxStationHullRepair.ResolveConstructionMaterial(_model.Layout,
                    BoxStationHullRepair.ConstructionAt(_model.Layout, live, construction), carried.Material, out _, out _, out _))
            {
                target = construction;
                targetContact = constructionContact;
            }
            if (held >= 0 && _model.MaterialState == state
                && (carried.Material is BoxStationMaterial.Cable or BoxStationMaterial.DoorElectronics
                    || carried.Material == BoxStationMaterial.Steel && _doorBuild)
                && _model.TryDoorConstructionTarget(localEye, _model.Root.transform.InverseTransformDirection(ray.direction),
                    carried.Material, out int door, out var localDoorContact))
            {
                var worldContact = _model.Root.transform.TransformPoint(localDoorContact);
                float distance = Vector3.Distance(ray.origin, worldContact);
                if (float.IsFinite(distance) && distance <= Reach && ClearContact(ray.origin, worldContact))
                {
                    target = door;
                    targetContact = worldContact;
                    doorTarget = true;
                }
            }
            if (held >= 0 && !doorTarget && carried.Material == BoxStationMaterial.Cable && _model.MaterialState == state
                && _model.TryCableTarget(ray, true, out int cableIndex, out var localContact))
            {
                var worldContact = _model.Root.transform.TransformPoint(localContact);
                float distance = Vector3.Distance(ray.origin, worldContact);
                if (float.IsFinite(distance) && distance <= Reach && ClearContact(ray.origin, worldContact))
                {
                    cableTarget = cableIndex;
                    cableContact = localContact;
                }
            }
        }
        if (storageOnly && (storage < 0 || Input.GetKeyDown(KeyCode.G) || Input.GetKeyDown(KeyCode.R)))
        {
            Hide();
            return false;
        }
        if (storage >= 0)
        {
            target = cableTarget = floorTarget = -1;
            doorTarget = false;
        }
        bool reinforce = held >= 0 && carried.Material is BoxStationMaterial.Glass or BoxStationMaterial.PlasmaGlass
            && bundle > 0 && !storedBundle && material == BoxStationMaterial.Rods && nearest <= Reach;
        bool canTake = held < 0 && holder != Guid.Empty && nearest <= Reach;
        bool canStorage = storage >= 0 && holder != Guid.Empty && nearest <= Reach && _model.StorageSettled(storage);
        bool visible = held >= 0 || source >= 0 || bundle >= 0 || yield >= 0 || storage >= 0;
        bool changed = held != _held || source != _source || bundle != _bundle || yield != _yield
            || target != _target || material != _yieldMaterial || cableTarget != _cableTarget || floorTarget != _floorTarget
            || doorTarget != _doorTarget || storage != _storage;
        _held = held; _source = source; _bundle = bundle; _yield = yield; _target = target; _yieldMaterial = material;
        _cableTarget = cableTarget;
        _floorTarget = floorTarget;
        _doorTarget = doorTarget;
        _storage = storage;
        if (_hud != null && _hud.activeSelf != visible) { _hud.SetActive(visible); changed = true; }
        if (visible && (changed || Time.unscaledTime >= _nextReadout))
        {
            _nextReadout = Time.unscaledTime + .1f;
            if (storage >= 0 && state != null)
                UpdateStorageHud(portables!.Value, storage, carried, held >= 0 || storageOnly, storageOnly, nearest <= Reach, feedback);
            else UpdateHud(held, carried, source, yield, material, count, canTake, target, reinforce, feedback);
        }
        if (state == null || holder == Guid.Empty) return false;
        if (storage >= 0 && nearest <= Reach && (Input.GetKeyDown(KeyCode.E)
            || (held >= 0 || storageOnly) && _model.StorageOpen(storage) && Input.GetKeyDown(KeyCode.F)))
        {
            if (!canStorage) return true;
            bool store = (held >= 0 || storageOnly) && _model.StorageOpen(storage) && Input.GetKeyDown(KeyCode.E);
            if (store && !BoxStationSystems.StorageHasRoom(_model.Layout, portables!.Value, storage)) return true;
            _command(store ? BoxStationMaterialControl.StoreStorage : BoxStationMaterialControl.ToggleStorage,
                storage, default, Vector3.zero);
            return true;
        }
        if (storageOnly) return true;
        if (canTake && (source >= 0 || bundle >= 0 || yield >= 0) && Input.GetKeyDown(KeyCode.E))
        {
            var action = yield >= 0 ? BoxStationMaterialControl.Recover
                : source >= 0 ? BoxStationMaterialControl.TakeStock : BoxStationMaterialControl.TakeBundle;
            _command(action, yield >= 0 ? yield : source >= 0 ? source : bundle,
                yield >= 0 ? material : default, Vector3.zero);
            return true;
        }
        if (held >= 0 && Input.GetKeyDown(KeyCode.G))
        {
            _command(BoxStationMaterialControl.Drop, 0, default, Vector3.zero);
            return true;
        }
        if (held >= 0 && carried.Material == BoxStationMaterial.Steel && Input.GetKeyDown(KeyCode.R))
        {
            _command(BoxStationMaterialControl.CraftTiles, 0, default, Vector3.zero);
            return true;
        }
        if (held >= 0 && doorTarget && !_clickAction && Input.GetMouseButton(0))
        {
            var contact = _model.Root.transform.InverseTransformPoint(targetContact);
            var intent = new BoxStationWorkIntent(-1, BoxStationWorkTarget.DoorConstruction, target, BoxStationWorkMode.Repair,
                new(contact.x, contact.y, contact.z));
            if (BoxStationEngineering.ValidIntent(_model.Layout, intent)) _work(intent);
            return true;
        }
        if (held >= 0 && carried.Material == BoxStationMaterial.Cable)
        {
            if (!_clickAction && cableTarget >= 0 && Input.GetMouseButtonDown(0))
            {
                _clickAction = true;
                _cable(cableTarget, true, cableContact);
            }
            return Input.GetMouseButton(0);
        }
        if (reinforce && Input.GetMouseButtonDown(0))
        {
            _command(BoxStationMaterialControl.Reinforce, bundle, default, Vector3.zero);
            return true;
        }
        if (_clickAction) return Input.GetMouseButton(0);
        if (held >= 0 && floorTarget >= 0 && Input.GetMouseButtonDown(0))
        {
            _clickAction = true;
            _floor(floorTarget, true, floorContact);
            return true;
        }
        if (held >= 0 && target >= 0 && !doorTarget && (carried.Material == BoxStationMaterial.FloorTiles
            ? Input.GetMouseButtonDown(0) : Input.GetMouseButton(0)))
        {
            var contact = _model.Root.transform.InverseTransformPoint(targetContact);
            if (float.IsFinite(contact.x) && float.IsFinite(contact.y) && float.IsFinite(contact.z))
            {
                if (carried.Material == BoxStationMaterial.FloorTiles)
                    _command(BoxStationMaterialControl.ReplacePanel, target, default, contact);
                else if (BoxStationHullRepair.TryConstruction(_model.Layout, target, out _))
                    _work(new(-1, BoxStationWorkTarget.WallConstruction, target, BoxStationWorkMode.Repair,
                        new(contact.x, contact.y, contact.z)));
                else if (MissingGrille(target))
                    _work(new(-1, BoxStationWorkTarget.Construction, target, BoxStationWorkMode.Repair,
                        new(contact.x, contact.y, contact.z)));
                else if (Input.GetMouseButtonDown(0)) _command(BoxStationMaterialControl.Rebuild, target, default, contact);
                return true;
            }
        }
        return held >= 0 && carried.Material == BoxStationMaterial.Steel && Input.GetKeyDown(KeyCode.V);
    }

    private bool MissingGrille(int target)
    {
        if (!BoxStationHull.TryStructure(_model.Layout, target, out var structure) || structure.Kind != BoxStationStructureKind.Grille) return false;
        foreach (var panel in _model.HullState.Panels)
            if (panel.Target == target) return panel.Integrity == 0;
        return false;
    }

    private static bool Nearby(Vector3 eye, System.Numerics.Vector3 position)
        => (eye - new Vector3(position.X, position.Y, position.Z)).sqrMagnitude
            <= (ReadoutDistance + .4f) * (ReadoutDistance + .4f);

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

    private void UpdateHud(int held, BoxStationMaterialBundle carried, int source, int yield,
        BoxStationMaterial material, int count, bool canTake, int target, bool reinforce, string feedback)
    {
        _feedback.text = feedback;
        _status.color = Ink;
        _detail.text = "";
        _hint.rectTransform.anchoredPosition = new(0, -27);
        _feedback.rectTransform.anchoredPosition = new(0, -55);
        if (held >= 0)
        {
            _status.text = BoxStationHullRepair.MaterialName(carried.Material).ToUpperInvariant() + " — "
                + carried.Count.ToString(CultureInfo.InvariantCulture) + " IN HAND";
            if (carried.Material == BoxStationMaterial.Steel)
                _status.text += _doorBuild ? " — BUILD DOOR" : " — BUILD STRUCTURE";
            string craftHint = carried.Material == BoxStationMaterial.Steel ? "V: build type   R: craft 4 tiles   " : "";
            _hint.text = craftHint + "G: drop";
            int constructionCost;
            if (!reinforce && target >= 0 && _checkedState is { } stock
                && (_doorTarget ? BoxStationHullRepair.ResolveDoorMaterial(_model.Layout,
                    BoxStationHullRepair.DoorAt(_model.Layout, stock.DoorConstruction, target), carried.Material,
                    out _, out constructionCost, out _)
                    : BoxStationHullRepair.TryConstruction(_model.Layout, target, out _)
                        && BoxStationHullRepair.ResolveConstructionMaterial(_model.Layout,
                            BoxStationHullRepair.ConstructionAt(_model.Layout, stock, target), carried.Material,
                            out _, out constructionCost, out _)))
            {
                _detail.text = (_doorTarget ? "DOOR ASSEMBLY — " : "CONSTRUCTION — ")
                    + constructionCost.ToString(CultureInfo.InvariantCulture) + " "
                    + BoxStationHullRepair.MaterialName(carried.Material).ToUpperInvariant();
                if (_model.EngineeringState is { } engineering && engineering.HullRevision == _model.HullState.Revision
                    && BoxStationEngineering.ValidState(_model.Layout, engineering, _model.HullState, stock))
                    foreach (var job in engineering.Work)
                        if (job.Holder == _holder && job.Intent.Kind == (_doorTarget
                                ? BoxStationWorkTarget.DoorConstruction : BoxStationWorkTarget.WallConstruction)
                            && job.Intent.Tool == -1 && job.Intent.Target == target)
                        {
                            _detail.text += " — " + Mathf.RoundToInt(Mathf.Clamp01(job.Elapsed / job.Duration) * 100)
                                .ToString(CultureInfo.InvariantCulture) + "%";
                            break;
                        }
                _detail.color = carried.Count >= constructionCost ? Muted : Amber;
                _hint.rectTransform.anchoredPosition = new(0, -53);
                _feedback.rectTransform.anchoredPosition = new(0, -81);
                _hint.text = (carried.Count >= constructionCost ? "Hold LMB: construct   " : "INSUFFICIENT MATERIAL   ")
                    + craftHint + "G: drop";
                return;
            }
            if (carried.Material == BoxStationMaterial.Cable)
            {
                if (_cableTarget >= 0)
                {
                    _detail.text = "CABLE ROUTE — 1 LENGTH";
                    _detail.color = Muted;
                    _hint.rectTransform.anchoredPosition = new(0, -53);
                    _feedback.rectTransform.anchoredPosition = new(0, -81);
                    _hint.text = "LMB: lay cable   G: drop";
                }
                return;
            }
            if (carried.Material == BoxStationMaterial.FloorTiles)
            {
                if (_floorTarget >= 0)
                {
                    _detail.text = "EXPOSED PLATING — 1 TILE";
                    _detail.color = Muted;
                    _hint.rectTransform.anchoredPosition = new(0, -53);
                    _feedback.rectTransform.anchoredPosition = new(0, -81);
                    _hint.text = "LMB: lay tile   G: drop tiles";
                }
                else if (target >= 0)
                {
                    byte integrity = BoxStationHull.Capacity;
                    bool foam = false;
                    foreach (var panel in _model.HullState.Panels)
                        if (panel.Target == target) { integrity = panel.Integrity; foam = panel.Foam; break; }
                    _detail.text = ((target & 1) == 0 ? "Floor" : "Roof") + " integrity "
                        + integrity.ToString(CultureInfo.InvariantCulture) + "%" + (foam ? " — foam seal" : " — damaged")
                        + "   1 TILE";
                    _detail.color = Muted;
                    _hint.rectTransform.anchoredPosition = new(0, -53);
                    _feedback.rectTransform.anchoredPosition = new(0, -81);
                    _hint.text = "LMB: replace panel   G: drop tiles";
                }
                else _hint.text = "G: drop tiles";
                return;
            }
            if (reinforce) { _hint.text = "LMB: reinforce   G: drop"; return; }
            if (target >= 0 && BoxStationHull.TryStructure(_model.Layout, target, out var structure))
            {
                byte integrity = structure.InitialIntegrity;
                foreach (var panel in _model.HullState.Panels)
                    if (panel.Target == target) { integrity = panel.Integrity; break; }
                int cost = BoxStationHullRepair.MaterialCost(_model.Layout, target, integrity);
                _detail.text = (structure.Kind == BoxStationStructureKind.Window ? "WINDOW" : "GRILLE") + " — "
                    + cost.ToString(CultureInfo.InvariantCulture) + " " + BoxStationHullRepair.MaterialName(carried.Material).ToUpperInvariant();
                _detail.color = carried.Count >= cost ? Muted : Amber;
                _hint.rectTransform.anchoredPosition = new(0, -53);
                _feedback.rectTransform.anchoredPosition = new(0, -81);
                _hint.text = carried.Count >= cost ? MissingGrille(target) ? "Hold LMB: fit grille   G: drop" : "LMB: rebuild   G: drop"
                    : "INSUFFICIENT MATERIAL   G: drop";
            }
            return;
        }
        _status.text = BoxStationHullRepair.MaterialName(material).ToUpperInvariant() + " — "
            + count.ToString(CultureInfo.InvariantCulture) + (yield >= 0 ? " RECOVERABLE" : source >= 0 ? " IN STOCK" : " IN BUNDLE");
        _hint.text = canTake ? yield >= 0 ? "E: recover" : "E: take" : "OUT OF REACH";
    }

    private void UpdateStorageHud(BoxStationPortableState state, int storage, BoxStationMaterialBundle carried,
        bool holding, bool equipment, bool withinReach, string feedback)
    {
        var definition = _model.StorageDefinitions[storage];
        int occupied = 0;
        foreach (var item in state.Materials.Bundles)
            if (item.Id > 0 && item.Place == BoxStationMaterialPlace.Stored && item.Storage == storage) occupied++;
        foreach (var item in state.Engineering.Tools)
            if (item.Place == BoxStationToolPlace.Stored && item.Storage == storage) occupied++;
        foreach (var item in state.Firefighting.Items)
            if (item.Place == BoxStationExtinguisherPlace.Stored && item.Storage == storage) occupied++;
        foreach (var item in state.Foam.Items)
            if (item.Place == BoxStationFoamPlace.Stored && item.Storage == storage) occupied++;
        foreach (var item in state.Repair.Packs)
            if (item.Place == BoxStationRepairPlace.Stored && item.Storage == storage) occupied++;
        int capacity = BoxStationStorage.Capacity(definition);
        _status.color = Ink;
        _status.text = definition.Name.ToUpperInvariant() + " — " + occupied.ToString(CultureInfo.InvariantCulture)
            + " / " + capacity.ToString(CultureInfo.InvariantCulture) + " ITEMS";
        _detail.color = Muted;
        _detail.text = equipment ? "EQUIPMENT IN HAND" : holding ? BoxStationHullRepair.MaterialName(carried.Material).ToUpperInvariant() + " — "
            + carried.Count.ToString(CultureInfo.InvariantCulture) + " IN HAND" : "";
        _hint.rectTransform.anchoredPosition = new(0, holding ? -53 : -27);
        _feedback.rectTransform.anchoredPosition = new(0, holding ? -81 : -55);
        _feedback.text = feedback;
        if (!withinReach) _hint.text = "OUT OF REACH";
        else if (!_model.StorageSettled(storage)) _hint.text = state.Materials.StorageOpen[storage] ? "OPENING" : "CLOSING";
        else if (!_model.StorageOpen(storage)) _hint.text = "E: open";
        else if (!holding) _hint.text = "E: close";
        else _hint.text = (occupied < capacity ? "E: store   " : "FULL   ") + "F: close";
        if (holding) _hint.text += "   G: drop";
    }

    private void CreateHud()
    {
        _hud = new GameObject("Material readout");
        var canvas = _hud.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 117;
        var scaler = _hud.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new(1280, 720);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
        var panel = new GameObject("Material status").AddComponent<RectTransform>();
        panel.SetParent(_hud.transform, false);
        panel.anchorMin = panel.anchorMax = panel.pivot = new(.5f, 0);
        panel.anchoredPosition = new(0, 266);
        panel.sizeDelta = new(900, 114);
        _status = Label(panel, "Stock and state", 0, 24, 18);
        _detail = Label(panel, "Rebuild cost", 27, 22, 15);
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
        _held = _source = _bundle = _yield = _target = _cableTarget = _floorTarget = _storage = -1;
        if (!Input.GetMouseButton(0)) _clickAction = false;
        _nextReadout = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Hide();
        _disposed = true;
        if (_hud != null && _hud) { _hud.SetActive(false); UnityEngine.Object.Destroy(_hud); }
        _hud = null;
        _typography.Dispose();
    }
}
