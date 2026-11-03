// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Modstoke.Client;

internal sealed partial class BoxStationPower
{
    private static readonly ConditionalWeakTable<BoxStationLayout, Definitions> LayoutDefinitions = new();
    private static readonly int[] Cardinals = { 1, 2, 4, 8 };
    private readonly record struct SmesInitial(double Joules, double InputWatts, double OutputWatts, byte Enabled);

    private sealed class Definitions
    {
        internal required ImmutableArray<BoxStationApc> Apcs { get; init; }
        internal required ImmutableArray<BoxStationLight> Lights { get; init; }
        internal required ImmutableArray<BoxStationSolarPanel> SolarPanels { get; init; }
        internal required ImmutableArray<BoxStationSmes> Smes { get; init; }
        internal required SmesInitial[] InitialSmes { get; init; }
        internal required int[] AreaControllers { get; init; }
        internal required int[][] NetworkSmes { get; init; }
        internal required double[] SolarWatts { get; init; }
        internal required double[] LightWatts { get; init; }
        internal required double[] InitialApcJoules { get; init; }
        internal required byte[] InitialChannels { get; init; }
        internal required bool[] ChargeApc { get; init; }
        internal required ImmutableArray<BoxStationCableSource> Cables { get; init; }
        internal required ImmutableArray<BoxStationCableSite> CableSites { get; init; }
        internal required ImmutableArray<byte> InitialCables { get; init; }
        internal required int[] CableIndices { get; init; }
        internal required int[] CableNext { get; init; }
        internal required bool[] SmesCells { get; init; }
        internal required int[] Terminals { get; init; }
        internal required int[] SmesTerminals { get; init; }
        internal required ImmutableArray<(int Object, int Cell)> SolarControllers { get; init; }
    }

    internal static ImmutableArray<BoxStationApc> GetApcs(BoxStationLayout layout)
        => LayoutDefinitions.GetValue(layout, BuildDefinitions).Apcs;

    internal static ImmutableArray<BoxStationLight> GetLights(BoxStationLayout layout)
        => LayoutDefinitions.GetValue(layout, BuildDefinitions).Lights;

    internal static ImmutableArray<BoxStationSmes> GetSmes(BoxStationLayout layout)
        => LayoutDefinitions.GetValue(layout, BuildDefinitions).Smes;

    private static Definitions BuildDefinitions(BoxStationLayout layout)
    {
        var cables = new bool[layout.Cells.Length];
        var smesCells = new bool[cables.Length];
        var terminals = new int[cables.Length];
        var cableSources = ImmutableArray.CreateBuilder<BoxStationCableSource>();
        foreach (var item in layout.Objects)
        {
            string type = layout.Types[item.Type];
            if (type == "/obj/structure/cable")
            {
                if (SettingNumber(item, "cable_layer", 2, 2) != 2
                    || SettingNumber(item, "machinery_layer", 1, 1) != 1)
                    throw InvalidPower(item, "unsupported cable layer");
                cables[item.Cell] = true;
                cableSources.Add(new(item.Id, item.Cell));
            }
            else if (IsType(type, "/obj/structure/cable"))
                throw InvalidPower(item, "unsupported cable subtype");
            else if (IsType(type, "/obj/machinery/power/smes")) smesCells[item.Cell] = true;
            else if (type == "/obj/machinery/power/terminal")
            {
                if (terminals[item.Cell] != 0) throw InvalidPower(item, "duplicate power terminal");
                terminals[item.Cell] = Cardinal(item);
            }
        }

        cableSources.Sort(static (a, b) => a.Object.CompareTo(b.Object));
        var sources = cableSources.ToImmutable();
        var cableSites = ImmutableArray.CreateBuilder<BoxStationCableSite>();
        for (int i = 0; i < sources.Length; i++) cableSites.Add(new(sources[i].Cell, i));
        for (int i = 0; i < layout.Cells.Length; i++)
        {
            var cell = layout.Cells[i];
            if (cables[i] || (cell.Flags & (BoxStationCellFlags.Floor | BoxStationCellFlags.Wall)) != BoxStationCellFlags.Floor) continue;
            string turf = layout.Types[cell.TurfType];
            if (IsType(turf, "/turf/open/floor/plating") || IsType(turf, "/turf/open/floor/plasteel"))
                cableSites.Add(new(i, -1));
        }
        var sites = cableSites.ToImmutable();
        var cableIndices = new int[cables.Length];
        var cableNext = new int[sites.Length];
        Array.Fill(cableIndices, -1);
        for (int i = sites.Length - 1; i >= 0; i--)
        {
            int cell = sites[i].Cell;
            cableNext[i] = cableIndices[cell];
            cableIndices[cell] = i;
        }
        var initialCables = OriginalCables(sources.Length, sites.Length);
        var networks = BuildCableNetworks(layout, sites, cableIndices, cableNext, smesCells, terminals,
            initialCables, out int networkCount);

        var areas = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < layout.Areas.Length; i++) areas.Add(layout.Areas[i], i);
        var areaControllers = new int[layout.Areas.Length];
        Array.Fill(areaControllers, -1);
        var apcs = ImmutableArray.CreateBuilder<BoxStationApc>();
        var lights = ImmutableArray.CreateBuilder<BoxStationLight>();
        var solarPanels = ImmutableArray.CreateBuilder<BoxStationSolarPanel>();
        var smes = ImmutableArray.CreateBuilder<BoxStationSmes>();
        var initialSmes = new List<SmesInitial>();
        var smesTerminals = new List<int>();
        var apcCharge = new List<double>();
        var apcChannels = new List<byte>();
        var chargeApc = new List<bool>();
        var solarWatts = new double[networkCount];
        // Controllers are associated by conductor component, never by the reused id text.
        var solarControllers = new int[networkCount];
        var controllerSources = ImmutableArray.CreateBuilder<(int Object, int Cell)>();
        Array.Fill(solarControllers, -1);
        foreach (var item in layout.Objects)
            if (layout.Types[item.Type] == "/obj/machinery/power/solar_control")
            {
                controllerSources.Add((item.Id, item.Cell));
                int network = networks[item.Cell];
                if (network >= 0 && solarControllers[network] < 0) solarControllers[network] = item.Id;
            }

        foreach (var item in layout.Objects)
        {
            string type = layout.Types[item.Type];
            if (IsType(type, "/obj/machinery/power/apc"))
            {
                double cellUnits = type switch
                {
                    "/obj/machinery/power/apc" => 2500,
                    "/obj/machinery/power/apc/highcap/five_k" => 5000,
                    "/obj/machinery/power/apc/highcap/fifteen_k" => 15000,
                    _ => throw InvalidPower(item, "unsupported APC subtype")
                };
                if (Setting(item, "cell_type") is not null) throw InvalidPower(item, "unsupported APC cell override");
                string? target = Setting(item, "areastring");
                int area = layout.Cells[item.Cell].Area;
                if (target is not null && !areas.TryGetValue(Unquote(target), out area))
                    throw InvalidPower(item, "unknown APC target area");
                if (areaControllers[area] >= 0) throw InvalidPower(item, "duplicate APC target area");
                areaControllers[area] = apcs.Count;
                // APC charge units have no SI duration in the source. The 3D model
                // explicitly assigns 1000 J per unit; SMES uses its separate 20 J rate.
                double capacity = cellUnits * 1000;
                apcs.Add(new(item.Id, item.Cell, area, Cardinal(item), capacity, networks[item.Cell]));
                apcCharge.Add(capacity * SettingNumber(item, "start_charge", 90, 100) / 100);
                bool operating = SettingBoolean(item, "operating", true);
                byte channels = 0;
                bool equipment = SettingChannel(item, "equipment");
                bool lighting = SettingChannel(item, "lighting");
                bool environment = SettingChannel(item, "environ");
                if (operating && equipment) channels |= 1;
                if (operating && lighting) channels |= 2;
                if (operating && environment) channels |= 4;
                apcChannels.Add(channels);
                chargeApc.Add(SettingBoolean(item, "chargemode", true));
            }
            else if (IsType(type, "/obj/machinery/light"))
            {
                if (type != "/obj/machinery/light" && type != "/obj/machinery/light/small")
                    throw InvalidPower(item, "unsupported light subtype");
                // Source LateInitialize resets mapped brightness to the fitting's
                // 8/4 value. Random broken bulbs and emergency cells are not seeded.
                _ = SettingNumber(item, "brightness", type.EndsWith("/small", StringComparison.Ordinal) ? 4 : 8, 100);
                lights.Add(new(item.Id, item.Cell, layout.Cells[item.Cell].Area, Cardinal(item),
                    type == "/obj/machinery/light/small"));
            }
            else if (IsType(type, "/obj/machinery/power/smes"))
            {
                if (type != "/obj/machinery/power/smes" && type != "/obj/machinery/power/smes/engineering")
                    throw InvalidPower(item, "unsupported SMES subtype");
                int terminal = -1;
                foreach (int direction in Cardinals)
                {
                    int next = Neighbor(layout, item.Cell, direction);
                    if (next >= 0 && terminals[next] == Opposite(direction)) { terminal = next; break; }
                }
                double capacity = SettingNumber(item, "capacity", 5e6, 1e8) * 20;
                double charge = SettingNumber(item, "charge", 0, capacity / 20) * 20;
                if (capacity <= 0) throw InvalidPower(item, "empty SMES capacity");
                double inputMaximum = SettingNumber(item, "input_level_max", 200_000, 200_000);
                double outputMaximum = SettingNumber(item, "output_level_max", 200_000, 200_000);
                double input = SettingNumber(item, "input_level", 50_000, inputMaximum);
                double output = SettingNumber(item, "output_level", 50_000, outputMaximum);
                if (input > inputMaximum || output > outputMaximum)
                    throw InvalidPower(item, "SMES setpoint exceeds its maximum");
                byte enabled = 0;
                if (SettingBoolean(item, "input_attempt", true)) enabled |= 1;
                if (SettingBoolean(item, "output_attempt", true)) enabled |= 2;
                // Only explicit map charge is seeded. Parts-derived startup energy
                // and capacity are outside this bounded source-layout adaptation.
                smes.Add(new(item.Id, item.Cell, Cardinal(item), terminal >= 0 ? networks[terminal] : -1,
                    terminal >= 0 ? networks[item.Cell] : -1, capacity, inputMaximum, outputMaximum));
                smesTerminals.Add(terminal);
                initialSmes.Add(new(charge, input, output, enabled));
            }
            else if (type == "/obj/machinery/power/solar")
            {
                int network = networks[item.Cell];
                solarPanels.Add(new(item.Id, item.Cell, network, network >= 0 ? solarControllers[network] : -1));
                if (network >= 0) solarWatts[network] += 1500;
            }
        }

        var lightWatts = new double[apcs.Count];
        foreach (var light in lights)
        {
            int apc = areaControllers[light.Area];
            if (apc >= 0) lightWatts[apc] += light.Watts;
        }
        var networkSmes = new List<int>[networkCount];
        for (int i = 0; i < networkSmes.Length; i++) networkSmes[i] = new();
        for (int i = 0; i < smes.Count; i++)
            if (smes[i].OutputNetwork >= 0) networkSmes[smes[i].OutputNetwork].Add(i);
        var outputs = new int[networkCount][];
        for (int i = 0; i < outputs.Length; i++) outputs[i] = networkSmes[i].ToArray();
        return new()
        {
            Apcs = apcs.ToImmutable(), Lights = lights.ToImmutable(), SolarPanels = solarPanels.ToImmutable(), Smes = smes.ToImmutable(),
            InitialSmes = initialSmes.ToArray(), AreaControllers = areaControllers, NetworkSmes = outputs, SolarWatts = solarWatts,
            LightWatts = lightWatts, InitialApcJoules = apcCharge.ToArray(),
            InitialChannels = apcChannels.ToArray(), ChargeApc = chargeApc.ToArray(),
            Cables = sources, CableSites = sites, InitialCables = initialCables, CableIndices = cableIndices,
            CableNext = cableNext, SmesCells = smesCells, Terminals = terminals,
            SmesTerminals = smesTerminals.ToArray(), SolarControllers = controllerSources.ToImmutable()
        };
    }

    private static bool IsType(string type, string parent)
        => type == parent || type.StartsWith(parent + "/", StringComparison.Ordinal);

    private static int Cardinal(BoxStationObject item)
        => item.Direction == 0 ? 2 : item.Direction is 1 or 2 or 4 or 8
            ? item.Direction : throw InvalidPower(item, "non-cardinal machinery direction");

    private static int Opposite(int direction) => direction switch { 1 => 2, 2 => 1, 4 => 8, 8 => 4, _ => 0 };

    private static int Neighbor(BoxStationLayout layout, int cell, int direction)
    {
        int x = cell % layout.Width, row = cell / layout.Width;
        return direction switch
        {
            1 => row > 0 ? cell - layout.Width : -1,
            2 => row + 1 < layout.Height ? cell + layout.Width : -1,
            4 => x + 1 < layout.Width ? cell + 1 : -1,
            8 => x > 0 ? cell - 1 : -1,
            _ => -1
        };
    }

    private static string? Setting(BoxStationObject item, string name)
    {
        var matches = Regex.Matches(item.Raw, @"(?:^|[;{])\s*" + Regex.Escape(name) + @"\s*=\s*([^;}]*)", RegexOptions.CultureInvariant);
        if (matches.Count > 1) throw InvalidPower(item, "duplicate " + name);
        return matches.Count == 0 ? null : matches[0].Groups[1].Value.Trim();
    }

    private static string Unquote(string value)
        => value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value.Substring(1, value.Length - 2) : value;

    private static double SettingNumber(BoxStationObject item, string name, double fallback, double maximum)
    {
        string? setting = Setting(item, name);
        if (setting is null) return fallback;
        if (!double.TryParse(setting, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            || !double.IsFinite(value) || value < 0 || value > maximum) throw InvalidPower(item, "invalid " + name);
        return value;
    }

    private static bool SettingBoolean(BoxStationObject item, string name, bool fallback)
    {
        string? setting = Setting(item, name);
        return setting switch
        {
            null => fallback,
            "TRUE" or "1" => true,
            "FALSE" or "0" => false,
            _ => throw InvalidPower(item, "invalid " + name)
        };
    }

    private static bool SettingChannel(BoxStationObject item, string name)
    {
        double value = SettingNumber(item, name, 3, 3);
        if (value != Math.Truncate(value)) throw InvalidPower(item, "invalid " + name);
        return value >= 2;
    }

    private static InvalidDataException InvalidPower(BoxStationObject item, string reason)
        => new($"Station power object {item.Id} has {reason}.");
}
