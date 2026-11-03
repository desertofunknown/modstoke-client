// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Vector3 = System.Numerics.Vector3;

namespace Modstoke.Client;

internal sealed partial class BoxStationHullRepair
{
    private long _cableRevision;
    private int _cableBudget, _cableInstalled;

    internal bool TryCutCable(BoxStationPower power, int cableIndex, Vector3 drop, out string result)
    {
        result = "Cable state is unavailable.";
        if (!SynchronizeFloors()) return false;
        if (!CableLedgerMatches(power) || !power.CableInstalled(cableIndex) || _cableInstalled == 0) return false;
        if (!CableFloorAccessible(cableIndex)) { result = "Expose the cable above an intact pressure deck first."; return false; }
        if (!ValidDropPosition(_layout, drop)) { result = "Stand on clear station flooring to recover cable."; return false; }
        if (!TryPrepareMaterialSalvage(BoxStationMaterial.Cable, drop, out int slot, out var salvage, out bool allocate, out result)) return false;
        long revision = _cableRevision + 1;
        int installed = _cableInstalled - 1;
        // Topology allocation and validation complete before the prepared, non-failing inventory commit.
        if (!power.TrySetCable(cableIndex, false)) { result = "Cable could not be cut."; return false; }
        _materialBundles[slot] = salvage;
        if (allocate) _nextMaterialBundleId++;
        _cableInstalled = installed;
        _cableRevision = revision;
        _materialsDirty = true;
        result = "Cable cut. One length recovered.";
        return true;
    }

    internal bool TryInstallCable(BoxStationPower power, int cableIndex, Guid holder, ulong actor, out string result)
    {
        result = "Cable state is unavailable.";
        if (!SynchronizeFloors()) return false;
        if (!CableLedgerMatches(power) || (uint)cableIndex >= _materialDefinitions.CableSiteCount
            || power.CableInstalled(cableIndex) || _cableInstalled >= _materialDefinitions.CableSiteCount
            || _cableInstalled >= _cableBudget) return false;
        if (!CableFloorAccessible(cableIndex)) { result = "Expose the cable above an intact pressure deck first."; return false; }
        int slot = HeldMaterialSlot(holder);
        if (slot < 0) { result = "You are not holding a cable coil."; return false; }
        var bundle = _materialBundles[slot];
        if (bundle.Actor != actor || bundle.Material != BoxStationMaterial.Cable || bundle.Count == 0)
        { result = "You are not holding an available cable coil."; return false; }
        var remaining = bundle.Count == 1 ? default : bundle with { Count = (ushort)(bundle.Count - 1) };
        long revision = _cableRevision + 1;
        int installed = _cableInstalled + 1;
        if (!power.TrySetCable(cableIndex, true)) { result = "Cable could not be installed."; return false; }
        _materialBundles[slot] = remaining;
        _cableInstalled = installed;
        _cableRevision = revision;
        _materialsDirty = true;
        result = "Cable installed. One length used.";
        return true;
    }

    private bool CableLedgerMatches(BoxStationPower power)
        => _materialsInitialized && _cableRevision >= 0 && _cableRevision < long.MaxValue
            && power.CableState.LayoutHash == _layout.LayoutHash && power.CableState.Revision == _cableRevision
            && power.CableInstalledCount == _cableInstalled && _cableBudget == _materialDefinitions.CableBudget;

    private bool CableFloorAccessible(int index)
    {
        var sites = BoxStationPower.GetCableSites(_layout);
        if (index < 0 || index >= sites.Length) return false;
        int cell = sites[index].Cell;
        return IntactFloorDeck(cell) && (BoxStationPower.CableAccessible(_layout, index)
            || FloorCovering(_layout, cell) && FloorRemoved(_floors, cell));
    }

    internal static bool ValidMaterials(BoxStationLayout layout, BoxStationMaterialState state, BoxStationCableState cables)
        => ValidMaterials(layout, state) && BoxStationPower.ValidCableState(layout, cables)
            && state.CableRevision == cables.Revision && state.CableInstalled == BoxStationPower.CountInstalledCables(layout, cables);

    internal static bool ValidMaterials(BoxStationLayout layout, BoxStationMaterialState state,
        BoxStationHullState hull, BoxStationCableState cables)
        => ValidMaterials(layout, state, hull) && BoxStationPower.ValidCableState(layout, cables)
            && state.CableRevision == cables.Revision && state.CableInstalled == BoxStationPower.CountInstalledCables(layout, cables);
}
