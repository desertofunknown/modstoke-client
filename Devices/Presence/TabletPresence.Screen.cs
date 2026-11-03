// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientTabletPresence
{
    private sealed partial class Remote
    {
        private byte[]? _screenBytes;
        private long _screenSeen;
        private bool _screenPending;
        internal bool CanShareScreen => _phase == ClientTabletPresencePhase.Open && Now - _seen <= Lifetime;

        internal void SetScreen(byte[] frame, long now)
        {
            if (!CanShareScreen) return;
            _screenBytes = frame; _screenSeen = now; _screenPending = true;
        }

        internal void ClearScreen()
        {
            _screenBytes = null; _screenSeen = 0; _screenPending = false;
            Pose?.ClearScreen();
        }

        private void RenderScreen(long now)
        {
            if (!CanShareScreen || now - _screenSeen > 3000) { ClearScreen(); return; }
            if (!_screenPending || Pose == null || _screenBytes == null) return;
            _screenPending = false;
            Pose.SetScreen(_screenBytes);
        }
    }

    private sealed partial class RemotePose
    {
        private Texture2D? _screenTexture;
        private bool _screenVisible, _screenErrorLogged;

        internal void SetScreen(byte[] jpeg)
        {
            if (!Live(_glass) || !ClientTabletScreenFrame.IsValid(jpeg)) return;
            try
            {
                _screenTexture ??= new Texture2D(512, 320, TextureFormat.RGB24, true)
                {
                    name = "Shared tablet display",
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Trilinear,
                    anisoLevel = 4
                };
                if (!ImageConversion.LoadImage(_screenTexture, jpeg, false)
                    || _screenTexture.width != 512 || _screenTexture.height != 320)
                { ClearScreen(); return; }
                _screenVisible = true;
                ClientDeviceScreenMaterial.SetDisplay(_glass, Color.white, _screenTexture);
            }
            catch (Exception error)
            {
                ClearScreen();
                if (_screenErrorLogged) return;
                _screenErrorLogged = true;
                Debug.LogWarning("Modstoke shared tablet display could not decode a frame (" + error.GetType().Name + ").");
            }
        }

        internal void ClearScreen()
        {
            if (!_screenVisible || !Live(_glass)) return;
            ClientDeviceScreenMaterial.SetDisplay(_glass, ClientDeviceScreenMaterial.NeutralColor, null);
            _screenVisible = false;
        }

        private void DisposeScreen()
        {
            ClearScreen();
            if (Live(_screenTexture)) UnityEngine.Object.Destroy(_screenTexture);
            _screenTexture = null;
        }
    }
}
