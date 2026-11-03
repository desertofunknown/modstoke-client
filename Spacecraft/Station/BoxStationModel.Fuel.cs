// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class BoxStationModel
{
    private static readonly Vector3 FuelTankValvePivot = new(0, .96f, .43f);
    private Transform[] _fuelTankFrames = Array.Empty<Transform>();
    private MeshFilter[][] _fuelTankFilters = Array.Empty<MeshFilter[]>();
    private Mesh[,] _fuelTankMeshes = new Mesh[2, 2];
    private bool[] _fuelTankDetail = Array.Empty<bool>();

    internal Transform FuelTankFrame(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)index >= _fuelTankFrames.Length) throw new ArgumentOutOfRangeException(nameof(index));
        return _fuelTankFrames[index];
    }

    internal Vector3 FuelTankPoint(int index)
        => FuelTankFrame(index).TransformPoint(new Vector3(0, .96f, .50f));

    private IEnumerable<bool> BuildFuelTanks()
    {
        string[] parts = { "machine-fuel-tank-body", "machine-fuel-tank-valve" };
        for (int part = 0; part < parts.Length; part++)
            for (int lod = 0; lod < 2; lod++)
            {
                _fuelTankMeshes[part, lod] = CreateMesh(_parts[parts[part] + (lod == 0 ? "" : "-lod")]);
                yield return false;
            }

        var tanks = BoxStationEngineering.GetFuelTanks(_layout);
        _fuelTankFrames = new Transform[tanks.Length];
        _fuelTankFilters = new MeshFilter[tanks.Length][];
        _fuelTankDetail = new bool[tanks.Length];
        for (int i = 0; i < tanks.Length; i++)
        {
            var source = tanks[i];
            var frame = _fuelTankFrames[i] = Node("Fuel tank " + i, Root.transform);
            frame.localPosition = _layout.CellPosition(source.Cell);
            frame.localRotation = WaterTankRotation(source.Cell, _layout.Objects[source.Object].Direction);
            var filters = _fuelTankFilters[i] = new MeshFilter[2];
            for (int part = 0; part < filters.Length; part++)
            {
                var node = Node(part == 0 ? "Fuel tank body" : "Fuel tank valve", frame);
                node.localPosition = part == 0 ? Vector3.zero : FuelTankValvePivot;
                Render(node, _fuelTankMeshes[part, 1], _firefighting!);
                filters[part] = node.GetComponent<MeshFilter>();
            }
            // Follow the round vessel instead of filling its empty corner space.
            for (int band = -2; band <= 2; band++)
            {
                float depth = .65f * (band == 0 ? 1 : Math.Abs(band) == 1 ? .9165f : .6f);
                AddSolid(frame, new(band * .13f, .625f, -.035f), new(.13f, .93f, depth));
            }
            AddSolid(frame, new(-.285f, .048f, -.02f), new(.15f, .096f, .72f));
            AddSolid(frame, new(.285f, .048f, -.02f), new(.15f, .096f, .72f));
            // End behind the outlet so reach checks can trace to its front contact.
            AddSolid(frame, new(0, .96f, .354f), new(.162f, .162f, .211f));
            yield return false;
        }
    }

    private void UpdateFuelTankDetail(Vector3 eye)
    {
        for (int i = 0; i < _fuelTankFrames.Length; i++)
        {
            bool detail = (_fuelTankFrames[i].localPosition - eye).sqrMagnitude < 20 * 20;
            if (!_detailIncomplete && _fuelTankDetail[i] == detail) continue;
            for (int part = 0; part < _fuelTankFilters[i].Length; part++)
                _fuelTankFilters[i][part].sharedMesh = _fuelTankMeshes[part, detail ? 0 : 1];
            _fuelTankDetail[i] = detail;
        }
    }

    private void ReleaseFuelTanks()
    {
        _fuelTankFrames = Array.Empty<Transform>();
        _fuelTankFilters = Array.Empty<MeshFilter[]>();
        _fuelTankMeshes = new Mesh[2, 2];
        _fuelTankDetail = Array.Empty<bool>();
    }
}
