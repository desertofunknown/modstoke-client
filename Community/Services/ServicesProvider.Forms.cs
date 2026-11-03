// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using Modstoke.Loader;

namespace Modstoke.Client;

internal sealed partial class ClientServicesProvider
{
    private Guid _scope = Guid.NewGuid();
    private Group _group;
    private Operation _operation;
    private string _campaignId = "";
    private string _destinationId = "";
    private string _requestId = "";
    private string _facilityId = "";
    private string _page = "1";
    private string _stage = "1";
    private Review? _review;

    private enum Group { Campaigns, Points, Travel, Facilities }
    private enum Operation {
        CampaignList, CampaignProgress, CampaignInfo, CampaignRoute, CampaignStart, CampaignVisit,
        PointsBalance, PointsHistory, PointsReward, PointsClaim,
        TravelList, TravelInfo, TravelHistory, TravelStatus, TravelGo,
        FacilityList, FacilityInfo, FacilityHelp
    }
    private sealed record Review(Guid Token, string Command, Operation Operation);

    public IReadOnlyList<CommandSnapshot> GetCommands()
    {
        lock (_sync)
        {
            ExpireObservation(MonotonicSeconds);
            var ready = CanRequest;
            var actions = new List<CommandSnapshot> {
                Action("campaigns", "Campaigns", "Browse public campaigns and your Tribe's progress.", ready),
                Action("points", "Tribe points", "Read or claim virtual Tribe points.", ready),
                Action("travel", "Travel", "Browse destinations and inspect your travel requests.", ready),
                Action("facilities", "Facilities", "Read enabled facility policies assigned to your current Tribe.", ready)
            };
            foreach (var operation in Operations(_group))
                actions.Add(Action("select." + operation, OperationLabel(operation),
                    "Select this action; selection sends nothing.", ready));
            if (MaximumPage(_operation) > 0)
            {
                var validPage = TryPage(out var page);
                actions.Add(Action("page-previous", "Read previous page", "Request one earlier page of this selected read.", ready && validPage && page > 1));
                actions.Add(Action("page-next", "Read next page", "Request one later page; available items are decided by the server.", ready && validPage && page < MaximumPage(_operation)));
            }
            if (UsesStage(_operation))
            {
                var validStage = int.TryParse(_stage, NumberStyles.None, CultureInfo.InvariantCulture, out var stage) && stage is >= 1 and <= 8;
                actions.Add(Action("stage-previous", "Previous stage", "Change the draft stage only; nothing is sent.", ready && validStage && stage > 1));
                actions.Add(Action("stage-next", "Next stage", "Change the draft stage only; nothing is sent.", ready && validStage && stage < 8));
            }
            if (SourceRead() is { } source)
                actions.Add(Action("source-read", "Read " + source.Label, "Read the source IDs without discarding this form or its draft.", ready));
            if (UsesCampaignId(_operation) || MaximumPage(_operation) > 0 || _operation is Operation.TravelInfo or Operation.TravelGo or Operation.TravelStatus or Operation.FacilityInfo)
                actions.Add(Action("reset-draft", "Reset this form", "Clear this form's inputs and review locally; no command is sent.", ready));
            if (IsMutation(_operation))
            {
                actions.Add(Action("review", ReviewLabel(_operation), "Validate and review the exact command and its effects.", ready));
                if (_review is { } review)
                {
                    actions.Add(Action("confirm." + review.Token.ToString("N"), ConfirmLabel(review.Operation), "Send the reviewed command once.", ready));
                    actions.Add(Action("cancel", "Cancel review", "Discard this confirmation without sending.", ready));
                }
            }
            else
                actions.Add(Action("read", ReadLabel(_operation), "Send the selected read with these inputs once.", ready));
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
            var prefix = "services." + _scope.ToString("N") + ".";
            if (commandId == null || !commandId.StartsWith(prefix, StringComparison.Ordinal) || !CanRequest)
                return Fail("That action is stale, unavailable, or another request is queued or pending.", out result);
            var action = commandId.Substring(prefix.Length);
            if (action.StartsWith("confirm.", StringComparison.Ordinal))
            {
                if (_review is not { } review || action != "confirm." + review.Token.ToString("N"))
                    return Fail("That confirmation is no longer current. Review the change again.", out result);
                Queue(review.Command, true, review.Operation == Operation.TravelGo);
                // Keep travel history reachable without repeating go or reusing a destination as a request ID.
                Select(review.Operation switch {
                    Operation.TravelGo => Operation.TravelHistory,
                    Operation.PointsClaim => Operation.PointsBalance,
                    _ => Operation.CampaignProgress
                });
            }
            else
            {
                // Every other action, including an invalid action, consumes the previous review.
                _review = null;
                switch (action)
                {
                    case "page-previous":
                    case "page-next":
                        if (!TryPage(out var currentPage))
                            return Fail("Enter a valid page before paging.", out result);
                        var nextPage = currentPage + (action == "page-next" ? 1 : -1);
                        if (nextPage < 1 || nextPage > MaximumPage(_operation))
                            return Fail("That page is outside this read's range.", out result);
                        var previousPage = _page;
                        _page = nextPage.ToString(CultureInfo.InvariantCulture);
                        if (!TryBuildCommand(out var pageCommand, out var pageError))
                        {
                            _page = previousPage;
                            return Fail(pageError, out result);
                        }
                        Queue(pageCommand, false, false);
                        break;
                    case "stage-previous":
                    case "stage-next":
                        if (!UsesStage(_operation) || !int.TryParse(_stage, NumberStyles.None, CultureInfo.InvariantCulture, out var currentStage))
                            return Fail("Enter a valid stage before stepping.", out result);
                        var nextStage = currentStage + (action == "stage-next" ? 1 : -1);
                        if (currentStage is < 1 or > 8 || nextStage is < 1 or > 8)
                            return Fail("Choose a stage from 1 to 8.", out result);
                        _stage = nextStage.ToString(CultureInfo.InvariantCulture);
                        _scope = Guid.NewGuid();
                        _status = "Stage draft updated. Request the route or review the check-in explicitly.";
                        break;
                    case "source-read":
                        if (SourceRead() is not { } source)
                            return Fail("This form has no source read.", out result);
                        Queue(source.Command, false, false);
                        break;
                    case "reset-draft":
                        if (UsesCampaignId(_operation)) _campaignId = "";
                        if (_operation is Operation.TravelInfo or Operation.TravelGo) _destinationId = "";
                        if (_operation == Operation.TravelStatus) _requestId = "";
                        if (_operation == Operation.FacilityInfo) _facilityId = "";
                        _page = "1";
                        _stage = "1";
                        _scope = Guid.NewGuid();
                        _status = "This form was reset. Nothing sent; other service identifiers are retained.";
                        break;
                    case "campaigns": Select(Operation.CampaignList); SelectedStatus(); break;
                    case "points": Select(Operation.PointsBalance); SelectedStatus(); break;
                    case "travel": Select(Operation.TravelList); SelectedStatus(); break;
                    case "facilities": Select(Operation.FacilityList); SelectedStatus(); break;
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
                        Queue(_lastRead, false, false);
                        break;
                    case "read":
                    case "review":
                        if ((action == "review") != IsMutation(_operation))
                            return Fail("Select a read or change first.", out result);
                        if (!TryBuildCommand(out var command, out var error))
                            return Fail(error, out result);
                        if (action == "read")
                            Queue(command, false, false);
                        else
                        {
                            _review = new Review(Guid.NewGuid(), command, _operation);
                            _status = "Review the exact command and effects, then confirm to send once.";
                        }
                        break;
                    default:
                        Operation? selected = null;
                        foreach (var operation in Operations(_group))
                            if (action == "select." + operation)
                                selected = operation;
                        if (!selected.HasValue)
                            return Fail("That action is unavailable in this group.", out result);
                        Select(selected.Value);
                        SelectedStatus();
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
            if (UsesCampaignId(_operation))
                fields.Add(new("campaign-id", "Campaign ID", _campaignId,
                    "From Campaign list: full GUID or unique 8–32 hexadecimal prefix; never a name or account ID.", 36, CanRequest));
            if (_operation is Operation.TravelInfo or Operation.TravelGo)
                fields.Add(new("destination-id", "Destination ID", _destinationId,
                    "From Travel destinations: full GUID or unique 8–32 hexadecimal prefix; never a request or account ID.", 36, CanRequest));
            if (_operation == Operation.TravelStatus)
                fields.Add(new("request-id", "Travel request ID", _requestId,
                    "Copy a request ID from your travel reply or history; never use a destination ID.", 36, CanRequest));
            if (_operation == Operation.FacilityInfo)
                fields.Add(new("facility-id", "Facility policy ID", _facilityId,
                    "From Tribe facilities: full GUID or unique 8–32 hexadecimal prefix; never a name or account ID.", 36, CanRequest));
            if (MaximumPage(_operation) > 0)
                fields.Add(new("page", "Page", _page, "1–" + MaximumPage(_operation).ToString(CultureInfo.InvariantCulture)
                    + (_operation == Operation.FacilityHelp ? "; choose a help page."
                        : "; the total is unknown. One item per reply."), 4, CanRequest));
            if (UsesStage(_operation))
                fields.Add(new("stage", "Stage", _stage, "1–8; the server decides which stages exist and are eligible.", 1, CanRequest));
            return new MenuInputSnapshot(_scope, fields);
        }
    }

    public bool TrySetInput(Guid scope, string fieldId, string value, out CommandResultSnapshot result)
    {
        lock (_sync)
        {
            if (scope != _scope || !CanRequest)
                return Fail("This form is stale or a request is still pending.", out result);
            // Even rejected edits invalidate the reviewed command.
            _review = null;
            var limit = fieldId switch {
                "campaign-id" when UsesCampaignId(_operation) => 36,
                "destination-id" when _operation is Operation.TravelInfo or Operation.TravelGo => 36,
                "request-id" when _operation == Operation.TravelStatus => 36,
                "facility-id" when _operation == Operation.FacilityInfo => 36,
                "page" when MaximumPage(_operation) > 0 => 4,
                "stage" when UsesStage(_operation) => 1,
                _ => 0
            };
            if (limit == 0)
                return Fail("That field is unavailable in this form.", out result);
            if (value == null || value.Length > limit || !ClientCommandText.IsSafeText(value))
                return Fail("Use plain single-line text within the field limit, without controls or formatting.", out result);
            switch (fieldId)
            {
                case "campaign-id": _campaignId = value; break;
                case "destination-id": _destinationId = value; break;
                case "request-id": _requestId = value; break;
                case "facility-id": _facilityId = value; break;
                case "page": _page = value; break;
                case "stage": _stage = value; break;
            }
            _status = "Draft updated in memory. Request the read or review the change explicitly.";
            result = CommandResultSnapshot.Success(_status);
            return true;
        }
    }

    private bool TryBuildCommand(out string command, out string error)
    {
        command = "";
        error = "";
        var id = UsesCampaignId(_operation) ? _campaignId.Trim()
            : _operation == Operation.FacilityInfo ? _facilityId.Trim()
            : _operation == Operation.TravelStatus ? _requestId.Trim()
            : _operation is Operation.TravelInfo or Operation.TravelGo ? _destinationId.Trim() : "";
        if ((UsesCampaignId(_operation) || _operation is Operation.TravelStatus or Operation.TravelInfo or Operation.TravelGo or Operation.FacilityInfo)
            && !ClientCommandText.ValidId(id))
        {
            error = "Enter the matching ID: full GUID or unique 8–32 hexadecimal prefix, never a name or account ID.";
            return false;
        }
        var page = 1;
        if (MaximumPage(_operation) > 0 && !TryPage(out page))
        {
            error = "Enter a page from 1 to " + MaximumPage(_operation).ToString(CultureInfo.InvariantCulture) + ".";
            return false;
        }
        var stage = 1;
        if (UsesStage(_operation) && (!int.TryParse(_stage.Trim(), NumberStyles.None,
            CultureInfo.InvariantCulture, out stage) || stage < 1 || stage > 8))
        {
            error = "Enter a stage from 1 to 8. The server checks stage availability and eligibility.";
            return false;
        }
        command = _operation switch {
            Operation.CampaignList => "/campaign list " + page.ToString(CultureInfo.InvariantCulture),
            Operation.CampaignProgress => "/campaign progress " + page.ToString(CultureInfo.InvariantCulture),
            Operation.CampaignInfo => "/campaign info " + id,
            Operation.CampaignRoute => "/campaign route " + id + " " + stage.ToString(CultureInfo.InvariantCulture),
            Operation.CampaignStart => "/campaign start " + id,
            Operation.CampaignVisit => "/campaign visit " + id + " " + stage.ToString(CultureInfo.InvariantCulture),
            Operation.PointsBalance => "/points balance",
            Operation.PointsHistory => "/points history " + page.ToString(CultureInfo.InvariantCulture),
            Operation.PointsReward => "/points reward " + id,
            Operation.PointsClaim => "/points claim " + id,
            Operation.TravelList => "/travel list " + page.ToString(CultureInfo.InvariantCulture),
            Operation.TravelInfo => "/travel info " + id,
            Operation.TravelHistory => "/travel history " + page.ToString(CultureInfo.InvariantCulture),
            Operation.TravelStatus => "/travel status " + id,
            Operation.TravelGo => "/travel go " + id,
            Operation.FacilityList => "/facility list " + page.ToString(CultureInfo.InvariantCulture),
            Operation.FacilityInfo => "/facility info " + id,
            Operation.FacilityHelp => "/facility help " + page.ToString(CultureInfo.InvariantCulture),
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
                Operation.CampaignStart => "Start shared progress for your current Tribe. A leader or officer must start it; repeat starts make no change. Archived campaigns cannot start.",
                Operation.CampaignVisit => "Record this stage for your current Tribe. Any member may check in; the server checks stage order and observed position within its radius. Repeat check-ins make no change; archived campaigns cannot advance.",
                Operation.PointsClaim => "Claim the configured campaign completion reward once for your current Tribe. Any member may claim. Already-claimed rewards make no change. These are virtual points, not inventory items.",
                _ => "Request movement to the operator-configured destination. Remote travel uses native spawn assignment and also updates look direction, race and cached spawn state. Destination terrain and loading safety are unverified."
            }));
        switch (_group)
        {
            case Group.Campaigns:
                rows.Add(Row("Campaign rules", "Routes are public; progress belongs to the current Tribe. Check-ins use server-observed positions, not proof of honest travel. No items or native story unlocks are granted."));
                break;
            case Group.Points:
                rows.Add(Row("Tribe points", "Points belong to your current Tribe. Campaign rewards depend on recorded completion and server configuration. Repeated claims add nothing; points do not grant items."));
                break;
            case Group.Travel:
                rows.Add(Row("Travel rules", "Travel is free. Jailed or non-alive players are blocked. One request per account can be pending; a world-wide account cooldown applies across destinations. The server rechecks destination access."));
                rows.Add(Row("Arrival evidence", "A request is not an arrival receipt. ArrivalObserved means the same attached player was observed within 1 m twice; it is not a client acknowledgement or proof of a safe outcome."));
                rows.Add(Row("Follow up", "Select Travel history or Travel request status and request a read explicitly. Copy request IDs manually; destination IDs are separate. Nothing polls or retries automatically."));
                rows.Add(Row("Travel records", "Travel records are in world memory, pending the next world save. A successful command response alone does not establish movement or arrival; inspect the reply and request history/status."));
                break;
            case Group.Facilities:
                rows.Add(Row("Tribe facilities", "Only enabled facility policies assigned to your current Tribe are listed. Request each read explicitly."));
                rows.Add(Row("Facility access", "Replies observe current policy and role information; they are not authority. An observed role does not grant permission or physical door access."));
                rows.Add(Row("Coverage", "Facility policies cover configured remote named-state updates. Direct host writes are outside this coverage. Facility access help explains the limits."));
                break;
        }
        if (IsMutation(_operation) && _operation != Operation.TravelGo)
            rows.Add(Row("Save", "Successful changes apply in memory and await the next world save."));
    }

    private void Select(Operation operation)
    {
        var keepStage = UsesStage(_operation) && UsesStage(operation);
        _operation = operation;
        _group = operation switch {
            Operation.PointsBalance or Operation.PointsHistory or Operation.PointsReward or Operation.PointsClaim => Group.Points,
            Operation.TravelList or Operation.TravelInfo or Operation.TravelHistory or Operation.TravelStatus or Operation.TravelGo => Group.Travel,
            Operation.FacilityList or Operation.FacilityInfo or Operation.FacilityHelp => Group.Facilities,
            _ => Group.Campaigns
        };
        _page = "1";
        if (!keepStage) _stage = "1";
        _review = null;
        _scope = Guid.NewGuid();
    }

    private bool TryPage(out int page) => int.TryParse(_page.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out page)
        && page >= 1 && page <= MaximumPage(_operation);

    private (string Label, string Command)? SourceRead() => UsesCampaignId(_operation)
        ? ("campaign IDs", "/campaign list 1")
        : _operation switch {
            Operation.TravelInfo or Operation.TravelGo => ("destination IDs", "/travel list 1"),
            Operation.TravelStatus => ("travel request history", "/travel history 1"),
            Operation.FacilityInfo => ("facility IDs", "/facility list 1"),
            _ => null
        };

    private void SelectedStatus() => _status = "Selected " + OperationLabel(_operation) + ". Nothing sent.";
    private static bool IsMutation(Operation operation) => operation is Operation.CampaignStart or Operation.CampaignVisit or Operation.PointsClaim or Operation.TravelGo;
    private static bool UsesCampaignId(Operation operation) => operation is Operation.CampaignInfo or Operation.CampaignRoute
        or Operation.CampaignStart or Operation.CampaignVisit or Operation.PointsReward or Operation.PointsClaim;
    private static bool UsesStage(Operation operation) => operation is Operation.CampaignRoute or Operation.CampaignVisit;
    private static int MaximumPage(Operation operation) => operation switch {
        Operation.CampaignList or Operation.CampaignProgress => 32,
        Operation.PointsHistory => 2048,
        Operation.TravelList => 64,
        Operation.TravelHistory => 256,
        Operation.FacilityList => 64,
        Operation.FacilityHelp => 2,
        _ => 0
    };
    private static Operation[] Operations(Group group) => group switch {
        Group.Points => new[] { Operation.PointsBalance, Operation.PointsHistory, Operation.PointsReward, Operation.PointsClaim },
        Group.Travel => new[] { Operation.TravelList, Operation.TravelInfo, Operation.TravelHistory, Operation.TravelStatus, Operation.TravelGo },
        Group.Facilities => new[] { Operation.FacilityList, Operation.FacilityInfo, Operation.FacilityHelp },
        _ => new[] { Operation.CampaignList, Operation.CampaignProgress, Operation.CampaignInfo, Operation.CampaignRoute, Operation.CampaignStart, Operation.CampaignVisit }
    };
    private static string GroupLabel(Group group) => group switch { Group.Points => "Tribe points", Group.Travel => "Travel", Group.Facilities => "Facilities", _ => "Campaigns" };
    private static string ReviewLabel(Operation operation) => operation switch {
        Operation.CampaignStart => "Review campaign start",
        Operation.CampaignVisit => "Review stage check-in",
        Operation.PointsClaim => "Review reward claim",
        _ => "Review travel request"
    };
    private static string ConfirmLabel(Operation operation) => operation switch {
        Operation.CampaignStart => "Confirm campaign start",
        Operation.CampaignVisit => "Confirm stage check-in",
        Operation.PointsClaim => "Confirm reward claim",
        _ => "Confirm travel request"
    };
    private static string ReadLabel(Operation operation) => operation switch {
        Operation.CampaignList => "Read campaigns",
        Operation.CampaignProgress => "Read Tribe progress",
        Operation.CampaignInfo => "Read campaign details",
        Operation.CampaignRoute => "Read stage route",
        Operation.PointsBalance => "Read points balance",
        Operation.PointsHistory => "Read points history",
        Operation.PointsReward => "Read reward details",
        Operation.TravelList => "Read destinations",
        Operation.TravelInfo => "Read destination details",
        Operation.TravelHistory => "Read travel history",
        Operation.TravelStatus => "Read request status",
        Operation.FacilityList => "Read Tribe facilities",
        Operation.FacilityInfo => "Read facility details",
        _ => "Read access help"
    };
    private static string OperationLabel(Operation operation) => operation switch {
        Operation.CampaignList => "Campaign list",
        Operation.CampaignProgress => "Tribe campaign progress",
        Operation.CampaignInfo => "Campaign details",
        Operation.CampaignRoute => "Campaign stage route",
        Operation.CampaignStart => "Start campaign",
        Operation.CampaignVisit => "Record campaign stage",
        Operation.PointsBalance => "Tribe points balance",
        Operation.PointsHistory => "Tribe points history",
        Operation.PointsReward => "Campaign reward details",
        Operation.PointsClaim => "Claim campaign reward",
        Operation.TravelList => "Travel destinations",
        Operation.TravelInfo => "Destination details",
        Operation.TravelHistory => "Travel history",
        Operation.TravelStatus => "Travel request status",
        Operation.FacilityList => "Tribe facilities",
        Operation.FacilityInfo => "Facility details",
        Operation.FacilityHelp => "Facility access help",
        _ => "Request travel"
    };
}
