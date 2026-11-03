// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Modstoke.Client;

internal sealed partial class ClientTabletPresence
{
    private sealed partial class RemotePose
    {
        private GameObject? _watchRoot;
        private ClientWristWatchModel? _watchModel;
        private Material? _watchGlass;
        private Mesh? _watchGlassMesh;
        private Quaternion _watchInHand;
        private Vector3 _watchOffset;

        private bool CreateWatch()
        {
            var hand = _bones[2];
            var index = hand.Find("LeftHandIndex1");
            var middle = hand.Find("LeftHandMiddle1");
            var pinky = hand.Find("LeftHandPinky1");
            if (!Live(index) || !Live(middle) || !Live(pinky)) return false;
            var along = middle.position - hand.position;
            var across = index.position - pinky.position;
            var palm = Vector3.Cross(across, along);
            var width = across.magnitude;
            if (!float.IsFinite(width) || width < .025f || width > .14f
                || !float.IsFinite(along.sqrMagnitude) || !float.IsFinite(palm.sqrMagnitude)
                || along.sqrMagnitude < .000001f || palm.sqrMagnitude < .0000001f) return false;
            // Match the local model's geometric dorsal frame, without consulting local
            // clothing, inventory, pose singletons or screen content for another actor.
            _watchInHand = Quaternion.Inverse(hand.rotation) * Quaternion.LookRotation(palm.normalized, along.normalized);
            _watchOffset = Quaternion.Inverse(hand.rotation) * (-along.normalized * .008f);
            var radius = Mathf.Clamp(width * .42f, .023f, .038f);
            _watchRoot = new GameObject("Remote field watch") { layer = 0 };
            _watchRoot.SetActive(false);
            // A free world-space anchor avoids inherited rig scale and rotated
            // nonuniform bone scaling. Only its world position and rotation follow the wrist.
            _watchModel = new ClientWristWatchModel(_watchRoot.transform, Quaternion.identity, Vector3.zero,
                0, radius + .002f, radius * .68f + .002f);
            _watchGlass = ClientDeviceScreenMaterial.CreateDisplayMaterial("Remote watch display");
            _watchGlassMesh = new Mesh { name = "Remote watch neutral display" };
            const float w = .022f, h = .023f;
            _watchGlassMesh.vertices = new[] { new Vector3(-w, -h, 0), new Vector3(w, -h, 0),
                new Vector3(w, h, 0), new Vector3(-w, h, 0) };
            _watchGlassMesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            _watchGlassMesh.RecalculateNormals(); _watchGlassMesh.RecalculateBounds();
            var screen = _watchModel.Screen.gameObject;
            screen.AddComponent<MeshFilter>().sharedMesh = _watchGlassMesh;
            var renderer = screen.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = _watchGlass;
            ClientDeviceScreenMaterial.ConfigureRenderer(renderer);
            return true;
        }

        private void RenderWatch(bool visible, float weight, bool touching)
        {
            if (!visible)
            {
                _watchTap.Reset();
                if (Live(_watchRoot)) _watchRoot.SetActive(false);
                return;
            }
            if (!Live(_watchRoot) || _watchModel == null || !Live(_watchModel.Root) || !Live(_watchModel.Screen))
            {
                DisposeWatch();
                if (!CreateWatch()) return;
            }
            if (weight > .001f)
            {
                var root = _entity.transform;
                var shoulders = (_bones[0].position + _bones[3].position) * .5f;
                var reach = Mathf.Min(Reach(0), Reach(3));
                if (float.IsFinite(reach) && reach >= .2f && reach <= 1.4f)
                {
                    var hand = _bones[2];
                    var nativeRotation = hand.rotation;
                    var target = shoulders - root.up * .18f + root.forward * (reach * .48f) + root.right * .025f;
                    _changed = true;
                    SolveArm(0, target, nativeRotation, -root.right - root.up * .35f, weight);
                    // Continue the achieved forearm through the hand; turn its dorsal
                    // side toward the actor's face rather than bending the wrist flat.
                    var along = (hand.position - _bones[1].position).normalized;
                    var view = hand.position - (shoulders + root.up * .15f);
                    var palm = Vector3.ProjectOnPlane(view, along);
                    if (palm.sqrMagnitude < .000001f) palm = Vector3.ProjectOnPlane(root.forward, along);
                    if (along.sqrMagnitude > .5f && palm.sqrMagnitude > .000001f)
                        hand.rotation = Quaternion.Slerp(nativeRotation,
                            Quaternion.LookRotation(palm.normalized, along) * Quaternion.Inverse(_watchInHand), weight);
                    PlaceWatch();
                    var screen = _watchModel!.Screen;
                    var tap = _watchTap.Sample(touching);
                    var contact = screen.position - screen.forward * (.003f + .035f * (1 - tap));
                    SolveTouch(screen, contact, root.right - root.up * .4f, weight);
                    HideItems();
                }
            }
            else _watchTap.Reset();
            PlaceWatch();
            _watchRoot!.SetActive(true);
        }

        private void PlaceWatch()
        {
            var hand = _bones[2];
            _watchRoot!.transform.SetPositionAndRotation(hand.position + hand.rotation * _watchOffset,
                hand.rotation * _watchInHand);
        }

        private void DisposeWatch()
        {
            Exception? failure = null;
            Cleanup(() => { if (Live(_watchRoot)) _watchRoot.SetActive(false); }, ref failure);
            if (_watchModel != null) Cleanup(_watchModel.Dispose, ref failure);
            _watchModel = null;
            Cleanup(() => { if (Live(_watchRoot)) UnityEngine.Object.Destroy(_watchRoot); }, ref failure);
            Cleanup(() => { if (Live(_watchGlassMesh)) UnityEngine.Object.Destroy(_watchGlassMesh); }, ref failure);
            Cleanup(() => { if (Live(_watchGlass)) UnityEngine.Object.Destroy(_watchGlass); }, ref failure);
            _watchRoot = null; _watchGlassMesh = null; _watchGlass = null;
            if (failure != null) throw failure;
        }
    }
}
