// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

namespace Modstoke.Client;

// Accept only the bounded baseline JPEG emitted by the tablet capture camera.
// Check dimensions before invoking Unity's native decoder on network input.
internal static class ClientTabletScreenFrame
{
    internal static bool IsValid(byte[] bytes)
    {
        if (bytes.Length is < 32 or > 24576 || bytes[0] != 0xff || bytes[1] != 0xd8) return false;
        var at = 2;
        var dimensions = false;
        while (at + 3 < bytes.Length)
        {
            if (bytes[at++] != 0xff) return false;
            while (at < bytes.Length && bytes[at] == 0xff) at++;
            if (at + 2 >= bytes.Length) return false;
            var marker = bytes[at++];
            var size = (bytes[at] << 8) | bytes[at + 1];
            if (size < 2 || size > bytes.Length - at) return false;
            if (marker == 0xc0)
            {
                if (dimensions || size != 17 || bytes[at + 2] != 8
                    || ((bytes[at + 3] << 8) | bytes[at + 4]) != 320
                    || ((bytes[at + 5] << 8) | bytes[at + 6]) != 512 || bytes[at + 7] != 3) return false;
                dimensions = true;
            }
            else if (marker == 0xda)
            {
                if (!dimensions || size != 12 || bytes[at + 2] != 3) return false;
                at += size;
                while (at < bytes.Length)
                {
                    if (bytes[at++] != 0xff) continue;
                    while (at < bytes.Length && bytes[at] == 0xff) at++;
                    if (at >= bytes.Length) return false;
                    var next = bytes[at++];
                    if (next == 0 || next is >= 0xd0 and <= 0xd7) continue;
                    return next == 0xd9 && at == bytes.Length;
                }
                return false;
            }
            else if (marker is not (0xc4 or 0xdb or 0xdd or 0xfe) && marker is not (>= 0xe0 and <= 0xef))
                return false;
            at += size;
        }
        return false;
    }
}
