// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Modstoke.Client;

internal sealed partial class BoxStationSystems
{
    private int[] _activeLinks = Array.Empty<int>();
    private double[] _breachAreas = Array.Empty<double>();
    private long _roomRevision = -1, _flowHullRevision = -1;

    private bool TryRestoreWall(int target, byte expectedIntegrity, BoxStationWallKind kind)
        => Hull.RestoreWall(target, expectedIntegrity, kind);

    private void SynchronizeRooms()
    {
        if (_roomRevision != _rooms.TopologyRevision)
        {
            _combustionDensity = new float[ZoneCount];
            _occupants = new int[ZoneCount];
            _breachAreas = new double[ZoneCount];
            _roomRevision = _rooms.TopologyRevision;
            _flowHullRevision = -1;
        }
        if (_flowHullRevision == Hull.State.Revision) return;
        var active = new List<int>();
        for (int index = 0; index < _links.Length; index++)
        {
            var link = _links[index];
            if (_rooms.NodeAtCell(link.First) != _rooms.NodeAtCell(link.Second) && LinkBarriersOpen(link))
                active.Add(index);
        }
        _activeLinks = active.ToArray();
        Array.Clear(_breachAreas);
        foreach (var panel in Hull.State.Panels)
        {
            if (panel.Integrity != 0 || !BoxStationHull.ValidPanelTarget(Layout, panel.Target)) continue;
            int node = _rooms.NodeAtCell(panel.Target / 2);
            if (node >= 0) _breachAreas[node] += BoxStationHull.ApertureArea;
        }
        _flowHullRevision = Hull.State.Revision;
    }

    internal static bool TryGetAtmosphere(BoxStationLayout layout, BoxStationState state, int cell,
        out OrbitalStationAtmosphere atmosphere)
    {
        atmosphere = default;
        if ((uint)cell >= (uint)layout.Cells.Length || state.LayoutHash != layout.LayoutHash
            || state.Zones.IsDefault || state.RoomSpans.IsDefault) return false;
        int low = 0, high = state.RoomSpans.Length - 1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            var span = state.RoomSpans[middle];
            if (cell < span.Start) high = middle - 1;
            else if (cell - span.Start >= span.Length) low = middle + 1;
            else
            {
                if ((uint)span.Node >= (uint)state.Zones.Length) return false;
                atmosphere = state.Zones[span.Node];
                return true;
            }
        }
        return false;
    }

    internal static bool ValidAtmosphere(OrbitalStationAtmosphere gas)
        => InRange(gas.Pressure, MaximumPressure) && InRange(gas.OxygenFraction, 1)
            && InRange(gas.CO2Fraction, 1) && InRange(gas.PlasmaFraction, 1) && InRange(gas.N2OFraction, 1)
            && InRange(gas.WaterVaporFraction, 1) && InRange(gas.TritiumFraction, 1) && InRange(gas.BZFraction, 1)
            && InRange(gas.CombustionWattsPerCubicMeter, MaximumCombustionDensity)
            && gas.OxygenFraction + gas.CO2Fraction + gas.PlasmaFraction + gas.N2OFraction
                + gas.WaterVaporFraction + gas.TritiumFraction + gas.BZFraction <= 1.000001f
            && float.IsFinite(gas.TemperatureC) && gas.TemperatureC >= -273.15f
            && gas.TemperatureC <= (float)(MaximumTemperature - 273.15);

    private bool RestoreLegacyGas(SavedBoxStationSystems saved)
    {
        if (saved.Gas?.Length != Layout.Compartments.Length) return false;
        var sources = new StationGas[saved.Gas.Length];
        var remaining = new int[sources.Length];
        var perCell = new double[sources.Length];
        foreach (var cell in Layout.Cells)
            if (cell.Zone >= 0) remaining[cell.Zone]++;
        for (int zone = 0; zone < sources.Length; zone++)
        {
            if (remaining[zone] == 0 || saved.Gas[zone]?.Length != (saved.Version >= 8 ? 9 : 6)
                || !TryRestoreGas(saved.Gas[zone], Layout.Compartments[zone].Volume, out sources[zone])) return false;
            perCell[zone] = sources[zone].Moles / remaining[zone];
        }
        // The last overlap receives every remaining species and joule, including rounding residue.
        foreach (var cell in Layout.Cells)
        {
            if (cell.Zone < 0) continue;
            int node = _rooms.NodeAtCell(cell.Id);
            if (node < 0) return false;
            ref var source = ref sources[cell.Zone];
            source.TransferTo(ref _rooms.Gas(node), --remaining[cell.Zone] == 0 ? source.Moles : perCell[cell.Zone]);
        }
        if (saved.Version >= 14)
        {
            var cavities = saved.Cavities;
            if (cavities == null || cavities.Version is < 1 or > 2 || cavities.LayoutHash != Layout.LayoutHash
                || cavities.Cells == null || cavities.Gas == null || cavities.Gas.Length != cavities.Cells.Length) return false;
            var expected = new List<int>();
            foreach (int cell in BoxStationHull.CavityCells(Layout))
                if (Hull.CavityActive(cell) && (cavities.Version >= 2 || BoxStationHull.WallIndex(Layout, cell) >= 0))
                    expected.Add(cell);
            expected.Sort();
            if (cavities.Cells.Length != expected.Count) return false;
            for (int index = 0; index < expected.Count; index++)
            {
                int cell = expected[index], node = _rooms.NodeAtCell(cell);
                if (cavities.Cells[index] != cell || node < 0 || cavities.Gas[index]?.Length != 9
                    || !TryRestoreGas(cavities.Gas[index], CellVolume, out var gas)) return false;
                gas.TransferTo(ref _rooms.Gas(node), gas.Moles);
            }
        }
        for (int node = 0; node < ZoneCount; node++)
            if (!TryRestoreGas(_rooms.Gas(node).CaptureValues(), _rooms.Gas(node).Volume, out _)) return false;
        return true;
    }
}

internal sealed class SavedBoxStationCavities
{
    [JsonRequired]
    public int Version { get; set; } = 2;
    [JsonRequired]
    public string? LayoutHash { get; set; }
    [JsonRequired]
    public int[]? Cells { get; set; }
    [JsonRequired]
    public double[][]? Gas { get; set; }
}
