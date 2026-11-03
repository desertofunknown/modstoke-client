// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

namespace Modstoke.Client;

internal sealed partial class ClientTerminal
{
    internal bool ControllerPresentation => _tablet.IsController && TabletPresencePhase != ClientTabletPresencePhase.Hidden;

    internal ClientTabletPresencePhase TabletPresencePhase => !_registered || _failed || !_tablet.PresentationReady
        ? ClientTabletPresencePhase.Hidden
        : _tablet.IsHolstering ? ClientTabletPresencePhase.Holstering
        : _tablet.IsDrawing ? ClientTabletPresencePhase.Drawing
        : _tablet.IsOpen ? ClientTabletPresencePhase.Open
        : ClientTabletPresencePhase.Hidden;

    internal ClientWatchPresencePhase WatchPresencePhase => !_registered || _failed
        ? ClientWatchPresencePhase.Hidden : _wristWatch.PresencePhase;
}
