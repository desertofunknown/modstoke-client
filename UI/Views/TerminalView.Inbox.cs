// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Modstoke.Client;

internal sealed partial class ClientTerminalView
{
    private const int InboxHistoryLimit = 100;
    private readonly List<ClientTabletMessage> _inboxHistory = new();
    private readonly List<InboxConversationRow> _inboxConversations = new();
    private readonly List<InboxMessageRow> _inboxMessages = new();
    private readonly Dictionary<string, (ClientTabletMessage Message, ClientFieldPlace Place)> _inboxPins = new(StringComparer.Ordinal);
    private RectTransform _inboxRoot = null!;
    private RectTransform _inboxPresets = null!;
    private TMP_InputField _inboxSearch = null!;
    private ScrollArea _inboxConversationList = null!;
    private ScrollArea _inboxTranscript = null!;
    private TextMeshProUGUI _inboxConversationEmpty = null!;
    private TextMeshProUGUI _inboxTranscriptEmpty = null!;
    private TextMeshProUGUI _inboxSummary = null!;
    private TextMeshProUGUI _inboxDraftBudget = null!;
    private ButtonWidget _inboxClearSearch = null!;
    private ButtonWidget _inboxUnreadFilter = null!;
    private ButtonWidget _inboxLocationFilter = null!;
    private ButtonWidget _inboxChannelFilter = null!;
    private ButtonWidget _inboxOldest = null!;
    private ButtonWidget _inboxLatest = null!;
    private readonly Dictionary<(string Channel, string Target), (float Position, bool Follow)> _inboxReadingPositions = new();
    private readonly List<(ButtonWidget Button, string Command, string Label)> _inboxDraftActions = new();
    private string _inboxPendingDraftCommand = "";
    private string _inboxPendingDraftText = "";
    private bool _inboxUnreadOnly;
    private bool _inboxLocationsOnly;
    private bool _inboxCurrentChannelOnly;
    private string _inboxChannel = "expedition";
    private string _inboxTarget = "";
    private string _inboxQuery = "";
    private bool _inboxVisible;
    private bool _inboxDirty = true;
    private bool _inboxResetScroll;

    private sealed class InboxConversationRow
    {
        internal ButtonWidget Button = null!;
        internal TextMeshProUGUI Preview = null!;
        internal TextMeshProUGUI Unread = null!;
        internal string Channel = "";
        internal string Target = "";
    }

    private sealed class InboxMessageRow
    {
        internal Image Root = null!;
        internal TextMeshProUGUI Sender = null!;
        internal TextMeshProUGUI Body = null!;
        internal TextMeshProUGUI LocationName = null!;
        internal TextMeshProUGUI LocationDetails = null!;
        internal ButtonWidget Pin = null!;
        internal ButtonWidget Reuse = null!;
        internal ClientTabletMessage? Message;
        internal string PinToken = "";
    }

    private sealed class InboxConversation
    {
        internal ClientTabletMessage Last = null!;
        internal ClientTabletMessage? Match;
        internal int Unread;
        internal bool Matches;
    }

    private void BuildInbox()
    {
        _inboxRoot = Node(_messagesSurface, "Inbox");
        Place(_inboxRoot, 16f, 132f, WorkspaceWidth - 32f, 266f);
        _inboxSearch = Input(_inboxRoot, "", 80);
        Place(_inboxSearch.GetComponent<RectTransform>(), 0f, 0f, 232f, 44f);
        _inboxSearch.textComponent.fontSize = 20f;
        var placeholder = Text(_inboxSearch.textViewport, "SearchHint", "Search history words", 20f, Muted,
            0f, 0f, 288f, 32f);
        Stretch(placeholder.rectTransform, 0f, 0f, 0f, 0f);
        placeholder.alignment = TextAlignmentOptions.MidlineLeft;
        placeholder.enableWordWrapping = false;
        _inboxSearch.placeholder = placeholder;
        _inboxSearch.onValueChanged.AddListener((UnityAction<string>)new Action<string>(SearchInbox));
        _inboxClearSearch = Button(_inboxRoot, "ClearSearch", "Clear", 240f, 0f, 76f, 44f, ClearInboxSearch);
        _inboxClearSearch.Label.fontSize = 18f;
        _inboxUnreadFilter = Button(_inboxRoot, "UnreadFilter", "Unread", 0f, 52f, 96f, 34f,
            () => ToggleInboxFilter("unread"));
        _inboxLocationFilter = Button(_inboxRoot, "LocationFilter", "Pins", 104f, 52f, 80f, 34f,
            () => ToggleInboxFilter("locations"));
        _inboxChannelFilter = Button(_inboxRoot, "ChannelFilter", "Channel", 192f, 52f, 124f, 34f,
            () => ToggleInboxFilter("channel"));
        foreach (var filter in new[] { _inboxUnreadFilter, _inboxLocationFilter, _inboxChannelFilter })
            filter.Label.fontSize = 17f;
        _inboxSummary = Text(_inboxRoot, "HistorySummary", "", 18f, Muted, 344f, 2f, WorkspaceWidth - 600f, 34f);
        _inboxSummary.enableWordWrapping = false;
        _inboxSummary.overflowMode = TextOverflowModes.Ellipsis;
        _inboxSummary.alignment = TextAlignmentOptions.MidlineLeft;
        _inboxOldest = Button(_inboxRoot, "OldestMessage", "Oldest", WorkspaceWidth - 232f, 0f, 96f, 36f,
            () => MoveInboxTranscript(false));
        _inboxLatest = Button(_inboxRoot, "LatestMessage", "Latest", WorkspaceWidth - 128f, 0f, 96f, 36f,
            () => MoveInboxTranscript(true));
        _inboxOldest.Label.fontSize = _inboxLatest.Label.fontSize = 18f;
        _inboxConversationList = Scroll(_inboxRoot, "Conversations", 0f, 94f, 316f, 172f);
        _inboxTranscript = Scroll(_inboxRoot, "Conversation", 332f, 44f, WorkspaceWidth - 364f, 222f);
        _inboxConversationEmpty = Text(_inboxConversationList.Content, "Empty", "No local conversations yet.",
            20f, Muted, 12f, 12f, 274f, 100f);
        _inboxTranscriptEmpty = Text(_inboxTranscript.Content, "Empty", "No messages in this local history yet.",
            24f, Muted, 20f, 20f, _inboxTranscript.Width - 40f, 100f);
        _inboxPresets = Node(_messagesSurface, "DraftReplies");
        Place(_messengerTarget.rectTransform, 24f, 412f, 350f, 32f);
        Place(_inboxPresets, 392f, 410f, WorkspaceWidth - 416f, 36f);
        Text(_inboxPresets, "Heading", "Draft", 20f, Muted, 0f, 0f, 70f, 36f)
            .alignment = TextAlignmentOptions.MidlineLeft;
        var presets = new[] { ("ack", "Acknowledged", 176f), ("on-my-way", "On my way", 144f), ("need-help", "Need help", 144f) };
        var presetX = 80f;
        for (var index = 0; index < presets.Length; index++)
        {
            var preset = presets[index];
            var button = Button(_inboxPresets, "Draft" + preset.Item1, preset.Item2,
                presetX, 0f, preset.Item3, 36f, () => PrepareInboxDraft("preset:" + preset.Item1));
            button.Label.fontSize = 20f;
            _inboxDraftActions.Add((button, "preset:" + preset.Item1, preset.Item2));
            presetX += preset.Item3 + 12f;
        }
        var share = Button(_inboxPresets, "SharePosition", "Share my location", presetX, 0f, 252f, 36f,
            () => PrepareInboxDraft("share-position"));
        share.Label.fontSize = 20f;
        _inboxDraftActions.Add((share, "share-position", "Share my location"));
        Place(_messageStatus.rectTransform, 24f, 516f, WorkspaceWidth - 244f, 40f);
        _messageStatus.fontSize = 17f;
        _inboxDraftBudget = Text(_messagesSurface, "DraftBudget", "", 18f, Muted,
            WorkspaceWidth - 204f, 516f, 180f, 40f);
        _inboxDraftBudget.alignment = TextAlignmentOptions.TopRight;
        _messageInput.onValueChanged.AddListener((UnityAction<string>)new Action<string>(_ => RefreshInboxDraftBudget()));
        BuildMessengerTools();
        RefreshInboxDraftBudget();
        _inboxRoot.gameObject.SetActive(false);
        _inboxPresets.gameObject.SetActive(false);
    }

    internal void SetInboxState(IReadOnlyList<ClientTabletMessage> history, string channel, string target)
    {
        if (_disposed) return;
        var selectionChanged = channel != _inboxChannel || target != _inboxTarget;
        if (selectionChanged)
        {
            RememberInboxReadingPosition();
            ResetInboxDraftConfirmation();
            ClearMessengerDraftUndo();
        }
        _inboxResetScroll |= selectionChanged;
        var start = Math.Max(0, history.Count - InboxHistoryLimit);
        var count = history.Count - start;
        var changed = selectionChanged || count != _inboxHistory.Count;
        for (var index = 0; !changed && index < count; index++)
            changed = history[start + index] != _inboxHistory[index];
        if (!changed && !_inboxDirty) return;
        if (changed)
        {
            _inboxHistory.Clear();
            for (var index = start; index < history.Count; index++) _inboxHistory.Add(history[index]);
            _inboxChannel = channel;
            _inboxTarget = target;
            RefreshMessengerContacts();
            _inboxPins.Clear();
            _inboxDirty = true;
        }
        LayoutInbox();
        if (_inboxVisible) RenderInbox(selectionChanged);
    }

    internal void SetInboxVisible(bool visible)
    {
        if (_disposed || _inboxRoot == null) return;
        if (_inboxVisible == visible) return;
        if (!visible) RememberInboxReadingPosition();
        _inboxVisible = visible;
        _inboxRoot.gameObject.SetActive(visible);
        _inboxPresets.gameObject.SetActive(visible);
        SetMessengerDraftToolsVisible(visible);
        _messageHistory.Root.gameObject.SetActive(!visible);
        if (visible)
        {
            LayoutInbox();
            RenderInbox(false);
        }
        else
        {
            ResetInboxDraftConfirmation();
            ClearMessengerDraftUndo();
            DeactivateInboxInputs();
            _inboxPins.Clear();
            _inboxDirty = true;
        }
    }

    internal void DeactivateInboxInputs()
    {
        if (_inboxSearch != null) _inboxSearch.DeactivateInputField();
    }

    internal bool TryGetSharedPlace(string token, out ClientFieldPlace place) =>
        TryGetSharedPlace(token, out place, out _);

    internal bool TryGetSharedPlace(string token, out ClientFieldPlace place, out ClientTabletMessage source)
    {
        place = null!;
        source = null!;
        if (_disposed || !_inboxVisible || _shadeCapture || _page?.App != ClientTerminalApp.Messages
            || !_inboxPins.TryGetValue(token, out var pin) || !InboxSelected(pin.Message)) return false;
        place = pin.Place;
        source = pin.Message;
        return true;
    }

    private void LayoutInbox()
    {
        if (_inboxRoot == null) return;
        var direct = _inboxChannel == "direct";
        var height = direct ? 196f : 266f;
        Place(_inboxRoot, 16f, direct ? 202f : 132f, WorkspaceWidth - 32f, height);
        Place(_inboxConversationList.Root.rectTransform, 0f, 94f, 316f, height - 94f);
        Place(_inboxTranscript.Root.rectTransform, 332f, 86f, WorkspaceWidth - 364f, height - 86f);
    }

    private void SearchInbox(string value)
    {
        if (_disposed || !_inboxVisible || _shadeCapture || _page?.App != ClientTerminalApp.Messages) return;
        var query = (value ?? "").Trim();
        _inboxClearSearch.Button.interactable = _inboxSearch.text.Length > 0;
        if (_inboxQuery == query) return;
        RememberInboxReadingPosition();
        _inboxQuery = query;
        _inboxSearchTerms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        _inboxMatchIndex = -1;
        _inboxDirty = true;
        _inboxConversationList.Reset();
        RenderInbox(true);
    }

    private bool InboxSelected(ClientTabletMessage message) => message.Channel == _inboxChannel
        && (_inboxChannel != "direct" || message.ConversationId == _inboxTarget);

    private bool CanInteractWithInbox() => !_disposed && _inboxVisible && !_shadeCapture
        && _page?.App == ClientTerminalApp.Messages;

    private void ClearInboxSearch()
    {
        if (!CanInteractWithInbox()) return;
        _inboxSearch.text = "";
    }

    private void ToggleInboxFilter(string filter)
    {
        if (!CanInteractWithInbox()) return;
        if (filter == "unread") _inboxUnreadOnly = !_inboxUnreadOnly;
        else if (filter == "channel") _inboxCurrentChannelOnly = !_inboxCurrentChannelOnly;
        else
        {
            RememberInboxReadingPosition();
            _inboxLocationsOnly = !_inboxLocationsOnly;
        }
        _inboxDirty = true;
        _inboxMatchIndex = -1;
        _inboxConversationList.Reset();
        RenderInbox(filter == "locations");
    }

    private void MoveInboxTranscript(bool latest)
    {
        if (!CanInteractWithInbox()) return;
        _inboxTranscript.Scroll.StopMovement();
        var bottom = Math.Max(0f, _inboxTranscript.Content.sizeDelta.y - _inboxTranscript.Root.rectTransform.rect.height);
        _inboxTranscript.Content.anchoredPosition = new Vector2(0f, latest ? bottom : 0f);
        RememberInboxReadingPosition();
    }

    private void RememberInboxReadingPosition()
    {
        if (!_inboxVisible || _inboxQuery.Length > 0 || _inboxLocationsOnly) return;
        var position = _inboxTranscript.Content.anchoredPosition.y;
        var bottom = Math.Max(0f, _inboxTranscript.Content.sizeDelta.y - _inboxTranscript.Root.rectTransform.rect.height);
        _inboxReadingPositions[(_inboxChannel, _inboxChannel == "direct" ? _inboxTarget : "")] =
            (position, position >= bottom - 8f);
    }

    private void PrepareInboxDraft(string command)
    {
        if (!CanInteractWithInbox()) return;
        var draft = _messageInput.text;
        if (!string.IsNullOrWhiteSpace(draft)
            && (_inboxPendingDraftCommand != command || _inboxPendingDraftText != draft))
        {
            ResetInboxDraftConfirmation();
            _inboxPendingDraftCommand = command;
            _inboxPendingDraftText = draft;
            foreach (var action in _inboxDraftActions)
                if (action.Command == command)
                {
                    action.Button.Label.text = "Replace draft?";
                    action.Button.Label.fontSize = 16f;
                }
            return;
        }
        ResetInboxDraftConfirmation();
        MessengerCommand(command);
    }

    private void ResetInboxDraftConfirmation()
    {
        _inboxPendingDraftCommand = _inboxPendingDraftText = "";
        _inboxReusePending = null;
        foreach (var row in _inboxMessages) row.Reuse.Label.text = "Use as draft";
        foreach (var action in _inboxDraftActions)
        {
            action.Button.Label.text = action.Label;
            action.Button.Label.fontSize = 20f;
        }
    }

    private void RefreshInboxDraftBudget()
    {
        if (_disposed || _inboxDraftBudget == null) return;
        if (_messageInput.text != _inboxPendingDraftText) ResetInboxDraftConfirmation();
        var bytes = Encoding.UTF8.GetByteCount(_messageInput.text);
        _inboxDraftBudget.text = _messengerChannel == "legacy"
            ? _messageInput.text.Length.ToString(CultureInfo.InvariantCulture) + " / 256 chars"
            : bytes.ToString(CultureInfo.InvariantCulture) + " / 256 bytes" + (bytes > 256 ? "\nShorten to send" : "");
        _inboxDraftBudget.color = _messengerChannel != "legacy" && bytes > 256 ? Amber : Muted;
        RefreshMessengerDraftTools();
    }

    private void SetInboxFilterColor(ButtonWidget button, bool active)
    {
        button.Image.color = active ? ChromeRaised : Raised;
        button.Label.color = active ? ChromeMint : Muted;
    }

    private bool InboxMatches(ClientTabletMessage message)
    {
        if (_inboxLocationsOnly && !ClientLocationMessage.TryRead(message.Text, out _)) return false;
        foreach (var term in _inboxSearchTerms)
            if (!message.SenderName.Contains(term, StringComparison.OrdinalIgnoreCase)
                && (_inboxSenderOnly || !message.Text.Contains(term, StringComparison.OrdinalIgnoreCase)
                    && !InboxTitle(message).Contains(term, StringComparison.OrdinalIgnoreCase))) return false;
        return true;
    }

    private string InboxTitle(ClientTabletMessage message)
    {
        if (message.Channel != "direct") return message.Channel switch
        { "tribe" => "Tribe", "legacy" => "Public game chat", _ => "Expedition" };
        for (var index = 0; index < _messengerContacts.Count; index++)
            if (_messengerContactIds[index] == message.ConversationId)
                return _messengerContacts[index].Label.text;
        for (var index = _inboxHistory.Count - 1; index >= 0; index--)
        {
            var candidate = _inboxHistory[index];
            if (candidate.Channel == "direct" && candidate.ConversationId == message.ConversationId
                && candidate.SenderId == message.ConversationId && !string.IsNullOrWhiteSpace(candidate.SenderName))
                return candidate.SenderName;
        }
        return "Direct · " + message.ConversationId;
    }

    private void RenderInbox(bool resetScroll)
    {
        if (!_inboxVisible || !_inboxDirty) return;
        _inboxDirty = false;
        resetScroll |= _inboxResetScroll;
        _inboxResetScroll = false;
        var conversations = new List<InboxConversation>();
        var byKey = new Dictionary<(string Channel, string Target), InboxConversation>();
        // Newest conversation first; counts still cover the complete retained local history.
        for (var index = _inboxHistory.Count - 1; index >= 0; index--)
        {
            var message = _inboxHistory[index];
            var key = (message.Channel, message.Channel == "direct" ? message.ConversationId : "");
            if (!byKey.TryGetValue(key, out var conversation))
            {
                conversation = new InboxConversation { Last = message };
                byKey.Add(key, conversation);
                conversations.Add(conversation);
            }
            if (message.Unread) conversation.Unread++;
            if (InboxMatches(message))
            {
                conversation.Matches = true;
                conversation.Match ??= message;
            }
        }
        foreach (var key in new List<(string Channel, string Target)>(_inboxReadingPositions.Keys))
            if (!byKey.ContainsKey(key)) _inboxReadingPositions.Remove(key);
        if (_inboxUnreadFirst)
        {
            // Stable partition preserves newest-first ordering inside both groups.
            var unread = conversations.FindAll(item => item.Unread > 0);
            unread.AddRange(conversations.FindAll(item => item.Unread == 0));
            conversations = unread;
        }
        var rowIndex = 0;
        foreach (var conversation in conversations)
        {
            if (!conversation.Matches || _inboxUnreadOnly && conversation.Unread == 0
                || _inboxCurrentChannelOnly && conversation.Last.Channel != _inboxChannel) continue;
            var row = GetInboxConversation(rowIndex);
            row.Channel = conversation.Last.Channel;
            row.Target = row.Channel == "direct" ? conversation.Last.ConversationId : "";
            row.Button.Label.text = InboxTitle(conversation.Last);
            row.Unread.text = conversation.Unread == 0 ? "" : conversation.Unread.ToString(CultureInfo.InvariantCulture);
            var preview = conversation.Match ?? conversation.Last;
            var prefix = string.IsNullOrWhiteSpace(preview.SenderName) ? "" : preview.SenderName + ": ";
            row.Preview.text = (ClientLocationMessage.TryRead(preview.Text, out var previewPlace)
                ? "Location · " + previewPlace.Name : prefix + preview.Text).Replace('\r', ' ').Replace('\n', ' ');
            Place(row.Button.Image.rectTransform, 4f, 4f + rowIndex * 84f, 292f, 78f);
            row.Button.Image.gameObject.SetActive(true);
            rowIndex++;
        }
        for (var index = rowIndex; index < _inboxConversations.Count; index++)
            _inboxConversations[index].Button.Image.gameObject.SetActive(false);
        _inboxConversationEmpty.gameObject.SetActive(rowIndex == 0);
        _inboxConversationEmpty.text = _inboxQuery.Length > 0 || _inboxLocationsOnly || _inboxUnreadOnly || _inboxCurrentChannelOnly
            ? "No conversations match. Clear search or turn off a filter." : "No local conversations yet.";
        _inboxConversationList.Height(Math.Max(120f, rowIndex * 84f + 4f));
        _inboxConversationList.Content.anchoredPosition = new Vector2(0f,
            Math.Clamp(_inboxConversationList.Content.anchoredPosition.y, 0f,
                Math.Max(0f, _inboxConversationList.Content.sizeDelta.y - _inboxConversationList.Root.rectTransform.rect.height)));

        var viewport = _inboxTranscript.Root.rectTransform.rect.height;
        var oldPosition = _inboxTranscript.Content.anchoredPosition.y;
        var follow = resetScroll || oldPosition >= _inboxTranscript.Content.sizeDelta.y - viewport - 8f;
        _inboxPins.Clear();
        _inboxMatchOffsets.Clear();
        var y = 8f;
        var retained = 0;
        rowIndex = 0;
        foreach (var message in _inboxHistory)
        {
            if (!InboxSelected(message)) continue;
            retained++;
            if (!InboxMatches(message)) continue;
            var row = GetInboxMessage(rowIndex++);
            row.Message = message;
            _inboxMatchOffsets.Add(y);
            row.Sender.text = string.IsNullOrWhiteSpace(message.SenderName)
                ? message.Channel == "legacy" ? "Game chat" : "Player" : message.SenderName;
            row.PinToken = "";
            var shared = ClientLocationMessage.TryRead(message.Text, out var place);
            row.Reuse.Image.gameObject.SetActive(!shared);
            row.Reuse.Button.interactable = message.Text.Length <= _messageInput.characterLimit;
            row.Reuse.Label.text = ReferenceEquals(_inboxReusePending, message) ? "Replace draft?" : "Use as draft";
            row.Body.gameObject.SetActive(!shared);
            row.LocationName.gameObject.SetActive(shared);
            row.LocationDetails.gameObject.SetActive(shared);
            var body = shared ? row.LocationName : row.Body;
            SetMessengerReadingSize(body, shared);
            body.text = shared ? place.Name : message.Text;
            var textWidth = _inboxTranscript.Width - 56f;
            var bodyHeight = TextHeight(body, textWidth, 30f);
            Place(body.rectTransform, 16f, 40f, textWidth, bodyHeight);
            var height = 52f + bodyHeight;
            row.Pin.Image.gameObject.SetActive(shared);
            if (shared)
            {
                row.PinToken = Guid.NewGuid().ToString("N");
                _inboxPins.Add(row.PinToken, (message, place));
                row.LocationDetails.text = "Sender-reported · X " + place.Position.X.ToString("F1", CultureInfo.InvariantCulture)
                    + " · Z " + place.Position.Z.ToString("F1", CultureInfo.InvariantCulture);
                var detailsWidth = textWidth - 172f;
                var detailsHeight = TextHeight(row.LocationDetails, detailsWidth, 28f);
                var footerHeight = Math.Max(42f, detailsHeight);
                Place(row.LocationDetails.rectTransform, 16f, height + (footerHeight - detailsHeight) * 0.5f,
                    detailsWidth, detailsHeight);
                Place(row.Pin.Image.rectTransform, 16f + textWidth - 156f, height, 156f, footerHeight);
                height += footerHeight + 12f;
            }
            Place(row.Root.rectTransform, 10f, y, _inboxTranscript.Width - 24f, height);
            row.Root.gameObject.SetActive(true);
            y += height + 10f;
        }
        for (var index = rowIndex; index < _inboxMessages.Count; index++)
        {
            _inboxMessages[index].PinToken = "";
            _inboxMessages[index].Message = null;
            _inboxMessages[index].Root.gameObject.SetActive(false);
        }
        _inboxTranscriptEmpty.gameObject.SetActive(rowIndex == 0);
        _inboxTranscriptEmpty.text = _inboxQuery.Length > 0 || _inboxLocationsOnly ? "No matching messages here. Clear search or turn off Pins."
            : _inboxChannel == "direct" && _inboxTarget.Length == 0 ? "Choose a contact or an existing conversation."
            : "No messages in this local history yet.";
        _inboxTranscript.Height(Math.Max(120f, y));
        _inboxTranscript.Scroll.StopMovement();
        var bottom = Math.Max(0f, _inboxTranscript.Content.sizeDelta.y - viewport);
        if (resetScroll && _inboxQuery.Length == 0 && !_inboxLocationsOnly
            && _inboxReadingPositions.TryGetValue((_inboxChannel, _inboxChannel == "direct" ? _inboxTarget : ""), out var reading))
        {
            oldPosition = reading.Position;
            follow = reading.Follow;
        }
        _inboxTranscript.Content.anchoredPosition = new Vector2(0f,
            resetScroll && (_inboxQuery.Length > 0 || _inboxLocationsOnly) ? 0f : follow ? bottom : Math.Clamp(oldPosition, 0f, bottom));
        _inboxSummary.text = rowIndex.ToString(CultureInfo.InvariantCulture) + " shown / "
            + retained.ToString(CultureInfo.InvariantCulture) + " in conversation · local history";
        _inboxOldest.Button.interactable = _inboxLatest.Button.interactable = rowIndex > 0 && bottom > 0f;
        RefreshMessengerTools();
        RefreshInboxColors();
    }

    private InboxConversationRow GetInboxConversation(int index)
    {
        if (index < _inboxConversations.Count) return _inboxConversations[index];
        var row = new InboxConversationRow();
        row.Button = Button(_inboxConversationList.Content, "Conversation" + index, "", 4f, 0f, 292f, 78f,
            () => MessengerCommand("conversation:" + row.Channel + ":" + row.Target));
        Place(row.Button.Label.rectTransform, 12f, 5f, 220f, 32f);
        row.Button.Label.fontSize = 21f;
        row.Button.Label.enableWordWrapping = false;
        row.Preview = Text(row.Button.Image.transform, "Preview", "", 18f, Muted, 12f, 42f, 268f, 28f);
        row.Preview.enableWordWrapping = false;
        row.Preview.overflowMode = TextOverflowModes.Ellipsis;
        row.Unread = Text(row.Button.Image.transform, "Unread", "", 19f, Mint, 234f, 7f, 46f, 28f, true);
        row.Unread.alignment = TextAlignmentOptions.MidlineRight;
        _inboxConversations.Add(row);
        return row;
    }

    private InboxMessageRow GetInboxMessage(int index)
    {
        if (index < _inboxMessages.Count) return _inboxMessages[index];
        var row = new InboxMessageRow();
        var width = _inboxTranscript.Width - 24f;
        row.Root = RoundedPanel(_inboxTranscript.Content, "Message" + index, Surface, 10f, 0f, width, 90f);
        row.Sender = Text(row.Root.transform, "Sender", "", 20f, Mint, 16f, 8f, width - 206f, 28f, true);
        row.Reuse = Button(row.Root.transform, "ReuseMessage", "Use as draft", width - 180f, 4f, 164f, 32f,
            () => ReuseInboxMessage(row));
        row.Reuse.Label.fontSize = 17f;
        row.Sender.enableWordWrapping = false;
        row.Sender.overflowMode = TextOverflowModes.Ellipsis;
        row.Body = Text(row.Root.transform, "Body", "", 23f, Ink, 16f, 40f, width - 32f, 34f);
        row.LocationName = Text(row.Root.transform, "LocationName", "", 26f, Ink, 16f, 40f, width - 32f, 34f, true);
        row.LocationDetails = Text(row.Root.transform, "LocationDetails", "", 18f, Muted, 16f, 0f, width - 204f, 28f);
        row.Pin = Button(row.Root.transform, "MapPin", "Navigate", 16f, 0f, 156f, 42f, () =>
        {
            if (row.PinToken.Length > 0) MessengerCommand("pin:" + row.PinToken);
        });
        row.Pin.Label.fontSize = 21f;
        _inboxMessages.Add(row);
        return row;
    }

    private void RefreshInboxColors()
    {
        if (_inboxRoot == null) return;
        _inboxSearch.caretColor = Mint;
        _inboxClearSearch.Button.interactable = _inboxSearch.text.Length > 0;
        SetInboxFilterColor(_inboxUnreadFilter, _inboxUnreadOnly);
        SetInboxFilterColor(_inboxLocationFilter, _inboxLocationsOnly);
        SetInboxFilterColor(_inboxChannelFilter, _inboxCurrentChannelOnly);
        _inboxTranscript.Root.color = Screen;
        foreach (var row in _inboxConversations)
        {
            var selected = row.Channel == _inboxChannel && (row.Channel != "direct" || row.Target == _inboxTarget);
            row.Button.Image.color = selected ? ChromeRaised : Raised;
            row.Button.Label.color = selected ? ChromeInk : Ink;
            row.Preview.color = selected ? ChromeMuted : Muted;
            row.Unread.color = selected ? ChromeMint : Mint;
        }
    }
}
