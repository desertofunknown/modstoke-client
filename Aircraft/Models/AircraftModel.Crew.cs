// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientAircraftModel
{
    private static readonly string[] Yokes = { "StickPort", "StickStarboard" };
    private static readonly string[] Pedals =
        { "PedalPortLeft", "PedalPortRight", "PedalStarboardLeft", "PedalStarboardRight" };
    private static readonly string[] Harnesses = { "SeatHarness0", "SeatHarness1", "SeatHarness2" };
    private static readonly string[] IdleHarnesses = { "SeatHarnessIdle0", "SeatHarnessIdle1", "SeatHarnessIdle2" };
    private uint _crewSequence;
    private AircraftInteraction _crewInteraction;
    private float _crewAge = 2;
    private Vector3 _crewRight;
    private Vector3 _crewReachStart;
    private bool _crewReady;
    private bool _crewReaching;
    private Quaternion? _crewReachRotation;

    private void UpdateCrewControls(AircraftFlightState state, float dt)
    {
        var initialize = !_crewReady;
        for (var seat = 0; seat < 2; seat++)
        {
            var x = AircraftLayout.Seat(seat).x;
            if (_parts.TryGetValue(Yokes[seat], out var yoke))
            {
                yoke.localPosition = new Vector3(x, .64f, 1.15f - .055f * state.PitchControl);
                yoke.localRotation = Quaternion.Euler(0, 0, -55 * state.RollControl);
            }
            for (var foot = 0; foot < 2; foot++)
            {
                var side = foot == 0 ? -1f : 1f;
                if (!_parts.TryGetValue(Pedals[seat * 2 + foot], out var pedal)) continue;
                pedal.localPosition = new Vector3(x + side * .105f, .16f, 1.20f + side * .04f * state.YawControl);
                pedal.localRotation = Quaternion.Euler(state.Brake ? 14 : 0, 0, 0);
            }
        }
        Rotate("ThrottleLever", Mathf.Lerp(-18, 24, state.Throttle), 0, 0);
        var rest = LocalContact("StickPort", new Vector3(.13f, .02f, -.21f));
        if (!_crewReady) { _crewRight = rest; _crewReady = true; }
        if (state.InteractionSequence != _crewSequence || state.Interaction != _crewInteraction)
        {
            _crewSequence = state.InteractionSequence;
            _crewInteraction = state.Interaction;
            _crewAge = state.InteractionAge;
            _crewReachStart = _crewRight;
        }
        else _crewAge = Mathf.Max(state.InteractionAge, _crewAge + dt);
        _crewAge = Mathf.Min(2, _crewAge);
        // Only the visible toggle waits for the reaching hand. The host applies
        // the actual control immediately; boarding never delays flight input.
        Toggle("SwitchEngine", state.Engine ? -22 : 22, AircraftInteraction.Engine, dt, initialize);
        Toggle("SwitchLights", state.Lights ? -22 : 22, AircraftInteraction.Lights, dt, initialize);
        Toggle("SwitchFlaps", 22 - state.Flaps * 22, AircraftInteraction.Flaps, dt, initialize);
        Toggle("SwitchBrake", state.Brake ? -22 : 22, AircraftInteraction.Brake, dt, initialize);
        Toggle("SwitchStarter", _crewInteraction == AircraftInteraction.Starter && _crewAge < .6f ? -22 : 22,
            AircraftInteraction.Starter, dt, initialize);
        _crewReaching = _crewInteraction != AircraftInteraction.None && _crewAge < 1.1f;
        if (!_crewReaching) { _crewRight = rest; return; }
        var part = _crewInteraction switch
        {
            AircraftInteraction.Engine => "SwitchEngine", AircraftInteraction.Lights => "SwitchLights",
            AircraftInteraction.Flaps => "SwitchFlaps", AircraftInteraction.Brake => "SwitchBrake",
            AircraftInteraction.Starter => "SwitchStarter", _ => "ThrottleLever"
        };
        var contact = LocalContact(part, part == "ThrottleLever"
            ? new Vector3(0, .10f, .01f) : new Vector3(0, .014f, -.020f));
        // Follow the toggle's visible pitch with the palm side at the contact.
        // Retain the last frame during the return to the yoke to avoid a wrist snap.
        _crewReachRotation = part != "ThrottleLever" && _parts.TryGetValue(part, out var toggle)
            ? Quaternion.Inverse(_root!.transform.rotation) * toggle.rotation : null;
        _crewRight = _crewAge < .32f
            ? Vector3.Lerp(_crewReachStart, contact, Mathf.SmoothStep(0, 1, _crewAge / .32f))
            : Vector3.Lerp(contact, rest, Mathf.SmoothStep(0, 1, (_crewAge - .62f) / .48f));
    }

    private void Toggle(string name, float angle, AircraftInteraction gesture, float dt, bool initialize)
    {
        if (!_parts.TryGetValue(name, out var part)) return;
        if (!initialize && _crewInteraction == gesture && _crewAge < .28f) return;
        part.localRotation = Quaternion.Euler(initialize ? angle
            : Mathf.MoveTowardsAngle(part.localEulerAngles.x, angle, 440 * dt), 0, 0);
    }

    private Vector3 LocalContact(string partName, Vector3 offset) =>
        _parts.TryGetValue(partName, out var part)
            ? _root!.transform.InverseTransformPoint(part.TransformPoint(offset)) : Vector3.zero;

    internal bool TryCrewTargets(int seat, out AircraftCrewTargets targets)
    {
        targets = default;
        if (_disposed || _root == null || !_crewReady || seat is < 0 or > 2) return false;
        var frame = _root.transform;
        var center = AircraftLayout.Seat(seat);
        var leftHand = seat == 0 ? LocalContact("StickPort", new Vector3(-.13f, .02f, -.21f))
            : center + new Vector3(-.17f, .13f, .20f);
        var rightHand = seat == 0 ? _crewRight : center + new Vector3(.17f, .13f, .20f);
        var leftFoot = seat < 2 ? LocalContact(Pedals[seat * 2], new Vector3(0, .10f, -.09f))
            : center + new Vector3(-.12f, -.10f, .48f);
        var rightFoot = seat < 2 ? LocalContact(Pedals[seat * 2 + 1], new Vector3(0, .10f, -.09f))
            : center + new Vector3(.12f, -.10f, .48f);
        targets = new AircraftCrewTargets(frame.TransformPoint(center + Vector3.up * .08f),
            frame.TransformPoint(leftFoot), frame.TransformPoint(rightFoot),
            frame.TransformPoint(leftHand), frame.TransformPoint(rightHand), frame.rotation,
            seat == 0 && _crewReaching)
        {
            RightReachRotation = seat == 0 && _crewReachRotation is { } reachRotation
                ? frame.rotation * reachRotation : null
        };
        return true;
    }

    internal void SetOccupants(byte mask)
    {
        for (var seat = 0; seat < 3; seat++)
        {
            var occupied = (mask & (1 << seat)) != 0;
            SetActive(Harnesses[seat], occupied);
            SetActive(IdleHarnesses[seat], !occupied);
        }
    }
}
