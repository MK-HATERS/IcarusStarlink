using System.Text;
using System.Text.Json;
using IcarusStarlink.Core.IO;
using IcarusStarlink.Core.Library;
using IcarusStarlink.PakIO;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;
using IcarusStarlink.PakIO.Import;
using IcarusStarlink.PakIO.Install;
using IcarusStarlink.PakIO.Safety;
using Microsoft.Extensions.Logging;

namespace IcarusStarlink.Storage.Library;

/// <summary>
/// The Extracted_Mods folder is the source of truth for each mod's own content; library-only
/// metadata (pin/favorite/notes) lives in a separate metadata directory, keyed by folder name —
/// see LibraryMetaStore for why it can't live inside the mod's own folder. The in-memory entry
/// cache and FTS5 index are both derived from these, never the other way around. A full folder
/// scan runs at construction and on Refresh, a restore from backup, a rename and a Library info
/// snapshot write; Import/Delete/UpdateMetadata/RefreshEntry each update just the one entry
/// they touched.
/// </summary>
public sealed class FolderLibraryRepository : ILibraryRepository, IExmodPackageImporter, IDisposable
{
    private readonly string _extractedModsDirectory;

    /// <summary>
    /// Beside Extracted_Mods (outside it, so a scan never lists it): Delete moves a mod's folder here
    /// first, then deletes it. Emptied at construction, in case a delete couldn't finish last time.
    /// </summary>
    private readonly string _trashDirectory;

    private readonly string _modBackupsDirectory;
    private readonly LibraryMetaStore _metaStore;
    private readonly LibrarySearchIndex _searchIndex;
    private readonly ILogger<FolderLibraryRepository> _logger;
    /// <summary>
    /// Copy-on-write: an import or delete replaces the list, never changes it, so a GetAll() being
    /// read on a background thread (the risk pass, the fix service) never sees it change under it
    /// ("Collection was modified"). Volatile so that thread always picks up the latest list.
    /// </summary>
    private volatile List<LibraryEntry> _cachedEntries = [];

    public FolderLibraryRepository(string extractedModsDirectory, string metaDirectory, string modBackupsDirectory, ILogger<FolderLibraryRepository> logger)
    {
        _extractedModsDirectory = extractedModsDirectory;
        _trashDirectory = Path.GetFullPath(extractedModsDirectory).TrimEnd('\\', '/') + ".deleting";
        _modBackupsDirectory = modBackupsDirectory;
        _logger = logger;
        _metaStore = new LibraryMetaStore(metaDirectory, logger);
        _searchIndex = new LibrarySearchIndex();

        Directory.CreateDirectory(_extractedModsDirectory);
        Directories.TryDelete(_trashDirectory, ex => _logger.LogWarning(ex, "Couldn't clear '{Folder}', what was left of deleted mods", _trashDirectory));
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

    public LibraryEntry ImportPak(string pakFilePath, string? source = null, int? nexusModId = null, string? catalogEntryId = null, string? mergedPackProfileName = null)
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
        var targetPakPath = Path.Combine(targetFolder, Path.GetFileName(pakFilePath));

        // A sibling ISL-Merged.txt next to the pak being imported means this is one of this app's
        // own previously-Rebuild-and-Installed paks, re-imported as its own Library entry (matches
        // classic IMM's own original idea — see RebuildService/InstallService's own doc comments).
        // Read here, at import time, rather than derived later: the source manifest sits in a
        // staging/game folder this repository has no other reason to know about, and it may not
        // even still exist by the time something needs this list. Read before the copy, so a read
        // that fails leaves nothing behind.
        var sourceManifestPath = Path.Combine(Path.GetDirectoryName(pakFilePath)!, InstallManifestNames.PakManifest);
        List<string>? mergedPackModNames = null;
        List<string>? mergedPackOptionDescriptions = null;
        if (File.Exists(sourceManifestPath))
        {
            var manifestText = File.ReadAllText(sourceManifestPath);
            mergedPackModNames = ModListText.ParseNames(manifestText).ToList();
            var optionDescriptions = ModListText.ParseOptionDescriptions(manifestText);
            if (optionDescriptions.Count > 0)
            {
                mergedPackOptionDescriptions = optionDescriptions.ToList();
            }
        }

        var meta = new LibraryMeta
        {
            ImportedAtUtc = DateTimeOffset.UtcNow, Source = source, NexusModId = nexusModId,
            CatalogEntryId = catalogEntryId, MergedPackModNames = mergedPackModNames,
            MergedPackProfileName = mergedPackProfileName,
            MergedPackOptionDescriptions = mergedPackOptionDescriptions,
        };
        WriteNewModFolder(targetFolder, () =>
        {
            CopyFiles(targetFolder, [(pakFilePath, targetPakPath)]);
            _metaStore.Save(folderName, meta);
        });

        // Surgical, same as Import(): only this one entry is new.
        var entry = ToOpaquePakEntry(folderName, [targetPakPath], meta);
        _cachedEntries = [.. _cachedEntries, entry];
        _searchIndex.Insert(entry, entry.Name);

        return entry;
    }

    public LibraryEntry ImportPaks(IReadOnlyList<string> pakFilePaths, string name, string? source = null, int? nexusModId = null, string? catalogEntryId = null)
    {
        if (pakFilePaths.Count == 0)
        {
            throw new ArgumentException("At least one .pak file is needed.", nameof(pakFilePaths));
        }

        // Same sanitizing as ImportPak — the name is the download's own (a UE4SS folder name or an
        // upload's file name), never something this app chose.
        var baseName = SanitizeFolderNameCandidate(name);
        var folderName = MakeUniqueFolderName(baseName);
        var targetFolder = Path.Combine(_extractedModsDirectory, folderName);
        var copies = pakFilePaths.Select(p => (Source: p, Target: Path.Combine(targetFolder, Path.GetFileName(p)))).ToList();
        var targetPaths = copies.Select(c => c.Target).ToList();

        var meta = new LibraryMeta
        {
            ImportedAtUtc = DateTimeOffset.UtcNow, Source = source, NexusModId = nexusModId, CatalogEntryId = catalogEntryId,
        };
        WriteNewModFolder(targetFolder, () =>
        {
            CopyFiles(targetFolder, copies);
            _metaStore.Save(folderName, meta);
        });

        var entry = ToOpaquePakEntry(folderName, [.. targetPaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase)], meta);
        _cachedEntries = [.. _cachedEntries, entry];
        _searchIndex.Insert(entry, entry.Name);
        return entry;
    }

    /// <summary>
    /// Writes a brand-new mod folder (its files, then its Library_Meta sidecar). Anything that fails
    /// (a missing source, disk full, a locked file, the sidecar save) removes the folder again before
    /// the error goes on: left behind, an empty folder showed up as an unreadable mod, and a
    /// half-written one came back on the next start as a mod with missing files and no Library info
    /// (a retry then landed as "_2"). Only for a folder MakeUniqueFolderName just picked, so
    /// removing it never loses anything; the cache and search index are updated after it.
    /// </summary>
    private static void WriteNewModFolder(string targetFolder, Action write)
    {
        try
        {
            write();
        }
        catch
        {
            // Best effort: the write's own error is the one worth reporting.
            Directories.TryDelete(targetFolder);
            throw;
        }
    }

    private static void CopyFiles(string targetFolder, IReadOnlyList<(string Source, string Target)> copies)
    {
        Directory.CreateDirectory(targetFolder);
        foreach (var (source, target) in copies)
        {
            File.Copy(source, target);
        }
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

        return RegisterNewPackage(contents, source, nexusModId, catalogEntryId);
    }

    /// <summary>See IExmodPackageImporter — same registration as Import(string, ...), for a caller that already has an in-memory package instead of a folder/zip to read one from.</summary>
    public LibraryEntry ImportPackage(ExmodPackageContents contents, string? source = null, int? nexusModId = null, string? catalogEntryId = null) =>
        RegisterNewPackage(contents, source, nexusModId, catalogEntryId);

    private LibraryEntry RegisterNewPackage(ExmodPackageContents contents, string? source, int? nexusModId, string? catalogEntryId)
    {
        // Package.FileName is already guaranteed a safe, simple identifier (AssetPathGuard runs
        // inside ExmodJson.Parse) — reuse it as the folder name directly rather than re-deriving
        // one, only disambiguating on collision.
        var folderName = MakeUniqueFolderName(contents.Package.FileName);
        var targetFolder = Path.Combine(_extractedModsDirectory, folderName);

        var meta = new LibraryMeta { ImportedAtUtc = DateTimeOffset.UtcNow, Source = source, NexusModId = nexusModId, CatalogEntryId = catalogEntryId };
        WriteNewModFolder(targetFolder, () =>
        {
            ExmodFolder.Write(targetFolder, contents);
            _metaStore.Save(folderName, meta);
        });

        // Surgical: this is the only mod whose content is actually new — no need to re-read
        // every other mod's package just to add one entry.
        var entry = ToEntry(folderName, contents.Package, meta);
        _cachedEntries = [.. _cachedEntries, entry];
        _searchIndex.Insert(entry, BuildSearchableContent(contents.Package));

        return entry;
    }

    /// <summary>
    /// "New mod…" — a blank EXMOD (zero Rows) so the editor has something to open
    /// straight into and build up, rather than reading from an existing folder/zip the way every
    /// other Import does. The EXMOD's FileName (and the folder name) is the typed name with spaces
    /// as _, the convention real EXMOD authors use, and every character a file name can't hold
    /// (: / ? * and the like) as _ too; the mod's Name keeps the name as typed.
    /// </summary>
    public LibraryEntry CreateBlankMod(string name, string author, ModTemplate template = ModTemplate.Blank, string? version = null)
    {
        var fileName = SanitizeFolderNameCandidate(name.Replace(' ', '_'));
        var folderName = MakeUniqueFolderName(fileName);
        var targetFolder = Path.Combine(_extractedModsDirectory, folderName);

        var package = ModTemplateContent.Create(template, name, author);
        package.FileName = fileName;
        if (!string.IsNullOrWhiteSpace(version))
        {
            package.Version = version.Trim();
        }
        var meta = new LibraryMeta { ImportedAtUtc = DateTimeOffset.UtcNow, MadeInStarlink = true };
        WriteNewModFolder(targetFolder, () =>
        {
            ExmodFolder.Write(targetFolder, new ExmodPackageContents(package, []));
            _metaStore.Save(folderName, meta);
        });

        var entry = ToEntry(folderName, package, meta);
        _cachedEntries = [.. _cachedEntries, entry];
        _searchIndex.Insert(entry, BuildSearchableContent(package));

        return entry;
    }

    /// <summary>
    /// All or nothing: the folder is first moved out of Extracted_Mods in one step, which Windows
    /// refuses while any file in it is open, so a locked mod stays whole and listed and the error
    /// goes on. A plain recursive delete used to remove every other file first and leave the mod
    /// gutted, still listed. Once the move worked the mod is gone; deleting the moved folder and the
    /// sidecar after that is tidying, and a failure there is only logged.
    /// </summary>
    public void Delete(string folderName)
    {
        var folder = ResolveFolder(folderName);
        string? moved = Path.Combine(_trashDirectory, $"{folderName}_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(_trashDirectory);
            Directory.Move(folder, moved);
        }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) == ErrorNotSameDevice)
        {
            // Extracted_Mods is a link to another drive: a move can't get there, so delete in place.
            moved = null;
            Directory.Delete(folder, recursive: true);
        }

        _cachedEntries = [.. _cachedEntries.Where(e => e.FolderName != folderName)];
        _searchIndex.Remove(folderName);

        try
        {
            // Otherwise a later mod imported with the same folder name would silently inherit this
            // deleted mod's pin/favorite/notes (every import saves a fresh sidecar, so a stale one
            // left here is overwritten by the next import under this name).
            _metaStore.Delete(folderName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Deleted '{Folder}' but couldn't delete its Library info", folderName);
        }

        if (moved is not null)
        {
            Directories.TryDelete(moved, ex => _logger.LogWarning(ex, "Deleted '{Folder}' but couldn't clear '{Moved}'; it goes on the next start", folderName, moved));
            try
            {
                Directory.Delete(_trashDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Something else is still in it; the next start clears it.
            }
        }
    }

    /// <summary>ERROR_NOT_SAME_DEVICE: a move to another drive.</summary>
    private const int ErrorNotSameDevice = 17;

    public void UpdateMetadata(string folderName, bool isPinned, bool isFavorite, string notes)
    {
        ResolveFolder(folderName); // validates the entry actually exists before writing metadata for it

        // Read-modify-write, and the read is load-bearing — do NOT "optimize" it away by building
        // a LibraryMeta from the cached entry. LibraryMeta carries fields this method never
        // touches (Source, CatalogEntryId, NexusModId, MergedPackModNames, ImportedAtUtc, the
        // Nexus-enrichment fields), several of which have no home on LibraryEntry at all, so
        // writing a memory-built object would silently erase them on something as routine as a
        // pin toggle — losing a mod's update-checking link.
        var meta = LoadMetaForWrite(folderName);
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

    public void MarkLocallyEdited(string folderName) =>
        UpdateMeta(folderName, meta => meta.IsLocallyEdited = true, entry => entry.IsLocallyEdited = true);

    public void MarkFixedByIsl(string folderName)
    {
        var now = DateTimeOffset.UtcNow;
        UpdateMeta(folderName, meta => meta.FixedByIslAtUtc ??= now, entry => entry.FixedByIslAtUtc ??= now);
    }

    /// <summary>See LibraryEntry.ConvertedFromPrebuiltPak — same shape as MarkLocallyEdited.</summary>
    public void MarkConvertedFromPrebuiltPak(string folderName) =>
        UpdateMeta(folderName, meta => meta.ConvertedFromPrebuiltPak = true, entry => entry.ConvertedFromPrebuiltPak = true);

    public void SetDisplayNameOverride(string folderName, string? displayName)
    {
        // No cached-entry mutator: unlike Pin/Favorite/Notes/IsLocallyEdited above (each a simple,
        // independently-known field that can be poked directly into the cached entry), clearing the
        // override needs to fall back to the mod's own default name — its EXMOD's own declared name,
        // or an opaque pak's Nexus-enriched name/filename — which isn't cheaply available here
        // without re-deriving the exact same logic ToEntry/ToOpaquePakEntry already encode. A full
        // rescan is the simple, always-correct choice for a rare, deliberate user action like a rename.
        UpdateMeta(folderName, meta => meta.DisplayNameOverride = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim(), mutateCachedEntry: null);
        RescanAll();
    }

    public void LinkToNexus(string folderName, int nexusModId)
    {
        // The recorded file belongs to the mod the folder was linked to before: keeping it after a
        // link to another ID kept comparing against that old mod's files (an old Update pill).
        var otherMod = false;
        UpdateMeta(folderName,
            meta =>
            {
                otherMod = meta.NexusModId != nexusModId;
                if (otherMod)
                {
                    meta.NexusFileId = null;
                    meta.NexusFileVersion = null;
                }

                meta.NexusModId = nexusModId;
                meta.Source = "Nexus";
            },
            entry =>
            {
                if (otherMod)
                {
                    entry.NexusFileId = null;
                    entry.NexusFileVersion = null;
                    // Same version a rescan shows once the file is gone (ToOpaquePakEntry/ToEntry).
                    if (entry.IsOpaquePak)
                    {
                        entry.Version = entry.NexusVersion ?? "";
                    }
                    else if (entry.ConvertedFromPrebuiltPak)
                    {
                        entry.Version = entry.NexusVersion ?? entry.Version;
                    }
                }

                entry.NexusModId = nexusModId;
                entry.Source = "Nexus";
            });
    }

    public void SetCatalogEntry(string folderName, string catalogEntryId) =>
        // Same surgical cached-entry update LinkToNexus does — nothing about the mod's own files
        // changed, so a full rescan would be pure waste.
        UpdateMeta(folderName,
            meta => { meta.CatalogEntryId = catalogEntryId; meta.Source = "Database"; },
            entry => { entry.CatalogEntryId = catalogEntryId; entry.Source = "Database"; });

    /// <summary>
    /// The read half of every read-modify-write on a sidecar. A sidecar that exists but can't be
    /// read right now (sync client, antivirus) must abort the write: saving the
    /// defaults Load hands back would silently drop the mod's Nexus link / source / catalog ID on
    /// something as small as a pin toggle. The IOException reaches the UI's existing "Save failed"
    /// status path.
    /// </summary>
    private LibraryMeta LoadMetaForWrite(string folderName)
    {
        var meta = _metaStore.Load(folderName, out var couldNotRead);
        if (couldNotRead)
        {
            throw new IOException($"Couldn't read the saved settings for '{folderName}' (the file is locked or unreadable right now) — nothing was changed; try again in a moment.");
        }

        return meta;
    }

    /// <summary>
    /// Shared by every "patch one sidecar metadata field, then mirror it into the cache" mutator
    /// above — ResolveFolder validates the mod exists, then meta is loaded/mutated/saved, then
    /// (unless mutateCachedEntry is null — SetDisplayNameOverride's own case, see its doc comment)
    /// the matching cached entry, if any, is patched the same way so a full RescanAll isn't needed
    /// for these narrow, single/few-field changes.
    /// </summary>
    private void UpdateMeta(string folderName, Action<LibraryMeta> mutateMeta, Action<LibraryEntry>? mutateCachedEntry)
    {
        ResolveFolder(folderName);

        var meta = LoadMetaForWrite(folderName);
        mutateMeta(meta);
        _metaStore.Save(folderName, meta);

        if (mutateCachedEntry is null)
        {
            return;
        }

        var existing = _cachedEntries.Find(e => e.FolderName == folderName);
        if (existing is not null)
        {
            mutateCachedEntry(existing);
        }
    }

    /// <summary>
    /// Snapshots a mod's whole folder before a risky edit — independent of the EXMOD editor's own
    /// per-field "was (before this edit)" preview, which only ever remembers the single most recent
    /// edit and is lost the moment the editor closes. Reuses PakIO's own FolderBackup (the same
    /// keep-last-5 timestamped-copy algorithm InstallService/Ue4ssLoaderInstallService already use
    /// for the real game folder) rather than a second implementation of the same thing.
    /// </summary>
    public string BackupMod(string folderName) => BackupMod(folderName, reason: null);

    /// <summary>
    /// The backup, then its label ("&lt;backup&gt;.label.json" beside it: why, the version, the Nexus
    /// file) for the version history. The trim that keeps the newest 5 doesn't know about labels, so
    /// labels whose backup is gone are removed here too; a label is only ever read by its own
    /// backup's exact name, so one left behind can't attach to another backup.
    /// </summary>
    public string BackupMod(string folderName, string? reason) => BackupMod(folderName, reason, keepBackup: null);

    /// <param name="keepBackup">A backup the keep-5 trim leaves alone this time (the one RestoreModBackup is about to put back).</param>
    private string BackupMod(string folderName, string? reason, string? keepBackup)
    {
        var folder = ResolveFolder(folderName);
        FolderBackup.BackupFolder(folder, _modBackupsDirectory, keepBackup);
        var backupPath = FindLatestModBackupPath(folderName)!;
        WriteBackupLabel(folderName, backupPath, reason);
        RemoveOrphanedBackupLabels(folderName);
        return backupPath;
    }

    private const string BackupLabelSuffix = FolderBackup.LabelSuffix;

    private static string BackupLabelPath(string backupPath) =>
        backupPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + BackupLabelSuffix;

    /// <summary>Best effort: the backup is already whole, and a missing label only means the history shows less about it.</summary>
    private void WriteBackupLabel(string folderName, string backupPath, string? reason)
    {
        try
        {
            var entry = _cachedEntries.Find(e => string.Equals(e.FolderName, folderName, StringComparison.OrdinalIgnoreCase));
            var label = new ModBackupLabel
            {
                Reason = string.IsNullOrWhiteSpace(reason) ? null : reason,
                Version = string.IsNullOrWhiteSpace(entry?.Version) ? null : entry.Version,
                NexusFileId = entry?.NexusFileId,
                NexusFileVersion = entry?.NexusFileVersion,
                LibraryInfo = reason == ModBackupReasons.BeforeDelete ? KeptLibraryInfo(folderName) : null,
            };
            AtomicFile.WriteAllText(BackupLabelPath(backupPath), JsonSerializer.Serialize(label));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Couldn't write the label for backup {Backup}", backupPath);
        }
    }

    /// <summary>The mod's Library info for a delete's backup label, so Undo can bring back its notes, Nexus link and source. Null when the sidecar can't be read right now.</summary>
    private string? KeptLibraryInfo(string folderName)
    {
        var meta = _metaStore.Load(folderName, out var couldNotRead);
        if (couldNotRead)
        {
            _logger.LogWarning("Couldn't read the Library info of '{Folder}' to keep with its backup", folderName);
            return null;
        }

        return JsonSerializer.Serialize(meta);
    }

    /// <summary>Labels and the other files kept beside a backup (FolderBackup.SidecarSuffixes) whose backup is gone.</summary>
    private void RemoveOrphanedBackupLabels(string folderName)
    {
        try
        {
            foreach (var suffix in FolderBackup.SidecarSuffixes)
            {
                foreach (var sidecar in Directory.EnumerateFiles(_modBackupsDirectory, "*" + suffix))
                {
                    var backupName = Path.GetFileName(sidecar)[..^suffix.Length];
                    if (FolderBackup.IsTimestampedBackupOf(backupName, folderName)
                        && !Directory.Exists(Path.Combine(_modBackupsDirectory, backupName)))
                    {
                        File.Delete(sidecar);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the next backup: an extra label is a few bytes and is never read.
        }
    }

    private static ModBackupLabel? ReadBackupLabel(string backupPath)
    {
        try
        {
            var path = BackupLabelPath(backupPath);
            return File.Exists(path) ? JsonSerializer.Deserialize<ModBackupLabel>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public IReadOnlyList<ModBackupInfo> ListModBackups(string folderName) =>
        [.. FolderBackup.EnumerateFolderBackups(_modBackupsDirectory, folderName)
            .Where(HoldsAModFile)
            .Select(path =>
            {
                var label = ReadBackupLabel(path);
                return new ModBackupInfo(path, BackupTimeUtc(path), label?.Reason, label?.Version, label?.NexusFileId);
            })];

    /// <summary>When a backup was taken, from the stamp in its name (not the folder's creation time, see FolderBackup.NewestFirst).</summary>
    private static DateTime? BackupTimeUtc(string backupPath)
    {
        var match = System.Text.RegularExpressions.Regex.Match(Path.GetFileName(backupPath), @"_(?<stamp>\d{8}-\d{6})(?:_\d+)?$");
        return match.Success && DateTime.TryParseExact(match.Groups["stamp"].Value, "yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var when)
            ? when
            : null;
    }

    /// <summary>
    /// Puts back an older backup. The current copy is backed up first, with a trim that leaves the
    /// chosen backup in place even when the history is full, then the chosen backup is copied from
    /// where it is: it is never renamed, so a crash or Clean up backups can't lose it. A copy that fails puts the current copy back from the backup just taken. The
    /// Nexus file the backup's label names comes back with it, so the update check sees the older file.
    /// </summary>
    public bool RestoreModBackup(string folderName, string backupPath)
    {
        if (!AssetPathGuard.IsSimpleFileName(folderName))
        {
            return false;
        }

        var chosen = ListModBackups(folderName).FirstOrDefault(b => string.Equals(
            Path.GetFullPath(b.Path).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(backupPath).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase));
        if (chosen is null)
        {
            return false;
        }

        var label = ReadBackupLabel(chosen.Path);
        var folder = Path.Combine(_extractedModsDirectory, folderName);
        string? currentBackup = null;
        if (Directory.Exists(folder))
        {
            currentBackup = BackupMod(folderName, ModBackupReasons.BeforeRestore, keepBackup: chosen.Path);
        }

        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }

            FolderBackup.CopyDirectory(chosen.Path, folder);
        }
        catch when (currentBackup is not null)
        {
            Directories.TryDelete(folder);
            FolderBackup.CopyDirectory(currentBackup, folder);
            RescanAll();
            throw;
        }

        if (label?.NexusFileId is { } fileId)
        {
            var meta = LoadMetaForWrite(folderName);
            meta.NexusFileId = fileId;
            meta.NexusFileVersion = label.NexusFileVersion;
            _metaStore.Save(folderName, meta);
        }

        ClearFixedMarkIfBackupIsOlder(folderName, chosen.Path);
        RescanAll();
        return true;
    }

    /// <summary>
    /// See ILibraryRepository.RestoreDeletedMod. The backup is copied, never moved, so it stays in
    /// the history. A new mod may have taken the name since the delete: the old one then comes back
    /// beside it under a free name, and the new one is left alone.
    /// </summary>
    public string? RestoreDeletedMod(string folderName, string backupPath)
    {
        if (!AssetPathGuard.IsSimpleFileName(folderName))
        {
            return null;
        }

        var wanted = Path.GetFullPath(backupPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var backup = FolderBackup.EnumerateFolderBackups(_modBackupsDirectory, folderName).FirstOrDefault(b => string.Equals(
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), wanted, StringComparison.OrdinalIgnoreCase));
        if (backup is null || !HoldsAModFile(backup))
        {
            return null;
        }

        var meta = ReadKeptLibraryInfo(backup) ?? new LibraryMeta { ImportedAtUtc = DateTimeOffset.UtcNow };
        var restoredName = MakeUniqueFolderName(folderName);
        var target = Path.Combine(_extractedModsDirectory, restoredName);
        WriteNewModFolder(target, () =>
        {
            FolderBackup.CopyDirectory(backup, target);
            _metaStore.Save(restoredName, meta);
        });

        RescanAll();
        return restoredName;
    }

    /// <summary>The Library info a delete's backup label kept; null when there is none or it can't be read (the mod then comes back with fresh info).</summary>
    private LibraryMeta? ReadKeptLibraryInfo(string backupPath)
    {
        if (ReadBackupLabel(backupPath)?.LibraryInfo is not { } json)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<LibraryMeta>(json);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "The Library info kept with backup {Backup} can't be read", backupPath);
            return null;
        }
    }

    public void SetKeptVersion(string folderName, KeptVersion? kept) =>
        UpdateMeta(folderName, meta => meta.KeptVersion = kept, entry => entry.KeptVersion = kept);

    public bool HasModBackup(string folderName) => FindLatestModBackupPath(folderName) is not null;

    public string? TryGetLatestModBackupPath(string folderName) => FindLatestModBackupPath(folderName);

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

        ClearFixedMarkIfBackupIsOlder(folderName, backupPath);
        RescanAll();
        return true;
    }

    /// <summary>
    /// A backup taken before the mod fixer first changed this mod holds none of its fixes. The
    /// Library info lives outside the mod's folder, so the restore itself doesn't undo it.
    /// </summary>
    private void ClearFixedMarkIfBackupIsOlder(string folderName, string backupPath)
    {
        var meta = LoadMetaForWrite(folderName);
        if (meta.FixedByIslAtUtc is { } fixedAt && Directory.GetCreationTimeUtc(backupPath) <= fixedAt.UtcDateTime)
        {
            meta.FixedByIslAtUtc = null;
            _metaStore.Save(folderName, meta);
        }
    }

    private string? FindLatestModBackupPath(string folderName)
    {
        if (!Directory.Exists(_modBackupsDirectory))
        {
            return null;
        }

        // Exact timestamped-name match, never a "{folderName}_*" glob — see FolderBackup.IsTimestampedBackupOf.
        // A backup with no .EXMOD and no .pak stopped partway (disk full); restoring it would swap a
        // whole mod for an unreadable folder, so it never counts as one.
        return FolderBackup.EnumerateFolderBackups(_modBackupsDirectory, folderName).FirstOrDefault(HoldsAModFile);
    }

    private static bool HoldsAModFile(string folder)
    {
        try
        {
            var (hasExmod, pakPaths) = ClassifyModFolder([.. Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)]);
            return hasExmod || pakPaths.Count > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public string? ReadMetaSnapshot(string folderName) =>
        JsonSerializer.Serialize(LoadMetaForWrite(folderName));

    public void WriteMetaSnapshot(string folderName, string snapshot)
    {
        ResolveFolder(folderName);
        var meta = JsonSerializer.Deserialize<LibraryMeta>(snapshot) ?? throw new InvalidDataException($"The saved Library info for '{folderName}' is empty.");
        _metaStore.Save(folderName, meta);
        // Source, links, marks and name all come from the sidecar: a rescan rebuilds the entry from it.
        RescanAll();
    }

    public void SetNexusFile(string folderName, int fileId, string? fileVersion) =>
        UpdateMeta(folderName,
            meta =>
            {
                meta.NexusFileId = fileId;
                meta.NexusFileVersion = fileVersion;
            },
            entry =>
            {
                entry.NexusFileId = fileId;
                entry.NexusFileVersion = fileVersion;
                if (fileVersion is not null && entry is { IsOpaquePak: true } or { ConvertedFromPrebuiltPak: true })
                {
                    entry.Version = fileVersion;
                }
            });

    public void SetNexusMetadata(string folderName, string? name, string? author, string? description, string? version)
    {
        UpdateMeta(folderName,
            meta =>
            {
                meta.NexusName = name;
                meta.NexusAuthor = author;
                meta.NexusDescription = description;
                meta.NexusVersion = version;
            },
            // Name/author/description: only an opaque pak or a converted-from-one entry reads those back
            // (ToOpaquePakEntry/ToEntry's own ConvertedFromPrebuiltPak branch) — for an ordinary,
            // author-declared EXMOD entry this metadata is saved but never surfaced, so a Nexus
            // lookup can never silently overwrite a real author's own declared name.
            entry =>
            {
                // Surfaced for EVERY entry kind (LibraryEntry.NexusVersion) — the like-for-like side
                // of update-checking; only the display fields below stay opaque/converted-only.
                entry.NexusVersion = version;
                if (entry is { IsOpaquePak: true } or { ConvertedFromPrebuiltPak: true })
                {
                    // Same precedence a rescan uses (ToOpaquePakEntry/ToEntry): the user's own
                    // rename beats the Nexus name, and the exact file's version beats the mod's
                    // headline version — otherwise the row showed the Nexus name/version until
                    // the next restart put the rename and file version back.
                    entry.Name = entry.DisplayNameOverride ?? name ?? entry.Name;
                    entry.Author = author ?? entry.Author;
                    entry.Description = description ?? entry.Description;
                    entry.Version = entry.NexusFileVersion ?? version ?? entry.Version;
                }
            });

        // The FTS index is a derived view too: the cached entry's displayed
        // name/author changed above for an opaque/converted pak, but the index row still carried the
        // pak's file name, so the mod was unsearchable by the name the Library showed until restart.
        var cached = _cachedEntries.Find(e => e.FolderName == folderName);
        if (cached is { IsOpaquePak: true } or { ConvertedFromPrebuiltPak: true })
        {
            _searchIndex.UpdateHeader(folderName, cached.Name, cached.Author);
        }
    }

    /// <summary>An opaque .pak entry (LibraryEntry.IsOpaquePak) has no .EXMOD to enumerate assets from — nothing to browse in the Files tab.</summary>
    public IReadOnlyList<string> ListAssetPaths(string folderName) =>
        ListAssetPaths(folderName, ListFolderFiles(folderName));

    public IReadOnlyList<string> ListAssetPaths(string folderName, IReadOnlyList<string> precomputedFiles)
    {
        var folder = ResolveFolder(folderName);
        return ClassifyModFolder(precomputedFiles).HasExmod ? ExmodFolder.ListAssetPaths(folder, precomputedFiles) : [];
    }

    public byte[] ReadAssetContent(string folderName, string relativePath) =>
        ExmodFolder.ReadAssetContent(ResolveFolder(folderName), relativePath);

    public string? ReadReadme(string folderName) =>
        ReadReadme(folderName, ListFolderFiles(folderName));

    public string? ReadReadme(string folderName, IReadOnlyList<string> precomputedFiles)
    {
        var folder = ResolveFolder(folderName);
        if (!ClassifyModFolder(precomputedFiles).HasExmod)
        {
            return null;
        }

        var readmePath = ExmodFolder.ListAssetPaths(folder, precomputedFiles)
            .FirstOrDefault(p => Path.GetFileNameWithoutExtension(p).Equals("readme", StringComparison.OrdinalIgnoreCase));

        return readmePath is null ? null : Encoding.UTF8.GetString(ExmodFolder.ReadAssetContent(folder, readmePath));
    }

    public IReadOnlyList<string> ListFolderFiles(string folderName) =>
        Directory.EnumerateFiles(ResolveFolder(folderName), "*", SearchOption.AllDirectories).ToList();

    public string GetFolderPath(string folderName) => ResolveFolder(folderName);

    private void RescanAll()
    {
        var scanned = new List<(LibraryEntry Entry, string SearchableContent)>();
        var unreadable = new List<string>();

        foreach (var folder in Directory.EnumerateDirectories(_extractedModsDirectory))
        {
            try
            {
                scanned.Add(ReadModFolder(folder, Path.GetFileName(folder)));
            }
            catch (Exception ex) when (ex is FormatException or JsonException or IOException or UnauthorizedAccessException)
            {
                // One unreadable folder must not stop the scan: the Library loads before the main window shows.
                _logger.LogWarning(ex, "Skipped '{Folder}' while scanning the library — could not read it", folder);
                unreadable.Add(Path.GetFileName(folder));
            }
        }

        _cachedEntries = [.. scanned.Select(t => t.Entry)];
        UnreadableFolders = unreadable;
        _searchIndex.Rebuild(scanned);
    }

    /// <summary>One mod folder's entry and search words; throws FormatException, JsonException, IOException or UnauthorizedAccessException when it can't be read.</summary>
    private (LibraryEntry Entry, string SearchableContent) ReadModFolder(string folder, string folderName)
    {
        var meta = _metaStore.Load(folderName);
        // One walk of the folder, reused for both the classify check and (if it turns out
        // to be an EXMOD mod) the actual package read — ReadPackageOnly's own single-arg
        // overload would otherwise re-walk the exact same folder a second time.
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToList();
        var (hasExmod, pakPaths) = ClassifyModFolder(files);

        if (hasExmod)
        {
            var package = ExmodFolder.ReadPackageOnly(folder, files);
            return (ToEntry(folderName, package, meta), BuildSearchableContent(package));
        }

        if (pakPaths.Count == 0)
        {
            throw new FormatException($"No .EXMOD or .pak file found under '{folder}'.");
        }

        var entry = ToOpaquePakEntry(folderName, pakPaths, meta);
        return (entry, entry.Name);
    }

    /// <summary>
    /// See ILibraryRepository.RefreshEntry. Only this folder is read; every other cached entry
    /// stays as it is. A folder that can't be read now leaves the list and is named in
    /// UnreadableFolders, the same as a full rescan would do.
    /// </summary>
    public void RefreshEntry(string folderName)
    {
        AssetPathGuard.EnsureSimpleFileName(folderName);
        var known = _cachedEntries.Find(e => string.Equals(e.FolderName, folderName, StringComparison.OrdinalIgnoreCase));
        var name = known?.FolderName ?? folderName;
        var folder = Path.Combine(_extractedModsDirectory, name);

        (LibraryEntry Entry, string SearchableContent)? read = null;
        var unreadable = false;
        if (Directory.Exists(folder))
        {
            try
            {
                read = ReadModFolder(folder, name);
            }
            catch (Exception ex) when (ex is FormatException or JsonException or IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Couldn't read '{Folder}' again after it changed", folder);
                unreadable = true;
            }
        }

        var others = _cachedEntries.Where(e => !ReferenceEquals(e, known));
        _cachedEntries = read is { } fresh
            ? known is null
                ? [.. others, fresh.Entry]
                : [.. _cachedEntries.Select(e => ReferenceEquals(e, known) ? fresh.Entry : e)]
            : [.. others];
        UnreadableFolders = unreadable
            ? [.. UnreadableFolders.Where(f => !string.Equals(f, name, StringComparison.OrdinalIgnoreCase)), name]
            : [.. UnreadableFolders.Where(f => !string.Equals(f, name, StringComparison.OrdinalIgnoreCase))];

        _searchIndex.Remove(name);
        if (read is { } indexed)
        {
            _searchIndex.Insert(indexed.Entry, indexed.SearchableContent);
        }
    }

    /// <summary>See ILibraryRepository — recomputed by every RescanAll (construction, Refresh, restore, rename), so it always reflects the same scan the cached entries came from.</summary>
    public IReadOnlyList<string> UnreadableFolders { get; private set; } = [];

    /// <summary>
    /// Classifies a folder as EXMOD-based vs. opaque-pak vs. neither, given a file list the caller
    /// already walked — RescanAll walks each folder exactly once and reuses that same list for
    /// both this check and (if it turns out to be an EXMOD mod) ExmodFolder.ReadPackageOnly's own
    /// precomputed-list overload, rather than each independently re-walking the folder. Still can't
    /// close the gap against a genuinely concurrent external modification between RescanAll's own
    /// walk and whatever it does with the result — the same TOCTOU caveat ExmodFolder's own
    /// SnapshotFiles callers already accept (see its class doc comment).
    /// </summary>
    private static (bool HasExmod, IReadOnlyList<string> PakPaths) ClassifyModFolder(IReadOnlyList<string> files)
    {
        var hasExmod = false;
        var pakPaths = new List<string>();
        foreach (var filePath in files)
        {
            if (filePath.EndsWith(".EXMOD", StringComparison.OrdinalIgnoreCase))
            {
                hasExmod = true;
            }
            else if (filePath.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
            {
                pakPaths.Add(filePath);
            }
        }

        pakPaths.Sort(StringComparer.OrdinalIgnoreCase);
        return (hasExmod, pakPaths);
    }

    private static LibraryEntry ToEntry(string folderName, ExmodPackage package, LibraryMeta meta) => new()
    {
        FolderName = folderName,
        // A converted-from-prebuilt-pak entry's own package.Name/Author are just placeholders (the
        // pak's filename, "Unknown") baked in at conversion time — same NexusName/NexusAuthor
        // fallback chain ToOpaquePakEntry already uses, so a Nexus link's enrichment actually
        // survives a rescan/restart instead of reverting to the placeholder every time.
        Name = meta.DisplayNameOverride ?? (meta.ConvertedFromPrebuiltPak ? meta.NexusName : null) ?? package.Name,
        Author = (meta.ConvertedFromPrebuiltPak ? meta.NexusAuthor : null) ?? package.Author,
        Version = (meta.ConvertedFromPrebuiltPak ? meta.NexusFileVersion ?? meta.NexusVersion : null) ?? package.Version,
        Description = (meta.ConvertedFromPrebuiltPak ? meta.NexusDescription : null) ?? package.Description,
        FileName = package.FileName,
        DeclaredName = package.Name,
        VariantGroup = package.VariantGroup,
        Variant = package.Variant,
        VariantSort = package.VariantSort,
        IsPinned = meta.IsPinned,
        IsFavorite = meta.IsFavorite,
        Notes = meta.Notes,
        ImportedAtUtc = meta.ImportedAtUtc,
        IsLocallyEdited = meta.IsLocallyEdited,
        FixedByIslAtUtc = meta.FixedByIslAtUtc,
        MadeInStarlink = meta.MadeInStarlink,
        ConvertedFromPrebuiltPak = meta.ConvertedFromPrebuiltPak,
        Source = meta.Source,
        NexusModId = meta.NexusModId,
        NexusVersion = meta.NexusVersion,
        NexusFileId = meta.NexusFileId,
        NexusFileVersion = meta.NexusFileVersion,
        KeptVersion = meta.KeptVersion,
        CatalogEntryId = meta.CatalogEntryId,
        DisplayNameOverride = meta.DisplayNameOverride,
    };

    private static LibraryEntry ToOpaquePakEntry(string folderName, IReadOnlyList<string> pakFilePaths, LibraryMeta meta)
    {
        var pakFilePath = pakFilePaths[0];
        var sizeMb = pakFilePaths.Sum(p => new FileInfo(p).Length) / 1_000_000.0;
        // Several paks from one download: the folder (named after the download itself by ImportPaks)
        // is the mod's name, not whichever pak happens to sort first.
        var displayName = pakFilePaths.Count > 1 ? folderName : Path.GetFileNameWithoutExtension(pakFilePath);
        // A bare .pak carries no embedded metadata of its own — meta.Nexus* (set via
        // SetNexusMetadata, right after a Nexus-sourced Activate looks the mod up by ID) is the
        // only real source of a name/author/description/version better than "Unknown"/blank/a
        // generic line. MergedPackProfileName (this app's own Merge & Install profile that
        // produced the pak) takes priority over NexusAuthor for Author specifically — a merged
        // pack was never authored by anyone on Nexus, and naming the profile instead makes
        // multiple profiles' own builds distinguishable in Library.
        // MergedPackModNames is already in real merge-queue order (position 0 = lowest priority,
        // matching MergeEngine's own convention) — it just wasn't ever LABELED as an order before.
        // Numbering it here makes that order visible without needing new storage: on an unresolved
        // conflict, the LAST-numbered mod touching a field is the one whose value won by default.
        // A build with an empty mod queue but at least one gameplay option enabled (e.g. just Stacks
        // Multiplier) leaves MergedPackModNames empty — without this branch, its own freshly-
        // installed Library entry would fall all the way to the generic "no EXMOD data" wording
        // below, indistinguishable from importing a totally unrelated prebuilt .pak, even though
        // MergedPackProfileName proves this really is this app's own Rebuild output.
        var description = meta.MergedPackModNames is { Count: > 0 } mergedNames
            ? $"IcarusStarlink's own merged pack — folds in {mergedNames.Count} mod(s), in merge order " +
              $"(the last one listed wins a field conflict by default): {string.Join(", ", mergedNames.Select((n, i) => $"{i + 1}. {n}"))}."
            : meta.MergedPackOptionDescriptions is { Count: > 0 } optionDescriptions
                ? $"IcarusStarlink's own merged pack — built from gameplay options only (no mods queued): {string.Join(", ", optionDescriptions)}."
                : meta.NexusDescription
                    ?? (pakFilePaths.Count > 1
                        ? $"Imported prebuilt .pak bundle — {pakFilePaths.Count} paks ({sizeMb:N1} MB total): {string.Join(", ", pakFilePaths.Select(Path.GetFileName))}. No EXMOD data, so no readme or editing; every pak is folded into the merge."
                        : $"Imported prebuilt .pak package ({sizeMb:N1} MB) — no EXMOD data, so no readme or editing. Its internal files can still be listed below.");
        return new LibraryEntry
        {
            FolderName = folderName,
            Name = meta.DisplayNameOverride ?? meta.NexusName ?? displayName,
            Author = meta.MergedPackProfileName ?? meta.NexusAuthor ?? "Unknown",
            Version = meta.NexusFileVersion ?? meta.NexusVersion ?? "",
            Description = description,
            FileName = Path.GetFileNameWithoutExtension(pakFilePath),
            IsOpaquePak = true,
            PakFileNames = [.. pakFilePaths.Select(p => Path.GetFileName(p)!)],
            IsPinned = meta.IsPinned,
            IsFavorite = meta.IsFavorite,
            Notes = meta.Notes,
            ImportedAtUtc = meta.ImportedAtUtc,
            IsLocallyEdited = meta.IsLocallyEdited,
            Source = meta.Source,
            NexusModId = meta.NexusModId,
            NexusVersion = meta.NexusVersion,
            NexusFileId = meta.NexusFileId,
            NexusFileVersion = meta.NexusFileVersion,
            KeptVersion = meta.KeptVersion,
            CatalogEntryId = meta.CatalogEntryId,
            DisplayNameOverride = meta.DisplayNameOverride,
            MergedPackModNames = meta.MergedPackModNames,
            MergedPackOptionDescriptions = meta.MergedPackOptionDescriptions,
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

    /// <summary>Makes an externally-sourced name safe to use as a single Windows folder-name component — delegates to AssetPathGuard.SanitizeToSimpleFileName (PakIO), the one shared implementation of this logic, rather than a second hand-copied one that had already drifted (its own reserved-device-name check compared the WHOLE sanitized string instead of the segment before its first dot, so e.g. "CON.v2" slipped through unfixed and reached Directory.CreateDirectory unguarded).</summary>
    private static string SanitizeFolderNameCandidate(string candidate) => AssetPathGuard.SanitizeToSimpleFileName(candidate);

    private string MakeUniqueFolderName(string fileName) => ModFolders.MakeUnique(_extractedModsDirectory, fileName);

    private string ResolveFolder(string folderName) =>
        ModFolders.Resolve(_extractedModsDirectory, folderName, $"No library entry named '{folderName}'.");

    public void Dispose() => _searchIndex.Dispose();
}
