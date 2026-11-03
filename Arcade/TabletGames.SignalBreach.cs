// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Client;

internal sealed partial class ClientTabletGames
{
    private readonly record struct Cover(float X, float Y, float Width, float Height);
    private sealed record OpsStage(string Name, int TimeLimit, Cover[] Cover, Flag[] Relays,
        Pickup[] Pickups, EnemySeed[] Enemies, Flag Exit);

    private static readonly OpsStage[] OpsStages =
    {
        new("01 / DOCKYARD UPLINK", 150,
            new Cover[]
            {
                new(176,65,90,70), new(176,255,90,70), new(358,154,110,72),
                new(520,55,80,64), new(520,273,80,64), new(682,155,82,72)
            },
            new Flag[] { new(335,72), new(630,315) },
            new Pickup[]
            {
                new(PickupKind.Secret,103,72), new(PickupKind.Secret,804,83),
                new(PickupKind.Rapid,313,194), new(PickupKind.Heal,103,315),
                new(PickupKind.Shield,630,72), new(PickupKind.Heal,807,309)
            },
            new EnemySeed[]
            {
                new(EnemyKind.Patrol,330,104,50,2), new(EnemyKind.Patrol,310,290,38,2),
                new(EnemyKind.Ranger,490,195,40,3), new(EnemyKind.Charger,650,117,38,2),
                new(EnemyKind.Patrol,648,290,35,2), new(EnemyKind.Turret,800,195,0,4)
            }, new(829,195)),
        new("02 / BLACKOUT TRANSIT", 180,
            new Cover[]
            {
                new(172,115,78,155), new(318,49,92,65), new(318,277,92,65),
                new(450,163,116,63), new(611,64,68,83), new(611,244,68,83), new(733,167,58,60)
            },
            new Flag[] { new(102,314), new(462,69), new(738,316) },
            new Pickup[]
            {
                new(PickupKind.Secret,365,195), new(PickupKind.Secret,813,72),
                new(PickupKind.Rapid,278,72), new(PickupKind.Shield,510,308),
                new(PickupKind.Heal,102,72), new(PickupKind.Heal,570,82), new(PickupKind.Rapid,814,314)
            },
            new EnemySeed[]
            {
                new(EnemyKind.Charger,100,295,38,2), new(EnemyKind.Patrol,285,195,22,2),
                new(EnemyKind.Ranger,450,122,35,3), new(EnemyKind.Turret,500,265,0,4),
                new(EnemyKind.Charger,566,320,33,2), new(EnemyKind.Ranger,703,194,22,3),
                new(EnemyKind.Patrol,746,104,40,2), new(EnemyKind.Turret,818,265,0,4)
            }, new(832,195)),
        new("03 / THE GLASS REACTOR", 210,
            new Cover[]
            {
                new(164,69,78,77), new(164,244,78,77), new(335,157,90,75),
                new(505,57,69,85), new(505,251,69,85), new(625,157,46,75)
            },
            new Flag[] { new(354,72), new(354,319) },
            new Pickup[]
            {
                new(PickupKind.Secret,104,72), new(PickupKind.Secret,792,320),
                new(PickupKind.Heal,106,319), new(PickupKind.Heal,704,73),
                new(PickupKind.Rapid,460,195), new(PickupKind.Shield,614,320), new(PickupKind.Rapid,795,73)
            },
            new EnemySeed[]
            {
                new(EnemyKind.Boss,758,195,0,32), new(EnemyKind.Patrol,290,92,27,2),
                new(EnemyKind.Charger,289,295,28,2), new(EnemyKind.Ranger,459,96,24,3),
                new(EnemyKind.Ranger,463,295,24,3), new(EnemyKind.Turret,619,82,0,4),
                new(EnemyKind.Turret,618,305,0,4)
            }, new(831,195))
    };

    private string OpsObjective(Run run)
    {
        if (run.Finished) return run.Won ? "Reactor offline! Bonus medals: shield = flawless / shot = full clear / crate = salvage."
            : run.Health <= 0 ? "Armor depleted. Restart to breach again." : "Extraction window expired. Restart to breach again.";
        if (!run.Started) return "Hack relays, defeat jammer officers, extract. Roll over mines; two secrets unlock a vault.";
        var encounterObjective = OpsEncounterObjective(run);
        if (encounterObjective.Length > 0) return encounterObjective;
        if (_opsCombat.Notice > 0) return _opsCombat.NoticeKind switch
        {
            1 => "Perfect roll! Next shot deals double damage. Counter charge lasts six seconds.",
            2 => "Armor hit. Downloads briefly interrupted; partial hack progress is retained.",
            3 => "Relay pulse! Nearby threats stunned, hostile shots cleared, roll recharged.",
            4 => "Salvage recovered! Faster recovery earns more points and recharges your roll.",
            5 => "Reactor shifting! Watch the gaps and the arriving guards.",
            6 => "Reactor venting! Its bright core takes double damage between volleys.",
            7 => OpsMedalText(_opsCombat.LastMedals),
            8 => "Countershot fired! Roll through a hostile shot to charge another.",
            9 => "Jammer offline. Its linked relays now accept downloads.",
            10 => "Relay repair complete. One armor restored; this station is now spent.",
            11 => "Both cipher fragments found! The gold vault is ready to decrypt.",
            12 => "Vault decrypted! Bonus +900, one armor and rapid fire recovered.",
            13 => "Black-box alarm! Hold the gold terminal to upload; leaving pauses progress.",
            14 => "Black box secured! Bonus +1000 and a fresh shield. Extraction remains your main goal.",
            15 => "Mine defused! Roll through its center before the warning finishes. +125",
            16 => "Guard collision! Bait charging enemies into their allies.",
            17 => "Mortar salvo! Leave the marked squares before impact; cover and rolls protect you.",
            18 => "Relay cascade! Reach the next uplink within 14 seconds for faster hacking and a bonus.",
            19 => "Cascade linked! +450. A fresh 14-second link is ready if another relay remains.",
            _ => "Recover salvage before it expires. Keep moving between cover."
        };
        var stage = OpsStages[run.Stage];
        if (run.RelayCount < stage.Relays.Length)
        {
            if (run.Stage == 2) return run.RelayCount == 0 ? "Hack both cyan relays to break the reactor shield. 0/2" : "One relay left. Break the reactor shield. 1/2";
            if (stage.Relays.Length == 3) return run.RelayCount switch
            {
                0 => "Stand on the cyan relays to hack them. Uplinks 0/3",
                1 => "Stand on the cyan relays to hack them. Uplinks 1/3",
                _ => "Stand on the cyan relays to hack them. Uplinks 2/3"
            };
            return run.RelayCount == 0 ? "Stand on both cyan relays to hack them. Uplinks 0/2" : "Stand on the remaining cyan relay. Uplinks 1/2";
        }
        if (run.Stage == 2 && run.Enemies[0].Active) return "Reactor: dodge red fans, hold the cyan gap for rings. Bright core takes double damage.";
        return "Extraction open! Reach the green pad at the right edge.";
    }

    private void UpdateOps(Run run)
    {
        var stage = OpsStages[run.Stage];
        UpdateOpsCombat(run, stage);
        var dx = (float)MoveX(run);
        var dy = (float)MoveY(run);
        var length = MathF.Sqrt(dx * dx + dy * dy);
        if (length > 0)
        {
            dx /= length;
            dy /= length;
            if (dx != 0) run.Facing = dx < 0 ? -1 : 1;
        }
        if (run.DashRequest && run.DashCooldown <= 0)
        {
            run.DashX = length > 0 ? dx : run.Facing;
            run.DashY = length > 0 ? dy : 0;
            run.DashTime = .18f;
            run.DashCooldown = 1.2f;
            _opsCombat.RollRewarded = false;
            Burst(run, run.X, run.Y, ClientGameInk.Dash, 6);
        }
        run.DashRequest = run.Jump = false;
        var dash = run.DashTime > 0;
        _opsCombat.RollActive = dash;
        MoveOps(ref run.X, ref run.Y, (dash ? run.DashX * 540 : dx * 173) * Step,
            (dash ? run.DashY * 540 : dy * 173) * Step, 11, stage.Cover);
        MoveOpsConveyor(run, stage);
        run.DashTime = Math.Max(0, run.DashTime - Step);
        UpdateOpsTerrain(run, stage);
        if (run.Finished) return;
        UpdateOpsFocus(run, stage);
        if (IsFiring(run) && run.Shot <= 0) FireOps(run, stage);
        UpdateOpsEnemies(run, stage);
        if (run.Finished) return;
        UpdateOpsMortars(run, stage);
        if (run.Finished) return;
        UpdateOpsShots(run, stage);
        if (run.Finished) return;
        for (var i = 0; i < stage.Pickups.Length; i++)
        {
            var pickup = stage.Pickups[i];
            if (run.Picked[i] || !Near(run.X, run.Y, pickup.X, pickup.Y, 23)) continue;
            if (pickup.Kind == PickupKind.Heal && run.Health >= 5) continue;
            run.Picked[i] = true;
            TakePickup(run, pickup);
        }
        UpdateOpsRelays(run, stage);
        UpdateOpsSideObjectives(run, stage);
        var exitOpen = run.RelayCount == stage.Relays.Length && (run.Stage != 2 || !run.Enemies[0].Active);
        if (exitOpen && Near(run.X, run.Y, stage.Exit.X, stage.Exit.Y, 26))
        {
            AwardOpsStage(run);
            ClearStage(run);
        }
        else if (run.StageTime >= stage.TimeLimit) Finish(run, false);
    }

    private static void MoveOps(ref float x, ref float y, float dx, float dy, float radius, Cover[] cover)
    {
        var newX = Math.Clamp(x + dx, radius + 8, Width - radius - 8);
        foreach (var block in cover)
        {
            if (!Overlap(newX - radius, y - radius, radius * 2, radius * 2, block.X, block.Y, block.Width, block.Height)) continue;
            newX = dx > 0 ? block.X - radius : dx < 0 ? block.X + block.Width + radius : x;
        }
        x = newX;
        var newY = Math.Clamp(y + dy, radius + 8, Height - radius - 8);
        foreach (var block in cover)
        {
            if (!Overlap(x - radius, newY - radius, radius * 2, radius * 2, block.X, block.Y, block.Width, block.Height)) continue;
            newY = dy > 0 ? block.Y - radius : dy < 0 ? block.Y + block.Height + radius : y;
        }
        y = newY;
    }

    private static bool ClearSight(float x, float y, float targetX, float targetY, Cover[] cover)
    {
        foreach (var block in cover)
            if (SegmentHits(x, y, targetX, targetY, block.X - 2, block.Y - 2, block.Width + 4, block.Height + 4)) return false;
        return true;
    }

    private static bool SegmentHits(float x, float y, float endX, float endY, float bx, float by, float bw, float bh)
    {
        var start = 0f;
        var end = 1f;
        return ClipAxis(x, endX - x, bx, bx + bw, ref start, ref end)
            && ClipAxis(y, endY - y, by, by + bh, ref start, ref end);
    }

    private static bool ClipAxis(float origin, float delta, float min, float max, ref float start, ref float end)
    {
        if (Math.Abs(delta) < .0001f) return origin >= min && origin <= max;
        var a = (min - origin) / delta;
        var b = (max - origin) / delta;
        if (a > b) (a, b) = (b, a);
        start = Math.Max(start, a);
        end = Math.Min(end, b);
        return start <= end;
    }

    private void FireOps(Run run, OpsStage stage)
    {
        if (!OpsCanTarget(run, stage, _opsCombat.Focus, 540, out var distance)) return;
        var target = run.Enemies[_opsCombat.Focus];
        var motion = _opsCombat.Enemies[_opsCombat.Focus];
        var lead = Math.Min(.28f, MathF.Sqrt(distance) / 510);
        var targetX = target.X + motion.VX * lead;
        var targetY = target.Y + motion.VY * lead;
        if (!ClearSight(run.X, run.Y, targetX, targetY, stage.Cover))
        { targetX = target.X; targetY = target.Y; }
        var dx = targetX - run.X;
        var dy = targetY - run.Y;
        var length = Math.Max(1, MathF.Sqrt(dx * dx + dy * dy));
        if (!SpawnShot(run, run.X, run.Y, dx / length * 510, dy / length * 510, false,
            _opsCombat.Counter > 0 ? 2 : 1)) return;
        run.Shot = run.Power > 0 ? .12f : .25f;
        if (_opsCombat.Counter > 0)
        {
            _opsCombat.Counter = 0;
            OpsNotice(8, 1.1f);
        }
    }

    private void UpdateOpsEnemies(Run run, OpsStage stage)
    {
        for (var i = 0; i < run.Enemies.Length; i++)
        {
            ref var enemy = ref run.Enemies[i];
            if (!enemy.Active) continue;
            ref var combat = ref _opsCombat.Enemies[i];
            enemy.Hurt = Math.Max(0, enemy.Hurt - Step);
            combat.Exposed = Math.Max(0, combat.Exposed - Step);
            combat.VX = combat.VY = 0;
            if (combat.Arrival > 0)
            {
                // A player occupying a spawn marker holds its guard safely in the telegraph.
                if (!Near(run.X, run.Y, enemy.X, enemy.Y, 65)) combat.Arrival = Math.Max(0, combat.Arrival - Step);
                continue;
            }
            enemy.Timer -= Step;
            combat.Stun = Math.Max(0, combat.Stun - Step);
            if (combat.Stun > 0) continue;
            var oldX = enemy.X;
            var oldY = enemy.Y;
            var dx = run.X - enemy.X;
            var dy = run.Y - enemy.Y;
            var distance = Math.Max(1, MathF.Sqrt(dx * dx + dy * dy));
            var aimX = dx / distance;
            var aimY = dy / distance;
            var visible = distance < 470 && ClearSight(enemy.X, enemy.Y, run.X, run.Y, stage.Cover);
            if (enemy.Kind == EnemyKind.Boss)
            {
                UpdateBoss(run, stage, ref enemy, ref combat, aimX, aimY);
            }
            else if (enemy.Kind == EnemyKind.Charger)
            {
                if (enemy.State == 0)
                {
                    MoveOps(ref enemy.X, ref enemy.Y, aimX * 58 * Step, aimY * 58 * Step, 12, stage.Cover);
                    if (visible && distance < 310 && enemy.Timer <= 0)
                    {
                        enemy.State = 1;
                        enemy.Timer = .55f;
                        enemy.AimX = aimX;
                        enemy.AimY = aimY;
                    }
                }
                else if (enemy.State == 1 && enemy.Timer <= 0)
                { enemy.State = 2; enemy.Timer = .42f; }
                else if (enemy.State == 2)
                {
                    var chargeX = enemy.AimX * 390 * Step;
                    var chargeY = enemy.AimY * 390 * Step;
                    MoveOps(ref enemy.X, ref enemy.Y, chargeX, chargeY, 12, stage.Cover);
                    var hitGuard = CrashOpsCharger(run, stage, i);
                    var hitWall = Math.Abs(enemy.X - oldX - chargeX) > .5f || Math.Abs(enemy.Y - oldY - chargeY) > .5f;
                    if (hitWall || hitGuard || enemy.Timer <= 0)
                    {
                        enemy.State = 3;
                        enemy.Timer = hitWall || hitGuard ? 1.2f : .55f;
                        if (hitWall) Burst(run, enemy.X, enemy.Y, ClientGameInk.Cloud, 7);
                    }
                }
                else if (enemy.State == 3 && enemy.Timer <= 0) { enemy.State = 0; enemy.Timer = .8f; }
            }
            else
            {
                if (enemy.Kind == EnemyKind.Patrol)
                {
                    // Patrols keep their lanes; they stop and shoot when the player enters sight.
                    if (enemy.State == 0 && (!visible || distance > 290))
                    {
                        var patrolX = enemy.OriginX + MathF.Sin(run.StageTime * .85f + enemy.Phase) * enemy.Range;
                        var vx = Math.Clamp(patrolX - enemy.X, -48 * Step, 48 * Step);
                        MoveOps(ref enemy.X, ref enemy.Y, vx, 0, 12, stage.Cover);
                    }
                }
                else if (enemy.Kind == EnemyKind.Ranger && enemy.State == 0)
                {
                    var approach = distance > 230 ? 1 : distance < 145 ? -.7f : 0;
                    var strafe = visible ? MathF.Sin(run.StageTime * 1.4f + enemy.Phase) * .8f : 0;
                    MoveOps(ref enemy.X, ref enemy.Y, (aimX * approach - aimY * strafe) * 69 * Step,
                        (aimY * approach + aimX * strafe) * 69 * Step, 12, stage.Cover);
                }
                if (enemy.State == 1)
                {
                    if (!visible)
                    {
                        enemy.State = 0;
                        enemy.Timer = .65f;
                    }
                    else if (enemy.Timer <= 0)
                    {
                        var speed = enemy.Kind == EnemyKind.Turret ? 220 : 180;
                        SpawnShot(run, enemy.X, enemy.Y, enemy.AimX * speed, enemy.AimY * speed, true);
                        enemy.State = 0;
                        enemy.Timer = enemy.Kind == EnemyKind.Turret ? 1.55f : enemy.Kind == EnemyKind.Ranger ? 1.8f : 2.1f;
                    }
                }
                else if (visible)
                {
                    enemy.AimX = aimX;
                    enemy.AimY = aimY;
                    if (enemy.Timer <= .5f)
                    {
                        enemy.State = 1;
                        enemy.Timer = enemy.Kind == EnemyKind.Turret ? .65f : .5f;
                    }
                }
                else if (enemy.Timer < .55f) enemy.Timer = .55f;
            }
            combat.VX = (enemy.X - oldX) / Step;
            combat.VY = (enemy.Y - oldY) / Step;
            var radius = enemy.Kind == EnemyKind.Boss ? 40 : 24;
            if (run.Hurt > 0 || run.Shield > 0 || _opsCombat.RollActive
                || enemy.Kind == EnemyKind.Charger && enemy.State == 3
                || !Near(run.X, run.Y, enemy.X, enemy.Y, radius)) continue;
            HurtOps(run);
            var pushX = aimX * 22;
            var pushY = aimY * 22;
            MoveOps(ref run.X, ref run.Y, pushX, pushY, 11, stage.Cover);
            if (run.Finished) return;
        }
    }

    private void UpdateBoss(Run run, OpsStage stage, ref Actor boss, ref OpsEnemyState combat, float aimX, float aimY)
    {
        if (run.RelayCount < stage.Relays.Length) return;
        var phase = boss.Health > 21 ? 0 : boss.Health > 10 ? 1 : 2;
        if (phase > run.Sequence)
        {
            run.Sequence = phase;
            AddBossGuard(run, 710, 105, phase == 1 ? EnemyKind.Charger : EnemyKind.Ranger);
            AddBossGuard(run, 710, 285, EnemyKind.Charger);
            for (var i = 0; i < run.Shots.Length; i++) if (run.Shots[i].Hostile) run.Shots[i].Active = false;
            Array.Clear(_opsEncounters.Mortars);
            boss.Timer = 1.5f;
            combat.Exposed = 0;
            Burst(run, boss.X, boss.Y, ClientGameInk.Boss, 12);
            OpsNotice(5, 2.4f);
        }
        if (boss.Timer > .6f)
        {
            boss.AimX = aimX;
            boss.AimY = aimY;
        }
        if (boss.Timer > 0) return;
        var angle = MathF.Atan2(boss.AimY, boss.AimX);
        var pattern = OpsBossPattern(phase, combat.Volleys);
        if (pattern == 2)
        {
            LaunchOpsMortars(run);
        }
        else if (pattern == 0)
        {
            var count = phase == 0 ? 3 : 5;
            for (var i = 0; i < count; i++)
            {
                var direction = angle + (i - (count - 1) * .5f) * .19f;
                SpawnShot(run, boss.X, boss.Y, MathF.Cos(direction) * (190 + phase * 22), MathF.Sin(direction) * (190 + phase * 22), true);
            }
        }
        else
        {
            // The gap is centred on the locked aim. Staying in the marked lane beats the ring.
            for (var i = 2; i <= 10; i++)
            {
                var direction = angle + i * MathF.Tau / 12;
                SpawnShot(run, boss.X, boss.Y, MathF.Cos(direction) * (180 + phase * 18), MathF.Sin(direction) * (180 + phase * 18), true);
            }
        }
        combat.Volleys++;
        combat.Exposed = .65f;
        if (combat.Volleys == 1) OpsNotice(6, 2.5f);
        boss.Timer = phase == 2 ? 1.45f : phase == 1 ? 1.65f : 1.9f;
    }

    private void AddBossGuard(Run run, float x, float y, EnemyKind kind)
    {
        for (var i = 1; i < run.Enemies.Length; i++)
        {
            if (run.Enemies[i].Active) continue;
            run.Enemies[i] = new Actor { Active = true, Kind = kind, X = x, Y = y, OriginX = x, OriginY = y,
                Health = 2, MaxHealth = 2, Timer = 1, Facing = -1 };
            _opsCombat.Enemies[i] = new OpsEnemyState { Arrival = 1.25f };
            return;
        }
    }

    private void UpdateOpsShots(Run run, OpsStage stage)
    {
        for (var i = 0; i < run.Shots.Length; i++)
        {
            ref var shot = ref run.Shots[i];
            if (!shot.Active) continue;
            var oldX = shot.X;
            var oldY = shot.Y;
            shot.X += shot.VX * Step;
            shot.Y += shot.VY * Step;
            shot.Life -= Step;
            if (shot.Life <= 0 || shot.X < 8 || shot.X > Width - 8 || shot.Y < 8 || shot.Y > Height - 8)
            { shot.Active = false; continue; }
            if (!ClearSight(oldX, oldY, shot.X, shot.Y, stage.Cover))
            {
                shot.Active = false;
                Burst(run, shot.X, shot.Y, ClientGameInk.Cloud, 2);
                continue;
            }
            if (shot.Hostile)
            {
                if (!SegmentHits(oldX, oldY, shot.X, shot.Y, run.X - 12, run.Y - 12, 24, 24)) continue;
                shot.Active = false;
                if (_opsCombat.RollActive) { RewardOpsRoll(run); continue; }
                if (run.Hurt > 0 || run.Shield > 0) continue;
                HurtOps(run);
                if (run.Finished) return;
            }
            else
            {
                for (var j = 0; j < run.Enemies.Length; j++)
                {
                    ref var enemy = ref run.Enemies[j];
                    if (!enemy.Active || _opsCombat.Enemies[j].Arrival > 0) continue;
                    var radius = enemy.Kind == EnemyKind.Boss ? 30 : 14;
                    if (!SegmentHits(oldX, oldY, shot.X, shot.Y, enemy.X - radius, enemy.Y - radius, radius * 2, radius * 2)) continue;
                    shot.Active = false;
                    if (enemy.Kind == EnemyKind.Boss && run.RelayCount < stage.Relays.Length)
                    { Burst(run, shot.X, shot.Y, ClientGameInk.Shield, 2); break; }
                    var damage = shot.Damage;
                    if (enemy.Kind == EnemyKind.Boss && _opsCombat.Enemies[j].Exposed > 0) damage *= 2;
                    DamageOpsEnemy(run, stage, j, damage, shot.VX, shot.VY);
                    Burst(run, shot.X, shot.Y, ClientGameInk.Supply, 3);
                    break;
                }
            }
        }
    }

    private void DrawOps(Run run)
    {
        var stage = OpsStages[run.Started ? run.Stage : 0];
        var time = run.Started ? run.StageTime : 0;
        Shape(0, 0, Width, Height, ClientGameInk.Sky);
        Sprite(8, 8, Width - 16, Height - 16, run.Stage == 2 ? ClientGameSprite.FloorGrate : ClientGameSprite.SteelFloor, tileSize: 64);
        Sprite(24, 172, Width - 48, 48, ClientGameSprite.FloorGrate, tileSize: 64);
        Sprite(0, 0, Width, 10, ClientGameSprite.CoverWall, tileSize: 32);
        Sprite(0, Height - 10, Width, 10, ClientGameSprite.CoverWall, tileSize: 32);
        Sprite(0, 0, 10, Height, ClientGameSprite.CoverWall, tileSize: 32);
        Sprite(Width - 10, 0, 10, Height, ClientGameSprite.CoverWall, tileSize: 32);
        DrawOpsTerrain(run);
        for (var i = 0; i < stage.Cover.Length; i++)
        {
            var block = stage.Cover[i];
            Sprite(block.X - 4, block.Y - 4, block.Width + 8, block.Height + 8,
                block.Height > 100 ? ClientGameSprite.CoverWall : ClientGameSprite.Crate,
                tileSize: block.Height > 100 ? 64 : 0);
        }
        for (var i = 0; i < stage.Relays.Length; i++)
        {
            var node = stage.Relays[i];
            var hacked = run.Started && run.Relays[i];
            Sprite(node.X - 27, node.Y - 27, 54, 54, hacked ? ClientGameSprite.RelayOn : ClientGameSprite.RelayOff);
            if (run.Started && !hacked && _opsCombat.Relays[i] > 0)
            {
                Shape(node.X - 20, node.Y + 25, 40, 5, ClientGameInk.Shadow);
                Shape(node.X - 20, node.Y + 25, 40 * Math.Clamp(_opsCombat.Relays[i] / 1.1f, 0, 1), 5,
                    (_opsCombat.HackLock > 0 && run.RelayIndex == i) || OpsRelayJammed(run, stage, i)
                        ? ClientGameInk.Enemy : ClientGameInk.Checkpoint);
            }
        }
        var exitOpen = run.Started && run.RelayCount == stage.Relays.Length && (run.Stage != 2 || !run.Enemies[0].Active);
        Sprite(stage.Exit.X - 29, stage.Exit.Y - 39, 58, 78, ClientGameSprite.GoalBeacon);
        if (!exitOpen) Sprite(stage.Exit.X - 10, stage.Exit.Y - 10, 20, 20, ClientGameSprite.Shield);
        for (var i = 0; i < stage.Pickups.Length; i++)
            if (!run.Started || !run.Picked[i]) DrawPickup(stage.Pickups[i], 0, time);
        DrawOpsSideObjectives(run, stage);
        if (run.Started)
        {
            if (run.Stage == 2 && !run.Enemies[0].Active)
                Sprite(run.Enemies[0].X - 42, run.Enemies[0].Y - 42, 84, 84, ClientGameSprite.ReactorBroken);
            for (var i = 0; i < run.Enemies.Length; i++) if (run.Enemies[i].Active) DrawOpsEnemy(run.Enemies[i], run, stage, i);
            foreach (var shot in run.Shots)
            {
                if (!shot.Active) continue;
                var size = !shot.Hostile && shot.Damage > 1 ? 18 : 12;
                Sprite(shot.X - size * .5f, shot.Y - size * .5f, size, size,
                    shot.Hostile ? ClientGameSprite.BulletEnemy : ClientGameSprite.BulletPlayer, shot.VX < 0);
            }
        }
        else
        {
            foreach (var seed in stage.Enemies)
                DrawOpsEnemy(new Actor { X = seed.X, Y = seed.Y, Kind = seed.Kind, Health = seed.Health, MaxHealth = seed.Health, Timer = 1 }, run, stage);
        }
        var px = run.Started ? run.X : 54;
        var py = run.Started ? run.Y : 196;
        var moving = run.Started && (MoveX(run) != 0 || MoveY(run) != 0);
        var frame = run.Started && run.DashTime > 0 ? ClientGameSprite.OpsRoll
            : moving ? (int)(time * 10) % 2 == 0 ? ClientGameSprite.OpsRun1 : ClientGameSprite.OpsRun2 : ClientGameSprite.OpsIdle;
        Sprite(px - 16, py - 16, 32, 32, frame, run.Facing < 0);
        if (run.Started && run.Shield > 0) Sprite(px - 7, py - 31, 14, 14, ClientGameSprite.Shield);
        if (run.Started && run.Hurt > 0 && (int)(run.Hurt * 12) % 2 == 0)
            Sprite(px - 19, py - 19, 38, 38, ClientGameSprite.Impact);
        DrawOpsCombat(run, stage);
        DrawParticles(run, 0);
        DrawAbility(run);
    }

    private void DrawOpsEnemy(Actor enemy, Run run, OpsStage stage, int index = -1)
    {
        var x = enemy.X;
        var y = enemy.Y;
        var combat = index >= 0 ? _opsCombat.Enemies[index] : default;
        if (combat.Arrival > 0)
        {
            Sprite(x - 19, y - 19, 38, 38, ClientGameSprite.RelayOff);
            Shape(x - 18, y + 24, 36 * (1 - combat.Arrival / 1.25f), 4, ClientGameInk.Enemy);
            if ((int)(run.StageTime * 6) % 2 == 0) Sprite(x - 9, y - 9, 18, 18,
                enemy.Kind == EnemyKind.Charger ? ClientGameSprite.ChargerReady : ClientGameSprite.RangerIdle);
            return;
        }
        if (enemy.Kind == EnemyKind.Boss)
        {
            var shielded = run.RelayCount < stage.Relays.Length;
            var frame = shielded ? ClientGameSprite.ReactorShielded : enemy.Health <= 21 ? ClientGameSprite.ReactorAngry : ClientGameSprite.ReactorCore;
            Sprite(x - 42, y - 42, 84, 84, frame);
            if (enemy.Hurt > 0) Sprite(x - 28, y - 28, 56, 56, ClientGameSprite.Impact);
            Shape(x - 40, y - 46, 80, 6, ClientGameInk.Shadow);
            Shape(x - 40, y - 46, 80 * Math.Clamp((float)enemy.Health / Math.Max(1, enemy.MaxHealth), 0, 1), 6, ClientGameInk.Boss);
            if (combat.Exposed > 0)
            {
                Sprite(x - 13, y - 13, 26, 26, ClientGameSprite.Rapid);
                Shape(x - 30, y + 46, 60 * combat.Exposed / .65f, 4, ClientGameInk.Shot);
            }
            if (!shielded && enemy.Timer <= .6f)
            {
                var angle = MathF.Atan2(enemy.AimY, enemy.AimX);
                var pattern = OpsBossPattern(enemy.Health > 21 ? 0 : enemy.Health > 10 ? 1 : 2, combat.Volleys);
                if (pattern == 2)
                {
                    Sprite(x - 12, y - 18, 24, 24, ClientGameSprite.VentActive);
                }
                else if (pattern == 0)
                {
                    var count = enemy.Health > 21 ? 3 : 5;
                    for (var i = 0; i < count; i++)
                    {
                        var direction = angle + (i - (count - 1) * .5f) * .19f;
                        DrawOpsAimLane(x, y, MathF.Cos(direction), MathF.Sin(direction), 6, stage.Cover, ClientGameInk.EnemyShot);
                    }
                }
                else
                {
                    for (var i = 2; i <= 10; i++)
                    {
                        var direction = angle + i * MathF.Tau / 12;
                        DrawOpsAimLane(x, y, MathF.Cos(direction), MathF.Sin(direction), 3, stage.Cover, ClientGameInk.EnemyShot);
                    }
                    DrawOpsAimLane(x, y, enemy.AimX, enemy.AimY, 7, stage.Cover, ClientGameInk.Checkpoint);
                }
            }
            return;
        }
        var alternate = (int)(run.StageTime * 7 + enemy.Phase) % 2 != 0;
        var patrolMoving = run.Started && (!Near(run.X, run.Y, enemy.X, enemy.Y, 290)
            || !ClearSight(enemy.X, enemy.Y, run.X, run.Y, stage.Cover));
        var actorFrame = enemy.Kind switch
        {
            EnemyKind.Turret => ClientGameSprite.Turret,
            EnemyKind.Charger => enemy.State is 1 or 3 || !run.Started ? ClientGameSprite.ChargerReady : ClientGameSprite.ChargerRun,
            EnemyKind.Ranger => enemy.State == 1 ? ClientGameSprite.RangerFire : ClientGameSprite.RangerIdle,
            _ => enemy.State == 1 ? ClientGameSprite.PatrolFire
                : patrolMoving && alternate ? ClientGameSprite.PatrolRun : ClientGameSprite.PatrolIdle
        };
        Sprite(x - 19, y - 19, 38, 38, actorFrame, (enemy.AimX != 0 ? enemy.AimX : run.X - enemy.X) < 0);
        if (run.Started && index == _opsEncounters.Jammer)
        {
            Sprite(x - 9, y - 35, 18, 18, ClientGameSprite.RelayOff);
            Shape(x - 23, y + 23, 46, 3, ClientGameInk.Boss);
        }
        if (enemy.Hurt > 0) Sprite(x - 22, y - 22, 44, 44, ClientGameSprite.Impact);
        if (combat.Stun > 0 || enemy.Kind == EnemyKind.Charger && enemy.State == 3)
        {
            Shape(x - 10, y - 27, 5, 4, ClientGameInk.Cloud);
            Shape(x + 5, y - 27, 5, 4, ClientGameInk.Cloud);
        }
        else if (enemy.Kind == EnemyKind.Charger && enemy.State == 1)
        {
            DrawOpsAimLane(x, y, enemy.AimX, enemy.AimY, 6, stage.Cover, ClientGameInk.EnemyShot);
        }
        else if (enemy.State == 1)
            DrawOpsAimLane(x, y, enemy.AimX, enemy.AimY, 5, stage.Cover, ClientGameInk.EnemyShot);
        if (enemy.Health < enemy.MaxHealth)
        {
            Shape(x - 12, y - 19, 24, 3, ClientGameInk.Shadow);
            Shape(x - 12, y - 19, 24 * enemy.Health / enemy.MaxHealth, 3, ClientGameInk.Enemy);
        }
    }
}
