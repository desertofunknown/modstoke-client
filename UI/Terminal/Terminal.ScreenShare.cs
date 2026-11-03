// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientTerminal
{
    private ClientTabletScreenCapture? _sharedScreenCapture;
    private bool _sharedScreenCaptureFailed;
    private float _nextSharedScreenCapture;

    // Called on the Unity thread after the native pre-late-update presentation.
    internal void UpdateSharedScreenCapture(bool wanted)
    {
        try
        {
            if (!wanted || _sharedScreenCaptureFailed || !_open || _failed || Fullscreen
                || !_tablet.IsOpen || _canvas == null || !_canvas || !_canvas.activeInHierarchy)
            {
                ReleaseSharedScreenCapture();
                return;
            }

            if (_sharedScreenCapture != null && !_sharedScreenCapture.Matches(_canvas))
                ReleaseSharedScreenCapture();
            if (_sharedScreenCaptureFailed) return;
            if (_sharedScreenCapture == null && Time.unscaledTime < _nextSharedScreenCapture) return;
            _sharedScreenCapture ??= new ClientTabletScreenCapture(_canvas);
            _sharedScreenCapture.Update();
            _nextSharedScreenCapture = _sharedScreenCapture.NextCaptureAt;
        }
        catch (Exception error) { FailSharedScreenCapture(error); }
    }

    internal byte[]? TakeSharedScreenFrame() => _sharedScreenCapture?.TakeFrame();

    internal void ReleaseSharedScreenCapture()
    {
        var capture = _sharedScreenCapture;
        _sharedScreenCapture = null;
        try { capture?.Dispose(); }
        catch (Exception error) { FailSharedScreenCapture(error); }
    }

    private void FailSharedScreenCapture(Exception error)
    {
        var report = !_sharedScreenCaptureFailed;
        _sharedScreenCaptureFailed = true;
        ReleaseSharedScreenCapture();
        if (!report) return;
        // A capture or logger failure must never retire the tablet or its presence.
        try { _log?.LogWarning("Tablet screen sharing disabled: " + error); }
        catch { }
    }
}
