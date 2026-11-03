// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using UnityEngine;

namespace Modstoke.Client;

internal sealed class SpacecraftFireEffects : IDisposable
{
    private const int FlameLayers = 3, FrameCount = 8;
    private static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
    private static readonly int EmissiveColor = Shader.PropertyToID("_EmissiveColor");
    private readonly ClientSpacecraftBody _body;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly Emitter?[] _emitters = new Emitter[3];
    private readonly Mesh?[] _frames = new Mesh[FrameCount];
    private GameObject? _root;
    private Material? _material;
    private Texture2D? _texture;
    private float _time;
    private bool _failed, _disposed;

    // The ship service owns these Unity resources on its main thread.
    internal SpacecraftFireEffects(ClientSpacecraftBody body) => _body = body;

    // Shared with the authoritative spray target; positions are in the ship frame.
    internal static Vector3 Source(SpacecraftCompartment compartment) => compartment switch
    {
        SpacecraftCompartment.Cabin => new(-3.13f, -.65f, -2.75f),
        SpacecraftCompartment.Airlock => new(-.72f, -.55f, -6.65f),
        SpacecraftCompartment.Entry => new(-1.64f, -1.10f, -11.40f),
        _ => throw new ArgumentOutOfRangeException(nameof(compartment))
    };

    internal void Update(SpacecraftFireState state, Vector3 eye, float dt)
    {
        if (_disposed || _failed || Environment.CurrentManagedThreadId != _thread) return;
        try
        {
            if (!_body.Ready || !_body.Root.activeInHierarchy || !state.Active
                || !float.IsFinite(dt) || dt <= 0 || !float.IsFinite(eye.x)
                || !float.IsFinite(eye.y) || !float.IsFinite(eye.z))
            { Hide(); return; }
            var frame = _body.Root.transform;
            var localEye = frame.InverseTransformPoint(eye);
            if (localEye.sqrMagnitude > 40 * 40) { Hide(); return; }
            Ensure();
            _time = Mathf.Repeat(_time + Mathf.Min(dt, .1f), 120);
            bool visible = false;
            for (int i = 0; i < _emitters.Length; i++)
            {
                var emitter = _emitters[i]!;
                var compartment = (SpacecraftCompartment)i;
                float watts = state.Section(compartment).PowerWatts;
                // Stored heat alone cannot sustain a flame after combustion stops.
                if (!float.IsFinite(watts) || watts <= 0)
                { emitter.Root.SetActive(false); continue; }
                float maximum = compartment == SpacecraftCompartment.Cabin ? 250000
                    : compartment == SpacecraftCompartment.Airlock ? 40000 : 80000;
                float level = Mathf.Sqrt(Mathf.Clamp01(watts / maximum));
                Draw(emitter, Source(compartment), localEye, level, i);
                visible = true;
            }
            _root!.SetActive(visible);
        }
        catch (Exception error)
        {
            _failed = true;
            UnityEngine.Debug.LogWarning("Modstoke Client: ship fire effects are unavailable: " + error);
            Release();
        }
    }

    private void Draw(Emitter emitter, Vector3 source, Vector3 eye, float level, int index)
    {
        var toward = source - eye;
        toward.y = 0;
        var rotation = toward.sqrMagnitude > .001f ? Quaternion.LookRotation(toward) : Quaternion.identity;
        float phase = _time + index * .173f;
        emitter.Properties.SetColor(BaseColor, new Color(0, 0, 0, .62f * level));
        emitter.Properties.SetColor(EmissiveColor, new Color(1, .67f, .33f) * ((2.5f + level * 3.5f) * level));
        emitter.Root.SetActive(true);
        for (int layer = 0; layer < FlameLayers; layer++)
        {
            float cycle = phase * (1.15f + level * .2f) + layer * .37f;
            float pulse = .84f + .16f * Mathf.Sin(cycle * Mathf.PI * 2);
            float width = (.18f + level * .20f) * (1 - layer * .15f) * pulse;
            float height = (.25f + level * .65f) * (1 - layer * .18f) * (1 + .06f * Mathf.Sin(cycle * 7));
            var card = emitter.Cards[layer];
            // The full billboard sweep stays within .226 m of its source and below
            // the native airlock ceiling; the port wall and centre aisle remain clear.
            card.localPosition = new Vector3(Mathf.Sin(layer * 2.4f + phase * 1.1f) * .025f,
                layer * .025f, Mathf.Cos(layer * 2.4f + phase * .9f) * .025f);
            card.localRotation = rotation;
            card.localScale = new Vector3(width, height, 1);
            emitter.Filters[layer].sharedMesh = _frames[(int)(phase * 12 + layer * 2) % FrameCount];
            emitter.Flames[layer].SetPropertyBlock(emitter.Properties);
        }
    }

    private void Ensure()
    {
        if (_root != null) return;
        var shader = Shader.Find("HDRP/Lit");
        if (!shader) throw new InvalidOperationException("Ship fire effects require HDRP/Lit.");
        _root = new GameObject("Ship fire") { layer = _body.Root.layer };
        _root.transform.SetParent(_body.Root.transform, false);
        _root.SetActive(false);
        _texture = BoxStationFireEffects.LoadTexture("flame", 1024, 768);
        _material = BoxStationFireEffects.CreateMaterial(shader, _texture, true);
        for (int frame = 0; frame < FrameCount; frame++)
            _frames[frame] = BoxStationFireEffects.CreateQuad(frame % 4 / 4f, frame / 4 / 2f, .25f, .5f, false);
        for (int i = 0; i < _emitters.Length; i++)
        {
            var node = new GameObject("Ship compartment flame") { layer = _root.layer };
            node.transform.SetParent(_root.transform, false);
            node.transform.localPosition = Source((SpacecraftCompartment)i);
            node.SetActive(false);
            var emitter = _emitters[i] = new Emitter(node);
            for (int layer = 0; layer < FlameLayers; layer++)
            {
                emitter.Flames[layer] = BoxStationFireEffects.AddCard(node.transform, _frames[0]!, _material, out var filter);
                emitter.Filters[layer] = filter;
                emitter.Cards[layer] = filter.transform;
            }
        }
    }

    private void Hide()
    {
        if (_root != null && _root) _root.SetActive(false);
    }

    private void Release()
    {
        Exception? failure = null;
        try { Hide(); }
        catch (Exception error) { failure = error; }
        foreach (var resource in new UnityEngine.Object?[] { _root, _material, _texture })
        {
            try { if (resource != null && resource) UnityEngine.Object.Destroy(resource); }
            catch (Exception error) { failure ??= error; }
        }
        foreach (var mesh in _frames)
        {
            try { if (mesh != null && mesh) UnityEngine.Object.Destroy(mesh); }
            catch (Exception error) { failure ??= error; }
        }
        _root = null;
        _material = null;
        _texture = null;
        Array.Clear(_frames);
        Array.Clear(_emitters);
        if (failure != null) UnityEngine.Debug.LogWarning("Modstoke Client: ship fire effect cleanup: " + failure);
    }

    public void Dispose()
    {
        if (_disposed || Environment.CurrentManagedThreadId != _thread) return;
        _disposed = true;
        Release();
    }

    private sealed class Emitter
    {
        internal readonly GameObject Root;
        internal readonly MeshRenderer[] Flames = new MeshRenderer[FlameLayers];
        internal readonly MeshFilter[] Filters = new MeshFilter[FlameLayers];
        internal readonly Transform[] Cards = new Transform[FlameLayers];
        internal readonly MaterialPropertyBlock Properties = new();

        internal Emitter(GameObject root) => Root = root;
    }
}
