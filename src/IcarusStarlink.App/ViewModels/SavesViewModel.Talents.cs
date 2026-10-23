using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace IcarusStarlink.App.ViewModels;

public sealed partial class SavesViewModel
{
    // --- Talents (character talents on the selected character; workshop research on the profile) ---

    public ObservableCollection<SaveTalentViewModel> WorkshopTalents { get; } = [];

    public ObservableCollection<SaveTalentViewModel> FilteredTalents { get; } = [];

    [ObservableProperty]
    private string _talentSearchText = "";

    /// <summary>Off = only what the character has; on = every talent the game defines, rank 0 included — turning one up grants it.</summary>
    [ObservableProperty]
    private bool _showUnlearnedTalents;

    partial void OnTalentSearchTextChanged(string value) => _talentSearchDebounceTimer.Restart();

    partial void OnShowUnlearnedTalentsChanged(bool value) => RefreshTalentFilter();

    /// <summary>The Talents tab's "Show": All, Talents (the Player trees), Blueprints, Solo.</summary>
    public IReadOnlyList<string> TalentKindOptions { get; } = ["All", "Talents", "Blueprints", "Solo"];

    [ObservableProperty]
    private string _talentKind = "All";

    partial void OnTalentKindChanged(string value) => RefreshTalentFilter();

    private bool MatchesTalentKind(SaveTalentViewModel talent) => TalentKind switch
    {
        "Talents" => talent.Model == "Player",
        "Blueprints" => talent.Model == "Blueprint",
        "Solo" => talent.Model == "Solo",
        _ => true,
    };

    private void RefreshTalentFilter()
    {
        // "Only what this character has" includes bDefaultUnlocked talents — the game grants those
        // from the start without writing them into the save, so rank alone under-reports.
        RefreshFilter(
            (SelectedCharacter?.Talents ?? []).OrderBy(t => t.Group, StringComparer.OrdinalIgnoreCase), FilteredTalents, TalentSearchText,
            t => (ShowUnlearnedTalents || t.IsUnlockedInGame || t.IsDirty) && MatchesTalentKind(t),
            t => t.DisplayName, t => t.RowName, t => t.Group);
        OnPropertyChanged(nameof(TalentRanksSummary));
        OnPropertyChanged(nameof(PointsNotes));
        OnPropertyChanged(nameof(HasPointsNotes));
        RefreshPointsEditors();
    }

    /// <summary>Marks every talent and research row that's for a DLC or the Supporters Edition (the user's ask, 2026-10-23).</summary>
    private void ApplyPackNotes() => ApplyPackNotes(Characters.SelectMany(c => c.Talents).Concat(WorkshopTalents));

    private void ApplyPackNotes(IEnumerable<SaveTalentViewModel> talents)
    {
        var rules = _gameNames.TalentRules;
        foreach (var talent in talents)
        {
            var packs = (rules.GetValueOrDefault(talent.RowName)?.RequiredFlags ?? [])
                .Where(f => f.Table == "D_DLCPackageData")
                .Select(f => _gameNames.DlcPackLabel(f.Row))
                .Distinct()
                .ToList();
            talent.PackNote = packs.Count == 0 ? null : $"For {string.Join(" and ", packs)}";
        }
    }
}
