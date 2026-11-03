// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;

namespace Modstoke.Client;

internal sealed partial class BoxStationModel : IDisposable
{
    private const int ChunkSize = 8;
    private const int DoorSlatCount = 6;
    private const float DoorHeight = BoxStationLayout.DoorHeight, DoorSlatHeight = .5f, DoorSlatStep = .018f;
    private readonly BoxStationLayout _layout;
    private readonly Dictionary<string, SpacecraftMeshData> _parts = new(StringComparer.Ordinal);
    private readonly List<Mesh> _meshes = new();
    private readonly List<Texture2D> _textures = new();
    private readonly List<Chunk> _chunks = new();
    private readonly List<BoxCollider?> _solids = new();
    private readonly List<int> _outsideSolids = new();
    private readonly List<int>?[] _cellSolids;
    private Transform[] _doorFrames;
    private List<Transform>[] _doorGates;
    private List<DoorLeaf>[] _leaves;
    private List<int>[] _doorSolids;
    private float[] _doorAmounts;
    private DoorMeshes _doorMeshes, _distantDoorMeshes;
    private ImmutableArray<BoxStationDoor> _doorDefinitions;
    private ImmutableArray<int> _builtDoors = ImmutableArray<int>.Empty;
    private long _doorRevision;
    private GameObject? _root;
    private Rigidbody? _body;
    private Material? _surface, _airlock, _glass, _tintedGlass, _plasmaGlass, _steelWall, _reinforcedWall;
    private PhysicMaterial? _contact;
    private int[] _visited = Array.Empty<int>();
    private int _query;
    private IEnumerator<bool>? _preparation;
    private int _preparationFrame = -1;
    private bool _prepared;
    private bool _detailIncomplete;
    private (BoxStationHullState Hull, BoxStationState Station, BoxStationPortableState Portables)? _preparationState;
    private bool _disposed;

    internal GameObject Root => _root ?? throw new ObjectDisposedException(nameof(BoxStationModel));
    internal BoxStationLayout Layout => _layout;
    internal ImmutableArray<BoxStationDoor> DoorDefinitions => _doorDefinitions;
    internal long DoorRevision => _doorRevision;
    internal event Action? DoorsChanging;
    internal bool Visible => _root != null && _root && _root.activeSelf;
    internal bool NeedsPreparationState { get; private set; }
    internal BoxStationCellFlags CellFlags(int cell) => _hull.EffectiveFlags(cell);

    // The service keeps this model private until preparation and coherent state application finish.
    internal BoxStationModel(BoxStationLayout layout)
    {
        _layout = layout;
        _doorDefinitions = layout.Doors;
        _hull = new BoxStationHull(layout);
        _hullRows = new List<int>?[layout.Height * 2];
        _wallRows = new List<int>?[layout.Height];
        _cellSolids = new List<int>?[layout.Cells.Length];
        _doorFrames = new Transform[layout.Doors.Length];
        _doorGates = new List<Transform>[layout.Doors.Length];
        _leaves = new List<DoorLeaf>[layout.Doors.Length];
        _doorSolids = new List<int>[layout.Doors.Length];
        _doorAmounts = new float[layout.Doors.Length];
        _preparation = Prepare().GetEnumerator();
    }

    // Unity work stays on the caller's main thread. True yields end the frame after a large native operation.
    internal bool PrepareStep()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_prepared) return true;
        if (_preparationFrame == Time.frameCount) return false;
        _preparationFrame = Time.frameCount;
        long start = Stopwatch.GetTimestamp();
        for (int step = 0; step < 32; step++)
        {
            if (!_preparation!.MoveNext())
            {
                _preparation.Dispose();
                _preparation = null;
                _prepared = true;
                return true;
            }
            if (_preparation.Current || Stopwatch.GetElapsedTime(start).TotalMilliseconds >= 3) break;
        }
        return false;
    }

    internal void SetPreparationState(BoxStationHullState hull, BoxStationState station, BoxStationPortableState portables)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!NeedsPreparationState || _preparationState.HasValue)
            throw new InvalidOperationException("The station is not waiting for its initial state.");
        _preparationState = (hull, station, portables);
        NeedsPreparationState = false;
    }

    private IEnumerable<bool> Prepare()
    {
        foreach (bool endFrame in LoadParts()) yield return endFrame;
        foreach (bool endFrame in LoadMaterials()) yield return endFrame;
        while (_pendingTextures.Count > 0)
            foreach (bool endFrame in PrepareTexture(_pendingTextures.Dequeue())) yield return endFrame;
        _contact = new PhysicMaterial("Station deck")
        {
            dynamicFriction = .65f, staticFriction = .8f, bounciness = 0,
            frictionCombine = PhysicMaterialCombine.Maximum,
            bounceCombine = PhysicMaterialCombine.Minimum
        };
        _root = new GameObject("Box Station");
        _root.SetActive(false);
        UnityEngine.Object.DontDestroyOnLoad(_root);
        _body = _root.AddComponent<Rigidbody>();
        _body.isKinematic = true;
        _body.useGravity = false;
        _body.interpolation = RigidbodyInterpolation.Interpolate;
        _body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        yield return false;
        foreach (bool endFrame in BuildMachinery()) yield return endFrame;
        foreach (bool endFrame in BuildPlumbingMachinery()) yield return endFrame;
        foreach (bool endFrame in BuildAirAlarmMachinery()) yield return endFrame;
        foreach (bool endFrame in BuildFireAlarmMachinery()) yield return endFrame;
        foreach (bool endFrame in BuildRadiatorMachinery()) yield return endFrame;
        foreach (bool endFrame in BuildRoomThermalMachinery()) yield return endFrame;
        foreach (bool endFrame in BuildFabricationMachinery()) yield return endFrame;
        foreach (bool endFrame in BuildFirefightingMachinery()) yield return endFrame;
        foreach (bool endFrame in BuildFuelTanks()) yield return endFrame;
        foreach (bool endFrame in BuildStorage()) yield return endFrame;
        foreach (bool endFrame in BuildFoamMachinery()) yield return endFrame;
        foreach (bool endFrame in BuildHullRepair()) yield return endFrame;
        foreach (bool endFrame in BuildMaterials()) yield return endFrame;
        foreach (bool endFrame in BuildConstruction()) yield return endFrame;
        foreach (bool endFrame in BuildEngineering()) yield return endFrame;
        foreach (bool endFrame in BuildFloors()) yield return endFrame;
        foreach (bool endFrame in BuildCables()) yield return endFrame;
        NeedsPreparationState = true;
        while (!_preparationState.HasValue) yield return true;
        var (hull, station, portables) = _preparationState.Value;
        // Build the initial saved geometry directly, before any chunks or deck rows exist.
        if (!_hull.TryApply(hull))
            throw new InvalidOperationException("Station hull projection rejected its initial damage state.");
        yield return false;
        if (!UpdateCables(station.Cables))
            throw new InvalidOperationException("Station cable projection rejected its initial state.");
        yield return false;
        if (station.HullRevision != hull.Revision || portables.Materials.HullRevision != hull.Revision
            || portables.Materials.CableRevision != station.Cables.Revision
            || portables.Materials.DoorConstruction.Revision != station.DoorRevision
            || !BoxStationDoors.CompleteEdges(portables.Materials.DoorConstruction).AsSpan().SequenceEqual(station.BuiltDoors.AsSpan())
            || !BoxStationSystems.ValidPortables(_layout, portables)
            || !BoxStationHullRepair.ValidMaterials(_layout, portables.Materials, hull, station.Cables)
            || !UpdateFloors(portables.Materials.Floors))
            throw new InvalidOperationException("Station construction projection rejected its initial state.");
        yield return false;
        for (int row = 0; row < _layout.Height; row += ChunkSize)
            for (int x = 0; x < _layout.Width; x += ChunkSize)
            {
                var chunk = new Chunk(x, row);
                chunk.Coarse = BuildChunk(chunk, true);
                if (chunk.Coarse != null) _chunks.Add(chunk);
                yield return chunk.Coarse != null;
            }
        foreach (bool endFrame in BuildCollision()) yield return endFrame;
        foreach (bool endFrame in BuildDoors()) yield return endFrame;
        if (!ValidDoorProjection(station))
            throw new InvalidOperationException("Station door projection rejected its initial state.");
        foreach (bool endFrame in ReplaceDoors(station)) yield return endFrame;
        for (int door = 0; door < station.Doors.Length; door++)
        {
            SetDoor(door, station.Doors[door].Amount);
            yield return false;
        }
        foreach (bool endFrame in BuildDockDeck()) yield return endFrame;
        foreach (bool endFrame in BuildMachineryCollision()) yield return endFrame;
        foreach (bool endFrame in ApplyConstruction(portables.Materials)) yield return endFrame;
        foreach (bool endFrame in ApplyDoorConstruction(portables.Materials.DoorConstruction)) yield return endFrame;
        UpdatePortables(portables);
        if (MaterialState != portables.Materials || !ConstructionReady)
            throw new InvalidOperationException("Station construction projection rejected its initial material state.");
        _preparationState = null;
    }

    internal void SetVisible(bool visible)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (visible && (!_prepared || !ConstructionReady))
            throw new InvalidOperationException("The station projection is not prepared.");
        if (Visible != visible) Root.SetActive(visible);
    }

    internal void SetPose(Vector3 position, Quaternion rotation, bool teleport)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Finite(position) || !float.IsFinite(rotation.x) || !float.IsFinite(rotation.y)
            || !float.IsFinite(rotation.z) || !float.IsFinite(rotation.w)
            || Quaternion.Dot(rotation, rotation) is < .5f or > 1.5f)
            throw new ArgumentException("The station projection is invalid.");
        rotation = rotation.normalized;
        if (!teleport)
        {
            _body!.MovePosition(position);
            _body.MoveRotation(rotation);
            return;
        }
        var interpolation = _body!.interpolation;
        _body.interpolation = RigidbodyInterpolation.None;
        try
        {
            _body.position = position;
            _body.rotation = rotation;
            Root.transform.SetPositionAndRotation(position, rotation);
        }
        finally { _body.interpolation = interpolation; }
    }

    // Nine nearby chunks retain detailed panels. Build at most one per frame;
    // distant hulls retain their low-detail silhouette without allocating a full high-detail station.
    internal void UpdateDetail(Vector3 worldEye)
    {
        if (!Visible || !Finite(worldEye)) return;
        try
        {
            UpdateDetailCore(worldEye);
            _detailIncomplete = false;
        }
        catch
        {
            // A failed mesh group can contain mixed LODs, even if the eye returns to its previous position.
            _detailIncomplete = true;
            throw;
        }
    }

    private void UpdateDetailCore(Vector3 worldEye)
    {
        var localEye = Root.transform.InverseTransformPoint(worldEye);
        UpdateRoomThermalDetail(localEye);
        UpdateFabricationDetail(localEye);
        UpdateFireAlarmDetail(localEye);
        UpdateFirefightingDetail(localEye);
        UpdateFuelTankDetail(localEye);
        UpdateStorageDetail(localEye);
        UpdateFoamDetail(localEye);
        UpdateHullRepairDetail(localEye);
        UpdateMaterialsDetail(localEye);
        UpdateEngineeringDetail(localEye);
        int cell = MathF.Abs(localEye.y - 1.6f) <= 32 ? _layout.CellAt(localEye) : -1;
        int focusX = cell < 0 ? -100 : cell % _layout.Width / ChunkSize;
        int focusRow = cell < 0 ? -100 : cell / _layout.Width / ChunkSize;
        Chunk? pending = null;
        int distance = int.MaxValue;
        foreach (var chunk in _chunks)
        {
            if (chunk.Coarse == null) continue;
            int dx = Math.Abs(chunk.X / ChunkSize - focusX), dr = Math.Abs(chunk.Row / ChunkSize - focusRow);
            if (dx <= 1 && dr <= 1)
            {
                if (chunk.Detail != null && _detailIncomplete) chunk.Coarse.Node.SetActive(false);
                if (chunk.Detail == null && dx + dr < distance) { pending = chunk; distance = dx + dr; }
                continue;
            }
            if (chunk.Detail == null) continue;
            if (chunk.Coarse != null) chunk.Coarse.Node.SetActive(true);
            ReleaseBatch(chunk.Detail);
            chunk.Detail = null;
        }
        if (pending == null) return;
        pending.Detail = BuildChunk(pending, false);
        if (pending.Detail != null && pending.Coarse != null) pending.Coarse.Node.SetActive(false);
    }

    internal Transform DoorFrame(int door)
    {
        if ((uint)door >= _doorFrames.Length) throw new ArgumentOutOfRangeException(nameof(door));
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _doorFrames[door];
    }

    internal void SetDoor(int door, float amount)
    {
        if ((uint)door >= _leaves.Length) throw new ArgumentOutOfRangeException(nameof(door));
        if (!float.IsFinite(amount) || amount is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(amount));
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_doorAmounts[door] == amount) return;
        _doorAmounts[door] = float.NaN;
        foreach (var leaf in _leaves[door]) SetDoorPose(leaf, amount);
        _doorAmounts[door] = amount;
    }

    private static void SetDoorPose(DoorLeaf leaf, float amount)
    {
        float bottom = DoorHeight * amount;
        float pitch = DoorHeight / DoorSlatCount * (1 - amount);
        for (int i = 0; i < leaf.Slats.Length; i++)
            leaf.Slats[i].localPosition = new(0, bottom + i * pitch, (i - (DoorSlatCount - 1) * .5f) * DoorSlatStep);
        if (amount < 1)
        {
            float height = DoorHeight - bottom;
            leaf.Blocker.center = new(0, bottom + height * .5f, 0);
            leaf.Blocker.size = new(BoxStationLayout.DoorWidth, height, .12f);
            leaf.Blocker.enabled = true;
        }
        else leaf.Blocker.enabled = false;
        float visibleHeight = (DoorSlatCount - 1) * pitch + DoorSlatHeight;
        leaf.Lod.localReferencePoint = new(0, bottom + visibleHeight * .5f, 0);
        leaf.Lod.size = MathF.Max(BoxStationLayout.DoorWidth, visibleHeight);
    }

    internal float DoorAmount(int door)
    {
        if ((uint)door >= _doorAmounts.Length) throw new ArgumentOutOfRangeException(nameof(door));
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _doorAmounts[door];
    }

    private Batch? BuildChunk(Chunk chunk, bool coarse, BoxStationHull? hull = null, bool active = true,
        BoxStationCableState? cableState = null, BoxStationFloorState? floorState = null)
    {
        hull ??= _hull;
        var opaque = new MeshBuilder();
        var steelWalls = new MeshBuilder();
        var reinforcedWalls = new MeshBuilder();
        var breaches = new MeshBuilder();
        var foam = new MeshBuilder();
        var panes = new MeshBuilder();
        var tinted = new MeshBuilder();
        var plasma = new MeshBuilder();
        var thermal = new MeshBuilder();
        var radiators = new MeshBuilder();
        var cables = new MeshBuilder();
        var cableHousings = new MeshBuilder();
        var deckHardware = new MeshBuilder();
        var floorBases = new MeshBuilder();
        var floorCovers = new MeshBuilder();
        var floorInsets = new MeshBuilder();
        var lamps = new Dictionary<int, MeshBuilder>();
        string suffix = coarse ? "-lod" : "";
        for (int row = chunk.Row; row < Math.Min(chunk.Row + ChunkSize, _layout.Height); row++)
            for (int x = chunk.X; x < Math.Min(chunk.X + ChunkSize, _layout.Width); x++)
            {
                var cell = _layout.GetCell(x, row);
                var p = _layout.CellPosition(cell.Id);
                var flags = EffectiveFlags(cell, hull);
                if ((flags & BoxStationCellFlags.Wall) != 0)
                {
                    var kind = hull.WallKind(BoxStationHull.WallTarget(_layout, cell.Id));
                    if (kind is BoxStationWallKind.Steel or BoxStationWallKind.Reinforced)
                    {
                        var walls = kind == BoxStationWallKind.Reinforced ? reinforcedWalls : steelWalls;
                        string wallPart = kind == BoxStationWallKind.Reinforced ? "wall-reinforced" : "wall-steel";
                        var face = _parts[wallPart + "-face" + suffix];
                        for (int direction = 0; direction < 4; direction++)
                        {
                            int neighbor = Neighbor(cell.Id, direction);
                            if (neighbor >= 0 && (hull.EffectiveFlags(neighbor) & BoxStationCellFlags.Wall) != 0) continue;
                            walls.Add(face, p, Turn(direction));
                        }
                        walls.Add(_parts[wallPart + "-cap" + suffix], p, Quaternion.identity);
                    }
                    else opaque.Add(_parts["wall" + suffix], p, Quaternion.identity);
                    continue;
                }
                if ((flags & BoxStationCellFlags.Floor) != 0)
                {
                    bool breached = hull.Breached(cell.Id, false), sealedWithFoam = hull.Foam(cell.Id, false);
                    if (breached || sealedWithFoam)
                        breaches.Add(_parts["hull-breach" + suffix], p, Quaternion.identity);
                    else if (_floorCoverings[cell.Id])
                        AddFloor(cell.Id, floorState ?? _floorState, suffix, floorBases, floorCovers, floorInsets, deckHardware);
                    else opaque.Add(_parts["floor" + suffix], p, Quaternion.identity);
                    if (sealedWithFoam && !breached)
                        foam.Add(_parts["foam-patch" + suffix], p, Quaternion.identity);
                }
                else if ((flags & BoxStationCellFlags.Catwalk) != 0)
                    opaque.Add(_parts["catwalk" + suffix], p, Quaternion.identity);
                else if ((flags & BoxStationCellFlags.Lattice) != 0)
                    opaque.Add(_parts["lattice" + suffix], p, Quaternion.identity);
                if ((flags & BoxStationCellFlags.Roof) != 0)
                {
                    bool breached = hull.Breached(cell.Id, true), sealedWithFoam = hull.Foam(cell.Id, true);
                    if (breached || sealedWithFoam)
                        breaches.Add(_parts["hull-breach" + suffix], p + Vector3.up * BoxStationLayout.DeckHeight,
                            Quaternion.Euler(180, 0, 0));
                    else opaque.Add(_parts["ceiling" + suffix], p, Quaternion.identity);
                    if (sealedWithFoam && !breached)
                        foam.Add(_parts["foam-patch" + suffix], p + Vector3.up * BoxStationLayout.DeckHeight,
                            Quaternion.Euler(180, 0, 0));
                }
                AddStructures(cell.Id, hull, suffix, opaque, panes, tinted, plasma);
                AddCable(cell.Id, hull, cableState ?? _cableState, floorState ?? _floorState, suffix, cables, cableHousings, deckHardware);
            }
        AddMachinery(chunk, coarse, opaque, thermal, radiators, lamps);
        if (opaque.Count == 0 && steelWalls.Count == 0 && reinforcedWalls.Count == 0 && breaches.Count == 0
            && panes.Count == 0 && tinted.Count == 0 && plasma.Count == 0
            && thermal.Count == 0 && radiators.Count == 0 && cables.Count == 0 && floorBases.Count == 0 && floorCovers.Count == 0
            && cableHousings.Count == 0 && deckHardware.Count == 0 && floorInsets.Count == 0
            && lamps.Count == 0) return null;
        var node = Node("Deck " + chunk.X + "/" + chunk.Row + (coarse ? " distant" : " detail"), Root.transform);
        node.gameObject.SetActive(active);
        var batch = new Batch(node.gameObject);
        try
        {
            AddMesh(node, opaque, _surface!, batch);
            if (steelWalls.Count > 0) AddMesh(Node("Steel walls", node), steelWalls, _steelWall!, batch);
            if (reinforcedWalls.Count > 0) AddMesh(Node("Reinforced walls", node), reinforcedWalls, _reinforcedWall!, batch);
            if (breaches.Count > 0) AddMesh(Node("Hull ruptures", node), breaches, _hullBreach!, batch);
            if (foam.Count > 0) AddMesh(Node("Hardened hull foam", node), foam, _foamPatch!, batch);
            AddMesh(Node("Glazing", node), panes, _glass!, batch);
            if (tinted.Count > 0) AddMesh(Node("Smoked glazing", node), tinted, _tintedGlass!, batch);
            if (plasma.Count > 0) AddMesh(Node("Plasma glazing", node), plasma, _plasmaGlass!, batch);
            if (thermal.Count > 0) AddMesh(Node("Thermal machinery", node), thermal, _thermal!, batch);
            if (radiators.Count > 0) AddMesh(Node("Passive radiators", node), radiators, _radiator!, batch);
            if (cables.Count > 0) AddMesh(Node("Exposed power cables", node), cables, _cableMaterial!, batch);
            if (cableHousings.Count > 0) AddMesh(Node("Cable junction housings", node), cableHousings, _cableHousingMaterial!, batch);
            if (deckHardware.Count > 0) AddMesh(Node("Deck fasteners and glands", node), deckHardware, _deckHardwareMaterial!, batch);
            if (floorBases.Count > 0) AddMesh(Node("Deck maintenance substrate", node), floorBases, _floorBaseMaterial!, batch);
            if (floorCovers.Count > 0) AddMesh(Node("Metal floor coverings", node), floorCovers, _floorCoverMaterial!, batch);
            if (floorInsets.Count > 0) AddMesh(Node("Deck grip inserts", node), floorInsets, _floorInsetMaterial!, batch);
            foreach (var lamp in lamps)
            {
                var fixture = Node("Area " + lamp.Key + " lighting", node);
                AddMesh(fixture, lamp.Value, _surface!, batch);
                var renderer = fixture.GetComponent<MeshRenderer>();
                batch.Lamps.Add(new(renderer, lamp.Key));
                SetLampEmission(renderer, lamp.Key);
            }
            return batch;
        }
        catch { ReleaseBatch(batch); throw; }
    }

    private IEnumerable<bool> BuildDoors()
    {
        var detail = _doorMeshes = new DoorMeshes(CreateMesh(_parts["airlock-frame"]), CreateMesh(_parts["airlock-leaf"]),
            CreateMesh(_parts["airlock-slat"]), CreateMesh(_parts["airlock-window-slat"]), CreateMesh(_parts["airlock-window-pane"]));
        var coarse = _distantDoorMeshes = new DoorMeshes(CreateMesh(_parts["airlock-frame-lod"]), CreateMesh(_parts["airlock-leaf-lod"]),
            CreateMesh(_parts["airlock-slat-lod"]), CreateMesh(_parts["airlock-window-slat-lod"]), CreateMesh(_parts["airlock-window-pane-lod"]));
        yield return true;
        var layers = new Dictionary<int, int>();
        foreach (var door in _layout.Doors)
        {
            var position = _layout.CellPosition(door.Cell);
            int direction = DoorDirection(door);
            _doorGates[door.Id] = new List<Transform>(4);
            _leaves[door.Id] = new List<DoorLeaf>(4);
            _doorSolids[door.Id] = new List<int>(16);
            int layer = 0;
            if (!door.Edge)
            {
                layers.TryGetValue(door.Cell, out layer);
                layers[door.Cell] = layer + 1;
            }
            bool junction = !door.Edge && (door.Ports.Length > 2 || door.Ports.Length == 2
                && PortDirection(door.Ports[0].Direction) % 2 != PortDirection(door.Ports[1].Direction) % 2);
            if (junction)
            {
                // A full-cell source door seals every port. Corner and junction
                // cells therefore need gates at each boundary, not a single centre leaf.
                for (int pass = 0; pass < 2; pass++)
                    foreach (var port in door.Ports)
                    {
                        int portDirection = PortDirection(port.Direction);
                        if ((portDirection == direction) != (pass == 0)) continue;
                        var rotation = Turn(portDirection);
                        BuildDoorGate(door.Id, door, position + rotation * new Vector3(0, 0, 1.2f - layer * .34f),
                            rotation, detail, coarse);
                        yield return false;
                    }
            }
            else
            {
                var rotation = Turn(direction);
                position += rotation * new Vector3(0, 0, door.Edge ? 1.2f : -layer * .34f);
                BuildDoorGate(door.Id, door, position, rotation, detail, coarse);
                yield return false;
            }
            SetDoor(door.Id, door.InitiallyOpen ? 1 : 0);
        }
    }

    private void BuildDoorGate(int index, BoxStationDoor door, Vector3 position, Quaternion rotation,
        DoorMeshes meshes, DoorMeshes distantMeshes)
    {
        bool first = _doorGates[index].Count == 0;
        BuildDoorGate(door, position, rotation, meshes, distantMeshes,
            _doorGates[index], _leaves[index], _doorSolids[index], _doorConstructionSolids, true);
        if (first) _doorFrames[index] = _doorGates[index][0];
    }

    private GameObject BuildDoorGate(BoxStationDoor door, Vector3 position, Quaternion rotation,
        DoorMeshes meshes, DoorMeshes distantMeshes, List<Transform> gates, List<DoorLeaf> leaves,
        List<int> solids, Dictionary<int, (int Index, int Target)> constructionSolids, bool active)
    {
        var node = CreateDoorNode(door.Name.Length > 0 ? door.Name : "Door " + door.Id, active);
        int firstSolid = solids.Count;
        try
        {
            var frame = node.transform;
            gates.Add(frame);
            frame.localPosition = position;
            frame.localRotation = rotation;
            RenderDoorPart(frame, meshes.Frame, distantMeshes.Frame, _airlock!);
            AddDoorSolid(solids, constructionSolids, door, frame, new(-1.12f, 1.6f, 0), new(.16f, 3.2f, .3f));
            AddDoorSolid(solids, constructionSolids, door, frame, new(1.12f, 1.6f, 0), new(.16f, 3.2f, .3f));
            AddDoorSolid(solids, constructionSolids, door, frame, new(0, 2.925f, 0), new(BoxStationLayout.DoorWidth, .55f, .3f));
            var leaf = Node("Leaf", frame);
            var slats = new Transform[DoorSlatCount];
            int rendererCount = DoorSlatCount + (door.Transparent ? DoorSlatCount - 1 : 0);
            var detail = new Renderer[rendererCount];
            var coarse = new Renderer[rendererCount];
            var slatMesh = door.Transparent ? meshes.Window : meshes.Slat;
            var distantSlatMesh = door.Transparent ? distantMeshes.Window : distantMeshes.Slat;
            for (int i = 0; i < slats.Length; i++)
            {
                var slat = slats[i] = Node("Slat " + i, leaf);
                detail[i] = Render(slat, i == 0 ? meshes.Leaf : slatMesh, _airlock!);
                coarse[i] = Render(Node("Distant surface", slat), i == 0 ? distantMeshes.Leaf : distantSlatMesh, _airlock!);
                if (door.Transparent && i > 0)
                {
                    int pane = DoorSlatCount + i - 1;
                    detail[pane] = Render(Node("Glazing", slat), meshes.Pane, _glass!);
                    coarse[pane] = Render(Node("Distant glazing", slat), distantMeshes.Pane, _glass!);
                }
            }
            var blocker = AddDoorSolid(solids, constructionSolids, door, leaf,
                new(0, DoorHeight * .5f, 0), new(BoxStationLayout.DoorWidth, DoorHeight, .12f));
            var mechanism = new DoorLeaf(leaf, slats, blocker, AddDoorLod(leaf, detail, coarse));
            SetDoorPose(mechanism, 0);
            leaves.Add(mechanism);
            return node;
        }
        catch
        {
            for (int index = firstSolid; index < solids.Count; index++) QueueSolidRetirement(solids[index]);
            QueueNodeRetirement(node);
            FinishGeometryRetirement();
            throw;
        }
    }

    private void RenderDoorPart(Transform node, Mesh detailMesh, Mesh coarseMesh, Material material)
    {
        var detail = Render(node, detailMesh, material);
        var coarse = Render(Node("Distant surface", node), coarseMesh, material);
        var group = AddDoorLod(node, new Renderer[] { detail }, new Renderer[] { coarse });
        // The frame's children also contain the moving leaf and controls; only this part defines its LOD bounds.
        var bounds = detailMesh.bounds;
        bounds.Encapsulate(coarseMesh.bounds);
        group.localReferencePoint = bounds.center;
        group.size = MathF.Max(bounds.size.x, MathF.Max(bounds.size.y, bounds.size.z));
    }

    private static LODGroup AddDoorLod(Transform node, Renderer[] detail, Renderer[] coarse)
    {
        var group = node.gameObject.AddComponent<LODGroup>();
        var levels = new Il2CppReferenceArray<LOD>(2);
        levels[0] = new LOD(.12f, new Il2CppReferenceArray<Renderer>(detail));
        levels[1] = new LOD(0, new Il2CppReferenceArray<Renderer>(coarse));
        group.SetLODs(levels);
        group.fadeMode = LODFadeMode.None;
        return group;
    }

    private static Transform Node(string name, Transform parent)
    {
        var node = new GameObject(name);
        try
        {
            node.layer = parent.gameObject.layer;
            node.transform.SetParent(parent, false);
            return node.transform;
        }
        catch { UnityEngine.Object.Destroy(node); throw; }
    }

    private MeshRenderer Render(Transform node, Mesh mesh, Material material)
    {
        node.gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = node.gameObject.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = material == _glass || material == _tintedGlass || material == _plasmaGlass
            ? ShadowCastingMode.Off : ShadowCastingMode.On;
        renderer.receiveShadows = true;
        return renderer;
    }

    private void AddMesh(Transform node, MeshBuilder builder, Material material, Batch batch)
    {
        if (builder.Count == 0) return;
        var mesh = builder.Create(node.name);
        batch.Meshes.Add(mesh);
        Render(node, mesh, material);
    }

    private Mesh CreateMesh(SpacecraftMeshData part)
    {
        var builder = new MeshBuilder();
        builder.Add(part, Vector3.zero, Quaternion.identity);
        var mesh = builder.Create(part.Name);
        _meshes.Add(mesh);
        return mesh;
    }

    private static Quaternion Turn(int direction) => Quaternion.Euler(0, direction * 90, 0);
    private static bool Finite(Vector3 p) => float.IsFinite(p.x) && float.IsFinite(p.y) && float.IsFinite(p.z);
    private BoxStationBarrier Barrier(BoxStationCell cell, int direction) => _hull.EffectiveBarrier(cell.Id, direction);

    private int Neighbor(int cell, int direction)
    {
        int x = cell % _layout.Width, row = cell / _layout.Width;
        return direction switch
        {
            0 => row > 0 ? cell - _layout.Width : -1,
            1 => x + 1 < _layout.Width ? cell + 1 : -1,
            2 => row + 1 < _layout.Height ? cell + _layout.Width : -1,
            _ => x > 0 ? cell - 1 : -1
        };
    }

    private static int DoorDirection(BoxStationDoor door)
    {
        if (door.Edge) return PortDirection(door.Direction);
        int northSouth = 0, eastWest = 0;
        foreach (var port in door.Ports)
            if (port.Direction is 1 or 2) northSouth++; else eastWest++;
        return eastWest > northSouth ? 1 : 0;
    }

    private static int PortDirection(int direction) => direction switch { 1 => 0, 4 => 1, 2 => 2, 8 => 3, _ => 0 };

    private int WindowDirection(int cell)
    {
        int northSouth = 0, eastWest = 0;
        for (int direction = 0; direction < 4; direction++)
        {
            int other = Neighbor(cell, direction);
            if (other < 0) continue;
            var flags = _layout.GetCell(other).Flags;
            if ((flags & BoxStationCellFlags.Floor) == 0
                || (flags & (BoxStationCellFlags.Wall | BoxStationCellFlags.FullWindow | BoxStationCellFlags.Grille)) != 0) continue;
            if (direction % 2 == 0) northSouth++; else eastWest++;
        }
        return eastWest > northSouth ? 1 : 0;
    }

    private static void ReleaseBatch(Batch batch)
    {
        if (batch.Node) { batch.Node.SetActive(false); UnityEngine.Object.Destroy(batch.Node); }
        foreach (var mesh in batch.Meshes) if (mesh) UnityEngine.Object.Destroy(mesh);
        batch.Meshes.Clear();
        batch.Lamps.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _preparation?.Dispose(); }
        catch (Exception error) { Console.WriteLine("Station preparation cancellation failed: " + error); }
        _preparation = null;
        _pendingTextures.Clear();
        _preparationState = null;
        NeedsPreparationState = false;
        if (_root != null && _root) _root.SetActive(false);
        FinishGeometryRetirement();
        foreach (var chunk in _chunks)
        {
            if (chunk.Detail != null) ReleaseBatch(chunk.Detail);
            if (chunk.Coarse != null) ReleaseBatch(chunk.Coarse);
        }
        _chunks.Clear();
        ReleaseMachinery();
        ReleasePlumbingMachinery();
        ReleaseAirAlarmMachinery();
        ReleaseFireAlarmMachinery();
        ReleaseRoomThermalMachinery();
        ReleaseFabricationMachinery();
        ReleaseFirefightingMachinery();
        ReleaseFuelTanks();
        ReleaseStorage();
        ReleaseFoamMachinery();
        ReleaseHullRepair();
        ReleaseDoorConstruction();
        ReleaseConstruction();
        ReleaseMaterials();
        ReleaseEngineering();
        ReleaseCables();
        ReleaseFloors();
        if (_root != null && _root) UnityEngine.Object.Destroy(_root);
        _root = null;
        _body = null;
        foreach (var mesh in _meshes) if (mesh) UnityEngine.Object.Destroy(mesh);
        foreach (var texture in _textures) if (texture) UnityEngine.Object.Destroy(texture);
        if (_surface) UnityEngine.Object.Destroy(_surface);
        if (_steelWall) UnityEngine.Object.Destroy(_steelWall);
        if (_reinforcedWall) UnityEngine.Object.Destroy(_reinforcedWall);
        if (_airlock) UnityEngine.Object.Destroy(_airlock);
        if (_glass) UnityEngine.Object.Destroy(_glass);
        if (_tintedGlass) UnityEngine.Object.Destroy(_tintedGlass);
        if (_plasmaGlass) UnityEngine.Object.Destroy(_plasmaGlass);
        if (_thermal) UnityEngine.Object.Destroy(_thermal);
        if (_radiator) UnityEngine.Object.Destroy(_radiator);
        if (_roomThermal) UnityEngine.Object.Destroy(_roomThermal);
        if (_hullBreach) UnityEngine.Object.Destroy(_hullBreach);
        if (_contact) UnityEngine.Object.Destroy(_contact);
        _meshes.Clear();
        _textures.Clear();
        _solids.Clear();
        _solidRegistrations.Clear();
        _freeSolids.Clear();
        _hullSolids.Clear();
        Array.Clear(_hullRows);
        _wallSolids.Clear();
        _structureSolids.Clear();
        Array.Clear(_wallRows);
        _visited = Array.Empty<int>();
        _outsideSolids.Clear();
        _dockSurfaces.Clear();
        _parts.Clear();
        Array.Clear(_cellSolids);
        Array.Clear(_leaves);
        Array.Clear(_doorGates);
        Array.Clear(_doorFrames);
        Array.Clear(_doorSolids);
        DoorsChanging = null;
    }

    private readonly record struct DoorMeshes(Mesh Frame, Mesh Leaf, Mesh Slat, Mesh Window, Mesh Pane);

    private sealed class DoorLeaf(Transform root, Transform[] slats, BoxCollider blocker, LODGroup lod)
    {
        internal readonly Transform Root = root;
        internal readonly Transform[] Slats = slats;
        internal readonly BoxCollider Blocker = blocker;
        internal readonly LODGroup Lod = lod;
    }

    private sealed class Chunk(int x, int row)
    {
        internal readonly int X = x, Row = row;
        internal Batch? Coarse, Detail;
    }

    private sealed class Batch(GameObject node)
    {
        internal readonly GameObject Node = node;
        internal readonly List<Mesh> Meshes = new(2);
        internal readonly List<LampRenderer> Lamps = new();
    }

    private sealed class MeshBuilder
    {
        private readonly List<Vector3> _vertices = new(), _normals = new();
        private readonly List<Vector2> _uv = new();
        internal int Count => _vertices.Count;

        internal void Add(SpacecraftMeshData part, Vector3 position, Quaternion rotation)
            => Add(part, position, rotation, Vector3.one);

        internal void Add(SpacecraftMeshData part, Vector3 position, Quaternion rotation, Vector3 scale)
        {
            var normalScale = new Vector3(1 / scale.x, 1 / scale.y, 1 / scale.z);
            foreach (int index in part.Triangles)
            {
                _vertices.Add(position + rotation * Vector3.Scale(part.Vertices[index], scale));
                _normals.Add(rotation * Vector3.Scale(part.Normals[index], normalScale).normalized);
                _uv.Add(part.Uv[index]);
            }
        }

        internal void Pane(Vector3 position, Quaternion rotation, float width, float height)
        {
            var right = rotation * Vector3.right * (width * .5f);
            var up = Vector3.up * (height * .5f);
            var normal = rotation * Vector3.forward;
            var a = position - right - up; var b = position + right - up;
            var c = position + right + up; var d = position - right + up;
            _vertices.Add(a); _vertices.Add(b); _vertices.Add(c);
            _vertices.Add(a); _vertices.Add(c); _vertices.Add(d);
            for (int i = 0; i < 6; i++) { _normals.Add(normal); _uv.Add(Vector2.zero); }
        }

        internal Mesh Create(string name)
        {
            var mesh = new Mesh { name = name, indexFormat = Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            try
            {
                mesh.vertices = _vertices.ToArray(); mesh.normals = _normals.ToArray(); mesh.uv = _uv.ToArray();
                var triangles = new int[Count];
                for (int i = 0; i < triangles.Length; i++) triangles[i] = i;
                mesh.SetTriangles(triangles, 0);
                mesh.RecalculateBounds(); mesh.RecalculateTangents(); mesh.UploadMeshData(true);
                return mesh;
            }
            catch { UnityEngine.Object.Destroy(mesh); throw; }
        }
    }
}
