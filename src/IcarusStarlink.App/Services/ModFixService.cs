using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Diffing;
using IcarusStarlink.PakIO.Assets;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.Services;

/// <summary>
/// Checks a Library mod against the current game data (ModHealthChecker) and writes chosen fixes
/// back, always backed up first (Restore latest backup undoes it). Remembers each mod's choices
/// (ModFixDecisions, one small file per mod) so what the user decided once is done by itself the
/// next time. A mod converted from a prebuilt pak isn't marked edited by a fix: it keeps being
/// re-converted against each new game version, and exactly the fixes made to it before (each one
/// remembered) are made again right after (ReapplyRemembered). Plain file work, safe off the UI thread.
///
/// With the game's content (gameAssets), the mod's cooked files are checked too: every game file
/// they use must still be where they look for it (AssetReferenceChecker), and one the game moved
/// can be pointed at its new place (AssetReferenceRewriter).
///
/// One call at a time: the fix window, Fix sure problems, the whole-library check and the
/// post-refresh ReapplyRemembered can all reach this one service from different threads. Every
/// public call takes the same lock, so the lazily-filled table index is never filled by two
/// threads at once, and two writers never back up and rewrite the same .EXMOD together.
/// </summary>
public sealed class ModFixService(
    ILibraryRepository repository, string dataFolder, Func<IEnumerable<FieldChange>>? gameHistory = null, string? decisionsDirectory = null,
    IGameAssetCatalog? gameAssets = null)
    : IcarusStarlink.Core.Memory.IIdleReleasable
{
    private static readonly JsonSerializerOptions DecisionJson = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    /// <summary>Held by every public call. Re-entrant (FixSure calls Check on the same thread).</summary>
    private readonly Lock _gate = new();

    public string DataFolder => dataFolder;

    private GameTableIndex? _gameIndex;
    private DateTime _gameIndexStamp;

    private GameRowHistory? _rowHistory;
    private DateTime _rowHistoryStamp;

    /// <summary>When a check last used the game indexes (under _gate).</summary>
    private DateTime _lastUsedUtc;

    /// <summary>Registers <paramref name="service"/> so its indexes are let go while the app sits unused (a primary constructor can't do it itself).</summary>
    public static ModFixService Registered(ModFixService service)
    {
        IcarusStarlink.Core.Memory.IdleMemory.Register(service);
        return service;
    }

    /// <summary>The game's table index and row history are let go while the app sits unused; the next check reads them again.</summary>
    public bool ReleaseIfIdle(TimeSpan idleFor)
    {
        lock (_gate)
        {
            if ((_gameIndex is null && _rowHistory is null) || DateTime.UtcNow - _lastUsedUtc < idleFor)
            {
                return false;
            }

            _gameIndex = null;
            _rowHistory = null;
            return true;
        }
    }

    /// <summary>Every row the game has ever had: shipped, plus the user's own refreshes (re-read when a refresh adds to it). Only read under _gate.</summary>
    private GameRowHistory RowHistory
    {
        get
        {
            var local = GameRowHistory.LocalFileFor(dataFolder);
            var stamp = File.Exists(local) ? File.GetLastWriteTimeUtc(local) : default;
            _lastUsedUtc = DateTime.UtcNow;
            if (_rowHistory is null || stamp != _rowHistoryStamp)
            {
                _rowHistory = GameRowHistory.Load(local);
                _rowHistoryStamp = stamp;
            }

            return _rowHistory;
        }
    }

    /// <summary>One table index for every check (re-read after Refresh game data changes the folder). Only read under _gate.</summary>
    private GameTableIndex GameIndex
    {
        get
        {
            var stamp = Directory.Exists(dataFolder) ? Directory.GetLastWriteTimeUtc(dataFolder) : default;
            _lastUsedUtc = DateTime.UtcNow;
            if (_gameIndex is null || stamp != _gameIndexStamp)
            {
                _gameIndex = new GameTableIndex(dataFolder);
                _gameIndexStamp = stamp;
            }

            return _gameIndex;
        }
    }

    /// <param name="libraryAddedRows">The rows every Library mod adds (LibraryAddedRows: share one across a pass), so a link to another mod's new row isn't called broken.</param>
    public ModHealthReport Check(string folderName, IDictionary<string, JsonObject?>? baseTableCache = null, GameValueHistory? history = null,
        IReadOnlySet<string>? libraryAddedRows = null)
    {
        lock (_gate)
        {
            var package = ExmodFolder.ReadPackageOnly(repository.GetFolderPath(folderName));
            var assetPaths = repository.ListAssetPaths(folderName);
            libraryAddedRows ??= LibraryAddedRows();
            return ModHealthChecker.Check(package, dataFolder, baseTableCache, history ?? History(), assetPaths,
                decisions: LoadDecisions(folderName), fromPrebuiltPak: Entry(folderName)?.ConvertedFromPrebuiltPak == true, gameIndex: GameIndex,
                rowsOtherModsAdd: libraryAddedRows,
                rowsSeveralModsAdd: (libraryAddedRows as LibraryAddedRowSet)?.AddedBySeveral,
                rowHistory: RowHistory,
                assetIssues: AssetIssues(folderName, assetPaths, (libraryAddedRows as LibraryAddedRowSet)?.Packages));
        }
    }

    /// <summary>What the mod's cooked files use that the game doesn't have (nothing when the game's content can't be read). Under _gate.</summary>
    private IReadOnlyList<ModIssue> AssetIssues(string folderName, IReadOnlyList<string> assetPaths, IReadOnlySet<string>? libraryPackages)
    {
        if (gameAssets is null || !assetPaths.Any(p => p.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".umap", StringComparison.OrdinalIgnoreCase)))
        {
            return [];
        }

        try
        {
            var found = AssetReferenceChecker.Check(assetPaths, file =>
            {
                try
                {
                    return repository.ReadAssetContent(folderName, file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return null;
                }
            }, gameAssets, libraryPackages ?? LibraryPackages());
            return found is null ? [] : AssetReferenceIssues.From(found);
        }
        catch (Exception ex)
        {
            // Only an extra check: the rest of the report stands without it.
            Serilog.Log.Warning(ex, "Couldn't check the game files '{Mod}' uses", folderName);
            return [];
        }
    }

    /// <summary>The game's recorded old values from recent Refresh game data runs, indexed once (share it across a whole-library pass).</summary>
    public GameValueHistory History()
    {
        try
        {
            return gameHistory is null ? GameValueHistory.Empty : new GameValueHistory(gameHistory());
        }
        catch (Exception)
        {
            // Only a hint: without it the other rules still apply.
            return GameValueHistory.Empty;
        }
    }

    /// <summary>
    /// Remembers every choice (what was kept isn't asked again; every fix made is made again after
    /// a re-conversion, exactly as chosen), then backs the mod up, applies the fixes and writes it.
    /// Returns how many fixes were applied.
    /// </summary>
    public int Apply(string folderName, IReadOnlyCollection<(ModIssue Issue, ModIssueFix Fix)> fixes)
    {
        lock (_gate)
        {
            var decisions = LoadDecisions(folderName);
            foreach (var (issue, fix) in fixes)
            {
                decisions.Remember(issue, fix);
            }

            if (fixes.Count > 0)
            {
                SaveDecisions(folderName, decisions);
            }

            return Write(folderName, fixes, backUp: true);
        }
    }

    /// <summary>
    /// The automatic part for one mod: every sure problem fixed, and every choice made before made
    /// again; tidying alone (values the game already has, old rows nothing uses) never is. Each fix
    /// made is remembered as a choice, so a re-conversion makes exactly these again
    /// (ReapplyRemembered). Returns (applied, still to look over).
    /// </summary>
    public (int Applied, int ToReview) FixSure(string folderName, IDictionary<string, JsonObject?>? baseTableCache = null, GameValueHistory? history = null,
        IReadOnlySet<string>? libraryAddedRows = null) =>
        Fix(folderName, baseTableCache, history, libraryAddedRows, onlyRemembered: false);

    /// <param name="onlyRemembered">Only the choices remembered for this mod (ReapplyRemembered), not every sure problem.</param>
    private (int Applied, int ToReview) Fix(string folderName, IDictionary<string, JsonObject?>? baseTableCache, GameValueHistory? history,
        IReadOnlySet<string>? libraryAddedRows, bool onlyRemembered)
    {
        lock (_gate)
        {
            libraryAddedRows ??= LibraryAddedRows();
            // A fix can reveal another (a renamed row whose value turns out to be the game's): a few
            // rounds until nothing sure is left. Backed up once, before the first round writes, so
            // Restore latest backup undoes the whole click rather than only its last round.
            var applied = 0;
            var report = Check(folderName, baseTableCache, history, libraryAddedRows);
            for (var round = 0; round < 3; round++)
            {
                var fixes = report.Issues.Where(i => i.IsAutomatic && (!onlyRemembered || i.Remembered)).Select(i => (i, i.DefaultFix)).ToList();
                if (fixes.Count == 0)
                {
                    break;
                }

                if (fixes.Any(f => !f.i.Remembered))
                {
                    var decisions = LoadDecisions(folderName);
                    foreach (var (issue, fix) in fixes.Where(f => !f.i.Remembered))
                    {
                        decisions.Remember(issue, fix);
                    }

                    SaveDecisions(folderName, decisions);
                }

                var thisRound = Write(folderName, fixes, backUp: applied == 0);
                if (thisRound == 0)
                {
                    break;
                }

                applied += thisRound;
                report = Check(folderName, baseTableCache, history, libraryAddedRows);
            }

            return (applied, report.ReviewCount);
        }
    }

    /// <summary>
    /// After a prebuilt pak was converted again: make again exactly the fixes made to it before
    /// (in the fix window or by Fix sure problems, each remembered as a choice), nothing more. A mod
    /// marked fixed by an earlier version, which didn't remember the fixes Fix sure problems made,
    /// gets its sure problems fixed once more (and remembered from then on). Without this a broken
    /// link fixed with Fix sure problems came back on the next refresh.
    /// </summary>
    public int ReapplyRemembered(string folderName, IDictionary<string, JsonObject?>? baseTableCache = null, GameValueHistory? history = null,
        IReadOnlySet<string>? libraryAddedRows = null)
    {
        lock (_gate)
        {
            if (!LoadDecisions(folderName).HasFixes)
            {
                return Entry(folderName)?.FixedByIslAtUtc is not null ? FixSure(folderName, baseTableCache, history, libraryAddedRows).Applied : 0;
            }

            return Fix(folderName, baseTableCache, history, libraryAddedRows, onlyRemembered: true).Applied;
        }
    }

    public int RememberedCount(string folderName)
    {
        lock (_gate)
        {
            return LoadDecisions(folderName).Count;
        }
    }

    public bool HasBackup(string folderName) => repository.HasModBackup(folderName);

    /// <summary>Puts the mod back as it was before its last change (each fix, rename or refresh backs it up first).</summary>
    public bool RestoreBackup(string folderName)
    {
        lock (_gate)
        {
            return repository.RestoreLatestModBackup(folderName);
        }
    }

    /// <summary>Forgets this mod's remembered choices: everything is suggested afresh next check.</summary>
    public void Forget(string folderName)
    {
        lock (_gate)
        {
            if (DecisionsPath(folderName) is { } path && File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>A replaced mod that landed under a different folder name keeps its remembered choices (they're stored by folder name).</summary>
    public void CarryOver(string fromFolderName, string toFolderName)
    {
        lock (_gate)
        {
            if (string.Equals(fromFolderName, toFolderName, StringComparison.OrdinalIgnoreCase)
                || DecisionsPath(fromFolderName) is not { } from || DecisionsPath(toFolderName) is not { } to || !File.Exists(from))
            {
                return;
            }

            File.Copy(from, to, overwrite: true);
        }
    }

    /// <param name="backUp">Back the mod up before writing (false for FixSure's later rounds: its first round already did).</param>
    private int Write(string folderName, IReadOnlyCollection<(ModIssue Issue, ModIssueFix Fix)> fixes, bool backUp)
    {
        var changing = fixes.Where(f => f.Fix != ModIssueFix.KeepMod).ToList();
        if (changing.Count == 0)
        {
            return 0;
        }

        var folderPath = repository.GetFolderPath(folderName);
        var package = ExmodFolder.ReadPackageOnly(folderPath);
        var applied = ModHealthChecker.Apply(package, changing.Where(f => f.Fix != ModIssueFix.Repoint));
        var repointed = Repointed(folderName, folderPath, changing.Where(f => f.Fix == ModIssueFix.Repoint).Select(f => f.Issue));
        // Advanced log: each change made to the mod.
        foreach (var (issue, fix) in changing)
        {
            Serilog.Log.Debug("Fix '{Mod}': {Kind} {File}/{Row}.{Field} -> {Fix}", folderName, issue.Kind, issue.CurrentFile, issue.ItemName, issue.FieldName ?? "(row)", fix);
        }
        if (applied == 0 && repointed.Count == 0)
        {
            return 0;
        }

        if (backUp)
        {
            repository.BackupMod(folderName);
        }

        if (applied > 0)
        {
            ExmodFolder.Write(folderPath, new ExmodPackageContents(package, []));
        }

        foreach (var (rewritten, _) in repointed)
        {
            AssetReferenceRewriter.Commit(rewritten);
        }

        applied += repointed.Sum(r => r.Fixes);
        repository.MarkFixedByIsl(folderName);

        // A converted pak that hasn't been hand-edited stays "live": it keeps re-converting against
        // new game data, with these fixes made again each time. Anything else shows the ✎.
        if (Entry(folderName) is not { ConvertedFromPrebuiltPak: true, IsLocallyEdited: false })
        {
            repository.MarkLocallyEdited(folderName);
        }

        return applied;
    }

    /// <summary>
    /// Each of the mod's cooked files with the chosen game files pointed at their new place,
    /// checked and ready to write (each with how many fixes it makes). A file AssetReferenceRewriter
    /// refuses stays as it is (logged), and its issues stay listed.
    /// </summary>
    private static List<(RewrittenAsset Asset, int Fixes)> Repointed(string folderName, string folderPath, IEnumerable<ModIssue> issues)
    {
        var result = new List<(RewrittenAsset, int)>();
        foreach (var file in issues.Where(i => i.SuggestedRow is not null).GroupBy(i => i.CurrentFile, StringComparer.OrdinalIgnoreCase))
        {
            var moves = file.GroupBy(i => i.ItemName, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().SuggestedRow!, StringComparer.OrdinalIgnoreCase);
            var path = Path.Combine(folderPath, file.Key.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                continue;
            }

            if (AssetReferenceRewriter.TryRewrite(path, moves, out var why) is { } rewritten)
            {
                result.Add((rewritten, moves.Count));
            }
            else
            {
                Serilog.Log.Warning("Fix '{Mod}': left {File} as it is: {Why}", folderName, file.Key, why);
            }
        }

        return result;
    }

    /// <summary>Every row the mods in the Library add (not in the game): a link to one of them is a dependency, not a broken link.</summary>
    public IReadOnlySet<string> LibraryAddedRows()
    {
        lock (_gate)
        {
            var rows = new LibraryAddedRowSet();
            rows.Packages.UnionWith(LibraryPackages());
            foreach (var entry in repository.GetAll().Where(e => !e.IsOpaquePak))
            {
                try
                {
                    foreach (var key in ModHealthChecker.AddedRows(ExmodFolder.ReadPackageOnly(repository.GetFolderPath(entry.FolderName)), GameIndex).Distinct(StringComparer.Ordinal))
                    {
                        if (!rows.Add(key))
                        {
                            rows.AddedBySeveral.Add(key);
                        }
                    }
                }
                catch (Exception)
                {
                    // An unreadable mod adds nothing to the set; it's reported when it's checked itself.
                }
            }

            return rows;
        }
    }

    /// <summary>
    /// Every game package path ("/Game/…") a Library mod has a file of, prebuilt paks included
    /// (merged packs aren't: they hold copies of other mods' files): a reference to one of them
    /// is a dependency on that mod, not a missing game file.
    /// </summary>
    private HashSet<string> LibraryPackages()
    {
        var packages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (gameAssets is null)
        {
            return packages;
        }

        foreach (var entry in repository.GetAll().Where(e => e.MergedPackModNames is null && e.MergedPackOptionDescriptions is null))
        {
            try
            {
                IReadOnlyList<string> files;
                if (entry.IsOpaquePak)
                {
                    using var paks = ModPakContents.TryOpen(repository.GetFolderPath(entry.FolderName));
                    files = paks?.ContentPaths ?? [];
                }
                else
                {
                    files = repository.ListAssetPaths(entry.FolderName);
                }

                packages.UnionWith(files.Select(AssetReferenceChecker.PackagePathOf).OfType<string>());
            }
            catch (Exception)
            {
                // An unreadable mod adds nothing to the set.
            }
        }

        return packages;
    }

    private LibraryEntry? Entry(string folderName) =>
        repository.GetAll().FirstOrDefault(e => string.Equals(e.FolderName, folderName, StringComparison.OrdinalIgnoreCase));

    private ModFixDecisions LoadDecisions(string folderName)
    {
        try
        {
            if (DecisionsPath(folderName) is { } path && File.Exists(path))
            {
                return new ModFixDecisions(JsonSerializer.Deserialize<Dictionary<string, ModIssueFix>>(File.ReadAllText(path), DecisionJson));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // An unreadable file just means nothing remembered; the next Apply rewrites it.
        }

        return new ModFixDecisions();
    }

    private void SaveDecisions(string folderName, ModFixDecisions decisions)
    {
        if (DecisionsPath(folderName) is not { } path)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        IcarusStarlink.Core.IO.AtomicFile.WriteAllText(path, JsonSerializer.Serialize(decisions.Entries, DecisionJson));
    }

    private string? DecisionsPath(string folderName) =>
        decisionsDirectory is null ? null : Path.Combine(decisionsDirectory, string.Join("_", folderName.Split(Path.GetInvalidFileNameChars())) + ".json");
}

/// <summary>The rows every Library mod adds (AddedRowKey keys), which of them more than one mod adds (old game rows two prebuilt paks both carry), and the game files Library mods have.</summary>
public sealed class LibraryAddedRowSet() : HashSet<string>(StringComparer.Ordinal)
{
    public HashSet<string> AddedBySeveral { get; } = new(StringComparer.Ordinal);

    /// <summary>Every game package path a Library mod has a file of (see ModFixService.LibraryPackages).</summary>
    public HashSet<string> Packages { get; } = new(StringComparer.OrdinalIgnoreCase);
}
