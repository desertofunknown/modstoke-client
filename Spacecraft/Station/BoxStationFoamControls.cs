// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Globalization;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using TheForest.Utils;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Modstoke.Client;

internal sealed class BoxStationFoamControls : IDisposable
{
    private const float Reach = 2.4f, ReadoutDistance = 6;
    private static readonly Color Ink = new(.92f, .96f, .97f, 1), Muted = new(.61f, .69f, .72f, 1),
        Amber = new(1, .72f, .28f, 1);
    private readonly BoxStationModel _model;
    private readonly Action<BoxStationFoamControl, int, Vector3> _command;
    private readonly ClientTabletTypography _typography = new();
    private readonly Il2CppStructArray<RaycastHit> _hits = new(size: 32);
    private readonly Vector3 _rackPoint;
    private BoxStationFoamState? _checkedState;
    private bool _validState, _disposed;
    private GameObject? _hud;
    private TextMeshProUGUI _status = null!, _hint = null!, _feedback = null!;
    private float _nextReadout;
    private int _held = -1, _focused = -1, _target = -1;
    private bool _rackFocused;

    internal BoxStationFoamControls(BoxStationModel model, Action<BoxStationFoamControl, int, Vector3> command)
    {
        _model = model;
        _command = command;
        var items = BoxStationFoam.GetItems(model.Layout);
        foreach (var item in items) _rackPoint += new Vector3(item.Position.X, item.Position.Y, item.Position.Z);
        if (items.Length > 0) _rackPoint = _rackPoint / items.Length + BoxStationModel.FoamCarryGrip;
        try { CreateHud(); }
        catch { Dispose(); throw; }
    }

    // Freshness and the authenticated holder lease are supplied by the station owner.
    internal bool Update(BoxStationFoamState? state, Guid holder, bool interactive, string feedback)
    {
        if (_disposed) return false;
        if (_checkedState != state)
        {
            _checkedState = state;
            _validState = state is { } candidate && BoxStationFoam.ValidState(_model.Layout, candidate);
            _nextReadout = 0;
        }
        if (!_validState || _model.PortableState is not { } projected || projected.Foam != state) state = null;
        var camera = LocalPlayer.MainCam;
        if (!_model.Visible || camera == null || !camera || !camera.isActiveAndEnabled
            || !interactive || !ClientSpacecraftRider.WorldInputAvailable || Cursor.visible)
        {
            Hide();
            return false;
        }
        var ray = new Ray(camera.transform.position, camera.transform.forward);
        int held = -1, focused = -1, target = -1;
        float nearest = ReadoutDistance, targetDistance = 0;
        Vector3 targetContact = default;
        bool canTake = false;
        if (state is { } live)
        {
            for (int i = 0; i < live.Items.Length; i++)
                if (live.Items[i].Place == BoxStationFoamPlace.Held && holder != Guid.Empty
                    && live.Items[i].Holder == holder) { held = i; break; }
            for (int i = 0; i < live.Items.Length; i++)
                if (live.Items[i].Place is not (BoxStationFoamPlace.Held or BoxStationFoamPlace.Stored)
                    && AimAt(ray, _model.FoamControlPoint(i), .13f, ref nearest)) focused = i;
            if (_model.TryStoredPortableTarget(ray, nearest, out var storedKind, out int stored, out var storedContact)
                && ClearContact(ray.origin, storedContact))
            {
                focused = storedKind == SpacecraftHeldStationToolKind.Foam ? stored : -1;
                nearest = Vector3.Distance(ray.origin, storedContact);
            }
            if (focused >= 0)
                canTake = held < 0 && holder != Guid.Empty && nearest <= Reach
                    && live.Items[focused].Place is BoxStationFoamPlace.Rack or BoxStationFoamPlace.Dropped or BoxStationFoamPlace.Stored;
            if (held >= 0 && _model.TryFoamTarget(ray, Reach, out int aperture, out var contact)
                && ClearContact(ray.origin, contact))
            {
                target = aperture;
                targetContact = contact;
                targetDistance = Vector3.Distance(ray.origin, contact);
            }
        }
        float rackDistance = ReadoutDistance;
        bool rackFocused = AimAt(ray, _model.Root.transform.TransformPoint(_rackPoint), .38f, ref rackDistance);
        bool visible = held >= 0 || focused >= 0 || rackFocused;
        bool changed = held != _held || focused != _focused || target != _target || rackFocused != _rackFocused;
        _held = held; _focused = focused; _target = target; _rackFocused = rackFocused;
        if (_hud != null && _hud.activeSelf != visible) { _hud.SetActive(visible); changed = true; }
        if (visible && (changed || Time.unscaledTime >= _nextReadout))
        {
            _nextReadout = Time.unscaledTime + .1f;
            UpdateHud(state, held, focused, canTake, target, targetDistance, nearest, feedback);
        }
        if (state == null || holder == Guid.Empty) return false;
        if (canTake && Input.GetKeyDown(KeyCode.E))
        {
            _command(BoxStationFoamControl.Take, focused, Vector3.zero);
            return true;
        }
        if (held >= 0 && Input.GetKeyDown(KeyCode.G))
        {
            _command(BoxStationFoamControl.Drop, 0, Vector3.zero);
            return true;
        }
        if (held >= 0 && target >= 0 && Input.GetMouseButtonDown(0))
        {
            var contact = _model.Root.transform.InverseTransformPoint(targetContact);
            if (float.IsFinite(contact.x) && float.IsFinite(contact.y) && float.IsFinite(contact.z))
            {
                _command(BoxStationFoamControl.Deploy, target, contact);
                return true;
            }
        }
        return false;
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
        if (count == _hits.Length) return false;
        var player = LocalPlayer.Transform;
        for (int i = 0; i < count; i++)
        {
            var collider = _hits[i].collider;
            if (collider != null && (player == null || !collider.transform.IsChildOf(player))) return false;
        }
        return true;
    }

    private void UpdateHud(BoxStationFoamState? state, int held, int focused, bool canTake, int target,
        float targetDistance, float itemDistance, string feedback)
    {
        _feedback.text = feedback;
        if (state is not { } live)
        {
            _status.text = "HULL SEAL — HOST OFFLINE";
            _status.color = Amber;
            _hint.text = "Stock and controls unavailable";
            return;
        }
        int unused = 0, rack = 0;
        foreach (var item in live.Items)
        {
            if (item.Place == BoxStationFoamPlace.Rack) rack++;
            if (item.Place is BoxStationFoamPlace.Rack or BoxStationFoamPlace.Dropped or BoxStationFoamPlace.Held
                or BoxStationFoamPlace.Stored) unused++;
        }
        string stock = "UNUSED " + unused.ToString(CultureInfo.InvariantCulture) + " / "
            + live.Items.Length.ToString(CultureInfo.InvariantCulture);
        _status.color = unused == 0 ? Amber : Ink;
        if (held >= 0)
        {
            _status.text = "HULL SEAL — SINGLE USE   " + stock;
            _hint.text = target >= 0 ? "LMB: attach at breach (" + targetDistance.ToString("F1", CultureInfo.InvariantCulture)
                + " m)   G: drop"
                : "Aim at a broken deck or roof within 2.4 m   G: drop";
        }
        else if (focused >= 0)
        {
            var item = live.Items[focused];
            string distance = itemDistance.ToString("F1", CultureInfo.InvariantCulture) + " m";
            if (item.Place == BoxStationFoamPlace.Armed)
            {
                _status.text = "HULL SEAL — FUSE " + item.RemainingSeconds.ToString("F1", CultureInfo.InvariantCulture) + "s   " + stock;
                _status.color = Amber;
                _hint.text = "Attached at breach   " + distance;
            }
            else if (item.Place == BoxStationFoamPlace.Foaming)
            {
                _status.text = "HULL SEAL — HARDENING " + item.RemainingSeconds.ToString("F1", CultureInfo.InvariantCulture) + "s   " + stock;
                _hint.text = "Foam spreading   " + distance;
            }
            else if (item.Place == BoxStationFoamPlace.Spent)
            {
                _status.text = "HULL SEAL — SPENT   " + stock;
                _hint.text = "Single-use canister depleted   " + distance;
            }
            else
            {
                _status.text = "HULL SEAL — READY   " + stock;
                _hint.text = (canTake ? "E: take canister   " : "Move within 2.4 m to take   ") + distance;
            }
        }
        else
        {
            _status.text = unused == 0 ? "HULL SEAL — DEPLETED" : "HULL SEAL RACK   " + stock;
            _hint.text = rack == 0 ? "No canisters on rack" : rack.ToString(CultureInfo.InvariantCulture) + " on rack   Aim at a canister to take";
        }
    }

    private void CreateHud()
    {
        _hud = new GameObject("Hull foam readout");
        var canvas = _hud.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 116;
        var scaler = _hud.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new(1280, 720);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
        var panel = new GameObject("Hull foam status").AddComponent<RectTransform>();
        panel.SetParent(_hud.transform, false);
        panel.anchorMin = panel.anchorMax = panel.pivot = new(.5f, 0);
        panel.anchoredPosition = new(0, 158);
        panel.sizeDelta = new(760, 92);
        _status = Label(panel, "Stock and state", 0, 24, 18);
        _hint = Label(panel, "Controls", 29, 22, 15);
        _feedback = Label(panel, "Host response", 57, 28, 14);
        _hint.color = Muted;
        _feedback.color = Amber;
        _hud.SetActive(false);
    }

    private TextMeshProUGUI Label(Transform parent, string name, float y, float height, float size)
    {
        var rect = new GameObject(name).AddComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.anchorMin = rect.anchorMax = rect.pivot = new(0, 1);
        rect.anchoredPosition = new(0, -y);
        rect.sizeDelta = new(760, height);
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
        _held = _focused = _target = -1;
        _rackFocused = false;
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
