// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using BepInEx.Logging;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Modstoke.Sons;
using RootMotion.FinalIK;
using Sons.Animation;
using Sons.Input;
using Sons.Save;
using TheForest.Items.Inventory;
using TheForest.Utils;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using GameInput = global::Sons.Input.InputSystem;

namespace Modstoke.Client;

// Owns only the current local actor. PlayerNetwork remains active and publishes its
// ordinary owner transform; the spacecraft's host separately owns the seat lease.
internal sealed partial class ClientSpacecraftRider : IDisposable
{
    private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("Modstoke spacecraft rider");
    private readonly Func<bool> _acquireFocus;
    private readonly Action _releaseFocus;
    private readonly Func<bool> _focusCurrent;
    private readonly Action<string>? _command;
    private readonly ClientWatchEquipment _equipment = new();
    private ClientSpacecraftBody? _spacecraft;
    private Capture? _capture;
    private bool _focus, _attached, _disposed, _pointer, _exitRequested;
    private float _deadline, _lookYaw, _lookPitch;
    private float _sitBlend, _viewYaw, _viewPitch, _panelUntil, _consoleUntil;
    private Vector3 _boardingPosition, _boardingEye, _panelTranslation, _panelRotation;
    private Quaternion _boardingRotation, _boardingView;
    private bool _panelBoost, _panelRightButton;
    private GameObject? _uiRoot, _pointerRoot;
    private EventSystem? _uiSystem, _previousUi, _navigationSystem;
    private InputActionAsset? _uiActions;
    private bool _navigationEnabled;
    private Vector3 _cabinPosition, _attachedPosition, _walkVelocity, _flowVelocity;
    private Quaternion _attachedRotation;
    private bool _outside;
    private float _standBlend = 1;
    private SpacecraftCrewInteraction _interaction;
    private SpacecraftCrewInteraction _renderInteraction;
    private BoxStationModel? _extinguisherStation;
    private BoxStationFirefightingHands? _extinguisherOwner;
    private SpacecraftHeldStationTool _extinguisher;
    private Vector3 _extinguisherPosition;
    private Quaternion _extinguisherRotation;
    private int _extinguisherFrame = -1, _extinguisherPoseFrame = -1;
    private readonly Il2CppStructArray<RaycastHit> _walkHits = new(size: 64);
    private readonly Il2CppReferenceArray<Collider> _walkOverlaps = new(size: 64);

    internal ClientSpacecraftRider(Func<bool> acquireFocus, Action releaseFocus, Func<bool> focusCurrent,
        Action<string>? command = null)
    {
        _acquireFocus = acquireFocus ?? throw new ArgumentNullException(nameof(acquireFocus));
        _releaseFocus = releaseFocus ?? throw new ArgumentNullException(nameof(releaseFocus));
        _focusCurrent = focusCurrent ?? throw new ArgumentNullException(nameof(focusCurrent));
        _command = command;
    }

    internal bool Active => _spacecraft != null;
    internal bool Attached => Walking || Seated;
    internal bool Walking { get; private set; }
    internal Vector3 CabinPosition => _cabinPosition;
    internal bool Restoring => !Active && (_controlCapture != null || _equipment.Active);
    internal int Seat { get; private set; } = -1;
    // A new capture or return to the same chair must retire its previous pending press.
    internal uint SeatRevision { get; private set; }
    internal SpacecraftControls Controls { get; private set; } = SpacecraftControls.Parked;
    internal bool Faulted { get; private set; }
    internal bool Suspended { get; private set; }
    internal bool Incapacitated { get; private set; }
    internal string FailureReason { get; private set; } = "";
    internal bool PointerActive => _attached && !Faulted && !Suspended && !Incapacitated && _pointer;
    internal Camera? Camera => _attached && !Faulted && !Incapacitated ? _capture?.Camera : null;
    internal ClientSpacecraftBody? Spacecraft => _spacecraft;
    internal bool Seated => _attached && !Walking && _sitBlend >= 1;

    internal void SetSuspended(bool suspended)
    {
        if (Suspended == suspended) return;
        Suspended = suspended;
        if (!suspended) return;
        Neutralize();
        if (ControlHandActive) CancelControlPress("Ship interaction is waiting for the connection.");
    }

    internal bool TryResume()
    {
        if (!Faulted && !Incapacitated) return true;
        if (_disposed || _capture == null || _spacecraft?.Ready != true
            || !Live(_spacecraft.Root) || !GameRuntime.State.IsInWorld) return false;
        try
        {
            if (UpdateIncapacitation()) { CarryIncapacitated(); return false; }
            if (!_capture.CoreCurrent || !_capture.Alive) return false;
            if (_focus && !_focusCurrent())
            {
                if (!WorldInputAvailable || !_acquireFocus() || !_focusCurrent()) return false;
            }
            if (!_capture.Current)
            {
                if (ControlHandActive) CancelControlPress("Your player presentation changed.");
                if (!_capture.TryRebindPresentation()) return false;
                _extinguisherPoseFrame = _consoleContactFrame = -1;
                ClearControlReachBinding();
            }
            _capture.RestorePose();
            if (_attached)
            {
                _capture.Maintain();
                CarryAttachedPose();
            }
            else
            {
                // Restart only preparation for this exact capture. No island
                // anchor or replacement native actor participates in recovery.
                ReleaseInput(_capture);
                _equipment.Finish(true);
                _equipment.UpdateRestore(true);
                if (_equipment.Active) return false;
                _deadline = Time.unscaledTime + 10;
            }
            Neutralize();
            Faulted = false;
            FailureReason = "";
            return true;
        }
        catch (Exception error)
        {
            Fail("The spacecraft rider is waiting for its player components.", error);
            return false;
        }
    }

    internal bool TryCorrectAttachment(ClientSpacecraftBody body, Vector3 localPoint, bool walking)
    {
        if (_disposed || Incapacitated || !_attached || !ReferenceEquals(body, _spacecraft) || !body.TelemetryCurrent
            || walking != Walking || !Finite(localPoint) || _capture is not { Current: true, Alive: true }
            || body.State.Travel != SpacecraftTravel.None) return false;
        try
        {
            var frame = body.Root.transform;
            bool outside = false;
            if (walking)
            {
                bool valid = body.State.Region == SpacecraftRegion.Space
                    ? body.CrewStep(localPoint, localPoint, false, out outside)
                        || body.CrewStep(localPoint, localPoint, true, out outside)
                    : body.CrewCabinStep(localPoint, localPoint);
                if (!valid || !CabinClear(frame.TransformPoint(localPoint))) return false;
            }
            else
            {
                var seatPoint = SpacecraftLayout.Seat(Seat) + body.SeatAdjustmentOffset(Seat) + Vector3.down * .8f;
                if ((localPoint - seatPoint).sqrMagnitude > .25f * .25f
                    || !body.CrewSeatPathClear(localPoint, localPoint, Seat)
                    || !CabinClear(frame.TransformPoint(ClientSpacecraftBody.CrewSeatFeet(localPoint)))) return false;
            }
            Neutralize();
            _capture.RestorePose();
            _capture.Maintain();
            _boardingPosition = _attachedPosition = localPoint;
            _walkVelocity = _flowVelocity = Vector3.zero;
            if (walking)
            {
                _cabinPosition = localPoint;
                _standBlend = 1;
                _outside = outside;
            }
            else _sitBlend = 1;
            CarryAttachedPose();
            return true;
        }
        catch (Exception error)
        {
            Fail("The spacecraft attachment could not be restored in place.", error);
            return false;
        }
    }

    internal bool TryAttachedPosition(ClientSpacecraftBody body, Transform player, out Vector3 position)
    {
        position = default;
        if (_disposed || !_attached || !ReferenceEquals(body, _spacecraft) || !body.Ready
            || _capture == null || !Same(player, _capture.Player)) return false;
        if (Incapacitated ? !_capture.CoreCurrent || _capture.Life is not (PlayerLifeState.Alive or PlayerLifeState.Dying)
            : Faulted || !_capture.Current || !_capture.Alive) return false;
        // Native survival callbacks can run before the rider follows this frame's ship motion.
        position = body.Root.transform.TransformPoint(_attachedPosition);
        return Finite(position);
    }

    // Console is our focus lease; native menus and exclusive states still own input.
    internal static bool WorldInputAvailable => Application.isFocused && GameRuntime.State.IsInWorld
        && !GameRuntime.State.PauseMenuOpen && !SonsSdk.GameState.IsInConsole && !KeyPressed(Key.Escape)
        && Live(LocalPlayer.Inventory) && LocalPlayer.Inventory.CurrentView == PlayerInventory.PlayerViews.World
        && !GameInput.GetState(InputState.Menu) && !GameInput.GetState(InputState.Locked)
        && !GameInput.GetState(InputState.SteamOverlay) && !GameInput.GetState(InputState.VirtualKeyboard)
        && !GameInput.GetState(InputState.InputBinding) && !GameInput.GetState(InputState.ModalDialog)
        && !GameInput.GetState(InputState.Cutscene) && !GameInput.GetState(InputState.TextInput)
        && !GameInput.GetState(InputState.GameOver) && !GameInput.GetState(InputState.FirstDeath)
        && !GameInput.GetState(InputState.SavingMaps);

    // Acceptance is asynchronous: the terminal finishes holstering, then focus
    // blocks new actions while native equipment lowers the retained hand items.
    internal bool Begin(ClientSpacecraftBody body, int seat)
    {
        if (_disposed || Active || Restoring)
        { FailureReason = "Finish your current action before boarding."; return false; }
        Faulted = _exitRequested = false;
        FailureReason = "The spacecraft seat is not ready to board.";
        try
        {
            if (seat < 0 || seat > 2 || !body.Ready || !Live(body.Root)
                || !GameRuntime.State.IsInWorld || GameRuntime.State.PauseMenuOpen || !Application.isFocused)
                return false;
            var capture = Capture.TryCreate();
            if (capture == null) { FailureReason = "Your player is not ready to board."; return false; }
            _capture = capture;
            _spacecraft = body;
            Seat = seat;
            SeatRevision = unchecked(SeatRevision + 1);
            body.SetSeatSitProgress(seat, 0, localRider: true);
            _deadline = Time.unscaledTime + 10;
            Controls = SpacecraftControls.Parked;
            FailureReason = "";
            return true;
        }
        catch (Exception error)
        {
            Fail("The spacecraft seat could not be prepared.", error);
            return false;
        }
    }

    // The host keeps the berth reserved while this same captured actor walks.
    internal bool BeginCabinWalk(ClientSpacecraftBody body, Vector3 worldStandingPoint)
    {
        if (_disposed || Faulted || Incapacitated || !Attached || !ReferenceEquals(body, _spacecraft)
            || _capture?.Current != true || !_capture.Alive || !Finite(worldStandingPoint)) return false;
        try
        {
            FailureReason = "The aisle beside your chair is blocked. Remain seated and try again.";
            if (_focus && !_focusCurrent())
            { Fail("Spacecraft input ownership was lost."); return false; }
            if (!_capture.CanWalkAnimate)
            { FailureReason = "Your player's walking animation is not ready."; return false; }
            var frame = body.Root.transform;
            var standing = frame.InverseTransformPoint(worldStandingPoint);
            if (body.State.Region == SpacecraftRegion.Space)
            {
                if (!body.CrewStep(standing, standing, false, out var outside) || outside) return false;
            }
            else if (!body.CrewCabinStep(standing, standing)) return false;
            if (!CabinClear(worldStandingPoint)) return false;
            CarryAttachedPose();
            _boardingPosition = _attachedPosition;
            if (!CabinTransferClear(_boardingPosition, standing, Seat)) return false;
            EndControlPress(body, immediate: true);
            _attachedPosition = _boardingPosition;
            _boardingRotation = Quaternion.Inverse(frame.rotation) * _capture.Player.rotation;
            _boardingEye = frame.InverseTransformPoint(_capture.Camera.transform.position);
            _boardingView = Quaternion.Inverse(frame.rotation) * _capture.Camera.transform.rotation;
            _capture.RestorePose();
            SetPointer(false);
            Neutralize();
            _cabinPosition = standing;
            _outside = false;
            var forward = body.Root.transform.InverseTransformDirection(_capture.Camera.transform.forward);
            _lookYaw = _viewYaw = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
            _lookPitch = _viewPitch = Mathf.Clamp(-Mathf.Atan2(forward.y,
                Mathf.Sqrt(forward.x * forward.x + forward.z * forward.z)) * Mathf.Rad2Deg, -65, 65);
            _walkVelocity = _flowVelocity = Vector3.zero;
            _standBlend = 0;
            _interaction = default;
            _renderInteraction = default;
            FailureReason = "";
            Walking = true;
            SeatRevision = unchecked(SeatRevision + 1);
            Attach();
            return true;
        }
        catch (Exception error) { Fail("Cabin walking could not begin.", error); return false; }
    }

    internal bool ResumeSeat(ClientSpacecraftBody body, int seat)
    {
        if (_disposed || Faulted || Incapacitated || !Walking || !ReferenceEquals(body, _spacecraft) || seat < 0 || seat > 2
            || _capture?.Current != true || !_capture.Alive) return false;
        try
        {
            FailureReason = "The path to your chair is blocked. Clear the approach and try again.";
            if (_focus && !_focusCurrent())
            { Fail("Spacecraft input ownership was lost."); return false; }
            var frame = body.Root.transform;
            CarryAttachedPose();
            _boardingPosition = _attachedPosition;
            var seated = body.Root.transform.InverseTransformPoint(body.SeatRestTargets(seat).Pelvis)
                + Vector3.down * .8f;
            if (!CabinTransferClear(_boardingPosition, seated, seat)) return false;
            EndControlPress(body, immediate: true);
            _capture.RestorePose();
            _capture.EndWalkAnimation();
            _attachedPosition = _boardingPosition;
            _boardingRotation = Quaternion.Inverse(frame.rotation) * _capture.Player.rotation;
            _boardingEye = frame.InverseTransformPoint(_capture.Camera.transform.position);
            _boardingView = Quaternion.Inverse(frame.rotation) * _capture.Camera.transform.rotation;
            FailureReason = "";
            Walking = false;
            _outside = false;
            _interaction = default;
            _renderInteraction = default;
            Seat = seat;
            SeatRevision = unchecked(SeatRevision + 1);
            _sitBlend = _lookYaw = _lookPitch = _viewYaw = _viewPitch = 0;
            _walkVelocity = _flowVelocity = Vector3.zero;
            Neutralize();
            var flight = body.State;
            Controls = Controls with { Powered = flight.Powered, Dampeners = flight.Dampeners,
                DoorOpen = flight.DoorOpen, GearDeployed = flight.GearDeployed };
            Attach();
            SetPointer(Seat == 0);
            return true;
        }
        catch (Exception error) { Fail("The spacecraft seat could not be resumed.", error); return false; }
    }

    internal void SetInteraction(SpacecraftCrewInteraction interaction)
    {
        // Cabin presses follow local fingertip contact; their host echo is only for observers.
        _interaction = !Incapacitated && Walking && _spacecraft != null && interaction.Active && _capture?.MatchesActor(interaction.Actor) == true
            && !(interaction.Press && interaction.LocalRotation.HasValue)
            ? interaction.SampleGuard(_spacecraft.State.Systems.ApuGuardAmount) : default;
    }

    internal void SetHeldExtinguisher(BoxStationFirefightingHands owner, BoxStationModel? station, SpacecraftHeldStationTool held)
    {
        if (!held.Active || !CanHoldExtinguisher(station, held)) return;
        if (ControlHandActive) CancelControlPress("Put away the held station tool before using this control.");
        if (!ReferenceEquals(owner, _extinguisherOwner) || !_extinguisher.SameHolder(held))
            ClearHeldExtinguisher(_extinguisherOwner);
        _extinguisherOwner = owner;
        _extinguisherStation = station;
        _extinguisher = held;
        _extinguisherFrame = Time.frameCount;
    }

    internal bool TryHeldExtinguisherPose(BoxStationFirefightingHands owner, SpacecraftHeldStationTool held,
        out Vector3 position, out Quaternion rotation)
    {
        position = _extinguisherPosition; rotation = _extinguisherRotation;
        return _extinguisherPoseFrame == Time.frameCount && ReferenceEquals(owner, _extinguisherOwner)
            && _extinguisher.SameHolder(held) && CanHoldExtinguisher(_extinguisherStation, held);
    }

    internal void ClearHeldExtinguisher(BoxStationFirefightingHands? owner)
    {
        if (!ReferenceEquals(owner, _extinguisherOwner)) return;
        if (_extinguisherPoseFrame >= 0) _capture?.RestoreHeldExtinguisher();
        _extinguisherOwner = null;
        _extinguisherStation = null;
        _extinguisher = default;
        _extinguisherFrame = _extinguisherPoseFrame = -1;
    }

    private bool CanHoldExtinguisher(BoxStationModel? station, SpacecraftHeldStationTool held)
        => !_disposed && !Faulted && !Incapacitated && _attached && Walking && _standBlend >= 1
            && _capture is { Current: true, Alive: true } && _capture.MatchesActor(held.Actor)
            && _spacecraft is { Ready: true, TelemetryCurrent: true } && _spacecraft.State.Travel == SpacecraftTravel.None
            && (held.Kind == SpacecraftHeldStationToolKind.Engineering
                || station == null && held.Kind == SpacecraftHeldStationToolKind.Extinguisher
                ? _spacecraft.ContainsCrewWalkArea(_capture.Player.position)
                : _spacecraft.State.Docked && station?.Visible == true && (held.IsShipSupply
                    ? _spacecraft.ContainsCrewWalkArea(_capture.Player.position)
                    : station.ContainsWalkArea(_capture.Player.position)));

    internal void Update()
    {
        if (_disposed) return;
        if (!Active) { UpdateControlPress(); _equipment.UpdateRestore(true); return; }
        try
        {
            // Retaining the seat during recovery must not retain a menu's navigation.
            if (Faulted) SyncPointer(false, false);
            if (UpdateIncapacitation()) { CarryIncapacitated(); return; }
            UpdateControlPress();
            if (Faulted)
            {
                if (_focus && (_capture?.SameActor != true || !_capture.Alive)) ReleaseInput(_capture);
                return;
            }
            if (_focus && !_focusCurrent())
            { Fail("Spacecraft input ownership was lost."); return; }
            if (_capture == null || !_capture.Current || !_capture.Alive
                || !GameRuntime.State.IsInWorld || !_spacecraft!.Ready || !Live(_spacecraft.Root))
            {
                Fail("The spacecraft seat is no longer available.");
                return;
            }
            // Remove only our last frame's writes before native animation runs.
            _capture.RestorePose();
            _extinguisherPoseFrame = -1;
            if (Suspended)
            {
                Neutralize();
                if (_attached)
                {
                    _capture.Maintain();
                    CarryAttachedPose();
                }
                SyncPointer(false, false);
                return;
            }
            if (!_attached)
            {
                if (Application.isFocused && !GameRuntime.State.PauseMenuOpen
                    && KeyPressed(Key.X))
                    _exitRequested = true;
                if (_exitRequested) return;
                if (Time.unscaledTime >= _deadline)
                {
                    Fail("Boarding could not finish. Keep the chair's approach clear and free your hands before trying again.");
                    return;
                }
                if (!_focus)
                {
                    if (!Application.isFocused || GameRuntime.State.PauseMenuOpen) return;
                    _capture.RememberCursor();
                    // The terminal grants this only after the tablet/watch pose
                    // and its equipment transition have relinquished their hands.
                    if (!_acquireFocus()) return;
                    _focus = true;
                    if (!_focusCurrent())
                    { Fail("Spacecraft input ownership was lost."); return; }
                    if (!_capture.Current || !_capture.Alive)
                    { Fail("Your player changed while boarding."); return; }
                    if (!_equipment.TryStash(out var reason))
                    { Fail(reason); return; }
                    _deadline = Time.unscaledTime + 10;
                }
                var ready = _equipment.Ready;
                if (!_equipment.Active)
                {
                    Fail("Boarding could not finish. Try again after your hands are free.");
                    return;
                }
                if (!ready || !Application.isFocused || GameRuntime.State.PauseMenuOpen) return;
                // A native equipment callback can replace the actor or camera.
                if (!_capture.Current || !_capture.Alive)
                {
                    Fail("Your player changed while boarding.");
                    return;
                }
                if (!_focusCurrent())
                { Fail("Spacecraft input ownership was lost."); return; }
                _capture.Take();
                if (!_focusCurrent())
                { Fail("Spacecraft input ownership was lost."); return; }
                var flight = _spacecraft!.State;
                Controls = new(Vector3.zero, Vector3.zero, flight.Powered, flight.Dampeners, false, flight.DoorOpen,
                    GearDeployed: flight.GearDeployed);
                var frame = _spacecraft.Root.transform;
                _boardingPosition = frame.InverseTransformPoint(_capture.Player.position);
                _attachedPosition = _boardingPosition;
                _boardingRotation = Quaternion.Inverse(frame.rotation) * _capture.Player.rotation;
                _boardingEye = frame.InverseTransformPoint(_capture.Camera.transform.position);
                _boardingView = Quaternion.Inverse(frame.rotation) * _capture.Camera.transform.rotation;
                _sitBlend = _lookYaw = _lookPitch = _viewYaw = _viewPitch = 0;
                _panelUntil = _consoleUntil = 0;
                _pointer = false;
                if (!Attach()) return;
                _attached = true;
                SetPointer(Seat == 0);
            }

            var acceptsInput = _focus && WorldInputAvailable;
            if (Walking)
            {
                UpdateCabinWalk(acceptsInput);
                return;
            }
            var previousSit = _sitBlend;
            if (acceptsInput)
            {
                _sitBlend = Mathf.MoveTowards(_sitBlend, 1, Mathf.Clamp(Time.unscaledDeltaTime, 0, .05f) / .72f);
                if (KeyPressed(Key.X))
                { _exitRequested = true; SetPointer(false); }
                if (Seated && KeyPressed(Key.Tab)) SetPointer(!_pointer);
                if (Seat == 0 && Seated)
                {
                    if (KeyPressed(Key.I)) SetCockpitControl("power");
                    if (KeyPressed(Key.F)) SetCockpitControl("dampeners");
                    ReadFlightInput();
                }
                if (!_pointer)
                {
                    var mouse = Mouse.current;
                    if (mouse != null)
                    {
                        var delta = mouse.delta.ReadValue();
                        _lookYaw = Mathf.Clamp(_lookYaw + delta.x * .09f, -115, 115);
                        _lookPitch = Mathf.Clamp(_lookPitch - delta.y * .09f, -65, 65);
                    }
                }
            }
            else Neutralize();
            if (_exitRequested) Neutralize();
            if (Time.unscaledTime >= _consoleUntil)
            {
                var reach = Mathf.MoveTowards(Controls.ConsoleReach, 0, Time.unscaledDeltaTime * 5);
                Controls = Controls with { ConsoleReach = reach,
                    ConsoleControl = reach > 0 ? Controls.ConsoleControl : SpacecraftConsoleControl.None };
            }
            SyncPointer(acceptsInput && _pointer, acceptsInput);
            if (acceptsInput)
            {
                Cursor.lockState = _pointer ? CursorLockMode.None : CursorLockMode.Locked;
                Cursor.visible = _pointer;
            }
            if (!Attach())
            {
                _sitBlend = previousSit;
                _spacecraft!.SetSeatSitProgress(Seat, _sitBlend, localRider: true);
            }
        }
        catch (Exception error) { Fail("Spacecraft controls stopped after a player state change.", error); }
    }

    // Called after native animation/camera constraints, through the shared late frame owner.
    internal void LateUpdate()
    {
        _extinguisherPoseFrame = -1;
        _consoleContactFrame = -1;
        if (_disposed) return;
        try
        {
            if (UpdateIncapacitation()) { CarryIncapacitated(); return; }
            if (_controlCapture != null) RenderControlPress();
            if (Faulted || !_attached || _capture == null || _spacecraft == null) return;
            if (_focus && !_focusCurrent())
            { Fail("Spacecraft input ownership was lost."); return; }
            if (!_capture.Current || !_capture.Alive || !_spacecraft.Ready || !Live(_spacecraft.Root))
            { Fail("The spacecraft seat is no longer available."); return; }
            if (!Attach()) return;
            if (Walking)
            {
                if (_standBlend < 1)
                    _capture.RenderPose(_spacecraft.SeatRestTargets(Seat), Seat, 1 - StandAmount);
                else if (ControlHandActive)
                    RenderControlPress();
                else if (_extinguisherFrame == Time.frameCount && _extinguisherOwner != null
                    && (_extinguisher.Kind == SpacecraftHeldStationToolKind.Extinguisher
                        || _extinguisher.Kind == SpacecraftHeldStationToolKind.Engineering && _extinguisher.WorkProgress >= 0
                        || !_renderInteraction.Active)
                    && CanHoldExtinguisher(_extinguisherStation, _extinguisher))
                {
                    _renderInteraction = default;
                    var up = _spacecraft.State.Docked && _extinguisherStation != null
                        ? _extinguisherStation.Root.transform.up : _spacecraft.Root.transform.up;
                    if (_capture.RenderHeldExtinguisher(_extinguisher, up,
                        out _extinguisherPosition, out _extinguisherRotation))
                        _extinguisherPoseFrame = Time.frameCount;
                }
                else if (_renderInteraction.Active && _capture.MatchesActor(_renderInteraction.Actor))
                    _capture.RenderRightHand(_spacecraft.Root.transform.TransformPoint(_renderInteraction.LocalTarget),
                        _spacecraft.Root.transform.up, _renderInteraction.Reach,
                        _renderInteraction.LocalRotation is { } rotation ? _spacecraft.Root.transform.rotation * rotation : null,
                        _renderInteraction.Press);
                PlaceWalkingCamera();
                return;
            }
            var pose = _spacecraft.ConsolePose(Seat, _capture.Actor);
            var localContact = _pointer && Controls.ConsoleControl != SpacecraftConsoleControl.None && Controls.ConsoleReach > 0;
            var control = localContact ? Controls.ConsoleControl : pose.Control;
            var reach = localContact ? Controls.ConsoleReach : pose.Reach;
            var targets = CrewTargets(_spacecraft, Seat, control, reach);
            _capture.RenderPose(targets, Seat, SitAmount);
            if (localContact && reach >= 1 && Seated)
            {
                _consoleContactControl = control;
                _consoleContactFrame = Time.frameCount;
            }
            // Seating moves the skeleton after native camera animation.
            PlaceCamera(targets);
        }
        catch (Exception error) { Fail("The cockpit camera could not be maintained.", error); }
    }

    internal void Rebase(Vector3 delta)
    {
        if (_disposed || !_attached || _capture == null || _spacecraft == null) return;
        if (!Finite(delta)) throw new ArgumentException("The spacecraft rebase offset is invalid.", nameof(delta));
        try
        {
            if (UpdateIncapacitation()) { CarryIncapacitated(); return; }
            if (Faulted) return;
            if (_focus && !_focusCurrent())
            { Fail("Spacecraft input ownership was lost."); return; }
            var capture = _capture;
            if (!capture.Current || !capture.Alive || !_spacecraft.Ready || !Live(_spacecraft.Root))
            { Fail("The spacecraft seat is no longer available."); return; }
            var camera = capture.Camera.transform;
            // Body and player can share a transform, and the camera can follow its hierarchy.
            var cameraPosition = camera.position;
            var bodyPosition = capture.Body.position;
            var playerPosition = capture.Player.position;
            capture.Body.position = bodyPosition + delta;
            capture.Player.position = playerPosition + delta;
            camera.position = cameraPosition + delta;
        }
        catch (Exception error) { Fail("The spacecraft rider could not be rebased.", error); }
    }

    private bool Attach()
    {
        var capture = _capture!;
        var frame = _spacecraft!.Root.transform;
        capture.Maintain();
        if (Walking)
        {
            if (_standBlend < 1) _spacecraft.SetSeatSitProgress(Seat, 1 - _standBlend, localRider: true);
            var localPosition = Vector3.Lerp(_boardingPosition, _cabinPosition, StandAmount);
            var worldPosition = frame.TransformPoint(localPosition);
            // Recheck even with no input: a moving hatch or external object may
            // have entered the previously clear pose since the movement query.
            if (_standBlend < 1 ? !CabinTransferClear(_attachedPosition, localPosition, Seat)
                : !_spacecraft.CrewCapsuleClear(localPosition) || !CabinClear(worldPosition))
            { _walkVelocity = _flowVelocity = Vector3.zero; CarryAttachedPose(); return false; }
            var worldRotation = frame.rotation * Quaternion.Slerp(_boardingRotation, Quaternion.Euler(0, _viewYaw, 0), StandAmount);
            capture.Body.position = worldPosition;
            capture.Body.rotation = worldRotation;
            capture.Player.SetPositionAndRotation(worldPosition, worldRotation);
            _attachedPosition = localPosition;
            _attachedRotation = Quaternion.Inverse(frame.rotation) * worldRotation;
            PlaceWalkingCamera();
            capture.RenderWalkAnimation(_spacecraft.CrewDeckGravity
                ? Quaternion.Inverse(worldRotation) * (frame.rotation * _walkVelocity) : Vector3.zero);
            return true;
        }
        _spacecraft.SetSeatSitProgress(Seat, _sitBlend, localRider: true);
        var targets = _spacecraft.SeatRestTargets(Seat);
        _viewYaw = Mathf.MoveTowardsAngle(_viewYaw, _lookYaw, 180 * Time.unscaledDeltaTime);
        _viewPitch = Mathf.MoveTowards(_viewPitch, _lookPitch, 180 * Time.unscaledDeltaTime);
        // The ordinary player origin is below the pelvis. Both meshes are seated
        // separately after animation; never parent the native network entity to the ship.
        var position = Vector3.Lerp(frame.TransformPoint(_boardingPosition),
            targets.Pelvis + targets.FrameRotation * Vector3.down * .8f, SitAmount);
        var local = frame.InverseTransformPoint(position);
        if (!CabinTransferClear(_attachedPosition, local, Seat))
        { CarryAttachedPose(); return false; }
        var rotation = Quaternion.Slerp(frame.rotation * _boardingRotation, targets.FrameRotation, SitAmount);
        capture.Body.position = position;
        capture.Body.rotation = rotation;
        capture.Player.SetPositionAndRotation(position, rotation);
        _attachedPosition = local;
        _attachedRotation = Quaternion.Inverse(frame.rotation) * rotation;
        PlaceCamera(targets);
        return true;
    }

    private void CarryAttachedPose()
    {
        if (!_attached) return;
        // Reject the requested step without leaving a kinematic player behind in
        // world space as the ship moves or changes its orbital reference frame.
        var capture = _capture!;
        var frame = _spacecraft!.Root.transform;
        var position = frame.TransformPoint(_attachedPosition);
        var rotation = frame.rotation * _attachedRotation;
        var cameraPosition = capture.Camera.transform.position;
        var cameraRotation = capture.Camera.transform.rotation;
        var eyeOffset = cameraPosition - capture.Player.position;
        var turn = rotation * Quaternion.Inverse(capture.Player.rotation);
        capture.Body.position = position;
        capture.Body.rotation = rotation;
        capture.Player.SetPositionAndRotation(position, rotation);
        capture.Camera.transform.SetPositionAndRotation(position + turn * eyeOffset, turn * cameraRotation);
    }

    private bool UpdateIncapacitation()
    {
        if (!Active || _capture == null) return false;
        var life = _capture.Life;
        if (!Incapacitated)
        {
            if (life != PlayerLifeState.Dying) return false;
            Incapacitated = true;
            FailureReason = "Your player is waiting to recover.";
        }
        if (ControlHandActive) CancelControlPress(FailureReason);
        Neutralize();
        _exitRequested = false;
        _walkVelocity = _flowVelocity = Vector3.zero;
        _interaction = _renderInteraction = default;
        _extinguisherOwner = null; _extinguisherStation = null; _extinguisher = default;
        _extinguisherFrame = _extinguisherPoseFrame = -1;
        if (_focus || _pointer) ReleaseInput(_capture);
        if (!_capture.CoreCurrent || life is not (PlayerLifeState.Alive or PlayerLifeState.Dying)) return true;
        _capture.SuspendPresentation();
        // Native revival reports Alive before its get-up worker restores controls.
        if (life != PlayerLifeState.Alive || !_capture.NativeControlsReady || !WorldInputAvailable) return true;
        if (!_capture.Current && !_capture.TryRebindPresentation()) return true;
        if (!_attached)
        {
            _capture.ResumePresentation();
            Incapacitated = false;
            Faulted = true;
            FailureReason = "Boarding preparation will resume after recovery.";
            return false;
        }
        _capture.RememberCursor();
        if (!_acquireFocus()) return true;
        _focus = true;
        if (!_focusCurrent() || !_capture.NativeControlsReady || !_capture.Current)
        { ReleaseInput(_capture); return true; }
        _capture.ResumePresentation();
        ClearControlReachBinding();
        Incapacitated = Faulted = false;
        FailureReason = "";
        return false;
    }

    private void CarryIncapacitated()
    {
        if (!Incapacitated || !_attached || _capture == null || _spacecraft?.Ready != true
            || !Live(_spacecraft.Root) || !GameRuntime.State.IsInWorld || !_capture.CoreCurrent
            || _capture.Life is not (PlayerLifeState.Alive or PlayerLifeState.Dying)) return;
        _capture.MaintainCarrier();
        var frame = _spacecraft.Root.transform;
        var position = frame.TransformPoint(_attachedPosition);
        var rotation = frame.rotation * _attachedRotation;
        _capture.Body.position = position;
        _capture.Body.rotation = rotation;
        _capture.Player.SetPositionAndRotation(position, rotation);
    }

    private void UpdateCabinWalk(bool acceptsInput)
    {
        Neutralize();
        SyncPointer(false, acceptsInput);
        var body = _spacecraft!;
        var floating = !body.CrewDeckGravity;
        if (!floating) _walkVelocity.y = _flowVelocity.y = 0;
        var step = Mathf.Clamp(Time.deltaTime, 0, .05f);
        var dt = acceptsInput ? step : 0;
        // Multiplayer menus release controls, but do not pause drift or decompression.
        var motionDt = body.TelemetryCurrent && (acceptsInput || BoltNetwork.isRunning) ? step : 0;
        var standing = _standBlend >= 1;
        var previousStand = _standBlend;
        if (!standing)
        {
            var next = Mathf.MoveTowards(_standBlend, 1, motionDt / .55f);
            var position = Vector3.Lerp(_boardingPosition, _cabinPosition, Mathf.SmoothStep(0, 1, next));
            body.SetSeatSitProgress(Seat, 1 - next, localRider: true);
            if (CabinTransferClear(_attachedPosition, position, Seat)) _standBlend = next;
            else body.SetSeatSitProgress(Seat, 1 - _standBlend, localRider: true);
        }
        else _renderInteraction = SpacecraftCrewInteraction.Advance(_renderInteraction, _interaction, dt);
        var desired = Vector3.zero;
        var braking = false;
        if (acceptsInput)
        {
            if (KeyPressed(Key.X)) _exitRequested = true;
            var mouse = Mouse.current;
            if (mouse != null)
            {
                var delta = mouse.delta.ReadValue();
                _lookYaw = Mathf.Repeat(_lookYaw + delta.x * .09f + 180, 360) - 180;
                _lookPitch = Mathf.Clamp(_lookPitch - delta.y * .09f, -65, 65);
            }
            if (!_exitRequested && standing)
            {
                var vertical = floating ? (KeyHeld(Key.Space) ? 1 : 0)
                    - (KeyHeld(Key.LeftCtrl) || KeyHeld(Key.RightCtrl) ? 1 : 0) : 0;
                desired = Vector3.ClampMagnitude(new Vector3(Axis(Key.D, Key.A), vertical, Axis(Key.W, Key.S)), 1);
                braking = floating && (KeyHeld(Key.LeftShift) || KeyHeld(Key.RightShift));
            }
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
        _viewYaw = Mathf.MoveTowardsAngle(_viewYaw, _lookYaw, 540 * dt);
        _viewPitch = Mathf.MoveTowards(_viewPitch, _lookPitch, 270 * dt);
        desired = Quaternion.Euler(floating ? _viewPitch : 0, floating ? _viewYaw : _lookYaw, 0) * desired;
        // Reach the host-selected standing point for a frame before drift begins.
        if (standing && motionDt > 0)
        {
            var airflow = body.CrewAirflow(_cabinPosition);
            if (floating)
            {
                _flowVelocity = Vector3.zero;
                _walkVelocity += (desired * 4 + airflow) * motionDt;
                if (braking) _walkVelocity = Vector3.MoveTowards(_walkVelocity, Vector3.zero, 6 * motionDt);
                _walkVelocity = Vector3.ClampMagnitude(_walkVelocity, ClientSpacecraftBody.CrewSpeed);
            }
            else
            {
                _flowVelocity = Vector3.ClampMagnitude(_flowVelocity * Mathf.Exp(-5 * motionDt)
                    + new Vector3(airflow.x, 0, airflow.z) * motionDt, ClientSpacecraftBody.CrewSpeed);
                _walkVelocity = Vector3.ClampMagnitude(desired * 2.2f + _flowVelocity, ClientSpacecraftBody.CrewSpeed);
            }
            var before = _cabinPosition;
            MoveCabin(_walkVelocity * motionDt, floating);
            if (!floating) _walkVelocity = (_cabinPosition - before) / motionDt;
        }
        else if (!floating) _walkVelocity = Vector3.zero;
        if (!Attach() && !standing)
        {
            _standBlend = previousStand;
            body.SetSeatSitProgress(Seat, 1 - _standBlend, localRider: true);
        }
        else if (!standing && _standBlend >= 1) body.ResetSeatAdjustment(Seat, localRider: true);
    }

    private void MoveCabin(Vector3 displacement, bool floating)
    {
        if (!Finite(displacement) || !Finite(_cabinPosition))
        { _walkVelocity = _flowVelocity = Vector3.zero; return; }
        var frame = _spacecraft!.Root.transform;
        var position = frame.TransformPoint(_cabinPosition);
        if (!_spacecraft.CrewCapsuleClear(_cabinPosition) || !CabinClear(position))
        { _walkVelocity = _flowVelocity = Vector3.zero; return; }
        if (floating && displacement.sqrMagnitude < 1e-10f) return;
        if (!floating) displacement.y = 0;
        var remaining = frame.TransformDirection(displacement);
        for (var iteration = 0; iteration < 2 && (iteration == 0 || remaining.sqrMagnitude > 1e-10f); iteration++)
        {
            var distance = remaining.magnitude;
            var direction = distance > .00001f ? remaining / distance : Vector3.zero;
            var count = distance > .00001f ? Physics.CapsuleCastNonAlloc(position + frame.up * ClientSpacecraftBody.CrewCapsuleBottom,
                position + frame.up * ClientSpacecraftBody.CrewCapsuleTop, ClientSpacecraftBody.CrewCapsuleRadius,
                direction, _walkHits, distance + .025f, ~0, QueryTriggerInteraction.Ignore) : 0;
            if (count < 0 || count >= _walkHits.Length)
            { _walkVelocity = _flowVelocity = Vector3.zero; return; }
            var allowed = distance;
            var normal = Vector3.zero;
            for (var i = 0; i < count; i++)
            {
                var hit = _walkHits[i];
                if (!Live(hit.collider) || !float.IsFinite(hit.distance) || hit.distance < 0
                    || !Finite(hit.normal) || hit.normal.sqrMagnitude < .5f)
                { _walkVelocity = _flowVelocity = Vector3.zero; return; }
                if (OwnCollider(hit.collider) || hit.distance > allowed + .025f) continue;
                allowed = Mathf.Max(0, hit.distance - .025f);
                normal = hit.normal;
            }
            var candidate = position + direction * allowed;
            if (!floating)
            {
                if (!_spacecraft.TryCrewWalkSurface(candidate, out var surface))
                { _walkVelocity = _flowVelocity = Vector3.zero; return; }
                var supported = surface + frame.up * .1f;
                if (Mathf.Abs(Vector3.Dot(supported - candidate, frame.up)) > .35f
                    || !_spacecraft.ContainsCrewWalkArea(supported))
                { _walkVelocity = _flowVelocity = Vector3.zero; return; }
                candidate = supported;
            }
            var local = frame.InverseTransformPoint(candidate);
            var nextOutside = _outside;
            if ((_spacecraft.State.Region == SpacecraftRegion.Space
                    ? !_spacecraft.CrewStep(_cabinPosition, local, _outside, out nextOutside)
                    : !_spacecraft.CrewCabinStep(_cabinPosition, local))
                || !CabinClear(candidate))
            { _walkVelocity = _flowVelocity = Vector3.zero; return; }
            position = candidate;
            _cabinPosition = local;
            _outside = nextOutside;
            if (normal.sqrMagnitude < .5f) return;
            remaining = Vector3.ProjectOnPlane(direction * (distance - allowed), normal);
            var localNormal = frame.InverseTransformDirection(normal);
            _walkVelocity = Vector3.ProjectOnPlane(_walkVelocity, localNormal);
            _flowVelocity = Vector3.ProjectOnPlane(_flowVelocity, localNormal);
            if (!floating)
            {
                remaining = Vector3.ProjectOnPlane(remaining, frame.up);
                _flowVelocity.y = 0;
            }
        }
    }

    private bool CabinTransferClear(Vector3 from, Vector3 to, int seat)
    {
        var body = _spacecraft!;
        if (!body.CrewSeatPathClear(from, to, seat)) return false;
        var frame = body.Root.transform;
        var start = frame.TransformPoint(ClientSpacecraftBody.CrewSeatFeet(from));
        var finish = frame.TransformPoint(ClientSpacecraftBody.CrewSeatFeet(to));
        var travel = finish - start;
        var distance = travel.magnitude;
        if (distance > .00001f)
        {
            var count = Physics.CapsuleCastNonAlloc(start + frame.up * ClientSpacecraftBody.CrewCapsuleBottom,
                start + frame.up * ClientSpacecraftBody.CrewCapsuleTop, ClientSpacecraftBody.CrewCapsuleRadius,
                travel / distance, _walkHits, distance, ~0, QueryTriggerInteraction.Ignore);
            if (count < 0 || count >= _walkHits.Length) return false;
            for (var i = 0; i < count; i++)
            {
                var hit = _walkHits[i];
                if (!Live(hit.collider) || !float.IsFinite(hit.distance) || hit.distance < 0) return false;
                // CrewSeatPathClear already checks the ship at its authored pose;
                // the physics query may still contain last step's collider transforms.
                if (!OwnCollider(hit.collider) && !hit.collider.transform.IsChildOf(body.Root.transform)) return false;
            }
        }
        return CabinClear(finish);
    }

    private bool CabinClear(Vector3 feet)
    {
        var up = _spacecraft!.Root.transform.up;
        try
        {
            var count = Physics.OverlapCapsuleNonAlloc(feet + up * ClientSpacecraftBody.CrewCapsuleBottom,
                feet + up * ClientSpacecraftBody.CrewCapsuleTop, ClientSpacecraftBody.CrewCapsuleRadius,
                _walkOverlaps, ~0, QueryTriggerInteraction.Ignore);
            if (count < 0 || count >= _walkOverlaps.Length) return false;
            for (var i = 0; i < count; i++)
            {
                var collider = _walkOverlaps[i];
                if (!Live(collider) || !OwnCollider(collider)
                    && !collider.transform.IsChildOf(_spacecraft.Root.transform)) return false;
            }
            return true;
        }
        finally
        {
            for (var i = 0; i < _walkOverlaps.Length; i++) _walkOverlaps[i] = null!;
        }
    }

    private bool OwnCollider(Collider collider) => Same(collider.attachedRigidbody, _capture!.Body)
        || Same(collider.transform, _capture.Player) || collider.transform.IsChildOf(_capture.Player);

    private float StandAmount => Mathf.SmoothStep(0, 1, _standBlend);

    private void PlaceWalkingCamera()
    {
        var frame = _spacecraft!.Root.transform;
        var eye = _capture!.EyePosition;
        var rotation = frame.rotation * Quaternion.Euler(_viewPitch, _viewYaw, 0);
        _capture.Camera.transform.SetPositionAndRotation(Vector3.Lerp(frame.TransformPoint(_boardingEye), eye, StandAmount),
            Quaternion.Slerp(frame.rotation * _boardingView, rotation, StandAmount));
    }

    private void PlaceCamera(AircraftCrewTargets targets)
    {
        var frame = _spacecraft!.Root.transform;
        var eye = _capture!.EyePosition;
        _capture!.Camera.transform.SetPositionAndRotation(Vector3.Lerp(frame.TransformPoint(_boardingEye), eye, SitAmount),
            Quaternion.Slerp(frame.rotation * _boardingView,
                targets.FrameRotation * Quaternion.Euler(_viewPitch, _viewYaw, 0), SitAmount));
    }

    private float SitAmount => Mathf.SmoothStep(0, 1, _sitBlend);

    internal static AircraftCrewTargets CrewTargets(ClientSpacecraftBody body, int seat,
        SpacecraftConsoleControl control, float reach)
    {
        var targets = body.SeatRestTargets(seat);
        var right = targets.Pelvis + targets.FrameRotation * new Vector3(.25f, .10f, .28f);
        targets = targets with
        {
            LeftHand = targets.Pelvis + targets.FrameRotation * new Vector3(-.25f, .10f, .28f),
            RightHand = right
        };
        if (seat > 1) return targets;
        targets = targets with
        {
            LeftHand = targets.Pelvis + targets.FrameRotation * new Vector3(seat == 0 ? -.18f : -.12f, .12f, .16f)
        };
        if (seat == 0)
        {
            var grip = body.FlightControlGrip;
            targets = targets with { RightHand = grip.position, PilotReaching = true,
                RightReachRotation = grip.rotation, RightHandGrip = 1 };
        }
        if (control == SpacecraftConsoleControl.None || reach <= 0 || control == SpacecraftConsoleControl.Flight)
            return targets;
        var contact = body.SeatConsoleContact(seat, control);
        if (contact == null || !contact) return targets;
        var amount = Mathf.SmoothStep(0, 1, Mathf.Clamp01(reach));
        return targets with
        {
            // Lift over the tray during approach and retraction; the final
            // fingertip contact remains on the moving key face.
            LeftHand = Vector3.Lerp(targets.LeftHand, contact.position, amount)
                - contact.forward * (.12f * Mathf.Sin(Mathf.PI * amount)),
            LeftReachRotation = contact.rotation * Quaternion.LookRotation(Vector3.up, Vector3.forward)
                * Quaternion.Euler(-55, 0, 0),
            LeftHandPress = Mathf.Clamp01(reach)
        };
    }

    private void SetPointer(bool active)
    {
        active &= Seat <= 1;
        if (_pointer == active) return;
        _pointer = active;
        Neutralize();
    }

    internal bool ConsumeExitRequest()
    {
        var requested = _exitRequested;
        _exitRequested = false;
        return requested;
    }

    internal void SetCockpitControl(string command)
    {
        if (_disposed || Faulted || Incapacitated || !_attached || !_focus || !WorldInputAvailable
            || _capture?.Current != true || !_capture.Alive) return;
        if (command == "exit") { _exitRequested = true; SetPointer(false); Neutralize(); return; }
        if (command == "pointer") { if (Seated) SetPointer(!_pointer); return; }
        if (Seat > 1 || !Seated) return;
        if (command is "power" or "dampeners" or "door" or "gear") _command?.Invoke(command);
    }

    internal void SetConsoleInteraction(SpacecraftConsoleControl control, float reach)
    {
        if (control == SpacecraftConsoleControl.None || !float.IsFinite(reach) || reach <= 0)
        {
            Controls = Controls with { ConsoleControl = SpacecraftConsoleControl.None, ConsoleReach = 0 };
            _consoleUntil = 0;
            return;
        }
        if (!ConsoleInput) return;
        Controls = Controls with { ConsoleControl = control, ConsoleReach = Mathf.Clamp01(reach) };
        _consoleUntil = Time.unscaledTime + .12f;
    }

    internal void SetPanelFlight(Vector3 translation, Vector3 rotation, bool boost, bool rightButton = false)
    {
        if (!Finite(translation) || !Finite(rotation)) { translation = rotation = Vector3.zero; boost = false; }
        if (translation.sqrMagnitude == 0 && rotation.sqrMagnitude == 0 && !boost)
        {
            var wasActive = _panelUntil > 0;
            _panelUntil = 0; _panelTranslation = _panelRotation = Vector3.zero; _panelBoost = false;
            if (wasActive) ReadFlightInput();
            return;
        }
        var mouse = Mouse.current;
        if (!ConsoleInput || Seat != 0 || mouse == null || !(rightButton ? mouse.rightButton.isPressed : mouse.leftButton.isPressed)) return;
        _panelTranslation = Vector3.ClampMagnitude(translation, 1); _panelRotation = Vector3.ClampMagnitude(rotation, 1);
        _panelBoost = boost; _panelRightButton = rightButton; _panelUntil = Time.unscaledTime + .12f;
        ReadFlightInput();
    }

    private void ReadFlightInput()
    {
        if (_disposed || Faulted || Incapacitated || !Seated || Seat != 0 || _exitRequested || !_focus || !WorldInputAvailable)
        {
            Controls = Controls with { Translation = Vector3.zero, Rotation = Vector3.zero, Boost = false };
            return;
        }
        var translation = new Vector3(Axis(Key.D, Key.A), Axis(Key.Space, Key.C), Axis(Key.W, Key.S));
        // Local +X pitches down, +Y yaws right and +Z rolls left.
        var rotation = new Vector3(Axis(Key.DownArrow, Key.UpArrow), Axis(Key.E, Key.Q), Axis(Key.LeftArrow, Key.RightArrow));
        var boost = KeyHeld(Key.LeftShift) || KeyHeld(Key.RightShift);
        var mouse = Mouse.current;
        var held = mouse != null && (_panelRightButton ? mouse.rightButton.isPressed : mouse.leftButton.isPressed);
        if (_pointer && held && Time.unscaledTime < _panelUntil)
        {
            // A planar stick or boost button must not discard keyboard ascent or rotation.
            translation += _panelTranslation;
            rotation += _panelRotation;
            boost |= _panelBoost;
        }
        Controls = Controls with
        {
            Translation = Vector3.ClampMagnitude(translation, 1),
            Rotation = Vector3.ClampMagnitude(rotation, 1),
            Boost = boost
        };
    }

    private bool ConsoleInput => !_disposed && !Faulted && !Suspended && !Incapacitated && Seated && _focus && _pointer && Seat <= 1
        && !_exitRequested && WorldInputAvailable && _capture?.Current == true && _capture.Alive;

    private void Neutralize()
    {
        _consoleContactFrame = -1;
        _panelUntil = _consoleUntil = 0; _panelTranslation = _panelRotation = Vector3.zero; _panelBoost = false;
        Controls = Controls with { Translation = Vector3.zero, Rotation = Vector3.zero, Boost = false,
            ConsoleControl = SpacecraftConsoleControl.None, ConsoleReach = 0 };
    }

    internal void End(Vector3? safeExit, bool restoreEquipment)
    {
        ReleaseControlPress(restoreEquipment);
        if (_spacecraft == null && _capture == null)
        {
            if (!restoreEquipment) _equipment.Abandon();
            return;
        }
        var capture = _capture;
        var attached = Attached;
        var spacecraft = _spacecraft;
        var seat = Seat;
        _spacecraft = null; _capture = null; _attached = _pointer = false; Seat = -1;
        Suspended = Incapacitated = false;
        SeatRevision = unchecked(SeatRevision + 1);
        _consoleContactFrame = -1;
        Walking = false;
        _outside = false;
        _standBlend = 1;
        _interaction = default;
        _renderInteraction = default;
        _extinguisherOwner = null;
        _extinguisherStation = null;
        _extinguisher = default;
        _extinguisherFrame = _extinguisherPoseFrame = -1;
        _cabinPosition = _attachedPosition = _walkVelocity = _flowVelocity = Vector3.zero;
        _sitBlend = _panelUntil = _consoleUntil = 0;
        _panelTranslation = _panelRotation = Vector3.zero; _panelBoost = false;
        _exitRequested = false;
        Controls = SpacecraftControls.Parked;
        var canRestoreEquipment = false;
        try
        {
            canRestoreEquipment = restoreEquipment && capture?.SameActor == true && capture.Alive;
            capture?.Release(safeExit, !attached);
        }
        catch (Exception error) { Log.LogWarning("Spacecraft player restoration: " + error.Message); }
        finally
        {
            try { spacecraft?.ResetSeatAdjustment(seat, localRider: true); }
            catch (Exception error) { Log.LogWarning("Spacecraft chair restoration: " + error.Message); }
            ReleaseInput(capture);
            _equipment.Finish(canRestoreEquipment);
        }
    }

    private void ReleaseInput(Capture? capture)
    {
        _pointer = false;
        try { DestroyPointer(); }
        catch (Exception error) { Log.LogWarning("Spacecraft interface restoration: " + error.Message); }
        if (!_focus) return;
        _focus = false;
        try { _releaseFocus(); }
        catch (Exception error) { Log.LogWarning("Spacecraft input restoration: " + error.Message); }
        try { capture?.ReleaseCursor(); }
        catch (Exception error) { Log.LogWarning("Spacecraft cursor restoration: " + error.Message); }
    }

    private void Fail(string reason, Exception? error = null)
    {
        if (!Faulted)
            Log.LogWarning(error == null ? reason : reason + " " + error.GetType().Name + ": " + error.Message);
        Faulted = true; FailureReason = reason;
        if (ControlHandActive) CancelControlPress(reason);
        Neutralize();
        // The service retains the seat while a downed actor recovers, and retires
        // it separately when native death or actor replacement is established.
        if (_focus && (_capture?.SameActor != true || !_capture.Alive)) ReleaseInput(_capture);
    }

    public void Dispose()
    {
        if (_disposed) return;
        End(null, false);
        _equipment.Abandon();
        _disposed = true;
    }

    // Device state remains readable while our focus lease disables native actions,
    // including Jump. Do not enable or rewrite the game's action maps or bindings.
    private static bool KeyHeld(Key key) => Keyboard.current?[key].isPressed == true;
    private static bool KeyPressed(Key key) => Keyboard.current?[key].wasPressedThisFrame == true;
    private static float Axis(Key positive, Key negative)
        => (KeyHeld(positive) ? 1 : 0) - (KeyHeld(negative) ? 1 : 0);

    private void SyncPointer(bool active, bool acceptsInput)
    {
        if (!active)
        {
            ReleasePointer(restoreNavigation: !acceptsInput);
            if (acceptsInput) SuppressNavigation(EventSystem.current);
            return;
        }
        if (!Live(_pointerRoot))
        {
            _pointerRoot = new GameObject("Modstoke Spacecraft Pointer");
            _pointerRoot.SetActive(false);
            var cursor = _pointerRoot.AddComponent<InputCursorState>();
            cursor._enabled = true; cursor._hardwareCursor = true; cursor._priority = 100;
        }
        if (!_pointerRoot!.activeSelf) _pointerRoot.SetActive(true);
        var current = EventSystem.current;
        if (!Usable(current))
        {
            if (!Live(_uiRoot))
            {
                _uiRoot = new GameObject("Modstoke Spacecraft Input");
                _uiRoot.SetActive(false);
                _uiSystem = _uiRoot.AddComponent<EventSystem>();
                _uiSystem.sendNavigationEvents = false;
                var module = _uiRoot.AddComponent<InputSystemUIInputModule>();
                module.AssignDefaultActions();
                _uiActions = module.actionsAsset;
            }
            if (!_uiRoot!.activeSelf)
            {
                _previousUi = current;
                _uiRoot.SetActive(true);
            }
            if (Live(_uiSystem)) EventSystem.current = _uiSystem;
            current = _uiSystem;
        }
        else if (!Same(current, _uiSystem) && Live(_uiRoot) && _uiRoot.activeSelf)
        {
            // An actual menu's dispatcher has taken over. Never steal it back.
            ReleasePointer();
            current = EventSystem.current;
        }
        SuppressNavigation(current);
    }

    private void SuppressNavigation(EventSystem? current)
    {
        // Space must not submit a previously selected crew button during free-look.
        if (!Same(_navigationSystem, current))
        {
            RestoreNavigation();
            if (Live(current))
            {
                _navigationSystem = current;
                _navigationEnabled = current.sendNavigationEvents;
                current.sendNavigationEvents = false;
            }
        }
    }

    private static bool Usable(EventSystem? system)
    {
        if (!Live(system) || !system.isActiveAndEnabled) return false;
        var module = system.currentInputModule;
        if (!Live(module) || !module.isActiveAndEnabled) return false;
        var input = module.TryCast<InputSystemUIInputModule>();
        return input == null || input.point?.action?.enabled == true && input.leftClick?.action?.enabled == true;
    }

    private void RestoreNavigation()
    {
        if (Live(_navigationSystem) && !_navigationSystem.sendNavigationEvents)
            _navigationSystem.sendNavigationEvents = _navigationEnabled;
        _navigationSystem = null;
    }

    private void ReleasePointer(bool restoreNavigation = true)
    {
        if (restoreNavigation) RestoreNavigation();
        var previous = _previousUi; _previousUi = null;
        var owned = Live(_uiSystem) && Same(EventSystem.current, _uiSystem);
        if (Live(_uiRoot) && _uiRoot.activeSelf) _uiRoot.SetActive(false);
        if (Live(_pointerRoot) && _pointerRoot.activeSelf) _pointerRoot.SetActive(false);
        // Disabling our dispatcher can select a newly opened native menu.
        var current = EventSystem.current;
        if (owned && (!Live(current) || Same(current, _uiSystem))
            && Live(previous) && previous.isActiveAndEnabled && !Same(previous, _uiSystem))
            EventSystem.current = previous;
    }

    private void DestroyPointer()
    {
        try { ReleasePointer(); }
        finally
        {
            if (Live(_uiRoot)) UnityEngine.Object.Destroy(_uiRoot);
            if (Live(_pointerRoot)) UnityEngine.Object.Destroy(_pointerRoot);
            if (Live(_uiActions)) UnityEngine.Object.Destroy(_uiActions);
            _uiRoot = _pointerRoot = null; _uiSystem = null; _uiActions = null; _previousUi = null;
        }
    }

    private sealed class Capture
    {
        internal readonly Transform Player;
        internal readonly Rigidbody Body;
        internal Camera Camera;
        private readonly FirstPersonCharacter _movement;
        private readonly PlayerInventory _inventory;
        private readonly PlayerStats _stats;
        private SimpleMouseRotator _cameraRotator, _mainRotator;
        private camFollowHead _follow;
        private readonly SaveGameManager _saveManager;
        private readonly BoltEntity? _entity;
        private readonly Bolt.Entity? _attachment;
        private readonly bool _networked;
        private readonly int _load;
        private readonly List<(Collider Collider, bool Enabled)> _colliders = new();
        private readonly List<(Behaviour Component, bool Enabled)> _viewStates = new();
        private readonly List<(Animator Animator, bool RootMotion)> _animatorStates = new();
        private readonly List<(Camera Camera, Transform? Parent, Vector3 Position, Quaternion Rotation,
            float Near, Vector3 EyeOffset)> _cameraStates = new();
        private Animator? _animator;
        private PlayerAnimatorControl? _animControl;
        private IkTargetsController? _ik;
        private BipedIK? _biped;
        private ClientAircraftCrewPose? _crew;
        private CabinAnimation? _walkAnimation;
        private bool _taken, _movementEnabled, _locked, _cameraRotatorEnabled, _mainRotatorEnabled, _followEnabled;
        private bool _passivePresentation, _restorePassiveFollow, _restorePassiveRootMotion, _restorePassiveNear;
        private bool _gravity, _kinematic, _collisions, _cursorVisible, _rootMotion;
        private CursorLockMode _cursorLock;
        private Vector3 _velocity, _angularVelocity, _cameraPosition, _startPosition, _eyeOffset;
        private Quaternion _cameraRotation, _startRotation;
        private Transform? _cameraParent;
        private float _near;
        private float _animationInputSpeed;
        internal Vector3 EyePosition => _biped!.references.head.position + _biped.references.head.rotation * _eyeOffset;
        internal bool CanWalkAnimate => _walkAnimation != null && float.IsFinite(_animationInputSpeed) && _animationInputSpeed > 0;

        private Capture(Transform player, Rigidbody body, Camera camera, FirstPersonCharacter movement,
            PlayerInventory inventory, PlayerStats stats, SimpleMouseRotator cameraRotator,
            SimpleMouseRotator mainRotator, camFollowHead follow, SaveGameManager saveManager)
        {
            Player = player; Body = body; Camera = camera; _movement = movement;
            _inventory = inventory; _stats = stats; _cameraRotator = cameraRotator;
            _mainRotator = mainRotator; _follow = follow;
            _saveManager = saveManager;
            _load = SaveLifecycle.State.BeforeLoadCount;
            _networked = BoltNetwork.isRunning;
            _entity = _networked ? LocalPlayer.Entity : null;
            _attachment = _entity?._entity;
        }

        internal static Capture? TryCreate()
        {
            var player = LocalPlayer.Transform; var body = LocalPlayer.Rigidbody;
            var camera = LocalPlayer.MainCam; var movement = LocalPlayer.FpCharacter;
            var inventory = LocalPlayer.Inventory; var stats = LocalPlayer.Stats;
            var cameraRotator = LocalPlayer.CamRotator; var mainRotator = LocalPlayer.MainRotator;
            var follow = LocalPlayer.CamFollowHead;
            if (GameRuntime.IsTitleScene || !SaveGameManager.TryGetInstance(out var saveManager)
                || !Live(saveManager) || saveManager._loadingInProgress
                || !Live(player) || !Live(body) || !Live(camera) || !camera.isActiveAndEnabled
                || !Live(movement) || !movement.isActiveAndEnabled || movement.Locked
                || !Live(inventory) || inventory.CurrentView != PlayerInventory.PlayerViews.World
                || !Live(stats) || !Live(cameraRotator) || !Live(mainRotator) || !Live(follow))
                return null;
            var capture = new Capture(player, body, camera, movement, inventory, stats,
                cameraRotator, mainRotator, follow, saveManager);
            return capture.Current && capture.Alive ? capture : null;
        }

        internal PlayerLifeState? Life => CoreCurrent ? SpacecraftLife.Read(_stats) : null;
        internal bool Alive => !_passivePresentation && Life == PlayerLifeState.Alive;
        internal bool NativeControlsReady => Life == PlayerLifeState.Alive
            && !_stats._healing && !_stats._isHealing && !_movement._isInMidAction
            && _movement.enabled && !_movement.Locked && _inventory.enabled
            && Live(LocalPlayer.CamRotator) && LocalPlayer.CamRotator.enabled
            && Live(LocalPlayer.MainRotator) && LocalPlayer.MainRotator.enabled;

        internal bool Current
        {
            get
            {
                try
                {
                    return CoreCurrent
                        && Live(Camera) && Camera.isActiveAndEnabled && Same(Camera, LocalPlayer.MainCam)
                        && Live(_cameraRotator) && Same(_cameraRotator, LocalPlayer.CamRotator)
                        && Live(_mainRotator) && Same(_mainRotator, LocalPlayer.MainRotator)
                        && Live(_follow) && Same(_follow, LocalPlayer.CamFollowHead)
                        && (!_taken && _crew == null || RigCurrent);
                }
                catch (Exception) { return false; }
            }
        }

        internal bool CoreCurrent => SameActor && Live(Body) && Same(Body, LocalPlayer.Rigidbody)
            && Live(_movement) && Same(_movement, LocalPlayer.FpCharacter)
            && Live(_inventory) && Same(_inventory, LocalPlayer.Inventory)
            && Live(_stats) && Same(_stats, LocalPlayer.Stats);

        internal bool SameActor
        {
            get
            {
                try
                {
                    return !GameRuntime.IsTitleScene && _load == SaveLifecycle.State.BeforeLoadCount
                        && Live(_saveManager) && SaveGameManager.TryGetInstance(out var saveManager)
                        && Same(_saveManager, saveManager) && !saveManager._loadingInProgress
                        && _networked == BoltNetwork.isRunning
                        && Live(Player) && Same(Player, LocalPlayer.Transform)
                        && (!_networked || Live(_entity) && _entity.isAttached && Same(_entity, LocalPlayer.Entity)
                            && Same(_attachment, _entity._entity));
                }
                catch (Exception) { return false; }
            }
        }

        private bool SamePlayer
        {
            get
            {
                try { return Live(Player) && Same(Player, LocalPlayer.Transform); }
                catch (Exception) { return false; }
            }
        }

        internal void RememberCursor() { _cursorLock = Cursor.lockState; _cursorVisible = Cursor.visible; }

        private bool CanRestoreView => SameActor && Alive && GameRuntime.State.IsInWorld
            && !GameRuntime.State.PauseMenuOpen && Live(_inventory) && Same(_inventory, LocalPlayer.Inventory)
            && _inventory.CurrentView == PlayerInventory.PlayerViews.World;

        internal void ReleaseCursor()
        {
            if (!CanRestoreView || !Application.isFocused) return;
            Cursor.lockState = _cursorLock; Cursor.visible = _cursorVisible;
        }

        internal void Take()
        {
            // A blocked initial attachment retries without replacing the native snapshots.
            if (_taken) return;
            if (!TryBindRig()) throw new InvalidOperationException("The player's seated limb bindings are unavailable.");
            _viewStates.Clear(); _animatorStates.Clear(); _cameraStates.Clear();
            _movementEnabled = _movement.enabled; _locked = _movement.Locked;
            _cameraRotatorEnabled = RememberView(_cameraRotator); _mainRotatorEnabled = RememberView(_mainRotator);
            _followEnabled = RememberView(_follow);
            _gravity = Body.useGravity; _kinematic = Body.isKinematic; _collisions = Body.detectCollisions;
            _velocity = Body.velocity; _angularVelocity = Body.angularVelocity;
            _startPosition = Player.position; _startRotation = Player.rotation;
            (_, _cameraParent, _cameraPosition, _cameraRotation, _near, _eyeOffset) = RememberCamera(Camera);
            _rootMotion = RememberAnimator(_animator!);
            _animationInputSpeed = _animControl!._maxInputSpeed;
            _walkAnimation = CabinAnimation.TryCreate(_animator!);
            var colliders = Body.GetComponentsInChildren<Collider>(true);
            if (colliders == null || colliders.WasCollected)
                throw new InvalidOperationException("The player collision hierarchy is unavailable.");
            // Inactive carried equipment can contribute hundreds of colliders.
            // Ownership, not the unfiltered hierarchy size, defines our capture.
            foreach (var collider in colliders)
                if (Live(collider) && Same(collider.attachedRigidbody, Body))
                    _colliders.Add((collider, collider.enabled));
            // All snapshots precede writes, including writes whose native setters can fail.
            _taken = true;
            _movement.enabled = false; _movement.Locked = true;
            _cameraRotator.enabled = false; _mainRotator.enabled = false; _follow.enabled = false;
            if (!Body.isKinematic) { Body.velocity = Vector3.zero; Body.angularVelocity = Vector3.zero; }
            Body.useGravity = false; Body.isKinematic = true; Body.detectCollisions = false;
            _animator!.applyRootMotion = false;
            foreach (var saved in _colliders) saved.Collider.enabled = false;
            Camera.nearClipPlane = .035f;
        }

        internal bool TryRebindPresentation()
        {
            if (!CoreCurrent || !Alive && !NativeControlsReady) return false;
            var camera = LocalPlayer.MainCam;
            var cameraRotator = LocalPlayer.CamRotator; var mainRotator = LocalPlayer.MainRotator;
            var follow = LocalPlayer.CamFollowHead;
            if (!Live(camera) || !camera.isActiveAndEnabled || !Live(cameraRotator)
                || !Live(mainRotator) || !Live(follow)) return false;
            RestorePose();
            if ((_taken || _crew != null) && !TryBindRig(true)) return false;
            if (_taken)
            {
                // A component can disappear and return during a view rebuild. Its
                // first baseline must survive our disabled state in the meantime.
                var cameraState = RememberCamera(camera);
                var cameraRotatorEnabled = RememberView(cameraRotator);
                var mainRotatorEnabled = RememberView(mainRotator);
                var followEnabled = RememberView(follow);
                if (!_passivePresentation && !RestoreDisplacedViews(camera, cameraRotator, mainRotator, follow)) return false;
                (_, _cameraParent, _cameraPosition, _cameraRotation, _near, _eyeOffset) = cameraState;
                _cameraRotatorEnabled = cameraRotatorEnabled; _mainRotatorEnabled = mainRotatorEnabled;
                _followEnabled = followEnabled;
            }
            Camera = camera; _cameraRotator = cameraRotator; _mainRotator = mainRotator; _follow = follow;
            return Current;
        }

        private bool RestoreDisplacedViews(Camera camera, SimpleMouseRotator cameraRotator,
            SimpleMouseRotator mainRotator, camFollowHead follow)
        {
            if (!CoreCurrent) return false;
            if (Live(Camera) && !Same(Camera, camera) && Mathf.Abs(Camera.nearClipPlane - .035f) < .0001f)
                Camera.nearClipPlane = _near;
            if (!CoreCurrent) return false;
            // A rotator can remain owned under the other role during a view rebuild.
            if (Live(_cameraRotator) && !Same(_cameraRotator, cameraRotator)
                && !Same(_cameraRotator, mainRotator) && !_cameraRotator.enabled)
                _cameraRotator.enabled = _cameraRotatorEnabled;
            if (!CoreCurrent) return false;
            if (Live(_mainRotator) && !Same(_mainRotator, mainRotator)
                && !Same(_mainRotator, cameraRotator) && !_mainRotator.enabled)
                _mainRotator.enabled = _mainRotatorEnabled;
            if (!CoreCurrent) return false;
            if (Live(_follow) && !Same(_follow, follow) && !_follow.enabled)
                _follow.enabled = _followEnabled;
            return CoreCurrent;
        }

        private bool RememberView(Behaviour component)
        {
            foreach (var saved in _viewStates)
                if (Same(saved.Component, component)) return saved.Enabled;
            var enabled = component.enabled;
            _viewStates.Add((component, enabled));
            return enabled;
        }

        private bool RememberAnimator(Animator animator)
        {
            foreach (var saved in _animatorStates)
                if (Same(saved.Animator, animator)) return saved.RootMotion;
            var rootMotion = animator.applyRootMotion;
            _animatorStates.Add((animator, rootMotion));
            return rootMotion;
        }

        private (Camera Camera, Transform? Parent, Vector3 Position, Quaternion Rotation, float Near, Vector3 EyeOffset)
            RememberCamera(Camera camera)
        {
            foreach (var saved in _cameraStates)
                if (Same(saved.Camera, camera)) return saved;
            // Preserve the native eye anchor rather than assuming the player origin is at the feet.
            var head = _biped!.references.head;
            var savedCamera = (camera, camera.transform.parent, camera.transform.localPosition,
                camera.transform.localRotation, camera.nearClipPlane,
                Quaternion.Inverse(head.rotation) * (camera.transform.position - head.position));
            _cameraStates.Add(savedCamera);
            return savedCamera;
        }

        internal bool TryBindRig(bool rebind = false)
        {
            if (_crew != null && (!rebind || RigCurrent)) return RigCurrent;
            // Bind after native equipment has finished lowering its hand items.
            // These are the actual locally driven solver bones, not a nearby rig.
            var animator = LocalPlayer.Animator; var animControl = LocalPlayer.AnimControl;
            var ik = LocalPlayer.IkTargetsController; var biped = Live(ik) ? ik._bipped : null;
            if (!RigReady(animator, animControl, ik, biped)) return false;
            // The old pose owns exact bones and compares its last applied values.
            // It can remove those writes even after the rig stops being current.
            _crew?.Restore();
            _walkAnimation?.Restore();
            var crew = ClientAircraftCrewPose.TryCreate(Player, biped!, true);
            if (crew?.Current != true) return false;
            if (_taken)
            {
                var rootMotion = RememberAnimator(animator!);
                var animationInputSpeed = animControl!._maxInputSpeed;
                var walkAnimation = CabinAnimation.TryCreate(animator!);
                if (!CoreCurrent) return false;
                if (!_passivePresentation && Live(_animator) && !Same(_animator, animator) && !_animator.applyRootMotion)
                    _animator.applyRootMotion = _rootMotion;
                if (!CoreCurrent) return false;
                _rootMotion = rootMotion; _animationInputSpeed = animationInputSpeed;
                _walkAnimation = walkAnimation;
            }
            _animator = animator; _animControl = animControl; _ik = ik; _biped = biped; _crew = crew;
            return RigCurrent;
        }

        private bool RigCurrent => RigReady(_animator, _animControl, _ik, _biped) && _crew?.Current == true;

        private static bool RigReady(Animator? animator, PlayerAnimatorControl? animControl,
            IkTargetsController? ik, BipedIK? biped) => Live(animator) && animator.isActiveAndEnabled
            && Same(animator, LocalPlayer.Animator) && Live(animControl) && animControl.isActiveAndEnabled
            && Same(animControl, LocalPlayer.AnimControl) && Live(ik) && ik.gameObject.activeInHierarchy
            && Same(ik, LocalPlayer.IkTargetsController) && Live(biped) && biped.gameObject.activeInHierarchy
            && Same(biped, ik._bipped) && Same(biped.transform, animControl.transform)
            && Same(biped.references.leftUpperArm, ik._leftHandLimb?.solver?.bone1?.transform)
            && Same(biped.references.leftForearm, ik._leftHandLimb?.solver?.bone2?.transform)
            && Same(biped.references.leftHand, ik._leftHandLimb?.solver?.bone3?.transform)
            && Same(biped.references.rightUpperArm, ik._rightHandLimb?.solver?.bone1?.transform)
            && Same(biped.references.rightForearm, ik._rightHandLimb?.solver?.bone2?.transform)
            && Same(biped.references.rightHand, ik._rightHandLimb?.solver?.bone3?.transform);

        internal void RestorePose()
        {
            if (!SameActor) return;
            _crew?.Restore();
            _walkAnimation?.Restore();
        }

        internal void RenderPose(AircraftCrewTargets targets, int seat, float sitBlend) => _crew?.Render(targets, seat, sitBlend);
        internal bool TryGetLeftPressContact(out Vector3 actualContact)
        {
            actualContact = default;
            return _crew != null && _crew.TryGetLeftPressContact(out actualContact);
        }
        internal void RenderRightHand(Vector3 target, Vector3 up, float reach, Quaternion? rotation, bool press)
            => _crew?.RenderRightHand(target, up, reach, rotation, press);
        internal bool TryRenderRightPress(Vector3 target, Vector3 up, Quaternion rotation, float reach,
            out Vector3 actualContact)
        {
            actualContact = default;
            return _crew != null && _crew.TryRenderRightPress(target, up, rotation, reach, out actualContact);
        }

        internal void RestoreControlPose()
        {
            if (SamePlayer && Live(_animator) && Same(_animator, LocalPlayer.Animator)
                && Live(_ik) && Same(_ik, LocalPlayer.IkTargetsController)
                && Live(_biped) && Same(_biped, _ik._bipped) && _crew?.Current == true)
                _crew.Restore();
        }

        internal void ReleaseControlRig()
        {
            try { RestoreControlPose(); }
            finally { _crew = null; }
        }
        internal bool RenderHeldExtinguisher(SpacecraftHeldStationTool held, Vector3 up,
            out Vector3 position, out Quaternion rotation)
        {
            position = default; rotation = Quaternion.identity;
            return _crew != null && held.Render(_crew, _biped!.references.rightUpperArm.position,
                Player.rotation, up, out position, out rotation);
        }
        internal void RestoreHeldExtinguisher()
        {
            if (SameActor) _crew?.Restore();
        }
        internal bool MatchesActor(ulong actor) => SameActor && (_networked
            ? Live(_entity) && _entity.isAttached && _entity.networkId.PackedValue == actor : actor == 0);
        internal ulong Actor => SameActor && _networked ? _entity!.networkId.PackedValue : 0;
        internal void RenderWalkAnimation(Vector3 localVelocity) => _walkAnimation?.Apply(localVelocity / _animationInputSpeed);
        internal void EndWalkAnimation() => _walkAnimation?.Restore();

        internal void SuspendPresentation()
        {
            if (!_taken || !CoreCurrent) return;
            if (!_passivePresentation)
            {
                _passivePresentation = true;
                _restorePassiveFollow = _restorePassiveRootMotion = _restorePassiveNear = true;
            }
            RestorePose();
            // Movement and rotators are also disabled by native downing. Only
            // relinquish our camera/animation overrides once per downing.
            if (_restorePassiveFollow)
            {
                if (Live(_follow) && !_follow.enabled) _follow.enabled = _followEnabled;
                _restorePassiveFollow = false;
            }
            if (_restorePassiveRootMotion)
            {
                if (Live(_animator) && !_animator.applyRootMotion) _animator.applyRootMotion = _rootMotion;
                _restorePassiveRootMotion = false;
            }
            if (_restorePassiveNear)
            {
                if (Live(Camera) && Mathf.Abs(Camera.nearClipPlane - .035f) < .0001f) Camera.nearClipPlane = _near;
                _restorePassiveNear = false;
            }
        }

        internal void ResumePresentation() => _passivePresentation = false;

        internal void MaintainCarrier()
        {
            if (!_taken || !CoreCurrent) return;
            Body.useGravity = false; Body.isKinematic = true; Body.detectCollisions = false;
            foreach (var saved in _colliders)
                if (Live(saved.Collider) && Same(saved.Collider.attachedRigidbody, Body) && saved.Collider.enabled)
                    saved.Collider.enabled = false;
        }

        internal void Maintain()
        {
            if (!_taken) return;
            if (_passivePresentation) { MaintainCarrier(); return; }
            _movement.enabled = false; _movement.Locked = true;
            _cameraRotator.enabled = false; _mainRotator.enabled = false; _follow.enabled = false;
            Body.useGravity = false; Body.isKinematic = true; Body.detectCollisions = false;
            if (Live(_animator)) _animator.applyRootMotion = false;
            foreach (var saved in _colliders) if (Live(saved.Collider) && saved.Collider.enabled) saved.Collider.enabled = false;
            Camera.nearClipPlane = .035f;
        }

        internal void Release(Vector3? safeExit, bool rollback)
        {
            if (!_taken) return;
            _taken = false;
            // World replacement and transport changes retire the seat lease,
            // but can leave this exact player alive with our movement overrides.
            // Release those components without granting authority to reposition it.
            if (!SamePlayer) return;
            var restoreControls = false;
            Exception? failure = null;
            void Restore(Action action)
            {
                try { action(); }
                catch (Exception error) { failure ??= error; }
            }
            Restore(() => restoreControls = Live(_stats) && Same(_stats, LocalPlayer.Stats)
                && SpacecraftLife.Read(_stats) == PlayerLifeState.Alive && !_passivePresentation);
            Restore(RestoreControlPose);
            Restore(() =>
            {
                if (Live(_animator) && Same(_animator, LocalPlayer.Animator)) _walkAnimation?.Restore();
            });
            _walkAnimation = null;
            _crew = null;
            Restore(() =>
            {
                if ((!_passivePresentation || _restorePassiveRootMotion)
                    && Live(_animator) && Same(_animator, LocalPlayer.Animator) && !_animator.applyRootMotion)
                    _animator.applyRootMotion = _rootMotion;
            });
            if (SameActor && Alive)
            {
                if (safeExit.HasValue && Finite(safeExit.Value))
                    Restore(() => Player.position = safeExit.Value);
                else if (rollback) Restore(() => Player.position = _startPosition);
                Restore(() => Player.rotation = _startRotation);
                Restore(() =>
                {
                    if (!Live(Body) || !Same(Body, LocalPlayer.Rigidbody)) return;
                    Body.position = Player.position; Body.rotation = _startRotation;
                });
            }
            Restore(() =>
            {
                if (!Live(Camera) || !Same(Camera, LocalPlayer.MainCam)) return;
                if (!_passivePresentation && CanRestoreView && SameParent(Camera.transform.parent, _cameraParent))
                {
                    Camera.transform.localPosition = _cameraPosition;
                    Camera.transform.localRotation = _cameraRotation;
                }
                if ((!_passivePresentation || _restorePassiveNear) && Mathf.Abs(Camera.nearClipPlane - .035f) < .0001f)
                    Camera.nearClipPlane = _near;
            });
            Restore(() =>
            {
                if (!Live(Body) || !Same(Body, LocalPlayer.Rigidbody)) return;
                if (!Body.useGravity) Body.useGravity = _gravity;
                if (Body.isKinematic) Body.isKinematic = _kinematic;
                if (!Body.detectCollisions) Body.detectCollisions = _collisions;
            });
            // Pre-boarding momentum is meaningful only when attachment itself failed.
            if (SameActor && !_kinematic && Alive && Live(Body) && Same(Body, LocalPlayer.Rigidbody))
                Restore(() => { Body.velocity = rollback ? _velocity : Vector3.zero; Body.angularVelocity = rollback ? _angularVelocity : Vector3.zero; });
            foreach (var saved in _colliders)
                Restore(() =>
                {
                    if (Live(Body) && Same(Body, LocalPlayer.Rigidbody) && Live(saved.Collider)
                        && Same(saved.Collider.attachedRigidbody, Body) && !saved.Collider.enabled)
                        saved.Collider.enabled = saved.Enabled;
                });
            Restore(() =>
            {
                if (!restoreControls || !Live(_movement) || !Same(_movement, LocalPlayer.FpCharacter)) return;
                if (!_movement.enabled) _movement.enabled = _movementEnabled;
                if (_movement.Locked) _movement.Locked = _locked;
            });
            Restore(() => { if (restoreControls && Live(_cameraRotator) && Same(_cameraRotator, LocalPlayer.CamRotator) && !_cameraRotator.enabled) _cameraRotator.enabled = _cameraRotatorEnabled; });
            Restore(() => { if (restoreControls && Live(_mainRotator) && Same(_mainRotator, LocalPlayer.MainRotator) && !_mainRotator.enabled) _mainRotator.enabled = _mainRotatorEnabled; });
            Restore(() => { if (restoreControls && Live(_follow) && Same(_follow, LocalPlayer.CamFollowHead) && !_follow.enabled) _follow.enabled = _followEnabled; });
            if (failure != null) throw failure;
        }
    }

    // Only the native locomotion blend inputs are leased; controller states,
    // upper-body layers, fingers and solver bindings continue to animate normally.
    private sealed class CabinAnimation
    {
        private static readonly int[] Parameters = { Animator.StringToHash("hSpeed"),
            Animator.StringToHash("vSpeed"), Animator.StringToHash("overallSpeed") };
        private readonly Animator _animator;
        private readonly float[] _before = new float[3], _applied = new float[3];
        private bool _changed;

        private CabinAnimation(Animator animator) { _animator = animator; }

        internal static CabinAnimation? TryCreate(Animator animator)
        {
            var parameters = animator.parameters;
            if (parameters == null || parameters.WasCollected || parameters.Length > 512) return null;
            foreach (var hash in Parameters)
            {
                var found = false;
                foreach (var parameter in parameters)
                    if (parameter != null && !parameter.WasCollected && parameter.nameHash == hash
                        && parameter.type == AnimatorControllerParameterType.Float) { found = true; break; }
                if (!found) return null;
            }
            return new CabinAnimation(animator);
        }

        internal void Apply(Vector3 movement)
        {
            if (!Live(_animator)) return;
            Restore();
            for (var i = 0; i < Parameters.Length; i++) _before[i] = _applied[i] = _animator.GetFloat(Parameters[i]);
            _changed = true;
            _applied[0] = Mathf.Clamp(movement.x, -1, 1);
            _applied[1] = Mathf.Clamp(movement.z, -1, 1);
            _applied[2] = Mathf.Clamp01(new Vector2(movement.x, movement.z).magnitude);
            for (var i = 0; i < Parameters.Length; i++) _animator.SetFloat(Parameters[i], _applied[i]);
        }

        internal void Restore()
        {
            if (!_changed) return;
            _changed = false;
            if (!Live(_animator)) return;
            Exception? failure = null;
            for (var i = 0; i < Parameters.Length; i++)
                try
                {
                    if (Mathf.Abs(_animator.GetFloat(Parameters[i]) - _applied[i]) < .0001f)
                        _animator.SetFloat(Parameters[i], _before[i]);
                }
                catch (Exception error) { failure ??= error; }
            if (failure != null) throw failure;
        }
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
    private static bool SameParent(Transform? first, Transform? second)
        => first == null && second == null || Same(first, second);
    private static bool Live([NotNullWhen(true)] UnityEngine.Object? value) => value != null && !value.WasCollected && value;
    private static bool Same(Il2CppObjectBase? first, Il2CppObjectBase? second)
        => first != null && second != null && !first.WasCollected && !second.WasCollected && first.Pointer == second.Pointer;
}
