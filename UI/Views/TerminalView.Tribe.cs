// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using TMPro;
using UnityEngine;
using UnityEngine.Events;

namespace Modstoke.Client;

internal sealed partial class ClientTerminalView
{
    private RectTransform? _tribeAppSurface;
    private TextMeshProUGUI _tribeTitle = null!, _tribeSummary = null!, _tribeNotice = null!;
    private TextMeshProUGUI _tribeMemberRole = null!, _tribeMemberAccount = null!, _tribeMemberPage = null!;
    private TextMeshProUGUI _tribeTaskState = null!, _tribeTaskText = null!, _tribeTaskPage = null!;
    private TextMeshProUGUI _tribeManageTitle = null!, _tribeManageHint = null!, _tribeDraftLabel = null!;
    private TextMeshProUGUI _tribeAppMessage = null!, _tribeReceipt = null!, _tribeReview = null!;
    private ButtonWidget _tribeRefresh = null!, _tribeMembers = null!, _tribeMemberPrevious = null!, _tribeMemberNext = null!;
    private ButtonWidget _tribeTasks = null!, _tribeTaskPrevious = null!, _tribeTaskNext = null!, _tribeTaskDone = null!, _tribeTaskAdd = null!;
    private ButtonWidget _tribeCreate = null!, _tribeLeave = null!, _tribeConfirm = null!, _tribeCancel = null!;
    private ButtonWidget _tribeOverview = null!, _tribePeople = null!, _tribePeopleShortcut = null!, _tribePinTask = null!;
    private ButtonWidget _tribeCopyTask = null!, _tribeClearTask = null!, _tribeClearName = null!;
    private TextMeshProUGUI _tribeTaskBudget = null!;
    private bool _tribeCanDraftTask, _tribeCanDraftName;
    private ButtonWidget _tribeTargetPrevious = null!, _tribeTargetNext = null!, _tribeTargets = null!, _tribeInvite = null!;
    private ButtonWidget _tribeInvitationPrevious = null!, _tribeInvitationNext = null!, _tribeInvitations = null!, _tribeAccept = null!, _tribeDecline = null!;
    private TextMeshProUGUI _tribeTargetName = null!, _tribeTargetDetail = null!, _tribeTargetPage = null!;
    private TextMeshProUGUI _tribeInvitationName = null!, _tribeInvitationDetail = null!, _tribeInvitationPage = null!;
    private RectTransform _tribeRosterPanel = null!, _tribeTasksPanel = null!, _tribePeoplePanel = null!;
    private TMP_InputField _tribeNameInput = null!, _tribeTaskInput = null!;
    private string _tribeConfirmVerb = "";
    private string _tribeTaskToPin = "", _tribeTaskPinId = "", _tribeLocalReceipt = "";
    private ScrollArea _tribeNoticeScroll = null!, _tribeAccountScroll = null!, _tribeTaskScroll = null!, _tribeReviewScroll = null!;
    private ScrollArea _tribeTargetScroll = null!, _tribeReceiptScroll = null!, _tribeManageScroll = null!;

    internal void BuildTribeApp()
    {
        if (_tribeAppSurface != null) return;
        _tribeAppSurface = Node(_applicationLayer, "TribeApp");
        Place(_tribeAppSurface, 56f, 176f, WorkspaceWidth, WorkspaceHeight);
        var header = RoundedPanel(_tribeAppSurface, "TribeIdentity", Surface, 0f, 0f, 1328f, 128f).rectTransform;
        _tribeTitle = TribeText(header, "TribeName", "Your Tribe", 28f, Ink, 24f, 12f, 548f, 76f, true);
        _tribeSummary = TribeText(header, "Membership", "Read current membership to begin.", 19f, Muted, 24f, 94f, 548f, 26f);
        _tribeNoticeScroll = Scroll(header, "AnnouncementScroll", 592f, 18f, 328f, 98f);
        _tribeNotice = Text(_tribeNoticeScroll.Content, "Announcement", "", 21f, Ink, 0f, 0f, _tribeNoticeScroll.Width, 98f);
        _tribeOverview = TribeButton(header, "TribeOverview", "Tribe", 940f, 12f, 112f, "overview");
        _tribePeople = TribeButton(header, "TribePeople", "Invites", 1066f, 12f, 112f, "people");
        _tribeCouncil = TribeButton(header, "TribeCouncil", "Council", 1192f, 12f, 112f, "council");
        _tribeOverview.Label.fontSize = _tribePeople.Label.fontSize = _tribeCouncil.Label.fontSize = 17f;
        _tribeRefresh = TribeButton(header, "RefreshTribe", "Refresh Tribe", 940f, 70f, 364f, "refresh");

        var roster = RoundedPanel(_tribeAppSurface, "TribeRoster", Surface, 0f, 144f, 392f, 320f).rectTransform;
        _tribeRosterPanel = roster;
        TribeText(roster, "RosterHeading", "ROSTER", 19f, Muted, 22f, 16f, 348f, 26f);
        _tribeMemberRole = TribeText(roster, "MemberRole", "No current member", 27f, Mint, 22f, 50f, 348f, 36f, true);
        _tribeAccountScroll = Scroll(roster, "AccountScroll", 22f, 96f, 348f, 122f);
        _tribeMemberAccount = Text(_tribeAccountScroll.Content, "MemberAccount", "", 20f, Ink, 0f, 0f, _tribeAccountScroll.Width, 122f);
        _tribeMemberPage = TribeText(roster, "MemberPage", "", 18f, Muted, 22f, 222f, 348f, 24f);
        _tribeMemberPrevious = TribeButton(roster, "PreviousMember", "Previous", 22f, 258f, 106f, "members-prev");
        _tribeMembers = TribeButton(roster, "ReadRoster", "Refresh", 140f, 258f, 100f, "members");
        _tribeMemberNext = TribeButton(roster, "NextMember", "Next", 252f, 258f, 118f, "members-next");

        var tasks = RoundedPanel(_tribeAppSurface, "TribeTasks", Surface, 412f, 144f, 448f, 320f).rectTransform;
        _tribeTasksPanel = tasks;
        _tribeTaskState = TribeText(tasks, "TaskState", "TASKS", 18f, Muted, 22f, 12f, 142f, 26f);
        _tribeTaskPage = TribeText(tasks, "TaskPage", "", 17f, Muted, 164f, 12f, 44f, 26f);
        _tribeCopyTask = Button(tasks, "CopyTaskDraft", "Copy to draft", 210f, 8f, 106f, 34f, () => TribeCommand("copy-task"));
        _tribeCopyTask.Label.fontSize = 15f;
        _tribePinTask = Button(tasks, "PinTaskToWatch", "Pin to watch", 324f, 8f, 102f, 34f, PinTribeTask);
        _tribePinTask.Label.fontSize = 15f;
        _tribeTaskScroll = Scroll(tasks, "TaskScroll", 22f, 46f, 404f, 72f);
        _tribeTaskText = Text(_tribeTaskScroll.Content, "TaskText", "", 22f, Ink, 0f, 0f, _tribeTaskScroll.Width, 72f);
        _tribeTaskPrevious = TribeButton(tasks, "PreviousTask", "Previous", 22f, 122f, 118f, "tasks-prev");
        _tribeTasks = TribeButton(tasks, "ReadTasks", "Refresh task", 152f, 122f, 142f, "tasks");
        _tribeTasks.Label.fontSize = 18f;
        _tribeTaskNext = TribeButton(tasks, "NextTask", "Next", 306f, 122f, 120f, "tasks-next");
        _tribeTaskBudget = TribeText(tasks, "TaskDraftBudget", "", 16f, Muted, 22f, 176f, 278f, 26f);
        _tribeClearTask = Button(tasks, "ClearTaskDraft", "Clear draft", 306f, 174f, 120f, 30f, () => TribeCommand("clear-task"));
        _tribeClearTask.Label.fontSize = 16f;
        _tribeTaskInput = TribeInput(tasks, "task", 160, 22f, 208f, 404f);
        _tribeTaskAdd = TribeButton(tasks, "AddTask", "Add task…", 22f, 264f, 126f, "prepare-add");
        _tribeTaskDone = TribeButton(tasks, "CompleteTask", "Complete…", 160f, 264f, 126f, "prepare-done");
        _tribeTaskRemove = TribeButton(tasks, "RemoveCompletedTask", "Remove…", 160f, 264f, 126f, "prepare-remove-task");
        _tribeDiscuss = TribeButton(tasks, "DiscussTribeTask", "Discuss…", 298f, 264f, 128f, "discuss-task");
        _tribeTaskAdd.Label.fontSize = _tribeTaskDone.Label.fontSize = _tribeTaskRemove.Label.fontSize = _tribeDiscuss.Label.fontSize = 17f;

        var manage = RoundedPanel(_tribeAppSurface, "TribeMembershipActions", Surface, 880f, 144f, 448f, 320f).rectTransform;
        _tribeManageTitle = TribeText(manage, "MembershipHeading", "MEMBERSHIP", 19f, Muted, 22f, 16f, 404f, 26f);
        _tribeManageScroll = Scroll(manage, "MembershipHelpScroll", 22f, 52f, 404f, 94f);
        _tribeManageHint = Text(_tribeManageScroll.Content, "MembershipHelp", "", 20f, Ink, 0f, 0f, _tribeManageScroll.Width, 94f);
        _tribeDraftLabel = TribeText(manage, "DraftLabel", "", 17f, Muted, 22f, 164f, 278f, 36f);
        _tribeClearName = Button(manage, "ClearNameDraft", "Clear draft", 306f, 164f, 120f, 32f, () => TribeCommand("clear-name"));
        _tribeClearName.Label.fontSize = 16f;
        _tribeNameInput = TribeInput(manage, "name", 48, 22f, 208f, 404f);
        _tribeCreate = TribeButton(manage, "CreateTribe", "Create Tribe…", 22f, 264f, 194f, "prepare-create");
        _tribePeopleShortcut = Button(manage, "OpenInvitations", "Players & invites", 22f, 264f, 194f, 46f,
            () => TribeCommand(_tribeCanDraftTask ? "targets" : "invitations"));
        _tribePeopleShortcut.Label.fontSize = 18f;
        _tribeLeave = TribeButton(manage, "LeaveTribe", "Leave Tribe…", 232f, 264f, 194f, "prepare-leave");

        _tribePeoplePanel = Node(_tribeAppSurface, "TribePlayersAndInvitations");
        Place(_tribePeoplePanel, 0f, 144f, 860f, 320f);
        var incoming = RoundedPanel(_tribePeoplePanel, "IncomingInvitations", Surface, 0f, 0f, 392f, 320f).rectTransform;
        TribeText(incoming, "InvitationsHeading", "INCOMING INVITATIONS", 18f, Muted, 22f, 16f, 348f, 26f);
        _tribeInvitationName = TribeText(incoming, "InvitationTribe", "No current invitation", 25f, Ink, 22f, 50f, 348f, 66f, true);
        _tribeInvitationDetail = TribeText(incoming, "InvitationDetail", "", 18f, Muted, 22f, 122f, 348f, 48f);
        _tribeInvitationPage = TribeText(incoming, "InvitationPage", "", 17f, Muted, 22f, 174f, 348f, 24f);
        _tribeInvitationPrevious = TribeButton(incoming, "PreviousInvitation", "Previous", 22f, 204f, 106f, "invitations-prev");
        _tribeInvitations = TribeButton(incoming, "ReadInvitations", "Read", 140f, 204f, 100f, "invitations");
        _tribeInvitationNext = TribeButton(incoming, "NextInvitation", "Next", 252f, 204f, 118f, "invitations-next");
        _tribeInvitationPrevious.Label.fontSize = 18f;
        _tribeAccept = TribeButton(incoming, "AcceptInvitation", "Join…", 22f, 264f, 166f, "prepare-accept");
        _tribeDecline = TribeButton(incoming, "DeclineInvitation", "Decline…", 204f, 264f, 166f, "prepare-decline");

        var online = RoundedPanel(_tribePeoplePanel, "OnlineInvitePlayers", Surface, 412f, 0f, 448f, 320f).rectTransform;
        TribeText(online, "PlayersHeading", "ONLINE PLAYERS WITHOUT A TRIBE", 18f, Muted, 22f, 16f, 404f, 26f);
        _tribeTargetName = TribeText(online, "TargetName", "Read online players", 27f, Ink, 22f, 50f, 404f, 66f, true);
        _tribeTargetScroll = Scroll(online, "TargetIdentityScroll", 22f, 120f, 404f, 50f);
        _tribeTargetDetail = Text(_tribeTargetScroll.Content, "TargetAccount", "", 19f, Muted, 0f, 0f, _tribeTargetScroll.Width, 50f);
        _tribeTargetPage = TribeText(online, "TargetPage", "", 17f, Muted, 22f, 174f, 404f, 24f);
        _tribeTargetPrevious = TribeButton(online, "PreviousInviteTarget", "Previous", 22f, 204f, 118f, "targets-prev");
        _tribeTargets = TribeButton(online, "ReadInviteTargets", "Read players", 152f, 204f, 142f, "targets");
        _tribeTargets.Label.fontSize = 17f;
        _tribeTargetNext = TribeButton(online, "NextInviteTarget", "Next", 306f, 204f, 120f, "targets-next");
        _tribeInvite = TribeButton(online, "InviteSelectedPlayer", "Invite selected player…", 22f, 264f, 404f, "prepare-invite");

        BuildTribeCouncil();

        var review = RoundedPanel(_tribeAppSurface, "TribeChangeReview", Surface, 0f, 480f, 1328f, 120f).rectTransform;
        _tribeAppMessage = TribeText(review, "TribeMessage", "", 19f, Ink, 22f, 10f, 930f, 27f);
        _tribeReceiptScroll = Scroll(review, "TribeReceiptScroll", 22f, 40f, 930f, 74f);
        _tribeReceipt = Text(_tribeReceiptScroll.Content, "TribeReceipt", "", 19f, Muted, 0f, 0f, _tribeReceiptScroll.Width, 74f);
        _tribeReviewScroll = Scroll(review, "ReviewScroll", 22f, 40f, 930f, 74f);
        _tribeReview = Text(_tribeReviewScroll.Content, "ReviewTribeChange", "", 20f, Amber, 0f, 0f, _tribeReviewScroll.Width, 74f);
        _tribeConfirm = Button(review, "ConfirmTribeChange", "Confirm", 980f, 34f, 154f, 52f,
            () => { if (_tribeConfirmVerb.Length > 0) TribeCommand(_tribeConfirmVerb); });
        _tribeCancel = TribeButton(review, "CancelTribeChange", "Cancel", 1150f, 34f, 156f, "cancel");
        _tribeConfirm.Label.fontSize = 18f;
        _tribeAppSurface.gameObject.SetActive(false);
    }

    private TextMeshProUGUI TribeText(Transform parent, string name, string value, float size, Color color,
        float x, float y, float width, float height, bool bold = false)
    {
        var text = Text(parent, name, value, size, color, x, y, width, height, bold);
        text.overflowMode = TextOverflowModes.Ellipsis;
        return text;
    }

    private void TribeCommand(string verb)
    {
        if (_disposed || _shadeCapture || _tribeAppSurface == null || !_tribeAppSurface.gameObject.activeInHierarchy) return;
        _tribeLocalReceipt = "";
        _command("tribe-app", verb);
    }

    private void PinTribeTask()
    {
        if (_disposed || _shadeCapture || _tribeAppSurface == null || !_tribeAppSurface.gameObject.activeInHierarchy
            || !_tribeTasksPanel.gameObject.activeInHierarchy || !_tribePinTask.Button.interactable || _tribeTaskToPin.Length == 0) return;
        var result = _edit("field", Guid.Empty, "mission", _tribeTaskToPin);
        _tribeLocalReceipt = result.Succeeded ? "Pinned to your personal watch mission. Shared task completion is unchanged."
            : "Could not pin task: " + result.Detail;
        SetTribeDetail(_tribeReceiptScroll, _tribeReceipt, _tribeLocalReceipt);
    }

    private ButtonWidget TribeButton(Transform parent, string name, string label, float x, float y, float width, string verb)
    {
        var button = Button(parent, name, label, x, y, width, 46f, () => TribeCommand(verb));
        button.Label.fontSize = 20f;
        return button;
    }

    private TMP_InputField TribeInput(Transform parent, string key, int limit, float x, float y, float width)
    {
        var input = Input(parent, "", limit);
        Place(input.GetComponent<RectTransform>(), x, y, width, 46f);
        input.textComponent.fontSize = 21f;
        var placeholder = Text(input.textViewport, "Hint", key switch {
            "task" => "New task · officers / leaders", "announcement" => "New announcement", _ => "Enter a Tribe name"
        }, 18f, Muted, 0f, 0f, width - 24f, 34f);
        placeholder.enableWordWrapping = false;
        placeholder.alignment = TextAlignmentOptions.MidlineLeft;
        placeholder.overflowMode = TextOverflowModes.Ellipsis;
        Stretch(placeholder.rectTransform, 0f, 0f, 0f, 0f);
        input.placeholder = placeholder;
        input.onValueChanged.AddListener((UnityAction<string>)new Action<string>(value =>
        {
            if (_disposed || _shadeCapture || _tribeAppSurface == null || !_tribeAppSurface.gameObject.activeInHierarchy || !input.interactable) return;
            var result = _edit("tribe-app", Guid.Empty, key, value ?? "");
            if (!result.Succeeded) _tribeAppMessage.text = result.Detail;
            RefreshTribeDraftBudget();
        }));
        return input;
    }

    internal void SetTribeAppVisible(bool visible)
    {
        if (_disposed || _tribeAppSurface == null) return;
        _tribeAppSurface.gameObject.SetActive(visible);
        if (!visible)
        {
            _tribeConfirmVerb = "";
            _tribeNameInput.DeactivateInputField();
            _tribeTaskInput.DeactivateInputField();
            _tribeAnnouncementInput.DeactivateInputField();
        }
    }

    private static void SetTribeDraft(TMP_InputField input, string draft)
    {
        if (!input.isFocused && input.text != draft) input.SetTextWithoutNotify(draft);
    }

    private void RefreshTribeDraftBudget()
    {
        var task = _tribeTaskInput.text ?? "";
        var name = _tribeNameInput.text ?? "";
        var taskProblem = ClientTribeApp.DraftProblem("task", task);
        var nameProblem = ClientTribeApp.DraftProblem("name", name);
        _tribeTaskBudget.text = task.Length + "/160 · " + ClientTribeApp.DraftBytes("task", task) + "/256 bytes";
        _tribeTaskBudget.color = task.Length > 0 && taskProblem.Length > 0 ? Amber : Muted;
        if (_tribeNameInput.gameObject.activeSelf)
        {
            _tribeDraftLabel.text = name.Length + "/48 · " + ClientTribeApp.DraftBytes("name", name) + "/256 bytes";
            _tribeDraftLabel.color = name.Length > 0 && nameProblem.Length > 0 ? Amber : Muted;
        }
        _tribeTaskAdd.Button.interactable = _tribeCanDraftTask && taskProblem.Length == 0;
        _tribeCreate.Button.interactable = _tribeCanDraftName && nameProblem.Length == 0;
        _tribeClearTask.Button.interactable = _tribeCanDraftTask && task.Length > 0;
        _tribeClearName.Button.interactable = _tribeCanDraftName && name.Length > 0;
        RefreshCouncilDraftBudget();
    }

    private static string TribeListHelp(ClientTribeAppSnapshot state, string kind, bool observed, string unread, string empty)
        => state.Activity == kind ? "Reading this page from the server…"
            : state.Info == null ? "Refresh Tribe to restore current membership and read this list."
            : observed ? empty : unread;

    private static string TribePage(int page, bool hasItem, bool more, bool observed, string singular)
        => !observed ? "Page " + page + " · awaiting read"
            : !hasItem ? "Page " + page + " · no " + singular
            : "Page " + page + (more ? " · more available" : " · end of list");

    private static void SetTribeDetail(ScrollArea scroll, TextMeshProUGUI text, string value)
    {
        if (text.text == value) return;
        text.text = value;
        var height = TextHeight(text, scroll.Width, scroll.Root.rectTransform.rect.height);
        Place(text.rectTransform, 0f, 0f, scroll.Width, height);
        scroll.Height(height);
        scroll.Reset();
    }

    internal void SetTribeAppState(ClientTribeAppSnapshot state)
    {
        if (_disposed || _tribeAppSurface == null) return;
        var ready = state.Available && !state.Busy;
        var info = state.Info;
        var member = info?.Id != null;
        var manage = member && info!.Role is "Leader" or "Officer";
        var noTribe = info is { Id: null };
        var review = state.Review.Length > 0;
        _tribeCanDraftTask = ready && manage && !review;
        _tribeCanDraftName = ready && noTribe && !review;
        _tribeRosterPanel.gameObject.SetActive(!state.People && !state.Council);
        _tribeTasksPanel.gameObject.SetActive(!state.People && !state.Council);
        _tribePeoplePanel.gameObject.SetActive(state.People && !state.Council);
        if (state.People || state.Council) _tribeTaskInput.DeactivateInputField();
        _tribeOverview.Button.interactable = ready && member && !review;
        _tribePeople.Button.interactable = ready && info != null && !review;
        _tribeOverview.Image.color = state.People || state.Council ? Raised : ChromeRaised;
        _tribePeople.Image.color = state.People ? ChromeRaised : Raised;
        _tribeCouncil.Button.interactable = ready && member && !review;
        _tribeCouncil.Image.color = state.Council ? ChromeRaised : Raised;
        _tribeTitle.text = info == null ? "Your Tribe" : noTribe ? "Find your people" : info.Name;
        _tribeSummary.text = info == null ? "No current membership observation · Refresh to read"
            : noTribe ? "No Tribe · " + (int)state.ObservationAge + "s ago"
            : info.Role + " · " + info.Members + (info.Members == 1 ? " member" : " members") + " · " + (int)state.ObservationAge + "s ago";
        SetTribeDetail(_tribeNoticeScroll, _tribeNotice, !member ? "Create a Tribe or join one from Invites."
            : info!.Announcement.Length > 0 ? info.Announcement : "No announcement posted.");
        _tribeRefresh.Button.interactable = ready && !review;
        var roster = state.Member;
        _tribeMemberRole.text = roster?.Role ?? (state.MembersRead ? "No member on this page" : "Roster not read");
        SetTribeDetail(_tribeAccountScroll, _tribeMemberAccount, roster == null
            ? TribeListHelp(state, "members", state.MembersRead, "Select Refresh to read this roster page. Accounts identify saved membership, not online status.",
                "No member returned. Go to the previous page or refresh Tribe if membership changed.") : "Account\n" + roster.Account
                + "\n\n" + (roster.Role == "Leader" ? "Leads this Tribe and manages officers."
                    : roster.Role == "Officer" ? "Can invite players and manage shared tasks." : "Can read shared tasks and participate in the Tribe."));
        _tribeMemberPage.text = TribePage(state.MemberPage, roster != null, state.MoreMembers, state.MembersRead, "member");
        _tribeMembers.Button.interactable = ready && member && !review;
        _tribeMemberPrevious.Button.interactable = ready && member && state.MemberPage > 1 && !review;
        _tribeMemberNext.Button.interactable = ready && member && state.MoreMembers && !review;
        var task = state.Task;
        _tribeTaskState.text = task == null ? "TASKS" : task.Done ? "COMPLETED" : "OPEN TASK";
        _tribeTaskState.color = task?.Done == true ? Mint : Muted;
        _tribeTaskPage.text = "#" + state.TaskPage;
        if (_tribeTaskPinId != (task?.Id ?? "")) _tribeLocalReceipt = "";
        _tribeTaskPinId = task?.Id ?? "";
        _tribeTaskToPin = task?.Text ?? "";
        _tribePinTask.Button.interactable = ready && task != null && !review;
        _tribeCopyTask.Button.interactable = _tribeCanDraftTask && task != null;
        _tribeCopyTask.Label.text = state.DraftAction == "copy-task" ? "Replace draft?" : "Copy to draft";
        _tribeClearTask.Label.text = state.DraftAction == "clear-task" ? "Discard draft?" : "Clear draft";
        _tribeClearName.Label.text = state.DraftAction == "clear-name" ? "Discard draft?" : "Clear draft";
        SetTribeDetail(_tribeTaskScroll, _tribeTaskText, task?.Text ?? TribeListHelp(state, "tasks", state.TasksRead,
            "Select Refresh task to read shared objectives.", "No task on this page. Try Previous; officers and leaders can add a task below."));
        _tribeTasks.Button.interactable = ready && member && !review;
        _tribeTaskPrevious.Button.interactable = ready && member && state.TaskPage > 1 && !review;
        _tribeTaskNext.Button.interactable = ready && member && state.MoreTasks && !review;
        _tribeTaskInput.interactable = ready && manage && !review;
        _tribeTaskDone.Button.interactable = ready && manage && task is { Done: false } && !review;
        _tribeTaskDone.Image.gameObject.SetActive(task?.Done != true);
        _tribeTaskRemove.Image.gameObject.SetActive(task?.Done == true);
        _tribeTaskRemove.Button.interactable = ready && manage && task is { Done: true } && !review;
        _tribeDiscuss.Button.interactable = ready && member && task != null && !review;
        _tribeDiscuss.Label.text = state.DiscussionReplacePending ? "Replace draft?" : "Discuss…";
        _tribeManageTitle.text = noTribe ? "START A TRIBE" : "MEMBERSHIP";
        SetTribeDetail(_tribeManageScroll, _tribeManageHint, info == null ? "Refresh to read your current membership and available actions."
            : noTribe ? "Join from an incoming invitation, or give a new Tribe a name. Creating one makes you its leader."
            : info?.Role == "Leader" ? "Lead your Tribe: invite players and coordinate shared tasks. Open Council to manage members, transfer leadership or publish an announcement."
            : manage ? "Coordinate your Tribe: invite players, manage shared tasks and publish announcements in Council. You can remove Members; the leader manages Officer roles."
            : "Read the roster and shared tasks. Pin a task to your personal watch to follow it. Officers and leaders manage shared tasks.");
        _tribeDraftLabel.text = noTribe ? "Tribe name · up to 48 characters"
            : info?.Role == "Leader" ? "Open Council to transfer leadership before leaving."
            : member ? "Membership actions" : "Refresh to unlock actions";
        _tribeDraftLabel.color = Muted;
        _tribeNameInput.gameObject.SetActive(noTribe);
        _tribeClearName.Image.gameObject.SetActive(noTribe);
        _tribeNameInput.interactable = ready && noTribe && !review;
        _tribeCreate.Image.gameObject.SetActive(noTribe);
        _tribePeopleShortcut.Image.gameObject.SetActive(!noTribe);
        _tribePeopleShortcut.Label.text = manage ? "Find players" : "Read invitations";
        _tribePeopleShortcut.Button.interactable = ready && info != null && !review;
        _tribeLeave.Button.interactable = ready && member && info!.Role != "Leader" && !review;
        _tribeLeave.Label.text = info?.Role == "Leader" ? "Transfer first" : "Leave Tribe…";
        SetTribeDraft(_tribeNameInput, state.NameDraft);
        SetTribeDraft(_tribeTaskInput, state.TaskDraft);
        SetTribeCouncilState(state, ready, review);
        RefreshTribeDraftBudget();
        var draftProblem = _tribeTaskInput.isFocused ? ClientTribeApp.DraftProblem("task", _tribeTaskInput.text)
            : _tribeNameInput.isFocused ? ClientTribeApp.DraftProblem("name", _tribeNameInput.text)
            : _tribeAnnouncementInput.isFocused ? ClientTribeApp.DraftProblem("announcement", _tribeAnnouncementInput.text) : "";
        _tribeAppMessage.text = review ? state.ReviewAction + " · confirm within " + state.ReviewSeconds + "s · Cancel keeps your draft"
            : draftProblem.Length > 0 ? draftProblem : state.Message;
        var target = state.Target;
        _tribeTargetName.text = target?.Name ?? (state.TargetsRead ? "No eligible player" : "Players not read");
        SetTribeDetail(_tribeTargetScroll, _tribeTargetDetail, target == null ? TribeListHelp(state, "targets", state.TargetsRead,
            "Read players to find someone online who has not joined a Tribe.", "No eligible player on this page. Try Previous or refresh players later.")
            : target.Name + "\nAccount · " + target.Account + (manage ? "" : "\nOnly officers and leaders can invite."));
        _tribeTargetPage.text = TribePage(state.TargetPage, target != null, state.MoreTargets, state.TargetsRead, "eligible player");
        _tribeTargets.Button.interactable = ready && info != null && !review;
        _tribeTargetPrevious.Button.interactable = ready && info != null && state.TargetPage > 1 && !review;
        _tribeTargetNext.Button.interactable = ready && info != null && state.MoreTargets && !review;
        _tribeInvite.Button.interactable = ready && manage && target != null && !review;
        var invitation = state.Invitation;
        _tribeInvitationName.text = invitation?.Name ?? (state.InvitationsRead ? "No invitation on this page" : "Invitations not read");
        var remaining = invitation == null ? 0 : Math.Max(0, (invitation.Expires - DateTimeOffset.UtcNow).TotalSeconds);
        _tribeInvitationDetail.text = invitation == null ? TribeListHelp(state, "invitations", state.InvitationsRead,
            "Read invitations sent to your account.", "No invite here. Ask an officer or leader for an invitation.")
            : "Expires in " + (remaining >= 60 ? Math.Ceiling(remaining / 60) + " min" : Math.Ceiling(remaining) + " sec")
                + " · " + invitation.Expires.ToLocalTime().ToString("t") + " local" + (member ? "\nLeave your Tribe before joining." : "\nJoining clears your other invites.");
        _tribeInvitationDetail.color = invitation != null && remaining <= 60 ? Amber : Muted;
        _tribeInvitationPage.text = TribePage(state.InvitationPage, invitation != null, state.MoreInvitations, state.InvitationsRead, "invitation");
        _tribeInvitations.Button.interactable = ready && info != null && !review;
        _tribeInvitationPrevious.Button.interactable = ready && info != null && state.InvitationPage > 1 && !review;
        _tribeInvitationNext.Button.interactable = ready && info != null && state.MoreInvitations && !review;
        _tribeAccept.Button.interactable = ready && noTribe && invitation != null && !review;
        _tribeDecline.Button.interactable = ready && info != null && invitation != null && !review;
        _tribeReceiptScroll.Root.gameObject.SetActive(!review);
        _tribeReviewScroll.Root.gameObject.SetActive(review);
        SetTribeDetail(_tribeReceiptScroll, _tribeReceipt, _tribeLocalReceipt.Length > 0 ? _tribeLocalReceipt : state.Receipt.Length > 0 ? state.Receipt
            : "Observations expire after 30 seconds. Drafts stay local until you prepare and confirm a change. Server receipts appear here.");
        SetTribeDetail(_tribeReviewScroll, _tribeReview, review ? state.Review : "Prepare a change to review it here before sending.");
        _tribeConfirmVerb = state.ConfirmVerb;
        _tribeConfirm.Label.text = state.ReviewAction;
        _tribeConfirm.Button.interactable = ready && _tribeConfirmVerb.Length > 0;
        _tribeCancel.Button.interactable = ready && review;
    }
}
