// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace Modstoke.Client;

internal enum TabletTextRole { Body, Heading, Control, Status, Display }

// Font assets belong to the game; only the tablet's material copies belong here.
internal sealed class ClientTabletTypography : IDisposable
{
    private readonly Dictionary<TabletTextRole, TMP_FontAsset?> _fonts = new();
    private readonly Dictionary<int, Material> _materials = new();

    internal ClientTabletTypography()
    {
        var loaded = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
        TMP_FontAsset? Find(string name)
        {
            foreach (var font in loaded)
                if (font != null && string.Equals(font.name, name, StringComparison.Ordinal)) return font;
            return null;
        }

        var body = Find("Roboto-Regular SDF") ?? Find("RobotoCondensed-Regular SDF") ?? Find("LiberationSans SDF")
            ?? Resources.Load<TMP_FontAsset>("fonts & materials/liberationsans sdf") ?? TMP_Settings.defaultFontAsset;
        var display = Find("Montserrat-Medium SDF") ?? body;
        var heading = Find("RobotoCondensed-Bold SDF") ?? body;
        var status = Find("RobotoCondensed-Regular SDF") ?? body;
        _fonts[TabletTextRole.Body] = body;
        _fonts[TabletTextRole.Heading] = heading;
        _fonts[TabletTextRole.Control] = body;
        _fonts[TabletTextRole.Status] = status;
        _fonts[TabletTextRole.Display] = display;
    }

    internal void Apply(TextMeshProUGUI text, TabletTextRole role)
    {
        var font = _fonts[role];
        if (font != null)
        {
            text.font = font;
            var source = font.material;
            if (source != null)
            {
                var id = font.GetInstanceID();
                if (!_materials.TryGetValue(id, out var material))
                {
                    material = new Material(source) { name = "Modstoke tablet " + font.name };
                    ClientDeviceScreenMaterial.ConfigureOwnedUiMaterial(material);
                    // Keep the atlas, SDF scale and baked font weight. Remove decorative effects only.
                    foreach (var property in new[] { "_OutlineWidth", "_OutlineSoftness", "_FaceDilate" })
                        if (material.HasProperty(property)) material.SetFloat(property, 0f);
                    material.DisableKeyword("OUTLINE_ON");
                    material.DisableKeyword("UNDERLAY_ON");
                    material.DisableKeyword("UNDERLAY_INNER");
                    material.DisableKeyword("GLOW_ON");
                    _materials.Add(id, material);
                }
                text.fontSharedMaterial = material;
            }
        }
        // The heading face is already bold. Synthetic bold thickens small tablet text unevenly.
        text.fontStyle = FontStyles.Normal;
    }

    public void Dispose()
    {
        foreach (var material in _materials.Values)
            if (material != null) UnityEngine.Object.Destroy(material);
        _materials.Clear();
        _fonts.Clear();
    }
}
