// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using TMPro;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientTerminalView
{
    private ButtonWidget _tribeCouncil = null!, _tribeTaskRemove = null!, _tribeDiscuss = null!;
    private RectTransform _tribeCouncilPanel = null!;
    private ScrollArea _tribeCouncilMemberScroll = null!, _tribeCouncilHelpScroll = null!;
    private TextMeshProUGUI _tribeCouncilMember = null!, _tribeCouncilHelp = null!, _tribeAnnouncementBudget = null!;
    private ButtonWidget _tribeCouncilPrevious = null!, _tribeCouncilRefresh = null!, _tribeCouncilNext = null!;
    private ButtonWidget _tribePromote = null!, _tribeDemote = null!, _tribeRemoveMember = null!, _tribeTransfer = null!;
    private ButtonWidget _tribePostAnnouncement = null!, _tribeClearAnnouncement = null!;
    private TMP_InputField _tribeAnnouncementInput = null!;
    private bool _tribeCanDraftAnnouncement;

    private void BuildTribeCouncil()
    {
        _tribeCouncilPanel = Node(_tribeAppSurface!, "TribeCouncilPanel");
        Place(_tribeCouncilPanel, 0f, 144f, 860f, 320f);
        var member = RoundedPanel(_tribeCouncilPanel, "CouncilMember", Surface, 0f, 0f, 392f, 320f).rectTransform;
        TribeText(member, "CouncilMemberHeading", "SELECTED MEMBER", 19f, Muted, 22f, 16f, 348f, 26f);
        _tribeCouncilMemberScroll = Scroll(member, "CouncilMemberIdentity", 22f, 50f, 348f, 90f);
        _tribeCouncilMember = Text(_tribeCouncilMemberScroll.Content, "MemberIdentity", "", 20f, Ink, 0f, 0f,
            _tribeCouncilMemberScroll.Width, 90f);
        _tribeCouncilPrevious = TribeButton(member, "CouncilPreviousMember", "Previous", 22f, 148f, 106f, "members-prev");
        _tribeCouncilRefresh = TribeButton(member, "CouncilReadMember", "Refresh", 140f, 148f, 100f, "members");
        _tribeCouncilNext = TribeButton(member, "CouncilNextMember", "Next", 252f, 148f, 118f, "members-next");
        _tribePromote = TribeButton(member, "PromoteMember", "Promote…", 22f, 206f, 166f, "prepare-promote");
        _tribeDemote = TribeButton(member, "DemoteOfficer", "Demote…", 204f, 206f, 166f, "prepare-demote");
        _tribeRemoveMember = TribeButton(member, "RemoveMember", "Remove…", 22f, 264f, 166f, "prepare-kick");
        _tribeTransfer = TribeButton(member, "TransferLeadership", "Transfer leadership…", 204f, 264f, 166f, "prepare-leader");
        _tribeTransfer.Label.fontSize = 16f;

        var announcement = RoundedPanel(_tribeCouncilPanel, "CouncilAnnouncement", Surface, 412f, 0f, 448f, 320f).rectTransform;
        TribeText(announcement, "AnnouncementHeading", "PUBLISH AN ANNOUNCEMENT", 19f, Muted, 22f, 16f, 404f, 26f);
        _tribeCouncilHelpScroll = Scroll(announcement, "CouncilHelp", 22f, 50f, 404f, 62f);
        _tribeCouncilHelp = Text(_tribeCouncilHelpScroll.Content, "CouncilHelpText", "", 18f, Ink, 0f, 0f,
            _tribeCouncilHelpScroll.Width, 62f);
        _tribeAnnouncementInput = TribeInput(announcement, "announcement", 320, 22f, 124f, 404f);
        _tribeAnnouncementBudget = Text(announcement, "AnnouncementBudget", "", 18f, Muted, 22f, 182f, 404f, 68f);
        _tribePostAnnouncement = TribeButton(announcement, "PostAnnouncement", "Post…", 22f, 264f, 194f, "prepare-announce");
        _tribeClearAnnouncement = TribeButton(announcement, "ClearPublishedAnnouncement", "Clear published…", 232f, 264f, 194f, "prepare-clear-announcement");
        _tribeClearAnnouncement.Label.fontSize = 17f;
        _tribeCouncilPanel.gameObject.SetActive(false);
    }

    private void SetTribeCouncilState(ClientTribeAppSnapshot state, bool ready, bool review)
    {
        _tribeCouncilPanel.gameObject.SetActive(state.Council);
        if (!state.Council) _tribeAnnouncementInput.DeactivateInputField();
        var info = state.Info;
        var role = info?.Id != null ? info.Role : "";
        var member = state.Member;
        var available = ready && info?.Id != null && !review;
        _tribeCanDraftAnnouncement = available && role is "Leader" or "Officer";
        SetTribeDetail(_tribeCouncilMemberScroll, _tribeCouncilMember, member == null
            ? TribeListHelp(state, "members", state.MembersRead, "Read a member to review Council actions.", "No member on this page. Refresh Tribe if the roster changed.")
            : member.Role + " · page " + state.MemberPage + "\n" + member.Account + "\nMember: " + member.Id);
        _tribeCouncilPrevious.Button.interactable = available && state.MemberPage > 1;
        _tribeCouncilRefresh.Button.interactable = available;
        _tribeCouncilNext.Button.interactable = available && state.MoreMembers;
        _tribePromote.Button.interactable = available && ClientTribeApp.CanManageMember("promote", role, member);
        _tribeDemote.Button.interactable = available && ClientTribeApp.CanManageMember("demote", role, member);
        _tribeRemoveMember.Button.interactable = available && ClientTribeApp.CanManageMember("kick", role, member);
        _tribeTransfer.Button.interactable = available && ClientTribeApp.CanManageMember("leader", role, member);
        SetTribeDetail(_tribeCouncilHelpScroll, _tribeCouncilHelp, role is "Leader" or "Officer"
            ? "Write a new announcement to replace the published one. The header can show shortened server text; it is never copied into your draft automatically."
            : "Officers and leaders publish announcements. Only the leader changes Officer roles or transfers leadership; Officers may remove Members.");
        _tribeAnnouncementInput.interactable = _tribeCanDraftAnnouncement;
        SetTribeDraft(_tribeAnnouncementInput, state.AnnouncementDraft);
        _tribeClearAnnouncement.Button.interactable = _tribeCanDraftAnnouncement && info!.Announcement.Length > 0;
    }

    private void RefreshCouncilDraftBudget()
    {
        if (_tribeAnnouncementInput == null) return;
        var draft = _tribeAnnouncementInput.text ?? "";
        var problem = ClientTribeApp.DraftProblem("announcement", draft);
        _tribeAnnouncementBudget.text = draft.Length + "/320 · " + ClientTribeApp.DraftBytes("announcement", draft)
            + "/256 command bytes\n" + (draft.Length == 0 ? "Post replaces the announcement; Clear published removes it." : problem);
        _tribeAnnouncementBudget.color = draft.Length > 0 && problem.Length > 0 ? Amber : Muted;
        _tribePostAnnouncement.Button.interactable = _tribeCanDraftAnnouncement && problem.Length == 0;
    }
}
