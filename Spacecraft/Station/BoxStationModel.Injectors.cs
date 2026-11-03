// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class BoxStationModel
{
    private static readonly string[] InjectorParts = ["body", "valve", "toggle", "pulse", "knob"];
    private static readonly Vector3[] InjectorPivots =
        [Vector3.zero, new(0, .045f, .055f), new(-.245f, .116f, -.275f), new(-.125f, .113f, -.275f), new(.245f, .112f, -.275f)];
    private readonly List<InjectorVisual> _injectors = new();
    private Mesh[,] _injectorMeshes = new Mesh[5, 2];
    private Material? _injectorMaterial;

    internal Vector3 InjectorControlPoint(int index, int button)
        => PlumbingControlFrame(index).TransformPoint(InjectorButtonCenter(button));

    internal static Vector3 InjectorButtonCenter(int button) => button switch
    {
        0 => new(-.245f, .158f, -.275f),
        1 => new(-.125f, .1255f, -.275f),
        2 or 3 => new(.245f, .1475f, -.275f),
        _ => throw new ArgumentOutOfRangeException(nameof(button))
    };

    private IEnumerable<bool> BuildInjectorMeshes()
    {
        for (int part = 0; part < InjectorParts.Length; part++)
        for (int lod = 0; lod < 2; lod++)
        {
            _injectorMeshes[part, lod] = CreateMesh(_parts["injector-" + InjectorParts[part] + (lod == 0 ? "" : "-lod")]);
            yield return false;
        }
    }

    private IEnumerable<bool> BuildInjector(int index, Transform frame, Vector3 position, Quaternion rotation)
    {
        var parts = new Transform[InjectorParts.Length];
        var detail = new Renderer[parts.Length];
        var coarse = new Renderer[parts.Length];
        for (int part = 0; part < parts.Length; part++)
        {
            var node = parts[part] = Node("Injector " + InjectorParts[part], frame);
            node.localPosition = InjectorPivots[part];
            detail[part] = Render(node, _injectorMeshes[part, 0], _injectorMaterial!);
            coarse[part] = Render(Node("Distant surface", node), _injectorMeshes[part, 1], _injectorMaterial!);
            yield return false;
        }
        parts[2].localRotation = Quaternion.Euler(-18, 0, 0);
        parts[4].localRotation = Quaternion.Euler(0, -135, 0);
        var lod = AddDoorLod(frame, detail, coarse);
        lod.localReferencePoint = new(0, 0, 0);
        lod.size = .8f;
        _injectors.Add(new(index, parts));
        // The inlet is below the deck. Keep the control strip above collision for camera and host reach.
        _machineSolids.Add(new(position, rotation, new(0, .045f, 0), new(.74f, .09f, .74f)));
        _machineSolids.Add(new(position, rotation, new(0, .12f, .055f), new(.46f, .08f, .46f)));
        _machineSolids.Add(new(position, rotation, new(-.274f, .135f, -.275f), new(.012f, .067f, .071f)));
        _machineSolids.Add(new(position, rotation, new(-.216f, .135f, -.275f), new(.012f, .067f, .071f)));
        _machineSolids.Add(new(position, rotation, new(-.245f, .158f, -.305f), new(.068f, .014f, .012f)));
        yield return false;
    }

    internal void UpdateInjectors(BoxStationState? state)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (state is not { } snapshot || snapshot.LayoutHash != _layout.LayoutHash
            || snapshot.Plumbing.Length != _plumbingFrames.Length) return;
        float step = Math.Clamp(Time.unscaledDeltaTime, 0, .1f);
        foreach (var visual in _injectors)
        {
            var device = snapshot.Plumbing[visual.Device];
            float valveTarget = (device.Enabled && device.Powered && device.Connected) || device.InjectorPulsing ? 70 : 0;
            float toggleTarget = device.Enabled ? 18 : -18;
            float pulseTarget = device.InjectorPulsing ? .004f : 0;
            float knobTarget = -135 + 270f * device.InjectorLitresPerSecond / BoxStationPlumbing.MaximumInjectorLitresPerSecond;
            float valve = visual.Known ? Mathf.MoveTowards(visual.Valve, valveTarget, step * 280) : valveTarget;
            float toggle = visual.Known ? Mathf.MoveTowards(visual.Toggle, toggleTarget, step * 240) : toggleTarget;
            float pulse = visual.Known ? Mathf.MoveTowards(visual.Pulse, pulseTarget, step * .06f) : pulseTarget;
            float knob = visual.Known ? Mathf.MoveTowards(visual.Knob, knobTarget, step * 360) : knobTarget;
            if (!visual.Known || valve != visual.Valve) visual.Parts[1].localRotation = Quaternion.Euler(valve, 0, 0);
            if (!visual.Known || toggle != visual.Toggle) visual.Parts[2].localRotation = Quaternion.Euler(toggle, 0, 0);
            if (!visual.Known || pulse != visual.Pulse) visual.Parts[3].localPosition = InjectorPivots[3] + Vector3.down * pulse;
            if (!visual.Known || knob != visual.Knob) visual.Parts[4].localRotation = Quaternion.Euler(0, knob, 0);
            visual.Valve = valve;
            visual.Toggle = toggle;
            visual.Pulse = pulse;
            visual.Knob = knob;
            visual.Known = true;
        }
    }

    private void LoadInjectorMaterial(Shader shader)
    {
        _injectorMaterial = new Material(shader) { name = "Outlet injector finish", enableInstancing = true };
        _injectorMaterial.SetColor("_BaseColor", Color.white);
        _injectorMaterial.SetTexture("_BaseColorMap", LoadTexture("injector-base", false));
        _injectorMaterial.SetTexture("_MaskMap", LoadTexture("injector-mask", true));
        _injectorMaterial.SetTexture("_NormalMap", LoadTexture("injector-normal", true, true));
        _injectorMaterial.SetFloat("_NormalMapSpace", 0); _injectorMaterial.SetFloat("_NormalScale", 1);
        _injectorMaterial.SetFloat("_MetallicRemapMin", 0); _injectorMaterial.SetFloat("_MetallicRemapMax", 1);
        _injectorMaterial.SetFloat("_SmoothnessRemapMin", 0); _injectorMaterial.SetFloat("_SmoothnessRemapMax", 1);
        _injectorMaterial.SetFloat("_AORemapMin", 0); _injectorMaterial.SetFloat("_AORemapMax", 1);
        _injectorMaterial.EnableKeyword("_MASKMAP");
        _injectorMaterial.EnableKeyword("_NORMALMAP");
        _injectorMaterial.EnableKeyword("_NORMALMAP_TANGENT_SPACE");
    }

    private void ReleaseInjectors()
    {
        _injectors.Clear();
        _injectorMeshes = new Mesh[5, 2];
        if (_injectorMaterial) UnityEngine.Object.Destroy(_injectorMaterial);
        _injectorMaterial = null;
    }

    private sealed class InjectorVisual(int device, Transform[] parts)
    {
        internal readonly int Device = device;
        internal readonly Transform[] Parts = parts;
        internal bool Known;
        internal float Valve, Toggle, Pulse, Knob;
    }
}
