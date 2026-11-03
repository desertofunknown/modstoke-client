// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.Json;

namespace Modstoke.Client;

internal sealed record ClientFieldPoint(float X, float Y, float Z);
internal sealed record ClientFieldPlace(Guid Id, string Name, ClientFieldPoint Position,
    string Note = "", string Category = "Personal");
internal sealed record ClientFieldNote(Guid Id, string Title, string Body, DateTimeOffset UpdatedAt,
    Guid? PlaceId = null);
internal sealed record ClientFieldRoute(Guid Id, string Name, IReadOnlyList<Guid> Places);

internal sealed record ClientFieldProfile
{
    public ClientTerminalApp LastApp { get; init; } = ClientTerminalApp.Home;
    public IReadOnlyDictionary<string, string> Drafts { get; init; } =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal));
    public IReadOnlyList<ClientFieldPlace> Places { get; init; } = Array.Empty<ClientFieldPlace>();
    public IReadOnlyList<ClientFieldNote> Notes { get; init; } = Array.Empty<ClientFieldNote>();
    public IReadOnlyList<ClientFieldRoute> Routes { get; init; } = Array.Empty<ClientFieldRoute>();
    public IReadOnlyList<ClientFieldPoint> Trail { get; init; } = Array.Empty<ClientFieldPoint>();
    public Guid? DestinationId { get; init; }
    public bool RecordTrail { get; init; }
    public string Mission { get; init; } = "";
    public IReadOnlyList<ClientTerminalApp> Favorites { get; init; } = Array.Empty<ClientTerminalApp>();
}

// Called by the managed OS worker. No game objects or server-owned history belong in this file.
internal sealed class ClientFieldProfileStore
{
    private const int MaximumBytes = 256 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        MaxDepth = 12
    };
    private readonly object _gate = new();
    private readonly string? _directory;
    private readonly HashSet<string> _blockedKeys = new(StringComparer.Ordinal);
    private string? _activeKey;
    private string _status = "Field profile has not been loaded.";

    internal ClientFieldProfileStore(string gameRoot)
    {
        if (!string.IsNullOrWhiteSpace(gameRoot) && Path.IsPathFullyQualified(gameRoot))
            _directory = Path.Combine(gameRoot, "BepInEx", "config", "Modstoke", "field-profiles");
    }

    internal string Status { get { lock (_gate) return _status; } }
    internal bool CanSave
    {
        get { lock (_gate) return _directory != null && _activeKey != null && !_blockedKeys.Contains(_activeKey); }
    }

    internal ClientFieldProfile Load(string profileKey)
    {
        lock (_gate)
        {
            if (!SelectKey(profileKey)) return new();
            try
            {
                var profile = ReadFile(ProfilePath(profileKey));
                _status = _blockedKeys.Contains(profileKey)
                    ? "Field profile loaded, but saving remains blocked for this session after an earlier file error."
                    : profile == null ? "No saved field profile; using a new local profile." : "Loaded local field profile.";
                return profile ?? new();
            }
            catch (Exception error) when (IsFileError(error))
            {
                Block(profileKey, error);
                return new();
            }
        }
    }

    internal bool Save(string profileKey, ClientFieldProfile profile)
    {
        lock (_gate)
        {
            if (!SelectKey(profileKey)) return false;
            if (_blockedKeys.Contains(profileKey))
            {
                _status = "Field profile saving is blocked; the existing file is preserved. Correct or move the file and restart the client.";
                return false;
            }

            byte[] bytes;
            try
            {
                // Freeze collections after validating their bounds; callers cannot change the serialized snapshot.
                var snapshot = ValidateAndFreeze(profile);
                using var buffer = new BoundedBuffer();
                JsonSerializer.Serialize(buffer, new { version = 1, profile = snapshot }, JsonOptions);
                bytes = buffer.ToArray();
            }
            catch (Exception error) when (error is FormatException or JsonException or ArgumentException)
            {
                _status = "Field profile was not saved: " + error.Message;
                return false;
            }

            var path = ProfilePath(profileKey);
            var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var ownsTemporary = false;
            try
            {
                Directory.CreateDirectory(_directory!);
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    ownsTemporary = true;
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }

                // Recheck on every save, including saves made without a preceding Load.
                bool exists;
                try { exists = ReadFile(path) != null; }
                catch (Exception error) when (IsFileError(error))
                {
                    Block(profileKey, error);
                    return false;
                }
                if (exists) File.Replace(temporaryPath, path, null);
                else File.Move(temporaryPath, path);
                ownsTemporary = false;
                _status = "Saved local field profile.";
                return true;
            }
            catch (Exception error) when (IsFileError(error))
            {
                _status = "Field profile was not saved: " + error.Message;
                return false;
            }
            finally
            {
                if (ownsTemporary)
                {
                    try { File.Delete(temporaryPath); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                }
            }
        }
    }

    private bool SelectKey(string key)
    {
        if (key == null || key.Length != 64)
        {
            _activeKey = null;
            _status = "A negotiated field profile identity is required before local storage is available.";
            return false;
        }
        foreach (var character in key)
            if (!(character is >= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                _activeKey = null;
                _status = "The field profile identity is invalid.";
                return false;
            }
        _activeKey = key;
        if (_directory != null) return true;
        _status = "The game directory is unavailable; field profiles cannot be saved.";
        return false;
    }

    private string ProfilePath(string key) => Path.Combine(_directory!, key + ".json");

    private void Block(string key, Exception error)
    {
        _blockedKeys.Add(key);
        _status = "Field profile could not be read. The existing file is preserved and saving is blocked. " + error.Message;
    }

    private static bool IsFileError(Exception error) =>
        error is IOException or UnauthorizedAccessException or JsonException or FormatException or ArgumentException;

    private static ClientFieldProfile? ReadFile(string path)
    {
        FileStream stream;
        try { stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        using (stream)
        {
            Require(stream.Length <= MaximumBytes, "The field profile exceeds 256 KiB.");
            var bytes = new byte[MaximumBytes + 1];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = stream.Read(bytes, count, bytes.Length - count);
                if (read == 0) break;
                count += read;
            }
            Require(count <= MaximumBytes, "The field profile exceeds 256 KiB.");
            using var document = JsonDocument.Parse(bytes.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 12 });
            var envelope = document.RootElement;
            Object(envelope, "version", "profile");
            var versionValue = envelope.GetProperty("version");
            Require(versionValue.ValueKind == JsonValueKind.Number && versionValue.TryGetInt32(out var version) && version == 1,
                "This field profile version is unsupported.");
            return ReadProfile(envelope.GetProperty("profile"));
        }
    }

    private static ClientFieldProfile ReadProfile(JsonElement value)
    {
        Object(value, "lastApp", "drafts", "places", "notes", "routes", "trail", "destinationId", "recordTrail", "mission", "favorites");
        var draftsValue = value.GetProperty("drafts");
        Require(draftsValue.ValueKind == JsonValueKind.Object, "Expected draft fields.");
        var draftCount = 0;
        foreach (var unused in draftsValue.EnumerateObject())
            Require(++draftCount <= 32, "Too many drafts.");
        var drafts = new Dictionary<string, string>(draftCount, StringComparer.Ordinal);
        foreach (var draft in draftsValue.EnumerateObject())
        {
            Text(draft.Name, 96);
            Require(drafts.TryAdd(draft.Name, Text(draft.Value, 256)), "Duplicate draft key.");
        }
        return ValidateAndFreeze(new ClientFieldProfile
        {
            LastApp = App(value.GetProperty("lastApp")),
            Drafts = drafts,
            Places = Array(value.GetProperty("places"), 128, item =>
            {
                Object(item, "id", "name", "position", "note", "category");
                return new ClientFieldPlace(Id(item.GetProperty("id")), Text(item.GetProperty("name"), 64),
                    Point(item.GetProperty("position")), Text(item.GetProperty("note"), 2048), Text(item.GetProperty("category"), 64));
            }),
            Notes = Array(value.GetProperty("notes"), 128, item =>
            {
                Object(item, "id", "title", "body", "updatedAt", "placeId");
                var date = item.GetProperty("updatedAt");
                Require(date.ValueKind == JsonValueKind.String && date.TryGetDateTimeOffset(out _), "Invalid note timestamp.");
                return new ClientFieldNote(Id(item.GetProperty("id")), Text(item.GetProperty("title"), 64),
                    Text(item.GetProperty("body"), 2048), date.GetDateTimeOffset(), OptionalId(item.GetProperty("placeId")));
            }),
            Routes = Array(value.GetProperty("routes"), 32, item =>
            {
                Object(item, "id", "name", "places");
                return new ClientFieldRoute(Id(item.GetProperty("id")), Text(item.GetProperty("name"), 64),
                    Array(item.GetProperty("places"), 128, Id));
            }),
            Trail = Array(value.GetProperty("trail"), 512, Point),
            DestinationId = OptionalId(value.GetProperty("destinationId")),
            RecordTrail = Boolean(value.GetProperty("recordTrail")),
            Mission = Text(value.GetProperty("mission"), 2048),
            Favorites = Array(value.GetProperty("favorites"), 8, App)
        });
    }

    private static ClientFieldProfile ValidateAndFreeze(ClientFieldProfile profile)
    {
        Require(profile != null, "A field profile is required.");
        Require(Enum.IsDefined(profile.LastApp), "Invalid last app.");
        Text(profile.Mission, 2048);
        Require(profile.Drafts != null && profile.Drafts.Count <= 32, "Too many drafts.");
        var drafts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var draft in profile.Drafts)
        {
            Require(drafts.Count < 32, "Too many drafts.");
            Text(draft.Key, 96);
            Text(draft.Value, 256);
            Require(drafts.TryAdd(draft.Key, draft.Value), "Duplicate draft key.");
        }
        var ids = new HashSet<Guid>();
        var placeIds = new HashSet<Guid>();
        var places = Freeze(profile.Places, 128, place =>
        {
            Require(place != null, "Invalid place.");
            Unique(place.Id, ids);
            placeIds.Add(place.Id);
            Text(place.Name, 64); Text(place.Note, 2048); Text(place.Category, 64);
            ValidatePoint(place.Position);
            return place;
        });
        var notes = Freeze(profile.Notes, 128, note =>
        {
            Require(note != null, "Invalid note.");
            Unique(note.Id, ids);
            Text(note.Title, 64); Text(note.Body, 2048);
            Require(note.PlaceId == null || placeIds.Contains(note.PlaceId.Value), "A note references a missing place.");
            return note;
        });
        var routes = Freeze(profile.Routes, 32, route =>
        {
            Require(route != null, "Invalid route.");
            Unique(route.Id, ids);
            Text(route.Name, 64);
            return route with { Places = Freeze(route.Places, 128, id =>
            {
                Require(placeIds.Contains(id), "A route references a missing place.");
                return id;
            }) };
        });
        var trail = Freeze(profile.Trail, 512, point => { ValidatePoint(point); return point; });
        var favoriteApps = new HashSet<ClientTerminalApp>();
        var favorites = Freeze(profile.Favorites, 8, app =>
        {
            Require(Enum.IsDefined(app) && favoriteApps.Add(app), "Invalid or duplicate favorite app.");
            return app;
        });
        Require(profile.DestinationId == null || placeIds.Contains(profile.DestinationId.Value), "The destination references a missing place.");
        return profile with
        {
            Drafts = new ReadOnlyDictionary<string, string>(drafts), Places = places, Notes = notes,
            Routes = routes, Trail = trail, Favorites = favorites
        };
    }

    private static IReadOnlyList<T> Freeze<T>(IReadOnlyList<T> source, int maximum, Func<T, T> validate)
    {
        Require(source != null && source.Count <= maximum, "A field profile collection exceeds its limit.");
        var result = new T[source.Count];
        for (var index = 0; index < result.Length; index++) result[index] = validate(source[index]);
        return System.Array.AsReadOnly(result);
    }

    private static T[] Array<T>(JsonElement value, int maximum, Func<JsonElement, T> read)
    {
        Require(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= maximum, "Invalid or oversized field profile collection.");
        var result = new T[value.GetArrayLength()];
        var index = 0;
        foreach (var item in value.EnumerateArray()) result[index++] = read(item);
        return result;
    }

    private static void Object(JsonElement value, params string[] fields)
    {
        Require(value.ValueKind == JsonValueKind.Object, "Expected a field profile object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            Require(System.Array.IndexOf(fields, property.Name) >= 0 && seen.Add(property.Name), "Unknown or duplicate field profile property.");
        Require(seen.Count == fields.Length, "Missing field profile property.");
    }

    private static string Text(JsonElement value, int maximum)
    {
        Require(value.ValueKind == JsonValueKind.String, "Expected field profile text.");
        var text = value.GetString()!;
        Text(text, maximum);
        return text;
    }

    private static void Text(string text, int maximum) =>
        Require(text != null && text.Length <= maximum, "Field profile text exceeds its limit or is missing.");

    private static Guid Id(JsonElement value)
    {
        Require(value.ValueKind == JsonValueKind.String && value.TryGetGuid(out var id) && id != Guid.Empty, "Invalid field profile ID.");
        return value.GetGuid();
    }

    private static Guid? OptionalId(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : Id(value);

    private static void Unique(Guid id, HashSet<Guid> ids) =>
        Require(id != Guid.Empty && ids.Add(id), "Invalid or duplicate field profile ID.");

    private static bool Boolean(JsonElement value)
    {
        Require(value.ValueKind is JsonValueKind.True or JsonValueKind.False, "Expected a field profile flag.");
        return value.GetBoolean();
    }

    private static ClientTerminalApp App(JsonElement value)
    {
        Require(value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            && Enum.IsDefined((ClientTerminalApp)number), "Invalid field profile app.");
        return (ClientTerminalApp)value.GetInt32();
    }

    private static ClientFieldPoint Point(JsonElement value)
    {
        Object(value, "x", "y", "z");
        return new ClientFieldPoint(Coordinate(value.GetProperty("x")), Coordinate(value.GetProperty("y")), Coordinate(value.GetProperty("z")));
    }

    private static float Coordinate(JsonElement value)
    {
        Require(value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out var number)
            && float.IsFinite(number) && Math.Abs(number) <= 1_000_000f, "Invalid field profile coordinate.");
        return value.GetSingle();
    }

    private static void ValidatePoint(ClientFieldPoint point) =>
        Require(point != null && float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z)
            && Math.Abs(point.X) <= 1_000_000f && Math.Abs(point.Y) <= 1_000_000f && Math.Abs(point.Z) <= 1_000_000f,
            "Invalid field profile coordinate.");

    private static void Require([DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition) throw new FormatException(message);
    }

    private sealed class BoundedBuffer : MemoryStream
    {
        internal BoundedBuffer() : base(MaximumBytes) { }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Require(count <= MaximumBytes - Position, "The saved field profile would exceed 256 KiB.");
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Require(buffer.Length <= MaximumBytes - Position, "The saved field profile would exceed 256 KiB.");
            base.Write(buffer);
        }
    }
}
