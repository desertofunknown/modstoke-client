// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace Modstoke.Client;

internal sealed partial class BoxStationModel
{
    private const int MaximumLights = 12;
    private readonly Dictionary<(int X, int Row), List<MachinePart>> _machinery = new();
    private readonly Dictionary<(int X, int Z), List<int>> _lightCells = new();
    private readonly List<Light> _localLights = new(MaximumLights);
    private readonly List<HDAdditionalLightData> _localLightData = new(MaximumLights);
    private readonly List<MachineSolid> _machineSolids = new();
    private readonly int[] _nearestLights = new int[MaximumLights];
    private readonly float[] _lightDistances = new float[MaximumLights];
    private readonly MaterialPropertyBlock _lampProperties = new();
    private ImmutableArray<BoxStationApc> _apcs;
    private ImmutableArray<BoxStationLight> _lamps;
    private Transform[] _apcFrames = Array.Empty<Transform>();
    private Transform[] _smesFrames = Array.Empty<Transform>();
    private Vector3[] _lampPositions = Array.Empty<Vector3>();
    private Quaternion[] _lampRotations = Array.Empty<Quaternion>();
    private bool[] _areaLighting = Array.Empty<bool>();
    private bool _lampEmissionDirty;
    private float _nextLights;

    internal Transform ApcControlFrame(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)index >= _apcFrames.Length) throw new ArgumentOutOfRangeException(nameof(index));
        return _apcFrames[index];
    }

    internal Vector3 ApcControlPoint(int index, BoxStationPowerChannel channel)
        => ApcControlFrame(index).TransformPoint(ApcButtonCenter(channel));

    internal static Vector3 ApcButtonCenter(BoxStationPowerChannel channel) => channel switch
    {
        BoxStationPowerChannel.Equipment => new(0, .04f, .181f),
        BoxStationPowerChannel.Lighting => new(0, -.08f, .181f),
        BoxStationPowerChannel.Environment => new(0, -.20f, .181f),
        _ => throw new ArgumentOutOfRangeException(nameof(channel))
    };

    internal Transform SmesControlFrame(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)index >= _smesFrames.Length) throw new ArgumentOutOfRangeException(nameof(index));
        return _smesFrames[index];
    }

    internal Vector3 SmesControlPoint(int index, BoxStationSmesControl control)
        => SmesControlFrame(index).TransformPoint(SmesButtonCenter(control));

    internal static Vector3 SmesButtonCenter(BoxStationSmesControl control)
    {
        if (!BoxStationPower.ValidSmesControl(control)) throw new ArgumentOutOfRangeException(nameof(control));
        int button = (int)control;
        return new(.28f - button % 5 * .14f, button < 5 ? 1.515f : 1.410f, .446f);
    }

    private IEnumerable<bool> BuildMachinery()
    {
        _apcs = BoxStationPower.GetApcs(_layout);
        _lamps = BoxStationPower.GetLights(_layout);
        _apcFrames = new Transform[_apcs.Length];
        var smes = BoxStationPower.GetSmes(_layout);
        _smesFrames = new Transform[smes.Length];
        _lampPositions = new Vector3[_lamps.Length];
        _lampRotations = new Quaternion[_lamps.Length];
        _areaLighting = new bool[_layout.Areas.Length];
        int scanned = 0;
        foreach (var source in _layout.Objects)
        {
            if (++scanned % 64 == 0) yield return false;
            string type = _layout.Types[source.Type];
            string? part = type.StartsWith("/obj/machinery/atmospherics/components/unary/vent_pump", StringComparison.Ordinal)
                ? "machine-air-supply" : type.StartsWith("/obj/machinery/atmospherics/components/unary/vent_scrubber", StringComparison.Ordinal)
                ? "machine-scrubber" : type == "/obj/machinery/power/solar" ? "machine-solar"
                : type is "/obj/machinery/power/smes" or "/obj/machinery/power/smes/engineering" ? "machine-smes" : null;
            if (part == null) continue;
            var position = _layout.CellPosition(source.Cell);
            var rotation = Turn(PortDirection(source.Direction == 0 ? 2 : source.Direction));
            AddMachine(new(source.Cell, part, position, rotation, Vector3.one, -1));
            if (part == "machine-solar") _machineSolids.Add(new(position, rotation, new(0, .2725f, 0), new(2.3f, .545f, 2.3f)));
            else if (part == "machine-smes") _machineSolids.Add(new(position, rotation, new(0, .9f, .0135f), new(.95f, 1.8f, .877f)));
            yield return false;
        }
        for (int i = 0; i < _apcs.Length; i++)
        {
            var source = _apcs[i];
            Mount(source.Cell, source.Direction, 1.5f, out var position, out var rotation);
            var frame = Node("APC " + i, Root.transform);
            frame.localPosition = position;
            frame.localRotation = rotation;
            _apcFrames[i] = frame;
            AddMachine(new(source.Cell, "machine-apc", position, rotation, Vector3.one, -1));
            yield return false;
        }
        for (int i = 0; i < smes.Length; i++)
        {
            var source = smes[i];
            var frame = Node("SMES " + i, Root.transform);
            frame.localPosition = _layout.CellPosition(source.Cell);
            frame.localRotation = Turn(PortDirection(source.Direction == 0 ? 2 : source.Direction));
            _smesFrames[i] = frame;
            yield return false;
        }
        for (int i = 0; i < _lamps.Length; i++)
        {
            var source = _lamps[i];
            Mount(source.Cell, source.Direction, 2.8f, out var position, out var rotation);
            AddMachine(new(source.Cell, "machine-light", position, rotation,
                new(source.Small ? .45f : 1, 1, 1), source.Area));
            _lampPositions[i] = position + rotation * new Vector3(0, -.09f, .15f);
            _lampRotations[i] = Quaternion.LookRotation(rotation * new Vector3(0, -1, .4f), rotation * Vector3.forward);
            var key = (Mathf.FloorToInt(_lampPositions[i].x / 14), Mathf.FloorToInt(_lampPositions[i].z / 14));
            if (!_lightCells.TryGetValue(key, out var entries)) _lightCells.Add(key, entries = new());
            entries.Add(i);
            yield return false;
        }
    }

    private void Mount(int cell, int direction, float height, out Vector3 position, out Quaternion rotation)
    {
        var outward = Turn(PortDirection(direction == 0 ? 2 : direction));
        position = _layout.CellPosition(cell) + outward * new Vector3(0, height, 1.19f);
        rotation = outward * Turn(2);
    }

    private void AddMachine(MachinePart part)
    {
        var key = (part.Cell % _layout.Width / ChunkSize * ChunkSize, part.Cell / _layout.Width / ChunkSize * ChunkSize);
        if (!_machinery.TryGetValue(key, out var entries)) _machinery.Add(key, entries = new());
        entries.Add(part);
    }

    private void AddMachinery(Chunk chunk, bool coarse, MeshBuilder opaque, MeshBuilder thermal, MeshBuilder radiators,
        Dictionary<int, MeshBuilder> lamps)
    {
        if (!_machinery.TryGetValue((chunk.X, chunk.Row), out var entries)) return;
        foreach (var part in entries)
        {
            var builder = part.Radiator ? radiators : part.Thermal ? thermal : opaque;
            if (part.Area >= 0 && !lamps.TryGetValue(part.Area, out builder))
                lamps.Add(part.Area, builder = new MeshBuilder());
            builder.Add(_parts[part.Name + (coarse ? "-lod" : "")], part.Position, part.Rotation, part.Scale);
        }
    }

    private IEnumerable<bool> BuildMachineryCollision()
    {
        foreach (var frame in _apcFrames)
        {
            AddSolid(frame, new(0, 0, .09f), new(.55f, .8f, .18f));
            yield return false;
        }
        foreach (var solid in _machineSolids)
        {
            var frame = Node("Power machinery collision", Root.transform);
            frame.localPosition = solid.Position;
            frame.localRotation = solid.Rotation;
            AddSolid(frame, solid.Center, solid.Size);
            yield return false;
        }
    }

    internal void UpdatePower(BoxStationState? state, Vector3 worldEye)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (state is { } snapshot && (snapshot.LayoutHash != _layout.LayoutHash || snapshot.Power.Length != _apcs.Length)) state = null;
        bool changed = _lampEmissionDirty;
        for (int i = 0; i < _apcs.Length; i++)
        {
            int area = _apcs[i].Area;
            bool lit = state is { } current && (current.Power[i].Available & (byte)BoxStationPowerChannel.Lighting) != 0;
            if (_areaLighting[area] == lit) continue;
            _areaLighting[area] = lit;
            changed = _lampEmissionDirty = true;
        }
        if (_lampEmissionDirty)
        {
            foreach (var chunk in _chunks)
            {
                if (chunk.Coarse != null) UpdateLampEmission(chunk.Coarse);
                if (chunk.Detail != null) UpdateLampEmission(chunk.Detail);
            }
            _lampEmissionDirty = false;
        }
        if (!Visible || !Finite(worldEye) || state == null)
        {
            foreach (var light in _localLights) light.enabled = false;
            return;
        }
        if (!changed && Time.unscaledTime < _nextLights) return;
        var eye = Root.transform.InverseTransformPoint(worldEye);
        int cell = _layout.CellAt(eye);
        int zone = cell < 0 || eye.y is < 0 or > BoxStationLayout.DeckHeight ? -1 : _layout.GetCell(cell).Zone;
        int count = 0;
        int lightX = Mathf.FloorToInt(eye.x / 14), lightZ = Mathf.FloorToInt(eye.z / 14);
        for (int dz = -1; dz <= 1; dz++)
        for (int dx = -1; dx <= 1; dx++)
        {
            if (!_lightCells.TryGetValue((lightX + dx, lightZ + dz), out var entries)) continue;
            foreach (int i in entries)
            {
                var source = _lamps[i];
                // Restrict the pool to the viewer's actual compartment. Shadowed downward cones
                // still occlude pillars and closed doors inside a larger source compartment.
                if (zone < 0 || !_areaLighting[source.Area] || _layout.GetCell(source.Cell).Zone != zone) continue;
                float distance = (_lampPositions[i] - eye).sqrMagnitude;
                if (distance > 14 * 14) continue;
                int slot = count;
                while (slot > 0 && distance < _lightDistances[slot - 1]) slot--;
                if (slot >= MaximumLights) continue;
                for (int j = Math.Min(count, MaximumLights - 1); j > slot; j--)
                {
                    _nearestLights[j] = _nearestLights[j - 1];
                    _lightDistances[j] = _lightDistances[j - 1];
                }
                _nearestLights[slot] = i;
                _lightDistances[slot] = distance;
                count = Math.Min(count + 1, MaximumLights);
            }
        }
        while (_localLights.Count < count) AddLocalLight();
        for (int i = 0; i < _localLights.Count; i++)
        {
            var light = _localLights[i];
            if (i < count)
            {
                int source = _nearestLights[i];
                light.transform.localPosition = _lampPositions[source];
                light.transform.localRotation = _lampRotations[source];
                _localLightData[i].intensity = _lamps[source].Small ? 425 : 850;
            }
            light.enabled = i < count;
        }
        _nextLights = Time.unscaledTime + .2f;
    }

    private void AddLocalLight()
    {
        var parent = Root.transform;
        var node = new GameObject("Local station lighting");
        int lightCount = _localLights.Count, dataCount = _localLightData.Count;
        try
        {
            node.SetActive(false);
            node.layer = parent.gameObject.layer;
            node.transform.SetParent(parent, false);
            var light = node.AddComponent<Light>();
            light.type = LightType.Spot;
            light.color = new(.85f, .92f, 1);
            light.range = 5.5f;
            light.spotAngle = 100;
            light.shadows = LightShadows.Hard;
            light.shadowBias = .025f;
            light.shadowNormalBias = .08f;
            light.enabled = false;
            var hd = node.AddComponent<HDAdditionalLightData>();
            hd.type = HDLightType.Spot;
            hd.lightUnit = LightUnit.Lumen;
            hd.intensity = 850;
            hd.EnableShadows(true);
            _localLights.Add(light);
            _localLightData.Add(hd);
            node.SetActive(true);
        }
        catch
        {
            if (_localLights.Count > lightCount) _localLights.RemoveAt(lightCount);
            if (_localLightData.Count > dataCount) _localLightData.RemoveAt(dataCount);
            UnityEngine.Object.Destroy(node);
            throw;
        }
    }

    private void UpdateLampEmission(Batch batch)
    {
        foreach (var lamp in batch.Lamps) SetLampEmission(lamp.Renderer, lamp.Area);
    }

    private void SetLampEmission(MeshRenderer renderer, int area)
    {
        _lampProperties.SetColor("_EmissiveColor", _areaLighting[area] ? new Color(2.5f, 2.7f, 3, 1) : Color.black);
        renderer.SetPropertyBlock(_lampProperties);
    }

    private void ReleaseMachinery()
    {
        _machinery.Clear();
        _lightCells.Clear();
        _localLights.Clear();
        _localLightData.Clear();
        _machineSolids.Clear();
        _apcFrames = Array.Empty<Transform>();
        _smesFrames = Array.Empty<Transform>();
        _lampPositions = Array.Empty<Vector3>();
        _lampRotations = Array.Empty<Quaternion>();
    }

    private readonly record struct MachinePart(int Cell, string Name, Vector3 Position, Quaternion Rotation, Vector3 Scale, int Area,
        bool Thermal = false, bool Radiator = false);
    private readonly record struct MachineSolid(Vector3 Position, Quaternion Rotation, Vector3 Center, Vector3 Size);
    private readonly record struct LampRenderer(MeshRenderer Renderer, int Area);
}
