// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using TMPro;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftStations
{
    private TextMeshProUGUI _overviewStatus = null!;
    private CompartmentReadout _overviewCabin = null!, _overviewAirlock = null!, _overviewEntry = null!;
    private StationMeter _overviewFuel = null!, _overviewAir = null!, _overviewBattery = null!, _overviewCoolant = null!;

    private void BuildEngineeringOverview()
    {
        var station = AddStation("EngineeringOverview", "ENGINEERING / SYSTEM STATUS", EngineeringOverviewPosition,
            EngineeringOverviewRotation, EngineeringScreenWidth, EngineeringScreenHeight, nativeScreen: true);
        station.Prompt.gameObject.SetActive(false);
        _overviewStatus = station.Readout;
        _overviewStatus.rectTransform.anchoredPosition = new Vector2(24, -76);
        _overviewStatus.rectTransform.sizeDelta = new Vector2(1232, 38);
        _overviewStatus.fontSize = 25;
        _overviewStatus.enableWordWrapping = false;
        _overviewStatus.color = Muted;
        _overviewCabin = AddEngineeringCompartment(station.Canvas, "CABIN AIR", 24, 392);
        _overviewAirlock = AddEngineeringCompartment(station.Canvas, "CHAMBER", 444, 392);
        _overviewEntry = AddEngineeringCompartment(station.Canvas, "ENTRY", 864, 392);
        _overviewFuel = AddStationMeter(station.Canvas, "FUEL", 24, 330, 260);
        _overviewAir = AddStationMeter(station.Canvas, "AIR MIX", 340, 330, 260);
        _overviewBattery = AddStationMeter(station.Canvas, "BATTERY", 656, 330, 260);
        _overviewCoolant = AddStationMeter(station.Canvas, "COOLANT", 972, 330, 260);
    }

    private void UpdateEngineeringOverview(ClientSpacecraftView view, SpacecraftComputerReport computer)
    {
        var systems = view.Flight.Systems;
        var ready = view.Ready && view.Available && systems.Ready;
        _overviewStatus.text = ready ? computer.Summary : "SYSTEMS SYNC";
        _overviewStatus.color = ready ? ComputerColor(computer.Mode) : Muted;
        UpdateCompartment(_overviewCabin, systems.CabinPressure, systems.OxygenFraction,
            systems.CO2Fraction, systems.CabinTemperatureC, ready);
        UpdateCompartment(_overviewAirlock, systems.AirlockPressure, systems.AirlockOxygenFraction,
            systems.AirlockCO2Fraction, systems.AirlockTemperatureC, ready);
        UpdateCompartment(_overviewEntry, systems.EntryPressure, systems.EntryOxygenFraction,
            systems.EntryCO2Fraction, systems.EntryTemperatureC, ready);
        UpdateStationMeter(_overviewFuel, systems.Fuel01, ready, systems.Fuel01 < .15f ? Amber : Ink);
        UpdateStationMeter(_overviewAir, systems.OxygenReserve01, ready, systems.OxygenReserve01 < .15f ? Amber : NavigationCyan);
        UpdateStationMeter(_overviewBattery, systems.Battery01, ready, systems.Battery01 < .15f ? Amber : Ink);
        UpdateStationMeter(_overviewCoolant, systems.Coolant01, ready, systems.Coolant01 < .15f ? Amber : NavigationCyan);
    }
}
