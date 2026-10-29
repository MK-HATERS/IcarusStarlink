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
public sealed partial class SaveTalentViewModel : DirtyRow
{
    private int _originalRank;

    public string RowName { get; }

    public string DisplayName { get; }

    public string Description { get; }

    public string Tree { get; }

    /// <summary>False for a row the game's tables don't list; its row name shows beside the readable one.</summary>
    public bool IsInGameTables { get; }

    /// <summary>0 means "no cap known" — only ever the case when the game data isn't extracted; a real table row always yields at least 1.</summary>
    public int MaxRank { get; }

    /// <summary>The row's own bDefaultUnlocked: the game grants it from the start without ever writing it into the save, so rank 0 here still means "unlocked in game".</summary>
    public bool IsDefaultUnlocked { get; }

    /// <summary>The heading it's listed under: "Survival · Resources", "Tier 2 · Crafting Bench", "Olympus".</summary>
    public string Group { get; }

    /// <summary>Where its group is listed (SaveGroupOrder): blueprint tiers 1 to 5, the game's tree order; int.MaxValue last.</summary>
    public int GroupOrder { get; }

    /// <summary>Its row's place in the game's D_Talents: missions list in this order.</summary>
    public int Order { get; }

    /// <summary>Player, Blueprint, Solo, Workshop or Prospect.</summary>
    public string Model { get; }

    /// <summary>Workshop research: "50 Ren".</summary>
    public string? Cost { get; }

    /// <summary>Missions (one rank): done or not, as a switch.</summary>
    public bool IsDone
    {
        get => Rank > 0;
        set => Rank = value ? Math.Max(1, Rank) : 0;
    }

    [ObservableProperty]
    private int _rank;

    /// <summary>Missions: "Reward: Medical Device" (the tab's description says marking it done turns that on in Unlocks).</summary>
    [ObservableProperty]
    private string? _linkHint;

    /// <summary>Where a talent the game doesn't have comes from (SaveModSources); null for the game's own.</summary>
    [ObservableProperty]
    private Services.SaveRowSource? _source;

    /// <summary>"For the Dangerous Horizons DLC", "For the Supporters Edition": the pack it needs (D_Talents RequiredFlags).</summary>
    [ObservableProperty]
    private string? _packNote;

    public override bool IsDirty => Rank != _originalRank;

    /// <summary>What the player actually has in game — a rank in the save OR unlocked-by-default. The filter's "only what this character has" uses this, not just Rank > 0, or 49 real default talents would look missing.</summary>
    public bool IsUnlockedInGame => Rank > 0 || IsDefaultUnlocked;

    public string RankDisplay =>
        Rank == 0 && IsDefaultUnlocked ? "Default"
        : MaxRank > 0 ? $"{Rank}/{MaxRank}"
        : Rank.ToString();

    public SaveTalentViewModel(string rowName, TalentDisplayInfo info, int rank, Action onDirtyChanged)
        : base(onDirtyChanged)
    {
        RowName = rowName;
        DisplayName = info.DisplayName;
        Description = info.Description;
        Tree = info.Tree;
        IsInGameTables = info.IsInGameTables;
        MaxRank = info.MaxRank;
        IsDefaultUnlocked = info.IsDefaultUnlocked;
        Group = info.Group.Length > 0 ? info.Group : info.Tree;
        GroupOrder = info.GroupOrder;
        Order = info.Order;
        Model = info.Model;
        Cost = info.Cost;
        // A rank the save already carries above the table's cap is kept as-is (an older game
        // version's data, or the table not matching) — clamping on LOAD would silently edit the
        // user's save just by opening it, which the editor must never do.
        _rank = rank;
        _originalRank = rank;
    }

    partial void OnRankChanged(int value)
    {
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(IsUnlockedInGame));
        OnPropertyChanged(nameof(RankDisplay));
        Changed();
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

    protected override void Snapshot() => _originalRank = Rank;
}

/// <summary>The slice of TalentInfo the row needs, with a fallback for talents the game table doesn't know (an ID from a different game version) — those show by RowName, uncapped.</summary>
/// <param name="Group">"Survival · Resources" — the tree it's listed under.</param>
/// <param name="Model">Player, Blueprint, Solo, Workshop or Prospect (SaveGameNames.TreeInfo).</param>
/// <param name="GroupOrder">Where its group is listed (TreeInfo.Order): blueprint tiers 1 to 5, trees in the game's order.</param>
/// <param name="Order">Its row's place in D_Talents (TalentInfo.Order).</param>
public sealed record TalentDisplayInfo(string DisplayName, string Description, string Tree, int MaxRank, bool IsDefaultUnlocked,
    string Group = "", string Model = "", string? Cost = null, int GroupOrder = int.MaxValue, int Order = int.MaxValue)
{
    public static TalentDisplayInfo Fallback(string rowName) =>
        new(ReadableRowName(rowName), "", "", 0, false, "Not in the game's tables (from a mod, or removed)") { IsInGameTables = false };

    /// <summary>The game's own tables list it (false: shown by its row name, which the row then shows faint as well).</summary>
    public bool IsInGameTables { get; init; } = true;

    /// <summary>"Prospect_OLY_Glacier_Exploration" reads "OLY Glacier Exploration"; other row names stay as they are.</summary>
    public static string ReadableRowName(string rowName) =>
        rowName.StartsWith("Prospect_", StringComparison.OrdinalIgnoreCase) && rowName.Length > "Prospect_".Length
            ? rowName["Prospect_".Length..].Replace('_', ' ').Trim()
            : rowName;
}
