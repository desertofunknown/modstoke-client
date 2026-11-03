// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using Modstoke.Loader;
using Modstoke.Sons;

namespace Modstoke.Client;

internal static class ClientTerminalModel
{
    internal static string ProviderId(ClientTerminalApp app) => app switch {
        ClientTerminalApp.Tribe => ClientTribeProvider.MenuId,
        ClientTerminalApp.Maps => ClientMapProvider.MenuId,
        ClientTerminalApp.Services => ClientServicesProvider.MenuId,
        ClientTerminalApp.Companions => ClientCompanionProvider.MenuId,
        ClientTerminalApp.Devices => ClientDeviceProvider.MenuId,
        _ => ""
    };

    internal static ClientTerminalPage Read(ClientTerminalApp app, string feedback)
    {
        var connected = ClientCommands.TryGetCurrentSession(out _)
            && ClientCommands.SupportsReplies;
        var provider = ProviderId(app);
        var title = app switch {
            ClientTerminalApp.Tribe => "Tribe & tasks",
            ClientTerminalApp.Maps => "Places & notes",
            ClientTerminalApp.Navigation => "GPS",
            ClientTerminalApp.Messages => "Messages",
            ClientTerminalApp.Settings => "Settings",
            ClientTerminalApp.Devices => "Devices",
            ClientTerminalApp.Journal => "Field notebook",
            ClientTerminalApp.Health => "Health",
            ClientTerminalApp.Watch => "Clock",
            ClientTerminalApp.Games => "Downtime",
            ClientTerminalApp.Drone => "Drone",
            ClientTerminalApp.Pack => "Pack",
            ClientTerminalApp.Hangar => "Hangar",
            ClientTerminalApp.Spaceflight => "Spaceflight",
            ClientTerminalApp.Services => "Community services",
            ClientTerminalApp.Companions => "Companions",
            _ => "Modstoke OS"
        };
        var subtitle = app switch {
            ClientTerminalApp.Tribe => "Your people, invitations, shared work and announcements.",
            ClientTerminalApp.Maps => "Personal waypoints, shared notes and rally points. These do not add GPS icons.",
            ClientTerminalApp.Services => "Campaigns, Tribe points, travel and facility information.",
            ClientTerminalApp.Companions => "Supported work orders and community queues.",
            ClientTerminalApp.Navigation => "Island navigation",
            ClientTerminalApp.Messages => "Expedition, Tribe and direct messages. The server controls who can receive them.",
            ClientTerminalApp.Settings => "Make this field tablet your own.",
            ClientTerminalApp.Devices => "Link to nearby equipment. Access and native operations are checked by the server.",
            ClientTerminalApp.Journal => "Personal notes and your next objective.",
            ClientTerminalApp.Health => "Your current condition, reported by the game.",
            ClientTerminalApp.Watch => "Shared timers, reminders and your paired field watch.",
            ClientTerminalApp.Games => "A little downtime on the island.",
            ClientTerminalApp.Drone => "Flight deck · camera, flight control and onboard systems.",
            ClientTerminalApp.Pack => "Your supplies, equipment and crafting plans.",
            ClientTerminalApp.Hangar => "Shared aircraft · one pilot, two passengers.",
            ClientTerminalApp.Spaceflight => "Your spacecraft, crew and next destination.",
            _ => "Your expedition, people and places."
        };
        if (app == ClientTerminalApp.Spaceflight)
            return new(app, provider, title, subtitle, Plugin.Spacecraft.View.Ready, Plugin.Spacecraft.View.Status,
                "", "", "", Array.Empty<ClientTerminalNote>(), Array.Empty<ClientTerminalNote>(),
                null, Array.Empty<CommandSnapshot>());
        if (app == ClientTerminalApp.Hangar)
            return new(app, provider, title, subtitle, Plugin.Aircraft.View.Ready, Plugin.Aircraft.View.Status,
                "", "", "", Array.Empty<ClientTerminalNote>(), Array.Empty<ClientTerminalNote>(),
                null, Array.Empty<CommandSnapshot>());
        if (app == ClientTerminalApp.Pack)
            return new(app, provider, title, subtitle, connected, "Local supplies and crafting plans.",
                "", "", "", Array.Empty<ClientTerminalNote>(), Array.Empty<ClientTerminalNote>(),
                null, Array.Empty<CommandSnapshot>());
        var unavailable = "Community services are not ready. Local tablet apps remain available.";
        if (app is ClientTerminalApp.Home or ClientTerminalApp.Navigation or ClientTerminalApp.Messages or ClientTerminalApp.Settings || !MenuRegistry.TryGetProviderContent(provider, out var content))
            return new(app, provider, title, subtitle, connected,
                connected ? "Server link available. Your server checks access for every request." : unavailable,
                "", "", "", Array.Empty<ClientTerminalNote>(), Array.Empty<ClientTerminalNote>(),
                null, Array.Empty<CommandSnapshot>());

        MenuRegistry.TryGetProviderInputs(provider, out var inputs);
        MenuRegistry.TryGetProviderCommands(provider, out var commands);
        var rows = content.Sections.SelectMany(section => section.Rows).ToArray();
        string Value(string label) => rows.FirstOrDefault(row => row.Label == label)?.Value ?? "";
        var notes = new List<ClientTerminalNote>();
        var details = new List<ClientTerminalNote>();
        var review = "";
        foreach (var row in rows)
        {
            if (row.Label.StartsWith("Confirm this", StringComparison.Ordinal) || row.Label == "Review exact command")
            {
                review = row.Value;
                continue;
            }
            if (row.Label is "Effects" or "Position" or "Sharing" or "Removal" or "Rally"
                or "Shortened reply" or "Travel request" or "Receipt meaning" or "Visible escapes")
                notes.Add(new(row.Label, row.Value));
            else if (row.Label is "Persistence" or "Save")
                notes.Add(new("World save", "Accepted changes remain in memory until the next world save."));
            else if (row.Label is "Freshness")
                notes.Add(new("Snapshot", "Replies expire after 30 seconds. Refresh to check for changes."));
            else if (row.Label is not ("Request" or "View" or "Selected" or "Reply" or "Last reply"))
                details.Add(new(row.Label, row.Value));
        }
        var status = connected ? Value("Request") : unavailable;
        if (!string.IsNullOrEmpty(feedback))
            status = feedback;
        var selection = Value("Selected");
        if (string.IsNullOrEmpty(selection))
            selection = Value("View");
        var reply = Value("Reply");
        if (string.IsNullOrEmpty(reply))
            reply = "Choose a read action to request information from your server.";
        return new(app, provider, title, subtitle, connected, status, selection, reply, review,
            notes, details, inputs, commands?.Commands ?? Array.Empty<CommandSnapshot>());
    }
}
