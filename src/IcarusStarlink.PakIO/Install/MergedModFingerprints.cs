using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IcarusStarlink.Core.IO;

namespace IcarusStarlink.PakIO.Install;

/// <summary>One mod as a merge was built from it: its version and a hash of its Library folder's files.</summary>
public sealed record MergedModFingerprint(string FolderName, string Version, string Hash);

/// <summary>One build: the pak it made (by hash) and the mods it was made from.</summary>
public sealed record MergedBuildRecord(string PakHash, DateTimeOffset BuiltAtUtc, IReadOnlyList<MergedModFingerprint> Mods);

/// <summary>
/// What each mod in a merged pak looked like when it was built, so the Library can say a mod
/// changed since (user request 2026-10-24: a mod updated in Starlink, or edited outside it, still
/// read "In game" — the installed pak only lists names). Every build adds a record keyed by its
/// pak's hash; the record that matches the pak really in the game is the one compared against, so
/// Remove from game, Restore previous install, Roll back and a pak swapped by hand need no
/// bookkeeping of their own.
/// </summary>
public static class MergedModFingerprints
{
    public const string FileName = "ISL-Merged.mods.json";

    /// <summary>Builds kept: enough for Restore previous install and Roll back to find theirs.</summary>
    private const int RecordsKept = 8;

    /// <summary>A hash of every file under the folder (relative path, size and contents); "" when the folder is gone.</summary>
    public static string HashFolder(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return "";
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var info = new FileInfo(file);
            hash.AppendData(Encoding.UTF8.GetBytes($"{Path.GetRelativePath(folder, file).ToLowerInvariant()}|{info.Length}|"));
            hash.AppendData(FileHash(info));
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>The file's hash, "" when it doesn't exist.</summary>
    public static string HashFile(string path) => File.Exists(path) ? Convert.ToHexString(FileHash(new FileInfo(path))) : "";

    /// <summary>Adds a build's record (replacing one for the same pak) and keeps the newest few.</summary>
    public static void Add(string recordPath, MergedBuildRecord record)
    {
        var records = Load(recordPath).Where(r => !string.Equals(r.PakHash, record.PakHash, StringComparison.OrdinalIgnoreCase)).ToList();
        records.Insert(0, record);
        Directory.CreateDirectory(Path.GetDirectoryName(recordPath)!);
        AtomicFile.WriteAllText(recordPath, JsonSerializer.Serialize(records.Take(RecordsKept).ToList()));
    }

    /// <summary>Every kept record, newest first; empty when there are none or the file can't be read.</summary>
    public static IReadOnlyList<MergedBuildRecord> Load(string recordPath)
    {
        try
        {
            return File.Exists(recordPath) ? JsonSerializer.Deserialize<List<MergedBuildRecord>>(File.ReadAllText(recordPath)) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>The record of the build whose pak is <paramref name="installedPakPath"/>; null when none matches (a pak built elsewhere, or none installed).</summary>
    public static MergedBuildRecord? ForInstalledPak(string recordPath, string installedPakPath)
    {
        var records = Load(recordPath);
        if (records.Count == 0 || !File.Exists(installedPakPath))
        {
            return null;
        }

        var hash = HashFile(installedPakPath);
        return records.FirstOrDefault(r => string.Equals(r.PakHash, hash, StringComparison.OrdinalIgnoreCase));
    }

    // Hashed again only when a file's size or time written changes: the Library asks after every change.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Path, long Length, DateTime Written), byte[]> FileHashes = new();

    private static byte[] FileHash(FileInfo info)
    {
        var key = (info.FullName.ToLowerInvariant(), info.Length, info.LastWriteTimeUtc);
        if (FileHashes.TryGetValue(key, out var known))
        {
            return known;
        }

        using var stream = info.OpenRead();
        var hash = SHA256.HashData(stream);
        if (FileHashes.Count > 20_000)
        {
            FileHashes.Clear();
        }

        FileHashes[key] = hash;
        return hash;
    }
}
