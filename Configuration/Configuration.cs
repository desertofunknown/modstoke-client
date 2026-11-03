// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.IO;
using System.Text;

namespace Modstoke.Client;

internal sealed class ClientConfiguration
{
    private const int MaximumBytes = 1024;
    private const string Key = "EnableManagedAdmission";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly string? _path;

    internal ClientConfiguration(string? gameRoot)
    {
        if (!string.IsNullOrWhiteSpace(gameRoot) && Path.IsPathFullyQualified(gameRoot))
            _path = Path.Combine(gameRoot, "BepInEx", "config", "modstoke.client.cfg");
    }

    internal bool TryLoad(out bool enabled, out string status)
    {
        enabled = false;
        if (_path == null)
        {
            status = "The SDK game directory is unavailable; configuration was not loaded.";
            return false;
        }
        try
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var bytes = new byte[MaximumBytes + 1];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = stream.Read(bytes, count, bytes.Length - count);
                if (read == 0)
                    break;
                count += read;
            }
            if (count > MaximumBytes)
                throw new FormatException("The configuration exceeds 1024 bytes.");
            var content = Utf8.GetString(bytes, 0, count).TrimStart('\uFEFF');
            var found = false;
            foreach (var rawLine in content.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                    continue;
                var separator = line.IndexOf('=');
                if (found || separator < 0 || !string.Equals(line[..separator].Trim(), Key, StringComparison.Ordinal)
                    || !bool.TryParse(line[(separator + 1)..].Trim(), out var parsed))
                    throw new FormatException("Expected exactly one EnableManagedAdmission = true or false setting.");
                enabled = parsed;
                found = true;
            }
            if (!found)
                throw new FormatException("EnableManagedAdmission is missing.");
            status = "Loaded BepInEx/config/modstoke.client.cfg.";
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            status = "No configuration file; participation defaults to disabled.";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or DecoderFallbackException)
        {
            enabled = false;
            status = "Configuration could not be loaded: " + ex.Message;
            return false;
        }
    }

    internal bool TrySave(bool enabled, out string status)
    {
        if (_path == null)
        {
            status = "The SDK game directory is unavailable; configuration was not saved.";
            return false;
        }
        var temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = Utf8.GetBytes(Key + " = " + (enabled ? "true" : "false") + "\n");
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(_path))
                File.Replace(temporaryPath, _path, null);
            else
                File.Move(temporaryPath, _path);
            status = "Saved BepInEx/config/modstoke.client.cfg.";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            status = "Configuration was not changed: " + ex.Message;
            return false;
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
