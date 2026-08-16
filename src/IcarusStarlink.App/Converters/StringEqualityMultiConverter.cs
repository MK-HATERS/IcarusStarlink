using System.Globalization;
using System.Windows.Data;

namespace IcarusStarlink.App.Converters;

public sealed class StringEqualityMultiConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        return values.Length == 2 && Equals(values[0], values[1]);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
