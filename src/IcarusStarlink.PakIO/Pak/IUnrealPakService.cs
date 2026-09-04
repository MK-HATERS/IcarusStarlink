using IcarusStarlink.PakIO.DataChanges;

namespace IcarusStarlink.PakIO.Pak;

public interface IUnrealPakService
{
    /// <summary>
    /// Extracts Content\Data\data.pak (the one pak that holds the game's gameplay DataTable JSON —
    /// see UnrealPakService's own doc comment for how that path was confirmed against a real
    /// install) into outputDirectory, replacing whatever was there from a previous run.
    ///
    /// previousUpdateAt is the timestamp the caller recorded the last time this succeeded (null if
    /// this is the very first run, or the caller has no record of one) — passed in rather than
    /// inferred from outputDirectory's own existence/mtime, so a folder that happens to exist for
    /// unrelated reasons (leftover manual testing, a copy from elsewhere) can't be mistaken for a
    /// genuine prior run and diffed against by accident. When it's non-null and outputDirectory
    /// has content, a WeeklyChangeReport is computed (old extraction vs new) before the old
    /// extraction is replaced; otherwise ChangeReport is null on the result — no prior snapshot,
    /// nothing to compare.
    ///
    /// Throws on any failure (missing exe, missing pak, UnrealPak itself failing) — same
    /// throw-and-let-the-caller's-UI-boundary-catch-it convention as ILibraryRepository/
    /// IDaedalusCatalogClient/etc. elsewhere in this app.
    /// </summary>
    Task<UnrealPakExtractResult> ExtractDataPakAsync(
        string unrealPakExePath, string icarusContentPath, string outputDirectory,
        DateTimeOffset? previousUpdateAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// A SHA256 of the current data.pak on disk, for detecting "the game (or at least this pak)
    /// has changed since the last Update data folder run" without re-extracting anything — cheap,
    /// data.pak is only a few MB. Returns null rather than throwing when the file can't be read
    /// (content path not set yet, wrong path, game not installed) — this is meant for a passive
    /// background check, not a user-initiated action that should surface an error dialog.
    /// </summary>
    Task<string?> TryGetDataPakHashAsync(string icarusContentPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Packs everything under stagingDirectory into one pak at outputPakPath, mounted so the
    /// result actually loads in-game (see UnrealPakService's own doc comment for the real
    /// mechanics this was built against). Returns the number of files packed. Throws on failure —
    /// same convention as ExtractDataPakAsync.
    /// </summary>
    Task<int> CreatePakAsync(string unrealPakExePath, string stagingDirectory, string outputPakPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists a pak's own internal asset paths via `-List` — read-only, never extracts anything.
    /// For browsing an opaque .pak's contents in the Library UI, which otherwise has no EXMOD data
    /// to enumerate assets from. Throws on failure, same convention as the other methods here.
    /// </summary>
    Task<IReadOnlyList<string>> ListPakContentsAsync(string unrealPakExePath, string pakFilePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Extracts an arbitrary pak's full contents into outputDirectory, additively — confirmed live
    /// that UnrealPak -Extract writes into a pre-populated folder without disturbing what's already
    /// there, which is exactly what RebuildService relies on to fold an attached opaque/prebuilt
    /// pak's own contents into the same staging folder as the merged EXMOD output, so the final
    /// -Create produces one single pak (matching classic IMM's own real behavior) rather than a
    /// separate sidecar file. Returns the number of files extracted. Throws on failure, same
    /// convention as the other methods here.
    ///
    /// filter (optional, defaults to null): passed straight through as UnrealPak.exe's own
    /// -Filter=&lt;value&gt; argument to the real -Extract invocation when non-null — confirmed live
    /// against the real bundled UnrealPak.exe: a single exact relative path (e.g.
    /// "AI/D_AIDescriptors.json") extracts just that one file in ~3ms instead of the whole pak, and
    /// a wildcard (e.g. "AI/*") matches and extracts multiple files. Left null (the default), the
    /// flag is omitted entirely and this does a full extract exactly as before — purely additive,
    /// every existing caller is unaffected. IMPORTANT for a caller targeting one specific asset: a
    /// real asset is never just its .uasset file alone — it always has a companion .uexp, and
    /// sometimes a .ubulk too for large bulk data — so the filter passed for a single asset must be
    /// a wildcard covering the asset's own base name (e.g. "Path/To/AssetName.*"), NOT the bare
    /// "Path/To/AssetName.uasset" path by itself, or the companion file(s) needed to actually
    /// decode the asset will silently be left out of the extract.
    /// </summary>
    Task<int> ExtractPakAsync(
        string unrealPakExePath, string pakFilePath, string outputDirectory,
        CancellationToken cancellationToken = default, string? filter = null);

    /// <summary>
    /// UnrealPak's own real internal integrity check (`-Verify`), confirmed live against both a
    /// healthy real pak and a deliberately byte-corrupted copy: it re-checks every packed file's
    /// own hash, not just whether the file is present — a stronger signal than this app's own
    /// "did every staged file make it into the pak" presence-only check
    /// (RebuildService.VerifyEveryStagedFileWasActuallyPackedAsync). Unlike the other methods here,
    /// a pak failing this check is a normal, expected OUTCOME to report, not a tool failure to
    /// throw for — only a missing exe/pak file, or UnrealPak itself failing to run at all, throws.
    /// </summary>
    Task<PakVerifyResult> VerifyPakAsync(string unrealPakExePath, string pakFilePath, CancellationToken cancellationToken = default);
}

public sealed record UnrealPakExtractResult(int ExtractedFileCount, WeeklyChangeReport? ChangeReport);

public sealed record PakVerifyResult(bool IsHealthy, string Message);
