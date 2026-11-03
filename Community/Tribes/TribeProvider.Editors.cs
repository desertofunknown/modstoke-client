// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using Modstoke.Loader;

namespace Modstoke.Client;

internal sealed partial class ClientTribeProvider
{
    private Guid _scope = Guid.NewGuid();
    private Form _form;
    private string _input = "";
    private Review? _review;

    private enum Form {
        Browse, Membership, Roles, ManageTasks,
        Create, Invite, Accept, Decline, CancelInvitation, Leave, Disband,
        Kick, Promote, Demote, Leader, Announce, AddTask, DoneTask, RemoveTask
    }
    private sealed record Review(Guid Token, string Command);

    public MenuInputSnapshot GetInputs()
    {
        lock (_sync)
        {
            var fields = new List<MenuInputFieldSnapshot>();
            if (InputFieldId is { } fieldId)
                fields.Add(new(fieldId, InputLabel, _input, InputHint, InputLimit, CanRequest));
            return new MenuInputSnapshot(_scope, fields);
        }
    }

    public bool TrySetInput(Guid scope, string fieldId, string value, out CommandResultSnapshot result)
    {
        lock (_sync)
        {
            if (scope != _scope || !CanRequest)
                return Fail("This form is stale or a request is still pending.", out result);
            // Even a rejected edit cancels the previous confirmation.
            _review = null;
            if (InputFieldId == null || fieldId != InputFieldId)
                return Fail("That field is unavailable in this form.", out result);
            if (value == null || value.Length > InputLimit)
                return Fail("This field allows at most " + InputLimit.ToString(CultureInfo.InvariantCulture) + " characters.", out result);
            if (!ClientCommandText.IsSafeText(value))
                return Fail("Use plain single-line text without controls, angle brackets, invisible formatting or supplementary characters.", out result);
            _input = value;
            _status = "Draft updated in memory. Review again before sending a change.";
            result = CommandResultSnapshot.Success(_status);
            return true;
        }
    }

    private string? InputFieldId => _form switch {
        Form.Create => "name",
        Form.Invite => "target",
        Form.Accept or Form.Decline or Form.CancelInvitation or Form.Kick or Form.Promote
            or Form.Demote or Form.Leader or Form.DoneTask or Form.RemoveTask => "id",
        Form.Announce or Form.AddTask => "text",
        _ => null
    };

    private int InputLimit => _form switch {
        Form.Create => 48,
        Form.Announce => 320,
        Form.AddTask => 160,
        _ => 36
    };

    private string InputLabel => _form switch {
        Form.Create => "Tribe name",
        Form.Invite => "Online session ID or host",
        Form.Accept or Form.Decline => "Incoming invitation ID",
        Form.CancelInvitation => "Sent invitation ID",
        Form.Kick or Form.Promote or Form.Demote or Form.Leader => "Member ID",
        Form.Announce => "Announcement",
        Form.AddTask => "Task text",
        _ => "Task ID"
    };

    private string InputHint => _form switch {
        Form.Create => "1–48 characters after trimming; the full command must fit in 256 UTF-8 bytes.",
        Form.Invite => "Use a session ID from Online invite targets, or host. Never enter an account ID.",
        Form.Accept or Form.Decline => "Use an ID from Invitations: full GUID or an unambiguous 8–32 hexadecimal prefix.",
        Form.CancelInvitation => "Use an ID from Sent invitations: full GUID or an unambiguous 8–32 hexadecimal prefix.",
        Form.Kick or Form.Promote or Form.Demote or Form.Leader => "Use a member ID from Roster, never an account ID: full GUID or 8–32 hexadecimal prefix.",
        Form.Announce => "Up to 320 characters after trimming; empty clears it. The full command must fit in 256 UTF-8 bytes.",
        Form.AddTask => "1–160 characters after trimming; the full command must fit in 256 UTF-8 bytes.",
        _ => "Use an ID from Tasks: full GUID or an unambiguous 8–32 hexadecimal prefix."
    };

    private void AddEditorCommands(List<CommandSnapshot> actions, bool ready)
    {
        if (!IsGroup(_form))
        {
            var source = EditorSourceView;
            actions.Add(Action("source-read", "Read " + ViewLabel(source, _view == source ? _page : 1),
                "Read current IDs or membership without discarding this draft. Reading cancels its review.", ready));
            if (source != View.Info)
            {
                actions.Add(Action("source-previous", "Read previous source item", "Keep the draft and request the preceding source item.", ready && _view == source && _page > 1));
                actions.Add(Action("source-next", "Read next source item", "Keep the draft and request the next source item; the total is unknown.", ready && _view == source && _page < MaximumPage(source)));
            }
        }
        switch (_form)
        {
            case Form.Membership:
                actions.Add(Action("create", "Create tribe", "Prepare a new tribe name.", ready));
                actions.Add(Action("invite", "Invite player", "Prepare an invitation using an online session ID or host.", ready));
                actions.Add(Action("accept", "Accept invitation", "Prepare to join the invited tribe.", ready));
                actions.Add(Action("decline", "Decline invitation", "Prepare to decline an incoming invitation.", ready));
                actions.Add(Action("cancel-invitation", "Cancel sent invitation", "Prepare to revoke an outgoing invitation.", ready));
                actions.Add(Action("leave", "Leave tribe", "Prepare to leave your current tribe.", ready));
                actions.Add(Action("disband", "Disband tribe", "Prepare to delete your current tribe, invitations, tasks and history.", ready));
                break;
            case Form.Roles:
                actions.Add(Action("kick", "Remove member", "Prepare to remove a member from the tribe.", ready));
                actions.Add(Action("promote", "Promote member", "Prepare to promote a Member to Officer.", ready));
                actions.Add(Action("demote", "Demote officer", "Prepare to demote an Officer to Member.", ready));
                actions.Add(Action("leader", "Transfer leadership", "Prepare to transfer leadership to another member.", ready));
                actions.Add(Action("announce", "Set or clear announcement", "Prepare announcement text; empty clears it.", ready));
                break;
            case Form.ManageTasks:
                actions.Add(Action("task-add", "Add task", "Prepare a new task.", ready));
                actions.Add(Action("task-done", "Complete task", "Prepare to mark a task complete.", ready));
                actions.Add(Action("task-remove", "Remove completed task", "Prepare to remove a completed task.", ready));
                break;
            default:
                actions.Add(Action("review", "Review change", "Validate and review the exact command and its effects.", ready));
                if (_review is { } review)
                {
                    actions.Add(Action("confirm." + review.Token.ToString("N"), "Confirm change", "Send the reviewed command once.", ready));
                    actions.Add(Action("cancel", "Cancel review", "Discard this confirmation without sending.", ready));
                }
                break;
        }
        actions.Add(Action("back", IsGroup(_form) ? "Back to browse" : "Back to " + FormLabel(ParentForm(_form)),
            "Discard this draft and confirmation.", ready));
    }

    private bool TryInvokeEditor(string action, out CommandResultSnapshot result)
    {
        if (action is "source-read" or "source-previous" or "source-next")
        {
            if (IsGroup(_form))
                return Fail("Choose a change form before reading its source.", out result);
            var source = EditorSourceView;
            var page = _view == source ? _page : 1;
            if (action != "source-read")
            {
                if (_view != source || source == View.Info)
                    return Fail("Read this form's source before paging it.", out result);
                page += action == "source-next" ? 1 : -1;
                if (page < 1 || page > MaximumPage(source))
                    return Fail("That source item is outside the available page range.", out result);
            }
            _view = source;
            _page = page;
            _scope = Guid.NewGuid();
            Queue(ReadCommand(source, page), false);
        }
        else if (action == "back")
        {
            ResetForm(ParentForm(_form));
            _status = "Draft discarded; nothing sent.";
        }
        else if (IsGroup(_form))
        {
            var next = (_form, action) switch {
                (Form.Membership, "create") => Form.Create,
                (Form.Membership, "invite") => Form.Invite,
                (Form.Membership, "accept") => Form.Accept,
                (Form.Membership, "decline") => Form.Decline,
                (Form.Membership, "cancel-invitation") => Form.CancelInvitation,
                (Form.Membership, "leave") => Form.Leave,
                (Form.Membership, "disband") => Form.Disband,
                (Form.Roles, "kick") => Form.Kick,
                (Form.Roles, "promote") => Form.Promote,
                (Form.Roles, "demote") => Form.Demote,
                (Form.Roles, "leader") => Form.Leader,
                (Form.Roles, "announce") => Form.Announce,
                (Form.ManageTasks, "task-add") => Form.AddTask,
                (Form.ManageTasks, "task-done") => Form.DoneTask,
                (Form.ManageTasks, "task-remove") => Form.RemoveTask,
                _ => Form.Browse
            };
            if (next == Form.Browse)
                return Fail("That action is unavailable in this group.", out result);
            ResetForm(next);
            _status = "Prepare the change, then review its command and effects.";
        }
        else if (action == "cancel")
            _status = "Review cancelled; nothing sent.";
        else if (action == "review")
        {
            if (!TryBuildMutation(out var command, out var error))
                return Fail(error, out result);
            _review = new Review(Guid.NewGuid(), command);
            _status = "Review the exact command and effects, then confirm to send once.";
        }
        else
            return Fail("That editor action is unavailable.", out result);
        result = CommandResultSnapshot.Success(_status);
        return true;
    }

    private bool TryBuildMutation(out string command, out string error)
    {
        command = "";
        error = "";
        var value = _input.Trim();
        if (!ClientCommandText.IsSafeText(_input) || value.Length > InputLimit)
        {
            error = "Use plain single-line text within this field's limit, without controls, angle brackets, invisible formatting or supplementary characters.";
            return false;
        }
        if (InputFieldId == "id" && !ClientCommandText.ValidId(value))
        {
            error = "Enter a full GUID or an unambiguous 8–32 hexadecimal ID prefix from the matching read view; never an account ID.";
            return false;
        }
        if (_form == Form.Invite)
        {
            if (string.Equals(value, "host", StringComparison.OrdinalIgnoreCase))
                value = "host";
            else if (!ClientCommandText.ValidId(value))
            {
                error = "Enter host or a session GUID or 8–32 hexadecimal prefix from Online invite targets; never an account ID.";
                return false;
            }
        }
        if ((_form is Form.Create or Form.AddTask) && value.Length == 0)
        {
            error = _form == Form.Create ? "Enter a tribe name." : "Enter task text.";
            return false;
        }
        command = _form switch {
            Form.Create => "/tribe create " + value,
            Form.Invite => "/invite " + value,
            Form.Accept => "/tribe accept " + value,
            Form.Decline => "/tribe decline " + value,
            Form.CancelInvitation => "/tribe cancel " + value,
            Form.Leave => "/tribe leave",
            Form.Disband => "/tribe disband confirm",
            Form.Kick => "/tribe kick " + value,
            Form.Promote => "/tribe promote " + value,
            Form.Demote => "/tribe demote " + value,
            Form.Leader => "/tribe leader " + value,
            Form.Announce => "/tribe announce" + (value.Length == 0 ? "" : " " + value),
            Form.AddTask => "/tasks add " + value,
            Form.DoneTask => "/tasks done " + value,
            Form.RemoveTask => "/tasks remove " + value,
            _ => ""
        };
        if (!ClientCommandText.IsCommand(command))
        {
            error = "The complete command, including its verb, must fit in 256 UTF-8 bytes. Shorten the text.";
            return false;
        }
        return true;
    }

    private void AddEditorContent(List<MenuRowSnapshot> rows)
    {
        if (_review is { } review)
            rows.Add(Row("Confirm this exact command", review.Command));
        if (InputFieldId != null)
            rows.Add(Row("Input", InputHint + " Leading and trailing whitespace is trimmed; quotes and backslashes are literal."));
        var effect = _form switch {
            Form.Membership => "Choose an action to prepare a change. Browse invitations and online targets first to obtain the correct IDs.",
            Form.Roles => "Use member IDs from Roster. Permissions are checked when the server executes the command; no role is inferred from cached replies.",
            Form.ManageTasks => "Use task IDs from Tasks. Removing a task requires it to be completed first.",
            Form.Create => "Creates a tribe with you as Leader and clears your incoming invitations. The server checks your current membership and the name.",
            Form.Invite => "Sends an invitation to the selected online session or host. The server checks the target's current membership and invitation rules.",
            Form.Accept => "Joins the tribe named by the incoming invitation and clears your other incoming invitations. Verify the invitation ID before confirming.",
            Form.Decline => "Removes the selected incoming invitation without joining the tribe.",
            Form.CancelInvitation => "Revokes the selected sent invitation, so its recipient can no longer accept it.",
            Form.Leave => "Removes you from your current tribe and its member access. A Leader must transfer leadership before leaving.",
            Form.Disband => "Deletes the current tribe, its invitations, tasks and history. All members lose membership. Only the Leader can disband; verify My tribe before confirming.",
            Form.Kick => "Removes the selected member from the tribe and its member access. Officers may remove Members; the Leader may remove Members or Officers. Verify the roster ID.",
            Form.Promote => "Changes the selected Member to Officer. Only the Leader can promote; this grants tribe permissions, not server administrator access.",
            Form.Demote => "Changes the selected Officer to Member, removing officer permissions. Only the Leader can demote.",
            Form.Leader => "Transfers leadership to the selected member and makes you an Officer. You lose Leader-only permissions. Verify the roster ID before confirming.",
            Form.Announce => "Replaces the tribe announcement. Empty text clears the current announcement. The server checks your permission.",
            Form.AddTask => "Creates a new tribe task. Adding the same text again can create another task; read Tasks after an unknown result before preparing another addition.",
            Form.DoneTask => "Marks the selected task complete. Verify the task ID and its current state before confirming.",
            Form.RemoveTask => "Removes the selected completed task from the tribe's task list. Active tasks cannot be removed; verify the task ID before confirming.",
            _ => null
        };
        if (effect != null)
            rows.Add(Row("Effects", effect));
    }

    private void ResetForm(Form form)
    {
        _form = form;
        _scope = Guid.NewGuid();
        _input = "";
        _review = null;
    }

    private static bool IsGroup(Form form) => form is Form.Membership or Form.Roles or Form.ManageTasks;

    private View EditorSourceView => _form switch {
        Form.Invite => View.Targets,
        Form.Accept or Form.Decline => View.Invitations,
        Form.CancelInvitation => View.Sent,
        Form.Kick or Form.Promote or Form.Demote or Form.Leader => View.Roster,
        Form.AddTask or Form.DoneTask or Form.RemoveTask => View.Tasks,
        _ => View.Info
    };

    private View FollowUpView => _form switch {
        Form.Invite or Form.CancelInvitation => View.Sent,
        Form.Decline => View.Invitations,
        Form.Kick or Form.Promote or Form.Demote or Form.Leader => View.Roster,
        Form.AddTask or Form.DoneTask or Form.RemoveTask => View.Tasks,
        _ => View.Info
    };

    private static Form ParentForm(Form form) => form switch {
        Form.Create or Form.Invite or Form.Accept or Form.Decline or Form.CancelInvitation or Form.Leave or Form.Disband => Form.Membership,
        Form.Kick or Form.Promote or Form.Demote or Form.Leader or Form.Announce => Form.Roles,
        Form.AddTask or Form.DoneTask or Form.RemoveTask => Form.ManageTasks,
        _ => Form.Browse
    };

    private static string FormLabel(Form form) => form switch {
        Form.Membership => "Membership",
        Form.Roles => "Roles and notices",
        Form.ManageTasks => "Manage tasks",
        Form.Create => "Create tribe",
        Form.Invite => "Invite player",
        Form.Accept => "Accept invitation",
        Form.Decline => "Decline invitation",
        Form.CancelInvitation => "Cancel sent invitation",
        Form.Leave => "Leave tribe",
        Form.Disband => "Disband tribe",
        Form.Kick => "Remove member",
        Form.Promote => "Promote member",
        Form.Demote => "Demote officer",
        Form.Leader => "Transfer leadership",
        Form.Announce => "Set or clear announcement",
        Form.AddTask => "Add task",
        Form.DoneTask => "Complete task",
        Form.RemoveTask => "Remove completed task",
        _ => "Browse"
    };
}
