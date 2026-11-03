// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Modstoke.Loader;
using Modstoke.Sons;

namespace Modstoke.Client;

internal sealed partial class ClientCompanionProvider : IMenuContentProvider, ICommandProvider,
    IMenuInputProvider
{
    internal const string MenuId = "modstoke.client.companions";
    private readonly object _sync = new();
    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
    private bool _active = true;
    private bool _started;
    private Guid? _session;
    private Query? _queued;
    private Query? _pendingQuery;
    private ClientCommand? _pending;
    private ClientCommandProgress? _progress;
    private Query? _observedQuery;
    private ClientCommandState? _observation;
    private string? _reply;
    private bool _replyShortened;
    private string? _lastRead;
    private double _observationExpires;
    private double _nextSend;
    private string _status = "Waiting for the client to start.";

    private readonly record struct Query(Guid Session, string Command);

    public string Id => MenuId;
    public string DisplayName => "Companions";
    public string Kind => MenuProviderKinds.Status;
    public int Priority => 540;
    public string Source => "Modstoke Client by OpenACAI";
    public string Description => "Community companion queues, receipts and current native order observations";
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
            // Connection reads and sends stay on the game thread; UI callbacks only queue work.
            var available = ClientCommands.TryGetCurrentSession(out var session)
                && ClientCommands.SupportsReplies;
            if ((available && _session != session) || (!available && _session.HasValue))
            {
                ClearSessionData();
                _session = available ? session : null;
                _status = "Ready. Request a read or review a queue change explicitly.";
            }
            if (!available)
            {
                _status = "Companions unavailable. Join a compatible server with command replies.";
                return;
            }
            var now = MonotonicSeconds;
            ExpireObservation(now);
            if (_pending != null && _pendingQuery is { } pendingQuery)
            {
                // Only this exact request handle can supply this operation's observation.
                var state = _pending.State;
                _progress = state.Progress;
                if (state.IsTerminal)
                {
                    if (pendingQuery.Session == session)
                    {
                        _observedQuery = pendingQuery;
                        _observation = state;
                        _reply = state.Reply == null ? null : ClientCommandText.DisplayReply(state.Reply, out _replyShortened);
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
            var sent = ClientCommands.Submit(query.Command, query.Session, out var request);
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
                _status = sent == ClientCommandSendResult.SendUncertain
                    ? "Send result unknown; no request handle was supplied. Read current state explicitly before another change."
                    : "Request was not queued: " + sent + ". No automatic retry was made.";
        }
    }

    public MenuContentSnapshot CreateContent()
    {
        lock (_sync)
        {
            ExpireObservation(MonotonicSeconds);
            var rows = new List<MenuRowSnapshot> {
                Row("Connection", _session.HasValue ? "Connected. The server checks access for every request."
                    : "Join a compatible server with command replies."),
                Row("Selected", OperationLabel(_operation)),
                Row("Request", _queued.HasValue ? "Queued; execution is not confirmed."
                    : _progress.HasValue ? DescribeProgress(_progress.Value) : _status),
                Row("Refresh read", _lastRead ?? "No read selected yet. Refresh never repeats a change.")
            };
            if (_observation is { } observation && _observedQuery is { } query)
            {
                rows.Add(Row("Requested", query.Command));
                rows.Add(Row("Observed (UTC)", observation.ObservedAt.UtcDateTime.ToString(
                    "yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)));
                rows.Add(Row("Server result", observation.Progress == ClientCommandProgress.ServerReported
                    ? observation.Outcome?.ToString() ?? "Not supplied" : "Unknown; no server result was received."));
                rows.Add(Row("Reply", string.IsNullOrEmpty(_reply) ? "No reply text was supplied." : _reply));
                if (observation.ReplyTruncated || _replyShortened)
                    rows.Add(Row("Shortened reply", "This is not the complete reply."));
                rows.Add(Row("Freshness", "Replies expire within 30 seconds and clear on disconnect. Access and native state can change sooner."));
            }
            else
                rows.Add(Row("Last reply", "No cached reply."));
            AddFormContent(rows);
            rows.Add(Row("Access", "The server must enable community queues. Current unmuted, unjailed Steam P2P Tribe leaders/officers only; queues require an attached player observed Alive. Tribe roles grant no admin rights."));
            rows.Add(Row("Temporary queues", "One personal queue in this live session; queues and receipts are not saved. Empty or finished queues expire after 120 seconds, even when polled."));
            rows.Add(Row("Receipt meaning", "NativeEnded means the native order ended, not that resources were delivered. Queue status summarizes current predicates; Queue receipts reads retained jobs; Current order observes native fields. Nothing polls automatically."));
            rows.Add(Row("Unknown results", "Timeouts and uncertain sends leave the result unknown. Read current state before reviewing another change; nothing retries automatically."));
            return new MenuContentSnapshot(DisplayName, "by OpenACAI | Community companion queues",
                new[] { new MenuSectionSnapshot(DisplayName, rows) });
        }
    }

    private void Queue(string command, bool mutation)
    {
        // Update rechecks the live session before consuming this one queued request.
        _queued = new Query(_session!.Value, command);
        if (!mutation)
            _lastRead = command;
        _review = null;
        _scope = Guid.NewGuid();
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

    private bool CanRequest => _active && _started && _session.HasValue && !_queued.HasValue && _pending == null;
    private static double MonotonicSeconds => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    private void ClearSessionData()
    {
        _queued = null;
        _pending = null;
        _pendingQuery = null;
        _progress = null;
        _lastRead = null;
        _networkId = "";
        _orderIndex = "";
        _dropMode = "DropHere";
        _jobId = "";
        _returnOperation = null;
        _returnPage = "1";
        Select(Operation.List);
        ClearObservation();
    }

    private void ClearObservation()
    {
        _observedQuery = null;
        _observation = null;
        _reply = null;
        _replyShortened = false;
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
        new("companions." + _scope.ToString("N") + "." + id, label, description, DisplayName, enabled, false);

    private static string DescribeProgress(ClientCommandProgress progress) => progress switch {
        ClientCommandProgress.Sending => "Sending; execution is not confirmed.",
        ClientCommandProgress.Queued => "Waiting for the server's report; execution is not confirmed.",
        ClientCommandProgress.SendUncertain => "Send result unknown. Waiting for this request; no automatic retry.",
        ClientCommandProgress.ServerReported => "The server reported a command result; delivery is not inferred.",
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
