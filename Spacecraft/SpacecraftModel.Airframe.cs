// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftModel
{
    private readonly Transform[] _gearFrames = new Transform[3];
    private readonly Transform[] _innerDoorFrames = new Transform[2];
    private readonly Transform[] _innerDoorLeaves = new Transform[2];
    private readonly Vector3[] _innerDoorClosedPositions = new Vector3[2];
    private Transform _upperDoorFrame = null!, _upperDoorLeaf = null!;
    private float _innerDoorAmount;
    private static readonly Vector3[] GearMounts =
    {
        new(0, -1.923f, 6.58f), new(-4.949f, -.268f, -6.965f), new(4.949f, -.268f, -6.965f)
    };

    internal float GearAmount { get; private set; } = 1;
    internal bool InnerDoorClosed => _innerDoorAmount <= .01f;
    internal float InnerDoorAmount => _innerDoorAmount;
    internal Transform GearFrame(int index) => _gearFrames[index];

    private void BindUpperDoor()
    {
        _upperDoorLeaf = _parts[ExteriorKey + "/ShipMeshes/SpaceshipDoorPartB"];
        _upperDoorFrame = new GameObject("Upper hatch slide") { layer = _root!.layer }.transform;
        _upperDoorFrame.SetParent(_root.transform, false);
        _upperDoorLeaf.SetParent(_upperDoorFrame, true);
        var railMaterial = _upperDoorLeaf.GetComponent<MeshRenderer>().sharedMaterials[2];
        foreach (var x in new[] { -1.15f, 1.14f })
        {
            var rail = GameObject.CreatePrimitive(PrimitiveType.Cube);
            rail.name = "Upper hatch guide";
            rail.layer = _root.layer;
            rail.transform.SetParent(_root.transform, false);
            rail.transform.localPosition = new Vector3(x, -.67f, -15.32f);
            rail.transform.localScale = new Vector3(.07f, .64f, .11f);
            rail.GetComponent<MeshRenderer>().sharedMaterial = railMaterial;
            var collider = rail.GetComponent<BoxCollider>();
            collider.enabled = false;
            UnityEngine.Object.Destroy(collider);
        }
    }

    private void BindInnerDoor()
    {
        const string prefix = InteriorKey + "/Entry/SpaceshipDoorA01/SpaceshipDoorAMesh.";
        // The native left leaf hangs at an angle in its partly open pose.
        // Seat it square in the frame so the powered hatch can meet at a seam.
        _parts[prefix + "003"].rotation = _parts[prefix + "002"].rotation;
        for (var i = 0; i < _innerDoorFrames.Length; i++)
        {
            var leaf = _parts[prefix + (i == 0 ? "002" : "003")];
            _innerDoorLeaves[i] = leaf;
            // Mesh-local Z is the sliding direction. The latch projects past
            // the straight mating edges, so mesh bounds leave a visible gap.
            var edge = leaf.TransformPoint(new Vector3(0, 0, i == 0 ? -.0054f : .014f));
            var innerEdge = _root!.transform.InverseTransformPoint(edge).x;
            var frame = new GameObject("Inner airlock leaf " + i) { layer = _root!.layer }.transform;
            frame.SetParent(_root.transform, false);
            leaf.SetParent(frame, true);
            _innerDoorFrames[i] = frame;
            // Native leaves are partly open and have different baked origins.
            // Translate their inner edges to the frame's centre seam without
            // changing either leaf's shape.
            _innerDoorClosedPositions[i] = new Vector3(-.094f - innerEdge, 0, 0);
            frame.localPosition = _innerDoorClosedPositions[i];
        }
    }

    internal void UpdateAirlock(float target, float dt)
    {
        _innerDoorAmount = Mathf.MoveTowards(_innerDoorAmount, target, dt * .8f);
        var travel = Mathf.SmoothStep(0, 1, _innerDoorAmount) * 1.05f;
        for (var i = 0; i < _innerDoorFrames.Length; i++)
            _innerDoorFrames[i].localPosition = _innerDoorClosedPositions[i]
                + Vector3.right * (i == 0 ? travel : -travel);
    }

    private void BindLandingGear()
    {
        var names = new[] { "ShipFeetFrontFoot", "ShipFeetRearFoot", "ShipFeetRearFootRightMirror" };
        for (var i = 0; i < names.Length; i++)
        {
            var artwork = _parts[ExteriorKey + "/ShipMeshes/SpaceshipLegsSplitMesh/" + names[i]];
            var frame = new GameObject("Landing gear " + i) { layer = _root!.layer }.transform;
            frame.SetParent(_root.transform, false);
            _gearFrames[i] = frame;
            artwork.SetParent(frame, true);
        }
    }

    internal void SetGearAmount(float amount)
    {
        GearAmount = Mathf.Clamp01(amount);
        // The source legs have baked origins rather than articulated pivots.
        // Fold each intact mesh around its measured upper attachment; the pads
        // remain partly exposed because this asset has no animated bay doors.
        var rotation = Quaternion.Euler(90 * Mathf.SmoothStep(0, 1, 1 - GearAmount), 0, 0);
        for (var i = 0; i < _gearFrames.Length; i++)
        {
            _gearFrames[i].localRotation = rotation;
            _gearFrames[i].localPosition = GearMounts[i] - rotation * GearMounts[i];
        }
    }

    internal void AddCabinCollision(PhysicMaterial material, List<BoxCollider> solids)
    {
        AddCabinDoorCollision(material, solids);
        AddAirlockSealCollision(material, solids);
        AddHullCollision(material, solids);
        AddSeatConsoleCollision(material, solids);
        AddMeshCollision(_upperDoorLeaf, _upperDoorFrame, material, solids);
        for (var i = 0; i < _innerDoorLeaves.Length; i++)
            AddMeshCollision(_innerDoorLeaves[i], _innerDoorFrames[i], material, solids);
        // Only separate solid panels and furnishings qualify. A convex hull
        // over an entire cabin, door frame or cockpit would seal its openings.
        foreach (var pair in _parts)
        {
            var path = pair.Key;
            var part = pair.Value;
            if (path == InteriorKey + "/CockpitAndCabin/CabinMeshes/SpaceshipBigMachineB")
            {
                AddNavigationCollision(material, solids);
                continue;
            }
            if (path == InteriorKey + "/CockpitAndCabin/CabinMeshes/SpaceshipNavigationMonitor")
            {
                AddEngineeringCollision(material, solids);
                continue;
            }
            var panel = part.name.StartsWith("Panel", StringComparison.Ordinal)
                && (path.Contains("/CabinWallPanels/", StringComparison.Ordinal)
                    || path.Contains("/CabinPanel0", StringComparison.Ordinal)
                    || path.Contains("/CockpitPanel0", StringComparison.Ordinal));
            var pillar = path.Contains("/CabinSupports/", StringComparison.Ordinal)
                && part.name.StartsWith("PillarB", StringComparison.Ordinal)
                || path.Contains("/CabinWallPanels/", StringComparison.Ordinal)
                    && part.name.StartsWith("PillarA", StringComparison.Ordinal);
            var cabinet = path.Contains("/CockpitMeshes/", StringComparison.Ordinal)
                    && part.name.StartsWith("SpaceshipSmallMachineA", StringComparison.Ordinal)
                || path.Contains("/CabinMeshes/", StringComparison.Ordinal)
                    && part.name is "SpaceshipBigMachineAMeshA" or "SpaceshipBigMachineAMeshB"
                        or "SpaceshipBigMachineAMeshC" or "SpaceshipBigMachineAMeshD";
            var railing = path.Contains("/CabinMeshes/", StringComparison.Ordinal)
                && part.name is "RailingAMesh (2)" or "RailingAMesh (9)" or "RailingAMesh (10)"
                || path == InteriorKey + "/Entry/EntryMeshes/RailingAMesh (4)"
                || path == InteriorKey + "/Entry/EntryMeshes/RailingAMesh (5)"
                || path == InteriorKey + "/Entry/EntryMeshes/RailingAMesh (6)"
                || path == InteriorKey + "/Entry/EntryMeshes/RailingAMesh (7)";
            var wallGrate = path.Contains("/CockpitPanel0", StringComparison.Ordinal)
                && part.name.StartsWith("FloorGrateLongMesh", StringComparison.Ordinal);
            if (!panel && !pillar && !cabinet && !railing && !wallGrate) continue;
            AddMeshCollision(part, _root!.transform, material, solids);
        }
    }

    private void AddNavigationCollision(PhysicMaterial material, List<BoxCollider> solids)
    {
        // The raised worktop and console must not fill the lower touch-screen recess.
        AddCabinBox("Navigation base", new(-.118846f, -.912842f, -.913011f),
            new(1.761388f, .729339f, 2.343362f), Quaternion.identity, material, solids);
        AddCabinBox("Navigation display", new(-.276077f, -.503465f, -1.573853f),
            new(1.014378f, .813977f, .034294f),
            Quaternion.LookRotation(new Vector3(.065039f, -.997883f, 0), Vector3.forward), material, solids);
        AddCabinBox("Navigation worktop", new(.206055f, -.369039f, -.739182f),
            new(.590709f, .358269f, .790085f), Quaternion.identity, material, solids);
        AddCabinBox("Navigation upper console", new(-.336438f, -.311687f, -.579157f),
            new(.320034f, .448234f, .979606f), Quaternion.identity, material, solids);
    }

    private void AddEngineeringCollision(PhysicMaterial material, List<BoxCollider> solids)
    {
        var origin = new Vector3(.072749f, 1.022060f, -1.120676f);
        var rotation = Quaternion.LookRotation(new Vector3(.985381f, .164644f, -.043783f),
            new Vector3(-.170366f, .952290f, -.253217f));
        // Glass is at Z zero in this frame. Keep its backing behind the display
        // and the projecting bezel outside the glass, clear of the touch plane.
        AddFrameBox("Engineering display backing", new(0, 0, .0148875f), new(1.406642f, .522975f, .029775f));
        AddFrameBox("Engineering upper bezel", new(0, .317116f, -.0113565f), new(1.406642f, .111258f, .082263f));
        AddFrameBox("Engineering lower bezel", new(.0000175f, -.278657f, -.0113565f), new(1.311053f, .034338f, .082263f));
        AddFrameBox("Engineering forward housing", new(-.841875f, .0384595f, -.0113565f), new(.27711f, .668571f, .082263f));
        AddFrameBox("Engineering aft bezel", new(.7383195f, .0384595f, -.0113565f), new(.069997f, .668571f, .082263f));
        AddFrameBox("Engineering rear mount", new(-.112009f, .1214445f, .0742495f), new(.209192f, .31331f, .089205f));
        // Auxiliary housings and suspension use their own native face axes;
        // one enclosing box would extend across the main screen and below them.
        AddCabinBox("Engineering lower panel", new(.115482f, .47625f, -1.290802f),
            new(.56142f, .366739f, .064949f), Quaternion.LookRotation(
                new Vector3(.999983f, -.00539f, .002353f), new Vector3(.005813f, .966443f, -.256816f)), material, solids);
        AddCabinBox("Engineering aft panel", new(.0155f, .658294f, -2.027034f),
            new(.466108f, .257685f, .065585f), Quaternion.LookRotation(
                new Vector3(.940977f, .080952f, -.328648f), new Vector3(-.170365f, .95229f, -.253217f)), material, solids);
        var suspensionRotation = Quaternion.LookRotation(new Vector3(-.000003f, -.256975f, -.966418f),
            new Vector3(0, .966418f, -.256975f));
        AddCabinBox("Engineering suspension", new(.242299f, 1.620577f, -1.163924f),
            new(.078985f, .529189f, .085791f), suspensionRotation, material, solids);
        AddCabinBox("Engineering upper hinge", new(.180211f, 1.95908f, -1.253933f),
            new(.169447f, .29513f, .049176f), suspensionRotation, material, solids);
        AddCabinBox("Engineering lower hinge", new(.199616f, 1.280158f, -1.073405f),
            new(.159463f, .298463f, .049176f), suspensionRotation, material, solids);
        AddCabinBox("Engineering ceiling mount", new(.138531f, 2.085951f, -1.287669f),
            new(.31331f, .089205f, .209192f), suspensionRotation, material, solids);

        void AddFrameBox(string name, Vector3 center, Vector3 size) =>
            AddCabinBox(name, origin + rotation * center, size, rotation, material, solids);
    }

    private void AddCabinBox(string name, Vector3 center, Vector3 size, Quaternion rotation,
        PhysicMaterial material, List<BoxCollider> solids)
    {
        var owner = new GameObject(name + " collision") { layer = _root!.layer };
        owner.transform.SetParent(_root.transform, false);
        owner.transform.localPosition = center;
        owner.transform.localRotation = rotation;
        var collider = owner.AddComponent<BoxCollider>();
        collider.size = size;
        collider.sharedMaterial = material;
        solids.Add(collider);
    }

    private static void AddMeshCollision(Transform part, Transform parent, PhysicMaterial material, List<BoxCollider> solids)
    {
        var filter = part.GetComponent<MeshFilter>();
        if (!filter || !filter.sharedMesh) return;
        var bounds = filter.sharedMesh.bounds;
        // Keep mirrored artwork out of the collider hierarchy. The owned ship
        // and door frames have unit scale; door collision follows its moving frame.
        var owner = new GameObject(part.name + " collision") { layer = part.gameObject.layer };
        var frame = owner.transform;
        frame.SetParent(parent, false);
        frame.SetPositionAndRotation(part.position,
            Quaternion.LookRotation(part.TransformVector(Vector3.forward), part.TransformVector(Vector3.up)));
        owner.SetActive(part.gameObject.activeInHierarchy);
        var collisionBounds = new Bounds(frame.InverseTransformPoint(part.TransformPoint(bounds.center)), Vector3.zero);
        // Project every corner to retain inherited rotations and nonuniform
        // scales. A sheared source box needs a conservative enclosing box.
        for (var corner = 0; corner < 8; corner++)
        {
            var point = bounds.center + Vector3.Scale(bounds.extents,
                new Vector3((corner & 1) == 0 ? -1 : 1, (corner & 2) == 0 ? -1 : 1, (corner & 4) == 0 ? -1 : 1));
            collisionBounds.Encapsulate(frame.InverseTransformPoint(part.TransformPoint(point)));
        }
        var collider = owner.AddComponent<BoxCollider>();
        collider.center = collisionBounds.center;
        collider.size = collisionBounds.size;
        collider.sharedMaterial = material;
        solids.Add(collider);
    }
}
