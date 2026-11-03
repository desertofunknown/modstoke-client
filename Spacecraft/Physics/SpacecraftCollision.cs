// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.Injection;
using Sons.Ai.Vail;
using Sons.Weapon;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Client;

public sealed class ClientSpacecraftCollisionRelay : MonoBehaviour
{
    private static readonly Dictionary<int, ClientSpacecraftBody> Owners = new();
    private static bool _registered;

    public ClientSpacecraftCollisionRelay(IntPtr pointer) : base(pointer) { }

    [HideFromIl2Cpp]
    internal static void Attach(GameObject root, ClientSpacecraftBody owner)
    {
        if (!_registered)
        {
            ClassInjector.RegisterTypeInIl2Cpp<ClientSpacecraftCollisionRelay>();
            _registered = true;
        }
        Owners[root.GetInstanceID()] = owner;
        try { root.AddComponent<ClientSpacecraftCollisionRelay>(); }
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

internal sealed partial class ClientSpacecraftBody
{
    private const float GentleContactSpeed = 3.2f;
    private const float MaximumImpactEnergy = 6000000;
    private readonly int _damageThread = Environment.CurrentManagedThreadId;
    private readonly List<Transform> _ignoredContactRoots = new();
    private readonly Dictionary<IntPtr, WeaponReceiver> _weaponReceivers = new();
    private readonly Dictionary<int, WeaponImpact> _weaponImpacts = new();
    private readonly WeaponReceipt[] _weaponReceipts = new WeaponReceipt[32];
    private Il2CppSystem.Action<IImpactSender, IImpactData>? _weaponCallback;
    private int _nextWeaponReceipt;
    private float _impactAt = -1, _impactEnergy, _pendingImpactEnergy;
    private readonly ImpactReport[] _pendingHullImpacts = new ImpactReport[3];
    private readonly CollisionHit[] _pendingCollisions = new CollisionHit[32];
    private int _pendingCollisionCount;

    // Delivered on the owning Unity thread after native collision/weapon callbacks
    // return. Position is ship-local; energy is joules (weapon conversion is tuning).
    internal event Action<float, Vector3>? Impact;
    // Actual opposite collider and world contact point; never inferred from bounds.
    internal event Action<Collider, Vector3, float>? CollisionImpact;

    internal void IgnoreContactRoot(Transform root)
    {
        if (!Ready || !root || Environment.CurrentManagedThreadId != _damageThread) return;
        foreach (var ignored in _ignoredContactRoots)
            if (ignored && ignored.Pointer == root.Pointer) return;
        _ignoredContactRoots.Add(root);
    }

    private bool IgnoreContact(Transform other)
    {
        if (other.IsChildOf(Root.transform)) return true;
        var player = LocalPlayer.Transform;
        if (player && other.IsChildOf(player)) return true;
        if (other.GetComponentInParent<BoltPlayerSetup>()) return true;
        foreach (var ignored in _ignoredContactRoots)
            if (ignored && other.IsChildOf(ignored)) return true;
        return false;
    }

    internal void Contact(Collision collision)
    {
        if (ServiceSuspended || !Ready || !_authoritative || Environment.CurrentManagedThreadId != _damageThread
            || collision == null || collision.WasCollected || !collision.collider
            || IgnoreContact(collision.collider.transform)) return;
        // The Collision.impulse accessor is unavailable in this IL2CPP player;
        // the native contact pair exposes the impulse used by the solver.
        var relative = collision.m_Header.m_RelativeVelocity;
        var impulse = collision.m_Pair.ImpulseSum;
        if (!Finite(relative) || !Finite(impulse)) return;
        var normalSpeed = 0f;
        var hitPoint = Vector3.zero;
        for (var i = 0; i < Math.Min(collision.contactCount, 8); i++)
        {
            var contact = collision.GetContact(i);
            if (!Finite(contact.point) || !Finite(contact.normal)) continue;
            var approach = MathF.Abs(Vector3.Dot(relative, contact.normal));
            if (approach <= normalSpeed) continue;
            normalSpeed = approach;
            hitPoint = contact.point;
        }
        var deltaVelocity = MathF.Min(impulse.magnitude / Mass, normalSpeed);
        if (!float.IsFinite(deltaVelocity) || deltaVelocity <= GentleContactSpeed) return;
        var energy = Math.Clamp(.5f * Mass * (deltaVelocity * deltaVelocity
            - GentleContactSpeed * GentleContactSpeed), 0, MaximumImpactEnergy);
        var now = Time.fixedTime;
        if (now - _impactAt >= .15f) _impactEnergy = 0;
        _impactAt = now;
        if (energy <= _impactEnergy) return;
        // Compound contacts and sustained solver support are one impact episode.
        float added = MathF.Min(energy - _impactEnergy, MaximumImpactEnergy - _pendingImpactEnergy);
        _pendingImpactEnergy += added;
        if (added > 0)
        {
            var point = Root.transform.InverseTransformPoint(hitPoint);
            var compartment = (int)ClientSpacecraftSystems.HullCompartment(point.z);
            var pending = _pendingHullImpacts[compartment];
            // Keep the energy-weighted point sum until the compartment is drained.
            _pendingHullImpacts[compartment] = new(pending.Energy + added, pending.Point + point * added);
        }
        // Overflow drops structural reports rather than assigning their energy
        // to another surface. Ship damage still retains the bounded aggregate.
        if (added > 0 && _pendingCollisionCount < _pendingCollisions.Length)
            _pendingCollisions[_pendingCollisionCount++] = new(collision.collider, hitPoint, added);
        _impactEnergy = energy;
    }

    private void AttachDamage()
    {
        ClientSpacecraftCollisionRelay.Attach(Root, this);
        _weaponCallback = (Action<IImpactSender, IImpactData>)CaptureWeaponImpact;
        var owners = new HashSet<int>();
        foreach (var collider in _solids)
            AttachDamageReceiver(collider, owners);
        foreach (var collider in _walkSurfaces)
            AttachDamageReceiver(collider, owners);
    }

    private void AttachDamageReceiver(Collider collider, HashSet<int> owners)
    {
        if (!owners.Add(collider.gameObject.GetInstanceID())) return;
        var node = collider.gameObject.AddComponent<DamageNode>();
        var receiver = new WeaponReceiver(node);
        _weaponReceivers.Add(node.Pointer, receiver);
        node._name = "Spacecraft hull";
        node._autoFindCollider = false;
        node._collider = collider;
        node.SetUseBreak(true);
        // DamageNode supplies native hit admission and duplicate-hit handling;
        // the systems computer owns structural health and destruction.
        node.SetBreakAmount(1000000);
        node._takeDamageAfterBreak = true;
        node.RegisterImpactCallback(_weaponCallback!);
        receiver.Registered = true;
    }

    private void CaptureWeaponImpact(IImpactSender sender, IImpactData data)
    {
        if (!Ready || !_authoritative || Environment.CurrentManagedThreadId != _damageThread
            || sender == null || sender.WasCollected
            || !_weaponReceivers.TryGetValue(sender.Pointer, out var receiver) || !receiver.Node) return;
        // Native HandleImpact has already applied this hit. Its callback sender
        // is the receiving node; the attacker is data.Sender.
        var applied = receiver.Node._currentAppliedNodeDamage;
        if (!float.IsFinite(applied) || applied < 0) return;
        var damage = applied - receiver.Reported;
        receiver.Reported = applied;
        // Consume native deltas during the hold without carrying them into the next hit.
        if (ServiceSuspended || damage <= 0 || data == null || data.WasCollected) return;
        var point = data.Center;
        if (!Finite(point)) return;
        var source = CaptureWeaponSource(data);
        if (!float.IsFinite(source.StructureDamageMultiplier) || source.StructureDamageMultiplier < 0
            || !float.IsFinite(source.SiegeDamageFactor) || source.SiegeDamageFactor < 0) return;
        var id = data.GetUniqueId();
        if (_weaponImpacts.TryGetValue(id, out var previous))
        {
            if (previous.Source == source && damage > previous.Damage)
                _weaponImpacts[id] = previous with { Damage = damage, Point = Root.transform.InverseTransformPoint(point) };
            return;
        }
        if (_weaponImpacts.Count >= 32) return;
        _weaponImpacts.Add(id, new WeaponImpact(damage, Root.transform.InverseTransformPoint(point), source));
    }

    private static WeaponSource CaptureWeaponSource(IImpactData data)
    {
        var sender = data.Sender;
        var attacker = IntPtr.Zero;
        var actorType = -1;
        if (sender != null && !sender.WasCollected)
        {
            attacker = sender.Pointer;
            var root = sender.GetRootTransform();
            var actor = root ? root.GetComponentInParent<VailActor>() : null;
            if (!actor)
            {
                var stimuli = sender.GetSourceStimuli();
                if (stimuli) actor = stimuli.GetComponentInParent<VailActor>();
            }
            if (actor != null && actor)
                actorType = (int)actor.TypeId;
        }
        return new WeaponSource(attacker, data.IsPlayer, data.IsPlayerTrap, data.IsDemonic, data.ImpactType,
            data.MeleeType, actorType, data.StructureDamageMult, data.SiegeDamageFactor);
    }

    private void ApplyPendingDamage()
    {
        if (ServiceSuspended || Environment.CurrentManagedThreadId != _damageThread) return;
        if (_pendingImpactEnergy > 0)
        {
            // Snapshot every compartment before dispatch: a handler can dispose
            // the ship or queue another impact without changing this batch.
            Span<ImpactReport> impacts = stackalloc ImpactReport[3];
            _pendingHullImpacts.AsSpan().CopyTo(impacts);
            Array.Clear(_pendingHullImpacts);
            _pendingImpactEnergy = 0;
            foreach (var impact in impacts)
            {
                if (impact.Energy <= 0) continue;
                Impact?.Invoke(impact.Energy, impact.Point / impact.Energy);
                if (!Ready) return;
            }
        }
        int collisionCount = _pendingCollisionCount;
        _pendingCollisionCount = 0;
        for (int i = 0; i < collisionCount; i++)
        {
            var hit = _pendingCollisions[i];
            _pendingCollisions[i] = default;
            if (Ready && hit.Collider) CollisionImpact?.Invoke(hit.Collider, hit.Point, hit.Energy);
        }
        SynchronizeDamageReceivers();
        var now = Time.fixedTime;
        ImpactReport report = default;
        foreach (var pair in _weaponImpacts)
        {
            var damage = pair.Value.Damage;
            if (damage <= 0) continue;
            var receipt = -1;
            for (var i = 0; i < _weaponReceipts.Length; i++)
                if (_weaponReceipts[i].Used && _weaponReceipts[i].Id == pair.Key && now - _weaponReceipts[i].At < 2)
                { receipt = i; break; }
            if (receipt >= 0 && _weaponReceipts[receipt].Source != pair.Value.Source) continue;
            var previous = receipt < 0 ? 0 : _weaponReceipts[receipt].Damage;
            if (damage <= previous) continue;
            if (receipt < 0) { receipt = _nextWeaponReceipt; _nextWeaponReceipt = (receipt + 1) % _weaponReceipts.Length; }
            _weaponReceipts[receipt] = new WeaponReceipt(pair.Key, now, damage, pair.Value.Source, true);
            // Native damage units have no physical energy conversion. This
            // gameplay scale keeps ordinary weapons below heavy hull impacts.
            var source = pair.Value.Source;
            // Fourfold hull resistance is gameplay tuning for resolved ordinary
            // NPC melee, not native armor. Positive siege capability stays at
            // its existing scale; the native node delta is already applied.
            var ordinaryNpcMelee = source.ActorType >= 0 && !source.IsPlayer && !source.IsPlayerTrap && !source.IsDemonic
                && source.Type == ImpactType.Melee
                && source.MeleeType is ImpactMeleeType.None or ImpactMeleeType.Axe
                    or ImpactMeleeType.Sharp or ImpactMeleeType.Blunt
                && source.SiegeDamageFactor == 0;
            var scale = ordinaryNpcMelee ? .25f : 1;
            var energy = MathF.Min(MaximumImpactEnergy, 20000 + (damage - previous) * 3000 * scale);
            // Parent nodes can include several child hits in one accepted delta.
            // Keep the strongest report for this step instead of adding that
            // aggregate to the individual child reports a second time.
            if (energy > report.Energy) report = new ImpactReport(energy, pair.Value.Point);
        }
        _weaponImpacts.Clear();
        if (report.Energy > 0 && Ready) Impact?.Invoke(report.Energy, report.Point);
    }

    private void SynchronizeDamageReceivers()
    {
        foreach (var receiver in _weaponReceivers.Values)
        {
            var node = receiver.Node;
            if (!node) continue;
            var applied = node._currentAppliedNodeDamage;
            // Consume unclassified native changes without admitting them as a
            // new hit or assigning them to the next callback's attack metadata.
            if (float.IsFinite(applied) && applied >= 0) receiver.Reported = applied;
        }
    }

    private void DisposeDamage()
    {
        if (Root) ClientSpacecraftCollisionRelay.Detach(Root);
        Exception? failure = null;
        foreach (var receiver in _weaponReceivers.Values)
        {
            try
            {
                if (!receiver.Node) continue;
                receiver.Node.enabled = false;
                if (receiver.Registered && _weaponCallback != null) receiver.Node.UnregisterImpactCallback(_weaponCallback);
                UnityEngine.Object.Destroy(receiver.Node);
            }
            catch (Exception error) { failure ??= error; }
        }
        _weaponReceivers.Clear();
        _weaponImpacts.Clear();
        _ignoredContactRoots.Clear();
        _weaponCallback = null;
        Impact = null;
        CollisionImpact = null;
        Array.Clear(_pendingHullImpacts);
        _pendingImpactEnergy = 0;
        Array.Clear(_pendingCollisions);
        _pendingCollisionCount = 0;
        if (failure != null) Console.WriteLine("Spacecraft damage receiver cleanup failed: " + failure);
    }

    private sealed class WeaponReceiver(DamageNode node)
    {
        internal readonly DamageNode Node = node;
        internal float Reported;
        internal bool Registered;
    }

    private readonly record struct WeaponSource(IntPtr Attacker, bool IsPlayer, bool IsPlayerTrap, bool IsDemonic,
        ImpactType Type, ImpactMeleeType MeleeType, int ActorType,
        float StructureDamageMultiplier, float SiegeDamageFactor);
    private readonly record struct WeaponImpact(float Damage, Vector3 Point, WeaponSource Source);
    private readonly record struct CollisionHit(Collider Collider, Vector3 Point, float Energy);
    private readonly record struct WeaponReceipt(int Id, float At, float Damage, WeaponSource Source, bool Used);
    private readonly record struct ImpactReport(float Energy, Vector3 Point);
}
