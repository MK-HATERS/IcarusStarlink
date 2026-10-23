using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace IcarusStarlink.App.Converters;

/// <summary>True -> Visible, false -> Hidden (keeps its space). For a row's buttons, so the ones every row has stay lined up whether or not a row has the optional ones.</summary>
public sealed class BoolToHiddenConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Hidden;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
