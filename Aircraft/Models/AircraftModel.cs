// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace Modstoke.Client;

// Presentation only. The flight body owns colliders, authority and the parent pose.
internal sealed partial class ClientAircraftModel : IDisposable
{
    private readonly List<Mesh> _meshes = new();
    private readonly List<Material> _materials = new();
    private readonly List<Texture2D> _textures = new();
    private readonly Dictionary<string, Transform> _parts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Material> _finishes = new(StringComparer.Ordinal);
    private GameObject? _root;
    private readonly Texture2D?[,] _surfaces = new Texture2D?[4, 3];
    private Light? _landingLight;
    private Light? _cabinLight;
    private float _propellerAngle;
    private float _wheelAngle;
    private float _clock;
    private float _flapAngle;
    private float _portDoorAngle;
    private float _starboardDoorAngle;
    private bool _disposed;
    private static readonly (Vector3 Center, Vector2 HalfSize, string Command)[] CockpitTargets =
    {
        (new Vector3(-.43f, .92f, 1.23f), new Vector2(.052f, .030f), "engine"),
        (new Vector3(-.23f, .92f, 1.23f), new Vector2(.052f, .030f), "lights"),
        (new Vector3(-.03f, .92f, 1.23f), new Vector2(.052f, .030f), "flaps"),
        (new Vector3(.17f, .92f, 1.23f), new Vector2(.052f, .030f), "brake"),
        (new Vector3(.37f, .92f, 1.23f), new Vector2(.052f, .030f), "starter"),
        (new Vector3(0, .58f, .930f), new Vector2(.050f, .045f), "throttle-up"),
        (new Vector3(0, .48f, .764f), new Vector2(.050f, .035f), "throttle-down")
    };

    internal ClientAircraftModel(Transform parent)
    {
        try
        {
            _root = new GameObject("Kestrel K3 airframe") { layer = parent.gameObject.layer };
            _root.transform.SetParent(parent, false);
            Load();
            SetOccupants(0);
            SetActive("PropellerBlur", false);
            _landingLight = CreateLight("Kestrel landing beam", new Vector3(0, .15f, 2.83f),
                LightType.Spot, new Color(1, .93f, .76f), 75, 8500);
            _landingLight.spotAngle = 38;
            _landingLight.transform.localRotation = Quaternion.Euler(5, 0, 0);
            _cabinLight = CreateLight("Kestrel instrument wash", new Vector3(0, 1.27f, .89f),
                LightType.Point, new Color(1, .48f, .20f), 1.15f, 4);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal void Update(AircraftFlightState state, float dt)
    {
        if (_disposed || _root == null) return;
        dt = Mathf.Clamp(dt, 0, .1f);
        // The body supplies the same hinge angles used by physical door leaves.
        Rotate("DoorPort", 0, _portDoorAngle, 0);
        Rotate("DoorStarboard", 0, -_starboardDoorAngle, 0);
        _clock = Mathf.Repeat(_clock + dt, 60);
        // RPM also represents a stopped engine's windmilling propeller.
        var rpm = Mathf.Clamp(state.Rpm, 0, 4000);
        _propellerAngle = Mathf.Repeat(_propellerAngle + rpm * 6 * dt, 360);
        Rotate("Propeller", 0, 0, _propellerAngle);
        Rotate("PropellerBlades", 0, 0, _propellerAngle);
        Rotate("PropellerBlur", 0, 0, _propellerAngle * .1f);
        SetActive("PropellerBlades", rpm < 900);
        SetActive("PropellerBlur", rpm > 450);
        if (_finishes.TryGetValue("Propeller motion", out var blur))
            ColorMaterial(blur, new Color(.13f, .15f, .16f, Mathf.Lerp(0, .085f, Mathf.InverseLerp(450, 1800, rpm))));

        var forwardSpeed = Vector3.Dot(state.Velocity, state.Rotation * Vector3.forward);
        if (state.Grounded)
            _wheelAngle = Mathf.Repeat(_wheelAngle + forwardSpeed / AircraftLayout.WheelRadius * Mathf.Rad2Deg * dt, 360);
        Rotate("WheelPort", _wheelAngle, 0, 0);
        Rotate("WheelStarboard", _wheelAngle, 0, 0);
        // The fork and axle steer together. Only the child wheel spins.
        Rotate("NoseGearSteering", 0, state.Grounded
            ? ClientAircraftAerodynamics.NoseSteering(state.YawControl, state.Velocity.magnitude) : 0, 0);
        Rotate("WheelNose", _wheelAngle, 0, 0);
        Rotate("AileronPort", -state.RollControl * 19, 0, 0);
        Rotate("AileronStarboard", state.RollControl * 19, 0, 0);
        Rotate("ElevatorPort", state.PitchControl * 22, 0, 0);
        Rotate("ElevatorStarboard", state.PitchControl * 22, 0, 0);
        Rotate("Rudder", 0, -state.YawControl * 24, 0);
        _flapAngle = Mathf.MoveTowards(_flapAngle, Mathf.Clamp(state.Flaps, 0, 2) * 15, dt * 16);
        Rotate("FlapPort", -_flapAngle, 0, 0);
        Rotate("FlapStarboard", -_flapAngle, 0, 0);
        UpdateCrewControls(state, dt);

        Gauge("GaugeAirspeed", ClientAircraftAerodynamics.IndicatedKnots(state) / 160);
        Gauge("GaugeAltitude", Mathf.Max(0, state.Position.y) * 3.28084f / 10000);
        Gauge("GaugeRpm", rpm / 3000);
        Gauge("GaugeFuel", state.Fuel);
        Gauge("GaugeHealth", state.Health);
        Gauge("GaugeVerticalSpeed", .5f + state.Velocity.y * 196.8504f / 4000);
        Rotate("GaugeHeading", 0, 0, state.Rotation.eulerAngles.y);
        // Roll card and pitch displacement stay inside the round attitude aperture.
        if (_parts.TryGetValue("AttitudeCard", out var attitude))
        {
            var angles = state.Rotation.eulerAngles;
            attitude.localRotation = Quaternion.Euler(0, 0, -Mathf.DeltaAngle(0, angles.z));
            var pitch = Mathf.Clamp(Mathf.DeltaAngle(0, angles.x), -35, 35);
            attitude.localPosition = new Vector3(-.21f, .80f + pitch * .00055f, 1.249f);
        }
        Emission("Port navigation lens", new Color(.9f, .015f, .004f), state.Lights ? 3 : 0);
        Emission("Starboard navigation lens", new Color(.008f, .8f, .12f), state.Lights ? 3 : 0);
        Emission("Warm landing lens", new Color(.95f, .89f, .67f), state.Lights ? 2 : 0);
        var beacon = (state.Engine || state.Lights) && Mathf.Repeat(_clock, 1.3f) < .12f;
        Emission("Anti-collision beacon", new Color(.9f, .015f, .004f), beacon ? 6 : 0);
        if (_landingLight != null) _landingLight.enabled = state.Lights;
        if (_cabinLight != null) _cabinLight.enabled = state.Lights;
    }

    // The service gates this on the local pilot and cockpit pointer mode.
    // Intersect authored control rectangles rather than the aircraft's physics
    // hull, which intentionally surrounds the seated camera.
    internal bool TryCockpitControl(Ray worldRay, out string command)
    {
        command = "";
        if (_disposed || _root == null) return false;
        var transform = _root.transform;
        var direction = worldRay.direction.normalized;
        var denominator = Vector3.Dot(direction, transform.forward);
        if (denominator <= .05f) return false;
        var nearest = 2.5f;
        foreach (var target in CockpitTargets)
        {
            var center = target.Center;
            if (target.Command == "throttle-up" && _parts.TryGetValue("ThrottleLever", out var lever))
                center = transform.InverseTransformPoint(lever.TransformPoint(new Vector3(0, .10f, .01f)));
            var point = transform.TransformPoint(center);
            var distance = Vector3.Dot(point - worldRay.origin, transform.forward) / denominator;
            if (distance < 0 || distance >= nearest) continue;
            var local = transform.InverseTransformPoint(worldRay.origin + direction * distance) - center;
            if (Mathf.Abs(local.x) > target.HalfSize.x || Mathf.Abs(local.y) > target.HalfSize.y) continue;
            nearest = distance;
            command = target.Command;
        }
        return command.Length != 0;
    }

    internal void SetDoorAngles(float port, float starboard)
    {
        if (_disposed) return;
        _portDoorAngle = port;
        _starboardDoorAngle = starboard;
    }

    private Light CreateLight(string name, Vector3 position, LightType type, Color color, float range, float intensity)
    {
        var root = _root!;
        var node = new GameObject(name) { layer = root.layer };
        node.transform.SetParent(root.transform, false);
        node.transform.localPosition = position;
        var light = node.AddComponent<Light>();
        light.type = type;
        light.color = color;
        light.range = range;
        light.shadows = LightShadows.None;
        // The shipped player uses HDRP. Its companion component owns physical units.
        var hd = node.AddComponent<HDAdditionalLightData>();
        hd.intensity = intensity;
        light.enabled = false;
        return light;
    }

    private void Rotate(string name, float x, float y, float z)
    {
        if (_parts.TryGetValue(name, out var part)) part.localRotation = Quaternion.Euler(x, y, z);
    }

    private void SetActive(string name, bool active)
    {
        if (_parts.TryGetValue(name, out var part) && part.gameObject.activeSelf != active)
            part.gameObject.SetActive(active);
    }

    private void Gauge(string name, float value) => Rotate(name, 0, 0, Mathf.Lerp(130, -130, Mathf.Clamp01(value)));

    private void Emission(string name, Color color, float amount)
    {
        if (_finishes.TryGetValue(name, out var material))
            material.SetColor("_EmissiveColor", color * amount);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_root != null)
        {
            _root.SetActive(false);
            UnityEngine.Object.Destroy(_root);
        }
        foreach (var mesh in _meshes) if (mesh) UnityEngine.Object.Destroy(mesh);
        foreach (var material in _materials) if (material) UnityEngine.Object.Destroy(material);
        foreach (var texture in _textures) if (texture) UnityEngine.Object.Destroy(texture);
        _parts.Clear();
        _finishes.Clear();
        _meshes.Clear();
        _materials.Clear();
        _textures.Clear();
        Array.Clear(_surfaces, 0, _surfaces.Length);
        _landingLight = null;
        _cabinLight = null;
        _root = null;
    }
}
