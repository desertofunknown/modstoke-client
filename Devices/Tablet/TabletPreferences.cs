// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace Modstoke.Client;

internal enum ClientTabletTheme { Field, Light, Amber }
internal enum ClientTabletTone { Pulse, Chime, Radio }
internal enum ClientTabletMode { Patrol, Camp, Silent }
internal enum ClientWatchKey { F3, F9, F11, None }
internal enum ClientTabletTextSize { Normal, Large }

internal sealed record ClientTabletPreferencesSnapshot(
    ClientTabletTheme Theme, float Brightness, bool Notifications, float Volume,
    ClientTabletTone Tone, bool DeveloperOptions, ClientTabletMode Mode = ClientTabletMode.Patrol,
    ClientWatchHudMode HudMode = ClientWatchHudMode.Standard, ClientWatchKey WatchKey = ClientWatchKey.F3,
    bool ReducedMotion = false, ClientTabletTextSize TextSize = ClientTabletTextSize.Normal,
    bool ExpeditionAlerts = true, bool TribeAlerts = true, bool DirectAlerts = true,
    ClientWatchTheme WatchTheme = ClientWatchTheme.Field, bool WatchNight = false, bool WatchPrivate = false)
{
    internal float EffectiveBrightness => Mode == ClientTabletMode.Silent ? Math.Min(Brightness, .55f) : Brightness;
    internal float EffectiveVolume => Mode == ClientTabletMode.Silent ? 0f : Volume;
}

internal sealed class ClientTabletPreferences
{
    private const int MaximumBytes = 4096;
    private readonly object _gate = new();
    private readonly string? _path;
    private ClientTabletPreferencesSnapshot _snapshot = new(ClientTabletTheme.Field, 1f, true, .35f, ClientTabletTone.Pulse, false);
    private bool _loaded;
    private bool _saveBlocked;
    private int _pendingChanges;
    private string _status = "Tablet preferences have not been loaded.";
    private Dictionary<string, JsonElement> _unknownSettings = new(StringComparer.Ordinal);

    internal ClientTabletPreferences(string? gameRoot)
    {
        if (!string.IsNullOrWhiteSpace(gameRoot) && Path.IsPathFullyQualified(gameRoot))
            _path = Path.Combine(gameRoot, "BepInEx", "config", "modstoke.tablet.json");
    }

    internal ClientTabletPreferencesSnapshot Snapshot => Volatile.Read(ref _snapshot);
    internal string Status
    {
        get => Volatile.Read(ref _pendingChanges) > 0 ? "Tablet preference queued for saving." : Volatile.Read(ref _status);
        private set => Volatile.Write(ref _status, value);
    }

    internal bool ValidateChange(string key, string value, out string status)
    {
        if (key is null || key.Length > 32 || value is null || value.Length > 64)
        {
            status = "Invalid tablet preference value.";
            return false;
        }
        return TryChange(Snapshot, key, value, out _, out status);
    }

    internal void PreferenceQueued() => Interlocked.Increment(ref _pendingChanges);

    internal void PreferenceCompleted() => Interlocked.Decrement(ref _pendingChanges);

    internal void PreferenceAbandoned()
    {
        Status = "The device operating system stopped before this preference could be saved.";
        PreferenceCompleted();
    }

    internal void Load()
    {
        lock (_gate)
        {
            _loaded = true;
            _saveBlocked = false;
            if (_path == null)
            {
                _saveBlocked = true;
                Status = "The SDK game directory is unavailable; tablet preferences cannot be saved.";
                return;
            }
            try
            {
                using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var bytes = new byte[MaximumBytes + 1];
                var count = 0;
                while (count < bytes.Length)
                {
                    var read = stream.Read(bytes, count, bytes.Length - count);
                    if (read == 0) break;
                    count += read;
                }
                if (count > MaximumBytes) throw new FormatException("The file exceeds 4096 bytes.");
                using var document = JsonDocument.Parse(bytes.AsMemory(0, count));
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) throw new FormatException("Expected a settings object.");
                var keys = new HashSet<string>(StringComparer.Ordinal);
                var next = Snapshot with
                {
                    Mode = ClientTabletMode.Patrol, HudMode = ClientWatchHudMode.Standard, WatchKey = ClientWatchKey.F3,
                    ReducedMotion = false, TextSize = ClientTabletTextSize.Normal,
                    ExpeditionAlerts = true, TribeAlerts = true, DirectAlerts = true,
                    WatchTheme = ClientWatchTheme.Field, WatchNight = false, WatchPrivate = false
                };
                var unknownSettings = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var property in root.EnumerateObject())
                {
                    if (!keys.Add(property.Name)) throw new FormatException("Duplicate setting: " + property.Name);
                    var value = property.Value;
                    if (property.Name is not ("theme" or "tone" or "brightness" or "volume" or "notifications" or "developer" or "mode"
                        or "hud" or "watch-key" or "reduced-motion" or "text-size" or "expedition-alerts" or "tribe-alerts" or "direct-alerts"
                        or "watch-theme" or "watch-night" or "watch-private"))
                    {
                        unknownSettings.Add(property.Name, value.Clone());
                        continue;
                    }
                    var correctType = property.Name switch
                    {
                        "theme" or "tone" or "mode" or "hud" or "watch-key" or "text-size" or "watch-theme" => value.ValueKind == JsonValueKind.String,
                        "brightness" or "volume" => value.ValueKind == JsonValueKind.Number,
                        "notifications" or "developer" or "reduced-motion" or "expedition-alerts" or "tribe-alerts" or "direct-alerts"
                            or "watch-night" or "watch-private"
                            => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                        _ => false
                    };
                    if (!correctType || !TryChange(next, property.Name, value.ToString(), out next, out _))
                        throw new FormatException("Invalid setting: " + property.Name);
                }
                foreach (var required in new[] { "theme", "brightness", "notifications", "volume", "tone", "developer" })
                    if (!keys.Contains(required)) throw new FormatException("One or more tablet settings are missing.");
                _unknownSettings = unknownSettings;
                Volatile.Write(ref _snapshot, next);
                Status = "Loaded local tablet preferences.";
            }
            catch (FileNotFoundException) { Status = "No saved tablet preferences; using current preferences."; }
            catch (DirectoryNotFoundException) { Status = "No saved tablet preferences; using current preferences."; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or FormatException)
            {
                _saveBlocked = true;
                Status = "Tablet preferences could not be loaded. The existing file is preserved; correct or move BepInEx/config/modstoke.tablet.json and reload the client. " + error.Message;
            }
        }
    }

    internal bool Change(string key, string value, out string status)
    {
        lock (_gate)
        {
            if (!_loaded) Load();
            if (_saveBlocked || _path == null) { status = Volatile.Read(ref _status); return false; }
            if (!TryChange(Snapshot, key, value, out var next, out status)) return false;
            if (next == Snapshot) { status = Status = "Tablet preference is already set."; return true; }
            var temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var settings = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["theme"] = next.Theme.ToString(), ["brightness"] = next.Brightness,
                    ["notifications"] = next.Notifications, ["volume"] = next.Volume,
                    ["tone"] = next.Tone.ToString(), ["developer"] = next.DeveloperOptions,
                    ["mode"] = next.Mode.ToString(), ["hud"] = next.HudMode.ToString(), ["watch-key"] = next.WatchKey.ToString(),
                    ["reduced-motion"] = next.ReducedMotion, ["text-size"] = next.TextSize.ToString(),
                    ["expedition-alerts"] = next.ExpeditionAlerts, ["tribe-alerts"] = next.TribeAlerts, ["direct-alerts"] = next.DirectAlerts,
                    ["watch-theme"] = next.WatchTheme.ToString(), ["watch-night"] = next.WatchNight, ["watch-private"] = next.WatchPrivate
                };
                foreach (var setting in _unknownSettings) settings.Add(setting.Key, setting.Value);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(settings);
                if (bytes.Length > MaximumBytes) throw new FormatException("The saved settings would exceed 4096 bytes.");
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                if (File.Exists(_path)) File.Replace(temporaryPath, _path, null);
                else File.Move(temporaryPath, _path);
                Volatile.Write(ref _snapshot, next);
                status = Status = "Saved local tablet preference.";
                return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException)
            {
                status = Status = "Tablet preference was not changed or saved: " + error.Message;
                return false;
            }
            finally
            {
                try { File.Delete(temporaryPath); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static bool TryChange(ClientTabletPreferencesSnapshot current, string key, string value,
        out ClientTabletPreferencesSnapshot next, out string status)
    {
        next = current;
        status = "Invalid tablet preference value.";
        value = value?.Trim() ?? string.Empty;
        switch (key)
        {
            case "watch-theme":
                if (!TryNamedEnum(value, out ClientWatchTheme watchTheme)) { status = "Choose Field, Amber, or Ice."; return false; }
                next = current with { WatchTheme = watchTheme }; break;
            case "watch-night":
                if (!bool.TryParse(value, out var watchNight)) { status = "Watch night mode must be true or false."; return false; }
                next = current with { WatchNight = watchNight }; break;
            case "watch-private":
                if (!bool.TryParse(value, out var watchPrivate)) { status = "Watch privacy must be true or false."; return false; }
                next = current with { WatchPrivate = watchPrivate }; break;
            case "hud":
                if (!TryNamedEnum(value, out ClientWatchHudMode hud)) { status = "Choose Standard, Minimal, or Immersive."; return false; }
                next = current with { HudMode = hud }; break;
            case "watch-key":
                if (!TryNamedEnum(value, out ClientWatchKey watchKey)) { status = "Choose F3, F9, F11, or None."; return false; }
                next = current with { WatchKey = watchKey }; break;
            case "text-size":
                if (!TryNamedEnum(value, out ClientTabletTextSize textSize)) { status = "Choose Normal or Large."; return false; }
                next = current with { TextSize = textSize }; break;
            case "reduced-motion":
                if (!bool.TryParse(value, out var reducedMotion)) { status = "Reduced motion must be true or false."; return false; }
                next = current with { ReducedMotion = reducedMotion }; break;
            case "expedition-alerts":
            case "tribe-alerts":
            case "direct-alerts":
                if (!bool.TryParse(value, out var channelAlerts)) { status = "Channel alerts must be true or false."; return false; }
                next = key switch
                {
                    "expedition-alerts" => current with { ExpeditionAlerts = channelAlerts },
                    "tribe-alerts" => current with { TribeAlerts = channelAlerts },
                    _ => current with { DirectAlerts = channelAlerts }
                };
                break;
            case "mode":
                if (!TryNamedEnum(value, out ClientTabletMode mode)) { status = "Choose Patrol, Camp, or Silent."; return false; }
                next = current with { Mode = mode }; break;
            case "theme":
                if (!TryNamedEnum(value, out ClientTabletTheme theme)) { status = "Choose Field, Light, or Amber."; return false; }
                next = current with { Theme = theme }; break;
            case "brightness":
                if (!TryNumber(value, .35f, 1f, out var brightness)) { status = "Brightness must be between 0.35 and 1."; return false; }
                next = current with { Brightness = brightness }; break;
            case "notifications":
                if (!bool.TryParse(value, out var notifications)) { status = "Notifications must be true or false."; return false; }
                next = current with { Notifications = notifications }; break;
            case "volume":
                if (!TryNumber(value, 0f, 1f, out var volume)) { status = "Volume must be between 0 and 1."; return false; }
                next = current with { Volume = volume }; break;
            case "tone":
                if (!TryNamedEnum(value, out ClientTabletTone tone)) { status = "Choose Pulse, Chime, or Radio."; return false; }
                next = current with { Tone = tone }; break;
            case "developer":
                if (!bool.TryParse(value, out var developer)) { status = "Developer information must be true or false."; return false; }
                next = current with { DeveloperOptions = developer }; break;
            default: status = "Unknown tablet preference."; return false;
        }
        status = string.Empty;
        return true;
    }

    private static bool TryNumber(string value, float minimum, float maximum, out float number) =>
        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number)
        && float.IsFinite(number) && number >= minimum && number <= maximum;

    private static bool TryNamedEnum<T>(string value, out T parsed) where T : struct, Enum =>
        Enum.TryParse(value, true, out parsed) && Enum.IsDefined(parsed)
        && string.Equals(Enum.GetName(parsed), value, StringComparison.OrdinalIgnoreCase);
}
