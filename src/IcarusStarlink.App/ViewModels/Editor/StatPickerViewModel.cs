using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// "Add a stat": search the game's stats by plain name (cold, movement…), see whether each is a
/// flat amount or a percent, pick one, add it. Shared by the Editor page and the mod editor.
/// </summary>
public sealed partial class StatPickerViewModel(IReadOnlyList<StatChoice> stats, Func<StatChoice, string?> add) : ObservableObject
{
    /// <summary>What the user typed to find a stat.</summary>
    [ObservableProperty]
    private string _statSearch = "";

    /// <summary>Stats matching every word of the search (plain name or game name), at most 40.</summary>
    public ObservableCollection<StatChoice> MatchingStats { get; } = [];

    public bool HasMatchingStats => MatchingStats.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedStatText))]
    private StatChoice? _selectedStat;

    public string SelectedStatText => SelectedStat is { } s ? $"{s.Label}  ·  {s.Kind}" : "Nothing picked yet: search, then click a stat.";

    [ObservableProperty]
    private string? _statError;

    partial void OnStatSearchChanged(string value)
    {
        MatchingStats.Clear();
        var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 0)
        {
            foreach (var stat in stats.Where(s => words.All(w => s.Label.Contains(w, StringComparison.OrdinalIgnoreCase) || s.Name.Contains(w, StringComparison.OrdinalIgnoreCase))).Take(40))
            {
                MatchingStats.Add(stat);
            }
        }

        OnPropertyChanged(nameof(HasMatchingStats));
    }

    [RelayCommand]
    private void AddStat()
    {
        if (SelectedStat is not { } stat)
        {
            StatError = "Search for a stat (e.g. cold, movement, health) and click one first.";
            return;
        }

        // The owner adds it, or says why not ("That stat is already here.").
        StatError = add(stat);
        if (StatError is null)
        {
            SelectedStat = null;
            StatSearch = "";
        }
    }
}
