// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientTerminal
{
    private float _nextSpacecraftDisplay;

    private void UpdateSpacecraftDisplay()
    {
        if (_failed || !_open || _app != ClientTerminalApp.Spaceflight || _contentObscured
            || !CanUseDesktop() || Time.unscaledTime < _nextSpacecraftDisplay) return;
        _nextSpacecraftDisplay = Time.unscaledTime + .1f;
        _view?.SetSpacecraft(Plugin.Spacecraft.View);
    }

    private void InvokeSpacecraft(string commandId)
    {
        if (_failed || !_open || _app != ClientTerminalApp.Spaceflight || _contentObscured
            || !CanUseDesktop() || _view?.SpaceflightVisible != true) return;
        if (commandId is not ("deploy" or "board-pilot" or "board-passenger" or "exit"
            or "launch" or "travel" or "return" or "recover" or "survey")) return;
        Plugin.Spacecraft.Command(commandId);
        _view.SetSpacecraft(Plugin.Spacecraft.View);
        _nextRefresh = 0;
    }
}
