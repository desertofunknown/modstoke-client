// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftModel
{
    internal static readonly Vector2 FlightControlHalfSize = new(.022f, .050f);

    private readonly List<Mesh> _flightControlMeshes = new();
    private readonly List<Texture2D> _flightControlTextures = new();
    private Transform _flightControlGimbal = null!;
    private Transform _flightControlHandle = null!;
    private Transform _flightControlTrigger = null!;
    private FlightControlBoot? _flightControlBoot;
    private float _flightControlPitch, _flightControlRoll, _flightControlTriggerAngle;

    internal Transform FlightControlGrip { get; private set; } = null!;
    internal Transform FlightControlPick { get; private set; } = null!;
    internal Transform FlightControlDrag { get; private set; } = null!;

    private void InitializeFlightControls()
    {
        var material = CreateControlMaterial("controls", "Pilot flight controls", 2048, 1024);
        // The chair frame is unit-scale in ship coordinates; the native chair yaw
        // is applied once here, and its occupied adjustment remains on the parent.
        var housing = AddMeshPart("Pilot flight control housing", LoadFlightControlMesh("housing"), material,
            SeatFrame(0), new(1.277f, -.609f, 4.350f), SpacecraftLayout.SeatRotation(0));
        var pivot = new Vector3(0, .043f, .077f);
        _flightControlGimbal = AddMeshPart("Flight control roll gimbal", LoadFlightControlMesh("gimbal"), material,
            housing, pivot, Quaternion.identity);
        _flightControlHandle = AddMeshPart("Flight control pitch grip", LoadFlightControlMesh("grip"), material,
            _flightControlGimbal, Vector3.zero, Quaternion.identity);
        _flightControlTrigger = AddMeshPart("Flight control boost trigger", LoadFlightControlMesh("trigger"), material,
            _flightControlHandle, new(0, .144f, .036f), Quaternion.identity);
        InitializeFlightControlBoot(housing, pivot, material);
        FlightControlGrip = Contact("Flight control palm", _flightControlHandle,
            new(.032f, .119f, .014f), Quaternion.LookRotation(Vector3.forward, Vector3.left));
        var pick = new Vector3(0, .112f, -.020f);
        FlightControlPick = Contact("Flight control pick", _flightControlHandle, pick, Quaternion.identity);
        // Picking follows the visible grip, but dragging uses its neutral plane
        // so moving the handle cannot feed back into the pointer's input.
        FlightControlDrag = Contact("Flight control drag", housing, pivot + pick, Quaternion.identity);
        InitSeatConsoles(CreateControlMaterial("console", "Seat consoles", 4096, 2048));

        static Transform Contact(string name, Transform parent, Vector3 position, Quaternion rotation)
        {
            var contact = new GameObject(name) { layer = parent.gameObject.layer }.transform;
            contact.SetParent(parent, false);
            contact.localPosition = position;
            contact.localRotation = rotation;
            return contact;
        }
    }

    private void UpdateFlightControls(SpacecraftFlightState state, float dt)
    {
        var pitch = Mathf.Clamp(state.TranslationInput.z - state.RotationInput.x, -1, 1) * 12;
        var roll = Mathf.Clamp(-state.TranslationInput.x - state.RotationInput.y + state.RotationInput.z, -1, 1) * 12;
        _flightControlPitch = Mathf.MoveTowards(_flightControlPitch, pitch, dt * 120);
        _flightControlRoll = Mathf.MoveTowards(_flightControlRoll, roll, dt * 120);
        _flightControlTriggerAngle = Mathf.MoveTowards(_flightControlTriggerAngle, state.BoostActive ? 8 : 0, dt * 120);
        _flightControlGimbal.localRotation = Quaternion.Euler(0, 0, _flightControlRoll);
        _flightControlHandle.localRotation = Quaternion.Euler(_flightControlPitch, 0, 0);
        _flightControlTrigger.localRotation = Quaternion.Euler(_flightControlTriggerAngle, 0, 0);
        _flightControlBoot!.Update(_flightControlPitch, _flightControlRoll);
        UpdateSeatConsoles(dt);
    }

    private Material CreateControlMaterial(string asset, string name, int width, int height)
    {
        var shader = Shader.Find("HDRP/Lit");
        if (!shader) throw new InvalidOperationException("The flight controls require the game's HDRP/Lit shader.");
        var material = new Material(shader) { name = name, enableInstancing = true };
        _materials.Add(material);
        material.SetColor("_BaseColor", Color.white);
        material.SetTexture("_BaseColorMap", LoadControlTexture(asset, "atlas", false, width, height));
        material.SetTexture("_NormalMap", LoadControlTexture(asset, "atlas-normal", true, width, height));
        material.SetTexture("_MaskMap", LoadControlTexture(asset, "atlas-mask", true, width, height));
        material.SetFloat("_NormalScale", 1);
        material.SetFloat("_NormalMapSpace", 0);
        material.SetFloat("_MetallicRemapMin", 0);
        material.SetFloat("_MetallicRemapMax", 1);
        material.SetFloat("_SmoothnessRemapMin", 0);
        material.SetFloat("_SmoothnessRemapMax", 1);
        material.SetFloat("_AORemapMin", 0);
        material.SetFloat("_AORemapMax", 1);
        material.EnableKeyword("_NORMALMAP_TANGENT_SPACE");
        material.EnableKeyword("_NORMALMAP");
        material.EnableKeyword("_MASKMAP");
        return material;
    }

    private Mesh LoadFlightControlMesh(string asset)
    {
        using var stream = FlightControlResource(asset + ".msa");
        var data = SpacecraftMeshData.Read(stream, asset);
        return CreateControlMesh(data, "Flight control " + asset);
    }

    private Mesh CreateControlMesh(SpacecraftMeshData data, string name)
    {
        var mesh = new Mesh();
        _flightControlMeshes.Add(mesh);
        mesh.name = name;
        mesh.indexFormat = data.Vertices.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
        mesh.vertices = data.Vertices;
        mesh.normals = data.Normals;
        mesh.uv = data.Uv;
        mesh.SetTriangles(data.Triangles, 0);
        mesh.RecalculateBounds();
        mesh.RecalculateTangents();
        mesh.UploadMeshData(true);
        return mesh;
    }

    private Texture2D LoadControlTexture(string part, string asset, bool linear, int width, int height)
    {
        var resource = "spacecraft-" + part + "-" + asset + ".png";
        using var stream = typeof(ClientSpacecraftModel).Assembly.GetManifestResourceStream("Modstoke.Client.Assets." + resource)
            ?? throw new InvalidOperationException("The control texture is missing: " + resource);
        if (!stream.CanSeek || stream.Length is < 24 or > 64 * 1024 * 1024)
            throw new InvalidDataException("The control texture size is invalid: " + resource);
        using var bytes = new MemoryStream((int)stream.Length);
        stream.CopyTo(bytes);
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear);
        _flightControlTextures.Add(texture);
        texture.name = part + " " + asset;
        if (!ImageConversion.LoadImage(texture, bytes.ToArray(), false) || texture.width != width || texture.height != height)
            throw new InvalidDataException("The control texture could not be decoded: " + resource);
        if (asset == "atlas-normal")
        {
            // HDRP unpacks tangent X from alpha and Y from green; source PNGs
            // retain ordinary RGB normals for the authored model.
            var pixels = texture.GetPixels32().ToArray();
            for (var i = 0; i < pixels.Length; i++)
                pixels[i] = new Color32(255, pixels[i].g, 0, pixels[i].r);
            texture.SetPixels32(pixels);
        }
        texture.wrapMode = TextureWrapMode.Clamp;
        texture.filterMode = FilterMode.Trilinear;
        texture.anisoLevel = 8;
        texture.Apply(true, false);
        texture.Compress(true);
        texture.Apply(false, true);
        return texture;
    }

    private static Stream FlightControlResource(string name) => typeof(ClientSpacecraftModel).Assembly.GetManifestResourceStream(
        "Modstoke.Client.Assets.spacecraft-controls-" + name)
        ?? throw new InvalidOperationException("The flight control asset is missing: " + name);

    private void ReleaseFlightControls()
    {
        ReleaseSeatConsoles();
        foreach (var mesh in _flightControlMeshes) if (mesh) UnityEngine.Object.Destroy(mesh);
        _flightControlMeshes.Clear();
        foreach (var texture in _flightControlTextures) if (texture) UnityEngine.Object.Destroy(texture);
        _flightControlTextures.Clear();
        _flightControlGimbal = _flightControlHandle = _flightControlTrigger = null!;
        _flightControlBoot = null;
        FlightControlGrip = FlightControlPick = FlightControlDrag = null!;
    }
}
