using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.Utilities;
using IcarusStarlink.App.Views;
using IcarusStarlink.Catalog;
using IcarusStarlink.Catalog.Daedalus;
using IcarusStarlink.Catalog.GitHub;
using IcarusStarlink.Catalog.Jimk72;
using IcarusStarlink.Catalog.Nexus;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Catalog;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Nexus;
using IcarusStarlink.Core.Secrets;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.Core.Ue4ss;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Import;

namespace IcarusStarlink.App.ViewModels;

// Activate / Reinstall: installing a download, replacing earlier copies, manual install and requirements.
public sealed partial class DownloadsViewModel
{
    /// <summary>
    /// MO2-style: never removes the entry from Pending Downloads (only Discard does that) — a
    /// second click on an already-installed entry is a Reinstall, which replaces whatever its last
    /// activation produced (so re-Activating doesn't leave a "_2"-suffixed duplicate next to the one
    /// it's replacing). The swap goes through ModReplacementService: backed up first, an edited copy
    /// only after asking, pin/notes/rename carried over, remembered fixes made again, an enabled
    /// UE4SS mod replaced in the game too, and the old copy put back if the new one fails.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private Task ActivatePendingDownloadAsync(PendingDownloadItemViewModel? item) => ActivateAsync(item);

    /// <summary>
    /// The Activate/Reinstall behind the button, returning how it went (Library's Reinstall original
    /// reports this rather than assuming success). pristine: put the download back exactly as it
    /// came — no rename carried over, no fixes made again, the remembered fix choices forgotten.
    /// </summary>
    public async Task<ModReplacementResult> ActivateAsync(PendingDownloadItemViewModel? item, bool pristine = false)
    {
        if (item is null)
        {
            return ModReplacementResult.Cancel("Nothing to install.");
        }

        try
        {
            var previous = FindPreviousCopies(item);

            var source = item.IsCatalog ? PendingDownloadEntry.DatabaseSource : PendingDownloadEntry.NexusSource;
            int? nexusModId = item.IsCatalog ? null : item.ModId;

            // Resolved at use (an audit finding): the stored path is absolute, so moving the app
            // folder — the "unzip anywhere" portable install — broke every pending entry; the file
            // name is always under this app's own Pending_Downloads folder.
            var localFilePath = ResolveLocalFilePath(item);
            (string Status, IReadOnlyList<string> Requirements) installed = ("", []);
            var replaced = await _modReplacement.ReplaceAsync(
                new ModReplacementRequest(item.DisplayName, previous.LibraryFolders, previous.Ue4ssFolders, pristine),
                async removeOld =>
                {
                    var (items, status, requirementLines) = await InstallDownloadAsync(item, localFilePath, source, nexusModId, previous, removeOld);
                    installed = (status, requirementLines);
                    return items;
                });
            var statusMessage = installed.Status;
            var requirements = installed.Requirements;

            if (!replaced.Succeeded)
            {
                // Same UI boundary as Library's own ImportFolder/ImportFile — a malformed archive or
                // a locked file shows a status message instead of crashing the app. Deliberately
                // leaves the entry in Pending Downloads (and the file on disk) on failure, so
                // nothing is lost and the user can retry or Discard explicitly.
                PendingDownloadStatusMessage = replaced.Cancelled ? replaced.Message : $"Import failed: {replaced.Message}";
                WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
                if (replaced.Cancelled || replaced.Error is null)
                {
                    return replaced;
                }

                // Mod Organizer's answer to a download it can't sort out: let the user say what's what.
                return await InstallManuallyAfterFailureAsync(item, replaced.Error.Message, pristine) ?? replaced;
            }

            var activatedItems = replaced.Items;
            if (!item.IsCatalog)
            {
                await RecordNexusFileAsync(item.ModId, item.FileId, activatedItems);
            }

            _pendingDownloadStore.SetActivation(item.ModId, item.FileId, activatedItems, requirements);
            item.SetActivated(activatedItems, requirements);
            RefreshVisiblePendingDownloads();
            PendingDownloadStatusMessage = statusMessage + replaced.Summary;
            WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());

            // A mod organizer resolves a dependency at download time, with a question — not a
            // warning buried in a status line after the fact.
            await OfferMissingRequirementsAsync(item);
            return replaced;
        }
        catch (Exception ex)
        {
            // What's left outside the replacement itself (recording the activation, the
            // requirement offer) — the mod is in; say what went wrong rather than crash.
            PendingDownloadStatusMessage = $"Import failed: {ex.Message}";
            return ModReplacementResult.Failure(ex.Message, ex, rolledBack: false);
        }
    }

    /// <summary>
    /// Imports the download itself (the install step ModReplacementService runs): a .pak straight
    /// through the pak importer, anything else unpacked and classified. Calls removeOld right
    /// before the first write. Returns what landed, the status line and the README requirements.
    /// </summary>
    private async Task<(IReadOnlyList<PendingDownloadActivatedItem> Items, string Status, IReadOnlyList<string> Requirements)> InstallDownloadAsync(
        PendingDownloadItemViewModel item, string localFilePath, string source, int? nexusModId, PreviousCopies previous, Action removeOld)
    {
        if (string.Equals(Path.GetExtension(localFilePath), ".pak", StringComparison.OrdinalIgnoreCase))
        {
            removeOld();
            var entry = await _prebuiltPakImporter.ImportAsync(
                localFilePath, _dataFolder, _settingsService.Current.UnrealPakExePath, source: source, nexusModId: nexusModId,
                catalogEntryId: item.CatalogEntryId, name: item.IsCatalog ? item.DisplayName : null);
            if (nexusModId is { } && (entry.IsOpaquePak || entry.ConvertedFromPrebuiltPak))
            {
                // A converted entry's own Name/Author are still just placeholders (the pak's
                // filename, "Unknown") — ConvertedFromPrebuiltPak is what lets
                // FolderLibraryRepository.SetNexusMetadata's cached-entry update (and ToEntry's
                // own NexusName/NexusAuthor fallback, on the next rescan) actually apply to it,
                // unlike an ordinary EXMOD import.
                await RecordNexusMetadataAsync(entry.FolderName, item.ModId);
            }

            var statusMessage = $"Imported '{entry.Name}' into your Library.";
            foreach (var warning in entry.ImportWarnings)
            {
                _activityLog.Log($"{entry.Name}: {warning}", ActivityEntryKind.Warning);
            }

            statusMessage += ExtractedModClassifier.WarningSummary(entry.ImportWarnings);
            _activityLog.Log($"Activated pending download '{entry.Name}'.", ActivityEntryKind.Success);
            return ([new PendingDownloadActivatedItem(entry.FolderName, PendingDownloadActivationKind.Library)], statusMessage, []);
        }

        // A real Nexus download's own extension can't be trusted (a mod author might
        // upload a zip, rar, or 7z, and it could contain an EXMOD-shaped mod, a prebuilt
        // .pak, or a UE4SS mod) — unlike every other import path in this app, there's no
        // dialog filter or button choice telling us which kind this is, so it has to be
        // extracted first and classified from its actual content.
        var tempExtractDirectory = Path.Combine(Path.GetTempPath(), $"IcarusStarlink_Activate_{Guid.NewGuid():N}");
        try
        {
            AnyArchiveExtractor.ExtractToDirectory(localFilePath, tempExtractDirectory);
            var (classifiedStatus, classifiedItems, classifiedRequirements) =
                await ClassifyAndImportExtractedModAsync(tempExtractDirectory, item.FileName, source, nexusModId, item.CatalogEntryId, previous, removeOld);
            return (classifiedItems, classifiedStatus, classifiedRequirements);
        }
        finally
        {
            try
            {
                Directory.Delete(tempExtractDirectory, recursive: true);
            }
            catch (Exception)
            {
                // Best-effort scratch cleanup — a locked file here (e.g. antivirus mid-scan)
                // shouldn't turn a successful import into a reported failure.
            }
        }
    }

    /// <summary>What a (re)install of this download replaces: Library folders and UE4SS folders.</summary>
    private sealed record PreviousCopies(IReadOnlyList<string> LibraryFolders, IReadOnlyList<string> Ue4ssFolders)
    {
        /// <summary>The UE4SS folder names this download owned — the new install may reuse them even while the game still holds the old copy.</summary>
        public IReadOnlySet<string> Ue4ssNames { get; } = new HashSet<string>(Ue4ssFolders, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Before (re)installing a download: what this row installed last time, or — the first time —
    /// any copy of the same Nexus mod / Database entry installed some other way, so an update
    /// replaces the mod instead of adding a "_2" copy. Only finds them; ModReplacementService removes
    /// them, as late as possible (after any "which one?" question, so cancelling it changes nothing).
    /// </summary>
    private PreviousCopies FindPreviousCopies(PendingDownloadItemViewModel item)
    {
        if (item.IsInstalled)
        {
            // The UE4SS folder names this download owned may still be INSTALLED in the game's Mods
            // folder (an audit of a real report: re-staging saw the name "in use" and landed as a
            // "_2" copy). Re-staging under the same name is the whole point of a reinstall.
            return new PreviousCopies(
                [.. item.ActivatedItems.Where(a => a.Kind == PendingDownloadActivationKind.Library).Select(a => a.FolderName)],
                [.. item.ActivatedItems.Where(a => a.Kind == PendingDownloadActivationKind.Ue4ssMod).Select(a => a.FolderName)]);
        }

        // Never activated through THIS row (a first download of a new file for this mod — e.g.
        // "Update" on a mod installed some other way: a manual import, a classic-IMM migration),
        // but entries linked to the same Nexus mod / catalog entry can still exist. Without
        // replacing them, "Update" silently added a "_2" copy and left the original untouched
        // (a real bug found live: "I download the update but it's not updating the mod").
        var library = item.IsCatalog
            ? _libraryRepository.GetAll().Where(e => string.Equals(e.CatalogEntryId, item.CatalogEntryId, StringComparison.OrdinalIgnoreCase))
            : _libraryRepository.GetAll().Where(e => e.NexusModId == item.ModId);
        var ue4ss = item.IsCatalog
            ? []
            // Enabled ones too: an enabled UE4SS mod lives in the game's Mods folder, not staging.
            : _ue4ssModRepository.GetAll().Concat(Ue4ssNamesInGame()).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(folderName => _ue4ssModMetaStore.Load(folderName).NexusModId == item.ModId);
        return new PreviousCopies([.. library.Select(e => e.FolderName)], [.. ue4ss]);
    }

    /// <summary>
    /// Figures out what an already-extracted download actually is and imports it the right way,
    /// via the shared ExtractedModClassifier (LibraryViewModel's own manual "Import archive…" uses
    /// the identical detection) — then layers this page's own Nexus-specific follow-up on top: an
    /// opaque pak or a UE4SS mod carries no embedded name/author of its own, so both get a
    /// best-effort real-name/version lookup from the Nexus mod ID this download already came with,
    /// and every kind fires the same reload/activity-log side effects a manual import doesn't need
    /// (Library's UE4SS tab has its own reload path, never wired to this pipeline before this).
    /// </summary>
    private async Task<(string StatusMessage, IReadOnlyList<PendingDownloadActivatedItem> Items, IReadOnlyList<string> Requirements)> ClassifyAndImportExtractedModAsync(
        string extractedDirectory, string originalFileName, string source, int? nexusModId, string? catalogEntryId, PreviousCopies previous,
        Action removeOld)
    {
        // Only meaningful if the game path is actually configured — with nothing to check against,
        // this degrades to the same "no collision avoidance" behavior importing already had. This
        // download's OWN previously-staged names are allowed back (a reinstall must land under the
        // same name even while the game folder still holds the earlier copy).
        var installedUe4ssNames = InstalledUe4ssNamesExcept(previous.Ue4ssNames);

        var classified = await ExtractedModClassifier.ClassifyAndImport(
            extractedDirectory, originalFileName, _libraryRepository, _ue4ssModRepository,
            _prebuiltPakImporter, _dataFolder, _settingsService.Current.UnrealPakExePath, source: source, nexusModId: nexusModId,
            ue4ssNamesAlreadyInstalledInGame: installedUe4ssNames, catalogEntryId: catalogEntryId, chooseArchive: ChooseArchive,
            beforeFirstWrite: removeOld);

        var landed = await FollowUpImportedAsync(classified.Items, nexusModId);

        var statusMessage = landed.Count == 1
            ? $"Activated {landed[0]}."
            : $"Activated {landed.Count} parts: {string.Join(", ", landed)}.";
        statusMessage += DescribeRequirements(classified.Requirements);
        foreach (var warning in classified.Warnings)
        {
            _activityLog.Log($"{originalFileName}: {warning}", ActivityEntryKind.Warning);
        }

        statusMessage += ExtractedModClassifier.WarningSummary(classified.Warnings);

        WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
        return (statusMessage, classified.Items, classified.Requirements);
    }

    /// <summary>UE4SS folder names already in the game's Mods folder (so a new staging avoids them), minus this download's own earlier names.</summary>
    private List<string>? InstalledUe4ssNamesExcept(IReadOnlySet<string>? ownUe4ssNamesToReuse)
    {
        // Only meaningful if the game path is actually configured — with nothing to check against,
        // this degrades to the same "no collision avoidance" behavior importing already had.
        var icarusContentPath = _settingsService.Current.IcarusContentPath;
        return !string.IsNullOrWhiteSpace(icarusContentPath)
            ? _ue4ssModRepository.ListInstalledInGame(Ue4ssGamePaths.ResolveModsFolder(icarusContentPath))
                .Where(name => ownUe4ssNamesToReuse is null || !ownUe4ssNamesToReuse.Contains(name))
                .ToList()
            : null;
    }

    /// <summary>After a download's mods are in: Nexus names/versions recorded on each, logged. Returns one "'Name' (Library)" phrase per mod.</summary>
    private async Task<List<string>> FollowUpImportedAsync(IReadOnlyList<PendingDownloadActivatedItem> items, int? nexusModId)
    {
        var landed = new List<string>();
        foreach (var produced in items)
        {
            if (produced.Kind == PendingDownloadActivationKind.Library)
            {
                // For EVERY Library kind, not just an opaque/converted pak (the original gating — a real
                // bug found live): an ordinary EXMODZ keeps its author-declared Name/Author (the
                // repository guards those), but still needs the Nexus VERSION recorded, or the update
                // check compares the author's own "1.0" against Nexus's "1" and shows "Update
                // available" forever right after activating. See UpdateVersionComparer.
                if (nexusModId is { } libraryModId)
                {
                    await RecordNexusMetadataAsync(produced.FolderName, libraryModId);
                }

                var entryName = _libraryRepository.GetAll().FirstOrDefault(e => e.FolderName == produced.FolderName)?.Name ?? produced.FolderName;
                landed.Add($"'{entryName}' (Library)");
                _activityLog.Log($"Activated pending download '{entryName}'.", ActivityEntryKind.Success);
            }
            else
            {
                if (nexusModId is { } ue4ssModId)
                {
                    await EnrichUe4ssModFromNexusAsync(produced.FolderName, ue4ssModId);
                }

                landed.Add($"'{produced.FolderName}' (UE4SS mod — tick it in the Library list, then Apply UE4SS changes)");
                _activityLog.Log($"Activated pending download '{produced.FolderName}' as a UE4SS mod.", ActivityEntryKind.Success);
            }
        }

        return landed;
    }

    /// <summary>The manual-install window: (download folder, mod name, why automatic failed, guesses) → the plan, or null when cancelled. Tests swap in a fake.</summary>
    internal Func<string, string, string, IEnumerable<ManualInstallItem>, IReadOnlyList<ManualInstallItem>?> ManualInstall { get; set; } = Views.ManualInstallWindow.Ask;

    /// <summary>
    /// Mod Organizer's "manual install", offered only when Activate couldn't sort a download out:
    /// unpack it, let the user choose what to keep and what each piece is (starting from
    /// Starlink's guesses), then install exactly that — with the same earlier-copy replacement,
    /// Nexus follow-up and file tracking as Activate. Cancelling leaves the failure message.
    /// Returns how the manual install went, or null when the user cancelled it.
    /// </summary>
    private async Task<ModReplacementResult?> InstallManuallyAfterFailureAsync(PendingDownloadItemViewModel item, string reason, bool pristine)
    {
        string? scratch = null;
        try
        {
            scratch = ManualInstaller.UnpackForManualInstall(ResolveLocalFilePath(item));
            if (ManualInstall(scratch, item.DisplayName, reason, ManualInstaller.Suggest(scratch)) is not { Count: > 0 } plan)
            {
                return null; // the "Import failed: …" message stays
            }

            var previous = FindPreviousCopies(item);
            var source = item.IsCatalog ? PendingDownloadEntry.DatabaseSource : PendingDownloadEntry.NexusSource;
            int? nexusModId = item.IsCatalog ? null : item.ModId;
            var landed = new List<string>();
            // The edited-copy question was already answered by the attempt that failed.
            var replaced = await _modReplacement.ReplaceAsync(
                new ModReplacementRequest(item.DisplayName, previous.LibraryFolders, previous.Ue4ssFolders, pristine, AskBeforeReplacingEdits: false),
                async removeOld =>
                {
                    var namesInGame = InstalledUe4ssNamesExcept(previous.Ue4ssNames);
                    removeOld();
                    var installed = await ManualInstaller.InstallAsync(
                        plan, _libraryRepository, _ue4ssModRepository, _prebuiltPakImporter, _dataFolder, _settingsService.Current.UnrealPakExePath,
                        source, nexusModId, item.CatalogEntryId, namesInGame);
                    landed = await FollowUpImportedAsync(installed, nexusModId);
                    return installed;
                });
            WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
            if (!replaced.Succeeded)
            {
                PendingDownloadStatusMessage = replaced.Cancelled ? replaced.Message : $"Manual install failed: {replaced.Message}";
                return replaced;
            }

            if (!item.IsCatalog)
            {
                await RecordNexusFileAsync(item.ModId, item.FileId, replaced.Items);
            }

            _pendingDownloadStore.SetActivation(item.ModId, item.FileId, replaced.Items, []);
            item.SetActivated(replaced.Items, []);
            RefreshVisiblePendingDownloads();
            PendingDownloadStatusMessage = $"Installed manually: {string.Join(", ", landed)}.{replaced.Summary}";
            return replaced;
        }
        catch (Exception ex)
        {
            PendingDownloadStatusMessage = $"Manual install failed: {ex.Message}";
            return ModReplacementResult.Failure(ex.Message, ex, rolledBack: false);
        }
        finally
        {
            ManualInstaller.TryDelete(scratch);
        }
    }

    /// <summary>
    /// " Needs: ArcFission Library 1.2.0 or newer — not installed (search Nexus for it); UE4SS — loader."
    /// A best-effort match of each README requirement against what's staged/installed (see
    /// ModReadmeRequirements) — the user's actual complaint was not being told which OTHER
    /// download a mod depended on. Logged as a warning too, so it survives leaving the page.
    /// </summary>
    private string DescribeRequirements(IReadOnlyList<string> requirements)
    {
        if (requirements.Count == 0)
        {
            return "";
        }

        // Enabled UE4SS mods sit in the game's Mods folder, not staging — both count as installed.
        var installedNames = _ue4ssModRepository.GetAll().Concat(Ue4ssNamesInGame()).Concat(_libraryRepository.GetAll().Select(e => e.Name)).ToList();
        var parts = new List<string>();
        var missing = new List<string>();
        foreach (var requirement in requirements.Where(r => !string.IsNullOrWhiteSpace(r)))
        {
            if (ModReadmeRequirements.IsLoaderRequirement(requirement))
            {
                parts.Add($"{requirement} (the UE4SS loader — Settings → UE4SS)");
            }
            else if (ModReadmeRequirements.IsModManagerRequirement(requirement))
            {
                parts.Add($"{requirement} ✓");
            }
            else if (ModReadmeRequirements.IsSatisfiedBy(requirement, installedNames))
            {
                parts.Add($"{requirement} ✓");
            }
            else
            {
                parts.Add($"{requirement} — NOT installed");
                missing.Add(ModReadmeRequirements.ExtractName(requirement));
            }
        }

        if (missing.Count > 0)
        {
            _activityLog.Log(
                $"This mod needs other downloads you don't have yet: {string.Join(", ", missing)} — find them on the Database page's Nexus tab.",
                ActivityEntryKind.Warning);
        }

        return $" Needs: {string.Join("; ", parts)}.";
    }

    /// <summary>Asks which of several alternative archives in one download to install (tests swap in a fake).</summary>
    internal Func<string, IReadOnlyList<ArchiveChoice>, int?> ChooseArchive { get; set; } = Views.ChooseArchiveDialog.Ask;

    /// <summary>
    /// The follow-through on a README requirement the activation flagged as NOT installed: jump to
    /// the Nexus page searching for it by name. The first missing one is searched; the rest are
    /// named in the status line so the user knows there are more.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task GetRequirementAsync(PendingDownloadItemViewModel? item)
    {
        if (item is null || item.MissingRequirementNames.Count == 0)
        {
            return;
        }

        if (!await OfferMissingRequirementsAsync(item))
        {
            // No dialog service, or nothing could be resolved: fall back to a Nexus search by name.
            var missing = item.MissingRequirementNames;
            WeakReferenceMessenger.Default.Send(new SearchNexusMessage(missing[0]));
            PendingDownloadStatusMessage = missing.Count == 1
                ? $"Searching Nexus for '{missing[0]}'."
                : $"Searching Nexus for '{missing[0]}' — this download also needs: {string.Join(", ", missing.Skip(1))}.";
        }
    }

    /// <summary>
    /// For every README requirement nothing installed satisfies: look it up on Nexus by name and
    /// ask "'Wireless Power' needs ArcFission Library 1.2.0 or newer. Download 'ArcFission Library'
    /// now?" — Yes runs the normal download-and-activate pipeline for it. Returns true when at least
    /// one requirement was offered (downloaded or declined), false when nothing could be offered at
    /// all (no dialog service, no API key, or no Nexus match for any name) so the caller can fall
    /// back to a plain search. Never throws: a Nexus hiccup just means no offer for that name.
    /// </summary>
    private async Task<bool> OfferMissingRequirementsAsync(PendingDownloadItemViewModel item)
    {
        if (_dialogService is null)
        {
            return false;
        }

        var apiKey = _credentialStore.Read(CredentialTargets.NexusApiKey);
        if (apiKey is null)
        {
            return false;
        }

        var offered = false;
        foreach (var requirementName in item.MissingRequirementNames.ToList())
        {
            NexusModInfo? match;
            try
            {
                var results = await _nexusApiClient.SearchModsAsync(apiKey, "icarus", requirementName);
                match = PickRequirementMatch(results, requirementName);
            }
            catch (Exception)
            {
                match = null;
            }

            if (match is null)
            {
                continue;
            }

            offered = true;
            var requirementLine = item.Requirements.FirstOrDefault(r => string.Equals(ModReadmeRequirements.ExtractName(r), requirementName, StringComparison.OrdinalIgnoreCase)) ?? requirementName;
            var download = _dialogService.Confirm(
                $"'{item.DisplayName}' needs {requirementLine}, which isn't installed.\n\nDownload '{match.Name}' (Nexus mod #{match.ModId}) now?",
                "Missing requirement", ThemedConfirmSeverity.Question);
            if (!download)
            {
                PendingDownloadStatusMessage = $"Skipped '{match.Name}' — '{item.DisplayName}' won't work until it's installed.";
                continue;
            }

            await ResolvePrimaryFileAndFetchAsync(match.ModId, match.Name);
        }

        return offered;
    }

    /// <summary>Exact name first, then a name that contains (or is contained by) the requirement — the first such hit in Nexus's own relevance order; null when nothing is close enough to offer without asking the user to look.</summary>
    internal static NexusModInfo? PickRequirementMatch(IReadOnlyList<NexusModInfo> results, string requirementName)
    {
        // Nexus lists hidden/removed mods with no name — never a match (an empty name is "contained" in everything).
        static string Norm(string? s) => s is null ? "" : new([.. s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);
        var wanted = Norm(requirementName);
        if (wanted.Length == 0)
        {
            return null;
        }

        var named = results.Select(r => (Mod: r, Name: Norm(r.Name))).Where(r => r.Name.Length > 0).ToList();
        return named.FirstOrDefault(r => r.Name == wanted).Mod
            ?? named.FirstOrDefault(r => r.Name.Contains(wanted, StringComparison.Ordinal) || wanted.Contains(r.Name, StringComparison.Ordinal)).Mod;
    }

    private string ResolveLocalFilePath(PendingDownloadItemViewModel item) =>
        File.Exists(item.LocalFilePath) ? item.LocalFilePath : Path.Combine(_pendingDownloadsDirectory, item.FileName);
}
