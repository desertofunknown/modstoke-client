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

internal sealed class ClientDeviceProvider : IMenuContentProvider, ICommandProvider,
    IMenuInputProvider
{
    internal const string MenuId = "modstoke.client.devices";
    private readonly object _sync = new();
    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
    private bool _active = true;
    private bool _started;
    private Guid? _session;
    private Guid _scope = Guid.NewGuid();
    private Operation _operation;
    private string _page = "1";
    private string _networkId = "";
    private string _recipeId = "";
    private Review? _review;
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

    private enum Operation { Scan, Status, Pair, Unpair, Recipe, Print }
    private readonly record struct Query(Guid Session, string Command);
    private sealed record Review(Guid Token, string Command);

    public string Id => MenuId;
    public string DisplayName => "Devices";
    public string Kind => MenuProviderKinds.Status;
    public int Priority => 550;
    public string Source => "Modstoke Client by OpenACAI";
    public string Description => "Simulated Bluetooth links to nearby printers";
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
            // Only the game-thread update reads the connection and submits queued commands.
            var available = ClientCommands.TryGetCurrentSession(out var session)
                && ClientCommands.SupportsReplies;
            if ((available && _session != session) || (!available && _session.HasValue))
            {
                ClearSessionData();
                _session = available ? session : null;
                _status = "Ready. Scan nearby devices or request current link status.";
            }
            if (!available)
            {
                _status = "Devices unavailable. Join a compatible server with command replies.";
                return;
            }
            var now = MonotonicSeconds;
            ExpireObservation(now);
            if (_pending != null && _pendingQuery is { } pendingQuery)
            {
                var state = _pending.State;
                _progress = state.Progress;
                if (state.IsTerminal)
                {
                    if (pendingQuery.Session == session)
                    {
                        _observedQuery = pendingQuery;
                        _observation = state;
                        _reply = state.Reply == null ? null : DisplayText(state.Reply, out _replyShortened);
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
                _status = sent == ClientCommandSendResult.SendUncertain
                    ? "Send result unknown; no request handle was supplied. Read current status before another change."
                    : "Request was not queued: " + sent + ". No automatic retry was made.";
        }
    }

    public MenuContentSnapshot CreateContent()
    {
        lock (_sync)
        {
            ExpireObservation(MonotonicSeconds);
            var rows = new List<MenuRowSnapshot> {
                Row("Connection", _session.HasValue ? "Connected. Device access is checked by the server."
                    : "Join a compatible server with command replies."),
                Row("Selected", OperationLabel(_operation)),
                Row("Request", _queued.HasValue ? "Queued; execution is not confirmed."
                    : _progress.HasValue ? DescribeProgress(_progress.Value) : _status),
                Row("Device link", "Simulated Bluetooth for in-game devices. Request Link status to observe your current pairing."),
                Row("Refresh read", _lastRead ?? "No read selected yet. Refresh never repeats a change.")
            };
            if (_review is { } review)
                rows.Add(Row("Review exact command", review.Command));
            if (IsMutation(_operation))
                rows.Add(Row("Effects", _operation switch {
                    Operation.Pair => "Request a temporary link to this exact nearby printer. The server checks range, current device identity and permission.",
                    Operation.Unpair => "Remove your current simulated device link for this session.",
                    Operation.Recipe => "Request the entered recipe on the currently paired printer. The server checks recipe availability and native control support.",
                    _ => "Request printing on the currently paired printer. Native resource and printer rules apply; unsupported controls are reported by the server."
                }));
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
                rows.Add(Row("Freshness", "Replies expire within 30 seconds and clear on disconnect. Range, pairing and printer state may change sooner."));
            }
            else
                rows.Add(Row("Last reply", "No cached reply. Current pairing has not been observed here."));
            rows.Add(Row("Range and access", "Stay near the printer. Each request rechecks server-observed range, your player session, device and server permissions."));
            rows.Add(Row("Temporary link", "Pairing belongs to this live player session and world. A network ID is not a durable device identity."));
            rows.Add(Row("Native controls", "Read the server reply for supported controls. A command report alone does not prove that printing started or finished."));
            rows.Add(Row("Unknown results", "Timeouts and uncertain sends leave the result unknown. Request Link status before another change; nothing polls or retries automatically."));
            return new MenuContentSnapshot(DisplayName, "by OpenACAI | Simulated Bluetooth device links",
                new[] { new MenuSectionSnapshot(DisplayName, rows) });
        }
    }

    public IReadOnlyList<CommandSnapshot> GetCommands()
    {
        lock (_sync)
        {
            ExpireObservation(MonotonicSeconds);
            var ready = CanRequest;
            var actions = new List<CommandSnapshot>();
            foreach (var operation in Enum.GetValues<Operation>())
                actions.Add(Action("select." + operation, OperationLabel(operation), "Select this action; nothing is sent.", ready));
            if (IsMutation(_operation))
            {
                actions.Add(Action("review", "Review change", "Review the exact command before sending.", ready));
                if (_review is { } review)
                {
                    actions.Add(Action("confirm." + review.Token.ToString("N"), "Confirm change", "Send this reviewed command once.", ready));
                    actions.Add(Action("cancel", "Cancel review", "Discard this review without sending.", ready));
                }
            }
            else
                actions.Add(Action("read", "Request selected read", "Send this scan or status request once.", ready));
            actions.Add(Action("refresh", "Refresh last read", "Repeat only the exact last read; never replay a change.", ready && _lastRead != null));
            return actions;
        }
    }

    public bool TryInvokeCommand(string commandId, out CommandResultSnapshot result)
    {
        lock (_sync)
        {
            ExpireObservation(MonotonicSeconds);
            var prefix = "devices." + _scope.ToString("N") + ".";
            var previousReview = _review;
            _review = null;
            if (commandId == null || !commandId.StartsWith(prefix, StringComparison.Ordinal) || !CanRequest)
                return Fail("That action is stale, unavailable, or another request is queued or pending.", out result);
            var action = commandId.Substring(prefix.Length);
            if (action.StartsWith("confirm.", StringComparison.Ordinal))
            {
                if (previousReview is not { } review || action != "confirm." + review.Token.ToString("N"))
                    return Fail("That confirmation is no longer current. Review the change again.", out result);
                Queue(review.Command, true);
                Select(Operation.Status);
            }
            else
            {
                switch (action)
                {
                    case "cancel":
                        _scope = Guid.NewGuid();
                        _status = "Review cancelled; nothing sent.";
                        break;
                    case "refresh":
                        if (_lastRead == null)
                            return Fail("No read is available to refresh.", out result);
                        Queue(_lastRead, false);
                        break;
                    case "read":
                    case "review":
                        if ((action == "review") != IsMutation(_operation))
                            return Fail("Select a read or change first.", out result);
                        if (!TryBuildCommand(out var command, out var error))
                            return Fail(error, out result);
                        if (action == "read")
                            Queue(command, false);
                        else
                        {
                            _review = new Review(Guid.NewGuid(), command);
                            _status = "Review the exact command and effects, then confirm to send once.";
                        }
                        break;
                    default:
                        Operation? selected = null;
                        foreach (var operation in Enum.GetValues<Operation>())
                            if (action == "select." + operation)
                                selected = operation;
                        if (!selected.HasValue)
                            return Fail("That action is unavailable.", out result);
                        Select(selected.Value);
                        _status = "Selected " + OperationLabel(_operation) + ". Nothing sent.";
                        break;
                }
            }
            result = CommandResultSnapshot.Success(_status);
            return true;
        }
    }

    public MenuInputSnapshot GetInputs()
    {
        lock (_sync)
        {
            var fields = new List<MenuInputFieldSnapshot>();
            if (_operation == Operation.Scan)
                fields.Add(new("page", "Scan page", _page, "1–64; one nearby printer per reply. Request each page explicitly.", 2, CanRequest));
            if (_operation == Operation.Pair)
                fields.Add(new("network-id", "Device network ID", _networkId,
                    "Copy the full positive decimal network ID from Scan nearby devices; never use a name or page number.", 20, CanRequest));
            if (_operation == Operation.Recipe)
                fields.Add(new("recipe-id", "Recipe ID", _recipeId,
                    "Native recipe ID, including zero. The server checks availability on your current paired printer.", 10, CanRequest));
            return new MenuInputSnapshot(_scope, fields);
        }
    }

    public bool TrySetInput(Guid scope, string fieldId, string value, out CommandResultSnapshot result)
    {
        lock (_sync)
        {
            // Edits, including rejected or stale edits, invalidate a reviewed command.
            _review = null;
            if (scope != _scope || !CanRequest)
                return Fail("This form is stale or a request is still pending.", out result);
            var limit = fieldId switch {
                "page" when _operation == Operation.Scan => 2,
                "network-id" when _operation == Operation.Pair => 20,
                "recipe-id" when _operation == Operation.Recipe => 10,
                _ => 0
            };
            if (limit == 0)
                return Fail("That field is unavailable in this form.", out result);
            if (value == null || value.Length > limit)
                return Fail("Enter decimal digits within the field length limit.", out result);
            foreach (var c in value)
                if (c is < '0' or > '9')
                    return Fail("Use decimal digits only, without spaces or formatting.", out result);
            switch (fieldId)
            {
                case "page": _page = value; break;
                case "network-id": _networkId = value; break;
                case "recipe-id": _recipeId = value; break;
            }
            _status = "Draft updated. Request the read or review the change explicitly.";
            result = CommandResultSnapshot.Success(_status);
            return true;
        }
    }

    private bool TryBuildCommand(out string command, out string error)
    {
        command = "";
        error = "";
        switch (_operation)
        {
            case Operation.Scan:
                if (!int.TryParse(_page, NumberStyles.None, CultureInfo.InvariantCulture, out var page) || page is < 1 or > 64)
                    error = "Enter a scan page from 1 to 64.";
                else
                    command = "/device scan " + page.ToString(CultureInfo.InvariantCulture);
                break;
            case Operation.Pair:
                if (!ulong.TryParse(_networkId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id == 0)
                    error = "Enter the full positive decimal network ID from a current device scan.";
                else
                    command = "/device pair " + id.ToString(CultureInfo.InvariantCulture);
                break;
            case Operation.Recipe:
                if (!int.TryParse(_recipeId, NumberStyles.None, CultureInfo.InvariantCulture, out var recipe) || recipe < 0)
                    error = "Enter a recipe ID from 0 to 2147483647. The server checks which recipes exist.";
                else
                    command = "/device recipe " + recipe.ToString(CultureInfo.InvariantCulture);
                break;
            case Operation.Status: command = "/device status"; break;
            case Operation.Unpair: command = "/device unpair"; break;
            case Operation.Print: command = "/device print"; break;
        }
        return command.Length != 0;
    }

    private void Queue(string command, bool mutation)
    {
        _queued = new Query(_session!.Value, command);
        if (!mutation)
            _lastRead = command;
        _review = null;
        _scope = Guid.NewGuid();
        ClearObservation();
        _status = "Request queued; execution is not confirmed.";
    }

    private void Select(Operation operation)
    {
        _operation = operation;
        _review = null;
        _scope = Guid.NewGuid();
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
        _page = "1";
        _networkId = "";
        _recipeId = "";
        _nextSend = 0;
        Select(Operation.Scan);
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
        new("devices." + _scope.ToString("N") + "." + id, label, description, DisplayName, enabled, false);

    private static bool IsMutation(Operation operation) => operation is Operation.Pair or Operation.Unpair or Operation.Recipe or Operation.Print;
    private static string OperationLabel(Operation operation) => operation switch {
        Operation.Scan => "Scan nearby devices",
        Operation.Status => "Link status",
        Operation.Pair => "Pair device",
        Operation.Unpair => "Unpair device",
        Operation.Recipe => "Select printer recipe",
        _ => "Print"
    };

    private static string DescribeProgress(ClientCommandProgress progress) => progress switch {
        ClientCommandProgress.Sending => "Sending; execution is not confirmed.",
        ClientCommandProgress.Queued => "Waiting for the server's report; execution is not confirmed.",
        ClientCommandProgress.SendUncertain => "Send result unknown. Waiting for this request; no automatic retry.",
        ClientCommandProgress.ServerReported => "The server reported a command result. Read the reply for device state and supported controls.",
        ClientCommandProgress.TimedOut => "Timed out. Result unknown; no automatic retry was made.",
        _ => "Request became unavailable. Result unknown; no automatic retry was made."
    };

    private static string DisplayText(string value, out bool shortened)
    {
        var text = new StringBuilder();
        var count = 0;
        shortened = false;
        foreach (var rune in value.EnumerateRunes())
        {
            if (count++ == 512)
            {
                shortened = true;
                break;
            }
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                text.Append(' ');
            else if (rune.Value == '<')
                text.Append('‹');
            else if (rune.Value == '>')
                text.Append('›');
            else
                text.Append(rune.ToString());
        }
        return text.ToString();
    }

    private static bool Fail(string message, out CommandResultSnapshot result)
    {
        result = CommandResultSnapshot.Failure(message);
        return false;
    }

    private static MenuRowSnapshot Row(string label, string value) => new(label, value, "", "");
}
