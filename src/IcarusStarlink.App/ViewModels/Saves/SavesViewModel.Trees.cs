using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace IcarusStarlink.App.ViewModels;

public sealed partial class SavesViewModel
{
    // --- Talents and Workshop as the game's trees (TalentTreeBrowserViewModel), with List as the
    //     other way to see them, and whole-tree actions. ---

    private TalentTreeBrowserViewModel? _talentTrees;
    private TalentTreeBrowserViewModel? _workshopTrees;

    /// <summary>What the talent trees were last built from: rebuilt only when it changes, a search just dims.</summary>
    private (SaveCharacterViewModel? Character, string Kind, object? Layouts)? _talentTreesBuiltFrom;

    private bool _workshopTreesStale = true;

    public TalentTreeBrowserViewModel TalentTrees => _talentTrees ??= new(
        "Max this tree", "Reset this tree", MaxTalentGroupCommand, ResetTalentGroupCommand, QueueTalentIcons);

    private TalentTreeBrowserViewModel? _missionTrees;
    private TalentTreeBrowserViewModel? _mountTrees;
    private bool _missionTreesStale = true;
    private SaveMountViewModel? _mountTreesBuiltFor;

    /// <summary>Missions as the game's mission chains, a tree per map (D_Talents places the Prospect_* rows too).</summary>
    public TalentTreeBrowserViewModel MissionTrees => _missionTrees ??= new(
        "Mark all done", "Mark none done", MarkMissionGroupDoneCommand, ClearMissionGroupCommand, QueueTalentIcons)
    {
        Zoom = 0.25,
    };

    /// <summary>The selected mount's trees, as its talent menu draws them.</summary>
    public TalentTreeBrowserViewModel MountTrees => _mountTrees ??= new(
        "Max this tree", "Reset this tree", MaxMountGroupCommand, ResetMountGroupCommand, QueueTalentIcons)
    {
        Zoom = 0.45,
    };

    /// <summary>Missions tab: false = the game's mission chains, true = the list.</summary>
    [ObservableProperty]
    private bool _missionsAsList;

    public bool MissionsAsTree
    {
        get => !MissionsAsList;
        set => MissionsAsList = !value;
    }

    partial void OnMissionsAsListChanged(bool value) => OnPropertyChanged(nameof(MissionsAsTree));

    /// <summary>Mounts tab: false = the mount's talent trees, true = the list.</summary>
    [ObservableProperty]
    private bool _mountTalentsAsList;

    public bool MountTalentsAsTree
    {
        get => !MountTalentsAsList;
        set => MountTalentsAsList = !value;
    }

    partial void OnMountTalentsAsListChanged(bool value) => OnPropertyChanged(nameof(MountTalentsAsTree));

    private IEnumerable<SaveTalentViewModel> MissionGroupRows(string? group) =>
        WorkshopTalents.Where(t => IsMission(t) && !IsDevMission(t) && string.Equals(t.Group, group, StringComparison.Ordinal));

    /// <summary>Every mission on the map done: each one's reward turns on too, as when one is switched on.</summary>
    [RelayCommand]
    private void MarkMissionGroupDone(string? group) => SetRanks(MissionGroupRows(group), max: true);

    [RelayCommand]
    private void ClearMissionGroup(string? group) => SetRanks(MissionGroupRows(group), max: false);

    private IEnumerable<ITreeTalent> MountGroupRows(string? group) =>
        (SelectedMount?.TalentRows ?? []).Where(t => string.Equals(t.Group, group, StringComparison.Ordinal));

    [RelayCommand]
    private void MaxMountGroup(string? group) => SetRanks(MountGroupRows(group), max: true);

    [RelayCommand]
    private void ResetMountGroup(string? group) => SetRanks(MountGroupRows(group), max: false);

    private void SyncMissionTrees()
    {
        if (_missionTreesStale)
        {
            _missionTreesStale = false;
            MissionTrees.Rebuild(WorkshopTalents.Where(t => IsMission(t) && !IsDevMission(t)), _gameNames.TalentLayouts, _gameNames.TalentRankGates, MissionSearchText);
        }
        else
        {
            MissionTrees.ApplySearch(MissionSearchText);
        }
    }

    /// <summary>Builds the selected mount's trees when another mount is picked (or the same one's rows were rebuilt).</summary>
    internal void SyncMountTrees()
    {
        var mount = SelectedMount;
        if (ReferenceEquals(mount, _mountTreesBuiltFor) && MountTrees.Trees.Count > 0
            && MountTrees.Trees[0].Talents.FirstOrDefault() is { } first && mount?.TalentRows.Contains(first) == true)
        {
            return;
        }

        _mountTreesBuiltFor = mount;
        MountTrees.Rebuild(mount?.TalentRows ?? [], _gameNames.TalentLayouts, _gameNames.TalentRankGates, "");
    }

    /// <summary>Workshop nodes are 250 units to a talent's 128: drawn smaller to start with, about the same on screen.</summary>
    public TalentTreeBrowserViewModel WorkshopTrees => _workshopTrees ??= new(
        "Research all in this tree", "Clear this tree", ResearchWorkshopGroupCommand, ClearWorkshopGroupCommand, QueueTalentIcons)
    {
        Zoom = 0.3,
    };

    /// <summary>Talents tab: false = the game's trees, true = the list.</summary>
    [ObservableProperty]
    private bool _talentsAsList;

    /// <summary>Workshop tab: false = the game's trees, true = the list.</summary>
    [ObservableProperty]
    private bool _workshopAsList;

    /// <summary>Settable for the Tree half of the Tree / List switch (each half binds its own side).</summary>
    public bool TalentsAsTree
    {
        get => !TalentsAsList;
        set => TalentsAsList = !value;
    }

    public bool WorkshopAsTree
    {
        get => !WorkshopAsList;
        set => WorkshopAsList = !value;
    }

    partial void OnTalentsAsListChanged(bool value)
    {
        OnPropertyChanged(nameof(TalentsAsTree));
        if (value)
        {
            QueueTalentIcons(FilteredTalents);
        }
    }

    partial void OnWorkshopAsListChanged(bool value) => OnPropertyChanged(nameof(WorkshopAsTree));

    private void SyncTalentTrees()
    {
        var from = (SelectedCharacter, TalentKind, (object?)_gameNames.TalentLayouts);
        if (_talentTreesBuiltFrom != from)
        {
            _talentTreesBuiltFrom = from;
            TalentTrees.Rebuild((SelectedCharacter?.Talents ?? []).Where(MatchesTalentKind), _gameNames.TalentLayouts, _gameNames.TalentRankGates, TalentSearchText);
        }
        else
        {
            TalentTrees.ApplySearch(TalentSearchText);
        }

        if (TalentsAsList)
        {
            QueueTalentIcons(FilteredTalents);
        }
    }

    private void SyncWorkshopTrees()
    {
        if (_workshopTreesStale)
        {
            _workshopTreesStale = false;
            WorkshopTrees.Rebuild(WorkshopTalents.Where(t => !IsMission(t)), _gameNames.TalentLayouts, _gameNames.TalentRankGates, WorkshopSearchText);
        }
        else
        {
            WorkshopTrees.ApplySearch(WorkshopSearchText);
        }
    }

    /// <summary>A talent group's every row on the selected character, listed or not: the list heading counts the whole tree.</summary>
    public IReadOnlyList<SaveTalentViewModel> TalentGroupRows(string group) =>
        [.. (SelectedCharacter?.Talents ?? []).Where(t => !IsMission(t) && string.Equals(t.Group, group, StringComparison.Ordinal))];

    /// <summary>Every talent in the tree to its highest rank (default-unlocked ones are already the game's).</summary>
    [RelayCommand]
    private void MaxTalentGroup(string? group) => SetRanks(TalentGroupRows(group ?? ""), max: true);

    /// <summary>Every talent in the tree back to not learned.</summary>
    [RelayCommand]
    private void ResetTalentGroup(string? group) => SetRanks(TalentGroupRows(group ?? ""), max: false);

    private IEnumerable<SaveTalentViewModel> WorkshopGroupRows(string? group) =>
        WorkshopTalents.Where(t => !IsMission(t) && string.Equals(t.Group, group, StringComparison.Ordinal));

    [RelayCommand]
    private void ResearchWorkshopGroup(string? group) => SetRanks(WorkshopGroupRows(group), max: true);

    [RelayCommand]
    private void ClearWorkshopGroup(string? group) => SetRanks(WorkshopGroupRows(group), max: false);

    /// <summary>Researches every row the Workshop list shows right now (after the search and filter).</summary>
    [RelayCommand]
    private void ResearchShownWorkshop() => SetRanks(FilteredWorkshop, max: true);

    private void SetRanks(IEnumerable<ITreeTalent> rows, bool max)
    {
        using var batch = BatchedDirtyChanges();
        foreach (var row in rows.ToList())
        {
            if (max && row.MaxRank > 0 && !(row.IsDefaultUnlocked && row.MaxRank == 1))
            {
                row.Rank = Math.Max(row.Rank, row.MaxRank);
            }
            else if (!max)
            {
                row.Rank = 0;
            }
        }
    }

    /// <summary>Only the research not done yet.</summary>
    [ObservableProperty]
    private bool _showUnresearchedOnly;

    partial void OnShowUnresearchedOnlyChanged(bool value) => RefreshAccountTalentFilters();

    /// <summary>"Left to research: 12,350 Ren + 2,000 Exotics"; null when it's all researched or nothing has a cost.</summary>
    public string? WorkshopLeftToResearch
    {
        get
        {
            var left = WorkshopTalents.Where(t => !IsMission(t) && t.Rank == 0)
                .SelectMany(t => t.CostParts)
                .GroupBy(c => c.Currency)
                .Select(g => (Currency: g.Key, Amount: g.Sum(c => (long)c.Amount)))
                .OrderByDescending(c => c.Amount)
                .ToList();
            return left.Count == 0 ? null : $"Left to research: {string.Join(" + ", left.Select(c => $"{c.Amount:N0} {c.Currency}"))}";
        }
    }

    // --- Talent and research icons: loaded for what's being looked at (a tree, the list) ---

    private int _talentIconGeneration;
    private Task _pendingTalentIconLoad = Task.CompletedTask;

    /// <summary>Test seam: the talent icons asked for last.</summary>
    internal Task WaitForTalentIconsAsync() => _pendingTalentIconLoad;

    /// <summary>Loads these rows' pictures; a newer ask (another tree picked) takes over from this one.</summary>
    private void QueueTalentIcons(IEnumerable<ITreeTalent> rows)
    {
        var requests = rows.Where(t => t.Icon is null && t.IconPath is not null)
            .Select(t => (t.IconPath, false, (Action<System.Windows.Media.Imaging.BitmapImage>)(bitmap => t.Icon = bitmap)))
            .ToList();
        var generation = ++_talentIconGeneration;
        if (requests.Count > 0)
        {
            _pendingTalentIconLoad = LoadIconsAsync(requests, () => generation == _talentIconGeneration);
        }
    }

    /// <summary>The workshop's research pictures: kept apart from the talents', so picking a tree doesn't stop them.</summary>
    private int _workshopIconGeneration;

    private void QueueWorkshopIcons()
    {
        var requests = WorkshopTalents.Where(t => !IsMission(t) && t.Icon is null && t.IconPath is not null)
            .Select(t => (t.IconPath, false, (Action<System.Windows.Media.Imaging.BitmapImage>)(bitmap => t.Icon = bitmap)))
            .ToList();
        var generation = ++_workshopIconGeneration;
        if (requests.Count > 0)
        {
            _ = LoadIconsAsync(requests, () => generation == _workshopIconGeneration);
        }
    }
}
