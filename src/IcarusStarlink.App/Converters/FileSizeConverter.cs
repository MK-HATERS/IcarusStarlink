using System.Globalization;
using System.Windows.Data;
using IcarusStarlink.PakIO.Install;

namespace IcarusStarlink.App.Converters;

/// <summary>A byte count as "572 MB" / "1.6 GB" (the same wording as Settings' Backups line). Null stays empty.</summary>
public sealed class FileSizeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        long bytes => BackupCleanup.FormatSize(bytes),
        int bytes => BackupCleanup.FormatSize(bytes),
        _ => "",
    };

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
