// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Modstoke.Loader;
using Modstoke.Sons;
using TheForest.Utils;
using TheForest.Items.Inventory;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientTerminal
{
    private readonly ClientWristWatch _wristWatch;
    private readonly ClientWatchHud _watchHud = new();
    private readonly ClientTabletGames _games = new();
    private string? _pendingGameScores;
    private ClientWatchKey? _appliedWatchKey;
    private bool _tabletAfterWatch;
    private bool _controllerAfterWatch;
    private bool _watchMessageShortcut;
    private bool _watchGpsShortcut;
    private ClientWristWatchSnapshot _watchSnapshot = new();
    private ClientCommand? _profileRequest;
    private Guid? _profileSession;
    private bool _profileAttempted;
    private bool _profileRestored;
    private float _nextFieldSample;
    private float _nextTrailSample;
    private Guid? _noteId;
    private string _noteTitle = "";
    private string _noteBody = "";
    private Guid? _notePlaceId;
    private Guid? _noteBrowsePlaceId;
    private readonly Dictionary<Guid, ClientFieldNote> _noteDrafts = new();
    private string _draftKey = "expedition|";
    private string? _sharedDraft;
    private string? _sharedDraftKey;
    private ClientFieldProfile? _navigationField;
    private readonly Dictionary<ClientTabletMessage, Guid> _sharedPlaceIds = new();
    private long _navigationNoticeId;
    private PlayerInventory? _inventoryTabletOwner;
    private float _inventoryTabletDeadline;
    private float _inventoryTabletWait;
    private int _inventoryTabletWaitFrame;
    private int _inventoryTabletRevision;
    private bool _inventoryControllerRequested;

    private bool AcceptInventoryTablet() => AcceptInventoryPresentation(false);

    private bool HasPresentationDevice(bool controller) => controller ? _deviceInventory.HasController : _deviceInventory.HasTablet;

    private bool AcceptInventoryPresentation(bool controller)
    {
        if (!_registered || _failed || !HasPresentationDevice(controller) || !_canEquipTablet()
            || _tablet.IsActive || !_deviceInventory.HasLiveInventory || GameRuntime.State.PauseMenuOpen) return false;
        ClearInventoryPresentation();
        _inventoryTabletOwner = LocalPlayer.Inventory;
        _inventoryTabletRevision = _deviceInventory.SessionRevision;
        _inventoryTabletDeadline = Time.unscaledTime + 30;
        _inventoryTabletWait = 0;
        _inventoryTabletWaitFrame = Time.frameCount;
        _inventoryControllerRequested = controller;
        return true;
    }

    private void QueueTabletShortcut()
    {
        if (SonsSdk.GameState.IsInConsole || GameRuntime.State.PauseMenuOpen || LoaderMenuVisible())
        { Notice("Close the pause or Mods menu before taking out the tablet."); return; }
        if (IsEditingText())
        { Notice("Finish typing and close chat before taking out the tablet."); return; }
        if (!_canEquipTablet())
        { Notice("The tablet is unavailable while managed combat owns your equipment."); return; }
        if (!_deviceInventory.HasLiveInventory || !_deviceInventory.HasTablet)
        {
            Notice(_deviceInventory.Status.Length > 0 ? _deviceInventory.Status : "The field tablet is not in your inventory.");
            return;
        }
        var inventory = LocalPlayer.Inventory;
        if (inventory._currentView != PlayerInventory.PlayerViews.World
            && inventory._currentView != PlayerInventory.PlayerViews.Inventory)
        { Notice("Finish the current activity before taking out the tablet."); return; }
        if (!AcceptInventoryTablet()) return;
        if (inventory._currentView == PlayerInventory.PlayerViews.Inventory) inventory.Close();
    }

    private bool ConsumeInventoryTablet(out bool controller)
    {
        controller = false;
        var owner = _inventoryTabletOwner;
        if (owner == null) return false;
        var current = LocalPlayer.Inventory;
        if (!_deviceInventory.HasLiveInventory || current == null || current.Pointer != owner.Pointer
            || !HasPresentationDevice(_inventoryControllerRequested) || _inventoryTabletRevision != _deviceInventory.SessionRevision
            || SonsSdk.GameState.IsInConsole || GameRuntime.State.PauseMenuOpen || LoaderMenuVisible() || IsEditingText())
        {
            ClearInventoryPresentation();
            return false;
        }
        if (_inventoryTabletWaitFrame != Time.frameCount)
        {
            _inventoryTabletWaitFrame = Time.frameCount;
            _inventoryTabletWait += Mathf.Clamp(Time.unscaledDeltaTime, 0, Time.maximumDeltaTime);
        }
        if (_inventoryTabletWait >= 5 || Time.unscaledTime > _inventoryTabletDeadline)
        {
            var wasController = _inventoryControllerRequested;
            ClearInventoryPresentation();
            Notice(wasController ? "The controller is waiting for your current equipment action to finish. Select it again when ready."
                : "The tablet is waiting for your current equipment action to finish. Press F4 again when ready.");
            return false;
        }
        if (!GameRuntime.State.IsInWorld || owner._currentView != PlayerInventory.PlayerViews.World
            || !ClientHeldTablet.HandsIdle(owner)) return false;
        _inventoryTabletOwner = null;
        controller = _inventoryControllerRequested;
        _inventoryControllerRequested = false;
        return true;
    }

    private void ClearInventoryPresentation()
    {
        _inventoryTabletOwner = null;
        _inventoryControllerRequested = false;
        _inventoryDroneRequested = false;
        _tabletAfterWatch = _controllerAfterWatch = _watchMessageShortcut = _watchGpsShortcut = false;
    }

    private bool BeginTablet(bool controller = false)
    {
        if (!HasPresentationDevice(controller)) return false;
        if (!AcquireDeviceInput()) return false;
        _wristWatch.EndInspection(immediate: true);
        return _tablet.Begin(controller);
    }

    private void ResetFieldSession()
    {
        ClearInventoryPresentation();
        _profileRequest = null;
        _profileSession = null;
        _profileAttempted = _profileRestored = false;
        _noteId = null;
        _noteTitle = _noteBody = "";
        _notePlaceId = null;
        _noteBrowsePlaceId = null;
        _noteDrafts.Clear();
        _draftKey = "expedition|";
        _sharedDraft = null;
        _sharedDraftKey = null;
        _nextFieldSample = _nextTrailSample = 0;
        _navigationField = null;
        _sharedPlaceIds.Clear();
        _navigationNoticeId = 0;
        _tablet.ResetFieldSelection();
        _tablet.SetFieldNavigation(new(), PostField, SharePlace);
        _games.LoadScores(null);
        _pendingGameScores = null;
        _watchSnapshot = new();
        _wristWatch.EndInspection();
    }

    private void UpdateField()
    {
        UpdateProfileLink();
        var os = _os.Snapshot;
        var current = os.Generation == _osGeneration;
        if (current && os.ProfileLoaded && !_profileRestored)
        {
            _profileRestored = true;
            RestoreMessageDraft();
            _games.LoadScores(os.Field.Drafts.TryGetValue(ClientTabletGames.ScoreDraftKey, out var savedScores) ? savedScores : null, resetRun: false);
        }
        if (current && !ReferenceEquals(_navigationField, os.Field))
        {
            _navigationField = os.Field;
            ReconcileFieldNotePlaces(os.Field);
            _tablet.SetFieldNavigation(os.Field, PostField, SharePlace);
        }
        var navigationNotice = current ? os.Notifications.FirstOrDefault(item => item.Key == "navigation") : null;
        if (navigationNotice != null && navigationNotice.Id > _navigationNoticeId)
        {
            _navigationNoticeId = navigationNotice.Id;
            Feedback(navigationNotice.Title + ". " + navigationNotice.Detail);
            _nextRefresh = 0;
        }
        var canUse = Application.isFocused && GameRuntime.State.IsInWorld && !GameRuntime.State.PauseMenuOpen
            && !SonsSdk.GameState.IsInConsole
            && !LoaderMenuVisible() && !IsEditingText() && _canEquipTablet();
        if (_preferences is { } preferences)
        {
            var preference = preferences.Snapshot;
            if (_appliedWatchKey != preference.WatchKey)
            {
                _appliedWatchKey = preference.WatchKey;
                _wristWatch.SetKey(preference.WatchKey switch
                { ClientWatchKey.F9 => KeyCode.F9, ClientWatchKey.F11 => KeyCode.F11, ClientWatchKey.None => KeyCode.None, _ => KeyCode.F3 });
            }
            _wristWatch.ReducedMotion = preference.ReducedMotion;
        }
        var gamesVisible = canUse && _deviceInput.Captured && CanUseDesktop()
            && !_contentObscured && _app == ClientTerminalApp.Games;
        UpdateArcadeInput(gamesVisible);
        _games.Update(Time.unscaledDeltaTime, gamesVisible);
        if (_games.TryTakeScores(out var scores)) _pendingGameScores = scores;
        if (_pendingGameScores != null && _os.FieldAction(new("draft", _pendingGameScores, ClientTabletGames.ScoreDraftKey)))
            _pendingGameScores = null;
        if (_open && _app == ClientTerminalApp.Games) _view?.SetGamesState(_games.Snapshot);
        if (Time.unscaledTime >= _nextFieldSample)
        {
            _nextFieldSample = Time.unscaledTime + .2f;
            var vitals = GameRuntime.State.IsInWorld ? ClientWristWatch.ReadVitals() : new ClientWristWatchVitals
            {
                Condition = new ClientHealthObservation { Status = LocalPlayerReadStatus.WorldUnavailable }
            };
            float? heading = null, bearing = null, distance = null;
            string destinationName = "";
            var destination = current ? os.Field.Places.FirstOrDefault(place => place.Id == os.Field.DestinationId) : null;
            if (TryFieldPosition(out var position, out var forward))
            {
                heading = Mathf.Repeat(Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg, 360f);
                if (destination != null)
                {
                    var dx = destination.Position.X - position.x;
                    var dz = destination.Position.Z - position.z;
                    bearing = Mathf.Repeat(Mathf.Atan2(dx, dz) * Mathf.Rad2Deg, 360f);
                    distance = MathF.Sqrt(dx * dx + dz * dz);
                    destinationName = destination.Name;
                }
                if (current && os.Field.RecordTrail && Time.unscaledTime >= _nextTrailSample)
                {
                    _nextTrailSample = Time.unscaledTime + 2f;
                    PostField(new("trail-point", Point: new(position.x, position.y, position.z)));
                }
            }
            _watchSnapshot = new ClientWristWatchSnapshot
            {
                Os = current ? os.WristWatch : new ClientWatchOsSnapshot(),
                Clock = ReadIslandClock(),
                Vitals = vitals, HeadingDegrees = heading, WaypointBearingDegrees = bearing,
                WaypointDistanceMetres = distance, WaypointLabel = destinationName,
                Notifications = current ? Array.AsReadOnly(os.Notifications.TakeLast(8).Reverse()
                    .Select(item => item.Title + " · " + item.Detail).ToArray()) : Array.Empty<string>(),
                TimerState = current ? os.Watch.State.ToString() : "Idle",
                TimerRemainingSeconds = current && os.Watch.State != ClientTabletWatchState.Idle ? os.Watch.RemainingSeconds : null,
                PrivateContentAvailable = current,
                Mission = current ? os.Field.Mission : ""
            };
        }
        // Navigation is sampled with the world; watch controls follow the OS
        // reply immediately instead of waiting for that slower sampling interval.
        if (current && !ReferenceEquals(_watchSnapshot.Os, os.WristWatch))
            _watchSnapshot = _watchSnapshot with
            {
                Os = os.WristWatch,
                TimerState = os.Watch.State.ToString(),
                TimerRemainingSeconds = os.Watch.State != ClientTabletWatchState.Idle ? os.Watch.RemainingSeconds : null
            };
        _wristWatch.Update(canUse, _tablet.IsActive || _open || _opening || _holstering, _watchSnapshot,
            _deviceInventory.HasWatch && _deviceInventory.WatchWorn);
        _watchHud.Update(_preferences?.Snapshot.HudMode ?? ClientWatchHudMode.Standard,
            _wristWatch.CanInspect, _watchSnapshot.Vitals);
    }

    private static bool TryFieldPosition(out Vector3 position, out Vector3 forward)
    {
        position = forward = default;
        if (!LocalPlayerReads.TryReadLocation(out var observed, out _) || observed.IsInCaves) return false;
        position = observed.Position;
        forward = observed.Forward;
        return forward.x * forward.x + forward.z * forward.z > .001f;
    }

    private void UpdateProfileLink()
    {
        if (!GameRuntime.State.IsInWorld || !ClientCommands.TryGetCurrentSession(out var session)) return;
        if (!_profileAttempted && ClientCommands.SupportsReplies)
        {
            _profileAttempted = true;
            _profileSession = session;
            ClientCommands.Submit("/comms profile", out _profileRequest);
            if (_profileRequest == null) PostField(new("profile-unavailable"));
        }
        if (_profileRequest == null || !_profileRequest.State.IsTerminal) return;
        var result = _profileRequest.State;
        _profileRequest = null;
        if (_profileSession != session) return;
        if (result.Progress == ClientCommandProgress.ServerReported
            && result.Outcome == ClientCommandOutcome.Succeeded && !result.ReplyTruncated
            && TryProfileKey(result.Reply, out var key)) PostField(new("profile", key));
        else PostField(new("profile-unavailable"));
    }

    private static bool TryProfileKey(string? reply, out string key)
    {
        key = "";
        if (reply == null || reply.Length > 512) return false;
        try
        {
            using var document = JsonDocument.Parse(reply, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 4
                || !root.TryGetProperty("v", out var version) || !version.TryGetInt32(out var v) || v != 1
                || !root.TryGetProperty("server", out var server) || server.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("world", out var world) || world.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("account", out var account) || account.ValueKind != JsonValueKind.String
                || !Guid.TryParseExact(server.GetString(), "N", out var serverId) || serverId == Guid.Empty
                || !Guid.TryParseExact(world.GetString(), "N", out var worldId) || worldId == Guid.Empty) return false;
            var accountId = account.GetString();
            if (accountId == null || accountId.Length != 64 || accountId.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))) return false;
            key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(serverId.ToString("N") + ":" + worldId.ToString("N") + ":" + accountId)));
            return true;
        }
        catch (JsonException) { return false; }
    }

    private void PostField(ClientFieldAction action)
    {
        if (action.Kind == "place-notes")
        {
            if (!CanUseDesktop() || _contentObscured || _app != ClientTerminalApp.Navigation) return;
            var snapshot = _os.Snapshot;
            var place = snapshot.Generation == _osGeneration && Guid.TryParse(action.Target, out var placeId)
                ? snapshot.Field.Places.FirstOrDefault(item => item.Id == placeId) : null;
            if (place == null) { Feedback("That saved pin is no longer available."); return; }
            OpenFieldPlaceNotebook(snapshot.Field, place);
            return;
        }
        if (action.Kind is "watch-navigation" or "scout-destination")
        {
            if (!CanUseDesktop() || _contentObscured || _app != ClientTerminalApp.Navigation) return;
            var os = _os.Snapshot;
            var place = Guid.TryParse(action.Target, out var id)
                ? os.Field.Places.FirstOrDefault(item => item.Id == id) : null;
            if (place == null) { Feedback("Select a saved pin first."); return; }
            if (action.Kind == "watch-navigation" && (!_deviceInventory.HasWatch || !_deviceInventory.WatchWorn))
            { Feedback("Put on your field watch before sending it a route."); return; }
            NavigateToPlace(place, action.Kind == "watch-navigation" ? "watch" : "drone");
            return;
        }
        if (!_os.FieldAction(action)) Feedback("The tablet is busy. Try again in a moment.");
    }

    private void NavigateToPlace(ClientFieldPlace place, string device = "map")
    {
        var app = device == "drone" ? ClientTerminalApp.Drone : ClientTerminalApp.Navigation;
        if (!_os.FieldAction(new("navigate-place", Target: device, Place: place, App: app)))
        { Feedback("The tablet is busy. Try again in a moment."); return; }
        // The map waits for the worker's acknowledged notebook before selecting this pin.
        _feedback = "";
        _tablet.FocusFieldDestination(place.Id);
        _nextRefresh = 0;
    }

    private void RememberMessageDraft() => PostField(new("draft", _messageDraft, _draftKey));

    private void RestoreMessageDraft()
    {
        _draftKey = _messenger.Channel + "|" + (_messenger.Channel == "direct" ? _messenger.Target : "");
        var os = _os.Snapshot;
        _messageDraft = os.Generation == _osGeneration && os.Field.Drafts.TryGetValue(_draftKey, out var draft) ? draft : "";
        if (_sharedDraft != null && _sharedDraftKey == _draftKey) { _messageDraft = _sharedDraft; RememberMessageDraft(); }
        else { _sharedDraft = null; _sharedDraftKey = null; }
        _messageStatus = "";
    }

    private void StageMessageDraft(string draft)
    {
        _sharedDraftKey = _draftKey;
        _sharedDraft = _messageDraft = draft;
        RememberMessageDraft();
    }

    private void SharePlace(ClientFieldPlace place)
    {
        if (!CanUseDesktop()) return;
        RememberMessageDraft();
        if (_messenger.Channel == "legacy" || _messenger.Channel == "direct" && _messenger.Target.Length == 0)
            _messenger.SelectChannel("expedition");
        _draftKey = _messenger.Channel + "|" + (_messenger.Channel == "direct" ? _messenger.Target : "");
        StageMessageDraft(ClientLocationMessage.Encode(place));
        _messageStatus = "Map pin prepared in this conversation. Review it and send when ready.";
        Select(ClientTerminalApp.Messages);
    }

    private bool InvokeInbox(string command)
    {
        var os = _os.Snapshot;
        if (os.Generation != _osGeneration || _contentObscured) return true;
        if (command.StartsWith("conversation:", StringComparison.Ordinal))
        {
            var parts = command[13..].Split(':', 2);
            if (parts.Length != 2 || !os.Messages.Any(message => message.Channel == parts[0]
                && (parts[0] != "direct" || message.ConversationId == parts[1]))) return true;
            RememberMessageDraft();
            _messenger.SelectChannel(parts[0]);
            if (parts[0] == "direct") _messenger.SelectIncomingConversation(parts[1]);
            RestoreMessageDraft();
            return true;
        }
        if (command.StartsWith("pin:", StringComparison.Ordinal))
        {
            if (_view == null || !_view.TryGetSharedPlace(command[4..], out var place, out var source)
                || !os.Messages.Contains(source) || source.Channel != _messenger.Channel
                || source.Channel == "direct" && source.ConversationId != _messenger.Target) return true;
            var sourceKey = source with { Unread = false };
            var hasKnownId = _sharedPlaceIds.TryGetValue(sourceKey, out var knownId);
            var saved = hasKnownId ? os.Field.Places.FirstOrDefault(item => item.Id == knownId) : null;
            saved ??= os.Field.Places.FirstOrDefault(item => item.Category == "Shared pin"
                && item.Name == place.Name && item.Position.X == place.Position.X && item.Position.Z == place.Position.Z);
            if (saved != null) place = saved;
            else if (hasKnownId) place = place with { Id = knownId };
            if (os.Field.Places.Count >= 128 && !os.Field.Places.Any(item => item.Id == place.Id))
            { Feedback("Remove a saved place before adding another."); return true; }
            var retained = os.Messages.Select(item => item with { Unread = false }).ToHashSet();
            foreach (var expired in _sharedPlaceIds.Keys.Where(item => !retained.Contains(item)).ToArray())
                _sharedPlaceIds.Remove(expired);
            _sharedPlaceIds[sourceKey] = place.Id;
            NavigateToPlace(place);
            return true;
        }
        if (command == "share-position")
        {
            if (!TryFieldPosition(out var position, out _))
            { Feedback("Your surface location is unavailable here. Share a saved map pin instead."); return true; }
            SharePlace(new ClientFieldPlace(Guid.NewGuid(), "My location", new(position.x, position.y, position.z)));
            return true;
        }
        if (command.StartsWith("preset:", StringComparison.Ordinal))
        {
            var draft = command[7..] switch
            { "ack" => "Acknowledged.", "on-my-way" => "On my way.", "need-help" => "I need assistance.", _ => "" };
            if (draft.Length > 0)
            {
                _messageDraft = draft;
                _sharedDraft = null;
                RememberMessageDraft();
                _messageStatus = "Reply prepared. Review it and press Send.";
            }
            return true;
        }
        return false;
    }

    private void WatchAction(string action)
    {
        if (!GameRuntime.State.IsInWorld) return;
        switch (action)
        {
            case "timer.start": _os.StartWatch(300); break;
            case "timer.pause": _os.PauseWatch(); break;
            case "timer.resume": _os.ResumeWatch(); break;
            case "timer.reset": _os.CancelWatch(); break;
            case "notifications.open":
                _wristWatch.EndInspection();
                if (_open) Select(ClientTerminalApp.Messages);
                else _messagesRequested = true;
                break;
        }
        _nextRefresh = 0;
    }

    private bool InvokeField(string provider, string command)
    {
        if (provider == "watch") { WatchAction(command); return true; }
        if (provider != "field") return false;
        var os = _os.Snapshot;
        if (os.Generation != _osGeneration) return true;
        if (command == "profile-refresh")
        {
            if (!_profileRestored && _profileRequest == null) _profileAttempted = false;
        }
        else if (command.StartsWith("notification:", StringComparison.Ordinal)
            && long.TryParse(command[13..], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            var item = os.Notifications.FirstOrDefault(notification => notification.Id == id);
            if (item == null) return true;
            RememberMessageDraft();
            if (item.App == ClientTerminalApp.Messages)
            {
                _messenger.SelectChannel(item.Channel);
                if (item.Channel == "direct") _messenger.SelectIncomingConversation(item.Target);
                RestoreMessageDraft();
            }
            Select(item.App);
            PostField(new("dismiss", NotificationId: id));
        }
        else if (command.StartsWith("dismiss:", StringComparison.Ordinal)
            && long.TryParse(command[8..], NumberStyles.None, CultureInfo.InvariantCulture, out var dismissed))
            PostField(new("dismiss", NotificationId: dismissed));
        else if (command.StartsWith("favorite:", StringComparison.Ordinal)
            && Enum.TryParse<ClientTerminalApp>(command[9..], out var favorite) && Enum.IsDefined(favorite))
            PostField(new("favorite", App: favorite));
        else if (InvokeFieldFavorite(command)) { }
        else if (command.StartsWith("dismiss-through:", StringComparison.Ordinal))
        {
            var parts = command[16..].Split(':');
            if (parts.Length == 2 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var cutoff)
                && parts[1] is "all" or "messages" or "other")
                PostField(new("dismiss-through", Target: parts[1], NotificationId: cutoff));
        }
        else if (command == "note-new")
        {
            RememberNoteDraft(os.Field);
            SelectNewFieldNote(os.Field, _noteBrowsePlaceId);
        }
        else if (InvokeFieldNoteLocation(command, os.Field)) { }
        else if (command.StartsWith("note-select:", StringComparison.Ordinal)
            && Guid.TryParse(command[12..], out var noteId))
        {
            var note = os.Field.Notes.FirstOrDefault(note => note.Id == noteId);
            if (note != null)
            {
                RememberNoteDraft(os.Field);
                SelectNoteDraft(note);
            }
        }
        else if (command == "note-revert")
        {
            _noteDrafts.Remove(_noteId ?? Guid.Empty);
            SelectNoteDraft(os.Field.Notes.FirstOrDefault(note => note.Id == _noteId));
            Feedback("Draft discarded. Your saved note is unchanged.");
        }
        else if (command == "note-save")
        {
            if (string.IsNullOrWhiteSpace(_noteTitle)) Feedback("Give this note a title before saving.");
            else if (!_noteId.HasValue && os.Field.Notes.Count >= 128) Feedback("Your notebook is full. Remove a note before adding another.");
            else
            {
                var linkedPlace = _notePlaceId.HasValue && os.Field.Places.Any(place => place.Id == _notePlaceId) ? _notePlaceId : null;
                var note = new ClientFieldNote(_noteId ?? Guid.NewGuid(), _noteTitle.Trim(), _noteBody, DateTimeOffset.UtcNow, linkedPlace);
                if (_os.FieldAction(new("note", Note: note)))
                {
                    _noteDrafts.Remove(_noteId ?? Guid.Empty);
                    _noteId = note.Id;
                    _noteTitle = note.Title;
                    _notePlaceId = linkedPlace;
                    Feedback("Note queued. Storage status reports disk persistence.");
                }
                else Feedback("The tablet is busy. Your draft is still here; try Save again.");
            }
        }
        else if (command == "note-delete" && _noteId.HasValue)
        {
            if (_os.FieldAction(new("remove-note", Target: _noteId.Value.ToString("N"))))
            {
                _noteDrafts.Remove(_noteId.Value);
                SelectNoteDraft(null);
            }
            else Feedback("The tablet is busy. Your note has not been removed.");
        }
        else if (command == "mission-from-note" && _noteId.HasValue)
        {
            var note = os.Field.Notes.FirstOrDefault(item => item.Id == _noteId);
            if (note != null) PostField(new("mission", note.Title));
        }
        else if (command.StartsWith("mission-complete:", StringComparison.Ordinal)) PostField(new("mission-complete", command[17..]));
        else if (command == "mission-clear") PostField(new("mission"));
        _nextRefresh = 0;
        return true;
    }

    private void RememberNoteDraft(ClientFieldProfile field)
    {
        var saved = field.Notes.FirstOrDefault(note => note.Id == _noteId);
        var key = _noteId ?? Guid.Empty;
        if (_noteTitle == (saved?.Title ?? "") && _noteBody == (saved?.Body ?? "")
            && _notePlaceId == saved?.PlaceId) _noteDrafts.Remove(key);
        else _noteDrafts[key] = new ClientFieldNote(key, _noteTitle, _noteBody, DateTimeOffset.UtcNow, _notePlaceId);
    }

    private void SelectNoteDraft(ClientFieldNote? saved)
    {
        _noteId = saved?.Id;
        var note = _noteDrafts.GetValueOrDefault(_noteId ?? Guid.Empty) ?? saved;
        _noteTitle = note?.Title ?? "";
        _noteBody = note?.Body ?? "";
        _notePlaceId = note?.PlaceId;
    }

    private CommandResultSnapshot EditField(string field, string value)
    {
        if (value == null) return CommandResultSnapshot.Failure("Enter a value.");
        switch (field)
        {
            case "note-title" when value.Length <= 64 && !value.Any(char.IsControl): _noteTitle = value; break;
            case "note-body" when value.Length <= 2048 && !value.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')): _noteBody = value; break;
            case "mission" when value.Length <= 256 && !value.Any(char.IsControl):
                if (!_os.FieldAction(new("mission", value))) return CommandResultSnapshot.Failure("The tablet is busy. Try again.");
                break;
            default: return CommandResultSnapshot.Failure("That value is too long or contains unsupported characters.");
        }
        return CommandResultSnapshot.Success("Draft updated.");
    }
}
