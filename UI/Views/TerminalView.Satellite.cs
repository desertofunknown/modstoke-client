// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Modstoke.Client;

internal sealed partial class ClientTerminalView
{
    private TextMeshProUGUI _satelliteBar = null!;
    private TextMeshProUGUI _satelliteTitle = null!;
    private TextMeshProUGUI _satelliteDetail = null!;
    private TextMeshProUGUI _satelliteServer = null!;
    private ButtonWidget _satelliteToggle = null!;
    private ButtonWidget _satelliteReconnect = null!;
    private ButtonWidget _shadeSatellite = null!;
    private readonly Image[] _satelliteStages = new Image[3];
    private readonly TextMeshProUGUI[] _satelliteStageLabels = new TextMeshProUGUI[3];
    private static readonly string[] SatelliteStageNames = { "Find relay", "Synchronize", "Link ready" };
    private TextMeshProUGUI _satelliteNext = null!;
    private ClientTabletSatelliteSnapshot _settingsSatellite;

    private void BuildSatelliteSettings(RectTransform page)
    {
        var scroll = Scroll(page, "SatelliteSettings", 0f, 0f, 1044f, 548f);
        _settingsScrolls.Add(2, scroll);
        page = scroll.Content;
        Text(page, "SatelliteTitle", "Satellite link", 34f, Ink, 32f, 24f, 980f, 46f, true);
        Text(page, "RelayName", "MODSTOKE  /  ORBITAL FIELD RELAY", 22f, Muted, 32f, 80f,
            980f, 34f, role: TabletTextRole.Status);
        _satelliteTitle = Text(page, "LinkState", "", 32f, Mint, 32f, 134f, 980f, 46f, true);
        _satelliteDetail = Text(page, "LinkDetail", "", 24f, Muted, 32f, 186f, 980f, 64f);
        var labels = new[] { "01  Find relay", "02  Synchronize", "03  Link ready" };
        for (var index = 0; index < labels.Length; index++)
        {
            var x = 32f + index * 332f;
            _satelliteStages[index] = RoundedPanel(page, "Stage" + index, Raised, x, 270f, 312f, 8f);
            _satelliteStageLabels[index] = Text(page, "StageLabel" + index, labels[index], 22f, Muted, x, 292f, 312f, 48f,
                role: TabletTextRole.Status);
        }
        _satelliteToggle = Button(page, "Transceiver", "Transceiver on", 32f, 352f, 474f, 60f,
            () => _command("os", "satellite-toggle"));
        _satelliteReconnect = Button(page, "ReacquireRelay", "Reacquire relay", 526f, 352f, 486f, 60f,
            () => _command("os", "satellite-reconnect"));
        _satelliteServer = Text(page, "ServerServices", "", 23f, Ink, 32f, 432f, 980f, 76f,
            role: TabletTextRole.Status);
        Text(page, "SimulationNote", "Simulated field equipment. Server access and message delivery are separate; this link does not provide internet access.",
            21f, Muted, 32f, 520f, 980f, 72f);
        Panel(page, "NextRule", Rule, 32f, 612f, 980f, 2f);
        Text(page, "NextHeading", "What can I do?", 28f, Ink, 32f, 638f, 980f, 42f, true);
        _satelliteNext = Text(page, "NextStep", "", 24f, Muted, 32f, 696f, 980f, 96f);
        Button(page, "OpenMessages", "Open Messages", 32f, 814f, 474f, 64f,
            () => SelectApp(ClientTerminalApp.Messages));
        Button(page, "OpenMap", "Open Navigation", 526f, 814f, 486f, 64f,
            () => SelectApp(ClientTerminalApp.Navigation));
        scroll.Height(910f);
    }

    private static string SatelliteCaption(ClientTabletSatelliteState state) => state switch
    {
        ClientTabletSatelliteState.Searching => "Searching",
        ClientTabletSatelliteState.Synchronizing => "Synchronizing",
        ClientTabletSatelliteState.Ready => "Relay ready",
        ClientTabletSatelliteState.Off => "Transceiver off",
        _ => "Standby"
    };

    internal void SetSatellite(ClientTabletSatelliteSnapshot satellite)
    {
        if (_disposed) return;
        _settingsSatellite = satellite;
        var state = satellite.State;
        var available = state != ClientTabletSatelliteState.Unavailable;
        var active = available && satellite.Enabled;
        var caption = SatelliteCaption(state);
        _satelliteBar.text = "SATCOM · " + caption.ToUpperInvariant();
        _satelliteBar.color = state == ClientTabletSatelliteState.Ready ? ChromeMint : ChromeMuted;
        _satelliteTitle.text = caption;
        _satelliteTitle.color = active ? Mint : Muted;
        _satelliteDetail.text = state switch
        {
            ClientTabletSatelliteState.Searching => "Scanning for an orbital relay…",
            ClientTabletSatelliteState.Synchronizing => "Relay acquired. Synchronizing the field transceiver…",
            ClientTabletSatelliteState.Ready => "Field relay acquired. The transceiver remains on while the display is in standby.",
            ClientTabletSatelliteState.Off => "The field transceiver is off. Local apps remain available.",
            _ => "The field transceiver starts when you enter a world."
        };
        _satelliteToggle.Label.text = satellite.Enabled ? "Turn transceiver off" : "Turn transceiver on";
        _satelliteToggle.Button.interactable = available;
        var busy = state is ClientTabletSatelliteState.Searching or ClientTabletSatelliteState.Synchronizing;
        _satelliteReconnect.Button.interactable = active && !busy;
        _satelliteReconnect.Label.text = busy ? "Acquisition in progress" : "Reacquire relay";
        _satelliteServer.text = "Server services: " + _link.text + "\n"
            + (_page?.Connected == true ? "A server connection is reported. Message delivery still depends on its services."
                : "No server connection is reported. Reacquiring this simulated relay does not connect to a server.");
        _satelliteNext.text = state switch
        {
            ClientTabletSatelliteState.Searching => "Wait for relay search to finish. You can browse local apps while acquisition continues.",
            ClientTabletSatelliteState.Synchronizing => "Synchronization is in progress. Reacquire becomes available when acquisition finishes.",
            ClientTabletSatelliteState.Ready => "Relay acquisition is complete. Open Messages to inspect your inbox; server connection and delivery are reported separately.",
            ClientTabletSatelliteState.Off => "Turn the transceiver on to start acquisition. Your retained inbox and local navigation remain available.",
            _ => "Enter a world to start the field transceiver. Local saved content remains available in its apps."
        };
        _shadeSatellite.Label.text = "SATCOM\n" + (!available ? "Standby" : active
            ? state == ClientTabletSatelliteState.Ready ? "Ready" : "Acquiring" : "Off");
        _shadeSatellite.Image.color = active ? Mint : Raised;
        _shadeSatellite.Label.color = active ? Screen : Ink;
        _shadeSatellite.Button.interactable = available && !_desktopLocked;
        var progress = state switch
        {
            ClientTabletSatelliteState.Searching => 0,
            ClientTabletSatelliteState.Synchronizing => 1,
            ClientTabletSatelliteState.Ready => 2,
            _ => -1
        };
        for (var index = 0; index < _satelliteStages.Length; index++)
        {
            _satelliteStages[index].color = index <= progress ? Mint : Raised;
            var marker = progress < 0 ? "— " : index < progress || state == ClientTabletSatelliteState.Ready ? "Done · "
                : index == progress ? "Now · " : "Next · ";
            _satelliteStageLabels[index].text = marker + SatelliteStageNames[index];
        }
        if (_settingsTabs.Count > 2) _settingsTabs[2].Label.text = "Satellite\n" + caption;
    }
}
