// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Linq;

namespace Modstoke.Client;

internal sealed partial class ClientTabletOperatingSystem
{
    private bool ApplyFavoriteEdit(ClientFieldAction action)
    {
        if (!Enum.IsDefined(action.App) || action.App == ClientTerminalApp.Home) return false;
        var favorites = _field.Favorites.ToArray();
        var index = Array.IndexOf(favorites, action.App);
        switch (action.Kind)
        {
            case "favorite-add":
                if (index >= 0 || favorites.Length >= 8) return false;
                favorites = favorites.Append(action.App).ToArray();
                break;
            case "favorite-remove":
                if (index < 0) return false;
                favorites = favorites.Where(app => app != action.App).ToArray();
                break;
            case "favorite-move":
                if (index < 0 || action.Target is not ("-1" or "1")) return false;
                var next = index + (action.Target == "-1" ? -1 : 1);
                if (next < 0 || next >= favorites.Length) return false;
                (favorites[index], favorites[next]) = (favorites[next], favorites[index]);
                break;
            case "favorite-replace":
                if (index >= 0 || !Enum.TryParse<ClientTerminalApp>(action.Target, out var previous)
                    || !Enum.IsDefined(previous) || previous == ClientTerminalApp.Home) return false;
                var replaced = Array.IndexOf(favorites, previous);
                if (replaced < 0) return false;
                favorites[replaced] = action.App;
                break;
            default: return false;
        }
        _field = _field with { Favorites = Array.AsReadOnly(favorites) };
        return true;
    }
}
