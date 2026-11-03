// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Client;

internal sealed partial class ClientTabletGames
{
    private readonly record struct TidePoint(float X, float Y);
    private readonly record struct TideWind(float X, float Y, float Width, float Height, float Push, float Lift, float Phase);
    private sealed record TideCourse(TideWind[] Winds, TidePoint[] Caches, TidePoint[] Rings, TidePoint Rune, float SwiftTime);

    private static readonly TideCourse[] TideCourses =
    {
        new(new TideWind[] { new(315,115,200,170,125,1250,0), new(1740,130,215,180,110,1160,2) },
            new TidePoint[] { new(350,200), new(2185,194) },
            new TidePoint[] { new(1110,222), new(1220,158), new(1370,105), new(1495,195), new(1578,225) },
            new(738,330), 85),
        new(new TideWind[] { new(780,145,185,165,115,1200,1), new(1700,140,210,170,125,1220,3) },
            new TidePoint[] { new(575,197), new(2180,143) },
            new TidePoint[] { new(500,220), new(610,158), new(720,99), new(795,207), new(930,211) },
            new(710,330), 90),
        new(new TideWind[] { new(795,130,190,180,110,1200,2), new(2100,130,215,180,130,1260,0) },
            new TidePoint[] { new(480,154), new(2545,148) },
            new TidePoint[] { new(1790,221), new(1940,167), new(2062,109), new(2145,212), new(2338,226) },
            new(757,330), 95)
    };

    private const float TideRingLimit = 10, TideRuneLimit = 12;
    private const int TidePaceSamples = 721;
    private readonly bool[] _tideCaches = new bool[2];
    private readonly int[] _tideMedals = new int[3], _tideSessionMedals = new int[3];
    private readonly float[] _tidePaceX = new float[TidePaceSamples], _tidePaceY = new float[TidePaceSamples];
    private readonly float[] _tideBestPaceX = new float[TidePaceSamples * 3], _tideBestPaceY = new float[TidePaceSamples * 3];
    private readonly float[] _tideBestTimes = new float[3];
    private readonly int[] _tideBestPaceCounts = new int[3];
    private int _tideRingProgress, _tideStageHits, _tideStageFalls, _tidePaceCount;
    private float _tideRingTime, _tideRuneTime, _tideRuneCharge;
    private bool _tideRingsWon, _tideRuneLatched, _tideDiving, _tideDiveHeld, _tideWindHint;
    private string _tideArrival = "", _tideFinish = "", _tideMedalHelp = "";

    private void ResetTideAdventure(Run run)
    {
        Array.Clear(_tideCaches);
        _tideRingProgress = _tideStageHits = _tideStageFalls = _tidePaceCount = 0;
        _tideRingTime = _tideRuneTime = _tideRuneCharge = 0;
        _tideRingsWon = _tideRuneLatched = _tideDiving = _tideDiveHeld = _tideWindHint = false;
        if (!run.Started)
        {
            Array.Clear(_tideSessionMedals);
            Array.Clear(_tideBestTimes);
            Array.Clear(_tideBestPaceCounts);
            _tideArrival = "";
        }
        if (run.Stage == 0)
        {
            Array.Clear(_tideMedals);
            _tideFinish = "";
            _tideArrival = "";
        }
        if (!run.Started) return;
        _tideMedalHelp = $"Medals: Clean = no hits/falls. Trail = both crates + five rings. Swift = clear within {TideCourses[run.Stage].SwiftTime:0}s.";
        TideNotice(_tideArrival.Length > 0 ? _tideArrival
            : _tideBestTimes[run.Stage] > 0 ? "Faint runner = your session-best pace. S/Down in midair: dive."
            : "Explore upper routes. S/Down in midair: dive; dash or dive to open treasure crates.");
        _tideArrival = "";
    }

    private void BeginTideDive(Run run)
    {
        var held = MoveY(run) > 0;
        if (held && !_tideDiveHeld && !run.Grounded && run.DashTime <= 0)
        {
            _tideDiving = true;
            run.VY = 580;
            run.Coyote = run.JumpBuffer = 0;
            Burst(run, run.X + 10, run.Y, ClientGameInk.Dash, 4);
        }
        _tideDiveHeld = held;
    }

    private void LandTideDive(Run run)
    {
        _tideDiving = false;
        Burst(run, run.X + 10, run.Y + 28, ClientGameInk.Dash, 8);
        var caches = TideCourses[run.Stage].Caches;
        for (var i = 0; i < caches.Length; i++)
            if (!_tideCaches[i] && Math.Abs(caches[i].X - run.X - 10) < 36
                && Math.Abs(caches[i].Y - run.Y - 28) < 8) OpenTideCache(run, i);
    }

    private static float TideWindStrength(TideWind wind, float time)
    {
        var phase = (time + wind.Phase) % 6;
        return phase < 4 ? MathF.Sin(phase * MathF.PI / 4) : 0;
    }

    private void ApplyTideWind(Run run)
    {
        if (run.Grounded || _tideDiving) return;
        foreach (var wind in TideCourses[run.Stage].Winds)
        {
            if (!Overlap(run.X, run.Y, 20, 28, wind.X, wind.Y, wind.Width, wind.Height)) continue;
            var strength = TideWindStrength(wind, run.StageTime);
            // Environmental carry is separate from the player's horizontal acceleration/braking.
            run.X += wind.Push * strength * Step;
            run.VY = Math.Max(-430, run.VY - wind.Lift * strength * Step);
            if (_tideWindHint || strength < .5f) continue;
            _tideWindHint = true;
            TideNotice("Updraft! Bright ribbons lift and carry you; dive to leave the current.");
        }
    }

    private void UpdateTideAdventureTimers()
    {
        _tideRuneTime = Math.Max(0, _tideRuneTime - Step);
        if (_tideRingProgress <= 0 || _tideRingsWon) return;
        _tideRingTime = Math.Max(0, _tideRingTime - Step);
        if (_tideRingTime > 0) return;
        ResetTideRingAttempt();
        TideNotice("Sky trail timed out. Return to its first bright ring to retry.");
    }

    private void ResetTideRingAttempt()
    {
        if (_tideRingsWon) return;
        _tideRingProgress = 0;
        _tideRingTime = 0;
    }

    private void UpdateTideAdventureObjects(Run run, float oldBottom)
    {
        var course = TideCourses[run.Stage];
        var onRune = run.Grounded && Math.Abs(run.X + 10 - course.Rune.X) < 23
            && Math.Abs(run.Y + 28 - course.Rune.Y) < 3;
        if (!onRune)
        {
            _tideRuneCharge = 0;
            _tideRuneLatched = false;
        }
        else if (!_tideRuneLatched && _tideRuneTime <= 0)
        {
            _tideRuneCharge += Step;
            if (_tideRuneCharge >= .55f)
            {
                _tideRuneTime = TideRuneLimit;
                _tideRuneLatched = true;
                _tideRuneCharge = 0;
                run.DashCooldown = 0;
                TideNotice("Rune bridge open for 12 seconds! Follow the violet ledges upward.");
                Burst(run, course.Rune.X, course.Rune.Y - 4, ClientGameInk.Secret, 8);
            }
        }
        for (var i = 0; i < course.Caches.Length; i++)
        {
            var cache = course.Caches[i];
            if (_tideCaches[i] || !Overlap(run.X, run.Y, 20, 28, cache.X - 14, cache.Y - 28, 28, 28)) continue;
            if (run.DashTime <= 0 && !(_tideDiving && oldBottom <= cache.Y - 20)) continue;
            OpenTideCache(run, i);
            if (!_tideDiving) continue;
            run.VY = -330;
            run.Jumps = 1;
            run.AirDash = run.Grounded = _tideDiving = false;
            run.Support = -1;
        }
        if (_tideRingsWon) return;
        var ring = course.Rings[_tideRingProgress];
        if (!Overlap(run.X, run.Y, 20, 28, ring.X - 14, ring.Y - 14, 28, 28)) return;
        if (_tideRingProgress == 0)
        {
            _tideRingTime = TideRingLimit;
            TideNotice("Sky trail started! Five rings in order, ten seconds. Follow the bright ring.");
        }
        _tideRingProgress++;
        Burst(run, ring.X, ring.Y, ClientGameInk.Secret, 4);
        if (_tideRingProgress < course.Rings.Length) return;
        _tideRingsWon = true;
        AddScore(run, 950);
        run.DashCooldown = 0;
        run.Shield = Math.Max(run.Shield, 4);
        TideNotice("Sky trail complete! +950, four-second shield, dash ready. Trail medal progress saved.");
    }

    private void OpenTideCache(Run run, int index)
    {
        _tideCaches[index] = true;
        var cache = TideCourses[run.Stage].Caches[index];
        AddScore(run, 450);
        run.DashCooldown = 0;
        if (index == 0)
        {
            run.Health = Math.Min(3, run.Health + 1);
            TideNotice("Treasure opened! +450, one heart if needed, dash ready.");
        }
        else
        {
            run.Shield = Math.Max(run.Shield, 3);
            TideNotice("Treasure opened! +450, three-second shield, dash ready.");
        }
        Burst(run, cache.X, cache.Y - 14, ClientGameInk.Secret, 10);
    }

    private static void UpdateTideBird(Run run, ref Actor bird)
    {
        if (bird.State == 0)
        {
            bird.X = Approach(bird.X, bird.OriginX + MathF.Sin(run.StageTime * 1.4f + bird.Phase) * bird.Range, 150 * Step);
            bird.Y = Approach(bird.Y, bird.OriginY + MathF.Sin(run.StageTime * 2.8f + bird.Phase) * 29, 100 * Step);
            if (bird.Timer > 0 || Math.Abs(run.X + 10 - bird.X) > 185 || run.Y + 14 < bird.Y) return;
            bird.State = 1;
            bird.Timer = .65f;
            bird.AimX = Math.Clamp(run.X + 10, bird.OriginX - bird.Range - 65, bird.OriginX + bird.Range + 65);
            bird.AimY = Math.Clamp(run.Y + 14, bird.Y + 24, 310);
            return;
        }
        if (bird.State == 1)
        {
            if (bird.Timer > 0) return;
            bird.State = 2;
            bird.Timer = .9f;
        }
        if (bird.State == 2)
        {
            var x = bird.AimX - bird.X;
            var y = bird.AimY - bird.Y;
            var length = MathF.Sqrt(x * x + y * y);
            if (length > 8 && bird.Timer > 0)
            {
                var travel = Math.Min(length, 330 * Step);
                bird.X += x / length * travel;
                bird.Y += y / length * travel;
                return;
            }
            bird.State = 3;
            bird.Timer = 1.2f;
        }
        bird.X = Approach(bird.X, bird.OriginX, 120 * Step);
        bird.Y = Approach(bird.Y, bird.OriginY, 150 * Step);
        if (bird.Timer <= 0) { bird.State = 0; bird.Timer = 1.6f; }
    }

    private static void UpdateTideGuardian(Run run, ref Actor guardian)
    {
        if (guardian.State == 0)
        {
            guardian.Facing = run.X + 10 < guardian.X ? -1 : 1;
            if (guardian.Timer > 0 || Math.Abs(run.X - guardian.X) > 440) return;
            guardian.State = 1;
            guardian.Timer = .9f;
        }
        else if (guardian.State == 1 && guardian.Timer <= 0)
        {
            for (var lane = -1; lane <= 1; lane++)
                SpawnShot(run, guardian.X + guardian.Facing * 20, guardian.Y - 9, guardian.Facing * 210, lane * 65, true);
            guardian.State = 2;
            guardian.Timer = 2;
        }
        else if (guardian.State == 2 && guardian.Timer <= 0)
        {
            guardian.State = 0;
            guardian.Timer = .8f;
        }
    }

    private static bool TideGuardianAlive(Run run)
    {
        if (!run.Started || run.Stage != 2) return false;
        foreach (var enemy in run.Enemies)
            if (enemy.Active && enemy.Kind == EnemyKind.Cannon && enemy.MaxHealth >= 6) return true;
        return false;
    }

    private static int TideHitDamage(Actor enemy, int damage)
        => enemy.Kind == EnemyKind.Cannon && enemy.MaxHealth >= 6 && enemy.State == 2 ? damage * 2 : damage;

    private void DefeatTideEnemy(Run run, ref Actor enemy)
    {
        enemy.Active = false;
        var guardian = enemy.Kind == EnemyKind.Cannon && enemy.MaxHealth >= 6;
        ChainScore(run, guardian ? 1400 : enemy.Kind == EnemyKind.Cannon ? 250 : 150);
        if (!guardian) return;
        // The encounter is over; its outstanding volley must not punish the victory approach.
        for (var i = 0; i < run.Shots.Length; i++)
            if (run.Shots[i].Hostile && Math.Abs(run.Shots[i].X - enemy.X) < 600) run.Shots[i].Active = false;
        TideNotice(run.StageCollected >= 6
            ? "Lighthouse guardian defeated! Its volley is cleared; the rescue beacon is open."
            : "Lighthouse guardian defeated! Collect six supplies to unlock the rescue beacon.");
        Burst(run, enemy.X, enemy.Y, ClientGameInk.Checkpoint, 16);
    }

    private void RecordTidePace(Run run)
    {
        var sample = Math.Clamp((int)(run.StageTime * 4), 0, TidePaceSamples - 1);
        while (_tidePaceCount <= sample)
        {
            _tidePaceX[_tidePaceCount] = run.X;
            _tidePaceY[_tidePaceCount] = run.Y;
            _tidePaceCount++;
        }
    }

    private void ClearIslandStage(Run run)
    {
        var medals = (_tideStageHits == 0 && _tideStageFalls == 0 ? 1 : 0)
            | (_tideCaches[0] && _tideCaches[1] && _tideRingsWon ? 2 : 0)
            | (run.StageTime <= TideCourses[run.Stage].SwiftTime ? 4 : 0);
        _tideMedals[run.Stage] = medals;
        _tideSessionMedals[run.Stage] |= medals;
        AddScore(run, ((medals & 1) != 0 ? 500 : 0) + ((medals & 2) != 0 ? 750 : 0) + ((medals & 4) != 0 ? 650 : 0));
        var faster = _tideBestTimes[run.Stage] <= 0 || run.StageTime < _tideBestTimes[run.Stage];
        if (faster)
        {
            _tideBestTimes[run.Stage] = run.StageTime;
            _tideBestPaceCounts[run.Stage] = _tidePaceCount;
            Array.Copy(_tidePaceX, 0, _tideBestPaceX, run.Stage * TidePaceSamples, _tidePaceCount);
            Array.Copy(_tidePaceY, 0, _tideBestPaceY, run.Stage * TidePaceSamples, _tidePaceCount);
        }
        var earned = medals == 0 ? "No medals" : $"{((medals & 1) != 0 ? "Clean +500  " : "")}{((medals & 2) != 0 ? "Trail +750  " : "")}{((medals & 4) != 0 ? "Swift +650" : "")}";
        _tideArrival = $"Island cleared: {earned.Trim()}.{(faster ? " Session pace ghost saved." : "")}";
        _tideFinish = $"Rescue secured! Medals {TideMedalCount(_tideMedals)}/9; session {TideMedalCount(_tideSessionMedals)}/9. Restart to race your ghost.";
        ClearStage(run);
    }

    private static int TideMedalCount(int[] medals)
    {
        var count = 0;
        foreach (var value in medals)
            for (var bit = 0; bit < 3; bit++) if ((value & (1 << bit)) != 0) count++;
        return count;
    }

    private string TideFinishSummary() => _tideFinish;

    private string? TideAdventureHint(Run run)
    {
        if (run.StageTime < 9) return _tideMedalHelp;
        var course = TideCourses[run.Stage];
        if (_tideRuneTime <= 0 && Math.Abs(run.X + 10 - course.Rune.X) < 75 && run.Y > 260)
            return "Stand on the violet rune to raise a temporary bridge to the upper route.";
        for (var i = 0; i < course.Caches.Length; i++)
            if (!_tideCaches[i] && Near(run.X + 10, run.Y + 14, course.Caches[i].X, course.Caches[i].Y - 14, 65))
                return "Sealed treasure: dash through it or press S/Down above it to dive and break it.";
        if (!_tideRingsWon && _tideRingProgress == 0
            && Near(run.X + 10, run.Y + 14, course.Rings[0].X, course.Rings[0].Y, 100))
            return "Optional sky trail: touch the bright ring, then follow all five before ten seconds expire.";
        return null;
    }

    private void DrawTideWind(Run run, float camera, float time)
    {
        foreach (var wind in TideCourses[run.Started ? run.Stage : 0].Winds)
        {
            var strength = TideWindStrength(wind, time);
            var ink = strength > .3f ? ClientGameInk.Dash : ClientGameInk.Cloud;
            for (var i = 0; i < 4; i++)
            {
                var x = wind.X + 18 + i * (wind.Width - 40) / 4 - camera;
                var y = wind.Y + wind.Height - (time * 52 + i * 39) % wind.Height;
                Shape(x, y, 20 + strength * 12, 2, ink);
                Shape(x + 25, y - 5, 2, 7, ink);
            }
        }
    }

    private void DrawTideAdventure(Run run, float camera, float time)
    {
        var course = TideCourses[run.Started ? run.Stage : 0];
        var rune = course.Rune;
        Shape(rune.X - 22 - camera, rune.Y - 5, 44, 5, ClientGameInk.Secret);
        Sprite(rune.X - 9 - camera, rune.Y - 25, 18, 18, ClientGameSprite.Secret);
        if (run.Started && _tideRuneCharge > 0)
            Shape(rune.X - 22 - camera, rune.Y - 31, 44 * Math.Min(1, _tideRuneCharge / .55f), 4, ClientGameInk.Secret);
        for (var i = 0; i < course.Caches.Length; i++)
        {
            var cache = course.Caches[i];
            if (run.Started && _tideCaches[i])
            {
                Shape(cache.X - 13 - camera, cache.Y - 5, 26, 5, ClientGameInk.Ground);
                continue;
            }
            Sprite(cache.X - 16 - camera, cache.Y - 31, 32, 32, ClientGameSprite.Crate);
            Sprite(cache.X - 7 - camera, cache.Y - 25, 14, 14, ClientGameSprite.Secret);
            Shape(cache.X - 12 - camera, cache.Y - 3, 24, 3, ClientGameInk.Dash);
        }
        for (var i = 0; i < course.Rings.Length; i++)
        {
            if (run.Started && (_tideRingsWon || i < _tideRingProgress)) continue;
            var ring = course.Rings[i];
            var next = i == (run.Started ? _tideRingProgress : 0);
            var ink = next ? ClientGameInk.Secret : ClientGameInk.Shadow;
            var x = ring.X - camera;
            Shape(x - 10, ring.Y - 14, 20, 3, ink);
            Shape(x - 13, ring.Y - 10, 3, 20, ink);
            Shape(x + 10, ring.Y - 10, 3, 20, ink);
            Shape(x - 10, ring.Y + 11, 20, 3, ink);
            if (next) Shape(x - 2, ring.Y - 23 - MathF.Sin(time * 5) * 2, 4, 5, ClientGameInk.Secret);
        }
        if (!run.Started) return;
        DrawTidePace(run, camera);
    }

    private void DrawTideAdventureHud(Run run)
    {
        var course = TideCourses[run.Stage];
        if (_tideRuneTime > 0)
        {
            Shape(320, Height - 16, 120, 5, ClientGameInk.Shadow);
            Shape(320, Height - 16, 120 * _tideRuneTime / TideRuneLimit, 5,
                _tideRuneTime < 2 ? ClientGameInk.Hazard : ClientGameInk.Secret);
        }
        if (_tideRingProgress > 0)
        {
            Shape(460, Height - 16, 120, 5, ClientGameInk.Shadow);
            Shape(460, Height - 16, _tideRingsWon ? 120 : 120 * _tideRingTime / TideRingLimit, 5,
                _tideRingsWon ? ClientGameInk.Checkpoint : ClientGameInk.Secret);
        }
        for (var i = 0; i < _tideCaches.Length; i++)
            Sprite(600 + i * 23, Height - 29, 18, 18, _tideCaches[i] ? ClientGameSprite.Secret : ClientGameSprite.Crate);
        var available = (_tideStageHits == 0 && _tideStageFalls == 0 ? 1 : 0)
            | (_tideCaches[0] && _tideCaches[1] && _tideRingsWon ? 2 : 0)
            | (run.StageTime <= course.SwiftTime ? 4 : 0);
        for (var medal = 0; medal < 3; medal++)
        {
            var x = 270 + medal * 28;
            Shape(x, 28, 22, 22, ClientGameInk.Shadow);
            if ((available & (1 << medal)) != 0)
                Sprite(x + 2, 30, 18, 18, medal == 0 ? ClientGameSprite.Shield : medal == 1 ? ClientGameSprite.Secret : ClientGameSprite.Rapid);
            if ((_tideSessionMedals[run.Stage] & (1 << medal)) != 0) Shape(x + 5, 52, 12, 3, ClientGameInk.Checkpoint);
        }
    }

    private void DrawTidePace(Run run, float camera)
    {
        var count = _tideBestPaceCounts[run.Stage];
        if (count == 0 || run.StageTime > _tideBestTimes[run.Stage]) return;
        var sample = run.StageTime * 4;
        var index = Math.Min((int)sample, count - 1);
        var next = Math.Min(index + 1, count - 1);
        var fraction = sample - MathF.Floor(sample);
        var offset = run.Stage * TidePaceSamples;
        var dx = _tideBestPaceX[offset + next] - _tideBestPaceX[offset + index];
        var dy = _tideBestPaceY[offset + next] - _tideBestPaceY[offset + index];
        // Checkpoint teleports are discontinuities, not a sprint through the level.
        if (Math.Abs(dx) > 160 || Math.Abs(dy) > 160) fraction = 0;
        var x = _tideBestPaceX[offset + index] + dx * fraction;
        var y = _tideBestPaceY[offset + index] + dy * fraction;
        var frame = dy < -2 ? ClientGameSprite.RunnerJump : dy > 2 ? ClientGameSprite.RunnerFall
            : Math.Abs(dx) < 1 ? ClientGameSprite.RunnerIdle
            : (int)(run.StageTime * 11) % 2 == 0 ? ClientGameSprite.RunnerRun1 : ClientGameSprite.RunnerRun2;
        Sprite(x - camera - 5, y - 5, 30, 38, frame, dx < 0, opacity: .32f);
        Shape(14 + (Width - 28) * x / IslandStages[run.Stage].Length, 11, 3, 10, ClientGameInk.Secret);
    }

    private void DrawTideBirdWarning(Actor bird, float camera)
    {
        if (bird.State == 3)
        {
            Shape(bird.X - camera - 8, bird.Y - 28, 16, 3, ClientGameInk.Heal);
            return;
        }
        if (bird.State != 1) return;
        for (var i = 1; i <= 5; i++)
            Shape(bird.X + (bird.AimX - bird.X) * i / 5 - camera - 2,
                bird.Y + (bird.AimY - bird.Y) * i / 5 - 2, 4, 4, ClientGameInk.Hazard);
        Shape(bird.X - camera - 16, bird.Y - 31, 32, 3, ClientGameInk.Shadow);
        Shape(bird.X - camera - 16, bird.Y - 31, 32 * Math.Clamp(1 - bird.Timer / .65f, 0, 1), 3, ClientGameInk.Hazard);
        Shape(bird.AimX - camera - 12, bird.AimY - 1, 24, 2, ClientGameInk.Hazard);
    }

    private void DrawTideGuardian(Actor guardian, float camera)
    {
        var x = guardian.X - camera;
        Sprite(x - 12, guardian.Y - 49, 24, 24, ClientGameSprite.Shield);
        Shape(x - 32, guardian.Y - 59, 64, 5, ClientGameInk.Shadow);
        Shape(x - 32, guardian.Y - 59, 64f * Math.Max(0, guardian.Health) / guardian.MaxHealth, 5, ClientGameInk.Enemy);
        if (guardian.State == 2)
        {
            Shape(x - 23, guardian.Y + 6, 46 * Math.Clamp(guardian.Timer / 2, 0, 1), 4, ClientGameInk.Heal);
            Sprite(x - 6, guardian.Y - 19, 12, 12, ClientGameSprite.Rapid);
        }
        if (guardian.State != 1) return;
        var muzzle = x + guardian.Facing * 20;
        for (var lane = -1; lane <= 1; lane++)
            for (var mark = 1; mark <= 4; mark++)
                Shape(muzzle + guardian.Facing * mark * 25 - 2, guardian.Y - 9 + lane * mark * (65f / 210f) * 25 - 1,
                    5, 3, ClientGameInk.Hazard);
        Shape(x - 24, guardian.Y - 28, 48 * Math.Clamp(1 - guardian.Timer / .9f, 0, 1), 4, ClientGameInk.Hazard);
    }
}
