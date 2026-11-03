// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Modstoke.Sons;

namespace Modstoke.Client;

internal sealed record ClientMessengerContact(string Id, string Name);

internal sealed partial class ClientMessenger : IDisposable
{
    private const int PendingLimit = 8;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
    private readonly Action<MessengerMessage> _received;
    private readonly Dictionary<Guid, PendingMessage> _pending = new();
    private Guid? _session;
    private Guid? _messengerSession;
    private bool _started;
    private bool _disposed;

    private readonly record struct PendingMessage(Guid Session, string Channel, string Target, long ExpiresAt);

    internal ClientMessenger(Action<MessengerMessage> received)
    {
        _received = received ?? throw new ArgumentNullException(nameof(received));
    }

    internal bool Available { get; private set; }
    internal string Status { get; private set; } = "Waiting for Messenger to start.";
    internal string Channel { get; private set; } = "expedition";
    internal string Target { get; private set; } = "";
    internal IReadOnlyList<ClientMessengerContact> Contacts { get; private set; } = Array.Empty<ClientMessengerContact>();
    internal int ContactPage { get; private set; } = 1;
    internal bool MoreContacts { get; private set; }
    internal bool CanSend => Available && IsManagedChannel(Channel) && PendingCount < PendingLimit
        && (Channel != "direct" || IsListedTarget());

    internal void Start()
    {
        if (!OnOwnerThread || _disposed || _started)
            return;
        _started = true;
        Messenger.Received += OnReceived;
        Messenger.Completed += OnCompleted;
        Update();
    }

    internal void Update()
    {
        if (!RefreshSession())
            return;

        var now = Environment.TickCount64;
        List<Guid>? expired = null;
        var selectedExpired = false;
        foreach (var entry in _pending)
        {
            if (now >= entry.Value.ExpiresAt)
            {
                (expired ??= new()).Add(entry.Key);
                selectedExpired |= IsSelectedConversation(entry.Value);
            }
        }
        if (expired != null)
        {
            foreach (var id in expired)
                _pending.Remove(id);
            if (selectedExpired)
                Status = "Message result unknown after timeout. Nothing was resent.";
        }

        UpdateContacts(now);
    }

    internal void SelectChannel(string channel)
    {
        if (!OnOwnerThread || _disposed || (!IsManagedChannel(channel) && channel != "legacy"))
            return;
        if (Channel != channel)
            StopContactSearch("Contact lookup cancelled when the conversation changed.");
        Channel = channel;
        Status = channel == "legacy" ? "Legacy public chat uses the native chat bridge."
            : channel == "direct" ? DirectComposeStatus() : "Ready to compose.";
    }

    internal void SelectTarget(string id)
    {
        if (!RefreshSession())
            return;
        if (!_contactsCurrent || ContactsBusy)
        {
            Status = "Refresh contacts successfully before selecting a recipient. The retained list is for browsing.";
            return;
        }
        foreach (var contact in Contacts)
        {
            if (!string.Equals(contact.Id, id, StringComparison.Ordinal))
                continue;
            Target = contact.Id;
            SetContactDetail("Recipient selected. The server checks the current session on Send.");
            return;
        }
        Status = "Select a recipient from the current contacts page.";
    }

    internal void SelectIncomingConversation(string id)
    {
        if (!RefreshSession() || string.IsNullOrEmpty(id) || id.Length > 64) return;
        if (Channel != "direct" || Target != id)
            StopContactSearch("Contact lookup cancelled when the conversation changed.");
        Channel = "direct";
        Target = id;
        // History can be opened immediately. Sending still requires a current contact selection.
        SetContactDetail(DirectComposeStatus());
    }

    internal bool Send(string text)
    {
        if (!OnOwnerThread || _disposed || !_started)
            return false;
        if (Channel == "legacy")
        {
            Status = "Send legacy public chat through the native chat bridge.";
            return false;
        }
        if (!RefreshSession())
            return false;
        if (!Available)
        {
            Status = "Messenger is unavailable on this server.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(text) || !FitsUtf8(text, Messenger.MaxTextBytes))
        {
            Status = "Enter a message of at most 256 UTF-8 bytes.";
            return false;
        }
        if (Channel == "direct" && !IsListedTarget())
        {
            Status = ContactsBusy ? "Waiting for fresh contacts before sending this direct message."
                : Target.Length == 0 ? "Load contacts and select a current recipient."
                : "Use Find current contact to check this conversation before replying.";
            return false;
        }
        if (PendingCount >= PendingLimit)
        {
            Status = "Wait for the pending message results before sending again.";
            return false;
        }
        var session = _messengerSession!.Value;
        var channel = Channel;
        var target = channel == "direct" ? Target : "";
        var sent = Messenger.TrySubmit(channel, target, text, out var id, out var detail);
        // A local handler can retire the Messenger lifetime during submission.
        if (!RefreshSession() || session != _messengerSession)
            return sent;
        if (id != Guid.Empty)
            _pending[id] = new PendingMessage(session, channel, target, Environment.TickCount64 + 30_000);
        Status = string.IsNullOrWhiteSpace(detail)
            ? sent ? "Message submitted; delivery is not confirmed." : "Message was not submitted. Nothing was retried."
            : detail;
        return sent;
    }

    private bool RefreshSession()
    {
        if (!OnOwnerThread || _disposed || !_started)
            return false;
        var current = ClientCommands.TryGetCurrentSession(out var session) ? session : (Guid?)null;
        if (_session != current)
        {
            _session = current;
            ClearContacts();
            Status = current.HasValue ? "Ready to compose. Load contacts explicitly for direct messages." : "Join a compatible server to use Messenger.";
        }
        var messengerCurrent = Messenger.TryGetCurrentSession(out var messengerSession) ? messengerSession : (Guid?)null;
        if (_messengerSession != messengerCurrent)
        {
            var retired = _messengerSession.HasValue;
            _messengerSession = messengerCurrent;
            _pending.Clear();
            _contactsCurrent = false;
            EndContactSearch();
            _discardContactsReply = _contactsRequest != null;
            if (retired)
                SetContactDetail("Messenger session changed. Refresh contacts explicitly; nothing was resent.");
        }
        Available = current.HasValue && messengerCurrent.HasValue;
        _supportsContactReplies = current.HasValue && ClientCommands.SupportsReplies;
        if (!Available || !_supportsContactReplies) _contactsCurrent = false;
        if (!current.HasValue)
            Status = "Join a compatible server to use Messenger.";
        return current.HasValue;
    }

    private void OnReceived(MessengerMessage message)
    {
        if (!RefreshSession() || !Available || message == null || !IsManagedChannel(message.Channel)
            || !FitsUtf8(message.Text, Messenger.MaxTextBytes))
            return;
        // The SDK dispatches only while its originating connection or host/world lifetime is current.
        _received(message);
    }

    private void OnCompleted(MessengerResult result)
    {
        if (!RefreshSession() || result == null || !_pending.Remove(result.Id, out var pending)
            || pending.Session != _messengerSession || !IsSelectedConversation(pending))
            return;
        Status = string.IsNullOrWhiteSpace(result.Detail)
            ? result.Accepted ? "Server accepted the message; delivery is not confirmed." : "Server rejected the message."
            : result.Detail;
    }

    private bool OnOwnerThread => Environment.CurrentManagedThreadId == _ownerThreadId;
    private int PendingCount => _pending.Count + (_contactsRequest == null ? 0 : 1);
    private bool IsSelectedConversation(PendingMessage pending) => pending.Channel == Channel
        && (Channel != "direct" || pending.Target == Target);
    private static bool IsManagedChannel(string channel) => channel is "expedition" or "tribe" or "direct";

    private static bool FitsUtf8(string? text, int limit)
    {
        if (text == null || text.Length > limit)
            return false;
        try { return StrictUtf8.GetByteCount(text) <= limit; }
        catch (EncoderFallbackException) { return false; }
    }

    private static bool TryReadContacts(string? reply, int page, out List<ClientMessengerContact> contacts, out bool more)
    {
        contacts = new List<ClientMessengerContact>();
        more = false;
        if (page < 1 || page > ContactPageLimit || !FitsUtf8(reply, ClientCommands.MaxReplyBytes))
            return false;
        try
        {
            using var document = JsonDocument.Parse(reply!, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;
            var fields = 0;
            JsonElement items = default;
            foreach (var property in root.EnumerateObject())
            {
                var flag = property.Name switch { "page" => 1, "more" => 2, "items" => 4, _ => 0 };
                if (flag == 0 || (fields & flag) != 0)
                    return false;
                fields |= flag;
                if (flag == 1 && (property.Value.ValueKind != JsonValueKind.Number
                    || !property.Value.TryGetInt32(out var returnedPage) || returnedPage != page))
                    return false;
                if (flag == 2)
                {
                    if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        return false;
                    more = property.Value.GetBoolean();
                }
                if (flag == 4)
                    items = property.Value;
            }
            if (fields != 7 || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > 4)
                return false;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    return false;
                string? id = null;
                string? name = null;
                foreach (var property in item.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.String)
                        return false;
                    if (property.Name == "id" && id == null)
                        id = property.Value.GetString();
                    else if (property.Name == "name" && name == null)
                        name = property.Value.GetString();
                    else
                        return false;
                }
                if (id == null || string.IsNullOrWhiteSpace(name) || name.Length > 64 || !FitsUtf8(name, 256))
                    return false;
                var identity = id;
                if (id != "host")
                {
                    if (id.Length != 36 || !Guid.TryParseExact(id, "D", out var parsed) || parsed == Guid.Empty)
                        return false;
                    identity = parsed.ToString("N");
                }
                if (!ids.Add(identity))
                    return false;
                foreach (var character in name)
                    if (char.IsControl(character))
                        return false;
                contacts.Add(new ClientMessengerContact(id, name));
            }
            return true;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { return false; }
    }

    public void Dispose()
    {
        if (!OnOwnerThread || _disposed)
            return;
        _disposed = true;
        if (_started)
        {
            Messenger.Received -= OnReceived;
            Messenger.Completed -= OnCompleted;
        }
        _started = false;
        _session = null;
        _messengerSession = null;
        Available = false;
        _pending.Clear();
        ClearContacts();
        Status = "Messenger has stopped.";
    }
}
