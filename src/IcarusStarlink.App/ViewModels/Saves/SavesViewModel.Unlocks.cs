using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using IcarusStarlink.App.Services;

namespace IcarusStarlink.App.ViewModels;

public sealed partial class SavesViewModel
{
    // --- Flags (character flags on the selected character; account flags on the profile) and the
    //     slot's flags_<SteamID>.dat, which is NOT flags: the game's cached list of owned DLC ---

    public ObservableCollection<SaveFlagViewModel> AccountFlags { get; } = [];

    public ObservableCollection<SaveFlagViewModel> BinaryFlags { get; } = [];

    public ObservableCollection<SaveFlagViewModel> FilteredCharacterFlags { get; } = [];

    public ObservableCollection<SaveFlagViewModel> FilteredAccountFlags { get; } = [];

    public ObservableCollection<SaveFlagViewModel> FilteredBinaryFlags { get; } = [];

    /// <summary>The DLC section shows only when the game itself has created the file.</summary>
    public bool HasBinaryFlags => BinaryFlags.Count > 0;

    /// <summary>"21 of 28 owned".</summary>
    public string OwnedDlcSummary => $"{BinaryFlags.Count(f => f.IsUnlocked)} of {BinaryFlags.Count} owned";

    [ObservableProperty]
    private string _flagSearchText = "";

    partial void OnFlagSearchTextChanged(string value) => RefreshFlagFilter();

    // The Unlocks tab (was Flags), grouped by what each flag is for (SaveGameNames.FlagInfo,
    // researched 2026-10-23): only mission rewards and bonus talent points are worth editing;
    // markers and prompts sit under Advanced; flags nothing uses hide unless asked for.

    /// <summary>Mission rewards that unlock blueprints / workshop items (account).</summary>
    public ObservableCollection<SaveFlagViewModel> RewardFlags { get; } = [];

    /// <summary>Flags that give bonus talent points (account).</summary>
    public ObservableCollection<SaveFlagViewModel> TalentPointFlags { get; } = [];

    /// <summary>One-off markers and "has seen" prompts (account), and the character's own flags worth showing.</summary>
    public ObservableCollection<SaveFlagViewModel> AdvancedFlags { get; } = [];

    /// <summary>Deprecated, tooltip-only, test and never-granted flags — listed only with Show unused flags on.</summary>
    public ObservableCollection<SaveFlagViewModel> UnusedFlags { get; } = [];

    /// <summary>"52 of 80 on": the Unlocks sections' headings, over the rows each lists.</summary>
    public string RewardFlagsSummary => OnSummary(RewardFlags);

    public string TalentPointFlagsSummary => OnSummary(TalentPointFlags);

    public string AdvancedFlagsSummary => OnSummary(AdvancedFlags);

    public string UnusedFlagsSummary => OnSummary(UnusedFlags);

    private static string OnSummary(IReadOnlyCollection<SaveFlagViewModel> flags) => $"{flags.Count(f => f.IsUnlocked)} of {flags.Count} on";

    private void NotifyFlagSummaries()
    {
        OnPropertyChanged(nameof(RewardFlagsSummary));
        OnPropertyChanged(nameof(TalentPointFlagsSummary));
        OnPropertyChanged(nameof(AdvancedFlagsSummary));
        OnPropertyChanged(nameof(UnusedFlagsSummary));
    }

    [ObservableProperty]
    private bool _showUnusedFlags;

    partial void OnShowUnusedFlagsChanged(bool value) => RefreshFlagFilter();

    /// <summary>How many unused flags are hidden, for the switch's label.</summary>
    public string ShowUnusedFlagsLabel => $"Show flags the game doesn't use ({AccountFlags.Concat(SelectedCharacter?.Flags ?? []).Count(f => f.Kind is FlagKind.Unused or FlagKind.Unknown)})";

    /// <summary>
    /// For each mission reward: the blueprints it gates that the selected character has already
    /// learned. With the flag off they still count in game (the user's report, 2026-10-23: rewards
    /// shown off here were unlocked on the in-game blueprint screen), so the row says so.
    /// </summary>
    private void UpdateLearnedNotes()
    {
        var learned = SelectedCharacter is { } character
            ? character.Talents.Where(t => t.Rank > 0).ToDictionary(t => t.RowName, t => t.DisplayName, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var flag in AccountFlags)
        {
            var have = _gameNames.TalentsGatedByFlag.GetValueOrDefault(flag.RowName, [])
                .Where(learned.ContainsKey)
                .Select(t => learned[t])
                .Distinct()
                .ToList();
            flag.LearnedNote = have.Count == 0 || SelectedCharacter is null
                ? null
                : $"Off in the save, but {SelectedCharacter.Name} already learned {string.Join(", ", have)} — the game shows those as unlocked.";
        }

        // DLC packs: the file is only the game's cache of what it last saw; the game also checks
        // DLC itself (Steam, or an unlocker), so a pack that's off here can still be on in game
        // (the user's report, 2026-10-23). The save's own content from a pack says so.
        var researched = WorkshopTalents.Where(t => t.Rank > 0).ToDictionary(t => t.RowName, t => t.DisplayName, StringComparer.OrdinalIgnoreCase);
        foreach (var pack in BinaryFlags)
        {
            var have = _gameNames.TalentsGatedByFlag.GetValueOrDefault(pack.RowName, [])
                .Select(t => learned.GetValueOrDefault(t) ?? researched.GetValueOrDefault(t))
                .OfType<string>()
                .Distinct()
                .ToList();
            pack.LearnedNote = have.Count == 0
                ? null
                : $"Off in the cache, but this save already has {string.Join(", ", have.Take(4))}{(have.Count > 4 ? $" and {have.Count - 4} more" : "")} — the game sees the pack itself.";
        }
    }

    private void RefreshFlagFilter()
    {
        // 45 + 100 rows — a straight rebuild per keystroke is cheaper than filter plumbing.
        RefreshFilter(SelectedCharacter?.Flags ?? [], FilteredCharacterFlags, FlagSearchText, null, f => f.Name);
        RefreshFilter(AccountFlags, FilteredAccountFlags, FlagSearchText, null, f => f.Name);
        RefreshFilter(BinaryFlags, FilteredBinaryFlags, FlagSearchText, null, f => f.Name);

        var characterFlags = SelectedCharacter?.Flags ?? [];
        UpdateLearnedNotes();
        RefreshFilter(AccountFlags, RewardFlags, FlagSearchText, f => f.Kind == FlagKind.Reward, f => f.Name, f => f.Detail);
        RefreshFilter(AccountFlags, TalentPointFlags, FlagSearchText, f => f.Kind == FlagKind.TalentPoints, f => f.Name, f => f.Detail);
        RefreshFilter(AccountFlags.Concat(characterFlags), AdvancedFlags, FlagSearchText,
            f => f.Kind is FlagKind.Marker or FlagKind.Prompt or FlagKind.FromTalent, f => f.Name, f => f.Detail);
        RefreshFilter(AccountFlags.Concat(characterFlags), UnusedFlags, FlagSearchText,
            f => ShowUnusedFlags && f.Kind is FlagKind.Unused or FlagKind.Unknown, f => f.Name, f => f.Detail);
        OnPropertyChanged(nameof(ShowUnusedFlagsLabel));
        NotifyFlagSummaries();
    }

    private void BuildAccountFlags()
    {
        AccountFlags.Clear();
        var unlocked = SaveJson.IdArray(_profile?["UnlockedFlags"]).ToHashSet();
        foreach (var id in SaveFlagIds.KnownThenExtra(_gameNames.AccountFlagNames.Count, unlocked))
        {
            var flag = new SaveFlagViewModel(id, _gameNames.AccountFlagInfo(id), unlocked.Contains(id), NotifyDirtyChanged);
            flag.PropertyChanged += OnRewardFlagChanged;
            AccountFlags.Add(flag);
        }
    }

    /// <summary>
    /// The slot's flags_&lt;SteamID&gt;.dat is the game's cached list of the DLC this account owns
    /// (0-based D_DLCPackageData rows; OfflinePackageFlagsSubsystem saves and loads it — researched
    /// 2026-10-23). It used to be read as character-flag names ("Talent_RepairBench" for Dangerous
    /// Horizons). Each pack is a toggle under its real name: the user wants to be able to turn DLC
    /// and Supporter Edition content on (2026-10-23). The game may rewrite the file from its own
    /// ownership check when it starts — the page says so.
    /// </summary>
    private void BuildBinaryFlags()
    {
        BinaryFlags.Clear();
        _binaryFlagIds = null;
        if (SelectedSlot is null)
        {
            OnPropertyChanged(nameof(HasBinaryFlags));
            return;
        }

        try
        {
            _binaryFlagIds = _repository.LoadBinaryFlags(SelectedSlot.SteamId) is { } ids ? [.. ids] : null;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentOutOfRangeException)
        {
            // An unreadable file just means the DLC section stays hidden — hiding beats showing
            // toggles that couldn't be written back safely.
        }

        if (_binaryFlagIds is { } owned)
        {
            var set = owned.ToHashSet();
            foreach (var id in SaveFlagIds.KnownThenExtra(_gameNames.DlcPackageNames.Count, set))
            {
                var info = new FlagInfo(_gameNames.DlcPackageRowName(id) ?? $"DLC {id}", _gameNames.DlcPackageName(id), "", FlagKind.Marker);
                BinaryFlags.Add(new SaveFlagViewModel(id, info, set.Contains(id), NotifyDirtyChanged));
            }
        }

        OnPropertyChanged(nameof(HasBinaryFlags));
        OnPropertyChanged(nameof(OwnedDlcSummary));
    }

    private void ApplyAccountFlagEdits(JsonObject profile) =>
        profile["UnlockedFlags"] = SaveJson.MergeIds(profile["UnlockedFlags"], AccountFlags.Where(f => f.IsUnlocked).Select(f => f.Id));
}
