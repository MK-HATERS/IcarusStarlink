namespace IcarusStarlink.PakIO.Rebuild;

public sealed record RebuildResult(
    int MergedFileCount,
    int PackedFileCount,
    string OutputPakPath,
    string ManifestPath,
    IReadOnlyList<string> Warnings);
