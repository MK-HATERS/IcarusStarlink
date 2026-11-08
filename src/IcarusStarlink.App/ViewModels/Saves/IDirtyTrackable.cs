namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// A Saves page row that keeps what it loaded, says whether it changed (IsDirty) and takes its
/// current value as the new saved one (MarkClean), so SavesViewModel.HasUnsavedChanges/SaveChanges
/// can walk every section's rows the same way.
/// </summary>
public interface IDirtyTrackable
{
    bool IsDirty { get; }

    void MarkClean();
}
