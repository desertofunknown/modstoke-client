// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientAircraftCrewPose
{
    private FingerPose? _leftPress;
    private FingerPose? _rightPress;
    private FingerPose?[]? _leftFold;
    private bool _pressPrepared;
    private bool _rightPressPrepared;
    private int _leftPressFrame = -1;

    internal bool TryCanReachRightPress(Vector3 contact, Quaternion rotation, out bool reachable)
    {
        reachable = false;
        if (!Finite(contact) || !float.IsFinite(rotation.x) || !float.IsFinite(rotation.y)
            || !float.IsFinite(rotation.z) || !float.IsFinite(rotation.w)
            || MathF.Abs(Quaternion.Dot(rotation, rotation) - 1) >= .02f
            || !Current || _rightHand == null) return false;
        PrepareRightPress();
        if (_rightPress is not { Current: true } press) return false;
        var upper = _limbs[9].position;
        var lower = _limbs[10].position;
        var hand = _limbs[11].position;
        var a = Vector3.Distance(upper, lower);
        var b = Vector3.Distance(lower, hand);
        if (!float.IsFinite(a) || !float.IsFinite(b) || a < .03f || b < .03f) return false;
        var offset = press.PosedTipOffset;
        var wrist = contact - rotation * _rightHand.Rotation * offset;
        var distance = Vector3.Distance(upper, wrist);
        // Match Solve's wrist limits without posing bones or taking hand ownership.
        if (!Finite(offset) || !float.IsFinite(distance)) return false;
        reachable = distance >= Mathf.Abs(a - b) + .01f && distance <= (a + b) * .97f;
        return true;
    }

    internal bool TryGetLeftPressContact(out Vector3 actualContact)
    {
        actualContact = default;
        if (_leftPressFrame != Time.frameCount || !_changed || !Current
            || _leftPress is not { Current: true } press) return false;
        var tip = press.TipPosition;
        if (!Finite(tip)) return false;
        actualContact = tip;
        return true;
    }

    internal bool TryRenderRightPress(Vector3 contact, Vector3 up, Quaternion rotation, float reach,
        out Vector3 actualContact)
    {
        actualContact = default;
        var frame = Time.frameCount;
        if (_frame == frame || !float.IsFinite(reach) || reach <= 0
            || !Finite(contact) || !Finite(up)
            || !float.IsFinite(rotation.x) || !float.IsFinite(rotation.y)
            || !float.IsFinite(rotation.z) || !float.IsFinite(rotation.w)
            || MathF.Abs(Quaternion.Dot(rotation, rotation) - 1) >= .02f
            || !Current || _rightHand == null) return false;
        RenderRightHand(contact, up, reach, rotation, press: true);
        if (_frame != frame || !_rightChanged || _rightPress is not { Current: true } press) return false;
        // Read the rendered terminal bone so native scale and the arm reach clamp remain intact.
        var tip = press.TipPosition;
        if (!Finite(tip)) return false;
        actualContact = tip;
        return true;
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y)
        && float.IsFinite(value.z);

    private void PrepareLeftPress()
    {
        if (_pressPrepared) return;
        _pressPrepared = true;
        if (_leftHand == null) return;
        var hand = _limbs[8];
        var basis = hand.rotation * Quaternion.Inverse(_leftHand.Rotation);
        var normal = basis * Vector3.up;
        _leftPress = FingerPose.TryCreatePress(hand, "LeftHandIndex", basis * Vector3.forward, normal);
        if (_leftPress == null) return;
        _leftFold = new[]
        {
            FingerPose.TryCreate(hand, "LeftHandMiddle", normal, false),
            FingerPose.TryCreate(hand, "LeftHandRing", normal, false),
            FingerPose.TryCreate(hand, "LeftHandPinky", normal, false),
            FingerPose.TryCreate(hand, "LeftHandThumb", normal, true)
        };
    }

    private void PrepareRightPress()
    {
        if (_rightPressPrepared) return;
        _rightPressPrepared = true;
        if (_rightHand == null) return;
        var hand = _limbs[11];
        var basis = hand.rotation * Quaternion.Inverse(_rightHand.Rotation);
        _rightPress = FingerPose.TryCreatePress(hand, "RightHandIndex", basis * Vector3.forward, basis * Vector3.up);
        if (_rightPress != null) PrepareRightGrip();
    }

    private void PoseRightPress(Vector3 contact, Quaternion basis, Vector3 bend, float amount)
    {
        var press = _rightPress!;
        press.Apply(amount);
        RenderRightGrip(amount * .85f, 1);
        var hand = _limbs[11];
        var offset = press.TipOffset;
        var rotation = Quaternion.Slerp(hand.rotation, basis * _rightHand!.Rotation, amount);
        // Interpolate the contact in world space; blending solved arm rotations
        // sweeps the fingertip through the panel before it reaches the key. Back
        // off along the authored index approach to clear sloped panels en route.
        contact = Vector3.Lerp(hand.position + hand.rotation * offset, contact, amount);
        contact -= basis * Vector3.forward * (.08f * Mathf.Sin(Mathf.PI * amount));
        var nativeBend = (_limbs[10].position - _limbs[9].position).normalized;
        Solve(9, contact - rotation * offset, Vector3.Lerp(nativeBend, bend.normalized, amount));
        hand.rotation = rotation;
    }

    private void PoseLeftPress(AircraftCrewTargets targets, Vector3 forward, Vector3 normal, Vector3 bend, float sitBlend)
    {
        var press = _leftPress!;
        var amount = Mathf.SmoothStep(0, 1, Mathf.Clamp01(targets.LeftHandPress));
        press.Apply(amount * sitBlend);
        if (_leftFold != null)
            for (var i = 0; i < _leftFold.Length; i++)
                if (_leftFold[i] is { Current: true } finger) finger.Apply(amount * sitBlend * .85f);
        var basis = Quaternion.Slerp(Quaternion.LookRotation(forward, normal), targets.LeftReachRotation!.Value, amount);
        var rotation = basis * _leftHand!.Rotation;
        // Read the posed chain in world metres, removing rotation only: native
        // skeleton scale stays intact, including the terminal fingertip offset.
        var offset = Vector3.Lerp(_leftHand.PalmOffset, press.TipOffset, amount);
        Solve(6, targets.LeftHand - rotation * offset - basis * Vector3.up * (.012f * (1 - amount)), bend);
        _limbs[8].rotation = rotation;
    }

    private void RestoreLeftPress(ref Exception? failure)
    {
        _leftPressFrame = -1;
        if (_leftFold != null)
            for (var i = _leftFold.Length - 1; i >= 0; i--) _leftFold[i]?.Restore(ref failure);
        _leftPress?.Restore(ref failure);
    }

    private sealed partial class FingerPose
    {
        internal static FingerPose? TryCreatePress(Transform hand, string name, Vector3 direction, Vector3 normal)
        {
            var bones = FindBones(hand, name);
            if (bones == null) return null;
            var hinge = Vector3.Cross(direction, normal).normalized;
            var rotations = new Quaternion[3];
            var parentRotation = hand.rotation;
            for (var i = 0; i < rotations.Length; i++)
            {
                var bone = bones[i];
                var segment = Quaternion.Inverse(bone.rotation) * (bones[i + 1].position - bone.position);
                var length = segment.magnitude;
                if (!float.IsFinite(length) || length < .001f) return null;
                // A relaxed index retains a slight bend rather than locking all
                // three native joints straight against the key surface.
                var angle = i == 0 ? 2 : i == 1 ? 6 : 12;
                var rotation = parentRotation * bone.localRotation;
                rotation = Quaternion.FromToRotation(rotation * segment,
                    Quaternion.AngleAxis(angle, hinge) * direction) * rotation;
                rotations[i] = Quaternion.Inverse(parentRotation) * rotation;
                parentRotation = rotation;
            }
            return new FingerPose(hand, bones, rotations);
        }

        internal Vector3 TipOffset => Quaternion.Inverse(_hand.rotation) * (_bones[3].position - _hand.position);
        internal Vector3 TipPosition => _bones[3].position;

        internal Vector3 PosedTipOffset
        {
            get
            {
                var offset = Quaternion.Inverse(_hand.rotation) * (_bones[0].position - _hand.position);
                var rotation = Quaternion.identity;
                for (var i = 0; i < _grip.Length; i++)
                {
                    // Rotation-only segment lengths retain the native skeleton's world scale.
                    var segment = Quaternion.Inverse(_bones[i].rotation) * (_bones[i + 1].position - _bones[i].position);
                    rotation *= _grip[i];
                    offset += rotation * segment;
                }
                return offset;
            }
        }
    }
}
