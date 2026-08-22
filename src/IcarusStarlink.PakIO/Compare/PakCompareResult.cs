using IcarusStarlink.PakIO.DataChanges;

namespace IcarusStarlink.PakIO.Compare;

public enum PakAssetDifferenceKind
{
    OnlyInFirst,
    OnlyInSecond,
    DifferentContent,
}

/// <summary>One non-DataTable file (binary asset, or a JSON that isn't a DataTable) that differs between the two paks. Files identical on both sides are never reported.</summary>
public sealed record PakAssetDifference(string RelativePath, PakAssetDifferenceKind Kind);

/// <summary>
/// The full difference between two paks. DataDifferences reuses Weekly Changes' own
/// ChangedDataFile shape (both are "two versions of the same DataTable JSON" comparisons), read as
/// the SECOND pak relative to the FIRST — IsNewFile/IsNewItem means "only in the second pak",
/// IsRemovedFile/RemovedRowNames means "only in the first".
/// </summary>
public sealed record PakCompareResult(
    IReadOnlyList<ChangedDataFile> DataDifferences,
    IReadOnlyList<PakAssetDifference> AssetDifferences,
    int FirstFileCount,
    int SecondFileCount);
