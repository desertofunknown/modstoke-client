// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using Bolt;
using Endnight.Extensions;
using Modstoke.Sons;
using Sons.Save;
using SonsSdk.Networking;

namespace Modstoke.Client;

internal enum ClientTabletPresencePhase : byte { Hidden, Drawing, Open, Holstering }
internal enum ClientWatchPresencePhase : byte { Hidden, Worn, Inspecting }

// Transient presentation only. Screen frames use a separate bounded channel;
// input coordinates and gameplay commands never cross either presentation channel.
internal sealed partial class ClientTabletPresence
{
    private const int MaxPeers = 16;
    private const long Lifetime = 2500;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly Dictionary<Guid, Peer> _peers = new();
    private readonly Dictionary<ulong, Remote> _remotes = new();
    private readonly Rate _ingress = new();
    private StateRow[] _stateSnapshot = Array.Empty<StateRow>();
    private bool _stateSnapshotInUse;
    private PresenceEvent? _event;
    private IDisposable? _frame;
    private bool _started, _loading, _failed, _failureLogged;
    private int _mode;
    private IntPtr _manager, _serverPointer;
    private uint _serverConnection;
    private Guid _epoch, _nonce, _token;
    private long _nextHello, _nextSend, _nextSnapshot, _lastSnapshot, _sequence, _receivedSequence;
    private ClientTabletPresencePhase _localPhase, _sentPhase;
    private ClientWatchPresencePhase _localWatchPhase, _sentWatchPhase;
    private long _localTouchedUntil, _localWatchTouchedUntil;
    private bool _sentTouch, _sentWatchTouch, _localController, _sentController;
    private Peer? _host;
    private static long Now => Environment.TickCount64;
    private bool OnThread => Environment.CurrentManagedThreadId == _thread;

    internal void Start()
    {
        if (!OnThread || _started || _failed) return;
        try
        {
            if (!Packets.SupportsClientRelayPolicy) return;
            _event = new PresenceEvent(this);
            Packets.Register(_event);
            _started = true;
            StartScreens();
            StartAppearance();
            GameRuntime.WorldChanged += WorldChanged;
            SaveLifecycle.Lifecycle += SaveChanged;
            _frame = ClientTabletPresenceFrameUpdate.TryInstall(() =>
            {
                LateUpdate();
                Plugin.Aircraft.LateUpdate();
                Plugin.Spacecraft.LateUpdate();
            }, Fail, out var failure);
            if (_frame == null) Fail(new InvalidOperationException(failure));
        }
        catch (Exception error) { Fail(error); }
    }

    internal void SetLocalState(ClientTabletPresencePhase phase, ClientWatchPresencePhase watchPhase, bool controller = false)
    {
        if (!OnThread || phase > ClientTabletPresencePhase.Holstering || watchPhase > ClientWatchPresencePhase.Inspecting) return;
        _localPhase = phase;
        _localController = phase != ClientTabletPresencePhase.Hidden && controller;
        _localWatchPhase = phase != ClientTabletPresencePhase.Hidden && watchPhase == ClientWatchPresencePhase.Inspecting
            ? ClientWatchPresencePhase.Worn : watchPhase;
        if (phase != ClientTabletPresencePhase.Open) _localTouchedUntil = 0;
        if (_localWatchPhase != ClientWatchPresencePhase.Inspecting) _localWatchTouchedUntil = 0;
    }

    internal void NotifyInteraction()
    {
        if (OnThread && _localPhase == ClientTabletPresencePhase.Open) _localTouchedUntil = Now + 450;
    }

    internal void NotifyWatchInteraction()
    {
        if (OnThread && _localWatchPhase == ClientWatchPresencePhase.Inspecting) _localWatchTouchedUntil = Now + 450;
    }

    internal void Update()
    {
        if (!OnThread || !_started) return;
        try { UpdateCore(); }
        catch (Exception error) { Fail(error); }
    }

    private void UpdateCore()
    {
        // Remove our last rendered pose before the native animation/IK update begins.
        foreach (var remote in _remotes.Values) remote.Pose?.Restore();
        if (!RefreshContext()) return;
        var now = Now;
        if (_mode == 1 && now >= _nextSnapshot)
        {
            _nextSnapshot = now + 200;
            foreach (var peer in _peers.Values.ToArray())
            {
                if (!Current(peer) || now - peer.Seen > 10000) { _peers.Remove(peer.Session!.SessionId); continue; }
                RefreshBinding(peer);
                if (now - peer.StateAt > Lifetime) peer.HideDevices();
            }
            _host ??= new Peer();
            RefreshBinding(_host);
            _host.Phase = _host.Binding == null ? ClientTabletPresencePhase.Hidden : _localPhase;
            _host.Controller = _host.Phase != ClientTabletPresencePhase.Hidden && _localController;
            _host.Touch = _localTouchedUntil > now;
            _host.WatchPhase = _host.Binding == null ? ClientWatchPresencePhase.Hidden : _localWatchPhase;
            _host.WatchTouch = _localWatchTouchedUntil > now;
            _host.StateAt = now;
            BroadcastStates(now);
        }
        else if (_mode == 2)
        {
            if (now - _lastSnapshot > Lifetime && _token != Guid.Empty)
            {
                ClearRemotes();
                _token = Guid.Empty;
                _nonce = Guid.NewGuid();
                _receivedSequence = 0;
            }
            if (_token == Guid.Empty && now >= _nextHello)
            { _nextHello = now + 1000; _event?.Hello(); }
            if (_token != Guid.Empty && now >= _nextSend)
            {
                var touch = _localPhase == ClientTabletPresencePhase.Open && now < _localTouchedUntil;
                var watchTouch = _localWatchPhase == ClientWatchPresencePhase.Inspecting && now < _localWatchTouchedUntil;
                if (_localPhase != _sentPhase || touch != _sentTouch || _localWatchPhase != _sentWatchPhase
                    || watchTouch != _sentWatchTouch || _localController != _sentController || now - _lastSend >= 500)
                {
                    _event?.State(++_sequence, _localPhase, touch, _localWatchPhase, watchTouch, _localController);
                    _sentPhase = _localPhase; _sentTouch = touch; _lastSend = now;
                    _sentWatchPhase = _localWatchPhase; _sentWatchTouch = watchTouch;
                    _sentController = _localController;
                }
                _nextSend = now + 100;
            }
        }
        if (_frame != null)
            foreach (var remote in _remotes.Values) remote.Refresh(now);
        UpdateScreens(now);
        UpdateAppearance(now);
    }

    private long _lastSend;

    // Installed after the complete native PreLateUpdate phase; no local hand-pose singleton is borrowed.
    internal void LateUpdate()
    {
        if (!OnThread || !_started || _loading) return;
        try
        {
            foreach (var remote in _remotes.Values) remote.Render(Now);
            Plugin.Terminal.UpdateSharedScreenCapture(SharedScreenWanted);
        }
        catch (Exception error) { Fail(error); }
    }

    internal void Stop()
    {
        if (!OnThread) return;
        Exception? failure = null;
        try
        {
            if (_started && _mode == 2 && _token != Guid.Empty && BoltNetwork.isRunning)
                _event?.State(++_sequence, ClientTabletPresencePhase.Hidden, false, ClientWatchPresencePhase.Hidden, false, false);
        }
        catch (Exception error) { failure = error; }
        Retire(ref failure);
        if (failure != null) { _failed = true; ReportFailure(failure); }
    }

    private void Fail(Exception error)
    {
        _failed = true;
        Exception? failure = error;
        Retire(ref failure);
        ReportFailure(error);
    }

    private void Retire(ref Exception? failure)
    {
        // Detach callbacks before cleanup; every resource is attempted even if one native object expired.
        _started = false;
        Cleanup(StopScreens, ref failure);
        Cleanup(StopAppearance, ref failure);
        Cleanup(() => GameRuntime.WorldChanged -= WorldChanged, ref failure);
        Cleanup(() => SaveLifecycle.Lifecycle -= SaveChanged, ref failure);
        var frame = _frame; _frame = null;
        var netEvent = _event; _event = null;
        if (frame != null) Cleanup(frame.Dispose, ref failure);
        if (netEvent != null) Cleanup(() => Packets.Unregister(netEvent), ref failure);
        Cleanup(Reset, ref failure);
        _loading = false;
    }

    private void ReportFailure(Exception error)
    {
        if (_failureLogged) return;
        _failureLogged = true;
        try { UnityEngine.Debug.LogWarning("Modstoke Client: remote device cosmetics stopped (" + error.GetType().Name + "): " + error.Message); }
        catch (Exception) { } // Logging cannot turn an optional presentation failure into a Client failure.
    }

    private static void Cleanup(Action action, ref Exception? failure)
    { try { action(); } catch (Exception error) { failure ??= error; } }

    private bool RefreshContext()
    {
        if (_loading || !BoltNetwork.isRunning || GameRuntime.IsTitleScene
            || !SaveGameManager.TryGetInstance(out var manager) || manager == null || manager.WasCollected || !manager
            || manager._loadingInProgress)
        { if (_mode != 0) Reset(); return false; }
        var mode = BoltNetwork.isServer ? 1 : 2;
        var server = mode == 2 ? BoltNetwork.server : null;
        if (mode == 2 && (server == null || server.WasCollected)) { Reset(); return false; }
        if (_mode != mode || _manager != manager.Pointer || mode == 2
            && (_serverPointer != server!.Pointer || _serverConnection != server.ConnectionId))
        {
            Reset();
            _mode = mode; _manager = manager.Pointer;
            _epoch = mode == 1 ? Guid.NewGuid() : Guid.Empty;
            _nonce = Guid.NewGuid();
            if (server != null) { _serverPointer = server.Pointer; _serverConnection = server.ConnectionId; }
        }
        return true;
    }

    private void Reset()
    {
        Exception? failure = null;
        Cleanup(ResetScreens, ref failure);
        Cleanup(ClearAppearance, ref failure);
        Cleanup(ClearRemotes, ref failure);
        _peers.Clear(); _host = null;
        _mode = 0; _manager = _serverPointer = IntPtr.Zero; _serverConnection = 0;
        _epoch = _nonce = _token = Guid.Empty;
        _nextHello = _nextSend = _nextSnapshot = _lastSnapshot = _lastSend = _sequence = _receivedSequence = 0;
        _localPhase = _sentPhase = ClientTabletPresencePhase.Hidden; _localTouchedUntil = 0; _sentTouch = false;
        _localWatchPhase = _sentWatchPhase = ClientWatchPresencePhase.Hidden; _localWatchTouchedUntil = 0; _sentWatchTouch = false;
        _localController = _sentController = false;
        if (failure != null) throw failure;
    }

    private void ClearRemotes()
    {
        Exception? failure = null;
        foreach (var remote in _remotes.Values) Cleanup(remote.Dispose, ref failure);
        _remotes.Clear();
        if (failure != null) throw failure;
    }

    private void WorldChanged(WorldEvent value)
    {
        if (!OnThread || !_started) return;
        try { if (!value.IsInWorld && GameRuntime.IsTitleScene) Reset(); }
        catch (Exception error) { Fail(error); }
    }

    private void SaveChanged(SaveLifecycleEvent value)
    {
        if (!OnThread || !_started || value.Operation != "Load") return;
        try
        {
            if (value.Phase == "Before") { Reset(); _loading = true; }
            else if (value.Phase == "After") _loading = false;
        }
        catch (Exception error) { Fail(error); }
    }

    private bool Current(Peer peer)
    {
        if (_mode != 1 || !BoltNetwork.isRunning || !BoltNetwork.isServer) return false;
        if (peer.Session == null) return ReferenceEquals(peer, _host);
        if (!peer.Session.IsCurrent
            || !ServerSessions.TryReadGameSteamId(peer.Session, out var steamId, out _)
            || steamId != peer.SteamId) return false;
        return ClientConnections.Contains(peer.Pointer, peer.Connection);
    }

    private WorldReadStatus RefreshBinding(Peer peer)
    {
        if (peer.Binding != null && ServerWorld.TryValidatePlayerBinding(peer.Binding, out _))
            return WorldReadStatus.Success;
        peer.Binding = null;
        peer.HideDevices();
        PlayerHandle binding;
        WorldReadStatus status;
        var captured = peer.Session == null
            ? ServerWorld.TryCaptureLocalPlayerBinding(out binding, out status)
            : ServerWorld.TryCapturePlayerBinding(peer.Session, out binding, out status);
        if (captured) { peer.Binding = binding; peer.Generation = Guid.NewGuid(); }
        return status;
    }

    private void BroadcastStates(long now)
    {
        var host = _host;
        var capacity = _peers.Count + (host == null ? 0 : 1);
        var ownsSnapshot = !_stateSnapshotInUse;
        var rows = ownsSnapshot ? _stateSnapshot : new StateRow[capacity];
        if (rows.Length < capacity)
        {
            rows = new StateRow[capacity];
            if (ownsSnapshot) _stateSnapshot = rows;
        }
        if (ownsSnapshot) _stateSnapshotInUse = true;
        try
        {
            var count = 0;
            foreach (var peer in _peers.Values)
                if (TryReadState(peer, now, out var row)) rows[count++] = row;
            if (host != null && TryReadState(host, now, out var hostRow)) rows[count++] = hostRow;
            Apply(rows.AsSpan(0, count), now);
            var sequence = ++_sequence;
            foreach (var peer in _peers.Values) _event?.Snapshot(peer, sequence, rows, count);
        }
        finally
        {
            // Serialization copies rows before returning. A reentrant broadcast
            // uses its own array until the outer application and sends finish.
            if (ownsSnapshot) _stateSnapshotInUse = false;
        }
    }

    private static bool TryReadState(Peer peer, long now, out StateRow row)
    {
        row = default;
        if (peer.Binding == null || peer.Phase == ClientTabletPresencePhase.Hidden && peer.WatchPhase == ClientWatchPresencePhase.Hidden
            || now - peer.StateAt > Lifetime
            || !ServerWorld.TryReadPlayerLife(peer.Binding, out var life, out _)
            || life.LifeState != PlayerLifeState.Alive) return false;
        row = new(peer.Binding.NetworkId, peer.Generation, peer.Phase, peer.Touch, peer.WatchPhase, peer.WatchTouch,
            peer.Controller);
        return true;
    }

    private void Apply(ReadOnlySpan<StateRow> states, long now)
    {
        var active = new HashSet<ulong>();
        foreach (var state in states)
        {
            active.Add(state.Actor);
            if (_remotes.TryGetValue(state.Actor, out var previous) && previous.Generation != state.Generation)
            { previous.Dispose(); _remotes.Remove(state.Actor); }
            if (!_remotes.TryGetValue(state.Actor, out var remote))
            { remote = new Remote(state.Actor, state.Generation); _remotes.Add(state.Actor, remote); }
            remote.Set(state.Phase, state.Touch, state.WatchPhase, state.WatchTouch, state.Controller, now);
        }
        foreach (var pair in _remotes.ToArray())
            if (!active.Contains(pair.Key) && !pair.Value.Retire(now)) _remotes.Remove(pair.Key);
    }

    private sealed class Peer
    {
        internal ServerSession? Session;
        internal PlayerHandle? Binding;
        internal ulong SteamId;
        internal IntPtr Pointer;
        internal uint Connection;
        internal Guid Nonce, Token = Guid.NewGuid(), Generation;
        internal long Seen, StateAt, Sequence;
        internal ClientTabletPresencePhase Phase;
        internal ClientWatchPresencePhase WatchPhase;
        internal bool Touch, WatchTouch, Controller;
        internal readonly Rate Packets = new();

        internal void HideDevices()
        {
            Phase = ClientTabletPresencePhase.Hidden;
            WatchPhase = ClientWatchPresencePhase.Hidden;
            Touch = WatchTouch = Controller = false;
        }
    }

    private readonly record struct StateRow(ulong Actor, Guid Generation, ClientTabletPresencePhase Phase, bool Touch,
        ClientWatchPresencePhase WatchPhase, bool WatchTouch, bool Controller);

    private sealed class Rate
    {
        private double _tokens;
        private long _at;
        internal bool Take(long now, double rate, double capacity)
        {
            _tokens = _at == 0 ? capacity : System.Math.Min(capacity, _tokens + System.Math.Max(0, now - _at) * rate / 1000);
            _at = now;
            if (_tokens < 1) return false;
            _tokens--; return true;
        }
    }
}
