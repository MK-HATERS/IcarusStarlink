using System.IO;
using IcarusStarlink.App.Utilities;
using IcarusStarlink.Catalog;
using IcarusStarlink.Catalog.Daedalus;
using IcarusStarlink.Catalog.Jimk72;
using IcarusStarlink.Catalog.Nexus;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Migration;
using IcarusStarlink.Core.Secrets;

namespace IcarusStarlink.App.Services;

public enum ImmMigrationOutcome
{
    Imported,
    AlreadyInLibrary,
    NotFoundOnDisk,
    Failed,
}

/// <summary>What happened to one mod from the list, including which source (if any) it was recognized as.</summary>
public sealed record ImmMigratedMod(string ListName, ImmMigrationOutcome Outcome, string? Source, string? Detail)
{
    public string Display => Outcome switch
    {
        ImmMigrationOutcome.Imported => $"{ListName} — brought over{SourceSuffix}",
        ImmMigrationOutcome.AlreadyInLibrary => $"{ListName} — already in your Library{SourceSuffix}",
        ImmMigrationOutcome.NotFoundOnDisk => $"{ListName} — not found in classic IMM's own mods folder",
        _ => $"{ListName} — couldn't import: {Detail}",
    };

    private string SourceSuffix => Source is null ? "" : $" (linked to {Source})";
}

public sealed record ImmMigrationResult(IReadOnlyList<ImmMigratedMod> Mods, IReadOnlyList<string> FailedCatalogSources)
{
    public int ImportedCount => Mods.Count(m => m.Outcome == ImmMigrationOutcome.Imported);
    public int AlreadyPresentCount => Mods.Count(m => m.Outcome == ImmMigrationOutcome.AlreadyInLibrary);
    public int MissingCount => Mods.Count(m => m.Outcome == ImmMigrationOutcome.NotFoundOnDisk);
    public int FailedCount => Mods.Count(m => m.Outcome == ImmMigrationOutcome.Failed);
    public int LinkedToDatabaseCount => Mods.Count(m => m.Source == "Database");
    public int LinkedToNexusCount => Mods.Count(m => m.Source == "Nexus");
}

/// <summary>
/// One-click migration from a classic IMM install: read one of its merged mod lists, copy every
/// mod it names straight out of IMM's own Extracted_Mods folder into this app's Library, and
/// identify where each mod came from so update-checking works from day one.
///
/// Copying beats re-downloading — the mods are already on disk, so migration is offline, instant,
/// and gets exactly the versions the user was actually running. Source detection is best-effort
/// enrichment layered on top: never a reason to fail a migration, since an unrecognized mod is
/// still perfectly usable, just without automatic update checks.
/// </summary>
public sealed class ImmMigrationService(
    ILibraryRepository repository,
    IDaedalusCatalogClient daedalusClient,
    IJimk72CatalogClient jimk72Client,
    INexusApiClient nexusApiClient,
    ICredentialStore credentialStore)
{
    public async Task<ImmMigrationResult> MigrateAsync(
        string modListPath, string installRoot, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var listNames = ModListText.ParseNames(await File.ReadAllTextAsync(modListPath, cancellationToken));
        var immMods = ImmExtractedMods.Parse(await File.ReadAllTextAsync(ImmInstallPaths.ExtractedModsJson(installRoot), cancellationToken));
        var immExtractedModsFolder = ImmInstallPaths.ExtractedModsFolder(installRoot);

        progress?.Report("Fetching the mod database…");
        var (catalog, failedCatalogSources) = await FetchCatalogAsync();

        var results = new List<ImmMigratedMod>();
        var index = 0;
        foreach (var listName in listNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            index++;
            progress?.Report($"Migrating {index} of {listNames.Count}: {listName}");
            results.Add(await MigrateOneAsync(listName, immMods, immExtractedModsFolder, catalog, cancellationToken));
        }

        return new ImmMigrationResult(results, failedCatalogSources);
    }

    private async Task<ImmMigratedMod> MigrateOneAsync(
        string listName, IReadOnlyList<ImmExtractedMod> immMods, string immExtractedModsFolder,
        IReadOnlyList<CatalogEntry> catalog, CancellationToken cancellationToken)
    {
        var immMod = ImmExtractedMods.Find(immMods, listName);

        // Already here? Don't re-import (that would duplicate the folder) — but still try to link
        // it, since a mod imported some other way may never have been given a source.
        var existing = FindInLibrary(listName, immMod);
        if (existing is not null)
        {
            var linkedSource = await LinkSourceAsync(existing, immMod?.Author ?? existing.Author, catalog, cancellationToken);
            return new ImmMigratedMod(listName, ImmMigrationOutcome.AlreadyInLibrary, linkedSource ?? existing.Source, null);
        }

        if (immMod is null)
        {
            return new ImmMigratedMod(listName, ImmMigrationOutcome.NotFoundOnDisk, null, null);
        }

        var sourceFolder = Path.Combine(immExtractedModsFolder, immMod.FolderName);
        if (!Directory.Exists(sourceFolder))
        {
            return new ImmMigratedMod(listName, ImmMigrationOutcome.NotFoundOnDisk, null, null);
        }

        try
        {
            // Resolved BEFORE importing so the source can be recorded in one write, rather than
            // importing and then mutating metadata straight afterwards.
            var catalogEntry = MatchCatalog(catalog, immMod.Name, immMod.Author);

            var imported = ImportFrom(sourceFolder, catalogEntry);
            if (imported is null)
            {
                return new ImmMigratedMod(listName, ImmMigrationOutcome.Failed, null, "no .EXMOD or .pak inside its folder");
            }

            if (catalogEntry is not null)
            {
                return new ImmMigratedMod(listName, ImmMigrationOutcome.Imported, "Database", null);
            }

            var nexusSource = await TryLinkNexusAsync(imported, cancellationToken);
            return new ImmMigratedMod(listName, ImmMigrationOutcome.Imported, nexusSource, null);
        }
        catch (Exception ex)
        {
            // One unreadable mod folder shouldn't abandon the other 46.
            return new ImmMigratedMod(listName, ImmMigrationOutcome.Failed, null, ex.Message);
        }
    }

    /// <summary>Classic IMM's Extracted_Mods holds both real EXMOD mod folders and folders wrapping a prebuilt .pak — this app imports those through two different methods, so pick by what's actually inside.</summary>
    private LibraryEntry? ImportFrom(string sourceFolder, CatalogEntry? catalogEntry)
    {
        var files = Directory.EnumerateFiles(sourceFolder, "*", SearchOption.AllDirectories).ToList();

        if (files.Any(f => f.EndsWith(".EXMOD", StringComparison.OrdinalIgnoreCase)))
        {
            return repository.Import(sourceFolder, source: catalogEntry is null ? null : "Database", catalogEntryId: catalogEntry?.Id);
        }

        var pakPath = files.FirstOrDefault(f => f.EndsWith(".pak", StringComparison.OrdinalIgnoreCase));
        return pakPath is null
            ? null
            : repository.ImportPak(pakPath, source: catalogEntry is null ? null : "Database", catalogEntryId: catalogEntry?.Id);
    }

    private LibraryEntry? FindInLibrary(string listName, ImmExtractedMod? immMod)
    {
        // Name-based matching only — LibraryEntry.FolderName (this app's own generated folder
        // name, derived from the EXMOD's own FileName field) and ImmExtractedMod.FolderName
        // (classic IMM's own separate on-disk extraction folder name) are two independent
        // identifier spaces with no guaranteed relationship, so comparing them (a prior version of
        // this method did) doesn't reliably match anything real — it's not a fallback worth having.
        // If a mod's display name has drifted since it was last migrated (e.g. renamed here via
        // Library's own Rename, or classic IMM's own cache updated), this legitimately won't find
        // it and a re-migration will re-import it as a second entry; the durable fix would be
        // recording classic-IMM provenance at import time the same way CatalogEntryId/NexusModId
        // already track catalog/Nexus provenance — not done here, since that's a real schema
        // addition, not a one-line correctness fix.
        var entries = repository.GetAll();
        return entries.FirstOrDefault(e => string.Equals(e.Name, listName, StringComparison.OrdinalIgnoreCase))
            ?? (immMod is null
                ? null
                : entries.FirstOrDefault(e => string.Equals(e.Name, immMod.Name, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Links an already-present mod that has no source yet. Returns the source it was linked to, or null if it stayed unlinked (already linked mods are left exactly as they are).</summary>
    private async Task<string?> LinkSourceAsync(LibraryEntry entry, string author, IReadOnlyList<CatalogEntry> catalog, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(entry.Source))
        {
            return null;
        }

        if (MatchCatalog(catalog, entry.Name, author) is { } catalogEntry)
        {
            repository.SetCatalogEntry(entry.FolderName, catalogEntry.Id);
            return "Database";
        }

        return await TryLinkNexusAsync(entry, cancellationToken);
    }

    private static CatalogEntry? MatchCatalog(IReadOnlyList<CatalogEntry> catalog, string name, string author)
    {
        var key = CatalogKey.Normalize(name, author);
        return catalog.FirstOrDefault(e => CatalogKey.Normalize(e.Name, e.Author) == key)
            // Author is frequently blank or differently-spelled in classic IMM's own cache
            // ("Unknown", ""), so fall back to a name-only match rather than missing a real hit.
            ?? catalog.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Nexus has no "which mod is this file from" lookup, so the only available signal is a search
    /// by name — accepted ONLY on an exact (case-insensitive) title match, since a fuzzy hit would
    /// silently attach a mod to the wrong Nexus page and then offer its updates.
    /// </summary>
    private async Task<string?> TryLinkNexusAsync(LibraryEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            var apiKey = credentialStore.Read(CredentialTargets.NexusApiKey);
            var matches = await nexusApiClient.SearchModsAsync(apiKey, "icarus", entry.Name, cancellationToken);
            var exact = matches.FirstOrDefault(m => string.Equals(m.Name, entry.Name, StringComparison.OrdinalIgnoreCase));
            if (exact is null)
            {
                return null;
            }

            repository.LinkToNexus(entry.FolderName, exact.ModId);
            return "Nexus";
        }
        catch (Exception)
        {
            // Offline, rate-limited, or Nexus changed its API — a mod without a Nexus link still
            // migrated fine, so this never fails the migration.
            return null;
        }
    }

    /// <summary>
    /// Returns which sources failed alongside the (partial) catalog, matching the same
    /// per-source-isolated fetch DownloadsViewModel.RefreshCatalogAsync already uses — a caller that
    /// silently discarded failedSources here would leave every mod that would have matched the
    /// down source unlinked with no indication a retry might fix it (a real bug this once was).
    /// </summary>
    private async Task<(IReadOnlyList<CatalogEntry> Catalog, IReadOnlyList<string> FailedSources)> FetchCatalogAsync()
    {
        var failedSources = new List<string>();
        var daedalusTask = CatalogSourceFetch.FetchAsync(daedalusClient.FetchAsync, "Daedalus", failedSources);
        var jimk72Task = CatalogSourceFetch.FetchAsync(jimk72Client.FetchAsync, "Jimk72", failedSources);
        await Task.WhenAll(daedalusTask, jimk72Task);
        return ([.. daedalusTask.Result, .. jimk72Task.Result], failedSources);
    }
}
