using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;

namespace IcarusStarlink.App.ViewModels;

public sealed partial class SavesViewModel
{
    // --- Accolades (account-wide, Accolades.json — a separate file from Profile.json) ---

    public ObservableCollection<SaveAccoladeViewModel> Accolades { get; } = [];

    public ObservableCollection<SaveAccoladeViewModel> FilteredAccolades { get; } = [];

    [ObservableProperty]
    private string _accoladeSearchText = "";

    partial void OnAccoladeSearchTextChanged(string value) => RefreshAccoladeFilter();

    /// <summary>Only the ones not earned yet.</summary>
    [ObservableProperty]
    private bool _showUnearnedAccoladesOnly;

    partial void OnShowUnearnedAccoladesOnlyChanged(bool value) => RefreshAccoladeFilter();

    /// <summary>"212 of 447 earned".</summary>
    public string AccoladesSummary => $"{Accolades.Count(a => a.IsCompleted)} of {Accolades.Count} earned";

    // Listed by the game's own categories (Survival, Hunting, Construction, Achievement...).
    private void RefreshAccoladeFilter() =>
        RefreshFilter(Accolades.OrderBy(a => a.Category.Length == 0 ? "~" : a.Category, StringComparer.OrdinalIgnoreCase), FilteredAccolades, AccoladeSearchText,
            ShowUnearnedAccoladesOnly ? a => !a.IsCompleted : null, a => a.DisplayName, a => a.Category, a => a.Description);

    /// <summary>CompletedAccolades only — PlayerTrackers/PlayerTaskListTrackers (the raw progress counters behind eligibility) are preserved untouched, out of scope for this editor.</summary>
    private void BuildAccolades()
    {
        Accolades.Clear();
        var completed = _accoladesRoot?["CompletedAccolades"] is JsonArray array
            ? array.OfType<JsonObject>()
                .Select(entry => SaveJson.String((entry["Accolade"] as JsonObject)?["RowName"]))
                .Where(name => name is not null)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)!
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rowName, info) in _gameNames.Accolades)
        {
            Accolades.Add(new SaveAccoladeViewModel(rowName, info.DisplayName, info.Description, info.Category, completed.Contains(rowName), NotifyDirtyChanged));
            seen.Add(rowName);
        }

        // A completed accolade the current data tables don't recognize (an old save vs. an updated
        // game) still shows, by RowName — same "never hide anything in the save" rule Talents' own
        // fallback path uses.
        foreach (var rowName in completed.Except(seen))
        {
            Accolades.Add(new SaveAccoladeViewModel(rowName, rowName, "", "", true, NotifyDirtyChanged));
        }

        RefreshAccoladeFilter();
        OnPropertyChanged(nameof(AccoladesSummary));
    }

    /// <summary>Writes Accolades back into its own root node — same minimal-diff order-preserving rule as ApplyProfileEdits, just against Accolades.json's CompletedAccolades array instead of Profile.json's UnlockedFlags.</summary>
    private void ApplyAccoladeEdits()
    {
        if (_accoladesRoot is null)
        {
            return;
        }

        var nowCompleted = Accolades.Where(a => a.IsCompleted).Select(a => a.RowName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newArray = new JsonArray();
        if (_accoladesRoot["CompletedAccolades"] is JsonArray existing)
        {
            foreach (var entry in existing.OfType<JsonObject>())
            {
                // Kept exactly as the game wrote it (TimeCompleted/ProspectID preserved) — only
                // whether it stays in the list changes here, never its own fields.
                if (SaveJson.String((entry["Accolade"] as JsonObject)?["RowName"]) is { } rowName && nowCompleted.Contains(rowName))
                {
                    newArray.Add(entry.DeepClone());
                }
            }
        }

        var alreadyWritten = newArray.OfType<JsonObject>()
            .Select(e => SaveJson.String((e["Accolade"] as JsonObject)?["RowName"]))
            .Where(n => n is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)!;
        foreach (var accolade in Accolades)
        {
            if (accolade.IsCompleted && !alreadyWritten.Contains(accolade.RowName))
            {
                newArray.Add(new JsonObject
                {
                    ["Accolade"] = new JsonObject { ["RowName"] = accolade.RowName, ["DataTableName"] = "D_Accolades" },
                    ["TimeCompleted"] = DateTime.Now.ToString("yyyy.MM.dd-HH.mm.ss", System.Globalization.CultureInfo.InvariantCulture),
                    ["ProspectID"] = "",
                });
            }
        }

        _accoladesRoot["CompletedAccolades"] = newArray;
    }
}
