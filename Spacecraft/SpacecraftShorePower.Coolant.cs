// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class SpacecraftShorePower
{
    internal static readonly Vector3 CoolantCabinetPosition = new(150.32f, 0, 18.65f);
    internal static readonly Quaternion CoolantCabinetRotation = Quaternion.Euler(0, 90, 0);

    private Material _coolantRunIndicator = null!;
    private Mesh _coolantCabinetMesh = null!, _coolantStartMesh = null!, _coolantStopMesh = null!;
    private Mesh _coolantRunMesh = null!, _coolantPlugMesh = null!, _coolantPipesMesh = null!;
    private Transform _coolantCabinet = null!, _coolantPipes = null!, _coolantCap = null!;
    private Transform _coolantStartButton = null!, _coolantStopButton = null!;
    private bool _coolantRunning;

    private void CreateCoolantMaterial(Shader shader)
    {
        _coolantRunIndicator = new Material(shader) { name = "Shore coolant run indicator", enableInstancing = true };
        _coolantRunIndicator.SetColor("_BaseColor", new Color(.012f, .028f, .018f));
        _coolantRunIndicator.SetColor("_EmissiveColor", Color.black);
        _coolantRunIndicator.SetFloat("_Metallic", 0);
        _coolantRunIndicator.SetFloat("_Smoothness", .55f);
    }

    private void CreateCoolantParts()
    {
        _coolantCabinetMesh = LoadMesh("coolant-cabinet");
        _coolantStartMesh = LoadMesh("coolant-start");
        _coolantStopMesh = LoadMesh("coolant-stop");
        _coolantRunMesh = LoadMesh("coolant-run");
        _coolantPlugMesh = LoadMesh("coolant-plug");
        _coolantPipesMesh = LoadMesh("coolant-pipes");
        var inletMesh = LoadMesh("coolant-inlet");
        var capMesh = LoadMesh("coolant-cap");
        AddPart("Shore coolant inlet", inletMesh, _surface, _inlet, Vector3.zero, Quaternion.identity);
        _coolantCap = AddPart("Shore coolant inlet cap", capMesh, _surface, _inlet,
            new(0, -.295f, .10f), Quaternion.identity);
    }

    private void AttachCoolantService(Transform station)
    {
        _coolantCabinet = AddPart("Station shore coolant cabinet", _coolantCabinetMesh, _surface, station,
            CoolantCabinetPosition, CoolantCabinetRotation);
        _coolantStartButton = AddPart("Coolant service start button", _coolantStartMesh, _surface, _coolantCabinet,
            ButtonMount(true, ShoreServiceKind.Coolant), Quaternion.identity);
        _coolantStopButton = AddPart("Coolant service stop button", _coolantStopMesh, _surface, _coolantCabinet,
            ButtonMount(false, ShoreServiceKind.Coolant), Quaternion.identity);
        AddPart("Coolant service run indicator", _coolantRunMesh, _coolantRunIndicator, _coolantCabinet,
            new(0, .72f, .208f), Quaternion.identity);
        AddPart("Shore coolant breakaway plug", _coolantPlugMesh, _surface, _inner,
            new(0, 0, PlugOffset), Quaternion.Euler(0, 180, 0));
        _coolantPipes = AddPart("Station shore coolant pipes", _coolantPipesMesh, _surface, station,
            Vector3.zero, Quaternion.identity);
        // The station-local route is exported at 1:10 to fit the mesh coordinate limit.
        _coolantPipes.localScale = new(10, 10, 10);
    }

    private void SetCoolantService(bool running)
    {
        if (_coolantRunning == running || !_coolantRunIndicator) return;
        _coolantRunning = running;
        _coolantRunIndicator.SetColor("_BaseColor", running ? new Color(.08f, .55f, .24f) : new Color(.012f, .028f, .018f));
        _coolantRunIndicator.SetColor("_EmissiveColor", running ? new Color(.10f, .8f, .3f) * 2 : Color.black);
    }

    private void DetachCoolantService()
    {
        if (_coolantCabinet)
        {
            _coolantCabinet.gameObject.SetActive(false);
            UnityEngine.Object.Destroy(_coolantCabinet.gameObject);
        }
        if (_coolantPipes)
        {
            _coolantPipes.gameObject.SetActive(false);
            UnityEngine.Object.Destroy(_coolantPipes.gameObject);
        }
        _coolantCabinet = _coolantPipes = null!;
        _coolantStartButton = _coolantStopButton = null!;
        SetCoolantService(false);
    }
}
