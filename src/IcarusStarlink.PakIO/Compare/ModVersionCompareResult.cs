using IcarusStarlink.PakIO.DataChanges;

namespace IcarusStarlink.PakIO.Compare;

/// <summary>
/// What a mod author actually changed between two versions of one mod. Reuses ChangedDataFile and
/// PakAssetDifference so the same UI renders this and a raw pak-vs-pak comparison — read as the
/// NEW version relative to the OLD one (IsNewFile/IsNewItem = the new version added it,
/// IsRemovedFile/RemovedRowNames = the new version dropped it).
/// </summary>
public sealed record ModVersionCompareResult(
    string OldLabel,
    string NewLabel,
    IReadOnlyList<ChangedDataFile> DataDifferences,
    IReadOnlyList<PakAssetDifference> AssetDifferences)
{
    public bool IsIdentical => DataDifferences.Count == 0 && AssetDifferences.Count == 0;
}
