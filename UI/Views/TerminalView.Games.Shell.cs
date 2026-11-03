// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Modstoke.Client;

internal sealed partial class ClientTerminalView
{
    private ButtonWidget _gamesRestart = null!;
    private TextMeshProUGUI _gamesProgressText = null!, _gamesElapsed = null!;
    private Image _gamesProgressBar = null!, _gamesManual = null!;
    private TextMeshProUGUI _gamesManualTitle = null!, _gamesManualBody = null!;
    private bool _gamesHasRun, _gamesRunFinished, _gamesRestartPending, _gamesManualOpen;
    private ClientTabletGame _gamesSelected;
    private float _gamesRestartUntil;
    private int _gamesManualPage;
    private ScrollArea _gamesObjectiveScroll = null!;
    private string _gamesControlsText = "";

    private void SetArcadeObjective(string value)
    {
        if (_gamesObjective.text == value) return;
        _gamesObjective.text = value;
        var height = TextHeight(_gamesObjective, _gamesObjectiveScroll.Width, 84);
        Place(_gamesObjective.rectTransform, 0, 0, _gamesObjectiveScroll.Width, height);
        _gamesObjectiveScroll.Height(height);
        _gamesObjectiveScroll.Reset();
    }

    private void BuildArcadeManual()
    {
        _gamesManual = RoundedPanel(_gamesBoard.transform, "FieldManual",
            new Color(.035f, .065f, .085f, .99f), 48, 18, 784, 354);
        _gamesManualTitle = GameText(_gamesManual.transform, "Title", "", 25, Color.white, 28, 16, 728, 36, true);
        _gamesManualBody = GameText(_gamesManual.transform, "Instructions", "", 21,
            new Color(.82f, .91f, .91f), 28, 62, 728, 222);
        Button(_gamesManual.transform, "Controls", "Controls", 28, 294, 162, 44, () => _gamesManualPage = 0);
        Button(_gamesManual.transform, "FieldGuide", "Field guide", 202, 294, 162, 44, () => _gamesManualPage = 1);
        Button(_gamesManual.transform, "Scoring", "Scoring", 376, 294, 162, 44, () => _gamesManualPage = 2);
        Button(_gamesManual.transform, "CloseManual", "Back to game", 550, 294, 206, 44, () => _gamesManualOpen = false);
        _gamesManual.gameObject.SetActive(false);
    }

    private void OpenArcadeManual()
    {
        if (!ArcadeCanAct) return;
        _command("games", "pause");
        _gamesRestartPending = false;
        _gamesManualOpen = true;
        _gamesManualPage = 0;
    }

    private void RefreshArcadeShell(ClientTabletGamesSnapshot state)
    {
        var island = state.Game == ClientTabletGame.TideRunner;
        var active = state.Started && !state.Paused && !state.Finished;
        if (_gamesSelected != state.Game || active)
        { _gamesRestartPending = false; _gamesManualOpen = false; }
        _gamesSelected = state.Game;
        _gamesHasRun = state.Started;
        _gamesRunFinished = state.Finished;
        if (Time.unscaledTime >= _gamesRestartUntil) _gamesRestartPending = false;
        _gamesRestart.Label.text = _gamesRestartPending ? "Confirm restart" : "Restart run";
        _gamesIsland.Label.text = "Tide Runner\n" + state.IslandSession;
        _gamesOps.Label.text = "Signal Breach\n" + state.OpsSession;
        _gamesIsland.Label.fontSize = _gamesOps.Label.fontSize = 19;
        _gamesElapsed.text = $"RUN TIME  {state.ElapsedSeconds / 60}:{state.ElapsedSeconds % 60:00}  ·  HITS TAKEN  {state.Hits}";
        var progress = Math.Clamp(state.StageProgress, 0, state.StageTarget);
        _gamesProgressText.text = island ? $"SUPPLY REQUIREMENT  {progress} / {state.StageTarget}"
            : $"RELAYS ONLINE  {progress} / {state.StageTarget}";
        // This is the collection requirement; a guardian or reactor may still guard the exit.
        Place(_gamesProgressBar.rectTransform, 24, 234, 376f * progress / Math.Max(1, state.StageTarget), 3);

        var ability = island ? "DASH" : "ROLL";
        var ready = !state.Started ? "Start a run" : state.AbilityCooldown > .05f ? $"{state.AbilityCooldown:0.0}s"
            : state.AirDashSpent ? "Land to recharge" : "READY";
        var power = state.PowerSeconds > 0 ? $"Rapid fire {Math.Ceiling(state.PowerSeconds):0}s" : "Rapid fire —";
        var shield = state.ShieldSeconds > 0 ? $"Shield {Math.Ceiling(state.ShieldSeconds):0}s" : "Shield —";
        _gamesHelp.text = $"{ability}  {ready}\n{shield}" + (island ? "" : $"  ·  {power}");
        _gamesHelp.color = state.Started && state.AbilityCooldown <= .05f && !state.AirDashSpent ? Mint : Muted;
        _gamesBest.text = state.Started && state.Score > state.BestAtStart
            ? $"NEW PERSONAL BEST  +{state.Score - state.BestAtStart:N0}"
            : state.Started && state.BestAtStart > 0 ? $"BEST {state.Best:N0}  ·  {Math.Max(0, state.BestAtStart - state.Score):N0} TO MATCH"
            : $"PERSONAL BEST  {state.Best:N0}";
        _gamesBest.color = state.Started && state.Score > state.BestAtStart ? Mint : Muted;

        if (active) _gamesStatus.text = state.Keyboard ? "KEYBOARD  /  P PAUSES" : "TOUCH  /  STOP TO HALT";
        _gamesTouchHint.text = state.Keyboard ? "Keyboard active · touch to switch" : "Touch arrows latch; Stop halts.";
        for (var i = 0; i < 3; i++)
        {
            var selected = active && (i == 0 ? state.DirectionX < 0 : i == 1 ? state.DirectionX > 0
                : state.DirectionX == 0 && state.DirectionY == 0);
            _gamesMovement[i].Image.color = selected ? AppColor(ClientTerminalApp.Games) : Raised;
            _gamesMovement[i].Label.color = selected ? Color.white : Ink;
        }
        _gamesUp.Image.color = active && state.DirectionY < 0 ? AppColor(ClientTerminalApp.Games) : Raised;
        _gamesDown.Image.color = active && state.DirectionY > 0 ? AppColor(ClientTerminalApp.Games) : Raised;
        _gamesUp.Label.color = active && state.DirectionY < 0 ? Color.white : Ink;
        _gamesDown.Label.color = active && state.DirectionY > 0 ? Color.white : Ink;
        _gamesDash.Label.text = state.AbilityCooldown > .05f ? $"{(island ? "Dash" : "Roll")} {state.AbilityCooldown:0.0}s"
            : state.AirDashSpent ? "Land to dash" : island ? "Dash / Shift" : "Roll / Shift";

        if (state.Finished)
            _gamesOverlayHint.text = $"Score {state.Score:N0}  ·  Peak combo x{state.PeakCombo}\n"
                + $"Time {state.ElapsedSeconds / 60}:{state.ElapsedSeconds % 60:00}  ·  Hits {state.Hits}\n"
                + $"Secrets {state.Secrets}/{state.TotalSecrets}  ·  Supplies {state.Collected}";
        if (_gamesRestartPending)
        {
            _gamesOverlayTitle.text = "Restart this run?";
            _gamesOverlayHint.text = $"Stage {state.Stage}/3 · {state.Score:N0} points\nThis run will be replaced. Personal bests stay saved.";
            _gamesOverlayAction.text = "Choose Restart again, or Continue to keep playing";
        }
        _gamesManual.gameObject.SetActive(_gamesManualOpen);
        if (_gamesManualOpen) RefreshArcadeManual(island);
    }

    private void RefreshArcadeManual(bool island)
    {
        _gamesManualTitle.text = (island ? "TIDE RUNNER" : "SIGNAL BREACH") + "  /  "
            + (_gamesManualPage == 0 ? "CONTROLS" : _gamesManualPage == 1 ? "FIELD GUIDE" : "SCORING");
        _gamesManualBody.text = _gamesManualPage switch
        {
            0 when island => "A/D or arrows · Move left and right\nSpace · Jump; press again in the air for a second jump\n"
                + "S/Down · Dive toward the ground\nShift · Dash through danger or return a cannonball\n"
                + "P · Pause   /   Enter · Start or resume\nTouch arrows keep moving until you select Stop.",
            0 => "WASD or arrows · Move\nHold Space · Auto-aim and fire at nearby enemies\nShift · Roll through danger\n"
                + "P · Pause   /   Enter · Start or resume\nTouch Fire toggles continuous fire; Stop halts touch movement.\nKeyboard aiming follows the current target automatically.",
            1 when island => "Collect six supplies to meet each island's exit requirement.\nLand on enemies from above; watch their attack warnings.\n"
                + "Cyan checkpoints provide a safe place to recover.\nPurple caches hide secrets away from the main route.\n"
                + "Land between air dashes and watch the recharge indicator.\nTry the optional upper paths before reaching the beacon.",
            1 => "Stand on cyan relays to download; stay alert while holding.\nUse solid cover to break hostile firing lines.\n"
                + "Watch for charged attacks before approaching enemies.\nCollect health, shields and rapid-fire power-ups.\n"
                + "The reactor shield depends on its relays.\nSide objectives are optional; the exit remains the main goal.",
            _ => "Collect supplies and secrets, and chain actions for up to x5.\nA chain fades after five seconds without a scoring action.\n"
                + "Taking a hit breaks your chain.\nClear a stage quickly and safely for additional points.\n"
                + "Personal bests are local; these games grant no world items.\nRuns pause when you leave Arcade; they reset on a new session."
        };
    }
}
