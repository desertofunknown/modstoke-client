// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Modstoke.Client;

// One look for this Windows user, kept beside the other Client settings.
internal sealed class ClientCharacterStore
{
    internal const string Random = "random";
    // Version 2 can save a body before the first look has been kept.
    private const string First = "first";
    private const int MaximumBytes = 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly string? _path;

    internal ClientCharacterStore(string? gameRoot)
    {
        if (!string.IsNullOrWhiteSpace(gameRoot) && Path.IsPathFullyQualified(gameRoot))
            _path = Path.Combine(gameRoot, "BepInEx", "config", "Modstoke", "character.json");
    }

    internal bool CanSave => _path != null;

    // An absent file means the player has not chosen yet; the first look they get is kept.
    internal bool TryLoad(out string? look, out CharacterAppearance? body, out bool custom, out string status)
    {
        look = null;
        body = null;
        custom = false;
        if (_path == null)
        {
            status = "The game directory is unavailable, so your look can't be saved.";
            return false;
        }
        try
        {
            if (!File.Exists(_path))
            {
                status = "Your first look in a world will be kept.";
                return true;
            }
            var bytes = File.ReadAllBytes(_path);
            if (bytes.Length > MaximumBytes)
                throw new FormatException("The character file exceeds 1024 bytes.");
            using var document = JsonDocument.Parse(Utf8.GetString(bytes).TrimStart('﻿'));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var version)
                || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number is not (1 or 2)
                || !root.TryGetProperty("look", out var value) || value.ValueKind != JsonValueKind.String
                || !IsLook(value.GetString()) && !(number == 2 && value.GetString() == First))
                throw new FormatException("Expected version 1 or 2 and a look of \"random\" or 1 to 8.");
            look = value.GetString() == First ? null : value.GetString();
            // Version 2 adds the custom body; an unreadable body falls back to the game's model.
            var buffer = new byte[96];
            if (number == 2 && root.TryGetProperty("body", out var encoded) && encoded.ValueKind == JsonValueKind.String
                && Convert.TryFromBase64String(encoded.GetString()!, buffer, out var written)
                && CharacterAppearance.TryDecode(buffer.AsSpan(0, written), out var appearance))
            {
                body = appearance;
                custom = !root.TryGetProperty("custom", out var flag) || flag.ValueKind != JsonValueKind.False;
            }
            status = "Loaded your saved look.";
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
            or FormatException or ArgumentException)
        {
            status = "Your saved look could not be read (" + error.Message
                + "). Choosing a look replaces BepInEx\\config\\Modstoke\\character.json.";
            return false;
        }
    }

    internal bool TrySave(string? look, CharacterAppearance? body, bool custom, out string status)
    {
        if (_path == null || look == null && body == null || look != null && !IsLook(look))
        {
            status = _path == null ? "The game directory is unavailable, so your look can't be saved." : "Unknown look.";
            return false;
        }
        var temporary = _path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var content = body == null
                ? "{\n  \"version\": 1,\n  \"look\": \"" + look + "\"\n}\n"
                : "{\n  \"version\": 2,\n  \"look\": \"" + (look ?? First) + "\",\n  \"custom\": " + (custom ? "true" : "false")
                    + ",\n  \"body\": \"" + Convert.ToBase64String(body.Encode()) + "\"\n}\n";
            File.WriteAllText(temporary, content, Utf8);
            if (File.Exists(_path)) File.Replace(temporary, _path, null);
            else File.Move(temporary, _path);
            status = "Saved.";
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temporary); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            status = "Your look could not be saved: " + error.Message;
            return false;
        }
    }

    // The adult setting is this viewer's own and lives apart from the shared look.
    internal bool TryLoadAdult(out bool adult, out int textures)
    {
        adult = false;
        textures = 0;
        if (_path == null) return false;
        try
        {
            var path = Path.Combine(Path.GetDirectoryName(_path)!, "content.json");
            if (!File.Exists(path)) return true;
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length > MaximumBytes) return false;
            using var document = JsonDocument.Parse(Utf8.GetString(bytes).TrimStart('\uFEFF'));
            adult = document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("adult", out var value) && value.ValueKind == JsonValueKind.True;
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("textures", out var size) && size.ValueKind == JsonValueKind.Number
                && size.TryGetInt32(out var pixels) && pixels is 0 or 2048 or 4096 or 8192)
                textures = pixels;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return false;
        }
    }

    internal bool TrySaveAdult(bool adult, int textures, out string status)
    {
        if (_path == null)
        {
            status = "The game directory is unavailable, so this setting can't be saved.";
            return false;
        }
        var path = Path.Combine(Path.GetDirectoryName(_path)!, "content.json");
        var temporary = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temporary, "{\n  \"version\": 1,\n  \"adult\": " + (adult ? "true" : "false")
                + ",\n  \"textures\": " + textures + "\n}\n", Utf8);
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
            status = "Saved.";
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temporary); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            status = "The setting could not be saved: " + error.Message;
            return false;
        }
    }

    internal static bool IsLook(string? look) =>
        look == Random || look is { Length: 1 } && look[0] is >= '1' and <= '8';
}
