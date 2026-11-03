// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using Endnight.Extensions;
using Modstoke.Sons;
using SonsSdk.Networking;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Client;

// The host owns the shared vessel; local players retain their own return guard.
internal sealed partial class ClientSpacecraftService
{
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly Dictionary<Guid, Peer> _peers = new();
    private readonly KeyValuePair<Guid, Peer>[] _peerSnapshot = new KeyValuePair<Guid, Peer>[16];
    private bool _peerSnapshotInUse;
    private readonly Peer?[] _seats = new Peer?[3];
    private readonly HashSet<Peer> _crew = new();
    private readonly ulong[] _actorIds = new ulong[3];
    private readonly bool[] _walking = new bool[3];
    private readonly SpacecraftCrewInteraction[] _interactions = new SpacecraftCrewInteraction[3];
    private readonly long[] _interactionUntil = new long[3];
    private readonly ApuGuardContact?[] _apuGuardContacts = new ApuGuardContact?[3];
    private readonly Rate _receiveRate = new();
    private ClientSpacecraftWorld _destination = new();
    private readonly ClientSpacecraftExpedition _expedition = new();
    private ClientSpacecraftBody? _body;
    private ClientSpacecraftRider? _rider;
    private ClientSpacecraftOccupants? _occupants;
    private ClientSpacecraftCockpit? _cockpit;
    private ClientSpacecraftAudio? _audio;
    private ClientSpacecraftEffects? _effects;
    private SpacecraftShorePower? _shorePower;
    private readonly ClientSpacecraftCabin _cabin = new();
    private ClientSpacecraftComputer _systems = new();
    private ClientSpacecraftStations? _stations;
    private ClientSpacecraftStorage? _storage;
    private long _nextCargoAttach, _cargoAttachUntil;
    private SpacecraftEvent? _event;
    private Peer? _local;
    private Action? _closeDevices;
    private bool _started, _ready, _clientReturn, _handledClientReturn;
    private Guid _shipId, _epoch, _nonce, _token, _clientLease, _ridingLease, _rejectedLease;
    private Guid _helloEpoch, _helloToken;
    private IntPtr _serverPointer;
    private uint _serverConnection;
    private int _clientSeat = -1;
    private Vector3? _clientExit;
    private bool _clientWalking;
    private Vector3? _clientCabinStandLocal;
    private Vector3 _islandPosition;
    private Quaternion _islandRotation = Quaternion.identity;
    private long _nextHello, _nextSnapshot, _nextControl, _nextSeatStatus, _lastSnapshot;
    private long _commandSequence, _controlSequence, _seatSequence, _snapshotSequence, _receivedSequence;
    private long _helloSequence, _pendingHelloSequence;
    private long _cancelledBoardingSequence;
    private long _travelStarted;
    private SpacecraftTravel _travel;
    private Pending? _pending;
    private ActionCode? _boardingCommand;
    private long _boardingUntil;
    private bool _cancelBoardingRequested;
    private long _nextBoardingCancel;
    private bool _assetsLoading;
    private string _status = "Waiting for the world.", _feedback = "";
    private bool IsClient => BoltNetwork.isRunning && !BoltNetwork.isServer;
    private bool BoardingPending => _cancelBoardingRequested || _boardingCommand.HasValue
        || _pending?.Action is ActionCode.Pilot or ActionCode.Passenger or ActionCode.PassengerRear
        || (IsClient ? _clientSeat >= 0 : _local?.Seat >= 0) && _rider?.Attached != true;
    private bool OnThread => Environment.CurrentManagedThreadId == _thread;
    private static long Now => Environment.TickCount64;
    internal ClientSpacecraftRider? Rider => _rider;
    internal bool UsingSpacecraft => _rider?.Active == true || _rider?.Restoring == true
        || (_ready && (IsClient ? _clientSeat >= 0 : _local?.Seat >= 0));
    internal SpacecraftRegion LocalRegion
    {
        get
        {
            var hasPosition = _expedition.TryReadPosition(out var position, out var retained);
            if (!retained) return SpacecraftRegion.Island;
            if (_ridingLease != Guid.Empty && _rider?.Attached == true && _body != null
                && ReferenceEquals(_rider.Spacecraft, _body)) return _body.State.Region;
            if (_surfaceLease != Guid.Empty) return _surfaceRegion;
            if (!hasPosition) return SpacecraftRegion.Island;
            if (position.y > 4000) return SpacecraftRegion.Space;
            if (position.y <= 2000) return SpacecraftRegion.Island;
            return (position - SpacecraftLayout.MoonOrigin).sqrMagnitude < (position - SpacecraftLayout.WorldOrigin).sqrMagnitude
                ? SpacecraftRegion.Moon : SpacecraftRegion.TestWorld;
        }
    }
    internal ClientSpacecraftView View { get; private set; } =
        new(false, false, "Waiting for the world.", "", -1, 0, 0, default, false, false, 0);

    internal void Start(Func<bool> acquire, Action release, Func<bool> focusCurrent, Action closeDevices)
    {
        if (_started) return;
        if (!Packets.SupportsClientRelayPolicy)
            throw new InvalidOperationException("Spacecraft requires authenticated host routing.");
        _closeDevices = closeDevices;
        _systems.Dispose(); _systems = new ClientSpacecraftComputer();
        _rider = new ClientSpacecraftRider(acquire, release, focusCurrent, command => Command(command));
        _occupants = new ClientSpacecraftOccupants();
        _expedition.Start();
        if (!_cabin.Start())
        {
            _status = _cabin.Status;
            View = View with { Ready = false, Status = _status };
        }
        _started = true;
        _event = new SpacecraftEvent(this);
        Packets.Register(_event);
        StartLifecycle();
    }

    internal void Stop()
    {
        if (!_started) return;
        // Return this exact actor before any captures, body or scene resources are removed.
        ReturnLocal("Spaceflight ended. Returned to your island anchor.");
        try
        {
            if (!IsClient)
            {
                foreach (var peer in _peers.Values) ReleaseSeat(peer, null, true);
                foreach (var peer in _peers.Values) _event?.Snapshot(peer, ++_snapshotSequence);
            }
            else if (_ready) _event?.Request(ActionCode.Cancel, ++_commandSequence, false);
        }
        catch (Exception error) { Console.WriteLine("Spacecraft shutdown notification: " + error.Message); }
        _started = false;
        StopLifecycle();
        try { if (_event != null) Packets.Unregister(_event); }
        catch (Exception error) { Console.WriteLine("Spacecraft packet cleanup: " + error.Message); }
        _event = null;
        ResetWorld();
        _rider?.Dispose(); _rider = null;
        _occupants?.Dispose(); _occupants = null;
        _cockpit?.Dispose(); _cockpit = null;
        _cabin.Dispose();
        _systems.Dispose();
        _expedition.Dispose(); _destination.Dispose();
    }

    internal void Update(ClientTabletPreferencesSnapshot? audio)
    {
        if (!_started || !OnThread) return;
        try
        {
            UpdateCore(audio);
            if (ServiceRunning) OperationCompleted(ServiceOperation.Update);
        }
        catch (Exception error) { Fail(error, ServiceOperation.Update); }
    }

    private void UpdateCore(ClientTabletPreferencesSnapshot? audio)
    {
        _worldAvailable = RefreshWorld();
        _cabin.Recover();
        if (!_cabin.Healthy) _status = _cabin.Status;
        if (!_worldAvailable || !_cabin.Healthy || Now < _retryServiceAt)
        {
            HoldService();
            UpdateHeldPlayer();
            return;
        }
        ResumeService();
        _occupants?.Update();
        _expedition.Update();
        if (_expedition.ReturnRequested)
        {
            ReturnLocal("Your expedition was returned to the island for save safety.");
            CancelLocalSeat();
        }
        UpdateOrbits();
        UpdateOrbitalStationPreparation();
        if (!IsClient) RestoreSavedVessel();
        if (!IsClient && (!_ready || _restoreNavigation)) { PublishView(); return; }
        if (BoardingPending && _rider?.Active != true && Application.isFocused
            && !GameRuntime.State.PauseMenuOpen && Input.GetKeyDown(KeyCode.X)) CancelBoarding();
        ProcessBoarding();
        if (_assetsLoading)
        {
            if (ClientSpacecraftBody.PrepareAssets(out var assetStatus))
            { _assetsLoading = false; _feedback = "Spacecraft ready. Deploy to place the ship."; }
            else if (!assetStatus.StartsWith("Loading ", StringComparison.Ordinal))
            { _assetsLoading = false; _feedback = assetStatus; }
        }
        var now = Now;
        if (IsClient)
        {
            if (now >= _nextHello)
            { _nextHello = now + (_ready ? 2000 : 1000); _event?.Hello(); }
            if (_ready && _body != null && now - _lastSnapshot > 1000)
            {
                _status = "Ship connection interrupted. Holding position while reconnecting.";
            }
            if (_pending != null && now >= _pending.Next)
            {
                if (_pending.Attempts++ >= 5)
                {
                    var preparing = _pending.Action == ActionCode.Prepare;
                    _feedback = "Request timed out. Check the ship state before trying again.";
                    _pending = null;
                    if (preparing) { _entryPrepared = false; _boardingCommand = null; }
                }
                else { _pending.Next = now + 500; _event?.Request(_pending.Action, _pending.Sequence, Prepared, _pending.StationTarget, _pending.StationSetting, _pending.StationAim, _pending.StationLease); }
            }
        }
        else
        {
            var localCurrent = RefreshLocal();
            if (localCurrent && _local != null)
            {
                _local.Prepared = Prepared;
                _local.Attached = _rider is { Attached: true, Faulted: false, Incapacitated: false };
                _local.ReadyAt = now;
            }
            // Nested callbacks and larger worlds retain their own complete snapshot.
            var ownsPeerSnapshot = !_peerSnapshotInUse && _peers.Count <= _peerSnapshot.Length;
            var peers = ownsPeerSnapshot ? _peerSnapshot : _peers.ToArray();
            var peerCount = ownsPeerSnapshot ? 0 : peers.Length;
            if (ownsPeerSnapshot) _peerSnapshotInUse = true;
            try
            {
                if (ownsPeerSnapshot)
                {
                    foreach (var pair in _peers)
                    {
                        peers[peerCount] = pair;
                        peerCount++;
                    }
                }
                for (var i = 0; i < peerCount; i++)
                {
                    var pair = peers[i];
                    var peer = pair.Value;
                    var identity = ReadPeerIdentity(peer);
                    if (identity == PeerIdentity.Retired)
                    { ReleaseSeat(peer, null, true); RetireMovementEvidence(peer); _crew.Remove(peer); _peers.Remove(pair.Key); continue; }
                    if (identity == PeerIdentity.Unavailable)
                    { SuspendAttachment(peer); continue; }
                    if (now - peer.LastSeen > 6000) SuspendAttachment(peer);
                    SendOrbits(peer, now);
                    if (!TryActor(peer, out _, out _, out _))
                    {
                        if (peer.Binding != null
                            && (!ServerWorld.TryReadPlayerLife(peer.Binding, out var life, out _)
                                || life.LifeState != PlayerLifeState.Dead))
                        { SuspendAttachment(peer); continue; }
                        peer.CrewApproved = false; peer.CrewRequestedAt = 0;
                        ReleaseSeat(peer, null, true); _crew.Remove(peer);
                        RetireMovementEvidence(peer);
                        peer.Binding = null;
                        if (now < peer.NextBindingRetry) continue;
                        peer.NextBindingRetry = now + 250;
                        peer.Binding = ServerWorld.TryCapturePlayerBinding(peer.Session!, out var binding, out _) ? binding : null;
                    }
                    else peer.NextBindingRetry = 0;
                }
            }
            finally
            {
                Array.Clear(peers, 0, peerCount);
                if (ownsPeerSnapshot) _peerSnapshotInUse = false;
            }
            for (var seat = 0; seat < 3; seat++)
            {
                var peer = _seats[seat];
                _actorIds[seat] = 0;
                _walking[seat] = false;
                if (peer == null || !peer.Walking || !peer.Attached || now >= _interactionUntil[seat])
                    ClearStationInteraction(seat);
                if (peer == null) continue;
                if (!TryActor(peer, out var position, out _, out var actor))
                { SuspendAttachment(peer); continue; }
                TryRestoreAttachmentEvidence(peer, position);
                var actorAtSeat = peer.Walking ? CabinActorCurrent(peer, position) : SeatedActorCurrent(peer, position);
                var statusCurrent = now - peer.ReadyAt <= 3000 && now - peer.LastSeen <= 6000;
                if (ReferenceEquals(peer, _local)) statusCurrent = true;
                if (peer.Attached && actorAtSeat && statusCurrent)
                {
                    peer.AttachedAt = now;
                    peer.AttachmentSuspended = false;
                    peer.Recovery = null;
                }
                else if (!statusCurrent || !actorAtSeat && now > peer.DistanceGrace
                    || !peer.Attached && now - peer.BoardedAt > 12000)
                    SuspendAttachment(peer);
                UpdateAttachmentRecovery(peer, now);
                if (peer.Attached) { _actorIds[seat] = actor; _walking[seat] = peer.Walking; }
                RefreshApuGuardContact(seat, actor);
            }
            RecordMovementEvidence();
            UpdateSurfaceRecovery(now);
            RetireUnusedEntryProtection();
            UpdateTravel(now);
            if (now >= _nextSnapshot)
            {
                _nextSnapshot = now + 100;
                var sequence = ++_snapshotSequence;
                foreach (var peer in _peers.Values) _event?.Snapshot(peer, sequence);
            }
        }
        UpdateStationBoarding();
        _rider?.SetSuspended(_cancelBoardingRequested || (IsClient
            ? _rider.Attached && (_body?.TelemetryCurrent != true
                || _awaitingAttachmentRecovery || _clientSeat is >= 0 and < 3 && _actorIds[_clientSeat] == 0)
            : _local?.AttachmentSuspended == true));
        SyncRider();
        if (_rider?.Seat is >= 0 and < 3) _rider.SetInteraction(_interactions[_rider.Seat]);
        _rider?.Update();
        HandleRiderFault();
        if (_rider?.ConsumeExitRequest() == true)
            Command(!_rider.Attached || _rider.Walking ? "exit" : "stand");
        if (_rider?.Active == true && !_rider.Walking && _rider.Seat is >= 0 and <= 1)
        {
            if (IsClient && _ready && now >= _nextControl)
            {
                _nextControl = now + 50;
                if (_rider.Seat == 0) _event?.Control(++_controlSequence, _clientLease, _rider.Controls);
                else _event?.ConsolePose(++_controlSequence, _clientLease, _rider.Controls);
            }
            else if (!IsClient && _local != null && _local.Seat == _rider.Seat
                && _local.Attached && !_local.AttachmentSuspended && TryActor(_local, out _, out _, out _))
            {
                if (_local.Seat == 0) { _local.Input = _rider.Controls; _local.LastControl = now; }
                ReceiveConsolePose(_local, _rider.Controls, _actorIds[_local.Seat]);
            }
        }
        if (IsClient && _ready && now >= _nextSeatStatus)
        {
            _nextSeatStatus = now + 500;
            _event?.SeatStatus(++_seatSequence, _clientLease,
                _rider is { Attached: true, Faulted: false, Incapacitated: false }, Prepared);
        }
        else if (!IsClient && _local != null && TryActor(_local, out _, out _, out _))
        {
            _local.Prepared = Prepared;
            _local.Attached = _rider is { Attached: true, Faulted: false, Incapacitated: false }
                && (now <= _local.DistanceGrace || ActorAtSeat(_local));
            _local.ReadyAt = now;
        }
        ObserveHeldFirefighting();
        SendPortableSnapshots();
        _body?.SetConsolePoses(CurrentConsolePose(0), CurrentConsolePose(1));
        _body?.UpdateVisuals(Time.unscaledDeltaTime);
        var localSuit = LocalEva(out bool suitWaiting, out _);
        var stationState = CurrentStationState;
        _cabin.Update(_body, LocalRegion, _orbitalStation, stationState, localSuit, suitWaiting, _rider);
        if (!_cabin.Healthy)
        {
            _status = _cabin.Status;
            HoldService();
            UpdateHeldPlayer();
            return;
        }
        if (_body != null)
        {
            _shorePower ??= new SpacecraftShorePower(_body.Root.transform);
            _shorePower.Update(_orbitalStation, _body.State, _body.TelemetryCurrent, stationState, audio, Time.unscaledDeltaTime, _interactions);
            // Create the rack collider before remote crew capture their owned
            // collision pairs and before the walking camera's late movement.
            EnsureEvaRack();
            EnsureToolRack();
            EnsureShipFirefighting();
            _occupants?.Set(_body, _actorIds, _walking, _interactions);
            _audio ??= new ClientSpacecraftAudio(_body.Root.transform);
            _effects ??= new ClientSpacecraftEffects(_body.Root.transform);
            if (_body.TelemetryCurrent)
            {
                var telemetry = FlightTelemetry(_body.State);
                _audio.Update(telemetry, audio);
                _effects.Update(telemetry);
            }
            else { _audio.Suspend(); _effects.Suspend(); }
            _stations ??= new ClientSpacecraftStations(_body, command => Command(command), _destination, _rider!,
                RequestManualRepair, ManualRepairPrompt);
            UpdateStorage();
        }
        PublishView();
    }

    internal void FixedUpdate()
    {
        try
        {
            if (!OnThread || !ServiceRunning) return;
            if (!_started || !_ready || _restoreNavigation)
            {
                _systems.SetShoreConnection(Guid.Empty, Guid.Empty);
                return;
            }
            StepEva(Time.fixedDeltaTime);
            StepStationSystems(Time.fixedDeltaTime);
            if (_body == null) { AdvanceOrbits(Time.fixedDeltaTime); HideOrbitalStation(); return; }
            var pilot = _seats[0];
            var fresh = !IsClient && _travel == SpacecraftTravel.None && pilot != null && pilot.Attached && !pilot.Walking
                && pilot.Prepared && PeerCurrent(pilot) && HasCrewAccess(pilot) && TryActor(pilot, out _, out _, out _)
                && Now - pilot.LastControl < 800;
            var flight = _body.State;
            var hover = new SpacecraftControls(Vector3.zero, Vector3.zero, flight.Powered, true, false,
                flight.DoorOpen, GearDeployed: flight.GearDeployed);
            var controls = fresh ? pilot!.Input : hover;
            if (!IsClient)
            {
                if (!_systems.State.CabinDoorOpen && !_body.CabinDoorClosed && HatchObstructed(cabin: true))
                    _systems.ReopenObstructedDoor(cabin: true);
                if (!_systems.State.InnerDoorOpen && !_body.InnerDoorClosed && HatchObstructed())
                    _systems.ReopenObstructedDoor(cabin: false);
                if (!flight.DoorOpen && flight.DoorAmount > .001f && HatchObstructed(outer: true))
                {
                    _body.SetDoorOpen(true);
                    flight = _body.State;
                }
                var crewCount = 0;
                var airlockCrewCount = 0;
                var entryCrewCount = 0;
                foreach (var occupant in _seats)
                {
                    if (occupant == null || WearingEva(occupant)) continue;
                    if (!TrySurvivalActor(occupant, out var point, out _)) continue;
                    var chest = CrewPosition(occupant, point) + _body.Root.transform.up * .9f;
                    if (!_body.ContainsCabinAir(chest)) continue;
                    crewCount++;
                    if (_body.IsInAirlock(chest)) airlockCrewCount++;
                    else if (_body.IsInEntryVestibule(chest)) entryCrewCount++;
                }
                _body.SetSystemsState(SystemsState(_systems.Update(Time.fixedDeltaTime, FlightTelemetry(flight),
                    crewCount, _body.InnerDoorClosed, airlockCrewCount, _body.CabinDoorClosed, entryCrewCount,
                    ShipRadiationEnvironment(flight))));
                StepManualRepairs(Time.fixedDeltaTime);
                controls = controls with { Powered = flight.Powered, Dampeners = fresh ? flight.Dampeners : true,
                    DoorOpen = flight.DoorOpen, GearDeployed = flight.GearDeployed };
                if (!_systems.CanThrust) _body.SetPower(false, true);
                if (!fresh && flight.Powered) _body.SetPower(_systems.CanThrust, true);
                var stationary = Stationary(flight);
                var crewBlocked = HasUnseatedCabinPlayer(allowCabinWalking: true);
                if (crewBlocked && flight.Region == SpacecraftRegion.Space && !flight.Dampeners)
                    _body.SetPower(flight.Powered, true);
                var feedback = "";
                var flightRequested = controls.Translation.sqrMagnitude > .0001f
                    || controls.Rotation.sqrMagnitude > .0001f || controls.Boost;
                if (feedback.Length == 0 && (flightRequested || controls.Powered && crewBlocked && stationary))
                    feedback = flight.Docked ? "Release the docking latch before using the thrusters."
                        : crewBlocked ? "Wait for crew to return inside and finish boarding."
                        : !controls.Powered ? "Switch ENGINE on before using the thrusters."
                        : !_systems.CanThrust ? "Restore the propulsion circuit, fuel feed and engine before applying thrust."
                        : flight.DoorOpen ? "Close the rear ramp before takeoff."
                        : !_body.DoorClosed ? "Wait for the rear ramp to finish closing before takeoff." : "";
                if (flight.Docked || !_body.DoorClosed || crewBlocked || !_systems.CanThrust)
                    controls = controls with { Translation = Vector3.zero, Rotation = Vector3.zero, Boost = false };
                if (crewBlocked && stationary) _body.SetPower(false, true);
                if (fresh) SetControlFeedback(pilot!, feedback);
            }
            var acceleration = Vector3.zero;
            var hoverAcceleration = Vector3.zero;
            var angularReference = Vector3.zero;
            if (!IsClient && !flight.Docked)
            {
                FlightAcceleration(flight, out acceleration, out hoverAcceleration);
                if (_stationAssist && TryStationPose(out var station)
                    && TryStationPose(out var nextStation, Time.fixedDeltaTime))
                {
                    // Following a velocity alone lags a curved orbit. Feed forward
                    // the berth's acceleration while PhysX still integrates every force.
                    hoverAcceleration = (nextStation.BerthVelocity - station.BerthVelocity) / Time.fixedDeltaTime - acceleration;
                    angularReference = station.AngularVelocity;
                }
            }
            _body.FixedUpdate(Time.fixedDeltaTime, controls, acceleration, hoverAcceleration,
                flight.Region == SpacecraftRegion.Space ? CruiseVelocityOffset() : Vector3.zero, angularReference);
            if (!IsClient) { ConstrainFlight(); RecordMovementEvidence(); }
            // Forces above use the completed physics state's time. Unity then
            // integrates this step; Update/save observes it at the advanced clock.
            AdvanceOrbits(Time.fixedDeltaTime);
            UpdateOrbitalStation();
            OperationCompleted(ServiceOperation.Physics);
        }
        catch (Exception error) { Fail(error, ServiceOperation.Physics); }
    }

    internal void LateUpdate()
    {
        if (!_started || !OnThread) return;
        try
        {
            if (!ServiceRunning)
            {
                if (_rider?.Attached == true) _rider.LateUpdate();
                return;
            }
            _firefightingInput = false;
            _foamInput = false;
            _repairInput = false;
            _materialInput = false;
            _engineeringInput = false;
            _shoreServiceInput = false;
            _engineeringRequested = null;
            BeginManualRepairFrame();
            _toolRack?.ApplyState(_ready && _body?.TelemetryCurrent == true ? CurrentEngineeringState : null);
            _extinguisherRack?.ApplyState(_ready && _body?.TelemetryCurrent == true ? CurrentFirefightingState : null);
            PrepareFirefightingHands();
            _rider?.LateUpdate();
            HandleRiderFault();
            ResolveStationStorageClosures();
            _destination.UpdateLocal(LocalRegion, CelestialView());
            UpdateOrbitalStationView();
            _occupants?.LateUpdate();
            _firefightingHands?.LateUpdate();
            _toolRack?.FinishFrame();
            _extinguisherRack?.FinishFrame();
            _waterEffects?.Update();
            _shipWaterEffects?.Update();
            UpdateShipFireEffects();
            bool evaUsed = UpdateEvaViews();
            bool shipFireUsed = UpdateShipFirefighting(!evaUsed);
            bool toolRackUsed = UpdateToolRack(!evaUsed && !shipFireUsed);
            // Target controls from the same final eye pose used to render them.
            // Native animation and the attached rider both reposition the camera.
            if (!evaUsed && !toolRackUsed && !shipFireUsed) _cockpit?.Update(View, _body);
            if (!evaUsed && !toolRackUsed && !shipFireUsed) UpdateStationControls();
            else { UpdateFirefightingControls(false); UpdateFoamControls(false); UpdateRepairControls(false); UpdateMaterialControls(false); UpdateFabricationControls(false); UpdateEngineeringControls(false); CancelShoreServicePress(); }
            FlushEngineeringInput();
            var otherControlUsed = evaUsed || toolRackUsed || shipFireUsed || _firefightingInput || _foamInput || _repairInput || _materialInput || _engineeringInput || _shoreServiceInput;
            var cabinControlUsed = _stations?.Update(View, !otherControlUsed, _interactions) == true;
            FlushManualRepairInput();
            if (toolRackUsed) ShowInteraction(_toolRackPrompt);
            else if (shipFireUsed) ShowInteraction(_shipFirefightingPrompt);
            else if (_shoreServiceInput) ShowInteraction(_shoreServicePrompt);
            else if (otherControlUsed || cabinControlUsed) ShowInteraction("");
            else UpdateInteractions();
            OperationCompleted(ServiceOperation.Presentation);
        }
        catch (Exception error) { Fail(error, ServiceOperation.Presentation); }
    }

    internal string Command(string command)
    {
        try { return CommandCore(command); }
        catch (Exception error) { Fail(error, ServiceOperation.Command); return _feedback; }
    }

    private string CommandCore(string command)
    {
        if (!OnThread || !_started || !ServiceRunning) return _status;
        string? crewRequestKey = null;
        var separator = command.IndexOf(':');
        if (separator > 0 && command[..separator] is "approve-crew" or "deny-crew")
        {
            crewRequestKey = command[(separator + 1)..];
            command = command[..separator];
        }
        if (command == "prepare") { TryPrepareEntry(); return _feedback; }
        if (command == "exit" && BoardingPending && _rider?.Attached != true)
            return CancelBoarding();
        if (command == "survey")
        {
            if (LocalRegion != SpacecraftRegion.TestWorld || UsingSpacecraft || !LocalAlive())
                return _feedback = "Leave the landed ship and walk to a survey beacon first.";
            _destination.TrySurvey(LocalPlayer.Transform.position, out var message);
            return _feedback = message;
        }
        if (command == "recover" && _expedition.Active)
        {
            ReturnLocal("Emergency return completed. Your island anchor was retained.");
            CancelLocalSeat();
            return _feedback;
        }
        if (command == "load-spares") return CommandLoadShipSpares();
        int stationTarget = -1;
        ushort stationSetting = 0;
        var action = command switch
        {
            "deploy" => ActionCode.Deploy, "board-pilot" => ActionCode.Pilot,
            "board-passenger" => ReachablePassenger(), "board-passenger-rear" => ActionCode.PassengerRear, "exit" => ActionCode.Exit,
            "board-passenger-front" => ActionCode.Passenger,
            "open-ramp" => ActionCode.OpenRamp,
            "launch" => ActionCode.Launch, "travel" => ActionCode.Travel,
            "return" => ActionCode.Return, "recover" => ActionCode.Recover,
            "stand" => ActionCode.Stand, "power" => ActionCode.Power,
            "dampeners" => ActionCode.Dampeners, "door" => ActionCode.Door,
            "gear" => ActionCode.Gear, "moon" => ActionCode.Moon,
            "station-dock" => ActionCode.StationDock, "station-assist" => ActionCode.StationAssist,
            _ when TryStationDoor(command, out stationTarget) => ActionCode.StationDoor,
            _ when TryStationPower(command, out stationTarget, out var stationAction) => stationAction,
            _ when TryStationSmes(command, out stationTarget, out stationSetting) => ActionCode.StationSmes,
            _ when TryStationShoreAir(command, out stationSetting) => ActionCode.StationShoreAir,
            _ when TryStationShoreFuel(command, out stationSetting) => ActionCode.StationShoreFuel,
            _ when TryStationShoreCoolant(command, out stationSetting) => ActionCode.StationShoreCoolant,
            _ when TryStationPlumbing(command, out stationTarget) => ActionCode.StationPlumbing,
            _ when TryStationPlumbingSetting(command, out stationTarget, out stationSetting, out var plumbingAction) => plumbingAction,
            _ when TryStationAirMode(command, out stationTarget, out stationSetting) => ActionCode.StationAirMode,
            _ when TryStationRoomThermal(command, out stationTarget, out stationSetting) => ActionCode.StationRoomThermal,
            _ when TryStationFireAlarm(command, out stationTarget, out stationSetting) => ActionCode.StationFireAlarm,
            _ when TryEvaCommand(command, out var evaSlot, out var evaService) =>
                (ActionCode)((int)ActionCode.EvaSuit0 + evaSlot * 2 + (evaService ? 1 : 0)),
            "inner-door" => ActionCode.InnerDoor, "override" => ActionCode.Override,
            "scrubber" => ActionCode.Scrubber, "generator" => ActionCode.Generator,
            "vent" => ActionCode.Vent, "service-supplies" => ActionCode.ServiceSupplies, "shore-air" => ActionCode.ShoreAir,
            "service-engine" => ActionCode.ServiceEngine,
            "seal-cabin" => ActionCode.SealCabin, "seal-airlock" => ActionCode.SealAirlock, "seal-entry" => ActionCode.SealEntry,
            "bus-isolate" => ActionCode.BusIsolate, "bus-connect" => ActionCode.BusConnect,
            "service-power" => ActionCode.ServicePower, "service-life" => ActionCode.ServiceLife,
            "service-hull" => ActionCode.ServiceHull,
            "service-hull-airlock" => ActionCode.ServiceHullAirlock, "service-hull-entry" => ActionCode.ServiceHullEntry,
            "service-warp" => ActionCode.ServiceWarp,
            "warp-discharge-start" => ActionCode.WarpDischargeStart, "warp-discharge-stop" => ActionCode.WarpDischargeStop,
            "select-island" => ActionCode.SelectIsland, "select-world" => ActionCode.SelectWorld,
            "select-moon" => ActionCode.SelectMoon, "engage-route" => ActionCode.EngageRoute,
            "cabin-door" => ActionCode.CabinDoor, "card-reader" => ActionCode.CardReader,
            "retina-scanner" => ActionCode.RetinaScanner, "crew-lock" => ActionCode.CrewLock,
            "approve-crew" => ActionCode.ApproveCrew, "deny-crew" => ActionCode.DenyCrew,
            "apu-isolate" => ActionCode.ApuIsolate, "apu-reset" => ActionCode.ApuReset,
            "apu-guard-open" => ActionCode.ApuGuardOpen, "apu-guard-close" => ActionCode.ApuGuardClose,
            "airlock-to-cabin" => ActionCode.AirlockToCabin, "airlock-to-entry" => ActionCode.AirlockToEntry,
            "airlock-cancel" => ActionCode.AirlockCancel,
            _ when ClientSpacecraftSystems.TryReadValveCommand(command, out var index, out var open) =>
                (ActionCode)((int)ActionCode.Valve0Open + index * 2 + (open ? 0 : 1)),
            _ when ClientSpacecraftSystems.TryReadCircuitCommand(command, out var circuit, out var isolate) =>
                (ActionCode)((int)ActionCode.Circuit0Open + circuit * 2 + (isolate ? 0 : 1)),
            _ when ClientSpacecraftSystems.TryReadCircuitRepairCommand(command, out var repairCircuit) =>
                (ActionCode)((int)ActionCode.Circuit0Repair + repairCircuit),
            _ when ClientSpacecraftSystems.TryReadLineRepairCommand(command, out var repairLine) =>
                (ActionCode)((int)ActionCode.Line0Repair + repairLine),
            _ => (ActionCode)255
        };
        if (action == (ActionCode)255) return _feedback = "Unknown spaceflight request.";
        if (!_started || !_ready)
            return _feedback = "Spaceflight service is not ready. " + _status;
        if (_pending != null || _boardingCommand.HasValue || _cancelBoardingRequested)
            return _feedback = "Waiting for the previous spaceflight request.";
        if (IsBoarding(action))
        {
            if (!LocalCrewAccess) return _feedback = "Crew authorization required. Request access at the card reader.";
            if (!LocalAlive()) return _feedback = "A living player is required.";
            if (_body == null || !_body.Ready) return _feedback = "Deploy the spacecraft and wait for its native assets.";
            if (!OwnWalkingChair(SeatFor(action)) && !Stationary(_body.State)) return _feedback = StationaryFailure(_body.State);
            if (!_body.CanReachSeat(LocalPlayer.Transform.position, SeatFor(action), out var reachReason, LocalPlayer.Transform))
                return _feedback = reachReason;
            _boardingCommand = action; _boardingUntil = Now + 6000;
            return _feedback = "Preparing your chair. X cancels sitting.";
        }
        if (action is ActionCode.Launch or ActionCode.Travel or ActionCode.Return or ActionCode.Moon or ActionCode.EngageRoute)
        {
            if (_rider?.Attached != true || _rider.Walking || _rider.Seat > 1)
                return _feedback = "Sit at a flight console to start a journey.";
            if (!Prepared) return _feedback = "Your return anchor or destination collision world is not ready.";
        }
        return Dispatch(action, crewRequestKey, stationTarget, stationSetting);
    }

    private string Dispatch(ActionCode action, string? crewRequestKey = null, int stationTarget = -1, ushort stationSetting = 0,
        Vector3 stationAim = default)
    {
        if (IsClient)
        {
            if (_pending != null) return _feedback = "Waiting for the host's previous reply.";
            _pending = new Pending(action, ++_commandSequence, stationTarget, stationSetting, stationAim, _clientLease);
            _event?.Request(action, _pending.Sequence, Prepared, stationTarget, stationSetting, stationAim, _pending.StationLease);
            return _feedback = "Request sent to the host.";
        }
        if (!RefreshLocal() || _local == null) return _feedback = "Waiting for a current living player.";
        _local.Prepared = Prepared;
        _local.Attached = _rider is { Attached: true, Faulted: false, Incapacitated: false }
            && (Now <= _local.DistanceGrace || ActorAtSeat(_local));
        _local.ReadyAt = Now;
        _feedback = ApplyCommand(_local, ++_commandSequence, action, crewRequestKey, stationTarget, stationSetting, stationAim, _local.Lease);
        if (action == ActionCode.Prepare)
        {
            var hasEntry = _movement.TryGetValue(_local, out var entry);
            ConfirmEntryPreparation(_local.EntryPrepared && hasEntry, entry?.IslandPosition ?? default);
        }
        return _feedback;
    }

    private string CancelBoarding()
    {
        if (_cancelBoardingRequested) return _feedback;
        var requested = _pending?.Action is ActionCode.Pilot or ActionCode.Passenger or ActionCode.PassengerRear
            || (IsClient ? _clientSeat >= 0 : _local?.Seat >= 0);
        _boardingCommand = null;
        if (!requested) return _feedback = "Boarding cancelled.";
        _cancelBoardingRequested = true;
        _nextBoardingCancel = 0;
        _pending = null;
        _rider?.SetSuspended(true);
        _feedback = "Cancelling the seat request.";
        ProcessBoarding();
        return _feedback;
    }

    private void ProcessBoarding()
    {
        if (_cancelBoardingRequested)
        {
            if (_pending != null || Now < _nextBoardingCancel) return;
            _nextBoardingCancel = Now + 1000;
            if (IsClient)
            {
                _pending = new Pending(ActionCode.CancelBoarding, ++_commandSequence);
                _cancelledBoardingSequence = _pending.Sequence;
                _event?.Request(_pending.Action, _pending.Sequence, Prepared);
            }
            else
            {
                Dispatch(ActionCode.CancelBoarding);
                if (_local is { Seat: < 0 } or { Walking: true }) _cancelBoardingRequested = false;
            }
            return;
        }
        if (!_boardingCommand.HasValue) return;
        if (!LocalAlive() || Now >= _boardingUntil)
        { _boardingCommand = null; _feedback = "Boarding stopped. Finish your current action and try again."; return; }
        if (LocalPlayer.Inventory == null || LocalPlayer.Inventory.WasCollected
            || LocalPlayer.Inventory.CurrentView != TheForest.Items.Inventory.PlayerInventory.PlayerViews.World
            || _rider?.Restoring == true) return;
        var action = _boardingCommand.Value;
        if (_body?.Ready != true)
        { _boardingCommand = null; _feedback = "The chair is unavailable."; return; }
        if (!_body.CanReachSeat(LocalPlayer.Transform.position, SeatFor(action), out var reachReason, LocalPlayer.Transform))
        { _boardingCommand = null; _feedback = reachReason; return; }
        if (!OwnWalkingChair(SeatFor(action)) && !Stationary(_body.State))
        { _boardingCommand = null; _feedback = StationaryFailure(_body.State); return; }
        if (_pending != null) return;
        if (!_destination.Ensure()) { _boardingCommand = null; _feedback = "Destination collision world is unavailable: " + _destination.Status; return; }
        if (!_entryPrepared || !Prepared)
        { TryPrepareEntry(); return; }
        _boardingCommand = null;
        Dispatch(action);
    }

    private bool Prepared => _cabin.Healthy && _rider?.Incapacitated != true && _body?.Ready == true && _destination.Ready
        && _expedition.Ready && _expedition.Active && _expedition.Current && !_expedition.ReturnRequested;
    private static bool Stationary(SpacecraftFlightState value) => (value.Docked || value.Grounded
        && value.Velocity.sqrMagnitude < 1 && value.AngularVelocity.sqrMagnitude < .04f)
        && value.Travel == SpacecraftTravel.None;

    private static string StationaryFailure(SpacecraftFlightState value) => value.Travel != SpacecraftTravel.None
        ? "Wait for the journey to finish before using the seats or ramp."
        : !value.Grounded ? "Land upright on solid ground before using the seats or ramp."
        : "Stop the spacecraft's movement before using the seats or ramp.";

    private void SetControlFeedback(Peer pilot, string message)
    {
        if (pilot.ControlFeedback == message) return;
        var previous = pilot.ControlFeedback;
        pilot.ControlFeedback = message;
        if (message.Length != 0 || pilot.Result == previous) pilot.Result = message;
        if (ReferenceEquals(pilot, _local) && (message.Length != 0 || _feedback == previous)) _feedback = message;
    }

    private string ApplyCommand(Peer peer, long sequence, ActionCode action, string? crewRequestKey = null,
        int stationTarget = -1, ushort stationSetting = 0, Vector3 stationAim = default, Guid stationLease = default)
    {
        if (!PeerCurrent(peer) || sequence <= 0) return "The player session changed.";
        if (sequence == peer.LastCommand) return peer.Result;
        if (sequence < peer.LastCommand) return "This request has expired.";
        if (!peer.Commands.Take(Now, 4, 4)) return "Please wait before trying again.";
        peer.LastCommand = sequence;
        return peer.Result = Execute(peer, action, crewRequestKey, stationTarget, stationSetting, stationAim, stationLease);
    }

    private string Execute(Peer peer, ActionCode action, string? crewRequestKey, int stationTarget, ushort stationSetting,
        Vector3 stationAim, Guid stationLease)
    {
        if (action == ActionCode.Cancel)
        {
            ReleaseSeat(peer, null, true);
            if (TryActor(peer, out var returned, out _, out _) && !peer.Prepared && ConfirmMovementReturn(peer, returned)) _crew.Remove(peer);
            return "Your spacecraft seat was released. Return to your island anchor.";
        }
        if (!TryActor(peer, out var actorPosition, out var actorRotation, out _)) return "A living current player is required.";
        if (action == ActionCode.Deploy)
        {
            if (_restoreNavigation) return "Wait for the saved spacecraft to finish loading.";
            if (_orbits is { PendingSeconds: >= 1 }) return "Navigation data unavailable.";
            if (_body != null) return "A shared spacecraft is already deployed.";
            if (actorPosition.y >= 2000) return "Deploy the spacecraft on the island.";
            if (!ClientSpacecraftBody.PrepareAssets(out var assetStatus))
            { _assetsLoading = assetStatus.StartsWith("Loading ", StringComparison.Ordinal); return assetStatus; }
            _assetsLoading = false;
            if (!_destination.Ensure()) return "Destination collision world is unavailable.";
            if (!TryPlacement(actorPosition, actorRotation, out var position, out var rotation, out var reason)) return reason;
            _body = new ClientSpacecraftBody(position, rotation, true);
            if (!_body.Ready) { _body.Dispose(); _body = null; return "The native spacecraft assets could not be prepared."; }
            ResetMovementEvidence();
            ResetAccess();
            if (_savedShip != null)
            {
                if (!_systems.TryRestore(_savedShip.Systems!))
                {
                    RemoveBody(); InvalidSave();
                    return "The ship's saved systems could not be restored. Saved data was preserved.";
                }
                _body.RestoreActuators(_savedShip.RearDoorOpen, _savedShip.RearDoorAmount,
                    _savedShip.InnerDoorAmount, _savedShip.CabinDoorAmount, _savedShip.GearDeployed, _savedShip.GearAmount);
            }
            else _systems.Reset();
            _body.SetSystemsState(SystemsState(_systems.State));
            _body.Impact += OnShipImpact;
            _body.CollisionImpact += OnStationCollision;
            _storage = new ClientSpacecraftStorage();
            _nextCargoAttach = 0; _cargoAttachUntil = Now + 12000;
            _shipId = _savedShip?.Id ?? Guid.NewGuid(); _islandPosition = position; _islandRotation = rotation;
            _restoreNavigation = false;
            BeginFlightFrame();
            return "Spacecraft deployed. Walk to the rear ramp to board.";
        }
        if (_body == null) return "Deploy a spacecraft first.";
        if (!AllowsLockedAction(peer, action, actorPosition)) return "Crew authorization required. Request access at the card reader.";
        if (IsAccessAction(action)) return ExecuteAccess(peer, action, actorPosition, crewRequestKey);
        var flight = _body.State;
        if (action == ActionCode.CancelBoarding)
        {
            if (peer.Seat < 0) return "Boarding cancelled.";
            if (peer.Walking) return "You remain standing in the cabin.";
            if (peer.Attached || peer.AttachedAt != 0) action = ActionCode.Stand;
            else
            {
                if (peer.Seat > 2 || peer.Lease == Guid.Empty || !ReferenceEquals(_seats[peer.Seat], peer)
                    || !peer.EntryPrepared || !_body.Ready
                    || !_movement.TryGetValue(peer, out var evidence) || !evidence.Active || evidence.ReturnIssued
                    || evidence.Ship != _shipId || !CurrentMovement(peer, evidence))
                    return "Waiting for the current seat reservation before cancelling.";
                if (!Stationary(flight)) return "Wait for the ship to stop before cancelling seat preparation.";
                if (!_body.TryCabinStandingPoint(peer.Seat, ActorTransform(peer), out var standing))
                    return "The aisle beside your chair is obstructed. Seat cancellation is waiting.";
                // Use the same host-selected endpoint and surface bounds as a
                // normal exit; a client position alone cannot create walking evidence.
                if ((actorPosition - standing).sqrMagnitude > 2.5f * 2.5f
                    || !SurfaceWalkPoint(actorPosition, flight.Region, out _))
                    return "Waiting for a clear standing position beside your chair.";
                ReleaseSeat(peer, standing, false);
                return "Boarding cancelled. You remain aboard.";
            }
        }
        if (action is ActionCode.StationDock or ActionCode.StationAssist)
            return ExecuteDocking(peer, action, actorPosition);
        if (action == ActionCode.StationDoor)
            return ExecuteStationDoor(peer, stationTarget, actorPosition);
        if (IsStationPower(action))
            return ExecuteStationPower(peer, stationTarget, StationChannel(action), actorPosition);
        if (action == ActionCode.StationSmes)
            return ExecuteStationSmes(peer, stationTarget, stationSetting, actorPosition);
        if (action is ActionCode.StationShoreAir or ActionCode.StationShoreFuel or ActionCode.StationShoreCoolant)
        {
            if (stationLease == Guid.Empty || stationLease != peer.Lease) return "Your station attachment changed. Try again.";
            var service = action switch
            {
                ActionCode.StationShoreAir => ShoreServiceKind.Air,
                ActionCode.StationShoreFuel => ShoreServiceKind.Fuel,
                _ => ShoreServiceKind.Coolant
            };
            return ExecuteStationShoreService(peer, stationSetting, actorPosition, service);
        }
        if (IsStationPlumbing(action))
            return ExecuteStationPlumbing(peer, action, stationTarget, stationSetting, actorPosition);
        if (action == ActionCode.StationAirMode)
            return ExecuteStationAirMode(peer, stationTarget, stationSetting, actorPosition);
        if (action == ActionCode.StationRoomThermal)
            return ExecuteStationRoomThermal(peer, stationTarget, stationSetting, actorPosition);
        if (action == ActionCode.StationFireAlarm)
            return ExecuteStationFireAlarm(peer, stationTarget, stationSetting, actorPosition);
        if (action == ActionCode.StationFirefighting)
            return stationLease == Guid.Empty || stationLease != peer.Lease ? "Your station attachment changed. Try again."
                : ExecuteStationFirefighting(peer, stationTarget, stationSetting, stationAim, actorPosition);
        if (action == ActionCode.StationFoam)
            return stationLease == Guid.Empty || stationLease != peer.Lease ? "Your station attachment changed. Try again."
                : ExecuteStationFoam(peer, stationTarget, stationSetting, stationAim, actorPosition);
        if (action == ActionCode.StationRepair)
            return stationLease == Guid.Empty || stationLease != peer.Lease ? "Your station attachment changed. Try again."
                : ExecuteStationRepair(peer, stationTarget, stationSetting, stationAim, actorPosition);
        if (action == ActionCode.StationMaterials)
            return stationLease == Guid.Empty || stationLease != peer.Lease ? "Your station attachment changed. Try again."
                : ExecuteStationMaterials(peer, stationTarget, stationSetting, stationAim, actorPosition);
        if (action == ActionCode.StationEngineering)
            return stationLease == Guid.Empty || stationLease != peer.Lease ? "Your station attachment changed. Try again."
                : ExecuteStationEngineering(peer, stationTarget, stationSetting, actorPosition);
        if (action == ActionCode.StationCable)
            return stationLease == Guid.Empty || stationLease != peer.Lease ? "Your station attachment changed. Try again."
                : ExecuteStationCable(peer, stationTarget, stationSetting, stationAim);
        if (action == ActionCode.StationFloor)
            return stationLease == Guid.Empty || stationLease != peer.Lease ? "Your station attachment changed. Try again."
                : ExecuteStationFloor(peer, stationTarget, stationSetting, stationAim);
        if (action is >= ActionCode.EvaSuit0 and <= ActionCode.EvaService2)
        {
            int evaAction = (int)action - (int)ActionCode.EvaSuit0;
            return ExecuteEva(peer, evaAction / 2, (evaAction & 1) != 0, actorPosition);
        }
        if (action == ActionCode.OpenRamp)
        {
            if (!Stationary(flight) || flight.Powered) return "Park and switch spacecraft power off before opening the ramp.";
            if (_seats[0] != null) return "Ask the seated pilot to open the rear ramp.";
            if (!AtRampApproach(actorPosition)) return "Stand on solid ground outside the rear ramp to open it.";
            if (!_systems.CanOpenOuter(flight, out var interlock, _body.InnerDoorClosed, _body.CabinDoorClosed)) return interlock;
            _body.SetDoorOpen(true);
            return DoorMotionFeedback(_systems.State, "Rear ramp", true);
        }
        if (action == ActionCode.Prepare)
        {
            peer.EntryPrepared = false;
            if (!peer.Prepared || !Stationary(flight) || flight.Region != SpacecraftRegion.Island
                || !(_body.ContainsCabin(actorPosition) || AtRampApproach(actorPosition)))
                return "Approach a spacecraft chair to board.";
            if (!TryIslandReturnAnchor(out var anchor, out var rotation)
                || !BeginMovementExpedition(peer, anchor, rotation)) return "The island return location could not be confirmed.";
            peer.EntryPrepared = true;
            peer.ForceReturn = false; peer.Exit = null;
            return "Seat ready.";
        }
        if (action == ActionCode.EngageRoute)
        {
            action = _systems.State.SelectedDestination == SpacecraftRegion.Island ? ActionCode.Return
                : flight.Region != SpacecraftRegion.Space ? ActionCode.Launch
                : _systems.State.SelectedDestination == SpacecraftRegion.Moon ? ActionCode.Moon : ActionCode.Travel;
        }
        if (action is ActionCode.Launch or ActionCode.Travel or ActionCode.Return or ActionCode.Moon) return BeginTravel(peer, action);
        if (IsSystemAction(action)) return ExecuteSystem(peer, action, actorPosition);
        if (action == ActionCode.Stand)
        {
            // A blocked return to the chair can leave the client in its previous
            // walking pose. Retain only that still-current movement contract.
            if (!peer.Walking && peer.Seat >= 0 && Now - peer.BoardedAt < 3000
                && _movement.TryGetValue(peer, out var priorWalk) && priorWalk.CabinActive
                && priorWalk.Active && !priorWalk.ReturnIssued && priorWalk.Ship == _shipId
                && peer.Lease != Guid.Empty && peer.Prepared && ReferenceEquals(_seats[peer.Seat], peer)
                && priorWalk.CabinEntered && !priorWalk.CabinFailed && priorWalk.CabinLease == peer.Lease
                && CurrentMovement(peer, priorWalk) && AcceptCabinSample(priorWalk, actorPosition, Now, false))
            {
                peer.Walking = peer.Attached = true;
                peer.CabinStandLocal = priorWalk.CabinLocal;
                _walking[peer.Seat] = true;
                peer.LastControl = 0;
                ClearConsolePose(peer);
                return "The chair approach is blocked. You remain standing.";
            }
            if (peer.Seat < 0 || !peer.Attached || !peer.Prepared) return "Wait until your seat is ready.";
            if (peer.Walking) return "You are already standing in the cabin.";
            if (flight.Travel != SpacecraftTravel.None) return "Remain seated until the journey finishes.";
            if (flight.Region == SpacecraftRegion.Space)
            {
                if (!_body.DoorClosed && !ClientSpacecraftBody.StoppedInSpace(FlightTelemetry(flight)))
                    return "Close the rear ramp or stop the ship before standing.";
            }
            else if (!_body.DoorClosed && !Stationary(flight)) return "Close the ramp before walking in flight.";
            if (!_body.TryCabinStandingPoint(peer.Seat, ActorTransform(peer), out var standing)) return "The aisle beside your chair is obstructed.";
            var local = _body.Root.transform.InverseTransformPoint(standing);
            if (!PrepareCabinMovement(peer, local)) return "Your cabin movement could not be confirmed. Remain seated and retry.";
            if (ReferenceEquals(peer, _local) && _rider != null && !_rider.BeginCabinWalk(_body, standing))
            {
                if (_movement.TryGetValue(peer, out var movement)) movement.CabinActive = false;
                return _rider.FailureReason;
            }
            ClearConsolePose(peer);
            peer.CabinStandLocal = local; peer.Walking = true;
            _walking[peer.Seat] = true;
            peer.LastControl = 0; peer.DistanceGrace = Now + 2000;
            if (peer.Seat == 0) _body.SetPower(flight.Powered, true);
            return flight.Region == SpacecraftRegion.Space
                ? "Zero gravity. Your chair remains reserved; an open rear hatch allows exterior movement. Vacuum is hazardous."
                : "Standing in the cabin. Approach your reserved chair and press E to sit.";
        }
        if (action == ActionCode.Exit)
        {
            if (peer.Seat < 0) return "You are not seated.";
            if (flight.Region == SpacecraftRegion.Space)
                return peer.Walking ? "Leave through the open rear hatch. Your return anchor and chair remain reserved."
                    : "Stand from your chair before approaching the airlock.";
            if (!Stationary(flight)) return StationaryFailure(flight);
            if (flight.Powered) return "Switch ENGINE off before disembarking.";
            if (!flight.DoorOpen) return "Open the rear ramp before disembarking.";
            var exit = actorPosition;
            if (!peer.Walking && !_body.TryCabinStandingPoint(peer.Seat, ActorTransform(peer), out exit)) return "The aisle beside your chair is obstructed.";
            ReleaseSeat(peer, exit, false);
            return "Standing in the cabin. Walk down the ramp to leave, or approach another chair.";
        }
        if (IsBoarding(action))
        {
            if (!peer.Prepared || Now - peer.ReadyAt > 3000) return "Your destination world and island return anchor must be ready before boarding.";
            if (!_destination.Ready) return "The host destination collision world is not ready.";
            var seat = SeatFor(action);
            if (peer.Seat == seat && peer.Walking && ReferenceEquals(_seats[seat], peer))
            {
                if (flight.Travel != SpacecraftTravel.None) return "Wait for travel to complete before returning to your chair.";
                var remainedSeated = _movement.TryGetValue(peer, out var walk) && walk.CabinActive
                    && !walk.CabinEntered && !walk.CabinFailed && walk.CabinLease == peer.Lease
                    && CurrentMovement(peer, walk) && CabinActorCurrent(peer, actorPosition)
                    && (actorPosition - SeatMovementOrigin(seat)).sqrMagnitude <= .25f;
                if (!remainedSeated && !_body.CanReachSeat(actorPosition, seat, out var returnReason, ActorTransform(peer))) return returnReason;
                if (ReferenceEquals(peer, _local) && _rider?.Walking == true && !_rider.ResumeSeat(_body, seat))
                    return _rider.FailureReason;
                ReleaseFirefighting(peer, retainEngineering: true);
                ClearConsolePose(peer);
                peer.Walking = false; peer.CabinStandLocal = null; _walking[seat] = false;
                ClearStationInteraction(seat);
                peer.Attached = false; peer.BoardedAt = Now; peer.DistanceGrace = Now + 3000;
                return remainedSeated ? "The aisle is blocked. You remain seated." : "Returning to your reserved chair.";
            }
            if (peer.Seat >= 0) return "Return to your reserved chair before changing seats.";
            if (!Stationary(flight)) return StationaryFailure(flight);
            if (!_body.CanReachSeat(actorPosition, seat, out var reachReason, ActorTransform(peer))) return reachReason;
            if (_seats[seat] != null) return seat == 0 ? "The pilot seat is occupied." : "That passenger chair is occupied.";
            if (!peer.EntryPrepared || !_movement.TryGetValue(peer, out var evidence)
                || !evidence.Active || evidence.ReturnIssued || !CurrentMovement(peer, evidence))
                return "The seat reservation expired. Please try the chair again.";
            ClearConsolePose(peer);
            peer.Seat = seat; peer.Lease = Guid.NewGuid(); peer.BoardedAt = Now; peer.AttachedAt = 0;
            peer.AttachmentSuspended = false;
            peer.Recovery = null;
            peer.DistanceGrace = Now + 12000;
            ClearStationInteraction(seat);
            peer.Exit = null; peer.ForceReturn = peer.Attached = false;
            peer.Walking = false; peer.CabinStandLocal = null;
            peer.LastControl = peer.LastControlSequence = peer.LastSeatSequence = 0;
            peer.Input = SpacecraftControls.Parked with { DoorOpen = flight.DoorOpen }; _seats[seat] = peer;
            evidence.FootActive = evidence.EndpointActive = evidence.DepartureActive = false;
            RecordMovementEvidence(true);
            return seat == 0 ? "Pilot seat reserved. Preparing your cockpit." : "Passenger seat reserved. Preparing your seat.";
        }
        if (action == ActionCode.Recover)
        {
            if (peer.Session != null) return "Only the hosting player can recover an empty spacecraft.";
            if (_seats.Any(value => value != null) || _crew.Count != 0) return "All expedition crew must return and leave the ship before recovery.";
            if (HasUnseatedCabinPlayer(true)) return "Everyone must walk out of the cabin before recovering the spacecraft.";
            if (flight.Region == SpacecraftRegion.Island && !Stationary(flight)) return "Land the empty spacecraft before recovery.";
            try { _savedShip = CaptureShip(); _savedShip!.Navigation = null; _savedShip.Docked = false; }
            catch (InvalidOperationException error)
            {
                Console.WriteLine("Spacecraft recovery could not retain its systems: " + error);
                return "Recovery stopped because the ship's condition could not be retained.";
            }
            RemoveBody(); _shipId = Guid.Empty;
            return "Spacecraft recovered. Its reserves and condition will be retained when redeployed.";
        }
        return "Unknown spaceflight request.";
    }

    private string BeginTravel(Peer requester, ActionCode action)
    {
        if (_body == null || requester.Seat is < 0 or > 1 || !ReferenceEquals(_seats[requester.Seat], requester)
            || !requester.Attached || requester.Walking || requester.Lease == Guid.Empty)
            return "Only a seated flight-console operator can start a journey.";
        var pilot = _seats[0];
        if (pilot == null || !pilot.Attached || pilot.Walking) return "The pilot must return to the flight chair.";
        if (_travel != SpacecraftTravel.None) return "A journey is already counting down.";
        if (_body.State.Docked) return "Release the docking latch before starting a journey.";
        if (!_body.State.Powered || Now - pilot.LastControl >= 800)
            return "Switch on spacecraft power and wait for the pilot controls before beginning a journey.";
        var region = _body.State.Region;
        if (action == ActionCode.Launch && region == SpacecraftRegion.Space) return "The spacecraft is already in space.";
        if (action is ActionCode.Travel or ActionCode.Moon && region != SpacecraftRegion.Space) return "Ascend into space before selecting a destination.";
        if (action == ActionCode.Return && region == SpacecraftRegion.Island) return "The spacecraft is already on the island.";
        if (!CrewReady(out var reason)) return "Departure refused: " + reason;
        if (action != ActionCode.Launch && !_systems.CanWarp(out reason)) return reason;
        foreach (var peer in _seats) if (peer != null) _crew.Add(peer);
        _travel = action == ActionCode.Launch ? SpacecraftTravel.Launch
            : action == ActionCode.Travel ? SpacecraftTravel.Destination
            : action == ActionCode.Moon ? SpacecraftTravel.Moon : SpacecraftTravel.Return;
        _stationAssist = false;
        _travelStarted = Now; _body.SetTravel(_travel, 0);
        return "All crew ready. Journey begins in 8 seconds.";
    }

    private bool CrewReady(out string reason)
    {
        reason = _cabin.Status;
        if (!_cabin.Healthy) return false;
        reason = "the spacecraft is unavailable.";
        if (_body == null) return false;
        reason = _body.State.DoorOpen ? "close the rear ramp before departure." : "wait for the rear ramp to finish closing.";
        if (!_body.DoorClosed) return false;
        reason = "everyone in the cabin must sit before departure.";
        if (HasUnseatedCabinPlayer(true)) return false;
        reason = "the destination collision world is not ready.";
        if (!_destination.Ready) return false;
        foreach (var peer in _crew)
            if (peer.Seat < 0) { reason = "every expedition member must reboard before departure."; return false; }
        foreach (var peer in _seats)
        {
            if (peer == null) continue;
            if (!PeerCurrent(peer) || !TryActor(peer, out _, out _, out _) || !peer.Attached || peer.Walking
                || !peer.Prepared || Now - peer.ReadyAt > 2500)
            { reason = "all occupants must have a current seat attachment, return anchor and destination world."; return false; }
        }
        reason = ""; return _seats[0] != null;
    }

    private void UpdateTravel(long now)
    {
        if (_travel == SpacecraftTravel.None || _body == null) return;
        var progress = Mathf.Clamp01((now - _travelStarted) / 8000f);
        var ready = CrewReady(out var reason);
        if (ready && (_seats[0] is not { } pilot || !_body.State.Powered || now - pilot.LastControl >= 800))
        { ready = false; reason = "the pilot must keep power on and controls connected during the countdown."; }
        if (ready && _travel != SpacecraftTravel.Launch)
            ready = _systems.CanContinueWarp(out reason, completing: progress >= 1);
        if (!ready)
        {
            _travel = SpacecraftTravel.None; _body.SetTravel(_travel, 0);
            _feedback = "Departure cancelled: " + reason;
            foreach (var peer in _seats) if (peer != null) peer.Result = _feedback;
            return;
        }
        _body.SetTravel(_travel, progress);
        if (progress < 1) return;
        var region = _travel == SpacecraftTravel.Launch ? SpacecraftRegion.Space
            : _travel == SpacecraftTravel.Destination ? SpacecraftRegion.TestWorld
            : _travel == SpacecraftTravel.Moon ? SpacecraftRegion.Moon : SpacecraftRegion.Island;
        var position = region == SpacecraftRegion.Space ? SpacecraftLayout.SpaceOrigin
            : region == SpacecraftRegion.TestWorld ? _destination.LandingPosition + Vector3.up * 60
            : region == SpacecraftRegion.Moon ? SpacecraftLayout.MoonOrigin + Vector3.up * (60 - SpacecraftLayout.GroundY)
            : _islandPosition + Vector3.up * 30;
        var rotation = region == SpacecraftRegion.Island ? _islandRotation : Quaternion.identity;
        var originRegion = _body.State.Region;
        RecordMovementEvidence(true);
        _body.Transition(region, position, rotation);
        BeginFlightFrame(originRegion);
        RecordMovementEvidence(true);
        _travel = SpacecraftTravel.None; _body.SetTravel(_travel, 0);
        foreach (var peer in _seats)
            if (peer != null) { peer.DistanceGrace = now + 10000; peer.Input = SpacecraftControls.Parked; peer.LastControl = 0; }
        _feedback = region == SpacecraftRegion.Space ? "Space reached. Select Travel to approach the unknown world."
            : region == SpacecraftRegion.TestWorld ? "Unknown world reached. Descend to the marked landing clearing."
            : region == SpacecraftRegion.Moon ? "Moon approach reached. Keep the cabin sealed; the surface has no breathable atmosphere."
            : "Island approach reached. Descend to your original deployment clearing.";
        foreach (var peer in _seats) if (peer != null) peer.Result = _feedback;
    }

    private void ConstrainFlight()
    {
        if (_body == null) return;
        var state = _body.State;
        if (state.Region == SpacecraftRegion.Island) return;
        if (state.Region == SpacecraftRegion.Space) { RebaseSpace(); return; }
        var center = SpacecraftLayout.SurfaceOrigin(state.Region);
        var offset = state.Position - center;
        var horizontal = new Vector2(offset.x, offset.z);
        if (horizontal.sqrMagnitude <= 190 * 190 && offset.y >= -25 && offset.y <= 300) return;
        horizontal = Vector2.ClampMagnitude(horizontal, 185);
        offset = offset.y < -25 ? new Vector3(0, 60 - SpacecraftLayout.GroundY, 0)
            : new Vector3(horizontal.x, MathF.Min(offset.y, 290), horizontal.y);
        RecordMovementEvidence(true);
        _body.Transition(state.Region, center + offset, state.Rotation);
        RecordMovementEvidence(true);
        foreach (var peer in _seats) if (peer != null) peer.DistanceGrace = Now + 10000;
    }

    private void ReleaseSeat(Peer peer, Vector3? exit, bool returnHome)
    {
        ClearConsolePose(peer);
        ReleaseFirefighting(peer);
        ReleaseEva(peer);
        if (returnHome) PrepareReturnMovement(peer);
        else if (exit.HasValue) PrepareExitMovement(peer, exit.Value);
        if (peer.Seat >= 0 && peer.Seat < 3 && ReferenceEquals(_seats[peer.Seat], peer))
        { _seats[peer.Seat] = null; _actorIds[peer.Seat] = 0; _walking[peer.Seat] = false;
            ClearStationInteraction(peer.Seat); }
        peer.Seat = -1; peer.Lease = Guid.Empty; peer.Exit = exit;
        peer.Walking = false; peer.CabinStandLocal = null;
        peer.Input = SpacecraftControls.Parked; peer.LastControl = 0; peer.Attached = false;
        peer.AttachmentSuspended = false;
        peer.Recovery = null;
        peer.ControlFeedback = "";
        peer.ForceReturn |= returnHome;
        if (returnHome) peer.Prepared = false;
    }

    private void PublishView()
    {
        RefreshStationContact();
        var distance = _body != null && LocalAlive() ? Vector3.Distance(LocalPlayer.Transform.position,
            _body.Root.transform.TransformPoint(SpacecraftLayout.Entry)) : 0;
        var saveWarning = _expedition.SaveWarning;
        var status = _ready && ServiceRunning && saveWarning.Length != 0 ? saveWarning : _status;
        View = new(_ready && ServiceRunning, _body?.Ready == true, status, _feedback,
            IsClient ? _clientSeat : _local?.Seat ?? -1,
            IsClient ? _actorIds.Count(id => id != 0) : _seats.Count(peer => peer != null), distance,
            _body != null ? FlightTelemetry(_body.State) : default, IsClient ? _actorIds[0] != 0 : _seats[0] != null,
            _expedition.Active, _destination.SurveyCount, _assetsLoading,
            BoardingPending,
            _expedition.Active || !IsClient && _body != null && _crew.Count == 0 && _seats.All(peer => peer == null)
                && (_body.State.Region != SpacecraftRegion.Island || Stationary(_body.State)) && !HasUnseatedCabinPlayer(),
            !UsingSpacecraft && !BoardingPending && CanReachChair(0) && (IsClient ? _actorIds[0] == 0 : _seats[0] == null),
            !UsingSpacecraft && !BoardingPending && (CanReachChair(1) && (IsClient ? _actorIds[1] == 0 : _seats[1] == null)
                || CanReachChair(2) && (IsClient ? _actorIds[2] == 0 : _seats[2] == null)),
            _rider?.Walking == true, !IsClient,
            LocalCrewAccess,
            CrewRequestLabel(), IsClient ? "" : _crewRequestKey, _ready ? _orbitView : null,
            _ready ? _stationView : null);
        if (_rider != null && _body?.Ready == true)
            _cockpit ??= new ClientSpacecraftCockpit(_rider, command => Command(command));
    }

    private sealed class Peer
    {
        internal ServerSession? Session;
        internal PlayerHandle? Binding;
        internal ulong Owner;
        internal uint Connection;
        internal IntPtr Pointer, ActorPointer;
        internal Guid Nonce, Token = Guid.NewGuid(), Lease;
        internal Guid PendingNonce, PendingToken;
        internal long HelloSequence, PendingHelloSequence, PendingHelloUntil;
        internal int Seat = -1;
        internal bool Prepared, Attached, ForceReturn, EntryPrepared, Walking;
        internal bool CrewApproved;
        internal long CrewRequestedAt;
        internal Vector3? Exit;
        internal Vector3? CabinStandLocal;
        internal bool AttachmentSuspended;
        internal AttachmentRecovery? Recovery;
        internal long NextRecovery;
        internal long LastCommand, LastControlSequence, LastSeatSequence, LastControl, BoardedAt, AttachedAt, LastSeen, ReadyAt, DistanceGrace;
        internal long NextBindingRetry, NextOrbitSnapshot, FrameRevision;
        internal SpacecraftControls Input = SpacecraftControls.Parked;
        internal SpacecraftConsolePose ConsolePose, ConsoleReplay;
        internal long LastConsolePose, ConsoleReplayUntil;
        internal string Result = "Ready.", ControlFeedback = "";
        internal readonly Rate Commands = new(), Controls = new(), Packets = new();
        internal EngineeringInput Engineering = new();
    }
    private sealed class Rate
    {
        private double _tokens;
        private long _last;
        internal bool Take(long now, double rate, double capacity)
        {
            _tokens = _last == 0 ? capacity : Math.Min(capacity, _tokens + Math.Max(0, now - _last) * rate / 1000);
            _last = now;
            if (_tokens < 1) return false;
            _tokens--; return true;
        }
    }
    private enum ActionCode : byte
    {
        Deploy, Pilot, Passenger, Exit, Launch, Travel, Return, Recover, Cancel, PassengerRear, Prepare, OpenRamp,
        Stand, Power, Dampeners, Door, Gear, Moon, InnerDoor, Override, Scrubber, Generator, Vent, ServiceSupplies,
        SelectIsland, SelectWorld, SelectMoon, EngageRoute,
        CabinDoor, CardReader, RetinaScanner, CrewLock, ApproveCrew, DenyCrew,
        Valve0Open = 34, Valve0Close = 35, Valve1Open = 36, Valve1Close = 37,
        Valve2Open = 38, Valve2Close = 39, Valve3Open = 40, Valve3Close = 41,
        Valve4Open = 42, Valve4Close = 43, Valve5Open = 44, Valve5Close = 45,
        Valve6Open = 46, Valve6Close = 47, Valve7Open = 48, Valve7Close = 49,
        Valve8Open = 50, Valve8Close = 51, Valve9Open = 52, Valve9Close = 53,
        Valve10Open = 54, Valve10Close = 55, Valve11Open = 56, Valve11Close = 57,
        ApuIsolate = 58, ApuReset = 59,
        AirlockToCabin = 60, AirlockToEntry = 61, AirlockCancel = 62,
        Circuit0Open = 63, Circuit0Close = 64, Circuit1Open = 65, Circuit1Close = 66,
        Circuit2Open = 67, Circuit2Close = 68, Circuit3Open = 69, Circuit3Close = 70,
        Circuit4Open = 71, Circuit4Close = 72, Circuit5Open = 73, Circuit5Close = 74,
        Circuit0Repair = 75, Circuit1Repair = 76, Circuit2Repair = 77,
        Circuit3Repair = 78, Circuit4Repair = 79, Circuit5Repair = 80,
        Line0Repair = 81, Line1Repair = 82, Line2Repair = 83, Line3Repair = 84,
        Line4Repair = 85, Line5Repair = 86, Line6Repair = 87, Line7Repair = 88,
        Line8Repair = 89, Line9Repair = 90, Line10Repair = 91, Line11Repair = 92,
        StationDock = 93, StationAssist = 94,
        StationDoor = 95,
        StationEquipment = 96, StationLighting = 97, StationEnvironment = 98,
        StationPlumbing = 99,
        EvaSuit0 = 105, EvaService0 = 106, EvaSuit1 = 107, EvaService1 = 108, EvaSuit2 = 109, EvaService2 = 110,
        ApuGuardOpen = 111, ApuGuardClose = 112,
        StationFilterSpecies = 113, StationFilterRate = 114,
        StationMixerRatio = 115, StationMixerPressure = 116, StationAirMode = 117,
        StationThermalMinimum = 118, StationThermalLower = 119, StationThermalHigher = 120,
        StationThermalMaximum = 121, StationThermalRoom = 122,
        StationRoomThermal = 123, StationFireAlarm = 124, StationFirefighting = 125, StationFoam = 126, StationRepair = 127,
        StationMaterials = 128, StationEngineering = 129, StationSmes = 130, StationCable = 131, StationFloor = 132,
        ShoreAir = 133, StationShoreAir = 134, StationShoreFuel = 135, StationShoreCoolant = 136,
        ServiceEngine = 137, SealCabin = 138, SealAirlock = 139, SealEntry = 140,
        BusIsolate = 141, BusConnect = 142, ServicePower = 143, ServiceLife = 144, ServiceHull = 145,
        ServiceWarp = 146, WarpDischargeStart = 147, WarpDischargeStop = 148,
        ServiceHullAirlock = 149, ServiceHullEntry = 150, StationInjectorRate = 151, StationInjectorPulse = 152,
        CancelBoarding = 153
    }
    private static bool IsBoarding(ActionCode action) => action is ActionCode.Pilot or ActionCode.Passenger or ActionCode.PassengerRear;
    private static int SeatFor(ActionCode action) => action == ActionCode.Pilot ? 0 : action == ActionCode.Passenger ? 1 : 2;
    private sealed class Pending(ActionCode action, long sequence, int stationTarget = -1, ushort stationSetting = 0,
        Vector3 stationAim = default, Guid stationLease = default)
    {
        internal readonly ActionCode Action = action;
        internal readonly long Sequence = sequence;
        internal readonly int StationTarget = stationTarget;
        internal readonly ushort StationSetting = stationSetting;
        internal readonly Vector3 StationAim = stationAim;
        internal readonly Guid StationLease = stationLease;
        internal int Attempts = 1;
        internal long Next = Now + 500;
    }
}
