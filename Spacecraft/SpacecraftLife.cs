// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using Modstoke.Sons;

namespace Modstoke.Client;

internal static class SpacecraftLife
{
    // Callers own and validate the exact native stats instance before reading.
    internal static PlayerLifeState? Read(PlayerStats stats)
    {
        if (stats._respawning || stats._doFinalDeath || stats._dyingState == PlayerStats.DyingState.Dead)
            return PlayerLifeState.Dead;
        return stats._dyingState switch
        {
            PlayerStats.DyingState.Dying => PlayerLifeState.Dying,
            PlayerStats.DyingState.Alive => stats._playerIsDowned ? PlayerLifeState.Dying : PlayerLifeState.Alive,
            _ => null
        };
    }
}
