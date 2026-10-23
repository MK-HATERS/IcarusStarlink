using System.Globalization;
using System.Windows.Data;
using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Converters;

/// <summary>
/// A grouped list's heading count ("9 of 14 complete", SaveGroupCounts). The first value is the
/// CollectionViewGroup; the rest are only there so the binding runs again when the tab's own
/// summary changes (a row switched on or off doesn't change the group itself).
/// </summary>
public sealed class SaveGroupCountConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.FirstOrDefault() is CollectionViewGroup group ? SaveGroupCounts.Describe(group.Items) : "";

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
