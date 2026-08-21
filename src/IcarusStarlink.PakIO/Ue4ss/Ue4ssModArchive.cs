using System.IO.Compression;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Safety;

namespace IcarusStarlink.PakIO.Ue4ss;

/// <summary>
/// UE4SS mod zips have no metadata file of their own (unlike EXMODZ's .EXMOD) — the folder name
/// on disk is the mod's only real identity, confirmed against a real UE4SS install's own Mods
/// folder (each entry there is just a bare directory name, e.g. "NearbyCrafting"). Most real UE4SS
/// mod distributions wrap their content in one top-level folder so unzipping straight into Mods\
/// works correctly; a minority ship loose files with no wrapper. This handles both, and applies
/// the same untrusted-input discipline ExmodzArchive.Read already established — a UE4SS mod zip
/// is exactly as untrusted (an internet download, or a file a stranger shared).
/// </summary>
public static class Ue4ssModArchive
{
    public static string DeriveFolderName(string zipFilePath)
    {
        using var archive = ZipFile.OpenRead(zipFilePath);
        var wrappingFolder = FindSingleWrappingFolder(archive);
        return wrappingFolder ?? Path.GetFileNameWithoutExtension(zipFilePath);
    }

    public static void Extract(string zipFilePath, string destinationFolder)
    {
        using var archive = ZipFile.OpenRead(zipFilePath);
        var wrappingFolder = FindSingleWrappingFolder(archive);
        var sizeBudget = new ExmodSizeBudget("UE4SS mod archive");

        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                continue; // a directory entry, nothing to write
            }

            var relativePath = entry.FullName.Replace('\\', '/');
            if (wrappingFolder is not null)
            {
                // Strip the zip's own wrapping folder — the caller's destinationFolder name
                // (already disambiguated against collisions) is what actually lands on disk, not
                // whatever the zip happened to be named internally.
                relativePath = relativePath[(wrappingFolder.Length + 1)..];
            }

            var destPath = AssetPathGuard.ResolveWithinDirectory(destinationFolder, relativePath);

            sizeBudget.Charge($"UE4SS mod entry '{entry.FullName}'", entry.Length);

            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            using var entryStream = entry.Open();
            using var fileStream = File.Create(destPath);
            entryStream.CopyTo(fileStream);
        }
    }

    /// <summary>Every entry sits under the same single top-level segment → that's a wrapping folder. Any loose entry directly at the root, or more than one distinct top-level segment, means there isn't one.</summary>
    private static string? FindSingleWrappingFolder(ZipArchive archive)
    {
        string? wrapping = null;
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                continue;
            }

            var segments = entry.FullName.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2)
            {
                return null; // a file sitting directly at the zip root — no single wrapping folder
            }

            wrapping ??= segments[0];
            if (!string.Equals(wrapping, segments[0], StringComparison.OrdinalIgnoreCase))
            {
                return null; // more than one top-level folder
            }
        }

        return wrapping;
    }
}
