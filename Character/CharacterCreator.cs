// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Modstoke.Loader;
using Modstoke.Sons;
using UnityEngine;

namespace Modstoke.Client;

// The full-screen character creator. Edits show on the live player at once, so the
// stage copy simply follows it; nothing is saved until Done.
internal sealed class ClientCharacterCreator
{
    private ClientCharacterProvider? _provider;
    private ScreenHostSession? _session;
    private PreviewStage? _stage;
    private CharacterAppearance _working = new();
    private bool _saved;
    private bool _capture;
    private int _recolour;
    private float _nextCapture;

    internal bool IsOpen => _session != null;

    internal bool TryOpen(ClientCharacterProvider provider, ScreenHostOrigin origin, out string status)
    {
        if (_session != null)
        {
            status = "The character creator is already open.";
            return false;
        }
        if (!Plugin.Characters.PackageInstalled)
        {
            status = "The Modstoke character package is not installed.";
            return false;
        }
        var request = new ScreenHostRequest
        {
            OwnerId = ClientCharacterProvider.MenuId,
            Title = "Character",
            Subtitle = "Changes show on your character as you make them. Choose Done to keep them.",
            Origin = origin,
            Closed = Closed
        };
        if (!ScreenHost.TryOpen(request, out var session, out status)) return false;
        _provider = provider;
        _session = session;
        _working = provider.Saved.Body;
        _saved = false;
        provider.Preview(_working);
        if (session.State == ScreenHostState.Open) Begin();
        else session.Opened += _ => Begin();
        status = "Opening the character creator.";
        return true;
    }

    private void Begin()
    {
        if (_session == null) return;
        if (PreviewStage.TryCreate(new PreviewStageOptions { OwnerId = ClientCharacterProvider.MenuId }, out var stage, out var status))
        {
            _stage = stage;
            _session.AttachStage(stage, true);
            Plugin.Characters.Preview = true;
            _capture = true;
            _nextCapture = 0;
        }
        else _session.SetStatus("The preview is unavailable: " + status);
        Rebuild();
        _session.SetHints("Drag to turn · wheel to zoom · double-click to reset the view");
    }

    // Game-thread update: the stage copy follows the live player.
    internal void Update()
    {
        if (_session == null || _stage == null) return;
        if (!_stage.IsAlive)
        {
            _stage = null;
            return;
        }
        if (_capture)
        {
            // The game may still be loading the look; try again shortly.
            if (Time.unscaledTime < _nextCapture) return;
            _nextCapture = Time.unscaledTime + .5f;
            if (_stage.TryCaptureLocalPlayer(new PreviewCaptureOptions(), out _, out var status)) _capture = false;
            else _session.SetStatus(status);
            return;
        }
        // A changed mesh, such as a new body type, starts a fresh capture by itself.
        _stage.Sync();
        // Sync follows meshes, flags and blend weights but not material values. The body re-tints
        // on its own update, which may come before or after a click, so render on two frames.
        if (_recolour > 0)
        {
            _recolour--;
            _stage.MarkDirty();
        }
    }

    private void Rebuild()
    {
        if (_session == null) return;
        var sections = new List<ScreenPanelSection>
        {
            new("Body", new[]
            {
                ScreenPanelRow.Choice("sex", "Body type", new[] { "Male", "Female" }, (int)_working.Sex, index =>
                {
                    Change(_working with { Sex = (CharacterSex)index });
                    Rebuild();
                }),
                Swatches("skin", "Skin tone", ClientCharacterProvider.SkinTones, _working.Skin,
                    color => _working with { Skin = color }),
                Swatches("eyes", "Eye colour", ClientCharacterProvider.EyeColors, _working.Eyes,
                    color => _working with { Eyes = color }),
                Swatches("underwear", "Underwear", ClientCharacterProvider.UnderwearColors, _working.Underwear,
                    color => _working with { Underwear = color, NoUnderwear = false })
            })
        };
        // Offered only to players who turned on adult content; others still see underwear.
        if (Plugin.Characters.AdultInstalled && Plugin.Characters.AdultOptIn)
            sections[0] = new ScreenPanelSection("Body", new List<ScreenPanelRow>(sections[0].Rows)
            {
                ScreenPanelRow.Toggle("nude", "No underwear", _working.NoUnderwear,
                    on => Change(_working with { NoUnderwear = on }))
            });
        var shapes = new List<ScreenPanelRow>();
        foreach (var name in _working.ShapeNames)
        {
            var shape = name;
            shapes.Add(ScreenPanelRow.Slider("shape:" + shape, shape.Replace('_', ' '), 0, 100,
                Mathf.Round(_working.Shape(shape) * 100),
                value => Change(_working with
                {
                    Shapes = new Dictionary<string, float>(_working.Shapes, StringComparer.Ordinal) { [shape] = value / 100 }
                }), _ => { }, 1));
        }
        sections.Add(new ScreenPanelSection("Shape", shapes));
        sections.Add(new ScreenPanelSection("View", new[]
        {
            ScreenPanelRow.Button("view:body", "Whole body", () => _stage?.SetFocus(PreviewFocus.Body)),
            ScreenPanelRow.Button("view:face", "Face", () => _stage?.SetFocus(PreviewFocus.Face)),
            ScreenPanelRow.Button("view:hands", "Hands", () => _stage?.SetFocus(PreviewFocus.Hands))
        }));
        sections.Add(new ScreenPanelSection("Finish", new[]
        {
            ScreenPanelRow.Button("done", "Done", Done),
            ScreenPanelRow.Button("reset", "Undo changes", () =>
            {
                if (_provider == null) return;
                Change(_provider.Saved.Body);
                Rebuild();
            })
        }));
        _session.SetSections(sections);
    }

    private ScreenPanelRow Swatches(string id, string label, (string Name, Color32 Color)[] presets, Color32 current,
        Func<Color32, CharacterAppearance> apply)
    {
        var colors = new Color[presets.Length];
        var index = -1;
        for (var i = 0; i < presets.Length; i++)
        {
            colors[i] = presets[i].Color;
            var preset = presets[i].Color;
            if (preset.r == current.r && preset.g == current.g && preset.b == current.b) index = i;
        }
        return ScreenPanelRow.Swatches(id, label, colors, index, chosen =>
        {
            if (chosen >= 0 && chosen < presets.Length) Change(apply(presets[chosen].Color));
        });
    }

    private void Change(CharacterAppearance appearance)
    {
        _working = appearance;
        _provider?.Preview(appearance);
        _recolour = 2;
    }

    private void Done()
    {
        if (_provider == null || _session == null) return;
        if (!_provider.TrySave(_working, out var status))
        {
            _session.SetStatus(status);
            return;
        }
        _saved = true;
        _session.RequestClose();
    }

    private void Closed(ScreenHostCloseReason reason)
    {
        Plugin.Characters.Preview = false;
        if (!_saved) _provider?.Preview(null);
        _stage?.Dispose();
        _stage = null;
        _session = null;
        _provider = null;
    }

    internal void Close() => _session?.Dispose();
}
