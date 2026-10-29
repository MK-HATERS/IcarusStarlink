using System.IO;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.Catalog.Nexus;
using IcarusStarlink.Core.Nexus;
using IcarusStarlink.Core.Secrets;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.ViewModels;

/// <summary>Loading the detail tabs on first select: the Files list, Readme, Changes and its summary, a pak's contents and the thumbnail.</summary>
public sealed partial class LibraryItemViewModel
{
    /// <summary>Files/readme are only loaded once the user actually selects this item, not for every entry in the library up front.</summary>
    public void EnsureDetailsLoaded()
    {
        if (_detailsLoaded)
        {
            return;
        }

        try
        {
            // One walk of the folder, reused for the asset list, the readme lookup, and (below)
            // the .EXMOD read — the three single-arg forms would otherwise each re-walk the exact
            // same folder from scratch, paid on every single mod selection.
            var files = _repository.ListFolderFiles(FolderName);

            // Cleared, not appended (an audit finding): NotifyRepairedFromDisk and a retry after a
            // transient failure both re-enter here with the previous list still populated, which
            // doubled every row in the Files tab.
            AssetPaths.Clear();
            foreach (var path in _repository.ListAssetPaths(FolderName, files))
            {
                AssetPaths.Add(path);
            }

            ReadmeContent = _repository.ReadReadme(FolderName, files);
            LoadThumbnailIfPresent();
            if (ThumbnailImage is null && (NexusModId is not null || CatalogEntryId is not null))
            {
                // Fire-and-forget, same pattern LoadPakContentsCommand already uses below for the
                // opaque-pak case — this synchronous method can't await a network call, and a
                // missing/slow picture is cosmetic, never a reason to block the rest of the details
                // load.
                _ = LoadRemoteThumbnailAsync();
            }

            // An opaque .pak entry has no .EXMOD at all — nothing to format, and ExmodFolder.Read
            // would throw trying. Its own internal files aren't on disk under this mod's folder
            // either (just the bare .pak itself) — LoadPakContentsCommand fetches those separately,
            // via UnrealPak -List, an external process this synchronous method can't await.
            if (!IsOpaquePak)
            {
                // ReadPackageOnly, not Read: Read pulls EVERY one of the mod's binary assets into
                // memory (a real mod's .uasset/.ubulk content can be many MB) purely to reach the
                // .EXMOD's own JSON, which is the only thing the Changes text is built from.
                var package = ExmodFolder.ReadPackageOnly(_repository.GetFolderPath(FolderName), files);
                ChangesContent = ExmodChangesFormatter.Format(package, AssetPaths);
                _ = LoadChangesSummaryAsync(package, ExmodChangesFormatter.ReplacedGameFiles(AssetPaths));
            }
            else
            {
                _ = LoadPakContentsCommand.ExecuteAsync(null);
            }

            // Only mark this as done once it actually succeeded — a transient failure (folder
            // locked, deleted out from under the app) should let a later reselect retry rather
            // than permanently pin this mod's Files/Readme tabs empty for the rest of the run.
            // The opaque-pak listing above is deliberately NOT part of this gate — it's its own
            // independently retryable action (see LoadPakContentsAsync's own doc comment).
            _detailsLoaded = true;
        }
        catch (Exception ex)
        {
            // Same UI boundary as SaveMetadata/ImportPaths/DeleteSelected: this runs from
            // OnSelectedItemChanged, off a binding-driven selection change, so an unhandled
            // exception here would crash the app instead of showing a status message.
            _reportStatus($"Couldn't load mod details: {ex.Message}");
        }
    }

    /// <summary>
    /// Compares the mod with current game data off the UI thread (a big mod touches tables of
    /// several MB) and boils the result down to a few readable lines.
    /// </summary>
    private async Task LoadChangesSummaryAsync(PakIO.Exmod.ExmodPackage package, IReadOnlyList<string> replacedGameFiles)
    {
        if (DataFolder is not { } dataFolder || !Directory.Exists(dataFolder))
        {
            ChangesSummary = "Run \"Refresh game data\" in Settings to see a summary of what this mod changes.";
            return;
        }

        // The row can be reset (edited, reloaded) while this runs; a stale result mustn't land on it.
        var run = ++_changesSummaryRun;
        ChangesSummary = "Working out what this mod changes…";
        string summary;
        try
        {
            var lines = await Task.Run(() => PakIO.Exmod.ModChangeSummarizer.SummarizePackage(package, dataFolder));
            var replaced = ExmodChangesFormatter.ReplacedGameFiles(replacedGameFiles) is { Count: > 0 } files
                ? $"Replaces {(files.Count == 1 ? "1 game file" : $"{files.Count} game files")}: {string.Join(", ", files.Take(3))}{(files.Count > 3 ? ", …" : "")}"
                : null;
            summary = lines.Count == 0
                ? (replaced is null ? "Nothing differs from current game data." : $"No data-table changes. {replaced}.")
                : string.Join(Environment.NewLine, replaced is null ? lines : [.. lines, replaced]);
        }
        catch (Exception ex)
        {
            summary = $"Couldn't summarise this mod: {ex.Message}";
        }

        if (run == _changesSummaryRun)
        {
            ChangesSummary = summary;
        }
    }

    private int _changesSummaryRun;

    /// <summary>
    /// Lists an opaque .pak's own internal files via UnrealPak -List — separate from
    /// EnsureDetailsLoaded's own once-only gate (_detailsLoaded) since this needs an external
    /// process and a configured UnrealPak.exe path, either of which can fail independently of
    /// whether the rest of this mod's (empty, for a pak) details loaded fine. Exposed as a real
    /// command, not just called internally, so the Files tab can offer a manual retry after the
    /// user goes and sets UnrealPak.exe's path in Settings.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task LoadPakContentsAsync()
    {
        var unrealPakExePath = _settingsService.Current.UnrealPakExePath;
        if (string.IsNullOrWhiteSpace(unrealPakExePath))
        {
            PakListingStatus = "Set UnrealPak.exe's path in Settings to list this pak's internal files.";
            return;
        }

        // Every pak of a bundle (one download can hold several), not just the first one found.
        string[] pakFilePaths;
        try
        {
            pakFilePaths = Directory.GetFiles(_repository.GetFolderPath(FolderName), "*.pak");
        }
        catch (Exception ex)
        {
            PakListingStatus = $"Couldn't find this mod's own .pak file: {ex.Message}";
            return;
        }

        if (pakFilePaths.Length == 0)
        {
            PakListingStatus = "Couldn't find this mod's own .pak file.";
            return;
        }

        // The row can be reset (Update after a refresh) while UnrealPak runs: a late listing mustn't
        // fill the cleared Files list again.
        var run = ++_pakContentsRun;
        try
        {
            PakListingStatus = "Loading pak contents…";
            var paths = new List<string>();
            foreach (var pakFilePath in pakFilePaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                paths.AddRange(await _unrealPakService.ListPakContentsAsync(unrealPakExePath, pakFilePath));
            }

            if (run != _pakContentsRun)
            {
                return;
            }

            AssetPaths.Clear();
            foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                AssetPaths.Add(path);
            }

            PakListingStatus = null;
        }
        catch (Exception ex)
        {
            if (run == _pakContentsRun)
            {
                PakListingStatus = $"Couldn't list this pak's contents: {ex.Message}";
            }
        }
    }

    private int _pakContentsRun;

    /// <summary>
    /// Best-effort only: a missing or corrupt thumbnail is cosmetic, not a reason to fail the
    /// whole details load the way a Files/Readme read failure would — so failures here are
    /// swallowed rather than routed through _reportStatus.
    /// </summary>
    private void LoadThumbnailIfPresent()
    {
        var thumbnailPath = AssetPaths.FirstOrDefault(p =>
            Path.GetFileNameWithoutExtension(p).Equals("ImageOnly", StringComparison.OrdinalIgnoreCase)
            && ImageExtensions.Contains(Path.GetExtension(p)));

        if (thumbnailPath is null)
        {
            return;
        }

        try
        {
            ThumbnailImage = TryDecodeImage(_repository.ReadAssetContent(FolderName, thumbnailPath));
        }
        catch (Exception)
        {
            // The read itself can fail (locked/vanished file) separately from the decode — both
            // are cosmetic here, per this method's own contract.
        }
    }

    /// <summary>
    /// Only runs when no local bundled preview exists (LoadThumbnailIfPresent already found
    /// nothing) and this mod is linked to a real Nexus mod or Database catalog entry — fetches
    /// that source's own real picture instead of leaving the generic placeholder showing for a mod
    /// that genuinely has a real image out there. Cached to disk by FolderName so a later reselect
    /// (or app restart) never re-fetches the same picture twice; best-effort throughout, same
    /// cosmetic-only contract as LoadThumbnailIfPresent — a missing key, an unreachable network, or
    /// a mod removed from the catalog since just means the placeholder stays showing.
    /// </summary>
    private async Task LoadRemoteThumbnailAsync()
    {
        try
        {
            var cachePath = Path.Combine(_thumbnailCacheDirectory, $"{FolderName}.img");
            if (File.Exists(cachePath))
            {
                ThumbnailImage = TryDecodeImage(await File.ReadAllBytesAsync(cachePath));
                return;
            }

            string? imageUrl = null;
            if (NexusModId is { } modId)
            {
                var apiKey = _credentialStore.Read(CredentialTargets.NexusApiKey);
                if (apiKey is not null)
                {
                    var info = await _nexusApiClient.GetModInfoAsync(apiKey, NxmHandlerCommand.IcarusDomain, modId);
                    imageUrl = info?.PictureUrl;
                }
            }
            else if (CatalogEntryId is { } catalogId)
            {
                var catalog = await _getOrFetchCatalog();
                imageUrl = catalog.FirstOrDefault(e => e.Id == catalogId)?.ImageUrl;
            }

            if (string.IsNullOrWhiteSpace(imageUrl))
            {
                return;
            }

            var bytes = await _httpClient.GetByteArrayAsync(imageUrl);
            var decoded = TryDecodeImage(bytes);
            if (decoded is null)
            {
                return;
            }

            ThumbnailImage = decoded;
            Directory.CreateDirectory(_thumbnailCacheDirectory);
            await File.WriteAllBytesAsync(cachePath, bytes);
        }
        catch (Exception)
        {
            // Cosmetic-only, per this method's own doc comment.
        }
    }
}
