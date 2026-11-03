// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientTerminal
{
    private float _nextAircraftDisplay;

    private void UpdateAircraftDisplay()
    {
        if (_failed || !_open || _app != ClientTerminalApp.Hangar || _contentObscured
            || !CanUseDesktop() || Time.unscaledTime < _nextAircraftDisplay) return;
        _nextAircraftDisplay = Time.unscaledTime + .1f;
        _view?.SetAircraft(Plugin.Aircraft.View);
    }

    private void InvokeAircraft(string commandId)
    {
        if (_failed || !_open || _app != ClientTerminalApp.Hangar || _contentObscured
            || !CanUseDesktop() || _view?.HangarVisible != true) return;
        if (commandId is not ("deploy" or "board-pilot" or "board-passenger" or "exit" or "service" or "recover")) return;
        Plugin.Aircraft.Command(commandId);
        _view.SetAircraft(Plugin.Aircraft.View);
        _nextRefresh = 0;
    }
}
