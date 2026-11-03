// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Client;

internal sealed partial class ClientTabletGames
{
    private enum PlatformKind { Ground, Ledge, Moving, Crumble, Spring, Lift, RuneBridge }
    private readonly record struct Platform(float X, float Y, float Width, float Height = 14,
        PlatformKind Kind = PlatformKind.Ledge, float Travel = 0, float Rate = 1, float Phase = 0);
    private readonly record struct IslandHazard(float X, float Y, float Width, bool Vent = false, float Phase = 0);
    private readonly record struct Flag(float X, float Y);
    private sealed record IslandStage(string Name, float Length, Platform[] Platforms, Pickup[] Pickups,
        EnemySeed[] Enemies, IslandHazard[] Hazards, Flag[] Flags);

    private const float TideCourierLimit = 22;
    private float _tideNoticeTime, _tideCourierTime;
    private string _tideNotice = "";
    private bool _tideRescueReady, _tideRescueEarned, _tideManifestClaimed;
    private int _tideAirStomps, _tideCourierState;

    private void ResetIslandStage(Run run)
    {
        _tideNoticeTime = _tideCourierTime = 0;
        _tideNotice = "";
        _tideRescueReady = _tideRescueEarned = _tideManifestClaimed = false;
        _tideAirStomps = _tideCourierState = 0;
        ResetTideAdventure(run);
    }

    private void TideNotice(string message)
    { _tideNotice = message; _tideNoticeTime = 3.2f; }

    private static readonly string[] SupplyObjectives =
    {
        "Collect 6 supplies, then reach the striped beacon. 0/6",
        "Collect 6 supplies, then reach the striped beacon. 1/6",
        "Collect 6 supplies, then reach the striped beacon. 2/6",
        "Collect 6 supplies, then reach the striped beacon. 3/6",
        "Collect 6 supplies, then reach the striped beacon. 4/6",
        "Collect 6 supplies, then reach the striped beacon. 5/6",
        "Beacon unlocked! Reach the striped flag at the far right."
    };

    // Every gap and optional route is authored. The base route fits the double jump without a dash;
    // elevated caches reward chaining the extra jump, springs and moving platforms.
    private static readonly IslandStage[] IslandStages =
    {
        new("01 / SUNWASH SHORE", 2360,
            new Platform[]
            {
                new(0,330,360,60,PlatformKind.Ground), new(450,330,420,60,PlatformKind.Ground),
                new(960,330,370,60,PlatformKind.Ground), new(1440,330,370,60,PlatformKind.Ground),
                new(1910,330,450,60,PlatformKind.Ground),
                new(210,260,88), new(330,200,82), new(470,146,108),
                new(730,255,90), new(850,221,90,14,PlatformKind.Moving,32,1.2f),
                new(1080,258,88), new(1180,195,88), new(1320,142,100),
                new(1540,263,88), new(1660,211,90,14,PlatformKind.Crumble),
                new(1810,170,105,14,PlatformKind.Moving,28,.9f),
                new(2020,254,88), new(2140,194,100), new(2250,254,68),
                new(1850,236,78,14,PlatformKind.Lift,66,.85f),
                new(805,258,84,14,PlatformKind.RuneBridge), new(905,202,84,14,PlatformKind.RuneBridge)
            },
            new Pickup[]
            {
                new(PickupKind.Supply,120,304), new(PickupKind.Supply,248,237),
                new(PickupKind.Supply,365,177), new(PickupKind.Secret,520,123),
                new(PickupKind.Supply,490,304), new(PickupKind.Supply,710,304),
                new(PickupKind.Supply,770,232), new(PickupKind.Heal,825,304),
                new(PickupKind.Supply,1010,304), new(PickupKind.Supply,1120,235),
                new(PickupKind.Supply,1218,172), new(PickupKind.Secret,1365,119),
                new(PickupKind.Supply,1490,304), new(PickupKind.Supply,1580,240),
                new(PickupKind.Shield,1695,188), new(PickupKind.Supply,1970,304),
                new(PickupKind.Supply,2060,231), new(PickupKind.Secret,2180,171),
                new(PickupKind.Heal,2270,304)
            },
            new EnemySeed[]
            {
                new(EnemyKind.Crab,570,316,65), new(EnemyKind.Crab,790,316,42),
                new(EnemyKind.Hopper,1140,316,52), new(EnemyKind.Bird,1550,217,100),
                new(EnemyKind.Crab,1640,316,65), new(EnemyKind.Cannon,2100,316,0,2)
            },
            new IslandHazard[] { new(650,312,36), new(1245,312,38), new(1748,312,38,true,1.4f) },
            new Flag[] { new(1000,330), new(1950,330) }),
        new("02 / SMUGGLER CAUSEWAY", 2480,
            new Platform[]
            {
                new(0,330,280,60,PlatformKind.Ground), new(380,330,400,60,PlatformKind.Ground),
                new(890,330,300,60,PlatformKind.Ground), new(1310,330,410,60,PlatformKind.Ground),
                new(1850,330,630,60,PlatformKind.Ground),
                new(190,262,76), new(310,232,86,14,PlatformKind.Moving,26,1.4f),
                new(475,261,82), new(570,197,80), new(675,138,96),
                new(790,258,90,14,PlatformKind.Moving,32,1.15f),
                new(940,252,88,14,PlatformKind.Crumble), new(1060,194,80,14,PlatformKind.Crumble),
                new(1200,235,94,14,PlatformKind.Moving,24,1.1f),
                new(1330,263,80), new(1420,198,84), new(1535,137,95),
                new(1660,247,76,14,PlatformKind.Crumble), new(1750,269,88,14,PlatformKind.Moving,28,1.5f),
                new(1930,260,90), new(2050,200,82), new(2160,143,100), new(2330,255,80),
                new(1220,244,78,14,PlatformKind.Lift,60,1),
                new(780,262,82,14,PlatformKind.RuneBridge), new(875,207,82,14,PlatformKind.RuneBridge)
            },
            new Pickup[]
            {
                new(PickupKind.Supply,125,304), new(PickupKind.Supply,225,239),
                new(PickupKind.Supply,420,304), new(PickupKind.Supply,510,238),
                new(PickupKind.Supply,610,174), new(PickupKind.Secret,720,115),
                new(PickupKind.Heal,735,304), new(PickupKind.Supply,930,304),
                new(PickupKind.Shield,1094,171), new(PickupKind.Supply,1150,304),
                new(PickupKind.Supply,1368,240), new(PickupKind.Supply,1462,175),
                new(PickupKind.Secret,1580,114), new(PickupKind.Supply,1640,304),
                new(PickupKind.Supply,1890,304), new(PickupKind.Supply,1975,237),
                new(PickupKind.Supply,2090,177), new(PickupKind.Secret,2205,120),
                new(PickupKind.Heal,2280,304), new(PickupKind.Supply,2365,232)
            },
            new EnemySeed[]
            {
                new(EnemyKind.Hopper,515,316,64), new(EnemyKind.Bird,770,193,95),
                new(EnemyKind.Cannon,1000,316,0,2), new(EnemyKind.Crab,1450,316,65),
                new(EnemyKind.Bird,1720,184,110), new(EnemyKind.Hopper,1980,316,65),
                new(EnemyKind.Cannon,2335,316,0,2)
            },
            new IslandHazard[] { new(630,312,42,true,.8f), new(1100,312,38), new(1550,312,45,true,2), new(2130,312,44) },
            new Flag[] { new(915,330), new(1875,330) }),
        new("03 / STORMGLASS LIGHTHOUSE", 2700,
            new Platform[]
            {
                new(0,330,320,60,PlatformKind.Ground), new(430,330,380,60,PlatformKind.Ground),
                new(920,330,290,60,PlatformKind.Ground), new(1320,330,300,60,PlatformKind.Ground),
                new(1730,330,360,60,PlatformKind.Ground), new(2200,330,500,60,PlatformKind.Ground),
                new(225,316,34,14,PlatformKind.Spring), new(310,215,90,14,PlatformKind.Moving,28,1.3f),
                new(470,154,100), new(635,217,88,14,PlatformKind.Crumble),
                new(785,255,90,14,PlatformKind.Moving,24,1.4f),
                new(970,256,80), new(1065,194,84,14,PlatformKind.Crumble), new(1180,135,98),
                new(1360,316,34,14,PlatformKind.Spring), new(1440,205,85),
                new(1550,150,86,14,PlatformKind.Crumble), new(1655,244,90,14,PlatformKind.Moving,26,1.2f),
                new(1780,261,84), new(1900,203,84), new(2020,145,96),
                new(2120,252,90,14,PlatformKind.Moving,24,1.6f),
                new(2300,262,80), new(2420,207,80), new(2525,148,94),
                new(1650,244,84,14,PlatformKind.Lift,60,.95f),
                new(817,263,84,14,PlatformKind.RuneBridge), new(920,204,84,14,PlatformKind.RuneBridge)
            },
            new Pickup[]
            {
                new(PickupKind.Supply,140,304), new(PickupKind.Supply,350,192),
                new(PickupKind.Secret,520,131), new(PickupKind.Supply,470,304),
                new(PickupKind.Supply,675,194), new(PickupKind.Heal,755,304),
                new(PickupKind.Supply,950,304), new(PickupKind.Supply,1010,233),
                new(PickupKind.Supply,1105,171), new(PickupKind.Secret,1225,112),
                new(PickupKind.Supply,1480,182), new(PickupKind.Shield,1590,127),
                new(PickupKind.Supply,1760,304), new(PickupKind.Supply,1820,238),
                new(PickupKind.Supply,1940,180), new(PickupKind.Secret,2065,122),
                new(PickupKind.Heal,2240,304), new(PickupKind.Supply,2340,239),
                new(PickupKind.Supply,2460,184), new(PickupKind.Supply,2570,125)
            },
            new EnemySeed[]
            {
                new(EnemyKind.Crab,590,316,70), new(EnemyKind.Bird,670,165,95),
                new(EnemyKind.Cannon,1040,316,0,2), new(EnemyKind.Bird,1250,208,100),
                new(EnemyKind.Hopper,1490,316,70), new(EnemyKind.Cannon,1900,316,0,2),
                new(EnemyKind.Bird,2120,185,105), new(EnemyKind.Hopper,2420,316,65),
                new(EnemyKind.Cannon,2590,316,0,6)
            },
            new IslandHazard[] { new(690,312,43,true,.2f), new(1130,312,43), new(1550,312,38,true,1.4f), new(2010,312,42), new(2510,312,42,true,2.3f) },
            new Flag[] { new(945,330), new(2240,330) })
    };

    private string IslandObjective(Run run)
        => run.Finished ? run.Won ? TideFinishSummary()
            : run.Health <= 0 ? "No lives left. Restart to try the islands again." : "Rescue window expired. Restart to try again."
            : run.Started && _tideNoticeTime > 0 ? _tideNotice
            : run.Started && TideGuardianAlive(run) && run.X > 2300
                ? "Guardian guards the beacon. Jump its volley; return shots or dive during recovery."
            : run.Started && _tideRingProgress > 0 && !_tideRingsWon
                ? "Sky trail: collect the bright ring next before its timer empties."
            : run.Started && run.StageCollected < 6 && run.X >= IslandStages[run.Stage].Length - 170
                ? "Beacon locked. Follow the supply marker; collect six to leave."
            : run.Started && _tideCourierState == 1 ? "Courier: next flag before the bar empties. Avoid damage and water!"
            : run.Started && run.StageCollected >= 6 && TideGuardianAlive(run)
                ? "Supply quota met. Defeat the lighthouse guardian to unlock the final rescue beacon."
            : run.Started && run.StageCollected >= 6 && !_tideManifestClaimed
                ? "Beacon open. Fill the supply bar for +1200; secrets grant a rescue."
            : run.Started && TideAdventureHint(run) is { } hint ? hint
            : SupplyObjectives[run.Started ? Math.Min(6, run.StageCollected) : 0];

    private static float PlatformX(Platform platform, float time)
        => platform.X + (platform.Kind == PlatformKind.Moving ? MathF.Sin(time * platform.Rate + platform.Phase) * platform.Travel : 0);
    private static float PlatformY(Platform platform, float time)
        => platform.Y + (platform.Kind == PlatformKind.Lift ? MathF.Sin(time * platform.Rate + platform.Phase) * platform.Travel : 0);

    private bool IslandPlatformAvailable(Run run, int index)
    {
        var platform = IslandStages[run.Stage].Platforms[index];
        return (platform.Kind != PlatformKind.Crumble || run.Platforms[index] < .65f)
            && (platform.Kind != PlatformKind.RuneBridge || _tideRuneTime > 0);
    }
    private static bool VentActive(IslandHazard hazard, float time) => !hazard.Vent || (time + hazard.Phase) % 3.6f >= 1.8f;

    private static float VentWarning(IslandHazard hazard, float time)
        => hazard.Vent ? Math.Clamp(((time + hazard.Phase) % 3.6f - 1.2f) / .6f, 0, 1) : 0;

    private void FailTideCourier()
    {
        if (_tideCourierState != 1) return;
        _tideCourierState = 3;
        TideNotice("Courier missed. Your checkpoint and the island route remain open.");
    }

    private void HurtIsland(Run run)
    {
        _tideStageHits++;
        _tideDiving = false;
        _tideAirStomps = 0;
        FailTideCourier();
        Hurt(run);
    }

    private static void ClearIslandCheckpointShots(Run run)
    {
        for (var i = 0; i < run.Shots.Length; i++)
            if (run.Shots[i].Active && run.Shots[i].Hostile && Math.Abs(run.Shots[i].X - run.CheckpointX) < 180)
                run.Shots[i].Active = false;
    }

    private void RallyIslandCheckpoint(Run run, int index)
    {
        run.Health = Math.Min(3, run.Health + 1);
        run.DashCooldown = 0;
        run.AirDash = false;
        run.Hurt = Math.Max(run.Hurt, .8f);
        ClearIslandCheckpointShots(run);
        if (index == 0)
        {
            _tideCourierState = 1;
            _tideCourierTime = TideCourierLimit;
            TideNotice("Rally: +1 heart, dash ready. Courier: next flag in 22s without hits.");
        }
        else if (_tideCourierState == 1)
        {
            _tideCourierState = 2;
            AddScore(run, 900);
            run.Shield = Math.Max(run.Shield, 6);
            TideNotice("Courier delivered! +900, shield, checkpoint heart and dash ready.");
        }
        else TideNotice("Rally: +1 heart, dash ready, nearby cannonballs cleared.");
    }

    private void RespawnIsland(Run run)
    {
        run.X = run.CheckpointX;
        run.Y = run.CheckpointY;
        run.VX = run.VY = run.DashTime = run.DashCooldown = run.Coyote = run.JumpBuffer = 0;
        run.Grounded = true;
        run.Jumps = 0;
        run.AirDash = false;
        run.Support = -1;
        run.Hurt = Math.Max(run.Hurt, 1.5f);
        _tideAirStomps = 0;
        _tideDiving = false;
        ResetTideRingAttempt();
        ClearIslandCheckpointShots(run);
    }

    private void RewardIslandPickup(Run run, Pickup pickup)
    {
        if (pickup.Kind == PickupKind.Secret && !_tideRescueEarned)
        {
            _tideRescueReady = _tideRescueEarned = true;
            TideNotice("Secret found! One free water rescue is ready for this island.");
        }
        if (pickup.Kind != PickupKind.Supply || _tideManifestClaimed) return;
        var total = IslandSupplyCount(IslandStages[run.Stage]);
        if (run.StageCollected < total) return;
        _tideManifestClaimed = true;
        AddScore(run, 1200);
        run.Shield = Math.Max(run.Shield, 8);
        TideNotice("All supplies found! +1200 and an 8-second shield.");
    }

    private static int IslandSupplyCount(IslandStage stage)
    {
        var total = 0;
        foreach (var pickup in stage.Pickups)
            if (pickup.Kind == PickupKind.Supply) total++;
        return total;
    }

    private void UpdateIsland(Run run)
    {
        var stage = IslandStages[run.Stage];
        _tideNoticeTime = Math.Max(0, _tideNoticeTime - Step);
        UpdateTideAdventureTimers();
        if (_tideCourierState == 1)
        {
            _tideCourierTime = Math.Max(0, _tideCourierTime - Step);
            if (_tideCourierTime <= 0) FailTideCourier();
        }
        var dx = MoveX(run);
        if (dx != 0) run.Facing = dx;
        run.Coyote = run.Grounded ? .11f : Math.Max(0, run.Coyote - Step);
        if (run.Jump) run.JumpBuffer = .13f;
        else run.JumpBuffer = Math.Max(0, run.JumpBuffer - Step);
        run.Jump = false;

        for (var i = 0; i < stage.Platforms.Length; i++)
        {
            if (run.Platforms[i] <= 0) continue;
            run.Platforms[i] += Step;
            if (run.Platforms[i] > 3.5f) run.Platforms[i] = 0;
        }
        var carriedSupport = run.Grounded && run.Support >= 0 && IslandPlatformAvailable(run, run.Support)
            ? run.Support : -1;
        if (carriedSupport >= 0)
        {
            var support = stage.Platforms[carriedSupport];
            run.X += PlatformX(support, run.StageTime) - PlatformX(support, run.StageTime - Step);
            run.Y += PlatformY(support, run.StageTime) - PlatformY(support, run.StageTime - Step);
        }
        if (run.JumpBuffer > 0 && (run.Coyote > 0 || run.Jumps < 2))
        {
            if (run.Coyote > 0) run.Jumps = 0;
            run.Jumps++;
            run.VY = run.Jumps == 1 ? -420 : -390;
            run.Grounded = false;
            run.Coyote = run.JumpBuffer = 0;
            run.Support = -1;
            _tideDiving = false;
            Burst(run, run.X + 10, run.Y + 28, ClientGameInk.Cloud, 4);
        }
        if (run.DashRequest && run.DashCooldown <= 0 && (!run.AirDash || run.Grounded))
        {
            run.DashTime = .17f;
            run.DashCooldown = 1.2f;
            run.DashX = run.Facing;
            run.AirDash = true;
            _tideDiving = false;
            Burst(run, run.X + 10, run.Y + 14, ClientGameInk.Dash, 6);
        }
        run.DashRequest = false;
        BeginTideDive(run);
        var oldBottom = run.Y + 28;
        if (run.DashTime > 0)
        {
            run.VX = run.DashX * 580;
            run.VY = 0;
        }
        else
        {
            run.VX = Approach(run.VX, dx * 205, (dx == 0 ? 1500 : 1250) * Step);
            run.VY = Math.Min(620, run.VY + 1000 * Step);
            ApplyTideWind(run);
        }
        run.X = Math.Clamp(run.X + run.VX * Step, 0, stage.Length - 20);
        run.Y += run.VY * Step;
        run.Grounded = false;
        run.Support = -1;
        // Select the highest crossed top, independently of the ordering of the authored platforms.
        var landingY = float.MaxValue;
        for (var i = 0; i < stage.Platforms.Length; i++)
        {
            var platform = stage.Platforms[i];
            if (!IslandPlatformAvailable(run, i)) continue;
            var x = PlatformX(platform, run.StageTime);
            var y = PlatformY(platform, run.StageTime);
            var previousY = PlatformY(platform, run.StageTime - Step);
            if (run.VY < 0 || (i != carriedSupport && oldBottom > previousY + 1) || run.Y + 28 < y
                || run.X + 20 <= x || run.X >= x + platform.Width || y >= landingY) continue;
            landingY = y;
            run.Support = i;
        }
        if (run.Support >= 0)
        {
            var platform = stage.Platforms[run.Support];
            run.Y = landingY - 28;
            run.VY = 0;
            run.Grounded = true;
            run.Jumps = 0;
            run.AirDash = false;
            _tideAirStomps = 0;
            if (_tideDiving) LandTideDive(run);
            if (platform.Kind == PlatformKind.Crumble && run.Platforms[run.Support] == 0) run.Platforms[run.Support] = Step;
            if (platform.Kind == PlatformKind.Spring)
            {
                run.VY = -570;
                run.Grounded = false;
                run.Jumps = 1;
                run.Support = -1;
                Burst(run, run.X + 10, run.Y + 28, ClientGameInk.Heal, 6);
            }
        }
        UpdateTideAdventureObjects(run, oldBottom);
        for (var i = 0; i < stage.Flags.Length; i++)
        {
            var flag = stage.Flags[i];
            if (i < run.Checkpoint || !Overlap(run.X, run.Y, 20, 28, flag.X - 10, flag.Y - 75, 30, 75)) continue;
            run.Checkpoint = i + 1;
            run.CheckpointX = flag.X + 16;
            run.CheckpointY = flag.Y - 28;
            AddScore(run, 200);
            RallyIslandCheckpoint(run, i);
            Burst(run, flag.X, flag.Y - 60, ClientGameInk.Checkpoint, 8);
        }
        for (var i = 0; i < stage.Pickups.Length; i++)
        {
            var pickup = stage.Pickups[i];
            if (run.Picked[i] || !Overlap(run.X, run.Y, 20, 28, pickup.X - 11, pickup.Y - 11, 22, 22)) continue;
            if (pickup.Kind == PickupKind.Heal && run.Health >= 3) continue;
            run.Picked[i] = true;
            TakePickup(run, pickup);
            RewardIslandPickup(run, pickup);
        }
        UpdateIslandEnemies(run, oldBottom);
        if (run.Finished) return;
        foreach (var hazard in stage.Hazards)
        {
            var height = hazard.Vent ? 48 : 18;
            if (!VentActive(hazard, run.StageTime) || run.Hurt > 0 || run.Shield > 0 || run.DashTime > 0
                || !Overlap(run.X, run.Y, 20, 28, hazard.X, hazard.Vent ? 330 - height : hazard.Y, hazard.Width, height)) continue;
            HurtIsland(run);
            run.VY = -300;
            run.Grounded = false;
            if (run.Finished) return;
        }
        if (run.Y > Height + 30)
        {
            _tideStageFalls++;
            // Combat invulnerability never protects from water. Only an earned tether prevents a lost heart.
            FailTideCourier();
            if (_tideRescueReady)
            {
                _tideRescueReady = false;
                TideNotice("Secret tether used! Checkpoint reached without losing a heart.");
            }
            else HurtIsland(run);
            if (run.Finished) return;
            RespawnIsland(run);
        }
        // Keep dash interactions active for every step that used dash movement.
        run.DashTime = Math.Max(0, run.DashTime - Step);
        RecordTidePace(run);
        if (run.X >= stage.Length - 92 && run.StageCollected >= 6 && !TideGuardianAlive(run)) ClearIslandStage(run);
        else if (run.StageTime >= 180) Finish(run, false);
    }

    private void UpdateIslandEnemies(Run run, float oldBottom)
    {
        for (var i = 0; i < run.Enemies.Length; i++)
        {
            ref var enemy = ref run.Enemies[i];
            if (!enemy.Active) continue;
            enemy.Hurt = Math.Max(0, enemy.Hurt - Step);
            enemy.Timer -= Step;
            switch (enemy.Kind)
            {
                case EnemyKind.Crab:
                    enemy.X = enemy.OriginX + MathF.Sin(run.StageTime * 1.9f + enemy.Phase) * enemy.Range;
                    break;
                case EnemyKind.Hopper:
                    if (enemy.State != 1)
                        enemy.X = Approach(enemy.X,
                            enemy.OriginX + MathF.Sin(run.StageTime * 1.35f + enemy.Phase) * enemy.Range, 105 * Step);
                    enemy.VY += 920 * Step;
                    enemy.Y += enemy.VY * Step;
                    if (enemy.Y >= enemy.OriginY)
                    {
                        enemy.Y = enemy.OriginY;
                        enemy.VY = 0;
                        if (enemy.State == 2) enemy.State = 0;
                        if (enemy.Timer <= 0)
                        {
                            if (enemy.State == 0) { enemy.State = 1; enemy.Timer = .38f; }
                            else { enemy.State = 2; enemy.VY = -280; enemy.Timer = 1.3f; }
                        }
                    }
                    break;
                case EnemyKind.Bird:
                    UpdateTideBird(run, ref enemy);
                    break;
                case EnemyKind.Cannon:
                    if (enemy.MaxHealth >= 6) { UpdateTideGuardian(run, ref enemy); break; }
                    if (enemy.State == 0)
                    {
                        enemy.Facing = run.X + 10 < enemy.X ? -1 : 1;
                        if (enemy.Timer <= 0 && Math.Abs(run.X - enemy.X) < 580)
                        {
                            enemy.State = 1;
                            enemy.Timer = .6f;
                        }
                    }
                    else if (enemy.Timer <= 0)
                    {
                        // The windup locks direction: crossing behind the cannon is a deliberate dodge.
                        SpawnShot(run, enemy.X + enemy.Facing * 18, enemy.Y - 9, enemy.Facing * 170, -12, true);
                        enemy.State = 0;
                        enemy.Timer = 2.2f;
                    }
                    break;
            }
            if (enemy.Hurt > 0 || !Overlap(run.X, run.Y, 20, 28, enemy.X - 14, enemy.Y - 15, 28, 29)) continue;
            var stomp = run.VY > 0 && oldBottom <= enemy.Y - 4;
            if (stomp || run.DashTime > 0 || run.Shield > 0)
            {
                enemy.Health -= TideHitDamage(enemy, stomp && _tideDiving ? 2 : 1);
                enemy.Hurt = .35f;
                if (stomp)
                {
                    run.VY = -340;
                    run.Jumps = 1;
                    run.Grounded = false;
                    run.AirDash = false;
                    run.Support = -1;
                    run.Coyote = 0;
                    _tideAirStomps++;
                    _tideDiving = false;
                    if (_tideAirStomps >= 2)
                    {
                        AddScore(run, Math.Min(5, _tideAirStomps) * 150);
                        run.DashCooldown = 0;
                        TideNotice("Aerial chain! Bonus points and dash ready. Keep bouncing.");
                    }
                }
                Burst(run, enemy.X, enemy.Y, ClientGameInk.Supply, 6);
                if (enemy.Health <= 0) DefeatTideEnemy(run, ref enemy);
            }
            else if (run.Hurt <= 0 && !(enemy.Kind == EnemyKind.Bird && enemy.State == 3))
            {
                HurtIsland(run);
                run.VY = -260;
                run.Grounded = false;
                if (run.Finished) return;
            }
        }
        for (var i = 0; i < run.Shots.Length; i++)
        {
            ref var shot = ref run.Shots[i];
            if (!shot.Active) continue;
            shot.X += shot.VX * Step;
            shot.Y += shot.VY * Step;
            shot.Life -= Step;
            if (shot.Life <= 0 || Math.Abs(shot.X - run.X) > 900) { shot.Active = false; continue; }
            if (!shot.Hostile)
            {
                for (var target = 0; target < run.Enemies.Length; target++)
                {
                    ref var enemy = ref run.Enemies[target];
                    if (!enemy.Active || enemy.Hurt > 0
                        || !Overlap(shot.X - 5, shot.Y - 5, 10, 10, enemy.X - 14, enemy.Y - 15, 28, 29)) continue;
                    shot.Active = false;
                    enemy.Health -= TideHitDamage(enemy, shot.Damage);
                    enemy.Hurt = .35f;
                    Burst(run, enemy.X, enemy.Y, ClientGameInk.Dash, 8);
                    if (enemy.Health <= 0)
                    {
                        DefeatTideEnemy(run, ref enemy);
                    }
                    break;
                }
                continue;
            }
            if (!Overlap(run.X, run.Y, 20, 28, shot.X - 5, shot.Y - 5, 10, 10)) continue;
            if (run.DashTime > 0)
            {
                shot.Hostile = false;
                shot.VY = -shot.VY * (360f / Math.Abs(shot.VX));
                shot.VX = run.DashX * 360;
                shot.X = run.X + 10 + run.DashX * 18;
                shot.Life = 2.4f;
                Burst(run, shot.X, shot.Y, ClientGameInk.Dash, 6);
                TideNotice("Cannonball returned! Dash through shots to hit enemies.");
                continue;
            }
            shot.Active = false;
            if (run.Hurt > 0 || run.Shield > 0) continue;
            HurtIsland(run);
            if (run.Finished) return;
        }
    }

    private void DrawIsland(Run run)
    {
        var stage = IslandStages[run.Started ? run.Stage : 0];
        var time = run.Started ? run.StageTime : 0;
        var camera = run.Started ? Math.Clamp(run.X - 260, 0, stage.Length - Width) : 0;
        Shape(0, 0, Width, Height, ClientGameInk.Sky);
        Sprite(687 - camera * .025f, 26, 56, 56, ClientGameSprite.Sun);
        for (var i = 0; i < 8; i++)
        {
            var x = i * 145 - camera * .12f % 145;
            var y = 44 + i % 3 * 22;
            Sprite(x, y - 15, 116, 54, ClientGameSprite.Cloud, i % 2 != 0);
            Sprite(x + 12, 165 + i % 3 * 14, 142, 180, ClientGameSprite.Cliff, i % 2 != 0);
        }
        Sprite(-64 - (camera * .35f + time * 12) % 64, 330, Width + 128, 64, ClientGameSprite.Water, tileSize: 64);
        DrawTideWind(run, camera, time);
        foreach (var platform in stage.Platforms)
            if (platform.Kind == PlatformKind.Ground)
                Sprite(platform.X + 24 - camera, platform.Y - 128, 106, 140, ClientGameSprite.PalmTree);
        for (var i = 0; i < stage.Platforms.Length; i++)
        {
            var platform = stage.Platforms[i];
            var x = PlatformX(platform, time) - camera;
            var y = PlatformY(platform, time);
            if (platform.Kind == PlatformKind.Crumble && run.Started && run.Platforms[i] >= .65f)
                continue;
            if (platform.Kind == PlatformKind.RuneBridge && (!run.Started || _tideRuneTime <= 0))
            {
                Shape(x, y, 9, 3, ClientGameInk.Secret);
                Shape(x + platform.Width - 9, y, 9, 3, ClientGameInk.Secret);
                continue;
            }
            if (platform.Kind == PlatformKind.Ground)
            {
                Sprite(x, platform.Y + 24, platform.Width, platform.Height - 24, ClientGameSprite.Soil, tileSize: 64);
                for (var edge = 0f; edge < platform.Width; edge += 64)
                    Sprite(x + edge, platform.Y, Math.Min(64, platform.Width - edge), 26, ClientGameSprite.GrassSoil);
            }
            else
            {
                var sprite = platform.Kind switch
                {
                    PlatformKind.Moving => ClientGameSprite.MovingPlatform,
                    PlatformKind.Lift => ClientGameSprite.MovingPlatform,
                    PlatformKind.RuneBridge => ClientGameSprite.StoneLedge,
                    PlatformKind.Crumble => run.Started && run.Platforms[i] > 0 ? ClientGameSprite.CrumbleCracked : ClientGameSprite.CrumbleIntact,
                    PlatformKind.Spring => run.Started && run.VY < -420 && Math.Abs(run.X + 10 - (platform.X + platform.Width / 2)) < 36
                        ? ClientGameSprite.SpringCompressed : ClientGameSprite.SpringIdle,
                    _ => ClientGameSprite.StoneLedge
                };
                Sprite(x - 2, y - 3, platform.Width + 4, platform.Height + 10, sprite);
                if (platform.Kind == PlatformKind.Lift)
                {
                    Shape(x + platform.Width / 2 - 1, platform.Y - platform.Travel, 2, platform.Travel * 2, ClientGameInk.Detail);
                    Shape(x + 8, y + 2, platform.Width - 16, 3, ClientGameInk.Dash);
                }
                if (platform.Kind == PlatformKind.RuneBridge)
                    Shape(x + 4, y, platform.Width - 8, 3,
                        _tideRuneTime < 2 && (int)(time * 6) % 2 == 0 ? ClientGameInk.Hazard : ClientGameInk.Secret);
            }
        }
        foreach (var hazard in stage.Hazards)
        {
            var x = hazard.X - camera;
            var active = VentActive(hazard, time);
            var sprite = !hazard.Vent ? ClientGameSprite.Spikes : active ? ClientGameSprite.VentActive : ClientGameSprite.VentIdle;
            var height = hazard.Vent && active ? 56 : 26;
            Sprite(x - 3, 334 - height, hazard.Width + 6, height, sprite);
            var warning = VentWarning(hazard, time);
            if (!active && warning > 0)
            {
                Shape(x, 326, hazard.Width, 4, ClientGameInk.Shadow);
                Shape(x, 326, hazard.Width * warning, 4, ClientGameInk.Hazard);
                Shape(x + hazard.Width / 2 - 2, 288, 4, 12, ClientGameInk.Hazard);
                Shape(x + hazard.Width / 2 - 2, 304, 4, 4, ClientGameInk.Hazard);
            }
        }
        for (var i = 0; i < stage.Pickups.Length; i++)
            if (!run.Started || !run.Picked[i]) DrawPickup(stage.Pickups[i], camera, time);
        for (var i = 0; i < stage.Flags.Length; i++)
        {
            var flag = stage.Flags[i];
            Sprite(flag.X - camera - 15, flag.Y - 79, 60, 88,
                run.Started && run.Checkpoint > i ? ClientGameSprite.CheckpointOn : ClientGameSprite.CheckpointOff);
        }
        var goal = stage.Length - 70 - camera;
        Sprite(goal - 19, 202, 84, 140, ClientGameSprite.GoalBeacon);
        if (!run.Started || run.StageCollected < 6 || TideGuardianAlive(run)) Sprite(goal + 20, 258, 22, 22, ClientGameSprite.Shield);
        DrawTideAdventure(run, camera, time);
        if (run.Started)
        {
            foreach (var enemy in run.Enemies) if (enemy.Active) DrawIslandEnemy(enemy, camera, time);
            foreach (var shot in run.Shots)
                if (shot.Active) Sprite(shot.X - camera - 7, shot.Y - 7, 14, 14,
                    shot.Hostile ? ClientGameSprite.BulletEnemy : ClientGameSprite.BulletPlayer, shot.VX < 0);
        }
        else
        {
            foreach (var seed in stage.Enemies)
                DrawIslandEnemy(new Actor { X = seed.X, Y = seed.Y, Kind = seed.Kind, Facing = -1 }, camera, time);
        }
        var px = run.Started ? run.X - camera : 44;
        var py = run.Started ? run.Y : 302;
        var blink = run.Started && run.Hurt > 0 && (int)(run.Hurt * 12) % 2 == 0;
        var frame = !run.Started ? ClientGameSprite.RunnerIdle : blink ? ClientGameSprite.RunnerHit
            : run.DashTime > 0 ? ClientGameSprite.RunnerDash
            : !run.Grounded ? run.VY < 0 ? ClientGameSprite.RunnerJump : ClientGameSprite.RunnerFall
            : Math.Abs(run.VX) > 10 ? (int)(time * 11) % 2 == 0 ? ClientGameSprite.RunnerRun1 : ClientGameSprite.RunnerRun2
            : ClientGameSprite.RunnerIdle;
        Sprite(px - 5, py - 5, 30, 38, frame, run.Facing < 0);
        if (run.Started && run.Shield > 0) Sprite(px + 3, py - 20, 15, 15, ClientGameSprite.Shield);
        if (run.Started && _tideDiving)
        {
            Shape(px + 1, py + 29, 18, 3, ClientGameInk.Dash);
            Shape(px + 5, py + 34, 10, 3, ClientGameInk.Dash);
        }
        DrawParticles(run, camera);
        Shape(14, 14, Width - 28, 4, ClientGameInk.Shadow);
        Shape(14, 14, (Width - 28) * (run.Started ? run.X / stage.Length : 0), 4, ClientGameInk.Checkpoint);
        DrawAbility(run);
        if (run.Started)
        {
            DrawIslandChallenges(run, stage, camera);
            DrawTideAdventureHud(run);
        }
    }

    private void DrawIslandChallenges(Run run, IslandStage stage, float camera)
    {
        var supplies = IslandSupplyCount(stage);
        Sprite(14, 26, 22, 22, ClientGameSprite.Supply);
        for (var i = 0; i < supplies; i++)
            Shape(42 + i * 10, 33, 6, 10, i < run.StageCollected ? ClientGameInk.Supply : ClientGameInk.Shadow);
        if (_tideManifestClaimed) Sprite(48 + supplies * 10, 26, 22, 22, ClientGameSprite.Shield);
        if (_tideRescueReady)
        {
            Sprite(Width - 41, 28, 23, 23, ClientGameSprite.Secret);
            Shape(Width - 32, 53, 4, 12, ClientGameInk.Secret);
            Shape(Width - 32, 62, 11, 3, ClientGameInk.Secret);
        }
        if (_tideCourierState != 0)
        {
            var ink = _tideCourierState == 3 ? ClientGameInk.Hazard : ClientGameInk.Checkpoint;
            Sprite(Width - 181, Height - 42, 25, 34,
                _tideCourierState == 2 ? ClientGameSprite.CheckpointOn : ClientGameSprite.CheckpointOff);
            Shape(Width - 148, Height - 16, 128, 5, ClientGameInk.Shadow);
            var fill = _tideCourierState == 2 ? 1 : _tideCourierState == 3 ? .04f : _tideCourierTime / TideCourierLimit;
            Shape(Width - 148, Height - 16, 128 * fill, 5, ink);
        }
        if (run.StageCollected >= 6 || run.X < stage.Length - 170) return;
        var nearest = -1;
        var distance = float.MaxValue;
        for (var i = 0; i < stage.Pickups.Length; i++)
        {
            var pickup = stage.Pickups[i];
            if (run.Picked[i] || pickup.Kind != PickupKind.Supply) continue;
            var nextDistance = Math.Abs(pickup.X - run.X) + Math.Abs(pickup.Y - run.Y) * .5f;
            if (nextDistance >= distance) continue;
            nearest = i;
            distance = nextDistance;
        }
        if (nearest < 0) return;
        var target = stage.Pickups[nearest];
        var markerX = Math.Clamp(target.X - camera, 30, Width - 30);
        Sprite(markerX - 11, 58, 22, 22, ClientGameSprite.Supply);
        if (target.X - camera < 30 || target.X - camera > Width - 30)
        {
            var direction = target.X < run.X ? -1 : 1;
            for (var i = 0; i < 3; i++)
                Shape(markerX + direction * (15 + i * 3) - 1, 61 + i * 3, 3, 16 - i * 6, ClientGameInk.Supply);
        }
        else
        {
            Shape(markerX - 1, 82, 3, 12, ClientGameInk.Supply);
            Shape(markerX - 4, 91, 9, 3, ClientGameInk.Supply);
        }
    }

    private void DrawIslandEnemy(Actor enemy, float camera, float time)
    {
        var x = enemy.X - camera;
        var alternate = (int)(time * 8 + enemy.Phase) % 2 != 0;
        var frame = enemy.Kind switch
        {
            EnemyKind.Hopper => Math.Abs(enemy.VY) > 5 ? ClientGameSprite.HopperJump : ClientGameSprite.HopperIdle,
            EnemyKind.Bird => alternate ? ClientGameSprite.Bird2 : ClientGameSprite.Bird1,
            EnemyKind.Cannon => enemy.Facing < 0 ? ClientGameSprite.CannonLeft : ClientGameSprite.CannonRight,
            _ => alternate ? ClientGameSprite.Crab2 : ClientGameSprite.Crab1
        };
        var facingLeft = enemy.Kind != EnemyKind.Cannon && MathF.Cos(time * (enemy.Kind == EnemyKind.Crab ? 1.9f : enemy.Kind == EnemyKind.Hopper ? 1.35f : 1.4f) + enemy.Phase) < 0;
        var width = enemy.Kind == EnemyKind.Bird ? 50 : enemy.Kind == EnemyKind.Cannon ? 44 : 38;
        var crouching = enemy.Kind == EnemyKind.Hopper && enemy.State == 1;
        Sprite(x - width / 2, enemy.Y - (crouching ? 12 : 21), width, crouching ? 29 : 38, frame, facingLeft);
        if (crouching)
        {
            Shape(x - 14, enemy.Y - 31, 28, 3, ClientGameInk.Shadow);
            Shape(x - 14, enemy.Y - 31, 28 * Math.Clamp(1 - enemy.Timer / .38f, 0, 1), 3, ClientGameInk.Hazard);
            Shape(x - 2, enemy.Y - 48, 4, 10, ClientGameInk.Hazard);
        }
        if (enemy.Kind == EnemyKind.Cannon && enemy.State == 1 && enemy.MaxHealth < 6)
        {
            var muzzleX = x + enemy.Facing * 18;
            Shape(enemy.Facing < 0 ? muzzleX - 90 : muzzleX, enemy.Y - 10, 90, 2, ClientGameInk.Hazard);
            Shape(x - 18, enemy.Y - 29, 36, 4, ClientGameInk.Shadow);
            Shape(x - 18, enemy.Y - 29, 36 * Math.Clamp(1 - enemy.Timer / .6f, 0, 1), 4, ClientGameInk.Hazard);
        }
        if (enemy.Kind == EnemyKind.Cannon && enemy.MaxHealth >= 6) DrawTideGuardian(enemy, camera);
        if (enemy.Kind == EnemyKind.Bird) DrawTideBirdWarning(enemy, camera);
        if (enemy.Hurt > 0) Sprite(x - 21, enemy.Y - 23, 42, 42, ClientGameSprite.Impact);
    }
}
