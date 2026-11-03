// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Modstoke.Client;

internal sealed record OrbitalBody(int Id, string Name, int ParentId, double Mu, double Radius, OrbitalState Initial);
internal sealed record OrbitalChartView(long Tick, double EpochJulianDateTdb,
    IReadOnlyList<OrbitalBody> Bodies, IReadOnlyList<OrbitalState> States);

internal sealed class OrbitalCatalog
{
    // Mean sidereal periods, seconds; shared by surface frames and visual meridians.
    internal const double EarthRotationPeriod = 23.9345 * 3600;
    internal const double MoonRotationPeriod = 655.720 * 3600;
    private static readonly Lazy<OrbitalCatalog> Instance = new(Load);
    internal static OrbitalCatalog Current => Instance.Value;
    internal IReadOnlyList<OrbitalBody> Bodies { get; }
    internal double EpochJulianDateTdb { get; }
    internal string Hash { get; }
    internal byte[] Fingerprint { get; }

    private OrbitalCatalog(CatalogData data, byte[] fingerprint)
    {
        if (data.Version != 1 || data.CatalogId != "jpl-20260101-icrf-v1"
            || data.EpochTdb != "2026-01-01T00:00:00" || data.EpochJulianDateTdb != 2461041.5
            || data.Frame != "ICRF" || data.Center != 0 || data.LengthUnit != "m" || data.TimeUnit != "s"
            || data.RadiusConvention != "volume-equivalent sphere"
            || data.Bodies == null || data.Bodies.Length is < 11 or > OrbitalSimulation.MaximumBodies)
            throw new InvalidDataException("The solar catalog has an unsupported frame, epoch, units or body count.");
        var bodies = new OrbitalBody[data.Bodies.Length];
        var ids = new HashSet<int>();
        for (int i = 0; i < bodies.Length; i++)
        {
            var body = data.Bodies[i];
            if (body == null || body.Id < 10 || !ids.Add(body.Id)
                || string.IsNullOrWhiteSpace(body.Name) || body.Name.Length > 64
                || body.Position == null || body.Velocity == null
                || !double.IsFinite(body.Mu) || body.Mu < 0 || body.Mu > 1e22
                || !double.IsFinite(body.Radius) || body.Radius <= 0 || body.Radius > 1e10)
                throw new InvalidDataException($"Solar catalog entry {i} is invalid.");
            foreach (char character in body.Name)
                if (char.IsControl(character)) throw new InvalidDataException("A solar body name contains a control character.");
            bodies[i] = new(body.Id, body.Name, body.ParentId, body.Mu, body.Radius,
                new(body.Id, body.Position.Vector, body.Velocity.Vector));
        }
        foreach (int required in new[] { 10, 199, 299, 399, 499, 599, 699, 799, 899, 999, 301 })
            if (!ids.Contains(required)) throw new InvalidDataException($"Solar catalog is missing physical body {required}.");
        foreach (var body in bodies)
        {
            if (body.Id == 10 ? body.ParentId != 0 || body.Mu <= 0
                : body.ParentId == body.Id || !ids.Contains(body.ParentId))
                throw new InvalidDataException($"Solar body {body.Id} has an invalid parent.");
        }
        // The solver validates finite states, unique identities and separation before use.
        Bodies = Array.AsReadOnly(bodies);
        EpochJulianDateTdb = data.EpochJulianDateTdb;
        Fingerprint = fingerprint;
        Hash = Convert.ToHexString(fingerprint);
    }

    private static OrbitalCatalog Load()
    {
        using var stream = typeof(OrbitalCatalog).Assembly.GetManifestResourceStream(
            "Modstoke.Client.Assets.spacecraft-solar-system.json")
            ?? throw new InvalidDataException("The solar-system catalog is missing.");
        if (stream.Length is <= 0 or > 1_048_576) throw new InvalidDataException("The solar-system catalog is too large.");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        var data = JsonSerializer.Deserialize<CatalogData>(bytes, new JsonSerializerOptions { MaxDepth = 8 })
            ?? throw new InvalidDataException("The solar-system catalog is empty.");
        return new(data, SHA256.HashData(bytes));
    }

    public sealed class CatalogData
    {
        [JsonRequired] public int Version { get; set; }
        [JsonRequired] public string CatalogId { get; set; } = "";
        [JsonRequired] public string EpochTdb { get; set; } = "";
        [JsonRequired] public double EpochJulianDateTdb { get; set; }
        [JsonRequired] public string Frame { get; set; } = "";
        [JsonRequired] public int Center { get; set; }
        [JsonRequired] public string LengthUnit { get; set; } = "";
        [JsonRequired] public string TimeUnit { get; set; } = "";
        [JsonRequired] public string RadiusConvention { get; set; } = "";
        [JsonRequired] public BodyData[]? Bodies { get; set; }
    }

    public sealed class BodyData
    {
        [JsonRequired] public int Id { get; set; }
        [JsonRequired] public string Name { get; set; } = "";
        [JsonRequired] public int ParentId { get; set; }
        [JsonRequired] public double Mu { get; set; }
        [JsonRequired] public double Radius { get; set; }
        [JsonRequired] public VectorData? Position { get; set; }
        [JsonRequired] public VectorData? Velocity { get; set; }
    }

    public sealed class VectorData
    {
        [JsonRequired] public double X { get; set; }
        [JsonRequired] public double Y { get; set; }
        [JsonRequired] public double Z { get; set; }
        internal OrbitalVector Vector => new(X, Y, Z);
    }
}
