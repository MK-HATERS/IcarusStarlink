namespace IcarusStarlink.PakIO.Rebuild;

public sealed record RebuildResult(
    int MergedFileCount,
    int PackedFileCount,
    string OutputPakPath,
    string ManifestPath,
    IReadOnlyList<string> Warnings,
    /// <summary>Non-failures worth showing — chiefly "this mod created an item the game's current data doesn't have", which is normal for add-content mods and the one visible symptom of a stale one.</summary>
    IReadOnlyList<string> Notes);
