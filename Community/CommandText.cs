// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Globalization;
using System.Text;

namespace Modstoke.Client;

internal static class ClientCommandText
{
    internal static string DisplayReply(string value) => DisplayReply(value, out _);

    internal static string DisplayReply(string value, out bool shortened)
    {
        var text = new StringBuilder();
        var count = 0;
        shortened = false;
        foreach (var rune in value.EnumerateRunes())
        {
            if (count++ == 512)
            {
                shortened = true;
                break;
            }
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                text.Append(' ');
            else if (rune.Value == '<')
                text.Append('\u2039');
            else if (rune.Value == '>')
                text.Append('\u203a');
            else
                text.Append(rune.ToString());
        }
        return text.ToString();
    }

    internal static bool ValidId(string id)
    {
        if (id.Length == 36 && Guid.TryParseExact(id, "D", out _))
            return true;
        if (id.Length is < 8 or > 32)
            return false;
        foreach (var c in id)
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                return false;
        return true;
    }

    internal static bool IsSafeText(string value)
    {
        foreach (var c in value)
            if (char.IsControl(c) || char.IsSurrogate(c) || c is '<' or '>' or '\u2028' or '\u2029'
                || char.GetUnicodeCategory(c) == UnicodeCategory.Format)
                return false;
        return true;
    }

    internal static bool IsCommand(string command)
    {
        return command.Length != 0 && IsSafeText(command) && Encoding.UTF8.GetByteCount(command) <= 256;
    }
}
