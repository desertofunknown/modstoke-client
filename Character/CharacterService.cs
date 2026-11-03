// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using Bolt;
using Modstoke.Sons;
using Sons.Multiplayer.Client;
using Sons.Save;
using Sons.Wearable.Clothing;
using Sons.Wearable.Race;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Client;

// Puts custom bodies on the local player and on every remote player whose descriptor the
// server relayed. Players without the character package keep the game's own models.
internal sealed class ClientCharacterService : IDisposable
{
    private const int LocalLod = 0, RemoteLod = 1;
    private readonly BodyLibrary _library = new();
    private BepInEx.Logging.ManualLogSource? _log;
    private string? _lastStatus;
    private readonly Dictionary<ulong, Remote> _remotes = new();
    private readonly Dictionary<ulong, (IntPtr System, Guid Generation, float RetryAt)> _failures = new();
    private CharacterBody? _local;
    private IntPtr _localSystem;
    private byte[]? _localAttached;
    private float _nextAttach;

    private sealed class Remote(CharacterBody body, IntPtr system, Guid generation, byte[] data, PlayerClothingSystem? clothing)
    {
        internal readonly CharacterBody Body = body;
        internal readonly IntPtr System = system;
        internal readonly Guid Generation = generation;
        internal readonly byte[] Data = data;
        internal readonly PlayerClothingSystem? Clothing = clothing;
    }

    internal CharacterAppearance? Appearance { get; set; }
    internal bool Preview { get; set; }
    // This viewer confirmed their age for the adult add-on. It is never sent to anyone.
    internal bool AdultOptIn { get; set; }
    internal bool AdultInstalled => _library.AdultInstalled;
    internal bool HdInstalled => _library.HdInstalled;
    internal bool UhdInstalled => _library.UhdInstalled;
    // 0 picks the best installed pack for this graphics card; otherwise 2048, 4096 or 8192.
    internal int TextureSetting { get; set; }

    internal int ResolvedTier
    {
        get
        {
            var best = UhdInstalled ? 8192 : HdInstalled ? 4096 : 2048;
            if (TextureSetting != 0) return System.Math.Min(TextureSetting, best);
            var memory = SystemInfo.graphicsMemorySize;
            return System.Math.Min(best, memory >= 10000 ? 8192 : memory >= 6000 ? 4096 : 2048);
        }
    }
    private bool _adultView;
    internal bool PackageInstalled => _library.Installed;
    internal string Status { get; private set; } = "The character package is not installed.";

    internal void Update()
    {
        if (_log == null)
        {
            _log = BepInEx.Logging.Logger.CreateLogSource("Modstoke Client character");
            _library.Log = _log;
        }
        if (!GameRuntime.State.IsInWorld || SaveGameManager.LoadInProgress())
        {
            Clear();
            return;
        }
        var adultView = _library.AdultInstalled && AdultOptIn;
        var tier = ResolvedTier;
        if (adultView != _adultView || tier != _library.Tier)
        {
            Clear();
            _adultView = adultView;
            _library.Tier = tier;
        }
        UpdateLocal();
        UpdateRemotes();
    }

    private void UpdateLocal()
    {
        var appearance = _library.Installed ? Appearance : null;
        var data = appearance?.Encode();
        Plugin.TabletPresence.SetLocalAppearance(data);
        var system = LocalPlayer.RaceSystem;
        if (appearance == null || system == null || !system)
        {
            DetachLocal();
            return;
        }
        if (_local != null && (_localSystem != system.Pointer || !_local.Live || _local.Model?.Sex != appearance.Sex))
            DetachLocal();
        if (_local == null)
        {
            if (Time.unscaledTime < _nextAttach) return;
            _nextAttach = Time.unscaledTime + 2;
            var body = new CharacterBody(system, true)
            {
                Coverage = (covered, preview) => _log?.LogInfo("Body coverage " + covered + (preview ? " (creator preview)." : "."))
            };
            if (!body.TryAttach(_library, appearance, LocalLod, _adultView, out var status))
            {
                Report(status);
                body.Dispose();
                return;
            }
            Report(status + " (" + appearance.Sex + ", " + _library.Tier / 1024 + "K textures)");
            _local = body;
            _localSystem = system.Pointer;
            _localAttached = data;
        }
        else if (!_localAttached!.AsSpan().SequenceEqual(data))
        {
            _local.Apply(appearance);
            _localAttached = data;
        }
        _local.Refresh(LocalPlayer.ClothingSystem, Preview);
    }

    private void UpdateRemotes()
    {
        var appearances = Plugin.TabletPresence.Appearances;
        foreach (var actor in _remotes.Keys.ToArray())
            if (!appearances.ContainsKey(actor)) Retire(actor);
        foreach (var actor in _failures.Keys.ToArray())
            if (!appearances.ContainsKey(actor)) _failures.Remove(actor);
        if (!_library.Installed || !BoltNetwork.isRunning) return;
        foreach (var (actor, synced) in appearances)
        {
            if (!TryFind(actor, out var system, out var clothing))
            {
                Retire(actor);
                continue;
            }
            if (_remotes.TryGetValue(actor, out var remote)
                && (remote.System != system.Pointer || remote.Generation != synced.Generation || !remote.Body.Live))
            {
                Retire(actor);
                remote = null;
            }
            if (!CharacterAppearance.TryDecode(synced.Data, out var appearance)) continue;
            if (remote != null && remote.Body.Model?.Sex != appearance.Sex)
            {
                Retire(actor);
                remote = null;
            }
            if (remote == null)
            {
                // A failed attach repeats only after a delay or when the player's model changes.
                if (_failures.TryGetValue(actor, out var failure) && failure.System == system.Pointer
                    && failure.Generation == synced.Generation && Time.unscaledTime < failure.RetryAt) continue;
                var body = new CharacterBody(system, false);
                if (!body.TryAttach(_library, appearance, RemoteLod, _adultView, out var status))
                {
                    body.Dispose();
                    _failures[actor] = (system.Pointer, synced.Generation, Time.unscaledTime + 5);
                    Report("Another player's body: " + status);
                    continue;
                }
                _failures.Remove(actor);
                remote = new Remote(body, system.Pointer, synced.Generation, synced.Data, clothing);
                _remotes[actor] = remote;
            }
            else if (!remote.Data.AsSpan().SequenceEqual(synced.Data))
            {
                remote.Body.Apply(appearance);
                _remotes[actor] = new Remote(remote.Body, remote.System, remote.Generation, synced.Data, remote.Clothing);
                remote = _remotes[actor];
            }
            remote.Body.Refresh(remote.Clothing, false);
        }
    }

    // Each distinct status is logged once, so retries stay quiet.
    private void Report(string status)
    {
        Status = status;
        if (status == _lastStatus) return;
        _lastStatus = status;
        _log?.LogInfo(status);
    }

    private static bool TryFind(ulong actor, out PlayerRaceSystem system, out PlayerClothingSystem? clothing)
    {
        system = null!;
        clothing = null;
        var entity = BoltNetwork.FindEntity(new NetworkId(actor));
        if (entity == null || !entity || !entity.isAttached) return false;
        var local = LocalPlayer.Entity;
        if (local != null && local && entity.Pointer == local.Pointer) return false;
        CoopPlayerRemoteSetup? setup = null;
        foreach (var candidate in entity.GetComponentsInChildren<CoopPlayerRemoteSetup>(true))
        {
            if (candidate == null || !candidate || candidate._entity == null || candidate._entity.Pointer != entity.Pointer) continue;
            if (setup != null) return false;
            setup = candidate;
        }
        if (setup == null || setup._raceSystem == null || !setup._raceSystem) return false;
        system = setup._raceSystem;
        clothing = setup._clothingSystem;
        return true;
    }

    private void Retire(ulong actor)
    {
        if (!_remotes.Remove(actor, out var remote)) return;
        remote.Body.Dispose();
    }

    private void DetachLocal()
    {
        _local?.Dispose();
        _local = null;
        _localSystem = IntPtr.Zero;
        _localAttached = null;
    }

    private void Clear()
    {
        DetachLocal();
        foreach (var actor in _remotes.Keys.ToArray()) Retire(actor);
        _failures.Clear();
    }

    public void Dispose()
    {
        Clear();
        _library.Dispose();
        if (_log != null)
        {
            BepInEx.Logging.Logger.Sources.Remove(_log);
            _log.Dispose();
            _log = null;
        }
    }
}
