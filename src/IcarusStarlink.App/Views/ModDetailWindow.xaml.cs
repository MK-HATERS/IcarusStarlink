using System.Windows;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Views;

public partial class ModDetailWindow : Window
{
    /// <summary>Reached by the window's own XAML via RelativeSource AncestorType=Window, so the Edit/Delete/Get update buttons can invoke Library's page-level commands even though DataContext here is the mod item itself, not the page.</summary>
    public LibraryViewModel LibraryViewModel { get; }

    public ModDetailWindow(LibraryItemViewModel item, LibraryViewModel libraryViewModel)
    {
        LibraryViewModel = libraryViewModel;
        InitializeComponent();
        ShowItem(item);
        Closed += (_, _) => WeakReferenceMessenger.Default.Unregister<LibraryChangedMessage>(this);
    }

    /// <summary>Shared by the constructor and the Prev/Next buttons — swaps which mod this window
    /// is showing, including re-pointing the "auto-close if this mod stops existing" watch at the
    /// newly-shown mod's own folder rather than the one navigated away from.</summary>
    private void ShowItem(LibraryItemViewModel item)
    {
        DataContext = item;

        // Without this, deleting the mod this window is showing (via its own Delete button, or
        // from the main window's tree while this pop-out is still open) left the window sitting
        // open on stale data — its Edit/Get update buttons would then operate on a folder that no
        // longer exists, surfacing any resulting error on the MAIN window's StatusMessage while
        // this window itself gave no indication anything was wrong.
        WeakReferenceMessenger.Default.Unregister<LibraryChangedMessage>(this);
        var folderName = item.FolderName;
        WeakReferenceMessenger.Default.Register<LibraryChangedMessage>(this, (recipient, _) =>
        {
            if (!LibraryViewModel.ContainsMod(folderName))
            {
                ((ModDetailWindow)recipient).Close();
            }
        });
    }

    private void ShowPrevious_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is LibraryItemViewModel current
            && LibraryViewModel.GetAdjacentItem(current, -1) is { } previous)
        {
            ShowItem(previous);
        }
    }

    private void ShowNext_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is LibraryItemViewModel current
            && LibraryViewModel.GetAdjacentItem(current, 1) is { } next)
        {
            ShowItem(next);
        }
    }
}
