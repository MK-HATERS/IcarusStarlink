using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.Services;
using IcarusStarlink.Core.Activity;

namespace IcarusStarlink.App.ViewModels;

public sealed partial class SavesViewModel
{
    // --- Points used and available (the user's ask, 2026-10-23: their own points are modded, so
    //     budgets come from the curves as installed, mods and Remove Level Cap included). ---

    private Task _pendingPlayerGrowth = Task.CompletedTask;

    internal Task WaitForPlayerGrowthAsync() => _pendingPlayerGrowth;

    /// <param name="reread">Read the installed paks again; otherwise the last read is applied (see GameContentStale).</param>
    private async Task ReadPlayerGrowthAsync(bool reread)
    {
        if (_readPlayerGrowth is not { } read)
        {
            return;
        }

        if (reread)
        {
            try
            {
                _playerGrowth = await Task.Run(read);
            }
            catch (Exception)
            {
                _playerGrowth = null;
                _gameContentStale = true;
            }
        }

        OnPropertyChanged(nameof(TalentRanksSummary));
        OnPropertyChanged(nameof(PointsNotes));
        OnPropertyChanged(nameof(HasPointsNotes));
        BuildPointsEditors();
        RefreshOverBudget();

        // Over-budget notes need the curves.
        RunSaveCheck();
    }

    // --- Change points per level: a "Starlink Player Points" Library mod (PlayerPointsMod). ---

    public ObservableCollection<PointsTreeEditor> PointsEditors { get; } = [];

    public bool CanChangePoints => _pointsMod is not null && PointsEditors.Count > 0;

    /// <summary>What happened last ("Made Starlink Player Points: ..."), or what's installed.</summary>
    [ObservableProperty]
    private string? _pointsModStatus;

    /// <summary>The Library folder of the mod just made, for "Turn on and go to Merge & Install".</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TurnOnPointsModCommand))]
    private string? _pointsModFolder;

    private void BuildPointsEditors()
    {
        // What the user typed stays: a reload or a re-read of the curves used to put the
        // multipliers back to what's installed.
        var typed = PointsEditors.ToDictionary(e => e.Field, e => (e.MultiplierText, e.KeepRising));
        PointsEditors.Clear();
        IReadOnlyDictionary<string, PointsCurveChange> lastMade;
        try
        {
            lastMade = _pointsMod?.LastSettings() ?? new Dictionary<string, PointsCurveChange>();
        }
        catch (Exception)
        {
            lastMade = new Dictionary<string, PointsCurveChange>();
        }

        if (_playerGrowth is { } growth)
        {
            void Add(string field, string label, IcarusStarlink.PakIO.Assets.GrowthCurve? installed)
            {
                if (growth.GameCurvesByField.GetValueOrDefault(field) is { } gameCurve)
                {
                    var editor = new PointsTreeEditor(field, label, gameCurve, installed, () => SelectedLevel, () => field == "TalentPointsPerLevel" ? TalentPointBonus : 0,
                        lastMade.GetValueOrDefault(field));
                    if (typed.TryGetValue(field, out var kept))
                    {
                        editor.MultiplierText = kept.MultiplierText;
                        editor.KeepRising = kept.KeepRising;
                    }

                    PointsEditors.Add(editor);
                }
            }

            Add("TalentPointsPerLevel", "Talents", growth.TalentPoints);
            Add("BlueprintPointsPerLevel", "Blueprints", growth.BlueprintPoints);
            Add("SoloPointsPerLevel", "Solo", growth.SoloPoints);
        }

        try
        {
            PointsModFolder = _pointsMod?.Existing?.FolderName;
        }
        catch (Exception)
        {
            PointsModFolder = null;
        }

        PointsModStatus = PointsModFolder is null ? null : $"{PlayerPointsMod.DisplayName} is in your Library.";
        OnPropertyChanged(nameof(CanChangePoints));
    }

    private void RefreshPointsEditors()
    {
        RefreshOverBudget();
        foreach (var editor in PointsEditors)
        {
            editor.Refresh();
        }
    }

    [RelayCommand]
    private void MakePointsMod()
    {
        if (_pointsMod is null || _playerGrowth is not { } growth)
        {
            return;
        }

        if (PointsEditors.FirstOrDefault(e => !e.IsValid) is { } bad)
        {
            PointsModStatus = $"{bad.Label}: enter a number above 0.";
            return;
        }

        try
        {
            var folder = _pointsMod.Build([.. PointsEditors.Select(e => new PointsCurveChange(e.Field, e.Multiplier, e.KeepRising))], growth);
            PointsModFolder = folder;
            WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
            PointsModStatus = folder is null
                ? "Everything matches the game's own points, so there's no mod to make (an old one was taken out of the Library)."
                : $"Made {PlayerPointsMod.DisplayName} in your Library. Turn it on in Merge & Install and rebuild; it counts in game, and here, once installed.";
            _activityLog.Log(PointsModStatus, ActivityEntryKind.Success);
        }
        catch (Exception ex)
        {
            PointsModStatus = $"Couldn't make the mod: {ex.Message}";
            _activityLog.Log(PointsModStatus, ActivityEntryKind.Warning);
        }
    }

    private bool CanTurnOnPointsMod() => PointsModFolder is not null;

    [RelayCommand(CanExecute = nameof(CanTurnOnPointsMod))]
    private void TurnOnPointsMod()
    {
        if (PointsModFolder is { } folder)
        {
            WeakReferenceMessenger.Default.Send(new TurnOnModMessage(folder));
        }
    }

    /// <summary>The selected character's level, from its XP on the installed XP curve; null when it can't be worked out.</summary>
    private int? SelectedLevel => SelectedCharacter is { } character ? LevelOf(character) : null;

    private int? LevelOf(SaveCharacterViewModel character) =>
        _playerGrowth is { Experience: { } xpCurve } growth && long.TryParse(character.XpText, out var xp)
            ? xpCurve.LevelFor(xp, growth.MaxLevel > 0 ? growth.MaxLevel : int.MaxValue)
            : null;

    /// <summary>Each tree's ranks used and points available for a character; null when the curves couldn't be read.</summary>
    internal IReadOnlyList<(string Label, int Used, int Available)>? BudgetOf(SaveCharacterViewModel character)
    {
        if (LevelOf(character) is not { } level || _playerGrowth is not { } growth)
        {
            return null;
        }

        var budget = new List<(string, int, int)>();
        void Add(string label, string model, IcarusStarlink.PakIO.Assets.GrowthCurve? curve, int bonus)
        {
            if (curve is not null)
            {
                budget.Add((label, character.Talents.Where(t => t.Model == model).Sum(t => t.Rank), (int)Math.Floor(curve.Evaluate(level)) + bonus));
            }
        }

        Add("Talents", "Player", growth.TalentPoints, TalentPointBonus);
        Add("Blueprints", "Blueprint", growth.BlueprintPoints, 0);
        Add("Solo", "Solo", growth.SoloPoints, 0);
        return budget;
    }

    /// <summary>"Talents by 477, Solo by 15"; null when every tree is within its budget (or it can't be told).</summary>
    internal string? OverBudgetOf(SaveCharacterViewModel character) =>
        BudgetOf(character)?.Where(b => b.Used > b.Available).ToList() is { Count: > 0 } over
            ? string.Join(", ", over.Select(b => $"{b.Label} by {b.Used - b.Available:N0}"))
            : null;

    /// <summary>The banner on the Talents tab when the selected character has more ranks than points (the user's ask, 2026-10-23).</summary>
    public string? OverBudgetNote =>
        SelectedCharacter is { } character && OverBudgetOf(character) is { } over
            ? $"Over budget: {over}. The game keeps every rank you have, but those trees show no points left, so nothing more can be bought there in game until you're under. Raise the budget with Change points per level (How these points work, below), or lower ranks here."
            : null;

    private void RefreshOverBudget()
    {
        foreach (var character in Characters)
        {
            character.OverBudget = OverBudgetOf(character);
        }

        OnPropertyChanged(nameof(OverBudgetNote));
    }

    /// <summary>Bonus talent points from the account's mission rewards that are on (D_PlayerTalentModifiers).</summary>
    private int TalentPointBonus => AccountFlags.Where(f => f.IsUnlocked).Sum(f => _gameNames.TalentPointModifiers.GetValueOrDefault(f.RowName));

    private int RanksSpent(string model) => (SelectedCharacter?.Talents ?? []).Where(t => t.Model == model).Sum(t => t.Rank);

    /// <summary>"Level 313 · Talents 583 of 111 (over) · Blueprints 701 of 1,096 · Solo 45 of 30 (over)" for the selected character.</summary>
    public string TalentRanksSummary
    {
        get
        {
            if (SelectedLevel is not { } level || _playerGrowth is not { } growth)
            {
                return $"Ranks spent: Talents {RanksSpent("Player"):N0} · Blueprints {RanksSpent("Blueprint"):N0} · Solo {RanksSpent("Solo"):N0}";
            }

            string Part(string label, string model, IcarusStarlink.PakIO.Assets.GrowthCurve? curve, int bonus)
            {
                var used = RanksSpent(model);
                if (curve is null)
                {
                    return $"{label} {used:N0} used";
                }

                var available = (int)Math.Floor(curve.Evaluate(level)) + bonus;
                return $"{label} {used:N0} of {available:N0}{(used > available ? " (over)" : "")}";
            }

            return $"Level {level:N0} · {Part("Talents", "Player", growth.TalentPoints, TalentPointBonus)} · "
                + $"{Part("Blueprints", "Blueprint", growth.BlueprintPoints, 0)} · {Part("Solo", "Solo", growth.SoloPoints, 0)}";
        }
    }

    public bool HasPointsNotes => PointsNotes.Count > 0;

    /// <summary>Where the numbers above come from, in plain words.</summary>
    public IReadOnlyList<string> PointsNotes
    {
        get
        {
            if (SelectedLevel is not { } level || _playerGrowth is not { Experience: { } xpCurve } growth || SelectedCharacter is not { } character)
            {
                return [];
            }

            var notes = new List<string>();
            var xp = long.TryParse(character.XpText, out var parsed) ? parsed : 0;
            notes.Add($"Level {level:N0} comes from {xp:N0} XP. "
                + (xpCurve.SlopeAfter > 0 ? $"Past level {xpCurve.Last.Time:0} each level takes {xpCurve.SlopeAfter:N0} XP. " : "")
                + $"Levels go up to {growth.MaxLevel:N0}; the game's menus show up to {growth.MaxDisplayLevel:N0}."
                + (growth.IsStarlinkLevelCap ? " That's Starlink's Remove Level Cap (Merge & Install), which is installed."
                    : growth.LevelCapChanged ? $" An installed mod changes it (the game's own cap is {growth.GameMaxLevel:N0}); it isn't Starlink's Remove Level Cap."
                    : " That's the game's own cap: no installed mod changes it."));

            void Curve(string label, IcarusStarlink.PakIO.Assets.GrowthCurve? curve)
            {
                if (curve is null)
                {
                    return;
                }

                notes.Add(curve.SlopeAfter > 0
                    ? $"{label} keep rising past level {curve.Last.Time:0} ({curve.Last.Value:N0} there), {curve.SlopeAfter:0.##} a level."
                    : $"{label} stop rising at level {curve.Last.Time:0}: {curve.Last.Value:N0} at most.");
            }

            Curve("Talent points", growth.TalentPoints);
            Curve("Blueprint points", growth.BlueprintPoints);
            Curve("Solo points", growth.SoloPoints);
            if (TalentPointBonus > 0)
            {
                notes.Add($"+{TalentPointBonus} talent points from mission rewards that are on (Unlocks tab, bonus talent points).");
            }

            notes.Add(growth.CurvesFromMods.Count > 0
                ? $"From an installed mod: {string.Join(", ", growth.CurvesFromMods)}. The rest are the game's own."
                : "All four curves are the game's own; no installed mod changes them.");
            if (TalentRanksSummary.Contains("(over)", StringComparison.Ordinal))
            {
                notes.Add("Over: more ranks than the budget. The save keeps every rank and the game doesn't take any away; the tree just shows no points left to spend.");
            }

            return notes;
        }
    }
}
