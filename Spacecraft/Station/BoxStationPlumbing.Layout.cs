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

internal sealed partial class BoxStationPlumbing
{
    private const string PipePath = "/obj/machinery/atmospherics/pipe/";
    private const string BinaryPath = "/obj/machinery/atmospherics/components/binary/";
    private const string UnaryPath = "/obj/machinery/atmospherics/components/unary/";
    private const string FilterPath = "/obj/machinery/atmospherics/components/trinary/filter";
    private const string MixerPath = "/obj/machinery/atmospherics/components/trinary/mixer";
    private const string ThermalPath = UnaryPath + "thermomachine/";
    private const string HeatExchangePath = PipePath + "heat_exchanging/";
    private static readonly int[] Cardinals = { 1, 2, 4, 8 };
    private static readonly ConditionalWeakTable<BoxStationLayout, Definitions> LayoutDefinitions = new();
    private readonly record struct Node(int Port, int Cell, int Directions, int Layers, double Volume, byte Tank,
        int HeatExchangeDirections = 0);
    private readonly record struct Tank(int Network, double Volume, bool Plasma);
    private readonly record struct FlowDefinition(int Input, int Output, double PortVolume,
        double TargetPressure, int PressureChecks, double ExternalBound, double InternalBound,
        bool Releasing, bool Scrubbing, double SampleVolume, bool Welded, bool Connected, BoxStationPowerChannel Channel,
        int Third = -1, StationGasSpecies FilterSpecies = 0, ushort FilterLitresPerSecond = 0,
        ushort MixerFirstBasisPoints = 0, float ThermalTargetKelvin = 0, ushort InjectorLitresPerSecond = 0);

    private sealed class Definitions
    {
        internal required ImmutableArray<BoxStationPlumbingDevice> Devices { get; init; }
        internal required ImmutableArray<BoxStationRadiator> Radiators { get; init; }
        internal required FlowDefinition[] Flow { get; init; }
        internal required double[] Volumes { get; init; }
        internal required int[] NetworkPorts { get; init; }
        internal required bool[] InitialEnabled { get; init; }
        internal required Tank[] Tanks { get; init; }
        internal required Node[] Nodes { get; init; }
        internal required int[] NodeNetworks { get; init; }
        internal required int[][] AirReceivers { get; init; }
    }

    internal static ImmutableArray<BoxStationPlumbingDevice> GetDevices(BoxStationLayout layout)
        => LayoutDefinitions.GetValue(layout, BuildDefinitions).Devices;

    internal static ImmutableArray<BoxStationRadiator> GetRadiators(BoxStationLayout layout)
        => LayoutDefinitions.GetValue(layout, BuildDefinitions).Radiators;

    internal static int PipeLayer(BoxStationObject item, string type)
        => ReadInteger(item, "piping_layer", HasSegment(type, "layer1") ? 1 : HasSegment(type, "layer3") ? 3 : 2, 1, 3);

    private static Definitions BuildDefinitions(BoxStationLayout layout)
        => BuildDefinitions(layout, includeFilters: true, includeMixers: true, includeThermal: true, includeRadiators: true, includeInjectors: true);

    private static Definitions BuildDefinitions(BoxStationLayout layout, bool includeFilters, bool includeMixers,
        bool includeThermal, bool includeRadiators, bool includeInjectors)
    {
        var nodes = new List<Node>();
        var devices = ImmutableArray.CreateBuilder<BoxStationPlumbingDevice>();
        var radiators = ImmutableArray.CreateBuilder<BoxStationRadiator>();
        var flow = new List<FlowDefinition>();
        var enabled = new List<bool>();
        foreach (var item in layout.Objects)
        {
            string type = layout.Types[item.Type];
            bool simple = IsType(type, PipePath + "simple");
            bool manifold = IsType(type, PipePath + "manifold");
            bool fourway = IsType(type, PipePath + "manifold4w");
            bool adapter = IsType(type, PipePath + "layer_manifold");
            bool tank = IsType(type, UnaryPath + "tank");
            bool valve = IsType(type, BinaryPath + "valve");
            bool pump = IsType(type, BinaryPath + "pump");
            bool vent = IsType(type, UnaryPath + "vent_pump");
            bool scrubber = IsType(type, UnaryPath + "vent_scrubber");
            bool filter = includeFilters && IsType(type, FilterPath);
            bool mixer = includeMixers && IsType(type, MixerPath);
            bool heater = includeThermal && IsType(type, ThermalPath + "heater");
            bool freezer = includeThermal && IsType(type, ThermalPath + "freezer");
            bool radiator = includeRadiators && IsType(type, HeatExchangePath + "simple");
            bool junction = includeRadiators && IsType(type, HeatExchangePath + "junction");
            bool injector = includeInjectors && IsType(type, UnaryPath + "outlet_injector");
            // Unsupported components contribute no connection, gas, or implicit bypass.
            if (!simple && !manifold && !fourway && !adapter && !tank && !valve && !pump && !vent && !scrubber
                && !filter && !mixer && !heater && !freezer && !radiator && !junction && !injector) continue;
            int direction = item.Direction == 0 ? 2 : item.Direction;
            int layer = PipeLayer(item, type);
            int layers = 1 << (layer - 1);
            if (radiator || junction)
            {
                int mask = Axis(direction);
                if (mask == 0 || junction && !IsCardinal(direction)) throw InvalidPlumbing(item, "invalid heat-exchange ports");
                int node = nodes.Count;
                nodes.Add(new(item.Id * 2, item.Cell, mask, layers, .070, 0, junction ? direction : mask));
                if (radiator) radiators.Add(new(item.Id, item.Cell, node, direction, layer));
                continue;
            }
            if (simple || manifold || fourway || adapter)
            {
                if (!simple && !fourway && !IsCardinal(direction)) throw InvalidPlumbing(item, "invalid pipe direction");
                int mask = simple ? Axis(direction) : manifold ? 15 & ~direction : fourway ? 15 : Axis(direction);
                if (mask == 0) throw InvalidPlumbing(item, "invalid pipe ports");
                if (adapter)
                {
                    if (layer != 2) throw InvalidPlumbing(item, "invalid layer adaptor layer");
                    layers = 7;
                }
                double volume = adapter ? .260 : fourway ? .140 : manifold ? .105 : .070;
                nodes.Add(new(item.Id * 2, item.Cell, mask, layers, volume, 0));
                continue;
            }
            if (!IsCardinal(direction)) throw InvalidPlumbing(item, "non-cardinal component direction");
            if (tank)
            {
                byte kind = type == UnaryPath + "tank/air" ? (byte)1
                    : type == UnaryPath + "tank/toxins" ? (byte)2
                    : throw InvalidPlumbing(item, "unsupported pressure tank contents");
                double volume = ReadNumber(item, "volume", 10_000, 100_000) / 1000;
                if (volume <= 0) throw InvalidPlumbing(item, "empty pressure tank volume");
                // Unary SetInitDirections sets the effective port from dir during
                // initialization; a raw initialize_directions field is not a second port.
                _ = ReadInteger(item, "initialize_directions", direction, 1, 15);
                nodes.Add(new(item.Id * 2, item.Cell, direction, layers, volume, kind));
                continue;
            }

            if (injector)
            {
                double rate = ReadNumber(item, "volume_rate", HasSegment(type, "atmos") ? 200 : 50,
                    MaximumInjectorLitresPerSecond * SourceInterval) / SourceInterval;
                if (rate != Math.Truncate(rate)) throw InvalidPlumbing(item, "unrepresentable injector volume_rate");
                _ = ReadInteger(item, "initialize_directions", direction, 1, 15);
                int node = nodes.Count;
                nodes.Add(new(item.Id * 2, item.Cell, direction, layers, TransportPortVolume, 0));
                devices.Add(new(item.Id, item.Cell, layout.Cells[item.Cell].Area, direction, layer, BoxStationPlumbingKind.Injector, true));
                enabled.Add(ReadBoolean(item, "on", HasSegment(type, "on") || HasSegment(type, "atmos")));
                flow.Add(new(node, -1, TransportPortVolume, 0,
                    0, 0, 0, true, false, 0, false, false, BoxStationPowerChannel.Environment,
                    InjectorLitresPerSecond: (ushort)rate));
                continue;
            }

            if (heater || freezer)
            {
                var kind = heater ? BoxStationPlumbingKind.Heater : BoxStationPlumbingKind.Freezer;
                var range = ThermalRange(kind);
                float initialTarget = freezer && HasSegment(type, "on") ? range.Minimum : NeutralThermalKelvin;
                if (freezer && HasSegment(type, "coldroom")) initialTarget = 259.15f;
                // Check thermostat endpoints after conversion so decimal source values round identically.
                float thermalTarget = (float)ReadNumber(item, "target_temperature", initialTarget, 1000);
                if (!ValidThermalTarget(kind, thermalTarget)) throw InvalidPlumbing(item, "invalid target_temperature");
                _ = ReadInteger(item, "initialize_directions", direction, 1, 15);
                int node = nodes.Count;
                nodes.Add(new(item.Id * 2, item.Cell, direction, layers, ThermalPortVolume, 0));
                devices.Add(new(item.Id, item.Cell, layout.Cells[item.Cell].Area, direction, layer, kind, true));
                enabled.Add(ReadBoolean(item, "on", HasSegment(type, "on")));
                flow.Add(new(node, node, ThermalPortVolume, 0,
                    0, 0, 0, false, false, 0, false, false, BoxStationPowerChannel.Environment,
                    ThermalTargetKelvin: thermalTarget));
                continue;
            }

            if (filter || mixer)
            {
                bool flipped = ReadBoolean(item, "flipped", HasSegment(type, "flipped"));
                int inputNode = nodes.Count, thirdNode = inputNode + 1, outputNode = inputNode + 2;
                nodes.Add(new(item.Id * 2, item.Cell, flipped ? direction : Opposite(direction), layers, .2, 0));
                nodes.Add(new(item.Id * 2 + 1, item.Cell, Clockwise(direction), layers, .2, 0));
                // The third port occupies a separate range; +2 would collide with
                // the next source object's existing v1 port identity.
                nodes.Add(new(layout.Objects.Length * 2 + item.Id, item.Cell,
                    flipped ? Opposite(direction) : direction, layers, mixer ? .3 : .2, 0));
                devices.Add(new(item.Id, item.Cell, layout.Cells[item.Cell].Area, direction, layer,
                    mixer ? BoxStationPlumbingKind.Mixer : BoxStationPlumbingKind.Filter, true, flipped));
                bool airmix = mixer && HasSegment(type, "airmix");
                enabled.Add(ReadBoolean(item, "on", HasSegment(type, "on") || filter && HasSegment(type, "atmos") || airmix));
                if (mixer)
                {
                    double pressure = ReadNumber(item, "target_pressure", airmix ? MaximumMixerPressure : Atmosphere,
                        MaximumMixerPressure);
                    flow.Add(new(inputNode, outputNode, .2, pressure,
                        0, 0, 0, false, false, 0, false, false, BoxStationPowerChannel.Environment,
                        thirdNode, MixerFirstBasisPoints: ReadMixerRatio(item, type)));
                    continue;
                }
                double rate = ReadNumber(item, "transfer_rate", MaximumFilterLitresPerSecond * SourceInterval,
                    MaximumFilterLitresPerSecond * SourceInterval) / SourceInterval;
                if (rate != Math.Truncate(rate)) throw InvalidPlumbing(item, "unrepresentable filter transfer_rate");
                flow.Add(new(inputNode, outputNode, .2, FilterOutputPressure,
                    0, 0, 0, false, false, 0, false, false, BoxStationPowerChannel.Environment,
                    thirdNode, ReadFilterSpecies(item, type), (ushort)rate));
                continue;
            }

            bool highVolume = vent && HasSegment(type, "high_volume");
            double portVolume = highVolume ? 1 : .2;
            int first = nodes.Count, second = -1;
            if (valve || pump)
            {
                nodes.Add(new(item.Id * 2, item.Cell, Opposite(direction), layers, portVolume, 0));
                second = nodes.Count;
                nodes.Add(new(item.Id * 2 + 1, item.Cell, direction, layers, portVolume, 0));
            }
            else nodes.Add(new(item.Id * 2, item.Cell, direction, layers, portVolume, 0));

            var kindDevice = valve ? BoxStationPlumbingKind.Valve : pump ? BoxStationPlumbingKind.Pump
                : vent ? BoxStationPlumbingKind.Vent : BoxStationPlumbingKind.Scrubber;
            bool requiresPower = !valve || HasSegment(type, "digital");
            devices.Add(new(item.Id, item.Cell, layout.Cells[item.Cell].Area, direction, layer, kindDevice, requiresPower));
            bool initial = HasSegment(type, "on") || vent && HasSegment(type, "atmos");
            enabled.Add(ReadBoolean(item, "on", initial));
            bool releasing = ReadInteger(item, "pump_direction", HasSegment(type, "siphon") ? 0 : 1, 0, 1) != 0;
            bool scrubbing = ReadBoolean(item, "scrubbing", true);
            int checks = ReadInteger(item, "pressure_checks", HasSegment(type, "siphon") ? 2 : 1, 0, 3);
            double external = ReadNumber(item, "external_pressure_bound", HasSegment(type, "siphon") ? 0 : Atmosphere,
                BoxStationSystems.MaximumPressure);
            double internalBound = ReadNumber(item, "internal_pressure_bound", HasSegment(type, "siphon") ? highVolume ? 2000 : 4000 : 0,
                BoxStationSystems.MaximumPressure);
            double target = ReadNumber(item, "target_pressure", Atmosphere, 50 * Atmosphere);
            double sampleVolume = ReadNumber(item, "volume_rate", 200, 100_000) / 1000;
            bool welded = ReadBoolean(item, "welded", false);
            if (scrubber)
            {
                if (ReadBoolean(item, "widenet", false)) throw InvalidPlumbing(item, "unsupported wide scrubber");
                string? filters = Setting(item, "filter_types");
                if (filters is not null && filters != "list(/datum/gas/carbon_dioxide)")
                    throw InvalidPlumbing(item, "unsupported scrubber filter contents");
            }
            int input = valve || pump || vent && releasing ? first : -1;
            int output = valve || pump ? second : vent && releasing ? -1 : first;
            flow.Add(new(input, output, portVolume, target, checks, external,
                internalBound, releasing, scrubbing, sampleVolume, welded, false,
                highVolume ? BoxStationPowerChannel.Equipment : BoxStationPowerChannel.Environment));
        }

        var parents = new int[nodes.Count];
        var connected = new bool[nodes.Count];
        var ports = new Dictionary<int, int>();
        for (int i = 0; i < nodes.Count; i++)
        {
            parents[i] = i;
            var node = nodes[i];
            for (int layer = 1; layer <= 3; layer++)
            {
                if ((node.Layers & 1 << (layer - 1)) == 0) continue;
                foreach (int direction in Cardinals)
                {
                    if ((node.Directions & direction) == 0) continue;
                    bool heatExchange = (node.HeatExchangeDirections & direction) != 0;
                    int key = PortKey(node.Cell, layer, direction, heatExchange);
                    // Ambiguous co-located endpoints remain isolated rather than
                    // choosing an arbitrary machine or shorting two pipe networks.
                    if (!ports.TryAdd(key, i)) ports[key] = -1;
                }
            }
        }
        for (int i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            for (int layer = 1; layer <= 3; layer++)
            {
                if ((node.Layers & 1 << (layer - 1)) == 0) continue;
                foreach (int direction in Cardinals)
                {
                    bool heatExchange = (node.HeatExchangeDirections & direction) != 0;
                    if ((node.Directions & direction) == 0 || ports[PortKey(node.Cell, layer, direction, heatExchange)] != i) continue;
                    int neighbor = Neighbor(layout, node.Cell, direction);
                    if (neighbor < 0 || !ports.TryGetValue(PortKey(neighbor, layer, Opposite(direction), heatExchange), out int other)
                        || other < 0) continue;
                    // Junctions are HE objects even on their ordinary side, which
                    // accepts only non-HE peers. Two ordinary junction ends cannot mate.
                    if (!heatExchange && node.HeatExchangeDirections != 0 && nodes[other].HeatExchangeDirections != 0) continue;
                    connected[i] = connected[other] = true;
                    int firstRoot = Find(parents, i), secondRoot = Find(parents, other);
                    if (firstRoot != secondRoot) parents[Math.Max(firstRoot, secondRoot)] = Math.Min(firstRoot, secondRoot);
                }
            }
        }

        var roots = new Dictionary<int, int>();
        var volumes = new List<double>();
        var identities = new List<int>();
        var nodeNetworks = new int[nodes.Count];
        var tanks = new List<Tank>();
        for (int i = 0; i < nodes.Count; i++)
        {
            int root = Find(parents, i);
            if (!roots.TryGetValue(root, out int network))
            {
                network = volumes.Count;
                roots.Add(root, network);
                volumes.Add(0);
                identities.Add(nodes[i].Port);
            }
            nodeNetworks[i] = network;
            volumes[network] += nodes[i].Volume;
            if (nodes[i].Tank != 0) tanks.Add(new(network, nodes[i].Volume, nodes[i].Tank == 2));
        }
        for (int i = 0; i < flow.Count; i++)
        {
            var definition = flow[i];
            bool binary = DevicesAreBinary(devices[i]);
            bool trinary = devices[i].Kind is BoxStationPlumbingKind.Filter or BoxStationPlumbingKind.Mixer;
            int unary = definition.Input >= 0 ? definition.Input : definition.Output;
            flow[i] = definition with
            {
                Input = definition.Input >= 0 ? nodeNetworks[definition.Input] : -1,
                Output = definition.Output >= 0 ? nodeNetworks[definition.Output] : -1,
                Third = definition.Third >= 0 ? nodeNetworks[definition.Third] : -1,
                Connected = trinary ? connected[definition.Input] && connected[definition.Third] && connected[definition.Output]
                    : binary || connected[unary]
            };
        }
        for (int i = 0; i < radiators.Count; i++)
            radiators[i] = radiators[i] with { Network = nodeNetworks[radiators[i].Network] };
        var receivers = new List<int>[layout.Areas.Length];
        for (int i = 0; i < receivers.Length; i++) receivers[i] = new();
        for (int i = 0; i < devices.Count; i++)
        {
            if (devices[i].Kind is BoxStationPlumbingKind.Vent or BoxStationPlumbingKind.Scrubber)
                receivers[devices[i].Area].Add(i);
        }
        var airReceivers = new int[receivers.Length][];
        for (int i = 0; i < receivers.Length; i++) airReceivers[i] = receivers[i].ToArray();
        return new()
        {
            Devices = devices.ToImmutable(), Radiators = radiators.ToImmutable(), Flow = flow.ToArray(), Volumes = volumes.ToArray(),
            NetworkPorts = identities.ToArray(), InitialEnabled = enabled.ToArray(), Tanks = tanks.ToArray(),
            Nodes = nodes.ToArray(), NodeNetworks = nodeNetworks,
            AirReceivers = airReceivers
        };
    }

    private void RefreshScrubberNeighbors(BoxStationRooms rooms)
    {
        if (ReferenceEquals(_scrubberRooms, rooms) && _scrubberTopologyRevision == rooms.TopologyRevision) return;
        for (int i = 0; i < Devices.Length; i++)
            if (Devices[i].Kind == BoxStationPlumbingKind.Scrubber)
                _scrubberNeighbors[i] = CountScrubberNeighbors(rooms, Devices[i].Cell);
        _scrubberRooms = rooms;
        _scrubberTopologyRevision = rooms.TopologyRevision;
    }

    private static byte CountScrubberNeighbors(BoxStationRooms rooms, int cell)
    {
        byte count = 0;
        foreach (int direction in Cardinals)
            if (OpenScrubberNeighbor(rooms, cell, direction) >= 0) count++;
        for (int vertical = 1; vertical <= 2; vertical <<= 1)
        {
            int first = OpenScrubberNeighbor(rooms, cell, vertical);
            if (first < 0) continue;
            for (int horizontal = 4; horizontal <= 8; horizontal <<= 1)
            {
                int second = OpenScrubberNeighbor(rooms, cell, horizontal);
                if (second < 0) continue;
                int diagonal = OpenScrubberNeighbor(rooms, first, horizontal);
                if (diagonal >= 0 && diagonal == OpenScrubberNeighbor(rooms, second, vertical)) count++;
            }
        }
        return count;
    }

    private static int OpenScrubberNeighbor(BoxStationRooms rooms, int cell, int direction)
    {
        int edge = direction switch { 1 => 0, 4 => 1, 2 => 2, 8 => 3, _ => -1 };
        // Intake excludes door-controlled edges; diagonals require both open routes.
        return edge >= 0 && rooms.OpenEdge(cell, edge) ? Neighbor(rooms.Layout, cell, direction) : -1;
    }

    private static bool DevicesAreBinary(BoxStationPlumbingDevice device)
        => device.Kind is BoxStationPlumbingKind.Valve or BoxStationPlumbingKind.Pump;

    private static int Find(int[] parents, int node)
    {
        while (parents[node] != node)
        {
            parents[node] = parents[parents[node]];
            node = parents[node];
        }
        return node;
    }

    private static int PortKey(int cell, int layer, int direction, bool heatExchange)
        => ((cell * 3 + layer - 1) * 16 + direction) * 2 + (heatExchange ? 1 : 0);
    private static bool IsType(string type, string parent) => type == parent || type.StartsWith(parent + "/", StringComparison.Ordinal);
    private static bool HasSegment(string type, string segment)
        => type.EndsWith("/" + segment, StringComparison.Ordinal) || type.Contains("/" + segment + "/", StringComparison.Ordinal);
    private static bool IsCardinal(int direction) => direction is 1 or 2 or 4 or 8;
    private static int Axis(int direction) => direction switch { 1 or 2 => 3, 4 or 8 => 12, 5 or 6 or 9 or 10 => direction, _ => 0 };
    private static int Opposite(int direction) => direction switch { 1 => 2, 2 => 1, 4 => 8, 8 => 4, _ => 0 };
    private static int Clockwise(int direction) => direction switch { 1 => 4, 4 => 2, 2 => 8, 8 => 1, _ => 0 };

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
        if (matches.Count > 1) throw InvalidPlumbing(item, "duplicate " + name);
        return matches.Count == 0 ? null : matches[0].Groups[1].Value.Trim();
    }

    private static double ReadNumber(BoxStationObject item, string name, double fallback, double maximum)
    {
        string? setting = Setting(item, name);
        if (setting is null) return fallback;
        if (!double.TryParse(setting, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            || !double.IsFinite(value) || value < 0 || value > maximum) throw InvalidPlumbing(item, "invalid " + name);
        return value;
    }

    private static int ReadInteger(BoxStationObject item, string name, int fallback, int minimum, int maximum)
    {
        double value = ReadNumber(item, name, fallback, maximum);
        if (value < minimum || value != Math.Truncate(value)) throw InvalidPlumbing(item, "invalid " + name);
        return (int)value;
    }

    private static bool ReadBoolean(BoxStationObject item, string name, bool fallback)
    {
        return Setting(item, name) switch
        {
            null => fallback,
            "TRUE" or "1" => true,
            "FALSE" or "0" => false,
            _ => throw InvalidPlumbing(item, "invalid " + name)
        };
    }

    private static StationGasSpecies ReadFilterSpecies(BoxStationObject item, string type)
    {
        string? setting = Setting(item, "filter_type");
        if (setting is not null)
        {
            return setting switch
            {
                "null" or "\"\"" => 0,
                "\"o2\"" or "/datum/gas/oxygen" => StationGasSpecies.Oxygen,
                "\"n2\"" or "/datum/gas/nitrogen" => StationGasSpecies.Nitrogen,
                "\"co2\"" or "/datum/gas/carbon_dioxide" => StationGasSpecies.CO2,
                "\"plasma\"" or "/datum/gas/plasma" => StationGasSpecies.Plasma,
                "\"n2o\"" or "/datum/gas/nitrous_oxide" => StationGasSpecies.N2O,
                "\"water_vapor\"" or "/datum/gas/water_vapor" => StationGasSpecies.WaterVapor,
                "\"tritium\"" or "/datum/gas/tritium" => StationGasSpecies.Tritium,
                "\"bz\"" or "/datum/gas/bz" => StationGasSpecies.BZ,
                _ => throw InvalidPlumbing(item, "unsupported filter_type")
            };
        }
        if (!HasSegment(type, "atmos")) return 0;
        if (HasSegment(type, "o2")) return StationGasSpecies.Oxygen;
        if (HasSegment(type, "n2")) return StationGasSpecies.Nitrogen;
        if (HasSegment(type, "co2")) return StationGasSpecies.CO2;
        if (HasSegment(type, "plasma")) return StationGasSpecies.Plasma;
        if (HasSegment(type, "n2o")) return StationGasSpecies.N2O;
        if (HasSegment(type, "water_vapor")) return StationGasSpecies.WaterVapor;
        if (HasSegment(type, "tritium")) return StationGasSpecies.Tritium;
        if (HasSegment(type, "bz")) return StationGasSpecies.BZ;
        return 0;
    }

    private static ushort ReadMixerRatio(BoxStationObject item, string type)
    {
        double initial = HasSegment(type, "airmix") ? HasSegment(type, "inverse") ? .21 : .79 : .5;
        double first = ReadNumber(item, "node1_concentration", initial, 1);
        double second = ReadNumber(item, "node2_concentration", 1 - initial, 1);
        double basisPoints = Math.Round(first * 10_000);
        if (Math.Abs(first + second - 1) > 1e-12 || Math.Abs(first * 10_000 - basisPoints) > 1e-8)
            throw InvalidPlumbing(item, "invalid mixer concentrations");
        return (ushort)basisPoints;
    }

    private static InvalidDataException InvalidPlumbing(BoxStationObject item, string reason)
        => new($"Station plumbing object {item.Id} has {reason}.");
}
