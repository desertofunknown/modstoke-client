// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using TheForest.Utils;
using UnityEngine;
using UnityEngine.Rendering;

namespace Modstoke.Client;

internal enum ShoreServiceKind { Air, Fuel, Coolant }

internal sealed partial class SpacecraftShorePower : IDisposable
{
    internal static readonly Vector3 InletPoint = new(4.677066f, -1.590742f, 6.511574f);
    internal static readonly Vector3 InletNormal = new Vector3(.976083f, -.000879f, .217394f).normalized;
    internal static readonly Quaternion InletRotation = Quaternion.LookRotation(InletNormal, Vector3.up);
    internal static readonly Vector3 StationHead = BoxStationDock.Position
        + BoxStationDock.Rotation * (InletPoint + InletNormal * 2);
    internal static readonly Vector3 PedestalPosition = new(StationHead.x, 0, StationHead.z);
    internal static readonly Quaternion PedestalRotation = Quaternion.LookRotation(
        Vector3.ProjectOnPlane(-(BoxStationDock.Rotation * InletNormal), Vector3.up), Vector3.up);
    internal static readonly Quaternion ArmRotation = Quaternion.Inverse(PedestalRotation)
        * BoxStationDock.Rotation * InletRotation * Quaternion.Euler(0, 180, 0);
    internal static readonly Vector3 CompressorPosition = new(150.32f, 0, 20.95f);
    internal static readonly Quaternion CompressorRotation = Quaternion.Euler(0, 90, 0);
    internal const float MastHeadHeight = 2.05f;
    internal const float OuterLength = .85f, MiddleLength = .75f, InnerLength = .55f, PlugLength = .33f;
    internal const float MiddleStowed = .15f, MiddleExtended = .70f, InnerStowed = .30f, InnerExtended = .35f;
    internal const float PlugOffset = InnerLength + PlugLength;

    private readonly List<Mesh> _meshes = new();
    private readonly List<Texture2D> _textures = new();
    private Material _surface = null!, _indicator = null!, _runIndicator = null!;
    private Mesh _pedestalMesh = null!, _outerMesh = null!, _middleMesh = null!, _innerMesh = null!;
    private Mesh _plugMesh = null!, _indicatorMesh = null!;
    private Mesh _airPlugMesh = null!, _compressorMesh = null!, _airPipesMesh = null!;
    private Mesh _needleMesh = null!, _runLensMesh = null!;
    private Mesh _controlsMesh = null!, _startMesh = null!, _stopMesh = null!;
    private Transform _inlet = null!, _cap = null!, _pedestal = null!, _middle = null!, _inner = null!;
    private Transform _compressor = null!, _airPipes = null!;
    private Transform _needle = null!;
    private Transform _startButton = null!, _stopButton = null!;
    private readonly Il2CppStructArray<RaycastHit> _controlHits = new(size: 32);
    private SpacecraftShoreAirAudio? _airAudio;
    private BoxStationModel? _station;
    private float _phase, _stroke = -1, _capFraction = -1, _meterLoad = -1;
    private bool _energized, _airRunning;

    internal SpacecraftShorePower(Transform vessel)
    {
        try
        {
            CreateMaterials();
            _pedestalMesh = LoadMesh("pedestal");
            _outerMesh = LoadMesh("outer");
            _middleMesh = LoadMesh("middle");
            _innerMesh = LoadMesh("inner");
            _plugMesh = LoadMesh("plug");
            var inletMesh = LoadMesh("inlet");
            var capMesh = LoadMesh("cap");
            _indicatorMesh = LoadMesh("indicator");
            var airInletMesh = LoadMesh("air-inlet");
            _airPlugMesh = LoadMesh("air-plug");
            _compressorMesh = LoadMesh("compressor");
            _needleMesh = LoadMesh("compressor-needle");
            _runLensMesh = LoadMesh("compressor-run");
            _controlsMesh = LoadMesh("compressor-controls");
            _startMesh = LoadMesh("compressor-start");
            _stopMesh = LoadMesh("compressor-stop");
            _airPipesMesh = LoadMesh("air-pipes");
            _inlet = AddPart("Spacecraft shore inlet", inletMesh, _surface, vessel, InletPoint, InletRotation);
            AddPart("Shore air inlet", airInletMesh, _surface, _inlet, Vector3.zero, Quaternion.identity);
            CreateFuelParts();
            CreateCoolantParts();
            _cap = AddPart("Shore inlet cap", capMesh, _surface, _inlet, new(0, .145f, .10f), Quaternion.identity);
            var indicator = AddPart("Shore inlet indicator", _indicatorMesh, _indicator, _inlet,
                new(.121f, .025f, .041f), Quaternion.identity);
            indicator.localScale = new(.1071429f, 6.333333f, 1.666667f);
            SetCap(0);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal void Update(BoxStationModel? station, SpacecraftFlightState flight, bool telemetryCurrent,
        BoxStationState? stationState, ClientTabletPreferencesSnapshot? preferences, float dt,
        ReadOnlySpan<SpacecraftCrewInteraction> interactions)
    {
        if (!_inlet || station == null || !station.Visible)
        {
            DetachStation();
            return;
        }

        var stationRoot = station.Root.transform;
        var idealPosition = stationRoot.TransformPoint(BoxStationDock.Position + BoxStationDock.Rotation * InletPoint);
        var idealRotation = stationRoot.rotation * BoxStationDock.Rotation * InletRotation;
        var nearBerth = (_inlet.position - idealPosition).sqrMagnitude <= .4f * .4f;
        var phase = flight.Systems.ShoreCoupling01;
        var connected = telemetryCurrent && nearBerth && Quaternion.Angle(_inlet.rotation, idealRotation) <= 4
            && flight.Docked && flight.Region == SpacecraftRegion.Space && flight.Travel == SpacecraftTravel.None
            && flight.Systems.Ready && float.IsFinite(phase) && phase is >= 0 and <= 1;
        var target = connected ? phase : 0;
        if (!ReferenceEquals(_station, station) || !_pedestal)
        {
            DetachStation();
            AttachStation(station);
            // A newly projected berth may already be coupled on the host.
            _phase = target;
        }
        else if (target == 1)
        {
            _phase = 1;
        }
        else if (float.IsFinite(dt) && dt > 0)
        {
            _phase = Mathf.MoveTowards(_phase, target, dt * (2 / ClientSpacecraftSystems.ShoreCouplingSeconds));
        }

        SetStroke(Mathf.Clamp01((_phase - .2f) / .8f));
        // The pedestal stays at its station pose during magnetic breakaway. Keep
        // the cap clear until the plug retracts, unless the hull has moved away.
        SetCap(nearBerth ? Mathf.Clamp01(_phase / .2f) : 0);
        var shoreKw = flight.Systems.Electrical.ShoreKw;
        SetEnergized(connected && phase == 1 && _phase == 1 && float.IsFinite(shoreKw) && shoreKw > 0);
        ushort watts = connected && phase == 1 && _phase == 1
            && flight.Systems.ShoreAirWatts <= SpacecraftShoreAir.MaximumWatts ? flight.Systems.ShoreAirWatts : (ushort)0;
        SetAirService(watts, dt);
        SetFuelService(connected && phase == 1 && _phase == 1
            && flight.Systems.ShoreFuelStatus == ShoreFuelStatus.Filling);
        SetCoolantService(connected && phase == 1 && _phase == 1
            && flight.Systems.ShoreCoolantStatus == ShoreCoolantStatus.Filling);
        var flow = flight.Systems.ShoreAirMolesPerSecond;
        _airAudio?.Update(watts, float.IsFinite(flow) && flow is >= 0 and <= SpacecraftShoreAir.MaximumMolesPerSecond ? flow : 0,
            watts > 0 ? AirSoundGain(station, stationState) : 0, preferences);
        UpdateButtons(telemetryCurrent ? interactions : default);
    }

    private void AttachStation(BoxStationModel station)
    {
        _station = station;
        try
        {
            _pedestal = AddPart("Station shore pedestal", _pedestalMesh, _surface, station.Root.transform,
                PedestalPosition, PedestalRotation);
            var outer = AddPart("Shore outer boom", _outerMesh, _surface, _pedestal,
                new(0, MastHeadHeight, 0), ArmRotation);
            _middle = AddPart("Shore middle boom", _middleMesh, _surface, outer,
                new(0, 0, MiddleStowed), Quaternion.identity);
            _inner = AddPart("Shore inner boom", _innerMesh, _surface, _middle,
                new(0, 0, InnerStowed), Quaternion.identity);
            AddPart("Shore breakaway plug", _plugMesh, _surface, _inner,
                new(0, 0, PlugOffset), Quaternion.Euler(0, 180, 0));
            AddPart("Shore air breakaway plug", _airPlugMesh, _surface, _inner,
                new(0, 0, PlugOffset), Quaternion.Euler(0, 180, 0));
            AddPart("Shore pedestal indicator", _indicatorMesh, _indicator, _pedestal,
                new(0, 1.414f, .185f), Quaternion.identity);
            _compressor = AddPart("Station shore compressor", _compressorMesh, _surface, station.Root.transform,
                CompressorPosition, CompressorRotation);
            _needle = AddPart("Compressor load needle", _needleMesh, _surface, _compressor,
                new(-.112f, .746f, .2032f), Quaternion.Euler(0, 0, -135));
            AddPart("Compressor run indicator", _runLensMesh, _runIndicator, _compressor,
                new(.062f, .706f, .191f), Quaternion.identity);
            AddPart("Compressor button fittings", _controlsMesh, _surface, _compressor, Vector3.zero, Quaternion.identity);
            _startButton = AddPart("Compressor start button", _startMesh, _surface, _compressor,
                ButtonMount(true), Quaternion.identity);
            _stopButton = AddPart("Compressor stop button", _stopMesh, _surface, _compressor,
                ButtonMount(false), Quaternion.identity);
            _airAudio = new SpacecraftShoreAirAudio(_compressor);
            _airPipes = AddPart("Station shore air pipes", _airPipesMesh, _surface, station.Root.transform,
                Vector3.zero, Quaternion.identity);
            // The long berth run is exported at 1:10 to fit the mesh coordinate limit.
            _airPipes.localScale = new(10, 10, 10);
            AttachFuelService(station.Root.transform);
            AttachCoolantService(station.Root.transform);
        }
        catch
        {
            DetachStation();
            throw;
        }
    }

    private void SetStroke(float stroke)
    {
        if (_stroke == stroke) return;
        _stroke = stroke;
        _middle.localPosition = new(0, 0, Mathf.Lerp(MiddleStowed, MiddleExtended, stroke));
        _inner.localPosition = new(0, 0, Mathf.Lerp(InnerStowed, InnerExtended, stroke));
        _station!.SetShorePowerStroke(stroke);
    }

    private void SetCap(float fraction)
    {
        if (_capFraction == fraction || !_cap) return;
        _capFraction = fraction;
        _cap.localRotation = Quaternion.Euler(-110 * fraction, 0, 0);
        _coolantCap.localRotation = Quaternion.Euler(110 * fraction, 0, 0);
    }

    private void SetEnergized(bool energized)
    {
        if (_energized == energized || !_indicator) return;
        _energized = energized;
        _indicator.SetColor("_BaseColor", energized ? new Color(.035f, .45f, .1f) : new Color(.012f, .018f, .014f));
        _indicator.SetColor("_EmissiveColor", energized ? new Color(.08f, 1, .2f) * 3 : Color.black);
    }

    private void SetAirService(ushort watts, float dt)
    {
        float load = (float)watts / SpacecraftShoreAir.MaximumWatts;
        if (load > 0 && _meterLoad >= 0 && float.IsFinite(dt) && dt > 0)
            load = Mathf.MoveTowards(_meterLoad, load, dt * 2);
        if (_needle && load != _meterLoad)
            _needle.localRotation = Quaternion.Euler(0, 0, -135 + load * 270);
        _meterLoad = load;
        bool running = watts > 0;
        if (_airRunning == running || !_runIndicator) return;
        _airRunning = running;
        _runIndicator.SetColor("_BaseColor", running ? new Color(.08f, .55f, .24f) : new Color(.012f, .028f, .018f));
        _runIndicator.SetColor("_EmissiveColor", running ? new Color(.10f, .8f, .3f) * 2 : Color.black);
    }

    private static float AirSoundGain(BoxStationModel station, BoxStationState? current)
    {
        var camera = TheForest.Utils.LocalPlayer.MainCam;
        if (!camera || current is not { } state || state.HullRevision != station.HullState.Revision) return 0;
        int cell = station.AtmosphereCellAt(camera.transform.position);
        if (!BoxStationSystems.TryGetAtmosphere(station.Layout, state, cell, out var listener)
            || !BoxStationSystems.TryGetAtmosphere(station.Layout, state, SpacecraftShoreAir.SupplyCell, out var source)) return 0;
        // This is airborne cabinet sound; a pressurized helmet does not make the surrounding vacuum carry it.
        float pressure = Mathf.Min(listener.Pressure, source.Pressure);
        return float.IsFinite(pressure) ? Mathf.Clamp01(pressure / 101.325f) : 0;
    }

    private static Vector3 ButtonMount(bool start, ShoreServiceKind service = ShoreServiceKind.Air)
        => service == ShoreServiceKind.Air ? new(start ? .137f : .183f, .706f, .198f) : new(start ? -.10f : .10f, .72f, .205f);
    private static Vector3 ButtonFace(bool start, ShoreServiceKind service = ShoreServiceKind.Air)
        => ButtonMount(start, service) + Vector3.forward * (service == ShoreServiceKind.Air ? .0043f : .004f);

    internal static Vector3 ControlPoint(Transform station, bool start, ShoreServiceKind service = ShoreServiceKind.Air)
        => station.TransformPoint(service switch
        {
            ShoreServiceKind.Air => CompressorPosition + CompressorRotation * ButtonFace(start),
            ShoreServiceKind.Fuel => FuelCabinetPosition + FuelCabinetRotation * ButtonFace(start, service),
            ShoreServiceKind.Coolant => CoolantCabinetPosition + CoolantCabinetRotation * ButtonFace(start, service),
            _ => throw new ArgumentOutOfRangeException(nameof(service))
        });

    internal static SpacecraftControlContact ControlContact(Transform station, Transform ship, bool start,
        ShoreServiceKind service = ShoreServiceKind.Air)
        => ClientSpacecraftStations.KeyControlContact(ship.InverseTransformPoint(ControlPoint(station, start, service)),
            Quaternion.Inverse(ship.rotation) * station.rotation
            * (service switch
            {
                ShoreServiceKind.Air => CompressorRotation,
                ShoreServiceKind.Fuel => FuelCabinetRotation,
                ShoreServiceKind.Coolant => CoolantCabinetRotation,
                _ => throw new ArgumentOutOfRangeException(nameof(service))
            }) * Quaternion.Euler(0, 180, 0));

    internal bool FindControl(Camera camera, out bool start, out ShoreServiceKind service)
    {
        service = ShoreServiceKind.Air;
        var ray = new Ray(camera.transform.position, camera.transform.forward);
        if (FindControl(_compressor, ray, service, out start)) return true;
        if (FindControl(_fuelCabinet, ray, ShoreServiceKind.Fuel, out start))
        {
            service = ShoreServiceKind.Fuel;
            return true;
        }
        if (FindControl(_coolantCabinet, ray, ShoreServiceKind.Coolant, out start))
        {
            service = ShoreServiceKind.Coolant;
            return true;
        }
        return false;
    }

    private bool FindControl(Transform cabinet, Ray ray, ShoreServiceKind service, out bool start)
    {
        start = false;
        if (!cabinet || !cabinet.gameObject.activeInHierarchy) return false;
        if (Vector3.Dot(ray.direction, cabinet.forward) >= -.1f) return false;
        var plane = new Plane(cabinet.forward, cabinet.TransformPoint(ButtonFace(true, service)));
        if (!plane.Raycast(ray, out float distance) || distance < 0 || distance > 2.4f) return false;
        var point = cabinet.InverseTransformPoint(ray.GetPoint(distance));
        var startFace = ButtonFace(true, service);
        var stopFace = ButtonFace(false, service);
        const float radius = .016f;
        if (Vector2.SqrMagnitude(new(point.x - startFace.x, point.y - startFace.y)) <= radius * radius) start = true;
        else if (Vector2.SqrMagnitude(new(point.x - stopFace.x, point.y - stopFace.y)) > radius * radius) return false;
        // Stop before the fitted cabinet rim without accepting obstructions along the approach.
        int count = Physics.RaycastNonAlloc(ray, _controlHits, Mathf.Max(0, distance - .01f), ~0, QueryTriggerInteraction.Ignore);
        if (count < 0 || count >= _controlHits.Length) return false;
        var player = LocalPlayer.Transform;
        for (int i = 0; i < count; i++)
        {
            var collider = _controlHits[i].collider;
            if (collider && (!player || !collider.transform.IsChildOf(player))) return false;
        }
        return true;
    }

    private void UpdateButtons(ReadOnlySpan<SpacecraftCrewInteraction> interactions)
    {
        UpdateButtons(_compressor, _startButton, _stopButton, ShoreServiceKind.Air, interactions);
        UpdateButtons(_fuelCabinet, _fuelStartButton, _fuelStopButton, ShoreServiceKind.Fuel, interactions);
        UpdateButtons(_coolantCabinet, _coolantStartButton, _coolantStopButton, ShoreServiceKind.Coolant, interactions);
    }

    private void UpdateButtons(Transform cabinet, Transform startButton, Transform stopButton, ShoreServiceKind service,
        ReadOnlySpan<SpacecraftCrewInteraction> interactions)
    {
        float start = 0, stop = 0;
        foreach (var interaction in interactions)
        {
            if (interaction.Actor == 0 || !interaction.Active || !interaction.Press) continue;
            var point = cabinet.InverseTransformPoint(_inlet.parent.TransformPoint(interaction.LocalTarget));
            if ((point - ButtonFace(true, service)).sqrMagnitude <= .008f * .008f) start = Mathf.Max(start, interaction.Reach);
            else if ((point - ButtonFace(false, service)).sqrMagnitude <= .008f * .008f) stop = Mathf.Max(stop, interaction.Reach);
        }
        startButton.localPosition = ButtonMount(true, service) - Vector3.forward * (.002f * start);
        stopButton.localPosition = ButtonMount(false, service) - Vector3.forward * (.002f * stop);
    }

    internal void DetachStation()
    {
        DetachFuelService();
        DetachCoolantService();
        _airAudio?.Dispose();
        _airAudio = null;
        _station?.SetShorePowerStroke(0);
        _station = null;
        if (_pedestal)
        {
            _pedestal.gameObject.SetActive(false);
            UnityEngine.Object.Destroy(_pedestal.gameObject);
        }
        if (_compressor)
        {
            _compressor.gameObject.SetActive(false);
            UnityEngine.Object.Destroy(_compressor.gameObject);
        }
        if (_airPipes)
        {
            _airPipes.gameObject.SetActive(false);
            UnityEngine.Object.Destroy(_airPipes.gameObject);
        }
        _pedestal = _middle = _inner = null!;
        _compressor = _airPipes = null!;
        _needle = null!;
        _startButton = _stopButton = null!;
        _phase = 0;
        _stroke = -1;
        _meterLoad = -1;
        SetCap(0);
        SetEnergized(false);
        SetAirService(0, 0);
    }

    private void CreateMaterials()
    {
        var shader = Shader.Find("HDRP/Lit");
        if (!shader) throw new InvalidOperationException("Shore service requires the game's HDRP/Lit shader.");
        _surface = new Material(shader) { name = "Shore service surfaces", enableInstancing = true };
        _surface.SetColor("_BaseColor", Color.white);
        _surface.SetTexture("_BaseColorMap", LoadTexture("base", false));
        _surface.SetTexture("_NormalMap", LoadTexture("normal", true));
        _surface.SetTexture("_MaskMap", LoadTexture("mask", true));
        _surface.SetFloat("_NormalScale", 1);
        _surface.SetFloat("_NormalMapSpace", 0);
        _surface.SetFloat("_MetallicRemapMin", 0);
        _surface.SetFloat("_MetallicRemapMax", 1);
        _surface.SetFloat("_SmoothnessRemapMin", 0);
        _surface.SetFloat("_SmoothnessRemapMax", 1);
        _surface.SetFloat("_AORemapMin", 0);
        _surface.SetFloat("_AORemapMax", 1);
        _surface.EnableKeyword("_NORMALMAP_TANGENT_SPACE");
        _surface.EnableKeyword("_NORMALMAP");
        _surface.EnableKeyword("_MASKMAP");
        _indicator = new Material(shader) { name = "Shore power indicators", enableInstancing = true };
        _indicator.SetColor("_BaseColor", new Color(.012f, .018f, .014f));
        _indicator.SetColor("_EmissiveColor", Color.black);
        _indicator.SetFloat("_Metallic", 0);
        _indicator.SetFloat("_Smoothness", .55f);
        _runIndicator = new Material(shader) { name = "Shore compressor run indicator", enableInstancing = true };
        _runIndicator.SetColor("_BaseColor", new Color(.012f, .028f, .018f));
        _runIndicator.SetColor("_EmissiveColor", Color.black);
        _runIndicator.SetFloat("_Metallic", 0);
        _runIndicator.SetFloat("_Smoothness", .55f);
        CreateFuelMaterial(shader);
        CreateCoolantMaterial(shader);
    }

    private Mesh LoadMesh(string asset)
    {
        using var stream = Resource(asset + ".msa");
        var data = SpacecraftMeshData.Read(stream, "shore " + asset);
        var mesh = new Mesh();
        _meshes.Add(mesh);
        mesh.name = "Shore " + asset;
        mesh.indexFormat = data.Vertices.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
        mesh.vertices = data.Vertices;
        mesh.normals = data.Normals;
        mesh.uv = data.Uv;
        mesh.SetTriangles(data.Triangles, 0);
        mesh.RecalculateBounds();
        mesh.RecalculateTangents();
        mesh.UploadMeshData(true);
        return mesh;
    }

    private Texture2D LoadTexture(string asset, bool linear)
    {
        using var stream = Resource("atlas-" + asset + ".png");
        if (!stream.CanSeek || stream.Length is < 24 or > 64 * 1024 * 1024)
            throw new InvalidDataException("The shore texture size is invalid: " + asset);
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        if (BinaryPrimitives.ReadUInt64BigEndian(bytes) != 0x89504e470d0a1a0aUL
            || BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(12)) != 0x49484452
            || BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16)) != 4096
            || BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20)) != 2048)
            throw new InvalidDataException("The shore texture dimensions are invalid: " + asset);
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear);
        _textures.Add(texture);
        texture.name = "Shore " + asset;
        if (!ImageConversion.LoadImage(texture, bytes, false) || texture.width != 4096 || texture.height != 2048)
            throw new InvalidDataException("The shore texture could not be decoded: " + asset);
        if (asset == "normal")
        {
            // Source PNGs use RGB +Y normals; HDRP decodes X from alpha and Y from green.
            var pixels = texture.GetPixels32().ToArray();
            for (var i = 0; i < pixels.Length; i++)
                pixels[i] = new Color32(255, pixels[i].g, 0, pixels[i].r);
            texture.SetPixels32(pixels);
        }
        texture.wrapMode = TextureWrapMode.Clamp;
        texture.filterMode = FilterMode.Trilinear;
        texture.anisoLevel = 8;
        texture.Apply(true, false);
        texture.Compress(true);
        texture.Apply(false, true);
        return texture;
    }

    private static Stream Resource(string asset) => typeof(SpacecraftShorePower).Assembly.GetManifestResourceStream(
        "Modstoke.Client.Assets.spacecraft-shore-" + asset)
        ?? throw new InvalidDataException("The shore service asset is missing: " + asset);

    private static Transform AddPart(string name, Mesh mesh, Material material, Transform parent,
        Vector3 position, Quaternion rotation)
    {
        var part = new GameObject(name);
        try
        {
            part.layer = parent.gameObject.layer;
            part.transform.SetParent(parent, false);
            part.transform.localPosition = position;
            part.transform.localRotation = rotation;
            part.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = part.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.On;
            renderer.receiveShadows = true;
            return part.transform;
        }
        catch
        {
            UnityEngine.Object.Destroy(part);
            throw;
        }
    }

    public void Dispose()
    {
        DetachStation();
        if (_inlet) UnityEngine.Object.Destroy(_inlet.gameObject);
        _inlet = _cap = _coolantCap = null!;
        if (_surface) UnityEngine.Object.Destroy(_surface);
        if (_indicator) UnityEngine.Object.Destroy(_indicator);
        if (_runIndicator) UnityEngine.Object.Destroy(_runIndicator);
        if (_fuelRunIndicator) UnityEngine.Object.Destroy(_fuelRunIndicator);
        if (_coolantRunIndicator) UnityEngine.Object.Destroy(_coolantRunIndicator);
        _surface = _indicator = _runIndicator = null!;
        _fuelRunIndicator = _coolantRunIndicator = null!;
        foreach (var mesh in _meshes) if (mesh) UnityEngine.Object.Destroy(mesh);
        _meshes.Clear();
        foreach (var texture in _textures) if (texture) UnityEngine.Object.Destroy(texture);
        _textures.Clear();
    }
}
