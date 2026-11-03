// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftModel
{
    private static readonly FrameSettingsField[] CabinCaptureDisabledEffects =
    {
        FrameSettingsField.ReflectionProbe, FrameSettingsField.PlanarProbe,
        FrameSettingsField.SkyReflection, FrameSettingsField.SSR,
        FrameSettingsField.TransparentSSR, FrameSettingsField.SSGI,
        FrameSettingsField.ProbeVolume, FrameSettingsField.AtmosphericScattering,
        FrameSettingsField.Volumetrics, FrameSettingsField.VolumetricClouds,
        FrameSettingsField.ExposureControl, FrameSettingsField.Postprocess,
        FrameSettingsField.CustomPostProcess, FrameSettingsField.CustomPass
    };

    private readonly HDAdditionalReflectionData?[] _cabinReflections = new HDAdditionalReflectionData?[2];
    private GameObject? _cabinReflectionRoot;
    private float _reflectionElapsed;
    private bool _reflectionRequested;
    private Vector3 _reflectionPosition;
    private Quaternion _reflectionRotation;
    private Vector3 _reflectionDoors;
    private Vector2 _reflectionPower;

    private void InitializeCabinReflections()
    {
        _cabinReflectionRoot = new GameObject("Cabin reflections") { layer = _root!.layer };
        _cabinReflectionRoot.SetActive(false);
        _cabinReflectionRoot.transform.SetParent(_root.transform, false);
        AddCabinReflection(0, "Cockpit and cabin reflection", new Vector3(0, .25f, 2), new Vector3(8, 5, 16));
        AddCabinReflection(1, "Aft corridor reflection", new Vector3(0, .25f, -10), new Vector3(4.5f, 4, 8));
    }

    private void AddCabinReflection(int index, string name, Vector3 position, Vector3 size)
    {
        var node = new GameObject(name) { layer = _root!.layer };
        node.transform.SetParent(_cabinReflectionRoot!.transform, false);
        node.transform.localPosition = position;
        var legacy = node.AddComponent<ReflectionProbe>();
        legacy.mode = ReflectionProbeMode.Realtime;
        legacy.refreshMode = ReflectionProbeRefreshMode.ViaScripting;
        legacy.boxProjection = true;
        legacy.size = size;
        var probe = node.AddComponent<HDAdditionalReflectionData>();
        _cabinReflections[index] = probe;
        probe.mode = ProbeSettings.Mode.Realtime;
        probe.realtimeMode = ProbeSettings.RealtimeMode.OnDemand;
        // A moving ship must capture every face at the same pose.
        probe.timeSlicing = false;
        probe.useInfluenceVolumeAsProxyVolume = true;
        probe.weight = 1;
        probe.multiplier = 1;
        var influence = probe.influenceVolume;
        influence.shape = InfluenceShape.Box;
        influence.boxSize = size;
        influence.boxBlendDistancePositive = Vector3.one * .2f;
        influence.boxBlendDistanceNegative = Vector3.one * .2f;

        // The generated settingsRaw ref cannot represent this boxed IL2CPP
        // value. Copy the native field and write it back through its accessor.
        var settings = probe.m_ProbeSettings;
        var resolution = settings.cubeResolution;
        resolution.useOverride = true;
        resolution.@override = CubeReflectionResolution.CubeReflectionResolution128;
        settings.cubeResolution = resolution;
        var camera = settings.cameraSettings;
        var clearing = camera.bufferClearing;
        clearing.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
        clearing.backgroundColorHDR = Color.black;
        clearing.clearDepth = true;
        camera.bufferClearing = clearing;
        var frustum = camera.frustum;
        frustum.nearClipPlaneRaw = .05f;
        // Capture nearby ship geometry without reaching the distant island.
        frustum.farClipPlaneRaw = 24;
        camera.frustum = frustum;
        var culling = camera.culling;
        culling.cullingMask = 1 << _root.layer;
        culling.useOcclusionCulling = false;
        camera.culling = culling;
        var volumes = camera.volumes;
        volumes.layerMask = 0;
        volumes.anchorOverride = node.transform;
        camera.volumes = volumes;
        camera.probeLayerMask = 0;
        var frame = FrameSettings.NewDefaultRealtimeReflectionProbe();
        var mask = new FrameSettingsOverrideMask();
        foreach (var field in CabinCaptureDisabledEffects)
        {
            frame.SetEnabled(field, false);
            mask.mask[(uint)field] = true;
        }
        camera.renderingPathCustomFrameSettings = frame;
        camera.renderingPathCustomFrameSettingsOverrideMask = mask;
        camera.customRenderingSettings = true;
        settings.cameraSettings = camera;
        probe.m_ProbeSettings = settings;
        probe.PrepareCulling();
    }

    private void UpdateCabinReflections(SpacecraftFlightState state, float dt)
    {
        if (_cabinReflectionRoot == null) return;
        var camera = state.Region == SpacecraftRegion.Space ? Camera.main : null;
        var active = camera != null && (camera.transform.position - _root!.transform.position).sqrMagnitude < 1600;
        if (_cabinReflectionRoot.activeSelf != active)
        {
            _cabinReflectionRoot.SetActive(active);
            _reflectionRequested = false;
            _reflectionElapsed = 1;
        }
        if (!active) return;
        _reflectionElapsed += dt;
        if (_reflectionRequested && _reflectionElapsed < 1) return;
        var position = _root!.transform.position;
        var rotation = _root.transform.rotation;
        var doors = new Vector3(_doorAmount, _innerDoorAmount, _cabinDoorAmount);
        var power = new Vector2(_cabinPower, _screenPower);
        if (_reflectionRequested && (position - _reflectionPosition).sqrMagnitude < 1
            && Quaternion.Angle(rotation, _reflectionRotation) < 5
            && (doors - _reflectionDoors).sqrMagnitude < .0025f
            && (power - _reflectionPower).sqrMagnitude < .0025f) return;
        foreach (var probe in _cabinReflections)
            if (probe != null) probe.RequestRenderNextUpdate();
        _reflectionPosition = position;
        _reflectionRotation = rotation;
        _reflectionDoors = doors;
        _reflectionPower = power;
        _reflectionElapsed = 0;
        _reflectionRequested = true;
    }

    private void ReleaseCabinReflections()
    {
        if (_cabinReflectionRoot != null) _cabinReflectionRoot.SetActive(false);
        for (var i = 0; i < _cabinReflections.Length; i++)
        {
            var probe = _cabinReflections[i];
            if (probe != null)
            {
                // The shipped player has no HDProbe.OnDestroy buffer release.
                // Disable registration before releasing only our probe's handles.
                probe.realtimeTextureRTH?.Release();
                probe.realtimeDepthTextureRTH?.Release();
                probe.m_RealtimeTexture = null;
                probe.m_RealtimeDepthBuffer = null;
            }
            _cabinReflections[i] = null;
        }
        _cabinReflectionRoot = null;
        _reflectionRequested = false;
    }
}
