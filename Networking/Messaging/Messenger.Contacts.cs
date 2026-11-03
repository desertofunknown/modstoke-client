// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using Modstoke.Sons;

namespace Modstoke.Client;

internal readonly record struct ClientMessengerContactState(bool CanRequest, bool CanFind, bool Busy,
    bool Current, bool TargetReady, bool Searching, string Target, string Detail);

internal sealed partial class ClientMessenger
{
    private const int ContactPageLimit = 64;
    private const long ContactSearchDuration = 45_000;
    private const long ContactSearchPageInterval = 500;
    private ClientCommand? _contactsRequest;
    private Guid _contactsSession;
    private int _requestedPage;
    private bool _contactsCurrent;
    private bool _supportsContactReplies;
    private bool _discardContactsReply;
    private string _contactSearchTarget = "";
    private int _contactSearchNextPage;
    private int _contactSearchPagesChecked;
    private int _contactSearchContactsChecked;
    private long _contactSearchDeadline;
    private long _contactSearchNextAt;
    private string _contactDetail = "Refresh contacts to check current recipients.";

    private bool ContactSearchActive => _contactSearchTarget.Length > 0;
    private bool ContactsBusy => _contactsRequest != null || ContactSearchActive;
    internal bool DirectTargetReady => Available && IsListedTarget();
    internal ClientMessengerContactState ContactState
    {
        get
        {
            var canRequest = Available && _supportsContactReplies && !ContactsBusy && PendingCount < PendingLimit;
            var detail = !_session.HasValue ? "Join a compatible server to request current contacts."
                : !_supportsContactReplies ? "This server does not support contact replies."
                : !Available ? "Messenger is unavailable on this server." : _contactDetail;
            return new(canRequest, canRequest && Channel == "direct" && IsContactId(Target), ContactsBusy,
                _contactsCurrent, DirectTargetReady, ContactSearchActive, Target, detail);
        }
    }

    internal void LoadContacts(int page)
    {
        if (!RefreshSession()) return;
        if (page < 1 || page > ContactPageLimit)
        {
            SetContactDetail("That contacts page is invalid.");
            return;
        }
        if (ContactsBusy || PendingCount >= PendingLimit)
        {
            Status = "Wait for the current request, or cancel the contact lookup first.";
            return;
        }
        // Retained rows and the conversation stay visible, but do not authorize sends.
        _contactsCurrent = false;
        RequestContactPage(page);
    }

    internal void FindCurrentContact()
    {
        if (!RefreshSession()) return;
        if (Channel != "direct" || !IsContactId(Target))
        {
            SetContactDetail("Open a direct conversation or select a contact before looking it up.");
            return;
        }
        if (ContactsBusy || PendingCount >= PendingLimit)
        {
            Status = "Wait for the current request, or cancel the contact lookup first.";
            return;
        }
        _contactsCurrent = false;
        if (!Available || !_supportsContactReplies)
        {
            SetContactDetail("Current contacts are unavailable on this server. The conversation is retained.");
            return;
        }
        // This is a bounded traversal authorized by Find, never a name/account lookup or a send.
        _contactSearchTarget = Target;
        _contactSearchPagesChecked = _contactSearchContactsChecked = 0;
        _contactSearchDeadline = Environment.TickCount64 + ContactSearchDuration;
        _contactSearchNextPage = 0;
        RequestContactPage(1);
    }

    internal void CancelContactSearch()
    {
        if (!OnOwnerThread || _disposed) return;
        StopContactSearch("Contact lookup cancelled. The conversation and draft are unchanged.");
    }

    internal void ResetForWorld()
    {
        if (!OnOwnerThread || _disposed) return;
        _session = null;
        _messengerSession = null;
        Available = false;
        _supportsContactReplies = false;
        _pending.Clear();
        ClearContacts();
        Status = "Messenger is waiting for the current world.";
    }

    private void UpdateContacts(long now)
    {
        if (ContactSearchActive)
        {
            if (Channel != "direct" || Target != _contactSearchTarget)
                StopContactSearch("Contact lookup cancelled when the conversation changed.");
            else if (!Available || !_supportsContactReplies)
                StopContactSearch("Contact lookup stopped because Messenger is unavailable. Presence is unknown.");
            else if (now >= _contactSearchDeadline)
                StopContactSearch("Contact lookup reached its 45-second limit after " + ContactSearchProgress()
                    + ". The result is incomplete; nothing was retried.");
        }

        if (_contactsRequest == null)
        {
            if (ContactSearchActive && _contactSearchNextPage > 0 && now >= _contactSearchNextAt)
                RequestContactPage(_contactSearchNextPage);
            return;
        }
        var state = _contactsRequest.State;
        if (!state.IsTerminal) return;
        _contactsRequest = null;
        if (_contactsSession != _session || _discardContactsReply)
        {
            _discardContactsReply = false;
            return;
        }
        if (!Available || !_supportsContactReplies)
        {
            FailContactRequest("The contact reply is no longer current because Messenger became unavailable.");
            return;
        }
        if (state.Progress != ClientCommandProgress.ServerReported)
        {
            FailContactRequest("Contacts unavailable: " + state.Progress + ". This does not show whether the person is online.");
            return;
        }
        if (state.Outcome != ClientCommandOutcome.Succeeded)
        {
            FailContactRequest("Server did not provide contacts: " + state.Outcome + ". This does not show whether the person is online.");
            return;
        }
        if (state.ReplyTruncated || !TryReadContacts(state.Reply, _requestedPage, out var contacts, out var more))
        {
            FailContactRequest("The contacts reply was incomplete or invalid. Request again explicitly; presence is unknown.");
            return;
        }

        if (!ContactSearchActive)
        {
            ApplyContactPage(contacts, more);
            SetContactDetail(contacts.Count == 0 ? "No contacts on this page. The selected conversation is retained."
                : Target.Length > 0 && !IsListedTarget()
                    ? "Selected conversation is on another page or no longer listed. Use Find current contact."
                    : "Contacts checked. The server checks each Send again.");
            return;
        }

        _contactSearchPagesChecked++;
        _contactSearchContactsChecked += contacts.Count;
        foreach (var contact in contacts)
        {
            if (!string.Equals(contact.Id, _contactSearchTarget, StringComparison.Ordinal)) continue;
            EndContactSearch();
            ApplyContactPage(contacts, more);
            SetContactDetail("Exact contact found on page " + ContactPage.ToString(CultureInfo.InvariantCulture)
                + ". Review the draft, then Send; delivery is not confirmed.");
            return;
        }
        if (!more)
        {
            EndContactSearch();
            SetContactDetail("No exact match after " + ContactSearchProgress()
                + ". They may be offline or have reconnected. Choose a current contact explicitly.");
            return;
        }
        if (_requestedPage >= ContactPageLimit)
        {
            EndContactSearch();
            SetContactDetail("Contact lookup reached the 64-page limit. The result is incomplete; nothing was retried.");
            return;
        }
        _contactSearchNextPage = _requestedPage + 1;
        SetContactDetail(ContactSearchProgress() + ". Looking for this exact connection on the next page…");
    }

    private void RequestContactPage(int page)
    {
        if (!Available || !_supportsContactReplies)
        {
            FailContactRequest("Current contacts are unavailable on this server. The retained list is for browsing.");
            return;
        }
        if (PendingCount >= PendingLimit)
        {
            FailContactRequest("The contact request limit was reached. Request again explicitly; nothing was retried.");
            return;
        }
        _contactsSession = _session!.Value;
        _requestedPage = page;
        _contactSearchNextPage = 0;
        _discardContactsReply = false;
        _contactSearchNextAt = Environment.TickCount64 + ContactSearchPageInterval;
        var sent = ClientCommands.Submit("/comms contacts " + page.ToString(CultureInfo.InvariantCulture), out var request);
        _contactsRequest = request;
        if (request == null)
        {
            FailContactRequest("Contacts were not queued: " + sent + ". Nothing was retried.");
            return;
        }
        SetContactDetail(ContactSearchActive
            ? "Finding current contact · page " + page.ToString(CultureInfo.InvariantCulture)
                + " / 64 · " + ContactSearchProgress() + (sent == ClientCommandSendResult.SendUncertain
                    ? " · request uncertain; waiting without retry" : " · Cancel to stop")
            : sent == ClientCommandSendResult.SendUncertain
                ? "Contact request uncertain. Waiting without retrying." : "Waiting for contacts; retained rows are stale.");
    }

    private void ApplyContactPage(List<ClientMessengerContact> contacts, bool more)
    {
        Contacts = contacts.AsReadOnly();
        _contactsCurrent = true;
        ContactPage = _requestedPage;
        MoreContacts = more;
        // Paging changes the browser, never the selected conversation or its draft.
    }

    private void FailContactRequest(string detail)
    {
        EndContactSearch();
        SetContactDetail(detail);
    }

    private void StopContactSearch(string detail)
    {
        if (!ContactSearchActive) return;
        EndContactSearch();
        // The SDK has no transport cancellation. Drain the one handle without applying its reply.
        _discardContactsReply = _contactsRequest != null;
        SetContactDetail(detail + (_discardContactsReply ? " Waiting for the requested page to finish." : ""));
    }

    private void EndContactSearch()
    {
        _contactSearchTarget = "";
        _contactSearchNextPage = 0;
        _contactSearchDeadline = _contactSearchNextAt = 0;
    }

    private string ContactSearchProgress() => _contactSearchPagesChecked.ToString(CultureInfo.InvariantCulture)
        + (_contactSearchPagesChecked == 1 ? " page / " : " pages / ")
        + _contactSearchContactsChecked.ToString(CultureInfo.InvariantCulture) + " entries checked";

    private void SetContactDetail(string detail) => Status = _contactDetail = detail;

    private string DirectComposeStatus() => IsListedTarget()
        ? "Current contact selected. The server checks each Send again."
        : Target.Length == 0 ? "Refresh contacts and select a recipient, or open an existing conversation."
        : "Retained conversation. Use Find current contact before replying; names do not identify connections.";

    private bool IsListedTarget()
    {
        if (!_contactsCurrent || ContactsBusy) return false;
        foreach (var contact in Contacts)
            if (contact.Id == Target) return true;
        return false;
    }

    private static bool IsContactId(string id) => id == "host"
        || id != null && id.Length == 36 && Guid.TryParseExact(id, "D", out var parsed) && parsed != Guid.Empty;

    private void ClearContacts()
    {
        _contactsRequest = null;
        _contactsSession = Guid.Empty;
        _contactsCurrent = _discardContactsReply = false;
        EndContactSearch();
        _contactSearchPagesChecked = _contactSearchContactsChecked = 0;
        _contactDetail = "Refresh contacts to check current recipients.";
        Contacts = Array.Empty<ClientMessengerContact>();
        Target = "";
        ContactPage = 1;
        MoreContacts = false;
    }
}
