using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Diffing;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.Services;

/// <summary>
/// Checks a Library mod against the current game data (ModHealthChecker) and writes chosen fixes
/// back, always backed up first (Restore latest backup undoes it). Remembers each mod's choices
/// (ModFixDecisions, one small file per mod) so what the user decided once is done by itself the
/// next time. A mod converted from a prebuilt pak isn't marked edited by a fix: it keeps being
/// re-converted against each new game version, and its sure fixes and remembered choices are made
/// again right after (ReapplyRemembered). Plain file work, safe off the UI thread.
///
/// One call at a time: the fix window, Fix sure problems, the whole-library check and the
/// post-refresh ReapplyRemembered can all reach this one service from different threads. Every
/// public call takes the same lock, so the lazily-filled table index is never filled by two
/// threads at once, and two writers never back up and rewrite the same .EXMOD together.
/// </summary>
public sealed class ModFixService(
    ILibraryRepository repository, string dataFolder, Func<IEnumerable<FieldChange>>? gameHistory = null, string? decisionsDirectory = null)
{
    private static readonly JsonSerializerOptions DecisionJson = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    /// <summary>Held by every public call. Re-entrant (FixSure calls Check on the same thread).</summary>
    private readonly Lock _gate = new();

    public string DataFolder => dataFolder;

    private GameTableIndex? _gameIndex;
    private DateTime _gameIndexStamp;

    /// <summary>One table index for every check (re-read after Refresh game data changes the folder). Only read under _gate.</summary>
    private GameTableIndex GameIndex
    {
        get
        {
            var stamp = Directory.Exists(dataFolder) ? Directory.GetLastWriteTimeUtc(dataFolder) : default;
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
            return ModHealthChecker.Check(package, dataFolder, baseTableCache, history ?? History(), repository.ListAssetPaths(folderName),
                decisions: LoadDecisions(folderName), fromPrebuiltPak: Entry(folderName)?.ConvertedFromPrebuiltPak == true, gameIndex: GameIndex,
                rowsOtherModsAdd: libraryAddedRows ?? LibraryAddedRows());
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
    /// Remembers the choices worth remembering (every one the user looked at: anything not a sure
    /// fix, and any sure fix they overrode), then backs the mod up, applies the fixes and writes it.
    /// Returns how many fixes were applied.
    /// </summary>
    public int Apply(string folderName, IReadOnlyCollection<(ModIssue Issue, ModIssueFix Fix)> fixes)
    {
        lock (_gate)
        {
            var decisions = LoadDecisions(folderName);
            var learned = 0;
            foreach (var (issue, fix) in fixes.Where(f => !f.Issue.IsSure || f.Fix != f.Issue.DefaultFix))
            {
                decisions.Remember(issue, fix);
                learned++;
            }

            if (learned > 0)
            {
                SaveDecisions(folderName, decisions);
            }

            return Write(folderName, fixes, backUp: true);
        }
    }

    /// <summary>The automatic part for one mod: every sure fix, remembered choices included. Returns (applied, still to look over).</summary>
    public (int Applied, int ToReview) FixSure(string folderName, IDictionary<string, JsonObject?>? baseTableCache = null, GameValueHistory? history = null,
        IReadOnlySet<string>? libraryAddedRows = null)
    {
        lock (_gate)
        {
            libraryAddedRows ??= LibraryAddedRows();
            // A fix can reveal another (a renamed row whose value turns out to be the game's): a few
            // rounds until nothing sure is left. Backed up once, before the first round writes, so
            // Restore latest backup undoes the whole click rather than only its last round.
            var applied = 0;
            var report = Check(folderName, baseTableCache, history, libraryAddedRows);
            for (var round = 0; round < 3 && report.SureCount > 0; round++)
            {
                var thisRound = Write(folderName, [.. report.Issues.Where(i => i.IsSure).Select(i => (i, i.DefaultFix))], backUp: applied == 0);
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
    /// After a prebuilt pak was converted again: make the remembered choices (and sure fixes) again.
    /// Sure fixes aren't remembered as choices, so a mod counts as fixed before when it's marked so,
    /// or when the fresh conversion has more sure problems than the copy it replaced
    /// (<paramref name="sureBefore"/>: that copy's count) — which also catches mods fixed before the
    /// mark existed. Without this a broken link fixed with Fix sure problems came back on the next refresh.
    /// </summary>
    public int ReapplyRemembered(string folderName, int? sureBefore = null)
    {
        lock (_gate)
        {
            var fixedBefore = LoadDecisions(folderName).Count > 0
                || Entry(folderName)?.FixedByIslAtUtc is not null
                || (sureBefore is { } before && Check(folderName).SureCount > before);
            return fixedBefore ? FixSure(folderName).Applied : 0;
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
        var applied = ModHealthChecker.Apply(package, changing);
        // Advanced log: each change made to the mod.
        foreach (var (issue, fix) in changing)
        {
            Serilog.Log.Debug("Fix '{Mod}': {Kind} {File}/{Row}.{Field} -> {Fix}", folderName, issue.Kind, issue.CurrentFile, issue.ItemName, issue.FieldName ?? "(row)", fix);
        }
        if (applied == 0)
        {
            return 0;
        }

        if (backUp)
        {
            repository.BackupMod(folderName);
        }

        ExmodFolder.Write(folderPath, new ExmodPackageContents(package, []));
        repository.MarkFixedByIsl(folderName);

        // A converted pak that hasn't been hand-edited stays "live": it keeps re-converting against
        // new game data, with these fixes made again each time. Anything else shows the ✎.
        if (Entry(folderName) is not { ConvertedFromPrebuiltPak: true, IsLocallyEdited: false })
        {
            repository.MarkLocallyEdited(folderName);
        }

        return applied;
    }

    /// <summary>Every row the mods in the Library add (not in the game): a link to one of them is a dependency, not a broken link.</summary>
    public IReadOnlySet<string> LibraryAddedRows()
    {
        lock (_gate)
        {
            var rows = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in repository.GetAll().Where(e => !e.IsOpaquePak))
            {
                try
                {
                    rows.UnionWith(ModHealthChecker.AddedRows(ExmodFolder.ReadPackageOnly(repository.GetFolderPath(entry.FolderName)), GameIndex));
                }
                catch (Exception)
                {
                    // An unreadable mod adds nothing to the set; it's reported when it's checked itself.
                }
            }

            return rows;
        }
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
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(decisions.Entries, DecisionJson));
        File.Move(temp, path, overwrite: true);
    }

    private string? DecisionsPath(string folderName) =>
        decisionsDirectory is null ? null : Path.Combine(decisionsDirectory, string.Join("_", folderName.Split(Path.GetInvalidFileNameChars())) + ".json");
}
