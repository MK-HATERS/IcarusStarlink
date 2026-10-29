using System.IO;
using IcarusStarlink.App.Views;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Nexus;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.Core.Ue4ss;
using IcarusStarlink.PakIO.Import;
using IcarusStarlink.PakIO.Install;

namespace IcarusStarlink.App.Services;

/// <summary>What a replacement swaps out: the mod's name (for questions and messages) and the copies it replaces.</summary>
/// <param name="Pristine">Reinstall original: the new copy is left exactly as downloaded — no rename carried over, no fixes made again, and the remembered fix choices forgotten. The user already agreed to lose edits, so they aren't asked again.</param>
/// <param name="AskBeforeReplacingEdits">False when the question was already answered (a manual install after a failed attempt).</param>
public sealed record ModReplacementRequest(
    string ModName,
    IReadOnlyList<string> LibraryFolders,
    IReadOnlyList<string> Ue4ssFolders,
    bool Pristine = false,
    bool AskBeforeReplacingEdits = true);

/// <summary>How a replacement went — callers report this instead of assuming success.</summary>
public sealed class ModReplacementResult
{
    public bool Succeeded { get; private init; }

    /// <summary>The user chose not to go ahead (kept an edited copy, cancelled a question) — nothing changed.</summary>
    public bool Cancelled { get; private init; }

    /// <summary>What the new copy landed as.</summary>
    public IReadOnlyList<PendingDownloadActivatedItem> Items { get; private init; } = [];

    /// <summary>Why it failed or was cancelled, in plain words (already says whether the old copy was put back).</summary>
    public string? Message { get; private init; }

    /// <summary>The exception a failed install threw, for callers that offer something else on failure (manual install).</summary>
    public Exception? Error { get; private init; }

    /// <summary>A failure after the old copy was removed, and the old copy is back.</summary>
    public bool RolledBack { get; private init; }

    /// <summary>Remembered fixes made again on the new copy.</summary>
    public int FixesReapplied { get; private init; }

    /// <summary>Things that didn't go to plan but didn't fail the replacement (an enabled UE4SS mod that couldn't be re-enabled).</summary>
    public IReadOnlyList<string> Warnings { get; private init; } = [];

    /// <summary>The UE4SS mod's config files kept from the previous copy ("config.lua"), so the player's settings survived the update.</summary>
    public IReadOnlyList<string> KeptConfigs { get; private init; } = [];

    /// <summary>Settings the new version's config has that the kept file lacked ("config.lua: Speed"): put in at the mod's defaults.</summary>
    public IReadOnlyList<string> NewSettings { get; private init; } = [];

    /// <summary>Settings the kept file had that the new version doesn't ("config.lua: OldName = 5"): left out, with the player's old value for a renamed one.</summary>
    public IReadOnlyList<string> RemovedSettings { get; private init; } = [];

    /// <summary>" Made 3 remembered fixes again. Kept your settings (config.lua). Couldn't enable …" — what to add to a success message; empty when there's nothing to say.</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (FixesReapplied > 0)
            {
                parts.Add(FixesReapplied == 1 ? "Made 1 remembered fix again." : $"Made {FixesReapplied} remembered fixes again.");
            }

            if (KeptConfigs.Count > 0)
            {
                parts.Add($"Kept your settings ({string.Join(", ", KeptConfigs)}).");
            }

            if (NewSettings.Count > 0 || RemovedSettings.Count > 0)
            {
                var review = new List<string>();
                if (NewSettings.Count > 0)
                {
                    review.Add($"new, at their defaults: {string.Join("; ", NewSettings)}");
                }

                if (RemovedSettings.Count > 0)
                {
                    review.Add($"no longer in the mod (your values): {string.Join("; ", RemovedSettings)}");
                }

                parts.Add($"Look over its settings in Edit config — {string.Join(". ", review)}.");
            }

            parts.AddRange(Warnings);
            return parts.Count == 0 ? "" : " " + string.Join(" ", parts);
        }
    }

    internal static ModReplacementResult Success(
        IReadOnlyList<PendingDownloadActivatedItem> items, int fixesReapplied, IReadOnlyList<string> warnings, Ue4ssModConfigCarryOverResult? configs = null) =>
        new()
        {
            Succeeded = true, Items = items, FixesReapplied = fixesReapplied, Warnings = warnings,
            KeptConfigs = configs?.Kept ?? [], NewSettings = configs?.NewSettings ?? [], RemovedSettings = configs?.RemovedSettings ?? [],
        };

    internal static ModReplacementResult Cancel(string message) => new() { Cancelled = true, Message = message };

    internal static ModReplacementResult Failure(string message, Exception? error, bool rolledBack) =>
        new() { Message = message, Error = error, RolledBack = rolledBack };
}

/// <summary>
/// The one way a mod is replaced by a new copy — Downloads' Activate/Reinstall, Library's Get
/// update and Reinstall original all go through here. Every existing Library copy is backed up and
/// its Library info (pin, favourite, notes, rename, source link) captured first; an edited copy is
/// only replaced after asking; an enabled UE4SS mod is taken out of the game (Apply, backed up)
/// before its staged copy goes, and the new one is enabled again afterwards. Pin/favourite/notes and
/// the rename carry over to the new copy and remembered fixes are made again — unless the user
/// asked for the pristine original. Any failure after the old copy was removed puts it back.
/// </summary>
public sealed class ModReplacementService(
    ILibraryRepository library,
    IUe4ssModRepository ue4ssRepository,
    IUe4ssModMetaStore ue4ssMetaStore,
    IUe4ssModStateService ue4ssStateService,
    ISettingsService settings,
    string backupDirectory,
    IDialogService? dialogs = null,
    ModFixService? fixService = null,
    IPrebuiltPakSourceStore? pakSourceStore = null,
    string? pakPreviewCacheDirectory = null)
{
    /// <summary>Takes the old copies out the same way Library's Delete does (the preview cache goes too).</summary>
    private readonly LibraryModRemover _remover = new(library, pakSourceStore, pakPreviewCacheDirectory, fixService);

    private sealed record OldLibraryCopy(LibraryEntry Entry, string? MetaSnapshot, bool HadRememberedFixes);

    private sealed record OldUe4ssCopy(string Name, bool WasEnabled, Ue4ssModMeta Meta);

    /// <summary>The fix service, for callers that share it (one lock for every writer of a mod's .EXMOD).</summary>
    public ModFixService? FixService => fixService;

    /// <param name="install">Imports the new copy. It's handed the "remove the old copies" step and calls it right before its first write (after any question of its own, so cancelling that changes nothing); it returns what landed.</param>
    public async Task<ModReplacementResult> ReplaceAsync(
        ModReplacementRequest request, Func<Action, Task<IReadOnlyList<PendingDownloadActivatedItem>>> install)
    {
        var entries = library.GetAll();
        var oldEntries = request.LibraryFolders
            .Select(folder => entries.FirstOrDefault(e => string.Equals(e.FolderName, folder, StringComparison.OrdinalIgnoreCase)))
            .Where(e => e is not null)
            .Select(e => e!)
            .DistinctBy(e => e.FolderName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // An edited copy (✎: the editor's Save, or a fix) holds work the download doesn't. Asked
        // before anything changes; keeping it leaves everything as it is.
        if (!request.Pristine && request.AskBeforeReplacingEdits && dialogs is not null && oldEntries.FirstOrDefault(e => e.IsLocallyEdited) is { } edited
            && !dialogs.Confirm(
                $"'{edited.Name}' has changes made in Starlink (✎ — your own edits or fixes).\n\nReplacing it with the download loses hand edits (remembered fixes are made again). The current copy is backed up first, so Restore latest backup can bring it back.\n\nReplace it? No keeps your edited copy and changes nothing.",
                "Replace an edited mod?", ThemedConfirmSeverity.Warning))
        {
            return ModReplacementResult.Cancel($"Kept your edited copy of '{edited.Name}' — nothing was replaced.");
        }

        // Every copy is backed up before anything is removed; one that can't be stops the whole thing.
        var oldLibrary = new List<OldLibraryCopy>();
        foreach (var entry in oldEntries)
        {
            try
            {
                library.BackupMod(entry.FolderName);
            }
            catch (Exception ex)
            {
                return ModReplacementResult.Failure($"Couldn't back up '{entry.Name}' first, so nothing was replaced: {ex.Message}", error: null, rolledBack: false);
            }

            string? snapshot = null;
            try
            {
                snapshot = library.ReadMetaSnapshot(entry.FolderName);
            }
            catch (Exception)
            {
                // Falls back to carrying the fields the entry itself holds (see RestoreLibraryInfo).
            }

            oldLibrary.Add(new OldLibraryCopy(entry, snapshot, fixService?.RememberedCount(entry.FolderName) > 0));
        }

        var modsFolder = string.IsNullOrWhiteSpace(settings.Current.IcarusContentPath) ? null : Ue4ssGamePaths.ResolveModsFolder(settings.Current.IcarusContentPath);
        var inGame = modsFolder is null ? [] : ue4ssRepository.ListInstalledInGame(modsFolder).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var staged = ue4ssRepository.GetAll().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var oldUe4ss = request.Ue4ssFolders
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(name => staged.Contains(name) || inGame.Contains(name))
            .Select(name => new OldUe4ssCopy(name, inGame.Contains(name), ue4ssMetaStore.Load(name)))
            .ToList();

        var libraryBefore = entries.Select(e => e.FolderName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var scratch = Path.Combine(Path.GetTempPath(), $"IcarusStarlink_Replace_{Guid.NewGuid():N}");
        var removedLibrary = new List<OldLibraryCopy>();
        var removedUe4ss = new List<OldUe4ssCopy>();
        var removed = false;

        void RemoveOld()
        {
            if (removed)
            {
                return;
            }

            removed = true;
            foreach (var copy in oldUe4ss)
            {
                if (copy.WasEnabled)
                {
                    // A staged copy next to the enabled one is a leftover of the old reinstall bug
                    // (the game kept the old copy); disabling would land the game copy as "_2".
                    if (staged.Contains(copy.Name))
                    {
                        ue4ssRepository.Delete(copy.Name);
                    }

                    // Out of the game first (backed up under Backups\UE4SS), the way Disable does —
                    // otherwise the game kept running the old copy and a later disable made a "_2".
                    var failures = ue4ssStateService.Apply(modsFolder!, new Dictionary<string, bool> { [copy.Name] = false }, backupDirectory);
                    if (failures.Count > 0)
                    {
                        throw new IOException($"Couldn't take '{copy.Name}' out of the game's Mods folder: {failures[0].Reason}");
                    }
                }

                // Kept aside until the new copy is in, so a failure can put it back.
                FolderBackup.CopyDirectory(ue4ssRepository.GetFolderPath(copy.Name), Path.Combine(scratch, copy.Name, copy.Name));
                ue4ssRepository.Delete(copy.Name);
                ue4ssMetaStore.Delete(copy.Name);
                removedUe4ss.Add(copy);
            }

            foreach (var copy in oldLibrary)
            {
                _remover.RemoveForReplacement(copy.Entry.FolderName);
                removedLibrary.Add(copy);
            }
        }

        try
        {
            IReadOnlyList<PendingDownloadActivatedItem> items;
            try
            {
                items = await install(RemoveOld);
            }
            catch (ImportCancelledException ex)
            {
                var putBack = !removed || RollBack(libraryBefore, staged, removedLibrary, removedUe4ss, modsFolder, scratch);
                return ModReplacementResult.Cancel(putBack ? ex.Message : $"{ex.Message} Your previous copy couldn't be put back — use Restore latest backup.");
            }
            catch (Exception ex)
            {
                if (!removed)
                {
                    return ModReplacementResult.Failure(ex.Message, ex, rolledBack: false);
                }

                var rolledBack = RollBack(libraryBefore, staged, removedLibrary, removedUe4ss, modsFolder, scratch);
                return ModReplacementResult.Failure(
                    rolledBack
                        ? $"{ex.Message} — your previous copy was restored from its backup."
                        : $"{ex.Message} — your previous copy couldn't be put back automatically; it's in Backups\\Mods.",
                    ex, rolledBack);
            }

            var warnings = new List<string>();
            var fixes = await CarryOverAsync(request, items, oldLibrary, warnings);
            // Before the new copy goes into the game: its config is set while it's still staged.
            var configs = KeepUe4ssConfigs(request, items, oldUe4ss, scratch, warnings);
            RestoreUe4ss(items, oldUe4ss, modsFolder, warnings);
            CleanUpOldSources(items, oldLibrary);
            return ModReplacementResult.Success(items, fixes, warnings, configs);
        }
        finally
        {
            TryDeleteDirectory(scratch);
        }
    }

    /// <summary>Pin/favourite/notes (always), the rename and remembered fixes (unless pristine) onto each new Library copy.</summary>
    private async Task<int> CarryOverAsync(ModReplacementRequest request, IReadOnlyList<PendingDownloadActivatedItem> items, IReadOnlyList<OldLibraryCopy> oldLibrary, List<string> warnings)
    {
        var newFolders = items.Where(i => i.Kind == PendingDownloadActivationKind.Library).Select(i => i.FolderName).ToList();
        var fixes = 0;
        if (request.Pristine && fixService is not null)
        {
            // Only now, after it worked: a failed reinstall keeps the old copy AND its choices.
            foreach (var folder in newFolders.Concat(oldLibrary.Select(o => o.Entry.FolderName)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                fixService.Forget(folder);
            }
        }

        foreach (var folder in newFolders)
        {
            // Same folder name first; otherwise a one-for-one swap (the mod's file name changed between versions).
            var old = oldLibrary.FirstOrDefault(o => string.Equals(o.Entry.FolderName, folder, StringComparison.OrdinalIgnoreCase))
                ?? (oldLibrary.Count == 1 && newFolders.Count == 1 ? oldLibrary[0] : null);
            if (old is null)
            {
                continue;
            }

            try
            {
                if (old.Entry.IsPinned || old.Entry.IsFavorite || !string.IsNullOrEmpty(old.Entry.Notes))
                {
                    library.UpdateMetadata(folder, old.Entry.IsPinned, old.Entry.IsFavorite, old.Entry.Notes);
                }

                if (!request.Pristine && old.Entry.DisplayNameOverride is { } rename)
                {
                    library.SetDisplayNameOverride(folder, rename);
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"Couldn't carry the pin/notes of '{old.Entry.Name}' over: {ex.Message}");
            }

            if (fixService is null || request.Pristine)
            {
                continue;
            }

            try
            {
                var current = library.GetAll().FirstOrDefault(e => string.Equals(e.FolderName, folder, StringComparison.OrdinalIgnoreCase));
                if (current is null || current.IsOpaquePak)
                {
                    continue;
                }

                var service = fixService;
                var fromFolder = old.Entry.FolderName;
                var fixedBefore = old.Entry.FixedByIslAtUtc is not null || old.HadRememberedFixes;
                var newFolder = folder;
                // A copy that was fixed before gets its sure fixes made again; remembered choices too.
                // Off the UI thread: a check reads the game's tables.
                fixes += await Task.Run(() =>
                {
                    service.CarryOver(fromFolder, newFolder);
                    return fixedBefore ? service.FixSure(newFolder).Applied : service.ReapplyRemembered(newFolder);
                });
            }
            catch (Exception ex)
            {
                warnings.Add($"Couldn't make the remembered fixes again on '{old.Entry.Name}': {ex.Message} — use Check against the game & fix.");
            }
        }

        return fixes;
    }

    /// <summary>
    /// The player's settings survive the update (user report 2026-10-27: activating a new Nexus
    /// version of a UE4SS mod replaced config.lua with the shipped one, and the settings were only
    /// in Backups\UE4SS). The old copy's config files, kept aside in the scratch folder, go into the
    /// new staged copy over the shipped ones; the shipped defaults file (config.default.lua) stays
    /// the new version's. Not for Reinstall original: the user asked for the copy as downloaded.
    /// </summary>
    private Ue4ssModConfigCarryOverResult KeepUe4ssConfigs(
        ModReplacementRequest request, IReadOnlyList<PendingDownloadActivatedItem> items, IReadOnlyList<OldUe4ssCopy> oldUe4ss, string scratch, List<string> warnings)
    {
        if (request.Pristine)
        {
            return Ue4ssModConfigCarryOverResult.Nothing;
        }

        var kept = new List<string>();
        var reviews = new List<Ue4ssConfigReview>();
        var newNames = items.Where(i => i.Kind == PendingDownloadActivationKind.Ue4ssMod).Select(i => i.FolderName).ToList();
        foreach (var name in newNames)
        {
            var old = oldUe4ss.FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? (oldUe4ss.Count == 1 && newNames.Count == 1 ? oldUe4ss[0] : null);
            if (old is null)
            {
                continue;
            }

            try
            {
                var newFolder = ue4ssRepository.GetFolderPath(name);
                var result = Ue4ssModConfigCarryOver.KeepInstalledConfigs(Path.Combine(scratch, old.Name, old.Name), newFolder, Ue4ssModVersion.TryRead(newFolder));
                kept.AddRange(result.Kept);
                reviews.AddRange(result.Reviews);
                // Remembered on the mod until the user has looked (the Library row and Edit config point it out).
                if (result.Reviews.Count > 0)
                {
                    var meta = ue4ssMetaStore.Load(name);
                    meta.ConfigReviews = [.. result.Reviews];
                    ue4ssMetaStore.Save(name, meta);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Couldn't keep your settings for '{name}': {ex.Message} — the previous copy is in Backups\\UE4SS.");
            }
        }

        return new Ue4ssModConfigCarryOverResult(kept, reviews);
    }

    /// <summary>The UE4SS settings the user made (minimum loader version) carry over, and an enabled mod is enabled again.</summary>
    private void RestoreUe4ss(IReadOnlyList<PendingDownloadActivatedItem> items, IReadOnlyList<OldUe4ssCopy> oldUe4ss, string? modsFolder, List<string> warnings)
    {
        var newNames = items.Where(i => i.Kind == PendingDownloadActivationKind.Ue4ssMod).Select(i => i.FolderName).ToList();
        foreach (var name in newNames)
        {
            var old = oldUe4ss.FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? (oldUe4ss.Count == 1 && newNames.Count == 1 ? oldUe4ss[0] : null);
            if (old?.Meta.MinUe4ssVersion is { } minimum)
            {
                var meta = ue4ssMetaStore.Load(name);
                if (meta.MinUe4ssVersion is null)
                {
                    meta.MinUe4ssVersion = minimum;
                    ue4ssMetaStore.Save(name, meta);
                }
            }
        }

        if (modsFolder is null || newNames.Count == 0 || !oldUe4ss.Any(o => o.WasEnabled))
        {
            return;
        }

        var failures = ue4ssStateService.Apply(modsFolder, newNames.ToDictionary(n => n, _ => true, StringComparer.OrdinalIgnoreCase), backupDirectory);
        warnings.AddRange(failures.Select(f => $"Couldn't enable the new '{f.Name}' in the game: {f.Reason} — tick it and Apply UE4SS changes."));
    }

    /// <summary>An old copy's saved source pak goes once nothing will re-convert from it (the new copy isn't a converted pak under the same name).</summary>
    private void CleanUpOldSources(IReadOnlyList<PendingDownloadActivatedItem> items, IReadOnlyList<OldLibraryCopy> oldLibrary)
    {
        if (pakSourceStore is null)
        {
            return;
        }

        foreach (var old in oldLibrary)
        {
            var replacement = items.Any(i => i.Kind == PendingDownloadActivationKind.Library && string.Equals(i.FolderName, old.Entry.FolderName, StringComparison.OrdinalIgnoreCase))
                ? library.GetAll().FirstOrDefault(e => string.Equals(e.FolderName, old.Entry.FolderName, StringComparison.OrdinalIgnoreCase))
                : null;
            if (replacement is { IsOpaquePak: true } or { ConvertedFromPrebuiltPak: true })
            {
                continue; // the import saved the new pak under this name
            }

            _remover.DeleteSourcePak(old.Entry.FolderName);
        }
    }

    /// <summary>Takes out whatever the failed install left and puts every removed copy back. True when all of it came back.</summary>
    private bool RollBack(
        IReadOnlySet<string> libraryBefore, IReadOnlySet<string> stagedBefore, IReadOnlyList<OldLibraryCopy> removedLibrary,
        IReadOnlyList<OldUe4ssCopy> removedUe4ss, string? modsFolder, string scratch)
    {
        var ok = true;
        var oldNames = removedLibrary.Select(o => o.Entry.FolderName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var partial in library.GetAll().Select(e => e.FolderName).Where(f => !libraryBefore.Contains(f) && !oldNames.Contains(f)).ToList())
        {
            try
            {
                library.Delete(partial);
            }
            catch (Exception)
            {
                ok = false;
            }
        }

        foreach (var old in removedLibrary)
        {
            try
            {
                if (!library.RestoreLatestModBackup(old.Entry.FolderName))
                {
                    ok = false;
                    continue;
                }

                RestoreLibraryInfo(old);
            }
            catch (Exception)
            {
                ok = false;
            }
        }

        var oldUe4ssNames = removedUe4ss.Select(o => o.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var partial in ue4ssRepository.GetAll().Where(n => !stagedBefore.Contains(n) || oldUe4ssNames.Contains(n)).ToList())
        {
            try
            {
                ue4ssRepository.Delete(partial);
                ue4ssMetaStore.Delete(partial);
            }
            catch (Exception)
            {
                ok = false;
            }
        }

        foreach (var old in removedUe4ss)
        {
            try
            {
                var name = ue4ssRepository.ImportFromFolder(Path.Combine(scratch, old.Name), old.Name);
                ue4ssMetaStore.Save(name, old.Meta);
                if (old.WasEnabled && modsFolder is not null
                    && ue4ssStateService.Apply(modsFolder, new Dictionary<string, bool> { [name] = true }, backupDirectory).Count > 0)
                {
                    ok = false;
                }
            }
            catch (Exception)
            {
                ok = false;
            }
        }

        return ok;
    }

    /// <summary>The Library info Delete removed: the exact snapshot when there is one, else the fields the entry holds.</summary>
    private void RestoreLibraryInfo(OldLibraryCopy old)
    {
        if (old.MetaSnapshot is { } snapshot)
        {
            library.WriteMetaSnapshot(old.Entry.FolderName, snapshot);
            return;
        }

        var entry = old.Entry;
        if (entry.IsPinned || entry.IsFavorite || !string.IsNullOrEmpty(entry.Notes))
        {
            library.UpdateMetadata(entry.FolderName, entry.IsPinned, entry.IsFavorite, entry.Notes);
        }

        if (entry.DisplayNameOverride is not null)
        {
            library.SetDisplayNameOverride(entry.FolderName, entry.DisplayNameOverride);
        }

        if (entry.CatalogEntryId is not null)
        {
            library.SetCatalogEntry(entry.FolderName, entry.CatalogEntryId);
        }
        else if (entry.NexusModId is { } nexusModId)
        {
            library.LinkToNexus(entry.FolderName, nexusModId);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception)
        {
            // Scratch space in %TEMP%; a locked leftover isn't worth reporting.
        }
    }
}
