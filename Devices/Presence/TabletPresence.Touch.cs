// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientTabletPresence
{
    private sealed partial class RemotePose
    {
        private readonly Quaternion[] _fingerBefore = new Quaternion[3], _fingerApplied = new Quaternion[3],
            _fingerContact = new Quaternion[3], _tabletFingerContact = new Quaternion[3];
        private Transform[] _index = null!;
        private int _changedFingers;
        private readonly TouchMotion _tabletTap = new(), _watchTap = new();

        private void PrepareTouchFinger()
        {
            _index = new[] { _indexTip.parent.parent.parent, _indexTip.parent.parent, _indexTip.parent, _indexTip };
            FitTouchFinger(_fingerContact, 6);
            // The right basis points out of the palm. Tablet contact curls into
            // the palm; keep the watch's existing contact frame separate.
            FitTouchFinger(_tabletFingerContact, -6);
        }

        private void FitTouchFinger(Quaternion[] contact, float bend)
        {
            var hand = _bones[5];
            var frame = hand.rotation * _rightBasis;
            var direction = frame * Vector3.forward;
            var hinge = Vector3.Cross(direction, frame * Vector3.up).normalized;
            var parentRotation = hand.rotation;
            // Fit a slightly bent pointing finger once; native weapon curl must not
            // become the reference direction for the wrist or each subsequent tap.
            for (var i = 0; i < 3; i++)
            {
                var bone = _index[i];
                var segment = Quaternion.Inverse(bone.rotation) * (_index[i + 1].position - bone.position);
                var rotation = parentRotation * bone.localRotation;
                rotation = Quaternion.FromToRotation(rotation * segment,
                    Quaternion.AngleAxis((i + 1) * bend, hinge) * direction) * rotation;
                contact[i] = Quaternion.Inverse(parentRotation) * rotation;
                parentRotation = rotation;
            }
        }

        private void SolveTouch(Transform screen, Vector3 contact, Vector3 elbowHint, float weight, bool tablet = false)
        {
            var fingers = tablet ? _tabletFingerContact : _fingerContact;
            for (var i = 0; i < 3; i++)
            {
                var bone = _index[i];
                _fingerBefore[i] = _fingerApplied[i] = bone.localRotation;
                _changedFingers = i + 1;
                bone.localRotation = Quaternion.Slerp(_fingerBefore[i], fingers[i], weight);
                _fingerApplied[i] = bone.localRotation;
            }
            // The remote tablet is pitched down 52 degrees. A mostly glass-normal
            // approach points down in actor space and puts the wrist above contact.
            var direction = screen.TransformDirection((tablet
                ? new Vector3(-.55f, .85f, .35f) : new Vector3(-.35f, .65f, 1)).normalized);
            var outward = tablet ? screen.right + screen.forward * .3f : screen.forward;
            var rotation = Quaternion.LookRotation(direction, outward) * Quaternion.Inverse(_rightBasis);
            var hand = _bones[5];
            // Rotation-only conversion retains world metres on the scaled player rig.
            var offset = Quaternion.Inverse(hand.rotation) * (_indexTip.position - hand.position);
            SolveArm(3, contact - rotation * offset, rotation, elbowHint, weight);
        }

        private void RestoreTouchFinger()
        {
            var count = _changedFingers;
            _changedFingers = 0;
            for (var i = count - 1; i >= 0; i--)
                if (Live(_index[i]) && Quaternion.Angle(_index[i].localRotation, _fingerApplied[i]) < .01f)
                    _index[i].localRotation = _fingerBefore[i];
        }

        private sealed class TouchMotion
        {
            private float _started = float.NegativeInfinity;
            private bool _held;

            internal float Sample(bool touching)
            {
                var now = Time.unscaledTime;
                if (touching && (!_held || now - _started >= .48f)) _started = now;
                _held = touching;
                var elapsed = now - _started;
                // Complete the approach/contact/return even if a snapshot ends the
                // touch early. Each new interaction starts above the glass.
                if (elapsed >= .4f) return 0;
                if (elapsed < .16f) return Mathf.SmoothStep(0, 1, elapsed / .16f);
                if (elapsed < .21f) return 1;
                return 1 - Mathf.SmoothStep(0, 1, (elapsed - .21f) / .19f);
            }

            internal void Reset() { _held = false; _started = float.NegativeInfinity; }
        }
    }
}
