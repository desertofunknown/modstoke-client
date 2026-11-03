// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Il2CppInterop.Runtime.InteropTypes;
using RootMotion;
using RootMotion.FinalIK;
using UnityEngine;

namespace Modstoke.Client;

// A final-frame layer shared by the native local and remote skeletons. The caller
// owns actor/attachment identity; this layer owns only these exact rig transforms.
internal sealed partial class ClientAircraftCrewPose
{
    private readonly BipedIK _biped;
    private readonly BipedReferences _references;
    private readonly Transform _mesh, _pelvis, _poseRoot;
    private readonly Transform[] _limbs, _referenceLimbs, _torso;
    private readonly Quaternion[] _torsoRest, _before, _applied;
    private readonly Quaternion _poseBasis, _leftFootBasis, _rightFootBasis;
    private readonly HandContact? _leftHand, _rightHand;
    private readonly bool _firstPerson;
    private readonly Transform? _head;
    private Vector3 _beforePosition, _appliedPosition;
    private Quaternion _beforeRotation, _appliedRotation;
    private bool _changed;
    private readonly Quaternion[] _rightBefore = new Quaternion[3], _rightApplied = new Quaternion[3];
    private bool _rightChanged;
    private float _reachBlend;
    private int _frame = -1;

    private ClientAircraftCrewPose(Transform actor, BipedIK biped, BipedReferences references,
        Transform[] limbs, Transform[] referenceLimbs, Transform[] torso, Transform? head)
    {
        _biped = biped; _references = references; _mesh = biped.transform; _pelvis = references.pelvis;
        _limbs = limbs; _referenceLimbs = referenceLimbs; _torso = torso; _head = head;
        _firstPerson = head != null;
        // The local BipedIK may live beside the player transform or on it.
        // Seat its pelvis without moving a native player/network root. The
        // remote path retains its separately validated presentation mesh.
        _poseRoot = _firstPerson ? _pelvis : _mesh;
        _before = new Quaternion[limbs.Length + torso.Length]; _applied = new Quaternion[_before.Length];
        _torsoRest = new Quaternion[torso.Length];
        for (var i = 0; i < torso.Length; i++) _torsoRest[i] = torso[i].localRotation;
        var inverse = Quaternion.Inverse(actor.rotation);
        _poseBasis = inverse * _poseRoot.rotation;
        _leftFootBasis = inverse * limbs[2].rotation; _rightFootBasis = inverse * limbs[5].rotation;
        _leftHand = HandContact.TryCreate(limbs[8], true);
        _rightHand = HandContact.TryCreate(limbs[11], false);
    }

    internal static ClientAircraftCrewPose? TryCreate(Transform actor, BipedIK biped, bool firstPerson)
    {
        if (!Live(actor) || !Live(biped) || !biped.gameObject.activeInHierarchy
            || !firstPerson && (Same(actor, biped.transform) || !biped.transform.IsChildOf(actor))) return null;
        var references = biped.references;
        if (references == null || references.WasCollected || !Live(references.pelvis)
            || !references.pelvis.IsChildOf(biped.transform)) return null;
        Transform[] limbs = { references.leftThigh, references.leftCalf, references.leftFoot,
            references.rightThigh, references.rightCalf, references.rightFoot,
            references.leftUpperArm, references.leftForearm, references.leftHand,
            references.rightUpperArm, references.rightForearm, references.rightHand };
        var referenceLimbs = limbs;
        if (!firstPerson)
        {
            limbs = (Transform[])limbs.Clone();
            if (!BindRemoteLeg(limbs, 0, "LeftLeg", "LeftFoot", "leftFootSpawnPos")
                || !BindRemoteLeg(limbs, 3, "RightLeg", "RightFoot", "rightFootSpawnPos")) return null;
        }
        for (var i = 0; i < limbs.Length; i++)
        {
            if (!Live(limbs[i]) || !limbs[i].IsChildOf(biped.transform)) return null;
            for (var j = 0; j < i; j++) if (Same(limbs[i], limbs[j])) return null;
        }
        for (var start = 0; start < limbs.Length; start += 3)
            if (!limbs[start + 1].IsChildOf(limbs[start]) || !limbs[start + 2].IsChildOf(limbs[start + 1])) return null;
        var torso = new List<Transform> { references.pelvis };
        var spine = references.spine;
        if (spine == null || spine.WasCollected || spine.Length > 8) return null;
        foreach (var bone in spine)
        {
            if (!Live(bone) || !bone.IsChildOf(references.pelvis)) return null;
            if (torso.Exists(saved => Same(saved, bone))) return null;
            torso.Add(bone);
        }
        Transform? head = null;
        if (firstPerson)
        {
            head = references.head;
            if (!Live(head) || !head.IsChildOf(references.pelvis)) return null;
        }
        return new ClientAircraftCrewPose(actor, biped, references, limbs, referenceLimbs, torso.ToArray(), head);
    }

    private static bool BindRemoteLeg(Transform[] limbs, int start, string calfName, string footName, string markerName)
    {
        var thigh = limbs[start]; var foot = limbs[start + 1]; var marker = limbs[start + 2];
        if (!Live(thigh) || !Live(foot) || !Live(marker)) return false;
        if (marker.name != markerName) return true;
        // PlayerNetwork's BipedIK uses the ankle as its calf and a toe-end spawn
        // marker as its foot. The seat solve needs the actual knee and ankle.
        var calf = thigh.Find(calfName);
        if (!Live(calf) || foot.name != footName || !Same(foot.parent, calf) || !marker.IsChildOf(foot)) return false;
        limbs[start + 1] = calf; limbs[start + 2] = foot;
        return true;
    }

    internal bool Current
    {
        get
        {
            if (!Live(_biped) || !Same(_biped.references, _references) || !Live(_mesh)
                || !Same(_biped.transform, _mesh) || !Live(_pelvis) || !Same(_references.pelvis, _pelvis)) return false;
            for (var i = 0; i < _limbs.Length; i++)
                if (!Live(_limbs[i]) || !Same(_referenceLimbs[i], CurrentLimb(i)) || !_limbs[i].IsChildOf(_mesh)) return false;
            if (!_firstPerson)
            {
                if (!Live(_referenceLimbs[2]) || !Live(_referenceLimbs[5])) return false;
                for (var start = 0; start < 6; start += 3)
                    if (!_limbs[start + 1].IsChildOf(_limbs[start])
                        || !_limbs[start + 2].IsChildOf(_limbs[start + 1])
                        || !_referenceLimbs[start + 2].IsChildOf(_limbs[start + 2])) return false;
            }
            var spine = _references.spine;
            if (spine == null || spine.WasCollected || spine.Length != _torso.Length - 1) return false;
            for (var i = 1; i < _torso.Length; i++)
                if (!Live(_torso[i]) || !Same(_torso[i], spine[i - 1]) || !_torso[i].IsChildOf(_pelvis)) return false;
            return !_firstPerson || Live(_head) && Same(_references.head, _head) && _head.IsChildOf(_pelvis);
        }
    }

    private Transform CurrentLimb(int index) => index switch
    {
        0 => _references.leftThigh, 1 => _references.leftCalf, 2 => _references.leftFoot,
        3 => _references.rightThigh, 4 => _references.rightCalf, 5 => _references.rightFoot,
        6 => _references.leftUpperArm, 7 => _references.leftForearm, 8 => _references.leftHand,
        9 => _references.rightUpperArm, 10 => _references.rightForearm, _ => _references.rightHand
    };

    private Transform Bone(int index) => index < _limbs.Length ? _limbs[index] : _torso[index - _limbs.Length];

    internal void Render(AircraftCrewTargets targets, int seat, float sitBlend = 1)
    {
        if (_frame == Time.frameCount || !Current) return;
        _frame = Time.frameCount;
        Restore();
        var gripping = float.IsFinite(targets.RightHandGrip) && targets.RightHandGrip > 0;
        if (gripping) PrepareRightGrip();
        var pressing = float.IsFinite(targets.LeftHandPress) && targets.LeftHandPress > 0
            && targets.LeftReachRotation.HasValue;
        if (pressing) PrepareLeftPress();
        sitBlend = Mathf.Clamp01(sitBlend);
        _beforePosition = _poseRoot.localPosition; _beforeRotation = _poseRoot.localRotation;
        for (var i = 0; i < _before.Length; i++) _before[i] = _applied[i] = Bone(i).localRotation;
        _changed = true;
        try
        {
            // Native camera/aim animation keeps running, but its torso sway must
            // not pull a seated chest out through the fixed shoulder harness.
            for (var i = 0; i < _torso.Length; i++) _torso[i].localRotation = _torsoRest[i];
            _poseRoot.rotation = targets.FrameRotation * _poseBasis;
            _poseRoot.position += targets.Pelvis - _pelvis.position;
            var forward = targets.FrameRotation * Vector3.forward;
            var right = targets.FrameRotation * Vector3.right;
            var up = targets.FrameRotation * Vector3.up;
            Solve(0, targets.LeftFoot, forward - right * .12f);
            Solve(3, targets.RightFoot, forward + right * .12f);
            _limbs[2].rotation = targets.FrameRotation * _leftFootBasis;
            _limbs[5].rotation = targets.FrameRotation * _rightFootBasis;
            if (pressing && _leftPress is { Current: true })
                PoseLeftPress(targets, forward, seat == 0 ? right : -up, -right - up * .45f, sitBlend);
            else
                PoseHand(6, targets.LeftHand, forward, seat == 0 ? right : -up, -right - up * .45f, _leftHand);
            _reachBlend = Mathf.MoveTowards(_reachBlend, targets.PilotReaching ? 1 : 0, Time.unscaledDeltaTime * 6);
            var rightPalm = seat == 0 ? Vector3.Lerp(-right, -up, _reachBlend).normalized : -up;
            var rightForward = forward;
            if (targets.RightReachRotation is { } reachRotation)
            {
                var reachForward = reachRotation * Vector3.forward;
                var reachNormal = reachRotation * Vector3.up;
                // Surface reaches can expose an outward normal; a grip instead
                // supplies the complete palm orientation through either roll angle.
                if (!gripping && Vector3.Dot(reachNormal, up) > 0)
                    reachNormal = -reachNormal;
                var rotation = Quaternion.Slerp(Quaternion.LookRotation(forward, -right),
                    Quaternion.LookRotation(reachForward, reachNormal), _reachBlend);
                rightForward = rotation * Vector3.forward;
                rightPalm = rotation * Vector3.up;
            }
            PoseHand(9, targets.RightHand, rightForward, rightPalm,
                right - up * .45f, _rightHand);
            // Spacecraft boarding eases from the current native standing pose.
            // Aircraft callers retain the original fully seated default.
            if (sitBlend < 1)
            {
                _poseRoot.localPosition = Vector3.Lerp(_beforePosition, _poseRoot.localPosition, sitBlend);
                if (!_firstPerson) _poseRoot.localRotation = Quaternion.Slerp(_beforeRotation, _poseRoot.localRotation, sitBlend);
                for (var i = 0; i < _before.Length; i++)
                    Bone(i).localRotation = Quaternion.Slerp(_before[i], Bone(i).localRotation, sitBlend);
            }
            if (gripping && sitBlend > 0)
                RenderRightGrip(Mathf.SmoothStep(0, 1, Mathf.Clamp01(targets.RightHandGrip)) * sitBlend);
            if (pressing && targets.LeftHandPress >= 1 && sitBlend >= 1 && _leftPress is { Current: true })
                _leftPressFrame = Time.frameCount;
        }
        finally
        {
            if (Live(_poseRoot)) { _appliedPosition = _poseRoot.localPosition; _appliedRotation = _poseRoot.localRotation; }
            for (var i = 0; i < _applied.Length; i++) if (Live(Bone(i))) _applied[i] = Bone(i).localRotation;
        }
    }

    internal void RenderRightHand(Vector3 contact, Vector3 up, float reach,
        Quaternion? contactRotation = null, bool press = false)
    {
        if (_frame == Time.frameCount || !Current || !float.IsFinite(reach) || reach <= 0) return;
        _frame = Time.frameCount;
        Restore();
        var forward = up;
        Vector3 normal;
        if (contactRotation is { } basis)
        {
            forward = basis * Vector3.forward;
            normal = basis * Vector3.up;
            if (press) PrepareRightPress();
        }
        else
        {
            normal = Vector3.ProjectOnPlane(contact - _limbs[9].position, up);
            if (normal.sqrMagnitude < .0001f) return;
            normal.Normalize();
        }
        if (press && contactRotation.HasValue && _rightPress is not { Current: true }) return;
        for (var i = 0; i < 3; i++) _rightBefore[i] = _rightApplied[i] = _limbs[9 + i].localRotation;
        _rightChanged = true;
        try
        {
            var amount = Mathf.SmoothStep(0, 1, Mathf.Clamp01(reach));
            var bend = _mesh.right - up * .45f;
            if (press && contactRotation.HasValue)
                PoseRightPress(contact, contactRotation.Value, bend, amount);
            else
            {
                PoseHand(9, contact, forward, normal, bend, _rightHand);
                for (var i = 0; i < 3; i++)
                    _limbs[9 + i].localRotation = Quaternion.Slerp(_rightBefore[i], _limbs[9 + i].localRotation, amount);
            }
        }
        finally
        {
            for (var i = 0; i < 3; i++) if (Live(_limbs[9 + i])) _rightApplied[i] = _limbs[9 + i].localRotation;
        }
    }

    internal bool RenderRightHandGrip(Vector3 contact, Vector3 up, Quaternion rotation, out Vector3 actualContact)
    {
        actualContact = default;
        if (_frame == Time.frameCount || !Current || _rightHand == null) return false;
        PrepareRightGrip();
        RenderRightHand(contact, up, 1, rotation);
        if (!_rightChanged) return false;
        RenderRightGrip(.85f);
        var hand = _limbs[11];
        // Follow the solved palm, including the reach clamp and native skeleton scale.
        actualContact = hand.position + hand.rotation * _rightHand.PalmOffset + rotation * Vector3.up * .012f;
        return true;
    }

    private void PoseHand(int start, Vector3 contact, Vector3 forward, Vector3 normal, Vector3 bend, HandContact? hand)
    {
        if (hand == null) { Solve(start, contact, bend); return; }
        var rotation = Quaternion.LookRotation(forward, normal) * hand.Rotation;
        Solve(start, contact - rotation * hand.PalmOffset - normal * .012f, bend);
        _limbs[start + 2].rotation = rotation;
    }

    private void Solve(int start, Vector3 target, Vector3 bend)
    {
        var upper = _limbs[start]; var lower = _limbs[start + 1]; var end = _limbs[start + 2];
        var origin = upper.position;
        var a = Vector3.Distance(origin, lower.position); var b = Vector3.Distance(lower.position, end.position);
        if (!float.IsFinite(a) || !float.IsFinite(b) || a < .03f || b < .03f) return;
        var direction = target - origin;
        if (direction.sqrMagnitude < .0001f) return;
        var distance = Mathf.Clamp(direction.magnitude, Mathf.Abs(a - b) + .01f, (a + b) * .97f);
        direction.Normalize(); target = origin + direction * distance;
        var pole = Vector3.ProjectOnPlane(bend, direction).normalized;
        if (pole.sqrMagnitude < .5f) return;
        var along = (a * a - b * b + distance * distance) / (2 * distance);
        var across = Mathf.Sqrt(Mathf.Max(0, a * a - along * along));
        var elbow = origin + direction * along + pole * across;
        upper.rotation = Quaternion.FromToRotation(lower.position - origin, elbow - origin) * upper.rotation;
        lower.rotation = Quaternion.FromToRotation(end.position - lower.position, target - lower.position) * lower.rotation;
    }

    internal void Restore()
    {
        Exception? failure = null;
        RestoreLeftPress(ref failure);
        _rightPress?.Restore(ref failure);
        RestoreRightGrip(ref failure);
        if (_rightChanged)
        {
            _rightChanged = false;
            for (var i = 2; i >= 0; i--)
                try
                {
                    var bone = _limbs[9 + i];
                    if (Live(bone) && Quaternion.Angle(bone.localRotation, _rightApplied[i]) < .01f)
                        bone.localRotation = _rightBefore[i];
                }
                catch (Exception error) { failure ??= error; }
        }
        if (_changed)
        {
            _changed = false;
            for (var i = _before.Length - 1; i >= 0; i--)
                try
                {
                    var bone = Bone(i);
                    if (Live(bone) && Quaternion.Angle(bone.localRotation, _applied[i]) < .01f) bone.localRotation = _before[i];
                }
                catch (Exception error) { failure ??= error; }
            try
            {
                if (Live(_poseRoot))
                {
                    if ((_poseRoot.localPosition - _appliedPosition).sqrMagnitude < .000001f) _poseRoot.localPosition = _beforePosition;
                    if (Quaternion.Angle(_poseRoot.localRotation, _appliedRotation) < .01f) _poseRoot.localRotation = _beforeRotation;
                }
            }
            catch (Exception error) { failure ??= error; }
        }
        if (failure != null) throw failure;
    }

    private sealed record HandContact(Quaternion Rotation, Vector3 PalmOffset)
    {
        internal static HandContact? TryCreate(Transform hand, bool left)
        {
            var prefix = left ? "LeftHand" : "RightHand";
            var middle = hand.Find(prefix + "Middle1"); var index = hand.Find(prefix + "Index1"); var pinky = hand.Find(prefix + "Pinky1");
            if (!Live(middle) || !Live(index) || !Live(pinky)) return null;
            // World metres in a rotation-only frame; the native skeleton is scaled.
            var inverse = Quaternion.Inverse(hand.rotation);
            var along = inverse * (middle.position - hand.position);
            var across = inverse * (index.position - pinky.position);
            var normal = Vector3.Cross(along, across) * (left ? -1 : 1);
            if (along.sqrMagnitude < .000001f || normal.sqrMagnitude < .0000001f) return null;
            return new HandContact(Quaternion.Inverse(Quaternion.LookRotation(along.normalized, normal.normalized)), along * .65f);
        }
    }

    private static bool Live([NotNullWhen(true)] UnityEngine.Object? value) => value != null && !value.WasCollected && value;
    private static bool Same(Il2CppObjectBase? first, Il2CppObjectBase? second)
        => first != null && second != null && !first.WasCollected && !second.WasCollected && first.Pointer == second.Pointer;
}
