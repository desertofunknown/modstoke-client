// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Modstoke.Loader;

namespace Modstoke.Client;

public static class Plugin
{
    private static ClientAdmissionProvider? _provider;
    private static ClientTribeProvider? _tribeProvider;
    private static ClientMapProvider? _mapProvider;
    private static ClientServicesProvider? _servicesProvider;
    private static ClientCompanionProvider? _companionProvider;
    private static ClientDeviceProvider? _deviceProvider;
    private static ClientCharacterProvider? _characterProvider;
    private static readonly ClientCombatController Combat = new();
    internal static readonly ClientDroneService Drones = new();
    internal static readonly ClientTabletPresence TabletPresence = new();
    internal static readonly ClientCharacterService Characters = new();
    internal static readonly ClientCharacterCreator Creator = new();
    internal static readonly ClientAircraftService Aircraft = new();
    internal static readonly ClientSpacecraftService Spacecraft = new();
    internal static readonly ClientTerminal Terminal = new(() => Combat.AllowsHandheldTerminal
        && !Aircraft.UsingAircraft && !Spacecraft.UsingSpacecraft);
    private static bool _settingsRegistrationAttempted;

    public static void Initialize()
    {
        if (_provider != null)
            return;
        if (LoaderConfig.TryGetProviderSnapshot(ClientAdmissionProvider.SettingsId, out _))
            throw new InvalidOperationException("Modstoke Client settings are already registered.");

        var provider = new ClientAdmissionProvider();
        if (!MenuRegistry.TryRegisterProvider(provider))
            throw new InvalidOperationException("Modstoke Client menu is already registered.");

        _provider = provider;
        try
        {
            var tribe = new ClientTribeProvider();
            if (!MenuRegistry.TryRegisterProvider(tribe))
                throw new InvalidOperationException("Modstoke Client Tribe menu is already registered.");
            _tribeProvider = tribe;
            var maps = new ClientMapProvider();
            if (!MenuRegistry.TryRegisterProvider(maps))
                throw new InvalidOperationException("Modstoke Client Maps menu is already registered.");
            _mapProvider = maps;
            var services = new ClientServicesProvider();
            if (!MenuRegistry.TryRegisterProvider(services))
                throw new InvalidOperationException("Modstoke Client Services menu is already registered.");
            _servicesProvider = services;
            var companions = new ClientCompanionProvider();
            if (!MenuRegistry.TryRegisterProvider(companions))
                throw new InvalidOperationException("Modstoke Client Companions menu is already registered.");
            _companionProvider = companions;
            var devices = new ClientDeviceProvider();
            if (!MenuRegistry.TryRegisterProvider(devices))
                throw new InvalidOperationException("Modstoke Client Devices menu is already registered.");
            _deviceProvider = devices;
            var character = new ClientCharacterProvider();
            if (!MenuRegistry.TryRegisterProvider(character))
                throw new InvalidOperationException("Modstoke Client Character menu is already registered.");
            _characterProvider = character;
            // Complete registration before applying the loaded configuration.
            _settingsRegistrationAttempted = true;
            if (!LoaderConfig.RegisterOrUpdateProvider(provider))
                throw new InvalidOperationException("Could not register Modstoke Client settings.");
            provider.Start();
            tribe.Start();
            maps.Start();
            services.Start();
            companions.Start();
            devices.Start();
            character.Start();
            Combat.Start();
            Drones.Start();
            TabletPresence.Start();
            Terminal.Start();
            Aircraft.Start(() => !Spacecraft.UsingSpacecraft && Spacecraft.LocalRegion == SpacecraftRegion.Island
                && Terminal.TryAcquireAircraftInput(),
                Terminal.ReleaseAircraftInput, Terminal.CloseForAircraft);
            Spacecraft.Start(() => !Aircraft.UsingAircraft && Terminal.TryAcquireAircraftInput(allowPause: true),
                Terminal.ReleaseAircraftInput, () => Terminal.AircraftInputCaptured, Terminal.CloseForAircraft);
        }
        catch
        {
            Shutdown();
            throw;
        }
    }

    public static void Update()
    {
        try
        {
            _provider?.Update();
            _tribeProvider?.Update();
            _mapProvider?.Update();
            _servicesProvider?.Update();
            _companionProvider?.Update();
            _deviceProvider?.Update();
            _characterProvider?.Update();
            Combat.Update();
            Drones.Update(Terminal.AudioPreferences);
            TabletPresence.Update();
            Characters.Update();
            Creator.Update();
            Aircraft.Update(Terminal.AudioPreferences);
            Spacecraft.Update(Terminal.AudioPreferences);
            Terminal.Update();
            TabletPresence.SetLocalState(Terminal.TabletPresencePhase, Terminal.WatchPresencePhase, Terminal.ControllerPresentation);
        }
        catch
        {
            // The loader stops dispatching this mod after a failed callback. Retire
            // its providers too, so their menus cannot accept requests with no sender.
            try { Shutdown(); }
            catch (Exception cleanupError)
            {
                Console.WriteLine("Modstoke Client: shutdown after update failure was incomplete ("
                    + cleanupError.GetType().Name + ").");
            }
            throw;
        }
    }

    public static void FixedUpdate()
    {
        try { Drones.FixedUpdate(); Aircraft.FixedUpdate(); Spacecraft.FixedUpdate(); }
        catch
        {
            try { Spacecraft.Stop(); } finally { try { Aircraft.Stop(); } finally { Drones.Stop(); } }
            throw;
        }
    }

    public static void Shutdown()
    {
        try { Spacecraft.Stop(); }
        finally { ShutdownWorldServices(); }
    }

    private static void ShutdownWorldServices()
    {
        try { Aircraft.Stop(); }
        finally
        {
            try { Terminal.Stop(); }
            finally
            {
                try { Combat.Stop(); }
                finally
                {
                    try { Drones.Stop(); }
                    finally { try { TabletPresence.Stop(); } finally { try { try { Creator.Close(); } finally { Characters.Dispose(); } } finally { DeactivateProviders(); } } }
                }
            }
        }
    }

    private static void DeactivateProviders()
    {
        var provider = _provider;
        var tribe = _tribeProvider;
        var maps = _mapProvider;
        var services = _servicesProvider;
        var companions = _companionProvider;
        var devices = _deviceProvider;
        var character = _characterProvider;
        if (provider == null && tribe == null && maps == null && services == null && companions == null && devices == null
            && character == null)
            return;
        _provider = null;
        _tribeProvider = null;
        _mapProvider = null;
        _servicesProvider = null;
        _companionProvider = null;
        _deviceProvider = null;
        _characterProvider = null;
        // Never withdraw admission or disable a live gameplay gate during shutdown.
        try
        {
            character?.Deactivate();
            devices?.Deactivate();
            companions?.Deactivate();
            services?.Deactivate();
            maps?.Deactivate();
            tribe?.Deactivate();
            provider?.Deactivate();
            if (_settingsRegistrationAttempted)
            {
                _settingsRegistrationAttempted = false;
                LoaderConfig.TryUnregisterProvider(ClientAdmissionProvider.SettingsId);
            }
        }
        finally
        {
            try
            {
                try
                {
                    if (character != null) MenuRegistry.TryUnregisterProvider(ClientCharacterProvider.MenuId);
                    if (devices != null) MenuRegistry.TryUnregisterProvider(ClientDeviceProvider.MenuId);
                }
                finally
                {
                    if (companions != null) MenuRegistry.TryUnregisterProvider(ClientCompanionProvider.MenuId);
                }
            }
            finally
            {
                try
                {
                    if (services != null)
                        MenuRegistry.TryUnregisterProvider(ClientServicesProvider.MenuId);
                }
                finally
                {
                    try
                    {
                        if (maps != null)
                            MenuRegistry.TryUnregisterProvider(ClientMapProvider.MenuId);
                    }
                    finally
                    {
                        try
                        {
                            if (tribe != null)
                                MenuRegistry.TryUnregisterProvider(ClientTribeProvider.MenuId);
                        }
                        finally
                        {
                            if (provider != null)
                                MenuRegistry.TryUnregisterProvider(ClientAdmissionProvider.MenuId);
                        }
                    }
                }
            }
        }
    }
}
