// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime.InteropTypes;
using Sons.Animation;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Client;

// The watch pose owns the callback and solves its left arm before this right hand.
internal sealed class ClientWristWatchTouch : IDisposable
{
    private const float RaiseDuration = .28f;
    private const float HoldDuration = .045f;
    private const float ReturnDuration = .18f;
    private const float RetractDuration = .22f;
    private const float FadeDuration = .07f;
    private const float DisplayHeight = .044f;
    private const float DisplayWidth = DisplayHeight * 320 / 344;
    private static readonly Quaternion DisplayRotation = Quaternion.Euler(0, 0, 90);
    private static readonly Vector3 ReadyElbow = new(.40f, -.28f, .06f);
    private readonly Camera _camera;
    private readonly IkTargetsController _controller;
    private readonly Func<Transform?> _screen;
    private readonly Quaternion[] _before = new Quaternion[3];
    private readonly Quaternion[] _applied = new Quaternion[3];
    private readonly Quaternion[] _indexContact = new Quaternion[3];
    private readonly float[] _indexLengths = new float[3];
    private Transform? _hand;
    private Transform[]? _index;
    private ClientTabletHandPose.FingerContact[]? _fingers;
    private ClientTabletHandPose.HandOperation? _operation;
    private Quaternion _contactRotation;
    private Vector3 _indexBase;
    private Vector3 _restOffset;
    private Quaternion _restRotation;
    private Vector3 _restElbowOffset;
    private Vector3 _lastPosition;
    private Quaternion _lastRotation;
    private Vector3 _lastElbow;
    private Vector3 _endPosition;
    private Quaternion _endRotation;
    private Vector3 _endElbow;
    private Vector3 _contactPoint;
    private Action? _onContact;
    private float _phaseAt;
    private float _approachDuration;
    private float _fingerAmount;
    private float _endFingerAmount;
    private int _changedFingers;
    private Phase _phase;
    private bool _disposed;

    private enum Phase { Inactive, Raising, Hover, Approach, Contact, Returning, Retracting, Fading }

    internal ClientWristWatchTouch(Camera camera, IkTargetsController controller, Func<Transform?> screen)
    {
        _camera = camera;
        _controller = controller;
        _screen = screen;
    }

    internal bool Available => !_disposed && _operation?.Owned == true && !_operation.Fading
        && _phase is not (Phase.Inactive or Phase.Retracting or Phase.Fading)
        && SameRig() && !OtherAction() && _screen() != null;
    internal bool Busy => _phase is not (Phase.Inactive or Phase.Hover);
    internal bool Finished => _operation == null;

    internal bool Begin()
    {
        if (_disposed || _operation != null || _controller == null) return false;
        _hand = _controller._rightHandLimb?.solver?.bone3?.transform;
        var screen = _screen();
        if (!SameRig() || screen == null || OtherAction()
            || _controller._rightHandIKOperation == null || _controller._rightHandIKOperation._running)
            return false;
        var arm = _controller._rightHandLimb?.solver;
        var shoulder = arm?.bone1?.transform;
        var elbow = arm?.bone2?.transform;
        if (shoulder == null || elbow == null || !BindFingers() || !FitTouch(screen)) return false;

        // Rest stays with the shoulder and body when the view moves. Capture it
        // once; later taps must not feed a solved wrist back into this baseline.
        var camera = _camera.transform;
        var inverseBody = Quaternion.Inverse(_controller.transform.rotation);
        _restOffset = inverseBody * (_hand!.position - shoulder.position);
        _restRotation = inverseBody * _hand.rotation;
        _restElbowOffset = inverseBody * (elbow.position - shoulder.position);
        var inverseCamera = Quaternion.Inverse(camera.rotation);
        _lastPosition = inverseCamera * (_hand.position - camera.position);
        _lastRotation = inverseCamera * _hand.rotation;
        _lastElbow = inverseCamera * (elbow.position - camera.position);
        _fingerAmount = 0;
        _operation = ClientTabletHandPose.HandOperation.Begin(_controller, true, _hand.position, _hand.rotation, .14f);
        if (_operation == null) return false;
        _phaseAt = Time.unscaledTime;
        _phase = Phase.Raising;
        return true;
    }

    internal bool TryTap(Vector2 normalizedBottomLeft, Action onContact)
    {
        if (!Available || Busy || _phase != Phase.Hover || onContact == null
            || !float.IsFinite(normalizedBottomLeft.x) || !float.IsFinite(normalizedBottomLeft.y)
            || normalizedBottomLeft.x is < 0 or > 1 || normalizedBottomLeft.y is < 0 or > 1)
            return false;
        // The canvas rotates +90 degrees about the forearm-aligned glass frame.
        // These are world metres, independent of the player's 0.4 rig scale.
        _contactPoint = DisplayRotation * new Vector3((normalizedBottomLeft.x - .5f) * DisplayWidth,
            (normalizedBottomLeft.y - .5f) * DisplayHeight, -.008f);
        _approachDuration = Mathf.Clamp((ContactWrist(_contactPoint) - HoverWrist()).magnitude / .75f + .04f, .12f, .22f);
        _onContact = onContact;
        _phaseAt = Time.unscaledTime;
        _phase = Phase.Approach;
        return true;
    }

    internal bool Matches(IkTargetsController.IkOperationOffFakeBone operation) => _operation?.Matches(operation) == true;
    internal bool ConsumeSolvePermit() => _operation?.ConsumeSolvePermit() == true;

    internal void RestoreFingers()
    {
        var count = _changedFingers;
        _changedFingers = 0;
        if (_index != null)
            for (var i = 0; i < count; i++)
                if (_index[i] != null && Quaternion.Angle(_index[i].localRotation, _applied[i]) < .01f)
                    _index[i].localRotation = _before[i];
        if (_fingers != null)
            foreach (var finger in _fingers) finger.Restore();
    }

    internal void Solve()
    {
        var operation = _operation;
        if (_disposed || operation == null) return;
        RestoreFingers();
        if (!operation.Owned || !SameRig() || OtherAction()) { Release(); return; }
        var camera = _camera.transform;
        var inverseCamera = Quaternion.Inverse(camera.rotation);
        var shoulder = inverseCamera * (_controller._rightHandLimb.solver.bone1.transform.position - camera.position);
        var bodyInCamera = inverseCamera * _controller.transform.rotation;
        var restPosition = shoulder + bodyInCamera * _restOffset;
        var restRotation = bodyInCamera * _restRotation;
        var restElbow = shoulder + bodyInCamera * _restElbowOffset;
        var elapsed = Mathf.Max(0, Time.unscaledTime - _phaseAt);
        Vector3 position;
        Quaternion rotation;
        Vector3 elbow;
        float amount;
        if (_phase is Phase.Retracting or Phase.Fading)
        {
            var retract = _phase == Phase.Fading ? 1 : Ease(elapsed / RetractDuration);
            var local = Curve(_endPosition, _endPosition + Vector3.back * .025f,
                Vector3.Lerp(_endPosition, restPosition, .7f), restPosition, retract);
            position = camera.position + camera.rotation * local;
            rotation = camera.rotation * Quaternion.Slerp(_endRotation, restRotation, retract);
            elbow = Vector3.Lerp(_endElbow, restElbow, retract);
            amount = _endFingerAmount * (1 - retract);
        }
        else
        {
            var screen = _screen();
            if (screen == null) { End(false); return; }
            var hover = HoverWrist();
            var contact = ContactWrist(_contactPoint);
            var local = hover;
            if (_phase == Phase.Approach)
                local = Curve(hover, hover + Vector3.back * .012f, contact + Vector3.back * .025f,
                    contact, Ease(elapsed / _approachDuration));
            else if (_phase == Phase.Contact) local = contact;
            else if (_phase == Phase.Returning)
                local = Curve(contact, contact + Vector3.back * .025f, hover + Vector3.back * .012f,
                    hover, Ease(elapsed / ReturnDuration));
            position = screen.position + screen.rotation * local;
            rotation = screen.rotation * _contactRotation;
            amount = _phase == Phase.Raising ? Ease(elapsed / RaiseDuration) : 1;
            elbow = Vector3.Lerp(restElbow, ReadyElbow, amount);
            if (_phase == Phase.Raising)
            {
                position = Vector3.Lerp(camera.position + camera.rotation * restPosition, position, amount);
                rotation = Quaternion.Slerp(camera.rotation * restRotation, rotation, amount);
            }
        }

        _lastPosition = inverseCamera * (position - camera.position);
        _lastRotation = inverseCamera * rotation;
        _lastElbow = elbow;
        operation.Move(position, rotation);
        operation.Solve(camera.position + camera.rotation * elbow);
        if (!operation.Owned || !SameRig() || OtherAction()) { Release(); return; }
        _fingerAmount = Mathf.Min(amount, Mathf.Clamp01(operation.Weight));
        PoseFingers(_fingerAmount);

        if (_phase == Phase.Raising && elapsed >= RaiseDuration && operation.Weight >= .999f)
            _phase = Phase.Hover;
        else if (_phase == Phase.Approach && elapsed >= _approachDuration)
        {
            var screen = _screen();
            if (screen == null) { End(false); return; }
            var gap = (_index![3].position - (screen.position + screen.rotation * _contactPoint)).sqrMagnitude;
            if (operation.Weight >= .999f && float.IsFinite(gap) && gap <= .005f * .005f)
            {
                // Commit after the actual post-solve fingertip contact, exactly
                // once. A callback may itself close the watch or dispose this pose.
                var callback = _onContact;
                _onContact = null;
                _phase = Phase.Contact;
                _phaseAt = Time.unscaledTime;
                callback?.Invoke();
            }
            else if (elapsed >= _approachDuration + .16f)
            {
                _onContact = null;
                _phase = Phase.Returning;
                _phaseAt = Time.unscaledTime;
            }
        }
        else if (_phase == Phase.Contact && elapsed >= HoldDuration)
        { _phase = Phase.Returning; _phaseAt = Time.unscaledTime; }
        else if (_phase == Phase.Returning && elapsed >= ReturnDuration) _phase = Phase.Hover;
        else if (_phase == Phase.Retracting && elapsed >= RetractDuration)
        {
            operation.Fade(FadeDuration);
            _phase = Phase.Fading;
            _phaseAt = Time.unscaledTime;
        }
        else if (_phase == Phase.Fading && operation.Weight <= .001f) Release();
    }

    internal void End(bool immediate)
    {
        _onContact = null;
        if (_operation == null) return;
        if (immediate || !SameRig() || !_operation.Owned || OtherAction()) { Release(); return; }
        if (_phase is Phase.Retracting or Phase.Fading) return;
        _endPosition = _lastPosition;
        _endRotation = _lastRotation;
        _endElbow = _lastElbow;
        _endFingerAmount = _fingerAmount;
        _phaseAt = Time.unscaledTime;
        _phase = Phase.Retracting;
    }

    private bool BindFingers()
    {
        if (_hand == null) return false;
        var first = _hand.Find("RightHandIndex1");
        var second = first != null ? first.Find("RightHandIndex2") : null;
        var third = second != null ? second.Find("RightHandIndex3") : null;
        var tip = third != null ? third.Find("RightHandIndex4") : null;
        if (first == null || second == null || third == null || tip == null) return false;
        var middle = ClientTabletHandPose.FingerContact.Find(_hand, "RightHandMiddle", 0);
        var ring = ClientTabletHandPose.FingerContact.Find(_hand, "RightHandRing", 0);
        var pinky = ClientTabletHandPose.FingerContact.Find(_hand, "RightHandPinky", 0);
        var thumb = ClientTabletHandPose.FingerContact.Find(_hand, "RightHandThumb", 0);
        if (middle == null || ring == null || pinky == null || thumb == null) return false;
        _index = new[] { first, second, third, tip };
        _fingers = new[] { middle, ring, pinky, thumb };
        return true;
    }

    private bool FitTouch(Transform screen)
    {
        if (_hand == null || _index == null || _fingers == null) return false;
        var along = _fingers[0].Root.position - _hand.position;
        var across = _index[0].position - _fingers[2].Root.position;
        var normal = Vector3.Cross(along, across);
        if (along.sqrMagnitude < .000001f || normal.sqrMagnitude < .0000001f) return false;
        normal.Normalize();
        // The shipped right-hand knuckle order fixes the palm side. Animated
        // finger curl must not flip the wrist frame by half a turn.
        var direction = screen.rotation * FingerDirection(0);
        var inward = screen.rotation * (DisplayRotation * new Vector3(-1, 0, -.3f));
        var palm = Vector3.ProjectOnPlane(inward, direction).normalized;
        var rotation = Quaternion.LookRotation(direction, palm)
            * Quaternion.Inverse(Quaternion.LookRotation(along.normalized, normal)) * _hand.rotation;
        _contactRotation = Quaternion.Inverse(screen.rotation) * rotation;
        _indexBase = Quaternion.Inverse(_hand.rotation) * (_index[0].position - _hand.position);
        for (var i = 0; i < _indexLengths.Length; i++)
        {
            _indexLengths[i] = (_index[i + 1].position - _index[i].position).magnitude;
            if (!float.IsFinite(_indexLengths[i]) || _indexLengths[i] < .001f) return false;
        }
        // Fit the entire chain once. Complete local rotations preserve the
        // measured anatomy without inheriting new animated finger twist per frame.
        var contactToNative = _hand.rotation * Quaternion.Inverse(rotation);
        var parentRotation = _hand.rotation;
        for (var i = 0; i < _indexContact.Length; i++)
        {
            var bone = _index[i];
            var localSegment = Quaternion.Inverse(bone.rotation) * (_index[i + 1].position - bone.position);
            var boneRotation = parentRotation * bone.localRotation;
            boneRotation = Quaternion.FromToRotation(boneRotation * localSegment,
                contactToNative * (screen.rotation * FingerDirection(i))) * boneRotation;
            _indexContact[i] = Quaternion.Inverse(parentRotation) * boneRotation;
            parentRotation = boneRotation;
        }
        for (var i = 0; i < _fingers.Length; i++) _fingers[i].PrepareRelax(_hand, normal, i);
        return true;
    }

    private static Vector3 FingerDirection(int segment)
    {
        var direction = new Vector3(-.5f, .45f, 1).normalized;
        return DisplayRotation * Vector3.RotateTowards(direction, Vector3.back, (2 - segment) * 7 * Mathf.Deg2Rad, 0);
    }

    private Vector3 ContactWrist(Vector3 tip)
    {
        var wrist = tip - _contactRotation * _indexBase;
        for (var i = 0; i < _indexLengths.Length; i++) wrist -= FingerDirection(i) * _indexLengths[i];
        return wrist;
    }

    private Vector3 HoverWrist() => ContactWrist(DisplayRotation * new Vector3(.060f, -.045f, -.045f));

    private void PoseFingers(float amount)
    {
        if (amount <= 0 || _index == null || _fingers == null) return;
        for (var i = 0; i < 3; i++)
        {
            var bone = _index[i];
            _before[i] = bone.localRotation;
            bone.localRotation = Quaternion.Slerp(_before[i], _indexContact[i], amount);
            _applied[i] = bone.localRotation;
            _changedFingers = i + 1;
        }
        foreach (var finger in _fingers) finger.Relax(Mathf.Clamp01(amount * 1.25f));
    }

    private bool SameRig()
    {
        var animator = LocalPlayer.AnimControl;
        var biped = _controller != null ? _controller._bipped : null;
        return _camera != null && _camera.isActiveAndEnabled && Same(_camera, LocalPlayer.MainCam)
            && _controller != null && _controller.gameObject.activeInHierarchy && Same(_controller, LocalPlayer.IkTargetsController)
            && _hand != null && Same(_hand, _controller._rightHandLimb?.solver?.bone3?.transform)
            && animator != null && animator.isActiveAndEnabled && biped != null && biped.gameObject.activeInHierarchy
            && Same(biped.transform, animator.transform) && _hand.IsChildOf(biped.transform);
    }

    private bool OtherAction() => LocalPlayer.Inventory?.RightHandItem != null
        || LocalPlayer.Inventory?.HeldOnlyItemController?.HasItem == true || ClientTabletHandPose.HasActiveSupport
        || _controller._weaponIKOperation?._running == true || _controller._aimIKOperation?._running == true
        || _controller._rightHeldRendererOnlyIKOperation?._running == true;

    private void Release()
    {
        _onContact = null;
        _phase = Phase.Inactive;
        var operation = _operation;
        _operation = null;
        try { RestoreFingers(); }
        finally { operation?.Stop(.01f); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Release();
    }

    private static float Ease(float amount) => Mathf.SmoothStep(0, 1, Mathf.Clamp01(amount));
    private static Vector3 Curve(Vector3 start, Vector3 first, Vector3 second, Vector3 end, float amount)
    {
        var remaining = 1 - amount;
        return start * (remaining * remaining * remaining) + first * (3 * remaining * remaining * amount)
            + second * (3 * remaining * amount * amount) + end * (amount * amount * amount);
    }

    private static bool Same(Il2CppObjectBase? first, Il2CppObjectBase? second) => first is not null
        && second is not null && !first.WasCollected && !second.WasCollected && first.Pointer == second.Pointer;
}
