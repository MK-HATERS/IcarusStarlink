using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.Views;
using IcarusStarlink.Core.Activity;

namespace IcarusStarlink.App.ViewModels;

public sealed partial class SavesViewModel
{
    // --- Save check (plan step 3): SaveChecker's findings on the Overview tab, each sure fix one click. ---

    public ObservableCollection<SaveCheckFinding> CheckFindings { get; } = [];

    /// <summary>"1 problem · 4 warnings · 12 notes", or that nothing was found.</summary>
    [ObservableProperty]
    private string _checkSummary = "";

    /// <summary>Fix all also turns on the DLC / Supporters Edition packs the notes name (the user's ask, 2026-10-23). Off by default.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFixableFindings))]
    private bool _fixAllIncludesDlc;

    private bool IsInFixAll(SaveCheckFinding f) => f.CanFix && (f.Severity != SaveCheckSeverity.Note || (FixAllIncludesDlc && f.IsDlc));

    public bool HasFixableFindings => CheckFindings.Any(IsInFixAll);

    [RelayCommand]
    private void RunSaveCheck()
    {
        // One at a time: the background reads (mod sources, points, mounts) re-run it when they finish.
        lock (_saveCheckLock)
        {
            RunSaveCheckCore();
        }
    }

    private readonly System.Threading.Lock _saveCheckLock = new();

    private void RunSaveCheckCore()
    {
        CheckFindings.Clear();
        if (_loadFailed || SelectedSlot is null)
        {
            CheckSummary = "";
            OnPropertyChanged(nameof(HasFixableFindings));
            return;
        }

        foreach (var finding in SaveChecker.Check(this, _gameNames, SelectedSlot.FolderPath))
        {
            CheckFindings.Add(finding);
        }

        var parts = new[] { (SaveCheckSeverity.Problem, "problem"), (SaveCheckSeverity.Warning, "warning"), (SaveCheckSeverity.Note, "note") }
            .Select(x => (Count: CheckFindings.Count(f => f.Severity == x.Item1), Word: x.Item2))
            .Where(x => x.Count > 0)
            .Select(x => $"{x.Count} {x.Word}{(x.Count == 1 ? "" : "s")}");
        CheckSummary = CheckFindings.Count == 0 ? "Nothing found: the save looks consistent." : string.Join(" · ", parts);
        OnPropertyChanged(nameof(HasFixableFindings));
    }

    [RelayCommand]
    private void FixFinding(SaveCheckFinding? finding)
    {
        if (finding?.Fix is not { } fix)
        {
            return;
        }

        ApplyLink(fix);
        _activityLog.Log($"Save check: {finding.FixLabel} (press Save to keep it).", ActivityEntryKind.Success);
        RunSaveCheck();
    }

    /// <summary>Every sure fix for a problem or warning, plus the DLC notes with their switch on (other notes stay a choice), after one confirm listing them.</summary>
    [RelayCommand]
    private void FixAllFindings()
    {
        var fixes = CheckFindings.Where(IsInFixAll).ToList();
        if (fixes.Count == 0)
        {
            return;
        }

        if (!_dialogService.Confirm(
                $"These {fixes.Count} fixes are made in the editor; nothing is written until you press Save:\n\n"
                + string.Join("\n", fixes.Take(20).Select(f => $"• {f.Area}: {f.FixLabel}"))
                + (fixes.Count > 20 ? $"\n• and {fixes.Count - 20} more" : ""),
                "Fix what the save check found?", ThemedConfirmSeverity.Question))
        {
            return;
        }

        using (BatchedDirtyChanges())
        {
            ApplyLink(() =>
            {
                foreach (var finding in fixes)
                {
                    finding.Fix!();
                }
            });
        }

        _activityLog.Log($"Save check: made {fixes.Count} fixes (press Save to keep them).", ActivityEntryKind.Success);
        RunSaveCheck();
    }

    // --- Leftovers from mods (the user's ask, 2026-10-23): every talent, research, mission and
    //     banked item the game doesn't have is labelled with where it comes from (SaveModSources),
    //     and the ones no installed or Library mod gives can be taken out. Taking them out is an
    //     ordinary edit (rank 0 isn't written; an item is dropped), saved with Save like any other. ---

    private Task _pendingModSources = Task.CompletedTask;

    /// <summary>Test seam: the background read of where mod rows come from.</summary>
    internal Task WaitForModSourcesAsync() => _pendingModSources;

    /// <param name="reread">Read every Library mod and the installed paks again; otherwise the last read labels the rows (see GameContentStale).</param>
    private async Task ClassifyModRowsAsync(bool reread)
    {
        if (_readModSources is not { } read)
        {
            return;
        }

        if (reread || _modSources is null)
        {
            try
            {
                _modSources = await Task.Run(read);
            }
            catch (Exception)
            {
                _gameContentStale = true;
                return;
            }
        }

        var index = _modSources;
        ClassifyTalents(Characters.SelectMany(c => c.Talents).Concat(WorkshopTalents));

        foreach (var item in MetaInventoryItems)
        {
            item.Source = index.Classify(SaveModSources.ItemsFile, item.RowName);
        }

        NotifyLeftoversChanged();

        // Mis-named items are only known now.
        RunSaveCheck();
    }

    /// <summary>The last read of where mod rows come from, for rows made after it (a duplicated character); null until read.</summary>
    private SaveModSources.Index? _modSources;

    private void ClassifyTalents(IEnumerable<SaveTalentViewModel> talents)
    {
        if (_modSources is not { } index)
        {
            return;
        }

        foreach (var talent in talents)
        {
            talent.Source = index.Classify(SaveModSources.TalentsFile, talent.RowName);
        }
    }

    private IEnumerable<(string Where, SaveTalentViewModel Talent)> LeftoverTalents() =>
        Characters.SelectMany(c => c.Talents.Select(t => (Where: c.Name, Talent: t)))
            .Concat(WorkshopTalents.Select(t => (Where: IsMission(t) ? "Missions" : "Workshop", Talent: t)))
            .Where(x => x.Talent.Rank > 0 && x.Talent.Source?.IsRemovable == true);

    private IEnumerable<SaveInventoryItemViewModel> LeftoverItems() => MetaInventoryItems.Where(i => i.Source?.IsRemovable == true);

    public bool HasLeftovers => LeftoverTalents().Any() || LeftoverItems().Any();

    /// <summary>"MKHATERS: 14 talents · Workshop: 30 · Items: 2".</summary>
    public string LeftoversSummary =>
        string.Join(" · ", LeftoverTalents().GroupBy(x => x.Where).Select(g => $"{g.Key}: {g.Count()}")
            .Concat(LeftoverItems().Count() is > 0 and var items ? [$"Items: {items}"] : []));

    /// <summary>What the installed mods add that this save uses: "Ferani Buyable Beasts".</summary>
    public string? ModsInUseSummary
    {
        get
        {
            var inUse = Characters.SelectMany(c => c.Talents).Concat(WorkshopTalents).Where(t => t.Rank > 0).Select(t => t.Source)
                .Concat(MetaInventoryItems.Select(i => i.Source))
                .Count(s => s?.Origin is SaveRowOrigin.InstalledMod or SaveRowOrigin.LibraryModNotInstalled);
            return inUse == 0 ? null : $"{inUse} more come from mods you have, and stay.";
        }
    }

    private void NotifyLeftoversChanged()
    {
        OnPropertyChanged(nameof(HasLeftovers));
        OnPropertyChanged(nameof(LeftoversSummary));
        OnPropertyChanged(nameof(ModsInUseSummary));
    }

    [RelayCommand]
    private void RemoveLeftovers()
    {
        var talents = LeftoverTalents().ToList();
        var items = LeftoverItems().ToList();
        if (talents.Count + items.Count == 0)
        {
            return;
        }

        var lines = talents.GroupBy(x => x.Where)
            .Select(g => $"{g.Key}: {string.Join(", ", g.Select(x => x.Talent.DisplayName).Take(8))}{(g.Count() > 8 ? $" and {g.Count() - 8} more" : "")}")
            .Concat(items.Count > 0 ? [$"Items: {string.Join(", ", items.Select(i => i.DisplayName).Take(8))}{(items.Count > 8 ? $" and {items.Count - 8} more" : "")}"] : []);
        if (!_dialogService.Confirm(
                "These come from mods you no longer have (or the game removed them), and nothing installed or in your Library gives them:\n\n"
                + string.Join("\n\n", lines)
                + "\n\nThey're taken out of the editor now; nothing is written until you press Save, which backs the slot up first. "
                + "Talent points they used come back. Ren spent on removed research isn't refunded.",
                "Remove leftovers from old mods?", ThemedConfirmSeverity.Warning))
        {
            return;
        }

        using (BatchedDirtyChanges())
        {
            foreach (var (_, talent) in talents)
            {
                talent.Rank = 0;
            }
        }

        foreach (var item in items)
        {
            MetaInventoryItems.Remove(item);
            FilteredMetaInventoryItems.Remove(item);
            _metaInventoryDirty = true;
        }

        OnPropertyChanged(nameof(ItemsEmptyText));
        NotifyDirtyChanged();
        NotifyLeftoversChanged();
        _activityLog.Log($"Took {talents.Count + items.Count} leftovers from old mods out of the save (press Save to keep that).", ActivityEntryKind.Success);
    }
}
