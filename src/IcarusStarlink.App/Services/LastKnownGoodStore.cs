using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using IcarusStarlink.Core.Profiles;
using IcarusStarlink.PakIO;
using IcarusStarlink.PakIO.Install;

namespace IcarusStarlink.App.Services;

/// <summary>
/// Everything the mod setup was right before an Update everything run changed anything: the
/// profile in use (queue order, merge options, conflict winners), the merged pak and its mod list
/// as they were in the game's mods folder, which UE4SS mods were on, and the game build.
/// </summary>
public sealed class LastKnownGoodSnapshot
{
    /// <summary>The snapshot's own folder (not saved: it's where the file is).</summary>
    [JsonIgnore]
    public string FolderPath { get; set; } = "";

    public DateTimeOffset CreatedAtUtc { get; set; }

    public string? GameBuildId { get; set; }

    public string? ProfileName { get; set; }

    public Profile? Profile { get; set; }

    /// <summary>UE4SS mods that were in the game (on), by folder name.</summary>
    public List<string> EnabledUe4ssMods { get; set; } = [];

    /// <summary>The merged pak and ISL-Merged.txt saved from the mods folder, by file name (none: nothing was installed).</summary>
    public List<string> MergedFiles { get; set; } = [];
}

/// <summary>One mod's proven fixes in an Update everything run.</summary>
public sealed record GameUpdateFixedMod(string FolderName, string Name, int FixCount);

/// <summary>What an Update everything run did, kept beside its snapshot so the review can be opened again later.</summary>
public sealed class GameUpdateRunRecord
{
    public DateTimeOffset FinishedAtUtc { get; set; }

    public string? GameBuildId { get; set; }

    public bool Completed { get; set; }

    public string Message { get; set; } = "";

    public List<GameUpdateFixedMod> Fixed { get; set; } = [];
}

/// <summary>
/// The "last known good" snapshots in &lt;appdata&gt;\Backups\LastKnownGood\&lt;yyyyMMdd-HHmmss&gt;\:
/// made before Update everything changes anything, newest three kept. Roll back puts the
/// merged pak back from one (RestoreMergedPak); the profile and UE4SS parts are put back by the
/// pages that own them.
/// </summary>
public sealed class LastKnownGoodStore(string rootDirectory)
{
    public const int Kept = 3;

    private const string SnapshotFile = "snapshot.json";
    private const string RunFile = "run.json";
    private const string ModsFolder = "mods";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string RootDirectory => rootDirectory;

    /// <summary>Saves a snapshot now, then keeps only the newest Kept.</summary>
    /// <param name="icarusContentPath">The game's Content folder: its Paks\mods merged pak and mod list are copied. Null: none saved.</param>
    public LastKnownGoodSnapshot Create(Profile? profile, string? icarusContentPath, IReadOnlyList<string> enabledUe4ssMods, string? gameBuildId, DateTimeOffset nowUtc)
    {
        Directory.CreateDirectory(rootDirectory);
        var stamp = nowUtc.ToLocalTime().ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var folder = Path.Combine(rootDirectory, stamp);
        for (var n = 2; Directory.Exists(folder); n++)
        {
            folder = Path.Combine(rootDirectory, $"{stamp}-{n}");
        }

        Directory.CreateDirectory(folder);
        var snapshot = new LastKnownGoodSnapshot
        {
            FolderPath = folder,
            CreatedAtUtc = nowUtc,
            GameBuildId = gameBuildId,
            ProfileName = profile?.Name,
            Profile = profile,
            EnabledUe4ssMods = [.. enabledUe4ssMods],
        };

        if (icarusContentPath is { Length: > 0 })
        {
            var modsDirectory = GameModsDirectory(icarusContentPath);
            var saved = Path.Combine(folder, ModsFolder);
            foreach (var name in MergedFileNames().Where(n => File.Exists(Path.Combine(modsDirectory, n))))
            {
                Directory.CreateDirectory(saved);
                File.Copy(Path.Combine(modsDirectory, name), Path.Combine(saved, name));
                snapshot.MergedFiles.Add(name);
            }
        }

        // The description last: a folder without one is a snapshot that didn't finish, never listed.
        File.WriteAllText(Path.Combine(folder, SnapshotFile), JsonSerializer.Serialize(snapshot, JsonOptions));
        Prune();
        return snapshot;
    }

    /// <summary>Every complete snapshot, newest first.</summary>
    public IReadOnlyList<LastKnownGoodSnapshot> List()
    {
        if (!Directory.Exists(rootDirectory))
        {
            return [];
        }

        var snapshots = new List<LastKnownGoodSnapshot>();
        foreach (var folder in Directory.GetDirectories(rootDirectory))
        {
            try
            {
                if (JsonSerializer.Deserialize<LastKnownGoodSnapshot>(File.ReadAllText(Path.Combine(folder, SnapshotFile)), JsonOptions) is { } snapshot)
                {
                    snapshot.FolderPath = folder;
                    snapshots.Add(snapshot);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // Not a snapshot (or not a whole one): left alone.
            }
        }

        return [.. snapshots.OrderByDescending(s => s.CreatedAtUtc)];
    }

    public LastKnownGoodSnapshot? Latest() => List().FirstOrDefault();

    /// <summary>Keeps what the run did beside its snapshot.</summary>
    public void SaveRunRecord(LastKnownGoodSnapshot snapshot, GameUpdateRunRecord record) =>
        File.WriteAllText(Path.Combine(snapshot.FolderPath, RunFile), JsonSerializer.Serialize(record, JsonOptions));

    /// <summary>What the run after this snapshot did; null when it didn't get to the end.</summary>
    public GameUpdateRunRecord? LoadRunRecord(LastKnownGoodSnapshot snapshot)
    {
        try
        {
            var path = Path.Combine(snapshot.FolderPath, RunFile);
            return File.Exists(path) ? JsonSerializer.Deserialize<GameUpdateRunRecord>(File.ReadAllText(path), JsonOptions) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Puts the snapshot's merged pak and mod list back in the game's mods folder. What's there now
    /// is backed up first into backupDirectory (where Install keeps its backups, so Merge &amp;
    /// Install's Restore previous install can switch back). With nothing saved in the snapshot
    /// (nothing was installed then), the current merged pak is backed up and removed.
    /// ArcFission Library's own data pak is never touched. The game must not be running.
    /// </summary>
    public void RestoreMergedPak(LastKnownGoodSnapshot snapshot, string icarusContentPath, string backupDirectory)
    {
        var modsDirectory = GameModsDirectory(icarusContentPath);
        Directory.CreateDirectory(modsDirectory);
        var saved = Path.Combine(snapshot.FolderPath, ModsFolder);
        foreach (var name in snapshot.MergedFiles)
        {
            if (!File.Exists(Path.Combine(saved, name)))
            {
                throw new FileNotFoundException($"The snapshot's copy of {name} is missing.", Path.Combine(saved, name));
            }
        }

        foreach (var name in MergedFileNames())
        {
            var current = Path.Combine(modsDirectory, name);
            if (File.Exists(current))
            {
                // Same backups Install makes (the pak's are trimmed together below).
                if (string.Equals(name, InstallManifestNames.PakManifest, StringComparison.OrdinalIgnoreCase))
                {
                    FolderBackup.BackupFile(current, backupDirectory, BackupCleanup.MergedPakBackupsKept);
                }
                else
                {
                    FolderBackup.BackupFile(current, backupDirectory);
                }

                File.Delete(current);
            }
        }

        foreach (var name in snapshot.MergedFiles)
        {
            var temp = Path.Combine(modsDirectory, name + ".isl-tmp");
            File.Copy(Path.Combine(saved, name), temp, overwrite: true);
            File.Move(temp, Path.Combine(modsDirectory, name), overwrite: true);
        }

        BackupCleanup.PruneMergedPakBackups(backupDirectory);
    }

    private static string GameModsDirectory(string icarusContentPath) => Path.Combine(icarusContentPath, "Paks", "mods");

    /// <summary>The merged pak under its current and earlier names, and its mod list.</summary>
    private static IEnumerable<string> MergedFileNames() =>
        InstallManifestNames.LegacyMergedPakFileNames
            .Prepend(InstallManifestNames.MergedPakFileName)
            .Append(InstallManifestNames.PakManifest)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private void Prune()
    {
        foreach (var stale in List().Skip(Kept))
        {
            try
            {
                Directory.Delete(stale.FolderPath, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Tried again after the next snapshot.
            }
        }
    }
}
