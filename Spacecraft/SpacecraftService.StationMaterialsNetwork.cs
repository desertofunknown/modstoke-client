// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftService
{
    private const int MaterialStockChunkSize = 244, MaterialPendingChunkSize = 48, MaterialBundleChunkSize = 9;
    private const int MaterialConstructionChunkSize = 40, MaterialFloorChunkSize = 244, MaterialStorageChunkSize = 64;
    private const int MaterialsChunksPerSend = 3;
    private const long MaterialsSendInterval = 100;
    private MaterialsSnapshot? _outgoingMaterials;
    private MaterialsAssembly? _incomingMaterials;
    private long _incomingMaterialsSequence, _nextMaterialsSendAt;

    private BoxStationMaterialState? CurrentMaterialsState => CurrentPortablesState?.Materials;

    private void ResetMaterialsTransport()
    {
        _outgoingMaterials = null;
        _incomingMaterials = null;
        _incomingMaterialsSequence = _nextMaterialsSendAt = 0;
    }

    private void MarkMaterialsDirty() => MarkPortablesDirty();

    private void SendMaterialsSnapshots()
    {
        long now = Now;
        ExpirePortableAssembly(now);
        if (_outgoingMaterials is not { } snapshot || _event == null || now < _nextMaterialsSendAt) return;
        _nextMaterialsSendAt = now + MaterialsSendInterval;
        for (int index = snapshot.Peers.Count - 1; index >= 0; index--)
        {
            var pending = snapshot.Peers[index];
            var peer = pending.Peer;
            if (!_peers.TryGetValue(pending.Session, out var current) || !ReferenceEquals(current, peer)
                || peer.Token != pending.Token || !PeerCurrent(peer))
            {
                snapshot.Peers.RemoveAt(index);
                continue;
            }
            // Every section belongs to one capture, including the installed mass summary.
            int end = Math.Min(snapshot.ChunkCount, pending.Chunk + MaterialsChunksPerSend);
            while (pending.Chunk < end)
            {
                _event.Materials(peer, pending.Token, snapshot, pending.Chunk);
                pending.Chunk++;
            }
            if (pending.Chunk == snapshot.ChunkCount) snapshot.Peers.RemoveAt(index);
        }
        if (snapshot.Peers.Count == 0) _outgoingMaterials = null;
    }

    private void ReceiveMaterials(Message message)
    {
        if (message.Materials is not { } chunk || !BeginPortableReceive(message)) return;
        if (message.Sequence > _incomingMaterialsSequence && _portablesState?.Materials is { } applied && (chunk.HullRevision < applied.HullRevision
                || chunk.NextBundleId < applied.NextBundleId || chunk.Budget != applied.Budget
                || chunk.ConstructionBudget != applied.ConstructionBudget || chunk.ImportedSteel < applied.ImportedSteel
                || chunk.ExportedSteel < applied.ExportedSteel
                || chunk.ConsumedFloorTiles < applied.ConsumedFloorTiles
                || chunk.CableRevision < applied.CableRevision || chunk.CableBudget != applied.CableBudget
                || chunk.CableRevision == applied.CableRevision && chunk.CableInstalled != applied.CableInstalled
                || chunk.DoorRevision < applied.DoorConstruction.Revision || chunk.DoorElectronicsBudget != applied.DoorElectronicsBudget
                || chunk.DoorRevision == applied.DoorConstruction.Revision && chunk.DoorCount != applied.DoorConstruction.Assemblies.Length
                || chunk.Fabrication.ProducedLine < applied.Fabrication.ProducedLine
                || chunk.Fabrication.ProducedCircuit < applied.Fabrication.ProducedCircuit
                || chunk.Fabrication.ExportedLine < applied.Fabrication.ExportedLine
                || chunk.Fabrication.ExportedCircuit < applied.Fabrication.ExportedCircuit
                || chunk.FloorRevision < applied.Floors.Revision || chunk.LostTiles < applied.Floors.LostTiles
                || chunk.FloorRevision == applied.Floors.Revision
                    && (chunk.LostTiles != applied.Floors.LostTiles || chunk.RemovedCount != applied.Floors.Removed.Length))
            || message.Sequence > _incomingMaterialsSequence && _incomingMaterials is { } previous
                && (chunk.HullRevision < previous.HullRevision || chunk.NextBundleId < previous.NextBundleId
                    || chunk.Budget != previous.Budget || chunk.ConstructionBudget != previous.ConstructionBudget
                    || chunk.ImportedSteel < previous.ImportedSteel || chunk.ConsumedFloorTiles < previous.ConsumedFloorTiles
                    || chunk.ExportedSteel < previous.ExportedSteel
                    || chunk.CableRevision < previous.CableRevision || chunk.CableBudget != previous.CableBudget
                    || chunk.CableRevision == previous.CableRevision && chunk.CableInstalled != previous.CableInstalled
                    || chunk.DoorRevision < previous.DoorRevision || chunk.DoorElectronicsBudget != previous.DoorElectronicsBudget
                    || chunk.DoorRevision == previous.DoorRevision && chunk.DoorCount != previous.DoorConstruction.Length
                    || chunk.Fabrication.ProducedLine < previous.Fabrication.ProducedLine
                    || chunk.Fabrication.ProducedCircuit < previous.Fabrication.ProducedCircuit
                    || chunk.Fabrication.ExportedLine < previous.Fabrication.ExportedLine
                    || chunk.Fabrication.ExportedCircuit < previous.Fabrication.ExportedCircuit
                    || chunk.FloorRevision < previous.FloorRevision || chunk.LostTiles < previous.LostTiles
                    || chunk.FloorRevision == previous.FloorRevision
                        && (chunk.LostTiles != previous.LostTiles || chunk.RemovedCount != previous.Removed.Length)))
        {
            DiscardPortableAssembly();
            return;
        }
        long now = Now;
        if (message.Sequence > _incomingMaterialsSequence)
        {
            _incomingMaterialsSequence = message.Sequence;
            _incomingMaterials = new(message.Epoch, message.Token, message.Ship, chunk, _incomingPortables!.StartedAt);
        }
        var incoming = _incomingMaterials;
        if (incoming == null) return;
        if (incoming.Epoch != message.Epoch || incoming.Token != message.Token || incoming.Ship != message.Ship
            || incoming.HullRevision != chunk.HullRevision || incoming.Budget != chunk.Budget
            || incoming.Installed != chunk.Installed || incoming.NextBundleId != chunk.NextBundleId
            || incoming.ConstructionBudget != chunk.ConstructionBudget || incoming.ConstructionInstalled != chunk.ConstructionInstalled
            || incoming.ConstructionWaste != chunk.ConstructionWaste || incoming.ImportedSteel != chunk.ImportedSteel
            || incoming.ExportedSteel != chunk.ExportedSteel
            || incoming.ConsumedFloorTiles != chunk.ConsumedFloorTiles
            || incoming.CableRevision != chunk.CableRevision || incoming.CableBudget != chunk.CableBudget
            || incoming.CableInstalled != chunk.CableInstalled
            || incoming.DoorRevision != chunk.DoorRevision || incoming.DoorElectronicsBudget != chunk.DoorElectronicsBudget
            || incoming.DoorConstruction.Length != chunk.DoorCount
            || incoming.FloorRevision != chunk.FloorRevision || incoming.LostTiles != chunk.LostTiles
            || incoming.Stocks.Length != chunk.SourceCount || incoming.Pending.Length != chunk.PendingCount
            || incoming.Bundles.Length != chunk.BundleCount || incoming.Construction.Length != chunk.ConstructionCount
            || incoming.Removed.Length != chunk.RemovedCount
            || incoming.StorageOpen.Length != chunk.StorageCount
            || incoming.Fabrication != chunk.Fabrication
            || now - incoming.StartedAt >= PortableTransportLayout.Timeout)
        {
            DiscardPortableAssembly();
            return;
        }
        int index = chunk.Section switch
        {
            0 => chunk.Offset / MaterialStockChunkSize,
            1 => MaterialChunkCount(chunk.SourceCount, MaterialStockChunkSize) + chunk.Offset / MaterialPendingChunkSize,
            2 => MaterialChunkCount(chunk.SourceCount, MaterialStockChunkSize)
                + MaterialChunkCount(chunk.PendingCount, MaterialPendingChunkSize) + chunk.Offset / MaterialBundleChunkSize,
            3 => MaterialChunkCount(chunk.SourceCount, MaterialStockChunkSize)
                + MaterialChunkCount(chunk.PendingCount, MaterialPendingChunkSize)
                + MaterialChunkCount(chunk.BundleCount, MaterialBundleChunkSize) + chunk.Offset / MaterialConstructionChunkSize,
            4 => incoming.Chunks.Length - MaterialChunkCount(chunk.StorageCount, MaterialStorageChunkSize)
                - 1 - MaterialChunkCount(chunk.RemovedCount, MaterialFloorChunkSize)
                + chunk.Offset / MaterialFloorChunkSize,
            5 => incoming.Chunks.Length - MaterialChunkCount(chunk.StorageCount, MaterialStorageChunkSize) - 1,
            _ => incoming.Chunks.Length - MaterialChunkCount(chunk.StorageCount, MaterialStorageChunkSize)
                + chunk.Offset / MaterialStorageChunkSize
        };
        if (incoming.Chunks[index])
        {
            bool same = chunk.Section switch
            {
                0 => incoming.Stocks.AsSpan(chunk.Offset, chunk.Stocks.Length).SequenceEqual(chunk.Stocks),
                1 => incoming.Pending.AsSpan(chunk.Offset, chunk.Pending.Length).SequenceEqual(chunk.Pending),
                2 => SameMaterialBundles(incoming.Bundles.AsSpan(chunk.Offset, chunk.Bundles.Length), chunk.Bundles),
                3 => incoming.Construction.AsSpan(chunk.Offset, chunk.Construction.Length).SequenceEqual(chunk.Construction),
                4 => incoming.Removed.AsSpan(chunk.Offset, chunk.Removed.Length).SequenceEqual(chunk.Removed),
                5 => incoming.DoorConstruction.AsSpan().SequenceEqual(chunk.DoorConstruction),
                _ => incoming.StorageOpen.AsSpan(chunk.Offset, chunk.StorageOpen.Length).SequenceEqual(chunk.StorageOpen)
            };
            if (!same) DiscardPortableAssembly();
            return;
        }
        if (chunk.Section == 0) chunk.Stocks.CopyTo(incoming.Stocks, chunk.Offset);
        else if (chunk.Section == 1) chunk.Pending.CopyTo(incoming.Pending, chunk.Offset);
        else if (chunk.Section == 2) chunk.Bundles.CopyTo(incoming.Bundles, chunk.Offset);
        else if (chunk.Section == 3) chunk.Construction.CopyTo(incoming.Construction, chunk.Offset);
        else if (chunk.Section == 4) chunk.Removed.CopyTo(incoming.Removed, chunk.Offset);
        else if (chunk.Section == 5) chunk.DoorConstruction.CopyTo(incoming.DoorConstruction, chunk.Offset);
        else chunk.StorageOpen.CopyTo(incoming.StorageOpen, chunk.Offset);
        incoming.Chunks[index] = true;
        if (++incoming.ReceivedChunks != incoming.Chunks.Length) return;
        var state = new BoxStationMaterialState(BoxStationLayout.Current.LayoutHash, incoming.HullRevision,
            incoming.Budget, incoming.Installed, incoming.NextBundleId, ImmutableArray.CreateRange(incoming.Stocks),
            ImmutableArray.CreateRange(incoming.Pending), ImmutableArray.CreateRange(incoming.Bundles),
            incoming.ConstructionBudget, incoming.ConstructionInstalled, incoming.ConstructionWaste, incoming.ImportedSteel,
            ImmutableArray.CreateRange(incoming.Construction), incoming.ConsumedFloorTiles,
            incoming.CableRevision, incoming.CableBudget, incoming.CableInstalled,
            new(incoming.FloorRevision, incoming.LostTiles, ImmutableArray.CreateRange(incoming.Removed)),
            new(incoming.DoorRevision, ImmutableArray.CreateRange(incoming.DoorConstruction)), incoming.DoorElectronicsBudget,
            ImmutableArray.CreateRange(incoming.StorageOpen), incoming.Fabrication, incoming.ExportedSteel);
        // Hull telemetry travels independently; this capture validates against its own installed summary.
        if (!BoxStationHullRepair.ValidMaterials(BoxStationLayout.Current, state))
        {
            DiscardPortableAssembly();
            return;
        }
        if (_portablesState?.Materials is { } current
            && (state.Floors.Revision == current.Floors.Revision
                && !state.Floors.Removed.AsSpan().SequenceEqual(current.Floors.Removed.AsSpan())
                || state.DoorConstruction.Revision == current.DoorConstruction.Revision
                    && !state.DoorConstruction.Assemblies.AsSpan().SequenceEqual(current.DoorConstruction.Assemblies.AsSpan())))
        {
            DiscardPortableAssembly();
            return;
        }
        _incomingPortables!.Materials = state;
        TryCommitPortables();
    }

    private static bool SameMaterialBundles(ReadOnlySpan<BoxStationMaterialBundle> left, ReadOnlySpan<BoxStationMaterialBundle> right)
    {
        if (left.Length != right.Length) return false;
        for (int index = 0; index < left.Length; index++)
        {
            var a = left[index]; var b = right[index];
            if (a.Id != b.Id || a.Material != b.Material || a.Count != b.Count || a.Place != b.Place
                || a.Actor != b.Actor || a.Holder != b.Holder || a.Storage != b.Storage || a.Slot != b.Slot
                || BitConverter.SingleToInt32Bits(a.Position.X) != BitConverter.SingleToInt32Bits(b.Position.X)
                || BitConverter.SingleToInt32Bits(a.Position.Y) != BitConverter.SingleToInt32Bits(b.Position.Y)
                || BitConverter.SingleToInt32Bits(a.Position.Z) != BitConverter.SingleToInt32Bits(b.Position.Z)) return false;
        }
        return true;
    }

    private static int MaterialChunkCount(int count, int size) => Math.Max(1, (count + size - 1) / size);
    private static int MaterialsChunkCount(int sources, int pending, int bundles, int construction, int removed, int storage)
        => MaterialChunkCount(sources, MaterialStockChunkSize) + MaterialChunkCount(pending, MaterialPendingChunkSize)
            + MaterialChunkCount(bundles, MaterialBundleChunkSize) + MaterialChunkCount(construction, MaterialConstructionChunkSize)
            + MaterialChunkCount(removed, MaterialFloorChunkSize) + 1 + MaterialChunkCount(storage, MaterialStorageChunkSize);
    private static long MaterialsTransferTimeout(int sources, int pending, int bundles, int construction, int removed, int storage) => 1500 + 4 * MaterialsSendInterval
        * ((MaterialsChunkCount(sources, pending, bundles, construction, removed, storage) + MaterialsChunksPerSend - 1L) / MaterialsChunksPerSend);

    private static class MaterialsTransportLayout
    {
        internal static readonly ImmutableArray<BoxStationMaterialSource> Sources = BoxStationHullRepair.GetMaterialSources(BoxStationLayout.Current);
        internal static readonly int MaxPending = BoxStationHull.GetStructures(BoxStationLayout.Current).Length;
        internal static readonly BoxStationMaterialBudget Budget = BoxStationHullRepair.GetMaterialBudget(BoxStationLayout.Current);
        internal static readonly int MaxConstruction = BoxStationHullRepair.GetConstructionDefinitions(BoxStationLayout.Current).Length;
        internal static readonly BoxStationConstructionBudget ConstructionBudget = BoxStationHullRepair.GetConstructionBudget(BoxStationLayout.Current);
        internal static readonly int MaxImportedSteel = MaximumImportedSteel();
        internal static readonly int CableCount = BoxStationPower.GetCables(BoxStationLayout.Current).Length;
        internal static readonly int CableSiteCount = BoxStationPower.GetCableSites(BoxStationLayout.Current).Length;
        internal static readonly int CableBudget = MaximumCableBudget();
        internal static readonly int DoorElectronicsBudget = MaximumDoorElectronicsBudget();
        internal static readonly int MaxRemoved = BoxStationHullRepair.GetFloorCells(BoxStationLayout.Current).Length;
        internal static readonly int StorageCount = BoxStationStorage.GetDefinitions(BoxStationLayout.Current).Length;

        private static int MaximumImportedSteel()
        {
            int total = 0;
            foreach (var source in RepairTransportLayout.Sources) total = checked(total + source.Sheets);
            return total;
        }

        private static int MaximumCableBudget()
        {
            int total = CableCount;
            foreach (var source in Sources)
                if (source.Material == BoxStationMaterial.Cable) total = checked(total + source.Count);
            return total;
        }

        private static int MaximumDoorElectronicsBudget()
        {
            int total = 0;
            foreach (var source in Sources)
                if (source.Material == BoxStationMaterial.DoorElectronics) total = checked(total + source.Count);
            return total;
        }
    }

    private sealed class MaterialsSnapshot(BoxStationMaterialState state, Guid epoch, Guid ship, long sequence)
    {
        internal readonly BoxStationMaterialState State = state;
        internal readonly Guid Epoch = epoch, Ship = ship;
        internal readonly long Sequence = sequence;
        internal readonly int StockChunks = MaterialChunkCount(state.Stocks.Length, MaterialStockChunkSize);
        internal readonly int PendingChunks = MaterialChunkCount(state.Pending.Length, MaterialPendingChunkSize);
        internal readonly int BundleChunks = MaterialChunkCount(state.Bundles.Length, MaterialBundleChunkSize);
        internal readonly int ConstructionChunks = MaterialChunkCount(state.Construction.Length, MaterialConstructionChunkSize);
        internal readonly int StorageChunks = MaterialChunkCount(state.StorageOpen.Length, MaterialStorageChunkSize);
        internal readonly int ChunkCount = MaterialsChunkCount(state.Stocks.Length, state.Pending.Length, state.Bundles.Length,
            state.Construction.Length, state.Floors.Removed.Length, state.StorageOpen.Length);
        internal readonly List<StationPeer> Peers = new();
    }

    private sealed class MaterialsChunk(long hullRevision, BoxStationMaterialBudget budget, BoxStationMaterialBudget installed,
        int nextBundleId, int sourceCount, int pendingCount, int bundleCount, byte section, int offset,
        ushort[] stocks, BoxStationPendingYield[] pending, BoxStationMaterialBundle[] bundles,
        BoxStationConstructionBudget constructionBudget, BoxStationConstructionBudget constructionInstalled,
        BoxStationConstructionBudget constructionWaste, int importedSteel, int constructionCount, BoxStationConstruction[] construction,
        int consumedFloorTiles, long cableRevision, int cableBudget, int cableInstalled,
        long floorRevision, int lostTiles, int removedCount, ushort[] removed,
        long doorRevision, int doorElectronicsBudget, int doorCount, BoxStationDoorAssembly[] doorConstruction,
        int storageCount, bool[] storageOpen, BoxStationFabricationState fabrication, int exportedSteel)
    {
        internal readonly long HullRevision = hullRevision;
        internal readonly BoxStationMaterialBudget Budget = budget, Installed = installed;
        internal readonly int NextBundleId = nextBundleId, SourceCount = sourceCount, PendingCount = pendingCount, BundleCount = bundleCount, Offset = offset;
        internal readonly byte Section = section;
        internal readonly ushort[] Stocks = stocks;
        internal readonly BoxStationPendingYield[] Pending = pending;
        internal readonly BoxStationMaterialBundle[] Bundles = bundles;
        internal readonly BoxStationConstructionBudget ConstructionBudget = constructionBudget, ConstructionInstalled = constructionInstalled,
            ConstructionWaste = constructionWaste;
        internal readonly int ImportedSteel = importedSteel, ConstructionCount = constructionCount, ConsumedFloorTiles = consumedFloorTiles;
        internal readonly long CableRevision = cableRevision;
        internal readonly int CableBudget = cableBudget, CableInstalled = cableInstalled;
        internal readonly BoxStationConstruction[] Construction = construction;
        internal readonly long FloorRevision = floorRevision;
        internal readonly int LostTiles = lostTiles, RemovedCount = removedCount;
        internal readonly ushort[] Removed = removed;
        internal readonly long DoorRevision = doorRevision;
        internal readonly int DoorElectronicsBudget = doorElectronicsBudget, DoorCount = doorCount;
        internal readonly BoxStationDoorAssembly[] DoorConstruction = doorConstruction;
        internal readonly int StorageCount = storageCount;
        internal readonly bool[] StorageOpen = storageOpen;
        internal readonly BoxStationFabricationState Fabrication = fabrication;
        internal readonly int ExportedSteel = exportedSteel;
    }

    private sealed class MaterialsAssembly(Guid epoch, Guid token, Guid ship, MaterialsChunk chunk, long startedAt)
    {
        internal readonly Guid Epoch = epoch, Token = token, Ship = ship;
        internal readonly long StartedAt = startedAt, HullRevision = chunk.HullRevision;
        internal readonly BoxStationMaterialBudget Budget = chunk.Budget, Installed = chunk.Installed;
        internal readonly int NextBundleId = chunk.NextBundleId;
        internal readonly ushort[] Stocks = new ushort[chunk.SourceCount];
        internal readonly BoxStationPendingYield[] Pending = new BoxStationPendingYield[chunk.PendingCount];
        internal readonly BoxStationMaterialBundle[] Bundles = new BoxStationMaterialBundle[chunk.BundleCount];
        internal readonly BoxStationConstructionBudget ConstructionBudget = chunk.ConstructionBudget, ConstructionInstalled = chunk.ConstructionInstalled,
            ConstructionWaste = chunk.ConstructionWaste;
        internal readonly int ImportedSteel = chunk.ImportedSteel, ConsumedFloorTiles = chunk.ConsumedFloorTiles;
        internal readonly long CableRevision = chunk.CableRevision;
        internal readonly int CableBudget = chunk.CableBudget, CableInstalled = chunk.CableInstalled;
        internal readonly BoxStationConstruction[] Construction = new BoxStationConstruction[chunk.ConstructionCount];
        internal readonly long FloorRevision = chunk.FloorRevision;
        internal readonly int LostTiles = chunk.LostTiles;
        internal readonly ushort[] Removed = new ushort[chunk.RemovedCount];
        internal readonly long DoorRevision = chunk.DoorRevision;
        internal readonly int DoorElectronicsBudget = chunk.DoorElectronicsBudget;
        internal readonly BoxStationDoorAssembly[] DoorConstruction = new BoxStationDoorAssembly[chunk.DoorCount];
        internal readonly bool[] StorageOpen = new bool[chunk.StorageCount];
        internal readonly BoxStationFabricationState Fabrication = chunk.Fabrication;
        internal readonly int ExportedSteel = chunk.ExportedSteel;
        internal readonly bool[] Chunks = new bool[MaterialsChunkCount(chunk.SourceCount, chunk.PendingCount, chunk.BundleCount,
            chunk.ConstructionCount, chunk.RemovedCount, chunk.StorageCount)];
        internal int ReceivedChunks;
    }

    private sealed partial class SpacecraftEvent
    {
        // Header: 279 bytes. Stocks/floors: 767; pending/construction: 759; bundles: 720; doors: 599; storage: 343.
        internal void Materials(Peer peer, Guid token, MaterialsSnapshot snapshot, int chunk) =>
            Emit(peer, (Token: token, Snapshot: snapshot, Chunk: chunk), static (writer, state) =>
        {
            var capture = state.Snapshot;
            var value = capture.State;
            Head(writer, Kind.Materials); GuidValue(writer, capture.Epoch); GuidValue(writer, state.Token);
            writer.Write(capture.Sequence); GuidValue(writer, capture.Ship);
            writer.Write(OrbitalStationOrbit.BodyId); writer.Write(StationLayoutFingerprint.Value);
            writer.Write(value.HullRevision); MaterialBudget(writer, value.Budget); MaterialBudget(writer, value.Installed);
            ConstructionBudget(writer, value.ConstructionBudget); ConstructionBudget(writer, value.ConstructionInstalled);
            ConstructionBudget(writer, value.ConstructionWaste); writer.Write(value.ImportedSteel);
            writer.Write(value.ConsumedFloorTiles);
            writer.Write(value.CableRevision); writer.Write(value.CableBudget); writer.Write(value.CableInstalled);
            writer.Write(value.Floors.Revision); writer.Write(value.Floors.LostTiles);
            writer.Write(value.NextBundleId);
            writer.Write(value.Stocks.Length); writer.Write(value.Pending.Length); writer.Write(value.Bundles.Length);
            writer.Write(value.Construction.Length); writer.Write(value.Floors.Removed.Length);
            writer.Write(value.DoorConstruction.Revision); writer.Write(value.DoorElectronicsBudget);
            writer.Write(value.DoorConstruction.Assemblies.Length);
            writer.Write(value.StorageOpen.Length);
            Fabrication(writer, value.Fabrication);
            writer.Write(value.ExportedSteel);
            byte section = state.Chunk < capture.StockChunks ? (byte)0
                : state.Chunk < capture.StockChunks + capture.PendingChunks ? (byte)1
                : state.Chunk < capture.StockChunks + capture.PendingChunks + capture.BundleChunks ? (byte)2
                : state.Chunk < capture.StockChunks + capture.PendingChunks + capture.BundleChunks + capture.ConstructionChunks ? (byte)3
                : state.Chunk < capture.ChunkCount - capture.StorageChunks - 1 ? (byte)4
                : state.Chunk < capture.ChunkCount - capture.StorageChunks ? (byte)5 : (byte)6;
            int offset = section == 0 ? state.Chunk * MaterialStockChunkSize
                : section == 1 ? (state.Chunk - capture.StockChunks) * MaterialPendingChunkSize
                : section == 2 ? (state.Chunk - capture.StockChunks - capture.PendingChunks) * MaterialBundleChunkSize
                : section == 3 ? (state.Chunk - capture.StockChunks - capture.PendingChunks - capture.BundleChunks) * MaterialConstructionChunkSize
                : section == 4 ? (state.Chunk - capture.StockChunks - capture.PendingChunks - capture.BundleChunks - capture.ConstructionChunks) * MaterialFloorChunkSize
                : section == 5 ? 0 : (state.Chunk - capture.ChunkCount + capture.StorageChunks) * MaterialStorageChunkSize;
            int count = section == 0 ? Math.Min(MaterialStockChunkSize, value.Stocks.Length - offset)
                : section == 1 ? Math.Min(MaterialPendingChunkSize, value.Pending.Length - offset)
                : section == 2 ? Math.Min(MaterialBundleChunkSize, value.Bundles.Length - offset)
                : section == 3 ? Math.Min(MaterialConstructionChunkSize, value.Construction.Length - offset)
                : section == 4 ? Math.Min(MaterialFloorChunkSize, value.Floors.Removed.Length - offset)
                : section == 5 ? value.DoorConstruction.Assemblies.Length : Math.Min(MaterialStorageChunkSize, value.StorageOpen.Length - offset);
            writer.Write(section); writer.Write(offset); writer.Write(checked((ushort)count));
            for (int index = offset; index < offset + count; index++)
            {
                if (section == 0) writer.Write(value.Stocks[index]);
                else if (section == 1)
                {
                    var pending = value.Pending[index];
                    writer.Write(pending.StructureId); writer.Write(pending.Sheets); writer.Write(pending.Shards); writer.Write(pending.Rods);
                }
                else if (section == 2)
                {
                    var bundle = value.Bundles[index];
                    writer.Write(bundle.Id); writer.Write((byte)bundle.Material); writer.Write(bundle.Count); writer.Write((byte)bundle.Place);
                    writer.Write(bundle.Position.X); writer.Write(bundle.Position.Y); writer.Write(bundle.Position.Z);
                    writer.Write(bundle.Actor); GuidValue(writer, bundle.Holder);
                    writer.Write(bundle.Storage); writer.Write(bundle.Slot);
                }
                else if (section == 3)
                {
                    var construction = value.Construction[index];
                    writer.Write(construction.Cell); writer.Write((byte)construction.Stage);
                    writer.Write(construction.WorkStage); writer.Write(construction.Integrity);
                    writer.Write(construction.SteelYield); writer.Write(construction.PlasteelYield);
                    writer.Write((byte)construction.WallKind);
                }
                else if (section == 4) writer.Write(value.Floors.Removed[index]);
                else if (section == 5)
                {
                    var assembly = value.DoorConstruction.Assemblies[index];
                    writer.Write(assembly.Edge); writer.Write((byte)assembly.Stage);
                }
                else writer.Write(value.StorageOpen[index]);
            }
        });

        private static void MaterialBudget(BinaryWriter writer, BoxStationMaterialBudget budget)
        {
            writer.Write(budget.Glass); writer.Write(budget.PlasmaGlass); writer.Write(budget.Rods);
        }

        private static BoxStationMaterialBudget ReadMaterialBudget(BinaryReader reader, BoxStationMaterialBudget maximum)
        {
            var budget = new BoxStationMaterialBudget(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
            if (budget.Glass < 0 || budget.Glass > maximum.Glass || budget.PlasmaGlass < 0
                || budget.PlasmaGlass > maximum.PlasmaGlass || budget.Rods < 0 || budget.Rods > maximum.Rods)
                throw new InvalidDataException();
            return budget;
        }

        private static void ConstructionBudget(BinaryWriter writer, BoxStationConstructionBudget budget)
        {
            writer.Write(budget.Steel); writer.Write(budget.Plasteel);
        }

        private static BoxStationConstructionBudget ReadConstructionBudget(BinaryReader reader, BoxStationConstructionBudget maximum)
        {
            var budget = new BoxStationConstructionBudget(reader.ReadInt32(), reader.ReadInt32());
            if (budget.Steel < 0 || budget.Steel > maximum.Steel || budget.Plasteel < 0 || budget.Plasteel > maximum.Plasteel)
                throw new InvalidDataException();
            return budget;
        }

        private static void Fabrication(BinaryWriter writer, BoxStationFabricationState state)
        {
            writer.Write(state.Machine); writer.Write(state.Steel); writer.Write(state.Cable);
            writer.Write(state.LineStock); writer.Write(state.CircuitStock);
            writer.Write(state.ProducedLine); writer.Write(state.ProducedCircuit);
            writer.Write(state.ExportedLine); writer.Write(state.ExportedCircuit);
            writer.Write((byte)state.Recipe); writer.Write(state.WorkJoules); writer.Write((byte)state.Status);
        }

        private static BoxStationFabricationState ReadFabrication(BinaryReader reader)
        {
            var state = new BoxStationFabricationState(reader.ReadInt32(), reader.ReadUInt16(), reader.ReadUInt16(),
                reader.ReadUInt16(), reader.ReadUInt16(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(),
                (BoxStationFabricationRecipe)reader.ReadByte(), reader.ReadDouble(), (BoxStationFabricationStatus)reader.ReadByte());
            if (!BoxStationHullRepair.ValidFabrication(BoxStationLayout.Current, state)) throw new InvalidDataException();
            return state;
        }

        private static MaterialsChunk ReadMaterials(BinaryReader reader)
        {
            if (reader.ReadInt32() != OrbitalStationOrbit.BodyId) throw new InvalidDataException();
            Span<byte> fingerprint = stackalloc byte[32];
            ReadExactly(reader, fingerprint);
            if (!fingerprint.SequenceEqual(StationLayoutFingerprint.Value)) throw new InvalidDataException();
            long hullRevision = reader.ReadInt64();
            var budget = ReadMaterialBudget(reader, MaterialsTransportLayout.Budget);
            var installed = ReadMaterialBudget(reader, budget);
            var constructionBudget = ReadConstructionBudget(reader, MaterialsTransportLayout.ConstructionBudget);
            var constructionMaximum = new BoxStationConstructionBudget(
                checked(constructionBudget.Steel + MaterialsTransportLayout.MaxImportedSteel), constructionBudget.Plasteel);
            var constructionInstalled = ReadConstructionBudget(reader, constructionMaximum);
            var constructionWaste = ReadConstructionBudget(reader, constructionMaximum);
            int importedSteel = reader.ReadInt32();
            int consumedFloorTiles = reader.ReadInt32();
            long cableRevision = reader.ReadInt64();
            int cableBudget = reader.ReadInt32(), cableInstalled = reader.ReadInt32();
            long floorRevision = reader.ReadInt64();
            int lostTiles = reader.ReadInt32();
            int nextBundleId = reader.ReadInt32();
            int sourceCount = reader.ReadInt32(), pendingCount = reader.ReadInt32(), bundleCount = reader.ReadInt32(), constructionCount = reader.ReadInt32();
            int removedCount = reader.ReadInt32();
            long doorRevision = reader.ReadInt64();
            int doorElectronicsBudget = reader.ReadInt32(), doorCount = reader.ReadInt32();
            int storageCount = reader.ReadInt32();
            var fabrication = ReadFabrication(reader);
            int exportedSteel = reader.ReadInt32();
            byte section = reader.ReadByte();
            int offset = reader.ReadInt32(), count = reader.ReadUInt16();
            var sources = MaterialsTransportLayout.Sources;
            if (hullRevision < 0 || nextBundleId <= 0 || sourceCount != sources.Length
                || cableRevision < 0 || cableBudget < 0 || cableBudget > MaterialsTransportLayout.CableBudget
                || cableInstalled < 0 || cableInstalled > MaterialsTransportLayout.CableSiteCount || cableInstalled > cableBudget
                || cableRevision == 0 && cableInstalled != MaterialsTransportLayout.CableCount
                || importedSteel < 0 || importedSteel > MaterialsTransportLayout.MaxImportedSteel
                || exportedSteel < 0 || exportedSteel > (long)constructionBudget.Steel + importedSteel
                || consumedFloorTiles < 0 || consumedFloorTiles > 4L * (constructionBudget.Steel + importedSteel) + MaterialsTransportLayout.MaxRemoved
                || floorRevision < 0 || lostTiles < 0 || lostTiles > 4L * (constructionBudget.Steel + importedSteel) + MaterialsTransportLayout.MaxRemoved
                || removedCount < 0 || removedCount > MaterialsTransportLayout.MaxRemoved
                || floorRevision == 0 && (lostTiles != 0 || removedCount != 0)
                || doorRevision < 0 || doorElectronicsBudget != MaterialsTransportLayout.DoorElectronicsBudget
                || doorCount is < 0 or > BoxStationDoors.MaximumAssemblies || doorRevision == 0 && doorCount != 0
                || constructionInstalled.Steel + constructionWaste.Steel > constructionBudget.Steel + importedSteel
                || constructionInstalled.Plasteel + constructionWaste.Plasteel > constructionBudget.Plasteel
                || constructionCount < 0 || constructionCount > MaterialsTransportLayout.MaxConstruction
                || pendingCount < 0 || pendingCount > MaterialsTransportLayout.MaxPending
                || bundleCount < 0 || bundleCount > BoxStationHullRepair.MaxMaterialBundles
                || storageCount != MaterialsTransportLayout.StorageCount || section > 6)
                throw new InvalidDataException();
            int total = section == 0 ? sourceCount : section == 1 ? pendingCount : section == 2 ? bundleCount
                : section == 3 ? constructionCount : section == 4 ? removedCount : section == 5 ? doorCount : storageCount;
            int size = section == 0 ? MaterialStockChunkSize : section == 1 ? MaterialPendingChunkSize
                : section == 2 ? MaterialBundleChunkSize : section == 3 ? MaterialConstructionChunkSize
                : section == 4 ? MaterialFloorChunkSize : section == 5 ? BoxStationDoors.MaximumAssemblies : MaterialStorageChunkSize;
            if (offset < 0 || offset % size != 0 || (total == 0 ? offset != 0 : offset >= total)
                || count != Math.Min(size, total - offset)) throw new InvalidDataException();
            var stocks = section == 0 ? new ushort[count] : Array.Empty<ushort>();
            var pending = section == 1 ? new BoxStationPendingYield[count] : Array.Empty<BoxStationPendingYield>();
            var bundles = section == 2 ? new BoxStationMaterialBundle[count] : Array.Empty<BoxStationMaterialBundle>();
            var construction = section == 3 ? new BoxStationConstruction[count] : Array.Empty<BoxStationConstruction>();
            var removed = section == 4 ? new ushort[count] : Array.Empty<ushort>();
            var doorConstruction = section == 5 ? new BoxStationDoorAssembly[count] : Array.Empty<BoxStationDoorAssembly>();
            var storageOpen = section == 6 ? new bool[count] : Array.Empty<bool>();
            int previous = -1;
            for (int index = 0; index < count; index++)
            {
                if (section == 0)
                {
                    ushort remaining = reader.ReadUInt16();
                    if (remaining > sources[offset + index].Count) throw new InvalidDataException();
                    stocks[index] = remaining;
                }
                else if (section == 1)
                {
                    int id = reader.ReadInt32();
                    ushort sheets = reader.ReadUInt16(), shards = reader.ReadUInt16(), rods = reader.ReadUInt16();
                    if (id <= previous || BoxStationHull.StructureTarget(BoxStationLayout.Current, id) < 0
                        || sheets + shards + rods == 0 || sheets > budget.Glass + budget.PlasmaGlass
                        || shards > budget.Glass + budget.PlasmaGlass || rods > budget.Rods) throw new InvalidDataException();
                    pending[index] = new(id, sheets, shards, rods);
                    previous = id;
                }
                else if (section == 2)
                {
                    int id = reader.ReadInt32();
                    var material = (BoxStationMaterial)reader.ReadByte();
                    ushort amount = reader.ReadUInt16();
                    var place = (BoxStationMaterialPlace)reader.ReadByte();
                    var position = Vector(reader);
                    ulong actor = reader.ReadUInt64(); var holder = GuidValue(reader);
                    int storage = reader.ReadInt32(); byte slot = reader.ReadByte();
                    if (id <= 0 || id <= previous || id >= nextBundleId
                        || material is < BoxStationMaterial.Rods or > BoxStationMaterial.CircuitSpare
                        || amount is 0 or > BoxStationHullRepair.MaxMaterialBundleCount
                        || material == BoxStationMaterial.FloorTiles && amount > BoxStationHullRepair.MaxFloorTileBundleCount
                        || material == BoxStationMaterial.Cable && amount > BoxStationHullRepair.MaxCableBundleCount
                        || material == BoxStationMaterial.LineSpare && amount > BoxStationHullRepair.FabricationLineCapacity
                        || material == BoxStationMaterial.CircuitSpare && amount > BoxStationHullRepair.FabricationCircuitCapacity
                        || place is < BoxStationMaterialPlace.Dropped or > BoxStationMaterialPlace.Stored || !Finite(position)
                        || place != BoxStationMaterialPlace.Stored && (storage != -1 || slot != 0)
                        || place == BoxStationMaterialPlace.Stored
                            && (!BoxStationStorage.TryDefinition(BoxStationLayout.Current, storage, out var definition)
                                || slot >= BoxStationStorage.Capacity(definition) || actor != 0 || holder != Guid.Empty))
                        throw new InvalidDataException();
                    bundles[index] = new(id, material, amount, place,
                        new System.Numerics.Vector3(position.x, position.y, position.z), actor, holder, storage, slot);
                    previous = id;
                }
                else if (section == 3)
                {
                    int cell = reader.ReadInt32();
                    var stage = (BoxStationFrameStage)reader.ReadByte();
                    byte workStage = reader.ReadByte(), integrity = reader.ReadByte();
                    ushort steel = reader.ReadUInt16(), plasteel = reader.ReadUInt16();
                    var wallKind = (BoxStationWallKind)reader.ReadByte();
                    var layout = BoxStationLayout.Current;
                    if (cell <= previous || cell >= layout.Cells.Length
                        || !BoxStationHullRepair.TryConstruction(layout, 2 * layout.Cells.Length + cell, out _)
                        || stage is < BoxStationFrameStage.Empty or > BoxStationFrameStage.Wall
                        || wallKind is < BoxStationWallKind.None or > BoxStationWallKind.Reinforced
                        || (stage == BoxStationFrameStage.Wall) != (wallKind != BoxStationWallKind.None)
                        || workStage > 6 || integrity > 100
                        || steel > constructionBudget.Steel + importedSteel || plasteel > constructionBudget.Plasteel)
                        throw new InvalidDataException();
                    construction[index] = new(cell, stage, workStage, integrity, steel, plasteel, wallKind);
                    previous = cell;
                }
                else if (section == 4)
                {
                    ushort cell = reader.ReadUInt16();
                    if (cell <= previous || !BoxStationHullRepair.FloorCovering(BoxStationLayout.Current, cell))
                        throw new InvalidDataException();
                    removed[index] = cell;
                    previous = cell;
                }
                else if (section == 5)
                {
                    int edge = reader.ReadInt32();
                    var stage = (BoxStationDoorStage)reader.ReadByte();
                    if (edge <= previous || !BoxStationDoors.ValidEdge(BoxStationLayout.Current, edge)
                        || stage is < BoxStationDoorStage.Frame or > BoxStationDoorStage.Complete)
                        throw new InvalidDataException();
                    doorConstruction[index] = new(edge, stage);
                    previous = edge;
                }
                else storageOpen[index] = Boolean(reader);
            }
            return new(hullRevision, budget, installed, nextBundleId, sourceCount, pendingCount, bundleCount,
                section, offset, stocks, pending, bundles, constructionBudget, constructionInstalled,
                constructionWaste, importedSteel, constructionCount, construction, consumedFloorTiles,
                cableRevision, cableBudget, cableInstalled, floorRevision, lostTiles, removedCount, removed,
                doorRevision, doorElectronicsBudget, doorCount, doorConstruction, storageCount, storageOpen, fabrication, exportedSteel);
        }
    }
}
