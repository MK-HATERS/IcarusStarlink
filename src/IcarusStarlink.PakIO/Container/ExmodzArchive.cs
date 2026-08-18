using System.IO.Compression;
using System.Text;
using IcarusStarlink.PakIO.Exmod;
using IcarusStarlink.PakIO.Safety;

namespace IcarusStarlink.PakIO.Container;

/// <summary>
/// Reads/writes the .EXMODZ zip layout confirmed against real samples during planning: one
/// "Extracted Mods/&lt;fileName&gt;.EXMOD" JSON entry, alongside a folder tree of already-compiled
/// binary assets plus a loose readme/image.
/// </summary>
public static class ExmodzArchive
{
    public static ExmodPackageContents Read(Stream zipStream)
    {
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);

        var exmodEntries = archive.Entries
            .Where(e => e.FullName.EndsWith(".EXMOD", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (exmodEntries.Count == 0)
        {
            throw new FormatException("EXMODZ archive does not contain an .EXMOD file.");
        }

        if (exmodEntries.Count > 1)
        {
            throw new FormatException("EXMODZ archive contains more than one .EXMOD file — ambiguous which is the mod's own package.");
        }

        var exmodEntry = exmodEntries[0];
        var sizeBudget = new ExmodSizeBudget("EXMODZ archive");

        // The .EXMOD entry is just as untrusted as the asset entries below it. Checking the
        // declared Length alone isn't enough — that comes from the zip's central directory,
        // which a crafted archive can have disagree with what the compressed data actually
        // decompresses to — so, like the asset path, read into a Length-sized buffer via
        // ReadExactly (which structurally never reads more than that many bytes, regardless of
        // what the underlying stream actually contains) instead of an unbounded ReadToEnd().
        sizeBudget.Charge("EXMODZ .EXMOD entry", exmodEntry.Length);

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
                    $"EXMODZ .EXMOD entry is corrupt — declared {exmodEntry.Length:N0} bytes but contained fewer.", ex);
            }

            if (exmodStream.ReadByte() != -1)
            {
                throw new FormatException(
                    $"EXMODZ .EXMOD entry is corrupt — contains more data than its declared {exmodEntry.Length:N0} byte size.");
            }

            // TrimStart handles a leading UTF-8 BOM some tools write; StreamReader used to do
            // this implicitly, decoding raw bytes ourselves doesn't.
            var json = Encoding.UTF8.GetString(buffer).TrimStart('\uFEFF');
            package = ExmodJson.Parse(json);
        }

        var assets = new List<ExmodAssetEntry>();
        var seenAssetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            if (entry == exmodEntry || entry.FullName.EndsWith('/'))
            {
                continue;
            }

            // The archive may be from an internet download or a stranger's shared file — never
            // trust an entry name to stay inside wherever this ends up getting extracted.
            AssetPathGuard.EnsureSafeRelativePath(entry.FullName);

            // ZipArchive itself doesn't reject or dedupe two entries sharing a name — but the
            // write side (ExmodPackageWriteGuard) considers that state invalid, so a crafted
            // archive shouldn't be able to produce it here either. Normalize separators first,
            // matching EnsureWritable/ExmodFolder.Read — "a/b.txt" and "a\b.txt" both pass
            // AssetPathGuard as the same safe segments and must be caught as the same path here too.
            if (!seenAssetPaths.Add(entry.FullName.Replace('\\', '/')))
            {
                throw new FormatException($"EXMODZ archive contains duplicate entry '{entry.FullName}'.");
            }

            // Declared uncompressed size, from the zip central directory. Checking it before
            // reading (rather than only capping bytes copied) rejects a decompression-bomb entry
            // without decompressing a single byte of it, and reading directly into a
            // correctly-sized buffer avoids CopyTo-into-a-growing-MemoryStream-then-ToArray's
            // extra reallocation and full-buffer copy.
            sizeBudget.Charge($"EXMODZ entry '{entry.FullName}'", entry.Length);

            using var entryStream = entry.Open();
            var content = new byte[entry.Length];
            try
            {
                entryStream.ReadExactly(content);
            }
            catch (EndOfStreamException ex)
            {
                // Declared size (from the zip central directory) overstated the actual content —
                // keep this a FormatException like every other invalid-archive path here, rather
                // than an unhandled EndOfStreamException a caller catching FormatException won't expect.
                throw new FormatException(
                    $"EXMODZ entry '{entry.FullName}' is corrupt — declared {entry.Length:N0} bytes but contained fewer.", ex);
            }

            // The opposite mismatch: declared size understated the actual content. ReadExactly
            // alone wouldn't catch this — it happily stops once the buffer is full — so check
            // explicitly that nothing is left, instead of silently accepting truncated content.
            if (entryStream.ReadByte() != -1)
            {
                throw new FormatException(
                    $"EXMODZ entry '{entry.FullName}' is corrupt — contains more data than its declared {entry.Length:N0} byte size.");
            }

            assets.Add(new ExmodAssetEntry(entry.FullName, content));
        }

        return new ExmodPackageContents(package, assets);
    }

    public static ExmodPackageContents Read(string zipFilePath)
    {
        using var stream = File.OpenRead(zipFilePath);
        return Read(stream);
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
