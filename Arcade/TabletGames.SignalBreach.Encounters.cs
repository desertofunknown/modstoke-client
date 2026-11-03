// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Client;

internal sealed partial class ClientTabletGames
{
    private readonly record struct OpsSector(Flag MineA, Flag MineB, Flag Vault, Flag BlackBox,
        Flag GuardA, Flag GuardB, int Jammer);

    private static readonly OpsSector[] OpsSectors =
    {
        new(new(305,315), new(628,194), new(104,130), new(804,349), new(720,350), new(806,40), 2),
        new(new(280,315), new(698,73), new(360,360), new(102,195), new(100,40), new(285,345), 2),
        new(new(460,350), new(704,305), new(459,350), new(462,195), new(460,350), new(704,73), 3)
    };

    private static readonly Cover[] OpsConveyors = { new(85,169,240,48), new(510,169,165,48) };
    private static readonly Cover[] OpsSurges = { new(264,173,40,80), new(576,152,28,86) };

    private sealed class OpsEncounterState
    {
        internal readonly OpsMine[] Mines = new OpsMine[2];
        internal readonly OpsMortar[] Mortars = new OpsMortar[3];
        internal readonly bool[] RepairUsed = new bool[4];
        internal int Jammer = -1, RepairIndex = -1;
        internal float RepairHold, VaultHold, BlackBoxArm, BlackBoxHold, Cascade;
        internal bool VaultRevealed, VaultClaimed, BlackBoxStarted, BlackBoxClaimed;
    }

    private struct OpsMine
    {
        internal bool Spent;
        internal float Fuse;
    }

    private struct OpsMortar
    {
        internal float X, Y, Fuse, Flash;
    }

    private readonly OpsEncounterState _opsEncounters = new();

    private void ResetOpsEncounters()
    {
        Array.Clear(_opsEncounters.Mines);
        Array.Clear(_opsEncounters.Mortars);
        Array.Clear(_opsEncounters.RepairUsed);
        _opsEncounters.Jammer = _opsEncounters.RepairIndex = -1;
        _opsEncounters.RepairHold = _opsEncounters.VaultHold = _opsEncounters.BlackBoxArm = 0;
        _opsEncounters.BlackBoxHold = _opsEncounters.Cascade = 0;
        _opsEncounters.VaultRevealed = _opsEncounters.VaultClaimed = false;
        _opsEncounters.BlackBoxStarted = _opsEncounters.BlackBoxClaimed = false;
    }

    private void EnterOpsEncounters(Run run) => _opsEncounters.Jammer = OpsSectors[run.Stage].Jammer;

    private static Flag OpsMinePosition(OpsSector sector, int index) => index == 0 ? sector.MineA : sector.MineB;

    private static int OpsConveyorDirection(Run run, int index)
        => (index == 0 ? 1 : -1) * (run.Started && run.Relays[index] ? -1 : 1);

    private void MoveOpsConveyor(Run run, OpsStage stage)
    {
        if (run.Stage != 0 || _opsCombat.RollActive) return;
        for (var i = 0; i < OpsConveyors.Length; i++)
        {
            var belt = OpsConveyors[i];
            if (!Overlap(run.X - 4, run.Y - 4, 8, 8, belt.X, belt.Y, belt.Width, belt.Height)) continue;
            MoveOps(ref run.X, ref run.Y, OpsConveyorDirection(run, i) * 38 * Step, 0, 11, stage.Cover);
        }
    }

    private static float OpsSurgePhase(Run run, int index) => (run.StageTime + index * 2.5f) % 6;

    private void UpdateOpsTerrain(Run run, OpsStage stage)
    {
        if (run.Stage == 1 && run.Hurt <= 0 && run.Shield <= 0 && !_opsCombat.RollActive)
        {
            for (var i = 0; i < OpsSurges.Length; i++)
            {
                var surge = OpsSurges[i];
                if (run.Relays[i] || OpsSurgePhase(run, i) < 4.8f
                    || !Overlap(run.X - 10, run.Y - 10, 20, 20, surge.X, surge.Y, surge.Width, surge.Height)) continue;
                HurtOps(run);
                break;
            }
            if (run.Finished) return;
        }
        var sector = OpsSectors[run.Stage];
        for (var i = 0; i < _opsEncounters.Mines.Length; i++)
        {
            ref var mine = ref _opsEncounters.Mines[i];
            if (mine.Spent) continue;
            var position = OpsMinePosition(sector, i);
            var visible = ClearSight(position.X, position.Y, run.X, run.Y, stage.Cover);
            if (_opsCombat.RollActive && visible && Near(run.X, run.Y, position.X, position.Y, 32))
            {
                mine.Spent = true;
                AddScore(run, 125);
                Burst(run, position.X, position.Y, ClientGameInk.Checkpoint, 6);
                OpsNotice(15);
                continue;
            }
            if (mine.Fuse <= 0)
            {
                if (!visible || !Near(run.X, run.Y, position.X, position.Y, 60)) continue;
                mine.Fuse = .9f;
            }
            mine.Fuse -= Step;
            if (mine.Fuse > 0) continue;
            mine.Spent = true;
            // Mines belong to the facility: their blast can also be baited into guards.
            for (var j = 0; j < run.Enemies.Length; j++)
            {
                var enemy = run.Enemies[j];
                if (!enemy.Active || !Near(position.X, position.Y, enemy.X, enemy.Y, 70)
                    || !ClearSight(position.X, position.Y, enemy.X, enemy.Y, stage.Cover)) continue;
                DamageOpsEnemy(run, stage, j, 2, enemy.X - position.X, enemy.Y - position.Y);
            }
            BlastOpsPlayer(run, stage, position.X, position.Y, 58);
            if (run.Finished) return;
        }
    }

    private void UpdateOpsMortars(Run run, OpsStage stage)
    {
        for (var i = 0; i < _opsEncounters.Mortars.Length; i++)
        {
            ref var mortar = ref _opsEncounters.Mortars[i];
            mortar.Flash = Math.Max(0, mortar.Flash - Step);
            if (mortar.Fuse <= 0) continue;
            mortar.Fuse -= Step;
            if (mortar.Fuse > 0) continue;
            mortar.Flash = .2f;
            BlastOpsPlayer(run, stage, mortar.X, mortar.Y, 34);
            if (run.Finished) return;
        }
    }

    private void BlastOpsPlayer(Run run, OpsStage stage, float x, float y, float radius)
    {
        Burst(run, x, y, ClientGameInk.Hazard, 8);
        if (run.Hurt > 0 || run.Shield > 0 || _opsCombat.RollActive
            || !Near(run.X, run.Y, x, y, radius + 10) || !ClearSight(x, y, run.X, run.Y, stage.Cover)) return;
        HurtOps(run);
    }

    private bool OpsRelayJammed(Run run, OpsStage stage, int relay)
    {
        var index = _opsEncounters.Jammer;
        if (index < 0 || !run.Enemies[index].Active) return false;
        var jammer = run.Enemies[index];
        var node = stage.Relays[relay];
        return Near(jammer.X, jammer.Y, node.X, node.Y, 225);
    }

    private void CompleteOpsRelayLink(Run run, OpsStage stage)
    {
        var linked = _opsEncounters.Cascade > 0;
        if (linked) AddScore(run, 450);
        _opsEncounters.Cascade = run.RelayCount < stage.Relays.Length ? 14 : 0;
        if (linked) OpsNotice(19, 2.8f);
        else if (_opsEncounters.Cascade > 0) OpsNotice(18, 3.2f);
    }

    private void UpdateOpsSideObjectives(Run run, OpsStage stage)
    {
        UpdateOpsRepair(run, stage);
        var sector = OpsSectors[run.Stage];
        if (!_opsEncounters.VaultRevealed && run.StageSecrets == 2)
        {
            _opsEncounters.VaultRevealed = true;
            Burst(run, sector.Vault.X, sector.Vault.Y, ClientGameInk.Secret, 10);
            OpsNotice(11, 3.2f);
        }
        var canUse = !_opsCombat.RollActive && _opsCombat.HackLock <= 0;
        if (_opsEncounters.VaultRevealed && !_opsEncounters.VaultClaimed)
        {
            if (canUse && Near(run.X, run.Y, sector.Vault.X, sector.Vault.Y, 26))
                _opsEncounters.VaultHold += Step;
            else _opsEncounters.VaultHold = Math.Max(0, _opsEncounters.VaultHold - Step * .5f);
            if (_opsEncounters.VaultHold >= 1.5f)
            {
                _opsEncounters.VaultClaimed = true;
                AddScore(run, 900);
                run.Health = Math.Min(5, run.Health + 1);
                run.Power = Math.Max(run.Power, 12);
                Burst(run, sector.Vault.X, sector.Vault.Y, ClientGameInk.Secret, 12);
                OpsNotice(12, 3);
            }
        }
        if (_opsEncounters.BlackBoxClaimed) return;
        var atBox = canUse && Near(run.X, run.Y, sector.BlackBox.X, sector.BlackBox.Y, 25);
        if (!_opsEncounters.BlackBoxStarted)
        {
            _opsEncounters.BlackBoxArm = atBox ? _opsEncounters.BlackBoxArm + Step : 0;
            if (_opsEncounters.BlackBoxArm < .75f) return;
            _opsEncounters.BlackBoxStarted = true;
            AddBossGuard(run, sector.GuardA.X, sector.GuardA.Y, EnemyKind.Ranger);
            AddBossGuard(run, sector.GuardB.X, sector.GuardB.Y, EnemyKind.Charger);
            OpsNotice(13, 3.2f);
        }
        if (atBox) _opsEncounters.BlackBoxHold += Step;
        if (_opsEncounters.BlackBoxHold < 4.5f) return;
        _opsEncounters.BlackBoxClaimed = true;
        AddScore(run, 1000);
        run.Shield = Math.Max(run.Shield, 10);
        Burst(run, sector.BlackBox.X, sector.BlackBox.Y, ClientGameInk.Secret, 12);
        OpsNotice(14, 3.2f);
    }

    private void UpdateOpsRepair(Run run, OpsStage stage)
    {
        var station = -1;
        if (run.Health < 5 && !_opsCombat.RollActive && _opsCombat.HackLock <= 0)
        {
            for (var i = 0; i < stage.Relays.Length; i++)
                if (run.Relays[i] && !_opsEncounters.RepairUsed[i]
                    && Near(run.X, run.Y, stage.Relays[i].X, stage.Relays[i].Y, 29)) { station = i; break; }
        }
        if (station < 0 || station != _opsEncounters.RepairIndex) _opsEncounters.RepairHold = 0;
        _opsEncounters.RepairIndex = station;
        if (station < 0) return;
        _opsEncounters.RepairHold += Step;
        if (_opsEncounters.RepairHold < 2) return;
        _opsEncounters.RepairUsed[station] = true;
        _opsEncounters.RepairIndex = -1;
        _opsEncounters.RepairHold = 0;
        run.Health = Math.Min(5, run.Health + 1);
        Burst(run, run.X, run.Y, ClientGameInk.Heal, 8);
        OpsNotice(10, 2.6f);
    }

    private bool CrashOpsCharger(Run run, OpsStage stage, int index)
    {
        var charger = run.Enemies[index];
        for (var i = 0; i < run.Enemies.Length; i++)
        {
            var other = run.Enemies[i];
            if (i == index || !other.Active || other.Kind == EnemyKind.Boss || _opsCombat.Enemies[i].Arrival > 0
                || !Near(charger.X, charger.Y, other.X, other.Y, 25)
                || !ClearSight(charger.X, charger.Y, other.X, other.Y, stage.Cover)) continue;
            DamageOpsEnemy(run, stage, i, 2, charger.AimX, charger.AimY);
            Burst(run, charger.X, charger.Y, ClientGameInk.Cloud, 8);
            OpsNotice(16);
            return true;
        }
        return false;
    }

    private void DamageOpsEnemy(Run run, OpsStage stage, int index, int damage, float vx, float vy)
    {
        ref var enemy = ref run.Enemies[index];
        if (!enemy.Active || _opsCombat.Enemies[index].Arrival > 0
            || enemy.Kind == EnemyKind.Boss && run.RelayCount < stage.Relays.Length) return;
        enemy.Health -= damage;
        enemy.Hurt = .1f;
        StaggerOpsEnemy(run, stage, index, vx, vy);
        if (enemy.Health > 0) return;
        enemy.Active = false;
        run.Collected++;
        ChainScore(run, enemy.Kind == EnemyKind.Boss ? 2500 : enemy.Kind == EnemyKind.Turret ? 300 : 180);
        DropOpsSalvage(enemy);
        Burst(run, enemy.X, enemy.Y, ClientGameInk.Enemy, 8);
        if (_opsEncounters.Jammer == index)
        {
            // Guard slots can be reused for reinforcements; the jammer designation cannot.
            _opsEncounters.Jammer = -1;
            OpsNotice(9, 2.6f);
        }
        if (enemy.Kind != EnemyKind.Boss) return;
        for (var i = 0; i < run.Shots.Length; i++) if (run.Shots[i].Hostile) run.Shots[i].Active = false;
        Array.Clear(_opsEncounters.Mortars);
        run.Hurt = 1.5f;
    }

    private static int OpsBossPattern(int phase, int volleys) => volleys % (phase == 0 ? 2 : 3);

    private void LaunchOpsMortars(Run run)
    {
        for (var i = 0; i < _opsEncounters.Mortars.Length; i++)
            _opsEncounters.Mortars[i] = new OpsMortar
            {
                X = Math.Clamp(run.X + (i - 1) * 60, 28, Width - 28),
                Y = Math.Clamp(run.Y, 28, Height - 28), Fuse = 1.15f
            };
        OpsNotice(17, 2.3f);
    }

    private string OpsEncounterObjective(Run run)
    {
        var stage = OpsStages[run.Stage];
        if (run.RelayIndex >= 0 && OpsRelayJammed(run, stage, run.RelayIndex))
            return "Download jammed! Defeat the officer marked by a relay badge and purple bar.";
        if (_opsEncounters.RepairIndex >= 0)
            return "Repairing armor. Hold this completed relay for two seconds; each relay repairs once.";
        var sector = OpsSectors[run.Stage];
        if (_opsEncounters.VaultRevealed && !_opsEncounters.VaultClaimed
            && Near(run.X, run.Y, sector.Vault.X, sector.Vault.Y, 42))
            return "Secret vault unlocked. Hold the gold crate to decrypt: score, armor and rapid fire.";
        if (!_opsEncounters.BlackBoxClaimed && Near(run.X, run.Y, sector.BlackBox.X, sector.BlackBox.Y, 44))
            return _opsEncounters.BlackBoxStarted
                ? "Black-box upload: hold the gold terminal. Leave to dodge; progress stays. Bonus +1000/shield."
                : "Optional black box: hold here to trigger guards, then upload for +1000 and a shield.";
        if (_opsCombat.Notice > 0) return "";
        if (run.StageTime < 6) return run.Stage switch
        {
            0 => "Freight belts carry you. Hacking each relay reverses its belt. Roll through blinking mines.",
            1 => "Blackout lanes flash before surging. Rolls cross safely; the first two relays shut them off.",
            _ => "The reactor gains aimed mortars after its first phase. Leave marked squares before impact."
        };
        if (_opsEncounters.Cascade > 0)
            return "Cascade window! The next relay hacks faster for 14 seconds; linking it earns +450.";
        return "";
    }

    private void DrawOpsTerrain(Run run)
    {
        var sectorIndex = run.Started ? run.Stage : 0;
        if (sectorIndex == 0)
        {
            for (var i = 0; i < OpsConveyors.Length; i++)
            {
                var belt = OpsConveyors[i];
                Sprite(belt.X, belt.Y, belt.Width, belt.Height, ClientGameSprite.FloorGrate, tileSize: 32);
                var direction = OpsConveyorDirection(run, i);
                var travel = run.Started ? run.StageTime * 38 % 45 : 0;
                for (var j = 0; j < 3; j++)
                {
                    var x = belt.X + 24 + j * (belt.Width - 48) / 3 + direction * (travel - 22) * .45f;
                    var y = belt.Y + belt.Height * .5f;
                    Shape(x - direction * 4 - 2, y - 6, 4, 4, ClientGameInk.Checkpoint);
                    Shape(x - 2, y - 2, 4, 4, ClientGameInk.Checkpoint);
                    Shape(x - direction * 4 - 2, y + 2, 4, 4, ClientGameInk.Checkpoint);
                }
            }
        }
        if (!run.Started) return;
        if (sectorIndex == 1)
        {
            for (var i = 0; i < OpsSurges.Length; i++)
            {
                var zone = OpsSurges[i];
                var phase = OpsSurgePhase(run, i);
                var active = !run.Relays[i] && phase >= 4.8f;
                var warning = !run.Relays[i] && phase >= 3.4f;
                var ink = run.Relays[i] ? ClientGameInk.Checkpoint : active ? ClientGameInk.EnemyShot
                    : warning ? ClientGameInk.Secret : ClientGameInk.Shadow;
                DrawOpsFrame(zone.X, zone.Y, zone.Width, zone.Height, ink);
                if (active || warning && (int)(run.StageTime * 8) % 2 == 0)
                    Sprite(zone.X + zone.Width * .5f - 10, zone.Y + zone.Height * .5f - 10, 20, 20,
                        active ? ClientGameSprite.VentActive : ClientGameSprite.VentIdle);
            }
        }
        var sector = OpsSectors[sectorIndex];
        for (var i = 0; i < _opsEncounters.Mines.Length; i++)
        {
            var mine = _opsEncounters.Mines[i];
            if (mine.Spent) continue;
            var position = OpsMinePosition(sector, i);
            Sprite(position.X - 11, position.Y - 11, 22, 22,
                mine.Fuse > 0 ? ClientGameSprite.VentActive : ClientGameSprite.VentIdle);
            if (mine.Fuse <= 0) continue;
            DrawOpsFrame(position.X - 58, position.Y - 58, 116, 116, ClientGameInk.Hazard);
            Shape(position.X - 18, position.Y + 16, 36 * mine.Fuse / .9f, 4, ClientGameInk.EnemyShot);
        }
        foreach (var mortar in _opsEncounters.Mortars)
        {
            if (mortar.Fuse <= 0 && mortar.Flash <= 0) continue;
            DrawOpsFrame(mortar.X - 34, mortar.Y - 34, 68, 68,
                mortar.Fuse > 0 ? ClientGameInk.EnemyShot : ClientGameInk.Hazard);
            Sprite(mortar.X - 10, mortar.Y - 10, 20, 20,
                mortar.Fuse > 0 ? ClientGameSprite.VentIdle : ClientGameSprite.Impact);
            if (mortar.Fuse > 0)
                Shape(mortar.X - 26, mortar.Y + 26, 52 * mortar.Fuse / 1.15f, 4, ClientGameInk.EnemyShot);
        }
    }

    private void DrawOpsSideObjectives(Run run, OpsStage stage)
    {
        if (!run.Started) return;
        for (var i = 0; i < stage.Relays.Length; i++)
        {
            var node = stage.Relays[i];
            if (!run.Relays[i] && OpsRelayJammed(run, stage, i))
            {
                Sprite(node.X - 8, node.Y - 38, 16, 16, ClientGameSprite.RelayOff);
                Shape(node.X - 19, node.Y + 30, 38, 3, ClientGameInk.Boss);
            }
            if (!run.Relays[i] || _opsEncounters.RepairUsed[i]) continue;
            Sprite(node.X - 8, node.Y - 39, 16, 16, ClientGameSprite.Medkit);
            if (_opsEncounters.RepairIndex != i) continue;
            Shape(node.X - 20, node.Y + 31, 40, 4, ClientGameInk.Shadow);
            Shape(node.X - 20, node.Y + 31, 40 * _opsEncounters.RepairHold / 2, 4, ClientGameInk.Heal);
        }
        var sector = OpsSectors[run.Stage];
        if (_opsEncounters.VaultRevealed && !_opsEncounters.VaultClaimed)
        {
            Sprite(sector.Vault.X - 18, sector.Vault.Y - 18, 36, 36, ClientGameSprite.Crate);
            Sprite(sector.Vault.X - 7, sector.Vault.Y - 7, 14, 14, ClientGameSprite.Secret);
            DrawOpsProgress(sector.Vault, _opsEncounters.VaultHold / 1.5f, ClientGameInk.Secret);
        }
        if (!_opsEncounters.BlackBoxClaimed)
        {
            Sprite(sector.BlackBox.X - 17, sector.BlackBox.Y - 17, 34, 34,
                _opsEncounters.BlackBoxStarted ? ClientGameSprite.RelayOn : ClientGameSprite.RelayOff);
            Sprite(sector.BlackBox.X - 6, sector.BlackBox.Y - 7, 12, 12, ClientGameSprite.Secret);
            DrawOpsProgress(sector.BlackBox, _opsEncounters.BlackBoxStarted
                ? _opsEncounters.BlackBoxHold / 4.5f : _opsEncounters.BlackBoxArm / .75f, ClientGameInk.Secret);
        }
        if (_opsEncounters.Cascade <= 0) return;
        Sprite(752, 12, 16, 16, ClientGameSprite.RelayOn);
        Shape(774, 18, 84, 5, ClientGameInk.Shadow);
        Shape(774, 18, 84 * _opsEncounters.Cascade / 14, 5, ClientGameInk.Checkpoint);
    }

    private void DrawOpsProgress(Flag position, float progress, ClientGameInk ink)
    {
        Shape(position.X - 20, position.Y + 21, 40, 4, ClientGameInk.Shadow);
        Shape(position.X - 20, position.Y + 21, 40 * Math.Clamp(progress, 0, 1), 4, ink);
    }

    private void DrawOpsFrame(float x, float y, float width, float height, ClientGameInk ink)
    {
        Shape(x, y, width, 2, ink);
        Shape(x, y + height - 2, width, 2, ink);
        Shape(x, y, 2, height, ink);
        Shape(x + width - 2, y, 2, height, ink);
    }
}
