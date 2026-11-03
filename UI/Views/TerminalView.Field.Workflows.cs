// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.Events;

namespace Modstoke.Client;

internal sealed partial class ClientTerminalView
{
    private TMP_InputField _notebookSearch = null!;
    private ButtonWidget _notebookSort = null!;
    private ButtonWidget _notebookClearSearch = null!;
    private ButtonWidget _notebookSave = null!;
    private ButtonWidget _notebookRevert = null!;
    private ButtonWidget _notebookObjective = null!;
    private ButtonWidget _completeObjective = null!;
    private TextMeshProUGUI _notebookResults = null!;
    private TextMeshProUGUI _notebookBudget = null!;
    private readonly List<Guid> _visibleFieldNotes = new();
    private int _notebookSortMode;
    private bool _fieldNoteWasDirty;
    private string _fieldWorkflowConfirmation = "";
    private float _fieldWorkflowConfirmUntil;
    private string _fieldWorkflowMission = "";
    private Guid? _fieldWorkflowNote;
    private RectTransform _fieldNoticeReader = null!;
    private ScrollArea _fieldNoticeList = null!;
    private ScrollArea _fieldNoticeDetail = null!;
    private TextMeshProUGUI _fieldNoticeCount = null!;
    private ButtonWidget _fieldNoticeClear = null!;
    private ButtonWidget _fieldNoticeOpen = null!;
    private ButtonWidget _fieldNoticeDismiss = null!;
    private readonly List<ButtonWidget> _fieldNoticeFilters = new();
    private string _fieldNoticeFilter = "all";
    private long? _fieldNoticeSelection;
    private long _fieldNoticeClearCutoff;

    private void BuildFieldWorkflows()
    {
        _notebookSearch = FieldInput(_journalSurface, 0f, 62f, 252f, 50f, 96, false);
        var hint = Text(_notebookSearch.textViewport, "Hint", "Search title or text", 21f, Muted, 0f, 0f, 232f, 34f);
        _notebookSearch.placeholder = hint;
        _notebookSearch.onValueChanged.AddListener((UnityAction<string>)new Action<string>(_ => RenderNotebookBrowser(reset: true)));
        _notebookClearSearch = Button(_journalSurface, "ClearSearch", "Clear", 260f, 62f, 100f, 50f,
            () => { if (!ContentObscured) SetFieldInput(_notebookSearch, ""); });
        _notebookSort = Button(_journalSurface, "SortNotes", "Newest first", 0f, 122f, 360f, 48f, () =>
        {
            if (ContentObscured) return;
            _notebookSortMode = (_notebookSortMode + 1) % 3;
            RenderNotebookBrowser(reset: true);
        });
        _notebookResults = Text(_journalSurface, "Results", "", 19f, Muted, 8f, 176f, 344f, 32f);
        _notebookResults.overflowMode = TextOverflowModes.Ellipsis;
        Place(_notebookList.Root.rectTransform, 0f, 214f, 360f, 302f);
        var editor = _noteTitle.transform.parent;
        Place(_notebookSave.Image.rectTransform, 24f, 414f, 224f, 52f);
        Place(_noteDelete.Image.rectTransform, 264f, 414f, 250f, 52f);
        _notebookRevert = Button(editor, "Revert", "Discard draft", 534f, 414f, 386f, 52f,
            () => ConfirmFieldWorkflow("revert"));
        _notebookBudget = Text(editor, "Budget", "", 18f, Muted, 24f, 370f, 896f, 32f);
        Place(_noteBody.GetComponent<RectTransform>(), 24f, 118f, 896f, 244f);
        _notebookObjective = Button(editor, "UseNoteObjective", "Use saved note as objective", 24f, 478f, 896f, 48f,
            () => ConfirmFieldWorkflow("objective"));
        Place(_fieldFeedback.rectTransform, 24f, 536f, 896f, 28f);
        Place(_fieldStorage.rectTransform, 24f, 568f, 896f, 24f);
        _completeObjective = Button(_missionEditor, "Complete", "Complete + archive", 400f, 550f, 528f, 72f,
            () => ConfirmFieldWorkflow("complete"));
        Text(_missionEditor, "CompletionHelp", "Complete keeps a dated note in your notebook, then clears this objective.",
            22f, Muted, 40f, 440f, 1248f, 72f);
        Place(_healthSource.rectTransform, 24f, 548f, 1280f, 48f);
        _healthSource.fontSize = 18f;
        _healthSource.enableWordWrapping = true;
        _healthSource.overflowMode = TextOverflowModes.Overflow;
        BuildFieldNoticeReader();
        BuildFieldLocationLinks();
        RenderNotebookBrowser();
        RefreshFieldWorkflowState();
    }

    private void RenderNotebookBrowser(bool reset = false)
    {
        if (_notebookSearch == null || _disposed) return;
        var tokens = _notebookSearch.text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var scoped = _fieldNotes.Where(note => !_fieldNotePlaceFilter.HasValue || note.PlaceId == _fieldNotePlaceFilter);
        var matches = scoped.Where(note => tokens.All(token => note.Title.Contains(token, StringComparison.OrdinalIgnoreCase)
            || note.Body.Contains(token, StringComparison.OrdinalIgnoreCase)));
        matches = _notebookSortMode switch
        {
            1 => matches.OrderBy(note => note.UpdatedAt).ThenBy(note => note.Id),
            2 => matches.OrderBy(note => note.Title, StringComparer.CurrentCultureIgnoreCase).ThenBy(note => note.Id),
            _ => matches.OrderByDescending(note => note.UpdatedAt).ThenBy(note => note.Id)
        };
        var shown = matches.ToArray();
        var offset = reset ? 0f : _notebookList.Content.anchoredPosition.y;
        _noteRows.Clear();
        _visibleFieldNotes.Clear();
        DestroyChildren(_notebookList.Content);
        var y = 8f;
        foreach (var note in shown)
        {
            var row = Button(_notebookList.Content, "Note" + note.Id, "", 8f, y, 328f, 112f,
                () => FieldCommand("note-select:" + note.Id));
            Text(row.Image.transform, "Title", note.Title, 23f, Ink, 16f, 10f, 296f, 34f, true)
                .overflowMode = TextOverflowModes.Ellipsis;
            Text(row.Image.transform, "Updated", note.UpdatedAt.ToLocalTime().ToString("MMM d · HH:mm", CultureInfo.CurrentCulture),
                18f, Muted, 16f, 48f, 296f, 26f);
            var place = _fieldPlaces.FirstOrDefault(item => item.Id == note.PlaceId);
            Text(row.Image.transform, "Location", place?.Name ?? "No linked location", 17f, place == null ? Muted : Mint,
                16f, 78f, 296f, 24f).overflowMode = TextOverflowModes.Ellipsis;
            _noteRows.Add(row);
            _visibleFieldNotes.Add(note.Id);
            y += 122f;
        }
        if (shown.Length == 0)
            Text(_notebookList.Content, "Empty", scoped.Any() ? (_fieldNotePlaceFilter.HasValue
                ? "No notes match. Clear search to see this location's notes." : "No notes match. Clear search to see all notes.")
                : _fieldNotePlaceFilter.HasValue ? "No saved notes here yet. Choose New note to write about this place."
                : "Create your first note to begin.",
                23f, Muted, 20f, 26f, 306f, 180f);
        _notebookResults.text = shown.Length + " / " + scoped.Count() + " notes · " + (128 - _fieldNotes.Count) + " slots free";
        _notebookSort.Label.text = _notebookSortMode switch { 1 => "Oldest first", 2 => "Title A–Z", _ => "Newest first" };
        _notebookClearSearch.Button.interactable = _notebookSearch.text.Length > 0;
        _notebookList.Height(Math.Max(246f, y));
        _notebookList.Content.anchoredPosition = new Vector2(0f, Math.Clamp(offset, 0f, Math.Max(0f, y - 246f)));
        RefreshNoteSelection();
        RefreshFieldLocationUi();
    }

    private bool FieldNoteDirty()
    {
        var saved = _fieldNotes.FirstOrDefault(note => note.Id == _fieldNoteId);
        return _fieldTitleAccepted != (saved?.Title ?? "") || _fieldBodyAccepted != (saved?.Body ?? "")
            || _fieldNotePlaceId != saved?.PlaceId;
    }

    private void RefreshFieldWorkflowState()
    {
        if (_notebookSave == null) return;
        if (_fieldWorkflowConfirmation.Length > 0 && (Time.unscaledTime > _fieldWorkflowConfirmUntil
            || _fieldWorkflowMission != _fieldMissionAccepted || _fieldWorkflowNote != _fieldNoteId)) ClearFieldWorkflowConfirmation();
        var dirty = FieldNoteDirty();
        var known = _fieldNotes.Any(note => note.Id == _fieldNoteId);
        if (_fieldNoteWasDirty != dirty && !_noteDeletePending)
            _fieldFeedback.text = dirty ? "Unsaved draft · Kept while switching notes this session. Save to keep it."
                : known ? "Saved in your notebook. Storage status below reports disk persistence." : "New note. Give it a title to save.";
        _fieldNoteWasDirty = dirty;
        var hasRoom = known || _fieldNotes.Count < 128;
        _notebookSave.Button.interactable = dirty && hasRoom && !string.IsNullOrWhiteSpace(_fieldTitleAccepted);
        _notebookSave.Label.text = !hasRoom ? "Notebook full" : !dirty && known ? "Saved note" : "Save note";
        _notebookRevert.Button.interactable = dirty;
        _notebookBudget.text = "Title " + _fieldTitleAccepted.Length + "/64 · Text " + _fieldBodyAccepted.Length
            + "/2048 · " + (dirty ? "Session draft (unsaved)" : known ? "Saved in notebook · see storage below" : "New note");
        _notebookBudget.overflowMode = TextOverflowModes.Ellipsis;
        _notebookObjective.Button.interactable = known && !dirty;
        _completeObjective.Button.interactable = !string.IsNullOrWhiteSpace(_fieldMissionAccepted) && _fieldNotes.Count < 128;
        if (_fieldNotes.Count >= 128 && _fieldWorkflowConfirmation != "complete") _completeObjective.Label.text = "Notebook full · remove a note";
        else if (_fieldWorkflowConfirmation != "complete") _completeObjective.Label.text = "Complete + archive";
    }

    private void ClearFieldWorkflowConfirmation()
    {
        _fieldWorkflowConfirmation = "";
        _fieldNoticeClearCutoff = 0;
        if (_notebookRevert != null) _notebookRevert.Label.text = "Discard draft";
        if (_notebookObjective != null) _notebookObjective.Label.text = "Use saved note as objective";
        if (_completeObjective != null) _completeObjective.Label.text = "Complete + archive";
        if (_fieldNoticeClear != null) _fieldNoticeClear.Label.text = "Clear shown";
    }

    private void ConfirmFieldWorkflow(string action)
    {
        if (_disposed || ContentObscured) return;
        if (action == "revert" && !FieldNoteDirty() || action == "objective" && (FieldNoteDirty()
            || !_fieldNotes.Any(note => note.Id == _fieldNoteId)) || action == "complete"
            && (string.IsNullOrWhiteSpace(_fieldMissionAccepted) || _fieldNotes.Count >= 128)) return;
        if (_fieldWorkflowConfirmation != action || Time.unscaledTime > _fieldWorkflowConfirmUntil
            || _fieldWorkflowNote != _fieldNoteId || _fieldWorkflowMission != _fieldMissionAccepted)
        {
            ClearFieldWorkflowConfirmation();
            _fieldWorkflowConfirmation = action;
            _fieldWorkflowConfirmUntil = Time.unscaledTime + 8f;
            _fieldWorkflowNote = _fieldNoteId;
            _fieldWorkflowMission = _fieldMissionAccepted;
            if (action == "revert") _notebookRevert.Label.text = "Confirm discard draft";
            else if (action == "objective") _notebookObjective.Label.text = string.IsNullOrWhiteSpace(_fieldMissionAccepted)
                ? "Confirm use note title" : "Confirm replace current objective";
            else if (action == "complete") _completeObjective.Label.text = "Confirm complete + archive";
            else if (action == "notices")
            {
                _fieldNoticeClearCutoff = _fieldNotifications.Where(FieldNoticeMatches).Select(item => item.Id).DefaultIfEmpty().Max();
                _fieldNoticeClear.Label.text = "Confirm clear shown";
            }
            return;
        }
        var cutoff = _fieldNoticeClearCutoff;
        FieldCommand(action switch
        {
            "revert" => "note-revert", "objective" => "mission-from-note", "complete" => "mission-complete:" + _fieldMissionAccepted,
            _ => "dismiss-through:" + cutoff.ToString(CultureInfo.InvariantCulture) + ":" + _fieldNoticeFilter
        });
    }

    private static string FieldHealthGuidance(float?[] factors)
    {
        static bool Low(float? value) => value is { } amount && float.IsFinite(amount) && amount >= 0f && amount <= .25f;
        if (Low(factors[0])) return "Low health: find safety and consider healing before continuing.";
        if (Low(factors[5])) return "Low air: reach the surface or a safe air supply.";
        if (Low(factors[3])) return "Low hydration: find safe drinking water.";
        if (Low(factors[2])) return "Low fullness: consider eating before a long journey.";
        if (Low(factors[4])) return "Low rest: consider a safe place to rest.";
        if (Low(factors[1])) return "Low stamina: pause strenuous activity while it recovers.";
        return factors.Any(value => value is not { } amount || !float.IsFinite(amount) || amount < 0f || amount > 1f)
            ? "Some levels cannot be assessed. Unknown readings do not establish safety."
            : "No monitored level is at or below the 25% marker. Stay aware of your surroundings.";
    }

    private void BuildFieldNoticeReader()
    {
        Place(_notificationCount.rectTransform, 692f, 142f, 422f, 30f);
        Button(_home, "ReadNotifications", "Read all", 1128f, 134f, 176f, 42f, () => OpenFieldNoticeReader(null));
        _fieldNoticeReader = RoundedPanel(_home, "NotificationReader", Screen, 0f, 0f, 1328f, 688f).rectTransform;
        _fieldNoticeReader.GetComponent<UnityEngine.UI.Image>().raycastTarget = true;
        Text(_fieldNoticeReader, "Heading", "Notification center", 36f, Ink, 24f, 16f, 900f, 52f, true);
        Button(_fieldNoticeReader, "Done", "Done", 1104f, 14f, 200f, 56f, () =>
        {
            CloseFieldNoticeReaderIfOpen();
        });
        var filters = new[] { "all", "messages", "other" };
        for (var index = 0; index < filters.Length; index++)
        {
            var filter = filters[index];
            _fieldNoticeFilters.Add(Button(_fieldNoticeReader, "Filter" + filter, filter == "all" ? "All" : filter == "messages" ? "Messages" : "Other",
                24f + index * 224f, 88f, 208f, 50f, () =>
                {
                    if (ContentObscured) return;
                    _fieldNoticeFilter = filter;
                    _fieldNoticeSelection = null;
                    ClearFieldWorkflowConfirmation();
                    RenderFieldNoticeReader(reset: true);
                }));
        }
        _fieldNoticeClear = Button(_fieldNoticeReader, "ClearShown", "Clear shown", 968f, 88f, 336f, 50f,
            () => ConfirmFieldWorkflow("notices"));
        _fieldNoticeCount = Text(_fieldNoticeReader, "Count", "", 21f, Muted, 24f, 150f, 1280f, 34f);
        _fieldNoticeList = Scroll(_fieldNoticeReader, "NoticeList", 24f, 196f, 396f, 466f);
        _fieldNoticeDetail = Scroll(_fieldNoticeReader, "FullNotice", 444f, 196f, 860f, 386f);
        _fieldNoticeOpen = Button(_fieldNoticeReader, "OpenApp", "Open app", 444f, 602f, 528f, 60f, () => InvokeFieldReaderNotice(open: true));
        _fieldNoticeDismiss = Button(_fieldNoticeReader, "Dismiss", "Dismiss", 992f, 602f, 312f, 60f, () => InvokeFieldReaderNotice(open: false));
        _fieldNoticeReader.gameObject.SetActive(false);
    }

    private bool FieldNoticeMatches(ClientTabletNotification notice) => _fieldNoticeFilter == "all"
        || (notice.App == ClientTerminalApp.Messages) == (_fieldNoticeFilter == "messages");

    private bool CloseFieldNoticeReaderIfOpen()
    {
        if (_fieldNoticeReader == null || !_fieldNoticeReader.gameObject.activeSelf) return false;
        _fieldNoticeReader.gameObject.SetActive(false);
        ClearFieldWorkflowConfirmation();
        return true;
    }

    private void OpenFieldNoticeReader(long? id)
    {
        if (_disposed || ContentObscured) return;
        CloseLauncherPanels();
        _fieldNoticeFilter = "all";
        _fieldNoticeSelection = id;
        ClearFieldWorkflowConfirmation();
        _fieldNoticeReader.gameObject.SetActive(true);
        _fieldNoticeReader.SetAsLastSibling();
        RenderFieldNoticeReader(reset: true);
    }

    private void RenderFieldNoticeReader(bool reset = false)
    {
        var shown = _fieldNotifications.Where(FieldNoticeMatches).Reverse().ToArray();
        var oldSelection = _fieldNoticeSelection;
        if (!shown.Any(item => item.Id == _fieldNoticeSelection)) _fieldNoticeSelection = shown.FirstOrDefault()?.Id;
        var listOffset = reset ? 0f : _fieldNoticeList.Content.anchoredPosition.y;
        var detailOffset = reset || oldSelection != _fieldNoticeSelection ? 0f : _fieldNoticeDetail.Content.anchoredPosition.y;
        DestroyChildren(_fieldNoticeList.Content);
        DestroyChildren(_fieldNoticeDetail.Content);
        var y = 8f;
        foreach (var notice in shown)
        {
            var row = Button(_fieldNoticeList.Content, "Notice" + notice.Id, "", 8f, y, 366f, 100f, () =>
            {
                if (ContentObscured) return;
                _fieldNoticeSelection = notice.Id;
                RenderFieldNoticeReader();
                _fieldNoticeDetail.Reset();
            });
            row.Image.color = notice.Id == _fieldNoticeSelection ? Raised : Surface;
            Text(row.Image.transform, "Title", notice.Title, 23f, Ink, 12f, 10f, 342f, 48f, true).overflowMode = TextOverflowModes.Ellipsis;
            Text(row.Image.transform, "App", AppName(notice.App), 19f, Muted, 12f, 64f, 342f, 28f);
            y += 110f;
        }
        _fieldNoticeList.Height(Math.Max(466f, y));
        _fieldNoticeList.Content.anchoredPosition = new Vector2(0f, Math.Clamp(listOffset, 0f, Math.Max(0f, y - 466f)));
        var selected = shown.FirstOrDefault(item => item.Id == _fieldNoticeSelection);
        var detail = Text(_fieldNoticeDetail.Content, "Text", selected == null ? "No notifications in this view."
            : selected.Title + "\n\n" + selected.Detail, 27f, Ink, 20f, 16f, 800f, 340f);
        detail.richText = false;
        detail.enableWordWrapping = true;
        // Reserve enough space for the supported large-text preference as well.
        detail.fontSize = 27f * 1.16f;
        var height = TextHeight(detail, 800f, 340f) + 16f;
        detail.fontSize = 27f;
        Place(detail.rectTransform, 20f, 16f, 800f, height);
        _fieldNoticeDetail.Height(height + 32f);
        _fieldNoticeDetail.Content.anchoredPosition = new Vector2(0f, Math.Clamp(detailOffset, 0f, Math.Max(0f, height + 32f - 386f)));
        _fieldNoticeCount.text = shown.Length + " shown · " + _fieldNotifications.Count + " retained · Reading leaves notifications in place";
        _fieldNoticeClear.Button.interactable = shown.Length > 0;
        _fieldNoticeOpen.Button.interactable = _fieldNoticeDismiss.Button.interactable = selected != null;
        _fieldNoticeOpen.Label.text = selected == null ? "Open app" : "Open " + AppName(selected.App);
        for (var index = 0; index < _fieldNoticeFilters.Count; index++)
            _fieldNoticeFilters[index].Image.color = index == (_fieldNoticeFilter == "all" ? 0 : _fieldNoticeFilter == "messages" ? 1 : 2) ? Raised : Surface;
    }

    private void InvokeFieldReaderNotice(bool open)
    {
        if (_disposed || ContentObscured || _fieldNoticeSelection is not { } id || !_fieldNotifications.Any(item => item.Id == id)) return;
        if (open) _fieldNoticeReader.gameObject.SetActive(false);
        FieldCommand((open ? "notification:" : "dismiss:") + id.ToString(CultureInfo.InvariantCulture));
    }
}
