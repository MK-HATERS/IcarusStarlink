using System.Text.RegularExpressions;

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

    /// <summary>
    /// The merged pak's file name, made to load after every other pak in the mods folder by both
    /// rules the game uses: paks of equal priority load in name order (hence the "ZZ_" mods use;
    /// lowercase "zz_zz_" sorts after "ZZ_ArcFission_…" and other "ZZ_"/"zz_" mods, whether case counts or not), and a
    /// "…_&lt;n&gt;_P.pak" ranks by n (Unreal's patch-chunk order, which ArcFission Library relies on).
    /// ArcFission Library's own ZZ_ArcFission_Data_&lt;n&gt;_P.pak copies whole tables from the
    /// game's data, so the merge has to load after it (its rows are folded into the merge).
    /// </summary>
    public const string MergedPakFileName = "zz_zz_ISL-Merged_9999_P.pak";

    /// <summary>The merged pak's earlier name(s), cleared wherever the merged pak is installed.</summary>
    public static readonly IReadOnlyList<string> LegacyMergedPakFileNames = ["ISL-Merged_P.pak", "ISL-Merged_9999_P.pak"];

    private static readonly Regex ArcFissionDataPak = new(@"^ZZ_ArcFission_Data_(\d+)_P\.pak$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// ArcFission Library (a UE4SS script library ArcFission mods like WirelessPower need) writes
    /// this pak itself at game start, from the game's own data plus its mods' rows. It belongs to
    /// that library: Install leaves it alone (deleting it only makes the library write it again),
    /// and its rows are merged in, so the merge outranking it loses nothing.
    /// </summary>
    public static bool IsArcFissionDataPak(string fileName) => ArcFissionDataPak.IsMatch(fileName);

    /// <summary>The newest ArcFission data pak in a mods folder (the highest number is the one the game uses), or null.</summary>
    public static string? NewestArcFissionDataPak(string modsFolder)
    {
        if (!Directory.Exists(modsFolder))
        {
            return null;
        }

        return Directory.EnumerateFiles(modsFolder, "*.pak")
            .Select(path => (Path: path, Match: ArcFissionDataPak.Match(Path.GetFileName(path))))
            .Where(p => p.Match.Success)
            .OrderByDescending(p => long.TryParse(p.Match.Groups[1].Value, out var n) ? n : 0)
            .Select(p => p.Path)
            .FirstOrDefault();
    }
}
