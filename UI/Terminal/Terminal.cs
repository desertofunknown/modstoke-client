// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using GameState = SonsSdk.GameState;
using System.Globalization;
using System.Linq;
using BepInEx.Logging;
using Modstoke.Loader;
using Modstoke.Sons;
using SonsSdk;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Modstoke.Client;

internal sealed partial class ClientTerminal
{
    private const string KeybindId = "modstoke.client.terminal";
    private const string PanelId = "modstoke.client.field-terminal";
    private static readonly string ClientVersion = typeof(Plugin).Assembly.GetName().Version?.ToString(3) ?? "unknown";
    private static readonly ManualLogSource NoticeLog = BepInEx.Logging.Logger.CreateLogSource("Modstoke Client tablet");
    private readonly ClientHeldTablet _tablet = new();
    private readonly Func<bool> _canEquipTablet;
    private readonly ClientGpsInput _gpsInput;
    private readonly ClientTabletOperatingSystem _os = new();
    private readonly ClientTabletMessages _messages;
    private readonly ClientMessenger _messenger;
    private readonly ClientDeviceApp _devices;
    private readonly ClientTribeApp _tribeApp;
    private readonly ClientPackApp _pack = new();
    private readonly ClientDeviceInventory _deviceInventory;
    private ClientTabletPreferences? _preferences;
    private ClientTabletAlerts? _alerts;
    private GameObject? _canvas;
    private ClientTerminalView? _view;
    private ClientTerminalApp _app;
    private bool _registered;
    private bool _open;
    private bool _opening;
    private bool _holstering;
    private bool _failed;
    private ManualLogSource? _log;
    private bool _equipmentRecoveryFailed;
    private ClientDroneView? _lastDroneInventoryState;
    private int _scene;
    private Guid? _session;
    private float _nextRefresh;
    private string _feedback = "";
    private float _feedbackUntil;
    private bool _nativeGpsRequested;
    private bool _messagesRequested;
    private int _inventoryRevision;
    private long _osGeneration;
    private long _osRevision;
    private bool? _satelliteContext;
    private long _watchCompletionId;
    private long _reminderCompletionId;
    private bool _contentObscured;
    private string _messageDraft = "";
    private string _messageStatus = "";
    private string _conversationChannel = "";
    private string _conversationTarget = "";
    internal ClientTabletPreferencesSnapshot? AudioPreferences => _registered && !_failed ? _preferences?.Snapshot : null;

    internal ClientTerminal(Func<bool> canEquipTablet)
    {
        _canEquipTablet = canEquipTablet;
        _gpsInput = new ClientGpsInput(AcceptGpsInput);
        _messages = new ClientTabletMessages(AcceptMessagesInput, ReceiveMessage, () => _os.ClearMessages("legacy"));
        _messenger = new ClientMessenger(ReceiveMessengerMessage);
        _wristWatch = new ClientWristWatch(WatchCommand, _os.SetWristWatchAwake, Notice, AcquireDeviceInput);
        _deviceInventory = new ClientDeviceInventory(AcceptInventoryTablet, AcceptInventoryDrone, AcceptInventoryController);
        _devices = new ClientDeviceApp((title, detail) => PostField(new("notify", title, detail, App: ClientTerminalApp.Devices)));
        _tribeApp = new ClientTribeApp((title, detail) => PostField(new("notify", title, detail, App: ClientTerminalApp.Tribe)));
    }

    internal void Start()
    {
        if (GameRuntime.Environment.IsDedicatedServer)
            return;
        _registered = InputBindings.TryRegisterKeybind(KeybindId, "Field tablet", "Modstoke Client",
            KeyCode.F4, InputContexts.Any, false, "Take out or put away the Modstoke field tablet.");
        if (!_registered)
            throw new InvalidOperationException("The Modstoke Client terminal key is already registered.");
        _log = BepInEx.Logging.Logger.CreateLogSource("Modstoke Client terminal");
        _failed = false;
        _deviceInput.Start();
        _preferences = new ClientTabletPreferences(GameRuntime.Environment.GameRootDirectory);
        _alerts = new ClientTabletAlerts();
        _os.Start(GameRuntime.Environment.GameRootDirectory, _preferences);
        _wristWatch.Start();
        _deviceInventory.Start();
        ResetOsSession();
        _messenger.Start();
        _devices.Start();
        _tribeApp.Start();
        _messages.Start();
        try { _gpsInput.Start(); }
        catch (Exception error)
        {
            Debug.LogWarning("Modstoke Client: GPS action replacement is unavailable; use F4 for the tablet: " + error);
        }
    }

    private bool AcceptGpsInput()
    {
        if (_registered && !_failed && DeviceActive) return true;
        if (!_registered || _failed || !GameRuntime.State.IsInWorld || !_deviceInventory.HasTablet)
            return false;
        if (!_open && !_opening && (GameState.IsInConsole || GameRuntime.State.PauseMenuOpen || LoaderMenuVisible()
            || IsEditingText() || !_canEquipTablet()))
            return false;
        _nativeGpsRequested = true;
        return true;
    }

    private bool AcceptMessagesInput()
    {
        if (_registered && !_failed && DeviceActive) return true;
        if (!_registered || _failed || !GameRuntime.State.IsInWorld || !_os.Snapshot.Ready || !_deviceInventory.HasTablet)
            return false;
        if (!_open && !_opening && (GameState.IsInConsole || GameRuntime.State.PauseMenuOpen || LoaderMenuVisible()
            || IsEditingText() || !_canEquipTablet() || !_tablet.CanBegin()))
            return false;
        _messagesRequested = true;
        return true;
    }

    private void ResetOsSession()
    {
        ReleaseArcadeKeyboard();
        SetArcadeNavigation(false);
        _messenger.ResetForWorld();
        ResetFieldSession();
        _os.ResetSession(++_osGeneration);
        _pack.ResetSession(_osGeneration);
        ResetEnvironment();
        _view?.ResetShellSession(_osGeneration);
        _satelliteContext = null;
        _messageDraft = "";
        _messageStatus = "";
        _conversationChannel = _conversationTarget = "";
        _feedback = "";
        _feedbackUntil = 0;
        _watchCompletionId = 0;
        _reminderCompletionId = 0;
        _contentObscured = false;
        _previousDroneAudio = null;
        _nextRefresh = 0;
    }

    private void ReceiveMessage(string line)
    {
        if (!_os.ReceiveMessage(new ClientTabletMessage("legacy", "legacy", "", "", line)))
            _messageStatus = "The inbox could not retain a message.";
        else AlertForMessage("legacy", "legacy");
    }

    private void ReceiveMessengerMessage(MessengerMessage message)
    {
        RefreshOsSession();
        if (!_os.ReceiveMessage(new ClientTabletMessage(message.Channel, message.ConversationId,
            message.SenderId, message.SenderName, message.Text)))
            _messageStatus = "The inbox could not retain a message.";
        else AlertForMessage(message.Channel, message.ConversationId);
    }

    private void AlertForMessage(string channel, string conversation)
    {
        var os = _os.Snapshot;
        if (_preferences != null && (!_open || os.Locked || os.TaskSwitcherOpen || _view?.ContentObscured == true
            || _app != ClientTerminalApp.Messages
            || _messenger.Channel != channel || (channel == "direct" && _messenger.Target != conversation)))
            _alerts?.Play(_preferences.Snapshot, channel);
    }

    private void RefreshOsSession()
    {
        var session = CurrentSession();
        var inventoryRevision = _deviceInventory.SessionRevision;
        if (_inventoryRevision == inventoryRevision && _session == session) return;
        ResetOsSession();
        _inventoryRevision = inventoryRevision;
        _session = session;
    }

    internal void Update()
    {
        if (!_registered)
            return;
        try
        {
            UpdateFrame();
            if (!_failed) UpdateDroneInput();
            if (!_failed) UpdateAircraftDisplay();
            if (!_failed) UpdateSpacecraftDisplay();
        }
        catch (Exception error)
        {
            RetireFailedScreen(error);
        }
        finally { SyncDeviceInput(); }
    }

    private void UpdateFrame()
    {
        var nativeGps = _nativeGpsRequested;
        _nativeGpsRequested = false;
        var messagesRequested = _messagesRequested;
        _messagesRequested = false;
        var shortcutPressed = InputBindings.IsPressed(KeybindId);
        var pressed = shortcutPressed || nativeGps;
        if (_failed)
        {
            // Native equipment restoration can take several frames. Keep its
            // owner alive after retiring the screen, without replaying an app action.
            if (!_equipmentRecoveryFailed)
            {
                try
                {
                    _tablet.Update();
                    _wristWatch.UpdateRelease(_tablet.IsActive);
                }
                catch (Exception error)
                {
                    _equipmentRecoveryFailed = true;
                    _log?.LogError("Equipment restoration after a tablet error failed: " + error);
                }
            }
            if (_equipmentRecoveryFailed || _tablet.IsActive || _wristWatch.IsReleasing)
            {
                if (pressed)
                    Notice(_equipmentRecoveryFailed ? "The tablet could not restore its equipment. Restart the game; details are in BepInEx/LogOutput.log."
                        : "Wait for your equipment to finish restoring, then press F4 again.");
                return;
            }
            if (!_os.OpenFromShortcut(ClientTerminalApp.Home))
            {
                if (pressed) Notice("The tablet's home screen is not ready. Try F4 again in a moment.");
                return;
            }
            // Resume ordinary device updates once restoration settles, including
            // F3 watch input. Reopening the tablet still requires an explicit action.
            _failed = false;
        }
        var state = GameRuntime.State;
        var session = CurrentSession();
        // BeforeRender can release the held presentation between updates. Its
        // screen is a child of that mount, while fullscreen has a separate root.
        // Settle that lifetime before any app or pointer touches the old screen.
        _tablet.Update();
        if (_holstering)
        {
            if (_tablet.HolsterComplete || !_tablet.IsHolstering)
                CloseImmediately(true);
            return;
        }
        if (_open && !_tablet.IsOpen)
        {
            CloseImmediately(true);
            return;
        }
        if (_open && (_view?.IsAlive != true || _canvas == null || !_canvas))
        {
            RetireFailedScreen(new InvalidOperationException("The native tablet screen was released while its app was open."));
            return;
        }
        var kit = Plugin.Drones.Snapshot;
        var previousKit = _lastDroneInventoryState;
        if (previousKit == null || previousKit.Ready != kit.Ready || previousKit.InventoryDrone != kit.InventoryDrone
            || previousKit.Deployed != kit.Deployed || previousKit.SparePacks != kit.SparePacks
            || previousKit.Status != kit.Status)
        {
            _lastDroneInventoryState = kit;
            var droneAvailability = $"ready={kit.Ready}; inventory={kit.InventoryDrone}; deployed={kit.Deployed}; packs={kit.SparePacks}";
            droneAvailability += "; " + kit.Status;
            _log?.LogInfo("Drone inventory: " + droneAvailability);
        }
        _deviceInventory.SetDroneKit(kit.Ready, kit.InventoryDrone, kit.SparePacks);
        _deviceInventory.Update();
        RefreshOsSession();
        var os = _os.Snapshot;
        if (os.Generation == _osGeneration && os.Revision != _osRevision)
        {
            _osRevision = os.Revision;
            _app = os.ActiveApp;
            _nextRefresh = 0;
        }
        SynchronizeTabletEventSystem();
        _pack.ObserveOpenMat(_pack.Snapshot.RememberMat && IsNativeCraftingMatVisible(), _deviceInventory.HasLiveInventory);
        ObserveDroneAudio(kit);
        if (shortcutPressed && !_open && !_opening && !_holstering && !_tablet.IsActive)
        {
            // The inventory view can suppress an InWorld key before it is
            // polled. Both entry points wait for the same native transition.
            QueueTabletShortcut();
            pressed = nativeGps;
        }
        pressed |= ConsumeInventoryTablet(out var controllerRequested);
        if ((nativeGps || messagesRequested) && !_open && !_opening)
        {
            controllerRequested = false;
            ClearInventoryPresentation();
        }
        if (_satelliteContext != state.IsInWorld && _os.SetSatelliteContext(state.IsInWorld))
            _satelliteContext = state.IsInWorld;
        SyncContentVisibility();
        _messenger.Update();
        _devices.SetVisible(CanUseDesktop() && !_contentObscured && _app == ClientTerminalApp.Devices);
        _devices.Update();
        _tribeApp.SetVisible(CanUseDesktop() && !_contentObscured && _app == ClientTerminalApp.Tribe);
        _tribeApp.Update();
        _pack.SetVisible(CanUseDesktop() && !_contentObscured && _app == ClientTerminalApp.Pack);
        _pack.Update(_deviceInventory.HasLiveInventory);
        UpdateField();
        UpdateEnvironment();
        _messages.Update(state.IsInWorld && _deviceInventory.HasTablet && _os.Snapshot.Ready && _canEquipTablet()
            && (_open || _opening || _tablet.CanBegin()));
        if ((_conversationChannel != _messenger.Channel || _conversationTarget != _messenger.Target)
            && _os.SelectConversation(_messenger.Channel, _messenger.Target))
        {
            _conversationChannel = _messenger.Channel;
            _conversationTarget = _messenger.Target;
            RestoreMessageDraft();
        }
        if (os.Ready && os.Generation == _osGeneration
            && (os.Watch.CompletionId > _watchCompletionId || os.Reminders.CompletionId > _reminderCompletionId))
        {
            _watchCompletionId = os.Watch.CompletionId;
            _reminderCompletionId = os.Reminders.CompletionId;
            if (state.IsInWorld && _preferences != null) _alerts?.Play(_preferences.Snapshot);
        }
        if (os.Visible != _open) _os.SetVisible(_open);
        var fullscreenEscape = SyncFullscreen();
        if (_tablet.IsActive && (!state.IsInWorld || _scene != state.SceneEventCount))
        {
            Close(false);
            return;
        }
        if (_tablet.IsActive && (!_canEquipTablet() || !HasPresentationDevice(_tablet.IsController)))
        {
            Close(false);
            return;
        }
        var modMenuVisible = LoaderMenuVisible();
        var escape = !fullscreenEscape && Input.GetKeyDown(KeyCode.Escape);
        if (_open && escape && !state.PauseMenuOpen && !modMenuVisible
            && _view?.TryHandleShellBack() == true)
            escape = false;
        if ((_open || _opening) && (state.PauseMenuOpen || modMenuVisible || escape))
        {
            Close();
            return;
        }
        if (_opening)
        {
            if (_tablet.IsOpen)
                Open();
            else if (!_tablet.IsDrawing)
            {
                _opening = false;
                ClearInventoryPresentation();
                Notice(_tablet.Status);
            }
        }
        if (messagesRequested && (_open || _opening))
        {
            OpenShortcut(ClientTerminalApp.Messages);
            return;
        }
        if (_tabletAfterWatch && !_wristWatch.IsReleasing)
        {
            _tabletAfterWatch = false;
            pressed = true;
            controllerRequested = _controllerAfterWatch;
            messagesRequested |= _watchMessageShortcut;
            nativeGps |= _watchGpsShortcut;
            _controllerAfterWatch = _watchMessageShortcut = _watchGpsShortcut = false;
        }
        if (pressed || messagesRequested)
        {
            if (_open || _opening)
                Close();
            else if (state.IsInWorld)
            {
                if (GameState.IsInConsole || state.PauseMenuOpen || LoaderMenuVisible())
                    Notice("Close the pause or Mods menu before taking out the tablet.");
                else if (IsEditingText())
                    Notice("Finish typing and close chat before taking out the tablet.");
                else if (!_canEquipTablet())
                    Notice("The tablet is unavailable while managed combat owns your equipment.");
                else if (!HasPresentationDevice(controllerRequested))
                    Notice(_deviceInventory.Status.Length > 0 ? _deviceInventory.Status
                        : controllerRequested ? "The drone controller is not in your inventory." : "The field tablet is not in your inventory.");
                else if (!os.Ready)
                    Notice(string.IsNullOrEmpty(os.Problem) ? "Modstoke OS is starting." : "Modstoke OS is unavailable: " + os.Problem);
                else if (_wristWatch.IsInspecting || _wristWatch.IsReleasing)
                {
                    _tabletAfterWatch = true;
                    _controllerAfterWatch = controllerRequested;
                    _watchMessageShortcut = messagesRequested;
                    _watchGpsShortcut = nativeGps;
                    _wristWatch.EndInspection(immediate: true);
                }
                else if (BeginTablet(controllerRequested))
                {
                    if (messagesRequested || nativeGps)
                        OpenShortcut(messagesRequested ? ClientTerminalApp.Messages : ClientTerminalApp.Navigation);
                    _scene = state.SceneEventCount;
                    _session = session;
                    _opening = true;
                }
                else
                    Notice(_tablet.Status);
            }
            if (!_opening && !_tabletAfterWatch) ClearInventoryPresentation();
            return;
        }
        if (_open && Time.unscaledTime >= _nextRefresh)
            Refresh(false);
        if (_open && _canvas != null && _canvas)
        {
            if (SuppressTabletPointer() || !Application.isFocused)
            {
                _view?.CancelPointer();
                _tablet.UpdateScreenContact(null, false, false, true);
                _tablet.SetNavigationVisible(false);
                return;
            }
            Vector2? normalized = null;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(_canvas.GetComponent<RectTransform>(),
                Input.mousePosition, _tablet.ScreenCamera, out var pointer))
                normalized = new Vector2(pointer.x / 1440 + 0.5f, pointer.y / 900 + 0.5f);
            var down = Input.GetMouseButtonDown(0);
            var held = Input.GetMouseButton(0);
            var up = Input.GetMouseButtonUp(0);
            _view?.UpdatePointer(normalized, down, held, up);
            SyncContentVisibility();
            _tablet.UpdateScreenContact(normalized, down, held, up);
            if ((down || held) && !Fullscreen && _tablet.IsOpen && normalized is { x: >= 0 and <= 1, y: >= 0 and <= 1 })
            {
                Plugin.TabletPresence.SetLocalState(TabletPresencePhase, WatchPresencePhase, ControllerPresentation);
                Plugin.TabletPresence.NotifyInteraction();
            }
            _tablet.SetNavigationVisible(_view?.NavigationVisible == true);
            _tablet.UpdateNavigationInput();
        }
    }

    private void RetireFailedScreen(Exception error)
    {
        // Inventory ownership and the OS outlive a screen. Disposing them here
        // removed the tablet and watch after an unrelated rendering failure.
        _log?.LogError("Tablet screen failed while opening or updating " + _app + ": " + error);
        _failed = true;
        _equipmentRecoveryFailed = false;
        _inventoryDroneRequested = false;
        try { CloseImmediately(true); }
        catch (Exception cleanupError) { _log?.LogError("Tablet screen cleanup: " + cleanupError); }
        try { _wristWatch.EndInspection(immediate: true); }
        catch (Exception cleanupError) { _log?.LogError("Watch release after tablet error: " + cleanupError); }
        try
        {
            Plugin.Drones.SetViewVisible(false);
            Plugin.Drones.SetPilot(false, DronePilotInput.Neutral);
        }
        catch (Exception cleanupError) { _log?.LogError("Drone controls release: " + cleanupError); }
        Notice("The tablet screen closed after an error. Your devices remain in inventory. Press F4 to reopen Home.");
    }

    private void Open()
    {
        Create();
        var os = _os.Snapshot;
        _app = os.Generation == _osGeneration ? os.ActiveApp : ClientTerminalApp.Home;
        _os.SetVisible(true);
        _feedback = "";
        Refresh(true);
        _opening = false;
        _open = true;
        SyncContentVisibility();
        _canvas!.SetActive(true);
        SynchronizeTabletEventSystem();
        if (_inventoryDroneRequested)
        {
            _inventoryDroneRequested = false;
            OpenShortcut(ClientTerminalApp.Drone);
        }
    }

    private void Create()
    {
        _sharedScreenCaptureFailed = false;
        // Configure the screen and its private input components while inactive.
        _canvas = new GameObject("Modstoke Tablet Screen");
        _canvas.SetActive(false);
        _canvas.layer = _tablet.Layer;
        _canvas.transform.SetParent(_tablet.ScreenParent, false);
        var canvas = _canvas.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = _tablet.ScreenCamera;
        canvas.sortingLayerID = _tablet.SortingLayer;
        canvas.sortingOrder = _tablet.SortingOrder;
        var rect = _canvas.GetComponent<RectTransform>();
        rect.sizeDelta = new Vector2(1440, 900);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.localScale = Vector3.one * _tablet.PixelScale;
        var scaler = _canvas.AddComponent<CanvasScaler>();
        scaler.dynamicPixelsPerUnit = 1;
        var raycaster = _canvas.AddComponent<GraphicRaycaster>();
        raycaster.blockingObjects = GraphicRaycaster.BlockingObjects.None;
        var panel = new GameObject(PanelId);
        panel.layer = _tablet.Layer;
        var panelRect = panel.AddComponent<RectTransform>();
        panelRect.SetParent(_canvas.transform, false);
        panelRect.anchorMin = Vector2.zero;
        panelRect.anchorMax = Vector2.one;
        panelRect.offsetMin = Vector2.zero;
        panelRect.offsetMax = Vector2.zero;
        CreateTabletEventSystem();
        _view = new ClientTerminalView(panelRect, Select, Invoke, Edit, Close) { TouchFeedback = PlayTouchFeedback };
        if (_view.NavigationMount != null)
            _tablet.AttachNavigation(_view.NavigationMount);
        _view.SetNavigationAvailable(_tablet.NavigationAvailable);
    }

    internal void Close() => Close(true);

    private void Close(bool restoreEquipment)
    {
        _view?.CancelPointer();
        ExitFullscreen();
        if (restoreEquipment && !_failed && _open && !_holstering && _tablet.BeginHolster())
        {
            _holstering = true;
            _open = false;
            ReleaseTabletEventSystem();
            _opening = false;
            _nativeGpsRequested = _messagesRequested = false;
            _os.SetVisible(false);
            SyncContentVisibility();
            if (_canvas != null && _canvas)
            {
                var input = _canvas.GetComponent<CanvasGroup>() ?? _canvas.AddComponent<CanvasGroup>();
                input.interactable = false;
                input.blocksRaycasts = false;
            }
            return;
        }
        CloseImmediately(restoreEquipment);
    }

    private void CloseImmediately(bool restoreEquipment)
    {
        // Retire logical state first. A failed native screen must not be asked
        // to animate pointer/shade resets before equipment and input can release.
        ClearInventoryPresentation();
        _holstering = false;
        _open = false;
        _opening = false;
        _nativeGpsRequested = false;
        _messagesRequested = false;
        _os.SetVisible(false);
        SyncContentVisibility();
        _feedback = "";
        try
        {
            ReleaseArcadeKeyboard();
            SetArcadeNavigation(false);
            ReleaseTabletEventSystem();
            ExitFullscreen();
        }
        finally
        {
            try { DestroyScreen(); }
            finally
            {
                try { _tablet.Close(restoreEquipment); }
                finally
                {
                    // Normal holstering retains focus through native equipment
                    // restoration; a retired screen releases it immediately.
                    if (_failed || !_tablet.IsActive) _deviceInput.Release();
                }
            }
        }
    }

    internal void Stop()
    {
        try
        {
            CloseImmediately(true);
        }
        finally
        {
            try { _tablet.Dispose(); }
            finally
            {
                _deviceInput.Dispose();
                if (_registered)
                    InputBindings.TryUnregisterKeybind(KeybindId);
                _registered = false;
                if (_log != null)
                {
                    BepInEx.Logging.Logger.Sources.Remove(_log);
                    _log.Dispose();
                    _log = null;
                }
                try { _gpsInput.Dispose(); }
                finally
                {
                    try { _messages.Dispose(); }
                    finally
                    {
                        try { _messenger.Dispose(); }
                        finally
                        {
                            try { _pack.Dispose(); ResetEnvironment(); _tribeApp.Dispose(); _devices.Dispose(); _watchHud.Dispose(); _wristWatch.Dispose(); }
                            finally { try { _deviceInventory.Dispose(); } finally { try { _alerts?.Dispose(); } finally { _os.Dispose(); } } }
                        }
                    }
                }
            }
        }
    }

    private void DestroyScreen()
    {
        ReleaseSharedScreenCapture();
        var view = _view;
        _view = null;
        try
        {
            try { ReleaseTabletEventSystem(); }
            finally
            {
                try { _tablet.DetachNavigation(); }
                finally { view?.Dispose(); }
            }
        }
        finally
        {
            try { DestroyTabletEventSystem(); }
            finally
            {
                if (_canvas != null && _canvas) UnityEngine.Object.Destroy(_canvas);
                _canvas = null;
            }
        }
    }

    private static void Notice(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;
        // Unity info lines never reach LogOutput.log; a log source does.
        NoticeLog.LogInfo(message);
        try { SonsTools.ShowMessage(message, 5); }
        catch (Exception error)
        {
            Debug.LogWarning("Modstoke Client: tablet message could not reach the HUD: " + error);
        }
    }

    private void Select(ClientTerminalApp app)
    {
        var os = _os.Snapshot;
        if (!os.Ready || os.Generation != _osGeneration || os.Locked) return;
        if (!_os.Navigate(app))
        {
            Feedback("The tablet could not open that app. Try again in a moment.");
            return;
        }
        _feedback = "";
        _nextRefresh = 0;
        // Render on Update, outside Unity's current input dispatch.
    }

    private void OpenShortcut(ClientTerminalApp app)
    {
        if (!_os.OpenFromShortcut(app))
            Feedback("The tablet could not open that app. Try again in a moment.");
        _nextRefresh = 0;
    }

    private bool CanUseDesktop()
    {
        var os = _os.Snapshot;
        return _open && os.Ready && os.Generation == _osGeneration && !os.Locked && !os.TaskSwitcherOpen;
    }

    private void SyncContentVisibility()
    {
        var obscured = _open && _view?.ContentObscured == true;
        if (obscured != _contentObscured && _os.SetContentObscured(obscured))
            _contentObscured = obscured;
    }

    private void Invoke(string provider, string commandId)
    {
        if (provider == "fullscreen") { InvokeFullscreen(commandId); return; }
        if (provider == "os" && commandId is "content-covered" or "content-visible")
        {
            SyncContentVisibility();
            return;
        }
        if (_open && provider == "os")
        {
            var os = _os.Snapshot;
            if (!os.Ready || os.Generation != _osGeneration) return;
            if (commandId.StartsWith("open:", StringComparison.Ordinal)
                && Enum.TryParse<ClientTerminalApp>(commandId[5..], out var shortcut) && Enum.IsDefined(shortcut))
            {
                OpenShortcut(shortcut);
                return;
            }
            var accepted = commandId switch
            {
                "unlock" => _os.Unlock(),
                "lock" => _os.Lock(),
                "back" => _os.Back(),
                "recents" => _os.SetTaskSwitcher(true),
                "recents-close" => _os.SetTaskSwitcher(false),
                "recents-clear" => !os.Locked && _os.ClearRecentApps(),
                "messages-read" => !os.Locked && _os.MarkAllMessagesRead(),
                "watch-pause" => !os.Locked && _os.PauseWatch(),
                "watch-resume" => !os.Locked && _os.ResumeWatch(),
                "watch-cancel" => !os.Locked && _os.CancelWatch(),
                "satellite-toggle" => !os.Locked && _os.SetSatelliteEnabled(!os.Satellite.Enabled),
                "satellite-reconnect" => !os.Locked && _os.ReconnectSatellite(),
                _ when !os.Locked && commandId.StartsWith("dismiss:", StringComparison.Ordinal)
                    && Enum.TryParse<ClientTerminalApp>(commandId[8..], out var app)
                    && app != ClientTerminalApp.Home => _os.DismissApp(app),
                _ when !os.Locked && commandId.StartsWith("watch-start:", StringComparison.Ordinal)
                    && int.TryParse(commandId[12..], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
                    => _os.StartWatch(seconds),
                _ => false
            };
            if (!accepted) Feedback("The tablet is busy. Try again in a moment.");
            _nextRefresh = 0;
            return;
        }
        if (!CanUseDesktop()) return;
        if (provider == "aircraft") { InvokeAircraft(commandId); return; }
        if (provider == "spacecraft") { InvokeSpacecraft(commandId); return; }
        if (provider == "pack" && _app == ClientTerminalApp.Pack && !_contentObscured)
        {
            if (commandId == "draft-supplies") { DraftPackSupplies(); return; }
            if (commandId.StartsWith("stow.", StringComparison.Ordinal))
            {
                var request = commandId.AsSpan(5);
                var separator = request.IndexOf('.');
                if (separator > 0
                    && int.TryParse(request[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out var slot)
                    && int.TryParse(request[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var expectedItemId))
                    StowPackItem((ItemEquipmentSlot)slot, expectedItemId);
                return;
            }
            if (commandId.StartsWith("drop.", StringComparison.Ordinal)
                && int.TryParse(commandId.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out var itemId))
            {
                DropPackItem(itemId);
                return;
            }
            _pack.HandleCommand(commandId);
            _nextRefresh = 0;
            return;
        }
        if (provider == "drone" && _app == ClientTerminalApp.Drone && !_contentObscured)
        {
            if (commandId == "navigate-recovery") { NavigateToDroneContact(); return; }
            if (commandId == "save-survey") { SaveDroneSurveyPin(); return; }
            Feedback(Plugin.Drones.Command(commandId));
            _nextRefresh = 0;
            return;
        }
        if (provider == "clock" && _app == ClientTerminalApp.Watch && !_contentObscured)
        {
            InvokeClock(commandId);
            return;
        }
        if (InvokeField(provider, commandId)) return;
        if (provider == "tribe-app" && _app == ClientTerminalApp.Tribe && !_contentObscured)
        {
            if (commandId == "discuss-task") { DiscussTribeTask(); return; }
            _tribeApp.HandleCommand(commandId);
            _nextRefresh = 0;
            return;
        }
        if (provider == "games" && _app == ClientTerminalApp.Games && !_contentObscured)
        {
            ReleaseArcadeKeyboard();
            _games.HandleCommand(commandId);
            _view?.SetGamesState(_games.Snapshot);
            return;
        }
        if (provider == "devices" && _app == ClientTerminalApp.Devices && !_contentObscured)
        {
            _devices.HandleCommand(commandId);
            _nextRefresh = 0;
            return;
        }
        if (_open && provider == "settings" && commandId == "preview-tone" && _preferences != null)
        {
            _alerts?.Preview(_preferences.Snapshot);
            _nextRefresh = 0;
            return;
        }
        if (_open && _app == ClientTerminalApp.Messages && provider == "messages" && commandId != "send")
        {
            if (InvokeInbox(commandId)) { _nextRefresh = 0; return; }
            if (commandId.StartsWith("channel:", StringComparison.Ordinal))
            {
                RememberMessageDraft();
                _messenger.SelectChannel(commandId[8..]);
                RestoreMessageDraft();
            }
            else if (commandId.StartsWith("target:", StringComparison.Ordinal))
            {
                RememberMessageDraft();
                _messenger.SelectTarget(commandId[7..]);
                RestoreMessageDraft();
            }
            else if (commandId == "contacts-find")
            {
                _messageStatus = "";
                _messenger.FindCurrentContact();
            }
            else if (commandId == "contacts-cancel")
            {
                _messageStatus = "";
                _messenger.CancelContactSearch();
            }
            else if (commandId is "contacts-refresh" or "contacts-prev" or "contacts-next")
            {
                _messageStatus = "";
                _messenger.LoadContacts(commandId == "contacts-prev" ? Math.Max(1, _messenger.ContactPage - 1)
                    : commandId == "contacts-next" ? _messenger.ContactPage + 1 : _messenger.ContactPage);
            }
            _nextRefresh = 0;
            return;
        }
        if (_open && _app == ClientTerminalApp.Messages && provider == "messages" && commandId == "send")
        {
            var legacy = _messenger.Channel == "legacy";
            if (legacy ? _messages.Send(_messageDraft) : _messenger.Send(_messageDraft))
            { _messageDraft = ""; _sharedDraft = null; RememberMessageDraft(); }
            // Managed completion updates its own status asynchronously.
            _messageStatus = legacy ? _messages.Status : "";
            _nextRefresh = 0;
            return;
        }
        if (_open && _app == ClientTerminalApp.Navigation && provider == "navigation")
        {
            if (commandId is "zoom-in" or "zoom-out")
                _tablet.ZoomNavigation(commandId == "zoom-in" ? 1 : -1);
            return;
        }
        if (!_open || provider != ClientTerminalModel.ProviderId(_app))
            return;
        if (!MenuRegistry.TryGetProviderCommands(provider, out var snapshot))
            return;
        var command = snapshot.Commands.FirstOrDefault(item => item.Id == commandId);
        if (command == null || !command.Enabled || command.RequiresConfirmation)
        {
            Feedback("This action is no longer available. Choose it again after the page updates.");
            return;
        }
        MenuRegistry.TryInvokeProviderCommand(provider, command.Id, out var result);
        if (result != null && !result.Succeeded)
            Feedback(result.Detail);
        else
            _feedback = "";
        _nextRefresh = 0;
    }

    private CommandResultSnapshot Edit(string provider, Guid scope, string field, string value)
    {
        if (!CanUseDesktop())
            return CommandResultSnapshot.Failure("Resume the app before changing it.");
        if (_open && provider == "settings" && _preferences != null)
        {
            var changed = _os.QueuePreference(field, value, out var status);
            _nextRefresh = 0;
            return changed ? CommandResultSnapshot.Success(status) : CommandResultSnapshot.Failure(status);
        }
        if (_open && provider == "field") return EditField(field, value);
        if (provider == "pack" && _app == ClientTerminalApp.Pack && !_contentObscured && field == "search")
        {
            var changed = _pack.SetSearch(value);
            _nextRefresh = 0;
            return changed ? CommandResultSnapshot.Success("Search updated.")
                : CommandResultSnapshot.Failure("Use a short item name or item ID.");
        }
        if (provider == "tribe-app" && _app == ClientTerminalApp.Tribe && !_contentObscured)
            return _tribeApp.HandleEdit(field, value);
        if (_open && _app == ClientTerminalApp.Messages && provider == "messages" && field == "message")
        {
            if (value.Length > (_messenger.Channel == "legacy" ? _messages.MaximumLength : 256) || value.Any(char.IsControl))
                return CommandResultSnapshot.Failure("Use a single line within the chat length limit.");
            _messageDraft = value;
            if (_sharedDraft != value) _sharedDraft = null;
            RememberMessageDraft();
            _messageStatus = "";
            return CommandResultSnapshot.Success("Draft updated.");
        }
        if (!_open || provider != ClientTerminalModel.ProviderId(_app))
            return CommandResultSnapshot.Failure("This form is no longer open.");
        MenuRegistry.TrySetProviderInput(provider, scope, field, value, out var result);
        if (result != null && !result.Succeeded)
            Feedback(result.Detail);
        else
            _feedback = "";
        _nextRefresh = 0;
        return result ?? CommandResultSnapshot.Failure("The draft could not be updated.");
    }

    private void Feedback(string message)
    {
        _feedback = message;
        _feedbackUntil = Time.unscaledTime + 8;
    }

    private void Refresh(bool resetScroll)
    {
        if (Time.unscaledTime >= _feedbackUntil)
            _feedback = "";
        _view?.ResetShellSession(_osGeneration);
        _view?.Render(ClientTerminalModel.Read(_app, _feedback), resetScroll);
        _view?.SetActionFeedback(_feedback);
        var os = _os.Snapshot;
        _view?.SetDesktopState(os.Generation == _osGeneration ? os
            : os with { Ready = false, Locked = true, TaskSwitcherOpen = false,
                UnreadMessages = 0, RecentApps = Array.Empty<ClientTerminalApp>(), Watch = default, Satellite = default });
        _view?.SetWatch(os.Generation == _osGeneration ? os.Watch : default);
        _view?.SetDroneDestination(os.Generation == _osGeneration
            ? os.Field.Places.FirstOrDefault(place => place.Id == os.Field.DestinationId)?.Name ?? "" : "");
        _view?.SetIslandClock(_watchSnapshot.Clock);
        var deviceState = os.Generation == _osGeneration ? os : os with
        {
            Ready = false, Reminders = new(), WristWatch = new(), Runtime = new(),
            StorageStatus = "Opening this world's notebook…"
        };
        SyncContentVisibility();
        _view?.SetSystemStatus(os.Ready ? "MODSTOKE OS / READY" : string.IsNullOrEmpty(os.Problem) ? "STARTING" : "OS UNAVAILABLE");
        var history = os.Generation == _osGeneration ? os.Messages : Array.Empty<ClientTabletMessage>();
        var visible = history.Where(message => message.Channel == _messenger.Channel
            && (_messenger.Channel != "direct" || message.ConversationId == _messenger.Target))
            .Select(message => string.IsNullOrEmpty(message.SenderName) ? message.Text : message.SenderName + ": " + message.Text).ToArray();
        var legacy = _messenger.Channel == "legacy";
        var messageStatus = string.IsNullOrEmpty(_messageStatus) ? legacy ? _messages.Status : _messenger.Status : _messageStatus;
        _view?.SetMessengerChannel(_messenger.Channel);
        _view?.SetMessengerContext(_messenger.Channel switch { "tribe" => "Tribe", "direct" => "Direct", "legacy" => "Game chat", _ => "Expedition" },
            _messenger.Channel switch
            {
                "direct" => _messenger.Contacts.FirstOrDefault(contact => contact.Id == _messenger.Target)?.Name
                    ?? (_messenger.Target.Length == 0 ? "Choose a recipient" : "Selected conversation"),
                "tribe" => "Message to your Tribe",
                "legacy" => "Message to public game chat",
                _ => "Message to the Expedition"
            }, messageStatus);
        _view?.SetMessengerContacts(_messenger.Contacts, _messenger.MoreContacts, _messenger.ContactPage);
        _view?.SetMessengerContactState(_messenger.ContactState);
        _view?.SetInboxState(history, _messenger.Channel, _messenger.Target);
        _view?.SetInboxVisible(_app == ClientTerminalApp.Messages && CanUseDesktop() && !_contentObscured);
        _view?.SetMessages(visible, _messageDraft, messageStatus, legacy ? _messages.CanSend
            : _messenger.CanSend,
            os.Generation == _osGeneration ? os.UnreadMessages : 0);
        var latestUnread = history.LastOrDefault(message => message.Unread);
        _view?.SetNotificationSummary(os.Generation == _osGeneration ? os.UnreadMessages : 0,
            latestUnread == null ? "" : (string.IsNullOrEmpty(latestUnread.SenderName) ? "Game chat" : latestUnread.SenderName) + ": " + latestUnread.Text);
        if (_preferences != null)
            _view?.SetPreferences(_preferences.Snapshot, _preferences.Status + "\n" + (_alerts?.Status ?? ""),
                "Modstoke OS · Client " + ClientVersion + "\nWorker: " + (os.Ready ? "ready" : "unavailable")
                + "\nSession generation: " + _osGeneration + "\nInbox: " + history.Count
                + " / 100\nDesktop: " + (!os.Visible ? "standby" : os.Locked ? "wake screen" : os.TaskSwitcherOpen ? "recent apps" : "app open")
                + "\nRecent apps: " + os.RecentApps.Count
                + "\nMap data: " + (_tablet.NavigationAvailable ? "available" : "unavailable")
                + "\nMessenger: " + (_messenger.Available ? "available" : "unavailable")
                + "\nWatch: " + (_wristWatch.Status.Length > 0 ? _wristWatch.Status : _wristWatch.IsAvailable ? "attached" : "waiting for wrist")
                + "\nEquipment: " + _deviceInventory.Status
                + "\n" + _watchHud.Status);
        _view?.SetSatellite(os.Ready && os.Generation == _osGeneration ? os.Satellite : default);
        _view?.SetFieldState(os.Generation == _osGeneration ? os : os with
        { Field = new(), Notifications = Array.Empty<ClientTabletNotification>(), ProfileLoaded = false, StorageStatus = "Opening this world's notebook…" }, _watchSnapshot);
        _view?.SetRuntimeState(deviceState);
        _view?.SetClockState(deviceState);
        _view?.SetFieldEditor(_noteId, _noteTitle, _noteBody, _notePlaceId, _noteBrowsePlaceId);
        _view?.SetFieldNotificationShade(os.Generation == _osGeneration ? os.Notifications : Array.Empty<ClientTabletNotification>());
        _view?.SetDeviceAppState(_devices.Snapshot);
        _view?.SetTribeAppState(_tribeApp.Snapshot);
        _view?.SetPackAppState(_pack.Snapshot);
        _view?.SetEnvironment(_environmentSnapshot);
        _view?.ApplyTextSize(_preferences?.Snapshot.TextSize ?? ClientTabletTextSize.Normal);
        _view?.SetNavigationAvailable(_tablet.NavigationAvailable);
        _tablet.SetNavigationVisible(_view?.NavigationVisible == true);
        _nextRefresh = Time.unscaledTime + 0.25f;
    }

    private static Guid? CurrentSession() => ClientCommands.TryGetCurrentSession(out var session)
        ? session : null;

    private static bool LoaderMenuVisible() => UiState.Snapshot().Values.Any(surface => surface.Visible);

    private static bool IsEditingText()
    {
        var current = EventSystem.current;
        var selected = current ? current.currentSelectedGameObject : null;
        if (selected == null || !selected)
            return false;
        var text = selected.GetComponent<TMP_InputField>();
        var legacy = selected.GetComponent<InputField>();
        return (text && text.isFocused) || (legacy && legacy.isFocused);
    }
}
