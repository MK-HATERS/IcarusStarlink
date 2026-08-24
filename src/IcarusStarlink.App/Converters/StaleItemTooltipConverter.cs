using System.Globalization;
using System.Windows.Data;

namespace IcarusStarlink.App.Converters;

/// <summary>
/// Builds the "possibly stale items" row badge's tooltip from [0] StaleItemCount and
/// [1] StaleItemSuggestionHint — a plain string-format binding can't conditionally include the
/// hint only when one exists, so this composes the two into one message instead.
/// </summary>
public sealed class StaleItemTooltipConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var count = values is [int c, ..] ? c : 0;
        var hint = values is [_, string h, ..] ? h : null;

        var basis = $"{count} item(s) may be editing a row this game version no longer has";
        return string.IsNullOrEmpty(hint)
            ? $"{basis} — click to review in the editor."
            : $"{basis} — possible match: '{hint}'. Click to review in the editor.";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
