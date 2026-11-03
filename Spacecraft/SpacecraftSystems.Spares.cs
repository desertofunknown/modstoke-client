// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftSystems
{
    internal bool LoadStationSpares(BoxStationHullRepair station, Guid holder, ulong actor,
        int expectedBundleId, out string feedback)
    {
        feedback = "Hold steel sheets, line kits or circuit kits to load the repair stores.";
        BoxStationMaterial kind = default;
        bool found = false;
        foreach (var bundle in station.Materials.Bundles)
        {
            if (bundle.Id != expectedBundleId || bundle.Place != BoxStationMaterialPlace.Held
                || bundle.Holder != holder || bundle.Actor != actor) continue;
            kind = bundle.Material;
            found = kind is BoxStationMaterial.Steel or BoxStationMaterial.LineSpare or BoxStationMaterial.CircuitSpare;
            break;
        }
        if (!found) return false;
        int headroom = kind switch
        {
            BoxStationMaterial.Steel => HullPlatesCapacity - _hullPlates,
            BoxStationMaterial.LineSpare => LineSparesCapacity - _lineSpares,
            _ => CircuitSparesCapacity - _circuitSpares
        };
        if (headroom <= 0) { feedback = "The matching repair stores are full."; return false; }
        // The computer gate owns ship headroom until the exact held bundle has been debited.
        if (!station.TryExportSpares(holder, actor, expectedBundleId, kind, headroom, out int taken, out feedback)) return false;
        if (kind == BoxStationMaterial.Steel) _hullPlates += (byte)taken;
        else if (kind == BoxStationMaterial.LineSpare) _lineSpares += (byte)taken;
        else _circuitSpares += (byte)taken;
        feedback = kind == BoxStationMaterial.Steel ? $"Loaded {taken} steel hull plates."
            : $"Loaded {taken} {(kind == BoxStationMaterial.LineSpare ? "line" : "circuit")} repair kits.";
        return true;
    }
}
