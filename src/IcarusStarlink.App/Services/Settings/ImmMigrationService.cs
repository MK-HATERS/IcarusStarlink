using System.IO;
using IcarusStarlink.App.Utilities;
using IcarusStarlink.Catalog;
using IcarusStarlink.Catalog.Daedalus;
using IcarusStarlink.Catalog.Jimk72;
using IcarusStarlink.Catalog.Nexus;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Migration;
using IcarusStarlink.Core.Secrets;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Import;

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
        ImmMigrationOutcome.Imported => $"{ListName} — brought over{SourceSuffix}{NoteSuffix}",
        ImmMigrationOutcome.AlreadyInLibrary => $"{ListName} — already in your Library{SourceSuffix}{NoteSuffix}",
        ImmMigrationOutcome.NotFoundOnDisk => $"{ListName} — not found in classic IMM's own mods folder",
        _ => $"{ListName} — couldn't import: {Detail}",
    };

    private string SourceSuffix => Source is null ? "" : $" (linked to {Source})";

    /// <summary>On a mod that did come over: an import warning, or a link that couldn't be made.</summary>
    private string NoteSuffix => string.IsNullOrWhiteSpace(Detail) ? "" : $"; {Detail}";
}

public sealed record ImmMigrationResult(IReadOnlyList<ImmMigratedMod> Mods, IReadOnlyList<string> FailedCatalogSources)
{
    public int ImportedCount => Mods.Count(m => m.Outcome == ImmMigrationOutcome.Imported);
    public int AlreadyPresentCount => Mods.Count(m => m.Outcome == ImmMigrationOutcome.AlreadyInLibrary);
    public int MissingCount => Mods.Count(m => m.Outcome == ImmMigrationOutcome.NotFoundOnDisk);
    public int FailedCount => Mods.Count(m => m.Outcome == ImmMigrationOutcome.Failed);
    public int LinkedToDatabaseCount => Mods.Count(m => m.Source == "Database");
    public int LinkedToNexusCount => Mods.Count(m => m.Source == "Nexus");

    /// <summary>Mods that came over (or were already here) with something to say on their line: an import warning, or a link that couldn't be made.</summary>
    public int WarningCount => Mods.Count(m => m.Outcome is ImmMigrationOutcome.Imported or ImmMigrationOutcome.AlreadyInLibrary && !string.IsNullOrWhiteSpace(m.Detail));
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
    ICredentialStore credentialStore,
    IPrebuiltPakImporter prebuiltPakImporter,
    ISettingsService settingsService,
    string dataFolder)
{
    // FolderLibraryRepository can register an already-read package, so reading a mod moves off the
    // UI thread; registering stays on the caller's thread because the Library's cache and search
    // index are only used from there. (AppServices registers the same instance under both.)
    private readonly IExmodPackageImporter? _packageImporter = repository as IExmodPackageImporter;

    public async Task<ImmMigrationResult> MigrateAsync(
        string modListPath, string installRoot, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        // Real IMM lists repeat names; the second copy would find the first one's import and count
        // as "already here". Deduped here, not in ParseNames, which Merge and Server share.
        var listNames = ModListText.ParseNames(await File.ReadAllTextAsync(modListPath, cancellationToken))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
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
            // Lets the window draw that line and answer clicks between mods; Task.Yield isn't
            // enough under WPF (its continuation runs ahead of drawing and input).
            await Task.Delay(1, cancellationToken);
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
            try
            {
                var linkedSource = await LinkSourceAsync(existing, immMod?.Author ?? existing.Author, catalog, cancellationToken);
                return new ImmMigratedMod(listName, ImmMigrationOutcome.AlreadyInLibrary, linkedSource ?? existing.Source, null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Its Library_Meta file locked (OneDrive, antivirus) or its folder gone: the mod is
                // still in the Library, so this is a note on its line, not a failed import, and the
                // rest of the run goes on.
                return new ImmMigratedMod(listName, ImmMigrationOutcome.AlreadyInLibrary, existing.Source,
                    $"couldn't link it to the mod database or Nexus ({ex.Message})");
            }
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

            var imported = await ImportFrom(sourceFolder, catalogEntry);
            if (imported is null)
            {
                return new ImmMigratedMod(listName, ImmMigrationOutcome.Failed, null, "no .EXMOD or .pak inside its folder");
            }

            // A pak mod that came over as a prebuilt pak (no game data extracted yet, say) carries
            // why it won't merge field by field; the Library and Downloads show this too.
            var warning = imported.ImportWarnings.Count > 0 ? "warning: " + string.Join(" ", imported.ImportWarnings) : null;

            if (catalogEntry is not null)
            {
                return new ImmMigratedMod(listName, ImmMigrationOutcome.Imported, "Database", warning);
            }

            var nexusSource = await TryLinkNexusAsync(imported, immMod.Author, cancellationToken);
            return new ImmMigratedMod(listName, ImmMigrationOutcome.Imported, nexusSource, warning);
        }
        catch (Exception ex)
        {
            // One unreadable mod folder shouldn't abandon the other 46.
            return new ImmMigratedMod(listName, ImmMigrationOutcome.Failed, null, ex.Message);
        }
    }

    /// <summary>Classic IMM's Extracted_Mods holds both real EXMOD mod folders and folders wrapping a prebuilt .pak — this app imports those through two different methods, so pick by what's actually inside.</summary>
    private async Task<LibraryEntry?> ImportFrom(string sourceFolder, CatalogEntry? catalogEntry)
    {
        var files = await Task.Run(() => Directory.EnumerateFiles(sourceFolder, "*", SearchOption.AllDirectories).ToList());

        if (files.Any(f => f.EndsWith(".EXMOD", StringComparison.OrdinalIgnoreCase)))
        {
            var source = catalogEntry is null ? null : "Database";
            if (_packageImporter is not null)
            {
                // Reading every file of the mod is the slow part; the same registration Import does
                // after its own read then runs here, on the caller's thread.
                var contents = await Task.Run(() => ExmodFolder.Read(sourceFolder));
                return _packageImporter.ImportPackage(contents, source, catalogEntryId: catalogEntry?.Id);
            }

            return repository.Import(sourceFolder, source: source, catalogEntryId: catalogEntry?.Id);
        }

        var pakPath = files.FirstOrDefault(f => f.EndsWith(".pak", StringComparison.OrdinalIgnoreCase));
        return pakPath is null
            ? null
            : await prebuiltPakImporter.ImportAsync(
                pakPath, dataFolder, settingsService.Current.UnrealPakExePath,
                source: catalogEntry is null ? null : "Database", catalogEntryId: catalogEntry?.Id,
                name: catalogEntry?.Name, author: catalogEntry?.Author);
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

        return await TryLinkNexusAsync(entry, author, cancellationToken);
    }

    /// <summary>
    /// The catalog entry with the same name and author. Author is often blank or "Unknown" in
    /// classic IMM's own cache, so a name-only match is taken too, but only when every catalog
    /// entry with that name is by the same author: two sources listing one mod still link, while
    /// two authors' same-named mods link to neither.
    /// </summary>
    private static CatalogEntry? MatchCatalog(IReadOnlyList<CatalogEntry> catalog, string name, string author)
    {
        var key = CatalogKey.Normalize(name, author);
        if (catalog.FirstOrDefault(e => CatalogKey.Normalize(e.Name, e.Author) == key) is { } exact)
        {
            return exact;
        }

        var sameName = catalog.Where(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        return sameName.Count > 0 && sameName.Select(e => CatalogKey.Normalize(e.Name, e.Author).Author).Distinct().Count() == 1
            ? sameName[0]
            : null;
    }

    /// <summary>Null when blank or "Unknown" (what classic IMM's cache says when it doesn't know), else trimmed.</summary>
    private static string? KnownAuthor(string? author) =>
        string.IsNullOrWhiteSpace(author) || string.Equals(author.Trim(), "Unknown", StringComparison.OrdinalIgnoreCase)
            ? null
            : author.Trim();

    /// <summary>
    /// Nexus has no "which mod is this file from" lookup, so the only available signal is a search
    /// by name — accepted ONLY on an exact (case-insensitive) title match, since a fuzzy hit would
    /// silently attach a mod to the wrong Nexus page and then offer its updates. When several mods
    /// have that exact title, only the one by the same author (classic IMM's, else the mod's own)
    /// is taken; with no known author, none is.
    /// </summary>
    private async Task<string?> TryLinkNexusAsync(LibraryEntry entry, string? immAuthor, CancellationToken cancellationToken)
    {
        try
        {
            var apiKey = credentialStore.Read(CredentialTargets.NexusApiKey);
            var matches = await nexusApiClient.SearchModsAsync(apiKey, "icarus", entry.Name, cancellationToken);
            var exactTitles = matches.Where(m => string.Equals(m.Name, entry.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            var author = KnownAuthor(immAuthor) ?? KnownAuthor(entry.Author);
            var exact = exactTitles.Count switch
            {
                1 => exactTitles[0],
                > 1 when author is not null
                    && exactTitles.Where(m => string.Equals(m.Author?.Trim(), author, StringComparison.OrdinalIgnoreCase)).ToList() is [var byAuthor] => byAuthor,
                _ => null,
            };
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
