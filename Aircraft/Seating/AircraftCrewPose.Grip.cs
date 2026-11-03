// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientAircraftCrewPose
{
    private FingerPose?[]? _rightGrip;
    private bool _gripPrepared;

    private void PrepareRightGrip()
    {
        if (_gripPrepared) return;
        _gripPrepared = true;
        if (_rightHand == null) return;
        var hand = _limbs[11];
        var normal = hand.rotation * Quaternion.Inverse(_rightHand.Rotation) * Vector3.up;
        _rightGrip = new[]
        {
            FingerPose.TryCreate(hand, "RightHandIndex", normal, false),
            FingerPose.TryCreate(hand, "RightHandMiddle", normal, false),
            FingerPose.TryCreate(hand, "RightHandRing", normal, false),
            FingerPose.TryCreate(hand, "RightHandPinky", normal, false),
            FingerPose.TryCreate(hand, "RightHandThumb", normal, true)
        };
    }

    private void RenderRightGrip(float amount, int first = 0)
    {
        if (_rightGrip == null) return;
        for (var i = first; i < _rightGrip.Length; i++)
            if (_rightGrip[i] is { Current: true } finger) finger.Apply(amount);
    }

    private void RestoreRightGrip(ref Exception? failure)
    {
        if (_rightGrip == null) return;
        for (var i = _rightGrip.Length - 1; i >= 0; i--)
            _rightGrip[i]?.Restore(ref failure);
    }

    private sealed partial class FingerPose
    {
        private readonly Transform _hand;
        private readonly Transform[] _bones;
        private readonly Quaternion[] _grip;
        private readonly Quaternion[] _before = new Quaternion[3], _applied = new Quaternion[3];
        private bool _changed;

        private FingerPose(Transform hand, Transform[] bones, Quaternion[] grip)
        {
            _hand = hand; _bones = bones; _grip = grip;
        }

        internal static FingerPose? TryCreate(Transform hand, string name, Vector3 normal, bool thumb)
        {
            var bones = FindBones(hand, name);
            if (bones == null) return null;
            var first = bones[0];
            var second = bones[1];
            var direction = thumb ? second.position - first.position : first.position - hand.position;
            direction = Vector3.ProjectOnPlane(direction, normal);
            var magnitude = direction.magnitude;
            if (!float.IsFinite(magnitude) || magnitude < .001f) return null;
            direction /= magnitude;
            var hinge = Vector3.Cross(direction, normal).normalized;
            var rotations = new Quaternion[3];
            var parentRotation = hand.rotation;
            var previousArc = 0f;
            // The thumb base rises past the shell before its distal joints oppose across the deck.
            var angle = thumb ? 18f : 35f;
            for (var i = 0; i < rotations.Length; i++)
            {
                var bone = bones[i];
                var segment = Quaternion.Inverse(bone.rotation) * (bones[i + 1].position - bone.position);
                var length = segment.magnitude;
                if (!float.IsFinite(length) || length < .001f) return null;
                // An authored wrap around a 22 mm shaft plus finger padding. Native
                // segment lengths set the chord turns; no bone is translated or scaled.
                var arc = 2 * Mathf.Asin(Mathf.Clamp(length / .060f, 0, .95f)) * Mathf.Rad2Deg;
                if (i > 0) angle = thumb ? (i == 1 ? 70 : 100) : Mathf.Min(165, angle + (previousArc + arc) * .5f);
                var rotation = parentRotation * bone.localRotation;
                rotation = Quaternion.FromToRotation(rotation * segment,
                    Quaternion.AngleAxis(angle, hinge) * direction) * rotation;
                rotations[i] = Quaternion.Inverse(parentRotation) * rotation;
                parentRotation = rotation;
                previousArc = arc;
            }
            return new FingerPose(hand, bones, rotations);
        }

        private static Transform[]? FindBones(Transform hand, string name)
        {
            var first = hand.Find(name + "1");
            if (!Live(first) || !Same(first.parent, hand)) return null;
            var second = first.Find(name + "2");
            if (!Live(second) || !Same(second.parent, first)) return null;
            var third = second.Find(name + "3");
            if (!Live(third) || !Same(third.parent, second)) return null;
            var tip = third.Find(name + "4");
            if (!Live(tip) || !Same(tip.parent, third)) return null;
            return new[] { first, second, third, tip };
        }

        internal bool Current
        {
            get
            {
                if (!Live(_hand)) return false;
                for (var i = 0; i < _bones.Length; i++)
                    if (!Live(_bones[i]) || !Same(_bones[i].parent, i == 0 ? _hand : _bones[i - 1])) return false;
                return true;
            }
        }

        internal void Apply(float amount)
        {
            for (var i = 0; i < _before.Length; i++) _before[i] = _applied[i] = _bones[i].localRotation;
            _changed = true;
            for (var i = 0; i < _before.Length; i++)
            {
                _applied[i] = Quaternion.Slerp(_before[i], _grip[i], amount);
                _bones[i].localRotation = _applied[i];
            }
        }

        internal void Restore(ref Exception? failure)
        {
            if (!_changed) return;
            _changed = false;
            for (var i = _before.Length - 1; i >= 0; i--)
                try
                {
                    var bone = _bones[i];
                    if (Live(bone) && Same(bone.parent, i == 0 ? _hand : _bones[i - 1])
                        && Quaternion.Angle(bone.localRotation, _applied[i]) < .01f)
                        bone.localRotation = _before[i];
                }
                catch (Exception error) { failure ??= error; }
        }
    }
}
