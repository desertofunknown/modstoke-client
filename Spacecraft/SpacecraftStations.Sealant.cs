// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Globalization;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftStations
{
    private static readonly PanelSurface[] SealantSurfaces =
    {
        new(new(3.266648f, .22f, -2.43f), Vector3.left, 250, 220),
        new(new(.908333f, .43f, -7.35f), Vector3.left, 250, 220),
        new(new(1.896176f, -.50f, -12.20f), Vector3.left, 250, 220)
    };
    private static readonly string[] SealantCommands = { "seal-cabin", "seal-airlock", "seal-entry" };
    private static readonly string[] SealantLabels = { "CABIN", "AIRLOCK", "ENTRY" };
    private static readonly Vector3 SealantButtonRest = new(.065f, -.035f, -.010f);
    private readonly SealantPanel[] _sealantPanels = new SealantPanel[3];

    private static SpacecraftControlContact SealantContact(SpacecraftCompartment compartment)
    {
        var surface = SealantSurfaces[(int)compartment];
        return KeyControlContact(surface.Position + surface.Rotation * (SealantButtonRest + Vector3.back * .0007f),
            surface.Rotation);
    }

    private void BuildSealantPanels()
    {
        var shell = Lit("Sealant housing", new(.46f, .49f, .47f, 1));
        shell.SetFloat("_Metallic", .15f); shell.SetFloat("_Smoothness", .42f);
        _panelFinish.Apply(shell, ClientDeviceSurfaceFinish.Surface.Polymer);
        var buttonMaterial = Lit("Sealant actuator", new(.18f, .24f, .22f, 1));
        buttonMaterial.SetFloat("_Metallic", 0); buttonMaterial.SetFloat("_Smoothness", .32f);
        _panelFinish.Apply(buttonMaterial, ClientDeviceSurfaceFinish.Surface.Rubber);
        var cartridge = Lit("Sealant cartridge", new(.64f, .22f, .035f, 1));
        cartridge.SetFloat("_Metallic", .05f); cartridge.SetFloat("_Smoothness", .38f);
        _panelFinish.Apply(cartridge, ClientDeviceSurfaceFinish.Surface.Polymer);
        var display = Lit("Sealant display cover", new(.012f, .022f, .025f, 1));
        display.SetFloat("_Metallic", .08f); display.SetFloat("_Smoothness", .64f);
        _panelFinish.Apply(display, ClientDeviceSurfaceFinish.Surface.Polymer);
        for (int index = 0; index < _sealantPanels.Length; index++)
        {
            var panel = AddPanel(SealantLabels[index] + " sealant", SealantSurfaces[index], default, 9);
            panel.Canvas.localPosition = new(0, 0, .0007f);
            Label(panel.Canvas, "Sealant title", "SEALANT", 116, 27, 104, 11, 8, Ink);
            Label(panel.Canvas, "Compartment", SealantLabels[index], 116, 40, 104, 10, 6, Ink);
            SetPanelPrompt(panel, new Rect(116, 55, 104, 18), 9);
            AddPanelModelPart(panel.Frame, "sealant-shell", shell);
            AddPanelModelPart(panel.Frame, "sealant-trim", _panelMetal);
            AddPanelModelPart(panel.Frame, "sealant-rubber", _panelRubber);
            AddPanelModelPart(panel.Frame, "sealant-cartridge", cartridge);
            AddPanelModelPart(panel.Frame, "sealant-glass", display);
            var button = AddPanelModelPart(panel.Frame, "sealant-button", buttonMaterial).transform;
            button.localPosition = SealantButtonRest;
            var key = AddPanelButton(panel, SealantCommands[index], "SEAL", new Rect(174, 129, 32, 32), 5, paint: false);
            key.Label.gameObject.SetActive(false);
            key.Contact = new PanelContact(button,
                new Bounds(new(0, 0, .00165f), new(.032f, .032f, .0047f)),
                new Bounds(new(0, 0, -.00035f), new(.030f, .030f, .0007f)));
            _sealantPanels[index] = new SealantPanel(panel, key, button);
            PositionSealantFocus(_sealantPanels[index]);
        }
    }

    private static bool SealantPowered(ClientSpacecraftView view) => view.Flight.Travel == SpacecraftTravel.None
        && view.Flight.Systems.LifeSupportIntegrity01 > .1f
        && SpacecraftElectrical.IsPowered(view.Flight.Systems.Electrical, SpacecraftCircuit.LifeSupport);

    private static bool CanStartSealant(SpacecraftCompartment compartment, ClientSpacecraftView view)
    {
        var systems = view.Flight.Systems;
        return !view.Boarding && (view.CabinWalking || view.Seat < 0) && SealantPowered(view)
            && systems.LineSpares > 0 && systems.Sealant.Leak(compartment) > 0 && systems.Sealant.Remaining(compartment) == 0;
    }

    private static string SealantBlockedReason(SpacecraftCompartment compartment, ClientSpacecraftView view)
    {
        var systems = view.Flight.Systems;
        if (view.Boarding || !view.CabinWalking && view.Seat >= 0) return "Leave the seat to operate the sealant unit";
        if (systems.Sealant.Remaining(compartment) > 0) return "Cartridge loaded / cycle in progress";
        if (systems.Sealant.Leak(compartment) <= 0) return "No pressure leak detected";
        if (systems.LifeSupportIntegrity01 <= .1f) return "Repair life support to restore the sealant pump";
        if (!SealantPowered(view)) return "Restore power to the life support circuit";
        return "Sealant requires one line spare aboard";
    }

    private void UpdateSealantButtons(ClientSpacecraftView view, ReadOnlySpan<SpacecraftCrewInteraction> interactions)
    {
        bool ready = view.Ready && view.Available && view.Flight.Systems.Ready;
        for (int index = 0; index < _sealantPanels.Length; index++)
        {
            var unit = _sealantPanels[index];
            var compartment = (SpacecraftCompartment)index;
            if (!ready) unit.PressedUntil = 0;
            float press = PanelKeyPress(ready, unit.PressedUntil, SealantContact(compartment), interactions);
            unit.Button.localPosition = SealantButtonRest + Vector3.forward * (.002f * press);
            PositionSealantFocus(unit);
        }
    }

    private void UpdateSealantReadouts(ClientSpacecraftView view)
    {
        bool ready = view.Ready && view.Available && view.Flight.Systems.Ready;
        bool powered = ready && SealantPowered(view);
        for (int index = 0; index < _sealantPanels.Length; index++)
        {
            var unit = _sealantPanels[index];
            var compartment = (SpacecraftCompartment)index;
            var sealant = view.Flight.Systems.Sealant;
            float remaining = sealant.Remaining(compartment);
            if (!ready) { unit.Status = "SYNC"; unit.Color = Muted; }
            else if (remaining > 0)
            {
                int percent = Mathf.Clamp(Mathf.FloorToInt((1 - remaining / ClientSpacecraftSystems.SealantCycleSeconds) * 100), 0, 99);
                unit.Status = (powered ? "SEALING " : "PAUSED ") + percent.ToString(CultureInfo.InvariantCulture) + "%";
                unit.Color = powered ? PanelCyan : Amber;
            }
            else if (sealant.Leak(compartment) <= 0) { unit.Status = "NO LEAK"; unit.Color = PanelCyan; }
            else { unit.Status = view.Flight.Systems.LineSpares > 0 ? "LEAK" : "NO KITS"; unit.Color = Amber; }
        }
    }

    private void UpdateSealantPrompts(ClientSpacecraftView view)
    {
        for (int index = 0; index < _sealantPanels.Length; index++)
        {
            var unit = _sealantPanels[index];
            bool running = view.Flight.Systems.Sealant.Remaining((SpacecraftCompartment)index) > 0;
            string feedback = running ? "" : PanelPrompt(unit.Station, view);
            bool transient = feedback.Length != 0;
            unit.Station.Prompt.text = transient ? feedback : unit.Status;
            unit.Station.Prompt.fontSize = transient ? 4.5f : 9;
            unit.Station.Prompt.color = transient ? Ink : unit.Color;
        }
    }

    private bool IsSealantPanel(Station station)
    {
        foreach (var unit in _sealantPanels)
            if (unit.Station == station) return true;
        return false;
    }

    private void ResetSealantPresses()
    {
        foreach (var unit in _sealantPanels) unit.PressedUntil = 0;
    }

    private static void PositionSealantFocus(SealantPanel unit)
    {
        var position = unit.Key.Key.localPosition;
        position.z = (unit.Button.localPosition.z - .0009f - unit.Station.Canvas.localPosition.z) * 1000;
        unit.Key.Key.localPosition = position;
    }

    private sealed record SealantPanel(Station Station, StationButton Key, Transform Button)
    {
        internal float PressedUntil;
        internal string Status = "SYNC";
        internal Color Color;
    }
}
