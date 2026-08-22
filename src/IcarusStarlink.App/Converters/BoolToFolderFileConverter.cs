using System.Globalization;
using System.Windows.Data;

namespace IcarusStarlink.App.Converters;

/// <summary>true (IsDirectory) → "Folder", false → "File" — for Server's remote file listing, where FtpEntry itself is a plain Core-level record with no UI-display text of its own.</summary>
public sealed class BoolToFolderFileConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "Folder" : "File";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
