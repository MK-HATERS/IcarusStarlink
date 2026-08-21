namespace IcarusStarlink.PakIO;

/// <summary>
/// Filenames for the two small manifests this app writes into the real game folder — shared
/// between the services that write them (RebuildService, InstallService) and the one that reads
/// them back for the installed-vs-list comparison (6.6), so a rename can't silently drift out of
/// sync between a writer and a reader.
/// </summary>
internal static class InstallManifestNames
{
    /// <summary>"ISL-" (IcarusStarlink) rather than classic IMM's own "IMM_Merged_Mod" naming, so the two tools' output can't collide if a user ever has both installed side by side. Sits alongside the pak in Content\Paks\mods.</summary>
    public const string PakManifest = "ISL-Merged.txt";

    /// <summary>Sits inside the real UE4SS Mods folder (Binaries\Win64\ue4ss\Mods) — the folder names this app itself installed there, so a later sync/remove/compare can tell those apart from the framework's own built-in mods or anything installed by hand.</summary>
    public const string Ue4ssInstalledMods = "ISL-Installed-Mods.txt";
}
