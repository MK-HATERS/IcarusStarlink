using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.Services;
using IcarusStarlink.Core.Library;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.ViewModels;

/// <summary>Something wrong between queued mods rather than in one of them: the check window's Between mods tab.</summary>
/// <param name="Code">A short fixed code (ISL-2xx; ModIssue.Code is ISL-1xx), in the copied report.</param>
public sealed record BetweenModsFinding(string Kind, string Mods, string Where, string Why, string Code)
{
    public const string SameFileCode = "ISL-201";
    public const string TalentTreeCode = "ISL-202";
    public const string NeedsAModCode = "ISL-203";
}

/// <summary>A queued mod the Library's health check flags: the check window's Per mod tab, where Fix… opens its fix window.</summary>
/// <param name="Problems">Every problem (not tidying), for the copied report.</param>
public sealed record QueuedModProblems(string FolderName, string Name, int ProblemCount, int SureCount, string Kinds, IReadOnlyList<ModIssue> Problems)
{
    public string CountText => ProblemCount == 1 ? "1 problem" : $"{ProblemCount} problems";

    public string SureText => SureCount == 0 ? "each needs a look"
        : SureCount == ProblemCount ? "all with a sure fix"
        : $"{SureCount} with a sure fix";
}

/// <summary>
/// The merge's check besides conflicts, behind the page's one bar and the one Review window:
/// what clashes between queued mods (the same file in two mods, talent trees, a link to a row only
/// a mod outside this merge adds), and which queued mods the Library's own health check flags
/// (the same check as the Library's badge, and the results are sent to the Library's rows, so
/// their badges agree; each mod's problems are fixed in the Library's fix window). The Library's
/// Problems chip also counts mods replacing high-risk game files, which this doesn't. Nothing
/// here stops a merge.
/// </summary>
public sealed partial class MergeInstallViewModel
{
    /// <summary>The Library's health check (null in tests that don't need it: no Per mod findings).</summary>
    private readonly ModFixService? _fixService;

    /// <summary>Each queued mod's health report (by folder) and the rows Library mods add, kept until the Library or the game data changes (under _packageCacheLock).</summary>
    private readonly Dictionary<string, ModHealthReport> _healthCache = new(StringComparer.OrdinalIgnoreCase);

    private LibraryAddedRowSet? _libraryAddedRows;

    /// <summary>
    /// Bumped by every InvalidateCheckCache (under _packageCacheLock). A check that started before
    /// the Library changed must not put what it read back into the cache after it was cleared, or
    /// the old rows and reports would stay until the next change.
    /// </summary>
    private int _checkCacheGeneration;

    [ObservableProperty]
    private IReadOnlyList<BetweenModsFinding> _betweenModsFindings = [];

    [ObservableProperty]
    private IReadOnlyList<QueuedModProblems> _modsWithProblems = [];

    partial void OnBetweenModsFindingsChanged(IReadOnlyList<BetweenModsFinding> value) => RaiseCheckSummaryChanged();

    partial void OnModsWithProblemsChanged(IReadOnlyList<QueuedModProblems> value) => RaiseCheckSummaryChanged();

    /// <summary>Also raised when ConflictCount changes (OnConflictCountChanged).</summary>
    private void RaiseCheckSummaryChanged()
    {
        OnPropertyChanged(nameof(HasCheckFindings));
        OnPropertyChanged(nameof(CheckSummary));
    }

    /// <summary>The bar shows when there's anything to look at.</summary>
    public bool HasCheckFindings => ConflictCount > 0 || BetweenModsFindings.Count > 0 || ModsWithProblems.Count > 0;

    /// <summary>The bar's line: "3 conflicts · 1 clash between mods · 2 mods with problems".</summary>
    public string CheckSummary
    {
        get
        {
            var parts = new List<string>();
            if (ConflictCount > 0)
            {
                parts.Add(ConflictCount == 1 ? "1 conflict" : $"{ConflictCount} conflicts");
            }

            if (BetweenModsFindings.Count > 0)
            {
                parts.Add(BetweenModsFindings.Count == 1 ? "1 clash between mods" : $"{BetweenModsFindings.Count} clashes between mods");
            }

            if (ModsWithProblems.Count > 0)
            {
                parts.Add(ModsWithProblems.Count == 1 ? "1 mod with problems" : $"{ModsWithProblems.Count} mods with problems");
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>A mod's fixes, an edit or new game data change what's flagged: checked afresh next time.</summary>
    private void InvalidateCheckCache()
    {
        lock (_packageCacheLock)
        {
            _healthCache.Clear();
            _libraryAddedRows = null;
            _checkCacheGeneration++;
        }
    }

    /// <summary>Guards RecomputeChecksAsync like _conflictCountVersion guards its sibling.</summary>
    private int _checkVersion;

    /// <summary>Best-effort background refresh of the Between mods and Per mod findings; a failure leaves the last ones (Review checks again). Internal for tests, like RecomputeConflictCountAsync.</summary>
    internal async Task RecomputeChecksAsync()
    {
        var version = ++_checkVersion;
        if (Queue.Count == 0)
        {
            BetweenModsFindings = [];
            ModsWithProblems = [];
            return;
        }

        try
        {
            var (between, perMod, reports) = await FindQueueChecksAsync();
            if (version == _checkVersion)
            {
                BetweenModsFindings = between;
                ModsWithProblems = perMod;
                // The Library's badges and Problems count show the same results.
                if (reports.Count > 0)
                {
                    _messenger.Send(new ModHealthCheckedMessage(reports));
                }
            }
        }
        catch (Exception)
        {
            // Left at the last findings on purpose, as for conflicts.
        }
    }

    /// <summary>
    /// Only the real EXMOD mods in the queue: a prebuilt pak's tables were written by the tool that
    /// built it (its old copies are the health check's once it's made editable), and the built-in
    /// gameplay options write fields Starlink knows.
    /// </summary>
    private async Task<(IReadOnlyList<BetweenModsFinding> Between, IReadOnlyList<QueuedModProblems> PerMod, IReadOnlyDictionary<string, ModHealthReport> Reports)> FindQueueChecksAsync()
    {
        var loaded = await LoadQueuedPackagesAsync();
        var (entries, packages) = (loaded.Entries, loaded.Packages);

        return await Task.Run(() =>
        {
            int generation;
            lock (_packageCacheLock)
            {
                generation = _checkCacheGeneration;
            }

            var gameIndex = GetOrCreateGameTableIndex();
            var queued = entries.Zip(packages, (entry, package) => (entry.Name, package)).ToList();
            var between = new List<BetweenModsFinding>();

            // Last in queue order wins a file both mods bundle, as for field conflicts.
            foreach (var collision in ExmodAssetCollisionChecker.Check(queued))
            {
                if (!collision.AreIdentical)
                {
                    between.Add(new BetweenModsFinding("Same file", string.Join(", ", collision.ModNames), collision.RelativePath,
                        $"These mods each bring their own version of this file; only '{collision.ModNames[^1]}' (lowest in the queue) is used.", BetweenModsFinding.SameFileCode));
                }
            }

            // The talent, blueprint and workshop trees as the whole queue leaves them.
            foreach (var finding in TalentTreeChecker.Check([.. queued.Select(q => (q.Name, q.package.Package))], gameIndex))
            {
                between.Add(new BetweenModsFinding("Talent tree", string.Join(", ", finding.Mods), $"D_Talents · {finding.Node}", finding.Reason, BetweenModsFinding.TalentTreeCode));
            }

            var libraryRows = LibraryAddedRowsForCheck(generation);
            if (libraryRows is not null)
            {
                between.AddRange(NeedsModsOutsideTheMerge(entries, packages.Select(p => p.Package).ToList(), gameIndex, libraryRows));
            }

            var perMod = new List<QueuedModProblems>();
            var reports = new Dictionary<string, ModHealthReport>(StringComparer.OrdinalIgnoreCase);
            if (_fixService is not null && libraryRows is not null && gameIndex.HasAnyTable())
            {
                var history = _fixService.History();
                var baseTables = new Dictionary<string, JsonObject?>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in entries)
                {
                    if (HealthFor(entry, baseTables, history, libraryRows, generation) is not { } report)
                    {
                        continue;
                    }

                    reports[entry.FolderName] = report;
                    if (report.ProblemCount > 0)
                    {
                        var problems = report.Issues.Where(i => !i.IsTidyOnly).ToList();
                        var kinds = string.Join(", ", problems.GroupBy(i => i.Kind).OrderByDescending(g => g.Count())
                            .Select(g => $"{ModFixViewModel.KindTitle(g.Key)} ({g.Count()})"));
                        perMod.Add(new QueuedModProblems(entry.FolderName, entry.Name, problems.Count, problems.Count(i => i.IsAutomatic), kinds, problems));
                    }
                }
            }

            return ((IReadOnlyList<BetweenModsFinding>)between, (IReadOnlyList<QueuedModProblems>)perMod, (IReadOnlyDictionary<string, ModHealthReport>)reports);
        });
    }

    /// <summary>
    /// Links in a queued mod to rows no queued mod adds but another Library mod does: an add-on
    /// queued without the mod it's for. One finding per pair of mods (an add-on usually links to
    /// many rows of the same mod).
    /// </summary>
    private static IEnumerable<BetweenModsFinding> NeedsModsOutsideTheMerge(
        IReadOnlyList<LibraryEntry> entries, IReadOnlyList<ExmodPackage> packages, GameTableIndex gameIndex, LibraryAddedRowSet libraryRows)
    {
        var queueRows = new HashSet<string>(StringComparer.Ordinal);
        foreach (var package in packages)
        {
            queueRows.UnionWith(ModHealthChecker.AddedRows(package, gameIndex));
        }

        for (var i = 0; i < packages.Count; i++)
        {
            var needed = ModHealthChecker.LinksMissingFrom(packages[i], gameIndex, queueRows)
                .Where(l => libraryRows.AddedBy.ContainsKey(l.Key))
                .GroupBy(l => libraryRows.AddedBy[l.Key]);
            foreach (var provider in needed)
            {
                var rows = provider.Select(l => l.Row).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var first = provider.First();
                yield return new BetweenModsFinding("Needs a mod", entries[i].Name, $"{first.Table} · {first.Item}",
                    $"Links to {(rows.Count == 1 ? $"'{rows[0]}'" : $"{rows.Count} rows (e.g. '{rows[0]}')")}, which only '{provider.Key}' adds, "
                    + $"and '{provider.Key}' isn't in this merge. Add it to the queue, or {(rows.Count == 1 ? "this link does" : "these links do")} nothing in game.",
                    BetweenModsFinding.NeedsAModCode);
            }
        }
    }

    /// <summary>The rows every Library mod adds, read once until the Library changes (null without the health check). Kept only when the cache wasn't cleared meanwhile (generation).</summary>
    private LibraryAddedRowSet? LibraryAddedRowsForCheck(int generation)
    {
        if (_fixService is null)
        {
            return null;
        }

        lock (_packageCacheLock)
        {
            if (_libraryAddedRows is { } cached)
            {
                return cached;
            }
        }

        var rows = _fixService.LibraryAddedRows() as LibraryAddedRowSet;
        lock (_packageCacheLock)
        {
            return generation == _checkCacheGeneration ? _libraryAddedRows ??= rows : rows;
        }
    }

    /// <summary>A queued mod's health report, the Library's check (cached until the Library or the game data changes); null when it can't be read.</summary>
    private ModHealthReport? HealthFor(LibraryEntry entry, IDictionary<string, JsonObject?> baseTables, GameValueHistory history, LibraryAddedRowSet libraryRows, int generation)
    {
        lock (_packageCacheLock)
        {
            if (_healthCache.TryGetValue(entry.FolderName, out var cached))
            {
                return cached;
            }
        }

        try
        {
            var report = _fixService!.Check(entry.FolderName, baseTables, history, libraryRows);
            lock (_packageCacheLock)
            {
                if (generation == _checkCacheGeneration)
                {
                    _healthCache[entry.FolderName] = report;
                }
            }

            return report;
        }
        catch (Exception)
        {
            // A mod that can't be read is the Library's to report.
            return null;
        }
    }
}
