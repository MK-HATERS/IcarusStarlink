using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// One talent row: the save stores {RowName, Rank}; display name, description, tree, and MAX RANK
/// come from the game's own D_Talents table (max = the row's Rewards tier count — a rank above
/// that doesn't exist in the game, so the editor can't set one). Every talent the game defines is
/// listed (rank 0 = not learned), so granting a new talent is just ranking it up — at save time,
/// rank 0 entries are simply not written, which is exactly how the game itself represents
/// "doesn't have it".
/// </summary>
public sealed partial class SaveTalentViewModel : ObservableObject
{
    private readonly Action _onDirtyChanged;
    private int _originalRank;

    public string RowName { get; }

    public string DisplayName { get; }

    public string Description { get; }

    public string Tree { get; }

    /// <summary>0 means "no cap known" — only ever the case when the game data isn't extracted; a real table row always yields at least 1.</summary>
    public int MaxRank { get; }

    [ObservableProperty]
    private int _rank;

    public bool IsDirty => Rank != _originalRank;

    public bool IsLearned => Rank > 0;

    public string RankDisplay => MaxRank > 0 ? $"{Rank}/{MaxRank}" : Rank.ToString();

    public SaveTalentViewModel(string rowName, TalentDisplayInfo info, int rank, Action onDirtyChanged)
    {
        RowName = rowName;
        DisplayName = info.DisplayName;
        Description = info.Description;
        Tree = info.Tree;
        MaxRank = info.MaxRank;
        // A rank the save already carries above the table's cap is kept as-is (an older game
        // version's data, or the table not matching) — clamping on LOAD would silently edit the
        // user's save just by opening it, which the editor must never do.
        _rank = rank;
        _originalRank = rank;
        _onDirtyChanged = onDirtyChanged;
    }

    partial void OnRankChanged(int value)
    {
        OnPropertyChanged(nameof(IsLearned));
        OnPropertyChanged(nameof(RankDisplay));
        _onDirtyChanged();
    }

    public bool CanRankUp => MaxRank == 0 || Rank < MaxRank;

    [RelayCommand]
    private void RankUp()
    {
        if (CanRankUp)
        {
            Rank++;
        }

        OnPropertyChanged(nameof(CanRankUp));
    }

    [RelayCommand]
    private void RankDown()
    {
        Rank = Math.Max(0, Rank - 1);
        OnPropertyChanged(nameof(CanRankUp));
    }

    public void MarkClean() => _originalRank = Rank;
}

/// <summary>The slice of TalentInfo the row needs, with a fallback for talents the game table doesn't know (an ID from a different game version) — those show by RowName, uncapped.</summary>
public sealed record TalentDisplayInfo(string DisplayName, string Description, string Tree, int MaxRank)
{
    public static TalentDisplayInfo Fallback(string rowName) => new(rowName, "", "", 0);
}
