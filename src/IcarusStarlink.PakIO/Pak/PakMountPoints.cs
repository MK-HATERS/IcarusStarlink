namespace IcarusStarlink.PakIO.Pak;

/// <summary>
/// Where a pak's files land inside the game's content tree. The merged pak this app builds mounts
/// everything at GameContent ("../../../Icarus/Content/", the root every "/Game/…" asset path
/// resolves under). A prebuilt pak can carry a DEEPER mount point — found live: WirelessPower's
/// model pak mounts at "../../../Icarus/Content/Mods/WirelessPower/", and UnrealPak -Extract
/// writes its files flat relative to that mount, so folding them into the merged pak at the root
/// put "SM_WirelessNode_Receiver" at /Game/… instead of /Game/Mods/WirelessPower/… and the mod's
/// models silently never loaded. RelativePrefixUnderGameContent gives the subfolder those files
/// have to be restaged under so the merged pak mounts them where the original pak did.
/// </summary>
public static class PakMountPoints
{
    public const string GameContent = "../../../Icarus/Content/";

    /// <summary>
    /// "../../../Icarus/Content/Mods/WirelessPower/" → "Mods/WirelessPower/"; the content root itself,
    /// null, or anything not under the content root → "" (fold at the root, the prior behavior —
    /// a pak mounting outside Content can't be represented in the merged pak at all).
    /// </summary>
    public static string RelativePrefixUnderGameContent(string? mountPoint)
    {
        if (string.IsNullOrWhiteSpace(mountPoint))
        {
            return "";
        }

        var normalized = mountPoint.Trim().Replace('\\', '/');
        if (!normalized.EndsWith('/'))
        {
            normalized += "/";
        }

        if (!normalized.StartsWith(GameContent, StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        var remainder = normalized[GameContent.Length..];
        return remainder == "/" ? "" : remainder;
    }

    /// <summary>True when the mount point is somewhere this app's merged pak can't reproduce (outside the game's Content tree).</summary>
    public static bool IsOutsideGameContent(string? mountPoint) =>
        !string.IsNullOrWhiteSpace(mountPoint)
        && !mountPoint.Trim().Replace('\\', '/').StartsWith(GameContent, StringComparison.OrdinalIgnoreCase);
}
