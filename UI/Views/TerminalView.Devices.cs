// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Globalization;
using TMPro;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientTerminalView
{
    private RectTransform? _deviceAppSurface;
    private RectTransform _deviceOverview = null!;
    private TextMeshProUGUI _deviceCardTitle = null!, _deviceCardDetail = null!, _deviceCardPage = null!;
    private TextMeshProUGUI _deviceNativeState = null!, _deviceNativeResources = null!, _deviceNativeProgress = null!;
    private TextMeshProUGUI _deviceObservationAge = null!, _deviceRecipeTitle = null!, _deviceRecipeCost = null!;
    private TextMeshProUGUI _deviceRecipePage = null!, _deviceAppMessage = null!, _deviceReceipt = null!, _deviceReview = null!;
    private ButtonWidget _devicePair = null!, _deviceScan = null!, _devicePrevious = null!, _deviceNext = null!;
    private ButtonWidget _deviceStatus = null!, _deviceUnpair = null!, _deviceRecipes = null!;
    private ButtonWidget _deviceRecipePrevious = null!, _deviceRecipeNext = null!, _deviceSelectRecipe = null!;
    private ButtonWidget _devicePrint = null!, _deviceConfirm = null!, _deviceCancel = null!;
    private ButtonWidget _deviceOverviewTab = null!, _deviceCatalogTab = null!;
    private string _deviceConfirmVerb = "";
    private ScrollArea _deviceRequestScroll = null!;

    internal void BuildDeviceApp()
    {
        if (_deviceAppSurface != null) return;
        _deviceAppSurface = Node(_applicationLayer, "DevicesApp");
        Place(_deviceAppSurface, 56f, 176f, WorkspaceWidth, WorkspaceHeight);
        _deviceOverviewTab = Button(_deviceAppSurface, "PrinterTab", "Printer", 0f, 0f, 236f, 44f,
            () => SetDevicePane(false));
        _deviceCatalogTab = Button(_deviceAppSurface, "PrinterCatalogTab", "Recipes & plan", 252f, 0f, 304f, 44f,
            () => SetDevicePane(true));
        DeviceText(_deviceAppSurface, "PrinterScope", "Native printer observations · each change reviewed once",
            20f, Muted, 578f, 9f, 750f, 30f);
        _deviceOverview = Node(_deviceAppSurface, "PrinterOverview");
        Place(_deviceOverview, 0f, 56f, WorkspaceWidth, 544f);
        var nearby = RoundedPanel(_deviceOverview, "NearbyPrinter", Surface, 0f, 0f, 326f, 544f).rectTransform;
        DeviceText(nearby, "NearbyHeading", "NEARBY DEVICES", 20f, Muted, 22f, 18f, 282f, 28f);
        _deviceCardTitle = DeviceText(nearby, "PrinterName", "Find a printer", 28f, Ink, 22f, 58f, 282f, 68f, true);
        _deviceCardDetail = DeviceText(nearby, "PrinterDetails", "", 20f, Muted, 22f, 132f, 282f, 120f);
        _devicePair = DeviceButton(nearby, "PairPrinter", "Pair this printer", 22f, 266f, 282f, "pair");
        _deviceScan = DeviceButton(nearby, "ScanPrinters", "Scan nearby", 22f, 326f, 282f, "scan");
        _devicePrevious = DeviceButton(nearby, "PreviousPrinter", "Previous", 22f, 386f, 133f, "scan-prev");
        _deviceNext = DeviceButton(nearby, "NextPrinter", "Next", 171f, 386f, 133f, "scan-next");
        _deviceCardPage = DeviceText(nearby, "PrinterPage", "", 19f, Muted, 22f, 446f, 282f, 28f);
        DeviceText(nearby, "PairingNote", "Stay near the printer. Server ownership does not grant player access.",
            19f, Muted, 22f, 486f, 282f, 50f);

        var printer = RoundedPanel(_deviceOverview, "PairedPrinter", Surface, 342f, 0f, 476f, 324f).rectTransform;
        DeviceText(printer, "StatusHeading", "LINKED PRINTER", 20f, Muted, 22f, 18f, 432f, 28f);
        _deviceNativeState = DeviceText(printer, "NativeState", "Not observed", 28f, Mint, 22f, 58f, 432f, 42f, true);
        _deviceNativeResources = DeviceText(printer, "Resin", "", 20f, Ink, 22f, 108f, 432f, 78f);
        _deviceNativeProgress = DeviceText(printer, "NativeProgress", "", 19f, Muted, 22f, 192f, 432f, 30f);
        _deviceObservationAge = DeviceText(printer, "ObservationAge", "", 18f, Muted, 22f, 226f, 432f, 26f);
        _deviceStatus = DeviceButton(printer, "ReadPrinter", "Read status", 22f, 262f, 208f, "status");
        _deviceUnpair = DeviceButton(printer, "UnpairPrinter", "Unpair", 246f, 262f, 208f, "unpair");

        var recipe = RoundedPanel(_deviceOverview, "PrinterRecipe", Surface, 834f, 0f, 494f, 324f).rectTransform;
        DeviceText(recipe, "RecipeHeading", "BROWSED RECIPE", 20f, Muted, 22f, 18f, 450f, 28f);
        _deviceRecipeTitle = DeviceText(recipe, "RecipeName", "Load recipes", 28f, Ink, 22f, 58f, 450f, 68f, true);
        _deviceRecipeCost = DeviceText(recipe, "RecipeCost", "", 21f, Muted, 22f, 134f, 450f, 72f);
        _deviceRecipePage = DeviceText(recipe, "RecipePage", "", 19f, Muted, 22f, 218f, 450f, 30f);
        _deviceRecipes = DeviceButton(recipe, "LoadRecipes", "Read first page", 22f, 262f, 214f, "recipes");
        _deviceSelectRecipe = DeviceButton(recipe, "PrepareRecipe", "Select recipe…", 252f, 262f, 220f, "prepare-recipe");

        var change = RoundedPanel(_deviceOverview, "PrinterActions", Surface, 342f, 340f, 986f, 204f).rectTransform;
        _deviceRequestScroll = Scroll(change, "RequestDetails", 22f, 12f, 942f, 118f);
        _deviceAppMessage = Text(_deviceRequestScroll.Content, "DeviceMessage", "", 21f, Ink, 0f, 0f, _deviceRequestScroll.Width, 30f);
        _deviceReceipt = Text(_deviceRequestScroll.Content, "InvocationReceipt", "", 19f, Muted, 0f, 36f, _deviceRequestScroll.Width, 48f);
        _deviceReview = Text(_deviceRequestScroll.Content, "ReviewChange", "", 20f, Amber, 0f, 88f, _deviceRequestScroll.Width, 54f);
        _devicePrint = DeviceButton(change, "PreparePrint", "Prepare one print…", 22f, 142f, 302f, "prepare-print");
        _deviceConfirm = Button(change, "ConfirmDeviceChange", "Confirm change", 340f, 142f, 302f, 50f,
            () => { if (_deviceConfirmVerb.Length > 0) _command("devices", _deviceConfirmVerb); });
        _deviceCancel = DeviceButton(change, "CancelDeviceChange", "Cancel", 658f, 142f, 306f, "cancel");
        BuildDeviceCatalog();
        SetDevicePane(false);
        _deviceAppSurface.gameObject.SetActive(false);
    }

    private TextMeshProUGUI DeviceText(Transform parent, string name, string value, float size, Color color,
        float x, float y, float width, float height, bool bold = false)
    {
        var text = Text(parent, name, value, size, color, x, y, width, height, bold);
        text.overflowMode = TextOverflowModes.Ellipsis;
        return text;
    }

    private ButtonWidget DeviceButton(Transform parent, string name, string label, float x, float y,
        float width, string verb) => Button(parent, name, label, x, y, width, 50f, () => _command("devices", verb));

    private void SetDevicePane(bool catalog)
    {
        _deviceOverview.gameObject.SetActive(!catalog);
        _deviceCatalogSurface.gameObject.SetActive(catalog);
        _deviceOverviewTab.Label.text = catalog ? "Printer" : "Printer · selected";
        _deviceCatalogTab.Label.text = catalog ? "Recipes & plan · selected" : "Recipes & plan";
    }

    internal void SetDeviceAppVisible(bool visible)
    {
        if (_disposed || _deviceAppSurface == null) return;
        _deviceAppSurface.gameObject.SetActive(visible);
        if (!visible) _deviceConfirmVerb = "";
    }

    internal void SetDeviceAppState(ClientDeviceAppSnapshot snapshot)
    {
        if (_disposed || _deviceAppSurface == null) return;
        var ready = snapshot.Available && !snapshot.Busy;
        var device = snapshot.Device;
        _deviceCardTitle.text = device?.Type ?? "No current device";
        _deviceCardDetail.text = device == null ? "Scan nearby to observe a printer. Device cards expire after 30 seconds."
            : device.State + " · " + ClientDeviceApp.Number(device.Distance) + " m\n"
                + (device.Owned ? "Server-owned components" : "Native ownership unavailable") + "\nDevice " + device.Id;
        _deviceCardPage.text = "Device page " + snapshot.DevicePage;
        _devicePair.Label.text = device != null && device.Id == snapshot.PairedId ? "Already linked" : "Pair this printer";
        _devicePair.Button.interactable = ready && device != null && device.Id != snapshot.PairedId;
        _deviceScan.Button.interactable = ready;
        _devicePrevious.Button.interactable = ready && snapshot.DevicePage > 1;
        _deviceNext.Button.interactable = ready && snapshot.MoreDevices && snapshot.DevicePage < 64;
        var observation = snapshot.Observation;
        _deviceNativeState.text = observation?.State ?? "Not observed";
        _deviceNativeState.color = observation == null ? Muted : observation.State == "idle" ? Mint : Amber;
        _deviceNativeResources.text = observation == null ? "Read status for current resin and recipe."
            : "Resin " + ClientDeviceApp.Number(observation.Resin) + " / " + ClientDeviceApp.Number(observation.Capacity)
                + "\nPrinter recipe #" + observation.Recipe + "\nDevice " + observation.Id;
        _deviceNativeProgress.text = observation?.Progress is { } progress
            ? "Native progress " + progress.ToString("G", CultureInfo.InvariantCulture) + " (raw)" : "Native progress unavailable";
        _deviceObservationAge.text = observation == null ? "No current server observation"
            : "Server observation · " + (int)snapshot.ObservationAge + "s ago";
        _deviceStatus.Button.interactable = ready;
        _deviceUnpair.Button.interactable = ready && snapshot.PairedId.Length > 0;
        var recipe = snapshot.Recipe;
        var entry = snapshot.Catalog.Selected;
        var current = recipe != null && observation?.Recipe == recipe.Id;
        _deviceRecipeTitle.text = recipe?.Name ?? "No recipe selected";
        _deviceRecipeCost.text = recipe == null ? "Read a page, then open Recipes & plan to compare the observed catalog."
            : ClientDeviceApp.Number(recipe.Resin) + " resin · " + ClientDeviceApp.Number(recipe.Seconds) + " recipe seconds\n"
                + (current ? "Selected on the printer" : "Local browsing; printer unchanged");
        _deviceRecipePage.text = "Page " + snapshot.RecipePage + (entry == null ? " · not observed"
            : " · " + DeviceRecipeAge(entry) + (entry.Fresh ? "" : " · refresh before selection"));
        _deviceRecipes.Button.interactable = ready && snapshot.Catalog.CanReadPages;
        _deviceSelectRecipe.Label.text = current ? "Already selected" : entry is { Fresh: false } ? "Refresh recipe first" : "Select recipe…";
        _deviceSelectRecipe.Button.interactable = ready && snapshot.CanSelectRecipe && snapshot.Review.Length == 0;
        SetDeviceRequestDetails(snapshot);
        _deviceConfirmVerb = snapshot.ConfirmVerb;
        _devicePrint.Button.interactable = ready && snapshot.CanPrint && snapshot.Review.Length == 0;
        _deviceConfirm.Label.text = snapshot.ConfirmVerb.Length > 0 ? snapshot.ConfirmLabel : "Confirm change";
        _deviceConfirm.Button.interactable = ready && snapshot.ConfirmVerb.Length > 0;
        _deviceCancel.Button.interactable = ready && snapshot.Review.Length > 0;
        SetDeviceCatalogState(snapshot);
    }

    private void SetDeviceRequestDetails(ClientDeviceAppSnapshot snapshot)
    {
        var receipt = snapshot.Receipt.Length > 0 ? snapshot.Receipt
            : "Prints on the bed do not identify your job. A server receipt does not prove print completion.";
        var review = snapshot.Review.Length > 0 ? snapshot.Review
            : snapshot.ChangeAvailability.Length > 0 ? snapshot.ChangeAvailability
            : !snapshot.CanPrint ? "The observed resin does not cover the printer's current recipe. Read status after adding resin."
            : "Recipe changes and one-print requests require review. The local plan never submits a queue or reserves resin.";
        if (_deviceAppMessage.text == snapshot.Message && _deviceReceipt.text == receipt && _deviceReview.text == review) return;
        _deviceAppMessage.text = snapshot.Message;
        _deviceReceipt.text = receipt;
        _deviceReview.text = review;
        var y = 0f;
        PlaceText(_deviceAppMessage, 0f, ref y, _deviceRequestScroll.Width, 10f);
        PlaceText(_deviceReceipt, 0f, ref y, _deviceRequestScroll.Width, 10f);
        PlaceText(_deviceReview, 0f, ref y, _deviceRequestScroll.Width, 10f);
        _deviceRequestScroll.Height(y);
        _deviceRequestScroll.Reset();
    }
}
