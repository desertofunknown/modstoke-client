// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftStations
{
    // Ship-local metres; sizes and key rectangles are millimetres. SpaceshipScan
    // slot 3 (SpaceshipScanUnique) contains these four faces in one static mesh.
    private static readonly PanelSurface CardSurface = new(new(1.433544f, .071431f, -4.622960f), Vector3.forward, 126, 30);
    private static readonly PanelSurface RetinaSurface = new(new(1.508002f, .210885f, -4.648260f), Vector3.forward, 142, 50);
    private static readonly PanelSurface ReaderStatusSurface = new(new(1.582465f, .055199f, -4.651027f), Vector3.forward, 126, 104);
    private static readonly PanelSurface AccessSurface = new(new(1.508005f, -.210971f, -4.683558f), new(0, .195184f, .980767f), 210, 270);
    // Forward plate clears the static pillar's outer face at z=-4.706006.
    private static readonly PanelSurface ForwardDoorSurface = new(new(.992848f, .30f, -4.703006f), Vector3.forward, 200, 126);
    // The aft keys face the corridor from the static side wall, clear of the sliding leaf.
    private static readonly PanelSurface AftDoorSurface = new(new(SpacecraftLayout.CabinEntryControl.min.x,
        SpacecraftLayout.CabinEntryControl.center.y, SpacecraftLayout.CabinEntryControl.center.z), Vector3.left, 200, 180);
    // SpaceshipControllers/SpaceshipControllersB, slot 1 glass, 101.235 mm dials.
    // The native numbered legends do not identify contents. These are configured
    // reservoirs; owned faces cover static pointers without moving native geometry.
    private static readonly PanelSurface Tank1Surface = new(new(2.445857f, -.762686f, 3.130269f), Vector3.left, 96, 96);
    private static readonly PanelSurface Tank2Surface = new(new(2.445856f, -.762686f, 2.939661f), Vector3.left, 96, 96);
    private static readonly PanelSurface Tank3Surface = new(new(2.445856f, -.762686f, 2.749052f), Vector3.left, 96, 96);
    // Tank legends retain their original slot 0 atlas areas.
    private static readonly PanelSurface Tank1Legend = new(new(2.449883f, -.833033f, 3.129583f), Vector3.left, 69, 25);
    private static readonly PanelSurface Tank2Legend = new(new(2.449882f, -.833100f, 2.938446f), Vector3.left, 70, 22);
    private static readonly PanelSurface Tank3Legend = new(new(2.449882f, -.832982f, 2.747309f), Vector3.left, 72, 25);
    private static readonly PanelSurface ApuIsolationLegend = new(SpacecraftLayout.ApuControlPosition + new Vector3(.042f, .083f, 0), Vector3.left, 82, 22);
    private static readonly PanelSurface ContentsSurface = new(new(2.446368f, -.942770f, 3.062605f), Vector3.left, 124, 124);
    private static readonly PanelSurface ApuSurface = new(SpacecraftLayout.ApuControlPosition, Vector3.left, 44, 96);
    private static readonly PanelSurface ApuStatusSurface = new(SpacecraftLayout.ApuControlPosition + new Vector3(.042f, -.076f, 0), Vector3.left, 130, 32);
    private static readonly PanelSurface ValveFeedbackSurface = new(new(2.451104f, -.337f, 2.294966f), Vector3.left, 480, 26);
    // SpaceshipControllersDMesh socket centres, in SpacecraftValve order. The
    // third lower socket is offset from its row; do not regularize the layout.
    // Front planes clear every baked lever by 2.5 mm (minimum x=2.421671).
    private static readonly PanelSurface[] ValveSurfaces =
    {
        new(new(2.419171f, -.026336f, 2.509570f), Vector3.left, 60, 64),
        new(new(2.419171f, -.026336f, 2.423266f), Vector3.left, 60, 64),
        new(new(2.419171f, -.026336f, 2.336963f), Vector3.left, 60, 64),
        new(new(2.419171f, -.026336f, 2.250659f), Vector3.left, 60, 64),
        new(new(2.419171f, -.026336f, 2.164356f), Vector3.left, 60, 64),
        new(new(2.419171f, -.026336f, 2.078052f), Vector3.left, 60, 64),
        new(new(2.419171f, -.199034f, 2.509570f), Vector3.left, 60, 64),
        new(new(2.419171f, -.199034f, 2.423266f), Vector3.left, 60, 64),
        new(new(2.419171f, -.201586f, 2.337646f), Vector3.left, 60, 64),
        new(new(2.419171f, -.199034f, 2.250659f), Vector3.left, 60, 64),
        new(new(2.419171f, -.199034f, 2.164356f), Vector3.left, 60, 64),
        new(new(2.419171f, -.199034f, 2.078052f), Vector3.left, 60, 64)
    };
    private static readonly string[] ValveTitles =
    {
        "CABIN\nSUPPLY", "LOCK+ENTRY\nSUPPLY", "CABIN\nSCRUBBER", "LOCK+ENTRY\nSCRUBBER", "CABIN\nCOOLING", "LOCK+ENTRY\nCOOLING",
        "ENGINE\nFUEL", "APU\nFUEL", "WARP\nFUEL", "RADIATOR", "COOLANT\nFEED", "CHAMBER\nVENT"
    };
    // SpaceshipControllersB: five 47.968 x 215.876 mm columns, not the
    // similarly named upper panel. The shallow overlay covers static bar marks.
    private static readonly PanelSurface[] PressureSurfaces =
    {
        new(new(2.443710f, -.489697f, 3.195603f), Vector3.left, 48, 216),
        new(new(2.443710f, -.489697f, 3.067529f), Vector3.left, 48, 216),
        new(new(2.443710f, -.489697f, 2.939455f), Vector3.left, 48, 216),
        new(new(2.443710f, -.489697f, 2.811381f), Vector3.left, 48, 216),
        new(new(2.443710f, -.489697f, 2.683307f), Vector3.left, 48, 216)
    };
    // CockpitMeshes/SpaceshipControllersDMesh, slot 0 SpaceshipControllers:
    // two 72.829 x 36.437 mm windows above the native degree-C markings.
    private static readonly PanelSurface CabinCoolingSurface = new(new(2.439899f, -.908415f, 2.405634f), Vector3.left, 68, 32);
    private static readonly PanelSurface AirlockCoolingSurface = new(new(2.439898f, -.908415f, 2.184297f), Vector3.left, 68, 32);
    private static readonly PanelSurface CoolingLegend = new(new(2.442156f, -.917563f, 2.290231f), Vector3.left, 94, 53);
    private static readonly PanelSurface CabinCelsiusLegend = new(new(2.437086f, -.945394f, 2.404133f), Vector3.left, 26, 19);
    private static readonly PanelSurface AirlockCelsiusLegend = new(new(2.437086f, -.945394f, 2.182796f), Vector3.left, 26, 19);

    private static readonly Rect CardKeyBounds = new(0, 0, 126, 30);
    private static readonly Rect RetinaKeyBounds = new(0, 0, 142, 32);
    private static readonly Rect CrewLockBounds = new(10, 187, 190, 26);
    private static readonly Rect ApproveCrewBounds = new(10, 218, 92, 24);
    private static readonly Rect DenyCrewBounds = new(108, 218, 92, 24);
    private static readonly Rect ForwardDoorBounds = new(10, 36, 180, 26);
    private static readonly Rect ForwardCycleBounds = new(10, 68, 86, 24);
    private static readonly Rect ForwardCancelBounds = new(104, 68, 86, 24);
    private static readonly Rect AftRequestBounds = new(10, 50, 180, 32);
    private static readonly Rect AftDoorBounds = new(10, 90, 180, 32);
    private static readonly Rect ValveKeyBounds = new(0, 0, 60, 64);
    private static readonly Rect ApuKeyBounds = new(0, 0, 44, 96);
    private static readonly Vector3 LeverPivot = new(0, 0, -.004f);
    private static readonly Bounds LeverGrip = new(new Vector3(0, 0, -.021f), new Vector3(.013f, .024f, .013f));
    private static readonly Bounds LeverCrown = new(new Vector3(0, 0, -.028f), new Vector3(.008f, .017f, .0015f));
    private static readonly Vector3 ApuGuardPivot = new(-.020f, 0, -.003f);
    private static readonly Bounds ApuGuardCover = new(new Vector3(.020f, 0, -.035f), new Vector3(.036f, .088f, .006f));
    private static readonly Bounds ApuGuardLip = new(new Vector3(.032f, -.032f, -.040f), new Vector3(.012f, .012f, .007f));
    private static readonly Bounds ApuGuardLeftEdge = new(new Vector3(.002f, 0, -.018f), new Vector3(.004f, .088f, .034f));
    private static readonly Bounds ApuGuardRightEdge = new(new Vector3(.038f, 0, -.018f), new Vector3(.004f, .088f, .034f));
    private static readonly Bounds[] ApuGuardSurfaces = { ApuGuardCover, ApuGuardLip, ApuGuardLeftEdge, ApuGuardRightEdge };

    private Station _cardPanel = null!, _retinaPanel = null!, _readerStatusPanel = null!, _accessPanel = null!;
    private Station _forwardDoorPanel = null!, _aftDoorPanel = null!;
    private Station _cabinCoolingPanel = null!, _airlockCoolingPanel = null!;
    private StationButton _crewLockKey = null!, _approveCrewKey = null!, _denyCrewKey = null!;
    private StationButton _forwardDoorKey = null!, _aftDoorKey = null!;
    private StationButton _forwardCycleKey = null!, _forwardCancelKey = null!;
    private PanelGauge _fuelGauge = null!, _airMixGauge = null!, _coolantGauge = null!;
    private ContentsGauge _contentsGauge = null!;
    private readonly PanelValve[] _valves = new PanelValve[12];
    private readonly PressureBar[] _pressureBars = new PressureBar[5];
    private Station _apuPanel = null!, _apuStatusPanel = null!, _valveFeedbackPanel = null!;
    private StationButton _apuGuardOpenKey = null!, _apuGuardCloseKey = null!, _apuIsolateKey = null!, _apuResetKey = null!;
    private Transform _apuGuard = null!, _apuLever = null!;
    private static readonly Color PanelDark = new(.018f, .020f, .022f, 1);
    private static readonly Color PanelCyan = new(.30f, .78f, .83f, 1);
    private static readonly Color PanelOff = new(.045f, .050f, .052f, 1);
    private Material _panelMetal = null!, _panelPaint = null!, _panelRubber = null!, _panelMarks = null!;
    private Material? _panelFaceMaterial;
    private Texture2D? _panelFaceTexture;
    private readonly ClientDeviceSurfaceFinish _panelFinish = new();
    private readonly Dictionary<Vector3, Mesh> _panelHardwareMeshes = new();
    private readonly Dictionary<(PrimitiveType Kind, Vector3 Size), Mesh> _panelPrimitiveMeshes = new();
    private readonly Dictionary<string, Mesh> _panelModelMeshes = new(StringComparer.Ordinal);
    private Mesh? _panelDialRimMesh, _panelContentsRimMesh, _valveFaceMesh;
    private Mesh? _panelDialScaleMesh, _panelContentsScaleMesh, _panelNeedleMesh;
    private Image _readerLamp = null!, _apuLamp = null!;
    private TextMeshProUGUI _readerDoor = null!, _readerLock = null!, _accessDetail = null!;
    private Image _readerStatusLine = null!, _accessStatusLine = null!;
    private Image _accessDoorLeft = null!, _accessDoorRight = null!;
    private TextMeshProUGUI _accessDoorState = null!;
    private Image _accessInnerGate = null!, _accessRampGate = null!;
    private TextMeshProUGUI _accessCabinPressure = null!, _accessAirlockPressure = null!, _accessEntryPressure = null!;

    private static bool IsCabinPanel(Station station) => station.Kind.StartsWith("Panel:", StringComparison.Ordinal);

    private void BuildPanelArt()
    {
        _panelMetal = Lit("Instrument satin metal", new Color(.32f, .35f, .37f, 1));
        _panelMetal.SetFloat("_Metallic", .8f); _panelMetal.SetFloat("_Smoothness", .46f);
        _panelPaint = Lit("Instrument label enamel", new Color(.025f, .030f, .033f, 1));
        _panelPaint.SetFloat("_Metallic", .15f); _panelPaint.SetFloat("_Smoothness", .42f);
        _panelRubber = Lit("Instrument socket rubber", PanelDark);
        _panelRubber.SetFloat("_Metallic", 0); _panelRubber.SetFloat("_Smoothness", .22f);
        _panelFinish.Apply(_panelMetal, ClientDeviceSurfaceFinish.Surface.Metal);
        _panelFinish.Apply(_panelPaint, ClientDeviceSurfaceFinish.Surface.Polymer);
        _panelFinish.Apply(_panelRubber, ClientDeviceSurfaceFinish.Surface.Rubber);
        _panelMarks = Emission("Instrument scale markings", new Color(.8f, .82f, .83f, 1));

        const string name = "spacecraft-instrument-face.png";
        using var stream = typeof(ClientSpacecraftStations).Assembly.GetManifestResourceStream("Modstoke.Client.Assets." + name)
            ?? throw new InvalidOperationException("The spacecraft instrument face is missing: " + name);
        if (!stream.CanSeek || stream.Length is < 33 or > 8 * 1024 * 1024)
            throw new InvalidDataException("The spacecraft instrument face size is invalid.");
        using var bytes = new MemoryStream((int)stream.Length);
        stream.CopyTo(bytes);
        var data = bytes.ToArray();
        // Check the PNG dimensions before Unity allocates the decoded surface.
        var width = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(20, 4));
        if (BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(0, 8)) != 0x89504e470d0a1a0aUL
            || BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(12, 4)) != 0x49484452
            || width is < 64 or > 2048 || height != width)
            throw new InvalidDataException("The spacecraft instrument face must be a square PNG up to 2048 pixels.");
        _panelFaceTexture = new Texture2D(2, 2, TextureFormat.RGBA32, true, false) { name = name };
        if (!ImageConversion.LoadImage(_panelFaceTexture, data, false)
            || _panelFaceTexture.width != width || _panelFaceTexture.height != height)
            throw new InvalidDataException("The spacecraft instrument face could not be decoded.");
        _panelFaceTexture.wrapMode = TextureWrapMode.Clamp;
        _panelFaceTexture.filterMode = FilterMode.Trilinear; _panelFaceTexture.anisoLevel = 4;
        _panelFaceTexture.Apply(true, true);
        _panelFaceMaterial = new Material(_graphics) { name = "Instrument face graphics" };
        Depth(_panelFaceMaterial);
    }

    private Transform PanelHardware(Transform parent, string name, Vector3 position, Vector3 size, Material material)
    {
        if (!_panelHardwareMeshes.TryGetValue(size, out var mesh))
        {
            // Share by dimensions so metre-based grain stays consistent on plates and grips.
            var outline = new Vector2[] { new(-.36f, -.5f), new(.36f, -.5f), new(.5f, -.36f), new(.5f, .36f),
                new(.36f, .5f), new(-.36f, .5f), new(-.5f, .36f), new(-.5f, -.36f) };
            var vertices = new List<Vector3>(144);
            var triangles = new List<int>(180);
            Vector3 Point(int index, int ring)
            {
                var p = outline[index % 8] * (ring is 0 or 3 ? .92f : 1);
                return new Vector3(p.x, p.y, ring switch { 0 => -.5f, 1 => -.3f, 2 => .3f, _ => .5f });
            }
            void Face(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
            {
                var start = vertices.Count;
                vertices.Add(a); vertices.Add(b); vertices.Add(c); vertices.Add(d);
                triangles.Add(start); triangles.Add(start + 1); triangles.Add(start + 2);
                triangles.Add(start); triangles.Add(start + 2); triangles.Add(start + 3);
            }
            for (var ring = 0; ring < 3; ring++)
                for (var edge = 0; edge < 8; edge++)
                    Face(Point(edge, ring), Point(edge + 1, ring), Point(edge + 1, ring + 1), Point(edge, ring + 1));
            for (var edge = 1; edge < 7; edge++)
            {
                var start = vertices.Count;
                vertices.Add(Point(0, 0)); vertices.Add(Point(edge + 1, 0)); vertices.Add(Point(edge, 0));
                triangles.Add(start); triangles.Add(start + 1); triangles.Add(start + 2);
                start = vertices.Count;
                vertices.Add(Point(0, 3)); vertices.Add(Point(edge, 3)); vertices.Add(Point(edge + 1, 3));
                triangles.Add(start); triangles.Add(start + 1); triangles.Add(start + 2);
            }
            mesh = new Mesh { name = "Instrument bevelled hardware" };
            _panelHardwareMeshes.Add(size, mesh);
            mesh.vertices = vertices.ToArray(); mesh.triangles = triangles.ToArray();
            mesh.RecalculateNormals();
            SetPanelSurfaceUv(mesh, size);
            mesh.RecalculateBounds(); mesh.UploadMeshData(true);
        }
        var node = new GameObject(name) { layer = parent.gameObject.layer };
        node.transform.SetParent(parent, false); node.transform.localPosition = position; node.transform.localScale = size;
        node.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = node.AddComponent<MeshRenderer>(); renderer.sharedMaterial = material;
        ClientDeviceScreenMaterial.ConfigureRenderer(renderer);
        return node.transform;
    }

    private static void SetPanelSurfaceUv(Mesh mesh, Vector3 size, bool cylinder = false)
    {
        var vertices = mesh.vertices;
        var normals = mesh.normals;
        var sourceUv = cylinder ? mesh.uv : null;
        var uv = new Vector2[vertices.Length];
        for (var index = 0; index < vertices.Length; index++)
        {
            var p = Vector3.Scale(vertices[index], size);
            var normal = normals[index];
            var n = new Vector3(normal.x / size.x, normal.y / size.y, normal.z / size.z).normalized;
            // Cylinder sides retain the primitive's split wrap seam; caps stay planar.
            uv[index] = cylinder && Mathf.Abs(n.y) < .5f
                ? new Vector2(sourceUv![index].x * Mathf.PI * size.x, p.y)
                : Mathf.Abs(n.z) > .5f ? new Vector2(p.x, p.y)
                : Mathf.Abs(n.x) > Mathf.Abs(n.y) ? new Vector2(p.z, p.y)
                : new Vector2(p.x, p.z);
        }
        mesh.uv = uv;
        mesh.RecalculateTangents();
    }

    private void FinishPanelPrimitive(Transform node, PrimitiveType kind, Vector3 size)
    {
        var filter = node.GetComponent<MeshFilter>();
        if (!_panelPrimitiveMeshes.TryGetValue((kind, size), out var mesh))
        {
            // Copy only the render mesh; never mutate Unity's shared primitive or its collider.
            var source = filter.sharedMesh;
            mesh = new Mesh { name = "Instrument " + kind + " finish" };
            _panelPrimitiveMeshes.Add((kind, size), mesh);
            mesh.vertices = source.vertices; mesh.normals = source.normals;
            mesh.triangles = source.triangles; mesh.uv = source.uv;
            SetPanelSurfaceUv(mesh, size, kind == PrimitiveType.Cylinder);
            mesh.RecalculateBounds(); mesh.UploadMeshData(true);
        }
        filter.sharedMesh = mesh;
    }

    private Transform PanelBox(Transform parent, string name, Vector3 position, Vector3 size, Material material)
    {
        var node = Box(parent, name, position, size, material);
        FinishPanelPrimitive(node, PrimitiveType.Cube, size);
        return node;
    }

    private Transform PanelCylinder(Transform parent, string name, Vector3 position, Vector3 scale, Material material)
    {
        var node = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        node.name = name; node.layer = parent.gameObject.layer; node.transform.SetParent(parent, false);
        node.transform.localPosition = position; node.transform.localScale = scale;
        var collider = node.GetComponent<Collider>();
        if (collider != null) { collider.enabled = false; UnityEngine.Object.Destroy(collider); }
        var renderer = node.GetComponent<MeshRenderer>(); renderer.sharedMaterial = material;
        ClientDeviceScreenMaterial.ConfigureRenderer(renderer);
        FinishPanelPrimitive(node.transform, PrimitiveType.Cylinder, scale);
        return node.transform;
    }

    private void DisposePanelArt()
    {
        // Lit/Emission materials belong to the station owner's material list.
        if (_panelFaceMaterial != null) UnityEngine.Object.Destroy(_panelFaceMaterial);
        if (_panelFaceTexture != null) UnityEngine.Object.Destroy(_panelFaceTexture);
        foreach (var mesh in _panelHardwareMeshes.Values) UnityEngine.Object.Destroy(mesh);
        foreach (var mesh in _panelPrimitiveMeshes.Values) UnityEngine.Object.Destroy(mesh);
        foreach (var mesh in _panelModelMeshes.Values) UnityEngine.Object.Destroy(mesh);
        _panelHardwareMeshes.Clear(); _panelPrimitiveMeshes.Clear();
        _panelModelMeshes.Clear();
        if (_panelDialRimMesh != null) UnityEngine.Object.Destroy(_panelDialRimMesh);
        if (_panelContentsRimMesh != null) UnityEngine.Object.Destroy(_panelContentsRimMesh);
        if (_valveFaceMesh != null) UnityEngine.Object.Destroy(_valveFaceMesh);
        if (_panelDialScaleMesh != null) UnityEngine.Object.Destroy(_panelDialScaleMesh);
        if (_panelContentsScaleMesh != null) UnityEngine.Object.Destroy(_panelContentsScaleMesh);
        if (_panelNeedleMesh != null) UnityEngine.Object.Destroy(_panelNeedleMesh);
        _panelFinish.Dispose();
        _panelFaceMaterial = null; _panelFaceTexture = null;
        _panelDialRimMesh = null; _panelContentsRimMesh = null;
        _valveFaceMesh = null;
        _panelDialScaleMesh = null; _panelContentsScaleMesh = null; _panelNeedleMesh = null;
    }

    internal static bool TryPanelControlContact(string command, Vector3 actorLocal, SpacecraftSystemState systems, out SpacecraftControlContact contact)
    {
        if (IsSystemServiceCommand(command))
        {
            contact = SystemServiceContact(command, systems.BusIsolated);
            return true;
        }
        if (IsWarpServiceCommand(command))
        {
            contact = WarpServiceContact(command, systems.WarpDischargeEnabled);
            return true;
        }
        if (ClientSpacecraftSystems.TryReadSealantCommand(command, out var compartment))
        {
            contact = SealantContact(compartment);
            return true;
        }
        if (command == "service-engine")
        {
            contact = EngineServiceContact();
            return true;
        }
        if (ClientSpacecraftSystems.TryReadHullRepairCommand(command, out var hullSection))
        {
            contact = HullServiceContact(hullSection);
            return true;
        }
        if (command == "load-spares")
        {
            contact = SparesContact();
            return true;
        }
        if (command == "line-10-repair")
        {
            contact = CoolantRepairContact();
            return true;
        }
        if (ClientSpacecraftSystems.TryReadValveCommand(command, out var valve, out _)
            || ClientSpacecraftSystems.TryReadLineRepairCommand(command, out valve))
        {
            contact = PanelLeverContact(ValveSurfaces[valve], PanelValveOpen(systems, (SpacecraftValve)valve));
            return true;
        }
        // A nearer target across closed A02 is not reachable. Resolve compartment
        // before distance, including the aft request key when the reader is inside.
        var forward = actorLocal.z > -4.84f;
        switch (command)
        {
            case "card-reader":
                contact = forward ? CardSurface.ControlContact(CardKeyBounds) : AftDoorSurface.ControlContact(AftRequestBounds);
                return true;
            case "cabin-door":
                contact = forward ? ForwardDoorSurface.ControlContact(ForwardDoorBounds) : AftDoorSurface.ControlContact(AftDoorBounds);
                return true;
            case "airlock-to-cabin" when forward:
                contact = ForwardDoorSurface.ControlContact(ForwardCycleBounds);
                return true;
            case "airlock-cancel" when forward:
                contact = ForwardDoorSurface.ControlContact(ForwardCancelBounds);
                return true;
            case "retina-scanner": contact = RetinaSurface.ControlContact(RetinaKeyBounds); return true;
            case "crew-lock": contact = AccessSurface.ControlContact(CrewLockBounds); return true;
            case "approve-crew": contact = AccessSurface.ControlContact(ApproveCrewBounds); return true;
            case "deny-crew": contact = AccessSurface.ControlContact(DenyCrewBounds); return true;
            case "apu-guard-open":
            case "apu-guard-close": contact = ApuGuardContact(actorLocal, systems.ApuGuardAmount); return true;
            case "apu-isolate":
            case "apu-reset": contact = PanelLeverContact(ApuSurface, !systems.ApuIsolated); return true;
            default: contact = default; return false;
        }
    }

    // The small toggle crown is a fingertip contact; an open palm would
    // push the native thumb through the surrounding panel.
    private static SpacecraftControlContact PanelLeverContact(PanelSurface surface, bool open)
        => KeyControlContact(PanelLeverPoint(surface, open), surface.Rotation);

    private static Quaternion PanelLeverRotation(bool open) => Quaternion.Euler(open ? 30 : -30, 0, 0);

    private static float PanelKeyPress(bool ready, float pressedUntil, SpacecraftControlContact contact,
        ReadOnlySpan<SpacecraftCrewInteraction> interactions)
    {
        if (!ready) return 0;
        float press = Time.unscaledTime < pressedUntil ? 1 : 0;
        foreach (var interaction in interactions)
        {
            if (interaction.Actor == 0 || !interaction.Active || !interaction.Press
                || interaction.LocalRotation is not { } rotation) continue;
            if ((interaction.LocalTarget - contact.Position).sqrMagnitude <= .000025f
                && Quaternion.Angle(rotation, contact.Rotation) < 1) press = Mathf.Max(press, interaction.Reach);
        }
        return press;
    }

    private static Vector3 PanelLeverPoint(PanelSurface surface, bool open) => surface.Position + surface.Rotation
        * (LeverPivot + PanelLeverRotation(open) * (LeverCrown.center + Vector3.back * LeverCrown.extents.z));

    private static Quaternion ApuGuardRotation(float amount) => Quaternion.Euler(0, 110 * Mathf.Clamp01(amount), 0);

    internal static SpacecraftControlContact MoveApuGuardContact(SpacecraftControlContact contact, float fromAmount, float toAmount)
    {
        var pivot = ApuSurface.Position + ApuSurface.Rotation * ApuGuardPivot;
        var from = ApuSurface.Rotation * ApuGuardRotation(fromAmount);
        var to = ApuSurface.Rotation * ApuGuardRotation(toAmount);
        var delta = to * Quaternion.Inverse(from);
        return contact with { Position = pivot + delta * (contact.Position - pivot), Rotation = delta * contact.Rotation };
    }

    internal static bool IsApuGuardContact(Vector3 position, float amount)
    {
        var pivot = ApuSurface.Position + ApuSurface.Rotation * ApuGuardPivot;
        var rotation = ApuSurface.Rotation * ApuGuardRotation(amount);
        var local = Quaternion.Inverse(rotation) * (position - pivot);
        // The guard angle is quantized in snapshots; retain a small surface tolerance.
        foreach (var surface in ApuGuardSurfaces)
            if (surface.SqrDistance(local) <= .003f * .003f) return true;
        return false;
    }

    private static SpacecraftControlContact ApuGuardContact(Vector3 actorLocal, float amount)
    {
        var rotation = ApuSurface.Rotation * ApuGuardRotation(amount);
        var pivot = ApuSurface.Position + ApuSurface.Rotation * ApuGuardPivot;
        // Match the host's control sight line from the actor root.
        var origin = Quaternion.Inverse(rotation) * (actorLocal + Vector3.up * 1.3f - pivot);
        var ray = new Ray(origin, (ApuGuardCover.center - origin).normalized);
        // Side flanges can hide the cover face. Reach the first physical surface toward the actor.
        var point = RaycastApuGuard(ray, out var distance, out var normal) ? ray.GetPoint(distance)
            : ApuGuardCover.center + Vector3.back * ApuGuardCover.extents.z;
        var up = Mathf.Abs(normal.y) > .9f ? Vector3.back : Vector3.up;
        return KeyControlContact(pivot + rotation * point, rotation * Quaternion.LookRotation(-normal, up));
    }

    private static bool RaycastApuGuard(Ray ray, out float distance, out Vector3 normal)
    {
        distance = float.MaxValue;
        normal = Vector3.back;
        var surface = default(Bounds);
        foreach (var bounds in ApuGuardSurfaces)
            if (bounds.IntersectRay(ray, out var hit) && hit >= 0 && hit < distance) { distance = hit; surface = bounds; }
        if (distance == float.MaxValue) return false;
        var offset = ray.GetPoint(distance) - surface.center;
        var edges = surface.extents;
        var x = Mathf.Abs(Mathf.Abs(offset.x) - edges.x);
        var y = Mathf.Abs(Mathf.Abs(offset.y) - edges.y);
        var z = Mathf.Abs(Mathf.Abs(offset.z) - edges.z);
        normal = x <= y && x <= z ? new Vector3(Mathf.Sign(offset.x), 0, 0)
            : y <= z ? new Vector3(0, Mathf.Sign(offset.y), 0) : new Vector3(0, 0, Mathf.Sign(offset.z));
        return true;
    }

    private bool ApuGuardRaycast(Ray ray, out float distance)
    {
        var localRay = new Ray(_apuGuard.InverseTransformPoint(ray.origin), _apuGuard.InverseTransformDirection(ray.direction));
        if (!RaycastApuGuard(localRay, out var localDistance, out _)) { distance = 0; return false; }
        distance = Vector3.Distance(ray.origin, _apuGuard.TransformPoint(localRay.GetPoint(localDistance)));
        return true;
    }

    private void BuildCabinPanels()
    {
        BuildPanelArt();
        _cardPanel = AddPanel("Card reader", CardSurface, default);
        var slot = AddPanelButton(_cardPanel, "card-reader", "REQUEST ACCESS", CardKeyBounds, 7, paint: false);
        slot.Label.gameObject.SetActive(false);
        // The actual slot and inserted native bar stay visible; this is session
        // identity presentation, not proof that an inventory card was consumed.
        _readerLamp = PanelLamp(_cardPanel, "Reader indicator", new Rect(16, 26, 94, 1.5f));

        _retinaPanel = AddPanel("Identity scanner", RetinaSurface, default);
        AddPanelButton(_retinaPanel, "retina-scanner", "IDENTITY", RetinaKeyBounds, 7, paint: false);
        PanelRule(_retinaPanel, new Rect(12, 7, 12, .7f), PanelCyan);
        PanelRule(_retinaPanel, new Rect(12, 7, .7f, 17), PanelCyan);
        PanelRule(_retinaPanel, new Rect(118, 7, 12, .7f), PanelCyan);
        PanelRule(_retinaPanel, new Rect(129.3f, 7, .7f, 17), PanelCyan);
        _readerStatusPanel = AddPanel("Reader status", ReaderStatusSurface, new Rect(12, 34, 102, 34), 12);
        PanelBacking(_readerStatusPanel, new Rect(0, 0, 126, 104));
        _readerDoor = Label(_readerStatusPanel.Canvas, "Cabin door state", "A02 / SYNC", 12, 10, 102, 14, 8, Muted);
        PanelRule(_readerStatusPanel, new Rect(12, 28, 102, .7f), ScreenRule);
        _readerStatusLine = PanelLamp(_readerStatusPanel, "Access state", new Rect(5, 34, 1.2f, 34));
        PanelRule(_readerStatusPanel, new Rect(12, 75, 102, .7f), ScreenRule);
        _readerLock = Label(_readerStatusPanel.Canvas, "Crew lock state", "", 12, 81, 102, 14, 8, Muted);
        _accessPanel = AddPanel("Session access", AccessSurface, new Rect(12, 36, 186, 22), 14);
        PanelBacking(_accessPanel, new Rect(0, 0, 210, 270));
        Label(_accessPanel.Canvas, "Access heading", "A02 / SESSION ACCESS", 12, 12, 186, 14, 8, PanelCyan);
        _accessStatusLine = PanelLamp(_accessPanel, "Access state", new Rect(12, 30, 186, .7f));
        BuildAccessDoorDiagram();
        _accessDetail = Label(_accessPanel.Canvas, "Access request", "", 12, 160, 186, 22, 8, Ink);
        _accessDetail.enableWordWrapping = true; _accessDetail.alignment = TextAlignmentOptions.TopLeft;
        _accessDetail.enableAutoSizing = true; _accessDetail.fontSizeMin = 6; _accessDetail.fontSizeMax = 8;
        PanelRule(_accessPanel, new Rect(12, 155, 186, .7f), ScreenRule);
        _crewLockKey = AddPanelButton(_accessPanel, "crew-lock", "CREW LOCK", CrewLockBounds, 10);
        _approveCrewKey = AddPanelButton(_accessPanel, "approve-crew", "APPROVE", ApproveCrewBounds, 9);
        _denyCrewKey = AddPanelButton(_accessPanel, "deny-crew", "DENY", DenyCrewBounds, 9);
        SetPanelPrompt(_accessPanel, new Rect(10, 245, 190, 22), 7);
        _accessPanel.Prompt.enableAutoSizing = true;
        _accessPanel.Prompt.fontSizeMin = 5; _accessPanel.Prompt.fontSizeMax = 7;
        SetPanelKeyVisible(_crewLockKey, false);
        SetPanelKeyVisible(_approveCrewKey, false);
        SetPanelKeyVisible(_denyCrewKey, false);

        _forwardDoorPanel = AddPanel("Cabin exit", ForwardDoorSurface, new Rect(32, 8, 156, 20), 8, ownedPlate: true);
        AddJambDetails(_forwardDoorPanel);
        PanelRule(_forwardDoorPanel, new Rect(32, 31, 156, .8f), PanelCyan);
        _forwardDoorKey = AddPanelButton(_forwardDoorPanel, "cabin-door", "CABIN DOOR", ForwardDoorBounds, 10);
        _forwardCycleKey = AddPanelButton(_forwardDoorPanel, "airlock-to-cabin", "TO CABIN", ForwardCycleBounds, 8);
        _forwardCancelKey = AddPanelButton(_forwardDoorPanel, "airlock-cancel", "CANCEL", ForwardCancelBounds, 8);
        SetPanelPrompt(_forwardDoorPanel, new Rect(32, 98, 156, 24), 8);
        _aftDoorPanel = AddPanel("Cabin entry", AftDoorSurface, new Rect(32, 10, 156, 32), 10);
        AddPanelModelPart(_aftDoorPanel.Frame, "door-control-shell", _panelPaint);
        AddPanelModelPart(_aftDoorPanel.Frame, "door-control-trim", _panelMetal);
        AddPanelModelPart(_aftDoorPanel.Frame, "door-control-gasket", _panelRubber);
        AddPanelModelPart(_aftDoorPanel.Frame, "door-control-hardware", _panelMetal);
        PanelRule(_aftDoorPanel, new Rect(10, 44, 180, .8f), PanelCyan);
        AddPanelButton(_aftDoorPanel, "card-reader", "REQUEST ENTRY", AftRequestBounds, 10);
        _aftDoorKey = AddPanelButton(_aftDoorPanel, "cabin-door", "CABIN DOOR", AftDoorBounds, 10);
        SetPanelPrompt(_aftDoorPanel, new Rect(32, 132, 156, 32), 8);

        _fuelGauge = AddPanelGauge("FUEL", Tank1Surface);
        _airMixGauge = AddPanelGauge("AIR MIX", Tank2Surface);
        _coolantGauge = AddPanelGauge("COOLANT", Tank3Surface);
        _contentsGauge = AddContentsGauge();
        AddPanelLegend("TANK 1", Tank1Legend, 10);
        AddPanelLegend("TANK 2", Tank2Legend, 10);
        AddPanelLegend("TANK 3", Tank3Legend, 10);
        AddPanelLegend("APU ISOLATION", ApuIsolationLegend, 8);
        AddPanelLegend("COOLING\nSYSTEM\nTARGET 22 \u00b0C", CoolingLegend, 7);
        AddPanelLegend("\u00b0C", CabinCelsiusLegend, 10);
        AddPanelLegend("\u00b0C", AirlockCelsiusLegend, 10);
        _cabinCoolingPanel = AddCoolingDisplay("CABIN", CabinCoolingSurface);
        _airlockCoolingPanel = AddCoolingDisplay("CHAMBER", AirlockCoolingSurface);
        _pressureBars[0] = AddPressureBar("CABIN", PressureSurfaces[0], 120);
        _pressureBars[1] = AddPressureBar("CHAMBER", PressureSurfaces[1], 120);
        _pressureBars[2] = AddPressureBar("AIR FEED", PressureSurfaces[2], 300);
        _pressureBars[3] = AddPressureBar("FUEL FEED", PressureSurfaces[3], 300);
        _pressureBars[4] = AddPressureBar("COOLANT", PressureSurfaces[4], 300);
        var handleMaterial = Lit("Engineering lever grips", new Color(.35f, .018f, .009f, 1));
        handleMaterial.SetFloat("_Metallic", 0); handleMaterial.SetFloat("_Smoothness", .34f);
        _panelFinish.Apply(handleMaterial, ClientDeviceSurfaceFinish.Surface.Rubber);
        BuildValveFace();
        for (var index = 0; index < _valves.Length; index++) _valves[index] = AddValve(index, handleMaterial);
        _valveFeedbackPanel = AddPanel("Valve feedback", ValveFeedbackSurface, default);
        SetPanelPrompt(_valveFeedbackPanel, new Rect(4, 2, 472, 22), 8);
        BuildCoolantRepairPanel();
        BuildSparesPanel();
        BuildEngineServicePanel();
        BuildHullService();
        BuildWarpServicePanel();
        BuildSealantPanels();
        BuildSystemServicePanel();
        BuildApuControl(handleMaterial);
    }

    private void BuildAccessDoorDiagram()
    {
        var canvas = _accessPanel.Canvas;
        var outline = new Color(.30f, .47f, .50f, 1);
        PlanLine("Hull", outline, new[] { new Vector2(49, 66), new Vector2(59, 66),
            new Vector2(68, 82), new Vector2(71, 112), new Vector2(80, 125), new Vector2(80, 144),
            new Vector2(68, 148), new Vector2(40, 148), new Vector2(28, 144), new Vector2(28, 125),
            new Vector2(37, 112), new Vector2(40, 82), new Vector2(49, 66) });
        PlanLine("Flight deck", outline, new[] { new Vector2(49, 73), new Vector2(59, 73),
            new Vector2(63, 84), new Vector2(45, 84), new Vector2(49, 73) });
        StationGraphic(canvas, "Cabin", 42, 88, 24, 21, KeyFace);
        StationGraphic(canvas, "Pressure chamber", 42, 114, 24, 16, KeyFace);
        StationGraphic(canvas, "Entry vestibule", 42, 134, 24, 10, KeyFace);
        PlanLine("Compartment wall", outline, new[] { new Vector2(42, 88), new Vector2(42, 144) });
        PlanLine("Compartment wall", outline, new[] { new Vector2(66, 88), new Vector2(66, 144) });
        for (var side = 0; side < 2; side++)
        {
            var x = side == 0 ? 31 : 70;
            PlanLine("Engine pod", outline, new[] { new Vector2(x, 124), new Vector2(x + 7, 120),
                new Vector2(x + 7, 140), new Vector2(x, 140), new Vector2(x, 124) });
        }
        PlanLine("Cabin leader", ScreenRule, new[] { new Vector2(66, 93), new Vector2(86, 79), new Vector2(98, 79) });
        PlanLine("Door leader", PanelCyan, new[] { new Vector2(66, 111), new Vector2(86, 97), new Vector2(98, 97) });
        PlanLine("Chamber leader", ScreenRule, new[] { new Vector2(66, 122), new Vector2(86, 120), new Vector2(98, 120) });
        PlanLine("Entry leader", ScreenRule, new[] { new Vector2(66, 139), new Vector2(86, 144), new Vector2(98, 144) });
        _accessDoorLeft = StationGraphic(canvas, "Door leaf left", 42, 110, 11, 2, PanelOff);
        _accessDoorRight = StationGraphic(canvas, "Door leaf right", 55, 110, 11, 2, PanelOff);
        _accessInnerGate = StationGraphic(canvas, "A01 entry door", 42, 131, 24, 2, PanelOff);
        _accessRampGate = StationGraphic(canvas, "Aft ramp", 42, 145, 24, 2, PanelOff);
        Label(canvas, "Entry door label", "A01", 12, 127, 23, 8, 5.5f, Muted);
        Label(canvas, "Ramp label", "RAMP", 10, 141, 25, 8, 5.5f, Muted);
        Label(canvas, "Cabin pressure heading", "CABIN", 106, 66, 92, 9, 7, Muted);
        _accessCabinPressure = Label(canvas, "Cabin pressure", "-- kPa", 106, 76, 92, 12, 9, Ink);
        Label(canvas, "Cabin door heading", "A02", 106, 91, 28, 12, 7, PanelCyan);
        _accessDoorState = Label(canvas, "Door travel", "SYNC", 136, 91, 62, 12, 8, Muted);
        Label(canvas, "Airlock pressure heading", "CHAMBER", 106, 107, 92, 9, 7, Muted);
        _accessAirlockPressure = Label(canvas, "Airlock pressure", "-- kPa", 106, 117, 92, 12, 9, Ink);
        Label(canvas, "Entry pressure heading", "ENTRY", 106, 131, 92, 9, 7, Muted);
        _accessEntryPressure = Label(canvas, "Entry pressure", "-- kPa", 106, 141, 92, 12, 9, Ink);

        void PlanLine(string name, Color color, Vector2[] points)
        {
            for (var i = 1; i < points.Length; i++)
            {
                var delta = points[i] - points[i - 1];
                var line = StationGraphic(canvas, name, points[i - 1].x, points[i - 1].y, delta.magnitude, .7f, color);
                line.rectTransform.pivot = new Vector2(0, .5f);
                line.rectTransform.localRotation = Quaternion.Euler(0, 0, -Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
            }
        }
    }

    private void AddJambDetails(Station station)
    {
        PanelRule(station, new Rect(12, 12, 12, .9f), PanelCyan);
        PanelRule(station, new Rect(12, 12, .9f, 23), PanelCyan);
        PanelRule(station, new Rect(23.1f, 12, .9f, 23), PanelCyan);
        PanelRule(station, new Rect(17.5f, 14, .7f, 19), Muted);
        PanelRule(station, new Rect(7, station.Height - 7, 18, .7f), PanelCyan);
        PanelRule(station, new Rect(station.Width - 25, station.Height - 7, 18, .7f), PanelCyan);
    }

    private Station AddPanel(string name, PanelSurface surface, Rect readoutBounds, float fontSize = 9, bool ownedPlate = false)
    {
        var frame = new GameObject(name).transform;
        frame.gameObject.layer = _root!.layer; frame.SetParent(_root.transform, false);
        frame.localPosition = surface.Position; frame.localRotation = surface.Rotation;
        if (ownedPlate)
        {
            PanelHardware(frame, "Jamb control plate", new Vector3(0, 0, .004f),
                new Vector3(surface.Width * .001f + .008f, surface.Height * .001f + .008f, .008f), _bezel);
            PanelHardware(frame, "Jamb control face", new Vector3(0, 0, -.0005f),
                new Vector3(surface.Width * .001f, surface.Height * .001f, .001f), _surface);
        }
        var canvas = new GameObject("Panel legends").AddComponent<RectTransform>();
        canvas.gameObject.layer = frame.gameObject.layer; canvas.SetParent(frame, false);
        canvas.localPosition = Vector3.back * NativeScreenOffset;
        canvas.localScale = Vector3.one * .001f; canvas.sizeDelta = new Vector2(surface.Width, surface.Height);
        canvas.gameObject.AddComponent<Canvas>().renderMode = RenderMode.WorldSpace;
        var readout = Label(canvas, "Panel telemetry", "SYNC", readoutBounds.x, readoutBounds.y,
            readoutBounds.width, readoutBounds.height, fontSize, Ink);
        readout.enableWordWrapping = true; readout.alignment = TextAlignmentOptions.TopLeft;
        readout.gameObject.SetActive(readoutBounds.width > 0);
        var prompt = Label(canvas, "Panel feedback", "", 0, 0, 1, 1, fontSize, Muted);
        prompt.gameObject.SetActive(false);
        var station = new Station("Panel:" + name, frame, canvas, surface.Width, surface.Height, readout, prompt, true);
        _stations.Add(station);
        return station;
    }

    private StationButton AddPanelButton(Station station, string command, string title, Rect bounds, float fontSize, bool paint = true)
    {
        var key = Node(station.Canvas, title + " target", bounds.x, bounds.y, bounds.width, bounds.height);
        if (paint)
        {
            var image = key.gameObject.AddComponent<Image>();
            image.material = _graphics; image.color = new Color(.08f, .09f, .095f, 1); image.raycastTarget = false;
            var edge = Node(key, "Key edge", 0, 0, bounds.width, .7f).gameObject.AddComponent<Image>();
            edge.material = _graphics; edge.color = Muted; edge.raycastTarget = false;
        }
        var focus = ButtonFocus(key, new Rect(0, 0, bounds.width, bounds.height));
        var label = Label(key, title, title, 3, 2, bounds.width - 6, bounds.height - 4, fontSize, Ink);
        label.alignment = TextAlignmentOptions.Center;
        var button = new StationButton(station, command, title, bounds, label, key, focus);
        station.Buttons.Add(button);
        return button;
    }

    private static void SetPanelPrompt(Station station, Rect bounds, float fontSize)
    {
        var prompt = station.Prompt;
        prompt.rectTransform.anchoredPosition = new Vector2(bounds.x, -bounds.y);
        prompt.rectTransform.sizeDelta = bounds.size; prompt.fontSize = fontSize;
        prompt.enableWordWrapping = true; prompt.alignment = TextAlignmentOptions.TopLeft;
        prompt.gameObject.SetActive(true);
    }

    private static void SetPanelKeyVisible(StationButton key, bool visible)
    {
        if (key.PanelVisible == visible) return;
        key.Key.gameObject.SetActive(visible);
        key.PanelVisible = visible;
    }

    private void AddPanelLegend(string text, PanelSurface surface, float fontSize, float topInset = 1)
    {
        var station = AddPanel("Native legend", surface, new Rect(1, topInset, surface.Width - 2, surface.Height - topInset - 1), fontSize);
        PanelHardware(station.Frame, "Engraved legend plate", Vector3.back * .001f,
            new Vector3(surface.Width * .001f, surface.Height * .001f, .002f), _panelPaint);
        station.Readout.text = text; station.Readout.alignment = TextAlignmentOptions.Center;
        station.Readout.fontStyle = FontStyles.Bold;
    }

    private void PanelBacking(Station station, Rect bounds)
    {
        var position = new Vector3((bounds.center.x - station.Width / 2) * .001f,
            (station.Height / 2 - bounds.center.y) * .001f, -.001f);
        PanelHardware(station.Frame, "Instrument inset rim", position,
            new Vector3((bounds.width + 1.5f) * .001f, (bounds.height + 1.5f) * .001f, .0015f), _panelMetal);
        var patch = Node(station.Canvas, "Instrument recess", bounds.x, bounds.y, bounds.width, bounds.height).gameObject.AddComponent<Image>();
        patch.material = _graphics; patch.color = PanelDark; patch.raycastTarget = false;
        patch.transform.SetAsFirstSibling();
    }

    private void PanelRule(Station station, Rect bounds, Color color) => PanelLamp(station, "Panel rule", bounds).color = color;

    private void BuildValveFace()
    {
        const float centerZ = 2.293811f, halfWidth = .043152f;
        const float top = .052f, bottom = -.312f, bevel = .0012f;
        var x0 = centerZ - ValveSurfaces[0].Position.z - halfWidth;
        var x1 = centerZ - ValveSurfaces[5].Position.z + halfWidth;
        var vertices = new List<Vector3>(1024);
        var triangles = new List<int>(1536);
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            var start = vertices.Count;
            vertices.Add(a); vertices.Add(b); vertices.Add(c); vertices.Add(d);
            triangles.Add(start); triangles.Add(start + 1); triangles.Add(start + 2);
            triangles.Add(start); triangles.Add(start + 2); triangles.Add(start + 3);
        }
        void Face(float left, float low, float right, float high) =>
            Quad(new(left, low, 0), new(left, high, 0), new(right, high, 0), new(right, low, 0));
        void Triangle(Vector3 a, Vector3 b, Vector3 c)
        {
            var start = vertices.Count;
            vertices.Add(a); vertices.Add(b); vertices.Add(c);
            triangles.Add(start); triangles.Add(start + 1); triangles.Add(start + 2);
        }

        // One flush face replaces the separate legend plates. Socket inserts
        // cover baked graphics without moving the original guard rails.
        for (var column = 0; column < 6; column++)
        {
            var upper = ValveSurfaces[column].Position;
            var lower = ValveSurfaces[column + 6].Position;
            var left = column == 0 ? x0 + bevel
                : centerZ - (ValveSurfaces[column - 1].Position.z + upper.z) * .5f;
            var right = column == 5 ? x1 - bevel
                : centerZ - (upper.z + ValveSurfaces[column + 1].Position.z) * .5f;
            Face(left, upper.y + .033f, right, top - bevel);
            Face(left, lower.y + .033f, right, upper.y - .033f);
            Face(left, bottom + bevel, right, lower.y - .033f);
            foreach (var socket in new[] { upper, lower })
            {
                var x = centerZ - socket.z;
                Face(left, socket.y - .033f, x - .025f, socket.y + .033f);
                Face(x + .025f, socket.y - .033f, right, socket.y + .033f);
            }
        }
        var faceIndexCount = triangles.Count;
        foreach (var surface in ValveSurfaces)
        {
            var x = centerZ - surface.Position.z;
            Face(x - .025f, surface.Position.y - .033f, x + .025f, surface.Position.y + .033f);
        }
        var screws = new Vector2[]
        {
            new(x0 + .0032f, top - .004f), new(x1 - .0032f, top - .004f),
            new(x0 + .0032f, bottom + .004f), new(x1 - .0032f, bottom + .004f)
        };
        // Dark slots sit inside the metal head, not on a second overlapping disc.
        foreach (var screw in screws)
            Quad(new(screw.x - .00115f, screw.y - .00018f, -.00016f), new(screw.x - .00115f, screw.y + .00018f, -.00016f),
                new(screw.x + .00115f, screw.y + .00018f, -.00016f), new(screw.x + .00115f, screw.y - .00018f, -.00016f));
        var socketIndexCount = triangles.Count - faceIndexCount;
        var outline = new Vector2[] { new(x0, bottom), new(x0, top), new(x1, top), new(x1, bottom) };
        var inset = new Vector2[]
        {
            new(x0 + bevel, bottom + bevel), new(x0 + bevel, top - bevel),
            new(x1 - bevel, top - bevel), new(x1 - bevel, bottom + bevel)
        };
        for (var edge = 0; edge < 4; edge++)
        {
            var next = (edge + 1) % 4;
            Quad(new(inset[edge].x, inset[edge].y, 0), new(outline[edge].x, outline[edge].y, bevel),
                new(outline[next].x, outline[next].y, bevel), new(inset[next].x, inset[next].y, 0));
        }
        foreach (var screw in screws)
        {
            const int segments = 24;
            Vector3 Ring(float angle, float radius, float depth) =>
                new(screw.x + Mathf.Cos(angle) * radius, screw.y + Mathf.Sin(angle) * radius, depth);
            for (var segment = 0; segment < segments; segment++)
            {
                var a = segment * (Mathf.PI * 2 / segments);
                var b = (segment + 1) * (Mathf.PI * 2 / segments);
                Quad(Ring(a, .0015f, -.00005f), Ring(a, .00115f, -.0003f),
                    Ring(b, .00115f, -.0003f), Ring(b, .0015f, -.00005f));
            }
            // Split the head at the driver slot so it remains recessed at grazing angles.
            var trim = Mathf.Asin(.00018f / .00115f);
            for (var half = 0; half < 2; half++)
            {
                var center = new Vector3(screw.x, screw.y + (half == 0 ? .00018f : -.00018f), -.0003f);
                for (var segment = 0; segment < segments / 2; segment++)
                {
                    var a = half * Mathf.PI + trim + segment * (Mathf.PI - 2 * trim) / (segments / 2);
                    var b = half * Mathf.PI + trim + (segment + 1) * (Mathf.PI - 2 * trim) / (segments / 2);
                    Triangle(center, Ring(b, .00115f, -.0003f), Ring(a, .00115f, -.0003f));
                }
            }
        }
        var hardwareIndexCount = triangles.Count - faceIndexCount - socketIndexCount;
        _valveFaceMesh = new Mesh { name = "Valve panel face" };
        _valveFaceMesh.vertices = vertices.ToArray(); _valveFaceMesh.subMeshCount = 3;
        _valveFaceMesh.SetTriangles(triangles.GetRange(0, faceIndexCount).ToArray(), 0);
        _valveFaceMesh.SetTriangles(triangles.GetRange(faceIndexCount, socketIndexCount).ToArray(), 1);
        _valveFaceMesh.SetTriangles(triangles.GetRange(faceIndexCount + socketIndexCount, hardwareIndexCount).ToArray(), 2);
        _valveFaceMesh.RecalculateNormals();
        SetPanelSurfaceUv(_valveFaceMesh, Vector3.one);
        _valveFaceMesh.RecalculateBounds(); _valveFaceMesh.UploadMeshData(true);
        var enamel = Lit("Valve panel satin finish", new Color(.24f, .26f, .27f, 1));
        enamel.SetFloat("_Metallic", .45f); enamel.SetFloat("_Smoothness", .44f);
        _panelFinish.Apply(enamel, ClientDeviceSurfaceFinish.Surface.Metal);
        var node = new GameObject("Valve panel face") { layer = _root!.layer };
        node.transform.SetParent(_root.transform, false);
        node.transform.localPosition = new Vector3(2.4448f, 0, centerZ);
        node.transform.localRotation = ValveSurfaces[0].Rotation;
        node.AddComponent<MeshFilter>().sharedMesh = _valveFaceMesh;
        var renderer = node.AddComponent<MeshRenderer>(); renderer.sharedMaterials = new[] { enamel, _panelRubber, _panelMetal };
        ClientDeviceScreenMaterial.ConfigureRenderer(renderer);
    }

    private void AddValveLegend(string text, PanelSurface surface, float fontSize, float topInset = 1)
    {
        var station = AddPanel("Valve legend", surface, new Rect(1, topInset, surface.Width - 2, surface.Height - topInset - 1), fontSize);
        station.Readout.text = text; station.Readout.color = PanelDark;
        station.Readout.alignment = TextAlignmentOptions.Center; station.Readout.fontStyle = FontStyles.Bold;
    }

    private PanelValve AddValve(int index, Material handleMaterial)
    {
        var surface = ValveSurfaces[index];
        var station = AddPanel(ValveTitles[index] + " valve", surface, default);
        // The rear block masks the entire baked handle, including its sides;
        // the native guard rails remain outside this socket.
        PanelBox(station.Frame, "Valve socket", new Vector3(0, 0, .016f), new Vector3(.026f, .058f, .032f), _panelRubber);
        PanelHardware(station.Frame, "Valve socket lip", new Vector3(0, 0, -.0005f), new Vector3(.030f, .061f, .003f), _panelMetal);
        PanelHardware(station.Frame, "Valve travel slot", new Vector3(0, 0, -.0021f), new Vector3(.018f, .050f, .0015f), _panelRubber);
        var lever = AddPanelLever(station.Frame, handleMaterial);
        var open = AddPanelButton(station, "valve-" + index + "-open", "OPEN " + ValveTitles[index].Replace('\n', ' '), ValveKeyBounds, 7, false);
        var close = AddPanelButton(station, "valve-" + index + "-close", "CLOSE " + ValveTitles[index].Replace('\n', ' '), ValveKeyBounds, 7, false);
        var repair = AddPanelButton(station, "line-" + index + "-repair", "REPAIR " + ValveTitles[index].Replace('\n', ' '), ValveKeyBounds, 7, false);
        open.Contact = close.Contact = repair.Contact = new PanelContact(lever, LeverGrip, LeverCrown);
        open.Label.gameObject.SetActive(false); close.Label.gameObject.SetActive(false); repair.Label.gameObject.SetActive(false);
        SetPanelKeyVisible(open, false); SetPanelKeyVisible(close, false); SetPanelKeyVisible(repair, false);
        var labelPosition = new Vector3(2.4470f, surface.Position.y, surface.Position.z);
        // Only 18 mm remain between the upper lamps and the lower OPEN legends.
        AddValveLegend(index < 6 ? ValveTitles[index] : ValveTitles[index].Replace('\n', ' '),
            new PanelSurface(labelPosition + Vector3.up * (index < 6 ? .062f : .052f), Vector3.left, 76, index < 6 ? 24 : 16), 8);
        AddValveLegend("OPEN", new PanelSurface(labelPosition + Vector3.up * .036f, Vector3.left, 52, 14), 6);
        AddValveLegend("CLOSE", new PanelSurface(labelPosition + Vector3.down * .036f, Vector3.left, 52, 14), 6, topInset: 5);
        var indicatorPosition = new Vector3(2.446821f, index < 6 ? -.113018f : -.284756f,
            ValveSurfaces[index % 6].Position.z + .000158f);
        var indicators = AddPanel("Valve position lamps", new PanelSurface(indicatorPosition, Vector3.left, 70, 50), default);
        PanelRule(indicators, new Rect(22, 1.636f, 26, 11.114f), PanelDark);
        PanelRule(indicators, new Rect(22, 37.25f, 26, 11.114f), PanelDark);
        var openLamp = PanelLamp(indicators, "Open position", new Rect(23, 2.636f, 24, 9.114f));
        var closedLamp = PanelLamp(indicators, "Closed position", new Rect(23, 38.25f, 24, 9.114f));
        Label(indicators.Canvas, "Open legend", "OPEN", 1, 13, 68, 8, 6, PanelDark).alignment = TextAlignmentOptions.Center;
        var connection = Label(indicators.Canvas, "Connection state", "SYNC", 1, 21, 68, 8, 6, PanelDark);
        connection.alignment = TextAlignmentOptions.Center;
        Label(indicators.Canvas, "Closed legend", "CLOSED", 1, 29, 68, 8, 6, PanelDark).alignment = TextAlignmentOptions.Center;
        return new PanelValve(station, lever, open, close, repair, openLamp, closedLamp, connection);
    }

    private Image PanelLamp(Station station, string name, Rect bounds)
    {
        var lamp = Node(station.Canvas, name, bounds.x, bounds.y, bounds.width, bounds.height).gameObject.AddComponent<Image>();
        lamp.material = _graphics; lamp.color = PanelOff; lamp.raycastTarget = false;
        return lamp;
    }

    private Transform AddPanelLever(Transform parent, Material handleMaterial)
    {
        var lever = new GameObject("Live lever").transform;
        lever.gameObject.layer = parent.gameObject.layer; lever.SetParent(parent, false);
        lever.localPosition = LeverPivot;
        PanelCylinder(lever, "Lever pivot", Vector3.zero, new Vector3(.010f, .004f, .010f), _panelMetal);
        var stem = PanelCylinder(lever, "Lever stem", new Vector3(0, 0, -.009f), new Vector3(.0045f, .009f, .0045f), _panelMetal);
        stem.localRotation = Quaternion.Euler(90, 0, 0);
        PanelHardware(lever, "Lever grip", LeverGrip.center, LeverGrip.size, handleMaterial);
        PanelHardware(lever, "Grip crown", LeverCrown.center, LeverCrown.size, handleMaterial);
        Box(lever, "Grip position mark", new Vector3(0, .005f, -.029f), new Vector3(.006f, .001f, .0004f), _panelMarks);
        lever.gameObject.SetActive(false);
        return lever;
    }

    private void BuildApuControl(Material handleMaterial)
    {
        _apuPanel = AddPanel("APU isolation", ApuSurface, default);
        var caution = Lit("APU caution enamel", new Color(.72f, .46f, .10f, 1));
        caution.SetFloat("_Metallic", .08f); caution.SetFloat("_Smoothness", .35f);
        _panelFinish.Apply(caution, ClientDeviceSurfaceFinish.Surface.Polymer);
        AddPanelModelPart(_apuPanel.Frame, "apu-control-shell", _panelPaint);
        AddPanelModelPart(_apuPanel.Frame, "apu-control-trim", _panelMetal);
        AddPanelModelPart(_apuPanel.Frame, "apu-control-gasket", _panelRubber);
        AddPanelModelPart(_apuPanel.Frame, "apu-control-caution", caution).shadowCastingMode = ShadowCastingMode.Off;
        _apuLever = AddPanelLever(_apuPanel.Frame, handleMaterial);
        foreach (var renderer in _apuLever.GetComponentsInChildren<Renderer>(true))
        {
            renderer.shadowCastingMode = ShadowCastingMode.On;
            renderer.receiveShadows = true;
            renderer.lightProbeUsage = LightProbeUsage.BlendProbes;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.BlendProbes;
        }
        var guardMaterial = Lit("APU guard enamel", new Color(.35f, .018f, .009f, 1));
        guardMaterial.SetFloat("_Metallic", .08f); guardMaterial.SetFloat("_Smoothness", .42f);
        _panelFinish.Apply(guardMaterial, ClientDeviceSurfaceFinish.Surface.Polymer);
        _apuGuard = new GameObject("APU guard hinge").transform;
        _apuGuard.gameObject.layer = _apuPanel.Frame.gameObject.layer; _apuGuard.SetParent(_apuPanel.Frame, false);
        _apuGuard.localPosition = ApuGuardPivot;
        AddPanelModelPart(_apuGuard, "apu-guard-shell", guardMaterial);
        AddPanelModelPart(_apuGuard, "apu-guard-liner", _panelPaint);
        AddPanelModelPart(_apuGuard, "apu-guard-hardware", _panelMetal);
        var guardLabel = new GameObject("Guard legend").AddComponent<RectTransform>();
        guardLabel.gameObject.layer = _apuGuard.gameObject.layer; guardLabel.SetParent(_apuGuard, false);
        guardLabel.localPosition = new Vector3(.020f, .006f, -.0394f);
        guardLabel.localScale = Vector3.one * .001f; guardLabel.sizeDelta = new Vector2(30, 36);
        guardLabel.gameObject.AddComponent<Canvas>().renderMode = RenderMode.WorldSpace;
        var isolationLegend = Label(guardLabel, "Isolation legend", "ISOLATE\nAPU", 0, 0, 30, 36, 6.5f, Ink);
        isolationLegend.alignment = TextAlignmentOptions.Center; isolationLegend.fontStyle = FontStyles.Bold;
        _apuGuardOpenKey = AddPanelButton(_apuPanel, "apu-guard-open", "OPEN APU GUARD", ApuKeyBounds, 7, false);
        _apuGuardCloseKey = AddPanelButton(_apuPanel, "apu-guard-close", "CLOSE APU GUARD", ApuKeyBounds, 7, false);
        _apuIsolateKey = AddPanelButton(_apuPanel, "apu-isolate", "CONFIRM APU ISOLATION", ApuKeyBounds, 7, false);
        _apuResetKey = AddPanelButton(_apuPanel, "apu-reset", "GROUND SERVICE RESET", ApuKeyBounds, 7, false);
        _apuIsolateKey.Contact = _apuResetKey.Contact = new PanelContact(_apuLever, LeverGrip, LeverCrown);
        foreach (var key in new[] { _apuGuardOpenKey, _apuGuardCloseKey, _apuIsolateKey, _apuResetKey })
        {
            key.Label.gameObject.SetActive(false);
            SetPanelKeyVisible(key, false);
        }
        _apuStatusPanel = AddPanel("APU status", ApuStatusSurface, new Rect(1, 1, 128, 20), 7);
        PanelBacking(_apuStatusPanel, new Rect(0, 0, 130, 32));
        _apuLamp = PanelLamp(_apuStatusPanel, "APU state lamp", new Rect(4, 4, 3, 12));
        _apuStatusPanel.Readout.alignment = TextAlignmentOptions.Center;
        SetPanelPrompt(_apuStatusPanel, new Rect(1, 22, 128, 9), 6);
        _apuStatusPanel.Prompt.alignment = TextAlignmentOptions.Center;
    }

    private void BuildEngineeringHousing()
    {
        AddPanelModelPart(_engineering.Frame, "engineering-control-shell", _panelPaint);
        AddPanelModelPart(_engineering.Frame, "engineering-control-trim", _panelMetal);
        AddPanelModelPart(_engineering.Frame, "engineering-control-gasket", _panelRubber);
        AddPanelModelPart(_engineering.Frame, "engineering-control-hardware", _panelMetal);
    }

    private void BuildNavigationHousing()
    {
        AddPanelModelPart(_navigation.Frame, "navigation-control-shell", _panelPaint);
        AddPanelModelPart(_navigation.Frame, "navigation-control-trim", _panelMetal);
        AddPanelModelPart(_navigation.Frame, "navigation-control-gasket", _panelRubber);
        AddPanelModelPart(_navigation.Frame, "navigation-control-hardware", _panelMetal);
    }

    private MeshRenderer AddPanelModelPart(Transform parent, string asset, Material material)
    {
        if (!_panelModelMeshes.TryGetValue(asset, out var mesh))
        {
            var resource = "Modstoke.Client.Assets.spacecraft-" + asset + ".msa";
            using var stream = typeof(ClientSpacecraftStations).Assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException("The spacecraft panel model is missing: " + asset);
            var data = SpacecraftMeshData.Read(stream, asset);
            mesh = new Mesh { name = asset };
            _panelModelMeshes.Add(asset, mesh);
            mesh.indexFormat = data.Vertices.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.vertices = data.Vertices; mesh.normals = data.Normals; mesh.uv = data.Uv;
            mesh.SetTriangles(data.Triangles, 0);
            mesh.RecalculateBounds(); mesh.RecalculateTangents(); mesh.UploadMeshData(true);
        }
        var node = new GameObject(mesh.name);
        node.layer = parent.gameObject.layer; node.transform.SetParent(parent, false);
        node.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = node.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        return renderer;
    }

    private PressureBar AddPressureBar(string title, PanelSurface surface, float fullScale)
    {
        // Readout occupies the original 47.968 x 23.713 mm inset below each bar.
        var station = AddPanel(title + " pressure", surface, new Rect(1, 246.327f, 46, 20), 7);
        station.Readout.alignment = TextAlignmentOptions.Center;
        PanelBacking(station, new Rect(0, 246.327f, 48, 20));
        PanelBacking(station, new Rect(-28, 218, 104, 22));
        Label(station.Canvas, "Pressure name", title + " kPa", -27, 219, 102, 20, 9, Ink).alignment = TextAlignmentOptions.Center;
        PanelBacking(station, new Rect(0, 0, 48, 216));
        var fill = PanelLamp(station, "Live pressure", new Rect(5, 208, 10, 0));
        fill.color = PanelCyan;
        for (var index = 1; index < 20; index++)
            PanelRule(station, new Rect(5, 208 - index * 10, 10, 1), PanelDark);
        for (var index = 0; index <= 10; index++)
        {
            var y = 208 - index * 20;
            var tick = PanelLamp(station, "Pressure tick", new Rect(18, y, index % 5 == 0 ? 8 : 4, 1));
            tick.color = Muted;
            if (index % 2 == 0)
                Label(station.Canvas, "Pressure scale", (fullScale * index / 10).ToString("0", System.Globalization.CultureInfo.InvariantCulture),
                    27, y - 5, 20, 10, 7, Ink).alignment = TextAlignmentOptions.Center;
        }
        return new PressureBar(station, fill, fullScale);
    }

    private Station AddCoolingDisplay(string zone, PanelSurface surface)
    {
        var station = AddPanel(zone + " cooling", surface, new Rect(2, 11, 64, 20), 7);
        PanelBacking(station, new Rect(0, 0, 68, 32));
        station.Readout.alignment = TextAlignmentOptions.Center;
        var title = Label(station.Canvas, "Thermal zone", zone, 2, 1, 64, 9, 5, Ink);
        title.alignment = TextAlignmentOptions.Center;
        return station;
    }

    private PanelGauge AddPanelGauge(string name, PanelSurface surface)
    {
        var station = AddPanel(name + " gauge", surface, new Rect(27, 72, 42, 12), 10);
        station.Readout.alignment = TextAlignmentOptions.Center;
        station.Readout.enableWordWrapping = false;
        AddDialFace(station, .094f);
        AddDialScale(station, contents: false);
        for (var index = 0; index <= 5; index++)
        {
            var rotation = Quaternion.Euler(0, 0, Mathf.Lerp(135, -135, index / 5f));
            var p = rotation * new Vector3(0, .0355f, 0);
            if (index is 0 or 5) p += new Vector3(index == 0 ? .003f : -.003f, -.001f, 0);
            Label(station.Canvas, "Dial value", (index * 20).ToString(System.Globalization.CultureInfo.InvariantCulture),
                48 + p.x * 1000 - 8, 48 - p.y * 1000 - 4, 16, 8, 4.5f, Ink).alignment = TextAlignmentOptions.Center;
        }
        var title = Label(station.Canvas, "Gauge name", name, 31, 63, 34, 8, 5.5f, Ink);
        title.alignment = TextAlignmentOptions.Center;
        var needle = AddDialNeedle(station, Vector3.zero, .030f, _amberLine);
        return new PanelGauge(station, needle);
    }

    private void AddDialScale(Station station, bool contents)
    {
        var mesh = contents ? _panelContentsScaleMesh : _panelDialScaleMesh;
        if (mesh == null)
        {
            var count = contents ? 20 : 50;
            var vertices = new List<Vector3>((count + 1) * (contents ? 8 : 4));
            var triangles = new List<int>((count + 1) * (contents ? 12 : 6));
            for (var dial = 0; dial < (contents ? 2 : 1); dial++)
            {
                var pivot = contents ? new Vector3(dial == 0 ? -.039932f : .039932f, -.015839f, 0) : Vector3.zero;
                var radius = contents ? .031f : .0275f;
                var sweep = contents ? 30f : 135f;
                for (var index = 0; index <= count; index++)
                {
                    var major = index % (contents ? 5 : 10) == 0;
                    var length = major ? .004f : index % 5 == 0 ? .0028f : .0016f;
                    var halfWidth = major ? .0005f : .00025f;
                    var rotation = Quaternion.Euler(0, 0, Mathf.Lerp(sweep, -sweep, index / (float)count));
                    var start = vertices.Count;
                    vertices.Add(pivot + rotation * new Vector3(-halfWidth, radius - length / 2, -.0033f));
                    vertices.Add(pivot + rotation * new Vector3(-halfWidth, radius + length / 2, -.0033f));
                    vertices.Add(pivot + rotation * new Vector3(halfWidth, radius + length / 2, -.0033f));
                    vertices.Add(pivot + rotation * new Vector3(halfWidth, radius - length / 2, -.0033f));
                    triangles.Add(start); triangles.Add(start + 1); triangles.Add(start + 2);
                    triangles.Add(start); triangles.Add(start + 2); triangles.Add(start + 3);
                }
            }
            mesh = new Mesh { name = contents ? "Contents scale graduations" : "Tank scale graduations" };
            if (contents) _panelContentsScaleMesh = mesh; else _panelDialScaleMesh = mesh;
            mesh.vertices = vertices.ToArray(); mesh.triangles = triangles.ToArray();
            mesh.RecalculateNormals(); mesh.RecalculateBounds(); mesh.UploadMeshData(true);
        }
        var node = new GameObject("Dial scale") { layer = station.Frame.gameObject.layer };
        node.transform.SetParent(station.Frame, false);
        node.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = node.AddComponent<MeshRenderer>(); renderer.sharedMaterial = _panelMarks;
        ClientDeviceScreenMaterial.ConfigureRenderer(renderer);
    }

    private void AddDialFace(Station station, float diameter, bool artwork = true)
    {
        AddDialRim(station.Frame, diameter, artwork);
        var disk = PanelCylinder(station.Frame, "Recessed dial face", new Vector3(0, 0, -.0015f),
            new Vector3(diameter - .003f, .00125f, diameter - .003f), _panelRubber);
        disk.localRotation = Quaternion.Euler(90, 0, 0);
        if (!artwork) return;
        var size = diameter * 1000;
        var art = Node(station.Canvas, "Instrument face", (station.Width - size) / 2, (station.Height - size) / 2, size, size)
            .gameObject.AddComponent<RawImage>();
        art.material = _panelFaceMaterial; art.texture = _panelFaceTexture; art.raycastTarget = false;
        art.rectTransform.localPosition += Vector3.forward * .1f;
        art.transform.SetAsFirstSibling();
    }

    private void AddDialRim(Transform parent, float diameter, bool artwork)
    {
        var mesh = artwork ? _panelDialRimMesh : _panelContentsRimMesh;
        if (mesh == null)
        {
            // The dual dial's original offset pivots require a wider clear aperture.
            var inner = artwork ? .432f : .468f;
            var profile = new Vector2[] { new(.5f, .0025f), new(.5f, -.001f), new(.49f, -.0035f),
                new(.48f, -.0045f), new(inner + .008f, -.0045f), new(inner, -.0028f), new(inner, .0025f) };
            const int segments = 64;
            var vertices = new List<Vector3>((profile.Length - 1) * (segments + 1) * 2);
            var normals = new List<Vector3>(vertices.Capacity);
            var uv = new List<Vector2>(vertices.Capacity);
            var triangles = new List<int>((profile.Length - 1) * segments * 6);
            var distance = 0f;
            for (var band = 0; band + 1 < profile.Length; band++)
            {
                var a = profile[band]; var b = profile[band + 1];
                var nextDistance = distance + new Vector2((b.x - a.x) * diameter, b.y - a.y).magnitude;
                var start = vertices.Count;
                for (var index = 0; index <= segments; index++)
                {
                    var angle = index * Mathf.PI * 2 / segments;
                    var x = Mathf.Cos(angle); var y = Mathf.Sin(angle);
                    vertices.Add(new Vector3(a.x * x, a.x * y, a.y));
                    vertices.Add(new Vector3(b.x * x, b.x * y, b.y));
                    var normal = new Vector3((a.y - b.y) * x, (a.y - b.y) * y, b.x - a.x).normalized;
                    normals.Add(normal); normals.Add(normal);
                    var circumference = angle * (a.x + b.x) * .5f * diameter;
                    uv.Add(new Vector2(circumference, distance));
                    uv.Add(new Vector2(circumference, nextDistance));
                    if (index == segments) continue;
                    var v = start + index * 2;
                    triangles.Add(v); triangles.Add(v + 1); triangles.Add(v + 3);
                    triangles.Add(v); triangles.Add(v + 3); triangles.Add(v + 2);
                }
                distance = nextDistance;
            }
            mesh = new Mesh { name = artwork ? "Instrument profiled rim" : "Contents profiled rim" };
            if (artwork) _panelDialRimMesh = mesh; else _panelContentsRimMesh = mesh;
            mesh.vertices = vertices.ToArray(); mesh.normals = normals.ToArray(); mesh.triangles = triangles.ToArray();
            mesh.uv = uv.ToArray(); mesh.RecalculateTangents();
            mesh.RecalculateBounds(); mesh.UploadMeshData(true);
        }
        var node = new GameObject("Machined dial rim") { layer = parent.gameObject.layer };
        node.transform.SetParent(parent, false); node.transform.localScale = new Vector3(diameter, diameter, 1);
        node.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = node.AddComponent<MeshRenderer>(); renderer.sharedMaterial = _panelMetal;
        ClientDeviceScreenMaterial.ConfigureRenderer(renderer);
    }

    private Transform AddDialNeedle(Station station, Vector3 position, float length, Material material)
    {
        if (_panelNeedleMesh == null)
        {
            var outline = new Vector2[] { new(-.5f, -.16f), new(.5f, -.16f), new(.6f, -.1f),
                new(.32f, .05f), new(.16f, .82f), new(0, 1), new(-.16f, .82f), new(-.32f, .05f), new(-.6f, -.1f) };
            var vertices = new List<Vector3>(outline.Length * 10);
            var triangles = new List<int>(outline.Length * 12);
            for (var index = 0; index < outline.Length; index++)
            {
                var a = outline[index]; var b = outline[(index + 1) % outline.Length];
                var start = vertices.Count;
                vertices.Add(new Vector3(0, 0, -.5f));
                vertices.Add(new Vector3(b.x, b.y, -.5f)); vertices.Add(new Vector3(a.x, a.y, -.5f));
                vertices.Add(new Vector3(0, 0, .5f));
                vertices.Add(new Vector3(a.x, a.y, .5f)); vertices.Add(new Vector3(b.x, b.y, .5f));
                vertices.Add(new Vector3(a.x, a.y, -.5f)); vertices.Add(new Vector3(b.x, b.y, -.5f));
                vertices.Add(new Vector3(b.x, b.y, .5f)); vertices.Add(new Vector3(a.x, a.y, .5f));
                triangles.Add(start); triangles.Add(start + 1); triangles.Add(start + 2);
                triangles.Add(start + 3); triangles.Add(start + 4); triangles.Add(start + 5);
                triangles.Add(start + 6); triangles.Add(start + 7); triangles.Add(start + 8);
                triangles.Add(start + 6); triangles.Add(start + 8); triangles.Add(start + 9);
            }
            _panelNeedleMesh = new Mesh { name = "Counterweighted instrument pointer" };
            _panelNeedleMesh.vertices = vertices.ToArray(); _panelNeedleMesh.triangles = triangles.ToArray();
            _panelNeedleMesh.RecalculateNormals();
            SetPanelSurfaceUv(_panelNeedleMesh, new Vector3(.003f, .030f, .0007f));
            _panelNeedleMesh.RecalculateBounds(); _panelNeedleMesh.UploadMeshData(true);
        }
        var needle = new GameObject("Live contents needle").transform;
        needle.gameObject.layer = station.Frame.gameObject.layer; needle.SetParent(station.Frame, false);
        needle.localPosition = position + Vector3.back * .004f;
        for (var layer = 0; layer < 2; layer++)
        {
            var node = new GameObject(layer == 0 ? "Needle frame" : "Needle insert") { layer = needle.gameObject.layer };
            node.transform.SetParent(needle, false);
            node.transform.localPosition = Vector3.back * (layer == 0 ? 0 : .0005f);
            node.transform.localScale = new Vector3(layer == 0 ? .003f : .002f, length * (layer == 0 ? 1 : .94f),
                layer == 0 ? .0007f : .0002f);
            node.AddComponent<MeshFilter>().sharedMesh = _panelNeedleMesh;
            var renderer = node.AddComponent<MeshRenderer>(); renderer.sharedMaterial = layer == 0 ? _panelMetal : material;
            ClientDeviceScreenMaterial.ConfigureRenderer(renderer);
        }
        var hub = PanelCylinder(needle, "Needle hub", Vector3.back * .0008f, new Vector3(.006f, .0008f, .006f), _panelMetal);
        hub.localRotation = Quaternion.Euler(90, 0, 0);
        needle.gameObject.SetActive(false);
        return needle;
    }

    private ContentsGauge AddContentsGauge()
    {
        var station = AddPanel("Fuel and air contents", ContentsSurface, default);
        // The two native pivots need a thinner rim than the single-dial artwork.
        AddDialFace(station, .124f, artwork: false);
        AddDialScale(station, contents: true);
        Label(station.Canvas, "Contents title", "CONTENTS", 20, 15, 84, 12, 8, Ink).alignment = TextAlignmentOptions.Center;
        Label(station.Canvas, "Contents scale", "% RESERVE", 32, 106, 60, 9, 6, Muted).alignment = TextAlignmentOptions.Center;
        var needles = new Transform[2];
        var readouts = new TextMeshProUGUI[2];
        for (var index = 0; index < 2; index++)
        {
            // Original hub centres at z=3.102537 / 3.022673, y=-.958609.
            var pivot = new Vector3(index == 0 ? -.039932f : .039932f, -.015839f, 0);
            var material = index == 0 ? _amberLine : _cyanLine;
            needles[index] = AddDialNeedle(station, pivot, .026f, material);
            var x = index == 0 ? 8 : 74;
            Label(station.Canvas, "Contents legend", index == 0 ? "FUEL" : "AIR MIX", x, 32, 42, 12, 7,
                index == 0 ? Amber : Ink).alignment = TextAlignmentOptions.Center;
            Label(station.Canvas, "Contents range", "0 - 100%", index == 0 ? 14 : 70, 84, 40, 6, 4.5f, Muted)
                .alignment = TextAlignmentOptions.Center;
            readouts[index] = Label(station.Canvas, "Live contents", "SYNC", index == 0 ? 14 : 70, 92, 40, 12, 8, Ink);
            readouts[index].alignment = TextAlignmentOptions.Center;
        }
        return new ContentsGauge(needles[0], needles[1], readouts[0], readouts[1]);
    }

    private void UpdateCabinPanels(ClientSpacecraftView view)
    {
        var systems = view.Flight.Systems;
        var ready = view.Ready && view.Available && systems.Ready;
        var door = DoorPosition(_body.CabinDoorAmount, systems.CabinDoorOpen, ready, systems,
            ClientSpacecraftSystems.CanOpenCabinDoor(systems, _body.InnerDoorClosed, out _));
        var access = !ready ? "SYNC" : !systems.CrewLocked ? "OPEN ACCESS" : view.CrewAccess ? "CREW ACCESS" : "REQUEST ACCESS";
        var permitted = ready && (!systems.CrewLocked || view.CrewAccess);
        _readerStatusPanel.Readout.text = access;
        _readerStatusPanel.Readout.color = permitted ? PanelCyan : Amber;
        _readerLamp.color = !ready ? PanelOff : permitted ? PanelCyan : Amber;
        _readerStatusLine.color = _accessStatusLine.color = _readerLamp.color;
        _readerDoor.text = "A02 / " + door;
        _readerLock.text = !ready ? "" : "CREW LOCK " + (systems.CrewLocked ? "ON" : "OFF");
        _accessPanel.Readout.text = access;
        _accessPanel.Readout.color = permitted ? PanelCyan : Amber;
        _accessDetail.text = !ready ? "" : view.IsHost
            ? view.CrewRequest.Length == 0 ? "NO PENDING REQUEST" : "PENDING REQUEST\n" + view.CrewRequest
            : view.CrewAccess ? "IDENTITY AVAILABLE" : !systems.CrewLocked ? "CREW LOCK OFF" : "ACCESS APPROVAL REQUIRED";
        var doorAmount = _body.CabinDoorAmount;
        var doorKnown = ready && _body.Ready && float.IsFinite(doorAmount);
        var travel = doorKnown ? Mathf.Clamp01(doorAmount) : 0;
        _accessDoorLeft.gameObject.SetActive(doorKnown);
        _accessDoorRight.gameObject.SetActive(doorKnown);
        _accessDoorLeft.rectTransform.sizeDelta = _accessDoorRight.rectTransform.sizeDelta = new Vector2(11 * (1 - travel), 2);
        _accessDoorRight.rectTransform.anchoredPosition = new Vector2(55 + 11 * travel, -110);
        _accessDoorLeft.color = _accessDoorRight.color = travel > 0 && travel < 1 ? Amber : PanelCyan;
        _accessDoorState.text = doorKnown ? door : "SYNC";
        _accessDoorState.color = doorKnown ? _accessDoorLeft.color : Muted;
        var innerAmount = _body.InnerDoorAmount;
        var rampAmount = view.Flight.DoorAmount;
        _accessInnerGate.gameObject.SetActive(ready && _body.Ready && float.IsFinite(innerAmount));
        _accessRampGate.gameObject.SetActive(ready && _body.Ready && float.IsFinite(rampAmount));
        _accessInnerGate.color = innerAmount > .01f ? Amber : PanelCyan;
        _accessRampGate.color = rampAmount > .01f ? Amber : PanelCyan;
        _accessCabinPressure.text = ready && float.IsFinite(systems.CabinPressure) && systems.CabinPressure >= 0
            ? Number(systems.CabinPressure) + " kPa" : "-- kPa";
        _accessAirlockPressure.text = ready && float.IsFinite(systems.AirlockPressure) && systems.AirlockPressure >= 0
            ? Number(systems.AirlockPressure) + " kPa" : "-- kPa";
        _accessEntryPressure.text = ready && float.IsFinite(systems.EntryPressure) && systems.EntryPressure >= 0
            ? Number(systems.EntryPressure) + " kPa" : "-- kPa";
        _crewLockKey.Label.text = !ready ? "CREW LOCK" : "CREW LOCK " + (systems.CrewLocked ? "ON" : "OFF");
        // Latch the request key with the displayed row, not at click time.
        var pending = ready && view.IsHost && view.CrewRequest.Length != 0 && view.CrewRequestKey.Length != 0;
        _approveCrewKey.CrewRequestKey = pending ? view.CrewRequestKey : "";
        _denyCrewKey.CrewRequestKey = pending ? view.CrewRequestKey : "";
        SetPanelKeyVisible(_crewLockKey, ready && view.IsHost);
        SetPanelKeyVisible(_approveCrewKey, pending);
        SetPanelKeyVisible(_denyCrewKey, pending);

        // The host decides permission and permits forward-cabin egress through
        // closed A02. CrewAccess=false must not hide or disable either exit.
        _forwardDoorPanel.Readout.text = "A02 / CABIN\n" + door;
        _aftDoorPanel.Readout.text = "A02 / CHAMBER\n" + access;
        _forwardDoorPanel.Readout.color = ready ? Ink : Amber;
        _aftDoorPanel.Readout.color = permitted ? Ink : Amber;
        _forwardDoorKey.Label.text = _aftDoorKey.Label.text = !ready ? "CABIN DOOR"
            : systems.CabinDoorOpen ? "CLOSE CABIN" : "OPEN CABIN";
        var cabinCycle = ready && systems.AirlockTarget == SpacecraftAirlockTarget.Cabin;
        _forwardCycleKey.Label.text = !cabinCycle ? "TO CABIN" : systems.AirlockCycle switch
        {
            SpacecraftAirlockPhase.Sealing => "SEALING",
            SpacecraftAirlockPhase.Equalizing => "EQUALIZING",
            SpacecraftAirlockPhase.PassageRequested => "OPEN REQUESTED",
            SpacecraftAirlockPhase.Fault => "RETRY CABIN",
            _ => "TO CABIN"
        };
        _forwardCycleKey.Label.color = !ready ? Muted : cabinCycle && systems.AirlockCycle == SpacecraftAirlockPhase.Fault ? Amber
            : cabinCycle && systems.AirlockCycle is SpacecraftAirlockPhase.Sealing or SpacecraftAirlockPhase.Equalizing ? PanelCyan : Ink;
        _forwardCancelKey.Label.color = !ready ? Muted
            : systems.AirlockCycle is SpacecraftAirlockPhase.Sealing or SpacecraftAirlockPhase.Equalizing ? Amber : Ink;
        UpdatePanelGauge(_fuelGauge, systems.Fuel01 * 100, 100, "%", ready);
        // OxygenReserve01 supplies breathing-gas mix; CO2 scrubbing is separate.
        UpdatePanelGauge(_airMixGauge, systems.OxygenReserve01 * 100, 100, "%", ready);
        UpdatePanelGauge(_coolantGauge, systems.Coolant01 * 100, 100, "%", ready);
        UpdateContentsNeedle(_contentsGauge.Fuel, _contentsGauge.FuelReadout, systems.Fuel01, ready);
        UpdateContentsNeedle(_contentsGauge.Air, _contentsGauge.AirReadout, systems.OxygenReserve01, ready);
        UpdatePressureBar(_pressureBars[0], systems.CabinPressure, ready);
        UpdatePressureBar(_pressureBars[1], systems.AirlockPressure, ready);
        UpdatePressureBar(_pressureBars[2], systems.AirFeedPressure, ready);
        UpdatePressureBar(_pressureBars[3], systems.FuelFeedPressure, ready);
        UpdatePressureBar(_pressureBars[4], systems.CoolantPressure, ready);
        for (var index = 0; index < _valves.Length; index++)
        {
            var valve = _valves[index];
            var open = PanelValveOpen(systems, (SpacecraftValve)index);
            var fault = PanelLineFaulted(systems, (SpacecraftValve)index);
            var leak = index == (int)SpacecraftValve.CoolantFeed && systems.CoolantLeak;
            valve.OpenLamp.color = ready && open ? PanelCyan : PanelOff;
            valve.ClosedLamp.color = ready && !open ? Amber : PanelOff;
            valve.Connection.text = !ready ? "SYNC" : leak ? fault ? "LEAK + FAULT" : "LEAK" : fault ? "FAULT" : "LINE OK";
            valve.Connection.color = ready && (fault || leak) ? Critical : PanelDark;
            valve.Close.Label.text = ready && fault ? "CLOSE / ISOLATE " + ValveTitles[index].Replace('\n', ' ') : valve.Close.Title;
        }
        UpdateCoolantRepairReadout(view);
        UpdateSparesReadout(view);
        UpdateEngineServiceReadout(view);
        UpdateHullService(view);
        UpdateWarpServiceReadout(view);
        UpdateSealantReadouts(view);
        UpdateSystemServiceReadouts(view);
        _apuStatusPanel.Readout.text = !ready ? "SYNC" : systems.ApuIsolated ? "ISOLATED\nGENERATOR INHIBITED"
            : "CONNECTED\nGENERATOR " + GeneratorState(systems, ready);
        _apuStatusPanel.Readout.color = !ready || systems.ApuIsolated ? Amber : Ink;
        _apuLamp.color = !ready ? PanelOff : systems.ApuIsolated ? Amber : PanelCyan;
        // Coolant pressure already includes bus, supply and radiator gates.
        // These remain compartment temperatures, not coolant temperatures.
        var thermalPower = ready && SpacecraftElectrical.IsPowered(systems.Electrical, SpacecraftCircuit.Thermal)
            && float.IsFinite(systems.CoolantPressure) && systems.CoolantPressure > 0;
        UpdateCoolingDisplay(_cabinCoolingPanel, systems.CabinTemperatureC, ready,
            thermalPower && PanelLineAvailable(systems, SpacecraftValve.CabinCooling) && systems.CabinPressure >= 10);
        UpdateCoolingDisplay(_airlockCoolingPanel, systems.AirlockTemperatureC, ready,
            thermalPower && PanelLineAvailable(systems, SpacecraftValve.AirlockCooling) && systems.AirlockPressure >= 10);
    }

    private bool HasPanelFeedback(Station station, ClientSpacecraftView view) => _commandStation == station
        && Time.unscaledTime < _feedbackUntil && !string.IsNullOrWhiteSpace(view.Feedback) && view.Feedback != _feedbackBefore;

    private void UpdatePanelPrompts(ClientSpacecraftView view)
    {
        UpdateSealantPrompts(view);
        _systemServicePanel.Prompt.text = PanelPrompt(_systemServicePanel, view);
        foreach (var panel in _hullServicePanels) panel.Prompt.text = PanelPrompt(panel, view);
        _warpServicePanel.Prompt.text = PanelPrompt(_warpServicePanel, view);
        _accessPanel.Prompt.text = PanelPrompt(_accessPanel, view, includeReader: true);
        _forwardDoorPanel.Prompt.text = PanelPrompt(_forwardDoorPanel, view);
        _aftDoorPanel.Prompt.text = PanelPrompt(_aftDoorPanel, view);
        var feedback = HasPanelFeedback(_apuPanel, view) ? view.Feedback : "";
        if (feedback.Length != 0) _apuStatusPanel.Prompt.text = feedback;
        foreach (var valve in _valves)
            if (HasPanelFeedback(valve.Station, view)) { feedback = view.Feedback; break; }
        if (HasPanelFeedback(_coolantRepairPanel, view)) feedback = view.Feedback;
        if (HasPanelFeedback(_sparesPanel, view)) feedback = view.Feedback;
        if (HasPanelFeedback(_engineServicePanel, view)) feedback = view.Feedback;
        if (feedback.Length == 0 && _hovered != null)
        {
            if (_hovered.Station == _apuPanel)
            {
                feedback = PanelPrompt(_apuPanel, view);
                _apuStatusPanel.Prompt.text = feedback;
            }
            else if (_hovered.Station == _coolantRepairPanel) feedback = PanelPrompt(_coolantRepairPanel, view);
            else if (_hovered.Station == _sparesPanel) feedback = PanelPrompt(_sparesPanel, view);
            else if (_hovered.Station == _engineServicePanel) feedback = PanelPrompt(_engineServicePanel, view);
            else foreach (var valve in _valves)
                if (_hovered.Station == valve.Station) { feedback = PanelPrompt(valve.Station, view); break; }
        }
        var systems = view.Flight.Systems;
        var stock = view.Ready && view.Available && systems.Ready
            ? "LINE SPARES " + systems.LineSpares.ToString(CultureInfo.InvariantCulture) + " / " + ClientSpacecraftSystems.LineSparesCapacity
            : "LINE SPARES / SYNC";
        _valveFeedbackPanel.Prompt.text = (feedback.Length == 0 ? "" : feedback + "\n") + stock + "  |  REPAIR COST 1";
    }

    private void UpdateEngineeringPanelControls(ClientSpacecraftView view)
    {
        var systems = view.Flight.Systems;
        var ready = view.Ready && view.Available && systems.Ready;
        var usable = ready && view.Flight.Travel == SpacecraftTravel.None;
        for (var index = 0; index < _valves.Length; index++)
        {
            var open = PanelValveOpen(systems, (SpacecraftValve)index);
            var fault = PanelLineFaulted(systems, (SpacecraftValve)index);
            _valves[index].Lever.gameObject.SetActive(ready);
            if (ready) _valves[index].Lever.localRotation = PanelLeverRotation(open);
            SetPanelKeyVisible(_valves[index].Open, usable && !open && !fault);
            SetPanelKeyVisible(_valves[index].Close, usable && open);
            SetPanelKeyVisible(_valves[index].Repair, index != (int)SpacecraftValve.CoolantFeed && usable && !open && fault);
        }
        _apuLever.gameObject.SetActive(ready);
        if (ready) _apuLever.localRotation = PanelLeverRotation(!systems.ApuIsolated);
        var reset = usable && systems.ApuIsolated && ApuResetAvailable(view);
        // Shared hardware follows telemetry even when this client cannot operate it.
        _apuGuard.localRotation = ApuGuardRotation(systems.ApuGuardAmount);
        SetPanelKeyVisible(_apuGuardOpenKey, usable && !systems.ApuGuardOpen);
        SetPanelKeyVisible(_apuGuardCloseKey, usable && systems.ApuGuardOpen);
        var exposed = usable && systems.ApuGuardOpen && systems.ApuGuardAmount >= 1;
        SetPanelKeyVisible(_apuIsolateKey, exposed && !systems.ApuIsolated);
        SetPanelKeyVisible(_apuResetKey, exposed && systems.ApuIsolated);
        _apuStatusPanel.Prompt.text = !ready ? "" : !usable ? "TRAVEL LOCK"
            : !systems.ApuGuardOpen ? systems.ApuGuardAmount > 0 ? "GUARD CLOSING" : "GUARD CLOSED"
            : systems.ApuGuardAmount < 1 ? "GUARD OPENING"
            : systems.ApuIsolated ? !reset ? "GROUND SERVICE REQUIRED" : "GROUND RESET READY"
            : "ISOLATION READY";
    }

    private static bool ApuResetAvailable(ClientSpacecraftView view) => view.Flight.Region == SpacecraftRegion.Island
        && SpacecraftPresentation.Parked(view) && view.Flight.Velocity.sqrMagnitude <= .25f
        && view.Flight.DoorOpen && view.Flight.DoorAmount is >= .99f and <= 1;

    private string PanelPrompt(Station station, ClientSpacecraftView view, bool includeReader = false)
    {
        if (HasPanelFeedback(station, view) || includeReader
            && (HasPanelFeedback(_cardPanel, view) || HasPanelFeedback(_retinaPanel, view))) return view.Feedback;
        var hovered = _hovered;
        if (hovered == null || hovered.Station != station
            && !(includeReader && (hovered.Station == _cardPanel || hovered.Station == _retinaPanel))) return "";
        if (ClientSpacecraftSystems.TryManualRepair(hovered.Action, out _, out _)) return RepairControlPrompt(hovered, view);
        if (ClientSpacecraftSystems.TryReadSealantCommand(hovered.Action, out _) && CanUse(hovered, view))
            return "E  SEAL | 1 LINE KIT";
        return CanUse(hovered, view) ? "E  " + hovered.Label.text : BlockedReason(hovered, view);
    }

    private static void UpdatePanelGauge(PanelGauge gauge, float value, float fullScale, string unit, bool ready)
    {
        var valid = ready && float.IsFinite(value);
        gauge.Needle.gameObject.SetActive(valid);
        gauge.Station.Readout.text = !ready ? "SYNC" : !valid ? "--" : Number(value) + unit;
        gauge.Station.Readout.color = valid && value >= 0 && value <= fullScale ? Ink : Amber;
        if (valid) gauge.Needle.localRotation = Quaternion.Euler(0, 0, Mathf.Lerp(135, -135, Mathf.Clamp01(value / fullScale)));
    }

    private static bool PanelValveOpen(SpacecraftSystemState systems, SpacecraftValve valve) =>
        (systems.ValveMask & (1 << (int)valve)) != 0;

    private static bool PanelLineFaulted(SpacecraftSystemState systems, SpacecraftValve valve) =>
        (systems.LineFaultMask & (1 << (int)valve)) != 0;

    private static bool PanelLineAvailable(SpacecraftSystemState systems, SpacecraftValve valve) =>
        PanelValveOpen(systems, valve) && !PanelLineFaulted(systems, valve);

    private static void UpdatePressureBar(PressureBar bar, float pressure, bool ready)
    {
        var valid = ready && float.IsFinite(pressure);
        bar.Fill.gameObject.SetActive(valid);
        bar.Station.Readout.text = !ready ? "SYNC" : !valid ? "--" : Number(pressure);
        var inRange = valid && pressure >= 0 && pressure <= bar.FullScale;
        bar.Station.Readout.color = inRange ? Ink : Amber;
        bar.Fill.color = inRange ? PanelCyan : Amber;
        if (!valid) return;
        var height = 200 * Mathf.Clamp01(pressure / bar.FullScale);
        bar.Fill.rectTransform.anchoredPosition = new Vector2(5, -(208 - height));
        bar.Fill.rectTransform.sizeDelta = new Vector2(10, height);
    }

    private static void UpdateContentsNeedle(Transform needle, TextMeshProUGUI readout, float contents, bool ready)
    {
        var valid = ready && float.IsFinite(contents);
        needle.gameObject.SetActive(valid);
        readout.text = !ready ? "SYNC" : !valid ? "--" : Number(contents * 100) + "%";
        readout.color = valid && contents >= 0 && contents <= 1 ? Ink : Amber;
        if (valid) needle.localRotation = Quaternion.Euler(0, 0, Mathf.Lerp(30, -30, Mathf.Clamp01(contents)));
    }

    private static void UpdateCoolingDisplay(Station station, float temperature, bool ready, bool available)
    {
        var valid = ready && float.IsFinite(temperature);
        station.Readout.text = !ready ? "SYNC" : !valid ? "--\nUNAVAILABLE"
            : Number(temperature) + "\n" + (available ? "AUTO" : "UNAVAILABLE");
        station.Readout.color = valid && available ? Ink : Amber;
    }

    private readonly record struct PanelSurface(Vector3 Position, Vector3 Normal, float Width, float Height)
    {
        internal Quaternion Rotation => Quaternion.LookRotation(-Normal, Vector3.up);

        internal Vector3 ControlPoint(Rect bounds) => Position + Rotation * new Vector3(
            (bounds.center.x - Width / 2) * .001f, (Height / 2 - bounds.center.y) * .001f, -NativeScreenOffset);

        internal SpacecraftControlContact ControlContact(Rect bounds) => KeyControlContact(ControlPoint(bounds), Rotation);
    }

    private sealed record PanelGauge(Station Station, Transform Needle);
    private sealed record PanelContact(Transform Frame, Bounds Body, Bounds Tip)
    {
        internal bool Raycast(Ray ray, out float distance)
        {
            var localRay = new Ray(Frame.InverseTransformPoint(ray.origin), Frame.InverseTransformDirection(ray.direction));
            var bodyHit = Body.IntersectRay(localRay, out var bodyDistance);
            var tipHit = Tip.IntersectRay(localRay, out var tipDistance);
            if (!bodyHit && !tipHit) { distance = 0; return false; }
            var localDistance = bodyHit && tipHit ? Mathf.Min(bodyDistance, tipDistance) : bodyHit ? bodyDistance : tipDistance;
            distance = Vector3.Distance(ray.origin, Frame.TransformPoint(localRay.GetPoint(localDistance)));
            return true;
        }
    }
    private sealed record PressureBar(Station Station, Image Fill, float FullScale);
    private sealed record PanelValve(Station Station, Transform Lever, StationButton Open, StationButton Close,
        StationButton Repair, Image OpenLamp, Image ClosedLamp, TextMeshProUGUI Connection);
    private sealed record ContentsGauge(Transform Fuel, Transform Air, TextMeshProUGUI FuelReadout, TextMeshProUGUI AirReadout);
}
