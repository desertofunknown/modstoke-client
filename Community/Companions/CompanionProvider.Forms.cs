// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using Modstoke.Loader;
using Modstoke.Sons;

namespace Modstoke.Client;

internal sealed partial class ClientCompanionProvider
{
    private Guid _scope = Guid.NewGuid();
    private Operation _operation = Operation.List;
    private Operation? _returnOperation;
    private string _returnPage = "1";
    private string _networkId = "";
    private string _orderIndex = "";
    private string _dropMode = "DropHere";
    private string _jobId = "";
    private string _page = "1";
    private Review? _review;

    private enum Operation { List, Orders, Status, Receipts, Current, Help, Open, Add, Remove, Close }
    private sealed record Review(Guid Token, string Command);

    public IReadOnlyList<CommandSnapshot> GetCommands()
    {
        lock (_sync)
        {
            ExpireObservation(MonotonicSeconds);
            var ready = CanRequest;
            var actions = new List<CommandSnapshot>();
            foreach (var operation in Enum.GetValues<Operation>())
                actions.Add(Action("select." + operation, OperationLabel(operation),
                    "Select this action; selection sends nothing.", ready));
            if (UsesPage(_operation))
            {
                var validPage = TryPage(out var page);
                actions.Add(Action("page-previous", "Read previous page", "Request the previous item of this selected read.", ready && validPage && page > 1));
                actions.Add(Action("page-next", "Read next page", "Request the next item; the server decides whether it exists.", ready && validPage && page < MaximumPage));
            }
            if (_operation == Operation.Add)
                foreach (var mode in Enum.GetValues<CompanionDropMode>())
                    actions.Add(Action("drop." + mode, (_dropMode == mode.ToString() ? "Selected: " : "Use ") + mode,
                        "Set the drop-mode draft only. Review the order separately before sending.", ready));
            if (SourceOperation is { } source)
                actions.Add(Action("source", "Prepare " + OperationLabel(source),
                    "Open the matching source read with retained identifiers; then explicitly request it.", ready));
            if (_returnOperation is { } returnOperation)
                actions.Add(Action("return-draft", "Return to " + OperationLabel(returnOperation),
                    "Return to the original draft with its entered values. Nothing is sent.", ready));
            if (IsMutation(_operation))
            {
                actions.Add(Action("review", "Review change", "Review the exact command and queue effects before sending.", ready));
                if (_review is { } review)
                {
                    actions.Add(Action("confirm." + review.Token.ToString("N"), "Confirm change", "Send this reviewed command once.", ready));
                    actions.Add(Action("cancel", "Cancel review", "Discard this review without sending.", ready));
                }
            }
            else
                actions.Add(Action("read", "Request selected read", "Send the selected read with these inputs once.", ready));
            actions.Add(Action("refresh", "Refresh last read", "Send only the exact last read again; never replay a change.", ready && _lastRead != null));
            actions.Add(Action("clear-reply", "Clear reply", "Discard the cached reply.", ready && _observation != null));
            return actions;
        }
    }

    public bool TryInvokeCommand(string commandId, out CommandResultSnapshot result)
    {
        lock (_sync)
        {
            ExpireObservation(MonotonicSeconds);
            var prefix = "companions." + _scope.ToString("N") + ".";
            if (commandId == null || !commandId.StartsWith(prefix, StringComparison.Ordinal) || !CanRequest)
            {
                _review = null;
                return Fail("That action is stale, unavailable, or another request is queued or pending.", out result);
            }
            var action = commandId.Substring(prefix.Length);
            // Consume even an invalid action's review. Only the exact confirm token can send it.
            var previousReview = _review;
            _review = null;
            if (action.StartsWith("confirm.", StringComparison.Ordinal))
            {
                if (previousReview is not { } review || action != "confirm." + review.Token.ToString("N"))
                    return Fail("That confirmation is no longer current. Review the change again.", out result);
                Queue(review.Command, true);
                _returnOperation = null;
                Select(Operation.Status);
            }
            else
            {
                switch (action)
                {
                    case "page-previous":
                    case "page-next":
                        if (!TryPage(out var currentPage))
                            return Fail("Enter a valid page before paging.", out result);
                        var nextPage = (long)currentPage + (action == "page-next" ? 1 : -1);
                        if (nextPage < 1 || nextPage > MaximumPage)
                            return Fail("That page is outside this read's range.", out result);
                        var previousPage = _page;
                        _page = nextPage.ToString(CultureInfo.InvariantCulture);
                        if (!TryBuildCommand(out var pageCommand, out var pageError))
                        {
                            _page = previousPage;
                            return Fail(pageError, out result);
                        }
                        Queue(pageCommand, false);
                        break;
                    case "source":
                        if (SourceOperation is not { } source)
                            return Fail("This action has no source read.", out result);
                        if (!_returnOperation.HasValue)
                        {
                            _returnOperation = _operation;
                            _returnPage = _page;
                        }
                        Select(source);
                        _status = "Prepared " + OperationLabel(source) + ". Request the read explicitly; identifiers were retained.";
                        break;
                    case "return-draft":
                        if (_returnOperation is not { } returnOperation)
                            return Fail("There is no source draft to return to.", out result);
                        Select(returnOperation);
                        _page = _returnPage;
                        _returnOperation = null;
                        _status = "Returned to the retained draft. Nothing sent; review changes again before confirming.";
                        break;
                    case "cancel":
                        _scope = Guid.NewGuid();
                        _status = "Review cancelled; nothing sent.";
                        break;
                    case "clear-reply":
                        ClearObservation();
                        _scope = Guid.NewGuid();
                        _status = "Cached reply cleared. Request another read explicitly.";
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
                        if (_operation == Operation.Add && action.StartsWith("drop.", StringComparison.Ordinal))
                        {
                            CompanionDropMode? chosenMode = null;
                            foreach (var mode in Enum.GetValues<CompanionDropMode>())
                                if (action == "drop." + mode) chosenMode = mode;
                            if (!chosenMode.HasValue)
                                return Fail("That drop mode is unavailable.", out result);
                            _dropMode = chosenMode.Value.ToString();
                            _scope = Guid.NewGuid();
                            _status = "Drop mode selected locally. Review the order before sending.";
                            break;
                        }
                        Operation? selected = null;
                        foreach (var operation in Enum.GetValues<Operation>())
                            if (action == "select." + operation)
                                selected = operation;
                        if (!selected.HasValue)
                            return Fail("That action is unavailable.", out result);
                        _returnOperation = null;
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
            if (UsesNetworkId(_operation))
                fields.Add(new("network-id", "Companion network ID", _networkId,
                    "Copy the current nonzero network ID from Companion list; this is not a job ID or durable identity.", 20, CanRequest));
            if (_operation == Operation.Add)
            {
                fields.Add(new("order-index", "Order index", _orderIndex,
                    "Copy the nonnegative index from Available orders for your queue's companion; this is not the page number.", 10, CanRequest));
                fields.Add(new("drop-mode", "Drop mode", _dropMode,
                    "DropHere, GiveToMe, FollowMe, FillHolder or FillLogSled (0–4).", 11, CanRequest));
            }
            if (_operation == Operation.Remove)
                fields.Add(new("job-id", "Pending job ID", _jobId,
                    "Copy the full hyphenated job GUID from Queue receipts; never enter a companion network ID.", 36, CanRequest));
            if (UsesPage(_operation))
                fields.Add(new("page", "Page", _page,
                    _operation == Operation.Help ? "1 or 2; choose a help page."
                        : _operation == Operation.Current ? "1: native order; 2: follow/drop targets; 3: timing and Get configuration."
                        : "Positive page number; one item per reply. The server checks the available count.", 10, CanRequest));
            return new MenuInputSnapshot(_scope, fields);
        }
    }

    public bool TrySetInput(Guid scope, string fieldId, string value, out CommandResultSnapshot result)
    {
        lock (_sync)
        {
            // Any edit, including rejected or stale input, invalidates an outstanding review.
            _review = null;
            if (scope != _scope || !CanRequest)
                return Fail("This form is stale or a request is still pending.", out result);
            var limit = fieldId switch {
                "network-id" when UsesNetworkId(_operation) => 20,
                "order-index" when _operation == Operation.Add => 10,
                "drop-mode" when _operation == Operation.Add => 11,
                "job-id" when _operation == Operation.Remove => 36,
                "page" when UsesPage(_operation) => 10,
                _ => 0
            };
            if (limit == 0)
                return Fail("That field is unavailable in this form.", out result);
            if (value == null || value.Length > limit || !ClientCommandText.IsSafeText(value))
                return Fail("Use plain single-line text within the field limit, without controls or formatting.", out result);
            switch (fieldId)
            {
                case "network-id": _networkId = value; break;
                case "order-index": _orderIndex = value; break;
                case "drop-mode": _dropMode = value; break;
                case "job-id": _jobId = value; break;
                case "page": _page = value; break;
            }
            _status = "Draft updated for this session. Request the read or review the change explicitly.";
            result = CommandResultSnapshot.Success(_status);
            return true;
        }
    }

    private bool TryBuildCommand(out string command, out string error)
    {
        command = "";
        error = "";
        ulong networkId = 0;
        if (UsesNetworkId(_operation) && (!ulong.TryParse(_networkId.Trim(), NumberStyles.None,
            CultureInfo.InvariantCulture, out networkId) || networkId == 0))
        {
            error = "Enter the current nonzero UInt64 companion network ID from Companion list.";
            return false;
        }
        var page = 1;
        if (UsesPage(_operation) && !TryPage(out page))
        {
            error = _operation == Operation.Help ? "Choose help page 1 or 2."
                : _operation == Operation.Current ? "Choose current-order page 1, 2 or 3."
                : "Enter a positive page number up to 2147483647.";
            return false;
        }
        var orderIndex = 0;
        var dropMode = CompanionDropMode.DropHere;
        if (_operation == Operation.Add)
        {
            if (!int.TryParse(_orderIndex.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out orderIndex) || orderIndex < 0)
            {
                error = "Enter a nonnegative order index from Available orders, up to 2147483647.";
                return false;
            }
            var dropText = _dropMode.Trim();
            if (!Enum.TryParse(dropText, true, out dropMode)
                || !Enum.IsDefined(typeof(CompanionDropMode), dropMode)
                || !(string.Equals(dropText, dropMode.ToString(), StringComparison.OrdinalIgnoreCase)
                    || (dropText.Length == 1 && dropText[0] >= '0' && dropText[0] <= '4')))
            {
                error = "Choose DropHere, GiveToMe, FollowMe, FillHolder or FillLogSled (0–4).";
                return false;
            }
        }
        var jobId = Guid.Empty;
        if (_operation == Operation.Remove && !Guid.TryParseExact(_jobId.Trim(), "D", out jobId))
        {
            error = "Enter the full hyphenated job GUID from Queue receipts; network IDs and GUID prefixes are not accepted.";
            return false;
        }
        command = _operation switch {
            Operation.Help => "/companion help " + page.ToString(CultureInfo.InvariantCulture),
            Operation.List => "/companion list " + page.ToString(CultureInfo.InvariantCulture),
            Operation.Orders => "/companion orders " + networkId.ToString(CultureInfo.InvariantCulture) + " " + page.ToString(CultureInfo.InvariantCulture),
            Operation.Status => "/companion status",
            Operation.Receipts => "/companion status " + page.ToString(CultureInfo.InvariantCulture),
            Operation.Current => "/companion current " + page.ToString(CultureInfo.InvariantCulture),
            Operation.Open => "/companion open " + networkId.ToString(CultureInfo.InvariantCulture),
            Operation.Add => "/companion add " + orderIndex.ToString(CultureInfo.InvariantCulture) + " " + dropMode,
            Operation.Remove => "/companion remove " + jobId.ToString("D"),
            Operation.Close => "/companion close",
            _ => ""
        };
        if (!ClientCommandText.IsCommand(command))
        {
            error = "The command must fit in 256 UTF-8 bytes and contain only plain single-line text.";
            return false;
        }
        return true;
    }

    private void AddFormContent(List<MenuRowSnapshot> rows)
    {
        if (_review is { } review)
            rows.Add(Row("Review exact command", review.Command));
        if (IsMutation(_operation))
            rows.Add(Row("Effects", _operation switch {
                Operation.Open => "Open your transient queue for this companion in the current session. The server checks eligibility and queue availability; this does not grant durable ownership.",
                Operation.Add => "Add this native order to your current queue once. The index must match that queue's companion. Acceptance or a native receipt does not prove delivery.",
                Operation.Remove => "Remove only this pending job from your current queue. A running native order cannot be cancelled here.",
                _ => "Close your current queue and remove its pending jobs. Running native work continues."
            }));
        if (_operation == Operation.Help)
            rows.Add(Row("Limits", "Up to 8 waiting jobs and 32 receipts per queue; 4 SDK queues globally, at most 3 remote queues. Help page 2 describes access and expiry."));
        if (_operation == Operation.Status)
        {
            rows.Add(Row("Queue observation", "Request the server's native idle predicate, pending count, tracked active and next receipts, next-order match and one-hour waiting expiry. This read never submits work."));
            rows.Add(Row("Unavailable state", "Idle false may mean native state is unavailable; it does not prove the companion is busy. An order mismatch can also mean unavailable data. A successful read does not authorize dispatch or prove execution or delivery."));
        }
        if (_operation == Operation.Receipts)
            rows.Add(Row("Receipt pages", "One retained receipt per page, including its full job ID, native status and detail. Queue status summarizes the active and first waiting receipt; pending counts can include jobs awaiting expiry processing."));
        if (_operation == Operation.Current)
        {
            rows.Add(Row("Current order pages", "1: native order and flags. 2: follow/drop target positions and availability. 3: raw timing and Get configuration. Each page is a separate server observation of the companion in your existing queue."));
            rows.Add(Row("Observation limits", "No queue is opened and no order is submitted. Names may be shortened. An absent order can coexist with targets; positions and flags do not prove execution, reachability, capacity or delivery. Native expiry is configuration, not time remaining."));
        }
        if (_operation is Operation.List or Operation.Orders or Operation.Open)
            rows.Add(Row("Current IDs", "Copy network IDs and order indices from fresh reads in this live session. They are not saved identities or proof of queue ownership."));
    }

    private void Select(Operation operation)
    {
        _operation = operation;
        _page = "1";
        _review = null;
        _scope = Guid.NewGuid();
    }

    private static bool IsMutation(Operation operation) => operation is Operation.Open or Operation.Add or Operation.Remove or Operation.Close;
    private int MaximumPage => _operation == Operation.Help ? 2 : _operation == Operation.Current ? 3 : int.MaxValue;
    private bool TryPage(out int page) => int.TryParse(_page.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out page)
        && UsesPage(_operation) && page >= 1 && page <= MaximumPage;
    private Operation? SourceOperation => _operation switch {
        Operation.Open or Operation.Orders => Operation.List,
        Operation.Add => Operation.Orders,
        Operation.Remove or Operation.Status => Operation.Receipts,
        Operation.Close or Operation.Receipts or Operation.Current => Operation.Status,
        _ => null
    };
    private static bool UsesNetworkId(Operation operation) => operation is Operation.Orders or Operation.Open;
    private static bool UsesPage(Operation operation) => operation is Operation.List or Operation.Orders or Operation.Receipts or Operation.Current or Operation.Help;
    private static string OperationLabel(Operation operation) => operation switch {
        Operation.List => "Companion list",
        Operation.Orders => "Available orders",
        Operation.Status => "Queue status",
        Operation.Receipts => "Queue receipts",
        Operation.Current => "Current order",
        Operation.Help => "Companion help",
        Operation.Open => "Open queue",
        Operation.Add => "Add order",
        Operation.Remove => "Remove pending job",
        _ => "Close queue"
    };
}
