using System.Globalization;
using System.Windows.Data;

namespace IcarusStarlink.App.Converters;

/// <summary>Formats a nullable variant label as " — Label", or "" when null/empty — StringFormat alone can't do this: WPF's Binding still runs String.Format (producing a bare " — ") even when the source value is null, since TargetNullValue is substituted before StringFormat runs, not instead of it.</summary>
public sealed class VariantSuffixConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string { Length: > 0 } label ? $" — {label}" : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
