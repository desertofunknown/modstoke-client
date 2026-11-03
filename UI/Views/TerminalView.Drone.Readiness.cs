// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Globalization;
using TMPro;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientTerminalView
{
    private TextMeshProUGUI _droneCameraLabel = null!, _droneChecklistTitle = null!, _droneChecklistNote = null!;
    private RectTransform _droneChecklist = null!, _droneContactCard = null!;
    private readonly TextMeshProUGUI[] _droneCheckLabels = new TextMeshProUGUI[6];
    private readonly TextMeshProUGUI[] _droneCheckValues = new TextMeshProUGUI[6];
    private TextMeshProUGUI _droneContactAge = null!;
    private ButtonWidget _droneRecoveryPin = null!;
    private TextMeshProUGUI _droneCheckSummary = null!;
    private int _droneChecksPassed, _droneChecksBlocked, _droneChecksUnknown;

    private void BuildDroneReadiness(RectTransform camera)
    {
        _droneChecklist = RoundedPanel(camera, "Preflight", Chrome, 18f, 58f, 410f, 366f).rectTransform;
        _droneChecklistTitle = DroneText(_droneChecklist, "Title", "Deployment checks", 26f,
            ChromeInk, 16f, 12f, 378f, 36f, true);
        _droneChecklistNote = DroneText(_droneChecklist, "CheckSource", "", 18f,
            ChromeMuted, 16f, 52f, 378f, 48f);
        _droneChecklistNote.richText = false;
        for (var i = 0; i < _droneCheckLabels.Length; i++)
        {
            var y = 110f + i * 35f;
            _droneCheckLabels[i] = DroneText(_droneChecklist, "CheckLabel" + i, "", 18f,
                ChromeMuted, 16f, y, 146f, 30f);
            _droneCheckValues[i] = DroneText(_droneChecklist, "CheckValue" + i, "", 17f,
                ChromeInk, 164f, y, 230f, 30f);
            _droneCheckValues[i].richText = false;
            _droneCheckValues[i].enableWordWrapping = false;
        }
        _droneCheckSummary = DroneText(_droneChecklist, "CheckSummary", "", 17f,
            ChromeMuted, 16f, 326f, 378f, 30f);

        _droneContactCard = RoundedPanel(camera, "LastContact", Chrome, 448f, 314f, 434f, 110f).rectTransform;
        DroneText(_droneContactCard, "Label", "LAST-KNOWN CONTACT", 18f, ChromeMuted, 16f, 4f, 402f, 26f);
        _droneContactAge = DroneText(_droneContactCard, "Age", "", 21f, ChromeInk, 16f, 30f, 402f, 28f);
        _droneRecoveryPin = Button(_droneContactCard, "SaveRecoveryPin", "Save recovery pin", 16f, 64f, 220f, 36f,
            () => { if (DroneVisible && _droneState.LastContact != null) _command("drone", "navigate-recovery"); });
        _droneRecoveryPin.Label.fontSize = 18f;
        DroneText(_droneContactCard, "PinHint", "GPS / Use on watch", 17f, ChromeMuted, 246f, 69f, 174f, 28f);
    }

    private void UpdateDroneReadiness(ClientDroneView state, ClientDronePlacementView? placement,
        bool linked, bool solar, bool nearby, bool stopped)
    {
        var flying = state.Deployed && state.Armed && state.Mode != "Ready";
        var showChecks = !flying;
        var contact = state.LastContact;
        var showContact = contact != null && (!flying || !linked);
        _droneChecklist.gameObject.SetActive(showChecks);
        // Keep the entire footprint visible alongside the checks, not behind them.
        Place(_droneFeed.rectTransform, showChecks ? 448f : 0f, showChecks ? 58f : 0f,
            showChecks ? 434f : 900f, showChecks ? 244.125f : 506.25f);
        Place(_droneNoFeed.rectTransform, showChecks ? 448f : 48f, showChecks ? 110f : state.LastContact != null ? 140f : 188f,
            showChecks ? 434f : 804f, showChecks ? 156f : 112f);
        _droneNoFeed.fontSize = showChecks ? 24f : 30f;
        var instrumentY = showChecks ? 58f : showContact ? 270f : 406f;
        Place(_droneInstrumentBar, showChecks ? 448f : 0f, instrumentY,
            showChecks ? 434f : 900f, 34f);
        Place(_droneTabletInstruments.rectTransform, showChecks ? 456f : 18f, instrumentY + 4f,
            showChecks ? 418f : 864f, 28f);
        _droneTabletInstruments.fontSize = showChecks ? 15f : 17f;

        if (showChecks)
        {
            _droneChecksPassed = _droneChecksBlocked = _droneChecksUnknown = 0;
            _droneChecklistTitle.text = state.Deployed ? "Preflight checks" : "Deployment checks";
            _droneChecklistNote.text = !state.Ready ? "Drone service unavailable."
                : !state.Deployed ? placement?.Active == true ? placement.Status : "Aim at clear, level ground."
                : !linked ? "Radio unavailable. Current checks unknown."
                : state.Readiness == null ? "Ground checks unknown for this host."
                : "Current host observations. Rechecked on request.";
            var available = state.Ready && (state.InventoryDrone || state.Deployed);
            var chargeKnown = available && float.IsFinite(state.Charge);
            var healthKnown = available && float.IsFinite(state.Health);
            if (!state.Deployed)
            {
                var preview = state.Ready && placement?.Active == true;
                SetDroneCheck(0, "Drone", state.Ready ? state.InventoryDrone ? "In inventory" : "Required" : "Unknown",
                    state.Ready ? state.InventoryDrone : null);
                SetDroneCheck(1, "Surface", preview ? placement!.HasSurface ? "Found · local preview" : "No valid ground" : "Unknown",
                    preview ? placement!.HasSurface : null);
                SetDroneCheck(2, "Clearance", preview && placement!.HasSurface ? placement.Clear ? "Clear · local preview" : "Blocked" : "Unknown",
                    preview && placement!.HasSurface ? placement.Clear : null);
                SetDroneCheck(3, "Battery ≥ 15%", chargeKnown ? DronePercent(state.Charge) : "Unknown", chargeKnown ? state.Charge >= .15f : null);
                SetDroneCheck(4, "Airframe ≥ 35%", healthKnown ? DronePercent(state.Health) : "Unknown", healthKnown ? state.Health >= .35f : null);
                SetDroneCheck(5, "Flight checks", "After deployment", null, false);
            }
            else
            {
                var readiness = linked ? state.Readiness : null;
                var panelsMoving = linked && state.Mode is "Solar unfolding" or "Solar folding";
                SetDroneCheck(0, "Ground / tilt", readiness == null ? "Unknown" : !readiness.Grounded ? "Not grounded"
                    : !readiness.Upright ? "Not upright" : "Grounded · upright", readiness == null ? null : readiness.Grounded && readiness.Upright);
                SetDroneCheck(1, "Solar panels", panelsMoving ? state.Mode == "Solar unfolding" ? "Unfolding" : "Folding"
                    : readiness == null ? solar && linked ? "Unfolded" : "Unknown" : solar ? "Unfolded" : "Folded",
                    panelsMoving ? false : readiness == null ? null : !solar);
                SetDroneCheck(2, "Battery ≥ 15%", linked && chargeKnown ? DronePercent(state.Charge) : "Unknown",
                    linked && chargeKnown ? state.Charge >= .15f : null);
                SetDroneCheck(3, "Airframe ≥ 35%", linked && healthKnown ? DronePercent(state.Health) : "Unknown",
                    linked && healthKnown ? state.Health >= .35f : null);
                SetDroneCheck(4, "Controls", !linked ? "No radio link" : state.Piloting ? "Connected" : "Connect to arm", linked && state.Piloting);
                SetDroneCheck(5, state.Armed ? "Take-off range" : "Arming range",
                    !linked || !float.IsFinite(state.Range) || state.Range < 0 ? "Unknown"
                    : nearby ? "Within 4 m" : DroneNumber(Math.Max(0f, state.Range - 4f), " m closer"),
                    !linked || !float.IsFinite(state.Range) || state.Range < 0 ? null : nearby);
            }
            _droneCheckSummary.text = _droneChecksPassed.ToString(CultureInfo.InvariantCulture) + " passed · "
                + _droneChecksBlocked.ToString(CultureInfo.InvariantCulture) + " blocked · "
                + _droneChecksUnknown.ToString(CultureInfo.InvariantCulture) + " unknown";
            _droneCheckSummary.color = _droneChecksBlocked > 0 ? Amber : ChromeMuted;
        }

        _droneContactCard.gameObject.SetActive(showContact);
        if (contact != null)
            _droneContactAge.text = "Recorded " + DroneContactAge(contact.AgeSeconds) + " ago · not live";
        SetDroneButton(_droneRecoveryPin, contact != null);
        _droneRecoveryPin.Image.color = ChromeRaised;
        _droneRecoveryPin.Label.color = contact != null ? ChromeInk : ChromeMuted;
    }

    private void SetDroneCheck(int row, string label, string value, bool? passed, bool applicable = true)
    {
        _droneCheckLabels[row].text = label;
        if (!applicable)
        {
            _droneCheckValues[row].text = "Later · " + value;
            _droneCheckValues[row].color = ChromeMuted;
            return;
        }
        if (passed == true) _droneChecksPassed++;
        else if (passed == false) _droneChecksBlocked++;
        else _droneChecksUnknown++;
        _droneCheckValues[row].text = (passed == true ? "OK · " : passed == false ? "! · " : "? · ") + value;
        _droneCheckValues[row].color = passed == true ? ChromeMint : passed == false ? Amber : ChromeMuted;
    }

    private static string DroneContactAge(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) return "an unknown time";
        if (seconds < 60) return Math.Floor(seconds).ToString("0", CultureInfo.InvariantCulture) + " s";
        if (seconds < 3600) return Math.Floor(seconds / 60).ToString("0", CultureInfo.InvariantCulture) + " min";
        return Math.Floor(seconds / 3600).ToString("0", CultureInfo.InvariantCulture) + " h";
    }
}
