using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace IcarusStarlink.App.Converters;

/// <summary>Null/empty-string -> Collapsed, anything else -> Visible. ConverterParameter="Invert" flips that.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hasValue = value is not null && value is not "";
        var invert = string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase);
        return (hasValue != invert) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
