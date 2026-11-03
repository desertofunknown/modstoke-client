// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Reflection;
using HarmonyLib;
using TheForest.Items.Inventory;
using TheForest.Utils;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Modstoke.Client;

// Replace the native GPS action only when the tablet accepts that local input.
internal sealed class ClientGpsInput : IDisposable
{
    private static ClientGpsInput? _active;
    private readonly Func<bool> _accept;
    private readonly Harmony _patches = new("modstoke.client.gps-input");
    private MethodInfo? _target;

    internal ClientGpsInput(Func<bool> accept) => _accept = accept;

    internal void Start()
    {
        if (_active != null) throw new InvalidOperationException("GPS tablet input is already registered.");
        var target = AccessTools.Method(typeof(PlayerInventory), nameof(PlayerInventory.ToggleGpsTracker),
            new[] { typeof(InputAction.CallbackContext) });
        if (target == null) throw new MissingMethodException("The native GPS action is unavailable.");
        _target = target;
        _active = this;
        try
        {
            _patches.Patch(target, prefix: new HarmonyMethod(typeof(ClientGpsInput), nameof(BeforeToggle))
                { priority = Priority.Last });
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private static bool BeforeToggle(PlayerInventory __instance, bool __runOriginal)
    {
        if (!__runOriginal || _active == null)
            return true;
        try
        {
            var local = LocalPlayer.Inventory;
            if (!local || !__instance || local.Pointer != __instance.Pointer)
                return true;
            // The native handler toggles GPS without checking the callback phase.
            // Every invocation must honor the tablet's current input ownership.
            return !_active._accept();
        }
        catch (Exception error)
        {
            Debug.LogWarning("Modstoke Client: native GPS input kept its original behavior: " + error);
            return true;
        }
    }

    public void Dispose()
    {
        if (ReferenceEquals(_active, this)) _active = null;
        if (_target != null)
        {
            _patches.Unpatch(_target, HarmonyPatchType.Prefix, _patches.Id);
            _target = null;
        }
    }
}
