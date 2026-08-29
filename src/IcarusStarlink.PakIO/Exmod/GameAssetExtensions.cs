namespace IcarusStarlink.PakIO.Exmod;

/// <summary>
/// Real, compiled Unreal binary assets are always .uasset/.uexp/.ubulk — confirmed repeatedly
/// throughout this project's own history (CueUassetTextureDecoder/CueUassetStaticMeshDecoder,
/// ExmodStalenessChecker's own asset-correlation, ExmodAssetEntry's own doc comment). Everything
/// else a mod folder's Assets list can legitimately contain — a Readme.txt, an "ImageOnly.png"
/// thumbnail this app's own Library reads, a promotional banner some mod-packaging convention
/// includes — is real content worth keeping in the mod's own folder for Library display, but has
/// no business being packed into the actual merged .pak file Icarus loads: Icarus's engine never
/// reads it, so packing it is pure wasted space, and identically-named files across unrelated mods
/// (confirmed live: "Banner.PNG"/"Readme.txt"/"ImageOnly.png" recurring across several real,
/// unrelated mods in the user's own library) silently overwrite each other for no reason.
/// </summary>
public static class GameAssetExtensions
{
    private static readonly HashSet<string> RealGameAssetExtensions = new(StringComparer.OrdinalIgnoreCase) { ".uasset", ".uexp", ".ubulk" };

    public static bool IsRealGameAsset(string relativePath) => RealGameAssetExtensions.Contains(Path.GetExtension(relativePath));
}
