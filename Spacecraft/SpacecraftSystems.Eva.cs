// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftSystems
{
    // The host caller validates that this suit is docked at its physical rack.
    internal bool TryServiceEva(SpacecraftEvaSuit suit, out string message)
    {
        if (!Powered(SpacecraftCircuit.LifeSupport) || _lifeSupport <= .1f || !_scrubber
            || !ConnectionUsable(SpacecraftValve.AirlockSupply)
            || !ConnectionUsable(SpacecraftValve.AirlockScrubber))
        {
            message = "EVA service requires powered life support and intact, open airlock supply and scrubber lines.";
            return false;
        }
        if (!suit.TryService(ref _reserve, ref _battery, out message)) return false;
        RefreshElectrical(_flight);
        return true;
    }
}
