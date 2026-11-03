// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using Modstoke.Sons;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftService
{
    private static bool IsSystemAction(ActionCode action) => action is ActionCode.Power or ActionCode.Dampeners
        or ActionCode.Door or ActionCode.Gear or ActionCode.InnerDoor or ActionCode.Override
        or ActionCode.Scrubber or ActionCode.Generator or ActionCode.Vent or ActionCode.ServiceSupplies or ActionCode.ShoreAir
        or ActionCode.SelectIsland or ActionCode.SelectWorld or ActionCode.SelectMoon or ActionCode.CabinDoor
        or ActionCode.ApuGuardOpen or ActionCode.ApuGuardClose or ActionCode.ServiceEngine
        or ActionCode.SealCabin or ActionCode.SealAirlock or ActionCode.SealEntry
        or ActionCode.BusIsolate or ActionCode.BusConnect or ActionCode.ServicePower or ActionCode.ServiceLife or ActionCode.ServiceHull
        or ActionCode.ServiceWarp or ActionCode.WarpDischargeStart or ActionCode.WarpDischargeStop
        or ActionCode.ServiceHullAirlock or ActionCode.ServiceHullEntry
        || action >= ActionCode.Valve0Open && action <= ActionCode.Line11Repair;

    private string ExecuteSystem(Peer peer, ActionCode action, Vector3 actorPosition)
    {
        if (_body == null) return "Deploy the spacecraft before operating its systems.";
        if (action == ActionCode.ServiceSupplies)
        {
            if (IsClient || !ReferenceEquals(peer, _local))
                return "Development servicing is restricted to the local host.";
            if (peer.Seat >= 0 && !peer.Walking)
                return "Walk to the engineering station before development servicing.";
        }
        bool circuitAction = action >= ActionCode.Circuit0Open && action <= ActionCode.Circuit5Close;
        bool circuitRepair = action >= ActionCode.Circuit0Repair && action <= ActionCode.Circuit5Repair;
        bool lineRepair = action >= ActionCode.Line0Repair && action <= ActionCode.Line11Repair;
        int circuitOffset = (int)action - (int)ActionCode.Circuit0Open;
        var command = circuitRepair ? "circuit-" + ((int)action - (int)ActionCode.Circuit0Repair) + "-repair"
            : lineRepair ? "line-" + ((int)action - (int)ActionCode.Line0Repair) + "-repair"
            : circuitAction
            ? "circuit-" + (circuitOffset / 2) + (circuitOffset % 2 == 0 ? "-open" : "-close")
            : action switch
        {
            ActionCode.Power => "power", ActionCode.Dampeners => "dampeners", ActionCode.Door => "door",
            ActionCode.Gear => "gear", ActionCode.InnerDoor => "inner-door", ActionCode.Override => "override",
            ActionCode.CabinDoor => "cabin-door", ActionCode.ShoreAir => "shore-air",
            ActionCode.Scrubber => "scrubber", ActionCode.Generator => "generator", ActionCode.Vent => "vent",
            ActionCode.ServiceSupplies => "service-supplies", ActionCode.ServiceEngine => "service-engine",
            ActionCode.SealCabin => "seal-cabin", ActionCode.SealAirlock => "seal-airlock", ActionCode.SealEntry => "seal-entry",
            ActionCode.BusIsolate => "bus-isolate", ActionCode.BusConnect => "bus-connect",
            ActionCode.ServicePower => "service-power", ActionCode.ServiceLife => "service-life", ActionCode.ServiceHull => "service-hull",
            ActionCode.ServiceHullAirlock => "service-hull-airlock", ActionCode.ServiceHullEntry => "service-hull-entry",
            ActionCode.ServiceWarp => "service-warp",
            ActionCode.WarpDischargeStart => "warp-discharge-start", ActionCode.WarpDischargeStop => "warp-discharge-stop",
            ActionCode.SelectIsland => "select-island",
            ActionCode.SelectWorld => "select-world", ActionCode.SelectMoon => "select-moon",
            ActionCode.Valve0Open => "valve-0-open", ActionCode.Valve0Close => "valve-0-close",
            ActionCode.Valve1Open => "valve-1-open", ActionCode.Valve1Close => "valve-1-close",
            ActionCode.Valve2Open => "valve-2-open", ActionCode.Valve2Close => "valve-2-close",
            ActionCode.Valve3Open => "valve-3-open", ActionCode.Valve3Close => "valve-3-close",
            ActionCode.Valve4Open => "valve-4-open", ActionCode.Valve4Close => "valve-4-close",
            ActionCode.Valve5Open => "valve-5-open", ActionCode.Valve5Close => "valve-5-close",
            ActionCode.Valve6Open => "valve-6-open", ActionCode.Valve6Close => "valve-6-close",
            ActionCode.Valve7Open => "valve-7-open", ActionCode.Valve7Close => "valve-7-close",
            ActionCode.Valve8Open => "valve-8-open", ActionCode.Valve8Close => "valve-8-close",
            ActionCode.Valve9Open => "valve-9-open", ActionCode.Valve9Close => "valve-9-close",
            ActionCode.Valve10Open => "valve-10-open", ActionCode.Valve10Close => "valve-10-close",
            ActionCode.Valve11Open => "valve-11-open", ActionCode.Valve11Close => "valve-11-close",
            ActionCode.ApuIsolate => "apu-isolate", ActionCode.ApuReset => "apu-reset",
            ActionCode.ApuGuardOpen => "apu-guard-open", ActionCode.ApuGuardClose => "apu-guard-close",
            ActionCode.AirlockToCabin => "airlock-to-cabin", ActionCode.AirlockToEntry => "airlock-to-entry",
            ActionCode.AirlockCancel => "airlock-cancel",
            _ => null
        };
        if (command == null) return "Unknown spacecraft system request.";
        if (action == ActionCode.ShoreAir && !peer.Walking && peer.Seat >= 0)
            return "Walk to the engineering station to control air service.";
        if (action is ActionCode.ApuGuardOpen or ActionCode.ApuGuardClose or ActionCode.ApuIsolate or ActionCode.ApuReset
            && !peer.Walking && peer.Seat >= 0)
            return "Walk to the APU isolation panel to operate its guard and lever.";
        if (ClientSpacecraftSystems.TryManualRepair(command, out _, out _))
            return "Use the matching tool at the repair point and hold it there to complete the work.";
        if (circuitAction && !peer.Walking && peer.Seat >= 0)
            return "Walk to the engineering distribution panel to operate a breaker.";
        if (action is ActionCode.BusIsolate or ActionCode.BusConnect
            && !peer.Walking && peer.Seat >= 0)
            return "Leave the seat and approach the systems service panel.";
        if (action is ActionCode.WarpDischargeStart or ActionCode.WarpDischargeStop
            && !peer.Walking && peer.Seat >= 0)
            return "Leave the seat and approach the warp service panel.";
        if (ClientSpacecraftSystems.TryReadSealantCommand(command, out var compartment))
        {
            if (!peer.Walking && peer.Seat >= 0) return "Leave the seat and approach the compartment sealant unit.";
            float localZ = _body.Root.transform.InverseTransformPoint(actorPosition).z;
            var actorCompartment = localZ < -9.1f ? SpacecraftCompartment.Entry
                : localZ < -4.84f ? SpacecraftCompartment.Airlock : SpacecraftCompartment.Cabin;
            if (actorCompartment != compartment) return "Enter the compartment containing this sealant unit.";
        }
        if (peer.Walking || peer.Seat < 0)
        {
            if (!AtControl(peer, command, actorPosition))
                return "Approach the matching control on your side of the compartment.";
            if (peer.Seat < 0 && !Stationary(_body.State)) return "Use a reserved cabin attachment while the ship is moving.";
        }
        else if (peer.Seat > 1 || !peer.Attached || !peer.Prepared)
            return "Use a flight console or walk to a systems station.";
        var flight = _body.State;
        if (flight.Travel != SpacecraftTravel.None && !(action == ActionCode.ShoreAir && _systems.State.ShoreAirEnabled))
            return "Wait for the active journey to finish.";
        string result;
        switch (action)
        {
            case ActionCode.CabinDoor:
                if (_systems.State.CabinDoorOpen && HatchObstructed(cabin: true))
                    return "Move clear of the cabin doorway before closing it.";
                if (!_systems.TryCommand(command, flight, out result, _body.InnerDoorClosed, _body.CabinDoorClosed))
                    return result;
                _body.SetSystemsState(SystemsState(_systems.State));
                break;
            case ActionCode.Power:
                if (flight.Docked) return "Release the docking latch before starting the engine.";
                if (!flight.Powered && !_systems.CanThrust) return "The engine needs fuel and electrical power.";
                _body.SetPower(!flight.Powered, flight.Dampeners);
                result = flight.Powered ? "Engine offline." : "Engine online.";
                break;
            case ActionCode.Dampeners:
                if (flight.Docked) return "The docking latch holds the ship's attitude and motion.";
                if (_stationAssist) return "Select HOLD on the chair console before switching off the dampeners.";
                if (flight.Region == SpacecraftRegion.Space && HasUnseatedCabinPlayer(allowCabinWalking: true))
                    return "Dampeners stay engaged until crew are back inside and ready for flight.";
                _body.SetPower(flight.Powered, !flight.Dampeners);
                result = flight.Dampeners ? "Inertial dampeners off." : "Inertial dampeners on.";
                break;
            case ActionCode.Door:
                if (flight.DoorOpen && HatchObstructed(outer: true))
                    return "Move clear of the rear ramp before closing it.";
                if (!flight.DoorOpen)
                {
                    if (!_systems.State.EmergencyOverride)
                    {
                        if (!Stationary(flight) && !ClientSpacecraftBody.StoppedInSpace(FlightTelemetry(flight)))
                            return "Stop the ship before opening the rear ramp, or use the emergency override.";
                        if (flight.Powered && flight.Region != SpacecraftRegion.Space)
                            return "Switch ENGINE off before opening the rear ramp.";
                    }
                    if (!_systems.CanOpenOuter(flight, out result, _body.InnerDoorClosed, _body.CabinDoorClosed)) return result;
                }
                _body.SetDoorOpen(!flight.DoorOpen);
                result = DoorMotionFeedback(_systems.State, "Rear ramp", !flight.DoorOpen);
                break;
            case ActionCode.Gear:
                if (flight.Docked) return "The landing gear is locked while docked.";
                if (!_body.SetGear(!flight.GearDeployed)) return "Lift clear of the ground before retracting the landing gear.";
                result = SpacecraftElectrical.IsPowered(_systems.State.Electrical, SpacecraftCircuit.Propulsion)
                    ? flight.GearDeployed ? "Landing gear retracting." : "Landing gear deploying."
                    : "Landing gear request stored. Restore the propulsion circuit to move the gear.";
                break;
            default:
                if ((action is ActionCode.AirlockToCabin or ActionCode.AirlockToEntry)
                    && (HatchObstructed(cabin: true) || HatchObstructed()))
                    return "Move clear of both pressure doorways before starting an airlock cycle.";
                if (action == ActionCode.InnerDoor && _systems.State.InnerDoorOpen && HatchObstructed())
                    return "Move clear of the inner airlock doorway before closing it.";
                if (!_systems.TryCommand(command, flight, out result, _body.InnerDoorClosed, _body.CabinDoorClosed)) return result;
                _body.SetSystemsState(SystemsState(_systems.State));
                break;
        }
        if (!peer.Walking)
        {
            ReplayConsolePose(peer, action switch
            {
                ActionCode.Power => SpacecraftConsoleControl.Power,
                ActionCode.Dampeners => SpacecraftConsoleControl.Dampeners,
                ActionCode.Door => SpacecraftConsoleControl.Door,
                ActionCode.Gear => SpacecraftConsoleControl.Gear,
                _ => SpacecraftConsoleControl.None
            });
        }
        else RecordStationInteraction(peer, command, actorPosition);
        return result;
    }

    private static string DoorMotionFeedback(SpacecraftSystemState systems, string door, bool open)
        => SpacecraftElectrical.IsPowered(systems.Electrical, SpacecraftCircuit.Airlock)
            ? door + (open ? " opening." : " closing.")
            : systems.EmergencyOverride ? door + " moving on the slow emergency drive."
            : door + " request stored. Restore the airlock circuit or use the emergency override.";

    private void RecordStationInteraction(Peer peer, string command, Vector3 actorPosition)
    {
        if (_body != null && peer.Walking && peer.Seat >= 0 && TryActor(peer, out _, out _, out var actor))
        {
            var contact = ClientSpacecraftStations.LocalControlContact(command,
                _body.Root.transform.InverseTransformPoint(actorPosition), _body.State.Systems);
            _interactions[peer.Seat] = new SpacecraftCrewInteraction(actor, contact.Position, 1)
                { LocalRotation = contact.Rotation, Press = contact.Press };
            _interactionUntil[peer.Seat] = Now + 650;
            _apuGuardContacts[peer.Seat] = command is "apu-guard-open" or "apu-guard-close"
                ? new ApuGuardContact(actor, contact, _body.State.Systems.ApuGuardAmount) : null;
        }
    }

    private void RefreshApuGuardContact(int seat, ulong actor)
    {
        if (_apuGuardContacts[seat] is not { } anchor) return;
        if (_body == null || anchor.Actor != actor || _interactions[seat].Actor != actor)
        {
            ClearStationInteraction(seat);
            return;
        }
        // Always transform the accepted contact from its original guard angle to avoid accumulated drift.
        var contact = ClientSpacecraftStations.MoveApuGuardContact(anchor.Contact, anchor.Amount,
            _body.State.Systems.ApuGuardAmount);
        _interactions[seat] = _interactions[seat] with
        {
            LocalTarget = contact.Position, LocalRotation = contact.Rotation, Press = contact.Press
        };
    }

    private void ClearStationInteraction(int seat)
    {
        _interactions[seat] = default;
        _interactionUntil[seat] = 0;
        _apuGuardContacts[seat] = null;
    }

    private readonly record struct ApuGuardContact(ulong Actor, SpacecraftControlContact Contact, float Amount);

    private bool HatchObstructed(bool outer = false, bool cabin = false)
    {
        if (_body == null) return false;
        if (LocalAlive() && AtHatch(TheForest.Utils.LocalPlayer.Transform.position, outer, cabin)) return true;
        if (_local != null && TrySurvivalActor(_local, out var local, out _)
            && AtHatch(CrewPosition(_local, local), outer, cabin)) return true;
        foreach (var peer in _peers.Values)
            if (TryActor(peer, out var position, out _, out _) && AtHatch(position, outer, cabin)) return true;
        if (!BoltNetwork.isRunning || !BoltNetwork.isServer) return false;
        foreach (var session in ServerSessions.Sessions)
        {
            if (!session.IsCurrent) continue;
            if (!ServerWorld.TryCapturePlayerBinding(session, out var binding, out _)
                || !ServerWorld.TryReadPlayerBinding(binding, out var actor, out _)) continue;
            if (AtHatch(new Vector3(actor.Position.X, actor.Position.Y, actor.Position.Z), outer, cabin)) return true;
        }
        return false;
    }

    private bool AtHatch(Vector3 position, bool outer, bool cabin) => cabin
        ? ClientSpacecraftModel.AtCabinDoorLocal(_body!.Root.transform.InverseTransformPoint(position))
        : outer ? _body!.AtOuterDoor(position) : _body!.AtInnerDoor(position);
}
