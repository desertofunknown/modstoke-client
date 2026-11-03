// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientTerminalView
{
    private RectTransform _servicesSurface = null!, _servicesTabs = null!, _servicesSubmit = null!;
    private ScrollArea _servicesOperations = null!, _servicesForm = null!, _servicesResponse = null!;
    private TextMeshProUGUI _servicesOperationHeading = null!, _servicesSelection = null!, _servicesHint = null!;
    private TextMeshProUGUI _servicesReview = null!, _servicesEffects = null!, _servicesStatus = null!;
    private TextMeshProUGUI _servicesReplyHeading = null!, _servicesReplyContext = null!, _servicesReply = null!, _servicesNotes = null!, _servicesDetails = null!;
    private ButtonWidget _servicesDetailsToggle = null!;
    private Guid? _servicesFormScope;
    private string _servicesReviewValue = "";

    private void BuildServicesApp()
    {
        _servicesSurface = Node(_applicationLayer, "ServicesApp");
        Place(_servicesSurface, 56f, 176f, WorkspaceWidth, WorkspaceHeight);
        _servicesTabs = Node(_servicesSurface, "ServiceCategories");
        Place(_servicesTabs, 0f, 0f, WorkspaceWidth, 60f);
        _servicesOperations = Scroll(_servicesSurface, "ServiceOperations", 0f, 78f, 264f, 522f);
        _servicesForm = Scroll(_servicesSurface, "ServiceForm", 282f, 78f, 474f, 314f);
        // Keep the primary controls above the temporary system notice at the foot of the screen.
        _servicesSubmit = RoundedPanel(_servicesSurface, "ServiceAction", Surface, 282f, 406f, 474f, 134f).rectTransform;
        _servicesResponse = Scroll(_servicesSurface, "ServiceResponse", 774f, 78f, 554f, 522f);
        _servicesOperationHeading = Text(_servicesOperations.Content, "OperationHeading", "CHOOSE AN ACTION", 19f, Muted,
            18f, 18f, 214f, 30f, true);
        _servicesSelection = Text(_servicesForm.Content, "Selection", "", 27f, Ink, 20f, 18f, 420f, 40f, true);
        _servicesHint = Text(_servicesForm.Content, "FormHint", "", 21f, Muted, 20f, 60f, 420f, 60f);
        _servicesReview = Text(_servicesForm.Content, "ExactReview", "", 23f, Amber, 20f, 130f, 420f, 60f);
        _servicesEffects = Text(_servicesForm.Content, "Effects", "", 22f, Ink, 20f, 200f, 420f, 80f);
        _servicesStatus = Text(_servicesResponse.Content, "RequestStatus", "", 21f, Muted, 20f, 18f, 500f, 60f);
        _servicesReplyHeading = Text(_servicesResponse.Content, "ReplyHeading", "LAST SERVER RESPONSE", 19f, Muted,
            20f, 90f, 500f, 30f, true);
        _servicesReplyContext = Text(_servicesResponse.Content, "ReplyContext", "", 21f, Muted, 20f, 120f, 500f, 50f);
        _servicesReply = Text(_servicesResponse.Content, "Reply", "", 24f, Ink, 20f, 130f, 500f, 80f);
        _servicesNotes = Text(_servicesResponse.Content, "ResponseNotes", "", 21f, Muted, 20f, 220f, 500f, 80f);
        _servicesDetailsToggle = Button(_servicesResponse.Content, "ServiceDetails", "+  Connection & app details",
            20f, 320f, 500f, 56f, ToggleDetails);
        _servicesDetails = Text(_servicesResponse.Content, "ServiceDetailsText", "", 21f, Muted, 20f, 390f, 500f, 80f);
        _servicesSurface.gameObject.SetActive(false);
    }

    private void SetServicesAppVisible(bool visible)
    {
        _servicesSurface.gameObject.SetActive(visible);
        // Share the existing bounded input widgets and their scope-checked callbacks.
        var parent = visible ? _servicesForm.Content : _actions.Content;
        if (_fieldsRoot.parent != parent) _fieldsRoot.SetParent(parent, false);
        if (!visible) _fieldsRoot.gameObject.SetActive(true);
    }

    private void ResetServicesScroll()
    {
        _servicesOperations.Reset();
        _servicesForm.Reset();
        _servicesResponse.Reset();
    }

    private void LayoutServices(ClientTerminalPage page)
    {
        var hasReview = !string.IsNullOrWhiteSpace(page.Review);
        if (_servicesFormScope != page.Inputs?.Scope || _servicesReviewValue != page.Review)
        {
            _servicesForm.Reset();
            if (_servicesFormScope != page.Inputs?.Scope) _servicesOperations.Reset();
        }
        _servicesFormScope = page.Inputs?.Scope;
        _servicesReviewValue = page.Review;
        var group = "campaigns";
        var mutation = false;
        foreach (var widget in _commands)
        {
            // These are local provider operation IDs, never interpreted reply text.
            if (widget.Action.StartsWith("select.Points", StringComparison.Ordinal)) group = "points";
            else if (widget.Action.StartsWith("select.Travel", StringComparison.Ordinal)) group = "travel";
            else if (widget.Action.StartsWith("select.Facility", StringComparison.Ordinal)) group = "facilities";
            mutation |= widget.Action == "review";
        }

        _servicesSelection.text = string.IsNullOrWhiteSpace(page.Selection) ? "Choose a service" : page.Selection;
        _servicesHint.text = hasReview ? "Review the command and effects. Confirm sends it once."
            : mutation ? "Complete the inputs, then review this change."
            : _fields.Count == 0 ? "Request this information when you are ready."
            : "Set the inputs, then request this information.";
        if (_commands.Count == 0) _servicesHint.text = "Services are unavailable. Join a compatible server to begin.";
        var formY = 18f;
        PlaceText(_servicesSelection, 20f, ref formY, 420f, 10f);
        PlaceText(_servicesHint, 20f, ref formY, 420f, 22f);
        _fieldsRoot.gameObject.SetActive(!hasReview);
        var fieldsY = 0f;
        if (!hasReview)
        {
            foreach (var widget in _fields)
            {
                PlaceText(widget.Label, 20f, ref fieldsY, 420f, 8f);
                Place(widget.Input.GetComponent<RectTransform>(), 20f, fieldsY, 420f, 58f);
                fieldsY += 68f;
                PlaceText(widget.Description, 20f, ref fieldsY, 420f, 10f);
                if (!string.IsNullOrWhiteSpace(widget.Error.text))
                    PlaceText(widget.Error, 20f, ref fieldsY, 420f, 8f);
                else Place(widget.Error.rectTransform, 20f, fieldsY, 420f, 0f);
                fieldsY += 16f;
            }
        }
        Place(_fieldsRoot, 0f, formY, _servicesForm.Width, fieldsY);
        formY += fieldsY;
        _servicesReview.gameObject.SetActive(hasReview);
        if (hasReview)
        {
            _servicesReview.text = "EXACT COMMAND\n" + page.Review;
            PlaceText(_servicesReview, 20f, ref formY, 420f, 22f);
        }
        _servicesEffects.text = "";
        var responseNotes = new List<ClientTerminalNote>();
        foreach (var note in page.Notes)
        {
            if (note.Label == "Effects") _servicesEffects.text = "EFFECTS\n" + note.Text;
            else responseNotes.Add(note);
        }
        _servicesEffects.gameObject.SetActive(_servicesEffects.text.Length > 0);
        if (_servicesEffects.text.Length > 0) PlaceText(_servicesEffects, 20f, ref formY, 420f, 20f);
        foreach (var widget in _commands)
        {
            if (widget.Action is not ("page-previous" or "page-next" or "stage-previous" or "stage-next" or "source-read" or "reset-draft")) continue;
            widget.Description.gameObject.SetActive(false);
            var height = Math.Max(56f, TextHeight(widget.Control.Label, 388f) + 18f);
            PlaceServiceCommand(widget, _servicesForm.Content, 20f, formY, 420f, height);
            widget.Control.Image.gameObject.SetActive(!hasReview);
            if (!hasReview) formY += height + 10f;
        }
        _servicesForm.Height(formY);

        var operationY = 18f;
        PlaceText(_servicesOperationHeading, 18f, ref operationY, 214f, 18f);
        var categoryIndex = 0;
        foreach (var widget in _commands)
        {
            widget.Description.gameObject.SetActive(false);
            var action = widget.Action;
            if (action is "campaigns" or "points" or "travel" or "facilities")
            {
                PlaceServiceCommand(widget, _servicesTabs, categoryIndex++ * 336f, 0f, 320f, 60f);
                StyleServiceChoice(widget, action == group);
            }
            else if (action.StartsWith("select.", StringComparison.Ordinal))
            {
                var height = Math.Max(64f, TextHeight(widget.Control.Label, 182f) + 20f);
                PlaceServiceCommand(widget, _servicesOperations.Content, 18f, operationY, 214f, height);
                // Selection is provider-owned local form metadata, not server text.
                StyleServiceChoice(widget, widget.Control.Label.text == page.Selection);
                operationY += height + 10f;
            }
            else if (action is "read" or "review")
            {
                PlaceServiceCommand(widget, _servicesSubmit, 20f, 18f, 434f, 98f);
                widget.Control.Image.gameObject.SetActive(!hasReview);
            }
            else if (action.StartsWith("confirm.", StringComparison.Ordinal))
                PlaceServiceCommand(widget, _servicesSubmit, 20f, 10f, 434f, 58f);
            else if (action == "cancel")
                PlaceServiceCommand(widget, _servicesSubmit, 20f, 78f, 434f, 46f);
        }
        _servicesOperations.Height(operationY + 12f);

        _servicesStatus.text = page.Status;
        _servicesReply.text = string.IsNullOrWhiteSpace(page.Reply) ? "No response yet. Request a read explicitly." : page.Reply;
        _servicesNotes.text = NotesText(responseNotes);
        _servicesDetails.text = NotesText(page.Details);
        _servicesReplyContext.text = "";
        foreach (var detail in page.Details)
            if (detail.Label == "Requested") _servicesReplyContext.text = detail.Text;
        var responseY = 18f;
        PlaceText(_servicesStatus, 20f, ref responseY, 500f, 22f);
        PlaceText(_servicesReplyHeading, 20f, ref responseY, 500f, 12f);
        _servicesReplyContext.gameObject.SetActive(_servicesReplyContext.text.Length > 0);
        if (_servicesReplyContext.text.Length > 0) PlaceText(_servicesReplyContext, 20f, ref responseY, 500f, 14f);
        PlaceText(_servicesReply, 20f, ref responseY, 500f, 24f);
        foreach (var widget in _commands)
        {
            if (widget.Action is not ("refresh" or "clear-reply")) continue;
            var height = Math.Max(56f, TextHeight(widget.Control.Label, 468f) + 18f);
            PlaceServiceCommand(widget, _servicesResponse.Content, 20f, responseY, 500f, height);
            responseY += height + 10f;
        }
        _servicesNotes.gameObject.SetActive(_servicesNotes.text.Length > 0);
        if (_servicesNotes.text.Length > 0) PlaceText(_servicesNotes, 20f, ref responseY, 500f, 22f);
        var hasDetails = page.Details.Count > 0;
        _servicesDetailsToggle.Image.gameObject.SetActive(hasDetails);
        _servicesDetails.gameObject.SetActive(hasDetails && _detailsExpanded);
        if (hasDetails)
        {
            _servicesDetailsToggle.Label.text = _detailsExpanded ? "-  Hide connection & app details" : "+  Connection & app details";
            var height = Math.Max(56f, TextHeight(_servicesDetailsToggle.Label, 468f) + 18f);
            Place(_servicesDetailsToggle.Image.rectTransform, 20f, responseY, 500f, height);
            Place(_servicesDetailsToggle.Label.rectTransform, 16f, 0f, 468f, height);
            responseY += height + 16f;
            if (_detailsExpanded) PlaceText(_servicesDetails, 20f, ref responseY, 500f, 16f);
        }
        _servicesResponse.Height(responseY + 8f);
    }

    private static void PlaceServiceCommand(CommandWidget widget, Transform parent, float x, float y, float width, float height)
    {
        if (widget.Control.Image.transform.parent != parent) widget.Control.Image.transform.SetParent(parent, false);
        widget.Control.Image.gameObject.SetActive(true);
        Place(widget.Control.Image.rectTransform, x, y, width, height);
        Place(widget.Control.Label.rectTransform, 16f, 0f, width - 32f, height);
        widget.Control.Label.overflowMode = TextOverflowModes.Ellipsis;
    }

    private void StyleServiceChoice(CommandWidget widget, bool selected)
    {
        widget.Control.Image.color = selected ? AppColor(ClientTerminalApp.Services) : Raised;
        widget.Control.Label.color = !widget.Control.Button.interactable ? Dim : selected ? Color.white : Ink;
    }
}
