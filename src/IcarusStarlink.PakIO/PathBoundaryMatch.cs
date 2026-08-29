namespace IcarusStarlink.PakIO;

/// <summary>
/// Shared by CueAssetProviderLocator and RebuildService — both need "does this longer path end
/// with a real path-segment-bounded copy of this shorter one," not a bare substring EndsWith
/// (which would wrongly match "OldVersions/Weapons/Ammo.uasset" against "Weapons/Ammo.uasset", or
/// a packed "Icon.png" against an unrelated staged "MyIcon.png"). Requires the character
/// immediately before the matched suffix to be a genuine '/' separator, or the suffix to consume
/// the whole string.
/// </summary>
internal static class PathBoundaryMatch
{
    public static bool EndsWithSegmentBoundary(string longerPath, string shorterPath)
    {
        if (longerPath.Length == shorterPath.Length)
        {
            return longerPath.Equals(shorterPath, StringComparison.OrdinalIgnoreCase);
        }

        return longerPath.Length > shorterPath.Length
            && longerPath.EndsWith(shorterPath, StringComparison.OrdinalIgnoreCase)
            && longerPath[^(shorterPath.Length + 1)] == '/';
    }
}
