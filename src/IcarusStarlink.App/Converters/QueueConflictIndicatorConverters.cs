using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace IcarusStarlink.App.Converters;

/// <summary>
/// Both converters here look up the same thing — [0] a queue row's own LibraryEntry.Name, [1] the
/// page's MergeInstallViewModel.ConflictingModNamesByMod — from a queue row's DataTemplate, which
/// is why they're kept in one file rather than split: WPF has no way to reuse one MultiBinding's
/// already-computed value for a second target property (Visibility vs ToolTip), so both need the
/// identical two-binding lookup, just returning a different shape.
/// </summary>
internal static class QueueConflictLookup
{
    public static IReadOnlyList<string>? Resolve(object[] values) =>
        values is [string modName, IReadOnlyDictionary<string, IReadOnlyList<string>> byMod, ..]
            && byMod.TryGetValue(modName, out var others) && others.Count > 0
                ? others
                : null;
}

/// <summary>Shows a small conflict-warning icon on a queue row only when this mod shares a conflicting field with at least one other queued mod.</summary>
public sealed class QueueConflictVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        QueueConflictLookup.Resolve(values) is null ? Visibility.Collapsed : Visibility.Visible;

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Names which other queued mods this row conflicts with, for the same icon's own tooltip.</summary>
public sealed class QueueConflictTooltipConverter : IMultiValueConverter
{
    public object? Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var others = QueueConflictLookup.Resolve(values);
        return others is null ? null : $"Changes the same field differently as: {string.Join(", ", others)}";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
