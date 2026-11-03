// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using BepInEx.Logging;
using Bolt;
using Modstoke.Sons;
using Sons.Crafting.Structures;
using Sons.Save;
using UnityEngine;

namespace Modstoke.Client;

internal sealed class ClientSpacecraftStorage : IDisposable
{
    private const int ShelfPrefabId = 182;
    private const int ShelfRecipeId = 49;
    private const string ShelfSerializer = "3e8a4eba-f4a1-4e95-8525-2098852559c0";
    private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("Modstoke spacecraft storage");
    // Face the aisle along the starboard wall, clear of the aft scanner and doorway.
    private static readonly Vector3 DockPosition = new(2.8f, -1.24f, -3.6f);
    private static readonly Quaternion DockRotation = Quaternion.Euler(0, -90, 0);
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private GameObject? _root;
    private BoltEntity? _entity;
    private SaveGameManager? _saveManager;
    private CargoRack? _rack;
    private bool _rackAttempted;
    private bool _authoritative, _networked, _attempted, _subscribed, _saved, _parkApplied, _failed, _disposed;

    internal ulong NetworkId { get; private set; }
    internal Transform? Root => Live(_root) ? _root.transform : null;
    internal bool Aboard { get; private set; }
    internal Vector3 ParkPosition { get; private set; }
    internal Quaternion ParkRotation { get; private set; } = Quaternion.identity;
    internal string Status { get; private set; } = "Cargo storage has not been deployed.";
    private bool OnThread => Environment.CurrentManagedThreadId == _thread;

    internal bool TryAttach(ClientSpacecraftBody body, Vector3 islandPosition, Quaternion islandRotation, out string status)
    {
        status = Status;
        if (!OnThread || _disposed || _failed || _saved || body?.Ready != true) return false;
        if (BoltNetwork.isRunning && !BoltNetwork.isServer)
            return Refuse("Cargo storage must be deployed by the host.", out status);
        if (!Finite(islandPosition) || islandPosition.y >= 2000 || !Valid(islandRotation))
            return Refuse("Cargo storage needs a safe island return point.", out status);
        try
        {
            if (!SaveLifecycle.State.HarmonyPatchesInstalled
                || !SaveGameManager.TryGetInstance(out var manager) || !Live(manager) || manager._loadingInProgress
                || !Live(ScrewStructureManager._instance))
                return Refuse("Cargo storage is unavailable until native world saving is ready.", out status);
            if (_attempted && (!Live(_root) || !SameWorld() || _networked != BoltNetwork.isRunning))
                return Refuse("The original cargo shelf is unavailable; no replacement was created.", out status);
            if (!TryParkPosition(islandPosition, islandRotation, out var parkPosition, out var parkRotation))
                return Refuse("Cargo storage needs clear dry ground beside the island return point.", out status);

            ParkPosition = parkPosition;
            ParkRotation = parkRotation;
            _parkApplied = false;
            _saveManager = manager;
            _authoritative = true;
            _networked = BoltNetwork.isRunning;
            if (!_subscribed) { SaveLifecycle.Lifecycle += OnSaveLifecycle; _subscribed = true; }
            if (!_attempted)
            {
                var lookup = PrefabDatabase._lookup;
                if (lookup == null || !lookup.TryGetValue(new PrefabId(ShelfPrefabId), out var prefab)
                    || !Shelf(prefab, true))
                    return Refuse("The installed native cargo shelf prefab is unavailable or incompatible.", out status);

                // The complete native prefab owns its slots, transfer callbacks and save data.
                // Keep an independent scene root: destroying the ship must never destroy cargo.
                _attempted = true;
                if (_networked)
                {
                    _entity = BoltNetwork.Instantiate(new PrefabId(ShelfPrefabId), ParkPosition, ParkRotation);
                    if (Live(_entity)) _root = _entity.gameObject;
                }
                else
                {
                    _root = UnityEngine.Object.Instantiate(prefab, ParkPosition, ParkRotation);
                    if (Live(_root)) _entity = _root.GetComponent<BoltEntity>();
                }
                if (!Shelf(_root, false) || !Registered(_root!))
                {
                    _failed = true;
                    return Refuse("The native cargo shelf could not register for world saving; it remains on the island.", out status);
                }
            }
            if (!CurrentEntity())
                return Refuse("Waiting for the host's native cargo shelf to attach.", out status);
            if (_networked) NetworkId = _entity!.networkId.PackedValue;
            Aboard = true;
            Follow(body);
            Status = status = "CARGO STORAGE READY";
            return true;
        }
        catch (Exception error)
        {
            Fail("deploy", error);
            status = Status;
            return false;
        }
    }

    // The caller admits the host's exact ship session and monotonically ordered snapshot.
    internal void Observe(ClientSpacecraftBody? body, ulong networkId, bool aboard, Vector3 parkPosition, Quaternion parkRotation)
    {
        if (!OnThread || _disposed || _authoritative || !BoltNetwork.isRunning || BoltNetwork.isServer
            || !Finite(parkPosition) || parkPosition.y >= 2000 || !Valid(parkRotation) || aboard && networkId == 0) return;
        if (NetworkId != networkId)
        {
            Park();
            _root = null; _entity = null; _failed = false;
            NetworkId = networkId;
            _parkApplied = false;
        }
        if (Aboard != aboard || ParkPosition != parkPosition || ParkRotation != parkRotation) _parkApplied = false;
        Aboard = aboard;
        ParkPosition = parkPosition;
        ParkRotation = parkRotation.normalized;
        _networked = true;
        Update(body);
    }

    internal void Update(ClientSpacecraftBody? body)
    {
        if (!OnThread || _disposed || _failed) return;
        try
        {
            if (!Live(_root)) ReleaseRack();
            if (!_authoritative && !Live(_root) && NetworkId != 0)
            {
                var entity = BoltNetwork.FindEntity(new NetworkId(NetworkId));
                if (!Live(entity) || !entity.isAttached) return;
                if (!Shelf(entity.gameObject, false))
                {
                    Aboard = false; _failed = true;
                    Status = "The host's cargo shelf identity is incompatible.";
                    return;
                }
                _entity = entity;
                _root = entity.gameObject;
            }
            if (!Live(_root)) return;
            if (!CurrentEntity() || _authoritative && !SameWorld()) { Park(); return; }
            if (Aboard && body?.Ready == true)
            {
                Follow(body);
                Status = "CARGO STORAGE READY";
            }
            else if (Aboard || !_parkApplied) Park();
        }
        catch (Exception error) { Fail("update", error); }
    }

    internal void Park()
    {
        if (!OnThread || _disposed) return;
        Aboard = false;
        try
        {
            if (!Live(_root)) return;
            Status = _saved
                ? "Cargo shelf parked at the island return point for saving; its contents remain in the native shelf."
                : "Cargo shelf and its contents are parked beside the island return point.";
            if (_parkApplied) return;
            // Do not disable, reparent, detach Bolt or destroy the native structure.
            _root!.transform.SetPositionAndRotation(ParkPosition, ParkRotation);
            _parkApplied = true;
        }
        finally { ReleaseRack(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        try { Park(); }
        catch (Exception error) { Log.LogError("Could not park the native cargo shelf during shutdown: " + error); }
        if (_subscribed) SaveLifecycle.Lifecycle -= OnSaveLifecycle;
        _subscribed = false;
        _disposed = true;
        _root = null; _entity = null; _saveManager = null;
    }

    private void OnSaveLifecycle(SaveLifecycleEvent value)
    {
        if (!OnThread || _disposed || !_authoritative || value.Phase != "Before") return;
        if (value.Operation == "Load") Park();
        else if (value.Operation == "Save" && value.SavePlayerOnly != true && Live(_root) && SameWorld())
        {
            // Before is dispatched inside the native Save prefix. Keep the shelf parked
            // afterwards because returning from Save does not prove asynchronous work ended.
            _saved = true;
            Park();
        }
    }

    private void Follow(ClientSpacecraftBody body)
    {
        var frame = body.Root.transform;
        _root!.transform.SetPositionAndRotation(frame.TransformPoint(DockPosition), frame.rotation * DockRotation);
        _parkApplied = false;
        if (_rackAttempted) return;
        _rackAttempted = true;
        try { _rack = new CargoRack(_root); }
        catch (Exception error)
        {
            // Presentation failure must not change cargo admission, authority or saving.
            Log.LogWarning("Could not present the spacecraft cargo rack; retaining the native shelf: " + error);
        }
    }

    private void ReleaseRack()
    {
        var rack = _rack;
        _rack = null; _rackAttempted = false;
        rack?.Dispose();
    }

    private bool CurrentEntity()
    {
        if (!Live(_root) || !Live(_entity)) return false;
        if (!_networked) return !BoltNetwork.isRunning;
        return BoltNetwork.isRunning && _entity!.isAttached && (!_authoritative || _entity.isOwner)
            && _entity.networkId.PackedValue != 0
            && (NetworkId == 0 || _entity.networkId.PackedValue == NetworkId)
            && BoltNetwork.FindEntity(_entity.networkId)?.Pointer == _entity.Pointer;
    }

    private bool SameWorld() => Live(_saveManager) && SaveGameManager.TryGetInstance(out var manager)
        && Live(manager) && manager.Pointer == _saveManager!.Pointer;

    private static bool Shelf(GameObject? root, bool prefab)
    {
        if (!Live(root) || root!.transform.parent || root.transform.localScale != Vector3.one) return false;
        var entity = root.GetComponent<BoltEntity>();
        var structure = root.GetComponent<ScrewStructureWithStorage>();
        if (!Live(entity) || entity._prefabId != ShelfPrefabId
            || !string.Equals(entity._serializerGuid, ShelfSerializer, StringComparison.OrdinalIgnoreCase)
            || !Live(structure) || !Live(structure._recipe) || structure._recipe.Id != ShelfRecipeId
            || structure._storageSlots == null || structure._storageSlots.Count != 12) return false;
        if (prefab && (!root.activeSelf || entity.isAttached || root.name != "ShelfStructure"
            || root.scene.IsValid() && root.scene.name != "DontDestroyOnLoad"
            || structure._recipe._builtPrefab?.Pointer != root.Pointer)) return false;
        for (var i = 0; i < structure._storageSlots.Count; i++)
        {
            var slot = structure._storageSlots[i];
            if (!Live(slot) || !slot.transform.IsChildOf(root.transform)) return false;
        }
        return true;
    }

    private static bool Registered(GameObject root)
    {
        var manager = ScrewStructureManager._instance;
        var structure = root.GetComponent<ScrewStructureWithStorage>().TryCast<IScrewStructure>();
        return Live(manager) && structure != null && manager._structures != null && manager._structures.Contains(structure);
    }

    private bool TryParkPosition(Vector3 anchor, Quaternion rotation, out Vector3 position, out Quaternion parkedRotation)
    {
        position = default;
        var forward = Vector3.ProjectOnPlane(rotation * Vector3.forward, Vector3.up);
        parkedRotation = forward.sqrMagnitude > .1f ? Quaternion.LookRotation(forward, Vector3.up) : Quaternion.identity;
        for (var i = 0; i < 6; i++)
        {
            var offset = new Vector3(i % 2 == 0 ? -3 : 3, 0, -(i / 2) * 2);
            var candidate = anchor + parkedRotation * offset;
            if (!Ground(candidate, out var ground)) continue;
            var highest = ground.y;
            var lowest = ground.y;
            var supported = true;
            for (var corner = 0; corner < 4; corner++)
            {
                var sample = candidate + parkedRotation * new Vector3(corner % 2 == 0 ? -.9f : .9f, 0, corner < 2 ? -.32f : .32f);
                if (!Ground(sample, out var point)) { supported = false; break; }
                highest = MathF.Max(highest, point.y); lowest = MathF.Min(lowest, point.y);
            }
            if (!supported || highest - lowest > .12f) continue;
            candidate.y = highest + .04f;
            var water = LayerMask.NameToLayer("Water");
            if (water < 0 || Physics.CheckBox(candidate, new Vector3(.95f, .08f, .36f), parkedRotation,
                1 << water, QueryTriggerInteraction.Collide)) continue;
            var clear = true;
            foreach (var hit in Physics.OverlapBox(candidate + Vector3.up, new Vector3(.95f, .98f, .36f),
                parkedRotation, ~0, QueryTriggerInteraction.Ignore))
            {
                if (!Live(hit) || Live(_root) && hit.transform.IsChildOf(_root!.transform)) continue;
                clear = false; break;
            }
            if (!clear) continue;
            position = candidate;
            return true;
        }
        return false;
    }

    private bool Ground(Vector3 near, out Vector3 point)
    {
        point = default;
        var distance = float.PositiveInfinity;
        RaycastHit nearest = default;
        foreach (var hit in Physics.RaycastAll(near + Vector3.up * 3, Vector3.down, 7, ~0, QueryTriggerInteraction.Ignore))
        {
            if (!Live(hit.collider) || Live(_root) && hit.collider.transform.IsChildOf(_root!.transform)
                || hit.distance >= distance) continue;
            nearest = hit; distance = hit.distance;
        }
        if (!float.IsFinite(distance) || nearest.normal.y < .97f || nearest.collider.attachedRigidbody != null) return false;
        var collider = nearest.collider;
        var terrain = collider.GetComponent("TerrainCollider");
        var mesh = collider.GetComponent<MeshCollider>();
        if (!(Live(terrain) && terrain.Pointer == collider.Pointer)
            && !(Live(mesh) && mesh.Pointer == collider.Pointer && collider.gameObject.isStatic)) return false;
        point = nearest.point;
        return point.y < 2000;
    }

    private bool Refuse(string reason, out string status) { Status = status = reason; return false; }

    private void Fail(string operation, Exception error)
    {
        _failed = true;
        try { Park(); }
        catch (Exception parkingError) { Log.LogError("Could not park the native cargo shelf: " + parkingError); }
        Status = "Cargo storage is unavailable. Any existing cargo remains in its native shelf.";
        Log.LogError("Could not " + operation + " spacecraft cargo storage: " + error);
    }

    private static bool Live([NotNullWhen(true)] UnityEngine.Object? value) => value != null && !value.WasCollected && value;
    private static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
    private static bool Valid(Quaternion value) => float.IsFinite(value.x) && float.IsFinite(value.y)
        && float.IsFinite(value.z) && float.IsFinite(value.w) && Quaternion.Dot(value, value) > .5f
        && Quaternion.Dot(value, value) < 1.5f;

    private sealed class CargoRack : IDisposable
    {
        private readonly List<(MeshRenderer Renderer, bool Hidden)> _wood = new();
        private readonly List<Mesh> _meshes = new();
        private readonly List<Material> _materials = new();
        private readonly List<Texture2D> _textures = new();
        private GameObject? _model;

        internal CargoRack(GameObject shelf)
        {
            try
            {
                // Prefab 182 has exactly five plank groups. Never traverse Hooks or
                // ItemLayoutGroups: their renderers belong to native stored items.
                for (var part = 0; part < 5; part++)
                {
                    var path = "Renderables/LogPlankQuarterBLODG" + (part == 0 ? "" : " (" + part + ")");
                    var meshName = part < 2 ? "LogPlankThreeQuarterAMesh_LOD" : "LogPlankHalfBMesh_LOD";
                    for (var lod = 0; lod < 5; lod++)
                    {
                        var node = shelf.transform.Find(path + "/" + (lod == 4 ? "Shadows" : "LogHalfBMesh_LOD" + lod));
                        var renderer = Live(node) ? node.GetComponent<MeshRenderer>() : null;
                        var filter = Live(node) ? node.GetComponent<MeshFilter>() : null;
                        if (!Live(renderer) || !Live(filter) || !Live(filter.sharedMesh)
                            || filter.sharedMesh.name != meshName + System.Math.Min(lod, 3))
                            throw new InvalidOperationException("The native cargo shelf frame is incompatible: " + path);
                        _wood.Add((renderer, renderer.forceRenderingOff));
                    }
                }

                var rows = new Vector3[3];
                var hooks = new[] { "BottomHooks", "MiddleHooks", "TopHooks" };
                for (var row = 0; row < rows.Length; row++)
                {
                    var hook = shelf.transform.Find("Hooks/" + hooks[row]);
                    if (!Live(hook)) throw new InvalidOperationException("The native cargo shelf slot row is unavailable: " + hooks[row]);
                    rows[row] = shelf.transform.InverseTransformPoint(hook.position);
                }

                _model = new GameObject("Spacecraft cargo rack") { layer = shelf.layer };
                _model.SetActive(false);
                _model.transform.SetParent(shelf.transform, false);
                var shader = Shader.Find("HDRP/Lit");
                if (!Live(shader)) throw new InvalidOperationException("The cargo rack surface shader is unavailable.");
                var frame = Load("frame", shader);
                var tray = Load("tray", shader);

                // Authored at native scale: floor-centred frame and tray top at Y=0.
                AttachMesh("Cargo rack frame", Vector3.zero, frame.Mesh, frame.Material);
                foreach (var row in rows)
                {
                    // The top surface follows the native item origin, including its
                    // small authored X/Z offsets; no slot or interaction moves.
                    AttachMesh("Cargo rack tray", row, tray.Mesh, tray.Material);
                }

                // LODGroup may change enabled; forceRenderingOff suppresses every
                // verified plank LOD and shadow without disabling native objects.
                foreach (var wood in _wood) wood.Renderer.forceRenderingOff = true;
                _model.SetActive(true);
            }
            catch { Dispose(); throw; }
        }

        private (Mesh Mesh, Material Material) Load(string asset, Shader shader)
        {
            using var stream = typeof(ClientSpacecraftStorage).Assembly.GetManifestResourceStream(
                "Modstoke.Client.Assets.spacecraft-cargo-" + asset + ".msa")
                ?? throw new InvalidOperationException("The cargo rack model is missing: " + asset);
            var data = SpacecraftMeshData.Read(stream, "cargo-" + asset);

            var mesh = new Mesh();
            _meshes.Add(mesh);
            mesh.name = "Cargo rack " + data.Name;
            mesh.indexFormat = data.Vertices.Length > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.vertices = data.Vertices;
            mesh.normals = data.Normals;
            mesh.uv = data.Uv;
            mesh.SetTriangles(data.Triangles, 0);
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            mesh.UploadMeshData(true);

            var material = new Material(shader);
            _materials.Add(material);
            material.name = "Cargo rack " + data.MaterialName;
            material.SetColor("_BaseColor", data.Color);
            material.SetFloat("_Metallic", data.Metallic);
            material.SetFloat("_Smoothness", 1 - data.Roughness);
            material.SetColor("_EmissiveColor", data.Color * data.Emission);
            if (data.Textured)
            {
                if (_textures.Count == 0) LoadAtlas();
                material.SetTexture("_BaseColorMap", _textures[0]);
                material.SetTexture("_MaskMap", _textures[1]);
                // The mask contains absolute metalness and smoothness per finish.
                material.SetFloat("_MetallicRemapMin", 0);
                material.SetFloat("_MetallicRemapMax", 1);
                material.SetFloat("_SmoothnessRemapMin", 0);
                material.SetFloat("_SmoothnessRemapMax", 1);
                material.SetFloat("_AORemapMin", 0);
                material.SetFloat("_AORemapMax", 1);
                material.EnableKeyword("_MASKMAP");
            }
            material.enableInstancing = true;
            return (mesh, material);
        }

        private void LoadAtlas()
        {
            for (var channel = 0; channel < 2; channel++)
            {
                var name = "spacecraft-cargo-atlas" + (channel == 0 ? "" : "-mask") + ".png";
                using var stream = typeof(ClientSpacecraftStorage).Assembly.GetManifestResourceStream("Modstoke.Client.Assets." + name)
                    ?? throw new InvalidOperationException("The cargo rack texture is missing: " + name);
                if (!stream.CanSeek || stream.Length is < 24 or > 8 * 1024 * 1024)
                    throw new System.IO.InvalidDataException("The cargo rack texture size is invalid: " + name);
                using var bytes = new System.IO.MemoryStream((int)stream.Length);
                stream.CopyTo(bytes);
                // Color is sRGB; the HDRP metal/AO/detail/smoothness mask is linear.
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true, channel != 0);
                _textures.Add(texture);
                texture.name = name;
                if (!ImageConversion.LoadImage(texture, bytes.ToArray(), false) || texture.width != 512 || texture.height != 512)
                    throw new System.IO.InvalidDataException("The cargo rack texture could not be decoded: " + name);
                texture.wrapMode = TextureWrapMode.Clamp;
                texture.filterMode = FilterMode.Trilinear;
                texture.anisoLevel = 8;
                texture.Apply(true, true);
            }
        }

        private void AttachMesh(string name, Vector3 position, Mesh mesh, Material material)
        {
            var node = new GameObject(name) { layer = _model!.layer };
            node.transform.SetParent(_model!.transform, false);
            node.transform.localPosition = position;
            node.transform.localRotation = Quaternion.Euler(0, 180, 0);
            // Presentation only: native shelf collision and storage triggers stay intact.
            node.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = node.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            renderer.receiveShadows = true;
        }

        public void Dispose()
        {
            foreach (var wood in _wood)
                if (Live(wood.Renderer)) wood.Renderer.forceRenderingOff = wood.Hidden;
            _wood.Clear();
            if (Live(_model)) { _model.SetActive(false); UnityEngine.Object.Destroy(_model); }
            _model = null;
            foreach (var mesh in _meshes) if (Live(mesh)) UnityEngine.Object.Destroy(mesh);
            _meshes.Clear();
            foreach (var material in _materials) if (Live(material)) UnityEngine.Object.Destroy(material);
            _materials.Clear();
            foreach (var texture in _textures) if (Live(texture)) UnityEngine.Object.Destroy(texture);
            _textures.Clear();
        }
    }
}
