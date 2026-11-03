// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class BoxStationModel
{
    private const float CableHeight = .018f, CableContactRadius = .04f;
    private readonly ClientDeviceSurfaceFinish _cableFinish = new();
    private ImmutableArray<BoxStationCableSite> _cableSites = ImmutableArray<BoxStationCableSite>.Empty;
    private int[] _cableCells = Array.Empty<int>();
    private int[,] _cableNeighbors = new int[0, 4];
    private bool[] _cableAccessible = Array.Empty<bool>();
    private BoxStationCableState _cableState;
    private Material? _cableMaterial, _cableHousingMaterial;

    internal long CableRevision => _cableState.Revision;

    private IEnumerable<bool> BuildCables()
    {
        _cableSites = BoxStationPower.GetCableSites(_layout);
        _cableCells = new int[_layout.Cells.Length];
        Array.Fill(_cableCells, -1);
        _cableAccessible = new bool[_cableSites.Length];
        _cableNeighbors = new int[_cableSites.Length, 4];
        for (int index = 0; index < _cableSites.Length; index++)
        {
            int cell = _cableSites[index].Cell;
            // One geometry entry represents every conductor slot at this cell.
            if (_cableCells[cell] < 0) _cableCells[cell] = index;
            _cableAccessible[index] = BoxStationPower.CableAccessible(_layout, index)
                || _floorCoverings[cell];
            if (index % 64 == 63) yield return false;
        }
        for (int index = 0; index < _cableSites.Length; index++)
        {
            for (int direction = 0; direction < 4; direction++)
            {
                int cell = _cableSites[index].Cell, neighbor = Neighbor(cell, direction);
                _cableNeighbors[index, direction] = neighbor >= 0 && BoxStationPower.CableEdge(_layout, cell, neighbor)
                    ? _cableCells[neighbor] : -1;
            }
            if (index % 64 == 63) yield return false;
        }
        _cableState = BoxStationPower.InitialCableState(_layout);
    }

    private void LoadCableMaterial(Shader shader)
    {
        _cableMaterial = new Material(shader) { name = "Station cable insulation", enableInstancing = true };
        _cableMaterial.SetColor("_BaseColor", new Color(.36f, .018f, .012f, 1));
        _cableMaterial.SetFloat("_Metallic", 0);
        _cableMaterial.SetFloat("_Smoothness", .32f);
        _cableFinish.Apply(_cableMaterial, ClientDeviceSurfaceFinish.Surface.Rubber);
        _cableHousingMaterial = new Material(shader) { name = "Station junction housings", enableInstancing = true };
        _cableHousingMaterial.SetColor("_BaseColor", new Color(.025f, .032f, .04f, 1));
        _cableHousingMaterial.SetFloat("_Metallic", 0);
        _cableHousingMaterial.SetFloat("_Smoothness", .35f);
        _cableFinish.Apply(_cableHousingMaterial, ClientDeviceSurfaceFinish.Surface.Rubber);
    }

    // Cable presence does not remove the pressure floor or alter hull collision.
    internal bool UpdateCables(BoxStationCableState state)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        FinishGeometryRetirement();
        if (!BoxStationPower.ValidCableState(_layout, state) || state.Revision < _cableState.Revision) return false;
        if (state.Revision == _cableState.Revision)
        {
            if (state.Installed == _cableState.Installed) return true;
            for (int i = 0; i < state.Installed.Length; i++)
                if (state.Installed[i] != _cableState.Installed[i]) return false;
            return true;
        }
        var chunks = new HashSet<int>();
        for (int index = 0; index < _cableSites.Length; index++)
        {
            if (CableInstalled(_cableState, index) == CableInstalled(state, index)) continue;
            AddCableChunk(index, chunks);
            for (int direction = 0; direction < 4; direction++)
            {
                int neighbor = _cableNeighbors[index, direction];
                if (neighbor >= 0) AddCableChunk(neighbor, chunks);
            }
        }
        RebuildSurfaceChunks(chunks, state, _floorState);
        _cableState = state;
        FinishGeometryRetirement();
        return true;
    }

    private void AddCableChunk(int index, HashSet<int> chunks)
    {
        if (!_cableAccessible[index]) return;
        AddSurfaceChunk(_cableSites[index].Cell, chunks);
    }

    private static bool CableInstalled(BoxStationCableState state, int index)
        => (state.Installed[index >> 3] & (1 << (index & 7))) != 0;

    private bool CableDeckPresent(int index, BoxStationHull hull)
        => _cableAccessible[index]
            && (hull.EffectiveFlags(_cableSites[index].Cell) & BoxStationCellFlags.Floor) != 0
            && !hull.Breached(_cableSites[index].Cell, false) && !hull.Foam(_cableSites[index].Cell, false);

    private bool CableExposed(int index, BoxStationHull hull, BoxStationFloorState floors)
        => CableDeckPresent(index, hull)
            && (!_floorCoverings[_cableSites[index].Cell] || BoxStationHullRepair.FloorRemoved(floors, _cableSites[index].Cell));

    private void AddCable(int cell, BoxStationHull hull, BoxStationCableState state, BoxStationFloorState floors,
        string suffix, MeshBuilder cables, MeshBuilder housings, MeshBuilder hardware)
    {
        int index = _cableCells[cell];
        if (index < 0 || !CableDeckPresent(index, hull) || !BoxStationPower.IsCableCellInstalled(_layout, state, cell)) return;
        bool covered = _floorCoverings[cell] && !BoxStationHullRepair.FloorRemoved(floors, cell);
        var position = _layout.CellPosition(cell, _floorCoverings[cell] ? FloorCableOffset : 0);
        if (!covered)
        {
            housings.Add(_parts["cable-hub" + suffix], position, Quaternion.identity);
            hardware.Add(_parts["cable-hardware" + suffix], position, Quaternion.identity);
        }
        for (int direction = 0; direction < 4; direction++)
        {
            int neighbor = _cableNeighbors[index, direction];
            if (neighbor >= 0 && BoxStationPower.IsCableCellInstalled(_layout, state, _cableSites[neighbor].Cell))
            {
                bool raisedEnd = _floorCoverings[cell] && BoxStationPower.CableAccessible(_layout, neighbor);
                // Closed covers expose only boundary risers through their service throats.
                if (covered && !raisedEnd) continue;
                cables.Add(_parts[(raisedEnd ? "cable-riser" : "cable-branch") + suffix], position, Turn(direction - 1));
            }
            else if (!covered)
                housings.Add(_parts["cable-cap" + suffix], position, Turn(direction - 1));
        }
    }

    internal bool TryCableTarget(Ray worldRay, bool installing, out int index, out Vector3 localContact)
    {
        index = -1;
        localContact = default;
        if (!Visible || !Finite(worldRay.origin) || !Finite(worldRay.direction)) return false;
        var origin = Root.transform.InverseTransformPoint(worldRay.origin);
        var direction = Root.transform.InverseTransformDirection(worldRay.direction);
        if (!Finite(origin) || !Finite(direction) || MathF.Abs(direction.y) < .000001f) return false;
        float nearest = float.PositiveInfinity;
        for (int plane = 0; plane < 2; plane++)
        {
            float height = CableHeight + (plane == 0 ? 0 : FloorCableOffset);
            float distance = (height - origin.y) / direction.y;
            if (!float.IsFinite(distance) || distance < 0 || distance >= nearest) continue;
            var contact = origin + direction * distance;
            int cell = _layout.CellAt(contact);
            if (cell < 0 || plane == 1 && !_floorCoverings[cell]) continue;
            int candidate = BoxStationPower.CableAtCell(_layout, _cableState, cell, installing);
            contact.y = CableHeight;
            if (!ValidCableContact(candidate, contact, installing)) continue;
            nearest = distance;
            index = candidate;
            localContact = contact;
        }
        return index >= 0;
    }

    internal bool ValidCableContact(int index, Vector3 localContact, bool installing)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)index >= _cableSites.Length || !Finite(localContact)
            || !CableExposed(index, _hull, _floorState) || CableInstalled(_cableState, index) == installing
            || _floorCoverings[_cableSites[index].Cell] && !FloorStateCurrent
            || MathF.Abs(localContact.y - CableHeight) > CableContactRadius) return false;
        // Keep input above the unchanged deck collider; recessed cable branches share this XZ footprint.
        var center = _layout.CellPosition(_cableSites[index].Cell, CableHeight);
        var delta = localContact - center;
        if (installing) return delta.sqrMagnitude <= .35f * .35f;
        if (delta.sqrMagnitude <= .05f * .05f) return true;
        for (int direction = 0; direction < 4; direction++)
        {
            int neighbor = _cableNeighbors[index, direction];
            if (neighbor < 0 || !BoxStationPower.IsCableCellInstalled(_layout, _cableState, _cableSites[neighbor].Cell)) continue;
            var axis = Turn(direction) * Vector3.forward;
            var nearest = axis * Mathf.Clamp(Vector3.Dot(delta, axis), 0, BoxStationLayout.TileSize * .5f);
            if ((delta - nearest).sqrMagnitude <= CableContactRadius * CableContactRadius) return true;
        }
        return false;
    }

    private void ReleaseCables()
    {
        if (_cableMaterial) UnityEngine.Object.Destroy(_cableMaterial);
        if (_cableHousingMaterial) UnityEngine.Object.Destroy(_cableHousingMaterial);
        _cableMaterial = _cableHousingMaterial = null;
        _cableFinish.Dispose();
        _cableSites = ImmutableArray<BoxStationCableSite>.Empty;
        _cableCells = Array.Empty<int>();
        _cableNeighbors = new int[0, 4];
        _cableAccessible = Array.Empty<bool>();
        _cableState = default;
    }
}
