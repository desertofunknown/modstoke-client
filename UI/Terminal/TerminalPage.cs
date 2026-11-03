// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Collections.Generic;
using Modstoke.Loader;

namespace Modstoke.Client;

internal enum ClientTerminalApp { Home, Tribe, Maps, Services, Companions, Navigation, Messages, Settings, Devices, Journal, Health, Watch, Games, Drone, Pack, Hangar, Spaceflight }

internal sealed record ClientTerminalNote(string Label, string Text);

// The terminal presents the existing controllers; it does not interpret replies as permissions.
internal sealed record ClientTerminalPage(
    ClientTerminalApp App,
    string ProviderId,
    string Title,
    string Subtitle,
    bool Connected,
    string Status,
    string Selection,
    string Reply,
    string Review,
    IReadOnlyList<ClientTerminalNote> Notes,
    IReadOnlyList<ClientTerminalNote> Details,
    MenuInputSnapshot? Inputs,
    IReadOnlyList<CommandSnapshot> Commands);
