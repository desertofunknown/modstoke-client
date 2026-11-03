// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftService
{
    private SpacecraftToolRack? _toolRack;
    private string _toolRackPrompt = "";

    private void EnsureToolRack()
    {
        if (_body?.Ready != true || _toolRack != null) return;
        _toolRack = new SpacecraftToolRack(_body, CommandStationEngineering);
        _body.SetToolRackSolid(_toolRack.Solid);
    }

    private bool UpdateToolRack(bool interactive)
    {
        _toolRackPrompt = "";
        int seat = IsClient ? _clientSeat : _local?.Seat ?? -1;
        bool available = interactive && _ready && _body is { Ready: true, TelemetryCurrent: true }
            && _body.State.Travel == SpacecraftTravel.None && !BoardingPending
            && _rider is { Active: true, Attached: true, Walking: true } && LocalCrewAccess && LocalAlive()
            && seat is >= 0 and < 3 && LocalFirefightingHolder != Guid.Empty;
        return _toolRack?.Interact(LocalFirefightingHolder, available ? _actorIds[seat] : 0, available, out _toolRackPrompt) == true;
    }

    private string ExecuteShipToolRack(Peer peer, BoxStationEngineeringControl control, int target)
    {
        if (_shipId == Guid.Empty || _body is not { Ready: true, TelemetryCurrent: true } || _toolRack == null
            || _travel != SpacecraftTravel.None || !PeerCurrent(peer) || !HasCrewAccess(peer)
            || !peer.Attached || !peer.Walking || !peer.Prepared || !peer.EntryPrepared
            || peer.Seat is < 0 or > 2 || !ReferenceEquals(_seats[peer.Seat], peer) || peer.Lease == Guid.Empty
            || !TryActor(peer, out var feet, out _, out var actor) || !CabinActorCurrent(peer, feet)
            || !_body.ContainsCabin(feet)) return "Stand inside the cabin at the tool rack.";
        var tools = _stationSystems.Engineering;
        bool take = control == BoxStationEngineeringControl.ShipTake;
        int id = take ? target : tools.Held(peer.Lease);
        if (id < 0) return "Hold the tool you want to store.";
        var item = tools.State.Tools[id];
        if (take && item.Place != BoxStationToolPlace.ShipStored) return "That tool is no longer in the rack.";
        if (!take && (item.Place != BoxStationToolPlace.Held || item.Actor != actor)) return "The held tool changed.";
        int slot = (int)BoxStationEngineering.GetSources(_stationSystems.Layout)[id].Kind;
        var point = _toolRack.ControlPoint(slot, take);
        var eye = feet + _body.Root.transform.up * 1.3f;
        if ((point - eye).sqrMagnitude > 2.4f * 2.4f
            || !_body.CanSeeControl(eye, point, -1, ActorTransform(peer), null, _toolRack.Solid))
            return "Move within clear reach of the tool rack.";
        string failure = OtherPortableInHand(peer.Lease, actor);
        if (failure.Length != 0) return failure;
        CancelEngineeringWork(peer);
        if (_manualRepairInputs[peer.Seat] is { } repair) StopManualRepair(repair);
        string result = take ? tools.TakeShipTool(id, peer.Lease, actor) : tools.StoreShipTool(peer.Lease, actor);
        MarkEngineeringDirty();
        return result;
    }

    private void DisposeToolRack()
    {
        _body?.SetToolRackSolid(null);
        var rack = _toolRack;
        _toolRack = null;
        _toolRackPrompt = "";
        rack?.Dispose();
    }
}
