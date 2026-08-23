using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using IcarusStarlink.App.Services;

namespace IcarusStarlink.App.Converters;

/// <summary>Hex string ("#RRGGBB"/"#AARRGGBB") → Brush for the skin editor's live swatches; anything unparseable renders Transparent rather than throwing mid-binding.</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hex && ThemeService.TryParseColor(hex.Trim(), out var color))
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        return Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
