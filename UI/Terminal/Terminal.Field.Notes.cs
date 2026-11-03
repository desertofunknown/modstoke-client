// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Globalization;
using System.Linq;

namespace Modstoke.Client;

internal sealed partial class ClientTerminal
{
    private void ReconcileFieldNotePlaces(ClientFieldProfile field)
    {
        if (_notePlaceId.HasValue && !field.Places.Any(place => place.Id == _notePlaceId)) _notePlaceId = null;
        if (_noteBrowsePlaceId.HasValue && !field.Places.Any(place => place.Id == _noteBrowsePlaceId)) _noteBrowsePlaceId = null;
        foreach (var key in _noteDrafts.Keys.ToArray())
        {
            var draft = _noteDrafts[key];
            if (draft.PlaceId.HasValue && !field.Places.Any(place => place.Id == draft.PlaceId))
                _noteDrafts[key] = draft with { PlaceId = null };
        }
    }

    private void OpenFieldPlaceNotebook(ClientFieldProfile field, ClientFieldPlace place)
    {
        RememberNoteDraft(field);
        _noteBrowsePlaceId = place.Id;
        var note = field.Notes.Where(item => item.PlaceId == place.Id)
            .OrderByDescending(item => item.UpdatedAt).ThenBy(item => item.Id).FirstOrDefault();
        Select(ClientTerminalApp.Journal);
        if (note != null) SelectNoteDraft(note);
        else SelectNewFieldNote(field, place.Id);
        _nextRefresh = 0;
    }

    private void SelectNewFieldNote(ClientFieldProfile field, Guid? placeId)
    {
        var retained = _noteDrafts.ContainsKey(Guid.Empty);
        SelectNoteDraft(null);
        if (retained)
        {
            if (_notePlaceId != placeId)
                Feedback("Your unsaved new note was restored. Save or discard it before starting another; its location was kept.");
            return;
        }
        _notePlaceId = field.Places.Any(place => place.Id == placeId) ? placeId : null;
    }

    private bool InvokeFieldNoteLocation(string command, ClientFieldProfile field)
    {
        if (command == "notes-all") _noteBrowsePlaceId = null;
        else if (command.StartsWith("notes-place:", StringComparison.Ordinal)
            && Guid.TryParse(command["notes-place:".Length..], out var filter))
        {
            if (field.Places.Any(place => place.Id == filter)) _noteBrowsePlaceId = filter;
            else Feedback("That saved pin is no longer available.");
        }
        else if (command.StartsWith("note-link:", StringComparison.Ordinal)
            && Guid.TryParse(command["note-link:".Length..], out var link))
        {
            if (!field.Places.Any(place => place.Id == link)) { Feedback("That saved pin is no longer available."); return true; }
            _notePlaceId = link;
            Feedback("Location linked in this draft. Choose Save note to keep the link.");
        }
        else if (command == "note-unlink")
        {
            _notePlaceId = null;
            Feedback("Location unlinked in this draft. The saved pin stays on your map; Save note to keep this edit.");
        }
        else if (command == "note-map")
        {
            var place = field.Places.FirstOrDefault(item => item.Id == _notePlaceId);
            if (place == null) { Feedback("Link this note to an existing saved pin first."); return true; }
            RememberNoteDraft(field);
            Select(ClientTerminalApp.Navigation);
            _tablet.FocusFieldDestination(place.Id);
        }
        else return false;
        return true;
    }

    private bool InvokeFieldFavorite(string command)
    {
        foreach (var kind in new[] { "favorite-add", "favorite-remove", "favorite-move", "favorite-replace" })
        {
            if (!command.StartsWith(kind + ":", StringComparison.Ordinal)) continue;
            var parts = command[(kind.Length + 1)..].Split(':');
            if (kind is "favorite-add" or "favorite-remove")
            {
                if (parts.Length == 1 && Enum.TryParse<ClientTerminalApp>(parts[0], out var app) && Enum.IsDefined(app))
                    PostField(new(kind, App: app));
            }
            else if (parts.Length == 2 && Enum.TryParse<ClientTerminalApp>(parts[0], out var first) && Enum.IsDefined(first))
            {
                if (kind == "favorite-move" && parts[1] is ("-1" or "1")) PostField(new(kind, Target: parts[1], App: first));
                else if (kind == "favorite-replace" && Enum.TryParse<ClientTerminalApp>(parts[1], out var next) && Enum.IsDefined(next))
                    PostField(new(kind, Target: first.ToString(), App: next));
            }
            return true;
        }
        return false;
    }
}

internal static class ClientFieldLocationText
{
    internal static string Coordinates(ClientFieldPlace place) => "X " + Number(place.Position.X)
        + " · Z " + Number(place.Position.Z) + (HeightUnknown(place) ? " · Height unknown" : " · Recorded Y " + Number(place.Position.Y));

    internal static bool HeightUnknown(ClientFieldPlace place) => place.Category is "Map pin" or "Shared pin";

    internal static string Source(ClientFieldPlace place) => place.Category switch
    {
        "Map pin" => "Marked on the map · Saved point, not a terrain survey",
        "Shared pin" => "Reported by its sender · Saved point, not a live player",
        "My location" => "Recorded from surface GPS · Saved point, not your live location",
        "Recovery snapshot" => "Recorded drone contact · Last known point, not a live tracking fix",
        _ => "Saved point · " + (string.IsNullOrWhiteSpace(place.Category) ? "Personal" : place.Category)
    };

    private static string Number(float value) => value.ToString("0", CultureInfo.InvariantCulture);
}
