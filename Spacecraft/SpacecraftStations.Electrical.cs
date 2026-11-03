// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Globalization;
using TMPro;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftStations
{
    private enum EngineeringPage { Operations, Diagnostics, Electrical, Service }

    private RectTransform _electricalArt = null!;
    private TextMeshProUGUI _electricalSupply = null!, _electricalShore = null!, _electricalLoad = null!, _electricalBattery = null!;
    private TextMeshProUGUI _electricalSpares = null!;
    private readonly TextMeshProUGUI[] _electricalStates = new TextMeshProUGUI[6];

    private void BuildElectricalPage()
    {
        _electricalArt = Node(_engineering.Canvas, "Electrical distribution", 0, 0, 1280, 440);
        _electricalSupply = Label(_electricalArt, "Generator output", "APU / SYNC", 24, 124, 230, 24, 19, Muted);
        _electricalShore = Label(_electricalArt, "Shore input", "SHORE / SYNC", 264, 124, 230, 24, 19, Muted);
        _electricalLoad = Label(_electricalArt, "Bus load", "LOAD / SYNC", 504, 124, 230, 24, 19, Muted);
        _electricalBattery = Label(_electricalArt, "Battery flow", "BATTERY / SYNC", 784, 124, 472, 24, 19, Muted);
        Label(_electricalArt, "Circuit column", "CIRCUIT", 24, 152, 300, 18, 13, Muted);
        Label(_electricalArt, "Breaker column", "BREAKER / SUPPLY", 350, 152, 390, 18, 13, Muted);
        _electricalSpares = Label(_electricalArt, "Circuit repair stock", "CIRCUIT SPARES / SYNC  |  REPAIR COST 1", 784, 152, 472, 18, 13, Muted);
        for (var index = 0; index < _electricalStates.Length; index++)
        {
            var circuit = (SpacecraftCircuit)index;
            var y = 174 + index * 34;
            Label(_electricalArt, "Circuit name", SpacecraftElectrical.CircuitName(circuit).ToUpperInvariant(),
                24, y, 300, 30, 19, Ink);
            _electricalStates[index] = Label(_electricalArt, "Circuit state", "SYNC", 350, y, 390, 30, 19, Muted);
            StationGraphic(_electricalArt, "Circuit separator", 24, y + 32, 1232, 1, ScreenRule);
            var command = "circuit-" + index.ToString(CultureInfo.InvariantCulture);
            AddButton(_engineering, command + "-open", "ISOLATE");
            AddButton(_engineering, command + "-repair", "REPAIR (1)");
            AddButton(_engineering, command + "-close", "CLOSE");
        }
    }

    private static bool ElectricalReady(ClientSpacecraftView view)
    {
        var electrical = view.Flight.Systems.Electrical;
        return view.Ready && view.Available && view.Flight.Systems.Ready
            && float.IsFinite(electrical.GenerationKw) && float.IsFinite(electrical.LoadKw)
            && float.IsFinite(electrical.BatteryKw) && float.IsFinite(electrical.ShoreKw);
    }

    private void UpdateElectricalReadouts(ClientSpacecraftView view)
    {
        var ready = ElectricalReady(view);
        var electrical = view.Flight.Systems.Electrical;
        _electricalSupply.text = ready ? "APU " + Number(electrical.GenerationKw) + " kW" : "APU / SYNC";
        _electricalShore.text = ready ? "SHORE " + Number(electrical.ShoreKw) + " kW" : "SHORE / SYNC";
        _electricalLoad.text = ready ? "LOAD " + Number(electrical.LoadKw) + " kW" : "LOAD / SYNC";
        _electricalBattery.text = !ready ? "BATTERY / SYNC"
            : electrical.BatteryKw > 0 ? "BATTERY DISCHARGE " + Number(electrical.BatteryKw) + " kW"
            : electrical.BatteryKw < 0 ? "BATTERY CHARGE " + Number(-electrical.BatteryKw) + " kW" : "BATTERY IDLE / 0.0 kW";
        _electricalSupply.color = _electricalShore.color = _electricalLoad.color = _electricalBattery.color = ready ? Ink : Muted;
        var spares = view.Flight.Systems.CircuitSpares;
        _electricalSpares.text = (ready ? "CIRCUIT SPARES " + spares.ToString(CultureInfo.InvariantCulture)
            + " / " + ClientSpacecraftSystems.CircuitSparesCapacity : "CIRCUIT SPARES / SYNC") + "  |  REPAIR COST 1";
        _electricalSpares.color = !ready ? Muted : spares == 0 ? Amber : Ink;
        for (var index = 0; index < _electricalStates.Length; index++)
        {
            var bit = 1 << index;
            var tripped = (electrical.TrippedMask & bit) != 0;
            var state = !ready ? "SYNC" : tripped ? (electrical.ClosedMask & bit) == 0 ? "TRIPPED / ISOLATED" : "TRIPPED / CLOSE SELECTED"
                : (electrical.ClosedMask & bit) == 0 ? "OPEN" : (electrical.ShedMask & bit) != 0 ? "SHED"
                : SpacecraftElectrical.IsPowered(electrical, (SpacecraftCircuit)index) ? "POWERED" : "STANDBY";
            _electricalStates[index].text = state;
            _electricalStates[index].color = !ready ? Muted : tripped ? Critical
                : state is "OPEN" or "SHED" ? Amber : state == "POWERED" ? Green : Muted;
        }
    }

    private static string GeneratorState(SpacecraftSystemState systems, bool ready) => !ready ? "SYNC"
        : systems.ApuIsolated ? "ISOLATED" : !systems.GeneratorEnabled ? "OFF"
        : !float.IsFinite(systems.Electrical.GenerationKw) ? "SYNC"
        : systems.Electrical.GenerationKw > 0 ? "GENERATING" : "STANDBY";

    private static string ScrubberState(SpacecraftSystemState systems, bool ready) => !ready ? "SYNC"
        : !systems.ScrubberEnabled ? "OFF"
        : !SpacecraftElectrical.IsPowered(systems.Electrical, SpacecraftCircuit.LifeSupport) ? "NO POWER" : "ENABLED";
}
