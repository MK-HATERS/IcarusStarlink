using System.Text.RegularExpressions;
using IcarusStarlink.Core.Server;
using IcarusStarlink.Core.Ue4ss;

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

    /// <summary>
    /// The merged pak's current name, then its earlier ones: everywhere that looks for "a merged pak
    /// this app installed" (Install, Remove, Restore, the snapshots, backup cleanup, the Library's
    /// "is this our own pack") — each used to build this list by hand.
    /// </summary>
    public static readonly IReadOnlyList<string> AllMergedPakFileNames = [MergedPakFileName, .. LegacyMergedPakFileNames];

    /// <summary>Whether a file name (with its .pak) is the merged pak under its current or an earlier name.</summary>
    public static bool IsMergedPakFile(string fileName) => AllMergedPakFileNames.Contains(fileName, StringComparer.OrdinalIgnoreCase);

    private static readonly Regex ArcFissionDataPak = new(@"^ZZ_ArcFission_Data_(\d+)_P\.pak$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// ArcFission Library (a UE4SS script library ArcFission mods like WirelessPower need) writes
    /// this pak itself at game start, from the game's own data plus its mods' rows. It belongs to
    /// that library: Install leaves it alone (deleting it only makes the library write it again),
    /// and its rows are merged in, so the merge outranking it loses nothing.
    /// </summary>
    public static bool IsArcFissionDataPak(string fileName) => ArcFissionDataPak.IsMatch(fileName);

    /// <summary>
    /// Whether ArcFission Library itself (the UE4SS mod that writes the data pak) is installed in
    /// the game and turned on: an "ArcFissionLibrary…" folder in ue4ss\Mods that has an enabled.txt
    /// or that mods.txt lists as on (either makes UE4SS load it). Its data pak is only folded into the merge
    /// and kept while this is true: a pak left behind after the library is removed holds whole
    /// tables from the game version it was written for, and folding it in would put old values back.
    /// </summary>
    public static bool IsArcFissionLibraryActive(string icarusContentPath)
    {
        try
        {
            var modsFolder = Ue4ssGamePaths.ResolveModsFolder(icarusContentPath);
            if (!Directory.Exists(modsFolder))
            {
                return false;
            }

            var modsTxt = Ue4ssModsTxt.Load(Path.Combine(modsFolder, "mods.txt"));
            foreach (var folder in Directory.GetDirectories(modsFolder))
            {
                var name = Path.GetFileName(folder);
                var letters = new string([.. name.Where(char.IsLetterOrDigit)]);
                if (!letters.StartsWith("ArcFissionLibrary", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (modsTxt.IsEnabled(name) == true || File.Exists(Path.Combine(folder, "enabled.txt")))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The newest ArcFission data pak in a mods folder (the highest number is the one the game uses), or null.</summary>
    public static string? NewestArcFissionDataPak(string modsFolder)
    {
        if (!Directory.Exists(modsFolder))
        {
            return null;
        }

        // Numbers compared as digit strings (no overflow on a huge one), then by name so "_05_"
        // and "_5_" always pick the same file.
        return Directory.EnumerateFiles(modsFolder, "*.pak")
            .Select(path => (Path: path, Match: ArcFissionDataPak.Match(Path.GetFileName(path))))
            .Where(p => p.Match.Success)
            .Select(p => (p.Path, Number: p.Match.Groups[1].Value.TrimStart('0')))
            .OrderByDescending(p => p.Number.Length)
            .ThenByDescending(p => p.Number, StringComparer.Ordinal)
            .ThenBy(p => Path.GetFileName(p.Path), StringComparer.Ordinal)
            .Select(p => p.Path)
            .FirstOrDefault();
    }

    /// <summary>
    /// ArcFission data paks in a game's or a dedicated server's mods folder other than the one its
    /// library uses now (CurrentArcFissionDataPak): file names, empty while ArcFission Library isn't
    /// installed and on there (then every one is a leftover, handled as such). Each rebuild writes
    /// the next number because the game keeps the loaded one locked, and the library can only delete
    /// the old ones while the game is closed, which it never is when the library runs.
    /// </summary>
    public static IReadOnlyList<string> OlderArcFissionDataPaks(string icarusContentPath)
    {
        var modsFolder = Path.Combine(icarusContentPath, "Paks", "mods");
        if (!Directory.Exists(modsFolder) || !IsArcFissionLibraryActive(icarusContentPath))
        {
            return [];
        }

        var current = Path.GetFileName(CurrentArcFissionDataPak(icarusContentPath));
        return [.. Directory.GetFiles(modsFolder).Select(Path.GetFileName).OfType<string>()
            .Where(n => IsArcFissionDataPak(n) && !string.Equals(n, current, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// The ArcFission data pak the game uses now: the highest number (Unreal ranks a higher n
    /// above a lower one, and the library writes each rebuild above the highest it finds). Not the
    /// gen its state.lua names: that file can be stale (the game's ArcFissionLibrary folder, state.lua
    /// with it, copied onto a dedicated server, or an older copy of the folder restored), and keeping
    /// the older pak it names made the library take that pak as up to date and serve its old tables.
    /// With the highest kept, a stale state.lua only makes the library rebuild once.
    /// </summary>
    public static string? CurrentArcFissionDataPak(string icarusContentPath) =>
        NewestArcFissionDataPak(Path.Combine(icarusContentPath, "Paks", "mods"));
}
