// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using Modstoke.Sons;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Modstoke.Client;

internal sealed partial class ClientTerminal
{
    private bool _fullscreenPointerRelease;
    private bool Fullscreen => _view?.FullscreenMode is ClientFullscreenMode.Drone or ClientFullscreenMode.Arcade;
    private bool DroneFullscreen => _view?.FullscreenMode == ClientFullscreenMode.Drone;

    // Returns true only when Escape has been used to leave fullscreen this frame.
    private bool SyncFullscreen()
    {
        if (!Fullscreen) return false;
        var state = GameRuntime.State;
        var mode = _view!.FullscreenMode;
        var appMatches = mode == ClientFullscreenMode.Drone ? _app == ClientTerminalApp.Drone : _app == ClientTerminalApp.Games;
        if (!Application.isFocused || !state.IsInWorld || state.PauseMenuOpen || LoaderMenuVisible()
            || !CanUseDesktop() || _contentObscured || !appMatches || !_deviceInput.Captured
            || mode == ClientFullscreenMode.Drone && !_view.CanFullscreenDrone)
        {
            ExitFullscreen();
            return false;
        }
        if (!Input.GetKeyDown(KeyCode.Escape)) return false;
        ExitFullscreen();
        return true;
    }

    private void InvokeFullscreen(string command)
    {
        if (command == "exit") { ExitFullscreen(); return; }
        if (_view == null || !Application.isFocused || !CanUseDesktop() || _contentObscured
            || !_deviceInput.Captured || IsEditingText()) return;
        var mode = command == "drone" && _app == ClientTerminalApp.Drone ? ClientFullscreenMode.Drone
            : command == "arcade" && _app == ClientTerminalApp.Games ? ClientFullscreenMode.Arcade : ClientFullscreenMode.None;
        if (mode == ClientFullscreenMode.None) return;
        if (mode == ClientFullscreenMode.Drone && !_view.CanFullscreenDrone)
        { Feedback("Open a placement preview or establish a live drone camera link first."); return; }
        if (!_view.EnterFullscreen(mode)) return;
        _view.CancelPointer();
        _tablet.UpdateScreenContact(null, false, false, true);
        EventSystem.current?.SetSelectedGameObject(null);
        if (mode == ClientFullscreenMode.Arcade) _arcadeKeyboard = true;
        _nextDroneDisplay = 0;
    }

    private void ExitFullscreen()
    {
        if (!Fullscreen) return;
        _view!.ExitFullscreen();
        Plugin.Drones.SetCameraView(false, _droneCameraPitch, _droneCameraZoom);
        _fullscreenPointerRelease = true;
        EventSystem.current?.SetSelectedGameObject(null);
        _nextDroneDisplay = 0;
    }

    private bool SuppressTabletPointer()
    {
        if (Fullscreen) return true;
        if (!_fullscreenPointerRelease) return false;
        if (!Input.GetMouseButton(0)) _fullscreenPointerRelease = false;
        return true;
    }
}
