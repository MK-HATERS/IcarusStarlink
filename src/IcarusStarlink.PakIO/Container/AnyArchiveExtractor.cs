using System.IO.Compression;
using IcarusStarlink.PakIO.Safety;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace IcarusStarlink.PakIO.Container;

/// <summary>
/// Extracts a zip, RAR, or 7z archive to a folder wholesale, format sniffed from content the same
/// way ExmodzArchive.Read(string) does — for a caller that doesn't yet know what's inside (Downloads'
/// Activate: a Nexus download could be an EXMODZ-shaped mod, a prebuilt .pak, or a UE4SS mod, and the
/// only way to tell is to look at the extracted content). Applies the same untrusted-input discipline
/// as every other archive reader in this project: safe relative paths (AssetPathGuard), a size budget
/// (ExmodSizeBudget), and a bounded copy that can't decompress past an entry's own declared size.
/// </summary>
public static class AnyArchiveExtractor
{
    public static void ExtractToDirectory(string archiveFilePath, string destinationDirectory)
    {
        if (!ArchiveFactory.IsArchive(archiveFilePath, out var archiveType))
        {
            throw new FormatException($"'{Path.GetFileName(archiveFilePath)}' isn't a recognized archive format.");
        }

        Directory.CreateDirectory(destinationDirectory);
        var sizeBudget = new ExmodSizeBudget("Archive");

        if (archiveType == ArchiveType.Zip)
        {
            // The format sniff only reads the first header: a download cut off midway, or an entry
            // that's encrypted or packed a way .NET can't read, fails later with ZipArchive's own
            // wording. The guards' and the size budget's FormatExceptions pass through as they are.
            try
            {
                using var zip = ZipFile.OpenRead(archiveFilePath);
                foreach (var entry in zip.Entries)
                {
                    // A genuine directory entry's own FullName ends in a path separator — but not every
                    // real-world zip writer uses the ZIP spec's own forward-slash convention: PowerShell's
                    // Compress-Archive cmdlet (a real, ordinary tool a mod author might use) writes
                    // directory entries as e.g. "BP\Objects\", backslash-terminated. Checking only '/'
                    // let a would-be directory entry fall through to ExtractEntry as if it were a real
                    // file, which then failed with a confusing "could not find a part of the path" —
                    // found by actually importing a Compress-Archive-produced zip, not by inspection.
                    if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                    {
                        continue;
                    }

                    ExtractEntry(entry.FullName.Replace('\\', '/'), entry.Length, entry.Open, destinationDirectory, sizeBudget);
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
            {
                throw new FormatException(
                    $"Couldn't read '{Path.GetFileName(archiveFilePath)}': the zip may be damaged or only partly downloaded. Download it again.", ex);
            }

            return;
        }

        if (archiveType is ArchiveType.Rar or ArchiveType.SevenZip)
        {
            try
            {
                using var archive = ArchiveFactory.Open(archiveFilePath);
                foreach (var entry in archive.Entries)
                {
                    if (entry.IsDirectory || string.IsNullOrEmpty(entry.Key))
                    {
                        continue;
                    }

                    ExtractEntry(entry.Key.Replace('\\', '/'), entry.Size, entry.OpenEntryStream, destinationDirectory, sizeBudget);
                }
            }
            catch (Exception ex) when (ex is not FormatException)
            {
                throw new FormatException($"Couldn't read this {archiveType} archive: {ex.Message}", ex);
            }

            return;
        }

        throw new FormatException(
            $"'{Path.GetFileName(archiveFilePath)}' is a {archiveType} archive, which Starlink doesn't support.");
    }

    private static void ExtractEntry(string relativePath, long declaredLength, Func<Stream> open, string destinationDirectory, ExmodSizeBudget sizeBudget)
    {
        // The archive may be from an internet download or a stranger's shared file — never trust
        // an entry name to stay inside wherever this ends up getting extracted.
        var destPath = AssetPathGuard.ResolveWithinDirectory(destinationDirectory, relativePath);
        sizeBudget.Charge($"entry '{relativePath}'", declaredLength);

        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        using var entryStream = open();
        using var fileStream = File.Create(destPath);
        BoundedZipEntryCopy.CopyBounded(entryStream, fileStream, declaredLength, relativePath);
    }
}
