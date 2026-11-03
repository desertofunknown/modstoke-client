// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Bolt;
using Modstoke.Sons;
using SonsSdk.Networking;
using TheForest.Utils;
using UdpKit;
using UnityEngine;
using Math = System.Math;

namespace Modstoke.Client;

internal sealed partial class ClientTabletPresence
{
    private const int ScreenChunkBytes = 512, ScreenMaximumBytes = 24576;
    private const long ScreenSubscriptionLifetime = 5000, ScreenDemandLifetime = 3000;
    private const long ScreenAssemblyLifetime = 2000;
    private const float ScreenDistanceSquared = 12 * 12;
    private readonly Dictionary<Peer, ScreenPeer> _screenPeers = new();
    private readonly Dictionary<ulong, ScreenReceiver> _screenReceivers = new();
    private readonly Dictionary<ulong, ScreenFrame> _screenPending = new();
    private KeyValuePair<Peer, ScreenPeer>[] _screenPeerSnapshot = Array.Empty<KeyValuePair<Peer, ScreenPeer>>();
    private KeyValuePair<ulong, ScreenReceiver>[] _screenReceiverSnapshot = Array.Empty<KeyValuePair<ulong, ScreenReceiver>>();
    private KeyValuePair<ulong, ScreenFrame>[] _screenPendingSnapshot = Array.Empty<KeyValuePair<ulong, ScreenFrame>>();
    private bool _screenPeerSnapshotInUse, _screenReceiverSnapshotInUse, _screenPendingSnapshotInUse;
    private readonly List<ulong> _screenOrder = new();
    private readonly HashSet<(Guid Token, Guid Generation, string Operation)> _screenFailures = new();
    private ScreenEvent? _screenEvent;
    private ScreenTransfer? _screenActive;
    private Rate _screenIngress = new(), _screenEgress = new();
    private Guid _screenEpoch, _screenToken, _screenDemandGeneration;
    private long _screenNextSubscribe, _screenSubscribeStarted, _screenDemandAt, _screenDemandSequence;
    private long _screenControlSequence, _screenLocalSequence;
    private int _screenReceiverCursor;
    private bool _screenDemand, _screenWanted, _screenFailed, _screenFailureLogged;

    internal bool SharedScreenWanted => _screenWanted && !_screenFailed && _started && !_loading;

    private void StartScreens()
    {
        if (_screenFailed || _screenEvent != null) return;
        try
        {
            _screenEvent = new ScreenEvent(this);
            Packets.Register(_screenEvent);
        }
        catch (Exception error) { FailScreens(error); }
    }

    private void StopScreens()
    {
        var channel = _screenEvent;
        _screenEvent = null;
        Exception? failure = null;
        if (channel != null) Cleanup(() => Packets.Unregister(channel), ref failure);
        Cleanup(ClearScreens, ref failure);
        if (failure != null) FailScreens(failure);
    }

    private void ResetScreens()
    {
        try { ClearScreens(); }
        catch (Exception error) { FailScreens(error); }
    }

    private void ClearScreens()
    {
        _screenWanted = _screenDemand = false;
        _screenEpoch = _screenToken = _screenDemandGeneration = Guid.Empty;
        _screenNextSubscribe = _screenSubscribeStarted = _screenDemandAt = _screenDemandSequence = 0;
        _screenControlSequence = _screenLocalSequence = 0;
        _screenReceiverCursor = 0;
        _screenActive = null;
        _screenPeers.Clear();
        _screenPending.Clear();
        _screenOrder.Clear();
        _screenFailures.Clear();
        _screenIngress = new Rate();
        _screenEgress = new Rate();
        Exception? failure = null;
        foreach (var receiver in _screenReceivers.Values) Cleanup(receiver.Remote.ClearScreen, ref failure);
        _screenReceivers.Clear();
        // A reset must not clear snapshots still owned by an outer pruning call.
        if (!_screenPeerSnapshotInUse) _screenPeerSnapshot = Array.Empty<KeyValuePair<Peer, ScreenPeer>>();
        if (!_screenReceiverSnapshotInUse) _screenReceiverSnapshot = Array.Empty<KeyValuePair<ulong, ScreenReceiver>>();
        if (!_screenPendingSnapshotInUse) _screenPendingSnapshot = Array.Empty<KeyValuePair<ulong, ScreenFrame>>();
        Cleanup(Plugin.Terminal.ReleaseSharedScreenCapture, ref failure);
        if (failure != null) throw failure;
    }

    private void FailScreens(Exception error)
    {
        _screenFailed = true;
        _screenWanted = false;
        var channel = _screenEvent;
        _screenEvent = null;
        Exception? ignored = null;
        if (channel != null) Cleanup(() => Packets.Unregister(channel), ref ignored);
        Cleanup(ClearScreens, ref ignored);
        if (_screenFailureLogged) return;
        _screenFailureLogged = true;
        try { Debug.LogWarning("Modstoke Client: shared tablet screens stopped (" + error.GetType().Name + "): " + error.Message); }
        catch (Exception) { }
    }

    private void ReportScreenUnavailable(Peer? peer, string operation, WorldReadStatus? status = null,
        int? dyingState = null)
    {
        // Keep diagnostics bounded even if attachments repeatedly change within one screen context.
        if (_screenFailures.Count >= 128 || !_screenFailures.Add((peer?.Token ?? _token,
            peer?.Generation ?? _screenDemandGeneration, operation))) return;
        try
        {
            var actor = peer == null ? "local client" : ReferenceEquals(peer, _host)
                ? "host" : "connection " + peer.Connection;
            Debug.LogWarning("Modstoke shared tablet screen unavailable (" + actor + "): " + operation
                + (status.HasValue ? " [" + status.Value + "]" : "")
                + (dyingState.HasValue ? " [Dying=" + dyingState.Value + "]" : "") + ".");
        }
        catch (Exception) { }
    }

    private bool ScreenContext()
    {
        if (_screenFailed || _screenEvent == null || !_started || _loading || _mode == 0
            || !BoltNetwork.isRunning || _epoch == Guid.Empty || _mode == 2 && _token == Guid.Empty)
            return false;
        if (_screenEpoch != _epoch || _screenToken != _token)
        {
            ClearScreens();
            _screenEpoch = _epoch;
            _screenToken = _token;
        }
        return true;
    }

    private void UpdateScreens(long now)
    {
        if (!OnThread || _screenFailed || _screenEvent == null) return;
        try
        {
            if (!ScreenContext())
            {
                if (_screenEpoch != Guid.Empty || _screenWanted) ClearScreens();
                return;
            }
            PruneScreens(now);
            var sends = 0;
            if (_mode == 2)
            {
                if (now >= _screenNextSubscribe && TakeScreenSend(now, ref sends))
                {
                    _screenEvent.Subscribe();
                    if (_screenSubscribeStarted == 0) _screenSubscribeStarted = now;
                    _screenNextSubscribe = now + 2000;
                }
                var responseAt = _screenDemandAt == 0 ? _screenSubscribeStarted : _screenDemandAt;
                if (responseAt != 0 && now - responseAt > ScreenSubscriptionLifetime)
                    ReportScreenUnavailable(null, "host demand response is overdue after screen subscriptions");
                _screenWanted = GuestScreenWanted(now);
            }
            else
            {
                foreach (var pair in _screenPeers)
                {
                    var state = pair.Value;
                    if (!ScreenSubscribed(pair.Key, now) || now < state.NextDemand
                        || !TakeScreenSend(now, ref sends)) continue;
                    state.Demand = ScreenSource(pair.Key, now, out var position)
                        && HasScreenViewer(pair.Key, position, now);
                    state.DemandAt = now;
                    state.NextDemand = now + 1000;
                    _screenEvent.Demand(pair.Key, ++_screenControlSequence, state.Demand);
                }
                _screenWanted = _localPhase == ClientTabletPresencePhase.Open && _host != null
                    && ScreenSource(_host, now, out var hostPosition)
                    && HasScreenViewer(_host, hostPosition, now);
            }

            // Capture runs later in the normal frame path. This only takes its latest completed JPEG.
            var captured = Plugin.Terminal.TakeSharedScreenFrame();
            if (_screenWanted && captured != null && ClientTabletScreenFrame.IsValid(captured))
            {
                var actor = _mode == 1 ? _host!.Binding!.NetworkId : 0;
                var generation = _mode == 1 ? _host!.Generation : _screenDemandGeneration;
                QueueScreen(new ScreenFrame(actor, generation, ++_screenLocalSequence, captured, now,
                    _mode == 1 ? _host : null));
            }
            SendScreens(now, ref sends);
        }
        catch (Exception error) { FailScreens(error); }
    }

    private bool GuestScreenWanted(long now) => _token != Guid.Empty && _screenDemand
        && now - _screenDemandAt <= ScreenDemandLifetime && _localPhase == ClientTabletPresencePhase.Open;

    private bool TakeScreenSend(long now, ref int sends)
    {
        if (sends >= 8 || !_screenEgress.Take(now, _mode == 1 ? 128 : 64, 8)) return false;
        sends++;
        return true;
    }

    private void PruneScreens(long now)
    {
        PruneScreenPeers(now);
        PruneScreenReceivers(now);
        PrunePendingScreens(now);
        _screenOrder.RemoveAll(actor => !_screenPending.ContainsKey(actor));
    }

    private void PruneScreenPeers(long now)
    {
        var ownsSnapshot = !_screenPeerSnapshotInUse;
        // Stale entries can outlive admission, so the player limit is not a snapshot bound.
        if (ownsSnapshot && _screenPeerSnapshot.Length < _screenPeers.Count)
            _screenPeerSnapshot = new KeyValuePair<Peer, ScreenPeer>[_screenPeers.Count];
        var peers = ownsSnapshot ? _screenPeerSnapshot : _screenPeers.ToArray();
        var count = ownsSnapshot ? 0 : peers.Length;
        if (ownsSnapshot) _screenPeerSnapshotInUse = true;
        try
        {
            if (ownsSnapshot)
            {
                foreach (var pair in _screenPeers)
                {
                    peers[count] = pair;
                    count++;
                }
            }
            for (var i = 0; i < count; i++)
            {
                var pair = peers[i];
                var peer = pair.Key;
                var state = pair.Value;
                if (!Current(peer) || state.Token != peer.Token || state.Generation != peer.Generation)
                {
                    RemoveScreenPeer(peer, state);
                    continue;
                }
                state.Assembly.Expire(now);
                if (peer.Phase != ClientTabletPresencePhase.Open || now - peer.StateAt > Lifetime
                    || now - state.Subscribed > ScreenSubscriptionLifetime)
                    state.Assembly.Discard();
            }
        }
        finally
        {
            Array.Clear(peers, 0, count);
            if (ownsSnapshot) _screenPeerSnapshotInUse = false;
        }
    }

    private void PruneScreenReceivers(long now)
    {
        var ownsSnapshot = !_screenReceiverSnapshotInUse;
        if (ownsSnapshot && _screenReceiverSnapshot.Length < _screenReceivers.Count)
            _screenReceiverSnapshot = new KeyValuePair<ulong, ScreenReceiver>[_screenReceivers.Count];
        var receivers = ownsSnapshot ? _screenReceiverSnapshot : _screenReceivers.ToArray();
        var count = ownsSnapshot ? 0 : receivers.Length;
        if (ownsSnapshot) _screenReceiverSnapshotInUse = true;
        try
        {
            if (ownsSnapshot)
            {
                foreach (var pair in _screenReceivers)
                {
                    receivers[count] = pair;
                    count++;
                }
            }
            for (var i = 0; i < count; i++)
            {
                var pair = receivers[i];
                var receiver = pair.Value;
                receiver.Assembly.Expire(now);
                if (!_remotes.TryGetValue(pair.Key, out var remote) || !ReferenceEquals(remote, receiver.Remote))
                {
                    receiver.Remote.ClearScreen();
                    _screenReceivers.Remove(pair.Key);
                    continue;
                }
                if (receiver.Delivered != 0 && (!remote.CanShareScreen || now - receiver.Delivered > ScreenDemandLifetime))
                { receiver.Remote.ClearScreen(); receiver.Delivered = 0; }
                if (!remote.CanShareScreen) receiver.Assembly.Discard();
            }
        }
        finally
        {
            Array.Clear(receivers, 0, count);
            if (ownsSnapshot) _screenReceiverSnapshotInUse = false;
        }
    }

    private void PrunePendingScreens(long now)
    {
        var ownsSnapshot = !_screenPendingSnapshotInUse;
        if (ownsSnapshot && _screenPendingSnapshot.Length < _screenPending.Count)
            _screenPendingSnapshot = new KeyValuePair<ulong, ScreenFrame>[_screenPending.Count];
        var pending = ownsSnapshot ? _screenPendingSnapshot : _screenPending.ToArray();
        var count = ownsSnapshot ? 0 : pending.Length;
        if (ownsSnapshot) _screenPendingSnapshotInUse = true;
        try
        {
            if (ownsSnapshot)
            {
                foreach (var pair in _screenPending)
                {
                    pending[count] = pair;
                    count++;
                }
            }
            for (var i = 0; i < count; i++)
            {
                var pair = pending[i];
                if (!ScreenFrameCurrent(pair.Value, now, out _) || now - pair.Value.Created > ScreenAssemblyLifetime)
                    _screenPending.Remove(pair.Key);
            }
        }
        finally
        {
            Array.Clear(pending, 0, count);
            if (ownsSnapshot) _screenPendingSnapshotInUse = false;
        }
    }

    private void RemoveScreenPeer(Peer peer, ScreenPeer state)
    {
        _screenPeers.Remove(peer);
        if (_screenReceivers.TryGetValue(state.Actor, out var receiver) && receiver.Remote.Generation == state.Generation)
        {
            receiver.Remote.ClearScreen();
            _screenReceivers.Remove(state.Actor);
        }
    }

    private bool ScreenSubscribed(Peer peer, long now) => _screenPeers.TryGetValue(peer, out var state)
        && state.Token == peer.Token && state.Generation == peer.Generation
        && now - state.Subscribed <= ScreenSubscriptionLifetime && Current(peer);

    private PlayerHandle? ScreenBinding(Peer peer)
    {
        if (!Current(peer)) return null;
        var binding = peer.Binding;
        if (binding == null)
            ReportScreenUnavailable(peer, "player binding is missing");
        return binding;
    }

    private bool ScreenPosition(Peer peer, out Vector3 position)
    {
        position = default;
        var binding = ScreenBinding(peer);
        if (binding == null) return false;
        if (!ServerWorld.TryReadPlayerBinding(binding, out var player, out var status))
        {
            ReportScreenUnavailable(peer, "player binding could not be read", status);
            return false;
        }
        return ScreenPosition(peer, player, out position);
    }

    private bool ScreenPosition(Peer peer, EntitySnapshot player, out Vector3 position)
    {
        position = new Vector3(player.Position.X, player.Position.Y, player.Position.Z);
        if (float.IsFinite(position.x) && float.IsFinite(position.y) && float.IsFinite(position.z)) return true;
        ReportScreenUnavailable(peer, "player position is not finite");
        return false;
    }

    private bool ScreenSource(Peer peer, long now, out Vector3 position)
    {
        position = default;
        if (peer.Phase != ClientTabletPresencePhase.Open || now - peer.StateAt > Lifetime) return false;
        var binding = ScreenBinding(peer);
        if (binding == null) return false;
        if (!ServerWorld.TryReadPlayerLife(binding, out var life, out var status))
        {
            ReportScreenUnavailable(peer, "player life state could not be read", status);
            return false;
        }
        if (!ScreenPosition(peer, life.Entity, out position)) return false;
        if (!life.LifeState.HasValue)
            ReportScreenUnavailable(peer, "player life state is unknown", dyingState: life.RawDyingState);
        return life.LifeState == PlayerLifeState.Alive;
    }

    private bool HostScreenViewer(Peer source, Vector3 position)
    {
        if (_host == null || ReferenceEquals(source, _host) || !ScreenPosition(_host, out var viewer)) return false;
        var camera = LocalPlayer.MainCam;
        if (camera == null || camera.WasCollected || !camera) return false;
        var cameraPosition = camera.transform.position;
        return (viewer - position).sqrMagnitude <= ScreenDistanceSquared
            && (cameraPosition - position).sqrMagnitude <= ScreenDistanceSquared;
    }

    private List<Peer> ScreenTargets(Peer source, Vector3 position, long now)
    {
        var targets = new List<Peer>();
        foreach (var peer in _screenPeers.Keys)
            if (IsScreenTarget(source, peer, position, now)) targets.Add(peer);
        return targets;
    }

    private bool IsScreenTarget(Peer source, Peer target, Vector3 position, long now)
        => !ReferenceEquals(source, target) && ScreenSubscribed(target, now)
            && ScreenPosition(target, out var viewer) && (viewer - position).sqrMagnitude <= ScreenDistanceSquared;

    private bool HasScreenViewer(Peer source, Vector3 position, long now)
    {
        if (HostScreenViewer(source, position)) return true;
        // Demand needs only one eligible viewer. Build the complete recipient
        // list only when selecting a frame's round-robin transfer targets.
        foreach (var peer in _screenPeers.Keys)
            if (IsScreenTarget(source, peer, position, now)) return true;
        return false;
    }

    private bool ScreenFrameCurrent(ScreenFrame frame, long now, out Vector3 position)
    {
        position = default;
        if (_mode == 2) return GuestScreenWanted(now) && frame.Generation == _screenDemandGeneration;
        return frame.Source != null && frame.Source.Binding?.NetworkId == frame.Actor
            && frame.Source.Generation == frame.Generation && frame.Source.Token == frame.SourceToken
            && ScreenSource(frame.Source, now, out position);
    }

    private void QueueScreen(ScreenFrame frame)
    {
        if (!_screenPending.ContainsKey(frame.Actor) && !_screenOrder.Contains(frame.Actor))
            _screenOrder.Add(frame.Actor);
        _screenPending[frame.Actor] = frame;
    }

    private ScreenTransfer? NextScreen(long now)
    {
        while (_screenOrder.Count > 0)
        {
            var actor = _screenOrder[0];
            _screenOrder.RemoveAt(0);
            if (!_screenPending.Remove(actor, out var frame) || !ScreenFrameCurrent(frame, now, out var position)
                || now - frame.Created > ScreenAssemblyLifetime) continue;
            if (_mode == 2) return new ScreenTransfer(frame, Array.Empty<ScreenTarget>());
            var candidates = ScreenTargets(frame.Source!, position, now);
            if (candidates.Count == 0) continue;
            var count = Math.Min(4, candidates.Count);
            var targets = new ScreenTarget[count];
            for (var i = 0; i < count; i++)
            {
                var peer = candidates[(_screenReceiverCursor + i) % candidates.Count];
                targets[i] = new ScreenTarget(peer, peer.Token, peer.Generation);
            }
            _screenReceiverCursor = (_screenReceiverCursor + count) % candidates.Count;
            return new ScreenTransfer(frame, targets);
        }
        return null;
    }

    private void SendScreens(long now, ref int sends)
    {
        while (sends < 8)
        {
            _screenActive ??= NextScreen(now);
            if (_screenActive == null) return;
            var transfer = _screenActive;
            // Re-read the source for every chunk, including after selecting a new transfer.
            if (!ScreenFrameCurrent(transfer.Frame, now, out var source)
                || transfer.Started != 0 && now - transfer.Started > ScreenAssemblyLifetime)
            { _screenActive = null; continue; }
            Peer? target = null;
            if (_mode == 1)
            {
                if (transfer.Receiver >= transfer.Targets.Length) { _screenActive = null; continue; }
                var recipient = transfer.Targets[transfer.Receiver];
                target = recipient.Peer;
                if (recipient.Token != target.Token || recipient.Generation != target.Generation
                    || !ScreenSubscribed(target, now)
                    || !ScreenPosition(target, out var viewer) || (viewer - source).sqrMagnitude > ScreenDistanceSquared)
                { transfer.Receiver++; transfer.Chunk = 0; continue; }
            }
            if (!TakeScreenSend(now, ref sends)) return;
            if (transfer.Started == 0) transfer.Started = now;
            _screenEvent!.Chunk(target, transfer.Frame, transfer.Chunk);
            transfer.Chunk++;
            if (transfer.Chunk * ScreenChunkBytes >= transfer.Frame.Bytes.Length)
            {
                transfer.Chunk = 0;
                transfer.Receiver++;
                if (_mode == 2 || transfer.Receiver >= transfer.Targets.Length) _screenActive = null;
            }
        }
    }

    private void ReceiveScreen(ScreenMessage message, BoltConnection connection, long now)
    {
        if (message.Epoch != _epoch) return;
        if (_mode == 1)
        {
            var peer = _peers.Values.FirstOrDefault(value => value.Pointer == connection.Pointer
                && value.Connection == connection.ConnectionId && Current(value));
            if (peer == null || message.Token != peer.Token || peer.Binding == null) return;
            _screenPeers.TryGetValue(peer, out var state);
            if (state != null && (state.Token != peer.Token || state.Generation != peer.Generation))
            {
                RemoveScreenPeer(peer, state);
                state = null;
            }
            if (message.Kind == ScreenKind.Subscribe)
            {
                if (!ScreenPosition(peer, out _)) return;
                if (state == null)
                    _screenPeers.Add(peer, state = new ScreenPeer(peer.Token, peer.Generation, peer.Binding.NetworkId));
                if (state.Controls.Take(now, 2, 2)) state.Subscribed = now;
                return;
            }
            if (message.Kind != ScreenKind.Upload || state == null || !ScreenSubscribed(peer, now)
                || message.Generation != peer.Generation
                || !state.Chunks.Take(now, 96, 16) || !state.Demand || now - state.DemandAt > ScreenDemandLifetime
                || !ScreenSource(peer, now, out var source) || !HasScreenViewer(peer, source, now)) return;
            var bytes = state.Assembly.Accept(message, now);
            if (bytes == null || !ClientTabletScreenFrame.IsValid(bytes)) return;
            var actor = peer.Binding.NetworkId;
            if (HostScreenViewer(peer, source)) DeliverScreen(actor, peer.Generation, bytes, now);
            QueueScreen(new ScreenFrame(actor, peer.Generation, message.Sequence, bytes, now, peer));
            return;
        }
        if (message.Token != _token) return;
        if (message.Kind == ScreenKind.Demand)
        {
            if (message.Sequence <= _screenDemandSequence) return;
            _screenDemandSequence = message.Sequence;
            _screenDemand = message.Demand;
            _screenDemandAt = now;
            _screenDemandGeneration = message.Generation;
            return;
        }
        if (message.Kind != ScreenKind.Frame || !_remotes.TryGetValue(message.Actor, out var remote)
            || remote.Generation != message.Generation || !remote.CanShareScreen) return;
        var receiver = GetScreenReceiver(message.Actor, remote);
        var completed = receiver.Assembly.Accept(message, now);
        if (completed != null && ClientTabletScreenFrame.IsValid(completed))
        { remote.SetScreen(completed, now); receiver.Delivered = now; }
    }

    private ScreenReceiver GetScreenReceiver(ulong actor, Remote remote)
    {
        if (_screenReceivers.TryGetValue(actor, out var receiver) && ReferenceEquals(receiver.Remote, remote))
            return receiver;
        receiver?.Remote.ClearScreen();
        receiver = new ScreenReceiver(remote);
        _screenReceivers[actor] = receiver;
        return receiver;
    }

    private void DeliverScreen(ulong actor, Guid generation, byte[] bytes, long now)
    {
        if (!_remotes.TryGetValue(actor, out var remote) || remote.Generation != generation || !remote.CanShareScreen) return;
        var receiver = GetScreenReceiver(actor, remote);
        remote.SetScreen(bytes, now);
        receiver.Delivered = now;
    }

    private enum ScreenKind : byte { Subscribe = 1, Demand, Upload, Frame }

    private sealed class ScreenMessage
    {
        internal ScreenKind Kind;
        internal Guid Epoch, Token, Generation;
        internal long Sequence;
        internal ulong Actor;
        internal int Length, Chunk;
        internal bool Demand;
        // The input buffer stays local to receive; assembly owns the completed frame.
        internal ReadOnlyMemory<byte> Bytes;
    }

    private sealed class ScreenPeer(Guid token, Guid generation, ulong actor)
    {
        internal readonly Guid Token = token, Generation = generation;
        internal readonly ulong Actor = actor;
        internal readonly Rate Controls = new(), Chunks = new();
        internal readonly ScreenAssembly Assembly = new();
        internal long Subscribed, NextDemand, DemandAt;
        internal bool Demand;
    }

    private sealed class ScreenReceiver(Remote remote)
    {
        internal readonly Remote Remote = remote;
        internal readonly ScreenAssembly Assembly = new();
        internal long Delivered;
    }

    private sealed record ScreenFrame(ulong Actor, Guid Generation, long Sequence, byte[] Bytes, long Created, Peer? Source)
    {
        internal readonly Guid SourceToken = Source?.Token ?? Guid.Empty;
    }

    private sealed record ScreenTarget(Peer Peer, Guid Token, Guid Generation);

    private sealed class ScreenTransfer(ScreenFrame frame, ScreenTarget[] targets)
    {
        internal readonly ScreenFrame Frame = frame;
        internal readonly ScreenTarget[] Targets = targets;
        internal long Started;
        internal int Receiver, Chunk;
    }

    private sealed class ScreenAssembly
    {
        private long _sequence, _started;
        private byte[]? _bytes;
        private ulong _chunks;

        internal void Expire(long now)
        {
            if (_bytes != null && now - _started > ScreenAssemblyLifetime) _bytes = null;
        }

        internal void Discard() => _bytes = null;

        internal byte[]? Accept(ScreenMessage message, long now)
        {
            Expire(now);
            if (message.Sequence < _sequence) return null;
            if (message.Sequence > _sequence)
            {
                _sequence = message.Sequence;
                _started = now;
                _bytes = new byte[message.Length];
                _chunks = 0;
            }
            if (_bytes == null || _bytes.Length != message.Length) return null;
            var bit = 1UL << message.Chunk;
            if ((_chunks & bit) != 0) return null;
            message.Bytes.Span.CopyTo(_bytes.AsSpan(message.Chunk * ScreenChunkBytes));
            _chunks |= bit;
            var count = (_bytes.Length + ScreenChunkBytes - 1) / ScreenChunkBytes;
            if (_chunks != (1UL << count) - 1) return null;
            var result = _bytes;
            _bytes = null;
            return result;
        }
    }

    private sealed class ScreenEvent(ClientTabletPresence service) : Packets.NetEvent
    {
        private const int Maximum = 640;
        public override string Id => "modstoke.client.tablet-screen.v1";
        public override bool AllowClientRelay => false;

        public override void Read(UdpPacket packet, BoltConnection fromConnection)
        {
            if (!service.OnThread || service._screenFailed || !service._started || service._loading) return;
            try
            {
                if (!service.ScreenContext() || fromConnection == null || fromConnection.WasCollected
                    || packet == null || packet.WasCollected) return;
                if (service._mode == 2)
                {
                    var server = BoltNetwork.server;
                    if (server == null || server.WasCollected || fromConnection.Pointer != service._serverPointer
                        || fromConnection.ConnectionId != service._serverConnection
                        || server.Pointer != fromConnection.Pointer || server.ConnectionId != fromConnection.ConnectionId) return;
                }
                var now = Now;
                if (!service._screenIngress.Take(now, 768, 96)) return;
                // SonsSdk's native envelope is 7 bytes upstream and 11 bytes downstream; Size is bits.
                var header = service._mode == 1 ? 7 : 11;
                if (packet.Size < (header + 2 + 34) * 8 || packet.Size > (header + 2 + Maximum) * 8) return;
                var length = packet.ReadUShort();
                if (length < 34 || length > Maximum || packet.Size != (header + 2 + length) * 8) return;
                var bytes = new byte[length];
                for (var i = 0; i < length; i++) bytes[i] = packet.ReadByte();
                using var stream = new MemoryStream(bytes, false);
                using var reader = new BinaryReader(stream);
                ScreenMessage? message;
                try { message = Parse(reader, bytes, service._mode == 1); }
                catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException or OverflowException)
                { return; }
                if (message == null || stream.Position != stream.Length) return;
                service.ReceiveScreen(message, fromConnection, now);
            }
            catch (Exception error) { service.FailScreens(error); }
        }

        internal void Subscribe() => Emit(null, ScreenKind.Subscribe, 0, static (_, _) => { });

        internal void Demand(Peer peer, long sequence, bool wanted) =>
            Emit(peer, ScreenKind.Demand, (Peer: peer, Sequence: sequence, Wanted: wanted), static (writer, state) =>
            { writer.Write(state.Sequence); WriteGuid(writer, state.Peer.Generation); writer.Write(state.Wanted); });

        internal void Chunk(Peer? peer, ScreenFrame frame, int chunk) => Emit(peer,
            peer == null ? ScreenKind.Upload : ScreenKind.Frame,
            (Peer: peer, Frame: frame, Chunk: chunk), static (writer, state) =>
            {
                var (peer, frame, chunk) = state;
                writer.Write(frame.Sequence);
                if (peer != null) writer.Write(frame.Actor);
                WriteGuid(writer, frame.Generation);
                writer.Write((ushort)frame.Bytes.Length);
                writer.Write((byte)chunk);
                var offset = chunk * ScreenChunkBytes;
                writer.Write(frame.Bytes, offset, Math.Min(ScreenChunkBytes, frame.Bytes.Length - offset));
            });

        private void Emit<TState>(Peer? peer, ScreenKind kind, TState state, Action<BinaryWriter, TState> write)
        {
            if (service._screenFailed || !service._started || !BoltNetwork.isRunning
                || peer != null && !service.Current(peer)) return;
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write((byte)1);
                writer.Write((byte)kind);
                WriteGuid(writer, service._epoch);
                WriteGuid(writer, peer?.Token ?? service._token);
                write(writer, state);
            }
            if (stream.Length > Maximum) throw new InvalidOperationException("The tablet screen packet exceeds its limit.");
            var length = (int)stream.Length;
            var bytes = stream.GetBuffer();
            var packet = peer == null ? NewPacket(length + 2, GlobalTargets.OnlyServer)
                : NewPacket(length + 2, peer.Connection);
            packet.Packet.WriteUShort((ushort)length);
            for (var i = 0; i < length; i++) packet.Packet.WriteByte(bytes[i]);
            Send(packet);
        }

        private static ScreenMessage? Parse(BinaryReader reader, byte[] bytes, bool host)
        {
            if (reader.ReadByte() != 1) return null;
            var message = new ScreenMessage { Kind = (ScreenKind)reader.ReadByte() };
            if (host ? message.Kind is not (ScreenKind.Subscribe or ScreenKind.Upload)
                : message.Kind is not (ScreenKind.Demand or ScreenKind.Frame)) return null;
            message.Epoch = ReadGuid(reader);
            message.Token = ReadGuid(reader);
            if (message.Epoch == Guid.Empty || message.Token == Guid.Empty) return null;
            if (message.Kind == ScreenKind.Subscribe) return message;
            message.Sequence = reader.ReadInt64();
            if (message.Sequence <= 0) return null;
            if (message.Kind == ScreenKind.Demand)
            {
                message.Generation = ReadGuid(reader);
                if (message.Generation == Guid.Empty) return null;
                message.Demand = reader.ReadByte() switch
                { 0 => false, 1 => true, _ => throw new InvalidDataException() };
                return message;
            }
            if (message.Kind == ScreenKind.Frame)
            {
                message.Actor = reader.ReadUInt64();
                if (message.Actor == 0) return null;
            }
            message.Generation = ReadGuid(reader);
            if (message.Generation == Guid.Empty) return null;
            message.Length = reader.ReadUInt16();
            message.Chunk = reader.ReadByte();
            if (message.Length is < 1 or > ScreenMaximumBytes
                || message.Chunk >= (message.Length + ScreenChunkBytes - 1) / ScreenChunkBytes) return null;
            var expected = Math.Min(ScreenChunkBytes, message.Length - message.Chunk * ScreenChunkBytes);
            if (reader.BaseStream.Length - reader.BaseStream.Position != expected) return null;
            message.Bytes = bytes.AsMemory((int)reader.BaseStream.Position, expected);
            reader.BaseStream.Position += expected;
            return message;
        }
    }
}
