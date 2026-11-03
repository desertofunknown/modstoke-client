// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Modstoke.Sons;
using TMPro;
using UnityEngine;
using UnityEngine.Events;

namespace Modstoke.Client;

internal sealed partial class ClientTerminalView
{
    private RectTransform? _packSurface;
    private RectTransform _packDetailsPane = null!, _packRecipePane = null!, _packLoadoutPane = null!, _packAmmoRowsRoot = null!;
    private RectTransform _packMatPane = null!;
    private TMP_InputField _packSearch = null!;
    private TextMeshProUGUI _packScope = null!, _packPage = null!, _packEmpty = null!;
    private TextMeshProUGUI _packItemTitle = null!, _packItemType = null!, _packStock = null!;
    private TextMeshProUGUI _packStockDetail = null!, _packFacts = null!, _packEquipmentNote = null!;
    private TextMeshProUGUI _packDropMessage = null!, _packStowMessage = null!;
    private TextMeshProUGUI _packAmmoHeading = null!, _packAmmoStatus = null!, _packLoadoutSummary = null!;
    private TextMeshProUGUI _packRecipeTitle = null!, _packRecipePage = null!, _packQuantity = null!;
    private TextMeshProUGUI _packRecipeMessage = null!, _packRecipeIngredients = null!, _packRecipeMetadata = null!;
    private TextMeshProUGUI _packMatStatus = null!, _packMatIngredients = null!, _packMatComparisonTitle = null!;
    private TextMeshProUGUI _packMatComparisonMessage = null!, _packMatRequirements = null!;
    private ButtonWidget _packRefresh = null!, _packPrevious = null!, _packNext = null!;
    private ButtonWidget _packDrop = null!;
    private ButtonWidget _packDetailsTab = null!, _packRecipeTab = null!, _packLoadoutTab = null!, _packMatTab = null!;
    private ButtonWidget _packRecipePrevious = null!, _packRecipeNext = null!, _packLess = null!, _packMore = null!;
    private ButtonWidget _packMakes = null!, _packUses = null!, _packDraftSupplies = null!;
    private ButtonWidget _packMatRead = null!, _packMatRemember = null!;
    private ScrollArea _packDetailsScroll = null!, _packRecipeScroll = null!, _packMatScroll = null!;
    private readonly PackRow[] _packRows = new PackRow[ClientPackApp.PageSize];
    private readonly List<PackRow> _packAmmoRows = new();
    private readonly PackLoadoutRow[] _packLoadoutRows = new PackLoadoutRow[6];
    private long _packGeneration = long.MinValue;
    private long _packMatObservedAt;
    private int _packDetailsItem;
    private string _packRecipeSelection = "";
    private enum PackPane { Details, Equipment, Recipes, Mat }

    private sealed class PackLoadoutRow
    {
        internal PackLoadoutRow(TextMeshProUGUI name, ButtonWidget current, TextMeshProUGUI next, ButtonWidget stow)
        { Name = name; Current = current; Next = next; Stow = stow; }
        internal TextMeshProUGUI Name { get; }
        internal ButtonWidget Current { get; }
        internal TextMeshProUGUI Next { get; }
        internal ButtonWidget Stow { get; }
        internal ItemEquipmentSlot Slot { get; set; }
        internal int ItemId { get; set; }
    }

    private sealed class PackRow
    {
        internal PackRow(ButtonWidget button, TextMeshProUGUI detail) { Button = button; Detail = detail; }
        internal ButtonWidget Button { get; }
        internal TextMeshProUGUI Detail { get; }
        internal int ItemId { get; set; }
    }

    internal void BuildPackApp()
    {
        if (_packSurface != null) return;
        _packSurface = Node(_applicationLayer, "PackApp");
        Place(_packSurface, 56f, 176f, WorkspaceWidth, WorkspaceHeight);
        _packScope = PackText(_packSurface, "ObservationScope", "Open Pack to read local stock.",
            20f, Muted, 0f, 0f, 1120f, 54f);
        _packRefresh = PackButton(_packSurface, "RefreshPack", "Refresh", 1144f, 0f, 184f, "refresh");
        PackText(_packSurface, "SearchHeading", "SEARCH ITEM DEFINITIONS", 18f, Muted, 0f, 64f, 440f, 26f);
        _packSearch = FieldInput(_packSurface, 0f, 96f, 440f, 48f, ClientPackApp.MaximumSearchLength, false);
        _packSearch.textComponent.fontSize = 22f;
        var hint = PackText(_packSearch.textViewport, "SearchHint", "Name, type or ID · press Enter", 21f,
            Muted, 0f, 0f, 416f, 34f);
        hint.alignment = TextAlignmentOptions.MidlineLeft;
        _packSearch.placeholder = hint;
        _packSearch.onEndEdit.AddListener((UnityAction<string>)new Action<string>(value =>
            _edit("pack", Guid.Empty, "search", value)));

        _packDetailsTab = Button(_packSurface, "ItemDetailsTab", "Item & ammo", 458f, 64f, 204f, 48f,
            () => SetPackPane(PackPane.Details));
        _packLoadoutTab = Button(_packSurface, "EquippedTab", "Equipped", 680f, 64f, 204f, 48f,
            () => SetPackPane(PackPane.Equipment));
        _packRecipeTab = Button(_packSurface, "CraftingTab", "Crafting", 902f, 64f, 204f, 48f,
            () => SetPackPane(PackPane.Recipes));
        _packMatTab = Button(_packSurface, "MatTab", "Mat", 1124f, 64f, 204f, 48f,
            () => SetPackPane(PackPane.Mat));
        var list = RoundedPanel(_packSurface, "PublishedItems", Surface, 0f, 156f, 440f, 444f).rectTransform;
        _packEmpty = PackText(list, "Empty", "Waiting for published item definitions.", 24f, Muted,
            20f, 26f, 400f, 220f);
        for (var index = 0; index < _packRows.Length; index++)
        {
            var rowIndex = index;
            var button = Button(list, "Item" + index, "", 14f, 10f + index * 56f, 412f, 50f,
                () => SelectPackRow(rowIndex));
            Place(button.Label.rectTransform, 12f, 3f, 388f, 25f);
            button.Label.fontSize = 22f;
            var detail = PackText(button.Image.transform, "Stock", "", 17f, Muted, 12f, 28f, 388f, 21f);
            _packRows[index] = new(button, detail);
            button.Image.gameObject.SetActive(false);
        }
        _packPage = PackText(list, "Page", "", 19f, Muted, 20f, 350f, 400f, 27f);
        _packPrevious = PackButton(list, "PreviousPage", "Previous", 14f, 382f, 198f, "page-prev");
        _packNext = PackButton(list, "NextPage", "Next", 228f, 382f, 198f, "page-next");

        _packDetailsPane = RoundedPanel(_packSurface, "ItemDetails", Surface, 458f, 124f, 870f, 476f).rectTransform;
        _packItemTitle = PackText(_packDetailsPane, "ItemName", "Select an item", 28f, Ink, 20f, 14f, 830f, 52f, true);
        _packItemType = PackText(_packDetailsPane, "ItemType", "", 19f, Muted, 20f, 70f, 830f, 26f);
        _packDetailsScroll = Scroll(_packDetailsPane, "ItemFacts", 20f, 104f, 830f, 272f);
        _packStock = Text(_packDetailsScroll.Content, "StockSummary", "", 23f, Ink, 0f, 0f, _packDetailsScroll.Width, 64f, true);
        _packStockDetail = Text(_packDetailsScroll.Content, "StockDetail", "", 19f, Muted, 0f, 70f, _packDetailsScroll.Width, 64f);
        _packAmmoHeading = Text(_packDetailsScroll.Content, "AmmunitionHeading", "Compatible ammunition", 22f, Ink,
            0f, 140f, _packDetailsScroll.Width, 34f, true);
        _packAmmoStatus = Text(_packDetailsScroll.Content, "AmmunitionStatus", "", 19f, Muted,
            0f, 180f, _packDetailsScroll.Width, 70f);
        _packAmmoRowsRoot = Node(_packDetailsScroll.Content, "AmmunitionEntries");
        _packFacts = Text(_packDetailsScroll.Content, "EquipmentFacts", "", 21f, Ink, 0f, 140f, _packDetailsScroll.Width, 310f);
        _packEquipmentNote = Text(_packDetailsScroll.Content, "EquipmentLimits", "", 18f, Muted,
            0f, 460f, _packDetailsScroll.Width, 62f);
        _packDrop = Button(_packDetailsPane, "DropItem", "Drop one", 20f, 390f, 190f, 48f,
            () => _command("pack", "drop." + _packDetailsItem.ToString(CultureInfo.InvariantCulture)));
        _packDrop.Button.interactable = false;
        _packDropMessage = PackText(_packDetailsPane, "DropStatus", "Select an item to drop one.",
            18f, Muted, 230f, 390f, 620f, 74f);

        BuildPackLoadout();
        BuildPackMat();

        _packRecipePane = RoundedPanel(_packSurface, "CraftingPlan", Surface, 458f, 124f, 870f, 476f).rectTransform;
        _packMakes = PackButton(_packRecipePane, "MakesItem", "Makes this", 20f, 12f, 190f, "recipe-mode-makes");
        _packUses = PackButton(_packRecipePane, "UsesItem", "Uses this", 222f, 12f, 190f, "recipe-mode-uses");
        _packDraftSupplies = PackButton(_packRecipePane, "DraftSupplies", "Draft supplies", 630f, 12f, 220f, "draft-supplies");
        _packRecipeTitle = PackText(_packRecipePane, "RecipeName", "Select an item", 26f, Ink, 20f, 70f, 830f, 34f, true);
        _packRecipePrevious = PackButton(_packRecipePane, "PreviousRecipe", "Previous", 20f, 112f, 180f, "recipe-prev");
        _packRecipePage = PackText(_packRecipePane, "RecipePage", "", 20f, Muted, 216f, 124f, 438f, 30f);
        _packRecipeNext = PackButton(_packRecipePane, "NextRecipe", "Next", 670f, 112f, 180f, "recipe-next");
        _packLess = PackButton(_packRecipePane, "FewerExecutions", "−", 20f, 168f, 64f, "quantity-minus");
        _packQuantity = PackText(_packRecipePane, "Executions", "1 recipe execution", 22f, Ink, 100f, 180f, 670f, 32f);
        _packMore = PackButton(_packRecipePane, "MoreExecutions", "+", 786f, 168f, 64f, "quantity-plus");
        _packRecipeScroll = Scroll(_packRecipePane, "IngredientComparison", 20f, 230f, 830f, 182f);
        _packRecipeMessage = Text(_packRecipeScroll.Content, "ComparisonState", "", 22f, Ink, 0f, 0f, _packRecipeScroll.Width, 70f, true);
        _packRecipeIngredients = Text(_packRecipeScroll.Content, "Ingredients", "", 21f, Ink, 0f, 80f, _packRecipeScroll.Width, 220f);
        _packRecipeMetadata = Text(_packRecipeScroll.Content, "Metadata", "", 18f, Muted, 0f, 310f, _packRecipeScroll.Width, 90f);
        PackText(_packRecipePane, "PlanningLimits", "Planning counts only. Unlocks, container rules and output capacity may still apply.\n"
            + "Draft supplies opens a Notebook draft for you to review.",
            17f, Muted, 20f, 424f, 830f, 44f);
        SetPackPane(PackPane.Details);
        _packSurface.gameObject.SetActive(false);
    }

    private TextMeshProUGUI PackText(Transform parent, string name, string value, float size, Color color,
        float x, float y, float width, float height, bool bold = false)
    {
        var text = Text(parent, name, value, size, color, x, y, width, height, bold);
        text.overflowMode = TextOverflowModes.Ellipsis;
        return text;
    }

    private ButtonWidget PackButton(Transform parent, string name, string label, float x, float y,
        float width, string verb) => Button(parent, name, label, x, y, width, 48f, () => _command("pack", verb));

    private void SelectPackRow(int index)
    {
        var id = _packRows[index].ItemId;
        if (id > 0) _command("pack", "select." + id.ToString(CultureInfo.InvariantCulture));
    }

    private void SetPackPane(PackPane pane)
    {
        _packDetailsPane.gameObject.SetActive(pane == PackPane.Details);
        _packLoadoutPane.gameObject.SetActive(pane == PackPane.Equipment);
        _packRecipePane.gameObject.SetActive(pane == PackPane.Recipes);
        _packMatPane.gameObject.SetActive(pane == PackPane.Mat);
        _packDetailsTab.Label.text = pane == PackPane.Details ? "› Item & ammo" : "Item & ammo";
        _packLoadoutTab.Label.text = pane == PackPane.Equipment ? "› Equipped" : "Equipped";
        _packRecipeTab.Label.text = pane == PackPane.Recipes ? "› Crafting" : "Crafting";
        _packMatTab.Label.text = pane == PackPane.Mat ? "› Mat" : "Mat";
    }

    internal void SetPackAppVisible(bool visible)
    {
        if (_disposed || _packSurface == null) return;
        _packSurface.gameObject.SetActive(visible);
    }

    internal void SetPackAppState(ClientPackAppSnapshot snapshot)
    {
        if (_disposed || _packSurface == null) return;
        if (_packGeneration != snapshot.Generation)
        {
            _packGeneration = snapshot.Generation;
            _packSearch.SetTextWithoutNotify(snapshot.Search);
            _packDetailsItem = 0;
            _packRecipeSelection = "";
            _packDetailsScroll.Reset();
            _packRecipeScroll.Reset();
            _packMatScroll.Reset();
            _packMatObservedAt = 0;
            SetPackPane(PackPane.Details);
        }
        else if (!_packSearch.isFocused && _packSearch.text != snapshot.Search)
            _packSearch.SetTextWithoutNotify(snapshot.Search);
        _packRefresh.Button.interactable = snapshot.Available;
        _packSearch.interactable = snapshot.CatalogStatus == ClientPackReadStatus.Ready;
        _packScope.text = snapshot.CatalogStatus != ClientPackReadStatus.Ready ? snapshot.CatalogMessage
            : snapshot.PublishedItems + " / " + snapshot.ReportedItems + " item definitions"
                + (snapshot.CatalogTruncated ? " · catalog coverage is limited" : " · catalog, not a complete pack listing")
                + "\n" + (snapshot.ObservedAt == 0 ? snapshot.InventoryMessage
                    : "Displayed stock observed " + PackAge(snapshot.ObservedAt) + " · refreshes while open");
        _packScope.color = snapshot.InventoryStatus is ClientPackReadStatus.Unavailable or ClientPackReadStatus.Uncertain
            or ClientPackReadStatus.Partial ? Amber : Muted;
        _packPage.text = "Page " + snapshot.Page + " / " + snapshot.PageCount + " · " + snapshot.MatchedItems + " matches";
        _packPrevious.Button.interactable = snapshot.Page > 1;
        _packNext.Button.interactable = snapshot.Page < snapshot.PageCount;
        _packEmpty.gameObject.SetActive(snapshot.Items.Count == 0);
        _packEmpty.text = snapshot.CatalogStatus != ClientPackReadStatus.Ready
            ? "Item definitions are unavailable. Open Pack again after the world has loaded."
            : "No published item definitions match this search. Try a shorter name, type or item ID.";
        for (var i = 0; i < _packRows.Length; i++)
        {
            var row = _packRows[i];
            var shown = i < snapshot.Items.Count;
            row.Button.Image.gameObject.SetActive(shown);
            if (!shown) { row.ItemId = 0; continue; }
            var item = snapshot.Items[i];
            row.ItemId = item.Id;
            var selected = item.Id == snapshot.SelectedItemId;
            row.Button.Label.text = (selected ? "› " : "") + item.Name;
            row.Button.Image.color = selected ? Raised : Surface;
            row.Button.Button.interactable = true;
            row.Detail.text = "Have " + PackCount(item.Count) + " · " + PackOwned(item.Owned)
                + " · " + PackEquipped(item.Equipped);
            row.Detail.color = item.Status == ClientPackReadStatus.Ready ? Muted : Amber;
        }
        SetPackItemDetails(snapshot);
        SetPackLoadout(snapshot);
        SetPackRecipeState(snapshot);
        SetPackMatState(snapshot);
    }

    private void SetPackItemDetails(ClientPackAppSnapshot snapshot)
    {
        var item = snapshot.Selected;
        var stock = snapshot.Items.FirstOrDefault(row => row.Id == snapshot.SelectedItemId);
        _packItemTitle.text = item?.Name ?? "Select an item";
        _packItemType.text = item == null ? "Read-only item information" : "Item " + item.Id + " · " + item.Type;
        _packStock.text = stock == null ? "No inventory reading" : "Have " + PackCount(stock.Count)
            + "  ·  " + PackOwned(stock.Owned) + "  ·  " + PackEquipped(stock.Equipped);
        _packStock.color = stock?.Status == ClientPackReadStatus.Ready ? Mint : Amber;
        _packStockDetail.text = stock is { Detail.Length: > 0 } ? stock.Detail : snapshot.InventoryMessage;
        var facts = new StringBuilder();
        if (item != null)
            foreach (var fact in item.Facts) facts.Append(fact.Label).Append(": ").Append(fact.Value).Append('\n');
        _packFacts.text = facts.ToString().TrimEnd();
        _packEquipmentNote.text = item?.EquipmentDetail ?? "Equipment details will appear for the selected item.";
        _packEquipmentNote.color = item?.EquipmentStatus is ClientPackReadStatus.Uncertain or ClientPackReadStatus.Unavailable
            ? Amber : Muted;
        _packDrop.Button.interactable = snapshot.CanDropSelected;
        _packDropMessage.text = snapshot.DropMessage;
        _packDropMessage.color = snapshot.DropNeedsAttention ? Amber : Muted;
        var y = 0f;
        PlaceText(_packStock, 0f, ref y, _packDetailsScroll.Width, 10f);
        PlaceText(_packStockDetail, 0f, ref y, _packDetailsScroll.Width, 18f);
        SetPackAmmunition(snapshot.Ammunition, ref y);
        PlaceText(_packFacts, 0f, ref y, _packDetailsScroll.Width, 14f);
        PlaceText(_packEquipmentNote, 0f, ref y, _packDetailsScroll.Width, 4f);
        _packDetailsScroll.Height(y);
        if (_packDetailsItem != snapshot.SelectedItemId)
        {
            _packDetailsItem = snapshot.SelectedItemId;
            _packDetailsScroll.Reset();
        }
    }

    private void SetPackRecipeState(ClientPackAppSnapshot snapshot)
    {
        var recipe = snapshot.Recipe;
        var present = recipe.Count > 0;
        _packMakes.Label.text = recipe.Mode == ClientPackRecipeMode.MakesItem ? "› Makes this" : "Makes this";
        _packUses.Label.text = recipe.Mode == ClientPackRecipeMode.UsesItem ? "› Uses this" : "Uses this";
        _packMakes.Button.interactable = _packUses.Button.interactable = snapshot.Selected != null;
        _packDraftSupplies.Button.interactable = present && snapshot.Available;
        _packRecipeTitle.text = present ? recipe.Name : snapshot.Selected?.Name ?? "Select an item";
        _packRecipePage.text = (present ? "Match " + recipe.Index + " / " + recipe.Count : "No confirmed match")
            + " · section " + recipe.SourcePage + " / " + recipe.SourcePageCount;
        _packQuantity.text = recipe.Executions.ToString(CultureInfo.InvariantCulture)
            + (recipe.Executions == 1 ? " recipe execution" : " recipe executions");
        _packRecipePrevious.Button.interactable = recipe.CanPrevious;
        _packRecipeNext.Button.interactable = recipe.CanNext;
        _packRecipePrevious.Label.text = recipe.Index > 1 ? "Previous" : "Prev section";
        _packRecipeNext.Label.text = recipe.Index < recipe.Count ? "Next" : "Next section";
        _packLess.Button.interactable = present && recipe.Executions > 1;
        _packMore.Button.interactable = present && recipe.Executions < ClientPackApp.MaximumRecipeExecutions;
        _packRecipeMessage.text = recipe.Message;
        _packRecipeMessage.color = recipe.Status is ClientPackReadStatus.Unavailable or ClientPackReadStatus.Uncertain
            ? Amber : recipe.CountsCovered == true ? Mint : Ink;
        var ingredients = new StringBuilder();
        foreach (var row in recipe.Ingredients)
        {
            ingredients.Append(row.Name).Append("  ·  #").Append(row.ItemId).Append('\n')
                .Append("Need ").Append(PackCount(row.RequiredCount)).Append("   Have ").Append(PackCount(row.InventoryCount))
                .Append("   Missing ").Append(PackCount(row.ShortageCount)).Append('\n')
                .Append(row.Reusable ? "Reusable · required once" : "Consumed · " + row.CountPerCraft.ToString(CultureInfo.InvariantCulture) + " per execution")
                .Append("\n\n");
        }
        if (present && recipe.Ingredients.Count == 0)
            ingredients.Append("This recipe declares no ingredient rows. Other crafting requirements may still apply.");
        if (recipe.IngredientCount > recipe.Ingredients.Count)
            ingredients.Append(recipe.IngredientCount - recipe.Ingredients.Count).Append(" additional ingredient entries are not shown.");
        _packRecipeIngredients.text = ingredients.ToString().TrimEnd();
        _packRecipeMetadata.text = !present ? recipe.Coverage
            : recipe.Coverage + "\nRecipe observed " + PackAge(recipe.MetadataObservedAt)
                + (recipe.StockObservedAt == 0 ? " · ingredient stock unavailable" : " · stock observed " + PackAge(recipe.StockObservedAt))
                + "\nEquipped stock included; pending equipment excluded."
                + (recipe.Mode == ClientPackRecipeMode.UsesItem && recipe.DeclaredResults.Length > 0
                    ? "\nDeclared results: " + recipe.DeclaredResults : "")
                + (recipe.UsesContainerData ? " Container contents may affect the result." : "");
        var y = 0f;
        PlaceText(_packRecipeMessage, 0f, ref y, _packRecipeScroll.Width, 16f);
        PlaceText(_packRecipeIngredients, 0f, ref y, _packRecipeScroll.Width, 16f);
        PlaceText(_packRecipeMetadata, 0f, ref y, _packRecipeScroll.Width, 4f);
        _packRecipeScroll.Height(y);
        var key = snapshot.SelectedItemId + ":" + recipe.Mode + ":" + recipe.SourcePage + ":" + recipe.Index;
        if (_packRecipeSelection != key)
        {
            _packRecipeSelection = key;
            _packRecipeScroll.Reset();
        }
    }

    private void BuildPackMat()
    {
        _packMatPane = RoundedPanel(_packSurface!, "CraftingMat", Surface, 458f, 124f, 870f, 476f).rectTransform;
        PackText(_packMatPane, "MatHeading", "Mat snapshot", 28f, Ink, 20f, 14f, 600f, 40f, true);
        _packMatRead = PackButton(_packMatPane, "ReadMat", "Read now", 642f, 14f, 208f, "mat-read");
        _packMatRemember = PackButton(_packMatPane, "RememberMat", "Remember mat: Off", 20f, 70f, 300f, "mat-remember");
        PackText(_packMatPane, "RememberMatHint", "Enable this, open your inventory, then return to Pack.",
            18f, Muted, 338f, 72f, 512f, 44f);
        _packMatStatus = PackText(_packMatPane, "MatStatus", "", 19f, Muted, 20f, 132f, 830f, 54f);
        _packMatScroll = Scroll(_packMatPane, "MatContents", 20f, 196f, 830f, 216f);
        _packMatIngredients = Text(_packMatScroll.Content, "MatIngredients", "", 21f, Ink,
            0f, 0f, _packMatScroll.Width, 80f);
        _packMatComparisonTitle = Text(_packMatScroll.Content, "MatComparisonTitle", "Recipe comparison", 23f, Ink,
            0f, 90f, _packMatScroll.Width, 40f, true);
        _packMatComparisonMessage = Text(_packMatScroll.Content, "MatComparisonStatus", "", 19f, Muted,
            0f, 140f, _packMatScroll.Width, 64f);
        _packMatRequirements = Text(_packMatScroll.Content, "MatRequirements", "", 21f, Ink,
            0f, 214f, _packMatScroll.Width, 100f);
        PackText(_packMatPane, "MatLimits", "Mat and recipe are separate observations. The comparison covers ingredient counts only.",
            17f, Muted, 20f, 424f, 830f, 44f);
    }

    private void SetPackMatState(ClientPackAppSnapshot snapshot)
    {
        var mat = snapshot.Mat;
        var comparison = snapshot.MatComparison;
        _packMatRead.Button.interactable = snapshot.Available && !mat.Pending;
        _packMatRemember.Button.interactable = snapshot.Available;
        _packMatRemember.Label.text = snapshot.RememberMat ? "Remember mat: On" : "Remember mat: Off";
        _packMatStatus.text = mat.Message + (mat.ObservedAt == 0 ? "" : "\n"
            + (mat.FromOpenInventory ? "Remembered from inventory" : "Read now") + " · " + PackAge(mat.ObservedAt)
            + " · " + mat.Ingredients.Count + (mat.Truncated ? " / " + mat.TotalRows : "") + " entries");
        _packMatStatus.color = mat.Status is ClientPackReadStatus.Unavailable or ClientPackReadStatus.Partial
            or ClientPackReadStatus.Uncertain ? Amber : Muted;
        var ingredients = new StringBuilder();
        foreach (var row in mat.Ingredients)
            ingredients.Append(row.Name).Append("  × ")
                .Append(row.Count < 0 ? "unknown" : row.Count.ToString(CultureInfo.InvariantCulture))
                .Append(row.Reusable ? " · reusable" : "").Append('\n');
        if (mat.Ingredients.Count == 0)
            ingredients.Append(mat.Status == ClientPackReadStatus.Empty ? "No ingredient entries in this reading." : "No mat observation yet.");
        _packMatIngredients.text = ingredients.ToString().TrimEnd();
        _packMatComparisonTitle.text = comparison.RecipeName.Length == 0 ? "Recipe comparison"
            : comparison.RecipeName + " · " + comparison.Executions
                + (comparison.Executions == 1 ? " execution" : " executions");
        _packMatComparisonMessage.text = comparison.Message
            + (comparison.RecipeObservedAt == 0 ? "" : "\nRecipe observed " + PackAge(comparison.RecipeObservedAt));
        _packMatComparisonMessage.color = comparison.Status is ClientPackReadStatus.Unavailable
            or ClientPackReadStatus.Uncertain or ClientPackReadStatus.Partial ? Amber
            : comparison.CountsCovered == true ? Mint : Muted;
        var requirements = new StringBuilder();
        foreach (var row in comparison.Ingredients)
        {
            requirements.Append(row.Name).Append('\n')
                .Append("Required ").Append(PackCount(row.RequiredCount)).Append("   On mat ").Append(PackCount(row.MatCount))
                .Append("   Missing ").Append(PackCount(row.ShortageCount))
                .Append(row.Reusable ? " · reusable, required once" : "").Append("\n\n");
        }
        if (comparison.IngredientCount > comparison.Ingredients.Count)
            requirements.Append(comparison.IngredientCount - comparison.Ingredients.Count)
                .Append(" additional recipe ingredients are not shown.");
        _packMatRequirements.text = requirements.ToString().TrimEnd();
        var y = 0f;
        PlaceText(_packMatIngredients, 0f, ref y, _packMatScroll.Width, 18f);
        PlaceText(_packMatComparisonTitle, 0f, ref y, _packMatScroll.Width, 10f);
        PlaceText(_packMatComparisonMessage, 0f, ref y, _packMatScroll.Width, 14f);
        PlaceText(_packMatRequirements, 0f, ref y, _packMatScroll.Width, 4f);
        _packMatScroll.Height(y);
        if (_packMatObservedAt != mat.ObservedAt)
        {
            _packMatObservedAt = mat.ObservedAt;
            _packMatScroll.Reset();
        }
    }

    private void BuildPackLoadout()
    {
        _packLoadoutPane = RoundedPanel(_packSurface!, "EquippedItems", Surface, 458f, 124f, 870f, 476f).rectTransform;
        _packLoadoutSummary = PackText(_packLoadoutPane, "EquipmentSummary", "Waiting for equipment readings.",
            20f, Muted, 20f, 14f, 830f, 44f);
        PackText(_packLoadoutPane, "CurrentHeading", "CURRENT", 18f, Muted, 170f, 64f, 310f, 24f);
        PackText(_packLoadoutPane, "PendingHeading", "PENDING", 18f, Muted, 500f, 64f, 200f, 24f);
        var names = new[] { "Right hand", "Left hand", "Chest", "Feet", "Full body", "Eyes" };
        for (var i = 0; i < _packLoadoutRows.Length; i++)
        {
            var index = i;
            var name = PackText(_packLoadoutPane, "Slot" + i, names[i], 21f, Muted,
                20f, 102f + i * 50f, 140f, 32f);
            var current = Button(_packLoadoutPane, "CurrentItem" + i, "Unknown", 170f, 90f + i * 50f, 310f, 44f,
                () => BrowsePackItem(_packLoadoutRows[index].ItemId));
            current.Label.fontSize = 21f;
            var next = PackText(_packLoadoutPane, "PendingItem" + i, "Unknown", 21f, Muted,
                500f, 102f + i * 50f, 200f, 32f);
            var stow = Button(_packLoadoutPane, "StowItem" + i, "Put away", 720f, 90f + i * 50f, 130f, 44f,
                () =>
                {
                    var row = _packLoadoutRows[index];
                    _command("pack", "stow." + ((int)row.Slot).ToString(CultureInfo.InvariantCulture)
                        + "." + row.ItemId.ToString(CultureInfo.InvariantCulture));
                });
            stow.Label.fontSize = 20f;
            stow.Button.interactable = false;
            _packLoadoutRows[i] = new(name, current, next, stow);
        }
        _packStowMessage = PackText(_packLoadoutPane, "StowStatus", ClientPackAppSnapshot.DefaultStowMessage,
            18f, Muted, 20f, 398f, 830f, 64f);
    }

    private void SetPackLoadout(ClientPackAppSnapshot snapshot)
    {
        var loadout = snapshot.Loadout;
        _packLoadoutSummary.text = loadout.Message
            + (loadout.ObservedAt == 0 ? "" : "\nObserved " + PackAge(loadout.ObservedAt));
        _packLoadoutSummary.color = loadout.Status is ClientPackReadStatus.Partial or ClientPackReadStatus.Unavailable
            ? Amber : Muted;
        _packStowMessage.text = snapshot.StowMessage;
        _packStowMessage.color = snapshot.StowNeedsAttention ? Amber : Muted;
        for (var i = 0; i < _packLoadoutRows.Length; i++)
        {
            var row = _packLoadoutRows[i];
            var entry = i < loadout.Slots.Count ? loadout.Slots[i] : null;
            if (entry != null) row.Name.text = entry.Name;
            row.Slot = entry?.Slot ?? default;
            row.ItemId = entry?.CurrentItemId ?? 0;
            row.Current.Label.text = entry?.CurrentName ?? "Unknown";
            row.Current.Button.interactable = entry?.CanBrowseCurrent == true;
            row.Stow.Button.interactable = snapshot.CanStowEquipment && entry?.CanStowCurrent == true;
            row.Next.text = entry?.NextName ?? "Unknown";
            row.Next.color = entry?.NextFound == true ? Amber : Muted;
        }
    }

    private void SetPackAmmunition(ClientPackAmmunitionSnapshot snapshot, ref float y)
    {
        _packAmmoStatus.text = snapshot.Message
            + (snapshot.Truncated ? "\nShowing " + snapshot.Entries.Count + " of " + PackCount(snapshot.ReportedCount) + " entries." : "")
            + (snapshot.StockObservedAt > 0 ? "\nStock observed " + PackAge(snapshot.StockObservedAt) + ". Magazine state is not shown." : "");
        _packAmmoStatus.color = snapshot.Status is ClientPackReadStatus.Partial or ClientPackReadStatus.Unavailable
            or ClientPackReadStatus.Uncertain ? Amber : Muted;
        PlaceText(_packAmmoHeading, 0f, ref y, _packDetailsScroll.Width, 8f);
        PlaceText(_packAmmoStatus, 0f, ref y, _packDetailsScroll.Width, 10f);
        while (_packAmmoRows.Count < snapshot.Entries.Count)
        {
            var index = _packAmmoRows.Count;
            var button = Button(_packAmmoRowsRoot, "Ammo" + index, "", 0f, index * 58f, _packDetailsScroll.Width, 50f,
                () => BrowsePackItem(_packAmmoRows[index].ItemId));
            Place(button.Label.rectTransform, 12f, 3f, _packDetailsScroll.Width - 24f, 25f);
            button.Label.fontSize = 21f;
            var detail = PackText(button.Image.transform, "Stock", "", 17f, Muted,
                12f, 28f, _packDetailsScroll.Width - 24f, 21f);
            _packAmmoRows.Add(new(button, detail));
        }
        for (var i = 0; i < _packAmmoRows.Count; i++)
        {
            var row = _packAmmoRows[i];
            var shown = i < snapshot.Entries.Count;
            row.Button.Image.gameObject.SetActive(shown);
            if (!shown) { row.ItemId = 0; continue; }
            var entry = snapshot.Entries[i];
            row.ItemId = entry.ItemId.GetValueOrDefault();
            row.Button.Label.text = entry.Name;
            row.Button.Button.interactable = entry.CanBrowse;
            row.Detail.text = "Have " + PackCount(entry.Count) + " · " + PackOwned(entry.Owned)
                + (entry.CanBrowse ? " · view item" : "");
            row.Detail.color = entry.Status == ClientPackReadStatus.Ready ? Muted : Amber;
        }
        Place(_packAmmoRowsRoot, 0f, y, _packDetailsScroll.Width, snapshot.Entries.Count * 58f);
        y += snapshot.Entries.Count * 58f + 18f;
    }

    private void BrowsePackItem(int itemId)
    {
        if (itemId <= 0) return;
        _command("pack", "browse." + itemId.ToString(CultureInfo.InvariantCulture));
        SetPackPane(PackPane.Details);
    }

    private static string PackCount(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "—";
    private static string PackOwned(bool? value) => value.HasValue ? value.Value ? "owned" : "not owned" : "ownership unknown";
    private static string PackEquipped(bool? value) => value.HasValue ? value.Value ? "equipped" : "not equipped" : "equipment unknown";
    private static string PackAge(long observedAt) => observedAt == 0 ? "not yet"
        : Math.Max(0L, (Environment.TickCount64 - observedAt) / 1000L).ToString(CultureInfo.InvariantCulture) + "s ago";
}
