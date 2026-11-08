using System.IO;
using IcarusStarlink.App.Services;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Nexus;
using IcarusStarlink.Core.Ue4ss;

namespace IcarusStarlink.App.ViewModels;

// Library's import of a mod that is already in the Library, when the user says Replace it.
public sealed partial class DownloadsViewModel
{
    /// <summary>
    /// Imports a local mod in place of its installed copy: a .pak file, or a folder (the user's own
    /// EXMOD folder, a copy, or an unpacked archive) classified like any import. Goes through
    /// ModReplacementService (backed up first, pin/notes/rename and remembered fixes carried over,
    /// put back on a failure), and keeps the old copy's source and Nexus link, which a local file
    /// doesn't carry. <paramref name="originalPath"/> names the import, as Library's own import names it.
    /// </summary>
    internal async Task<ModReplacementResult> ReplaceWithLocalImportAsync(
        string stagedPath, string originalPath, LibraryEntry? oldEntry, string? oldUe4ssName, Action<ClassifiedImport>? classified = null)
    {
        var modName = oldEntry?.Name ?? oldUe4ssName ?? Path.GetFileNameWithoutExtension(originalPath);
        var previous = new PreviousCopies(oldEntry is null ? [] : [oldEntry.FolderName], oldUe4ssName is null ? [] : [oldUe4ssName]);
        if (RefuseWhileGameHoldsMods(previous, modName) is { } refused)
        {
            // A failure, not a cancel: the player didn't say no, and the reason (close the game) must show.
            return ModReplacementResult.Failure(refused, error: null, rolledBack: false);
        }

        var oldUe4ssMeta = oldUe4ssName is null ? null : _ue4ssModMetaStore.Load(oldUe4ssName);
        var source = oldEntry?.Source ?? (oldUe4ssMeta?.NexusModId is null ? null : PendingDownloadEntry.NexusSource);
        var nexusModId = oldEntry?.NexusModId ?? oldUe4ssMeta?.NexusModId;
        var catalogEntryId = oldEntry?.CatalogEntryId;

        var result = await RunInstallStepAsync(() => _modReplacement.ReplaceAsync(
            new ModReplacementRequest(modName, previous.LibraryFolders, previous.Ue4ssFolders),
            async (removeOld, writes) =>
            {
                if (File.Exists(stagedPath) && string.Equals(Path.GetExtension(stagedPath), ".pak", StringComparison.OrdinalIgnoreCase))
                {
                    if (ExtractedModClassifier.WhyNotLoadable(stagedPath) is { } notLoadable)
                    {
                        throw new InvalidOperationException(notLoadable);
                    }

                    RemoveOldOrExplain(removeOld);
                    var entry = await _prebuiltPakImporter.ImportAsync(
                        stagedPath, _dataFolder, _settingsService.Current.UnrealPakExePath, source, nexusModId, catalogEntryId);
                    writes.Wrote(entry.FolderName, PendingDownloadActivationKind.Library);
                    classified?.Invoke(new ClassifiedImport(entry.Name, entry.FolderName, PendingDownloadActivationKind.Library, entry.IsOpaquePak,
                        [new PendingDownloadActivatedItem(entry.FolderName, PendingDownloadActivationKind.Library)], [], entry.ImportWarnings));
                    return [new PendingDownloadActivatedItem(entry.FolderName, PendingDownloadActivationKind.Library)];
                }

                var imported = await ExtractedModClassifier.ClassifyAndImport(
                    stagedPath, originalPath, _libraryRepository, _ue4ssModRepository,
                    _prebuiltPakImporter, _dataFolder, _settingsService.Current.UnrealPakExePath, source: source, nexusModId: nexusModId,
                    ue4ssNamesAlreadyInstalledInGame: _ue4ssNames.InGameExcept(previous.Ue4ssNames), catalogEntryId: catalogEntryId,
                    chooseArchive: ChooseArchive, beforeFirstWrite: () => RemoveOldOrExplain(removeOld), writes: writes);
                classified?.Invoke(imported);
                return imported.Items;
            }));

        if (result.Succeeded)
        {
            KeepNexusLink(result.Items, oldEntry, oldUe4ssMeta);
        }

        return result;
    }

    /// <summary>
    /// The Nexus file and versions the old copy recorded, onto the new one (a local file carries
    /// none of it). Only a one-for-one swap of each half. Best effort: the mod is in either way.
    /// </summary>
    private void KeepNexusLink(IReadOnlyList<PendingDownloadActivatedItem> items, LibraryEntry? oldEntry, Ue4ssModMeta? oldUe4ssMeta)
    {
        try
        {
            var libraryItems = items.Where(i => i.Kind == PendingDownloadActivationKind.Library).ToList();
            if (oldEntry?.NexusModId is not null && libraryItems.Count == 1)
            {
                var folder = libraryItems[0].FolderName;
                if (oldEntry.NexusFileId is { } fileId)
                {
                    _libraryRepository.SetNexusFile(folder, fileId, oldEntry.NexusFileVersion);
                }

                // An opaque pak shows the Nexus name, author and description; an EXMOD keeps its own.
                var showsNexusText = oldEntry.IsOpaquePak || oldEntry.ConvertedFromPrebuiltPak;
                _libraryRepository.SetNexusMetadata(
                    folder,
                    showsNexusText && oldEntry.DisplayNameOverride is null ? oldEntry.Name : null,
                    showsNexusText ? oldEntry.Author : null,
                    showsNexusText ? oldEntry.Description : null,
                    oldEntry.NexusVersion);
            }

            var ue4ssItems = items.Where(i => i.Kind == PendingDownloadActivationKind.Ue4ssMod).ToList();
            if (oldUe4ssMeta?.NexusModId is not null && ue4ssItems.Count == 1)
            {
                var name = ue4ssItems[0].FolderName;
                var meta = _ue4ssModMetaStore.Load(name);
                meta.NexusModId ??= oldUe4ssMeta.NexusModId;
                meta.NexusVersion ??= oldUe4ssMeta.NexusVersion;
                meta.NexusFileId ??= oldUe4ssMeta.NexusFileId;
                meta.NexusFileVersion ??= oldUe4ssMeta.NexusFileVersion;
                meta.NexusName ??= oldUe4ssMeta.NexusName;
                _ue4ssModMetaStore.Save(name, meta);
            }
        }
        catch (Exception ex)
        {
            _activityLog.Log($"Couldn't keep the Nexus link on the new copy: {ex.Message} Use Link to Nexus ID to set it again.", ActivityEntryKind.Warning);
        }
    }
}
