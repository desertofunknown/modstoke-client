// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Modstoke.Sons;
using TheForest.Utils;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Modstoke.Client;

internal sealed partial class ClientTerminal
{
    private readonly ClientDeviceInput _deviceInput = new();
    private EventSystem? _arcadeEventSystem;
    private bool _arcadeNavigationWasEnabled;
    private bool _arcadeKeyboard;
    private bool _aircraftInput;
    private object? _aircraftInputCapture;
    private bool _aircraftAllowsPause;
    private IntPtr _aircraftInputActor, _aircraftInputStats;
    private int _aircraftInputLoad;

    internal bool TryAcquireAircraftInput(bool allowPause = false)
    {
        if (_aircraftInput && !AircraftInputCaptured) ReleaseAircraftInput();
        if (!_registered || DeviceActive || !GameRuntime.State.IsInWorld || GameRuntime.IsTitleScene || !LocalInputAlive()) return false;
        var actor = LocalPlayer.Transform.Pointer;
        var stats = LocalPlayer.Stats.Pointer;
        var load = SaveLifecycle.State.BeforeLoadCount;
        var inventory = LocalPlayer.Inventory;
        if (inventory == null || inventory.WasCollected || !inventory
            || inventory._currentEquipState != TheForest.Items.Inventory.PlayerInventory.HandEquipState.Idle) return false;
        if (_aircraftInput) return _aircraftAllowsPause == allowPause;
        _aircraftInputActor = actor;
        _aircraftInputStats = stats;
        _aircraftInputLoad = load;
        if (!_deviceInput.Acquire(allowPause ? () => AircraftInputCurrent : null)) return false;
        _aircraftInputCapture = _deviceInput.CaptureToken;
        _aircraftAllowsPause = allowPause;
        _aircraftInput = true;
        return true;
    }

    internal bool AircraftInputCaptured => _aircraftInput && AircraftInputCurrent
        && _deviceInput.IsCaptureCurrent(_aircraftInputCapture, _aircraftAllowsPause);

    internal void ReleaseAircraftInput()
    {
        var capture = _aircraftInputCapture;
        _aircraftInput = false;
        _aircraftInputCapture = null;
        _aircraftAllowsPause = false;
        _aircraftInputActor = _aircraftInputStats = IntPtr.Zero;
        _aircraftInputLoad = 0;
        // A stale rider must not release a newer tablet or watch capture.
        if (capture != null && ReferenceEquals(_deviceInput.CaptureToken, capture)) _deviceInput.Release();
    }

    private bool AircraftInputCurrent => GameRuntime.State.IsInWorld && !GameRuntime.IsTitleScene
        && _aircraftInputLoad == SaveLifecycle.State.BeforeLoadCount && LocalInputAlive()
        && LocalPlayer.Transform.Pointer == _aircraftInputActor && LocalPlayer.Stats.Pointer == _aircraftInputStats;

    private static bool LocalInputAlive() => LocalPlayer.Transform != null && !LocalPlayer.Transform.WasCollected && LocalPlayer.Transform
        && LocalPlayer.Stats != null && !LocalPlayer.Stats.WasCollected
        && LocalPlayer.Stats._dyingState == PlayerStats.DyingState.Alive
        && !LocalPlayer.Stats._playerIsDowned && !LocalPlayer.Stats._respawning;

    internal void CloseForAircraft()
    {
        ClearInventoryPresentation();
        _tabletAfterWatch = _controllerAfterWatch = _nativeGpsRequested = _messagesRequested = false;
        _wristWatch.EndInspection(immediate: true);
        if (_open || _opening || _tablet.IsActive) Close();
    }

    private bool DeviceActive => _tablet.IsActive || _open || _opening || _holstering
        || _wristWatch.IsInspecting || _wristWatch.IsReleasing;

    private bool AcquireDeviceInput()
    {
        if (_deviceInput.Acquire()) return true;
        Notice("Close the current menu before using a field device.");
        return false;
    }

    private void SyncDeviceInput()
    {
        if (_aircraftInput && !AircraftInputCaptured) ReleaseAircraftInput();
        if (!_registered || !GameRuntime.State.IsInWorld || !_aircraftInput && (_failed || !DeviceActive))
            _deviceInput.Release();
        if (!_open || _failed || !_deviceInput.Captured || !Application.isFocused
            || (_app != ClientTerminalApp.Games && _app != ClientTerminalApp.Drone)
            || _contentObscured || !CanUseDesktop())
        {
            _games.SetVisible(false);
            ReleaseArcadeKeyboard();
            SetArcadeNavigation(false);
        }
    }

    private void UpdateArcadeInput(bool visible)
    {
        _games.SetVisible(visible);
        // Drone input owns its own navigation decision after the field update.
        if (_app != ClientTerminalApp.Drone) SetArcadeNavigation(visible);
        if (!visible)
        {
            ReleaseArcadeKeyboard();
            return;
        }
        var x = (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow) ? 1 : 0)
            - (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow) ? 1 : 0);
        var y = (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow) ? 1 : 0)
            - (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow) ? 1 : 0);
        var jump = Input.GetKeyDown(KeyCode.Space);
        var fire = Input.GetKey(KeyCode.Space);
        var dash = Input.GetKeyDown(KeyCode.LeftShift) || Input.GetKeyDown(KeyCode.RightShift);
        var enter = Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter);
        var pause = Input.GetKeyDown(KeyCode.P);
        if (x != 0 || y != 0 || fire || dash || enter || pause) _arcadeKeyboard = true;
        var state = _games.Snapshot;
        if (pause && state.Started && !state.Finished)
            _games.HandleCommand(state.Paused ? "continue" : "pause");
        else if (enter && !state.Finished)
            _games.HandleCommand("continue");
        _games.SetKeyboard(x, y, jump, fire, dash, _arcadeKeyboard);
    }

    private void ReleaseArcadeKeyboard()
    {
        _arcadeKeyboard = false;
        _games.SetKeyboard(0, 0, false, false, false, false);
    }

    private void SetArcadeNavigation(bool captured)
    {
        var current = EventSystem.current;
        captured = captured && _tabletEventSystem != null && _tabletEventSystem
            && current != null && current && current.Pointer == _tabletEventSystem.Pointer;
        if (_arcadeEventSystem != null && (!_arcadeEventSystem || current == null
            || current.Pointer != _arcadeEventSystem.Pointer || !captured))
        {
            if (_arcadeEventSystem && !_arcadeEventSystem.sendNavigationEvents)
                _arcadeEventSystem.sendNavigationEvents = _arcadeNavigationWasEnabled;
            _arcadeEventSystem = null;
        }
        if (!captured || current == null || !current || _arcadeEventSystem != null) return;
        _arcadeEventSystem = current;
        _arcadeNavigationWasEnabled = current.sendNavigationEvents;
        current.sendNavigationEvents = false;
    }
}
