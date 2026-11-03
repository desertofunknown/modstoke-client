// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using Modstoke.Sons;

namespace Modstoke.Client;

internal sealed record ClientPackMatIngredient(int ItemId, string Name, int Count, bool Reusable);

internal sealed record ClientPackMatSnapshot
{
    internal ClientPackReadStatus Status { get; init; } = ClientPackReadStatus.NotObserved;
    internal string Message { get; init; } = "Read the mat to capture its ingredients.";
    internal bool Pending { get; init; }
    internal long ObservedAt { get; init; }
    internal int TotalRows { get; init; }
    internal bool Truncated { get; init; }
    internal bool FromOpenInventory { get; init; }
    internal IReadOnlyList<ClientPackMatIngredient> Ingredients { get; init; } = Array.Empty<ClientPackMatIngredient>();
}

internal sealed record ClientPackMatRequirement(string Name, bool Reusable, long? RequiredCount,
    int? MatCount, long? ShortageCount);

internal sealed record ClientPackMatComparisonSnapshot
{
    internal ClientPackReadStatus Status { get; init; } = ClientPackReadStatus.NotObserved;
    internal string Message { get; init; } = "Choose a recipe in Crafting to compare its ingredients.";
    internal string RecipeName { get; init; } = "";
    internal int Executions { get; init; }
    internal int IngredientCount { get; init; }
    internal long MatObservedAt { get; init; }
    internal long RecipeObservedAt { get; init; }
    internal bool? CountsCovered { get; init; }
    internal IReadOnlyList<ClientPackMatRequirement> Ingredients { get; init; } = Array.Empty<ClientPackMatRequirement>();
}

internal sealed partial class ClientPackApp
{
    private const int MaximumMatEntries = 64;
    private bool _readMatRequested;
    private bool _rememberMat;
    private long _lastMatRead;
    private ClientPackMatSnapshot _mat = new();

    internal void ObserveOpenMat(bool visible, bool canObserve)
    {
        if (!OnThread || _disposed) return;
        if (!canObserve)
        {
            if (_rememberMat || _mat.ObservedAt != 0 || _mat.Pending)
            {
                StopRememberingMat();
                ResetMat(ClientPackReadStatus.Unavailable, "The mat is unavailable until your inventory is ready.");
                Publish();
            }
            return;
        }
        if (!_rememberMat || !visible || _lastMatRead != 0 && Now - _lastMatRead < 1000) return;
        try { CaptureMat(fromOpenInventory: true); }
        catch (Exception) { ResetMat(ClientPackReadStatus.Unavailable, "The open mat could not be read."); }
        Publish();
    }

    private void StopRememberingMat()
    {
        _rememberMat = false;
        _lastMatRead = 0;
    }

    private void ResetMat(ClientPackReadStatus status = ClientPackReadStatus.NotObserved,
        string message = "Read the mat to capture its ingredients.")
    {
        _readMatRequested = false;
        _mat = new() { Status = status, Message = message };
    }

    private void ReadMatIfRequested()
    {
        if (!_readMatRequested) return;
        _readMatRequested = false;
        CaptureMat(fromOpenInventory: false);
    }

    private void CaptureMat(bool fromOpenInventory)
    {
        _readMatRequested = false;
        _lastMatRead = Now;
        if (!Crafting.TryReadCraftingMat(out var captured, out _, maxEntries: MaximumMatEntries))
        {
            ResetMat(ClientPackReadStatus.Unavailable, "The mat is unavailable. Read it again when ready.");
            return;
        }
        var rows = captured.Ingredients.Select(row => new ClientPackMatIngredient(row.ItemId,
            row.ItemId > 0 ? IngredientName(row.ItemId) : "Unknown item", row.Count, row.IsReusable)).ToArray();
        var ambiguous = rows.Any(row => row.ItemId <= 0 || row.Count < 0)
            || rows.Select(row => row.ItemId).Distinct().Count() != rows.Length;
        _mat = new()
        {
            Status = captured.Truncated ? ClientPackReadStatus.Partial : ambiguous ? ClientPackReadStatus.Uncertain
                : rows.Length == 0 ? ClientPackReadStatus.Empty : ClientPackReadStatus.Ready,
            Message = captured.Truncated ? "Only part of the mat was read. Exact gaps are unavailable."
                : ambiguous ? "Some ingredient quantities are unclear. Exact gaps are unavailable."
                : rows.Length == 0 ? "The observed mat has no ingredient entries." : "Ingredients from the last mat reading.",
            ObservedAt = Now, TotalRows = captured.TotalRows, Truncated = captured.Truncated,
            FromOpenInventory = fromOpenInventory,
            Ingredients = Array.AsReadOnly(rows)
        };
    }

    private ClientPackMatComparisonSnapshot CompareMat()
    {
        if (_recipe.Count == 0 || _recipeIndex < 0 || _recipeIndex >= _recipes.Length) return new();
        var selected = _recipes[_recipeIndex];
        var recipeClear = selected.Ingredients.Count <= MaximumRecipeIngredients
            && selected.Ingredients.All(row => row.ItemId > 0 && row.Count > 0)
            && selected.Ingredients.Select(row => row.ItemId).Distinct().Count() == selected.Ingredients.Count;
        var matClear = !_mat.Pending && !_mat.Truncated && _mat.ObservedAt != 0
            && _mat.Status is ClientPackReadStatus.Ready or ClientPackReadStatus.Empty;
        var counts = matClear ? _mat.Ingredients.ToDictionary(row => row.ItemId, row => row.Count)
            : new Dictionary<int, int>();
        var rows = selected.Ingredients.Take(MaximumRecipeIngredients).Select(row =>
        {
            long? required = recipeClear ? row.IsReusable ? row.Count : (long)row.Count * _recipeExecutions : null;
            int? onMat = matClear && row.ItemId > 0 ? counts.GetValueOrDefault(row.ItemId) : null;
            long? missing = required.HasValue && onMat.HasValue ? Math.Max(0L, required.Value - onMat.Value) : null;
            return new ClientPackMatRequirement(IngredientName(row.ItemId), row.IsReusable, required, onMat, missing);
        }).ToArray();
        bool? covered = recipeClear && matClear ? rows.All(row => row.ShortageCount == 0) : null;
        var status = !recipeClear ? ClientPackReadStatus.Uncertain
            : matClear ? ClientPackReadStatus.Ready : _mat.Status;
        var message = !recipeClear ? "Recipe quantities are unclear. Gaps remain unknown."
            : _mat.Pending ? "Waiting for the mat reading."
            : _mat.ObservedAt == 0 ? "Read the mat to compare its ingredient counts."
            : !matClear ? "The mat reading is incomplete or ambiguous. Gaps remain unknown."
            : rows.Length == 0 ? "This recipe declares no ingredient counts."
            : covered == true ? "The observed mat covers these declared counts."
            : "These quantities are missing from the observed mat.";
        return new()
        {
            Status = status, Message = message, RecipeName = _recipe.Name, Executions = _recipeExecutions,
            IngredientCount = selected.Ingredients.Count, MatObservedAt = _mat.ObservedAt,
            RecipeObservedAt = _recipe.MetadataObservedAt, CountsCovered = covered, Ingredients = Array.AsReadOnly(rows)
        };
    }
}
