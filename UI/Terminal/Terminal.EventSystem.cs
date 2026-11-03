// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using Sons.Input;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Modstoke.Client;

internal sealed partial class ClientTerminal
{
    private EventSystem? _tabletEventSystem;
    private EventSystem? _previousEventSystem;
    private GameObject? _tabletInputRoot;

    private void CreateTabletEventSystem()
    {
        _tabletInputRoot = new GameObject("Modstoke Tablet Input");
        _tabletInputRoot.SetActive(false);
        _tabletInputRoot.transform.SetParent(_canvas!.transform, false);
        _tabletEventSystem = _tabletInputRoot.AddComponent<EventSystem>();
        var module = _tabletInputRoot.AddComponent<StandaloneInputModule>();
        module.inputOverride = _tabletInputRoot.AddComponent<BaseInput>();
        // ClientDeviceInput alone owns action maps; this root owns only UI dispatch and cursor.
        var cursor = _tabletInputRoot.AddComponent<InputCursorState>();
        cursor._enabled = true;
        cursor._hardwareCursor = true;
        cursor._priority = 100;
    }

    private void SynchronizeTabletEventSystem()
    {
        if (!_open || _failed || !Application.isFocused || !global::Modstoke.Sons.GameRuntime.State.IsInWorld
            || global::Modstoke.Sons.GameRuntime.State.PauseMenuOpen || LoaderMenuVisible())
        {
            ReleaseTabletEventSystem();
            return;
        }

        if (_tabletEventSystem == null || !_tabletEventSystem
            || _tabletInputRoot == null || !_tabletInputRoot) return;
        if (!_tabletInputRoot.activeSelf)
        {
            // OnEnable can change EventSystem.current, so capture the owner first.
            _previousEventSystem = EventSystem.current;
            _tabletInputRoot.SetActive(true);
        }

        if (_tabletEventSystem.isActiveAndEnabled && EventSystem.current != _tabletEventSystem)
            EventSystem.current = _tabletEventSystem;
    }

    private void ReleaseTabletEventSystem()
    {
        var dispatcher = _tabletEventSystem;
        var previous = _previousEventSystem;
        _previousEventSystem = null;
        try
        {
            SetArcadeNavigation(false);
            if (dispatcher != null && dispatcher)
            {
                var selected = dispatcher.currentSelectedGameObject;
                if (selected != null && selected && _canvas != null && _canvas
                    && selected.transform.IsChildOf(_canvas.transform))
                    dispatcher.SetSelectedGameObject(null);
            }
        }
        finally
        {
            var owned = dispatcher != null && dispatcher && EventSystem.current == dispatcher;
            // Disabling also releases the cursor, including while the screen is holstering.
            if (_tabletInputRoot != null && _tabletInputRoot && _tabletInputRoot.activeSelf)
                _tabletInputRoot.SetActive(false);
            // Another menu may have taken over before release. Do not overwrite it.
            if (owned && previous != null && previous && previous != dispatcher && previous.isActiveAndEnabled)
                EventSystem.current = previous;
        }
    }

    private void DestroyTabletEventSystem()
    {
        try { ReleaseTabletEventSystem(); }
        finally
        {
            if (_tabletInputRoot != null && _tabletInputRoot) UnityEngine.Object.Destroy(_tabletInputRoot);
            _tabletInputRoot = null;
            _tabletEventSystem = null;
            _previousEventSystem = null;
        }
    }
}
