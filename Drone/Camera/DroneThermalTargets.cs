// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Il2CppInterop.Runtime.InteropTypes;
using Sons.Ai.Vail;
using TheForest.Utils;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Modstoke.Client;

// The vision owner calls this on the Unity thread and handles native-boundary failures.
internal sealed class ClientDroneThermalTargets
{
    private const float RefreshInterval = 0.5f;
    private const int MaximumRegistryEntries = 256;
    private const int MaximumTargets = 64;
    private const int MaximumHierarchyEntries = 128;
    private const int MaximumHierarchyWork = 4096;
    private const int MaximumRenderers = 256;
    private const int MaximumDraws = 512;
    private readonly List<Target> _targets = new(MaximumTargets);
    private readonly HashSet<int> _roots = new();
    private readonly List<Transform> _bodyNodes = new(MaximumHierarchyEntries);
    private readonly List<Transform> _networkNodes = new(MaximumHierarchyEntries);
    private Camera? _camera;
    private GameObject? _localRoot;
    private float _nextRefresh;
    private int _hierarchyWork;
    private int _rendererCount;

    internal void Refresh(Camera camera)
    {
        float now = Time.unscaledTime;
        if (!Live(camera) || !Live(LocalPlayer.GameObject))
        {
            Clear();
            return;
        }
        if (!Same(_camera, camera) || !Same(_localRoot, LocalPlayer.GameObject))
            Clear();
        _camera = camera;
        _localRoot = LocalPlayer.GameObject;
        if (now < _nextRefresh && now >= _nextRefresh - RefreshInterval)
            return;
        _nextRefresh = now + RefreshInterval;
        _targets.Clear();
        _roots.Clear();
        _hierarchyWork = 0;
        _rendererCount = 0;

        if (Live(_localRoot) && Live(LocalPlayer.Stats))
            AddTarget(new Target(_localRoot!, LocalPlayer.Stats));

        var tracker = PlayerTracker._instance;
        if (Live(tracker))
        {
            var players = tracker.AllPlayerEntities;
            if (Native(players))
            {
                int count = Math.Min(players.Count, MaximumRegistryEntries);
                for (int i = 0; i < count && i < players.Count; i++)
                    AddPlayer(players[i]);
            }
        }

        var manager = VailActorManager._instance;
        if (Live(manager))
        {
            var actors = manager._activeActors;
            if (Native(actors))
            {
                int count = Math.Min(actors.Count, MaximumRegistryEntries);
                for (int i = 0; i < count && i < actors.Count; i++)
                    AddActor(actors[i]);
            }
        }

        // The active-AI list may omit client replicas. Only inspect attached registry roots.
        if (BoltNetwork.isRunning)
        {
            var entries = BoltNetwork.entities.GetEnumerator();
            var cursor = entries.Cast<Il2CppSystem.Collections.IEnumerator>();
            try
            {
                int inspected = 0;
                while (inspected++ < MaximumRegistryEntries && _targets.Count < MaximumTargets && cursor.MoveNext())
                {
                    var entity = entries.Current;
                    if (!Live(entity) || !entity.isAttached)
                        continue;
                    if (entity.prefabId == BoltPrefabs.PlayerNetwork)
                    {
                        AddPlayer(entity);
                        continue;
                    }
                    if (InRange(entity.gameObject))
                        FindNetworkActors(entity);
                }
            }
            finally { entries.Cast<Il2CppSystem.IDisposable>().Dispose(); }
        }
        _bodyNodes.Clear();
        _networkNodes.Clear();
    }

    internal void Clear()
    {
        _targets.Clear();
        _roots.Clear();
        _bodyNodes.Clear();
        _networkNodes.Clear();
        _camera = null;
        _localRoot = null;
        _nextRefresh = 0f;
        _rendererCount = 0;
        _hierarchyWork = 0;
    }

    internal void Draw(CommandBuffer cmd, Material material, int shaderPass)
    {
        if (!Live(_camera) || !Same(_localRoot, LocalPlayer.GameObject))
            return;
        int draws = 0;
        foreach (var target in _targets)
        {
            if (!target.Current || !InRange(target.Root))
                continue;
            foreach (var part in target.Parts)
            {
                var renderer = part.Renderer;
                if (!Live(renderer) || !renderer.enabled || renderer.forceRenderingOff
                    || !renderer.gameObject.activeInHierarchy || !Live(part.Mesh)
                    || !renderer.transform.IsChildOf(target.Root.transform)
                    || (_camera!.cullingMask & (1 << renderer.gameObject.layer)) == 0)
                    continue;
                // A mesh swap invalidates cached submesh counts until the next membership refresh.
                var currentMesh = Live(part.Skin) ? part.Skin!.sharedMesh
                    : Live(part.Filter) ? part.Filter!.sharedMesh : null;
                if (!Same(currentMesh, part.Mesh) || currentMesh!.subMeshCount != part.Submeshes)
                    continue;
                for (int submesh = 0; submesh < part.Submeshes; submesh++)
                {
                    if (draws++ >= MaximumDraws)
                        return;
                    cmd.DrawRenderer(renderer, material, submesh, shaderPass);
                }
            }
        }
    }

    private void AddPlayer(BoltEntity entity)
    {
        if (!Live(entity) || !entity.isAttached || entity.prefabId != BoltPrefabs.PlayerNetwork
            || Same(entity.gameObject, _localRoot))
            return;
        var state = entity.GetState<IPlayerState>();
        if (Native(state))
            AddTarget(new Target(entity, state));
    }

    private void AddActor(VailActor actor)
    {
        if (!Live(actor) || (actor.ClassId is not (VailActorClassId.Cannibal
            or VailActorClassId.Creepy or VailActorClassId.Animal)
            && actor._id is not (VailActorTypeId.Robby or VailActorTypeId.Virginia)))
            return;
        AddTarget(new Target(actor));
    }

    private void FindNetworkActors(BoltEntity entity)
    {
        _networkNodes.Clear();
        _networkNodes.Add(entity.transform);
        for (int i = 0; i < _networkNodes.Count && _hierarchyWork < MaximumHierarchyWork; i++)
        {
            var node = _networkNodes[i];
            _hierarchyWork++;
            if (!Live(node) || !node.gameObject.activeInHierarchy)
                continue;
            var nested = node.GetComponent<BoltEntity>();
            if (Live(nested) && !Same(nested, entity))
                continue;
            var network = node.GetComponent<VailActorNetworked>();
            if (Live(network) && Same(network._entity, entity) && Live(network._vailActor)
                && Same(network._vailActor._actorNetworked, network)
                && network._vailActor.transform.IsChildOf(entity.transform))
                AddActor(network._vailActor);
            AddChildren(node, _networkNodes);
        }
    }

    private void AddTarget(Target target)
    {
        if (_targets.Count >= MaximumTargets || _rendererCount >= MaximumRenderers
            || !target.Current || !InRange(target.Root) || !_roots.Add(target.RootId))
            return;
        _bodyNodes.Clear();
        _bodyNodes.Add(target.Root.transform);
        bool skinned = false;
        for (int i = 0; i < _bodyNodes.Count && _hierarchyWork < MaximumHierarchyWork; i++)
        {
            var node = _bodyNodes[i];
            _hierarchyWork++;
            if (!Live(node) || !node.gameObject.activeInHierarchy || Excluded(node.name))
                continue;
            var nested = node.GetComponent<BoltEntity>();
            if (i != 0 && Live(nested) && !Same(nested, target.Entity))
                continue;
            var renderer = node.GetComponent<Renderer>();
            if (Live(renderer))
            {
                var skin = renderer.TryCast<SkinnedMeshRenderer>();
                var filter = skin == null && renderer.TryCast<MeshRenderer>() != null
                    ? node.GetComponent<MeshFilter>() : null;
                var mesh = Live(skin) ? skin!.sharedMesh : Live(filter) ? filter!.sharedMesh : null;
                if (Live(mesh) && mesh!.subMeshCount > 0 && mesh.subMeshCount <= 16)
                {
                    // Prefer body skins even if earlier static attachments used the per-root budget.
                    if (Live(skin) && !skinned)
                    {
                        _rendererCount -= target.Parts.Count;
                        target.Parts.Clear();
                        skinned = true;
                    }
                    if ((!skinned || Live(skin)) && target.Parts.Count < 16 && _rendererCount < MaximumRenderers)
                    {
                        target.Parts.Add(new Part(renderer, skin, filter, mesh, mesh.subMeshCount));
                        _rendererCount++;
                    }
                }
            }
            AddChildren(node, _bodyNodes);
        }
        if (target.Parts.Count != 0)
            _targets.Add(target);
    }

    private bool InRange(GameObject root)
    {
        if (!Live(root) || !Live(_camera))
            return false;
        float range = Math.Min(_camera!.farClipPlane, 350f);
        float distance = (root.transform.position - _camera.transform.position).sqrMagnitude;
        return float.IsFinite(distance) && distance <= range * range;
    }

    private static void AddChildren(Transform node, List<Transform> nodes)
    {
        int count = Math.Min(node.childCount, MaximumHierarchyEntries - nodes.Count);
        for (int i = 0; i < count; i++)
            nodes.Add(node.GetChild(i));
    }

    private static bool Excluded(string name)
    {
        return name.Contains("firstperson", StringComparison.OrdinalIgnoreCase)
            || name.Contains("first_person", StringComparison.OrdinalIgnoreCase)
            || name.Contains("first person", StringComparison.OrdinalIgnoreCase)
            || name.Contains("weapon", StringComparison.OrdinalIgnoreCase)
            || name.Contains("equipment", StringComparison.OrdinalIgnoreCase)
            || name.Contains("inventory", StringComparison.OrdinalIgnoreCase)
            || name.Contains("held", StringComparison.OrdinalIgnoreCase)
            || name.Contains("armor", StringComparison.OrdinalIgnoreCase)
            || name.Contains("armour", StringComparison.OrdinalIgnoreCase)
            || name.Contains("effect", StringComparison.OrdinalIgnoreCase)
            || name.Contains("particle", StringComparison.OrdinalIgnoreCase)
            || name.Contains("trail", StringComparison.OrdinalIgnoreCase)
            || name.Contains("camera", StringComparison.OrdinalIgnoreCase);
    }

    private static bool Native([NotNullWhen(true)] Il2CppObjectBase? value)
        => value is not null && !value.WasCollected;
    private static bool Live([NotNullWhen(true)] Object? value) => Native(value) && value;
    private static bool Same(Il2CppObjectBase? left, Il2CppObjectBase? right)
        => Native(left) && Native(right) && left!.Pointer == right!.Pointer;

    private sealed class Target
    {
        internal readonly GameObject Root;
        internal readonly int RootId;
        internal readonly BoltEntity? Entity;
        internal readonly List<Part> Parts = new(16);
        private readonly PlayerStats? _stats;
        private readonly IPlayerState? _state;
        private readonly VailActor? _actor;
        private readonly VailActorNetworked? _network;
        private readonly Bolt.Entity? _attachment;
        private readonly ulong _networkId;
        private readonly int _actorId;
        private readonly int _actorType;
        private readonly int _worldSimId;

        internal Target(GameObject root, PlayerStats stats)
        {
            Root = root;
            RootId = root.GetInstanceID();
            _stats = stats;
        }

        internal Target(BoltEntity entity, IPlayerState state)
        {
            Root = entity.gameObject;
            RootId = Root.GetInstanceID();
            Entity = entity;
            _state = state;
            _attachment = entity._entity;
            _networkId = entity.networkId.PackedValue;
        }

        internal Target(VailActor actor)
        {
            Root = actor.gameObject;
            RootId = Root.GetInstanceID();
            _actor = actor;
            _actorId = actor.GetInstanceID();
            _actorType = (int)actor._id;
            _worldSimId = actor._worldSimUniqueId;
            _network = actor._actorNetworked;
            if (Live(_network))
            {
                Entity = _network!._entity;
                if (Live(Entity) && Entity!.isAttached)
                {
                    _attachment = Entity._entity;
                    _networkId = Entity.networkId.PackedValue;
                }
            }
        }

        internal bool Current
        {
            get
            {
                if (!Live(Root) || Root.GetInstanceID() != RootId || !Root.activeInHierarchy)
                    return false;
                if (_stats is not null)
                    return Live(_stats) && Same(LocalPlayer.Stats, _stats)
                        && Same(LocalPlayer.GameObject, Root) && (int)_stats._dyingState is 0 or 1;
                if (Entity is not null && (!Live(Entity) || !Entity.isAttached
                    || Entity.networkId.PackedValue != _networkId || !Same(Entity._entity, _attachment)
                    || !Same(_attachment!.UnityObject, Entity)))
                    return false;
                if (_state is not null)
                    return Live(Entity) && Entity!.prefabId == BoltPrefabs.PlayerNetwork
                        && Same(Entity.gameObject, Root) && Native(_state)
                        && Same(Entity.GetState<IPlayerState>(), _state) && _state.Dying is 0 or 1;
                if (!Live(_actor) || _actor!.GetInstanceID() != _actorId
                    || (int)_actor._id != _actorType || _actor._worldSimUniqueId != _worldSimId
                    || !Same(_actor.gameObject, Root) || _actor._isDead || _actor.IsDead() || _actor.IsDying())
                    return false;
                var network = _actor._actorNetworked;
                if (_network is not null || network is not null)
                {
                    if (!Live(_network) || !Same(network, _network) || !Same(_network._vailActor, _actor))
                        return false;
                    if (Entity is null)
                    {
                        if (Live(_network._entity))
                            return false;
                    }
                    else if (!Same(_network._entity, Entity) || !Root.transform.IsChildOf(Entity.transform))
                        return false;
                }
                // Replica health is not authoritative; native death state controls presentation.
                return true;
            }
        }
    }

    private readonly struct Part
    {
        internal readonly Renderer Renderer;
        internal readonly SkinnedMeshRenderer? Skin;
        internal readonly MeshFilter? Filter;
        internal readonly Mesh Mesh;
        internal readonly int Submeshes;

        internal Part(Renderer renderer, SkinnedMeshRenderer? skin, MeshFilter? filter, Mesh mesh, int submeshes)
        {
            Renderer = renderer;
            Skin = skin;
            Filter = filter;
            Mesh = mesh;
            Submeshes = submeshes;
        }
    }
}
