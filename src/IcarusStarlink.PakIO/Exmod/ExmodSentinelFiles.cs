namespace IcarusStarlink.PakIO.Exmod;

/// <summary>
/// CurrentFile values that are structural markers in the EXMOD format, not real game file
/// references — checked wherever a row is about to be resolved against real base game data, so a
/// marker doesn't get misreported as a broken/invalid reference.
/// </summary>
internal static class ExmodSentinelFiles
{
    /// <summary>
    /// A real, universal terminator marker — confirmed present at the end of every one of dozens of
    /// real EXMOD files inspected, always with an empty File_Items — not a game file reference at
    /// all, just a "this is the end of the mod" sentinel every known extraction tool appends.
    /// Resolving it against base game data can only ever fail (there is no "EndOfMod" table),
    /// producing a "no matching base file" warning that reads as a real problem with the mod when
    /// it's actually universal and harmless.
    /// </summary>
    public static bool IsEndOfModMarker(string currentFile) =>
        string.Equals(currentFile, "EndOfMod", StringComparison.OrdinalIgnoreCase);
}
