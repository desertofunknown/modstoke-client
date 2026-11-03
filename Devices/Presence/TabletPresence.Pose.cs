// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Il2CppInterop.Runtime.InteropTypes;
using RootMotion;
using RootMotion.FinalIK;
using Sons.Multiplayer.Client;
using TheForest.Utils;
using UnityEngine;
using UnityEngine.Rendering;

namespace Modstoke.Client;

internal sealed partial class ClientTabletPresence
{
    private sealed partial class Remote(ulong actor, Guid generation) : IDisposable
    {
        internal readonly Guid Generation = generation;
        internal RemotePose? Pose;
        private ClientTabletPresencePhase _phase;
        private ClientWatchPresencePhase _watchPhase;
        private bool _touch, _watchTouch, _controller;
        private long _seen, _nextBind, _retireAt;
        private float _weight, _watchWeight;
        private bool _awaitingFreshState;

        internal void Set(ClientTabletPresencePhase phase, bool touch, ClientWatchPresencePhase watchPhase,
            bool watchTouch, bool controller, long now)
        {
            // A snapshot that first detects attachment loss cannot also authorize rebinding.
            // The next accepted snapshot can, even if its timestamp is in the same millisecond.
            if (Pose != null && !Pose.SameAttachment)
            {
                _awaitingFreshState = true;
                Dispose();
            }
            else _awaitingFreshState = false;
            _retireAt = 0;
            _phase = phase; _touch = touch; _watchPhase = watchPhase; _watchTouch = watchTouch; _seen = now;
            _controller = phase != ClientTabletPresencePhase.Hidden && controller;
        }

        internal bool Retire(long now)
        {
            if (Pose == null || !Pose.Current || now - _seen > Lifetime
                || (_retireAt != 0 && now >= _retireAt) || (_weight <= .001f && _watchWeight <= .001f))
            { Dispose(); return false; }
            if (_retireAt == 0)
            {
                // Omission does not establish why presence ended. Release only this
                // existing pose, without renewing its state lifetime or permitting a rebind.
                _retireAt = now + 500;
                _phase = ClientTabletPresencePhase.Hidden;
                _watchPhase = ClientWatchPresencePhase.Hidden;
                _touch = _watchTouch = false;
                ClearScreen();
            }
            return true;
        }

        internal void Refresh(long now)
        {
            if (Pose != null && (!Pose.Current || (_retireAt != 0 && now >= _retireAt)))
            {
                _awaitingFreshState |= !Pose.SameAttachment;
                Dispose();
            }
            if (_retireAt != 0 || _awaitingFreshState || Pose != null || now < _nextBind || now - _seen > Lifetime) return;
            _nextBind = now + 1000;
            Pose = RemotePose.TryCreate(actor, _controller);
        }

        internal void Render(long now)
        {
            if (Pose == null) return;
            if (now - _seen > Lifetime || !Pose.Current || (_retireAt != 0 && now >= _retireAt))
            { _awaitingFreshState |= !Pose.SameAttachment; Dispose(); return; }
            var drawTablet = _phase is ClientTabletPresencePhase.Drawing or ClientTabletPresencePhase.Open;
            var changingProfile = _phase != ClientTabletPresencePhase.Hidden && Pose.IsController != _controller;
            var delta = Mathf.Min(Time.unscaledDeltaTime, .1f);
            // Release the outgoing arm pose before the other device takes it over.
            _weight = Mathf.MoveTowards(_weight, drawTablet && !changingProfile && _watchWeight <= .001f ? 1 : 0, delta / .38f);
            var inspect = _watchPhase == ClientWatchPresencePhase.Inspecting
                && _phase == ClientTabletPresencePhase.Hidden && _weight <= .001f;
            _watchWeight = Mathf.MoveTowards(_watchWeight, inspect ? 1 : 0, delta / .28f);
            if (_retireAt != 0 && _weight <= .001f && _watchWeight <= .001f)
            { Dispose(); return; }
            Pose.Render(_weight, _touch && _phase == ClientTabletPresencePhase.Open && !changingProfile,
                _watchPhase != ClientWatchPresencePhase.Hidden || _watchWeight > .001f, _watchWeight, inspect && _watchTouch,
                _controller, _phase != ClientTabletPresencePhase.Hidden && !changingProfile);
            if (changingProfile) ClearScreen();
            else RenderScreen(now);
        }

        public void Dispose()
        {
            _screenBytes = null; _screenSeen = 0; _screenPending = false;
            try { Pose?.Dispose(); }
            finally { Pose = null; _weight = _watchWeight = 0; }
        }
    }

    // Bone references come from this remote player's native BipedIK. Its solver settings,
    // operations, animation layers, inventory and network state are never written.
    private sealed partial class RemotePose : IDisposable
    {
        private const float Width = .352f, Height = .22f;
        private const float VisibleDistance = 80;
        private readonly BoltEntity _entity;
        private readonly Bolt.Entity _attachment;
        private readonly ulong _actor;
        private readonly CoopPlayerRemoteSetup _setup;
        private readonly Animator _animator;
        private readonly BipedIK _biped;
        private readonly BipedReferences _references;
        private readonly Transform[] _bones;
        private readonly Quaternion[] _before = new Quaternion[6], _applied = new Quaternion[6];
        private readonly Transform _indexTip;
        private readonly List<Renderer> _hiddenItems = new();
        private readonly Quaternion _leftBasis, _rightBasis;
        private GameObject? _root;
        private IDisposable? _model;
        private Material? _glass;
        private Mesh? _glassMesh;
        private bool _changed, _disposed, _controller;
        private int _frame = -1;
        internal bool IsController => _controller;

        private RemotePose(BoltEntity entity, CoopPlayerRemoteSetup setup, Animator animator,
            BipedIK biped, BipedReferences references,
            Transform[] bones, Transform indexTip, Quaternion leftBasis, Quaternion rightBasis)
        {
            _entity = entity; _attachment = entity._entity; _actor = entity.networkId.PackedValue;
            _setup = setup; _animator = animator; _biped = biped;
            _references = references; _bones = bones;
            _indexTip = indexTip;
            _leftBasis = leftBasis; _rightBasis = rightBasis;
            PrepareTouchFinger();
        }

        internal static RemotePose? TryCreate(ulong actor, bool controller)
        {
            RemotePose? pose = null;
            try
            {
                // No render objects on a headless server, and never touch the local actor's rig.
                if (!Live(LocalPlayer.MainCam) || !BoltNetwork.isRunning) return null;
                var entity = BoltNetwork.FindEntity(new Bolt.NetworkId(actor));
                if (!Live(entity) || !entity.isAttached || Same(entity, LocalPlayer.Entity) || !Nearby(entity)
                    || entity._entity == null || entity._entity.WasCollected) return null;
                var setups = entity.GetComponentsInChildren<CoopPlayerRemoteSetup>(true);
                if (setups == null || setups.WasCollected || setups.Length > 8) return null;
                CoopPlayerRemoteSetup? setup = null;
                foreach (var candidate in setups)
                {
                    if (!Live(candidate) || !Same(candidate._entity, entity)) continue;
                    if (setup != null) return null;
                    setup = candidate;
                }
                if (!Live(setup) || !Live(setup!._playerAnimator)) return null;
                var animator = setup._playerAnimator;
                if (!animator.isActiveAndEnabled || !animator.transform.IsChildOf(entity.transform)) return null;
                // PlayerBase's remote PlayerAnimator has BipedIK, not the local IkTargetsController.
                var bipeds = animator.GetComponents<BipedIK>();
                if (bipeds == null || bipeds.WasCollected || bipeds.Length != 1) return null;
                var biped = bipeds[0];
                if (!Live(biped)) return null;
                var references = biped.references;
                if (references == null || references.WasCollected) return null;
                Transform[] bones = { references.leftUpperArm, references.leftForearm, references.leftHand,
                    references.rightUpperArm, references.rightForearm, references.rightHand };
                for (var i = 0; i < bones.Length; i++)
                {
                    if (!Live(bones[i]) || !bones[i].IsChildOf(animator.transform)) return null;
                    for (var j = 0; j < i; j++) if (Same(bones[i], bones[j])) return null;
                }
                if (!bones[1].IsChildOf(bones[0]) || !bones[2].IsChildOf(bones[1])
                    || !bones[4].IsChildOf(bones[3]) || !bones[5].IsChildOf(bones[4])) return null;
                if (!HandBasis(bones[2], "Left", out var leftBasis, out _)
                    || !HandBasis(bones[5], "Right", out var rightBasis, out var tip)) return null;
                pose = new RemotePose(entity, setup, animator, biped, references, bones, tip!, leftBasis, rightBasis);
                pose.CreateModel(controller);
                return pose;
            }
            catch (Exception)
            {
                try { pose?.Dispose(); } catch (Exception) { }
                throw;
            }
        }

        internal bool Current
        {
            get
            {
                try
                {
                    if (_disposed || !SameAttachment || !Nearby(_entity)
                        || !Live(_setup) || !Same(_setup._entity, _entity) || !Live(_animator)
                        || !_animator.isActiveAndEnabled || !Same(_setup._playerAnimator, _animator)
                        || !Live(_biped) || !Same(_biped.references, _references)
                        || !Same(_biped.transform, _animator.transform)
                        || !Live(_indexTip) || !Live(_root)) return false;
                    if (!Same(_references.leftUpperArm, _bones[0]) || !Same(_references.leftForearm, _bones[1])
                        || !Same(_references.leftHand, _bones[2]) || !Same(_references.rightUpperArm, _bones[3])
                        || !Same(_references.rightForearm, _bones[4]) || !Same(_references.rightHand, _bones[5])) return false;
                    foreach (var bone in _bones)
                        if (!Live(bone) || !bone.IsChildOf(_animator.transform)) return false;
                    return true;
                }
                catch (Exception) { return false; }
            }
        }

        private static bool Nearby(BoltEntity entity)
        {
            var camera = LocalPlayer.MainCam;
            return Live(camera) && (camera.transform.position - entity.transform.position).sqrMagnitude
                <= VisibleDistance * VisibleDistance;
        }

        internal bool SameAttachment
        {
            get
            {
                try
                {
                    return BoltNetwork.isRunning && Live(_entity) && _entity.isAttached
                        && !Same(_entity, LocalPlayer.Entity) && _entity.networkId.PackedValue == _actor
                        && Same(_entity._entity, _attachment)
                        && Same(BoltNetwork.FindEntity(new Bolt.NetworkId(_actor)), _entity);
                }
                catch (Exception) { return false; }
            }
        }

        private void CreateModel(bool controller)
        {
            _root = new GameObject("Remote field tablet");
            _root.SetActive(false);
            // World-space, on the ordinary world layer; never attach UI, colliders or an inventory item.
            _root.layer = 0;
            SetController(controller);
            _glass = ClientDeviceScreenMaterial.CreateDisplayMaterial("Remote tablet display");
            var w = Width * ClientTabletModel.DisplayScale * .5f;
            var h = Height * ClientTabletModel.DisplayScale * .5f;
            _glassMesh = new Mesh { name = "Remote tablet neutral display" };
            _glassMesh.vertices = new[] { new Vector3(-w, -h, 0), new Vector3(w, -h, 0),
                new Vector3(w, h, 0), new Vector3(-w, h, 0) };
            _glassMesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            _glassMesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
            _glassMesh.RecalculateNormals(); _glassMesh.RecalculateBounds();
            _root.AddComponent<MeshFilter>().sharedMesh = _glassMesh;
            var renderer = _root.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = _glass;
            ClientDeviceScreenMaterial.ConfigureRenderer(renderer);
        }

        private void SetController(bool controller)
        {
            if (_model != null && _controller == controller) return;
            // Only replace the casing. The display root, material and shared frame keep their ownership.
            var previous = _model;
            _model = null;
            previous?.Dispose();
            _model = controller
                ? new ClientDroneControllerModel(_root!.transform, Width, Height, 0, null)
                : new ClientTabletModel(_root!.transform, Width, Height, 0, null);
            _controller = controller;
        }

        internal void Render(float weight, bool touching, bool watchVisible, float watchWeight, bool watchTouching,
            bool controller, bool deviceVisible)
        {
            if (_frame == Time.frameCount || !Current) return;
            _frame = Time.frameCount;
            Restore();
            _root!.SetActive(false);
            try
            {
                // Keep the outgoing casing through its holster, including profile changes
                // whose intermediate Hidden state was not present in a snapshot.
                if (deviceVisible || weight <= .001f) SetController(controller);
                for (var i = 0; i < _bones.Length; i++) _before[i] = _bones[i].localRotation;
                if (weight > .001f) RenderTablet(weight, touching);
                else _tabletTap.Reset();
                RenderWatch(watchVisible, watchWeight, watchTouching);
                if (_changed)
                    for (var i = 0; i < _bones.Length; i++) _applied[i] = _bones[i].localRotation;
            }
            catch (Exception)
            {
                // Capture partial writes too, so exception cleanup can relinquish only this layer.
                if (_changed)
                    for (var i = 0; i < _bones.Length; i++)
                        try { if (Live(_bones[i])) _applied[i] = _bones[i].localRotation; } catch (Exception) { }
                try { Restore(); } catch (Exception) { }
                try { if (Live(_root)) _root!.SetActive(false); } catch (Exception) { }
                try { if (Live(_watchRoot)) _watchRoot!.SetActive(false); } catch (Exception) { }
                throw;
            }
        }

        private void RenderTablet(float weight, bool touching)
        {
            // Shoulder height follows crouching and native animation without writing the torso.
            var root = _entity.transform;
            var shoulders = (_bones[0].position + _bones[3].position) * .5f;
            var reach = Mathf.Min(Reach(0), Reach(3));
            if (!float.IsFinite(reach) || reach < .2f || reach > 1.4f) return;
            var rotation = root.rotation * Quaternion.Euler(52, 0, 0);
            var center = shoulders + root.up * (-.26f - .17f * (1 - weight))
                + root.forward * (reach * .55f);
            _root!.transform.SetPositionAndRotation(center, rotation);
            var plane = _root.transform;
            var grip = new Vector3(-Width * .5f - .012f, -Height * .12f, .015f);
            var leftTarget = plane.TransformPoint(grip);
            var leftRotation = Quaternion.LookRotation(plane.up, -plane.forward) * Quaternion.Inverse(_leftBasis);
            _changed = true;
            SolveArm(0, leftTarget, leftRotation, root.right * -1 + root.up * -.5f, weight);
            // Follow the achieved support wrist, including partial draw/holster and reach limits.
            var heldRotation = _bones[2].rotation * _leftBasis
                * Quaternion.Inverse(Quaternion.LookRotation(Vector3.up, Vector3.back));
            plane.SetPositionAndRotation(_bones[2].position - heldRotation * grip, heldRotation);
            var tap = _tabletTap.Sample(touching);
            var hover = _controller ? new Vector3(Width * .22f, -Height * .02f, -.073f)
                : new Vector3(Width * .43f, -Height * .32f, -.033f);
            var press = new Vector3(Width * .22f, -Height * .02f, -.008f);
            var contact = plane.TransformPoint(Vector3.Lerp(hover, press, tap));
            SolveTouch(plane, contact, root.right - root.up * .5f, weight, tablet: !_controller);
            HideItems();
            _root.SetActive(true);
        }

        private float Reach(int start) => Vector3.Distance(_bones[start].position, _bones[start + 1].position)
            + Vector3.Distance(_bones[start + 1].position, _bones[start + 2].position);

        private void SolveArm(int start, Vector3 target, Quaternion rotation, Vector3 elbowHint, float weight)
        {
            var upper = _bones[start]; var lower = _bones[start + 1]; var hand = _bones[start + 2];
            var shoulder = upper.position;
            var a = Vector3.Distance(shoulder, lower.position);
            var b = Vector3.Distance(lower.position, hand.position);
            if (a < .03f || b < .03f) return;
            var direction = target - shoulder;
            var distance = Mathf.Clamp(direction.magnitude, Mathf.Abs(a - b) + .01f, (a + b) * .97f);
            direction.Normalize(); target = shoulder + direction * distance;
            var pole = Vector3.ProjectOnPlane(elbowHint, direction).normalized;
            if (pole.sqrMagnitude < .5f) return;
            var along = (a * a - b * b + distance * distance) / (2 * distance);
            var across = Mathf.Sqrt(Mathf.Max(0, a * a - along * along));
            var elbow = shoulder + direction * along + pole * across;
            var blend = weight < 1f;
            var upperBefore = blend ? upper.localRotation : default;
            var lowerBefore = blend ? lower.localRotation : default;
            var handBefore = blend ? hand.localRotation : default;
            // Solve the complete chain first. Blending a parent before solving its
            // child changes the child's target pose as the animation weight changes.
            upper.rotation = Quaternion.FromToRotation(lower.position - shoulder, elbow - shoulder) * upper.rotation;
            lower.rotation = Quaternion.FromToRotation(hand.position - lower.position, target - lower.position) * lower.rotation;
            hand.rotation = rotation;
            if (!blend) return;
            var upperSolved = upper.localRotation;
            var lowerSolved = lower.localRotation;
            var handSolved = hand.localRotation;
            upper.localRotation = Quaternion.Slerp(upperBefore, upperSolved, weight);
            lower.localRotation = Quaternion.Slerp(lowerBefore, lowerSolved, weight);
            hand.localRotation = Quaternion.Slerp(handBefore, handSolved, weight);
        }

        internal void Restore()
        {
            Exception? failure = null;
            Cleanup(RestoreTouchFinger, ref failure);
            foreach (var renderer in _hiddenItems)
            {
                try { if (Live(renderer) && renderer.forceRenderingOff) renderer.forceRenderingOff = false; }
                catch (Exception error) { failure ??= error; }
            }
            _hiddenItems.Clear();
            if (_changed)
            {
                _changed = false;
                // Local rotations remain comparable when the actor moves. A later native/mod write wins.
                for (var i = _bones.Length - 1; i >= 0; i--)
                {
                    try
                    {
                        if (Live(_bones[i]) && Quaternion.Angle(_bones[i].localRotation, _applied[i]) < .01f)
                            _bones[i].localRotation = _before[i];
                    }
                    catch (Exception error) { failure ??= error; }
                }
            }
            if (failure != null) throw failure;
        }

        private static bool HandBasis(Transform hand, string side, out Quaternion basis, out Transform? tip)
        {
            basis = Quaternion.identity; tip = null;
            var index = hand.Find(side + "HandIndex1");
            var middle = hand.Find(side + "HandMiddle1");
            var pinky = hand.Find(side + "HandPinky1");
            tip = hand.Find(side + "HandIndex1/" + side + "HandIndex2/" + side + "HandIndex3/" + side + "HandIndex4");
            if (!Live(index) || !Live(middle) || !Live(pinky) || !Live(tip)) return false;
            // Knuckles define the palm even while a native weapon animation curls the fingers.
            var forward = hand.InverseTransformDirection(middle.position - hand.position).normalized;
            var across = hand.InverseTransformDirection(pinky.position - index.position).normalized;
            var up = Vector3.Cross(forward, across).normalized * (side == "Left" ? -1 : 1);
            if (forward.sqrMagnitude < .5f || up.sqrMagnitude < .5f) return false;
            basis = Quaternion.LookRotation(forward, up);
            return true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Exception? failure = null;
            Cleanup(Restore, ref failure);
            Cleanup(DisposeScreen, ref failure);
            Cleanup(DisposeWatch, ref failure);
            Cleanup(() => { if (Live(_root)) _root!.SetActive(false); }, ref failure);
            if (_model != null) Cleanup(_model.Dispose, ref failure);
            _model = null;
            Cleanup(() => { if (Live(_root)) UnityEngine.Object.Destroy(_root); }, ref failure);
            Cleanup(() => { if (Live(_glassMesh)) UnityEngine.Object.Destroy(_glassMesh); }, ref failure);
            Cleanup(() => { if (Live(_glass)) UnityEngine.Object.Destroy(_glass); }, ref failure);
            _root = null; _glassMesh = null; _glass = null;
            if (failure != null) throw failure;
        }

        private static bool Live([NotNullWhen(true)] UnityEngine.Object? value) => value != null && !value.WasCollected && value;
        private static bool Same(Il2CppObjectBase? first, Il2CppObjectBase? second) => first != null && second != null
            && !first.WasCollected && !second.WasCollected && first.Pointer == second.Pointer;
    }
}
