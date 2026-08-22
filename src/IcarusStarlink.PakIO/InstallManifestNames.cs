namespace IcarusStarlink.PakIO;

/// <summary>
/// Filename for the small manifest this app writes into the real game folder — shared between the
/// services that write it (RebuildService, InstallService), the one that reads it back for the
/// installed-vs-list comparison (6.6), and Storage's FolderLibraryRepository (recognizing a
/// re-imported pak as one of this app's own merged packs) — so a rename can't silently drift out
/// of sync between a writer and any of its readers. Public (not internal) for that last case
/// specifically: Storage depends on PakIO already, but has no InternalsVisibleTo grant, and
/// duplicating this filename as a second string literal would reintroduce exactly the drift risk
/// this class exists to prevent. UE4SS mods have no equivalent manifest (Phase 8.5) — a mod's real
/// presence in the game's own Mods folder IS its state, nothing else to track.
/// </summary>
public static class InstallManifestNames
{
    /// <summary>"ISL-" (IcarusStarlink) rather than classic IMM's own "IMM_Merged_Mod" naming, so the two tools' output can't collide if a user ever has both installed side by side. Sits alongside the pak in Content\Paks\mods.</summary>
    public const string PakManifest = "ISL-Merged.txt";
}
