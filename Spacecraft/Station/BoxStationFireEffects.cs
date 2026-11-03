// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.IO;
using Modstoke.Sons;
using TheForest.Utils;
using UnityEngine;
using UnityEngine.Rendering;

namespace Modstoke.Client;

internal sealed class BoxStationFireEffects : IDisposable
{
    private const int EmitterCount = 12, FlameLayers = 3, FrameCount = 8, CellRadius = 8;
    private const float ViewDistance = 18;
    private static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
    private static readonly int EmissiveColor = Shader.PropertyToID("_EmissiveColor");
    private readonly BoxStationModel _model;
    private readonly BoxStationLayout _layout;
    private readonly Transform _station;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly bool[] _eligible;
    private readonly bool[] _doorCells;
    private readonly int[] _selected = new int[EmitterCount];
    private readonly float[] _distances = new float[EmitterCount];
    private readonly Emitter?[] _emitters = new Emitter[EmitterCount];
    private readonly Mesh?[] _frames = new Mesh[FrameCount];
    private BoxStationHullState _lastHull;
    private GameObject? _root;
    private Mesh? _steamMesh;
    private Material? _flameMaterial, _steamMaterial;
    private Texture2D? _flameTexture, _steamTexture;
    private float _lastUpdate = -1, _nextSelection, _time;
    private long _retryAt, _nextFailureLog;
    private int _failures;
    private bool _disposed;

    // The station service updates and disposes these owned Unity objects on its main thread.
    internal BoxStationFireEffects(BoxStationModel model)
    {
        _model = model;
        _layout = model.Layout;
        _station = model.Root.transform;
        _eligible = new bool[_layout.Cells.Length];
        _doorCells = new bool[_layout.Cells.Length];
        // Door leaves can occupy the centre of an otherwise valid floor cell.
        foreach (var door in _layout.Doors) _doorCells[door.Cell] = true;
        foreach (var cell in _layout.Cells) RefreshCell(cell.Id);
        Array.Fill(_selected, -1);
    }

    internal void Update(BoxStationState? state)
    {
        if (_disposed || Environment.CurrentManagedThreadId != _thread || Environment.TickCount64 < _retryAt) return;
        try
        {
            var camera = LocalPlayer.MainCam;
            if (!_model.Visible || !_model.Root.activeInHierarchy || GameRuntime.IsTitleScene
                || GameRuntime.State.PauseMenuOpen || Time.timeScale <= 0
                || camera == null || camera.WasCollected || !camera)
            { Hide(); return; }

            float now = Time.realtimeSinceStartup;
            float dt = _lastUpdate < 0 ? 1f / 60 : Mathf.Clamp(now - _lastUpdate, 0, .1f);
            _lastUpdate = now;
            _time = Mathf.Repeat(_time + dt, 120);
            // The caller already expires transport snapshots. Gas must also match
            // the hull revision that owns the currently visible cells and barriers.
            var snapshot = state.GetValueOrDefault();
            var hull = _model.HullState;
            if (!state.HasValue || snapshot.LayoutHash != _layout.LayoutHash
                || snapshot.Zones.IsDefault || snapshot.RoomSpans.IsDefault || snapshot.HullRevision != hull.Revision)
            { Hide(); return; }
            RefreshEligibleCells(hull);

            var eye = camera.transform.position;
            var localEye = _station.InverseTransformPoint(eye);
            var forward = _station.InverseTransformDirection(camera.transform.forward);
            if (now >= _nextSelection)
            {
                SelectCells(snapshot, localEye, forward, eye);
                _nextSelection = now + .2f;
            }
            if (_root is not null && (_root.WasCollected || !_root))
                throw new InvalidOperationException("The station fire effect root is no longer available.");
            if (_root == null)
            {
                if (_selected[0] < 0) return;
                Ensure();
            }
            AssignCells();
            bool visible = false;
            foreach (var emitter in _emitters)
            {
                if (emitter == null || emitter.Cell < 0) continue;
                if (!_eligible[emitter.Cell]
                    || !BoxStationSystems.TryGetAtmosphere(_layout, snapshot, emitter.Cell, out var gas))
                {
                    emitter.Root.SetActive(false);
                    emitter.Cell = -1;
                    emitter.Level = 0;
                    continue;
                }
                float demand = Selected(emitter.Cell) ? Intensity(gas) : 0;
                emitter.Level = Mathf.MoveTowards(emitter.Level, demand, dt * (demand > emitter.Level ? 2.5f : 1.8f));
                if (emitter.Level <= .001f)
                {
                    emitter.Root.SetActive(false);
                    emitter.Cell = -1;
                    continue;
                }
                visible = true;
                Draw(emitter, gas, localEye);
            }
            _root!.SetActive(visible);
            _failures = 0;
            _retryAt = 0;
        }
        catch (Exception error)
        {
            _retryAt = Environment.TickCount64 + (500L << _failures);
            _failures = Math.Min(_failures + 1, 4);
            var cleanup = Release();
            LogFailure(cleanup == null ? error
                : new AggregateException("Station fire effects and their cleanup failed.", error, cleanup));
        }
    }

    private void LogFailure(Exception error)
    {
        var now = Environment.TickCount64;
        if (now < _nextFailureLog) return;
        _nextFailureLog = now + 30000;
        UnityEngine.Debug.LogWarning("Modstoke Client: station fire effects are unavailable: " + error);
    }

    private void RefreshEligibleCells(BoxStationHullState hull)
    {
        if (!_lastHull.Panels.IsDefault && _lastHull.Revision == hull.Revision) return;
        if (!_lastHull.Panels.IsDefault)
            foreach (var panel in _lastHull.Panels)
                RefreshTarget(panel.Target);
        foreach (var panel in hull.Panels)
            RefreshTarget(panel.Target);
        _lastHull = hull;
        _nextSelection = 0;
    }

    private void RefreshTarget(int target)
    {
        int cell = BoxStationHull.WallCell(_layout, target);
        if (cell < 0 && BoxStationHull.TryStructure(_layout, target, out var structure)) cell = structure.Cell;
        if (cell >= 0) RefreshCell(cell);
    }

    private void RefreshCell(int cell)
    {
        var flags = _model.CellFlags(cell);
        _eligible[cell] = !_doorCells[cell]
            && (flags & (BoxStationCellFlags.Floor | BoxStationCellFlags.Catwalk)) != 0
            && (flags & (BoxStationCellFlags.Wall | BoxStationCellFlags.FullWindow | BoxStationCellFlags.Grille)) == 0;
    }

    private void SelectCells(BoxStationState state, Vector3 eye, Vector3 forward, Vector3 worldEye)
    {
        Array.Fill(_selected, -1);
        Array.Fill(_distances, float.PositiveInfinity);
        int origin = _layout.CellAt(eye);
        if (origin < 0 || eye.y < -2 || eye.y > BoxStationLayout.DeckHeight + 2) return;
        var originCell = _layout.Cells[origin];
        int minX = Math.Max(0, originCell.X - CellRadius), maxX = Math.Min(_layout.Width - 1, originCell.X + CellRadius);
        int minRow = Math.Max(0, originCell.Row - CellRadius), maxRow = Math.Min(_layout.Height - 1, originCell.Row + CellRadius);
        for (int row = minRow; row <= maxRow; row++)
            for (int x = minX; x <= maxX; x++)
            {
                int cell = row * _layout.Width + x;
                if (!_eligible[cell] || !BoxStationSystems.TryGetAtmosphere(_layout, state, cell, out var gas)
                    || Intensity(gas) <= .001f) continue;
                var anchor = _layout.CellPosition(cell, 1.2f);
                var offset = anchor - eye;
                float distance = offset.sqrMagnitude;
                if (distance > ViewDistance * ViewDistance || distance >= _distances[^1]
                    || distance > 9 && Vector3.Dot(offset, forward) < Mathf.Sqrt(distance) * .15f) continue;
                // The bounded station query includes closed doors and thin edge barriers.
                // Its conservative capsule clearance also keeps hidden rooms out of the emitter budget.
                var worldAnchor = _station.TransformPoint(anchor);
                if (!_model.CrewPathClear(worldEye, worldEye, worldAnchor - worldEye)) continue;
                int slot = EmitterCount - 1;
                while (slot > 0 && distance < _distances[slot - 1])
                {
                    _selected[slot] = _selected[slot - 1];
                    _distances[slot] = _distances[slot - 1];
                    slot--;
                }
                _selected[slot] = cell;
                _distances[slot] = distance;
            }
    }

    private void AssignCells()
    {
        foreach (int cell in _selected)
        {
            if (cell < 0) break;
            bool assigned = false;
            foreach (var emitter in _emitters)
                if (emitter != null && emitter.Cell == cell) { assigned = true; break; }
            if (assigned) continue;
            foreach (var emitter in _emitters)
                if (emitter != null && emitter.Cell < 0)
                {
                    emitter.Cell = cell;
                    emitter.Transform.localPosition = _layout.CellPosition(cell);
                    break;
                }
        }
    }

    private bool Selected(int cell)
    {
        foreach (int selected in _selected) if (selected == cell) return true;
        return false;
    }

    private static float Intensity(OrbitalStationAtmosphere gas)
    {
        if (!float.IsFinite(gas.Pressure) || gas.Pressure <= .01f
            || !float.IsFinite(gas.CombustionWattsPerCubicMeter) || gas.CombustionWattsPerCubicMeter <= 0) return 0;
        return Mathf.Clamp01(Mathf.Log10(1 + gas.CombustionWattsPerCubicMeter / 250) / 3);
    }

    private void Draw(Emitter emitter, OrbitalStationAtmosphere gas, Vector3 eye)
    {
        var toward = _layout.CellPosition(emitter.Cell) - eye;
        toward.y = 0;
        var rotation = toward.sqrMagnitude > .001f ? Quaternion.LookRotation(toward) : Quaternion.identity;
        float phase = _time + emitter.Cell * .173f;
        float fuel = gas.PlasmaFraction + gas.TritiumFraction;
        if (float.IsFinite(fuel) && fuel > .000001f)
            emitter.Tint = Color.Lerp(new Color(1, .67f, .33f), new Color(.28f, .62f, 1),
                Mathf.Clamp01(gas.TritiumFraction / fuel));
        emitter.Properties.SetColor(BaseColor, new Color(0, 0, 0, .62f * emitter.Level));
        emitter.Properties.SetColor(EmissiveColor, emitter.Tint * ((2.5f + emitter.Level * 3.5f) * emitter.Level));
        emitter.Root.SetActive(true);
        for (int layer = 0; layer < FlameLayers; layer++)
        {
            var card = emitter.Cards[layer];
            float cycle = phase * (1.15f + emitter.Level * .2f) + layer * .37f;
            float pulse = .84f + .16f * Mathf.Sin(cycle * Mathf.PI * 2);
            float width = (.65f + emitter.Level * .35f) * (1 - layer * .15f) * pulse;
            float height = (.8f + emitter.Level * 1.35f) * (1 - layer * .18f) * (1 + .06f * Mathf.Sin(cycle * 7));
            // Vertical billboards cannot tilt through the deck or ceiling. The widest card
            // plus drift stays inside a .7 m radius, clear of every 1.2 m cell boundary.
            card.localPosition = new Vector3(Mathf.Sin(layer * 2.4f + phase * 1.1f) * .12f,
                .08f + layer * .11f, Mathf.Cos(layer * 2.4f + phase * .9f) * .12f);
            card.localRotation = rotation;
            card.localScale = new Vector3(width, height, 1);
            int frame = (int)(phase * 12 + layer * 2) % FrameCount;
            emitter.Filters[layer].sharedMesh = _frames[frame];
            emitter.Flames[layer].SetPropertyBlock(emitter.Properties);
        }

        // Water vapor is invisible while superheated; only cool supersaturation gets a pale mist.
        float steam = 0;
        if (gas.TemperatureC is > 0 and < 95 && float.IsFinite(gas.WaterVaporFraction))
        {
            float saturation = .61094f * Mathf.Exp(17.625f * gas.TemperatureC / (gas.TemperatureC + 243.04f));
            steam = Mathf.Clamp01((gas.Pressure * gas.WaterVaporFraction - saturation) / 5) * emitter.Level;
        }
        emitter.SteamObject.SetActive(steam > .005f);
        if (steam <= .005f) return;
        float age = Mathf.Repeat(phase * .45f, 1);
        emitter.SteamTransform.localPosition = new Vector3(Mathf.Sin(phase) * .15f, 1.5f + age * .7f, 0);
        emitter.SteamTransform.localRotation = rotation;
        emitter.SteamTransform.localScale = Vector3.one * (.55f + age * .45f);
        emitter.Properties.SetColor(BaseColor, new Color(.7f, .8f, .9f, steam * Mathf.Sin(age * Mathf.PI) * .16f));
        emitter.Properties.SetColor(EmissiveColor, Color.black);
        emitter.Steam.SetPropertyBlock(emitter.Properties);
    }

    private void Ensure()
    {
        var shader = Shader.Find("HDRP/Lit");
        if (!shader) throw new InvalidOperationException("Station fire effects require HDRP/Lit.");
        _root = new GameObject("Station gas fire") { layer = _model.Root.layer };
        _root.transform.SetParent(_station, false);
        _root.SetActive(false);
        _flameTexture = LoadTexture("flame", 1024, 768);
        _steamTexture = LoadTexture("steam", 256, 256);
        _flameMaterial = CreateMaterial(shader, _flameTexture, true);
        _steamMaterial = CreateMaterial(shader, _steamTexture, false);
        for (int frame = 0; frame < FrameCount; frame++)
            _frames[frame] = CreateQuad(frame % 4 / 4f, frame / 4 / 2f, .25f, .5f, false);
        _steamMesh = CreateQuad(0, 0, 1, 1, true);
        for (int i = 0; i < EmitterCount; i++)
        {
            var node = new GameObject("Gas flame cell") { layer = _root.layer };
            node.transform.SetParent(_root.transform, false);
            node.SetActive(false);
            var emitter = new Emitter(node);
            _emitters[i] = emitter;
            for (int layer = 0; layer < FlameLayers; layer++)
            {
                emitter.Flames[layer] = AddCard(node.transform, _frames[0]!, _flameMaterial, out var filter);
                emitter.Filters[layer] = filter;
                emitter.Cards[layer] = filter.transform;
            }
            emitter.Steam = AddCard(node.transform, _steamMesh, _steamMaterial, out _);
            emitter.SteamObject = emitter.Steam.gameObject;
            emitter.SteamTransform = emitter.Steam.transform;
        }
    }

    internal static MeshRenderer AddCard(Transform parent, Mesh mesh, Material material, out MeshFilter filter)
    {
        var node = new GameObject("Gas effect card") { layer = parent.gameObject.layer };
        node.transform.SetParent(parent, false);
        filter = node.AddComponent<MeshFilter>();
        filter.sharedMesh = mesh;
        var renderer = node.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.lightProbeUsage = LightProbeUsage.Off;
        renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        return renderer;
    }

    internal static Texture2D LoadTexture(string name, int width, int height)
    {
        using var stream = typeof(BoxStationFireEffects).Assembly.GetManifestResourceStream("Modstoke.Client.Assets.box-fire-" + name + ".png")
            ?? throw new InvalidDataException("The station fire texture is missing: " + name);
        if (!stream.CanSeek || stream.Length is < 24 or > 4 * 1024 * 1024)
            throw new InvalidDataException("The station fire texture size is invalid: " + name);
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, false) { name = "Station fire " + name };
        try
        {
            if (!ImageConversion.LoadImage(texture, bytes, false) || texture.width != width || texture.height != height)
                throw new InvalidDataException("The station fire texture dimensions are invalid: " + name);
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            texture.Apply(false, true);
            return texture;
        }
        catch { UnityEngine.Object.Destroy(texture); throw; }
    }

    internal static Material CreateMaterial(Shader shader, Texture2D texture, bool emissive)
    {
        var material = new Material(shader) { name = emissive ? "Station gas incandescence" : "Station condensed vapor" };
        try
        {
            material.SetFloat("_SurfaceType", 1);
            material.SetOverrideTag("RenderType", "Transparent");
            // Flames add radiance without darkening the room through dim texture pixels.
            material.SetFloat("_BlendMode", emissive ? 1 : 0);
            material.SetFloat("_SrcBlend", (float)BlendMode.One);
            material.SetFloat("_DstBlend", (float)(emissive ? BlendMode.One : BlendMode.OneMinusSrcAlpha));
            material.SetFloat("_AlphaSrcBlend", (float)BlendMode.One);
            material.SetFloat("_AlphaDstBlend", (float)(emissive ? BlendMode.One : BlendMode.OneMinusSrcAlpha));
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
            material.SetFloat("_EnableBlendModePreserveSpecularLighting", 0);
            material.SetTexture("_BaseColorMap", texture);
            material.SetColor("_BaseColor", Color.clear);
            material.SetColor("_EmissiveColor", Color.black);
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.EnableKeyword("_ENABLE_FOG_ON_TRANSPARENT");
            material.EnableKeyword("_DOUBLESIDED_ON");
            if (emissive)
            {
                material.SetTexture("_EmissiveColorMap", texture);
                material.EnableKeyword("_EMISSIVE_COLOR_MAP");
            }
            material.SetShaderPassEnabled("TransparentDepthPrepass", false);
            material.SetShaderPassEnabled("TransparentDepthPostpass", false);
            material.SetShaderPassEnabled("ShadowCaster", false);
            material.renderQueue = (int)RenderQueue.Transparent;
            return material;
        }
        catch { UnityEngine.Object.Destroy(material); throw; }
    }

    internal static Mesh CreateQuad(float u, float v, float width, float height, bool centered)
    {
        var mesh = new Mesh { name = "Station gas effect quad" };
        try
        {
            float bottom = centered ? -.5f : 0;
            mesh.vertices = new[] { new Vector3(-.5f, bottom, 0), new Vector3(.5f, bottom, 0),
                new Vector3(.5f, bottom + 1, 0), new Vector3(-.5f, bottom + 1, 0) };
            mesh.uv = new[] { new Vector2(u, v), new Vector2(u + width, v), new Vector2(u + width, v + height), new Vector2(u, v + height) };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
        catch { UnityEngine.Object.Destroy(mesh); throw; }
    }

    private void Hide()
    {
        if (_root != null && _root) _root.SetActive(false);
        foreach (var emitter in _emitters)
            if (emitter != null)
            {
                emitter.Root.SetActive(false);
                emitter.Cell = -1;
                emitter.Level = 0;
            }
        Array.Fill(_selected, -1);
        _nextSelection = 0;
        _lastUpdate = -1;
    }

    private Exception? Release()
    {
        Exception? failure = null;
        try { if (_root != null && _root) _root.SetActive(false); }
        catch (Exception error) { failure = error; }
        foreach (var resource in new UnityEngine.Object?[] { _root, _steamMesh, _flameMaterial, _steamMaterial, _flameTexture, _steamTexture })
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
        _steamMesh = null;
        _flameMaterial = _steamMaterial = null;
        _flameTexture = _steamTexture = null;
        Array.Clear(_frames);
        Array.Clear(_emitters);
        Array.Fill(_selected, -1);
        Array.Fill(_distances, float.PositiveInfinity);
        _nextSelection = _time = 0;
        _lastUpdate = -1;
        return failure;
    }

    public void Dispose()
    {
        if (_disposed || Environment.CurrentManagedThreadId != _thread) return;
        _disposed = true;
        if (Release() is { } error) LogFailure(error);
    }

    private sealed class Emitter
    {
        internal readonly GameObject Root;
        internal readonly Transform Transform;
        internal readonly MeshRenderer[] Flames = new MeshRenderer[FlameLayers];
        internal readonly MeshFilter[] Filters = new MeshFilter[FlameLayers];
        internal readonly Transform[] Cards = new Transform[FlameLayers];
        internal readonly MaterialPropertyBlock Properties = new();
        internal MeshRenderer Steam = null!;
        internal GameObject SteamObject = null!;
        internal Transform SteamTransform = null!;
        internal int Cell = -1;
        internal float Level;
        internal Color Tint = new(1, .67f, .33f);

        internal Emitter(GameObject root)
        {
            Root = root;
            Transform = root.transform;
        }
    }
}
