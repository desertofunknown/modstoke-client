// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Globalization;
using System.IO;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Client;

internal readonly record struct SpacecraftManualRepairWork(Guid Holder, ulong Actor, int Tool,
    string Command, Vector3 LocalContact, float Progress);

internal sealed partial class ClientSpacecraftService
{
    private const int MaximumManualRepairs = 3;
    private const long ManualRepairPulse = 200, ManualRepairTimeout = 800;
    private readonly ManualRepairInput?[] _manualRepairInputs = new ManualRepairInput?[MaximumManualRepairs];
    private readonly SpacecraftManualRepairWork[] _manualRepairWork = new SpacecraftManualRepairWork[MaximumManualRepairs];
    private int _manualRepairCount;
    private string? _manualRepairRequested;
    private string _manualRepairSent = "";
    private int _manualRepairRequestedTool = -1, _manualRepairSentTool = -1;
    private bool _manualRepairSending;
    private Guid _manualRepairSentLease, _manualRepairStateEpoch, _manualRepairStateToken, _manualRepairStateShip;
    private IntPtr _manualRepairSentActor;
    private long _manualRepairInputSequence, _manualRepairGesture, _nextManualRepairPulse;
    private long _manualRepairStateSequence, _receivedManualRepairSequence, _lastManualRepairState, _nextManualRepairState;

    private sealed class ManualRepairInput(Peer peer, Guid lease)
    {
        internal readonly Peer Peer = peer;
        internal readonly Guid Lease = lease;
        internal Guid Epoch, Ship;
        internal ulong Actor;
        internal IntPtr ActorPointer;
        internal long Sequence, Gesture, At, NextStart;
        internal bool Active;
        internal int Tool = -1;
        internal string Command = "";
        internal Vector3 Feet, Contact;
        internal float Elapsed, Duration;
        internal SpacecraftSystemState State;
    }

    private ReadOnlySpan<SpacecraftManualRepairWork> CurrentManualRepairs
    {
        get
        {
            if (!_ready || _body is not { Ready: true, TelemetryCurrent: true }
                || _body.State.Travel != SpacecraftTravel.None || _shipId == Guid.Empty
                || IsClient && (_manualRepairStateEpoch != _epoch || _manualRepairStateToken != _token
                    || _manualRepairStateShip != _shipId || _lastManualRepairState == 0
                    || Now - _lastManualRepairState >= ManualRepairTimeout)) return default;
            return _manualRepairWork.AsSpan(0, _manualRepairCount);
        }
    }

    private void BeginManualRepairFrame()
    {
        _manualRepairRequested = null;
        _manualRepairRequestedTool = -1;
    }

    private void RequestManualRepair(string command)
    {
        if (!ClientSpacecraftSystems.TryManualRepair(command, out var required, out _)) return;
        int tool = LocalManualRepairTool();
        if (tool < 0 || BoxStationEngineering.GetSources(_stationSystems.Layout)[tool].Kind != required) return;
        if (required == BoxStationToolKind.Welder
            && (CurrentEngineeringState is not { } state || !state.Tools[tool].Lit || state.Tools[tool].Fuel == 0)) return;
        _manualRepairRequested = command;
        _manualRepairRequestedTool = tool;
    }

    private int LocalManualRepairTool()
    {
        int seat = IsClient ? _clientSeat : _local?.Seat ?? -1;
        if (!_ready || BoardingPending || !LocalAlive() || !LocalCrewAccess || seat is < 0 or > 2
            || !(IsClient ? _clientWalking : _local?.Walking == true) || _rider?.Attached != true
            || _body is not { Ready: true, TelemetryCurrent: true } || _body.State.Travel != SpacecraftTravel.None
            || LocalFirefightingHolder == Guid.Empty || CurrentEngineeringState is not { } state) return -1;
        ulong actor = _actorIds[seat];
        foreach (var tool in state.Tools)
            if (tool.Place == BoxStationToolPlace.Held && tool.Holder == LocalFirefightingHolder && tool.Actor == actor)
                return tool.Id;
        return -1;
    }

    private string ManualRepairPrompt(string command)
    {
        if (!ClientSpacecraftSystems.TryManualRepair(command, out var required, out float seconds)) return "";
        string name = required switch { BoxStationToolKind.Screwdriver => "screwdriver", BoxStationToolKind.Welder => "welder", _ => "wrench" };
        int tool = LocalManualRepairTool();
        if (tool < 0) return "Hold a " + name + " to repair";
        if (BoxStationEngineering.GetSources(_stationSystems.Layout)[tool].Kind != required)
            return "Use a " + name + " for this repair";
        if (required == BoxStationToolKind.Welder && CurrentEngineeringState is { } tools)
        {
            if (tools.Tools[tool].Fuel == 0) return "Welder fuel empty";
            if (!tools.Tools[tool].Lit) return "R · Ignite welder";
        }
        foreach (var work in CurrentManualRepairs)
        {
            if (work.Command != command) continue;
            if (work.Holder != LocalFirefightingHolder) return "Another crew member is repairing this";
            if (work.Tool == tool)
                return "Keep holding primary / " + Mathf.FloorToInt(work.Progress * 100).ToString(CultureInfo.InvariantCulture) + "%";
        }
        string cost = ClientSpacecraftSystems.TryReadHullRepairCommand(command, out _) ? "1 hull plate"
            : command is "service-engine" or "service-life" or "service-warp" ? "1 circuit + 1 line spare"
            : required == BoxStationToolKind.Screwdriver ? "1 circuit spare" : "1 line spare";
        return "Hold primary to repair / " + seconds.ToString("0", CultureInfo.InvariantCulture) + " s / " + cost;
    }

    private void FlushManualRepairInput()
    {
        int held = LocalManualRepairTool();
        if (held >= 0 && ClientSpacecraftRider.WorldInputAvailable && !Cursor.visible
            && _body?.ContainsCabin(LocalPlayer.Transform.position) == true
            && BoxStationEngineering.GetSources(_stationSystems.Layout)[held].Kind == BoxStationToolKind.Welder
            && Input.GetKeyDown(KeyCode.R))
            CommandStationEngineering(BoxStationEngineeringControl.ToggleWelder, 0);
        string? request = _manualRepairRequested;
        if (request == null || _manualRepairRequestedTool != LocalManualRepairTool())
        {
            if (_manualRepairSending) SendManualRepairInput(false);
            _manualRepairSending = false;
            return;
        }
        bool changed = !_manualRepairSending || request != _manualRepairSent
            || _manualRepairRequestedTool != _manualRepairSentTool || _manualRepairSentLease != LocalFirefightingHolder
            || _manualRepairSentActor != LocalPlayer.Transform.Pointer;
        if (changed)
        {
            if (_manualRepairSending) SendManualRepairInput(false);
            _manualRepairGesture = checked(_manualRepairGesture + 1);
            _manualRepairSent = request;
            _manualRepairSentTool = _manualRepairRequestedTool;
            _manualRepairSentLease = LocalFirefightingHolder;
            _manualRepairSentActor = LocalPlayer.Transform.Pointer;
            _manualRepairSending = true;
            _nextManualRepairPulse = 0;
        }
        if (Now < _nextManualRepairPulse) return;
        _nextManualRepairPulse = Now + ManualRepairPulse;
        SendManualRepairInput(true);
    }

    private void SendManualRepairInput(bool active)
    {
        if (!_ready || _shipId == Guid.Empty || _manualRepairSentLease == Guid.Empty
            || _manualRepairSentLease != LocalFirefightingHolder) return;
        long sequence = checked(++_manualRepairInputSequence);
        var action = ManualRepairAction(_manualRepairSent);
        if (IsClient) _event?.ShipRepairInput(sequence, _manualRepairGesture, active, action, _manualRepairSentTool);
        else if (_local is { } peer)
            ReceiveManualRepairInput(peer, new Message { Sequence = sequence, Lease = _manualRepairSentLease,
                ShipRepairGesture = _manualRepairGesture, ShipRepairActive = active,
                Action = action, ShipRepairTool = _manualRepairSentTool });
    }

    private void ReceiveManualRepairInput(Peer peer, Message message)
    {
        if (IsClient || !_ready || !PeerCurrent(peer) || peer.Lease == Guid.Empty || peer.Lease != message.Lease
            || peer.Seat is < 0 or > 2 || !ReferenceEquals(_seats[peer.Seat], peer)
            || message.Sequence <= 0 || message.ShipRepairGesture <= 0) return;
        var input = _manualRepairInputs[peer.Seat];
        if (input == null || !ReferenceEquals(input.Peer, peer) || input.Lease != peer.Lease)
            _manualRepairInputs[peer.Seat] = input = new(peer, peer.Lease);
        if (message.Sequence <= input.Sequence) return;
        input.Sequence = message.Sequence;
        if (message.ShipRepairGesture < input.Gesture) return;
        string? command = ManualRepairCommand(message.Action);
        if (message.ShipRepairGesture == input.Gesture)
        {
            if (!message.ShipRepairActive || command != input.Command || message.ShipRepairTool != input.Tool
                || Now - input.At >= ManualRepairTimeout) StopManualRepair(input);
            else if (input.Active) input.At = Now;
            return;
        }
        StopManualRepair(input);
        input.Gesture = message.ShipRepairGesture;
        if (!message.ShipRepairActive || command == null || Now < input.NextStart) return;
        input.NextStart = Now + ManualRepairTimeout;
        string result = ManualRepairActor(peer, command, message.ShipRepairTool, out var feet, out var contact, out var actor);
        var state = _systems.State;
        if (result.Length == 0)
            ClientSpacecraftSystems.CanManualRepair(command, state, _body!.State.Powered, _body.State.Travel, out result);
        if (result.Length == 0)
            foreach (var other in _manualRepairInputs)
                if (other?.Active == true && other.Command == command)
                { result = "Another crew member is repairing this target."; break; }
        if (result.Length == 0)
        {
            ClientSpacecraftSystems.TryManualRepair(command, out _, out float duration);
            input.Epoch = _epoch; input.Ship = _shipId; input.Actor = actor; input.ActorPointer = peer.ActorPointer;
            input.Command = command; input.Tool = message.ShipRepairTool; input.Feet = feet; input.Contact = contact;
            input.Elapsed = 0; input.Duration = duration; input.State = state; input.At = Now; input.Active = true;
            _nextManualRepairState = 0;
        }
        SetManualRepairFeedback(peer, result);
    }

    private string ManualRepairActor(Peer peer, string command, int tool, out Vector3 localFeet,
        out Vector3 contact, out ulong actor)
    {
        localFeet = contact = default;
        actor = 0;
        if (_body is not { Ready: true, TelemetryCurrent: true } || _shipId == Guid.Empty
            || _travel != SpacecraftTravel.None || _body.State.Travel != SpacecraftTravel.None)
            return "Wait for a current ship with no active journey.";
        if (!PeerCurrent(peer) || !HasCrewAccess(peer) || !TryActor(peer, out var feet, out _, out actor)
            || !peer.Prepared || !peer.EntryPrepared || !peer.Attached || !peer.Walking
            || peer.Seat is < 0 or > 2 || !ReferenceEquals(_seats[peer.Seat], peer) || peer.Lease == Guid.Empty
            || !CabinActorCurrent(peer, feet)) return "Use a current walking attachment to repair the ship.";
        var engineering = _stationSystems.Engineering;
        if (!ValidManualRepairTool(command, tool) || engineering.Held(peer.Lease) != tool)
            return "Hold the required tool to repair this target.";
        var held = engineering.State.Tools[tool];
        if (held.Place != BoxStationToolPlace.Held || held.Holder != peer.Lease || held.Actor != actor)
            return "The held tool or player changed.";
        if (ClientSpacecraftSystems.TryReadHullRepairCommand(command, out _) && (!held.Lit || held.Fuel == 0))
            return held.Fuel == 0 ? "Welder fuel empty." : "Ignite the welder before repairing the hull.";
        foreach (var work in engineering.State.Work)
            if (work.Holder == peer.Lease || actor != 0 && work.Actor == actor)
                return "Finish or release the station work first.";
        if (!AtControl(peer, command, feet)) return "Approach the matching repair point with a clear reach.";
        localFeet = _body.Root.transform.InverseTransformPoint(feet);
        contact = ClientSpacecraftStations.LocalControlContact(command, localFeet, _body.State.Systems).Position;
        return string.Empty;
    }

    private void StepManualRepairs(float dt)
    {
        if (IsClient) return;
        float step = float.IsFinite(dt) ? Mathf.Clamp(dt, 0, .1f) : 0;
        _manualRepairCount = 0;
        for (int seat = 0; seat < _manualRepairInputs.Length; seat++)
        {
            var input = _manualRepairInputs[seat];
            if (input == null) continue;
            var peer = input.Peer;
            if (!ReferenceEquals(_seats[seat], peer) || peer.Lease != input.Lease || !PeerCurrent(peer))
            {
                StopManualRepair(input);
                _manualRepairInputs[seat] = null;
                continue;
            }
            if (!input.Active) continue;
            string result = ManualRepairActor(peer, input.Command, input.Tool, out var feet, out var contact, out var actor);
            var state = _systems.State;
            if (result.Length == 0 && (input.Epoch != _epoch || input.Ship != _shipId
                || input.Actor != actor || input.ActorPointer != peer.ActorPointer || Now - input.At >= ManualRepairTimeout
                || (feet - input.Feet).sqrMagnitude > .15f * .15f
                || (contact - input.Contact).sqrMagnitude > .000025f))
                result = "Repair interrupted. Release primary and start again.";
            if (result.Length == 0 && !ClientSpacecraftSystems.ManualRepairTargetUnchanged(input.Command, input.State, state))
                result = "The repair target changed. Release primary and start again.";
            if (result.Length == 0)
                ClientSpacecraftSystems.CanManualRepair(input.Command, state, _body!.State.Powered, _body.State.Travel, out result);
            if (result.Length != 0)
            {
                StopManualRepair(input);
                SetManualRepairFeedback(peer, result);
                continue;
            }
            input.Elapsed = MathF.Min(input.Duration, input.Elapsed + step);
            if (input.Elapsed >= input.Duration)
            {
                // The computer drains pending simulation and checks this same target again under its gate.
                bool committed = _systems.TryManualRepair(input.Command, _body!.State, input.State, out result,
                    _body.InnerDoorClosed, _body.CabinDoorClosed);
                if (committed) _body.SetSystemsState(SystemsState(_systems.State));
                StopManualRepair(input);
                SetManualRepairFeedback(peer, result);
                continue;
            }
            _manualRepairWork[_manualRepairCount++] = new(input.Lease, input.Actor, input.Tool,
                input.Command, input.Contact, input.Elapsed / input.Duration);
        }
        if (!_ready || _shipId == Guid.Empty || Now < _nextManualRepairState) return;
        _nextManualRepairState = Now + (_manualRepairCount == 0 ? 1000 : ManualRepairPulse);
        long sequence = checked(++_manualRepairStateSequence);
        foreach (var peer in _peers.Values)
            if (PeerCurrent(peer)) _event?.ShipRepairState(peer, sequence, _manualRepairWork, _manualRepairCount);
    }

    private void StopManualRepair(ManualRepairInput input)
    {
        if (!input.Active) return;
        input.Active = false;
        _nextManualRepairState = 0;
    }

    private void SetManualRepairFeedback(Peer peer, string result)
    {
        peer.Result = result;
        if (ReferenceEquals(peer, _local)) _feedback = result;
        _nextSnapshot = 0;
    }

    private void ReceiveManualRepairState(Message message)
    {
        if (message.Ship != _shipId || _shipId == Guid.Empty || message.Sequence <= _receivedManualRepairSequence
            || message.ShipRepairs is not { } work) return;
        _receivedManualRepairSequence = message.Sequence;
        _manualRepairStateEpoch = message.Epoch; _manualRepairStateToken = message.Token; _manualRepairStateShip = message.Ship;
        _lastManualRepairState = Now;
        work.CopyTo(_manualRepairWork, 0);
        _manualRepairCount = work.Length;
    }

    private void ResetManualRepairs()
    {
        Array.Clear(_manualRepairInputs);
        Array.Clear(_manualRepairWork);
        _manualRepairCount = 0;
        BeginManualRepairFrame();
        _manualRepairSent = "";
        _manualRepairSentTool = -1;
        _manualRepairSending = false;
        _manualRepairSentLease = _manualRepairStateEpoch = _manualRepairStateToken = _manualRepairStateShip = Guid.Empty;
        _manualRepairSentActor = IntPtr.Zero;
        _manualRepairInputSequence = _manualRepairGesture = _nextManualRepairPulse = 0;
        _manualRepairStateSequence = _receivedManualRepairSequence = _lastManualRepairState = _nextManualRepairState = 0;
    }

    private static ActionCode ManualRepairAction(string command)
    {
        if (ClientSpacecraftSystems.TryReadCircuitRepairCommand(command, out int circuit))
            return (ActionCode)((int)ActionCode.Circuit0Repair + circuit);
        if (ClientSpacecraftSystems.TryReadLineRepairCommand(command, out int line))
            return (ActionCode)((int)ActionCode.Line0Repair + line);
        return command switch
        {
            "service-power" => ActionCode.ServicePower, "service-life" => ActionCode.ServiceLife,
            "service-engine" => ActionCode.ServiceEngine, "service-hull" => ActionCode.ServiceHull,
            "service-hull-airlock" => ActionCode.ServiceHullAirlock, "service-hull-entry" => ActionCode.ServiceHullEntry,
            "service-warp" => ActionCode.ServiceWarp, _ => (ActionCode)255
        };
    }

    private static string? ManualRepairCommand(ActionCode action)
    {
        if (action is >= ActionCode.Circuit0Repair and <= ActionCode.Circuit5Repair)
            return "circuit-" + ((int)action - (int)ActionCode.Circuit0Repair).ToString(CultureInfo.InvariantCulture) + "-repair";
        if (action is >= ActionCode.Line0Repair and <= ActionCode.Line11Repair)
            return "line-" + ((int)action - (int)ActionCode.Line0Repair).ToString(CultureInfo.InvariantCulture) + "-repair";
        return action switch
        {
            ActionCode.ServicePower => "service-power", ActionCode.ServiceLife => "service-life",
            ActionCode.ServiceEngine => "service-engine", ActionCode.ServiceHull => "service-hull",
            ActionCode.ServiceHullAirlock => "service-hull-airlock", ActionCode.ServiceHullEntry => "service-hull-entry",
            ActionCode.ServiceWarp => "service-warp", _ => null
        };
    }

    private static bool ValidManualRepairTool(string command, int tool)
    {
        var sources = BoxStationEngineering.GetSources(BoxStationLayout.Current);
        return (uint)tool < (uint)sources.Length && ClientSpacecraftSystems.TryManualRepair(command, out var required, out _)
            && sources[tool].Kind == required;
    }

    private sealed partial class SpacecraftEvent
    {
        // Input is 88 bytes; a complete three-person work state is 194 bytes.
        internal void ShipRepairInput(long sequence, long gesture, bool active, ActionCode action, int tool) =>
            Emit(null, (Event: this, Owner: service, Sequence: sequence, Gesture: gesture, Active: active, Action: action, Tool: tool),
                static (writer, state) =>
        {
            string? command = ManualRepairCommand(state.Action);
            if (state.Sequence <= 0 || state.Gesture <= 0 || state.Owner._shipId == Guid.Empty
                || state.Owner._clientLease == Guid.Empty || command == null || !ValidManualRepairTool(command, state.Tool))
                throw new InvalidDataException();
            Head(writer, Kind.ShipRepairInput); state.Event.Envelope(writer, null); writer.Write(state.Sequence);
            GuidValue(writer, state.Owner._shipId); GuidValue(writer, state.Owner._clientLease);
            writer.Write(state.Gesture); writer.Write(state.Active); writer.Write((byte)state.Action); writer.Write(state.Tool);
        });

        internal void ShipRepairState(Peer peer, long sequence, SpacecraftManualRepairWork[] work, int count) =>
            Emit(peer, (Event: this, Owner: service, Peer: peer, Sequence: sequence, Work: work, Count: count), static (writer, state) =>
        {
            if (state.Sequence <= 0 || state.Owner._shipId == Guid.Empty || state.Count is < 0 or > MaximumManualRepairs)
                throw new InvalidDataException();
            Head(writer, Kind.ShipRepairState); state.Event.Envelope(writer, state.Peer); writer.Write(state.Sequence);
            GuidValue(writer, state.Owner._shipId); writer.Write((byte)state.Count);
            for (int index = 0; index < state.Count; index++)
            {
                var item = state.Work[index];
                GuidValue(writer, item.Holder); writer.Write(item.Actor); writer.Write(item.Tool);
                writer.Write((byte)ManualRepairAction(item.Command)); Vector(writer, item.LocalContact); writer.Write(item.Progress);
            }
        });

        private static SpacecraftManualRepairWork[] ReadShipRepairs(BinaryReader reader)
        {
            int count = reader.ReadByte();
            if (count > MaximumManualRepairs) throw new InvalidDataException();
            var work = new SpacecraftManualRepairWork[count];
            for (int index = 0; index < count; index++)
            {
                var holder = GuidValue(reader);
                ulong actor = reader.ReadUInt64();
                int tool = reader.ReadInt32();
                string? command = ManualRepairCommand((ActionCode)reader.ReadByte());
                var contact = Vector(reader);
                float progress = Range(reader, 0, 1);
                if (holder == Guid.Empty || actor == 0 || command == null || !ValidManualRepairTool(command, tool)
                    || !Finite(contact) || contact.sqrMagnitude > 400) throw new InvalidDataException();
                for (int previous = 0; previous < index; previous++)
                    if (work[previous].Holder == holder || work[previous].Actor == actor
                        || work[previous].Tool == tool || work[previous].Command == command) throw new InvalidDataException();
                work[index] = new(holder, actor, tool, command, contact, progress);
            }
            return work;
        }
    }
}
