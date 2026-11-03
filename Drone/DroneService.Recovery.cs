// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientDroneService
{
    private ClientDroneContact? _lastContact;

    private static bool HasRadioContact(ClientDroneView view) => view.Deployed && view.Link
        && view.Charge > .0001f && view.Health > 0;

    private void RecordContact(string id, Vector3 position, long observedAt)
    {
        if (!Finite(position)) return;
        _lastContact = new(id, new(position.x, position.y, position.z), observedAt);
    }
}
