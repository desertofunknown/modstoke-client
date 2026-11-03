// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
using NetworkId = Bolt.NetworkId;
using HarmonyLib;
using Modstoke.Sons;
using TheForest.UI.Multiplayer;
using UnityEngine;
using NativeInput = Sons.Input.InputSystem;

namespace Modstoke.Client;

// Keeps native chat processing and server policy in charge; only its presentation moves.
internal sealed class ClientTabletMessages : IDisposable
{
    private const int HistoryLimit = 100;
    private const int DisplayLimit = 2048;
    private static ClientTabletMessages? _active;
    [ThreadStatic] private static InputScope? _inputScope;
    private readonly Func<bool> _requestOpen;
    private readonly Action<string> _received;
    private readonly Action _sessionChanged;
    private readonly Harmony _patches = new("modstoke.client.tablet-messages");
    private MethodInfo? _addLine;
    private MethodInfo? _update;
    private MethodInfo? _getButtonDown;
    private ChatBox? _box;
    private int _scene;
    private int _ownerThread;
    private bool _started;
    private bool _failed;
    private bool _canSend;
    private volatile bool _stopRequested;
    private CanvasGroup? _popupGroup;
    private GameObject? _popupContainer;
    private bool _ownsPopupGroup;
    private float _popupAlpha;
    private bool _popupEnabled;
    private bool _popupInteractable;
    private bool _popupRaycasts;

    internal ClientTabletMessages(Func<bool> requestOpen, Action<string> received, Action sessionChanged)
    {
        _requestOpen = requestOpen ?? throw new ArgumentNullException(nameof(requestOpen));
        _received = received ?? throw new ArgumentNullException(nameof(received));
        _sessionChanged = sessionChanged ?? throw new ArgumentNullException(nameof(sessionChanged));
    }

    internal bool CanSend => Ready && _canSend;
    internal int MaximumLength { get; private set; } = 256;
    internal string Status { get; private set; } = "Chat is waiting for a multiplayer game.";
    private bool OwnerThread => Environment.CurrentManagedThreadId == _ownerThread;
    private bool Ready => _started && !_failed && !_stopRequested && OwnerThread;

    internal void Start()
    {
        if (_started) return;
        _ownerThread = Environment.CurrentManagedThreadId;
        _failed = false;
        _stopRequested = false;
        if (_active != null && !ReferenceEquals(_active, this))
        {
            Status = "Tablet messages are unavailable; use the game's chat.";
            return;
        }
        _active = this;
        _started = true;
        try
        {
            _addLine = AccessTools.Method(typeof(ChatBox), nameof(ChatBox.AddLine),
                new[] { typeof(Il2CppSystem.Nullable<NetworkId>), typeof(string), typeof(bool) })
                ?? throw new MissingMethodException("ChatBox.AddLine");
            _update = AccessTools.Method(typeof(ChatBox), nameof(ChatBox.Update), Type.EmptyTypes)
                ?? throw new MissingMethodException("ChatBox.Update");
            _getButtonDown = AccessTools.Method(typeof(NativeInput), nameof(NativeInput.GetButtonDown),
                new[] { typeof(NativeInput.Action) })
                ?? throw new MissingMethodException("InputSystem.GetButtonDown(Action)");
            _patches.Patch(_addLine, prefix: Hook(nameof(BeforeAddLine)), postfix: Hook(nameof(AfterAddLine)));
            _patches.Patch(_getButtonDown, postfix: Hook(nameof(AfterGetButtonDown)));
            _patches.Patch(_update, prefix: Hook(nameof(BeforeChatUpdate)), finalizer: Hook(nameof(FinishChatUpdate)));
            RefreshContext();
        }
        catch (Exception error)
        {
            Fail(error);
            Stop();
        }
    }

    private static HarmonyMethod Hook(string name) => new(typeof(ClientTabletMessages), name)
        { priority = Priority.Last };

    internal void Update(bool suppressPopup)
    {
        if (!OwnerThread) return;
        if (_stopRequested) { Stop(); return; }
        if (!Ready) return;
        try
        {
            RefreshContext();
            SetPopupSuppressed(_box != null && suppressPopup && !ChatBox.IsChatOpen);
        }
        catch (Exception error) { Fail(error); }
    }

    internal bool Send(string text)
    {
        if (!Ready) return false;
        var attempted = false;
        try
        {
            RefreshContext();
            if (!_canSend || !IsCurrent(_box))
            {
                Status = "Chat is unavailable in this game.";
                return false;
            }
            if (!ValidMessage(text, MaximumLength))
            {
                Status = "Enter plain text on one line, up to " + MaximumLength + " characters.";
                return false;
            }
            // This exact native entry point retains SDK host commands and remote mute policy.
            // It has no delivery result, and a throwing call must never be retried here.
            attempted = true;
            _box!.SendLine(text);
            Status = "Submitted to game chat; delivery is not confirmed.";
            return true;
        }
        catch (Exception error)
        {
            Fail(error);
            Status = attempted
                ? "Chat submission ended with an error; delivery is unknown. It was not retried."
                : "Chat is unavailable; the message was not submitted.";
            return false;
        }
    }

    private void RefreshContext()
    {
        var current = CurrentBox();
        var scene = GameRuntime.State.SceneEventCount;
        if (!(_box is null && current is null) && (!Same(_box, current) || scene != _scene))
        {
            RestorePopup();
            _box = current;
            _scene = scene;
            _canSend = false;
            MaximumLength = 256;
            _sessionChanged();
            if (current != null)
            {
                Status = "Public game chat. Messages follow the server's chat rules.";
                ImportHistory(current);
            }
            else Status = "Chat is waiting for a multiplayer game.";
            RefreshSending();
            return;
        }
        RefreshSending();
    }

    private void RefreshSending()
    {
        _canSend = false;
        MaximumLength = 256;
        if (!IsCurrent(_box)) return;
        var input = _box!._input;
        if (!Live(input)) return;
        var nativeLimit = input.characterLimit;
        if (nativeLimit > 0) MaximumLength = Math.Min(MaximumLength, nativeLimit);
        _canSend = true;
    }

    private static ChatBox? CurrentBox()
    {
        if (!GameRuntime.State.IsInWorld || !BoltNetwork.isRunning) return null;
        var hud = HudGui._instance;
        if (!Live(hud)) return null;
        var current = hud.Chatbox;
        return Live(current) ? current : null;
    }

    private bool IsCurrent(ChatBox? box) => Ready && Live(box) && Same(box, CurrentBox())
        && _scene == GameRuntime.State.SceneEventCount;

    private static bool Live([NotNullWhen(true)] UnityEngine.Object? value) =>
        value is not null && !value.WasCollected && value;
    private static bool Same(UnityEngine.Object? first, UnityEngine.Object? second) =>
        Live(first) && Live(second) && first!.Pointer == second!.Pointer;

    private void ImportHistory(ChatBox box)
    {
        var rows = box._messageRows;
        if (rows == null || rows.WasCollected) return;
        var remaining = Math.Min(HistoryLimit, Math.Max(0, rows.Count));
        var enumerator = rows.GetEnumerator();
        try
        {
            while (remaining-- > 0 && enumerator.MoveNext())
            {
                var row = enumerator.Current;
                if (!Live(row)) continue;
                var name = row._name;
                var message = row._message;
                if (Live(message)) Publish(Live(name) ? name.text : "", message.text);
            }
        }
        finally { enumerator.Dispose(); }
    }

    private void Publish(string? name, string? message)
    {
        var plainName = DisplayText(name, 128).Trim().TrimEnd(':');
        var prefix = plainName.Length == 0 ? "" : plainName + ": ";
        var plainMessage = DisplayText(message, DisplayLimit - prefix.Length);
        if (plainMessage.Length != 0) _received(prefix + plainMessage);
    }

    private static string DisplayText(string? text, int maximum)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var length = Math.Min(text.Length, maximum);
        if (length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
        var result = new StringBuilder(length);
        for (var index = 0; index < length; index++)
        {
            var character = text[index];
            result.Append(char.IsControl(character) || character is '\u2028' or '\u2029' ? ' ' : character);
        }
        // Angle brackets remain literal: the tablet's display must keep richText disabled.
        return result.ToString();
    }

    private static bool ValidMessage(string? text, int maximum)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > maximum) return false;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (char.IsControl(character) || character is '\u2028' or '\u2029' or '<' or '>') return false;
            if (char.IsHighSurrogate(character))
            {
                if (++index == text.Length || !char.IsLowSurrogate(text[index])) return false;
            }
            else if (char.IsLowSurrogate(character)) return false;
        }
        return true;
    }

    private static void BeforeAddLine(ChatBox __instance, bool __runOriginal, out RowState? __state)
    {
        __state = null;
        var bridge = _active;
        if (!__runOriginal || bridge == null || !bridge.Ready) return;
        try
        {
            bridge.RefreshContext();
            if (bridge.IsCurrent(__instance)) __state = RowState.Read(__instance);
        }
        catch (Exception error) { bridge.Fail(error); }
    }

    private static void AfterAddLine(ChatBox __instance, Il2CppSystem.Nullable<NetworkId> __0,
        string __1, bool __2, bool __runOriginal, RowState? __state)
    {
        var bridge = _active;
        if (!__runOriginal || __state == null || bridge == null || !bridge.Ready) return;
        try
        {
            if (!bridge.IsCurrent(__instance)) return;
            var after = RowState.Read(__instance);
            if (after.Count == 0 || (__state.Id == after.Id && __state.Count == after.Count
                && __state.LastRow == after.LastRow)) return;
            var name = "";
            if (!__2 && __0 != null && __0.HasValue)
            {
                var id = __0.Value;
                if (id.Packed == __instance._localPlayerId.Packed) name = ChatBox.YouName;
                else
                {
                    var players = __instance._players;
                    if (players != null && !players.WasCollected && players.TryGetValue(id, out var player)
                        && player != null && !player.WasCollected) name = player._name;
                }
            }
            bridge.Publish(name, __1);
        }
        catch (Exception error) { bridge.Fail(error); }
    }

    private static void BeforeChatUpdate(ChatBox __instance, bool __runOriginal, out InputScope? __state)
    {
        __state = _inputScope;
        _inputScope = null;
        var bridge = _active;
        if (!__runOriginal || bridge == null || !bridge.Ready) return;
        try
        {
            bridge.RefreshContext();
            if (!bridge.IsCurrent(__instance)) return;
            if (ChatBox.IsChatOpen)
            {
                bridge.RestorePopup();
                return;
            }
            if (NativeInput.GetButtonDown(NativeInput.Actions.OpenChat) && bridge._requestOpen()
                && bridge.IsCurrent(__instance))
                _inputScope = new InputScope(bridge, __instance.Pointer);
        }
        catch (Exception error) { bridge.Fail(error); }
    }

    private static void AfterGetButtonDown(NativeInput.Action __0, ref bool __result)
    {
        var scope = _inputScope;
        if (scope == null || !__result || !scope.Owner.Ready) return;
        try
        {
            if (__0 is not null && __0.Equals(NativeInput.Actions.OpenChat)
                && scope.Owner.IsCurrent(scope.Owner._box) && scope.Owner._box!.Pointer == scope.Box)
                __result = false;
        }
        catch (Exception error) { scope.Owner.Fail(error); }
    }

    private static Exception? FinishChatUpdate(Exception? __exception, InputScope? __state)
    {
        _inputScope = __state;
        var bridge = _active;
        if (bridge != null && bridge.Ready)
        {
            try
            {
                if (ChatBox.IsChatOpen) bridge.RestorePopup();
            }
            catch (Exception error) { bridge.Fail(error); }
        }
        return __exception;
    }

    private void SetPopupSuppressed(bool suppress)
    {
        var container = suppress && IsCurrent(_box) ? _box!._messages : null;
        if (!Live(container)) { RestorePopup(); return; }
        if (!Same(container, _popupContainer) || !Live(_popupGroup))
        {
            RestorePopup();
            var existing = container!.GetComponents<CanvasGroup>();
            CanvasGroup? group = null;
            try { group = container.AddComponent<CanvasGroup>(); }
            catch (Exception) when (existing.Length > 0) { group = existing[0]; }
            if (!Live(group) && existing.Length > 0) group = existing[0];
            if (!Live(group)) throw new InvalidOperationException("The chat popup cannot be hidden reversibly.");
            var owned = true;
            foreach (var previous in existing)
                if (Same(group, previous)) { owned = false; break; }
            var alpha = owned ? 1 : group!.alpha;
            var enabled = owned || group!.enabled;
            var interactable = owned || group!.interactable;
            var raycasts = owned || group!.blocksRaycasts;
            _ownsPopupGroup = owned;
            _popupGroup = group;
            _popupContainer = container;
            _popupAlpha = alpha;
            _popupEnabled = enabled;
            _popupInteractable = interactable;
            _popupRaycasts = raycasts;
        }
        _popupGroup!.alpha = 0;
        _popupGroup.interactable = false;
        _popupGroup.blocksRaycasts = false;
        _popupGroup.enabled = true;
    }

    private void RestorePopup()
    {
        var group = _popupGroup;
        if (Live(group))
        {
            // Make an owned group inert immediately; Unity destruction is deferred to frame end.
            group!.enabled = !_ownsPopupGroup && _popupEnabled;
            group.alpha = _ownsPopupGroup ? 1 : _popupAlpha;
            group.interactable = _ownsPopupGroup || _popupInteractable;
            group.blocksRaycasts = _ownsPopupGroup || _popupRaycasts;
            if (_ownsPopupGroup) UnityEngine.Object.Destroy(group);
        }
        _popupGroup = null;
        _popupContainer = null;
    }

    private void Fail(Exception error)
    {
        _failed = true;
        _canSend = false;
        _inputScope = null;
        try { RestorePopup(); }
        catch (Exception restoreError) { Warn("Restoring the native chat popup failed", restoreError); }
        Status = "Tablet messages are unavailable; use the game's chat.";
        Warn("Messages kept the game's chat behavior after an error", error);
    }

    internal void Stop()
    {
        if (_started && !OwnerThread) { _stopRequested = true; return; }
        _started = false;
        _canSend = false;
        if (ReferenceEquals(_active, this)) _active = null;
        if (_inputScope?.Owner == this) _inputScope = null;
        try { RestorePopup(); }
        catch (Exception error) { Warn("Restoring the native chat popup failed", error); }
        _box = null;
        foreach (var method in new[] { _addLine, _getButtonDown, _update })
        {
            if (method == null) continue;
            try { _patches.Unpatch(method, HarmonyPatchType.All, _patches.Id); }
            catch (Exception error) { Warn("Removing a passive chat hook failed", error); }
        }
        _addLine = _getButtonDown = _update = null;
    }

    public void Dispose() => Stop();

    private static void Warn(string message, Exception error)
    {
        try { Debug.LogWarning("Modstoke Client: " + message + " (" + error.GetType().Name + ")."); }
        catch (Exception) { }
    }

    private sealed record InputScope(ClientTabletMessages Owner, IntPtr Box);

    private sealed record RowState(int Id, int Count, IntPtr LastRow)
    {
        internal static RowState Read(ChatBox box)
        {
            var rows = box._messageRows;
            if (rows == null || rows.WasCollected) return new RowState(box._lastMessageId, 0, IntPtr.Zero);
            var count = rows.Count;
            var last = IntPtr.Zero;
            // Native history is small. Never allocate or walk an unbounded native queue.
            if (count <= HistoryLimit)
            {
                var enumerator = rows.GetEnumerator();
                try
                {
                    var remaining = Math.Max(0, count);
                    while (remaining-- > 0 && enumerator.MoveNext())
                    {
                        var row = enumerator.Current;
                        if (Live(row)) last = row.Pointer;
                    }
                }
                finally { enumerator.Dispose(); }
            }
            return new RowState(box._lastMessageId, count, last);
        }
    }
}
