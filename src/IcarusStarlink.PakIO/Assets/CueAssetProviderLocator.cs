using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Versions;

namespace IcarusStarlink.PakIO.Assets;

/// <summary>
/// Shared by CueUassetTextureDecoder/CueUassetStaticMeshDecoder — both need the identical "index
/// this mod folder fresh, find the one package matching a relative path, pull out its export of a
/// given type" sequence, previously duplicated line-for-line in each decoder with two real bugs:
/// the DefaultFileProvider was never disposed (it implements IDisposable — a `using var` here
/// makes that a compiler-enforced guarantee, not a comment to remember), and the path match was a
/// raw substring EndsWith with no path-boundary check, so a shorter selected path could resolve to
/// a longer, unrelated file that merely shares the same trailing path segment.
/// </summary>
internal static class CueAssetProviderLocator
{
    // GAME_UE4_27 never varies for this app (Icarus is a fixed UE4.27 title) — one shared instance
    // instead of a fresh allocation per decode call.
    private static readonly VersionContainer Versions = new(EGame.GAME_UE4_27);

    public static T? TryLoadExport<T>(string modFolderPath, string relativeAssetPath) where T : class =>
        TryLoadExportAndProject<T, T>(modFolderPath, relativeAssetPath, static export => export);

    /// <summary>
    /// Same lookup as TryLoadExport, but keeps the mod-local DefaultFileProvider alive for the
    /// duration of <paramref name="project"/> — required for any consumer that reads a lazily-
    /// resolved cross-package reference off the export (CueUassetMaterialDecoder's GetParams call
    /// resolving another package's Texture2D for a texture parameter is exactly this case).
    /// CUE4Parse resolves an FPackageIndex reference on first access, not at load time, and
    /// TryLoadExport's own `using var provider` disposes (clearing Files/VirtualPaths and every
    /// mounted VFS reader) the instant it returns — silently starving any such later resolution
    /// (it fails closed, returning null/false, never throwing) rather than a caller noticing
    /// anything went wrong. A consumer with this need can't safely call TryLoadExport and process
    /// the result afterward at all; it has to do all of that processing here, inside project,
    /// while the provider backing the export is still open.
    /// </summary>
    public static TResult? TryLoadExportAndProject<T, TResult>(
        string modFolderPath, string relativeAssetPath, Func<T, TResult> project)
        where T : class where TResult : class
    {
        using var provider = new DefaultFileProvider(modFolderPath, SearchOption.AllDirectories, Versions, StringComparer.OrdinalIgnoreCase);
        provider.Initialize();

        // CUE4Parse's own internal file keys carry a package-root prefix ahead of the plain
        // mod-relative path this app already knows (confirmed live: e.g.
        // "JimK_Weapons_Pack_1/Pistols/Textures/Pistols_A_Diff.uasset" for a caller-supplied
        // "Pistols/Textures/Pistols_A_Diff.uasset"), so an exact-equals match alone would never
        // succeed and PathBoundaryMatch's suffix tolerance is genuinely required here.
        var normalizedRelativePath = relativeAssetPath.Replace('\\', '/').TrimStart('/');
        var matchedKey = provider.Files.Keys.FirstOrDefault(key => PathBoundaryMatch.EndsWithSegmentBoundary(key, normalizedRelativePath));
        if (matchedKey is null)
        {
            return null;
        }

        var package = provider.LoadPackage(matchedKey);
        var export = package.ExportsLazy.Select(e => e.Value).OfType<T>().FirstOrDefault();
        return export is null ? null : project(export);
    }
}
