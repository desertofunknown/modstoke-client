// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using GameState = SonsSdk.GameState;
using Modstoke.Loader;
using Modstoke.Sons;
using Sons.Multiplayer.Client;
using SonsSdk;
using TheForest.Items.Inventory;
using TheForest.Utils;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Modstoke.Client;

internal sealed partial class ClientAircraftService
{
    private GameObject? _interactionRoot;
    private TextMeshProUGUI? _interactionText;
    private ClientTabletTypography? _interactionTypography;
    private long _interactionFeedbackUntil;
    private int _interactionFeedbackSeat = -1;
    private string _interactionMessage = "";
    private long _nextCabinScan;
    private bool _unseatedCabin;

    private void UpdateInteractions() => ShowInteraction(InteractionMessage());

    private string InteractionMessage()
    {
        if (!_ready || _body == null || !_body.Root || !LocalAlive() || UsingAircraft
            || Plugin.Spacecraft.UsingSpacecraft || !Application.isFocused
            || !GameRuntime.State.IsInWorld || GameRuntime.State.PauseMenuOpen
            || GameState.IsInConsole || InputBindings.HasFocus || Cursor.visible
            || LocalPlayer.Inventory == null || !LocalPlayer.Inventory
            || LocalPlayer.Inventory.CurrentView != PlayerInventory.PlayerViews.World) return "";
        var camera = LocalPlayer.MainCam;
        if (camera == null || !camera) return "";
        var actor = LocalPlayer.Transform;
        var position = actor.position;
        if ((position - _body.State.Position).sqrMagnitude > 36) return "";
        foreach (var surface in UiState.Snapshot().Values)
            if (surface.Visible) return "";

        var gaze = new Ray(camera.transform.position, camera.transform.forward);
        var flight = _body.State;
        var blocked = Plugin.Spacecraft.LocalRegion != SpacecraftRegion.Island ? "Return to the island before using the plane."
            : !flight.Grounded || flight.Velocity.sqrMagnitude >= 4 ? "The plane must be on the ground and below 2 m/s."
            : "";
        if (_body.TryFindSeat(gaze, position, actor, out var seat))
        {
            if (SeatOccupied(seat)) return "That chair is occupied.";
            if (blocked.Length != 0) return blocked;
            if (flight.Health <= .08f) return "The airframe is disabled. Service it before boarding.";
            if (!_body.CanReachSeat(position, seat, actor, SeatReachOrigin(position, actor)))
                return "Move beside that chair with a clear path.";
            return ChairCommandMessage(seat, seat switch
            {
                0 => "sit-pilot", 1 => "sit-passenger-front", _ => "sit-passenger-rear"
            }, seat == 0 ? "E - Sit in pilot chair" : seat == 1 ? "E - Sit in front passenger chair" : "E - Sit in rear passenger chair");
        }
        if (!_body.TryFindDoor(gaze, position, actor, out var side)) return "";
        if (blocked.Length != 0) return blocked;
        var open = (_doorMask & (1 << side)) != 0;
        return ChairCommandMessage(3 + side + (open ? 2 : 0), side == 0
            ? open ? "close-port-door" : "open-port-door"
            : open ? "close-starboard-door" : "open-starboard-door",
            open ? "E - Close cabin door" : "E - Open cabin door");
    }

    private bool SeatOccupied(int seat) => IsClient ? _actorIds[seat] != 0 : _seats[seat] != null;

    private string ChairCommandMessage(int target, string command, string prompt)
    {
        if (_pending != null) return "Waiting for the host's reply.";
        if (Input.GetKeyDown(KeyCode.E))
        {
            _interactionFeedbackSeat = target;
            _interactionFeedbackUntil = Now + 7000;
            return Command(command);
        }
        return _interactionFeedbackSeat == target && Now < _interactionFeedbackUntil && _feedback.Length != 0 ? _feedback : prompt;
    }

    private static Transform? ActorTransform(Peer peer)
    {
        if (peer.Session == null) return LocalPlayer.Transform;
        if (peer.Binding == null || peer.Binding.NetworkId == 0) return null;
        var entity = BoltNetwork.FindEntity(new Bolt.NetworkId(peer.Binding.NetworkId));
        return entity != null && !entity.WasCollected && entity && entity.isAttached ? entity.transform : null;
    }

    private static Vector3? SeatReachOrigin(Vector3 actorPosition, Transform? actor)
    {
        if (actor == null || actor.WasCollected || !actor || !Finite(actorPosition)) return null;
        CapsuleCollider? capsule;
        var local = LocalPlayer.Transform;
        if (local != null && !local.WasCollected && local && local.Pointer == actor.Pointer)
        {
            var character = LocalPlayer.FpCharacter;
            var body = LocalPlayer.Rigidbody;
            if (character == null || character.WasCollected || !character || !character.isActiveAndEnabled
                || character.IsCrouchActive || character._shouldCrouch
                || body == null || body.WasCollected || !body || body.transform.Pointer != actor.Pointer
                || !character.transform.IsChildOf(actor) || character._rigidbody?.Pointer != body.Pointer) return null;
            capsule = character._primaryCollider;
            if (capsule == null || capsule.WasCollected || !capsule
                || capsule.attachedRigidbody?.Pointer != body.Pointer
                || capsule.height != character._defaultPrimaryColliderHeight
                || !capsule.center.Equals(character._defaultPrimaryColliderCenter)) return null;
        }
        else
        {
            var entity = actor.GetComponent<BoltEntity>();
            if (entity == null || entity.WasCollected || !entity || !entity.isAttached
                || entity.prefabId != BoltPrefabs.PlayerNetwork || entity.transform.Pointer != actor.Pointer) return null;
            var state = entity.GetState<IPlayerState>();
            // Remote blocking capsules need not shrink with the crouch animation.
            if (state == null || state.WasCollected || state.crouch != 0) return null;
            var setups = entity.GetComponentsInChildren<CoopPlayerRemoteSetup>(true);
            if (setups == null || setups.WasCollected || setups.Length > 8) return null;
            CoopPlayerRemoteSetup? setup = null;
            foreach (var candidate in setups)
            {
                if (candidate == null || candidate.WasCollected || !candidate || candidate._entity?.Pointer != entity.Pointer) continue;
                if (setup != null) return null;
                setup = candidate;
            }
            var animator = setup?._playerAnimator;
            if (animator == null || animator.WasCollected || !animator || !animator.isActiveAndEnabled
                || !animator.transform.IsChildOf(actor)) return null;
            var control = animator.GetComponent<PlayerAnimatorControl>();
            if (control == null || control.WasCollected || !control || !control.isActiveAndEnabled
                || !control._isNetworkPlayer || control._doingCrouch || control._doingJumpCrouch) return null;
            capsule = control.playerCollider;
        }
        if (capsule == null || capsule.WasCollected || !capsule || !capsule.enabled
            || !capsule.gameObject.activeInHierarchy || capsule.isTrigger || capsule.direction != 1) return null;
        var frame = capsule.transform;
        var attached = capsule.attachedRigidbody;
        if (!frame.IsChildOf(actor) || attached == null || attached.WasCollected || !attached
            || attached.transform.Pointer != actor.Pointer) return null;
        var scale = frame.lossyScale;
        var actorScale = actor.lossyScale;
        var up = frame.up;
        if (!Finite(scale) || !Finite(actorScale) || !Finite(up) || up.y < .9999f
            || MathF.Abs(scale.x - 1) >= .0001f || MathF.Abs(scale.y - 1) >= .0001f || MathF.Abs(scale.z - 1) >= .0001f
            || MathF.Abs(actorScale.x - 1) >= .0001f || MathF.Abs(actorScale.y - 1) >= .0001f || MathF.Abs(actorScale.z - 1) >= .0001f)
            return null;
        var radius = capsule.radius;
        var height = capsule.height;
        var center = capsule.center;
        if (!float.IsFinite(radius) || radius <= 0 || !float.IsFinite(height)
            || height < 2 * radius || !Finite(center)) return null;
        var upperCap = frame.TransformPoint(center + Vector3.up * (height * .5f - radius));
        var offset = upperCap.y - actor.position.y;
        if (!Finite(upperCap) || !float.IsFinite(offset) || offset <= .85f) return null;
        // PlayerNetwork's standing capsule upper-cap centre is 1.513531 m above its root.
        return actorPosition + Vector3.up * MathF.Min(offset, 1.513531f);
    }

    private bool HasUnseatedCabinPlayer(bool force = false)
    {
        if (_body == null) return false;
        if (LocalAlive() && !UsingAircraft && _body.ContainsCabin(LocalPlayer.Transform.position)) return true;
        if (!BoltNetwork.isRunning || !BoltNetwork.isServer) return false;
        if (!force && Now < _nextCabinScan) return _unseatedCabin;
        _nextCabinScan = Now + 100;
        _unseatedCabin = false;
        foreach (var session in ServerSessions.Sessions)
        {
            if (!session.IsCurrent) continue;
            PlayerHandle? binding = null;
            if (_peers.TryGetValue(session.SessionId, out var cabinPeer))
                binding = cabinPeer.Binding;
            // The peer loop drops a downed or dead peer's binding between retries; a fresh capture still reads its life.
            if (binding == null)
                ServerWorld.TryCapturePlayerBinding(session, out binding, out _);
            if (binding == null || !ServerWorld.TryReadPlayerBinding(binding, out var actor, out _))
                return _unseatedCabin = true;
            var point = new Vector3(actor.Position.X, actor.Position.Y, actor.Position.Z);
            if (!_body.ContainsCabin(point)) continue;
            if (!ServerWorld.TryReadPlayerLife(binding, out var life, out _)) return _unseatedCabin = true;
            if (life.LifeState != PlayerLifeState.Alive) continue;
            var seated = false;
            foreach (var peer in _seats)
                if (peer != null && peer.Session?.SessionId == session.SessionId && PeerCurrent(peer)) { seated = true; break; }
            if (!seated) return _unseatedCabin = true;
        }
        return false;
    }

    private void ShowInteraction(string message)
    {
        if (message == _interactionMessage && (message.Length == 0 || _interactionRoot != null && _interactionRoot)) return;
        _interactionMessage = message;
        if (message.Length == 0)
        {
            if (_interactionRoot != null && _interactionRoot) _interactionRoot.SetActive(false);
            return;
        }
        if (!_interactionRoot)
        {
            _interactionTypography = new ClientTabletTypography();
            _interactionRoot = new GameObject("Aircraft boarding prompt");
            var canvas = _interactionRoot.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 110;
            var scaler = _interactionRoot.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 720);
            var label = new GameObject("Interaction");
            var rect = label.AddComponent<RectTransform>();
            rect.SetParent(_interactionRoot.transform, false);
            rect.anchorMin = new Vector2(.1f, .22f);
            rect.anchorMax = new Vector2(.9f, .22f);
            rect.sizeDelta = new Vector2(0, 90);
            _interactionText = label.AddComponent<TextMeshProUGUI>();
            _interactionTypography.Apply(_interactionText, TabletTextRole.Control);
            _interactionText.fontSize = 22;
            _interactionText.alignment = TextAlignmentOptions.Center;
            _interactionText.color = Color.white;
            _interactionText.raycastTarget = false;
        }
        _interactionText!.SetText(message);
        var root = _interactionRoot!;
        if (!root.activeSelf) root.SetActive(true);
    }

    private void ResetInteractions()
    {
        _interactionFeedbackUntil = 0;
        _interactionFeedbackSeat = -1;
        if (_interactionRoot != null && _interactionRoot)
        {
            _interactionRoot.SetActive(false);
            UnityEngine.Object.Destroy(_interactionRoot);
        }
        _interactionRoot = null;
        _interactionText = null;
        _interactionMessage = "";
        _nextCabinScan = 0;
        _unseatedCabin = false;
        _interactionTypography?.Dispose();
        _interactionTypography = null;
    }
}
