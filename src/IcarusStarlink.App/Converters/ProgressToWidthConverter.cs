using System.Globalization;
using System.Windows.Data;

namespace IcarusStarlink.App.Converters;

/// <summary>Drives the themed ProgressBar template's indicator width — (Value, Maximum, the track's own ActualWidth) -> a pixel width. WPF's stock ProgressBar template does this internally via a private adapter this app can't reuse once the template's replaced for theming, so it needs its own small equivalent.</summary>
public sealed class ProgressToWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is [double value, double maximum, double trackWidth] && maximum > 0 && trackWidth > 0)
        {
            return trackWidth * Math.Clamp(value / maximum, 0, 1);
        }

        return 0.0;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
