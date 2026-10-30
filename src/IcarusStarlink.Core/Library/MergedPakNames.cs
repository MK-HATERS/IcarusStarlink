namespace IcarusStarlink.Core.Library;

/// <summary>
/// Every file name Starlink's merged pak goes by in a mods folder (game, local server, FTP), in one
/// place so the install, the server copy and every clean-up recognise the same set. PakIO's
/// InstallManifestNames reads these; Core's server code can't reference PakIO.
/// </summary>
public static class MergedPakNames
{
    /// <summary>
    /// The name while ArcFission Library is on: its own ZZ_ArcFission_Data_&lt;n&gt;_P.pak holds whole
    /// tables, so the merge must load after it. Lowercase "zz_zz_" sorts after "ZZ_ArcFission_…"
    /// whether case counts or not, and "_9999_P" outranks any "_&lt;n&gt;_P" by Unreal's patch order.
    /// Also the name the merge is built under in Staged_Build.
    /// </summary>
    public const string ArcFissionName = "zz_zz_ISL-Merged_9999_P.pak";

    /// <summary>The name without ArcFission Library: nothing else is left in the mods folder to outrank (Install clears it).</summary>
    public const string PlainName = "ISL-Merged_P.pak";

    /// <summary>A name only older versions used: recognised and cleared, never written.</summary>
    public static IReadOnlyList<string> LegacyNames { get; } = ["ISL-Merged_9999_P.pak"];

    /// <summary>Every name the merged pak has gone by: what any mods folder clean-up treats as "Starlink's merged pak".</summary>
    public static IReadOnlyList<string> All { get; } = [ArcFissionName, PlainName, .. LegacyNames];

    /// <summary>The name the merged pak is installed under in a mods folder, by whether ArcFission Library is on there.</summary>
    public static string For(bool arcFissionLibraryActive) => arcFissionLibraryActive ? ArcFissionName : PlainName;

    public static bool IsMergedPak(string fileName) => All.Contains(fileName, StringComparer.OrdinalIgnoreCase);
}
