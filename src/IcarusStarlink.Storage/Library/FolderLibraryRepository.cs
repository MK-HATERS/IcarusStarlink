using System.Text;
using IcarusStarlink.Core.Library;
using IcarusStarlink.PakIO;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;
using IcarusStarlink.PakIO.Install;
using Microsoft.Extensions.Logging;

namespace IcarusStarlink.Storage.Library;

/// <summary>
/// The Extracted_Mods folder is the source of truth for each mod's own content; library-only
/// metadata (pin/favorite/notes) lives in a separate metadata directory, keyed by folder name —
/// see LibraryMetaStore for why it can't live inside the mod's own folder. The in-memory entry
/// cache and FTS5 index are both derived from these, never the other way around. A full folder
/// scan only happens once, at construction — Import/Delete/UpdateMetadata each update just the
/// one entry they touched afterward.
/// </summary>
public sealed class FolderLibraryRepository : ILibraryRepository, IDisposable
{
    private readonly string _extractedModsDirectory;
    private readonly string _modBackupsDirectory;
    private readonly LibraryMetaStore _metaStore;
    private readonly LibrarySearchIndex _searchIndex;
    private readonly ILogger<FolderLibraryRepository> _logger;
    private List<LibraryEntry> _cachedEntries = [];

    public FolderLibraryRepository(string extractedModsDirectory, string metaDirectory, string modBackupsDirectory, ILogger<FolderLibraryRepository> logger)
    {
        _extractedModsDirectory = extractedModsDirectory;
        _modBackupsDirectory = modBackupsDirectory;
        _logger = logger;
        _metaStore = new LibraryMetaStore(metaDirectory, logger);
        _searchIndex = new LibrarySearchIndex();

        Directory.CreateDirectory(_extractedModsDirectory);
        RescanAll();
    }

    public IReadOnlyList<LibraryEntry> GetAll() => _cachedEntries;

    public IReadOnlyList<LibraryEntry> Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return _cachedEntries;
        }

        var matchingFolders = _searchIndex.Search(query);
        return [.. _cachedEntries.Where(e => matchingFolders.Contains(e.FolderName))];
    }

    public void Refresh() => RescanAll();

    public LibraryEntry ImportPak(string pakFilePath, string? source = null, int? nexusModId = null, string? catalogEntryId = null)
    {
        // Unlike Import()'s own EXMOD path (whose folder name comes from Package.FileName — already
        // guaranteed safe by AssetPathGuard running inside ExmodJson.Parse, per Import()'s own
        // comment), this name comes straight from whatever file name the caller passed — in
        // practice, a Nexus download's own Content-Disposition file name, chosen by a remote
        // server/mirror. Sanitized here so a Windows-reserved device name or a trailing dot/space
        // can't reach Directory.CreateDirectory/File.Copy below and throw an unhandled exception.
        var baseName = SanitizeFolderNameCandidate(Path.GetFileNameWithoutExtension(pakFilePath));
        var folderName = MakeUniqueFolderName(baseName);
        var targetFolder = Path.Combine(_extractedModsDirectory, folderName);
        Directory.CreateDirectory(targetFolder);

        var targetPakPath = Path.Combine(targetFolder, Path.GetFileName(pakFilePath));
        File.Copy(pakFilePath, targetPakPath);

        // A sibling ISL-Merged.txt next to the pak being imported means this is one of this app's
        // own previously-Rebuild-and-Installed paks, re-imported as its own Library entry (matches
        // classic IMM's own original idea — see RebuildService/InstallService's own doc comments).
        // Read here, at import time, rather than derived later: the source manifest sits in a
        // staging/game folder this repository has no other reason to know about, and it may not
        // even still exist by the time something needs this list.
        var sourceManifestPath = Path.Combine(Path.GetDirectoryName(pakFilePath)!, InstallManifestNames.PakManifest);
        var mergedPackModNames = File.Exists(sourceManifestPath)
            ? ModListText.ParseNames(File.ReadAllText(sourceManifestPath)).ToList()
            : null;

        var meta = new LibraryMeta
        {
            ImportedAtUtc = DateTimeOffset.UtcNow, Source = source, NexusModId = nexusModId,
            CatalogEntryId = catalogEntryId, MergedPackModNames = mergedPackModNames,
        };
        _metaStore.Save(folderName, meta);

        // Surgical, same as Import(): only this one entry is new.
        var entry = ToOpaquePakEntry(folderName, targetPakPath, meta);
        _cachedEntries.Add(entry);
        _searchIndex.Insert(entry, entry.Name);

        return entry;
    }

    public LibraryEntry Import(string sourcePath, string? source = null, int? nexusModId = null, string? catalogEntryId = null)
    {
        if (sourcePath.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("A .pak file isn't an EXMODZ archive — use ImportPak() instead.");
        }

        var contents = Directory.Exists(sourcePath)
            ? ExmodFolder.Read(sourcePath)
            : ExmodzArchive.Read(sourcePath);

        // Package.FileName is already guaranteed a safe, simple identifier (AssetPathGuard runs
        // inside ExmodJson.Parse) — reuse it as the folder name directly rather than re-deriving
        // one, only disambiguating on collision.
        var folderName = MakeUniqueFolderName(contents.Package.FileName);
        var targetFolder = Path.Combine(_extractedModsDirectory, folderName);

        ExmodFolder.Write(targetFolder, contents);
        var meta = new LibraryMeta { ImportedAtUtc = DateTimeOffset.UtcNow, Source = source, NexusModId = nexusModId, CatalogEntryId = catalogEntryId };
        _metaStore.Save(folderName, meta);

        // Surgical: this is the only mod whose content is actually new — no need to re-read
        // every other mod's package just to add one entry.
        var entry = ToEntry(folderName, contents.Package, meta);
        _cachedEntries.Add(entry);
        _searchIndex.Insert(entry, BuildSearchableContent(contents.Package));

        return entry;
    }

    /// <summary>
    /// "New mod…" per the spec — a blank EXMOD (zero Rows) so the editor has something to open
    /// straight into and build up, rather than reading from an existing folder/zip the way every
    /// other Import does. name.Replace(' ', '_') derives the EXMOD's own FileName, matching the
    /// convention real EXMOD authors already use — ExmodFolder.Write's own validation (via
    /// ExmodPackageWriteGuard) rejects anything that isn't actually a safe simple filename, same
    /// as every other write path.
    /// </summary>
    public LibraryEntry CreateBlankMod(string name, string author)
    {
        var fileName = name.Replace(' ', '_');
        var folderName = MakeUniqueFolderName(fileName);
        var targetFolder = Path.Combine(_extractedModsDirectory, folderName);

        var package = new ExmodPackage { Name = name, Author = author, Version = "1.0", Description = "", FileName = fileName, Rows = [] };
        ExmodFolder.Write(targetFolder, new ExmodPackageContents(package, []));

        var meta = new LibraryMeta { ImportedAtUtc = DateTimeOffset.UtcNow };
        _metaStore.Save(folderName, meta);

        var entry = ToEntry(folderName, package, meta);
        _cachedEntries.Add(entry);
        _searchIndex.Insert(entry, BuildSearchableContent(package));

        return entry;
    }

    public void Delete(string folderName)
    {
        Directory.Delete(ResolveFolder(folderName), recursive: true);
        // Otherwise a later mod imported with the same folder name would silently inherit this
        // deleted mod's pin/favorite/notes.
        _metaStore.Delete(folderName);

        _cachedEntries.RemoveAll(e => e.FolderName == folderName);
        _searchIndex.Remove(folderName);
    }

    public void UpdateMetadata(string folderName, bool isPinned, bool isFavorite, string notes)
    {
        ResolveFolder(folderName); // validates the entry actually exists before writing metadata for it

        var meta = _metaStore.Load(folderName);
        meta.IsPinned = isPinned;
        meta.IsFavorite = isFavorite;
        meta.Notes = notes;
        _metaStore.Save(folderName, meta);

        // Only this entry's sidecar metadata changed — its EXMOD content (and every other mod's)
        // is untouched, so update the one cached entry and its one index row in place rather
        // than re-scanning the whole library.
        var existing = _cachedEntries.Find(e => e.FolderName == folderName);
        if (existing is not null)
        {
            existing.IsPinned = isPinned;
            existing.IsFavorite = isFavorite;
            existing.Notes = notes;
            _searchIndex.UpdateNotes(folderName, notes);
        }
    }

    public void MarkLocallyEdited(string folderName)
    {
        ResolveFolder(folderName);

        var meta = _metaStore.Load(folderName);
        meta.IsLocallyEdited = true;
        _metaStore.Save(folderName, meta);

        var existing = _cachedEntries.Find(e => e.FolderName == folderName);
        if (existing is not null)
        {
            existing.IsLocallyEdited = true;
        }
    }

    public void SetDisplayNameOverride(string folderName, string? displayName)
    {
        ResolveFolder(folderName);

        var meta = _metaStore.Load(folderName);
        meta.DisplayNameOverride = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
        _metaStore.Save(folderName, meta);

        // Unlike Pin/Favorite/Notes/IsLocallyEdited above (each a simple, independently-known field
        // that can be poked directly into the cached entry), clearing the override needs to fall
        // back to the mod's own default name — its EXMOD's own declared name, or an opaque pak's
        // Nexus-enriched name/filename — which isn't cheaply available here without re-deriving the
        // exact same logic ToEntry/ToOpaquePakEntry already encode. A full rescan is the simple,
        // always-correct choice for a rare, deliberate user action like a rename.
        RescanAll();
    }

    public void LinkToNexus(string folderName, int nexusModId)
    {
        ResolveFolder(folderName);

        var meta = _metaStore.Load(folderName);
        meta.NexusModId = nexusModId;
        meta.Source = "Nexus";
        _metaStore.Save(folderName, meta);

        var existing = _cachedEntries.Find(e => e.FolderName == folderName);
        if (existing is not null)
        {
            existing.NexusModId = nexusModId;
            existing.Source = "Nexus";
        }
    }

    /// <summary>
    /// Snapshots a mod's whole folder before a risky edit — independent of the EXMOD editor's own
    /// per-field "was (before this edit)" preview, which only ever remembers the single most recent
    /// edit and is lost the moment the editor closes. Reuses PakIO's own FolderBackup (the same
    /// keep-last-5 timestamped-copy algorithm InstallService/Ue4ssLoaderInstallService already use
    /// for the real game folder) rather than a second implementation of the same thing.
    /// </summary>
    public string BackupMod(string folderName)
    {
        var folder = ResolveFolder(folderName);
        FolderBackup.BackupFolder(folder, _modBackupsDirectory);
        return FindLatestModBackupPath(folderName)!;
    }

    public bool HasModBackup(string folderName) => FindLatestModBackupPath(folderName) is not null;

    /// <summary>
    /// Replaces the mod's current folder content with its own most recent backup — a real
    /// point-in-time restore (the folder is deleted first, not merged), so an edit made since the
    /// backup is genuinely undone rather than just overwritten field-by-field. A missing folder is
    /// tolerated, not an error: restoring a mod that was deleted since the backup was taken (e.g.
    /// Get update's own delete-then-reimport failing halfway) is exactly the rescue this exists
    /// for. Returns false (not an error) only when no backup exists at all for this mod.
    /// </summary>
    public bool RestoreLatestModBackup(string folderName)
    {
        var backupPath = FindLatestModBackupPath(folderName);
        if (backupPath is null)
        {
            return false;
        }

        var folder = Path.Combine(_extractedModsDirectory, folderName);
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }

        FolderBackup.CopyDirectory(backupPath, folder);

        RescanAll();
        return true;
    }

    private string? FindLatestModBackupPath(string folderName)
    {
        if (!Directory.Exists(_modBackupsDirectory))
        {
            return null;
        }

        return Directory.GetDirectories(_modBackupsDirectory, $"{folderName}_*")
            .OrderByDescending(Directory.GetCreationTimeUtc)
            .FirstOrDefault();
    }

    public void SetNexusMetadata(string folderName, string? name, string? author, string? description, string? version)
    {
        ResolveFolder(folderName);

        var meta = _metaStore.Load(folderName);
        meta.NexusName = name;
        meta.NexusAuthor = author;
        meta.NexusDescription = description;
        meta.NexusVersion = version;
        _metaStore.Save(folderName, meta);

        // Only an opaque pak entry actually reads these back (ToOpaquePakEntry) — for a normal
        // EXMOD entry this metadata is saved but never surfaced, matching how Nexus enrichment is
        // only ever attempted for the pak case (see DownloadsViewModel.ActivatePendingDownload).
        var existing = _cachedEntries.Find(e => e.FolderName == folderName);
        if (existing is { IsOpaquePak: true })
        {
            existing.Name = name ?? existing.Name;
            existing.Author = author ?? existing.Author;
            existing.Description = description ?? existing.Description;
            existing.Version = version ?? existing.Version;
        }
    }

    /// <summary>An opaque .pak entry (LibraryEntry.IsOpaquePak) has no .EXMOD to enumerate assets from — nothing to browse in the Files tab.</summary>
    public IReadOnlyList<string> ListAssetPaths(string folderName)
    {
        var folder = ResolveFolder(folderName);
        return ClassifyModFolder(folder).HasExmod ? ExmodFolder.ListAssetPaths(folder) : [];
    }

    public byte[] ReadAssetContent(string folderName, string relativePath) =>
        ExmodFolder.ReadAssetContent(ResolveFolder(folderName), relativePath);

    public string? ReadReadme(string folderName)
    {
        var folder = ResolveFolder(folderName);
        if (!ClassifyModFolder(folder).HasExmod)
        {
            return null;
        }

        var readmePath = ExmodFolder.ListAssetPaths(folder)
            .FirstOrDefault(p => Path.GetFileNameWithoutExtension(p).Equals("readme", StringComparison.OrdinalIgnoreCase));

        return readmePath is null ? null : Encoding.UTF8.GetString(ExmodFolder.ReadAssetContent(folder, readmePath));
    }

    public string GetFolderPath(string folderName) => ResolveFolder(folderName);

    private void RescanAll()
    {
        var scanned = new List<(LibraryEntry Entry, string SearchableContent)>();
        var unreadable = new List<string>();

        foreach (var folder in Directory.EnumerateDirectories(_extractedModsDirectory))
        {
            try
            {
                var folderName = Path.GetFileName(folder);
                var meta = _metaStore.Load(folderName);
                // One walk of the folder, reused for both the classify check and (if it turns out
                // to be an EXMOD mod) the actual package read — ReadPackageOnly's own single-arg
                // overload would otherwise re-walk the exact same folder a second time.
                var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToList();
                var (hasExmod, pakPath) = ClassifyModFolder(files);

                if (hasExmod)
                {
                    var package = ExmodFolder.ReadPackageOnly(folder, files);
                    scanned.Add((ToEntry(folderName, package, meta), BuildSearchableContent(package)));
                }
                else
                {
                    var resolvedPakPath = pakPath ?? throw new FormatException($"No .EXMOD or .pak file found under '{folder}'.");
                    var entry = ToOpaquePakEntry(folderName, resolvedPakPath, meta);
                    scanned.Add((entry, entry.Name));
                }
            }
            catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
            {
                // This runs during construction, which the app's default nav page resolves
                // before the main window is even shown — one locked/permission-denied/
                // mid-write folder must not be able to crash the whole app at launch.
                _logger.LogWarning(ex, "Skipped '{Folder}' while scanning the library — could not read it", folder);
                unreadable.Add(Path.GetFileName(folder));
            }
        }

        _cachedEntries = [.. scanned.Select(t => t.Entry)];
        UnreadableFolders = unreadable;
        _searchIndex.Rebuild(scanned);
    }

    /// <summary>See ILibraryRepository — recomputed by every RescanAll (construction, Refresh, restore, rename), so it always reflects the same scan the cached entries came from.</summary>
    public IReadOnlyList<string> UnreadableFolders { get; private set; } = [];

    /// <summary>Convenience overload for a single-folder classification with no precomputed list (ListAssetPaths, ReadReadme) — walks the folder itself, then delegates.</summary>
    private static (bool HasExmod, string? PakPath) ClassifyModFolder(string folder) =>
        ClassifyModFolder(Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToList());

    /// <summary>
    /// Classifies a folder as EXMOD-based vs. opaque-pak vs. neither, given a file list the caller
    /// already walked — RescanAll walks each folder exactly once and reuses that same list for
    /// both this check and (if it turns out to be an EXMOD mod) ExmodFolder.ReadPackageOnly's own
    /// precomputed-list overload, rather than each independently re-walking the folder. Still can't
    /// close the gap against a genuinely concurrent external modification between RescanAll's own
    /// walk and whatever it does with the result — the same TOCTOU caveat ExmodFolder's own
    /// SnapshotFiles callers already accept (see its class doc comment).
    /// </summary>
    private static (bool HasExmod, string? PakPath) ClassifyModFolder(IReadOnlyList<string> files)
    {
        var hasExmod = false;
        string? pakPath = null;
        foreach (var filePath in files)
        {
            if (filePath.EndsWith(".EXMOD", StringComparison.OrdinalIgnoreCase))
            {
                hasExmod = true;
            }
            else if (pakPath is null && filePath.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
            {
                pakPath = filePath;
            }
        }

        return (hasExmod, pakPath);
    }

    private static LibraryEntry ToEntry(string folderName, ExmodPackage package, LibraryMeta meta) => new()
    {
        FolderName = folderName,
        Name = meta.DisplayNameOverride ?? package.Name,
        Author = package.Author,
        Version = package.Version,
        Description = package.Description,
        FileName = package.FileName,
        VariantGroup = package.VariantGroup,
        Variant = package.Variant,
        VariantSort = package.VariantSort,
        IsPinned = meta.IsPinned,
        IsFavorite = meta.IsFavorite,
        Notes = meta.Notes,
        ImportedAtUtc = meta.ImportedAtUtc,
        IsLocallyEdited = meta.IsLocallyEdited,
        Source = meta.Source,
        NexusModId = meta.NexusModId,
        CatalogEntryId = meta.CatalogEntryId,
        DisplayNameOverride = meta.DisplayNameOverride,
    };

    private static LibraryEntry ToOpaquePakEntry(string folderName, string pakFilePath, LibraryMeta meta)
    {
        var sizeMb = new FileInfo(pakFilePath).Length / 1_000_000.0;
        var displayName = Path.GetFileNameWithoutExtension(pakFilePath);
        // A bare .pak carries no embedded metadata of its own — meta.Nexus* (set via
        // SetNexusMetadata, right after a Nexus-sourced Activate looks the mod up by ID) is the
        // only real source of a name/author/description/version better than "Unknown"/blank/a
        // generic line.
        var description = meta.MergedPackModNames is { Count: > 0 } mergedNames
            ? $"IcarusStarlink's own merged pack — folds in {mergedNames.Count} mod(s): {string.Join(", ", mergedNames)}."
            : meta.NexusDescription
                ?? $"Imported prebuilt .pak package ({sizeMb:N1} MB) — no EXMOD data, so no readme or editing. Its internal files can still be listed below.";
        return new LibraryEntry
        {
            FolderName = folderName,
            Name = meta.DisplayNameOverride ?? meta.NexusName ?? displayName,
            Author = meta.NexusAuthor ?? "Unknown",
            Version = meta.NexusVersion ?? "",
            Description = description,
            FileName = displayName,
            IsOpaquePak = true,
            IsPinned = meta.IsPinned,
            IsFavorite = meta.IsFavorite,
            Notes = meta.Notes,
            ImportedAtUtc = meta.ImportedAtUtc,
            IsLocallyEdited = meta.IsLocallyEdited,
            Source = meta.Source,
            NexusModId = meta.NexusModId,
            CatalogEntryId = meta.CatalogEntryId,
            DisplayNameOverride = meta.DisplayNameOverride,
            MergedPackModNames = meta.MergedPackModNames,
        };
    }

    private static string BuildSearchableContent(ExmodPackage package)
    {
        var text = new StringBuilder();
        foreach (var row in package.Rows)
        {
            text.Append(row.CurrentFile).Append(' ');
            foreach (var item in row.FileItems)
            {
                text.Append(item.Name).Append(' ');
                foreach (var fieldName in item.Fields.Keys)
                {
                    text.Append(fieldName).Append(' ');
                }
            }
        }

        return text.ToString();
    }

    private static readonly HashSet<string> ReservedWindowsDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Makes an externally-sourced name safe to use as a single Windows folder-name component — replaces invalid filename characters, trims trailing dots/spaces (Windows silently strips these, which can otherwise produce a confusingly different name than what was asked for), and dodges reserved device names.</summary>
    private static string SanitizeFolderNameCandidate(string candidate)
    {
        var sanitized = new string([.. candidate.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)])
            .TrimEnd('.', ' ');

        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = "mod";
        }

        if (ReservedWindowsDeviceNames.Contains(sanitized))
        {
            sanitized += "_mod";
        }

        return sanitized;
    }

    private string MakeUniqueFolderName(string fileName)
    {
        var candidate = fileName;
        var suffix = 1;
        while (Directory.Exists(Path.Combine(_extractedModsDirectory, candidate)))
        {
            candidate = $"{fileName}_{++suffix}";
        }

        return candidate;
    }

    private string ResolveFolder(string folderName)
    {
        var folder = Path.Combine(_extractedModsDirectory, folderName);
        if (!Directory.Exists(folder))
        {
            throw new DirectoryNotFoundException($"No library entry named '{folderName}'.");
        }

        return folder;
    }

    public void Dispose() => _searchIndex.Dispose();
}
