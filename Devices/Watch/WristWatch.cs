// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using BepInEx.Logging;
using Il2CppInterop.Runtime.InteropTypes;
using Modstoke.Loader;
using Modstoke.Sons;
using Sons.Animation;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Client;

internal sealed class ClientWristWatch : IDisposable
{
    internal const string KeybindId = "modstoke.client.wristwatch";
    private readonly Func<ClientWatchCommand, bool> _command;
    private readonly Func<bool, bool> _setAwake;
    private readonly Action<string> _notice;
    private readonly Func<bool> _acquireInput;
    private readonly ClientWristWatchFit _fit = new();
    private ClientWristWatchModel? _model;
    private ClientWristWatchDisplay? _display;
    private ClientWristWatchPose? _pose;
    private readonly ClientWatchEquipment _equipment = new();
    private bool _pendingInspection;
    private float _inspectionDeadline;
    private Transform? _hand;
    private Camera? _camera;
    private IkTargetsController? _controller;
    private Quaternion _watchInHand;
    private Vector3 _watchOffset;
    private float _bareRadius;
    private Vector2 _cuffFit;
    private Vector2 _cuffCenter;
    private Vector2[]? _cuffContour;
    private int _cuffRenderers;
    private int _cuffSamples;
    private ClientWristWatchSnapshot _snapshot = new();
    private bool _registered;
    private KeyCode _assignedKey;
    private bool _disposed;
    private bool _canUse;
    private bool _tabletActive;
    private bool _worn;
    private float _nextAttach;
    private float _nextDisplay;
    private bool _osAwake;
    private long _osGeneration = -1;
    private readonly ManualLogSource _log = BepInEx.Logging.Logger.CreateLogSource("Modstoke Client watch");

    internal ClientWristWatch(Func<ClientWatchCommand, bool> command, Func<bool, bool> setAwake,
        Action<string> notice, Func<bool> acquireInput)
    {
        _command = command;
        _setAwake = setAwake;
        _notice = notice;
        _acquireInput = acquireInput;
    }
    internal bool IsInspecting => _pose?.Inspecting == true;
    internal ClientWatchPresencePhase PresencePhase => !IsAvailable
        ? ClientWatchPresencePhase.Hidden : IsInspecting ? ClientWatchPresencePhase.Inspecting : ClientWatchPresencePhase.Worn;
    internal bool HasInspectionKey => _registered && _assignedKey != KeyCode.None;
    // Equipment and the retired pose must finish before a tablet handoff.
    internal bool IsReleasing => _pendingInspection || _pose != null && !_pose.Inspecting
        || _equipment.Active && !IsInspecting;
    internal bool IsAvailable => !_disposed && _worn && GameRuntime.State.IsInWorld && SameBinding() && _display?.IsAvailable == true;
    internal bool CanInspect => IsAvailable && _canUse && !_tabletActive && HasInspectionKey
        && (IsInspecting || _pendingInspection || _pose == null && !_equipment.Active && HandActionFree(stowing: true));
    internal bool ReducedMotion { get; set; }
    internal string Status { get; private set; } = "";

    internal void Start(KeyCode configuredKey = KeyCode.F3)
    {
        if (_disposed || _registered) return;
        var key = KeyAvailable(configuredKey) ? configuredKey : KeyCode.None;
        _registered = InputBindings.TryRegisterKeybind(KeybindId, "Inspect wristwatch", "Modstoke Client",
            key, InputContexts.InWorld, true, "Hold to raise the wristwatch. Arrows browse; Enter selects; Backspace goes back; H opens Home and Q opens quick settings.");
        if (!_registered) throw new InvalidOperationException("The wristwatch key is already registered.");
        _assignedKey = key;
        if (key != configuredKey) Status = "The watch key is already assigned. Choose a free key in tablet settings.";
    }

    internal bool SetKey(KeyCode key)
    {
        if (!_registered || !KeyAvailable(key))
        { Status = "That key is already assigned."; return false; }
        EndInspection();
        var changed = InputBindings.SetKey(KeybindId, key);
        if (changed) { _assignedKey = key; Status = ""; }
        return changed;
    }

    private static bool KeyAvailable(KeyCode key)
    {
        if (key == KeyCode.None) return true;
        foreach (var binding in InputBindings.Snapshot().Values)
            if (binding.Id != KeybindId && binding.Enabled && binding.Key == key) return false;
        return true;
    }

    // Called once from the client main-thread update. The DTO may originate in the shared OS.
    internal void Update(bool canUse, bool tabletActive, ClientWristWatchSnapshot snapshot, bool worn)
    {
        if (_disposed) return;
        var inWorld = GameRuntime.State.IsInWorld;
        _canUse = canUse && inWorld;
        _tabletActive = tabletActive;
        if (!ReferenceEquals(_snapshot.Os, snapshot.Os)) _nextDisplay = 0;
        _snapshot = snapshot;
        if (_osGeneration != snapshot.Os.Generation)
        {
            _osGeneration = snapshot.Os.Generation;
            _osAwake = false;
        }
        _worn = worn;
        if (_pose != null)
        {
            _pose.ReducedMotion = ReducedMotion;
            if (!_pose.Available)
            {
                if (_pose.Status.Length > 0) { Status = _pose.Status; _notice(Status); }
                _pose.Dispose();
                _pose = null;
            }
        }
        if (!inWorld || !worn)
        {
            EndInspection(immediate: true);
            ReleasePresentation();
            if (_pose == null) UpdateEquipmentRestore(_tabletActive);
            return;
        }
        // Focus and pause affect hand interaction, not the wearable's lifetime.
        if (!_canUse || tabletActive) EndInspection(immediate: true);
        if (!SameBinding())
        {
            EndInspection(immediate: true);
            ReleasePresentation();
            if (Time.unscaledTime >= _nextAttach)
            {
                _nextAttach = Time.unscaledTime + 1;
                try { TryAttach(); }
                catch (Exception error)
                {
                    ReleasePresentation();
                    Status = "The wristwatch display is unavailable.";
                    _log.LogWarning("Wristwatch attachment failed: " + error);
                }
            }
        }
        // Observe competing equipment even while the arm is raised or fading.
        // Once ownership is lost, a later empty slot must not revive a restore.
        if (_pose != null && _equipment.Active && !_equipment.Ready) EndInspection();
        if (_pendingInspection && !_equipment.Active)
        {
            _pendingInspection = false;
            Status = "";
        }
        if (_pose == null && !_pendingInspection) UpdateEquipmentRestore(_tabletActive);
        if (_model == null || _display == null)
        {
            if (_canUse && _registered && !tabletActive && InputBindings.IsPressed(KeybindId))
                _notice("The watch is waiting for the player's arm model.");
            return;
        }
        var held = _canUse && _registered && !tabletActive && InputBindings.IsHeld(KeybindId);
        if (!held && (_pose != null || _pendingInspection)) EndInspection();
        if (held && _pose == null && !_equipment.Active && InputBindings.IsPressed(KeybindId)) RequestInspection();
        if (_pendingInspection)
        {
            if (_equipment.Ready && HandActionFree())
            {
                _pendingInspection = false;
                BeginInspection();
            }
            else if (Time.unscaledTime >= _inspectionDeadline)
            {
                EndInspection();
                Status = "Your equipment did not finish stowing. Finish its action, then hold the watch key again.";
                _notice(Status);
            }
        }
        if (IsInspecting)
        {
            if (Input.GetKeyDown(KeyCode.Escape)) EndInspection();
            else if (Input.GetKeyDown(KeyCode.LeftArrow)) Touch(ClientWatchCommand.Left);
            else if (Input.GetKeyDown(KeyCode.RightArrow)) Touch(ClientWatchCommand.Right);
            else if (Input.GetKeyDown(KeyCode.UpArrow)) Touch(ClientWatchCommand.Up);
            else if (Input.GetKeyDown(KeyCode.DownArrow)) Touch(ClientWatchCommand.Down);
            else if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) Touch(ClientWatchCommand.Activate);
            else if (Input.GetKeyDown(KeyCode.Backspace)) Touch(ClientWatchCommand.Back);
            else if (Input.GetKeyDown(KeyCode.H)) Touch(ClientWatchCommand.Home);
            else if (Input.GetKeyDown(KeyCode.Q)) Touch(ClientWatchCommand.QuickSettings);
        }
        if (_osAwake != IsInspecting && _setAwake(IsInspecting)) _osAwake = IsInspecting;
        if (Time.unscaledTime >= _nextDisplay)
        {
            _nextDisplay = Time.unscaledTime + .15f;
            _display.Refresh(_snapshot, IsInspecting);
        }
    }

    private void Touch(ClientWatchCommand command)
    {
        _pose?.TryTap(ClientWristWatchDisplay.TouchPoint(command), () =>
        {
            if (!IsInspecting) return;
            Plugin.TabletPresence.NotifyWatchInteraction();
            if (!_command(command)) _notice("The watch is busy. Try again in a moment.");
            _nextDisplay = 0;
        });
    }

    private bool HandActionFree(bool stowing = false) => _controller?._leftHandIKOperation != null
        && (!_controller._leftHandIKOperation._running || stowing && LocalPlayer.Inventory?.LeftHandItem != null)
        && _controller._rightHandIKOperation != null
        && (!_controller._rightHandIKOperation._running || stowing && LocalPlayer.Inventory?.RightHandItem != null)
        && LocalPlayer.Inventory?.HeldOnlyItemController?.HasItem != true
        && _controller._weaponIKOperation?._running != true && _controller._aimIKOperation?._running != true
        && (_controller._leftHeldRendererOnlyIKOperation?._running != true || stowing && LocalPlayer.Inventory?.LeftHandItem != null)
        && (_controller._rightHeldRendererOnlyIKOperation?._running != true || stowing && LocalPlayer.Inventory?.RightHandItem != null)
        && !ClientTabletHandPose.HasActiveSupport;

    private void RequestInspection()
    {
        if (!HandActionFree(stowing: true))
        {
            Status = LocalPlayer.Inventory?.HeldOnlyItemController?.HasItem == true
                ? "Put down the carried logs or planks before checking the watch."
                : "Finish the current hand action before checking the watch.";
            _notice(Status);
            return;
        }
        if (!_acquireInput()) return;
        if (!_equipment.TryStash(out var status))
        {
            Status = status;
            _notice(Status);
            return;
        }
        _pendingInspection = true;
        _inspectionDeadline = Time.unscaledTime + 8f;
        Status = "Stowing your equipment.";
    }

    private void BeginInspection()
    {
        if (_hand == null || _camera == null || _controller == null || _tabletActive) return;
        var pose = new ClientWristWatchPose(_camera, _controller, _hand, _watchInHand, _watchOffset,
            () => _canUse && _worn && !_tabletActive && _registered && InputBindings.IsHeld(KeybindId) && SameBinding(),
            RefitCuff, () => Mathf.Max(.005f, _cuffFit.y - _cuffCenter.y), () => _model?.Screen)
        { ReducedMotion = ReducedMotion };
        try
        {
            if (!pose.Begin())
            {
                Status = pose.Status;
                pose.Dispose();
                _notice(Status);
                return;
            }
            _pose = pose;
            Status = "";
            _nextDisplay = 0;
        }
        catch (Exception error)
        {
            pose.Dispose();
            Status = "The wristwatch inspection pose is unavailable.";
            _notice(Status);
            _log.LogWarning("Wristwatch inspection could not start: " + error);
        }
    }

    internal void EndInspection(bool immediate = false)
    {
        var wasInspecting = IsInspecting;
        _pendingInspection = false;
        // A menu or focus change can retire the pose without discarding the
        // stored equipment. Restore it once this same player is ready again.
        _pose?.EndInspection(immediate);
        if (_osAwake && _setAwake(false)) _osAwake = false;
        if (wasInspecting) _nextDisplay = 0;
    }

    // A failed tablet screen stops the normal field update. Finish the watch's
    // native handoff without recreating or refreshing either device display.
    internal void UpdateRelease(bool tabletActive)
    {
        if (_disposed) return;
        _canUse = false;
        _tabletActive = tabletActive;
        EndInspection(immediate: true);
        if (_pose != null && !_pose.Available)
        {
            _pose.Dispose();
            _pose = null;
        }
        if (_pose != null) return;
        UpdateEquipmentRestore(tabletActive);
    }

    private void UpdateEquipmentRestore(bool tabletActive)
    {
        _equipment.Finish();
        _equipment.UpdateRestore(!tabletActive && !ClientTabletHandPose.HasActiveSupport);
    }

    private bool TryAttach()
    {
        var controller = LocalPlayer.IkTargetsController;
        var camera = LocalPlayer.MainCam;
        var hand = controller?._leftHandLimb?.solver?.bone3?.transform;
        var biped = controller?._bipped;
        if (controller == null || camera == null || hand == null || biped == null
            || !controller.gameObject.activeInHierarchy || !camera.isActiveAndEnabled || !hand.IsChildOf(biped.transform)) return false;
        var index = hand.Find("LeftHandIndex1");
        var middle = hand.Find("LeftHandMiddle1");
        var pinky = hand.Find("LeftHandPinky1");
        if (index == null || middle == null || pinky == null) return false;
        var along = middle.position - hand.position;
        var across = index.position - pinky.position;
        var palm = Vector3.Cross(across, along);
        if (along.sqrMagnitude < .000001f || palm.sqrMagnitude < .0000001f) return false;
        palm.Normalize();
        // On this rig, pinky-to-index crossed with wrist-to-middle points into
        // the palm. The case sits on the opposite, dorsal side of that frame.
        var scale = hand.lossyScale;
        if (scale.x <= .0001f || scale.y <= .0001f || scale.z <= .0001f) return false;
        var rotation = Quaternion.LookRotation(palm, along.normalized);
        _watchInHand = Quaternion.Inverse(hand.rotation) * rotation;
        _watchOffset = Quaternion.Inverse(hand.rotation) * (-along.normalized * .008f);
        var width = across.magnitude;
        if (!float.IsFinite(width) || width < .025f || width > .14f) return false;
        var forearm = controller._leftHandLimb.solver.bone2?.transform;
        if (forearm == null) return false;
        var layer = -1;
        // The wrist bone uses the world layer. Use a live arm renderer, including
        // equipped clothes whose objects sit beside the animation rig.
        foreach (var skin in ClientWristWatchFit.Renderers(biped.transform, hand, forearm))
        {
            layer = skin.gameObject.layer;
            break;
        }
        if (layer < 0 || (camera.cullingMask & (1 << layer)) == 0) return false;
        _hand = hand;
        _camera = camera;
        _controller = controller;
        _bareRadius = Mathf.Clamp(width * .42f, .023f, .038f);
        _cuffFit = _fit.Measure(biped.transform, hand, forearm, _watchInHand, _watchOffset, _bareRadius,
            out _cuffRenderers, out _cuffSamples, measureSurface: false);
        _cuffCenter = _fit.Center;
        _cuffContour = _fit.Contour;
        _model = new ClientWristWatchModel(hand, _watchInHand, _watchOffset, layer, _cuffFit.x, _cuffFit.y, _cuffCenter, _cuffContour);
        _display = new ClientWristWatchDisplay(_model.Screen, camera, layer);
        _nextDisplay = 0;
        Status = "";
        return true;
    }

    private void RefitCuff()
    {
        if (!SameBinding() || _hand == null || _camera == null || _model == null) return;
        var forearm = _controller!._leftHandLimb?.solver?.bone2?.transform;
        if (forearm == null) return;
        var fit = _fit.Measure(_controller._bipped.transform, _hand, forearm, _watchInHand, _watchOffset, _bareRadius,
            out _cuffRenderers, out _cuffSamples);
        var center = _fit.Center;
        var contour = _fit.Contour;
        if (Mathf.Abs(fit.x - _cuffFit.x) < .002f && Mathf.Abs(fit.y - _cuffFit.y) < .002f
            && (center - _cuffCenter).sqrMagnitude < .000004f && ReferenceEquals(contour, _cuffContour))
        {
            LogInspectionFit();
            return;
        }
        // Rebuild only for a changed outfit. Held-item poses reuse the same fit.
        var model = new ClientWristWatchModel(_hand, _watchInHand, _watchOffset, _model.Root.layer, fit.x, fit.y, center, contour);
        ClientWristWatchDisplay? display = null;
        try
        {
            display = new ClientWristWatchDisplay(model.Screen, _camera, model.Root.layer);
            display.Refresh(_snapshot, IsInspecting);
        }
        catch { display?.Dispose(); model.Dispose(); throw; }
        _display?.Dispose();
        _model.Dispose();
        _model = model;
        _display = display;
        _cuffFit = fit;
        _cuffCenter = center;
        _cuffContour = contour;
        _nextDisplay = 0;
        LogInspectionFit();
    }

    private void LogInspectionFit()
    {
        if (_model == null || _camera == null) return;
        var screen = _model.Screen;
        var inView = _camera.WorldToViewportPoint(screen.position);
        var facing = Vector3.Dot(-screen.forward, (_camera.transform.position - screen.position).normalized);
        _log.LogInfo($"Watch raised; cuff radius {_cuffFit.x:F3} m, depth {_cuffFit.y:F3} m; "
            + $"center ({_cuffCenter.x:F3}, {_cuffCenter.y:F3}) m; "
            + $"screen viewport ({inView.x:F2}, {inView.y:F2}), distance {inView.z:F3} m, "
            + $"near plane {_camera.nearClipPlane:F3} m, facing {facing:F2}, layer {_model.Root.layer}; "
            + $"{_cuffRenderers} arm renderers, {_cuffSamples} cuff samples.");
    }

    private bool SameBinding() => _model != null && _model.Root != null && _hand != null && _camera != null
        && _camera.isActiveAndEnabled && Same(_camera, LocalPlayer.MainCam) && _controller != null
        && _controller.gameObject.activeInHierarchy && Same(_controller, LocalPlayer.IkTargetsController)
        && Same(_hand, _controller._leftHandLimb?.solver?.bone3?.transform);

    internal static ClientWristWatchVitals ReadVitals()
    {
        var condition = new ClientHealthObservation();
        try
        {
            condition = LocalPlayerReads.TryReadVitals(out var observed, out var status)
                ? ClientHealthObservation.From(observed) : new ClientHealthObservation { Status = status };
            // Stored SDK stat ranges do not include the game's resolved limits and factors.
            // Keep these native readings independent of condition-observation availability.
            var vitals = LocalPlayer.Vitals;
            if (vitals == null || !vitals.gameObject.activeInHierarchy) return new() { Condition = condition };
            var breathing = vitals.LungBreathing;
            var capacity = breathing?.MaxLungAirCapacityFinal;
            return new ClientWristWatchVitals
            {
                Condition = condition,
                Health = Nonnegative(vitals.GetHealth()),
                MaximumHealth = Positive(vitals.GetMaxHealth()),
                Stamina = Factor(vitals.GetStaminaFactor()),
                Fullness = Factor(vitals.GetFullnessFactor()),
                Hydration = Factor(vitals.GetHydrationFactor()),
                Rest = Factor(vitals.GetRestFactor()),
                Air = breathing != null && capacity is > 0 ? Factor(breathing.CurrentLungAir / capacity.Value) : null
            };
        }
        catch { return new() { Condition = condition }; }
    }

    private static float? Factor(float value) => float.IsFinite(value) ? Mathf.Clamp01(value) : null;
    private static float? Nonnegative(float value) => float.IsFinite(value) && value >= 0 ? value : null;
    private static float? Positive(float value) => float.IsFinite(value) && value > 0 ? value : null;
    private static bool Same(Il2CppObjectBase? first, Il2CppObjectBase? second) => first is not null
        && second is not null && !first.WasCollected && !second.WasCollected && first.Pointer == second.Pointer;

    private void ReleasePresentation()
    {
        // Inventory and equipment views retire this presentation, not the
        // player's fitted strap. Measure validates the rig and outfit before
        // reusing it, even when those views temporarily hide the arm.
        try { _display?.Dispose(); }
        finally
        {
            _display = null;
            try { _model?.Dispose(); }
            finally { _model = null; _hand = null; _camera = null; _controller = null; }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _fit.Reset();
        _equipment.Abandon();
        try { _pose?.Dispose(); }
        finally
        {
            _pose = null;
            try { ReleasePresentation(); }
            finally
            {
                if (_registered) InputBindings.TryUnregisterKeybind(KeybindId);
                _registered = false;
                BepInEx.Logging.Logger.Sources.Remove(_log);
                _log.Dispose();
            }
        }
    }
}
