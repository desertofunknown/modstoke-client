// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Modstoke.Sons;

namespace Modstoke.Client;

internal enum ClientPackReadStatus { NotObserved, Ready, Partial, Empty, Unavailable, Uncertain }

internal sealed record ClientPackItem(int Id, string Name, string Type, int? Count, bool? Owned,
    bool? Equipped, ClientPackReadStatus Status, string Detail);

internal sealed record ClientPackFact(string Label, string Value);

internal sealed record ClientPackItemDetails(int Id, string Name, string Type,
    ClientPackReadStatus EquipmentStatus, string EquipmentDetail, IReadOnlyList<ClientPackFact> Facts);

internal sealed record ClientPackAppSnapshot
{
    internal long Generation { get; init; }
    internal long Revision { get; init; }
    internal bool Available { get; init; }
    internal string Search { get; init; } = "";
    internal int Page { get; init; } = 1;
    internal int PageCount { get; init; } = 1;
    internal int MatchedItems { get; init; }
    internal int PublishedItems { get; init; }
    internal int ReportedItems { get; init; }
    internal bool CatalogTruncated { get; init; }
    internal ClientPackReadStatus CatalogStatus { get; init; } = ClientPackReadStatus.NotObserved;
    internal string CatalogMessage { get; init; } = "Waiting for item definitions.";
    internal ClientPackReadStatus InventoryStatus { get; init; } = ClientPackReadStatus.NotObserved;
    internal string InventoryMessage { get; init; } = "Open Pack to read this page's stock.";
    internal long ObservedAt { get; init; }
    internal IReadOnlyList<ClientPackItem> Items { get; init; } = Array.Empty<ClientPackItem>();
    internal int SelectedItemId { get; init; }
    internal ClientPackItemDetails? Selected { get; init; }
    internal ClientPackRecipeSnapshot Recipe { get; init; } = new();
    internal ClientPackLoadoutSnapshot Loadout { get; init; } = new();
    internal ClientPackAmmunitionSnapshot Ammunition { get; init; } = new();
    internal ClientPackMatSnapshot Mat { get; init; } = new();
    internal bool RememberMat { get; init; }
    internal ClientPackMatComparisonSnapshot MatComparison { get; init; } = new();
    internal bool CanDropSelected { get; init; }
    internal string DropMessage { get; init; } = DefaultDropMessage;
    internal bool DropNeedsAttention { get; init; }
    internal bool CanStowEquipment { get; init; }
    internal string StowMessage { get; init; } = DefaultStowMessage;
    internal bool StowNeedsAttention { get; init; }

    internal const string DefaultDropMessage = "Drop one stored item in single-player or as host. Some items can only be dropped from the game's inventory.";
    internal const string DefaultStowMessage = "Select a current item to inspect it, or put away worn equipment. Hands stay reserved for the tablet.";
}

// The terminal owns this controller on the Unity thread. Only copied SDK values
// leave it; observations neither reserve stock nor authorize inventory changes.
internal sealed partial class ClientPackApp : IDisposable
{
    internal const int PageSize = 6;
    internal const int MaximumSearchLength = 80;
    private const long ReadIntervalMilliseconds = 2000;
    private const long MinimumReadIntervalMilliseconds = 500;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private bool _visible, _canObserve, _disposed, _actionInProgress, _readRequested = true;
    private long _generation, _revision, _nextRead, _lastRead;
    private string _search = "";
    private int _page = 1, _selectedItemId;
    private ItemCatalogSnapshot? _catalog;
    private ItemSnapshot[] _catalogItems = Array.Empty<ItemSnapshot>();
    private ItemSnapshot[] _filtered = Array.Empty<ItemSnapshot>();
    private ItemSnapshot[] _pageItems = Array.Empty<ItemSnapshot>();
    private int[] _pageItemIds = Array.Empty<int>();
    private IReadOnlyList<ClientPackItem> _rows = Array.Empty<ClientPackItem>();
    private ClientPackReadStatus _inventoryStatus = ClientPackReadStatus.NotObserved;
    private string _inventoryMessage = "Open Pack to read this page's stock.";
    private long _observedAt;
    private ClientPackAppSnapshot _snapshot = new();

    private bool OnThread => Environment.CurrentManagedThreadId == _thread;
    private bool CanChangeInventory => OnThread && !_disposed && _visible && _canObserve && !_actionInProgress;
    private static long Now => Environment.TickCount64;
    private int PageCount => Math.Max(1, (_filtered.Length + PageSize - 1) / PageSize);
    internal ClientPackAppSnapshot Snapshot => _snapshot;

    internal void ResetSession(long generation)
    {
        if (!OnThread || _disposed) return;
        _generation = generation;
        _canObserve = false;
        _catalog = null;
        _catalogItems = _filtered = _pageItems = Array.Empty<ItemSnapshot>();
        _pageItemIds = Array.Empty<int>();
        _rows = Array.Empty<ClientPackItem>();
        _search = "";
        _page = 1;
        _selectedItemId = 0;
        _nextRead = _lastRead = _observedAt = 0;
        _readRequested = true;
        _inventoryStatus = ClientPackReadStatus.NotObserved;
        _inventoryMessage = "Waiting for this player's inventory.";
        _recipeMode = ClientPackRecipeMode.MakesItem;
        _actionInProgress = false;
        ResetDropMessage();
        ResetStowMessage();
        ResetRecipes();
        ResetEquipment();
        StopRememberingMat();
        ResetMat();
        Publish();
    }

    internal void SetVisible(bool visible)
    {
        if (!OnThread || _disposed || _visible == visible) return;
        _visible = visible;
        if (visible) _readRequested = true;
        else if (_readMatRequested)
        {
            ResetMat();
            Publish();
        }
    }

    internal void Update(bool canObserve)
    {
        if (!OnThread || _disposed) return;
        if (_canObserve != canObserve)
        {
            _canObserve = canObserve;
            _readRequested = true;
            if (!canObserve)
            {
                ClearStock(ClientPackReadStatus.Unavailable, "Local inventory is unavailable. Readings will resume when it is ready.");
                ResetRecipes();
                ResetEquipment();
                StopRememberingMat();
                ResetMat(ClientPackReadStatus.Unavailable, "The mat is unavailable until your inventory is ready.");
                Publish();
            }
        }
        if (!_visible || !_canObserve || _actionInProgress) return;
        var now = Now;
        if (_lastRead != 0 && now - _lastRead < MinimumReadIntervalMilliseconds) return;
        if (!_readRequested && now < _nextRead) return;
        _readRequested = false;
        _lastRead = now;
        _nextRead = now + ReadIntervalMilliseconds;
        try
        {
            ReadCatalog();
            ReadMatIfRequested();
            ReadLoadout();
            if (_catalog is not { Ready: true })
            {
                ClearStock(ClientPackReadStatus.Unavailable, "Item definitions are not available yet.");
                ResetRecipes();
                ResetAmmunition();
                Publish();
                return;
            }
            ReadPageStock();
            ReadAmmunition();
            ReadRecipes();
            Publish();
        }
        catch (Exception)
        {
            ClearStock(ClientPackReadStatus.Unavailable, "Pack readings could not be completed. Refresh when the inventory is ready.");
            ResetRecipes();
            ResetEquipment();
            ResetMat(ClientPackReadStatus.Unavailable, "The mat reading could not be completed. Read it again when ready.");
            _recipe = new() { Status = ClientPackReadStatus.Unavailable, Mode = _recipeMode,
                Message = "Recipe readings are unavailable." };
            Publish();
        }
    }

    internal bool SetSearch(string value)
    {
        if (!OnThread || _disposed || value == null || value.Length > MaximumSearchLength
            || value.Any(char.IsControl)) return false;
        value = value.Trim();
        if (string.Equals(_search, value, StringComparison.Ordinal)) return true;
        _search = value;
        _page = 1;
        _selectedItemId = 0;
        FilterCatalog();
        QueuePageRead();
        return true;
    }

    internal void HandleCommand(string verb)
    {
        if (!OnThread || _disposed || !_visible) return;
        if (verb == "mat-remember")
        {
            if (!_canObserve) return;
            _rememberMat = !_rememberMat;
            Publish();
            return;
        }
        if (verb == "mat-read")
        {
            if (!_canObserve) return;
            _readMatRequested = true;
            _mat = new() { Pending = true, Message = "Waiting to read the mat." };
            _readRequested = true;
            Publish();
            return;
        }
        if (verb == "refresh")
        {
            ResetRecipeDiscovery();
            ResetAmmunition();
            _readRequested = true;
            return;
        }
        if (verb.StartsWith("browse.", StringComparison.Ordinal)
            && int.TryParse(verb.AsSpan(7), NumberStyles.None, CultureInfo.InvariantCulture, out var browseId))
        {
            BrowseItem(browseId);
            return;
        }
        if (verb == "page-prev" && _page > 1) _page--;
        else if (verb == "page-next" && _page < PageCount) _page++;
        else if (verb.StartsWith("select.", StringComparison.Ordinal)
            && int.TryParse(verb.AsSpan(7), NumberStyles.None, CultureInfo.InvariantCulture, out var selected)
            && _pageItems.Any(item => item.Id == selected))
        {
            if (_selectedItemId == selected) return;
            _selectedItemId = selected;
            ResetDropMessage();
            ResetRecipes();
            ResetAmmunition();
            _readRequested = true;
            Publish();
            return;
        }
        else
        {
            HandleRecipeCommand(verb);
            return;
        }
        _selectedItemId = 0;
        SelectPage();
        QueuePageRead();
    }

    private void ReadCatalog()
    {
        // Catalog is the SDK's published managed snapshot. Do not initialize or
        // refresh the native database from a read-only inventory page.
        var catalog = ItemCatalog.Catalog;
        if (ReferenceEquals(_catalog, catalog)) return;
        _catalog = catalog;
        _catalogItems = catalog.Ready
            ? catalog.Items.Where(item => item.Id > 0).GroupBy(item => item.Id).Select(group => group.First())
                .Take(ItemCatalog.MaxPublishedItems).OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Id).ToArray()
            : Array.Empty<ItemSnapshot>();
        FilterCatalog();
        ResetRecipes();
        ResetAmmunition();
    }

    private void FilterCatalog()
    {
        _filtered = _search.Length == 0 ? _catalogItems : _catalogItems.Where(item =>
            item.DisplayName.Contains(_search, StringComparison.OrdinalIgnoreCase)
            || item.Name.Contains(_search, StringComparison.OrdinalIgnoreCase)
            || item.Type.Contains(_search, StringComparison.OrdinalIgnoreCase)
            || item.Id.ToString(CultureInfo.InvariantCulture).Contains(_search, StringComparison.Ordinal)).ToArray();
        _page = Math.Clamp(_page, 1, PageCount);
        SelectPage();
    }

    private void SelectPage()
    {
        _pageItems = _filtered.Skip((_page - 1) * PageSize).Take(PageSize).ToArray();
        _pageItemIds = _pageItems.Select(item => item.Id).ToArray();
        if (!_pageItems.Any(item => item.Id == _selectedItemId))
            _selectedItemId = _pageItems.Length == 0 ? 0 : _pageItems[0].Id;
    }

    private void QueuePageRead()
    {
        ResetDropMessage();
        ClearStock(ClientPackReadStatus.NotObserved, "Waiting to read this page's stock.");
        ResetRecipes();
        ResetAmmunition();
        _readRequested = true;
        Publish();
    }

    private void BrowseItem(int itemId)
    {
        var index = Array.FindIndex(_catalogItems, item => item.Id == itemId);
        if (index < 0) return;
        _search = "";
        _filtered = _catalogItems;
        _page = index / PageSize + 1;
        _selectedItemId = itemId;
        SelectPage();
        QueuePageRead();
    }

    private void ClearStock(ClientPackReadStatus status, string message)
    {
        _inventoryStatus = status;
        _inventoryMessage = message;
        _observedAt = 0;
        _rows = Array.AsReadOnly(_pageItems.Select(item => new ClientPackItem(item.Id, item.DisplayName,
            item.Type, null, null, null, status, message)).ToArray());
    }

    private void ReadPageStock()
    {
        if (_pageItems.Length == 0)
        {
            ClearStock(ClientPackReadStatus.Empty, "No published item definitions match this search.");
            return;
        }
        if (!Inventory.TryCaptureItems(_pageItemIds,
                out var entries, out _, includeEquipped: true, checkNextSlot: false)
            || entries.Count != _pageItems.Length)
        {
            ClearStock(ClientPackReadStatus.Unavailable, "This page's stock is unavailable. Refresh when your inventory is ready.");
            return;
        }
        var rows = new ClientPackItem[_pageItems.Length];
        var failures = 0;
        for (var i = 0; i < rows.Length; i++)
        {
            var item = _pageItems[i];
            var entry = entries[i];
            var state = !entry.ReadSucceeded ? ClientPackReadStatus.Unavailable
                : entry.Count < 0 ? ClientPackReadStatus.Uncertain : ClientPackReadStatus.Ready;
            if (state != ClientPackReadStatus.Ready) failures++;
            rows[i] = new(item.Id, item.DisplayName, item.Type, entry.Count >= 0 ? entry.Count : null,
                entry.OwnsItem, entry.Equipped, state, state == ClientPackReadStatus.Uncertain
                    ? "This item's quantity is uncertain." : state == ClientPackReadStatus.Unavailable
                        ? "This item's stock could not be read." : "");
        }
        _rows = Array.AsReadOnly(rows);
        _observedAt = Now;
        _inventoryStatus = failures == 0 ? ClientPackReadStatus.Ready
            : failures == rows.Length ? ClientPackReadStatus.Unavailable : ClientPackReadStatus.Partial;
        _inventoryMessage = failures == 0 ? "Counts include equipped items and exclude pending equipment slots."
            : failures + " item readings are unavailable or uncertain. Unknown stock is not zero.";
    }

    private ClientPackItemDetails? SelectedDetails()
    {
        var item = _pageItems.FirstOrDefault(candidate => candidate.Id == _selectedItemId);
        if (item == null) return null;
        var equipment = item.Equipment;
        var state = equipment.Ready ? ClientPackReadStatus.Ready
            : equipment.Slot.HasValue || equipment.RequiresBothHands.HasValue ? ClientPackReadStatus.Uncertain
            : ClientPackReadStatus.Unavailable;
        var facts = new[]
        {
            new ClientPackFact("Equip slot", equipment.Slot.HasValue ? EquipmentSlotName(equipment.Slot.Value) : "Unknown"),
            new ClientPackFact("Requires both hands", YesNo(equipment.RequiresBothHands)),
            new ClientPackFact("Can be stowed", YesNo(!item.CanNotBeStored)),
            new ClientPackFact("Drops when put away", YesNo(item.AlwaysDropOnUnequip)),
            new ClientPackFact("Quick select", YesNo(item.AllowQuickSelect)),
            new ClientPackFact("Hotkey support", YesNo(item.CanBeHotKeyed))
        };
        return new(item.Id, item.DisplayName, item.Type, state, state == ClientPackReadStatus.Ready
            ? "Item definitions describe equipment support; current actions may still prevent equipping."
            : "Some equipment information is unavailable.", Array.AsReadOnly(facts));
    }

    private static string YesNo(bool? value) => value.HasValue ? value.Value ? "Yes" : "No" : "Unknown";

    private void Publish()
    {
        _snapshot = new()
        {
            Generation = _generation, Revision = ++_revision, Available = _canObserve,
            Search = _search, Page = _page, PageCount = PageCount, MatchedItems = _filtered.Length,
            PublishedItems = _catalogItems.Length, ReportedItems = _catalog?.ItemCount ?? 0,
            CatalogTruncated = _catalog?.Truncated == true,
            CatalogStatus = _catalog is { Ready: true } ? ClientPackReadStatus.Ready : ClientPackReadStatus.Unavailable,
            CatalogMessage = _catalog is { Ready: true } ? "Item catalog available." : "Waiting for the item catalog.",
            InventoryStatus = _inventoryStatus, InventoryMessage = _inventoryMessage,
            ObservedAt = _observedAt, Items = _rows, SelectedItemId = _selectedItemId,
            Selected = SelectedDetails(), Recipe = _recipe, Loadout = _loadout, Ammunition = _ammunition,
            Mat = _mat, RememberMat = _rememberMat, MatComparison = CompareMat(),
            CanDropSelected = CanDropItem(_selectedItemId), DropMessage = _dropMessage,
            DropNeedsAttention = _dropNeedsAttention, CanStowEquipment = CanChangeInventory,
            StowMessage = _stowMessage, StowNeedsAttention = _stowNeedsAttention
        };
    }

    public void Dispose()
    {
        if (!OnThread || _disposed) return;
        _visible = _canObserve = false;
        _catalog = null;
        _catalogItems = _filtered = _pageItems = Array.Empty<ItemSnapshot>();
        _pageItemIds = Array.Empty<int>();
        _rows = Array.Empty<ClientPackItem>();
        ResetRecipes();
        ResetEquipment();
        StopRememberingMat();
        ResetMat();
        _disposed = true;
        _snapshot = new() { Generation = _generation, Revision = ++_revision,
            InventoryStatus = ClientPackReadStatus.Unavailable, InventoryMessage = "Pack is closed." };
    }
}
