// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Modstoke.Sons;
using UnityEngine;

namespace Modstoke.Client;

internal readonly record struct SpacecraftEvaSlotState(int Seat, ulong Actor, Guid Lease, SpacecraftEvaSuitState Suit);

internal sealed partial class ClientSpacecraftService
{
    private SpacecraftEvaSuit[] _evaSuits = [new(), new(), new()];
    private readonly EvaLease?[] _evaLeases = new EvaLease?[SpacecraftEvaSuit.Count];
    private SpacecraftEvaSlotState[]? _evaState;
    private long _nextEvaSnapshot, _lastEvaAt, _evaSequence, _receivedEvaSequence;
    private SpacecraftEvaRack? _evaRack;
    private SpacecraftEvaVisuals? _evaVisuals;
    private SpacecraftEvaHud? _evaHud;

    private SpacecraftEvaSlotState[]? CurrentEvaState => !_ready ? null
        : IsClient && (_lastEvaAt == 0 || Now - _lastEvaAt > 2000) ? null : _evaState;

    private void ResetEva()
    {
        DisposeEvaViews();
        _evaSuits = [new(), new(), new()];
        Array.Clear(_evaLeases);
        _evaState = null;
        _nextEvaSnapshot = _lastEvaAt = _evaSequence = _receivedEvaSequence = 0;
    }

    private SavedSpacecraftEvaSuit[] CaptureEva()
    {
        var saved = new SavedSpacecraftEvaSuit[SpacecraftEvaSuit.Count];
        for (int slot = 0; slot < saved.Length; slot++) saved[slot] = _evaSuits[slot].Capture();
        if (!TryRestoreEva(saved, out _)) throw new InvalidOperationException("The EVA suits have invalid resource state.");
        return saved;
    }

    private static bool TryRestoreEva(SavedSpacecraftEvaSuit[]? saved, out SpacecraftEvaSuit[] suits)
    {
        suits = Array.Empty<SpacecraftEvaSuit>();
        if (saved is not { Length: SpacecraftEvaSuit.Count }) return false;
        var restored = new SpacecraftEvaSuit[SpacecraftEvaSuit.Count];
        for (int slot = 0; slot < restored.Length; slot++)
            if (!SpacecraftEvaSuit.TryRestore(saved[slot], out restored[slot])) return false;
        suits = restored;
        return true;
    }

    private void DisposeEvaViews()
    {
        _body?.SetEvaRackSolid(null);
        var visuals = _evaVisuals; _evaVisuals = null;
        var rack = _evaRack; _evaRack = null;
        var hud = _evaHud; _evaHud = null;
        try { visuals?.Dispose(); }
        catch (Exception error) { Console.WriteLine("Spacecraft EVA visual cleanup: " + error); }
        try { rack?.Dispose(); }
        catch (Exception error) { Console.WriteLine("Spacecraft EVA rack cleanup: " + error); }
        try { hud?.Dispose(); }
        catch (Exception error) { Console.WriteLine("Spacecraft EVA HUD cleanup: " + error); }
    }

    private void ReleaseEva(Peer peer)
    {
        // Expedition recovery returns the same spent unit. Supplies belong to
        // the world, not a player lease, and never reset on death or reconnect.
        for (int slot = 0; slot < _evaLeases.Length; slot++)
            if (_evaLeases[slot] is { } lease && ReferenceEquals(lease.Peer, peer))
            { _evaLeases[slot] = null; _nextEvaSnapshot = 0; }
    }

    private bool EvaReserved(EvaLease lease)
    {
        var peer = lease.Peer;
        if (lease.Epoch != _epoch || lease.Ship != _shipId || lease.Token != peer.Token
            || lease.Lease != peer.Lease || peer.Lease == Guid.Empty || peer.Seat is < 0 or > 2
            || lease.Seat != peer.Seat || !ReferenceEquals(_seats[peer.Seat], peer)
            || !ReferenceEquals(lease.Binding, peer.Binding) || !peer.EntryPrepared || peer.ForceReturn
            || (peer.Binding?.NetworkId ?? 0) != lease.Actor) return false;
        var actor = ActorTransform(peer);
        // A missing observation cannot transfer custody to another actor.
        return actor == null || actor.WasCollected || !actor || actor.Pointer == lease.ActorPointer;
    }

    private bool EvaCurrent(EvaLease lease, out Vector3 position, out ulong actorId)
    {
        position = default; actorId = 0;
        var peer = lease.Peer;
        return EvaReserved(lease)
            && TrySurvivalActor(peer, out position, out actorId) && actorId == lease.Actor
            && ActorTransform(peer) is { } actor && !actor.WasCollected && actor && actor.Pointer == lease.ActorPointer;
    }

    private bool WearingEva(Peer peer)
    {
        foreach (var lease in _evaLeases)
            if (lease != null && ReferenceEquals(lease.Peer, peer) && EvaCurrent(lease, out _, out _)) return true;
        return false;
    }

    private void StepEva(float dt)
    {
        if (IsClient) return;
        for (int slot = 0; slot < _evaLeases.Length; slot++)
        {
            var lease = _evaLeases[slot];
            if (lease == null) continue;
            if (!EvaReserved(lease))
            { _evaLeases[slot] = null; _nextEvaSnapshot = 0; continue; }
            if (_body?.Ready != true || !EvaCurrent(lease, out var position, out _))
            { lease.PositionCurrent = false; continue; }
            position = CrewPosition(lease.Peer, position);
            var relative = _body.Root.transform.InverseTransformPoint(position);
            float effort = 1;
            bool active = lease.Peer.Attached && lease.Peer.Prepared
                && (!ReferenceEquals(lease.Peer, _local) || _rider?.Incapacitated != true);
            if (active && lease.PositionCurrent && lease.FrameRevision == _frameRevision && dt > 0)
                effort = 1 + Mathf.Clamp((relative - lease.LastPosition).magnitude / dt, 0, 3);
            lease.LastPosition = relative;
            lease.FrameRevision = _frameRevision;
            lease.PositionCurrent = active;
            var systems = _systems.State;
            var chest = position + _body.Root.transform.up * .9f;
            float temperature = _body.State.Region == SpacecraftRegion.Space ? -80 : 22;
            if (_body.ContainsCabinAir(chest))
                temperature = _body.IsInEntryVestibule(chest) ? systems.EntryTemperatureC
                    : _body.IsInAirlock(chest) ? systems.AirlockTemperatureC : systems.CabinTemperatureC;
            else if (_orbitalStation?.Visible == true && CurrentStationState is { } station
                && station.HullRevision == _orbitalStation.HullState.Revision)
            {
                int cell = _orbitalStation.AtmosphereCellAt(position + _orbitalStation.Root.transform.up * .9f);
                if (cell >= 0) temperature = _stationSystems.GetAtmosphere(cell).TemperatureC;
            }
            _evaSuits[slot].Step(dt, effort, temperature);
        }
        if (Now < _nextEvaSnapshot) return;
        _nextEvaSnapshot = Now + 200;
        _evaState = new SpacecraftEvaSlotState[SpacecraftEvaSuit.Count];
        for (int slot = 0; slot < _evaState.Length; slot++)
        {
            var lease = _evaLeases[slot];
            _evaState[slot] = new(lease?.Peer.Seat ?? -1, lease?.Actor ?? 0,
                lease?.Lease ?? Guid.Empty, _evaSuits[slot].State);
        }
        long sequence = checked(++_evaSequence);
        foreach (var peer in _peers.Values) _event?.Eva(peer, sequence, _shipId, _evaState);
    }

    private SpacecraftEvaSuitState? LocalEva(out bool waiting, out int localSeat)
    {
        waiting = false;
        localSeat = IsClient ? _clientSeat : _local?.Seat ?? -1;
        var lease = IsClient ? _clientLease : _local?.Lease ?? Guid.Empty;
        if (localSeat is < 0 or > 2 || lease == Guid.Empty) return null;
        bool incapacitated = _rider?.Incapacitated == true && _body?.Ready == true
            && _rider.TryAttachedPosition(_body, TheForest.Utils.LocalPlayer.Transform, out _);
        if (!LocalAlive() && !incapacitated) return null;
        var state = CurrentEvaState;
        if (_evaState == null) return null;
        foreach (var item in _evaState)
        {
            if (item.Seat != localSeat || item.Lease != lease) continue;
            if (IsClient)
            {
                if (item.Actor == 0 || _actorIds[localSeat] != 0 && item.Actor != _actorIds[localSeat]) return null;
                waiting = _actorIds[localSeat] == 0;
                if (waiting && incapacitated)
                {
                    var entity = TheForest.Utils.LocalPlayer.Entity;
                    if (entity != null && !entity.WasCollected && entity && entity.isAttached
                        && entity.networkId.PackedValue == item.Actor) waiting = false;
                }
            }
            else
            {
                EvaLease? reserved = null;
                foreach (var candidate in _evaLeases)
                    if (candidate != null && ReferenceEquals(candidate.Peer, _local) && EvaReserved(candidate))
                    { reserved = candidate; break; }
                if (reserved == null) return null;
                waiting = !EvaCurrent(reserved, out _, out _);
            }
            waiting |= state == null || _rider?.Attached != true;
            return waiting ? null : item.Suit;
        }
        return null;
    }

    private bool UpdateEvaViews()
    {
        if (_body?.Ready != true) return false;
        var state = CurrentEvaState;
        EnsureEvaRack();
        var localSuit = LocalEva(out _, out int localSeat);
        _evaHud ??= new SpacecraftEvaHud();
        _evaHud.Update(localSuit.HasValue ? state : null, localSeat);
        Span<SpacecraftEvaWearer> wearers = stackalloc SpacecraftEvaWearer[SpacecraftEvaSuit.Count];
        int count = 0;
        if (state != null)
            for (int slot = 0; slot < state.Length; slot++)
            {
                var item = state[slot];
                if (item.Seat < 0 || item.Actor != _actorIds[item.Seat]) continue;
                bool local = item.Seat == localSeat && localSuit.HasValue;
                if (item.Seat == localSeat && !local) continue;
                wearers[count++] = new(slot, item.Actor, local, item.Lease);
            }
        _evaVisuals ??= new SpacecraftEvaVisuals(_evaRack!.SuitMaterial);
        _evaVisuals.Update(wearers[..count], _ready && _body.TelemetryCurrent);
        return _evaRack!.Update(state, localSeat, _ready && _body.TelemetryCurrent && state != null
            && _rider is { Active: true, Attached: true, Walking: true } && LocalCrewAccess && LocalAlive(), _feedback);
    }

    private void EnsureEvaRack()
    {
        if (_body?.Ready != true || _evaRack != null) return;
        _evaRack = new SpacecraftEvaRack(_body, (slot, service) => Command((service ? "eva-service-" : "eva-suit-") + slot));
        _body.SetEvaRackSolid(_evaRack.Solid);
    }

    private static bool TryEvaCommand(string command, out int slot, out bool service)
    {
        service = command.StartsWith("eva-service-", StringComparison.Ordinal);
        string prefix = service ? "eva-service-" : "eva-suit-";
        slot = command.Length == prefix.Length + 1 && command.StartsWith(prefix, StringComparison.Ordinal)
            ? command[^1] - '0' : -1;
        return slot is >= 0 and < SpacecraftEvaSuit.Count;
    }

    private string ExecuteEva(Peer peer, int slot, bool service, Vector3 position)
    {
        if (_body?.TelemetryCurrent != true || _evaRack == null || _body.State.Travel != SpacecraftTravel.None
            || !peer.Attached || !peer.Walking || !peer.Prepared || !peer.EntryPrepared
            || peer.Seat is < 0 or > 2 || !ReferenceEquals(_seats[peer.Seat], peer)
            || peer.Lease == Guid.Empty || !CabinActorCurrent(peer, position))
            return "Stand inside the cabin at the EVA rack.";
        var frame = _body.Root.transform;
        var chest = position + frame.up * .9f;
        if (!_body.ContainsCabinAir(chest) || _body.IsInEntryVestibule(chest) || _body.IsInAirlock(chest))
            return "Use the EVA rack inside the main cabin.";
        var air = _systems.State;
        if (air.CabinPressure < 70 || air.CabinPressure * air.OxygenFraction < 18
            || air.CO2Fraction > .005f || air.CabinTemperatureC < 5)
            return "Restore safe cabin air before opening or servicing an EVA suit.";
        var target = _evaRack.ControlPoint(slot, service);
        var eye = position + frame.up * 1.3f;
        var delta = target - eye;
        if (delta.sqrMagnitude > 2.4f * 2.4f) return "Move within reach of this suit's rack control.";
        var hits = _accessHits ??= new Il2CppStructArray<RaycastHit>(size: 32);
        int count = Physics.RaycastNonAlloc(eye, delta.normalized, hits, Mathf.Max(0, delta.magnitude - .04f),
            ~0, QueryTriggerInteraction.Ignore);
        if (count < 0 || count >= hits.Length) return "The EVA rack control is obstructed.";
        var actor = ActorTransform(peer);
        for (int index = 0; index < count; index++)
        {
            var collider = hits[index].collider;
            if (!collider || actor != null && actor && collider.transform.IsChildOf(actor)) continue;
            return "The EVA rack control is obstructed.";
        }
        var held = _evaLeases[slot];
        string message;
        if (service)
        {
            if (held != null) return "Return this suit to its rack before servicing it.";
            _systems.TryServiceEva(_evaSuits[slot], out message);
        }
        else if (held != null)
        {
            if (!ReferenceEquals(held.Peer, peer)) return "This suit is issued to another crew member.";
            _evaLeases[slot] = null;
            message = "EVA suit returned. Remaining supplies were retained.";
        }
        else
        {
            foreach (var reservation in _evaLeases)
                if (reservation != null && ReferenceEquals(reservation.Peer, peer) && EvaReserved(reservation))
                    return "Return your current EVA suit before taking another.";
            var suit = _evaSuits[slot].State;
            if (suit.Pressure < 55 || suit.Pressure * suit.OxygenFraction < 18 || suit.CO2Fraction > .005f
                || suit.BatteryKwh < .01f || suit.FilterMoles < .01f || suit.OxygenMoles < .1f)
                return "Service this EVA suit before wearing it.";
            if (!TryActor(peer, out _, out _, out var actorId) || actor == null || !actor)
                return "The current crew member could not be confirmed.";
            _evaLeases[slot] = new(peer, _epoch, _shipId, actor.Pointer, actorId,
                frame.InverseTransformPoint(position), _frameRevision);
            message = "EVA suit sealed. Monitor oxygen, battery and scrubber supplies.";
        }
        if (TryActor(peer, out _, out _, out var interactionActor))
        {
            _interactions[peer.Seat] = new(interactionActor, frame.InverseTransformPoint(target), 1);
            _apuGuardContacts[peer.Seat] = null;
            _interactionUntil[peer.Seat] = Now + 650;
        }
        _nextEvaSnapshot = _nextSnapshot = 0;
        return message;
    }

    private sealed class EvaLease(Peer peer, Guid epoch, Guid ship, IntPtr actorPointer,
        ulong actor, Vector3 position, long frameRevision)
    {
        internal readonly Peer Peer = peer;
        internal readonly Guid Epoch = epoch, Ship = ship, Token = peer.Token, Lease = peer.Lease;
        internal readonly PlayerHandle? Binding = peer.Binding;
        internal readonly IntPtr ActorPointer = actorPointer;
        internal readonly ulong Actor = actor;
        internal readonly int Seat = peer.Seat;
        internal Vector3 LastPosition = position;
        internal long FrameRevision = frameRevision;
        internal bool PositionCurrent = true;
    }
}
