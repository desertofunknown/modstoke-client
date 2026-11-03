// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Modstoke.Sons;

namespace Modstoke.Client;

internal sealed record ClientDeviceCard(string Id, string Type, double Distance, string State, bool Owned);
internal sealed record ClientDeviceRecipe(int Id, string Name, double Resin, double Seconds);
internal sealed record ClientDeviceObservation(string Id, string State, double Resin, double Capacity,
    int Recipe, double? Progress, bool Owned);
internal sealed record ClientDeviceAppSnapshot(bool Available, bool Busy, string Message,
    int DevicePage, bool MoreDevices, ClientDeviceCard? Device, int RecipePage, bool MoreRecipes,
    ClientDeviceRecipe? Recipe, ClientDeviceObservation? Observation, double ObservationAge,
    string PairedId, string Review, string ConfirmVerb, string Receipt,
    ClientDeviceCatalogSnapshot Catalog, bool CanSelectRecipe, bool CanPrint, string ChangeAvailability,
    string ConfirmLabel);

// Owned by the terminal's game thread. A receipt never substitutes for a native observation.
internal sealed partial class ClientDeviceApp : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly Action<string, string>? _commandResult;
    private Guid? _session;
    private bool _started, _disposed, _visible, _opened;
    private ClientCommand? _pending;
    private Request? _request;
    private Request? _queuedRead;
    private Review? _review;
    private ClientDeviceCard? _device;
    private ClientDeviceRecipe? _recipe;
    private ClientDeviceObservation? _observation;
    private int _devicePage = 1, _recipePage = 1;
    private bool _moreDevices, _moreRecipes;
    private string _pairedId = "", _message = "Join a compatible server to use Devices.", _receipt = "";
    private long _nextRead, _deviceAt, _recipeAt, _observedAt, _pairRevision;
    private bool _wantStatus;

    private sealed record Request(Guid Session, string Kind, int Page, string Command, string DeviceId,
        long PairRevision, long SelectionRevision);
    private sealed record Review(Guid Session, Guid Token, string DeviceId, string Kind, string Command,
        string Description, long ExpiresAt, long PairRevision, int NativeRecipe, int? SelectedRecipe, long RecipeAt);

    internal ClientDeviceApp(Action<string, string>? commandResult = null) => _commandResult = commandResult;
    private bool OnThread => Environment.CurrentManagedThreadId == _thread;
    private static long Now => Environment.TickCount64;
    private static bool Fresh(long observedAt) => observedAt != 0 && Now - observedAt < 30000;
    private bool Ready => _session.HasValue && _pending == null && _queuedRead == null;

    internal ClientDeviceAppSnapshot Snapshot
    {
        get
        {
            Expire();
            return new(_session.HasValue, _pending != null || _queuedRead != null, _message, _devicePage, _moreDevices,
                _device, _recipePage, _moreRecipes, _recipe, _observation,
                _observation == null ? 0 : Math.Max(0, (Now - _observedAt) / 1000d), _pairedId,
                _review?.Description ?? "", _review == null ? "" : "confirm." + _review.Token.ToString("N"), _receipt,
                CatalogSnapshot(), CanSelectRecipe, CanPrint, ChangeAvailability,
                _review?.Kind == "print" ? "Confirm one print" : "Confirm recipe");
        }
    }

    internal void Start()
    {
        if (!OnThread || _disposed) return;
        _started = true;
        Update();
    }

    internal void SetVisible(bool visible)
    {
        if (!OnThread || _disposed) return;
        if (_visible && !visible) { _review = null; _queuedRead = null; }
        _visible = visible;
    }

    private bool RefreshSession()
    {
        if (!OnThread || !_started || _disposed) return false;
        var current = ClientCommands.TryGetCurrentSession(out var session)
            && ClientCommands.SupportsReplies ? session : (Guid?)null;
        if (_session != current)
        {
            Clear();
            _session = current;
            _message = current.HasValue ? "Scan for a nearby printer or read your current link."
                : "Join a compatible server with command replies to use Devices.";
        }
        Expire();
        return current.HasValue;
    }

    internal void Update()
    {
        if (!RefreshSession()) return;
        if (_pending != null && _request is { } request)
        {
            var result = _pending.State;
            if (!result.IsTerminal) return;
            _pending = null;
            _request = null;
            if (_session == request.Session) Complete(request, result);
        }
        if (!_visible || _pending != null || _review != null || Now < _nextRead) return;
        if (_queuedRead is { } queued)
        {
            _queuedRead = null;
            if (queued.Session == _session && queued.PairRevision == _pairRevision
                && (queued.Kind != "recipes" || queued.DeviceId == _pairedId && CurrentObservation))
                Send(queued.Kind, queued.Page, queued.Command, selectionRevision: queued.SelectionRevision);
            else _message = "The printer observation changed. Read status before browsing again.";
            return;
        }
        if (_wantStatus || _pairedId.Length > 0)
        {
            _wantStatus = false;
            Send("status", 0, "/device view status");
        }
        else if (!_opened)
        {
            _opened = true;
            Send("scan", 1, "/device view scan 1");
        }
    }

    internal void HandleCommand(string verb)
    {
        if (!RefreshSession() || !_visible) return;
        if (HandleCatalogCommand(verb)) return;
        if (!Ready) return;
        if (verb.StartsWith("confirm.", StringComparison.Ordinal))
        {
            var review = _review;
            _review = null;
            if (review == null || verb != "confirm." + review.Token.ToString("N")
                || review.Session != _session || review.DeviceId != _pairedId || Now >= review.ExpiresAt
                || review.PairRevision != _pairRevision || !CurrentObservation || ChangeAvailability.Length > 0
                || _observation!.Recipe != review.NativeRecipe
                || review.Kind == "recipe" && (_recipe?.Id != review.SelectedRecipe
                    || _recipeAt != review.RecipeAt || !Fresh(_recipeAt))
                || review.Kind == "print" && !CanPrint)
            {
                _message = "That review expired. Refresh the printer and prepare the change again.";
                return;
            }
            Send(review.Kind, 0, review.Command);
            return;
        }
        _review = null;
        switch (verb)
        {
            case "cancel": _message = "Change cancelled. Nothing sent."; break;
            case "scan": ReadPage("scan", 1); break;
            case "scan-prev" when _devicePage > 1: ReadPage("scan", _devicePage - 1); break;
            case "scan-next" when _moreDevices: ReadPage("scan", _devicePage + 1); break;
            case "status": ReadStatus(); break;
            case "recipes": ReadPage("recipes", 1); break;
            case "recipes-refresh": ReadPage("recipes", _recipePage); break;
            case "recipes-prev" when _recipePage > 1: BrowseRecipePage(_recipePage - 1); break;
            case "recipes-next" when _moreRecipes && _recipePage < MaximumCatalogPages: BrowseRecipePage(_recipePage + 1); break;
            case "pair" when _device != null && Fresh(_deviceAt):
                Send("pair", 0, "/device pair " + _device.Id, _device.Id); break;
            case "unpair" when _pairedId.Length > 0:
                Send("unpair", 0, "/device unpair"); break;
            case "prepare-recipe" when CanSelectRecipe:
                var selection = _recipe!;
                Prepare("recipe", "/device recipe " + selection.Id.ToString(CultureInfo.InvariantCulture),
                    "Select " + selection.Name + " on the paired printer. Requires " + Number(selection.Resin)
                    + " resin per print; selecting a recipe does not start the local plan."); break;
            case "prepare-print" when CanPrint:
                Prepare("print", "/device print", DescribePrintReview()); break;
        }
    }

    private void Prepare(string kind, string command, string description)
    {
        if (!CurrentObservation || ChangeAvailability.Length > 0)
        {
            _message = "Read the paired printer's current status before preparing a change.";
            return;
        }
        _review = new(_session!.Value, Guid.NewGuid(), _pairedId, kind, command, description, Now + 20000,
            _pairRevision, _observation!.Recipe, kind == "recipe" ? _recipe?.Id : null, _recipeAt);
        _message = "Review this change, then confirm once to send it.";
    }

    private void ReadStatus()
    {
        QueueRead("status", 0, "/device view status");
    }

    private void ReadPage(string kind, int page)
    {
        if (page < 1 || page > (kind == "recipes" ? MaximumCatalogPages : 64)) return;
        if (kind == "recipes" && (!CurrentObservation || _catalogNeedsStatus))
        {
            _message = "Read the linked printer's status before requesting a recipe page.";
            return;
        }
        _opened = true;
        QueueRead(kind, page, "/device view " + kind + " " + page.ToString(CultureInfo.InvariantCulture));
    }

    private void QueueRead(string kind, int page, string command)
    {
        _queuedRead = new(_session!.Value, kind, page, command, _pairedId, _pairRevision, _catalogSelectionRevision);
        _message = "Observation queued. Waiting for the refresh interval.";
    }

    private void Send(string kind, int page, string command, string deviceId = "", long? selectionRevision = null)
    {
        if (!Ready) return;
        var mutation = kind is "pair" or "unpair" or "recipe" or "print";
        if (mutation)
        {
            _observation = null;
            if (kind is "pair" or "unpair") SetPair("", true);
            _receipt = kind == "print" ? "Latest print invocation: waiting for a server receipt." : "Waiting for the server's change receipt.";
        }
        else if (kind == "scan") { _device = null; _moreDevices = false; _observation = null; }
        _nextRead = Now + 3000;
        var request = new Request(_session!.Value, kind, page, command,
            kind == "pair" ? deviceId : _pairedId, _pairRevision, selectionRevision ?? _catalogSelectionRevision);
        var sent = ClientCommands.Submit(command, out var handle);
        _pending = handle;
        _request = handle == null ? null : request;
        _message = handle != null ? "Waiting for the server…" : "Request was not queued: " + sent + ". Nothing was retried.";
        if (sent == ClientCommandSendResult.SendUncertain)
            _message = "Result unknown. Waiting if a request handle exists; no change will be retried.";
        if (mutation && handle == null) { _receipt = _message; _commandResult?.Invoke("Devices", _message); }
    }

    private void Complete(Request request, ClientCommandState result)
    {
        var reported = result.Progress == ClientCommandProgress.ServerReported;
        var success = reported && result.Outcome == ClientCommandOutcome.Succeeded;
        var mutation = request.Kind is "pair" or "unpair" or "recipe" or "print";
        if (mutation)
        {
            _receipt = request.Kind == "print" ? "Latest print invocation: " : "Latest change: ";
            _receipt += success ? "server reported success. This is an invocation receipt, not completion."
                : reported ? "server reported " + result.Outcome + "." : "result unknown (" + result.Progress + "). Nothing was retried.";
            _message = _receipt;
            if (success && request.Kind == "pair") SetPair(request.DeviceId);
            if (success && request.Kind == "unpair") SetPair("", true);
            else _wantStatus = true;
            _commandResult?.Invoke("Devices", _receipt);
            return;
        }
        if (!success)
        {
            _message = reported ? "Server could not provide " + request.Kind + ": " + result.Outcome + "."
                : "Observation unavailable; result unknown (" + result.Progress + ").";
            if (request.Kind == "status") SetPair("", true);
            else if (request.Kind == "recipes") { ClearCatalog(); _observation = null; }
            return;
        }
        var age = Math.Max(0, (DateTimeOffset.UtcNow - result.ObservedAt).TotalMilliseconds);
        if (result.ReplyTruncated || age >= 30000 || !Parse(request, result.Reply, Now - (long)age))
        {
            _message = "Server observation rejected: incomplete, invalid, or expired reply.";
            if (request.Kind == "status") SetPair("", true);
            else if (request.Kind == "recipes") { ClearCatalog(); _observation = null; }
            return;
        }
        _message = request.Kind == "scan" ? (_device == null ? "No printer on this page." : "Nearby printer observed. Select Pair to link it.")
            : request.Kind == "recipes" ? "Recipe page received. Waiting for current status to confirm the printer link."
            : "Printer status observed by the server.";
    }

    private bool Parse(Request request, string? reply, long at)
    {
        try
        {
            if (string.IsNullOrEmpty(reply) || Utf8.GetByteCount(reply) > 512) return false;
            using var document = JsonDocument.Parse(reply, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (request.Kind == "status")
            {
                if (!Fields(root, "v", "kind", "id", "state", "resin", "capacity", "recipe", "progress", "owned")
                    || !Header(root, request.Kind) || !Id(root, out var id) || !Text(root, "state", out var state)
                    || !Number(root, "resin", out var resin) || !Number(root, "capacity", out var capacity)
                    || capacity < resin || !Integer(root, "recipe", out var recipe) || recipe < 0
                    || !Boolean(root, "owned", out var owned)) return false;
                var progress = root.GetProperty("progress");
                double? nativeProgress = null;
                if (progress.ValueKind != JsonValueKind.Null)
                {
                    if (progress.ValueKind != JsonValueKind.Number || !progress.TryGetDouble(out var value) || !double.IsFinite(value)) return false;
                    nativeProgress = value;
                }
                SetPair(id);
                _observation = new(id, state, resin, capacity, recipe, nativeProgress, owned);
                _pairedId = id;
                _observedAt = at;
                ConfirmCatalogLink();
                return true;
            }
            if (!Fields(root, "v", "kind", "page", "more", "item") || !Header(root, request.Kind)
                || !Integer(root, "page", out var page) || page != request.Page || !Boolean(root, "more", out var more)) return false;
            var item = root.GetProperty("item");
            if (item.ValueKind == JsonValueKind.Null && more) return false;
            if (request.Kind == "scan")
            {
                ClientDeviceCard? device = null;
                if (item.ValueKind != JsonValueKind.Null)
                {
                    if (!Fields(item, "id", "type", "distance", "state", "owned") || !Id(item, out var id)
                        || !Text(item, "type", out var type) || !Text(item, "state", out var state)
                        || !Number(item, "distance", out var distance) || !Boolean(item, "owned", out var owned)) return false;
                    device = new(id, type, distance, state, owned);
                }
                _device = device; _devicePage = page; _moreDevices = more; _deviceAt = at;
                return true;
            }
            if (request.Kind != "recipes") return false;
            if (request.PairRevision != _pairRevision || request.DeviceId.Length == 0
                || request.DeviceId != _pairedId || !CurrentObservation) return false;
            ClientDeviceRecipe? selection = null;
            if (item.ValueKind != JsonValueKind.Null)
            {
                if (!Fields(item, "id", "name", "resin", "seconds") || !Integer(item, "id", out var id) || id < 0
                    || !Text(item, "name", out var name) || !Integer(item, "resin", out var resin) || resin < 0
                    || !Number(item, "seconds", out var seconds)) return false;
                selection = new(id, name, resin, seconds);
            }
            _pendingCatalogPage = new(selection, page, more, at, request.SelectionRevision);
            _catalogNeedsStatus = true;
            return true;
        }
        catch (JsonException) { return false; }
        catch (EncoderFallbackException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static bool Fields(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;
        var remaining = new HashSet<string>(names, StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject()) if (!remaining.Remove(property.Name)) return false;
        return remaining.Count == 0;
    }
    private static bool Header(JsonElement root, string kind) => Integer(root, "v", out var version)
        && version == 1 && root.GetProperty("kind").ValueKind == JsonValueKind.String && root.GetProperty("kind").GetString() == kind;
    private static bool Integer(JsonElement root, string name, out int value)
    { value = 0; var field = root.GetProperty(name); return field.ValueKind == JsonValueKind.Number && field.TryGetInt32(out value); }
    private static bool Number(JsonElement root, string name, out double value)
    { value = 0; var field = root.GetProperty(name); return field.ValueKind == JsonValueKind.Number && field.TryGetDouble(out value)
        && double.IsFinite(value) && value >= 0 && value <= float.MaxValue; }
    private static bool Boolean(JsonElement root, string name, out bool value)
    { var field = root.GetProperty(name); value = field.ValueKind == JsonValueKind.True; return field.ValueKind is JsonValueKind.True or JsonValueKind.False; }
    private static bool Text(JsonElement root, string name, out string value)
    {
        value = "";
        var field = root.GetProperty(name);
        if (field.ValueKind != JsonValueKind.String) return false;
        value = field.GetString() ?? "";
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128) return false;
        foreach (var character in value) if (char.IsControl(character)) return false;
        return true;
    }
    private static bool Id(JsonElement root, out string value)
    {
        if (!Text(root, "id", out value) || value.Length > 64) return false;
        foreach (var character in value)
            if (!char.IsAsciiLetterOrDigit(character) && character is not (':' or '-' or '_' or '.')) return false;
        return true;
    }
    internal static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private void Expire()
    {
        if (_device != null && !Fresh(_deviceAt)) { _device = null; _moreDevices = false; }
        if (_observation != null && !Fresh(_observedAt)) _observation = null;
        if (_review != null && (Now >= _review.ExpiresAt || _observation == null
            || _review.PairRevision != _pairRevision || _review.Kind == "recipe" && !Fresh(_recipeAt)))
        {
            _review = null;
            _message = "Review expired. Read current status and prepare the change again.";
        }
    }
    private void Clear()
    {
        _pending = null; _request = null; _queuedRead = null; _review = null; _device = null; _recipe = null; _observation = null;
        _pairedId = ""; _receipt = ""; _devicePage = _recipePage = 1; _moreDevices = _moreRecipes = false;
        _wantStatus = _opened = false; _nextRead = _deviceAt = _recipeAt = _observedAt = 0;
        _pairRevision++;
        ClearCatalog();
    }
    public void Dispose()
    {
        if (!OnThread) return;
        _disposed = true; Clear(); _session = null;
    }
}
