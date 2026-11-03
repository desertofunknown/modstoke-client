// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Collections.Generic;
using Modstoke.Sons;

namespace Modstoke.Client;

// Copied condition flags. Unknown observations never become false or a healthy state.
internal sealed record ClientHealthObservation
{
    internal LocalPlayerReadStatus Status { get; init; } = LocalPlayerReadStatus.Unavailable;
    internal PlayerLifeState? LifeState { get; init; }
    internal bool? IsDowned { get; init; }
    internal bool? IsRespawning { get; init; }
    internal bool? IsSeated { get; init; }
    internal bool? IsOnFire { get; init; }
    internal bool? IsCold { get; init; }
    internal bool? IsFreezing { get; init; }

    internal bool Available => Status == LocalPlayerReadStatus.Success;
    internal bool Urgent => Available && (IsOnFire == true || IsFreezing == true || IsDowned == true
        || LifeState is PlayerLifeState.Dying or PlayerLifeState.Dead);
    internal bool NeedsAttention => Urgent || Available && IsCold == true;

    internal static ClientHealthObservation From(LocalPlayerVitalsSnapshot snapshot) => new()
    {
        Status = LocalPlayerReadStatus.Success,
        LifeState = snapshot.LifeState,
        IsDowned = snapshot.IsDowned,
        IsRespawning = snapshot.IsRespawning,
        IsSeated = snapshot.IsSeated,
        IsOnFire = snapshot.IsOnFire,
        IsCold = snapshot.IsCold,
        IsFreezing = snapshot.IsFreezing
    };

    internal string Summary
    {
        get
        {
            if (!Available) return Status switch
            {
                LocalPlayerReadStatus.WorldUnavailable => "Waiting for a world",
                LocalPlayerReadStatus.PlayerUnavailable => "Waiting for player",
                LocalPlayerReadStatus.ManagerRootUnavailable
                    or LocalPlayerReadStatus.StatsUnavailable => "Waiting for condition readings",
                LocalPlayerReadStatus.ContextChanged => "Refreshing condition",
                _ => "Condition unavailable"
            };
            var parts = new List<string>();
            if (IsOnFire == true) parts.Add("On fire");
            if (IsDowned == true) parts.Add("Downed");
            if (IsRespawning == true) parts.Add("Respawning");
            if (LifeState == PlayerLifeState.Dead) parts.Add("Dead");
            else if (LifeState == PlayerLifeState.Dying) parts.Add("Dying");
            if (IsFreezing == true) parts.Add("Freezing");
            if (IsCold == true) parts.Add("Cold");
            if (IsSeated == true) parts.Add("Seated");
            if (LifeState == null) parts.Add("Life state unavailable");
            return parts.Count == 0 ? "Alive" : string.Join(" · ", parts);
        }
    }

    // The wrist shows the most urgent condition; the tablet has room for every observed flag.
    internal string WatchSummary => !Available ? Status switch
    {
        LocalPlayerReadStatus.WorldUnavailable => "WAITING FOR WORLD",
        LocalPlayerReadStatus.PlayerUnavailable => "WAITING FOR PLAYER",
        LocalPlayerReadStatus.ManagerRootUnavailable
            or LocalPlayerReadStatus.StatsUnavailable => "WAITING FOR READINGS",
        LocalPlayerReadStatus.ContextChanged => "UPDATING CONDITION",
        _ => "CONDITION UNAVAILABLE"
    }
        : IsOnFire == true ? "ON FIRE"
        : IsDowned == true ? "DOWNED"
        : IsRespawning == true ? "RESPAWNING"
        : LifeState == PlayerLifeState.Dead ? "DEAD"
        : LifeState == PlayerLifeState.Dying ? "DYING"
        : IsFreezing == true ? "FREEZING"
        : IsCold == true ? "COLD"
        : LifeState == null ? "LIFE STATE UNKNOWN"
        : IsSeated == true ? "SEATED" : "ALIVE";
}
