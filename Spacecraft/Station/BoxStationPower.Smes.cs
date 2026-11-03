// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Modstoke.Client;

internal enum BoxStationSmesControl : byte
{
    InputToggle,
    InputDown,
    InputUp,
    InputMin,
    InputMax,
    OutputToggle,
    OutputDown,
    OutputUp,
    OutputMin,
    OutputMax
}

internal readonly record struct BoxStationSmes(int Object, int Cell, int Direction,
    int InputNetwork, int OutputNetwork, double CapacityJoules, double InputMaximumWatts, double OutputMaximumWatts);

internal readonly record struct BoxStationSmesState(float ChargeKwh, float InputLimitKw, float OutputLimitKw,
    float InputKw, float OutputKw, byte Enabled);

internal sealed partial class BoxStationPower
{
    private const double SmesAdjustmentWatts = 10_000;
    private readonly double[] _smesInputLimits, _smesOutputLimits, _smesInputJoules, _smesOutputJoules;
    private readonly double[] _smesInputWatts, _smesOutputWatts;
    private readonly byte[] _smesEnabled;

    internal ImmutableArray<BoxStationSmes> Smes => _connectivity.Smes;

    internal ImmutableArray<BoxStationSmesState> SmesSnapshot
    {
        get
        {
            var states = ImmutableArray.CreateBuilder<BoxStationSmesState>(Smes.Length);
            for (int i = 0; i < Smes.Length; i++)
                states.Add(new((float)(_smesJoules[i] / JoulesPerKwh),
                    (float)(_smesInputLimits[i] / 1000), (float)(_smesOutputLimits[i] / 1000),
                    (float)(_smesInputWatts[i] / 1000), (float)(_smesOutputWatts[i] / 1000), _smesEnabled[i]));
            return states.MoveToImmutable();
        }
    }

    internal bool ControlSmes(int index, BoxStationSmesControl control)
    {
        if (_stepping || index < 0 || index >= Smes.Length || !ValidSmesControl(control)) return false;
        bool input = control <= BoxStationSmesControl.InputMax;
        byte channel = input ? (byte)1 : (byte)2;
        if (control is BoxStationSmesControl.InputToggle or BoxStationSmesControl.OutputToggle)
            _smesEnabled[index] ^= channel;
        else
        {
            double current = input ? _smesInputLimits[index] : _smesOutputLimits[index];
            double maximum = input ? Smes[index].InputMaximumWatts : Smes[index].OutputMaximumWatts;
            double value = control switch
            {
                BoxStationSmesControl.InputDown or BoxStationSmesControl.OutputDown => current - SmesAdjustmentWatts,
                BoxStationSmesControl.InputUp or BoxStationSmesControl.OutputUp => current + SmesAdjustmentWatts,
                BoxStationSmesControl.InputMin or BoxStationSmesControl.OutputMin => 0,
                _ => maximum
            };
            value = Math.Clamp(value, 0, maximum);
            if (value == current) return false;
            if (input) _smesInputLimits[index] = value;
            else _smesOutputLimits[index] = value;
        }
        // A changed channel has no completed work at its new setting yet.
        if (input) _smesInputWatts[index] = 0;
        else _smesOutputWatts[index] = 0;
        RefreshAvailability();
        return true;
    }

    internal static bool ValidSmesControl(BoxStationSmesControl control)
        => control is BoxStationSmesControl.InputToggle or BoxStationSmesControl.InputDown
            or BoxStationSmesControl.InputUp or BoxStationSmesControl.InputMin or BoxStationSmesControl.InputMax
            or BoxStationSmesControl.OutputToggle or BoxStationSmesControl.OutputDown
            or BoxStationSmesControl.OutputUp or BoxStationSmesControl.OutputMin or BoxStationSmesControl.OutputMax;

    private static bool ValidSmesReadings(BoxStationLayout layout, ImmutableArray<BoxStationSmesState> states)
    {
        var smes = GetSmes(layout);
        if (states.IsDefault || states.Length != smes.Length) return false;
        for (int i = 0; i < smes.Length; i++)
        {
            var state = states[i];
            var device = smes[i];
            // Compare in the wire representation so a full double store rounded
            // up to the nearest float remains a valid capacity readout.
            if (!ValidSmesReading(state.ChargeKwh, (float)(device.CapacityJoules / JoulesPerKwh))
                || !ValidSmesReading(state.InputLimitKw, (float)(device.InputMaximumWatts / 1000))
                || !ValidSmesReading(state.OutputLimitKw, (float)(device.OutputMaximumWatts / 1000))
                || !ValidSmesReading(state.InputKw, state.InputLimitKw)
                || !ValidSmesReading(state.OutputKw, state.OutputLimitKw)
                || (state.Enabled & ~3) != 0
                || (state.Enabled & 1) == 0 && state.InputKw != 0
                || (state.Enabled & 2) == 0 && state.OutputKw != 0) return false;
        }
        return true;
    }

    private static bool ValidSmesReading(float value, float maximum)
        => float.IsFinite(value) && value >= 0 && value <= maximum;

    internal static bool ValidSmesState(BoxStationLayout layout, ImmutableArray<BoxStationSmesState> states, BoxStationCableState cables)
    {
        if (!ValidCableState(layout, cables) || !ValidSmesReadings(layout, states)) return false;
        for (int i = 0; i < states.Length; i++)
        {
            SmesConnections(layout, cables, i, out bool input, out bool output);
            if (!input && states[i].InputKw != 0 || !output && states[i].OutputKw != 0) return false;
        }
        return true;
    }

    internal static void SmesConnections(BoxStationLayout layout, BoxStationCableState cables, int index, out bool input, out bool output)
    {
        input = output = false;
        var definitions = LayoutDefinitions.GetValue(layout, BuildDefinitions);
        if (index < 0 || index >= definitions.Smes.Length || cables.LayoutHash != layout.LayoutHash
            || cables.Installed.IsDefault || cables.Installed.Length != (definitions.CableSites.Length + 7) / 8) return;
        int terminal = definitions.SmesTerminals[index];
        input = terminal >= 0 && CablePresentAtCell(cables.Installed, definitions.CableIndices, definitions.CableNext, terminal);
        output = terminal >= 0 && CablePresentAtCell(cables.Installed, definitions.CableIndices, definitions.CableNext, definitions.Smes[index].Cell);
    }

    private static bool ValidSmesSetting(BoxStationSmesSetting? setting, BoxStationSmes device)
        => setting is not null && ValidCharge(setting.InputWatts, device.InputMaximumWatts)
            && ValidCharge(setting.OutputWatts, device.OutputMaximumWatts) && (setting.Enabled & ~3) == 0;
}

internal sealed class BoxStationSmesSetting
{
    [JsonRequired]
    public double InputWatts { get; set; }
    [JsonRequired]
    public double OutputWatts { get; set; }
    [JsonRequired]
    public byte Enabled { get; set; }
}
