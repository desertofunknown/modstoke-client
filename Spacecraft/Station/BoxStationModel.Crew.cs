// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Modstoke.Client;

internal static class BoxStationDock
{
    internal const int Door = 370;
    internal static readonly Vector3 Position = new(192.4f, 3.6425f, 21.6f);
    internal static readonly Quaternion Rotation = Quaternion.Euler(0, 90, 0);
}

internal sealed partial class BoxStationModel
{
    private readonly List<BoxCollider> _dockSurfaces = new(2);
    private Transform _shoreMiddle = null!, _shoreInner = null!;

    internal bool ContainsWalkArea(Vector3 worldFeet)
    {
        if (!TryWalkSurface(worldFeet, out var surface)) return false;
        float height = Vector3.Dot(worldFeet - surface, Root.transform.up);
        return height is >= -.05f and <= .7f;
    }

    internal Vector3 DoorControlPoint(int door, bool back) =>
        DoorControlPoint(door, back, 0);

    internal int DoorGateCount(int door)
    {
        DoorFrame(door);
        return _doorGates[door].Count;
    }

    internal Transform DoorGateFrame(int door, int gate)
    {
        DoorFrame(door);
        if ((uint)gate >= _doorGates[door].Count) throw new ArgumentOutOfRangeException(nameof(gate));
        return _doorGates[door][gate];
    }

    internal Vector3 DoorControlPoint(int door, bool back, int gate) =>
        DoorGateFrame(door, gate).TransformPoint(new Vector3(1.04f, 1.4f, back ? -.21f : .21f));

    internal bool DoorObstructed(int door, ReadOnlySpan<Vector3> worldFeet)
    {
        DoorFrame(door);
        if (!Visible) return true;
        // Closing sweeps the aperture; the packed panels remain above it inside the header.
        for (int gate = 0; gate < _doorGates[door].Count; gate++)
        {
            var frame = _doorGates[door][gate];
            var leaf = _leaves[door][gate];
            if (!frame || !leaf.Root || !leaf.Blocker || !leaf.Root.gameObject.activeInHierarchy) return true;
            foreach (var feet in worldFeet)
            {
                if (!Finite(feet)) return true;
                var point = frame.InverseTransformPoint(feet);
                if (!Finite(point)) return true;
                float x = Mathf.Max(0, MathF.Abs(point.x) - 1.04f);
                float z = Mathf.Max(0, MathF.Abs(point.z) - .06f);
                float low = point.y + ClientSpacecraftBody.CrewCapsuleBottom;
                float high = point.y + ClientSpacecraftBody.CrewCapsuleTop;
                float y = Mathf.Max(0, Mathf.Max(low - DoorHeight, -high));
                if (x * x + y * y + z * z <= ClientSpacecraftBody.CrewCapsuleRadius * ClientSpacecraftBody.CrewCapsuleRadius)
                    return true;
            }
        }
        return false;
    }

    private bool DockSurfaceAt(Vector3 point)
    {
        foreach (var floor in _dockSurfaces)
        {
            if (!floor || !floor.enabled || !floor.gameObject.activeInHierarchy) continue;
            var offset = point - floor.center;
            var half = floor.size * .5f;
            if (MathF.Abs(offset.x) <= half.x && MathF.Abs(offset.z) <= half.z) return true;
        }
        return false;
    }

    private IEnumerable<bool> BuildDockDeck()
    {
        // The source map ends at the escape airlock. This exterior deck is a
        // physical addition only: its source cells and atmosphere remain vacuum.
        // The existing airlock tile supplies the first 1.2 m beyond its centre.
        foreach (bool endFrame in BuildDockSection("Dock approach", 171.6f, 173.9f, 20.4f, 22.8f)) yield return endFrame;
        foreach (bool endFrame in BuildDockSection("Berthing deck", 173.9f, 206.4f, 8.6f, 34.6f)) yield return endFrame;
        BuildShorePowerCollision();
        yield return true;
    }

    private void BuildShorePowerCollision()
    {
        var pedestal = Node("Shore pedestal collision", Root.transform);
        pedestal.localPosition = SpacecraftShorePower.PedestalPosition;
        pedestal.localRotation = SpacecraftShorePower.PedestalRotation;
        AddSolid(pedestal, new(0, .037f, 0), new(.55f, .074f, .50f));
        AddSolid(pedestal, new(0, .24f, 0), new(.35f, .33f, .32f));
        AddSolid(pedestal, new(0, 1.16f, 0), new(.336f, 1.57f, .32f));
        AddSolid(pedestal, new(0, 2.02f, 0), new(.39f, .24f, .31f));
        var outer = Node("Shore boom collision", pedestal);
        outer.localPosition = new(0, SpacecraftShorePower.MastHeadHeight, 0);
        outer.localRotation = SpacecraftShorePower.ArmRotation;
        AddSolid(outer, new(0, 0, SpacecraftShorePower.OuterLength * .5f),
            new(.17f, .148f, SpacecraftShorePower.OuterLength));
        AddSolid(outer, new(0, .097f, .23f), new(.126f, .066f, .39f));
        _shoreMiddle = Node("Shore intermediate collision", outer);
        _shoreMiddle.localPosition = new(0, 0, SpacecraftShorePower.MiddleStowed);
        AddMovingShoreSolid(_shoreMiddle, new(0, 0, SpacecraftShorePower.MiddleLength * .5f),
            new(.146f, .119f, SpacecraftShorePower.MiddleLength));
        _shoreInner = Node("Shore terminal collision", _shoreMiddle);
        _shoreInner.localPosition = new(0, 0, SpacecraftShorePower.InnerStowed);
        AddMovingShoreSolid(_shoreInner, new(0, 0, SpacecraftShorePower.InnerLength * .5f),
            new(.08f, .074f, SpacecraftShorePower.InnerLength));
        AddMovingShoreSolid(_shoreInner, new(0, 0, .53f), new(.137f, .109f, .083f));
        AddMovingShoreSolid(_shoreInner, new(0, 0, SpacecraftShorePower.PlugOffset - .16f),
            new(.177f, .177f, .325f));
        AddMovingShoreSolid(_shoreInner, new(-.23f, 0, SpacecraftShorePower.PlugOffset - .14f),
            new(.075f, .14f, .28f));
        AddMovingShoreSolid(_shoreInner, new(-.13f, -.04f, SpacecraftShorePower.PlugOffset - .29f),
            new(.23f, .035f, .10f));
        var compressor = Node("Shore air compressor collision", Root.transform);
        compressor.localPosition = SpacecraftShorePower.CompressorPosition;
        compressor.localRotation = SpacecraftShorePower.CompressorRotation;
        AddSolid(compressor, new(.00375f, .443675f, -.006f), new(.5275f, .88735f, .423f));
        var fuelCabinet = Node("Shore fuel cabinet collision", Root.transform);
        fuelCabinet.localPosition = SpacecraftShorePower.FuelCabinetPosition;
        fuelCabinet.localRotation = SpacecraftShorePower.FuelCabinetRotation;
        AddSolid(fuelCabinet, new(0, .45f, 0), new(.52f, .90f, .38f));
        var coolantCabinet = Node("Shore coolant cabinet collision", Root.transform);
        coolantCabinet.localPosition = SpacecraftShorePower.CoolantCabinetPosition;
        coolantCabinet.localRotation = SpacecraftShorePower.CoolantCabinetRotation;
        AddSolid(coolantCabinet, new(0, .45f, 0), new(.52f, .90f, .38f));
    }

    private void AddMovingShoreSolid(Transform frame, Vector3 center, Vector3 size)
    {
        AddSolid(frame, center, size, true, out int index);
        var registration = _solidRegistrations[index];
        if (registration.Outside) return;
        // Sliding sections cross their rest-pose grid cells. Include them in
        // each crew query without rebuilding the spatial index every frame.
        _outsideSolids.Add(index);
        _solidRegistrations[index] = registration with { Outside = true };
    }

    internal void SetShorePowerStroke(float stroke)
    {
        if (_disposed || !_shoreMiddle || !_shoreInner) return;
        _shoreMiddle.localPosition = new(0, 0, Mathf.Lerp(SpacecraftShorePower.MiddleStowed,
            SpacecraftShorePower.MiddleExtended, stroke));
        _shoreInner.localPosition = new(0, 0, Mathf.Lerp(SpacecraftShorePower.InnerStowed,
            SpacecraftShorePower.InnerExtended, stroke));
    }

    private IEnumerable<bool> BuildDockSection(string name, float left, float right, float back, float front)
    {
        var builder = new MeshBuilder();
        float tile = BoxStationLayout.TileSize;
        for (float x = left; x < right; x += tile)
            for (float z = back; z < front; z += tile)
            {
                float width = MathF.Min(tile, right - x), depth = MathF.Min(tile, front - z);
                builder.Add(_parts["floor"], new Vector3(x + width * .5f, 0, z + depth * .5f),
                    Quaternion.identity, new Vector3(width / tile, 1, depth / tile));
                yield return false;
            }
        var mesh = builder.Create(name);
        _meshes.Add(mesh);
        Render(Node(name, Root.transform), mesh, _surface!);
        _dockSurfaces.Add(AddSolid(Root.transform, new Vector3((left + right) * .5f, -.09f, (back + front) * .5f),
            new Vector3(right - left, .18f, front - back)));
        yield return true;
    }
}
