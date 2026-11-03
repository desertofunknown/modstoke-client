// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class BoxStationModel
{
    private Material? _roomThermal;
    private Transform[] _roomThermalFrames = Array.Empty<Transform>();
    private Transform[] _roomThermalHatches = Array.Empty<Transform>();
    private Transform[] _roomThermalCells = Array.Empty<Transform>();
    private Transform[] _roomThermalRejection = Array.Empty<Transform>();
    private MeshFilter[][] _roomThermalFilters = Array.Empty<MeshFilter[]>();
    private Mesh[,] _roomThermalMeshes = new Mesh[4, 2];
    private bool[] _roomThermalDetail = Array.Empty<bool>();
    private byte[] _roomThermalPoses = Array.Empty<byte>();
    private float[] _roomThermalHatchAmounts = Array.Empty<float>();
    private float[] _roomThermalCellAmounts = Array.Empty<float>();
    private float[] _roomThermalRejectionAmounts = Array.Empty<float>();
    private bool _roomThermalPoseKnown;

    internal Transform RoomThermalControlFrame(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)index >= _roomThermalFrames.Length) throw new ArgumentOutOfRangeException(nameof(index));
        return _roomThermalFrames[index];
    }

    internal Vector3 RoomThermalControlPoint(int index, BoxStationRoomThermalControl control)
        => RoomThermalControlFrame(index).TransformPoint(RoomThermalButtonCenter(control));

    internal static Vector3 RoomThermalButtonCenter(BoxStationRoomThermalControl control)
    {
        int button = (int)control;
        if ((uint)button >= 8) throw new ArgumentOutOfRangeException(nameof(control));
        return new(.195f - button % 4 * .13f, button < 4 ? .710f : .635f, .281f);
    }

    private IEnumerable<bool> BuildRoomThermalMachinery()
    {
        var devices = BoxStationRoomThermal.GetDevices(_layout);
        _roomThermalFrames = new Transform[devices.Length];
        _roomThermalHatches = new Transform[devices.Length];
        _roomThermalCells = new Transform[devices.Length];
        _roomThermalRejection = new Transform[devices.Length];
        _roomThermalFilters = new MeshFilter[devices.Length][];
        _roomThermalDetail = new bool[devices.Length];
        _roomThermalPoses = new byte[devices.Length];
        _roomThermalHatchAmounts = new float[devices.Length];
        _roomThermalCellAmounts = new float[devices.Length];
        _roomThermalRejectionAmounts = new float[devices.Length];
        string[] parts = { "body", "hatch", "cell", "rejection" };
        for (int part = 0; part < parts.Length; part++)
            for (int lod = 0; lod < 2; lod++)
            {
                _roomThermalMeshes[part, lod] = CreateMesh(_parts["machine-room-thermal-" + parts[part] + (lod == 0 ? "" : "-lod")]);
                yield return false;
            }
        for (int i = 0; i < devices.Length; i++)
        {
            var source = devices[i];
            var position = _layout.CellPosition(source.Cell);
            var rotation = Turn(PortDirection(source.Direction == 0 ? 2 : source.Direction));
            var frame = Node("Room conditioner " + i, Root.transform);
            frame.localPosition = position;
            frame.localRotation = rotation;
            _roomThermalFrames[i] = frame;
            var filters = _roomThermalFilters[i] = new MeshFilter[parts.Length];
            for (int part = 0; part < parts.Length; part++)
            {
                var node = Node("Room conditioner " + parts[part], frame);
                Render(node, _roomThermalMeshes[part, 1], _roomThermal!);
                filters[part] = node.GetComponent<MeshFilter>();
            }
            _roomThermalHatches[i] = filters[1].transform;
            _roomThermalHatches[i].localPosition = new(.235f, .369f, .264f);
            _roomThermalCells[i] = filters[2].transform;
            _roomThermalCells[i].localPosition = new(0, .355f, .132f);
            _roomThermalRejection[i] = filters[3].transform;
            _roomThermalRejection[i].localPosition = new(0, .55f, -.255f);
            _machineSolids.Add(new(position, rotation, new(0, .59f, 0), new(.60f, .84f, .52f)));
            _machineSolids.Add(new(position, rotation, new(0, .085f, .015f), new(.57f, .17f, .51f)));
            _machineSolids.Add(new(position, rotation, new(-.39f, .205f, .337f), new(.26f, .06f, .166f)));
            _machineSolids.Add(new(position, rotation, new(-.285f, .15f, .27f), new(.31f, .045f, .10f)));
            _machineSolids.Add(new(position, rotation, new(0, 1.045f, -.155f), new(.40f, .065f, .13f)));
            _machineSolids.Add(new(position, rotation, new(.37f, .04f, -.26f), new(.13f, .08f, .15f)));
            // Every moving solid stays inside this 2.4 m tile (X -.49..+.39, Z -.62..+.74),
            // so its rest-pose cell membership remains valid through the complete service motion.
            AddSolid(_roomThermalHatches[i], new(-.23f, 0, .006f), new(.48f, .354f, .04f));
            AddSolid(_roomThermalCells[i], new(0, .018f, 0), new(.20f, .304f, .112f));
            AddSolid(_roomThermalRejection[i], new(0, .193f, -.016f), new(.484f, .41f, .078f));
            yield return false;
        }
    }

    internal void UpdateRoomThermal(BoxStationState? state)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        bool current = state is { } snapshot && snapshot.LayoutHash == _layout.LayoutHash
            && snapshot.RoomThermal.Length == _roomThermalFrames.Length;
        if (!current && !_roomThermalPoseKnown) return;
        bool first = !_roomThermalPoseKnown;
        float step = Math.Clamp(Time.unscaledDeltaTime, 0, .1f);
        for (int i = 0; i < _roomThermalFrames.Length; i++)
        {
            if (current)
            {
                var device = state!.Value.RoomThermal[i];
                _roomThermalPoses[i] = (byte)((device.HatchOpen ? 1 : 0) | (device.CellDocked ? 2 : 0) | (device.RejectHeat ? 4 : 0));
            }
            int pose = _roomThermalPoses[i];
            float cellTarget = (pose & 2) != 0 ? 1 : 0;
            // Rapid commands still open the hatch before extraction and finish seating before closing.
            float hatchTarget = (pose & 1) != 0 || !first && _roomThermalCellAmounts[i] != cellTarget ? 1 : 0;
            float hatch = first ? hatchTarget : Mathf.MoveTowards(_roomThermalHatchAmounts[i], hatchTarget, step * 2.5f);
            float cell = first ? cellTarget : hatch < 1 ? _roomThermalCellAmounts[i]
                : Mathf.MoveTowards(_roomThermalCellAmounts[i], cellTarget, step * 1.6f);
            float rejectionTarget = (pose & 4) != 0 ? 1 : 0;
            float rejection = first ? rejectionTarget : Mathf.MoveTowards(_roomThermalRejectionAmounts[i], rejectionTarget, step * 2.5f);
            if (first || hatch != _roomThermalHatchAmounts[i])
                _roomThermalHatches[i].localRotation = Quaternion.Euler(0, hatch * 105, 0);
            if (first || cell != _roomThermalCellAmounts[i])
                _roomThermalCells[i].localPosition = cell <= .45f
                    ? Vector3.Lerp(new(0, .355f, .132f), new(0, .355f, .36f), cell / .45f)
                    : Vector3.Lerp(new(0, .355f, .36f), new(-.39f, .355f, .36f), (cell - .45f) / .55f);
            if (first || rejection != _roomThermalRejectionAmounts[i])
                _roomThermalRejection[i].localRotation = Quaternion.Euler(rejection * -55, 0, 0);
            _roomThermalHatchAmounts[i] = hatch;
            _roomThermalCellAmounts[i] = cell;
            _roomThermalRejectionAmounts[i] = rejection;
        }
        _roomThermalPoseKnown = true;
    }

    private void UpdateRoomThermalDetail(Vector3 eye)
    {
        for (int i = 0; i < _roomThermalFrames.Length; i++)
        {
            bool detail = (_roomThermalFrames[i].localPosition - eye).sqrMagnitude < 20 * 20;
            if (!_detailIncomplete && _roomThermalDetail[i] == detail) continue;
            for (int part = 0; part < 4; part++)
                _roomThermalFilters[i][part].sharedMesh = _roomThermalMeshes[part, detail ? 0 : 1];
            _roomThermalDetail[i] = detail;
        }
    }

    private void LoadRoomThermalMaterial(Shader shader)
    {
        _roomThermal = new Material(shader) { name = "Room conditioner finish", enableInstancing = true };
        _roomThermal.SetColor("_BaseColor", Color.white);
        _roomThermal.SetTexture("_BaseColorMap", LoadTexture("room-thermal-base", false));
        _roomThermal.SetTexture("_MaskMap", LoadTexture("room-thermal-mask", true));
        _roomThermal.SetTexture("_NormalMap", LoadTexture("room-thermal-normal", true, true));
        _roomThermal.SetFloat("_NormalMapSpace", 0); _roomThermal.SetFloat("_NormalScale", 1);
        _roomThermal.SetFloat("_MetallicRemapMin", 0); _roomThermal.SetFloat("_MetallicRemapMax", 1);
        _roomThermal.SetFloat("_SmoothnessRemapMin", 0); _roomThermal.SetFloat("_SmoothnessRemapMax", 1);
        _roomThermal.SetFloat("_AORemapMin", 0); _roomThermal.SetFloat("_AORemapMax", 1);
        _roomThermal.EnableKeyword("_MASKMAP");
        _roomThermal.EnableKeyword("_NORMALMAP");
        _roomThermal.EnableKeyword("_NORMALMAP_TANGENT_SPACE");
    }

    private void ReleaseRoomThermalMachinery()
    {
        _roomThermalFrames = _roomThermalHatches = _roomThermalCells = _roomThermalRejection = Array.Empty<Transform>();
        _roomThermalFilters = Array.Empty<MeshFilter[]>();
        _roomThermalMeshes = new Mesh[4, 2];
        _roomThermalDetail = Array.Empty<bool>();
        _roomThermalPoses = Array.Empty<byte>();
        _roomThermalHatchAmounts = _roomThermalCellAmounts = _roomThermalRejectionAmounts = Array.Empty<float>();
        _roomThermalPoseKnown = false;
    }
}
