// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftService
{
    private enum ServiceOperation { Update, Physics, Presentation, Command, Network }
    private bool _worldAvailable, _serviceHeld, _holdPrepared;
    private long _heldAt, _retryServiceAt, _nextFaultLog, _nextHoldLog;
    private int _faultRepeats;
    private ServiceOperation? _faultOperation;
    private string _faultSignature = "";
    private bool ServiceRunning => _worldAvailable && !_serviceHeld && !_restoreNavigation && _cabin.Healthy;

    private void HoldService()
    {
        var first = !_serviceHeld;
        if (first) { _serviceHeld = true; _holdPrepared = false; _heldAt = Now; }
        // Kinematic hold precedes other cleanup: Unity otherwise integrates the
        // last velocity even though this service has stopped submitting steps.
        _body?.SetServiceSuspended(true);
        _rider?.SetSuspended(true);
        if (_holdPrepared) return;
        if (!IsClient)
        {
            foreach (var peer in _seats) if (peer != null) SuspendAttachment(peer);
            _systems.FinishPendingSteps();
        }
        _audio?.Suspend();
        _effects?.Suspend();
        ShowInteraction("");
        _holdPrepared = true;
    }

    private void ResumeService()
    {
        if (!_serviceHeld) return;
        HoldService();
        if (!IsClient) _systems.FinishPendingSteps();
        _body?.SetServiceSuspended(false);
        // Departure is measured in wall time; none of the held interval belongs
        // to its countdown. Peer ACKs and pilot input still expire normally.
        if (_travel != SpacecraftTravel.None) _travelStarted += Math.Max(0, Now - _heldAt);
        _serviceHeld = _holdPrepared = false;
        _heldAt = _retryServiceAt = 0;
        _nextHello = _nextSnapshot = _nextSeatStatus = 0;
        if (_ready) _status = "Ship systems resumed.";
    }

    private void UpdateHeldPlayer()
    {
        if (_rider?.Attached == true) _rider.Update();
        var now = Now;
        if (_worldAvailable && IsClient && _ready && now >= _nextSeatStatus)
        {
            var server = BoltNetwork.server;
            if (server != null && !server.WasCollected && _serverPointer != IntPtr.Zero
                && server.Pointer == _serverPointer && server.ConnectionId == _serverConnection)
            {
                _nextSeatStatus = now + 500;
                _event?.SeatStatus(++_seatSequence, _clientLease,
                    _rider is { Attached: true, Faulted: false, Incapacitated: false }, false);
            }
        }
        View = View with { Ready = false, Status = _status, Feedback = _feedback,
            CanRecover = false, CanBoardPilot = false, CanBoardPassenger = false, Boarding = false };
    }

    private void OperationCompleted(ServiceOperation operation)
    {
        if (_faultOperation != operation
            && !(operation == ServiceOperation.Update && _faultOperation is ServiceOperation.Command or ServiceOperation.Network)) return;
        _faultOperation = null;
        _faultRepeats = 0;
        _faultSignature = "";
    }

    private void Fail(Exception error, ServiceOperation operation)
    {
        var now = Now;
        var signature = operation + ": " + error.GetType().FullName + ": " + error.Message;
        if (_faultSignature != signature || now >= _nextFaultLog)
        {
            Console.WriteLine("Modstoke spacecraft " + operation + " paused: " + error);
            _nextFaultLog = now + 30000;
        }
        _faultRepeats = _faultSignature == signature ? Math.Min(_faultRepeats + 1, 4) : 0;
        _faultSignature = signature;
        _faultOperation = operation;
        _retryServiceAt = now + (500L << _faultRepeats);
        _status = "Ship systems paused while recovering from an error.";
        _feedback = "Your ship and expedition have been retained.";
        try { HoldService(); }
        catch (Exception holdError)
        {
            // Do not trade a failed hold for a destructive service shutdown.
            // The same body retains its capture so the next update can retry.
            if (now >= _nextHoldLog)
            {
                Console.WriteLine("Spacecraft position hold is pending: " + holdError);
                _nextHoldLog = now + 30000;
            }
        }
        View = View with { Ready = false, Status = _status, Feedback = _feedback,
            CanRecover = false, CanBoardPilot = false, CanBoardPassenger = false, Boarding = false };
    }
}
