using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.Views;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Diffing;
using IcarusStarlink.PakIO.Exmod;
using IcarusStarlink.PakIO.GameplayToggles;
using IcarusStarlink.PakIO.Rebuild;

namespace IcarusStarlink.App.ViewModels;

/// <summary>Conflicts: the one conflict check behind the badge, the review window and the build's picks, the saved winners, and moving winners between ids and the names the window shows.</summary>
public sealed partial class MergeInstallViewModel
{
    // --- Advanced conflict picker ---
    // Null means "no manual picks" (every conflict resolves via the registry's own default rule).
    // Invalidated (reset to null) whenever Queue changes at all — a pick's own index only means
    // the mod it meant when it was made; any Add/Remove/Move/Clear could change which mod that
    // index now refers to, or whether the field is even still a conflict at all.
    private IReadOnlyDictionary<(string CurrentFile, string ItemName, string FieldName), int>? _manualPicks;

    /// <summary>
    /// Conflict winners (see Profile.ConflictWinners), loaded and saved with the profile: "file|row|field"
    /// → the winner's id, its Library folder name (FolderName/pak name for one pak of a prebuilt
    /// entry; OptionsWinnerId / ArcFissionWinnerId for those two). A winner still saved by display
    /// name (before ids) is moved to its id when the profile loads, where that's unambiguous.
    /// </summary>
    private Dictionary<string, string> _winners = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The winner id of the built-in gameplay options (also their name in the conflict window).</summary>
    internal const string OptionsWinnerId = "Built-in gameplay options";

    /// <summary>The winner id of ArcFission Library's data pak (its file name's number changes with every rebuild it does).</summary>
    internal const string ArcFissionWinnerId = "ArcFission Library data";

    /// <summary>Test-only: the saved winners as the profile will store them.</summary>
    internal IReadOnlyDictionary<string, string> WinnersForTesting => _winners;

    /// <summary>How many conflict winners the user has picked for this profile.</summary>
    public int SavedWinnerCount => _winners.Count;

    /// <summary>Test-only seam — see AssemblyInfo.cs's own InternalsVisibleTo grant. Lets a test set up a stored manual pick and then assert whether a given invalidation trigger correctly cleared it, without a real conflict-picker dialog flow.</summary>
    internal IReadOnlyDictionary<(string CurrentFile, string ItemName, string FieldName), int>? ManualPicksForTesting
    {
        get => _manualPicks;
        set => _manualPicks = value;
    }

    [ObservableProperty]
    private string? _conflictStatusMessage;

    /// <summary>Phase 10: a proactive conflict-count badge on the queue, recomputed in the background whenever Queue changes — Review conflicts still does the same computation synchronously for the authoritative picker flow; this is a best-effort preview, not load-bearing.</summary>
    [ObservableProperty]
    private int _conflictCount;

    public bool HasConflicts => ConflictCount > 0;

    partial void OnConflictCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasConflicts));
        RaiseCheckSummaryChanged();
    }

    /// <summary>The bar's counts and wording; ConflictCount is its NeedsDecision. Set only through ApplyConflicts, which the review window's own count comes from too.</summary>
    [ObservableProperty]
    private MergeConflictTally _conflictTally = MergeConflictTally.None;

    /// <summary>
    /// Keyed by LibraryEntry.Name (matching ConflictCandidate.ModName's own "display text, not a
    /// folder identifier" convention — see FieldConflict's doc comment) — lets a queue row's own
    /// DataTemplate show which OTHER queued mods it conflicts with, without needing to open Review
    /// conflicts first. Recomputed alongside ConflictCount everywhere that already runs
    /// FindQueueConflictsAsync, so it never drifts out of sync with the badge/banner. Only values
    /// needing a decision mark a row: same-name new items are combined with nothing to pick.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyDictionary<string, IReadOnlyList<string>> _conflictingModNamesByMod = new Dictionary<string, IReadOnlyList<string>>();

    private void InvalidateManualPicks()
    {
        _manualPicks = null;
        ConflictStatusMessage = null;
        _ = RecomputeConflictCountAsync();
    }

    /// <summary>One conflict check: what it found, and who took part, in merge order (Ids[i] is the winner id of the mod shown as Names[i]).</summary>
    internal sealed record QueueConflicts(
        IReadOnlyList<FieldConflict> Conflicts,
        IReadOnlyList<NewItemNameCollision> NewItemCollisions,
        IReadOnlyList<string> Names,
        IReadOnlyList<string> Ids)
    {
        public static QueueConflicts None { get; } = new([], [], [], []);

        public string? NameOf(string id) => Ids.ToList().IndexOf(id) is >= 0 and var i ? Names[i] : null;

        public string? IdOf(string name) => Names.ToList().IndexOf(name) is >= 0 and var i ? Ids[i] : null;
    }

    /// <summary>
    /// Per prebuilt pak (and ArcFission's data pak): its field changes as last extracted, keyed by
    /// path, with the file's size and write time — so the background conflict badge counts the same
    /// set the review window and the build do without unpacking every pak on every queue change.
    /// Cleared when the game data changes (a pak's changes are its diff against it).
    /// </summary>
    private readonly Dictionary<string, (long Length, DateTime WriteUtc, IReadOnlyList<FieldChange> Changes)> _prebuiltPakChangesCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Bumped with every clear of _prebuiltPakChangesCache for new game data (under _packageCacheLock).</summary>
    private int _prebuiltPakChangesGeneration;

    private async Task<IReadOnlyList<FieldChange>> GetPrebuiltPakChangesAsync(string pakPath, string unrealPakExePath)
    {
        var file = new FileInfo(pakPath);
        var stamp = (file.Exists ? file.Length : -1, file.Exists ? file.LastWriteTimeUtc : DateTime.MinValue);
        int generation;
        lock (_packageCacheLock)
        {
            if (_prebuiltPakChangesCache.TryGetValue(pakPath, out var cached) && (cached.Length, cached.WriteUtc) == stamp)
            {
                return cached.Changes;
            }

            generation = _prebuiltPakChangesGeneration;
        }

        var results = await _rebuildService.ComputePrebuiltPakFieldChangesAsync([pakPath], _dataFolder, unrealPakExePath, new MergeReport());
        IReadOnlyList<FieldChange> changes = results.Count > 0 ? results[0].Changes : [];
        lock (_packageCacheLock)
        {
            // Diffed against game data a Refresh game data has replaced since: not kept, or the
            // badge and the picks would count the old diff until the pak itself changed.
            if (generation == _prebuiltPakChangesGeneration)
            {
                _prebuiltPakChangesCache[pakPath] = (stamp.Item1, stamp.Item2, changes);
            }
        }

        return changes;
    }

    /// <summary>
    /// The one conflict check behind the badge, the review window, and the build's saved-winner
    /// picks, so all three count the same set: every queued EXMOD mod and prebuilt pak in true queue
    /// order (the order RebuildService merges in, see its prebuiltPakQueuePositions), ArcFission
    /// Library's data pak first (lowest priority) and the "Built-in gameplay options" synthetic entry
    /// last, so a candidate's index here is the index Rebuild uses. A pak's changes are its diff
    /// against the game data with its sure old copies left out, as Rebuild does
    /// (ComputePrebuiltPakFieldChangesAsync; cached per pak). Paks are left out only when UnrealPak
    /// isn't set up, which a build can't run without either. Display names are made unique (a
    /// " (folder)" suffix) so a pick by name always means one mod.
    ///
    /// Also runs MergeEngine.FindNewItemNameCollisions over the exact same (names, orderedModChanges,
    /// baseTablesByFile) — a genuinely different question from FieldConflict's own "do two mods
    /// disagree on one field's value" (see NewItemNameCollision's own doc comment), surfaced through
    /// the same picker rather than a parallel mechanism.
    /// </summary>
    private async Task<QueueConflicts> FindQueueConflictsAsync(BuildSnapshot snapshot, LoadedQueue loaded)
    {
        var classifier = new DefaultSemanticClassifier();
        var unrealPak = snapshot.UnrealPakExePath;
        var canUnpack = !string.IsNullOrWhiteSpace(unrealPak);
        var orderedModChanges = new List<IReadOnlyList<FieldChange>>();
        var names = new List<string>();
        var ids = new List<string>();

        var exmodChanges = await Task.Run(
            () => loaded.Packages.Select(p => ExmodFieldChangeMapper.ToFieldChanges(p.Package, classifier)).ToList());
        var arcFission = canUnpack ? snapshot.ArcFissionPakPath : null;
        // MergeParticipants' order, the same one RebuildService merges in.
        foreach (var participant in MergeParticipants.Build(
                     arcFission is null ? 0 : 1, loaded.Packages.Count, canUnpack ? loaded.PrebuiltPakFilePaths.Count : 0,
                     canUnpack ? loaded.PrebuiltPakQueuePositions : null))
        {
            switch (participant.Kind)
            {
                case MergeParticipantKind.LowPriorityPak:
                    var arcFissionChanges = await GetPrebuiltPakChangesAsync(arcFission!, unrealPak);
                    if (arcFissionChanges.Count > 0)
                    {
                        orderedModChanges.Add(arcFissionChanges);
                        names.Add(Path.GetFileNameWithoutExtension(arcFission!));
                        ids.Add(ArcFissionWinnerId);
                    }

                    break;
                case MergeParticipantKind.Mod:
                    orderedModChanges.Add(exmodChanges[participant.Index]);
                    names.Add(loaded.Entries[participant.Index].Name);
                    ids.Add(loaded.Entries[participant.Index].FolderName);
                    break;
                case MergeParticipantKind.Pak:
                    var pakPath = loaded.PrebuiltPakFilePaths[participant.Index];
                    var pakChanges = await GetPrebuiltPakChangesAsync(pakPath, unrealPak);
                    if (pakChanges.Count > 0)
                    {
                        var pakName = Path.GetFileNameWithoutExtension(pakPath);
                        orderedModChanges.Add(pakChanges);
                        names.Add(pakName);
                        ids.Add($"{loaded.PrebuiltPakEntries[participant.Index].FolderName}/{pakName}");
                    }

                    break;
                case MergeParticipantKind.Options:
                    var fixedOptionChanges = GameplayOptionsFieldChangeGenerator.GenerateFixedFieldChanges(snapshot.Options, _dataFolder, new MergeReport());
                    if (fixedOptionChanges.Count > 0)
                    {
                        orderedModChanges.Add(fixedOptionChanges);
                        names.Add(OptionsWinnerId);
                        ids.Add(OptionsWinnerId);
                    }

                    break;
            }
        }

        // Two mods with the same display name would make a pick by name ambiguous.
        var duplicateNames = names.GroupBy(n => n, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < names.Count; i++)
        {
            if (duplicateNames.Contains(names[i]))
            {
                names[i] = $"{names[i]} ({ids[i]})";
            }
        }

        var (conflicts, newItemCollisions) = await Task.Run(() =>
        {
            // Base-aware filtering (see MergeEngine.Merge's own doc comment) needs real current
            // base values for whatever files this preview's own candidates touch — genuinely
            // best-effort: a report here is thrown away, not surfaced, and any read failure (a
            // locked/permission-denied base file — ReadBaseTables' own File.ReadAllText has no
            // catch for that, unlike a merely-missing file) falls back to no filtering rather than
            // blocking the conflict badge/picker entirely. The same baseTablesByFile is what
            // lets FindNewItemNameCollisions tell a genuinely new item apart from an existing one
            // every real EXMOD-sourced FieldChange otherwise always marks IsNewItem: true (see its
            // own doc comment) — without it, this would fall back to that unreliable raw flag.
            IReadOnlyDictionary<string, JsonObject>? baseTablesByFile = null;
            try
            {
                var requiredFiles = orderedModChanges.SelectMany(c => c).Select(c => c.CurrentFile).Distinct();
                baseTablesByFile = _rebuildService.ReadKeyedBaseTables(requiredFiles, _dataFolder, new MergeReport());
            }
            catch (Exception)
            {
                // Best-effort — see the comment above.
            }

            return (
                MergeEngine.FindConflicts(names, orderedModChanges, baseTablesByFile),
                MergeEngine.FindNewItemNameCollisions(names, orderedModChanges, baseTablesByFile));
        });
        return new QueueConflicts(conflicts, newItemCollisions, names, ids);
    }

    /// <summary>Guards RecomputeConflictCountAsync against two overlapping runs (the queue/options changing again while a previous background recompute is still in flight) finishing out of order — same "only the newest request's result is applied" shape as NexusCatalogViewModel's own _loadVersion.</summary>
    private int _conflictCountVersion;

    /// <summary>Best-effort background refresh of ConflictCount — a malformed queued mod here just leaves the badge at its last value; ReviewConflictsAsync/Rebuild surface the real error when the user actually acts. Internal (not private) purely so a test can invoke the exact same recompute the debounce timer would have, on demand — see IsRecomputeDebouncePending's own doc comment.</summary>
    internal async Task RecomputeConflictCountAsync()
    {
        var version = ++_conflictCountVersion;

        var snapshot = TakeSnapshot();
        if (snapshot.Queue.Count == 0)
        {
            ApplyConflicts(QueueConflicts.None);
            return;
        }

        try
        {
            var conflicts = await FindQueueConflictsAsync(snapshot, await LoadQueuedPackagesAsync(snapshot.Queue));
            if (version == _conflictCountVersion)
            {
                ApplyConflicts(conflicts);
            }
        }
        catch (Exception)
        {
            // Left at its last value on purpose (see the doc comment above): zeroing it here read as
            // "the conflict is gone" the moment a queued mod's file was briefly locked mid-save.
        }
    }

    private QueueConflicts _lastConflicts = QueueConflicts.None;

    /// <summary>The bar, the badge and the ⚠ rows from one conflict check — counted by MergeConflictTally, the same as the review window.</summary>
    private void ApplyConflicts(QueueConflicts found)
    {
        _lastConflicts = found;
        ConflictTally = MergeConflictTally.From(found.Conflicts, found.NewItemCollisions);
        ConflictCount = ConflictTally.NeedsDecision;
        ConflictingModNamesByMod = MergeEngine.GroupConflictsByMod(found.Conflicts);
    }

    /// <summary>Test-only: the review window Review conflicts would open for the last conflict check.</summary>
    internal MergeConflictsViewModel CreateConflictReviewForTesting() =>
        new(_lastConflicts.Conflicts, _lastConflicts.NewItemCollisions, ToDisplayWinners(_winners, _lastConflicts));

    /// <summary>
    /// The saved winners (by id) as the review window shows them (by the name in this check). A
    /// winner still saved by display name (ambiguous at migration) works while one mod here has it.
    /// Winners for mods not in this check are left out (kept aside by FromDisplayWinners).
    /// </summary>
    private static Dictionary<string, string> ToDisplayWinners(IReadOnlyDictionary<string, string> winners, QueueConflicts found)
    {
        var display = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, winner) in winners)
        {
            if (found.NameOf(winner) is { } name)
            {
                display[key] = name;
            }
            else if (found.IdOf(winner) is not null)
            {
                display[key] = winner;
            }
        }

        return display;
    }

    /// <summary>
    /// The review window's winners (by name) back to ids, plus the saved winners it wasn't shown
    /// (a mod out of the queue for a while) for values not in conflict right now. A value in
    /// conflict now was decided in the window, so an unshown old pick for it goes.
    /// </summary>
    private static Dictionary<string, string> FromDisplayWinners(
        IReadOnlyDictionary<string, string> result, IReadOnlyDictionary<string, string> previous,
        IReadOnlyDictionary<string, string> shown, QueueConflicts found)
    {
        var inConflictNow = found.Conflicts
            .Select(c => MergeConflictsViewModel.WinnerKey(c.CurrentFile, c.ItemName, c.FieldName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var winners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, winner) in previous)
        {
            if (!shown.ContainsKey(key) && !inConflictNow.Contains(key))
            {
                winners[key] = winner;
            }
        }

        foreach (var (key, name) in result)
        {
            winners[key] = found.IdOf(name) ?? name;
        }

        return winners;
    }

    /// <summary>Keeps the winners with the selected profile at once (only the winners: its queue and options stay as saved).</summary>
    private void SaveWinnersToProfile()
    {
        if (SelectedProfileName is not { } name)
        {
            // No profile yet: the auto-save creates "Default" with the queue, options and winners.
            MarkWorkingStateChanged();
            return;
        }

        try
        {
            var profile = _profileStore.Load(name);
            profile.ConflictWinners = new(_winners);
            _profileStore.Save(profile);
            RefreshCanRestoreProfileBackup();
        }
        catch (Exception ex)
        {
            ConflictStatusMessage = $"Couldn't save the picks with '{name}': {ex.Message}";
        }
    }

    /// <summary>
    /// The snapshot's winners turned into the merge's per-field picks for its queue (a pick's
    /// position among the mods changing that value, counted the way Rebuild merges: see
    /// FindQueueConflictsAsync), plus how many saved winners for mods in this build no longer match
    /// a conflict (the value isn't changed differently any more, or that mod doesn't change it). A
    /// test's ManualPicksForTesting is used when nothing was picked.
    /// </summary>
    private async Task<(IReadOnlyDictionary<(string CurrentFile, string ItemName, string FieldName), int>? Picks, int Unmatched)> ResolveWinnerPicksAsync(
        BuildSnapshot snapshot, LoadedQueue loaded)
    {
        if (snapshot.Winners.Count == 0)
        {
            return (snapshot.ManualPicks, 0);
        }

        var found = await FindQueueConflictsAsync(snapshot, loaded);
        var picks = new Dictionary<(string CurrentFile, string ItemName, string FieldName), int>(FieldChangeKeyComparer.Instance);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var conflict in found.Conflicts)
        {
            var key = MergeConflictsViewModel.WinnerKey(conflict.CurrentFile, conflict.ItemName, conflict.FieldName);
            if (snapshot.Winners.TryGetValue(key, out var winner)
                && (found.NameOf(winner) ?? (found.IdOf(winner) is not null ? winner : null)) is { } name)
            {
                var index = conflict.Candidates.ToList().FindIndex(c => string.Equals(c.ModName, name, StringComparison.Ordinal));
                if (index >= 0)
                {
                    picks[(conflict.CurrentFile, conflict.ItemName, conflict.FieldName)] = index;
                    used.Add(key);
                }
            }
        }

        var unmatched = snapshot.Winners.Count(w => !used.Contains(w.Key) && (found.NameOf(w.Value) is not null || found.IdOf(w.Value) is not null));
        return (picks.Count > 0 ? picks : null, unmatched);
    }

    /// <summary>
    /// The page's one Review: checks the queue afresh and opens the check window, with conflicts
    /// (pick a winner per value, or leave it on Default: usually the mod lower in the queue, but a
    /// combine rule unions every candidate's value for some field shapes; see
    /// MergeConflictViewModel), what clashes between mods, and which queued mods have problems
    /// (Fix… opens that mod's fix window in the Library). Winners are saved with the profile by mod
    /// (folder) id and used by every later build.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task ReviewMergeAsync()
    {
        var snapshot = TakeSnapshot();
        if (snapshot.Queue.Count == 0)
        {
            ConflictStatusMessage = "Add mods to the queue first.";
            return;
        }

        try
        {
            var found = await FindQueueConflictsAsync(snapshot, await LoadQueuedPackagesAsync(snapshot.Queue));
            var (conflicts, newItemCollisions, modNames) = (found.Conflicts, found.NewItemCollisions, found.Names);
            await RecomputeChecksAsync();
            // The window and the bar count from the same tally (the window's own), so they agree.
            var shownWinners = ToDisplayWinners(snapshot.Winners, found);
            var reviewViewModel = new MergeConflictsViewModel(conflicts, newItemCollisions, shownWinners, BetweenModsFindings, ModsWithProblems);
            ApplyConflicts(found);

            if (conflicts.Count == 0 && newItemCollisions.Count == 0 && BetweenModsFindings.Count == 0 && ModsWithProblems.Count == 0)
            {
                _manualPicks = null;
                ConflictStatusMessage = $"Nothing to look at among {modNames.Count} mod(s): no conflicts, nothing clashing between mods, and no problems in the queued mods.";
                return;
            }

            var collisionNote = newItemCollisions.Count > 0 ? $" ({newItemCollisions.Count} same-name new item(s) combined automatically)" : "";
            if (conflicts.Count > 0)
            {
                _activityLog.Log($"{conflicts.Count} conflict(s){collisionNote} found among {modNames.Count} queued mod(s).", ActivityEntryKind.Warning);
            }

            var window = new MergeConflictsWindow(reviewViewModel) { Owner = Application.Current.MainWindow };
            if (window.ShowDialog() == true)
            {
                _winners = FromDisplayWinners(window.ResultWinners!, snapshot.Winners, shownWinners, found);
                OnPropertyChanged(nameof(SavedWinnerCount));
                SaveWinnersToProfile();
                var pickedNow = reviewViewModel.PickedCount;
                ConflictStatusMessage = conflicts.Count == 0 ? null
                    : pickedNow > 0
                        ? $"{conflicts.Count} conflict(s){collisionNote}, {pickedNow} won by your pick (saved with the profile). Merge & Install to apply."
                        // "Default" per line: the mod lower in the queue wins, or a list takes everyone's additions.
                        : $"{conflicts.Count} conflict(s){collisionNote}, all left on default.";

                // Fix… on a mod: the Library's own fix window for it.
                if (reviewViewModel.FixFolderName is { } folder)
                {
                    _messenger.Send(new OpenModFixMessage(folder));
                }
            }
        }
        catch (Exception ex)
        {
            // Same UI boundary as Rebuild itself — a malformed queued mod shouldn't crash the app.
            ConflictStatusMessage = $"Couldn't check the merge: {ex.Message}";
        }
    }
}
