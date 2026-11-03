// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;

namespace Modstoke.Client;

internal sealed record ClientDeviceCatalogEntry(ClientDeviceRecipe Recipe, int Page, double Age, bool Fresh);
internal sealed record ClientDeviceCatalogSnapshot(IReadOnlyList<ClientDeviceCatalogEntry> Entries,
    ClientDeviceCatalogEntry? Selected, ClientDeviceCatalogEntry? Comparison,
    ClientDeviceCatalogEntry? Current, int ObservedCount, string Sort, bool AffordableOnly, int PlanCount, bool CanReadPages);

internal sealed partial class ClientDeviceApp
{
    // The native catalog contains at most 32 recipes. These are visited pages, never a production queue.
    internal const int MaximumCatalogPages = 32;
    internal const int MaximumPlanCount = 20;
    private readonly Dictionary<int, CatalogPage> _catalog = new();
    private int? _comparisonId;
    private int _catalogSort, _planCount = 1;
    private bool _affordableOnly, _catalogNeedsStatus;
    private long _catalogSelectionRevision;
    private PendingCatalogPage? _pendingCatalogPage;

    private sealed record CatalogPage(ClientDeviceRecipe Recipe, int Page, bool More, long At);
    private sealed record PendingCatalogPage(ClientDeviceRecipe? Recipe, int Page, bool More, long At, long SelectionRevision);

    private bool CurrentObservation => _pairedId.Length > 0 && _observation != null
        && _observation.Id == _pairedId && Fresh(_observedAt);

    private string ChangeAvailability
    {
        get
        {
            if (!CurrentObservation) return "Read the linked printer's current status before changing it.";
            if (_catalogNeedsStatus) return "Read current status to confirm this recipe page still belongs to the linked printer.";
            var observation = _observation!;
            return !observation.Owned ? "The server does not own both native printer components; controls are unavailable."
                : observation.State != "idle" ? "The printer is " + observation.State + ". Controls need an idle printer with a clear bed."
                : "";
        }
    }

    private bool CanSelectRecipe => ChangeAvailability.Length == 0 && _recipe != null && Fresh(_recipeAt)
        && _recipe.Id != _observation!.Recipe;

    private bool CanPrint
    {
        get
        {
            if (ChangeAvailability.Length > 0) return false;
            var observation = _observation!;
            var current = FindCatalogRecipe(observation.Recipe);
            return current == null || !Fresh(current.At) || current.Recipe.Resin <= observation.Resin;
        }
    }

    private void SetPair(string id, bool invalidate = false)
    {
        if (!invalidate && _pairedId == id) return;
        _pairRevision++;
        _pairedId = id;
        _review = null;
        _observation = null;
        _observedAt = 0;
        _wantStatus = false;
        ClearCatalog();
    }

    private void ClearCatalog()
    {
        _catalog.Clear();
        _comparisonId = null;
        _recipe = null;
        _recipePage = 1;
        _recipeAt = 0;
        _moreRecipes = false;
        _planCount = 1;
        _catalogSort = 0;
        _affordableOnly = false;
        _catalogNeedsStatus = false;
        _pendingCatalogPage = null;
        _catalogSelectionRevision++;
    }

    private CatalogPage? FindCatalogRecipe(int id)
    {
        foreach (var entry in _catalog.Values) if (entry.Recipe.Id == id) return entry;
        return null;
    }

    private void ConfirmCatalogLink()
    {
        // A changed status ID has already cleared this staging slot in SetPair.
        if (_pendingCatalogPage is { } page)
            RememberRecipe(page.Page, page.More, page.Recipe, page.At, page.SelectionRevision == _catalogSelectionRevision);
        _pendingCatalogPage = null;
        _catalogNeedsStatus = false;
    }

    private void RememberRecipe(int page, bool more, ClientDeviceRecipe? recipe, long at, bool select)
    {
        // Page positions can change. A newer page replaces its previous recipe and any older copy of its ID.
        var remove = new List<int>();
        foreach (var entry in _catalog.Values)
            if (entry.Page == page || recipe != null && entry.Recipe.Id == recipe.Id
                || !more && entry.Page > page) remove.Add(entry.Page);
        foreach (var oldPage in remove) _catalog.Remove(oldPage);
        if (recipe != null) _catalog[page] = new(recipe, page, more, at);
        var retained = !select && _recipe != null ? FindCatalogRecipe(_recipe.Id) : null;
        _recipe = select ? recipe : retained?.Recipe;
        _recipePage = retained?.Page ?? page;
        _moreRecipes = retained?.More ?? more;
        _recipeAt = retained?.At ?? at;
        if (_comparisonId is { } comparison && FindCatalogRecipe(comparison) == null) _comparisonId = null;
    }

    private void BrowseRecipePage(int page)
    {
        if (_catalog.TryGetValue(page, out var cached)) SelectCatalogRecipe(cached);
        else ReadPage("recipes", page);
    }

    private void SelectCatalogRecipe(CatalogPage entry)
    {
        _review = null;
        _catalogSelectionRevision++;
        _recipe = entry.Recipe;
        _recipePage = entry.Page;
        _moreRecipes = entry.More;
        _recipeAt = entry.At;
        _message = Fresh(entry.At) ? "Recipe selected locally. The printer has not changed."
            : "Saved recipe selected for planning. Refresh its page before changing the printer.";
    }

    private bool HandleCatalogCommand(string verb)
    {
        const string prefix = "catalog.select.";
        if (verb.StartsWith(prefix, StringComparison.Ordinal))
        {
            if (int.TryParse(verb.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                && FindCatalogRecipe(id) is { } entry) SelectCatalogRecipe(entry);
            return true;
        }
        switch (verb)
        {
            case "catalog-sort": _catalogSort = (_catalogSort + 1) % 4; return true;
            case "catalog-affordable":
                if (CurrentObservation) _affordableOnly = !_affordableOnly;
                return true;
            case "plan-less": if (_recipe != null && _planCount > 1) _planCount--; return true;
            case "plan-more": if (_recipe != null && _planCount < MaximumPlanCount) _planCount++; return true;
            case "compare-recipe":
                if (_recipe != null) _comparisonId = _comparisonId == _recipe.Id ? null : _recipe.Id;
                return true;
            case "clear-comparison": _comparisonId = null; return true;
            default: return false;
        }
    }

    private ClientDeviceCatalogSnapshot CatalogSnapshot()
    {
        var entries = new List<CatalogPage>(_catalog.Count);
        foreach (var entry in _catalog.Values)
            if (!_affordableOnly || !CurrentObservation || entry.Recipe.Resin <= _observation!.Resin)
                entries.Add(entry);
        entries.Sort((left, right) =>
        {
            var order = _catalogSort switch
            {
                1 => StringComparer.OrdinalIgnoreCase.Compare(left.Recipe.Name, right.Recipe.Name),
                2 => left.Recipe.Resin.CompareTo(right.Recipe.Resin),
                3 => left.Recipe.Seconds.CompareTo(right.Recipe.Seconds),
                _ => left.Page.CompareTo(right.Page)
            };
            return order != 0 ? order : left.Page.CompareTo(right.Page);
        });
        var snapshots = new List<ClientDeviceCatalogEntry>(entries.Count);
        foreach (var entry in entries) snapshots.Add(CatalogEntry(entry)!);
        return new(snapshots, _recipe == null ? null : CatalogEntry(FindCatalogRecipe(_recipe.Id)),
            _comparisonId is { } comparison ? CatalogEntry(FindCatalogRecipe(comparison)) : null,
            CurrentObservation ? CatalogEntry(FindCatalogRecipe(_observation!.Recipe)) : null,
            _catalog.Count, _catalogSort switch { 1 => "Name", 2 => "Resin", 3 => "Duration", _ => "Native page" },
            _affordableOnly, _planCount, CurrentObservation && !_catalogNeedsStatus);
    }

    private static ClientDeviceCatalogEntry? CatalogEntry(CatalogPage? page) => page == null ? null
        : new(page.Recipe, page.Page, Math.Max(0, (Now - page.At) / 1000d), Fresh(page.At));

    private string DescribePrintReview()
    {
        var observation = _observation!;
        var current = FindCatalogRecipe(observation.Recipe);
        var name = "recipe #" + observation.Recipe;
        var resources = " This recipe's resin cost is not currently loaded; it can consume resin.";
        if (current != null && Fresh(current.At))
        {
            name = current.Recipe.Name + " (#" + current.Recipe.Id + ")";
            resources = " Recipe cost: " + Number(current.Recipe.Resin) + " resin; observed printer resin: "
                + Number(observation.Resin) + ".";
        }
        return "Request one print of the printer's current " + name + "." + resources
            + " The local plan is not submitted. The server rechecks the printer, access and resources.";
    }
}
