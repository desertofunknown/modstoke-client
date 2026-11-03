// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Modstoke.Client;

internal sealed class BoxStationWaterEffects : IDisposable
{
    private const int BurstCount = 8, StreamCount = 5;
    private const float Lifetime = .38f;
    private static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
    private readonly BoxStationModel? _model;
    private readonly ClientSpacecraftBody? _body;
    private readonly Action<int, float> _setSpray;
    private readonly Burst[] _bursts = new Burst[BurstCount];
    private GameObject? _root;
    private Material? _material;
    private long _sequence;
    private int _next;
    private bool _disposed;

    internal BoxStationWaterEffects(BoxStationModel model)
    {
        _model = model;
        _setSpray = model.SetExtinguisherSpray;
    }

    internal BoxStationWaterEffects(ClientSpacecraftBody body, Action<int, float> setSpray)
    {
        _body = body;
        _setSpray = setSpray;
    }

    private bool Visible => _model?.Visible ?? (_body is { Ready: true, TelemetryCurrent: true }
        && _body.State.Travel == SpacecraftTravel.None && _body.Root.activeInHierarchy);

    // Receipts are authenticated and bounded by the owning service before presentation.
    internal void Show(BoxStationWaterBurst receipt)
    {
        if (_disposed || !Visible || receipt.Sequence <= _sequence) return;
        if (_root == null) Ensure();
        _sequence = receipt.Sequence;
        var burst = _bursts[_next];
        _next = (_next + 1) % BurstCount;
        Stop(burst);
        burst.Item = receipt.Item;
        burst.Start = Time.unscaledTime;
        burst.Origin = receipt.Origin;
        burst.Scale = Mathf.Sqrt(receipt.Units / BoxStationFirefighting.BurstUnits);
        for (int i = 0; i < StreamCount; i++)
        {
            burst.Endpoints[i] = receipt.Endpoints[i];
            burst.Streams[i].SetPosition(0, receipt.Origin);
            burst.Streams[i].SetPosition(1, receipt.Origin);
        }
        burst.Root.SetActive(true);
        _setSpray(burst.Item, 1);
    }

    internal void Update()
    {
        if (_disposed || _root == null) return;
        float now = Time.unscaledTime;
        for (int i = 0; i < BurstCount; i++)
        {
            var burst = _bursts[i];
            if (burst.Item < 0) continue;
            float age = (now - burst.Start) / Lifetime;
            if (!Visible || age >= 1) { Stop(burst); continue; }
            _setSpray(burst.Item, Mathf.Clamp01(1 - age * 2));
            float opacity = Mathf.Clamp01(1 - age) * .65f;
            burst.Properties.SetColor(BaseColor, new Color(.64f, .85f, .94f, opacity));
            for (int stream = 0; stream < StreamCount; stream++)
            {
                var line = burst.Streams[stream];
                // Segments stay on the accepted ray and never travel beyond its obstruction endpoint.
                float lead = Mathf.Clamp01(age * 2.2f - stream * .035f);
                float tail = Mathf.Clamp01(lead - .18f - (1 - age) * .38f);
                line.SetPosition(0, Vector3.Lerp(burst.Origin, burst.Endpoints[stream], tail));
                line.SetPosition(1, Vector3.Lerp(burst.Origin, burst.Endpoints[stream], lead));
                line.startWidth = .012f * burst.Scale * (1 - age * .5f);
                line.endWidth = .024f * burst.Scale * (1 - age * .5f);
                line.SetPropertyBlock(burst.Properties);
            }
        }
    }

    private void Ensure()
    {
        try
        {
            var shader = Shader.Find("HDRP/Lit");
            if (!shader) throw new InvalidOperationException("Water effects require HDRP/Lit.");
            var parent = _model?.Root ?? _body!.Root;
            _root = new GameObject("Water bursts") { layer = parent.layer };
            _root.transform.SetParent(parent.transform, false);
            _material = new Material(shader) { name = "Water spray" };
            _material.SetFloat("_SurfaceType", 1);
            _material.SetOverrideTag("RenderType", "Transparent");
            _material.SetFloat("_BlendMode", 0);
            _material.SetFloat("_SrcBlend", (float)BlendMode.One);
            _material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            _material.SetFloat("_AlphaSrcBlend", (float)BlendMode.One);
            _material.SetFloat("_AlphaDstBlend", (float)BlendMode.OneMinusSrcAlpha);
            _material.SetFloat("_ZWrite", 0);
            _material.SetFloat("_TransparentZWrite", 0);
            _material.SetFloat("_ZTestDepthEqualForOpaque", (float)CompareFunction.LessEqual);
            _material.SetFloat("_ZTestTransparent", (float)CompareFunction.LessEqual);
            _material.SetFloat("_CullMode", (float)CullMode.Off);
            _material.SetFloat("_CullModeForward", (float)CullMode.Off);
            _material.SetFloat("_DoubleSidedEnable", 1);
            _material.SetFloat("_DoubleSidedNormalMode", 2);
            _material.SetVector("_DoubleSidedConstants", new Vector4(1, 1, 1, 0));
            _material.SetFloat("_Metallic", 0);
            _material.SetFloat("_Smoothness", .7f);
            _material.SetFloat("_EnableBlendModePreserveSpecularLighting", 0);
            _material.SetColor("_BaseColor", new Color(.64f, .85f, .94f, .65f));
            _material.SetColor("_EmissiveColor", Color.black);
            _material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            _material.EnableKeyword("_ENABLE_FOG_ON_TRANSPARENT");
            _material.EnableKeyword("_DOUBLESIDED_ON");
            _material.SetShaderPassEnabled("TransparentDepthPrepass", false);
            _material.SetShaderPassEnabled("TransparentDepthPostpass", false);
            _material.SetShaderPassEnabled("ShadowCaster", false);
            _material.renderQueue = (int)RenderQueue.Transparent;
            for (int i = 0; i < BurstCount; i++)
            {
                var node = new GameObject("Water burst") { layer = _root.layer };
                node.transform.SetParent(_root.transform, false);
                var burst = new Burst(node);
                _bursts[i] = burst;
                for (int stream = 0; stream < StreamCount; stream++)
                {
                    var lineObject = new GameObject("Water stream") { layer = _root.layer };
                    lineObject.transform.SetParent(node.transform, false);
                    var line = lineObject.AddComponent<LineRenderer>();
                    burst.Streams[stream] = line;
                    line.useWorldSpace = false; line.loop = false; line.positionCount = 2;
                    line.sharedMaterial = _material;
                    line.generateLightingData = true;
                    line.shadowCastingMode = ShadowCastingMode.Off;
                    line.receiveShadows = false;
                    line.lightProbeUsage = LightProbeUsage.Off;
                    line.reflectionProbeUsage = ReflectionProbeUsage.Off;
                }
                node.SetActive(false);
            }
        }
        catch { Dispose(); throw; }
    }

    private void Stop(Burst burst)
    {
        if (burst.Item < 0) return;
        _setSpray(burst.Item, 0);
        burst.Item = -1;
        burst.Root.SetActive(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var burst in _bursts) if (burst != null) Stop(burst);
        if (_root != null && _root) { _root.SetActive(false); UnityEngine.Object.Destroy(_root); }
        if (_material != null && _material) UnityEngine.Object.Destroy(_material);
        _root = null; _material = null;
    }

    private sealed class Burst(GameObject root)
    {
        internal readonly GameObject Root = root;
        internal readonly LineRenderer[] Streams = new LineRenderer[StreamCount];
        internal readonly Vector3[] Endpoints = new Vector3[StreamCount];
        internal readonly MaterialPropertyBlock Properties = new();
        internal Vector3 Origin;
        internal int Item = -1;
        internal float Start, Scale;
    }
}
