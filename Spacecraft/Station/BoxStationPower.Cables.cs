// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Modstoke.Client;

internal readonly record struct BoxStationCableSource(int Object, int Cell);
internal readonly record struct BoxStationCableSite(int Cell, int SourceIndex);
internal readonly record struct BoxStationCableState(string LayoutHash, long Revision, ImmutableArray<byte> Installed);

internal sealed partial class BoxStationPower
{
    private readonly record struct CableConnectivity(ImmutableArray<BoxStationApc> Apcs, ImmutableArray<BoxStationSmes> Smes,
        ImmutableArray<BoxStationSolarPanel> SolarPanels, int[][] NetworkSmes, double[] SolarWatts);

    private CableConnectivity _connectivity;
    private BoxStationCableState _cableState;

    internal ImmutableArray<BoxStationCableSource> Cables => _definitions.Cables;
    internal ImmutableArray<BoxStationCableSite> Sites => _definitions.CableSites;
    internal BoxStationCableState CableState => _cableState;
    internal int CableInstalledCount { get; private set; }

    internal static ImmutableArray<BoxStationCableSource> GetCables(BoxStationLayout layout)
        => LayoutDefinitions.GetValue(layout, BuildDefinitions).Cables;

    internal static ImmutableArray<BoxStationCableSite> GetCableSites(BoxStationLayout layout)
        => LayoutDefinitions.GetValue(layout, BuildDefinitions).CableSites;

    internal static BoxStationCableState InitialCableState(BoxStationLayout layout)
        => new(layout.LayoutHash, 0, LayoutDefinitions.GetValue(layout, BuildDefinitions).InitialCables);

    internal bool CableInstalled(int index)
        => index >= 0 && index < CableSiteCount && CablePresent(_cableState.Installed, index);

    internal bool TrySetCable(int index, bool installed)
    {
        if (_stepping || index < 0 || index >= CableSiteCount || _cableState.Revision == long.MaxValue
            || CableInstalled(index) == installed) return false;
        int mask = 1 << (index & 7);
        byte value = (byte)(installed ? _cableState.Installed[index >> 3] | mask : _cableState.Installed[index >> 3] & ~mask);
        var state = _cableState with { Revision = _cableState.Revision + 1, Installed = _cableState.Installed.SetItem(index >> 3, value) };
        var connectivity = BuildCableConnectivity(_layout, _definitions, state.Installed);
        var solar = new double[connectivity.SolarWatts.Length];
        var remaining = new double[solar.Length];
        CommitCables(state, connectivity, solar, remaining, CableInstalledCount + (installed ? 1 : -1));
        return true;
    }

    internal static bool ValidCableState(BoxStationLayout layout, BoxStationCableState state)
    {
        var definitions = LayoutDefinitions.GetValue(layout, BuildDefinitions);
        int count = definitions.CableSites.Length;
        if (state.LayoutHash != layout.LayoutHash || state.Revision < 0 || state.Installed.IsDefault
            || state.Installed.Length != (count + 7) / 8) return false;
        int used = count & 7;
        if (used != 0 && (state.Installed[^1] & ~((1 << used) - 1)) != 0) return false;
        return state.Revision != 0 || state.Installed.AsSpan().SequenceEqual(definitions.InitialCables.AsSpan());
    }

    internal static bool IsCableInstalled(BoxStationLayout layout, BoxStationCableState state, int index)
        => index >= 0 && index < GetCableSites(layout).Length && !state.Installed.IsDefault
            && (index >> 3) < state.Installed.Length && CablePresent(state.Installed, index);

    internal static int CountInstalledCables(BoxStationLayout layout, BoxStationCableState state)
    {
        int count = GetCableSites(layout).Length, installed = 0;
        if (state.Installed.IsDefault || state.Installed.Length != (count + 7) / 8) return 0;
        for (int i = 0; i < count; i++)
            if (CablePresent(state.Installed, i)) installed++;
        return installed;
    }

    internal static bool CableAccessible(BoxStationLayout layout, int index)
    {
        var sites = GetCableSites(layout);
        return index >= 0 && index < sites.Length
            && IsType(layout.Types[layout.Cells[sites[index].Cell].TurfType], "/turf/open/floor/plating");
    }

    internal static int CableAtCell(BoxStationLayout layout, BoxStationCableState state, int cell, bool installing)
    {
        var definitions = LayoutDefinitions.GetValue(layout, BuildDefinitions);
        if (cell < 0 || cell >= definitions.CableIndices.Length || state.LayoutHash != layout.LayoutHash
            || state.Installed.IsDefault || state.Installed.Length != (definitions.CableSites.Length + 7) / 8) return -1;
        for (int index = definitions.CableIndices[cell]; index >= 0; index = definitions.CableNext[index])
            if (CablePresent(state.Installed, index) != installing) return index;
        return -1;
    }

    internal static bool IsCableCellInstalled(BoxStationLayout layout, BoxStationCableState state, int cell)
        => CableAtCell(layout, state, cell, false) >= 0;

    internal static bool CableEdge(BoxStationLayout layout, int cell, int neighbor)
    {
        var definitions = LayoutDefinitions.GetValue(layout, BuildDefinitions);
        return CableEdge(layout, cell, neighbor, definitions.CableIndices, definitions.SmesCells, definitions.Terminals);
    }

    private static bool CableEdge(BoxStationLayout layout, int cell, int neighbor, int[] indices, bool[] smes, int[] terminals)
    {
        if (cell < 0 || cell >= indices.Length || neighbor < 0 || neighbor >= indices.Length
            || indices[cell] < 0 || indices[neighbor] < 0) return false;
        bool adjacent = false;
        foreach (int direction in Cardinals)
            if (Neighbor(layout, cell, direction) == neighbor) { adjacent = true; break; }
        // SMES input terminals and output bodies must not become a direct conductor bridge.
        return adjacent && !(smes[cell] && terminals[neighbor] != 0 || terminals[cell] != 0 && smes[neighbor]);
    }

    private static bool CablePresent(ImmutableArray<byte> installed, int index)
        => index >= 0 && (installed[index >> 3] & 1 << (index & 7)) != 0;

    private static bool CablePresentAtCell(ImmutableArray<byte> installed, int[] indices, int[] next, int cell)
    {
        // Same-layer source duplicates keep separate material units but share the cell's conductor connections.
        for (int index = indices[cell]; index >= 0; index = next[index])
            if (CablePresent(installed, index)) return true;
        return false;
    }

    private static ImmutableArray<byte> OriginalCables(int sourceCount, int siteCount)
    {
        var installed = new byte[(siteCount + 7) / 8];
        Array.Fill(installed, byte.MaxValue, 0, sourceCount / 8);
        if ((sourceCount & 7) != 0) installed[sourceCount >> 3] = (byte)((1 << (sourceCount & 7)) - 1);
        return ImmutableArray.CreateRange(installed);
    }

    private static CableConnectivity OriginalConnectivity(Definitions definitions)
        => new(definitions.Apcs, definitions.Smes, definitions.SolarPanels, definitions.NetworkSmes, definitions.SolarWatts);

    private bool TryRestoreCableState(BoxStationPowerSave saved, out BoxStationCableState state)
    {
        state = default;
        if (saved.Version < 4 ? saved.HasAddedCableCells
            : saved.AddedCableCells is null || saved.AddedCableCells.Length > CableSiteCount - CableCount) return false;
        if (saved.Version < 3)
        {
            if (saved.HasCableRevision || saved.HasRemovedCables) return false;
            state = InitialCableState(_layout);
            return true;
        }
        if (!saved.HasCableRevision || saved.CableRevision < 0 || saved.RemovedCables is null
            || saved.RemovedCables.Length > CableCount || saved.CableRevision == 0 && saved.RemovedCables.Length != 0) return false;
        var installed = _definitions.InitialCables.ToBuilder();
        int source = 0, previous = -1;
        foreach (int removed in saved.RemovedCables)
        {
            if (removed <= previous) return false;
            previous = removed;
            while (source < Cables.Length && Cables[source].Object < removed) source++;
            if (source >= Cables.Length || Cables[source].Object != removed) return false;
            installed[source >> 3] = (byte)(installed[source >> 3] & ~(1 << (source & 7)));
        }
        if (saved.Version >= 4)
        {
            previous = -1;
            foreach (ushort cell in saved.AddedCableCells!)
            {
                if (cell <= previous || cell >= _definitions.CableIndices.Length) return false;
                previous = cell;
                int index = _definitions.CableIndices[cell];
                if (index < 0 || Sites[index].SourceIndex >= 0) return false;
                installed[index >> 3] = (byte)(installed[index >> 3] | 1 << (index & 7));
            }
        }
        state = new(_layout.LayoutHash, saved.CableRevision, installed.ToImmutable());
        return ValidCableState(_layout, state);
    }

    private static int[] BuildCableNetworks(BoxStationLayout layout, ImmutableArray<BoxStationCableSite> sites,
        int[] indices, int[] nextCable, bool[] smes, int[] terminals, ImmutableArray<byte> installed, out int count)
    {
        var networks = new int[layout.Cells.Length];
        Array.Fill(networks, -1);
        var queue = new int[sites.Length];
        count = 0;
        for (int cell = 0; cell < networks.Length; cell++)
        {
            if (!CablePresentAtCell(installed, indices, nextCable, cell) || networks[cell] >= 0) continue;
            int head = 0, tail = 1;
            queue[0] = cell;
            networks[cell] = count;
            while (head < tail)
            {
                int current = queue[head++];
                foreach (int direction in Cardinals)
                {
                    int next = Neighbor(layout, current, direction);
                    if (next < 0 || networks[next] >= 0 || !CablePresentAtCell(installed, indices, nextCable, next)
                        || !CableEdge(layout, current, next, indices, smes, terminals)) continue;
                    networks[next] = count;
                    queue[tail++] = next;
                }
            }
            count++;
        }
        return networks;
    }

    private static CableConnectivity BuildCableConnectivity(BoxStationLayout layout, Definitions definitions, ImmutableArray<byte> installed)
    {
        var networks = BuildCableNetworks(layout, definitions.CableSites, definitions.CableIndices, definitions.CableNext,
            definitions.SmesCells, definitions.Terminals, installed, out int count);
        var apcs = ImmutableArray.CreateBuilder<BoxStationApc>(definitions.Apcs.Length);
        foreach (var apc in definitions.Apcs) apcs.Add(apc with { Network = networks[apc.Cell] });
        var outputs = new List<int>[count];
        for (int i = 0; i < count; i++) outputs[i] = new();
        var smes = ImmutableArray.CreateBuilder<BoxStationSmes>(definitions.Smes.Length);
        for (int i = 0; i < definitions.Smes.Length; i++)
        {
            var device = definitions.Smes[i];
            int terminal = definitions.SmesTerminals[i];
            device = device with { InputNetwork = terminal < 0 ? -1 : networks[terminal], OutputNetwork = terminal < 0 ? -1 : networks[device.Cell] };
            smes.Add(device);
            if (device.OutputNetwork >= 0) outputs[device.OutputNetwork].Add(i);
        }
        var controllers = new int[count];
        Array.Fill(controllers, -1);
        foreach (var controller in definitions.SolarControllers)
        {
            int network = networks[controller.Cell];
            if (network >= 0 && controllers[network] < 0) controllers[network] = controller.Object;
        }
        var solar = ImmutableArray.CreateBuilder<BoxStationSolarPanel>(definitions.SolarPanels.Length);
        var watts = new double[count];
        foreach (var panel in definitions.SolarPanels)
        {
            int network = networks[panel.Cell];
            solar.Add(panel with { Network = network, Controller = network < 0 ? -1 : controllers[network] });
            if (network >= 0) watts[network] += 1500;
        }
        var networkSmes = new int[count][];
        for (int i = 0; i < count; i++) networkSmes[i] = outputs[i].ToArray();
        return new(apcs.MoveToImmutable(), smes.MoveToImmutable(), solar.MoveToImmutable(), networkSmes, watts);
    }

    private void CommitCables(BoxStationCableState state, CableConnectivity connectivity, double[] solar, double[] remaining, int installed)
    {
        _cableState = state;
        _connectivity = connectivity;
        _solarJoules = solar;
        _networkRemaining = remaining;
        CableInstalledCount = installed;
        Array.Clear(_outputRemaining);
        Array.Clear(_smesInputJoules);
        Array.Clear(_smesOutputJoules);
        Array.Clear(_smesInputWatts);
        Array.Clear(_smesOutputWatts);
        SolarWatts = 0;
        foreach (double watts in connectivity.SolarWatts) SolarWatts += watts * _solarExposure;
        RefreshAvailability();
    }
}
