// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Modstoke.Sons.Combat;

namespace Modstoke.Client;

internal enum ClientCombatResourceProfile
{
    None,
    Breathing,
    BreathingWithRebreather
}

internal sealed partial class ClientCombatController
{
    private const ulong ResourceCapabilities = StrictCombatBreathingProtocol.Capability
        | StrictCombatRebreatherProtocol.Capability | StrictCombatRebreatherRefillProtocol.Capability
        | StrictCombatRebreatherWearProtocol.Capability | StrictCombatRebreatherProgressProtocol.Capability;
    private ulong _resourceCapabilities;
    private ulong _breathingLife, _rebreatherLife;
    private long _resourceWaitDeadline;

    // Admission must deliberately select the same profile before preparation.
    // This describes the internal driver; it does not advertise or enable a mode.
    internal static bool TryGetRequiredCapabilities(ClientCombatResourceProfile profile, out ulong required)
    {
        required = 0;
        switch (profile)
        {
            case ClientCombatResourceProfile.None:
                required = RequiredCapabilities;
                return true;
            case ClientCombatResourceProfile.Breathing:
                required = RequiredCapabilities | StrictCombatBreathingProtocol.Capability;
                return true;
            case ClientCombatResourceProfile.BreathingWithRebreather:
                required = RequiredCapabilities | ResourceCapabilities;
                return true;
            default:
                return false;
        }
    }

    private bool PrepareResources(ulong capabilities)
    {
        if ((capabilities & StrictCombatBreathingProtocol.Capability) != 0)
        {
            var lungs = StrictCombatClient.PrepareNativeBreathingBarrierForNextStart();
            if (lungs != StrictCombatBreathingStatus.Success)
            { Status = "Native breathing preparation: " + lungs; return false; }
        }
        if ((capabilities & StrictCombatRebreatherProtocol.Capability) != 0)
        {
            var tank = StrictCombatClient.PrepareNativeRebreatherBarrierForNextStart();
            if (tank != StrictCombatRebreatherStatus.Success)
            { Status = "Native rebreather preparation: " + tank; return false; }
        }
        return true;
    }

    private bool HasProvisionedResources(StrictCombatClient client, in StrictCombatBinding binding,
        in StrictCombatSnapshot state)
    {
        if ((_resourceCapabilities & StrictCombatBreathingProtocol.Capability) != 0
            && (!client.TryReadOwnedBreathing(out var lungBinding, out var lungState, out var lungs)
                || !SameBinding(in binding, in lungBinding) || lungState.Revision != state.Revision
                || lungState.LifeGeneration != state.LifeGeneration || !lungs.IsProvisioned)) return false;
        if ((_resourceCapabilities & StrictCombatRebreatherProtocol.Capability) != 0
            && (!client.TryReadOwnedRebreather(out var tankBinding, out var tankState, out var tank)
                || !SameBinding(in binding, in tankBinding) || tankState.Revision != state.Revision
                || tankState.LifeGeneration != state.LifeGeneration || !tank.IsProvisioned || !tank.HasWearState)) return false;
        return true;
    }

    private bool SynchronizeResources(StrictCombatClient client, in StrictCombatBinding binding,
        in StrictCombatSnapshot state)
    {
        if (_resourceCapabilities == 0) return true;
        if (!_ownsLife || !SameBinding(in binding, in _ownedBinding)
            || state.LifeStage != StrictCombatLifeStage.Alive)
        { Retire("Breathing resources no longer belong to the owned life."); return false; }
        if (!HasProvisionedResources(client, in binding, in state))
            return WaitForResources("Waiting for accepted server breathing resources.");

        if ((_resourceCapabilities & StrictCombatBreathingProtocol.Capability) != 0)
        {
            if (_breathingLife != state.LifeGeneration)
            {
                // A new life can only transfer the retained objects through the SDK's
                // correlated respawn handoff. Never reacquire them as a fresh grant.
                StrictCombatBreathingStatus status;
                bool acquired = _breathingLife == 0
                    ? client.TryTakeOverNativeBreathingBarrier(out status)
                    : client.TryHandoffNativeBreathingBarrier(out status);
                if (!acquired)
                {
                    if (status == StrictCombatBreathingStatus.Busy)
                        return WaitForResources("Waiting for native breathing to finish.");
                    Retire("Native breathing ownership: " + status);
                    return false;
                }
                _breathingLife = state.LifeGeneration;
            }
            var projection = client.ProjectLocalBreathing();
            if (projection == StrictCombatBreathingProjectionStatus.Busy)
                return WaitForResources("Waiting for native breathing projection.");
            if (projection is not (StrictCombatBreathingProjectionStatus.Applied
                or StrictCombatBreathingProjectionStatus.AlreadyCurrent))
            { Retire("Breathing projection: " + projection); return false; }
        }

        if ((_resourceCapabilities & StrictCombatRebreatherProtocol.Capability) != 0)
        {
            if (_rebreatherLife != state.LifeGeneration)
            {
                StrictCombatRebreatherStatus status;
                bool acquired = _rebreatherLife == 0
                    ? client.TryAcquireNativeRebreatherBarrier(out status)
                    : client.TryHandoffNativeRebreatherBarrier(out status);
                if (!acquired)
                { Retire("Native rebreather ownership: " + status); return false; }
                _rebreatherLife = state.LifeGeneration;
            }
            // Accepted wear controls the native breathing mirror, including unworn
            // tanks. This does not equip an item, supply air or run a native refill.
            var projection = client.ProjectLocalRebreatherActivation();
            if (projection == StrictCombatRebreatherProjectionStatus.Busy)
                return WaitForResources("Waiting for native rebreather projection.");
            if (projection is not (StrictCombatRebreatherProjectionStatus.Applied
                or StrictCombatRebreatherProjectionStatus.AlreadyCurrent))
            { Retire("Rebreather projection: " + projection); return false; }
        }
        _resourceWaitDeadline = 0;
        return true;
    }

    private bool WaitForResources(string status)
    {
        long now = Environment.TickCount64;
        if (now < 0 || now > long.MaxValue - InitialOwnershipWaitMs)
        { Retire("Breathing resource synchronization clock is unavailable."); return false; }
        if (_resourceWaitDeadline == 0) _resourceWaitDeadline = now + InitialOwnershipWaitMs;
        if (CheckOwnershipDeadlines()) Status = status;
        return false;
    }
}
