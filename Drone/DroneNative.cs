// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Sons.Ai.Vail.StimuliTypes;
using Sons.Weapon;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Modstoke.Client;

// Created only by the drone's simulation authority, after its physical body exists.
internal sealed class ClientDroneNative : IDisposable
{
    private const float NativeHealth = 100f;
    private readonly Action<float> _damage;
    private DamageNode? _damageNode;
    private GameObject? _interestRoot;
    private float _reportedDamage;
    private bool _disposed;

    internal ClientDroneNative(GameObject root, Action<float> damage, float initialHealth = 1f)
    {
        if (root == null)
            throw new ArgumentNullException(nameof(root));
        _damage = damage ?? throw new ArgumentNullException(nameof(damage));
        if (!float.IsFinite(initialHealth) || initialHealth < 0f || initialHealth > 1f)
            throw new ArgumentOutOfRangeException(nameof(initialHealth));
        Collider collider = root.GetComponent<Collider>();
        if (collider == null)
            throw new ArgumentException("The drone body must have a collider on its root.", nameof(root));

        try
        {
            // All compound shapes are on this same object, so they share native hit deduplication.
            // Native Start only links parent nodes; its collider field must be assigned explicitly.
            _damageNode = root.AddComponent<DamageNode>();
            _damageNode._name = "Drone";
            _damageNode._autoFindCollider = false;
            _damageNode._collider = collider;
            _damageNode.SetUseBreak(true);
            _damageNode.SetBreakAmount(NativeHealth);
            _reportedDamage = (1f - initialHealth) * NativeHealth;
            _damageNode._currentAppliedNodeDamage = _reportedDamage;
            _damageNode._isBroken = initialHealth <= 0f;
            _damageNode.enabled = initialHealth > 0f;

            _interestRoot = new GameObject("Modstoke drone interest");
            _interestRoot.SetActive(false);
            _interestRoot.transform.SetParent(root.transform, false);
            _interestRoot.AddComponent<AttractEnemyStimuli>();
            // Its native default is a valid dynamic-world stimulus. Enable/disable owns registration;
            // the native grid reads this transform. AI thought selection remains with the game.
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal void Update(bool motorsRunning)
    {
        if (_disposed)
            return;

        if (_damageNode != null)
        {
            // Read native accepted damage after its impact handler returns. Calling the flight
            // owner from a native impact callback could destroy a receiver still on that stack.
            float applied = _damageNode._currentAppliedNodeDamage;
            if (float.IsFinite(applied) && applied > _reportedDamage)
            {
                applied = Math.Min(applied, NativeHealth);
                float amount = (applied - _reportedDamage) / NativeHealth;
                _reportedDamage = applied;
                if (amount > 0f)
                    _damage(amount);
            }
        }

        if (_disposed || _interestRoot == null)
            return;
        bool active = motorsRunning && _reportedDamage < NativeHealth;
        if (_interestRoot.activeSelf != active)
            _interestRoot.SetActive(active);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_interestRoot != null)
        {
            _interestRoot.SetActive(false);
            Object.Destroy(_interestRoot);
        }
        _interestRoot = null;
        if (_damageNode != null)
        {
            _damageNode.enabled = false;
            Object.Destroy(_damageNode);
        }
        _damageNode = null;
    }
}
