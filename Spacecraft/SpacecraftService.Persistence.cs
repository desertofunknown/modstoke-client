// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Modstoke.Sons;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftService
{
    private const string SaveOwner = "modstoke.client", SaveName = "spacecraft-systems-v1";
    private const int SaveLengthLimit = WorldRecords.MaxRecordBytes;
    private static readonly JsonSerializerOptions SaveOptions = new() { MaxDepth = 8 };
    private IDisposable? _saveRegistration;
    private SavedSpacecraft? _savedShip;
    private SavedSpacecraftWorld? _pendingOffline;
    private Guid _offlineWorld;
    private bool _offlineLoadComplete, _loadInvalid;

    private void SetOfflineRegistration(bool active)
    {
        if (active) _saveRegistration ??= SaveRegistry.Register(new OfflineSave(this));
        else { _saveRegistration?.Dispose(); _saveRegistration = null; }
    }

    private bool InvalidSave()
    {
        _loadInvalid = true;
        _ready = false;
        _status = "The spacecraft save record is unavailable or invalid. Saved data was preserved.";
        return false;
    }

    private SavedSpacecraft? CaptureShip()
    {
        if (_body == null) return _savedShip;
        if (!_body.Ready || _shipId == Guid.Empty)
            throw new InvalidOperationException("The spacecraft has no valid vessel identity for saving.");
        var systems = _systems.Capture();
        var flight = _body.State;
        var saved = new SavedSpacecraft
        {
            Id = _shipId, Systems = systems,
            RearDoorOpen = flight.DoorOpen, RearDoorAmount = flight.DoorAmount,
            InnerDoorAmount = flight.InnerDoorAmount, CabinDoorAmount = flight.CabinDoorAmount,
            GearDeployed = flight.GearDeployed, GearAmount = flight.GearAmount, Docked = flight.Docked,
            Navigation = CaptureNavigation(flight)
        };
        if (!ValidShip(saved)) throw new InvalidOperationException("The spacecraft has invalid saved actuator state.");
        return saved;
    }

    private SavedSpacecraftWorld CaptureWorld(Guid world)
    {
        if (world == Guid.Empty) throw new InvalidOperationException("No spacecraft save world is active.");
        if (_restoreNavigation) throw new InvalidOperationException("The saved spacecraft is still being restored.");
        if (_body != null && _orbits is { PendingSeconds: >= 1 })
            throw new InvalidOperationException("The spacecraft's orbital clock has unfinished steps.");
        return new SavedSpacecraftWorld { World = world, Ship = CaptureShip(), Orbits = CaptureOrbits(),
            BoxStation = _stationSystems.Capture(), EvaSuits = CaptureEva() };
    }

    private void StoreWorld()
    {
        if (!_ready || _loading || _loadInvalid || IsClient || _world == null) return;
        var result = WorldRecords.WriteJson(_world, SaveOwner, SaveName, SaveWorld(_world.WorldId));
        if (result != WorldRecordStatus.Success)
        {
            _status = "Spacecraft changes have not been accepted by the world save.";
            Console.WriteLine("Spacecraft world record write failed: " + result);
        }
    }

    private string SaveWorld(Guid world)
    {
        var json = JsonSerializer.Serialize(CaptureWorld(world), SaveOptions);
        if (json.Length > SaveLengthLimit || Encoding.UTF8.GetByteCount(json) > SaveLengthLimit)
            throw new InvalidOperationException("The spacecraft record exceeds its save limit.");
        return json;
    }

    private bool LoadShip(string json, Guid world)
    {
        if (json == null || json.Length > SaveLengthLimit || Encoding.UTF8.GetByteCount(json) > SaveLengthLimit) return false;
        try { return RestoreShipRecord(JsonSerializer.Deserialize<SavedSpacecraftWorld>(json, SaveOptions), world); }
        catch (JsonException) { return false; }
    }

    private bool RestoreShipRecord(SavedSpacecraftWorld? saved, Guid world)
    {
        if (!ValidWorld(saved, world)) return false;
        OrbitalSimulation orbits;
        if (saved!.Version == 1) orbits = new(OrbitalStationOrbit.Bodies(OrbitalCatalog.Current));
        else if (!TryRestoreOrbits(saved.Orbits, saved.Version, out orbits)) return false;
        BoxStationSystems station;
        if (saved.Version < 7) station = new BoxStationSystems(BoxStationLayout.Current);
        else if (saved.Version < 9)
        {
            if (!BoxStationSystems.TryMigrate(saved.Station, out station)) return false;
        }
        else if (!BoxStationSystems.TryRestore(saved.BoxStation, out station)) return false;
        SpacecraftEvaSuit[] suits;
        if (saved.Version < 8) suits = [new(), new(), new()];
        else if (!TryRestoreEva(saved.EvaSuits, out suits)) return false;
        // The world owns the vessel's position; player poses and live leases
        // remain under the native island return guard.
        _savedShip = saved!.Ship;
        _migrateStationDock = saved.Version < 9 && _savedShip?.Docked == true;
        if (saved.Version < 6 && _savedShip != null) _savedShip.Docked = false;
        _restoreNavigation = _savedShip?.Navigation != null;
        _nextRestoreAttempt = 0;
        _orbits = orbits;
        _stationSystems = station;
        _stationState = station.State;
        _stationSolarSample = double.NaN;
        _stationRadiation = default;
        ResetStationTransport();
        ResetPortableTransport();
        ResetHullTransport();
        ReleaseOrbitalStation();
        _stationFailed = false;
        _nextHeldFirefighting = 0;
        _nextStationSnapshot = 0;
        _evaSuits = suits;
        Array.Clear(_evaLeases);
        _evaState = null;
        _nextEvaSnapshot = 0;
        _orbitView = null; _orbitViewTick = -1;
        return true;
    }

    private static bool ValidWorld(SavedSpacecraftWorld? saved, Guid world) => saved != null
        && saved.Version is >= 1 and <= 41 && world != Guid.Empty && saved.World == world
        && (saved.Version < 9 || saved.BoxStation?.Version == saved.Version - 8)
        && (saved.Version is >= 7 and < 9 ? saved.Station != null : saved.Station == null)
        && (saved.Version >= 9 ? saved.BoxStation != null : saved.BoxStation == null)
        && (saved.Version >= 8 ? saved.EvaSuits is { Length: SpacecraftEvaSuit.Count } : saved.EvaSuits == null)
        && (saved.Version == 1 ? saved.Orbits == null : saved.Orbits != null)
        && (saved.Version >= 3 || saved.Ship?.Navigation == null)
        && (saved.Ship?.Navigation is not { } navigation
            || saved.Version < 4 && navigation.Coast == null
            || saved.Version >= 4 && (navigation.Region == SpacecraftRegion.Space) == (navigation.Coast != null))
        && (saved.Ship?.Navigation == null || saved.Orbits!.PendingSeconds < 1)
        && (saved.Ship == null || (saved.Version >= 6) == saved.Ship.Docked.HasValue)
        && (saved.Ship == null || ValidShip(saved.Ship));

    private static bool ValidShip(SavedSpacecraft saved) => saved.Id != Guid.Empty
        && ClientSpacecraftSystems.IsValid(saved.Systems)
        && SavedFraction(saved.RearDoorAmount) && SavedFraction(saved.InnerDoorAmount)
        && SavedFraction(saved.CabinDoorAmount) && SavedFraction(saved.GearAmount)
        && (saved.Navigation == null || ValidNavigation(saved.Navigation))
        && (saved.Docked != true || saved.Navigation is { Region: SpacecraftRegion.Space, Powered: false }
            && saved.GearDeployed && saved.GearAmount >= .999f);

    private static bool SavedFraction(float value) => float.IsFinite(value) && value is >= 0 and <= 1;

    private sealed class OfflineSave(ClientSpacecraftService service) : ISaveable
    {
        public string ModId => SaveOwner;
        public string Name => SaveName + "-singleplayer";
        public bool IncludeInPlayerSave => false;

        public string Save()
        {
            if (!service.OnThread || BoltNetwork.isRunning || !service._ready || service._loading
                || service._loadInvalid || service._offlineWorld == Guid.Empty)
                throw new InvalidOperationException("No valid single-player spacecraft world is active.");
            return service.SaveWorld(service._offlineWorld);
        }

        public void Load(string json)
        {
            if (!service.OnThread || BoltNetwork.isRunning
                || !service._loading && (service._ready || !service._offlineLoadComplete))
                throw new InvalidOperationException("The spacecraft record does not belong to an active single-player load.");
            try
            {
                if (json == null || json.Length > SaveLengthLimit) throw new JsonException("Invalid spacecraft save length.");
                var saved = JsonSerializer.Deserialize<SavedSpacecraftWorld>(json, SaveOptions);
                if (saved == null || !ValidWorld(saved, saved.World)) throw new JsonException("Invalid spacecraft save state.");
                service._pendingOffline = saved;
            }
            catch (JsonException)
            {
                service.InvalidSave();
                throw;
            }
        }
    }

    public sealed class SavedSpacecraftWorld
    {
        [JsonRequired]
        public int Version { get; set; } = 41;
        [JsonRequired]
        public Guid World { get; set; }
        [JsonRequired]
        public SavedSpacecraft? Ship { get; set; }
        // Version 1 predates the orbital world; its first load starts at the catalog epoch.
        public SavedOrbits? Orbits { get; set; }
        public SavedOrbitalStationSystems? Station { get; set; }
        public SavedBoxStationSystems? BoxStation { get; set; }
        public SavedSpacecraftEvaSuit[]? EvaSuits { get; set; }
    }

    public sealed class SavedSpacecraft
    {
        [JsonRequired]
        public Guid Id { get; set; }
        [JsonRequired]
        public SavedSpacecraftSystems? Systems { get; set; }
        [JsonRequired]
        public bool RearDoorOpen { get; set; }
        [JsonRequired]
        public float RearDoorAmount { get; set; }
        [JsonRequired]
        public float InnerDoorAmount { get; set; }
        [JsonRequired]
        public float CabinDoorAmount { get; set; }
        [JsonRequired]
        public bool GearDeployed { get; set; }
        [JsonRequired]
        public float GearAmount { get; set; }
        public SavedNavigation? Navigation { get; set; }
        public bool? Docked { get; set; }
    }
}
