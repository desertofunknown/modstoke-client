// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Client;

internal sealed partial class ClientTabletGames
{
    private sealed class OpsCombatState
    {
        internal readonly OpsEnemyState[] Enemies = new OpsEnemyState[24];
        internal readonly OpsSalvage[] Salvage = new OpsSalvage[24];
        internal readonly float[] Relays = new float[4];
        internal int Focus = -1, DamageTaken, SalvageSpawned, SalvageRecovered, LastMedals, NoticeKind;
        internal float Counter, HackLock, Notice, EmpTime, EmpX, EmpY;
        internal bool RollActive, RollRewarded, StageAwarded;
    }

    private struct OpsEnemyState
    {
        internal float Stun, Arrival, Exposed, VX, VY;
        internal int Volleys;
    }

    private struct OpsSalvage
    {
        internal bool Active;
        internal float X, Y, Life;
        internal int Value;
    }

    private readonly OpsCombatState _opsCombat = new();

    private void ResetOpsState()
    {
        Array.Clear(_opsCombat.Enemies);
        Array.Clear(_opsCombat.Salvage);
        Array.Clear(_opsCombat.Relays);
        _opsCombat.Focus = -1;
        _opsCombat.DamageTaken = _opsCombat.SalvageSpawned = _opsCombat.SalvageRecovered = 0;
        _opsCombat.LastMedals = _opsCombat.NoticeKind = 0;
        _opsCombat.Counter = _opsCombat.HackLock = _opsCombat.Notice = _opsCombat.EmpTime = 0;
        _opsCombat.EmpX = _opsCombat.EmpY = 0;
        _opsCombat.RollActive = _opsCombat.RollRewarded = _opsCombat.StageAwarded = false;
        ResetOpsEncounters();
    }

    private void EnterOpsStage(Run run)
    {
        var medals = run.Stage > 0 ? _opsCombat.LastMedals : 0;
        ResetOpsState();
        EnterOpsEncounters(run);
        _opsCombat.LastMedals = medals;
        if (run.Stage > 0) OpsNotice(7, 3.5f);
    }

    private void OpsNotice(int kind, float duration = 1.8f)
    {
        _opsCombat.NoticeKind = kind;
        _opsCombat.Notice = duration;
    }

    private void UpdateOpsCombat(Run run, OpsStage stage)
    {
        _opsCombat.Counter = Math.Max(0, _opsCombat.Counter - Step);
        _opsCombat.HackLock = Math.Max(0, _opsCombat.HackLock - Step);
        _opsCombat.Notice = Math.Max(0, _opsCombat.Notice - Step);
        _opsCombat.EmpTime = Math.Max(0, _opsCombat.EmpTime - Step);
        _opsEncounters.Cascade = Math.Max(0, _opsEncounters.Cascade - Step);
        for (var i = 0; i < _opsCombat.Salvage.Length; i++)
        {
            ref var salvage = ref _opsCombat.Salvage[i];
            if (!salvage.Active) continue;
            salvage.Life -= Step;
            if (salvage.Life <= 0) { salvage.Active = false; continue; }
            if (!Near(run.X, run.Y, salvage.X, salvage.Y, 24)
                || !ClearSight(run.X, run.Y, salvage.X, salvage.Y, stage.Cover)) continue;
            salvage.Active = false;
            _opsCombat.SalvageRecovered++;
            ChainScore(run, salvage.Value + Math.Max(0, (int)(salvage.Life * 8)));
            run.DashCooldown = Math.Max(0, run.DashCooldown - .25f);
            Burst(run, salvage.X, salvage.Y, ClientGameInk.Secret, 5);
            OpsNotice(4);
        }
    }

    private bool OpsCanTarget(Run run, OpsStage stage, int index, float range, out float distance)
    {
        distance = float.MaxValue;
        if (index < 0 || index >= run.Enemies.Length) return false;
        var enemy = run.Enemies[index];
        if (!enemy.Active || _opsCombat.Enemies[index].Arrival > 0
            || enemy.Kind == EnemyKind.Boss && run.RelayCount < stage.Relays.Length) return false;
        var dx = enemy.X - run.X;
        var dy = enemy.Y - run.Y;
        distance = dx * dx + dy * dy;
        return distance < range * range && ClearSight(run.X, run.Y, enemy.X, enemy.Y, stage.Cover);
    }

    private void UpdateOpsFocus(Run run, OpsStage stage)
    {
        var retained = OpsCanTarget(run, stage, _opsCombat.Focus, 540, out var distance);
        var focus = retained ? _opsCombat.Focus : -1;
        var threshold = retained ? distance * .55f : 540 * 540;
        if (focus < 0) distance = 540 * 540;
        for (var i = 0; i < run.Enemies.Length; i++)
        {
            if (!OpsCanTarget(run, stage, i, 540, out var candidate)) continue;
            // Keep a moving target selected unless a substantially nearer threat enters sight.
            if (candidate >= threshold) continue;
            focus = i;
            distance = candidate;
            threshold = distance;
        }
        _opsCombat.Focus = focus;
    }

    private void HurtOps(Run run)
    {
        _opsCombat.DamageTaken++;
        _opsCombat.HackLock = .65f;
        _opsCombat.Counter = 0;
        if (run.RelayIndex >= 0 && !run.Relays[run.RelayIndex])
            _opsCombat.Relays[run.RelayIndex] = Math.Max(0, _opsCombat.Relays[run.RelayIndex] - .35f);
        OpsNotice(2);
        Hurt(run);
    }

    private void RewardOpsRoll(Run run)
    {
        if (_opsCombat.RollRewarded) return;
        _opsCombat.RollRewarded = true;
        _opsCombat.Counter = 6;
        run.DashCooldown = Math.Min(run.DashCooldown, .75f);
        AddScore(run, 75);
        Burst(run, run.X, run.Y, ClientGameInk.Dash, 8);
        OpsNotice(1, 2.3f);
    }

    private void StaggerOpsEnemy(Run run, OpsStage stage, int index, float vx, float vy)
    {
        ref var enemy = ref run.Enemies[index];
        ref var combat = ref _opsCombat.Enemies[index];
        if (enemy.Kind == EnemyKind.Boss) return;
        combat.Stun = Math.Max(combat.Stun, .16f);
        combat.VX = combat.VY = 0;
        if (enemy.Kind == EnemyKind.Charger)
        {
            if (enemy.State == 1) { enemy.State = 3; enemy.Timer = .8f; }
        }
        else
        {
            enemy.State = 0;
            enemy.Timer = Math.Max(enemy.Timer, .75f);
            if (enemy.Kind != EnemyKind.Turret)
            {
                var length = Math.Max(1, MathF.Sqrt(vx * vx + vy * vy));
                MoveOps(ref enemy.X, ref enemy.Y, vx / length * 6, vy / length * 6, 12, stage.Cover);
            }
        }
    }

    private void DropOpsSalvage(Actor enemy)
    {
        if (enemy.Kind == EnemyKind.Boss) return;
        for (var i = 0; i < _opsCombat.Salvage.Length; i++)
        {
            if (_opsCombat.Salvage[i].Active) continue;
            _opsCombat.Salvage[i] = new OpsSalvage
            {
                Active = true, X = enemy.X, Y = enemy.Y, Life = 10,
                Value = enemy.Kind == EnemyKind.Turret ? 150 : 80
            };
            _opsCombat.SalvageSpawned++;
            return;
        }
    }

    private void PulseOpsRelay(Run run, OpsStage stage, int relay)
    {
        var node = stage.Relays[relay];
        _opsCombat.EmpX = node.X;
        _opsCombat.EmpY = node.Y;
        _opsCombat.EmpTime = .6f;
        for (var i = 0; i < run.Shots.Length; i++)
            if (run.Shots[i].Hostile && Near(node.X, node.Y, run.Shots[i].X, run.Shots[i].Y, 175))
                run.Shots[i].Active = false;
        for (var i = 0; i < run.Enemies.Length; i++)
        {
            ref var enemy = ref run.Enemies[i];
            if (!enemy.Active || enemy.Kind == EnemyKind.Boss || !Near(node.X, node.Y, enemy.X, enemy.Y, 175)) continue;
            _opsCombat.Enemies[i].Stun = 1.2f;
            enemy.State = 0;
            enemy.Timer = Math.Max(enemy.Timer, 1.8f);
        }
        run.DashCooldown = 0;
        OpsNotice(3, 2.3f);
    }

    private void UpdateOpsRelays(Run run, OpsStage stage)
    {
        var relay = -1;
        for (var i = 0; i < stage.Relays.Length; i++)
        {
            if (run.Relays[i]) continue;
            var node = stage.Relays[i];
            var active = Near(run.X, run.Y, node.X, node.Y, 29) && !_opsCombat.RollActive;
            if (active)
            {
                relay = i;
                if (_opsCombat.HackLock <= 0 && !OpsRelayJammed(run, stage, i))
                {
                    _opsCombat.Relays[i] += Step * (_opsEncounters.Cascade > 0 ? 1.65f : 1);
                    if (run.Combo > 1) run.ComboTime = Math.Max(run.ComboTime, .15f);
                }
            }
            else _opsCombat.Relays[i] = Math.Max(0, _opsCombat.Relays[i] - Step * .12f);
            if (_opsCombat.Relays[i] < 1.1f) continue;
            run.Relays[i] = true;
            run.RelayCount++;
            ChainScore(run, 500);
            PulseOpsRelay(run, stage, i);
            CompleteOpsRelayLink(run, stage);
            Burst(run, node.X, node.Y, ClientGameInk.Checkpoint, 12);
            if (run.Stage == 2 && run.RelayCount == stage.Relays.Length)
            {
                run.Enemies[0].Timer = 2;
                run.Hurt = Math.Max(run.Hurt, .8f);
            }
        }
        run.RelayIndex = relay >= 0 && !run.Relays[relay] ? relay : -1;
        run.RelayHold = run.RelayIndex >= 0 ? _opsCombat.Relays[run.RelayIndex] : 0;
    }

    private void AwardOpsStage(Run run)
    {
        if (_opsCombat.StageAwarded) return;
        _opsCombat.StageAwarded = true;
        var clear = true;
        foreach (var enemy in run.Enemies) if (enemy.Active) { clear = false; break; }
        var medals = 0;
        if (_opsCombat.DamageTaken == 0) { medals |= 1; AddScore(run, 700); }
        if (clear) { medals |= 2; AddScore(run, 650); }
        if (_opsCombat.SalvageSpawned > 0 && _opsCombat.SalvageRecovered == _opsCombat.SalvageSpawned)
        { medals |= 4; AddScore(run, 400); }
        _opsCombat.LastMedals = medals;
    }

    private static string OpsMedalText(int medals) => medals switch
    {
        1 => "Sector bonus: no damage +700. Next uplink ahead.",
        2 => "Sector bonus: full clear +650. Next uplink ahead.",
        3 => "Sector bonuses: no damage +700 / full clear +650.",
        4 => "Sector bonus: all salvage +400. Next uplink ahead.",
        5 => "Sector bonuses: no damage +700 / all salvage +400.",
        6 => "Sector bonuses: full clear +650 / all salvage +400.",
        7 => "Perfect sector! No damage / full clear / all salvage +1750.",
        _ => "Sector clear. Bonus goals: no damage / full clear / all salvage."
    };

    private void DrawOpsCombat(Run run, OpsStage stage)
    {
        if (!run.Started) return;
        for (var i = 0; i < _opsCombat.Salvage.Length; i++)
        {
            var salvage = _opsCombat.Salvage[i];
            if (!salvage.Active) continue;
            if (salvage.Life > 2 || (int)(salvage.Life * 8) % 2 == 0)
                Sprite(salvage.X - 10, salvage.Y - 10, 20, 20, ClientGameSprite.Supply);
            Shape(salvage.X - 12, salvage.Y + 12, 24, 3, ClientGameInk.Shadow);
            Shape(salvage.X - 12, salvage.Y + 12, salvage.Life / 10 * 24, 3, ClientGameInk.Secret);
        }
        if (_opsCombat.EmpTime > 0)
        {
            var radius = 175 * (1 - _opsCombat.EmpTime / .6f);
            for (var i = 0; i < 16; i++)
            {
                var angle = i * MathF.Tau / 16;
                Shape(_opsCombat.EmpX + MathF.Cos(angle) * radius - 3,
                    _opsCombat.EmpY + MathF.Sin(angle) * radius - 3, 6, 6, ClientGameInk.Checkpoint);
            }
        }
        if (_opsCombat.Counter > 0)
        {
            Sprite(run.X + 12, run.Y - 31, 12, 12, ClientGameSprite.BulletPlayer);
            Shape(run.X - 12, run.Y + 22, 24 * _opsCombat.Counter / 6, 3, ClientGameInk.Dash);
        }
        if (OpsCanTarget(run, stage, _opsCombat.Focus, 540, out _))
        {
            var enemy = run.Enemies[_opsCombat.Focus];
            var radius = enemy.Kind == EnemyKind.Boss ? 45 : 23;
            var ink = _opsCombat.Counter > 0 ? ClientGameInk.Dash : ClientGameInk.Shot;
            Shape(enemy.X - radius, enemy.Y - radius, 8, 2, ink);
            Shape(enemy.X + radius - 8, enemy.Y - radius, 8, 2, ink);
            Shape(enemy.X - radius, enemy.Y + radius, 8, 2, ink);
            Shape(enemy.X + radius - 8, enemy.Y + radius, 8, 2, ink);
        }
        if ((_opsCombat.Notice > 0 && _opsCombat.NoticeKind == 7) || (run.Finished && run.Won))
        {
            var medals = _opsCombat.LastMedals;
            if ((medals & 1) != 0) Sprite(402, 12, 20, 20, ClientGameSprite.Shield);
            if ((medals & 2) != 0) Sprite(430, 12, 20, 20, ClientGameSprite.BulletPlayer);
            if ((medals & 4) != 0) Sprite(458, 12, 20, 20, ClientGameSprite.Supply);
        }
    }

    private void DrawOpsAimLane(float x, float y, float aimX, float aimY, int dots, Cover[] cover, ClientGameInk ink)
    {
        var oldX = x;
        var oldY = y;
        for (var i = 1; i <= dots; i++)
        {
            var px = x + aimX * i * 25;
            var py = y + aimY * i * 25;
            if (!ClearSight(oldX, oldY, px, py, cover)) break;
            Shape(px - 2, py - 2, 4, 4, ink);
            oldX = px;
            oldY = py;
        }
    }
}
