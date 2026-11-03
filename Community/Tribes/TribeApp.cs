// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Modstoke.Loader;
using Modstoke.Sons;

namespace Modstoke.Client;

internal sealed record ClientTribeInfo(string? Id, string Name, string Role, int Members, string Announcement);
internal sealed record ClientTribeMember(string Id, string Role, string Account);
internal sealed record ClientTribeTask(string Id, string Text, bool Done);
internal sealed record ClientTribeTarget(string Id, string Name, string Account);
internal sealed record ClientTribeInvitation(string Id, string TribeId, string Name, DateTimeOffset Expires);
internal sealed record ClientTribeAppSnapshot(bool Available, bool Busy, string Message, ClientTribeInfo? Info,
    ClientTribeMember? Member, int MemberPage, bool MoreMembers, ClientTribeTask? Task, int TaskPage,
    bool MoreTasks, double ObservationAge, string NameDraft, string TaskDraft, bool People,
    ClientTribeTarget? Target, int TargetPage, bool MoreTargets,
    ClientTribeInvitation? Invitation, int InvitationPage, bool MoreInvitations,
    string Review, string ConfirmVerb, string Receipt)
{
    internal string Activity { get; init; } = "";
    internal string ReviewAction { get; init; } = "Confirm";
    internal int ReviewSeconds { get; init; }
    internal bool MembersRead { get; init; }
    internal bool TasksRead { get; init; }
    internal bool TargetsRead { get; init; }
    internal bool InvitationsRead { get; init; }
    internal string DraftAction { get; init; } = "";
    internal bool Council { get; init; }
    internal string AnnouncementDraft { get; init; } = "";
    internal bool DiscussionReplacePending { get; init; }
}

// Session-scoped observations and one in-flight command, owned by the terminal's game thread.
internal sealed partial class ClientTribeApp : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly Action<string, string>? _commandResult;
    private Guid? _session;
    private bool _started, _disposed, _visible, _opened, _loadTasks, _people;
    private ClientCommand? _pending;
    private Request? _request, _queuedRead;
    private Review? _review;
    private ClientTribeInfo? _info;
    private ClientTribeMember? _member;
    private ClientTribeTask? _task;
    private ClientTribeTarget? _target;
    private ClientTribeInvitation? _invitation;
    private int _memberPage = 1, _taskPage = 1;
    private bool _moreMembers, _moreTasks;
    private int _targetPage = 1, _invitationPage = 1;
    private bool _moreTargets, _moreInvitations;
    private long _nextRead, _infoAt, _memberAt, _taskAt;
    private long _targetAt, _invitationAt;
    private string _nameDraft = "", _taskDraft = "";
    private string _draftAction = "", _draftReplacement = "";
    private long _draftActionUntil;
    private string _message = "Join a compatible server to open your Tribe.", _receipt = "";
    private sealed record Request(Guid Session, string Kind, int Page, string Command, string? TribeId,
        string Description = "", string Draft = "");
    private sealed record Review(Guid Session, Guid Token, string? TribeId, string Role, string Kind,
        string Command, string Description, string ItemId, long ExpiresAt);

    internal ClientTribeApp(Action<string, string>? commandResult = null) => _commandResult = commandResult;
    private bool OnThread => Environment.CurrentManagedThreadId == _thread;
    private static long Now => Environment.TickCount64;
    private static bool Fresh(long at) => at != 0 && Now - at < 30000;
    private bool Ready => _session.HasValue && _pending == null && _queuedRead == null;
    private bool CanManage => _info?.Id != null && _info.Role is "Leader" or "Officer";

    internal ClientTribeAppSnapshot Snapshot
    {
        get
        {
            Expire();
            return new(_session.HasValue, _pending != null || _queuedRead != null, _message, _info,
                _member, _memberPage, _moreMembers, _task, _taskPage, _moreTasks,
                _info == null ? 0 : Math.Max(0, (Now - _infoAt) / 1000d), _nameDraft, _taskDraft, _people,
                _target, _targetPage, _moreTargets, _invitation, _invitationPage, _moreInvitations,
                _review?.Description ?? "", _review == null ? "" : "confirm." + _review.Token.ToString("N"), _receipt)
            {
                Activity = (_request ?? _queuedRead)?.Kind ?? "",
                ReviewAction = _review == null ? "Confirm" : ActionLabel(_review.Kind),
                ReviewSeconds = _review == null ? 0 : (int)Math.Ceiling(Math.Max(0, _review.ExpiresAt - Now) / 1000d),
                MembersRead = Fresh(_memberAt), TasksRead = Fresh(_taskAt), TargetsRead = Fresh(_targetAt),
                InvitationsRead = Fresh(_invitationAt), DraftAction = _draftAction,
                Council = _council, AnnouncementDraft = _announcementDraft,
                DiscussionReplacePending = _discussionReview != null
            };
        }
    }

    internal void Start()
    {
        if (!OnThread || _disposed) return;
        _started = true;
        Update();
    }

    internal void SetVisible(bool visible)
    {
        if (!OnThread || _disposed) return;
        if (_visible && !visible)
        {
            if (_queuedRead != null)
                _message = "Queued read cancelled. Refresh to request it again.";
            else if (_review != null || _draftAction.Length > 0 || _discussionReview != null)
                _message = "Review cancelled. Your drafts were kept; nothing sent.";
            _review = null; _queuedRead = null; _loadTasks = false; ClearDraftAction(); ClearDiscussion();
        }
        if (!_visible && visible && !Fresh(_infoAt)) _opened = false;
        _visible = visible;
    }

    private bool RefreshSession()
    {
        if (!OnThread || !_started || _disposed) return false;
        var current = ClientCommands.TryGetCurrentSession(out var session)
            && ClientCommands.SupportsReplies ? session : (Guid?)null;
        if (_session != current)
        {
            Clear();
            _session = current;
            _message = current.HasValue ? "Read your Tribe's current membership."
                : "Join a compatible server with command replies to use Tribe.";
        }
        Expire();
        return current.HasValue;
    }

    internal void Update()
    {
        if (!RefreshSession()) return;
        if (_pending != null && _request is { } request)
        {
            var result = _pending.State;
            if (!result.IsTerminal) return;
            _pending = null;
            _request = null;
            if (_session == request.Session) Complete(request, result);
        }
        if (!_visible || _pending != null || _review != null || Now < _nextRead) return;
        if (_queuedRead is { } queued)
        {
            _queuedRead = null;
            if (queued.Session == _session && (queued.Kind == "info" || _info != null && _info.Id == queued.TribeId))
                Send(queued.Kind, queued.Page, queued.Command);
            else
                _message = "Tribe observation expired before the read started. Refresh to read again.";
        }
        else if (!_opened)
        {
            _opened = true;
            Send("info", 0, "/tribe view info");
        }
    }

    internal CommandResultSnapshot HandleEdit(string key, string value)
    {
        if (!RefreshSession() || !_visible || !Ready)
            return CommandResultSnapshot.Failure("Wait for the current request before editing.");
        _review = null;
        ClearDraftAction();
        ClearDiscussion();
        var limit = key switch { "name" => 48, "task" => 160, "announcement" => 320, _ => 0 };
        if (limit == 0 || value == null || !SafeText(value, limit, true))
            return CommandResultSnapshot.Failure("Use plain single-line text within the field's limit.");
        switch (key)
        {
            case "name": _nameDraft = value; break;
            case "task": _taskDraft = value; break;
            case "announcement": _announcementDraft = value; break;
        }
        _message = "Draft saved locally. Prepare and confirm to send it.";
        return CommandResultSnapshot.Success("Draft updated. Prepare and confirm to send it.");
    }

    internal void HandleCommand(string verb)
    {
        if (!RefreshSession() || !_visible || !Ready) return;
        ClearDiscussion();
        if (verb.StartsWith("confirm.", StringComparison.Ordinal))
        {
            var review = _review;
            _review = null;
            if (review == null || verb != "confirm." + review.Token.ToString("N") || review.Session != _session
                || _info == null || !Fresh(_infoAt) || _info.Id != review.TribeId || _info.Role != review.Role
                || Now >= review.ExpiresAt || !ReviewItemCurrent(review))
            {
                _message = "That review expired. Refresh your Tribe and prepare the change again.";
                return;
            }
            Send(review.Kind, 0, review.Command, review.Description);
            return;
        }
        _review = null;
        if (verb is not ("copy-task" or "clear-task" or "clear-name")) ClearDraftAction();
        if (HandleCouncilCommand(verb)) return;
        switch (verb)
        {
            case "copy-task" when CanManage && _task != null && Fresh(_taskAt):
                ChangeDraft("copy-task", _task.Text); break;
            case "clear-task": ChangeDraft("clear-task", ""); break;
            case "clear-name": ChangeDraft("clear-name", ""); break;
            case "cancel": _message = "Change cancelled. Nothing sent."; break;
            case "refresh": QueueRead("info", 0); break;
            case "people":
                _council = false;
                _people = true;
                _loadTasks = false;
                if (_info != null && !Fresh(CanManage ? _targetAt : _invitationAt))
                    QueueRead(CanManage ? "targets" : "invitations", CanManage ? _targetPage : _invitationPage);
                break;
            case "overview":
                _council = false;
                _people = false;
                if (_info?.Id != null)
                {
                    if (!Fresh(_memberAt)) { _loadTasks = !Fresh(_taskAt); QueueRead("members", _memberPage); }
                    else if (!Fresh(_taskAt)) QueueRead("tasks", _taskPage);
                }
                break;
            case "targets" when _info != null: _council = false; _people = true; QueueRead("targets", _targetPage); break;
            case "targets-prev" when _info != null && _targetPage > 1: QueueRead("targets", _targetPage - 1); break;
            case "targets-next" when _info != null && _moreTargets: QueueRead("targets", _targetPage + 1); break;
            case "invitations" when _info != null: _council = false; _people = true; QueueRead("invitations", _invitationPage); break;
            case "invitations-prev" when _info != null && _invitationPage > 1: QueueRead("invitations", _invitationPage - 1); break;
            case "invitations-next" when _info != null && _moreInvitations: QueueRead("invitations", _invitationPage + 1); break;
            case "members" when _info?.Id != null: QueueRead("members", _memberPage); break;
            case "members-prev" when _info?.Id != null && _memberPage > 1: QueueRead("members", _memberPage - 1); break;
            case "members-next" when _info?.Id != null && _moreMembers: QueueRead("members", _memberPage + 1); break;
            case "tasks" when _info?.Id != null: QueueRead("tasks", _taskPage); break;
            case "tasks-prev" when _info?.Id != null && _taskPage > 1: QueueRead("tasks", _taskPage - 1); break;
            case "tasks-next" when _info?.Id != null && _moreTasks: QueueRead("tasks", _taskPage + 1); break;
            case "prepare-create" when _info is { Id: null }:
                PrepareText("create", "/tribe create ", _nameDraft, 48, "Create a Tribe named "); break;
            case "prepare-invite" when CanManage && _target != null && Fresh(_targetAt):
                Prepare("invite", "/tribe invite " + _target.Id, "Invite " + _target.Name + " (" + _target.Account
                    + ") to " + _info!.Name + "?", _target.Id); break;
            case "prepare-accept" when _info is { Id: null } && _invitation is { } acceptance && InvitationCurrent:
                Prepare("accept", "/tribe accept " + acceptance.Id, "Join " + acceptance.Name
                    + "? Accepting clears your other incoming invitations.", acceptance.Id); break;
            case "prepare-decline" when _info != null && _invitation is { } decline && InvitationCurrent:
                Prepare("decline", "/tribe decline " + decline.Id, "Decline the invitation to " + decline.Name + "?", decline.Id); break;
            case "prepare-leave" when _info?.Id != null && _info.Role != "Leader":
                Prepare("leave", "/tribe leave", "Leave " + _info.Name + "? You will need another invitation to return."); break;
            case "prepare-add" when CanManage:
                PrepareText("add", "/tribe tasks add ", _taskDraft, 160, "Add task: "); break;
            case "prepare-done" when CanManage && _task is { Done: false } && Fresh(_taskAt):
                Prepare("done", "/tribe tasks done " + _task.Id, "Mark complete: " + _task.Text, _task.Id); break;
        }
    }

    private bool InvitationCurrent => _invitation != null && Fresh(_invitationAt) && _invitation.Expires > DateTimeOffset.UtcNow;
    private void ClearDraftAction() { _draftAction = _draftReplacement = ""; _draftActionUntil = 0; }

    private void ChangeDraft(string action, string replacement)
    {
        var current = action == "clear-name" ? _nameDraft : _taskDraft;
        if (current.Length > 0 && current != replacement
            && (_draftAction != action || _draftReplacement != replacement || Now >= _draftActionUntil))
        {
            _draftAction = action; _draftReplacement = replacement; _draftActionUntil = Now + 8000;
            _message = action == "copy-task" ? "Select Replace draft again within 8 seconds to replace your local task draft."
                : "Select Discard draft again within 8 seconds. This only clears your local draft.";
            return;
        }
        if (action == "clear-name") _nameDraft = replacement; else _taskDraft = replacement;
        ClearDraftAction();
        _message = replacement.Length == 0 ? "Local draft cleared. Nothing sent." : "Task copied to your local draft. Edit, prepare and confirm to add a new task.";
    }

    internal static string DraftProblem(string key, string draft)
    {
        var value = draft.Trim();
        var limit = key switch { "name" => 48, "announcement" => 320, _ => 160 };
        if (value.Length == 0) return "Enter " + (key switch { "name" => "a Tribe name", "announcement" => "an announcement", _ => "a task" }) + " to prepare.";
        if (!SafeText(value, limit, false)) return "Use plain single-line text (max " + limit + " characters).";
        return Utf8.GetByteCount(DraftPrefix(key) + value) > 256
            ? "Shorten this draft: the command exceeds 256 UTF-8 bytes." : "";
    }

    internal static int DraftBytes(string key, string draft) => Encoding.UTF8.GetByteCount(
        DraftPrefix(key) + draft.Trim());

    private static string DraftPrefix(string key) => key switch
    { "name" => "/tribe create ", "announcement" => "/tribe announce ", _ => "/tribe tasks add " };

    private static string ActionLabel(string kind) => kind switch
    {
        "create" => "Create Tribe", "invite" => "Send invite", "accept" => "Join Tribe", "decline" => "Decline invite",
        "leave" => "Leave Tribe", "add" => "Add task", "done" => "Complete task", "remove-task" => "Remove task",
        "promote" => "Promote member", "demote" => "Demote officer", "kick" => "Remove member", "leader" => "Transfer leadership",
        "announce" => "Post announcement", "clear-announcement" => "Clear announcement", _ => "Confirm"
    };
    private bool ReviewItemCurrent(Review review) => review.Kind switch
    {
        "done" => CanManage && _task is { Done: false } && _task.Id == review.ItemId && Fresh(_taskAt),
        "remove-task" => CanManage && _task is { Done: true } && _task.Id == review.ItemId && Fresh(_taskAt),
        "promote" or "demote" or "kick" or "leader" => _member?.Id == review.ItemId && Fresh(_memberAt)
            && CanManageMember(review.Kind, _info?.Role ?? "", _member),
        "announce" or "clear-announcement" => CanManage,
        "invite" => _target?.Id == review.ItemId && Fresh(_targetAt),
        "accept" or "decline" => InvitationCurrent && _invitation!.Id == review.ItemId,
        _ => true
    };

    private void PrepareText(string kind, string prefix, string draft, int limit, string description)
    {
        var value = draft.Trim();
        if (!SafeText(value, limit, false)) { _message = "Enter valid text before preparing this change."; return; }
        Prepare(kind, prefix + value, description + value);
    }

    private void Prepare(string kind, string command, string description, string itemId = "")
    {
        if (_info == null || !Fresh(_infoAt)) { _message = "Refresh your Tribe before preparing a change."; return; }
        if (Utf8.GetByteCount(command) > 256) { _message = "This text exceeds the command's byte limit. Shorten it before sending."; return; }
        var sourceAt = kind switch {
            "done" or "remove-task" => _taskAt,
            "promote" or "demote" or "kick" or "leader" => _memberAt,
            "invite" => _targetAt, "accept" or "decline" => _invitationAt, _ => _infoAt
        };
        var expires = Math.Min(Now + 20000, Math.Min(_infoAt + 30000, sourceAt + 30000));
        if (kind is "accept" or "decline" && _invitation != null)
            expires = Math.Min(expires, Now + (long)(_invitation.Expires - DateTimeOffset.UtcNow).TotalMilliseconds);
        _review = new(_session!.Value, Guid.NewGuid(), _info.Id, _info.Role, kind, command, description, itemId, expires);
        _message = "Review the change, then confirm once to send it.";
    }

    private void QueueRead(string kind, int page)
    {
        if (page < 0 || page > 8192) return;
        _opened = true;
        _queuedRead = new(_session!.Value, kind, page, "/tribe view " + kind
            + (kind == "info" ? "" : " " + page.ToString(CultureInfo.InvariantCulture)), _info?.Id);
        _message = "Reading your Tribe; observations are spaced at least three seconds apart.";
    }

    private static bool Mutation(string kind) => kind is "create" or "invite" or "accept" or "decline" or "leave" or "add" or "done"
        or "remove-task" or "promote" or "demote" or "kick" or "leader" or "announce" or "clear-announcement";

    private void Send(string kind, int page, string command, string description = "")
    {
        if (!Ready) return;
        var request = new Request(_session!.Value, kind, page, command, _info?.Id, description,
            kind == "create" ? _nameDraft : kind == "add" ? _taskDraft : kind == "announce" ? _announcementDraft : "");
        if (Mutation(kind) || kind == "info") ClearObservations();
        else if (kind == "members") { _member = null; _memberAt = 0; _moreMembers = false; }
        else if (kind == "tasks") { _task = null; _taskAt = 0; _moreTasks = false; }
        else if (kind == "targets") { _target = null; _targetAt = 0; _moreTargets = false; }
        else if (kind == "invitations") { _invitation = null; _invitationAt = 0; _moreInvitations = false; }
        if (Mutation(kind)) _receipt = description + "\nWaiting for the server's " + ActionLabel(kind).ToLowerInvariant() + " receipt.";
        _nextRead = Now + 3000;
        var sent = ClientCommands.Submit(command, out var handle);
        _pending = handle;
        _request = handle == null ? null : request;
        _message = handle != null ? "Waiting for the server…" : "Request was not queued: " + sent + ". Nothing was retried.";
        if (sent == ClientCommandSendResult.SendUncertain)
            _message = "Result unknown. Waiting if a request handle exists; no change will be retried.";
        if (Mutation(kind) && handle == null) { _receipt = description + "\n" + _message; _commandResult?.Invoke("Tribe", _receipt); }
    }

    private void Complete(Request request, ClientCommandState result)
    {
        var reported = result.Progress == ClientCommandProgress.ServerReported;
        var success = reported && result.Outcome == ClientCommandOutcome.Succeeded;
        if (Mutation(request.Kind))
        {
            if (success)
            {
                if (request.Kind == "create" && _nameDraft == request.Draft) _nameDraft = "";
                if (request.Kind == "add" && _taskDraft == request.Draft) _taskDraft = "";
                if (request.Kind == "announce" && _announcementDraft == request.Draft) _announcementDraft = "";
            }
            _receipt = DateTimeOffset.Now.ToString("t", CultureInfo.CurrentCulture) + " · " + ActionLabel(request.Kind)
                + "\n" + request.Description + "\n" + (success ? "Server reported success. Changes await a world save; refresh to observe current state."
                : reported ? "Server reported " + result.Outcome + ". Nothing was retried."
                : "Result unknown (" + result.Progress + "). Nothing was retried; refresh before trying another change.");
            _message = ActionLabel(request.Kind) + (success ? ": server reported success." : ": read the result below before trying again.");
            if (success && _visible) QueueRead("info", 0);
            _commandResult?.Invoke("Tribe", _receipt);
            return;
        }
        if (!success)
        {
            // A denied list may mean membership changed. Do not retain actionable role observations.
            ClearObservations();
            _message = reported ? "Server could not read your Tribe: " + result.Outcome + ". Refresh to try again."
                : "Observation unavailable; result unknown (" + result.Progress + ").";
            return;
        }
        var age = Math.Max(0, (DateTimeOffset.UtcNow - result.ObservedAt).TotalMilliseconds);
        if (result.ReplyTruncated || age >= 30000 || !Parse(request, result.Reply, Now - (long)age))
        {
            ClearObservations();
            _message = "Server observation rejected: incomplete, invalid, or expired reply.";
            return;
        }
        _message = request.Kind == "info" ? (_info?.Id == null ? "You do not currently belong to a Tribe." : "Tribe membership observed by the server.")
            : request.Kind == "members" ? (_member == null ? "No member on this page." : "Roster member observed by the server.")
            : request.Kind == "targets" ? (_target == null ? "No eligible online player on this page." : "Online player observed. Prepare an invitation to review it.")
            : request.Kind == "invitations" ? (_invitation == null ? "No incoming invitation on this page." : "Incoming invitation observed by the server.")
            : _task == null ? "No task on this page." : "Task observed by the server.";
        if (_visible && request.Kind == "info" && _info != null)
        {
            if (_info.Id == null) { _people = true; _council = false; }
            if (_people) QueueRead(CanManage ? "targets" : "invitations", 1);
            else { _loadTasks = !_council; QueueRead("members", 1); }
        }
        else if (_visible && request.Kind == "members" && _loadTasks)
        { _loadTasks = false; QueueRead("tasks", _taskPage); }
    }

    private bool Parse(Request request, string? reply, long at)
    {
        try
        {
            if (string.IsNullOrEmpty(reply) || Utf8.GetByteCount(reply) > 512) return false;
            using var document = JsonDocument.Parse(reply, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (request.Kind == "info")
            {
                if (!Fields(root, "v", "kind", "id", "name", "role", "members", "announcement") || !Header(root, "info")
                    || !Text(root, "name", 48, true, out var name) || !Text(root, "role", 7, true, out var role)
                    || !Integer(root, "members", out var count) || count < 0
                    || !Text(root, "announcement", 320, true, out var announcement)) return false;
                string? id = null;
                if (root.GetProperty("id").ValueKind == JsonValueKind.Null)
                { if (name.Length != 0 || role.Length != 0 || count != 0 || announcement.Length != 0) return false; }
                else if (!Id(root, out id) || !Role(role) || string.IsNullOrWhiteSpace(name) || count < 1) return false;
                _info = new(id, name, role, count, announcement); _infoAt = at;
                return true;
            }
            if (_info == null || _info.Id != request.TribeId || !Fresh(_infoAt)
                || !Fields(root, "v", "kind", "page", "more", "item") || !Header(root, request.Kind)
                || !Integer(root, "page", out var page) || page != request.Page
                || !Boolean(root, "more", out var more)) return false;
            var item = root.GetProperty("item");
            if (item.ValueKind == JsonValueKind.Null && more) return false;
            if (request.Kind == "targets")
            {
                ClientTribeTarget? target = null;
                if (item.ValueKind != JsonValueKind.Null)
                {
                    if (!Fields(item, "id", "name", "account") || !Text(item, "id", 32, false, out var id)
                        || id != "host" && (!Guid.TryParseExact(id, "N", out var session) || session == Guid.Empty)
                        || !Text(item, "name", 128, false, out var name) || !Text(item, "account", 160, false, out var account)) return false;
                    target = new(id, name, account);
                }
                _target = target; _targetPage = page; _moreTargets = more; _targetAt = at;
                return true;
            }
            if (request.Kind == "invitations")
            {
                ClientTribeInvitation? invitation = null;
                if (item.ValueKind != JsonValueKind.Null)
                {
                    if (!Fields(item, "id", "tribe", "name", "expires") || !Id(item, out var id)
                        || !Text(item, "tribe", 32, false, out var tribeId) || !Guid.TryParseExact(tribeId, "N", out var tribe) || tribe == Guid.Empty
                        || !Text(item, "name", 48, false, out var name) || item.GetProperty("expires").ValueKind != JsonValueKind.Number
                        || !item.GetProperty("expires").TryGetInt64(out var seconds) || seconds < 0 || seconds > 253402300799L) return false;
                    var expires = DateTimeOffset.FromUnixTimeSeconds(seconds);
                    if (expires <= DateTimeOffset.UtcNow) return false;
                    invitation = new(id, tribeId, name, expires);
                }
                _invitation = invitation; _invitationPage = page; _moreInvitations = more; _invitationAt = at;
                return true;
            }
            if (_info.Id == null) return false;
            if (request.Kind == "members")
            {
                ClientTribeMember? member = null;
                if (item.ValueKind != JsonValueKind.Null)
                {
                    if (!Fields(item, "id", "role", "account") || !Id(item, out var id)
                        || !Text(item, "role", 7, false, out var role) || !Role(role)
                        || !Text(item, "account", 160, false, out var account)) return false;
                    member = new(id, role, account);
                }
                _member = member; _memberPage = page; _moreMembers = more; _memberAt = at;
                return true;
            }
            if (request.Kind != "tasks") return false;
            ClientTribeTask? task = null;
            if (item.ValueKind != JsonValueKind.Null)
            {
                if (!Fields(item, "id", "text", "done") || !Id(item, out var id)
                    || !Text(item, "text", 160, false, out var text) || !Boolean(item, "done", out var done)) return false;
                task = new(id, text, done);
            }
            _task = task; _taskPage = page; _moreTasks = more; _taskAt = at;
            return true;
        }
        catch (JsonException) { return false; }
        catch (EncoderFallbackException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static bool Fields(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;
        var remaining = new HashSet<string>(names, StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject()) if (!remaining.Remove(property.Name)) return false;
        return remaining.Count == 0;
    }
    private static bool Integer(JsonElement root, string name, out int value)
    { value = 0; var field = root.GetProperty(name); return field.ValueKind == JsonValueKind.Number && field.TryGetInt32(out value); }
    private static bool Boolean(JsonElement root, string name, out bool value)
    { var field = root.GetProperty(name); value = field.ValueKind == JsonValueKind.True; return field.ValueKind is JsonValueKind.True or JsonValueKind.False; }
    private static bool Header(JsonElement root, string kind) => Integer(root, "v", out var version) && version == 1
        && root.GetProperty("kind").ValueKind == JsonValueKind.String && root.GetProperty("kind").GetString() == kind;
    private static bool Text(JsonElement root, string key, int limit, bool empty, out string value)
    {
        value = "";
        var field = root.GetProperty(key);
        if (field.ValueKind != JsonValueKind.String) return false;
        value = field.GetString() ?? "";
        if (value.Length > limit || !empty && string.IsNullOrWhiteSpace(value)) return false;
        foreach (var character in value) if (char.IsControl(character)) return false;
        return true;
    }
    private static bool Id(JsonElement root, out string value) => Text(root, "id", 32, false, out value)
        && value.Length == 32 && Guid.TryParseExact(value, "N", out var id) && id != Guid.Empty;
    private static bool Role(string role) => role is "Leader" or "Officer" or "Member";
    private static bool SafeText(string value, int limit, bool empty)
    {
        if (value.Length > limit || !empty && string.IsNullOrWhiteSpace(value)) return false;
        foreach (var character in value)
            if (char.IsControl(character) || char.IsSurrogate(character) || character is '<' or '>' or '\u2028' or '\u2029'
                || char.GetUnicodeCategory(character) == UnicodeCategory.Format) return false;
        return true;
    }

    private void Expire()
    {
        var hadReview = _review != null;
        if (_draftAction.Length > 0 && Now >= _draftActionUntil) ClearDraftAction();
        ExpireDiscussion();
        if (_info != null && !Fresh(_infoAt)) ClearObservations();
        if (_member != null && !Fresh(_memberAt)) { _member = null; _moreMembers = false; }
        if (_task != null && !Fresh(_taskAt)) { _task = null; _moreTasks = false; }
        if (_target != null && !Fresh(_targetAt)) { _target = null; _moreTargets = false; }
        if (_invitation != null && !InvitationCurrent) { _invitation = null; _moreInvitations = false; }
        if (_review != null && (Now >= _review.ExpiresAt || _info == null || !ReviewItemCurrent(_review)))
            _review = null;
        if (hadReview && _review == null) _message = "Review expired. Refresh your Tribe and prepare the change again.";
    }
    private void ClearObservations()
    {
        ClearDiscussion();
        _info = null; _member = null; _task = null; _target = null; _invitation = null; _review = null;
        _memberPage = _taskPage = 1; _moreMembers = _moreTasks = _loadTasks = false;
        _infoAt = _memberAt = _taskAt = 0;
        _targetPage = _invitationPage = 1; _moreTargets = _moreInvitations = false; _targetAt = _invitationAt = 0;
    }
    private void Clear()
    {
        ClearObservations(); _pending = null; _request = null; _queuedRead = null;
        _nameDraft = _taskDraft = _receipt = ""; ClearDraftAction(); _opened = _people = false; _nextRead = 0;
        _announcementDraft = ""; _council = false;
    }
    public void Dispose()
    {
        if (!OnThread) return;
        _disposed = true; Clear(); _session = null;
    }
}
