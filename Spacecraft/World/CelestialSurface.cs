// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using UnityEngine;

namespace Modstoke.Client;

internal sealed class CelestialSurface
{
    private const int LinearSteps = 65535;
    private static readonly double[] LinearChannels = CreateLinearChannels();
    private static readonly byte[] SrgbChannels = CreateSrgbChannels();
    private readonly Color32[] _albedo;
    private readonly Color32[] _output;
    private readonly double[] _longitudeSin;
    private readonly double[] _longitudeCos;
    private readonly double[] _latitudeSin;
    private readonly double[] _latitudeCos;

    // Takes ownership of the albedo buffer; callers must not modify it afterward.
    internal CelestialSurface(Color32[] albedo, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(albedo);
        if (height < 1 || height > 1024)
            throw new ArgumentOutOfRangeException(nameof(height));
        if (width != height * 2)
            throw new ArgumentOutOfRangeException(nameof(width), "Celestial surfaces must have a 2:1 aspect ratio.");
        if (albedo.Length != width * height)
            throw new ArgumentException("Albedo length must match the surface dimensions.", nameof(albedo));

        _albedo = albedo;
        _output = new Color32[albedo.Length];
        _longitudeSin = new double[width];
        _longitudeCos = new double[width];
        _latitudeSin = new double[height];
        _latitudeCos = new double[height];
        for (var column = 0; column < width; column++)
        {
            var longitude = (column + .5) * 2 * Math.PI / width;
            // East increases to the right in the map; the sphere maps it toward negative Z.
            _longitudeSin[column] = -Math.Sin(longitude);
            _longitudeCos[column] = Math.Cos(longitude);
        }
        for (var row = 0; row < height; row++)
        {
            // GetPixels32 starts at the bottom; the sphere's v is 1 - theta / pi.
            var latitude = (1 - (row + .5) / height) * Math.PI;
            _latitudeSin[row] = Math.Sin(latitude);
            _latitudeCos[row] = Math.Cos(latitude);
        }
    }

    // Fill rows serially with the same sphere-local unit sunlight direction for a complete bake.
    // Upload only after all rows are filled, before starting the next bake; the output is reused.
    internal Color32[] ShadeRows(OrbitalVector sunlight, int firstRow, int rowCount)
    {
        if (!sunlight.IsFinite || Math.Abs(sunlight.LengthSquared - 1) > .000001)
            throw new ArgumentException("Sunlight must be a finite unit direction.", nameof(sunlight));
        if (firstRow < 0 || firstRow > _latitudeSin.Length)
            throw new ArgumentOutOfRangeException(nameof(firstRow));
        if (rowCount < 0 || rowCount > _latitudeSin.Length - firstRow)
            throw new ArgumentOutOfRangeException(nameof(rowCount));

        var width = _longitudeSin.Length;
        var endRow = firstRow + rowCount;
        for (var row = firstRow; row < endRow; row++)
        {
            var radialX = _latitudeSin[row] * sunlight.X;
            var radialZ = _latitudeSin[row] * sunlight.Z;
            var vertical = _latitudeCos[row] * sunlight.Y;
            var offset = row * width;
            for (var column = 0; column < width; column++)
            {
                var diffuse = Math.Max(0, radialX * _longitudeCos[column]
                    + vertical + radialZ * _longitudeSin[column]);
                var lighting = Math.Min(1, .002 + diffuse);
                var albedo = _albedo[offset + column];
                _output[offset + column] = new Color32
                {
                    r = Encode(LinearChannels[albedo.r] * lighting),
                    g = Encode(LinearChannels[albedo.g] * lighting),
                    b = Encode(LinearChannels[albedo.b] * lighting),
                    a = albedo.a
                };
            }
        }
        return _output;
    }

    private static byte Encode(double linear) => SrgbChannels[(int)(linear * LinearSteps + .5)];

    private static double[] CreateLinearChannels()
    {
        var channels = new double[256];
        for (var i = 0; i < channels.Length; i++)
        {
            var srgb = i / 255.0;
            channels[i] = srgb <= .04045 ? srgb / 12.92 : Math.Pow((srgb + .055) / 1.055, 2.4);
        }
        return channels;
    }

    private static byte[] CreateSrgbChannels()
    {
        var channels = new byte[LinearSteps + 1];
        for (var i = 0; i < channels.Length; i++)
        {
            var linear = i / (double)LinearSteps;
            var srgb = linear <= .0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1 / 2.4) - .055;
            channels[i] = (byte)(srgb * 255 + .5);
        }
        return channels;
    }
}
