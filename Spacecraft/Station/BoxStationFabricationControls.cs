// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using TheForest.Utils;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

namespace Modstoke.Client;

internal sealed class BoxStationFabricationControls : IDisposable
{
    private const float PixelScale = .0005f, Reach = 2.4f, ReadoutDistance = 5;
    private static readonly Color Ink = new(.90f, .94f, .92f, 1), Muted = new(.46f, .53f, .52f, 1),
        Green = new(.38f, .84f, .67f, 1), Amber = new(1, .72f, .28f, 1);
    private static readonly string[] KeyNames = { "LOAD", "STEEL OUT", "CABLE OUT", "MAKE LINE", "MAKE CIRCUIT", "CANCEL", "TAKE LINE", "TAKE CIRCUIT" };
    private readonly BoxStationModel _model;
    private readonly Action<BoxStationMaterialControl, int, BoxStationMaterial, Vector3> _command;
    private readonly ClientTabletTypography _typography = new();
    private readonly Il2CppStructArray<RaycastHit> _hits = new(size: 32);
    private readonly List<Material> _materials = new();
    private readonly Dictionary<int, Material> _fonts = new();
    private readonly List<Transform> _canvases = new();
    private readonly TextMeshProUGUI[] _rows = new TextMeshProUGUI[6], _keys = new TextMeshProUGUI[8];
    private float _nextReadout;
    private int _focused = -1;
    private bool _visible, _disposed;

    internal BoxStationFabricationControls(BoxStationModel model,
        Action<BoxStationMaterialControl, int, BoxStationMaterial, Vector3> command)
    {
        _model = model;
        _command = command;
        try { CreateDisplay(); }
        catch { Dispose(); throw; }
    }

    // The owner supplies fresh snapshots. Controls additionally require the model's coherent portable projection.
    internal bool Update(BoxStationMaterialState? state, bool interactive, string feedback)
    {
        if (_disposed) return false;
        if (state is not { } snapshot || snapshot.LayoutHash != _model.Layout.LayoutHash
            || snapshot.Fabrication.Machine != BoxStationHullRepair.FabricationMachine
            || _model.PortableState is not { } portables || portables.Materials != snapshot) state = null;
        var camera = LocalPlayer.MainCam;
        bool cameraLive = camera != null && camera && camera.isActiveAndEnabled;
        var frame = _model.FabricationControlFrame(BoxStationHullRepair.FabricationMachine);
        bool visible = _model.Visible && cameraLive
            && (camera!.transform.position - frame.position).sqrMagnitude < ReadoutDistance * ReadoutDistance;
        SetVisible(visible);
        if (!visible) { _focused = -1; return false; }
        bool input = interactive && ClientSpacecraftRider.WorldInputAvailable && !Cursor.visible;
        int focused = input ? FindControl(camera!) : -1;
        if (focused >= 0 && state is { } current && DisabledReason(current.Fabrication, Control(focused)).Length == 0
            && (Input.GetKeyDown(KeyCode.E) || Input.GetMouseButtonDown(0)))
        {
            _command(Control(focused), BoxStationHullRepair.FabricationMachine, default, Vector3.zero);
            _nextReadout = 0;
        }
        if (Time.unscaledTime >= _nextReadout || focused != _focused)
        {
            _nextReadout = Time.unscaledTime + .2f;
            UpdateReadout(state, interactive, focused, feedback);
        }
        _focused = focused;
        return focused >= 0;
    }

    private void CreateDisplay()
    {
        var machine = _model.FabricationControlFrame(BoxStationHullRepair.FabricationMachine);
        var screen = CreateCanvas("Autolathe display", machine, new(0, 1.27f, .456f), new(1000, 480));
        for (int row = 0; row < _rows.Length; row++)
            _rows[row] = Label(screen, "Autolathe readout " + row, new(34, 25 + row * 70, 932, 56), row == 0 ? 31 : 29, Ink);
        for (int button = 0; button < _keys.Length; button++)
        {
            var key = _model.FabricationKeyFrame(BoxStationHullRepair.FabricationMachine, Control(button));
            var canvas = CreateCanvas("Autolathe key label " + button, key, new(0, 0, .0007f), new(412, 122));
            var text = _keys[button] = Label(canvas, KeyNames[button], new(12, 12, 388, 98), 27, Ink);
            text.alignment = TextAlignmentOptions.Center;
            text.text = KeyNames[button];
        }
    }

    private RectTransform CreateCanvas(string name, Transform parent, Vector3 position, Vector2 size)
    {
        var canvas = new GameObject(name).AddComponent<RectTransform>();
        _canvases.Add(canvas);
        canvas.SetParent(parent, false);
        canvas.gameObject.layer = parent.gameObject.layer;
        canvas.localPosition = position;
        canvas.localRotation = Quaternion.Euler(0, 180, 0);
        canvas.localScale = Vector3.one * PixelScale;
        canvas.sizeDelta = size;
        canvas.gameObject.AddComponent<Canvas>().renderMode = RenderMode.WorldSpace;
        canvas.gameObject.SetActive(false);
        return canvas;
    }

    private void SetVisible(bool visible)
    {
        if (_visible == visible) return;
        _visible = visible;
        foreach (var canvas in _canvases) canvas.gameObject.SetActive(visible);
        _nextReadout = 0;
    }

    private int FindControl(Camera camera)
    {
        var ray = new Ray(camera.transform.position, camera.transform.forward);
        if (!_model.TryFabricationControlTarget(ray, Reach, out var control, out var contact)) return -1;
        float distance = Vector3.Distance(ray.origin, contact);
        int count = Physics.RaycastNonAlloc(ray, _hits, MathF.Max(0, distance - .003f), ~0, QueryTriggerInteraction.Ignore);
        if (count == _hits.Length) return -1;
        var player = LocalPlayer.Transform;
        for (int i = 0; i < count; i++)
        {
            var collider = _hits[i].collider;
            if (collider != null && (player == null || !collider.transform.IsChildOf(player))) return -1;
        }
        return (int)control - (int)BoxStationMaterialControl.LoadFabricator;
    }

    private void UpdateReadout(BoxStationMaterialState? state, bool interactive, int focused, string feedback)
    {
        _rows[0].text = "AUTOLATHE / SHIP SPARES";
        if (state is not { } snapshot)
        {
            _rows[1].text = "SYNCING / HOST DATA UNAVAILABLE";
            _rows[1].color = Amber;
            _rows[2].text = "JOB --   WORK --";
            _rows[3].text = "STEEL -- / 50    CABLE -- / 30";
            _rows[4].text = "LINE -- / 24    CIRCUIT -- / 12";
            _rows[5].text = "CONTROLS LOCKED";
            _rows[5].color = Muted;
            foreach (var key in _keys) key.color = Muted;
            return;
        }
        var value = snapshot.Fabrication;
        _rows[1].text = value.Status switch
        {
            BoxStationFabricationStatus.Working => "FABRICATING",
            BoxStationFabricationStatus.NoPower => "PAUSED / NO POWER",
            BoxStationFabricationStatus.NoCooling => "PAUSED / COOLING LIMIT",
            BoxStationFabricationStatus.OutputBlocked => "PAUSED / OUTPUT FULL",
            _ => "READY"
        };
        _rows[1].color = value.Status is BoxStationFabricationStatus.NoPower or BoxStationFabricationStatus.NoCooling
            or BoxStationFabricationStatus.OutputBlocked ? Amber : value.Status == BoxStationFabricationStatus.Working ? Green : Ink;
        double required = BoxStationHullRepair.FabricationWork(value.Recipe);
        _rows[2].text = required <= 0 ? "NO JOB SELECTED" : (value.Recipe == BoxStationFabricationRecipe.Line ? "LINE KIT" : "CIRCUIT KIT")
            + "   " + (value.WorkJoules / required * 100).ToString("F0", CultureInfo.InvariantCulture) + "%";
        _rows[3].text = "STEEL " + Number(value.Steel) + " / " + Number(BoxStationHullRepair.FabricationSteelCapacity)
            + "    CABLE " + Number(value.Cable) + " / " + Number(BoxStationHullRepair.FabricationCableCapacity);
        _rows[4].text = "LINE " + Number(value.LineStock) + " / " + Number(BoxStationHullRepair.FabricationLineCapacity)
            + "    CIRCUIT " + Number(value.CircuitStock) + " / " + Number(BoxStationHullRepair.FabricationCircuitCapacity);
        string reason = focused >= 0 ? DisabledReason(value, Control(focused)) : "";
        _rows[5].text = !interactive ? "READ ONLY" : reason.Length > 0 ? reason
            : focused >= 0 && feedback.Length > 0 ? feedback : focused == 3 ? "LINE KIT / 1 STEEL"
            : focused == 4 ? "CIRCUIT KIT / 1 STEEL + 2 CABLE" : "INPUT: STEEL / CABLE";
        _rows[5].color = reason.Length > 0 ? Amber : Muted;
        for (int i = 0; i < _keys.Length; i++)
            _keys[i].color = !interactive || DisabledReason(value, Control(i)).Length > 0 ? Muted : i == focused ? Green : Ink;
    }

    private static string DisabledReason(BoxStationFabricationState state, BoxStationMaterialControl control)
    {
        bool working = state.Recipe != BoxStationFabricationRecipe.None;
        switch (control)
        {
            case BoxStationMaterialControl.LoadFabricator:
                return state.Steel >= BoxStationHullRepair.FabricationSteelCapacity && state.Cable >= BoxStationHullRepair.FabricationCableCapacity
                    ? "INPUT FULL" : "";
            case BoxStationMaterialControl.UnloadFabricatorSteel:
                return working ? "JOB ACTIVE" : state.Steel == 0 ? "STEEL INPUT EMPTY" : "";
            case BoxStationMaterialControl.UnloadFabricatorCable:
                return working ? "JOB ACTIVE" : state.Cable == 0 ? "CABLE INPUT EMPTY" : "";
            case BoxStationMaterialControl.FabricateLine:
                return working ? "JOB ACTIVE" : state.Steel < 1 ? "REQUIRES 1 STEEL"
                    : state.LineStock >= BoxStationHullRepair.FabricationLineCapacity ? "LINE OUTPUT FULL" : "";
            case BoxStationMaterialControl.FabricateCircuit:
                return working ? "JOB ACTIVE" : state.Steel < 1 || state.Cable < 2 ? "REQUIRES 1 STEEL + 2 CABLE"
                    : state.CircuitStock >= BoxStationHullRepair.FabricationCircuitCapacity ? "CIRCUIT OUTPUT FULL" : "";
            case BoxStationMaterialControl.CancelFabrication:
                return working ? "" : "NO JOB SELECTED";
            case BoxStationMaterialControl.TakeFabricatorLine:
                return state.LineStock == 0 ? "LINE OUTPUT EMPTY" : "";
            case BoxStationMaterialControl.TakeFabricatorCircuit:
                return state.CircuitStock == 0 ? "CIRCUIT OUTPUT EMPTY" : "";
            default: return "";
        }
    }

    private static BoxStationMaterialControl Control(int button)
        => (BoxStationMaterialControl)((int)BoxStationMaterialControl.LoadFabricator + button);
    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private TextMeshProUGUI Label(Transform parent, string name, Rect bounds, float size, Color color)
    {
        var rect = new GameObject(name).AddComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.gameObject.layer = parent.gameObject.layer;
        rect.anchorMin = rect.anchorMax = rect.pivot = new(0, 1);
        rect.anchoredPosition = new(bounds.x, -bounds.y);
        rect.sizeDelta = new(bounds.width, bounds.height);
        var text = rect.gameObject.AddComponent<TextMeshProUGUI>();
        _typography.Apply(text, TabletTextRole.Control);
        var source = text.fontSharedMaterial;
        if (source != null)
        {
            int id = source.GetInstanceID();
            if (!_fonts.TryGetValue(id, out var material))
            {
                material = new Material(source) { name = "Autolathe display text" };
                _materials.Add(material);
                var shader = Shader.Find("TextMeshPro/Distance Field");
                if (shader != null) material.shader = shader;
                material.SetInt("unity_GUIZTestMode", (int)CompareFunction.LessEqual);
                if (material.HasProperty("_ZTestMode")) material.SetInt("_ZTestMode", (int)CompareFunction.LessEqual);
                ClientDeviceScreenMaterial.ConfigureOwnedUiMaterial(material);
                _fonts.Add(id, material);
            }
            text.fontSharedMaterial = material;
        }
        text.transform.localPosition += new Vector3(0, 0, -.4f);
        text.isOverlay = false;
        text.text = "";
        text.fontSize = size;
        text.color = color;
        text.richText = false;
        text.enableWordWrapping = false;
        text.raycastTarget = false;
        text.overflowMode = TextOverflowModes.Ellipsis;
        text.alignment = TextAlignmentOptions.MidlineLeft;
        return text;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var canvas in _canvases) if (canvas) UnityEngine.Object.Destroy(canvas.gameObject);
        _canvases.Clear();
        foreach (var material in _materials) if (material) UnityEngine.Object.Destroy(material);
        _materials.Clear();
        _fonts.Clear();
        _typography.Dispose();
    }
}
