// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using Sons.Animation;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Client;

// Only an owned native operation moves the arm. The watch itself follows the wrist.
internal sealed class ClientWristWatchPose : IDisposable
{
    private static ClientWristWatchPose? _active;
    private readonly Camera _camera;
    private readonly IkTargetsController _controller;
    private readonly Transform _hand;
    private readonly Quaternion _watchInHand;
    private readonly Vector3 _watchOffset;
    private readonly Func<bool> _context;
    private readonly Action _fitCuff;
    private readonly Func<float> _cuffDepth;
    private readonly Func<Transform?> _screen;
    private readonly Harmony _patches = new("modstoke.client.wristwatch-hand");
    private ClientTabletHandPose.HandOperation? _operation;
    private ClientWristWatchTouch? _touch;
    private IDisposable? _frameUpdate;
    private MethodInfo? _operationUpdate;
    private float _upperLength;
    private float _lowerToWatch;
    private Vector3 _restOffset;
    private Quaternion _restRotation;
    private Vector3 _restElbowOffset;
    private Vector3 _lastPosition;
    private Quaternion _lastRotation;
    private Vector3 _lastElbow;
    private float _startedAt;
    private int _solvedFrame = -1;
    private bool _disposed;
    private bool _fitted;
    private bool _ending;

    internal static bool Active => _active != null;
    internal bool Available => !_disposed && _operation?.Owned == true && SameRig();
    internal bool Inspecting => Available && !_ending && _operation?.Fading == false;
    internal bool ReducedMotion { get; set; }
    internal string Status { get; private set; } = "";

    internal ClientWristWatchPose(Camera camera, IkTargetsController controller, Transform hand,
        Quaternion watchInHand, Vector3 watchOffset, Func<bool> context, Action fitCuff, Func<float> cuffDepth,
        Func<Transform?> screen)
    {
        _camera = camera;
        _controller = controller;
        _hand = hand;
        _watchInHand = watchInHand;
        _watchOffset = watchOffset;
        _context = context;
        _fitCuff = fitCuff;
        _cuffDepth = cuffDepth;
        _screen = screen;
    }

    internal bool Begin()
    {
        if (_disposed || _active != null || ClientTabletHandPose.HasActiveSupport)
            return Refuse("Lower the tablet before inspecting the watch.");
        if (LocalPlayer.Inventory?.HeldOnlyItemController?.HasItem == true)
            return Refuse("Put down the carried logs or planks, then hold the watch key to inspect your wrist.");
        if (!SameRig() || !HandFree()) return Refuse("Finish the current hand action before checking the watch.");
        var arm = _controller._leftHandLimb?.solver;
        if (arm?.bone1?.transform == null || arm.bone2?.transform == null)
            return Refuse("The watch could not bind the player's arm.");
        _upperLength = (arm.bone2.transform.position - arm.bone1.transform.position).magnitude;
        // The strap center sits eight millimetres behind the wrist. Solve to
        // that center, then continue the forearm through the wrist and hand.
        _lowerToWatch = (_hand.position - arm.bone2.transform.position).magnitude - _watchOffset.magnitude;
        if (!float.IsFinite(_upperLength) || !float.IsFinite(_lowerToWatch)
            || _upperLength < .05f || _lowerToWatch < .05f
            || Mathf.Abs(_upperLength - _lowerToWatch) + .005f >= (_upperLength + _lowerToWatch) * .9f)
            return Refuse("The watch could not measure the player's arm.");
        // Rest follows the torso and shoulder, not later changes in view pitch.
        // Rotation-only offsets preserve world metres on the scaled player rig.
        var inverseBody = Quaternion.Inverse(_controller.transform.rotation);
        var shoulder = arm.bone1.transform.position;
        _restOffset = inverseBody * (_hand.position - shoulder);
        _restRotation = inverseBody * _hand.rotation;
        _restElbowOffset = inverseBody * (arm.bone2.transform.position - shoulder);
        var inverseCamera = Quaternion.Inverse(_camera.transform.rotation);
        _lastPosition = inverseCamera * (_hand.position - _camera.transform.position);
        _lastRotation = inverseCamera * _hand.rotation;
        _lastElbow = inverseCamera * (arm.bone2.transform.position - _camera.transform.position);
        _startedAt = Time.unscaledTime;
        _operation = ClientTabletHandPose.HandOperation.Begin(_controller, false, _hand.position, _hand.rotation, .15f);
        if (_operation == null) return Refuse("The game did not accept the watch hand target.");
        _operationUpdate = AccessTools.Method(typeof(IkTargetsController.IkOperationOffFakeBone),
            nameof(IkTargetsController.IkOperationOffFakeBone.UpdateIK));
        if (_operationUpdate == null) { Dispose(); return Refuse("The watch animation callback is unavailable."); }
        _active = this;
        try
        {
            _patches.Patch(_operationUpdate,
                prefix: new HarmonyMethod(typeof(ClientWristWatchPose), nameof(BeforeHandSolve)) { priority = Priority.Last });
            _frameUpdate = ClientTabletFrameUpdate.TryInstall(AfterNativeRig, out var failure);
            if (_frameUpdate == null) { Dispose(); return Refuse(failure); }
            return true;
        }
        catch { Dispose(); throw; }
    }

    private bool SameRig()
    {
        var animator = LocalPlayer.AnimControl;
        var biped = _controller != null ? _controller._bipped : null;
        return _camera != null && _camera.isActiveAndEnabled && Same(_camera, LocalPlayer.MainCam)
            && _controller != null && _controller.gameObject.activeInHierarchy && Same(_controller, LocalPlayer.IkTargetsController)
            && _hand != null && Same(_hand, _controller._leftHandLimb?.solver?.bone3?.transform)
            && animator != null && animator.isActiveAndEnabled && biped != null && biped.gameObject.activeInHierarchy
            && Same(biped.transform, animator.transform) && _hand.IsChildOf(biped.transform);
    }

    private bool HandFree() => _controller._leftHandIKOperation != null && !_controller._leftHandIKOperation._running
        && _controller._rightHandIKOperation != null && !_controller._rightHandIKOperation._running
        && !OtherAction() && LocalPlayer.Inventory?.LeftHandItem == null && LocalPlayer.Inventory?.RightHandItem == null;

    private bool OtherAction() => LocalPlayer.Inventory?.HeldOnlyItemController?.HasItem == true
        || _controller._weaponIKOperation?._running == true
        || _controller._aimIKOperation?._running == true || _controller._leftHeldRendererOnlyIKOperation?._running == true
        || _controller._rightHeldRendererOnlyIKOperation?._running == true;

    private static bool BeforeHandSolve(IkTargetsController.IkOperationOffFakeBone __instance)
    {
        var operation = _active?._operation;
        if (operation?.Matches(__instance) == true) return operation.ConsumeSolvePermit();
        var touch = _active?._touch;
        return touch?.Matches(__instance) != true || touch.ConsumeSolvePermit();
    }

    private static void AfterNativeRig()
    {
        var pose = _active;
        if (pose == null || pose._solvedFrame == Time.frameCount) return;
        pose._solvedFrame = Time.frameCount;
        try
        {
            pose._touch?.RestoreFingers();
            if (!pose.Available || pose.OtherAction()
                || LocalPlayer.Inventory?.LeftHandItem != null || LocalPlayer.Inventory?.RightHandItem != null
                || ClientTabletHandPose.HasActiveSupport)
            { pose.Release(); return; }
            if (!pose._context()) pose.EndInspection();
            var operation = pose._operation;
            if (operation?.Owned != true) { pose.Release(); return; }
            var camera = pose._camera.transform;
            if (operation.Fading)
            {
                // Native weight updates still run. Keep the target in wrist mode,
                // with an independent elbow and the late solve, until that blend ends.
                operation.Move(camera.position + camera.rotation * pose._lastPosition, camera.rotation * pose._lastRotation);
                operation.Solve(camera.position + camera.rotation * pose._lastElbow);
                return;
            }
            if (pose._ending)
            {
                // Keep the wrist still while the touching hand clears the glass.
                operation.Move(camera.position + camera.rotation * pose._lastPosition, camera.rotation * pose._lastRotation);
                operation.Solve(camera.position + camera.rotation * pose._lastElbow);
                pose._touch?.Solve();
                if (pose._touch == null || pose._touch.Finished) operation.Fade(.08f);
                return;
            }
            pose.PlaceArm(out var wrist, out var wristRotation, out var elbow);
            // Reduced motion removes the additional approach sweep. Native IK
            // still eases its weight, and neither mode adds idle wrist noise.
            var lift = pose.ReducedMotion ? 1 : Mathf.SmoothStep(0, 1, Mathf.Clamp01((Time.unscaledTime - pose._startedAt) / .28f));
            if (lift < 1)
            {
                var inverseCamera = Quaternion.Inverse(camera.rotation);
                var shoulder = inverseCamera * (pose._controller._leftHandLimb.solver.bone1.transform.position - camera.position);
                var bodyInCamera = inverseCamera * pose._controller.transform.rotation;
                wrist = Vector3.Lerp(shoulder + bodyInCamera * pose._restOffset, wrist, lift);
                wristRotation = Quaternion.Slerp(bodyInCamera * pose._restRotation, wristRotation, lift);
                elbow = Vector3.Lerp(shoulder + bodyInCamera * pose._restElbowOffset, elbow, lift);
            }
            pose._lastPosition = wrist;
            pose._lastRotation = wristRotation;
            pose._lastElbow = elbow;
            operation.Move(camera.position + camera.rotation * wrist, camera.rotation * wristRotation);
            operation.Solve(camera.position + camera.rotation * elbow);
            if (!pose._fitted && lift >= 1 && operation.Weight >= .999f)
            {
                pose._fitted = true;
                pose._fitCuff();
                pose._touch = new ClientWristWatchTouch(pose._camera, pose._controller, pose._screen);
                if (!pose._touch.Begin())
                {
                    pose.Status = "The watch could not place the touching hand. Finish the current hand action and try again.";
                    pose.EndInspection();
                    return;
                }
                var error = Quaternion.Angle(pose._hand.rotation, camera.rotation * wristRotation);
                Debug.Log("Modstoke Client: watch inspection raised; wrist target error " + error.ToString("F1") + " degrees.");
            }
            pose._touch?.Solve();
            if (!pose._ending && pose._touch != null && !pose._touch.Available)
            {
                pose.Status = "The watch released the touching hand because another action took control.";
                pose.EndInspection();
            }
        }
        catch (Exception error)
        {
            pose.Release();
            Debug.LogWarning("Modstoke Client: wristwatch pose released: " + error);
        }
    }

    private void PlaceArm(out Vector3 wrist, out Quaternion wristRotation, out Vector3 elbow)
    {
        var camera = _camera.transform;
        var shoulder = Quaternion.Inverse(camera.rotation)
            * (_controller._leftHandLimb.solver.bone1.transform.position - camera.position);
        // This target is independent of the solved hand. Reading the limb root
        // keeps the reachable pose correct when the player looks up or down.
        var center = new Vector3(0, -.08f,
            Mathf.Max(.30f, _camera.nearClipPlane + Mathf.Max(.12f, _cuffDepth() + .06f)));
        var delta = center - shoulder;
        var axis = delta.sqrMagnitude > .000001f ? delta.normalized : Vector3.forward;
        var distance = Mathf.Clamp(delta.magnitude, Mathf.Abs(_upperLength - _lowerToWatch) + .005f,
            (_upperLength + _lowerToWatch) * .9f);
        center = shoulder + axis * distance;

        var along = (_upperLength * _upperLength + distance * distance - _lowerToWatch * _lowerToWatch)
            / (2 * distance);
        var height = Mathf.Sqrt(Mathf.Max(0, _upperLength * _upperLength - along * along));
        var viewRay = center.normalized;
        var preferredHand = Vector3.ProjectOnPlane(Vector3.right, viewRay).normalized;
        var preferredElbow = center - _lowerToWatch * preferredHand;
        var bend = Vector3.ProjectOnPlane(preferredElbow - shoulder, axis);
        if (bend.sqrMagnitude < .000001f) bend = Vector3.ProjectOnPlane(Vector3.down, axis);
        elbow = shoulder + axis * along + bend.normalized * height;

        // Aim the hand along the reachable forearm instead of bending the wrist
        // to force a flat screen. Rotate about that axis to expose its dorsal side.
        var handAlong = (center - elbow).normalized;
        var palmAway = Vector3.ProjectOnPlane(viewRay, handAlong);
        if (palmAway.sqrMagnitude < .000001f) palmAway = Vector3.ProjectOnPlane(Vector3.up, handAlong);
        var watchRotation = Quaternion.LookRotation(palmAway.normalized, handAlong);
        wristRotation = watchRotation * Quaternion.Inverse(_watchInHand);
        wrist = center - wristRotation * _watchOffset;
    }

    internal void EndInspection(bool immediate = false)
    {
        if (_disposed || _operation == null) return;
        if (!Available) { Release(); return; }
        _ending = true;
        _touch?.End(immediate);
        if (immediate || _touch == null || _touch.Finished)
            _operation.Fade(immediate ? .01f : .08f);
    }

    internal bool TryTap(Vector2 point, Action onContact) => Inspecting && _fitted
        && _touch?.TryTap(point, onContact) == true;

    private void Release()
    {
        if (ReferenceEquals(_active, this)) _active = null;
        try { _touch?.Dispose(); _touch = null; }
        finally
        {
            try { _operation?.Stop(.01f); }
            finally { _operation = null; }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Release(); }
        finally
        {
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

    private bool Refuse(string message) { Status = message; return false; }
    private static bool Same(Il2CppObjectBase? first, Il2CppObjectBase? second) => first is not null
        && second is not null && !first.WasCollected && !second.WasCollected && first.Pointer == second.Pointer;
}
