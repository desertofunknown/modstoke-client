// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Modstoke.Sons;
using TheForest.Utils;
using UnityEngine;
using UnityEngine.Rendering;

namespace Modstoke.Client;

internal sealed class ClientSpacecraftEffects : IDisposable
{
    internal static readonly Vector3 HatchOutlet = new(0, -1.55f, -15.6f);
    // Assign the valve to the native starboard roof grille, clear of the walking route.
    internal static readonly Vector3 VentOutlet = new(1.2f, 3.3f, -7.76f);
    private const int HatchPuffs = 12;
    private static readonly Vector3[] Turbines =
    {
        new(-5.23f, 1.43f, -15.85f), new(5.23f, 1.43f, -15.85f),
        new(-3.35f, 3.49f, -14.36f), new(3.35f, 3.49f, -14.36f)
    };
    private readonly Transform _ship;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly Transform[] _plumes = new Transform[8];
    private readonly Transform[] _rings = new Transform[3];
    private readonly Transform[] _streaks = new Transform[24];
    private readonly Transform[] _vents = new Transform[16], _smokePuffs = new Transform[6], _sparks = new Transform[8];
    private GameObject? _root;
    private Mesh? _plumeMesh, _ringMesh;
    private Mesh? _puffMesh;
    private Texture2D? _puffTexture;
    private Material? _exhaust, _core, _warp;
    private Material? _mist, _smoke, _sparkMaterial;
    private SpacecraftFlightState _previous;
    private float _lastUpdate = -1, _time, _thrust, _arrival;
    private float _hatchLevel, _ventLevel, _impact;
    private bool _observed, _failed, _disposed;

    internal ClientSpacecraftEffects(Transform ship) => _ship = ship;

    internal void Update(SpacecraftFlightState state)
    {
        if (_disposed || _failed || Environment.CurrentManagedThreadId != _thread) return;
        try
        {
            if (!_ship || !_ship.gameObject.activeInHierarchy || GameRuntime.IsTitleScene
                || GameRuntime.State.PauseMenuOpen || Time.timeScale <= 0)
            { Suspend(); return; }
            Ensure();
            var now = Time.realtimeSinceStartup;
            var dt = _lastUpdate < 0 ? 1f / 60 : Mathf.Clamp(now - _lastUpdate, .001f, .1f);
            if (_lastUpdate >= 0 && now - _lastUpdate > .5f) _observed = false;
            _lastUpdate = now;
            _time += dt;
            var travel = state.Travel != SpacecraftTravel.None;
            var acceleration = _observed ? (state.Velocity - _previous.Velocity).magnitude / dt : 0;
            var demand = state.Powered || travel ? travel ? 1f : .12f
                + .88f * Mathf.Clamp01(acceleration / 18 + state.AngularVelocity.magnitude * .25f) : 0;
            if (!float.IsFinite(demand)) demand = 0;
            _thrust = Mathf.MoveTowards(_thrust, demand, dt * 2.5f);
            var progress = float.IsFinite(state.TravelProgress) ? Mathf.Clamp01(state.TravelProgress) : 0;
            if (_observed && !travel && _previous.Travel != SpacecraftTravel.None
                && state.Region != _previous.Region) _arrival = 1;
            _arrival = Mathf.MoveTowards(_arrival, 0, dt * 1.25f);
            var damageVisible = UpdateDamage(state, dt);
            _root!.SetActive(_thrust > .001f || travel || _arrival > 0 || damageVisible);

            for (var i = 0; i < Turbines.Length; i++)
            {
                var pulse = 1 + .06f * Mathf.Sin(_time * 27 + i * 1.7f);
                var length = (.6f + _thrust * 5.5f) * pulse;
                var radius = (i < 2 ? .42f : .31f) * Mathf.Sqrt(_thrust);
                _plumes[i * 2].localScale = new Vector3(radius, radius, length);
                _plumes[i * 2 + 1].localScale = new Vector3(radius * .46f, radius * .46f, length * .7f);
                _plumes[i * 2].gameObject.SetActive(_thrust > .005f);
                _plumes[i * 2 + 1].gameObject.SetActive(_thrust > .005f);
            }

            var charge = travel ? Mathf.SmoothStep(0, 1, Mathf.Clamp01(progress / .45f)) : 0;
            for (var i = 0; i < _rings.Length; i++)
            {
                var ring = _rings[i];
                ring.gameObject.SetActive(travel || _arrival > 0);
                var cycle = Mathf.Repeat(_time * (.25f + charge * .9f) + i / 3f, 1);
                var radius = travel ? 1 + cycle * .18f : 1 + (1 - _arrival) * 1.1f;
                ring.localScale = new Vector3((11.5f + charge) * radius,
                    (6.8f + charge * .5f) * radius, 1);
                ring.localPosition = new Vector3(0, .52f, travel ? Mathf.Lerp(13, -20, cycle) : -2.5f + i * 3);
                ring.localRotation = Quaternion.Euler(0, 0, _time * 12 + i * 30);
            }
            var streakPower = travel ? Mathf.SmoothStep(0, 1, Mathf.Clamp01((progress - .3f) / .4f)) : _arrival;
            for (var i = 0; i < _streaks.Length; i++)
            {
                var streak = _streaks[i];
                streak.gameObject.SetActive(streakPower > .01f);
                var angle = i * 2.399963f;
                var radius = 1 + (i % 5) * .13f;
                var cycle = Mathf.Repeat(_time * (.45f + streakPower) + i * .618034f, 1);
                streak.localPosition = new Vector3(Mathf.Cos(angle) * 15 * radius,
                    .52f + Mathf.Sin(angle) * 9 * radius, Mathf.Lerp(35, -45, cycle));
                // A narrow tapered tail also fades through scale at each wrap point.
                var width = .045f * streakPower * Mathf.Sin(cycle * Mathf.PI);
                streak.localScale = new Vector3(width, width, (4 + streakPower * 15) * (1 + i % 3 * .18f));
            }
            SetColor(_warp!, new Color(.25f, .56f, 1, .12f + .26f * Mathf.Max(charge, _arrival)),
                1.5f + Mathf.Max(charge, _arrival) * 3);
            _previous = state;
            _observed = true;
        }
        catch (Exception error)
        {
            _failed = true;
            UnityEngine.Debug.LogWarning("Modstoke Client: spacecraft effects are unavailable: " + error);
            Release();
        }
    }

    private void Ensure()
    {
        if (_root != null && _root) return;
        var shader = Shader.Find("HDRP/Lit");
        if (!shader) throw new InvalidOperationException("Spacecraft effects require the HDRP/Lit shader.");
        _root = new GameObject("Spacecraft effects") { layer = _ship.gameObject.layer };
        _root.transform.SetParent(_ship, false);
        _root.SetActive(false);
        _exhaust = CreateMaterial(shader, "Spacecraft exhaust", new Color(.13f, .43f, 1, .22f), 3);
        _core = CreateMaterial(shader, "Spacecraft exhaust core", new Color(.65f, .87f, 1, .5f), 5);
        _warp = CreateMaterial(shader, "Spacecraft warp", new Color(.25f, .56f, 1, .2f), 2);
        _plumeMesh = CreatePlume();
        _ringMesh = CreateRing();
        for (var i = 0; i < Turbines.Length; i++)
        {
            _plumes[i * 2] = AddMesh("Turbine plume", _plumeMesh, _exhaust, Turbines[i]);
            _plumes[i * 2 + 1] = AddMesh("Turbine core", _plumeMesh, _core, Turbines[i] - Vector3.forward * .03f);
        }
        for (var i = 0; i < _rings.Length; i++)
            _rings[i] = AddMesh("Warp charge ring", _ringMesh, _warp, Vector3.zero);
        for (var i = 0; i < _streaks.Length; i++)
            _streaks[i] = AddMesh("Warp streak", _plumeMesh, _warp, Vector3.zero);
        _puffTexture = CreatePuffTexture();
        _puffMesh = CreateMesh("Spacecraft vapor puff",
            new[] { new Vector3(-.5f, -.5f, 0), new Vector3(.5f, -.5f, 0), new Vector3(.5f, .5f, 0), new Vector3(-.5f, .5f, 0) },
            new[] { 0, 1, 2, 0, 2, 3 });
        _puffMesh.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
        _mist = CreateMaterial(shader, "Spacecraft vent vapor", new Color(.76f, .86f, .94f, .3f), .15f);
        _smoke = CreateMaterial(shader, "Spacecraft damage smoke", new Color(.13f, .15f, .17f, .32f), 0);
        _sparkMaterial = CreateMaterial(shader, "Spacecraft impact sparks", new Color(1, .48f, .09f, .8f), 4);
        _mist.SetTexture("_BaseColorMap", _puffTexture);
        _smoke.SetTexture("_BaseColorMap", _puffTexture);
        for (var i = 0; i < _vents.Length; i++)
            _vents[i] = AddMesh(i < HatchPuffs ? "Airlock hatch vapor" : "Airlock valve vapor", _puffMesh, _mist, Vector3.zero);
        for (var i = 0; i < _smokePuffs.Length; i++)
            _smokePuffs[i] = AddMesh("Damaged engine smoke", _puffMesh, _smoke, Vector3.zero);
        for (var i = 0; i < _sparks.Length; i++)
            _sparks[i] = AddMesh("Hull impact spark", _plumeMesh, _sparkMaterial, Vector3.zero);
    }

    private bool UpdateDamage(SpacecraftFlightState state, float dt)
    {
        var systems = state.Systems;
        // Separate the two outlets without duplicating the simulation's vent coefficients.
        var hatchFlow = ClientSpacecraftSystems.Outflow(state with { Systems = systems with { AirlockVenting = false } });
        var valveFlow = Mathf.Max(0, ClientSpacecraftSystems.Outflow(state) - hatchFlow);
        var opening = float.IsFinite(state.DoorAmount) ? Mathf.Clamp01(state.DoorAmount) : 0;
        _hatchLevel = opening > .01f && hatchFlow > 0
            ? Mathf.MoveTowards(_hatchLevel, Mathf.Clamp01(hatchFlow / 40), dt * 6) : 0;
        _ventLevel = systems.AirlockVenting && valveFlow > 0
            ? Mathf.MoveTowards(_ventLevel, Mathf.Clamp01(valveFlow / 40), dt * 6) : 0;
        if (systems.Ready && _observed && _previous.Systems.Ready)
        {
            var damage = Mathf.Max(_previous.Systems.HullIntegrity01 - systems.HullIntegrity01,
                _previous.Systems.EngineIntegrity01 - systems.EngineIntegrity01);
            if (damage > .015f) _impact = Mathf.Clamp01(damage * 12 + .35f);
        }
        _impact = Mathf.MoveTowards(_impact, 0, dt * .8f);
        var engineSmoke = systems.Ready && state.Powered ? Mathf.Clamp01((.6f - systems.EngineIntegrity01) / .5f) : 0;
        var smoke = ClientSpacecraftSystems.Breathable(state.Region) ? Mathf.Max(engineSmoke, _impact * .7f) : 0;
        var camera = LocalPlayer.MainCam;
        var billboard = camera != null && !camera.WasCollected && camera
            ? Quaternion.Inverse(_ship.rotation) * camera.transform.rotation : Quaternion.identity;
        // The closed ramp reaches Y -0.683; the header's bottom starts at -1.005.
        // Wait for their overlap to clear, then widen the jet as the ramp lowers.
        var rampOpening = Mathf.SmoothStep(0, 1, Mathf.Clamp01((opening - .25f) / .5f));
        var headerLift = .45f * Mathf.SmoothStep(0, 1, Mathf.Clamp01(opening / .25f));
        var headerGap = Mathf.Max(0, headerLift - .34f);
        var outlet = HatchOutlet;
        outlet.y = Mathf.Lerp(-.674f + headerGap * .5f, HatchOutlet.y, rampOpening);
        var aperture = Mathf.Lerp(headerGap * .65f, .65f, rampOpening);
        var hatchLevel = _hatchLevel * Mathf.Clamp01(aperture / .06f);
        for (var i = 0; i < _vents.Length; i++)
        {
            var puff = _vents[i];
            var hatch = i < HatchPuffs;
            var level = hatch ? hatchLevel : _ventLevel;
            puff.gameObject.SetActive(level > .01f);
            if (level <= .01f) continue;
            var interior = hatch && i < HatchPuffs / 2;
            var count = hatch ? HatchPuffs / 2 : _vents.Length - HatchPuffs;
            var index = hatch ? i % count : i - HatchPuffs;
            var age = Mathf.Repeat(_time * (1.2f + level * .8f) + index / (float)count, 1);
            var spread = interior ? aperture * .3f : hatch ? age * .75f * rampOpening : age * .3f;
            var swirl = new Vector3(Mathf.Sin(i * 2.4f + age * 2) * spread,
                Mathf.Cos(i * 2.4f + age * 2) * spread * .45f, 0);
            var size = interior ? aperture * (.55f + age * .35f)
                : hatch ? aperture + age * 1.25f : .18f + age * .85f;
            if (interior)
                // Stay aft of the inner hatch even when it is sealed.
                puff.localPosition = Vector3.Lerp(new Vector3(-.094f, -1.15f, -9.9f), outlet, age) + swirl;
            else if (hatch)
                puff.localPosition = outlet + Vector3.back * (age * 4.5f) + swirl;
            else
                puff.localPosition = VentOutlet + new Vector3(0, age * 2.8f, -age) + swirl;
            puff.localRotation = billboard;
            puff.localScale = Vector3.one * (size * Mathf.Sin(age * Mathf.PI) * level);
        }
        for (var i = 0; i < _smokePuffs.Length; i++)
        {
            var puff = _smokePuffs[i];
            puff.gameObject.SetActive(smoke > .01f);
            if (smoke <= .01f) continue;
            var age = Mathf.Repeat(_time * .45f + i / (float)_smokePuffs.Length, 1);
            var side = i % 2 == 0 ? -1 : 1;
            // Damaged engine housings vent outside the cabin; no interior occluding cloud.
            puff.localPosition = new Vector3(side * (5.2f + age * .5f), 1.7f + age * 2.4f, -15.7f - age * 2);
            puff.localRotation = billboard;
            puff.localScale = Vector3.one * ((.6f + age * 2.2f) * Mathf.Sin(age * Mathf.PI) * smoke);
        }
        for (var i = 0; i < _sparks.Length; i++)
        {
            var spark = _sparks[i];
            spark.gameObject.SetActive(_impact > .01f);
            if (_impact <= .01f) continue;
            var age = 1 - _impact;
            var side = i % 2 == 0 ? -1 : 1;
            var direction = new Vector3(Mathf.Sin(i * 2.4f) * .8f, .3f + (i % 3) * .25f, -.8f).normalized;
            var gravity = state.Region == SpacecraftRegion.Space ? 0 : age * age * 1.5f;
            spark.localPosition = new Vector3(side * 5.25f, 1.5f, -15.85f) + direction * (age * 4) - Vector3.up * gravity;
            spark.localRotation = Quaternion.LookRotation(direction);
            spark.localScale = new Vector3(.025f * _impact, .025f * _impact, .3f + _impact * .5f);
        }
        return hatchLevel > .01f || _ventLevel > .01f || smoke > .01f || _impact > .01f;
    }

    private static Texture2D CreatePuffTexture()
    {
        const int size = 32;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false, false) { name = "Spacecraft vapor falloff" };
        try
        {
            var pixels = new Color[size * size];
            for (var y = 0; y < size; y++)
                for (var x = 0; x < size; x++)
                {
                    var dx = (x + .5f) / size * 2 - 1;
                    var dy = (y + .5f) / size * 2 - 1;
                    var falloff = Mathf.Clamp01(1 - dx * dx - dy * dy);
                    pixels[y * size + x] = new Color(1, 1, 1, falloff * falloff * (.85f + .15f * Mathf.Sin(x * 1.3f + y * .7f)));
                }
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            texture.SetPixels(pixels);
            texture.Apply(false, true);
            return texture;
        }
        catch { UnityEngine.Object.Destroy(texture); throw; }
    }

    private Transform AddMesh(string name, Mesh mesh, Material material, Vector3 position)
    {
        var node = new GameObject(name) { layer = _root!.layer };
        node.transform.SetParent(_root.transform, false);
        node.transform.localPosition = position;
        node.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = node.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.lightProbeUsage = LightProbeUsage.Off;
        renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        node.SetActive(false);
        return node.transform;
    }

    private static Material CreateMaterial(Shader shader, string name, Color color, float emission)
    {
        var material = new Material(shader) { name = name };
        try
        {
            // Match the aircraft's owned HDRP transparent materials; never edit native turbine materials.
            material.SetFloat("_SurfaceType", 1);
            material.SetFloat("_BlendMode", 0);
            material.SetFloat("_SrcBlend", (float)BlendMode.One);
            material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            material.SetFloat("_AlphaSrcBlend", (float)BlendMode.One);
            material.SetFloat("_AlphaDstBlend", (float)BlendMode.OneMinusSrcAlpha);
            material.SetFloat("_ZWrite", 0);
            material.SetFloat("_TransparentZWrite", 0);
            material.SetFloat("_ZTestDepthEqualForOpaque", (float)CompareFunction.LessEqual);
            material.SetFloat("_ZTestTransparent", (float)CompareFunction.LessEqual);
            material.SetFloat("_CullMode", (float)CullMode.Off);
            material.SetFloat("_CullModeForward", (float)CullMode.Off);
            material.SetFloat("_DoubleSidedEnable", 1);
            material.SetFloat("_DoubleSidedNormalMode", 2);
            material.SetVector("_DoubleSidedConstants", new Vector4(1, 1, 1, 0));
            material.SetFloat("_Metallic", 0);
            material.SetFloat("_Smoothness", 0);
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.EnableKeyword("_BLENDMODE_ALPHA");
            material.EnableKeyword("_ENABLE_FOG_ON_TRANSPARENT");
            material.EnableKeyword("_DOUBLESIDED_ON");
            material.SetShaderPassEnabled("TransparentDepthPrepass", false);
            material.SetShaderPassEnabled("TransparentDepthPostpass", false);
            material.SetShaderPassEnabled("ShadowCaster", false);
            material.renderQueue = (int)RenderQueue.Transparent;
            SetColor(material, color, emission);
            return material;
        }
        catch { UnityEngine.Object.Destroy(material); throw; }
    }

    private static void SetColor(Material material, Color color, float emission)
    {
        material.SetColor("_BaseColor", color);
        material.SetColor("_EmissiveColor", new Color(color.r, color.g, color.b) * emission);
    }

    private static Mesh CreatePlume()
    {
        const int sides = 16;
        var vertices = new Vector3[sides * 2 + 1];
        var triangles = new int[sides * 9];
        for (var i = 0; i < sides; i++)
        {
            var angle = i * Mathf.PI * 2 / sides;
            vertices[i] = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0);
            vertices[sides + i] = new Vector3(Mathf.Cos(angle) * .6f, Mathf.Sin(angle) * .6f, -.45f);
            var next = (i + 1) % sides;
            var offset = i * 9;
            triangles[offset] = i; triangles[offset + 1] = next; triangles[offset + 2] = sides + i;
            triangles[offset + 3] = next; triangles[offset + 4] = sides + next; triangles[offset + 5] = sides + i;
            triangles[offset + 6] = sides + i; triangles[offset + 7] = sides + next; triangles[offset + 8] = sides * 2;
        }
        vertices[sides * 2] = new Vector3(0, 0, -1);
        return CreateMesh("Spacecraft tapered plume", vertices, triangles);
    }

    private static Mesh CreateRing()
    {
        const int segments = 80;
        var vertices = new Vector3[segments * 2];
        var triangles = new int[segments * 6];
        for (var i = 0; i < segments; i++)
        {
            var angle = i * Mathf.PI * 2 / segments;
            var direction = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0);
            vertices[i * 2] = direction * .995f;
            vertices[i * 2 + 1] = direction * 1.005f;
            var next = (i + 1) % segments * 2;
            var offset = i * 6;
            triangles[offset] = i * 2; triangles[offset + 1] = next; triangles[offset + 2] = i * 2 + 1;
            triangles[offset + 3] = next; triangles[offset + 4] = next + 1; triangles[offset + 5] = i * 2 + 1;
        }
        return CreateMesh("Spacecraft charge ring", vertices, triangles);
    }

    private static Mesh CreateMesh(string name, Vector3[] vertices, int[] triangles)
    {
        var mesh = new Mesh { name = name };
        try
        {
            mesh.vertices = vertices;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
        catch { UnityEngine.Object.Destroy(mesh); throw; }
    }

    internal void Suspend()
    {
        if (_disposed || Environment.CurrentManagedThreadId != _thread) return;
        if (_root != null && _root) _root.SetActive(false);
        _observed = false;
        _thrust = _arrival = _hatchLevel = _ventLevel = _impact = 0;
        _lastUpdate = -1;
    }

    private void Release()
    {
        Exception? failure = null;
        try { if (_root != null && _root) _root.SetActive(false); }
        catch (Exception error) { failure = error; }
        // Cleanup is isolated so one invalid native wrapper cannot strand the other owned resources.
        foreach (var resource in new UnityEngine.Object?[]
            { _root, _plumeMesh, _ringMesh, _puffMesh, _puffTexture, _exhaust, _core, _warp, _mist, _smoke, _sparkMaterial })
        {
            try { if (resource != null && resource) UnityEngine.Object.Destroy(resource); }
            catch (Exception error) { failure ??= error; }
        }
        _root = null;
        _plumeMesh = _ringMesh = null;
        _puffMesh = null; _puffTexture = null;
        _exhaust = _core = _warp = null;
        _mist = _smoke = _sparkMaterial = null;
        Array.Clear(_plumes); Array.Clear(_rings); Array.Clear(_streaks);
        Array.Clear(_vents); Array.Clear(_smokePuffs); Array.Clear(_sparks);
        if (failure != null) UnityEngine.Debug.LogWarning("Modstoke Client: spacecraft effect cleanup: " + failure);
    }

    public void Dispose()
    {
        if (_disposed || Environment.CurrentManagedThreadId != _thread) return;
        _disposed = true;
        Release();
    }
}
