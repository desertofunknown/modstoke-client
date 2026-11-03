// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftStations
{
    private static readonly PanelSurface[] HullServiceSurfaces =
    {
        new(new(-3.4968588f, .115f, -2.75f), Vector3.right, 240, 100),
        new(new(-1.074094f, .43f, -7.35f), Vector3.right, 240, 100),
        new(new(-2.003025f, -.50f, -12.20f), Vector3.right, 240, 100)
    };
    private static readonly string[] HullServiceCommands = { "service-hull", "service-hull-airlock", "service-hull-entry" };
    private readonly Station[] _hullServicePanels = new Station[3];
    private readonly GameObject[] _hullWelds = new GameObject[3];

    private static SpacecraftControlContact HullServiceContact(SpacecraftCompartment compartment)
    {
        var surface = HullServiceSurfaces[(int)compartment];
        return KeyControlContact(surface.Position + surface.Rotation * new Vector3(0, 0, -.010f), surface.Rotation);
    }

    private void BuildHullService()
    {
        var shell = Lit("Structural backing", new(.15f, .18f, .20f, 1));
        shell.SetFloat("_Metallic", .75f); shell.SetFloat("_Smoothness", .40f);
        var plate = Lit("Hull reinforcement plate", new(.40f, .43f, .45f, 1));
        plate.SetFloat("_Metallic", .90f); plate.SetFloat("_Smoothness", .53f);
        var weld = Lit("Hull weld bead", new(.32f, .29f, .24f, 1));
        weld.SetFloat("_Metallic", .85f); weld.SetFloat("_Smoothness", .48f);
        _panelFinish.Apply(shell, ClientDeviceSurfaceFinish.Surface.Metal);
        _panelFinish.Apply(plate, ClientDeviceSurfaceFinish.Surface.Metal);
        _panelFinish.Apply(weld, ClientDeviceSurfaceFinish.Surface.Metal);
        for (int index = 0; index < _hullServicePanels.Length; index++)
        {
            var panel = _hullServicePanels[index] = AddPanel("Hull reinforcement", HullServiceSurfaces[index], default);
            panel.Canvas.localPosition = new(0, 0, -.0115f);
            AddPanelModelPart(panel.Frame, "hull-weld-shell", shell);
            AddPanelModelPart(panel.Frame, "hull-weld-plate", plate);
            _hullWelds[index] = AddPanelModelPart(panel.Frame, "hull-weld-weld", weld).gameObject;
            var key = AddPanelButton(panel, HullServiceCommands[index], "WELD", new Rect(100, 30, 40, 40), 5, paint: false);
            key.Label.gameObject.SetActive(false);
            key.Contact = new PanelContact(panel.Frame,
                new Bounds(new(0, 0, -.010f), new(.040f, .040f, .008f)),
                new Bounds(new(0, 0, -.010f), new(.040f, .040f, .0008f)));
            SetPanelPrompt(panel, new Rect(8, 104, 224, 24), 5);
        }
    }

    private void UpdateHullService(ClientSpacecraftView view)
    {
        var systems = view.Flight.Systems;
        bool ready = view.Ready && view.Available && systems.Ready;
        for (int index = 0; index < _hullWelds.Length; index++)
        {
            var compartment = (SpacecraftCompartment)index;
            bool healthy = ready && systems.Hull.Integrity(compartment) >= 1 && systems.Sealant.Leak(compartment) <= 0;
            if (_hullWelds[index].activeSelf != healthy) _hullWelds[index].SetActive(healthy);
        }
    }
}
