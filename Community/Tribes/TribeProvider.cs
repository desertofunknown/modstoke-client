// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Modstoke.Loader;
using Modstoke.Sons;

namespace Modstoke.Client;

internal sealed partial class ClientTribeProvider : IMenuContentProvider, ICommandProvider,
    IMenuInputProvider
{
    internal const string MenuId = "modstoke.client.tribe";
    private const double ObservationLifetime = 30;
    private readonly object _sync = new();
    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
    private bool _active = true;
    private bool _started;
    private Guid? _session;
    private View _view;
    private int _page = 1;
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

    private enum View { Info, Roster, Invitations, Sent, Targets, Tasks, Activity }
    private readonly record struct Query(Guid Session, string Command, bool Mutation);

    public string Id => MenuId;
    public string DisplayName => "Tribe";
    public string Kind => MenuProviderKinds.Status;
    public int Priority => 510;
    public string Source => "Modstoke Client by OpenACAI";
    public string Description => "Tribe membership, roles, announcements and tasks";
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

            // Only this owner-thread path reads live SDK/native connection state or sends commands.
            var available = ClientCommands.TryGetCurrentSession(out var session)
                && ClientCommands.SupportsReplies;
            if ((available && _session != session) || (!available && _session.HasValue))
            {
                ClearSessionData();
                _session = available ? session : null;
                _status = "Ready. Choose a view or prepare a change.";
            }
            if (!available)
            {
                _status = "Tribe menu unavailable. Join a compatible server to use these actions.";
                return;
            }

            var now = MonotonicSeconds;
            ExpireObservation(now);
            if (_pending != null && _pendingQuery is { } pendingQuery)
            {
                // The exact request handle owns this immutable state; sequences are never identity.
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
                        _observationExpires = now + Math.Max(0, ObservationLifetime - age);
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

    public IReadOnlyList<CommandSnapshot> GetCommands()
    {
        lock (_sync)
        {
            ExpireObservation(MonotonicSeconds);
            var ready = CanRequest;
            var actions = new List<CommandSnapshot>();
            if (_form == Form.Browse)
            {
                actions.Add(Action("info", "My tribe", "Request /tribe info.", ready));
                actions.Add(Action("roster", "Roster", "Request a roster item with a member ID.", ready));
                actions.Add(Action("invites", "Invitations", "Request an incoming invitation item.", ready));
                actions.Add(Action("sent", "Sent invitations", "Request an outgoing invitation item.", ready));
                actions.Add(Action("targets", "Online invite targets", "Request an online session ID or host target.", ready));
                actions.Add(Action("tasks", "Tasks", "Request a task item.", ready));
                actions.Add(Action("history", "Activity", "Request a tribe history item.", ready));
                actions.Add(Action("previous", "Previous item", "Request the previous item; the total is unknown.",
                    ready && _view != View.Info && _page > 1));
                actions.Add(Action("next", "Next item", "Request the next item; the total is unknown.",
                    ready && _view != View.Info && _page < MaximumPage(_view)));
                actions.Add(Action("refresh", "Refresh selected read", "Request only the selected read view and item again.", ready));
                actions.Add(Action("membership", "Membership", "Create, invite, accept, decline, cancel, leave or disband.", ready));
                actions.Add(Action("roles", "Roles and notices", "Manage member roles, leadership and announcements.", ready));
                actions.Add(Action("manage-tasks", "Manage tasks", "Add, complete or remove completed tasks.", ready));
            }
            else
                AddEditorCommands(actions, ready);
            actions.Add(Action("clear-reply", "Clear reply", "Discard the cached reply.", ready && _observation != null));
            return actions;
        }
    }

    public bool TryInvokeCommand(string commandId, out CommandResultSnapshot result)
    {
        lock (_sync)
        {
            ExpireObservation(MonotonicSeconds);
            var prefix = "tribe." + _scope.ToString("N") + ".";
            if (commandId == null || !commandId.StartsWith(prefix, StringComparison.Ordinal) || !CanRequest)
                return Fail("That action is stale, unavailable, or another request is queued or pending.", out result);
            var action = commandId.Substring(prefix.Length);
            if (action.StartsWith("confirm.", StringComparison.Ordinal))
            {
                if (_review is not { } review || action != "confirm." + review.Token.ToString("N"))
                    return Fail("That confirmation is no longer current. Review the change again.", out result);
                Queue(review.Command, true);
                _view = FollowUpView;
                _page = 1;
                ResetForm(Form.Browse);
                result = CommandResultSnapshot.Success(_status);
                return true;
            }

            // Any other action invalidates the reviewed command before changing the form.
            _review = null;
            if (action == "clear-reply")
            {
                ClearObservation();
                _status = "Cached reply cleared. Request another read explicitly.";
            }
            else if (_form == Form.Browse)
            {
                switch (action)
                {
                    case "membership": ResetForm(Form.Membership); break;
                    case "roles": ResetForm(Form.Roles); break;
                    case "manage-tasks": ResetForm(Form.ManageTasks); break;
                    default:
                        var view = _view;
                        var page = _page;
                        switch (action)
                        {
                            case "info": view = View.Info; break;
                            case "roster": view = View.Roster; break;
                            case "invites": view = View.Invitations; break;
                            case "sent": view = View.Sent; break;
                            case "targets": view = View.Targets; break;
                            case "tasks": view = View.Tasks; break;
                            case "history": view = View.Activity; break;
                            case "previous" when view != View.Info && page > 1: page--; break;
                            case "next" when view != View.Info && page < MaximumPage(view): page++; break;
                            case "refresh": break;
                            default: return Fail("That action or item is unavailable.", out result);
                        }
                        if (view != _view)
                            page = 1;
                        _view = view;
                        _page = page;
                        Queue(ReadCommand(view, page), false);
                        break;
                }
            }
            else if (!TryInvokeEditor(action, out result))
                return false;
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
                Row("Connection", _session.HasValue ? "Connected. The server checks every request against its current rules."
                    : "Tribe menu unavailable. Join a compatible server to use these actions."),
                Row("View", _form == Form.Browse ? ViewLabel(_view, _page) : FormLabel(_form)),
                Row("Request", _queued.HasValue ? "Queued; execution is not confirmed."
                    : _progress.HasValue ? DescribeProgress(_progress.Value) : _status)
            };
            if (_observation is { } observation && _observedQuery is { } query)
            {
                rows.Add(Row("Requested", query.Command));
                rows.Add(Row("Observed (UTC)", observation.ObservedAt.UtcDateTime.ToString(
                    "yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)));
                rows.Add(Row("Server result", observation.Progress == ClientCommandProgress.ServerReported
                    ? observation.Outcome?.ToString() ?? "Not supplied" : "Unknown; no server result was received."));
                rows.Add(Row("Reply", string.IsNullOrEmpty(_reply) ? "No reply text was supplied." : _reply));
                if (observation.ReplyTruncated)
                    rows.Add(Row("Shortened reply", "The received reply was truncated; this is not the complete result."));
                if (query.Mutation)
                    rows.Add(Row("Persistence", "A successful change is applied in memory, pending the next world save; this does not confirm a durable save."));
                rows.Add(Row("Freshness", "Already received replies can become stale after membership or role changes. They expire within 30 seconds and clear on disconnect."));
            }
            else
                rows.Add(Row("Last reply", "No cached reply."));
            if (_form == Form.Browse)
            {
                rows.Add(Row("Paging", _view == View.Info ? "Choose a list to browse one item per reply."
                    : "One item per reply, pages 1–" + MaximumPage(_view).ToString(CultureInfo.InvariantCulture)
                        + "; the total is unknown. Refresh requests the selected read only."));
                if (_observedQuery is { Mutation: true })
                    rows.Add(Row("Follow up", "Refresh selected read requests " + ViewLabel(_view, _page)
                        + " to inspect current state. It does not repeat the change."));
            }
            AddEditorContent(rows);
            rows.Add(Row("Access", "The server decides membership, permissions and visibility for every request. Tribe roles do not grant server administrator access."));
            rows.Add(Row("Unknown results", "A timeout or uncertain send does not mean the change failed. Read the current state before preparing another change; adding a task again can create a duplicate."));
            return new MenuContentSnapshot(DisplayName, "by OpenACAI | Tribe and tasks",
                new[] { new MenuSectionSnapshot("Tribe and tasks", rows) });
        }
    }

    private void Queue(string command, bool mutation)
    {
        // Callbacks capture only the last owner-thread observation; Update rechecks the live session.
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

    private void ClearSessionData()
    {
        _queued = null;
        _pending = null;
        _pendingQuery = null;
        _progress = null;
        _view = View.Info;
        _page = 1;
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
        new("tribe." + _scope.ToString("N") + "." + id, label, description, "Tribe", enabled, false);

    private static int MaximumPage(View view) => view switch {
        View.Info => 1,
        View.Roster => 64,
        View.Invitations => 8192,
        View.Targets => 257,
        _ => 128
    };

    private static string ViewLabel(View view, int page) => (view switch {
        View.Info => "My tribe",
        View.Roster => "Roster",
        View.Invitations => "Invitations",
        View.Sent => "Sent invitations",
        View.Targets => "Online invite targets",
        View.Tasks => "Tasks",
        _ => "Activity"
    }) + (view == View.Info ? "" : " | Item " + page.ToString(CultureInfo.InvariantCulture));

    private static string ReadCommand(View view, int page) => view switch {
        View.Info => "/tribe info",
        View.Roster => "/tribe roster " + page.ToString(CultureInfo.InvariantCulture),
        View.Invitations => "/tribe invites " + page.ToString(CultureInfo.InvariantCulture),
        View.Sent => "/tribe sent " + page.ToString(CultureInfo.InvariantCulture),
        View.Targets => "/invite list " + page.ToString(CultureInfo.InvariantCulture),
        View.Tasks => "/tasks list " + page.ToString(CultureInfo.InvariantCulture),
        _ => "/tribe history " + page.ToString(CultureInfo.InvariantCulture)
    };

    private static string DescribeProgress(ClientCommandProgress progress) => progress switch {
        ClientCommandProgress.Sending => "Sending; execution is not confirmed.",
        ClientCommandProgress.Queued => "Waiting for the server's report; execution is not confirmed.",
        ClientCommandProgress.SendUncertain => "Send result unknown. Waiting for this request; no automatic retry.",
        ClientCommandProgress.ServerReported => "The server reported a result for this request.",
        ClientCommandProgress.TimedOut => "Timed out. Result unknown; no automatic retry was made.",
        _ => "Request became unavailable. Result unknown; no automatic retry was made."
    };

    private static bool Fail(string message, out CommandResultSnapshot result)
    {
        result = CommandResultSnapshot.Failure(message);
        return false;
    }

    private static MenuRowSnapshot Row(string label, string value) => new(label, value, "", "");
}
