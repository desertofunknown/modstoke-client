// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftService
{
    private bool _shoreServiceInput;
    private ShoreServiceKind _shoreServiceKind;
    private int _shoreServicePressed = -1;
    private IntPtr _shoreServiceActor;
    private Guid _shoreServiceLease, _shoreServiceShip;
    private ClientSpacecraftBody? _shoreServicePressBody;
    private SpacecraftControlContact _shoreServiceContact;
    private string _shoreServicePrompt = "", _shoreServiceFeedback = "";
    private float _shoreServiceFeedbackUntil;
    private long _shoreServiceRequest;

    private static bool ValidStationShoreAir(ushort setting) => setting <= 1;

    private static bool TryStationShoreAir(string command, out ushort setting)
    {
        setting = command == "station-shore-air-start" ? (ushort)1 : (ushort)0;
        return command is "station-shore-air-start" or "station-shore-air-stop";
    }

    private static bool ValidStationShoreFuel(ushort setting) => setting <= 1;

    private static bool TryStationShoreFuel(string command, out ushort setting)
    {
        setting = command == "station-shore-fuel-start" ? (ushort)1 : (ushort)0;
        return command is "station-shore-fuel-start" or "station-shore-fuel-stop";
    }

    private static bool ValidStationShoreCoolant(ushort setting) => setting <= 1;

    private static bool TryStationShoreCoolant(string command, out ushort setting)
    {
        setting = command == "station-shore-coolant-start" ? (ushort)1 : (ushort)0;
        return command is "station-shore-coolant-start" or "station-shore-coolant-stop";
    }

    private static string ShoreServiceCommand(ShoreServiceKind service, bool start) => service switch
    {
        ShoreServiceKind.Air => start ? "shore-air-start" : "shore-air-stop",
        ShoreServiceKind.Fuel => start ? "shore-fuel-start" : "shore-fuel-stop",
        ShoreServiceKind.Coolant => start ? "shore-coolant-start" : "shore-coolant-stop",
        _ => throw new ArgumentOutOfRangeException(nameof(service))
    };

    private string ExecuteStationShoreService(Peer peer, ushort setting, Vector3 actorPosition, ShoreServiceKind service)
    {
        if (setting > 1) return "Invalid shore service control.";
        string failure = StationControlAccess(peer, actorPosition);
        if (failure.Length != 0) return failure;
        bool start = setting == 1;
        var station = _orbitalStation!.Root.transform;
        var target = SpacecraftShorePower.ControlPoint(station, start, service);
        failure = ReachStationControl(peer, actorPosition, target, animate: false);
        if (failure.Length != 0) return failure;
        UpdateShoreConnection();
        var command = ShoreServiceCommand(service, start);
        if (!_systems.TryCommand(command, _body!.State, out var message,
            _body.InnerDoorClosed, _body.CabinDoorClosed)) return message;
        _body.SetSystemsState(SystemsState(_systems.State));
        if (TryActor(peer, out _, out _, out var actor))
        {
            RecordStationInteraction(peer, actor, target);
            var contact = SpacecraftShorePower.ControlContact(station, _body.Root.transform, start, service);
            _interactions[peer.Seat] = _interactions[peer.Seat] with { LocalRotation = contact.Rotation, Press = true };
        }
        _nextSnapshot = 0;
        return message;
    }

    private bool UpdateShoreServiceControls(bool interactive)
    {
        if (_shoreServiceRequest != 0 && _pending?.Sequence != _shoreServiceRequest)
        {
            if (_commandSequence == _shoreServiceRequest) ShoreServiceFeedback(_feedback);
            _shoreServiceRequest = 0;
        }
        var camera = LocalPlayer.MainCam;
        if (!interactive || _body == null || _shorePower == null || _rider == null
            || _orbitalStation?.Visible != true || !ClientSpacecraftRider.WorldInputAvailable || Cursor.visible
            || !camera || !camera.isActiveAndEnabled || !_shorePower.FindControl(camera, out bool start, out var service))
        { CancelShoreServicePress(); return false; }
        _shoreServiceInput = true;
        _shoreServicePrompt = Time.unscaledTime < _shoreServiceFeedbackUntil ? _shoreServiceFeedback
            : (start ? "START " : "STOP ") + (service switch
                { ShoreServiceKind.Air => "AIR", ShoreServiceKind.Fuel => "FUEL", _ => "COOLANT" }) + " SERVICE  ·  E";
        var lease = IsClient ? _clientLease : _local?.Lease ?? Guid.Empty;
        var actor = LocalPlayer.Transform;
        var contact = SpacecraftShorePower.ControlContact(_orbitalStation.Root.transform, _body.Root.transform, start, service);
        if (_shoreServicePressed >= 0)
        {
            if (_shoreServicePressed != (start ? 1 : 0) || _shoreServiceKind != service || !actor || actor.Pointer != _shoreServiceActor
                || lease == Guid.Empty || lease != _shoreServiceLease || _shipId != _shoreServiceShip
                || !ReferenceEquals(_body, _shoreServicePressBody)
                || (contact.Position - _shoreServiceContact.Position).sqrMagnitude > .000025f
                || Quaternion.Angle(contact.Rotation, _shoreServiceContact.Rotation) > 1)
            { CancelShoreServicePress(); return true; }
            if (!_rider.ControlHandActive)
            {
                ShoreServiceFeedback(_rider.ControlPressFailure);
                CancelShoreServicePress();
                return true;
            }
            if (!_rider.ControlPressReached(_body)) return true;
            _shoreServicePressed = -1;
            _shoreServicePressBody = null;
            _rider.EndControlPress(_body);
            ShoreServiceFeedback(Command("station-" + ShoreServiceCommand(service, start)));
            _shoreServiceRequest = _pending is { Action: ActionCode.StationShoreAir or ActionCode.StationShoreFuel
                or ActionCode.StationShoreCoolant } request ? request.Sequence : 0;
            return true;
        }
        if (!Input.GetKeyDown(KeyCode.E) || _pending != null || !actor || lease == Guid.Empty) return true;
        if (!_rider.BeginControlPress(_body, contact, out var reason))
        { ShoreServiceFeedback(reason); return true; }
        _shoreServicePressed = start ? 1 : 0;
        _shoreServiceKind = service;
        _shoreServiceActor = actor.Pointer;
        _shoreServiceLease = lease;
        _shoreServiceShip = _shipId;
        _shoreServicePressBody = _body;
        _shoreServiceContact = contact;
        _shoreServiceFeedbackUntil = 0;
        return true;
    }

    private void ShoreServiceFeedback(string message)
    {
        _shoreServiceFeedback = _shoreServicePrompt = message;
        _shoreServiceFeedbackUntil = Time.unscaledTime + 3;
    }

    private void CancelShoreServicePress()
    {
        if (_shoreServicePressBody is { } body) _rider?.EndControlPress(body, immediate: true);
        _shoreServicePressBody = null;
        _shoreServicePressed = -1;
        _shoreServiceActor = IntPtr.Zero;
        _shoreServiceLease = _shoreServiceShip = Guid.Empty;
    }
}
