// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

namespace Modstoke.Client;

internal sealed partial class ClientTerminal
{
    private void DiscussTribeTask()
    {
        var existing = _messenger.Channel == "tribe" ? _messageDraft
            : _os.Snapshot.Field.Drafts.TryGetValue("tribe|", out var saved) ? saved : "";
        if (_tribeApp.TryPrepareDiscussion(existing, out var draft))
        {
            if (!_os.FieldAction(new("draft", _messageDraft, _draftKey)))
            {
                Feedback("The notebook is busy. Your current draft is unchanged; try discussing the task again.");
                _nextRefresh = 0;
                return;
            }
            _messenger.SelectChannel("tribe");
            _draftKey = "tribe|";
            StageMessageDraft(draft);
            _messageStatus = "Task added to your Tribe draft. Review it before sending.";
            Select(ClientTerminalApp.Messages);
        }
        _nextRefresh = 0;
    }
}
