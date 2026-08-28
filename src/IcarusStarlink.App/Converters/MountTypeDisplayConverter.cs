using System.Globalization;
using System.Windows.Data;
using IcarusStarlink.App.Services;

namespace IcarusStarlink.App.Converters;

/// <summary>A raw D_Mounts row name (e.g. "WoollyMammoth") -> its humanized display text ("Woolly Mammoth").</summary>
public sealed class MountTypeDisplayConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string rowName ? SaveGameNames.HumanizeMountType(rowName) : value ?? "";

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
