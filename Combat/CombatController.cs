// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Modstoke.Sons;
using Modstoke.Sons.Combat;

namespace Modstoke.Client;

// Owns one server connection. Startup selection remains with admission; merely
// installing the Client or receiving a combat packet must not acquire native life.
internal sealed partial class ClientCombatController
{
    private const int MaximumMessages = 32;
    private const long InitialOwnershipWaitMs = 8000;
    private readonly Queue<Datagram> _messages = new();
    private int _thread;
    private bool _started, _prepared, _subscribed, _updating;
    private ControlPeer? _peer;
    private StrictCombatClient? _client;
    private bool _retired, _acquisitionAttempted, _initialAcquisitionComplete, _ownsLife, _ownsWeapon, _ownsArmour;
    private StrictCombatBinding _ownedBinding;
    private ulong _ownedLife;
    private StrictCombatLocalArmourBinding? _armourBinding;
    private long _armourWaitDeadline;
    private ulong _weaponLife, _readyLife;
    private bool _equipRequested, _equipReady;
    private bool _equipInitial;
    private long _initialEquipDeadline;
    private StrictCombatBinding _equipBinding;
    private ulong _equipLife;
    private StrictCombatLocalLifeStatus _lifeStatus;

    internal string Status { get; private set; } = "Strict combat is not selected.";

    internal bool AllowsHandheldTerminal => !_prepared && !_ownsLife && !_ownsWeapon && !_ownsArmour && !_equipRequested;

    internal void Start()
    {
        if (_thread == 0) _thread = Environment.CurrentManagedThreadId;
        if (!OnThread || _started) return;
        _started = true;
    }

    // The future composed admission selection must call this explicitly before
    // network startup. Current package admission does not call it or offer combat.
    internal bool PrepareForNextStart() => PrepareForNextStart(ClientCombatResourceProfile.None);

    internal bool PrepareForNextStart(ClientCombatResourceProfile resources)
    {
        if (!OnThread || !_started || _updating) return false;
        ReleaseClosedPeer();
        if (_peer is not null) return false;
        _prepared = false;
        _resourceCapabilities = 0;
        _messages.Clear();
        if (!TryGetRequiredCapabilities(resources, out ulong required))
        { Status = "The combat resource profile is unsupported."; return false; }
        var life = StrictCombatClient.PrepareNativeLifeBarrierForNextStart();
        if (life != StrictCombatLifeStatus.Success)
        { Status = "Native life preparation: " + life; return false; }
        var weapon = StrictCombatClient.PrepareNativeWeaponBarrierForNextStart();
        if (weapon != StrictCombatWeaponStatus.Success)
        { Status = "Native weapon preparation: " + weapon; return false; }
        var armour = StrictCombatClient.PrepareNativeArmourBarrierForNextStart();
        if (armour != StrictCombatArmourStatus.Success)
        { Status = "Native armour preparation: " + armour; return false; }
        if (!PrepareResources(required & ResourceCapabilities)) return false;
        if (!_subscribed)
        {
            Datagrams.Received += OnDatagram;
            _subscribed = true;
        }
        _resourceCapabilities = required & ResourceCapabilities;
        _prepared = true;
        Status = "Prepared; waiting for matching server enrollment.";
        return true;
    }

    internal void Update()
    {
        if (!OnThread || !_started || _updating) return;
        _updating = true;
        try
        {
            ReleaseClosedPeer();
            if (!_prepared) return;
            if (_peer is null) DiscoverPeer();
            var client = _client;
            if (client is null) return;
            if (_retired) return;
            if (_peer is null || !Eligible(_peer))
            { Retire("Combat enrollment ended."); return; }

            // Receive callbacks only enqueue copied packets. Native mutation and
            // protocol transitions all run under this nonrecursive update owner.
            int count = _messages.Count;
            for (int i = 0; i < count && !_retired; i++)
            {
                var message = _messages.Dequeue();
                if (!ReferenceEquals(message.Peer, _peer)) continue;
                if (client.TryReceiveReadiness(message)) continue;
                if (client.TryReceiveRespawn(message)) continue;
                if (client.TryReceiveImpactPresentation(message)) continue;
                if (client.TryReceiveImpactAudio(message)) continue;
                if (client.TryReceive(message, out var receipt, out bool completed) && completed)
                    ObserveReceipt(in receipt);
                if (client.IsRetired) Retire("The combat session ended.");
            }
            if (_retired) return;
            if (!client.Update())
            { Retire("The combat session is unavailable."); return; }
            if (!client.UpdateReadiness())
            { Retire("Combat readiness: " + client.ReadinessStatus); return; }
            if (!client.TryRead(out var binding, out var state))
            {
                if (!CheckOwnershipDeadlines()) return;
                if (_ownsLife) WaitForArmour("Waiting for accepted combat state.");
                return;
            }
            if (_ownsLife && !SameBinding(in binding, in _ownedBinding))
            { Retire("The owned combat binding changed."); return; }
            if (!_initialAcquisitionComplete && _ownsWeapon && !client.DiscardLocalWeaponInput())
            { Retire("Initial weapon input could not be discarded."); return; }
            if (_ownsLife && state.LifeStage != StrictCombatLifeStage.Alive)
            {
                // Optional resources are acquired/projected only while alive. A
                // downed interval must not consume their synchronization timeout.
                _resourceWaitDeadline = 0;
                if (!_initialAcquisitionComplete && _ownsWeapon && _ownsArmour
                    && state.LifeGeneration == _ownedLife)
                {
                    // Life, pistol and armour are already contained. Follow the
                    // accepted transition instead of waiting for optional air setup.
                    _initialAcquisitionComplete = true;
                    ClearEquipmentRequest();
                }
            }
            if (!CheckOwnershipDeadlines()) return;
            // Never project a later life before its correlated SDK handoff.
            if (_ownsLife && state.LifeGeneration == _ownedLife && !SynchronizeHealth(client)) return;
            if (!_initialAcquisitionComplete)
            {
                Acquire(client, in binding, in state);
                return;
            }
            if (state.LifeStage != StrictCombatLifeStage.Alive)
            {
                _ownsWeapon = false;
                ClearEquipmentRequest();
            }
            if (client.RespawnStatus is StrictCombatRespawnClientStatus.Requested
                or StrictCombatRespawnClientStatus.Offered or StrictCombatRespawnClientStatus.Placed)
            {
                if (!client.UpdateRespawn())
                { Retire("Respawn synchronization is unavailable."); return; }
                // State polling may see the next server life before its correlated
                // placement outcome. Keep the old native life contained until then.
                if (client.RespawnStatus == StrictCombatRespawnClientStatus.Placed)
                { Status = "Waiting for the server's respawn outcome."; return; }
            }
            if (client.RespawnStatus is StrictCombatRespawnClientStatus.Uncertain or StrictCombatRespawnClientStatus.Expired)
            { Retire("Respawn synchronization: " + client.RespawnStatus); return; }

            if (!SynchronizeArmour(client, in binding, in state)) return;

            // Life presentation projects accepted health itself and cancels owned
            // equipment before disabling inventory for a downed/dead player.
            _lifeStatus = client.UpdateLocalLife();
            if (_lifeStatus is StrictCombatLocalLifeStatus.Pending or StrictCombatLocalLifeStatus.Busy)
                return;
            if (_lifeStatus is not (StrictCombatLocalLifeStatus.Presented
                or StrictCombatLocalLifeStatus.AlreadyCurrent or StrictCombatLocalLifeStatus.RespawnRequired))
            { Retire("Local life presentation: " + _lifeStatus); return; }

            if (_lifeStatus == StrictCombatLocalLifeStatus.RespawnRequired)
            {
                _ownsWeapon = false;
                ClearEquipmentRequest();
                if (client.RespawnStatus == StrictCombatRespawnClientStatus.Offered)
                {
                    var placement = client.ApplyRespawnPlacement();
                    if (placement == StrictCombatRespawnClientStatus.Uncertain)
                        Retire("Respawn placement is uncertain.");
                }
                if (!_retired) Status = "Waiting for an explicit respawn request or its server outcome.";
                return;
            }
            if (!client.TryRead(out binding, out state)) return;
            if (state.LifeStage != StrictCombatLifeStage.Alive)
            {
                _ownsWeapon = false;
                ClearEquipmentRequest();
                Status = "Following the server's downed state.";
                return;
            }
            if (!SynchronizeResources(client, in binding, in state)) return;
            if (_weaponLife != state.LifeGeneration) _ownsWeapon = false;
            if (_equipReady)
            {
                _equipReady = false;
                if (!SameBinding(in binding, in _equipBinding) || state.LifeGeneration != _equipLife)
                { ClearEquipmentRequest(); Status = "The equipment request belongs to an earlier life."; return; }
                ClearEquipmentRequest();
                if (!client.TryBeginLocalEquipment(out var start))
                {
                    Status = "Equipment restoration: " + start;
                    if (start == StrictCombatEquipmentStatus.MutationUncertain) Retire(Status);
                    return;
                }
            }
            if (client.EquipmentStatus is not (StrictCombatEquipmentStatus.Unavailable or StrictCombatEquipmentStatus.Returned))
            {
                var equipment = client.UpdateLocalEquipment();
                if (equipment == StrictCombatEquipmentStatus.MutationUncertain)
                { Retire("Equipment restoration is uncertain."); return; }
                if (equipment != StrictCombatEquipmentStatus.Equipped)
                { _ownsWeapon = false; Status = "Equipment restoration: " + equipment; return; }
                _ownsWeapon = true;
                _weaponLife = state.LifeGeneration;
            }
            if (!_ownsWeapon || !state.Equipped)
            { Status = "Life synchronized; waiting for an explicit equipment request."; return; }
            var ammo = client.ProjectLocalAmmunition();
            if (ammo is not (StrictCombatAmmoProjectionStatus.Applied or StrictCombatAmmoProjectionStatus.AlreadyCurrent))
            { Retire("Ammunition projection: " + ammo); return; }
            if (client.ReadinessStatus != StrictCombatReadinessClientStatus.Accepted
                || client.ReadinessLifeGeneration != state.LifeGeneration)
            {
                if (!client.DiscardLocalWeaponInput() || !client.TryBeginReadiness())
                { Retire("Combat readiness could not be established."); return; }
                Status = "Waiting for the server to acknowledge combat readiness.";
                return;
            }
            if (_readyLife != state.LifeGeneration)
            {
                if (!client.DiscardLocalWeaponInput())
                { Retire("Pending weapon input could not be discarded."); return; }
                _readyLife = state.LifeGeneration;
            }
            var shot = client.UpdateLocalShotPresentation();
            if (shot == StrictCombatShotPresentationStatus.MutationUncertain)
            { Retire("Shot presentation is uncertain."); return; }
            var input = client.UpdateLocalWeaponInput();
            if (input is StrictCombatWeaponInputStatus.BarrierRequired or StrictCombatWeaponInputStatus.ContextChanged)
            { Retire("Weapon input ownership changed."); return; }
            Status = "Following accepted server combat state.";
        }
        catch (Exception)
        {
            Retire("The combat session could not continue.");
        }
        finally { _updating = false; }
    }

    // These requests stay internal until the complete server/client mode can be
    // selected. A receipt authorizes initial ownership or restoration of an owned stash.
    internal bool RequestEquipment()
    {
        if (!CanRequest || _equipRequested || _ownsWeapon || _client is null
            || (_ownsLife && !_initialAcquisitionComplete)
            || (_ownsLife && _lifeStatus is not (StrictCombatLocalLifeStatus.Presented or StrictCombatLocalLifeStatus.AlreadyCurrent))
            || (!_ownsLife && _acquisitionAttempted)
            || _client.EquipmentStatus is not (StrictCombatEquipmentStatus.Unavailable or StrictCombatEquipmentStatus.Returned)
            || !_client.TryRead(out var binding, out var state)
            || state.LifeStage != StrictCombatLifeStage.Alive || !state.OwnsPistol) return false;
        long now = Environment.TickCount64;
        if (now < 0 || now > long.MaxValue - InitialOwnershipWaitMs
            || !_client.TryQueue(StrictCombatAction.EquipPistol)) return false;
        _equipRequested = true;
        _equipInitial = !_ownsLife;
        _initialEquipDeadline = _equipInitial ? now + InitialOwnershipWaitMs : 0;
        _equipBinding = binding;
        _equipLife = state.LifeGeneration;
        return true;
    }

    internal bool RequestRespawn()
        => CanRequest && _ownsLife && _lifeStatus == StrictCombatLocalLifeStatus.RespawnRequired
            && _client is not null && _client.ReadinessStatus == StrictCombatReadinessClientStatus.Accepted
            && _client.ReadinessLifeGeneration == _ownedLife && _client.TryRequestRespawn(out _);

    internal void Stop()
    {
        if (!OnThread) return;
        _started = _prepared = false;
        if (_subscribed)
        {
            Datagrams.Received -= OnDatagram;
            _subscribed = false;
        }
        Retire("Strict combat is stopped.");
        // Keep the terminal peer until it actually closes. SDK-owned cleanup
        // continues independently of Mod.Update while the runtime still ticks.
    }

    private bool Acquire(StrictCombatClient client, in StrictCombatBinding binding,
        in StrictCombatSnapshot state)
    {
        if (!_equipRequested)
        {
            Status = state.LifeStage == StrictCombatLifeStage.Uninitialized
                ? "Waiting for accepted combat state." : "Waiting for an explicit initial pistol request.";
            return false;
        }
        if (!_equipInitial || !SameBinding(in binding, in _equipBinding) || state.LifeGeneration != _equipLife)
        { Retire("Initial equipment authorization belongs to another combat life."); return false; }
        long now = Environment.TickCount64;
        if (now < 0 || now >= _initialEquipDeadline)
        { Retire("Initial equipment ownership could not be established in time."); return false; }
        if (!_equipReady)
        { Status = "Waiting for initial equipment authorization."; return false; }
        bool completingDowned = _ownsLife && _ownsWeapon
            && state.LifeStage is (StrictCombatLifeStage.Downed or StrictCombatLifeStage.Dead);
        if (!completingDowned && (state.LifeStage != StrictCombatLifeStage.Alive || !state.OwnsPistol || !state.Equipped))
        { Retire("Initial combat state no longer authorizes the held-pistol profile."); return false; }

        if (!HasProvisionedArmour(client, in binding, in state))
            return WaitForArmour("Waiting for initial server armour ownership.");
        if (!completingDowned && !HasProvisionedResources(client, in binding, in state))
            return WaitForResources("Waiting for initial server breathing resources.");

        if (!_ownsArmour)
        {
            // Establish the exact pieces while alive, before claiming life. A retained
            // binding can follow server damage during a later acquisition wait; a new
            // downed binding cannot infer which native pieces originally belonged here.
            if (!client.TryBindLocalArmour(out var capture, out var bound))
            {
                if (_armourBinding?.IsRetired != true && bound is (StrictCombatArmourBindingStatus.Unavailable
                    or StrictCombatArmourBindingStatus.StateUnavailable or StrictCombatArmourBindingStatus.Unprovisioned
                    or StrictCombatArmourBindingStatus.StaleObservation or StrictCombatArmourBindingStatus.LimitReached))
                    return WaitForArmour("Local armour binding: " + bound);
                Retire("Local armour binding: " + bound);
                return false;
            }
            _armourBinding = capture;
        }

        if (!_ownsLife)
        {
            // Capture after the matching receipt, never retain an observation across
            // the network wait. Native state confirms a grant; it never supplies one.
            if (!client.TryReadLocalPlayer(out var observation, out var read))
            {
                if (read is not (StrictCombatLocalReadStatus.PlayerUnavailable or StrictCombatLocalReadStatus.StateUnavailable))
                    Retire("Local player observation: " + read);
                return false;
            }
            _acquisitionAttempted = true;
            if (!client.TryAcquireInitializedNativeLifeBarrier(observation, out var life))
            {
                if (life is StrictCombatLifeStatus.Busy or StrictCombatLifeStatus.NativeInitializationPending)
                { Status = "Waiting for native vitals initialization."; return false; }
                Retire("Native life acquisition: " + life);
                return false;
            }
            _ownsLife = true;
            _ownedBinding = binding;
            _ownedLife = state.LifeGeneration;
        }
        if (!SynchronizeHealth(client)) return false;
        if (!_ownsWeapon)
        {
            // Contain the idle held pistol before a resource or armour wait can
            // yield. Downing can then stash it through the owned life adapter.
            if (!client.TryAcquireNativeWeaponBarrier(out var weapon))
            { Retire("Native weapon acquisition: " + weapon); return false; }
            _ownsWeapon = true;
            _weaponLife = state.LifeGeneration;
        }
        if (!_ownsArmour)
        {
            if (!client.TryAcquireNativeArmourBarrier(_armourBinding, out var armour))
                return HandleArmourBarrierWait("Native armour acquisition: ", armour);
            _ownsArmour = true;
        }
        if (!ProjectArmour(client)) return false;
        if (!completingDowned && !SynchronizeResources(client, in binding, in state)) return false;
        if (!client.DiscardLocalWeaponInput())
        { Retire("Initial weapon input could not be discarded."); return false; }
        _initialAcquisitionComplete = true;
        ClearEquipmentRequest();
        return true;
    }

    private bool SynchronizeHealth(StrictCombatClient client)
    {
        var projection = client.ProjectLocalHealth();
        if (projection is StrictCombatHealthProjectionStatus.Applied or StrictCombatHealthProjectionStatus.AlreadyCurrent)
            return true;
        if (projection == StrictCombatHealthProjectionStatus.Busy)
        { Status = "Waiting for accepted health projection."; return false; }
        Retire("Health projection: " + projection);
        return false;
    }

    private bool SynchronizeArmour(StrictCombatClient client, in StrictCombatBinding binding,
        in StrictCombatSnapshot state)
    {
        if (!_ownsArmour || _armourBinding is null || _armourBinding.IsRetired)
        { Retire("Native armour ownership ended."); return false; }
        if (state.LifeGeneration != _ownedLife)
        {
            // Placed containment returned above. Only the SDK's correlated commit
            // permits transferring the retained armour identities to the next life.
            if (_ownedLife == ulong.MaxValue || state.LifeGeneration != _ownedLife + 1
                || client.RespawnStatus != StrictCombatRespawnClientStatus.Committed
                || !client.TryReadRespawnOutcome(out var outcome) || !outcome.IsValid
                || outcome.Result != StrictCombatRespawnResult.Committed
                || !SameBinding(outcome.Request.Binding, in _ownedBinding)
                || outcome.Request.LifeGeneration != _ownedLife
                || outcome.State.LifeGeneration != state.LifeGeneration)
            { Retire("The combat life changed without a correlated respawn commit."); return false; }
            _ownsWeapon = false;
            ClearEquipmentRequest();
            // A respawn outcome carries no armour grant. Wait for the ordinary
            // receipt paired with the current accepted state before handing off.
            if (!HasProvisionedArmour(client, in binding, in state))
                return WaitForArmour("Waiting for the new life's server armour ownership.");
            if (!client.TryHandoffNativeArmourBarrier(out var capture, out var handoff))
                return HandleArmourBarrierWait("Native armour handoff: ", handoff);
            _armourBinding = capture;
            _ownedLife = state.LifeGeneration;
        }
        // Project even when dead, downed or unequipped, before life presentation
        // or an equipment early return can defer accepted armour break effects.
        return ProjectArmour(client);
    }

    private static bool HasProvisionedArmour(StrictCombatClient client, in StrictCombatBinding binding,
        in StrictCombatSnapshot state)
        => client.TryReadOwnedArmour(out var currentBinding, out var current, out var armour)
            && SameBinding(in binding, in currentBinding) && current.Revision == state.Revision
            && current.LifeGeneration == state.LifeGeneration && armour.IsProvisioned;

    private bool ProjectArmour(StrictCombatClient client)
    {
        var projection = client.ProjectLocalArmour();
        if (projection is StrictCombatArmourProjectionStatus.Applied or StrictCombatArmourProjectionStatus.AlreadyCurrent)
        { _armourWaitDeadline = 0; return true; }
        if (_armourBinding?.IsRetired != true && projection is (StrictCombatArmourProjectionStatus.Busy
            or StrictCombatArmourProjectionStatus.LimitReached or StrictCombatArmourProjectionStatus.StateUnavailable))
            return WaitForArmour("Armour projection: " + projection);
        Retire("Armour projection: " + projection);
        return false;
    }

    private bool HandleArmourBarrierWait(string prefix, StrictCombatArmourStatus status)
    {
        if (_armourBinding?.IsRetired != true && status is (StrictCombatArmourStatus.Busy
            or StrictCombatArmourStatus.LimitReached or StrictCombatArmourStatus.StaleObservation
            or StrictCombatArmourStatus.Unprovisioned))
            return WaitForArmour(prefix + status);
        Retire(prefix + status);
        return false;
    }

    private bool WaitForArmour(string status)
    {
        long now = Environment.TickCount64;
        if (now < 0 || now > long.MaxValue - InitialOwnershipWaitMs)
        { Retire("Armour synchronization clock is unavailable."); return false; }
        if (_armourWaitDeadline == 0) _armourWaitDeadline = now + InitialOwnershipWaitMs;
        if (CheckOwnershipDeadlines()) Status = status;
        return false;
    }

    private bool CheckOwnershipDeadlines()
    {
        long now = Environment.TickCount64;
        if (_equipInitial && (now < 0 || now >= _initialEquipDeadline))
        { Retire("Initial equipment ownership could not be established in time."); return false; }
        if (_armourWaitDeadline != 0 && (now < 0 || now >= _armourWaitDeadline))
        { Retire("Armour synchronization could not complete in time."); return false; }
        if (_resourceWaitDeadline != 0 && (now < 0 || now >= _resourceWaitDeadline))
        { Retire("Breathing resource synchronization could not complete in time."); return false; }
        return true;
    }

    private void ObserveReceipt(in StrictCombatReceipt receipt)
    {
        if (!_equipRequested || receipt.Action != StrictCombatAction.EquipPistol
            || receipt.Epoch != _equipBinding.Epoch || receipt.RequestLifeGeneration != _equipLife) return;
        if (receipt.Result == StrictCombatResult.Accepted)
        {
            if (_equipInitial && (receipt.State.LifeGeneration != _equipLife
                || receipt.State.LifeStage != StrictCombatLifeStage.Alive
                || !receipt.State.OwnsPistol || !receipt.State.Equipped))
            { Retire("Initial equipment receipt does not authorize the held-pistol profile."); return; }
            _equipReady = true;
        }
        else
        {
            ClearEquipmentRequest();
            Status = "Equipment request: " + receipt.Result;
        }
    }

    private void OnDatagram(Datagram message)
    {
        if (!OnThread || !_started || !_prepared || message is null) return;
        // Enrollment and the initial binding can arrive before the next Mod.Update.
        // Creating the transport adapter here does not acquire native ownership.
        if (_peer is null && !_updating) DiscoverPeer();
        if (_retired || !ReferenceEquals(message.Peer, _peer)) return;
        if (_messages.Count >= MaximumMessages)
        { Retire("Combat receive capacity was exceeded."); return; }
        _messages.Enqueue(message);
    }

    private void DiscoverPeer()
    {
        ControlPeer? candidate = null;
        foreach (var peer in ControlTransport.GetPeers())
        {
            if (!Eligible(peer)) continue;
            if (candidate is not null && !ReferenceEquals(candidate, peer))
            { Status = "More than one combat server connection is present."; return; }
            candidate = peer;
        }
        if (candidate is not null) TryAttach(candidate);
    }

    private void TryAttach(ControlPeer peer)
    {
        if (_peer is not null || !Eligible(peer) || !StrictCombatClient.TryCreate(peer, out var client)) return;
        _peer = peer;
        _client = client;
        _retired = false;
        ClearOwnership();
        Status = "Waiting for accepted combat state.";
    }

    private void ReleaseClosedPeer()
    {
        if (_peer is null || _peer.IsCurrent) return;
        Retire("The combat server connection closed.");
        if (_client?.EquipmentCleanupPending == true) return;
        _client = null;
        _peer = null;
        _prepared = false;
        _resourceCapabilities = 0;
    }

    private void Retire(string status)
    {
        _retired = true;
        Status = status;
        _messages.Clear();
        // SDK retirement retains native suppression; clear only coordinator state.
        _client?.Retire();
        ClearOwnership();
    }

    private void ClearOwnership()
    {
        _acquisitionAttempted = _initialAcquisitionComplete = _ownsLife = _ownsWeapon = _ownsArmour = false;
        _ownedBinding = default;
        _ownedLife = _weaponLife = _readyLife = 0;
        _armourBinding = null;
        _armourWaitDeadline = 0;
        _breathingLife = _rebreatherLife = 0;
        _resourceWaitDeadline = 0;
        _lifeStatus = StrictCombatLocalLifeStatus.Unavailable;
        ClearEquipmentRequest();
    }

    private void ClearEquipmentRequest()
    {
        _equipRequested = _equipReady = false;
        _equipInitial = false;
        _initialEquipDeadline = 0;
        _equipBinding = default;
        _equipLife = 0;
    }

    private bool OnThread => _thread != 0 && Environment.CurrentManagedThreadId == _thread;
    private bool CanRequest => OnThread && _started && _prepared && !_updating && !_retired
        && _peer is not null && Eligible(_peer);
    private bool Eligible(ControlPeer peer)
        => peer is not null && peer.ServerSession is null && peer.IsCurrent
            && ClientEnrollment.TryRead(peer, out var enrollment) == EnrollmentReadStatus.Success
            && ReferenceEquals(enrollment.Peer, peer) && enrollment.State == EnrollmentState.Enrolled
            && (enrollment.RequiredCapabilities & RequiredCapabilities) == RequiredCapabilities
            && (enrollment.RequiredCapabilities & ResourceCapabilities) == _resourceCapabilities
            && (enrollment.OfferedCapabilities & (RequiredCapabilities | _resourceCapabilities))
                == (RequiredCapabilities | _resourceCapabilities);
    private const ulong RequiredCapabilities = StrictCombatRespawnProtocol.RequiredCapabilities
        | StrictCombatArmourProtocol.RequiredCapabilities | StrictCombatImpactAudioProtocol.Capability
        | StrictCombatImpactPresentationProtocol.Capability | StrictCombatReadinessProtocol.Capability;
    private static bool SameBinding(in StrictCombatBinding first, in StrictCombatBinding second)
        => first.Epoch == second.Epoch && first.Generation == second.Generation
            && first.ActorNetworkId == second.ActorNetworkId && first.ReloadDurationMs == second.ReloadDurationMs;
}
