// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Modstoke.Client;

// Atlas coordinates use the artwork's top-left origin. Each frame is retained
// for the lifetime of the screen; drawing a frame never creates Unity assets.
internal sealed class ClientGameSprites : IDisposable
{
    private readonly List<Texture2D> _textures = new(4);
    private readonly Sprite?[] _sprites = new Sprite?[65];

    internal ClientGameSprites()
    {
        try
        {
            Load("arcade-island.png", ClientGameSprite.RunnerIdle, new Rect[]
            {
                new(0,0,344,344), new(344,0,296,344), new(640,0,305,344), new(945,0,309,344),
                new(0,344,344,291), new(344,344,296,291), new(640,344,305,291), new(945,344,309,291),
                new(0,635,328,295), new(328,635,312,295), new(640,635,305,295), new(945,635,309,295),
                new(0,930,328,324), new(328,930,312,324), new(640,930,305,324), new(945,930,309,324)
            });
            Load("arcade-ops.png", ClientGameSprite.OpsIdle, new Rect[]
            {
                new(0,0,344,300), new(344,0,296,300), new(640,0,305,300), new(945,0,309,300),
                new(0,300,344,270), new(344,300,296,270), new(640,300,305,270), new(945,300,309,270),
                new(0,570,344,255), new(344,570,296,255), new(640,570,305,255), new(945,570,309,255),
                new(0,825,344,429), new(344,825,296,429), new(640,825,305,429), new(945,825,309,429)
            });
            Load("arcade-objects.png", ClientGameSprite.Supply, new Rect[]
            {
                new(0,0,328,309), new(328,0,300,309), new(628,0,312,309), new(940,0,314,309),
                new(0,309,328,307), new(328,309,300,307), new(628,309,312,307), new(940,309,314,307),
                new(0,616,328,299), new(328,616,300,299), new(637,633,290,273), new(946,633,284,273),
                new(24,929,293,277), new(339,929,277,277), new(666,998,242,140), new(982,998,240,140)
            });
            Load("arcade-terrain.png", ClientGameSprite.GrassSoil, new Rect[]
            {
                new(32,90,280,88), new(349,65,268,247), new(640,120,302,191), new(945,120,309,191),
                new(0,344,328,253), new(328,344,312,253), new(640,344,302,245), new(945,344,309,245),
                new(0,690,328,208), new(328,690,312,208), new(640,589,302,309), new(950,627,274,253),
                new(0,898,320,356), new(320,898,315,356), new(635,922,310,316), new(945,922,309,316)
            });
        }
        catch { Dispose(); throw; }
    }

    internal Sprite Get(ClientGameSprite id) => _sprites[(int)id]
        ?? throw new InvalidOperationException("Arcade sprite is unavailable: " + id);

    private void Load(string file, ClientGameSprite first, Rect[] frames)
    {
        using var source = typeof(ClientGameSprites).Assembly.GetManifestResourceStream("Modstoke.Client.Assets." + file)
            ?? throw new InvalidOperationException("Arcade artwork is missing: " + file);
        using var bytes = new MemoryStream();
        source.CopyTo(bytes);
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
        {
            name = file, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, anisoLevel = 0
        };
        _textures.Add(texture);
        if (!ImageConversion.LoadImage(texture, bytes.ToArray(), true) || texture.width != 1254 || texture.height != 1254)
            throw new InvalidOperationException("Arcade atlas dimensions do not match its frame layout: " + file);
        for (var i = 0; i < frames.Length; i++)
        {
            var frame = frames[i];
            frame.y = texture.height - frame.y - frame.height;
            var id = (ClientGameSprite)((int)first + i);
            var sprite = Sprite.Create(texture, frame, new Vector2(.5f, .5f), 100, 0, SpriteMeshType.FullRect);
            sprite.name = id.ToString();
            _sprites[(int)id] = sprite;
        }
    }

    public void Dispose()
    {
        for (var i = 0; i < _sprites.Length; i++)
        {
            if (_sprites[i] != null) UnityEngine.Object.Destroy(_sprites[i]);
            _sprites[i] = null;
        }
        foreach (var texture in _textures) UnityEngine.Object.Destroy(texture);
        _textures.Clear();
    }
}
