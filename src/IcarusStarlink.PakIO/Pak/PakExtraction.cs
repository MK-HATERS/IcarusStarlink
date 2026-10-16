namespace IcarusStarlink.PakIO.Pak;

public static class PakExtraction
{
    /// <summary>
    /// Extracts a pak and puts every file where the game sees it under Content (see
    /// PakMountPoints.RestageUnderGameContent) — the one way every reader of a prebuilt pak's
    /// contents (import conversion, merge, conflict preview, compare) should extract, so a pak
    /// mounted at "…/Content/data/Traits/" is read as data/Traits/D_Itemable.json like any other.
    /// Returns the files that mount outside Content (left at the folder root).
    /// </summary>
    public static async Task<IReadOnlyList<string>> ExtractUnderGameContentAsync(
        this IUnrealPakService unrealPakService, string unrealPakExePath, string pakFilePath, string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        await unrealPakService.ExtractPakAsync(unrealPakExePath, pakFilePath, outputDirectory, cancellationToken);
        var mountPoint = await unrealPakService.TryGetMountPointAsync(unrealPakExePath, pakFilePath, cancellationToken);
        return PakMountPoints.RestageUnderGameContent(outputDirectory, mountPoint);
    }
}
