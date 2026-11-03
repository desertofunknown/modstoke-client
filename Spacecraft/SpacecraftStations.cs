// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Modstoke.Loader;
using Modstoke.Sons;
using TheForest.Utils;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace Modstoke.Client;

// Rotation maps +Z to the finger direction and +Y to the palm normal.
internal readonly record struct SpacecraftControlContact(Vector3 Position, Quaternion Rotation, bool Press);

internal sealed partial class ClientSpacecraftStations : IDisposable
{
    private static readonly Color Ink = new(.92f, .94f, .95f, 1);
    private static readonly Color Muted = new(.55f, .59f, .62f, 1);
    private static readonly Color Green = new(.38f, .83f, .67f, 1);
    private static readonly Color Amber = new(1, .72f, .28f, 1);
    private static readonly Color NavigationCyan = new(.35f, .84f, .91f, 1);
    private static readonly Color Critical = new(.96f, .28f, .23f, 1);
    private static readonly Color ScreenRule = new(.22f, .26f, .28f, 1);
    private static readonly Color KeyFace = new(.035f, .043f, .048f, 1);
    private readonly ClientSpacecraftBody _body;
    private readonly ClientSpacecraftWorld _world;
    private readonly ClientSpacecraftRider _rider;
    private readonly Action<string> _command;
    private readonly Action<string> _manualRepair;
    private readonly Func<string, string> _manualRepairPrompt;
    private readonly List<Station> _stations = new();
    private readonly List<Material> _materials = new();
    private readonly Dictionary<int, Material> _fonts = new();
    private readonly ClientTabletTypography _typography = new();
    private GameObject? _root;
    private Material _surface = null!, _bezel = null!, _button = null!, _graphics = null!;
    private Material _cyanLine = null!, _amberLine = null!, _selectedLine = null!;
    private Material _orbitLine = null!;
    private readonly Material[] _globeSurfaces = new Material[3];
    private Mesh? _globeGrid;
    private Material[] _globeGridMaterials = null!;
    private Transform _map = null!, _shipMarker = null!;
    private Mesh? _terrainMesh;
    private Il2CppStructArray<Vector3>? _terrainVertices;
    private const int TerrainSide = 13;
    private const float ProjectionScale = 1.7f;
    private static readonly Vector3 ProjectionPosition = new(.02f, -.05f, -1.38f);
    private readonly float[] _terrainHeights = new float[TerrainSide * TerrainSide];
    private readonly bool[] _terrainValid = new bool[TerrainSide * TerrainSide];
    private Il2CppStructArray<RaycastHit>? _terrainHits;
    private Il2CppStructArray<RaycastHit>? _controlHits;
    private Vector3 _terrainCenter;
    private SpacecraftRegion _terrainRegion;
    private float _terrainSpan, _terrainDatum, _nextTerrainRefresh;
    private int _terrainSamples;
    private bool _terrainDirty = true;
    private readonly GameObject[] _mapModes = new GameObject[3];
    private readonly TextMeshProUGUI[] _destinationStates = new TextMeshProUGUI[3];
    private MeshRenderer _planetSurface = null!;
    private Transform _planetSelection = null!;
    private TextMeshProUGUI _planetTitle = null!;
    private TextMeshProUGUI _mapCaption = null!;
    private TextMeshProUGUI _navigationOrigin = null!, _navigationStatus = null!, _navigationChartKind = null!;
    private TextMeshProUGUI _navigationZoom = null!;
    private Image _navigationZoomCursor = null!;
    private StationMeter _warpMeter = null!;
    private readonly List<AirlockDiagram> _airlockDiagrams = new();
    private Station _engineering = null!, _navigation = null!;
    private TextMeshProUGUI _diagnostics = null!, _connectionFaults = null!;
    private TextMeshProUGUI _circuitSpares = null!, _lineSpares = null!;
    private RectTransform _operationsArt = null!, _diagnosticsArt = null!;
    private CompartmentReadout _engineeringCabin = null!, _engineeringAirlock = null!, _engineeringEntry = null!;
    private TextMeshProUGUI _engineeringScrubber = null!, _engineeringCoolant = null!;
    private TextMeshProUGUI _engineeringDoors = null!;
    private StationMeter _engineeringFuel = null!, _engineeringAir = null!, _engineeringBattery = null!, _engineeringCoolantReserve = null!;
    private TextMeshProUGUI _cabinScrubberValve = null!, _airlockScrubberValve = null!;
    private TextMeshProUGUI _cabinCoolantValve = null!, _airlockCoolantValve = null!;
    private Image _cabinScrubberConnection = null!, _airlockScrubberConnection = null!;
    private Image _cabinCoolantConnection = null!, _airlockCoolantConnection = null!;
    private readonly DiagnosticRow[] _diagnosticRows = new DiagnosticRow[5];
    private EngineeringPage _engineeringPage;
    private StationButton? _hovered;
    private Station? _armedStation;
    private Station? _commandStation;
    private string _feedbackBefore = "";
    private float _feedbackUntil;
    private float _armedUntil, _nextReadout;
    private float _zoom = 1;
    private int _mapMode = 2;
    private bool _disposed;
    private static readonly Vector3 AftAirlockPosition = new(1.85f, .13f, -9.7f);
    // The port-facing main display of CabinMeshes/SpaceshipNavigationMonitor is
    // 1.407 x 0.523 m. Keep the overview inside its chamfered native bezel.
    private static readonly Vector3 EngineeringOverviewPosition = new(.072749f, 1.022060f, -1.120676f);
    private static readonly Quaternion EngineeringOverviewRotation = Quaternion.LookRotation(
        new Vector3(.985381f, .164644f, -.043783f), new Vector3(-.170366f, .952290f, -.253217f));
    private static readonly Vector3 EngineeringScreenPosition = SpacecraftLayout.EngineeringControlPosition;
    private static readonly Quaternion EngineeringScreenRotation = SpacecraftLayout.EngineeringControlRotation;
    private const float EngineeringScreenWidth = 1280, EngineeringScreenHeight = 440;
    private static readonly Vector3 NavigationScreenPosition = SpacecraftLayout.NavigationControlPosition;
    private static readonly Quaternion NavigationScreenRotation = SpacecraftLayout.NavigationControlRotation;
    private const float NavigationScreenWidth = 960, NavigationScreenHeight = 420;
    private const float NativeScreenOffset = .003f;
    private const float StationScreenOffset = .025f;

    internal static Vector3 LocalPosition(string command) => command switch
    {
        "inner-door" or "door" or "override" or "vent"
            or "airlock-to-cabin" or "airlock-to-entry" or "airlock-cancel" => new(.86f, .13f, -8),
        "scrubber" or "generator" or "service-supplies" or "shore-air" or "power" or "dampeners" or "gear" => EngineeringScreenPosition,
        _ when ClientSpacecraftSystems.TryReadCircuitCommand(command, out _, out _) => EngineeringScreenPosition,
        _ when ClientSpacecraftSystems.TryReadCircuitRepairCommand(command, out _) => EngineeringScreenPosition,
        _ => NavigationScreenPosition
    };

    internal static Vector3 LocalControlPoint(string command, Vector3 actorLocal, SpacecraftSystemState systems)
        => LocalControlContact(command, actorLocal, systems).Position;

    internal static SpacecraftControlContact LocalControlContact(string command, Vector3 actorLocal, SpacecraftSystemState systems)
    {
        if (TryPanelControlContact(command, actorLocal, systems, out var panelContact)) return panelContact;
        var bounds = ControlBounds(command);
        var position = LocalPosition(command);
        var airlock = command is "inner-door" or "door" or "override" or "vent"
            or "airlock-to-cabin" or "airlock-to-entry" or "airlock-cancel";
        var navigation = command is "select-island" or "select-world" or "select-moon";
        // Resolve the A01 side before distance so a closed door cannot redirect reach to the opposite panel.
        if (airlock && actorLocal.z < -9.1f)
            position = AftAirlockPosition;
        var rotation = airlock ? Quaternion.Euler(0, 90, 0)
            : navigation ? NavigationScreenRotation : EngineeringScreenRotation;
        var width = airlock ? 720 : navigation ? NavigationScreenWidth : EngineeringScreenWidth;
        var height = airlock ? 560 : navigation ? NavigationScreenHeight : EngineeringScreenHeight;
        var point = position + rotation * new Vector3((bounds.center.x - width / 2f) * .001f,
            (height / 2f - bounds.center.y) * .001f, airlock ? -StationScreenOffset : -NativeScreenOffset);
        return KeyControlContact(point, rotation, indexDown: navigation);
    }

    // Keep the wrist above the low navigation keys with the palm facing down.
    internal static SpacecraftControlContact KeyControlContact(Vector3 position, Quaternion surfaceRotation, bool indexDown = false)
    {
        var direction = indexDown ? Vector3.down : Vector3.up;
        var normal = indexDown ? Vector3.back : Vector3.forward;
        return new(position, surfaceRotation * Quaternion.LookRotation(direction, normal)
            * Quaternion.Euler(indexDown ? 55 : -55, 0, 0), true);
    }

    private static Rect ControlBounds(string command) => command switch
    {
        "airlock-to-cabin" => new(24, 320, 216, 50),
        "airlock-to-entry" => new(252, 320, 216, 50),
        "airlock-cancel" => new(480, 320, 216, 50),
        "inner-door" => new(24, 382, 216, 50),
        "door" => new(252, 382, 216, 50),
        "vent" => new(480, 382, 216, 50),
        "override" => new(252, 454, 444, 42),
        "scrubber" => new(24, 334, 160, 54),
        "generator" => new(198, 334, 194, 54),
        "power" => new(406, 334, 160, 54),
        "dampeners" => new(580, 334, 160, 54),
        "gear" => new(754, 334, 174, 54),
        "service-supplies" => new(942, 334, 314, 54),
        "shore-air" => new(942, 334, 314, 54),
        "select-island" => new(24, 250, 288, 60),
        "select-world" => new(336, 250, 288, 60),
        "select-moon" => new(648, 250, 288, 60),
        _ when ClientSpacecraftSystems.TryReadCircuitCommand(command, out var circuit, out var open)
            => new(open ? 784 : 1104, 174 + circuit * 34, 152, 30),
        _ when ClientSpacecraftSystems.TryReadCircuitRepairCommand(command, out var repairCircuit)
            => new(944, 174 + repairCircuit * 34, 152, 30),
        _ => throw new ArgumentOutOfRangeException(nameof(command), command, "No cabin station key exists for this command.")
    };

    internal ClientSpacecraftStations(ClientSpacecraftBody body, Action<string> command, ClientSpacecraftWorld world,
        ClientSpacecraftRider rider, Action<string> manualRepair, Func<string, string> manualRepairPrompt)
    {
        _body = body;
        _command = command;
        _world = world;
        _rider = rider;
        _manualRepair = manualRepair;
        _manualRepairPrompt = manualRepairPrompt;
        try
        {
            _root = new GameObject("Spacecraft cabin stations") { layer = body.Root.layer };
            _root.transform.SetParent(body.Root.transform, false);
            _surface = Lit("Station graphite", new Color(.025f, .028f, .03f, 1));
            _bezel = Lit("Station frame", new Color(.34f, .36f, .37f, 1));
            _button = Lit("Station keys", KeyFace);
            _graphics = new Material(Graphic.defaultGraphicMaterial) { name = "Station graphics" };
            _materials.Add(_graphics); Depth(_graphics);
            _cyanLine = Emission("Navigation cyan", NavigationCyan);
            _amberLine = Emission("Navigation destination", Amber);
            _selectedLine = Emission("Navigation selection", Green);
            _orbitLine = Emission("Navigation coordinates", new Color(.24f, .53f, .57f, 1));
            _globeSurfaces[0] = Emission("Island schematic surface", new Color(.009f, .03f, .036f, 1));
            _globeSurfaces[1] = Emission("World schematic surface", new Color(.01f, .032f, .024f, 1));
            _globeSurfaces[2] = Emission("Moon schematic surface", new Color(.024f, .027f, .031f, 1));
            _globeGridMaterials = new[]
            {
                Emission("Navigation minor coordinates", new Color(.10f, .23f, .25f, 1)),
                _orbitLine, _cyanLine
            };
            // The two panels flank the measured DoorA01 threshold; neither is
            // attached to a moving leaf or placed within its sliding pocket.
            BuildAirlock(LocalPosition("inner-door"), "AIRLOCK / ENTRY");
            BuildAirlock(AftAirlockPosition, "ENTRY / AIRLOCK");
            BuildEngineering();
            BuildEngineeringOverview();
            BuildNavigation();
            BuildCabinPanels();
            BuildEngineeringHousing();
            BuildNavigationHousing();
            BuildAuxiliaryDisplays();
        }
        catch { Dispose(); throw; }
    }

    private void BuildAirlock(Vector3 position, string title)
    {
        var station = AddStation("Airlock", title, position, Quaternion.Euler(0, 90, 0), 720, 560);
        station.Readout.rectTransform.anchoredPosition = new Vector2(24, -224);
        station.Readout.rectTransform.sizeDelta = new Vector2(672, 88);
        station.Readout.fontSize = 15;
        station.Readout.alignment = TextAlignmentOptions.TopLeft;
        station.Readout.enableWordWrapping = false;
        var cabin = AddCompartment(station.Canvas, "CABIN", 24);
        var airlock = AddCompartment(station.Canvas, "CHAMBER", 246);
        var entry = AddCompartment(station.Canvas, "ENTRY", 468);
        var cabinDoor = StationGraphic(station.Canvas, "A02 cabin door state", 218, 124, 14, 64, Muted);
        var inner = StationGraphic(station.Canvas, "A01 entry door state", 440, 124, 14, 64, Muted);
        var ramp = StationGraphic(station.Canvas, "Rear hatch state", 662, 124, 14, 64, Muted);
        StationGraphic(station.Canvas, "A02 threshold", 204, 154, 42, 2, Muted);
        StationGraphic(station.Canvas, "A01 threshold", 426, 154, 42, 2, Muted);
        StationGraphic(station.Canvas, "Rear threshold", 648, 154, 42, 2, Muted);
        Label(station.Canvas, "A02 label", "A02", 208, 88, 34, 18, 12, Muted);
        Label(station.Canvas, "A01 label", "A01", 430, 88, 34, 18, 12, Muted);
        Label(station.Canvas, "Rear hatch label", "REAR\nHATCH", 652, 82, 44, 32, 11, Muted);
        _airlockDiagrams.Add(new AirlockDiagram(station, cabin, airlock, entry, cabinDoor, inner, ramp));
        AddButton(station, "airlock-to-cabin", "CYCLE TO CABIN");
        AddButton(station, "airlock-to-entry", "CYCLE TO ENTRY");
        AddButton(station, "airlock-cancel", "CANCEL CYCLE");
        AddButton(station, "inner-door", "A01 ENTRY DOOR");
        AddButton(station, "door", "REAR RAMP");
        Label(station.Canvas, "Entry vent procedure", "MANUAL VENT: SEAL A02; OPEN A01 TO VENT ENTRY", 24, 436, 672, 14, 12, Muted);
        AddButton(station, "vent", "VENT CHAMBER");
        AddButton(station, "@arm", "ARM OVERRIDE", 24, 454, 216, 42);
        AddButton(station, "override", "CONFIRM OVERRIDE");
        Label(station.Canvas, "Override warning", "EMERGENCY BYPASS / CREW EXPOSURE RISK", 24, 500, 672, 14, 12, Amber);
    }

    private void BuildEngineering()
    {
        var station = AddStation("Engineering", "ENGINEERING / SHIP COMPUTER", LocalPosition("scrubber"),
            EngineeringScreenRotation, EngineeringScreenWidth, EngineeringScreenHeight, nativeScreen: true);
        _engineering = station;
        AddButton(station, "@operations", "OPS", 24, 76, 176, 38);
        AddButton(station, "@diagnostics", "DIAGNOSTICS", 212, 76, 228, 38);
        AddButton(station, "@electrical", "ELECTRICAL", 452, 76, 228, 38);
        AddButton(station, "@service", "SERVICE", 692, 76, 176, 38);
        station.Readout.rectTransform.anchoredPosition = new Vector2(892, -76);
        station.Readout.rectTransform.sizeDelta = new Vector2(364, 38);
        station.Readout.fontSize = 18;
        station.Readout.alignment = TextAlignmentOptions.MidlineRight;
        _operationsArt = Node(station.Canvas, "Operations schematic", 0, 0, 1280, 440);
        _engineeringCabin = AddEngineeringCompartment(_operationsArt, "CABIN AIR", 24);
        _engineeringAirlock = AddEngineeringCompartment(_operationsArt, "CHAMBER", 612, 124);
        _engineeringEntry = AddEngineeringCompartment(_operationsArt, "ENTRY", 748, 124);
        _engineeringDoors = Label(_operationsArt, "Compartment doors", "DOORS / SYNC", 24, 119, 560, 15, 11, Muted);
        Label(_operationsArt, "Shared aft circuits", "CHAMBER + ENTRY / SHARED VALVES", 612, 119, 260, 15, 11, Muted);
        BuildEngineeringCircuits();
        StationGraphic(_operationsArt, "Reserve separator", 900, 138, 1, 166, ScreenRule);
        _engineeringFuel = AddStationMeter(_operationsArt, "FUEL", 924, 138, 144);
        _engineeringAir = AddStationMeter(_operationsArt, "AIR MIX", 1112, 138, 144);
        _engineeringBattery = AddStationMeter(_operationsArt, "BATTERY", 924, 234, 144);
        _engineeringCoolantReserve = AddStationMeter(_operationsArt, "COOLANT", 1112, 234, 144);
        StationGraphic(_operationsArt, "Control strip rule", 24, 318, 1232, 1, ScreenRule);
        AddButton(station, "scrubber", "SCRUBBER");
        AddButton(station, "generator", "GENERATOR");
        AddButton(station, "power", "ENGINE");
        AddButton(station, "dampeners", "DAMPENERS");
        AddButton(station, "gear", "LANDING GEAR");
        Label(_operationsArt, "Maintenance stock", "ONBOARD MAINTENANCE STOCK", 942, 334, 314, 14, 11, Muted);
        _circuitSpares = Label(_operationsArt, "Circuit spares", "CIRCUIT SPARES / SYNC", 942, 350, 314, 17, 14, Muted);
        _lineSpares = Label(_operationsArt, "Line spares", "LINE SPARES / SYNC", 942, 369, 314, 17, 14, Muted);
        _diagnosticsArt = Node(station.Canvas, "System health table", 0, 0, 1280, 440);
        Label(_diagnosticsArt, "System column", "SYSTEM", 24, 148, 180, 20, 14, Muted);
        Label(_diagnosticsArt, "Health column", "INTEGRITY", 224, 148, 286, 20, 14, Muted);
        Label(_diagnosticsArt, "Telemetry column", "LIVE STATE", 556, 148, 700, 20, 14, Muted);
        var names = new[] { "HULL", "PROPULSION", "POWER", "LIFE SUPPORT", "WARP DRIVE" };
        for (var index = 0; index < names.Length; index++)
        {
            var y = 172 + index * 34;
            StationGraphic(_diagnosticsArt, "Row separator", 24, y + 32, 1232, 1, ScreenRule);
            Label(_diagnosticsArt, "System", names[index], 24, y, 180, 30, 19, Ink);
            var value = Label(_diagnosticsArt, "Health", "SYNC", 224, y, 140, 30, 18, Muted);
            StationGraphic(_diagnosticsArt, "Integrity track", 374, y + 13, 150, 5, ScreenRule);
            var fill = StationGraphic(_diagnosticsArt, "Integrity", 374, y + 13, 0, 5, Muted);
            var detail = Label(_diagnosticsArt, "Live state", "AWAITING HOST", 556, y, 700, 30, 19, Muted);
            _diagnosticRows[index] = new DiagnosticRow(value, fill, detail);
        }
        _connectionFaults = Label(_diagnosticsArt, "Connection faults", "CONNECTIONS / SYNC", 24, 346, 1232, 18, 15, Muted);
        _diagnostics = Label(station.Canvas, "Computer diagnostics", "SYSTEMS SYNC", 24, 368, 1232, 26, 15, Ink);
        _diagnostics.alignment = TextAlignmentOptions.TopLeft;
        BuildElectricalPage();
        BuildShoreAirPage();
        SetComputerPage(EngineeringPage.Operations);
    }

    private void BuildEngineeringCircuits()
    {
        var scrubberTitle = Label(_operationsArt, "Scrubber circuit", "CO2 SCRUBBER", 344, 138, 208, 20, 16, Muted);
        scrubberTitle.alignment = TextAlignmentOptions.Center;
        _cabinScrubberConnection = StationGraphic(_operationsArt, "Cabin scrubber branch", 284, 183, 130, 2, ScreenRule);
        _airlockScrubberConnection = StationGraphic(_operationsArt, "Airlock scrubber branch", 482, 183, 130, 2, ScreenRule);
        _cabinScrubberValve = Label(_operationsArt, "Cabin scrubber valve", "SYNC", 292, 199, 104, 18, 13, Muted);
        _airlockScrubberValve = Label(_operationsArt, "Airlock scrubber valve", "SYNC", 500, 199, 104, 18, 13, Muted);
        _airlockScrubberValve.alignment = TextAlignmentOptions.MidlineRight;
        _engineeringScrubber = Label(_operationsArt, "Scrubber state", "SYNC", 400, 206, 96, 18, 13, Muted);
        _engineeringScrubber.alignment = TextAlignmentOptions.Center;
        StationGraphic(_operationsArt, "Filter face", 414, 169, 68, 30, KeyFace);
        var filter = new[] { new Vector2(414, 175), new Vector2(420, 169), new Vector2(476, 169),
            new Vector2(482, 175), new Vector2(482, 193), new Vector2(476, 199),
            new Vector2(420, 199), new Vector2(414, 193), new Vector2(414, 175) };
        for (var index = 1; index < filter.Length; index++)
            StationStroke(_operationsArt, "Filter housing", filter[index - 1], filter[index], Muted);
        for (var x = 426; x <= 470; x += 11)
            StationGraphic(_operationsArt, "Filter pleat", x, 175, 2, 18, ScreenRule);
        StationGraphic(_operationsArt, "Scrubber separator", 340, 228, 216, 1, ScreenRule);

        var coolantTitle = Label(_operationsArt, "Coolant circuit", "COOLANT LOOP", 344, 236, 208, 20, 16, Muted);
        coolantTitle.alignment = TextAlignmentOptions.Center;
        _cabinCoolantConnection = StationGraphic(_operationsArt, "Cabin coolant branch", 284, 278, 130, 2, ScreenRule);
        _airlockCoolantConnection = StationGraphic(_operationsArt, "Airlock coolant branch", 482, 278, 130, 2, ScreenRule);
        _cabinCoolantValve = Label(_operationsArt, "Cabin coolant valve", "SYNC", 292, 290, 104, 18, 13, Muted);
        _airlockCoolantValve = Label(_operationsArt, "Airlock coolant valve", "SYNC", 500, 290, 104, 18, 13, Muted);
        _airlockCoolantValve.alignment = TextAlignmentOptions.MidlineRight;
        _engineeringCoolant = Label(_operationsArt, "Coolant pressure", "SYNC", 398, 296, 100, 18, 13, Muted);
        _engineeringCoolant.alignment = TextAlignmentOptions.Center;
        StationGraphic(_operationsArt, "Radiator face", 414, 264, 68, 28, KeyFace);
        StationGraphic(_operationsArt, "Radiator upper manifold", 414, 264, 68, 2, Muted);
        StationGraphic(_operationsArt, "Radiator lower manifold", 414, 290, 68, 2, Muted);
        for (var x = 414; x <= 482; x += 17)
            StationGraphic(_operationsArt, "Radiator channel", x, 266, 2, 24, Muted);
    }

    private CompartmentReadout AddEngineeringCompartment(RectTransform parent, string name, float x, float width = 260)
    {
        var compact = width < 200;
        StationGraphic(parent, name + " top", x, 138, width, 2, ScreenRule);
        StationGraphic(parent, name + " left", x, 138, 2, 166, ScreenRule);
        StationGraphic(parent, name + " right", x + width - 2, 138, 2, 166, ScreenRule);
        StationGraphic(parent, name + " pressure track", x, 302, width, 2, ScreenRule);
        Label(parent, name, name, x + 14, 148, width - 28, 22, compact ? 14 : 16, NavigationCyan);
        var pressure = Label(parent, name + " pressure", "-- kPa", x + 14, 180, width - 28, 34, compact ? 20 : 27, Ink);
        var gases = Label(parent, name + " gases", "O2 --\nCO2 --", x + 14, 220, compact ? width - 28 : 134, 52, compact ? 15 : 19, Ink);
        var temperature = Label(parent, name + " temperature", "-- °C", x + (compact ? 14 : 150),
            compact ? 274 : 220, 96, compact ? 24 : 30, compact ? 16 : 19, Ink);
        if (!compact) Label(parent, name + " temperature caption", "TEMP", x + 150, 254, 96, 20, 13, Muted);
        var fill = StationGraphic(parent, name + " pressure", x, 302, 0, 2, Muted);
        return new CompartmentReadout(pressure, gases, temperature, fill, width);
    }

    private void BuildNavigation()
    {
        var station = AddStation("Navigation", "HOLOGRAPHIC NAVIGATION", LocalPosition("select-world"),
            NavigationScreenRotation, NavigationScreenWidth, NavigationScreenHeight, nativeScreen: true);
        _navigation = station;
        station.Readout.rectTransform.anchoredPosition = new Vector2(24, -96);
        station.Readout.rectTransform.sizeDelta = new Vector2(584, 28);
        station.Readout.fontSize = 26;
        station.Prompt.rectTransform.anchoredPosition = new Vector2(24, -394);
        station.Prompt.rectTransform.sizeDelta = new Vector2(912, 22);
        station.Prompt.fontSize = 16;
        Label(station.Canvas, "Destination caption", "SELECTED DESTINATION", 24, 76, 584, 16, 13, Muted);
        _navigationOrigin = Label(station.Canvas, "Current region", "CURRENT / AWAITING HOST", 24, 130, 584, 20, 17, Muted);
        _navigationStatus = Label(station.Canvas, "Warp readiness", "AWAITING HOST", 24, 152, 584, 24, 17, Muted);
        _navigationStatus.enableWordWrapping = false;
        _warpMeter = AddStationMeter(station.Canvas, "WARP CHARGE", 640, 76, 296);
        // Mask the continuous fill into segments without quantizing the live charge value.
        for (var index = 1; index < 12; index++)
            StationGraphic(station.Canvas, "Charge segment", 640 + index * (296f / 12) - 1, 130, 2, 4, KeyFace);
        StationGraphic(station.Canvas, "Telemetry divider", 624, 76, 1, 104, ScreenRule);
        _navigationChartKind = Label(station.Canvas, "Chart scale", "LOG DISTANCE\nNOT TO SCALE", 640, 148, 296, 32, 14, Muted);
        AddButton(station, "@local", "LOCAL", 24, 184, 288, 44);
        AddButton(station, "@planet", "PLANET", 336, 184, 288, 44);
        AddButton(station, "@system", "SYSTEM", 648, 184, 288, 44);
        AddButton(station, "select-island", "ISLAND");
        AddButton(station, "select-world", "UNKNOWN WORLD");
        AddButton(station, "select-moon", "MOON");
        Label(station.Canvas, "Body caption", "DESTINATIONS", 24, 232, 912, 14, 12, Muted);
        for (var index = 0; index < 3; index++)
        {
            var key = station.Buttons[index + 3];
            key.Label.rectTransform.anchoredPosition = new Vector2(62, -8);
            key.Label.rectTransform.sizeDelta = new Vector2(214, 24);
            key.Label.fontSize = 18;
            key.Label.alignment = TextAlignmentOptions.MidlineLeft;
            _destinationStates[index] = Label(key.Key, "Destination state", "AWAITING HOST", 62, 36, 214, 18, 12, Muted);
            var center = new Vector2(31, 30);
            for (var segment = 0; segment < 24; segment++)
            {
                var a = segment * Mathf.PI / 12;
                var b = (segment + 1) * Mathf.PI / 12;
                StationStroke(key.Key, "Body outline", center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * 18,
                    center + new Vector2(Mathf.Cos(b), Mathf.Sin(b)) * 18, NavigationCyan);
                StationStroke(key.Key, "Body meridian", center + new Vector2(Mathf.Cos(a) * 8, Mathf.Sin(a) * 18),
                    center + new Vector2(Mathf.Cos(b) * 8, Mathf.Sin(b) * 18), ScreenRule);
            }
            StationGraphic(key.Key, "Body equator", 13, 29, 36, 2, NavigationCyan);
        }
        AddButton(station, "@zoom-out", "-", 24, 322, 444, 44);
        AddButton(station, "@zoom-in", "+", 492, 322, 444, 44);
        for (var index = 6; index < 8; index++)
        {
            var key = station.Buttons[index];
            key.Label.rectTransform.anchoredPosition = new Vector2(index == 6 ? 12 : 384, -4);
            key.Label.rectTransform.sizeDelta = new Vector2(48, 36);
        }
        _navigationZoom = Label(station.Canvas, "Projection zoom", "ZOOM 1.00x", 360, 326, 240, 24, 18, Ink);
        _navigationZoom.alignment = TextAlignmentOptions.Center;
        StationGraphic(station.Canvas, "Zoom track", 132, 357, 696, 1, ScreenRule);
        for (var index = 0; index <= 12; index++)
            StationGraphic(station.Canvas, "Zoom division", 132 + index * 58, 354, 1, index % 3 == 0 ? 10 : 5, Muted);
        _navigationZoomCursor = StationGraphic(station.Canvas, "Zoom setting", 132, 351, 3, 14, NavigationCyan);
        StationGraphic(station.Canvas, "Chart footer rule", 24, 370, 912, 1, ScreenRule);
        _mapCaption = Label(station.Canvas, "Chart mode", "AWAITING ORBIT DATA", 24, 372, 912, 18, 12, Muted);
        _mapCaption.enableWordWrapping = false;

        _map = new GameObject("Navigation projection").transform;
        _map.gameObject.layer = _root!.layer;
        _map.SetParent(_root.transform, false);
        for (var index = 0; index < _mapModes.Length; index++)
        {
            var mode = new GameObject(index == 0 ? "Local terrain" : index == 1 ? "Planet chart" : "Solar system chart");
            mode.layer = _root.layer; mode.transform.SetParent(_map, false); _mapModes[index] = mode;
        }
        BuildLocalChart(_mapModes[0].transform);
        BuildPlanetChart(_mapModes[1].transform);
        BuildSystemChart(_mapModes[2].transform);
        UpdateMapMode();
    }

    private CompartmentReadout AddCompartment(Transform parent, string name, float x)
    {
        StationGraphic(parent, name + " top", x, 76, 180, 2, ScreenRule);
        StationGraphic(parent, name + " bottom", x, 218, 180, 2, ScreenRule);
        StationGraphic(parent, name + " left", x, 76, 2, 144, ScreenRule);
        StationGraphic(parent, name + " right", x + 178, 76, 2, 144, ScreenRule);
        Label(parent, name, name, x + 12, 82, 156, 24, 18, Ink);
        var pressure = Label(parent, name + " pressure", "-- kPa", x + 12, 111, 156, 32, 26, Muted);
        var gases = Label(parent, name + " fractions", "O2 --\nCO2 --", x + 12, 147, 156, 42, 18, Muted);
        var temperature = Label(parent, name + " temperature", "-- °C", x + 12, 192, 156, 22, 18, Muted);
        var fill = StationGraphic(parent, name + " pressure level", x, 218, 0, 2, Muted);
        return new CompartmentReadout(pressure, gases, temperature, fill, 180);
    }

    private StationMeter AddStationMeter(Transform parent, string title, float x, float y, float width)
    {
        Label(parent, title, title, x, y, width, 18, 15, Muted);
        var value = Label(parent, title + " value", "--", x, y + 20, width, 30, 24, Ink);
        StationGraphic(parent, title + " track", x, y + 54, width, 4, ScreenRule);
        var fill = StationGraphic(parent, title + " level", x, y + 54, 0, 4, Muted);
        return new StationMeter(value, fill, width);
    }

    private void UpdateAirlockDiagrams(ClientSpacecraftView view)
    {
        var systems = view.Flight.Systems;
        var cabinAmount = _body.CabinDoorAmount;
        var innerAmount = _body.InnerDoorAmount;
        var rampAmount = view.Flight.DoorAmount;
        var doorsReady = systems.Ready && _body.Ready;
        var cabinKnown = doorsReady && float.IsFinite(cabinAmount) && cabinAmount is >= 0 and <= 1;
        var innerKnown = doorsReady && float.IsFinite(innerAmount) && innerAmount is >= 0 and <= 1;
        var rampKnown = doorsReady && float.IsFinite(rampAmount) && rampAmount is >= 0 and <= 1;
        var cabinState = DoorPosition(cabinAmount, systems.CabinDoorOpen, doorsReady, systems,
            ClientSpacecraftSystems.CanOpenCabinDoor(systems, _body.InnerDoorClosed, out _));
        var innerState = DoorPosition(innerAmount, systems.InnerDoorOpen, doorsReady, systems,
            ClientSpacecraftSystems.CanOpenInnerDoor(systems, _body.CabinDoorClosed, out _));
        var rampState = DoorPosition(rampAmount, view.Flight.DoorOpen, doorsReady, systems,
            ClientSpacecraftSystems.CanOpenOuter(systems, view.Flight.Region, _body.InnerDoorClosed, _body.CabinDoorClosed, out _));
        var targetPressure = systems.AirlockTarget switch
        {
            SpacecraftAirlockTarget.Cabin => systems.CabinPressure,
            SpacecraftAirlockTarget.Entry => systems.EntryPressure,
            _ => float.NaN
        };
        var difference = float.IsFinite(systems.AirlockPressure) && float.IsFinite(targetPressure)
            ? Number(Mathf.Abs(systems.AirlockPressure - targetPressure)) + " kPa" : "-- kPa";
        var equalizing = systems.AirlockCycle == SpacecraftAirlockPhase.Equalizing;
        var pressureStatus = equalizing ? "TARGET-SIDE BYPASS / DIFFERENCE " + difference
            : "TARGET DIFFERENCE " + difference;
        foreach (var diagram in _airlockDiagrams)
        {
            UpdateCompartment(diagram.Cabin, systems.CabinPressure, systems.OxygenFraction,
                systems.CO2Fraction, systems.CabinTemperatureC, systems.Ready);
            UpdateCompartment(diagram.Airlock, systems.AirlockPressure, systems.AirlockOxygenFraction,
                systems.AirlockCO2Fraction, systems.AirlockTemperatureC, systems.Ready);
            UpdateCompartment(diagram.Entry, systems.EntryPressure, systems.EntryOxygenFraction,
                systems.EntryCO2Fraction, systems.EntryTemperatureC, systems.Ready);
            diagram.CabinDoor.color = !cabinKnown ? Muted : cabinAmount > .01f ? NavigationCyan : Ink;
            diagram.Inner.color = !innerKnown ? Muted : innerAmount > .01f ? NavigationCyan : Ink;
            diagram.Ramp.color = !rampKnown ? Muted : rampAmount > .01f ? Amber : Ink;
            diagram.CabinDoor.rectTransform.sizeDelta = new Vector2(cabinKnown ? Mathf.Lerp(14, 3, cabinAmount) : 14, 64);
            diagram.Inner.rectTransform.sizeDelta = new Vector2(innerKnown ? Mathf.Lerp(14, 3, innerAmount) : 14, 64);
            diagram.Ramp.rectTransform.sizeDelta = new Vector2(rampKnown ? Mathf.Lerp(14, 3, rampAmount) : 14, 64);
            diagram.Station.Readout.text = !systems.Ready ? "SYSTEMS SYNC / AWAITING HOST"
                : AirlockCycleStatus(systems) + "\n" + pressureStatus + " / OPEN TOLERANCE 3 kPa"
                    + "\nA02 " + cabinState + " / A01 " + innerState + " / REAR " + rampState
                    + "\nEXTERIOR VENT " + (systems.AirlockVenting ? "OPEN" : "CLOSED")
                    + (systems.EmergencyOverride ? " / INTERLOCK BYPASSED" : " / INTERLOCK ACTIVE");
            diagram.Station.Readout.color = !systems.Ready ? Muted : systems.EmergencyOverride ? Critical
                : systems.AirlockCycle == SpacecraftAirlockPhase.Fault || systems.AirlockVenting ? Amber
                : equalizing ? NavigationCyan : Ink;
        }
    }

    private static string AirlockCycleStatus(SpacecraftSystemState systems)
    {
        var route = systems.AirlockTarget switch
        {
            SpacecraftAirlockTarget.Cabin => "TO CABIN",
            SpacecraftAirlockTarget.Entry => "TO ENTRY",
            _ => "NO TARGET"
        };
        var phase = systems.AirlockCycle switch
        {
            SpacecraftAirlockPhase.Idle => "IDLE",
            SpacecraftAirlockPhase.Sealing => "SEALING BOTH DOORS",
            SpacecraftAirlockPhase.Equalizing => "EQUALIZING",
            SpacecraftAirlockPhase.PassageRequested => "PASSAGE REQUESTED",
            SpacecraftAirlockPhase.Fault => "FAULT",
            _ => "UNKNOWN PHASE"
        };
        if (systems.AirlockCycle != SpacecraftAirlockPhase.Fault) return "CYCLE " + route + " / " + phase;
        var fault = systems.AirlockFault switch
        {
            SpacecraftAirlockFault.None => "UNSPECIFIED",
            SpacecraftAirlockFault.Cancelled => "CANCELLED",
            SpacecraftAirlockFault.Obstructed => "DOOR OBSTRUCTED",
            SpacecraftAirlockFault.PowerLost => "POWER LOST",
            SpacecraftAirlockFault.TravelStarted => "TRAVEL STARTED",
            SpacecraftAirlockFault.OverrideEnabled => "OVERRIDE ENABLED",
            SpacecraftAirlockFault.SealLost => "SEAL LOST",
            SpacecraftAirlockFault.SealTimeout => "SEAL TIMEOUT",
            SpacecraftAirlockFault.PressureTimeout => "PRESSURE TIMEOUT",
            _ => "UNKNOWN FAULT"
        };
        return "CYCLE " + route + " / " + phase + ": " + fault;
    }

    private static void UpdateCompartment(CompartmentReadout compartment, float pressure, float oxygen,
        float co2, float temperature, bool ready)
    {
        var color = !ready ? Muted : pressure < 35 || pressure * oxygen < 10 || co2 > .03f ? Critical
            : pressure < 70 || pressure * oxygen < 16 || co2 > .01f || temperature < 12 || temperature > 35 ? Amber : Ink;
        compartment.Pressure.text = ready ? Number(pressure) + " kPa" : "-- kPa";
        compartment.Gases.text = ready ? "O2 " + Percent(oxygen) + "\nCO2 " + GasPercent(co2) : "O2 --\nCO2 --";
        compartment.Temperature.text = ready ? Number(temperature) + " °C" : "-- °C";
        compartment.Pressure.color = compartment.Gases.color = compartment.Temperature.color = color;
        compartment.Fill.color = color;
        compartment.Fill.rectTransform.sizeDelta = new Vector2(ready && float.IsFinite(pressure) ? compartment.Width * Mathf.Clamp01(pressure / 120) : 0, 2);
    }

    private void UpdateEngineeringReadouts(ClientSpacecraftView view, SpacecraftComputerReport computer)
    {
        var systems = view.Flight.Systems with { Ready = view.Ready && view.Available && view.Flight.Systems.Ready };
        UpdateCompartment(_engineeringCabin, systems.CabinPressure, systems.OxygenFraction,
            systems.CO2Fraction, systems.CabinTemperatureC, systems.Ready);
        UpdateCompartment(_engineeringAirlock, systems.AirlockPressure, systems.AirlockOxygenFraction,
            systems.AirlockCO2Fraction, systems.AirlockTemperatureC, systems.Ready);
        UpdateCompartment(_engineeringEntry, systems.EntryPressure, systems.EntryOxygenFraction,
            systems.EntryCO2Fraction, systems.EntryTemperatureC, systems.Ready);
        _engineeringDoors.text = !systems.Ready ? "DOORS / SYNC"
            : "CABIN - A02 " + DoorPosition(_body.CabinDoorAmount, systems.CabinDoorOpen, true, systems,
                    ClientSpacecraftSystems.CanOpenCabinDoor(systems, _body.InnerDoorClosed, out _))
                + " - CHAMBER - A01 " + DoorPosition(_body.InnerDoorAmount, systems.InnerDoorOpen, true, systems,
                    ClientSpacecraftSystems.CanOpenInnerDoor(systems, _body.CabinDoorClosed, out _))
                + " - ENTRY - REAR " + DoorPosition(view.Flight.DoorAmount, view.Flight.DoorOpen, true, systems,
                    ClientSpacecraftSystems.CanOpenOuter(systems, view.Flight.Region, _body.InnerDoorClosed, _body.CabinDoorClosed, out _));
        var scrubberOpen = PanelValveOpen(systems, SpacecraftValve.CabinScrubber);
        var airlockScrubberOpen = PanelValveOpen(systems, SpacecraftValve.AirlockScrubber);
        var coolingOpen = PanelValveOpen(systems, SpacecraftValve.CabinCooling);
        var airlockCoolingOpen = PanelValveOpen(systems, SpacecraftValve.AirlockCooling);
        _engineeringScrubber.text = ScrubberState(systems, systems.Ready);
        var thermalPowered = SpacecraftElectrical.IsPowered(systems.Electrical, SpacecraftCircuit.Thermal);
        _engineeringCoolant.text = !systems.Ready ? "SYNC" : systems.CoolantLeak ? "LEAK"
            : !thermalPowered ? "NO POWER" : Number(systems.CoolantPressure) + " kPa";
        UpdateStationMeter(_engineeringFuel, systems.Fuel01, systems.Ready, systems.Fuel01 < .15f ? Amber : Ink);
        UpdateStationMeter(_engineeringAir, systems.OxygenReserve01, systems.Ready, systems.OxygenReserve01 < .15f ? Amber : NavigationCyan);
        UpdateStationMeter(_engineeringBattery, systems.Battery01, systems.Ready, systems.Battery01 < .15f ? Amber : Ink);
        UpdateStationMeter(_engineeringCoolantReserve, systems.Coolant01, systems.Ready, systems.Coolant01 < .15f ? Amber : NavigationCyan);
        _circuitSpares.text = !systems.Ready ? "CIRCUIT SPARES / SYNC"
            : "CIRCUIT SPARES " + systems.CircuitSpares.ToString(CultureInfo.InvariantCulture) + " / " + ClientSpacecraftSystems.CircuitSparesCapacity;
        _lineSpares.text = !systems.Ready ? "LINE SPARES / SYNC"
            : "LINE SPARES " + systems.LineSpares.ToString(CultureInfo.InvariantCulture) + " / " + ClientSpacecraftSystems.LineSparesCapacity;
        _circuitSpares.color = !systems.Ready ? Muted : systems.CircuitSpares == 0 ? Amber : Ink;
        _lineSpares.color = !systems.Ready ? Muted : systems.LineSpares == 0 ? Amber : Ink;
        _engineeringScrubber.color = !systems.Ready ? Muted : systems.ScrubberEnabled
            && SpacecraftElectrical.IsPowered(systems.Electrical, SpacecraftCircuit.LifeSupport) ? Ink : Amber;
        _engineeringCoolant.color = !systems.Ready ? Muted : systems.CoolantLeak ? Critical
            : thermalPowered && systems.CoolantPressure > 1 ? Ink : Amber;
        // Labels retain selected valve position; connection colors show faults, not measured flow.
        UpdateCircuitBranch(_cabinScrubberValve, _cabinScrubberConnection, scrubberOpen,
            PanelLineFaulted(systems, SpacecraftValve.CabinScrubber), systems.Ready);
        UpdateCircuitBranch(_airlockScrubberValve, _airlockScrubberConnection, airlockScrubberOpen,
            PanelLineFaulted(systems, SpacecraftValve.AirlockScrubber), systems.Ready);
        UpdateCircuitBranch(_cabinCoolantValve, _cabinCoolantConnection, coolingOpen,
            PanelLineFaulted(systems, SpacecraftValve.CabinCooling), systems.Ready);
        UpdateCircuitBranch(_airlockCoolantValve, _airlockCoolantConnection, airlockCoolingOpen,
            PanelLineFaulted(systems, SpacecraftValve.AirlockCooling), systems.Ready);
        UpdateConnectionFaults(systems);
        _diagnostics.text = computer.Summary + " / " + computer.Action;
        _diagnostics.color = ComputerColor(computer.Mode);
        UpdateDiagnosticRow(_diagnosticRows[0], systems.HullIntegrity01, .3f, systems.Ready,
            "CABIN " + Percent(systems.Hull.Cabin) + "   /   AIRLOCK " + Percent(systems.Hull.Airlock)
                + "   /   ENTRY " + Percent(systems.Hull.Entry));
        UpdateDiagnosticRow(_diagnosticRows[1], systems.EngineIntegrity01, .15f, systems.Ready,
            "ENGINE " + (view.Flight.Powered ? "ON" : "OFF") + "   /   FUEL " + Percent(systems.Fuel01)
                + "   /   FEED " + Number(systems.FuelFeedPressure) + " kPa");
        UpdateDiagnosticRow(_diagnosticRows[2], systems.PowerIntegrity01, .08f, systems.Ready,
            "APU " + GeneratorState(systems, systems.Ready)
                + "   /   BATTERY " + Percent(systems.Battery01));
        UpdateDiagnosticRow(_diagnosticRows[3], systems.LifeSupportIntegrity01, .1f, systems.Ready,
            "SCRUBBER " + ScrubberState(systems, systems.Ready) + "   /   O2 " + Percent(systems.OxygenFraction)
                + "   /   CO2 " + GasPercent(systems.CO2Fraction));
        UpdateDiagnosticRow(_diagnosticRows[4], systems.WarpIntegrity01, .35f, systems.Ready,
            "CHARGE " + Percent(systems.WarpCharge01) + "   /   "
                + (ClientSpacecraftComputer.CanWarp(systems) ? "DRIVE READY" : "NOT READY"));
    }

    private static void UpdateCircuitBranch(TextMeshProUGUI label, Image line, bool open, bool fault, bool ready)
    {
        label.text = !ready ? "SYNC" : fault ? open ? "OPEN / FAULT" : "SHUT / FAULT" : open ? "OPEN" : "SHUT";
        label.color = !ready ? Muted : fault ? Critical : open ? NavigationCyan : Amber;
        line.color = !ready ? ScreenRule : fault ? Critical : open ? NavigationCyan : Amber;
    }

    private void UpdateConnectionFaults(SpacecraftSystemState systems)
    {
        var count = 0;
        var names = "";
        for (var index = 0; index < ValveTitles.Length; index++)
        {
            if (!ClientSpacecraftSystems.LineNeedsRepair(systems, (SpacecraftValve)index)) continue;
            if (count < 2) names += (count == 0 ? "" : ", ") + ValveTitles[index].Replace('\n', ' ')
                + (index == (int)SpacecraftValve.CoolantFeed && systems.CoolantLeak ? " LEAK" : "");
            count++;
        }
        for (var index = 0; index < _electricalStates.Length; index++)
        {
            if ((systems.Electrical.TrippedMask & (1 << index)) == 0) continue;
            if (count < 2) names += (count == 0 ? "" : ", ")
                + SpacecraftElectrical.CircuitName((SpacecraftCircuit)index).ToUpperInvariant() + " BREAKER";
            count++;
        }
        _connectionFaults.text = !systems.Ready ? "CONNECTIONS / SYNC" : count == 0 ? "CONNECTIONS / NO FAULTS"
            : "FAILED CONNECTIONS " + count.ToString(CultureInfo.InvariantCulture) + " / " + names
                + (count > 2 ? " / +" + (count - 2).ToString(CultureInfo.InvariantCulture) + " MORE" : "");
        _connectionFaults.color = !systems.Ready ? Muted : count > 0 ? Critical : Ink;
    }

    private static void UpdateDiagnosticRow(DiagnosticRow row, float integrity, float failed, bool ready, string detail)
    {
        var color = !ready ? Muted : integrity <= failed ? Critical : integrity < .999f ? Amber : Ink;
        row.Value.text = ready ? Percent(integrity) : "--";
        row.Value.color = color;
        row.Fill.color = color;
        row.Fill.rectTransform.sizeDelta = new Vector2(ready && float.IsFinite(integrity) ? 150 * Mathf.Clamp01(integrity) : 0, 5);
        row.Detail.text = ready ? detail : "AWAITING HOST TELEMETRY";
        row.Detail.color = ready ? Ink : Muted;
    }

    private static Color ComputerColor(SpacecraftComputerMode mode) => mode switch
    {
        SpacecraftComputerMode.Emergency => Critical,
        SpacecraftComputerMode.Warning or SpacecraftComputerMode.Advisory => Amber,
        SpacecraftComputerMode.Offline => Muted,
        _ => Ink
    };

    private void UpdateNavigationReadouts(ClientSpacecraftView view)
    {
        var systems = view.Flight.Systems;
        var warpReady = ClientSpacecraftComputer.CanWarp(systems, out var reason);
        _navigationOrigin.text = systems.Ready ? "CURRENT / " + SpacecraftPresentation.Region(view.Flight.Region).ToUpperInvariant() : "CURRENT / AWAITING HOST";
        _navigationStatus.text = !systems.Ready ? "AWAITING HOST TELEMETRY" : view.Flight.Travel != SpacecraftTravel.None
            ? "TRANSFER / " + view.Flight.Travel.ToString().ToUpperInvariant() : warpReady ? "WARP DRIVE / READY" : reason;
        _navigationStatus.color = !systems.Ready ? Muted : warpReady ? NavigationCyan : Amber;
        _navigationChartKind.text = _mapMode == 0 ? "SAMPLED TERRAIN\nGAPS / NO DATA"
            : _mapMode == 1 ? "SCHEMATIC\nNOT TO SCALE" : "LOG DISTANCE\nNOT TO SCALE";
        UpdateStationMeter(_warpMeter, systems.WarpCharge01, systems.Ready, NavigationCyan);
        _navigationZoom.text = "ZOOM " + _zoom.ToString("0.00", CultureInfo.InvariantCulture) + "x";
        _navigationZoomCursor.rectTransform.anchoredPosition = new Vector2(132 + (_zoom - .55f) / .6f * 696, -351);
    }

    private static void UpdateStationMeter(StationMeter meter, float value, bool ready, Color color)
    {
        meter.Value.text = ready ? Percent(value) : "--";
        meter.Value.color = ready ? color : Muted;
        meter.Fill.color = color;
        meter.Fill.rectTransform.sizeDelta = new Vector2(ready && float.IsFinite(value) ? meter.Width * Mathf.Clamp01(value) : 0, 4);
    }

    private void UpdateStationKey(StationButton button, ClientSpacecraftView view)
    {
        var systems = view.Flight.Systems;
        var cycleActive = systems.Ready && systems.AirlockCycle is SpacecraftAirlockPhase.Sealing
            or SpacecraftAirlockPhase.Equalizing;
        button.Selected = button.Action switch
        {
            "@operations" => _engineeringPage == EngineeringPage.Operations,
            "@diagnostics" => _engineeringPage == EngineeringPage.Diagnostics,
            "@electrical" => _engineeringPage == EngineeringPage.Electrical,
            "@service" => _engineeringPage == EngineeringPage.Service,
            "@local" => _mapMode == 0,
            "@planet" => _mapMode == 1,
            "@system" => _mapMode == 2,
            "select-island" => systems.Ready && systems.SelectedDestination == SpacecraftRegion.Island,
            "select-world" => systems.Ready && systems.SelectedDestination == SpacecraftRegion.TestWorld,
            "select-moon" => systems.Ready && systems.SelectedDestination == SpacecraftRegion.Moon,
            "airlock-to-cabin" => cycleActive && systems.AirlockTarget == SpacecraftAirlockTarget.Cabin,
            "airlock-to-entry" => cycleActive && systems.AirlockTarget == SpacecraftAirlockTarget.Entry,
            _ => false
        };
        if (button.Face != null)
            button.Face.color = button.Selected ? new Color(.035f, .065f, .075f, 1) : KeyFace;
        if (button.Accent != null)
            button.Accent.color = button.Selected ? NavigationCyan : button.Action is "override" or "@arm" ? Amber
                : !systems.Ready ? ScreenRule : button.Action switch
                {
                    "scrubber" => !systems.ScrubberEnabled ? Muted
                        : SpacecraftElectrical.IsPowered(systems.Electrical, SpacecraftCircuit.LifeSupport) ? Green : Amber,
                    "generator" => systems.ApuIsolated ? Amber
                        : systems.GeneratorEnabled && systems.Electrical.GenerationKw > 0 ? Green : Muted,
                    "power" => view.Flight.Powered ? Green : Muted,
                    "dampeners" => view.Flight.Dampeners ? Green : Muted,
                    "gear" => view.Flight.GearDeployed ? Green : Muted,
                    "shore-air" => ShoreAirColor(view),
                    "vent" => systems.AirlockVenting ? Amber : ScreenRule,
                    "airlock-cancel" => cycleActive ? Amber : ScreenRule,
                    _ => ScreenRule
                };
    }

    private Image StationGraphic(Transform parent, string name, float x, float y, float width, float height, Color color)
    {
        var image = Node(parent, name, x, y, width, height).gameObject.AddComponent<Image>();
        image.material = _graphics; image.color = color; image.raycastTarget = false;
        return image;
    }

    private void StationStroke(Transform parent, string name, Vector2 from, Vector2 to, Color color)
    {
        var delta = to - from;
        var image = StationGraphic(parent, name, from.x, from.y, delta.magnitude, 2, color);
        image.rectTransform.localRotation = Quaternion.Euler(0, 0, -Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
    }

    private void BuildLocalChart(Transform parent)
    {
        var grid = new GameObject("Sampled surface grid") { layer = parent.gameObject.layer };
        grid.transform.SetParent(parent, false);
        const int edges = 2 * TerrainSide * (TerrainSide - 1);
        _terrainVertices = new Il2CppStructArray<Vector3>(size: edges * 4);
        var triangles = new int[edges * 12];
        var face = new[] { 0, 1, 2, 2, 1, 3, 2, 1, 0, 3, 1, 2 };
        for (var edge = 0; edge < edges; edge++)
        {
            for (var index = 0; index < face.Length; index++)
                triangles[edge * 12 + index] = edge * 4 + face[index];
        }
        _terrainMesh = new Mesh { name = "Local survey grid" };
        _terrainMesh.MarkDynamic();
        _terrainMesh.vertices = _terrainVertices;
        _terrainMesh.triangles = triangles;
        grid.AddComponent<MeshFilter>().sharedMesh = _terrainMesh;
        var renderer = grid.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = _cyanLine;
        ClientDeviceScreenMaterial.ConfigureRenderer(renderer);
        _shipMarker = new GameObject("Ship ground position").transform;
        _shipMarker.gameObject.layer = parent.gameObject.layer;
        _shipMarker.SetParent(parent, false);
        Line(_shipMarker, "Ship heading", new[] { new Vector3(0, 0, .018f), new Vector3(.012f, 0, -.012f),
            Vector3.zero, new Vector3(-.012f, 0, -.012f) }, true, _selectedLine);
    }

    private void BuildPlanetChart(Transform parent)
    {
        var globe = new GameObject("Selected world").transform;
        globe.gameObject.layer = parent.gameObject.layer; globe.SetParent(parent, false);
        globe.localPosition = new Vector3(-.035f, .05f, 0);
        _planetSurface = Globe(globe, .12f, _globeSurfaces[0]);
        var orbitAxis = new Vector3(0, .32f, .9474f);
        Ring(globe, "Schematic orbit", .195f, Vector3.right, orbitAxis, _orbitLine, .0012f);
        _planetSelection = new GameObject("Selected destination ring").transform;
        _planetSelection.gameObject.layer = parent.gameObject.layer; _planetSelection.SetParent(globe, false);
        SelectionRing(_planetSelection, .15f, orbitAxis);
        // This marks the selected body, not an invented landing coordinate.
        var marker = new Vector3(.15f, 0, 0);
        Line(_planetSelection, "Destination leader", new[] { marker, new Vector3(.20f, .05f, -.10f),
            new Vector3(.22f, .05f, -.15f) }, false, _amberLine, .0012f);
        Ring(_planetSelection, "Destination reticle", .012f, Vector3.right, Vector3.up, _amberLine, .0018f)
            .transform.localPosition = marker;
        Ring(_planetSelection, "Reticle core", .005f, Vector3.right, Vector3.up, _amberLine, .0012f)
            .transform.localPosition = marker;
        _planetTitle = MapLabel(globe, "DESTINATION", Amber, new Vector3(.22f, .05f, -.195f));
    }

    private void SelectionRing(Transform parent, float radius, Vector3 axis)
    {
        // Broken arcs frame a selected body, not a simulated orbital trajectory.
        for (var quadrant = 0; quadrant < 4; quadrant++)
        {
            var points = new Vector3[13];
            for (var index = 0; index < points.Length; index++)
            {
                var angle = (quadrant * 90 + 12 + index * 5.5f) * Mathf.Deg2Rad;
                points[index] = (Vector3.right * Mathf.Cos(angle) + axis * Mathf.Sin(angle)) * radius;
            }
            Line(parent, "Selection arc", points, false, _amberLine, .002f);
        }
    }

    private TextMeshProUGUI MapLabel(Transform marker, string title, Color color, Vector3 position)
    {
        var canvas = new GameObject(title + " map label").AddComponent<RectTransform>();
        canvas.gameObject.layer = marker.gameObject.layer; canvas.SetParent(marker, false);
        canvas.localPosition = position; canvas.localRotation = Quaternion.Euler(55, 0, 0);
        canvas.localScale = Vector3.one * .00065f; canvas.sizeDelta = new Vector2(240, 40);
        canvas.gameObject.AddComponent<Canvas>().renderMode = RenderMode.WorldSpace;
        var label = Label(canvas, title, title, 0, 0, 240, 40, 24, color);
        label.alignment = TextAlignmentOptions.Center;
        return label;
    }

    internal bool Update(ClientSpacecraftView view, bool interactive, ReadOnlySpan<SpacecraftCrewInteraction> interactions)
    {
        if (_disposed || _root == null || !_root) return false;
        var visible = _body.Ready && GameRuntime.State.IsInWorld;
        _root.SetActive(visible);
        if (!visible)
        {
            CancelControlPress(); Disarm();
            _sparesPressedUntil = 0;
            ResetSealantPresses();
            ResetSystemServiceControls();
            _warpDischargeAngleInitialized = false;
            return false;
        }
        if (_mapMode == 0)
        {
            var offset = view.Flight.Position - _terrainCenter;
            if (_terrainDirty || view.Flight.Region != _terrainRegion
                || Time.unscaledTime >= _nextTerrainRefresh && (offset.x * offset.x + offset.z * offset.z > _terrainSpan * _terrainSpan / 16
                    || _terrainSamples == 0 && view.Flight.Region != SpacecraftRegion.Space))
                RefreshTerrain(view.Flight);
            UpdateShipMarker(view.Flight);
        }
        if (Time.unscaledTime >= _nextReadout)
        {
            _nextReadout = Time.unscaledTime + .12f;
            UpdateReadouts(view);
        }
        var player = LocalPlayer.Transform;
        var inCabin = player != null && _body.ContainsCabin(player.position);
        var acceptsInput = interactive && view.Ready && view.Available && !view.Boarding && (view.CabinWalking || view.Seat < 0)
            && ClientSpacecraftRider.WorldInputAvailable && inCabin
            && !Cursor.visible && (!InputBindings.HasFocus || view.CabinWalking || _rider.ControlHandActive);
        if (_armedStation != null && (Time.unscaledTime >= _armedUntil || !acceptsInput
            || LocalPlayer.Transform == null || (_armedStation.Frame.position - LocalPlayer.Transform.position).sqrMagnitude > 9)) Disarm();
        // Physical contacts must follow the current state even between telemetry refreshes.
        UpdateEngineeringPanelControls(view);
        UpdateWarpServiceControls(view);
        var camera = LocalPlayer.MainCam;
        _hovered = acceptsInput && camera != null && camera && camera.isActiveAndEnabled ? FindButton(camera) : null;
        if (_hovered != null && !CanReachControl(_hovered, view)) _hovered = null;
        UpdateControlPress(view, acceptsInput);
        UpdateSparesButton(view, interactions);
        UpdateSealantButtons(view, interactions);
        UpdateSystemServiceControls(view);
        foreach (var station in _stations)
        {
            if (station.Buttons.Count == 0) continue;
            foreach (var button in station.Buttons)
            {
                var focused = ReferenceEquals(button, _hovered);
                var usable = CanUse(button, view);
                var warning = button.Action is "override" or "@arm";
                button.Label.color = focused ? usable ? NavigationCyan : Amber
                    : !usable ? Muted : warning ? Amber : button.Selected ? NavigationCyan : Ink;
                // A fixed panel highlight would point behind the moving cover.
                var showFocus = focused && button.Action is not ("apu-guard-open" or "apu-guard-close");
                if (button.Focus.gameObject.activeSelf != showFocus) button.Focus.gameObject.SetActive(showFocus);
                if (focused) button.Focus.color = usable ? new Color(.35f, .95f, .72f, .22f) : new Color(1, .72f, .28f, .22f);
            }
            if (IsCabinPanel(station)) continue;
            var hovered = _hovered;
            station.Prompt.text = _commandStation == station && Time.unscaledTime < _feedbackUntil
                && !string.IsNullOrWhiteSpace(view.Feedback) && view.Feedback != _feedbackBefore ? view.Feedback
                : hovered != null && hovered.Station == station
                ? ClientSpacecraftSystems.TryManualRepair(hovered.Action, out _, out _) ? RepairControlPrompt(hovered, view)
                    : CanUse(hovered, view) ? "E · " + hovered.Label.text : BlockedReason(hovered, view)
                : _armedStation == station ? "OVERRIDE ARMED · Look at CONFIRM and press E" : "";
        }
        UpdatePanelPrompts(view);
        ShowControlFeedback();
        return _hovered != null || _rider.ControlHandActive;
    }

    private StationButton? FindButton(Camera camera)
    {
        var ray = new Ray(camera.transform.position, camera.transform.forward);
        StationButton? selected = null;
        var nearest = 2.4f;
        foreach (var station in _stations)
        {
            if (station.Buttons.Count == 0) continue;
            // Use the visible key plane; a recessed hit plane shifts targets at oblique viewing angles.
            var plane = new Plane(station.Frame.forward, station.Canvas.position);
            var hitsPlane = plane.Raycast(ray, out var planeDistance) && planeDistance >= 0;
            var point = station.Frame.InverseTransformPoint(ray.GetPoint(planeDistance));
            var pixel = new Vector2(point.x * 1000 + station.Width / 2, station.Height / 2 - point.y * 1000);
            foreach (var button in station.Buttons)
            {
                if (!button.Key.gameObject.activeSelf) continue;
                var guard = button.Action is "apu-guard-open" or "apu-guard-close";
                // The hinged guard exposes faces outside the fixed panel's facing direction.
                if (!guard && Vector3.Dot(ray.direction, station.Frame.forward) <= .1f) continue;
                var distance = planeDistance;
                var hit = guard ? ApuGuardRaycast(ray, out distance)
                    : button.Contact != null ? button.Contact.Raycast(ray, out distance)
                    : hitsPlane && button.Bounds.Contains(pixel);
                if (hit && distance >= 0 && distance < nearest) { selected = button; nearest = distance; }
            }
        }
        if (selected != null)
        {
            if (selected.Station == _apuPanel && selected.Action is "apu-isolate" or "apu-reset"
                && ApuGuardRaycast(ray, out var guardDistance) && guardDistance < nearest) return null;
            _controlHits ??= new Il2CppStructArray<RaycastHit>(32);
            var count = Physics.RaycastNonAlloc(ray, _controlHits, Mathf.Max(0, nearest - .03f), ~0, QueryTriggerInteraction.Ignore);
            if (count >= _controlHits.Length) return null;
            var player = LocalPlayer.Transform;
            for (var index = 0; index < count; index++)
            {
                var collider = _controlHits[index].collider;
                if (collider != null && (player == null || !collider.transform.IsChildOf(player))) return null;
            }
        }
        return selected;
    }

    private bool CanUse(StationButton button, ClientSpacecraftView view)
    {
        var action = button.Action;
        if (button.Station == _apuPanel)
        {
            if (!view.Ready || !view.Available || !view.Flight.Systems.Ready
                || view.Flight.Travel != SpacecraftTravel.None || view.Boarding || !view.CabinWalking && view.Seat >= 0) return false;
            var apu = view.Flight.Systems;
            if (action == "apu-guard-open") return !apu.ApuGuardOpen;
            if (action == "apu-guard-close") return apu.ApuGuardOpen;
            if (!apu.ApuGuardOpen || apu.ApuGuardAmount < 1) return false;
            var reset = apu.ApuIsolated;
            if (reset && !ApuResetAvailable(view)) return false;
            return action == (reset ? "apu-reset" : "apu-isolate");
        }
        if (action.StartsWith("@", StringComparison.Ordinal)) return action != "@arm"
            || view.Flight.Systems.Ready && !view.Flight.Systems.EmergencyOverride && view.Flight.Travel == SpacecraftTravel.None;
        if (action == "shore-air") return _engineeringPage == EngineeringPage.Service && ShoreAirReady(view)
            && (view.Flight.Systems.ShoreAirEnabled || ShoreAirAtBerth(view));
        if (!view.Ready || !view.Available || !view.Flight.Systems.Ready || view.Flight.Travel != SpacecraftTravel.None) return false;
        var systems = view.Flight.Systems;
        if (ClientSpacecraftSystems.TryReadSealantCommand(action, out var compartment)) return CanStartSealant(compartment, view);
        if (ClientSpacecraftSystems.TryManualRepair(action, out _, out _))
            return !view.Boarding && view.CabinWalking
                && (!ClientSpacecraftSystems.TryReadCircuitRepairCommand(action, out _) || _engineeringPage == EngineeringPage.Electrical)
                && ClientSpacecraftSystems.CanManualRepair(action, systems, view.Flight.Powered, view.Flight.Travel, out _);
        if (IsSystemServiceCommand(action)) return CanUseSystemService(action, view);
        if (IsWarpServiceCommand(action)) return CanUseWarpService(action, view);
        if (action == "generator") return systems.GeneratorEnabled || !systems.BusIsolated;
        if (action == "load-spares")
            return !view.Boarding && view.CabinWalking && view.Flight.Docked
                && (systems.LineSpares < ClientSpacecraftSystems.LineSparesCapacity
                    || systems.CircuitSpares < ClientSpacecraftSystems.CircuitSparesCapacity
                    || systems.HullPlates < ClientSpacecraftSystems.HullPlatesCapacity);
        if (ClientSpacecraftSystems.TryReadValveCommand(action, out var valve, out var valveOpen))
            return valveOpen ? !PanelValveOpen(systems, (SpacecraftValve)valve) && !PanelLineFaulted(systems, (SpacecraftValve)valve)
                    && (valve != (int)SpacecraftValve.WarpFuel || !systems.WarpDischargeEnabled)
                : PanelValveOpen(systems, (SpacecraftValve)valve);
        if (ClientSpacecraftSystems.TryReadCircuitCommand(action, out var circuit, out var open))
        {
            var electrical = view.Flight.Systems.Electrical;
            var bit = 1 << circuit;
            return _engineeringPage == EngineeringPage.Electrical && ElectricalReady(view)
                && (open || circuit != (int)SpacecraftCircuit.Warp || !systems.WarpDischargeEnabled)
                && (open ? (electrical.ClosedMask & bit) != 0
                    : (electrical.ClosedMask & bit) == 0 && (electrical.TrippedMask & bit) == 0);
        }
        if (action is "crew-lock" or "approve-crew" or "deny-crew")
            return view.IsHost && (action == "crew-lock" || view.CrewRequest.Length != 0);
        if (action == "override") return view.Flight.Systems.EmergencyOverride
            || _armedStation == button.Station && Time.unscaledTime < _armedUntil;
        if (action == "gear") return !view.Flight.Grounded || !view.Flight.GearDeployed;
        return true;
    }

    private string BlockedReason(StationButton button, ClientSpacecraftView view)
    {
        if (!view.Ready || !view.Available || !view.Flight.Systems.Ready) return "Waiting for host system state";
        if (view.Flight.Travel != SpacecraftTravel.None) return "Controls locked during travel";
        var systems = view.Flight.Systems;
        if (ClientSpacecraftSystems.TryReadSealantCommand(button.Action, out var compartment)) return SealantBlockedReason(compartment, view);
        if (ClientSpacecraftSystems.TryManualRepair(button.Action, out _, out _))
        {
            if (view.Boarding || !view.CabinWalking) return "Use a walking attachment to repair the ship";
            if (ClientSpacecraftSystems.TryReadCircuitRepairCommand(button.Action, out _) && _engineeringPage != EngineeringPage.Electrical)
                return "Open the electrical page";
            return ClientSpacecraftSystems.CanManualRepair(button.Action, systems, view.Flight.Powered, view.Flight.Travel, out var failure)
                ? _manualRepairPrompt(button.Action) : failure;
        }
        if (IsSystemServiceCommand(button.Action)) return SystemServiceBlockedReason(button.Action, view);
        if (IsWarpServiceCommand(button.Action)) return WarpServiceBlockedReason(button.Action, view);
        if (button.Action == "generator" && systems.BusIsolated) return "Connect the main bus before starting the generator";
        if (button.Action == "load-spares")
            return view.Boarding || !view.CabinWalking ? "Leave the seat to load repair stores"
                : !view.Flight.Docked ? "Latch the ship to the station berth"
                : "Repair stores are full";
        if (button.Station == _apuPanel)
            return view.Boarding || !view.CabinWalking && view.Seat >= 0 ? "Leave the seat to operate APU hardware"
                : button.Action is "apu-guard-open" or "apu-guard-close" ? "Waiting for the host guard state"
                : !systems.ApuGuardOpen ? "Open the guard before operating the lever"
                : systems.ApuGuardAmount < 1 ? "Wait for the guard to finish opening"
                : systems.ApuIsolated && !ApuResetAvailable(view) ? "Park on the island and fully open the ramp for ground service"
                : "Waiting for the host APU state";
        if (ClientSpacecraftSystems.TryReadValveCommand(button.Action, out var valve, out var valveOpen))
        {
            if (valveOpen && valve == (int)SpacecraftValve.WarpFuel && systems.WarpDischargeEnabled)
                return "Return the warp service selector to RUN before opening its fuel valve";
            return valveOpen && PanelLineFaulted(systems, (SpacecraftValve)valve) ? "Repair this isolated connection before opening"
                : valveOpen ? "Valve already open" : "Valve already closed";
        }
        if (ClientSpacecraftSystems.TryReadCircuitCommand(button.Action, out var circuit, out var open))
        {
            if (!open && circuit == (int)SpacecraftCircuit.Warp && systems.WarpDischargeEnabled)
                return "Return the warp service selector to RUN before closing its breaker";
            return !ElectricalReady(view) ? "Waiting for host electrical state"
                : _engineeringPage != EngineeringPage.Electrical ? "Open the electrical page"
                : !open && (systems.Electrical.TrippedMask & (1 << circuit)) != 0 ? "Isolate and repair this breaker before closing"
                : open ? "Circuit already isolated" : "Circuit already closed";
        }
        return button.Action switch
        {
            "crew-lock" => "Host control",
            "approve-crew" or "deny-crew" => view.IsHost ? "No pending crew request" : "Host control",
            "override" => "ARM, then CONFIRM · Crew exposure risk",
            "@arm" => "Use RESTORE INTERLOCK to disable the bypass",
            "gear" => "Lift clear of the ground before retracting gear",
            "shore-air" => _engineeringPage != EngineeringPage.Service ? "Open the service page" : "Dock at the station berth",
            _ => "Waiting for the host"
        };
    }

    private void Activate(StationButton button, ClientSpacecraftView view)
    {
        switch (button.Action)
        {
            case "@operations": SetComputerPage(EngineeringPage.Operations); return;
            case "@diagnostics": SetComputerPage(EngineeringPage.Diagnostics); return;
            case "@electrical": SetComputerPage(EngineeringPage.Electrical); return;
            case "@service": SetComputerPage(EngineeringPage.Service); return;
            case "@local": _mapMode = 0; UpdateMapMode(); return;
            case "@planet": _mapMode = 1; UpdateMapMode(); return;
            case "@system": _mapMode = 2; UpdateMapMode(); return;
            case "@zoom-in": _zoom = Mathf.Min(1.15f, _zoom + .15f); UpdateMapMode(); return;
            case "@zoom-out": _zoom = Mathf.Max(.55f, _zoom - .15f); UpdateMapMode(); return;
            case "@arm":
                if (view.Flight.Systems.EmergencyOverride) return;
                _armedStation = button.Station; _armedUntil = Time.unscaledTime + 8; _nextReadout = 0; return;
        }
        if (button.Action == "override") Disarm();
        if (button.Action == "load-spares") _sparesPressedUntil = Time.unscaledTime + .16f;
        if (ClientSpacecraftSystems.TryReadSealantCommand(button.Action, out var compartment))
            _sealantPanels[(int)compartment].PressedUntil = Time.unscaledTime + .16f;
        _commandStation = button.Station; _feedbackBefore = view.Feedback; _feedbackUntil = Time.unscaledTime + 6;
        _command(button.Action is "approve-crew" or "deny-crew"
            ? button.Action + ":" + button.CrewRequestKey : button.Action);
        _nextReadout = 0;
    }

    private void Disarm()
    {
        _armedStation = null; _armedUntil = 0; _nextReadout = 0;
    }

    private void SetComputerPage(EngineeringPage page)
    {
        _engineeringPage = page;
        var operations = page == EngineeringPage.Operations;
        var diagnostics = page == EngineeringPage.Diagnostics;
        _engineering.Readout.gameObject.SetActive(!diagnostics);
        _operationsArt.gameObject.SetActive(operations);
        _diagnostics.gameObject.SetActive(diagnostics);
        _diagnosticsArt.gameObject.SetActive(diagnostics);
        _electricalArt.gameObject.SetActive(page == EngineeringPage.Electrical);
        _shoreAirArt.gameObject.SetActive(page == EngineeringPage.Service);
        foreach (var button in _engineering.Buttons)
        {
            if (button.Action.StartsWith("@", StringComparison.Ordinal)) continue;
            var visible = button.Action == "shore-air" ? page == EngineeringPage.Service
                : ClientSpacecraftSystems.TryReadCircuitCommand(button.Action, out _, out _)
                || ClientSpacecraftSystems.TryReadCircuitRepairCommand(button.Action, out _)
                ? page == EngineeringPage.Electrical : operations;
            button.Key.gameObject.SetActive(visible);
            button.Label.gameObject.SetActive(visible);
        }
        _nextReadout = 0;
    }

    private void UpdateReadouts(ClientSpacecraftView view)
    {
        var systems = view.Flight.Systems;
        var computer = ClientSpacecraftComputer.Analyze(systems);
        UpdateEngineeringReadouts(view, computer);
        UpdateEngineeringOverview(view, computer);
        UpdateElectricalReadouts(view);
        UpdateShoreAirReadouts(view);
        UpdateAirlockDiagrams(view);
        UpdateNavigationReadouts(view);
        foreach (var station in _stations)
        {
            if (station.Buttons.Count == 0 || IsCabinPanel(station)) continue;
            if (station.Kind == "Engineering")
            {
                station.Readout.text = computer.Summary;
                station.Readout.color = ComputerColor(computer.Mode);
            }
            else if (station.Kind == "Navigation")
            {
                station.Readout.text = systems.Ready ? SpacecraftPresentation.Region(systems.SelectedDestination).ToUpperInvariant() : "AWAITING HOST";
                station.Readout.color = systems.Ready ? Ink : Muted;
            }
            foreach (var button in station.Buttons)
            {
                var destinationKey = station.Kind == "Navigation" && button.Action.StartsWith("select-", StringComparison.Ordinal);
                button.Label.text = destinationKey ? button.Title : ButtonLabel(button, view);
                button.CrewRequestKey = view.CrewRequestKey;
                UpdateStationKey(button, view);
                if (destinationKey)
                {
                    if (button.Face != null) button.Face.color = button.Selected ? new Color(.085f, .062f, .028f, 1) : KeyFace;
                    if (button.Accent != null) button.Accent.color = button.Selected ? Amber : ScreenRule;
                }
                else if (station.Kind == "Navigation" && (button.Action is "@zoom-in" or "@zoom-out"))
                {
                    if (button.Face != null) button.Face.color = new Color(.018f, .022f, .025f, 1);
                    if (button.Accent != null) button.Accent.color = ScreenRule;
                }
            }
        }
        var selected = ChartIndex(systems.SelectedDestination);
        var current = ChartIndex(view.Flight.Region);
        for (var index = 0; index < _destinationStates.Length; index++)
        {
            var state = _destinationStates[index];
            state.text = !systems.Ready ? "AWAITING HOST" : index == selected && index == current ? "CURRENT / SELECTED"
                : index == selected ? "SELECTED" : index == current ? "CURRENT" : "";
            state.color = !systems.Ready ? Muted : index == selected ? Amber : Green;
        }
        UpdateSystemChart(view);
        _planetSelection.gameObject.SetActive(systems.Ready && selected >= 0);
        _planetSurface.transform.parent.gameObject.SetActive(systems.Ready && selected >= 0);
        if (systems.Ready && selected >= 0)
        {
            if (_planetSurface.sharedMaterial != _globeSurfaces[selected]) _planetSurface.sharedMaterial = _globeSurfaces[selected];
            _planetTitle.text = SpacecraftPresentation.Region(systems.SelectedDestination).ToUpperInvariant();
        }
        _mapCaption.text = _mapMode == 2 ? _orbitCaption : !systems.Ready ? "CHART / AWAITING HOST" : (_mapMode == 0
            ? _terrainSamples == 0 ? "LOCAL / NO SURFACE DATA" : "LOCAL / " + _terrainSpan.ToString("0", CultureInfo.InvariantCulture) + " m"
                + (_mapModes[0].transform.localScale.y < .99f ? " / RELIEF SCALED" : " / NORTH UP")
            : "PLANET / " + SpacecraftPresentation.Region(systems.SelectedDestination).ToUpperInvariant());
        UpdateCabinPanels(view);
        UpdateAuxiliaryDisplays(view);
    }

    private string ButtonLabel(StationButton button, ClientSpacecraftView view)
    {
        var systems = view.Flight.Systems;
        if (button.Action is "@operations" or "@diagnostics" or "@electrical" or "@service" or "@local" or "@planet" or "@system") return button.Title;
        if (ClientSpacecraftSystems.TryReadCircuitCommand(button.Action, out _, out var open))
            return open ? "ISOLATE" : "CLOSE";
        if (!systems.Ready) return button.Title;
        return button.Action switch
        {
            "select-island" => systems.SelectedDestination == SpacecraftRegion.Island ? "ISLAND / SET" : button.Title,
            "select-world" => systems.SelectedDestination == SpacecraftRegion.TestWorld ? "WORLD / SET" : button.Title,
            "select-moon" => systems.SelectedDestination == SpacecraftRegion.Moon ? "MOON / SET" : button.Title,
            "cabin-door" => systems.CabinDoorOpen ? "CLOSE CABIN" : "OPEN CABIN",
            "crew-lock" => systems.CrewLocked ? "CREW LOCK / ON" : "CREW LOCK / OFF",
            "airlock-to-cabin" => "CYCLE TO CABIN",
            "airlock-to-entry" => "CYCLE TO ENTRY",
            "airlock-cancel" => "CANCEL CYCLE",
            "inner-door" => systems.InnerDoorOpen ? "CLOSE A01 DOOR" : "OPEN A01 DOOR",
            "door" => view.Flight.DoorOpen ? "CLOSE REAR RAMP" : "OPEN REAR RAMP",
            "vent" => systems.AirlockVenting ? "STOP VENTING" : "VENT CHAMBER",
            "@arm" => systems.EmergencyOverride ? "BYPASS ACTIVE" : _armedStation == button.Station ? "OVERRIDE ARMED" : "ARM OVERRIDE",
            "override" => systems.EmergencyOverride ? "RESTORE INTERLOCK" : "CONFIRM OVERRIDE",
            "scrubber" => "SCRUBBER / " + ScrubberState(systems, view.Ready && view.Available),
            "generator" => "GENERATOR / " + GeneratorState(systems, view.Ready && view.Available),
            "power" => "ENGINE / " + (view.Flight.Powered ? "ON" : "OFF"),
            "dampeners" => "DAMP / " + (view.Flight.Dampeners ? "ON" : "OFF"),
            "gear" => view.Flight.GearDeployed ? "RETRACT GEAR" : "DEPLOY GEAR",
            "shore-air" => ShoreAirReady(view) ? systems.ShoreAirEnabled ? "STOP AIR SERVICE" : "START AIR SERVICE" : button.Title,
            _ => button.Title
        };
    }

    private void UpdateMapMode()
    {
        for (var index = 0; index < _mapModes.Length; index++) _mapModes[index].SetActive(index == _mapMode);
        // The wider solar chart sits port and aft of the hanging monitor.
        _map.localPosition = _mapMode == 0 ? new Vector3(.206f, -.02f, -1.15f)
            : _mapMode == 2 ? SystemProjectionPosition : ProjectionPosition;
        _map.localScale = Vector3.one * ((_mapMode == 2 ? SystemProjectionScale : ProjectionScale) * (_mapMode == 0 ? 1 : _zoom));
        if (_mapMode == 0) _terrainDirty = true;
        _nextReadout = 0;
    }

    private static int ChartIndex(SpacecraftRegion region) => region switch
    {
        SpacecraftRegion.Island => 0,
        SpacecraftRegion.TestWorld => 1,
        SpacecraftRegion.Moon => 2,
        _ => -1
    };

    private void RefreshTerrain(SpacecraftFlightState flight)
    {
        _terrainDirty = false; _nextTerrainRefresh = Time.unscaledTime + 4;
        _terrainCenter = flight.Position; _terrainRegion = flight.Region; _terrainSpan = 160 / _zoom;
        _terrainSamples = 0; _terrainDatum = float.PositiveInfinity;
        var peak = float.NegativeInfinity;
        _mapModes[0].transform.localScale = Vector3.one;
        for (var z = 0; z < TerrainSide; z++)
        for (var x = 0; x < TerrainSide; x++)
        {
            var index = z * TerrainSide + x;
            var point = _terrainCenter + new Vector3((x / (float)(TerrainSide - 1) - .5f) * _terrainSpan,
                0, (z / (float)(TerrainSide - 1) - .5f) * _terrainSpan);
            _terrainValid[index] = TryMapSurface(point, flight.Region, out var height);
            if (!_terrainValid[index]) continue;
            _terrainHeights[index] = height; _terrainDatum = Mathf.Min(_terrainDatum, height);
            peak = Mathf.Max(peak, height); _terrainSamples++;
        }
        // Bound relief height without shrinking the horizontal chart into an unreadable thumbnail.
        var relief = _terrainSamples == 0 ? 0 : (peak - _terrainDatum) * .46f / _terrainSpan;
        _mapModes[0].transform.localScale = new Vector3(1, Mathf.Min(1, .23f / (ProjectionScale * (relief + .03f))), 1);
        // Fixed ribbons keep missing samples as gaps without rebuilding line objects on every survey.
        var vertices = _terrainVertices!;
        var edge = 0;
        for (var axis = 0; axis < 2; axis++)
        for (var row = 0; row < TerrainSide; row++)
        for (var column = 0; column < TerrainSide - 1; column++)
        {
            var x = axis == 0 ? column : row;
            var z = axis == 0 ? row : column;
            var nextX = x + (axis == 0 ? 1 : 0);
            var nextZ = z + (axis == 0 ? 0 : 1);
            var v = edge++ * 4;
            if (!_terrainValid[z * TerrainSide + x] || !_terrainValid[nextZ * TerrainSide + nextX])
            {
                vertices[v] = vertices[v + 1] = vertices[v + 2] = vertices[v + 3] = Vector3.zero;
                continue;
            }
            var a = TerrainVertex(x, z);
            var b = TerrainVertex(nextX, nextZ);
            var side = (axis == 0 ? Vector3.forward : Vector3.right) * .0009f;
            vertices[v] = a - side; vertices[v + 1] = a + side;
            vertices[v + 2] = b - side; vertices[v + 3] = b + side;
        }
        _terrainMesh!.vertices = vertices;
        _terrainMesh.RecalculateBounds();
        _nextReadout = 0;
    }

    private Vector3 TerrainVertex(int x, int z) => new((x / (float)(TerrainSide - 1) - .5f) * .46f,
        (_terrainHeights[z * TerrainSide + x] - _terrainDatum) * .46f / _terrainSpan,
        (z / (float)(TerrainSide - 1) - .5f) * .46f);

    private bool TryMapSurface(Vector3 point, SpacecraftRegion region, out float height)
    {
        height = 0;
        if (region is SpacecraftRegion.TestWorld or SpacecraftRegion.Moon)
        {
            if (!_world.TrySurfacePoint(point, out var surface)) return false;
            height = surface.y; return true;
        }
        if (region != SpacecraftRegion.Island) return false;
        var hits = _terrainHits ??= new Il2CppStructArray<RaycastHit>(size: 32);
        var origin = new Vector3(point.x, Mathf.Max(2000, point.y + 256), point.z);
        var count = Physics.RaycastNonAlloc(origin, Vector3.down, hits, origin.y + 256, ~0, QueryTriggerInteraction.Ignore);
        if (count == hits.Length) return false;
        var nearest = float.PositiveInfinity;
        for (var index = 0; index < count; index++)
        {
            var hit = hits[index];
            var collider = hit.collider;
            if (collider == null || !collider || collider.attachedRigidbody != null
                || !float.IsFinite(hit.distance) || !float.IsFinite(hit.point.y) || hit.distance >= nearest) continue;
            var terrain = collider.GetComponent("TerrainCollider");
            if (terrain == null || !terrain || terrain.Pointer != collider.Pointer) continue;
            nearest = hit.distance; height = hit.point.y;
        }
        return float.IsFinite(nearest);
    }

    private void UpdateShipMarker(SpacecraftFlightState flight)
    {
        var offset = flight.Position - _terrainCenter;
        var x = Mathf.RoundToInt((offset.x / _terrainSpan + .5f) * (TerrainSide - 1));
        var z = Mathf.RoundToInt((offset.z / _terrainSpan + .5f) * (TerrainSide - 1));
        var valid = x >= 0 && x < TerrainSide && z >= 0 && z < TerrainSide && _terrainValid[z * TerrainSide + x];
        _shipMarker.gameObject.SetActive(valid);
        if (!valid) return;
        var scale = .46f / _terrainSpan;
        _shipMarker.localPosition = new Vector3(offset.x * scale,
            (_terrainHeights[z * TerrainSide + x] - _terrainDatum) * scale + .018f, offset.z * scale);
        var forward = flight.Rotation * Vector3.forward;
        _shipMarker.localRotation = Quaternion.Euler(0, Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg, 0);
    }

    private Station AddStation(string kind, string title, Vector3 position, Quaternion rotation, float width, float height,
        bool nativeScreen = false)
    {
        var frame = new GameObject(title).transform;
        frame.gameObject.layer = _root!.layer; frame.SetParent(_root.transform, false);
        frame.localPosition = position; frame.localRotation = rotation;
        if (!nativeScreen)
        {
            Box(frame, "Inset frame", new Vector3(0, 0, .012f), new Vector3(width * .001f + .024f, height * .001f + .024f, .032f), _bezel);
            Box(frame, "Instrument face", Vector3.zero, new Vector3(width * .001f, height * .001f, .012f), _surface);
        }
        var canvas = new GameObject("Station legends").AddComponent<RectTransform>();
        canvas.gameObject.layer = frame.gameObject.layer; canvas.SetParent(frame, false);
        canvas.localPosition = Vector3.back * (nativeScreen ? NativeScreenOffset : StationScreenOffset);
        canvas.localScale = Vector3.one * .001f;
        canvas.sizeDelta = new Vector2(width, height);
        canvas.gameObject.AddComponent<Canvas>().renderMode = RenderMode.WorldSpace;
        StationGraphic(canvas, "Display glass", 0, 0, width, height, new Color(.012f, .015f, .017f, 1));
        Label(canvas, "Heading", title, 24, 18, width - 48, 32, 25, Ink);
        var rule = Node(canvas, "Heading rule", 24, 60, width - 48, 2).gameObject.AddComponent<Image>();
        rule.material = _graphics; rule.color = ScreenRule; rule.raycastTarget = false;
        var readout = Label(canvas, "Telemetry", "SYSTEMS SYNC", 24, 76, width - 48,
            kind == "Navigation" ? 126 : 172, kind == "Navigation" ? 25 : 23, Ink);
        readout.enableWordWrapping = true;
        var prompt = Label(canvas, "Interaction", "", 24, height - 42, width - 48, 30, 19, Muted);
        var station = new Station(kind, frame, canvas, width, height, readout, prompt, nativeScreen);
        _stations.Add(station); return station;
    }

    private void AddButton(Station station, string action, string title)
    {
        var bounds = ControlBounds(action);
        AddButton(station, action, title, bounds.x, bounds.y, bounds.width, bounds.height);
    }

    private void AddButton(Station station, string action, string title, float x, float y, float width, float height = 62)
    {
        Transform key;
        var face = StationGraphic(station.Canvas, title + " touch region", x, y, width, height, KeyFace);
        if (station.NativeScreen)
        {
            key = face.transform;
        }
        else
        {
            key = Box(station.Frame, title + " key", new Vector3((x + width / 2 - station.Width / 2) * .001f,
                (station.Height / 2 - y - height / 2) * .001f, -.011f), new Vector3(width * .001f, height * .001f, .016f), _button);
        }
        var bounds = new Rect(x, y, width, height);
        var focus = ButtonFocus(face.transform, new Rect(0, 0, width, height));
        var accent = StationGraphic(face.transform, "Key state", 0, height - 2, width, 2,
            action is "override" or "@arm" ? Amber : ScreenRule);
        var label = Label(face.transform, title, title, 12, 6, width - 24, height - 12,
            action is "@zoom-in" or "@zoom-out" ? 34 : station.Kind == "Navigation" ? 22 : width < 230 ? 17 : 20,
            action == "override" || action == "@arm" ? Amber : Ink);
        label.alignment = TextAlignmentOptions.Center;
        if (station.Kind == "Engineering")
        {
            label.enableAutoSizing = true;
            label.fontSizeMin = 14;
            label.fontSizeMax = label.fontSize;
        }
        station.Buttons.Add(new StationButton(station, action, title, bounds, label, key, focus) { Face = face, Accent = accent });
    }

    private Image ButtonFocus(Transform parent, Rect bounds)
    {
        var image = Node(parent, "Key focus", bounds.x, bounds.y, bounds.width, bounds.height).gameObject.AddComponent<Image>();
        image.material = _graphics; image.raycastTarget = false;
        image.gameObject.SetActive(false);
        return image;
    }

    private TextMeshProUGUI Label(Transform parent, string name, string value, float x, float y, float width, float height, float size, Color color)
    {
        var text = Node(parent, name, x, y, width, height).gameObject.AddComponent<TextMeshProUGUI>();
        _typography.Apply(text, TabletTextRole.Control);
        var source = text.fontSharedMaterial;
        if (source != null)
        {
            var id = source.GetInstanceID();
            if (!_fonts.TryGetValue(id, out var material))
            {
                material = new Material(source) { name = "Station text" };
                var shader = Shader.Find("TextMeshPro/Distance Field");
                if (shader != null) material.shader = shader;
                Depth(material); _materials.Add(material); _fonts.Add(id, material);
            }
            text.fontSharedMaterial = material;
        }
        text.isOverlay = false; text.text = value; text.fontSize = size; text.color = color;
        text.richText = false; text.enableWordWrapping = false; text.raycastTarget = false;
        text.overflowMode = TextOverflowModes.Ellipsis; text.alignment = TextAlignmentOptions.MidlineLeft;
        return text;
    }

    private static RectTransform Node(Transform parent, string name, float x, float y, float width, float height)
    {
        var rect = new GameObject(name).AddComponent<RectTransform>();
        rect.gameObject.layer = parent.gameObject.layer; rect.SetParent(parent, false);
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
        rect.anchoredPosition = new Vector2(x, -y); rect.sizeDelta = new Vector2(width, height); return rect;
    }

    private Material Lit(string name, Color color)
    {
        var shader = Shader.Find("HDRP/Lit");
        if (!shader) throw new InvalidOperationException("The cabin station surface shader is unavailable.");
        var material = new Material(shader) { name = name };
        _materials.Add(material); material.SetColor("_BaseColor", color);
        material.SetFloat("_Metallic", .3f); material.SetFloat("_Smoothness", .4f); return material;
    }

    private Material Emission(string name, Color color)
    {
        var material = ClientDeviceScreenMaterial.CreateDisplayMaterial(name);
        _materials.Add(material); ClientDeviceScreenMaterial.SetDisplay(material, color, null); return material;
    }

    private static void Depth(Material material)
    {
        material.SetInt("unity_GUIZTestMode", (int)CompareFunction.LessEqual);
        if (material.HasProperty("_ZTestMode")) material.SetInt("_ZTestMode", (int)CompareFunction.LessEqual);
        ClientDeviceScreenMaterial.ConfigureOwnedUiMaterial(material);
    }

    private static Transform Box(Transform parent, string name, Vector3 position, Vector3 size, Material material)
    {
        var node = GameObject.CreatePrimitive(PrimitiveType.Cube);
        node.name = name; node.layer = parent.gameObject.layer; node.transform.SetParent(parent, false);
        node.transform.localPosition = position; node.transform.localScale = size;
        var collider = node.GetComponent<Collider>();
        if (collider != null) { collider.enabled = false; UnityEngine.Object.Destroy(collider); }
        var renderer = node.GetComponent<MeshRenderer>(); renderer.sharedMaterial = material;
        ClientDeviceScreenMaterial.ConfigureRenderer(renderer);
        return node.transform;
    }

    private static LineRenderer Line(Transform parent, string name, Vector3[] points, bool loop, Material material, float width = .0026f)
    {
        var node = new GameObject(name) { layer = parent.gameObject.layer };
        node.transform.SetParent(parent, false);
        var line = node.AddComponent<LineRenderer>();
        line.useWorldSpace = false; line.loop = loop; line.sharedMaterial = material;
        line.startWidth = line.endWidth = width; line.positionCount = points.Length;
        for (var index = 0; index < points.Length; index++) line.SetPosition(index, points[index]);
        ClientDeviceScreenMaterial.ConfigureRenderer(line);
        return line;
    }

    private static LineRenderer Ring(Transform parent, string name, float radius, Vector3 a, Vector3 b, Material material, float width = .0026f)
    {
        var points = new Vector3[48];
        for (var index = 0; index < points.Length; index++)
        {
            var angle = index * 2 * Mathf.PI / points.Length;
            points[index] = (a * Mathf.Cos(angle) + b * Mathf.Sin(angle)) * radius;
        }
        return Line(parent, name, points, true, material, width);
    }

    private MeshRenderer Globe(Transform parent, float radius, Material surface)
    {
        // Keep the rear grid occluded without inventing geography or depending on cabin lighting.
        var node = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        node.name = "Schematic globe surface"; node.layer = parent.gameObject.layer;
        node.transform.SetParent(parent, false); node.transform.localScale = Vector3.one * (radius * 1.96f);
        var collider = node.GetComponent<Collider>();
        if (collider != null) { collider.enabled = false; UnityEngine.Object.Destroy(collider); }
        var renderer = node.GetComponent<MeshRenderer>(); renderer.sharedMaterial = surface;
        ClientDeviceScreenMaterial.ConfigureRenderer(renderer);
        var grid = new GameObject("Globe coordinates") { layer = parent.gameObject.layer };
        grid.transform.SetParent(parent, false); grid.transform.localScale = Vector3.one * radius;
        grid.AddComponent<MeshFilter>().sharedMesh = _globeGrid ??= BuildGlobeGrid();
        var lines = grid.AddComponent<MeshRenderer>(); lines.sharedMaterials = _globeGridMaterials;
        ClientDeviceScreenMaterial.ConfigureRenderer(lines);
        return renderer;
    }

    private Mesh BuildGlobeGrid()
    {
        const int segments = 96;
        var vertices = new List<Vector3>(35 * (segments + 1) * 2);
        var triangles = new[] { new List<int>(), new List<int>(), new List<int>() };
        // Spherical ribbons stay on the projection surface; one mesh serves every body and zoom.
        void Band(Vector3 a, Vector3 b, float latitude, float width, int layer)
        {
            var normal = Vector3.Cross(a, b).normalized;
            var first = vertices.Count;
            var lower = latitude - width * .5f;
            var upper = latitude + width * .5f;
            for (var segment = 0; segment <= segments; segment++)
            {
                var angle = segment * (Mathf.PI * 2 / segments);
                var radial = a * Mathf.Cos(angle) + b * Mathf.Sin(angle);
                vertices.Add(radial * Mathf.Cos(lower) + normal * Mathf.Sin(lower));
                vertices.Add(radial * Mathf.Cos(upper) + normal * Mathf.Sin(upper));
                if (segment == segments) continue;
                var index = first + segment * 2;
                var indices = triangles[layer];
                indices.Add(index); indices.Add(index + 2); indices.Add(index + 1);
                indices.Add(index + 1); indices.Add(index + 2); indices.Add(index + 3);
            }
        }
        for (var meridian = 0; meridian < 18; meridian++)
        {
            var angle = meridian * Mathf.PI / 18;
            var major = meridian % 3 == 0;
            Band(new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)), Vector3.up,
                0, major ? .007f : .0035f, major ? 1 : 0);
        }
        for (var latitude = -8; latitude <= 8; latitude++)
        {
            var major = latitude % 2 == 0;
            Band(Vector3.right, Vector3.forward, latitude * Mathf.PI / 18,
                latitude == 0 ? .01f : major ? .007f : .0035f, latitude == 0 ? 2 : major ? 1 : 0);
        }
        var mesh = new Mesh { name = "Spherical navigation grid" };
        _globeGrid = mesh;
        mesh.vertices = vertices.ToArray(); mesh.subMeshCount = triangles.Length;
        for (var layer = 0; layer < triangles.Length; layer++)
            mesh.SetTriangles(triangles[layer].ToArray(), layer);
        mesh.RecalculateNormals(); mesh.RecalculateBounds(); mesh.UploadMeshData(true);
        return mesh;
    }

    private static string Number(float value) => float.IsFinite(value) ? value.ToString("0.0", CultureInfo.InvariantCulture) : "—";
    private static string Percent(float value) => float.IsFinite(value) ? (value * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%" : "—";
    private static string GasPercent(float value) => float.IsFinite(value) ? (value * 100).ToString("0.00", CultureInfo.InvariantCulture) + "%" : "—";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _hovered = null; _armedStation = _commandStation = null;
        CancelControlPress();
        if (_root != null) { _root.SetActive(false); UnityEngine.Object.Destroy(_root); }
        DisposePanelArt();
        _root = null; _terrainHits = null; _terrainVertices = null; _stations.Clear(); _fonts.Clear(); _airlockDiagrams.Clear();
        _orbitStates.Clear(); _orbitMarkers.Clear();
        if (_terrainMesh != null) UnityEngine.Object.Destroy(_terrainMesh);
        _terrainMesh = null;
        if (_globeGrid != null) UnityEngine.Object.Destroy(_globeGrid);
        _globeGrid = null;
        foreach (var material in _materials) if (material != null) UnityEngine.Object.Destroy(material);
        _materials.Clear(); _typography.Dispose();
    }

    private sealed record Station(string Kind, Transform Frame, RectTransform Canvas, float Width, float Height,
        TextMeshProUGUI Readout, TextMeshProUGUI Prompt, bool NativeScreen)
    {
        internal readonly List<StationButton> Buttons = new();
    }

    private sealed record CompartmentReadout(TextMeshProUGUI Pressure, TextMeshProUGUI Gases, TextMeshProUGUI Temperature, Image Fill,
        float Width);
    private sealed record AirlockDiagram(Station Station, CompartmentReadout Cabin, CompartmentReadout Airlock,
        CompartmentReadout Entry, Image CabinDoor, Image Inner, Image Ramp);
    private sealed record StationMeter(TextMeshProUGUI Value, Image Fill, float Width);
    private sealed record DiagnosticRow(TextMeshProUGUI Value, Image Fill, TextMeshProUGUI Detail);

    private sealed record StationButton(Station Station, string Action, string Title, Rect Bounds, TextMeshProUGUI Label, Transform Key, Image Focus)
    {
        internal string CrewRequestKey = "";
        internal bool PanelVisible = true;
        internal bool Selected;
        internal Image? Face, Accent;
        internal PanelContact? Contact;
    }
}
