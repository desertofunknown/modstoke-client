// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace Modstoke.Client;

public sealed class ClientAircraftCollisionRelay : MonoBehaviour
{
    private static readonly Dictionary<int, ClientAircraftBody> Owners = new();
    private static bool _registered;

    public ClientAircraftCollisionRelay(IntPtr pointer) : base(pointer) { }

    [HideFromIl2Cpp]
    internal static void Attach(GameObject root, ClientAircraftBody owner)
    {
        if (!_registered)
        {
            ClassInjector.RegisterTypeInIl2Cpp<ClientAircraftCollisionRelay>();
            _registered = true;
        }
        Owners[root.GetInstanceID()] = owner;
        try { root.AddComponent<ClientAircraftCollisionRelay>(); }
        catch { Owners.Remove(root.GetInstanceID()); throw; }
    }

    [HideFromIl2Cpp]
    internal static void Detach(GameObject root) => Owners.Remove(root.GetInstanceID());

    private void OnCollisionEnter(Collision collision)
    {
        if (Owners.TryGetValue(gameObject.GetInstanceID(), out var body)) body.Contact(collision);
    }

    private void OnCollisionStay(Collision collision)
    {
        if (Owners.TryGetValue(gameObject.GetInstanceID(), out var body)) body.Contact(collision);
    }

    private void OnDestroy() => Owners.Remove(gameObject.GetInstanceID());
}
