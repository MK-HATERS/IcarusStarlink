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
/// </summary>
public sealed class ModFixService(
    ILibraryRepository repository, string dataFolder, Func<IEnumerable<FieldChange>>? gameHistory = null, string? decisionsDirectory = null)
{
    private static readonly JsonSerializerOptions DecisionJson = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public string DataFolder => dataFolder;

    public ModHealthReport Check(string folderName, IDictionary<string, JsonObject?>? baseTableCache = null, GameValueHistory? history = null)
    {
        var package = ExmodFolder.ReadPackageOnly(repository.GetFolderPath(folderName));
        return ModHealthChecker.Check(package, dataFolder, baseTableCache, history ?? History(), repository.ListAssetPaths(folderName),
            decisions: LoadDecisions(folderName), fromPrebuiltPak: Entry(folderName)?.ConvertedFromPrebuiltPak == true);
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

        return Write(folderName, fixes);
    }

    /// <summary>The automatic part for one mod: every sure fix, remembered choices included. Returns (applied, still to look over).</summary>
    public (int Applied, int ToReview) FixSure(string folderName, IDictionary<string, JsonObject?>? baseTableCache = null, GameValueHistory? history = null)
    {
        // A fix can reveal another (a renamed row whose value turns out to be the game's): a few
        // rounds, each backed up, until nothing sure is left.
        var applied = 0;
        var report = Check(folderName, baseTableCache, history);
        for (var round = 0; round < 3 && report.SureCount > 0; round++)
        {
            var thisRound = Write(folderName, [.. report.Issues.Where(i => i.IsSure).Select(i => (i, i.DefaultFix))]);
            if (thisRound == 0)
            {
                break;
            }

            applied += thisRound;
            report = Check(folderName, baseTableCache, history);
        }

        return (applied, report.ReviewCount);
    }

    /// <summary>After a prebuilt pak was converted again: make the remembered choices (and sure fixes) again. Nothing remembered: nothing to do.</summary>
    public int ReapplyRemembered(string folderName) =>
        LoadDecisions(folderName).Count == 0 ? 0 : FixSure(folderName).Applied;

    public int RememberedCount(string folderName) => LoadDecisions(folderName).Count;

    public bool HasBackup(string folderName) => repository.HasModBackup(folderName);

    /// <summary>Puts the mod back as it was before its last change (each fix, rename or refresh backs it up first).</summary>
    public bool RestoreBackup(string folderName) => repository.RestoreLatestModBackup(folderName);

    /// <summary>Forgets this mod's remembered choices: everything is suggested afresh next check.</summary>
    public void Forget(string folderName)
    {
        if (DecisionsPath(folderName) is { } path && File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private int Write(string folderName, IReadOnlyCollection<(ModIssue Issue, ModIssueFix Fix)> fixes)
    {
        var changing = fixes.Where(f => f.Fix != ModIssueFix.KeepMod).ToList();
        if (changing.Count == 0)
        {
            return 0;
        }

        var folderPath = repository.GetFolderPath(folderName);
        var package = ExmodFolder.ReadPackageOnly(folderPath);
        var applied = ModHealthChecker.Apply(package, changing);
        if (applied == 0)
        {
            return 0;
        }

        repository.BackupMod(folderName);
        ExmodFolder.Write(folderPath, new ExmodPackageContents(package, []));

        // A converted pak that hasn't been hand-edited stays "live": it keeps re-converting against
        // new game data, with these fixes made again each time. Anything else shows the ✎.
        if (Entry(folderName) is not { ConvertedFromPrebuiltPak: true, IsLocallyEdited: false })
        {
            repository.MarkLocallyEdited(folderName);
        }

        return applied;
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
