using IcarusStarlink.Diffing;

namespace IcarusStarlink.PakIO.DataChanges;

/// <summary>
/// What changed in the game's own DataTable JSON between two "Update data folder" runs — mirrors
/// classic IMM's own "This Weeks Changes" feature (confirmed from a real install:
/// Extracted_Mods\This_Weeks_Changes\This_Weeks_Changes.EXMOD), but kept as its own dedicated
/// report/page here rather than a synthetic library entry, since a fake "mod" cluttering the real
/// mod list is a workaround for not having a purpose-built page, not a design worth copying.
/// </summary>
public sealed record WeeklyChangeReport(
    DateTimeOffset PreviousUpdateAt,
    DateTimeOffset CurrentUpdateAt,
    IReadOnlyList<ChangedDataFile> ChangedFiles);

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
    IReadOnlyList<FieldChange> FieldChanges);
