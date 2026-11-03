// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Linq;
using Modstoke.Sons;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientDroneService
{
    private double? ReadSolarHour()
    {
        if (!_kits.Values.Any(kit => kit.Solar && kit.Body != null)
            || !EnvironmentReads.TryReadClock(out var clock, out _)) return null;
        return clock.EffectiveTime.TotalHours % 24;
    }

    private void UpdateSolar(Kit kit, long now, double? hour)
    {
        kit.Charging = false;
        kit.SolarStatus = "";
        if (kit.Body == null) return;
        var body = kit.Body;
        body.SetSolarCharging(false);
        if (kit.SolarObservationEpoch != _epoch)
        {
            kit.SolarObservationEpoch = _epoch;
            kit.SolarObstructionAvailable = false;
            kit.NextSolarObstruction = 0;
        }
        if ((body.Position - kit.SolarObservationPosition).sqrMagnitude > .0025f)
            kit.SolarObstructionAvailable = false;
        if (!kit.Solar)
        {
            kit.SolarObstructionAvailable = false;
            if (body.SolarMoving) kit.SolarStatus = body.SolarStatus;
            return;
        }
        if (body.Armed || !body.Grounded || body.Velocity.sqrMagnitude > .25f || body.Health <= 0)
        {
            kit.Solar = false;
            kit.SolarObstructionAvailable = false;
            body.SetSolarDeployed(false);
            kit.SolarStatus = body.SolarStatus;
            return;
        }
        body.SetSolarDeployed(true);
        if (!body.SolarReady)
        {
            kit.SolarObstructionAvailable = false;
            kit.SolarStatus = body.SolarStatus;
            return;
        }
        if (body.BatteryCharge >= 1)
        {
            kit.SolarStatus = "Battery full · solar paused";
            return;
        }
        if (!hour.HasValue)
        {
            kit.SolarStatus = "Clock unavailable · solar paused";
            return;
        }
        if (hour.Value < 7 || hour.Value > 18)
        {
            kit.SolarStatus = "Night · solar charging paused";
            return;
        }
        if (now >= kit.NextSolarObstruction)
        {
            kit.NextSolarObstruction = now + 1000;
            kit.SolarObservationPosition = body.Position;
            // A cached upward ray observes loaded obstructions, not native electricity or complete sky coverage.
            kit.SolarObstructed = Physics.Raycast(body.Position + Vector3.up * .5f, Vector3.up,
                2000, ~0, QueryTriggerInteraction.Ignore);
            kit.SolarObstructionAvailable = true;
        }
        if (!kit.SolarObstructionAvailable)
        {
            kit.SolarStatus = "Sky check pending · solar paused";
            return;
        }
        if (kit.SolarObstructed)
        {
            kit.SolarStatus = "Sky obstructed · solar paused";
            return;
        }
        kit.Charging = true;
        kit.SolarStatus = "Solar charging active";
        body.SetSolarCharging(true);
        body.BatteryCharge = Mathf.Clamp01(body.BatteryCharge + Mathf.Min(Time.deltaTime, .1f) / 600f);
        SyncKit(kit);
    }
}
