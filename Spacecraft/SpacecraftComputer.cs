// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Threading;
using BepInEx.Logging;

namespace Modstoke.Client;

internal enum SpacecraftComputerMode { Offline, Standby, Nominal, Advisory, Warning, Emergency }
internal readonly record struct SpacecraftComputerReport(SpacecraftComputerMode Mode, string Summary, string Action);

// Unity telemetry is copied on the owning thread. One shared worker advances only
// numeric ship systems; commands and impacts serialize against that same state.
internal sealed class ClientSpacecraftComputer : IDisposable
{
    private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("Modstoke spacecraft computer");
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly object _gate = new();
    private readonly ClientSpacecraftSystems _systems = new();
    private readonly PendingInterval[] _pending = new PendingInterval[32];
    private SpacecraftSystemState _state;
    private SpacecraftComputerReport _report;
    private int _pendingCount;
    private Guid _shoreEpoch, _shoreShip;
    private Exception? _failure;
    private bool _failureLogged, _disposed;

    internal ClientSpacecraftComputer()
    {
        Publish();
        Worker.Add(this);
    }

    internal SpacecraftSystemState State
    {
        get { ReportFailure(); lock (_gate) return _state; }
    }

    internal SpacecraftComputerReport Report
    {
        get { ReportFailure(); lock (_gate) return _report; }
    }

    internal bool CanThrust
    {
        get { lock (_gate) return !_disposed && _failure == null && _systems.CanThrust; }
    }

    internal void Reset()
    {
        RequireThread();
        ReportFailure();
        lock (_gate)
        {
            if (_disposed) return;
            _pendingCount = 0;
            _shoreEpoch = _shoreShip = Guid.Empty;
            _failure = null;
            _failureLogged = false;
            _systems.Reset();
            Publish();
        }
    }

    internal SpacecraftSystemState Update(float dt, SpacecraftFlightState flight, int crewCount,
        bool innerDoorClosed = true, int airlockCrewCount = 0, bool cabinDoorClosed = true, int entryCrewCount = 0,
        SpacecraftRadiation radiation = default)
    {
        RequireThread();
        ReportFailure();
        if (!float.IsFinite(dt) || dt <= 0) return State;
        var sample = FromFlight(flight, crewCount, innerDoorClosed, airlockCrewCount, cabinDoorClosed, entryCrewCount);
        SpacecraftSystemState state;
        lock (_gate)
        {
            if (_disposed || _failure != null) return _state;
            sample = sample with { ShoreConnected = _shoreShip != Guid.Empty && flight.Docked, Radiation = radiation };
            // Keep each interval's telemetry and step boundary. A stalled worker
            // applies backpressure here instead of discarding elapsed time.
            if (_pendingCount == _pending.Length) ProcessPending();
            if (_failure != null) return _state;
            _pending[_pendingCount++] = new(dt, sample);
            state = _state;
        }
        Worker.Wake();
        return state;
    }

    internal void FinishPendingSteps()
    {
        RequireThread();
        lock (_gate)
        {
            ProcessPending();
            if (_disposed || _failure != null)
                throw new InvalidOperationException("The ship computer cannot finish its pending simulation steps.", _failure);
        }
    }

    internal SavedSpacecraftSystems Capture()
    {
        RequireThread();
        ReportFailure();
        lock (_gate)
        {
            ProcessPending();
            if (_disposed || _failure != null)
                throw new InvalidOperationException("The ship computer cannot capture its systems.", _failure);
            var saved = _systems.Capture();
            if (!ClientSpacecraftSystems.IsValid(saved))
                throw new InvalidOperationException("The ship systems are outside their saved-state limits.");
            return saved;
        }
    }

    internal bool TryRestore(SavedSpacecraftSystems saved)
    {
        RequireThread();
        ReportFailure();
        lock (_gate)
        {
            if (_disposed || !_systems.TryRestore(saved)) return false;
            _pendingCount = 0;
            _shoreEpoch = _shoreShip = Guid.Empty;
            _failure = null;
            _failureLogged = false;
            Publish();
            return true;
        }
    }

    internal bool TryCommand(string command, SpacecraftFlightState flight, out string feedback,
        bool innerDoorClosed = true, bool cabinDoorClosed = true)
    {
        RequireThread();
        ReportFailure();
        var sample = FromFlight(flight, 0, innerDoorClosed, 0, cabinDoorClosed, 0);
        lock (_gate)
        {
            ProcessPending();
            if (_disposed || _failure != null)
            { feedback = "Ship computer is unavailable."; return false; }
            sample = sample with { ShoreConnected = _shoreShip != Guid.Empty && flight.Docked };
            bool accepted = _systems.TryCommand(command, sample, out feedback);
            if (accepted) Publish();
            return accepted;
        }
    }

    internal bool TryManualRepair(string command, SpacecraftFlightState flight, SpacecraftSystemState expectedState,
        out string feedback, bool innerDoorClosed = true, bool cabinDoorClosed = true)
    {
        RequireThread();
        ReportFailure();
        var sample = FromFlight(flight, 0, innerDoorClosed, 0, cabinDoorClosed, 0);
        lock (_gate)
        {
            ProcessPending();
            if (_disposed || _failure != null)
            { feedback = "Ship computer is unavailable."; return false; }
            if (!ClientSpacecraftSystems.TryManualRepair(command, out _, out _)
                || !ClientSpacecraftSystems.ManualRepairTargetUnchanged(command, expectedState, _systems.State))
            { feedback = "The repair target changed. Release primary and start again."; return false; }
            if (_systems.State.Fire.Active)
            { feedback = "Extinguish the onboard fire before starting repairs."; return false; }
            sample = sample with { ShoreConnected = _shoreShip != Guid.Empty && flight.Docked };
            bool accepted = _systems.TryCommand(command, sample, out feedback);
            if (accepted) Publish();
            return accepted;
        }
    }

    internal bool TryServiceEva(SpacecraftEvaSuit suit, out string feedback)
    {
        RequireThread();
        ReportFailure();
        lock (_gate)
        {
            ProcessPending();
            if (_disposed || _failure != null)
            { feedback = "Ship computer is unavailable."; return false; }
            bool accepted = _systems.TryServiceEva(suit, out feedback);
            if (accepted) Publish();
            return accepted;
        }
    }

    internal void SetShoreConnection(Guid epoch, Guid ship)
    {
        RequireThread();
        ReportFailure();
        if (epoch == Guid.Empty || ship == Guid.Empty) epoch = ship = Guid.Empty;
        lock (_gate)
        {
            if (_disposed || _failure != null || _shoreEpoch == epoch && _shoreShip == ship) return;
            // Flush before invalidating: queued time belongs to the old berth.
            ProcessPending();
            if (_failure != null) return;
            _systems.DisconnectShorePower();
            _shoreEpoch = epoch;
            _shoreShip = ship;
            Publish();
        }
    }

    internal bool ServiceFromStation(BoxStationSystems station, int area, int fuelTank, int coolantTank,
        SpacecraftFlightState flight, float dt)
    {
        RequireThread();
        ReportFailure();
        if (!float.IsFinite(dt) || dt <= 0 || dt > .1f) throw new ArgumentOutOfRangeException(nameof(dt));
        lock (_gate)
        {
            if (_disposed || _failure != null || _shoreShip == Guid.Empty) return false;
            ProcessPending();
            if (_failure != null) return false;
            // Station servicing precedes the next ship step; an engine change may be newer than its completed interval.
            var current = FromFlight(flight, 0, true, 0, true, 0)
                with { ShoreConnected = _shoreShip != Guid.Empty && flight.Docked };
            _systems.CheckShoreFuelSafety(current);
            _systems.CheckShoreCoolantSafety(current);
            // Air, fuel and coolant services take priority over battery charging, in that order.
            _systems.RefillFromStation(station, dt);
            bool fuelChanged = _systems.RefillFuelFromStation(station, area, fuelTank, dt);
            bool coolantChanged = _systems.RefillCoolantFromStation(station, area, coolantTank, dt);
            if (_systems.TryTakeShoreChargeRequest(dt, out double request))
            {
                double supplied = request > 0 ? station.Power.Draw(area, BoxStationPowerChannel.Equipment, request) : 0;
                _systems.CreditShoreCharge(supplied);
            }
            Publish();
            return fuelChanged || coolantChanged;
        }
    }

    internal bool LoadStationSpares(BoxStationHullRepair station, Guid epoch, Guid ship, Guid holder,
        ulong actor, int expectedBundleId, SpacecraftFlightState flight, out string feedback)
    {
        RequireThread();
        ReportFailure();
        lock (_gate)
        {
            ProcessPending();
            if (_disposed || _failure != null)
            { feedback = "Ship computer is unavailable."; return false; }
            if (epoch == Guid.Empty || ship == Guid.Empty || epoch != _shoreEpoch || ship != _shoreShip
                || !flight.Docked || flight.Travel != SpacecraftTravel.None)
            { feedback = "Latch the ship to the station berth before loading repair stores."; return false; }
            if (!_systems.LoadStationSpares(station, holder, actor, expectedBundleId, out feedback)) return false;
            Publish();
            return true;
        }
    }

    internal void ReopenObstructedDoor(bool cabin)
    {
        RequireThread();
        ReportFailure();
        lock (_gate)
        {
            ProcessPending();
            if (_disposed || _failure != null) return;
            _systems.ReopenObstructedDoor(cabin);
            Publish();
        }
    }

    internal bool CanOpenOuter(SpacecraftFlightState flight, out string reason, bool innerDoorClosed = true,
        bool cabinDoorClosed = true)
    {
        RequireThread();
        ReportFailure();
        var sample = FromFlight(flight, 0, innerDoorClosed, 0, cabinDoorClosed, 0);
        lock (_gate)
        {
            ProcessPending();
            if (_disposed || _failure != null)
            { reason = "Ship computer is unavailable."; return false; }
            return _systems.CanOpenOuter(sample, out reason);
        }
    }

    internal bool CanWarp(out string reason)
    {
        RequireThread();
        ReportFailure();
        lock (_gate)
        {
            ProcessPending();
            if (_disposed || _failure != null)
            { reason = "Ship computer is unavailable."; return false; }
            return _systems.CanWarp(out reason);
        }
    }

    internal static bool CanWarp(SpacecraftSystemState state) => ClientSpacecraftSystems.CanWarp(state, out _);
    internal static bool CanWarp(SpacecraftSystemState state, out string reason)
        => ClientSpacecraftSystems.CanWarp(state, out reason);

    internal bool CanContinueWarp(out string reason, bool completing)
    {
        RequireThread();
        ReportFailure();
        lock (_gate)
        {
            // Observe completed worker state during countdown; settle all elapsed
            // simulation before allowing the actual frame transition.
            if (completing) ProcessPending();
            if (_disposed || _failure != null)
            { reason = "Ship computer is unavailable."; return false; }
            return _systems.CanWarp(out reason, starting: false);
        }
    }

    internal bool Impact(float energyJoules, float localX, float localY, float localZ)
    {
        RequireThread();
        ReportFailure();
        lock (_gate)
        {
            ProcessPending();
            if (_disposed || _failure != null || !_systems.ApplyImpact(energyJoules, localX, localY, localZ)) return false;
            Publish();
            return true;
        }
    }

    internal bool SprayFire(BoxStationFirefighting tools, Guid holder, ulong actor,
        ReadOnlySpan<SpacecraftCompartment> hitSections, out int item, out float units, out string status)
    {
        RequireThread();
        ReportFailure();
        item = -1;
        units = 0;
        if (hitSections.Length != 5) throw new ArgumentException("A spray has five resolved rays.", nameof(hitSections));
        foreach (var section in hitSections)
            if (section > SpacecraftCompartment.Entry && (byte)section != byte.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(hitSections));
        lock (_gate)
        {
            ProcessPending();
            if (_disposed || _failure != null)
            { status = "Ship computer is unavailable."; return false; }
            int held = tools.Held(holder);
            if (held < 0 || tools.State.Items[held].Actor != actor)
            { status = "The held extinguisher changed."; return false; }
            if (!tools.TrySpray(holder, out item, out units, out status)) return false;
            foreach (var section in hitSections)
                if (section <= SpacecraftCompartment.Entry) _systems.SprayFire(section, units / 5);
            Publish();
            return true;
        }
    }

    private static SpacecraftTelemetry FromFlight(SpacecraftFlightState flight, int crewCount,
        bool innerDoorClosed, int airlockCrewCount, bool cabinDoorClosed, int entryCrewCount)
    {
        float translation = MathF.Sqrt(flight.TranslationInput.x * flight.TranslationInput.x
            + flight.TranslationInput.y * flight.TranslationInput.y + flight.TranslationInput.z * flight.TranslationInput.z);
        float rotation = MathF.Sqrt(flight.RotationInput.x * flight.RotationInput.x
            + flight.RotationInput.y * flight.RotationInput.y + flight.RotationInput.z * flight.RotationInput.z);
        float speed = flight.Velocity.x * flight.Velocity.x + flight.Velocity.y * flight.Velocity.y
            + flight.Velocity.z * flight.Velocity.z;
        float spin = flight.AngularVelocity.x * flight.AngularVelocity.x + flight.AngularVelocity.y * flight.AngularVelocity.y
            + flight.AngularVelocity.z * flight.AngularVelocity.z;
        if (!float.IsFinite(translation) || !float.IsFinite(rotation) || !float.IsFinite(speed)
            || !float.IsFinite(spin) || !float.IsFinite(flight.DoorAmount))
            throw new InvalidOperationException("Spacecraft telemetry contains a nonfinite motion or door value.");
        float effort = Math.Clamp(translation + rotation * .3f, 0, 1) * (flight.BoostActive ? 1.8f : 1);
        return new(flight.Powered, flight.Grounded, flight.Region, flight.Travel, flight.DoorOpen,
            Math.Clamp(flight.DoorAmount, 0, 1), effort, speed, spin, Math.Clamp(crewCount, 0, 64),
            innerDoorClosed, Math.Clamp(airlockCrewCount, 0, Math.Clamp(crewCount, 0, 64)),
            cabinDoorClosed, Math.Clamp(entryCrewCount, 0, Math.Clamp(crewCount - airlockCrewCount, 0, 64)));
    }

    private void Process()
    {
        lock (_gate) ProcessPending();
    }

    // The gate is held by the worker, a full queue or a command/impact/departure boundary.
    // Draining preceding time there prevents a new command from acting retroactively.
    private void ProcessPending()
    {
        if (_disposed || _failure != null || _pendingCount == 0) return;
        int count = _pendingCount;
        _pendingCount = 0;
        try
        {
            for (int i = 0; i < count; i++)
            {
                var interval = _pending[i];
                double remaining = interval.Seconds;
                while (remaining > 0)
                {
                    float step = (float)Math.Min(remaining, .5);
                    _systems.Step(step, interval.Telemetry);
                    remaining -= step;
                }
            }
            Publish();
        }
        catch (Exception error) { Fail(error); }
    }

    private void Publish()
    {
        _state = _systems.State;
        _report = Analyze(_state);
    }

    private void Fail(Exception error)
    {
        _failure ??= error;
        _pendingCount = 0;
        _shoreEpoch = _shoreShip = Guid.Empty;
        _systems.ResetShoreAir();
        _systems.ResetShoreFuel();
        _systems.ResetShoreCoolant();
        _state = _state with
        {
            Ready = false, ShoreAirEnabled = false, ShoreAirStatus = ShoreAirStatus.Off,
            ShoreAirMolesPerSecond = 0, ShoreAirWatts = 0,
            ShoreFuelEnabled = false, ShoreFuelStatus = ShoreFuelStatus.Off,
            ShoreCoolantEnabled = false, ShoreCoolantStatus = ShoreCoolantStatus.Off
        };
        _report = new(SpacecraftComputerMode.Offline, "COMPUTER FAULT", "Systems control is unavailable.");
    }

    private void ReportFailure()
    {
        if (Environment.CurrentManagedThreadId != _thread) return;
        Exception? failure;
        lock (_gate)
        {
            if (_failureLogged || _failure == null) return;
            _failureLogged = true;
            failure = _failure;
        }
        Log.LogError("Spacecraft computer stopped: " + failure);
    }

    private void RequireThread()
    {
        if (Environment.CurrentManagedThreadId != _thread)
            throw new InvalidOperationException("Capture spacecraft telemetry and commands on the owning game thread.");
    }

    internal static SpacecraftComputerReport Analyze(SpacecraftSystemState state)
    {
        if (!state.Ready) return new(SpacecraftComputerMode.Offline, "AWAITING TELEMETRY", "Wait for the ship connection.");
        if (state.Fire.Active)
        {
            string alarm = state.Fire.Cabin.Active ? "CABIN FIRE" : state.Fire.Airlock.Active ? "AIRLOCK FIRE" : "AFT ENTRY FIRE";
            return new(SpacecraftComputerMode.Emergency, alarm, "Isolate the compartment and extinguish the fire.");
        }
        if (state.CabinPressure < 35 || state.CabinPressure * state.OxygenFraction < 10)
            return new(SpacecraftComputerMode.Emergency, "CABIN ATMOSPHERE CRITICAL", "Seal the cabin and restore life support.");
        if (state.CO2Fraction > .03f)
            return new(SpacecraftComputerMode.Emergency, "CARBON DIOXIDE CRITICAL", "Restore scrubbers or reach breathable air.");
        if (state.PowerIntegrity01 <= .08f)
            return new(SpacecraftComputerMode.Emergency, "POWER BUS FAILED", "Repair the power system; life support is offline.");
        if (state.Hull.MinimumIntegrity01 <= .3f)
            return new(SpacecraftComputerMode.Emergency, "HULL INTEGRITY CRITICAL", "Isolate the airlock and return for repairs.");
        if (state.CabinTemperatureC < 0 || state.CabinTemperatureC > 50)
            return new(SpacecraftComputerMode.Emergency, "CABIN TEMPERATURE CRITICAL", "Restore thermal control or leave the compartment.");
        if (state.CabinPressure < 70 || state.CabinPressure * state.OxygenFraction < 16)
            return new(SpacecraftComputerMode.Warning, "CABIN OXYGEN LOW", "Check hatches, hull damage and breathing-gas reserve.");
        if (state.CO2Fraction > .01f)
            return new(SpacecraftComputerMode.Warning, "CARBON DIOXIDE RISING", "Enable scrubbers and check life-support power.");
        if (state.LifeSupportIntegrity01 <= .1f)
            return new(SpacecraftComputerMode.Warning, "LIFE SUPPORT FAILED", "Return for repairs before cabin air is exhausted.");
        if (state.Battery01 <= .00001f && state.Electrical.GenerationKw <= 0)
            return new(SpacecraftComputerMode.Warning, "ELECTRICAL BUS OFFLINE", state.ApuIsolated
                ? "APU is isolated; recharge at a powered station berth."
                : "Open the APU fuel valve and enable the generator, or recharge at a powered station berth.");
        if (!SpacecraftElectrical.IsPowered(state.Electrical, SpacecraftCircuit.LifeSupport))
            return new(SpacecraftComputerMode.Warning, "LIFE SUPPORT CIRCUIT OFFLINE",
                "Check its breaker, damage and supply on the engineering electrical page.");
        if (state.Electrical.TrippedMask != 0)
            return new(SpacecraftComputerMode.Warning, "ELECTRICAL BRANCH TRIPPED",
                "Isolate damaged equipment and repair it before reconnecting its breaker.");
        if (state.LineFaultMask != 0)
            return new(SpacecraftComputerMode.Warning, "SUPPLY CONNECTION FAULT",
                "Check engineering diagnostics. Close the affected valve, repair its connection, then reopen it.");
        if (state.Electrical.ShedMask != 0)
            return new(SpacecraftComputerMode.Warning, "ELECTRICAL LOAD SHEDDING",
                "Enable the generator or isolate lower-priority circuits at engineering.");
        if (state.EngineIntegrity01 <= .15f)
            return new(SpacecraftComputerMode.Warning, "ENGINE FAILED", "Repair the engine before applying thrust.");
        if (state.WarpIntegrity01 <= .35f)
            return new(SpacecraftComputerMode.Warning, "WARP DRIVE FAILED", "Repair the warp drive before plotting a jump.");
        if (state.EmergencyOverride)
            return new(SpacecraftComputerMode.Warning, "AIRLOCK INTERLOCK BYPASSED", "Restore the interlock after the emergency procedure.");
        if (state.Hull.MinimumIntegrity01 < .85f)
            return new(SpacecraftComputerMode.Warning, "HULL DAMAGE DETECTED", "Monitor pressure and arrange repairs.");
        if (state.OxygenReserve01 < .1f)
            return new(SpacecraftComputerMode.Warning, "BREATHING GAS RESERVE LOW", "Avoid venting and replenish supplies on the island.");
        if (state.Coolant01 < .1f)
            return new(SpacecraftComputerMode.Warning, "COOLANT RESERVE LOW", "Isolate the coolant feed if leaking; return to the island for repair and refill.");
        if (state.CoolantPressure <= 1)
            return new(SpacecraftComputerMode.Warning, "COOLANT FLOW LOST", "Check the coolant feed, radiator, cooling valves and electrical power.");
        if (state.Thermal.CoolantKelvin >= 353.15f)
            return new(SpacecraftComputerMode.Warning, "COOLANT LOOP OVERHEATED", "Reduce electrical and engine loads; check radiator flow.");
        if (state.Thermal.CabinHullKelvin >= 393.15f || state.Thermal.AirlockHullKelvin >= 393.15f
            || state.Thermal.EntryHullKelvin >= 393.15f)
            return new(SpacecraftComputerMode.Warning, "HULL TEMPERATURE HIGH", "Reduce heat sources and turn the hull away from sunlight.");
        if (state.CabinTemperatureC < 12 || state.CabinTemperatureC > 35)
            return new(SpacecraftComputerMode.Warning, "CABIN TEMPERATURE UNSAFE", "Restore cabin coolant flow and check life-support integrity and power.");
        if (state.Fuel01 < .1f)
            return new(SpacecraftComputerMode.Advisory, "FUEL RESERVE LOW", "Return to the island for servicing.");
        if (state.Battery01 < .1f)
            return new(SpacecraftComputerMode.Advisory, "BATTERY RESERVE LOW", state.ApuIsolated
                ? "Reduce demand and recharge at a powered station berth."
                : "Enable the generator or recharge at a powered station berth.");
        if (state.ApuIsolated)
            return new(SpacecraftComputerMode.Advisory, "APU ISOLATED", "Generator restart is inhibited until an island service reset.");
        if (!state.ScrubberEnabled)
            return new(SpacecraftComputerMode.Advisory, "SCRUBBERS DISABLED", "Enable scrubbers before cabin carbon dioxide rises.");
        if (state.AirlockCycle == SpacecraftAirlockPhase.Fault)
            return new(SpacecraftComputerMode.Advisory, "AIRLOCK CYCLE STOPPED", "Check the airlock's fault readout before starting another cycle.");
        if (state.AirlockCycle is SpacecraftAirlockPhase.Sealing or SpacecraftAirlockPhase.Equalizing)
            return new(SpacecraftComputerMode.Standby, "AIRLOCK CYCLE ACTIVE", "Keep both pressure doorways clear while the chamber equalizes.");
        if (!state.Thermal.EnvironmentAvailable)
            return new(SpacecraftComputerMode.Advisory, "THERMAL ENVIRONMENT UNAVAILABLE", "External heat exchange is awaiting orbital telemetry.");
        if (!CanWarp(state, out var reason))
            return new(SpacecraftComputerMode.Standby, "SYSTEMS STABLE", reason);
        return new(SpacecraftComputerMode.Nominal, "ALL SYSTEMS NOMINAL", "Warp drive ready. Select a destination at navigation.");
    }

    public void Dispose()
    {
        ReportFailure();
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _pendingCount = 0;
            _shoreEpoch = _shoreShip = Guid.Empty;
            _systems.ResetShoreFuel();
            _systems.ResetShoreCoolant();
            _state = _state with
            {
                Ready = false, ShoreFuelEnabled = false, ShoreFuelStatus = ShoreFuelStatus.Off,
                ShoreCoolantEnabled = false, ShoreCoolantStatus = ShoreCoolantStatus.Off
            };
            _report = new(SpacecraftComputerMode.Offline, "COMPUTER OFFLINE", "");
        }
        Worker.Remove(this);
    }

    private readonly record struct PendingInterval(float Seconds, SpacecraftTelemetry Telemetry);

    private static class Worker
    {
        private const int Capacity = 64;
        private static readonly object Gate = new();
        private static readonly List<ClientSpacecraftComputer> Computers = new(Capacity);
        private static readonly AutoResetEvent Signal = new(false);
        private static Thread? _worker;

        internal static void Add(ClientSpacecraftComputer computer)
        {
            lock (Gate)
            {
                if (Computers.Count >= Capacity) throw new InvalidOperationException("The spacecraft computer limit has been reached.");
                Computers.Add(computer);
                if (_worker == null)
                {
                    _worker = new Thread(Run) { IsBackground = true, Name = "Modstoke spacecraft computers" };
                    try { _worker.Start(); }
                    catch { _worker = null; Computers.Remove(computer); throw; }
                }
            }
            Signal.Set();
        }

        internal static void Wake() => Signal.Set();

        internal static void Remove(ClientSpacecraftComputer computer)
        {
            lock (Gate) Computers.Remove(computer);
            Signal.Set();
        }

        private static void Run()
        {
            var batch = new ClientSpacecraftComputer[Capacity];
            while (true)
            {
                Signal.WaitOne();
                int count;
                lock (Gate)
                {
                    count = Computers.Count;
                    if (count == 0) { _worker = null; return; }
                    Computers.CopyTo(batch);
                }
                for (var i = 0; i < count; ++i)
                {
                    var computer = batch[i];
                    try { computer.Process(); }
                    catch (Exception error) { lock (computer._gate) computer.Fail(error); }
                    batch[i] = null!;
                }
            }
        }
    }
}
