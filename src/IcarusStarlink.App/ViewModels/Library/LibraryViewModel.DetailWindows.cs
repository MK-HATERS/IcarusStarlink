namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// The rows open details windows show (ModDetailWindow tells the page as it moves between mods
/// with Prev/Next). A Library change clears every row's details, so these are read again right
/// after it; the list's own selection loads nothing, as nothing on the page shows the details.
/// </summary>
public sealed partial class LibraryViewModel
{
    /// <summary>One entry per open window: two windows on the same mod keep it here until both close.</summary>
    private readonly List<LibraryItemViewModel> _rowsInDetailWindows = [];

    /// <summary>A details window now shows this row.</summary>
    internal void DetailWindowShows(LibraryItemViewModel item) => _rowsInDetailWindows.Add(item);

    /// <summary>A details window moved away from this row, or closed.</summary>
    internal void DetailWindowStopsShowing(LibraryItemViewModel item) => _rowsInDetailWindows.Remove(item);

    internal bool IsShownInDetailWindow(LibraryItemViewModel item) => _rowsInDetailWindows.Contains(item);

    /// <summary>
    /// A details window closed or moved away from this row: when no other window still shows it,
    /// its preview (image, mesh, material, sound) is let go. Rows stay cached for the session, so a
    /// decoded texture used to stay in memory until the app closed.
    /// </summary>
    internal void DetailWindowLeaves(LibraryItemViewModel item)
    {
        DetailWindowStopsShowing(item);
        if (!IsShownInDetailWindow(item))
        {
            item.ReleasePreview();
        }
    }

    /// <summary>
    /// Reads the details of every row a window shows again, after a change cleared them. A row no
    /// longer in the Library (deleted; its window closes itself) is skipped. A row already loaded
    /// costs nothing.
    /// </summary>
    private void ReloadOpenDetails()
    {
        foreach (var item in _rowsInDetailWindows.Distinct().ToList())
        {
            if (_itemsByFolderName.TryGetValue(item.FolderName, out var cached) && ReferenceEquals(cached, item))
            {
                item.EnsureDetailsLoaded();
            }
        }
    }
}
