namespace IcarusStarlink.PakIO.Pak;

public interface IUnrealPakService
{
    /// <summary>
    /// Extracts Content\Data\data.pak (the one pak that holds the game's gameplay DataTable JSON —
    /// see UnrealPakService's own doc comment for how that path was confirmed against a real
    /// install) into outputDirectory, replacing whatever was there from a previous run.
    /// Throws on any failure (missing exe, missing pak, UnrealPak itself failing) — same
    /// throw-and-let-the-caller's-UI-boundary-catch-it convention as ILibraryRepository/
    /// IDaedalusCatalogClient/etc. elsewhere in this app.
    /// </summary>
    Task<UnrealPakExtractResult> ExtractDataPakAsync(
        string unrealPakExePath, string icarusContentPath, string outputDirectory, CancellationToken cancellationToken = default);
}

public sealed record UnrealPakExtractResult(int ExtractedFileCount);
