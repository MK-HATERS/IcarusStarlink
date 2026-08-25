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
public sealed partial class SaveTalentViewModel : ObservableObject, IDirtyTrackable
{
    private readonly Action _onDirtyChanged;
    private int _originalRank;

    public string RowName { get; }

    public string DisplayName { get; }

    public string Description { get; }

    public string Tree { get; }

    /// <summary>0 means "no cap known" — only ever the case when the game data isn't extracted; a real table row always yields at least 1.</summary>
    public int MaxRank { get; }

    /// <summary>The row's own bDefaultUnlocked: the game grants it from the start without ever writing it into the save, so rank 0 here still means "unlocked in game".</summary>
    public bool IsDefaultUnlocked { get; }

    [ObservableProperty]
    private int _rank;

    public bool IsDirty => Rank != _originalRank;

    public bool IsLearned => Rank > 0;

    /// <summary>What the player actually has in game — a rank in the save OR unlocked-by-default. The filter's "only what this character has" uses this, not IsLearned, or 49 real default talents would look missing.</summary>
    public bool IsUnlockedInGame => Rank > 0 || IsDefaultUnlocked;

    public string RankDisplay =>
        Rank == 0 && IsDefaultUnlocked ? "Default"
        : MaxRank > 0 ? $"{Rank}/{MaxRank}"
        : Rank.ToString();

    public SaveTalentViewModel(string rowName, TalentDisplayInfo info, int rank, Action onDirtyChanged)
    {
        RowName = rowName;
        DisplayName = info.DisplayName;
        Description = info.Description;
        Tree = info.Tree;
        MaxRank = info.MaxRank;
        IsDefaultUnlocked = info.IsDefaultUnlocked;
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
        OnPropertyChanged(nameof(IsUnlockedInGame));
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
public sealed record TalentDisplayInfo(string DisplayName, string Description, string Tree, int MaxRank, bool IsDefaultUnlocked)
{
    public static TalentDisplayInfo Fallback(string rowName) => new(rowName, "", "", 0, false);
}
