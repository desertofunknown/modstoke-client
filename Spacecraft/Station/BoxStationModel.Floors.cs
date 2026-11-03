// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class BoxStationModel
{
    private const float FloorCableOffset = -.045f;
    private readonly ClientDeviceSurfaceFinish _floorFinish = new();
    private bool[] _floorCoverings = Array.Empty<bool>();
    private BoxStationFloorState _floorState = new(0, 0, ImmutableArray<ushort>.Empty);
    private Material? _floorBaseMaterial, _floorCoverMaterial, _floorInsetMaterial, _deckHardwareMaterial;

    internal long FloorRevision => _floorState.Revision;

    private bool FloorStateCurrent => _materialState is { } state && state.HullRevision == _hull.State.Revision
        && state.CableRevision == _cableState.Revision && state.Floors.Revision == _floorState.Revision;

    private IEnumerable<bool> BuildFloors()
    {
        _floorCoverings = new bool[_layout.Cells.Length];
        int count = 0;
        foreach (int cell in BoxStationHullRepair.GetFloorCells(_layout))
        {
            _floorCoverings[cell] = true;
            if (++count % 256 == 0) yield return false;
        }
    }

    private void LoadFloorMaterials(Shader shader)
    {
        _floorBaseMaterial = new Material(shader) { name = "Station deck substrate", enableInstancing = true };
        _floorBaseMaterial.SetColor("_BaseColor", new Color(.18f, .22f, .25f, 1));
        _floorBaseMaterial.SetFloat("_Metallic", .85f);
        _floorBaseMaterial.SetFloat("_Smoothness", .32f);
        _floorFinish.Apply(_floorBaseMaterial, ClientDeviceSurfaceFinish.Surface.Metal);
        _floorCoverMaterial = new Material(shader) { name = "Station metal floor coverings", enableInstancing = true };
        _floorCoverMaterial.SetColor("_BaseColor", new Color(.32f, .36f, .39f, 1));
        _floorCoverMaterial.SetFloat("_Metallic", .8f);
        _floorCoverMaterial.SetFloat("_Smoothness", .42f);
        _floorFinish.Apply(_floorCoverMaterial, ClientDeviceSurfaceFinish.Surface.Metal);
        _floorInsetMaterial = new Material(shader) { name = "Station deck grip inserts", enableInstancing = true };
        _floorInsetMaterial.SetColor("_BaseColor", new Color(.055f, .065f, .075f, 1));
        _floorInsetMaterial.SetFloat("_Metallic", 0);
        _floorInsetMaterial.SetFloat("_Smoothness", .2f);
        _floorFinish.Apply(_floorInsetMaterial, ClientDeviceSurfaceFinish.Surface.Rubber);
        _deckHardwareMaterial = new Material(shader) { name = "Station deck hardware", enableInstancing = true };
        _deckHardwareMaterial.SetColor("_BaseColor", new Color(.45f, .48f, .5f, 1));
        _deckHardwareMaterial.SetFloat("_Metallic", .95f);
        _deckHardwareMaterial.SetFloat("_Smoothness", .58f);
        _floorFinish.Apply(_deckHardwareMaterial, ClientDeviceSurfaceFinish.Surface.Metal);
    }

    private void AddFloor(int cell, BoxStationFloorState state, string suffix, MeshBuilder bases, MeshBuilder covers,
        MeshBuilder insets, MeshBuilder hardware)
    {
        var position = _layout.CellPosition(cell);
        bases.Add(_parts["floor-exposed" + suffix], position, Quaternion.identity);
        if (BoxStationHullRepair.FloorRemoved(state, cell))
            hardware.Add(_parts["floor-base-hardware" + suffix], position, Quaternion.identity);
        else
        {
            covers.Add(_parts["floor-cover" + suffix], position, Quaternion.identity);
            insets.Add(_parts["floor-cover-inset" + suffix], position, Quaternion.identity);
            hardware.Add(_parts["floor-cover-hardware" + suffix], position, Quaternion.identity);
        }
    }

    // Incomplete material streams retain the last accepted covering state.
    private bool UpdateFloors(BoxStationFloorState state)
    {
        FinishGeometryRetirement();
        if (!BoxStationHullRepair.ValidFloors(_layout, state, _hull.State) || state.Revision < _floorState.Revision
            || state.LostTiles < _floorState.LostTiles) return false;
        if (state.Revision == _floorState.Revision)
        {
            if (state.LostTiles != _floorState.LostTiles || state.Removed.Length != _floorState.Removed.Length) return false;
            if (state.Removed == _floorState.Removed) return true;
            for (int i = 0; i < state.Removed.Length; i++)
                if (state.Removed[i] != _floorState.Removed[i]) return false;
            return true;
        }
        var chunks = new HashSet<int>();
        int oldIndex = 0, newIndex = 0;
        while (oldIndex < _floorState.Removed.Length || newIndex < state.Removed.Length)
        {
            int previous = oldIndex < _floorState.Removed.Length ? _floorState.Removed[oldIndex] : int.MaxValue;
            int next = newIndex < state.Removed.Length ? state.Removed[newIndex] : int.MaxValue;
            int cell = Math.Min(previous, next);
            if (previous == cell) oldIndex++;
            if (next == cell) newIndex++;
            if (previous == next) continue;
            AddSurfaceChunk(cell, chunks);
        }
        RebuildSurfaceChunks(chunks, _cableState, state);
        _floorState = state;
        FinishGeometryRetirement();
        return true;
    }

    private void AddSurfaceChunk(int cell, HashSet<int> chunks)
    {
        int row = cell / _layout.Width, x = cell % _layout.Width;
        chunks.Add(row / ChunkSize * ChunkSize * _layout.Width + x / ChunkSize * ChunkSize);
    }

    private void RebuildSurfaceChunks(HashSet<int> chunks, BoxStationCableState cables, BoxStationFloorState floors)
    {
        FinishGeometryRetirement();
        var replacements = new List<(Chunk Previous, Chunk Next)>();
        try
        {
            foreach (var chunk in _chunks)
            {
                if (!chunks.Contains(chunk.Row * _layout.Width + chunk.X)) continue;
                var replacement = new Chunk(chunk.X, chunk.Row);
                replacements.Add((chunk, replacement));
                replacement.Coarse = BuildChunk(replacement, true, active: false, cableState: cables, floorState: floors);
                if (chunk.Detail != null)
                    replacement.Detail = BuildChunk(replacement, false, active: false, cableState: cables, floorState: floors);
            }
            foreach (var replacement in replacements)
            {
                if (replacement.Next.Coarse != null) replacement.Next.Coarse.Node.SetActive(replacement.Next.Detail == null);
                if (replacement.Next.Detail != null) replacement.Next.Detail.Node.SetActive(true);
            }
        }
        catch
        {
            foreach (var replacement in replacements)
            {
                if (replacement.Next.Coarse != null) QueueBatchRetirement(replacement.Next.Coarse);
                if (replacement.Next.Detail != null) QueueBatchRetirement(replacement.Next.Detail);
            }
            throw;
        }
        foreach (var replacement in replacements)
        {
            var chunk = replacement.Previous;
            if (chunk.Coarse != null) QueueBatchRetirement(chunk.Coarse);
            if (chunk.Detail != null) QueueBatchRetirement(chunk.Detail);
            chunk.Coarse = replacement.Next.Coarse;
            chunk.Detail = replacement.Next.Detail;
        }
    }

    internal bool TryFloorTarget(Ray worldRay, bool installing, out int cell, out Vector3 localContact)
    {
        cell = -1;
        localContact = default;
        if (!Visible || !Finite(worldRay.origin) || !Finite(worldRay.direction)) return false;
        var origin = Root.transform.InverseTransformPoint(worldRay.origin);
        var direction = Root.transform.InverseTransformDirection(worldRay.direction);
        if (!Finite(origin) || !Finite(direction) || MathF.Abs(direction.y) < .000001f) return false;
        float distance = -origin.y / direction.y;
        if (!float.IsFinite(distance) || distance < 0) return false;
        var contact = origin + direction * distance;
        int candidate = _layout.CellAt(contact);
        if (!ValidFloorContact(candidate, contact, installing)) return false;
        cell = candidate;
        localContact = contact;
        return true;
    }

    internal bool ValidFloorContact(int cell, Vector3 localContact, bool installing)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)cell >= _floorCoverings.Length || !_floorCoverings[cell] || !Finite(localContact)
            || MathF.Abs(localContact.y) > .04f || _layout.CellAt(localContact) != cell
            || (_hull.EffectiveFlags(cell) & BoxStationCellFlags.Floor) == 0 || _hull.Breached(cell, false) || _hull.Foam(cell, false)
            || BoxStationHullRepair.FloorRemoved(_floorState, cell) != installing) return false;
        return FloorStateCurrent;
    }

    private void ReleaseFloors()
    {
        if (_floorBaseMaterial) UnityEngine.Object.Destroy(_floorBaseMaterial);
        if (_floorCoverMaterial) UnityEngine.Object.Destroy(_floorCoverMaterial);
        if (_floorInsetMaterial) UnityEngine.Object.Destroy(_floorInsetMaterial);
        if (_deckHardwareMaterial) UnityEngine.Object.Destroy(_deckHardwareMaterial);
        _floorBaseMaterial = _floorCoverMaterial = _floorInsetMaterial = _deckHardwareMaterial = null;
        _floorFinish.Dispose();
        _floorCoverings = Array.Empty<bool>();
        _floorState = new(0, 0, ImmutableArray<ushort>.Empty);
    }
}
