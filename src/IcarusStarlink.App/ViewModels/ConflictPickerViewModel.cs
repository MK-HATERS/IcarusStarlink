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
        IReadOnlyDictionary<(string CurrentFile, string ItemName, string FieldName), int>? existingPicks,
        IReadOnlyList<NewItemNameCollision>? newItemCollisions = null)
    {
        // Collisions first — a "two unrelated new items got merged into one" surprise is worth
        // seeing before the ordinary per-field picks below.
        var rows = new List<ConflictRowViewModel>();
        foreach (var collision in newItemCollisions ?? [])
        {
            rows.Add(new ConflictRowViewModel(collision));
        }

        foreach (var conflict in conflicts)
        {
            var key = (conflict.CurrentFile, conflict.ItemName, conflict.FieldName);
            var existingPick = existingPicks is not null && existingPicks.TryGetValue(key, out var index) ? (int?)index : null;
            rows.Add(new ConflictRowViewModel(conflict, existingPick));
        }

        Rows = rows;
    }

    public IReadOnlyList<ConflictRowViewModel> Rows { get; }

    /// <summary>
    /// Only rows with an actual manual pick are included — "Default" means absent from the
    /// dictionary, matching MergeEngine.Merge's own manualPicks semantics; a NewItemNameCollision
    /// row (Conflict null) never has one, since there's no (file, item, field) key it could mean —
    /// filtered out explicitly rather than relying solely on PickedCandidateIndex staying null, so
    /// the row.Conflict! below is never reached for one. Built with FieldChangeKeyComparer, the same
    /// comparer MergeEngine.Merge/FindConflicts group through (case-insensitive on CurrentFile) — a
    /// plain ToDictionary here would default to case-sensitive tuple equality, and MergeEngine.Merge
    /// looks up a key using THIS dictionary's own comparer, not its own, so a CurrentFile casing
    /// mismatch between when a pick is recorded and when Merge runs would silently make the pick a
    /// no-op instead of an error.
    /// </summary>
    public IReadOnlyDictionary<(string CurrentFile, string ItemName, string FieldName), int> BuildPicks() =>
        Rows.Where(row => row.Conflict is not null && row.PickedCandidateIndex.HasValue)
            .ToDictionary(
                row => (row.Conflict!.CurrentFile, row.Conflict.ItemName, row.Conflict.FieldName),
                row => row.PickedCandidateIndex!.Value,
                FieldChangeKeyComparer.Instance);
}
