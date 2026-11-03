// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftStations
{
    private static readonly Color AuxiliaryBlack = new(.012f, .016f, .019f, 1);

    // Measured inset faces of CockpitMonitors D, E, C (1), A, B and C.
    // The A/E screens are rolled; preserve their face axes, not ship-up.
    private static readonly PanelSurface[] AuxiliarySurfaces =
    {
        new(new(-1.271357f, 1.093557f, 3.905985f), new(.329204f, -.157126f, -.931094f), 573, 318),
        new(new(-.657977f, 1.138862f, 3.875427f), new(.271887f, -.498086f, -.823400f), 323, 225),
        new(new(-.163529f, 1.272265f, 3.932917f), new(-.264226f, -.133294f, -.955205f), 431, 271),
        new(new(1.106068f, .783309f, 3.840530f), new(-.170483f, -.252467f, -.952468f), 521, 350),
        new(new(2.001927f, .541463f, 3.878107f), new(-.653117f, -.243802f, -.716937f), 383, 293),
        new(new(1.840915f, 1.029640f, 3.765571f), new(-.650631f, -.258808f, -.713931f), 431, 271)
    };
    private static readonly Vector3[] AuxiliaryUp =
    {
        new(.052372f, .987579f, -.148141f), new(-.201630f, .807155f, -.554838f),
        new(-.035537f, .991077f, -.128469f), new(-.267205f, .942245f, -.201930f),
        new(-.164198f, .969825f, -.180217f), new(-.174328f, .965929f, -.191288f)
    };

    // SpaceshipCockpitPart: two portrait displays, two octagonal inset faces,
    // and three small rectangular windows. Insets retain the native keys/bezel.
    private static readonly PanelSurface OverheadPowerSurface = new(new(-.341576f, 1.382792f, 5.272781f), new(0, -.812197f, -.583383f), 121, 215);
    private static readonly PanelSurface OverheadReservesSurface = new(new(.353521f, 1.382792f, 5.272781f), new(0, -.812197f, -.583383f), 121, 215);
    private static readonly PanelSurface OverheadRampSurface = new(new(-.608941f, 1.179186f, 5.492957f), new(0, -.576792f, -.816891f), 49, 103);
    private static readonly PanelSurface OverheadGearSurface = new(new(.620887f, 1.179186f, 5.492957f), new(0, -.576792f, -.816891f), 49, 103);
    private static readonly PanelSurface OverheadLegendSurface = new(new(.005973f, 1.381962f, 5.273937f), new(0, -.812197f, -.583383f), 335, 24);
    private static readonly PanelSurface[] OverheadAtmosphereSurfaces =
    {
        new(new(-.088456f, 1.331547f, 5.354528f), new(0, -.812196f, -.583384f), 82, 22),
        new(new(.005973f, 1.331547f, 5.354528f), new(0, -.812196f, -.583384f), 82, 22),
        new(new(.100401f, 1.331548f, 5.354529f), new(0, -.812196f, -.583384f), 82, 22)
    };
    // Cockpit split-mesh face 5997 and its reflected counterpart. These
    // rectangles lie inside the rounded lens, not its larger projected bounds.
    private static readonly PanelSurface[] KeyboardSurfaces =
    {
        new(new(1.623701f, -.431385f, 5.078039f), new(-.637210f, .191563f, -.746503f), 197, 124),
        new(new(-1.831722f, -.431385f, 5.078039f), new(.637210f, .191563f, -.746503f), 197, 124)
    };
    // The schematic's OFF and RAMP UNLOCK plaques are textured status plates,
    // not live controls. Keep its four guarded LOCK handles unassigned.
    private static readonly PanelSurface ChassisGearSurface = new(new(2.453587f, .600986f, 2.395665f), Vector3.left, 96, 36);
    private static readonly PanelSurface ChassisRampSurface = new(new(2.453587f, .174556f, 2.395665f), Vector3.left, 75, 20);
    private static readonly PanelSurface WallSummarySurface = new(new(2.444461f, -.468092f, 2.294966f), Vector3.left, 220, 85);

    private readonly AuxiliaryReading[] _auxiliaryReserves = new AuxiliaryReading[4];
    private readonly AuxiliaryReading[] _auxiliaryThermal = new AuxiliaryReading[4];
    private readonly AuxiliaryReading[] _auxiliaryPressure = new AuxiliaryReading[3];
    private readonly AuxiliaryReading[] _auxiliaryFeeds = new AuxiliaryReading[3];
    private readonly AuxiliaryStatus[] _auxiliaryHardware = new AuxiliaryStatus[6];
    private readonly AuxiliaryReading[] _overheadReserves = new AuxiliaryReading[3];
    private readonly AuxiliaryReading[] _overheadAtmosphere = new AuxiliaryReading[3];
    private readonly AuxiliaryReading[] _keyboardFuel = new AuxiliaryReading[2];
    private readonly AuxiliaryReading[] _keyboardBattery = new AuxiliaryReading[2];
    private readonly AuxiliaryStatus[] _keyboardMode = new AuxiliaryStatus[2];
    private readonly AuxiliaryStatus[] _keyboardSummary = new AuxiliaryStatus[2];
    private AuxiliaryReading _overheadBattery = null!;
    private AuxiliaryStatus _auxiliaryMode = null!, _auxiliarySummary = null!, _auxiliaryAction = null!;
    private AuxiliaryStatus _auxiliaryInnerHatch = null!, _auxiliaryInterlock = null!;
    private AuxiliaryStatus _overheadGenerator = null!, _overheadScrubber = null!;
    private AuxiliaryStatus _overheadRamp = null!, _overheadGear = null!;
    private AuxiliaryStatus _chassisGear = null!, _chassisRamp = null!, _wallMode = null!, _wallSummary = null!;

    private void BuildAuxiliaryDisplays()
    {
        var reserves = AddAuxiliaryScreen(0, "CURRENT RESERVES");
        _auxiliaryReserves[0] = AddAuxiliaryReserve(reserves, "FUEL / %", 22, 61, 248);
        _auxiliaryReserves[1] = AddAuxiliaryReserve(reserves, "AIR MIX / %", 303, 61, 248);
        _auxiliaryReserves[2] = AddAuxiliaryReserve(reserves, "COOLANT / %", 22, 181, 248);
        _auxiliaryReserves[3] = AddAuxiliaryReserve(reserves, "BATTERY / %", 303, 181, 248);
        AuxiliaryLine(reserves, "Reserve columns", new Rect(286, 61, 1, 210));
        AuxiliaryLine(reserves, "Reserve rows", new Rect(22, 164, 529, 1));
        Label(reserves.Canvas, "Reserve scope", "CURRENT LEVELS", 22, 292, 529, 14, 10, Muted);

        var thermal = AddAuxiliaryScreen(1, "STRUCTURE / COOLANT");
        _auxiliaryThermal[0] = AddAuxiliaryReading(thermal, "CABIN HULL / \u00b0C", 22, 53, 279, 23);
        _auxiliaryThermal[1] = AddAuxiliaryReading(thermal, "CHAMBER HULL / \u00b0C", 22, 94, 279, 23);
        _auxiliaryThermal[2] = AddAuxiliaryReading(thermal, "ENTRY HULL / \u00b0C", 22, 135, 279, 23);
        _auxiliaryThermal[3] = AddAuxiliaryReading(thermal, "COOLANT / \u00b0C", 22, 176, 279, 23);
        AuxiliaryLine(thermal, "Cabin row", new Rect(22, 87, 279, 1));
        AuxiliaryLine(thermal, "Chamber row", new Rect(22, 128, 279, 1));
        AuxiliaryLine(thermal, "Entry row", new Rect(22, 169, 279, 1));

        var computer = AddAuxiliaryScreen(2, "SHIP COMPUTER");
        Label(computer.Canvas, "Priority heading", "PRIORITY", 22, 55, 88, 24, 11, Muted);
        _auxiliaryMode = AddAuxiliaryStatus(computer, "Computer mode", 116, 55, 293, 24, 17);
        _auxiliarySummary = AddAuxiliaryStatus(computer, "Priority report", 22, 94, 387, 68, 26);
        Label(computer.Canvas, "Action heading", "RECOMMENDED ACTION", 22, 185, 387, 14, 10, NavigationCyan);
        _auxiliaryAction = AddAuxiliaryStatus(computer, "Computer action", 22, 208, 387, 49, 14);
        _auxiliarySummary.Text.enableWordWrapping = _auxiliaryAction.Text.enableWordWrapping = true;
        _auxiliarySummary.Text.alignment = _auxiliaryAction.Text.alignment = TextAlignmentOptions.TopLeft;
        AuxiliaryLine(computer, "Report divider", new Rect(22, 175, 387, 1));

        var hardware = AddAuxiliaryScreen(3, "SHIP SYSTEMS");
        Label(hardware.Canvas, "System column", "SYSTEM", 22, 51, 225, 15, 10, NavigationCyan);
        Label(hardware.Canvas, "State column", "STATE", 263, 51, 236, 15, 10, NavigationCyan)
            .alignment = TextAlignmentOptions.MidlineRight;
        _auxiliaryHardware[0] = AddAuxiliaryStatusRow(hardware, "GENERATOR", 76);
        _auxiliaryHardware[1] = AddAuxiliaryStatusRow(hardware, "SCRUBBER", 116);
        _auxiliaryHardware[2] = AddAuxiliaryStatusRow(hardware, "LANDING GEAR", 156);
        _auxiliaryHardware[3] = AddAuxiliaryStatusRow(hardware, "REAR RAMP", 196);
        _auxiliaryHardware[4] = AddAuxiliaryStatusRow(hardware, "A02 CABIN DOOR", 236);
        _auxiliaryHardware[5] = AddAuxiliaryStatusRow(hardware, "CREW ACCESS", 276);
        Label(hardware.Canvas, "System scope", "SYSTEM TELEMETRY", 22, 331, 477, 13, 10, Muted);

        var airlock = AddAuxiliaryScreen(4, "COMPARTMENTS");
        _auxiliaryPressure[0] = AddAuxiliaryReading(airlock, "CABIN / kPa", 22, 55, 339, 27);
        _auxiliaryPressure[1] = AddAuxiliaryReading(airlock, "CHAMBER / kPa", 22, 99, 339, 27);
        _auxiliaryPressure[2] = AddAuxiliaryReading(airlock, "ENTRY / kPa", 22, 143, 339, 27);
        AuxiliaryLine(airlock, "Cabin pressure row", new Rect(22, 88, 339, 1));
        AuxiliaryLine(airlock, "Chamber pressure row", new Rect(22, 132, 339, 1));
        AuxiliaryLine(airlock, "Pressure divider", new Rect(22, 176, 339, 1));
        Label(airlock.Canvas, "A01 label", "A01 ENTRY DOOR", 22, 191, 142, 20, 13, Muted);
        _auxiliaryInnerHatch = AddAuxiliaryStatus(airlock, "A01 entry door", 171, 188, 190, 27, 17);
        _auxiliaryInnerHatch.Text.alignment = TextAlignmentOptions.MidlineRight;
        _auxiliaryInterlock = AddAuxiliaryStatus(airlock, "Airlock interlock", 22, 239, 339, 32, 16);
        AuxiliaryLine(airlock, "Compartment divider", new Rect(22, 225, 339, 1));

        var feeds = AddAuxiliaryScreen(5, "SUPPLY PRESSURES");
        _auxiliaryFeeds[0] = AddAuxiliaryReading(feeds, "AIR MIX / kPa", 22, 65, 387, 27);
        _auxiliaryFeeds[1] = AddAuxiliaryReading(feeds, "FUEL / kPa", 22, 130, 387, 27);
        _auxiliaryFeeds[2] = AddAuxiliaryReading(feeds, "COOLANT / kPa", 22, 195, 387, 27);
        AuxiliaryLine(feeds, "Air feed row", new Rect(22, 109, 387, 1));
        AuxiliaryLine(feeds, "Fuel feed row", new Rect(22, 174, 387, 1));

        var power = AddAuxiliaryFace("Overhead power", OverheadPowerSurface);
        Label(power.Canvas, "Power heading", "POWER", 7, 7, 107, 16, 11, Ink);
        AuxiliaryLine(power, "Power rule", new Rect(7, 29, 107, 1));
        _overheadBattery = AddAuxiliaryReading(power, "BATTERY / %", 7, 41, 107, 18, true, stacked: true);
        Label(power.Canvas, "Generator label", "GENERATOR", 7, 109, 107, 13, 8, Muted);
        _overheadGenerator = AddAuxiliaryStatus(power, "Generator state", 7, 125, 107, 19, 11);
        Label(power.Canvas, "Scrubber label", "SCRUBBER", 7, 161, 107, 13, 8, Muted);
        _overheadScrubber = AddAuxiliaryStatus(power, "Scrubber state", 7, 178, 107, 19, 11);

        var overhead = AddAuxiliaryFace("Overhead reserves", OverheadReservesSurface);
        Label(overhead.Canvas, "Reserves heading", "RESERVES", 7, 7, 107, 16, 11, Ink);
        AuxiliaryLine(overhead, "Reserves rule", new Rect(7, 29, 107, 1));
        _overheadReserves[0] = AddAuxiliaryReading(overhead, "FUEL / %", 7, 39, 107, 17, true, stacked: true);
        _overheadReserves[1] = AddAuxiliaryReading(overhead, "AIR MIX / %", 7, 96, 107, 17, true, stacked: true);
        _overheadReserves[2] = AddAuxiliaryReading(overhead, "COOLANT / %", 7, 153, 107, 17, true, stacked: true);

        var ramp = AddAuxiliaryFace("Overhead ramp", OverheadRampSurface);
        Label(ramp.Canvas, "Ramp heading", "RAMP", 4, 10, 41, 13, 8, Muted);
        _overheadRamp = AddAuxiliaryStatus(ramp, "Ramp position", 4, 40, 41, 20, 9);
        Label(ramp.Canvas, "Ramp scope", "POSITION", 4, 83, 41, 10, 6, Muted);
        var gear = AddAuxiliaryFace("Overhead gear", OverheadGearSurface);
        Label(gear.Canvas, "Gear heading", "GEAR", 4, 10, 41, 13, 8, Muted);
        _overheadGear = AddAuxiliaryStatus(gear, "Gear position", 4, 40, 41, 20, 9);
        Label(gear.Canvas, "Gear scope", "POSITION", 4, 83, 41, 10, 6, Muted);

        var legend = AddAuxiliaryFace("Overhead atmosphere legend", OverheadLegendSurface);
        Label(legend.Canvas, "Atmosphere heading", "COMPARTMENT TELEMETRY", 5, 2, 325, 20, 11, Ink)
            .alignment = TextAlignmentOptions.Center;
        for (var index = 0; index < _overheadAtmosphere.Length; index++)
        {
            var window = AddAuxiliaryFace("Overhead atmosphere " + index, OverheadAtmosphereSurfaces[index]);
            var title = index == 0 ? "CAB kPa" : index == 1 ? "LOCK kPa" : "ENTRY kPa";
            Label(window.Canvas, "Reading unit", title, 3, 1, 76, 8, 6, Muted);
            _overheadAtmosphere[index] = new AuxiliaryReading(Label(window.Canvas, "Reading", "SYNC", 3, 9, 76, 12, 9, Ink));
        }

        for (var index = 0; index < KeyboardSurfaces.Length; index++)
        {
            var terminal = AddAuxiliaryFace(index == 0 ? "Pilot service monitor" : "Copilot service monitor", KeyboardSurfaces[index]);
            Label(terminal.Canvas, "Service heading", "SERVICE / READINESS", 8, 6, 181, 14, 9, Ink);
            AuxiliaryLine(terminal, "Service rule", new Rect(8, 24, 181, 1)).color = NavigationCyan;
            AuxiliaryLine(terminal, "Service columns", new Rect(66, 35, 1, 77));
            Label(terminal.Canvas, "Computer heading", "SHIP COMPUTER", 76, 33, 113, 9, 6.5f, NavigationCyan);
            _keyboardMode[index] = AddAuxiliaryStatus(terminal, "Readiness mode", 76, 46, 113, 17, 8);
            _keyboardSummary[index] = AddAuxiliaryStatus(terminal, "Readiness report", 76, 73, 113, 43, 9);
            _keyboardSummary[index].Text.enableWordWrapping = true;
            _keyboardSummary[index].Text.alignment = TextAlignmentOptions.TopLeft;
            Label(terminal.Canvas, "Fuel unit", "FUEL / %", 8, 35, 50, 10, 6.5f, Muted);
            Label(terminal.Canvas, "Battery unit", "BATTERY / %", 8, 80, 50, 10, 6.5f, Muted);
            _keyboardFuel[index] = new AuxiliaryReading(Label(terminal.Canvas, "Fuel value", "SYNC", 8, 49, 50, 20, 14, Ink));
            _keyboardBattery[index] = new AuxiliaryReading(Label(terminal.Canvas, "Battery value", "SYNC", 8, 94, 50, 20, 14, Ink));
        }

        var chassisGear = AddAuxiliaryFace("Chassis gear status", ChassisGearSurface);
        Label(chassisGear.Canvas, "Gear legend", "LANDING GEAR", 3, 2, 90, 10, 6, Muted);
        _chassisGear = AddAuxiliaryStatus(chassisGear, "Gear state", 3, 15, 90, 16, 9);
        var chassisRamp = AddAuxiliaryFace("Chassis ramp status", ChassisRampSurface);
        Label(chassisRamp.Canvas, "Ramp legend", "REAR RAMP", 3, 1, 69, 7, 5, Muted);
        _chassisRamp = AddAuxiliaryStatus(chassisRamp, "Ramp state", 3, 9, 69, 10, 7);
        var wall = AddAuxiliaryFace("Wall computer summary", WallSummarySurface);
        _wallMode = AddAuxiliaryStatus(wall, "Computer state", 7, 5, 206, 18, 11);
        AuxiliaryLine(wall, "Summary rule", new Rect(7, 29, 206, 1));
        _wallSummary = AddAuxiliaryStatus(wall, "Current report", 7, 36, 206, 43, 11);
        _wallSummary.Text.enableWordWrapping = true;
        _wallSummary.Text.alignment = TextAlignmentOptions.TopLeft;
    }

    private Station AddAuxiliaryScreen(int index, string title)
    {
        var station = AddAuxiliaryFace(title, AuxiliarySurfaces[index], AuxiliaryUp[index]);
        // Monitor B's upper clamps overlap the lens.
        var titleY = index == 4 ? 20 : 12;
        var ruleY = index == 4 ? 51 : 43;
        Label(station.Canvas, "Screen title", title, 22, titleY, station.Width - 44, 24, 16, Ink);
        AuxiliaryLine(station, "Screen rule", new Rect(22, ruleY, station.Width - 44, 1));
        AuxiliaryLine(station, "Title accent", new Rect(22, ruleY, 40, 2)).color = NavigationCyan;
        return station;
    }

    private Station AddAuxiliaryFace(string name, PanelSurface surface, Vector3 up = default)
    {
        var station = AddPanel(name, surface, default);
        if (up != default) station.Frame.localRotation = Quaternion.LookRotation(-surface.Normal, up);
        var face = PanelLamp(station, "Display face", new Rect(0, 0, surface.Width, surface.Height));
        face.color = AuxiliaryBlack;
        face.transform.SetAsFirstSibling();
        return station;
    }

    private Image AuxiliaryLine(Station station, string name, Rect bounds)
    {
        var line = PanelLamp(station, name, bounds);
        line.color = ScreenRule;
        return line;
    }

    private AuxiliaryReading AddAuxiliaryReading(Station station, string title, float x, float y, float width,
        float size, bool reserve = false, bool stacked = false)
    {
        Label(station.Canvas, title, title, x, y, stacked ? width : width * .62f, stacked ? 12 : 26,
            stacked ? 8 : 13, Muted);
        var value = Label(station.Canvas, title + " value", "SYNC", stacked ? x : x + width * .62f,
            stacked ? y + 15 : y, stacked ? width : width * .38f, 28, size, Ink);
        value.alignment = stacked ? TextAlignmentOptions.MidlineLeft : TextAlignmentOptions.MidlineRight;
        Image? fill = null;
        if (reserve)
        {
            var barY = y + (stacked ? 45 : 37);
            AuxiliaryLine(station, title + " track", new Rect(x, barY, width, 3));
            fill = PanelLamp(station, title + " contents", new Rect(x, barY, 0, 3));
            fill.color = NavigationCyan;
        }
        return new AuxiliaryReading(value, fill, width);
    }

    private AuxiliaryStatus AddAuxiliaryStatusRow(Station station, string title, float y)
    {
        Label(station.Canvas, title, title, 22, y, 225, 28, 14, Muted);
        AuxiliaryLine(station, title + " separator", new Rect(22, y + 33, station.Width - 44, 1));
        var status = AddAuxiliaryStatus(station, title + " state", 263, y, station.Width - 285, 28, 19);
        status.Text.alignment = TextAlignmentOptions.MidlineRight;
        return status;
    }

    private AuxiliaryReading AddAuxiliaryReserve(Station station, string title, float x, float y, float width)
    {
        Label(station.Canvas, title, title, x, y, width, 18, 13, Muted);
        var value = Label(station.Canvas, title + " value", "SYNC", x, y + 22, width, 48, 38, Ink);
        AuxiliaryLine(station, title + " track", new Rect(x, y + 82, width, 3));
        var fill = PanelLamp(station, title + " contents", new Rect(x, y + 82, 0, 3));
        fill.color = NavigationCyan;
        return new AuxiliaryReading(value, fill, width);
    }

    private AuxiliaryStatus AddAuxiliaryStatus(Station station, string name, float x, float y,
        float width, float height, float size) => new(Label(station.Canvas, name, "SYNC", x, y, width, height, size, Muted));

    private void UpdateAuxiliaryDisplays(ClientSpacecraftView view)
    {
        var systems = view.Flight.Systems;
        var ready = view.Ready && view.Available && systems.Ready;
        SetAuxiliaryNumber(_auxiliaryReserves[0], systems.Fuel01 * 100, ready);
        SetAuxiliaryNumber(_auxiliaryReserves[1], systems.OxygenReserve01 * 100, ready);
        SetAuxiliaryNumber(_auxiliaryReserves[2], systems.Coolant01 * 100, ready);
        SetAuxiliaryNumber(_auxiliaryReserves[3], systems.Battery01 * 100, ready);
        SetAuxiliaryNumber(_auxiliaryThermal[0], systems.Thermal.CabinHullKelvin - 273.15f, ready);
        SetAuxiliaryNumber(_auxiliaryThermal[1], systems.Thermal.AirlockHullKelvin - 273.15f, ready);
        SetAuxiliaryNumber(_auxiliaryThermal[2], systems.Thermal.EntryHullKelvin - 273.15f, ready);
        SetAuxiliaryNumber(_auxiliaryThermal[3], systems.Thermal.CoolantKelvin - 273.15f, ready);
        SetAuxiliaryNumber(_auxiliaryPressure[0], systems.CabinPressure, ready);
        SetAuxiliaryNumber(_auxiliaryPressure[1], systems.AirlockPressure, ready);
        SetAuxiliaryNumber(_auxiliaryPressure[2], systems.EntryPressure, ready);
        SetAuxiliaryNumber(_auxiliaryFeeds[0], systems.AirFeedPressure, ready);
        SetAuxiliaryNumber(_auxiliaryFeeds[1], systems.FuelFeedPressure, ready);
        SetAuxiliaryNumber(_auxiliaryFeeds[2], systems.CoolantPressure, ready);
        SetAuxiliaryNumber(_overheadBattery, systems.Battery01 * 100, ready);
        SetAuxiliaryNumber(_overheadReserves[0], systems.Fuel01 * 100, ready);
        SetAuxiliaryNumber(_overheadReserves[1], systems.OxygenReserve01 * 100, ready);
        SetAuxiliaryNumber(_overheadReserves[2], systems.Coolant01 * 100, ready);
        SetAuxiliaryNumber(_overheadAtmosphere[0], systems.CabinPressure, ready);
        SetAuxiliaryNumber(_overheadAtmosphere[1], systems.AirlockPressure, ready);
        SetAuxiliaryNumber(_overheadAtmosphere[2], systems.EntryPressure, ready);

        var generator = GeneratorState(systems, ready);
        var scrubber = ScrubberState(systems, ready);
        var ramp = DoorPosition(view.Flight.DoorAmount, view.Flight.DoorOpen, ready, systems,
            ClientSpacecraftSystems.CanOpenOuter(systems, view.Flight.Region, _body.InnerDoorClosed, _body.CabinDoorClosed, out _));
        var gear = AuxiliaryPosition(view.Flight.GearAmount, view.Flight.GearDeployed, ready, "RETRACTED", "DEPLOYED", "DEPLOYING", "RETRACTING",
            SpacecraftElectrical.IsPowered(systems.Electrical, SpacecraftCircuit.Propulsion));
        var cabin = DoorPosition(_body.CabinDoorAmount, systems.CabinDoorOpen, ready, systems,
            ClientSpacecraftSystems.CanOpenCabinDoor(systems, _body.InnerDoorClosed, out _));
        var normal = ready ? Ink : Muted;
        var generatorColor = ready && systems.ApuIsolated ? Amber : normal;
        SetAuxiliaryStatus(_auxiliaryHardware[0], generator, generatorColor);
        var scrubberColor = ready && systems.ScrubberEnabled
            && !SpacecraftElectrical.IsPowered(systems.Electrical, SpacecraftCircuit.LifeSupport) ? Amber : normal;
        SetAuxiliaryStatus(_auxiliaryHardware[1], scrubber, scrubberColor);
        SetAuxiliaryStatus(_auxiliaryHardware[2], gear, normal);
        SetAuxiliaryStatus(_auxiliaryHardware[3], ramp, normal);
        SetAuxiliaryStatus(_auxiliaryHardware[4], cabin, normal);
        SetAuxiliaryStatus(_auxiliaryHardware[5], !ready ? "SYNC" : systems.CrewLocked ? "CREW LOCK" : "OPEN ACCESS", normal);
        SetAuxiliaryStatus(_overheadGenerator, generator, generatorColor);
        SetAuxiliaryStatus(_overheadScrubber, scrubber, scrubberColor);
        SetAuxiliaryStatus(_overheadRamp, ramp is "OPENING" or "CLOSING" ? "MOVING" : ramp, normal);
        SetAuxiliaryStatus(_overheadGear, gear switch
        {
            "DEPLOYED" => "DOWN",
            "RETRACTED" => "UP",
            "DEPLOYING" or "RETRACTING" => "MOVING",
            _ => gear
        }, normal);
        SetAuxiliaryStatus(_chassisGear, gear, normal);
        SetAuxiliaryStatus(_chassisRamp, ramp, normal);
        SetAuxiliaryStatus(_auxiliaryInnerHatch,
            DoorPosition(_body.InnerDoorAmount, systems.InnerDoorOpen, ready, systems,
                ClientSpacecraftSystems.CanOpenInnerDoor(systems, _body.CabinDoorClosed, out _)), normal);
        SetAuxiliaryStatus(_auxiliaryInterlock, !ready ? "SYNC" : systems.EmergencyOverride ? "INTERLOCK BYPASSED" : "INTERLOCK ACTIVE",
            ready && systems.EmergencyOverride ? Amber : normal);

        var report = ClientSpacecraftComputer.Analyze(systems with { Ready = ready });
        var color = report.Mode == SpacecraftComputerMode.Emergency ? Critical
            : report.Mode is SpacecraftComputerMode.Warning or SpacecraftComputerMode.Advisory ? Amber : normal;
        var mode = report.Mode switch
        {
            SpacecraftComputerMode.Emergency => "EMERGENCY",
            SpacecraftComputerMode.Warning => "WARNING",
            SpacecraftComputerMode.Advisory => "ADVISORY",
            SpacecraftComputerMode.Nominal => "NOMINAL",
            SpacecraftComputerMode.Standby => "STANDBY",
            _ => "AWAITING TELEMETRY"
        };
        SetAuxiliaryStatus(_auxiliaryMode, mode, color);
        SetAuxiliaryStatus(_auxiliarySummary, report.Summary, color);
        SetAuxiliaryStatus(_auxiliaryAction, report.Action, Muted);
        SetAuxiliaryStatus(_wallMode, mode, color);
        SetAuxiliaryStatus(_wallSummary, report.Summary, color);
        for (var index = 0; index < KeyboardSurfaces.Length; index++)
        {
            SetAuxiliaryNumber(_keyboardFuel[index], systems.Fuel01 * 100, ready);
            SetAuxiliaryNumber(_keyboardBattery[index], systems.Battery01 * 100, ready);
            SetAuxiliaryStatus(_keyboardMode[index], mode, color);
            SetAuxiliaryStatus(_keyboardSummary[index], report.Summary, color);
        }
    }

    private static string DoorPosition(float amount, bool opening, bool ready, SpacecraftSystemState systems, bool openingAllowed) =>
        ready && opening && amount is >= 0 and <= .01f && !openingAllowed ? "INTERLOCK"
        : AuxiliaryPosition(amount, opening, ready, "CLOSED", "OPEN", "OPENING", "CLOSING",
            SpacecraftElectrical.IsPowered(systems.Electrical, SpacecraftCircuit.Airlock), systems.EmergencyOverride);

    private static string AuxiliaryPosition(float amount, bool opening, bool ready, string closed, string open,
        string movingOpen, string movingClosed, bool powered, bool manual = false)
    {
        if (!ready) return "SYNC";
        if (!float.IsFinite(amount) || amount is < 0 or > 1) return "--";
        if (opening && amount >= .99f) return open;
        if (!opening && amount <= .01f) return closed;
        if (!powered) return manual ? "MANUAL" : "NO POWER";
        return opening ? movingOpen : movingClosed;
    }

    private static void SetAuxiliaryNumber(AuxiliaryReading reading, float value, bool ready)
    {
        var valid = ready && float.IsFinite(value);
        var rounded = valid ? MathF.Round(value, 1) : 0;
        var inRange = valid && (reading.Fill == null || value is >= 0 and <= 100);
        var state = !ready ? 0 : !valid ? 1 : inRange ? 2 : 3;
        if (reading.State == state && reading.Value == rounded) return;
        reading.State = state;
        reading.Value = rounded;
        reading.Text.text = !ready ? "SYNC" : !valid ? "--" : rounded.ToString("0.0", CultureInfo.InvariantCulture);
        reading.Text.color = !ready ? Muted : inRange ? Ink : Amber;
        if (reading.Fill == null) return;
        reading.Fill.rectTransform.sizeDelta = new Vector2(valid ? reading.Width * Mathf.Clamp01(value / 100) : 0, 3);
        reading.Fill.color = inRange ? NavigationCyan : Amber;
    }

    private static void SetAuxiliaryStatus(AuxiliaryStatus status, string value, Color color)
    {
        if (status.Value != value) { status.Value = value; status.Text.text = value; }
        if (status.Color != color) { status.Color = color; status.Text.color = color; }
    }

    private sealed class AuxiliaryReading(TextMeshProUGUI text, Image? fill = null, float width = 0)
    {
        internal readonly TextMeshProUGUI Text = text;
        internal readonly Image? Fill = fill;
        internal readonly float Width = width;
        internal int State = -1;
        internal float Value;
    }

    private sealed class AuxiliaryStatus(TextMeshProUGUI text)
    {
        internal readonly TextMeshProUGUI Text = text;
        internal string Value = "SYNC";
        internal Color Color = Muted;
    }
}
