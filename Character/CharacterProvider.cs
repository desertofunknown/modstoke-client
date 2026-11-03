// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using Modstoke.Loader;
using Modstoke.Sons;
using Sons.Save;
using Sons.Wearable.Race;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Client;

// Keeps the game's own player model the same across joins. The native race is synced
// through player state, so every peer, with or without Modstoke, sees the same look.
internal sealed class ClientCharacterProvider : IMenuContentProvider, IMenuSettingsProvider, ICommandProvider,
    IMenuProviderPresentation
{
    internal const string MenuId = "modstoke.client.character";
    private const string CustomiseCommand = "customise";
    private const string LookKey = "look";
    private const int LookCount = 8;
    private const string RandomOption = "Random each join";
    private const string FirstOption = "Keep my first look";
    internal const string NoUnderwearOption = "None";
    private const string AdultOnOption = "On: I am 18 or older";
    private const string TextureAuto = "Auto";
    private const float SettleSeconds = 15;
    private static ClientCharacterProvider? _active;
    private readonly object _sync = new();
    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
    private readonly Harmony _patches = new("modstoke.client.character");
    private readonly ClientCharacterStore _store = new(GameRuntime.Environment.GameRootDirectory);
    private readonly Guid _scope = Guid.NewGuid();
    private bool _enabled;
    private bool _readable;
    private string? _look;
    private string _status = "Waiting for the client to start.";
    private int _attachFrame = -1;
    private float _nextCheck;
    private PlayerRace.Race? _firstRace;
    private float _keepAt;
    private CharacterAppearance _body = new();
    private bool _custom;
    private bool _bodySaved;

    internal static readonly (string Name, Color32 Color)[] SkinTones =
    {
        ("Porcelain", new(250, 224, 210, 255)), ("Fair", new(240, 206, 180, 255)), ("Light", new(226, 188, 160, 255)),
        ("Medium", new(206, 160, 124, 255)), ("Olive", new(188, 150, 108, 255)), ("Tan", new(170, 124, 88, 255)),
        ("Brown", new(138, 96, 66, 255)), ("Dark", new(104, 70, 48, 255)), ("Deep", new(76, 50, 36, 255))
    };
    internal static readonly (string Name, Color32 Color)[] EyeColors =
    {
        ("Brown", new(92, 62, 40, 255)), ("Hazel", new(120, 96, 56, 255)), ("Green", new(80, 112, 72, 255)),
        ("Blue", new(80, 112, 150, 255)), ("Grey", new(120, 128, 136, 255)), ("Amber", new(160, 112, 48, 255))
    };
    internal static readonly (string Name, Color32 Color)[] UnderwearColors =
    {
        ("Charcoal", new(40, 42, 48, 255)), ("Black", new(18, 18, 20, 255)), ("White", new(220, 220, 216, 255)),
        ("Navy", new(30, 40, 72, 255)), ("Red", new(130, 30, 34, 255)), ("Olive", new(70, 78, 52, 255))
    };

    public string Id => MenuId;
    public string DisplayName => "Character";
    public string Kind => MenuProviderKinds.Settings;
    public int Priority => 450;
    public string Source => "Modstoke Client by OpenACAI";
    public string Description => "Keep the same look every time you join";
    public bool RequiresAuthorization => false;
    public string OwnerModId => "modstoke.client";
    public bool Enabled { get { lock (_sync) return _enabled; } }

    internal void Start()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId || _active != null) return;
        lock (_sync)
        {
            _readable = _store.TryLoad(out _look, out var body, out _custom, out _status);
            _body = body ?? new CharacterAppearance();
            _bodySaved = body != null;
            _store.TryLoadAdult(out var adult, out var textures);
            Plugin.Characters.AdultOptIn = adult;
            Plugin.Characters.TextureSetting = textures;
            _enabled = true;
            Publish();
        }
        _active = this;
        try
        {
            Patch(typeof(BoltPlayerSetup), nameof(BoltPlayerSetup.LocalAttached), nameof(BeforeLocalAttached));
            Patch(typeof(PlayerRaceSystem), nameof(PlayerRaceSystem.GetRandomRace), nameof(BeforeRandomRace));
        }
        catch
        {
            Deactivate();
            throw;
        }
    }

    private void Patch(Type type, string name, string prefix)
    {
        var target = AccessTools.DeclaredMethod(type, name, Type.EmptyTypes)
            ?? throw new MissingMethodException(type.FullName, name);
        // Run after the Loader's player-role prefix so a redirected carrier never counts as our attach.
        _patches.Patch(target, prefix: new HarmonyMethod(typeof(ClientCharacterProvider), prefix) { priority = HarmonyLib.Priority.Last });
    }

    // Only the local player's own attach picks from the profile; stand-ins the host
    // spawns for absent players keep the game's random choice.
    private static void BeforeLocalAttached(bool __runOriginal)
    {
        if (__runOriginal && _active != null) _active._attachFrame = Time.frameCount;
    }

    private static bool BeforeRandomRace(ref PlayerRace.Race __result)
    {
        var active = _active;
        if (active == null || active._attachFrame != Time.frameCount || !active.TryGetRace(out var race)) return true;
        __result = race;
        return false;
    }

    // The spawn point, a stand-in takeover or a load can still apply another race, so keep checking.
    internal void Update()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId || !Enabled || Time.unscaledTime < _nextCheck) return;
        _nextCheck = Time.unscaledTime + 1;
        var system = LocalPlayer.RaceSystem;
        if (!GameRuntime.State.IsInWorld || SaveGameManager.LoadInProgress()
            || system == null || !system || !system.IsLocalPlayer())
        {
            _firstRace = null;
            return;
        }
        var current = system.CurrentRace;
        lock (_sync)
        {
            if (_look == null)
            {
                KeepFirstLook(current);
                return;
            }
        }
        if (TryGetRace(out var race) && race != current)
            system.ApplyRace(race);
    }

    // The host sends a returning player's look in a spawn event after the join's random
    // pick, so only keep a look that has held for a while.
    private void KeepFirstLook(PlayerRace.Race current)
    {
        if (!_readable) return;
        if (_firstRace != current)
        {
            _firstRace = current;
            _keepAt = Time.unscaledTime + SettleSeconds;
            return;
        }
        if (Time.unscaledTime < _keepAt) return;
        if (_store.TrySave(((int)current + 1).ToString(CultureInfo.InvariantCulture), _bodySaved ? _body : null, _custom, out var saved))
        {
            _look = ((int)current + 1).ToString(CultureInfo.InvariantCulture);
            _status = "Kept the look you joined with.";
        }
        else
        {
            _keepAt = Time.unscaledTime + 30;
            _status = saved;
        }
    }

    private bool TryGetRace(out PlayerRace.Race race)
    {
        race = default;
        string? look;
        lock (_sync) look = _look;
        if (look == null || look == ClientCharacterStore.Random) return false;
        var index = look[0] - '1';
        if (index >= PlayerRace.RaceCount) return false;
        race = (PlayerRace.Race)index;
        return true;
    }

    public MenuSettingsSnapshot GetSettings()
    {
        lock (_sync)
        {
            var options = new List<string> { RandomOption };
            for (var look = 1; look <= LookCount; look++) options.Add("Look " + look.ToString(CultureInfo.InvariantCulture));
            var value = _look == ClientCharacterStore.Random ? RandomOption : _look == null ? FirstOption : options[_look[0] - '0'];
            if (_look == null) options.Insert(0, FirstOption);
            var editable = _enabled && _store.CanSave;
            var rows = new List<MenuSettingSnapshot> {
                new("Character", LookKey, "Look", ConfigValueKinds.Choice, value,
                    _custom ? "The game's own face, hair and skin, shown to players who don't see your Modstoke body."
                        : "The game's own face, hair and skin. Everyone in the world sees the same look.", editable, options)
            };
            if (Plugin.Characters.PackageInstalled) AddBodyRows(rows, editable);
            return new MenuSettingsSnapshot(_scope, rows);
        }
    }

    public bool TrySetSetting(Guid scope, string key, string value, CommandOrigin origin, out CommandResultSnapshot result)
    {
        lock (_sync)
        {
            // Other mods may call from any thread; the store and Unity types stay on the game thread.
            if (!_enabled || scope != _scope || Environment.CurrentManagedThreadId != _ownerThreadId)
            {
                result = CommandResultSnapshot.Failure("This setting is no longer available.");
                return false;
            }
            if (key != LookKey) return TrySetBody(key, value, out result);
            if (value == FirstOption && _look == null)
            {
                result = CommandResultSnapshot.Success(_status);
                return true;
            }
            var look = value == RandomOption ? ClientCharacterStore.Random
                : value.StartsWith("Look ", StringComparison.Ordinal) ? value[5..] : "";
            if (!ClientCharacterStore.IsLook(look))
            {
                result = CommandResultSnapshot.Failure("Unknown look.");
                return false;
            }
            if (!_store.TrySave(look, _bodySaved ? _body : null, _custom, out var status))
            {
                result = CommandResultSnapshot.Failure(status);
                return false;
            }
            _look = look;
            _readable = true;
            _status = look == ClientCharacterStore.Random ? "The game picks a new look each time you join."
                : "Your look applies now and every time you join.";
            _nextCheck = 0;
            result = CommandResultSnapshot.Success(_status);
            return true;
        }
    }

    public IReadOnlyList<CommandSnapshot> GetCommands() => new[]
    {
        new CommandSnapshot(CustomiseCommand, "Customise character",
            "Open the character creator with a preview of your character.",
            !Plugin.Characters.PackageInstalled ? "Needs the Modstoke character package."
                : GameRuntime.State.IsInWorld ? "" : "Available in a world.",
            Plugin.Characters.PackageInstalled && GameRuntime.State.IsInWorld, false)
    };

    public bool TryInvokeCommand(string commandId, out CommandResultSnapshot result)
    {
        if (commandId != CustomiseCommand || Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            result = CommandResultSnapshot.Failure("Unknown command.");
            return false;
        }
        var opened = Plugin.Creator.TryOpen(this, ScreenHostOrigin.ModsMenu, out var status);
        result = opened ? CommandResultSnapshot.Success(status) : CommandResultSnapshot.Failure(status);
        return opened;
    }

    public MenuContentSnapshot CreateContent()
    {
        lock (_sync)
            return new MenuContentSnapshot(DisplayName, "by OpenACAI", new[] {
                new MenuSectionSnapshot("Your character", new[] {
                    new MenuRowSnapshot("Status", _status, "", ""),
                    new MenuRowSnapshot("Shared with", _custom
                        ? "Your body: players with the character package, when the host runs Modstoke Client. Your game look: everyone else."
                        : "Everyone in the world, including players without Modstoke", "", ""),
                    new MenuRowSnapshot("Clothing", "Saved with your character by the game", "", "")
                })
            });
    }

    private void AddBodyRows(List<MenuSettingSnapshot> rows, bool editable)
    {
        const string section = "Modstoke body";
        rows.Add(new(section, "custom", "Body", ConfigValueKinds.Choice, _custom ? "Modstoke body" : "Game model",
            "Players with the Modstoke character package see your custom body when the host runs Modstoke Client; everyone else sees your game look.",
            editable, new[] { "Game model", "Modstoke body" }));
        if (!_custom) return;
        rows.Add(new(section, "sex", "Body type", ConfigValueKinds.Choice, _body.Sex.ToString(), "", editable,
            new[] { nameof(CharacterSex.Male), nameof(CharacterSex.Female) }));
        rows.Add(Choice(section, "skin", "Skin tone", SkinTones, _body.Skin, editable));
        rows.Add(Choice(section, "eyes", "Eye colour", EyeColors, _body.Eyes, editable));
        var underwear = Choice(section, "underwear", "Underwear", UnderwearColors, _body.Underwear, editable);
        if (Plugin.Characters.AdultInstalled && Plugin.Characters.AdultOptIn)
            underwear = new MenuSettingSnapshot(section, "underwear", "Underwear", ConfigValueKinds.Choice,
                _body.NoUnderwear ? NoUnderwearOption : underwear.Value,
                "Without underwear you appear unclothed only to players who turned on adult content; everyone else sees underwear.",
                editable, new List<string>(underwear.Options) { NoUnderwearOption });
        rows.Add(underwear);
        var sizes = new List<string> { TextureAuto, "2K" };
        if (Plugin.Characters.HdInstalled || Plugin.Characters.UhdInstalled) sizes.Add("4K");
        if (Plugin.Characters.UhdInstalled) sizes.Add("8K");
        var setting = Plugin.Characters.TextureSetting switch { 2048 => "2K", 4096 => "4K", 8192 => "8K", _ => TextureAuto };
        rows.Add(new(section, "textures", "Texture detail", ConfigValueKinds.Choice, sizes.Contains(setting) ? setting : TextureAuto,
            "4K needs the Modstoke Characters HD pack and 8K the UHD pack. Auto uses the best installed pack your graphics memory allows, now "
            + Plugin.Characters.ResolvedTier / 1024 + "K.", editable, sizes));
        if (Plugin.Characters.AdultInstalled)
            rows.Add(new("Adult content", "adult", "Adult content", ConfigValueKinds.Choice,
                Plugin.Characters.AdultOptIn ? AdultOnOption : "Off",
                "Shows parts from the Modstoke.Characters.Adult package. Turn this on only if you are 18 or older and adult content is legal where you live. It stays on this PC.",
                editable, new[] { "Off", AdultOnOption }));
        foreach (var name in _body.ShapeNames)
            rows.Add(new(section, "shape:" + name, name.Replace('_', ' '), ConfigValueKinds.Number,
                _body.Shape(name).ToString("0.##", CultureInfo.InvariantCulture), "", editable, null, 0, 1, 0.05));
    }

    private static MenuSettingSnapshot Choice(string section, string key, string label, (string Name, Color32 Color)[] presets,
        Color32 current, bool editable)
    {
        var names = Array.ConvertAll(presets, preset => preset.Name);
        var value = Array.Find(presets, preset => Same(preset.Color, current)).Name ?? names[0];
        return new MenuSettingSnapshot(section, key, label, ConfigValueKinds.Choice, value, "", editable, names);
    }

    private static bool Same(Color32 a, Color32 b) => a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;

    private bool TrySetBody(string key, string value, out CommandResultSnapshot result)
    {
        var body = _body;
        var custom = _custom;
        switch (key)
        {
            case "custom": custom = value == "Modstoke body"; break;
            case "sex" when Enum.TryParse<CharacterSex>(value, out var sex): body = body with { Sex = sex }; break;
            case "skin" when TryPreset(SkinTones, value, out var skin): body = body with { Skin = skin }; break;
            case "eyes" when TryPreset(EyeColors, value, out var eyes): body = body with { Eyes = eyes }; break;
            case "underwear" when value == NoUnderwearOption: body = body with { NoUnderwear = true }; break;
            case "underwear" when TryPreset(UnderwearColors, value, out var underwear): body = body with { Underwear = underwear, NoUnderwear = false }; break;
            case "textures":
                var pixels = value switch { "2K" => 2048, "4K" => 4096, "8K" => 8192, _ => 0 };
                if (!_store.TrySaveAdult(Plugin.Characters.AdultOptIn, pixels, out var stored))
                {
                    result = CommandResultSnapshot.Failure(stored);
                    return false;
                }
                Plugin.Characters.TextureSetting = pixels;
                result = CommandResultSnapshot.Success("Texture detail: " + Plugin.Characters.ResolvedTier / 1024 + "K.");
                return true;
            case "adult":
                var adult = value == AdultOnOption;
                if (!_store.TrySaveAdult(adult, Plugin.Characters.TextureSetting, out var saved))
                {
                    result = CommandResultSnapshot.Failure(saved);
                    return false;
                }
                Plugin.Characters.AdultOptIn = adult;
                result = CommandResultSnapshot.Success(adult ? "Adult content is on for this PC." : "Adult content is off.");
                return true;
            default:
                var shape = key.StartsWith("shape:", StringComparison.Ordinal) ? key[6..] : "";
                if (Array.IndexOf(CharacterAppearance.MaleShapes, shape) < 0 && Array.IndexOf(CharacterAppearance.FemaleShapes, shape) < 0
                    || !float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
                {
                    result = CommandResultSnapshot.Failure("Unknown setting.");
                    return false;
                }
                body = body with { Shapes = new Dictionary<string, float>(body.Shapes, StringComparer.Ordinal) { [shape] = Math.Clamp(amount, 0, 1) } };
                break;
        }
        return Commit(body, custom, out result);
    }

    // The saved body and whether it is in use, for the creator to start from.
    internal (CharacterAppearance Body, bool Custom) Saved
    {
        get { lock (_sync) return (_body, _custom); }
    }

    // Shows a look on the live player without saving it; null returns to the saved look.
    internal void Preview(CharacterAppearance? body)
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId) return;
        lock (_sync) Plugin.Characters.Appearance = body ?? (_custom ? _body : null);
    }

    internal bool TrySave(CharacterAppearance body, out string status)
    {
        lock (_sync)
        {
            var saved = Commit(body, true, out var result);
            status = result.Detail;
            return saved;
        }
    }

    private bool Commit(CharacterAppearance body, bool custom, out CommandResultSnapshot result)
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            result = CommandResultSnapshot.Failure("Character changes are saved on the game thread.");
            return false;
        }
        // Before the first look is kept, a body choice keeps the look the player has now, if any.
        var look = _look;
        var system = LocalPlayer.RaceSystem;
        if (look == null && GameRuntime.State.IsInWorld && system != null && system && system.IsLocalPlayer())
            look = ((int)system.CurrentRace + 1).ToString(CultureInfo.InvariantCulture);
        if (!_store.TrySave(look, body, custom, out var status))
        {
            result = CommandResultSnapshot.Failure(status);
            return false;
        }
        _look = look;
        _body = body;
        _custom = custom;
        _bodySaved = true;
        _readable = true;
        Publish();
        result = CommandResultSnapshot.Success("Saved.");
        return true;
    }

    private static bool TryPreset((string Name, Color32 Color)[] presets, string name, out Color32 color)
    {
        foreach (var preset in presets)
            if (preset.Name == name) { color = preset.Color; return true; }
        color = default;
        return false;
    }

    private void Publish() => Plugin.Characters.Appearance = _custom ? _body : null;

    internal void Deactivate()
    {
        lock (_sync) _enabled = false;
        if (ReferenceEquals(_active, this)) _active = null;
        _patches.UnpatchSelf();
    }
}
