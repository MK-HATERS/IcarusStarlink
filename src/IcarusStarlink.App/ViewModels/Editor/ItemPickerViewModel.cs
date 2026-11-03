using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// "Add ingredient" on a recipe card: search the game's items by name, pick one, add it. Shaped like
/// StatPickerViewModel (the same search-then-pick), for items instead of stats.
/// </summary>
public sealed partial class ItemPickerViewModel(Func<IReadOnlyList<ItemChoice>> items, Func<ItemChoice, string?> add) : ObservableObject
{
    [ObservableProperty]
    private string _itemSearch = "";

    /// <summary>Items matching every word of the search (in-game name or row name), at most 40.</summary>
    public ObservableCollection<ItemChoice> MatchingItems { get; } = [];

    public bool HasMatchingItems => MatchingItems.Count > 0;

    [ObservableProperty]
    private ItemChoice? _selectedItem;

    [ObservableProperty]
    private string? _itemError;

    partial void OnItemSearchChanged(string value)
    {
        MatchingItems.Clear();
        var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 0)
        {
            foreach (var item in items().Where(i => words.All(w => i.Display.Contains(w, StringComparison.OrdinalIgnoreCase))).Take(40))
            {
                MatchingItems.Add(item);
            }
        }

        OnPropertyChanged(nameof(HasMatchingItems));
    }

    [RelayCommand]
    private void AddItem()
    {
        if (SelectedItem is not { } item)
        {
            ItemError = "Search for an item (e.g. stone, fiber, iron) and click one first.";
            return;
        }

        // The card adds it, or says why not ("Already an ingredient.").
        ItemError = add(item);
        if (ItemError is null)
        {
            SelectedItem = null;
            ItemSearch = "";
        }
    }
}
