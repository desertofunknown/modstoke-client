// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Modstoke.Client;

internal sealed record ClientTabletOsSnapshot(
    bool Ready,
    ClientTerminalApp ActiveApp,
    IReadOnlyList<ClientTabletMessage> Messages,
    int UnreadMessages,
    long Generation,
    long Revision,
    string Problem,
    bool Visible,
    bool Locked,
    bool TaskSwitcherOpen,
    IReadOnlyList<ClientTerminalApp> RecentApps,
    ClientTabletWatchSnapshot Watch,
    ClientTabletSatelliteSnapshot Satellite)
{
    internal ClientFieldProfile Field { get; init; } = new();
    internal IReadOnlyList<ClientTabletNotification> Notifications { get; init; } = Array.Empty<ClientTabletNotification>();
    internal string StorageStatus { get; init; } = "Session notebook";
    internal bool ProfileLoaded { get; init; }
    internal ClientWatchOsSnapshot WristWatch { get; init; } = new();
    internal ClientTabletRuntimeSnapshot Runtime { get; init; } = new();
    internal ClientTabletReminderSnapshot Reminders { get; init; } = new();
}

internal enum ClientTabletWatchState { Idle, Running, Paused, Elapsed }

internal readonly record struct ClientTabletWatchSnapshot(ClientTabletWatchState State, int RemainingSeconds,
    long CompletionId);

internal enum ClientTabletSatelliteState { Unavailable, Off, Searching, Synchronizing, Ready }

internal readonly record struct ClientTabletSatelliteSnapshot(ClientTabletSatelliteState State, bool Enabled);

internal sealed record ClientTabletMessage(string Channel, string ConversationId, string SenderId,
    string SenderName, string Text, bool Unread = true);

// Only managed tablet state belongs here. Game and provider work stays on the main thread.
internal sealed partial class ClientTabletOperatingSystem : IDisposable
{
    private const int QueueLimit = 128;
    private const int HistoryLimit = 100;
    private const int MessageLimit = 2048;
    private const int BackStackLimit = 16;
    private const int RecentAppsLimit = 8;
    private const int SatelliteStageSeconds = 3;
    private static readonly IReadOnlyList<ClientTabletMessage> EmptyMessages = Array.AsReadOnly(Array.Empty<ClientTabletMessage>());
    private static readonly IReadOnlyList<ClientTerminalApp> EmptyApps = Array.AsReadOnly(Array.Empty<ClientTerminalApp>());

    private readonly object _gate = new();
    private readonly AutoResetEvent _wakeup = new(false);
    private readonly Queue<Operation> _operations = new(QueueLimit);
    private readonly List<ClientTabletMessage> _messages = new(HistoryLimit);
    private readonly List<ClientTerminalApp> _backStack = new(BackStackLimit);
    private readonly List<ClientTerminalApp> _recentApps = new(RecentAppsLimit);
    private ClientTabletOsSnapshot _snapshot = new(false, ClientTerminalApp.Home, EmptyMessages, 0, 0, 0, string.Empty,
        false, true, false, EmptyApps, default, new(ClientTabletSatelliteState.Unavailable, true));
    private Thread? _thread;
    private bool _started;
    private bool _finished;
    private bool _disposed;
    private bool _hasSessionRequest;
    private long _requestedGeneration;
    private long? _pendingResetGeneration;
    private long _generation;
    private long _revision;
    private bool _ready;
    private bool _visible;
    private bool _locked = true;
    private bool _taskSwitcherOpen;
    private bool _contentObscured;
    private ClientTabletWatchState _watchState;
    private long _watchDeadline;
    private long _watchRemainingTicks;
    private int _watchRemainingSeconds;
    private long _watchCompletionId;
    private bool _satelliteInWorld;
    private bool _satelliteEnabled = true;
    private ClientTabletSatelliteState _satelliteState;
    private long _satelliteDeadline;
    private ClientTerminalApp _activeApp = ClientTerminalApp.Home;
    private int _unreadMessages;
    private string _messageChannel = "expedition";
    private string _messageTarget = "";
    private string _problem = string.Empty;

    public ClientTabletOsSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public void Start(string? gameRoot = null, ClientTabletPreferences? preferences = null)
    {
        lock (_gate)
        {
            if (_started || _disposed || _finished) return;
            if (!string.IsNullOrWhiteSpace(gameRoot)) _profileStore = new ClientFieldProfileStore(gameRoot);
            _devicePreferenceStore = preferences ?? new ClientTabletPreferences(gameRoot);
            _started = true;
            _runtimeStarted = Stopwatch.GetTimestamp();
            try
            {
                var thread = new Thread(Run)
                {
                    IsBackground = true, Name = "Modstoke tablet OS", Priority = ThreadPriority.BelowNormal
                };
                thread.Start();
                _thread = thread;
            }
            catch (Exception exception)
            {
                Fail(exception);
                _finished = true;
                _wakeup.Dispose();
            }
        }
    }

    public bool Navigate(ClientTerminalApp app)
    {
        if (!Enum.IsDefined(app)) return false;
        return Post(OperationKind.Navigate, app, false, string.Empty);
    }

    public bool OpenFromShortcut(ClientTerminalApp app)
    {
        if (!Enum.IsDefined(app)) return false;
        return Post(OperationKind.OpenShortcut, app, false, string.Empty);
    }

    public bool SetVisible(bool visible) => Post(OperationKind.Visibility, ClientTerminalApp.Home, visible, string.Empty);

    public bool Lock() => Post(OperationKind.Lock, ClientTerminalApp.Home, false, string.Empty);

    public bool Unlock() => Post(OperationKind.Unlock, ClientTerminalApp.Home, false, string.Empty);

    public bool Back() => Post(OperationKind.Back, ClientTerminalApp.Home, false, string.Empty);

    public bool SetTaskSwitcher(bool open) => Post(OperationKind.TaskSwitcher, ClientTerminalApp.Home, open, string.Empty);

    public bool DismissApp(ClientTerminalApp app) => Enum.IsDefined(app)
        && Post(OperationKind.DismissApp, app, false, string.Empty);

    public bool ClearRecentApps() => Post(OperationKind.ClearRecentApps, ClientTerminalApp.Home, false, string.Empty);

    public bool MarkAllMessagesRead() => Post(OperationKind.MarkAllMessagesRead, ClientTerminalApp.Home, false, string.Empty);

    public bool SetContentObscured(bool obscured) => Post(OperationKind.ContentObscured, ClientTerminalApp.Home, obscured, string.Empty);

    public bool StartWatch(int seconds) => seconds is >= 60 and <= 3600
        && Post(OperationKind.StartWatch, ClientTerminalApp.Home, false, string.Empty, seconds: seconds);

    public bool PauseWatch() => Post(OperationKind.PauseWatch, ClientTerminalApp.Home, false, string.Empty);

    public bool ResumeWatch() => Post(OperationKind.ResumeWatch, ClientTerminalApp.Home, false, string.Empty);

    public bool CancelWatch() => Post(OperationKind.CancelWatch, ClientTerminalApp.Home, false, string.Empty);

    public bool SetSatelliteContext(bool inWorld) =>
        Post(OperationKind.SatelliteContext, ClientTerminalApp.Home, inWorld, string.Empty);

    public bool SetSatelliteEnabled(bool enabled) =>
        Post(OperationKind.SatelliteEnabled, ClientTerminalApp.Home, enabled, string.Empty);

    public bool ReconnectSatellite() =>
        Post(OperationKind.SatelliteReconnect, ClientTerminalApp.Home, false, string.Empty);

    public bool ReceiveMessage(ClientTabletMessage message)
    {
        if (message is null || message.Text.Length > MessageLimit || string.IsNullOrWhiteSpace(message.Text)
            || message.Channel.Length > 16 || message.ConversationId.Length > 64
            || message.SenderId.Length > 64 || message.SenderName.Length > 128) return false;
        return Post(OperationKind.Message, ClientTerminalApp.Home, false, string.Empty, message);
    }

    public bool SelectConversation(string channel, string target) =>
        channel is "expedition" or "tribe" or "direct" or "legacy" && target.Length <= 64
        && Post(OperationKind.Conversation, ClientTerminalApp.Home, false, channel, null, target);

    public bool ClearMessages(string channel) => channel == "legacy"
        && Post(OperationKind.ClearChannel, ClientTerminalApp.Home, false, channel);

    public bool ResetSession(long generation)
    {
        lock (_gate)
        {
            if (!Accepting || generation < _requestedGeneration) return RejectCommand();
            if (_hasSessionRequest && generation == _requestedGeneration) return true;
            _hasSessionRequest = true;
            _requestedGeneration = generation;
            // Finish local edits in their original profile before crossing the session boundary.
            RetainLocalEdits();
            _pendingResetGeneration = generation;
            EnqueuePendingReset();
            _wakeup.Set();
            return true;
        }
    }

    public void Dispose()
    {
        Thread? thread;
        lock (_gate)
        {
            if (_disposed) return;
            UpdateForegroundActivity();
            _disposed = true;
            _pendingResetGeneration = null;
            RetainLocalEdits();
            _ready = false;
            _messages.Clear();
            _unreadMessages = 0;
            _visible = false;
            _locked = true;
            _taskSwitcherOpen = false;
            _contentObscured = false;
            ResetWatch();
            ResetWristWatch();
            ResetReminders();
            ResetSatellite();
            _backStack.Clear();
            _recentApps.Clear();
            Publish();
            thread = _thread;
            if (!_finished)
            {
                if (thread is null)
                {
                    _finished = true;
                    _wakeup.Dispose();
                }
                else
                {
                    _wakeup.Set();
                }
            }
        }

        // The worker owns its wait handle until it exits, including after a timed-out join.
        if (thread is not null && thread != Thread.CurrentThread) thread.Join(500);
    }

    private bool Accepting => _started && !_finished && !_disposed && _problem.Length == 0;

    private bool Post(OperationKind kind, ClientTerminalApp app, bool visible, string line,
        ClientTabletMessage? message = null, string target = "", int seconds = 0,
        ClientWatchCommand watchCommand = default)
    {
        lock (_gate)
        {
            if (!Accepting || _operations.Count >= QueueLimit) return RejectCommand();
            _operations.Enqueue(new Operation(kind, _requestedGeneration, app, visible, line, message, target, seconds,
                WatchCommand: watchCommand));
            _wakeup.Set();
            return true;
        }
    }

    private void Run()
    {
        try
        {
            // Preferences own only managed data; all of their disk work stays on this worker.
            _devicePreferenceStore?.Load();
            while (true)
            {
                var sliceStarted = Stopwatch.GetTimestamp();
                var changed = false;
                var processed = 0;
                while (processed < CommandsPerSlice)
                {
                    lock (_gate)
                    {
                        if (!_operations.TryPeek(out Operation operation)) break;
                        // Finish loading the old profile before a reset clears its retained edits.
                        if (operation.Kind == OperationKind.Reset && _loadProfileKey.Length != 0) break;
                        _operations.Dequeue();
                        EnqueuePendingReset();
                        var localEdit = IsLocalEdit(operation)
                            && (operation.Kind == OperationKind.Preference || operation.Generation == _generation);
                        if ((_disposed || operation.Generation != _requestedGeneration) && !localEdit)
                            _rejectedCommands++;
                        else
                        {
                            UpdateForegroundActivity();
                            Apply(operation);
                            UpdateForegroundActivity();
                            _processedCommands++;
                        }
                        changed = true;
                    }
                    // Release the queue gate between every command, including a slow file write.
                    ProcessPreferencePersistence();
                    processed++;
                    if (Stopwatch.GetElapsedTime(sliceStarted).TotalMilliseconds >= SliceMilliseconds) break;
                }

                int waitMilliseconds;
                bool stopping;
                bool pending;
                lock (_gate)
                {
                    stopping = _disposed;
                    changed |= !_ready;
                    if (stopping) SaveField();

                    changed |= UpdateWatch();
                    changed |= UpdateWristWatch();
                    changed |= UpdateSatellite();
                    changed |= UpdateReminders();
                    changed |= FlushFieldIfDue();

                    // Ready means the initial pump has actually run, with no artificial boot delay.
                    _ready = !stopping;
                    UpdateForegroundActivity();
                    changed |= UpdateRuntimeDiagnostics();
                    _lastSliceMilliseconds = Stopwatch.GetElapsedTime(sliceStarted).TotalMilliseconds;
                    if (changed) Publish();
                    waitMilliseconds = WristWatchTicking ? 100
                        : _watchState == ClientTabletWatchState.Running || SatelliteAcquiring || _fieldDirty
                            || RemindersPending || RuntimeDiagnosticsVisible
                            ? 1000 : Timeout.Infinite;
                }

                ProcessFieldPersistence();
                lock (_gate)
                {
                    stopping = _disposed;
                    pending = _operations.Count != 0 || _fieldWrites.Count != 0 || _loadProfileKey.Length != 0
                        || stopping && _fieldDirty;
                }
                if (stopping && !pending) return;
                // A backlog yields between slices; disposal wakes this wait and drains retained edits.
                // The budget is cooperative: an individual command or disk write is not preempted.
                _wakeup.WaitOne(pending ? 1 : waitMilliseconds);
            }
        }
        catch (Exception exception)
        {
            lock (_gate) Fail(exception);
        }
        finally
        {
            lock (_gate)
            {
                _finished = true;
                _wakeup.Dispose();
            }
        }
    }

    private void Apply(Operation operation)
    {
        if (operation.Kind == OperationKind.Preference)
        {
            _pendingPreferenceChange = new PreferenceChange(operation.Line, operation.Target);
            return;
        }
        if (operation.Kind == OperationKind.Reset)
        {
            ResetRuntimeActivity();
            ResetField();
            _generation = operation.Generation;
            _messages.Clear();
            _unreadMessages = 0;
            _activeApp = ClientTerminalApp.Home;
            _locked = true;
            _taskSwitcherOpen = false;
            _contentObscured = false;
            ResetWatch();
            ResetWristWatch();
            ResetReminders();
            ResetSatellite();
            _backStack.Clear();
            _recentApps.Clear();
            _messageChannel = "expedition";
            _messageTarget = "";
            return;
        }

        if (operation.Generation != _generation) return;
        if (operation.Field != null) { ApplyField(operation.Field); return; }
        switch (operation.Kind)
        {
            case OperationKind.WristWatchAwake:
                _wristAwake = operation.Visible;
                break;
            case OperationKind.WristWatchCommand:
                ApplyWristWatchCommand(operation.WatchCommand);
                break;
            case OperationKind.Navigate:
                if (!_locked) OpenApp(operation.App, rememberPrevious: true);
                break;
            case OperationKind.OpenShortcut:
                _locked = false;
                OpenApp(operation.App, rememberPrevious: true);
                break;
            case OperationKind.Visibility:
                _visible = operation.Visible;
                if (!_visible)
                {
                    _locked = true;
                    _taskSwitcherOpen = false;
                    _contentObscured = false;
                }
                break;
            case OperationKind.Lock:
                _locked = true;
                _taskSwitcherOpen = false;
                break;
            case OperationKind.Unlock:
                _locked = false;
                break;
            case OperationKind.TaskSwitcher:
                _taskSwitcherOpen = operation.Visible && !_locked;
                break;
            case OperationKind.DismissApp:
                if (_locked) break;
                _appActivity.Remove(operation.App);
                _recentApps.Remove(operation.App);
                _backStack.Remove(operation.App);
                if (_activeApp == operation.App) _activeApp = ClientTerminalApp.Home;
                break;
            case OperationKind.ClearRecentApps:
                if (_locked) break;
                ResetRuntimeActivity();
                _recentApps.Clear();
                _backStack.Clear();
                _activeApp = ClientTerminalApp.Home;
                break;
            case OperationKind.MarkAllMessagesRead:
                if (!_visible || _locked) break;
                for (var index = 0; index < _messages.Count; index++)
                    _messages[index] = _messages[index] with { Unread = false };
                _notifications.RemoveAll(item => item.App == ClientTerminalApp.Messages);
                break;
            case OperationKind.ContentObscured:
                _contentObscured = operation.Visible;
                break;
            case OperationKind.StartWatch:
                _watchRemainingTicks = operation.Seconds * Stopwatch.Frequency;
                _watchDeadline = Stopwatch.GetTimestamp() + _watchRemainingTicks;
                _watchRemainingSeconds = operation.Seconds;
                _watchState = ClientTabletWatchState.Running;
                break;
            case OperationKind.PauseWatch:
                UpdateWatch();
                if (_watchState != ClientTabletWatchState.Running) break;
                _watchState = ClientTabletWatchState.Paused;
                break;
            case OperationKind.ResumeWatch:
                if (_watchState != ClientTabletWatchState.Paused) break;
                _watchDeadline = Stopwatch.GetTimestamp() + _watchRemainingTicks;
                _watchState = ClientTabletWatchState.Running;
                break;
            case OperationKind.CancelWatch:
                _watchState = ClientTabletWatchState.Idle;
                _watchRemainingTicks = 0;
                _watchRemainingSeconds = 0;
                _watchDeadline = 0;
                break;
            case OperationKind.SatelliteContext:
                if (_satelliteInWorld == operation.Visible) break;
                _satelliteInWorld = operation.Visible;
                RestartSatellite();
                break;
            case OperationKind.SatelliteEnabled:
                if (_satelliteEnabled == operation.Visible) break;
                _satelliteEnabled = operation.Visible;
                RestartSatellite();
                break;
            case OperationKind.SatelliteReconnect:
                if (_satelliteInWorld && _satelliteEnabled) RestartSatellite();
                break;
            case OperationKind.Back:
                if (_taskSwitcherOpen)
                {
                    _taskSwitcherOpen = false;
                }
                else if (!_locked)
                {
                    var app = ClientTerminalApp.Home;
                    if (_backStack.Count > 0)
                    {
                        int index = _backStack.Count - 1;
                        app = _backStack[index];
                        _backStack.RemoveAt(index);
                    }
                    OpenApp(app, rememberPrevious: false);
                }
                break;
            case OperationKind.Message:
                if (operation.Message is null) break;
                if (_messages.Count == HistoryLimit) _messages.RemoveAt(0);
                _messages.Add(operation.Message with { Unread = true });
                AddNotification("message:" + operation.Message.Channel + ":" + operation.Message.ConversationId,
                    operation.Message.SenderName.Length == 0 ? "Game chat" : operation.Message.SenderName,
                    operation.Message.Text, ClientTerminalApp.Messages, operation.Message.Channel,
                    operation.Message.ConversationId);
                break;
            case OperationKind.Conversation:
                _messageChannel = operation.Line;
                _messageTarget = operation.Target;
                break;
            case OperationKind.ClearChannel:
                _messages.RemoveAll(message => message.Channel == operation.Line);
                _notifications.RemoveAll(item => item.Channel == operation.Line);
                break;
        }

        var readingMessages = _visible && !_locked && !_taskSwitcherOpen && !_contentObscured
            && _activeApp == ClientTerminalApp.Messages;
        _unreadMessages = 0;
        for (var index = 0; index < _messages.Count; index++)
        {
            var message = _messages[index];
            if (readingMessages && message.Channel == _messageChannel
                && (_messageChannel != "direct" || message.ConversationId == _messageTarget))
                _messages[index] = message = message with { Unread = false };
            if (message.Unread) _unreadMessages++;
        }
        if (readingMessages)
            _notifications.RemoveAll(item => item.App == ClientTerminalApp.Messages && item.Channel == _messageChannel
                && (_messageChannel != "direct" || item.Target == _messageTarget));
    }

    private void OpenApp(ClientTerminalApp app, bool rememberPrevious)
    {
        RecordAppOpen(app);
        _taskSwitcherOpen = false;
        if (app != _activeApp)
        {
            // Keep each destination once so repeated app switching cannot build navigation loops.
            _backStack.Remove(app);
            if (rememberPrevious)
            {
                _backStack.Remove(_activeApp);
                if (_backStack.Count == BackStackLimit) _backStack.RemoveAt(0);
                _backStack.Add(_activeApp);
            }
            _activeApp = app;
            _field = _field with { LastApp = app };
            _editedFieldKinds.Add("app");
            DirtyField();
        }

        if (app == ClientTerminalApp.Home) return;
        _recentApps.Remove(app);
        if (_recentApps.Count == RecentAppsLimit) _recentApps.RemoveAt(_recentApps.Count - 1);
        _recentApps.Insert(0, app);
    }

    private void Fail(Exception exception)
    {
        UpdateForegroundActivity();
        _ready = false;
        _problem = exception.Message.Length == 0 ? exception.GetType().Name : exception.Message;
        if (_problem.Length > MessageLimit) _problem = _problem[..MessageLimit];
        foreach (var operation in _operations)
            if (operation.Kind == OperationKind.Preference) _devicePreferenceStore?.PreferenceAbandoned();
        if (_pendingPreferenceChange.HasValue)
        {
            _devicePreferenceStore?.PreferenceAbandoned();
            _pendingPreferenceChange = null;
        }
        _rejectedCommands += _operations.Count;
        _operations.Clear();
        _pendingResetGeneration = null;
        ResetWristWatch();
        ResetSatellite();
        Publish();
    }

    private bool UpdateWatch()
    {
        if (_watchState != ClientTabletWatchState.Running) return false;
        _watchRemainingTicks = Math.Max(0, _watchDeadline - Stopwatch.GetTimestamp());
        int seconds = WatchSeconds(_watchRemainingTicks);
        bool changed = seconds != _watchRemainingSeconds;
        _watchRemainingSeconds = seconds;
        if (_watchRemainingTicks == 0)
        {
            _watchState = ClientTabletWatchState.Elapsed;
            _watchCompletionId++;
            AddNotification("field-timer", "Field timer", "Your timer has finished.", ClientTerminalApp.Watch);
            changed = true;
        }
        return changed;
    }

    private static int WatchSeconds(long ticks) => (int)((ticks + Stopwatch.Frequency - 1) / Stopwatch.Frequency);

    private void ResetWatch()
    {
        _watchState = ClientTabletWatchState.Idle;
        _watchDeadline = 0;
        _watchRemainingTicks = 0;
        _watchRemainingSeconds = 0;
        _watchCompletionId = 0;
    }

    private bool SatelliteAcquiring => _satelliteState is ClientTabletSatelliteState.Searching
        or ClientTabletSatelliteState.Synchronizing;

    private void ResetSatellite()
    {
        _satelliteInWorld = false;
        _satelliteState = ClientTabletSatelliteState.Unavailable;
        _satelliteDeadline = 0;
    }

    private void RestartSatellite()
    {
        _satelliteState = !_satelliteInWorld ? ClientTabletSatelliteState.Unavailable
            : !_satelliteEnabled ? ClientTabletSatelliteState.Off : ClientTabletSatelliteState.Searching;
        _satelliteDeadline = SatelliteAcquiring ? Stopwatch.GetTimestamp() + SatelliteStageSeconds * Stopwatch.Frequency : 0;
    }

    private bool UpdateSatellite()
    {
        // Fictional equipment acquisition only; no transport or game services participate.
        if (!SatelliteAcquiring || Stopwatch.GetTimestamp() < _satelliteDeadline) return false;
        _satelliteState = _satelliteState == ClientTabletSatelliteState.Searching
            ? ClientTabletSatelliteState.Synchronizing : ClientTabletSatelliteState.Ready;
        _satelliteDeadline = SatelliteAcquiring ? Stopwatch.GetTimestamp() + SatelliteStageSeconds * Stopwatch.Frequency : 0;
        return true;
    }

    private void Publish()
    {
        IReadOnlyList<ClientTabletMessage> messages = _messages.Count == 0 ? EmptyMessages : Array.AsReadOnly(_messages.ToArray());
        IReadOnlyList<ClientTerminalApp> recentApps = _recentApps.Count == 0 ? EmptyApps : Array.AsReadOnly(_recentApps.ToArray());
        Volatile.Write(ref _snapshot, new ClientTabletOsSnapshot(
            _ready, _activeApp, messages, _unreadMessages, _generation, ++_revision, _problem, _visible,
            _locked, _taskSwitcherOpen, recentApps,
            new ClientTabletWatchSnapshot(_watchState, _watchRemainingSeconds, _watchCompletionId),
            new ClientTabletSatelliteSnapshot(_satelliteState, _satelliteEnabled))
        {
            Field = _field,
            Notifications = Array.AsReadOnly(_notifications.ToArray()),
            StorageStatus = _fieldStorageStatus,
            ProfileLoaded = _profileLoaded,
            WristWatch = CaptureWristWatch(),
            Runtime = CaptureRuntime(),
            Reminders = CaptureReminders()
        });
    }

    private enum OperationKind
    {
        Navigate, Visibility, Message, Reset, Conversation, ClearChannel, Lock, Unlock, Back, TaskSwitcher,
        DismissApp, ClearRecentApps, MarkAllMessagesRead, ContentObscured, StartWatch, PauseWatch, ResumeWatch, CancelWatch,
        SatelliteContext, SatelliteEnabled, SatelliteReconnect, Field, WristWatchAwake, WristWatchCommand, Preference,
        OpenShortcut
    }

    private readonly record struct Operation(OperationKind Kind, long Generation, ClientTerminalApp App, bool Visible,
        string Line, ClientTabletMessage? Message = null, string Target = "", int Seconds = 0,
        ClientFieldAction? Field = null, ClientWatchCommand WatchCommand = default);
}
