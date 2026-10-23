using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace IcarusStarlink.App.ViewModels;

public sealed partial class SavesViewModel
{
    // --- Bestiary (account-wide, BestiaryData.json — a separate file from Profile.json) ---

    public ObservableCollection<SaveBestiaryEntryViewModel> BestiaryEntries { get; } = [];

    public ObservableCollection<SaveBestiaryEntryViewModel> FilteredBestiaryEntries { get; } = [];

    [ObservableProperty]
    private string _bestiarySearchText = "";

    partial void OnBestiarySearchTextChanged(string value) => RefreshBestiaryFilter();

    /// <summary>Only creatures whose field-guide entry isn't complete.</summary>
    [ObservableProperty]
    private bool _showIncompleteBestiaryOnly;

    partial void OnShowIncompleteBestiaryOnlyChanged(bool value) => RefreshBestiaryFilter();

    /// <summary>"61 of 107 complete".</summary>
    public string BestiarySummary => $"{BestiaryEntries.Count(b => b.IsComplete)} of {BestiaryEntries.Count(b => b.PointsRequired > 0)} complete";

    // Listed by biome (Forest, Arctic, Desert..., Great Hunt arenas).
    private void RefreshBestiaryFilter() =>
        RefreshFilter(BestiaryEntries.OrderBy(b => b.Group, StringComparer.OrdinalIgnoreCase), FilteredBestiaryEntries, BestiarySearchText,
            ShowIncompleteBestiaryOnly ? b => !b.IsComplete : null, b => b.DisplayName, b => b.Group);

    /// <summary>Fills every creature listed right now (search and filter applied) to its full points.</summary>
    [RelayCommand]
    private void CompleteShownBestiary()
    {
        foreach (var entry in FilteredBestiaryEntries.Where(b => b.CanSetMax && !b.IsComplete).ToList())
        {
            entry.SetMaxCommand.Execute(null);
        }
    }

    /// <summary>BestiaryTracking only — FishTracking (a separate fishing sub-tracker) is preserved untouched, out of scope for this editor.</summary>
    private void BuildBestiaryEntries()
    {
        BestiaryEntries.Clear();
        var points = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (_bestiaryRoot?["BestiaryTracking"] is JsonArray array)
        {
            foreach (var entry in array.OfType<JsonObject>())
            {
                if (SaveJson.String((entry["BestiaryGroup"] as JsonObject)?["RowName"]) is { } rowName)
                {
                    points[rowName] = SaveJson.Int(entry["NumPoints"]) ?? 0;
                }
            }
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rowName, info) in _gameNames.BestiaryCreatures)
        {
            BestiaryEntries.Add(new SaveBestiaryEntryViewModel(rowName, info.DisplayName, info.PointsRequired, info.IsBoss, points.GetValueOrDefault(rowName), info.ImagePath, NotifyDirtyChanged, info.Group));
            seen.Add(rowName);
        }

        foreach (var (rowName, currentPoints) in points)
        {
            if (!seen.Contains(rowName))
            {
                BestiaryEntries.Add(new SaveBestiaryEntryViewModel(rowName, rowName, 0, false, currentPoints, null, NotifyDirtyChanged));
            }
        }

        RefreshBestiaryFilter();
        OnPropertyChanged(nameof(BestiarySummary));
    }

    /// <summary>Writes BestiaryEntries back into its own root node — a plain per-row rebuild (unlike the array-membership rewrite pattern above) since every tracked creature always has exactly one NumPoints entry, not a present/absent one.</summary>
    private void ApplyBestiaryEdits()
    {
        if (_bestiaryRoot is null)
        {
            return;
        }

        var newArray = new JsonArray();
        foreach (var entry in BestiaryEntries)
        {
            // EffectivePoints, not a re-parse of PointsText: a box left unparsable (e.g. cleared
            // mid-edit) must fall back to the entry's own last-known-good value, never silently
            // drop the whole creature's tracking entry out of the save.
            var points = entry.EffectivePoints;
            if (points > 0)
            {
                newArray.Add(new JsonObject
                {
                    ["BestiaryGroup"] = new JsonObject { ["RowName"] = entry.RowName, ["DataTableName"] = "D_BestiaryData" },
                    ["NumPoints"] = points,
                });
            }
        }

        _bestiaryRoot["BestiaryTracking"] = newArray;
    }
}
