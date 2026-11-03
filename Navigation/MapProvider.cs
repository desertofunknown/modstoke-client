// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Modstoke.Loader;
using Modstoke.Sons;

namespace Modstoke.Client;

internal sealed class ClientMapProvider : IMenuContentProvider, ICommandProvider,
    IMenuInputProvider
{
    internal const string MenuId = "modstoke.client.map";
    private static readonly string[] Categories = { "general", "camp", "resource", "danger", "discovery" };
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly object _sync = new();
    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
    private bool _active = true;
    private bool _started;
    private Guid? _session;
    private Guid _scope = Guid.NewGuid();
    private Browse _browse;
    private Form _form;
    private Edit _edit;
    private int _page = 1;
    private int _category;
    private string _id = "";
    private string _readId = "";
    private string _name = "";
    private string _text = "";
    private Review? _review;
    private Query? _queued;
    private Query? _pendingQuery;
    private ClientCommand? _pending;
    private ClientCommandProgress? _progress;
    private Query? _observedQuery;
    private ClientCommandState? _observation;
    private string? _reply;
    private double _observationExpires;
    private double _nextSend;
    private string _status = "Waiting for the client to start.";

    private enum Browse { Personal, Tribe, Rally, Details, Note }
    private enum Form { Browse, Add, Edit, Share, Remove, SetRally, ClearRally }
    private enum Edit { Rename, Text, Category, Move }
    private sealed record Review(Guid Token, string Command);
    private readonly record struct Query(Guid Session, string Command, bool Mutation);

    public string Id => MenuId;
    public string DisplayName => "Maps";
    public string Kind => MenuProviderKinds.Status;
    public int Priority => 520;
    public string Source => "Modstoke Client by OpenACAI";
    public string Description => "Personal and Tribe waypoints and rally points";
    public bool RequiresAuthorization => false;
    public bool Enabled { get { lock (_sync) return _active; } }

    internal void Start()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
            return;
        lock (_sync)
        {
            if (!_active)
                return;
            _started = true;
        }
        Update();
    }

    internal void Update()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
            return;
        lock (_sync)
        {
            if (!_active || !_started)
                return;
            // Native connection state and Submit are restricted to this owner-thread path.
            var available = ClientCommands.TryGetCurrentSession(out var session)
                && ClientCommands.SupportsReplies;
            if ((available && _session != session) || (!available && _session.HasValue))
            {
                ClearSessionData();
                _session = available ? session : null;
                _status = "Ready. Choose a read or prepare a change.";
            }
            if (!available)
            {
                _status = "Maps unavailable. Join a compatible server; hosts can use /map and /rally in chat.";
                return;
            }
            var now = MonotonicSeconds;
            ExpireObservation(now);
            if (_pending != null && _pendingQuery is { } pendingQuery)
            {
                // Retain the exact handle; a sequence or prose reply is never request identity.
                var state = _pending.State;
                _progress = state.Progress;
                if (state.IsTerminal)
                {
                    if (pendingQuery.Session == session)
                    {
                        _observedQuery = pendingQuery;
                        _observation = state;
                        _reply = state.Reply == null ? null : ClientCommandText.DisplayReply(state.Reply);
                        var age = Math.Max(0, (DateTimeOffset.UtcNow - state.ObservedAt).TotalSeconds);
                        _observationExpires = now + Math.Max(0, 30 - age);
                        _status = DescribeProgress(state.Progress);
                        ExpireObservation(now);
                    }
                    _pending = null;
                    _pendingQuery = null;
                    _progress = null;
                }
            }
            if (_pending != null || _queued is not { } query || now < _nextSend)
                return;
            _queued = null;
            if (query.Session != session)
                return;
            _nextSend = now + 1;
            var sent = ClientCommands.Submit(query.Command, out var request);
            if (request != null)
            {
                _pending = request;
                _pendingQuery = query;
                _progress = request.State.Progress;
                _status = sent == ClientCommandSendResult.SendUncertain
                    ? "Send result unknown. Waiting for this request; no automatic retry."
                    : "Waiting for the server's reply.";
            }
            else
                _status = "Request was not queued: " + sent + ". No automatic retry was made.";
        }
    }

    public MenuInputSnapshot GetInputs()
    {
        lock (_sync)
        {
            var fields = new List<MenuInputFieldSnapshot>();
            if (_form is Form.Browse or Form.Edit or Form.Share or Form.Remove)
                fields.Add(new("id", "Waypoint ID", _id,
                    "Full GUID or an unambiguous 8–32 hexadecimal prefix; the server resolves it.", 36, CanRequest));
            if (_form is Form.Add or Form.SetRally || (_form == Form.Edit && _edit == Edit.Rename))
                fields.Add(new("name", "Name", _name, "1–48 characters after trimming.", 48, CanRequest));
            if (_form == Form.Edit && _edit == Edit.Text)
                fields.Add(new("text", "Note", _text, "Up to 120 characters; empty clears the note.", 120, CanRequest));
            return new MenuInputSnapshot(_scope, fields);
        }
    }

    public bool TrySetInput(Guid scope, string fieldId, string value, out CommandResultSnapshot result)
    {
        lock (_sync)
        {
            if (scope != _scope || !CanRequest)
                return Fail("This form is stale or a request is still pending.", out result);
            var allowed = fieldId switch {
                "id" => _form is Form.Browse or Form.Edit or Form.Share or Form.Remove,
                "name" => _form is Form.Add or Form.SetRally || (_form == Form.Edit && _edit == Edit.Rename),
                "text" => _form == Form.Edit && _edit == Edit.Text,
                _ => false
            };
            var maximum = fieldId == "id" ? 36 : fieldId == "name" ? 48 : 120;
            // A rejected edit must not leave an earlier confirmation available either.
            _review = null;
            if (!allowed || value == null || value.Length > maximum || !SafeText(value))
                return Fail("Input is too long or contains unsupported characters.", out result);
            switch (fieldId)
            {
                case "id": _id = value; break;
                case "name": _name = value; break;
                case "text": _text = value; break;
            }
            result = CommandResultSnapshot.Success("Draft updated in memory. Review again before sending a change.");
            return true;
        }
    }

    public IReadOnlyList<CommandSnapshot> GetCommands()
    {
        lock (_sync)
        {
            ExpireObservation(MonotonicSeconds);
            var ready = CanRequest;
            var actions = new List<CommandSnapshot>();
            if (_form == Form.Browse)
            {
                actions.Add(Action("personal", "Personal", "Request one personal waypoint.", ready));
                actions.Add(Action("tribe", "Tribe", "Request one shared Tribe waypoint.", ready));
                actions.Add(Action("rally", "Rally", "Request the Tribe rally point.", ready));
                actions.Add(Action("details", "Details", "Request details for the entered waypoint ID.", ready));
                actions.Add(Action("note", "Read note", "Request the note for the entered waypoint ID.", ready));
                var paging = _browse is Browse.Personal or Browse.Tribe;
                actions.Add(Action("previous", "Previous item", "Request the previous item; total unknown.", ready && paging && _page > 1));
                actions.Add(Action("next", "Next item", "Request the next item; total unknown.", ready && paging && _page < 64));
                actions.Add(Action("refresh", "Refresh selected read", "Explicitly request the selected read again.", ready));
                actions.Add(Action("add", "New waypoint", "Prepare a personal waypoint at your server-observed position.", ready));
                actions.Add(Action("edit", "Edit waypoint", "Prepare a rename, note, category or position change.", ready));
                actions.Add(Action("share", "Share", "Prepare an independent Tribe copy of a personal waypoint.", ready));
                actions.Add(Action("remove", "Remove", "Prepare removal of the entered waypoint.", ready));
                actions.Add(Action("set-rally", "Set rally", "Prepare a rally point at your server-observed position.", ready));
                actions.Add(Action("clear-rally", "Clear rally", "Prepare clearing the Tribe rally point.", ready));
            }
            else
            {
                if (_form == Form.Edit)
                    actions.Add(Action("edit-action", "Edit action: " + _edit, "Cycle Rename, Text, Category and Move.", ready));
                if (_form == Form.Add || (_form == Form.Edit && _edit == Edit.Category))
                    actions.Add(Action("category", "Category: " + Categories[_category], "Cycle waypoint category.", ready));
                actions.Add(Action("review", "Review change", "Validate and review the exact command before confirmation.", ready));
                if (_review is { } review)
                {
                    actions.Add(Action("confirm." + review.Token.ToString("N"), "Confirm change", "Send the reviewed command once.", ready));
                    actions.Add(Action("cancel", "Cancel review", "Discard this confirmation without sending.", ready));
                }
                actions.Add(Action("back", "Back to browse", "Discard the editor draft and confirmation.", ready));
            }
            actions.Add(Action("clear-reply", "Clear reply", "Discard the cached reply.", ready && _observation != null));
            return actions;
        }
    }

    public bool TryInvokeCommand(string commandId, out CommandResultSnapshot result)
    {
        lock (_sync)
        {
            ExpireObservation(MonotonicSeconds);
            var prefix = "map." + _scope.ToString("N") + ".";
            if (commandId == null || !commandId.StartsWith(prefix, StringComparison.Ordinal) || !CanRequest)
                return Fail("That action is stale, unavailable, or another request is queued or pending.", out result);
            var action = commandId.Substring(prefix.Length);
            if (action.StartsWith("confirm.", StringComparison.Ordinal))
            {
                if (_review is not { } review || action != "confirm." + review.Token.ToString("N"))
                    return Fail("That confirmation is no longer current. Review the change again.", out result);
                Queue(review.Command, true);
                ResetForm(Form.Browse);
                result = CommandResultSnapshot.Success(_status);
                return true;
            }
            // Any other action invalidates the exact-command confirmation.
            _review = null;
            if (action == "clear-reply")
            {
                ClearObservation();
                _status = "Cached reply cleared.";
            }
            else if (_form == Form.Browse)
            {
                switch (action)
                {
                    case "add": ResetForm(Form.Add); break;
                    case "edit": ResetForm(Form.Edit, keepId: true); break;
                    case "share": ResetForm(Form.Share, keepId: true); break;
                    case "remove": ResetForm(Form.Remove, keepId: true); break;
                    case "set-rally": ResetForm(Form.SetRally); break;
                    case "clear-rally": ResetForm(Form.ClearRally); break;
                    default:
                        var browse = _browse;
                        var page = _page;
                        switch (action)
                        {
                            case "personal": browse = Browse.Personal; break;
                            case "tribe": browse = Browse.Tribe; break;
                            case "rally": browse = Browse.Rally; break;
                            case "details": browse = Browse.Details; break;
                            case "note": browse = Browse.Note; break;
                            case "previous" when (browse is Browse.Personal or Browse.Tribe) && page > 1: page--; break;
                            case "next" when (browse is Browse.Personal or Browse.Tribe) && page < 64: page++; break;
                            case "refresh": break;
                            default: return Fail("That action is unavailable.", out result);
                        }
                        if (browse != _browse)
                            page = 1;
                        var id = action == "refresh" ? _readId : _id.Trim();
                        if ((browse is Browse.Details or Browse.Note) && !ClientCommandText.ValidId(id))
                            return Fail("Enter a full GUID or 8–32 hexadecimal ID prefix.", out result);
                        var command = browse switch {
                            Browse.Personal => "/map list " + page.ToString(CultureInfo.InvariantCulture),
                            Browse.Tribe => "/map tribe " + page.ToString(CultureInfo.InvariantCulture),
                            Browse.Rally => "/rally info",
                            Browse.Details => "/map info " + id,
                            _ => "/map note " + id
                        };
                        if (!ValidCommand(command))
                            return Fail("The complete command must fit in 256 UTF-8 bytes.", out result);
                        _browse = browse;
                        _page = page;
                        _readId = browse is Browse.Details or Browse.Note ? id : "";
                        Queue(command, false);
                        break;
                }
            }
            else
            {
                switch (action)
                {
                    case "back": ResetForm(Form.Browse); break;
                    case "cancel": _status = "Review cancelled; nothing sent."; break;
                    case "edit-action" when _form == Form.Edit:
                        _edit = (Edit)(((int)_edit + 1) % 4);
                        _scope = Guid.NewGuid();
                        break;
                    case "category" when _form == Form.Add || (_form == Form.Edit && _edit == Edit.Category):
                        _category = (_category + 1) % Categories.Length;
                        break;
                    case "review":
                        if (!TryBuildMutation(out var command, out var error))
                            return Fail(error, out result);
                        _review = new Review(Guid.NewGuid(), command);
                        _status = "Review the exact command and effects, then confirm to send once.";
                        break;
                    default: return Fail("That editor action is unavailable.", out result);
                }
            }
            result = CommandResultSnapshot.Success(_status);
            return true;
        }
    }

    public MenuContentSnapshot CreateContent()
    {
        lock (_sync)
        {
            ExpireObservation(MonotonicSeconds);
            var rows = new List<MenuRowSnapshot> {
                Row("Connection", _session.HasValue ? "Connected. Your server decides which map actions are available."
                    : "Maps unavailable. Join a compatible server; hosts can use /map and /rally in chat."),
                Row("View", _form == Form.Browse ? BrowseLabel : _form + (_form == Form.Edit ? " / " + _edit : "")),
                Row("Request", _queued.HasValue ? "Queued; execution is not confirmed."
                    : _progress.HasValue ? DescribeProgress(_progress.Value) : _status)
            };
            if (_observation is { } observation && _observedQuery is { } query)
            {
                rows.Add(Row("Requested", ClientCommandText.DisplayReply(query.Command)));
                rows.Add(Row("Observed (UTC)", observation.ObservedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)));
                rows.Add(Row("Server result", observation.Progress == ClientCommandProgress.ServerReported
                    ? observation.Outcome?.ToString() ?? "Not supplied" : "Unknown; no server result was received."));
                rows.Add(Row("Reply", string.IsNullOrEmpty(_reply) ? "No reply text was supplied." : _reply));
                if (observation.ReplyTruncated)
                    rows.Add(Row("Shortened reply", "The received reply was truncated; this is not the complete result."));
                if (query.Mutation)
                    rows.Add(Row("Persistence", "A successful change is applied in memory, pending the next world save; it does not confirm a durable save."));
                rows.Add(Row("Freshness", "Reply kept for up to 30 seconds. Request another read to check changes."));
            }
            else
                rows.Add(Row("Last reply", "No cached reply."));
            if (_review is { } review)
            {
                var preview = DisplayCommand(review.Command);
                rows.Add(Row("Confirm this command", preview));
                if (preview != review.Command)
                    rows.Add(Row("Visible escapes", "In this preview, \\\\ means one literal backslash; \\uXXXX or \\UXXXXXXXX identifies an invisible formatting character. The original characters are sent."));
            }
            if (_form is Form.Add or Form.SetRally || (_form == Form.Edit && _edit == Edit.Move))
                rows.Add(Row("Position", "Uses your server-observed position when the command executes. No coordinates are entered here."));
            if (_form == Form.Share)
                rows.Add(Row("Sharing", "Creates an independent Tribe copy and retains the personal original. Later edits are not synchronized."));
            if (_form == Form.Remove)
                rows.Add(Row("Removal", "Removes the selected waypoint; verify its ID before confirming."));
            if (_form is Form.SetRally or Form.ClearRally)
                rows.Add(Row("Rally", "Setting replaces the current rally point; clearing removes it. Leader/officer permission is checked by the server."));
            if (_form == Form.Browse)
            {
                rows.Add(Row("Browsing", "One item per reply, pages 1–64; total unknown. Enter an ID for Details or Read note."));
                if (_browse is Browse.Details or Browse.Note)
                    rows.Add(Row("Selected ID", _readId));
            }
            rows.Add(Row("Access", "Server rules and your current Tribe role decide access. Community must be enabled and you must not be muted."));
            rows.Add(Row("About Maps", "Manage waypoints and rally points. These do not add icons to the game GPS."));
            return new MenuContentSnapshot(DisplayName, "by OpenACAI | Waypoints and rally points",
                new[] { new MenuSectionSnapshot("Maps", rows) });
        }
    }

    private bool TryBuildMutation(out string command, out string error)
    {
        command = "";
        error = "";
        var id = _id.Trim();
        var name = _name.Trim();
        if ((_form is Form.Edit or Form.Share or Form.Remove) && !ClientCommandText.ValidId(id))
        {
            error = "Enter a full GUID or 8–32 hexadecimal ID prefix.";
            return false;
        }
        if ((_form is Form.Add or Form.SetRally || (_form == Form.Edit && _edit == Edit.Rename))
            && (name.Length is < 1 or > 48 || !SafeText(name)))
        {
            error = "A name must contain 1–48 characters after trimming, without controls or angle brackets.";
            return false;
        }
        if (_form == Form.Edit && _edit == Edit.Text && (_text.Length > 120 || !SafeText(_text)))
        {
            error = "A note must contain at most 120 characters, without controls or angle brackets.";
            return false;
        }
        command = _form switch {
            Form.Add => "/map add " + Categories[_category] + " " + name,
            Form.Share => "/map share " + id,
            Form.Remove => "/map remove " + id,
            Form.SetRally => "/rally set " + name,
            Form.ClearRally => "/rally clear",
            Form.Edit => _edit switch {
                Edit.Rename => "/map rename " + id + " " + name,
                Edit.Text => "/map text " + id + (_text.Length == 0 ? "" : " " + _text),
                Edit.Category => "/map category " + id + " " + Categories[_category],
                _ => "/map move " + id
            },
            _ => ""
        };
        if (!ValidCommand(command))
        {
            error = "The complete command must fit in 256 strict UTF-8 bytes; shorten the name or note.";
            return false;
        }
        return true;
    }

    private void Queue(string command, bool mutation)
    {
        _queued = new Query(_session!.Value, command, mutation);
        ClearObservation();
        _status = "Request queued; execution is not confirmed.";
    }

    internal void Deactivate()
    {
        lock (_sync)
        {
            _active = false;
            _started = false;
            _session = null;
            ClearSessionData();
            _status = "Modstoke Client has stopped.";
        }
    }

    private bool Busy => _queued.HasValue || _pending != null;
    private bool CanRequest => _active && _started && _session.HasValue && !Busy;
    private static double MonotonicSeconds => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    private string BrowseLabel => _browse + (_browse is Browse.Personal or Browse.Tribe
        ? " | Item " + _page.ToString(CultureInfo.InvariantCulture) : "");

    private void ResetForm(Form form, bool keepId = false)
    {
        _form = form;
        _scope = Guid.NewGuid();
        _review = null;
        _edit = Edit.Rename;
        _category = 0;
        if (!keepId)
            _id = "";
        _name = "";
        _text = "";
    }

    private void ClearSessionData()
    {
        _queued = null;
        _pending = null;
        _pendingQuery = null;
        _progress = null;
        _browse = Browse.Personal;
        _readId = "";
        _page = 1;
        _nextSend = 0;
        ResetForm(Form.Browse);
        ClearObservation();
    }

    private void ClearObservation()
    {
        _observedQuery = null;
        _observation = null;
        _reply = null;
        _observationExpires = 0;
    }

    private void ExpireObservation(double now)
    {
        if (_observation != null && now >= _observationExpires)
        {
            ClearObservation();
            _status = "Cached reply expired after 30 seconds. Request another read explicitly.";
        }
    }

    private CommandSnapshot Action(string id, string label, string description, bool enabled) =>
        new("map." + _scope.ToString("N") + "." + id, label, description, "Maps", enabled, false);

    private static bool Fail(string message, out CommandResultSnapshot result)
    {
        result = CommandResultSnapshot.Failure(message);
        return false;
    }

    private static bool SafeText(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsControl(c) || c is '<' or '>' or '\u2028' or '\u2029')
                return false;
            if (char.IsHighSurrogate(c))
            {
                if (++i >= value.Length || !char.IsLowSurrogate(value[i]))
                    return false;
            }
            else if (char.IsLowSurrogate(c))
                return false;
        }
        return true;
    }

    private static bool ValidCommand(string command)
    {
        if (command.Length == 0 || !SafeText(command))
            return false;
        try { return StrictUtf8.GetByteCount(command) <= 256; }
        catch (EncoderFallbackException) { return false; }
    }

    private static string DescribeProgress(ClientCommandProgress progress) => progress switch {
        ClientCommandProgress.Sending => "Sending; execution is not confirmed.",
        ClientCommandProgress.Queued => "Waiting for the server's report; execution is not confirmed.",
        ClientCommandProgress.SendUncertain => "Send result unknown. Waiting for this request; no automatic retry.",
        ClientCommandProgress.ServerReported => "The server reported a result for this request.",
        ClientCommandProgress.TimedOut => "Timed out. Result unknown; no automatic retry was made.",
        _ => "Request became unavailable. Result unknown; no automatic retry was made."
    };

    private static string DisplayCommand(string command)
    {
        var text = new StringBuilder();
        foreach (var rune in command.EnumerateRunes())
        {
            if (rune.Value == '\\')
                text.Append("\\\\");
            else if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format)
            {
                text.Append(rune.Value <= 0xffff ? "\\u" : "\\U");
                text.Append(rune.Value.ToString(rune.Value <= 0xffff ? "X4" : "X8", CultureInfo.InvariantCulture));
            }
            else
                text.Append(rune.ToString());
        }
        return text.ToString();
    }

    private static MenuRowSnapshot Row(string label, string value) => new(label, value, "", "");
}
