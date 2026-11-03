// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Modstoke.Sons;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftRider
{
    private ClientSpacecraftBody? _controlBody;
    private Capture? _controlCapture;
    private SpacecraftControlContact _controlContact;
    private Vector3 _controlActualContact;
    private float _controlReach, _controlDeadline, _controlReachRetryAt;
    private float? _controlGuardAmount;
    private bool _controlReady, _controlRetracting;
    private int _controlContactFrame = -1;
    private int _consoleContactFrame = -1;
    private SpacecraftConsoleControl _consoleContactControl;
    private ClientAircraftCrewPose? _controlReachPose;
    private (int Load, IntPtr Player, IntPtr Ik, IntPtr Biped, IntPtr References,
        IntPtr UpperArm, IntPtr Forearm, IntPtr Hand) _controlReachBinding;
    private bool _controlReachWarned;

    internal bool ConsolePressReached(ClientSpacecraftBody body, SpacecraftConsoleControl control)
    {
        if (_consoleContactFrame != Time.frameCount || _consoleContactControl != control
            || control is SpacecraftConsoleControl.None or SpacecraftConsoleControl.Flight
            || !ReferenceEquals(body, _spacecraft)
            || Controls.ConsoleControl != control || Controls.ConsoleReach < 1) return false;
        try
        {
            if (!body.Ready || !body.TelemetryCurrent || !ConsoleInput || !_focusCurrent() || !_equipment.Ready)
                return false;
            var target = body.SeatConsoleContact(Seat, control);
            return target != null && target && _capture is { Current: true, Alive: true }
                && _capture.TryGetLeftPressContact(out var actualContact)
                && (actualContact - target.position).sqrMagnitude <= .012f * .012f;
        }
        catch (Exception error) { Fail("The cockpit hand contact could not be maintained.", error); return false; }
    }

    internal bool ControlHandActive => _controlBody != null;
    internal string ControlPressFailure { get; private set; } = "";

    internal bool CanReachControl(ClientSpacecraftBody body, SpacecraftControlContact contact)
    {
        if (_disposed) { ClearControlReachBinding(); return false; }
        try
        {
            var player = LocalPlayer.Transform;
            var ik = LocalPlayer.IkTargetsController;
            var biped = Live(ik) ? ik._bipped : null;
            var references = Live(biped) ? biped.references : null;
            if (references?.WasCollected == true) references = null;
            var upper = references?.rightUpperArm;
            var lower = references?.rightForearm;
            var hand = references?.rightHand;
            var binding = (SaveLifecycle.State.BeforeLoadCount,
                Live(player) ? player.Pointer : IntPtr.Zero, Live(ik) ? ik.Pointer : IntPtr.Zero,
                Live(biped) ? biped.Pointer : IntPtr.Zero, references?.Pointer ?? IntPtr.Zero,
                Live(upper) ? upper.Pointer : IntPtr.Zero, Live(lower) ? lower.Pointer : IntPtr.Zero,
                Live(hand) ? hand.Pointer : IntPtr.Zero);
            if (binding != _controlReachBinding)
            {
                ClearControlReachBinding();
                _controlReachBinding = binding;
            }
            if (_controlReachPose == null && Time.realtimeSinceStartup < _controlReachRetryAt) return false;
            if (!body.Ready || !Live(body.Root) || !contact.Press
                || !Finite(contact.Position) || !Finite(contact.Rotation)) return false;
            var animator = LocalPlayer.Animator;
            var animControl = LocalPlayer.AnimControl;
            if (!Live(player) || !Live(animator) || !animator.isActiveAndEnabled
                || !Live(animControl) || !animControl.isActiveAndEnabled
                || !Live(ik) || !ik.gameObject.activeInHierarchy
                || !Live(biped) || !biped.gameObject.activeInHierarchy
                || !Same(animControl.transform, biped.transform)) return false;
            if (!Live(upper) || !Live(lower) || !Live(hand)
                || !Same(upper, ik._rightHandLimb?.solver?.bone1?.transform)
                || !Same(lower, ik._rightHandLimb?.solver?.bone2?.transform)
                || !Same(hand, ik._rightHandLimb?.solver?.bone3?.transform)) return false;
            if (_controlReachPose == null)
            {
                // This read-only pose is never rendered; actual rig binding still follows equipment stashing.
                _controlReachPose = ClientAircraftCrewPose.TryCreate(player, biped, true);
                if (_controlReachPose == null)
                    return RejectControlReachBinding("The native press rig could not be bound.");
            }
            var pose = _controlReachPose;
            if (pose is not { Current: true })
                return RejectControlReachBinding("The native press rig is no longer current.");
            var frame = body.Root.transform;
            if (!pose.TryCanReachRightPress(frame.TransformPoint(contact.Position),
                frame.rotation * contact.Rotation, out var reachable))
                return RejectControlReachBinding("The native press rig could not be read.");
            return reachable;
        }
        catch (Exception error)
        { return RejectControlReachBinding("The native reach query failed.", error); }
    }

    private bool RejectControlReachBinding(string reason, Exception? error = null)
    {
        _controlReachPose = null;
        _controlReachRetryAt = Time.realtimeSinceStartup + 1;
        if (!_controlReachWarned)
        {
            _controlReachWarned = true;
            Log.LogWarning("Spacecraft control reach preflight: " + reason + " Load=" + _controlReachBinding.Load
                + " Player=" + _controlReachBinding.Player + " Rig=" + _controlReachBinding.Biped
                + (error == null ? "" : " " + error));
        }
        return false;
    }

    private void ClearControlReachBinding()
    {
        // The probe never writes bones, so dropping these references requires no pose restoration.
        _controlReachPose = null;
        _controlReachBinding = default;
        _controlReachRetryAt = 0;
        _controlReachWarned = false;
    }

    internal bool BeginControlPress(ClientSpacecraftBody body, SpacecraftControlContact contact, out string reason)
    {
        reason = "Finish your current action before using this control.";
        if (_disposed || ControlHandActive || !Active && Restoring) return false;
        try
        {
            if (!body.Ready || !body.TelemetryCurrent || !Live(body.Root)
                || body.State.Travel != SpacecraftTravel.None || !WorldInputAvailable)
            { reason = "The spacecraft control is not available."; return false; }
            if (!contact.Press || !Finite(contact.Position) || !Finite(contact.Rotation))
            { reason = "The spacecraft control contact is unavailable."; return false; }
            if (_extinguisher.Active)
            { reason = "Put away the held station tool before using this control."; return false; }
            if (Active && (Faulted || Suspended || !_attached || !Walking || _standBlend < 1
                || !ReferenceEquals(body, _spacecraft)))
            { reason = "Finish standing before using this control."; return false; }
            var capture = Active ? _capture : Capture.TryCreate();
            if (capture is not { Current: true, Alive: true })
            { reason = "Your player is not ready to use this control."; return false; }
            if (Active && (!_focus || !_focusCurrent()))
            { Fail("Spacecraft input ownership was lost."); reason = FailureReason; return false; }
            if (!CanReachControl(body, contact))
            { reason = "Move closer to the control and free your hands before trying again."; return false; }

            _controlBody = body;
            _controlContact = contact;
            _controlGuardAmount = ClientSpacecraftStations.IsApuGuardContact(contact.Position, body.State.Systems.ApuGuardAmount)
                ? body.State.Systems.ApuGuardAmount : null;
            _controlReach = 0;
            _controlContactFrame = -1;
            _controlReady = Active;
            _controlRetracting = false;
            _controlDeadline = Time.unscaledTime + 10;
            ControlPressFailure = "";
            if (!Active)
            {
                _controlCapture = capture;
                capture.RememberCursor();
                if (!_acquireFocus())
                { CancelControlPress(reason); return false; }
                _focus = true;
                if (!_focusCurrent() || !capture.Current || !capture.Alive)
                {
                    reason = "Your player or input ownership changed while preparing the control.";
                    CancelControlPress(reason);
                    return false;
                }
                if (!_equipment.TryStash(out _))
                {
                    reason = "Put away your held items before using this control.";
                    CancelControlPress(reason);
                    return false;
                }
            }
            if (!ControlPressCurrent(out reason))
            { CancelControlPress(reason); return false; }
            reason = "";
            return true;
        }
        catch (Exception error)
        {
            reason = "Your hand could not be prepared. Free your hands and try again.";
            CancelControlPress(reason, error);
            return false;
        }
    }

    internal bool ControlPressReached(ClientSpacecraftBody body)
    {
        if (!ReferenceEquals(body, _controlBody) || _controlRetracting || !_controlReady
            || _controlReach < 1 || _controlContactFrame != Time.frameCount) return false;
        try
        {
            if (!_equipment.Ready)
            { CancelControlPress("Your hands changed. Free your hands and try again."); return false; }
            if (!ControlPressCurrent(out var reason))
            { CancelControlPress(reason); return false; }
            var target = body.Root.transform.TransformPoint(_controlContact.Position);
            return (_controlActualContact - target).sqrMagnitude <= .012f * .012f;
        }
        catch (Exception error)
        { CancelControlPress("The control contact could not be maintained.", error); return false; }
    }

    internal void EndControlPress(ClientSpacecraftBody body, bool immediate = false)
    {
        if (!ReferenceEquals(body, _controlBody)) return;
        _controlContactFrame = -1;
        if (immediate || !_controlReady || _controlReach <= 0) ReleaseControlPress(true);
        else _controlRetracting = true;
    }

    private bool ControlPressCurrent(out string reason)
    {
        reason = "The spacecraft control is no longer available.";
        if (_controlBody is not { Ready: true, TelemetryCurrent: true } body || !Live(body.Root)
            || body.State.Travel != SpacecraftTravel.None) return false;
        reason = "Your player changed while using the control.";
        var capture = _controlCapture ?? _capture;
        if (capture is not { Current: true, Alive: true }) return false;
        if (_controlCapture != null ? Active : Faulted || Suspended || !_attached || !Walking || _standBlend < 1
            || !ReferenceEquals(body, _spacecraft)) return false;
        reason = "Spacecraft input ownership was lost.";
        if (!_focus || !_focusCurrent()) return false;
        reason = "The control press was interrupted.";
        if (!WorldInputAvailable || _exitRequested) return false;
        reason = "Put away the held station tool before using this control.";
        if (_extinguisher.Active) return false;
        reason = "Move closer to the control and free your hands before trying again.";
        if (!_controlRetracting && !CanReachControl(body, _controlContact)) return false;
        reason = "";
        return true;
    }

    private void UpdateControlPress()
    {
        if (!ControlHandActive) return;
        _controlContactFrame = -1;
        try
        {
            // Undo last frame before native animation; an unreserved capture never takes movement or camera ownership.
            _controlCapture?.RestoreControlPose();
            if (!ControlPressCurrent(out var reason))
            { CancelControlPress(reason); return; }
            var ready = _equipment.Ready;
            if (!_equipment.Active || _controlReady && !ready)
            { CancelControlPress("Your hands changed. Free your hands and try again."); return; }
            if (!_controlReady)
            {
                if (Time.unscaledTime >= _controlDeadline)
                { CancelControlPress("Your hand could not be prepared. Free your hands and try again."); return; }
                if (!ready) return;
                // Stashing invokes native callbacks; revalidate the same actor and focus before binding its rig.
                if (!ControlPressCurrent(out reason))
                { CancelControlPress(reason); return; }
                if (!_controlCapture!.TryBindRig()) return;
                _controlReady = true;
            }
            if (!ControlPressCurrent(out reason))
            { CancelControlPress(reason); return; }
            var dt = Mathf.Clamp(Time.unscaledDeltaTime, 0, .05f);
            _controlReach = Mathf.MoveTowards(_controlReach, _controlRetracting ? 0 : 1,
                dt / (_controlRetracting ? .25f : .22f));
            if (_controlRetracting && _controlReach <= 0) ReleaseControlPress(true);
        }
        catch (Exception error) { CancelControlPress("The control press could not continue.", error); }
    }

    private void RenderControlPress()
    {
        _controlContactFrame = -1;
        if (!ControlHandActive) return;
        try
        {
            if (!ControlPressCurrent(out var reason))
            { CancelControlPress(reason); return; }
            if (!_controlReady || _controlReach <= 0) return;
            if (!_equipment.Ready)
            { CancelControlPress("Your hands changed. Free your hands and try again."); return; }
            if (!ControlPressCurrent(out reason))
            { CancelControlPress(reason); return; }
            var body = _controlBody!;
            if (_controlRetracting && _controlGuardAmount is { } previous)
            {
                var current = body.State.Systems.ApuGuardAmount;
                _controlContact = ClientSpacecraftStations.MoveApuGuardContact(_controlContact, previous, current);
                _controlGuardAmount = current;
            }
            var frame = body.Root.transform;
            var target = frame.TransformPoint(_controlContact.Position);
            var capture = _controlCapture ?? _capture!;
            var rendered = capture.TryRenderRightPress(target, frame.up, frame.rotation * _controlContact.Rotation,
                _controlReach, out _controlActualContact);
            if (_controlRetracting || _controlReach < 1) return;
            if (!rendered || (_controlActualContact - target).sqrMagnitude > .012f * .012f)
            { CancelControlPress("Move closer to the control and free your hands before trying again."); return; }
            _controlContactFrame = Time.frameCount;
        }
        catch (Exception error) { CancelControlPress("The control contact could not be maintained.", error); }
    }

    private void CancelControlPress(string reason, Exception? error = null)
    {
        ControlPressFailure = reason;
        if (error != null) Log.LogWarning(reason + " " + error);
        ReleaseControlPress(true);
    }

    private void ReleaseControlPress(bool restoreEquipment)
    {
        if (!restoreEquipment || _disposed) ClearControlReachBinding();
        if (!ControlHandActive && _controlCapture == null) return;
        var controlCapture = _controlCapture;
        var capture = controlCapture ?? _capture;
        _controlBody = null;
        _controlCapture = null;
        _controlReady = _controlRetracting = false;
        _controlReach = 0;
        _controlGuardAmount = null;
        _controlContactFrame = -1;
        var canRestore = false;
        try { canRestore = restoreEquipment && capture?.SameActor == true && capture.Alive; }
        catch (Exception error) { Log.LogWarning("Spacecraft control equipment ownership: " + error); }
        try
        {
            if (controlCapture != null) controlCapture.ReleaseControlRig();
            else capture?.RestoreControlPose();
        }
        catch (Exception error) { Log.LogWarning("Spacecraft control hand restoration: " + error); }
        if (controlCapture == null) return;
        ReleaseInput(controlCapture);
        _equipment.Finish(canRestore);
    }

    private static bool Finite(Quaternion value) => float.IsFinite(value.x) && float.IsFinite(value.y)
        && float.IsFinite(value.z) && float.IsFinite(value.w) && MathF.Abs(Quaternion.Dot(value, value) - 1) < .02f;
}
