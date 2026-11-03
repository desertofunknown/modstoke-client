// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Client;

internal static class ClientConnections
{
    internal static bool Contains(IntPtr pointer, uint id)
    {
        // BoltNetwork.clients filters this live list by the transport's server-side role.
        var connections = BoltCore._connections;
        var count = connections.count;
        if (count == 0) return false;
        var connection = connections.first;
        for (var i = 0; i < count; i++)
        {
            if (connection != null && !connection.WasCollected && connection._udp.IsServer
                && connection.Pointer == pointer && connection.ConnectionId == id) return true;
            if (i + 1 < count) connection = connections.Next(connection);
        }
        return false;
    }
}
