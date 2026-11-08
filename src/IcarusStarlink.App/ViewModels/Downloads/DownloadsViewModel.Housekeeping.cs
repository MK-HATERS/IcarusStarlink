using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.App.Views;
using IcarusStarlink.Catalog.Nexus;
using IcarusStarlink.Core.Nexus;
using IcarusStarlink.PakIO.Install;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// Keeping Pending_Downloads tidy: stray .part files a crash left, each download's Nexus file
/// version, Discard older versions (superseded rows only) and Remove unused files (files no
/// download points at). Both deletes ask first.
/// </summary>
public sealed partial class DownloadsViewModel
{
    /// <summary>How old a .part file must be before the sweep deletes it: a download still running writes to its own every few seconds.</summary>
    private static readonly TimeSpan StrayPartAge = TimeSpan.FromDays(1);

    /// <summary>The sweep run when the page is first shown this session; null until then.</summary>
    private Task<int>? _partSweep;

    /// <summary>The Downloads page is on screen: the first time this session, stray .part files are cleared out in the background.</summary>
    public void OnPageShown()
    {
        _partSweep ??= SweepStrayPartFilesAsync();
        RefreshCleanupCounts();
    }

    /// <summary>
    /// Deletes .part files under Pending_Downloads older than a day. A download deletes its own
    /// .part when it fails or is cancelled, but a crash or a killed process leaves it. Skips any
    /// file a running download is writing (its reserved path, or a file held open). Never throws;
    /// returns how many went.
    /// </summary>
    internal Task<int> SweepStrayPartFilesAsync(DateTimeOffset? now = null)
    {
        var cutoff = (now ?? DateTimeOffset.UtcNow).UtcDateTime - StrayPartAge;
        return Task.Run(() =>
        {
            var removed = 0;
            try
            {
                if (!Directory.Exists(_pendingDownloadsDirectory))
                {
                    return 0;
                }

                foreach (var part in Directory.EnumerateFiles(_pendingDownloadsDirectory, "*.part", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(part) > cutoff || IsActiveDownloadFile(part))
                        {
                            continue;
                        }

                        // A file still held open (a download in another Starlink, antivirus) can't be deleted: it stays.
                        File.Delete(part);
                        removed++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort housekeeping: an unreadable folder is left as it is.
            }

            return removed;
        });
    }

    /// <summary>Whether a running download owns this path: its final file, or its "&lt;final&gt;.&lt;guid&gt;.part".</summary>
    private bool IsActiveDownloadFile(string path)
    {
        var full = Path.GetFullPath(path);
        lock (_reservedDownloadPaths)
        {
            return _reservedDownloadPaths.Any(reserved =>
                string.Equals(full, reserved, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(reserved + ".", StringComparison.OrdinalIgnoreCase));
        }
    }

    // --- File versions ---

    /// <summary>Downloads the back-fill looked up and found no version for: not looked up again on every reload.</summary>
    private readonly HashSet<(int ModId, int FileId)> _fileVersionBackfillTried = [];

    /// <summary>Saves a download's Nexus file version and shows it on its row (and on the row now listed for the same file).</summary>
    private void RecordFileVersion(PendingDownloadItemViewModel item, string fileVersion)
    {
        TrySaveDownloadsList(() => _pendingDownloadStore.SetFileVersion(item.ModId, item.FileId, fileVersion));
        ShowFileVersion(item, fileVersion);
    }

    private void ShowFileVersion(PendingDownloadItemViewModel item, string fileVersion)
    {
        item.FileVersion = fileVersion;
        foreach (var row in PendingDownloads.Where(p => p.ModId == item.ModId && p.FileId == item.FileId))
        {
            row.FileVersion = fileVersion;
        }
    }

    /// <summary>
    /// Downloads recorded before the version was kept: the version the installed copy of the SAME
    /// Nexus file recorded (Library entry or UE4SS meta, mod and file id both matching). Another
    /// file of the mod is a different version, so it never lends its own. All found are saved in
    /// one write; a download with none is not looked up again this session.
    /// </summary>
    private void BackfillFileVersions(InstalledModsSnapshot installed)
    {
        var found = new List<(PendingDownloadItemViewModel Item, string Version)>();
        foreach (var item in PendingDownloads.Where(p => p.FileVersion is null && !p.IsCatalog && p.ModId > 0 && !_fileVersionBackfillTried.Contains((p.ModId, p.FileId))).ToList())
        {
            var version = installed.LibraryVersionForNexusFile(item.ModId, item.FileId);
            if (version is null)
            {
                foreach (var part in item.ActivatedItems.Where(p => p.Kind == PendingDownloadActivationKind.Ue4ssMod))
                {
                    var meta = _ue4ssModMetaStore.Load(part.FolderName);
                    if (meta.NexusModId == item.ModId && meta.NexusFileId == item.FileId && !string.IsNullOrWhiteSpace(meta.NexusFileVersion))
                    {
                        version = meta.NexusFileVersion;
                        break;
                    }
                }
            }

            if (version is null)
            {
                _fileVersionBackfillTried.Add((item.ModId, item.FileId));
            }
            else
            {
                found.Add((item, version));
            }
        }

        if (found.Count == 0)
        {
            return;
        }

        TrySaveDownloadsList(() => _pendingDownloadStore.SetFileVersions([.. found.Select(f => (f.Item.ModId, f.Item.FileId, f.Version))]));
        foreach (var (item, version) in found)
        {
            ShowFileVersion(item, version);
        }
    }

    // --- Discard older versions / Remove unused files ---

    /// <summary>Superseded rows whose file is still on disk, and those files' total size.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOlderVersionFiles), nameof(DiscardOlderVersionsLabel))]
    private int _olderVersionFileCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DiscardOlderVersionsLabel))]
    private long _olderVersionBytes;

    public bool HasOlderVersionFiles => OlderVersionFileCount > 0;

    public string DiscardOlderVersionsLabel => $"Discard older versions ({FileCount(OlderVersionFileCount)}, {BackupCleanup.FormatSize(OlderVersionBytes)})";

    /// <summary>Files in Pending_Downloads no download in the list points at.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnusedFiles), nameof(RemoveUnusedFilesLabel))]
    private int _unusedFileCount;

    public bool HasUnusedFiles => UnusedFileCount > 0;

    public string RemoveUnusedFilesLabel => $"Remove unused files ({UnusedFileCount})";

    private static string FileCount(int count) => count == 1 ? "1 file" : $"{count} files";

    /// <summary>Where a list entry or row says its file is: copied on the UI thread so the disk checks can run off it.</summary>
    private readonly record struct DownloadFile(int ModId, int FileId, string LocalFilePath, string FileName);

    private static DownloadFile FileOf(PendingDownloadEntry entry) => new(entry.ModId, entry.FileId, entry.LocalFilePath, entry.FileName);

    private static DownloadFile FileOf(PendingDownloadItemViewModel item) => new(item.ModId, item.FileId, item.LocalFilePath, item.FileName);

    private List<DownloadFile> ListedFiles() => [.. _pendingDownloadStore.Entries.Select(FileOf)];

    /// <summary>Bumped by each recount, so a slower earlier one never overwrites a newer one's numbers.</summary>
    private int _cleanupCountsGeneration;

    /// <summary>The latest recount of the two clean-up buttons (tests wait on it).</summary>
    internal Task CleanupCountsRefresh { get; private set; } = Task.CompletedTask;

    /// <summary>Recounts what the two clean-up buttons would delete, off the UI thread.</summary>
    private void RefreshCleanupCounts() => CleanupCountsRefresh = RefreshCleanupCountsAsync();

    private async Task RefreshCleanupCountsAsync()
    {
        var generation = ++_cleanupCountsGeneration;
        var listed = ListedFiles();
        var superseded = PendingDownloads.Where(p => p.IsSuperseded).Select(FileOf).ToList();
        var (olderCount, olderBytes, unusedCount) = await Task.Run(() =>
        {
            var older = DistinctFiles(OlderFilesToDelete(superseded, listed));
            return (older.Count, older.Sum(f => f.Length), FindUnusedFiles(listed).Count);
        });
        if (generation != _cleanupCountsGeneration)
        {
            return;
        }

        OlderVersionBytes = olderBytes;
        OlderVersionFileCount = olderCount;
        UnusedFileCount = unusedCount;
    }

    /// <summary>
    /// The entry's file on disk, or null when it is gone, can't be read, or isn't directly in
    /// Pending_Downloads (a moved install or an edited list can point anywhere: never deleted).
    /// </summary>
    private FileInfo? ExistingFile(DownloadFile download)
    {
        try
        {
            var file = new FileInfo(File.Exists(download.LocalFilePath) ? download.LocalFilePath : Path.Combine(_pendingDownloadsDirectory, download.FileName));
            return file.Exists && string.Equals(file.DirectoryName, Path.TrimEndingDirectorySeparator(Path.GetFullPath(_pendingDownloadsDirectory)), StringComparison.OrdinalIgnoreCase)
                ? file
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Full paths the entries point at (their own path and Pending_Downloads\FileName).</summary>
    private HashSet<string> UsedPaths(IEnumerable<DownloadFile> entries)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            TryAdd(entry.LocalFilePath);
            TryAdd(Path.Combine(_pendingDownloadsDirectory, entry.FileName));
        }

        return used;

        void TryAdd(string path)
        {
            try
            {
                used.Add(Path.GetFullPath(path));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
            }
        }
    }

    /// <summary>
    /// The files of these older downloads that can go: on disk in Pending_Downloads, not used by a
    /// download that stays in the list, and not a running download's. A download with no such
    /// file isn't in the result, so it is never dropped from the list either.
    /// </summary>
    private List<(DownloadFile Download, FileInfo File)> OlderFilesToDelete(IReadOnlyCollection<DownloadFile> older, IReadOnlyCollection<DownloadFile> listed)
    {
        var leaving = older.Select(d => (d.ModId, d.FileId)).ToHashSet();
        var usedByStaying = UsedPaths(listed.Where(d => !leaving.Contains((d.ModId, d.FileId))));
        return [.. older
            .Select(d => (Download: d, File: ExistingFile(d)))
            .Where(f => f.File is not null && !usedByStaying.Contains(f.File.FullName) && !IsActiveDownloadFile(f.File.FullName))
            .Select(f => (f.Download, f.File!))];
    }

    private static List<FileInfo> DistinctFiles(IEnumerable<(DownloadFile Download, FileInfo File)> files) =>
        [.. files.Select(f => f.File).DistinctBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// Files directly in Pending_Downloads that no download in the list points at. .part files are
    /// the sweep's (by age), and a running download's file isn't listed until it lands, so both
    /// are left out. Nothing at all while the list couldn't be read: every download would look unused.
    /// </summary>
    private List<FileInfo> FindUnusedFiles(IReadOnlyCollection<DownloadFile> listed)
    {
        if (_pendingDownloadStore.HasLoadProblem)
        {
            return [];
        }

        try
        {
            if (!Directory.Exists(_pendingDownloadsDirectory))
            {
                return [];
            }

            var used = UsedPaths(listed);
            return [.. new DirectoryInfo(_pendingDownloadsDirectory).EnumerateFiles("*", SearchOption.TopDirectoryOnly)
                .Where(f => !f.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                    && !used.Contains(f.FullName)
                    && !IsActiveDownloadFile(f.FullName))];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Deletes the files of downloads that a newer version of the same Nexus file has replaced
    /// (the rows reading "Older version"), and drops them from the list. Nexus's own update chain
    /// decides when it can be read: a superseded row that is a different file of the mod (another
    /// variant) stays. Asks first with the count and size; never touches installed mods.
    /// </summary>
    [RelayCommand]
    private async Task DiscardOlderVersionsAsync()
    {
        var installed = InstalledModsSnapshot.Take(_libraryRepository, _ue4ssNames);
        foreach (var item in PendingDownloads)
        {
            item.Refresh(installed);
        }

        var candidates = PendingDownloads
            .Select(item => (Item: item, Newer: NewerInstalledFileIds(item, installed)))
            .Where(c => c.Newer.Count > 0)
            .ToList();
        if (candidates.Count == 0)
        {
            PendingDownloadStatusMessage = "No older versions to discard.";
            RefreshCleanupCounts();
            return;
        }

        var older = new List<PendingDownloadItemViewModel>();
        foreach (var group in candidates.GroupBy(c => c.Item.ModId))
        {
            // No key or Nexus unreachable: "Older version" alone decides (the newer file sits in the same folder).
            var fileSet = await TryGetFileSetAsync(group.Key);
            older.AddRange(group
                .Where(c => fileSet is null || c.Newer.Any(newer => NexusFileLineage.AreVersionsOfOneFile(fileSet, c.Item.FileId, newer)))
                .Select(c => c.Item));
        }

        var olderFiles = older.Select(FileOf).ToList();
        var listed = ListedFiles();
        var asked = DistinctFiles(await Task.Run(() => OlderFilesToDelete(olderFiles, listed)));
        if (asked.Count == 0)
        {
            PendingDownloadStatusMessage = "No older versions to discard.";
            RefreshCleanupCounts();
            return;
        }

        var question = $"Delete {FileCount(asked.Count)} ({BackupCleanup.FormatSize(asked.Sum(f => f.Length))}) downloaded for older versions of mods you've since updated?\n\n"
            + "The newer versions stay installed. You can't put an older version back without downloading it again.";
        if (_dialogService?.Confirm(question, "Discard older versions", ThemedConfirmSeverity.Warning) != true)
        {
            PendingDownloadStatusMessage = "Nothing discarded.";
            return;
        }

        // A Reinstall that finished while Nexus was asked or the question was open can have made
        // one of them the installed version again, or the list changed: check again before deleting.
        installed = InstalledModsSnapshot.Take(_libraryRepository, _ue4ssNames);
        foreach (var item in PendingDownloads)
        {
            item.Refresh(installed);
        }

        var stillOlder = older.Where(item => PendingDownloads.Contains(item) && NewerInstalledFileIds(item, installed).Count > 0).Select(FileOf).ToList();
        listed = ListedFiles();
        var askedPaths = asked.Select(f => f.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var toDelete = (await Task.Run(() => OlderFilesToDelete(stillOlder, listed))).Where(f => askedPaths.Contains(f.File.FullName)).ToList();
        if (toDelete.Count == 0)
        {
            PendingDownloadStatusMessage = "Nothing discarded: those versions are installed again.";
            RefreshVisiblePendingDownloads();
            return;
        }

        var files = DistinctFiles(toDelete);
        var notDeleted = await DeleteFilesAsync(files);
        var deletedFiles = files.Where(f => !notDeleted.ContainsKey(f.FullName)).ToList();
        var size = BackupCleanup.FormatSize(deletedFiles.Sum(f => f.Length));
        var removed = toDelete.Where(f => !notDeleted.ContainsKey(f.File.FullName)).Select(f => (f.Download.ModId, f.Download.FileId)).ToList();
        var saveError = TrySaveDownloadsList(() => _pendingDownloadStore.RemoveMany(removed));
        ReloadPendingDownloads();
        PendingDownloadStatusMessage = (notDeleted.Count == 0
                ? $"Discarded {FileCount(deletedFiles.Count)} of older versions ({size})."
                : $"Discarded {FileCount(deletedFiles.Count)}. Couldn't delete {string.Join("; ", notDeleted.Values)}")
            + (saveError is null ? "" : $" Starlink couldn't save the downloads list ({saveError}).");
    }

    /// <summary>Deletes files no download in the list points at, after asking with the count and size.</summary>
    [RelayCommand]
    private async Task RemoveUnusedFilesAsync()
    {
        if (_pendingDownloadStore.HasLoadProblem)
        {
            PendingDownloadStatusMessage = "Starlink couldn't read the downloads list when it started, so it won't remove any files now. Restart Starlink and try again.";
            UnusedFileCount = 0;
            return;
        }

        var listed = ListedFiles();
        var unused = await Task.Run(() => FindUnusedFiles(listed));
        if (unused.Count == 0)
        {
            PendingDownloadStatusMessage = "No unused files in Pending_Downloads.";
            UnusedFileCount = 0;
            return;
        }

        var size = BackupCleanup.FormatSize(unused.Sum(f => f.Length));
        var names = string.Join("\n", unused.Take(10).Select(f => $"• {f.Name}")) + (unused.Count > 10 ? $"\n• …and {unused.Count - 10} more" : "");
        var question = $"Delete {FileCount(unused.Count)} ({size}) in Pending_Downloads that no download in this list points at?\n\n{names}\n\nNothing in your Library changes.";
        if (_dialogService?.Confirm(question, "Remove unused files", ThemedConfirmSeverity.Warning) != true)
        {
            PendingDownloadStatusMessage = "Nothing removed.";
            return;
        }

        var notDeleted = await DeleteFilesAsync(unused);
        RefreshCleanupCounts();
        await CleanupCountsRefresh;
        PendingDownloadStatusMessage = notDeleted.Count == 0
            ? $"Removed {FileCount(unused.Count)} ({size})."
            : $"Removed {FileCount(unused.Count - notDeleted.Count)}. Couldn't delete {string.Join("; ", notDeleted.Values)}";
    }

    /// <summary>Deletes the files off the UI thread; returns the ones that couldn't be deleted (full path → "name: reason").</summary>
    private static Task<Dictionary<string, string>> DeleteFilesAsync(IReadOnlyList<FileInfo> files) => Task.Run(() =>
    {
        var failed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            try
            {
                File.Delete(file.FullName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed[file.FullName] = $"{file.Name}: {ex.Message}";
            }
        }

        return failed;
    });
}
