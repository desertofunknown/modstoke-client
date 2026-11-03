// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Collections.Generic;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftModel
{
    private void AddHullCollision(PhysicMaterial material, List<BoxCollider> solids)
    {
        // Extrude the native BottomA, Head, RoofB and SideB faces inward by 12cm.
        // Bound individual skin faces rather than enclosing the canopy,
        // vents, gear wells or the walking space between them.
        for (var side = -1; side <= 1; side += 2)
        {
            var flank = side < 0 ? "Port " : "Starboard ";
            AddCabinBox(flank + "widened belly shoulder", new(side * 2.655f, -2.395376f, 3.13f),
                new(.56f, .12f, 3.16f), Quaternion.identity, material, solids);
            AddCabinBox(flank + "nose belly inner", new(side * 2.755f, -2.395317f, 7.15f),
                new(.76f, .12f, 4.85f), Quaternion.identity, material, solids);
            AddCabinBox(flank + "nose belly taper", new(side * 3.1314f, -2.395317f, 7.1636f),
                new(1, .12f, 4.668f), Quaternion.LookRotation(
                    new Vector3(-side * .2027f, 0, .979241f), Vector3.up), material, solids);

            var lowerChine = Quaternion.LookRotation(new Vector3(-side * .203651f, 0, .979044f),
                new Vector3(side * .457712f, -.883988f, .095209f));
            AddCabinBox(flank + "nose lower chine", new(side * 4.13467f, -2.182661f, 6.793157f),
                new(.84f, .12f, 4.05f), lowerChine, material, solids);
            AddCabinBox(flank + "nose lower chine forward", new(side * 3.533397f, -2.234087f, 9.206278f),
                new(.52f, .12f, .98f), lowerChine, material, solids);

            var upperFlank = Quaternion.LookRotation(new Vector3(-side * .10442f, -.322832f, .940679f),
                new Vector3(side * .289495f, .895025f, .339299f));
            AddCabinBox(flank + "nose upper flank", new(side * 2.871024f, -.122914f, 7.587531f),
                new(.93f, .12f, 2.2f), upperFlank, material, solids);
            AddCabinBox(flank + "nose upper flank forward", new(side * 2.507336f, -.581199f, 9.106727f),
                new(.84f, .12f, 1.07f), upperFlank, material, solids);

            // Start beyond the rectangular side vent and reduce the height
            // as the native upper edge descends toward the nose.
            var noseSide = Quaternion.LookRotation(new Vector3(-side * .217394f, 0, .976084f),
                new Vector3(side * .976083f, -.000879f, .217394f));
            AddCabinBox(flank + "nose side aft", new(side * 4.618501f, -1.569954f, 6.49853f),
                new(.82f, .12f, 1.41f), noseSide, material, solids);
            AddCabinBox(flank + "nose side forward", new(side * 4.314471f, -1.80995f, 7.862632f),
                new(.36f, .12f, 1.41f), noseSide, material, solids);

            AddCabinBox(flank + "shoulder roof", new(side * 3.232788f, 1.46876f, 2.298661f),
                new(.94f, .12f, 1.38f), Quaternion.LookRotation(
                    new Vector3(-side * .018873f, -.155357f, .987678f),
                    new Vector3(side * .119108f, .98047f, .156499f)), material, solids);
            var forwardShoulder = Quaternion.LookRotation(new Vector3(-side * .099077f, -.310518f, .94539f),
                new Vector3(side * .287373f, .900655f, .325941f));
            AddCabinBox(flank + "shoulder forward roof", new(side * 3.455272f, 1.105683f, 3.760552f),
                new(1.08f, .12f, 1.3f), forwardShoulder, material, solids);
            AddCabinBox(flank + "shoulder forward lip", new(side * 2.935453f, .874671f, 4.857205f),
                new(.67f, .12f, 1.06f), forwardShoulder, material, solids);

            AddCabinBox(flank + "shoulder outer slope", new(side * 5.258558f, .727635f, 1.13821f),
                new(1.3f, .12f, 2.05f), Quaternion.LookRotation(
                    new Vector3(-side * .253112f, -.036317f, .966755f),
                    new Vector3(side * .713753f, .667558f, .211949f)), material, solids);
            AddCabinBox(flank + "shoulder outer joint", new(side * 5.084032f, .438459f, 2.44243f),
                new(1.1f, .12f, .5f), Quaternion.LookRotation(
                    new Vector3(-side * .167984f, .039673f, .984991f),
                    new Vector3(side * .755904f, .646549f, .102873f)), material, solids);
            AddCabinBox(flank + "shoulder outer forward", new(side * 5.026593f, .23809f, 3.470561f),
                new(.75f, .12f, 1.4f), Quaternion.LookRotation(
                    new Vector3(-side * .253193f, -.035682f, .966757f),
                    new Vector3(side * .720153f, .660318f, .21298f)), material, solids);
        }

        var underside = Quaternion.LookRotation(new Vector3(.000001f, .147594f, .989048f),
            new Vector3(-.000007f, -.989048f, .147594f));
        AddCabinBox("Nose underside aft", new(0, -2.324349f, 10.015203f),
            new(6.3f, .12f, .94f), underside, material, solids);

        // Narrow separate strips stay inside the rounded nose perimeter.
        var forwardUnderside = Quaternion.LookRotation(new Vector3(0, .146538f, .989205f),
            new Vector3(0, -.989205f, .146538f));
        AddCabinBox("Nose underside middle", new(0, -2.229659f, 10.64958f),
            new(5.05f, .12f, .39f), forwardUnderside, material, solids);
        AddCabinBox("Nose underside forward", new(0, -2.170683f, 11.047694f),
            new(3.4f, .12f, .43f), forwardUnderside, material, solids);
        AddCabinBox("Nose underside tip", new(0, -2.109452f, 11.461031f),
            new(1.9f, .12f, .44f), forwardUnderside, material, solids);
    }
}
