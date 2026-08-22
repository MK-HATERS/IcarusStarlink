using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using IcarusStarlink.Core.Library;

namespace IcarusStarlink.App.Converters;

/// <summary>
/// Drives the "already queued" indicator on Merge &amp; Install's own Library tree — values are
/// [0] the row's own LibraryEntry, [1] the Queue collection, [2] Queue.Count. [1] alone wouldn't
/// re-trigger this MultiBinding on Add/Remove (the ObservableCollection reference itself never
/// changes), so [2] is bound purely to force a re-evaluation whenever the queue's size changes —
/// ObservableCollection raises PropertyChanged for "Count" on every Add/Remove/Move, which a plain
/// binding to the collection reference does not.
/// </summary>
public sealed class LibraryEntryQueuedConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is [LibraryEntry entry, IEnumerable queue, ..])
        {
            foreach (var queued in queue)
            {
                if (queued is LibraryEntry queuedEntry && string.Equals(queuedEntry.FolderName, entry.FolderName, StringComparison.OrdinalIgnoreCase))
                {
                    return Visibility.Visible;
                }
            }
        }

        return Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
