namespace IcarusStarlink.PakIO.Compare;

public interface IPakCompareService
{
    /// <summary>
    /// Extracts both paks (to temp folders, cleaned up afterwards) and compares their contents
    /// file by file: DataTable JSONs get a field-level diff (reusing the same TableDiffer engine
    /// the merge/editor pipelines use), everything else — binary assets, and JSONs that aren't
    /// DataTables — is compared by raw content. Throws on any failure (missing exe/pak, UnrealPak
    /// failing), same convention as IUnrealPakService's own methods.
    /// </summary>
    Task<PakCompareResult> CompareAsync(
        string unrealPakExePath, string firstPakPath, string secondPakPath, CancellationToken cancellationToken = default);
}
