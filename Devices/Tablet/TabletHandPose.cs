// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using RootMotion.FinalIK;
using Sons.Animation;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Client;

// Native limb IK carries owned wrist targets; measured finger chains contact the housing and glass.
internal sealed class ClientTabletHandPose : IDisposable
{
    private const float TouchContact = 0.035f;
    private const float TouchLift = 0.11f;
    private const float TouchReady = 0.17f;
    private const float TouchRetract = 0.20f;
    private static ClientTabletHandPose? _active;
    private readonly Transform _screen;
    private readonly float _width;
    private readonly float _height;
    private readonly Action<Vector3, float, Vector3> _beforeIk;
    private readonly Func<bool> _context;
    private readonly Camera _camera;
    private readonly PlayerAnimatorControl? _animator;
    private readonly BipedIK? _biped;
    private readonly Harmony _patches = new("modstoke.client.tablet-hands");
    private IDisposable? _frameUpdate;
    private MethodInfo? _operationUpdate;
    private IkTargetsController? _controller;
    private Transform? _leftHand;
    private Transform? _rightHand;
    private Transform[]? _index;
    private FingerContact[]? _touchFingers;
    private FingerContact[]? _supportFingers;
    private FingerContact? _supportThumb;
    private readonly Quaternion[] _fingerBefore = new Quaternion[3];
    private readonly Quaternion[] _fingerApplied = new Quaternion[3];
    private readonly Quaternion[] _indexContact = new Quaternion[3];
    private int _changedFingers;
    private HandOperation? _support;
    private HandOperation? _tap;
    private Vector3 _wristPosition;
    private Quaternion _wristRotation;
    private Vector3 _supportForward;
    private Vector3 _touchForward;
    private float _supportReach;
    private Vector2 _press;
    private Vector2 _touchPoint;
    private Vector2 _touchVelocity;
    private Vector3 _tapStartPosition;
    private Quaternion _tapStartRotation;
    private Vector3 _tapRestOffset;
    private Quaternion _tapRestRotation;
    private Quaternion _contactRotation;
    private Vector3 _indexBase;
    private readonly float[] _indexLengths = new float[3];
    private float _tapAt;
    private float _approachDuration;
    private float _releaseAt;
    private float _touchStartAmount;
    private bool _contactHeld;
    private bool _touchRetiring;
    private bool _touchFitted;
    private bool _supportSolved;
    private int _solvedFrame = -1;
    private bool _disposed;

    internal ClientTabletHandPose(Transform screen, Camera camera, float width, float height,
        Action<Vector3, float, Vector3> beforeIk, Func<bool> context)
    {
        _screen = screen;
        _width = width;
        _height = height;
        _beforeIk = beforeIk;
        _context = context;
        _camera = camera;
        _animator = LocalPlayer.AnimControl;
        _controller = LocalPlayer.IkTargetsController;
        _biped = _controller?._bipped;
        // Read the actual solver hands, not another render rig beneath the player hierarchy.
        _leftHand = _controller?._leftHandLimb?.solver?.bone3?.transform;
        _rightHand = _controller?._rightHandLimb?.solver?.bone3?.transform;
        // This exact chain is present on the shipped PlayerNetwork/PlayerAnimator skeleton.
        if (_rightHand != null && _rightHand.Find("RightHandIndex1/RightHandIndex2/RightHandIndex3/RightHandIndex4") != null)
        {
            var first = _rightHand.Find("RightHandIndex1");
            var second = first.Find("RightHandIndex2");
            var third = second.Find("RightHandIndex3");
            _index = new[] { first, second, third, third.Find("RightHandIndex4") };
            var middle = FingerContact.Find(_rightHand, "RightHandMiddle", 0);
            var ring = FingerContact.Find(_rightHand, "RightHandRing", 0);
            var pinky = FingerContact.Find(_rightHand, "RightHandPinky", 0);
            var thumb = FingerContact.Find(_rightHand, "RightHandThumb", 0);
            if (middle != null && ring != null && pinky != null && thumb != null)
                _touchFingers = new[] { middle, ring, pinky, thumb };
        }
        if (_leftHand != null)
        {
            var fingers = new FingerContact?[4];
            var names = new[] { "Index", "Middle", "Ring", "Pinky" };
            // A shorter pinky needs less distal curl to reach the rear shell.
            var contactAngles = new[] { 60f, 65f, 60f, 45f };
            for (var i = 0; i < names.Length; i++)
                fingers[i] = FingerContact.Find(_leftHand, "LeftHand" + names[i], contactAngles[i]);
            _supportThumb = FingerContact.Find(_leftHand, "LeftHandThumb", 0);
            if (fingers[0] != null && fingers[1] != null && fingers[2] != null && fingers[3] != null)
                _supportFingers = new[] { fingers[0]!, fingers[1]!, fingers[2]!, fingers[3]! };
        }
    }

    // The controller disables BipedIK and the limb components in Awake, then
    // drives their solvers directly. Its animator must still belong to this rig.
    private bool RigAvailable => _animator != null && _animator.isActiveAndEnabled
        && Same(_animator, LocalPlayer.AnimControl)
        && _biped != null && _biped.gameObject.activeInHierarchy && Same(_biped, _controller?._bipped)
        && Same(_biped.transform, _animator.transform)
        && _camera != null && _camera.isActiveAndEnabled && Same(_camera, LocalPlayer.MainCam);
    internal string Status { get; private set; } = "The native hand pose is not ready.";
    internal static bool HasActiveSupport => _active != null;
    internal bool SupportAvailable => !_disposed && RigAvailable && _support?.Owned == true
        && (!_supportSolved || Time.frameCount - _solvedFrame <= 1);
    internal bool SupportReady => SupportAvailable && _supportSolved && _support!.Weight >= 0.999f;
    internal float IdleWeight => _tap == null ? 1 : 1 - ReachAmount();

    private bool FitGrip()
    {
        if (_leftHand == null || _supportFingers == null || _supportThumb == null) return false;
        // The wrist frame comes from the palm's anatomy, independent of the GPS clip.
        // Keep world metres: the player skeleton has a 0.4 scale, while the
        // tablet mount has unit scale. InverseTransformPoint removes that scale.
        var handToLocal = Quaternion.Inverse(_leftHand.rotation);
        var middle = handToLocal * (_supportFingers[1].Root.position - _leftHand.position);
        var across = handToLocal * (_supportFingers[0].Root.position - _supportFingers[3].Root.position);
        var normal = Vector3.Cross(middle, across);
        if (normal.sqrMagnitude < 0.0000001f || across.sqrMagnitude < 0.0000001f) return false;
        _supportForward = middle.normalized;
        _wristRotation = Quaternion.AngleAxis(-30, Vector3.up)
            * Quaternion.Inverse(Quaternion.LookRotation(normal.normalized, across.normalized));
        var roots = new Bounds(_wristRotation * middle, Vector3.zero);
        foreach (var finger in _supportFingers)
            roots.Encapsulate(_wristRotation * (handToLocal * (finger.Root.position - _leftHand.position)));
        if (roots.size.x > .22f || roots.size.y > .22f || roots.size.z > .22f) return false;
        // The four knuckles sit behind the shell. The oblique palm leaves the
        // thumb base outside the edge, free to reach the front bezel.
        _wristPosition = new Vector3(-_width * .5f - .030f - roots.max.x,
            -_height * .16f - roots.center.y, .046f - roots.min.z);
        return true;
    }

    internal bool BeginSupport()
    {
        var controller = _controller;
        if (_disposed || _active != null) return Refuse("Another tablet hand pose is still active.");
        if (ClientWristWatchPose.Active) return Refuse("Lower the wristwatch before taking out the tablet.");
        if (!RigAvailable) return Refuse("The tablet's player animation or camera binding is unavailable.");
        if (controller == null || !controller.gameObject.activeInHierarchy
            || _leftHand == null || _rightHand == null || _index == null || !Same(controller, LocalPlayer.IkTargetsController)
            || !_leftHand.IsChildOf(_biped!.transform) || !_rightHand.IsChildOf(_biped.transform)
            || _supportFingers == null || _supportThumb == null)
            return Refuse("The tablet could not bind the player's hand bones.");
        if (Busy(controller, false)) return Refuse("The native hand pose is busy. Finish the current action and try again.");
        if (!FitGrip()) return Refuse("The tablet could not fit the player's grip.");
        var arm = controller._leftHandLimb?.solver;
        if (arm?.bone1?.transform == null || arm.bone2?.transform == null)
            return Refuse("The tablet could not bind the player's arm.");
        _supportReach = ((arm.bone2.transform.position - arm.bone1.transform.position).magnitude
            + (_leftHand.position - arm.bone2.transform.position).magnitude) * .88f;
        if (!float.IsFinite(_supportReach) || _supportReach < .1f)
            return Refuse("The tablet could not measure the player's arm reach.");
        // An idle native controller is disabled by its own LateUpdate. BeginHandIK enables it.
        // Native SmoothWeightUpdate treats a zero duration as interpolation amount
        // zero, not an immediate transition; support would remain at zero weight.
        _support = HandOperation.Begin(controller, false, _leftHand.position, _leftHand.rotation, 0.12f);
        if (_support == null) return Refuse("The game did not accept the tablet hand target.");
        _operationUpdate = AccessTools.Method(typeof(IkTargetsController.IkOperationOffFakeBone), nameof(IkTargetsController.IkOperationOffFakeBone.UpdateIK));
        if (_operationUpdate == null)
        { Dispose(); return Refuse("The tablet's native animation callbacks are unavailable."); }
        _active = this;
        try
        {
            _patches.Patch(_operationUpdate,
                prefix: new HarmonyMethod(typeof(ClientTabletHandPose), nameof(BeforeHandSolve)) { priority = Priority.Last });
            _frameUpdate = ClientTabletFrameUpdate.TryInstall(AfterNativeRig, out var frameFailure);
            if (_frameUpdate == null)
            { Dispose(); return Refuse(frameFailure); }
            return true;
        }
        catch { Dispose(); throw; }
    }

    internal void UpdateContact(Vector2? normalized, bool down, bool held, bool up)
    {
        Update();
        var valid = normalized is { } point && float.IsFinite(point.x) && float.IsFinite(point.y);
        var inside = valid && normalized is { x: >= 0 and <= 1, y: >= 0 and <= 1 };
        // Capture the last sample before processing release. A captured drag may
        // leave the rectangle; its finger stays at the corresponding glass edge.
        if (_tap != null && _contactHeld && valid)
        {
            var captured = normalized!.Value;
            _press = new Vector2(Mathf.Clamp01(captured.x), Mathf.Clamp01(captured.y));
        }
        if (_tap != null && _contactHeld && (up || !held))
        {
            _contactHeld = false;
            // A quick click still reaches the glass without delaying its UI event.
            _releaseAt = Mathf.Max(Time.unscaledTime, _tapAt + _approachDuration + TouchContact);
        }
        if (down && inside && SupportAvailable && _controller != null && _rightHand != null
            && _index != null && LocalPlayer.Inventory?.RightHandItem == null
            && (_tap != null ? _tap.Owned && !_tap.Fading : !Busy(_controller, true)))
        {
            var startAmount = _tap != null ? ReachAmount() : 0;
            RestoreFinger();
            if (_tap == null)
            {
                if (!FitTouch()) return;
                _tap = HandOperation.Begin(_controller, true, _rightHand.position, _rightHand.rotation, 0.035f);
                if (_tap == null) return;
                // The resting hand belongs to the body, not the device being lowered.
                var inverseBody = Quaternion.Inverse(_controller.transform.rotation);
                var shoulder = _controller._rightHandLimb.solver.bone1.transform.position;
                _tapRestOffset = inverseBody * (_rightHand.position - shoulder);
                _tapRestRotation = inverseBody * _rightHand.rotation;
                _touchPoint = SurfacePoint(normalized!.Value);
                _touchVelocity = Vector2.zero;
            }
            _tapAt = Time.unscaledTime;
            _touchStartAmount = startAmount;
            _press = normalized!.Value;
            _contactHeld = held && !up;
            _touchRetiring = false;
            _tapStartPosition = _screen.InverseTransformPoint(_rightHand.position);
            _tapStartRotation = Quaternion.Inverse(_screen.rotation) * _rightHand.rotation;
            var distance = (ContactWrist(SurfacePoint(_press)) - _tapStartPosition).magnitude;
            _approachDuration = Mathf.Clamp(distance / 1.4f + .06f, .12f, .26f);
            _releaseAt = _contactHeld ? float.PositiveInfinity : _tapAt + _approachDuration + TouchContact;
        }
    }

    internal void Update()
    {
        if (_disposed) return;
        if (_tap != null && !_tap.Owned)
        {
            CancelContact();
        }
        else if (_tap != null && !_tap.Fading && !_contactHeld && RetractAmount() >= 1)
        {
            RestoreFinger();
            // Native stop blends for several frames. Keep the wrist-coordinate
            // target and independent elbow pole owned until that blend finishes.
            _tap.Fade(0.06f);
        }
    }

    internal void CancelContact()
    {
        var tap = _tap;
        _tap = null;
        _contactHeld = false;
        try { RestoreFinger(); }
        finally { tap?.Stop(0.01f); }
    }

    internal void ReleaseContact()
    {
        if (_tap == null || _tap.Fading || _rightHand == null) return;
        _touchStartAmount = ReachAmount();
        _tapStartPosition = _screen.InverseTransformPoint(_rightHand.position);
        _tapStartRotation = Quaternion.Inverse(_screen.rotation) * _rightHand.rotation;
        _contactHeld = false;
        _releaseAt = Time.unscaledTime;
        _touchRetiring = true;
    }

    private static bool BeforeHandSolve(IkTargetsController.IkOperationOffFakeBone __instance)
    {
        var pose = _active;
        if (pose == null) return true;
        // Keep native weight/lifecycle updates, but defer only our exact operations.
        // A replacement operation or any other player's hand always runs normally.
        if (pose._support?.Matches(__instance) == true) return pose._support.ConsumeSolvePermit();
        if (pose._tap?.Matches(__instance) == true) return pose._tap.ConsumeSolvePermit();
        return true;
    }

    private static void AfterNativeRig()
    {
        var pose = _active;
        if (pose == null || pose._solvedFrame == Time.frameCount) return;
        pose._solvedFrame = Time.frameCount;
        try
        {
            var controller = pose._controller!;
            if (!pose._context() || !pose.SupportAvailable || controller._weaponIKOperation?._running == true
                || controller._aimIKOperation?._running == true || controller._leftHeldRendererOnlyIKOperation?._running == true)
            { pose.ReleaseHands(); return; }
            if (controller._rightHeldRendererOnlyIKOperation?._running == true
                || LocalPlayer.Inventory?.RightHandItem != null) pose.CancelContact();
            // Solve after the complete PreLateUpdate phase, before renderer preparation.
            pose.RestoreFingers();
            pose.Update();
            // The limb root follows the current torso pose but is not moved by this arm solve.
            // Never feed the previously solved elbow or wrist back into the screen target.
            var camera = pose._camera.transform;
            var shoulder = controller._leftHandLimb.solver.bone1.transform.position;
            var shoulderInCamera = Quaternion.Inverse(camera.rotation) * (shoulder - camera.position);
            pose._beforeIk(shoulderInCamera, pose._supportReach, pose._wristPosition);
            pose._support!.Move(pose._screen.TransformPoint(pose._wristPosition), pose._screen.rotation * pose._wristRotation);
            pose._support!.Solve(pose._camera, pose._support.Target.rotation * pose._supportForward);
            if (pose.SupportAvailable && pose._leftHand != null && pose._support.Weight >= 1f)
            {
                var wasSupported = pose._supportSolved;
                pose._supportSolved = pose.PoseSupportFingers(out var failure);
                if (!pose._supportSolved)
                {
                    pose.Status = failure;
                    pose.RestoreFingers();
                    // The first native solve can still be settling. The owner
                    // keeps the casing hidden until contact succeeds and bounds
                    // this wait with its existing draw deadline.
                    if (wasSupported)
                    {
                        pose.ReleaseHands();
                        Debug.LogWarning("Modstoke Client: " + pose.Status);
                    }
                    return;
                }
                pose.Status = "";
            }
            if (pose._tap?.Owned != true || pose._index == null) return;
            // Contact uses the final supported screen, including any reach limit.
            pose.MoveTap();
            pose._tap.Solve(pose._camera, pose._tap.Target.rotation * pose._touchForward);
            var amount = pose.ReachAmount();
            for (var i = 0; i < 3; i++)
            {
                var bone = pose._index[i];
                pose._fingerBefore[i] = bone.localRotation;
                bone.localRotation = Quaternion.Slerp(pose._fingerBefore[i], pose._indexContact[i], amount);
                pose._fingerApplied[i] = bone.localRotation;
                pose._changedFingers = i + 1;
            }
            if (pose._touchFingers != null)
            {
                // Gather the spare fingers before reaching the glass and keep
                // them gathered until the hand is on its way back down.
                var curl = Mathf.SmoothStep(0, 1, Mathf.Clamp01(amount * 1.35f));
                foreach (var finger in pose._touchFingers) finger.Relax(curl);
            }
        }
        catch (Exception error)
        {
            pose.ReleaseHands();
            Debug.LogWarning("Modstoke Client: tablet finger pose released: " + error);
        }
    }

    private static Vector3 FingerDirection(int segment = 2)
    {
        var direction = new Vector3(-.35f, .65f, 1f).normalized;
        // Tablet-space directions: a softly bent index approaches the glass
        // obliquely, with its proximal links shallower than the fingertip.
        return Vector3.RotateTowards(direction, Vector3.back, (2 - segment) * 6 * Mathf.Deg2Rad, 0);
    }

    private bool FitTouch()
    {
        if (_rightHand == null || _index == null || _touchFingers == null) return false;
        if (_touchFitted) return true;
        var along = _touchFingers[0].Root.position - _rightHand.position;
        var across = _index[0].position - _touchFingers[2].Root.position;
        var normal = Vector3.Cross(along, across);
        if (along.sqrMagnitude < .000001f || normal.sqrMagnitude < .0000001f) return false;
        normal.Normalize();
        // The shipped right-hand knuckle order fixes the palm side. Animated
        // finger curl must not flip the wrist frame by half a turn.
        var pointDirection = _screen.rotation * FingerDirection(0);
        // Keep the measured side-on contact pose. A full-palm clearance search
        // can reject every candidate and prevent the native hand operation starting.
        var inward = -_screen.right - _screen.forward * .3f;
        var palmDirection = inward - pointDirection * Vector3.Dot(inward, pointDirection);
        var rotation = Quaternion.LookRotation(pointDirection, palmDirection.normalized)
            * Quaternion.Inverse(Quaternion.LookRotation(along.normalized, normal)) * _rightHand.rotation;
        var inverseHand = Quaternion.Inverse(_rightHand.rotation);
        _touchForward = inverseHand * along.normalized;
        _indexBase = inverseHand * (_index[0].position - _rightHand.position);
        for (var i = 0; i < _indexLengths.Length; i++)
        {
            _indexLengths[i] = (_index[i + 1].position - _index[i].position).magnitude;
            if (!float.IsFinite(_indexLengths[i]) || _indexLengths[i] < .001f) return false;
        }
        // Fit complete local rotations once in the measured hand frame. A
        // per-frame shortest swing would keep inheriting animated finger twist.
        var contactToNative = _rightHand.rotation * Quaternion.Inverse(rotation);
        var parentRotation = _rightHand.rotation;
        for (var i = 0; i < _indexContact.Length; i++)
        {
            var bone = _index[i];
            var localSegment = Quaternion.Inverse(bone.rotation) * (_index[i + 1].position - bone.position);
            var boneRotation = parentRotation * bone.localRotation;
            boneRotation = Quaternion.FromToRotation(boneRotation * localSegment,
                contactToNative * (_screen.rotation * FingerDirection(i))) * boneRotation;
            _indexContact[i] = Quaternion.Inverse(parentRotation) * boneRotation;
            parentRotation = boneRotation;
        }
        for (var i = 0; i < _touchFingers.Length; i++)
            _touchFingers[i].PrepareRelax(_rightHand, normal, i);
        _contactRotation = Quaternion.Inverse(_screen.rotation) * rotation;
        // The same tablet session keeps this measured wrist roll across taps.
        _touchFitted = true;
        return true;
    }

    private float ApproachAmount() => Mathf.SmoothStep(0, 1,
        Mathf.Clamp01((Mathf.Min(Time.unscaledTime, _releaseAt) - _tapAt) / _approachDuration));

    private float RetractAmount() => _contactHeld ? 0 : Mathf.SmoothStep(0, 1,
        Mathf.Clamp01((Time.unscaledTime - _releaseAt - (_touchRetiring ? 0 : TouchLift + TouchReady)) / TouchRetract));

    private float ReachAmount() => (_touchRetiring ? _touchStartAmount : Mathf.Lerp(_touchStartAmount, 1,
        Mathf.SmoothStep(0, 1, Mathf.Clamp01((Mathf.Min(Time.unscaledTime, _releaseAt) - _tapAt) / (_approachDuration * .7f)))))
        * (1 - RetractAmount());

    private Vector2 SurfacePoint(Vector2 normalized) => new((normalized.x - .5f) * _width * ClientTabletModel.DisplayScale,
        (normalized.y - .5f) * _height * ClientTabletModel.DisplayScale);

    private Vector3 ContactWrist(Vector2 point)
    {
        var wrist = new Vector3(point.x, point.y, -.012f) - _contactRotation * _indexBase;
        for (var i = 0; i < _indexLengths.Length; i++) wrist -= FingerDirection(i) * _indexLengths[i];
        return wrist;
    }

    private static Vector3 Curve(Vector3 start, Vector3 first, Vector3 second, Vector3 end, float amount)
    {
        var remaining = 1 - amount;
        return start * (remaining * remaining * remaining) + first * (3 * remaining * remaining * amount)
            + second * (3 * remaining * amount * amount) + end * (amount * amount * amount);
    }

    private void MoveTap()
    {
        if (_tap?.Owned != true || _index == null || _rightHand == null || _controller == null) return;
        // Follow the current shoulder without inheriting the solved upper-arm rotation.
        // Only the curve uses tablet coordinates; its rest endpoint stays with the player.
        var bodyRotation = _controller.transform.rotation;
        var shoulder = _controller._rightHandLimb.solver.bone1.transform.position;
        var restPosition = _screen.InverseTransformPoint(shoulder + bodyRotation * _tapRestOffset);
        var restRotation = bodyRotation * _tapRestRotation;
        var retract = RetractAmount();
        if (_touchRetiring)
        {
            // Holstering retracts directly from the current wrist, including a
            // partially completed tap or ready pose, without visiting glass again.
            var retreat = Curve(_tapStartPosition, _tapStartPosition + Vector3.back * .035f,
                Vector3.Lerp(_tapStartPosition, restPosition, .7f), restPosition, retract);
            _tap.Move(_screen.TransformPoint(retreat),
                Quaternion.Slerp(_screen.rotation * _tapStartRotation, restRotation, retract));
            return;
        }
        var handRotation = _screen.rotation * _contactRotation;
        // UI dispatch keeps the raw pointer. Only its visual fingertip follows a
        // continuous, speed-bounded path in the supported tablet's own frame.
        _touchPoint = Vector2.SmoothDamp(_touchPoint, SurfacePoint(_press), ref _touchVelocity,
            .055f, 1.6f, Time.unscaledDeltaTime);
        var wrist = ContactWrist(_touchPoint);
        var approach = ApproachAmount();
        var lift = _contactHeld ? 0 : Mathf.SmoothStep(0, 1,
            Mathf.Clamp01((Time.unscaledTime - _releaseAt) / TouchLift));
        var clearance = Mathf.Clamp((_tapStartPosition - wrist).magnitude * .25f, .025f, .075f);
        // The final control point is directly in front of contact, so approach
        // lands into the glass instead of sweeping the fingertip across it.
        var position = Curve(_tapStartPosition,
            Vector3.Lerp(_tapStartPosition, wrist, .35f) + Vector3.back * clearance,
            wrist + Vector3.back * clearance, wrist, approach) + Vector3.back * (lift * .018f);
        var rotation = Quaternion.Slerp(_screen.rotation * _tapStartRotation, handRotation, approach);
        position = Curve(position, position + Vector3.back * .035f,
            Vector3.Lerp(position, restPosition, .7f), restPosition, retract);
        _tap.Move(_screen.TransformPoint(position),
            Quaternion.Slerp(rotation, restRotation, retract));
    }

    private void RestoreFinger()
    {
        var count = _changedFingers;
        _changedFingers = 0;
        if (_index != null)
            for (var i = 0; i < count; i++)
                if (_index[i] != null && Quaternion.Angle(_index[i].localRotation, _fingerApplied[i]) < 0.01f)
                    _index[i].localRotation = _fingerBefore[i];
        if (_touchFingers != null)
            foreach (var finger in _touchFingers) finger.Restore();
    }

    private bool PoseSupportFingers(out string failure)
    {
        failure = "The tablet could not bind the supporting fingers.";
        if (_supportFingers == null || _supportThumb == null) return false;
        var left = -_width * 0.5f;
        // Housing rear is Z=.024; finger centres stay another centimetre behind it.
        // These contacts use the measured joints, rather than retaining a portrait GPS curl.
        foreach (var finger in _supportFingers)
        {
            var root = _screen.InverseTransformPoint(finger.Root.position);
            var y = Mathf.Clamp(root.y, -_height * 0.5f + 0.020f, _height * 0.5f - 0.020f);
            if (!finger.Solve(_screen.TransformPoint(new Vector3(left + 0.038f, y, 0.038f)),
                _screen, left - 0.022f, true))
            {
                failure = "The tablet could not fit the " + finger.Name + ": " + finger.Failure;
                return false;
            }
        }
        var indexRoot = _screen.InverseTransformPoint(_supportFingers[0].Root.position);
        if (!_supportThumb.Solve(_screen.TransformPoint(new Vector3(left - 0.006f,
            Mathf.Clamp(indexRoot.y + 0.025f, -_height * 0.40f, _height * 0.25f), -0.012f)),
            _screen, left - 0.022f, false))
        {
            failure = "The tablet could not fit the left thumb: " + _supportThumb.Failure;
            return false;
        }
        failure = "";
        return true;
    }

    private void RestoreFingers()
    {
        RestoreFinger();
        if (_supportFingers != null)
            foreach (var finger in _supportFingers) finger.Restore();
        _supportThumb?.Restore();
    }

    private static bool Busy(IkTargetsController controller, bool right)
    {
        var operation = right ? controller._rightHandIKOperation : controller._leftHandIKOperation;
        return operation == null || operation._running || controller._weaponIKOperation?._running == true
            || controller._aimIKOperation?._running == true
            || (right ? controller._rightHeldRendererOnlyIKOperation : controller._leftHeldRendererOnlyIKOperation)?._running == true;
    }

    private void ReleaseHands()
    {
        if (ReferenceEquals(_active, this)) _active = null;
        var tap = _tap;
        var support = _support;
        _tap = null;
        _support = null;
        _contactHeld = false;
        try { RestoreFingers(); }
        finally
        {
            try { tap?.Stop(0); }
            finally { support?.Stop(0); }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { ReleaseHands(); }
        finally
        {
            // A callback only releases its hands; the ordinary owner update removes
            // the frame entry and hand detour after that invocation has returned.
            try { _frameUpdate?.Dispose(); }
            finally
            {
                _frameUpdate = null;
                if (_operationUpdate != null)
                {
                    _patches.Unpatch(_operationUpdate, HarmonyPatchType.All, _patches.Id);
                    _operationUpdate = null;
                }
            }
        }
    }

    internal sealed class FingerContact
    {
        private readonly Transform[] _bones;
        private readonly Quaternion[] _before;
        private readonly Quaternion[] _applied;
        private readonly Quaternion[] _contact;
        private Quaternion[]? _relaxedRotations;
        private readonly float _contactAngle;
        private bool _changed;
        private bool _fitted;
        internal Transform Root => _bones[0];
        internal string Name { get; }
        internal string Failure { get; private set; } = "";

        private FingerContact(Transform[] bones, float contactAngle, string name)
        {
            _bones = bones;
            Name = name.Replace("LeftHand", "left ").Replace("RightHand", "right ").ToLowerInvariant();
            _before = new Quaternion[bones.Length - 1];
            _applied = new Quaternion[bones.Length - 1];
            _contact = new Quaternion[bones.Length - 1];
            _contactAngle = contactAngle;
        }

        internal static FingerContact? Find(Transform hand, string name, float contactAngle)
        {
            var first = hand.Find(name + "1");
            var second = first != null ? first.Find(name + "2") : null;
            var third = second != null ? second.Find(name + "3") : null;
            if (first == null || second == null || third == null) return null;
            // All five shipped PlayerAnimator left chains include a terminal bone 4.
            var tip = third.Find(name + "4");
            return tip != null ? new FingerContact(new[] { first, second, third, tip }, contactAngle, name) : null;
        }

        internal void PrepareRelax(Transform hand, Vector3 palmNormal, int digit)
        {
            var rotations = new Quaternion[_before.Length];
            for (var i = 0; i < rotations.Length; i++) rotations[i] = _bones[i].localRotation;
            _relaxedRotations = rotations;
            var thumb = digit == 3;
            var proximal = (_bones[1].position - _bones[0].position).normalized;
            var direction = thumb ? proximal : _bones[0].position - hand.position;
            direction -= palmNormal * Vector3.Dot(direction, palmNormal);
            if (direction.sqrMagnitude < .000001f) return;
            direction.Normalize();
            var hinge = Vector3.Cross(direction, palmNormal).normalized;
            var parentRotation = hand.rotation;
            for (var i = 0; i < rotations.Length; i++)
            {
                // Preserve each digit's measured fan across the palm. The thumb
                // has its own anatomical direction and a smaller opposing curl.
                var angle = thumb ? 10 + i * 11 : (i == 0 ? 28 : i == 1 ? 68 : 100) + digit * 5;
                var bone = _bones[i];
                var localSegment = Quaternion.Inverse(bone.rotation) * (_bones[i + 1].position - bone.position);
                var boneRotation = parentRotation * bone.localRotation;
                if (localSegment.sqrMagnitude > .000001f)
                    boneRotation = Quaternion.FromToRotation(boneRotation * localSegment,
                        Quaternion.AngleAxis(angle, hinge) * direction) * boneRotation;
                rotations[i] = Quaternion.Inverse(parentRotation) * boneRotation;
                parentRotation = boneRotation;
            }
        }

        internal void Relax(float amount)
        {
            if (_relaxedRotations == null) return;
            CaptureRotations();
            for (var i = 0; i < _before.Length; i++)
            {
                var bone = _bones[i];
                bone.localRotation = Quaternion.Slerp(_before[i], _relaxedRotations[i], amount);
                _applied[i] = bone.localRotation;
            }
        }

        internal bool Solve(Vector3 target, Transform screen, float outside, bool behind)
        {
            Failure = "";
            CaptureRotations();
            if (_fitted)
            {
                for (var i = 0; i < _contact.Length; i++) _bones[i].localRotation = _applied[i] = _contact[i];
                if (ContactFits(target, screen, outside, behind)) return true;
                _fitted = false;
            }
            // Curl the last segment toward the casing, then solve the first two
            // measured links. An unreachable clear seed is not a usable grip.
            var first = (_bones[1].position - _bones[0].position).magnitude;
            var second = (_bones[2].position - _bones[1].position).magnitude;
            var third = (_bones[3].position - _bones[2].position).magnitude;
            var contactDirection = Quaternion.AngleAxis(_contactAngle, screen.up) * screen.right;
            var distal = target - contactDirection * third;
            var root = _bones[0].position;
            var offset = distal - root;
            var distance = offset.magnitude;
            if (!float.IsFinite(distance) || first < .001f || second < .001f || third < .001f)
                return Reject("the finger measurements are unavailable.");
            if (distance < Mathf.Abs(first - second) + .0001f || distance > first + second - .0001f)
                return Reject(FormattableString.Invariant($"the bend needs {distance * 100:0.0} cm; reachable range is {Mathf.Abs(first - second) * 100:0.0}–{(first + second) * 100:0.0} cm."));
            var direction = offset / distance;
            var bend = behind ? screen.forward : -screen.right;
            bend -= direction * Vector3.Dot(bend, direction);
            if (bend.sqrMagnitude < .000001f) return Reject("there is no clear bend direction.");
            var along = (first * first + distance * distance - second * second) / (2 * distance);
            var elbow = root + direction * along
                + bend.normalized * Mathf.Sqrt(Mathf.Max(0, first * first - along * along));
            Aim(0, elbow);
            Aim(1, distal);
            Aim(2, target);
            if (!ContactFits(target, screen, outside, behind)) { Restore(); return false; }
            Array.Copy(_applied, _contact, _applied.Length);
            _fitted = true;
            return true;
        }

        private void CaptureRotations()
        {
            for (var i = 0; i < _before.Length; i++) _before[i] = _applied[i] = _bones[i].localRotation;
            _changed = true;
        }

        private bool Reject(string reason)
        {
            Failure = reason;
            Restore();
            return false;
        }

        private void Aim(int index, Vector3 target)
        {
            var bone = _bones[index];
            bone.rotation = Quaternion.FromToRotation(_bones[index + 1].position - bone.position,
                target - bone.position) * bone.rotation;
            _applied[index] = bone.localRotation;
        }

        private bool ContactFits(Vector3 target, Transform screen, float outside, bool behind)
        {
            var gapSquared = (_bones[^1].position - target).sqrMagnitude;
            if (!float.IsFinite(gapSquared) || gapSquared > .002f * .002f)
            {
                Failure = FormattableString.Invariant($"the tip remains {Mathf.Sqrt(gapSquared) * 1000:0.0} mm from its contact.");
                return false;
            }
            if (!ClearChain(screen, outside, behind)) return false;
            Failure = "";
            return true;
        }

        private bool ClearChain(Transform screen, float outside, bool behind)
        {
            for (var i = 0; i < _before.Length; i++)
            {
                var start = screen.InverseTransformPoint(_bones[i].position);
                var end = screen.InverseTransformPoint(_bones[i + 1].position);
                if (!ClearOfHousing(start, end, outside, behind))
                {
                    Failure = "finger segment " + (i + 1) + " would cross the casing.";
                    return false;
                }
            }
            return true;
        }

        private static bool ClearOfHousing(Vector3 start, Vector3 end, float outside, bool behind)
        {
            const float rear = 0.034f;
            if ((start.x <= outside && end.x <= outside) || (start.z >= rear && end.z >= rear)) return true;
            if (!behind && start.z <= -.010f && end.z <= -.010f) return true;
            if (start.x > outside && end.x > outside) return false;
            var crossing = (outside - start.x) / (end.x - start.x);
            var z = Mathf.Lerp(start.z, end.z, crossing);
            return (z >= rear && (start.x <= outside || start.z >= rear) && (end.x <= outside || end.z >= rear))
                || (!behind && z <= -.010f && (start.x <= outside || start.z <= -.010f) && (end.x <= outside || end.z <= -.010f));
        }

        internal void Restore()
        {
            if (!_changed) return;
            _changed = false;
            for (var i = 0; i < _before.Length; i++)
                if (_bones[i] != null && Quaternion.Angle(_bones[i].localRotation, _applied[i]) < 0.01f)
                    _bones[i].localRotation = _before[i];
        }
    }

    internal sealed class HandOperation
    {
        private readonly IkTargetsController _controller;
        private readonly IkTargetsController.IkOperationOffFakeBone _operation;
        private readonly IKSolverLimb _solver;
        private GameObject? _elbow;
        private readonly bool _right;
        private float _activation;
        private readonly Vector3 _relativePosition;
        private readonly Quaternion _relativeRotation;
        private IIkFakeBone? _nativeFakeBone;
        private bool _solving;
        private bool _solvePermit;
        private bool _released;
        internal Transform Target { get; }
        internal float Weight => _operation._currentWeight;
        internal bool Fading { get; private set; }

        private HandOperation(IkTargetsController controller, IkTargetsController.IkOperationOffFakeBone operation,
            IKSolverLimb solver, Transform target, bool right)
        {
            _controller = controller;
            _operation = operation;
            _solver = solver;
            Target = target;
            _right = right;
            _activation = operation._activationTime;
            _relativePosition = operation._relPos;
            _relativeRotation = operation._relRot;
        }

        internal bool Owned => !_released && _controller != null && _operation != null && Target != null
            && Same(_controller, LocalPlayer.IkTargetsController) && _operation._running
            && Same(_right ? _controller._rightHandIKOperation : _controller._leftHandIKOperation, _operation)
            && Same(_solver, _operation._solver)
            && Same(_solver, (_right ? _controller._rightHandLimb : _controller._leftHandLimb)?.solver)
            && Same(_operation._ikTarget, Target) && _operation._relativeTo == null && _operation._relativeTo2 == null
            && _operation._activationTime == _activation && _operation._targetPosWeight == (Fading ? 0 : 1)
            && _operation._relPos.Equals(_relativePosition) && _operation._relRot.Equals(_relativeRotation);

        internal static HandOperation? Begin(IkTargetsController controller, bool right,
            Vector3 position, Quaternion rotation, float duration)
        {
            var operation = right ? controller._rightHandIKOperation : controller._leftHandIKOperation;
            var solver = (right ? controller._rightHandLimb : controller._leftHandLimb)?.solver;
            if (operation == null || solver == null) return null;
            // maintainRotation=false lets the solver use the requested wrist orientation.
            controller.BeginHandIK(0, duration, position, rotation, right, false, out var target, out _, 1, null);
            if (target == null || !Same(operation._ikTarget, target)) return null;
            var owned = new HandOperation(controller, operation, solver, target, right);
            return owned.Owned ? owned : null;
        }

        internal void Move(Vector3 position, Quaternion rotation)
        {
            if (Owned) Target.SetPositionAndRotation(position, rotation);
        }

        internal bool Matches(IkTargetsController.IkOperationOffFakeBone operation) => Owned && Same(_operation, operation);

        internal bool ConsumeSolvePermit()
        {
            var permit = _solvePermit;
            _solvePermit = false;
            return permit;
        }

        internal void Solve(Camera camera, Vector3 handForward)
        {
            if (!Owned) return;
            var shoulder = _solver.bone1.transform.position;
            var elbow = _solver.bone2.transform.position;
            var upper = Vector3.Distance(shoulder, elbow);
            var lower = Vector3.Distance(elbow, _solver.bone3.transform.position);
            var offset = Target.position - shoulder;
            var distance = offset.magnitude;
            if (!float.IsFinite(upper) || !float.IsFinite(lower) || !float.IsFinite(distance)
                || upper <= .001f || lower <= .001f)
                throw new InvalidOperationException("The device arm has invalid limb measurements.");
            var axis = distance > .001f ? offset / distance : camera.transform.forward;
            distance = Mathf.Clamp(distance, Mathf.Abs(upper - lower) + .001f, upper + lower - .001f);
            var along = (upper * upper + distance * distance - lower * lower) / (2 * distance);
            var height = Mathf.Sqrt(Mathf.Max(0, upper * upper - along * along));
            // Align the forearm with the fitted palm. When that direction lies
            // on the reach axis, choose a body-relative down/out elbow plane.
            var bend = Vector3.ProjectOnPlane(-handForward.normalized, axis);
            if (bend.sqrMagnitude < .000001f)
            {
                var body = _controller.transform;
                bend = Vector3.ProjectOnPlane(-body.up + body.right * (_right ? .35f : -.35f), axis);
                if (bend.sqrMagnitude < .000001f)
                    bend = Vector3.ProjectOnPlane(body.forward, axis);
            }
            Solve(shoulder + axis * along + bend.normalized * height);
        }

        internal void Solve(Vector3 elbowPosition)
        {
            if (!Owned) return;
            _elbow ??= new GameObject("Field device elbow target");
            _elbow.transform.position = elbowPosition;
            var nativeGoal = _solver.bendGoal;
            var nativeModifier = _solver.bendModifier;
            var nativeWeight = _solver.bendModifierWeight;
            try
            {
                EnterSolve();
                _solvePermit = true;
                // Native elbow goals are children of the arms themselves. Borrow
                // an independent pole only while solving this exact owned hand.
                _solver.bendGoal = _elbow.transform;
                _solver.bendModifier = IKSolverLimb.BendModifier.Goal;
                _solver.bendModifierWeight = 1f;
                _operation.UpdateIK();
            }
            finally
            {
                try
                {
                    if (Same(_solver.bendGoal, _elbow.transform)) _solver.bendGoal = nativeGoal;
                    if (_solver.bendModifier == IKSolverLimb.BendModifier.Goal) _solver.bendModifier = nativeModifier;
                    if (_solver.bendModifierWeight == 1f) _solver.bendModifierWeight = nativeWeight;
                }
                finally
                {
                    _solvePermit = false;
                    ExitSolve();
                }
            }
        }

        internal void EnterSolve()
        {
            if (_solving || !Owned) return;
            _nativeFakeBone = _operation._fakeBone;
            _solving = true;
            // OffFakeBone.UpdateIK uses zero/identity offsets without a fake bone.
            // Borrow that wrist mode only for this native solve, never between frames.
            _operation._fakeBone = null;
        }

        internal void ExitSolve()
        {
            if (!_solving) return;
            _solving = false;
            var nativeFakeBone = _nativeFakeBone;
            _nativeFakeBone = null;
            // Field ownership differs from operation ownership: a replacement operation
            // must recover its native fake bone too, but a new registration wins.
            if (_operation != null && _operation._fakeBone == null) _operation._fakeBone = nativeFakeBone;
        }

        internal void Fade(float duration)
        {
            if (Fading || !Owned) return;
            _controller.StopTwoHandsIK(Mathf.Max(duration, 0.01f), _right, !_right);
            // This exact native transition changes the activation and target
            // weight. Every other operation/target/solver identity stays pinned.
            _activation = _operation._activationTime;
            Fading = true;
        }

        internal void Stop(float duration)
        {
            if (_released) return;
            var owned = false;
            try { owned = Owned; }
            finally
            {
                _released = true;
                _solvePermit = false;
                try { ExitSolve(); }
                finally
                {
                    try
                    {
                        // A zero native stop duration never advances the blend and strands the
                        // hand operation across close/reopen. Keep native teardown progressing.
                        if (owned) _controller.StopTwoHandsIK(Mathf.Max(duration, 0.01f), _right, !_right);
                    }
                    finally
                    {
                        var elbow = _elbow;
                        _elbow = null;
                        if (elbow != null) UnityEngine.Object.Destroy(elbow);
                    }
                }
            }
        }
    }

    private static bool Same(Il2CppObjectBase? first, Il2CppObjectBase? second) => first is not null
        && second is not null && !first.WasCollected && !second.WasCollected && first.Pointer == second.Pointer;

    private bool Refuse(string status) { Status = status; return false; }
}
