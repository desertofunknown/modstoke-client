// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using BepInEx.Logging;
using Il2CppInterop.Runtime.InteropTypes;
using Modstoke.Sons;
using RootMotion.FinalIK;
using Sons.Animation;
using Sons.Input;
using TheForest.Items.Inventory;
using TheForest.Utils;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;

namespace Modstoke.Client;

// Owns only the current local actor. PlayerNetwork remains active and publishes its
// ordinary owner transform; the aircraft's host separately owns the seat lease.
internal sealed class ClientAircraftRider : IDisposable
{
    private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("Modstoke aircraft rider");
    private readonly Func<bool> _acquireFocus;
    private readonly Action _releaseFocus;
    private readonly ClientWatchEquipment _equipment = new();
    private ClientAircraftBody? _aircraft;
    private Capture? _capture;
    private bool _focus, _attached, _disposed, _pointer, _parkingBrake;
    private float _deadline, _lookYaw, _lookPitch, _nextThrottleGesture;
    private GameObject? _uiRoot, _pointerRoot;
    private EventSystem? _uiSystem, _previousUi, _navigationSystem;
    private InputActionAsset? _uiActions;
    private bool _navigationEnabled;

    internal ClientAircraftRider(Func<bool> acquireFocus, Action releaseFocus)
    {
        _acquireFocus = acquireFocus ?? throw new ArgumentNullException(nameof(acquireFocus));
        _releaseFocus = releaseFocus ?? throw new ArgumentNullException(nameof(releaseFocus));
    }

    internal bool Active => _aircraft != null;
    internal bool Attached => _attached;
    internal bool Restoring => !Active && _equipment.Active;
    internal int Seat { get; private set; } = -1;
    internal AircraftControls Controls { get; private set; } = AircraftControls.Parked;
    internal bool ExitRequested { get; private set; }
    internal bool Faulted { get; private set; }
    internal string FailureReason { get; private set; } = "";
    internal bool PointerActive => _attached && _pointer;
    internal Camera? Camera => _attached ? _capture?.Camera : null;

    // Acceptance is asynchronous: the terminal finishes holstering, then focus
    // blocks new actions while native equipment lowers the retained hand items.
    internal bool Begin(ClientAircraftBody body, int seat, out string reason)
    {
        reason = "Finish your current action before boarding.";
        if (_disposed || Active || Restoring || seat < 0 || seat > 2 || !Live(body.Root)) return false;
        Faulted = ExitRequested = false;
        FailureReason = "";
        try
        {
            if (!GameRuntime.State.IsInWorld || GameRuntime.State.PauseMenuOpen || !Application.isFocused)
                return false;
            var capture = Capture.TryCreate();
            if (capture == null) { reason = "Your player is not ready to board."; return false; }
            _capture = capture;
            _aircraft = body;
            Seat = seat;
            _deadline = Time.unscaledTime + 10;
            Controls = AircraftControls.Parked;
            _nextThrottleGesture = 0;
            reason = "";
            return true;
        }
        catch (Exception error)
        {
            Fail("The aircraft seat could not be prepared.", error);
            reason = FailureReason;
            return false;
        }
    }

    internal void Update()
    {
        if (_disposed) return;
        if (!Active) { _equipment.UpdateRestore(true); return; }
        try
        {
            if (Faulted && _attached)
            {
                if (_capture?.PhysicsCurrent != true || !_capture.Alive || !Live(_aircraft!.Root)) End(null, true);
                else
                {
                    if (Application.isFocused && !GameRuntime.State.PauseMenuOpen && Input.GetKeyDown(KeyCode.X))
                        ExitRequested = true;
                    Attach(false);
                }
                return;
            }
            if (_capture == null || !_capture.Current || !_capture.Alive
                || !GameRuntime.State.IsInWorld || !Live(_aircraft!.Root))
            {
                Fail("The aircraft seat is no longer available.");
                return;
            }
            // Remove only our last frame's writes before native animation runs.
            _capture.RestorePose();
            if (!_attached)
            {
                if (Input.GetKeyDown(KeyCode.Escape)
                    || Application.isFocused && !GameRuntime.State.PauseMenuOpen && Input.GetKeyDown(KeyCode.X))
                    ExitRequested = true;
                if (ExitRequested) return;
                if (Time.unscaledTime >= _deadline)
                {
                    Fail("Boarding could not finish. Try again after your hands are free.");
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
                _capture.Take();
                var flight = _aircraft!.State;
                _parkingBrake = false;
                Controls = new(0, 0, 0, flight.Throttle, _parkingBrake, flight.Flaps, flight.Engine, flight.Lights);
                _lookYaw = 0;
                // Frame the instrument panel and windscreen together on boarding.
                _lookPitch = Seat == 0 ? 12 : 0;
                _pointer = false;
                _attached = true;
                Attach();
            }

            var acceptsInput = Application.isFocused && !GameRuntime.State.PauseMenuOpen
                && LocalPlayer.Inventory.CurrentView == PlayerInventory.PlayerViews.World;
            if (acceptsInput)
            {
                if (Input.GetKeyDown(KeyCode.X) || Input.GetKeyDown(KeyCode.Escape)) ExitRequested = true;
                if (Input.GetKeyDown(KeyCode.Tab)) _pointer = !_pointer;
                if (Seat == 0)
                {
                    if (Input.GetKeyDown(KeyCode.I)) SetCockpitControl("engine");
                    if (Input.GetKeyDown(KeyCode.L)) SetCockpitControl("lights");
                    if (Input.GetKeyDown(KeyCode.F)) SetCockpitControl("flaps");
                    var throttle = Axis(KeyCode.LeftShift, KeyCode.LeftControl);
                    if (Input.GetKey(KeyCode.RightShift)) throttle = 1;
                    if (Input.GetKey(KeyCode.RightControl)) throttle = -1;
                    SetThrottle(Controls.Throttle + throttle * Mathf.Min(Time.unscaledDeltaTime, .1f) * .35f);
                    SetBrake(_parkingBrake || Input.GetKey(KeyCode.Space), Input.GetKeyDown(KeyCode.Space));
                    Controls = Controls with
                    {
                        Pitch = Axis(KeyCode.S, KeyCode.W),
                        Roll = Axis(KeyCode.D, KeyCode.A),
                        Yaw = Axis(KeyCode.E, KeyCode.Q)
                    };
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
            else Controls = Controls with { Pitch = 0, Roll = 0, Yaw = 0, Brake = _parkingBrake };
            SyncPointer(acceptsInput && _pointer);
            if (acceptsInput)
            {
                Cursor.lockState = _pointer ? CursorLockMode.None : CursorLockMode.Locked;
                Cursor.visible = _pointer;
            }
            Attach();
        }
        catch (Exception error) { Fail("Aircraft controls were released after a player state change.", error); }
    }

    // Called after native animation/camera constraints, through the shared late frame owner.
    internal void LateUpdate()
    {
        if (!_attached || Faulted || _capture == null || _aircraft == null) return;
        try
        {
            if (!_capture.Current || !_capture.Alive || !Live(_aircraft.Root))
            { Fail("The aircraft seat is no longer available."); return; }
            Attach();
            if (_aircraft.TryCrewTargets(Seat, out var targets)) _capture.RenderPose(targets, Seat);
            else { Fail("The cockpit crew targets are no longer available."); return; }
            // Seating moves the skeleton after native camera animation.
            PlaceCamera();
        }
        catch (Exception error) { Fail("The cockpit camera could not be maintained.", error); }
    }

    private void Attach(bool camera = true)
    {
        var capture = _capture!;
        var frame = _aircraft!.Root.transform;
        capture.Maintain();
        // The ordinary player origin is below the pelvis. Both meshes are seated
        // separately after animation; never parent the native network entity to the plane.
        var position = frame.TransformPoint(AircraftLayout.Seat(Seat) + Vector3.down * .8f);
        capture.Body.position = position;
        capture.Body.rotation = frame.rotation;
        capture.Player.SetPositionAndRotation(position, frame.rotation);
        if (camera) PlaceCamera();
    }

    private void PlaceCamera()
    {
        var frame = _aircraft!.Root.transform;
        _capture!.Camera.transform.SetPositionAndRotation(_capture.EyePosition,
            frame.rotation * Quaternion.Euler(_lookPitch, _lookYaw, 0));
    }

    internal bool ConsumeExitRequest()
    {
        var requested = ExitRequested;
        ExitRequested = false;
        return requested;
    }

    internal void SetCockpitControl(string command)
    {
        if (!_attached || !Application.isFocused || GameRuntime.State.PauseMenuOpen) return;
        if (command == "exit") { ExitRequested = true; return; }
        if (command == "pointer") { _pointer = !_pointer; return; }
        if (Seat != 0) return;
        switch (command)
        {
            case "engine":
            case "starter":
                Controls = Controls with { Engine = !Controls.Engine };
                Interact(command == "starter" ? AircraftInteraction.Starter : AircraftInteraction.Engine);
                break;
            case "lights": Controls = Controls with { Lights = !Controls.Lights }; Interact(AircraftInteraction.Lights); break;
            case "flaps": Controls = Controls with { Flaps = (byte)((Controls.Flaps + 1) % 3) }; Interact(AircraftInteraction.Flaps); break;
            case "throttle-up": SetThrottle(Controls.Throttle + .1f); break;
            case "throttle-down": SetThrottle(Controls.Throttle - .1f); break;
            case "brake":
                _parkingBrake = !_parkingBrake;
                SetBrake(_parkingBrake || Input.GetKey(KeyCode.Space), true);
                break;
        }
    }

    private void SetThrottle(float value)
    {
        value = Mathf.Clamp01(value);
        if (Mathf.Abs(Controls.Throttle - value) < .000001f) return;
        Controls = Controls with { Throttle = value };
        if (Time.unscaledTime < _nextThrottleGesture) return;
        _nextThrottleGesture = Time.unscaledTime + .6f;
        Interact(AircraftInteraction.Throttle);
    }

    private void SetBrake(bool value, bool operated)
    {
        Controls = Controls with { Brake = value };
        if (operated) Interact(AircraftInteraction.Brake);
    }

    private void Interact(AircraftInteraction interaction)
    {
        // Reset only on a new seat capture. Neutral/focus-loss updates retain
        // metadata, so the host never replays the last reach as a new action.
        var sequence = unchecked(Controls.InteractionSequence + 1);
        if (sequence == 0) sequence = 1;
        Controls = Controls with { Interaction = interaction, InteractionSequence = sequence };
    }

    internal bool End(Vector3? safeExit, bool restoreEquipment) => End(safeExit, restoreEquipment, false);

    private bool End(Vector3? safeExit, bool restoreEquipment, bool teardown)
    {
        if (_aircraft == null && _capture == null)
        {
            if (!restoreEquipment) _equipment.Abandon();
            return true;
        }
        var capture = _capture;
        var attached = _attached;
        var canRestoreEquipment = false;
        try
        {
            // Death or actor replacement must not leave the native Console map
            // captured if physical restoration cannot complete this frame.
            if (_focus && (capture?.SameActor != true || !capture.Alive)) ReleaseInput(capture);
            canRestoreEquipment = restoreEquipment && capture?.SameActor == true && capture.Alive;
            var velocity = Vector3.zero;
            if (attached && capture?.SameActor == true && capture.Alive)
            {
                var aircraft = _aircraft;
                if (teardown) safeExit = capture.Player.position;
                else if (!safeExit.HasValue)
                {
                    // Lease/world teardown has no exit packet. Never restore the
                    // living actor's collisions at its seat inside the solid hull.
                    if (aircraft != null && Live(aircraft.Root))
                    {
                        bool Obstacle(Collider collider) => Live(collider)
                            && !collider.transform.IsChildOf(aircraft.Root.transform)
                            && !collider.transform.IsChildOf(capture.Player);
                        if (!ClientAircraftService.TryEmergencyExit(aircraft, Seat, out var exit, Obstacle)) return false;
                        safeExit = exit;
                    }
                    else safeExit = capture.Player.position;
                }
                if (!teardown && !ClientAircraftService.ExitPositionClear(safeExit.Value)) return false;
                if (aircraft != null && Live(aircraft.Root))
                {
                    var flight = aircraft.State;
                    if (!teardown && (safeExit.Value - flight.Position).sqrMagnitude > 144) return false;
                    velocity = flight.Velocity + Vector3.Cross(flight.AngularVelocity,
                        capture.Player.position - flight.Position);
                    if (!Finite(velocity))
                    {
                        if (!teardown) return false;
                        velocity = Vector3.zero;
                    }
                }
            }
            capture?.Release(safeExit, !attached, velocity, teardown);
        }
        catch (Exception error)
        {
            Log.LogWarning("Aircraft player restoration: " + error.Message);
            if (capture?.Taken == true) return false;
        }
        _aircraft = null; _capture = null; _attached = _pointer = false; Seat = -1;
        ExitRequested = false;
        Controls = AircraftControls.Parked;
        try { ReleaseInput(capture); }
        finally { _equipment.Finish(canRestoreEquipment); }
        return true;
    }

    private void ReleaseInput(Capture? capture)
    {
        _pointer = false;
        try { DestroyPointer(); }
        catch (Exception error) { Log.LogWarning("Aircraft interface restoration: " + error.Message); }
        if (!_focus) return;
        _focus = false;
        try { _releaseFocus(); }
        catch (Exception error) { Log.LogWarning("Aircraft input restoration: " + error.Message); }
        try { capture?.ReleaseCursor(); }
        catch (Exception error) { Log.LogWarning("Aircraft cursor restoration: " + error.Message); }
    }

    private void Fail(string reason, Exception? error = null)
    {
        Faulted = true; FailureReason = reason;
        if (error != null) Log.LogWarning(reason + " " + error.GetType().Name + ": " + error.Message);
        Controls = AircraftControls.Parked;
        // The service requests host-approved egress and waits for its existing
        // leaving/acknowledgement handshake before releasing an attached actor.
        if (!_attached || _capture?.SameActor != true || !_capture.Alive) End(null, true);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Teardown(false);
        _equipment.Abandon();
        _disposed = true;
    }

    internal void Teardown(bool restoreEquipment)
    {
        // Teardown is not a departure: remove the owned collision volume first,
        // then give the current actor back to native physics even in a blocked wreck.
        if (_aircraft != null && Live(_aircraft.Root)) _aircraft.Root.SetActive(false);
        if (!End(null, restoreEquipment, true))
            throw new InvalidOperationException("The aircraft player capture could not be released during teardown.");
    }

    private static float Axis(KeyCode positive, KeyCode negative)
        => (Input.GetKey(positive) ? 1 : 0) - (Input.GetKey(negative) ? 1 : 0);

    private void SyncPointer(bool active)
    {
        if (!active) { ReleasePointer(); return; }
        if (!Live(_pointerRoot))
        {
            _pointerRoot = new GameObject("Modstoke Aircraft Pointer");
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
                _uiRoot = new GameObject("Modstoke Aircraft Input");
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

    private void ReleasePointer()
    {
        RestoreNavigation();
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
        internal readonly Camera Camera;
        private readonly FirstPersonCharacter _movement;
        private readonly PlayerInventory _inventory;
        private readonly PlayerStats _stats;
        private readonly SimpleMouseRotator _cameraRotator, _mainRotator;
        private readonly camFollowHead _follow;
        private readonly BoltEntity? _entity;
        private readonly Bolt.Entity? _attachment;
        private readonly bool _networked;
        private readonly int _scene;
        private readonly List<(Collider Collider, bool Enabled)> _colliders = new();
        private Animator? _animator;
        private PlayerAnimatorControl? _animControl;
        private IkTargetsController? _ik;
        private BipedIK? _biped;
        private ClientAircraftCrewPose? _crew;
        private bool _taken, _movementEnabled, _locked, _cameraRotatorEnabled, _mainRotatorEnabled, _followEnabled;
        private bool _gravity, _kinematic, _collisions, _cursorVisible;
        private CursorLockMode _cursorLock;
        private Vector3 _velocity, _angularVelocity, _cameraPosition, _startPosition, _eyeOffset;
        private Quaternion _cameraRotation, _startRotation;
        private Transform? _cameraParent;
        private float _near;
        internal bool Taken => _taken;
        internal Vector3 EyePosition => _biped!.references.head.position + _biped.references.head.rotation * _eyeOffset;

        private Capture(Transform player, Rigidbody body, Camera camera, FirstPersonCharacter movement,
            PlayerInventory inventory, PlayerStats stats, SimpleMouseRotator cameraRotator,
            SimpleMouseRotator mainRotator, camFollowHead follow)
        {
            Player = player; Body = body; Camera = camera; _movement = movement;
            _inventory = inventory; _stats = stats; _cameraRotator = cameraRotator;
            _mainRotator = mainRotator; _follow = follow;
            _scene = GameRuntime.State.SceneEventCount;
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
            if (!Live(player) || !Live(body) || !Live(camera) || !camera.isActiveAndEnabled
                || !Live(movement) || !movement.isActiveAndEnabled || movement.Locked
                || !Live(inventory) || inventory.CurrentView != PlayerInventory.PlayerViews.World
                || !Live(stats) || !Live(cameraRotator) || !Live(mainRotator) || !Live(follow))
                return null;
            var capture = new Capture(player, body, camera, movement, inventory, stats, cameraRotator, mainRotator, follow);
            return capture.Current && capture.Alive ? capture : null;
        }

        internal bool Alive => Live(_stats) && Same(_stats, LocalPlayer.Stats) && _stats._dyingState == PlayerStats.DyingState.Alive
            && !_stats._playerIsDowned && !_stats._respawning;

        internal bool PhysicsCurrent => SameActor && Live(Body) && Same(Body, LocalPlayer.Rigidbody);

        internal bool Current
        {
            get
            {
                try
                {
                    return PhysicsCurrent
                        && Live(Camera) && Same(Camera, LocalPlayer.MainCam)
                        && Live(_movement) && Same(_movement, LocalPlayer.FpCharacter)
                        && Live(_inventory) && Same(_inventory, LocalPlayer.Inventory)
                        && Live(_stats) && Same(_stats, LocalPlayer.Stats)
                        && Live(_cameraRotator) && Same(_cameraRotator, LocalPlayer.CamRotator)
                        && Live(_mainRotator) && Same(_mainRotator, LocalPlayer.MainRotator)
                        && Live(_follow) && Same(_follow, LocalPlayer.CamFollowHead)
                        && (!_taken || RigCurrent);
                }
                catch (Exception) { return false; }
            }
        }

        internal bool SameActor
        {
            get
            {
                try
                {
                    return _scene == GameRuntime.State.SceneEventCount && _networked == BoltNetwork.isRunning
                        && Live(Player) && Same(Player, LocalPlayer.Transform)
                        && (!_networked || Live(_entity) && _entity.isAttached && Same(_entity, LocalPlayer.Entity)
                            && Same(_attachment, _entity._entity));
                }
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
            // Bind after native equipment has finished lowering its hand items.
            // These are the actual locally driven solver bones, not a nearby rig.
            _animator = LocalPlayer.Animator; _animControl = LocalPlayer.AnimControl;
            _ik = LocalPlayer.IkTargetsController; _biped = Live(_ik) ? _ik._bipped : null;
            if (!Live(_animator) || !_animator.isActiveAndEnabled || !Live(_animControl) || !_animControl.isActiveAndEnabled
                || !Live(_ik) || !_ik.gameObject.activeInHierarchy || !Live(_biped)
                || !Same(_animControl.transform, _biped.transform))
                throw new InvalidOperationException("The player's seated animation rig is unavailable.");
            _crew = ClientAircraftCrewPose.TryCreate(Player, _biped, true);
            if (!RigCurrent) throw new InvalidOperationException("The player's seated limb bindings are unavailable.");
            _movementEnabled = _movement.enabled; _locked = _movement.Locked;
            _cameraRotatorEnabled = _cameraRotator.enabled; _mainRotatorEnabled = _mainRotator.enabled;
            _followEnabled = _follow.enabled;
            _gravity = Body.useGravity; _kinematic = Body.isKinematic; _collisions = Body.detectCollisions;
            _velocity = Body.velocity; _angularVelocity = Body.angularVelocity;
            _startPosition = Player.position; _startRotation = Player.rotation;
            _cameraParent = Camera.transform.parent;
            _cameraPosition = Camera.transform.localPosition; _cameraRotation = Camera.transform.localRotation;
            _near = Camera.nearClipPlane;
            // Seating preserves the native eye anchor without changing the head's scale or visibility.
            var head = _biped!.references.head;
            _eyeOffset = Quaternion.Inverse(head.rotation) * (Camera.transform.position - head.position);
            var colliders = Body.GetComponentsInChildren<Collider>(true);
            if (colliders == null || colliders.WasCollected || colliders.Length > 128)
                throw new InvalidOperationException("The player collision hierarchy is unavailable.");
            foreach (var collider in colliders)
                if (Live(collider) && Same(collider.attachedRigidbody, Body))
                    _colliders.Add((collider, collider.enabled));
            // All snapshots precede writes, including writes whose native setters can fail.
            _taken = true;
            _movement.enabled = false; _movement.Locked = true;
            _cameraRotator.enabled = false; _mainRotator.enabled = false; _follow.enabled = false;
            if (!Body.isKinematic) { Body.velocity = Vector3.zero; Body.angularVelocity = Vector3.zero; }
            Body.useGravity = false; Body.isKinematic = true; Body.detectCollisions = false;
            foreach (var saved in _colliders) saved.Collider.enabled = false;
            Camera.nearClipPlane = .035f;
        }

        private bool RigCurrent => Live(_animator) && _animator.isActiveAndEnabled && Same(_animator, LocalPlayer.Animator)
            && Live(_animControl) && _animControl.isActiveAndEnabled && Same(_animControl, LocalPlayer.AnimControl)
            && Live(_ik) && _ik.gameObject.activeInHierarchy && Same(_ik, LocalPlayer.IkTargetsController)
            && Live(_biped) && _biped.gameObject.activeInHierarchy && Same(_biped, _ik._bipped)
            && Same(_biped.transform, _animControl.transform)
            && _crew?.Current == true
            && Same(_biped.references.leftUpperArm, _ik._leftHandLimb?.solver?.bone1?.transform)
            && Same(_biped.references.leftForearm, _ik._leftHandLimb?.solver?.bone2?.transform)
            && Same(_biped.references.leftHand, _ik._leftHandLimb?.solver?.bone3?.transform)
            && Same(_biped.references.rightUpperArm, _ik._rightHandLimb?.solver?.bone1?.transform)
            && Same(_biped.references.rightForearm, _ik._rightHandLimb?.solver?.bone2?.transform)
            && Same(_biped.references.rightHand, _ik._rightHandLimb?.solver?.bone3?.transform);

        internal void RestorePose()
        {
            if (SameActor) _crew?.Restore();
        }

        internal void RenderPose(AircraftCrewTargets targets, int seat) => _crew?.Render(targets, seat);

        internal void Maintain()
        {
            if (!_taken) return;
            _movement.enabled = false; _movement.Locked = true;
            _cameraRotator.enabled = false; _mainRotator.enabled = false; _follow.enabled = false;
            Body.useGravity = false; Body.isKinematic = true; Body.detectCollisions = false;
            foreach (var saved in _colliders) if (Live(saved.Collider) && saved.Collider.enabled) saved.Collider.enabled = false;
            Camera.nearClipPlane = .035f;
        }

        internal void Release(Vector3? safeExit, bool rollback, Vector3 exitVelocity, bool teardown)
        {
            if (!_taken) return;
            // Never restore a replacement player, reparent a new camera, or move a dead actor.
            if (!SameActor) { _taken = false; return; }
            Exception? failure = null;
            void Restore(Action action)
            {
                try { action(); }
                catch (Exception error) { failure ??= error; }
            }
            if (Alive)
            {
                if (!rollback && (!safeExit.HasValue || !Finite(safeExit.Value)))
                    throw new InvalidOperationException("The aircraft exit is not ready.");
                // Complete relocation while collision and native movement are still
                // captured. A failed native setter must not fall through to restoration.
                var position = safeExit ?? _startPosition;
                void Relocate()
                {
                    Player.SetPositionAndRotation(position, _startRotation);
                    if (Live(Body) && Same(Body, LocalPlayer.Rigidbody))
                    {
                        Body.position = position;
                        Body.rotation = _startRotation;
                    }
                }
                // Once the owned hull is disabled, a failed pose setter must not
                // orphan captured physics or input during world/service teardown.
                if (teardown) Restore(Relocate);
                else Relocate();
            }
            _taken = false;
            Restore(() => _crew?.Restore());
            _crew = null;
            Restore(() =>
            {
                if (!Live(Camera) || !Same(Camera, LocalPlayer.MainCam)) return;
                if (CanRestoreView && SameParent(Camera.transform.parent, _cameraParent))
                {
                    Camera.transform.localPosition = _cameraPosition;
                    Camera.transform.localRotation = _cameraRotation;
                }
                if (Mathf.Abs(Camera.nearClipPlane - .035f) < .0001f) Camera.nearClipPlane = _near;
            });
            Restore(() =>
            {
                if (!Live(Body) || !Same(Body, LocalPlayer.Rigidbody)) return;
                Body.useGravity = _gravity; Body.isKinematic = _kinematic; Body.detectCollisions = _collisions;
            });
            // Pre-boarding momentum is meaningful only when attachment itself failed.
            if (!_kinematic && Alive && Live(Body) && Same(Body, LocalPlayer.Rigidbody))
                Restore(() => { Body.velocity = rollback ? _velocity : exitVelocity; Body.angularVelocity = rollback ? _angularVelocity : Vector3.zero; });
            foreach (var saved in _colliders)
                Restore(() => { if (Live(saved.Collider) && !saved.Collider.enabled) saved.Collider.enabled = saved.Enabled; });
            Restore(() =>
            {
                if (!Live(_movement) || !Same(_movement, LocalPlayer.FpCharacter)) return;
                if (!_movement.enabled) _movement.enabled = _movementEnabled;
                if (_movement.Locked) _movement.Locked = _locked;
            });
            Restore(() => { if (Live(_cameraRotator) && Same(_cameraRotator, LocalPlayer.CamRotator) && !_cameraRotator.enabled) _cameraRotator.enabled = _cameraRotatorEnabled; });
            Restore(() => { if (Live(_mainRotator) && Same(_mainRotator, LocalPlayer.MainRotator) && !_mainRotator.enabled) _mainRotator.enabled = _mainRotatorEnabled; });
            Restore(() => { if (Live(_follow) && Same(_follow, LocalPlayer.CamFollowHead) && !_follow.enabled) _follow.enabled = _followEnabled; });
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
