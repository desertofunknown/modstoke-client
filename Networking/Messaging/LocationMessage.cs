// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Modstoke.Client;

// Plain-text map attachments remain readable by older managed clients. Positions are sender reports.
internal static class ClientLocationMessage
{
    internal static string Encode(ClientFieldPlace place)
    {
        var coordinates = " [MST1 " + place.Position.X.ToString("F1", CultureInfo.InvariantCulture) + ","
            + place.Position.Z.ToString("F1", CultureInfo.InvariantCulture) + "]";
        var name = new StringBuilder();
        var remaining = 256 - Encoding.UTF8.GetByteCount("Map pin: " + coordinates);
        foreach (var scalar in place.Name.EnumerateRunes())
        {
            if (scalar.Utf8SequenceLength > remaining || name.Length + scalar.Utf16SequenceLength > 64) break;
            name.Append(scalar.ToString());
            remaining -= scalar.Utf8SequenceLength;
        }
        return "Map pin: " + name + coordinates;
    }

    internal static bool TryRead(string text, out ClientFieldPlace place)
    {
        place = null!;
        if (text == null || text.Length > 256 || !text.StartsWith("Map pin: ", StringComparison.Ordinal)
            || !text.EndsWith(']')) return false;
        var marker = text.LastIndexOf(" [MST1 ", StringComparison.Ordinal);
        if (marker < 10) return false;
        var name = text[9..marker];
        var coordinates = text[(marker + 7)..^1].Split(',');
        if (name.Length > 64 || string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl) || coordinates.Length != 2
            || !float.TryParse(coordinates[0], NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var x)
            || !float.TryParse(coordinates[1], NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var z)
            || !float.IsFinite(x) || !float.IsFinite(z) || MathF.Abs(x) > 1_000_000 || MathF.Abs(z) > 1_000_000) return false;
        place = new ClientFieldPlace(Guid.NewGuid(), name, new(x, 0, z), "Shared map pin; location reported by its sender.", "Shared pin");
        return true;
    }
}
