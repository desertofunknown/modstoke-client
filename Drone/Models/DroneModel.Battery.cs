// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientDroneModel
{
    private static void BuildBattery(Geometry mesh, Vector3 origin)
    {
        // The same replaceable cassette fits the belly cradle and the inventory mat.
        mesh.BevelBox(origin + new Vector3(0, -.011f, 0), new Vector3(.158f, .019f, .218f), .006f, 0);
        mesh.BevelBox(origin + new Vector3(0, -.001f, 0), new Vector3(.160f, .003f, .220f), .001f, 1);
        mesh.BevelBox(origin + new Vector3(0, .010f, 0), new Vector3(.160f, .020f, .220f), .007f, 0);
        // Molded end shoulders protect the removable connector and latch faces.
        foreach (var z in new[] { -.095f, .095f })
            mesh.BevelBox(origin + new Vector3(0, .018f, z), new Vector3(.133f, .006f, .016f), .002f, 0);
        foreach (var sign in new[] { -1f, 1f })
        {
            // Guide feet and inset side grip strips are part of the cassette, not its cradle.
            mesh.BevelBox(origin + new Vector3(sign * .052f, -.022f, .008f),
                new Vector3(.015f, .008f, .169f), .002f, 1);
            mesh.BevelBox(origin + new Vector3(sign * .079f, .007f, -.006f),
                new Vector3(.003f, .012f, .112f), .001f, 1);
            for (var rib = -3; rib <= 3; rib++)
                mesh.Box(origin + new Vector3(sign * .081f, .007f, -.006f + rib * .014f),
                    new Vector3(.002f, .010f, .003f), 0);
            foreach (var z in new[] { -.087f, .087f })
            {
                mesh.Cylinder(origin + new Vector3(sign * .060f, .021f, z), .0047f, .001f, 1);
                Screw(mesh, origin + new Vector3(sign * .060f, .0215f, z), .003f);
            }
            // The underside slides on two shallow rails; heel stops locate the pack.
            mesh.BevelBox(origin + new Vector3(sign * .052f, -.026f, -.075f),
                new Vector3(.022f, .004f, .019f), .001f, 2);
            mesh.BevelBox(origin + new Vector3(sign * .052f, -.026f, .079f),
                new Vector3(.022f, .004f, .017f), .001f, 0);
        }

        // Raised connector rim surrounds the dark pocket; terminals sit behind the rim.
        mesh.BevelBox(origin + new Vector3(0, -.002f, .111f), new Vector3(.066f, .030f, .006f), .002f, 1);
        foreach (var sign in new[] { -1f, 1f })
        {
            mesh.BevelBox(origin + new Vector3(sign * .034f, -.002f, .114f),
                new Vector3(.006f, .030f, .012f), .0015f, 0);
            mesh.BevelBox(origin + new Vector3(0, -.002f + sign * .014f, .114f),
                new Vector3(.066f, .005f, .012f), .0015f, 0);
        }
        for (var contact = 0; contact < 5; contact++)
        {
            var power = contact == 0 || contact == 4;
            var x = -.024f + contact * .012f;
            mesh.BevelBox(origin + new Vector3(x, -.002f, .114f),
                new Vector3(power ? .010f : .007f, .021f, .003f), .001f, 0);
            mesh.BevelBox(origin + new Vector3(x, -.002f, .116f),
                new Vector3(power ? .007f : .004f, .016f, .001f), .0004f, 1);
            // Recessed split spring contacts leave their surrounding insulation visible.
            foreach (var half in new[] { -1f, 1f })
                mesh.BevelBox(origin + new Vector3(x + half * (power ? .0017f : .0009f), -.002f, .117f),
                    new Vector3(power ? .0023f : .0011f, power ? .012f : .009f, .0018f), .0003f, 2);
        }
        // Keyed socket divider makes the connection direction legible.
        mesh.Box(origin + new Vector3(.012f, .009f, .117f), new Vector3(.004f, .008f, .006f), 0);
        foreach (var sign in new[] { -1f, 1f })
            mesh.BevelBox(origin + new Vector3(sign * .039f, -.011f, .113f),
                new Vector3(.004f, .008f, .008f), .001f, 2);

        // The muted coyote release paddle is the cassette's only colored accent.
        mesh.BevelBox(origin + new Vector3(0, .005f, -.110f), new Vector3(.058f, .027f, .010f), .003f, 1);
        mesh.BevelBox(origin + new Vector3(0, .007f, -.116f), new Vector3(.046f, .018f, .011f), .003f, 3);
        for (var groove = -1; groove <= 1; groove++)
            mesh.Box(origin + new Vector3(groove * .010f, .007f, -.122f), new Vector3(.002f, .011f, .001f), 1);
        foreach (var sign in new[] { -1f, 1f })
        {
            mesh.Cylinder(origin + new Vector3(sign * .026f, .007f, -.114f), .004f, .006f, 2,
                Quaternion.Euler(0, 0, 90));
            mesh.BevelBox(origin + new Vector3(sign * .019f, -.011f, -.114f),
                new Vector3(.008f, .010f, .008f), .0015f, 2);
        }
        mesh.BevelBox(origin + new Vector3(0, -.009f, -.114f), new Vector3(.025f, .004f, .008f), .001f, 1);

        // One protected status strip and an inset test button; no unsupported charge readout.
        mesh.BevelBox(origin + new Vector3(.039f, .021f, -.031f), new Vector3(.039f, .003f, .071f), .001f, 1);
        for (var bar = 0; bar < 4; bar++)
            mesh.BevelBox(origin + new Vector3(.039f, .023f, -.050f + bar * .011f),
                new Vector3(.023f, .0015f, .004f), .0005f, 4);
        mesh.Cylinder(origin + new Vector3(.039f, .023f, -.006f), .005f, .0015f, 2);
        mesh.Ring(origin + new Vector3(.039f, .023f, -.006f), .0065f, .0055f, .0012f, 0);

        // A restrained molded identifier and serial strokes on a recessed dark plaque.
        mesh.BevelBox(origin + new Vector3(-.027f, .021f, -.019f), new Vector3(.066f, .002f, .096f), .0007f, 1);
        BatteryMark(mesh, origin + new Vector3(-.049f, .0224f, -.043f), .0026f);
        mesh.Box(origin + new Vector3(-.027f, .0224f, -.016f), new Vector3(.044f, .0006f, .0015f), 2);
        for (var line = 0; line < 4; line++)
            mesh.Box(origin + new Vector3(-.027f, .0224f, -.003f + line * .007f),
                new Vector3(.039f - line * .006f, .0006f, .001f), 2);
        // Positive terminal mark is molded beside the connector rather than a bright full-body stripe.
        mesh.Box(origin + new Vector3(-.024f, .0205f, .082f), new Vector3(.009f, .001f, .002f), 2);
        mesh.Box(origin + new Vector3(-.024f, .0205f, .082f), new Vector3(.002f, .001f, .009f), 2);
        mesh.Box(origin + new Vector3(.024f, .0205f, .082f), new Vector3(.009f, .001f, .002f), 2);
    }

    private static void BatteryMark(Geometry mesh, Vector3 origin, float pitch)
    {
        // Fine, continuous strokes sit flush with the plaque like an etched infill.
        // The original character spacing, reading direction and extent are retained.
        void Stroke(float offset, params Vector2[] points)
        {
            for (var i = 0; i < points.Length - 1; i++)
            {
                var a = points[i];
                var b = points[i + 1];
                mesh.Beam(origin + new Vector3((offset + a.x) * pitch, -.00034f, -a.y * pitch),
                    origin + new Vector3((offset + b.x) * pitch, -.00034f, -b.y * pitch),
                    pitch * .25f, .00012f, 2);
            }
        }
        Stroke(0, new Vector2(0, .45f), new Vector2(.35f, 0), new Vector2(1.65f, 0),
            new Vector2(2, .35f), new Vector2(2, 1.25f), new Vector2(1.75f, 1.7f),
            new Vector2(0, 4), new Vector2(2.2f, 4));
        Stroke(4, new Vector2(1.65f, 0), new Vector2(0, 2.7f), new Vector2(2.2f, 2.7f));
        Stroke(4, new Vector2(1.65f, 0), new Vector2(1.65f, 4));
        Stroke(8, new Vector2(0, 0), new Vector2(1, 4), new Vector2(2, 0));
    }
}
