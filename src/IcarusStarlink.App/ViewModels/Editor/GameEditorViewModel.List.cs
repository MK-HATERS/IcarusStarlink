using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.Utilities;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.ViewModels;

/// <summary>A category chip above the Editor's item list, with how many items it holds under the other filters.</summary>
public sealed partial class EditorCategoryChoice(string? name) : ObservableObject
{
    /// <summary>The category; null for "All".</summary>
    public string? Name { get; } = name;

    public string Label => Name ?? "All";

    [ObservableProperty]
    private int _count;

    /// <summary>What a screen reader says for the chip.</summary>
    public override string ToString() => $"{Label} ({Count})";
}

/// <summary>A choice in the Editor's "Has" or "Made at" drop-down; a null key means any.</summary>
public sealed record EditorFilterChoice(string? Key, string Label)
{
    public override string ToString() => Label;
}

/// <summary>The item list: every game item with its icon, category chips, "Has" / "Made at" filters, and the marks for what the chosen mod already changes.</summary>
public sealed partial class GameEditorViewModel
{
    private readonly GameIconLoader? _icons;
    private IReadOnlyList<EditorItemEntry> _allItems = [];
    private bool _refreshingFilter;

    public ObservableCollection<EditorItemEntry> FilteredItems { get; } = [];

    public ObservableCollection<EditorCategoryChoice> Categories { get; } = [];

    public ObservableCollection<EditorFilterChoice> HasChoices { get; } = [];

    public ObservableCollection<EditorFilterChoice> BenchChoices { get; } = [];

    [ObservableProperty]
    private string _filterText = "";

    [ObservableProperty]
    private EditorCategoryChoice? _selectedCategory;

    [ObservableProperty]
    private EditorFilterChoice? _selectedHas;

    [ObservableProperty]
    private EditorFilterChoice? _selectedBench;

    /// <summary>Only the items the mod under Save to already changes (one of the user's mods only).</summary>
    [ObservableProperty]
    private bool _onlyInTargetMod;

    /// <summary>"176 of 3,090 items" under the filters.</summary>
    [ObservableProperty]
    private string _showingText = "";

    [ObservableProperty]
    private bool _hasNoMatches;

    /// <summary>"This mod changes 4 items" beside the filter for them.</summary>
    [ObservableProperty]
    private string _targetModText = "";

    /// <summary>The search runs once the typing stops: each pass goes through every item (3,090) and can change thousands of list rows.</summary>
    private readonly DebounceTimer _filterDebounce;

    partial void OnFilterTextChanged(string value) => _filterDebounce.Restart();

    /// <summary>Runs a search still waiting for the typing to stop (tests: the timer never ticks without a message loop).</summary>
    internal void FlushFilter() => _filterDebounce.Flush();

    partial void OnSelectedCategoryChanged(EditorCategoryChoice? value) => RefreshFilter();

    partial void OnSelectedHasChanged(EditorFilterChoice? value) => RefreshFilter();

    partial void OnSelectedBenchChanged(EditorFilterChoice? value) => RefreshFilter();

    partial void OnOnlyInTargetModChanged(bool value) => RefreshFilter();

    /// <summary>Back to every item: no search, all categories, any "Has" and bench.</summary>
    [RelayCommand]
    private void ClearFilters()
    {
        _refreshingFilter = true;
        try
        {
            FilterText = "";
            SelectedCategory = Categories.FirstOrDefault();
            SelectedHas = HasChoices.FirstOrDefault();
            SelectedBench = BenchChoices.FirstOrDefault();
            OnlyInTargetMod = false;
        }
        finally
        {
            _refreshingFilter = false;
        }

        RefreshFilter();
        _filterDebounce.Cancel(); // the emptied search is already shown
    }

    /// <summary>The chips and drop-downs offer only what the loaded game data has.</summary>
    private void BuildFilterChoices(GameTableIndex game)
    {
        _refreshingFilter = true;
        try
        {
            var keepCategory = SelectedCategory?.Name;
            var keepHas = SelectedHas?.Key;
            var keepBench = SelectedBench?.Key;

            Categories.Clear();
            Categories.Add(new EditorCategoryChoice(null));
            var present = _allItems.Select(i => i.Category).ToHashSet(StringComparer.Ordinal);
            foreach (var category in GameItemCatalog.CategoryOrder.Where(present.Contains))
            {
                Categories.Add(new EditorCategoryChoice(category));
            }

            HasChoices.Clear();
            HasChoices.Add(new EditorFilterChoice(null, "Anything"));
            foreach (var (key, label) in GameItemCatalog.HasFilters.Where(f => _allItems.Any(i => i.Info.Has.Contains(f.Key))))
            {
                HasChoices.Add(new EditorFilterChoice(key, label));
            }

            BenchChoices.Clear();
            BenchChoices.Add(new EditorFilterChoice(null, "Any bench"));
            foreach (var bench in _allItems.SelectMany(i => i.Info.Benches).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(b => new EditorFilterChoice(b, GameItemCatalog.BenchName(game, b)))
                .OrderBy(b => b.Label, StringComparer.OrdinalIgnoreCase))
            {
                BenchChoices.Add(bench);
            }

            SelectedCategory = Categories.FirstOrDefault(c => c.Name == keepCategory) ?? Categories[0];
            SelectedHas = HasChoices.FirstOrDefault(c => c.Key == keepHas) ?? HasChoices[0];
            SelectedBench = BenchChoices.FirstOrDefault(c => c.Key == keepBench) ?? BenchChoices[0];
        }
        finally
        {
            _refreshingFilter = false;
        }
    }

    /// <summary>Marks the items the mod under Save to changes (none for a new mod).</summary>
    private void RefreshTargetMarks()
    {
        if (_allItems.Count == 0)
        {
            return;
        }

        ExmodPackage? package = null;
        if (SelectedTarget is { IsNew: false, FolderName: { } folder })
        {
            try
            {
                package = ExmodFolder.ReadPackageOnly(_repository.GetFolderPath(folder), _repository.ListFolderFiles(folder));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or System.Text.Json.JsonException)
            {
                // No marks then; picking an item says why the mod can't be read.
            }
        }

        var changed = GameItemCatalog.ItemsChangedBy(_allItems.Select(i => i.Info), package);
        foreach (var item in _allItems)
        {
            item.IsInTargetMod = changed.Contains(item.Name);
        }

        if (_queueState is not null)
        {
            _ = RefreshOtherModsAsync(reread: false); // "other" mods are every mod but this one
        }

        TargetModText = changed.Count switch
        {
            0 => "This mod changes no game item yet",
            1 => "Only the 1 item this mod changes",
            var n => $"Only the {n:N0} items this mod changes",
        };
        RefreshFilter();
    }

    /// <summary>
    /// Rebuilds the list under the search and filters. Items whose name matches come first, then
    /// those matched only by their description. The item on show always stays in the list, at the
    /// top if the filters would hide it, so what was typed for it isn't thrown away.
    /// </summary>
    private void RefreshFilter()
    {
        if (_refreshingFilter)
        {
            return;
        }

        var picked = SelectedItem;
        _refreshingFilter = true;
        try
        {
            var words = FilterText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var has = SelectedHas?.Key;
            var bench = SelectedBench?.Key;
            var onlyTarget = OnlyInTargetMod && IsOwnModTarget;
            var category = SelectedCategory?.Name;
            var perCategory = new Dictionary<string, int>(StringComparer.Ordinal);
            var byName = new List<EditorItemEntry>();
            var byDescription = new List<EditorItemEntry>();

            foreach (var item in _allItems)
            {
                if ((has is not null && !item.Info.Has.Contains(has))
                    || (bench is not null && !item.Info.Benches.Contains(bench, StringComparer.OrdinalIgnoreCase))
                    || (onlyTarget && !item.IsInTargetMod))
                {
                    continue;
                }

                var nameMatch = words.All(w => item.Display.Contains(w, StringComparison.OrdinalIgnoreCase));
                if (!nameMatch && !words.All(w => item.Display.Contains(w, StringComparison.OrdinalIgnoreCase)
                        || (item.Info.Description?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false)))
                {
                    continue;
                }

                // Each chip counts what it would show with every other filter as it is.
                perCategory[item.Category] = perCategory.GetValueOrDefault(item.Category) + 1;
                if (category is not null && item.Category != category)
                {
                    continue;
                }

                (nameMatch ? byName : byDescription).Add(item);
            }

            List<EditorItemEntry> list = [.. byName, .. byDescription];
            var matched = list.Count;
            if (picked is not null && !list.Contains(picked))
            {
                list.Insert(0, picked);
            }

            ObservableCollectionSync.SyncTo(FilteredItems, list);
            foreach (var chip in Categories)
            {
                chip.Count = chip.Name is null ? perCategory.Values.Sum() : perCategory.GetValueOrDefault(chip.Name);
            }

            HasNoMatches = matched == 0;
            ShowingText = matched == _allItems.Count ? $"All {matched:N0} items" : $"{matched:N0} of {_allItems.Count:N0} items";
            SelectedItem = picked;
        }
        finally
        {
            _refreshingFilter = false;
        }
    }
}
