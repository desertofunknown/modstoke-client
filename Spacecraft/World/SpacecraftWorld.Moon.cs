// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftWorld
{
    private Vector3[]? _moonVertices;
    private Light? _moonLight;
    private readonly record struct MoonCrater(Vector2 Center, float Radius, float Depth);
    private static readonly MoonCrater[] MoonCraters =
    {
        new(new Vector2(-106, 79), 42, 11),
        new(new Vector2(113, 97), 57, 15),
        new(new Vector2(76, -125), 36, 8),
        new(new Vector2(-131, -87), 28, 7),
        new(new Vector2(-27, 173), 22, 6)
    };

    private void BuildMoon(Shader shader)
    {
        var moon = Child("Moon landing area", _root!.transform);
        moon.transform.position = SpacecraftLayout.MoonOrigin;
        var dust = Lit(shader, "Cold lunar regolith", new Color(.48f, .46f, .43f), .08f);
        dust.SetTexture("_BaseColorMap", CreateRegolithTexture());
        _moonVertices = BuildGround(moon.transform, dust, true);

        var stones = new Geometry();
        for (var i = 0; i < 150; i++)
        {
            var angle = i * 2.399963f;
            var radius = 53 + 153 * Mathf.Sqrt(Hash(i, 401));
            var x = Mathf.Cos(angle) * radius;
            var z = Mathf.Sin(angle) * radius;
            var height = .35f + 2.6f * Hash(i, 403);
            AddColumn(stones, new Vector3(x, MoonHeight(x, z) - .15f, z),
                height * (.55f + .5f * Hash(i, 407)), height, angle, .35f);
        }
        AttachGeometry("Crater ejecta", moon.transform, stones, dust, true);

        var markers = new Geometry();
        for (var i = 0; i < 8; i++)
        {
            var angle = i * Mathf.PI / 4;
            var point = new Vector3(Mathf.Cos(angle) * 32, .02f, Mathf.Sin(angle) * 32);
            AddColumn(markers, point, .22f, .55f, angle, .75f);
        }
        AttachGeometry("Landing perimeter", moon.transform, markers,
            Display("Lunar landing lights", new Color(.18f, .74f, 1)), false, false);

        var light = Child("Moon surface light", moon.transform);
        light.transform.localPosition = new Vector3(-95, 150, 75);
        _moonLight = light.AddComponent<Light>();
        _moonLight.type = LightType.Point;
        _moonLight.color = new Color(.83f, .89f, 1);
        _moonLight.range = 430;
        _moonLight.shadows = LightShadows.None;
        light.AddComponent<HDAdditionalLightData>().intensity = 18000000;
        _moonLight.enabled = false;
    }

    private static float MoonHeight(float x, float z)
    {
        var radius = Mathf.Sqrt(x * x + z * z);
        if (radius <= LandingRadius) return 0;
        var height = 1.8f * Mathf.Sin(x * .027f) * Mathf.Cos(z * .021f)
            + .42f * Mathf.Sin(x * .17f + z * .07f) * Mathf.Cos(z * .13f);
        foreach (var crater in MoonCraters)
        {
            var dx = x - crater.Center.x;
            var dz = z - crater.Center.y;
            var distance = Mathf.Sqrt(dx * dx + dz * dz) / crater.Radius;
            if (distance > 1.5f) continue;
            var rim = Mathf.Exp(-Mathf.Pow((distance - 1) * 7, 2)) * crater.Depth * .42f;
            var floor = -crater.Depth * (1 - Smooth(.30f, .95f, distance));
            height += rim + floor;
        }
        // The authored footprint ends in a broken ridge; its collision remains
        // bounded to the same 220 m landing-area contract as the wetland.
        height += Smooth(179, 220, radius) * (13 + 7 * Mathf.Sin(x * .039f + z * .021f));
        return height * Smooth(LandingRadius, 65, radius);
    }

    private Texture2D CreateRegolithTexture()
    {
        const int size = 256;
        var pixels = new Color32[size * size];
        for (var i = 0; i < pixels.Length; i++)
        {
            var value = (byte)(168 + 61 * Hash(i, 419));
            pixels[i] = new Color32(value, value, value, 255);
        }
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, true, false);
        _textures.Add(texture);
        texture.name = "Lunar regolith grain";
        texture.wrapMode = TextureWrapMode.Repeat;
        texture.filterMode = FilterMode.Trilinear;
        texture.anisoLevel = 8;
        texture.SetPixels32(pixels);
        texture.Apply(true, true);
        return texture;
    }
}
