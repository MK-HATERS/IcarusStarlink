using System.Globalization;
using System.Windows.Data;
using IcarusStarlink.Core.Server;

namespace IcarusStarlink.App.Converters;

/// <summary>A server log line -> its ServerLogLevel, so the Log panel can colour errors and warnings without a row object per line.</summary>
public sealed class LogLineLevelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        ServerLogFilter.LevelOf(value as string);

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
