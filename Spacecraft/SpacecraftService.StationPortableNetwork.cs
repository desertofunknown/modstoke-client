// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.IO;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftService
{
    private const long PortableSendInterval = 100, PortableIdleInterval = 1000;
    private PortableAssembly? _incomingPortables;
    private BoxStationPortableState? _portablesState, _localPortablesState;
    private Guid _portablesStateEpoch, _portablesStateToken, _portablesStateShip;
    private Guid _portableCaptureEpoch, _portableCaptureShip;
    private long _portableSequence, _incomingPortableSequence, _receivedPortableSequence;
    private long _nextPortableSnapshot, _nextLocalPortablesState, _lastPortablesAt, _portableReadAt;
    private int _portableReadFrame = -1;

    private BoxStationPortableState? CurrentPortablesState
    {
        get
        {
            if (!_ready) return null;
            // All typed readers use the same freshness instant during this main-thread frame.
            if (_portableReadFrame != Time.frameCount)
            {
                _portableReadFrame = Time.frameCount;
                _portableReadAt = Now;
            }
            if (IsClient) return _shipId == Guid.Empty || _portablesStateEpoch != _epoch || _portablesStateToken != _token
                || _portablesStateShip != _shipId || _lastPortablesAt == 0
                || _portableReadAt - _lastPortablesAt >= PortableTransportLayout.Freshness ? null : _portablesState;
            if (_localPortablesState is not { } current || _portableReadAt >= _nextLocalPortablesState
                || current.Materials.HullRevision != _stationSystems.Hull.State.Revision)
            {
                _localPortablesState = _stationSystems.Portables;
                _nextLocalPortablesState = _portableReadAt + PortableSendInterval;
            }
            return _localPortablesState;
        }
    }

    private void ResetPortableTransport()
    {
        ResetMaterialsTransport();
        ResetEngineeringTransport();
        ResetFirefightingTransport();
        ResetFoamTransport();
        ResetRepairTransport();
        _incomingPortables = null;
        _portablesState = _localPortablesState = null;
        _portablesStateEpoch = _portablesStateToken = _portablesStateShip = Guid.Empty;
        _portableCaptureEpoch = _portableCaptureShip = Guid.Empty;
        _portableSequence = _incomingPortableSequence = _receivedPortableSequence = 0;
        _nextPortableSnapshot = _nextLocalPortablesState = _lastPortablesAt = _portableReadAt = 0;
        _portableReadFrame = -1;
    }

    private void MarkPortablesDirty()
    {
        _nextPortableSnapshot = 0;
        _localPortablesState = null;
    }

    private void SendPortableSnapshots()
    {
        long now = Now;
        ExpirePortableAssembly(now);
        if (IsClient || !_ready || _shipId == Guid.Empty)
        {
            ClearOutgoingPortables();
            return;
        }
        if (_portableCaptureEpoch != _epoch || _portableCaptureShip != _shipId)
            ClearOutgoingPortables();
        if (_outgoingMaterials == null && _outgoingEngineering == null && _outgoingFirefighting == null
            && _outgoingFoam == null && _outgoingRepair == null && now >= _nextPortableSnapshot)
            CapturePortableSnapshots(now);
        SendMaterialsSnapshots();
        SendEngineeringSnapshots();
        SendFirefightingSnapshots();
        SendFoamSnapshots();
        SendRepairSnapshots();
    }

    private void ClearOutgoingPortables()
    {
        _outgoingMaterials = null;
        _outgoingEngineering = null;
        _outgoingFirefighting = null;
        _outgoingFoam = null;
        _outgoingRepair = null;
    }

    private void CapturePortableSnapshots(long now)
    {
        var state = _stationSystems.Portables;
        var hull = _stationSystems.Hull.State;
        if (!BoxStationSystems.ValidPortables(BoxStationLayout.Current, state)
            || !BoxStationHullRepair.ValidMaterials(BoxStationLayout.Current, state.Materials, hull, _stationSystems.Power.CableState)
            || !BoxStationEngineering.ValidState(BoxStationLayout.Current, state.Engineering, hull, state.Materials))
            throw new InvalidDataException("Invalid station portable telemetry.");
        _localPortablesState = state;
        _nextLocalPortablesState = now + PortableSendInterval;
        _nextPortableSnapshot = now + (PortableStateActive(state) ? PortableSendInterval : PortableIdleInterval);
        _portableCaptureEpoch = _epoch;
        _portableCaptureShip = _shipId;
        long sequence = checked(++_portableSequence);
        var materials = new MaterialsSnapshot(state.Materials, _epoch, _shipId, sequence);
        var engineering = new EngineeringSnapshot(state.Engineering, _epoch, _shipId, sequence);
        var firefighting = new FirefightingSnapshot(state.Firefighting, _epoch, _shipId, sequence);
        var foam = new FoamSnapshot(state.Foam, _epoch, _shipId, sequence);
        var repair = new RepairSnapshot(state.Repair, _epoch, _shipId, sequence);
        foreach (var pair in _peers)
        {
            if (!PeerCurrent(pair.Value)) continue;
            materials.Peers.Add(new(pair.Key, pair.Value));
            engineering.Peers.Add(new(pair.Key, pair.Value));
            firefighting.Peers.Add(new(pair.Key, pair.Value));
            foam.Peers.Add(new(pair.Key, pair.Value));
            repair.Peers.Add(new(pair.Key, pair.Value));
        }
        if (materials.Peers.Count == 0) return;
        _outgoingMaterials = materials;
        _outgoingEngineering = engineering;
        _outgoingFirefighting = firefighting;
        _outgoingFoam = foam;
        _outgoingRepair = repair;
    }

    private static bool PortableStateActive(BoxStationPortableState state)
    {
        if (!state.Engineering.Work.IsEmpty || state.Materials.Fabrication.Recipe != BoxStationFabricationRecipe.None) return true;
        foreach (var bundle in state.Materials.Bundles)
            if (bundle.Place == BoxStationMaterialPlace.Held) return true;
        foreach (var tool in state.Engineering.Tools)
            if (tool.Place == BoxStationToolPlace.Held || tool.Lit) return true;
        foreach (var item in state.Firefighting.Items)
            if (item.Place == BoxStationExtinguisherPlace.Held) return true;
        foreach (var item in state.Foam.Items)
            if (item.Place is BoxStationFoamPlace.Held or BoxStationFoamPlace.Armed or BoxStationFoamPlace.Foaming) return true;
        foreach (var pack in state.Repair.Packs)
            if (pack.Place == BoxStationRepairPlace.Held) return true;
        return false;
    }

    private bool BeginPortableReceive(Message message)
    {
        if (!IsClient || !_ready || message.Epoch != _epoch || message.Token != _token
            || message.Ship == Guid.Empty || message.Ship != _shipId
            || message.Sequence <= _receivedPortableSequence || message.Sequence < _incomingPortableSequence) return false;
        long now = Now;
        ExpirePortableAssembly(now);
        if (message.Sequence > _incomingPortableSequence)
        {
            _incomingPortableSequence = message.Sequence;
            _incomingPortables = new(message.Epoch, message.Token, message.Ship, message.Sequence, now);
        }
        var incoming = _incomingPortables;
        // Typed assemblies retain their previous headers until replaced, preserving monotonic resource checks.
        if (incoming == null) return false;
        if (incoming.Epoch != message.Epoch || incoming.Token != message.Token || incoming.Ship != message.Ship)
        {
            DiscardPortableAssembly();
            return false;
        }
        return true;
    }

    private void ExpirePortableAssembly(long now)
    {
        if (_incomingPortables is { } incoming && now - incoming.StartedAt >= PortableTransportLayout.Timeout)
            DiscardPortableAssembly();
    }

    private void DiscardPortableAssembly()
    {
        // Keep all sequence watermarks: an expired or conflicting capture cannot restart its deadline.
        _incomingPortables = null;
        _incomingMaterials = null;
        _incomingEngineering = null;
        _incomingFirefighting = null;
        _incomingFoam = null;
        _incomingRepair = null;
    }

    private void TryCommitPortables()
    {
        if (_incomingPortables is not { Materials: { } materials, Engineering: { } engineering,
                Firefighting: { } firefighting, Foam: { } foam, Repair: { } repair } incoming) return;
        var state = new BoxStationPortableState(materials, engineering, firefighting, foam, repair);
        long now = Now;
        if (now - incoming.StartedAt >= PortableTransportLayout.Timeout
            || !BoxStationSystems.ValidPortables(BoxStationLayout.Current, state))
        {
            DiscardPortableAssembly();
            return;
        }
        _portablesState = state;
        _portablesStateEpoch = incoming.Epoch;
        _portablesStateToken = incoming.Token;
        _portablesStateShip = incoming.Ship;
        _receivedPortableSequence = incoming.Sequence;
        _lastPortablesAt = now;
        DiscardPortableAssembly();
    }

    private static class PortableTransportLayout
    {
        internal static readonly long Timeout = MaximumTransferTimeout();
        internal static readonly long Freshness = PortableIdleInterval + Timeout;

        private static long MaximumTransferTimeout()
        {
            // Streams drain together; allow the slowest legal capture, including later growth.
            long materials = MaterialsTransferTimeout(MaterialsTransportLayout.Sources.Length, MaterialsTransportLayout.MaxPending,
                BoxStationHullRepair.MaxMaterialBundles, MaterialsTransportLayout.MaxConstruction,
                MaterialsTransportLayout.MaxRemoved, MaterialsTransportLayout.StorageCount);
            long engineering = EngineeringTransferTimeout(EngineeringTransportLayout.Sources.Length,
                EngineeringTransportLayout.MaxMaintenance, EngineeringWorkChunkSize, EngineeringTransportLayout.FuelTanks.Length);
            long repair = RepairTransferTimeout(RepairTransportLayout.Sources.Length, RepairTransportLayout.MaxPacks);
            long foam = 1500 + 4 * FoamSendInterval
                * ((FoamChunkCount(BoxStationFoam.GetItems(BoxStationLayout.Current).Length) + FoamChunksPerSend - 1L) / FoamChunksPerSend);
            int firefightingChunks = 1 + (BoxStationFirefighting.GetItems(BoxStationLayout.Current).Length
                + FirefightingChunkSize - 1) / FirefightingChunkSize;
            long firefighting = 1500 + 4 * PortableSendInterval * ((firefightingChunks + 2L) / 3);
            return Math.Max(materials, Math.Max(engineering, Math.Max(repair, Math.Max(foam, firefighting))));
        }
    }

    private sealed class PortableAssembly(Guid epoch, Guid token, Guid ship, long sequence, long startedAt)
    {
        internal readonly Guid Epoch = epoch, Token = token, Ship = ship;
        internal readonly long Sequence = sequence, StartedAt = startedAt;
        internal BoxStationMaterialState? Materials;
        internal BoxStationEngineeringState? Engineering;
        internal BoxStationFirefightingState? Firefighting;
        internal BoxStationFoamState? Foam;
        internal BoxStationRepairState? Repair;
    }
}
