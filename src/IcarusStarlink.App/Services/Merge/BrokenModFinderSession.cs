using System.IO;
using System.Text.Json;

namespace IcarusStarlink.App.Services;

/// <summary>
/// One Find the broken mod search as saved on disk: the player's full queue from before it
/// started (what's put back at the end), the search itself, what's in the game right now, and the
/// save backups taken. Written before every install, so a crash or a closed app mid-search still
/// knows the game holds a cut-down set and the next start can put the mods back.
/// </summary>
public sealed class BrokenModSearchRecord
{
    public DateTimeOffset StartedAtUtc { get; set; }

    /// <summary>What the player said is broken (may be empty).</summary>
    public string Problem { get; set; } = "";

    /// <summary>The queued data mods (folder names) in queue order when the search started.</summary>
    public List<string> OriginalQueue { get; set; } = [];

    /// <summary>Folder name → the name the player knows.</summary>
    public Dictionary<string, string> Names { get; set; } = [];

    /// <summary>Folder name → why it's a suspect (the first group only).</summary>
    public Dictionary<string, string> SuspectReasons { get; set; } = [];

    /// <summary>The search itself (its Needs also give the result's "it needs …").</summary>
    public BrokenModSearchState Search { get; set; } = new();

    /// <summary>The mods a round put in the game; null while the game still has the player's own install.</summary>
    public List<string>? Installed { get; set; }

    /// <summary>
    /// The round being tested reached the game (its install finished). Installed is set before
    /// the install starts (so a crash still puts the mods back), so it can't say this: a round
    /// whose install failed must not reopen next start as waiting for an answer.
    /// </summary>
    public bool RoundInstalled { get; set; }

    /// <summary>
    /// A round's install (or its removal of the merge) has finished once, and that one backed up
    /// what was in the game: the player's real install, now the newest merged pak backup. Later
    /// rounds and putting the mods back only replace temporary round paks, so they don't back up
    /// (a backup each round used to push the real install out of Restore previous install after
    /// a few rounds). Saved, so a restart mid-search keeps the rule. False in a search saved
    /// before this existed: its next install backs up once more (the safe way round).
    /// </summary>
    public bool PlayerInstallBackedUp { get; set; }

    /// <summary>The save backup zips taken before the first round.</summary>
    public List<string> SaveBackups { get; set; } = [];

    /// <summary>Mods the player turned off from the result: not put back in the queue.</summary>
    public List<string> TurnedOff { get; set; } = [];

    public string NameOf(string folderName) =>
        Names.FirstOrDefault(n => string.Equals(n.Key, folderName, StringComparison.OrdinalIgnoreCase)).Value ?? folderName;
}

/// <summary>The search record on disk (one file; none while no search is running).</summary>
public sealed class BrokenModSearchStore(string filePath)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public string FilePath => filePath;

    /// <summary>The saved search; null when there's none, or it can't be read (then it's left there for a look).</summary>
    public BrokenModSearchRecord? Load()
    {
        try
        {
            return File.Exists(filePath) ? JsonSerializer.Deserialize<BrokenModSearchRecord>(File.ReadAllText(filePath), Json) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Serilog.Log.Warning(ex, "Couldn't read the saved Find the broken mod search at {Path}", filePath);
            return null;
        }
    }

    public void Save(BrokenModSearchRecord record)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        IcarusStarlink.Core.IO.AtomicFile.WriteAllText(filePath, JsonSerializer.Serialize(record, Json));
    }

    public void Delete()
    {
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }
}

/// <summary>What the finder needs from the rest of the app: Merge &amp; Install's build and install, the saves, the game-running check.</summary>
public interface IBrokenModFinderHost
{
    bool IsGameRunning();

    /// <summary>The queued data mods that can build (folder names, queue order).</summary>
    IReadOnlyList<string> QueuedFolderNames();

    /// <summary>
    /// Builds and installs only these queued mods (the queue itself isn't changed). Null when done;
    /// otherwise why not. backUpWhatsThere: back up the merged pak in the game first (only the
    /// search's first change does: later ones replace a round's own pak).
    /// </summary>
    Task<string?> InstallOnlyAsync(IReadOnlyCollection<string> folderNames, bool backUpWhatsThere);

    /// <summary>Puts <paramref name="putBack"/> back in the queue where missing, then builds and installs the whole queue. Null when done; otherwise why not. backUpWhatsThere as InstallOnlyAsync's.</summary>
    Task<string?> InstallWholeQueueAsync(IReadOnlyCollection<string> putBack, bool backUpWhatsThere);

    /// <summary>Takes the mod out of the merge queue (its Library ON tick goes off).</summary>
    void RemoveFromQueue(string folderName);

    /// <summary>Backs up every save slot (kept, never pruned). The zips written.</summary>
    IReadOnlyList<string> BackUpSaves();
}

/// <summary>
/// Runs a search: the rounds, the answers and, above all, putting the player's mods back. The
/// record is saved before every install and deleted only once the whole queue is installed again,
/// so the game is never left on a cut-down set without Starlink knowing. No WPF: tested with a fake host.
/// </summary>
public sealed class BrokenModFinderSession(IBrokenModFinderHost host, BrokenModSearchStore store, BrokenModSearchRecord record)
{
    public BrokenModSearchRecord Record => record;

    /// <summary>True once the mods are back and the saved search is gone.</summary>
    public bool IsClosed { get; private set; }

    public const string GameRunningMessage = "Icarus is running. Close the game first, then try again.";

    /// <summary>Backs up the saves before the first round (once). Null when done; otherwise why not (the search doesn't start then).</summary>
    public string? BackUpSaves()
    {
        if (record.SaveBackups.Count > 0)
        {
            return null;
        }

        try
        {
            record.SaveBackups = [.. host.BackUpSaves()];
            store.Save(record);
            return null;
        }
        catch (Exception ex)
        {
            return $"Couldn't back up your saves: {ex.Message}";
        }
    }

    /// <summary>The round to test now (the same one until it's answered); null once the search has ended.</summary>
    public BrokenModTest? CurrentTest => BrokenModSearch.NextTest(record.Search);

    /// <summary>
    /// Installs the current round. Refused while the game runs. The record is saved first: from
    /// here on the game may hold a cut-down set. Null when installed; otherwise why not.
    /// </summary>
    public async Task<string?> InstallRoundAsync()
    {
        if (host.IsGameRunning())
        {
            return GameRunningMessage;
        }

        if (CurrentTest is not { } test)
        {
            return "The search is over: nothing more to test.";
        }

        record.Installed = [.. test.Install];
        record.RoundInstalled = false;
        store.Save(record);
        // Only until a round's install has worked once: that one backed up the player's real
        // install. A failed one may not have got that far, so the next try backs up again.
        var failed = await host.InstallOnlyAsync(test.Install, backUpWhatsThere: !record.PlayerInstallBackedUp);
        if (failed is null)
        {
            record.RoundInstalled = true;
            record.PlayerInstallBackedUp = true;
            store.Save(record);
        }

        return failed;
    }

    public void Answer(BrokenModAnswer answer)
    {
        record.RoundInstalled = false;
        BrokenModSearch.Answer(record.Search, answer);
        store.Save(record);
    }

    /// <summary>Turns a found mod off in the queue: it stays out when the mods are put back.</summary>
    public void TurnOff(string folderName)
    {
        if (!record.TurnedOff.Contains(folderName, StringComparer.OrdinalIgnoreCase))
        {
            record.TurnedOff.Add(folderName);
        }

        host.RemoveFromQueue(folderName);
        store.Save(record);
    }

    /// <summary>
    /// Puts the player's full queue back (every mod from before the search, but the ones turned off
    /// from the result) and installs it. The saved search goes only once that worked; on a failure
    /// it stays, and the next start offers this again. Null when done; otherwise why not.
    /// </summary>
    public async Task<string?> PutModsBackAsync()
    {
        if (IsClosed)
        {
            return null;
        }

        if (host.IsGameRunning())
        {
            return GameRunningMessage;
        }

        var putBack = record.OriginalQueue.Where(f => !record.TurnedOff.Contains(f, StringComparer.OrdinalIgnoreCase)).ToList();
        // Nothing was ever installed by a round: the game still has the player's own install.
        if (record.Installed is not null)
        {
            // What's in the game is a round's pak: not backed up, so the player's real install stays
            // the newest backup (unless no round's install ever worked: then it's backed up now).
            if (await host.InstallWholeQueueAsync(putBack, backUpWhatsThere: !record.PlayerInstallBackedUp) is { } failed)
            {
                return failed;
            }
        }
        else
        {
            foreach (var turnedOff in record.TurnedOff)
            {
                host.RemoveFromQueue(turnedOff);
            }
        }

        store.Delete();
        IsClosed = true;
        return null;
    }

    /// <summary>The queued mods now that weren't when the search started, and the other way round: the search's answers may not fit any more.</summary>
    public (IReadOnlyList<string> Added, IReadOnlyList<string> Gone) QueueChangesSinceStart()
    {
        var now = host.QueuedFolderNames();
        var turnedOff = record.TurnedOff.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = now.Where(f => !record.OriginalQueue.Contains(f, StringComparer.OrdinalIgnoreCase)).ToList();
        var gone = record.OriginalQueue.Where(f => !turnedOff.Contains(f) && !now.Contains(f, StringComparer.OrdinalIgnoreCase)).ToList();
        return (added, gone);
    }
}
