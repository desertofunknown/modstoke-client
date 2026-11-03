// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;

namespace Modstoke.Client;

// Complete native animation and camera updates before placing the remote tablet and hands.
internal sealed class ClientTabletPresenceFrameUpdate : IDisposable
{
    private static ClientTabletPresenceFrameUpdate? _installed;
    private readonly PlayerLoopSystem.UpdateFunction _update;
    private readonly Action<Exception> _onFailure;
    private Action? _callback;

    private ClientTabletPresenceFrameUpdate(Action callback, Action<Exception> onFailure)
    {
        _callback = callback;
        _onFailure = onFailure;
        _update = (Action)Invoke;
    }

    internal static IDisposable? TryInstall(Action callback, Action<Exception> onFailure, out string failure)
    {
        failure = "";
        // A previous removal may have failed while the game was changing loops.
        // Its retained delegate is inert; retry that removal before reacquiring.
        if (_installed != null && _installed._callback == null) _installed.Dispose();
        if (_installed != null)
        {
            failure = "The previous remote tablet animation update has not been released.";
            return null;
        }
        ClientTabletPresenceFrameUpdate? owned = null;
        try
        {
            // The installed interop's public tree-conversion helpers are incomplete.
            // Its internal flat-array getter/setter resolve Unity icalls directly.
            var current = PlayerLoop.GetCurrentPlayerLoopInternal();
            if (!TryParents(current, out var parents))
            {
                failure = "The remote tablet could not read the game's animation update layout.";
                return null;
            }
            var late = FindType<PreLateUpdate.ScriptRunBehaviourLateUpdate>(current);
            var preLate = FindType<PreLateUpdate>(current);
            var postLate = FindType<PostLateUpdate>(current);
            if (preLate < 0 || postLate <= preLate || late <= preLate
                || parents[preLate] != 0 || parents[postLate] != 0)
            {
                failure = "The remote tablet could not find the game's animation-to-render boundary.";
                return null;
            }
            var insertion = preLate + current[preLate].numSubSystems + 1;
            if (late >= insertion || insertion > postLate)
            {
                failure = "The game's animation updates overlap its rendering stage.";
                return null;
            }

            owned = new ClientTabletPresenceFrameUpdate(callback, onFailure);
            // Use the length overload; an int binds to the native-pointer constructor.
            var updated = new Il2CppReferenceArray<PlayerLoopSystemInternal>((long)current.Length + 1);
            for (var i = 0; i < current.Length; i++)
                updated[i < insertion ? i : i + 1] = current[i];
            // This is a sibling after the complete phase, including all camera
            // constraints. Optional native leaves need not expose a function pointer.
            for (var ancestor = parents[preLate]; ancestor >= 0; ancestor = parents[ancestor])
            {
                // Value-type array reads return boxed copies; write each change back.
                var node = updated[ancestor];
                node.numSubSystems++;
                updated[ancestor] = node;
            }
            updated[insertion] = new PlayerLoopSystemInternal
            {
                type = Il2CppType.Of<PlayerLoopSystem.UpdateFunction>(),
                updateDelegate = owned._update,
                updateFunction = IntPtr.Zero,
                loopConditionFunction = IntPtr.Zero,
                numSubSystems = 0
            };
            // Retain the native delegate before publishing it, including a setter
            // failure whose outcome must be checked by the ordinary removal path.
            _installed = owned;
            PlayerLoop.SetPlayerLoopInternal(updated);
            return owned;
        }
        catch (Exception error)
        {
            try { owned?.Dispose(); } catch (Exception) { }
            failure = "The remote tablet animation update could not be registered (" + error.GetType().Name + ").";
            return null;
        }
    }

    private void Invoke()
    {
        try { _callback?.Invoke(); }
        catch (Exception error)
        {
            // Pose callbacks release their ownership before cleanup. A cleanup
            // failure must not escape through Unity's native player-loop delegate.
            _callback = null;
            try { _onFailure(error); } catch (Exception) { }
        }
    }

    public void Dispose()
    {
        _callback = null;
        if (!ReferenceEquals(_installed, this)) return;
        // Keep the inert delegate rooted until removal succeeds; Unity may still own it.
        // Other modules may have changed the loop, so remove from its current layout.
        var current = PlayerLoop.GetCurrentPlayerLoopInternal();
        if (!TryParents(current, out var parents))
            throw new InvalidOperationException("The current player loop has invalid subtree counts.");
        var index = -1;
        for (var i = 0; i < current.Length; i++)
        {
            if (current[i].updateDelegate?.Pointer != _update.Pointer) continue;
            if (index >= 0) throw new InvalidOperationException("The remote tablet frame callback appears more than once.");
            index = i;
        }
        if (index < 0) { _installed = null; return; }
        var owned = current[index];
        if (index == 0 || owned.numSubSystems != 0 || owned.updateFunction != IntPtr.Zero
            || owned.loopConditionFunction != IntPtr.Zero
            || owned.type?.Pointer != Il2CppType.Of<PlayerLoopSystem.UpdateFunction>().Pointer)
            throw new InvalidOperationException("The remote tablet frame callback no longer owns an unchanged leaf.");

        var updated = new Il2CppReferenceArray<PlayerLoopSystemInternal>((long)current.Length - 1);
        for (var i = 0; i < current.Length; i++)
            if (i != index) updated[i < index ? i : i - 1] = current[i];
        for (var ancestor = parents[index]; ancestor >= 0; ancestor = parents[ancestor])
        {
            var node = updated[ancestor];
            node.numSubSystems--;
            updated[ancestor] = node;
        }
        PlayerLoop.SetPlayerLoopInternal(updated);
        _installed = null;
    }

    private static int FindType<T>(Il2CppReferenceArray<PlayerLoopSystemInternal> loop)
    {
        var pointer = Il2CppType.Of<T>().Pointer;
        var found = -1;
        for (var i = 0; i < loop.Length; i++)
        {
            if (loop[i].type?.Pointer != pointer) continue;
            if (found >= 0) return -1;
            found = i;
        }
        return found;
    }

    private static bool TryParents(Il2CppReferenceArray<PlayerLoopSystemInternal>? loop, out int[] parents)
    {
        parents = Array.Empty<int>();
        if (loop == null || loop.Length == 0 || loop[0].numSubSystems != loop.Length - 1) return false;
        parents = new int[loop.Length];
        var ancestors = new List<int>();
        for (var i = 0; i < loop.Length; i++)
        {
            while (ancestors.Count > 0 && i > ancestors[^1] + loop[ancestors[^1]].numSubSystems)
                ancestors.RemoveAt(ancestors.Count - 1);
            var count = loop[i].numSubSystems;
            if (count < 0 || count > loop.Length - i - 1 || (i > 0 && ancestors.Count == 0)) return false;
            var parent = ancestors.Count > 0 ? ancestors[^1] : -1;
            if (parent >= 0 && i + count > parent + loop[parent].numSubSystems) return false;
            parents[i] = parent;
            if (count > 0) ancestors.Add(i);
        }
        return true;
    }
}


