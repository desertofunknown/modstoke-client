// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Client;

internal sealed partial class ClientTribeApp
{
    private bool _council;
    private string _announcementDraft = "";
    private DiscussionReview? _discussionReview;
    private sealed record DiscussionReview(Guid Session, string TribeId, string TaskId,
        string Draft, string Replacing, long ExpiresAt);

    // These checks describe the observed Tribe roles; the server remains authoritative.
    internal static bool CanManageMember(string action, string role, ClientTribeMember? member)
    {
        if (member == null) return false;
        return action switch {
            "promote" => role == "Leader" && member.Role == "Member",
            "demote" => role == "Leader" && member.Role == "Officer",
            "kick" => role == "Leader" && member.Role is "Member" or "Officer"
                || role == "Officer" && member.Role == "Member",
            "leader" => role == "Leader" && member.Role is "Member" or "Officer",
            _ => false
        };
    }

    private bool HandleCouncilCommand(string verb)
    {
        if (verb == "council")
        {
            if (_info?.Id == null) { _message = "Refresh your Tribe before opening Council."; return true; }
            _council = true; _people = false; _loadTasks = false;
            if (!Fresh(_memberAt)) QueueRead("members", _memberPage);
            return true;
        }
        if (verb is "prepare-promote" or "prepare-demote" or "prepare-kick" or "prepare-leader")
        {
            var action = verb[8..];
            if (_member == null || !Fresh(_memberAt) || !CanManageMember(action, _info?.Role ?? "", _member))
            { _message = "Read a current roster member and check your Tribe role before preparing this change."; return true; }
            var member = _member;
            var consequence = action switch {
                "promote" => "Make this member an Officer. They can invite players and manage shared tasks; this grants no server administrator powers.",
                "demote" => "Make this Officer a Member. Their outstanding sent invitations will be removed.",
                "kick" => "Remove this member from the Tribe. They will need another invitation to return.",
                _ => "Make this member the Tribe leader. You become an Officer and lose leader-only permissions."
            };
            Prepare(action, "/tribe " + action + " " + member.Id,
                consequence + "\nAccount: " + member.Account + "\nMember: " + member.Id + "\nObserved role: " + member.Role, member.Id);
            return true;
        }
        if (verb == "prepare-remove-task")
        {
            if (!CanManage || _task is not { Done: true } || !Fresh(_taskAt))
            { _message = "Read a completed task first. Officers and leaders can remove completed tasks."; return true; }
            Prepare("remove-task", "/tribe tasks remove " + _task.Id,
                "Remove this completed task from the shared list?\n" + _task.Text + "\nTask: " + _task.Id, _task.Id);
            return true;
        }
        if (verb is "prepare-announce" or "prepare-clear-announcement")
        {
            if (!CanManage) { _message = "Officers and leaders can change the Tribe announcement."; return true; }
            if (verb == "prepare-announce")
                PrepareText("announce", "/tribe announce ", _announcementDraft, 320,
                    "Replace the published Tribe announcement with:\n");
            else
                Prepare("clear-announcement", "/tribe announce", "Clear the published Tribe announcement? Your unsent local announcement draft is kept.");
            return true;
        }
        return false;
    }

    // The terminal supplies its current Tribe draft and opens Messages only after this returns true.
    // This method never submits a message or changes shared task state.
    internal bool TryPrepareDiscussion(string existingTribeDraft, out string draft)
    {
        draft = "";
        if (!RefreshSession() || !_visible || !Ready || _people || _council || _review != null
            || _info?.Id == null || !Fresh(_infoAt) || _task == null || !Fresh(_taskAt))
        {
            ClearDiscussion();
            _message = "Read the selected task and current Tribe before discussing it.";
            return false;
        }
        ClearDraftAction();
        var text = "Task: " + _task.Text;
        var replacing = existingTribeDraft ?? "";
        if (!string.IsNullOrWhiteSpace(replacing) && replacing != text)
        {
            var review = _discussionReview;
            if (review == null || review.Session != _session || review.TribeId != _info.Id
                || review.TaskId != _task.Id || review.Draft != text || review.Replacing != replacing || Now >= review.ExpiresAt)
            {
                _discussionReview = new(_session!.Value, _info.Id, _task.Id, text, replacing,
                    Math.Min(Now + 8000, Math.Min(_infoAt + 30000, _taskAt + 30000)));
                _message = "Tribe draft exists. Select Replace draft? within 8 seconds. Nothing is sent.";
                return false;
            }
        }
        ClearDiscussion();
        draft = text;
        _message = "Task discussion prepared. Review the Tribe message and press Send when ready.";
        return true;
    }

    private void ClearDiscussion() => _discussionReview = null;

    private void ExpireDiscussion()
    {
        if (_discussionReview is not { } review) return;
        if (Now >= review.ExpiresAt || review.Session != _session || _info?.Id != review.TribeId
            || !Fresh(_infoAt) || _task?.Id != review.TaskId || !Fresh(_taskAt))
            ClearDiscussion();
    }
}
