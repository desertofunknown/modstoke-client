// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using TMPro;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientTerminalView
{
    private readonly Dictionary<string, ButtonWidget> _messengerChannels = new(StringComparer.Ordinal);
    private readonly List<ButtonWidget> _messengerContacts = new();
    private readonly string[] _messengerContactIds = new string[4];
    private RectTransform _messengerContactsRow = null!;
    private ButtonWidget _contactsPrevious = null!;
    private ButtonWidget _contactsNext = null!;
    private ButtonWidget _contactsRefresh = null!;
    private ButtonWidget _contactsFind = null!;
    private TextMeshProUGUI _contactsPage = null!;
    private TextMeshProUGUI _contactsEmpty = null!;
    private TextMeshProUGUI _contactsLookupStatus = null!;
    private ClientMessengerContactState _messengerContactState;
    private int _messengerContactPage = 1;
    private int _messengerContactCount;
    private bool _messengerMoreContacts;
    private string _messengerChannel = "expedition";

    private void BuildMessengerNavigation()
    {
        var channels = new[] { ("expedition", "Expedition"), ("tribe", "Tribe"), ("direct", "Direct"), ("legacy", "Legacy public") };
        for (var index = 0; index < channels.Length; index++)
        {
            var channel = channels[index].Item1;
            var button = Button(_messagesSurface, "Channel" + channel, channels[index].Item2,
                24f + index * 324f, 64f, 308f, 50f, () => MessengerCommand("channel:" + channel));
            button.Label.fontSize = 22f;
            _messengerChannels.Add(channel, button);
        }
        _messengerContactsRow = Node(_messagesSurface, "DirectRecipients");
        Place(_messengerContactsRow, 0f, 128f, WorkspaceWidth, 70f);
        _contactsPrevious = Button(_messengerContactsRow, "PreviousContacts", "<", 24f, 0f, 48f, 54f,
            () => MessengerCommand("contacts-prev"));
        for (var index = 0; index < 4; index++)
        {
            var contactIndex = index;
            var contact = Button(_messengerContactsRow, "Contact" + index, "", 80f + index * 218f, 0f, 210f, 54f, () =>
            {
                var id = _messengerContactIds[contactIndex];
                if (_messengerContactState.Current && !_messengerContactState.Busy && !string.IsNullOrWhiteSpace(id))
                    MessengerCommand("target:" + id);
            });
            contact.Label.fontSize = 21f;
            contact.Label.enableWordWrapping = false;
            contact.Label.overflowMode = TextOverflowModes.Ellipsis;
            contact.Image.gameObject.SetActive(false);
            _messengerContacts.Add(contact);
        }
        _contactsNext = Button(_messengerContactsRow, "NextContacts", ">", 952f, 0f, 48f, 54f,
            () => MessengerCommand("contacts-next"));
        _contactsRefresh = Button(_messengerContactsRow, "RefreshContacts", "Refresh", 1008f, 0f, 96f, 54f,
            () => MessengerCommand("contacts-refresh"));
        _contactsRefresh.Label.fontSize = 19f;
        _contactsFind = Button(_messengerContactsRow, "FindCurrentContact", "Find current contact", 1112f, 0f, 192f, 54f, () =>
        {
            if (!CanInteractWithInbox()) return;
            if (_messengerContactState.Searching) MessengerCommand("contacts-cancel");
            else if (_messengerContactState.CanFind) MessengerCommand("contacts-find");
        });
        _contactsFind.Label.fontSize = 19f;
        _contactsPage = Text(_messengerContactsRow, "ContactPage", "", 15f, Muted, 24f, 54f, 312f, 18f);
        _contactsPage.enableWordWrapping = false;
        _contactsPage.overflowMode = TextOverflowModes.Ellipsis;
        _contactsLookupStatus = Text(_messengerContactsRow, "ContactLookupStatus", "", 15f, Muted, 344f, 54f, 960f, 18f);
        _contactsLookupStatus.enableWordWrapping = false;
        _contactsLookupStatus.overflowMode = TextOverflowModes.Ellipsis;
        _contactsEmpty = Text(_messengerContactsRow, "NoContacts", "No recipients on this page. Refresh to request the contact list.",
            20f, Muted, 88f, 10f, 848f, 36f);
        _contactsEmpty.enableWordWrapping = false;
        _contactsEmpty.overflowMode = TextOverflowModes.Ellipsis;
        _messengerContactsRow.gameObject.SetActive(false);
        RefreshMessengerChannels();
    }

    private void MessengerCommand(string command)
    {
        if (_disposed || !_inboxVisible || _shadeCapture || _page?.App != ClientTerminalApp.Messages) return;
        _command("messages", command);
    }

    internal void SetMessengerChannel(string channel)
    {
        if (_disposed || !_messengerChannels.ContainsKey(channel)) return;
        var changed = _messengerChannel != channel;
        _messengerChannel = channel;
        var direct = channel == "direct";
        _messengerContactsRow.gameObject.SetActive(direct);
        Place(_messageHistory.Root.rectTransform, 16f, direct ? 202f : 132f,
            WorkspaceWidth - 32f, direct ? 196f : 266f);
        if (changed) _messagesScrollToEnd = true;
        RefreshInboxDraftBudget();
        RefreshMessengerChannels();
    }

    internal void SetMessengerContacts(IReadOnlyList<ClientMessengerContact> contacts, bool more, int page)
    {
        if (_disposed) return;
        for (var index = 0; index < _messengerContacts.Count; index++)
        {
            var available = index < contacts.Count;
            var id = available ? contacts[index].Id : "";
            var name = available ? contacts[index].Name : "";
            if (_messengerContactIds[index] != id || _messengerContacts[index].Label.text != name)
                _inboxDirty = true;
            _messengerContacts[index].Image.gameObject.SetActive(available);
            _messengerContactIds[index] = id;
            _messengerContacts[index].Label.text = name;
        }
        _messengerContactCount = contacts.Count;
        _messengerContactPage = Math.Max(1, page);
        _messengerMoreContacts = more;
        RefreshMessengerContactActions();
        RefreshMessengerContacts();
    }

    internal void SetMessengerContactState(ClientMessengerContactState state)
    {
        if (_disposed) return;
        _messengerContactState = state;
        RefreshMessengerContactActions();
        RefreshMessengerContacts();
    }

    private void RefreshMessengerContactActions()
    {
        var state = _messengerContactState;
        _contactsEmpty.gameObject.SetActive(_messengerContactCount == 0);
        _contactsEmpty.text = state.Searching ? "Searching fresh contact pages; your conversation stays selected."
            : state.Busy ? "Waiting for the current contact request to finish."
            : state.Current ? "No recipients on this page. Browse another page or find the selected conversation."
            : "Refresh to browse recipients, or open a conversation and choose Find current contact.";
        _contactsPage.text = "Page " + _messengerContactPage.ToString(CultureInfo.InvariantCulture)
            + " · " + _messengerContactCount.ToString(CultureInfo.InvariantCulture) + " shown · "
            + (state.Current ? "Checked" : _messengerContactCount > 0 ? "Stale" : "Not checked")
            + (_messengerMoreContacts ? " · More" : "");
        _contactsLookupStatus.text = state.Detail ?? "Refresh contacts to check current recipients.";
        _contactsLookupStatus.color = state.TargetReady ? Mint : Muted;
        _contactsPrevious.Button.interactable = state.CanRequest && _messengerContactPage > 1;
        _contactsNext.Button.interactable = state.CanRequest && _messengerMoreContacts && _messengerContactPage < 64;
        _contactsRefresh.Button.interactable = state.CanRequest;
        _contactsFind.Button.interactable = state.Searching || state.CanFind;
        _contactsFind.Label.text = state.Searching ? "Cancel lookup"
            : state.Busy ? "Request pending" : string.IsNullOrEmpty(state.Target) ? "Select conversation"
            : state.TargetReady ? "Recheck contact" : "Find current contact";
        _contactsFind.Image.color = state.Searching ? ChromeRaised : Raised;
        _contactsFind.Label.color = state.Searching || state.CanFind ? ChromeMint : Dim;
    }

    private void RefreshMessengerContacts()
    {
        for (var index = 0; index < _messengerContacts.Count; index++)
        {
            var selected = _inboxChannel == "direct" && !string.IsNullOrEmpty(_messengerContactIds[index])
                && _messengerContactIds[index] == _inboxTarget;
            _messengerContacts[index].Image.color = selected ? ChromeRaised : Raised;
            var current = _messengerContactState.Current && !_messengerContactState.Busy;
            _messengerContacts[index].Button.interactable = current;
            _messengerContacts[index].Label.color = current ? selected ? ChromeMint : Ink : Dim;
        }
    }

    private void RefreshMessengerChannels()
    {
        foreach (var pair in _messengerChannels)
        {
            var active = pair.Key == _messengerChannel;
            pair.Value.Image.color = active ? ChromeRaised : Raised;
            pair.Value.Label.color = active ? ChromeInk : Muted;
        }
        RefreshMessengerContacts();
        RefreshInboxColors();
    }
}
