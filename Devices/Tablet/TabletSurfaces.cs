// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using UnityEngine;
using UnityEngine.UI;

namespace Modstoke.Client;

// A transparent margin lets texture filtering cover straight edges as well as corners.
internal sealed class ClientTabletSurfaces : IDisposable
{
    private Texture2D? _squareTexture;
    private Texture2D? _roundTexture;
    private Texture2D? _trackTexture;
    private Sprite? _square;
    private Sprite? _round;
    private Sprite? _track;
    private Material? _displayMaterial;

    internal void Apply(Image image, bool rounded = false, bool track = false)
    {
        if (!_displayMaterial)
        {
            // Own the copy; never change the game's shared UI material or its
            // stencil/masking shader. Surface maps belong only to the casing.
            _displayMaterial = new Material(image.defaultMaterial) { name = "Field device UI surface" };
            ClientDeviceScreenMaterial.ConfigureOwnedUiMaterial(_displayMaterial);
        }
        image.material = _displayMaterial;
        if (track)
        {
            _track ??= Make(32, 6f, 10f, out _trackTexture);
            image.sprite = _track;
        }
        else if (rounded)
        {
            _round ??= Make(64, 24f, 28f, out _roundTexture);
            image.sprite = _round;
        }
        else
        {
            _square ??= Make(16, 0f, 4f, out _squareTexture);
            image.sprite = _square;
        }
        image.type = Image.Type.Sliced;
    }

    private static Sprite Make(int side, float radius, float border, out Texture2D texture)
    {
        const float inset = 2f;
        const float feather = 2f;
        texture = new Texture2D(side, side, TextureFormat.RGBA32, true)
        {
            name = "Modstoke tablet surface",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Trilinear,
            anisoLevel = 2
        };
        for (var y = 0; y < side; y++)
        for (var x = 0; x < side; x++)
        {
            var qx = MathF.Abs(x + .5f - side * .5f) - (side * .5f - inset - radius);
            var qy = MathF.Abs(y + .5f - side * .5f) - (side * .5f - inset - radius);
            var dx = MathF.Max(qx, 0f);
            var dy = MathF.Max(qy, 0f);
            var distance = MathF.Sqrt(dx * dx + dy * dy) + MathF.Min(MathF.Max(qx, qy), 0f) - radius;
            var alpha = Math.Clamp(.5f - distance / feather, 0f, 1f);
            texture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
        }
        texture.Apply(true, true);
        // Two texels per canvas unit keep the existing twelve-unit corner radius.
        return Sprite.Create(texture, new Rect(0f, 0f, side, side), new Vector2(.5f, .5f),
            200f, 0, SpriteMeshType.FullRect, new Vector4(border, border, border, border));
    }

    public void Dispose()
    {
        if (_displayMaterial) UnityEngine.Object.Destroy(_displayMaterial);
        _displayMaterial = null;
        if (_square != null) UnityEngine.Object.Destroy(_square);
        if (_round != null) UnityEngine.Object.Destroy(_round);
        if (_track != null) UnityEngine.Object.Destroy(_track);
        if (_squareTexture != null) UnityEngine.Object.Destroy(_squareTexture);
        if (_roundTexture != null) UnityEngine.Object.Destroy(_roundTexture);
        if (_trackTexture != null) UnityEngine.Object.Destroy(_trackTexture);
        _square = _round = _track = null;
        _squareTexture = _roundTexture = _trackTexture = null;
    }
}
