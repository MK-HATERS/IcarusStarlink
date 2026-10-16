using System.Text.Json.Nodes;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Diffing;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.Services;

/// <summary>
/// Checks a Library mod against the current game data (ModHealthChecker) and writes chosen fixes
/// back: always backed up first (Restore latest backup undoes it), then marked as edited so the
/// re-conversion after a game data refresh never quietly replaces it. Plain file work, safe off
/// the UI thread.
/// </summary>
public sealed class ModFixService(ILibraryRepository repository, string dataFolder, Func<IEnumerable<FieldChange>>? gameHistory = null)
{
    public string DataFolder => dataFolder;

    public ModHealthReport Check(string folderName, IDictionary<string, JsonObject?>? baseTableCache = null, IReadOnlyList<FieldChange>? history = null)
    {
        var package = ExmodFolder.ReadPackageOnly(repository.GetFolderPath(folderName));
        return ModHealthChecker.Check(package, dataFolder, baseTableCache, history ?? History(), repository.ListAssetPaths(folderName));
    }

    /// <summary>The game's recorded changes from recent Refresh game data runs (old values a mod may still hold).</summary>
    public IReadOnlyList<FieldChange> History()
    {
        try
        {
            return gameHistory?.Invoke().ToList() ?? [];
        }
        catch (Exception)
        {
            // History is only a hint: without it the other rules still apply.
            return [];
        }
    }

    /// <summary>Backs the mod up, applies the fixes (KeepMod ones are skipped) and writes it; returns how many were applied.</summary>
    public int Apply(string folderName, IReadOnlyCollection<(ModIssue Issue, ModIssueFix Fix)> fixes)
    {
        var changing = fixes.Where(f => f.Fix != ModIssueFix.KeepMod).ToList();
        if (changing.Count == 0)
        {
            return 0;
        }

        var folderPath = repository.GetFolderPath(folderName);
        var package = ExmodFolder.ReadPackageOnly(folderPath);
        repository.BackupMod(folderName);
        var applied = ModHealthChecker.Apply(package, changing);
        if (applied > 0)
        {
            ExmodFolder.Write(folderPath, new ExmodPackageContents(package, []));
            repository.MarkLocallyEdited(folderName);
        }

        return applied;
    }

    /// <summary>The automatic part: every sure fix in one mod. Returns (applied, still to review).</summary>
    public (int Applied, int ToReview) FixSure(string folderName, IDictionary<string, JsonObject?>? baseTableCache = null, IReadOnlyList<FieldChange>? history = null)
    {
        var report = Check(folderName, baseTableCache, history);
        var applied = Apply(folderName, report.Issues.Where(i => i.IsSure).Select(i => (i, i.DefaultFix)).ToList());
        return (applied, report.ReviewCount);
    }
}
