// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Modstoke.Loader;
using Modstoke.Sons;
using SonsSdk;
using TheForest.Utils;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftService
{
    private bool _entryPrepared;
    private long _nextEntryAttempt;
    private long _chairFeedbackUntil;
    private long _nextCabinScan;
    private bool _unseatedRemoteCabin;
    private GameObject? _interactionRoot;
    private TextMeshProUGUI? _interactionText;
    private ClientTabletTypography? _interactionTypography;
    private string _interactionMessage = "";

    internal bool CanReachChair(int seat) => _ready && ServiceRunning && _body?.Ready == true && LocalAlive()
        && LocalCrewAccess
        && seat is >= 0 and <= 2
        && (Stationary(_body.State) || OwnWalkingChair(seat) && _body.State.Travel == SpacecraftTravel.None)
        && _body.CanReachSeat(LocalPlayer.Transform.position, seat, LocalPlayer.Transform);

    private bool OwnWalkingChair(int seat) => seat is >= 0 and <= 2
        && _rider is { Walking: true, Faulted: false } && _rider.Seat == seat
        && (IsClient ? _clientWalking && _clientSeat == seat : _local is { Walking: true } local && local.Seat == seat);

    private ActionCode ReachablePassenger() => OwnWalkingChair(1)
        || CanReachChair(1) && (IsClient ? _actorIds[1] == 0 : _seats[1] == null) ? ActionCode.Passenger
        : ActionCode.PassengerRear;

    private void ConfirmEntryPreparation(bool prepared, Vector3 hostAnchor)
    {
        _entryPrepared = prepared && Prepared && _expedition.ReturnPosition is { } anchor
            && (anchor - hostAnchor).sqrMagnitude <= .75f * .75f;
        if (_entryPrepared) return;
        // A rejected reservation must not teleport a player who is simply
        // walking through the cabin. Retain the anchor for a later retry.
        _boardingCommand = null;
        if (prepared) _feedback = "The host's return location changed. Please try the chair again.";
    }

    private bool AtIslandEntry(Vector3 position) => _body?.Ready == true
        && _body.State.Region == SpacecraftRegion.Island && !_body.ContainsCabin(position)
        && (position - _body.Root.transform.TransformPoint(SpacecraftLayout.Entry)).sqrMagnitude <= 36
        && TryGround(position + Vector3.up * .25f, 3, out var ground)
        && ground.point.y < 2000 && DryGround(ground.point);

    private bool AtRampApproach(Vector3 position)
    {
        if (_body?.Ready != true || !Finite(position) || _body.ContainsCabin(position)
            || (position - _body.Root.transform.TransformPoint(SpacecraftLayout.Entry)).sqrMagnitude > 36) return false;
        if (_body.State.Region == SpacecraftRegion.Island) return AtIslandEntry(position);
        return _body.State.Region is SpacecraftRegion.TestWorld or SpacecraftRegion.Moon
            && _destination.TrySurfacePoint(position, out var surface)
            && position.y - surface.y >= -.35f && position.y - surface.y <= 2.3f;
    }

    private void TryPrepareEntry()
    {
        if (!_ready || !LocalCrewAccess || _body?.Ready != true || !LocalAlive() || _entryPrepared && Prepared
            || _pending != null || Now < _nextEntryAttempt || !Stationary(_body.State)) return;
        _nextEntryAttempt = Now + 1500;
        var position = LocalPlayer.Transform.position;
        if (_body.State.Region != SpacecraftRegion.Island
            || !(_body.ContainsCabin(position) || AtRampApproach(position)))
        { _boardingCommand = null; _feedback = "Approach a spacecraft chair to board."; return; }
        if (!TryIslandReturnAnchor(out var anchor, out var rotation))
        { _boardingCommand = null; _feedback = "The ground behind the ramp is obstructed. Clear an exit before flying."; return; }
        if (!_destination.Ensure() || !_expedition.Ready || !_expedition.Begin(anchor, rotation))
        { _boardingCommand = null; _feedback = "Unable to prepare this seat. " + _expedition.Status; return; }
        Dispatch(ActionCode.Prepare);
    }

    private void UpdateInteractions() => ShowInteraction(InteractionMessage());

    private string InteractionMessage()
    {
        var walking = OwnWalkingChair(_rider?.Seat ?? -1);
        if (!_ready || _body?.Ready != true || !LocalAlive() || UsingSpacecraft && !walking || Plugin.Aircraft.UsingAircraft) return "";
        var position = LocalPlayer.Transform.position;
        var distance = (position - _body.Root.transform.TransformPoint(SpacecraftLayout.Entry)).sqrMagnitude;
        if (!_body.ContainsCabin(position) && distance > 81)
        {
            if (!walking && _pending == null && _expedition.OnSafeIslandGround && _body.State.Region == SpacecraftRegion.Island)
            { _expedition.End(); _entryPrepared = false; }
            return "";
        }
        // Cabin walking retains the spacecraft input lease and its chair prompts.
        if (!ClientSpacecraftRider.WorldInputAvailable || InputBindings.HasFocus && !walking || Cursor.visible) return "";
        if (BoardingPending)
        {
            return _cancelBoardingRequested ? "Cancelling seat…" : "Preparing seat…";
        }
        if (!_body.State.DoorOpen && Stationary(_body.State) && AtRampApproach(position))
        {
            if (!LocalCrewAccess) return "Crew lock active. Ask the host to open the ramp.";
            var pilotPresent = IsClient ? _actorIds[0] != 0 : _seats[0] != null;
            if (!_body.State.Powered && !pilotPresent && Input.GetKeyDown(KeyCode.E)) Command("open-ramp");
            return _body.State.Powered ? "Ask the pilot to switch power off and open the ramp."
                : pilotPresent ? "Ask the seated pilot to open the rear ramp." : "E · Open rear ramp";
        }
        var camera = LocalPlayer.MainCam;
        if (camera == null || !camera || !camera.isActiveAndEnabled) return "";
        if (_body.TryFindSeat(new Ray(camera.transform.position, camera.transform.forward), position, out var seat, LocalPlayer.Transform))
        {
            if (!LocalCrewAccess) return "Crew access required. Use the card reader to request it.";
            var ownChair = OwnWalkingChair(seat);
            var occupied = IsClient ? _actorIds[seat] != 0 : _seats[seat] != null;
            var reachable = _body.CanReachSeat(position, seat, out var reachReason, LocalPlayer.Transform);
            if (occupied && !ownChair) return "Seat occupied";
            if (walking && !ownChair) return "Return to your reserved chair.";
            if (ownChair && _body.State.Travel != SpacecraftTravel.None) return "Wait for travel to complete before returning to your chair.";
            if (Input.GetKeyDown(KeyCode.E) && CanReachChair(seat))
            {
                _chairFeedbackUntil = Now + 7000;
                Command(seat == 0 ? "board-pilot" : seat == 1 ? "board-passenger-front" : "board-passenger-rear");
            }
            return Now < _chairFeedbackUntil && _feedback.Length != 0 ? _feedback
                : !ownChair && !Stationary(_body.State) ? StationaryFailure(_body.State)
                : !reachable ? reachReason
                : ownChair ? "E · Return to your chair"
                : seat == 0 ? "E · Sit in pilot chair" : seat == 1 ? "E · Sit in copilot chair" : "E · Sit in passenger chair";
        }
        return "";
    }

    private bool HasUnseatedCabinPlayer(bool force = false, bool allowCabinWalking = false)
    {
        if (_body == null) return false;
        if (allowCabinWalking || _body.State.Region == SpacecraftRegion.Space)
            foreach (var peer in _seats)
                if (peer?.Walking == true && (!allowCabinWalking || !CabinWalkerCarried(peer))) return true;
        if (LocalAlive() && _body.ContainsCabin(LocalPlayer.Transform.position)
            && (_local?.Seat < 0 || _local == null || !_local.Attached)) return true;
        if (!BoltNetwork.isRunning || !BoltNetwork.isServer) return false;
        if (!force && Now < _nextCabinScan) return _unseatedRemoteCabin;
        _nextCabinScan = Now + 100;
        _unseatedRemoteCabin = false;
        foreach (var session in ServerSessions.Sessions)
        {
            if (!session.IsCurrent) continue;
            PlayerHandle? binding;
            if (_peers.TryGetValue(session.SessionId, out var cabinPeer))
                binding = cabinPeer.Binding;
            else
                ServerWorld.TryCapturePlayerBinding(session, out binding, out _);
            if (binding == null || !ServerWorld.TryReadPlayerBinding(binding, out var actor, out _))
                return _unseatedRemoteCabin = true;
            var position = new Vector3(actor.Position.X, actor.Position.Y, actor.Position.Z);
            if (!Finite(position)) return _unseatedRemoteCabin = true;
            if (!_body.ContainsCabin(position)) continue;
            if (!ServerWorld.TryReadPlayerLife(binding, out var life, out _))
                return _unseatedRemoteCabin = true;
            if (life.LifeState != PlayerLifeState.Alive) continue;
            var seated = false;
            foreach (var peer in _seats)
                if (peer != null && peer.Session?.SessionId == session.SessionId && peer.Attached && PeerCurrent(peer))
                { seated = true; break; }
            if (!seated) return _unseatedRemoteCabin = true;
        }
        return false;
    }

    private void RetireUnusedEntryProtection()
    {
        if (_body == null || _body.State.Region != SpacecraftRegion.Island) return;
        void Retire(Peer peer)
        {
            if (!peer.EntryPrepared || peer.Prepared || peer.Seat >= 0 || !TryActor(peer, out var point, out _, out _)
                || _body.ContainsCabin(point) || (point - _body.Root.transform.TransformPoint(SpacecraftLayout.Entry)).sqrMagnitude <= 81
                || !TryGround(point + Vector3.up * .25f, 3, out _)) return;
            peer.EntryPrepared = false;
            _crew.Remove(peer);
            RetireMovementEvidence(peer);
        }
        if (_local != null) Retire(_local);
        foreach (var peer in _peers.Values) Retire(peer);
    }

    private void ShowInteraction(string message)
    {
        if (_interactionMessage == message && (_interactionRoot || message.Length == 0)) return;
        _interactionMessage = message;
        if (message.Length == 0) { if (_interactionRoot != null && _interactionRoot) _interactionRoot.SetActive(false); return; }
        if (!_interactionRoot)
        {
            _interactionTypography = new ClientTabletTypography();
            _interactionRoot = new GameObject("Spacecraft chair prompt");
            var canvas = _interactionRoot.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 110;
            var scaler = _interactionRoot.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 720);
            var label = new GameObject("Interaction");
            var rect = label.AddComponent<RectTransform>(); rect.SetParent(_interactionRoot.transform, false);
            rect.anchorMin = rect.anchorMax = new Vector2(.5f, .22f);
            rect.sizeDelta = new Vector2(880, 70);
            _interactionText = label.AddComponent<TextMeshProUGUI>();
            _interactionTypography.Apply(_interactionText, TabletTextRole.Control);
            _interactionText.fontSize = 22; _interactionText.alignment = TextAlignmentOptions.Center;
            _interactionText.color = Color.white; _interactionText.raycastTarget = false;
        }
        _interactionText!.text = message;
        if (!_interactionRoot!.activeSelf) _interactionRoot.SetActive(true);
    }

    private void ResetInteractions()
    {
        _entryPrepared = false; _nextEntryAttempt = _chairFeedbackUntil = 0;
        _nextCabinScan = 0; _unseatedRemoteCabin = false;
        if (_interactionRoot != null && _interactionRoot) { _interactionRoot.SetActive(false); UnityEngine.Object.Destroy(_interactionRoot); }
        _interactionRoot = null; _interactionText = null;
        _interactionMessage = "";
        _interactionTypography?.Dispose(); _interactionTypography = null;
    }
}
