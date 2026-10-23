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
using IcarusStarlink.Core.IO;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Nexus;
using IcarusStarlink.Core.Secrets;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.Core.Ue4ss;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Import;

namespace IcarusStarlink.App.ViewModels;

// The Nexus download flow: nxm:// links, file choice, the download itself and the Nexus details recorded on what it installed.
public sealed partial class DownloadsViewModel
{
    /// <summary>
    /// Resolves a bare Nexus mod ID to a downloadable file, then runs it through
    /// FetchAndDownloadAsync — the same two-step DownloadModAsync uses. Shared here so
    /// NexusCatalogViewModel's own per-card Download button (its only real caller) doesn't carry
    /// its own copy of this logic. A mod with exactly one file downloads it directly, same as
    /// before; a mod with more than one (different versions, optional add-ons, or FOMOD-style
    /// variant packs) shows a real picker instead of silently grabbing whatever Nexus marks
    /// primary — that was silently discarding every other file a mod offered.
    /// </summary>
    public async Task ResolvePrimaryFileAndFetchAsync(int nexusModId, string? displayName = null)
    {
        var apiKey = _credentialStore.Read(CredentialTargets.NexusApiKey);
        if (apiKey is null)
        {
            PendingDownloadStatusMessage = "Sign in with your Nexus API key in Settings first.";
            return;
        }

        PendingDownloadStatusMessage = $"Finding mod #{nexusModId}'s file(s)…";
        try
        {
            var fileSet = await _nexusApiClient.GetModFileSetAsync(apiKey, "icarus", nexusModId);
            var files = fileSet.Files;
            if (files.Count == 0)
            {
                PendingDownloadStatusMessage = $"Mod #{nexusModId} has no downloadable files listed.";
                return;
            }

            // Updating: the newer version of the exact file already installed (one of several
            // variants, perhaps) is what's wanted — fetched directly when it's the only choice,
            // otherwise preselected, so switching to a different variant stays possible.
            var newer = NewerVersionOfInstalledFile(nexusModId, fileSet);
            NexusModFile file;
            if (files.Count == 1)
            {
                file = files[0];
            }
            else
            {
                var dialog = new PickNexusFileDialog(displayName ?? $"Mod #{nexusModId}", files, newer) { Owner = Application.Current.MainWindow };
                if (dialog.ShowDialog() != true || dialog.SelectedFile is not { } chosen)
                {
                    PendingDownloadStatusMessage = "Download cancelled.";
                    return;
                }

                file = chosen;
            }

            await FetchAndDownloadAsync($"nxm://icarus/mods/{nexusModId}/files/{file.FileId}", displayName);
        }
        catch (NexusRateLimitedException ex)
        {
            PendingDownloadStatusMessage = ex.Message;
        }
        catch (InvalidOperationException ex)
        {
            // GetModFilesAsync's own rejection — an actually invalid/expired stored key (401/403
            // on a plain file listing, not a premium-tier restriction: listing works the same for
            // every account tier). The premium-specific rejection this used to (wrongly) describe
            // instead happens one step later, inside FetchAndDownloadAsync's own GetDownloadLinksAsync
            // call — handled there now, with its own accurate, actionable message.
            PendingDownloadStatusMessage = $"{ex.Message} Sign in again with a valid key in Settings.";
        }
        catch (Exception ex)
        {
            PendingDownloadStatusMessage = $"Couldn't find that mod's file: {ex.Message}";
        }
    }

    /// <summary>
    /// The real download pipeline — parse the nxm:// link, resolve it to a real CDN URL via
    /// Nexus's own API, download the bytes, record it in Pending Downloads, then immediately
    /// activate it (the same classify-and-import logic ActivatePendingDownloadAsync already has,
    /// which already tells an EXMOD mod from a prebuilt pak from a UE4SS mod). A successful
    /// download just becomes a normal Library (or UE4SS) entry with no separate click needed —
    /// per direct feedback, a downloaded mod should "blend in" automatically. displayName (when
    /// the caller already knows it) drives the Library page's in-progress stub; falls back to
    /// "mod #N" when it isn't known yet, which has no name to show until Nexus's own headers
    /// reveal the real file name partway through. Public (not private) since this is the landing
    /// point for a real nxm:// link handed off from the OS protocol handler (App.xaml.cs's
    /// HandleNxmUrl) — its only real caller.
    /// </summary>
    public async Task FetchAndDownloadAsync(string nxmUrlText, string? displayName = null)
    {
        NxmUrl nxmUrl;
        try
        {
            nxmUrl = NxmUrl.Parse(nxmUrlText);
        }
        catch (FormatException ex)
        {
            PendingDownloadStatusMessage = $"That doesn't look like a Nexus mod page URL or an nxm:// link: {ex.Message}";
            return;
        }

        // Windows sends every game's nxm:// links to one app. A link for another game reaches here
        // only when no other mod manager was there to pass it to (App.TryForwardOtherGameLink);
        // before this, it was downloaded and installed as an Icarus mod.
        if (!string.Equals(nxmUrl.GameDomain, NxmHandlerCommand.IcarusDomain, StringComparison.OrdinalIgnoreCase))
        {
            PendingDownloadStatusMessage = $"That link is for another game ({nxmUrl.GameDomain}), not Icarus — open it in that game's mod manager.";
            _activityLog.Log($"Ignored a Nexus download for another game ({nxmUrl.GameDomain}): Starlink only installs Icarus mods, and no other mod manager was set up to pass it to.", ActivityEntryKind.Warning);
            return;
        }

        var apiKey = _credentialStore.Read(CredentialTargets.NexusApiKey);
        if (apiKey is null)
        {
            PendingDownloadStatusMessage = "Sign in with your Nexus API key in Settings first.";
            return;
        }

        var trackerKey = $"{nxmUrl.ModId}:{nxmUrl.FileId}";
        var trackerDisplayName = displayName ?? $"Mod #{nxmUrl.ModId}";

        // A second handoff for a file already in flight (a double-click on Nexus's download button
        // is common) used to download it twice and orphan the first copy (an audit finding).
        if (_activeDownloadsTracker.Current.Any(d => d.Key == trackerKey))
        {
            PendingDownloadStatusMessage = $"'{trackerDisplayName}' is already downloading.";
            return;
        }

        _activeDownloadsTracker.Start(trackerKey, trackerDisplayName);
        try
        {
            var links = await _nexusApiClient.GetDownloadLinksAsync(
                apiKey, nxmUrl.GameDomain, nxmUrl.ModId, nxmUrl.FileId, nxmUrl.Key, nxmUrl.Expires);
            if (links.Count == 0)
            {
                PendingDownloadStatusMessage = "Nexus didn't return a download link for this file.";
                return;
            }

            // Every server Nexus offers, in its order, until one answers — streamed to a .part file
            // and renamed into place on completion (an audit finding): writing straight to the final
            // path truncated an already-complete earlier download of the same file the moment a
            // re-download started, so a mid-stream failure left a truncated file that the Pending
            // Downloads row still presented as good.
            var fileName = "";
            var localPath = await HttpFileDownloader.DownloadToFileAsync(_downloadHttpClient, [.. links.Select(l => l.Uri)], response =>
            {
                fileName = ChooseNexusFileName(response, nxmUrl, links[0].Uri);
                return Path.Combine(_pendingDownloadsDirectory, fileName);
            }, HttpFileDownloader.DefaultIdleTimeout);

            _pendingDownloadStore.Add(new PendingDownloadEntry
            {
                ModId = nxmUrl.ModId,
                FileId = nxmUrl.FileId,
                FileName = fileName,
                LocalFilePath = localPath,
                DownloadedAtUtc = DateTimeOffset.UtcNow,
            });
            ReloadPendingDownloads();
            PendingDownloadStatusMessage = $"Downloaded '{fileName}' — importing…";

            var justDownloaded = PendingDownloads.FirstOrDefault(p => p.ModId == nxmUrl.ModId && p.FileId == nxmUrl.FileId);
            if (justDownloaded is not null)
            {
                await ActivatePendingDownloadAsync(justDownloaded);
            }
        }
        catch (InvalidOperationException ex)
        {
            // Nexus's own real rejection for exactly this case: a non-premium account's API key
            // alone isn't accepted for a direct download-link request (GetDownloadLinksAsync
            // above) — the account needs a real, temporary key+expires pair, which only comes from
            // clicking Nexus's own "Mod Manager Download" button on the actual website. Caught
            // specifically, ahead of the generic catch below, and opens the real mod page directly
            // rather than just naming it, so there's one less manual step for a confused non-
            // premium user. Found live: this exact case used to fall through to the generic
            // "Download failed: ..." message below with no actionable next step at all, since
            // ResolvePrimaryFileAndFetchAsync's own separate (more helpful) catch for this same
            // exception type was unreachable dead code — this method's own broad catch already
            // swallowed it first, every time, before it could ever propagate that far.
            UrlOpener.TryOpen(NexusModWebUrl.For(nxmUrl.ModId));
            PendingDownloadStatusMessage =
                $"{ex.Message} Opened the mod's Nexus page — if your account isn't Premium, click its "
                + "\"Mod Manager Download\" button there instead (register IcarusStarlink as your nxm:// "
                + "handler in Settings first, if you haven't already).";
        }
        catch (Exception ex)
        {
            // Same UI boundary as everywhere else — a stale/expired nxm key, a network hiccup, or
            // a disk-full save all show a status message rather than crashing the app.
            PendingDownloadStatusMessage = $"Download failed: {ex.Message}";
        }
        finally
        {
            _activeDownloadsTracker.Finish(trackerKey);
        }
    }

    /// <summary>
    /// The name a Nexus download is saved under: the server's Content-Disposition name (or the URL's
    /// last segment), made safe, Nexus's id/timestamp tail dropped, and kept apart from a different
    /// file's download of the same name.
    /// </summary>
    private string ChooseNexusFileName(HttpResponseMessage response, NxmUrl nxmUrl, string firstLink)
    {
        var answeredUri = response.RequestMessage?.RequestUri ?? new Uri(firstLink);
        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar?.Trim('"')
            ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
            ?? Path.GetFileName(answeredUri.LocalPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = $"nexus_{nxmUrl.ModId}_{nxmUrl.FileId}.zip";
        }

        // fileName came straight from the CDN response's own Content-Disposition header (or,
        // failing that, the download URL's own path) — neither is trustworthy: a rooted or
        // "../"-laden value would otherwise let Path.Combine write anywhere the app process can
        // write. Nexus's "<name> <modId> <fileId> <timestamp> <hash>" tail dropped from the saved
        // name (a real complaint: it rode into staged mod names). The (ModId, FileId) pair is what
        // keeps same-named files apart, not the hash.
        fileName = DownloadFileNameSanitizer.Sanitize(
            NexusDownloadFileName.StripNexusSuffix(Path.GetFileNameWithoutExtension(fileName), nxmUrl.ModId) + Path.GetExtension(fileName));

        Directory.CreateDirectory(_pendingDownloadsDirectory);
        // See DownloadFileNameSanitizer.ResolveUniqueFileName's own doc comment: disambiguates
        // against a DIFFERENT (ModId, FileId) pair's download that happens to share this exact
        // file name, without disturbing a legitimate re-download of this same pair.
        return DownloadFileNameSanitizer.ResolveUniqueFileName(
            _pendingDownloadsDirectory, fileName, nxmUrl.ModId, nxmUrl.FileId,
            path => _pendingDownloadStore.Entries.Any(e => e.ModId == nxmUrl.ModId && e.FileId == nxmUrl.FileId && e.LocalFilePath == path));
    }

    /// <summary>
    /// When exactly one file of this mod is installed (by its recorded Nexus file ID) and Nexus lists
    /// a newer file in that file's own update chain, that newer file; otherwise null, and the usual
    /// choice applies. Two different files of the same mod installed means there's no single answer.
    /// </summary>
    internal NexusModFile? NewerVersionOfInstalledFile(int nexusModId, NexusModFileSet fileSet)
    {
        var installedFileIds = _libraryRepository.GetAll()
            .Where(e => e.NexusModId == nexusModId && e.NexusFileId is not null)
            .Select(e => e.NexusFileId!.Value)
            .Concat(_ue4ssModRepository.GetAll()
                .Select(folder => _ue4ssModMetaStore.Load(folder))
                .Where(meta => meta.NexusModId == nexusModId && meta.NexusFileId is not null)
                .Select(meta => meta.NexusFileId!.Value))
            .Distinct()
            .ToList();
        if (installedFileIds.Count == 0)
        {
            return null;
        }

        // One download can land as a pak mod AND a UE4SS mod from the same file — that's still one file.
        var newest = installedFileIds.Select(id => NexusFileLineage.Newest(fileSet, id)).ToList();
        if (newest.Any(n => n is null) || newest.Select(n => n!.FileId).Distinct().Count() != 1)
        {
            return null;
        }

        var target = newest[0]!;
        return installedFileIds.Contains(target.FileId) ? null : target;
    }

    /// <summary>
    /// Remembers which Nexus file every mod from this download came from, and that file's own
    /// version, so update checks follow THIS file (one of several variants, perhaps) rather than
    /// the mod's headline version. The file ID is always known here; the version is best-effort
    /// (no key, or Nexus unreachable, leaves it unknown and the check falls back to the mod).
    /// </summary>
    private async Task RecordNexusFileAsync(int modId, int fileId, IReadOnlyList<PendingDownloadActivatedItem> activatedItems)
    {
        string? fileVersion = null;
        if (_credentialStore.Read(CredentialTargets.NexusApiKey) is { } apiKey)
        {
            try
            {
                var files = await _nexusApiClient.GetModFilesAsync(apiKey, "icarus", modId);
                fileVersion = files.FirstOrDefault(f => f.FileId == fileId)?.Version is { Length: > 0 } v ? v : null;
            }
            catch (Exception)
            {
                // Best-effort — the file ID alone is still worth keeping.
            }
        }

        foreach (var activated in activatedItems)
        {
            try
            {
                if (activated.Kind == PendingDownloadActivationKind.Library)
                {
                    _libraryRepository.SetNexusFile(activated.FolderName, fileId, fileVersion);
                }
                else
                {
                    var meta = _ue4ssModMetaStore.Load(activated.FolderName);
                    meta.NexusFileId = fileId;
                    meta.NexusFileVersion = fileVersion;
                    _ue4ssModMetaStore.Save(activated.FolderName, meta);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Bookkeeping only (which Nexus file this is, for per-file updates). A locked
                // sidecar must not turn an install that worked into a "failed" one — that used to
                // open the manual install and delete the mod that had just landed.
                _activityLog.Log($"Couldn't record which Nexus file '{activated.FolderName}' came from: {ex.Message}", ActivityEntryKind.Warning);
            }
        }

        // Callers refreshed the Library before this ran; once more so it sees the file id.
        WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
    }

    /// <summary>
    /// Looks the mod up by the ID the nxm:// download already told us and records what Nexus knows
    /// (name/author/description/version) in the entry's sidecar. For an opaque .pak (no embedded
    /// metadata of its own) or a converted one, that becomes the displayed name/author; for an
    /// ordinary EXMODZ only the version is surfaced (as LibraryEntry.NexusVersion, the like-for-like
    /// side of update-checking) — FolderLibraryRepository.SetNexusMetadata never overwrites a real
    /// author's own declared name. Best-effort only: no signed-in key, a rejected key, or a network
    /// hiccup all fall through silently, since a missing enrichment shouldn't turn an otherwise-
    /// successful import into a reported failure — the entry just keeps showing "Unknown" the way
    /// it always has.
    /// </summary>
    private async Task RecordNexusMetadataAsync(string folderName, int modId)
    {
        var apiKey = _credentialStore.Read(CredentialTargets.NexusApiKey);
        if (apiKey is null)
        {
            return;
        }

        try
        {
            var info = await _nexusApiClient.GetModInfoAsync(apiKey, "icarus", modId);
            if (info is not null)
            {
                _libraryRepository.SetNexusMetadata(folderName, info.Name, info.Author, info.Summary, info.Version);
            }
        }
        catch (Exception)
        {
            // Best-effort — see the doc comment above.
        }
    }

    /// <summary>
    /// Same reasoning as RecordNexusMetadataAsync, but for a UE4SS mod: a UE4SS mod carries no
    /// metadata file at all (not even a name/author), so its Nexus mod ID and version are recorded
    /// in the separate Ue4ss_Meta sidecar store instead of LibraryMeta — that's what lets Library's
    /// UE4SS tab show a "Nexus" badge and check for updates.
    ///
    /// The ID itself is saved unconditionally, BEFORE the enrichment call — matching the opaque-pak
    /// path's own ImportPak(..., nexusModId: modId), which records the ID at import time regardless
    /// of what SetNexusMetadata's own best-effort lookup does. Only the version lookup is
    /// best-effort/silently-swallowed: a missing key or a rejected/failed API call still leaves the
    /// mod correctly linked (HasNexusLink true, eligible for CheckForUpdatesAsync), just without a
    /// known starting version yet — versus the original shape, where any enrichment failure meant
    /// the ID was never saved at all, permanently excluding the mod from update-checking with no way
    /// to relink it (Library's UE4SS tab has no manual "Link to Nexus…" action, unlike its Mods tab).
    ///
    /// Load-modify-Save, not a fresh `new Ue4ssModMeta { ... }` — folderName is freshly staged by
    /// this same activation almost always, but a disambiguated/reused folder name could already
    /// carry a user-declared MinUe4ssVersion (see its own doc comment) from a previous mod that once
    /// lived under this same name; a blind replace would silently wipe that out.
    /// </summary>
    private async Task EnrichUe4ssModFromNexusAsync(string folderName, int modId)
    {
        var meta = _ue4ssModMetaStore.Load(folderName);
        meta.NexusModId = modId;
        _ue4ssModMetaStore.Save(folderName, meta);

        var apiKey = _credentialStore.Read(CredentialTargets.NexusApiKey);
        if (apiKey is null)
        {
            return;
        }

        try
        {
            var info = await _nexusApiClient.GetModInfoAsync(apiKey, "icarus", modId);
            if (info is not null)
            {
                meta.NexusVersion = info.Version;
                meta.NexusName = info.Name;
                _ue4ssModMetaStore.Save(folderName, meta);
            }
        }
        catch (Exception)
        {
            // Best-effort — see the doc comment above. The ID link above already succeeded regardless.
        }
    }
}
