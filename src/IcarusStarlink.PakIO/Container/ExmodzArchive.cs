using System.IO.Compression;
using System.Text;
using IcarusStarlink.PakIO.Exmod;
using IcarusStarlink.PakIO.Safety;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace IcarusStarlink.PakIO.Container;

/// <summary>
/// Reads/writes the .EXMODZ zip layout confirmed against real samples during planning: one
/// "Extracted Mods/&lt;fileName&gt;.EXMOD" JSON entry, alongside a folder tree of already-compiled
/// binary assets plus a loose readme/image. Read(string) also accepts a plain RAR or 7z archive
/// with the same internal layout — real Nexus mod authors sometimes upload one of those instead of
/// a zip, and a downloaded file's own extension can't be trusted anyway, so the real archive format
/// is sniffed from its content (via SharpCompress.ArchiveFactory.IsArchive) rather than assumed.
/// Write always produces a real EXMODZ zip — nothing in this app ever needs to author a RAR/7z.
/// </summary>
public static class ExmodzArchive
{
    /// <summary>A name + declared size + a way to open its content, uniform across ZipArchiveEntry and SharpCompress's IArchiveEntry so ReadEntries only needs to be written once.</summary>
    private readonly record struct ArchiveEntryHandle(string Name, long Length, Func<Stream> Open);

    public static ExmodPackageContents Read(Stream zipStream)
    {
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);

        // Normalize '\' to '/' up front, same as the RAR/7z path's NormalizeEntryKey — a zip entry
        // name may technically contain a backslash (readers vary on how they interpret it), and
        // without this, "a/b.txt" and "a\b.txt" would land as distinct keys and slip past the
        // duplicate-entry check in ReadEntries below.
        var entries = archive.Entries
            .Where(e => !e.FullName.EndsWith('/'))
            .Select(e => new ArchiveEntryHandle(e.FullName.Replace('\\', '/'), e.Length, e.Open))
            .ToList();

        return ReadEntries(entries);
    }

    public static ExmodPackageContents Read(string zipFilePath)
    {
        // Never trust the extension — a Nexus download can be renamed or simply mislabeled, and
        // the OS-level import path (Downloads' Activate) has no user-picked file-type filter to
        // fall back on the way a manual "Import EXMODZ..." dialog does.
        if (!ArchiveFactory.IsArchive(zipFilePath, out var archiveType))
        {
            throw new FormatException(
                $"'{Path.GetFileName(zipFilePath)}' isn't a recognized archive format — refusing to read it.");
        }

        if (archiveType == ArchiveType.Zip)
        {
            using var stream = File.OpenRead(zipFilePath);
            return Read(stream);
        }

        if (archiveType is ArchiveType.Rar or ArchiveType.SevenZip)
        {
            return ReadFromSharpCompress(zipFilePath, archiveType.Value);
        }

        throw new FormatException(
            $"'{Path.GetFileName(zipFilePath)}' is a {archiveType} archive, which IcarusStarlink doesn't " +
            "support — extract it yourself, then use Import folder... instead.");
    }

    private static ExmodPackageContents ReadFromSharpCompress(string filePath, ArchiveType archiveType)
    {
        try
        {
            using var archive = ArchiveFactory.Open(filePath);

            var entries = archive.Entries
                .Where(e => !e.IsDirectory)
                .Select(e => new ArchiveEntryHandle(NormalizeEntryKey(e.Key, archiveType), e.Size, e.OpenEntryStream))
                .ToList();

            return ReadEntries(entries);
        }
        catch (Exception ex) when (ex is not FormatException)
        {
            // SharpCompress throws its own exception types (ArchiveException, CryptographicException
            // for a password-protected archive, etc.) — normalize to FormatException so callers can
            // keep catching one type for "this isn't a usable mod archive", same as the zip path.
            throw new FormatException($"Couldn't read this {archiveType} archive: {ex.Message}", ex);
        }
    }

    private static string NormalizeEntryKey(string? key, ArchiveType archiveType)
    {
        if (string.IsNullOrEmpty(key))
        {
            throw new FormatException($"{archiveType} archive contains an entry with no name.");
        }

        // RAR entries commonly use '\' as their internal separator; 7z normally uses '/' already —
        // normalize both the same way the zip Write side already does, so AssetPathGuard and the
        // duplicate-entry check downstream see one consistent separator regardless of source format.
        return key.Replace('\\', '/');
    }

    /// <summary>
    /// The untrusted-input validation shared by every archive format this reads: exactly one
    /// .EXMOD entry, every asset path safe and non-duplicate, every declared size enforced exactly
    /// (not more, not fewer bytes than claimed) via ReadExactly + a trailing-byte check.
    /// </summary>
    private static ExmodPackageContents ReadEntries(IReadOnlyList<ArchiveEntryHandle> entries)
    {
        var exmodEntries = entries
            .Where(e => e.Name.EndsWith(".EXMOD", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (exmodEntries.Count == 0)
        {
            throw new FormatException("Archive does not contain an .EXMOD file.");
        }

        if (exmodEntries.Count > 1)
        {
            throw new FormatException("Archive contains more than one .EXMOD file — ambiguous which is the mod's own package.");
        }

        var exmodEntry = exmodEntries[0];
        var sizeBudget = new ExmodSizeBudget("Archive");

        // The .EXMOD entry is just as untrusted as the asset entries below it. Checking the
        // declared Length alone isn't enough — that comes from the archive's own directory, which
        // a crafted archive can have disagree with what the compressed data actually decompresses
        // to — so, like the asset path, read into a Length-sized buffer via ReadExactly (which
        // structurally never reads more than that many bytes, regardless of what the underlying
        // stream actually contains) instead of an unbounded ReadToEnd().
        sizeBudget.Charge("archive's .EXMOD entry", exmodEntry.Length);

        ExmodPackage package;
        using (var exmodStream = exmodEntry.Open())
        {
            var buffer = new byte[exmodEntry.Length];
            try
            {
                exmodStream.ReadExactly(buffer);
            }
            catch (EndOfStreamException ex)
            {
                throw new FormatException(
                    $"Archive's .EXMOD entry is corrupt — declared {exmodEntry.Length:N0} bytes but contained fewer.", ex);
            }

            if (exmodStream.ReadByte() != -1)
            {
                throw new FormatException(
                    $"Archive's .EXMOD entry is corrupt — contains more data than its declared {exmodEntry.Length:N0} byte size.");
            }

            // TrimStart handles a leading UTF-8 BOM some tools write; StreamReader used to do
            // this implicitly, decoding raw bytes ourselves doesn't.
            var json = Encoding.UTF8.GetString(buffer).TrimStart('\uFEFF');
            package = ExmodJson.Parse(json);
        }

        var assets = new List<ExmodAssetEntry>();
        var seenAssetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (entry.Name == exmodEntry.Name)
            {
                continue;
            }

            // The archive may be from an internet download or a stranger's shared file — never
            // trust an entry name to stay inside wherever this ends up getting extracted.
            AssetPathGuard.EnsureSafeRelativePath(entry.Name);

            // Neither ZipArchive nor SharpCompress rejects or dedupes two entries sharing a name —
            // but the write side (ExmodPackageWriteGuard) considers that state invalid, so a
            // crafted archive shouldn't be able to produce it here either. Names are already
            // normalized to '/' by the caller (ZipArchive entries natively; RAR/7z via
            // NormalizeEntryKey), so "a/b.txt" and "a\b.txt" both land as the same key here too.
            if (!seenAssetPaths.Add(entry.Name))
            {
                throw new FormatException($"Archive contains duplicate entry '{entry.Name}'.");
            }

            // Declared uncompressed size, from the archive's own directory. Checking it before
            // reading (rather than only capping bytes copied) rejects a decompression-bomb entry
            // without decompressing a single byte of it, and reading directly into a
            // correctly-sized buffer avoids CopyTo-into-a-growing-MemoryStream-then-ToArray's
            // extra reallocation and full-buffer copy.
            sizeBudget.Charge($"archive entry '{entry.Name}'", entry.Length);

            using var entryStream = entry.Open();
            var content = new byte[entry.Length];
            try
            {
                entryStream.ReadExactly(content);
            }
            catch (EndOfStreamException ex)
            {
                // Declared size (from the archive's own directory) overstated the actual content —
                // keep this a FormatException like every other invalid-archive path here, rather
                // than an unhandled EndOfStreamException a caller catching FormatException won't expect.
                throw new FormatException(
                    $"Archive entry '{entry.Name}' is corrupt — declared {entry.Length:N0} bytes but contained fewer.", ex);
            }

            // The opposite mismatch: declared size understated the actual content. ReadExactly
            // alone wouldn't catch this — it happily stops once the buffer is full — so check
            // explicitly that nothing is left, instead of silently accepting truncated content.
            if (entryStream.ReadByte() != -1)
            {
                throw new FormatException(
                    $"Archive entry '{entry.Name}' is corrupt — contains more data than its declared {entry.Length:N0} byte size.");
            }

            assets.Add(new ExmodAssetEntry(entry.Name, content));
        }

        return new ExmodPackageContents(package, assets);
    }

    public static void Write(Stream outputStream, ExmodPackageContents contents)
    {
        ExmodPackageWriteGuard.EnsureWritable(contents);

        using var archive = new ZipArchive(outputStream, ZipArchiveMode.Create, leaveOpen: true);

        var exmodEntry = archive.CreateEntry($"Extracted Mods/{contents.Package.FileName}.EXMOD", CompressionLevel.Optimal);
        using (var entryStream = exmodEntry.Open())
        using (var writer = new StreamWriter(entryStream))
        {
            writer.Write(ExmodJson.Serialize(contents.Package));
        }

        foreach (var asset in contents.Assets)
        {
            // The zip spec only recognizes '/' as a path separator; a literal '\' would be
            // treated as part of the filename (not a folder boundary) by most non-.NET zip
            // tools, so normalize regardless of which separator the caller used.
            var entry = archive.CreateEntry(asset.RelativePath.Replace('\\', '/'), CompressionLevel.Optimal);
            using var entryStream = entry.Open();
            entryStream.Write(asset.Content);
        }
    }

    public static void Write(string outputZipFilePath, ExmodPackageContents contents)
    {
        // Validate before File.Create, which truncates any pre-existing file at this path
        // immediately — otherwise an invalid `contents` destroys a valid file that was already
        // there before the Write(Stream, ...) overload's own validation gets a chance to run.
        // This does mean EnsureWritable runs twice per call (Write(Stream,...) validates again,
        // since it must be self-sufficient for callers that invoke it directly) — accepted as a
        // small, fixed cost rather than adding a "skip validation" parameter or duplicating the
        // write logic just to avoid it.
        ExmodPackageWriteGuard.EnsureWritable(contents);

        using var stream = File.Create(outputZipFilePath);
        Write(stream, contents);
    }
}
