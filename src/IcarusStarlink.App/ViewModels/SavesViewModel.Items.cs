using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace IcarusStarlink.App.ViewModels;

public sealed partial class SavesViewModel
{
    // --- Items (account-wide, MetaInventory.json — a separate file from Profile.json) ---

    public ObservableCollection<SaveInventoryItemViewModel> MetaInventoryItems { get; } = [];

    public ObservableCollection<SaveInventoryItemViewModel> FilteredMetaInventoryItems { get; } = [];

    [ObservableProperty]
    private string _itemSearchText = "";

    partial void OnItemSearchTextChanged(string value) => RefreshItemFilter();

    private void RefreshItemFilter()
    {
        RefreshFilter(MetaInventoryItems, FilteredMetaInventoryItems, ItemSearchText, null, i => i.DisplayName, i => i.RowName);
        OnPropertyChanged(nameof(ItemsEmptyText));
    }

    /// <summary>
    /// What the Items tab says instead of an empty list; null while it lists something. An empty
    /// bank is normal (the user's slot, 2026-10-23: MetaInventory.json's Items is []), so it says
    /// what would show here rather than looking broken.
    /// </summary>
    public string? ItemsEmptyText =>
        FilteredMetaInventoryItems.Count > 0 ? null
        : MetaInventoryItems.Count > 0 ? "No item in the bank matches the search."
        : _metaInventoryRoot is null ? "This slot has no item bank (MetaInventory.json) yet, or it couldn't be read."
        : "This account's item bank is empty. Items kept on the account between prospects, outside any character's loadout, are listed here; you can delete them one by one.";

    private void BuildMetaInventoryItems()
    {
        MetaInventoryItems.Clear();
        _metaInventoryDirty = false;
        if (_metaInventoryRoot?["Items"] is JsonArray array)
        {
            foreach (var entry in array.OfType<JsonObject>())
            {
                var rowName = SaveJson.String((entry["ItemStaticData"] as JsonObject)?["RowName"]) ?? "?";
                var info = _gameNames.Items.GetValueOrDefault(rowName);
                MetaInventoryItems.Add(new SaveInventoryItemViewModel(entry, info?.DisplayName ?? rowName, rowName, info?.Weight ?? 0, info?.MaxStack ?? 1, info?.IconPath));
            }
        }

        RefreshItemFilter();
    }

    /// <summary>Deliberately view + delete only (see SaveInventoryItemViewModel's own doc comment) — every surviving item's Node is its own original JsonObject, written back completely untouched.</summary>
    [RelayCommand]
    private void DeleteMetaInventoryItem(SaveInventoryItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        MetaInventoryItems.Remove(item);
        FilteredMetaInventoryItems.Remove(item);
        OnPropertyChanged(nameof(ItemsEmptyText));
        _metaInventoryDirty = true;
        NotifyDirtyChanged();
    }

    private void ApplyMetaInventoryEdits()
    {
        if (_metaInventoryRoot is null)
        {
            return;
        }

        var newArray = new JsonArray();
        foreach (var item in MetaInventoryItems)
        {
            newArray.Add(item.Node.DeepClone());
        }

        _metaInventoryRoot["Items"] = newArray;
    }
}
