// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class SpacecraftShorePower
{
    internal static readonly Vector3 FuelCabinetPosition = new(150.32f, 0, 19.80f);
    internal static readonly Quaternion FuelCabinetRotation = Quaternion.Euler(0, 90, 0);

    private Material _fuelRunIndicator = null!;
    private Mesh _fuelCabinetMesh = null!, _fuelStartMesh = null!, _fuelStopMesh = null!;
    private Mesh _fuelRunMesh = null!, _fuelPlugMesh = null!, _fuelPipesMesh = null!;
    private Transform _fuelCabinet = null!, _fuelPipes = null!;
    private Transform _fuelStartButton = null!, _fuelStopButton = null!;
    private bool _fuelRunning;

    private void CreateFuelMaterial(Shader shader)
    {
        _fuelRunIndicator = new Material(shader) { name = "Shore fuel run indicator", enableInstancing = true };
        _fuelRunIndicator.SetColor("_BaseColor", new Color(.012f, .028f, .018f));
        _fuelRunIndicator.SetColor("_EmissiveColor", Color.black);
        _fuelRunIndicator.SetFloat("_Metallic", 0);
        _fuelRunIndicator.SetFloat("_Smoothness", .55f);
    }

    private void CreateFuelParts()
    {
        _fuelCabinetMesh = LoadMesh("fuel-cabinet");
        _fuelStartMesh = LoadMesh("fuel-start");
        _fuelStopMesh = LoadMesh("fuel-stop");
        _fuelRunMesh = LoadMesh("fuel-run");
        _fuelPlugMesh = LoadMesh("fuel-plug");
        _fuelPipesMesh = LoadMesh("fuel-pipes");
        var inletMesh = LoadMesh("fuel-inlet");
        AddPart("Shore fuel inlet", inletMesh, _surface, _inlet, Vector3.zero, Quaternion.identity);
    }

    private void AttachFuelService(Transform station)
    {
        _fuelCabinet = AddPart("Station shore fuel cabinet", _fuelCabinetMesh, _surface, station,
            FuelCabinetPosition, FuelCabinetRotation);
        _fuelStartButton = AddPart("Fuel service start button", _fuelStartMesh, _surface, _fuelCabinet,
            ButtonMount(true, ShoreServiceKind.Fuel), Quaternion.identity);
        _fuelStopButton = AddPart("Fuel service stop button", _fuelStopMesh, _surface, _fuelCabinet,
            ButtonMount(false, ShoreServiceKind.Fuel), Quaternion.identity);
        AddPart("Fuel service run indicator", _fuelRunMesh, _fuelRunIndicator, _fuelCabinet,
            new(0, .72f, .208f), Quaternion.identity);
        AddPart("Shore fuel breakaway plug", _fuelPlugMesh, _surface, _inner,
            new(0, 0, PlugOffset), Quaternion.Euler(0, 180, 0));
        _fuelPipes = AddPart("Station shore fuel pipes", _fuelPipesMesh, _surface, station,
            Vector3.zero, Quaternion.identity);
        // The station-local route is exported at 1:10 to fit the mesh coordinate limit.
        _fuelPipes.localScale = new(10, 10, 10);
    }

    private void SetFuelService(bool running)
    {
        if (_fuelRunning == running || !_fuelRunIndicator) return;
        _fuelRunning = running;
        _fuelRunIndicator.SetColor("_BaseColor", running ? new Color(.08f, .55f, .24f) : new Color(.012f, .028f, .018f));
        _fuelRunIndicator.SetColor("_EmissiveColor", running ? new Color(.10f, .8f, .3f) * 2 : Color.black);
    }

    private void DetachFuelService()
    {
        if (_fuelCabinet)
        {
            _fuelCabinet.gameObject.SetActive(false);
            UnityEngine.Object.Destroy(_fuelCabinet.gameObject);
        }
        if (_fuelPipes)
        {
            _fuelPipes.gameObject.SetActive(false);
            UnityEngine.Object.Destroy(_fuelPipes.gameObject);
        }
        _fuelCabinet = _fuelPipes = null!;
        _fuelStartButton = _fuelStopButton = null!;
        SetFuelService(false);
    }
}
