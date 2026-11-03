// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Globalization;

namespace Modstoke.Client;

internal enum ClientTabletGame { TideRunner, SignalBreach }
internal enum ClientGameInk
{
    Water, Ground, Grass, Player, Visor, Supply, Enemy, Shot, Beacon, Detail, Heal,
    Sky, Cloud, Rock, Hazard, Secret, Checkpoint, Cover, Terminal, EnemyShot, Boss, Shield, Dash, Shadow
}
internal enum ClientGameSprite
{
    None,
    RunnerIdle, RunnerRun1, RunnerRun2, RunnerJump, RunnerDash, RunnerFall, Crab1, Crab2,
    HopperIdle, HopperJump, Bird1, Bird2, CannonLeft, CannonRight, RunnerHit, Impact,
    OpsIdle, OpsRun1, OpsRun2, OpsRoll, PatrolIdle, PatrolRun, PatrolFire, ChargerReady,
    ChargerRun, RangerIdle, RangerFire, Turret, ReactorShielded, ReactorCore, ReactorAngry, ReactorBroken,
    Supply, Secret, Medkit, Shield, Rapid, CheckpointOff, CheckpointOn, GoalBeacon,
    RelayOff, RelayOn, Crate, CoverWall, SteelFloor, FloorGrate, BulletPlayer, BulletEnemy,
    GrassSoil, Soil, StoneLedge, MovingPlatform, CrumbleIntact, CrumbleCracked, SpringIdle, SpringCompressed,
    Spikes, VentIdle, VentActive, Water, PalmTree, Cliff, Cloud, Sun
}
internal readonly record struct ClientGameShape(float X, float Y, float Width, float Height, ClientGameInk Ink,
    ClientGameSprite Sprite = ClientGameSprite.None, bool FlipX = false, float TileSize = 0, float Opacity = 1f);

// Reused on the terminal's main thread; consume before the next Snapshot call.
internal sealed class ClientTabletGamesSnapshot
{
    internal bool Started, Paused, Finished, Won, Firing;
    internal ClientTabletGame Game;
    internal int Score, Best, Health, Collected, Seconds, Count, Stage, StageCount, Secrets, TotalSecrets, Combo;
    internal string Motion = "Stopped", StageName = "", Objective = "", Controls = "";
    internal string IslandSession = "New run", OpsSession = "New run";
    internal bool Keyboard, AirDashSpent;
    internal int ElapsedSeconds, Hits, PeakCombo, BestAtStart, StageProgress, StageTarget, DirectionX, DirectionY;
    internal float AbilityCooldown, PowerSeconds, ShieldSeconds;
    internal readonly ClientGameShape[] Shapes = new ClientGameShape[ClientTabletGames.ShapeCapacity];
}

// Local, fixed-step arcade simulations. No Unity objects, native input, network or world rewards.
// Level data and fixed actor buffers are allocated once; stepping and drawing create no objects.
internal sealed partial class ClientTabletGames
{
    internal const string ScoreDraftKey = "games.scores.v1";
    internal const float Width = 880f, Height = 390f;
    internal const int ShapeCapacity = 512;
    private const float Step = 1f / 60f;
    private readonly Run _island = new(), _ops = new();
    private readonly ClientTabletGamesSnapshot _snapshot = new();
    private ClientTabletGame _game;
    private bool _visible, _scoresDirty, _keyboardActive, _keyboardFire;
    private int _keyboardX, _keyboardY, _islandBest, _opsBest;
    private float _accumulator;
    private Run Current => _game == ClientTabletGame.TideRunner ? _island : _ops;

    private enum EnemyKind { Crab, Hopper, Bird, Cannon, Patrol, Ranger, Charger, Turret, Boss }
    private enum PickupKind { Supply, Secret, Heal, Rapid, Shield }
    private readonly record struct Pickup(PickupKind Kind, float X, float Y);
    private readonly record struct EnemySeed(EnemyKind Kind, float X, float Y, float Range = 64, int Health = 1);

    private sealed class Run
    {
        internal bool Started, Paused = true, Finished, Won, Grounded, Jump, Firing, DashRequest, AirDash;
        internal float X, Y, VX, VY, StageTime, Hurt, Shot, CheckpointX, CheckpointY;
        internal float DashTime, DashCooldown, DashX, DashY, Coyote, JumpBuffer, ComboTime, Power, Shield, RelayHold;
        internal int DX, DY, Facing = 1, Health, Score, Collected, StageCollected, Stage, Secrets, StageSecrets;
        internal int Jumps, Checkpoint, Combo = 1, Chain, RelayCount, RelayIndex = -1, Sequence, Support = -1;
        internal float Elapsed;
        internal int Hits, PeakCombo = 1, BestAtStart;
        internal readonly bool[] Picked = new bool[48];
        internal readonly bool[] Relays = new bool[4];
        internal readonly float[] Platforms = new float[40];
        internal readonly Actor[] Enemies = new Actor[24];
        internal readonly Projectile[] Shots = new Projectile[64];
        internal readonly Particle[] Particles = new Particle[32];
    }

    private struct Actor
    {
        internal bool Active;
        internal EnemyKind Kind;
        internal float X, Y, OriginX, OriginY, VY, Range, Timer, Phase, Hurt, AimX, AimY;
        internal int Health, MaxHealth, Facing, State;
    }

    private struct Projectile
    {
        internal bool Active, Hostile;
        internal float X, Y, VX, VY, Life;
        internal int Damage;
    }

    private struct Particle
    {
        internal bool Active;
        internal float X, Y, VX, VY, Life;
        internal ClientGameInk Ink;
    }

    internal void LoadScores(string? saved, bool resetRun = true)
    {
        if (resetRun)
        {
            _visible = false;
            _islandBest = _opsBest = 0;
            _scoresDirty = false;
            _island.Started = _ops.Started = false;
            _island.Finished = _ops.Finished = _island.Won = _ops.Won = false;
            _island.Stage = _ops.Stage = _island.StageCollected = _ops.StageCollected = 0;
            _island.Hurt = _ops.Hurt = _island.Power = _ops.Power = _island.Shield = _ops.Shield = 0;
            Array.Clear(_island.Platforms);
            Array.Clear(_island.Particles);
            Array.Clear(_ops.Particles);
            ResetOpsState();
            ResetIslandStage(_island);
            Pause(_island);
            Pause(_ops);
            ClearKeyboard();
            _accumulator = 0;
        }
        var (island, ops) = ReadScores(saved);
        _islandBest = Math.Max(_islandBest, island);
        _opsBest = Math.Max(_opsBest, ops);
    }

    internal static string MergeScores(string? saved, string? current)
    {
        var a = ReadScores(saved);
        var b = ReadScores(current);
        return string.Create(CultureInfo.InvariantCulture, $"1,{Math.Max(a.Island, b.Island)},{Math.Max(a.Ops, b.Ops)}");
    }

    private static (int Island, int Ops) ReadScores(string? saved)
    {
        if (string.IsNullOrEmpty(saved)) return (0, 0);
        var fields = saved.Split(',');
        if (fields.Length != 3 || fields[0] != "1") return (0, 0);
        if (int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var island)
            && int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var ops)
            && island is >= 0 and <= 100000 && ops is >= 0 and <= 100000)
            return (island, ops);
        return (0, 0);
    }

    internal bool TryTakeScores(out string scores)
    {
        scores = "";
        if (!_scoresDirty) return false;
        scores = string.Create(CultureInfo.InvariantCulture, $"1,{_islandBest},{_opsBest}");
        _scoresDirty = false;
        return true;
    }

    // Keyboard holds and touchscreen latches are independent. Losing keyboard ownership restores
    // the touchscreen state; it must not erase the latches on each pointer-driven frame.
    internal void SetKeyboard(int x, int y, bool jumpPressed, bool fireHeld, bool dashPressed, bool active)
    {
        _keyboardActive = active;
        _keyboardX = active ? Math.Clamp(x, -1, 1) : 0;
        _keyboardY = active ? Math.Clamp(y, -1, 1) : 0;
        _keyboardFire = active && fireHeld;
        var run = Current;
        if (!active || !run.Started || run.Paused || run.Finished) return;
        run.Jump |= jumpPressed;
        run.DashRequest |= dashPressed;
    }

    private void ClearKeyboard()
    { _keyboardActive = _keyboardFire = false; _keyboardX = _keyboardY = 0; }
    private int MoveX(Run run) => _keyboardActive ? _keyboardX : run.DX;
    private int MoveY(Run run) => _keyboardActive ? _keyboardY : run.DY;
    private bool IsFiring(Run run) => _keyboardActive ? _keyboardFire : run.Firing;

    internal void SetVisible(bool visible)
    {
        _visible = visible;
        if (visible) return;
        Pause(_island);
        Pause(_ops);
        ClearKeyboard();
        _accumulator = 0;
    }

    internal void Update(float dt, bool visible)
    {
        SetVisible(visible);
        if (!visible) return;
        var run = Current;
        if (!run.Started || run.Paused || run.Finished || !float.IsFinite(dt) || dt <= 0) return;
        _accumulator += Math.Min(dt, 1f / 15f);
        while (_accumulator >= Step)
        {
            _accumulator -= Step;
            run.StageTime += Step;
            run.Elapsed += Step;
            run.Hurt = Math.Max(0, run.Hurt - Step);
            run.DashCooldown = Math.Max(0, run.DashCooldown - Step);
            run.Shot = Math.Max(0, run.Shot - Step);
            run.Power = Math.Max(0, run.Power - Step);
            run.Shield = Math.Max(0, run.Shield - Step);
            run.ComboTime = Math.Max(0, run.ComboTime - Step);
            if (run.ComboTime <= 0) { run.Combo = 1; run.Chain = 0; }
            UpdateParticles(run);
            if (_game == ClientTabletGame.TideRunner) UpdateIsland(run);
            else UpdateOps(run);
            if (run.Finished || run.Paused) { _accumulator = 0; break; }
        }
    }

    internal void HandleCommand(string command)
    {
        if (!_visible) return;
        if (command is "island" or "ops")
        {
            Pause(Current);
            ClearKeyboard();
            _game = command == "island" ? ClientTabletGame.TideRunner : ClientTabletGame.SignalBreach;
            _accumulator = 0;
            return;
        }
        var run = Current;
        if (command == "restart") { Start(run); return; }
        if (command == "continue")
        {
            if (!run.Started) Start(run);
            else if (!run.Finished) { run.Paused = false; _accumulator = 0; }
            return;
        }
        if (command == "pause") { Pause(run); ClearKeyboard(); _accumulator = 0; return; }
        if (!run.Started || run.Paused || run.Finished) return;
        switch (command)
        {
            case "left": run.DX = -1; run.DY = 0; break;
            case "right": run.DX = 1; run.DY = 0; break;
            case "up" when _game == ClientTabletGame.SignalBreach: run.DX = 0; run.DY = -1; break;
            case "down" when _game == ClientTabletGame.SignalBreach: run.DX = 0; run.DY = 1; break;
            case "stop": run.DX = run.DY = 0; break;
            case "jump" when _game == ClientTabletGame.TideRunner: run.Jump = true; break;
            case "fire" when _game == ClientTabletGame.SignalBreach: run.Firing = !run.Firing; break;
            case "dash": run.DashRequest = true; break;
        }
    }

    private static void Pause(Run run)
    {
        run.Paused = true;
        run.DX = run.DY = 0;
        run.Jump = run.Firing = run.DashRequest = false;
        run.JumpBuffer = run.VX = run.DashTime = 0;
    }

    private void Start(Run run)
    {
        run.Started = true;
        run.Paused = run.Finished = run.Won = false;
        run.Score = run.Collected = run.Secrets = run.Stage = 0;
        run.Elapsed = 0;
        run.Hits = 0;
        run.PeakCombo = 1;
        run.BestAtStart = _game == ClientTabletGame.TideRunner ? _islandBest : _opsBest;
        run.Health = _game == ClientTabletGame.TideRunner ? 3 : 5;
        run.Firing = _game == ClientTabletGame.SignalBreach;
        run.Combo = 1;
        run.Chain = 0;
        run.ComboTime = 0;
        EnterStage(run);
        _accumulator = 0;
    }

    private void EnterStage(Run run)
    {
        run.VX = run.VY = run.StageTime = run.Shot = run.DashTime = run.DashCooldown = 0;
        run.Power = run.Shield = run.RelayHold = run.JumpBuffer = run.Coyote = 0;
        run.Hurt = 1.5f;
        run.DX = run.DY = run.StageCollected = run.StageSecrets = run.Checkpoint = run.RelayCount = run.Sequence = 0;
        run.Support = run.RelayIndex = -1;
        run.Facing = 1;
        run.Jumps = 0;
        run.Jump = run.DashRequest = run.AirDash = false;
        run.Grounded = _game == ClientTabletGame.TideRunner;
        Array.Clear(run.Picked);
        Array.Clear(run.Relays);
        Array.Clear(run.Platforms);
        Array.Clear(run.Enemies);
        Array.Clear(run.Shots);
        Array.Clear(run.Particles);
        if (_game == ClientTabletGame.TideRunner)
        {
            run.X = run.CheckpointX = 44;
            run.Y = run.CheckpointY = 302;
            LoadEnemies(run, IslandStages[run.Stage].Enemies);
            ResetIslandStage(run);
        }
        else
        {
            run.X = 54;
            run.Y = 196;
            LoadEnemies(run, OpsStages[run.Stage].Enemies);
            EnterOpsStage(run);
        }
    }

    private static void LoadEnemies(Run run, EnemySeed[] seeds)
    {
        for (var i = 0; i < seeds.Length; i++)
        {
            var seed = seeds[i];
            run.Enemies[i] = new Actor
            {
                Active = true, Kind = seed.Kind, X = seed.X, Y = seed.Y,
                OriginX = seed.X, OriginY = seed.Y, Range = seed.Range,
                Health = seed.Health, MaxHealth = seed.Health, Facing = i % 2 == 0 ? 1 : -1,
                Timer = 1.1f + i % 4 * .25f, Phase = i * .9f
            };
        }
    }

    private void ClearStage(Run run)
    {
        var limit = _game == ClientTabletGame.TideRunner ? 180 : OpsStages[run.Stage].TimeLimit;
        AddScore(run, 1000 + Math.Max(0, (int)(limit - run.StageTime)) * 12 + run.Health * 150);
        if (run.StageSecrets == (_game == ClientTabletGame.TideRunner ? 3 : 2)) AddScore(run, 1500);
        if (run.Stage == 2)
        {
            AddScore(run, 3000 + run.Secrets * 250);
            Finish(run, true);
            return;
        }
        run.Stage++;
        run.Health = Math.Min(_game == ClientTabletGame.TideRunner ? 3 : 5, run.Health + 1);
        EnterStage(run);
    }

    private void Hurt(Run run)
    {
        run.Health--;
        run.Hits++;
        run.Hurt = 1.3f;
        run.Combo = 1;
        run.Chain = 0;
        run.ComboTime = 0;
        Burst(run, run.X, run.Y, ClientGameInk.Enemy, 8);
        if (run.Health <= 0) Finish(run, false);
    }

    private static void Finish(Run run, bool won)
    { run.Finished = true; run.Won = won; Pause(run); }

    private void AddScore(Run run, int points)
    {
        run.Score = Math.Clamp(run.Score + points, 0, 100000);
        ref var best = ref (_game == ClientTabletGame.TideRunner ? ref _islandBest : ref _opsBest);
        if (run.Score <= best) return;
        best = run.Score;
        _scoresDirty = true;
    }

    private void ChainScore(Run run, int points)
    {
        run.Chain++;
        run.Combo = Math.Min(5, 1 + run.Chain / 3);
        run.PeakCombo = Math.Max(run.PeakCombo, run.Combo);
        run.ComboTime = 5;
        AddScore(run, points * run.Combo);
    }

    private void TakePickup(Run run, Pickup pickup)
    {
        switch (pickup.Kind)
        {
            case PickupKind.Supply: run.Collected++; run.StageCollected++; ChainScore(run, 100); break;
            case PickupKind.Secret: run.Secrets++; run.StageSecrets++; ChainScore(run, 600); break;
            case PickupKind.Heal: run.Health = Math.Min(_game == ClientTabletGame.TideRunner ? 3 : 5, run.Health + 1); AddScore(run, 100); break;
            case PickupKind.Rapid: run.Power = 12; AddScore(run, 150); break;
            case PickupKind.Shield: run.Shield = 10; AddScore(run, 150); break;
        }
        Burst(run, pickup.X, pickup.Y, PickupInk(pickup.Kind), 6);
    }

    private static ClientGameInk PickupInk(PickupKind kind) => kind switch
    {
        PickupKind.Secret => ClientGameInk.Secret, PickupKind.Heal => ClientGameInk.Heal,
        PickupKind.Rapid => ClientGameInk.Shot, PickupKind.Shield => ClientGameInk.Shield, _ => ClientGameInk.Supply
    };

    private static void Burst(Run run, float x, float y, ClientGameInk ink, int amount)
    {
        for (var i = 0; i < run.Particles.Length && amount > 0; i++)
        {
            if (run.Particles[i].Active) continue;
            var angle = amount * 2.39996f;
            run.Particles[i] = new Particle { Active = true, X = x, Y = y,
                VX = MathF.Cos(angle) * 65, VY = MathF.Sin(angle) * 65, Life = .45f, Ink = ink };
            amount--;
        }
    }

    private static void UpdateParticles(Run run)
    {
        for (var i = 0; i < run.Particles.Length; i++)
        {
            ref var p = ref run.Particles[i];
            if (!p.Active) continue;
            p.Life -= Step;
            p.X += p.VX * Step;
            p.Y += p.VY * Step;
            if (p.Life <= 0) p.Active = false;
        }
    }

    private static bool SpawnShot(Run run, float x, float y, float vx, float vy, bool hostile, int damage = 1)
    {
        for (var i = 0; i < run.Shots.Length; i++)
        {
            if (run.Shots[i].Active) continue;
            run.Shots[i] = new Projectile { Active = true, X = x, Y = y, VX = vx, VY = vy, Hostile = hostile, Damage = damage, Life = 3 };
            return true;
        }
        return false;
    }

    private static bool Overlap(float x, float y, float w, float h, float a, float b, float c, float d)
        => x < a + c && x + w > a && y < b + d && y + h > b;
    private static bool Near(float x, float y, float a, float b, float radius)
        => (x - a) * (x - a) + (y - b) * (y - b) < radius * radius;
    private static float Approach(float value, float target, float amount)
        => value < target ? Math.Min(target, value + amount) : Math.Max(target, value - amount);

    internal ClientTabletGamesSnapshot Snapshot
    {
        get
        {
            var run = Current;
            var state = _snapshot;
            var island = _game == ClientTabletGame.TideRunner;
            var stage = run.Started ? run.Stage : 0;
            state.Game = _game;
            state.Started = run.Started;
            state.Paused = run.Paused;
            state.Finished = run.Finished;
            state.Won = run.Won;
            state.Firing = run.Started && !run.Paused && IsFiring(run);
            state.Score = run.Started ? run.Score : 0;
            state.Health = run.Started ? run.Health : island ? 3 : 5;
            state.Collected = run.Started ? run.Collected : 0;
            state.Best = island ? _islandBest : _opsBest;
            state.Seconds = Math.Max(0, (int)Math.Ceiling((island ? 180 : OpsStages[stage].TimeLimit) - (run.Started ? run.StageTime : 0)));
            state.Stage = stage + 1;
            state.StageCount = 3;
            state.StageName = island ? IslandStages[stage].Name : OpsStages[stage].Name;
            state.Secrets = run.Started ? run.Secrets : 0;
            state.TotalSecrets = island ? 9 : 6;
            state.Combo = run.Started ? run.Combo : 1;
            state.Objective = island ? IslandObjective(run) : OpsObjective(run);
            state.Controls = island ? "A/D: move. Space: double jump. S/Down: dive. Shift: dash / return cannonballs."
                : "WASD/arrows: move. Hold Space: aim and fire. Shift: roll.";
            var dx = MoveX(run);
            var dy = MoveY(run);
            state.IslandSession = SessionLabel(_island);
            state.OpsSession = SessionLabel(_ops);
            state.Keyboard = _keyboardActive;
            state.DirectionX = dx;
            state.DirectionY = dy;
            state.ElapsedSeconds = run.Started ? (int)run.Elapsed : 0;
            state.Hits = run.Started ? run.Hits : 0;
            state.PeakCombo = run.Started ? run.PeakCombo : 1;
            state.BestAtStart = run.Started ? run.BestAtStart : state.Best;
            state.StageProgress = run.Started ? island ? run.StageCollected : run.RelayCount : 0;
            state.StageTarget = island ? 6 : OpsStages[stage].Relays.Length;
            state.AbilityCooldown = run.Started ? run.DashCooldown : 0;
            state.AirDashSpent = island && run.Started && run.AirDash && !run.Grounded;
            state.PowerSeconds = run.Started ? run.Power : 0;
            state.ShieldSeconds = run.Started ? run.Shield : 0;
            state.Motion = !run.Started || run.Paused || run.Finished ? "Stopped"
                : run.DashTime > 0 ? "Dashing" : dx < 0 ? "Left" : dx > 0 ? "Right" : dy < 0 ? "Up" : dy > 0 ? "Down" : "Stopped";
            state.Count = 0;
            if (island) DrawIsland(run);
            else DrawOps(run);
            return state;
        }
    }

    private static string SessionLabel(Run run) => !run.Started ? "New run"
        : run.Finished ? (run.Won ? "Completed" : "Ended") + $" · {run.Score:N0} pts"
        : $"{(run.Paused ? "Resume" : "Playing")} · {run.Stage + 1}/3 · {run.Score:N0} pts";

    private void Shape(float x, float y, float w, float h, ClientGameInk ink)
    {
        if (_snapshot.Count >= ShapeCapacity || w <= 0 || h <= 0 || x + w < 0 || x > Width || y + h < 0 || y > Height) return;
        _snapshot.Shapes[_snapshot.Count++] = new ClientGameShape(x, y, w, h, ink);
    }

    private void Sprite(float x, float y, float w, float h, ClientGameSprite sprite, bool flipX = false, float tileSize = 0, float opacity = 1f)
    {
        if (_snapshot.Count >= ShapeCapacity || w <= 0 || h <= 0 || x + w < 0 || x > Width || y + h < 0 || y > Height) return;
        _snapshot.Shapes[_snapshot.Count++] = new ClientGameShape(x, y, w, h, ClientGameInk.Detail, sprite, flipX, tileSize, Math.Clamp(opacity, 0f, 1f));
    }

    private void DrawParticles(Run run, float camera)
    {
        if (!run.Started) return;
        foreach (var particle in run.Particles)
            if (particle.Active) Shape(particle.X - camera, particle.Y, 4, 4, particle.Ink);
    }

    private void DrawPickup(Pickup pickup, float camera, float time)
    {
        var x = pickup.X - camera;
        var y = pickup.Y + MathF.Sin(time * 3 + pickup.X) * 3;
        var sprite = pickup.Kind switch
        {
            PickupKind.Secret => ClientGameSprite.Secret, PickupKind.Heal => ClientGameSprite.Medkit,
            PickupKind.Rapid => ClientGameSprite.Rapid, PickupKind.Shield => ClientGameSprite.Shield,
            _ => ClientGameSprite.Supply
        };
        Sprite(x - 13, y - 13, 26, 26, sprite);
    }

    private void DrawAbility(Run run)
    {
        if (!run.Started) return;
        Shape(14, Height - 16, 88, 5, ClientGameInk.Shadow);
        Shape(14, Height - 16, 88 * (1 - Math.Clamp(run.DashCooldown / 1.2f, 0, 1)), 5, ClientGameInk.Dash);
        if (run.Power > 0) Shape(112, Height - 16, run.Power / 12 * 88, 5, ClientGameInk.Shot);
        if (run.Shield > 0) Shape(210, Height - 16, run.Shield / 10 * 88, 5, ClientGameInk.Shield);
    }
}
