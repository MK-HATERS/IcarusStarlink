namespace IcarusStarlink.PakIO.Compare;

public interface IModVersionComparer
{
    /// <summary>
    /// Compares two copies of the same mod on disk (typically a backup taken before an update
    /// against the freshly-installed version). EXMOD mods diff at field level directly from their
    /// own packages; opaque .pak mods are unpacked and compared with the same pak-vs-pak engine
    /// (which needs unrealPakExePath — an opaque pak with no UnrealPak configured throws with a
    /// clear message rather than silently reporting "no differences").
    /// </summary>
    Task<ModVersionCompareResult> CompareAsync(
        string oldFolderPath, string newFolderPath, string? unrealPakExePath, CancellationToken cancellationToken = default);
}
