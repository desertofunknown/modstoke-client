// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftService
{
    private readonly SpacecraftConsolePose[] _consolePoses = new SpacecraftConsolePose[2];

    private static bool ConsoleControlAllowed(int seat, SpacecraftConsoleControl control)
        => control == SpacecraftConsoleControl.None || seat == 0 && SpacecraftSeatConsole.Index(control) >= 0
            || seat == 1 && control is SpacecraftConsoleControl.Power or SpacecraftConsoleControl.Dampeners
                or SpacecraftConsoleControl.Door or SpacecraftConsoleControl.Gear;

    private static void ReceiveConsolePose(Peer peer, SpacecraftControls input, ulong actor)
    {
        peer.ConsolePose = input.ConsoleReach > 0 && input.ConsoleControl != SpacecraftConsoleControl.None
            && ConsoleControlAllowed(peer.Seat, input.ConsoleControl)
            ? new(actor, input.ConsoleControl, input.ConsoleReach) : default;
        peer.LastConsolePose = Now;
        if (peer.ConsolePose.Active) peer.ConsoleReplay = default;
    }

    private SpacecraftConsolePose CurrentConsolePose(int seat)
    {
        var actor = _actorIds[seat];
        if (_body == null || _walking[seat] || BoltNetwork.isRunning && actor == 0) return default;
        if (IsClient)
        {
            var pose = _consolePoses[seat];
            return Now - _lastSnapshot <= 500 && pose.Actor == actor ? pose : default;
        }
        var peer = _seats[seat];
        if (peer == null) return default;
        if (peer.Seat != seat || peer.Walking || !peer.Attached || !peer.Prepared
            || peer.Lease == Guid.Empty || !HasCrewAccess(peer))
        { ClearConsolePose(peer); return default; }
        if (Now - peer.LastConsolePose <= 500 && peer.ConsolePose.Active && peer.ConsolePose.Actor == actor)
            return peer.ConsolePose;
        if (Now < peer.ConsoleReplayUntil && peer.ConsoleReplay.Active && peer.ConsoleReplay.Actor == actor)
            return peer.ConsoleReplay with { Reach = Mathf.Clamp01((peer.ConsoleReplayUntil - Now) / 300f) };
        return default;
    }

    private void ReplayConsolePose(Peer peer, SpacecraftConsoleControl control)
    {
        if (control == SpacecraftConsoleControl.None || peer.Seat is < 0 or > 1 || peer.Walking
            || !peer.Attached || !peer.Prepared || peer.Lease == Guid.Empty
            || !ReferenceEquals(_seats[peer.Seat], peer) || !ConsoleControlAllowed(peer.Seat, control)
            || !TryActor(peer, out _, out _, out var actor)) return;
        // Pointer gestures already carry their approach; keyboard commands need a local-seat replay.
        if (Now - peer.LastConsolePose <= 500 && peer.ConsolePose.Active && peer.ConsolePose.Actor == actor) return;
        peer.ConsoleReplay = new(actor, control, 1);
        peer.ConsoleReplayUntil = Now + 650;
    }

    private void ClearConsolePose(Peer peer)
    {
        peer.ConsolePose = peer.ConsoleReplay = default;
        peer.LastConsolePose = peer.ConsoleReplayUntil = 0;
        if (peer.Seat is >= 0 and <= 1) _consolePoses[peer.Seat] = default;
    }
}
