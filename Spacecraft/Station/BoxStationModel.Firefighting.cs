// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class BoxStationModel
{
    internal static readonly Vector3 ExtinguisherCarryGrip = new(0, .555f, -.020f);
    internal static readonly Vector3 ExtinguisherMuzzle = new(.087f, .425f, .120f);
    private static readonly Vector3 CabinetDoorHinge = new(-.202f, 0, .136f);
    private static readonly Vector3 ExtinguisherLeverPivot = new(0, .535f, 0);
    private static readonly Vector3 ExtinguisherPinPivot = new(-.043f, .523f, .005f);
    private Material? _firefighting;
    private Transform[] _cabinetFrames = Array.Empty<Transform>();
    private Transform[] _cabinetDoors = Array.Empty<Transform>();
    private Transform[] _waterTankFrames = Array.Empty<Transform>();
    private Transform[] _extinguisherFrames = Array.Empty<Transform>();
    private Transform[] _extinguisherLevers = Array.Empty<Transform>();
    private Transform[] _extinguisherPins = Array.Empty<Transform>();
    private MeshFilter[][] _cabinetFilters = Array.Empty<MeshFilter[]>();
    private MeshFilter[][] _waterTankFilters = Array.Empty<MeshFilter[]>();
    private MeshFilter[][] _extinguisherFilters = Array.Empty<MeshFilter[]>();
    private Mesh[,] _firefightingMeshes = new Mesh[12, 2];
    private bool[] _cabinetDetail = Array.Empty<bool>();
    private bool[] _waterTankDetail = Array.Empty<bool>();
    private bool[] _extinguisherDetail = Array.Empty<bool>();
    private bool[] _waterTankHigh = Array.Empty<bool>();
    private bool[] _extinguisherHeld = Array.Empty<bool>();
    private bool[] _extinguisherSafety = Array.Empty<bool>();
    private float[] _cabinetDoorAmounts = Array.Empty<float>();
    private float[] _extinguisherSpray = Array.Empty<float>();
    private bool _firefightingCurrent;
    private bool _cabinetPoseKnown;

    internal Transform CabinetFrame(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)index >= _cabinetFrames.Length) throw new ArgumentOutOfRangeException(nameof(index));
        return _cabinetFrames[index];
    }

    internal Transform CabinetDoorFrame(int index)
    {
        CabinetFrame(index);
        return _cabinetDoors[index];
    }

    internal Transform TankFrame(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)index >= _waterTankFrames.Length) throw new ArgumentOutOfRangeException(nameof(index));
        return _waterTankFrames[index];
    }

    internal Transform ExtinguisherFrame(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)index >= _extinguisherFrames.Length) throw new ArgumentOutOfRangeException(nameof(index));
        return _extinguisherFrames[index];
    }

    // Item controls use the resolved item index here; network held-item commands have no item target.
    internal Vector3 FirefightingControlPoint(BoxStationFirefightingControl control, int target) => control switch
    {
        BoxStationFirefightingControl.TakeCabinet or BoxStationFirefightingControl.StoreCabinet
            => CabinetFrame(target).TransformPoint(new Vector3(0, .235f, .190f)),
        BoxStationFirefightingControl.ToggleCabinet
            => CabinetDoorFrame(target).TransformPoint(new Vector3(.376f, -.018f, .047f)),
        BoxStationFirefightingControl.Refill => TankFrame(target).TransformPoint(_waterTankHigh[target]
            ? new Vector3(.50f, .48f, 1.02975f) : new Vector3(.20f, .24f, .3965f)),
        BoxStationFirefightingControl.TakeDropped or BoxStationFirefightingControl.Drop
            => ExtinguisherFrame(target).TransformPoint(ExtinguisherCarryGrip),
        BoxStationFirefightingControl.Safety => ExtinguisherFrame(target).TransformPoint(ExtinguisherPinPivot),
        BoxStationFirefightingControl.Spray => ExtinguisherFrame(target).TransformPoint(ExtinguisherMuzzle),
        _ => throw new ArgumentOutOfRangeException(nameof(control))
    };

    private IEnumerable<bool> BuildFirefightingMachinery()
    {
        string[] parts = { "extinguisher-body", "extinguisher-lever", "extinguisher-pin", "extinguisher-nozzle",
            "extinguisher-cabinet-body", "extinguisher-cabinet-door", "extinguisher-cabinet-window",
            "water-tank-body", "water-tank-valve", "water-tank-high-body", "water-tank-high-valve" };
        for (int part = 0; part < parts.Length; part++)
            for (int lod = 0; lod < 2; lod++)
            {
                _firefightingMeshes[part, lod] = CreateMesh(_parts["machine-" + parts[part] + (lod == 0 ? "" : "-lod")]);
                yield return false;
            }
        _firefightingMeshes[11, 0] = _firefightingMeshes[11, 1] = CreateMesh(_parts["machine-water-connected-valve"]);

        var cabinets = BoxStationFirefighting.GetCabinets(_layout);
        _cabinetFrames = new Transform[cabinets.Length];
        _cabinetDoors = new Transform[cabinets.Length];
        _cabinetFilters = new MeshFilter[cabinets.Length][];
        _cabinetDetail = new bool[cabinets.Length];
        _cabinetDoorAmounts = new float[cabinets.Length];
        for (int i = 0; i < cabinets.Length; i++)
        {
            var source = cabinets[i];
            FireAlarmMount(source.Object, source.Cell, source.Direction, out var position, out var rotation);
            position.y = 1.15f;
            // The source mount is a wall plane; the cabinet's back is 15 cm behind its center.
            position += rotation * new Vector3(0, 0, .150f);
            var frame = _cabinetFrames[i] = Node("Extinguisher cabinet " + i, Root.transform);
            frame.localPosition = position;
            frame.localRotation = rotation;
            var filters = _cabinetFilters[i] = new MeshFilter[3];
            filters[0] = FirefightingPart("Cabinet body", frame, 4, Vector3.zero);
            filters[1] = FirefightingPart("Cabinet door", frame, 5, CabinetDoorHinge);
            _cabinetDoors[i] = filters[1].transform;
            filters[2] = FirefightingPart("Cabinet window", _cabinetDoors[i], 6, Vector3.zero, true);
            // A hollow shell leaves the stored bottle and front contact accessible.
            AddSolid(frame, new(0, 0, -.14f), new(.42f, .78f, .02f));
            AddSolid(frame, new(-.20f, 0, 0), new(.02f, .78f, .28f));
            AddSolid(frame, new(.20f, 0, 0), new(.02f, .78f, .28f));
            AddSolid(frame, new(0, -.335f, 0), new(.38f, .03f, .28f));
            AddSolid(frame, new(0, .375f, 0), new(.38f, .03f, .28f));
            yield return false;
        }

        var tanks = BoxStationFirefighting.GetTanks(_layout);
        _waterTankFrames = new Transform[tanks.Length];
        _waterTankFilters = new MeshFilter[tanks.Length][];
        _waterTankDetail = new bool[tanks.Length];
        _waterTankHigh = new bool[tanks.Length];
        for (int i = 0; i < tanks.Length; i++)
        {
            var source = tanks[i];
            bool high = _waterTankHigh[i] = source.High;
            bool connected = SpacecraftShoreCoolant.IsSupply(source);
            var frame = _waterTankFrames[i] = Node("Water tank " + i, Root.transform);
            frame.localPosition = _layout.CellPosition(source.Cell);
            frame.localRotation = connected ? Quaternion.Euler(0, 180, 0)
                : WaterTankRotation(source.Cell, _layout.Objects[source.Object].Direction);
            // The hard-plumbed valve stays connected at every detail level.
            var filters = _waterTankFilters[i] = new MeshFilter[connected ? 1 : 2];
            filters[0] = FirefightingPart("Water tank body", frame, high ? 9 : 7, Vector3.zero);
            var valve = FirefightingPart("Water tank valve", frame, connected ? 11 : high ? 10 : 8,
                high ? new(.50f, .48f, .90f) : new(.20f, .24f, .31f));
            if (!connected) filters[1] = valve;
            // Narrow vertical bands follow the round vessel and leave its offset refill fitting clear.
            float width = high ? 2.10f : .70f, height = high ? 2.94f : .84f;
            for (int band = -2; band <= 2; band++)
            {
                float depth = width * (band == 0 ? 1 : Math.Abs(band) == 1 ? .9165f : .6f);
                AddSolid(frame, new(band * width * .2f, height * .5f, 0), new(width * .2f, height, depth));
            }
            yield return false;
        }

        int count = BoxStationFirefighting.GetItems(_layout).Length;
        _extinguisherFrames = new Transform[count];
        _extinguisherLevers = new Transform[count];
        _extinguisherPins = new Transform[count];
        _extinguisherFilters = new MeshFilter[count][];
        _extinguisherDetail = new bool[count];
        _extinguisherHeld = new bool[count];
        _extinguisherSafety = new bool[count];
        _extinguisherSpray = new float[count];
        for (int i = 0; i < count; i++)
        {
            var frame = _extinguisherFrames[i] = Node("Extinguisher " + i, Root.transform);
            var filters = _extinguisherFilters[i] = new MeshFilter[4];
            filters[0] = FirefightingPart("Extinguisher body", frame, 0, Vector3.zero);
            filters[1] = FirefightingPart("Extinguisher lever", frame, 1, ExtinguisherLeverPivot);
            filters[2] = FirefightingPart("Extinguisher pin", frame, 2, ExtinguisherPinPivot);
            filters[3] = FirefightingPart("Extinguisher nozzle", frame, 3, new(.087f, .425f, .035f));
            _extinguisherLevers[i] = filters[1].transform;
            _extinguisherPins[i] = filters[2].transform;
            // Portable items have no station-owned collider: their cell and holder can change each frame.
            frame.gameObject.SetActive(false);
            yield return false;
        }
    }

    private Quaternion WaterTankRotation(int cell, int sourceDirection)
    {
        int facing = PortDirection(sourceDirection == 0 ? 2 : sourceDirection);
        if (WaterTankApproach(cell, facing)) return Turn(facing);
        // Source tanks have no authored facing; fit their valve to an open side of the source cell.
        for (int direction = 0; direction < 4; direction++)
            if (WaterTankApproach(cell, direction)) return Turn(direction);
        return Turn(facing);
    }

    private bool WaterTankApproach(int cell, int direction)
    {
        int neighbor = Neighbor(cell, direction);
        if (neighbor < 0) return false;
        var next = _layout.GetCell(neighbor);
        const BoxStationCellFlags blocked = BoxStationCellFlags.Wall | BoxStationCellFlags.FullWindow | BoxStationCellFlags.Grille;
        const BoxStationBarrier barrier = BoxStationBarrier.Wall | BoxStationBarrier.Window
            | BoxStationBarrier.Grille | BoxStationBarrier.DoorControlled;
        return (next.Flags & (BoxStationCellFlags.Floor | BoxStationCellFlags.Catwalk)) != 0
            && (next.Flags & blocked) == 0
            && ((Barrier(_layout.GetCell(cell), direction) | Barrier(next, (direction + 2) % 4)) & barrier) == 0;
    }

    private MeshFilter FirefightingPart(string name, Transform parent, int part, Vector3 position, bool glass = false)
    {
        var node = Node(name, parent);
        node.localPosition = position;
        Render(node, _firefightingMeshes[part, 1], glass ? _glass! : _firefighting!);
        return node.GetComponent<MeshFilter>();
    }

    private void UpdateFirefighting(BoxStationFirefightingState? state)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        bool current = state is { } snapshot && snapshot.LayoutHash == _layout.LayoutHash
            && !snapshot.Items.IsDefault && snapshot.Items.Length == _extinguisherFrames.Length
            && !snapshot.CabinetOpen.IsDefault && snapshot.CabinetOpen.Length == _cabinetFrames.Length
            && !snapshot.TankWater.IsDefault && snapshot.TankWater.Length == _waterTankFrames.Length;
        _firefightingCurrent = false;
        float step = Math.Clamp(Time.unscaledDeltaTime, 0, .1f);
        if (current && _portablesCurrent)
        {
            for (int i = 0; i < _cabinetFrames.Length; i++)
            {
                float target = state!.Value.CabinetOpen[i] ? 1 : 0;
                float amount = !_cabinetPoseKnown ? target : Mathf.MoveTowards(_cabinetDoorAmounts[i], target, step * 2.5f);
                if (!_cabinetPoseKnown || amount != _cabinetDoorAmounts[i])
                    _cabinetDoors[i].localRotation = Quaternion.Euler(0, -100 * amount, 0);
                _cabinetDoorAmounts[i] = amount;
            }
            _cabinetPoseKnown = true;
        }
        for (int i = 0; i < _extinguisherFrames.Length; i++)
        {
            var frame = _extinguisherFrames[i];
            _extinguisherHeld[i] = false;
            bool visible = false;
            if (current)
            {
                var item = state!.Value.Items[i];
                _extinguisherSafety[i] = item.Safety;
                if (_extinguisherPins[i].gameObject.activeSelf != item.Safety)
                    _extinguisherPins[i].gameObject.SetActive(item.Safety);
                if (item.Place == BoxStationExtinguisherPlace.Cabinet && (uint)item.Cabinet < _cabinetFrames.Length)
                {
                    var cabinet = _cabinetFrames[item.Cabinet];
                    frame.localPosition = cabinet.localPosition + cabinet.localRotation * new Vector3(0, -.32f, 0);
                    frame.localRotation = cabinet.localRotation;
                    visible = true;
                }
                else if (item.Place == BoxStationExtinguisherPlace.Dropped)
                {
                    frame.localPosition = new(item.Position.X, 0, item.Position.Z);
                    frame.localRotation = Quaternion.identity;
                    visible = true;
                }
                else if (item.Place == BoxStationExtinguisherPlace.Held) _extinguisherHeld[i] = true;
                else if (item.Place == BoxStationExtinguisherPlace.Stored
                    && StoredPortablePose(item.Storage, item.Slot, _storedEquipmentBaseOffsets[5], out var position, out var rotation))
                {
                    frame.localPosition = position;
                    frame.localRotation = rotation;
                    visible = true;
                }
            }
            // Hands must provide a fresh world pose after this update, including for remote holders.
            if (frame.gameObject.activeSelf != visible) frame.gameObject.SetActive(visible);
            if (!current || !_extinguisherHeld[i] || _extinguisherSafety[i]) SetExtinguisherSpray(i, 0);
        }
        _firefightingCurrent = current;
    }

    internal void SetHeldExtinguisherPose(int item, Vector3 position, Quaternion rotation, bool visible)
    {
        var frame = ExtinguisherFrame(item);
        // A retiring hand pose cannot hide an item transferred to world or storage custody.
        if (!_portablesCurrent || !_firefightingCurrent || !_extinguisherHeld[item]) return;
        if (visible)
        {
            if (!Finite(position) || !float.IsFinite(rotation.x) || !float.IsFinite(rotation.y)
                || !float.IsFinite(rotation.z) || !float.IsFinite(rotation.w)
                || Quaternion.Dot(rotation, rotation) is < .5f or > 1.5f)
                throw new ArgumentException("The held extinguisher projection is invalid.");
            frame.SetPositionAndRotation(position, rotation.normalized);
        }
        if (frame.gameObject.activeSelf != visible) frame.gameObject.SetActive(visible);
    }

    // A committed burst drives this amount; cooldown alone must never hold the lever down.
    internal void SetExtinguisherSpray(int item, float amount)
    {
        ExtinguisherFrame(item);
        if (!float.IsFinite(amount) || amount is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(amount));
        if (!_portablesCurrent || !_firefightingCurrent || !_extinguisherHeld[item] || _extinguisherSafety[item]) amount = 0;
        if (_extinguisherSpray[item] == amount) return;
        _extinguisherLevers[item].localRotation = Quaternion.Euler(-10 * amount, 0, 0);
        _extinguisherSpray[item] = amount;
    }

    private void UpdateFirefightingDetail(Vector3 eye)
    {
        for (int i = 0; i < _cabinetFrames.Length; i++)
            FirefightingDetail(_cabinetFrames[i], _cabinetFilters[i], 4, eye, ref _cabinetDetail[i]);
        for (int i = 0; i < _waterTankFrames.Length; i++)
            FirefightingDetail(_waterTankFrames[i], _waterTankFilters[i], _waterTankHigh[i] ? 9 : 7, eye, ref _waterTankDetail[i]);
        for (int i = 0; i < _extinguisherFrames.Length; i++)
            if (_extinguisherFrames[i].gameObject.activeSelf)
                FirefightingDetail(_extinguisherFrames[i], _extinguisherFilters[i], 0, eye, ref _extinguisherDetail[i]);
    }

    private void FirefightingDetail(Transform frame, MeshFilter[] filters, int first, Vector3 eye, ref bool previous)
    {
        bool detail = (frame.localPosition - eye).sqrMagnitude < 20 * 20;
        if (!_detailIncomplete && previous == detail) return;
        for (int part = 0; part < filters.Length; part++)
            filters[part].sharedMesh = _firefightingMeshes[first + part, detail ? 0 : 1];
        previous = detail;
    }

    private void LoadFirefightingMaterial(Shader shader)
    {
        _firefighting = new Material(shader) { name = "Firefighting equipment finish", enableInstancing = true };
        _firefighting.SetColor("_BaseColor", Color.white);
        _firefighting.SetTexture("_BaseColorMap", LoadTexture("firefighting-base", false));
        _firefighting.SetTexture("_MaskMap", LoadTexture("firefighting-mask", true));
        _firefighting.SetTexture("_NormalMap", LoadTexture("firefighting-normal", true, true));
        _firefighting.SetFloat("_NormalMapSpace", 0); _firefighting.SetFloat("_NormalScale", 1);
        _firefighting.SetFloat("_MetallicRemapMin", 0); _firefighting.SetFloat("_MetallicRemapMax", 1);
        _firefighting.SetFloat("_SmoothnessRemapMin", 0); _firefighting.SetFloat("_SmoothnessRemapMax", 1);
        _firefighting.SetFloat("_AORemapMin", 0); _firefighting.SetFloat("_AORemapMax", 1);
        _firefighting.EnableKeyword("_MASKMAP");
        _firefighting.EnableKeyword("_NORMALMAP");
        _firefighting.EnableKeyword("_NORMALMAP_TANGENT_SPACE");
    }

    private void ReleaseFirefightingMachinery()
    {
        _cabinetFrames = _cabinetDoors = _waterTankFrames = _extinguisherFrames
            = _extinguisherLevers = _extinguisherPins = Array.Empty<Transform>();
        _cabinetFilters = _waterTankFilters = _extinguisherFilters = Array.Empty<MeshFilter[]>();
        _firefightingMeshes = new Mesh[12, 2];
        _cabinetDetail = _waterTankDetail = _extinguisherDetail = _waterTankHigh
            = _extinguisherHeld = _extinguisherSafety = Array.Empty<bool>();
        _cabinetDoorAmounts = _extinguisherSpray = Array.Empty<float>();
        _firefightingCurrent = _cabinetPoseKnown = false;
        if (_firefighting) UnityEngine.Object.Destroy(_firefighting);
        _firefighting = null;
    }
}
