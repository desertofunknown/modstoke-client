// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class BoxStationModel
{
    private Material? _fireAlarm;
    private Transform[] _fireAlarmFrames = Array.Empty<Transform>();
    private Transform[] _fireAlarmSwitches = Array.Empty<Transform>();
    private MeshFilter[][] _fireAlarmFilters = Array.Empty<MeshFilter[]>();
    private Mesh[,] _fireAlarmMeshes = new Mesh[2, 2];
    private bool[] _fireAlarmDetail = Array.Empty<bool>();

    internal Transform FireAlarmControlFrame(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)index >= _fireAlarmFrames.Length) throw new ArgumentOutOfRangeException(nameof(index));
        return _fireAlarmFrames[index];
    }

    internal Vector3 FireAlarmControlPoint(int index, BoxStationFireAlarmControl control)
        => FireAlarmControlFrame(index).TransformPoint(FireAlarmButtonCenter(control));

    internal static Vector3 FireAlarmButtonCenter(BoxStationFireAlarmControl control) => control switch
    {
        BoxStationFireAlarmControl.Raise => new(.13f, -.12f, .152f),
        BoxStationFireAlarmControl.Reset => new(-.13f, -.12f, .152f),
        BoxStationFireAlarmControl.Detection => new(-.15f, -.225f, .162f),
        _ => throw new ArgumentOutOfRangeException(nameof(control))
    };

    internal static Vector2 FireAlarmButtonSize(BoxStationFireAlarmControl control) => control switch
    {
        BoxStationFireAlarmControl.Raise => new(.18f, .08f),
        BoxStationFireAlarmControl.Reset => new(.15f, .08f),
        BoxStationFireAlarmControl.Detection => new(.10f, .06f),
        _ => throw new ArgumentOutOfRangeException(nameof(control))
    };

    internal void SetFireAlarmDetection(int index, bool detecting)
    {
        FireAlarmControlFrame(index);
        _fireAlarmSwitches[index].localPosition = new(-.15f + (detecting ? .018f : -.018f), -.225f, .145f);
    }

    private IEnumerable<bool> BuildFireAlarmMachinery()
    {
        var devices = BoxStationFireAlarms.GetDevices(_layout);
        _fireAlarmFrames = new Transform[devices.Length];
        _fireAlarmSwitches = new Transform[devices.Length];
        _fireAlarmFilters = new MeshFilter[devices.Length][];
        _fireAlarmDetail = new bool[devices.Length];
        string[] parts = { "body", "switch" };
        for (int part = 0; part < parts.Length; part++)
            for (int lod = 0; lod < 2; lod++)
            {
                _fireAlarmMeshes[part, lod] = CreateMesh(_parts["machine-firealarm-" + parts[part] + (lod == 0 ? "" : "-lod")]);
                yield return false;
            }
        for (int i = 0; i < devices.Length; i++)
        {
            var source = devices[i];
            FireAlarmMount(source.Object, source.Cell, source.Direction, out var position, out var rotation);
            var frame = Node("Fire alarm " + i, Root.transform);
            frame.localPosition = position;
            frame.localRotation = rotation;
            _fireAlarmFrames[i] = frame;
            var filters = _fireAlarmFilters[i] = new MeshFilter[parts.Length];
            for (int part = 0; part < parts.Length; part++)
            {
                var node = Node("Fire alarm " + parts[part], frame);
                Render(node, _fireAlarmMeshes[part, 1], _fireAlarm!);
                filters[part] = node.GetComponent<MeshFilter>();
            }
            _fireAlarmSwitches[i] = filters[1].transform;
            SetFireAlarmDetection(i, true);
            _machineSolids.Add(new(position, rotation, new(0, 0, .067f), new(.52f, .62f, .134f)));
            // Raised side guards protect the Raise paddle without obstructing its central contact.
            foreach (float x in new[] { .025f, .235f })
                _machineSolids.Add(new(position, rotation, new(x, -.12f, .157f), new(.012f, .12f, .046f)));
            yield return false;
        }
    }

    private void FireAlarmMount(int sourceObject, int cell, int direction, out Vector3 position, out Quaternion rotation)
    {
        string raw = _layout.Objects[sourceObject].Raw;
        int x = FireAlarmPixel(raw, "pixel_x"), y = FireAlarmPixel(raw, "pixel_y");
        int wall = x == 0 && y == 0 ? direction switch { 1 => 2, 4 => 8, 8 => 4, _ => 1 }
            : Math.Abs(x) > Math.Abs(y) ? x > 0 ? 4 : 8 : y > 0 ? 1 : 2;
        float tangent = Math.Abs(x) > Math.Abs(y) ? y * .075f : x * .075f;
        // Two authored corner offsets refer to a diagonal wall. Preserve that lateral placement
        // only when the face opens onto walkable floor; otherwise fit the source room's window edge.
        if (Math.Abs(x) > 16 && Math.Abs(y) > 16)
        {
            int side = Neighbor(cell, x > 0 ? 1 : 3);
            var flags = side < 0 ? BoxStationCellFlags.None : _layout.GetCell(side).Flags;
            if ((flags & BoxStationCellFlags.Floor) != 0
                && (flags & (BoxStationCellFlags.Wall | BoxStationCellFlags.FullWindow | BoxStationCellFlags.Grille)) == 0)
            {
                wall = y > 0 ? 1 : 2;
                tangent = x * .075f;
            }
            else tangent = Math.Clamp(tangent, -.85f, .85f);
        }
        Mount(cell, wall, 1.55f, out position, out rotation);
        position += wall is 1 or 2 ? new Vector3(tangent, 0, 0) : new Vector3(0, 0, tangent);
    }

    private static int FireAlarmPixel(string raw, string name)
    {
        var matches = Regex.Matches(raw, @"(?:^|[;{])\s*" + name + @"\s*=\s*([^;}]*)", RegexOptions.CultureInvariant);
        if (matches.Count == 0) return 0;
        if (matches.Count != 1 || !int.TryParse(matches[0].Groups[1].Value.Trim(), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int value) || value is < -64 or > 64)
            throw new InvalidDataException("The fire alarm has an invalid " + name + " placement.");
        return value;
    }

    private void UpdateFireAlarmDetail(Vector3 eye)
    {
        for (int i = 0; i < _fireAlarmFrames.Length; i++)
        {
            bool detail = (_fireAlarmFrames[i].localPosition - eye).sqrMagnitude < 20 * 20;
            if (!_detailIncomplete && _fireAlarmDetail[i] == detail) continue;
            for (int part = 0; part < 2; part++)
                _fireAlarmFilters[i][part].sharedMesh = _fireAlarmMeshes[part, detail ? 0 : 1];
            _fireAlarmDetail[i] = detail;
        }
    }

    private void LoadFireAlarmMaterial(Shader shader)
    {
        _fireAlarm = new Material(shader) { name = "Fire alarm finish", enableInstancing = true };
        _fireAlarm.SetColor("_BaseColor", Color.white);
        _fireAlarm.SetTexture("_BaseColorMap", LoadTexture("firealarm-base", false));
        _fireAlarm.SetTexture("_MaskMap", LoadTexture("firealarm-mask", true));
        _fireAlarm.SetTexture("_NormalMap", LoadTexture("firealarm-normal", true, true));
        _fireAlarm.SetFloat("_NormalMapSpace", 0); _fireAlarm.SetFloat("_NormalScale", 1);
        _fireAlarm.SetFloat("_MetallicRemapMin", 0); _fireAlarm.SetFloat("_MetallicRemapMax", 1);
        _fireAlarm.SetFloat("_SmoothnessRemapMin", 0); _fireAlarm.SetFloat("_SmoothnessRemapMax", 1);
        _fireAlarm.SetFloat("_AORemapMin", 0); _fireAlarm.SetFloat("_AORemapMax", 1);
        _fireAlarm.EnableKeyword("_MASKMAP");
        _fireAlarm.EnableKeyword("_NORMALMAP");
        _fireAlarm.EnableKeyword("_NORMALMAP_TANGENT_SPACE");
    }

    private void ReleaseFireAlarmMachinery()
    {
        _fireAlarmFrames = _fireAlarmSwitches = Array.Empty<Transform>();
        _fireAlarmFilters = Array.Empty<MeshFilter[]>();
        _fireAlarmMeshes = new Mesh[2, 2];
        _fireAlarmDetail = Array.Empty<bool>();
        if (_fireAlarm) UnityEngine.Object.Destroy(_fireAlarm);
        _fireAlarm = null;
    }
}
