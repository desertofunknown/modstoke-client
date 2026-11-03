// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Modstoke.Client;

internal sealed partial class ClientTerminalView
{
    private RectTransform _gamesSurface = null!;
    private Image _gamesBoard = null!, _gamesOverlay = null!;
    private TextMeshProUGUI _gamesTitle = null!, _gamesStage = null!, _gamesScore = null!, _gamesBest = null!, _gamesHealth = null!, _gamesObjective = null!;
    private TextMeshProUGUI _gamesSecrets = null!, _gamesCombo = null!, _gamesTouchHint = null!, _gamesOverlayStage = null!, _gamesOverlayAction = null!;
    private TextMeshProUGUI _gamesClock = null!, _gamesHelp = null!, _gamesStatus = null!, _gamesOverlayTitle = null!, _gamesOverlayHint = null!;
    private ButtonWidget _gamesIsland = null!, _gamesOps = null!, _gamesContinue = null!, _gamesPause = null!;
    private ButtonWidget _gamesJump = null!, _gamesUp = null!, _gamesDown = null!, _gamesDash = null!, _gamesFire = null!;
    private readonly List<ButtonWidget> _gamesMovement = new();
    private readonly List<Image> _gameShapes = new(ClientTabletGames.ShapeCapacity);
    private readonly List<Image> _gamesHealthPips = new(5);
    private ClientGameSprites? _gameSprites;
    private int _gamesDrawCount;
    private bool ArcadeCanAct => IsAlive && !ContentObscured && _page?.App == ClientTerminalApp.Games
        && _gamesSurface != null && _gamesSurface && _gamesSurface.gameObject.activeInHierarchy;

    private void BuildGames()
    {
        _gameSprites = new ClientGameSprites();
        _gamesSurface = FieldSurface("Arcade");
        _gamesIsland = Button(_gamesSurface, "TideRunner", "Tide Runner", 0, 0, 300, 54, () => GameCommand("island"));
        _gamesOps = Button(_gamesSurface, "SignalBreach", "Signal Breach", 316, 0, 300, 54, () => GameCommand("ops"));
        Button(_gamesSurface, "FieldManual", "How to play", 640, 0, 168, 54, OpenArcadeManual);
        _gamesStatus = GameText(_gamesSurface, "Local", "OFFLINE ARCADE", 18, Muted, 824, 13, 272, 32);
        _gamesStatus.alignment = TextAlignmentOptions.MidlineRight;
        Button(_gamesSurface, "Fullscreen", "Fullscreen", 1112, 0, 216, 54, () =>
        {
            if (ArcadeCanAct) _command("fullscreen", "arcade");
        });
        _gamesBoard = Panel(_gamesSurface, "GameBoard", new Color(.07f, .14f, .18f), 0, 74, ClientTabletGames.Width, ClientTabletGames.Height);
        _gamesBoard.gameObject.AddComponent<RectMask2D>();
        for (var index = 0; index < ClientTabletGames.ShapeCapacity; index++)
        {
            var shape = Panel(_gamesBoard.transform, "Piece" + index, Color.white, 0, 0, 1, 1);
            shape.gameObject.SetActive(false);
            _gameShapes.Add(shape);
        }

        _gamesOverlay = RoundedPanel(_gamesBoard.transform, "GameState", new Color(.035f, .065f, .085f, .97f), 140, 75, 600, 240);
        Panel(_gamesOverlay.transform, "Accent", new Color(.45f, .88f, .82f), 32, 0, 536, 3);
        _gamesOverlayStage = GameText(_gamesOverlay.transform, "Stage", "", 19, new Color(.58f, .84f, .84f), 24, 20, 552, 28, true);
        _gamesOverlayStage.alignment = TextAlignmentOptions.Center;
        _gamesOverlayTitle = GameText(_gamesOverlay.transform, "Title", "Tide Runner", 35, Color.white, 24, 54, 552, 50, true);
        _gamesOverlayTitle.alignment = TextAlignmentOptions.Center;
        _gamesOverlayHint = GameText(_gamesOverlay.transform, "Hint", "", 21, new Color(.8f, .88f, .88f), 32, 112, 536, 70);
        _gamesOverlayHint.alignment = TextAlignmentOptions.Center;
        _gamesOverlayAction = GameText(_gamesOverlay.transform, "Action", "", 22, new Color(1f, .82f, .46f), 24, 194, 552, 32, true);
        _gamesOverlayAction.alignment = TextAlignmentOptions.Center;

        _gamesMovement.Add(GameButton("Left", "Left", 0, 480, 126, "left"));
        _gamesMovement.Add(GameButton("Right", "Right", 138, 480, 126, "right"));
        _gamesMovement.Add(GameButton("Stop", "Stop", 276, 480, 126, "stop"));
        _gamesJump = GameButton("Jump", "Jump / Space", 414, 480, 240, "jump");
        _gamesUp = GameButton("Up", "Up", 414, 480, 114, "up");
        _gamesDown = GameButton("Down", "Down", 540, 480, 114, "down");
        _gamesMovement.Add(_gamesJump);
        _gamesMovement.Add(_gamesUp);
        _gamesMovement.Add(_gamesDown);
        _gamesDash = GameButton("Dash", "Dash / Shift", 666, 480, 214, "dash");
        _gamesMovement.Add(_gamesDash);
        _gamesPause = GameButton("Pause", "Pause / P", 0, 544, 250, "pause");
        _gamesContinue = GameButton("Continue", "Start / Enter", 266, 544, 300, "continue");
        _gamesRestart = GameButton("Restart", "Restart run", 582, 544, 298, "restart");

        var card = RoundedPanel(_gamesSurface, "Mission", Surface, 904, 74, 424, 526);
        _gamesTitle = GameText(card.transform, "Heading", "TIDE RUNNER", 20, Mint, 24, 16, 376, 28, true);
        _gamesStage = GameText(card.transform, "Stage", "", 26, Ink, 24, 50, 376, 64, true);
        _gamesObjectiveScroll = Scroll(card.transform, "ObjectiveScroll", 24, 116, 376, 84);
        _gamesObjective = Text(_gamesObjectiveScroll.Content, "Objective", "", 20, Ink, 0, 0, 376, 84);
        _gamesProgressText = GameText(card.transform, "Progress", "", 18, Muted, 24, 206, 376, 24);
        _gamesProgressBar = Panel(card.transform, "ProgressBar", Mint, 24, 234, 1, 3);
        _gamesScore = GameText(card.transform, "Score", "", 32, Ink, 24, 244, 248, 38, true);
        _gamesCombo = GameText(card.transform, "Combo", "", 22, Amber, 276, 252, 124, 30, true);
        _gamesCombo.alignment = TextAlignmentOptions.TopRight;
        _gamesBest = GameText(card.transform, "Best", "", 18, Muted, 24, 286, 376, 24);
        _gamesHealth = GameText(card.transform, "Health", "", 20, Ink, 24, 320, 376, 28);
        for (var index = 0; index < 5; index++)
            _gamesHealthPips.Add(Panel(card.transform, "Health" + index, Mint, 24 + index * 76, 352, 68, 10));
        _gamesSecrets = GameText(card.transform, "Secrets", "", 18, Mint, 24, 370, 224, 24);
        _gamesClock = GameText(card.transform, "Clock", "", 18, Amber, 252, 370, 148, 24);
        _gamesClock.alignment = TextAlignmentOptions.TopRight;
        _gamesElapsed = GameText(card.transform, "RunTime", "", 16, Muted, 24, 398, 376, 22);
        Panel(card.transform, "ControlsDivider", Rule, 24, 426, 376, 1);
        _gamesHelp = GameText(card.transform, "AbilityState", "", 17, Muted, 24, 436, 376, 42);
        _gamesTouchHint = GameText(card.transform, "TouchHint", "Touch arrows move until Stop.", 21, Mint, 24, 486, 376, 30);
        _gamesFire = Button(card.transform, "Fire", "Auto fire: on", 24, 478, 376, 44, () => GameCommand("fire"));
        BuildArcadeManual();
        _gamesSurface.gameObject.SetActive(false);
    }

    private void GameCommand(string command)
    {
        if (!ArcadeCanAct) return;
        if (Time.unscaledTime >= _gamesRestartUntil) _gamesRestartPending = false;
        if (command == "restart" && _gamesHasRun && !_gamesRunFinished && !_gamesRestartPending)
        {
            _command("games", "pause");
            _gamesRestartPending = true;
            _gamesRestartUntil = Time.unscaledTime + 15;
            _gamesManualOpen = false;
            return;
        }
        _gamesRestartPending = false;
        _gamesManualOpen = false;
        _command("games", command);
    }

    private ButtonWidget GameButton(string name, string label, float x, float y, float width, string command)
        => Button(_gamesSurface, name, label, x, y, width, 52, () => GameCommand(command));

    private TextMeshProUGUI GameText(Transform parent, string name, string value, float size, Color color,
        float x, float y, float width, float height, bool bold = false)
    {
        var label = Text(parent, name, value, size, color, x, y, width, height, bold);
        label.overflowMode = TextOverflowModes.Ellipsis;
        return label;
    }

    internal void SetGamesVisible(bool visible)
    {
        _gamesSurface.gameObject.SetActive(visible);
        if (!visible) { _gamesRestartPending = false; _gamesManualOpen = false; }
    }

    internal void SetGamesState(ClientTabletGamesSnapshot state)
    {
        var island = state.Game == ClientTabletGame.TideRunner;
        var active = state.Started && !state.Paused && !state.Finished;
        _gamesIsland.Image.color = island ? AppColor(ClientTerminalApp.Games) : Raised;
        _gamesOps.Image.color = island ? Raised : AppColor(ClientTerminalApp.Games);
        _gamesIsland.Label.color = island ? Color.white : Ink;
        _gamesOps.Label.color = island ? Ink : Color.white;
        _gamesBoard.color = island ? new Color(.08f, .19f, .24f) : new Color(.055f, .105f, .12f);
        _gamesTitle.text = island ? "TIDE RUNNER" : "SIGNAL BREACH";
        _gamesStage.text = $"{state.Stage} / {state.StageCount}  {state.StageName}";
        _gamesScore.text = $"{state.Score:N0} pts";
        _gamesBest.text = $"PERSONAL BEST  {state.Best:N0}";
        _gamesCombo.text = $"x{state.Combo} combo";
        _gamesCombo.color = state.Combo > 1 ? Amber : Muted;
        _gamesHealth.text = island ? $"Lives  {state.Health} / 3" : $"Armor  {state.Health} / 5";
        var healthMaximum = island ? 3 : 5;
        for (var index = 0; index < _gamesHealthPips.Count; index++)
        {
            var pip = _gamesHealthPips[index];
            pip.gameObject.SetActive(index < healthMaximum);
            Place(pip.rectTransform, 24 + index * (384f / healthMaximum), 352, 384f / healthMaximum - 8, 10);
            pip.color = index < state.Health ? state.Health == 1 ? Amber : Mint : Rule;
        }
        SetArcadeObjective(state.Objective);
        _gamesSecrets.text = $"Secrets  {state.Secrets} / {state.TotalSecrets}";
        _gamesClock.text = $"Time {state.Seconds / 60}:{state.Seconds % 60:00}";
        _gamesClock.color = state.Seconds <= 30 && active ? new Color(1f, .5f, .38f) : Amber;
        _gamesControlsText = state.Controls;
        _gamesStatus.text = active ? $"{state.Motion.ToUpperInvariant()}  /  P TO PAUSE"
            : state.Finished ? state.Won ? "CAMPAIGN COMPLETE  /  PERSONAL BESTS" : "RUN ENDED  /  TRY AGAIN"
            : state.Started ? "PAUSED  /  ENTER TO CONTINUE" : "OFFLINE ARCADE  /  ENTER TO START";
        _gamesJump.Image.gameObject.SetActive(island);
        _gamesUp.Image.gameObject.SetActive(!island);
        _gamesDown.Image.gameObject.SetActive(!island);
        _gamesDash.Label.text = island ? "Dash / Shift" : "Roll / Shift";
        _gamesTouchHint.gameObject.SetActive(island);
        _gamesFire.Image.gameObject.SetActive(!island);
        _gamesFire.Label.text = state.Firing ? "Auto fire: on" : "Auto fire: off";
        _gamesFire.Button.interactable = active;
        foreach (var control in _gamesMovement) control.Button.interactable = active;
        _gamesPause.Button.interactable = active;
        _gamesContinue.Button.interactable = !active && !state.Finished;
        _gamesContinue.Label.text = state.Started ? "Continue / Enter" : "Start / Enter";
        _gamesContinue.Image.color = !active && !state.Finished ? AppColor(ClientTerminalApp.Games) : Raised;
        _gamesContinue.Label.color = !active && !state.Finished ? Color.white : Muted;
        _gamesOverlay.gameObject.SetActive(!active);
        _gamesOverlayStage.text = $"STAGE {state.Stage} / {state.StageCount}  ·  {state.StageName}";
        _gamesOverlayTitle.text = !state.Started ? island ? "Tide Runner" : "Signal Breach"
            : state.Finished ? state.Won ? island ? "Rescue secured" : "Extraction complete" : "Run ended" : "Paused";
        _gamesOverlayTitle.color = state.Finished ? state.Won ? new Color(.6f, 1f, .79f) : new Color(1f, .66f, .5f) : Color.white;
        _gamesOverlayHint.text = !state.Started ? state.Objective
            : state.Finished ? $"Score {state.Score:N0}  ·  Best {state.Best:N0}\nSecrets {state.Secrets} / {state.TotalSecrets}"
            : "Your run is on hold.\nMovement and auto fire are paused.";
        _gamesOverlayAction.text = state.Finished ? "Choose Restart run below"
            : state.Started ? "Press Enter or choose Continue" : "Press Enter or choose Start";

        for (var index = 0; index < state.Count; index++)
        {
            var shape = state.Shapes[index];
            var image = _gameShapes[index];
            if (index >= _gamesDrawCount) image.gameObject.SetActive(true);
            var sprite = shape.Sprite == ClientGameSprite.None ? null : _gameSprites!.Get(shape.Sprite);
            var tiled = sprite != null && shape.TileSize > 0;
            var flipped = sprite != null && shape.FlipX;
            image.sprite = sprite;
            image.type = tiled ? Image.Type.Tiled : Image.Type.Simple;
            image.pixelsPerUnitMultiplier = tiled ? sprite!.rect.width / shape.TileSize : 1f;
            image.color = sprite != null ? new Color(1f, 1f, 1f, shape.Opacity) : GameColor(shape.Ink);
            // Place uses a left pivot: move that pivot to the right edge before mirroring.
            Place(image.rectTransform, shape.X + (flipped ? shape.Width : 0f), shape.Y, shape.Width, shape.Height);
            image.rectTransform.localScale = new Vector3(flipped ? -1f : 1f, 1f, 1f);
        }
        for (var index = state.Count; index < _gamesDrawCount; index++) _gameShapes[index].gameObject.SetActive(false);
        _gamesDrawCount = state.Count;
        RefreshArcadeShell(state);
        UpdateImmersiveGames();
    }

    private void ReleaseGameSprites()
    {
        foreach (var image in _gameShapes)
            if (image != null && image) image.sprite = null;
        _gameSprites?.Dispose();
        _gameSprites = null;
    }

    private static Color GameColor(ClientGameInk ink) => ink switch
    {
        ClientGameInk.Water => new Color(.08f, .39f, .52f),
        ClientGameInk.Ground => new Color(.25f, .28f, .24f),
        ClientGameInk.Grass => new Color(.44f, .64f, .41f),
        ClientGameInk.Player => new Color(.62f, .93f, .84f),
        ClientGameInk.Visor => new Color(.055f, .12f, .16f),
        ClientGameInk.Supply => new Color(1f, .79f, .35f),
        ClientGameInk.Enemy => new Color(.96f, .39f, .34f),
        ClientGameInk.Shot => new Color(1f, .91f, .59f),
        ClientGameInk.Beacon => new Color(.8f, .94f, .97f),
        ClientGameInk.Heal => new Color(.45f, 1f, .53f),
        ClientGameInk.Sky => new Color(.075f, .16f, .23f),
        ClientGameInk.Cloud => new Color(.40f, .58f, .64f),
        ClientGameInk.Rock => new Color(.23f, .34f, .38f),
        ClientGameInk.Hazard => new Color(1f, .46f, .18f),
        ClientGameInk.Secret => new Color(.83f, .56f, 1f),
        ClientGameInk.Checkpoint => new Color(.32f, .95f, 1f),
        ClientGameInk.Cover => new Color(.34f, .49f, .59f),
        ClientGameInk.Terminal => new Color(.23f, .85f, .73f),
        ClientGameInk.EnemyShot => new Color(1f, .56f, .48f),
        ClientGameInk.Boss => new Color(.81f, .25f, .56f),
        ClientGameInk.Shield => new Color(.38f, .68f, 1f),
        ClientGameInk.Dash => new Color(.71f, 1f, 1f),
        ClientGameInk.Shadow => new Color(.035f, .075f, .10f),
        _ => new Color(.16f, .28f, .31f)
    };
}
