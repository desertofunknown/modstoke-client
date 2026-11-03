// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftStations
{
    private RectTransform _shoreAirArt = null!;
    private TextMeshProUGUI _shoreAirState = null!, _shoreAirCoupling = null!, _shoreAirFlow = null!;
    private TextMeshProUGUI _shoreAirPower = null!, _shoreAirReserve = null!, _shoreAirRequest = null!, _shoreAirBerth = null!;
    private TextMeshProUGUI _shoreFuelState = null!, _shoreFuelReserve = null!;
    private TextMeshProUGUI _shoreCoolantState = null!, _shoreCoolantReserve = null!;
    private Image _shoreAirReserveFill = null!;

    private void BuildShoreAirPage()
    {
        _shoreAirArt = Node(_engineering.Canvas, "Shore air service", 0, 0, 1280, 440);
        Label(_shoreAirArt, "Shore air", "SHORE AIR", 24, 138, 408, 18, 15, Muted);
        _shoreAirState = Label(_shoreAirArt, "Shore air state", "SYNC", 24, 164, 408, 40, 32, Muted);
        _shoreAirCoupling = Label(_shoreAirArt, "Gas coupling", "COUPLING / SYNC", 24, 216, 408, 26, 19, Muted);
        _shoreAirBerth = Label(_shoreAirArt, "Service berth", "BERTH / SYNC", 24, 254, 408, 26, 19, Muted);
        StationGraphic(_shoreAirArt, "Supply separator", 456, 138, 1, 166, ScreenRule);
        Label(_shoreAirArt, "Transfer rate", "TRANSFER", 488, 138, 392, 18, 15, Muted);
        _shoreAirFlow = Label(_shoreAirArt, "Gas flow", "-- mol/s", 488, 164, 392, 40, 32, Muted);
        Label(_shoreAirArt, "Compressor", "COMPRESSOR", 488, 230, 392, 18, 15, Muted);
        _shoreAirPower = Label(_shoreAirArt, "Compressor draw", "-- kW", 488, 254, 392, 40, 28, Muted);
        StationGraphic(_shoreAirArt, "Storage separator", 912, 138, 1, 166, ScreenRule);
        Label(_shoreAirArt, "Breathing gas reserve", "BREATHING GAS RESERVE", 942, 138, 314, 18, 15, Muted);
        _shoreAirReserve = Label(_shoreAirArt, "Gas reserve level", "--", 942, 164, 314, 60, 44, Muted);
        StationGraphic(_shoreAirArt, "Reserve track", 942, 244, 314, 6, ScreenRule);
        _shoreAirReserveFill = StationGraphic(_shoreAirArt, "Reserve level", 942, 244, 0, 6, Muted);
        StationGraphic(_shoreAirArt, "Service control rule", 24, 318, 1232, 1, ScreenRule);
        Label(_shoreAirArt, "Air request", "AIR REQUEST", 24, 338, 408, 18, 15, Muted);
        _shoreAirRequest = Label(_shoreAirArt, "Requested state", "SYNC", 24, 360, 408, 24, 19, Muted);
        Label(_shoreAirArt, "Fuel service", "FUEL SERVICE", 488, 338, 180, 18, 15, Muted);
        _shoreFuelState = Label(_shoreAirArt, "Fuel service state", "SYNC", 488, 360, 180, 20, 16, Muted);
        _shoreFuelReserve = Label(_shoreAirArt, "Fuel reserve", "--", 488, 386, 180, 26, 22, Muted);
        Label(_shoreAirArt, "Coolant service", "COOLANT SERVICE", 700, 338, 180, 18, 15, Muted);
        _shoreCoolantState = Label(_shoreAirArt, "Coolant service state", "SYNC", 700, 360, 180, 20, 16, Muted);
        _shoreCoolantReserve = Label(_shoreAirArt, "Coolant reserve", "--", 700, 386, 180, 26, 22, Muted);
        AddButton(_engineering, "shore-air", "AIR / SYNC");
    }

    private static bool ShoreAirReady(ClientSpacecraftView view) => view.Ready && view.Available && view.Flight.Systems.Ready;

    private static bool ShoreAirAtBerth(ClientSpacecraftView view) => view.Flight.Docked
        && view.Flight.Region == SpacecraftRegion.Space && view.Flight.Travel == SpacecraftTravel.None;

    private static string ShoreAirState(ClientSpacecraftView view)
    {
        if (!ShoreAirReady(view)) return "SYNC";
        var systems = view.Flight.Systems;
        if (!ShoreAirAtBerth(view)) return systems.ShoreAirEnabled ? "NO SUPPLY" : "OFF";
        if (systems.ShoreAirStatus == ShoreAirStatus.UnsafeGas) return "GAS REJECTED";
        if (systems.ShoreAirStatus == ShoreAirStatus.Full) return "COMPLETE";
        if (!systems.ShoreAirEnabled) return "OFF";
        if (systems.ShoreAirStatus == ShoreAirStatus.NoSupply) return "NO SUPPLY";
        if (!float.IsFinite(systems.ShoreCoupling01) || systems.ShoreCoupling01 < 1) return "CONNECTING";
        return systems.ShoreAirStatus switch
        {
            ShoreAirStatus.Filling => float.IsFinite(systems.ShoreAirMolesPerSecond) && systems.ShoreAirMolesPerSecond > 0
                ? "TRANSFER" : "CONNECTING",
            ShoreAirStatus.NoPower => "POWER HOLD",
            ShoreAirStatus.Cooling => "COOLING HOLD",
            ShoreAirStatus.Waiting => "CONNECTING",
            _ => "OFF"
        };
    }

    private static Color ShoreAirColor(ClientSpacecraftView view) => ShoreAirState(view) switch
    {
        "TRANSFER" or "COMPLETE" => Green,
        "GAS REJECTED" => Critical,
        "SYNC" or "OFF" => Muted,
        _ => Amber
    };

    private void UpdateShoreAirReadouts(ClientSpacecraftView view)
    {
        var ready = ShoreAirReady(view);
        var systems = view.Flight.Systems;
        var atBerth = ready && ShoreAirAtBerth(view);
        var couplingKnown = ready && float.IsFinite(systems.ShoreCoupling01);
        var coupled = atBerth && couplingKnown && systems.ShoreCoupling01 >= 1;
        // Settled transfer readings must not survive a lost berth or stale host state.
        var active = coupled && (systems.ShoreAirEnabled
            || systems.ShoreAirStatus is ShoreAirStatus.Full or ShoreAirStatus.UnsafeGas);
        var flow = active && float.IsFinite(systems.ShoreAirMolesPerSecond) ? Mathf.Max(0, systems.ShoreAirMolesPerSecond) : 0;
        var watts = active ? systems.ShoreAirWatts : 0;
        var color = ShoreAirColor(view);
        _shoreAirState.text = ShoreAirState(view);
        _shoreAirState.color = color;
        _shoreAirCoupling.text = !couplingKnown ? "COUPLING / SYNC" : !atBerth ? "COUPLING / DISCONNECTED"
            : coupled ? "COUPLING / CONNECTED" : "COUPLING / " + Percent(Mathf.Clamp01(systems.ShoreCoupling01));
        _shoreAirCoupling.color = !couplingKnown || !atBerth ? Muted : coupled ? Ink : Amber;
        _shoreAirBerth.text = !ready ? "BERTH / SYNC" : atBerth ? "BERTH / DOCKED" : "BERTH / UNDOCKED";
        _shoreAirBerth.color = atBerth ? Ink : Muted;
        _shoreAirFlow.text = ready ? Number(flow) + " mol/s" : "-- mol/s";
        _shoreAirFlow.color = !ready ? Muted : flow > 0 ? Green : Ink;
        _shoreAirPower.text = ready ? Number(watts * .001f) + " kW" : "-- kW";
        _shoreAirPower.color = ready ? Ink : Muted;
        var reserveKnown = ready && float.IsFinite(systems.OxygenReserve01);
        var reserve = reserveKnown ? Mathf.Clamp01(systems.OxygenReserve01) : 0;
        var reserveColor = !reserveKnown ? Muted : reserve < .1f ? Amber : Ink;
        _shoreAirReserve.text = reserveKnown ? Percent(reserve) : "--";
        _shoreAirReserve.color = reserveColor;
        _shoreAirReserveFill.color = reserveColor;
        _shoreAirReserveFill.rectTransform.sizeDelta = new Vector2(314 * reserve, 6);
        _shoreAirRequest.text = !ready ? "SYNC" : systems.ShoreAirEnabled ? "ON" : "OFF";
        _shoreAirRequest.color = !ready || !systems.ShoreAirEnabled ? Muted : color;
        var fuelStatus = !ready ? "SYNC" : !atBerth ? "DISCONNECTED" : systems.ShoreFuelStatus switch
        {
            ShoreFuelStatus.Waiting => "CONNECTING",
            ShoreFuelStatus.Filling => coupled ? "TRANSFER" : "CONNECTING",
            ShoreFuelStatus.Limit => "RESERVE LIMIT",
            ShoreFuelStatus.NoSupply => "SUPPLY EMPTY",
            ShoreFuelStatus.NoPower => "POWER HOLD",
            ShoreFuelStatus.Unsafe => "INTERLOCK",
            _ => "OFF"
        };
        _shoreFuelState.text = fuelStatus;
        _shoreFuelState.color = !ready || !atBerth || systems.ShoreFuelStatus == ShoreFuelStatus.Off ? Muted
            : systems.ShoreFuelStatus == ShoreFuelStatus.Filling && coupled ? Green : Amber;
        _shoreFuelReserve.text = ready ? Percent(systems.Fuel01) : "--";
        _shoreFuelReserve.color = !ready ? Muted : systems.Fuel01 < .1f ? Amber : Ink;
        _shoreCoolantState.text = !ready ? "SYNC" : !atBerth ? "DISCONNECTED" : systems.ShoreCoolantStatus switch
        {
            ShoreCoolantStatus.Waiting => "CONNECTING",
            ShoreCoolantStatus.Filling => coupled ? "TRANSFER" : "CONNECTING",
            ShoreCoolantStatus.Limit => "RESERVE LIMIT",
            ShoreCoolantStatus.NoSupply => "SUPPLY EMPTY",
            ShoreCoolantStatus.NoPower => "POWER HOLD",
            ShoreCoolantStatus.Unsafe => "INTERLOCK",
            _ => "OFF"
        };
        _shoreCoolantState.color = !ready || !atBerth || systems.ShoreCoolantStatus == ShoreCoolantStatus.Off ? Muted
            : systems.ShoreCoolantStatus == ShoreCoolantStatus.Filling && coupled ? Green : Amber;
        _shoreCoolantReserve.text = ready ? Percent(systems.Coolant01) : "--";
        _shoreCoolantReserve.color = !ready ? Muted : systems.Coolant01 < .1f ? Amber : Ink;
    }
}
