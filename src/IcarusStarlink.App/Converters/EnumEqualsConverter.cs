using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace IcarusStarlink.App.Converters;

/// <summary>value.ToString() == ConverterParameter -> Visible, else Collapsed. For swapping panes on an enum-valued ViewModel property (e.g. ExmodEditorViewMode) without a MultiBinding per pane.</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter as string, StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
