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
    private string[] _inboxSearchTerms = Array.Empty<string>();
    private bool _inboxSenderOnly;
    private bool _inboxUnreadFirst;
    private bool _inboxLargeText;
    private readonly List<float> _inboxMatchOffsets = new();
    private int _inboxMatchIndex = -1;
    private ButtonWidget _inboxSenderSearch = null!;
    private ButtonWidget _inboxSort = null!;
    private ButtonWidget _inboxResetFilters = null!;
    private ButtonWidget _inboxReadingSize = null!;
    private ButtonWidget _inboxPreviousMatch = null!;
    private ButtonWidget _inboxNextMatch = null!;
    private TextMeshProUGUI _inboxMatchLabel = null!;
    private ButtonWidget _inboxClearDraft = null!;
    private ButtonWidget _inboxUndoDraft = null!;
    private TextMeshProUGUI _inboxAttachmentPreview = null!;
    private string _inboxDiscardedDraft = "";
    private ClientTabletMessage? _inboxReusePending;

    private void BuildMessengerTools()
    {
        _inboxSenderSearch = Button(_inboxRoot, "SenderSearch", "Any text", 344f, 44f, 118f, 34f,
            () => ChangeMessengerBrowsing("sender"));
        _inboxSort = Button(_inboxRoot, "ConversationSort", "Recent first", 470f, 44f, 138f, 34f,
            () => ChangeMessengerBrowsing("sort"));
        _inboxResetFilters = Button(_inboxRoot, "ResetBrowsing", "Reset view", 616f, 44f, 126f, 34f,
            () => ChangeMessengerBrowsing("reset"));
        _inboxReadingSize = Button(_inboxRoot, "ReadingSize", "Larger text", 750f, 44f, 130f, 34f,
            () => ChangeMessengerBrowsing("size"));
        _inboxPreviousMatch = Button(_inboxRoot, "PreviousMatch", "< Match", 888f, 44f, 98f, 34f,
            () => MoveInboxMatch(-1));
        _inboxNextMatch = Button(_inboxRoot, "NextMatch", "Match >", 994f, 44f, 98f, 34f,
            () => MoveInboxMatch(1));
        foreach (var button in new[] { _inboxSenderSearch, _inboxSort, _inboxResetFilters,
            _inboxReadingSize, _inboxPreviousMatch, _inboxNextMatch }) button.Label.fontSize = 17f;
        _inboxMatchLabel = Text(_inboxRoot, "MatchPosition", "", 17f, Muted, 1100f, 44f, 188f, 34f);
        _inboxMatchLabel.alignment = TextAlignmentOptions.MidlineLeft;
        _inboxMatchLabel.enableWordWrapping = false;
        _inboxMatchLabel.overflowMode = TextOverflowModes.Ellipsis;
        _inboxClearDraft = Button(_messagesSurface, "ClearDraft", "Clear draft", 24f, 558f, 132f, 32f, ClearMessengerDraft);
        _inboxUndoDraft = Button(_messagesSurface, "UndoClearDraft", "Undo clear", 164f, 558f, 132f, 32f, UndoMessengerDraft);
        _inboxClearDraft.Label.fontSize = _inboxUndoDraft.Label.fontSize = 18f;
        _inboxAttachmentPreview = Text(_messagesSurface, "DraftAttachment", "", 18f, Muted, 312f, 558f, 992f, 32f);
        _inboxAttachmentPreview.enableWordWrapping = false;
        _inboxAttachmentPreview.overflowMode = TextOverflowModes.Ellipsis;
        RefreshMessengerDraftTools();
        SetMessengerDraftToolsVisible(false);
    }

    private void ChangeMessengerBrowsing(string action)
    {
        if (!CanInteractWithInbox()) return;
        RememberInboxReadingPosition();
        if (action == "sender") _inboxSenderOnly = !_inboxSenderOnly;
        else if (action == "sort") _inboxUnreadFirst = !_inboxUnreadFirst;
        else if (action == "size") _inboxLargeText = !_inboxLargeText;
        else if (action == "reset")
        {
            _inboxUnreadOnly = _inboxLocationsOnly = _inboxCurrentChannelOnly = _inboxSenderOnly = _inboxUnreadFirst = false;
            _inboxQuery = "";
            _inboxSearchTerms = Array.Empty<string>();
            _inboxSearch.SetTextWithoutNotify("");
        }
        _inboxMatchIndex = -1;
        _inboxDirty = true;
        if (action != "size") _inboxConversationList.Reset();
        RenderInbox(action is "sender" or "reset");
    }

    private void RefreshMessengerTools()
    {
        if (_inboxSenderSearch == null) return;
        _inboxSenderSearch.Label.text = _inboxSenderOnly ? "Sender only" : "Any text";
        _inboxSort.Label.text = _inboxUnreadFirst ? "Unread first" : "Recent first";
        _inboxReadingSize.Label.text = _inboxLargeText ? "Normal text" : "Larger text";
        SetInboxFilterColor(_inboxSenderSearch, _inboxSenderOnly);
        SetInboxFilterColor(_inboxSort, _inboxUnreadFirst);
        SetInboxFilterColor(_inboxReadingSize, _inboxLargeText);
        _inboxResetFilters.Button.interactable = _inboxSearch.text.Length > 0 || _inboxUnreadOnly || _inboxLocationsOnly
            || _inboxCurrentChannelOnly || _inboxSenderOnly || _inboxUnreadFirst;
        var count = _inboxMatchOffsets.Count;
        _inboxMatchIndex = Math.Clamp(_inboxMatchIndex, -1, count - 1);
        var searching = _inboxQuery.Length > 0 || _inboxLocationsOnly;
        _inboxPreviousMatch.Button.interactable = searching && count > 0 && _inboxMatchIndex != 0;
        _inboxNextMatch.Button.interactable = searching && count > 0 && _inboxMatchIndex < count - 1;
        _inboxMatchLabel.text = !searching ? "Search to step"
            : (_inboxMatchIndex < 0 ? "–" : (_inboxMatchIndex + 1).ToString(CultureInfo.InvariantCulture))
                + " / " + count.ToString(CultureInfo.InvariantCulture);
    }

    private void MoveInboxMatch(int direction)
    {
        if (!CanInteractWithInbox() || _inboxMatchOffsets.Count == 0
            || _inboxQuery.Length == 0 && !_inboxLocationsOnly) return;
        _inboxMatchIndex = _inboxMatchIndex < 0 ? direction > 0 ? 0 : _inboxMatchOffsets.Count - 1
            : Math.Clamp(_inboxMatchIndex + direction, 0, _inboxMatchOffsets.Count - 1);
        _inboxTranscript.Scroll.StopMovement();
        var bottom = Math.Max(0f, _inboxTranscript.Content.sizeDelta.y - _inboxTranscript.Root.rectTransform.rect.height);
        _inboxTranscript.Content.anchoredPosition = new Vector2(0f, Math.Min(bottom, _inboxMatchOffsets[_inboxMatchIndex]));
        RefreshMessengerTools();
    }

    private void SetMessengerReadingSize(TextMeshProUGUI body, bool location)
    {
        var baseline = _inboxLargeText ? 29f : location ? 26f : 23f;
        _textSizes[body] = baseline;
        var large = _appliedTextSize == ClientTabletTextSize.Large;
        body.enableAutoSizing = large;
        body.fontSizeMin = baseline;
        body.fontSizeMax = large ? baseline * 1.16f : baseline;
        body.fontSize = body.fontSizeMax;
    }

    private void ReuseInboxMessage(InboxMessageRow row)
    {
        var message = row.Message;
        if (!CanInteractWithInbox() || message == null || !InboxSelected(message) || !_inboxHistory.Contains(message)
            || ClientLocationMessage.TryRead(message.Text, out _) || message.Text.Length > _messageInput.characterLimit) return;
        if (!string.IsNullOrWhiteSpace(_messageInput.text)
            && (!ReferenceEquals(_inboxReusePending, message) || _inboxPendingDraftText != _messageInput.text))
        {
            ResetInboxDraftConfirmation();
            _inboxReusePending = message;
            _inboxPendingDraftText = _messageInput.text;
            row.Reuse.Label.text = "Replace draft?";
            return;
        }
        ResetInboxDraftConfirmation();
        // Uses the same validated edit callback as typing. This never submits a message.
        _messageInput.text = message.Text;
        if (_messageInput.text == message.Text)
        {
            _messageInput.ActivateInputField();
            _messageInput.caretPosition = _messageInput.text.Length;
        }
    }

    private void ClearMessengerDraft()
    {
        if (!CanInteractWithInbox() || _messageInput.text.Length == 0) return;
        var previous = _messageInput.text;
        _messageInput.text = "";
        if (_messageInput.text.Length == 0) _inboxDiscardedDraft = previous;
        RefreshMessengerDraftTools();
    }

    private void UndoMessengerDraft()
    {
        if (!CanInteractWithInbox() || _messageInput.text.Length != 0 || _inboxDiscardedDraft.Length == 0) return;
        var previous = _inboxDiscardedDraft;
        _messageInput.text = previous;
        if (_messageInput.text == previous) _inboxDiscardedDraft = "";
        RefreshMessengerDraftTools();
    }

    private void ClearMessengerDraftUndo()
    {
        _inboxDiscardedDraft = "";
        _inboxMatchIndex = -1;
        RefreshMessengerDraftTools();
    }

    private void RefreshMessengerDraftTools()
    {
        if (_inboxClearDraft == null) return;
        if (_messageInput.text.Length > 0) _inboxDiscardedDraft = "";
        _inboxClearDraft.Button.interactable = _messageInput.text.Length > 0;
        _inboxUndoDraft.Button.interactable = _messageInput.text.Length == 0 && _inboxDiscardedDraft.Length > 0;
        _inboxAttachmentPreview.text = ClientLocationMessage.TryRead(_messageInput.text, out var place)
            ? "Location draft · " + place.Name + " · X " + place.Position.X.ToString("F1", CultureInfo.InvariantCulture)
                + " · Z " + place.Position.Z.ToString("F1", CultureInfo.InvariantCulture) + " · review before Send"
            : "Draft only · sending uses the selected conversation";
    }

    private void SetMessengerDraftToolsVisible(bool visible)
    {
        _inboxClearDraft.Image.gameObject.SetActive(visible);
        _inboxUndoDraft.Image.gameObject.SetActive(visible);
        _inboxAttachmentPreview.gameObject.SetActive(visible);
    }
}
