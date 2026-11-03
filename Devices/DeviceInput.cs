// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Reflection;
using HarmonyLib;
using Modstoke.Loader;
using Sons.Input;
using SonsSdk;
using TheForest;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Client;

// The native Console state disables world, vehicle and item hotkeys while retaining UI input.
// It is a boolean, not a stack: the tablet and watch share one owner through their transitions.
internal sealed class ClientDeviceInput : IDisposable
{
    private static ClientDeviceInput? _active;
    private readonly Harmony _patches = new("modstoke.client.device-input");
    private MethodInfo? _consoleKeys;
    private MethodInfo? _applyInputMaps;
    private GameObject? _capture;
    private IDisposable? _focus;
    private Func<bool>? _allowPause;
    private SonsInputMapping? _pauseMapping;
    private UnityEngine.InputSystem.InputAction? _pauseAction;
    private bool _pauseEnabled;

    internal bool Captured => _focus != null;
    internal object? CaptureToken => _focus;

    internal bool IsCaptureCurrent(object? token, bool allowPause)
        => token != null && ReferenceEquals(_focus, token) && _capture != null && _capture && _capture.activeSelf
            && (_allowPause != null) == allowPause && (!allowPause || PauseBindingCurrent);

    private bool PauseBindingCurrent => _pauseMapping != null && !_pauseMapping.WasCollected
        && InputSystem.InputMapping?.Pointer == _pauseMapping.Pointer
        && _pauseAction != null && !_pauseAction.WasCollected;

    internal void Start()
    {
        if (_active != null) throw new InvalidOperationException("Device input is already registered.");
        _consoleKeys = AccessTools.Method(typeof(DebugConsole), nameof(DebugConsole.HandleConsoleKeys), Type.EmptyTypes)
            ?? throw new MissingMethodException("The game's console input handler is unavailable.");
        _applyInputMaps = AccessTools.Method(typeof(InputSystem), nameof(InputSystem.Apply))
            ?? throw new MissingMethodException("The game's input map handler is unavailable.");
        _active = this;
        try
        {
            _patches.Patch(_consoleKeys, prefix: new HarmonyMethod(typeof(ClientDeviceInput), nameof(BeforeConsoleKeys)));
            _patches.Patch(_applyInputMaps,
                prefix: new HarmonyMethod(typeof(ClientDeviceInput), nameof(BeforeApplyInputMaps)),
                postfix: new HarmonyMethod(typeof(ClientDeviceInput), nameof(AfterApplyInputMaps)));
        }
        catch { Dispose(); throw; }
    }

    internal bool Acquire(Func<bool>? allowPause = null)
    {
        if (Captured) return IsCaptureCurrent(_focus, allowPause != null);
        if (GameState.IsInConsole || !InputBindings.TryAcquireFocus("modstoke.client.devices", out _focus,
            "modstoke.client.terminal", ClientWristWatch.KeybindId)) return false;
        try
        {
            var focus = _focus;
            _allowPause = allowPause;
            if (allowPause != null)
            {
                _pauseMapping = InputSystem.InputMapping;
                _pauseAction = _pauseMapping.@default.TogglePauseMenu;
            }
            if (_capture == null || !_capture)
            {
                _capture = new GameObject("Modstoke Device Input");
                _capture.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(_capture);
                _capture.AddComponent<InputActionMapState>()._applyState = InputState.Console;
            }
            _capture.SetActive(true);
            // Applying Console can synchronously retire a failed pause binding.
            if (IsCaptureCurrent(focus, allowPause != null)) return true;
            throw new InvalidOperationException("Device input capture was lost while applying the game's input maps.");
        }
        catch { Release(); throw; }
    }

    internal void Release()
    {
        var focus = _focus;
        _focus = null;
        _allowPause = null;
        try
        {
            try { ReleasePauseAction(); }
            finally
            {
                _pauseAction = null;
                _pauseMapping = null;
                if (_capture != null && _capture && _capture.activeSelf) _capture.SetActive(false);
            }
        }
        finally { focus?.Dispose(); }
    }

    private void ReleasePauseAction()
    {
        var owned = _pauseEnabled;
        _pauseEnabled = false;
        if (owned && _pauseAction != null && !_pauseAction.WasCollected && _pauseAction.enabled)
            _pauseAction.Disable();
    }

    private static void BeforeApplyInputMaps()
    {
        // Remove our one-action exception before native routing takes ownership again.
        var owner = _active;
        try { owner?.ReleasePauseAction(); }
        catch (Exception error) { owner?.PauseActionFailed(error); }
    }

    private static void AfterApplyInputMaps()
    {
        var owner = _active;
        if (owner?._allowPause == null || !owner.Captured) return;
        try
        {
            if (!owner.PauseBindingCurrent)
            {
                owner.PauseActionFailed(new InvalidOperationException("The captured spacecraft pause binding is no longer current."));
                return;
            }
            if (!owner._allowPause() || !InputSystem.GetState(InputState.Console)
                || InputSystem.GetState(InputState.Menu) || InputSystem.GetState(InputState.Locked)
                || InputSystem.GetState(InputState.SteamOverlay) || InputSystem.GetState(InputState.VirtualKeyboard)
                || InputSystem.GetState(InputState.InputBinding) || InputSystem.GetState(InputState.ModalDialog)
                || InputSystem.GetState(InputState.Cutscene) || InputSystem.GetState(InputState.TextInput)
                || InputSystem.GetState(InputState.GameOver) || InputSystem.GetState(InputState.FirstDeath)
                || InputSystem.GetState(InputState.SavingMaps) || owner._pauseAction!.enabled) return;
            // Console suppresses Default, including pause. Enable only the bound pause action;
            // the native Menu map still owns closing the menu and navigating its controls.
            owner._pauseEnabled = true;
            owner._pauseAction!.Enable();
        }
        catch (Exception error) { owner.PauseActionFailed(error); }
    }

    private void PauseActionFailed(Exception error)
    {
        Debug.LogWarning("Modstoke Client: spacecraft pause input could not follow the game's input state: " + error);
        try { Release(); }
        catch (Exception cleanupError)
        {
            Debug.LogWarning("Modstoke Client: spacecraft input capture could not be fully released: " + cleanupError);
        }
    }

    private static bool BeforeConsoleKeys(ref bool __result)
    {
        if (_active?.Captured != true) return true;
        __result = false;
        return false;
    }

    public void Dispose()
    {
        try { Release(); }
        finally
        {
            if (ReferenceEquals(_active, this)) _active = null;
            if (_capture != null && _capture) UnityEngine.Object.Destroy(_capture);
            _capture = null;
            if (_consoleKeys != null) _patches.Unpatch(_consoleKeys, HarmonyPatchType.Prefix, _patches.Id);
            _consoleKeys = null;
            if (_applyInputMaps != null) _patches.Unpatch(_applyInputMaps, HarmonyPatchType.All, _patches.Id);
            _applyInputMaps = null;
        }
    }
}
