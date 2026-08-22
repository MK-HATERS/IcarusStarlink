using IcarusStarlink.Diffing;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// Transient, per-open-instance ViewModel for ConflictPickerWindow — same shape as
/// ExmodEditorViewModel (a fresh instance per open, not a DI singleton), but simpler: this window
/// is only ever opened from MergeInstallViewModel, so it's constructed directly rather than through
/// a DI factory.
/// </summary>
public sealed class ConflictPickerViewModel
{
    public ConflictPickerViewModel(
        IReadOnlyList<FieldConflict> conflicts,
        IReadOnlyDictionary<(string CurrentFile, string ItemName, string FieldName), int>? existingPicks)
    {
        Rows = [.. conflicts.Select(conflict =>
        {
            var key = (conflict.CurrentFile, conflict.ItemName, conflict.FieldName);
            var existingPick = existingPicks is not null && existingPicks.TryGetValue(key, out var index) ? (int?)index : null;
            return new ConflictRowViewModel(conflict, existingPick);
        })];
    }

    public IReadOnlyList<ConflictRowViewModel> Rows { get; }

    /// <summary>Only rows with an actual manual pick are included — "Default" means absent from the dictionary, matching MergeEngine.Merge's own manualPicks semantics.</summary>
    public IReadOnlyDictionary<(string CurrentFile, string ItemName, string FieldName), int> BuildPicks() =>
        Rows.Where(row => row.PickedCandidateIndex.HasValue)
            .ToDictionary(
                row => (row.Conflict.CurrentFile, row.Conflict.ItemName, row.Conflict.FieldName),
                row => row.PickedCandidateIndex!.Value);
}
