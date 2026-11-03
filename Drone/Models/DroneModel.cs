// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Modstoke.Client;

internal sealed partial class ClientDroneModel : IDisposable
{
    internal const float SolarMotionSeconds = 1.8f;
    internal const float GroundY = -.225f;
    internal static readonly Vector3 CameraPivot = new(0, -.145f, .224f);
    internal static readonly Vector3 CameraLensOffset = new(0, 0, .075f);
    private readonly List<Material> _materials = new();
    private readonly List<Mesh> _meshes = new();
    private readonly ClientDeviceSurfaceFinish _finish = new();
    private readonly Transform[] _rotors = new Transform[4];
    private readonly float[] _rotorAngles = new float[4];
    private readonly float[] _rotorSpeeds = new float[4];
    private readonly Transform[] _solarHinges = new Transform[2];
    private readonly Transform[] _solarLeaves = new Transform[2];
    private GameObject? _root;
    private bool _solarDeployed;
    private bool _solarCharging;
    private float _solarExtension;
    private float _visualTime;
    private Transform? _gimbal;
    private readonly bool _inventory;
    private readonly List<(Transform Transform, Geometry Shape)> _inventoryParts = new();
    private Color _lastStatus;
    private bool _disposed;

    internal GameObject Root => _root!;

    internal ClientDroneModel(Transform parent) : this(parent, false, false) { }

    internal static ClientDroneModel CreateInventory(Transform parent, bool battery)
        => new(parent, true, battery);

    private ClientDroneModel(Transform parent, bool inventory, bool battery)
    {
        _inventory = inventory;
        try
        {
            _root = Node("Survey drone airframe", parent);
            var shader = Shader.Find("HDRP/Lit");
            if (!shader) throw new InvalidOperationException("The drone's lit material is unavailable.");
            Material(shader, "Drone coyote composite", new Color(.22f, .185f, .125f), 0, .25f);
            Material(shader, "Drone rubber guards", new Color(.014f, .018f, .015f), 0, .18f);
            Material(shader, "Drone charcoal coated hardware", new Color(.055f, .065f, .060f), 0, .25f);
            Material(shader, "Drone coyote battery release", new Color(.19f, .16f, .10f), 0, .25f);
            Material(shader, "Drone camera glass", new Color(.025f, .08f, .12f), 0, .94f);
            Material(shader, "Drone status light", inventory ? new Color(.015f, .04f, .025f) : new Color(.08f, .8f, .25f), 0, .6f);
            Material(shader, "Drone solar cells", new Color(.009f, .018f, .025f), 0, .5f);

            _finish.ApplyShell(_materials[0], ClientDeviceSurfaceFinish.Surface.Composite);
            _finish.Apply(_materials[1], ClientDeviceSurfaceFinish.Surface.Rubber);
            _finish.Apply(_materials[2], ClientDeviceSurfaceFinish.Surface.Metal);
            _finish.Apply(_materials[6], ClientDeviceSurfaceFinish.Surface.Solar);
            if (battery)
            {
                _root.name = "Rechargeable drone pack";
                var pack = new Geometry();
                BuildBattery(pack, Vector3.zero);
                Publish(_root, pack, "Rechargeable drone pack mesh");
                FlattenInventory(true);
                return;
            }
            var airframe = new Geometry();
            BuildShell(airframe);

            for (var i = 0; i < 4; i++)
            {
                var motor = MotorPosition(i);
                BuildArm(airframe, motor);
                var rotor = Node("Drone rotor " + (i + 1), _root.transform);
                rotor.transform.localPosition = motor + Vector3.up * .064f;
                _rotors[i] = rotor.transform;
                var blades = new Geometry();
                var handedness = i == 0 || i == 3 ? -1f : 1f;
                blades.Blade(1, handedness, 2);
                blades.Blade(-1, handedness, 2);
                blades.Cylinder(new Vector3(0, .003f, 0), .020f, .012f, 1);
                blades.Cylinder(new Vector3(0, .010f, 0), .013f, .011f, 2);
                Screw(blades, new Vector3(0, .017f, 0), .005f);
                // Offset parked blades make the separate hubs legible at rest;
                // the existing speed/angle integration owns all powered movement.
                _rotorAngles[i] = i % 2 == 0 ? 24 : 114;
                rotor.transform.localRotation = Quaternion.Euler(0, _rotorAngles[i], 0);
                Publish(rotor, blades, "Drone rotor mesh");
            }
            BuildCamera(airframe);
            Publish(_root, airframe, "Survey drone airframe mesh");

            BuildSolarCharger();
            if (_inventory) FlattenInventory(false);
        }
        catch { Dispose(); throw; }
    }

    internal static Vector3 MotorPosition(int index) => new(index % 2 == 0 ? -ClientDroneFirmware.ArmX : ClientDroneFirmware.ArmX,
        -.070f, index < 2 ? ClientDroneFirmware.ArmZ : -ClientDroneFirmware.ArmZ);

    internal bool SolarFolded => !_solarDeployed && _solarExtension <= 0;
    internal bool SolarReady => _solarDeployed && _solarExtension >= 1;
    internal bool SolarMoving => _solarDeployed ? _solarExtension < 1 : _solarExtension > 0;
    internal float MotorActivity { get; private set; }

    internal void SetSolarDeployed(bool deployed) => _solarDeployed = deployed;
    internal void SetSolarCharging(bool charging) => _solarCharging = charging;

    internal void Update(float delta, float[] motors, bool armed, float health, float charge)
    {
        if (_disposed) return;
        delta = float.IsFinite(delta) ? Mathf.Clamp(delta, 0, .1f) : 0;
        _visualTime += delta;
        _solarExtension = Mathf.MoveTowards(_solarExtension, _solarDeployed ? 1 : 0, delta / SolarMotionSeconds);
        ApplySolarPose();
        var activity = 0f;
        for (var i = 0; i < 4; i++)
        {
            var target = Mathf.Sqrt(Mathf.Clamp01(motors[i] / ClientDroneFirmware.MaximumMotorForce));
            _rotorSpeeds[i] = Mathf.MoveTowards(_rotorSpeeds[i], target, delta * (target > _rotorSpeeds[i] ? 2.8f : .85f));
            activity += _rotorSpeeds[i];
            _rotorAngles[i] = (_rotorAngles[i] + _rotorSpeeds[i] * 2200 * delta * (i == 0 || i == 3 ? -1 : 1)) % 360;
            _rotors[i].localRotation = Quaternion.Euler(0, _rotorAngles[i], 0);
        }
        MotorActivity = activity * .25f;
        var status = health <= 0 ? new Color(.08f, .015f, .01f)
            : _solarCharging ? Color.Lerp(new Color(.04f, .22f, .16f), new Color(.12f, .75f, .45f),
                .5f - .5f * Mathf.Cos(_visualTime * Mathf.PI))
            : charge <= 0 ? new Color(.08f, .015f, .01f)
            : health < .35f || charge < .12f ? new Color(.9f, .09f, .015f)
            : SolarMoving ? new Color(.7f, .35f, .04f)
            : armed ? new Color(.05f, .9f, .28f) : new Color(.1f, .4f, .8f);
        if (status != _lastStatus)
        {
            _lastStatus = status;
            var material = _materials[5];
            material.SetColor("_BaseColor", status);
            if (material.HasProperty("_EmissiveColor")) material.SetColor("_EmissiveColor", status * 2);
        }
    }

    private void BuildSolarCharger()
    {
        // Four narrow leaves stow over the spine. Their fore/aft edge remains
        // inside the gap between rotor disks throughout the upward fold.
        var basePlate = new Geometry();
        basePlate.BevelBox(new Vector3(0, -.105f, 0), new Vector3(.190f, .012f, .240f), .004f, 2);
        foreach (var x in new[] { -.070f, .070f })
        foreach (var z in new[] { -.115f, .115f })
            Screw(basePlate, new Vector3(x, -.098f, z), .003f);
        Publish(Node("Solar spine mount", _root!.transform), basePlate, "Solar spine mount mesh");
        for (var side = 0; side < 2; side++)
        {
            var sign = side == 0 ? -1f : 1f;
            var height = side == 0 ? -.080f : -.044f;
            var mount = new Geometry();
            foreach (var z in new[] { -.088f, .088f })
            {
                mount.Beam(new Vector3(sign * .09f, -.099f, z), new Vector3(sign * .09f, height, z), .011f, .011f, 2);
                mount.Cylinder(new Vector3(sign * .09f, height, z), .008f, .021f, 2, Quaternion.Euler(90, 0, 0));
            }
            mount.BevelBox(new Vector3(sign * .09f, height - .009f, -.110f),
                new Vector3(.018f, .025f, .016f), .003f, 1);
            Publish(Node("Charger hinge mount", _root!.transform), mount, "Charger hinge mount mesh");
            var hinge = Node("Charger inner hinge", _root!.transform);
            hinge.transform.localPosition = new Vector3(sign * .09f, height, 0);
            _solarHinges[side] = hinge.transform;
            Publish(hinge, SolarLeaf(sign), "Charger inner leaf mesh");
            var leaf = Node("Charger outer hinge", hinge.transform);
            leaf.transform.localPosition = new Vector3(sign * .17f, .013f, 0);
            _solarLeaves[side] = leaf.transform;
            Publish(leaf, SolarLeaf(sign), "Charger outer leaf mesh");
        }
        ApplySolarPose();
    }

    private static Geometry SolarLeaf(float sign)
    {
        var leaf = new Geometry();
        var x = sign * .085f;
        leaf.BevelBox(new Vector3(x, 0, 0), new Vector3(.17f, .008f, .22f), .0025f, 2);
        leaf.Box(new Vector3(x, .0048f, 0), new Vector3(.154f, .0015f, .203f), 6);
        foreach (var column in new[] { -.051f, 0f, .051f })
            leaf.Box(new Vector3(x + column, .0059f, 0), new Vector3(.001f, .0006f, .203f), 2);
        foreach (var row in new[] { -.051f, 0f, .051f })
            leaf.Box(new Vector3(x, .0059f, row), new Vector3(.154f, .0006f, .001f), 2);
        foreach (var z in new[] { -.088f, .088f })
            leaf.Cylinder(new Vector3(0, 0, z), .006f, .018f, 2, Quaternion.Euler(90, 0, 0));
        return leaf;
    }

    private void ApplySolarPose()
    {
        // Lift the upper stack first, then the lower stack, then their outer
        // leaves. Reversing this order folds them without crossing each other.
        var upper = Ease(Mathf.Clamp01(_solarExtension * 3));
        var lower = Ease(Mathf.Clamp01(_solarExtension * 3 - 1));
        var outer = Ease(Mathf.Clamp01(_solarExtension * 3 - 2));
        for (var i = 0; i < 2; i++)
        {
            var sign = i == 0 ? -1f : 1f;
            var inner = i == 0 ? lower : upper;
            _solarHinges[i].localRotation = Quaternion.Euler(0, 0, sign * 180 * (1 - inner));
            _solarLeaves[i].localRotation = Quaternion.Euler(0, 0, sign * 180 * (1 - outer));
        }
    }

    private static float Ease(float value) => value * value * value * (value * (value * 6 - 15) + 10);

    private static GameObject Node(string name, Transform parent)
    {
        var node = new GameObject(name) { layer = parent.gameObject.layer };
        node.transform.SetParent(parent, false);
        return node;
    }

    private void Material(Shader shader, string name, Color color, float metal, float smoothness)
    {
        var material = new Material(shader) { name = name };
        _materials.Add(material);
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
        if (material.HasProperty("_Color")) material.SetColor("_Color", color);
        if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", metal);
        if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", smoothness);
    }

    private void Publish(GameObject node, Geometry geometry, string name)
    {
        if (_inventory)
        {
            _inventoryParts.Add((node.transform, geometry));
            return;
        }
        PublishMesh(node, geometry, name, ShadowCastingMode.On);
    }

    private void PublishMesh(GameObject node, Geometry geometry, string name, ShadowCastingMode shadows)
    {
        var mesh = new Mesh { name = name };
        _meshes.Add(mesh);
        var materials = geometry.Apply(mesh, _materials);
        node.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = node.AddComponent<MeshRenderer>();
        renderer.sharedMaterials = materials;
        renderer.shadowCastingMode = shadows;
        renderer.receiveShadows = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_root != null) { _root.SetActive(false); UnityEngine.Object.Destroy(_root); }
        foreach (var mesh in _meshes) if (mesh) UnityEngine.Object.Destroy(mesh);
        foreach (var material in _materials) if (material) UnityEngine.Object.Destroy(material);
        _finish.Dispose();
        _gimbal = null;
        _meshes.Clear();
        _materials.Clear();
        _inventoryParts.Clear();
    }

    private static void BuildShell(Geometry mesh)
    {
        // Long, narrow folded planes leave daylight between the raised arms.
        // The forward shell ends in two cheeks around the optical head.
        mesh.FacetedLoft(Vector3.zero, new[] {
            (-.215f, .045f, -.156f, -.132f), (-.192f, .076f, -.165f, -.119f),
            (-.132f, .104f, -.166f, -.110f), (.040f, .096f, -.166f, -.110f),
            (.113f, .083f, -.161f, -.116f), (.166f, .052f, -.151f, -.128f) }, 0, undersideMaterial: 1);
        mesh.FacetedLoft(Vector3.zero, new[] {
            (-.190f, .074f, -.167f, -.162f), (-.130f, .103f, -.168f, -.163f),
            (.040f, .095f, -.168f, -.163f), (.110f, .080f, -.163f, -.158f) }, 2);
        // The exact inventory cassette slides into a shallow underslung cradle.
        BuildBattery(mesh, new Vector3(0, -.195f, -.080f));
        mesh.BevelBox(new Vector3(0, -.169f, -.080f), new Vector3(.160f, .006f, .219f), .002f, 1);
        foreach (var sign in new[] { -1f, 1f })
        {
            mesh.BevelBox(new Vector3(sign * .087f, -.181f, -.080f), new Vector3(.012f, .040f, .229f), .003f, 2);
            foreach (var z in new[] { -.173f, .011f })
            {
                mesh.BevelBox(new Vector3(sign * .090f, -.181f, z), new Vector3(.017f, .023f, .022f), .003f, 0);
                mesh.Cylinder(new Vector3(sign * .099f, -.180f, z), .004f, .003f, 2, Quaternion.Euler(0, 0, 90));
            }
            mesh.FacetedLoft(new Vector3(sign * .091f, 0, 0), new[] {
                (.109f, .022f, -.157f, -.120f), (.170f, .024f, -.160f, -.124f),
                (.228f, .018f, -.161f, -.131f), (.255f, .012f, -.153f, -.139f) }, 0, undersideMaterial: 2);
            mesh.Beam(new Vector3(sign * .093f, -.119f, .123f), new Vector3(sign * .091f, -.132f, .225f), .006f, .003f, 2);
            foreach (var z in new[] { -.151f, .122f })
                Screw(mesh, new Vector3(sign * .072f, z < 0 ? -.114f : -.119f, z), .0035f);
            // The seam and recessed cooling slots follow the flat side panel.
            mesh.BevelBox(new Vector3(sign * .100f, -.150f, -.050f), new Vector3(.003f, .003f, .150f), .0008f, 1);
            for (var i = 0; i < 7; i++)
                mesh.BevelBox(new Vector3(sign * (.102f - i * .0006f), -.132f, -.102f + i * .015f),
                    new Vector3(.003f, .012f, .005f), .001f, 1);
            mesh.BevelBox(new Vector3(sign * .085f, -.115f, .074f), new Vector3(.016f, .008f, .031f), .002f, 2);
            Screw(mesh, new Vector3(sign * .085f, -.110f, .074f), .003f);
        }
        mesh.BevelBox(new Vector3(0, -.122f, .140f), new Vector3(.071f, .008f, .030f), .002f, 1);
        for (var i = -3; i <= 3; i++)
            mesh.Box(new Vector3(i * .009f, -.1175f, .140f), new Vector3(.004f, .002f, .023f), 2);
        mesh.BevelBox(new Vector3(.044f, -.118f, -.175f), new Vector3(.027f, .018f, .029f), .004f, 0);
        mesh.Cylinder(new Vector3(.044f, -.104f, -.175f), .009f, .016f, 2);
        mesh.Beam(new Vector3(.044f, -.103f, -.175f), new Vector3(.044f, -.033f, -.220f), .008f, .008f, 1);
        mesh.BevelBox(new Vector3(-.032f, -.119f, -.178f), new Vector3(.032f, .005f, .015f), .002f, 1);
        mesh.BevelBox(new Vector3(-.032f, -.1155f, -.178f), new Vector3(.020f, .002f, .006f), .0008f, 5);
    }

    private static void BuildArm(Geometry mesh, Vector3 motor)
    {
        var x = Mathf.Sign(motor.x);
        var z = Mathf.Sign(motor.z);
        var root = new Vector3(x * .095f, -.142f, z * .120f);
        var elbow = new Vector3(x * .205f, -.083f, z * .195f);
        var rise = elbow - root;
        mesh.FacetedLoft(root, new[] {
            (0f, .025f, -.015f, .015f), (.034f, .023f, -.014f, .014f),
            (rise.magnitude, .017f, -.011f, .012f) }, 2, Quaternion.LookRotation(rise), 1);
        var reach = motor - elbow;
        mesh.FacetedLoft(elbow, new[] {
            (-.010f, .018f, -.012f, .012f), (.025f, .019f, -.011f, .011f),
            (reach.magnitude, .025f, -.010f, .010f) }, 2, Quaternion.LookRotation(reach), 1);
        mesh.Beam(root + Vector3.down * .012f, elbow + Vector3.down * .009f, .009f, .006f, 1);
        mesh.Beam(elbow + Vector3.down * .009f, motor + Vector3.down * .009f, .009f, .006f, 1);
        // A keyed seat and capped vertical pivot make each arm interface legible.
        var hinge = Vector3.Lerp(root, elbow, .18f);
        mesh.BevelBox(root, new Vector3(.052f, .039f, .049f), .005f, 0);
        mesh.Cylinder(hinge, .026f, .039f, 2);
        mesh.Ring(hinge + Vector3.up * .021f, .023f, .017f, .003f, 1);
        mesh.Cylinder(hinge + Vector3.up * .024f, .017f, .004f, 2);
        Screw(mesh, hinge + Vector3.up * .028f, .005f);
        foreach (var offset in new[] { -.017f, .017f })
            Screw(mesh, root + new Vector3(0, .021f, offset), .003f);
        mesh.BevelBox(elbow, new Vector3(.036f, .028f, .036f), .004f, 2, Quaternion.LookRotation(reach));
        Screw(mesh, elbow + Vector3.up * .016f, .0035f);

        mesh.Cylinder(motor + Vector3.down * .021f, .037f, .012f, 2);
        mesh.Cylinder(motor, .030f, .050f, 1);
        mesh.Ring(motor + Vector3.up * .024f, .033f, .023f, .010f, 2);
        for (var fin = 0; fin < 12; fin++)
        {
            var angle = fin * Mathf.PI / 6;
            var radial = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle));
            mesh.BevelBox(motor + radial * .030f, new Vector3(.005f, .032f, .006f), .001f, 2,
                Quaternion.Euler(0, -fin * 30f, 0));
        }
        foreach (var sign in new[] { -1f, 1f })
        {
            var bolt = motor + new Vector3(sign * .026f, .031f, 0);
            mesh.Cylinder(bolt, .0045f, .008f, 2);
            Screw(mesh, bolt + Vector3.up * .005f, .0025f);
        }
        mesh.Ring(motor + Vector3.up * .035f, .020f, .011f, .008f, 1);
        mesh.Cylinder(motor + Vector3.up * .048f, .010f, .026f, 2);
        // Tapered motor feet replace the broad sled runners and guard hoops.
        // All four rubber tips end at the shared placement datum.
        mesh.FacetedLoft(new Vector3(motor.x, -.097f, motor.z), new[] {
            (0f, .018f, -.016f, .016f), (.032f, .014f, -.012f, .012f),
            (.105f, .006f, -.006f, .006f), (.122f, .005f, -.005f, .005f) },
            1, Quaternion.Euler(90, 0, 0));
        mesh.BevelBox(new Vector3(motor.x, GroundY + .003f, motor.z), new Vector3(.014f, .006f, .016f), .002f, 1);
        mesh.BevelBox(motor + new Vector3(0, -.041f, z * .016f), new Vector3(.015f, .012f, .004f), .001f, 5);
    }

    private void BuildCamera(Geometry airframe)
    {
        foreach (var sign in new[] { -1f, 1f })
        {
            airframe.Cylinder(CameraPivot + Vector3.right * sign * .091f, .019f, .042f, 1, Quaternion.Euler(0, 0, 90));
            airframe.Cylinder(CameraPivot + Vector3.right * sign * .115f, .012f, .006f, 2, Quaternion.Euler(0, 0, 90));
        }
        var node = Node("Drone stabilized optical pod", _root!.transform);
        _gimbal = node.transform;
        _gimbal.localPosition = CameraPivot;
        var camera = new Geometry();
        camera.FacetedLoft(Vector3.zero, new[] {
            (-.041f, .048f, -.025f, .025f), (-.029f, .064f, -.032f, .032f),
            (.024f, .064f, -.032f, .032f), (.041f, .057f, -.027f, .027f) }, 0);
        camera.BevelBox(new Vector3(0, 0, .041f), new Vector3(.114f, .055f, .010f), .004f, 1);
        var forward = Quaternion.Euler(90, 0, 0);
        // Three separate optical windows share the existing stabilized day view.
        // Their appearance does not introduce additional sensor modes.
        foreach (var aperture in new[] { (-.038f, .012f), (0f, .017f), (.038f, .010f) })
        {
            var center = new Vector3(aperture.Item1, 0, .049f);
            var radius = aperture.Item2;
            camera.Cylinder(center, radius + .003f, .013f, 2, forward);
            camera.Ring(center + Vector3.forward * .007f, radius + .003f, radius, .004f, 1, forward);
            camera.Cylinder(center + Vector3.forward * .006f, radius, .001f, 1, forward);
            camera.Lens(center + Vector3.forward * .007f, radius * .94f, .0015f, 4, forward);
            camera.Ring(center + Vector3.forward * .008f, radius, radius * .92f, .001f, 2, forward);
        }
        foreach (var sign in new[] { -1f, 1f })
        {
            camera.Cylinder(new Vector3(sign * .065f, 0, 0), .013f, .008f, 2, Quaternion.Euler(0, 0, 90));
            camera.Ring(new Vector3(sign * .069f, 0, 0), .011f, .007f, .002f, 1, Quaternion.Euler(0, 0, 90));
            for (var vent = 0; vent < 4; vent++)
                camera.BevelBox(new Vector3(sign * .064f, -.012f + vent * .008f, -.012f),
                    new Vector3(.002f, .002f, .020f), .0005f, 1);
            foreach (var z in new[] { -.019f, .019f })
                Screw(camera, new Vector3(sign * .046f, .033f, z), .0025f);
        }
        Publish(node, camera, "Drone optical pod mesh");
        SetCameraTilt(12);
    }

    private static void Screw(Geometry mesh, Vector3 position, float radius)
    {
        mesh.Cylinder(position, radius, .003f, 2);
        mesh.Box(position + Vector3.up * .0017f, new Vector3(radius * 1.15f, .0005f, radius * .26f), 1);
    }

    internal void SetCameraTilt(float pitch)
    {
        if (!_disposed && _gimbal != null) _gimbal.localRotation = Quaternion.Euler(pitch, 0, 0);
    }

}
