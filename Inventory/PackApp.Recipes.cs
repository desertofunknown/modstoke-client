// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Modstoke.Sons;

namespace Modstoke.Client;

internal enum ClientPackRecipeMode { MakesItem, UsesItem }

internal sealed record ClientPackIngredient(int ItemId, string Name, int CountPerCraft, bool Reusable,
    long? RequiredCount, int? InventoryCount, long? ShortageCount);

internal sealed record ClientPackRecipeSnapshot
{
    internal ClientPackReadStatus Status { get; init; } = ClientPackReadStatus.NotObserved;
    internal string Message { get; init; } = "Select an item to inspect its declared recipes.";
    internal string Name { get; init; } = "";
    internal string Type { get; init; } = "";
    internal ClientPackRecipeMode Mode { get; init; }
    internal int Index { get; init; }
    internal int Count { get; init; }
    internal int ReportedCount { get; init; }
    internal int SourcePage { get; init; } = 1;
    internal int SourcePageCount { get; init; } = 1;
    internal bool CanPrevious { get; init; }
    internal bool CanNext { get; init; }
    internal string Coverage { get; init; } = "";
    internal int Executions { get; init; } = 1;
    internal bool? CountsCovered { get; init; }
    internal bool UsesContainerData { get; init; }
    internal bool ForceNewInstance { get; init; }
    internal int IngredientCount { get; init; }
    internal string DeclaredResults { get; init; } = "";
    internal long MetadataObservedAt { get; init; }
    internal long StockObservedAt { get; init; }
    internal IReadOnlyList<ClientPackIngredient> Ingredients { get; init; } = Array.Empty<ClientPackIngredient>();
}

internal sealed partial class ClientPackApp
{
    internal const int MaximumRecipeExecutions = 99;
    private const int RecipeQuerySize = 64;
    private const int MaximumRecipeIngredients = 64;
    private const int SupplyDraftBodyLimit = 2048;
    private RecipeSnapshot[] _recipes = Array.Empty<RecipeSnapshot>();
    private int _recipeIndex, _recipeExecutions = 1, _recipeStart, _nextRecipeStart, _recipeTotal;
    private int _unknownRecipeCount;
    private bool _recipeHasMore, _selectLastRecipe;
    private bool _reloadRecipes = true;
    private ClientPackRecipeMode _recipeMode;
    private long _recipesObservedAt;
    private ClientPackRecipeSnapshot _recipe = new();

    private void ResetRecipes()
    {
        _recipes = Array.Empty<RecipeSnapshot>();
        _recipeIndex = 0;
        _recipeExecutions = 1;
        ResetRecipeDiscovery();
    }

    private void ResetRecipeDiscovery()
    {
        _recipes = Array.Empty<RecipeSnapshot>();
        _recipeIndex = _recipeStart = _nextRecipeStart = _recipeTotal = _unknownRecipeCount = 0;
        _recipeHasMore = _selectLastRecipe = false;
        _recipesObservedAt = 0;
        _reloadRecipes = true;
        _recipe = new() { Mode = _recipeMode };
    }

    private void HandleRecipeCommand(string verb)
    {
        if (verb is "recipe-mode-makes" or "recipe-mode-uses")
        {
            var mode = verb == "recipe-mode-uses" ? ClientPackRecipeMode.UsesItem : ClientPackRecipeMode.MakesItem;
            if (_recipeMode == mode) return;
            _recipeMode = mode;
            ResetRecipes();
            _readRequested = true;
            Publish();
            return;
        }
        switch (verb)
        {
            case "recipe-prev" when _recipeIndex > 0: _recipeIndex--; break;
            case "recipe-prev" when _recipeStart > 0:
                QueueRecipeSection(Math.Max(0, _recipeStart - RecipeQuerySize), selectLast: true);
                return;
            case "recipe-next" when _recipeIndex + 1 < _recipes.Length: _recipeIndex++; break;
            case "recipe-next" when _recipeHasMore:
                QueueRecipeSection(_nextRecipeStart, selectLast: false);
                return;
            case "quantity-minus" when _recipes.Length > 0 && _recipeExecutions > 1: _recipeExecutions--; break;
            case "quantity-plus" when _recipes.Length > 0 && _recipeExecutions < MaximumRecipeExecutions: _recipeExecutions++; break;
            default: return;
        }
        _recipe = DescribeRecipe(ClientPackReadStatus.NotObserved, "Waiting to compare ingredient stock.");
        _readRequested = true;
        Publish();
    }

    private void QueueRecipeSection(int start, bool selectLast)
    {
        _recipeStart = start;
        _recipeIndex = 0;
        _recipes = Array.Empty<RecipeSnapshot>();
        _recipeHasMore = false;
        _recipesObservedAt = 0;
        _unknownRecipeCount = 0;
        _selectLastRecipe = selectLast;
        _reloadRecipes = true;
        _recipe = RecipePageState(ClientPackReadStatus.NotObserved, "Reading the next recipe section.");
        _readRequested = true;
        Publish();
    }

    private void ReadRecipes()
    {
        if (_selectedItemId == 0)
        {
            _recipe = RecipePageState(ClientPackReadStatus.Empty, "Select a published item to inspect its recipes.");
            return;
        }
        if (_reloadRecipes)
        {
            _reloadRecipes = false;
            _recipes = Array.Empty<RecipeSnapshot>();
            _recipesObservedAt = 0;
            _recipeHasMore = false;
            RecipeQuerySnapshot query;
            var read = _recipeMode == ClientPackRecipeMode.UsesItem
                ? Crafting.TryQueryRecipesForIngredient(_selectedItemId, out query, out _,
                    startRecipeIndex: _recipeStart, maximumRecipes: RecipeQuerySize)
                : Crafting.TryQueryRecipesForResult(_selectedItemId, out query, out _,
                    startRecipeIndex: _recipeStart, maximumRecipes: RecipeQuerySize);
            if (!read)
            {
                _recipe = RecipePageState(ClientPackReadStatus.Unavailable,
                    "Recipes are unavailable or have changed. Refresh to start again.");
                return;
            }
            _recipes = query.Recipes.ToArray();
            _recipeStart = query.StartRecipeIndex;
            _nextRecipeStart = query.NextRecipeIndex;
            _recipeTotal = query.TotalRecipeCount;
            _unknownRecipeCount = query.UnavailableRecipeCount + query.RowLimitRecipeCount;
            _recipeHasMore = query.HasMoreRecipes;
            _recipeIndex = _selectLastRecipe ? Math.Max(0, _recipes.Length - 1) : 0;
            _selectLastRecipe = false;
            _recipesObservedAt = Now;
            if (_recipes.Length == 0)
            {
                _recipe = RecipePageState(query.IsComplete ? ClientPackReadStatus.Empty : ClientPackReadStatus.Partial,
                    query.IsComplete ? _recipeMode == ClientPackRecipeMode.UsesItem
                        ? "No recipe in the observed catalog lists this item as an ingredient."
                        : "No recipe in the observed catalog declares this item as a result."
                        : "No matching recipe was confirmed in this section. Browse another section or refresh.");
                return;
            }
        }
        if (_recipes.Length == 0) return;
        var selected = _recipes[_recipeIndex];
        if (selected.Ingredients.Count > MaximumRecipeIngredients)
        {
            _recipe = DescribeRecipe(ClientPackReadStatus.Uncertain,
                "This recipe is too large to compare here. Ingredient quantities remain unknown.");
            return;
        }
        // The SDK rejects invalid or duplicate ingredient rows. Do not infer
        // alternative ingredients, sum duplicate rows or treat a failure as zero.
        if (!Crafting.TryGetRecipeRequirements(selected, _recipeExecutions,
                out var requirements, out _, includeEquipped: true, checkNextSlot: false))
        {
            var ambiguous = selected.Ingredients.Any(item => item.ItemId <= 0 || item.Count <= 0)
                || selected.Ingredients.Select(item => item.ItemId).Distinct().Count() != selected.Ingredients.Count;
            _recipe = DescribeRecipe(ambiguous ? ClientPackReadStatus.Uncertain : ClientPackReadStatus.Unavailable,
                ambiguous ? "This recipe contains ambiguous ingredient rows. Quantities cannot be combined safely."
                    : "Ingredient stock is unavailable. Refresh to read it again.");
            return;
        }
        var rows = requirements.Ingredients.Select(row => new ClientPackIngredient(row.ItemId,
            IngredientName(row.ItemId), row.CountPerCraft, row.IsReusable,
            row.RequiredCount, row.InventoryCount, row.ShortageCount)).ToArray();
        _recipe = DescribeRecipe(ClientPackReadStatus.Ready,
            requirements.HasRequiredItemCounts ? "Observed stock covers every declared ingredient count."
                : "Gather the missing ingredients shown below.") with
        {
            CountsCovered = requirements.HasRequiredItemCounts,
            StockObservedAt = Now, Ingredients = Array.AsReadOnly(rows)
        };
    }

    private ClientPackRecipeSnapshot DescribeRecipe(ClientPackReadStatus status, string message)
    {
        var selected = _recipes[_recipeIndex];
        var rows = selected.Ingredients.Take(MaximumRecipeIngredients).Select(row =>
            new ClientPackIngredient(row.ItemId, IngredientName(row.ItemId), row.Count, row.IsReusable,
                null, null, null)).ToArray();
        return RecipePageState(status, message) with
        {
            Name = string.IsNullOrWhiteSpace(selected.Name)
                ? "Declared recipe" : selected.Name,
            Type = selected.RecipeType, Index = _recipeIndex + 1, Count = _recipes.Length,
            UsesContainerData = selected.UsesContainerDataForResults,
            ForceNewInstance = selected.ForceNewItemInstance,
            IngredientCount = selected.Ingredients.Count,
            DeclaredResults = string.Join(", ", selected.DeclaredResultItemIds.Take(4).Select(IngredientName))
                + (selected.DeclaredResultItemIds.Count > 4 ? ", …" : ""),
            Ingredients = Array.AsReadOnly(rows)
        };
    }

    private ClientPackRecipeSnapshot RecipePageState(ClientPackReadStatus status, string message) => new()
    {
        Status = status, Message = message, Mode = _recipeMode,
        Executions = _recipeExecutions, Count = _recipes.Length,
        ReportedCount = _recipes.Length, MetadataObservedAt = _recipesObservedAt,
        SourcePage = _recipeStart / RecipeQuerySize + 1,
        SourcePageCount = Math.Max(1, (_recipeTotal + RecipeQuerySize - 1) / RecipeQuerySize),
        CanPrevious = _recipeIndex > 0 || _recipeStart > 0,
        CanNext = _recipeIndex + 1 < _recipes.Length || _recipeHasMore,
        Coverage = _recipesObservedAt == 0 ? "Recipe coverage has not been established."
            : _unknownRecipeCount > 0 ? "Some entries in this catalog section could not be read."
            : _recipeStart > 0 || _recipeHasMore ? "Matches are from this section of the current catalog."
            : "The current recipe catalog was fully inspected."
    };

    private string IngredientName(int itemId) => itemId <= 0 ? "Unknown item"
        : _catalogItems.FirstOrDefault(item => item.Id == itemId)?.DisplayName
            ?? "Item " + itemId.ToString(CultureInfo.InvariantCulture);

    internal bool TryBuildSupplyDraft(out string title, out string body, out string status)
    {
        title = body = "";
        status = "Select a recipe before drafting supplies.";
        if (!OnThread || _disposed || !_visible || !_canObserve || _recipe.Count == 0) return false;
        var recipe = _recipe;
        title = SupplyText("Supplies · " + recipe.Name, 64);
        var draft = new StringBuilder();
        draft.Append("Recipe: ").Append(SupplyText(recipe.Name, 120)).Append('\n')
            .Append("Plan: ").Append(recipe.Executions.ToString(CultureInfo.InvariantCulture))
            .Append(recipe.Executions == 1 ? " recipe execution.\n" : " recipe executions.\n")
            .Append("Stock: ").Append(SupplyAge(recipe.StockObservedAt))
            .Append("; recipe: ").Append(SupplyAge(recipe.MetadataObservedAt)).Append(".\n")
            .Append("Planning counts only; no items are reserved.\n")
            .Append("Equipped stock included; pending equipment excluded.\n");
        var unknown = recipe.Ingredients.Count(row => !row.RequiredCount.HasValue
            || !row.InventoryCount.HasValue || !row.ShortageCount.HasValue)
            + Math.Max(0, recipe.IngredientCount - recipe.Ingredients.Count);
        draft.Append("Missing ingredient types: ")
            .Append(recipe.Ingredients.Count(row => row.ShortageCount > 0).ToString(CultureInfo.InvariantCulture))
            .Append("; unknown ingredient rows: ").Append(unknown.ToString(CultureInfo.InvariantCulture)).Append(".\n\n");
        var written = 0;
        foreach (var row in recipe.Ingredients.OrderBy(row => row.ShortageCount > 0 ? 0
                     : !row.ShortageCount.HasValue ? 1 : 2))
        {
            var line = SupplyText(row.Name, 64) + " (#" + row.ItemId.ToString(CultureInfo.InvariantCulture)
                + "): need " + SupplyCount(row.RequiredCount) + ", have " + SupplyCount(row.InventoryCount)
                + ", missing " + SupplyCount(row.ShortageCount)
                + (row.Reusable ? " · reusable, required once" : row.CountPerCraft <= 0
                    ? " · per-execution quantity unknown" : " · "
                        + row.CountPerCraft.ToString(CultureInfo.InvariantCulture) + " per execution") + ".\n";
            if (draft.Length + line.Length > SupplyDraftBodyLimit - 80) break;
            draft.Append(line);
            written++;
        }
        var omitted = Math.Max(0, recipe.IngredientCount - written);
        if (omitted > 0)
            draft.Append('\n').Append(omitted.ToString(CultureInfo.InvariantCulture))
                .Append(" additional ingredient rows omitted from this draft.");
        else if (recipe.IngredientCount == 0)
            draft.Append("No ingredient counts declared. Other crafting requirements may still apply.");
        body = draft.ToString();
        status = "";
        return true;
    }

    private static string SupplyCount(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
    private static string SupplyAge(long observedAt) => observedAt == 0 ? "not observed"
        : Math.Max(0L, (Now - observedAt) / 1000L).ToString(CultureInfo.InvariantCulture) + "s ago";
    private static string SupplyText(string value, int limit)
    {
        var plain = new string(value.Select(character => char.IsControl(character) ? ' ' : character).ToArray()).Trim();
        if (plain.Length <= limit) return plain;
        var end = limit - 1;
        if (char.IsHighSurrogate(plain[end - 1])) end--;
        return plain.Substring(0, end).TrimEnd() + "…";
    }
}
