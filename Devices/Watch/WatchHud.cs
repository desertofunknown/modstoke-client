// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes;
using Shapes;
using Sons;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Client;

internal enum ClientWatchHudMode { Standard, Minimal, Immersive }

// Main-thread presentation ownership only. Native vitals, icons and HUD parents remain active.
internal sealed class ClientWatchHud : IDisposable
{
    [Flags]
    private enum Vital { Health = 1, Stamina = 2, Fullness = 4, Rest = 8, Hydration = 16 }

    private sealed class Entry
    {
        internal readonly Renderer Renderer;
        internal readonly IntPtr Pointer;
        internal readonly int InstanceId;
        internal Vital Vitals;
        internal bool Alert;
        internal bool Requested;
        internal bool OriginalEnabled;
        internal bool Owned;
        internal bool Yielded;

        internal Entry(Renderer renderer)
        {
            Renderer = renderer;
            Pointer = NativePointer(renderer);
            InstanceId = renderer.GetInstanceID();
        }

        internal bool Live => Renderer != null && NativePointer(Renderer) == Pointer
            && Renderer.GetInstanceID() == InstanceId;

        internal void Apply(bool hide)
        {
            if (!Live) { Owned = false; return; }
            var enabled = Renderer.enabled;
            // Another owner or the native HUD has changed our applied state. Yield for this binding.
            if (Requested && enabled) { Owned = false; Yielded = true; }
            if (!hide)
            {
                Restore();
                return;
            }
            if (Requested || Yielded) return;
            Requested = true;
            OriginalEnabled = enabled;
            if (!enabled) return;
            Renderer.enabled = false;
            Owned = true;
        }

        internal void Restore()
        {
            if (Owned && Live && !Renderer.enabled) Renderer.enabled = OriginalEnabled;
            Owned = false;
            Requested = false;
        }
    }

    private const string Caveat = " Icons and unsupported indicators remain visible; third-party minimap hiding is unsupported.";
    private Dictionary<IntPtr, Entry> _entries = new();
    private VitalsGui? _gui;
    private UnityEngine.Object? _player;
    private int _scene;
    private float _nextDiscovery;
    private float _retryAfter;
    private bool _disposed;
    private int _missing;

    internal string Status { get; private set; } = "Standard HUD.";

    internal void Update(ClientWatchHudMode mode, bool watchAvailable, ClientWristWatchVitals vitals)
    {
        if (_disposed) return;
        if (mode is not (ClientWatchHudMode.Minimal or ClientWatchHudMode.Immersive) || !watchAvailable)
        {
            Release();
            Status = mode == ClientWatchHudMode.Standard ? "Standard HUD."
                : "Native HUD retained while the watch is unavailable.";
            return;
        }

        try
        {
            if (Time.unscaledTime < _retryAfter) return;
            var player = LocalPlayer.Stats;
            if (player == null || !player.gameObject.activeInHierarchy)
            {
                Release();
                Status = "Native HUD retained while local vitals are unavailable.";
                return;
            }
            var scene = player.gameObject.scene.handle;
            if (!Same(_player, player) || _scene != scene)
            {
                Release();
                _player = player;
                _scene = scene;
            }

            var now = Time.unscaledTime;
            if (now >= _nextDiscovery)
            {
                _nextDiscovery = now + 2f;
                Discover(player);
            }
            if (_gui == null || !_gui.isActiveAndEnabled || !Same(_gui._playerStats, player))
            {
                RestoreEntries();
                Status = "Native HUD retained: local vital indicators are unavailable." + Caveat;
                return;
            }

            var represented = Represented(mode, vitals);
            var hidden = 0;
            var yielded = 0;
            foreach (var entry in _entries.Values)
            {
                entry.Apply((entry.Vitals & represented) == entry.Vitals
                    && !(mode == ClientWatchHudMode.Minimal && entry.Alert));
                if (entry.Owned) hidden++;
                if (entry.Yielded) yielded++;
            }
            Status = $"{mode}: {hidden} vital renderers hidden."
                + (mode == ClientWatchHudMode.Minimal ? " Low resources and alert discs retain the native HUD." : "")
                + (_missing > 0 ? $" {_missing} native elements unavailable." : "")
                + (yielded > 0 ? $" {yielded} renderers yielded to another HUD owner." : "") + Caveat;
        }
        catch (Exception error)
        {
            Release();
            _retryAfter = Time.unscaledTime + 2f;
            Status = "Native HUD retained: selective replacement is unavailable. " + error.Message + Caveat;
        }
    }

    private static Vital Represented(ClientWatchHudMode mode, ClientWristWatchVitals vitals)
    {
        var mask = (Vital)0;
        float? health = vitals.Health is { } current && float.IsFinite(current) && current >= 0
            && vitals.MaximumHealth is { } maximum && float.IsFinite(maximum) && maximum > 0
                ? current / maximum : null;
        if (CanHide(health, mode)) mask |= Vital.Health;
        if (CanHide(vitals.Stamina, mode)) mask |= Vital.Stamina;
        if (CanHide(vitals.Fullness, mode)) mask |= Vital.Fullness;
        if (CanHide(vitals.Rest, mode)) mask |= Vital.Rest;
        if (CanHide(vitals.Hydration, mode)) mask |= Vital.Hydration;
        return mask;
    }

    private static bool CanHide(float? factor, ClientWatchHudMode mode) => factor is { } value
        && float.IsFinite(value) && value >= 0 && value <= 1
        && (mode == ClientWatchHudMode.Immersive || value > .25f);

    private void Discover(UnityEngine.Object player)
    {
        VitalsGui? found = null;
        // Exact type and local player identity exclude prefab assets and another player's GUI.
        foreach (var candidate in Resources.FindObjectsOfTypeAll<VitalsGui>())
        {
            if (candidate == null || !candidate.isActiveAndEnabled || !Same(candidate._playerStats, player)) continue;
            if (found != null && !Same(found, candidate))
            {
                RestoreEntries();
                _gui = null;
                return; // Ambiguous ownership: retain native presentation.
            }
            found = candidate;
        }

        if (!Same(_gui, found)) RestoreEntries();
        _gui = found;
        if (found == null) return;
        var next = new Dictionary<IntPtr, Entry>();
        _missing = 0;
        Add(next, found._healthElementDef?._discElement, Vital.Health);
        Add(next, found._healthBackgroundElementDef?._discElement, Vital.Health);
        Add(next, found._healthVitalityClampedElementDef?._discElement, Vital.Health);
        Add(next, found._healthPaddingHighlightElementDef?._discElement, Vital.Health);
        Add(next, found._targetHealthElementDef?._discElement, Vital.Health);
        Add(next, found._vitalityHealthElementDef?._discElement, Vital.Health);
        Add(next, found._healthAmountIndicator?._discElement, Vital.Health);
        Add(next, found._energyElementDef?._discElement, Vital.Stamina);
        Add(next, found._energyBackgroundElementDef?._discElement, Vital.Stamina);
        Add(next, found._energyVitalityClampedElementDef?._discElement, Vital.Stamina);
        Add(next, found._energyPaddingHighlightElementDef?._discElement, Vital.Stamina);
        Add(next, found._vitalityEnergyElementDef?._discElement, Vital.Stamina);
        Add(next, found._fullnessElementDef?._discElement, Vital.Fullness);
        Add(next, found._fullnessBuffElementDef?._discElement, Vital.Fullness);
        Add(next, found._fullnessAlertDisc, Vital.Fullness, alert: true);
        Add(next, found._restedElementDef?._discElement, Vital.Rest);
        Add(next, found._restedBuffElementDef?._discElement, Vital.Rest);
        Add(next, found._restedAlertDisc, Vital.Rest, alert: true);
        Add(next, found._hydrationElementDef?._discElement, Vital.Hydration);
        Add(next, found._hydrationBuffElementDef?._discElement, Vital.Hydration);
        Add(next, found._hydrationAlertDisc, Vital.Hydration, alert: true);
        foreach (var old in _entries.Values)
            if (!next.TryGetValue(old.Pointer, out var retained) || !ReferenceEquals(old, retained)) Restore(old);
        _entries = next;
    }

    private void Add(Dictionary<IntPtr, Entry> next, Disc? disc, Vital vital, bool alert = false)
    {
        var renderer = disc != null ? disc.rnd : null;
        if (renderer == null) { _missing++; return; }
        var pointer = NativePointer(renderer);
        if (!next.TryGetValue(pointer, out var entry))
        {
            if (!_entries.TryGetValue(pointer, out entry) || !entry.Live
                || entry.InstanceId != renderer.GetInstanceID()) entry = new Entry(renderer);
            entry.Vitals = 0;
            entry.Alert = false;
            next.Add(pointer, entry);
        }
        entry.Vitals |= vital;
        entry.Alert |= alert;
    }

    private static IntPtr NativePointer(UnityEngine.Object value) => ((Il2CppObjectBase)value).Pointer;
    private static bool Same(UnityEngine.Object? left, UnityEngine.Object? right) =>
        left != null && right != null && NativePointer(left) == NativePointer(right)
        && left.GetInstanceID() == right.GetInstanceID();

    private static void Restore(Entry entry)
    {
        try { entry.Restore(); }
        catch (Exception) { /* A destroyed scene object cannot be restored. Continue releasing the rest. */ }
    }

    private void RestoreEntries()
    {
        foreach (var entry in _entries.Values) Restore(entry);
        _entries.Clear();
    }

    private void Release()
    {
        RestoreEntries();
        _gui = null;
        _player = null;
        _scene = 0;
        _nextDiscovery = 0;
        _missing = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Release();
        Status = "Native HUD ownership released.";
    }
}
