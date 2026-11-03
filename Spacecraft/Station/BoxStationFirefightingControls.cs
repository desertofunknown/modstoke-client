// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using TheForest.Utils;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace Modstoke.Client;

internal sealed class BoxStationFirefightingControls : IDisposable
{
    private const int MaximumPads = 4;
    private const float NearbyDistance = 6, Reach = 2.4f;
    private static readonly Color Ink = new(.92f, .96f, .97f, 1), Muted = new(.61f, .69f, .72f, 1),
        Amber = new(1, .72f, .28f, 1);
    private readonly BoxStationModel _model;
    private readonly Action<BoxStationFirefightingControl, int, Vector3> _command;
    private readonly ImmutableArray<BoxStationWaterTank> _tanks;
    private readonly int _cabinetCount;
    private readonly List<Pad> _pads = new(MaximumPads);
    private readonly int[] _nearest = new int[MaximumPads];
    private readonly float[] _distances = new float[MaximumPads];
    private readonly Dictionary<int, Material> _fonts = new();
    private readonly ClientTabletTypography _typography = new();
    private readonly Il2CppStructArray<RaycastHit> _hits = new(size: 32);
    private BoxStationFirefightingState? _checkedState;
    private bool _validState, _disposed;
    private GameObject? _hud;
    private TextMeshProUGUI _status = null!, _hint = null!, _feedback = null!;
    private float _nextNearby, _nextReadout, _nextSpray;
    private int _focused = -1, _toggle = -1, _held = -1;
    private BoxStationFirefightingControl _focusedControl;

    internal BoxStationFirefightingControls(BoxStationModel model,
        Action<BoxStationFirefightingControl, int, Vector3> command)
    {
        _model = model;
        _command = command;
        _tanks = BoxStationFirefighting.GetTanks(model.Layout);
        _cabinetCount = BoxStationFirefighting.GetCabinets(model.Layout).Length;
        try { CreateHud(); }
        catch { Dispose(); throw; }
    }

    // The owner supplies a fresh snapshot and the current authenticated station holder lease.
    internal bool Update(BoxStationFirefightingState? state, Guid localHolder, bool interactive, string feedback)
    {
        if (_disposed) return false;
        if (_checkedState != state)
        {
            _checkedState = state;
            _validState = state is { } candidate && BoxStationFirefighting.ValidState(_model.Layout, candidate);
            _nextReadout = 0;
        }
        if (!_validState || _model.PortableState is not { } projected || projected.Firefighting != state) state = null;
        var camera = LocalPlayer.MainCam;
        bool visible = _model.Visible && camera != null && camera && camera.isActiveAndEnabled;
        bool acceptsInput = visible && interactive && state != null && localHolder != Guid.Empty
            && ClientSpacecraftRider.WorldInputAvailable && !Cursor.visible;
        if (visible && Time.unscaledTime >= _nextNearby)
        {
            _nextNearby = Time.unscaledTime + .2f;
            UpdateNearby(camera!.transform.position);
        }
        if (!visible)
        {
            foreach (var pad in _pads) pad.Frame.gameObject.SetActive(false);
            _nextNearby = 0;
        }
        int held = -1, focused = -1, toggle = -1;
        var control = BoxStationFirefightingControl.TakeCabinet;
        if (acceptsInput)
        {
            var snapshot = state!.Value;
            for (int i = 0; i < snapshot.Items.Length; i++)
                if (snapshot.Items[i].Place == BoxStationExtinguisherPlace.Held
                    && snapshot.Items[i].Holder == localHolder) { held = i; break; }
            FindTarget(camera!, snapshot, held, out control, out focused, out toggle);
        }
        bool changed = held != _held || focused != _focused || toggle != _toggle || control != _focusedControl;
        _held = held; _focused = focused; _toggle = toggle; _focusedControl = control;
        bool hudVisible = acceptsInput && (held >= 0 || focused >= 0 || toggle >= 0);
        if (_hud != null && _hud.activeSelf != hudVisible) { _hud.SetActive(hudVisible); changed = true; }
        if (Time.unscaledTime >= _nextReadout || changed)
        {
            _nextReadout = Time.unscaledTime + .1f;
            foreach (var pad in _pads) if (pad.Active) UpdateReadout(pad, state);
            if (hudVisible) UpdateHud(state!.Value, held, control, focused, toggle, feedback);
        }
        if (!acceptsInput) return false;
        if (Input.GetKeyDown(KeyCode.F) && toggle >= 0)
            _command(BoxStationFirefightingControl.ToggleCabinet, toggle, Vector3.zero);
        else if (Input.GetKeyDown(KeyCode.E) && focused >= 0)
            _command(control, focused, Vector3.zero);
        else if (held >= 0 && Input.GetKeyDown(KeyCode.R))
            _command(BoxStationFirefightingControl.Safety, 0, Vector3.zero);
        else if (held >= 0 && Input.GetKeyDown(KeyCode.G))
            _command(BoxStationFirefightingControl.Drop, 0, Vector3.zero);
        else if (held >= 0 && Input.GetMouseButton(0) && Time.unscaledTime >= _nextSpray)
        {
            var item = state!.Value.Items[held];
            if (!item.Safety && item.Water >= 1 && item.CooldownSeconds <= 0)
            {
                var aim = _model.Root.transform.InverseTransformDirection(camera!.transform.forward);
                float length = aim.sqrMagnitude;
                if (float.IsFinite(length) && length > .99f && length < 1.01f)
                {
                    // Throttle requests only; the snapshot remains the sole water/cooldown readout.
                    _nextSpray = Time.unscaledTime + BoxStationFirefighting.SprayCooldownSeconds;
                    _command(BoxStationFirefightingControl.Spray, 0, aim.normalized);
                }
            }
        }
        return focused >= 0 && Input.GetKeyDown(KeyCode.E) || toggle >= 0 && Input.GetKeyDown(KeyCode.F)
            || held >= 0 && (Input.GetKeyDown(KeyCode.R) || Input.GetKeyDown(KeyCode.G) || Input.GetMouseButton(0));
    }

    private void FindTarget(Camera camera, BoxStationFirefightingState state, int held,
        out BoxStationFirefightingControl control, out int target, out int toggle)
    {
        control = BoxStationFirefightingControl.TakeCabinet;
        target = toggle = -1;
        float nearest = Reach, nearestToggle = Reach;
        var ray = new Ray(camera.transform.position, camera.transform.forward);
        foreach (var pad in _pads)
        {
            if (!pad.Active) continue;
            int index = pad.Fixture;
            if (index < _cabinetCount)
            {
                if (AimAt(ray, BoxStationFirefightingControl.ToggleCabinet, index, .09f, ref nearestToggle)) toggle = index;
                int stored = CabinetItem(state, index);
                var action = held < 0 ? BoxStationFirefightingControl.TakeCabinet : BoxStationFirefightingControl.StoreCabinet;
                if ((held < 0 ? stored >= 0 : stored < 0 && state.CabinetOpen[index])
                    && AimAt(ray, action, index, .16f, ref nearest)) { control = action; target = index; }
            }
            else if (held >= 0 && state.TankWater[index - _cabinetCount] > 0
                && !SpacecraftShoreCoolant.IsSupply(_tanks[index - _cabinetCount])
                && state.Items[held].Water < BoxStationFirefighting.ExtinguisherCapacity
                && AimAt(ray, BoxStationFirefightingControl.Refill, index - _cabinetCount, .12f, ref nearest))
            { control = BoxStationFirefightingControl.Refill; target = index - _cabinetCount; }
        }
        if (held >= 0) return;
        for (int i = 0; i < state.Items.Length; i++)
            if (state.Items[i].Place == BoxStationExtinguisherPlace.Dropped
                && AimAt(ray, BoxStationFirefightingControl.TakeDropped, i, .15f, ref nearest))
            { control = BoxStationFirefightingControl.TakeDropped; target = i; }
        if (_model.TryStoredPortableTarget(ray, nearest, out var storedKind, out int storedItem, out var contact)
            && AimAt(ray, contact, .15f, ref nearest))
        {
            control = BoxStationFirefightingControl.TakeDropped;
            target = storedKind == SpacecraftHeldStationToolKind.Extinguisher ? storedItem : -1;
        }
    }

    private bool AimAt(Ray ray, BoxStationFirefightingControl control, int target, float radius, ref float nearest)
        => AimAt(ray, _model.FirefightingControlPoint(control, target), radius, ref nearest);

    private bool AimAt(Ray ray, Vector3 point, float radius, ref float nearest)
    {
        var delta = point - ray.origin;
        float distance = delta.magnitude, along = Vector3.Dot(delta, ray.direction);
        if (!float.IsFinite(distance) || distance <= .01f || distance > nearest || along <= 0
            || (delta - ray.direction * along).sqrMagnitude > radius * radius) return false;
        // Trace to the same contact used by host reach, not to a nearby UI rectangle.
        int count = Physics.RaycastNonAlloc(new Ray(ray.origin, delta / distance), _hits,
            Mathf.Max(0, distance - .003f), ~0, QueryTriggerInteraction.Ignore);
        if (count == _hits.Length) return false;
        var player = LocalPlayer.Transform;
        for (int i = 0; i < count; i++)
        {
            var collider = _hits[i].collider;
            if (collider != null && (player == null || !collider.transform.IsChildOf(player))) return false;
        }
        nearest = distance;
        return true;
    }

    private void UpdateNearby(Vector3 eye)
    {
        int count = 0;
        for (int index = 0; index < _cabinetCount + _tanks.Length; index++)
        {
            var frame = index < _cabinetCount ? _model.CabinetFrame(index) : _model.TankFrame(index - _cabinetCount);
            float distance = (frame.position - eye).sqrMagnitude;
            if (distance > NearbyDistance * NearbyDistance) continue;
            int slot = count;
            while (slot > 0 && distance < _distances[slot - 1]) slot--;
            if (slot >= MaximumPads) continue;
            for (int i = Math.Min(count, MaximumPads - 1); i > slot; i--)
            { _nearest[i] = _nearest[i - 1]; _distances[i] = _distances[i - 1]; }
            _nearest[slot] = index; _distances[slot] = distance;
            count = Math.Min(count + 1, MaximumPads);
        }
        while (_pads.Count < count) AddPad();
        for (int i = 0; i < _pads.Count; i++)
        {
            var pad = _pads[i];
            pad.Active = i < count;
            if (pad.Active && pad.Fixture != _nearest[i])
            {
                pad.Fixture = _nearest[i];
                bool cabinet = pad.Fixture < _cabinetCount;
                int tank = pad.Fixture - _cabinetCount;
                bool high = !cabinet && _tanks[tank].High;
                pad.Frame.SetParent(cabinet ? _model.CabinetDoorFrame(pad.Fixture) : _model.TankFrame(tank), false);
                pad.Frame.localPosition = cabinet ? new(.202f, -.315f, .051f)
                    : high ? new(-.25f, 1.42f, 1.093f) : new(-.09f, .485f, .353f);
                pad.Frame.localRotation = Quaternion.Euler(0, 180, 0);
                pad.Frame.localScale = Vector3.one * (cabinet ? .001f : high ? .00138f : .000647f);
            }
            pad.Frame.gameObject.SetActive(pad.Active);
        }
        _nextReadout = 0;
    }

    private void AddPad()
    {
        var rect = new GameObject("Firefighting fitted display").AddComponent<RectTransform>();
        rect.gameObject.layer = _model.Root.layer;
        rect.SetParent(_model.Root.transform, false);
        rect.sizeDelta = new(340, 130);
        rect.gameObject.AddComponent<Canvas>().renderMode = RenderMode.WorldSpace;
        var pad = new Pad(rect);
        _pads.Add(pad);
        pad.Title = Label(rect, "Fixture", 10, 7, 320, 24, 19, true);
        pad.Water = Label(rect, "Water", 10, 37, 320, 30, 24, true);
        pad.Hint = Label(rect, "Use", 10, 76, 320, 43, 17, true);
        pad.Hint.color = Muted;
        pad.Hint.enableWordWrapping = true;
    }

    private static int CabinetItem(BoxStationFirefightingState state, int cabinet)
    {
        for (int i = 0; i < state.Items.Length; i++)
            if (state.Items[i].Place == BoxStationExtinguisherPlace.Cabinet && state.Items[i].Cabinet == cabinet) return i;
        return -1;
    }

    private void UpdateReadout(Pad pad, BoxStationFirefightingState? state)
    {
        bool cabinet = pad.Fixture < _cabinetCount;
        int index = cabinet ? pad.Fixture : pad.Fixture - _cabinetCount;
        pad.Title.text = (cabinet ? "EXTINGUISHER " : "WATER TANK ") + (index + 1).ToString("00", CultureInfo.InvariantCulture);
        if (state is not { } live)
        {
            pad.Water.text = "HOST OFFLINE"; pad.Water.color = Amber;
            pad.Hint.text = "Water unavailable\nControls unavailable";
            return;
        }
        int item = cabinet ? CabinetItem(live, index) : -1;
        float water = cabinet ? item >= 0 ? live.Items[item].Water : 0 : live.TankWater[index];
        float capacity = cabinet ? BoxStationFirefighting.ExtinguisherCapacity : _tanks[index].Capacity;
        pad.Water.text = cabinet && item < 0 ? "CABINET EMPTY" : water <= 0 ? "WATER EMPTY"
            : water.ToString("0.#", CultureInfo.InvariantCulture) + " / " + capacity.ToString("0", CultureInfo.InvariantCulture) + " u";
        pad.Water.color = water > 0 ? Ink : Amber;
        pad.Hint.text = cabinet ? (live.CabinetOpen[index] ? "OPEN" : "CLOSED") + " / F: latch\nE: take / store"
            : SpacecraftShoreCoolant.IsSupply(_tanks[index]) ? "COOLANT SUPPLY CONNECTED"
            : water > 0 ? "Aim at valve\nE: refill held extinguisher" : "No refill water remaining";
    }

    private void CreateHud()
    {
        _hud = new GameObject("Extinguisher readout");
        var canvas = _hud.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 116;
        var scaler = _hud.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new(1280, 720);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
        var panel = new GameObject("Extinguisher status").AddComponent<RectTransform>();
        panel.SetParent(_hud.transform, false);
        panel.anchorMin = panel.anchorMax = panel.pivot = new(.5f, 0);
        panel.anchoredPosition = new(0, 54);
        panel.sizeDelta = new(660, 92);
        _status = Label(panel, "Water and safety", 0, 0, 660, 24, 18, false);
        _hint = Label(panel, "Controls", 0, 29, 660, 22, 15, false);
        _hint.color = Muted;
        _feedback = Label(panel, "Host response", 0, 57, 660, 28, 14, false);
        _status.alignment = _hint.alignment = _feedback.alignment = TextAlignmentOptions.Center;
        _feedback.color = Amber;
        _hud.SetActive(false);
    }

    private void UpdateHud(BoxStationFirefightingState state, int held, BoxStationFirefightingControl control,
        int focused, int toggle, string feedback)
    {
        string context = focused < 0 ? toggle >= 0 ? "F: toggle cabinet latch" : ""
            : control == BoxStationFirefightingControl.Refill ? "E: refill"
            : control == BoxStationFirefightingControl.StoreCabinet ? "E: store"
            : "E: take extinguisher";
        if (held >= 0)
        {
            var item = state.Items[held];
            _status.text = "WATER " + item.Water.ToString("0.#", CultureInfo.InvariantCulture) + " / 50 u   "
                + (item.Safety ? "SAFETY ON" : "SAFETY OFF") + "   "
                + (item.Water < 1 ? "EMPTY" : item.Safety ? "PIN ENGAGED" : item.CooldownSeconds > 0
                    ? "WAIT " + item.CooldownSeconds.ToString("F1", CultureInfo.InvariantCulture) + "s" : "READY");
            _status.color = item.Water < 1 || item.Safety ? Amber : Ink;
            _hint.text = "LMB: spray   R: safety   G: drop" + (context.Length > 0 ? "   " + context : "");
        }
        else { _status.text = context; _status.color = Ink; _hint.text = toggle >= 0 ? "F: toggle cabinet latch" : ""; }
        _feedback.text = feedback;
    }

    private TextMeshProUGUI Label(Transform parent, string name, float x, float y, float width,
        float height, float size, bool world)
    {
        var rect = new GameObject(name).AddComponent<RectTransform>();
        rect.gameObject.layer = parent.gameObject.layer;
        rect.SetParent(parent, false);
        rect.anchorMin = rect.anchorMax = rect.pivot = new(0, 1);
        rect.anchoredPosition = new(x, -y); rect.sizeDelta = new(width, height);
        var text = rect.gameObject.AddComponent<TextMeshProUGUI>();
        _typography.Apply(text, TabletTextRole.Control);
        if (world && text.fontSharedMaterial is { } source)
        {
            int id = source.GetInstanceID();
            if (!_fonts.TryGetValue(id, out var material))
            {
                material = new Material(source) { name = "Firefighting fitted text" };
                _fonts.Add(id, material);
                var shader = Shader.Find("TextMeshPro/Distance Field");
                if (shader != null) material.shader = shader;
                material.SetInt("unity_GUIZTestMode", (int)CompareFunction.LessEqual);
                if (material.HasProperty("_ZTestMode")) material.SetInt("_ZTestMode", (int)CompareFunction.LessEqual);
                ClientDeviceScreenMaterial.ConfigureOwnedUiMaterial(material);
            }
            text.fontSharedMaterial = material;
        }
        text.fontSize = size; text.color = Ink; text.isOverlay = !world;
        text.richText = false; text.enableWordWrapping = false; text.raycastTarget = false;
        text.overflowMode = TextOverflowModes.Ellipsis; text.alignment = TextAlignmentOptions.MidlineLeft;
        return text;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hud != null && _hud) { _hud.SetActive(false); UnityEngine.Object.Destroy(_hud); }
        _hud = null;
        foreach (var pad in _pads) if (pad.Frame) UnityEngine.Object.Destroy(pad.Frame.gameObject);
        _pads.Clear();
        foreach (var material in _fonts.Values) if (material) UnityEngine.Object.Destroy(material);
        _fonts.Clear();
        _typography.Dispose();
    }

    private sealed class Pad(Transform frame)
    {
        internal readonly Transform Frame = frame;
        internal int Fixture = -1;
        internal bool Active;
        internal TextMeshProUGUI Title = null!, Water = null!, Hint = null!;
    }
}
