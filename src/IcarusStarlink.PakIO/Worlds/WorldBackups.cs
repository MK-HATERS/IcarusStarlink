using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using IcarusStarlink.PakIO.Install;
using IcarusStarlink.PakIO.Safety;

namespace IcarusStarlink.PakIO.Worlds;

/// <summary>What made a world backup.</summary>
public enum WorldBackupKind
{
    /// <summary>World settings › Save copied the world first.</summary>
    BeforeSettingsSave,

    /// <summary>A world backup Restore copied the world first.</summary>
    BeforeRestore,

    /// <summary>Find the broken mod zipped the slot's whole Prospects folder (every world) before its first round.</summary>
    BeforeFindBrokenMod,
}

/// <summary>One backup holding a world: a single copied world file, or a zip of a slot's worlds.</summary>
/// <param name="TakenAt">Local time, read from the name (a copied file keeps the world's own time, so the file time says nothing).</param>
/// <param name="Sequence">The name's "_2" when two backups landed in the same second (orders them).</param>
public sealed record WorldBackup(string Path, string WorldFileName, WorldBackupKind Kind, DateTime TakenAt, int Sequence)
{
    public bool IsZip => Kind == WorldBackupKind.BeforeFindBrokenMod;

    /// <summary>"before world settings save".</summary>
    public string KindText => Kind switch
    {
        WorldBackupKind.BeforeSettingsSave => "before world settings save",
        WorldBackupKind.BeforeRestore => "before a restore",
        _ => "before Find the broken mod",
    };
}

/// <summary>
/// Each world's backups in the app's own world backup folder (Cache\save_backups\Worlds for player
/// slots, Backups\WorldSettings for servers), listed, restored and pruned per world.
/// <para>
/// Names carry their owner (a slot's Steam id, or "server_&lt;id&gt;"), so two slots with a world
/// of the same name never see each other's backups:
/// "&lt;owner&gt;_world_before_settings_20261028-140312__MyWorld.json",
/// "&lt;owner&gt;_world_before_restore_20261028-140312_2__MyWorld.json", and Find the broken mod's
/// "&lt;owner&gt;_worlds_before_find_broken_mod_20261028-140312.zip" (MergeInstallFinderHost) holding
/// the whole Prospects folder. Backups from before names carried an owner
/// ("2026-10-28_140312_123_MyWorld.json") can't be told apart by slot, so they're left alone: not
/// listed, not pruned.
/// </para>
/// </summary>
public static partial class WorldBackups
{
    private const string SettingsToken = "world_before_settings";
    private const string RestoreToken = "world_before_restore";

    /// <summary>The base name Find the broken mod's world zips use (MergeInstallFinderHost); the stamp and ".zip" follow it.</summary>
    public static string FindBrokenModZipBaseName(string owner) => $"{owner}_worlds_before_find_broken_mod";

    /// <summary>A world restored from a backup bigger than this is refused (no real world comes near it; a damaged zip could claim anything).</summary>
    internal const long MaxWorldBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>The owner for a dedicated server's worlds, from its profile id.</summary>
    public static string ServerOwner(Guid profileId) => $"server_{profileId:N}";

    /// <summary>
    /// Copies the world file into <paramref name="backupFolder"/> under a name that says whose it is
    /// and why it was taken. Returns the backup's path.
    /// </summary>
    public static string BackUp(string worldPath, string backupFolder, string owner, WorldBackupKind kind)
    {
        if (kind == WorldBackupKind.BeforeFindBrokenMod)
        {
            throw new ArgumentException("Find the broken mod zips the whole Prospects folder itself.", nameof(kind));
        }

        CheckOwner(owner);
        Directory.CreateDirectory(backupFolder);
        var baseName = $"{owner}_{(kind == WorldBackupKind.BeforeRestore ? RestoreToken : SettingsToken)}";
        var path = FolderBackup.MakeUniqueTimestampedPath(backupFolder, baseName, DateTimeOffset.Now, "__" + System.IO.Path.GetFileName(worldPath));

        // Copied under a temp name and moved: a half-copied world never looks like a backup.
        var temp = path + ".partial";
        try
        {
            File.Copy(worldPath, temp, overwrite: true);
            File.Move(temp, path);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }

        return path;
    }

    /// <summary>
    /// The backups holding this world (by its file name, "MyWorld.json"), newest first: its own
    /// copies, and Find the broken mod zips with that file at their top. A zip that can't be read
    /// is skipped, never a failure.
    /// </summary>
    public static IReadOnlyList<WorldBackup> List(string backupFolder, string owner, string worldFileName)
    {
        if (!Directory.Exists(backupFolder) || !IsValidOwner(owner))
        {
            return [];
        }

        var result = new List<WorldBackup>();
        foreach (var file in Directory.EnumerateFiles(backupFolder))
        {
            var name = System.IO.Path.GetFileName(file);
            if (ParseCopy(name, owner) is { } copy)
            {
                if (string.Equals(copy.World, worldFileName, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(new WorldBackup(file, copy.World, copy.Kind, copy.TakenAt, copy.Sequence));
                }
            }
            else if (ParseZip(name, owner) is { } zip && ZipHoldsWorld(file, worldFileName))
            {
                result.Add(new WorldBackup(file, worldFileName, WorldBackupKind.BeforeFindBrokenMod, zip.TakenAt, zip.Sequence));
            }
        }

        // Within one second the name's "_2" only counts backups of the same kind, so the moment
        // each file was made (a copy's write time is the world's own, its creation time is the
        // backup's) puts a Save and a Restore backup of the same second in order.
        return [.. result
            .OrderByDescending(b => b.TakenAt)
            .ThenByDescending(b => CreatedUtc(b.Path))
            .ThenByDescending(b => b.Sequence)
            .ThenByDescending(b => b.Path, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Puts the backup's copy of the world back over <paramref name="worldPath"/>. The backup is
    /// read into a temp file beside the world first and checked to be a world save; only then is
    /// the world as it is now backed up (kind BeforeRestore) and the temp file moved over it, so a
    /// damaged backup changes nothing. From a zip of worlds only the world's own file is taken (by
    /// its name at the zip's top, never a path out of the zip). The restored file's time is set to
    /// now, so Steam Cloud takes it as the newest copy. Prunes this world's copies to
    /// <paramref name="keep"/> afterwards. Doesn't check whether the game or server runs: the caller does.
    /// Returns the path of the backup of the world as it was before.
    /// </summary>
    public static string? Restore(WorldBackup backup, string worldPath, string backupFolder, string owner, int keep)
    {
        CheckOwner(owner);
        if (!File.Exists(backup.Path))
        {
            throw new FileNotFoundException("That backup isn't there any more.", backup.Path);
        }

        var fileName = System.IO.Path.GetFileName(worldPath);
        if (!string.Equals(backup.WorldFileName, fileName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"That backup is of '{backup.WorldFileName}', not '{fileName}'. Nothing was changed.");
        }

        var temp = $"{worldPath}.{Guid.NewGuid():N}.isl-restore";
        try
        {
            if (backup.IsZip)
            {
                ExtractWorld(backup.Path, fileName, temp);
            }
            else
            {
                if (new FileInfo(backup.Path).Length > MaxWorldBytes)
                {
                    throw new InvalidDataException("That backup is too big to be a world. Nothing was changed.");
                }

                File.Copy(backup.Path, temp, overwrite: true);
            }

            if (WorldSave.Load(temp) is null)
            {
                throw new InvalidDataException("That backup isn't a readable world save. Nothing was changed.");
            }

            var before = File.Exists(worldPath) ? BackUp(worldPath, backupFolder, owner, WorldBackupKind.BeforeRestore) : null;
            File.Move(temp, worldPath, overwrite: true);
            TouchNow(worldPath);
            Prune(backupFolder, owner, fileName, keep, protect: backup.Path);
            return before;
        }
        finally
        {
            TryDelete(temp);
        }
    }

    /// <summary>
    /// Keeps this world's own copies (Save and Restore backups, together) at <paramref name="keep"/>,
    /// the same rule as the slot backups (SaveRepository): the oldest copy, the world before the
    /// editor ever changed it, always stays, with the newest (keep - 1), and never fewer than one
    /// newest. Find the broken mod zips hold every world and aren't pruned here. A locked file
    /// just stays until the next prune.
    /// </summary>
    /// <param name="protect">A backup never deleted by this prune (the one just restored from).</param>
    public static void Prune(string backupFolder, string owner, string worldFileName, int keep, string? protect = null)
    {
        if (!Directory.Exists(backupFolder) || !IsValidOwner(owner))
        {
            return;
        }

        // Housekeeping after a write that already succeeded: a folder that can't be read never
        // turns that Save or Restore into a reported failure.
        List<WorldBackup> copies;
        try
        {
            copies = [.. List(backupFolder, owner, worldFileName).Where(b => !b.IsZip).Reverse()];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var newestKept = Math.Max(1, keep - 1);
        if (copies.Count <= newestKept + 1)
        {
            return;
        }

        var prunable = copies.Skip(1).ToList();
        foreach (var stale in prunable.Take(prunable.Count - newestKept))
        {
            if (!string.Equals(stale.Path, protect, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(stale.Path);
            }
        }
    }

    /// <summary>Sets a restored file's time to now (best effort: a file whose time can't be set is still restored).</summary>
    public static void TouchNow(string path)
    {
        try
        {
            File.SetLastWriteTime(path, DateTime.Now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void ExtractWorld(string zipPath, string fileName, string destination)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = TopLevelEntry(zip, fileName) ?? throw new InvalidDataException($"That backup has no '{fileName}' in it. Nothing was changed.");
        if (entry.Length > MaxWorldBytes)
        {
            throw new InvalidDataException("That backup's world is too big to be a world. Nothing was changed.");
        }

        using var source = entry.Open();
        using var target = File.Create(destination);
        BoundedZipEntryCopy.CopyBounded(source, target, entry.Length, entry.FullName);
    }

    private static ZipArchiveEntry? TopLevelEntry(ZipArchive zip, string fileName) =>
        zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, fileName, StringComparison.OrdinalIgnoreCase));

    private static bool ZipHoldsWorld(string zipPath, string worldFileName)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            return TopLevelEntry(zip, worldFileName) is not null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static (string World, WorldBackupKind Kind, DateTime TakenAt, int Sequence)? ParseCopy(string name, string owner)
    {
        if (!name.StartsWith(owner + "_", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var match = CopyName().Match(name[(owner.Length + 1)..]);
        if (!match.Success || !TryStamp(match.Groups["stamp"].Value, out var at))
        {
            return null;
        }

        var kind = match.Groups["kind"].Value.Equals(RestoreToken, StringComparison.OrdinalIgnoreCase) ? WorldBackupKind.BeforeRestore : WorldBackupKind.BeforeSettingsSave;
        return (match.Groups["world"].Value, kind, at, Sequence(match));
    }

    private static (DateTime TakenAt, int Sequence)? ParseZip(string name, string owner)
    {
        var baseName = FindBrokenModZipBaseName(owner) + "_";
        if (!name.StartsWith(baseName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var match = ZipName().Match(name[baseName.Length..]);
        return match.Success && TryStamp(match.Groups["stamp"].Value, out var at) ? (at, Sequence(match)) : null;
    }

    private static DateTime CreatedUtc(string path)
    {
        try
        {
            return File.GetCreationTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    private static int Sequence(Match match) => match.Groups["n"].Success && int.TryParse(match.Groups["n"].Value, CultureInfo.InvariantCulture, out var n) ? n : 1;

    private static bool TryStamp(string stamp, out DateTime at) =>
        DateTime.TryParseExact(stamp, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out at);

    private static bool IsValidOwner(string owner) =>
        owner.Length > 0 && owner.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0 && !owner.Contains('*') && !owner.Contains('?');

    private static void CheckOwner(string owner)
    {
        if (!IsValidOwner(owner))
        {
            throw new ArgumentException($"'{owner}' can't be part of a backup name.", nameof(owner));
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // After "<owner>_": the kind, the stamp, an optional "_2", then "__" and the world's file name.
    [GeneratedRegex(@"^(?<kind>world_before_settings|world_before_restore)_(?<stamp>\d{8}-\d{6})(_(?<n>\d+))?__(?<world>.+\.json)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CopyName();

    [GeneratedRegex(@"^(?<stamp>\d{8}-\d{6})(_(?<n>\d+))?\.zip$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ZipName();
}
