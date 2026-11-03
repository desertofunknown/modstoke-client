// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftModel
{
    private readonly Transform[] _seatConsoleHousings = new Transform[2];
    private readonly BoxCollider?[] _seatConsoleSolids = new BoxCollider?[3];
    private readonly Transform?[] _seatConsoleKeys = new Transform?[2 * SpacecraftSeatConsole.ButtonCount];
    private readonly Transform?[] _seatConsoleContacts = new Transform?[2 * SpacecraftSeatConsole.ButtonCount];
    private readonly Vector3[] _seatConsoleRestPositions = new Vector3[2 * SpacecraftSeatConsole.ButtonCount];
    private readonly SpacecraftConsolePose[] _seatConsolePoses = new SpacecraftConsolePose[2];
    private readonly int[] _pressedSeatConsoleKeys = { -1, -1 };
    private readonly ulong[] _pressedSeatConsoleActors = new ulong[2];
    private readonly float[] _seatConsolePress = new float[2];

    internal Transform? SeatConsoleContact(int seat, SpacecraftConsoleControl kind)
    {
        var index = SpacecraftSeatConsole.Index(kind);
        return seat is < 0 or > 1 || index < 0 ? null
            : _seatConsoleContacts[seat * SpacecraftSeatConsole.ButtonCount + index];
    }

    private void InitSeatConsoles(Material material)
    {
        var housingMesh = LoadSeatConsoleMesh("housing");
        var keyMesh = LoadSeatConsoleMesh("key");
        for (var seat = 0; seat < 2; seat++)
        {
            var seatRotation = SpacecraftLayout.SeatRotation(seat);
            var housing = AddMeshPart("Seat " + seat + " console", housingMesh, material, SeatFrame(seat),
                SpacecraftLayout.Seat(seat) + seatRotation * SpacecraftSeatConsole.MountOffset,
                seatRotation * SpacecraftSeatConsole.PanelRotation);
            _seatConsoleHousings[seat] = housing;
            for (var button = 0; button < SpacecraftSeatConsole.ButtonCount; button++)
            {
                var index = seat * SpacecraftSeatConsole.ButtonCount + button;
                var title = SpacecraftSeatConsole.Buttons[button].Title;
                var position = SpacecraftSeatConsole.Position(button);
                var key = AddMeshPart(title + " key", keyMesh, material, housing, position, Quaternion.identity);
                _seatConsoleKeys[index] = key;
                _seatConsoleRestPositions[index] = position;
                var contact = new GameObject(title + " contact") { layer = key.gameObject.layer }.transform;
                contact.SetParent(key, false);
                // Picking uses +X right, +Y along the panel, and +Z into the key face.
                contact.localRotation = Quaternion.LookRotation(Vector3.down, Vector3.forward);
                _seatConsoleContacts[index] = contact;
            }
        }
    }

    internal void SetConsolePoses(SpacecraftConsolePose pilot, SpacecraftConsolePose copilot)
    {
        _seatConsolePoses[0] = pilot;
        _seatConsolePoses[1] = copilot;
    }

    private void UpdateSeatConsoles(float dt)
    {
        for (var seat = 0; seat < _seatConsolePoses.Length; seat++)
        {
            var pose = _seatConsolePoses[seat];
            var button = SpacecraftSeatConsole.Index(pose.Control);
            var active = pose.Active && button >= 0 ? seat * SpacecraftSeatConsole.ButtonCount + button : -1;
            var previous = _pressedSeatConsoleKeys[seat];
            if (active != previous || pose.Actor != _pressedSeatConsoleActors[seat])
            {
                if (previous >= 0)
                    _seatConsoleKeys[previous]!.localPosition = _seatConsoleRestPositions[previous];
                _pressedSeatConsoleKeys[seat] = active;
                _pressedSeatConsoleActors[seat] = pose.Actor;
                _seatConsolePress[seat] = 0;
            }
            if (active < 0) continue;
            // Key travel starts at fingertip contact, not during the arm's approach.
            var target = pose.Reach >= .999f ? SpacecraftSeatConsole.KeyTravel : 0;
            var before = _seatConsolePress[seat];
            _seatConsolePress[seat] = Mathf.MoveTowards(before, target, dt * .012f);
            if (_seatConsolePress[seat] != before)
                _seatConsoleKeys[active]!.localPosition = _seatConsoleRestPositions[active]
                    - Vector3.up * _seatConsolePress[seat];
        }
    }

    private Mesh LoadSeatConsoleMesh(string asset)
    {
        using var stream = typeof(ClientSpacecraftModel).Assembly.GetManifestResourceStream(
            "Modstoke.Client.Assets.spacecraft-console-" + asset + ".msa")
            ?? throw new InvalidOperationException("The seat console asset is missing: " + asset);
        var data = SpacecraftMeshData.Read(stream, asset);
        return CreateControlMesh(data, "Seat console " + asset);
    }

    private void AddSeatConsoleCollision(PhysicMaterial material, List<BoxCollider> solids)
    {
        // The tray overhangs the native chair. Cover just its case, retaining
        // the open space beneath the arm and the existing chair collision.
        for (var seat = 0; seat < _seatConsoleHousings.Length; seat++)
        {
            var solid = _seatConsoleHousings[seat].gameObject.AddComponent<BoxCollider>();
            solid.center = new Vector3(0, -.010f, 0);
            solid.size = new Vector3(.170f, .030f, .250f);
            solid.sharedMaterial = material;
            _seatConsoleSolids[seat] = solid;
            solids.Add(solid);
        }
        // The angled copilot mount extends past its native chair's side box.
        var support = _seatConsoleHousings[1].gameObject.AddComponent<BoxCollider>();
        support.center = new Vector3(-.0202f, -.0791f, -.0414f);
        support.size = new Vector3(.022f, .117f, .212f);
        support.sharedMaterial = material;
        _seatConsoleSolids[2] = support;
        solids.Add(support);
    }

    internal bool IsSeatConsoleCollider(Collider collider, int seat)
    {
        if (seat is < 0 or > 1 || !collider) return false;
        var tray = _seatConsoleSolids[seat];
        var support = seat == 1 ? _seatConsoleSolids[2] : null;
        return tray != null && tray && collider.Pointer == tray.Pointer
            || support != null && support && collider.Pointer == support.Pointer;
    }

    private void ReleaseSeatConsoles()
    {
        Array.Clear(_seatConsoleKeys);
        Array.Clear(_seatConsoleContacts);
        Array.Clear(_seatConsoleHousings);
        Array.Clear(_seatConsoleSolids);
        Array.Clear(_seatConsolePoses);
        Array.Fill(_pressedSeatConsoleKeys, -1);
        Array.Clear(_pressedSeatConsoleActors);
        Array.Clear(_seatConsolePress);
    }
}
