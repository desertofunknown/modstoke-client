// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Globalization;
using System.Text;
using TMPro;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientTerminalView
{
    private RectTransform _deviceCatalogSurface = null!;
    private ScrollArea _deviceCatalogScroll = null!, _devicePlanScroll = null!;
    private TextMeshProUGUI _deviceCatalogSummary = null!, _deviceCatalogEmpty = null!, _devicePlanQuantity = null!;
    private TextMeshProUGUI _devicePlanTitle = null!, _devicePlanSource = null!, _devicePlanTotals = null!;
    private TextMeshProUGUI _devicePlanResources = null!, _devicePlanComparison = null!, _devicePlanNote = null!;
    private ButtonWidget _deviceCatalogSort = null!, _deviceCatalogFilter = null!, _deviceRecipeRefresh = null!;
    private ButtonWidget _devicePlanLess = null!, _devicePlanMore = null!, _devicePlanPin = null!, _devicePlanClear = null!;
    private ButtonWidget _devicePlanReview = null!, _devicePlanStatus = null!;
    private readonly DeviceCatalogRow[] _deviceCatalogRows = new DeviceCatalogRow[ClientDeviceApp.MaximumCatalogPages];
    private string _deviceCatalogOrder = "", _devicePlanScope = "";

    private sealed class DeviceCatalogRow
    {
        internal DeviceCatalogRow(ButtonWidget button, TextMeshProUGUI detail) { Button = button; Detail = detail; }
        internal ButtonWidget Button { get; }
        internal TextMeshProUGUI Detail { get; }
        internal int RecipeId { get; set; } = -1;
    }

    private void BuildDeviceCatalog()
    {
        _deviceCatalogSurface = Node(_deviceAppSurface!, "PrinterCatalogAndPlan");
        Place(_deviceCatalogSurface, 0f, 56f, WorkspaceWidth, 544f);
        var catalog = RoundedPanel(_deviceCatalogSurface, "ObservedCatalog", Surface, 0f, 0f, 648f, 544f).rectTransform;
        DeviceText(catalog, "CatalogHeading", "OBSERVED RECIPES", 20f, Muted, 22f, 18f, 604f, 28f);
        _deviceCatalogSummary = DeviceText(catalog, "CatalogScope", "", 19f, Muted, 22f, 52f, 604f, 42f);
        _deviceCatalogSort = Button(catalog, "SortRecipes", "Sort: Native page", 22f, 100f, 248f, 44f,
            () => _command("devices", "catalog-sort"));
        _deviceCatalogFilter = Button(catalog, "FilterRecipes", "All observed costs", 286f, 100f, 340f, 44f,
            () => _command("devices", "catalog-affordable"));
        _deviceCatalogScroll = Scroll(catalog, "RecipePages", 22f, 156f, 604f, 304f);
        _deviceCatalogEmpty = Text(_deviceCatalogScroll.Content, "NoRecipePages", "", 23f, Muted,
            10f, 16f, _deviceCatalogScroll.Width - 20f, 180f);
        for (var i = 0; i < _deviceCatalogRows.Length; i++)
        {
            var index = i;
            var button = Button(_deviceCatalogScroll.Content, "RecipeRow" + i, "", 0f, i * 92f,
                _deviceCatalogScroll.Width, 82f, () => SelectDeviceCatalogRow(index));
            Place(button.Label.rectTransform, 16f, 7f, _deviceCatalogScroll.Width - 32f, 32f);
            var detail = DeviceText(button.Image.transform, "RecipeObservation", "", 18f, Muted,
                16f, 44f, _deviceCatalogScroll.Width - 32f, 28f);
            _deviceCatalogRows[i] = new(button, detail);
            button.Image.gameObject.SetActive(false);
        }
        _deviceRecipePrevious = DeviceButton(catalog, "PreviousRecipe", "Previous", 22f, 478f, 150f, "recipes-prev");
        _deviceRecipeRefresh = DeviceButton(catalog, "ReadRecipePage", "Read page 1", 188f, 478f, 272f, "recipes-refresh");
        _deviceRecipeNext = DeviceButton(catalog, "NextRecipe", "Next", 476f, 478f, 150f, "recipes-next");

        var plan = RoundedPanel(_deviceCatalogSurface, "RecipePlan", Surface, 666f, 0f, 662f, 544f).rectTransform;
        DeviceText(plan, "PlanHeading", "LOCAL RECIPE PLAN", 20f, Muted, 22f, 18f, 618f, 28f);
        _devicePlanScroll = Scroll(plan, "RecipePlanDetails", 22f, 56f, 618f, 346f);
        _devicePlanTitle = Text(_devicePlanScroll.Content, "PlannedRecipe", "", 28f, Ink, 0f, 0f, _devicePlanScroll.Width, 70f, true);
        _devicePlanSource = Text(_devicePlanScroll.Content, "RecipeSource", "", 19f, Muted, 0f, 80f, _devicePlanScroll.Width, 50f);
        _devicePlanTotals = Text(_devicePlanScroll.Content, "PlanTotals", "", 23f, Ink, 0f, 140f, _devicePlanScroll.Width, 80f);
        _devicePlanResources = Text(_devicePlanScroll.Content, "PlanResin", "", 21f, Ink, 0f, 230f, _devicePlanScroll.Width, 100f);
        _devicePlanComparison = Text(_devicePlanScroll.Content, "ComparedRecipe", "", 21f, Mint, 0f, 340f, _devicePlanScroll.Width, 90f);
        _devicePlanNote = Text(_devicePlanScroll.Content, "PlanLimits", "", 19f, Muted, 0f, 440f, _devicePlanScroll.Width, 80f);
        _devicePlanLess = DeviceButton(plan, "LessPlannedPrints", "−", 22f, 414f, 64f, "plan-less");
        _devicePlanQuantity = DeviceText(plan, "PlanQuantity", "1 print", 21f, Ink, 102f, 426f, 94f, 28f);
        _devicePlanMore = DeviceButton(plan, "MorePlannedPrints", "+", 204f, 414f, 64f, "plan-more");
        _devicePlanPin = DeviceButton(plan, "PinRecipeComparison", "Pin comparison", 284f, 414f, 228f, "compare-recipe");
        _devicePlanClear = DeviceButton(plan, "ClearRecipeComparison", "Clear", 528f, 414f, 112f, "clear-comparison");
        _devicePlanReview = Button(plan, "ReviewPlannedRecipe", "Review recipe…", 22f, 478f, 302f, 50f, () =>
        {
            _command("devices", "prepare-recipe");
            SetDevicePane(false);
        });
        _devicePlanStatus = DeviceButton(plan, "ReadPlanPrinterStatus", "Read printer status", 340f, 478f, 300f, "status");
    }

    private void SelectDeviceCatalogRow(int index)
    {
        var id = _deviceCatalogRows[index].RecipeId;
        if (id >= 0) _command("devices", "catalog.select." + id.ToString(CultureInfo.InvariantCulture));
    }

    private void SetDeviceCatalogState(ClientDeviceAppSnapshot snapshot)
    {
        var catalog = snapshot.Catalog;
        var ready = snapshot.Available && !snapshot.Busy;
        var current = snapshot.Observation;
        _deviceCatalogSummary.text = catalog.ObservedCount + " saved page observations for this link · " + catalog.Entries.Count + " shown\n"
            + (snapshot.Busy ? "Waiting for a server reply; saved pages remain browsable."
                : catalog.AffordableOnly && current == null ? "Resin filter paused until current status is available."
                : "Pages load only when requested. This is not a complete live catalog.");
        _deviceCatalogSort.Label.text = "Sort: " + catalog.Sort;
        _deviceCatalogSort.Button.interactable = snapshot.Available && catalog.ObservedCount > 1;
        _deviceCatalogFilter.Label.text = catalog.AffordableOnly ? "Cost ≤ observed resin · on" : "All observed costs";
        _deviceCatalogFilter.Button.interactable = snapshot.Available && current != null && catalog.ObservedCount > 0;
        var order = new StringBuilder(catalog.Sort).Append('|').Append(catalog.AffordableOnly);
        for (var i = 0; i < _deviceCatalogRows.Length; i++)
        {
            var row = _deviceCatalogRows[i];
            var visible = i < catalog.Entries.Count;
            row.Button.Image.gameObject.SetActive(visible);
            if (!visible) { row.RecipeId = -1; continue; }
            var entry = catalog.Entries[i];
            row.RecipeId = entry.Recipe.Id;
            order.Append('|').Append(entry.Page).Append(':').Append(entry.Recipe.Id);
            var selected = catalog.Selected?.Recipe.Id == entry.Recipe.Id;
            var pinned = catalog.Comparison?.Recipe.Id == entry.Recipe.Id;
            row.Button.Label.text = (selected ? "Selected · " : pinned ? "Pinned · " : "") + entry.Recipe.Name;
            row.Detail.text = "Page " + entry.Page + " · " + DevicePlanNumber(entry.Recipe.Resin) + " resin · "
                + DevicePlanNumber(entry.Recipe.Seconds) + " s · " + DeviceRecipeAge(entry) + (entry.Fresh ? "" : " · saved");
            row.Button.Button.interactable = snapshot.Available;
        }
        _deviceCatalogEmpty.gameObject.SetActive(catalog.Entries.Count == 0);
        _deviceCatalogEmpty.text = catalog.ObservedCount > 0
            ? "No saved recipe cost fits the observed resin. Show all costs or read current printer status."
            : current == null ? "Read printer status, then request the first recipe page. Each page belongs to the current link."
            : "Read page 1 to start. Next requests one additional page; saved rows can be browsed locally.";
        _deviceCatalogScroll.Height(Math.Max(220f, catalog.Entries.Count * 92f));
        var orderKey = order.ToString();
        if (orderKey != _deviceCatalogOrder) { _deviceCatalogOrder = orderKey; _deviceCatalogScroll.Reset(); }
        _deviceRecipePrevious.Button.interactable = ready && catalog.CanReadPages && snapshot.RecipePage > 1;
        _deviceRecipeNext.Button.interactable = ready && catalog.CanReadPages && snapshot.MoreRecipes
            && snapshot.RecipePage < ClientDeviceApp.MaximumCatalogPages;
        _deviceRecipeRefresh.Label.text = "Read page " + snapshot.RecipePage;
        _deviceRecipeRefresh.Button.interactable = ready && catalog.CanReadPages;
        _devicePlanQuantity.text = catalog.PlanCount + (catalog.PlanCount == 1 ? " print" : " prints");
        _devicePlanLess.Button.interactable = snapshot.Available && catalog.Selected != null && catalog.PlanCount > 1;
        _devicePlanMore.Button.interactable = snapshot.Available && catalog.Selected != null && catalog.PlanCount < ClientDeviceApp.MaximumPlanCount;
        _devicePlanPin.Label.text = catalog.Selected != null && catalog.Selected.Recipe.Id == catalog.Comparison?.Recipe.Id
            ? "Unpin comparison" : "Pin comparison";
        _devicePlanPin.Button.interactable = snapshot.Available && catalog.Selected != null;
        _devicePlanClear.Button.interactable = snapshot.Available && catalog.Comparison != null;
        _devicePlanReview.Button.interactable = ready && snapshot.CanSelectRecipe && snapshot.Review.Length == 0;
        _devicePlanReview.Label.text = catalog.Selected is { Fresh: false } ? "Refresh recipe first"
            : catalog.Selected != null && current?.Recipe == catalog.Selected.Recipe.Id ? "Already selected" : "Review recipe…";
        _devicePlanStatus.Button.interactable = ready;
        SetDevicePlanDetails(snapshot);
    }

    private void SetDevicePlanDetails(ClientDeviceAppSnapshot snapshot)
    {
        var catalog = snapshot.Catalog;
        var entry = catalog.Selected;
        var recipe = entry?.Recipe;
        var title = recipe == null ? "Choose an observed recipe" : recipe.Name + " (#" + recipe.Id + ")";
        var source = entry == null ? "Recipe pages are saved only for this player session and printer link."
            : "Recipe page " + entry.Page + " · " + DeviceRecipeAge(entry)
                + (entry.Fresh ? " · less than 30 seconds old" : "\nSaved for planning; refresh before changing the printer.");
        var totalResin = (recipe?.Resin ?? 0) * catalog.PlanCount;
        var totalSeconds = (recipe?.Seconds ?? 0) * catalog.PlanCount;
        var totals = recipe == null ? "Choose a row to calculate resin and recipe time for 1–20 print requests."
            : catalog.PlanCount + " single-print request" + (catalog.PlanCount == 1 ? "" : "s") + "\n"
                + DevicePlanNumber(totalResin) + " resin · " + DevicePlanNumber(totalSeconds) + " recipe seconds\n"
                + "Per print: " + DevicePlanNumber(recipe.Resin) + " resin · " + DevicePlanNumber(recipe.Seconds) + " s";
        var resources = DevicePlanResources(snapshot, recipe, totalResin);
        var comparison = DevicePlanComparison(catalog, totalResin, totalSeconds);
        var note = "Local arithmetic only: no queue, resin reservation or item quantity is implied. Recipe seconds are not a completion estimate.\n"
            + (snapshot.ChangeAvailability.Length > 0 ? snapshot.ChangeAvailability
                : "Printer state and access are checked again for each confirmed change.") + "\n" + snapshot.Message;
        var changed = _devicePlanTitle.text != title || _devicePlanSource.text != source || _devicePlanTotals.text != totals
            || _devicePlanResources.text != resources || _devicePlanComparison.text != comparison || _devicePlanNote.text != note;
        if (!changed) return;
        _devicePlanTitle.text = title;
        _devicePlanSource.text = source;
        _devicePlanTotals.text = totals;
        _devicePlanResources.text = resources;
        _devicePlanComparison.text = comparison;
        _devicePlanNote.text = note;
        var y = 0f;
        PlaceText(_devicePlanTitle, 0f, ref y, _devicePlanScroll.Width, 10f);
        PlaceText(_devicePlanSource, 0f, ref y, _devicePlanScroll.Width, 14f);
        PlaceText(_devicePlanTotals, 0f, ref y, _devicePlanScroll.Width, 16f);
        PlaceText(_devicePlanResources, 0f, ref y, _devicePlanScroll.Width, 16f);
        PlaceText(_devicePlanComparison, 0f, ref y, _devicePlanScroll.Width, 16f);
        PlaceText(_devicePlanNote, 0f, ref y, _devicePlanScroll.Width, 10f);
        _devicePlanScroll.Height(y);
        var scope = snapshot.PairedId + ":" + recipe?.Id;
        if (scope != _devicePlanScope) { _devicePlanScope = scope; _devicePlanScroll.Reset(); }
    }

    private static string DevicePlanResources(ClientDeviceAppSnapshot snapshot, ClientDeviceRecipe? recipe, double total)
    {
        var observation = snapshot.Observation;
        if (observation == null) return "No current printer resin observation. Read status to compare this plan with available resin.";
        var text = "Printer resin: " + DevicePlanNumber(observation.Resin) + " / " + DevicePlanNumber(observation.Capacity)
            + " · " + (int)snapshot.ObservationAge + "s ago\nPrinter selects recipe #" + observation.Recipe
            + (snapshot.Catalog.Current is { Fresh: true } current ? " · " + current.Recipe.Name : "");
        if (recipe == null) return text;
        text += total > observation.Resin ? "\nAdditional resin needed: " + DevicePlanNumber(total - observation.Resin)
            : "\nObserved resin beyond this plan: " + DevicePlanNumber(observation.Resin - total);
        if (recipe.Resin > observation.Capacity) text += "\nOne print costs more than the observed printer capacity.";
        else if (total > observation.Capacity) text += "\nTotal plan cost exceeds one full printer reservoir.";
        return text;
    }

    private static string DevicePlanComparison(ClientDeviceCatalogSnapshot catalog, double resin, double seconds)
    {
        var comparison = catalog.Comparison;
        if (comparison == null) return "Pin a comparison recipe, then choose another row to compare the same number of print requests.";
        var text = "Comparison: " + comparison.Recipe.Name + " (#" + comparison.Recipe.Id + ") · " + DeviceRecipeAge(comparison);
        if (catalog.Selected == null || catalog.Selected.Recipe.Id == comparison.Recipe.Id)
            return text + "\nChoose a different observed recipe to compare it with this one.";
        var baseResin = comparison.Recipe.Resin * catalog.PlanCount;
        var baseSeconds = comparison.Recipe.Seconds * catalog.PlanCount;
        return text + "\nFor " + catalog.PlanCount + " print requests, the browsed recipe uses "
            + DeviceDifference(resin - baseResin, "resin") + " and " + DeviceDifference(seconds - baseSeconds, "recipe seconds") + ".";
    }

    private static string DeviceDifference(double value, string unit) => value == 0 ? "the same " + unit
        : DevicePlanNumber(Math.Abs(value)) + " " + (value < 0 ? "less " : "more ") + unit;

    private static string DevicePlanNumber(double value) => value >= 1000000
        ? value.ToString("0.##e+0", CultureInfo.InvariantCulture) : ClientDeviceApp.Number(value);

    private static string DeviceRecipeAge(ClientDeviceCatalogEntry entry) => entry.Age < 60 ? (int)entry.Age + "s old"
        : entry.Age < 3600 ? (int)(entry.Age / 60) + "m old" : (int)(entry.Age / 3600) + "h old";
}
