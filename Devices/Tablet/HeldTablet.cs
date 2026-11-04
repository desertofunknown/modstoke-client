// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using BepInEx.Logging;
using Il2CppInterop.Runtime.InteropTypes;
using Modstoke.Sons;
using Sons.Inventory;
using Sons.Items.Core;
using Sons.Weapon;
using ItemIdentifiers = SonsSdk.ItemTools.Identifiers;
using TheForest.Items.Inventory;
using TheForest.Utils;
using UnityEngine;
using UnityEngine.UI;

namespace Modstoke.Client;

// Retains native inventory and map services; the tablet owns its visible draw and grip.
internal sealed class ClientHeldTablet : IDisposable
{
    private const float DrawDuration = 0.62f;
    private const float HolsterDuration = 0.38f;
    private enum Phase { Idle, Drawing, Open, Holstering, Closing }
    private Phase _phase;
    private PlayerInventory? _inventory;
    private ItemInstanceManager? _itemManager;
    private Transform? _player;
    private int _scene;
    private int _playerScene;
    private int _inventoryScene;
    private ItemInstance? _previous;
    private ItemInstance? _previousRight;
    private GameObject? _previousRightObject;
    private ItemInstance? _gpsItem;
    private GameObject? _heldObject;
    private GameObject? _mount;
    private Canvas? _nativeScreen;
    private GraphicRaycaster? _nativeRaycaster;
    private Transform? _poseParent;
    private ClientTabletGpsPose? _gpsPose;
    private Quaternion _viewRotation;
    private float _presentedAt;
    private float _holsterAt;
    private float _holsterLift;
    private bool _raised;
    private float _width;
    private float _height;
    private bool _nativeScreenEnabled;
    private bool _nativeRaycasterEnabled;
    private bool _gpsRequested;
    private bool _restoreLeft;
    private bool _restoreRight;
    private bool _holsterRequested;
    private bool _controller;
    private float _deadline;
    private float _drawWait;
    private int _drawWaitFrame;
    private byte _drawStage;
    private float _restoreWait;
    private int _restoreWaitFrame;
    private ClientTabletModel? _model;
    private ClientDroneControllerModel? _controllerModel;
    private ClientTabletNavigation? _navigation;
    private ClientFieldProfile _fieldProfile = new();
    private Guid? _pendingFieldFocus;
    private Action<ClientFieldAction>? _fieldChanged;
    private Action<ClientFieldPlace>? _sharePlace;
    private readonly ClientTabletNavigation.ViewState _mapView = new();
    private ClientTabletHandPose? _handPose;
    private readonly List<Renderer> _hiddenRenderers = new();
    private Canvas.WillRenderCanvases? _beforeRender;
    private readonly ManualLogSource _log = BepInEx.Logging.Logger.CreateLogSource("Modstoke Client tablet");

    internal bool IsDrawing => _phase == Phase.Drawing;
    internal bool IsActive => _phase != Phase.Idle;
    internal bool IsController => _controller;
    internal bool IsOpen => _phase == Phase.Open;
    internal bool IsHolstering => _phase == Phase.Holstering;
    internal bool HolsterComplete => _phase == Phase.Holstering && Time.unscaledTime - _holsterAt >= HolsterDuration;
    internal bool PresentationReady => _mount != null && _mount;
    internal Transform ScreenParent => _mount!.transform;
    internal Camera? ScreenCamera { get; private set; }
    internal int Layer { get; private set; }
    internal int SortingLayer { get; private set; }
    internal int SortingOrder { get; private set; }
    internal float PixelScale { get; private set; }
    internal string Status { get; private set; } = "";
    internal bool NavigationAvailable => _navigation?.Available == true;

    internal bool ReservesInventoryItem(int itemId) => IsActive
        && (itemId == ItemIdentifiers.GPSTracker
            || _previous is not null && (_previous.WasCollected || _previous._itemID == itemId)
            || _previousRight is not null && (_previousRight.WasCollected || _previousRight._itemID == itemId));

    internal bool CanBegin()
    {
        var inventory = LocalPlayer.Inventory;
        return _phase == Phase.Idle && CanUse(inventory)
            && HandsIdle(inventory!)
            && NextHandEmpty(inventory, EquipmentSlot.RightHand) && NextHandEmpty(inventory, EquipmentSlot.LeftHand)
            && (inventory.LeftHandItem?._itemID == ItemIdentifiers.GPSTracker
                || (HasStoredGps(inventory) && inventory.CanEquipItemInLeftHand(ItemIdentifiers.GPSTracker)));
    }

    internal bool Begin(bool controller = false)
    {
        if (_phase != Phase.Idle)
            return false;
        var inventory = LocalPlayer.Inventory;
        if (!CanUse(inventory) || !HandsIdle(inventory!)
            || !NextHandEmpty(inventory, EquipmentSlot.RightHand) || !NextHandEmpty(inventory, EquipmentSlot.LeftHand))
            return Refuse("Finish your current equipment action before taking out the tablet.");
        var left = inventory.LeftHandItem;
        var existingGps = left != null && left._itemID == ItemIdentifiers.GPSTracker;
        if (!existingGps && !HasStoredGps(inventory))
            return Refuse("Your GPS must be in your inventory before opening the tablet.");
        var player = LocalPlayer.Transform;
        if (player == null)
            return Refuse("Wait for the local player and inventory to finish loading before taking out the tablet.");
        var playerScene = player.gameObject.scene;
        var inventoryScene = inventory.gameObject.scene;
        if (!playerScene.IsValid() || !playerScene.isLoaded || !inventoryScene.IsValid() || !inventoryScene.isLoaded)
            return Refuse("Wait for the local player and inventory to finish loading before taking out the tablet.");
        _inventory = inventory;
        _itemManager = inventory._itemInstanceManager;
        _player = player;
        _scene = GameRuntime.State.SceneEventCount;
        _playerScene = playerScene.handle;
        _inventoryScene = inventoryScene.handle;
        // The GPS supplies map services while the tablet is open. Closing the
        // replacement puts both away, even when the GPS was already equipped.
        _previous = existingGps ? null : left;
        _previousRight = inventory.RightHandItem;
        if (_previousRight != null) HeldObject(_previousRight._itemID, out _previousRightObject);
        _gpsRequested = existingGps;
        _gpsItem = existingGps ? left : null;
        _restoreLeft = true;
        _restoreRight = _previousRight != null;
        _deadline = Time.unscaledTime + 30;
        _drawWait = 0;
        _drawWaitFrame = Time.frameCount;
        _drawStage = 0;
        _controller = controller;
        _phase = Phase.Drawing;
        _beforeRender = (Action)BeforeRender;
        Canvas.add_preWillRenderCanvases(_beforeRender);
        SuppressNativePresentation();
        Status = controller ? "Taking out the drone controller…" : "Taking out the field tablet…";
        _log.LogInfo($"Preparing tablet; left item {left?._itemID ?? 0}, right item {_previousRight?._itemID ?? 0}.");
        // Retain the exact instance; native stash memorizes it and runs its normal unequip animation.
        if (_restoreRight)
            inventory.StashRightHandItem(false, true, false);
        AdvanceDrawing();
        return _phase == Phase.Drawing || _phase == Phase.Open;
    }

    internal void Update()
    {
        if (_phase == Phase.Idle)
            return;
        if (_phase == Phase.Closing)
        {
            if (!SameEquipmentContext())
            {
                ReleasePresentation();
                Reset();
                return;
            }
            // Native unequip removes the dictionary entry before its held object
            // finishes the layer-5 idle wait. Keep that retiring GPS hidden.
            SuppressNativePresentation();
            RestoreEquipment();
            return;
        }
        if (!SameContext())
        {
            if (_phase == Phase.Drawing) DrawingFailure("The player's equipment changed while preparing the tablet.");
            Close(true);
            return;
        }
        if (_phase == Phase.Drawing)
        {
            AdvanceDrawing();
            return;
        }
        if (!Same(_inventory!.LeftHandItem, _gpsItem) || !RightHandEmpty(_inventory))
        {
            Close(false);
            return;
        }
        if (!PresentationValid())
        {
            Close(true);
            return;
        }
        if (!_raised) UpdatePresentation();
        _handPose?.Update();
        if (_raised && _handPose?.SupportAvailable != true) Close(true);
    }

    private void AdvanceDrawing()
    {
        var inventory = _inventory!;
        // Native equipment and IK advance in frames. A multiplayer loading
        // stall must not spend their entire animation allowance in one frame.
        if (_drawWaitFrame != Time.frameCount)
        {
            _drawWaitFrame = Time.frameCount;
            _drawWait += Mathf.Clamp(Time.unscaledDeltaTime, 0, Time.maximumDeltaTime);
        }
        if (_raised && _handPose?.SupportAvailable != true)
        {
            DrawingFailure(!string.IsNullOrEmpty(_handPose?.Status) ? _handPose.Status
                : "The native hand pose is no longer available.");
            Close(true);
            return;
        }
        var right = inventory.RightHandItem;
        if ((right != null && !Same(right, _previousRight)) || !NextHandEmpty(inventory, EquipmentSlot.RightHand))
        {
            DrawingFailure("Another right-hand action interrupted the tablet. Finish equipping it, then try again.");
            Close(false);
            return;
        }
        if (_gpsRequested && !PresentationReady) TryAttach();
        SuppressNativePresentation();
        if (PresentationReady)
        {
            if (!PresentationValid())
            {
                DrawingFailure("The GPS display changed while preparing the tablet.");
                Close(true);
                return;
            }
            if (!_raised) UpdatePresentation();
        }
        // Stash clears the inventory slot before the old held object finishes
        // its native animation. Do not start another equip during that interval.
        if (RightHandEmpty(inventory) && PreviousRightStashed())
        {
            if (!_gpsRequested && ((_previous == null ? inventory.LeftHandItem != null : !Same(inventory.LeftHandItem, _previous))
                || !NextHandEmpty(inventory, EquipmentSlot.LeftHand)))
            {
                DrawingFailure("Another left-hand action interrupted the tablet. Finish equipping it, then try again.");
                Close(false);
                return;
            }
            if (!_gpsRequested && HandsIdle(inventory))
            {
                var canEquip = inventory.CanEquipItemInLeftHand(ItemIdentifiers.GPSTracker);
                if (canEquip)
                {
                    // Hide the existing held prefab before native equip activates it.
                    _gpsRequested = true;
                    SuppressNativePresentation();
                    if (!inventory.TryEquip(ItemIdentifiers.GPSTracker, false, true, false))
                    {
                        // TryEquip may already have changed native inventory state.
                        // Never retry the request or manufacture a replacement GPS.
                        DrawingFailure("The game did not accept the GPS equip request. Check your GPS in the inventory before trying again.");
                        _restoreLeft = false;
                        _restoreRight = false;
                        Close(false);
                        return;
                    }
                    _log.LogInfo("Right-hand stash finished; preparing native GPS services.");
                    AdvanceDrawStage(1);
                    SuppressNativePresentation();
                    if (!PresentationReady) TryAttach();
                }
                // CanEquip includes native mid-action and toggle cooldown gates.
                // Wait within this draw's deadline without submitting another equip.
            }
            var left = inventory.LeftHandItem;
            var next = NextHand(inventory, EquipmentSlot.LeftHand);
            var gps = left != null && left._itemID == ItemIdentifiers.GPSTracker ? left
                : next != null && next._itemID == ItemIdentifiers.GPSTracker ? next : null;
            if (gps != null)
            {
                _gpsItem ??= gps;
                if (!Same(gps, _gpsItem))
                {
                    DrawingFailure("The equipped GPS changed while preparing the tablet.");
                    Close(false);
                    return;
                }
                if (PresentationReady)
                {
                    AdvanceDrawStage(2);
                    if (!PresentationValid())
                    {
                        DrawingFailure("The GPS display changed while preparing the tablet.");
                        Close(true);
                        return;
                    }
                    if (!_raised) UpdatePresentation();
                    if (Same(left, _gpsItem) && NextHandEmpty(inventory, EquipmentSlot.LeftHand)
                        && inventory._currentEquipState == PlayerInventory.HandEquipState.Idle)
                    {
                        // Native GPS equip waits for layer 5 before activating
                        // its services. Take over that layer only after it ends.
                        if (_gpsPose == null)
                        {
                            if (!ClientTabletGpsPose.TryAcquire(out _gpsPose))
                            {
                                DrawingFailure("The tablet could not take over the GPS animation layer.");
                                Close(true);
                                return;
                            }
                            _log.LogInfo("Native GPS services ready; preparing tablet grip.");
                            AdvanceDrawStage(3);
                        }
                        if (!_raised && !PrepareGrip())
                        {
                            DrawingFailure(Status);
                            Close(true);
                            return;
                        }
                        // Equip and the native support blend finish before exposing either the
                        // casing or the screen. The terminal builds its UI in this same update.
                        if (_handPose?.SupportReady == true)
                        {
                            _phase = Phase.Open;
                            _presentedAt = Time.unscaledTime;
                            _mount!.SetActive(true);
                            Status = "";
                            _log.LogInfo("Tablet grip ready; opening display.");
                            return;
                        }
                    }
                }
            }
            else if (left != null && !Same(left, _previous))
            {
                DrawingFailure("Another left-hand item interrupted the tablet.");
                Close(false);
                return;
            }
        }
        if (_drawWait >= 8 || Time.unscaledTime >= _deadline)
        {
            DrawingFailure(!PreviousRightStashed() ? "The previous held item did not finish stashing. Finish its action, then try again."
                : !_gpsRequested ? "The game is still finishing a hand action. Finish it before opening the tablet."
                : !PresentationReady ? "The local GPS screen did not become ready. Put away the GPS and try F4 again."
                : !Same(inventory.LeftHandItem, _gpsItem) || !NextHandEmpty(inventory, EquipmentSlot.LeftHand)
                    || inventory._currentEquipState != PlayerInventory.HandEquipState.Idle
                    ? "The local GPS equip animation did not finish. Finish that hand action and try F4 again."
                : _raised && !string.IsNullOrEmpty(_handPose?.Status)
                ? _handPose.Status : "The local tablet grip did not finish preparing.");
            Close(true);
        }
    }

    private void AdvanceDrawStage(byte stage)
    {
        if (stage <= _drawStage) return;
        _drawStage = stage;
        _drawWait = 0;
    }

    private bool TryAttach()
    {
        if (!CurrentHeldObject(out var held) || held == null || !held.activeInHierarchy)
            return false;
        var gps = held.GetComponentInChildren<GpsTrackerController>(false);
        if (gps == null || !gps.isActiveAndEnabled)
            return false;
        var system = gps._gpsTrackerSystem;
        var nativeScreen = system != null ? system._rootCanvas : null;
        if (nativeScreen == null || !nativeScreen.gameObject.activeInHierarchy || nativeScreen.renderMode != RenderMode.WorldSpace)
            return false;
        var screenRect = nativeScreen.GetComponent<RectTransform>();
        // This is our own first-person display, independent of the native canvas camera.
        var camera = LocalPlayer.MainCam;
        if (screenRect == null || !screenRect.IsChildOf(held.transform) || screenRect.parent == null
            || gps.transform.IsChildOf(screenRect)
            || camera == null || !camera.isActiveAndEnabled || camera.targetTexture != null)
            return false;
        var scale = screenRect.lossyScale;
        var nativeWidth = screenRect.rect.width * Math.Abs(scale.x);
        var nativeHeight = screenRect.rect.height * Math.Abs(scale.y);
        if (!float.IsFinite(nativeWidth) || !float.IsFinite(nativeHeight)
            || nativeWidth < 0.015f || nativeWidth > 0.8f || nativeHeight < 0.015f || nativeHeight > 0.8f)
            return false;
        var renderers = held.GetComponentsInChildren<Renderer>(false);
        var cameraScale = camera.transform.lossyScale;
        if (renderers.Length == 0 || renderers.Length > 64 || !PositiveScale(cameraScale))
            return false;

        _heldObject = held;
        RememberNativeScreen(nativeScreen);
        ScreenCamera = camera;
        Layer = nativeScreen.gameObject.layer;
        SortingLayer = nativeScreen.sortingLayerID;
        SortingOrder = nativeScreen.sortingOrder + 1;
        _height = Mathf.Clamp(nativeHeight * 1.35f, 0.20f, 0.24f);
        _width = _height * 1.6f;
        PixelScale = _height / 900 * (_controller ? ClientDroneControllerModel.DisplayScale : ClientTabletModel.DisplayScale);
        // Retain the canvas only as the source of map assets and presentation settings.
        _poseParent = screenRect.parent;
        _viewRotation = Quaternion.Euler(3f, -5f, -2f);
        _mount = new GameObject(_controller ? "Modstoke Held Drone Controller" : "Modstoke Held Tablet");
        _mount.SetActive(false);
        _mount.layer = Layer;
        _mount.transform.SetParent(camera.transform, false);
        _mount.transform.localScale = new Vector3(1 / cameraScale.x, 1 / cameraScale.y, 1 / cameraScale.z);
        UpdatePresentation();
        if (_controller)
            _controllerModel = new ClientDroneControllerModel(_mount.transform, _width, _height, Layer, renderers[0].sharedMaterial);
        else
            _model = new ClientTabletModel(_mount.transform, _width, _height, Layer, renderers[0].sharedMaterial);
        HideNativeRenderers();
        SuppressNativeScreen();
        _navigation = new ClientTabletNavigation(system!, nativeScreen, held.transform, camera, SortingLayer, SortingOrder);
        _navigation.RestoreView(_mapView);
        _navigation.SetFieldNavigation(_fieldProfile, _fieldChanged, _sharePlace);
        if (_pendingFieldFocus is { } focus)
        {
            _navigation.FocusFieldDestination(focus);
            _pendingFieldFocus = null;
        }
        _handPose = new ClientTabletHandPose(_mount.transform, camera, _width, _height, UpdateSupportedPresentation,
            () => SameContext() && RightHandEmpty(_inventory!) && PresentationValid()
                && _phase != Phase.Closing && _phase != Phase.Idle);
        return true;
    }

    private void UpdatePresentation() => UpdatePresentation(null, 0, Vector3.zero);

    private void UpdateSupportedPresentation(Vector3 shoulder, float reach, Vector3 wristOffset)
        => UpdatePresentation(shoulder, reach, wristOffset);

    private void UpdatePresentation(Vector3? shoulder, float supportReach, Vector3 wristOffset)
    {
        if (_mount == null || ScreenCamera == null) return;
        var cameraScale = ScreenCamera.transform.lossyScale;
        if (!PositiveScale(cameraScale)) return;
        var inverseProjection = ScreenCamera.projectionMatrix.inverse;
        var lift = _phase == Phase.Open ? Mathf.SmoothStep(0, 1,
            Mathf.Clamp01((Time.unscaledTime - _presentedAt) / DrawDuration))
            : _phase == Phase.Holstering ? _holsterLift * (1 - Mathf.SmoothStep(0, 1,
                Mathf.Clamp01((Time.unscaledTime - _holsterAt) / HolsterDuration))) : 0;
        // Move the casing and grip together, before fitting reach. Scaled time
        // stops the idle at pause; a reaching finger smoothly quiets it.
        var idle = _controller ? 0 : lift * lift * (_handPose?.IdleWeight ?? 0);
        var time = Time.time;
        var rotation = _viewRotation * Quaternion.Euler(idle * .20f * Mathf.Sin(time * 1.3f),
            idle * .12f * Mathf.Sin(time * .91f), idle * .16f * Mathf.Sin(time * 1.1f));
        var center = FitInCamera(rotation, inverseProjection);
        center += new Vector3(.0008f * Mathf.Sin(time * .91f), .0012f * Mathf.Sin(time * 1.3f), 0) * idle;
        var bottom = FrustumPoint(inverseProjection, 0.50f, 0) * center.z;
        var extent = Mathf.Abs((rotation * Vector3.up).y) * (_height * 0.5f + 0.016f)
            + Mathf.Abs((rotation * Vector3.right).y) * (_width * 0.5f + 0.016f);
        if (_controller)
        {
            var bounds = ClientDroneControllerModel.VisualBounds(_width, _height);
            var loweredRotation = rotation * Quaternion.Euler(32f, -8f, -10f);
            extent = float.NegativeInfinity;
            var bottomSlope = FrustumPoint(inverseProjection, 0.50f, 0).y;
            for (var corner = 0; corner < 8; corner++)
            {
                var offset = loweredRotation * BoundsCorner(bounds, corner);
                extent = Mathf.Max(extent, offset.y - bottomSlope * offset.z);
            }
        }
        var lowered = bottom - Vector3.up * (extent + 0.045f) - Vector3.right * .045f;
        // One tablet-specific path drives the casing and wrist targets together.
        // No native GPS transform or previous solved wrist feeds this position.
        center = Vector3.Lerp(lowered, center, lift);
        rotation *= Quaternion.Slerp(Quaternion.Euler(32f, -8f, -10f), Quaternion.identity, lift);
        if (shoulder is { } origin)
        {
            var reach = center + rotation * wristOffset - origin;
            if (reach.magnitude > supportReach)
                center += reach.normalized * supportReach - reach;
        }
        // Work in camera-local metres throughout. Repeated world-space projection
        // and subtraction lose precision far from the island's origin.
        _mount.transform.localPosition = new Vector3(center.x / cameraScale.x,
            center.y / cameraScale.y, center.z / cameraScale.z);
        _mount.transform.localRotation = rotation;
        _mount.transform.localScale = new Vector3(1 / cameraScale.x, 1 / cameraScale.y, 1 / cameraScale.z);
        HideNativeRenderers();
        SuppressNativeScreen();
        _navigation?.Update();
    }

    private bool PrepareGrip()
    {
        if (_mount == null || ScreenCamera == null || _handPose == null)
            return Refuse("The tablet display is no longer available.");
        UpdatePresentation();
        _raised = _handPose.BeginSupport();
        if (!_raised) Status = _handPose.Status;
        return _raised;
    }

    private Vector3 FitInCamera(Quaternion rotation, Matrix4x4 inverseProjection)
    {
        if (_controller) return FitControllerInCamera(rotation, inverseProjection);
        var camera = ScreenCamera!;
        var low = FrustumPoint(inverseProjection, 0.10f, 0.08f);
        var high = FrustumPoint(inverseProjection, 0.88f, 0.82f);
        var middle = FrustumPoint(inverseProjection, 0.50f, 0.43f);
        var depth = Mathf.Max(0.34f, camera.nearClipPlane + 0.08f);
        // Solve each housing corner against the camera frustum.
        for (var corner = 0; corner < 8; corner++)
        {
            var offset = rotation * new Vector3((corner & 1) == 0 ? -_width * 0.5f - 0.016f
                : _width * 0.5f + 0.016f, ((corner & 2) == 0 ? -1 : 1) * (_height * 0.5f + 0.016f),
                (corner & 4) == 0 ? 0 : 0.028f);
            depth = Mathf.Max(depth, (low.x * offset.z - offset.x) / (middle.x - low.x));
            depth = Mathf.Max(depth, (offset.x - high.x * offset.z) / (high.x - middle.x));
            depth = Mathf.Max(depth, (low.y * offset.z - offset.y) / (middle.y - low.y));
            depth = Mathf.Max(depth, (offset.y - high.y * offset.z) / (high.y - middle.y));
        }
        return middle * depth;
    }

    private Vector3 FitControllerInCamera(Quaternion rotation, Matrix4x4 inverseProjection)
    {
        var camera = ScreenCamera!;
        var bounds = ClientDroneControllerModel.VisualBounds(_width, _height);
        var low = FrustumPoint(inverseProjection, 0.10f, 0.08f);
        var high = FrustumPoint(inverseProjection, 0.88f, 0.82f);
        var middle = FrustumPoint(inverseProjection, 0.50f, 0.43f);
        var depth = Mathf.Max(0.34f, camera.nearClipPlane + 0.08f);
        // Center the complete housing, including its deck and antennas, in the
        // available view while retaining the screen's original local origin.
        for (var corner = 0; corner < 8; corner++)
        {
            var offset = rotation * (BoundsCorner(bounds, corner) - bounds.center);
            depth = Mathf.Max(depth, camera.nearClipPlane + 0.02f - offset.z);
            depth = Mathf.Max(depth, (low.x * offset.z - offset.x) / (middle.x - low.x));
            depth = Mathf.Max(depth, (offset.x - high.x * offset.z) / (high.x - middle.x));
            depth = Mathf.Max(depth, (low.y * offset.z - offset.y) / (middle.y - low.y));
            depth = Mathf.Max(depth, (offset.y - high.y * offset.z) / (high.y - middle.y));
        }
        return middle * depth - rotation * bounds.center;
    }

    private static Vector3 BoundsCorner(Bounds bounds, int corner) => new(
        (corner & 1) == 0 ? bounds.min.x : bounds.max.x,
        (corner & 2) == 0 ? bounds.min.y : bounds.max.y,
        (corner & 4) == 0 ? bounds.min.z : bounds.max.z);

    private static Vector3 FrustumPoint(Matrix4x4 inverseProjection, float x, float y)
    {
        var view = inverseProjection.MultiplyPoint(new Vector3(x * 2 - 1, y * 2 - 1, 0));
        // Unity projection uses negative view Z; transforms use positive forward Z.
        return new Vector3(view.x / -view.z, view.y / -view.z, 1);
    }

    private void BeforeRender()
    {
        if (_phase == Phase.Idle) return;
        try
        {
            if (_phase == Phase.Closing)
            {
                if (!SameEquipmentContext()) Close(false);
                else SuppressNativePresentation();
                return;
            }
            if (!SameContext())
            {
                if (_phase == Phase.Drawing) DrawingFailure("The player's equipment changed while preparing the tablet.");
                Close(true);
                return;
            }
            if (PresentationReady && !_raised) UpdatePresentation();
            SuppressNativePresentation();
        }
        catch (Exception error)
        {
            if (_phase == Phase.Drawing) Status = "The tablet display could not be prepared. Try again after the current equipment action.";
            Close(true);
            _log.LogWarning("Tablet presentation released: " + error);
        }
    }

    private void RememberNativeScreen(Canvas canvas)
    {
        if (_nativeScreen != null) return;
        _nativeScreen = canvas;
        _nativeScreenEnabled = canvas.enabled;
        _nativeRaycaster = canvas.GetComponent<GraphicRaycaster>();
        _nativeRaycasterEnabled = _nativeRaycaster != null && _nativeRaycaster.enabled;
    }

    private void SuppressNativeScreen()
    {
        if (_nativeScreen != null) _nativeScreen.enabled = false;
        // Canvas visibility does not unregister its independent raycaster.
        // The replacement tablet is the sole pointer surface while borrowed.
        if (_nativeRaycaster != null) _nativeRaycaster.enabled = false;
    }

    private void SuppressNativePresentation()
    {
        if (!_gpsRequested) return;
        if (_phase == Phase.Closing && _heldObject != null)
        {
            HideNativeRenderers();
            SuppressNativeScreen();
            return;
        }
        if (!CurrentHeldObject(out var held) || held == null) return;
        if (_heldObject != null && !Same(_heldObject, held)) return;
        _heldObject = held;
        var gps = held.GetComponentInChildren<GpsTrackerController>(true);
        var screen = gps?._gpsTrackerSystem?._rootCanvas;
        if (screen != null && screen.transform.IsChildOf(held.transform)) RememberNativeScreen(screen);
        HideNativeRenderers();
        SuppressNativeScreen();
        _navigation?.Update();
    }

    private void HideNativeRenderers()
    {
        if (_heldObject == null)
            return;
        // Native equip may activate more renderers after the first valid screen frame.
        foreach (var renderer in _heldObject.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer == null || renderer.forceRenderingOff
                || (_mount != null && renderer.transform.IsChildOf(_mount.transform)))
                continue;
            RememberHiddenRenderer(renderer);
            renderer.forceRenderingOff = true;
        }
    }

    private void RememberHiddenRenderer(Renderer renderer)
    {
        for (var i = 0; i < _hiddenRenderers.Count; i++)
            if (Same(_hiddenRenderers[i], renderer)) return;
        _hiddenRenderers.Add(renderer);
    }

    internal bool AttachNavigation(RectTransform viewport) => _navigation?.Attach(viewport) == true;
    internal void SetFieldNavigation(ClientFieldProfile profile, Action<ClientFieldAction> changed, Action<ClientFieldPlace> share)
    {
        _fieldProfile = profile;
        _fieldChanged = changed;
        _sharePlace = share;
        _navigation?.SetFieldNavigation(profile, changed, share);
    }
    internal void SetNavigationVisible(bool visible) => _navigation?.SetVisible(visible);
    internal void UpdateNavigationInput() => _navigation?.UpdateInput();
    internal void FocusFieldDestination(Guid id)
    {
        if (_navigation != null) _navigation.FocusFieldDestination(id);
        else _pendingFieldFocus = id;
    }
    internal void ResetFieldSelection()
    {
        _pendingFieldFocus = null;
        _navigation?.ResetFieldSelection();
    }
    internal void ZoomNavigation(int delta) => _navigation?.Zoom(delta);
    internal void DetachNavigation() => _navigation?.Detach();

    internal void UpdateScreenContact(Vector2? normalized, bool down, bool held, bool up)
    {
        if (IsOpen && SameContext() && RightHandEmpty(_inventory!))
            _handPose?.UpdateContact(normalized, down, held, up);
        else
            _handPose?.CancelContact();
    }

    internal bool BeginHolster()
    {
        if (!IsOpen || !SameContext() || !PresentationValid() || _handPose?.SupportAvailable != true) return false;
        _holsterLift = Mathf.SmoothStep(0, 1, Mathf.Clamp01((Time.unscaledTime - _presentedAt) / DrawDuration));
        _holsterAt = Time.unscaledTime;
        _phase = Phase.Holstering;
        _handPose.ReleaseContact();
        return true;
    }

    internal void Close(bool restoreEquipment)
    {
        // The terminal removes its UI before this call; never expose an empty shell
        // while native equipment is being restored.
        if (_mount != null && _mount) _mount.SetActive(false);
        // The terminal repeats scene-close requests while native restoration is pending.
        var finishRestoring = _phase == Phase.Closing && _scene != GameRuntime.State.SceneEventCount;
        if ((!restoreEquipment && !finishRestoring) || _phase == Phase.Idle || !SameEquipmentContext())
        {
            ReleasePresentation();
            Reset();
            return;
        }
        _handPose?.Dispose();
        _handPose = null;
        _navigation?.Dispose();
        _navigation = null;
        if (_phase != Phase.Closing)
        {
            _phase = Phase.Closing;
            _deadline = Time.unscaledTime + 30;
            _restoreWait = 0;
            _restoreWaitFrame = Time.frameCount;
        }
        RestoreEquipment();
    }

    private void RestoreEquipment()
    {
        // Native left-hand destruction waits for an idle tag on layer 5. Finish
        // releasing our disabler before starting or continuing GPS restoration.
        _gpsPose?.Dispose();
        _gpsPose = null;
        // Inventory/menu transitions do not transfer ownership to another
        // player. Keep the captured items until native equipment is usable.
        if (!CanUse(_inventory))
        {
            _deadline = Time.unscaledTime + 30;
            _restoreWait = 0;
            _restoreWaitFrame = Time.frameCount;
            return;
        }
        var inventory = _inventory!;
        if (_restoreWaitFrame != Time.frameCount)
        {
            _restoreWaitFrame = Time.frameCount;
            _restoreWait += Mathf.Clamp(Time.unscaledDeltaTime, 0, Time.maximumDeltaTime);
        }
        var timedOut = _restoreWait >= 4 || Time.unscaledTime >= _deadline;
        if (_restoreLeft)
        {
            var left = inventory.LeftHandItem;
            var next = NextHand(inventory, EquipmentSlot.LeftHand);
            if (_gpsRequested && _gpsItem == null)
            {
                if (left != null && left._itemID == ItemIdentifiers.GPSTracker) _gpsItem = left;
                else if (next != null && next._itemID == ItemIdentifiers.GPSTracker) _gpsItem = next;
            }
            if (!_gpsRequested || timedOut || (left != null && !Same(left, _gpsItem) && !Same(left, _previous))
                || (next != null && !Same(next, _gpsItem)))
                _restoreLeft = false;
            else if (Same(left, _gpsItem) && inventory._currentEquipState == PlayerInventory.HandEquipState.Idle
                && NextHandEmpty(inventory, EquipmentSlot.LeftHand))
            {
                // Consume before callbacks; never repeat an uncertain equip.
                _restoreLeft = false;
                _holsterRequested = true;
                _restoreWait = 0;
                if (_previous != null && Same(inventory.PreviousLeftHandItem, _previous))
                {
                    inventory.EquipPreviousLeftHandItem(false);
                }
                else
                {
                    inventory.UnequipItemAtSlot(EquipmentSlot.LeftHand, false, true, false, false, false, false);
                }
            }
        }
        if (_restoreRight)
        {
            var right = inventory.RightHandItem;
            if (timedOut || !SameEquipmentContext() || (right != null && !Same(right, _previousRight))
                || !NextHandEmpty(inventory, EquipmentSlot.RightHand))
                _restoreRight = false;
            else if (right == null && !Same(inventory.PreviousRightHandItem, _previousRight))
                _restoreRight = false;
            else if (right == null && PreviousRightStashed() && !_restoreLeft && inventory._currentEquipState == PlayerInventory.HandEquipState.Idle
                && NextHandEmpty(inventory, EquipmentSlot.LeftHand)
                && (!_holsterRequested || _heldObject == null || !_heldObject.activeInHierarchy))
            {
                _restoreRight = false;
                _restoreWait = 0;
                inventory.EquipPreviousRightHandItem(false);
            }
        }
        if (!_restoreLeft && !_restoreRight && (!_holsterRequested || _heldObject == null
            || !_heldObject.activeInHierarchy || timedOut))
        {
            ReleasePresentation();
            Reset();
        }
    }

    private void ReleasePresentation()
    {
        _handPose?.Dispose();
        _handPose = null;
        // Release navigation's borrowed references before destroying the owned viewport.
        _navigation?.Dispose();
        _navigation = null;
        if (_mount != null && _mount) _mount.SetActive(false);
        _controller = false;
        foreach (var renderer in _hiddenRenderers)
            if (renderer != null && renderer.forceRenderingOff) renderer.forceRenderingOff = false;
        _hiddenRenderers.Clear();
        if (_nativeScreen != null) _nativeScreen.enabled = _nativeScreenEnabled;
        if (_nativeRaycaster != null) _nativeRaycaster.enabled = _nativeRaycasterEnabled;
        _nativeScreen = null;
        _nativeRaycaster = null;
        _poseParent = null;
        _model?.Dispose();
        _model = null;
        _controllerModel?.Dispose();
        _controllerModel = null;
        if (_mount != null && _mount) UnityEngine.Object.Destroy(_mount);
        _mount = null;
        ScreenCamera = null;
    }

    private bool PresentationValid() => _heldObject != null && _heldObject.activeInHierarchy
        && PresentationReady && _poseParent != null && ScreenCamera != null && ScreenCamera.isActiveAndEnabled
        && CurrentHeldObject(out var current) && Same(current, _heldObject);

    private bool SameContext() => _scene == GameRuntime.State.SceneEventCount
        && (_gpsPose != null ? _gpsPose.Owned : _phase == Phase.Drawing || _phase == Phase.Closing)
        && CanUse(_inventory) && SameEquipmentContext();

    private bool SameEquipmentContext()
    {
        var stats = LocalPlayer.Stats;
        if (_inventory == null || !Same(_inventory, LocalPlayer.Inventory) || !_inventory.isActiveAndEnabled
            || _player == null || !Same(_player, LocalPlayer.Transform)
            || (_itemManager is null ? _inventory._itemInstanceManager != null : !Same(_itemManager, _inventory._itemInstanceManager))
            || stats == null || stats._dyingState != PlayerStats.DyingState.Alive
            || stats._playerIsDowned || stats._respawning)
            return false;
        // Unrelated scene events can close the screen without replacing its equipment owner.
        var playerScene = _player.gameObject.scene;
        var inventoryScene = _inventory.gameObject.scene;
        return playerScene.IsValid() && playerScene.isLoaded && playerScene.handle == _playerScene
            && inventoryScene.IsValid() && inventoryScene.isLoaded && inventoryScene.handle == _inventoryScene;
    }

    private bool CurrentHeldObject(out GameObject? held) => HeldObject(ItemIdentifiers.GPSTracker, out held);

    private bool HeldObject(int itemId, out GameObject? held)
    {
        held = null;
        var props = _inventory != null ? _inventory.InventoryProps : null;
        return props != null && props._gameObjectInstances != null
            && props._gameObjectInstances.TryGetValue(itemId, out held);
    }

    private bool PreviousRightStashed() => _previousRightObject == null || !_previousRightObject.activeInHierarchy;

    private static bool HasStoredGps(PlayerInventory inventory)
    {
        var items = inventory._itemInstanceManager?._itemsMap;
        return items != null && !items.WasCollected && items.TryGetValue(ItemIdentifiers.GPSTracker, out var stored)
            && stored != null && !stored.WasCollected && stored.Count > 0;
    }

    private void DrawingFailure(string status)
    {
        Status = status;
        _log.LogWarning(status);
    }

    private static ItemInstance? NextHand(PlayerInventory inventory, EquipmentSlot slot)
    {
        var next = inventory._equipmentSlotsNext;
        return next != null && !next.WasCollected && next.Length > (int)slot ? next[(int)slot] : null;
    }

    private static bool NextHandEmpty(PlayerInventory inventory, EquipmentSlot slot)
    {
        var next = inventory._equipmentSlotsNext;
        return next != null && !next.WasCollected && next.Length > (int)slot && next[(int)slot] == null;
    }

    // Only EquipSequence returns the state to Idle. A new game starts in Equipping and opening the
    // inventory turns that into StashCancelRightEquip, so until the first equip, empty hands with
    // nothing mid-action or queued are settled whatever the state says.
    internal static bool HandsIdle(PlayerInventory inventory) =>
        !LocalPlayer.IsInMidAction
        && NextHandEmpty(inventory, EquipmentSlot.RightHand) && NextHandEmpty(inventory, EquipmentSlot.LeftHand)
        && (inventory._currentEquipState == PlayerInventory.HandEquipState.Idle
            || inventory.RightHandItem == null && inventory.LeftHandItem == null);

    private static bool RightHandEmpty(PlayerInventory inventory) => inventory.RightHandItem == null
        && NextHandEmpty(inventory, EquipmentSlot.RightHand);

    private static bool CanUse(PlayerInventory? inventory)
    {
        var stats = LocalPlayer.Stats;
        return inventory != null && inventory.isActiveAndEnabled && LocalPlayer.IsInWorld
            && inventory._currentView == PlayerInventory.PlayerViews.World
            && !inventory._IsInQuickSelect_k__BackingField && !inventory._IsThrowing_k__BackingField
            && stats != null && stats._dyingState == PlayerStats.DyingState.Alive
            && !stats._playerIsDowned && !stats._respawning;
    }

    private static bool PositiveScale(Vector3 scale) => float.IsFinite(scale.x) && scale.x > 0.0001f
        && float.IsFinite(scale.y) && scale.y > 0.0001f && float.IsFinite(scale.z) && scale.z > 0.0001f;

    private static bool Same(Il2CppObjectBase? first, Il2CppObjectBase? second) => first is not null
        && second is not null && !first.WasCollected && !second.WasCollected && first.Pointer == second.Pointer;

    private bool Refuse(string status) { Status = status; return false; }

    private void Reset()
    {
        _pendingFieldFocus = null;
        _gpsPose?.Dispose();
        _gpsPose = null;
        if (_beforeRender != null) Canvas.remove_preWillRenderCanvases(_beforeRender);
        _beforeRender = null;
        _raised = false;
        _controller = false;
        _phase = Phase.Idle;
        _inventory = null;
        _itemManager = null;
        _player = null;
        _scene = 0;
        _playerScene = 0;
        _inventoryScene = 0;
        _previous = null;
        _previousRight = null;
        _previousRightObject = null;
        _gpsItem = null;
        _heldObject = null;
        _gpsRequested = false;
        _restoreLeft = false;
        _restoreRight = false;
        _holsterRequested = false;
    }

    public void Dispose()
    {
        Close(true);
        // An unfinished native equip remains native-owned when the client unloads.
        ReleasePresentation();
        Reset();
        BepInEx.Logging.Logger.Sources.Remove(_log);
        _log.Dispose();
    }
}
