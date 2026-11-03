// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;

namespace Modstoke.Client;

internal sealed record ClientTabletNotification(long Id, string Key, string Title, string Detail,
    ClientTerminalApp App, string Channel = "", string Target = "");

internal sealed record ClientFieldAction(string Kind, string Text = "", string Target = "",
    ClientFieldPlace? Place = null, ClientFieldNote? Note = null, ClientFieldRoute? Route = null,
    ClientFieldPoint? Point = null, long NotificationId = 0, ClientTerminalApp App = ClientTerminalApp.Home,
    ClientReminderAction? Reminder = null);

internal sealed partial class ClientTabletOperatingSystem
{
    private ClientFieldProfileStore? _profileStore;
    private ClientFieldProfile _field = new();
    private string _profileKey = "";
    private string _fieldStorageStatus = "Session notebook — waiting for world identity.";
    private bool _fieldDirty;
    private bool _fieldChanged;
    private bool _profileLoaded;
    private long _saveFieldAfter;
    private string _loadProfileKey = "";
    private readonly Dictionary<string, ClientFieldProfile> _fieldWrites = new(StringComparer.Ordinal);
    private readonly List<ClientTabletNotification> _notifications = new(64);
    private long _nextNotificationId;
    private readonly HashSet<string> _editedFieldKinds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _editedDraftKeys = new(StringComparer.Ordinal);

    // A queued profile selection must survive with the edits that will be saved into it.
    private static bool IsLocalEdit(Operation operation) => operation.Kind == OperationKind.Preference
        || operation.Kind == OperationKind.Field && operation.Field?.Kind is "profile" or "draft" or "place" or "navigate-place" or "remove-place"
            or "destination" or "note" or "remove-note" or "mission" or "mission-complete" or "route" or "remove-route"
            or "trail-toggle" or "trail-clear" or "trail-point" or "favorite" or "favorite-add"
            or "favorite-remove" or "favorite-move" or "favorite-replace";

    private void RetainLocalEdits()
    {
        var edits = _operations.Where(operation => operation.Kind == OperationKind.Preference
            || operation.Generation == _generation && IsLocalEdit(operation)).ToArray();
        _rejectedCommands += _operations.Count - edits.Length;
        _operations.Clear();
        foreach (var edit in edits) _operations.Enqueue(edit);
    }

    internal bool FieldAction(ClientFieldAction action)
    {
        if (action == null) return false;
        lock (_gate)
        {
            if (!Accepting || _operations.Count >= QueueLimit) return RejectCommand();
            _operations.Enqueue(new Operation(OperationKind.Field, _requestedGeneration,
                ClientTerminalApp.Home, false, "", Field: action));
            _wakeup.Set();
            return true;
        }
    }

    private void ResetField()
    {
        SaveField();
        _field = new();
        _profileKey = "";
        _fieldDirty = false;
        _fieldChanged = false;
        _profileLoaded = false;
        _loadProfileKey = "";
        _fieldStorageStatus = "Session notebook — waiting for world identity.";
        _notifications.Clear();
        _editedFieldKinds.Clear();
        _editedDraftKeys.Clear();
    }

    private void ApplyField(ClientFieldAction action)
    {
        if (action.Kind == "reminder")
        {
            if (action.Reminder is { } reminder) ApplyReminder(reminder);
            return;
        }
        switch (action.Kind)
        {
            case "profile":
                if (_profileKey.Length != 0 || _profileStore == null || action.Text.Length != 64
                    || action.Text.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))) return;
                _profileKey = action.Text;
                _loadProfileKey = _profileKey;
                _fieldStorageStatus = "Opening your field notebook…";
                return;
            case "profile-unavailable":
                if (_profileKey.Length == 0) _fieldStorageStatus = "Session notebook — this server has not supplied a persistent world identity.";
                return;
            case "draft":
                if (action.Target.Length is < 1 or > 96 || action.Text.Length > 256 || action.Text.Any(char.IsControl)) return;
                var drafts = new Dictionary<string, string>(_field.Drafts, StringComparer.Ordinal);
                if (action.Text.Length == 0) drafts.Remove(action.Target);
                else if (drafts.ContainsKey(action.Target) || drafts.Count < 32)
                    drafts[action.Target] = action.Target == ClientTabletGames.ScoreDraftKey
                        ? ClientTabletGames.MergeScores(drafts.GetValueOrDefault(action.Target), action.Text) : action.Text;
                else return;
                _field = _field with { Drafts = new ReadOnlyDictionary<string, string>(drafts) };
                // Empty initial composers are remembered during channel changes before loading.
                // Only a draft actually edited this session can suppress the saved draft.
                if (!_profileLoaded && action.Text.Length != 0) _editedDraftKeys.Add(action.Target);
                break;
            case "place":
                if (action.Place is not { } place || !ValidPlace(place)) return;
                if (!_field.Places.Any(item => item.Id == place.Id) && _field.Places.Count >= 128) return;
                _field = _field with { Places = Array.AsReadOnly(_field.Places.Where(item => item.Id != place.Id).Append(place).ToArray()) };
                break;
            case "navigate-place":
                if (action.Place is not { } navigationPlace || !ValidPlace(navigationPlace)
                    || action.App is not (ClientTerminalApp.Navigation or ClientTerminalApp.Drone)) return;
                var knownPlace = _field.Places.FirstOrDefault(item => item.Id == navigationPlace.Id);
                if (knownPlace == null && _field.Places.Count >= 128)
                {
                    AddNotification("navigation", "Location not added", "Remove a saved pin before adding another.", ClientTerminalApp.Navigation);
                    return;
                }
                // The destination and its pin are one notebook edit, including when the queue is nearly full.
                _field = _field with {
                    Places = knownPlace != null ? _field.Places : Array.AsReadOnly(_field.Places.Append(navigationPlace).ToArray()),
                    DestinationId = navigationPlace.Id
                };
                _editedFieldKinds.Add("destination");
                if (action.Target == "watch")
                {
                    _wristSettingsReturn = ClientWatchPage.Navigation;
                    OpenWristApp(ClientWatchPage.Navigation);
                    var watchKey = DevicePreferences.WatchKey;
                    var hint = watchKey == ClientWatchKey.None ? "Choose a watch key in Settings to view your route."
                        : "Stow the tablet, then hold " + watchKey + " to view your route.";
                    AddNotification("navigation", "Watch route ready", hint, ClientTerminalApp.Navigation);
                }
                if (_visible && !_locked && !_contentObscured) OpenApp(action.App, rememberPrevious: true);
                break;
            case "remove-place":
                if (!Guid.TryParse(action.Target, out var remove)) return;
                _field = _field with {
                    Places = Array.AsReadOnly(_field.Places.Where(item => item.Id != remove).ToArray()),
                    DestinationId = _field.DestinationId == remove ? null : _field.DestinationId,
                    Routes = Array.AsReadOnly(_field.Routes.Select(route => route with {
                        Places = Array.AsReadOnly(route.Places.Where(id => id != remove).ToArray()) }).ToArray()),
                    Notes = Array.AsReadOnly(_field.Notes.Select(note => note.PlaceId == remove ? note with { PlaceId = null } : note).ToArray()) };
                break;
            case "destination":
                if (action.Target.Length == 0) _field = _field with { DestinationId = null };
                else if (Guid.TryParse(action.Target, out var destination) && _field.Places.Any(place => place.Id == destination))
                    _field = _field with { DestinationId = destination };
                else return;
                break;
            case "note":
                if (action.Note is not { } note || note.Id == Guid.Empty || string.IsNullOrWhiteSpace(note.Title)
                    || note.Title.Length > 64 || note.Title.Any(char.IsControl) || note.Body.Length > 2048
                    || note.PlaceId.HasValue && !_field.Places.Any(place => place.Id == note.PlaceId)) return;
                if (!_field.Notes.Any(item => item.Id == note.Id) && _field.Notes.Count >= 128) return;
                _field = _field with { Notes = Array.AsReadOnly(_field.Notes.Where(item => item.Id != note.Id).Append(note).ToArray()) };
                break;
            case "remove-note":
                if (!Guid.TryParse(action.Target, out var removeNote)) return;
                _field = _field with { Notes = Array.AsReadOnly(_field.Notes.Where(note => note.Id != removeNote).ToArray()) };
                break;
            case "mission":
                if (action.Text.Length > 256 || action.Text.Any(char.IsControl)) return;
                _field = _field with { Mission = action.Text };
                break;
            case "mission-complete":
                if (string.IsNullOrWhiteSpace(_field.Mission) || action.Text != _field.Mission) return;
                if (_field.Notes.Count >= 128)
                {
                    AddNotification("mission", "Objective kept", "Your notebook is full. Remove a note before archiving this objective.", ClientTerminalApp.Journal);
                    return;
                }
                var completedAt = DateTimeOffset.UtcNow;
                var objective = _field.Mission.Trim();
                var completed = new ClientFieldNote(Guid.NewGuid(), "Completed · " + (objective.Length > 52 ? objective[..52] : objective),
                    objective + "\n\nCompleted " + completedAt.ToString("u", System.Globalization.CultureInfo.InvariantCulture), completedAt);
                _field = _field with { Mission = "", Notes = Array.AsReadOnly(_field.Notes.Append(completed).ToArray()) };
                _editedFieldKinds.Add("mission");
                _editedFieldKinds.Add("note");
                AddNotification("mission", "Objective archived", "The completed objective is now a notebook entry. Choose your next step on Home.", ClientTerminalApp.Journal);
                break;
            case "route":
                if (action.Route is not { } route || route.Id == Guid.Empty || string.IsNullOrWhiteSpace(route.Name)
                    || route.Name.Length > 64 || route.Name.Any(char.IsControl) || route.Places.Count > 128
                    || route.Places.Any(id => !_field.Places.Any(place => place.Id == id))) return;
                if (!_field.Routes.Any(item => item.Id == route.Id) && _field.Routes.Count >= 32) return;
                _field = _field with { Routes = Array.AsReadOnly(_field.Routes.Where(item => item.Id != route.Id)
                    .Append(route with { Places = Array.AsReadOnly(route.Places.ToArray()) }).ToArray()) };
                break;
            case "trail-toggle":
                _field = _field with { RecordTrail = !_field.RecordTrail };
                break;
            case "remove-route":
                if (!Guid.TryParse(action.Target, out var removeRoute)) return;
                _field = _field with { Routes = Array.AsReadOnly(_field.Routes.Where(route => route.Id != removeRoute).ToArray()) };
                break;
            case "trail-clear":
                _field = _field with { Trail = Array.Empty<ClientFieldPoint>() };
                break;
            case "trail-point":
                if (!_field.RecordTrail || action.Point is not { } point || !ValidPoint(point)) return;
                if (_field.Trail.LastOrDefault() is { } last && DistanceSquared(point, last) < 25f) return;
                _field = _field with { Trail = Array.AsReadOnly(_field.Trail.TakeLast(511).Append(point).ToArray()) };
                break;
            case "favorite":
                if (!Enum.IsDefined(action.App) || action.App == ClientTerminalApp.Home) return;
                _field = _field with { Favorites = _field.Favorites.Contains(action.App)
                    ? Array.AsReadOnly(_field.Favorites.Where(app => app != action.App).ToArray())
                    : Array.AsReadOnly(_field.Favorites.TakeLast(7).Append(action.App).ToArray()) };
                break;
            case "favorite-add":
            case "favorite-remove":
            case "favorite-move":
            case "favorite-replace":
                if (!ApplyFavoriteEdit(action)) return;
                _editedFieldKinds.Add("favorite");
                break;
            case "dismiss":
                _notifications.RemoveAll(item => item.Id == action.NotificationId);
                return;
            case "dismiss-through":
                if (action.NotificationId <= 0 || action.Target is not ("all" or "messages" or "other")) return;
                _notifications.RemoveAll(item => item.Id <= action.NotificationId && (action.Target == "all"
                    || (item.App == ClientTerminalApp.Messages) == (action.Target == "messages")));
                return;
            case "notify":
                if (action.Text.Length > 128 || action.Target.Length > 512 || !Enum.IsDefined(action.App)) return;
                AddNotification("activity:" + action.Text, action.Text, action.Target, action.App);
                return;
            default: return;
        }
        _editedFieldKinds.Add(action.Kind);
        DirtyField();
    }

    private ClientFieldProfile MergeField(ClientFieldProfile saved, ClientFieldProfile current)
    {
        var places = current.Places.Concat(saved.Places).DistinctBy(item => item.Id).Take(128).ToArray();
        var ids = places.Select(place => place.Id).ToHashSet();
        // Current edits take the available slots first, and cleared drafts must stay cleared.
        var drafts = current.Drafts.Concat(saved.Drafts.Where(pair => !_editedDraftKeys.Contains(pair.Key)))
            .DistinctBy(pair => pair.Key, StringComparer.Ordinal).Take(32)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        if (drafts.ContainsKey(ClientTabletGames.ScoreDraftKey))
            drafts[ClientTabletGames.ScoreDraftKey] = ClientTabletGames.MergeScores(
                saved.Drafts.GetValueOrDefault(ClientTabletGames.ScoreDraftKey),
                current.Drafts.GetValueOrDefault(ClientTabletGames.ScoreDraftKey));
        var destinationChoice = _editedFieldKinds.Contains("destination") ? current.DestinationId
            : current.DestinationId ?? saved.DestinationId;
        return saved with {
            Drafts = new ReadOnlyDictionary<string, string>(drafts),
            Places = Array.AsReadOnly(places),
            Notes = Array.AsReadOnly(current.Notes.Concat(saved.Notes).DistinctBy(item => item.Id).Take(128)
                .Select(note => note.PlaceId.HasValue && !ids.Contains(note.PlaceId.Value) ? note with { PlaceId = null } : note).ToArray()),
            Routes = Array.AsReadOnly(current.Routes.Concat(saved.Routes).DistinctBy(item => item.Id).Take(32)
                .Select(route => route with { Places = Array.AsReadOnly(route.Places.Where(ids.Contains).ToArray()) }).ToArray()),
            DestinationId = destinationChoice is { } destination && ids.Contains(destination) ? destination : null,
            Mission = _editedFieldKinds.Contains("mission") ? current.Mission : saved.Mission,
            Favorites = _editedFieldKinds.Contains("favorite") ? current.Favorites : saved.Favorites,
            RecordTrail = _editedFieldKinds.Contains("trail-toggle") ? current.RecordTrail : saved.RecordTrail,
            Trail = _editedFieldKinds.Contains("trail-point") || _editedFieldKinds.Contains("trail-clear") ? current.Trail : saved.Trail,
            LastApp = _editedFieldKinds.Contains("app") ? current.LastApp : saved.LastApp
        };
    }

    private void AddNotification(string key, string title, string detail, ClientTerminalApp app, string channel = "", string target = "")
    {
        _notifications.RemoveAll(item => item.Key == key);
        if (_notifications.Count == 64) _notifications.RemoveAt(0);
        _notifications.Add(new ClientTabletNotification(++_nextNotificationId, key, title, detail, app, channel, target));
    }

    private void DirtyField()
    {
        _fieldChanged = true;
        if (!_fieldDirty) _saveFieldAfter = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
        _fieldDirty = true;
    }

    private bool FlushFieldIfDue()
    {
        if (!_fieldDirty || Stopwatch.GetTimestamp() < _saveFieldAfter) return false;
        SaveField();
        return true;
    }

    private void SaveField()
    {
        if (!_fieldDirty) return;
        _fieldDirty = false;
        if (_profileKey.Length == 0 || _profileStore == null || !_profileLoaded) return;
        _fieldWrites[_profileKey] = _field;
    }

    private void ProcessFieldPersistence()
    {
        if (_profileStore == null) return;
        KeyValuePair<string, ClientFieldProfile>[] writes;
        string load;
        long generation;
        lock (_gate)
        {
            writes = _fieldWrites.ToArray();
            _fieldWrites.Clear();
            load = _loadProfileKey;
            _loadProfileKey = "";
            generation = _generation;
        }
        // Disk work never holds the command queue lock or blocks Unity's update thread.
        foreach (var write in writes)
        {
            _profileStore.Save(write.Key, write.Value);
            lock (_gate)
                if (write.Key == _profileKey && generation == _requestedGeneration)
                { _fieldStorageStatus = _profileStore.Status; Publish(); }
        }
        if (load.Length == 0) return;
        var saved = _profileStore.Load(load);
        lock (_gate)
        {
            // A reset or disposal can arrive during the read. Finish the old profile so its
            // retained edits can still be saved, without presenting it in the next session.
            if (generation != _generation || load != _profileKey) return;
            var current = !_disposed && generation == _requestedGeneration;
            var hadEdits = _fieldChanged;
            if (current) UpdateForegroundActivity();
            _field = hadEdits ? MergeField(saved, _field) : saved;
            _profileLoaded = true;
            _editedDraftKeys.Clear();
            _fieldStorageStatus = _profileStore.Status;
            if (hadEdits && _profileStore.CanSave) DirtyField();
            if (current)
            {
                _activeApp = _field.LastApp;
                Publish();
            }
            if (_fieldDirty) _wakeup.Set();
        }
    }

    private static bool ValidPlace(ClientFieldPlace place) => place.Id != Guid.Empty
        && !string.IsNullOrWhiteSpace(place.Name) && place.Name.Length <= 64 && !place.Name.Any(char.IsControl)
        && place.Note.Length <= 2048 && place.Category.Length <= 64 && !place.Category.Any(char.IsControl)
        && ValidPoint(place.Position);

    private static bool ValidPoint(ClientFieldPoint point) => float.IsFinite(point.X) && float.IsFinite(point.Y)
        && float.IsFinite(point.Z) && MathF.Abs(point.X) <= 1_000_000 && MathF.Abs(point.Y) <= 1_000_000 && MathF.Abs(point.Z) <= 1_000_000;

    private static float DistanceSquared(ClientFieldPoint a, ClientFieldPoint b) =>
        (a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z);
}
