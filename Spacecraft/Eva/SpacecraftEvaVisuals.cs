// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using Il2CppInterop.Runtime.InteropTypes;
using RootMotion;
using RootMotion.FinalIK;
using Sons.Multiplayer.Client;
using TheForest.Utils;
using UnityEngine;
using UnityEngine.Rendering;

namespace Modstoke.Client;

internal readonly record struct SpacecraftEvaWearer(int Slot, ulong Actor, bool Local, Guid Lease);

// Presentation only: native animation, body visibility and camera ownership remain with the player.
internal sealed class SpacecraftEvaVisuals : IDisposable
{
    private static readonly string[] AssetNames = { "helmet", "torso", "pelvis", "pack", "upper-arm",
        "lower-arm", "upper-leg", "lower-leg", "glove-left", "glove-right", "boot" };
    private readonly Binding?[] _bindings = new Binding?[3];
    private readonly SpacecraftEvaWearer?[] _wearers = new SpacecraftEvaWearer?[3];
    private readonly float[] _retry = new float[3];
    private readonly bool[] _retired = new bool[3];
    private readonly List<Mesh> _meshes = new();
    private readonly Material _material;
    private bool _failed, _disposed;

    // The rack owns the shared finish and outlives these wearable renderers.
    internal SpacecraftEvaVisuals(Material material) => _material = material;

    internal void Update(ReadOnlySpan<SpacecraftEvaWearer> wearers, bool visible)
    {
        if (_disposed || _failed) return;
        try
        {
            for (var slot = 0; slot < _bindings.Length; slot++)
            {
                SpacecraftEvaWearer? wearer = null;
                for (var i = 0; i < wearers.Length; i++)
                {
                    if (wearers[i].Slot != slot) continue;
                    if (wearer.HasValue) { wearer = null; break; }
                    wearer = wearers[i];
                }
                if (_wearers[slot] != wearer)
                {
                    Release(slot);
                    _wearers[slot] = wearer;
                    _retry[slot] = 0;
                    _retired[slot] = false;
                }
                if (!wearer.HasValue) continue;
                var binding = _bindings[slot];
                if (binding != null && !binding.Current)
                {
                    // A reused network ID must not inherit a previous attachment's suit.
                    _retired[slot] = !binding.SameActor;
                    Release(slot);
                    binding = null;
                    _retry[slot] = Time.unscaledTime + 1;
                }
                if (!visible || !Live(LocalPlayer.MainCam))
                {
                    binding?.Hide();
                    continue;
                }
                if (binding == null && !_retired[slot] && Time.unscaledTime >= _retry[slot])
                {
                    _retry[slot] = Time.unscaledTime + 1;
                    binding = Binding.TryCreate(wearer.Value);
                    if (binding != null)
                    {
                        _bindings[slot] = binding;
                        LoadAssets();
                        binding.CreateParts(_meshes, _material);
                    }
                }
                binding?.Render();
            }
        }
        catch (Exception error)
        {
            _failed = true;
            Exception? cleanup = null;
            ReleaseBindings(ref cleanup);
            UnityEngine.Debug.LogWarning("Modstoke Client: EVA suit visuals stopped: " + error);
            if (cleanup != null) UnityEngine.Debug.LogWarning("Modstoke Client: EVA suit cleanup failed: " + cleanup);
        }
    }

    private void LoadAssets()
    {
        if (_meshes.Count != 0) return;
        foreach (var name in AssetNames)
        {
            using var stream = Resource(name + ".msa");
            var data = SpacecraftMeshData.Read(stream, name);
            var mesh = new Mesh { name = "EVA " + data.Name };
            _meshes.Add(mesh);
            mesh.indexFormat = data.Vertices.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.vertices = data.Vertices;
            mesh.normals = data.Normals;
            mesh.uv = data.Uv;
            mesh.SetTriangles(data.Triangles, 0);
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            mesh.UploadMeshData(true);
        }
    }

    private static Stream Resource(string name) => typeof(SpacecraftEvaVisuals).Assembly.GetManifestResourceStream(
        "Modstoke.Client.Assets.eva-" + name) ?? throw new InvalidOperationException("The EVA asset is missing: " + name);

    private void Release(int slot)
    {
        var binding = _bindings[slot];
        _bindings[slot] = null;
        binding?.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Exception? failure = null;
        ReleaseBindings(ref failure);
        foreach (var mesh in _meshes) Destroy(mesh, ref failure);
        _meshes.Clear();
        if (failure != null) throw failure;
    }

    private void ReleaseBindings(ref Exception? failure)
    {
        for (var i = 0; i < _bindings.Length; i++)
            try { Release(i); }
            catch (Exception error) { failure ??= error; }
    }

    private static void Destroy(UnityEngine.Object? value, ref Exception? failure)
    {
        try { if (Live(value)) UnityEngine.Object.Destroy(value); }
        catch (Exception error) { failure ??= error; }
    }

    private sealed class Binding : IDisposable
    {
        private readonly SpacecraftEvaWearer _wearer;
        private readonly Transform _actor, _mesh;
        private readonly BoltEntity? _entity;
        private readonly Bolt.Entity? _attachment;
        private readonly CoopPlayerRemoteSetup? _setup;
        private readonly Animator _animator;
        private readonly BipedIK _biped;
        private readonly BipedReferences _references;
        private readonly ClientAircraftCrewPose _rig;
        private readonly Transform[] _bones;
        private readonly Quaternion[] _basis;
        private readonly List<Part> _parts = new();
        private GameObject? _root;

        private Binding(SpacecraftEvaWearer wearer, Transform actor, BoltEntity? entity,
            CoopPlayerRemoteSetup? setup, Animator animator, BipedIK biped, ClientAircraftCrewPose rig,
            Transform[] bones, Quaternion[] basis)
        {
            _wearer = wearer; _actor = actor; _entity = entity;
            _attachment = Live(entity) && entity.isAttached ? entity._entity : null;
            _setup = setup; _animator = animator; _biped = biped; _mesh = biped.transform;
            _references = biped.references; _rig = rig; _bones = bones; _basis = basis;
        }

        internal static Binding? TryCreate(SpacecraftEvaWearer wearer)
        {
            Transform actor;
            BoltEntity? entity;
            CoopPlayerRemoteSetup? setup = null;
            Animator animator;
            BipedIK biped;
            if (wearer.Local)
            {
                var ik = LocalPlayer.IkTargetsController;
                var control = LocalPlayer.AnimControl;
                if (!Live(LocalPlayer.Transform) || !Live(LocalPlayer.Animator) || !LocalPlayer.Animator.isActiveAndEnabled
                    || !Live(control) || !control.isActiveAndEnabled || !Live(ik) || !ik.gameObject.activeInHierarchy
                    || !Live(ik._bipped) || !Same(control.transform, ik._bipped.transform)) return null;
                actor = LocalPlayer.Transform; animator = LocalPlayer.Animator; biped = ik._bipped;
                entity = LocalPlayer.Entity;
                if (BoltNetwork.isRunning && (!Live(entity) || !entity.isAttached
                    || entity.networkId.PackedValue != wearer.Actor || entity._entity == null || entity._entity.WasCollected)) return null;
                if (!BoltNetwork.isRunning && wearer.Actor != 0) return null;
            }
            else
            {
                if (!BoltNetwork.isRunning) return null;
                entity = BoltNetwork.FindEntity(new Bolt.NetworkId(wearer.Actor));
                if (!Live(entity) || !entity.isAttached || Same(entity, LocalPlayer.Entity)
                    || entity._entity == null || entity._entity.WasCollected) return null;
                var setups = entity.GetComponentsInChildren<CoopPlayerRemoteSetup>(true);
                if (setups == null || setups.WasCollected || setups.Length > 8) return null;
                foreach (var candidate in setups)
                {
                    if (!Live(candidate) || !Same(candidate._entity, entity)) continue;
                    if (setup != null) return null;
                    setup = candidate;
                }
                if (!Live(setup) || !Live(setup._playerAnimator)) return null;
                animator = setup._playerAnimator;
                if (!animator.isActiveAndEnabled || Same(animator.transform, entity.transform)
                    || !animator.transform.IsChildOf(entity.transform)) return null;
                var bipeds = animator.GetComponents<BipedIK>();
                if (bipeds == null || bipeds.WasCollected || bipeds.Length != 1 || !Live(bipeds[0])) return null;
                biped = bipeds[0]; actor = entity.transform;
            }
            // Reuse the seated rig's exact native-reference and hierarchy validation without applying a pose.
            var rig = ClientAircraftCrewPose.TryCreate(actor, biped, wearer.Local);
            if (rig == null) return null;
            var refs = biped.references;
            var spine = refs.spine;
            if (spine == null || spine.WasCollected || spine.Length == 0 || !Live(refs.head)
                || !refs.head.IsChildOf(refs.pelvis)) return null;
            Transform[] bones = { refs.head, spine[spine.Length - 1], refs.pelvis,
                refs.leftUpperArm, refs.leftForearm, refs.leftHand,
                refs.rightUpperArm, refs.rightForearm, refs.rightHand,
                refs.leftThigh, refs.leftCalf, refs.leftFoot,
                refs.rightThigh, refs.rightCalf, refs.rightFoot };
            if (!wearer.Local)
            {
                // The validated remote rig may name an ankle and toe marker as calf and foot.
                CorrectLeg(bones, 9, "LeftLeg", "leftFootSpawnPos");
                CorrectLeg(bones, 12, "RightLeg", "rightFootSpawnPos");
            }
            var up = (bones[3].position + bones[6].position) * .5f - bones[2].position;
            var across = bones[6].position - bones[3].position;
            var forward = Vector3.Cross(across, up);
            if (!Direction(up) || !Direction(forward)) return null;
            var frame = Quaternion.LookRotation(forward.normalized, up.normalized);
            var basis = new Quaternion[bones.Length];
            for (var i = 0; i < bones.Length; i++) basis[i] = Quaternion.Inverse(bones[i].rotation) * frame;
            if (!HandBasis(bones[5], "Left", out basis[5]) || !HandBasis(bones[8], "Right", out basis[8])) return null;
            return new Binding(wearer, actor, entity, setup, animator, biped, rig, bones, basis);
        }

        private static void CorrectLeg(Transform[] bones, int start, string calf, string marker)
        {
            if (bones[start + 2].name != marker) return;
            bones[start + 2] = bones[start + 1];
            bones[start + 1] = bones[start].Find(calf);
        }

        private static bool HandBasis(Transform hand, string side, out Quaternion basis)
        {
            basis = Quaternion.identity;
            var index = hand.Find(side + "HandIndex1");
            var middle = hand.Find(side + "HandMiddle1");
            var pinky = hand.Find(side + "HandPinky1");
            if (!Live(index) || !Live(middle) || !Live(pinky)) return false;
            var along = middle.position - hand.position;
            var across = pinky.position - index.position;
            var dorsal = Vector3.Cross(along, across) * (side == "Left" ? 1 : -1);
            if (!Direction(along) || !Direction(dorsal)) return false;
            basis = Quaternion.Inverse(hand.rotation) * Quaternion.LookRotation(dorsal.normalized, along.normalized);
            return true;
        }

        internal bool SameActor
        {
            get
            {
                if (!Live(_actor)) return false;
                if (_wearer.Local && !Same(_actor, LocalPlayer.Transform)) return false;
                if (_attachment == null) return _wearer.Local && !BoltNetwork.isRunning && _wearer.Actor == 0;
                return BoltNetwork.isRunning && Live(_entity) && _entity.isAttached
                    && _entity.networkId.PackedValue == _wearer.Actor && Same(_entity._entity, _attachment)
                    && Same(BoltNetwork.FindEntity(new Bolt.NetworkId(_wearer.Actor)), _entity)
                    && (_wearer.Local ? Same(_entity, LocalPlayer.Entity) : !Same(_entity, LocalPlayer.Entity));
            }
        }

        internal bool Current
        {
            get
            {
                if (!Live(_root) || !SameActor || !Live(_animator) || !_animator.isActiveAndEnabled || !Live(_biped)
                    || !_biped.gameObject.activeInHierarchy || !Same(_biped.transform, _mesh)
                    || !_rig.Current || !Same(_references.head, _bones[0]) || !Live(_bones[0])
                    || !_bones[0].IsChildOf(_references.pelvis)) return false;
                if (_wearer.Local)
                {
                    var ik = LocalPlayer.IkTargetsController;
                    var control = LocalPlayer.AnimControl;
                    return Same(_animator, LocalPlayer.Animator) && Live(ik) && ik.gameObject.activeInHierarchy
                        && Same(ik._bipped, _biped) && Live(control) && control.isActiveAndEnabled
                        && Same(control.transform, _mesh);
                }
                return Live(_setup) && Same(_setup._entity, _entity) && Same(_setup._playerAnimator, _animator)
                    && Same(_animator.transform, _mesh) && _mesh.IsChildOf(_actor);
            }
        }

        internal void CreateParts(List<Mesh> meshes, Material material)
        {
            _root = new GameObject("EVA pressure suit");
            _root.SetActive(false);
            var native = new SkinnedMeshRenderer?[_bones.Length];
            if (_wearer.Local) BindBodyRenderers(native);
            Add(0, 0); Add(1, 1); Add(2, 2); Add(3, 1);
            Add(4, 3, 4, .30f); Add(5, 4, 5, .27f); Add(8, 5);
            Add(4, 6, 7, .30f); Add(5, 7, 8, .27f); Add(9, 8);
            Add(6, 9, 10, .43f); Add(7, 10, 11, .43f); Add(10, 11);
            Add(6, 12, 13, .43f); Add(7, 13, 14, .43f); Add(10, 14);

            void Add(int asset, int bone, int end = -1, float length = 1)
            {
                var part = new GameObject("EVA " + AssetNames[asset]);
                part.transform.SetParent(_root.transform, false);
                part.AddComponent<MeshFilter>().sharedMesh = meshes[asset];
                var renderer = part.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = material;
                renderer.shadowCastingMode = ShadowCastingMode.On;
                renderer.receiveShadows = true;
                var body = native[bone];
                part.layer = Live(body) ? body.gameObject.layer : 0;
                _parts.Add(new Part(part.transform, renderer, body, meshes[asset].bounds, bone, end, length,
                    _wearer.Local && asset is 0 or 1 or 3));
            }
        }

        private void BindBodyRenderers(SkinnedMeshRenderer?[] result)
        {
            var renderers = _mesh.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (renderers == null || renderers.WasCollected || renderers.Length > 128) return;
            foreach (var renderer in renderers)
            {
                if (!Live(renderer)) continue;
                var bones = renderer.bones;
                if (bones == null || bones.WasCollected || bones.Length > 256) continue;
                foreach (var candidate in bones)
                    for (var i = 0; i < _bones.Length; i++)
                        if (Same(candidate, _bones[i]) && (!Live(result[i])
                            || !result[i]!.enabled && renderer.enabled)) result[i] = renderer;
            }
        }

        internal void Render()
        {
            if (!Live(_root)) return;
            var camera = LocalPlayer.MainCam;
            if (!Live(camera)) { Hide(); return; }
            foreach (var part in _parts)
            {
                var bone = _bones[part.Bone];
                var rotation = bone.rotation * _basis[part.Bone];
                var scale = Vector3.one;
                if (part.End >= 0)
                {
                    var along = _bones[part.End].position - bone.position;
                    var length = along.magnitude;
                    if (!float.IsFinite(length) || length < .05f || length > .8f) { Hide(); return; }
                    var forward = Vector3.ProjectOnPlane(rotation * Vector3.forward, along);
                    if (!Direction(forward)) { Hide(); return; }
                    rotation = Quaternion.LookRotation(forward.normalized, along.normalized);
                    scale.y = length / part.Length;
                }
                if (!Finite(bone.position) || !float.IsFinite(rotation.x) || !float.IsFinite(rotation.y)
                    || !float.IsFinite(rotation.z) || !float.IsFinite(rotation.w)) { Hide(); return; }
                part.Transform.SetPositionAndRotation(bone.position, rotation);
                part.Transform.localScale = scale;
                var visible = !part.Hidden;
                if (_wearer.Local)
                {
                    var native = part.Native;
                    visible &= Live(native) && native.enabled && native.gameObject.activeInHierarchy
                        && native.gameObject.layer == part.Transform.gameObject.layer
                        && (camera.cullingMask & (1 << part.Transform.gameObject.layer)) != 0;
                    // Native first-person body parts can approach the eyes while crouching or using equipment.
                    var center = bone.position + rotation * Vector3.Scale(part.Bounds.center, scale);
                    var clearance = Vector3.Scale(part.Bounds.extents, scale).magnitude + camera.nearClipPlane + .08f;
                    visible &= (center - camera.transform.position).sqrMagnitude > clearance * clearance;
                }
                part.Renderer.enabled = visible;
            }
            if (!_root.activeSelf) _root.SetActive(true);
        }

        internal void Hide() { if (Live(_root) && _root.activeSelf) _root.SetActive(false); }

        public void Dispose()
        {
            Exception? failure = null;
            try { Hide(); }
            catch (Exception error) { failure = error; }
            Destroy(_root, ref failure);
            _root = null; _parts.Clear();
            if (failure != null) throw failure;
        }

        private sealed record Part(Transform Transform, MeshRenderer Renderer, SkinnedMeshRenderer? Native,
            Bounds Bounds, int Bone, int End, float Length, bool Hidden);
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
    private static bool Direction(Vector3 value) => Finite(value) && value.sqrMagnitude > .000001f;
    private static bool Live([NotNullWhen(true)] UnityEngine.Object? value) => value != null && !value.WasCollected && value;
    private static bool Same(Il2CppObjectBase? first, Il2CppObjectBase? second) => first != null && second != null
        && !first.WasCollected && !second.WasCollected && first.Pointer == second.Pointer;
}
