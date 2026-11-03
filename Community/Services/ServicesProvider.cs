// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Modstoke.Loader;
using Modstoke.Sons;

namespace Modstoke.Client;

internal sealed partial class ClientServicesProvider : IMenuContentProvider, ICommandProvider,
    IMenuInputProvider
{
    internal const string MenuId = "modstoke.client.services";
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
    private string? _lastRead;
    private double _observationExpires;
    private double _nextSend;
    private string _status = "Waiting for the client to start.";

    private readonly record struct Query(Guid Session, string Command, bool Mutation, bool Travel);

    public string Id => MenuId;
    public string DisplayName => "Services";
    public string Kind => MenuProviderKinds.Status;
    public int Priority => 530;
    public string Source => "Modstoke Client by OpenACAI";
    public string Description => "Campaigns, Tribe points, travel and facilities";
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
            // Live connection reads and command sends belong only to this owner-thread path.
            var available = ClientCommands.TryGetCurrentSession(out var session)
                && ClientCommands.SupportsReplies;
            if ((available && _session != session) || (!available && _session.HasValue))
            {
                ClearSessionData();
                _session = available ? session : null;
                _status = "Ready. Select a service and explicitly request a read or review a change.";
            }
            if (!available)
            {
                _status = "Services unavailable. Join a compatible server to use these actions.";
                return;
            }
            var now = MonotonicSeconds;
            ExpireObservation(now);
            if (_pending != null && _pendingQuery is { } pendingQuery)
            {
                // Observe the exact request handle, never a sequence number or another request's reply.
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
                    ? "Send result unknown; no request handle was supplied. Check current state explicitly; no automatic retry."
                    : "Request was not queued: " + sent + ". No automatic retry was made.";
        }
    }

    public MenuContentSnapshot CreateContent()
    {
        lock (_sync)
        {
            ExpireObservation(MonotonicSeconds);
            var rows = new List<MenuRowSnapshot> {
                Row("Connection", _session.HasValue ? "Connected. The server checks current access for each request."
                    : "Services unavailable. Join a compatible server to use these actions."),
                Row("Selected", OperationLabel(_operation)),
                Row("Request", _queued.HasValue ? "Queued; execution is not confirmed."
                    : _progress.HasValue ? DescribeProgress(_progress.Value) : _status),
                Row("Refresh read", _lastRead ?? "No read selected yet. Refresh never sends a change.")
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
                if (query.Mutation && !query.Travel)
                    rows.Add(Row("Persistence", "A successful change is applied in memory, pending the next world save. This does not confirm a durable save."));
                if (query.Travel)
                    rows.Add(Row("Travel request", "A successful command response can still mean server-pending or queued travel. Request history or status explicitly; no arrival is inferred."));
                rows.Add(Row("Freshness", "Replies expire within 30 seconds and clear on disconnect. Access and world state can change sooner."));
            }
            else
                rows.Add(Row("Last reply", "No cached reply."));
            AddFormContent(rows);
            rows.Add(Row("Access", "The server decides membership, roles, visibility and eligibility. Reply text does not grant permission."));
            rows.Add(Row("Unknown results", "A timeout or uncertain send is an unknown result, not a failed change. Read current state before reviewing another change. Nothing retries or polls automatically."));
            return new MenuContentSnapshot(DisplayName, "by OpenACAI | Campaigns, Tribe points, travel and facilities",
                new[] { new MenuSectionSnapshot(GroupLabel(_group), rows) });
        }
    }

    private void Queue(string command, bool mutation, bool travel)
    {
        // Callback state is cached. Update checks the live session again before submitting.
        _queued = new Query(_session!.Value, command, mutation, travel);
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
        _campaignId = "";
        _destinationId = "";
        _requestId = "";
        _facilityId = "";
        Select(Operation.CampaignList);
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
        new("services." + _scope.ToString("N") + "." + id, label, description, GroupLabel(_group), enabled, false);

    private static string DescribeProgress(ClientCommandProgress progress) => progress switch {
        ClientCommandProgress.Sending => "Sending; execution is not confirmed.",
        ClientCommandProgress.Queued => "Waiting for the server's report; execution is not confirmed.",
        ClientCommandProgress.SendUncertain => "Send result unknown. Waiting for this request; no automatic retry.",
        ClientCommandProgress.ServerReported => "The server reported a command result; travel arrival is not inferred.",
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
