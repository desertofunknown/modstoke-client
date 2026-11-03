// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Text;
using Modstoke.Loader;
using TMPro;
using UnityEngine;
using UnityEngine.Events;

namespace Modstoke.Client;

// Native widgets only. The owning terminal controls visibility, focus and input capture.
internal sealed partial class ClientTerminalView : IDisposable
{
    private const float WorkspaceWidth = 1328f;
    private const float WorkspaceHeight = 600f;
    private const float ActionsWidth = 480f;
    private const float ReadingWidth = 824f;
    private const float ReadingX = 504f;
    private const float FormWidth = 418f;
    private const float ReadingTextWidth = 758f;
    private readonly Action<ClientTerminalApp> _select;
    private readonly Action<string, string> _command;
    private readonly Func<string, Guid, string, string, CommandResultSnapshot> _edit;
    private readonly GameObject _device;
    private readonly RectTransform _applicationLayer;
    private readonly CanvasGroup _applicationInput;
    private readonly RectTransform _home;
    private readonly RectTransform _workspace;
    private readonly RectTransform _navigationSurface;
    private readonly TextMeshProUGUI _navigationUnavailable;
    private readonly ButtonWidget _zoomIn;
    private readonly ButtonWidget _zoomOut;
    private readonly RectTransform _messagesSurface;
    private readonly ScrollArea _messageHistory;
    private readonly TextMeshProUGUI _messageLog;
    private readonly TMP_InputField _messageInput;
    private readonly ButtonWidget _messageSend;
    private readonly TextMeshProUGUI _messageStatus;
    private readonly TextMeshProUGUI _messengerContext;
    private readonly TextMeshProUGUI _messengerTarget;
    private readonly TextMeshProUGUI _messagesUnread;
    private readonly RectTransform _messagesUnreadBadge;
    private readonly TextMeshProUGUI _messagesHomeUnread;
    private readonly TextMeshProUGUI _title;
    private readonly TextMeshProUGUI _subtitle;
    private readonly TextMeshProUGUI _link;
    private readonly UnityEngine.UI.Image _linkIndicator;
    private readonly TextMeshProUGUI _systemStatus;
    private readonly ScrollArea _actions;
    private readonly RectTransform _fieldsRoot;
    private readonly RectTransform _commandsRoot;
    private readonly TextMeshProUGUI _actionHeading;
    private readonly TextMeshProUGUI _emptyActions;
    private readonly RectTransform _execution;
    private readonly ScrollArea _review;
    private readonly TextMeshProUGUI _reviewHeading;
    private readonly TextMeshProUGUI _reviewText;
    private readonly ScrollArea _reading;
    private readonly TextMeshProUGUI _status;
    private readonly TextMeshProUGUI _selection;
    private readonly TextMeshProUGUI _notesHeading;
    private readonly TextMeshProUGUI _notes;
    private readonly TextMeshProUGUI _replyHeading;
    private readonly TextMeshProUGUI _reply;
    private readonly ButtonWidget _detailsToggle;
    private readonly TextMeshProUGUI _details;
    private readonly List<FieldWidget> _fields = new();
    private readonly List<CommandWidget> _commands = new();
    private ClientTerminalPage? _page;
    private Guid? _scope;
    private bool _detailsExpanded;
    private bool _messageInputUpdating;
    private bool _messagesScrollToEnd;
    private int _messageSnapshotVersion;
    private string _acceptedMessageDraft = "";
    private bool _disposed;
    internal bool IsAlive => !_disposed && _device != null && _device;
    private ClientIslandClock _islandClock;
    internal Action? TouchFeedback { get; set; }

    internal void SetIslandClock(ClientIslandClock clock)
    {
        if (_disposed) return;
        _islandClock = clock;
        _systemStatus.text = clock.TimeText;
        UpdateClock();
    }

    internal RectTransform? NavigationMount { get; }
    internal bool NavigationVisible => !_disposed && !_shadeCapture && !DesktopCaptured
        && _page?.App == ClientTerminalApp.Navigation;

    internal void SetNavigationAvailable(bool available)
    {
        if (_disposed) return;
        _navigationUnavailable.gameObject.SetActive(!available);
        _zoomIn.Button.interactable = available;
        _zoomOut.Button.interactable = available;
    }

    internal void SetSystemStatus(string status)
    {
        if (_disposed) return;
        _systemStatus.text = _islandClock.TimeText;
        RefreshNotificationShade();
        RefreshDesktopStatus();
    }

    private sealed class FieldWidget
    {
        internal required string Id;
        internal required TMP_InputField Input;
        internal required TextMeshProUGUI Label;
        internal required TextMeshProUGUI Description;
        internal required TextMeshProUGUI Error;
        internal required string AcceptedValue;
        internal bool Updating;
    }

    private sealed class CommandWidget
    {
        internal required string Id;
        internal required string Action;
        internal required bool Pinned;
        internal required ButtonWidget Control;
        internal required TextMeshProUGUI Description;
    }

    internal ClientTerminalView(Transform parent, Action<ClientTerminalApp> select,
        Action<string, string> command,
        Func<string, Guid, string, string, CommandResultSnapshot> edit, Action close)
    {
        _select = select;
        _command = command;
        _edit = edit;
        var device = Panel(parent, "ModstokeFieldTerminal", Bezel, 0f, 0f, 1440f, 900f);
        _device = device.gameObject;
        device.raycastTarget = true;
        device.rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
        device.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        device.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        device.rectTransform.anchoredPosition = Vector2.zero;
        Panel(device.transform, "Screen", Screen, 24f, 24f, 1392f, 852f);
        var statusBar = Panel(device.transform, "StatusBar", Chrome, 24f, 24f, 1392f, 48f);
        statusBar.raycastTarget = true;
        Text(device.transform, "Brand", "MODSTOKE", 20f, ChromeInk, 56f, 34f, 170f, 30f, role: TabletTextRole.Display);
        Panel(device.transform, "ShadeHandle", ChromeMuted, 675f, 58f, 90f, 4f);
        _systemStatus = Text(device.transform, "SystemStatus", "", 22f, ChromeInk, 226f, 34f, 130f, 30f, role: TabletTextRole.Display);
        _systemStatus.alignment = TextAlignmentOptions.TopRight;
        _systemStatus.overflowMode = TextOverflowModes.Ellipsis;
        _linkIndicator = RoundedPanel(device.transform, "ConnectionIndicator", ChromeMuted, 1016f, 43f, 8f, 8f);
        _link = Text(device.transform, "Link", "", 20f, ChromeMuted, 1036f, 34f, 190f, 30f, role: TabletTextRole.Status);
        _link.alignment = TextAlignmentOptions.TopRight;
        _satelliteBar = Text(device.transform, "SatelliteLink", "SATCOM · STANDBY", 19f, ChromeMuted,
            734f, 36f, 240f, 28f, role: TabletTextRole.Status);
        _satelliteBar.overflowMode = TextOverflowModes.Ellipsis;
        var closeButton = Button(device.transform, "Close", "Put away", 1246f, 30f, 146f, 36f, close);
        closeButton.Image.color = ChromeRaised;
        closeButton.Label.color = ChromeInk;
        closeButton.Label.fontSize = 21f;

        _applicationLayer = Node(device.transform, "Applications");
        Place(_applicationLayer, 0f, 0f, 1440f, 900f);
        _applicationInput = _applicationLayer.gameObject.AddComponent<CanvasGroup>();
        _title = Text(_applicationLayer, "PageTitle", "Home", 36f, Ink, 56f, 86f, WorkspaceWidth, 46f, true);
        _title.overflowMode = TextOverflowModes.Ellipsis;
        _subtitle = Text(_applicationLayer, "PageSubtitle", "", 21f, Muted, 56f, 134f, WorkspaceWidth, 30f);
        _subtitle.overflowMode = TextOverflowModes.Ellipsis;

        _home = Node(_applicationLayer, "Home");
        Place(_home, 56f, 92f, WorkspaceWidth, 688f);
        _messagesHomeUnread = BuildFieldHome();

        _workspace = Node(_applicationLayer, "ApplicationWorkspace");
        Place(_workspace, 56f, 176f, WorkspaceWidth, WorkspaceHeight);
        _actions = Scroll(_workspace, "Actions", 0f, 0f, ActionsWidth, WorkspaceHeight);
        _fieldsRoot = Node(_actions.Content, "Fields");
        _commandsRoot = Node(_actions.Content, "Commands");
        _actionHeading = Text(_commandsRoot, "Heading", "BROWSE & PREPARE", 20f, Muted, 24f, 0f, FormWidth, 32f, true);
        _emptyActions = Text(_commandsRoot, "Empty", "No actions are available for this view.", 24f, Muted, 24f, 40f, FormWidth, 80f);
        _execution = Panel(_workspace, "CurrentAction", Surface, ReadingX, 0f, ReadingWidth, 130f).rectTransform;
        _selection = Text(_execution, "Selection", "", 26f, Ink, 26f, 18f, ReadingTextWidth, 38f, true);
        _selection.overflowMode = TextOverflowModes.Ellipsis;
        _review = Scroll(_workspace, "Review", ReadingX, 0f, ReadingWidth, 220f);
        _review.Root.color = ReviewSurface;
        _reviewHeading = Text(_review.Content, "Heading", "REVIEW BEFORE CONFIRMING", 22f, Amber, 26f, 24f, ReadingTextWidth, 34f, true);
        _reviewText = Text(_review.Content, "ReviewText", "", 25f, Ink, 26f, 68f, ReadingTextWidth, 150f);
        _reading = Scroll(_workspace, "Response", ReadingX, 0f, ReadingWidth, WorkspaceHeight);
        _status = Text(_reading.Content, "Status", "", 22f, Ink, 26f, 24f, ReadingTextWidth, 36f, true);
        _notesHeading = Text(_reading.Content, "NotesHeading", "EFFECTS & NOTES", 20f, Amber, 26f, 120f, ReadingTextWidth, 30f, true);
        _notes = Text(_reading.Content, "Notes", "", 24f, Ink, 26f, 158f, ReadingTextWidth, 100f);
        _replyHeading = Text(_reading.Content, "ReplyHeading", "RESPONSE", 20f, Muted, 26f, 270f, ReadingTextWidth, 30f, true);
        _reply = Text(_reading.Content, "Reply", "", 25f, Ink, 26f, 310f, ReadingTextWidth, 150f);
        _detailsToggle = Button(_reading.Content, "DetailsToggle", "+  Connection & app details", 26f, 470f, ReadingTextWidth, 50f, ToggleDetails);
        _detailsToggle.Label.fontSize = 21f;
        _detailsToggle.Image.color = Screen;
        _details = Text(_reading.Content, "Details", "", 22f, Muted, 26f, 532f, ReadingTextWidth, 80f);

        _navigationSurface = RoundedPanel(_applicationLayer, "NavigationSurface", Surface, 56f, 146f,
            WorkspaceWidth, 630f).rectTransform;
        var navigationMount = Node(_navigationSurface, "LandscapeMapMount");
        NavigationMount = navigationMount;
        Stretch(navigationMount, 0f, 0f, 0f, 0f);
        navigationMount.gameObject.AddComponent<UnityEngine.UI.RectMask2D>();
        _navigationUnavailable = Text(_navigationSurface, "MapUnavailable", "Waiting for navigation data…",
            26f, Muted, 40f, 240f, WorkspaceWidth - 80f, 54f);
        _navigationUnavailable.alignment = TextAlignmentOptions.Center;
        _zoomOut = Button(_navigationSurface, "ZoomOut", "-", WorkspaceWidth - 168f, 24f, 64f, 58f, () =>
        {
            if (NavigationVisible) _command("navigation", "zoom-out");
        });
        _zoomIn = Button(_navigationSurface, "ZoomIn", "+", WorkspaceWidth - 88f, 24f, 64f, 58f, () =>
        {
            if (NavigationVisible) _command("navigation", "zoom-in");
        });
        _zoomOut.Label.fontSize = _zoomIn.Label.fontSize = 34f;
        _zoomOut.Label.alignment = _zoomIn.Label.alignment = TextAlignmentOptions.Center;
        SetNavigationAvailable(false);
        _navigationSurface.gameObject.SetActive(false);

        _messagesSurface = Panel(_applicationLayer, "MessagesSurface", Surface, 56f, 176f,
            WorkspaceWidth, WorkspaceHeight).rectTransform;
        _messengerContext = Text(_messagesSurface, "ChatContext", "Expedition", 24f,
            Ink, 24f, 16f, WorkspaceWidth - 48f, 34f, true);
        _messengerContext.overflowMode = TextOverflowModes.Ellipsis;
        BuildMessengerNavigation();
        _messageHistory = Scroll(_messagesSurface, "ChatHistory", 16f, 132f, WorkspaceWidth - 32f, 266f);
        _messageHistory.Root.color = Screen;
        _messageLog = Text(_messageHistory.Content, "Messages", "No messages yet.",
            25f, Ink, 24f, 18f, WorkspaceWidth - 96f, 50f);
        _messengerTarget = Text(_messagesSurface, "MessageLabel", "Message to expedition", 22f, Muted, 24f, 412f,
            WorkspaceWidth - 48f, 32f, true);
        _messengerTarget.overflowMode = TextOverflowModes.Ellipsis;
        _messageInput = Input(_messagesSurface, "", 256);
        Place(_messageInput.GetComponent<RectTransform>(), 24f, 454f, WorkspaceWidth - 224f, 58f);
        _messageInput.onValueChanged.AddListener((UnityAction<string>)new Action<string>(EditMessage));
        _messageSend = Button(_messagesSurface, "SendMessage", "Send", WorkspaceWidth - 176f,
            454f, 152f, 58f, SendMessage);
        _messageSend.Button.interactable = false;
        _messageStatus = Text(_messagesSurface, "ChatStatus", "", 21f, Ink, 24f, 526f,
            WorkspaceWidth - 48f, 62f);
        _messageStatus.overflowMode = TextOverflowModes.Ellipsis;
        _messagesSurface.gameObject.SetActive(false);

        _messagesUnreadBadge = RoundedPanel(_home, "UnreadBadge",
            ReviewSurface, 1230f, 140f, 64f, 40f).rectTransform;
        _messagesUnread = Text(_messagesUnreadBadge, "Unread", "", 24f, Ink, 2f, 1f, 60f, 38f, true);
        _messagesUnread.alignment = TextAlignmentOptions.Center;
        _messagesUnreadBadge.gameObject.SetActive(false);
        _workspace.gameObject.SetActive(false);
        BuildFieldApps();
        BuildInbox();
        BuildDeviceApp();
        BuildTribeApp();
        BuildServicesApp();
        BuildPackApp();
        BuildGames();
        BuildDrone();
        BuildHangar();
        BuildSpaceflight();
        BuildSettings();
        BuildActionFeedback();
        BuildNotificationShade();
        BuildDesktop();
        closeButton.Image.transform.SetAsLastSibling();
        _brightnessDim = Panel(device.transform, "DisplayBrightness", new Color(0f, 0f, 0f, 0f), 24f, 24f, 1392f, 852f);
    }

    internal void Render(ClientTerminalPage page, bool resetScroll)
    {
        if (_disposed) return;
        for (var index = _ownedGraphics.Count - 1; index >= 0; index--)
            if (_ownedGraphics[index] == null || !_ownedGraphics[index])
            {
                if (_ownedGraphics[index] is TextMeshProUGUI text) _textSizes.Remove(text);
                _ownedGraphics.RemoveAt(index);
            }
        var previous = _page;
        var changedApp = previous == null || previous.App != page.App || previous.ProviderId != page.ProviderId;
        BeforeShellPageChange(page, resetScroll);
        _page = page;
        _title.text = page.Title;
        _title.gameObject.SetActive(page.App != ClientTerminalApp.Home);
        _subtitle.text = page.Subtitle;
        _subtitle.gameObject.SetActive(page.App != ClientTerminalApp.Navigation && page.App != ClientTerminalApp.Home);
        _link.text = page.Connected ? "Connected" : "Offline";
        _link.color = page.Connected ? ChromeMint : ChromeMuted;
        _linkIndicator.color = _link.color;
        var home = page.App == ClientTerminalApp.Home;
        var navigation = page.App == ClientTerminalApp.Navigation;
        var messages = page.App == ClientTerminalApp.Messages;
        var settings = page.App == ClientTerminalApp.Settings;
        var services = page.App == ClientTerminalApp.Services;
        var fieldApp = page.App is ClientTerminalApp.Journal or ClientTerminalApp.Health or ClientTerminalApp.Watch
            or ClientTerminalApp.Devices or ClientTerminalApp.Games or ClientTerminalApp.Tribe or ClientTerminalApp.Drone or ClientTerminalApp.Pack or ClientTerminalApp.Hangar or ClientTerminalApp.Spaceflight;
        _home.gameObject.SetActive(home);
        _workspace.gameObject.SetActive(!home && !navigation && !messages && !settings && !fieldApp && !services);
        SetServicesAppVisible(services);
        _navigationSurface.gameObject.SetActive(navigation);
        _messagesSurface.gameObject.SetActive(messages);
        _settingsSurface.gameObject.SetActive(settings);
        _journalSurface.gameObject.SetActive(page.App == ClientTerminalApp.Journal);
        _healthSurface.gameObject.SetActive(page.App == ClientTerminalApp.Health);
        _watchSurface.gameObject.SetActive(page.App == ClientTerminalApp.Watch);
        SetDeviceAppVisible(page.App == ClientTerminalApp.Devices);
        SetTribeAppVisible(page.App == ClientTerminalApp.Tribe);
        SetPackAppVisible(page.App == ClientTerminalApp.Pack);
        SetGamesVisible(page.App == ClientTerminalApp.Games);
        _droneSurface.gameObject.SetActive(page.App == ClientTerminalApp.Drone);
        SetHangarVisible(page.App == ClientTerminalApp.Hangar);
        SetSpaceflightVisible(page.App == ClientTerminalApp.Spaceflight);
        if (changedApp)
        {
            CloseLauncherPanels();
            DeactivateFieldInputs();
            DeactivateInboxInputs();
            DeactivateSettingsInputs();
            ResetNoteDelete();
        }
        if (changedApp) SetShadeAmount(0f);
        if (messages && changedApp)
        {
            _messagesScrollToEnd = true;
            ScrollMessagesToEnd();
        }
        if (changedApp) _detailsExpanded = false;

        // Changing form scope destroys old listeners, even if the new form uses the same field IDs.
        var changedForm = changedApp || !SameFields(page.Inputs);
        if (changedForm) RebuildFields(page);
        if (changedApp || !SameCommands(page.Commands)) RebuildCommands(page);
        RefreshFields(page);
        RefreshCommands(page);
        if (services) LayoutServices(page);
        else if (!home && !navigation && !messages && !settings && !fieldApp)
        {
            LayoutActions();
            RefreshReading(page);
        }
        if (changedForm) _actions.Reset();
        if (resetScroll || changedApp)
        {
            ResetServicesScroll();
            _actions.Reset();
            _reading.Reset();
            _review.Reset();
        }
        RefreshNotificationShade();
        RefreshDesktopStatus();
        if (changedApp && previous != null) RevealPage();
        AfterShellPageRender(page, resetScroll);
        RefreshButtonVisuals();
    }

    internal void SetMessages(IReadOnlyList<string> lines, string draft, string status, bool canSend, int unread)
    {
        if (_disposed) return;
        _messageSnapshotVersion++;
        if (!_inboxVisible)
        {
            var viewportHeight = _messageHistory.Root.rectTransform.rect.height;
            var oldPosition = _messageHistory.Content.anchoredPosition.y;
            var oldBottom = Math.Max(0f, _messageHistory.Content.sizeDelta.y - viewportHeight);
            var follow = _messagesScrollToEnd || oldPosition >= oldBottom - 8f;
            var history = lines.Count == 0 ? "No messages yet." : string.Join("\n\n", lines);
            if (_messageLog.text != history)
            {
                _messageLog.text = history;
                var y = 18f;
                PlaceText(_messageLog, 24f, ref y, WorkspaceWidth - 96f, 18f);
                _messageHistory.Height(y);
                if (!follow)
                    _messageHistory.Content.anchoredPosition = new Vector2(0f,
                        Math.Clamp(oldPosition, 0f, Math.Max(0f, y - viewportHeight)));
            }
            if (follow) ScrollMessagesToEnd();
        }
        _messagesScrollToEnd = false;
        _acceptedMessageDraft = draft ?? "";
        SetMessageDraft(_acceptedMessageDraft);
        _messageStatus.text = status ?? "";
        _messageSend.Button.interactable = canSend;
        _messageSend.Image.color = canSend ? AppColor(ClientTerminalApp.Messages) : Raised;
        _messageSend.Label.color = canSend ? Color.white : Dim;
        SetNotificationSummary(unread, lines.Count > 0 ? lines[lines.Count - 1] : "");
    }

    internal void SetMessengerContext(string label, string target, string status)
    {
        if (_disposed) return;
        _messengerContext.text = label ?? "";
        _messengerTarget.text = target ?? "";
        _messageStatus.text = status ?? "";
    }

    private void EditMessage(string value)
    {
        if (_disposed || _shadeCapture || _messageInputUpdating || _page?.App != ClientTerminalApp.Messages) return;
        var version = _messageSnapshotVersion;
        var result = _edit("messages", Guid.Empty, "message", value ?? "");
        if (_disposed) return;
        if (result.Succeeded)
        {
            if (version == _messageSnapshotVersion) _acceptedMessageDraft = value ?? "";
        }
        else
        {
            _messageStatus.text = string.IsNullOrWhiteSpace(result.Detail) ? result.Status : result.Detail;
            SetMessageDraft(_acceptedMessageDraft);
        }
    }

    private void SendMessage()
    {
        if (!_disposed && !_shadeCapture && _page?.App == ClientTerminalApp.Messages && _messageSend.Button.interactable)
            _command("messages", "send");
    }

    private void SetMessageDraft(string draft)
    {
        if (_messageInput.text == draft) return;
        var caret = _messageInput.caretPosition;
        _messageInputUpdating = true;
        try
        {
            _messageInput.text = draft;
            _messageInput.caretPosition = Math.Min(caret, draft.Length);
        }
        finally { _messageInputUpdating = false; }
    }

    private void ScrollMessagesToEnd()
    {
        _messageHistory.Scroll.StopMovement();
        _messageHistory.Content.anchoredPosition = new Vector2(0f, Math.Max(0f,
            _messageHistory.Content.sizeDelta.y - _messageHistory.Root.rectTransform.rect.height));
    }

    private bool SameFields(MenuInputSnapshot? snapshot)
    {
        if (_scope != snapshot?.Scope || _fields.Count != (snapshot?.Fields.Count ?? 0)) return false;
        for (var index = 0; index < _fields.Count; index++)
            if (_fields[index].Id != snapshot!.Fields[index].Id) return false;
        return true;
    }

    private bool SameCommands(IReadOnlyList<CommandSnapshot> snapshots)
    {
        if (_commands.Count != snapshots.Count) return false;
        for (var index = 0; index < snapshots.Count; index++)
            if (_commands[index].Id != snapshots[index].Id) return false;
        return true;
    }

    private void RebuildFields(ClientTerminalPage page)
    {
        DestroyChildren(_fieldsRoot);
        _fields.Clear();
        _scope = page.Inputs?.Scope;
        if (page.Inputs == null) return;
        var providerId = page.ProviderId;
        var scope = page.Inputs.Scope;
        foreach (var field in page.Inputs.Fields)
        {
            var widget = new FieldWidget
            {
                Id = field.Id,
                Input = Input(_fieldsRoot, field.Value, field.MaximumLength),
                Label = Text(_fieldsRoot, "FieldLabel", field.DisplayName, 25f, Ink, 24f, 0f, FormWidth, 40f, true),
                Description = Text(_fieldsRoot, "FieldHelp", field.Description, 22f, Muted, 24f, 0f, FormWidth, 36f),
                Error = Text(_fieldsRoot, "FieldError", "", 23f, Amber, 24f, 0f, FormWidth, 36f),
                AcceptedValue = field.Value
            };
            _fields.Add(widget);
            widget.Input.onValueChanged.AddListener((UnityAction<string>)new Action<string>(value =>
                EditField(providerId, scope, widget, value ?? "")));
        }
    }

    private void EditField(string providerId, Guid scope, FieldWidget widget, string value)
    {
        if (_disposed || _shadeCapture || widget.Updating || _page?.ProviderId != providerId || _scope != scope || !widget.Input.interactable) return;
        var result = _edit(providerId, scope, widget.Id, value);
        // The callback may itself refresh the screen and retire this form.
        if (_disposed || _page?.ProviderId != providerId || _scope != scope || !_fields.Contains(widget)) return;
        if (result.Succeeded)
        {
            widget.AcceptedValue = value;
            widget.Error.text = "";
        }
        else
        {
            widget.Error.text = string.IsNullOrWhiteSpace(result.Detail) ? result.Status : result.Detail;
            SetInputValue(widget, widget.AcceptedValue);
        }
        LayoutActions();
    }

    private void RefreshFields(ClientTerminalPage page)
    {
        if (page.Inputs == null) return;
        for (var index = 0; index < _fields.Count; index++)
        {
            var widget = _fields[index];
            var field = page.Inputs.Fields[index];
            widget.Label.text = field.DisplayName;
            widget.Description.text = field.Description;
            widget.Input.characterLimit = field.MaximumLength;
            widget.Input.interactable = field.Enabled;
            widget.Label.color = field.Enabled ? Ink : Muted;
            widget.AcceptedValue = field.Value;
            if (!widget.Input.isFocused && widget.Input.text != field.Value) SetInputValue(widget, field.Value);
        }
    }

    private static void SetInputValue(FieldWidget widget, string value)
    {
        var caret = widget.Input.caretPosition;
        widget.Updating = true;
        try
        {
            widget.Input.text = value;
            widget.Input.caretPosition = Math.Min(caret, value.Length);
        }
        finally { widget.Updating = false; }
    }

    private void RebuildCommands(ClientTerminalPage page)
    {
        foreach (var widget in _commands)
        {
            _ownedGraphics.Remove(widget.Control.Image);
            _ownedGraphics.Remove(widget.Control.Label);
            _ownedGraphics.Remove(widget.Description);
            _textSizes.Remove(widget.Control.Label);
            _textSizes.Remove(widget.Description);
            widget.Control.Image.gameObject.SetActive(false);
            widget.Description.gameObject.SetActive(false);
            UnityEngine.Object.Destroy(widget.Control.Image.gameObject);
            UnityEngine.Object.Destroy(widget.Description.gameObject);
        }
        _commands.Clear();
        var providerId = page.ProviderId;
        foreach (var command in page.Commands)
        {
            var commandId = command.Id;
            var action = CommandAction(commandId);
            var pinned = action is "read" or "refresh" or "review" or "cancel"
                || action.StartsWith("confirm.", StringComparison.Ordinal);
            var control = Button(pinned ? _execution : _commandsRoot, "Command", command.DisplayName, 24f, 0f, FormWidth, 58f, () =>
            {
                if (_disposed || _shadeCapture || _page?.ProviderId != providerId) return;
                foreach (var current in _page.Commands)
                    if (current.Id == commandId && current.Enabled)
                    { _command(providerId, commandId); return; }
            });
            control.Label.overflowMode = TextOverflowModes.Overflow;
            _commands.Add(new CommandWidget
            {
                Id = commandId,
                Action = action,
                Pinned = pinned,
                Control = control,
                Description = Text(_commandsRoot, "CommandDescription", "", 22f, Muted, 28f, 0f, FormWidth - 8f, 36f)
            });
        }
    }

    private void RefreshCommands(ClientTerminalPage page)
    {
        for (var index = 0; index < _commands.Count; index++)
        {
            var widget = _commands[index];
            var command = page.Commands[index];
            widget.Control.Button.interactable = command.Enabled;
            widget.Control.Label.text = command.DisplayName;
            var confirmation = command.RequiresConfirmation || widget.Action.StartsWith("confirm.", StringComparison.Ordinal);
            var primary = command.Enabled && (widget.Action is "read" or "review");
            widget.Control.Label.color = !command.Enabled ? Dim : confirmation ? Amber : primary ? Color.white : Ink;
            widget.Control.Image.color = confirmation ? ReviewSurface : primary ? AppColor(page.App) : Raised;
            widget.Description.text = widget.Action.StartsWith("select.", StringComparison.Ordinal) ? ""
                : command.Status == command.Description || command.Status == command.DisplayName
                    ? command.Description : Join(command.Description, command.Status);
            widget.Description.gameObject.SetActive(!widget.Pinned);
        }
        _emptyActions.gameObject.SetActive(_commands.Count == 0);
    }

    private void LayoutActions()
    {
        if (_page?.App == ClientTerminalApp.Services)
        {
            LayoutServices(_page);
            return;
        }
        var y = 26f;
        foreach (var widget in _fields)
        {
            PlaceText(widget.Label, 24f, ref y, FormWidth, 7f);
            if (!string.IsNullOrWhiteSpace(widget.Description.text)) PlaceText(widget.Description, 24f, ref y, FormWidth, 12f);
            else Place(widget.Description.rectTransform, 24f, y, FormWidth, 0f);
            Place(widget.Input.GetComponent<RectTransform>(), 24f, y, FormWidth, 58f);
            y += 68f;
            if (!string.IsNullOrWhiteSpace(widget.Error.text)) PlaceText(widget.Error, 24f, ref y, FormWidth, 8f);
            else Place(widget.Error.rectTransform, 24f, y, FormWidth, 0f);
            y += 20f;
        }
        Place(_fieldsRoot, 0f, 0f, _actions.Width, y);
        Place(_commandsRoot, 0f, y, _actions.Width, 1f);
        var commandsY = 0f;
        PlaceText(_actionHeading, 24f, ref commandsY, FormWidth, 14f);
        foreach (var widget in _commands)
        {
            if (widget.Pinned) continue;
            var height = Math.Max(58f, TextHeight(widget.Control.Label, FormWidth - 36f) + 22f);
            Place(widget.Control.Image.rectTransform, 24f, commandsY, FormWidth, height);
            Place(widget.Control.Label.rectTransform, 18f, 0f, FormWidth - 36f, height);
            commandsY += height + 10f;
            if (!string.IsNullOrEmpty(widget.Description.text)) PlaceText(widget.Description, 28f, ref commandsY, FormWidth - 8f, 0f);
            else Place(widget.Description.rectTransform, 28f, commandsY, FormWidth - 8f, 0f);
            commandsY += 24f;
        }
        if (_commands.Count == 0) PlaceText(_emptyActions, 24f, ref commandsY, FormWidth, 16f);
        _commandsRoot.sizeDelta = new Vector2(_actions.Width, commandsY);
        _actions.Height(y + commandsY + 12f);
    }

    private void RefreshReading(ClientTerminalPage page)
    {
        _selection.text = string.IsNullOrWhiteSpace(page.Selection) ? page.Title : page.Selection;
        var executionHeight = LayoutExecution();
        var hasReview = !string.IsNullOrWhiteSpace(page.Review);
        foreach (var command in page.Commands)
            hasReview |= command.Enabled && command.RequiresConfirmation;
        _review.Root.gameObject.SetActive(hasReview);
        _reviewText.text = string.IsNullOrWhiteSpace(page.Review) ? "Read the action and its effects before confirming." : page.Review;
        var reviewY = 24f;
        PlaceText(_reviewHeading, 26f, ref reviewY, ReadingTextWidth, 10f);
        PlaceText(_reviewText, 26f, ref reviewY, ReadingTextWidth, 24f);
        _review.Height(reviewY);
        var reviewHeight = Math.Clamp(reviewY, 164f, 206f);
        var reviewTop = executionHeight + 18f;
        Place(_review.Root.rectTransform, ReadingX, reviewTop, ReadingWidth, reviewHeight);
        var readingTop = hasReview ? reviewTop + reviewHeight + 18f : reviewTop;
        Place(_reading.Root.rectTransform, ReadingX, readingTop, ReadingWidth, WorkspaceHeight - readingTop);
        _status.text = page.Status;
        _notes.text = NotesText(page.Notes);
        _reply.text = string.IsNullOrWhiteSpace(page.Reply) ? "No response yet. Choose an available action." : page.Reply;
        _details.text = NotesText(page.Details);
        var y = 26f;
        PlaceOptional(_status, ref y, 10f);
        var hasNotes = !string.IsNullOrWhiteSpace(_notes.text);
        _notesHeading.gameObject.SetActive(hasNotes);
        _notes.gameObject.SetActive(hasNotes);
        if (hasNotes)
        {
            PlaceText(_notesHeading, 26f, ref y, ReadingTextWidth, 10f);
            PlaceText(_notes, 26f, ref y, ReadingTextWidth, 28f);
        }
        PlaceText(_replyHeading, 26f, ref y, ReadingTextWidth, 10f);
        PlaceText(_reply, 26f, ref y, ReadingTextWidth, 28f);
        var hasDetails = page.Details.Count > 0;
        _detailsToggle.Image.gameObject.SetActive(hasDetails);
        _details.gameObject.SetActive(hasDetails && _detailsExpanded);
        if (hasDetails)
        {
            _detailsToggle.Label.text = _detailsExpanded ? "-  Hide connection & app details" : "+  Connection & app details";
            Place(_detailsToggle.Image.rectTransform, 26f, y, ReadingTextWidth, 50f);
            y += 64f;
            if (_detailsExpanded) PlaceText(_details, 26f, ref y, ReadingTextWidth, 24f);
        }
        _reading.Height(y + 4f);
    }

    private float LayoutExecution()
    {
        var index = 0;
        const float buttonWidth = (ReadingTextWidth - 14f) / 2f;
        foreach (var widget in _commands)
        {
            if (!widget.Pinned) continue;
            Place(widget.Control.Image.rectTransform, 26f + (index % 2) * (buttonWidth + 14f),
                62f + (index / 2) * 64f, buttonWidth, 54f);
            Place(widget.Control.Label.rectTransform, 16f, 0f, buttonWidth - 32f, 54f);
            widget.Control.Label.fontSize = 22f;
            widget.Control.Label.overflowMode = TextOverflowModes.Ellipsis;
            index++;
        }
        var height = 66f + ((index + 1) / 2) * 64f;
        Place(_execution, ReadingX, 0f, ReadingWidth, height);
        return height;
    }

    // Provider command IDs include a form scope. This only chooses presentation; dispatch
    // still checks the complete current ID and Enabled flag immediately before invocation.
    private static string CommandAction(string commandId)
    {
        var prefix = commandId.IndexOf('.');
        var scope = prefix < 0 ? -1 : commandId.IndexOf('.', prefix + 1);
        return scope < 0 ? commandId : commandId[(scope + 1)..];
    }

    private void ToggleDetails()
    {
        if (_disposed || _page == null) return;
        _detailsExpanded = !_detailsExpanded;
        if (_page.App == ClientTerminalApp.Services) LayoutServices(_page);
        else RefreshReading(_page);
    }

    private static void PlaceText(TextMeshProUGUI text, float x, ref float y, float width, float spacing)
    {
        var height = TextHeight(text, width);
        Place(text.rectTransform, x, y, width, height);
        y += height + spacing;
    }

    private static void PlaceOptional(TextMeshProUGUI text, ref float y, float spacing)
    {
        var visible = !string.IsNullOrWhiteSpace(text.text);
        text.gameObject.SetActive(visible);
        if (visible) PlaceText(text, 26f, ref y, ReadingTextWidth, spacing);
    }

    private static string Join(string first, string second) => string.IsNullOrWhiteSpace(first) ? second
        : string.IsNullOrWhiteSpace(second) ? first : first + "\n" + second;

    private static string NotesText(IReadOnlyList<ClientTerminalNote> notes)
    {
        var text = new StringBuilder();
        foreach (var note in notes)
        {
            if (text.Length > 0) text.Append("\n\n");
            if (!string.IsNullOrWhiteSpace(note.Label)) text.Append(note.Label).Append('\n');
            text.Append(note.Text);
        }
        return text.ToString();
    }

    public void Dispose()
    {
        if (_disposed) return;
        // Invalidate callbacks before touching native objects: the tablet parent
        // may already be gone while its separate fullscreen controls are alive.
        _disposed = true;
        TouchFeedback = null;
        _page = null;
        _fields.Clear();
        _commands.Clear();
        _ownedGraphics.Clear();
        try { DisposeImmersive(); }
        finally
        {
            try { ReleaseGameSprites(); }
            finally
            {
                try
                {
                    if (_device != null && _device)
                    {
                        _device.SetActive(false);
                        UnityEngine.Object.Destroy(_device);
                    }
                }
                finally { DisposeWidgetResources(); }
            }
        }
    }
}
