// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.Events;

namespace Modstoke.Client;

internal sealed partial class ClientTerminalView
{
    private readonly List<ClientFieldPlace> _fieldPlaces = new();
    private Guid? _fieldNotePlaceId;
    private Guid? _fieldNotePlaceFilter;
    private ButtonWidget _notePlaceLink = null!;
    private ButtonWidget _notePlaceMap = null!;
    private ButtonWidget _notePlaceUnlink = null!;
    private ButtonWidget _notePlaceScope = null!;
    private ButtonWidget _notePlaceAll = null!;
    private TextMeshProUGUI _notePlaceSummary = null!;
    private RectTransform _fieldPlacePicker = null!;
    private CanvasGroup _fieldNotebookContent = null!;
    private TMP_InputField _fieldPlaceSearch = null!;
    private TextMeshProUGUI _fieldPlacePickerHeading = null!;
    private TextMeshProUGUI _fieldPlacePickerStatus = null!;
    private ScrollArea _fieldPlaceList = null!;
    private bool _fieldPlacePickerLinks;

    private void BuildFieldLocationLinks()
    {
        _notePlaceScope = Button(_journalSurface, "LocationScope", "All locations", 0f, 214f, 252f, 44f,
            () => OpenFieldPlacePicker(link: false));
        _notePlaceScope.Label.overflowMode = TextOverflowModes.Ellipsis;
        _notePlaceAll = Button(_journalSurface, "AllLocations", "All", 260f, 214f, 100f, 44f,
            () => FieldCommand("notes-all"));
        Place(_notebookList.Root.rectTransform, 0f, 270f, 360f, 246f);

        var editor = _noteTitle.transform.parent;
        _notePlaceLink = Button(editor, "LinkedLocation", "Link a saved place", 24f, 110f, 496f, 44f,
            () => OpenFieldPlacePicker(link: true));
        _notePlaceLink.Label.overflowMode = TextOverflowModes.Ellipsis;
        _notePlaceMap = Button(editor, "ShowLocation", "Show on map", 532f, 110f, 212f, 44f,
            () => FieldCommand("note-map"));
        _notePlaceUnlink = Button(editor, "UnlinkLocation", "Unlink", 756f, 110f, 164f, 44f,
            () => FieldCommand("note-unlink"));
        _notePlaceSummary = Text(editor, "LocationDetails", "", 18f, Muted, 24f, 158f, 896f, 54f);
        _notePlaceSummary.richText = false;
        _notePlaceSummary.enableWordWrapping = true;
        _notePlaceSummary.overflowMode = TextOverflowModes.Ellipsis;
        Place(_noteBody.GetComponent<RectTransform>(), 24f, 220f, 896f, 142f);

        // Keep the editor and its controls together so the picker can cover them without
        // leaving hidden inputs or automatic keyboard navigation active underneath it.
        var content = RoundedPanel(_journalSurface, "NotebookContent", Screen, 0f, 0f, 1328f, 600f).rectTransform;
        content.GetComponent<UnityEngine.UI.Image>().raycastTarget = false;
        while (_journalSurface.childCount > 1) _journalSurface.GetChild(0).SetParent(content, false);
        _fieldNotebookContent = content.gameObject.AddComponent<CanvasGroup>();
        _fieldPlacePicker = RoundedPanel(_journalSurface, "ChooseSavedPlace", Screen, 0f, 0f, 1328f, 600f).rectTransform;
        _fieldPlacePicker.GetComponent<UnityEngine.UI.Image>().raycastTarget = true;
        _fieldPlacePickerHeading = Text(_fieldPlacePicker, "Heading", "Link a saved place", 32f, Ink, 24f, 16f, 1064f, 48f, true);
        Button(_fieldPlacePicker, "Cancel", "Cancel", 1128f, 16f, 176f, 48f, () => CloseFieldPlacePickerIfOpen());
        _fieldPlaceSearch = FieldInput(_fieldPlacePicker, 24f, 78f, 908f, 48f, 96, false);
        var placeholder = Text(_fieldPlaceSearch.textViewport, "Hint", "Search saved pin names, categories or notes", 22f, Muted, 0f, 0f, 884f, 36f);
        _fieldPlaceSearch.placeholder = placeholder;
        _fieldPlaceSearch.onValueChanged.AddListener((UnityAction<string>)new Action<string>(_ => RenderFieldPlacePicker(reset: true)));
        Button(_fieldPlacePicker, "ClearSearch", "Clear search", 948f, 78f, 356f, 48f, () =>
        {
            if (!ContentObscured) SetFieldInput(_fieldPlaceSearch, "");
        });
        _fieldPlacePickerStatus = Text(_fieldPlacePicker, "Status", "", 19f, Muted, 24f, 134f, 1280f, 30f);
        _fieldPlaceList = Scroll(_fieldPlacePicker, "SavedPlaces", 24f, 176f, 1280f, 400f);
        _fieldPlacePicker.gameObject.SetActive(false);
        RefreshFieldLocationUi();
    }

    private void RefreshFieldPlaces(IReadOnlyList<ClientFieldPlace> places)
    {
        if (SameFieldItems(_fieldPlaces, places)) return;
        _fieldPlaces.Clear();
        _fieldPlaces.AddRange(places);
        RenderNotebookBrowser();
        if (_fieldPlacePicker != null && _fieldPlacePicker.gameObject.activeSelf) RenderFieldPlacePicker();
        RefreshFieldLocationUi();
    }

    private void RefreshFieldLocationUi()
    {
        if (_notePlaceLink == null) return;
        var place = _fieldPlaces.FirstOrDefault(item => item.Id == _fieldNotePlaceId);
        var filter = _fieldPlaces.FirstOrDefault(item => item.Id == _fieldNotePlaceFilter);
        _notePlaceLink.Label.text = place == null ? "Link a saved place" : "Location: " + place.Name;
        _notePlaceLink.Button.interactable = _fieldPlaces.Count > 0;
        _notePlaceMap.Button.interactable = place != null;
        _notePlaceUnlink.Button.interactable = _fieldNotePlaceId.HasValue;
        _notePlaceSummary.text = place == null
            ? _fieldPlaces.Count == 0 ? "Save a pin on your map, then link it here. Your note can also stay unlinked."
                : "No location linked. Choose an existing pin; the link is saved with your note."
            : ClientFieldLocationText.Coordinates(place) + "\n" + ClientFieldLocationText.Source(place);
        _notePlaceScope.Label.text = filter?.Name ?? "All locations";
        _notePlaceScope.Button.interactable = _fieldPlaces.Count > 0;
        _notePlaceAll.Button.interactable = _fieldNotePlaceFilter.HasValue;
    }

    private void OpenFieldPlacePicker(bool link)
    {
        if (_disposed || ContentObscured || _fieldPlaces.Count == 0) return;
        _fieldPlacePickerLinks = link;
        _noteTitle.DeactivateInputField();
        _noteBody.DeactivateInputField();
        _notebookSearch.DeactivateInputField();
        ClearFieldWorkflowConfirmation();
        ResetNoteDelete();
        _fieldPlaceSearch.SetTextWithoutNotify("");
        _fieldPlacePickerHeading.text = link ? "Link this note to a saved place" : "Read notes for a saved place";
        _fieldNotebookContent.interactable = false;
        _fieldNotebookContent.blocksRaycasts = false;
        _fieldPlacePicker.gameObject.SetActive(true);
        _fieldPlacePicker.SetAsLastSibling();
        RenderFieldPlacePicker(reset: true);
        if (UnityEngine.EventSystems.EventSystem.current != null)
            UnityEngine.EventSystems.EventSystem.current.SetSelectedGameObject(_fieldPlaceSearch.gameObject);
        _fieldPlaceSearch.ActivateInputField();
    }

    private bool CloseFieldPlacePickerIfOpen()
    {
        if (_fieldPlacePicker == null || !_fieldPlacePicker.gameObject.activeSelf) return false;
        _fieldPlaceSearch.DeactivateInputField();
        _fieldPlacePicker.gameObject.SetActive(false);
        _fieldNotebookContent.interactable = true;
        _fieldNotebookContent.blocksRaycasts = true;
        var opener = _fieldPlacePickerLinks ? _notePlaceLink : _notePlaceScope;
        if (_journalSurface.gameObject.activeInHierarchy && UnityEngine.EventSystems.EventSystem.current != null)
            UnityEngine.EventSystems.EventSystem.current.SetSelectedGameObject(opener.Button.gameObject);
        return true;
    }

    private void RenderFieldPlacePicker(bool reset = false)
    {
        if (_disposed || _fieldPlacePicker == null || !_fieldPlacePicker.gameObject.activeSelf) return;
        var tokens = _fieldPlaceSearch.text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var shown = _fieldPlaces.Where(place => tokens.All(token => place.Name.Contains(token, StringComparison.OrdinalIgnoreCase)
            || place.Category.Contains(token, StringComparison.OrdinalIgnoreCase) || place.Note.Contains(token, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(place => place.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(place => place.Id).ToArray();
        var offset = reset ? 0f : _fieldPlaceList.Content.anchoredPosition.y;
        DestroyChildren(_fieldPlaceList.Content);
        var y = 8f;
        foreach (var place in shown)
        {
            var selected = place.Id == (_fieldPlacePickerLinks ? _fieldNotePlaceId : _fieldNotePlaceFilter);
            var row = Button(_fieldPlaceList.Content, "Place" + place.Id, "", 8f, y, 1248f, 112f, () =>
            {
                if (_disposed || ContentObscured) return;
                CloseFieldPlacePickerIfOpen();
                FieldCommand((_fieldPlacePickerLinks ? "note-link:" : "notes-place:") + place.Id.ToString("N"));
            });
            row.Image.color = selected ? Raised : Surface;
            var count = _fieldNotes.Count(note => note.PlaceId == place.Id);
            Text(row.Image.transform, "Name", (selected ? "Selected · " : "") + place.Name, 24f, Ink,
                16f, 8f, 954f, 38f, true).overflowMode = TextOverflowModes.Ellipsis;
            Text(row.Image.transform, "NoteCount", count + (count == 1 ? " note" : " notes"), 20f, Mint,
                990f, 10f, 238f, 30f).alignment = TextAlignmentOptions.MidlineRight;
            Text(row.Image.transform, "Coordinates", ClientFieldLocationText.Coordinates(place), 19f, Muted,
                16f, 48f, 1216f, 28f).overflowMode = TextOverflowModes.Ellipsis;
            Text(row.Image.transform, "Source", ClientFieldLocationText.Source(place), 19f, Muted,
                16f, 80f, 1216f, 28f).overflowMode = TextOverflowModes.Ellipsis;
            y += 124f;
        }
        if (shown.Length == 0) Text(_fieldPlaceList.Content, "Empty", "No saved places match. Clear search to browse all pins.",
            25f, Muted, 24f, 24f, 1184f, 120f);
        _fieldPlaceList.Height(Math.Max(400f, y));
        _fieldPlaceList.Content.anchoredPosition = new Vector2(0f, Math.Clamp(offset, 0f, Math.Max(0f, y - 400f)));
        _fieldPlacePickerStatus.text = shown.Length + " / " + _fieldPlaces.Count + " saved places · "
            + (_fieldPlacePickerLinks ? "Choose a pin, then Save note to keep the link." : "Choose a place to filter your saved notes.");
    }
}
