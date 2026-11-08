using System.IO.Compression;
using System.Text;
using System.Text.Json;
using IcarusStarlink.Core.IO;
using IcarusStarlink.Core.Patches;
using IcarusStarlink.Core.Server;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Safety;

namespace IcarusStarlink.PakIO.Patches;

/// <summary>
/// A patch's zip layout mirrors .EXMODZ's own precedent (ExmodzArchive): one manifest.json entry
/// plus, for each bundled EXMOD, a real standalone .EXMODZ file stored verbatim under
/// Bundled/&lt;FolderName&gt;.EXMODZ — not decomposed, so it can be pulled out and imported on its
/// own with any tool that already reads .EXMODZ, this app included. Format 2 adds the pak files
/// of a bundled pak mod under BundledPaks/&lt;FolderName&gt;/ and a bundled UE4SS mod's folder
/// under BundledUe4ss/&lt;Name&gt;/, stored as plain files for the same reason.
/// </summary>
public sealed class PatchService : IPatchService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // A pak mod's files can be hundreds of MB (a model pak); a UE4SS mod is scripts. Both are
    // streamed to disk on import, never buffered whole, so the caps only bound a crafted archive.
    private const long MaxPakEntryBytes = 2_000_000_000;
    private const long MaxUe4ssEntryBytes = 100_000_000;
    private const long MaxExtractedBytes = 8_000_000_000;

    /// <summary>What a bundled UE4SS mod folder leaves behind: the copy's own runtime files, not the mod.</summary>
    private static readonly string[] Ue4ssSkippedExtensions = [".log", ".bak", ".tmp", ".old", ".cache"];

    private readonly long _maxBundledBytes;

    public PatchService()
        : this(ExmodSizeLimits.MaxTotalAssetBytes)
    {
    }

    /// <param name="maxBundledBytes">How much all bundled EXMOD mods may hold together once read, so tests can use a small limit.</param>
    internal PatchService(long maxBundledBytes) => _maxBundledBytes = maxBundledBytes;

    /// <summary>
    /// Runs on a worker thread (deflating every bundled mod and copying paks takes a while), and
    /// writes through a temp file beside outputPath that replaces it only once the whole patch is
    /// written: a failure or a cancel leaves an earlier patch at that path as it was.
    /// </summary>
    public Task ExportAsync(PatchManifest manifest, PatchBundle bundle, string outputPath, CancellationToken cancellationToken = default) =>
        Task.Run(
            () =>
            {
                var json = JsonSerializer.Serialize(manifest, JsonOptions);
                if (bundle.IsEmpty)
                {
                    AtomicFile.WriteAllText(outputPath, json);
                    return;
                }

                AtomicFile.Write(outputPath, temp =>
                {
                    // Disposed inside the callback: the zip's directory is written before the move.
                    using var fileStream = File.Create(temp);
                    using var archive = new ZipArchive(fileStream, ZipArchiveMode.Create);
                    WriteZipPatch(archive, json, bundle, cancellationToken);
                });
            },
            cancellationToken);

    private static void WriteZipPatch(ZipArchive archive, string json, PatchBundle bundle, CancellationToken cancellationToken)
    {
        var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
        using (var entryStream = manifestEntry.Open())
        using (var writer = new StreamWriter(entryStream))
        {
            writer.Write(json);
        }

        foreach (var (folderName, contents) in bundle.Exmods)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = archive.CreateEntry($"Bundled/{folderName}.EXMODZ", CompressionLevel.NoCompression);
            using var entryStream = entry.Open();
            // NoCompression: the entry already holds a fully-compressed .EXMODZ (ExmodzArchive.Write
            // itself deflates its own contents) — compressing already-compressed bytes again wastes
            // CPU for no size benefit.
            ExmodzArchive.Write(entryStream, contents);
        }

        foreach (var (folderName, pakPaths) in bundle.PakFolders)
        {
            foreach (var pakPath in pakPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // A pak is already compressed inside (see paks.md): stored as is.
                archive.CreateEntryFromFile(pakPath, $"BundledPaks/{folderName}/{Path.GetFileName(pakPath)}", CompressionLevel.NoCompression);
            }
        }

        foreach (var (name, folder) in bundle.Ue4ssFolders)
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(folder, file);
                if (IsCopyOwnFile(folder, relative))
                {
                    continue;
                }

                archive.CreateEntryFromFile(file, $"BundledUe4ss/{name}/{relative.Replace('\\', '/')}", CompressionLevel.Optimal);
            }
        }
    }

    /// <summary>
    /// A file of this copy of the mod, not of the mod: enabled.txt (the exporter's on/off), logs,
    /// backups and caches, and ArcFission Library's session files: the exporter's own session,
    /// stale in a friend's copy (<see cref="ServerUe4ssSync.IsArcFissionLibraryState"/>, the same
    /// rule the server sync uses).
    /// </summary>
    private static bool IsCopyOwnFile(string modFolder, string relativePath) =>
        string.Equals(relativePath, "enabled.txt", StringComparison.OrdinalIgnoreCase)
        || Ue4ssSkippedExtensions.Contains(Path.GetExtension(relativePath).ToLowerInvariant())
        || ServerUe4ssSync.IsArcFissionLibraryState(modFolder, relativePath);

    /// <summary>Runs on a worker thread: a patch's bundled mods are inflated and its paks written to disk.</summary>
    public Task<PatchImportContents> ImportAsync(string patchFilePath, CancellationToken cancellationToken = default) =>
        Task.Run(
            () =>
            {
                using var fileStream = File.OpenRead(patchFilePath);
                return IsZip(fileStream) ? ReadZipPatch(fileStream, cancellationToken) : ReadJsonPatch(fileStream);
            },
            cancellationToken);

    /// <summary>Runs on a worker thread and reads manifest.json alone: nothing bundled is inflated or written to disk.</summary>
    public Task<PatchManifest> ReadManifestAsync(string patchFilePath, CancellationToken cancellationToken = default) =>
        Task.Run(
            () =>
            {
                using var fileStream = File.OpenRead(patchFilePath);
                if (!IsZip(fileStream))
                {
                    return ReadJsonPatch(fileStream).Manifest;
                }

                try
                {
                    using var archive = new ZipArchive(fileStream, ZipArchiveMode.Read, leaveOpen: true);
                    return ReadManifest(archive, new ExmodSizeBudget("Patch archive", _maxBundledBytes));
                }
                catch (InvalidDataException ex)
                {
                    throw new FormatException("Couldn't read this patch archive — it may be corrupt or truncated.", ex);
                }
            },
            cancellationToken);

    /// <summary>Sniffed by content ('P' 'K'), not by extension: the user picked this file by hand and may have renamed it.</summary>
    private static bool IsZip(Stream stream)
    {
        var header = new byte[4];
        var headerBytesRead = stream.Read(header, 0, header.Length);
        stream.Position = 0;
        return headerBytesRead >= 2 && header[0] == 0x50 && header[1] == 0x4B;
    }

    private static PatchImportContents ReadJsonPatch(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var json = reader.ReadToEnd();
        var manifest = Deserialize(json);
        return new PatchImportContents(manifest, new Dictionary<string, ExmodPackageContents>());
    }

    private static PatchManifest ReadManifest(ZipArchive archive, ExmodSizeBudget sizeBudget)
    {
        var manifestEntry = archive.GetEntry("manifest.json")
            ?? throw new FormatException("Patch archive is missing manifest.json.");
        return Deserialize(Encoding.UTF8.GetString(ReadEntryBytes(manifestEntry, sizeBudget, "manifest.json")));
    }

    private PatchImportContents ReadZipPatch(Stream stream, CancellationToken cancellationToken)
    {
        string? extractedRoot = null;
        try
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            // One budget for the manifest and every bundled mod, packed and read: each bundled
            // EXMODZ used to get a fresh 1 GB of its own, so many small zip bombs could add up to
            // many GB held at once. A bundled mod is charged twice, once for its packed bytes and
            // once for the files inside it, so mods that barely compress (textures) fit in about
            // half the limit together.
            var sizeBudget = new ExmodSizeBudget("Patch archive", _maxBundledBytes);
            var manifest = ReadManifest(archive, sizeBudget);

            var bundledMods = new Dictionary<string, ExmodPackageContents>();
            foreach (var mod in manifest.Mods.Where(m => m.Bundled && !m.IsPak))
            {
                var entryName = $"Bundled/{mod.FolderName}.EXMODZ";
                var entry = archive.GetEntry(entryName)
                    ?? throw new FormatException($"Patch archive is missing the bundled mod '{mod.Name}' ({entryName}).");
                var bytes = ReadEntryBytes(entry, sizeBudget, entryName);
                using var entryContentStream = new MemoryStream(bytes, writable: false);
                bundledMods[mod.FolderName] = ExmodzArchive.Read(entryContentStream, sizeBudget);
            }

            // Paks and UE4SS folders go to disk: a pak can be hundreds of MB.
            var pakFolders = new Dictionary<string, string>();
            var ue4ssFolders = new Dictionary<string, string>();
            long extracted = 0;
            foreach (var mod in manifest.Mods.Where(m => m.Bundled && m.IsPak))
            {
                extractedRoot ??= NewExtractionRoot();
                var target = Path.Combine(extractedRoot, "Paks", SafeFolderName(mod.FolderName));
                var prefix = $"BundledPaks/{mod.FolderName}/";
                var found = ExtractUnder(archive, prefix, target, MaxPakEntryBytes, ref extracted, cancellationToken);
                if (found == 0)
                {
                    throw new FormatException($"Patch archive is missing the bundled pak mod '{mod.Name}' ({prefix}).");
                }

                pakFolders[mod.FolderName] = target;
            }

            foreach (var ue4ss in manifest.Ue4ssMods.Where(m => m.Bundled))
            {
                extractedRoot ??= NewExtractionRoot();
                // Under a folder named after the mod, inside its own parent: ImportFromFolder's shape.
                var target = Path.Combine(extractedRoot, "Ue4ss", SafeFolderName(ue4ss.Name), ue4ss.Name);
                var prefix = $"BundledUe4ss/{ue4ss.Name}/";
                var found = ExtractUnder(archive, prefix, target, MaxUe4ssEntryBytes, ref extracted, cancellationToken);
                if (found == 0)
                {
                    throw new FormatException($"Patch archive is missing the bundled UE4SS mod '{ue4ss.Name}' ({prefix}).");
                }

                ue4ssFolders[ue4ss.Name] = target;
            }

            return new PatchImportContents(manifest, bundledMods, pakFolders, ue4ssFolders, extractedRoot);
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or IOException or OperationCanceledException)
        {
            TryDelete(extractedRoot);
            if (ex is InvalidDataException)
            {
                // Same normalization ExmodzArchive.Read already applies to its own corrupt/truncated
                // zip case — a patch archive is exactly as untrusted (shared by a friend, downloaded
                // from a server), so callers can keep catching one FormatException type for "this
                // isn't a usable archive" instead of also needing to know about ZipArchive's own
                // exception type.
                throw new FormatException("Couldn't read this patch archive — it may be corrupt or truncated.", ex);
            }

            throw;
        }
    }

    /// <summary>Extracts every entry under <paramref name="prefix"/> into <paramref name="targetFolder"/>, refusing a path that escapes it or an entry over the cap. Returns how many files landed.</summary>
    private static int ExtractUnder(ZipArchive archive, string prefix, string targetFolder, long maxEntryBytes, ref long extractedTotal, CancellationToken cancellationToken)
    {
        var count = 0;
        var root = Path.GetFullPath(targetFolder);
        foreach (var entry in archive.Entries.Where(e => e.FullName.StartsWith(prefix, StringComparison.Ordinal)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = entry.FullName[prefix.Length..];
            if (relative.Length == 0 || relative.EndsWith('/'))
            {
                continue; // a folder entry
            }

            if (entry.Length > maxEntryBytes)
            {
                throw new FormatException($"Patch archive entry '{entry.FullName}' is {entry.Length:N0} bytes, over the {maxEntryBytes:N0} byte limit — refusing to read it.");
            }

            extractedTotal += entry.Length;
            if (extractedTotal > MaxExtractedBytes)
            {
                throw new FormatException($"Patch archive exceeds the maximum total size ({MaxExtractedBytes:N0} bytes) — refusing to read it.");
            }

            // The shared guard, not only a containment check: it also refuses an NTFS stream
            // ("readme.txt:hidden") and a device name (CON).
            string destination;
            try
            {
                destination = AssetPathGuard.ResolveWithinDirectory(root, relative);
            }
            catch (FormatException ex)
            {
                throw new FormatException($"Patch archive entry '{entry.FullName}' points outside its folder or isn't a name Windows can use, so it wasn't extracted.", ex);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using (var source = entry.Open())
            using (var file = File.Create(destination))
            {
                // Bounded by the entry's declared length: a deflate stream that expands past what
                // the header says (a bomb) is refused instead of filling the disk.
                Safety.BoundedZipEntryCopy.CopyBounded(source, file, entry.Length, entry.FullName);
            }

            count++;
        }

        return count;
    }

    private static string NewExtractionRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"IcarusStarlink_Patch_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>
    /// A manifest name must be one folder or file name Windows keeps as written: the shared guard
    /// (no separators, not "." or "..", no character a file name can't hold, no device name like
    /// CON or NUL.txt), no spaces around it and no trailing dot (Windows drops those).
    /// </summary>
    private static void RequireSafeName(string? name, string what)
    {
        if (name is null || !AssetPathGuard.IsSimpleFileName(name) || name.IndexOfAny(['/', '\\', ':']) >= 0
            || name.Trim() != name || name.EndsWith('.'))
        {
            throw new FormatException($"Patch manifest is corrupt: {what} ('{name}') isn't a plain name.");
        }
    }

    private static string SafeFolderName(string name) => AssetPathGuard.SanitizeToSimpleFileName(name, "_");

    /// <summary>Best effort: a leftover temp folder isn't worth hiding the import's own error.</summary>
    private static void TryDelete(string? folder)
    {
        if (folder is not null)
        {
            Directories.TryDelete(folder);
        }
    }

    /// <summary>
    /// Reads a zip entry's full content by its own declared (central-directory) length — same
    /// shape ExmodzArchive.Read already uses for its own entries (see ArchiveEntryReader's own doc
    /// comment), applied one layer up since a patch archive is exactly as untrusted (shared by a
    /// friend, downloaded from a server).
    /// </summary>
    private static byte[] ReadEntryBytes(ZipArchiveEntry entry, ExmodSizeBudget sizeBudget, string description) =>
        ArchiveEntryReader.ReadExactly($"Patch archive entry '{description}'", entry.Length, entry.Open, sizeBudget);

    private static PatchManifest Deserialize(string json)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<PatchManifest>(json, JsonOptions)
                ?? throw new FormatException("Patch manifest is empty or corrupt.");

            // An explicit null (a hand edit, another tool) gets past the deserializer's defaults and
            // used to surface later as a NullReferenceException instead of this documented one.
            if (manifest.Mods is null || manifest.Mods.Any(m => m is null) || manifest.Options is null)
            {
                throw new FormatException("Patch manifest is corrupt: its mod list or options are missing.");
            }

            manifest.Ue4ssMods ??= [];
            if (manifest.Ue4ssMods.Any(m => m is null))
            {
                throw new FormatException("Patch manifest is corrupt: its UE4SS mod list is missing.");
            }

            // The names decide where bundled files are extracted to and, later, which Library and
            // Mods folders are written: a name that is a path (a crafted manifest) is refused here,
            // before any of that.
            foreach (var mod in manifest.Mods)
            {
                mod.PakFileNames ??= [];
                RequireSafeName(mod.FolderName, "a mod's folder name");
                foreach (var pak in mod.PakFileNames)
                {
                    RequireSafeName(pak, "a pak file name");
                }
            }

            foreach (var mod in manifest.Ue4ssMods)
            {
                mod.Configs ??= [];
                mod.Configs.RemoveAll(c => c is null || string.IsNullOrWhiteSpace(c.Path));
                RequireSafeName(mod.Name, "a UE4SS mod's name");
                // Written into mods the player runs with no question: settings only, never code.
                PatchConfigSafety.Filter(mod);
            }

            return manifest;
        }
        catch (JsonException ex)
        {
            throw new FormatException("Patch manifest is corrupt.", ex);
        }
    }
}
