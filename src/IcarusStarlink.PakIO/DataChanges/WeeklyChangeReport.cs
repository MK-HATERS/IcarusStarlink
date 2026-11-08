using IcarusStarlink.Diffing;

namespace IcarusStarlink.PakIO.DataChanges;

/// <summary>
/// What changed in the game's own DataTable JSON between two "Refresh game data" runs — mirrors
/// classic IMM's own "This Weeks Changes" feature (confirmed from a real install:
/// Extracted_Mods\This_Weeks_Changes\This_Weeks_Changes.EXMOD), but kept as its own dedicated
/// report/page here rather than a synthetic library entry, since a fake "mod" cluttering the real
/// mod list is a workaround for not having a purpose-built page, not a design worth copying.
/// </summary>
public sealed record WeeklyChangeReport(
    DateTimeOffset PreviousUpdateAt,
    DateTimeOffset CurrentUpdateAt,
    IReadOnlyList<ChangedDataFile> ChangedFiles)
{
    public string? GameBuildId { get; init; }
}

/// <param name="RemovedRowNames">
/// Whole rows present in the previous extraction but absent from this one. TableDiffer can't see
/// these on its own — see DataFolderChangeTracker's own doc comment for why — so they're tracked
/// as a separate list rather than folded into FieldChanges.
/// </param>
public sealed record ChangedDataFile(
    string RelativePath,
    bool IsNewFile,
    bool IsRemovedFile,
    IReadOnlyList<string> RemovedRowNames,
    IReadOnlyList<FieldChange> FieldChanges)
{
    /// <summary>
    /// The parts of the table outside its rows that changed ("Defaults", the values every row
    /// inherits for fields it doesn't list, or "RowStruct"). No row diff can show these, and they
    /// are kept out of FieldChanges on purpose: readers of FieldChanges take every ItemName as a
    /// real row. Empty for a new or removed file, and in reports saved before this was recorded.
    /// </summary>
    public IReadOnlyList<string> ChangedTableParts { get; init; } = [];
}
