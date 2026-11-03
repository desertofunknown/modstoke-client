// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

namespace Modstoke.Client;

internal sealed partial class ClientTerminal
{
    private ClientDroneView? _previousDroneAudio;

    private void PlayTouchFeedback()
    {
        if (_open && !_failed && !Fullscreen && _tablet.IsOpen)
            Plugin.TabletPresence.NotifyInteraction();
        if (AudioPreferences is { } preferences)
            _alerts?.PlayDeviceCue(preferences, ClientDeviceCue.Touch);
    }

    private bool WatchCommand(ClientWatchCommand command)
    {
        if (!_os.WatchCommand(command)) return false;
        PlayTouchFeedback();
        return true;
    }

    private void ObserveDroneAudio(ClientDroneView current)
    {
        if (AudioPreferences is { } currentPreferences) _alerts?.UpdateDeviceVolume(currentPreferences);
        var previous = _previousDroneAudio;
        // Always advance the baseline while muted. Reconnection or unmuting must
        // not replay operations that happened before this observation.
        _previousDroneAudio = current;
        if (AudioPreferences is not { } preferences || previous == null
            || !previous.Ready || !current.Ready || !previous.Link || !current.Link
            || !previous.Deployed || !current.Deployed || previous.DroneId != current.DroneId)
            return;

        if (current.Range >= 0 && current.Range <= 4)
        {
            if (previous.InstalledPackId != current.InstalledPackId)
                _alerts?.PlayDeviceCue(preferences, ClientDeviceCue.BatteryLatch);
            else if (previous.Readiness is { } before && current.Readiness is { } after
                && before.SolarDeployed != after.SolarDeployed)
                _alerts?.PlayDeviceCue(preferences, after.SolarDeployed ? ClientDeviceCue.SolarOpen : ClientDeviceCue.SolarClose);
        }
        if (previous.Armed != current.Armed && (_open || current.Range >= 0 && current.Range <= 4))
            _alerts?.PlayDeviceCue(preferences, current.Armed ? ClientDeviceCue.Arm : ClientDeviceCue.Disarm);
    }
}
