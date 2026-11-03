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

internal sealed class BoxStationHullRepairControls : IDisposable
{
    private const float Reach = 2.4f, ReadoutDistance = 6;
    private static readonly Color Ink = new(.92f, .96f, .97f, 1), Muted = new(.61f, .69f, .72f, 1),
        Amber = new(1, .72f, .28f, 1);
    private readonly BoxStationModel _model;
    private readonly Action<BoxStationRepairControl, int, Vector3> _command;
    private readonly ImmutableArray<BoxStationRepairSource> _sources;
    private readonly ClientTabletTypography _typography = new();
    private readonly Il2CppStructArray<RaycastHit> _hits = new(size: 32);
    private BoxStationRepairState? _checkedState;
    private bool _validState, _disposed;
    private GameObject? _hud;
    private TextMeshProUGUI _status = null!, _detail = null!, _hint = null!, _feedback = null!;
    private float _nextReadout;
    private int _held = -1, _source = -1, _pack = -1, _target = -1;

    internal BoxStationHullRepairControls(BoxStationModel model, Action<BoxStationRepairControl, int, Vector3> command)
    {
        _model = model;
        _command = command;
        _sources = BoxStationHullRepair.GetSources(model.Layout);
        try { CreateHud(); }
        catch { Dispose(); throw; }
    }

    // The station owner supplies snapshot freshness and authenticates the holder lease.
    internal bool Update(BoxStationRepairState? state, Guid holder, bool interactive, string feedback)
    {
        if (_disposed) return false;
        if (_checkedState != state)
        {
            _checkedState = state;
            _validState = state is { } candidate && BoxStationHullRepair.ValidState(_model.Layout, candidate);
            _nextReadout = 0;
        }
        if (!_validState || _model.PortableState is not { } projected || projected.Repair != state) state = null;
        var camera = LocalPlayer.MainCam;
        if (!_model.Visible || camera == null || !camera || !camera.isActiveAndEnabled
            || !interactive || !ClientSpacecraftRider.WorldInputAvailable || Cursor.visible)
        {
            Hide();
            return false;
        }
        var ray = new Ray(camera.transform.position, camera.transform.forward);
        var localEye = _model.Root.transform.InverseTransformPoint(ray.origin);
        int held = -1, source = -1, pack = -1, target = -1;
        float nearest = ReadoutDistance, targetDistance = 0;
        Vector3 targetContact = default;
        if (state is { } live)
        {
            for (int i = 0; i < live.Packs.Length; i++)
            {
                var item = live.Packs[i];
                if (item.Place == BoxStationRepairPlace.Held && holder != Guid.Empty && item.Holder == holder) held = i;
                if (item.Place is not (BoxStationRepairPlace.Source or BoxStationRepairPlace.Dropped)
                    || !Nearby(localEye, item.Position)) continue;
                if (AimAt(ray, _model.RepairPackPoint(i), .24f, ref nearest)) pack = i;
            }
            for (int i = 0; i < _sources.Length; i++)
                if (live.Sheets[i] > 0 && Nearby(localEye, _sources[i].Position)
                    && AimAt(ray, _model.RepairStockPoint(i), .30f, ref nearest))
                {
                    source = i;
                    pack = -1;
                }
            if (_model.TryStoredPortableTarget(ray, nearest, out var storedKind, out int stored, out var storedContact)
                && ClearContact(ray.origin, storedContact))
            {
                pack = storedKind == SpacecraftHeldStationToolKind.RepairTiles ? stored : -1;
                source = -1;
                nearest = Vector3.Distance(ray.origin, storedContact);
            }
            if (held >= 0 && _model.TryRepairTarget(ray, Reach, out int panel, out var contact)
                && ClearContact(ray.origin, contact))
            {
                target = panel;
                targetContact = contact;
                targetDistance = Vector3.Distance(ray.origin, contact);
            }
        }
        bool canTake = held < 0 && holder != Guid.Empty && nearest <= Reach;
        bool visible = held >= 0 || source >= 0 || pack >= 0;
        bool changed = held != _held || source != _source || pack != _pack || target != _target;
        _held = held; _source = source; _pack = pack; _target = target;
        if (_hud != null && _hud.activeSelf != visible) { _hud.SetActive(visible); changed = true; }
        if (visible && (changed || Time.unscaledTime >= _nextReadout))
        {
            _nextReadout = Time.unscaledTime + .1f;
            UpdateHud(state, held, source, pack, canTake, target, targetDistance, nearest, feedback);
        }
        if (state == null || holder == Guid.Empty) return false;
        if (canTake && source >= 0 && Input.GetKeyDown(KeyCode.R))
        {
            _command(BoxStationRepairControl.TakeSheets, source, Vector3.zero);
            return true;
        }
        if (canTake && (source >= 0 || pack >= 0) && Input.GetKeyDown(KeyCode.E))
        {
            _command(source >= 0 ? BoxStationRepairControl.Craft : BoxStationRepairControl.Take,
                source >= 0 ? source : pack, Vector3.zero);
            return true;
        }
        if (held >= 0 && Input.GetKeyDown(KeyCode.G))
        {
            _command(BoxStationRepairControl.Drop, 0, Vector3.zero);
            return true;
        }
        if (held >= 0 && target >= 0 && Input.GetMouseButtonDown(0))
        {
            var contact = _model.Root.transform.InverseTransformPoint(targetContact);
            if (float.IsFinite(contact.x) && float.IsFinite(contact.y) && float.IsFinite(contact.z))
            {
                _command(BoxStationRepairControl.Replace, target, contact);
                return true;
            }
        }
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
        if (count == _hits.Length) return false;
        var player = LocalPlayer.Transform;
        for (int i = 0; i < count; i++)
        {
            var collider = _hits[i].collider;
            if (collider != null && (player == null || !collider.transform.IsChildOf(player))) return false;
        }
        return true;
    }

    private void UpdateHud(BoxStationRepairState? state, int held, int source, int pack, bool canTake,
        int target, float targetDistance, float itemDistance, string feedback)
    {
        _feedback.text = feedback;
        _status.color = Ink;
        _detail.text = "";
        _hint.rectTransform.anchoredPosition = new(0, -27);
        _feedback.rectTransform.anchoredPosition = new(0, -55);
        if (state is not { } live)
        {
            _status.text = "HULL REPAIR — HOST OFFLINE";
            _status.color = Amber;
            _hint.text = "Material counts unavailable";
            return;
        }
        if (held >= 0)
        {
            _status.text = "HULL REPAIR — " + live.Packs[held].Tiles.ToString(CultureInfo.InvariantCulture) + " TILES IN HAND";
            if (target >= 0)
            {
                byte integrity = BoxStationHull.Capacity;
                bool foam = false;
                foreach (var panel in _model.HullState.Panels)
                    if (panel.Target == target) { integrity = panel.Integrity; foam = panel.Foam; break; }
                _detail.text = ((target & 1) == 0 ? "Floor" : "Roof") + " integrity "
                    + integrity.ToString(CultureInfo.InvariantCulture) + "%" + (foam ? " — foam seal" : " — damaged")
                    + "   1 TILE";
                _hint.rectTransform.anchoredPosition = new(0, -53);
                _feedback.rectTransform.anchoredPosition = new(0, -81);
                _hint.text = "LMB: replace panel (" + targetDistance.ToString("F1", CultureInfo.InvariantCulture) + " m)   G: drop tiles";
            }
            else _hint.text = "Aim at a damaged or foam floor / roof within 2.4 m   G: drop tiles";
        }
        else if (source >= 0)
        {
            _status.text = "METAL STOCK — " + live.Sheets[source].ToString(CultureInfo.InvariantCulture) + " SHEETS";
            _hint.text = (canTake ? "E: craft 4 tiles   R: take sheets   " : "Move within 2.4 m   ")
                + itemDistance.ToString("F1", CultureInfo.InvariantCulture) + " m";
        }
        else if (pack >= 0)
        {
            _status.text = "REPAIR BUNDLE — " + live.Packs[pack].Tiles.ToString(CultureInfo.InvariantCulture) + " TILES";
            _hint.text = (canTake ? "E: take tiles   " : "Move within 2.4 m to take   ")
                + itemDistance.ToString("F1", CultureInfo.InvariantCulture) + " m";
        }
    }

    private void CreateHud()
    {
        _hud = new GameObject("Hull repair readout");
        var canvas = _hud.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 117;
        var scaler = _hud.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new(1280, 720);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
        var panel = new GameObject("Hull repair status").AddComponent<RectTransform>();
        panel.SetParent(_hud.transform, false);
        panel.anchorMin = panel.anchorMax = panel.pivot = new(.5f, 0);
        panel.anchoredPosition = new(0, 266);
        panel.sizeDelta = new(900, 114);
        _status = Label(panel, "Stock and state", 0, 24, 18);
        _detail = Label(panel, "Materials and cost", 27, 22, 15);
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
        _held = _source = _pack = _target = -1;
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
