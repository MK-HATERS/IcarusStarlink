namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// A placeholder row Library's own list shows for a download still in progress — there's no folder
/// on disk yet to have become a real LibraryItemViewModel. A record (not a class) so two Reload()
/// passes while the same download is still active produce equal instances rather than a new
/// reference each time — ObservableCollectionSync.SyncTo compares by equality, so this keeps the
/// row stable in place instead of removing and re-inserting it on every unrelated reload.
/// </summary>
public sealed record DownloadStubViewModel(string DisplayName);
