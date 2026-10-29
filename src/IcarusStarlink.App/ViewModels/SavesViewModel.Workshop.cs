using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using IcarusStarlink.Core.Activity;

namespace IcarusStarlink.App.ViewModels;

public sealed partial class SavesViewModel
{
    // --- Workshop research and Missions: both live in the profile's Talents list (WorkshopTalents
    //     holds them all and is what Save writes); the tabs show its Workshop and Prospect parts. ---

    public ObservableCollection<SaveTalentViewModel> FilteredWorkshop { get; } = [];

    public ObservableCollection<SaveTalentViewModel> FilteredMissions { get; } = [];

    [ObservableProperty]
    private string _workshopSearchText = "";

    [ObservableProperty]
    private string _missionSearchText = "";

    partial void OnWorkshopSearchTextChanged(string value) => RefreshAccountTalentFilters();

    partial void OnMissionSearchTextChanged(string value) => RefreshAccountTalentFilters();

    /// <summary>Only the missions not done yet.</summary>
    [ObservableProperty]
    private bool _showUndoneMissionsOnly;

    partial void OnShowUndoneMissionsOnlyChanged(bool value) => RefreshAccountTalentFilters();

    internal static bool IsMission(SaveTalentViewModel t) => t.Model == "Prospect" || t.RowName.StartsWith("Prospect_", StringComparison.OrdinalIgnoreCase);

    /// <summary>The developers' own test missions (the Prospect_Dev tree): kept in the save, never listed.</summary>
    private static bool IsDevMission(SaveTalentViewModel t) => t.Tree.Equals("Prospect_Dev", StringComparison.OrdinalIgnoreCase) && t.Rank == 0;

    public string WorkshopSummary
    {
        get
        {
            var research = WorkshopTalents.Where(t => !IsMission(t)).ToList();
            return $"{research.Count(t => t.Rank > 0)} of {research.Count} researched";
        }
    }

    public string MissionsSummary
    {
        get
        {
            var missions = WorkshopTalents.Where(t => IsMission(t) && !IsDevMission(t)).ToList();
            return $"{missions.Count(t => t.Rank > 0)} of {missions.Count} done";
        }
    }

    private void RefreshAccountTalentFilters()
    {
        RefreshFilter(SaveGroupOrder.Talents(WorkshopTalents.Where(t => !IsMission(t))), FilteredWorkshop, WorkshopSearchText, null,
            t => t.DisplayName, t => t.RowName, t => t.Group);
        RefreshFilter(SaveGroupOrder.Missions(WorkshopTalents.Where(t => IsMission(t) && !IsDevMission(t))), FilteredMissions, MissionSearchText,
            ShowUndoneMissionsOnly ? t => t.Rank == 0 : null,
            t => t.DisplayName, t => t.RowName, t => t.Group);
        OnPropertyChanged(nameof(WorkshopSummary));
        OnPropertyChanged(nameof(MissionsSummary));
    }

    // --- Mission <-> reward links (the user's ask, 2026-10-23): marking a mission done turns on
    // the unlocks it rewards, switching a reward on marks its mission done, and a message says so.
    // Only switching ON carries over: the game never takes a reward back when a mission is undone. ---

    private readonly Dictionary<SaveTalentViewModel, List<SaveFlagViewModel>> _rewardsOfMission = [];

    /// <summary>Each mission row and the reward rows it gives, for the save check.</summary>
    internal IEnumerable<(SaveTalentViewModel Mission, IReadOnlyList<SaveFlagViewModel> Rewards)> MissionRewards =>
        _rewardsOfMission.Select(kv => (kv.Key, (IReadOnlyList<SaveFlagViewModel>)kv.Value));
    private readonly Dictionary<SaveFlagViewModel, List<SaveTalentViewModel>> _missionsOfReward = [];
    private bool _applyingLink;

    /// <summary>Pairs each mission row with its reward rows and writes both rows' hints; after both lists are built.</summary>
    private void LinkMissionRewards()
    {
        _rewardsOfMission.Clear();
        _missionsOfReward.Clear();
        var flagsByRow = new Dictionary<string, SaveFlagViewModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var flag in AccountFlags)
        {
            flagsByRow.TryAdd(flag.RowName, flag);
            flag.LinkHint = null;
            flag.ShowLinkHint = false;
        }

        foreach (var mission in WorkshopTalents.Where(IsMission))
        {
            var rewards = _gameNames.MissionRewardFlags.GetValueOrDefault(mission.RowName, [])
                .Select(row => flagsByRow.GetValueOrDefault(row))
                .OfType<SaveFlagViewModel>()
                .ToList();
            mission.LinkHint = rewards.Count == 0
                ? null
                : $"Reward: {string.Join(", ", rewards.Select(r => r.Name))}";
            if (rewards.Count == 0)
            {
                continue;
            }

            _rewardsOfMission[mission] = rewards;
            foreach (var reward in rewards)
            {
                (_missionsOfReward.TryGetValue(reward, out var list) ? list : _missionsOfReward[reward] = []).Add(mission);
            }
        }

        foreach (var (reward, missions) in _missionsOfReward)
        {
            reward.LinkHint = missions.Count == 1
                ? $"Switching it on marks {missions[0].DisplayName} done on the Missions tab too."
                : $"Comes from {string.Join(" or ", missions.Select(m => m.DisplayName))}; switching it on marks none of them done.";
            reward.ShowLinkHint = missions.Count != 1;
        }
    }

    private void OnMissionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_applyingLink || e.PropertyName != nameof(SaveTalentViewModel.Rank)
            || sender is not SaveTalentViewModel { Rank: > 0 } mission || !_rewardsOfMission.TryGetValue(mission, out var rewards))
        {
            return;
        }

        var turnedOn = rewards.Where(r => !r.IsUnlocked).ToList();
        ApplyLink(() => turnedOn.ForEach(r => r.IsUnlocked = true));
        if (turnedOn.Count > 0)
        {
            _activityLog.Log($"Marked {mission.DisplayName} done, so its reward is on too: {string.Join(", ", turnedOn.Select(r => r.Name))} (Unlocks tab).", ActivityEntryKind.Success);
        }
    }

    private void OnRewardFlagChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_applyingLink || e.PropertyName != nameof(SaveFlagViewModel.IsUnlocked)
            || sender is not SaveFlagViewModel { IsUnlocked: true } reward || !_missionsOfReward.TryGetValue(reward, out var missions))
        {
            return;
        }

        // Only a reward that comes from one mission says which mission was done; one that several
        // missions give ("A or B") marks none (review, 2026-10-23: it marked them all).
        if (missions.Count != 1)
        {
            return;
        }

        var marked = missions.Where(m => m.Rank == 0).ToList();
        ApplyLink(() => marked.ForEach(m => m.IsDone = true));
        if (marked.Count > 0)
        {
            _activityLog.Log($"Turned on {reward.Name}, so its mission is marked done too: {string.Join(", ", marked.Select(m => m.DisplayName))} (Missions tab).", ActivityEntryKind.Success);
        }
    }

    private void ApplyLink(Action change)
    {
        _applyingLink = true;
        try
        {
            change();
        }
        finally
        {
            _applyingLink = false;
        }
    }

    private void BuildWorkshopTalents()
    {
        WorkshopTalents.Clear();

        // The profile's own list first (its order), then any Workshop_* talent the game defines
        // that isn't researched yet — rank one up to research it — and missions not done yet
        // (Prospect_*), so the Missions tab lists every one.
        var others = _gameNames.Talents.Keys.Where(rowName =>
            rowName.StartsWith("Workshop_", StringComparison.OrdinalIgnoreCase) || rowName.StartsWith("Prospect_", StringComparison.OrdinalIgnoreCase));
        foreach (var (rowName, rank) in TalentArray.LearnedThenOthers(TalentArray.Read(_profile?["Talents"]), others))
        {
            AddWorkshopTalent(new SaveTalentViewModel(rowName, SaveCharacterViewModel.DisplayInfoFor(_gameNames, rowName), rank, NotifyDirtyChanged));
        }

        LinkMissionRewards();
        RefreshAccountTalentFilters();
    }

    private void AddWorkshopTalent(SaveTalentViewModel talent)
    {
        if (IsMission(talent))
        {
            talent.PropertyChanged += OnMissionChanged;
        }

        WorkshopTalents.Add(talent);
    }

    private void ApplyWorkshopTalentEdits(JsonObject profile) =>
        profile["Talents"] = TalentArray.Write(profile["Talents"], [.. WorkshopTalents.Select(t => (t.RowName, t.Rank))]);
}
