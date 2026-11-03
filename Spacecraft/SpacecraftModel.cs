// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftModel : IDisposable
{
    private const string ExteriorKey = "SpaceshipCaveDExterior";
    private const string InteriorKey = "SpaceshipCaveDInterior";
    // Native lower-leaf tread plane in the shared prefab frame, in metres.
    private static readonly Vector3 RampDeckForward = new(.0015f, -2.8185f, -14.6549f);
    private static readonly Vector3 RampDeckAft = new(.0015f, -3.4458f, -16.6719f);
    // The owned renderers share native meshes and textures. Keep the two load
    // references for the module lifetime, including between ship instances.
    private static AsyncOperationHandle<GameObject>? _exteriorLoad;
    private static AsyncOperationHandle<GameObject>? _interiorLoad;
    private static string _assetFailure = "";
    private static long _nextAssetAttempt;
    private static int _assetFailures;
    private readonly List<Material> _materials = new();
    private readonly List<Material> _screens = new();
    private readonly List<Material> _exhaust = new();
    private readonly List<(Material Material, Color Color)> _cabinEmission = new();
    private readonly Dictionary<int, Material> _finishes = new();
    private readonly List<HDAdditionalLightData> _thrustLights = new();
    private readonly List<HDAdditionalLightData> _cabinLights = new();
    private readonly Dictionary<string, Transform> _parts = new(StringComparer.Ordinal);
    private readonly Transform[] _seatFrames = new Transform[3];
    private readonly Transform[] _seatArtwork = new Transform[3];
    private readonly Bounds[] _seatInteractionBounds = new Bounds[3];
    private readonly Vector3[] _seatBackOffsets = new Vector3[3];
    private GameObject? _root;
    private Transform _rampExtension = null!;
    private Quaternion _boardingRampRotation = Quaternion.identity;
    private float _rampClosedAngle, _rampOpenAngle, _boardingRampLength, _boardingExtensionLength;
    private float _boardingRampToeHeight = float.NaN;
    private float _doorAmount = 1;
    private float _appliedDoorAmount = float.NaN;
    private float _thrust;
    private float _screenPower;
    private float _cabinPower = .7f;
    private bool _disposed;

    internal Transform Ramp { get; private set; } = null!;
    internal BoxCollider BoardingRamp { get; private set; } = null!;
    internal BoxCollider BoardingExtension { get; private set; } = null!;
    internal float DoorAmount => _doorAmount;
    internal bool Ready => !_disposed && _root && Ramp;

    internal static bool PrepareAssets(out string status)
    {
        if (_assetFailure.Length != 0 && Environment.TickCount64 < _nextAssetAttempt)
        { status = _assetFailure; return false; }
        try
        {
            _exteriorLoad ??= Addressables.LoadAssetAsync<GameObject>(ExteriorKey);
            _interiorLoad ??= Addressables.LoadAssetAsync<GameObject>(InteriorKey);
            if (!_exteriorLoad.IsValid() || !_interiorLoad.IsValid())
                throw new InvalidOperationException("The installed spacecraft asset handles are invalid.");
            if (!_exteriorLoad.IsDone || !_interiorLoad.IsDone)
            {
                status = "Loading the installed spacecraft exterior and cabin...";
                return false;
            }
            if (_exteriorLoad.Status != AsyncOperationStatus.Succeeded || !_exteriorLoad.Result)
                throw new InvalidOperationException("The installed spacecraft exterior could not be loaded.");
            if (_interiorLoad.Status != AsyncOperationStatus.Succeeded || !_interiorLoad.Result)
                throw new InvalidOperationException("The installed spacecraft cabin could not be loaded.");
            _assetFailure = "";
            _assetFailures = 0;
            _nextAssetAttempt = 0;
            status = "Spacecraft assets ready.";
            return true;
        }
        catch (Exception error)
        {
            _assetFailure = "Spacecraft unavailable: " + error.Message;
            _nextAssetAttempt = Environment.TickCount64 + Math.Min(30000L, 500L << _assetFailures);
            _assetFailures = Math.Min(_assetFailures + 1, 6);
            ReleaseFailedLoad(ref _exteriorLoad);
            ReleaseFailedLoad(ref _interiorLoad);
            status = _assetFailure;
            return false;
        }
    }

    private static void ReleaseFailedLoad(ref AsyncOperationHandle<GameObject>? handle)
    {
        try { if (handle != null && handle.IsValid()) Addressables.Release<GameObject>(handle); }
        catch { /* Preserve the original asset error in the tablet status. */ }
        handle = null;
    }

    internal ClientSpacecraftModel(Transform parent)
    {
        if (!PrepareAssets(out var status)) throw new InvalidOperationException(status);
        try
        {
            _root = new GameObject("Installed spacecraft artwork") { layer = parent.gameObject.layer };
            _root.SetActive(false);
            _root.transform.SetParent(parent, false);
            CloneVisualTree(_exteriorLoad!.Result.transform, _root.transform, ExteriorKey);
            CloneVisualTree(_interiorLoad!.Result.transform, _root.transform, InteriorKey);
            BindLandingGear();
            BindSeat(0, "SpaceshipSeat (2)");
            BindSeat(1, "SpaceshipSeat (1)");
            BindSeat(2, "SpaceshipSeat");
            InitializeFlightControls();
            var rampMesh = _parts[ExteriorKey + "/ShipMeshes/SpaceshipDoorPartA"];
            var doorFrame = _parts[ExteriorKey + "/ShipMeshes/SpaceshipDoorFrame"];
            var hinge = new GameObject("Aft ramp hinge") { layer = _root.layer };
            Ramp = hinge.transform;
            Ramp.SetParent(_root.transform, false);
            Ramp.localPosition = _root.transform.InverseTransformPoint(doorFrame.position);
            // The native latch tongues need 4.65 degrees beyond frame alignment
            // to retain closing clearance while keeping the transverse hinge.
            _rampClosedAngle = Vector3.SignedAngle(rampMesh.forward, doorFrame.forward, _root.transform.right) + 4.65f;
            rampMesh.SetParent(Ramp, true);
            BindUpperDoor();
            BindInnerDoor();
            BindCabinDoor();
            BindAirlockSeals();
            UpdateCabinDoor(1, 2);
            BoardingRamp = AddBoardingRamp("Aft boarding ramp surface", Ramp);
            var deck = RampDeckForward - RampDeckAft;
            _boardingRampLength = deck.magnitude;
            _boardingRampRotation = Quaternion.LookRotation(deck, Vector3.up);
            BoardingRamp.transform.localPosition = (RampDeckForward + RampDeckAft) * .5f - Ramp.localPosition;
            BoardingRamp.transform.localRotation = _boardingRampRotation;
            BoardingRamp.transform.localScale = new Vector3(2.05f, .06f, _boardingRampLength + .025f);
            _rampExtension = new GameObject("Aft ramp folding extension") { layer = _root.layer }.transform;
            _rampExtension.SetParent(Ramp, false);
            _rampExtension.localPosition = RampDeckAft - Ramp.localPosition
                + _boardingRampRotation * Vector3.up * .03f;
            BoardingExtension = AddBoardingRamp("Aft boarding ramp extension surface", _rampExtension);
            SetBoardingRampToe(-3.56f);
            AddLight("Port lower turbine", new Vector3(-5.23f, 1.43f, -15.85f), 8, true);
            AddLight("Starboard lower turbine", new Vector3(5.23f, 1.43f, -15.85f), 8, true);
            AddLight("Port upper turbine", new Vector3(-3.35f, 3.49f, -14.36f), 6, true);
            AddLight("Starboard upper turbine", new Vector3(3.35f, 3.49f, -14.36f), 6, true);
            AddLight("Aft entry light", new Vector3(-.05f, .65f, -12), 5, false);
            AddLight("Airlock light", new Vector3(-.05f, 1.15f, -6.8f), 4.5f, false);
            AddLight("Port cabin light", new Vector3(-1.65f, 1.35f, -1), 5, false);
            AddLight("Starboard cabin light", new Vector3(1.65f, 1.35f, -1), 5, false);
            AddLight("Cockpit instrument light", new Vector3(0, 1.35f, 4.8f), 6, false);
            AddLight("Port console light", new Vector3(-1.3f, .65f, 4.45f), 3.2f, false);
            AddLight("Starboard console light", new Vector3(1.1f, .65f, 4.45f), 3.2f, false);
            AddLight("Navigation station light", new Vector3(-1.45f, .8f, -2.7f), 3.8f, false);
            InitializeCabinReflections();
            _root.SetActive(true);
        }
        catch { Dispose(); throw; }
    }

    private void BindSeat(int seat, string name)
    {
        var chair = _parts[InteriorKey + "/CockpitAndCabin/Seats/" + name];
        _seatArtwork[seat] = chair;
        _seatInteractionBounds[seat] = chair.GetComponent<MeshFilter>().sharedMesh.bounds;
        var frame = new GameObject("Chair adjustment " + seat) { layer = _root!.layer }.transform;
        frame.SetParent(_root.transform, false);
        _seatFrames[seat] = frame;
        _seatBackOffsets[seat] = _root.transform.InverseTransformDirection(chair.forward) * -.12f;
        // Keep the native mesh and collider in the same adjustment frame.
        chair.SetParent(frame, true);
    }

    internal Transform SeatFrame(int seat) => _seatFrames[seat];

    // Mesh-local bounds plus the live artwork transform retain native chair yaw,
    // scale and the existing adjustment without enlarging its physical collider.
    internal Transform SeatArtwork(int seat) => _seatArtwork[seat];
    internal Bounds SeatInteractionBounds(int seat) => _seatInteractionBounds[seat];

    internal void SetSeatSitProgress(int seat, float progress)
    {
        // Leave admission room until the final third of the existing sit.
        var settle = Mathf.SmoothStep(0, 1, Mathf.Clamp01((progress - .65f) / .35f));
        var swivel = Mathf.SmoothStep(0, 1, Mathf.Clamp01((progress - .2f) / .8f));
        var rotation = seat == 0 ? Quaternion.identity
            : Quaternion.Slerp(Quaternion.identity, Quaternion.Inverse(SpacecraftLayout.SeatRotation(seat)), swivel);
        var pivot = SpacecraftLayout.Seat(seat);
        var frame = _seatFrames[seat];
        frame.localRotation = rotation;
        frame.localPosition = pivot - rotation * pivot + _seatBackOffsets[seat] * (1 - settle);
    }

    private BoxCollider AddBoardingRamp(string name, Transform parent)
    {
        // A metal plate covers the native stepped lip with one uninterrupted
        // incline. The rendered plate and its collider always share a pose.
        var plate = GameObject.CreatePrimitive(PrimitiveType.Cube);
        plate.name = name;
        plate.layer = _root!.layer;
        plate.transform.SetParent(parent, false);
        var source = _parts[InteriorKey + "/Entry/EntryFloor/SpaceshipAddCMesh"].GetComponent<MeshRenderer>();
        plate.GetComponent<MeshRenderer>().sharedMaterial = source.sharedMaterial;
        return plate.GetComponent<BoxCollider>();
    }

    internal void SetBoardingRampToe(float height)
    {
        if (!float.IsFinite(height)) return;
        if (float.IsFinite(_boardingRampToeHeight) && Mathf.Abs(height - _boardingRampToeHeight) < .005f) return;
        _boardingRampToeHeight = height;
        var toe = new Vector3(RampDeckAft.x, height, -18.5f);
        var offset = toe - Ramp.localPosition;
        var deck = RampDeckForward - RampDeckAft;
        var normal = _boardingRampRotation * Vector3.up;
        var planeHeight = Vector3.Dot(RampDeckForward - Ramp.localPosition, normal) + .03f;
        // Rotate the tread plane about the native hinge until its extension
        // reaches the terrain. The leaf and its positive-scale plate stay rigid.
        var slope = Mathf.Asin(planeHeight / Mathf.Sqrt(offset.y * offset.y + offset.z * offset.z))
            - Mathf.Atan2(offset.y, -offset.z);
        _rampOpenAngle = (Mathf.Atan2(deck.y, deck.z) - slope) * Mathf.Rad2Deg;
        var openRotation = Quaternion.Euler(_rampOpenAngle, 0, 0);
        var mainAft = Ramp.localPosition + openRotation * _rampExtension.localPosition;
        _boardingExtensionLength = (toe - mainAft).magnitude;
        _appliedDoorAmount = float.NaN;
        ApplyDoorPose();
    }

    private void CloneVisualTree(Transform source, Transform parent, string path)
    {
        var node = new GameObject(source.name) { layer = _root!.layer };
        var transform = node.transform;
        transform.SetParent(parent, false);
        transform.localPosition = source.localPosition;
        transform.localRotation = source.localRotation;
        transform.localScale = source.localScale;
        _parts[path] = transform;
        var filter = source.GetComponent<MeshFilter>();
        var original = source.GetComponent<MeshRenderer>();
        if (filter && original && filter.sharedMesh)
        {
            node.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
            var renderer = node.AddComponent<MeshRenderer>();
            var materials = original.sharedMaterials;
            var forwardDisplay = path.StartsWith(InteriorKey
                + "/CockpitAndCabin/CockpitMeshes/SpaceshipCockpitOptimizedSplitMesh/SpaceshipCockpitOptimizedSplit", StringComparison.Ordinal);
            var turbine = path.Contains("/SpaceshipTurbineHalfMesh/", StringComparison.Ordinal);
            var interior = path.StartsWith(InteriorKey + "/", StringComparison.Ordinal);
            for (var i = 0; i < materials.Length; i++)
            {
                var native = materials[i];
                if (!native) continue;
                // Native slot 4 is the forward display decal; slot 13 is glass.
                // The hanging monitors share one finish with their housings and
                // have no emission mask, so leave those finishes unchanged.
                var display = forwardDisplay && i == 4 && native.name == "SpaceshipCockpitDecalEm"
                    || interior && (native.name == "SpaceshipControllersGlass"
                        || native.name == "SpaceshipNavigationMonitor 1");
                var strip = interior && native.name.StartsWith("SciFiEmissive", StringComparison.Ordinal);
                if (!display && !strip && !(turbine && native.name == "Fire")) continue;
                if (_finishes.TryGetValue(native.GetInstanceID(), out var owned))
                {
                    materials[i] = owned;
                    continue;
                }
                var material = new Material(native);
                _materials.Add(material);
                _finishes.Add(native.GetInstanceID(), material);
                material.name = "Spacecraft " + native.name;
                materials[i] = material;
                if (display)
                {
                    // This slot contains only the alpha-clipped display artwork.
                    // Preserve its UVs, clipping, base, normal and mask maps.
                    if (material.HasProperty("_EmissiveColorMap") && !material.GetTexture("_EmissiveColorMap")
                        && material.HasProperty("_BaseColorMap"))
                    {
                        var map = material.GetTexture("_BaseColorMap");
                        if (map)
                        {
                            material.SetTexture("_EmissiveColorMap", map);
                            material.EnableKeyword("_EMISSIVE_COLOR_MAP");
                        }
                    }
                    if (material.HasProperty("_EmissiveColorMap") && material.GetTexture("_EmissiveColorMap"))
                        material.EnableKeyword("_EMISSIVE_COLOR_MAP");
                    if (material.HasProperty("_EmissiveExposureWeight")) material.SetFloat("_EmissiveExposureWeight", 0);
                    _screens.Add(material);
                }
                else if (strip)
                {
                    var color = material.HasProperty("_EmissiveColorLDR")
                        ? material.GetColor("_EmissiveColorLDR") : Color.white;
                    if (color.maxColorComponent < .05f) color = new Color(.35f, .65f, .85f);
                    if (material.HasProperty("_EmissiveExposureWeight")) material.SetFloat("_EmissiveExposureWeight", 0);
                    _cabinEmission.Add((material, color));
                }
                else _exhaust.Add(material);
                if (material.HasProperty("_EmissiveColor")) material.SetColor("_EmissiveColor", Color.black);
            }
            renderer.sharedMaterials = materials;
            renderer.shadowCastingMode = original.shadowCastingMode;
            renderer.receiveShadows = original.receiveShadows;
            renderer.renderingLayerMask = original.renderingLayerMask;
            renderer.lightProbeUsage = original.lightProbeUsage;
            renderer.reflectionProbeUsage = original.reflectionProbeUsage;
            // Keep the serialized visual state (including unused hidden decals).
            // No native script, collider, light, animation, scene renderer
            // reference or DistanceActivator is instantiated.
            renderer.enabled = original.enabled;
        }
        for (var i = 0; i < source.childCount; i++)
        {
            var child = source.GetChild(i);
            CloneVisualTree(child, transform, path + "/" + child.name);
        }
        node.SetActive(source.gameObject.activeSelf);
    }

    private void AddLight(string name, Vector3 position, float range, bool thrust)
    {
        var node = new GameObject(name) { layer = _root!.layer };
        node.transform.SetParent(_root.transform, false);
        node.transform.localPosition = position;
        var light = node.AddComponent<Light>();
        light.type = LightType.Point;
        light.color = thrust ? new Color(.28f, .65f, 1) : new Color(.85f, .92f, 1);
        light.range = range;
        light.shadows = LightShadows.None;
        var hd = node.AddComponent<HDAdditionalLightData>();
        hd.type = HDLightType.Point;
        hd.lightUnit = LightUnit.Lumen;
        hd.intensity = thrust ? 0 : _cabinPower * 2200;
        if (thrust) _thrustLights.Add(hd); else _cabinLights.Add(hd);
    }

    internal void SetDoorAmount(float amount)
    {
        _doorAmount = Mathf.Clamp01(amount);
        if (Ready) ApplyDoorPose();
    }

    private void ApplyDoorPose()
    {
        if (_appliedDoorAmount == _doorAmount) return;
        var closing = 1 - _doorAmount;
        // Stow the telescoping extension on the leaf, swing to the fixed frame,
        // then lower the header. Terrain fitting affects only the open endpoint.
        var fold = Mathf.SmoothStep(0, 1, Mathf.Clamp01(closing / .25f));
        var travel = Mathf.SmoothStep(0, 1, Mathf.Clamp01((_doorAmount - .25f) / .5f));
        Ramp.localRotation = Quaternion.Euler(Mathf.Lerp(_rampClosedAngle, _rampOpenAngle, travel), 0, 0);
        _rampExtension.localRotation = _boardingRampRotation * Quaternion.Euler(180 * fold, 0, 0);
        var extensionLength = Mathf.Lerp(_boardingRampLength, _boardingExtensionLength, 1 - fold);
        BoardingExtension.transform.localPosition = new Vector3(0, -.03f, -extensionLength * .5f);
        BoardingExtension.transform.localScale = new Vector3(2.05f, .06f, extensionLength + .025f);
        // Retract the header before the ramp lowers; close it last.
        _upperDoorFrame.localPosition = Vector3.up * (.45f * Mathf.SmoothStep(0, 1, Mathf.Clamp01(_doorAmount / .25f)));
        _appliedDoorAmount = _doorAmount;
    }

    internal void Update(SpacecraftFlightState state, Vector3 translation, float dt, bool authoritative)
    {
        if (!Ready) return;
        UpdateFlightControls(state, dt);
        var powered = state.Powered && ClientSpacecraftSystems.PropulsionAvailable(state.Systems)
            || state.Travel != SpacecraftTravel.None;
        var demand = powered ? state.Travel != SpacecraftTravel.None ? 1
            : .18f + .82f * Mathf.Clamp01(translation.magnitude) : 0;
        _thrust = Mathf.MoveTowards(_thrust, demand, dt * 2.5f);
        _screenPower = Mathf.MoveTowards(_screenPower, powered ? 1 : .08f, dt * 2);
        bool cabinLighting = state.Systems.Ready && SpacecraftElectrical.IsPowered(state.Systems.Electrical, SpacecraftCircuit.Cabin);
        _cabinPower = Mathf.MoveTowards(_cabinPower, cabinLighting ? powered ? 1 : .7f : 0, dt * 2);
        // Only the host integrates the command. Replicas approach the received
        // actual amount, preserving host state instead of running a second door.
        bool outerBlocked = authoritative && state.DoorOpen && _doorAmount <= .01f
            && !ClientSpacecraftSystems.CanOpenOuter(state.Systems, state.Region,
                InnerDoorClosed, CabinDoorClosed, out _);
        _doorAmount = authoritative ? Mathf.MoveTowards(_doorAmount, state.DoorOpen ? 1 : 0,
                outerBlocked ? 0 : dt * .65f * DoorDrive(state.Systems))
            : Mathf.MoveTowards(_doorAmount, state.DoorAmount, dt * 2);
        ApplyDoorPose();
        bool gearPowered = state.Systems.Ready && SpacecraftElectrical.IsPowered(state.Systems.Electrical, SpacecraftCircuit.Propulsion);
        SetGearAmount(Mathf.MoveTowards(GearAmount, authoritative ? state.GearDeployed ? 1 : 0 : state.GearAmount,
            dt * (authoritative ? gearPowered ? .4f : 0 : 2)));
        var exhaust = new Color(.15f, .5f, 1) * (_thrust * 45);
        foreach (var material in _exhaust)
            if (material) material.SetColor("_EmissiveColor", exhaust);
        foreach (var material in _screens)
            if (material) material.SetColor("_EmissiveColor", new Color(.55f, .8f, 1) * (_screenPower * 1.8f));
        foreach (var finish in _cabinEmission)
            if (finish.Material) finish.Material.SetColor("_EmissiveColor", finish.Color * (_cabinPower * 2));
        foreach (var light in _thrustLights) if (light) light.intensity = _thrust * 1800;
        foreach (var light in _cabinLights) if (light) light.intensity = _cabinPower * 2200;
        UpdateCabinReflections(state, dt);
    }

    // Pressure doors and the rear ramp have a slow mechanical emergency drive.
    internal static float DoorDrive(SpacecraftSystemState systems) => !systems.Ready ? 0
        : SpacecraftElectrical.IsPowered(systems.Electrical, SpacecraftCircuit.Airlock) ? 1
        : systems.EmergencyOverride ? .2f : 0;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseCabinReflections();
        if (_root != null && _root) { _root.SetActive(false); UnityEngine.Object.Destroy(_root); }
        _root = null;
        foreach (var material in _materials) if (material) UnityEngine.Object.Destroy(material);
        _materials.Clear();
        ReleaseFlightControls();
        ReleaseAirlockSeals();
        _screens.Clear();
        _exhaust.Clear();
        _cabinEmission.Clear();
        _finishes.Clear();
        _thrustLights.Clear();
        _cabinLights.Clear();
        _parts.Clear();
    }
}
