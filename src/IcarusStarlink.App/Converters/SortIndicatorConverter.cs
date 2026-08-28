using System.Globalization;
using System.Windows.Data;

namespace IcarusStarlink.App.Converters;

/// <summary>Library's own sortable column headers: values[0] is the ViewModel's current SortColumn, values[1] is SortDescending, parameter is this header's own column key. Returns an up/down arrow only for whichever column is actually active, empty otherwise.</summary>
public sealed class SortIndicatorConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is not [string sortColumn, bool descending] || !string.Equals(sortColumn, parameter as string, StringComparison.Ordinal))
        {
            return "";
        }

        return descending ? "▼" : "▲";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
