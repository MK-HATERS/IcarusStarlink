using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Core.Library;

namespace IcarusStarlink.App.Views;

public partial class MergeInstallView : UserControl
{
    private Point _queueDragStartPoint;

    public MergeInstallView()
    {
        InitializeComponent();
    }

    private void QueueList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        _queueDragStartPoint = e.GetPosition(null);

    /// <summary>
    /// WPF has no bindable/MVVM path for drag-reordering a ListBox — this is the standard
    /// code-behind shape for it: wait for the mouse to actually move past the OS's own configured
    /// drag threshold (so a plain click-to-select doesn't misfire as a drag), then hand the
    /// clicked-on row's own bound LibraryEntry to DoDragDrop.
    /// </summary>
    private void QueueList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var position = e.GetPosition(null);
        if (Math.Abs(position.X - _queueDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(position.Y - _queueDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is not { } listBoxItem
            || listBoxItem.DataContext is not LibraryEntry entry)
        {
            return;
        }

        DragDrop.DoDragDrop(listBoxItem, entry, DragDropEffects.Move);
    }

    private void QueueList_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(LibraryEntry))
            || e.Data.GetData(typeof(LibraryEntry)) is not LibraryEntry droppedEntry
            || sender is not ListBox listBox
            || listBox.DataContext is not MergeInstallViewModel viewModel)
        {
            return;
        }

        var targetIndex = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is { } targetItem
            ? listBox.ItemContainerGenerator.IndexFromContainer(targetItem)
            : viewModel.Queue.Count - 1;

        viewModel.ReorderQueueEntry(droppedEntry, targetIndex);
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null and not T)
        {
            current = VisualTreeHelper.GetParent(current);
        }

        return current as T;
    }
}
